// Runs a LaunchBox EMULATOR-INTEGRATION plugin's InstallEmulator() UNMODIFIED under LiteBox.
//
// The obfuscated plugins (ScummVM, RetroArch, Dolphin, …) don't use the SDK IDataManager for install —
// they hit the CORE directly: `new Emulator{…}`, `LocalDbEmulator.Get*AutoHotkeyScript`, and crucially
// `Root.DataManager.GetDefaultEmulatorExistsForPlatform` + `((DataManagerBase)Root.DataManager).Games/Platforms`.
// Under LiteBox `Root.DataManager` is NULL (LiteBox never boots the core data pipeline; it uses its own
// HostDataManagerXml), so those calls throw NullReferenceException and the whole install dies.
//
// Differential probing (LaunchBox golden vs LiteBox) proved: the obfuscated Unbroken.LaunchBox.Windows.dll IS
// loaded under LiteBox (`new Emulator()`, the LocalDb statics work); only the STATEFUL `Root.DataManager`
// singleton is missing; `new DataManager(bare:true, settingsOnly:false)` builds a functional EMPTY manager,
// and `Root.DataManager` has a public setter → we can inject one for the duration of the call.
//
// STRATEGY (option "B" — generic shim): before the call we build a bare core DataManager and POPULATE it
// with LIGHT MIRRORS of LiteBox's platforms (Name + ScrapeAs) and its eligible games (+ their unassigned
// additional-apps) — mirrors are blank core objects, only the fields the plugin reads are set, so no Notes/
// images/RAM bloat. We inject it (via reflection — LiteBox keeps NO compile-time ref to the obfuscated core),
// let the plugin run its OWN assignment logic (ScrapeAs matching, add-apps, everything) on the mirrors, then
// READ BACK which mirrors it assigned and apply that verdict to the REAL HostGames/add-apps. The plugin — not
// us — decides; we only mirror data in and the result out. Root.DataManager is restored right after the call.

#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Data;
using LbApiHost.Host.Data;

namespace LbApiHost.Host;

/// <summary>Outcome of running a plugin's InstallEmulator against a mirrored library.</summary>
internal sealed class PluginInstallResult
{
    public bool Ok;
    public string Message = "";
    public IEmulator? Core;                                          // the core Emulator the plugin built
    public readonly List<IGame> GamesToAssign = new();              // REAL host games the plugin selected
    public readonly List<IAdditionalApplication> AppsToAssign = new(); // REAL host add-apps the plugin selected
}

internal static class EmuInstall
{
    // Emulator fields LaunchBox persists that the SDK IEmulator interface does NOT expose. Copied core→host
    // through ILiteBoxFields (raw XML element names) so the written <Emulator> node matches LaunchBox 1:1.
    private static readonly string[] ExtraFields =
    {
        "UsePauseScreen", "SuspendProcessOnPause", "ForcefulPauseScreenActivation", "DefaultPauseSettingsPushed",
        "OverrideDefaultPauseScreenSettings", "MonitorStartupShutdownWithProcess", "ForceFrontendFocusOnShutdown",
        "StartupScreenPostLaunchDisplayTime", "ShutdownScreenPostReadyDisplayTime", "SkipVersionCheck",
        "LoginToCheevoOnGameLaunch",
    };

    // ── reflection handles onto the obfuscated core (resolved lazily; null if the DLL isn't present) ──
    private static readonly Type? TRoot = Type.GetType("Unbroken.LaunchBox.Windows.Root, Unbroken.LaunchBox.Windows");
    private static readonly Type? TDataManager = Type.GetType("Unbroken.LaunchBox.Windows.Data.DataManager, Unbroken.LaunchBox.Windows");
    private static PropertyInfo? RootDmProp => TRoot?.GetProperty("DataManager", BindingFlags.Public | BindingFlags.Static);

    /// <summary>True when the obfuscated core is resolvable — i.e. plugin install CAN be shimmed here.</summary>
    public static bool CanShim => TDataManager != null && RootDmProp != null;

    /// <summary>The integration plugin that Supports <paramref name="platform"/>, preferring a Recommended one.</summary>
    public static EmulatorPlugin? FindPlugin(string platform)
    {
        EmulatorPlugin? supported = null;
        foreach (var p in EmuPlugins.All)
        {
            try
            {
                var r = p.IsPlatformSupported(platform);
                if (r == null || !r.Supported) continue;
                if (r.Recommended) return p;
                supported ??= p;
            }
            catch { }
        }
        return supported;
    }

    /// <summary>The integration plugin whose EmulatorName matches <paramref name="name"/> (Add-Emulator "Download").</summary>
    public static EmulatorPlugin? FindPluginByName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        foreach (var p in EmuPlugins.All)
        {
            string en; try { en = p.EmulatorName ?? ""; } catch { en = ""; }
            if (string.Equals(en, name, StringComparison.OrdinalIgnoreCase)) return p;
        }
        return null;
    }

    /// <summary>Run the plugin's InstallEmulator UNMODIFIED against a mirrored library, capture the core
    /// Emulator it builds and the REAL host games/add-apps its own logic assigned. Persists nothing.</summary>
    public static PluginInstallResult RunPluginInstall(
        EmulatorPlugin plugin, string platform,
        Action<string, double>? progress = null, Func<bool>? cancel = null, Action<string>? log = null)
    {
        void L(string s) { try { log?.Invoke(s); } catch { } }
        var r = new PluginInstallResult();
        if (!CanShim) { r.Message = "Obfuscated core (Unbroken.LaunchBox.Windows) not resolvable — cannot shim Root.DataManager."; return r; }
        EnsureLocalDbConfigured(L);   // RetroArch/Dolphin/MAME/PCSX2 read GamesDb (EF) → needs the metadata db path

        object shim;
        try
        {
            var ctor = TDataManager!.GetConstructor(new[] { typeof(bool), typeof(bool) });
            if (ctor == null) { r.Message = "core DataManager(bool,bool) ctor not found"; return r; }
            shim = ctor.Invoke(new object[] { true /*bare*/, false /*settingsOnly*/ });
        }
        catch (Exception ex) { r.Message = "Cannot construct bare core DataManager: " + ex.Message; return r; }

        // Populate the shim with light mirrors, tracking mirror↔host pairs for read-back.
        var gamePairs = new List<(IGame mirror, IGame host)>();
        var appPairs = new List<(IAdditionalApplication mirror, IAdditionalApplication host)>();
        try { PopulateShim(shim, PluginHelper.DataManager, gamePairs, appPairs, L); }
        catch (Exception ex) { L("[emuinstall] shim populate warning (degraded): " + ex.Message); }

        object? prev = null;
        try { prev = RootDmProp!.GetValue(null); } catch { }
        try { RootDmProp!.SetValue(null, shim); }
        catch (Exception ex) { r.Message = "Cannot inject Root.DataManager: " + ex.Message; return r; }
        L($"[emuinstall] running {plugin.EmulatorName}.InstallEmulator(\"{platform}\") on mirrored library…");

        EmulatorInstallResponse? resp = null; Exception? err = null;
        try
        {
            var args = new InstallEmulatorArgs(
                platform,
                (msg, sub, frac) => { try { progress?.Invoke(msg ?? "", frac ?? 0); } catch { } },
                () => { try { return cancel?.Invoke() ?? false; } catch { return false; } },
                existing: null, version: null, createEmulator: true);
            resp = plugin.InstallEmulator(args);
        }
        catch (Exception ex) { err = ex; }

        // Read back BEFORE restoring — the mirrors (and their mutated EmulatorId) are only valid while injected.
        IEmulator? core = (err == null && resp != null && resp.WasSuccess) ? resp.InstalledEmulator : null;
        if (core != null)
        {
            string coreId = Safe(() => core.Id) ?? "";
            foreach (var (m, h) in gamePairs)
                if (string.Equals(Safe(() => m.EmulatorId), coreId, StringComparison.OrdinalIgnoreCase)) r.GamesToAssign.Add(h);
            foreach (var (m, h) in appPairs)
                if (string.Equals(Safe(() => m.EmulatorId), coreId, StringComparison.OrdinalIgnoreCase)) r.AppsToAssign.Add(h);
        }

        try { RootDmProp!.SetValue(null, prev); } catch { }   // surgical restore

        if (err != null) { r.Message = $"{plugin.EmulatorName}.InstallEmulator threw: {err.Message}"; return r; }
        if (core == null) { r.Message = resp?.Message ?? "install failed (no emulator returned)"; return r; }

        r.Ok = true; r.Core = core; r.Message = resp?.Message ?? "ok";
        L($"[emuinstall] plugin OK — core '{Safe(() => core.Title)}'; selected {r.GamesToAssign.Count} game(s) + {r.AppsToAssign.Count} add-app(s)");
        return r;
    }

    /// <summary>Translate the plugin result into a LiteBox HostEmulator: copy every field, copy the platforms
    /// (IsDefault recomputed vs LiteBox data), and apply the plugin's game/add-app assignment verdict — with
    /// OUR host emulator id (the plugin assigned the core emulator's id on the mirrors). Does NOT Save.</summary>
    public static void ApplyToHost(PluginInstallResult r, IEmulator host, Action<string>? log = null)
    {
        void L(string s) { try { log?.Invoke(s); } catch { } }
        if (r.Core == null) return;
        var dm = PluginHelper.DataManager;

        CopyEmulator(r.Core, host, L);

        foreach (var cep in Safe(() => r.Core.GetAllEmulatorPlatforms()) ?? Array.Empty<IEmulatorPlatform>())
        {
            string plat = Safe(() => cep.Platform) ?? ""; if (plat.Length == 0) continue;
            var hep = host.AddNewEmulatorPlatform();
            hep.Platform = plat;
            TrySet(() => hep.CommandLine = Safe(() => cep.CommandLine));
            TrySet(() => hep.M3uDiscLoadEnabled = Safe(() => cep.M3uDiscLoadEnabled));
            hep.IsDefault = !DefaultEmulatorExistsForPlatform(dm, host.Id, plat);
        }

        // Apply the plugin's verdict (real host objects) with the host emulator's id.
        int ng = 0, na = 0;
        foreach (var g in r.GamesToAssign) { TrySet(() => g.EmulatorId = host.Id); ng++; }
        foreach (var a in r.AppsToAssign) { TrySet(() => a.EmulatorId = host.Id); na++; }
        L($"[emuinstall] applied assignment to {ng} game(s) + {na} add-app(s)");
    }

    /// <summary>Full headless install: run the plugin (mirrored), create a HostEmulator, translate, apply, Save.</summary>
    public static (bool ok, string message, string? emulatorId) Install(
        EmulatorPlugin plugin, string platform,
        Action<string, double>? progress = null, Func<bool>? cancel = null, Action<string>? log = null)
    {
        var r = RunPluginInstall(plugin, platform, progress, cancel, log);
        if (!r.Ok || r.Core == null) return (false, r.Message, null);

        var dm = PluginHelper.DataManager;
        IEmulator he = dm.AddNewEmulator();
        ApplyToHost(r, he, log);
        dm.Save(true);
        return (true, $"Installed {plugin.EmulatorName} (id={he.Id}); assigned {r.GamesToAssign.Count} game(s) + {r.AppsToAssign.Count} add-app(s).", he.Id);
    }

    /// <summary>Run plugin.InstallEmulator(args) with the core shimmed (LocalDb configured + a bare Root.DataManager
    /// injected for the call) so an UPDATE / REINSTALL of an EXISTING emulator doesn't NRE. For an existing
    /// emulator the plugin updates ApplicationPath in place and returns early; the empty shim covers any
    /// Root.DataManager touch. The caller builds the args (ExistingEmulator, version, createEmulator:false).</summary>
    public static EmulatorInstallResponse? InstallEmulatorShimmed(EmulatorPlugin plugin, InstallEmulatorArgs args, Action<string>? log = null)
    {
        void L(string s) { try { log?.Invoke(s); } catch { } }
        EnsureLocalDbConfigured(L);
        object? prev = null; object? shim = null;
        if (CanShim)
        {
            try { shim = TDataManager!.GetConstructor(new[] { typeof(bool), typeof(bool) })!.Invoke(new object[] { true, false }); } catch { }
            if (shim != null) { try { prev = RootDmProp!.GetValue(null); } catch { } try { RootDmProp!.SetValue(null, shim); } catch { } }
        }
        try { return plugin.InstallEmulator(args); }
        finally { if (shim != null) { try { RootDmProp!.SetValue(null, prev); } catch { } } }
    }

    /// <summary>Run a plugin call with the core shimmed (LocalDb configured + a bare Root.DataManager injected
    /// for the duration). Used for save-management (GetSaves/AddSaveFile/…): RetroArch null-checks
    /// Root.DataManager so it works raw, but PCSX2/Dolphin read it unchecked and would NRE (silently, inside
    /// SaveManager's try/catch → missing saves). An empty shim satisfies the null-check path; Root.DataManager
    /// is restored right after.</summary>
    public static T WithCoreShim<T>(Func<T> action)
    {
        EnsureLocalDbConfigured(_ => { });
        object? prev = null, shim = null;
        if (CanShim)
        {
            try { shim = TDataManager!.GetConstructor(new[] { typeof(bool), typeof(bool) })!.Invoke(new object[] { true, false }); } catch { }
            if (shim != null) { try { prev = RootDmProp!.GetValue(null); } catch { } try { RootDmProp!.SetValue(null, shim); } catch { } }
        }
        try { return action(); }
        finally { if (shim != null) { try { RootDmProp!.SetValue(null, prev); } catch { } } }
    }

    // ── populate the bare shim with light mirrors ──
    private static void PopulateShim(object shim, IDataManager dm,
        List<(IGame, IGame)> gamePairs, List<(IAdditionalApplication, IAdditionalApplication)> appPairs, Action<string> log)
    {
        var tShim = shim.GetType();
        var mAddGame = tShim.GetMethod("AddNewGame", new[] { typeof(string) });
        if (mAddGame == null) { log("[emuinstall] shim has no AddNewGame — cannot mirror"); return; }
        var srcPlatforms = Safe(() => dm.GetAllPlatforms()) ?? Array.Empty<IPlatform>();

        // Platforms (Name + ScrapeAs) so the plugin's "p.ScrapeAs==dbPlat || p.Name==dbPlat" match works.
        // The bare shim's AddNewPlatform/AddMissingPlatform NRE (obfuscated init path), so inject core Platform
        // objects DIRECTLY into the DataManagerBase.Platforms ConcurrentDictionary via reflection. The 3-arg
        // Platform(name,scrape,folder) ctor also NREs on a bare shim — use the parameterless ctor + setters.
        int nPlat = 0; string? platErr = null;
        try
        {
            var tBase = Type.GetType("Unbroken.LaunchBox.Data.DataManagerBase, Unbroken.LaunchBox");
            object? dict = tBase?.GetProperty("Platforms")?.GetValue(shim);
            var tPlatform = Type.GetType("Unbroken.LaunchBox.Windows.Data.Platform, Unbroken.LaunchBox.Windows");
            const BindingFlags BF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var ctor0 = tPlatform?.GetConstructor(Type.EmptyTypes);
            var setName = tPlatform?.GetMethod("set_Name", BF);
            var setScrape = tPlatform?.GetMethod("set_ScrapeAs", BF);
            var setItem = dict?.GetType().GetMethod("set_Item");
            if (dict != null && ctor0 != null && setName != null && setItem != null)
                foreach (var p in srcPlatforms)
                {
                    string name = Safe(() => p.Name) ?? ""; if (name.Length == 0) continue;
                    string scrape = Safe(() => p.ScrapeAs) ?? "";
                    try
                    {
                        var plat = ctor0.Invoke(null);
                        setName.Invoke(plat, new object[] { name });
                        if (scrape.Length > 0) setScrape?.Invoke(plat, new object[] { scrape });
                        setItem.Invoke(dict, new object[] { name, plat! });
                        nPlat++;
                    }
                    catch (Exception ex) { platErr = ex.InnerException?.Message ?? ex.Message; break; }
                }
            else platErr = "platform-injection reflection unavailable";
        }
        catch (Exception ex) { platErr = ex.InnerException?.Message ?? ex.Message; }
        if (platErr != null) log($"[emuinstall] WARN platform injection incomplete ({nPlat}/{srcPlatforms.Length}): {platErr}");

        // Eligible games: EmulatorId empty OR ≥1 additional-app with EmulatorId empty (the plugin also assigns
        // orphan add-apps of already-assigned games). Mirror the game's real EmulatorId so the plugin skips an
        // already-assigned game but still processes its add-apps.
        int nGame = 0, nApp = 0;
        foreach (var g in dm.GetAllGames())
        {
            string gid = Safe(() => g.EmulatorId) ?? "";
            bool gameEmpty = gid.Length == 0 || gid == Guid.Empty.ToString();
            var freeApps = (Safe(() => g.GetAllAdditionalApplications()) ?? Array.Empty<IAdditionalApplication>())
                .Where(a =>
                {
                    // LB parity: only add-apps that actually launch through an emulator get an EmulatorId.
                    // Add-apps with UseEmulator=false (run directly) are never assigned — the core's EmulatorId
                    // getter returns non-Guid.Empty for them, so the plugin skips them. We must filter here too,
                    // else we'd force Guid.Empty and over-assign (≈900 extra on a full Game Boy set).
                    if (!Safe(() => a.UseEmulator)) return false;
                    var x = Safe(() => a.EmulatorId) ?? "";
                    return x.Length == 0 || x == Guid.Empty.ToString();
                })
                .ToList();
            if (!gameEmpty && freeApps.Count == 0) continue;   // fully assigned → the plugin would ignore it

            IGame mirror;
            try { mirror = (IGame)mAddGame.Invoke(shim, new object[] { Safe(() => g.Title) ?? "g" })!; }
            catch { continue; }
            TrySet(() => mirror.Platform = Safe(() => g.Platform) ?? "");
            // Stamp EmulatorId EXPLICITLY (AddNewGame's own default is NOT reliably Guid.Empty — could be
            // null/""): empty games → Guid.Empty so the plugin's "== Guid.Empty" check fires and assigns;
            // assigned games → their real id so the plugin skips the game but still processes its add-apps.
            TrySet(() => mirror.EmulatorId = gameEmpty ? Guid.Empty.ToString() : gid);
            gamePairs.Add((mirror, g));
            nGame++;

            var mAddApp = mirror.GetType().GetMethod("AddNewAdditionalApplication", Type.EmptyTypes);
            if (mAddApp != null)
                foreach (var a in freeApps)
                {
                    try
                    {
                        if (mAddApp.Invoke(mirror, null) is IAdditionalApplication mA)
                        {
                            TrySet(() => mA.EmulatorId = Guid.Empty.ToString());   // so the plugin's orphan-app check fires
                            appPairs.Add((mA, a)); nApp++;
                        }
                    }
                    catch { }
                }
        }
        log($"[emuinstall] shim populated: {nPlat} platform(s), {nGame} eligible game(s), {nApp} orphan add-app(s)");
    }

    // ── core Emulator → HostEmulator, every field ──
    private static void CopyEmulator(IEmulator s, IEmulator d, Action<string> log)
    {
        TrySet(() => d.Title = s.Title);
        TrySet(() => d.ApplicationPath = s.ApplicationPath);
        TrySet(() => d.CommandLine = s.CommandLine);
        TrySet(() => d.DefaultPlatform = s.DefaultPlatform);
        TrySet(() => d.NoQuotes = s.NoQuotes);
        TrySet(() => d.NoSpace = s.NoSpace);
        TrySet(() => d.HideConsole = s.HideConsole);
        TrySet(() => d.FileNameWithoutExtensionAndPath = s.FileNameWithoutExtensionAndPath);
        TrySet(() => d.AutoExtract = s.AutoExtract);
        TrySet(() => d.UseStartupScreen = s.UseStartupScreen);
        TrySet(() => d.HideAllNonExclusiveFullscreenWindows = s.HideAllNonExclusiveFullscreenWindows);
        TrySet(() => d.StartupLoadDelay = s.StartupLoadDelay);
        TrySet(() => d.HideMouseCursorInGame = s.HideMouseCursorInGame);
        TrySet(() => d.DisableShutdownScreen = s.DisableShutdownScreen);
        TrySet(() => d.AggressiveWindowHiding = s.AggressiveWindowHiding);
        TrySet(() => d.EnableHardcoreAchievements = s.EnableHardcoreAchievements);
        TrySet(() => d.AutoHotkeyScript = s.AutoHotkeyScript);
        TrySet(() => d.PauseAutoHotkeyScript = s.PauseAutoHotkeyScript);
        TrySet(() => d.ResumeAutoHotkeyScript = s.ResumeAutoHotkeyScript);
        TrySet(() => d.ResetAutoHotkeyScript = s.ResetAutoHotkeyScript);
        TrySet(() => d.SaveStateAutoHotkeyScript = s.SaveStateAutoHotkeyScript);
        TrySet(() => d.LoadStateAutoHotkeyScript = s.LoadStateAutoHotkeyScript);
        TrySet(() => d.SwapDiscsAutoHotkeyScript = s.SwapDiscsAutoHotkeyScript);
        TrySet(() => d.ExitAutoHotkeyScript = s.ExitAutoHotkeyScript);

        if (d is ILiteBoxFields lb)
        {
            int extra = 0;
            foreach (var name in ExtraFields)
            {
                var v = ReflectProp(s, name);
                if (!string.IsNullOrEmpty(v)) { lb.SetField(name, v); extra++; }
            }
            if (extra > 0) log($"[emuinstall] copied {extra} extra field(s) via ILiteBoxFields");
        }
    }

    private static bool DefaultEmulatorExistsForPlatform(IDataManager dm, string excludeId, string platform)
    {
        foreach (var e in dm.GetAllEmulators() ?? Array.Empty<IEmulator>())
        {
            if (string.Equals(Safe(() => e.Id), excludeId, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var ep in Safe(() => e.GetAllEmulatorPlatforms()) ?? Array.Empty<IEmulatorPlatform>())
                if (string.Equals(Safe(() => ep.Platform), platform, StringComparison.OrdinalIgnoreCase) && Safe(() => ep.IsDefault))
                    return true;
        }
        return false;
    }

    private static string? ReflectProp(object o, string prop)
    {
        try
        {
            var pi = o.GetType().GetProperty(prop, BindingFlags.Public | BindingFlags.Instance);
            var v = pi?.GetValue(o);
            if (v == null) return null;
            if (v is bool b) return b ? "true" : "false";
            if (v is int i) return i.ToString(CultureInfo.InvariantCulture);
            return v.ToString();
        }
        catch { return null; }
    }

    private static T? Safe<T>(Func<T?> f) { try { return f(); } catch { return default; } }
    private static void TrySet(Action a) { try { a(); } catch { } }

    /// <summary>Point the core's LocalDb EF context (Unbroken.LaunchBox.LocalDb.LocalDbContext) at LaunchBox's
    /// metadata database. LaunchBox sets this static at boot; LiteBox doesn't, so plugins that read GamesDb
    /// (RetroArch/Dolphin/MAME/PCSX2 → GetEmulatorsByNameWithPlatformsAsync) otherwise throw "No database
    /// provider has been configured for this DbContext".</summary>
    private static void EnsureLocalDbConfigured(Action<string> log)
    {
        try
        {
            var t = Type.GetType("Unbroken.LaunchBox.LocalDb.LocalDbContext, Unbroken.LaunchBox.LocalDb");
            var prop = t?.GetProperty("DbFilePath", BindingFlags.Public | BindingFlags.Static);
            if (prop == null) { log("[emuinstall] LocalDbContext.DbFilePath not found"); return; }
            var cur = prop.GetValue(null) as string;
            if (!string.IsNullOrEmpty(cur) && File.Exists(cur)) return;   // already configured
            string root = Media.MediaResolver.LbRoot ?? "";
            string db = Path.Combine(root, "Metadata", "LaunchBox.Metadata.db");
            if (File.Exists(db)) { prop.SetValue(null, db); log($"[emuinstall] configured LocalDbContext.DbFilePath = {db}"); }
            else log($"[emuinstall] LaunchBox.Metadata.db not found at {db}");
        }
        catch (Exception ex) { log("[emuinstall] EnsureLocalDbConfigured: " + ex.Message); }
    }
}
