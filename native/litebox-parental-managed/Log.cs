// Tiny file logger next to the plugin dll (litebox-parentalcontrol.log). The plugin runs
// inside LaunchBox/BigBox where there is no console; when a guard silently refuses or the
// ASI bridge no-ops, the log is the only way to see why. Never throws.

using System;
using System.IO;
using System.Reflection;

namespace LiteBoxParental
{
    internal static class Log
    {
        private static readonly object _lock = new object();
        private static readonly string _path = Resolve();

        private static string Resolve()
        {
            try
            {
                var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                return string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, "litebox-parentalcontrol.log");
            }
            catch { return null; }
        }

        public static void Line(string msg)
        {
            if (_path == null) return;
            // DEV test ini can silence all parental logging (DebugLog=0). EnsureLoaded is re-entrant-safe (it sets
            // its loaded flag before its own summary Log.Line), so this never recurses.
            try { TestConfig.EnsureLoaded(); if (!TestConfig.DebugLog) return; } catch { }
            try { lock (_lock) File.AppendAllText(_path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {msg}{Environment.NewLine}"); }
            catch { }
        }
    }
}
