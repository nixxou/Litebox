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
        private static bool _enabled, _isBigBox, _isHost, _configError, _pinSet;

        private static bool _locked = true;            // runtime lock — starts locked
        // The anti-corruption WRITE LATCH now lives NATIVELY in the .bin (g_writesBlocked), armed/cleared via
        // AsiBridge.SetWritesBlocked. There is no managed WriteGuard anymore (the .bin's CopyFileExW hook is it).

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
        /// <summary>...AND a PIN is set. Without a PIN there is no way to UNLOCK, so enforcing parental would lock
        /// the user out forever — so no PIN ⇒ fully inert, whatever the Enabled flag says.</summary>
        public static bool ScopeActive { get { EnsureConfig(); return _isHost && _enabled && _pinSet; } }

        /// <summary>The config file EXISTS but couldn't be read (transient sharing / permission / I/O). We can't
        /// tell enabled from disabled — and the ASI may already have read Enabled=1 and be filtering — so this is
        /// NOT "parental off". Callers fail CLOSED on it: keep the write-guard armed, leave the ASI as-is. Only a
        /// process that IS a third-party host cares (LiteBox etc. stay inert).</summary>
        public static bool ConfigIndeterminate { get { EnsureConfig(); return _isHost && _configError; } }

        public static bool Locked => _locked;

        /// <summary>Lock or unlock, driving the .bin's two independent native flags in the safe order. On LOCK:
        /// arm the write latch FIRST, then turn read-filtering on, then reload — safe regardless of outcome. On
        /// UNLOCK: turn read-filtering off, reload the REAL library, and only THEN clear the write latch — so a
        /// save during the unlock window can never persist the filtered subset. Any failure FAILS CLOSED (re-arm
        /// filtering, keep the latch, stay LOCKED). Returns true iff the requested state was fully reached.</summary>
        public static bool SetLocked(bool locked)
        {
            EnsureConfig();
            if (!ScopeActive) return false;

            if (locked)
            {
                AsiBridge.SetWritesBlocked(true);         // arm the latch BEFORE we (re)filter
                _locked = true;
                bool readOn       = AsiBridge.SetReadFiltering(true);
                bool lockReloadOk = ForceReload();
                bool effective    = readOn && lockReloadOk;   // honest: is the filtered view actually in place now?
                Log.Line($"[LockState] LOCKED (effective={effective} readOn={readOn} reloadOk={lockReloadOk})");
                try { Branding.Reapply(); } catch { }   // refresh the status-corner
                return effective;
            }

            // Unlock. Turn reads unfiltered, then reload the real library; keep WRITES BLOCKED until that reload
            // settled. No native loaded → nothing ever filtered → treat as "reads off".
            bool readOff   = AsiBridge.SetReadFiltering(false);
            bool filterOff = readOff || !AsiBridge.IsNativeLoaded;
            bool reloadOk  = filterOff && ForceReload();
            if (filterOff && reloadOk)
            {
                AsiBridge.SetWritesBlocked(false);        // real library restored → writes safe now
                _locked = false;
                Log.Line("[LockState] unlocked (writes unblocked after reload)");
                try { Branding.Reapply(); } catch { }   // refresh the status-corner
                return true;
            }

            // Degraded transition → roll back to a consistent, data-safe LOCKED state (latch stays armed).
            AsiBridge.SetReadFiltering(true);
            _locked = true;
            Log.Line($"[LockState] unlock FAILED (readOff={readOff} reloadOk={reloadOk}) — stayed LOCKED, writes still blocked (data-safe)");
            return false;
        }

        private static bool ForceReload()
        {
            try
            {
                Unbroken.LaunchBox.Plugins.PluginHelper.DataManager.ForceReload();
                // Re-render the CURRENT LaunchBox view so the filtered/unfiltered library shows immediately
                // (lock/unlock toggles the .bin's read filter live; without this the games only refresh on the
                // next navigation). LaunchBox-only + UI-thread (the menu click is on it); null/BigBox → no-op.
                try { Unbroken.LaunchBox.Plugins.PluginHelper.LaunchBoxMainViewModel?.RefreshData(); }
                catch (Exception rex) { Log.Line("[LockState] RefreshData error: " + rex.Message); }
                Log.Line("[LockState] ForceReload done.");
                return true;
            }
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
                    // File EXISTS: a read failure is INDETERMINATE (not "disabled"), so it must not be mistaken for
                    // Enabled=0 and quietly drop protection. The .dat is small + static (LiteBox writes it atomically
                    // from another process), so a transient sharing/AV glitch clears fast — retry a few times before
                    // giving up. Only a PERSISTENT failure (locked file / permissions) leaves us indeterminate.
                    string[] lines = null;
                    for (int attempt = 1; attempt <= 3 && lines == null; attempt++)
                    {
                        try { lines = File.ReadAllLines(dat); }
                        catch (Exception ex)
                        {
                            if (attempt >= 3)
                            {
                                _configError = true;   // exists-but-unreadable after retries → indeterminate → fail closed
                                Log.Line("[LockState] config read failed after retries (indeterminate, failing closed): " + ex.Message);
                            }
                            else { try { System.Threading.Thread.Sleep(50); } catch { } }
                        }
                    }
                    if (lines != null)
                    {
                        foreach (var raw in lines)
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
                }
                // A PIN (BigBox LockPin) is required for parental to engage — no PIN, no unlock path, so no lock.
                try { _pinSet = PinVerify.HasPin; } catch { _pinSet = false; }
                // A missing file is a DEFINITE "not configured" (disabled), NOT indeterminate — the ASI has no
                // config either and stays inert, so there is nothing to guard.
                Log.Line($"[LockState] config: isBigBox={_isBigBox} enabled={_enabled} pinSet={_pinSet} indeterminate={_configError}");
            }
            catch (Exception ex) { Log.Line("[LockState] config load error: " + ex.Message); }
        }
    }
}
