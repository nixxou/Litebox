// Badge preferences — shared with LaunchBox, in its own Data\Settings.xml.
//
// Three keys, all LaunchBox's (found in Unbroken.LaunchBox.Windows.dll, which is where its badge
// engine lives — BadgeManager + the built-in Badge* bitmaps):
//   • ShowBadges     bool, already present in every LB install. Governs the badges drawn in the GAME
//                    LIST; the detail/hero indicators show regardless, which is why nothing here
//                    consults it — the hero draws whatever is enabled.
//   • EnabledBadges  the per-badge toggles of View ▸ Badges ▸ (Game Attributes / Storefronts /
//                    Controller Support). ABSENT = every badge enabled (a fresh LB shows them all).
//   • BadgePack      the media pack the user picked in "Change Badge Images..."; absent = LB's own
//                    built-in art, which is where our "first pack found wins" fallback lands.
//
// ── EnabledBadges: the format, as LaunchBox writes it ───────────────────────────────────────────
// MEASURED, not guessed — read out of a library where LaunchBox had written it:
//
//   <EnabledBadges>Favorite|Hidden|Broken|Portable|Multiple Discs|Multiple Versions|Installed|
//   Not Installed|GOG|Steam|EpicGames|Uplay|MAME High Scores|EA|Amazon|Xbox|GamepadSupport|…|
//   Documents|Achievements|HasSavedGame|HasSaveStates|Progress</EnabledBadges>
//
// PIPE-separated, holding the ENABLED badges, named exactly like the media-pack files — the same
// identities BadgeCatalog uses, so nothing has to be mapped. Absent element = every badge enabled.
// (This mattered: parsing it as comma-separated turns the whole value into one unknown name, which
// reads as "every badge disabled" — badges silently vanish everywhere.)
//
// The parse also accepts ',' and ';' so a hand-edited value still reads. Writing preserves the
// ORDER LaunchBox had, appending anything new at the end, so LiteBox's rewrite doesn't reshuffle a
// file LaunchBox owns.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using LbApiHost.Host.Data;
using Unbroken.LaunchBox.Plugins;

namespace LbApiHost.Host.Badges;

internal static class BadgeSettings
{
    private const string KeyShow = "ShowBadges";
    private const string KeyEnabled = "EnabledBadges";
    private const string KeyPack = "BadgePack";
    // LiteBox's own (LiteBox.ini): the draw order of every badge, and which CUSTOM badges are off.
    private const string KeyOrder = "BadgeOrder";
    private const string KeyCustomOff = "CustomBadgesDisabled";

    /// <summary>Raised after any change, so every surface showing badges can repaint.</summary>
    public static event Action? Changed;

    /// <summary>Bumped by every change that alters what gets drawn (enabled set, order, the custom
    /// badge list). Callers memoise their derived results against it — the badge strips are rebuilt
    /// for thousands of rows on every view change, so re-filtering and re-sorting per row there was
    /// millions of string comparisons per keystroke.</summary>
    public static int Version { get; private set; }

    private static void Bump() { Version++; _customIds = null; _orderIndex = null; Changed?.Invoke(); }

    static BadgeSettings() { BadgeCustomStore.Changed += () => { _order = null; Bump(); }; }

    private static LbSettingsStore? Store => (PluginHelper.DataManager as HostDataManagerXml)?.LbSettings;

    /// <summary>LaunchBox's "Show Badges" — the game LIST toggle. The hero indicators ignore it.</summary>
    public static bool ShowBadges
    {
        get => Store?.GetBool(KeyShow, true) ?? true;
        set { Store?.SetBool(KeyShow, value); Bump(); }
    }

    /// <summary>The media pack chosen in LaunchBox, or null when it uses its built-in art.</summary>
    public static string? Pack
    {
        get { var v = Store?.Get(KeyPack, "") ?? ""; return v.Length > 0 ? v : null; }
    }

    /// <summary>Is this badge enabled? Unknown/absent setting = enabled, like a fresh LaunchBox.</summary>
    public static bool IsEnabled(string id)
    {
        if (IsCustom(id)) return !DisabledCustom().Contains(id);
        var set = Enabled();
        if (set == null) return true;
        // LaunchBox's EnabledBadges only ever lists ITS OWN badges. An id it cannot know — a custom
        // one — must not be judged by that list: absent from it would read as "disabled", which is how
        // a freshly created custom badge could silently never appear. Only LaunchBox's own ids are
        // answered from LaunchBox's own setting.
        if (!BadgeCatalog.BuiltIns.Any(b => string.Equals(b.Id, id, StringComparison.OrdinalIgnoreCase)))
            return !DisabledCustom().Contains(id);
        return set.Contains(id);
    }

    public static void SetEnabled(string id, bool on)
    {
        // Custom badges never enter LaunchBox's EnabledBadges: that key belongs to LaunchBox, which
        // rewrites the file and would drop names it doesn't know. Their state is LiteBox's, and it
        // records the DISABLED ones so a newly created badge starts visible.
        if (IsCustom(id))
        {
            var off = new HashSet<string>(DisabledCustom(), StringComparer.OrdinalIgnoreCase);
            if (on) off.Remove(id); else off.Add(id);
            Cfg.Set(KeyCustomOff, string.Join("|", off));
            Cfg.Save();
            _customOff = off;
            Bump();
            return;
        }

        var store = Store;
        if (store == null) return;
        // Materialise the full set the first time (absent = all enabled), then add/remove one.
        var set = Enabled() ?? new HashSet<string>(BadgeCatalog.All.Select(b => b.Id), StringComparer.OrdinalIgnoreCase);
        if (on) set.Add(id); else set.Remove(id);
        string formatted = Format(set);   // reads the PREVIOUS raw value for its ordering — before we overwrite it
        _cache = set;
        _cacheRaw = formatted;
        store.Set(KeyEnabled, formatted);
        Bump();
    }

    /// <summary>Forget the parsed set (settings reloaded from disk).</summary>
    public static void Reset() { _cache = null; _cacheRaw = null; _customOff = null; _order = null; Bump(); }

    // ── draw order ───────────────────────────────────────────────────────────
    // ONE global list of every badge id, LiteBox's own (LaunchBox's EnabledBadges only lists the
    // enabled ones and so cannot carry the order of the others). Ids the catalog no longer knows are
    // dropped on read, badges the list doesn't mention are appended — the catalog can grow without
    // invalidating the setting.

    private static List<string>? _order;

    public static IReadOnlyList<string> Order
    {
        get
        {
            if (_order != null) return _order;
            var known = BadgeCatalog.All.Select(b => b.Id).ToList();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<string>(known.Count);
            foreach (var id in Split(Cfg.Get(KeyOrder) ?? ""))
            {
                var match = known.FirstOrDefault(k => string.Equals(k, id, StringComparison.OrdinalIgnoreCase));
                if (match != null && seen.Add(match)) list.Add(match);
            }
            foreach (var id in known) if (seen.Add(id)) list.Add(id);
            return _order = list;
        }
    }

    private static Dictionary<string, int>? _orderIndex;

    /// <summary>Where a badge sits in the draw order (large for an unknown id, so it lands last).
    /// O(1): this is called from a sort comparator, once per pair of badges per game drawn.</summary>
    public static int OrderIndex(string id)
    {
        if (_orderIndex == null)
        {
            var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var list = Order;
            for (int i = 0; i < list.Count; i++) d[list[i]] = i;
            _orderIndex = d;
        }
        return _orderIndex.TryGetValue(id, out var ix) ? ix : int.MaxValue;
    }

    /// <summary>A badge changed identity (a custom badge was renamed): carry its place in the order
    /// and its disabled state over, instead of dropping it to the end and back to "enabled".</summary>
    public static void RenameId(string oldId, string newId)
    {
        if (string.IsNullOrWhiteSpace(oldId) || string.IsNullOrWhiteSpace(newId)) return;
        var order = Order.Select(x => string.Equals(x, oldId, StringComparison.OrdinalIgnoreCase) ? newId : x).ToList();
        SetOrder(order);

        var off = DisabledCustom();
        if (off.Remove(oldId))
        {
            off.Add(newId);
            Cfg.Set(KeyCustomOff, string.Join("|", off));
            Cfg.Save();
        }
    }

    public static void SetOrder(IEnumerable<string> ids)
    {
        var list = ids?.Where(s => !string.IsNullOrWhiteSpace(s)).ToList() ?? new List<string>();
        _order = list;
        Cfg.Set(KeyOrder, string.Join("|", list));
        Cfg.Save();
        Bump();
    }

    /// <summary>Moves a badge one step in the order — but measured against the badges the user can
    /// actually SEE (<paramref name="visible"/>, the family-filtered list). It lands immediately
    /// before (or after) that visible neighbour, jumping over any hidden badge in between, and every
    /// position is renumbered. That is what keeps a press from doing nothing visible when the filtered
    /// view hides the global neighbour.</summary>
    public static void Move(string id, bool up, IReadOnlyList<string> visible)
    {
        var order = Order.ToList();
        int cur = order.FindIndex(x => string.Equals(x, id, StringComparison.OrdinalIgnoreCase));
        if (cur < 0) return;

        // The neighbour is the previous/next id of the FILTERED view, in global order.
        var shown = order.Where(x => visible.Any(v => string.Equals(v, x, StringComparison.OrdinalIgnoreCase))).ToList();
        int at = shown.FindIndex(x => string.Equals(x, id, StringComparison.OrdinalIgnoreCase));
        if (at < 0) return;
        int nbAt = up ? at - 1 : at + 1;
        if (nbAt < 0 || nbAt >= shown.Count) return;          // already first/last of what is shown
        string neighbour = shown[nbAt];

        order.RemoveAt(cur);
        int nb = order.FindIndex(x => string.Equals(x, neighbour, StringComparison.OrdinalIgnoreCase));
        if (nb < 0) return;
        order.Insert(up ? nb : nb + 1, id);
        SetOrder(order);
    }

    // ── custom badges: enabled state (LiteBox's, keyed by the DISABLED ones) ──

    private static HashSet<string>? _customOff;

    private static HashSet<string> DisabledCustom()
        => _customOff ??= new HashSet<string>(Split(Cfg.Get(KeyCustomOff) ?? ""), StringComparer.OrdinalIgnoreCase);

    private static HashSet<string>? _customIds;

    private static bool IsCustom(string id)
        => (_customIds ??= new HashSet<string>(
                BadgeCatalog.All.Where(b => b.Group == BadgeGroup.Custom).Select(b => b.Id),
                StringComparer.OrdinalIgnoreCase)).Contains(id);

    private static LiteBoxConfig? _cfgInstance;
    private static LiteBoxConfig Cfg => _cfgInstance ??= LiteBoxConfig.LoadForExe();

    // ── the assumed encoding, in one place ───────────────────────────────────

    private static HashSet<string>? _cache;      // null = "all enabled"
    private static string? _cacheRaw;            // the raw value _cache was parsed from

    private static HashSet<string>? Enabled()
    {
        var raw = Store?.Get(KeyEnabled, "") ?? "";
        if (raw.Length == 0) { _cache = null; _cacheRaw = null; return null; }
        if (_cacheRaw == raw) return _cache;
        _cacheRaw = raw;
        return _cache = Parse(raw);
    }

    private static readonly char[] Separators = { '|', ',', ';' };

    private static HashSet<string> Parse(string raw)
        => new(Split(raw), StringComparer.OrdinalIgnoreCase);

    private static string[] Split(string raw)
        => raw.Split(Separators, StringSplitOptions.RemoveEmptyEntries)
              .Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();

    // LaunchBox's order first (whatever it wrote stays put), then any badge it didn't know about.
    // Rewriting the list in our own order would churn a file LaunchBox owns for no reason.
    private static string Format(HashSet<string> set)
    {
        var order = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in Split(_cacheRaw ?? "")) if (seen.Add(id)) order.Add(id);
        foreach (var b in BadgeCatalog.All) if (seen.Add(b.Id)) order.Add(b.Id);
        return string.Join("|", order.Where(set.Contains));
    }
}
