// The badge set — LaunchBox's own, name for name.
//
// Each entry pairs the pack FILE NAME (which is also the badge's identity in LaunchBox's settings and
// in its menu) with the wording LaunchBox shows, taken from its string table (Unbroken.LaunchBox.dll →
// Strings.resources, keys Indicator*): IndicatorWheelYokeSupport = "Wheel/Yoke Support",
// IndicatorXboxMsStore = "Xbox/Microsoft Store", and so on. The Badges menu composes its entries the
// way LaunchBox does — LabelEnableSomething ("Enable {0}") + that wording.
//
// The three groups are LaunchBox's three submenus: Game Attributes, Storefronts, Controller Support.
//
// Predicates are evaluated ONCE PER GAME by BadgeEngine's background pass and cached, so they may
// cost more than a field read — but not much more, and never per repaint. The two that would be
// expensive done naively (documents, controller categories) get a per-platform index / per-game memo
// through BadgeContext.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using LbApiHost.Host.Data;
using LbApiHost.Host.Media;
using LbApiHost.Host.Saves;
using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Badges;

internal enum BadgeGroup { GameAttributes, Storefronts, ControllerSupport, Custom }

/// <summary>One badge that applies to one game: which definition, which pack image, the tooltip and
/// the tint. Produced by the engine, consumed by every surface that draws badges.</summary>
internal readonly struct BadgeHit
{
    public readonly string Id;        // catalog identity (= settings identity)
    public readonly string Image;     // resolved pack image name
    public readonly string Tip;       // full tooltip text
    public readonly BadgeTint Tint;

    public BadgeHit(string id, string image, string tip, BadgeTint tint)
    { Id = id; Image = image; Tip = tip; Tint = tint; }
}

internal sealed class BadgeDef
{
    /// <summary>Pack file name without extension — also the badge's identity in LB's settings.</summary>
    public string Id = "";
    public BadgeGroup Group;
    /// <summary>LaunchBox's own wording (its Indicator* string).</summary>
    public string Label = "";
    /// <summary>Does this badge apply to this game?</summary>
    public Func<IGame, BadgeContext, bool> Applies = (_, _) => false;
    /// <summary>Image names to try, best first. Null → the Id. Only Progress needs more than one
    /// (the exact "Category _ Value" file, else the category's own file, else the generic marker).</summary>
    public Func<IGame, string[]>? Images;
    /// <summary>Text put in parentheses after the label ("Required", "Partial Support", the progress
    /// value…). Null / empty → the label alone.</summary>
    public Func<IGame, BadgeContext, string?>? Detail;
    /// <summary>Colour treatment. Only REQUIRED controller support uses one.</summary>
    public Func<IGame, BadgeContext, BadgeTint>? Tint;
}

internal static class BadgeCatalog
{
    private static bool B(Func<bool> f) { try { return f(); } catch { return false; } }
    private static string S(Func<string?> f) { try { return f() ?? ""; } catch { return ""; } }

    /// <summary>LaunchBox's own badges. The live set (<see cref="All"/>) is these plus the user's
    /// custom ones, so everything downstream — menu, engine, surfaces, ordering — treats a custom
    /// badge exactly like a built-in.</summary>
    public static readonly IReadOnlyList<BadgeDef> BuiltIns = new List<BadgeDef>
    {
        // ── Game attributes ──────────────────────────────────────────────────
        new() { Id = "Favorite", Group = BadgeGroup.GameAttributes, Label = "Favorite",
                Applies = (g, _) => B(() => g.Favorite) },
        new() { Id = "Broken", Group = BadgeGroup.GameAttributes, Label = "Broken",
                Applies = (g, _) => B(() => g.Broken) },
        new() { Id = "Hidden", Group = BadgeGroup.GameAttributes, Label = "Hidden",
                Applies = (g, _) => B(() => g.Hide) },
        new() { Id = "Portable", Group = BadgeGroup.GameAttributes, Label = "Portable",
                Applies = (g, _) => B(() => g.Portable) },
        // Progress: the game's "Category / Value" (ProgressModel's organization). The pack ships one
        // image per value ("Done _ Beaten.png") plus one per category ("Done.png") under Progress\,
        // so an organization the pack doesn't know still shows its category's badge.
        new() { Id = "Progress", Group = BadgeGroup.GameAttributes, Label = "Progress",
                Applies = (g, _) => S(() => g.Progress).Length > 0,
                Images = g => ProgressImages(S(() => g.Progress)),
                Detail = (g, _) => S(() => g.Progress) },
        // Installed is a THREE-state field (ticked / explicitly unticked / never set), so the two
        // badges are not each other's negation: an untouched game shows neither.
        new() { Id = "Installed", Group = BadgeGroup.GameAttributes, Label = "Installed",
                Applies = (g, _) => { try { return g.Installed == true; } catch { return false; } } },
        new() { Id = "Not Installed", Group = BadgeGroup.GameAttributes, Label = "Not Installed",
                Applies = (g, _) => { try { return g.Installed == false; } catch { return false; } } },
        new() { Id = "Documents", Group = BadgeGroup.GameAttributes, Label = "Documents",
                Applies = (g, ctx) => ctx.HasManual(g) },
        // A game's alternate builds live as additional applications ("Play (Japan) Version..."). One
        // of them already means the game exists in more than one version — measured against LaunchBox
        // on this library: 1941 (4 apps) and 1942 (5) wear the badge, 1943 Kai and A. D. 2083 (none) don't.
        new() { Id = "Multiple Versions", Group = BadgeGroup.GameAttributes, Label = "Multiple Versions",
                Applies = (g, ctx) => ctx.AddApps(g).Count > 0 },
        // Discs are FIELDS, never parsed from names — the same doctrine as M3uPlaylistPlanner. Two
        // distinct disc numbers = a multi-disc set.
        new() { Id = "Multiple Discs", Group = BadgeGroup.GameAttributes, Label = "Multiple Discs",
                Applies = (g, ctx) => ctx.AddApps(g).Select(a => a.Disc).Where(d => d.HasValue)
                                         .Distinct().Count() >= 2 },
        new() { Id = "HasSavedGame", Group = BadgeGroup.GameAttributes, Label = "Has Saved Game",
                Applies = (g, ctx) => ctx.Saves(g).Any(e => !e.IsState) },
        new() { Id = "HasSaveStates", Group = BadgeGroup.GameAttributes, Label = "Has Save States",
                Applies = (g, ctx) => ctx.Saves(g).Any(e => e.IsState) },
        // "Supported" (the machine is in hiscore.dat), which is what LiteBox can answer locally —
        // GameSortCatalog owns that rule and memoises it per game.
        new() { Id = "MAME High Scores", Group = BadgeGroup.GameAttributes, Label = "MAME High Scores",
                Applies = (g, _) => B(() => GameSortCatalog.MameHighScoresSupported(g)) },
        // LiteBox only knows RetroAchievements, via the hash LaunchBox stores on the game — the same
        // answer PlaylistFilterCatalog gives for its AnyAchievements filter.
        new() { Id = "Achievements", Group = BadgeGroup.GameAttributes, Label = "Achievements",
                Applies = (g, _) => g is HostGame hg && !string.IsNullOrWhiteSpace(hg.RetroAchievementsHash) },

        // ── Storefronts ──────────────────────────────────────────────────────
        // LaunchBox's Source IS the store name; PlaylistFilterCatalog.StoreMatches already owns the
        // aliases each importer writes ("Origin"/"EA app", "Microsoft Store"/"Xbox"…).
        new() { Id = "Amazon", Group = BadgeGroup.Storefronts, Label = "Amazon Games",
                Applies = (g, _) => Store(g, "Amazon") },
        new() { Id = "EpicGames", Group = BadgeGroup.Storefronts, Label = "Epic Games",
                Applies = (g, _) => Store(g, "EpicGames") },
        new() { Id = "GOG", Group = BadgeGroup.Storefronts, Label = "GOG",
                Applies = (g, _) => Store(g, "GOG") },
        new() { Id = "EA", Group = BadgeGroup.Storefronts, Label = "EA",
                Applies = (g, _) => Store(g, "EA") },
        new() { Id = "Steam", Group = BadgeGroup.Storefronts, Label = "Steam",
                Applies = (g, _) => Store(g, "Steam") },
        new() { Id = "Uplay", Group = BadgeGroup.Storefronts, Label = "Uplay/Ubisoft Connect",
                Applies = (g, _) => Store(g, "Uplay") },
        new() { Id = "Xbox", Group = BadgeGroup.Storefronts, Label = "Xbox/Microsoft Store",
                Applies = (g, _) => Store(g, "Xbox") },

        // ── Controller support ───────────────────────────────────────────────
        // The game's <GameControllerSupport> rows name controllers by id; the CATEGORY that decides
        // which badge shows lives in the Data\GameControllers.xml catalog (ControllerCatalogStore).
        // The strongest level found for a category rides in the tooltip, and REQUIRED paints it red.
        Ctl("GamepadSupport", "Gamepad Support", "Gamepad"),
        Ctl("JoystickSupport", "Joystick Support", "Joystick"),
        Ctl("KeyboardSupport", "Keyboard Support", "Keyboard"),
        Ctl("LightGunSupport", "Light Gun Support", "Light Gun"),
        Ctl("MotionSupport", "Motion Support", "Motion"),
        Ctl("MouseSupport", "Mouse Support", "Mouse"),
        Ctl("PaddleSupport", "Paddle Support", "Paddle"),
        Ctl("RhythmSupport", "Rhythm Support", "Rhythm"),
        Ctl("TrackballSupport", "Trackball Support", "Trackball"),
        Ctl("VrSupport", "VR Support", "VR"),
        Ctl("WheelYokeSupport", "Wheel/Yoke Support", "Wheel/Yoke"),
    };

    private static BadgeDef Ctl(string id, string label, string category) => new()
    {
        Id = id,
        Group = BadgeGroup.ControllerSupport,
        Label = label,
        // Declared, and not the explicit "Not Supported" (0).
        Applies = (g, ctx) => ctx.ControllerLevel(g, category) is var l
                              && l != BadgeContext.NoCategory && l != 0,
        // LaunchBox's own wording for the level, in parentheses. A row with no SupportLevel shows an
        // empty cell in LB's grid, so it gets no parenthesis here either.
        Detail = (g, ctx) => ctx.ControllerLevel(g, category) is var l && l >= 1
                             ? Controllers.ControllerSupport.ToDisplay(l.ToString()) : null,
        Tint = (g, ctx) => ctx.ControllerLevel(g, category) == BadgeContext.LevelRequired
                           ? BadgeTint.Required : BadgeTint.None,
    };

    // ── the live set: built-ins + the user's custom badges ───────────────────
    private static IReadOnlyList<BadgeDef>? _all;
    private static readonly object _allLock = new();

    static BadgeCatalog() { BadgeCustomStore.Changed += () => { lock (_allLock) _all = null; }; }

    /// <summary>Every badge, built-in first then custom (which is also the default draw order before
    /// the user reorders anything).</summary>
    public static IReadOnlyList<BadgeDef> All
    {
        get
        {
            lock (_allLock)
            {
                if (_all != null) return _all;
                var list = new List<BadgeDef>(BuiltIns);
                foreach (var c in BadgeCustomStore.All())
                {
                    var id = c.Id;
                    list.Add(new BadgeDef
                    {
                        Id = id,
                        Group = BadgeGroup.Custom,
                        Label = string.IsNullOrWhiteSpace(c.Name) ? id : c.Name,
                        Applies = (g, _) => BadgeCustomStore.Matches(id, g),
                    });
                }
                return _all = list;
            }
        }
    }

    public static IEnumerable<BadgeDef> Of(BadgeGroup group) => All.Where(b => b.Group == group);

    public static BadgeDef? ById(string id)
        => All.FirstOrDefault(b => string.Equals(b.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Every badge that applies to a game, in catalog order — WITHOUT the enabled filter.
    /// The engine caches this; the enabled set is applied at draw time, so toggling a badge in the
    /// menu costs nothing.</summary>
    /// <summary>Evaluate straight into PACKED slots — two bytes per applicable badge (its catalog
    /// index, and a pooled variant for the image/detail/tint when it is not drawn plainly). This is
    /// the only path the pass uses: a BadgeHit[] per game would be 300 000 arrays and 300 000 lists of
    /// strings to keep, which is exactly what BadgeTable exists to avoid. Returns the number of slots
    /// written into <paramref name="slots"/> (2 bytes each).</summary>
    public static int EvaluatePacked(IGame? game, BadgeContext ctx, BadgeTable table, byte[] slots)
    {
        if (game == null) return 0;
        var all = All;
        int max = Math.Min(all.Count, 255);      // the index is one byte; a 255th badge would alias
        int n = 0;
        for (int i = 0; i < max; i++)
        {
            var b = all[i];
            bool applies;
            try { applies = b.Applies(game, ctx); } catch { applies = false; }
            if (!applies) continue;

            // No array, no LINQ delegate: this runs per applicable badge per game.
            string? img = null;
            if (b.Images != null)
            {
                string[] cands;
                try { cands = b.Images(game); } catch { cands = Array.Empty<string>(); }
                foreach (var c in cands) if (BadgeImages.Has(c)) { img = c; break; }
            }
            else if (BadgeImages.Has(b.Id)) img = b.Id;
            if (img == null) continue;                     // the pack has no such file — draw nothing

            string detail = "";
            try { detail = b.Detail?.Invoke(game, ctx) ?? ""; } catch { }
            var tint = BadgeTint.None;
            try { tint = b.Tint?.Invoke(game, ctx) ?? BadgeTint.None; } catch { }

            if (n * 2 + 2 > slots.Length) break;
            slots[n * 2] = (byte)i;
            slots[n * 2 + 1] = (byte)table.Variant(img, b.Id, detail, tint);
            n++;
        }
        return n;
    }

    public static BadgeHit[] Evaluate(IGame? game, BadgeContext ctx)
    {
        if (game == null) return Array.Empty<BadgeHit>();
        List<BadgeHit>? hits = null;
        foreach (var b in All)
        {
            bool applies;
            try { applies = b.Applies(game, ctx); } catch { applies = false; }
            if (!applies) continue;

            string? img = (b.Images?.Invoke(game) ?? new[] { b.Id }).FirstOrDefault(BadgeImages.Has);
            if (img == null) continue;                     // the pack has no such file — draw nothing

            string detail = "";
            try { detail = b.Detail?.Invoke(game, ctx) ?? ""; } catch { }
            string tip = detail.Length == 0 ? b.Label
                       : b.Id == "Progress" ? detail          // the value speaks for itself
                       : $"{b.Label} ({detail})";
            var tint = BadgeTint.None;
            try { tint = b.Tint?.Invoke(game, ctx) ?? BadgeTint.None; } catch { }

            (hits ??= new List<BadgeHit>(4)).Add(new BadgeHit(b.Id, img, tip, tint));
        }
        return hits?.ToArray() ?? Array.Empty<BadgeHit>();
    }

    // ── predicate helpers ────────────────────────────────────────────────────

    private static bool Store(IGame g, string storeKey)
        => PlaylistFilterCatalog.StoreMatches(S(() => g.Source), storeKey);

    // "Done / Beaten" → Progress\Done _ Beaten.png, else Progress\Done.png, else the generic
    // Progress.png. The pack's file names use '_' where the value uses '/', and BadgeImages folds
    // '_' to ' ' on both sides, so the lookup matches "Done _ Beaten.png" on disk.
    private static string[] ProgressImages(string progress)
    {
        if (progress.Length == 0) return Array.Empty<string>();
        var (category, value) = ProgressModel.Split(progress);
        return category.Length == 0
            ? new[] { "Progress/" + value, "Progress" }
            : new[] { "Progress/" + category + " _ " + value, "Progress/" + category, "Progress" };
    }
}

/// <summary>Per-pass scratch space: the things a predicate would otherwise recompute per game (the
/// platform's manual index) or per badge (a game's controller levels, additional apps, saves).
/// One instance per worker; NOT thread-safe by design — each worker owns its own.</summary>
internal sealed class BadgeContext
{
    /// <summary>The game declares nothing for that controller category.</summary>
    public const int NoCategory = int.MinValue;
    /// <summary>Declared, with no SupportLevel — LaunchBox's empty cell.</summary>
    public const int LevelUnspecified = -1;
    public const int LevelRequired = 3;

    private readonly Dictionary<string, ManualIndex> _manuals = new(StringComparer.OrdinalIgnoreCase);
    private readonly bool _indexManuals;

    // Per-game memos, reset by BeginGame — a game is evaluated by one worker, badge after badge, and
    // several badges read the same three lists.
    private Guid _game;
    private readonly HashSet<Guid> _docsDone = new();
    private Dictionary<string, int>? _ctlLevels;
    private IReadOnlyList<IAdditionalApplication>? _apps;
    private IReadOnlyList<VaultEntry>? _saves;

    /// <param name="indexManuals">True for the bulk pass (build one file index per platform); false
    /// for a single on-demand game, where MediaResolver's own per-game walk is cheaper.</param>
    public BadgeContext(bool indexManuals) { _indexManuals = indexManuals; }

    public void BeginGame(IGame g)
    {
        _game = Guid.TryParse(Safe(() => g.Id), out var id) ? id : Guid.Empty;
        _ctlLevels = null; _apps = null; _saves = null;
    }

    // ── additional applications (versions / discs) ───────────────────────────
    public IReadOnlyList<IAdditionalApplication> AddApps(IGame g)
    {
        if (_apps != null) return _apps;
        try { _apps = g.GetAllAdditionalApplications() ?? Array.Empty<IAdditionalApplication>(); }
        catch { _apps = Array.Empty<IAdditionalApplication>(); }
        return _apps;
    }

    // ── save vault ───────────────────────────────────────────────────────────
    public IReadOnlyList<VaultEntry> Saves(IGame g)
    {
        if (_saves != null) return _saves;
        try { _saves = SaveVault.ForGame(Safe(() => g.Id) ?? ""); }
        catch { _saves = Array.Empty<VaultEntry>(); }
        return _saves;
    }

    // ── controller categories ────────────────────────────────────────────────
    /// <summary>The strongest support level the game declares for a controller category:
    /// <see cref="NoCategory"/> when it declares none, <see cref="LevelUnspecified"/> when declared
    /// without a level, else LB's 0 "Not Supported" / 1 partial / 2 full / 3 required.</summary>
    public int ControllerLevel(IGame g, string category)
    {
        _ctlLevels ??= BuildControllerLevels(g);
        return _ctlLevels.TryGetValue(category, out var lvl) ? lvl : NoCategory;
    }

    private static Dictionary<string, int> BuildControllerLevels(IGame g)
    {
        var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (g is not ILiteBoxGame lbg) return d;
            var rows = lbg.GetSubEntities("GameControllerSupport");
            if (rows == null || rows.Count == 0) return d;
            var cats = ControllerCategories();
            foreach (var row in rows)
            {
                // ControllerId, not Id — LB's field name in the row (same read as the Edit Game
                // ▸ Controller Support grid).
                if (!row.TryGetValue("ControllerId", out var cid) || string.IsNullOrEmpty(cid)) continue;
                if (!cats.TryGetValue(cid, out var cat) || cat.Length == 0) continue;
                int lvl = row.TryGetValue("SupportLevel", out var s) && int.TryParse(s, out var v) ? v : LevelUnspecified;
                if (!d.TryGetValue(cat, out var cur) || lvl > cur) d[cat] = lvl;
            }
        }
        catch { }
        return d;
    }

    // controller id → category, read once from the catalog (a few dozen entries, stable per session).
    private static Dictionary<string, string>? _cats;
    private static readonly object _catLock = new();

    private static Dictionary<string, string> ControllerCategories()
    {
        lock (_catLock)
        {
            if (_cats != null) return _cats;
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var c in ControllerCatalogStore.All())
                    if (!string.IsNullOrEmpty(c.Id)) d[c.Id] = c.Category ?? "";
            }
            catch { }
            return _cats = d;
        }
    }

    /// <summary>Forget the controller-category map (the catalog was edited).</summary>
    public static void InvalidateControllers() { lock (_catLock) _cats = null; }

    // ── documents ────────────────────────────────────────────────────────────
    /// <summary>Does the game have a manual? The answer lives on the game row (HasManual), worked out
    /// by the media sweep this context runs per platform — so a single-game recompute is a field read
    /// instead of a walk of the whole Manuals folder.
    ///
    /// ZERO I/O by design, and that shapes one rule: a pinned ManualPath is trusted on its face when
    /// it points OUTSIDE the scanned tree (another drive, a folder we never indexed), and checked
    /// against the index when it points inside it. Probing the file would be one File.Exists per game
    /// — exactly what this whole mechanism exists to avoid. A stale external pin therefore keeps its
    /// badge; the Documents page is where that shows up as "(missing)".</summary>
    public bool HasManual(IGame g)
    {
        try
        {
            EnsureDocumentBits(g);
            return g is HostGame hg ? hg.HasManual : !string.IsNullOrEmpty(Safe(() => g.ManualPath));
        }
        catch { return false; }
    }

    /// <summary>Several documents (the pinned one plus matched files, or several matched files).
    /// Computed by the same sweep; no badge uses it yet, but the walk has it in hand.</summary>
    public bool HasMultipleDocuments(IGame g)
    {
        try { EnsureDocumentBits(g); return g is HostGame hg && hg.HasMultipleDocuments; }
        catch { return false; }
    }

    // Fills the row's two document bits from the platform index, once per game and per sweep.
    private void EnsureDocumentBits(IGame g)
    {
        var id = Guid.TryParse(Safe(() => g.Id), out var x) ? x : Guid.Empty;
        if (id == Guid.Empty || !_docsDone.Add(id)) return;

        string platform = Safe(() => g.Platform) ?? "";
        string title = Safe(() => g.Title) ?? "";
        string pinned = Safe(() => g.ManualPath) ?? "";

        int matched;
        if (_indexManuals)
        {
            if (!_manuals.TryGetValue(platform, out var idx))
                _manuals[platform] = idx = ManualIndex.Build(platform);
            matched = idx.Count(id, title);
        }
        else
        {
            // Single game outside a sweep (a recompute): the resolver's own walk, for one game only.
            matched = MediaResolver.ManualsAll(platform, id, title).Count;
        }

        bool pinnedInside = pinned.Length > 0 && ManualIndex.LooksInsideTree(platform, pinned);
        bool hasManual = matched > 0 || (pinned.Length > 0 && !pinnedInside);
        bool multiple = matched > 1 || (matched > 0 && pinned.Length > 0 && !pinnedInside);

        try { (PluginHelper.DataManager as HostDataManagerXml)?.Store?.SetDocumentBits(id, hasManual, multiple); }
        catch { }
    }

    private static string? Safe(Func<string?> f) { try { return f(); } catch { return null; } }

    /// <summary>One platform's Manuals folder, reduced to what a "has a manual?" question needs: the
    /// GUIDs named by GUID-form files, and the title stems of the plain-form ones. Same two shapes
    /// MediaResolver matches ("&lt;title&gt;.&lt;guid&gt;…-NN" and "&lt;title&gt;[-NN]"), minus the
    /// ordering rules — those only matter when you have to pick WHICH file.</summary>
    internal sealed class ManualIndex
    {
        private readonly HashSet<Guid> _ids = new();
        private readonly HashSet<string> _stems = new(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<Guid, int> _idCount = new();
        private readonly Dictionary<string, int> _stemCount = new(StringComparer.OrdinalIgnoreCase);

        public bool Has(Guid id, string title) => Count(id, title) > 0;

        /// <summary>How many files in the platform's Manuals tree belong to this game.</summary>
        public int Count(Guid id, string title)
        {
            int n = 0;
            if (id != Guid.Empty && _idCount.TryGetValue(id, out var byId)) n += byId;
            if (_stemCount.TryGetValue(MediaResolver.Sanitize(title ?? ""), out var byName)) n += byName;
            return n;
        }

        /// <summary>Is this stored path inside the folder the sweep indexed? String comparison only —
        /// no File.Exists, no path probing.</summary>
        public static bool LooksInsideTree(string platform, string storedPath)
        {
            try
            {
                var dir = MediaResolver.ManualsFolder(platform);
                if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(storedPath)) return false;
                var lb = MediaResolver.LbRoot ?? "";
                string full = System.IO.Path.IsPathRooted(storedPath)
                    ? storedPath
                    : System.IO.Path.Combine(lb, storedPath);
                const char Sep = '\\';
                return full.Replace('/', Sep).StartsWith(dir.TrimEnd(Sep) + Sep, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        public static ManualIndex Build(string platform)
        {
            var idx = new ManualIndex();
            try
            {
                string dir = MediaResolver.ManualsFolder(platform);
                if (string.IsNullOrEmpty(dir) || !System.IO.Directory.Exists(dir)) return idx;
                foreach (var f in System.IO.Directory.EnumerateFiles(dir, "*", System.IO.SearchOption.AllDirectories))
                {
                    string stem = System.IO.Path.GetFileNameWithoutExtension(f);
                    var gm = GuidStem.Match(stem);
                    if (gm.Success && Guid.TryParse(gm.Groups[2].Value, out var gid))
                    { idx._ids.Add(gid); idx._idCount[gid] = idx._idCount.TryGetValue(gid, out var c) ? c + 1 : 1; continue; }
                    var pm = NumberedStem.Match(stem);
                    string key = pm.Success ? pm.Groups[1].Value : stem;
                    idx._stems.Add(key);
                    idx._stemCount[key] = idx._stemCount.TryGetValue(key, out var c2) ? c2 + 1 : 1;
                }
            }
            catch { }
            return idx;
        }

        private static readonly System.Text.RegularExpressions.Regex GuidStem = new(
            @"^(.+)\.([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})((?:-[^-]+)*)-(\d+)$",
            System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        private static readonly System.Text.RegularExpressions.Regex NumberedStem = new(
            @"^(.+)-(\d+)$", System.Text.RegularExpressions.RegexOptions.Compiled);
    }
}
