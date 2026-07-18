// Config-surface support for the RetroAchievements module options panel (Host/Options/Modules/RaPanel.cs).
//
// The panel needs three things the existing RA backends don't already expose publicly, so they live here in
// their OWN additive file (the big RA files — RaPlatformMap / RaScanLite / RaCatalogEngine — stay untouched):
//
//   • RaPanelConfig  — persists the panel's two LiteBox-own settings (the auto-update trigger + the per-
//                      platform "Enabled" flags) to Core\litebox\ra-panel.json, mirroring how RaPlatformMap
//                      persists its overrides. RaPlatformMap only stores platform→console-key overrides; it
//                      has no notion of "enabled" or an update trigger, so those diffs are kept here.
//   • RaPanelActions — the game-gather + scan launcher (RunRaScan is private to MainWindow) and the
//                      Clear-RA-data sweep, plus the frozen RAHasher-key → console-id lookup the grid needs
//                      to show/refresh a row's console id live (RaPlatformMap keeps that table private).
//
// Nothing here is wired into the auto-resolve path; it is config plumbing consumed by the options panel.

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Forms;
using LbApiHost.Host.Data;
using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Ra;

/// <summary>The RetroAchievements options panel's own persisted state — the auto-update trigger and the
/// per-platform Enabled flags — in Core\litebox\ra-panel.json. Only diffs-from-default are stored for the
/// Enabled flags (a platform's default is "enabled when it maps to a console"), same philosophy as
/// RaPlatformMap's override file.</summary>
internal static class RaPanelConfig
{
    public const string ModeOnSelect = "select";
    public const string ModeOnLaunch = "launch";

    private sealed class Model
    {
        public string mode { get; set; } = ModeOnSelect;
        // platform name → explicit enabled state (only the platforms whose state differs from the default).
        public Dictionary<string, bool> enabled { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static readonly object _lock = new();
    private static Model? _model;
    private static string FilePath => LiteBoxPaths.File("ra-panel.json");

    private static Model Get()
    {
        lock (_lock)
        {
            if (_model != null) return _model;
            var m = new Model();
            try
            {
                if (File.Exists(FilePath))
                {
                    var j = JsonSerializer.Deserialize<Model>(File.ReadAllText(FilePath));
                    if (j != null)
                    {
                        m.mode = string.Equals(j.mode, ModeOnLaunch, StringComparison.OrdinalIgnoreCase) ? ModeOnLaunch : ModeOnSelect;
                        if (j.enabled != null)
                            foreach (var kv in j.enabled)
                                if (!string.IsNullOrWhiteSpace(kv.Key)) m.enabled[kv.Key.Trim()] = kv.Value;
                    }
                }
            }
            catch { }
            _model = m;
            return m;
        }
    }

    /// <summary>The stored auto-update trigger ("select" or "launch").</summary>
    public static string Mode => Get().mode;

    /// <summary>Effective enabled state for a platform: the stored diff if present, else the caller's default.</summary>
    public static bool IsEnabled(string platform, bool def)
    {
        if (string.IsNullOrWhiteSpace(platform)) return def;
        var m = Get();
        return m.enabled.TryGetValue(platform.Trim(), out var v) ? v : def;
    }

    /// <summary>Replaces the whole panel state and persists it. Pass only the enabled diffs (platforms whose
    /// checkbox differs from their default).</summary>
    public static void Save(string mode, IDictionary<string, bool> enabledDiffs)
    {
        lock (_lock)
        {
            var m = new Model { mode = string.Equals(mode, ModeOnLaunch, StringComparison.OrdinalIgnoreCase) ? ModeOnLaunch : ModeOnSelect };
            if (enabledDiffs != null)
                foreach (var kv in enabledDiffs)
                    if (!string.IsNullOrWhiteSpace(kv.Key)) m.enabled[kv.Key.Trim()] = kv.Value;
            _model = m;
            try { File.WriteAllText(FilePath, JsonSerializer.Serialize(m)); } catch { }
        }
    }
}

/// <summary>Game-gather + scan launcher (mirrors MainWindow.RunRaScan, which is private) and the
/// Clear-RA-data sweep, for the RA options panel. Uses PluginHelper.DataManager directly.</summary>
internal static class RaPanelActions
{
    public const string AllPlatforms = "(All platforms)";

    // Frozen RAHasher console KEY → numeric console id — a verbatim mirror of RaPlatformMap's private
    // KeyToId table (kept private there). Needed so the grid can show/refresh a row's console id from the
    // key the user picked, before any override is saved. Console ids are stable (RC_CONSOLE_* values), so
    // this rarely changes; re-copy it from RaPlatformMap if that table is ever refreshed.
    private static readonly Dictionary<string, int> KeyToId = new(StringComparer.OrdinalIgnoreCase)
    {
        { "NES", 7 }, { "FDS", 81 }, { "SNES", 3 }, { "N64", 2 }, { "GC", 16 }, { "Wii", 19 },
        { "GB", 4 }, { "GBC", 6 }, { "GBA", 5 }, { "DS", 18 }, { "DSi", 78 }, { "MINI", 24 },
        { "VB", 28 }, { "G&W", 60 }, { "3DS", 62 }, { "WiiU", 20 },
        { "PS1", 12 }, { "PS2", 21 }, { "PSP", 41 },
        { "2600", 25 }, { "7800", 51 }, { "JAG", 17 }, { "JCD", 77 }, { "Lynx", 13 }, { "5200", 50 }, { "AST", 36 },
        { "SG1K", 33 }, { "SMS", 11 }, { "MD", 1 }, { "SCD", 9 }, { "32X", 10 }, { "SAT", 39 },
        { "DC", 40 }, { "GG", 15 }, { "Pico", 68 },
        { "80/88", 47 }, { "PCE", 8 }, { "PCCD", 76 }, { "PC-FX", 49 }, { "9800", 48 },
        { "NGCD", 56 }, { "NGP", 14 },
        { "3DO", 43 }, { "CPC", 37 }, { "A2", 38 }, { "ARC", 27 }, { "A2001", 73 }, { "ARD", 71 },
        { "CV", 44 }, { "ELEK", 75 }, { "CHF", 57 }, { "INTV", 45 }, { "VC4000", 74 }, { "MO2", 23 },
        { "DUCK", 69 }, { "MSX", 29 }, { "UZE", 80 }, { "VECT", 46 }, { "WASM4", 72 }, { "WSV", 63 },
        { "WS", 53 }, { "Amiga", 35 }, { "ESCV", 55 }, { "C64", 30 }, { "FMTowns", 58 }, { "N-Gage", 61 },
        { "Oric", 32 }, { "CD-i", 42 }, { "X1", 64 }, { "X68K", 52 }, { "TO8", 66 }, { "TI83", 79 },
        { "TIC-80", 65 }, { "VIC-20", 34 }, { "Zeebo", 70 }, { "ZX81", 31 }, { "ZXS", 59 },
        { "DOS", 26 }, { "Xbox", 22 },
    };

    /// <summary>Numeric RA console id for a RAHasher key (0 when the key is empty/unknown).</summary>
    public static int ConsoleIdForKey(string? key)
        => (!string.IsNullOrEmpty(key) && KeyToId.TryGetValue(key!, out var id)) ? id : 0;

    /// <summary>All LB platform names, distinct + sorted (case-insensitive).</summary>
    public static List<string> PlatformNames()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>();
        try
        {
            var plats = PluginHelper.DataManager?.GetAllPlatforms();
            if (plats != null)
                foreach (var p in plats)
                {
                    string? n = null; try { n = p?.Name; } catch { }
                    if (!string.IsNullOrWhiteSpace(n) && seen.Add(n!.Trim())) names.Add(n!.Trim());
                }
        }
        catch { }
        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    /// <summary>Every game of a platform (null/AllPlatforms → the whole library). When
    /// <paramref name="honorEnabled"/> is set, platforms the user disabled in the panel are skipped
    /// (only meaningful for the whole-library case).</summary>
    public static List<IGame> GatherGames(string? platform, bool honorEnabled)
    {
        bool all = string.IsNullOrEmpty(platform) || platform == AllPlatforms;
        var list = new List<IGame>();
        try
        {
            foreach (var p in PluginHelper.DataManager?.GetAllPlatforms() ?? Array.Empty<IPlatform>())
            {
                if (p == null) continue;
                string? name = null; try { name = p.Name; } catch { }
                if (!all && !string.Equals(name, platform, StringComparison.OrdinalIgnoreCase)) continue;
                if (all && honorEnabled && !string.IsNullOrWhiteSpace(name)
                    && !RaPanelConfig.IsEnabled(name!, RaPlatformMap.ConsoleIdFor(name) != null))
                    continue;
                IGame[]? gs = null; try { gs = p.GetAllGames(true, true); } catch { }
                if (gs != null) foreach (var g in gs) if (g != null) list.Add(g);
            }
        }
        catch { }
        return list;
    }

    /// <summary>Runs the modal RA scan (lite or full) over a platform (or the whole enabled library) and
    /// flushes the write-back journal. Mirrors MainWindow.RunRaScan. Returns false when there was nothing
    /// to scan.</summary>
    public static bool RunScan(IWin32Window? owner, string? platform, bool full)
    {
        // Module gate (plugin parity): the tab is reachable with the module off, but every RA action
        // honours the flag — a manual scan must not hash while the module is disabled.
        if (!Modules.LbModules.On(Modules.LbModule.RetroAchievements))
        {
            try
            {
                MessageBox.Show(owner, "The RetroAchievements module is disabled — enable it in the Modules grid first.",
                    "RetroAchievements", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch { }
            return false;
        }
        var games = GatherGames(platform, honorEnabled: string.IsNullOrEmpty(platform) || platform == AllPlatforms);
        if (games.Count == 0) return false;
        try
        {
            using var f = new RaScanProgress(games, full, string.IsNullOrEmpty(platform) ? AllPlatforms : platform!);
            if (owner != null) f.ShowDialog(owner); else f.ShowDialog();
        }
        finally { Flush(); }
        return true;
    }

    /// <summary>Force-refetches the catalogue for a set of consoles (background-safe; call off the UI
    /// thread). Returns how many refreshed successfully.</summary>
    public static int RefreshConsoles(IEnumerable<int> consoleIds)
    {
        int ok = 0;
        foreach (var id in consoleIds.Where(i => i > 0).Distinct())
            try { if (RaCatalogEngine.RefreshOne(id)) ok++; } catch { }   // engine: guards + store + IGame sync
        return ok;
    }

    /// <summary>Wipes the RetroAchievements hash + id from every game (so they re-resolve), KEEPING the
    /// downloaded catalogue. Returns the number of games cleared. BLOCKING — call off the UI thread.</summary>
    public static int ClearRaData()
    {
        int n = 0;
        try
        {
            foreach (var g in GatherGames(null, honorEnabled: false))
            {
                if (g is not ILiteBoxFields f) continue;
                bool had = false;
                try
                {
                    if (!string.IsNullOrEmpty(f.GetField("RetroAchievementsHash"))) { f.SetField("RetroAchievementsHash", ""); had = true; }
                    if (!string.IsNullOrEmpty(f.GetField("RetroAchievementsId"))) { f.SetField("RetroAchievementsId", ""); had = true; }
                }
                catch { }
                if (had) n++;
            }
        }
        catch { }
        Flush();
        return n;
    }

    /// <summary>Opens a console's games page on retroachievements.org in the default browser.</summary>
    public static void OpenConsoleGames(int consoleId)
    {
        if (consoleId <= 0) return;
        try { Process.Start(new ProcessStartInfo { FileName = $"https://retroachievements.org/gameList.php?c={consoleId}", UseShellExecute = true }); }
        catch { }
    }

    private static void Flush()
    {
        try { (PluginHelper.DataManager as HostDataManagerXml)?.FlushIfSafe(); } catch { }
    }
}
