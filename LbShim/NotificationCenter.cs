// LaunchBox's Unbroken.LaunchBox.Windows.Desktop.Notifications, re-implemented on top of LiteBox.
//
// The shapes here mirror the real ones member for member (verified against LaunchBox.dll 13.26/13.28), so
// a plugin's reflection — GetType(...), GetMethod("SendInfoNotification"), Invoke(null, new object[]{msg, 0})
// — resolves and runs exactly as it does under LaunchBox. Two deliberate deviations, both invisible to a
// reflecting plugin:
//
//   * Notification.Buttons is an ObservableCollection<NotificationButton>, not Caliburn.Micro's
//     BindableCollection<T> (which derives from it). Shipping Caliburn here just to name a base class
//     would drag a UI framework into a compatibility shim.
//   * Notification.Icon is whatever ImageSource the caller passed; LiteBox draws its OWN icon in the
//     popup (its WinForms toast cannot render a WPF ImageSource without a full render pass).
//
// Everything that CHANGES state forwards to LiteBox through ShimBridge and comes back as an event, so the
// plugin's view of the world and the host's list never drift.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Media;
using LiteBox.Shim;

namespace Unbroken.LaunchBox.Windows.Desktop.Notifications
{
    /// <summary>One notification, as a LaunchBox plugin knows it. Instances handed to a plugin always
    /// mirror a live LiteBox notification (see <see cref="HostHandle"/>); an instance a plugin constructs
    /// itself is inert until it is passed to one of the Add* methods, exactly as under LaunchBox.</summary>
    public class Notification
    {
        public Notification(ImageSource icon, string message, int? minLifeSpan, bool isError, bool isInProgress)
            : this(icon, message, null, minLifeSpan, isError, isInProgress) { }

        public Notification(ImageSource icon, string message, IEnumerable<NotificationButton> buttons,
                            int? minLifeSpan, bool isError, bool isInProgress)
        {
            Icon = icon;
            MessageInternal = message ?? "";
            LifeSpan = minLifeSpan;
            IsError = isError;
            IsInProgress = isInProgress;
            DateRaisedInternal = DateTime.Now;
            Buttons = new ObservableCollection<NotificationButton>();
            if (buttons != null) foreach (var b in buttons) Buttons.Add(b);
        }

        /// <summary>The LiteBox notification this mirrors — an opaque object, only ever handed back to
        /// <see cref="ShimBridge"/>. Null on an instance a plugin built itself.</summary>
        internal object HostHandle;

        internal string MessageInternal;
        internal DateTime DateRaisedInternal;

        public ObservableCollection<NotificationButton> Buttons { get; private set; }
        public DateTime DateRaised { get { return DateRaisedInternal; } }
        public DateTime? DateRead { get; set; }
        public ImageSource Icon { get; private set; }
        public bool IsError { get; private set; }
        public bool IsInProgress { get; private set; }
        public int? LifeSpan { get; private set; }
        public string Message { get { return MessageInternal; } }

        /// <summary>Closes the popup. The notification stays in the list (LiteBox's bell), unread state
        /// untouched — the same thing LaunchBox's × does.</summary>
        public void Dismiss() { ShimBridge.Call("dismiss", HostHandle); }
        public void MarkRead() { ShimBridge.Call("read", HostHandle); }
        public void MarkUnread() { ShimBridge.Call("unread", HostHandle); }

        /// <summary>One clickable action on a notification.</summary>
        public class NotificationButton
        {
            public NotificationButton(Notification parent, string label, Action command)
            {
                Parent = parent;
                Label = label ?? "";
                Action = command;
                Command = new RelayCommand(command);
            }

            internal Notification Parent;
            internal Action Action;
            public ICommand Command { get; private set; }
            public string Label { get; private set; }
        }

        /// <summary>Minimal ICommand over an Action — LaunchBox exposes the button as a bindable command,
        /// so a plugin may call button.Command.Execute(null) instead of holding the Action.</summary>
        private sealed class RelayCommand : ICommand
        {
            private readonly Action _run;
            public RelayCommand(Action run) { _run = run; }
            public event EventHandler CanExecuteChanged { add { } remove { } }
            public bool CanExecute(object parameter) { return true; }
            public void Execute(object parameter) { if (_run != null) _run(); }
        }
    }

    /// <summary>An update notification. LaunchBox subclasses Notification for it; kept so a plugin that
    /// type-tests (`is UpdateFoundNotification`) still compiles and behaves.</summary>
    public class UpdateFoundNotification : Notification
    {
        public UpdateFoundNotification(ImageSource icon, string message, int minLifeSpan, bool isError)
            : base(icon, message, minLifeSpan, isError, false) { }

        public UpdateFoundNotification(ImageSource icon, string message, IEnumerable<NotificationButton> buttons,
                                       int minLifeSpan, bool isError)
            : base(icon, message, buttons, minLifeSpan, isError, false) { }
    }

    public static class NotificationCenter
    {
        public enum NotificationTypes
        {
            LaunchBoxNotifications,
            WindowsNotifications,
            MessageBoxes,
        }

        // ── The list a plugin can read ───────────────────────────────────────────────────────────────
        public static IEnumerable<Notification> Notifications { get { return ShimBridge.Snapshot(); } }

        private static bool _busy;
        public static bool IsBusy { get { return _busy; } }

        // ── Events. LaunchBox raises Raised+Added together; LiteBox has one raise, so both fire. ─────
        public static event Action<Notification> NotificationRaised;
        public static event Action<Notification> NotificationAdded;
        public static event Action<Notification> NotificationRead;
        public static event Action<Notification> NotificationUnread;
        public static event Action<Notification> NotificationDismissed;
        public static event Action<bool> BusyStateChanged;

        internal static void FireRaised(Notification n)
        {
            var a = NotificationRaised; if (a != null) Safe(() => a(n));
            var b = NotificationAdded; if (b != null) Safe(() => b(n));
        }
        internal static void FireRead(Notification n) { var a = NotificationRead; if (a != null) Safe(() => a(n)); }
        internal static void FireUnread(Notification n) { var a = NotificationUnread; if (a != null) Safe(() => a(n)); }
        internal static void FireDismissed(Notification n) { var a = NotificationDismissed; if (a != null) Safe(() => a(n)); }
        internal static void SetBusyInternal(bool busy)
        {
            if (_busy == busy) return;
            _busy = busy;
            var a = BusyStateChanged; if (a != null) Safe(() => a(busy));
        }
        private static void Safe(Action a) { try { a(); } catch { } }

        // ── State changes ────────────────────────────────────────────────────────────────────────────
        public static void MarkNotificationRead(Notification notification)
        { if (notification != null) ShimBridge.Call("read", notification.HostHandle); }

        public static void MarkNotificationUnread(Notification notification)
        { if (notification != null) ShimBridge.Call("unread", notification.HostHandle); }

        public static void RemoveNotification(Notification notification)
        { if (notification != null) ShimBridge.Call("remove", notification.HostHandle); }

        public static void SetBusy(bool busy) { ShimBridge.Call("busy", busy); }

        // ── Sending ──────────────────────────────────────────────────────────────────────────────────
        // Every path funnels here. kind: "info" | "error" | "progress"; popup=false means the
        // notification only lands in the list behind the bell (LaunchBox's "notification center" flavor).
        private static Notification Send(string message, string kind, int? lifeSpan,
                                         IEnumerable<KeyValuePair<string, Action>> actions,
                                         object icon, bool popup, bool markUnread)
        {
            var handle = ShimBridge.Call("send", message ?? "", kind, lifeSpan, actions, icon, popup, markUnread);
            if (handle == null) return null;
            // The host raises synchronously, so the mirror normally exists by now; Mirror() covers the
            // case where a future host defers the event.
            return ShimBridge.Find(handle)
                ?? ShimBridge.Mirror(handle, message ?? "", kind == "error", kind == "progress",
                                     lifeSpan, DateTime.Now, actions);
        }

        public static void SendInfoNotification(string message, int minimumLifeSpan = 0)
        { Send(message, "info", minimumLifeSpan, null, null, true, true); }

        public static void SendErrorNotification(string message, int minimumLifeSpan = 0)
        { Send(message, "error", minimumLifeSpan, null, null, true, true); }

        public static Notification SendInputNotification(string message,
            IEnumerable<KeyValuePair<string, Action>> actions, int? minimumLifeSpan = 0)
        { return Send(message, "info", minimumLifeSpan, actions, null, true, true); }

        public static Notification AddPassiveNotification(string message, bool markUnread)
        { return Send(message, "info", null, null, null, true, markUnread); }

        public static Notification AddPassiveNotification(string message, bool markUnread, bool isInProgress)
        { return Send(message, isInProgress ? "progress" : "info", null, null, null, true, markUnread); }

        public static Notification AddPassiveNotification(string message, bool markUnread, bool isInProgress, int? minimumLifeSpan)
        { return Send(message, isInProgress ? "progress" : "info", minimumLifeSpan, null, null, true, markUnread); }

        public static Notification AddPassiveNotification(ImageSource icon, string message, bool markUnread)
        { return Send(message, "info", null, null, icon, true, markUnread); }

        public static Notification AddPassiveNotification(ImageSource icon, string message, bool markUnread, bool isInProgress)
        { return Send(message, isInProgress ? "progress" : "info", null, null, icon, true, markUnread); }

        public static Notification AddPassiveNotification(ImageSource icon, string message, bool markUnread, bool isInProgress, int? minimumLifeSpan)
        { return Send(message, isInProgress ? "progress" : "info", minimumLifeSpan, null, icon, true, markUnread); }

        public static Notification AddNotificationCenterNotification(string message, bool markUnread, bool isInProgress)
        { return Send(message, isInProgress ? "progress" : "info", null, null, null, false, markUnread); }

        public static Notification AddNotificationCenterNotification(ImageSource icon, string message, bool markUnread, bool isInProgress)
        { return Send(message, isInProgress ? "progress" : "info", null, null, icon, false, markUnread); }

        // ── LaunchBox's canned notifications. Their wording is LaunchBox's own; a plugin that triggers
        //    one (rare, but SendPluginUpdateFoundNotification exists for exactly that) gets the same text.
        public static void SendAutomaticImportNotification()
        { SendInfoNotification("Automatic import completed."); }

        public static void SendAutomaticRomImportNotification(int platforms, int games, IEnumerable<string> results)
        {
            string detail = "";
            if (results != null) { var j = string.Join(", ", new List<string>(results).ToArray()); if (j.Length > 0) detail = " (" + j + ")"; }
            SendInfoNotification(string.Format("Automatic ROM import: {0} game(s) across {1} platform(s).{2}", games, platforms, detail));
        }

        public static void SendAutomaticRomImportFeatureNotification()
        { SendInfoNotification("Automatic ROM import is available — point it at your ROM folders to keep the library in sync."); }

        public static void SendCheevoBadgeDisabledNotification()
        { SendInfoNotification("RetroAchievements badges have been disabled."); }

        public static void SendExpiredCredentialsNotification()
        { SendErrorNotification("Your credentials have expired — sign in again."); }

        public static void SendSentToTrayNotification()
        { SendInfoNotification("Still running in the system tray."); }

        public static void SendPluginUpdateFoundNotification()
        { SendInfoNotification("One or more of your installed plugins have updates pending!"); }

        public static void SendUpdateFoundNotification(string fileUrl, string fileName)
        {
            var url = fileUrl;
            var actions = new List<KeyValuePair<string, Action>>
            {
                new KeyValuePair<string, Action>("Open download page", () =>
                {
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
                    catch { }
                }),
            };
            SendInputNotification("An update is available: " + (fileName ?? ""), actions);
        }
    }
}
