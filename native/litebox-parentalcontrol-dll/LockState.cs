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
        private static bool _enabled, _isBigBox, _isHost, _configError;

        private static bool _locked = true;            // runtime lock — starts locked
        private static bool _inMemoryFiltered = true;  // latch — starts filtered (boots locked)

        public static bool IsBigBox { get { EnsureConfig(); return _isBigBox; } }

        /// <summary>The host process is one of the two third-party apps (LaunchBox.exe / BigBox.exe).
        /// Everything else — LiteBox.exe (which does its own parental filtering + writes Data\), a helper,
        /// an updater — must see the plugin as fully inert.</summary>
        public static bool IsHost { get { EnsureConfig(); return _isHost; } }

        /// <summary>Parental is configured (the single Enabled switch in litebox-parental.dat) AND the host is
        /// one of the two third-party apps. When false the plugin is inert — no write-guard. The host check
        /// keeps the File.Copy guard off anything but LaunchBox.exe / BigBox.exe (never LiteBox.exe, which
        /// writes Data\ legitimately). MUST agree with the ASI's own `enabled` gate — they read the SAME file;
        /// a divergence (e.g. the ASI filters while this stays false) would leave a filtered library unguarded.</summary>
        public static bool ScopeActive { get { EnsureConfig(); return _isHost && _enabled; } }

        /// <summary>The config file EXISTS but couldn't be read (transient sharing / permission / I/O). We can't
        /// tell enabled from disabled — and the ASI may already have read Enabled=1 and be filtering — so this is
        /// NOT "parental off". Callers fail CLOSED on it: keep the write-guard armed, leave the ASI as-is. Only a
        /// process that IS a third-party host cares (LiteBox etc. stay inert).</summary>
        public static bool ConfigIndeterminate { get { EnsureConfig(); return _isHost && _configError; } }

        public static bool Locked => _locked;

        /// <summary>The write-guard's single observable: block a `Data\` write while the in-memory library
        /// may be the FILTERED subset — locked, or mid-unlock before the real reload cleared the latch. Also
        /// armed when the config is indeterminate (we can't prove parental is off, so we can't prove writes safe).</summary>
        public static bool WritesUnsafe => (ScopeActive || ConfigIndeterminate) && _inMemoryFiltered;

        /// <summary>Lock or unlock: (re)arm the ASI read filter, reload the library, move the latch. On lock
        /// the latch arms BEFORE filtering and we are safe regardless of the outcome. On unlock the latch — and
        /// the unlocked state — only commit once the unfiltered library is PROVABLY in memory: the ASI filter is
        /// off (we flipped it, or no ASI is loaded to filter) AND an unfiltered ForceReload settled. Any failure
        /// FAILS CLOSED — re-arm filtering, stay LOCKED, keep the latch — so a save can never persist a filtered
        /// subset. Returns true iff the requested state was fully reached.</summary>
        public static bool SetLocked(bool locked)
        {
            EnsureConfig();
            if (!ScopeActive) return false;

            if (locked)
            {
                _inMemoryFiltered = true;             // arm the latch BEFORE we (re)filter
                _locked = true;
                bool lockAsiOn   = AsiBridge.SetFiltering(true);
                bool lockReloadOk = ForceReload();
                // The state is LOCKED + latched regardless (data-safe). But report honestly whether the VIEW is
                // actually filtered now: the ASI must be filtering (no ASI ⇒ nothing hides content) AND the
                // reload must have settled the filtered view. Callers surface a "refresh/restart" hint otherwise.
                bool effective = lockAsiOn && lockReloadOk;
                Log.Line($"[LockState] LOCKED (effective={effective} asiOn={lockAsiOn} reloadOk={lockReloadOk} writesUnsafe={WritesUnsafe})");
                return effective;
            }

            // Unlock. Only reload once filtering is confirmed OFF — reloading while the ASI still filters would
            // repopulate memory with the FILTERED subset. No ASI loaded → nothing ever filtered → filter is "off".
            bool asiOff    = AsiBridge.SetFiltering(false);
            bool filterOff = asiOff || !AsiBridge.IsAsiLoaded;
            bool reloadOk  = filterOff && ForceReload();
            if (filterOff && reloadOk)
            {
                _locked = false;
                _inMemoryFiltered = false;            // real library restored → writes safe
                Log.Line($"[LockState] unlocked (writesUnsafe={WritesUnsafe})");
                return true;
            }

            // Degraded transition → roll back to a consistent, data-safe LOCKED state.
            AsiBridge.SetFiltering(true);
            _locked = true;
            _inMemoryFiltered = true;
            Log.Line($"[LockState] unlock FAILED (asiOff={asiOff} reloadOk={reloadOk}) — stayed LOCKED (data-safe)");
            return false;
        }

        private static bool ForceReload()
        {
            try { Unbroken.LaunchBox.Plugins.PluginHelper.DataManager.ForceReload(); Log.Line("[LockState] ForceReload done."); return true; }
            catch (Exception ex) { Log.Line("[LockState] ForceReload error: " + ex.Message); return false; }
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
                    // File EXISTS: a read failure here is INDETERMINATE (not "disabled"). Isolate it so a transient
                    // sharing/permission/I/O error can't be mistaken for Enabled=0 and quietly drop protection.
                    try
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
                            // Single switch now. Tolerate the two retired per-app keys the same way the ASI does
                            // (any one on → enabled) so a stale .dat read before LiteBox rewrites it still arms.
                            if (key == "enabled" || key == "launchboxenabled" || key == "bigboxenabled")
                                _enabled = _enabled || on;
                        }
                    }
                    catch (Exception ex)
                    {
                        _configError = true;   // exists-but-unreadable → indeterminate → callers fail closed
                        Log.Line("[LockState] config read error (indeterminate, failing closed): " + ex.Message);
                    }
                }
                // A missing file is a DEFINITE "not configured" (disabled), NOT indeterminate — the ASI has no
                // config either and stays inert, so there is nothing to guard.
                Log.Line($"[LockState] config: isBigBox={_isBigBox} enabled={_enabled} indeterminate={_configError}");
            }
            catch (Exception ex) { Log.Line("[LockState] config load error: " + ex.Message); }
        }
    }
}
