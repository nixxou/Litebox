// How notifications behave: which system shows them, how long they stay, how many stack.
//
// The SYSTEM choice mirrors LaunchBox's own Options ▸ General ▸ Notifications ("LaunchBox Notifications /
// Windows Notifications / Message Boxes", stored in Settings.xml as NotificationType 0/1/2). LiteBox
// follows that setting by default — one place to configure both frontends — and a LiteBox.ini override
// exists for the case where you want LiteBox popups while LaunchBox uses message boxes.
//
// Values are cached: a notification must not cost an INI parse. Refresh() re-reads them (called at boot
// and after the Options window applies).

#nullable enable

using System;
using LbApiHost.Host.Data;
using Unbroken.LaunchBox.Plugins;

namespace LbApiHost.Host.Notifications;

/// <summary>Which mechanism puts a notification in front of the user.</summary>
internal enum NotificationSystem
{
    /// <summary>LiteBox's own popups, bottom-right of the monitor the window is on.</summary>
    LiteBox,
    /// <summary>The Windows notification area (a tray balloon → Windows' own toast + Action Center).</summary>
    Windows,
    /// <summary>A modal message box. Deliberately blunt; LaunchBox offers it, so LiteBox does too.</summary>
    MessageBox,
}

internal static class NotificationSettings
{
    private static bool _loaded;
    private static NotificationSystem _system = NotificationSystem.LiteBox;
    private static int _seconds = 8, _errorSeconds = 15, _maxPopups = 4;
    private static bool _duringGame;

    public static NotificationSystem System { get { Ensure(); return _system; } }
    /// <summary>Default popup lifetime for Info.</summary>
    public static int Seconds { get { Ensure(); return _seconds; } }
    /// <summary>Default popup lifetime for Error (an error you missed is worse than one you saw twice).</summary>
    public static int ErrorSeconds { get { Ensure(); return _errorSeconds; } }
    /// <summary>How many popups may share the corner before the rest queue.</summary>
    public static int MaxPopups { get { Ensure(); return _maxPopups; } }
    /// <summary>Show popups while a game is running. Off by default: a topmost popup over a fullscreen
    /// game can cost it exclusive mode (the same trap the startup cover works around).</summary>
    public static bool DuringGame { get { Ensure(); return _duringGame; } }

    /// <summary>The INI value for the system choice: "auto" (follow LaunchBox) or an explicit choice.</summary>
    public const string AutoValue = "auto";

    public static void Refresh() { _loaded = false; Ensure(); }

    /// <summary>Drop the cache; the next read re-parses LiteBox.ini. Used by the Options window, whose
    /// ApplyLive hooks run BEFORE the file is written — re-reading eagerly there would cache the OLD
    /// values back.</summary>
    public static void Invalidate() => _loaded = false;

    private static void Ensure()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            var cfg = LiteBoxConfig.LoadForExe();
            _seconds = Math.Clamp(cfg.GetInt("NotificationSeconds", 8), 2, 120);
            _errorSeconds = Math.Clamp(cfg.GetInt("NotificationErrorSeconds", 15), 2, 120);
            _maxPopups = Math.Clamp(cfg.GetInt("NotificationMaxPopups", 4), 1, 10);
            _duringGame = cfg.GetBool("NotificationsDuringGame", false);
            _system = Parse(cfg.Get("NotificationSystem", AutoValue));
        }
        catch (Exception ex) { Console.WriteLine("[notify] settings: " + ex.Message); }
    }

    private static NotificationSystem Parse(string? value)
    {
        switch ((value ?? "").Trim().ToLowerInvariant())
        {
            case "litebox": return NotificationSystem.LiteBox;
            case "windows": return NotificationSystem.Windows;
            case "messagebox": case "messageboxes": return NotificationSystem.MessageBox;
            default: return FromLaunchBox();
        }
    }

    /// <summary>What "Follow LaunchBox" resolves to right now. The Options combo shows it in the label,
    /// so the default choice isn't a mystery ("follow it… to what?").</summary>
    public static NotificationSystem LaunchBoxChoice => FromLaunchBox();

    /// <summary>Human name for a system, for labels and help text.</summary>
    public static string DisplayName(NotificationSystem s) => s switch
    {
        NotificationSystem.Windows => "Windows notifications",
        NotificationSystem.MessageBox => "message boxes",
        _ => "LiteBox popups",
    };

    /// <summary>LaunchBox's Settings.xml ▸ NotificationType (0 = LaunchBox notifications, 1 = Windows,
    /// 2 = message boxes). Absent / unreadable ⇒ LiteBox popups.</summary>
    private static NotificationSystem FromLaunchBox()
    {
        try
        {
            var s = (PluginHelper.DataManager as HostDataManagerXml)?.LbSettings;
            if (s == null || !s.Loaded) return NotificationSystem.LiteBox;
            return s.Get("NotificationType", "0").Trim() switch
            {
                "1" => NotificationSystem.Windows,
                "2" => NotificationSystem.MessageBox,
                _ => NotificationSystem.LiteBox,
            };
        }
        catch { return NotificationSystem.LiteBox; }
    }

    /// <summary>The lifespan a notification actually gets: its own if it set one, else the kind default.
    /// A negative result means "sticky — only a click closes it".</summary>
    public static int EffectiveSeconds(LiteBox.Notifications.LiteBoxNotification n)
    {
        if (n.LifeSpanSeconds < 0) return -1;
        if (n.Kind == LiteBox.Notifications.NotificationKind.Progress) return -1;
        if (n.LifeSpanSeconds > 0) return n.LifeSpanSeconds;
        return n.Kind == LiteBox.Notifications.NotificationKind.Error ? ErrorSeconds : Seconds;
    }
}
