// Launch rules — the LiteBox port of BigBoxProfile's EmulatorActions ("sondes & actions"): an ordered
// list of actions run against the command line right before the game spawns, each guarded by probes
// (command-line filters today; more probes as actions get ported one by one, faithfully).
//
// V1 carries ONE action, Prefix — deliberately: each BigBoxProfile action is ported alone, verified
// against the original's behaviour, then the next one lands. The rule model already holds what the
// whole family needs (a Type discriminator, the shared filter/exclude probes, ordering, Enabled).
//
// Attachment is per entity (Edit Emulator / Edit Game / Edit Additional Version), resolved EXCLUSIVELY:
// version > game > emulator — the most specific level that has any enabled rule replaces the others,
// the same contract as Monitor Profile assignments (Mehdi's call; BigBoxProfile had one flat pipeline
// per emulator exe and no notion of game, so there is no original semantic to preserve here).

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using LbApiHost.Host.Data;
using LbApiHost.Host.Diag;
using LbApiHost.Host.Modules;

namespace LbApiHost.Host.Rules;

/// <summary>One rule. Fields are a superset across action types; <see cref="Type"/> says which apply.
/// The probe fields (Filter/Exclude and their modes) are shared by the whole family, exactly as in
/// BigBoxProfile where every action carried the same filter block.</summary>
internal sealed class LaunchRule
{
    public const string TypePrefix = "Prefix";

    public string Type { get; set; } = TypePrefix;
    public bool Enabled { get; set; } = true;

    // ── Prefix ──
    /// <summary>The text to prepend to the emulator's arguments.</summary>
    public string Prefix { get; set; } = "";
    /// <summary>true = the prefix (trimmed) becomes ONE argument inserted first; false = the prefix is
    /// prepended verbatim to the joined argument string and re-parsed, so it may carry SEVERAL
    /// arguments at once (BigBoxProfile's "Add As Argument" / "Add As cmdLine" radio).</summary>
    public bool AsArg { get; set; } = true;

    // ── probes (shared by every action type) ──
    /// <summary>"Only if cmdLine contains" — case-insensitive substring over exe + arguments.</summary>
    public string Filter { get; set; } = "";
    /// <summary>Filter is a comma-separated list: fire when ANY entry matches…</summary>
    public bool CommaFilter { get; set; }
    /// <summary>…unless this asks for ALL entries to match.</summary>
    public bool MatchAllFilter { get; set; }
    /// <summary>BigBoxProfile's "If match an arg, remove before execute": arguments strictly equal to a
    /// filter entry are stripped in a FINAL pass, after every rule ran — the marker-argument system,
    /// where a dummy per-game parameter set in LaunchBox routes rules and never reaches the emulator.</summary>
    public bool RemoveFilter { get; set; }
    /// <summary>Declares this rule's CONDITION as a group anchor: the future grouped view folds the
    /// consecutive rules sharing this signature into one branch, and by ticking it the author commits
    /// that no rule in the pipeline modifies the arguments the condition matches on. Presentation +
    /// contract only — the engine still re-probes every rule against the current line (the branching
    /// bus), and the preview trace is what audits the commitment, not the flag.</summary>
    public bool AsGroup { get; set; }

    /// <summary>"Exclude if cmdLine contains" — the blocking mirror of Filter.</summary>
    public string Exclude { get; set; } = "";
    public bool CommaExclude { get; set; }
    /// <summary>Block only when ALL exclude entries are present. NOTE: this is the INTENT of
    /// BigBoxProfile's checkbox — its original code had the any-match test first, making the flag
    /// unsatisfiable (ticked, the action never fired). Ported as what the label always promised.</summary>
    public bool MatchAllExclude { get; set; }

    public bool IsConfigured => Type switch
    {
        TypePrefix => Prefix.Length > 0,
        _ => false,
    };

    /// <summary>One-line description for the rule lists — mirrors BigBoxProfile's ToString().</summary>
    public string Describe()
    {
        if (!IsConfigured) return $"{Type} => NOT CONFIGURED";
        string d = Type switch
        {
            TypePrefix => (AsArg ? "Prefix this to the Arg List : " : "Prefix this to the command line : ") + Prefix,
            _ => Type,
        };
        if (Filter.Length > 0) d += $" [Only if command line contains {Filter}]" + (MatchAllFilter ? "[matchall]" : "");
        if (Exclude.Length > 0) d += $" [Exclude {Exclude}]" + (MatchAllExclude ? "[matchall]" : "");
        if (RemoveFilter) d += " [remove marker]";
        if (AsGroup && Filter.Length > 0) d += " [group]";
        if (!Enabled) d = "(disabled) " + d;
        return $"{Type} => {d}";
    }
}

/// <summary>The canonical form of a rule's CONDITION, for the derived group view — semantic, not
/// textual: entries normalized (trimmed, lowercased) and SORTED, so "a, b" and "B,a" are the same
/// signature. Two rules group when their signatures are EQUAL; a group nests under another when its
/// signature REFINES it — every-kind only (single text counts as EVERY of one entry), excludes
/// identical — because that is the one implication a human can predict at a glance (spec: the
/// launch-rules-groups design, Mehdi 2026-08).</summary>
internal sealed class ProbeSignature : IEquatable<ProbeSignature>
{
    /// <summary>Sorted, normalized filter entries.</summary>
    public IReadOnlyList<string> Entries { get; }
    /// <summary>true = ALL entries must be present (single text, or comma+matchall); false = ANY.</summary>
    public bool EveryKind { get; }
    /// <summary>The exclude side, normalized "entries|comma|matchall" — must be EQUAL to group or nest.</summary>
    public string ExcludeKey { get; }

    private ProbeSignature(IReadOnlyList<string> entries, bool everyKind, string excludeKey)
    {
        Entries = entries; EveryKind = everyKind; ExcludeKey = excludeKey;
    }

    /// <summary>The signature of an ANCHORED rule (AsGroup + a configured filter); null otherwise —
    /// grouping is opt-in and declared, never inferred.</summary>
    public static ProbeSignature? Of(LaunchRule r)
    {
        if (!r.AsGroup || r.Filter.Length == 0) return null;
        var entries = (r.CommaFilter ? r.Filter.Split(',') : new[] { r.Filter })
            .Select(e => e.Trim().ToLowerInvariant()).Where(e => e.Length > 0)
            .OrderBy(e => e, StringComparer.Ordinal).ToList();
        if (entries.Count == 0) return null;
        bool every = !r.CommaFilter || r.MatchAllFilter || entries.Count == 1;
        string exKey = r.Exclude.Length == 0 ? "" :
            string.Join(",", (r.CommaExclude ? r.Exclude.Split(',') : new[] { r.Exclude })
                .Select(e => e.Trim().ToLowerInvariant()).Where(e => e.Length > 0)
                .OrderBy(e => e, StringComparer.Ordinal))
            + "|" + r.CommaExclude + "|" + r.MatchAllExclude;
        return new ProbeSignature(entries, every, exKey);
    }

    public bool Equals(ProbeSignature? o)
        => o != null && EveryKind == o.EveryKind && ExcludeKey == o.ExcludeKey
           && Entries.SequenceEqual(o.Entries);
    public override bool Equals(object? o) => Equals(o as ProbeSignature);
    public override int GetHashCode()
        => Entries.Aggregate(EveryKind.GetHashCode() ^ ExcludeKey.GetHashCode(), (h, e) => h * 31 + e.GetHashCode());

    /// <summary>Strict refinement: this signature implies <paramref name="parent"/> — EVERY-kind on
    /// both sides, same excludes, and a strict superset of entries. The nesting relation.</summary>
    public bool Refines(ProbeSignature parent)
        => EveryKind && parent.EveryKind && ExcludeKey == parent.ExcludeKey
           && Entries.Count > parent.Entries.Count
           && !parent.Entries.Except(Entries).Any();

    /// <summary>Group-header text; with a parent, only the DELTA is shown ("... and --sinden").</summary>
    public string Label(ProbeSignature? parent = null)
    {
        var shown = parent == null ? Entries : Entries.Except(parent.Entries).ToList();
        string joined = string.Join(", ", shown);
        string head = parent != null ? "... and " + joined
            : EveryKind
                ? (Entries.Count == 1 ? "When the line contains " + joined : "When the line contains EVERY of: " + joined)
                : "When the line contains ANY of: " + joined;
        if (parent == null && ExcludeKey.Length > 0)
            head += "  (excl. " + ExcludeKey.Split('|')[0] + ")";
        return head;
    }
}

/// <summary>Storage + resolution. One JSON list per entity (option key "LaunchRules", scopes
/// version/game/emulator), read at launch time only — Cold, like the monitor assignments.</summary>
internal static class LaunchRuleStore
{
    private const string Tag = "rules";
    public const string Key = "LaunchRules";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public static List<LaunchRule> Get(string scope, string entityId)
    {
        if (string.IsNullOrEmpty(entityId)) return new List<LaunchRule>();
        try
        {
            var raw = LiteBoxOptionsDb.Get(scope, entityId, Key);
            if (string.IsNullOrWhiteSpace(raw)) return new List<LaunchRule>();
            return JsonSerializer.Deserialize<List<LaunchRule>>(raw, Json) ?? new List<LaunchRule>();
        }
        catch (Exception ex)
        {
            LbLog.Warn(Tag, $"unreadable rules for {scope}/{entityId}: {ex.Message}");
            return new List<LaunchRule>();
        }
    }

    /// <summary>Persist a list; empty/null deletes the row (no row = no opinion, like every option).</summary>
    public static void Set(string scope, string entityId, List<LaunchRule>? rules)
    {
        if (string.IsNullOrEmpty(entityId)) return;
        try
        {
            LiteBoxOptionsDb.Set(scope, entityId, Key,
                rules is { Count: > 0 } ? JsonSerializer.Serialize(rules, Json) : null);
        }
        catch (Exception ex) { LbLog.Warn(Tag, $"rules write failed ({scope}/{entityId}): {ex.Message}"); }
    }

    /// <summary>The rules a launch should run, or an empty list. EXCLUSIVE walk, version &gt; game &gt;
    /// emulator: the most specific entity with at least one ENABLED rule provides the whole pipeline.</summary>
    public static List<LaunchRule> Resolve(string? gameId, string? versionId, string? emulatorId)
    {
        if (!LbModules.On(LbModule.Rules)) return new List<LaunchRule>();

        foreach (var (scope, id) in new[]
                 {
                     (LiteBoxOption.ScopeVersion,  versionId ?? ""),
                     (LiteBoxOption.ScopeGame,     gameId ?? ""),
                     (LiteBoxOption.ScopeEmulator, emulatorId ?? ""),
                 })
        {
            if (id.Length == 0) continue;
            var rules = Get(scope, id);
            if (rules.Any(r => r.Enabled && r.IsConfigured))
            {
                LbLog.Info(Tag, $"launch: {rules.Count(r => r.Enabled && r.IsConfigured)} rule(s) from {scope}");
                return rules;
            }
        }
        return new List<LaunchRule>();
    }

    // ── the module-options listing (same shape as the monitor Assignments page) ──

    internal sealed record Row(string Scope, string EntityId, string EntityName, string What);

    public static List<Row> All(string scope, Func<string, string?> nameOf)
    {
        var rows = new List<Row>();
        Dictionary<string, string> raw;
        try { raw = LiteBoxOptionsDb.AllOf(scope, Key); }
        catch { return rows; }

        foreach (var kv in raw)
        {
            var rules = Get(scope, kv.Key);
            if (rules.Count == 0) continue;
            string what = rules.Count == 1 ? rules[0].Describe()
                        : $"{rules.Count} rules: " + string.Join(" · ", rules.Select(r => r.Type));
            rows.Add(new Row(scope, kv.Key, nameOf(kv.Key) ?? $"<unknown {kv.Key}>", what));
        }
        return rows.OrderBy(r => r.EntityName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static void Clear(string scope, string entityId) => Set(scope, entityId, null);

    // ── whole-set export / import ────────────────────────────────────────────
    // The file format wraps the stored JSON as-is, plus the entity NAMES — ids are meaningless on
    // another install, so import matches by id first and falls back to the name (emulators and
    // games; versions are id-only, their names are too ambiguous across libraries). Import REPLACES
    // the rules of the entities it matches and touches nothing else.

    internal sealed class RuleSetEntry
    {
        public string Scope { get; set; } = "";
        public string EntityId { get; set; } = "";
        public string EntityName { get; set; } = "";
        public List<LaunchRule> Rules { get; set; } = new();
    }

    internal sealed class RuleSetExport
    {
        public string Format { get; set; } = "LiteBoxLaunchRules";
        public int Version { get; set; } = 1;
        public List<RuleSetEntry> Entries { get; set; } = new();
    }

    private static readonly string[] AllScopes =
        { LiteBoxOption.ScopeEmulator, LiteBoxOption.ScopeGame, LiteBoxOption.ScopeVersion };

    /// <summary>Every stored rule list, names resolved through <paramref name="nameOf"/>.</summary>
    public static RuleSetExport ExportAll(Func<string, Func<string, string?>> nameOf)
    {
        var export = new RuleSetExport();
        foreach (var scope in AllScopes)
        {
            Dictionary<string, string> raw;
            try { raw = LiteBoxOptionsDb.AllOf(scope, Key); } catch { continue; }
            var resolve = nameOf(scope);
            foreach (var kv in raw)
            {
                var rules = Get(scope, kv.Key);
                if (rules.Count == 0) continue;
                export.Entries.Add(new RuleSetEntry
                {
                    Scope = scope, EntityId = kv.Key,
                    EntityName = resolve(kv.Key) ?? "",
                    Rules = rules,
                });
            }
        }
        return export;
    }

    internal sealed record ImportPlanItem(RuleSetEntry Entry, string? TargetId, string How);

    /// <summary>Matches every entry of an export against THIS library: by id when the entity exists,
    /// by unique name otherwise (emulator/game). The plan is computed first so the confirmation can
    /// say exactly what will happen before anything is written.</summary>
    public static List<ImportPlanItem> PlanImport(RuleSetExport export,
        Func<string, string?> emulatorIdByName, Func<string, string?> gameIdByName,
        Func<string, string, bool> entityExists)
    {
        var plan = new List<ImportPlanItem>();
        foreach (var e in export.Entries)
        {
            if (!AllScopes.Contains(e.Scope)) { plan.Add(new ImportPlanItem(e, null, "unknown scope")); continue; }
            if (e.EntityId.Length > 0 && entityExists(e.Scope, e.EntityId))
            {
                plan.Add(new ImportPlanItem(e, e.EntityId, "matched by id"));
                continue;
            }
            string? byName = e.EntityName.Length == 0 ? null
                : e.Scope == LiteBoxOption.ScopeEmulator ? emulatorIdByName(e.EntityName)
                : e.Scope == LiteBoxOption.ScopeGame ? gameIdByName(e.EntityName)
                : null;
            plan.Add(byName != null
                ? new ImportPlanItem(e, byName, "matched by name")
                : new ImportPlanItem(e, null, e.Scope == LiteBoxOption.ScopeVersion
                    ? "version not found (versions match by id only)" : "not found"));
        }
        return plan;
    }

    /// <summary>Writes the matched entries — each REPLACES that entity's rules. Returns how many.</summary>
    public static int ApplyImport(List<ImportPlanItem> plan)
    {
        int done = 0;
        foreach (var item in plan)
        {
            if (item.TargetId == null) continue;
            Set(item.Entry.Scope, item.TargetId, item.Entry.Rules);
            done++;
        }
        if (done > 0) LbLog.Info(Tag, $"rule-set import: {done} entit(ies) written");
        return done;
    }
}
