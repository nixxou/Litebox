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
    public static int Run(string[] args)
    {
        string coreDir = AppContext.BaseDirectory;
        Mem.Report("startup");

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
            store.ReadOnly = LiteBoxConfig.LoadForExe().ReadOnly;   // default true → never write to the XMLs
            Console.WriteLine($"[store] ReadOnly = {store.ReadOnly}");
            store.RecoverJournalOnLoad();   // apply any pending user-state (crash/kill or deferred-while-LB-up)
            Mem.Report("after store build");
            string dataDir = Path.GetFullPath(Path.Combine(platformsDir, ".."));     // ...\LB\Data
            lbRoot = Path.GetFullPath(Path.Combine(dataDir, ".."));                   // ...\LB
            string imagesRoot = Path.Combine(lbRoot, "Images");                       // ...\LB\Images
            LbApiHost.Host.Media.MediaResolver.Init(lbRoot);                          // media (IO + GameCache fast path)
            LbApiHost.Host.Media.MagickSupport.Init(lbRoot);                          // deploy native ImageMagick (like ExtendDB) before plugins
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

        // ── Plugins (whitelist-driven) ──────────────────────────────────────
        string pluginsRoot = GetArg(args, "--plugins")
            ?? Path.GetFullPath(Path.Combine(coreDir, "..", "Plugins"));
        string whitelistPath = Path.Combine(coreDir, "whitelist.txt");

        // Default whitelist (ExtendDB) on first run — created only if absent, so user edits survive.
        if (!File.Exists(whitelistPath))
        {
            try
            {
                File.WriteAllText(whitelistPath,
                    "# LiteBox plugin whitelist — one plugin folder name per line (subfolders of <LB>\\Plugins)." + Environment.NewLine +
                    "# Lines starting with # or ; are ignored." + Environment.NewLine +
                    "ExtendDB" + Environment.NewLine);
            }
            catch { }
        }

        var names = ReadWhitelist(whitelistPath);
        Console.WriteLine($"Whitelist ({whitelistPath}): [{string.Join(", ", names)}]");
        Console.WriteLine($"Plugins root: {pluginsRoot}");

        var pluginDirs = new List<string>();
        foreach (var nm in names)
        {
            var d = Path.Combine(pluginsRoot, nm);
            if (Directory.Exists(d)) pluginDirs.Add(d);
            else Console.WriteLine($"  ! whitelisted plugin folder not found: {d}");
        }

        var reg = PluginLoader.LoadFrom(pluginDirs);
        Console.WriteLine($"Loaded {reg.All.Count} plugin object(s): events={reg.SystemEvents.Count} sysmenu={reg.SystemMenus.Count} gamemenu={reg.GameMenus.Count} themeel={reg.ThemeElements.Count}");

        // Launch lifecycle: drop/reload the optional tier + notify launching plugins.
        HostLaunch.DryRun = args.Contains("--drylaunch");
        HostLaunch.Configure(reg, store, lbRoot);

        EventBus.FirePluginInitialized(reg);

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

    private static List<string> ReadWhitelist(string path)
    {
        var list = new List<string>();
        if (!File.Exists(path))
        {
            Console.WriteLine($"  ! whitelist.txt not found at {path} — no plugins will be activated.");
            return list;
        }
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
            list.Add(line);
        }
        return list;
    }

    private static string GetArg(string[] args, string flag)
    {
        int i = Array.IndexOf(args, flag);
        return (i >= 0 && i + 1 < args.Length) ? args[i + 1] : null;
    }
}
