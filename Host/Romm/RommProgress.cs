// LaunchBox's Progress field ↔ RomM's rom_user, translated without one hard-coded write.
//
// The Progress vocabulary is the USER'S: ProgressPriorities is freely edited (renamed, reworded,
// translated), so nothing here may assume "Done / Beaten" exists. Both directions go through a
// KIND — a recognized meaning — and strings only ever come from the library itself:
//
//   reading   game.Progress ──Classify──► kind ──► the rom_user combination to serve
//   writing   rom_user fields ──TargetOf──► kind ──Resolve──► a string the LIBRARY already has
//
// Resolve's ladder: the automation's configured target values first (the user typed those spellings
// himself), then the live ProgressPriorities scanned through Classify (any wording it recognizes,
// French included), and NOTHING as the honest floor — an unrecognizable vocabulary means the status
// stays in the RomM extras and Progress is left alone, traced, rather than polluted with a foreign
// value. Classification is token-based and bilingual so stock installs and translated ones both work.
//
// The mapping itself (docs/romm-server-plan.md §5.6ter follow-up):
//   Not Started / Unplayed      ↔ incomplete, completion 0
//   Not Started / Want to Play  ↔ incomplete + backlogged
//   Not Started / Won't Play    ↔ never_playing
//   Active / In Progress        ↔ incomplete + now_playing
//   Active / Continuous         →  same as In Progress (LB-only refinement, never downgraded)
//   Active / Paused             ↔ incomplete, completion > 0, not now_playing
//   Done / Beaten               ↔ finished
//   Done / Completed            ↔ completed_100 (completion floored to 100)
//   Done / Mastered             →  same as Completed (LB-only refinement, never downgraded)
//   Done / Dropped              ↔ retired (and Freegosy's "dropped" spelling)

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using LbApiHost.Host.Data;

namespace LbApiHost.Host.Romm;

internal enum ProgressKind
{
    Unknown, Unplayed, WantToPlay, WontPlay,
    InProgress, Continuous, Paused,
    Beaten, Completed, Mastered, Dropped,
}

internal static class RommProgress
{
    // ── Recognition ───────────────────────────────────────────────────────────

    /// <summary>Most specific first: "Want to Play" must not fall into the generic play tokens, and
    /// "Mastered" must be seen before the completed family. EN + FR, matched on a lowercased,
    /// apostrophe-stripped copy.</summary>
    private static readonly (ProgressKind kind, string[] tokens)[] Rules =
    {
        (ProgressKind.Mastered,   new[] { "master", "maitris", "maîtris" }),
        (ProgressKind.Continuous, new[] { "continuous", "continu" }),
        (ProgressKind.Paused,     new[] { "paused", "pause" }),
        (ProgressKind.Dropped,    new[] { "dropped", "drop", "abandon" }),
        (ProgressKind.Beaten,     new[] { "beaten", "beat", "battu", "vaincu" }),
        (ProgressKind.WontPlay,   new[] { "wont", "never", "jamais" }),
        (ProgressKind.WantToPlay, new[] { "want", "envie", "backlog" }),
        (ProgressKind.InProgress, new[] { "in progress", "progress", "playing", "en cours" }),
        (ProgressKind.Completed,  new[] { "completed", "complete", "finished", "termin", "fini", "100" }),
        (ProgressKind.Unplayed,   new[] { "unplayed", "not started", "non commence", "non commencé", "pas commence" }),
    };

    /// <summary>What this Progress string MEANS, or Unknown for a value nothing recognizes — which is
    /// a legitimate answer, never an error: a free vocabulary is allowed to be mute here.</summary>
    public static ProgressKind Classify(string? progress)
    {
        if (string.IsNullOrWhiteSpace(progress)) return ProgressKind.Unknown;
        var (_, value) = ProgressModel.Split(progress);
        // The value half first — the category ("Not Started") repeats across three entries and would
        // shadow the discriminating word.
        var k = ClassifyText(value);
        return k != ProgressKind.Unknown ? k : ClassifyText(progress);
    }

    private static ProgressKind ClassifyText(string text)
    {
        var t = text.ToLowerInvariant().Replace("'", "").Replace("’", "");
        if (t.Trim().Length == 0) return ProgressKind.Unknown;
        foreach (var (kind, tokens) in Rules)
            foreach (var tok in tokens)
                if (t.Contains(tok, StringComparison.Ordinal))
                    return kind;
        return ProgressKind.Unknown;
    }

    /// <summary>"notstarted" / "active" / "done" — the stickiness unit: an incoming write that lands in
    /// the family already in place never downgrades a refinement LaunchBox alone can express.</summary>
    public static string FamilyOf(ProgressKind k) => k switch
    {
        ProgressKind.Unplayed or ProgressKind.WantToPlay or ProgressKind.WontPlay => "notstarted",
        ProgressKind.InProgress or ProgressKind.Continuous or ProgressKind.Paused => "active",
        ProgressKind.Beaten or ProgressKind.Completed or ProgressKind.Mastered or ProgressKind.Dropped => "done",
        _ => "",
    };

    // ── Reading: a kind as rom_user speaks ────────────────────────────────────

    /// <summary>The rom_user combination a classified Progress imposes: the status, overrides for the
    /// two flags where the LB value IS the flag (null leaves the stored extra alone), and a completion
    /// floor. Null for Unknown — the caller keeps its old derivation.</summary>
    public static (string status, bool? backlogged, bool? nowPlaying, int completionFloor)? RomUserOf(ProgressKind k) => k switch
    {
        ProgressKind.Unplayed   => ("incomplete", false, false, 0),
        ProgressKind.WantToPlay => ("incomplete", true, false, 0),
        ProgressKind.WontPlay   => ("never_playing", false, false, 0),
        ProgressKind.InProgress => ("incomplete", null, true, 0),
        ProgressKind.Continuous => ("incomplete", null, true, 0),
        ProgressKind.Paused     => ("incomplete", null, false, 0),
        ProgressKind.Beaten     => ("finished", null, null, 0),
        ProgressKind.Completed  => ("completed_100", null, null, 100),
        ProgressKind.Mastered   => ("completed_100", null, null, 100),
        ProgressKind.Dropped    => ("retired", null, null, 0),
        _ => null,
    };

    // ── Writing: the merged rom_user as a kind ────────────────────────────────

    /// <summary>The Progress a client's merged rom_user asks for. Explicit status first, then the
    /// derivations; Unknown when nothing is actionable — an already-played game with no signal is not
    /// walked back to Unplayed.</summary>
    public static ProgressKind TargetOf(string? status, int completion, bool backlogged, bool nowPlaying,
                                        bool everPlayed)
    {
        if (Eq(status, "never_playing")) return ProgressKind.WontPlay;
        if (Eq(status, "retired") || Eq(status, "dropped")) return ProgressKind.Dropped;
        if (Eq(status, "finished")) return ProgressKind.Beaten;
        if (Eq(status, "completed_100") || completion >= 100) return ProgressKind.Completed;
        if (nowPlaying) return ProgressKind.InProgress;
        if (completion > 0) return ProgressKind.Paused;
        if (backlogged) return ProgressKind.WantToPlay;
        if (!everPlayed) return ProgressKind.Unplayed;
        return ProgressKind.Unknown;
    }

    private static bool Eq(string? a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // ── Resolution: a kind as THIS library spells it ──────────────────────────

    /// <summary>The string to write for a kind, from the library's own vocabulary — or null, meaning
    /// "do not write Progress at all": nothing here invents a value the user's list does not carry.</summary>
    public static string? Resolve(ProgressKind kind)
    {
        var store = ProgressModel.Store;

        // The automation's configured targets: spellings the user typed himself, keyed by meaning.
        if (store != null && store.Loaded)
        {
            string Cfg(string key) { try { return store.Get(key).Trim(); } catch { return ""; } }
            var direct = kind switch
            {
                ProgressKind.Unplayed => Cfg("AutoProgressNotStartedValue"),
                ProgressKind.Paused => Cfg("AutoProgressPausedValue"),
                ProgressKind.Beaten => Cfg("AutoProgressBeatenSoftcoreValue") is { Length: > 0 } b ? b
                                     : Cfg("AutoProgressBeatenHardcoreValue"),
                ProgressKind.Completed => Cfg("AutoProgressCompletedValue"),
                ProgressKind.Mastered => Cfg("AutoProgressMasteredValue"),
                _ => "",
            };
            if (!string.IsNullOrEmpty(direct)) return direct;
        }

        // The live vocabulary, read through the same classifier — whatever the user renamed things to,
        // as long as a word still says what the entry means.
        try
        {
            foreach (var entry in ProgressModel.Values(store))
                if (Classify(entry) == kind)
                    return entry;
        }
        catch { }
        return null;
    }
}
