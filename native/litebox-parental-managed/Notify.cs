// Sends a LaunchBox in-app notification through its own NotificationCenter (reflection — the Desktop assembly
// isn't referenced at compile time). Used for the lock/unlock feedback so we don't pop a modal messagebox.
// Fully fail-soft: if the type/method isn't there (other LB build), it's a silent no-op.
//
// Shape (from the host): Unbroken.LaunchBox.Windows.Desktop.Notifications.NotificationCenter
//                        static SendInfoNotification(string message, int /*or enum*/).

using System;
using System.Linq;
using System.Reflection;

namespace LiteBoxParental
{
    internal static class Notify
    {
        private const string TypeName = "Unbroken.LaunchBox.Windows.Desktop.Notifications.NotificationCenter";

        private static bool _tried;
        private static MethodInfo _send;
        private static object[] _tailArgs;   // the args AFTER the message (default-filled for this build's signature)

        /// <summary>Show an info notification in LaunchBox's notification center. Best-effort, never throws.</summary>
        public static void Info(string message)
        {
            try
            {
                Resolve();
                if (_send == null) return;
                var args = new object[1 + (_tailArgs?.Length ?? 0)];
                args[0] = message ?? "";
                if (_tailArgs != null) Array.Copy(_tailArgs, 0, args, 1, _tailArgs.Length);
                _send.Invoke(null, args);
            }
            catch (Exception ex) { Log.Line("[Notify] " + ex.Message); }
        }

        private static void Resolve()
        {
            if (_tried) return;
            _tried = true;
            try
            {
                Type t = null;
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try { t = a.GetType(TypeName); } catch { }
                    if (t != null) break;
                }
                if (t == null) { Log.Line("[Notify] NotificationCenter type not found"); return; }

                // SendInfoNotification(string, …) — take the shortest overload whose first param is the message.
                var chosen = t.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Where(m => m.Name == "SendInfoNotification")
                    .Where(m => { var ps = m.GetParameters(); return ps.Length >= 1 && ps[0].ParameterType == typeof(string); })
                    .OrderBy(m => m.GetParameters().Length)
                    .FirstOrDefault();
                if (chosen == null) { Log.Line("[Notify] SendInfoNotification not found"); return; }

                // Default-fill any trailing params for whatever this build's signature is (int 0, enum 0, etc.).
                var ps = chosen.GetParameters();
                _tailArgs = new object[ps.Length - 1];
                for (int i = 1; i < ps.Length; i++)
                {
                    var pt = ps[i].ParameterType;
                    _tailArgs[i - 1] = pt.IsEnum ? Enum.ToObject(pt, 0)
                                     : pt.IsValueType ? Activator.CreateInstance(pt)
                                     : null;
                }
                _send = chosen;
                Log.Line("[Notify] using SendInfoNotification(" + string.Join(",", ps.Select(p => p.ParameterType.Name)) + ")");
            }
            catch (Exception ex) { Log.Line("[Notify] resolve: " + ex.Message); }
        }
    }
}
