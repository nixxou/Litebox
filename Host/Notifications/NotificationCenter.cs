// LiteBox's notification center: the ONE place a notification is born, remembered, read and forgotten.
//
// It is deliberately UI-free — it owns the list, the unread state and the events, and hands the display
// job to whatever sink is attached (the WinForms popups + bell, see NotificationUi). With no sink (a
// headless / CLI run) everything still works: the notification is recorded, the events fire, and the line
// goes to the console. Nothing here touches a Control, so any thread may call any method.
//
// Two audiences reach this class:
//   * LiteBox itself      — internal callers use it directly (NotificationCenter.Info("…")).
//   * Plugins             — by reflection off the loaded "LiteBox" assembly, or transparently through the
//                           LaunchBox-compatibility shim (Unbroken.LaunchBox…NotificationCenter). Both are
//                           documented in Host\Notifications\README-API.md; the public signatures here are
//                           a contract.

#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace LiteBox.Notifications;

/// <summary>Where the display work goes. Implemented by the GUI (Host\Notifications\NotificationUi).</summary>
internal interface INotificationSink
{
    /// <summary>Put the notification on screen (popup / Windows balloon / message box — the sink decides
    /// from the user's setting).</summary>
    void Show(LiteBoxNotification n);
    /// <summary>Take it off screen. The notification itself stays in the list.</summary>
    void Close(LiteBoxNotification n);
    /// <summary>The list or the unread count changed — repaint the bell and any open list.</summary>
    void Refresh();
}

public static class NotificationCenter
{
    // Newest LAST (append order); All exposes newest FIRST, which is how every UI wants it.
    private static readonly List<LiteBoxNotification> _all = new();
    private static readonly object _lock = new();

    /// <summary>How many notifications are kept before the oldest READ ones are dropped. The bell list is
    /// a log, not an archive — LaunchBox keeps far fewer.</summary>
    private const int MaxKept = 200;

    internal static INotificationSink? Sink;

    // ── Events. All fire on the thread that caused the change; a UI handler must marshal itself. ─────
    public static event Action<LiteBoxNotification>? Raised;
    public static event Action<LiteBoxNotification>? Read;
    public static event Action<LiteBoxNotification>? Unread;
    /// <summary>The popup closed (the notification is still in the list).</summary>
    public static event Action<LiteBoxNotification>? Dismissed;
    public static event Action<LiteBoxNotification>? Removed;
    /// <summary>Text changed in place (progress).</summary>
    public static event Action<LiteBoxNotification>? Updated;
    /// <summary>Anything at all changed — the cheap hook for "repaint my badge".</summary>
    public static event Action? Changed;

    /// <summary>Every notification still remembered, newest first.</summary>
    public static IReadOnlyList<LiteBoxNotification> All
    {
        get { lock (_lock) { var a = _all.ToArray(); Array.Reverse(a); return a; } }
    }

    public static int Count { get { lock (_lock) return _all.Count; } }

    public static int UnreadCount { get { lock (_lock) return _all.Count(n => !n.IsRead); } }

    // ── Raising ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Plain information popup.</summary>
    public static LiteBoxNotification Info(string message, int lifeSpanSeconds = 0)
        => Send(message, NotificationKind.Info, lifeSpanSeconds, null, null);

    /// <summary>Error popup (red accent, stays longer).</summary>
    public static LiteBoxNotification Error(string message, int lifeSpanSeconds = 0)
        => Send(message, NotificationKind.Error, lifeSpanSeconds, null, null);

    /// <summary>A popup that stays up until you <see cref="Update"/> or <see cref="Remove"/> it.</summary>
    public static LiteBoxNotification Progress(string message)
        => Send(message, NotificationKind.Progress, -1, null, null);

    /// <summary>A popup carrying clickable actions (LaunchBox's "input notification"). Always sticky:
    /// it waits for a choice.</summary>
    public static LiteBoxNotification Input(string message, IEnumerable<KeyValuePair<string, Action>> actions,
                                            int lifeSpanSeconds = -1)
        => Send(message, NotificationKind.Info, lifeSpanSeconds,
                actions?.Select(a => new NotificationAction(a.Key, a.Value)), null);

    /// <summary>The full form. <paramref name="popup"/> false records the notification in the bell list
    /// without ever putting it on screen.</summary>
    public static LiteBoxNotification Send(string message, NotificationKind kind, int lifeSpanSeconds = 0,
                                           IEnumerable<NotificationAction>? actions = null, Image? icon = null,
                                           bool popup = true, bool markUnread = true)
    {
        var list = actions?.Where(a => a != null).ToArray() ?? Array.Empty<NotificationAction>();
        // A notification asking a question must not disappear on a timer, whatever the caller asked for.
        if (list.Length > 0 && lifeSpanSeconds >= 0) lifeSpanSeconds = -1;

        var n = new LiteBoxNotification(message, kind, lifeSpanSeconds, list, icon);
        if (!markUnread) n.DateRead = n.DateRaised;

        lock (_lock)
        {
            _all.Add(n);
            Trim();
        }

        Console.WriteLine($"[notify] {kind}: {n.Message}");
        Fire(Raised, n);

        // A Raised subscriber runs BEFORE the popup goes up and may have removed the notification
        // synchronously (a plugin that routes notifications somewhere else, say). Re-check membership
        // rather than showing regardless: a popup for an entry no longer in the list is an orphan —
        // absent from the bell, yet still carrying clickable actions.
        bool stillListed;
        lock (_lock) stillListed = _all.Contains(n);
        if (popup && stillListed)
        {
            try { Sink?.Show(n); } catch (Exception ex) { Console.WriteLine("[notify] show failed: " + ex.Message); }
        }
        Touch();
        return n;
    }

    /// <summary>Drops the oldest READ notifications once the log is over budget (an unread one is never
    /// silently lost; if they are ALL unread the oldest goes anyway).</summary>
    private static void Trim()
    {
        while (_all.Count > MaxKept)
        {
            int i = _all.FindIndex(n => n.IsRead && !n.IsShowing);
            _all.RemoveAt(i >= 0 ? i : 0);
        }
    }

    // ── State changes ────────────────────────────────────────────────────────────────────────────────

    public static void MarkRead(LiteBoxNotification? n)
    {
        if (n == null || n.IsRead) return;
        n.DateRead = DateTime.Now;
        Fire(Read, n);
        Touch();
    }

    public static void MarkUnread(LiteBoxNotification? n)
    {
        if (n == null || !n.IsRead) return;
        n.DateRead = null;
        Fire(Unread, n);
        Touch();
    }

    public static void MarkAllRead()
    {
        LiteBoxNotification[] fresh;
        lock (_lock) fresh = _all.Where(n => !n.IsRead).ToArray();
        if (fresh.Length == 0) return;
        foreach (var n in fresh) { n.DateRead = DateTime.Now; Fire(Read, n); }
        Touch();
    }

    /// <summary>Closes the popup; the notification stays in the list.</summary>
    public static void Dismiss(LiteBoxNotification? n)
    {
        if (n == null) return;
        try { Sink?.Close(n); } catch { }
        Fire(Dismissed, n);
        Touch();
    }

    /// <summary>Closes the popup AND forgets the notification.</summary>
    public static void Remove(LiteBoxNotification? n)
    {
        if (n == null) return;
        bool had;
        lock (_lock) had = _all.Remove(n);
        try { Sink?.Close(n); } catch { }
        if (had) Fire(Removed, n);
        Touch();
    }

    /// <summary>Empties the list (the bell's "Clear All") and closes every popup.</summary>
    public static void Clear()
    {
        LiteBoxNotification[] gone;
        lock (_lock) { gone = _all.ToArray(); _all.Clear(); }
        foreach (var n in gone) { try { Sink?.Close(n); } catch { } Fire(Removed, n); }
        Touch();
    }

    /// <summary>Rewrites the message in place — the popup and the list follow. Use for progress.</summary>
    public static void Update(LiteBoxNotification? n, string message)
    {
        if (n == null) return;
        n.Message = message ?? "";
        Fire(Updated, n);
        Touch();
    }

    /// <summary>Turns a progress notification into its final state: new text, a normal lifespan, and the
    /// popup restarts its countdown.</summary>
    public static void Complete(LiteBoxNotification? n, string message, bool error = false, int lifeSpanSeconds = 0)
    {
        if (n == null) return;
        n.Message = message ?? "";
        n.Kind = error ? NotificationKind.Error : NotificationKind.Info;
        n.LifeSpanSeconds = lifeSpanSeconds;
        Fire(Updated, n);
        // Re-show: a completed job should be visible even if its progress popup was dismissed meanwhile.
        try { Sink?.Show(n); } catch { }
        Touch();
    }

    // ── Plumbing ─────────────────────────────────────────────────────────────────────────────────────

    private static void Fire(Action<LiteBoxNotification>? ev, LiteBoxNotification n)
    {
        if (ev == null) return;
        foreach (var d in ev.GetInvocationList().Cast<Action<LiteBoxNotification>>())
            try { d(n); } catch (Exception ex) { Console.WriteLine("[notify] handler threw: " + ex.Message); }
    }

    private static void Touch()
    {
        try { Sink?.Refresh(); } catch { }
        var ev = Changed;
        if (ev == null) return;
        foreach (var d in ev.GetInvocationList().Cast<Action>())
            try { d(); } catch { }
    }
}
