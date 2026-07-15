// Tiny dependency-free INI config, stored next to the exe (LiteBox.ini). No JSON,
// no extra packages — just key=value lines (';' / '#' comments, optional [sections]
// are ignored/flattened). A commented default file is written on first run so the
// user can discover the keys.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;

namespace LbApiHost.Host;

internal sealed class LiteBoxConfig
{
    private const StringComparison OIC = StringComparison.OrdinalIgnoreCase;
    private readonly string _path;
    private readonly Dictionary<string, string> _kv = new(StringComparer.OrdinalIgnoreCase);
    // Keys this instance actually CHANGED since load. Save() persists ONLY these (merged into the current
    // on-disk file), so two live instances (e.g. MainWindow._cfg + DependencyCheck's) never clobber each
    // other's keys — each writes back only its own edits.
    private readonly HashSet<string> _dirty = new(StringComparer.OrdinalIgnoreCase);

    public LiteBoxConfig(string path)
    {
        _path = path;
        if (File.Exists(_path)) Load();
        else WriteTemplate();
    }

    /// <summary>LiteBox.ini under Core\litebox\ (the single home for LiteBox-created files).</summary>
    public static LiteBoxConfig LoadForExe() => new LiteBoxConfig(LiteBoxPaths.File("LiteBox.ini"));

    private void Load()
    {
        try
        {
            foreach (var raw in File.ReadAllLines(_path))
            {
                var t = raw.Trim();
                if (t.Length == 0 || t[0] == ';' || t[0] == '#' || t[0] == '[') continue;
                int eq = t.IndexOf('=');
                if (eq <= 0) continue;
                _kv[t.Substring(0, eq).Trim()] = t.Substring(eq + 1).Trim();
            }
        }
        catch { }
    }

    public void Save()
    {
        try
        {
            // MERGE-save: reload the CURRENT on-disk keys, then overlay ONLY the keys THIS instance changed.
            // (Save used to rewrite the whole file from _kv, so a second live instance's Save clobbered any
            // key changed by the first. Now each instance persists just its own edits; untouched keys are
            // preserved from disk — fixes the config-clobber family, e.g. DependencyCheck vs MainWindow._cfg.)
            var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(_path))
                    foreach (var raw in File.ReadAllLines(_path))
                    {
                        var t = raw.Trim();
                        if (t.Length == 0 || t[0] == ';' || t[0] == '#' || t[0] == '[') continue;
                        int eq = t.IndexOf('=');
                        if (eq > 0) merged[t.Substring(0, eq).Trim()] = t.Substring(eq + 1).Trim();
                    }
            }
            catch { }
            foreach (var k in _dirty) merged[k] = _kv.TryGetValue(k, out var v) ? v : "";
            var sb = new StringBuilder();
            sb.AppendLine("; LiteBox configuration");
            foreach (var kv in merged) sb.AppendLine($"{kv.Key}={kv.Value}");
            File.WriteAllText(_path, sb.ToString());
            foreach (var kv in merged) _kv[kv.Key] = kv.Value;   // this instance is now consistent with disk
            _dirty.Clear();
        }
        catch { }
    }

    private void WriteTemplate()
    {
        // Seed defaults + comments so the file is self-documenting.
        _kv["ReadOnly"] = "false";
        _kv["ShowGameRunningScreen"] = "true";
        _kv["UnloadListDuringGame"] = "true";
        _kv["KillStoreLauncherAfterGame"] = "false";
        _kv["KillStoreLauncherEvenIfPreRunning"] = "false";
        _kv["StoreExitFocusFallback"] = "false";
        _kv["UseImageCache"] = "true";
        _kv["UseGameCache"] = "true";
        _kv["UnloadGameCacheDuringGame"] = "true";
        _kv["Use16:9ForMainScreenshot"] = "true";
        _kv["GameRunningText"] = "Game running...";
        _kv["GameRunningColor"] = "#0F0F12";
        _kv["DebugLog"] = "false";
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("; LiteBox configuration");
            sb.AppendLine("; ReadOnly              : never write to the LaunchBox XMLs; favorites/ratings/play");
            sb.AppendLine(";                         changes stay in memory for this run only. Set false to persist.");
            sb.AppendLine("; ShowGameRunningScreen : show a fanart/colour screen while a game runs");
            sb.AppendLine("; UnloadListDuringGame  : free the game list while a game runs, reload after");
        sb.AppendLine("; KillStoreLauncherAfterGame : when a GOG/Steam/Epic/Ubisoft game exits, close the store");
        sb.AppendLine(";                         client (GalaxyClient/Steam/EpicGamesLauncher/UbisoftConnect) ONLY IF");
        sb.AppendLine(";                         this launch started it (a client you already had open is left alone,");
        sb.AppendLine(";                         unless KillStoreLauncherEvenIfPreRunning is on). Off by default.");
        sb.AppendLine("; KillStoreLauncherEvenIfPreRunning : with KillStoreLauncherAfterGame on, ALSO close the store");
        sb.AppendLine(";                         client when it was ALREADY running before the launch (default off =");
        sb.AppendLine(";                         only close an instance LiteBox itself started).");
        sb.AppendLine("; StoreExitFocusFallback: how to detect a GOG/Steam/Epic game has EXITED. Default (false)");
        sb.AppendLine(";                         uses ONLY the game's process under its install folder — robust,");
        sb.AppendLine(";                         works on a 2nd monitor. Set true to ALSO fall back to the window-");
        sb.AppendLine(";                         focus signal when no install-folder process is ever seen (older,");
        sb.AppendLine(";                         flakier; only needed if the install dir can't be resolved).");
            sb.AppendLine("; UseImageCache         : use the shared degraded-thumbnail cache for UI images");
            sb.AppendLine("; UseGameCache          : build & use an in-memory media cache (Everything-backed) when");
            sb.AppendLine(";                          ExtendDB is NOT loaded (ExtendDB's own cache is preferred when present).");
            sb.AppendLine("; UnloadGameCacheDuringGame : free the host game cache while a game runs, rebuild on exit.");
            sb.AppendLine("; Use16:9ForMainScreenshot : reserve a 16:9 area for the main media (true);");
            sb.AppendLine(";                            false reserves a poster-ratio (2:3) area instead.");
            sb.AppendLine("; GameRunningText       : message shown on the running screen");
            sb.AppendLine("; GameRunningColor      : base colour (#RRGGBB) behind the fanart");
            sb.AppendLine("; DebugLog              : write litebox-debug.log (Core\\litebox\\) with the runtime trace.");
            sb.AppendLine(";                         Off by default (no file, zero I/O). Set true to diagnose an issue,");
            sb.AppendLine(";                         then reproduce it (or launch with --debug for a one-off).");
            sb.AppendLine($"ReadOnly={_kv["ReadOnly"]}");
            sb.AppendLine($"ShowGameRunningScreen={_kv["ShowGameRunningScreen"]}");
            sb.AppendLine($"UnloadListDuringGame={_kv["UnloadListDuringGame"]}");
            sb.AppendLine($"KillStoreLauncherAfterGame={_kv["KillStoreLauncherAfterGame"]}");
            sb.AppendLine($"KillStoreLauncherEvenIfPreRunning={_kv["KillStoreLauncherEvenIfPreRunning"]}");
            sb.AppendLine($"StoreExitFocusFallback={_kv["StoreExitFocusFallback"]}");
            sb.AppendLine($"UseImageCache={_kv["UseImageCache"]}");
            sb.AppendLine($"UseGameCache={_kv["UseGameCache"]}");
            sb.AppendLine($"UnloadGameCacheDuringGame={_kv["UnloadGameCacheDuringGame"]}");
            sb.AppendLine($"Use16:9ForMainScreenshot={_kv["Use16:9ForMainScreenshot"]}");
            sb.AppendLine($"GameRunningText={_kv["GameRunningText"]}");
            sb.AppendLine($"GameRunningColor={_kv["GameRunningColor"]}");
            sb.AppendLine($"DebugLog={_kv["DebugLog"]}");
            File.WriteAllText(_path, sb.ToString());
        }
        catch { }
    }

    // ── Raw accessors ────────────────────────────────────────────────────────
    public string Get(string key, string def = null) => _kv.TryGetValue(key, out var v) ? v : def;
    public void Set(string key, string val) { _kv[key] = val ?? ""; _dirty.Add(key); }

    public bool GetBool(string key, bool def)
    {
        var v = Get(key);
        if (v == null) return def;
        return v == "1" || v.Equals("true", OIC) || v.Equals("yes", OIC) || v.Equals("on", OIC);
    }
    public void SetBool(string key, bool val) { _kv[key] = val ? "true" : "false"; _dirty.Add(key); }

    public int GetInt(string key, int def)
        => int.TryParse(Get(key), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : def;
    public void SetInt(string key, int val) { _kv[key] = val.ToString(System.Globalization.CultureInfo.InvariantCulture); _dirty.Add(key); }

    // ── Enabled plugins (LiteBox.ini EnabledPlugins) ───────────────────────────
    // Comma-separated folder names under <LB>\Plugins. KEY ABSENT (null) means
    // "never configured" → the host defaults to enabling every folder present.
    // An empty value means "none enabled".
    public System.Collections.Generic.List<string> GetEnabledPluginsOrNull()
    {
        if (!_kv.ContainsKey("EnabledPlugins")) return null;
        var list = new System.Collections.Generic.List<string>();
        foreach (var p in (Get("EnabledPlugins") ?? "").Split(','))
        {
            var t = p.Trim();
            if (t.Length > 0) list.Add(t);
        }
        return list;
    }
    public void SetEnabledPlugins(System.Collections.Generic.IEnumerable<string> names)
        => Set("EnabledPlugins", string.Join(",", names));

    // ── Typed options ────────────────────────────────────────────────────────
    public bool ReadOnly              { get => GetBool("ReadOnly", false); set => SetBool("ReadOnly", value); }
    public bool ShowGameRunningScreen { get => GetBool("ShowGameRunningScreen", true); set => SetBool("ShowGameRunningScreen", value); }
    public bool UnloadListDuringGame  { get => GetBool("UnloadListDuringGame", true); set => SetBool("UnloadListDuringGame", value); }
    // Close the GOG/Steam/Epic/Ubisoft client after a store game exits — by default only the instance
    // THIS launch started (see KillStoreLauncherEvenIfPreRunning to override).
    public bool KillStoreLauncherAfterGame { get => GetBool("KillStoreLauncherAfterGame", false); set => SetBool("KillStoreLauncherAfterGame", value); }
    // With KillStoreLauncherAfterGame on, also close the client when it was already running before the launch.
    public bool KillStoreLauncherEvenIfPreRunning { get => GetBool("KillStoreLauncherEvenIfPreRunning", false); set => SetBool("KillStoreLauncherEvenIfPreRunning", value); }
    // Store-game exit detection: false (default) = install-folder process only; true = also use the
    // window-focus fallback when no install-folder process is ever seen (older, flakier).
    public bool StoreExitFocusFallback { get => GetBool("StoreExitFocusFallback", false); set => SetBool("StoreExitFocusFallback", value); }
    public bool UseImageCache         { get => GetBool("UseImageCache", true); set => SetBool("UseImageCache", value); }
    public bool UseGameCache          { get => GetBool("UseGameCache", true); set => SetBool("UseGameCache", value); }
    public bool UnloadGameCacheDuringGame { get => GetBool("UnloadGameCacheDuringGame", true); set => SetBool("UnloadGameCacheDuringGame", value); }
    // true → reserve a 16:9 area for the main media; false → a poster-ratio (2:3) area.
    public bool Use169ForMainScreenshot { get => GetBool("Use16:9ForMainScreenshot", true); set => SetBool("Use16:9ForMainScreenshot", value); }
    // RetroAchievements native fallback: at launch, refresh up to 3 console catalogues older than 48h and
    // re-link games that gained a raid (rolling background update). Off by default (a startup network op).
    public bool RaStartupRollingRefresh { get => GetBool("RaStartupRollingRefresh", false); set => SetBool("RaStartupRollingRefresh", value); }
    // Automatic Progress Tracking triggers (the RULES live in LB's Settings.xml; these choose WHEN
    // LiteBox runs them). Boot sweep = whole library in the background at startup (off by default —
    // avoidable cost on huge libraries); on-select = re-evaluate a game while its detail pane loads
    // (cheap: RAM + at most one cached-RA read). Game-exit evaluation is always on.
    public bool ProgressSweepOnBoot   { get => GetBool("ProgressSweepOnBoot", false); set => SetBool("ProgressSweepOnBoot", value); }
    public bool ProgressApplyOnSelect { get => GetBool("ProgressApplyOnSelect", true); set => SetBool("ProgressApplyOnSelect", value); }
    public string GameRunningText     => Get("GameRunningText", "Game running...");
    public Color GameRunningColor     => ParseColor(Get("GameRunningColor", "#0F0F12"), Color.FromArgb(15, 15, 18));

    private static Color ParseColor(string s, Color def)
    {
        if (string.IsNullOrWhiteSpace(s)) return def;
        s = s.Trim();
        try
        {
            if (s.StartsWith("#") && s.Length == 7)
                return Color.FromArgb(
                    Convert.ToInt32(s.Substring(1, 2), 16),
                    Convert.ToInt32(s.Substring(3, 2), 16),
                    Convert.ToInt32(s.Substring(5, 2), 16));
            if (s.Contains(","))
            {
                var p = s.Split(',');
                if (p.Length == 3) return Color.FromArgb(int.Parse(p[0]), int.Parse(p[1]), int.Parse(p[2]));
            }
            var named = Color.FromName(s);
            if (named.IsKnownColor || named.A != 0) return named;
        }
        catch { }
        return def;
    }
}
