// Placement + install/uninstall logic for the standalone parental plugin. Mirrors LiteBox's
// Host\Parental\ParentalNativeInstall exactly so both installers produce the SAME layout:
//   • <root>\Core\winhttp.dll                         — Ultimate ASI Loader
//   • <root>\Core\litebox-parental.asi                — trigger (sets DOTNET_STARTUP_HOOKS)
//   • <root>\Plugins\litebox-parental\litebox-parental.dll         — managed dll (startup hook + plugin)
//   • <root>\Plugins\litebox-parental\litebox-parental-native.bin  — native hooks
// <root> is the LaunchBox install root (the folder that holds Core\ and Plugins\), i.e. the directory of the
// ROOT LaunchBox.exe — NOT Core\LaunchBox.exe. Core\litebox-parental.dat (shared config) is NEVER touched on
// uninstall, so LiteBox keeps working.

using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;

namespace LiteBoxParentalInstaller;

internal sealed record Layout(string Root, string Core, string PluginDir, string BigBoxSettings, string Dat);

internal static class InstallerCore
{
    private const string PluginRel = @"Plugins\litebox-parental";

    // Each payload file is written BOTH to its live location AND to a ".api" backup on the OTHER side, so the
    // SelfHeal (managed, restores Core\ from the plugin stash) and the trigger (native, restores the plugin from
    // the Core stash) can rebuild whatever a LaunchBox update wipes. (resource, live sub\name, stash sub\name).
    // Plugin files first so the trigger never points at a missing dll.
    private static readonly (string Res, string LiveSub, string LiveName, string StashSub, string StashName)[] Files =
    {
        ("payload/litebox-parental.dll",        PluginRel, "litebox-parental.dll",        "Core",    "litebox-parental.dll.api"),
        ("payload/litebox-parental-native.bin", PluginRel, "litebox-parental-native.bin", "Core",    "litebox-parental-native.bin.api"),
        ("payload/winhttp.dll",                 "Core",    "winhttp.dll",                 PluginRel, "winhttp.dll.api"),
        ("payload/litebox-parental.asi",        "Core",    "litebox-parental.asi",        PluginRel, "litebox-parental.asi.api"),
    };

    // ── Root resolution ──────────────────────────────────────────────────────

    /// <summary>A LaunchBox root holds Core\ with the real LaunchBox.exe/BigBox.exe (modern layout: the root exe
    /// is a launcher, the app runs from Core\). Accept a root that at least has Core\ and a LaunchBox/BigBox exe.</summary>
    public static bool LooksLikeRoot(string? dir)
    {
        try
        {
            if (string.IsNullOrEmpty(dir)) return false;
            var core = Path.Combine(dir, "Core");
            if (!Directory.Exists(core)) return false;
            return File.Exists(Path.Combine(core, "LaunchBox.exe"))
                || File.Exists(Path.Combine(core, "BigBox.exe"))
                || File.Exists(Path.Combine(dir, "LaunchBox.exe"))
                || File.Exists(Path.Combine(dir, "BigBox.exe"));
        }
        catch { return false; }
    }

    /// <summary>Root derived from a selected LaunchBox.exe — its own directory. Callers validate with LooksLikeRoot.</summary>
    public static string RootFromExe(string exePath) => Path.GetDirectoryName(Path.GetFullPath(exePath)) ?? exePath;

    public static Layout Resolve(string root) => new(
        root,
        Path.Combine(root, "Core"),
        Path.Combine(root, "Plugins", "litebox-parental"),
        Path.Combine(root, "Data", "BigBoxSettings.xml"),
        Path.Combine(root, "Core", "litebox-parental.dat"));

    public static bool IsInstalled(Layout l) =>
        File.Exists(Path.Combine(l.Core, "winhttp.dll")) && File.Exists(Path.Combine(l.Core, "litebox-parental.asi"));

    /// <summary>LiteBox is installed in this LaunchBox folder (it SHARES litebox-parental.dat) — so removing the
    /// .dat would also wipe LiteBox's parental settings.</summary>
    public static bool LiteBoxInstalled(Layout l)
    {
        try { return File.Exists(Path.Combine(l.Core, "LiteBox.exe")) || File.Exists(Path.Combine(l.Root, "LiteBox.exe")); }
        catch { return false; }
    }

    // ── Host-running guard ─────────────────────────────────────────────────────

    /// <summary>LaunchBox/BigBox holds the files open — install/uninstall must run with the host closed.</summary>
    public static bool HostRunning()
    {
        try
        {
            return Process.GetProcessesByName("LaunchBox").Length > 0
                || Process.GetProcessesByName("BigBox").Length > 0;
        }
        catch { return false; }
    }

    // ── PIN gate (uninstall) ───────────────────────────────────────────────────

    /// <summary>BigBox's encrypted &lt;LockPin&gt; blob, or null when no PIN is set (absent / self-closing / empty).
    /// Matches both &lt;LockPin /&gt; and &lt;LockPin&gt;blob&lt;/LockPin&gt;, with or without attributes.</summary>
    private static string? StoredPinBlob(Layout l)
    {
        try
        {
            if (!File.Exists(l.BigBoxSettings)) return null;
            var m = Regex.Match(File.ReadAllText(l.BigBoxSettings), @"<LockPin\b[^>]*/>|<LockPin\b[^>]*>([^<]*)</LockPin>");
            if (!m.Success) return null;
            var blob = m.Groups[1].Value.Trim();
            return blob.Length == 0 ? null : blob;
        }
        catch { return null; }
    }

    public static bool HasPin(Layout l) => StoredPinBlob(l) != null;

    /// <summary>True when <paramref name="entered"/> matches BigBox's PIN (or no PIN is set).</summary>
    public static bool VerifyPin(Layout l, string entered)
    {
        var blob = StoredPinBlob(l);
        if (blob == null) return true;                     // no PIN → nothing to verify
        var clear = PinCrypto.Decrypt(blob);
        return clear.Length > 0 && clear == (entered ?? "").Trim();
    }

    // ── Install / Uninstall ────────────────────────────────────────────────────

    public static (bool ok, string message) Install(Layout l)
    {
        if (HostRunning()) return (false, "Close LaunchBox and BigBox first, then try again.");
        string backupNote = BackupDataXml(l);       // safety net BEFORE we touch anything
        string importsNote = DisableAutoImports(l); // turn off auto ROM imports once, at install
        try
        {
            foreach (var (res, liveSub, liveName, stashSub, stashName) in Files)
            {
                var liveDir = Path.Combine(l.Root, liveSub);
                Directory.CreateDirectory(liveDir);
                WriteResource(res, Path.Combine(liveDir, liveName));      // the live file

                var stashDir = Path.Combine(l.Root, stashSub);
                Directory.CreateDirectory(stashDir);
                WriteResource(res, Path.Combine(stashDir, stashName));    // its cross-side .api backup
            }
            return (true, "Parental plugin installed." + backupNote + importsNote + "\n\nRestart LaunchBox / BigBox for it to take effect.");
        }
        catch (UnauthorizedAccessException)
        {
            return (false, "Access denied writing to the LaunchBox folder.\n\nTry running this installer as administrator.");
        }
        catch (Exception ex)
        {
            return (false, "Install failed (a file may be locked — close LaunchBox / BigBox):\n" + ex.Message);
        }
    }

    public static (bool ok, string message) Uninstall(Layout l, bool removeDat)
    {
        if (HostRunning()) return (false, "Close LaunchBox and BigBox first, then try again.");
        var problems = new List<string>();
        void Del(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) { problems.Add(Path.GetFileName(path) + ": " + ex.Message); }
        }

        // Trigger first (startup hook stops firing), then everything else — both the live files AND their .api
        // backups on each side. Core\litebox-parental.dat is deliberately LEFT in place (shared with LiteBox).
        Del(Path.Combine(l.Core, "litebox-parental.asi"));
        Del(Path.Combine(l.Core, "winhttp.dll"));
        foreach (var (res, liveSub, liveName, stashSub, stashName) in Files)
        {
            Del(Path.Combine(l.Root, liveSub, liveName));
            Del(Path.Combine(l.Root, stashSub, stashName));
        }
        Del(Path.Combine(l.PluginDir, "litebox-parental.dat.api"));   // the runtime config backup SelfHeal writes
        if (removeDat) Del(l.Dat);   // shared config — only when the user opted in (+ confirmed if LiteBox is present)
        // Remove leftover plugin artefacts (logs) and the folder if now empty.
        try
        {
            if (Directory.Exists(l.PluginDir))
            {
                foreach (var f in Directory.GetFiles(l.PluginDir, "*.log")) Del(f);
                if (Directory.GetFileSystemEntries(l.PluginDir).Length == 0) Directory.Delete(l.PluginDir);
            }
        }
        catch { /* best-effort */ }

        if (problems.Count > 0)
            return (false, "Some files could not be removed (close LaunchBox / BigBox):\n" + string.Join("\n", problems));
        var datNote = removeDat
            ? "The shared configuration (litebox-parental.dat) was also removed."
            : "The shared configuration (litebox-parental.dat) was kept.";
        return (true, "Parental plugin removed.\n\n" + datNote + "\nRestart LaunchBox / BigBox to fully clear it.");
    }

    // ── Pre-install safety backup ───────────────────────────────────────────────

    /// <summary>Before install, archive EVERY .xml under Data\ (hierarchy preserved) into
    /// Backups\ParentalControl-backupbeforeinstall-&lt;datetime&gt;.7z using the 7-Zip LaunchBox ships. Returns a
    /// one-line note appended to the install message. Never throws, never blocks the install.</summary>
    private static string BackupDataXml(Layout l)
    {
        try
        {
            var data = Path.Combine(l.Root, "Data");
            if (!Directory.Exists(data)) return "";
            if (!Directory.EnumerateFiles(data, "*.xml", SearchOption.AllDirectories).Any()) return "";   // nothing to back up

            var sevenZip = SevenZipPath(l);
            if (sevenZip == null) return "\n\n(Note: Data XML was NOT backed up — 7-Zip not found in the LaunchBox folder.)";

            var backups = Path.Combine(l.Root, "Backups");
            Directory.CreateDirectory(backups);
            var archive = Path.Combine(backups, "ParentalControl-backupbeforeinstall-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".7z");

            var psi = new ProcessStartInfo
            {
                FileName = sevenZip,
                WorkingDirectory = l.Root,        // store paths relative to root → "Data\...\*.xml"
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("a");            // add to archive
            psi.ArgumentList.Add(archive);
            psi.ArgumentList.Add(@"Data\*.xml");  // only .xml …
            psi.ArgumentList.Add("-r");           // … recursively (keeps the Data\ sub-folder hierarchy)

            using var p = Process.Start(psi);
            if (p == null) return "\n\n(Note: Data XML backup could not be started.)";
            p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();   // drain so 7-Zip can't block on a full pipe
            if (!p.WaitForExit(120000)) { try { p.Kill(); } catch { } return "\n\n(Note: Data XML backup timed out.)"; }
            if (p.ExitCode == 0 || p.ExitCode == 1)   // 0 = ok, 1 = 7-Zip "warning" (archive still created)
                return "\n\nBacked up Data XML → Backups\\" + Path.GetFileName(archive);
            return "\n\n(Note: Data XML backup failed — 7-Zip exit code " + p.ExitCode + ".)";
        }
        catch (Exception ex) { return "\n\n(Note: Data XML backup error: " + ex.Message + ")"; }
    }

    /// <summary>Disable LaunchBox's automatic ROM imports (Options → Automated Imports) ONCE at install time: the
    /// feature scans for and imports new ROMs at startup and while running, which would mutate the library behind
    /// parental control. We edit Data\Settings.xml on disk (LaunchBox is closed during install, so it reloads our
    /// value cleanly). Both flags → false. Surgical replace; the rest of the file is untouched. Returns a note.</summary>
    private static string DisableAutoImports(Layout l)
    {
        try
        {
            var path = Path.Combine(l.Root, "Data", "Settings.xml");
            if (!File.Exists(path)) return "";
            var xml = File.ReadAllText(path);
            var before = xml;
            foreach (var name in new[] { "EnableAutomatedImports", "EnableRomAutoImports" })
            {
                // <Name>…</Name> or self-closing <Name/> (with/without attributes) → <Name>false</Name>.
                var pat = "<" + name + @"\b[^>]*/>|<" + name + @"\b[^>]*>[^<]*</" + name + ">";
                if (Regex.IsMatch(xml, pat)) xml = Regex.Replace(xml, pat, "<" + name + ">false</" + name + ">");
            }
            if (xml == before) return "";
            File.WriteAllText(path, xml, new System.Text.UTF8Encoding(false));
            return "\n\nDisabled automatic ROM imports.";
        }
        catch { return ""; }
    }

    /// <summary>The 7-Zip LaunchBox ships (or a couple of fallbacks). Null when none is present.</summary>
    private static string? SevenZipPath(Layout l)
    {
        foreach (var rel in new[] { @"ThirdParty\7-Zip\7z.exe", @"Core\7z.exe", @"Core\7za.exe", "7z.exe", "7za.exe" })
        {
            var p = Path.Combine(l.Root, rel);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    // ── Embedded payload ───────────────────────────────────────────────────────

    private static void WriteResource(string logicalName, string destPath)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var s = asm.GetManifestResourceStream(logicalName)
            ?? throw new FileNotFoundException("Embedded payload missing: " + logicalName);
        var tmp = destPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var f = File.Create(tmp)) s.CopyTo(f);
        try { File.Move(tmp, destPath, overwrite: true); }
        catch { try { File.Delete(tmp); } catch { } throw; }
    }
}
