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

    // ── Sections ─────────────────────────────────────────────────────────────
    // "[Name]" opens a section: keys under it are stored internally as "Name/Key" ('/' never occurs in the
    // historical flat keys, unlike '.', which several root keys already contain — StartupStayOnTop.Store.Gog).
    // Files with no sections parse exactly as before (all keys root). Used for the per-MODULE config blocks
    // ([Base], [Rom], …) so the ini stays readable; root keys remain the LiteBox-global options.

    /// <summary>Reads a whole ini file into a flat dict — section keys become "Section/Key".</summary>
    private static Dictionary<string, string> ParseFile(string path)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in File.ReadAllLines(path))
        {
            var t = raw.Trim();
            if (t.Length == 0 || t[0] == ';' || t[0] == '#') continue;
            if (t[0] == '[') { var end = t.IndexOf(']'); d["\0section"] = end > 1 ? t.Substring(1, end - 1).Trim() : ""; continue; }
            int eq = t.IndexOf('=');
            if (eq <= 0) continue;
            string sec = d.TryGetValue("\0section", out var s) ? s : "";
            string key = t.Substring(0, eq).Trim();
            d[(sec.Length > 0 ? sec + "/" : "") + key] = t.Substring(eq + 1).Trim();
        }
        d.Remove("\0section");
        return d;
    }

    private void Load()
    {
        try { foreach (var kv in ParseFile(_path)) _kv[kv.Key] = kv.Value; }
        catch { }
    }

    /// <summary>A key inside a module section ("[section] key=…"). Same semantics as Get.</summary>
    public string GetSec(string section, string key, string def = null) => Get(section + "/" + key, def);
    public bool GetSecBool(string section, string key, bool def) => GetBool(section + "/" + key, def);
    /// <summary>Set a key inside a module section (persisted under "[section]" by Save).</summary>
    public void SetSec(string section, string key, string value) => Set(section + "/" + key, value);

    public void Save()
    {
        try
        {
            // MERGE-save: reload the CURRENT on-disk keys, then overlay ONLY the keys THIS instance changed.
            // (Save used to rewrite the whole file from _kv, so a second live instance's Save clobbered any
            // key changed by the first. Now each instance persists just its own edits; untouched keys are
            // preserved from disk — fixes the config-clobber family, e.g. DependencyCheck vs MainWindow._cfg.)
            var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try { if (File.Exists(_path)) foreach (var kv in ParseFile(_path)) merged[kv.Key] = kv.Value; }
            catch { }
            foreach (var k in _dirty) merged[k] = _kv.TryGetValue(k, out var v) ? v : "";
            foreach (var k in _removed) merged.Remove(k);   // explicit deletions win over the disk copy
            // Root keys first (historical flat block), then one "[Section]" block per module section.
            var sb = new StringBuilder();
            sb.AppendLine("; LiteBox configuration");
            foreach (var kv in merged) { if (!kv.Key.Contains('/')) sb.AppendLine($"{kv.Key}={kv.Value}"); }
            foreach (var group in merged.Where(kv => kv.Key.Contains('/'))
                                        .GroupBy(kv => kv.Key.Substring(0, kv.Key.IndexOf('/')), StringComparer.OrdinalIgnoreCase)
                                        .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine();
                sb.AppendLine($"[{group.Key}]");
                foreach (var kv in group) sb.AppendLine($"{kv.Key.Substring(kv.Key.IndexOf('/') + 1)}={kv.Value}");
            }
            File.WriteAllText(_path, sb.ToString());
            foreach (var kv in merged) _kv[kv.Key] = kv.Value;   // this instance is now consistent with disk
            _dirty.Clear();
            _removed.Clear();
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
        _kv["Model3dRequireBack"] = "false";
        _kv["Model3dRequireSpine"] = "false";
        _kv["Model3dRequireBothScans"] = "false";
        _kv["Model3dAcceptFullScan"] = "false";
        _kv["GameRunningText"] = "Game running...";
        _kv["GameRunningColor"] = "#0F0F12";
        _kv["SlimScrollDetail"] = "true";
        _kv["SlimScrollNotes"] = "true";
        _kv["SlimScrollTree"] = "true";
        _kv["SlimScrollList"] = "true";
        _kv["PosterSelectionPlate"] = "true";
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
            sb.AppendLine("; Model3dRequire* / Model3dAcceptFullScan : when is a 3D case model worth showing AND");
            sb.AppendLine(";                         baking? The box FRONT is always required (without it the case");
            sb.AppendLine(";                         wears LaunchBox's NoImage placeholder). RequireBack/RequireSpine");
            sb.AppendLine(";                         demand those scans too; with BOTH on, RequireBothScans picks");
            sb.AppendLine(";                         between 'need both' (true) and 'either one' (false).");
            sb.AppendLine(";                         AcceptFullScan: a Box - Full sheet alone is enough (it composes");
            sb.AppendLine(";                         the whole case), for games where full-scan mode applies.");
            sb.AppendLine("; SlimScrollDetail      : the right-hand detail pane uses the thin 3px scrollbar drawn by");
            sb.AppendLine(";                         LiteBox instead of the 17px one Windows imposes (its width is a");
            sb.AppendLine(";                         SYSTEM metric, so a thin one cannot be had any other way). The thin");
            sb.AppendLine(";                         bar overlays the content and widens under the pointer. Set false to");
            sb.AppendLine(";                         go back to the native bars. Takes effect at the next start.");
            sb.AppendLine("; SlimScrollNotes       : same, for the description box under the tabs. Off also brings back");
            sb.AppendLine(";                         the native bar being shown PERMANENTLY there, even over text that fits.");
            sb.AppendLine("; SlimScrollTree        : same, for the platform tree on the left. One switch for both of its");
            sb.AppendLine(";                         bars: they are hidden by the same change, and cannot be split.");
            sb.AppendLine("; SlimScrollList        : same, for the game list in the middle — its horizontal bar only.");
            sb.AppendLine(";                         The list scrolls vertically through the A-Z index rail on its right,");
            sb.AppendLine(";                         which is already a scrollbar of its own.");
            sb.AppendLine("; PosterSelectionPlate  : in poster view, selecting a game colours the tile BACKGROUND and");
            sb.AppendLine(";                         leaves the artwork alone, instead of Windows tinting the whole tile");
            sb.AppendLine(";                         blue. Set false for the plain Windows highlight.");
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
            sb.AppendLine($"Model3dRequireBack={_kv["Model3dRequireBack"]}");
            sb.AppendLine($"Model3dRequireSpine={_kv["Model3dRequireSpine"]}");
            sb.AppendLine($"Model3dRequireBothScans={_kv["Model3dRequireBothScans"]}");
            sb.AppendLine($"Model3dAcceptFullScan={_kv["Model3dAcceptFullScan"]}");
            sb.AppendLine($"SlimScrollDetail={_kv["SlimScrollDetail"]}");
            sb.AppendLine($"SlimScrollNotes={_kv["SlimScrollNotes"]}");
            sb.AppendLine($"SlimScrollTree={_kv["SlimScrollTree"]}");
            sb.AppendLine($"SlimScrollList={_kv["SlimScrollList"]}");
            sb.AppendLine($"PosterSelectionPlate={_kv["PosterSelectionPlate"]}");
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

    /// <summary>Delete a key from the file at the next Save (obsolete-key scrub). No-op if absent.</summary>
    public void Remove(string key) { _kv.Remove(key); _removed.Add(key); _dirty.Add(key); }
    private readonly HashSet<string> _removed = new(StringComparer.OrdinalIgnoreCase);

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
    // Settle delay (ms) before the deferred detail-pane parts load on selection (thumb strip + full box
    // upgrade + RA/store panels, and the fanart fade-in) — one value for both debounce timers. Clamped
    // 0..5000; 0 = load immediately (heavier while scrolling fast).
    public int DetailLoadDelayMs { get => Math.Clamp(GetInt("DetailLoadDelayMs", 300), 0, 5000); set => SetInt("DetailLoadDelayMs", Math.Clamp(value, 0, 5000)); }
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
    /// <summary>Move a game's media files when its title changes. OFF by default: LaunchBox itself
    /// does NOT do this, so it is a LiteBox addition, and it writes to the user's media folders.
    /// An opt-in is the honest default for behaviour that both deviates and touches files.</summary>
    /// <summary>Media follow a game's title. LaunchBox does NOT do this — it leaves them behind —
    /// which is why it started as opt-in and off. It is now always on: leaving a renamed game with
    /// no art is not a behaviour worth reproducing, and every path that moves media has since grown
    /// the guards that made it risky (shared titles are copied, nothing is overwritten, orphans are
    /// swept). The setting is kept as a constant rather than deleted so the choice can be handed
    /// back without unpicking the call sites.</summary>
    public const bool RenameMediaWithGameDefault = true;
    public bool RenameMediaWithGame   { get => RenameMediaWithGameDefault; set { } }
    public bool UseImageCache         { get => GetBool("UseImageCache", true); set => SetBool("UseImageCache", value); }
    public bool UseGameCache          { get => GetBool("UseGameCache", true); set => SetBool("UseGameCache", value); }
    public bool UnloadGameCacheDuringGame { get => GetBool("UnloadGameCacheDuringGame", true); set => SetBool("UnloadGameCacheDuringGame", value); }
    public TitleSortNormalization TitleSortNormalizationMode
    {
        get => TitleSortNormalizer.Parse(Get(TitleSortNormalizer.ConfigKey, "simple"));
        set => Set(TitleSortNormalizer.ConfigKey, TitleSortNormalizer.ConfigValue(value));
    }
    // true → reserve a 16:9 area for the main media; false → a poster-ratio (2:3) area.
    public bool Use169ForMainScreenshot { get => GetBool("Use16:9ForMainScreenshot", true); set => SetBool("Use16:9ForMainScreenshot", value); }
    /// <summary>Start playing a video as soon as it becomes the main media (Display → Right panel).
    /// Off: its still frame is shown with a ▶ and playback waits for a click.</summary>
    public bool VideoAutoplay { get => GetBool("VideoAutoplay", false); set => SetBool("VideoAutoplay", value); }
    /// <summary>Autoplay WITH SOUND. Off: an autoplayed video starts muted (scrolling a list should not
    /// talk). A video the user starts by CLICKING always has sound, whatever this says.</summary>
    public bool VideoAutoplaySound { get => GetBool("VideoAutoplaySound", false); set => SetBool("VideoAutoplaySound", value); }

    // ── When is a 3D case model worth showing (and baking)? ──────────────────
    // Flat globals, hence the ini (media-layout.json exists for the LIST-shaped layout, not for these).
    // The FRONT is always required — without it the case wears LaunchBox's "NoImage" placeholder. These
    // add optional requirements on top; the Box - Full sheet is an alternative branch on its own.
    // Read through Model3d.Model3dOptions (cached snapshot — no ini I/O on the hot paths).
    public bool Model3dRequireBack  { get => GetBool("Model3dRequireBack", false); set => SetBool("Model3dRequireBack", value); }
    public bool Model3dRequireSpine { get => GetBool("Model3dRequireSpine", false); set => SetBool("Model3dRequireSpine", value); }
    /// <summary>With BOTH of the above on: true = need both scans, false = either one is enough.</summary>
    public bool Model3dRequireBothScans { get => GetBool("Model3dRequireBothScans", false); set => SetBool("Model3dRequireBothScans", value); }
    /// <summary>A Box - Full scan alone validates the model (only for games where full-scan mode applies —
    /// that mode is set per platform/game, so this stays meaningful whatever any global setting says).</summary>
    public bool Model3dAcceptFullScan { get => GetBool("Model3dAcceptFullScan", false); set => SetBool("Model3dAcceptFullScan", value); }

    /// <summary>A DIVERGENCE from LaunchBox, on by default. LB pins Sega Saturn and Sega CD to the US long
    /// box (longJewelCase); their Japanese releases came in an ordinary CD jewel case. True = a game whose
    /// front artwork fits the jewel case's proportions better than the long box's is built as a jewel case.
    /// False = LaunchBox's own choice, unconditionally — set this when comparing against the oracle.</summary>
    public bool Model3dAutoJewelCase { get => GetBool("Model3dAutoJewelCase", true); set => SetBool("Model3dAutoJewelCase", value); }

    /// <summary>Also a divergence, on by default. True = a jewel-case game whose OWN Box - Spine scan
    /// measures a double-width case is built as a doubleJewelCase — how a multi-disc release (FF7 and
    /// friends) gets its real shape without a per-game override. Never uses a {Resources} preset spine:
    /// that strip is identical for every game on the platform and would flip all of them at once.</summary>
    public bool Model3dAutoDoubleJewel { get => GetBool("Model3dAutoDoubleJewel", true); set => SetBool("Model3dAutoDoubleJewel", value); }

    // ═══ LB-ORACLE (Model3dLbOracle) — dev/diagnostic, default OFF ═══
    // true → the Edit Platform 3D tab shows a SECOND preview zone rendered by LaunchBox's own core
    // (CoverFlow.FlowModel via CoreModelHost) above LiteBox's renderer, for side-by-side comparison of
    // the home-made 3D builders against the original. Everything oracle-related is lazy: while false
    // (the default) no core assembly is loaded and no oracle code path runs.
    public bool Model3dLbOracle { get => GetBool("Model3dLbOracle", false); set => SetBool("Model3dLbOracle", value); }

    /// <summary>Edit Game (multi-selection) → Images: split the Screenshots column into "Game Title",
    /// "Game Over" and "Other Screenshot" (every remaining configured type of that family). Off = the
    /// one merged column. Set from the matrix's own checkbox, remembered across sessions.</summary>
    public bool ImagesMatrixExpandScreenshots
    { get => GetBool("ImagesMatrixExpandScreenshots", false); set => SetBool("ImagesMatrixExpandScreenshots", value); }
    // Automatic Progress Tracking triggers (the RULES live in LB's Settings.xml; these choose WHEN
    // LiteBox runs them). Boot sweep = whole library in the background at startup (off by default —
    // avoidable cost on huge libraries); on-select = re-evaluate a game while its detail pane loads
    // (cheap: RAM + at most one cached-RA read). Game-exit evaluation is always on.
    public bool ProgressSweepOnBoot   { get => GetBool("ProgressSweepOnBoot", false); set => SetBool("ProgressSweepOnBoot", value); }
    public bool ProgressApplyOnSelect { get => GetBool("ProgressApplyOnSelect", true); set => SetBool("ProgressApplyOnSelect", value); }

    /// <summary>Family moves the progress automation must never make, "from>to", semicolon separated.
    /// LiteBox's own setting: LaunchBox has no equivalent field, and rewrites Settings.xml without the
    /// keys it does not know. Default: a game can be carried FORWARD through the families of the Game
    /// Progress Organization page, never backward.</summary>
    public string ProgressForbiddenFamilyMoves
    {
        get => Get("ProgressForbiddenFamilyMoves", "Active>Not Started; Done>Not Started; Done>Active");
        set => Set("ProgressForbiddenFamilyMoves", value ?? "");
    }
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
