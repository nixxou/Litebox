// Hand-written specializations of the generated Dummy* classes — the few
// members that need real (dummy) behavior: the fake catalog wiring and the
// MessageBox "launch". Everything else stays inherited from the generated stub.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Data;
using LbApiHost.Generated;
using LbApiHost.Host.Data;

namespace LbApiHost.Host;

/// <summary>A platform that actually returns its games.</summary>
internal sealed class HostPlatform : DummyPlatform
{
    public List<IGame> GamesList { get; } = new();

    public override IGame[] GetAllGames(bool includeHidden, bool includeBroken) => GamesList.ToArray();
    public override int GetGameCount(bool includeHidden, bool includeBroken) => GamesList.Count;
    public override bool HasGames(bool includeHidden, bool includeBroken) => GamesList.Count > 0;
}

/// <summary>DataManager backed by the in-memory dummy catalog.</summary>
internal sealed class HostDataManager : DummyDataManager
{
    private readonly HostCatalog _cat;
    public HostDataManager(HostCatalog cat) { _cat = cat; }

    public override IGame[] GetAllGames() => _cat.Games.ToArray();
    public override IPlatform[] GetAllPlatforms() => _cat.Platforms.ToArray();
    public override IEmulator[] GetAllEmulators() => _cat.Emulators.ToArray();

    public override IGame GetGameById(string id) => _cat.Games.FirstOrDefault(g => g.Id == id);
    public override IEmulator GetEmulatorById(string id) => _cat.Emulators.FirstOrDefault(e => e.Id == id);
    public override IPlatform GetPlatformByName(string name)
        => _cat.Platforms.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    public override IList<IPlatform> GetRootPlatformsCategoriesPlaylists() => _cat.Platforms.ToList();

    public override IGame AddNewGame(string title)
    {
        var g = new DummyGame { Id = Guid.NewGuid().ToString(), Title = title };
        _cat.Games.Add(g);
        return g;
    }

    public override void Save(bool wait) => Console.WriteLine($"[HostDataManager] Save(wait={wait}) — dummy no-op");
    public override void ForceReload() => Console.WriteLine("[HostDataManager] ForceReload() — dummy no-op");
}

/// <summary>StateManager with desktop-ish defaults.</summary>
internal sealed class HostStateManager : DummyStateManager
{
    public HostStateManager()
    {
        IsPremium = true;     // generated auto-props, settable
        IsBigBox = false;
        IsBigBoxLocked = false;
    }

    public override IPlatform GetSelectedPlatform()
        => Unbroken.LaunchBox.Plugins.PluginHelper.DataManager?.GetAllPlatforms()?.FirstOrDefault();

    /// <summary>Set by the GUI so plugins can read the currently-selected games.</summary>
    public static Func<IGame[]> SelectedGamesProvider;

    public override IGame[] GetAllSelectedGames()
    {
        try { return SelectedGamesProvider?.Invoke() ?? Array.Empty<IGame>(); }
        catch { return Array.Empty<IGame>(); }
    }
}

/// <summary>BigBox view-model: launch routes through HostLaunch.</summary>
internal sealed class HostBigBoxMainViewModel : DummyBigBoxMainViewModel
{
    public override void PlayGame(IGame game, IAdditionalApplication app, IEmulator emulator, string overrideCommandLine)
        => HostLaunch.Launch("BigBox", game, app, emulator, overrideCommandLine);
}

/// <summary>LaunchBox view-model: launch routes through HostLaunch.</summary>
internal sealed class HostLaunchBoxMainViewModel : DummyLaunchBoxMainViewModel
{
    public override void PlayGame(IGame game, IAdditionalApplication app, IEmulator emulator, string overrideCommandLine)
        => HostLaunch.Launch("LaunchBox", game, app, emulator, overrideCommandLine);
}

/// <summary>
/// The launch lifecycle, kept close to LaunchBox's: notify IGameLaunchingPlugin
/// plugins, FREE the optional data tier (the "free RAM at launch" feature), run
/// AutoRunBefore additional apps, launch the main target (through the emulator OR
/// directly for PC/no-emulator games), then AutoRunAfter apps; finally notify exit
/// and reload the optional tier. Relative paths resolve against the LB root, never
/// the process CWD. With <see cref="DryRun"/> nothing is spawned (commands are just
/// logged) — used to test the lifecycle without launching real processes.
/// </summary>
internal static class HostLaunch
{
    private static PluginRegistry _reg;
    private static GameStore _store;
    private static string _lbRoot;

    /// <summary>When true, the launch logs the resolved commands instead of spawning.</summary>
    public static bool DryRun;

    /// <summary>Raised when a game launch BEGINS (on the caller's thread — usually the UI thread).</summary>
    public static event Action<IGame> GameStarted;

    /// <summary>Raised when the launched game has EXITED and optional data is reloaded (on a worker thread).</summary>
    public static event Action<IGame> GameEnded;

    public static void Configure(PluginRegistry reg, GameStore store, string lbRoot)
    {
        _reg = reg; _store = store; _lbRoot = lbRoot;
    }

    public static void Launch(string who, IGame game, IAdditionalApplication app, IEmulator emulator, string overrideCmd)
    {
        if (game == null) return;

        // Resolve the game's configured emulator when the caller passed none (LaunchBox does this).
        // The web launch path (ExtendDB's GameLauncher) calls PlayGame(emulator=null), so without this
        // the host would Process.Start the raw ROM instead of "emulator.exe <cmd> rom" — which also
        // means an ExtendDB Harmony patch on Process.Start sees empty args and can't do its job.
        if (emulator == null && app == null)
        {
            try
            {
                string emuId = game.EmulatorId;
                if (!string.IsNullOrEmpty(emuId))
                    emulator = PluginHelper.DataManager?.GetEmulatorById(emuId);
            }
            catch { }
        }

        Console.WriteLine($"[launch/{who}] {game.Title}  emu={emulator?.Title ?? "(none)"}  app={app?.Name ?? "(none)"}{(DryRun ? "  (dry)" : "")}");

        // 0. snapshot the launched game BEFORE anything is dropped — in-game
        //    surfaces (pause screen fanart/logo/session time) read this, never
        //    the store / cache (both are freed below). The emulator contributes its
        //    startup/end-screen tier (global < emulator < game). The launch category
        //    (DOSBox / emulator / plain app) selects the StartupStayOnTop global default.
        bool useDosCat = SafeBool(() => app != null ? app.UseDosBox : game.UseDosBox);
        string stayCat = useDosCat ? "DosBox" : (emulator != null ? "Emulator" : "App");
        LaunchedGame.Capture(game, emulator, stayCat);

        // 0b. notify the GUI (it may show a "game running" screen / unload its list)
        //    BEFORE DropOptional so freed memory is reclaimed by the drop's GC.
        try { GameStarted?.Invoke(game); } catch { }

        // 1. notify launching plugins
        Fire(p => p.OnBeforeGameLaunching(game, app, emulator));

        // 2. free the optional tier + trim the working set — the headline
        //    "free RAM at launch" (GameStarted already unloaded the GUI list above,
        //    so the trim returns those pages to the OS too).
        try
        {
            Mem.Report("before drop (launch)");
            _store?.DropOptional();
            if (Gc.HostGameCache.Enabled && Gc.HostGameCache.UnloadDuringGame)
            { Gc.HostGameCache.ClearForMemory(); Console.WriteLine("[gamecache] cleared for game launch"); }
            // libvlc holds ~50 MB once it has decoded frames. LiteBox is idle while the game runs, so hand it
            // back; the next thumbnail re-creates the instance transparently (~200 ms).
            Video.VlcService.Shutdown();
            Mem.Trim();
            Mem.Report("after drop+trim (launch)");
        }
        catch { }

        // 3. run + wait on a worker thread so the UI/web stay responsive
        var t = new Thread(() => RunAndWait(game, app, emulator, overrideCmd)) { IsBackground = true, Name = "LbApiHost-game" };
        t.Start();
    }

    /// <summary>Launch a GOG/Steam game the store way: ShellExecute its ApplicationPath (a GOG .lnk or a
    /// steam:// URI) — the store client owns the process, so there's no Process to WaitForExit on. We
    /// still run the GUI lifecycle (running screen via GameStarted, play-time, GameEnded) and detect
    /// exit by watching the game's install folder for its process (StoreProcessWatcher).</summary>
    public static void LaunchStore(IGame game, Func<bool> regainedFocus = null, bool killLauncherAfter = false, bool killEvenIfPreRunning = false)
    {
        if (game == null) return;
        var kind = StoreSupport.KindOf(game);
        if (kind == StoreKind.None) return;
        string target = StoreSupport.LaunchTarget(kind, game);   // EA: ea://{id} → origin2:// (no ea:// handler)
        if (string.IsNullOrEmpty(target)) return;
        string installDir = StoreSupport.ResolveInstallDir(kind, game);
        Console.WriteLine($"[store-launch] {SafeStr(() => game.Title)} target={target} dir={installDir ?? "(unknown)"}");
        StoreTrace.Log($"store-launch START '{SafeStr(() => game.Title)}' kind={kind} dir={installDir ?? "(unknown)"} killLauncher={killLauncherAfter} evenIfPreRunning={killEvenIfPreRunning}");

        LaunchedGame.Capture(game, null, "Store." + kind);   // before any GUI unload; per-store stay-on-top default
        try { GameStarted?.Invoke(game); } catch { }   // GUI shows the running screen + unloads its list

        var t = new Thread(() => RunStoreAndWait(game, kind, target, installDir, regainedFocus, killLauncherAfter, killEvenIfPreRunning))
        { IsBackground = true, Name = "LbApiHost-store" };
        t.Start();
    }

    private static void RunStoreAndWait(IGame game, StoreKind kind, string target, string installDir, Func<bool> regainedFocus, bool killLauncherAfter, bool killEvenIfPreRunning = false)
    {
        int gi = GameIndex(game);
        var sw = Stopwatch.StartNew();
        bool seen = false;
        // Snapshot the store client's PIDs BEFORE launching so we can later kill only the one this launch
        // starts. Skipped when killEvenIfPreRunning is set (we then kill ALL clients regardless).
        var clientsBefore = (killLauncherAfter && !killEvenIfPreRunning) ? StoreProcessWatcher.SnapshotClients(kind) : null;
        try
        {
            if (DryRun) { Thread.Sleep(2500); return; }
            // SmartCapture GLOBAL scan for the store game: the game is spawned by the store client, NOT as
            // a descendant of the shell-opened URI, so a process tree can't find it. Instead we snapshot
            // every window that's open BEFORE launching and then watch for a NEW window that renders — that
            // is the game. The cover holds on a long backstop (like the emulator path) and SmartCapture
            // lifts it the moment the game renders, instead of the old blind Post-Launch-Display-Time timer.
            var scCfg = Gameplay.GameplaySettings.ResolveSmartCapture(null, SafeStr(() => game?.Id));
            scCfg.GlobalScan = true;
            scCfg.Baseline = Diag.WinScan.BaselineHwnds();
            int scDisplay = SafeNullableInt(() => Gameplay.GameplaySettings.Resolve(LaunchedGame.Current)?.StartupMinMs) ?? 2000;
            // Fallback safety-max ("reveal anyway after"): reveal the cover if a render is never detected.
            // Sourced from the LB "Startup Load Delay" (per-emulator/game), default 5s, floored at display.
            int scMax = Math.Max(scDisplay, Gameplay.GameplaySettings.RevealMaxMs(LaunchedGame.Current));
            // If this game was slow to detect last time, keep the cover up past the blind ceiling so we
            // don't reveal prematurely (extend to historical detection + 3s, capped at 2 min).
            long? histDet = null; try { histDet = _store.GetLastDetectionMs(SafeStr(() => game?.Id)); } catch { }
            if (histDet.HasValue) scMax = Math.Max(scMax, (int)Math.Min(120000, histDet.Value + 3000));
            int scBackstop = scMax + scDisplay + 5000;

            if (!StoreSupport.ShellOpen(target)) { Console.WriteLine("[store-launch] ShellOpen failed: " + target); StoreTrace.Log("store-launch ShellOpen FAILED"); return; }
            if (gi >= 0) { try { _store.JournalPlayStart(gi); } catch { } }
            // Notify launching plugins the game is up — same as the emulator path.
            // ExtendDB's GameLaunchHook needs this pair (OnAfterGameLaunched /
            // OnGameExited) to tear down + REOPEN the BigBox-web kiosk around a
            // store game; without OnGameExited the kiosk never comes back.
            Fire(p => p.OnAfterGameLaunched(game, null, null));
            if (!DryRun) Gameplay.ScreenCapture.Arm();   // screenshot hotkey active during the store game too (parity with RunAndWait)
            int scFade = Gameplay.GameplaySettings.FadeMs(LaunchedGame.Current, scDisplay);   // ≤1s dissolve at the reveal
            // Progress-bar ETA: predicted fade-start = past detection + hold − fade (null = no bar / first launch).
            int? scEta = (Gameplay.GameplaySettings.StartupProgressBar() && histDet.HasValue)
                ? (int?)Math.Max(500, (int)(histDet.Value + Math.Max(0, scDisplay - (scCfg.UseFps ? scCfg.SustainMs : 0)) - scFade)) : null;
            Console.WriteLine($"[store-launch] startup timing: display={scDisplay}ms fade={scFade}ms reveal-ceiling={scMax}ms histDet={(histDet?.ToString() ?? "none")} barEta={(scEta?.ToString() ?? "off")}");
            bool aggressive = AggressiveHiding(game, null);   // store game: no emulator, resolve from the game
            if (!DryRun) Gameplay.GameScreens.ShowStartup(LaunchedGame.Current, scCfg.Enabled ? scBackstop : (int?)null, scEta, aggressive);   // "NOW LOADING…"
            if (scCfg.Enabled) Gameplay.SmartCapture.Start(0, scCfg, scDisplay, scMax, () =>
            {
                Gameplay.GameScreens.Close(scFade);
                if (aggressive) Gameplay.WindowHider.Activate(Gameplay.SmartCapture.DetectedGameWindow);   // force the game active at the reveal
            }, scFade);
            sw.Restart();
            StoreTrace.Log("store-launch ShellOpen ok — watching for game process…");
            // When SmartCapture is on, its detected game window closing is a faster, more precise exit
            // signal than the process-gone debounce — pass it so GAME OVER fires the moment the window dies.
            Func<bool>? windowGone = scCfg.Enabled ? Gameplay.SmartCapture.GameWindowDetectedAndGone : (Func<bool>?)null;
            // Arm the pause screen on the STORE game process the watcher locks onto (store games have no
            // IEmulator; Arm falls back to the global/per-game hotkey + process suspend). Also arm the
            // "Hide Mouse Cursor During Game" / "Hide All Windows…" flags (game-only, no emulator) now that
            // the game process + window are up. All restored in the finally.
            bool hideCursor = HideCursorInGame(game, null);
            bool hideOthers = HideOtherWindows(game, null);
            Action<int> armPause = pid =>
            {
                try { Pause.PauseManager.Arm(Process.GetProcessById(pid), null, game); } catch { }
                if (hideCursor) Gameplay.GameCursor.Hide();
                if (hideOthers) Gameplay.WindowHider.Hide(pid);
            };
            seen = StoreProcessWatcher.WaitForGame(installDir, regainedFocus, windowGone, armPause);   // process-under-install-dir; focus only if opted in
            StoreTrace.Log($"store-launch watch done: seen={seen} elapsed={(int)sw.Elapsed.TotalSeconds}s");
        }
        catch (Exception ex) { Console.WriteLine("[store-launch] error: " + ex.Message); StoreTrace.Log("store-launch EX: " + ex.Message); }
        finally
        {
            Pause.PauseManager.Disarm();   // hotkey hook off + resume a still-frozen game + close the screen
            Gameplay.GameCursor.Show();        // undo "Hide Mouse Cursor During Game" (no-op if off)
            Gameplay.WindowHider.Restore();    // undo "Hide All Windows…" (no-op if off)
            if (killLauncherAfter && !DryRun)
            {
                try
                {
                    if (killEvenIfPreRunning) StoreProcessWatcher.KillAllClients(kind);
                    else StoreProcessWatcher.KillClientsStartedSince(kind, clientsBefore);
                }
                catch { }
            }
            Gameplay.ScreenCapture.Disarm();             // screenshot hotkey off (parity with RunAndWait)
            // Record the launch → detection latency (LiteBox-only) BEFORE Stop() clears it — reused next
            // launch to extend the reveal ceiling + drive the progress bar. Null when never detected.
            if (!DryRun) { var det = Gameplay.SmartCapture.DetectedAtMs; if (det.HasValue) { string gid = SafeStr(() => game?.Id); try { _store.RecordDetection(gid, det.Value); } catch { } try { Media.RomBridge.RecordDetection(game, det.Value); } catch { } } }
            Gameplay.SmartCapture.Stop();                // stop the global reveal watcher (game exited or never detected)
            var endSnap = LaunchedGame.Current;          // capture cosmetics before clearing
            if (!DryRun) Gameplay.GameScreens.ShowEndEager(endSnap);   // cover the exit transition (was Close())
            LaunchedGame.Clear();
            StoreTrace.Log("store-launch END → GameEnded (hide running screen)");
            // Record play time only if the game process was actually observed (else the launch likely
            // failed, or we couldn't track it — don't bill the watcher's wait as play time).
            if (!DryRun && seen && gi >= 0) { try { _store.JournalPlayTime(gi, (int)sw.Elapsed.TotalSeconds); } catch { } }
            if (!DryRun && seen) { try { Data.ProgressAutomation.ApplyToGame(game); } catch { } }   // LB parity (see RunAndWait)
            // Restore the optional data dropped at launch BEFORE the kiosk reopens / GUI reloads (see RunAndWait).
            try { _store?.ReloadOptional(); } catch { }
            try { if (Gc.HostGameCache.Enabled && Gc.HostGameCache.UnloadDuringGame) Gc.HostGameCache.Reload(); } catch { }
            EndOfGameFinish(endSnap);                     // OnGameExited (kiosk reopen) + GAME OVER, per WebReturnTiming
            try { GameEnded?.Invoke(game); } catch { }    // GUI hides the running screen + reloads its list
        }
    }

    private static void RunAndWait(IGame game, IAdditionalApplication app, IEmulator emulator, string overrideCmd)
    {
        // Store game (GOG / Steam / Epic) launched as the game itself (no explicit
        // additional-app): its ApplicationPath is a protocol URI / store .lnk, NOT a
        // runnable file. Hand it to the store path (ShellExecute) instead of falling
        // through to RunProcess — which runs it through ResolvePath (Path.Combine with
        // the LB root) and mangles "com.epicgames.launcher://apps/…" into a bogus
        // "<LB>\com.epicgames.launcher:\apps\…" local path that can't start. The GUI
        // Play already calls LaunchStore directly; this makes the web/kiosk launch
        // (ExtendDB GameLauncher → PlayGame → Launch → RunAndWait) behave the same.
        // Launch's lifecycle (OnBeforeGameLaunching/kiosk-suspend, GameStarted,
        // DropOptional) already ran; RunStoreAndWait owns play-time + GameEnded.
        if (app == null)
        {
            var storeKind = StoreSupport.KindOf(game);
            if (storeKind != StoreKind.None)
            {
                // Web-kiosk / GameLauncher path: honour the SAME "close the store client on exit" options
                // as the GUI Play button (read fresh from LiteBox.ini), instead of the old hardcoded false.
                var cfgNow = LiteBoxConfig.LoadForExe();
                RunStoreAndWait(game, storeKind, StoreSupport.LaunchTarget(storeKind, game),
                    StoreSupport.ResolveInstallDir(storeKind, game), null,
                    cfgNow.KillStoreLauncherAfterGame, cfgNow.KillStoreLauncherEvenIfPreRunning);
                return;
            }
        }

        // Play tracking (our user-state → journal): a real launch bumps play count +
        // last-played now; on exit we add the elapsed seconds to play time. Skipped in DryRun.
        int gi = GameIndex(game);
        var sw = Stopwatch.StartNew();
        if (!DryRun && gi >= 0) { try { _store.JournalPlayStart(gi); } catch { } }
        // Per-VERSION play tracking (LB parity): the launched additional app carries its own
        // PlayCount / LastPlayed / PlayTime alongside the game's. Safe post-DropOptional: the
        // add-app list is resident (not Tier-2), and the setters persist through the op-log.
        if (!DryRun && app != null)
            try { app.PlayCount += 1; app.LastPlayed = DateTime.Now; } catch { }
        // LiteBox's own last-launch history (emulator + version). Dual-written on EVERY launch — even
        // when ExtendDB is loaded — so the history survives disabling the plugin (ROM stays ExtendDB's).
        if (!DryRun) { try { _store.RecordLaunch(SafeStr(() => game.Id), SafeStr(() => emulator?.Id), SafeStr(() => app?.Id)); } catch { } }
        try
        {
            Fire(p => p.OnAfterGameLaunched(game, app, emulator));
            if (!DryRun) Gameplay.ScreenCapture.Arm();   // screenshot hotkey active during the game

            var addApps = SafeAddApps(game);

            // Main target: built-in DOSBox, an explicit additional-app, or the game.
            bool useDos = SafeBool(() => app != null ? app.UseDosBox : game.UseDosBox);
            string target = !string.IsNullOrEmpty(SafeStr(() => app?.ApplicationPath))
                ? app.ApplicationPath : SafeStr(() => game.ApplicationPath);

            // Dependency pre-check (integration plugin's required bios files) —
            // BEFORE any side effect (autoruns included), like LB's dialog. The
            // user may cancel the launch.
            if (!DryRun && !useDos && emulator != null
                && !DependencyCheck.PreLaunchCheck(emulator, game))
                return;   // finally still runs (play-time, idle state, reload)

            // AutoRunBefore additional apps (scripts, mounts, …).
            foreach (var a in addApps.Where(a => a.AutoRunBefore))
                RunProcess(a.ApplicationPath, a.CommandLine, emulator, game, a.UseEmulator, $"autorun-before \"{a.Name}\"");

            if (useDos && !string.IsNullOrEmpty(target))
            {
                RunDosBox(game, target, "dosbox");   // Spawn() handles DryRun
            }
            else
            {
                var main = ResolveMain(game, app, emulator, overrideCmd);
                if (main.HasValue)
                {
                    // Emulator "Running AutoHotkey Script" — started right before the
                    // emulator spawn, killed on game exit (see AhkScript / LB parity).
                    if (!DryRun && main.Value.useEmu && emulator != null)
                        AhkScript.StartGameScript(SafeStr(() => emulator.AutoHotkeyScript), _lbRoot);
                    // Pause screen: arm the global hotkey + remember the emulator
                    // process for suspend/resume (PauseManager.Disarm in the finally).
                    // SmartCapture: reveal the cover when the game actually renders, not on a
                    // blind timer. Resolved game → emulator → global; when on, the cover uses a
                    // safety-max and the coordinator closes it early (GameScreens.Close).
                    var scCfg = Gameplay.GameplaySettings.ResolveSmartCapture(SafeStr(() => emulator?.Id), SafeStr(() => game?.Id));
                    // Post-Launch Display Time: the cover shows for this long AFTER the game starts
                    // rendering (SmartCapture detects the render, then keeps the cover displayMs more
                    // — the detection window is subtracted inside). Safety max = fallback if the game
                    // is never detected (exclusive fullscreen).
                    int scDisplay = SafeNullableInt(() => Gameplay.GameplaySettings.Resolve(LaunchedGame.Current)?.StartupMinMs) ?? 2000;
                    int scMax = Math.Max(scDisplay, Gameplay.GameplaySettings.RevealMaxMs(LaunchedGame.Current));   // "reveal anyway after" = LB Startup Load Delay (default 5s)
                    // Slow-to-detect last time → keep the cover past the blind ceiling (historical + 3s, cap 2 min).
                    long? histDet = null; try { histDet = _store.GetLastDetectionMs(SafeStr(() => game?.Id)); } catch { }
                    if (histDet.HasValue) scMax = Math.Max(scMax, (int)Math.Min(120000, histDet.Value + 3000));
                    int scBackstop = scMax + scDisplay + 5000;   // the coordinator owns the reveal; this is a last-resort cover timer
                    int scFade = Gameplay.GameplaySettings.FadeMs(LaunchedGame.Current, scDisplay);   // ≤1s dissolve at the reveal
                    // Progress-bar ETA: predicted fade-start = past detection + hold − fade (null = no bar / first launch).
                    int? scEta = (Gameplay.GameplaySettings.StartupProgressBar() && histDet.HasValue)
                        ? (int?)Math.Max(500, (int)(histDet.Value + Math.Max(0, scDisplay - (scCfg.UseFps ? scCfg.SustainMs : 0)) - scFade)) : null;
                    Console.WriteLine($"[emu-launch] startup timing: display={scDisplay}ms fade={scFade}ms reveal-ceiling={scMax}ms histDet={(histDet?.ToString() ?? "none")} barEta={(scEta?.ToString() ?? "off")}");
                    // LB "Hide Mouse Cursor During Game" / "Hide All Windows not in Exclusive Fullscreen":
                    // resolved game → emulator (the game wins when it overrides the default startup settings,
                    // or has no emulator). Armed on spawn, restored in the finally.
                    bool hideCursor = HideCursorInGame(game, emulator);
                    bool hideOthers = HideOtherWindows(game, emulator);
                    bool aggressive = AggressiveHiding(game, emulator);
                    Action<Process> onSpawned = p =>
                    {
                        // Pause works for ALL launch types, not just emulators: a direct-exe game (useEmu
                        // false / no emulator) is the main process itself, so we arm on it too. Arm handles
                        // the null emulator (global/per-game hotkey, process suspend, pause screen).
                        Pause.PauseManager.Arm(p, emulator, game);
                        Diag.RenderProbe.MaybeStart(p);   // no-op unless LITEBOX_RENDERPROBE=1
                        if (!DryRun && hideCursor) Gameplay.GameCursor.Hide();
                        if (!DryRun && hideOthers) Gameplay.WindowHider.ArmFor(p.Id, () => { try { p.Refresh(); return p.MainWindowHandle; } catch { return IntPtr.Zero; } });
                        if (scCfg.Enabled && !DryRun)
                            Gameplay.SmartCapture.Start(p.Id, scCfg, scDisplay, scMax, () =>
                            {
                                Gameplay.GameScreens.Close(scFade);
                                // Aggressive hiding kept the game behind the cover during load → force it
                                // active at the reveal so it gets focus/input (detected window, else main).
                                if (aggressive) { IntPtr h = Gameplay.SmartCapture.DetectedGameWindow; if (h == IntPtr.Zero) { try { p.Refresh(); h = p.MainWindowHandle; } catch { } } Gameplay.WindowHider.Activate(h); }
                            }, scFade);
                    };
                    // Startup screen ("NOW LOADING…"). SmartCapture on → cover held to a generous
                    // backstop; the coordinator closes it at render-start + displayTime.
                    if (!DryRun) Gameplay.GameScreens.ShowStartup(LaunchedGame.Current, scCfg.Enabled ? scBackstop : (int?)null, scEta, aggressive);
                    RunProcess(main.Value.path, main.Value.args, emulator, game, main.Value.useEmu, "main", onSpawned);
                }
                else if (!DryRun)
                    System.Windows.Forms.MessageBox.Show(
                        $"[dummy launch] {game.Title}\nPlatform: {game.Platform}\nApp: {game.ApplicationPath}\n\n(Close = game exited)",
                        "LiteBox — dummy game");
                else
                    Console.WriteLine($"[launch/dry] main: (nothing runnable for \"{game.Title}\")");
            }
            if (DryRun) Thread.Sleep(2500); // hold so the running state is observable while testing

            // AutoRunAfter additional apps (cleanup).
            foreach (var a in addApps.Where(a => a.AutoRunAfter))
                RunProcess(a.ApplicationPath, a.CommandLine, emulator, game, a.UseEmulator, $"autorun-after \"{a.Name}\"");
        }
        catch (Exception ex) { Console.WriteLine("[launch] error: " + ex.Message); }
        finally
        {
            Pause.PauseManager.Disarm();  // hotkey off + resume a still-frozen process + close the screen
            Gameplay.ScreenCapture.Disarm();
            Gameplay.GameCursor.Show();        // undo "Hide Mouse Cursor During Game" (no-op if off)
            Gameplay.WindowHider.Restore();    // undo "Hide All Windows…" (no-op if off)
            // Record the launch → detection latency (LiteBox-only) BEFORE Stop() clears it.
            if (!DryRun) { var det = Gameplay.SmartCapture.DetectedAtMs; if (det.HasValue) { string gid = SafeStr(() => game?.Id); try { _store.RecordDetection(gid, det.Value); } catch { } try { Media.RomBridge.RecordDetection(game, det.Value); } catch { } } }
            Gameplay.SmartCapture.Stop();  // stop the reveal watcher (game exited before/after detection)
            var endSnap = LaunchedGame.Current;   // capture cosmetics before clearing (end screen needs them)
            // Cover the exit transition RIGHT NOW — before the cleanup below AND before OnGameExited
            // (which, under ExtendDB, reopens the web kiosk). Everything then happens BEHIND the GAME
            // OVER cover, so neither LiteBox nor the kiosk is revealed until the shutdown screen ends.
            // Previously the end screen was shown only at the very end of this block, so the game's exit
            // uncovered LiteBox (or flashed the reopening kiosk) during play-time / progress / plugin work.
            // ShowEndEager also drops any lingering startup cover, and no-ops (no flash) when the pause-menu
            // exit already put an early cover up.
            if (!DryRun) Gameplay.GameScreens.ShowEndEager(endSnap);
            AhkScript.KillGameScript();   // running script dies with the game (LB parity)
            LaunchedGame.Clear();
            if (!DryRun && gi >= 0) { try { _store.JournalPlayTime(gi, (int)sw.Elapsed.TotalSeconds); } catch { } }
            // Per-version play time — same elapsed seconds as the game's (see JournalPlayStart above).
            if (!DryRun && app != null)
                try { int secs = (int)sw.Elapsed.TotalSeconds; if (secs > 0) app.PlayTime += secs; } catch { }
            // Automatic Progress Tracking (LB parity): re-evaluate this game's Progress now that its
            // play time / last-played moved. Gated internally on EnableAutoProgressTracking.
            if (!DryRun) { try { Data.ProgressAutomation.ApplyToGame(game); } catch { } }
            // Rebuild the data LiteBox dropped during the game (optional game fields: notes, extra/GOG,
            // sub-entities) BEFORE OnGameExited reopens the ExtendDB web kiosk / GameEnded reloads the GUI
            // — otherwise they read INCOMPLETE games. This bit the "behind" timing hardest: the kiosk
            // preloads its data hidden during the GAME OVER hold, so it must be complete by then. All of
            // this runs behind the GAME OVER cover (already shown at the top of the finally).
            try { _store?.ReloadOptional(); Mem.Report("after ReloadOptional (exit)"); } catch { }
            try { if (Gc.HostGameCache.Enabled && Gc.HostGameCache.UnloadDuringGame) { Gc.HostGameCache.Reload(); Console.WriteLine("[gamecache] rebuilding after game exit"); } } catch { }
            // OnGameExited (reopens the ExtendDB kiosk) + the GAME OVER screen, ordered per WebReturnTiming.
            EndOfGameFinish(endSnap);
            // GUI: game over + data reloaded → reload its list and restore selection.
            try { GameEnded?.Invoke(game); } catch { }
        }
    }

    /// <summary>Fire OnGameExited (which, under ExtendDB, reopens the web kiosk) and show the GAME OVER
    /// screen, ordered by the WebReturnTiming option so the kiosk doesn't reappear before the shutdown
    /// screen. Degrades to "immediate" when ExtendDB isn't loaded, no end screen will show, or the plugin
    /// is too old for the deferred path. The GAME OVER cover was already put up (ShowEndEager) by the caller.
    ///   • immediate — OnGameExited now; GAME OVER (may reuse/re-assert the cover) holds then closes.
    ///   • after     — GAME OVER holds; OnGameExited fires over the cover; the cover then closes behind it.
    ///   • behind     — kiosk reopens HIDDEN now (PrepareDeferredReopen); GAME OVER holds+closes; reveal it.</summary>
    private static void EndOfGameFinish(LaunchedGame endSnap)
    {
        string timing = (!DryRun && Media.KioskBridge.Available && Gameplay.GameScreens.EndScreenWillShow(endSnap))
            ? Gameplay.GameplaySettings.WebReturnTiming() : "immediate";
        if (timing == "behind" && !Media.KioskBridge.SupportsDeferredReopen) timing = "immediate";

        if (timing == "behind") Media.KioskBridge.PrepareDeferredReopen();   // next OnGameExited restore → hidden
        if (timing != "after") Fire(p => p.OnGameExited());                  // immediate + behind fire now
        if (!DryRun)
            Gameplay.GameScreens.ShowEndBlocking(endSnap,
                timing == "after" ? (Action)(() => Fire(p => p.OnGameExited())) : null);
        if (timing == "behind") Media.KioskBridge.RevealDeferredKiosk();     // reveal the kiosk loaded under the cover
    }

    /// <summary>Row index for a game (via the store's id map), or -1.</summary>
    private static int GameIndex(IGame game)
    {
        try { return _store != null && Guid.TryParse(game?.Id, out var id) && _store.ById.TryGetValue(id, out var i) ? i : -1; }
        catch { return -1; }
    }

    /// <summary>(path, args, useEmu) of the main thing to launch, or null if nothing runnable.</summary>
    private static (string path, string args, bool useEmu)? ResolveMain(
        IGame game, IAdditionalApplication app, IEmulator emulator, string overrideCmd)
    {
        string targetPath = !string.IsNullOrEmpty(app?.ApplicationPath) ? app.ApplicationPath : game.ApplicationPath;
        if (string.IsNullOrEmpty(targetPath)) return null;

        // Use the emulator when the app says so, or (no app) whenever one resolved.
        bool useEmu = app != null ? app.UseEmulator : emulator != null;
        string cmd = overrideCmd ?? (app?.CommandLine ?? SafeGameCommandLine(game));
        return (targetPath, cmd ?? "", useEmu);
    }

    /// <summary>Resolves the command, spawns (or logs in DryRun), waits for exit.</summary>
    private static void RunProcess(string targetPath, string cmd, IEmulator emulator, IGame game, bool useEmu, string label, Action<Process> onSpawned = null)
    {
        if (string.IsNullOrEmpty(targetPath)) return;

        string fileName, args, workDir = null;
        if (useEmu && emulator != null && !string.IsNullOrEmpty(emulator.ApplicationPath))
        {
            var ep = ResolveEmulatorPlatform(emulator, game);
            string emuCmd = !string.IsNullOrWhiteSpace(cmd) ? cmd
                          : (SafeStr(() => ep?.CommandLine) is { Length: > 0 } pc ? pc : SafeStr(() => emulator.CommandLine));
            fileName = ResolvePath(emulator.ApplicationPath);
            // Integration-plugin fixups on the emulator's command line (per-exe
            // normalisation the plugin knows about, e.g. RetroArch core paths).
            emuCmd = EmuPlugins.NormalizeCommandLine(emulator, emuCmd ?? "", fileName);
            // ROM to pass: m3u (multi-disc) → auto-extracted file (archive) → the rom itself.
            string romAbs = ResolvePath(targetPath);
            string rom = ResolveLaunchRomPath(game, emulator, ep, romAbs, label);
            // When the command line ALREADY places the ROM itself via %romfile% (ScummVM's "-p %romfile%",
            // DOSBox, …), LB substitutes it IN PLACE and does NOT also append the ROM at the end — appending
            // would pass the ROM twice and the emulator chokes (ScummVM never launches). %romlocation% & co.
            // are partial (MAME's "-rompath %romlocation%") and DON'T suppress the append.
            bool cmdPlacesRom = !string.IsNullOrEmpty(emuCmd)
                                && emuCmd.IndexOf("%romfile%", StringComparison.OrdinalIgnoreCase) >= 0;
            // Expand the LaunchBox command-line variables (%romlocation%, %romfile%, …) that
            // LB's core (Game.PlayEmulator) resolves at launch and that integration plugins
            // embed in their command lines — most importantly MAME's "-rompath %romlocation%".
            // %romfile% resolves to the actual launch rom (extracted / m3u when applicable).
            emuCmd = ExpandLaunchVariables(emuCmd, cmdPlacesRom ? rom : romAbs, fileName, game);
            args = cmdPlacesRom ? (emuCmd?.Trim() ?? "") : BuildEmulatorArgs(emuCmd, rom, emulator);
            // PrepareEmulatorForLaunch: the integration plugin may rewrite the
            // final command line right before the spawn (what LB does silently).
            // Main launch only — autorun helpers aren't emulator launches.
            if (label == "main" && game != null)
                args = EmuPlugins.PrepareForLaunch(emulator, game, null, args);
        }
        else
        {
            fileName = ResolvePath(targetPath);   // direct launch (PC, TeknoParrot, scripts)
            args = cmd?.Trim() ?? "";
            // LB semantics: a direct exe launch runs in the game's Root Folder when one is set AND
            // the directory exists (LB fills it in by default at import) — else in the exe's own
            // folder (Spawn's default). Emulator launches keep the emulator's folder; DOSBox uses
            // the Root Folder as its C: mount instead (separate, deferred runtime wiring).
            workDir = RootFolderDir(game);
        }
        // Honour the emulator's "Attempt to hide console" flag (LB's HideConsole). A
        // console-subsystem emulator like MAME otherwise pops a console window that grabs
        // the foreground, leaving the game window unfocused — CreateNoWindow suppresses it.
        bool hideConsole = useEmu && emulator != null && SafeBool(() => emulator.HideConsole);
        // Startup screen yields the foreground to the emulator we're about to spawn: the
        // "NOW LOADING…" overlay drops its always-on-top + focus-stealing so the emulator
        // window comes up focused on top of it (the overlay stays visible until its timer
        // closes it). Main emulator launch only — not autorun helpers.
        if (label == "main") Gameplay.GameScreens.ReleaseStartupTopFront();
        Spawn(fileName, args, label, onSpawned, hideConsole, workDir);
    }

    /// <summary>The game's Root Folder resolved to an absolute path (relative values are
    /// LB-root-based, like every LB path), when set AND the directory exists — the working
    /// directory for a direct exe launch. Null otherwise (caller falls back to the exe's folder).</summary>
    private static string RootFolderDir(IGame game)
    {
        try
        {
            var rf = SafeStr(() => game?.RootFolder);
            if (string.IsNullOrWhiteSpace(rf)) return null;
            var abs = ResolvePath(rf.Trim());
            return Directory.Exists(abs) ? abs : null;
        }
        catch { return null; }
    }

    /// <summary>The EmulatorPlatform matching the game's platform, else the emulator's default platform.</summary>
    private static IEmulatorPlatform ResolveEmulatorPlatform(IEmulator emulator, IGame game)
    {
        try
        {
            var eps = emulator.GetAllEmulatorPlatforms();
            return eps?.FirstOrDefault(x => string.Equals(x.Platform, SafeStr(() => game.Platform), StringComparison.OrdinalIgnoreCase))
                ?? eps?.FirstOrDefault(x => x.IsDefault);
        }
        catch { return null; }
    }

    /// <summary>Formats the emulator command line + ROM token, honouring the LaunchBox emulator flags:
    /// NoQuotes (no surrounding quotes), NoSpace (no space before the ROM), FileNameWithoutExtensionAndPath
    /// (just the base name). Mirrors LB's "Sample Command".</summary>
    private static string BuildEmulatorArgs(string emuCmd, string romPath, IEmulator emulator)
    {
        bool noQuotes = SafeBool(() => emulator.NoQuotes);
        bool noSpace = SafeBool(() => emulator.NoSpace);
        bool nameOnly = SafeBool(() => emulator.FileNameWithoutExtensionAndPath);

        string token = nameOnly ? Path.GetFileNameWithoutExtension(romPath) : romPath;
        if (!noQuotes) token = "\"" + token + "\"";
        emuCmd = emuCmd?.Trim() ?? "";
        return emuCmd.Length == 0 ? token : emuCmd + (noSpace ? "" : " ") + token;
    }

    /// <summary>Expands the LaunchBox emulator command-line variables that LB's core resolves at launch
    /// (Game.PlayEmulator) and that integration plugins embed in their command lines — most importantly
    /// MAME's "-rompath %romlocation%". Tokens are matched case-insensitively (LB parity); unknown
    /// %tokens% are left untouched. Resolved against the ORIGINAL ROM path (not an extracted/m3u file),
    /// mirroring LB. Values containing spaces are auto-quoted unless the template already quotes them.
    ///   %romfile%      → full absolute path of the ROM
    ///   %romlocation%  → directory containing the ROM
    ///   %romfilename%  → ROM file name with extension (no path)
    ///   %romname%      → ROM file name without extension
    ///   %emulatorpath% → directory of the emulator executable
    ///   %platform%     → the game's platform name
    /// </summary>
    private static string ExpandLaunchVariables(string cmd, string romAbs, string emulatorExe, IGame game)
    {
        if (string.IsNullOrEmpty(cmd) || cmd.IndexOf('%') < 0) return cmd ?? "";
        var vars = new (string name, string value)[]
        {
            ("romlocation",  SafeStr(() => Path.GetDirectoryName(romAbs)) ?? ""),
            ("romfilename",  SafeStr(() => Path.GetFileName(romAbs)) ?? ""),
            ("romname",      SafeStr(() => Path.GetFileNameWithoutExtension(romAbs)) ?? ""),
            ("romfile",      romAbs ?? ""),
            ("emulatorpath", SafeStr(() => Path.GetDirectoryName(emulatorExe)) ?? ""),
            ("platform",     SafeStr(() => game?.Platform) ?? ""),
        };
        foreach (var (name, value) in vars)
            cmd = ReplaceTokenCI(cmd, "%" + name + "%", value);
        return cmd;
    }

    /// <summary>Case-insensitive replace of every <paramref name="token"/> in <paramref name="s"/>. The
    /// inserted value is wrapped in quotes when it contains a space, unless the template already wraps the
    /// token in quotes (LB's auto-quoting behaviour). Advances past inserted text so a value that itself
    /// contains a '%' is never re-scanned.</summary>
    private static string ReplaceTokenCI(string s, string token, string value)
    {
        int i = 0;
        while ((i = s.IndexOf(token, i, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            bool alreadyQuoted = i > 0 && s[i - 1] == '"'
                                 && i + token.Length < s.Length && s[i + token.Length] == '"';
            bool needQuotes = !alreadyQuoted && value.IndexOf(' ') >= 0
                              && !(value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"');
            string ins = needQuotes ? "\"" + value + "\"" : value;
            s = s.Substring(0, i) + ins + s.Substring(i + token.Length);
            i += ins.Length;
        }
        return s;
    }

    /// <summary>Resolves the ROM path actually passed to the emulator: an auto-generated .m3u for a
    /// multi-disc game (M3uDiscLoadEnabled), an extracted file for an archive (AutoExtract), else the ROM.</summary>
    private static string ResolveLaunchRomPath(IGame game, IEmulator emulator, IEmulatorPlatform ep, string romAbs, string label)
    {
        try
        {
            if (ep != null && ep.M3uDiscLoadEnabled)
            {
                var m3u = TryBuildM3u(game);
                if (!string.IsNullOrEmpty(m3u)) { Console.WriteLine($"[launch] {label}: m3u disc-load → \"{m3u}\""); return m3u; }
            }
        }
        catch (Exception ex) { Console.WriteLine($"[launch] {label}: m3u build error: {ex.Message}"); }

        try
        {
            bool autoExtract = (ep?.AutoExtract) ?? SafeBool(() => emulator.AutoExtract);
            if (autoExtract && IsArchive(romAbs))
            {
                // ExtendDB owns archive extraction (Select-ROM pick, region/tag priority,
                // smart-extract, cache, RA bonus). When its Archive MultiGame Selector is on,
                // DEFER to it: leave the archive on the command line so the plugin's Process.Start
                // patch extracts the CHOSEN entry. Our own 7z below would instead extract everything
                // and launch the first file alphabetically — silently ignoring the user's selection.
                // The fallback runs only when ExtendDB isn't handling archives, so launches still
                // work if the plugin is absent/disabled (ROM stays the plugin's domain).
                if (Media.RomBridge.Available)
                {
                    Console.WriteLine($"[launch] {label}: archive \"{Path.GetFileName(romAbs)}\" — ExtendDB owns extraction; passing the archive through (plugin picks the entry).");
                    return romAbs;
                }
                var extracted = TryExtractArchive(romAbs, label);
                if (!string.IsNullOrEmpty(extracted)) return extracted;
            }
        }
        catch (Exception ex) { Console.WriteLine($"[launch] {label}: auto-extract error: {ex.Message}"); }

        return romAbs;
    }

    private static readonly string[] _archiveExts = { ".zip", ".7z", ".rar" };
    private static bool IsArchive(string p)
    { try { return _archiveExts.Contains(Path.GetExtension(p ?? "").ToLowerInvariant()); } catch { return false; } }

    // Primary-file extension priority after extraction: disc descriptors first, then PC exe, then common
    // ROM/disc image extensions; otherwise the first file alphabetically.
    private static readonly string[] _romPriority =
    {
        ".m3u", ".cue", ".gdi", ".ccd", ".chd", ".exe",
        ".iso", ".bin", ".img",
        ".nds", ".3ds", ".gba", ".gb", ".gbc", ".nes", ".fds", ".sfc", ".smc", ".n64", ".z64", ".v64",
        ".gen", ".md", ".smd", ".sms", ".gg", ".pce", ".ws", ".wsc", ".ngp", ".ngc", ".a26", ".a78",
        ".lnx", ".col", ".int", ".vec", ".32x", ".rom", ".d64", ".adf", ".dsk", ".cdi", ".pbp",
    };

    /// <summary>Extracts an archive via LB's bundled 7-Zip (ExtendDB recognises this native call and
    /// stays out of the way) into a temp dir (under LB if writable, else %TEMP%), and returns the
    /// primary file to launch (by extension priority, else the first). Null on failure.</summary>
    private static string TryExtractArchive(string archiveAbs, string label)
    {
        string sevenZip = ResolvePath(Path.Combine("ThirdParty", "7-Zip", "7z.exe"));
        if (!File.Exists(sevenZip) || !File.Exists(archiveAbs)) return null;

        string outDir = null;
        string sub = Path.Combine("LiteBox", Sanitize(Path.GetFileNameWithoutExtension(archiveAbs)));
        try { outDir = ResolvePath(Path.Combine("ThirdParty", "7-Zip", "Temp", sub)); Directory.CreateDirectory(outDir); }
        catch { outDir = null; }
        if (outDir == null)
        {
            try { outDir = Path.Combine(Path.GetTempPath(), sub); Directory.CreateDirectory(outDir); } catch { return null; }
        }
        try { foreach (var f in Directory.GetFiles(outDir, "*", SearchOption.AllDirectories)) File.Delete(f); } catch { }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = sevenZip,
                Arguments = $"x \"{archiveAbs}\" -o\"{outDir}\" -y",
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(sevenZip) ?? outDir,
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(120000);
        }
        catch (Exception ex) { Console.WriteLine($"[launch] {label}: 7z error: {ex.Message}"); return null; }

        var primary = PickPrimaryFile(outDir);
        Console.WriteLine($"[launch] {label}: auto-extract \"{archiveAbs}\" → \"{primary ?? "<none>"}\"");
        return primary;
    }

    private static string PickPrimaryFile(string dir)
    {
        string[] files;
        try { files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories); } catch { return null; }
        if (files.Length == 0) return null;
        foreach (var ext in _romPriority)
        {
            var hit = files.Where(f => Path.GetExtension(f).Equals(ext, StringComparison.OrdinalIgnoreCase))
                           .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            if (hit != null) return hit;
        }
        return files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).First();
    }

    /// <summary>Builds an .m3u listing the game's disc files (one absolute path per line, in disc order)
    /// for a multi-disc game. Written under &lt;LB&gt;\Metadata\Temp\&lt;Title&gt;\&lt;GUID&gt;\ (so ExtendDB's
    /// interception recognises it as the LB-native m3u and can reformulate it), else %TEMP%. Null if the
    /// game isn't multi-disc.
    ///
    /// Disc set = the game's own ROM (disc 1) + every additional application that has an ApplicationPath
    /// and isn't a setup/utility auto-run app. LB-native does NOT require the numeric &lt;Disc&gt; field —
    /// most real multi-disc games only carry the disc number in the add-app name ("CD 1", "Disc 2"). So
    /// ordering uses &lt;Disc&gt; when present, else the first integer in the name, else enumeration order.</summary>
    private static string TryBuildM3u(IGame game)
    {
        // (discKey, nameNum, name, idx, path) — idx preserves enumeration order as the final tiebreaker.
        var apps = new List<(int discKey, int nameNum, string name, int idx, string path)>();
        try
        {
            int idx = 0;
            foreach (var a in SafeAddApps(game))
            {
                int i = idx++;
                // auto-run-before / -after apps are setup/utility steps, not discs
                if (SafeBool(() => a.AutoRunBefore) || SafeBool(() => a.AutoRunAfter)) continue;
                string p = ResolvePath(SafeStr(() => a.ApplicationPath));
                if (string.IsNullOrEmpty(p)) continue;
                int discKey = SafeNullableInt(() => a.Disc) ?? int.MaxValue;
                string nm = SafeStr(() => a.Name);
                apps.Add((discKey, FirstInt(nm), nm, i, p));
            }
        }
        catch { }

        var orderedApps = apps
            .OrderBy(e => e.discKey)
            .ThenBy(e => e.nameNum)
            .ThenBy(e => e.name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.idx)
            .Select(e => e.path);

        // The game's own ROM is disc 1 by convention → first; dedup keeps the first occurrence so a
        // disc-1 add-app pointing at the same file doesn't double it.
        var all = new List<string>();
        string mainPath = ResolvePath(SafeStr(() => game.ApplicationPath));
        if (!string.IsNullOrEmpty(mainPath)) all.Add(mainPath);
        all.AddRange(orderedApps);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>();
        foreach (var p in all) if (seen.Add(p)) ordered.Add(p);
        if (ordered.Count < 2) return null;   // single disc → no m3u

        string title = Sanitize(SafeStr(() => game.Title) is { Length: > 0 } t ? t : "game");
        string gid = SafeStr(() => game.Id) is { Length: > 0 } g2 ? g2 : Guid.NewGuid().ToString("N");
        string dir = null;
        try { dir = ResolvePath(Path.Combine("Metadata", "Temp", title, gid)); Directory.CreateDirectory(dir); }
        catch { dir = null; }
        if (dir == null) { try { dir = Path.Combine(Path.GetTempPath(), "LiteBox", "m3u", gid); Directory.CreateDirectory(dir); } catch { return null; } }

        string m3u = Path.Combine(dir, title + ".m3u");
        try { File.WriteAllLines(m3u, ordered); return m3u; } catch { return null; }
    }

    private static string Sanitize(string s)
    {
        if (string.IsNullOrEmpty(s)) return "_";
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Trim().Length == 0 ? "_" : s.Trim();
    }

    /// <summary>First run of digits in <paramref name="s"/> as an int ("CD 1" → 1, "Disc 2" → 2),
    /// or int.MaxValue when there's none (sorts such entries last).</summary>
    private static int FirstInt(string s)
    {
        if (string.IsNullOrEmpty(s)) return int.MaxValue;
        var m = System.Text.RegularExpressions.Regex.Match(s, @"\d+");
        return m.Success && int.TryParse(m.Value, out var n) ? n : int.MaxValue;
    }

    /// <summary>
    /// LaunchBox's built-in DOSBox launch (LB\ThirdParty\DOSBox\DOSBox.exe): mount the
    /// game's folder as C:, CALL the entry file, with the per-game .conf (or the default
    /// dosbox.conf). Mirrors LB's exact command line.
    /// </summary>
    /// <summary>Resolve a native LB startup flag game → emulator: the game's value wins when it overrides
    /// the default startup settings (or has no emulator), else the emulator's. Used for the two window/cursor
    /// hiding flags, which — like the rest of the startup group — follow OverrideDefaultStartupScreenSettings.</summary>
    private static bool HideCursorInGame(IGame game, IEmulator emulator)
        => (SafeBool(() => game.OverrideDefaultStartupScreenSettings) || emulator == null)
            ? SafeBool(() => game.HideMouseCursorInGame)
            : SafeBool(() => emulator.HideMouseCursorInGame);

    private static bool HideOtherWindows(IGame game, IEmulator emulator)
        => (SafeBool(() => game.OverrideDefaultStartupScreenSettings) || emulator == null)
            ? SafeBool(() => game.HideAllNonExclusiveFullscreenWindows)
            : SafeBool(() => emulator.HideAllNonExclusiveFullscreenWindows);

    private static bool AggressiveHiding(IGame game, IEmulator emulator)
        => (SafeBool(() => game.OverrideDefaultStartupScreenSettings) || emulator == null)
            ? SafeBool(() => game.AggressiveWindowHiding)
            : SafeBool(() => emulator.AggressiveWindowHiding);

    private static void RunDosBox(IGame game, string targetPath, string label)
        => Spawn(DosBoxExe(game), BuildDosBoxArgs(game, targetPath, SafeStr(() => game.CommandLine)), label,
                 // Pause the DOS game too — DOSBox has no IEmulator record (it's our bundle), so arm with a
                 // null emulator; suspend/resume + the pause screen work the same. Disarm runs in RunGame's finally.
                 onSpawned: p =>
                 {
                     if (DryRun) return;
                     Pause.PauseManager.Arm(p, null, game);
                     if (HideCursorInGame(game, null)) Gameplay.GameCursor.Hide();
                     if (HideOtherWindows(game, null)) Gameplay.WindowHider.ArmFor(p.Id, () => { try { p.Refresh(); return p.MainWindowHandle; } catch { return IntPtr.Zero; } });
                 });

    /// <summary>The DOSBox exe: per-game Custom DOSBox Version EXE, else the bundle.</summary>
    private static string DosBoxExe(IGame game)
    {
        string custom = (game as LbApiHost.Host.Data.HostGame)?.CustomDosBoxVersionPath;
        return !string.IsNullOrWhiteSpace(custom)
            ? ResolvePath(custom)
            : ResolvePath(Path.Combine("ThirdParty", "DOSBox", "DOSBox.exe"));
    }

    /// <summary>
    /// Builds LaunchBox's exact DOSBox command line for an entry (game OR config app):
    /// optional additional mounts, MOUNT C = game folder, CALL/.bat-or-run-direct the
    /// entry, with the global DOSBox options (Show all commands / Don't exit / Pause).
    /// </summary>
    private static string BuildDosBoxArgs(IGame game, string entryPath, string extraCmd)
    {
        var (show, exit, pauseEach, pauseExit) = DosBoxOpts();

        string appAbs = ResolvePath(entryPath);
        string appDir = SafeDir(appAbs) ?? "";
        // C: is mounted at the game's Root Folder (auto-populated to the app folder, but
        // user-editable). We then CD into the app's sub-path within that root.
        string rootRaw = SafeStr(() => game.RootFolder);
        string rootAbs = !string.IsNullOrWhiteSpace(rootRaw) ? ResolvePath(rootRaw) : appDir;
        string relDir = "";
        try { var r = Path.GetRelativePath(rootAbs, appDir); if (r != "." && !r.StartsWith("..")) relDir = r; } catch { }
        string entryFile = Path.GetFileName(appAbs);
        string ext = Path.GetExtension(entryFile).ToLowerInvariant();
        string entry = (ext == ".bat" || ext == ".cmd") ? "CALL " + entryFile : entryFile;  // .exe/.com run direct
        if (!string.IsNullOrWhiteSpace(extraCmd)) entry += " " + extraCmd.Trim();

        string confCustom = SafeStr(() => game.DosBoxConfigurationPath);
        string conf = !string.IsNullOrWhiteSpace(confCustom)
            ? ResolvePath(confCustom)
            : ResolvePath(Path.Combine("ThirdParty", "DOSBox", "dosbox.conf"));

        // Ordered (-c) commands; quote = always wrap the payload in quotes.
        var cmds = new List<(string cmd, bool quote)>();
        if (!show) { cmds.Add(("@ECHO OFF", true)); cmds.Add(("CLS", false)); }
        foreach (var m in SafeMounts(game)) { var mc = MountCmd(m); if (mc != null) cmds.Add((mc, true)); }
        cmds.Add(($"MOUNT C '{rootAbs}'", true));
        cmds.Add(("C:", false));
        if (!show) cmds.Add(("CLS", false));
        cmds.Add(("CD " + relDir, true));   // empty relDir → "CD " (app at root), matches LB
        cmds.Add((entry, true));                 // entry always quoted (e.g. "INSTALL.EXE")
        if (exit)
        {
            if (pauseExit) cmds.Add(("@PAUSE", false));
            cmds.Add(("EXIT", false));
        }

        var sb = new StringBuilder();
        foreach (var (cmd, quote) in cmds)
        {
            if (pauseEach) sb.Append("-c @PAUSE ");
            sb.Append("-c ");
            sb.Append(quote || cmd.Contains(' ') ? $"\"{cmd}\"" : cmd);
            sb.Append(' ');
        }
        sb.Append($"-noautoexec -noconsole -conf \"{conf}\"");
        return sb.ToString();
    }

    /// <summary>One DOSBox mount command: folder → MOUNT, disk image → IMGMOUNT.</summary>
    private static string MountCmd(IMount m)
    {
        string path = ResolvePath(SafeStr(() => m.Path));
        if (string.IsNullOrEmpty(path)) return null;
        char drive = 'C'; try { drive = m.DriveLetter; } catch { }
        if (SafeStr(() => m.MountType).Equals("Folder", StringComparison.OrdinalIgnoreCase))
            return $"MOUNT {drive} '{path}'";

        string type = SafeStr(() => m.Type);
        string fsRaw = SafeStr(() => m.Filesystem);
        string t = (type.IndexOf("ISO", StringComparison.OrdinalIgnoreCase) >= 0 || type.IndexOf("CD", StringComparison.OrdinalIgnoreCase) >= 0) ? "iso"
                 : type.IndexOf("Floppy", StringComparison.OrdinalIgnoreCase) >= 0 ? "floppy" : "hdd";
        string fs = fsRaw.Equals("ISO", StringComparison.OrdinalIgnoreCase) ? "iso"
                  : fsRaw.Equals("FAT", StringComparison.OrdinalIgnoreCase) ? "fat"
                  : (string.IsNullOrEmpty(fsRaw) ? "iso" : fsRaw.ToLowerInvariant());
        return $"IMGMOUNT {drive} '{path}' -t {t} -fs {fs}";
    }

    private static IEnumerable<IMount> SafeMounts(IGame game)
    {
        try { return game.GetAllMounts() ?? Array.Empty<IMount>(); }
        catch { return Array.Empty<IMount>(); }
    }

    /// <summary>Reads the global DOSBox options from Settings.xml (fresh each launch).</summary>
    private static (bool show, bool exit, bool pauseEach, bool pauseExit) DosBoxOpts()
    {
        bool show = false, exit = true, pe = false, px = false;
        try
        {
            string f = Path.Combine(_lbRoot ?? ".", "Data", "Settings.xml");
            if (File.Exists(f))
            {
                var s = XDocument.Load(f).Root?.Element("Settings");
                bool B(string k, bool d) { var v = (string)s?.Element(k); return v == null ? d : v.Equals("true", StringComparison.OrdinalIgnoreCase); }
                show = B("ShowCommands", false);
                exit = B("ExitDosBox", true);
                pe = B("PauseBeforeCommands", false);
                px = B("PauseBeforeExit", false);
            }
        }
        catch { }
        return (show, exit, pe, px);
    }

    /// <summary>Runs the game's Configuration Application (DOSBox-aware). Fire-and-forget; returns the config path.</summary>
    public static string RunConfigTool(IGame game)
    {
        string cfg = SafeStr(() => game.ConfigurationPath);
        if (string.IsNullOrEmpty(cfg)) return null;
        bool useDos = SafeBool(() => game.UseDosBox);
        string extra = SafeStr(() => game.ConfigurationCommandLine);
        // Working directory: there's no dedicated setting for the config tool, so — when a valid
        // Root Folder exists AND the config exe lives in the SAME folder as the game's exe (a
        // Setup.exe next to the game shares its launch context) — use the Root Folder, like a
        // direct game launch would. A config tool living elsewhere (external utility) runs in its
        // own folder (Spawn's default). DOSBox config launches are untouched.
        string cfgWorkDir = null;
        try
        {
            var rfd = RootFolderDir(game);
            if (rfd != null)
            {
                string gameDir = SafeDir(ResolvePath(SafeStr(() => game.ApplicationPath)));
                string cfgDir = SafeDir(ResolvePath(cfg));
                if (!string.IsNullOrEmpty(gameDir) && !string.IsNullOrEmpty(cfgDir)
                    && string.Equals(Path.GetFullPath(gameDir), Path.GetFullPath(cfgDir), StringComparison.OrdinalIgnoreCase))
                    cfgWorkDir = rfd;
            }
        }
        catch { }
        var t = new Thread(() =>
        {
            try
            {
                if (useDos) Spawn(DosBoxExe(game), BuildDosBoxArgs(game, cfg, extra), "configure");
                else Spawn(ResolvePath(cfg), extra?.Trim() ?? "", "configure", workDir: cfgWorkDir);
            }
            catch (Exception ex) { Console.WriteLine("[configure] error: " + ex.Message); }
        })
        { IsBackground = true, Name = "LiteBox-configure" };
        t.Start();
        return cfg;
    }

    /// <summary>Spawns a process (or logs it in DryRun) and waits for exit.</summary>
    private static void Spawn(string fileName, string args, string label, Action<Process> onSpawned = null, bool hideConsole = false, string workDir = null)
    {
        if (string.IsNullOrEmpty(fileName)) return;
        if (DryRun) { Console.WriteLine($"[launch/dry] {label}: \"{fileName}\" {args}"); return; }

        Console.WriteLine($"[launch] {label}: \"{fileName}\" {args}" + (workDir != null ? $" (cwd={workDir})" : ""));
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = hideConsole,   // LB's "Attempt to hide console" — suppress the child console window
                WorkingDirectory = workDir ?? SafeDir(fileName) ?? _lbRoot ?? AppContext.BaseDirectory,
            };
            using var proc = Process.Start(psi);
            if (proc != null && onSpawned != null) { try { onSpawned(proc); } catch { } }
            proc?.WaitForExit();
        }
        catch (Exception ex) { Console.WriteLine($"[launch] {label} error: {ex.Message}"); }
    }

    private static IEnumerable<IAdditionalApplication> SafeAddApps(IGame game)
    {
        try
        {
            var all = game.GetAllAdditionalApplications();
            if (all == null) return Array.Empty<IAdditionalApplication>();
            // Documents (Section=="Document") are add-app records too, but never launchable apps/discs — every
            // caller here means real apps (autorun-before/after) or discs (TryBuildM3u). Without this, TryBuildM3u
            // would add a document file (a PDF, …) as a bogus disc in a multi-disc game's M3U playlist.
            return all.Where(a => a is not Data.HostAdditionalApplication { IsDocument: true }).ToArray();
        }
        catch { return Array.Empty<IAdditionalApplication>(); }
    }

    private static string SafeGameCommandLine(IGame game)
    {
        try { return game.CommandLine ?? ""; } catch { return ""; }
    }

    private static bool SafeBool(Func<bool> f) { try { return f(); } catch { return false; } }
    private static string SafeStr(Func<string> f) { try { return f() ?? ""; } catch { return ""; } }
    private static int? SafeNullableInt(Func<int?> f) { try { return f(); } catch { return null; } }

    private static string ResolvePath(string p)
    {
        if (string.IsNullOrEmpty(p) || Path.IsPathRooted(p)) return p;
        try { return Path.GetFullPath(Path.Combine(_lbRoot ?? AppContext.BaseDirectory, p)); } catch { return p; }
    }

    private static string SafeDir(string fullPath)
    {
        try { return Path.GetDirectoryName(fullPath); } catch { return null; }
    }

    private static void Fire(Action<IGameLaunchingPlugin> a)
    {
        if (_reg == null) return;
        foreach (var p in _reg.GameLaunching)
        {
            try { a(p); } catch (Exception ex) { Console.WriteLine("[launch] plugin error: " + ex.Message); }
        }
    }
}
