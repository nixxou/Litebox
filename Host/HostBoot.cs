// Host boot: wire dummy services into PluginHelper, load the WHITELISTED plugins
// (whitelist.txt next to the exe), fire PluginInitialized, then show the GUI
// (a simple menu of the plugins' system-menu items + a blank area).
//
//   --host                         GUI (default). Plugins from whitelist.txt.
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
            SetLaunchBoxCoreRootFolder(lbRoot);                                       // process-wide LB-root static the integration plugins read (save scans, …)
            LbApiHost.Host.Install.NativeInstaller.EnsureDeployed(lbRoot, refreshNatives);  // deploy embedded natives → ThirdParty (only-if-absent; a refresh pass on a version bump). Single owner of ThirdParty placement.
            LbApiHost.Host.Media.MagickSupport.Init(lbRoot);                          // point the native-lib search dir at ThirdParty\ExtendDB (already deployed above)
            LbApiHost.Host.Media.ThumbCache.Init(lbRoot);                             // shared degraded-thumb cache (LB\Plugins\ExtendDB\cache\thumbs)
            dm = store.Count > 0 ? new HostDataManagerXml(store, dataDir, imagesRoot) : new HostDataManager(HostCatalog.BuildDummy());
        }
        else
        {
            Console.WriteLine($"Platforms dir not found ({platformsDir}) — using dummy catalog.");
            dm = new HostDataManager(HostCatalog.BuildDummy());
        }

        // Injection — PluginHelper exposes public setters for all 5 services.
        PluginHelper.DataManager = dm;
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

        // Sweep obsolete leftovers from OLDER LiteBox versions (pre-litebox\ Core-root config/journal/caches,
        // old launcher markers, whitelist.txt, copied Magick DLLs, loose .api payload) so an upgrade or a
        // zip-extract-over-an-old-install self-cleans. Idempotent; never touches current data. See LegacyCleanup.
        try { LbApiHost.Host.Install.LegacyCleanup.SweepObsolete(Path.GetFullPath(Path.Combine(coreDir, ".."))); } catch { }

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

        var reg = PluginLoader.LoadFrom(pluginDirs);
        Console.WriteLine($"Loaded {reg.All.Count} plugin object(s): events={reg.SystemEvents.Count} sysmenu={reg.SystemMenus.Count} gamemenu={reg.GameMenus.Count} themeel={reg.ThemeElements.Count}");

        // Launch lifecycle: drop/reload the optional tier + notify launching plugins.
        HostLaunch.DryRun = args.Contains("--drylaunch");
        HostLaunch.Configure(reg, store, lbRoot);
        Pause.PauseManager.Configure(LiteBoxConfig.LoadForExe(), lbRoot);   // pause screens (hotkey + suspend + AHK)
        Gameplay.GameScreens.Configure(lbRoot);    // startup ("NOW LOADING…") + end ("GAME OVER") screens
        Gameplay.ScreenCapture.Configure(lbRoot);  // screenshot hotkey
        EmuPlugins.Configure(reg);   // emulator-integration plugins (RetroArch/Dolphin/… DLLs)
        DependencyCheck.Configure(LiteBoxConfig.LoadForExe(), lbRoot);   // pre-launch bios/dependency check

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
                Console.WriteLine("[gamecache] ExtendDB present → using ExtendDB's GameCache");
        }
        catch (Exception ex) { Console.WriteLine("[gamecache] init error: " + ex.Message); }

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
