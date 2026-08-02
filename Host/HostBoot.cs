// Host boot: wire dummy services into PluginHelper, load the enabled plugins
// (LiteBox.ini EnabledPlugins; default = every folder under LB\Plugins), fire
// PluginInitialized, then show the GUI (a simple menu of the plugins'
// system-menu items + a blank area).
//
//   --host                         GUI (default). Plugins from LiteBox.ini (EnabledPlugins).
//   --host --plugins <root>        override the plugins root (default LB\Plugins)
//   --host --headless [--loop]     no GUI (diagnostics); --loop keeps it alive
//   --host --headless --menu N     invoke system menu N on the UI thread
//   --host --headless --play       demo PlayGame MessageBox

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Data;
using LbApiHost.Generated;
using LbApiHost.Host.Data;

namespace LbApiHost.Host;

internal static class HostBoot
{
    /// <summary>The resolved &lt;LB&gt;\Plugins root, captured at boot so the
    /// Options → Plugins section lists the same folders the host loads from.</summary>
    public static string PluginsRoot { get; private set; }

    // ── ExtendDB integration (integration-extenddb branch) ────────────────────
    // LiteBox is folding ExtendDB's functionality in natively. The plugin is therefore NEVER loaded here — its
    // DLL would double-provide (and Harmony-patch) what LiteBox now does itself. The Options → Plugins list
    // still SHOWS the folder, greyed out, with a note. Matched by the plugin folder name (Plugins\ExtendDB).
    public const bool IntegrateExtendDb = true;
    public const string ExtendDbFolder = "ExtendDB";
    public static bool IsExtendDb(string folderName) => folderName.Equals(ExtendDbFolder, StringComparison.OrdinalIgnoreCase);

    // ── Hands-free UI drivers (diagnostics / remote testing) ──────────────────
    // Once the main window is shown:
    //   --edit-game "<title|id>"      open Edit Game for that game (id exact → title exact → title contains)
    //   --edit-page "<page>"          page for --edit-game, by node tag or label, case/space-insensitive
    //                                 (Metadata, Notes, "Additional Versions", GameSaves, Emulation, …)
    //   --edit-gamesaves "<title|id>" sugar for --edit-game X --edit-page GameSaves
    //   --edit-emu "<title|id>"       open Edit Emulator for that emulator (id exact → title match)
    //   --options ["<section>"]       open the Options window, optionally on the named section
    //                                 (fuzzy: "gameplay" → "LB · Gameplay")
    public static string AutoPlay;      // --play "<title|id>" → launch on boot (pair with --drylaunch to audit)
    public static string DupCycle;      // --dup-cycle "<title|id>" → hands-free dup-filter lifecycle test (cold/suspend/warm builds)
    public static string AutoGenCache;  // --gencache [csv] → self-driving bulk cache generation test
    public static string AutoEditGame;
    public static string AutoEditEmu;   // --edit-emu "<title|id>" → open Edit Emulator on boot
    public static string AutoEditPage;
    public static string AutoOptions;   // null = not requested; "" = open on the first section

    /// <summary>Immediate subfolder names of <paramref name="root"/> (plugin folders),
    /// sorted case-insensitively. Empty when the root is missing/unreadable.</summary>
    public static List<string> ListPluginFolders(string root)
    {
        var list = new List<string>();
        try
        {
            if (Directory.Exists(root))
                foreach (var d in Directory.GetDirectories(root))
                    list.Add(Path.GetFileName(d));
        }
        catch { }
        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    public static int Run(string[] args)
    {
        string coreDir = AppContext.BaseDirectory;
        Mem.Report("startup");
        InstanceGuard.Probe();   // a 2nd LiteBox must not also write the XMLs / op-log (forces read-only below)
        bool refreshNatives = LbApiHost.Host.Install.Migration.MigrateConfigAndNeedNatives();   // config migration + upgrade detection (before config/db are used)
        Data.DataMaintenance.RunPendingCleanup();   // delete any db flagged for reset in the Caches page — BEFORE any db opens

        // --mame-probe [rom|scores.xml]: passive feasibility probe for driving LB's obfuscated MAME
        // high-score code directly (no UI boot, no network). Sets the LB-root static the core reads, runs
        // the probe, and exits. See Host/Diag/MameProbe.cs.
        if (args.Contains("--mame-probe"))
        {
            string lbRootProbe = Path.GetFullPath(Path.Combine(coreDir, ".."));
            try { SetLaunchBoxCoreRootFolder(lbRootProbe); } catch { }
            Diag.MameProbe.Run(lbRootProbe, GetArg(args, "--mame-probe"));
            return 0;
        }
        // --mame-keyscan [knownKeyHex]: scan the obfuscated core's static fields for the leaderboard blob key
        // (no Harmony). Pass the 13.27 key to flag a match across versions. See Host/Diag/MameProbe.cs.
        if (args.Contains("--mame-keyscan"))
        {
            string lbRootScan = Path.GetFullPath(Path.Combine(coreDir, ".."));
            try { SetLaunchBoxCoreRootFolder(lbRootScan); } catch { }
            Diag.MameProbe.KeyScan(lbRootScan, GetArg(args, "--mame-keyscan"));
            return 0;
        }
        // --mame-drivetest [rom]: drive the core's GamesDatabase.DownloadMameGameLeaderboard directly (read-only).
        if (args.Contains("--mame-drivetest"))
        {
            string lbRootD = Path.GetFullPath(Path.Combine(coreDir, ".."));
            try { SetLaunchBoxCoreRootFolder(lbRootD); } catch { }
            Diag.MameProbe.DriveTest(lbRootD, GetArg(args, "--mame-drivetest"));
            return 0;
        }
        // --mame-uploadtest: no-network self-test of the captured-key fallback crypto (byte-identity check).
        if (args.Contains("--mame-uploadtest"))
        {
            var (match, mine, expected) = Mame.MameUpload.SelfTest();
            Console.WriteLine("[mame-uploadtest] fallback Rijndael-256 blob reproduction: " + (match ? "MATCH ✓" : "MISMATCH ✗"));
            Console.WriteLine("  mine     = " + mine);
            Console.WriteLine("  expected = " + expected);
            return match ? 0 : 1;
        }
        // --lockpin-decrypt <blob> [keyHex] [seedHex]: pure-crypto probe (no core, no network). Decrypt a BigBox
        // LockPin blob with an explicit key/seed pair; defaults to the captured LockPin pair. Used to test whether
        // that key still decrypts a blob produced on a DIFFERENT install (independent <ID>) → fixed vs derived.
        if (args.Contains("--lockpin-decrypt"))
        {
            int i = Array.IndexOf(args, "--lockpin-decrypt");
            string blob = i + 1 < args.Length ? args[i + 1] : "";
            string key = i + 2 < args.Length && !args[i + 2].StartsWith("--") ? args[i + 2] : "7b7fdf9d179643e0be4bea45c827b693";
            string seed = i + 3 < args.Length && !args[i + 3].StartsWith("--") ? args[i + 3] : "cf2976b6f11c459bab7a3f2acc1795f3";
            var clear = Data.LbSettingsCrypto.TryDecryptExplicit(blob, key, seed);
            Console.WriteLine($"[lockpin] blob = {blob}");
            Console.WriteLine($"[lockpin] key={key} seed={seed}");
            Console.WriteLine(clear == null
                ? "[lockpin] DECRYPT FAILED — wrong key/seed for this blob (or not a valid blob)."
                : $"[lockpin] CLEAR PIN = \"{clear}\"  → this key DOES decrypt the blob.");
            return clear == null ? 1 : 0;
        }
        // --model-dump: passive RE probe for LB's 3D box-model system (embedded .obj/.mtl cases, CaseType enum,
        // ModelSettings members, platform→model methods) → <Core>\model-dump.log. Metadata only. See ModelProbe.
        if (args.Contains("--model-dump"))
        {
            string lbRootMd = Path.GetFullPath(Path.Combine(coreDir, ".."));
            try { SetLaunchBoxCoreRootFolder(lbRootMd); } catch { }
            Diag.ModelProbe.Dump(lbRootMd);
            return 0;
        }
        // --model-defaults-extract [out.json]: freeze LB's per-platform 3D defaults to a JSON table.
        if (args.Contains("--model-defaults-extract"))
        {
            Diag.ModelProbe.DefaultsExtract(Path.GetFullPath(Path.Combine(coreDir, "..")), GetArg(args, "--model-defaults-extract"));
            return 0;
        }
        // --hunt-regions: locate the core's hard-coded prioritized-region static (see ModelProbe.HuntRegions).
        if (args.Contains("--hunt-regions"))
        {
            Diag.ModelProbe.HuntRegions();
            return 0;
        }
        // --model-export <outDir>: extract the embedded .obj/.mtl case models to disk (home-made reproduction).
        if (args.Contains("--model-export"))
        {
            Diag.ModelProbe.Export(GetArg(args, "--model-export") ?? Path.Combine(coreDir, "model-export"));
            return 0;
        }
        // --model-defaults [platform]: drive ModelSettings.GetDefaultSettings to dump the hardcoded per-platform
        // box-model defaults → <Core>\model-defaults.log. See ModelProbe.Defaults.
        if (args.Contains("--model-defaults"))
        {
            string lbRootMdf = Path.GetFullPath(Path.Combine(coreDir, ".."));
            try { SetLaunchBoxCoreRootFolder(lbRootMdf); } catch { }
            Diag.ModelProbe.Defaults(lbRootMdf, GetArg(args, "--model-defaults"));
            return 0;
        }
        // --model-spines: dump the JewelCaseSpines.resources entry names (Spine Style presets) → jewel-spines.log.
        if (args.Contains("--model-spines"))
        {
            string lbRs = Path.GetFullPath(Path.Combine(coreDir, ".."));
            try { SetLaunchBoxCoreRootFolder(lbRs); } catch { }
            Diag.ModelProbe.JewelSpines(lbRs);
            return 0;
        }
        // --edit-platform-render <platform> <outDir>: offscreen-render the Edit Platform sections to PNGs (visual
        // iteration; no UI shown). See Host/Platforms/EditPlatformRenderProbe.cs.
        if (args.Contains("--edit-platform-render"))
        {
            int i = Array.IndexOf(args, "--edit-platform-render");
            string plat = i + 1 < args.Length ? args[i + 1] : "";
            string outDir = i + 2 < args.Length ? args[i + 2] : Path.Combine(AppContext.BaseDirectory, "editplat-render");
            string lbR = Path.GetFullPath(Path.Combine(coreDir, ".."));
            try { SetLaunchBoxCoreRootFolder(lbR); } catch { }
            Platforms.EditPlatformRenderProbe.Render(lbR, plat, outDir);
            return 0;
        }
        // --model3d-probe <platform> <scrapeAs> <out.png>: PLAN A test — host LB's FlowModel + RedrawModel by
        // reflection and render its Viewport to a PNG. See Model3dProbe.
        if (args.Contains("--model3d-probe"))
        {
            int i = Array.IndexOf(args, "--model3d-probe");
            string plat = i + 1 < args.Length ? args[i + 1] : "Sony Playstation";
            string scr = i + 2 < args.Length ? args[i + 2] : "";
            string outP = i + 3 < args.Length ? args[i + 3] : Path.Combine(AppContext.BaseDirectory, "model3d-probe.png");
            if (i + 4 < args.Length) Diag.Model3dProbe.GameTitle = args[i + 4];
            string lbR4 = Path.GetFullPath(Path.Combine(coreDir, ".."));
            try { SetLaunchBoxCoreRootFolder(lbR4); } catch { }
            Diag.Model3dProbe.Run(lbR4, plat, scr, outP);
            return 0;
        }
        // --type-dump <FullOrSimpleTypeName>: ctors + props (get/SET) + methods of one core type.
        if (args.Contains("--type-dump"))
        {
            int i = Array.IndexOf(args, "--type-dump");
            string tn = i + 1 < args.Length ? args[i + 1] : "";
            string lbR3 = Path.GetFullPath(Path.Combine(coreDir, ".."));
            try { SetLaunchBoxCoreRootFolder(lbR3); } catch { }
            Diag.ModelProbe.TypeDump(lbR3, tn);
            return 0;
        }
        // --model-map: full CoverFlow member dump + assembly-wide ModelSettings/image→3D method hunt.
        if (args.Contains("--model-map"))
        {
            string lbRs2 = Path.GetFullPath(Path.Combine(coreDir, ".."));
            try { SetLaunchBoxCoreRootFolder(lbRs2); } catch { }
            Diag.ModelProbe.MapCoverFlow(lbRs2);
            return 0;
        }
        // --model-default-resolve <platform> [scrapeAs]: print ModelDefaults.TryGet (the runtime reflection
        // resolver of LB's hardcoded per-platform 3D defaults) — verifies the scrapeAs-driven preset matching.
        if (args.Contains("--model-default-resolve"))
        {
            int i = Array.IndexOf(args, "--model-default-resolve");
            string plat = i + 1 < args.Length ? args[i + 1] : "";
            string scr = i + 2 < args.Length ? args[i + 2] : "";
            string lbR = Path.GetFullPath(Path.Combine(coreDir, ".."));
            try { SetLaunchBoxCoreRootFolder(lbR); } catch { }
            var map = Platforms.ModelDefaults.TryGet(plat, scr);
            Console.WriteLine($"[model-def-resolve] ('{plat}', '{scr}') => " + (map == null ? "null (no default)" : ""));
            if (map != null) foreach (var kv in map) Console.WriteLine($"  {kv.Key} = {kv.Value}");
            return 0;
        }
        // --model3d-live <platform> <outDir>: host the live 3D preview panel in a visible form + screenshot.
        if (args.Contains("--model3d-live"))
        {
            int i = Array.IndexOf(args, "--model3d-live");
            string plat = i + 1 < args.Length ? args[i + 1] : "Sony Playstation";
            string outDir = i + 2 < args.Length ? args[i + 2] : Path.Combine(AppContext.BaseDirectory, "model3d-live");
            string lbRl = Path.GetFullPath(Path.Combine(coreDir, ".."));
            try { SetLaunchBoxCoreRootFolder(lbRl); } catch { }
            Platforms.EditPlatformRenderProbe.RenderLive(lbRl, plat, outDir);
            return 0;
        }
        // --edit-category-render <category> <outDir>: same offscreen render for the Edit Category window.
        if (args.Contains("--edit-category-render"))
        {
            int i = Array.IndexOf(args, "--edit-category-render");
            string cat = i + 1 < args.Length ? args[i + 1] : "";
            string outDir = i + 2 < args.Length ? args[i + 2] : Path.Combine(AppContext.BaseDirectory, "editcat-render");
            string lbR = Path.GetFullPath(Path.Combine(coreDir, ".."));
            try { SetLaunchBoxCoreRootFolder(lbR); } catch { }
            Platforms.EditPlatformRenderProbe.RenderCategory(lbR, cat, outDir);
            return 0;
        }
        // --edit-playlist-render [lbRoot] <playlist> <outDir>: all five sections + persistent Images panel.
        // lbRoot is optional when the executable already runs from LaunchBox\Core.
        if (args.Contains("--edit-playlist-render"))
        {
            int i = Array.IndexOf(args, "--edit-playlist-render");
            string lbR = Path.GetFullPath(Path.Combine(coreDir, ".."));
            int valueAt = i + 1;
            if (valueAt < args.Length && Directory.Exists(Path.Combine(args[valueAt], "Data")))
            {
                lbR = Path.GetFullPath(args[valueAt]);
                valueAt++;
            }
            string playlist = valueAt < args.Length ? args[valueAt] : "";
            string outDir = valueAt + 1 < args.Length ? args[valueAt + 1] : Path.Combine(AppContext.BaseDirectory, "editplaylist-render");
            try { SetLaunchBoxCoreRootFolder(lbR); } catch { }
            Platforms.EditPlatformRenderProbe.RenderPlaylist(lbR, playlist, outDir);
            return 0;
        }
        // --filter-selftest: build the advanced-search dialog + range slider with dummy facets, force handle
        // creation, and report — a headless catch for construction/layout exceptions (no user interaction).
        if (args.Contains("--filter-selftest"))
        {
            int rc = 0;
            var th = new Thread(() =>
            {
                try
                {
                    var facet = new[] { "Action", "RPG", "Puzzle" };
                    var hist = new System.Collections.Generic.List<Search.FilterCriteria> { new() { Fav = true, Genres = { "Action" } } };
                    using var dlg = new Search.FilterDialog(new Search.FilterCriteria(), facet, facet, facet, new[] { "Physical", "Digital" }, hist);
                    dlg.CreateControl();
                    int n = 0;
                    void Walk(System.Windows.Forms.Control c) { var _ = c.Handle; n++; foreach (System.Windows.Forms.Control ch in c.Controls) Walk(ch); }
                    Walk(dlg);
                    Console.WriteLine("[filter-selftest] dialog built OK (" + n + " controls)");
                }
                catch (Exception ex) { Console.WriteLine("[filter-selftest] FAILED: " + ex); rc = 1; }
            });
            th.SetApartmentState(ApartmentState.STA);
            th.Start(); th.Join();
            return rc;
        }
        // --medialayout-selftest: build the media-layout editor + round-trip the config, headless.
        if (args.Contains("--medialayout-selftest"))
        {
            int rc = 0;
            var th = new Thread(() =>
            {
                try
                {
                    var def = Media.MediaLayout.Default();
                    Console.WriteLine($"[medialayout-selftest] default: imm(list={def.ImmediateList},poster={def.ImmediatePoster}), postLoad={def.PostLoad.Count} entries, families={Media.MediaLayout.Families.Length}, exactTypes={Media.MediaLayout.ExactTypes().Length}");
                    using var p = new Media.MediaLayoutPanel();
                    p.CreateControl();
                    int n = 0; void Walk(System.Windows.Forms.Control c) { var _ = c.Handle; n++; foreach (System.Windows.Forms.Control ch in c.Controls) Walk(ch); }
                    Walk(p);
                    Console.WriteLine("[medialayout-selftest] editor built OK (" + n + " controls)");
                }
                catch (Exception ex) { Console.WriteLine("[medialayout-selftest] FAILED: " + ex); rc = 1; }
            });
            th.SetApartmentState(ApartmentState.STA);
            th.Start(); th.Join();
            return rc;
        }
        // --thumb-gen <imagePath>: generate the degraded thumbnail for one image and print the cache path —
        // headless test of the ThumbCache pipeline (Magick presence, cache dir layout).
        if (args.Contains("--thumb-gen"))
        {
            string src = GetArg(args, "--thumb-gen") ?? "";
            string lbRt = Path.GetFullPath(Path.Combine(coreDir, ".."));
            Media.MagickSupport.Init(lbRt);
            Media.ThumbCache.Init(lbRt);
            var outPath = Media.ThumbCache.GetOrCreate(src, keepAlpha: false);
            Console.WriteLine("[thumb-gen] src    = " + src);
            Console.WriteLine("[thumb-gen] result = " + (outPath ?? "NULL (generation failed — Magick missing from Core, or unreadable source)"));
            return outPath != null ? 0 : 1;
        }
        // --fbneo-hiscore <destFile>: extract the embedded FBNeo hiscore.dat to a path (test the embed/extract).
        if (args.Contains("--fbneo-hiscore"))
        {
            int i = Array.IndexOf(args, "--fbneo-hiscore");
            string dest = i + 1 < args.Length ? args[i + 1] : Path.Combine(AppContext.BaseDirectory, "fbneo-hiscore-test.dat");
            bool ok = Mame.FbneoHiscore.WriteTo(dest);
            Console.WriteLine($"[fbneo-hiscore] {(ok ? "OK" : "FAILED")} → {dest}");
            return ok ? 0 : 1;
        }
        // --mame-members: dump all methods of the core's MAME high-score + GamesDatabase types (no network),
        // to find the post-game "process/extract/upload high score" pipeline we can drive at game exit.
        if (args.Contains("--mame-members"))
        {
            string lbRootM = Path.GetFullPath(Path.Combine(coreDir, ".."));
            try { SetLaunchBoxCoreRootFolder(lbRootM); } catch { }
            Diag.MameProbe.Members(lbRootM);
            return 0;
        }
        // --mame-keyhook: the ORACLE — hook the core's cipher in-process, block the POST, trigger the encrypt,
        // and print the current leaderboard key. Nothing is uploaded. See Host/Diag/MameKeyHook.cs.
        if (args.Contains("--mame-keyhook"))
        {
            string lbRootK = Path.GetFullPath(Path.Combine(coreDir, ".."));
            try { SetLaunchBoxCoreRootFolder(lbRootK); } catch { }
            Diag.MameKeyHook.Run(lbRootK);
            return 0;
        }
        // --key-harvest: read ALL needed keys out of the core (MAME + settings/EmuMovies) → litebox\keys.report.txt
        // + keys.json. Nothing uploaded. See Host/Diag/KeyHarvest.cs.
        if (args.Contains("--key-harvest"))
        {
            string lbRootH = Path.GetFullPath(Path.Combine(coreDir, ".."));
            try { SetLaunchBoxCoreRootFolder(lbRootH); } catch { }
            Diag.KeyHarvest.Run(lbRootH);
            return 0;
        }

        // ── Real data: LaunchBox Platform XMLs (authoritative, no ExtendDB dep) ──
        IDataManager dm;
        GameStore store = null;
        string lbRoot = null;
        string platformsDir = GetArg(args, "--library")
            ?? Path.GetFullPath(Path.Combine(coreDir, "..", "Data", "Platforms"));
        if (Directory.Exists(platformsDir))
        {
            Console.WriteLine($"Loading library from {platformsDir} ...");
            var sw = Stopwatch.StartNew();
            store = GameStore.Load(platformsDir);
            sw.Stop();
            store.LogStats();
            Console.WriteLine($"Parsed XML in {sw.ElapsedMilliseconds} ms");
            bool cfgReadOnly = LiteBoxConfig.LoadForExe().ReadOnly;   // default true → never write to the XMLs
            store.ReadOnly = cfgReadOnly || InstanceGuard.AnotherInstanceRunning;
            if (InstanceGuard.AnotherInstanceRunning)
                Console.WriteLine("[store] another LiteBox instance is running → read-only enforced (in-memory; LiteBox.ini untouched)");
            Console.WriteLine($"[store] ReadOnly = {store.ReadOnly}");
            store.RecoverJournalOnLoad();   // apply any pending user-state (crash/kill or deferred-while-LB-up)
            Mem.Report("after store build");
            string dataDir = Path.GetFullPath(Path.Combine(platformsDir, ".."));     // ...\LB\Data
            lbRoot = Path.GetFullPath(Path.Combine(dataDir, ".."));                   // ...\LB
            string imagesRoot = Path.Combine(lbRoot, "Images");                       // ...\LB\Images
            LbApiHost.Host.Media.MediaResolver.Init(lbRoot);                          // media (IO + GameCache fast path)
            Data.ExtDbDownloader.ApplyPendingTodoIfAny();                             // finish a deferred extended-DB swap BEFORE anything opens it
            SetLaunchBoxCoreRootFolder(lbRoot);                                       // process-wide LB-root static the integration plugins read (save scans, …)
            LbApiHost.Host.Install.NativeInstaller.EnsureDeployed(lbRoot, refreshNatives);  // deploy embedded natives → ThirdParty (only-if-absent; a refresh pass on a version bump). Single owner of ThirdParty placement.
            LbApiHost.Host.Media.MagickSupport.Init(lbRoot);                          // point the native-lib search dir at ThirdParty\ExtendDB (already deployed above)
            LbApiHost.Host.Media.ThumbCache.Init(lbRoot);                             // shared degraded-thumb cache (LB\Plugins\ExtendDB\cache\thumbs)
            LbApiHost.Host.Media.PdfThumbnailer.Configure(lbRoot);                    // point the PDF renderer at ThirdParty\Pdfium\pdfium.dll (loaded lazily on first use)
            // Which LaunchBox are we running against → which Settings.xml keys can't safely live in
            // its XML (routed to LiteBox's own DB). Detect version, build the problem-key set, open
            // the options DB, then seed any renamed key from its pre-rename XML value (one-shot).
            Data.LbVersion.Detect(lbRoot);
            Data.ProblemKeys.Build();
            Data.LiteBoxOptionsDb.Open();
            Data.ProblemKeys.SeedRenamedFromXml(Path.Combine(dataDir, "Settings.xml"));
            // Guarantee every LiteBox-own gameplay GLOBAL default is present in LiteBox.ini with a
            // visible value (no hidden keys). Must run BEFORE anything resolves a launch
            // (PauseManager.Configure, DependencyCheck, first game start).
            Gameplay.GameplayDefaults.Seed(LiteBoxConfig.LoadForExe());
            // Same guarantee for every OTHER LiteBox-own global the ini template doesn't write (it only runs
            // on a fresh install, so options added later stayed invisible until first toggled).
            Options.GlobalDefaults.Seed(LiteBoxConfig.LoadForExe());
            // The LaunchBox settings <ID> is the key for encrypted values (EmuMovies password, …). LaunchBox
            // writes it on its very first run; a real install always has it. If it's missing, LaunchBox was never
            // launched here — tell the user and stop rather than guessing or minting a bogus id.
            if (!Data.LbSettingsCrypto.HasSettingsId)
            {
                System.Windows.Forms.MessageBox.Show(
                    "This LaunchBox install has no settings ID yet.\n\nPlease run LaunchBox at least once (then close it) before starting LiteBox.",
                    "LiteBox", System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Warning);
                Environment.Exit(1);
            }
            Data.LbKeys.LogSummary();   // one-line inventory of resolved keys/token (presence only, no secrets)
            dm = store.Count > 0 ? new HostDataManagerXml(store, dataDir, imagesRoot) : new HostDataManager(HostCatalog.BuildDummy());
        }
        else
        {
            Console.WriteLine($"Platforms dir not found ({platformsDir}) — using dummy catalog.");
            dm = new HostDataManager(HostCatalog.BuildDummy());
        }

        // Injection — PluginHelper exposes public setters for all 5 services.
        PluginHelper.DataManager = dm;
        // Media follow a game's title: the hook fires once a flush has durably written it.
        try { Media.GameMediaSync.Attach(store); } catch { }
        PluginHelper.StateManager = new HostStateManager();
        PluginHelper.BigBoxMainViewModel = new HostBigBoxMainViewModel();
        PluginHelper.LaunchBoxMainViewModel = new HostLaunchBoxMainViewModel();
        PluginHelper.RetroAchievementsApi = new DummyRetroAchievementsApi();
        Console.WriteLine($"PluginHelper wired. DataManager={dm.GetType().Name} games={dm.GetAllGames().Length} platforms={dm.GetAllPlatforms().Length} categories={dm.GetAllPlatformCategories().Length} emulators={dm.GetAllEmulators().Length} playlists={dm.GetAllPlaylists().Length}");
        Mem.Report("after wrappers+inject");

        // ── Plugins (config-driven; edited in Options → Plugins) ────────────
        // The enabled set lives in LiteBox.ini (EnabledPlugins=A,B,…). KEY ABSENT
        // (first run / not configured) → enable EVERY folder present under
        // <LB>\Plugins (ExtendDB + the base LaunchBox plugins). Changes apply on
        // the next start (plugins are loaded once, here, before the UI exists).
        string pluginsRoot = GetArg(args, "--plugins")
            ?? Path.GetFullPath(Path.Combine(coreDir, "..", "Plugins"));
        PluginsRoot = pluginsRoot;   // exposed to the Options "Plugins" section

        var pluginCfg = LiteBoxConfig.LoadForExe();
        var enabled = pluginCfg.GetEnabledPluginsOrNull();
        List<string> names = enabled ?? ListPluginFolders(pluginsRoot);

        // ExtendDB is integrated into LiteBox now → never load its plugin, whatever the enabled set says.
        if (IntegrateExtendDb)
        {
            int before = names.Count;
            names = names.Where(n => !IsExtendDb(n)).ToList();
            if (names.Count != before) Console.WriteLine("[loader] ExtendDB skipped (functionality integrated into LiteBox)");
        }

        Console.WriteLine($"Plugins root: {pluginsRoot}");
        Console.WriteLine($"Enabled plugins: [{string.Join(", ", names)}]"
            + (enabled == null ? "  (default: all present)" : ""));

        var pluginDirs = new List<string>();
        foreach (var nm in names)
        {
            var d = Path.Combine(pluginsRoot, nm);
            if (Directory.Exists(d)) pluginDirs.Add(d);
            else Console.WriteLine($"  ! enabled plugin folder not found: {d}");
        }

        // Pin OUR (bundled, net10) WPF assemblies BEFORE any plugin is LoadFrom'd. Some LaunchBox plugins
        // (e.g. "LaunchBox Reader") ship a loose .NET Framework WindowsBase.dll (v4.0.0.0) in their own
        // folder; a plugin loaded before WPF is present makes its LoadFrom context probe that folder and
        // load the 4.0.0.0 copy into the process, after which our WPF init dies with "could not load
        // System.Windows.Threading.DispatcherObject from WindowsBase 4.0.0.0". Touching a type from each
        // WPF assembly here loads the correct 10.0.0.0 copies first, so the plugin's ref rolls forward to
        // them and the stale 4.0.0.0 is never probed — mirroring how a real (WPF) LaunchBox boots.
        try
        {
            _ = typeof(System.Windows.Threading.DispatcherObject).Assembly;   // WindowsBase
            _ = typeof(System.Windows.Media.Brush).Assembly;                  // PresentationCore
            _ = typeof(System.Windows.Application).Assembly;                  // PresentationFramework
        }
        catch (Exception ex) { Console.WriteLine("[wpf] pin failed: " + ex.Message); }

        // Initialize the OBFUSCATED CORE assemblies' method-body decryptors BEFORE any plugin loads.
        // Each protected assembly has a <Module> initializer that installs a JIT hook after inspecting
        // the runtime; when a plugin's own detours are already installed (ExtendDB ships Harmony/MonoMod
        // patches), that inspection NREs (SeparatedUser.ValidateDividedAuthorizerDecryptor) and EVERY
        // later call into that assembly — including the emulator-integration plugins' GetSaves — throws
        // TypeInitializationException for the whole session (symptom: every save card shows the red
        // "file no longer exists" dot). LaunchBox never hits this because ITS protectors always
        // initialize first (LaunchBox.exe itself is protected); replicate that order by running each
        // core module constructor NOW, before plugins get a chance to hook anything.
        foreach (var an in new[] { "Unbroken", "Unbroken.LaunchBox", "Unbroken.LaunchBox.Windows",
                                   "Unbroken.LaunchBox.LocalDb", "Unbroken.LaunchBox.MediaEngine.WindowsClient" })
        {
            try
            {
                var asm = System.Reflection.Assembly.Load(an);
                System.Runtime.CompilerServices.RuntimeHelpers.RunModuleConstructor(asm.ManifestModule.ModuleHandle);
                Console.WriteLine($"[core] pre-initialized {an}");
            }
            catch (Exception ex) { Console.WriteLine($"[core] pre-init {an} failed: {ex.Message}"); }
        }

        var reg = PluginLoader.LoadFrom(pluginDirs);
        Console.WriteLine($"Loaded {reg.All.Count} plugin object(s): events={reg.SystemEvents.Count} sysmenu={reg.SystemMenus.Count} gamemenu={reg.GameMenus.Count} themeel={reg.ThemeElements.Count}");

        // Hands-free UI drivers (see the fields' doc): --edit-game/--edit-page/--edit-gamesaves/--options.
        // --play "<title|id>" launches the game right after the window shows — combined with
        // --drylaunch it prints the exact spawn command line without starting anything (the
        // hands-free way to audit a launch: DOSBox args, emulator command, working dir).
        AutoPlay = GetArg(args, "--play");
        DupCycle = GetArg(args, "--dup-cycle");
        AutoEditGame = GetArg(args, "--edit-game");
        AutoEditEmu = GetArg(args, "--edit-emu");
        AutoEditPage = GetArg(args, "--edit-page");
        string legacySaves = GetArg(args, "--edit-gamesaves");
        if (legacySaves != null) { AutoEditGame = legacySaves; AutoEditPage ??= "GameSaves"; }
        // --gencache [logos,fronts,shots,videos,docs]: drive the bulk cache generation hands-free —
        // opens the progress form pseudo-modally, verifies the block, minimizes (verifies the unblock),
        // waits for completion, prints [gencache] lines and exits. Default selection: fronts,shots.
        int genIx = Array.IndexOf(args, "--gencache");
        if (genIx >= 0)
            AutoGenCache = (genIx + 1 < args.Length && !args[genIx + 1].StartsWith("--", StringComparison.Ordinal))
                ? args[genIx + 1] : "fronts,shots";
        int optIx = Array.IndexOf(args, "--options");
        if (optIx >= 0)
            AutoOptions = (optIx + 1 < args.Length && !args[optIx + 1].StartsWith("--", StringComparison.Ordinal))
                ? args[optIx + 1] : "";

        // Launch lifecycle: drop/reload the optional tier + notify launching plugins.
        HostLaunch.DryRun = args.Contains("--drylaunch");
        HostLaunch.Configure(reg, store, lbRoot);
        Pause.PauseManager.Configure(LiteBoxConfig.LoadForExe(), lbRoot);   // pause screens (hotkey + suspend + AHK)
        Gameplay.GameScreens.Configure(lbRoot);    // startup ("NOW LOADING…") + end ("GAME OVER") screens
        Gameplay.ScreenCapture.Configure(lbRoot);  // screenshot hotkey
        EmuPlugins.Configure(reg);   // emulator-integration plugins (RetroArch/Dolphin/… DLLs)
        // Warm up the integration plugins ONCE, on THIS thread: the first call into a plugin
        // JIT-compiles obfuscated core code, which initializes the core's method-body decryptor.
        // Doing it here — serially, before any UI or scan thread exists — makes that fragile init
        // deterministic instead of racing (see EmuPlugins.CallGate). Also pre-fills the
        // plugin-per-emulator cache the UI reads on every selection.
        try { foreach (var e in dm.GetAllEmulators() ?? Array.Empty<IEmulator>()) EmuPlugins.ForEmulator(e); }
        catch (Exception ex) { Console.WriteLine("[emuplugin] warmup failed: " + ex.Message); }
        DependencyCheck.Configure(LiteBoxConfig.LoadForExe(), lbRoot);   // pre-launch bios/dependency check
        Modules.LbModules.LogState();   // boot recap: "[modules] ON: … | OFF: …"

        // Extended-DB auto-update (Base module): the same keep-fresh behaviour the plugin has at boot, but in
        // the background — check the release, and download/install silently when missing or out of date.
        // Opt-out via LiteBox.ini [Base] AutoUpdateDb=false. Progress goes to the [extdb] log only.
        if (Modules.LbModules.On(Modules.LbModule.Base)
            && LiteBoxConfig.LoadForExe().GetSecBool("Base", "AutoUpdateDb", true))
        {
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    var (upd, remote, local, bytes) = await Data.ExtDbDownloader.CheckAsync(System.Threading.CancellationToken.None);
                    if (upd)
                    {
                        Diag.LbLog.Info("extdb", $"update {local ?? "none"} -> {remote} ({bytes / (1 << 20)} MB), downloading...");
                        // ExtendDB-parity: a real download shows the update window. Start the SHARED operation
                        // first (headless-safe), then try to attach the viewer once the UI is up; if no UI ever
                        // comes (headless), the download simply completes in the background.
                        var op = Data.ExtDbDownloader.RunSharedAsync();
                        _ = System.Threading.Tasks.Task.Run(async () =>
                        {
                            for (int i = 0; i < 20 && !op.IsCompleted; i++)
                            {
                                try { UiThread.Invoke(() => Options.ExtDbUpdateWindow.ShowOrFocus()); return; }
                                catch { await System.Threading.Tasks.Task.Delay(1000); }
                            }
                        });
                        await op;
                    }
                    else
                    {
                        Diag.LbLog.Info("extdb", $"up to date (local {local ?? "none"})");
                        await Data.ExtDbDownloader.RunSharedAsync();   // silent no-op / legacy adoption when needed
                    }
                }
                catch (Exception ex) { Diag.LbLog.Warn("extdb", "auto-update: " + ex.Message); }
                // Keep the precomputed defaultOverview in step (fresh install / stale signature → rebuild;
                // valid → no-op). Runs after the update so a new DB gets its column immediately.
                Data.OverviewCache.RunSyncIfNeeded();
            });
        }
        else if (Modules.LbModules.On(Modules.LbModule.Base))
            Data.OverviewCache.RunSyncIfNeeded();   // auto-update off → still keep the overview cache valid


        EventBus.FirePluginInitialized(reg);

        // Let ExtendDB's Similar-Games viewer jump to an owned game in-host (instead of
        // opening a web page). No-op if ExtendDB is absent / too old. The callback finds
        // the MainWindow lazily, so registering here (before the window exists) is fine.
        Media.HostGameNavBridge.Register();

        // ── Host GameCache (backported) ─────────────────────────────────────
        // Build & use our own in-memory media cache ONLY when ExtendDB isn't providing one
        // (ExtendDB's own GameCache is preferred when the plugin is loaded). Everything's native
        // is deployed the same way as ExtendDB so the fast scan works standalone.
        try
        {
            var gcCfg = LiteBoxConfig.LoadForExe();
            LbApiHost.Host.Gc.HostGameCache.Enabled =
                gcCfg.UseGameCache && lbRoot != null && !LbApiHost.Host.Media.GameCacheBridge.ExtendDbPresent;
            LbApiHost.Host.Gc.HostGameCache.UnloadDuringGame = gcCfg.UnloadGameCacheDuringGame;
            if (LbApiHost.Host.Gc.HostGameCache.Enabled)
            {
                LbApiHost.Host.Media.EverythingSupport.Init(lbRoot);   // deploy Everything64.dll if absent
                Console.WriteLine("[gamecache] ExtendDB absent → building host GameCache");
                LbApiHost.Host.Gc.HostGameCache.Build();               // async; flips IsGlobalReady when done
            }
            else if (gcCfg.UseGameCache)
                // Trois raisons distinctes de ne pas construire le cache, et ce message n'en nommait
                // qu'une : il annoncait "ExtendDB present" meme quand la bibliotheque etait
                // introuvable. Diagnostic trompeur, et suffisant pour faire croire a une integration
                // qui n'existe plus.
                Console.WriteLine(LbApiHost.Host.Media.GameCacheBridge.ExtendDbPresent
                    ? "[gamecache] ExtendDB present → using ExtendDB's GameCache"
                    : $"[gamecache] cache non construit : lbRoot={(lbRoot ?? "(null)")}");
        }
        catch (Exception ex) { Console.WriteLine("[gamecache] init error: " + ex.Message); }

        // ── Embedded web server (Web module) ────────────────────────────────
        // Deploy any bundled web assets, then start the local HTTP server (LiteBox Web / BigBox Web /
        // database site). Only when the Web module is on; never started otherwise. Non-fatal.
        try
        {
            if (Modules.LbModules.On(Modules.LbModule.Web))
            {
                Web.WebAssets.EnsureDeployed();
                int webPort = int.TryParse(LiteBoxConfig.LoadForExe().GetSec("Web", "Port"), out var wp) ? wp : 8080;
                Web.EmbeddedWebServer.Start(webPort);   // logs the listen URL itself
            }
        }
        catch (Exception ex) { Console.WriteLine("[web] start failed: " + ex.Message); }

        for (int i = 0; i < reg.SystemMenus.Count; i++)
        {
            try
            {
                var m = reg.SystemMenus[i];
                Console.WriteLine($"  [sysmenu #{i}] \"{m.Caption}\"  LB={m.ShowInLaunchBox} BB={m.ShowInBigBox}");
            }
            catch (Exception ex) { Console.WriteLine($"  [sysmenu #{i}] caption threw: {ex.Message}"); }
        }

        Mem.Report("after plugin init");

        // ── Headless paths (diagnostics / automation) ───────────────────────
        if (args.Contains("--headless"))
        {
            // Let ExtendDB's async GameCache build settle, then measure the delta.
            Thread.Sleep(8000);
            Mem.Collect();
            Mem.Report("after settle + GC");
            store?.LogStats();
            if (args.Contains("--gcdump")) LbApiHost.Host.Diag.GameCacheProbe.Dump();

            if (args.Contains("--drop") && store != null)
            {
                store.DropOptional();
                Mem.Report("after DropOptional(Notes)");
                store.LogStats();
            }

            if (args.Contains("--mediatest"))
            {
                int shown = 0, scanned = 0;
                foreach (var g in PluginHelper.DataManager.GetAllGames())
                {
                    scanned++;
                    string front = g.FrontImagePath, shot = g.ScreenshotImagePath, vid = g.GetVideoPath(false);
                    if (string.IsNullOrEmpty(front) && string.IsNullOrEmpty(shot) && string.IsNullOrEmpty(vid)) continue;
                    Console.WriteLine($"[mediatest] \"{g.Title}\" [{g.Platform}]");
                    if (!string.IsNullOrEmpty(front)) Console.WriteLine($"    front: {front}");
                    if (!string.IsNullOrEmpty(shot)) Console.WriteLine($"    shot : {shot}");
                    if (!string.IsNullOrEmpty(vid)) Console.WriteLine($"    video: {vid}");
                    if (++shown >= 8) break;
                }
                Console.WriteLine($"[mediatest] scanned {scanned} game(s), {shown} with media shown");
            }

            // --thumbtest: replay the Generate-Image-Cache pipeline headless and DIAGNOSE the failures —
            // per-source result (empty / file missing / generated / FAILED) with the first failing paths.
            if (args.Contains("--thumbtest"))
            {
                Media.ThumbCache.Init(Path.GetFullPath(Path.Combine(coreDir, "..")));
                // Direct Magick smoke test with the REAL exception (ThumbCache swallows it).
                try
                {
                    var probeImg = Directory.EnumerateFiles(Path.Combine(coreDir, "..", "Images"), "*.png", SearchOption.AllDirectories).FirstOrDefault();
                    if (probeImg != null) { using var mimg = new ImageMagick.MagickImage(probeImg); Console.WriteLine($"[thumbtest] magick smoke OK: {mimg.Width}x{mimg.Height} ({probeImg})"); }
                }
                catch (Exception mex) { Console.WriteLine("[thumbtest] MAGICK SMOKE FAILED: " + mex); }
                int okC = 0, emptyC = 0, missC = 0, failC = 0, shownFails = 0;
                var games2 = PluginHelper.DataManager.GetAllGames();
                Console.WriteLine($"[thumbtest] {games2.Length} game(s)");
                foreach (var g in games2)
                {
                    var srcs = MainWindow.ResolveCacheSources(g);
                    if (srcs == null) continue;
                    for (int si = 0; si < 3; si++)
                    {
                        string src = srcs[si];
                        if (string.IsNullOrEmpty(src)) { emptyC++; continue; }
                        if (!File.Exists(src)) { missC++; if (shownFails < 5) { Console.WriteLine($"[thumbtest] MISSING src ({(si == 0 ? "logo" : si == 1 ? "box" : "shot")}): {src}"); shownFails++; } continue; }
                        var r = Media.ThumbCache.GetOrCreate(src, Media.ThumbCache.DefaultMaxDim, keepAlpha: si == 0);
                        if (r != null) okC++;
                        else { failC++; if (shownFails < 15) { Console.WriteLine($"[thumbtest] GEN FAILED ({(si == 0 ? "logo/webp" : "jpeg")}): {src}"); shownFails++; } }
                    }
                }
                Console.WriteLine($"[thumbtest] generated/hit={okC}  empty-src={emptyC}  file-missing={missC}  gen-FAILED={failC}");
            }

            if (args.Contains("--apitest"))
            {
                var dm2 = PluginHelper.DataManager;
                var cats = dm2.GetAllPlatformCategories() ?? Array.Empty<IPlatformCategory>();
                if (cats.Length > 0)
                {
                    var n = cats[0].Name;
                    var c = dm2.GetPlatformCategoryByName(n);
                    Console.WriteLine($"[apitest] GetPlatformCategoryByName(\"{n}\") -> {(c != null ? "OK: " + c.Name : "NULL")}");
                }
                var plat = dm2.GetAllPlatforms().FirstOrDefault(p => p.GetAllGames(true, true).Length > 0);
                if (plat != null)
                {
                    int all = plat.GetAllGames(true, true).Length;
                    int vis = plat.GetAllGames(false, false).Length;
                    int withFront = plat.GetGameCount(true, true, false, true, false, false, false);
                    Console.WriteLine($"[apitest] platform \"{plat.Name}\": all={all} noHide/noBroken={vis} withBoxFront={withFront}");
                }
                foreach (var g in dm2.GetAllGames())
                {
                    var imgs = g.GetAllImagesWithDetails();
                    if (imgs.Length > 0)
                    {
                        Console.WriteLine($"[apitest] GetAllImagesWithDetails(\"{g.Title}\") = {imgs.Length} image(s):");
                        foreach (var d in imgs.Take(5)) Console.WriteLine($"    [{d.ImageType}] region='{d.Region}' -> {d.FilePath}");
                        break;
                    }
                }
                // extended fields: dump the first game + any DosBox/ScummVM game found
                var g0 = dm2.GetAllGames().FirstOrDefault();
                if (g0 != null)
                    Console.WriteLine($"[apitest] ext \"{g0.Title}\": CommandLine='{g0.CommandLine}' Series='{g0.Series}' Source='{g0.Source}' ReleaseType='{g0.ReleaseType}' Devs=[{string.Join(",", g0.Developers)}] Genres=[{string.Join(",", g0.Genres)}] UseDosBox={g0.UseDosBox} UseScummVm={g0.UseScummVm} DateModified={g0.DateModified:yyyy-MM-dd}");
                int nDos = 0, nScumm = 0;
                IGame dosEx = null, scummEx = null;
                foreach (var g in dm2.GetAllGames())
                {
                    if (g.UseDosBox) { nDos++; dosEx ??= g; }
                    if (g.UseScummVm) { nScumm++; scummEx ??= g; }
                }
                Console.WriteLine($"[apitest] UseDosBox games={nDos} UseScummVm games={nScumm}");
                // quick-wins: playlist filters, platform images, custom fields
                var apl = dm2.GetAllPlaylists()?.FirstOrDefault(p => p.AutoPopulate);
                if (apl != null) Console.WriteLine($"[apitest] playlist \"{apl.Name}\" filters={apl.GetAllPlaylistFilters().Length}");
                var aplat = dm2.GetPlatformByName("MS-DOS") ?? dm2.GetAllPlatforms().FirstOrDefault();
                if (aplat != null) Console.WriteLine($"[apitest] platform \"{aplat.Name}\" banner='{aplat.BannerImagePath}' clearLogo='{aplat.ClearLogoImagePath}' bg='{aplat.BackgroundImagePath}'");
                foreach (var gg in dm2.GetAllGames())
                {
                    var cf = gg.GetAllCustomFields();
                    if (cf.Length > 0) { Console.WriteLine($"[apitest] customFields \"{gg.Title}\": " + string.Join(", ", cf.Select(c => $"{c.Name}={c.Value}"))); break; }
                }

                if (dosEx != null) Console.WriteLine($"[apitest] DosBox ex: \"{dosEx.Title}\" cfg='{dosEx.DosBoxConfigurationPath}' cmd='{dosEx.CommandLine}'");
                if (scummEx != null) Console.WriteLine($"[apitest] ScummVM ex: \"{scummEx.Title}\" type='{scummEx.ScummVmGameType}' data='{scummEx.ScummVmGameDataFolderPath}'");

                // In DryRun, exercise the DosBox launch + Configure paths to print the commands.
                if (HostLaunch.DryRun && dosEx != null)
                {
                    Console.WriteLine("[apitest] dry-launching DosBox game to show the built command:");
                    PluginHelper.LaunchBoxMainViewModel.PlayGame(dosEx, null, null, null);
                    Thread.Sleep(3200);
                    if (!string.IsNullOrEmpty(dosEx.ConfigurationPath))
                    {
                        Console.WriteLine("[apitest] dry-Configure to show the config command:");
                        dosEx.Configure();
                        Thread.Sleep(1500);
                    }
                }
            }

            if (args.Contains("--playlists"))
            {
                foreach (var pl in PluginHelper.DataManager.GetAllPlaylists() ?? Array.Empty<IPlaylist>())
                {
                    try { Console.WriteLine($"[playlist] \"{pl.Name}\" autopop={pl.AutoPopulate} games={pl.GetAllGames(false).Length}"); }
                    catch (Exception ex) { Console.WriteLine($"[playlist] \"{pl?.Name}\" error: {ex.Message}"); }
                }
            }

            string menuArg = GetArg(args, "--menu");
            if (menuArg != null && int.TryParse(menuArg, out int mi) && mi >= 0 && mi < reg.SystemMenus.Count)
            {
                UiThread.Start();
                UiThread.Invoke(() =>
                {
                    try { reg.SystemMenus[mi].OnSelected(); }
                    catch (Exception ex) { Console.WriteLine("OnSelected threw: " + ex); }
                });
            }
            string installEmu = GetArg(args, "--install-emu");
            if (!string.IsNullOrWhiteSpace(installEmu))
            {
                string root = Media.MediaResolver.LbRoot ?? AppContext.BaseDirectory;
                string logPath = System.IO.Path.Combine(root, "EmuInstall.LiteBox.log");
                try { System.IO.File.Delete(logPath); } catch { }
                void EL(string s) { Console.WriteLine(s); try { System.IO.File.AppendAllText(logPath, s + "\r\n"); } catch { } }
                EL($"==== EmuInstall '{installEmu}' — {DateTime.Now:yyyy-MM-dd HH:mm:ss} ====");
                // The arg is the emulator NAME (RetroArch, ScummVM…), not a platform — match by name first.
                var plugin = EmuInstall.FindPluginByName(installEmu) ?? EmuInstall.FindPlugin(installEmu);
                if (plugin == null) EL($"[emuinstall] NO integration plugin named/supporting '{installEmu}' (loaded emu plugins: {EmuPlugins.All.Count})");
                else
                {
                    EL($"[emuinstall] plugin = {plugin.GetType().FullName} (EmulatorName='{plugin.EmulatorName}')");
                    // Empty platform = general install (the plugin uses its own LocalDb platform set); passing the
                    // emulator name as a platform would make RetroArch add a bogus "RetroArch" EmulatorPlatform.
                    var (ok, message, id) = EmuInstall.Install(plugin, "",
                        progress: (m, f) => Console.WriteLine($"  … {m} {(int)(f * 100)}%"), cancel: null, log: EL);
                    EL($"[emuinstall] RESULT ok={ok} id={id ?? "-"} :: {message}");
                }
                EL("==== DONE ====");
            }
            if (args.Contains("--play"))
            {
                // Prefer a game that resolves an emulator (more representative); else the first.
                var all = PluginHelper.DataManager.GetAllGames();
                var g = all.FirstOrDefault(x => PluginHelper.DataManager.GetEmulatorById(x.EmulatorId) != null)
                        ?? all.FirstOrDefault();
                var emu = g != null ? PluginHelper.DataManager.GetEmulatorById(g.EmulatorId) : null;
                UiThread.Start();
                UiThread.Invoke(() => PluginHelper.LaunchBoxMainViewModel.PlayGame(g, null, emu, null));
                if (HostLaunch.DryRun) Thread.Sleep(1500); // let the launch worker log Drop→…→Reload
            }
            if (args.Contains("--loop"))
            {
                Console.WriteLine("Headless loop alive — Ctrl+C to exit.");
                Thread.Sleep(Timeout.Infinite);
            }
            return 0;
        }

        // ── Default: GUI (simple menu of plugin options + blank area) ───────
        Console.WriteLine("Launching GUI (close the window to exit). Web server keeps running.");
        // LB-parity: start the LaunchBox-flagged Startup Applications (LiteBox
        // plays the LaunchBox role). Non-fatal; skips already-running singles.
        try
        {
            if (dm is Data.HostDataManagerXml hdmBoot)
                StartupApps.LaunchAll(hdmBoot.LbSettings, Media.MediaResolver.LbRoot ?? "");
        }
        catch (Exception ex) { Console.WriteLine("[startupapps] " + ex.Message); }
        var ui = new Thread(() =>
        {
            try
            {
                Application.SetHighDpiMode(HighDpiMode.SystemAware);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
#pragma warning disable WFO5001 // experimental .NET 9 dark mode (title bar + scrollbars)
                try { Application.SetColorMode(SystemColorMode.Dark); } catch { }
#pragma warning restore WFO5001
                // Provide a WPF Application on this (STA) GUI thread so plugins that marshal work via
                // System.Windows.Application.Current.Dispatcher work — notably ExtendDB's web GameLauncher,
                // which BeginInvokes PlayGame onto that dispatcher (else: "no dispatcher"). We don't call
                // its Run(); the WinForms message loop pumps the WPF Dispatcher queue (same-thread interop).
                try { if (System.Windows.Application.Current == null) _ = new System.Windows.Application(); } catch (Exception ex) { Console.WriteLine("[gui] WPF Application init: " + ex.Message); }
                UiKit.LiteBoxTheme.Load(LiteBoxConfig.LoadForExe());   // apply saved color overrides BEFORE any window copies the palette
                Application.Run(new MainWindow(reg, dm));
            }
            catch (Exception ex) { Console.WriteLine("[gui] " + ex); }
        })
        { Name = "LbApiHost-GUI" };
        ui.SetApartmentState(ApartmentState.STA);
        ui.Start();
        ui.Join();
        // GUI closed → flush pending user-state to the XMLs if LaunchBox/BigBox aren't running
        // (else the journal is kept and applied next time it's safe).
        try { store?.FlushJournalIfSafe(); } catch { }
        return 0;
    }


    private static string GetArg(string[] args, string flag)
    {
        int i = Array.IndexOf(args, flag);
        return (i >= 0 && i + 1 < args.Length) ? args[i + 1] : null;
    }

    // LaunchBox's core keeps a process-wide LB-root static — Unbroken.LaunchBox.NamingHelper.RootFolder
    // (public auto-property) — that LaunchBox.exe sets at boot and the emulator-integration plugins
    // read to rebase relative paths: the RetroArch plugin resolves retroarch.cfg's ":\saves" prefix via
    // Path.GetFullPath(emulator.ApplicationPath, NamingHelper.RootFolder). Left unset under LiteBox,
    // that call throws inside the plugin's try/catch → GetSaves silently finds nothing. Set by
    // reflection: the obfuscated core assembly (resolved from LB\Core) is not compile-referenced.
    internal static void SetLaunchBoxCoreRootFolder(string lbRoot)
    {
        try
        {
            var t = Type.GetType("Unbroken.LaunchBox.NamingHelper, Unbroken.LaunchBox");
            var p = t?.GetProperty("RootFolder", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (p?.SetMethod == null) { Console.WriteLine("[boot] NamingHelper.RootFolder not found/settable — plugin save scans may miss ':\\'-relative dirs"); return; }
            p.SetValue(null, lbRoot);
            Console.WriteLine($"[boot] NamingHelper.RootFolder = {p.GetValue(null)}");
        }
        catch (Exception ex)
        {
            Console.WriteLine("[boot] NamingHelper.RootFolder init failed: " + ex.Message);
        }
    }
}
