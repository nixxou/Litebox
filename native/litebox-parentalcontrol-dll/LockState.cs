// The runtime lock state + the anti-corruption latch that the write-guard reads.
//
// Starts LOCKED so a restart can never leave the library exposed. SetLocked() flips the ASI
// read filter (AsiBridge), reloads the library, and moves the latch. The latch (WritesUnsafe)
// mirrors ExtendDB's BigBoxWritesUnsafe: it stays armed through the whole unlock window until
// an unfiltered ForceReload has settled the in-memory library, so a save in the unlock
// micro-window can never persist the filtered subset (see WS0 / the write-guard).

using System;
using System.Diagnostics;
using System.IO;

namespace LiteBoxParental
{
    internal static class LockState
    {
        private static bool _configLoaded;
        private static bool _launchBoxEnabled, _bigBoxEnabled, _isBigBox, _isHost;

        private static bool _locked = true;            // runtime lock — starts locked
        private static bool _inMemoryFiltered = true;  // latch — starts filtered (boots locked)

        public static bool IsBigBox { get { EnsureConfig(); return _isBigBox; } }

        /// <summary>Parental is configured for THIS process (LaunchBoxEnabled here / BigBoxEnabled in BB)
        /// AND the host is one of the two third-party apps. When false the plugin is inert — no
        /// write-guard. The host check keeps the File.Copy guard off anything but LaunchBox.exe /
        /// BigBox.exe (never LiteBox.exe, which writes Data\ legitimately).</summary>
        public static bool ScopeActive { get { EnsureConfig(); return _isHost && (_isBigBox ? _bigBoxEnabled : _launchBoxEnabled); } }

        public static bool Locked => _locked;

        /// <summary>The write-guard's single observable: block a `Data\` write while the in-memory library
        /// may be the FILTERED subset — locked, or mid-unlock before the real reload cleared the latch.</summary>
        public static bool WritesUnsafe => ScopeActive && _inMemoryFiltered;

        /// <summary>Lock or unlock: (re)arm the ASI read filter, reload the library, move the latch.
        /// On lock the latch arms BEFORE filtering; on unlock it clears AFTER the real reload — the
        /// asymmetry is what closes the unlock micro-window.</summary>
        public static void SetLocked(bool locked)
        {
            EnsureConfig();
            if (!ScopeActive) return;
            _locked = locked;
            if (locked) _inMemoryFiltered = true;     // arm before we (re)filter
            AsiBridge.SetFiltering(locked);
            ForceReload();
            if (!locked) _inMemoryFiltered = false;    // real library restored → writes safe
            Log.Line($"[LockState] {(locked ? "LOCKED" : "unlocked")} (scopeActive={ScopeActive} writesUnsafe={WritesUnsafe})");
        }

        private static void ForceReload()
        {
            try { Unbroken.LaunchBox.Plugins.PluginHelper.DataManager.ForceReload(); Log.Line("[LockState] ForceReload done."); }
            catch (Exception ex) { Log.Line("[LockState] ForceReload error: " + ex.Message); }
        }

        // ── config (LB\Core\litebox-parental.dat) ───────────────────────────────
        private static void EnsureConfig()
        {
            if (_configLoaded) return;
            _configLoaded = true;
            try
            {
                var procName = Process.GetCurrentProcess().ProcessName;
                _isBigBox = string.Equals(procName, "BigBox", StringComparison.OrdinalIgnoreCase);
                _isHost = _isBigBox || string.Equals(procName, "LaunchBox", StringComparison.OrdinalIgnoreCase);
                var core = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule?.FileName ?? "");
                var dat = string.IsNullOrEmpty(core) ? null : Path.Combine(core, "litebox-parental.dat");
                if (dat != null && File.Exists(dat))
                {
                    foreach (var raw in File.ReadAllLines(dat))
                    {
                        var line = raw.Trim();
                        if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        var key = line.Substring(0, eq).Trim().ToLowerInvariant();
                        var val = line.Substring(eq + 1).Trim();
                        bool on = val == "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase);
                        if (key == "launchboxenabled") _launchBoxEnabled = on;
                        else if (key == "bigboxenabled") _bigBoxEnabled = on;
                    }
                }
                Log.Line($"[LockState] config: isBigBox={_isBigBox} launchBoxEnabled={_launchBoxEnabled} bigBoxEnabled={_bigBoxEnabled}");
            }
            catch (Exception ex) { Log.Line("[LockState] config load error: " + ex.Message); }
        }
    }
}
