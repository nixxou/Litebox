// Self-install / relocate logic for the merged LiteBox.exe (installer + host in one self-contained
// single-file binary). Called once at startup, BEFORE the host GUI boots.
//
// Where am I?  (self = the exe's own directory)
//   • self is <LB>\Core         → I'm the host in place → boot the GUI (TryRun returns false).
//   • self is a dev build        → a loose LiteBox.dll sits next to me → boot (never self-install a dev build).
//   • self is <LB> root          → Core\LaunchBox.exe sits next to me → copy myself to Core\LiteBox.exe,
//                                   drop a re-launcher at the root, then launch Core\LiteBox.exe.
//   • self is anywhere else       → ask for the ROOT LaunchBox.exe, then install as above.
//
// Because the binary is a self-contained SINGLE FILE, "installing" is just copying MYSELF into Core —
// the native payload (RAHasher, Everything64, Magick.Native, steam_api64) is embedded and unpacked to
// <LB>\ThirdParty\… by NativeInstaller when the Core host boots. No zip, no loose deps.

#nullable enable

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace LbApiHost.Host.Install;

internal static class Installer
{
    /// <summary>Handles install/relocate. Returns true when it took over (caller must return
    /// <paramref name="exitCode"/>); false when we're already the in-place host and should boot the GUI.</summary>
    public static bool TryRun(string[] args, out int exitCode)
    {
        exitCode = 0;
        try
        {
            string selfPath = Process.GetCurrentProcess().MainModule!.FileName;
            string selfDir = Path.GetDirectoryName(selfPath)!.TrimEnd('\\', '/');

            // In place already? (I'm <LB>\Core\LiteBox.exe) or a dev build (loose LiteBox.dll next to a
            // NON-single-file exe) → just boot the GUI.
            bool inCore = string.Equals(Path.GetFileName(selfDir), "Core", StringComparison.OrdinalIgnoreCase)
                          && File.Exists(Path.Combine(selfDir, "LaunchBox.exe"));
            bool devBuild = File.Exists(Path.Combine(selfDir, "LiteBox.dll"));

#if SELF_CONTAINED_BUILD
            // Silent scripted install: --install "<LaunchBox root>" (no UI, no launch) — checked first so it
            // works from anywhere, even from within Core.
            int ii = Array.FindIndex(args, a => a.Equals("--install", StringComparison.OrdinalIgnoreCase));
            if (ii >= 0 && ii + 1 < args.Length)
            {
                string r = args[ii + 1].TrimEnd('\\', '/');
                if (!File.Exists(Path.Combine(r, "Core", "LaunchBox.exe")))
                {
                    Warn("--install: not a LaunchBox root (no Core\\LaunchBox.exe): " + r);
                    exitCode = 1; return true;
                }
                InstallToCore(r, selfPath);
                return true;
            }

            if (inCore || devBuild) return false;

            // Self-contained single-file dropped outside Core → install. WinForms file/message dialogs require
            // an STA thread (the process main thread is MTA), so the picker + prompts run on a dedicated one.
            bool atRoot = File.Exists(Path.Combine(selfDir, "Core", "LaunchBox.exe"));
            string? root = atRoot ? selfDir : RunSta(AskLaunchBoxRoot);   // dropped somewhere random → ask
            if (root == null) { exitCode = 1; return true; }              // cancelled

            InstallToCore(root, selfPath);
            if (!atRoot)
                RunSta(() =>
                {
                    MessageBox.Show(
                        $"LiteBox was installed into:\n{root}\n\nStarting it now. A LiteBox.exe was also placed at the\n" +
                        "LaunchBox root — run it any time. (Uninstall from LiteBox's Options.)",
                        "LiteBox — installation complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return true;
                });
            LaunchCoreHost(root, args);
            return true;
#else
            // Framework-dependent "zip" build: Core-only. It can't self-install (no bundled runtime, no
            // embedded payload), so if it isn't in place just tell the user where it belongs.
            if (inCore || devBuild) return false;
            MessageBox.Show(
                "This LiteBox needs to live in your LaunchBox \"Core\" folder.\n\n" +
                "Extract the zip into <LaunchBox>\\Core — or use the standalone (self-installing) LiteBox.exe " +
                "to install it from anywhere.",
                "LiteBox", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            exitCode = 1; return true;
#endif
        }
        catch (Exception ex)
        {
            try { Warn("LiteBox install error:\n\n" + ex); } catch { }
            exitCode = 1; return true;
        }
    }

#if SELF_CONTAINED_BUILD
    // Copy MYSELF to <root>\Core\LiteBox.exe (the host) and to <root>\LiteBox.exe (the root re-launcher).
    // Never copies over itself. (Uninstall is done in-app from LiteBox's Options — no .bat is written.)
    private static void InstallToCore(string root, string selfPath)
    {
        string core = Path.Combine(root, "Core");
        Directory.CreateDirectory(core);
        string full = Path.GetFullPath(selfPath);

        string coreExe = Path.Combine(core, "LiteBox.exe");
        if (!string.Equals(full, Path.GetFullPath(coreExe), StringComparison.OrdinalIgnoreCase))
            File.Copy(selfPath, coreExe, overwrite: true);

        string rootExe = Path.Combine(root, "LiteBox.exe");
        if (!string.Equals(full, Path.GetFullPath(rootExe), StringComparison.OrdinalIgnoreCase))
            try { File.Copy(selfPath, rootExe, overwrite: true); } catch { }
    }

    // Launch <root>\Core\LiteBox.exe (the host), forwarding our args (minus --install), CWD = root.
    private static void LaunchCoreHost(string root, string[] args)
    {
        string hostExe = Path.Combine(root, "Core", "LiteBox.exe");
        if (!File.Exists(hostExe)) throw new FileNotFoundException("Core\\LiteBox.exe missing after install.", hostExe);
        var psi = new ProcessStartInfo { FileName = hostExe, WorkingDirectory = root, UseShellExecute = false };
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--install", StringComparison.OrdinalIgnoreCase)) { i++; continue; }
            psi.ArgumentList.Add(args[i]);
        }
        Process.Start(psi);
    }

    // Run a WinForms-dialog action on a dedicated STA thread (the process main thread is MTA).
    private static T RunSta<T>(Func<T> f)
    {
        T result = default!;
        Exception? err = null;
        var t = new Thread(() =>
        {
            try { Application.EnableVisualStyles(); } catch { }   // themed native dialogs; SetCompatibleTextRenderingDefault not needed here
            try { result = f(); } catch (Exception ex) { err = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (err != null) throw err;
        return result;
    }

    // Prompt for the ROOT LaunchBox.exe (reject the one inside Core, require a sibling Core\LaunchBox.exe).
    private static string? AskLaunchBoxRoot()
    {
        while (true)
        {
            using var dlg = new OpenFileDialog
            {
                Title = "Select the ROOT LaunchBox.exe (not the one inside the Core folder)",
                Filter = "LaunchBox.exe|LaunchBox.exe",
                CheckFileExists = true,
            };
            if (dlg.ShowDialog() != DialogResult.OK) return null;

            string? dir = Path.GetDirectoryName(dlg.FileName)?.TrimEnd('\\', '/');
            if (string.IsNullOrEmpty(dir)) continue;
            if (string.Equals(Path.GetFileName(dir), "Core", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("That's the LaunchBox.exe inside the Core folder. Pick the one at the LaunchBox ROOT (one level up).",
                    "Wrong file", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                continue;
            }
            if (!File.Exists(Path.Combine(dir, "Core", "LaunchBox.exe")))
            {
                MessageBox.Show("This folder doesn't look like a LaunchBox root (no Core\\LaunchBox.exe next to it).",
                    "Wrong folder", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                continue;
            }
            return dir;
        }
    }
#endif

    private static void Warn(string msg)
        => MessageBox.Show(msg, "LiteBox", MessageBoxButtons.OK, MessageBoxIcon.Error);
}
