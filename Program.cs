using System;
using System.IO;
using System.Linq;
using System.Runtime.Loader;
using LbApiHost.Host;
using LbApiHost.Tools;
using System.Runtime.InteropServices;

// The app is a WinExe (no console by default → transparent when launched by the launcher). Only
// show a console with --debug (or --headless diagnostics): attach to the launching terminal if any,
// else allocate a fresh one, and route Console.Out/Error to it.
bool debugConsole = args.Contains("--debug") || args.Contains("--headless") || args.Contains("--selftest-writeback") || args.Contains("--seed-writeback") || args.Contains("--dump-extra") || args.Contains("--dump-emupresets") || args.Contains("--store-sync") || args.Contains("--dump-uninstall-bat") || args.Contains("--deploy-natives") || args.Contains("--migrate") || args.Contains("--sweep-legacy") || args.Contains("--probe-saves");
if (debugConsole)
    DebugConsole.Enable();

// True when LiteBox.ini (Core\litebox\) has DebugLog=true. Read inline: the config layer isn't up this early
// and we must NOT write the ini template as a side effect here. Same key=value parse as LiteBoxConfig.
static bool DebugLogWanted(string iniPath)
{
    try
    {
        if (!System.IO.File.Exists(iniPath)) return false;
        foreach (var raw in System.IO.File.ReadAllLines(iniPath))
        {
            var t = raw.Trim();
            if (t.Length == 0 || t[0] == ';' || t[0] == '#' || t[0] == '[') continue;
            int eq = t.IndexOf('=');
            if (eq <= 0 || !t.Substring(0, eq).Trim().Equals("DebugLog", StringComparison.OrdinalIgnoreCase)) continue;
            var v = t.Substring(eq + 1).Trim();
            return v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1";
        }
    }
    catch { }
    return false;
}

// litebox-debug.log is written ONLY in "debug mode": a diagnostic flag above (--debug / --headless / …) OR
// LiteBox.ini "DebugLog=true". A normal launch writes NO log (zero file I/O) — Console output goes to the
// console when one exists, else nowhere. Set DebugLog=true (or pass --debug) to capture the [smartcapture]/…
// trace. Fresh file each launch; tee'd with the console when one exists.
try
{
    var exeDir0 = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    var logDir = string.Equals(Path.GetFileName(exeDir0), "Core", StringComparison.OrdinalIgnoreCase)
        ? Path.Combine(exeDir0, "litebox") : exeDir0;
    if (debugConsole || DebugLogWanted(Path.Combine(logDir, "LiteBox.ini")))
    {
        Directory.CreateDirectory(logDir);
        var fw = new StreamWriter(Path.Combine(logDir, "litebox-debug.log"), append: false, new System.Text.UTF8Encoding(false)) { AutoFlush = true };
        Console.SetOut(new TeeTextWriter(Console.Out, fw));
        Console.SetError(Console.Out);
    }
}
catch { }

// Act like LaunchBox's root launcher: LiteBox.exe lives in <LB>\Core (so
// ExtendDB's Process.MainModule-based paths — LBPath = grand-parent of the exe —
// resolve correctly), but the WORKING DIRECTORY must be the LB root, because
// ExtendDB creates some folders from RELATIVE paths (ThirdParty\ExtendDB,
// ThirdParty\Everything → relative to CWD). Without this they'd land inside Core.
// Our own paths are all derived from AppContext.BaseDirectory (absolute), so they
// are unaffected by this CWD change — including the XML data dir.
try
{
    var exeDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    if (string.Equals(Path.GetFileName(exeDir), "Core", StringComparison.OrdinalIgnoreCase))
    {
        var lbRoot = Path.GetDirectoryName(exeDir);
        if (!string.IsNullOrEmpty(lbRoot)) Environment.CurrentDirectory = lbRoot;
    }
}
catch { /* leave CWD as-is if anything goes wrong */ }

// Version-agnostic SDK binding: resolve any assembly the runtime can't find
// (notably Unbroken.LaunchBox.Plugins, whose version differs between LB installs:
// 13.26 vs 13.27 …) from the app base dir (LB\Core) by simple name, ignoring the
// reference version. This mirrors how an LB plugin binds to the already-loaded SDK
// and lets the SAME host binary run on any LB version without a rebuild.
//
// CRUCIAL: this app is self-contained (UseWPF + UseWindowsForms), so it already carries the whole
// .NET desktop framework in its bundle. LaunchBox's SDK is WPF-based; the first time a plugin JITs
// SDK code that touches WPF (e.g. ExtendDB), the runtime resolves WindowsBase. We must NOT hand it
// Core\WindowsBase.dll — that loads a SECOND copy of an assembly identity already present from the
// bundle, and the CLR aborts with "The located assembly's manifest definition does not match the
// assembly reference." So: (1) reuse an already-loaded assembly of the same name, and (2) never
// redirect Microsoft-signed framework/BCL assemblies to Core. LaunchBox's own assemblies (Unbroken.*)
// are unsigned (no public key token), so they still fall through to Core exactly as before.
var frameworkPkts = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
{ "b77a5c561934e089", "31bf3856ad364e35", "b03f5f7f11d50a3a", "7cec85d7bea7798e", "cc7b13ffcd2ddd51" };
AssemblyLoadContext.Default.Resolving += (ctx, name) =>
{
    // Framework/BCL/desktop assemblies (WindowsBase, PresentationCore/Framework, System.*) are Microsoft-
    // signed. NEVER pull them from Core: this app is self-contained (its own net10 WPF is in the bundle),
    // and loading Core's copy — or a plugin-provided .NET Framework 4.0.0.0 copy — gives a SECOND, mismatched
    // WindowsBase, which crashes WPF init with "could not load DispatcherObject" / "manifest does not match".
    // Returning null lets the runtime bind these to the bundle. LaunchBox's own SDK (Unbroken.*) is unsigned,
    // so it still falls through to Core exactly as intended (version-agnostic plugin-style binding).
    var pkt = name.GetPublicKeyToken();
    if (pkt != null && pkt.Length > 0 && frameworkPkts.Contains(Convert.ToHexString(pkt).ToLowerInvariant()))
        return null;
    var candidate = Path.Combine(AppContext.BaseDirectory, name.Name + ".dll");
    return File.Exists(candidate) ? ctx.LoadFromAssemblyPath(candidate) : null;
};

// Steam achievements helper: read ONE appid's achievement unlock state via Steamworks and print it as
// JSON, then exit. Steamworks binds a single app per process, so LiteBox re-launches itself once per
// query (see Store.SteamHelper). Handled early — never reaches the GUI boot.
if (args.Contains("--steam-ach"))
{
    int si = Array.IndexOf(args, "--steam-ach");
    string appId = (si >= 0 && si + 1 < args.Length) ? args[si + 1] : null;
    return LbApiHost.Host.Store.SteamHelper.RunHelperMode(appId);
}

// Temporary entry point. For now the host only knows how to dump the LB
// plugin SDK surface (the spec we implement next). Real host boot comes later.
string ProjPath(string rel) =>
    Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", rel));

// Dev tools — explicit flags only.
if (args.Contains("--dump-api"))
    return ApiDump.Run(ProjPath("api-surface.txt"));

if (args.Contains("--gen-stubs"))
    return StubGen.Run(ProjPath(Path.Combine("Generated", "Dummies.g.cs")));

// Ctor visibility of the EmulatorPlugin arg classes (can the host `new` them?).
if (args.Contains("--dump-ctors"))
    return CtorDump.Run();

// Empirical probe of the RetroArch integration plugin's command-line behaviour.
if (args.Contains("--probe-emuplugin"))
    return EmuPluginProbe.Run();

// Headless diagnostic of the Game Saves scan pipeline (read-only, real data + real plugins).
if (args.Contains("--probe-saves"))
    return ProbeSaves.Run(args);

// Dump the pending write-back ops of the REAL deploy (diagnostic, read-only).
if (args.Contains("--dump-oplog"))
{
    // Sqlite + friends live in LB\Core (the deploy), not in bin — probe there.
    System.Runtime.Loader.AssemblyLoadContext.Default.Resolving += (ctx, name) =>
    {
        var p = Path.Combine(@"C:\Users\mehdi\source\repos\scrapper-project\LB\Core", name.Name + ".dll");
        return File.Exists(p) ? ctx.LoadFromAssemblyPath(p) : null;
    };
    var dbPath = @"C:\Users\mehdi\source\repos\scrapper-project\LB\Core\LiteBox.pending.db";
    using var log = LbApiHost.Host.Data.OpLog.Open(dbPath);
    var ops = log?.ReadAll();
    Console.WriteLine($"pending ops: {ops?.Count ?? -1}");
    if (ops != null)
        foreach (var op in ops)
            Console.WriteLine($"  #{op.Seq} {op.OpType} {op.Entity}/{op.Id} parent={op.ParentId} field={op.Field} value={(op.Value?.Length > 120 ? op.Value.Substring(0, 120) + "…" : op.Value)}");
    return 0;
}

// Dump LB's Add-Emulator presets from LB\Metadata\LaunchBox.Metadata.db (read-only).
if (args.Contains("--dump-emupresets"))
    return EmuPresetDump.Run(args);

// Dump the self-uninstall .bat (dev/test, read-only): --dump-uninstall-bat <lbRoot> [thumbs] [tp]
if (args.Contains("--dump-uninstall-bat"))
{
    int di = Array.IndexOf(args, "--dump-uninstall-bat");
    string r = (di >= 0 && di + 1 < args.Length) ? args[di + 1].TrimEnd('\\', '/') : AppContext.BaseDirectory;
    Console.Write(LbApiHost.Host.Install.Uninstaller.BuildScript(Path.Combine(r, "Core"), r, args.Contains("thumbs"), args.Contains("tp")));
    return 0;
}

// Deploy/refresh the embedded native payload into <root>\ThirdParty (dev/test): --deploy-natives <root> [refresh]
if (args.Contains("--deploy-natives"))
{
    int di = Array.IndexOf(args, "--deploy-natives");
    string r = (di >= 0 && di + 1 < args.Length) ? args[di + 1].TrimEnd('\\', '/') : AppContext.BaseDirectory;
    LbApiHost.Host.Install.NativeInstaller.EnsureDeployed(r, args.Contains("refresh"));
    Console.WriteLine("[deploy-natives] done -> " + r);
    return 0;
}

// Sweep obsolete leftovers of OLDER LiteBox versions under <root> (dev/test — same as the boot sweep):
//   --sweep-legacy <lbRoot>
if (args.Contains("--sweep-legacy"))
{
    int di = Array.IndexOf(args, "--sweep-legacy");
    string r = (di >= 0 && di + 1 < args.Length) ? args[di + 1].TrimEnd('\\', '/') : AppContext.BaseDirectory;
    LbApiHost.Host.Install.LegacyCleanup.SweepObsolete(r);
    Console.WriteLine("[sweep-legacy] done -> " + r);
    return 0;
}

// Run the config migration against THIS exe's Core\litebox (dev/test): --migrate
if (args.Contains("--migrate"))
{
    bool need = LbApiHost.Host.Install.Migration.MigrateConfigAndNeedNatives();
    Console.WriteLine($"[migrate-test] needNatives={need}  litebox={LbApiHost.Host.LiteBoxPaths.Data}");
    return 0;
}

// Write-back round-trip test (temp files only — never touches real LB data / pending db).
if (args.Contains("--selftest-writeback"))
    return WriteBackSelfTest.Run();

// Seed real write-back changes across Platform XMLs via the plugin API (for the LB-ingestion test).
if (args.Contains("--seed-writeback"))
    return WriteBackSeed.Run(args);

// Read-only: dump the non-IGame fields LiteBox exposes for games matching a title substring.
if (args.Contains("--dump-extra"))
    return WriteBackDump.Run(args);

// Read-only: reconcile GOG/Steam install state against the clients' local DBs and dump before/after.
if (args.Contains("--store-sync"))
{
    System.Runtime.Loader.AssemblyLoadContext.Default.Resolving += (ctx, name) =>
    {
        var p = Path.Combine(@"C:\Users\mehdi\source\repos\scrapper-project\LB\Core", name.Name + ".dll");
        return File.Exists(p) ? ctx.LoadFromAssemblyPath(p) : null;
    };
    return StoreSyncDump.Run(args);
}

// Merged installer / relocate: when this single-file binary is dropped at the LaunchBox ROOT (or anywhere
// outside Core), copy itself into <LB>\Core and launch that host. Returns false when we're already the
// in-place Core host (or a dev build) → fall through and boot the GUI here. Handles the silent --install too.
if (LbApiHost.Host.Install.Installer.TryRun(args, out int installExit))
    return installExit;

// Default (no args, or --host): run the host GUI.
return HostBoot.Run(args);

// Writes to two TextWriters at once (the console, if any, + the debug-log file).
sealed class TeeTextWriter : System.IO.TextWriter
{
    private readonly System.IO.TextWriter _a, _b;
    public TeeTextWriter(System.IO.TextWriter a, System.IO.TextWriter b) { _a = a; _b = b; }
    public override System.Text.Encoding Encoding => _b.Encoding;
    public override void Write(char c) { try { _a.Write(c); } catch { } try { _b.Write(c); } catch { } }
    public override void Write(string? s) { try { _a.Write(s); } catch { } try { _b.Write(s); } catch { } }
    public override void WriteLine(string? s) { try { _a.WriteLine(s); } catch { } try { _b.WriteLine(s); } catch { } }
    public override void Flush() { try { _a.Flush(); } catch { } try { _b.Flush(); } catch { } }
}

// Console allocation for --debug / --headless (WinExe has no console otherwise).
static class DebugConsole
{
    [DllImport("kernel32.dll")] private static extern bool AllocConsole();
    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int dwProcessId);
    private const int ATTACH_PARENT_PROCESS = -1;

    public static void Enable()
    {
        try
        {
            if (!AttachConsole(ATTACH_PARENT_PROCESS)) AllocConsole();
            // Non-ASCII (em-dashes/arrows, of which the logs have many) needs BOTH halves to render:
            // (1) the console's OUTPUT CODEPAGE decides how our bytes are decoded — a fresh Windows
            // console isn't UTF-8, so without this the UTF-8 bytes below come out as mojibake no matter
            // how we encode them; (2) we then write UTF-8 with no BOM (a BOM mid-stream would itself
            // show as garbage). Setting the codepage can throw when stdout is redirected, so guard it
            // and still install the writer.
            try { Console.OutputEncoding = new System.Text.UTF8Encoding(false); } catch { }
            var w = new StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false)) { AutoFlush = true };
            Console.SetOut(w);
            Console.SetError(w);
        }
        catch { }
    }
}
