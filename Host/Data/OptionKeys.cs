// The REGISTRY of every key that may live in litebox-options.db — the single place the option-db
// namespace is defined. LiteBoxOptionsDb validates every Get/Set against it: an undeclared (scope, key)
// pair LOGS in normal runs ("[options-db] unknown key") and THROWS under --debug, so a typo'd key can no
// longer silently create an orphan row or an override that never resolves (the tri-state "no row =
// inherit" made that class of bug invisible).
//
// Each entry also declares:
//   • Type — how typed accessors parse the value (declarative; RAW string semantics are unchanged and
//     legacy values are never rejected: validation is on the NAMESPACE, not the values);
//   • Cache — Hot keys are pre-loaded into RAM at Open() and served from a write-through dictionary
//     (used on list/search/detail paths where latency matters); Cold keys hit the DB on demand (launch
//     -time and punctual reads — a few indexed lookups are irrelevant there). DECISION RULE: a key is
//     Hot only when it is read while rendering a game list, a search, or the detail pane. Cold is the
//     default. Note: Cold also means cross-process writes (ExtendDB under real LB) are always seen
//     fresh — which is why FieldLocks, the shared-with-plugin key, must stay Cold.
//   • Owner — the subsystem that reads/writes it (documentation; where to look);
//   • SharedWithPlugin — the value format is a cross-repo contract (ExtendDB reads/writes the same
//     rows with direct SQL); change the format only with a coordinated plugin change.
//
// Adding a key = adding ONE entry here (EAV table — never a schema migration).

#nullable enable

using System;
using System.Collections.Generic;

namespace LbApiHost.Host.Data;

internal enum OptionType { Bool, String, Json }
internal enum OptionCache { Hot, Cold }

internal sealed record OptionKeyDef(
    string Key,
    string[] Scopes,          // allowed scopes ("global", "game", "emulator", "platform", "playlist")
    OptionType Type,
    OptionCache Cache,
    string Owner,
    bool SharedWithPlugin = false,
    string? Note = null);

internal static class OptionKeys
{
    private const string G = LiteBoxOptionsDb.Global;
    private static readonly string[] Glob = { G };
    private static readonly string[] GameEmu = { LiteBoxOption.ScopeGame, LiteBoxOption.ScopeEmulator };
    private static readonly string[] GameEmuGlob = { LiteBoxOption.ScopeGame, LiteBoxOption.ScopeEmulator, G };
    private static readonly string[] Game = { LiteBoxOption.ScopeGame };
    private static readonly string[] Platform = { LiteBoxOption.ScopePlatform };

    public static readonly OptionKeyDef[] All =
    {
        // ── ProblemKeys globals (in the DB ONLY when the detected LB can't host them in Settings.xml;
        //    per-entity overrides of the same names are regular gameplay overrides) ──
        new("StartupScreenPostLaunchDisplayTime", GameEmuGlob, OptionType.String, OptionCache.Hot, "Gameplay/ProblemKeys"),
        new("ShutdownScreenPostReadyDisplayTime", GameEmuGlob, OptionType.String, OptionCache.Hot, "Gameplay/ProblemKeys"),
        new("ForceFrontendFocusOnShutdown",       GameEmuGlob, OptionType.Bool,   OptionCache.Hot, "Gameplay/ProblemKeys"),
        new("MonitorStartupShutdownWithProcess",  GameEmuGlob, OptionType.Bool,   OptionCache.Hot, "Gameplay/ProblemKeys"),

        // ── Module master switches (LbModules; row absent = module default) ──
        new("Module.base",             Glob, OptionType.Bool, OptionCache.Hot, "LbModules"),
        new("Module.rom",              Glob, OptionType.Bool, OptionCache.Hot, "LbModules"),
        new("Module.retroachievements",Glob, OptionType.Bool, OptionCache.Hot, "LbModules"),
        new("Module.parental",         Glob, OptionType.Bool, OptionCache.Hot, "LbModules"),
        new("Module.web",              Glob, OptionType.Bool, OptionCache.Hot, "LbModules"),

        // ── Gameplay PER-ENTITY overrides (tri-state: no row = inherit; game → emulator → GLOBAL) ──
        // ONLY the per-entity tiers (game/emulator) live in the DB — that's what the EAV store is for.
        // The GLOBAL default of each lives in LiteBox.ini, seeded there with a visible value by
        // GameplayDefaults (no hidden keys). So a resolution reads the DB for the two entity tiers and
        // the ini for the global fallback — each store doing what it's best at, NOT a problematic split.
        // Cold: read when resolving a launch, never on a list/search/detail path.
        // (SmartCaptureShowBorder is declared like its siblings so the resolver's per-key probe of the
        //  entity tiers stays legal even though no editor writes a per-entity value for it.)
        // (SmartCaptureIgnoreExes and the StartupStayOnTop.<category> global defaults are ini-ONLY —
        //  no per-entity tier — so they are NOT declared here.)
        new("StartupStayOnTop",         GameEmu, OptionType.Bool,   OptionCache.Cold, "Gameplay"),
        new("ExitScreenEagerMs",        GameEmu, OptionType.String, OptionCache.Cold, "Gameplay", Note: "int ms, or -1 = disabled"),
        new("PauseHotkey",              GameEmu, OptionType.String, OptionCache.Cold, "Pause"),
        new("ScreenCaptureKey",         GameEmu, OptionType.String, OptionCache.Cold, "Gameplay", Note: "\"None\" sentinel = explicitly disabled"),
        new("PadPauseEnabled",          GameEmu, OptionType.Bool,   OptionCache.Cold, "Pause"),
        new("PadPauseButton",           GameEmu, OptionType.String, OptionCache.Cold, "Pause"),
        new("PauseTarget",              GameEmu, OptionType.String, OptionCache.Cold, "Pause", Note: "smartcapture | process"),
        new("PauseFreezeTree",          GameEmu, OptionType.Bool,   OptionCache.Cold, "Pause"),
        new("PauseScreenFreezeTiming",  GameEmu, OptionType.String, OptionCache.Cold, "Pause", Note: "before | after"),
        new("PauseScreenFreezeOffsetMs",GameEmu, OptionType.String, OptionCache.Cold, "Pause", Note: "int ms 0..5000"),
        new("PauseExitKill",            GameEmu, OptionType.String, OptionCache.Cold, "Pause", Note: "none | smartcapture:<s> | process:<s>"),
        new("PauseExitKillAhk",         GameEmu, OptionType.String, OptionCache.Cold, "Pause", Note: "same encoding as PauseExitKill"),
        new("SmartCaptureEnabled",          GameEmu, OptionType.Bool,   OptionCache.Cold, "SmartCapture"),
        new("SmartCaptureUseFps",           GameEmu, OptionType.Bool,   OptionCache.Cold, "SmartCapture"),
        new("SmartCaptureUseSize",          GameEmu, OptionType.Bool,   OptionCache.Cold, "SmartCapture"),
        new("SmartCaptureCombine",          GameEmu, OptionType.String, OptionCache.Cold, "SmartCapture", Note: "and | or"),
        new("SmartCaptureMinFps",           GameEmu, OptionType.String, OptionCache.Cold, "SmartCapture"),
        new("SmartCaptureSustainMs",        GameEmu, OptionType.String, OptionCache.Cold, "SmartCapture"),
        new("SmartCaptureMinSizePct",       GameEmu, OptionType.String, OptionCache.Cold, "SmartCapture"),
        new("SmartCaptureTitle",            GameEmu, OptionType.String, OptionCache.Cold, "SmartCapture"),
        new("SmartCaptureStopOnWindowClose",GameEmu, OptionType.Bool,   OptionCache.Cold, "SmartCapture"),
        new("SmartCaptureShowBorder",       GameEmu, OptionType.Bool,   OptionCache.Cold, "SmartCapture",
            Note: "global default seeded in LiteBox.ini; per-entity tier declared only so the resolver probe is legal."),

        // ── Per-game DATA (not option overrides) ──
        // "Requires parental rights" per-game flag. LiteBox is the sole writer (Edit Game); read on the
        // visibility hot path while parental is locked, and the whole blocked-ID set is exported to the
        // native ASI (which can't read this SQLite store). See Host/Parental/ParentalGameFlag.
        new("ParentalBlocked", Game, OptionType.Bool, OptionCache.Hot, "Parental/ParentalGameFlag",
            Note: "per-game 'requires parental rights' flag; blocked-ID set exported to the ASI."),
        new("FieldLocks", Game, OptionType.Json, OptionCache.Cold, "Editor/LockStore", SharedWithPlugin: true,
            Note: "JSON {column:lockedValue}; columns = ExtendDB LockStorage.AllColumns. MUST stay Cold: plugin writes must always be seen fresh."),
        new("Model3dImages", Game, OptionType.Json, OptionCache.Hot, "Model3d/Model3dImageStore",
            Note: "JSON {slot:LB-root-relative path}; read on EVERY game selection (3D block key resolution) + library sweep."),
        new("RomSelection", Game, OptionType.Json, OptionCache.Hot, "RomSelectionStore",
            Note: "JSON {verKey:{rom,force}}; read at detail display (Play seeding). Desktop-client state (web keeps localStorage)."),

        // ── Per-platform (entity_id = platform NAME — LB platforms have no guid) ──
        new("RaConsoleKey", Platform, OptionType.String, OptionCache.Hot, "Ra/RaPlatformMap",
            Note: "RAHasher console key; \"-\" sentinel = explicitly none (empty would delete the row = inherit auto)."),
    };

    private static Dictionary<string, OptionKeyDef>? _byKey;
    private static Dictionary<string, OptionKeyDef> ByKey()
    {
        if (_byKey != null) return _byKey;
        var d = new Dictionary<string, OptionKeyDef>(StringComparer.Ordinal);
        foreach (var k in All) d[k.Key] = k;
        return _byKey = d;
    }

    public static OptionKeyDef? Find(string key) => ByKey().TryGetValue(key ?? "", out var d) ? d : null;

    /// <summary>Declared and allowed in this scope. Undeclared pairs are LOGGED (and THROW under --debug)
    /// by LiteBoxOptionsDb — never silently accepted.</summary>
    public static bool IsDeclared(string scope, string key)
        => Find(key) is { } d && Array.IndexOf(d.Scopes, scope) >= 0;

    /// <summary>The Hot set for one scope — what Open() pre-loads.</summary>
    public static IEnumerable<string> HotKeys(string scope)
    {
        foreach (var d in All)
            if (d.Cache == OptionCache.Hot && Array.IndexOf(d.Scopes, scope) >= 0)
                yield return d.Key;
    }

    public static readonly string[] AllScopes =
    { LiteBoxOptionsDb.Global, LiteBoxOption.ScopeGame, LiteBoxOption.ScopeEmulator, LiteBoxOption.ScopePlatform, "playlist" };
}
