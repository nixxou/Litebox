// The notification MODEL — the vocabulary shared by the center, the popups, the bell list and the
// LaunchBox-compatibility shim.
//
// These types are PUBLIC and live in the plugin-facing "LiteBox.Notifications" namespace on purpose: a
// plugin reaches them by reflection off the loaded LiteBox assembly (see Host\Notifications\README-API.md),
// so their names and signatures are a contract — rename with the same care as an SDK.

#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;

namespace LiteBox.Notifications;

/// <summary>What a notification IS, which decides its accent colour and its default lifetime.</summary>
public enum NotificationKind
{
    /// <summary>Plain information. Auto-closes after the default lifespan.</summary>
    Info,
    /// <summary>Something failed. Red accent, longer default lifespan.</summary>
    Error,
    /// <summary>Work in progress. Never auto-closes — the sender updates or removes it.</summary>
    Progress,
}

/// <summary>One clickable button on a notification (LaunchBox's "input notification").</summary>
public sealed class NotificationAction
{
    public NotificationAction(string label, Action run, bool dismissOnClick = true)
    {
        Label = label ?? "";
        Run = run ?? (() => { });
        DismissOnClick = dismissOnClick;
    }

    public string Label { get; }
    public Action Run { get; }
    /// <summary>Close the popup once the button has run (the default — a button that starts a long job
    /// can pass false and update the notification instead).</summary>
    public bool DismissOnClick { get; }
}

/// <summary>A single notification: what the popup shows, what the bell lists, what an event carries.
/// Mutable only through the center (or the convenience methods here, which delegate to it) so every
/// change reaches the UI and the plugin mirrors.</summary>
public sealed class LiteBoxNotification
{
    internal LiteBoxNotification(string message, NotificationKind kind, int lifeSpanSeconds,
                                 IReadOnlyList<NotificationAction> actions, Image? icon)
    {
        Message = message ?? "";
        Kind = kind;
        LifeSpanSeconds = lifeSpanSeconds;
        Actions = actions ?? Array.Empty<NotificationAction>();
        Icon = icon;
        DateRaised = DateTime.Now;
    }

    /// <summary>Stable id — handy for a plugin that wants to track one notification across events.</summary>
    public string Id { get; } = Guid.NewGuid().ToString("N");

    public string Message { get; internal set; }
    public NotificationKind Kind { get; internal set; }
    public DateTime DateRaised { get; }
    /// <summary>When the user (or a plugin) marked it read; null while unread.</summary>
    public DateTime? DateRead { get; internal set; }
    public bool IsRead => DateRead.HasValue;
    public bool IsError => Kind == NotificationKind.Error;
    public bool IsInProgress => Kind == NotificationKind.Progress;

    /// <summary>Seconds the popup stays up. 0 = the center's default for this kind; negative = sticky
    /// (only a click closes it). Notifications carrying actions are always sticky.</summary>
    public int LifeSpanSeconds { get; internal set; }

    /// <summary>Optional per-notification icon; null = the kind's default glyph.</summary>
    public Image? Icon { get; internal set; }

    public IReadOnlyList<NotificationAction> Actions { get; }

    /// <summary>True while the popup is on screen.</summary>
    public bool IsShowing { get; internal set; }

    /// <summary>Free slot for whoever raised it (the LaunchBox shim stores its mirror object here).
    /// LiteBox itself never reads it.</summary>
    public object? Tag { get; set; }

    public void MarkRead() => NotificationCenter.MarkRead(this);
    public void MarkUnread() => NotificationCenter.MarkUnread(this);
    /// <summary>Closes the popup; the notification stays in the bell list.</summary>
    public void Dismiss() => NotificationCenter.Dismiss(this);
    /// <summary>Closes the popup AND drops it from the list.</summary>
    public void Remove() => NotificationCenter.Remove(this);
    /// <summary>Rewrites the text in place (progress notifications).</summary>
    public void Update(string message) => NotificationCenter.Update(this, message);

    public override string ToString() => $"[{Kind}] {Message}";
}
