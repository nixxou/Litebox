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
bool debugConsole = args.Contains("--debug") || args.Contains("--headless") || args.Contains("--selftest-writeback") || args.Contains("--selftest-title-sort") || args.Contains("--selftest-game-sort") || args.Contains("--selftest-sort-parity") || args.Contains("--selftest-filter-parity") || args.Contains("--selftest-media-rename") || args.Contains("--selftest-lbxml") || args.Contains("--selftest-disc") || args.Contains("--selftest-m3u") || args.Contains("--selftest-savemove") || args.Contains("--selftest-safewrite") || args.Contains("--mame-submit") || args.Contains("--selftest-mediamerge") || args.Contains("--selftest-hiscore-dat") || args.Contains("--selftest-mame-plugin") || args.Contains("--selftest-playlist-copy") || args.Contains("--selftest-model3d") || args.Contains("--selftest-selection") || args.Contains("--selftest-bakeleak") || args.Contains("--selftest-filter-match") || args.Contains("--hiscore-dat") || args.Contains("--media-audit") || args.Contains("--disc-predict") || args.Contains("--combine-probe") || args.Contains("--rename-probe") || args.Contains("--expand-probe") || args.Contains("--seed-writeback") || args.Contains("--dump-extra") || args.Contains("--dump-emupresets") || args.Contains("--store-sync") || args.Contains("--dump-uninstall-bat") || args.Contains("--deploy-natives") || args.Contains("--migrate") || args.Contains("--sweep-legacy") || args.Contains("--probe-saves") || args.Contains("--pause-demo") || args.Contains("--media-hash") || args.Contains("--dedup-test") || args.Contains("--render-jewel") || args.Contains("--render-glb") || args.Contains("--render-oracle");
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
        // Debug mode also unlocks the per-comparison dup-check trace ([dedup] file-vs-file scores,
        // cache hits, verdicts) — kept off otherwise so the hot path pays no string formatting.
        LbApiHost.Host.Media.Dedup.DedupEngine.Verbose = true;
        // Debug mode also makes options-db namespace violations THROW (typo'd keys fail loudly in dev;
        // production only logs them).
        LbApiHost.Host.Data.LiteBoxOptionsDb.Strict = true;
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
    if (File.Exists(candidate)) return ctx.LoadFromAssemblyPath(candidate);
    // DEV fallback (a bin\ run: selftests, probes, dev GUI): the exe folder has no LB assemblies —
    // probe the TARGET LB's Core instead (PluginLoader.LbCoreDir when --library/--lbroot named one,
    // else the dev-repo sibling LB). Installed runs never get here: BaseDirectory IS Core. Historically
    // this was papered over by a stale SDK dll lying in bin\, which broke the moment versions diverged
    // (a 13.28 copy shadowing the v14 install's SDK).
    try
    {
        var core = PluginLoader.ResolveCoreDir();
        var p = Path.Combine(core, name.Name + ".dll");
        if (File.Exists(p)) return ctx.LoadFromAssemblyPath(p);
    }
    catch { }
    return null;
};

// Dev runs name their target LB with --library <LB>\Data\Platforms (GUI) — pin that install's Core for
// the resolver above BEFORE any SDK type JITs, so a v14 target resolves the v14 SDK (IPluginPaths, …)
// instead of the dev-default LB's copy. Probes taking --lbroot pin it themselves at method entry.
{
    int li = Array.IndexOf(args, "--library");
    if (li >= 0 && li + 1 < args.Length)
        try { PluginLoader.LbCoreDir = Path.GetFullPath(Path.Combine(args[li + 1], "..", "..", "Core")); } catch { }
}

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

// Write the encrypted ScreenScraper dev-credentials file (Core\litebox\config\ss-dev.dat) from clear args and
// exit. Maintenance one-off for whoever owns a ScreenScraper dev account: the clear values never live in source
// or config — only this run's command line. Usage: --write-ss-dev <devid> <devpassword> <softname>
if (args.Contains("--write-ss-dev"))
{
    int wi = Array.IndexOf(args, "--write-ss-dev");
    if (wi + 3 >= args.Length) { Console.WriteLine("usage: --write-ss-dev <devid> <devpassword> <softname>"); return 2; }
    LbApiHost.Host.Media.BaseCredentials.WriteDevCredsFile(args[wi + 1], args[wi + 2], args[wi + 3]);
    Console.WriteLine("ss-dev.dat written.");
    return 0;
}

// Visual probe of the LEGACY PAUSE SCREEN (no game needed): fake context, documents taken from
// <--lbroot>\TestDocs when present (else three fake entries). Exercises the inline-vs-submenu
// rules (≤2 docs / 1 link inline; more → submenu swap), icons and the favicon fetch.
// --demo-docs N / --demo-links N trim the lists to probe each shape. Resume / Exit closes.
if (args.Contains("--pause-demo"))
{
    int ArgN(string name, int def)
    { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var n) ? n : def; }
    var docs = new List<(string Name, string Path)>();
    try
    {
        int ri = Array.IndexOf(args, "--lbroot");
        string dir = Path.Combine(ri >= 0 && ri + 1 < args.Length ? args[ri + 1] : ".", "TestDocs");
        if (Directory.Exists(dir))
            foreach (var f in Directory.GetFiles(dir, "TestDoc.*"))
                docs.Add(("Doc " + Path.GetExtension(f).TrimStart('.').ToUpperInvariant(), f));
    }
    catch { }
    if (docs.Count == 0)
        docs.AddRange(new[] { ("Manual (US)", @"C:\nonexistent\a.pdf"), ("Strategy Guide", @"C:\nonexistent\b.pdf"), ("Map", @"C:\nonexistent\c.png") });
    var links = new List<(string Name, string Url)>
    {
        ("Official site (Wikipedia)", "https://www.wikipedia.org/"),
        ("Longplay (YouTube)", "https://www.youtube.com/"),
    };
    docs = docs.Take(Math.Max(0, ArgN("--demo-docs", docs.Count))).ToList();
    links = links.Take(Math.Max(0, ArgN("--demo-links", links.Count))).ToList();
    var demoCtx = new LbApiHost.Host.Pause.PauseContext
    {
        GameTitle = "Pause Demo", Platform = "LiteBox", Developer = "Probe", ReleaseYear = 2026,
        SessionStartUtc = DateTime.UtcNow, CanViewManual = false, Documents = docs, Links = links,
        OnOpenDocument = p => Console.WriteLine("[pause-demo] open: " + p),
        OnOpenLink = u => Console.WriteLine("[pause-demo] link: " + u),
    };
    var screen = new LbApiHost.Host.Pause.LegacyPauseScreen();
    demoCtx.OnAction = a =>
    {
        Console.WriteLine("[pause-demo] action: " + a);
        if (a is LbApiHost.Host.Pause.PauseAction.Resume or LbApiHost.Host.Pause.PauseAction.ExitGame)
        { screen.Close(); System.Windows.Forms.Application.Exit(); }
    };
    screen.Show(demoCtx);
    System.Windows.Forms.Application.Run();
    return 0;
}

// Empirical probe of the RetroArch integration plugin's command-line behaviour.
if (args.Contains("--probe-emuplugin"))
    return EmuPluginProbe.Run();

// Headless diagnostic of the Game Saves scan pipeline (read-only, real data + real plugins).
if (args.Contains("--probe-saves") || args.Contains("--pause-demo"))
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
    Console.Write(LbApiHost.Host.Install.Uninstaller.BuildScript(Path.Combine(r, "Core"), r, args.Contains("tp")));
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

// Dump the per-view post-load config JSON + its MD5 key (dev/test): --media-hash
if (args.Contains("--media-hash"))
{
    var ml = LbApiHost.Host.Media.MediaLayout.Current;
    Console.WriteLine("list   hash = " + ml.PostLoadHash(false));
    Console.WriteLine("poster hash = " + ml.PostLoadHash(true));
    Console.WriteLine("--- media-postload-list.json ---");   Console.WriteLine(ml.PostLoadJson(false));
    Console.WriteLine("--- media-postload-poster.json ---"); Console.WriteLine(ml.PostLoadJson(true));
    return 0;
}

// Compare two images with every dedup engine (dev/test): --dedup-test <imgA> <imgB> [cpu]
// Prints dhash/phash Hamming distances + the CNN cosine similarity (exercises the Magick preprocess,
// the ONNX natives under ThirdParty\ImageDedup and the DirectML→CPU fallback).
if (args.Contains("--dedup-test"))
{
    int di = Array.IndexOf(args, "--dedup-test");
    if (di + 2 >= args.Length) { Console.WriteLine("usage: --dedup-test <imgA> <imgB> [cpu]"); return 2; }
    string a = args[di + 1], b = args[di + 2];
    bool gpu = !args.Contains("cpu");
    var sw = System.Diagnostics.Stopwatch.StartNew();
    ulong da = LbApiHost.Host.Media.Dedup.DedupHash.DHash(LbApiHost.Host.Media.Dedup.DedupPreprocess.LoadGrayResized(a, 9, 8));
    ulong db = LbApiHost.Host.Media.Dedup.DedupHash.DHash(LbApiHost.Host.Media.Dedup.DedupPreprocess.LoadGrayResized(b, 9, 8));
    Console.WriteLine($"dhash: {da:x16} vs {db:x16}  hamming={LbApiHost.Host.Media.Dedup.DedupHash.Hamming(da, db)}  ({sw.ElapsedMilliseconds} ms)");
    sw.Restart();
    ulong pa = LbApiHost.Host.Media.Dedup.DedupHash.PHash(LbApiHost.Host.Media.Dedup.DedupPreprocess.LoadGrayResized(a, 32, 32));
    ulong pb = LbApiHost.Host.Media.Dedup.DedupHash.PHash(LbApiHost.Host.Media.Dedup.DedupPreprocess.LoadGrayResized(b, 32, 32));
    Console.WriteLine($"phash: {pa:x16} vs {pb:x16}  hamming={LbApiHost.Host.Media.Dedup.DedupHash.Hamming(pa, pb)}  ({sw.ElapsedMilliseconds} ms)");
    sw.Restart();
    if (LbApiHost.Host.Media.Dedup.CnnEmbedder.IsAvailable())
    {
        long ws0 = Environment.WorkingSet;
        using (var cnn = new LbApiHost.Host.Media.Dedup.CnnEmbedder(gpu))
        {
            var ea = cnn.Embed(LbApiHost.Host.Media.Dedup.DedupPreprocess.LoadCnnInput(a));
            var eb = cnn.Embed(LbApiHost.Host.Media.Dedup.DedupPreprocess.LoadCnnInput(b));
            Console.WriteLine($"cnn:   cosine={LbApiHost.Host.Media.Dedup.CnnEmbedder.Cosine(ea, eb):0.0000}  gpu={cnn.GpuActive}  ({sw.ElapsedMilliseconds} ms)");
            Console.WriteLine($"cnn:   session cost: workingset +{(Environment.WorkingSet - ws0) / (1024.0 * 1024):0} MB (total {Environment.WorkingSet / (1024.0 * 1024):0} MB)");
            // "hold": keep the live session 10 s so an external tool can sample the process's GPU
            // (VRAM) counters — the session is what holds the DirectML/D3D12 allocations.
            if (args.Contains("hold")) { Console.WriteLine($"cnn:   holding session 10 s (pid {Environment.ProcessId})..."); System.Threading.Thread.Sleep(10000); }
        }
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Console.WriteLine($"cnn:   after dispose: workingset {Environment.WorkingSet / (1024.0 * 1024):0} MB");
    }
    else Console.WriteLine("cnn:   UNAVAILABLE (deploy ThirdParty\\ImageDedup first — LiteBox.exe --deploy-natives)");
    return 0;
}

// Dev-only: render a jewel case (FF7 PS1) headless to a PNG at a chosen angle — geometry iteration harness.
if (args.Contains("--render-jewel"))
    return LbApiHost.Tools.JewelRenderProbe.Run(args, Array.IndexOf(args, "--render-jewel"));
if (args.Contains("--render-glb"))
    return LbApiHost.Tools.JewelRenderProbe.RunGlb(args, Array.IndexOf(args, "--render-glb"));
if (args.Contains("--render-oracle"))   // ═══ LB-ORACLE ═══ ground-truth render via LB's own FlowModel
    return LbApiHost.Tools.JewelRenderProbe.RunOracle(args, Array.IndexOf(args, "--render-oracle"));

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
if (args.Contains("--selftest-title-sort"))
    return TitleSortSelfTest.Run();
if (args.Contains("--selftest-game-sort"))
    return GameSortSelfTest.Run();
// Desktop vs LB-Web/BB-Web ordering parity — runs the real vendor/game-sort.js under node.
if (args.Contains("--selftest-sort-parity"))
    return SortParitySelfTest.Run();
// Desktop vs LB-Web/BB-Web text-search parity — runs the real vendor/game-sort.js under node.
if (args.Contains("--selftest-filter-parity"))
    return FilterParitySelfTest.Run();
// Media rename / GUID-transit logic, on a real temporary tree.
if (args.Contains("--selftest-media-rename"))
    return MediaRenameSelfTest.Run();
// Playlist copy/paste: source-platform deduction + platform-name substitution.
if (args.Contains("--selftest-playlist-copy"))
    return PlaylistCopySelfTest.Run();
// hiscore.dat parsing: which roms an installed MAME/FBNeo database says can produce a high score.
if (args.Contains("--selftest-hiscore-dat"))
    return HiscoreDatSelfTest.Run();
// plugin.ini / mame.ini editing: we enable MAME's hiscore plugin in a file we don't own — one line changes,
// everything else survives.
if (args.Contains("--selftest-mame-plugin"))
    return MamePluginIniSelfTest.Run();
// Advanced-search matching: OR within a dimension, AND across, and the tokenised multi-valued fields.
// Poster select-all + selection read: both were quadratic in virtual mode (see SelectionSelfTest).
if (args.Contains("--selftest-selection"))
{ int qi = Array.IndexOf(args, "--selftest-selection");
  int qn = qi + 1 < args.Length && int.TryParse(args[qi + 1], out var qv) ? qv : 5000;
  return LbApiHost.Host.Diag.SelectionSelfTest.Run(qn); }
if (args.Contains("--selftest-filter-match"))
    return FilterMatchSelfTest.Run();

if (args.Contains("--disc-predict"))
{ int di = Array.IndexOf(args, "--disc-predict"); return DiscParseSelfTest.Predict(args[di+1]); }
if (args.Contains("--media-audit"))
{ int ai = Array.IndexOf(args, "--media-audit"); return MediaAudit.Run(args[ai+1]); }
if (args.Contains("--selftest-mediamerge"))
    return MediaMergeSelfTest.Run();
if (args.Contains("--selftest-savemove"))
    return SaveMoveSelfTest.Run();
if (args.Contains("--selftest-safewrite"))
    return SafeWriteSelfTest.Run();
if (args.Contains("--selftest-disc"))
    return DiscParseSelfTest.Run();
if (args.Contains("--selftest-m3u"))
    return M3uPlaylistSelfTest.Run();

if (args.Contains("--expand-probe"))
{ int ei = Array.IndexOf(args, "--expand-probe"); return CombineProbe.RunExpand(args[ei+1], args[ei+2]); }

// Replay a Combine on a copy, to diff against LaunchBox's own output.
if (args.Contains("--rename-probe"))
{ int ri = Array.IndexOf(args, "--rename-probe"); return CombineProbe.RunRename(args[ri+1], args[ri+2], args[ri+3], args.Length > ri + 4 ? args[ri+4] : "Auto"); }

if (args.Contains("--combine-probe"))
{ int ci = Array.IndexOf(args, "--combine-probe"); return CombineProbe.Run(args[ci+1], args[ci+2], args[ci+3]); }

// XML output shape vs LaunchBox's. Pass an LB root to also round-trip its real files.
if (args.Contains("--selftest-lbxml"))
{
    int at = Array.IndexOf(args, "--selftest-lbxml");
    return LbXmlSelfTest.Run(at + 1 < args.Length && !args[at + 1].StartsWith("--") ? args[at + 1] : null);
}

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
