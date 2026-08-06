// User-defined badges: an image plus the conditions under which it shows.
//
// The conditions are NOT a new language — they are the auto-playlist rules
// (PlaylistFilterCatalog / PlaylistFilterDef / PlaylistFilterPlan), the same vocabulary LaunchBox
// offers under Edit Playlist ▸ Auto-Populate, with the same semantics: rules on DIFFERENT fields are
// ANDed, repeated rules on the SAME field are ORed. That buys the whole field list (custom fields
// included), the per-type comparisons and an evaluator already measured against LaunchBox.
//
// Definitions live in LiteBox's own data dir (badges-custom.json) — LaunchBox has no concept of them.
// The IMAGES, on the other hand, go where all badge art goes: a media pack folder,
// <LB>\Images\Media Packs\Badges\LiteBox Custom\<id>.png, so BadgeImages indexes them with the rest
// and the user can see, copy or back them up alongside their library.
//
// Plans are compiled once per store revision: a badge's rules are evaluated over the whole library
// by BadgeEngine's pass, so recompiling per game would be the difference between milliseconds and
// seconds.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using LbApiHost.Host.Data;
using LbApiHost.Host.Diag;
using LbApiHost.Host.Media;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Badges;

/// <summary>One rule row, in the shape the playlist editor reads and writes.</summary>
internal sealed class BadgeRule : PlaylistFilterDefLike
{
    public string Field { get; set; } = "";
    public string Comparison { get; set; } = "";
    public string Value { get; set; } = "";

    [JsonIgnore] string PlaylistFilterDefLike.FieldKey => Field;
    [JsonIgnore] string PlaylistFilterDefLike.ComparisonTypeKey => Comparison;
    [JsonIgnore] string PlaylistFilterDefLike.Value => Value;

    public PlaylistFilterDef ToDef() => new(Field, Comparison, Value);
    public static BadgeRule From(PlaylistFilterDef d)
        => new() { Field = d.FieldKey ?? "", Comparison = d.ComparisonTypeKey ?? "", Value = d.Value ?? "" };
}

internal sealed class BadgeCustom
{
    /// <summary>Stable identity: the settings key, the pack file name, the order entry. Generated
    /// once from the name and never rewritten — renaming a badge must not orphan its image.</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<BadgeRule> Rules { get; set; } = new();

    [JsonIgnore] public string ImageFileName => Id + ".png";
}

internal static class BadgeCustomStore
{
    /// <summary>The pack folder custom badge images live in — a normal badge media pack, so the
    /// image index finds them without knowing they are ours.</summary>
    public const string PackName = "LiteBox Custom";

    private static readonly object _lock = new();
    private static List<BadgeCustom>? _list;
    private static Dictionary<string, PlaylistFilterPlan>? _plans;

    /// <summary>Raised when the set of custom badges changes (added, edited, deleted).</summary>
    public static event Action? Changed;

    private static string FilePath => LiteBoxPaths.File("badges-custom.json");

    public static string? ImageFolder
    {
        get
        {
            var root = BadgeImages.PacksRoot;
            return string.IsNullOrEmpty(root) ? null : Path.Combine(root, PackName);
        }
    }

    public static string? ImagePath(BadgeCustom b)
    {
        var dir = ImageFolder;
        return dir == null || string.IsNullOrEmpty(b.Id) ? null : Path.Combine(dir, b.ImageFileName);
    }

    public static IReadOnlyList<BadgeCustom> All()
    {
        lock (_lock) { return Load().Select(Clone).ToList(); }
    }

    public static BadgeCustom? ById(string id)
    {
        lock (_lock)
        {
            var b = Load().FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
            return b == null ? null : Clone(b);
        }
    }

    /// <summary>Adds or replaces a badge (matched on Id) and persists. Returns the stored copy.</summary>
    public static BadgeCustom Save(BadgeCustom badge)
    {
        lock (_lock)
        {
            var list = Load();
            if (string.IsNullOrWhiteSpace(badge.Id)) badge.Id = NewId(badge.Name, list);
            int i = list.FindIndex(x => string.Equals(x.Id, badge.Id, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) list[i] = Clone(badge); else list.Add(Clone(badge));
            Persist(list);
        }
        Changed?.Invoke();
        return badge;
    }

    public static void Delete(string id)
    {
        lock (_lock)
        {
            var list = Load();
            int removed = list.RemoveAll(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
            if (removed == 0) return;
            Persist(list);
        }
        Changed?.Invoke();
    }

    /// <summary>Does this game match the badge's rules? A badge with no (evaluable) rule matches
    /// nothing — an unconditional badge on every game would only be noise.</summary>
    public static bool Matches(string id, IGame game)
    {
        PlaylistFilterPlan? plan;
        lock (_lock)
        {
            _plans ??= Load().ToDictionary(b => b.Id,
                                           b => PlaylistFilterPlan.Compile(b.Rules),
                                           StringComparer.OrdinalIgnoreCase);
            if (!_plans.TryGetValue(id, out plan)) return false;
        }
        try { return plan.HasEvaluableGroup && plan.Matches(game); } catch { return false; }
    }

    /// <summary>Does this badge's rule set resolve to something the evaluator can actually test?
    /// (An unknown field or comparison compiles away to nothing, which shows as "matches no game".)</summary>
    public static bool IsEvaluable(BadgeCustom badge)
    {
        try { return PlaylistFilterPlan.Compile(badge.Rules).HasEvaluableGroup; } catch { return false; }
    }

    /// <summary>How many games in the library the rules match — the editor's live feedback.</summary>
    public static int CountMatches(IEnumerable<BadgeRule> rules, IEnumerable<IGame> games)
    {
        var plan = PlaylistFilterPlan.Compile(rules);
        if (!plan.HasEvaluableGroup) return 0;
        int n = 0;
        foreach (var g in games) { try { if (plan.Matches(g)) n++; } catch { } }
        return n;
    }

    // ── persistence ──────────────────────────────────────────────────────────

    private static List<BadgeCustom> Load()
    {
        if (_list != null) return _list;
        var list = new List<BadgeCustom>();
        try
        {
            if (File.Exists(FilePath))
                list = JsonSerializer.Deserialize<List<BadgeCustom>>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (Exception ex) { LbLog.Warn("badges", "custom badges load failed: " + ex.Message); }
        foreach (var b in list) b.Rules ??= new List<BadgeRule>();
        return _list = list;
    }

    private static void Persist(List<BadgeCustom> list)
    {
        _list = list;
        _plans = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath) ?? ".");
            File.WriteAllText(FilePath, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { LbLog.Warn("badges", "custom badges save failed: " + ex.Message); }
    }

    private static BadgeCustom Clone(BadgeCustom b) => new()
    {
        Id = b.Id,
        Name = b.Name,
        Rules = b.Rules?.Select(r => new BadgeRule { Field = r.Field, Comparison = r.Comparison, Value = r.Value }).ToList()
                ?? new List<BadgeRule>(),
    };

    /// <summary>Is that name already used by another badge? Names are unique — the id, and therefore
    /// the image file, is derived from the name, so two badges called the same thing would fight over
    /// one PNG. Built-in badges count too: a custom "Favorite" would collide with LaunchBox's own.</summary>
    public static bool NameTaken(string name, string? exceptId = null)
    {
        string n = (name ?? "").Trim();
        if (n.Length == 0) return false;
        lock (_lock)
        {
            if (Load().Any(b => !string.Equals(b.Id, exceptId, StringComparison.OrdinalIgnoreCase)
                                && string.Equals(b.Name?.Trim(), n, StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        return BadgeCatalog.BuiltIns.Any(b => string.Equals(b.Label, n, StringComparison.OrdinalIgnoreCase)
                                              || string.Equals(b.Id, n, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Renames a badge, id and image file included. The id IS the file name and the key the
    /// order and the disabled list use, so all three move together — otherwise a rename would orphan
    /// the art and reset the badge's place in the order.</summary>
    public static string Rename(BadgeCustom badge, string newName)
    {
        string oldId = badge.Id ?? "";
        var probe = new BadgeCustom { Name = newName };
        lock (_lock) probe.Id = NewId(newName, Load().Where(b => !string.Equals(b.Id, oldId, StringComparison.OrdinalIgnoreCase)).ToList());
        badge.Name = newName;
        if (string.Equals(probe.Id, oldId, StringComparison.OrdinalIgnoreCase)) return oldId;

        var from = ImagePath(badge);
        badge.Id = probe.Id;
        var to = ImagePath(badge);
        try
        {
            if (from != null && to != null && File.Exists(from)) { File.Move(from, to, overwrite: true); BadgeImages.Reset(); }
        }
        catch (Exception ex)
        {
            LbLog.Warn("badges", $"could not move {from} → {to}: {ex.Message}");
            badge.Id = oldId;      // the art stayed put, so the id must too
            return oldId;
        }

        lock (_lock)
        {
            var list = Load();
            list.RemoveAll(b => string.Equals(b.Id, oldId, StringComparison.OrdinalIgnoreCase));
            Persist(list);
        }
        BadgeSettings.RenameId(oldId, badge.Id);
        return badge.Id;
    }

    /// <summary>Gives a badge its id if it doesn't have one yet — the image dialog needs it BEFORE
    /// the badge is saved, because the id is the PNG's file name.</summary>
    public static void MintId(BadgeCustom badge)
    {
        if (!string.IsNullOrWhiteSpace(badge.Id)) return;
        lock (_lock) badge.Id = NewId(badge.Name, Load());
    }

    // An id derived from the name (it becomes a FILE name) and made unique. Kept for the badge's
    // whole life, so a rename never orphans the image.
    private static string NewId(string name, List<BadgeCustom> existing)
    {
        string bas = MediaResolver.Sanitize(string.IsNullOrWhiteSpace(name) ? "Custom Badge" : name.Trim());
        if (bas.Length == 0) bas = "Custom Badge";
        string id = bas;
        int n = 2;
        while (existing.Any(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase))
               || BadgeCatalog.BuiltIns.Any(b => string.Equals(b.Id, id, StringComparison.OrdinalIgnoreCase)))
            id = bas + " " + n++;
        return id;
    }
}
