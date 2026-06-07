// LiteBox launcher / installer (produces LiteBox.exe meant to live at the LaunchBox ROOT).
//
// Flow:
//   • Already at a LaunchBox root (Core\LaunchBox.exe sits next to me) → silently launch
//     Core\LiteBox.exe (deploying first if it isn't there yet). No window, no prompt.
//   • Otherwise → ask for the ROOT LaunchBox.exe (reject Core\LaunchBox.exe), extract the embedded
//     release zip into <LB>\Core, copy myself to <LB>\LiteBox.exe, write "LiteBox uninstall.bat",
//     then launch.
//
// We locate ourselves via Process.MainModule.FileName (the real exe path) — NOT AppContext.BaseDirectory,
// which for a single-file build can point at a temp extraction folder.
//
// Safety: any pre-existing Core file we'd overwrite that ISN'T one we installed before is backed up to
// Core\_litebox_backup first; the uninstaller restores those and deletes the rest. The set of files we
// install is recorded in Core\_litebox_files.txt.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

namespace LiteBoxLauncher;

internal static class Program
{
    private const string ZipResource = "LiteBox.release.zip";
    private const string Marker = "_litebox_files.txt";
    private const string BackupDir = "_litebox_backup";

    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            string selfPath = Process.GetCurrentProcess().MainModule!.FileName;
            string exeDir = Path.GetDirectoryName(selfPath)!.TrimEnd('\\', '/');

            // Silent install: --install "<LaunchBox root>" (deploy + write uninstall.bat, no UI, no launch).
            int ii = Array.FindIndex(args, a => a.Equals("--install", StringComparison.OrdinalIgnoreCase));
            if (ii >= 0 && ii + 1 < args.Length)
            {
                string r = args[ii + 1].TrimEnd('\\', '/');
                if (!File.Exists(Path.Combine(r, "Core", "LaunchBox.exe")))
                {
                    MessageBox.Show("--install: not a LaunchBox root (no Core\\LaunchBox.exe): " + r, "LiteBox", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 1;
                }
                WriteUninstallBat(r, Deploy(r, selfPath, copySelf: true));
                return 0;
            }

            // Already sitting at a LaunchBox root? (Core\LaunchBox.exe next to me) → just launch.
            if (File.Exists(Path.Combine(exeDir, "Core", "LaunchBox.exe")))
            {
                if (!File.Exists(Path.Combine(exeDir, "Core", "LiteBox.exe")))
                    WriteUninstallBat(exeDir, Deploy(exeDir, selfPath, copySelf: false));   // host not deployed yet
                LaunchHost(exeDir, args);
                return 0;
            }

            // Not in place → interactive install.
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            string root = AskLaunchBoxRoot();
            if (root == null) return 1;   // cancelled

            WriteUninstallBat(root, Deploy(root, selfPath, copySelf: true));
            MessageBox.Show(
                $"LiteBox was installed into:\n{root}\n\n" +
                "A LiteBox.exe was placed at the LaunchBox root (run it to start LiteBox),\n" +
                "along with \"LiteBox uninstall.bat\" to remove everything.",
                "LiteBox — installation complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
            LaunchHost(root, args);
            return 0;
        }
        catch (Exception ex)
        {
            try { MessageBox.Show("LiteBox launcher error:\n\n" + ex, "LiteBox", MessageBoxButtons.OK, MessageBoxIcon.Error); } catch { }
            return 1;
        }
    }

    // Prompt for the ROOT LaunchBox.exe, validating it isn't the one inside Core.
    static string AskLaunchBoxRoot()
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

            string dir = Path.GetDirectoryName(dlg.FileName)?.TrimEnd('\\', '/');
            if (string.IsNullOrEmpty(dir)) continue;

            if (string.Equals(Path.GetFileName(dir), "Core", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("That's the LaunchBox.exe inside the Core folder. Pick the one at the LaunchBox ROOT (one level up).",
                    "Wrong file", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                continue;
            }
            if (!File.Exists(Path.Combine(dir, "Core", "LaunchBox.exe")))
            {
                MessageBox.Show("This folder doesn't look like a LaunchBox root (no Core\\LaunchBox.exe found next to it).",
                    "Wrong folder", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                continue;
            }
            return dir;
        }
    }

    // Extract the embedded release zip into <root>\Core (backing up any foreign file we'd overwrite),
    // record what we installed, and (optionally) copy this launcher to <root>\LiteBox.exe.
    // Returns the list of Core file names installed (used to bake the uninstaller).
    static List<string> Deploy(string root, string selfPath, bool copySelf)
    {
        string core = Path.Combine(root, "Core");
        Directory.CreateDirectory(core);
        string backup = Path.Combine(core, BackupDir);
        var prevOurs = ReadMarker(core);
        var installed = new List<string>();

        using (var zs = Assembly.GetExecutingAssembly().GetManifestResourceStream(ZipResource)
                        ?? throw new InvalidOperationException("Embedded release zip not found."))
        using (var zip = new ZipArchive(zs, ZipArchiveMode.Read))
        {
            foreach (var e in zip.Entries)
            {
                if (string.IsNullOrEmpty(e.Name) || e.FullName.EndsWith("/")) continue;
                if (string.Equals(e.Name, "README.txt", StringComparison.OrdinalIgnoreCase)) continue;

                string target = Path.Combine(core, e.Name);
                // Back up a pre-existing FOREIGN file once, so uninstall can restore LaunchBox's own.
                // NEVER back up our own LiteBox.* files (LaunchBox never ships them) — otherwise a first
                // install over an older marker-less LiteBox would "restore" the old ones on uninstall
                // instead of removing them.
                bool alwaysOurs = e.Name.StartsWith("LiteBox.", StringComparison.OrdinalIgnoreCase);
                if (!alwaysOurs && File.Exists(target) && !prevOurs.Contains(e.Name))
                {
                    Directory.CreateDirectory(backup);
                    string bpath = Path.Combine(backup, e.Name);
                    if (!File.Exists(bpath)) File.Copy(target, bpath);
                }
                using (var es = e.Open())
                using (var fs = new FileStream(target, FileMode.Create, FileAccess.Write))
                    es.CopyTo(fs);
                installed.Add(e.Name);
            }
        }
        WriteMarker(core, installed);

        if (copySelf)
        {
            string dst = Path.Combine(root, "LiteBox.exe");
            if (!string.Equals(Path.GetFullPath(selfPath), Path.GetFullPath(dst), StringComparison.OrdinalIgnoreCase))
                File.Copy(selfPath, dst, overwrite: true);
        }
        return installed;
    }

    static void LaunchHost(string root, string[] args)
    {
        string hostExe = Path.Combine(root, "Core", "LiteBox.exe");
        if (!File.Exists(hostExe)) throw new FileNotFoundException("Core\\LiteBox.exe not found after deployment.", hostExe);
        // The host is a WinExe: launching it normally shows its GUI and NO console (transparent). Forward
        // our args (e.g. --debug) so "LiteBox.exe --debug" at the root opens the host's console.
        var psi = new ProcessStartInfo
        {
            FileName = hostExe,
            WorkingDirectory = root,   // host self-normalises CWD to the LB root anyway; set it explicitly to match
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        Process.Start(psi);
    }

    static HashSet<string> ReadMarker(string core)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string m = Path.Combine(core, Marker);
            if (File.Exists(m))
                foreach (var l in File.ReadAllLines(m))
                    if (l.Trim().Length > 0) set.Add(l.Trim());
        }
        catch { }
        return set;
    }

    static void WriteMarker(string core, IEnumerable<string> files)
    {
        try { File.WriteAllLines(Path.Combine(core, Marker), files); } catch { }
    }

    // Generates "<root>\LiteBox uninstall.bat": restores backed-up foreign files, deletes ours, the
    // host's runtime-deployed ThirdParty natives, the config/journal, the root launcher, then self-deletes.
    // The installed file list is BAKED IN here (not read from a runtime marker) so the uninstaller works
    // regardless of whether the marker survived.
    static void WriteUninstallBat(string root, IEnumerable<string> installed)
    {
        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("setlocal EnableExtensions");
        sb.AppendLine("cd /d \"%~dp0\"");
        sb.AppendLine("echo Uninstalling LiteBox...");
        sb.AppendLine("taskkill /IM LiteBox.exe /F >nul 2>&1");
        sb.AppendLine("ping -n 2 127.0.0.1 >nul");
        // Restore-or-delete each Core file we installed (baked in at install time).
        foreach (var f in installed)
            sb.AppendLine($"call :undo \"{f}\"");
        sb.AppendLine($"rmdir /s /q \"Core\\{BackupDir}\" 2>nul");
        sb.AppendLine($"del /q \"Core\\{Marker}\" 2>nul");
        // ThirdParty natives the host deploys on first run (also re-created by ExtendDB if present).
        sb.AppendLine("del /q \"ThirdParty\\Everything\\Everything64.dll\" 2>nul");
        sb.AppendLine("del /q \"ThirdParty\\ExtendDB\\Magick.Native-Q16-x64.dll\" 2>nul");
        sb.AppendLine("rmdir \"ThirdParty\\Everything\" 2>nul");
        sb.AppendLine("rmdir \"ThirdParty\\ExtendDB\" 2>nul");
        sb.AppendLine("rmdir \"ThirdParty\" 2>nul");
        // Remove LiteBox's leftover thumb cache (Plugins\ExtendDB\cache) ONLY when the ExtendDB plugin
        // is NOT installed — i.e. Plugins\ExtendDB contains nothing but the "cache" directory. Rigorous:
        // any other entry (or an ExtendDB.dll) aborts the deletion so a real ExtendDB is never touched.
        sb.AppendLine("set \"_ed=Plugins\\ExtendDB\"");
        sb.AppendLine("if not exist \"%_ed%\\cache\\\" goto :skip_edcache");
        sb.AppendLine("set \"_other=\"");
        sb.AppendLine("for /f \"delims=\" %%X in ('dir /b \"%_ed%\" 2^>nul') do if /i not \"%%X\"==\"cache\" set \"_other=1\"");
        sb.AppendLine("if exist \"%_ed%\\ExtendDB.dll\" set \"_other=1\"");
        sb.AppendLine("if not defined _other rmdir /s /q \"%_ed%\"");
        sb.AppendLine(":skip_edcache");
        // Config / journal / default whitelist (LiteBox-created).
        sb.AppendLine("del /q \"Core\\LiteBox.ini\" 2>nul");
        sb.AppendLine("del /q \"Core\\LiteBox.pending\" 2>nul");
        sb.AppendLine("del /q \"Core\\whitelist.txt\" 2>nul");
        // The root launcher.
        sb.AppendLine("del /q \"LiteBox.exe\" 2>nul");
        sb.AppendLine("echo Done.");
        sb.AppendLine("pause");
        sb.AppendLine("(goto) 2>nul & del \"%~f0\"");
        sb.AppendLine(":undo");
        sb.AppendLine($"if exist \"Core\\{BackupDir}\\%~nx1\" ( move /y \"Core\\{BackupDir}\\%~nx1\" \"Core\\%~1\" >nul ) else ( del /q \"Core\\%~1\" 2>nul )");
        sb.AppendLine("exit /b");
        File.WriteAllText(Path.Combine(root, "LiteBox uninstall.bat"), sb.ToString(), new UTF8Encoding(false));
    }
}
