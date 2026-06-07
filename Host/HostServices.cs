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
        Console.WriteLine($"[launch/{who}] {game.Title}  emu={emulator?.Title ?? "(none)"}  app={app?.Name ?? "(none)"}{(DryRun ? "  (dry)" : "")}");

        // 0. notify the GUI (it may show a "game running" screen / unload its list)
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
            Mem.Trim();
            Mem.Report("after drop+trim (launch)");
        }
        catch { }

        // 3. run + wait on a worker thread so the UI/web stay responsive
        var t = new Thread(() => RunAndWait(game, app, emulator, overrideCmd)) { IsBackground = true, Name = "LbApiHost-game" };
        t.Start();
    }

    private static void RunAndWait(IGame game, IAdditionalApplication app, IEmulator emulator, string overrideCmd)
    {
        // Play tracking (our user-state → journal): a real launch bumps play count +
        // last-played now; on exit we add the elapsed seconds to play time. Skipped in DryRun.
        int gi = GameIndex(game);
        var sw = Stopwatch.StartNew();
        if (!DryRun && gi >= 0) { try { _store.JournalPlayStart(gi); } catch { } }
        try
        {
            Fire(p => p.OnAfterGameLaunched(game, app, emulator));

            var addApps = SafeAddApps(game);

            // AutoRunBefore additional apps (scripts, mounts, …).
            foreach (var a in addApps.Where(a => a.AutoRunBefore))
                RunProcess(a.ApplicationPath, a.CommandLine, emulator, game, a.UseEmulator, $"autorun-before \"{a.Name}\"");

            // Main target: built-in DOSBox, an explicit additional-app, or the game.
            bool useDos = SafeBool(() => app != null ? app.UseDosBox : game.UseDosBox);
            string target = !string.IsNullOrEmpty(SafeStr(() => app?.ApplicationPath))
                ? app.ApplicationPath : SafeStr(() => game.ApplicationPath);

            if (useDos && !string.IsNullOrEmpty(target))
            {
                RunDosBox(game, target, "dosbox");   // Spawn() handles DryRun
            }
            else
            {
                var main = ResolveMain(game, app, emulator, overrideCmd);
                if (main.HasValue)
                    RunProcess(main.Value.path, main.Value.args, emulator, game, main.Value.useEmu, "main");
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
            if (!DryRun && gi >= 0) { try { _store.JournalPlayTime(gi, (int)sw.Elapsed.TotalSeconds); } catch { } }
            Fire(p => p.OnGameExited());
            try { _store?.ReloadOptional(); Mem.Report("after ReloadOptional (exit)"); } catch { }
            try { if (Gc.HostGameCache.Enabled && Gc.HostGameCache.UnloadDuringGame) { Gc.HostGameCache.Reload(); Console.WriteLine("[gamecache] rebuilding after game exit"); } } catch { }
            // GUI: game over + data reloaded → reload its list and restore selection.
            try { GameEnded?.Invoke(game); } catch { }
        }
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
    private static void RunProcess(string targetPath, string cmd, IEmulator emulator, IGame game, bool useEmu, string label)
    {
        if (string.IsNullOrEmpty(targetPath)) return;

        string fileName, args;
        if (useEmu && emulator != null && !string.IsNullOrEmpty(emulator.ApplicationPath))
        {
            string emuCmd = string.IsNullOrWhiteSpace(cmd) ? EmulatorCmdFor(emulator, game) : cmd;
            fileName = ResolvePath(emulator.ApplicationPath);
            args = (string.IsNullOrWhiteSpace(emuCmd) ? "" : emuCmd.Trim() + " ") + "\"" + ResolvePath(targetPath) + "\"";
        }
        else
        {
            fileName = ResolvePath(targetPath);   // direct launch (PC, TeknoParrot, scripts)
            args = cmd?.Trim() ?? "";
        }
        Spawn(fileName, args, label);
    }

    /// <summary>
    /// LaunchBox's built-in DOSBox launch (LB\ThirdParty\DOSBox\DOSBox.exe): mount the
    /// game's folder as C:, CALL the entry file, with the per-game .conf (or the default
    /// dosbox.conf). Mirrors LB's exact command line.
    /// </summary>
    private static void RunDosBox(IGame game, string targetPath, string label)
        => Spawn(DosBoxExe(game), BuildDosBoxArgs(game, targetPath, SafeStr(() => game.CommandLine)), label);

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
        var t = new Thread(() =>
        {
            try
            {
                if (useDos) Spawn(DosBoxExe(game), BuildDosBoxArgs(game, cfg, extra), "configure");
                else Spawn(ResolvePath(cfg), extra?.Trim() ?? "", "configure");
            }
            catch (Exception ex) { Console.WriteLine("[configure] error: " + ex.Message); }
        })
        { IsBackground = true, Name = "LiteBox-configure" };
        t.Start();
        return cfg;
    }

    /// <summary>Spawns a process (or logs it in DryRun) and waits for exit.</summary>
    private static void Spawn(string fileName, string args, string label)
    {
        if (string.IsNullOrEmpty(fileName)) return;
        if (DryRun) { Console.WriteLine($"[launch/dry] {label}: \"{fileName}\" {args}"); return; }

        Console.WriteLine($"[launch] {label}: \"{fileName}\" {args}");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = args,
                UseShellExecute = false,
                WorkingDirectory = SafeDir(fileName) ?? _lbRoot ?? AppContext.BaseDirectory,
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit();
        }
        catch (Exception ex) { Console.WriteLine($"[launch] {label} error: {ex.Message}"); }
    }

    private static string EmulatorCmdFor(IEmulator emulator, IGame game)
    {
        try
        {
            var eps = emulator.GetAllEmulatorPlatforms();
            var ep = eps?.FirstOrDefault(x => string.Equals(x.Platform, game.Platform, StringComparison.OrdinalIgnoreCase))
                  ?? eps?.FirstOrDefault(x => x.IsDefault);
            return ep?.CommandLine ?? emulator.CommandLine ?? "";
        }
        catch { return ""; }
    }

    private static IEnumerable<IAdditionalApplication> SafeAddApps(IGame game)
    {
        try { return game.GetAllAdditionalApplications() ?? Array.Empty<IAdditionalApplication>(); }
        catch { return Array.Empty<IAdditionalApplication>(); }
    }

    private static string SafeGameCommandLine(IGame game)
    {
        try { return game.CommandLine ?? ""; } catch { return ""; }
    }

    private static bool SafeBool(Func<bool> f) { try { return f(); } catch { return false; } }
    private static string SafeStr(Func<string> f) { try { return f() ?? ""; } catch { return ""; } }

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
