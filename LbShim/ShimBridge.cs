// The ONE seam between this compatibility assembly and LiteBox.
//
// LiteBox cannot reference this project at compile time (that would make the host itself depend on an
// assembly called "LaunchBox", which the CLR would happily satisfy from the REAL LaunchBox.dll sitting
// next to LiteBox.exe in <LB>\Core). So the host wires itself in by reflection at boot, and everything
// crossing the boundary is a BCL type only:
//
//     Handler   : set by the host — Func<string verb, object[] args, object result>
//     HostEvent : called by the host — the center telling the shim a notification changed
//
// A verb the host doesn't know returns null; a verb the shim doesn't know is ignored. That way an older
// LiteBox and a newer shim (or the reverse) degrade to "the call does nothing" instead of throwing into
// a plugin's UI thread.

using System;
using System.Collections.Generic;

namespace LiteBox.Shim
{
    public static class ShimBridge
    {
        /// <summary>Set by LiteBox at boot. Null until then — every shim call is a no-op meanwhile.</summary>
        public static Func<string, object[], object> Handler;

        /// <summary>True once LiteBox has wired itself in (a plugin can use it to feature-detect).</summary>
        public static bool IsConnected => Handler != null;

        /// <summary>Fire-and-forget call into the host. Never throws: a plugin raising a notification must
        /// not be able to crash on a host that changed shape.</summary>
        public static object Call(string verb, params object[] args)
        {
            var h = Handler;
            if (h == null) return null;
            try { return h(verb, args ?? new object[0]); }
            catch { return null; }
        }

        // ── Host → shim ──────────────────────────────────────────────────────────────────────────────
        // The shim keeps a MIRROR of the host's notification list so that NotificationCenter.Notifications
        // and its events behave like LaunchBox's for a plugin that listens rather than sends. Each mirror
        // Notification holds the host object it stands for (its HostHandle) so calls made ON it (Dismiss,
        // MarkRead) can be routed back.

        private static readonly Dictionary<object, Unbroken.LaunchBox.Windows.Desktop.Notifications.Notification> _mirror
            = new Dictionary<object, Unbroken.LaunchBox.Windows.Desktop.Notifications.Notification>();
        private static readonly List<Unbroken.LaunchBox.Windows.Desktop.Notifications.Notification> _order
            = new List<Unbroken.LaunchBox.Windows.Desktop.Notifications.Notification>();
        private static readonly object _lock = new object();

        /// <summary>The mirror for a host notification, created on demand (used by the send path, which
        /// gets the handle back synchronously, and by the "raised" event).</summary>
        internal static Unbroken.LaunchBox.Windows.Desktop.Notifications.Notification Mirror(
            object handle, string message, bool isError, bool isInProgress, int? lifeSpan, DateTime raised,
            IEnumerable<KeyValuePair<string, Action>> buttons)
        {
            if (handle == null) return null;
            lock (_lock)
            {
                Unbroken.LaunchBox.Windows.Desktop.Notifications.Notification n;
                if (_mirror.TryGetValue(handle, out n)) return n;
                n = new Unbroken.LaunchBox.Windows.Desktop.Notifications.Notification(
                        null, message, lifeSpan, isError, isInProgress);
                n.HostHandle = handle;
                n.DateRaisedInternal = raised;
                if (buttons != null)
                    foreach (var b in buttons)
                        n.Buttons.Add(new Unbroken.LaunchBox.Windows.Desktop.Notifications.Notification.NotificationButton(n, b.Key, b.Value));
                _mirror[handle] = n;
                _order.Add(n);
                return n;
            }
        }

        internal static Unbroken.LaunchBox.Windows.Desktop.Notifications.Notification Find(object handle)
        {
            if (handle == null) return null;
            lock (_lock)
            {
                Unbroken.LaunchBox.Windows.Desktop.Notifications.Notification n;
                return _mirror.TryGetValue(handle, out n) ? n : null;
            }
        }

        internal static Unbroken.LaunchBox.Windows.Desktop.Notifications.Notification[] Snapshot()
        {
            lock (_lock) return _order.ToArray();
        }

        /// <summary>Called by LiteBox's notification center on every state change. args[0] is always the
        /// host notification object.</summary>
        public static void HostEvent(string verb, object[] args)
        {
            try
            {
                if (verb == null || args == null || args.Length == 0) return;
                object handle = args[0];

                if (verb == "raised")
                {
                    var n = Mirror(handle,
                        Arg<string>(args, 1), Arg<bool>(args, 2), Arg<bool>(args, 3),
                        args.Length > 4 ? args[4] as int? : null,
                        args.Length > 5 && args[5] is DateTime ? (DateTime)args[5] : DateTime.Now,
                        args.Length > 6 ? args[6] as IEnumerable<KeyValuePair<string, Action>> : null);
                    Unbroken.LaunchBox.Windows.Desktop.Notifications.NotificationCenter.FireRaised(n);
                    return;
                }

                var m = Find(handle);
                if (m == null) return;
                switch (verb)
                {
                    case "read":
                        m.DateRead = DateTime.Now;
                        Unbroken.LaunchBox.Windows.Desktop.Notifications.NotificationCenter.FireRead(m);
                        break;
                    case "unread":
                        m.DateRead = null;
                        Unbroken.LaunchBox.Windows.Desktop.Notifications.NotificationCenter.FireUnread(m);
                        break;
                    case "dismissed":
                        Unbroken.LaunchBox.Windows.Desktop.Notifications.NotificationCenter.FireDismissed(m);
                        break;
                    case "removed":
                        lock (_lock) { _mirror.Remove(handle); _order.Remove(m); }
                        Unbroken.LaunchBox.Windows.Desktop.Notifications.NotificationCenter.FireDismissed(m);
                        break;
                    case "updated":
                        m.MessageInternal = Arg<string>(args, 1);
                        break;
                    case "busy":
                        Unbroken.LaunchBox.Windows.Desktop.Notifications.NotificationCenter.SetBusyInternal(Arg<bool>(args, 1));
                        break;
                }
            }
            catch { /* a shim mirror must never break the host's notification path */ }
        }

        private static T Arg<T>(object[] args, int i)
            => args != null && i < args.Length && args[i] is T ? (T)args[i] : default(T);
    }
}
