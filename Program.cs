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
if (args.Contains("--debug") || args.Contains("--headless") || args.Contains("--selftest-writeback") || args.Contains("--seed-writeback") || args.Contains("--dump-extra") || args.Contains("--dump-emupresets") || args.Contains("--store-sync"))
    DebugConsole.Enable();

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
AssemblyLoadContext.Default.Resolving += (ctx, name) =>
{
    var candidate = Path.Combine(AppContext.BaseDirectory, name.Name + ".dll");
    return File.Exists(candidate) ? ctx.LoadFromAssemblyPath(candidate) : null;
};

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

// Default (no args, or --host): run the host GUI.
return HostBoot.Run(args);

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
            var w = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            Console.SetOut(w);
            Console.SetError(w);
        }
        catch { }
    }
}
