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
using System.Text;
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

            // Silent scripted install: --install "<LaunchBox root>"  (no UI, no launch).
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

            // Already the in-place host? (I'm <LB>\Core\LiteBox.exe) → let the GUI boot.
            bool inCore = string.Equals(Path.GetFileName(selfDir), "Core", StringComparison.OrdinalIgnoreCase)
                          && File.Exists(Path.Combine(selfDir, "LaunchBox.exe"));
            if (inCore) return false;

            // Dev build (a loose LiteBox.dll next to a NON-single-file exe) → never self-install; just boot.
            if (File.Exists(Path.Combine(selfDir, "LiteBox.dll"))) return false;

            // Published single-file dropped outside Core → install. WinForms file/message dialogs require an
            // STA thread, but the process main thread is MTA (top-level Main; the GUI uses its own STA thread
            // in HostBoot), so the picker + prompts MUST run on a dedicated STA thread — else OpenFileDialog
            // throws and no picker appears.
            bool atRoot = File.Exists(Path.Combine(selfDir, "Core", "LaunchBox.exe"));
            string? root = atRoot ? selfDir : RunSta(AskLaunchBoxRoot);   // dropped somewhere random → ask
            if (root == null) { exitCode = 1; return true; }              // cancelled

            InstallToCore(root, selfPath);
            if (!atRoot)
                RunSta(() =>
                {
                    MessageBox.Show(
                        $"LiteBox was installed into:\n{root}\n\nStarting it now. A LiteBox.exe was also placed at the\n" +
                        "LaunchBox root (run it any time), along with \"LiteBox uninstall.bat\".",
                        "LiteBox — installation complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return true;
                });
            LaunchCoreHost(root, args);
            return true;
        }
        catch (Exception ex)
        {
            try { Warn("LiteBox install error:\n\n" + ex); } catch { }
            exitCode = 1; return true;
        }
    }

    // Copy MYSELF to <root>\Core\LiteBox.exe (the host) and to <root>\LiteBox.exe (the root re-launcher),
    // then bake the uninstaller. Never copies over itself.
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

        WriteUninstallBat(root);
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
            try { Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false); } catch { }
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

    private static void Warn(string msg)
        => MessageBox.Show(msg, "LiteBox", MessageBoxButtons.OK, MessageBoxIcon.Error);

    // "<root>\LiteBox uninstall.bat": removes LiteBox-exclusive files, PRESERVES everything shared with
    // ExtendDB (the plugin + ThirdParty\{RetroAchievements,ExtendDB,Everything}). Mirrors clean-litebox.bat.
    private static void WriteUninstallBat(string root)
    {
        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("setlocal EnableExtensions");
        sb.AppendLine("cd /d \"%~dp0\"");
        sb.AppendLine("echo Uninstalling LiteBox (ExtendDB + shared ThirdParty preserved)...");
        sb.AppendLine("taskkill /IM LiteBox.exe /F >nul 2>&1");
        sb.AppendLine("ping -n 2 127.0.0.1 >nul");
        sb.AppendLine("del /q \"LiteBox.exe\" 2>nul");                 // root re-launcher
        sb.AppendLine("del /q \"litebox*.log\" 2>nul");
        sb.AppendLine("del /q \"Core\\LiteBox.exe\" 2>nul");           // the host binary
        sb.AppendLine("rmdir /s /q \"Core\\litebox\" 2>nul");         // all LiteBox-created files/dirs live here
        sb.AppendLine("del /q \"ThirdParty\\Steam\\steam_api64.dll\" 2>nul");   // LiteBox-only ThirdParty native
        sb.AppendLine("rmdir \"ThirdParty\\Steam\" 2>nul");
        sb.AppendLine("echo Done.");
        sb.AppendLine("pause");
        sb.AppendLine("(goto) 2>nul & del \"%~f0\"");
        try { File.WriteAllText(Path.Combine(root, "LiteBox uninstall.bat"), sb.ToString(), new UTF8Encoding(false)); } catch { }
    }
}
