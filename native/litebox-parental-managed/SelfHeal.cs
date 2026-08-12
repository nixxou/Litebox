// Cross-redundant self-healing. A LaunchBox UPDATE rewrites Core\ and deletes foreign files it doesn't know —
// notably our ASI loader (winhttp.dll) and trigger (litebox-parental.asi) — which silently breaks native game
// filtering (StartupHook never runs → the .bin is never armed). But updates leave Plugins\ untouched AND leave
// unknown-named files (our ".api" backups, the .dat) in place. So each side keeps ".api" backups of the OTHER
// side's live files:
//
//   Core\                         live: winhttp.dll, litebox-parental.asi, litebox-parental.dat
//                                 stash: litebox-parental.dll.api, litebox-parental-native.bin.api
//   Plugins\litebox-parental\     live: litebox-parental.dll, litebox-parental-native.bin
//                                 stash: winhttp.dll.api, litebox-parental.asi.api, litebox-parental.dat.api
//
// On plugin load we RESTORE whatever live file is missing from its backup, and REFRESH each backup from its live
// file (so the stash self-populates on the first normal run and tracks the .dat). If any native-chain file had to
// be restored, it wasn't armed THIS session — so we show a red warning and restart LaunchBox to arm it.
//
// The reverse (a wiped plugin dll/.bin) is handled natively by the trigger (.asi restores them from the Core
// stash before .NET starts) — see litebox-parental-trigger/src/trigger.cpp.

using System;
using System.Diagnostics;
using System.IO;

namespace LiteBoxParental
{
    internal static class SelfHeal
    {
        private static bool _ran;

        // Core live files backed up next to the plugin (Plugins\litebox-parental\<name>.api).
        private static readonly string[] CoreLive = { "winhttp.dll", "litebox-parental.asi", "litebox-parental.dat" };
        // Plugin live files backed up in Core (Core\<name>.api). The dll can't self-restore (if it were gone we
        // wouldn't be running — the .asi handles that), but the .bin can.
        private static readonly string[] PluginLive = { "litebox-parental.dll", "litebox-parental-native.bin" };

        public static void Run()
        {
            if (_ran) return;
            _ran = true;
            try
            {
                if (!LockState.IsHost) return;                 // LaunchBox/BigBox only — never LiteBox / helpers
                var core = CoreDir();
                var plug = PluginDir();
                if (core == null || plug == null) return;

                bool restoredAny = false;
                foreach (var name in CoreLive)
                    restoredAny |= Sync(live: Path.Combine(core, name), stash: Path.Combine(plug, name + ".api"));
                foreach (var name in PluginLive)
                    restoredAny |= Sync(live: Path.Combine(plug, name), stash: Path.Combine(core, name + ".api"));

                if (restoredAny)
                {
                    Log.Line("[SelfHeal] restored missing file(s) — native chain needs a restart");
                    PromptAndRestart();
                }
            }
            catch (Exception ex) { Log.Line("[SelfHeal] " + ex.Message); }
        }

        /// <summary>Keep live↔stash consistent. If the LIVE file exists, refresh the backup from it (creating it the
        /// first time). If the live file is MISSING but a backup exists, rebuild the live file from it. Returns true
        /// only when the LIVE file was rebuilt (a real restore).</summary>
        private static bool Sync(string live, string stash)
        {
            try
            {
                bool liveEx = File.Exists(live), stashEx = File.Exists(stash);
                if (liveEx)
                {
                    if (!stashEx || Differs(live, stash)) { try { CopyAtomic(live, stash); } catch { } }
                    return false;
                }
                if (stashEx)
                {
                    CopyAtomic(stash, live);
                    Log.Line("[SelfHeal] restored " + live);
                    return true;
                }
                return false;   // neither present — nothing we can do
            }
            catch (Exception ex) { Log.Line("[SelfHeal] sync " + Path.GetFileName(live) + ": " + ex.Message); return false; }
        }

        private static bool Differs(string a, string b)
        {
            try { var fa = new FileInfo(a); var fb = new FileInfo(b); return fa.Length != fb.Length || fa.LastWriteTimeUtc > fb.LastWriteTimeUtc; }
            catch { return true; }
        }

        private static void CopyAtomic(string src, string dst)
        {
            var dir = Path.GetDirectoryName(dst);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = dst + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.Copy(src, tmp, true);
            try { File.Move(tmp, dst, true); } catch { try { File.Delete(tmp); } catch { } throw; }
        }

        private static string CoreDir()
        {
            try { return Path.GetDirectoryName(Process.GetCurrentProcess().MainModule?.FileName ?? ""); }
            catch { return null; }
        }
        private static string PluginDir()
        {
            try { var l = typeof(SelfHeal).Assembly.Location; return string.IsNullOrEmpty(l) ? null : Path.GetDirectoryName(l); }
            catch { return null; }
        }

        // Red warning on the UI thread once WPF is up, then restart the host so the restored native chain arms.
        private static void PromptAndRestart()
        {
            try
            {
                var app = System.Windows.Application.Current;
                if (app == null) { RestartNow(); return; }
                app.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
                {
                    try { ShowRedNotice(); } catch { }
                    RestartNow();
                }));
            }
            catch { RestartNow(); }
        }

        private static void ShowRedNotice()
        {
            using var f = new System.Windows.Forms.Form
            {
                Text = "Parental control",
                FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog,
                StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen,
                MaximizeBox = false,
                MinimizeBox = false,
                ShowInTaskbar = true,
                TopMost = true,
                ClientSize = new System.Drawing.Size(460, 150),
                Font = new System.Drawing.Font("Segoe UI", 9f),
            };
            var msg = new System.Windows.Forms.Label
            {
                AutoSize = false,
                Location = new System.Drawing.Point(16, 16),
                Size = new System.Drawing.Size(428, 84),
                ForeColor = System.Drawing.Color.Firebrick,
                Font = new System.Drawing.Font("Segoe UI", 10f, System.Drawing.FontStyle.Bold),
                Text = "Parental control files were restored after a LaunchBox update.\n\n" +
                       "LaunchBox must restart to re-enable game filtering.\nIt will restart now.",
            };
            var ok = new System.Windows.Forms.Button
            {
                Text = "Restart now",
                DialogResult = System.Windows.Forms.DialogResult.OK,
                Location = new System.Drawing.Point(346, 110),
                Width = 98,
            };
            f.Controls.Add(msg);
            f.Controls.Add(ok);
            f.AcceptButton = ok;
            f.ShowDialog();
        }

        private static void RestartNow()
        {
            try
            {
                var exe = Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(exe)) { Log.Line("[SelfHeal] restart: exe path unknown"); return; }
                Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    WorkingDirectory = Path.GetDirectoryName(exe),
                    UseShellExecute = true,
                });
            }
            catch (Exception ex) { Log.Line("[SelfHeal] restart failed (staying up, unfiltered): " + ex.Message); return; }
            try { Environment.Exit(0); } catch { }   // only after the new instance started
        }
    }
}
