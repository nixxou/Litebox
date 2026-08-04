// Options ▸ Notifications. Stored in LiteBox.ini (the shared instance the Options window saves), so the
// LaunchBox settings file is untouched — except for the "Follow LaunchBox" default, which READS
// LaunchBox's own Notification System choice instead of duplicating it.

#nullable enable

using System;
using LbApiHost.Host.Options;
using LiteBox.Notifications;

namespace LbApiHost.Host.Notifications;

internal static class NotificationOptions
{
    private static readonly string[] SystemValues =
    {
        NotificationSettings.AutoValue, "litebox", "windows", "messagebox",
    };

    /// <summary>The combo entries. Built per-open rather than static: the first one names what "follow
    /// LaunchBox" currently resolves to, which is the whole difficulty with that option.</summary>
    private static string[] SystemLabels() => new[]
    {
        "Follow LaunchBox — currently: " + NotificationSettings.DisplayName(NotificationSettings.LaunchBoxChoice),
        "LiteBox popups — cards in the corner",
        "Windows notifications — the system's own",
        "Message boxes — a modal dialog",
    };

    public static void Add(OptionsWindow w, LiteBoxConfig cfg)
    {
        // Every row invalidates the cache rather than re-reading it: ApplyLive runs BEFORE the window
        // saves LiteBox.ini, so the next notification (after the save) picks the new values up.
        void Invalidate() => NotificationSettings.Invalidate();

        var system = OptionItem.Choice("Notifications", "Notification system", SystemLabels(),
            () => Normalize(cfg.Get("NotificationSystem", NotificationSettings.AutoValue)),
            v => cfg.Set("NotificationSystem", v),
            "Where a notification appears. Whichever you pick, it is ALSO kept behind the bell in the "
            + "menu bar until you read or clear it — this only chooses what pops up at the moment it "
            + "arrives.\n"
            + "\n"
            + "• Follow LaunchBox — reuse LaunchBox's own choice (its Options ▸ General ▸ Notifications), "
            + "so both frontends behave the same. This is the default.\n"
            + "• LiteBox popups — dark cards stacking in the bottom-right corner of the monitor this "
            + "window is on, above the taskbar.\n"
            + "• Windows notifications — handed to Windows: its own toast, and a copy in the Action "
            + "Center.\n"
            + "• Message boxes — a modal dialog you must click away. Blunt on purpose; LaunchBox offers "
            + "it, so LiteBox does too.\n"
            + "\n"
            + "Two things override this: a notification carrying buttons always uses a LiteBox popup (the "
            + "other two have nowhere to put them), and while a web kiosk or a game is running nothing "
            + "pops up at all — it goes straight to the bell.",
            applyLive: Invalidate);
        system.ChoiceValues = SystemValues;

        w.AddSection("Notifications", new[]
        {
            system,
            OptionItem.Number("Notifications", "Popup duration (seconds)",
                () => cfg.GetInt("NotificationSeconds", 8), v => cfg.SetInt("NotificationSeconds", v),
                2, 120, 1,
                "How long a LiteBox popup stays in the corner. The countdown pauses while the pointer is "
                + "over it, and a popup that times out stays UNREAD — the bell keeps its badge.",
                applyLive: Invalidate),
            OptionItem.Number("Notifications", "Error popup duration (seconds)",
                () => cfg.GetInt("NotificationErrorSeconds", 15), v => cfg.SetInt("NotificationErrorSeconds", v),
                2, 120, 1, "Errors get their own, longer, default.", applyLive: Invalidate),
            OptionItem.Number("Notifications", "Maximum popups on screen",
                () => cfg.GetInt("NotificationMaxPopups", 4), v => cfg.SetInt("NotificationMaxPopups", v),
                1, 10, 1,
                "Popups stack upward from the bottom-right corner of the monitor the window is on. Past "
                + "this many, the rest queue and appear as slots free — they are all in the bell list "
                + "either way.", applyLive: Invalidate),
            OptionItem.Toggle("Notifications", "Show popups while a game is running",
                () => cfg.GetBool("NotificationsDuringGame", false),
                v => cfg.SetBool("NotificationsDuringGame", v),
                "Off by default: an always-on-top popup over a fullscreen game can knock it out of "
                + "exclusive mode. The notifications still arrive — they wait behind the bell.",
                applyLive: Invalidate),
            // Three separate buttons rather than one that fires a mixed batch: each answers ONE question
            // ("does a plain one work", "do buttons work", "does it reach the kiosk"), and a test whose
            // output you have to untangle isn't much of a test.
            OptionItem.Action("Notifications", "Send a test notification",
                () => NotificationCenter.Info("This is a LiteBox notification."),
                "One plain notification, right now, through whichever system is selected above."),

            OptionItem.Action("Notifications", "Send a test notification with buttons",
                () => NotificationCenter.Input("Notification with actions.", new[]
                {
                    new System.Collections.Generic.KeyValuePair<string, Action>(
                        "Say hi", () => NotificationCenter.Info("Hi.")),
                    new System.Collections.Generic.KeyValuePair<string, Action>(
                        "Raise an error", () => NotificationCenter.Error("This is an error notification.")),
                }),
                "One notification carrying two clickable actions. Notifications with buttons never "
                + "auto-close — they wait for a choice — and always use a LiteBox popup."),

            OptionItem.Action("Notifications", $"Send a test notification in {DelaySeconds} seconds",
                () => SendLater(DelaySeconds, () => NotificationCenter.Info(
                    $"Delayed test notification — raised {DelaySeconds} seconds after you asked for it.")),
                $"Schedules one for {DelaySeconds} seconds from now and returns immediately, so you can "
                + "close this window and put something else on screen first: open the LB web kiosk (it "
                + "should show the notification itself), the BigBox kiosk (nothing on screen — straight "
                + "to the bell), or launch a game (same). Nothing pops up when you click."),
        });
    }

    /// <summary>Long enough to close the Options window and get a kiosk on screen (WebView2 takes a few
    /// seconds to boot), short enough not to forget you asked.</summary>
    private const int DelaySeconds = 10;

    /// <summary>Fires the action later, off the UI thread — the center marshals its own display work, so
    /// nothing here needs a window. Task.Delay owns the timer's lifetime, so there is nothing to keep
    /// alive by hand (a bare System.Threading.Timer would be collectable before it fires).</summary>
    private static void SendLater(int seconds, Action a)
    {
        Console.WriteLine($"[notify] test notification scheduled in {seconds}s");
        _ = System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(seconds)).ContinueWith(_ =>
        {
            try { a(); } catch (Exception ex) { Console.WriteLine("[notify] delayed test failed: " + ex.Message); }
        });
    }

    /// <summary>Unknown / legacy values fall back to "follow LaunchBox" so the combo always has a match.</summary>
    private static string Normalize(string? stored)
    {
        var v = (stored ?? "").Trim().ToLowerInvariant();
        foreach (var known in SystemValues) if (known == v) return known;
        return NotificationSettings.AutoValue;
    }
}
