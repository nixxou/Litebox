// traitement_guid_lb — ce qu'un jeu doit offrir, et à qui.
//
// Called once per game by the boot pass, and again for one game whenever something that could change the
// answer happens. It reads LaunchBox data and the archive listing cache; it NEVER opens a file and never
// checks that a path exists on disk. An unplugged external drive must not invalidate half the catalogue.
//
// The eight steps are the specification, in order, and the order matters twice:
//
//   • step 5 disables with THIS generation, step 6 only revives rows from OLDER generations — otherwise
//     the pass would resurrect what it had just invalidated, in the same breath;
//   • step 8 runs last, because it needs the default row to exist (step 7) and the disabled rows to have
//     been emptied of their clients (step 1).
//
// Nothing is deleted, ever. A row that stops being valid is disabled and its clients fall back to the
// default — which is a DIFFERENT row, hence a different rom_id. That is what stops a client from ever
// being handed the wrong file under an identifier it already trusts.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using LbApiHost.Host.Diag;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Romm;

/// <summary>One playable file a game could offer, as step 2 builds it.</summary>
internal sealed class RommCandidate
{
    public string AppId = "";           // "" = the game's own ROM
    public string FilePath = "";
    public bool IsExtract;
    public bool Known;                  // the archive listing is in the cache
    public HashSet<string> Roms = new(StringComparer.OrdinalIgnoreCase);   // paths inside the archive
}

/// <summary>What one game's pass concluded — for the log, and for the caller's counters.</summary>
internal sealed class RommPassResult
{
    public bool Advertised;
    public string? Reason;              // why not, when it is not
    public long DefaultRomId;
}

internal static class RommIndexPass
{
    /// <summary>Runs the eight steps on one game. <paramref name="rows"/> is every row of this guid,
    /// whatever its state; the pass mutates them in place and marks what must be written.</summary>
    public static RommPassResult Run(IGame game, List<RommGameRow> rows, int platformId, long gen,
                                     IReadOnlyDictionary<int, int> liveClients,
                                     RommLaunchMemory history)
    {
        var res = new RommPassResult();
        var guid = RommLibrary.IdOf(game);

        // ── 0. Un jeu non émulé est hors du mécanisme ─────────────────────────
        // One row, never revisited, valid for as long as the guid exists: no candidates, no validation,
        // no generations, no clients. DOSBox and ScummVM land here too — they distribute a game FOLDER,
        // not a file, and until that is built they have nothing to offer.
        if (!RommFiles.IsEmulated(game))
        {
            var row = rows.FirstOrDefault(r => !r.Emulated)
                   ?? Adopt(rows, guid, platformId, appId: "", filePath: "", romPath: "", isExtract: false);
            row.Emulated = false;
            if (row.Disabled != 0) { row.Disabled = 0; row.DisabledUtc = null; row.Touch(); }
            if (row.IsDefaultUtc == null) { row.IsDefaultUtc = DateTime.UtcNow; row.Touch(); }
            SettleClients(rows, row, liveClients);
            res.Advertised = true;
            res.DefaultRomId = row.RomId;
            return res;
        }

        // ── 1. Hygiène ────────────────────────────────────────────────────────
        foreach (var r in rows)
            if (!r.IsValid && r.Clients.Count > 0) { r.Clients.Clear(); r.Touch(); }

        // ── 2. Les candidats ──────────────────────────────────────────────────
        var candidates = RommFiles.CandidatesOf(game);

        // ── 3. Sortie sèche ───────────────────────────────────────────────────
        // Two situations, one rule: we do not advertise what we cannot name. Both repair themselves —
        // attach the file, or analyse the archive, and the trigger brings the game back.
        bool anyKnown = candidates.Any(c => !c.IsExtract || c.Known);
        if (candidates.Count == 0 || !anyKnown)
        {
            foreach (var r in rows) Disable(r, gen);
            res.Reason = candidates.Count == 0 ? "no playable file" : "archive contents unknown";
            return res;
        }

        // ── 4. Le défaut ──────────────────────────────────────────────────────
        var def = ComputeDefault(game, candidates, history);
        if (def == null)
        {
            foreach (var r in rows) Disable(r, gen);
            res.Reason = "no usable default";
            return res;
        }

        // ── 5. Validation des lignes actives ──────────────────────────────────
        foreach (var r in rows.Where(r => r.IsValid))
            if (!Validates(r, candidates)) Disable(r, gen);

        // ── 6. Réhabilitation des générations ANTÉRIEURES ─────────────────────
        foreach (var r in rows.Where(r => r.Disabled > 0 && r.Disabled < gen))
            if (Validates(r, candidates))
            {
                r.Disabled = 0; r.DisabledUtc = null; r.Touch();
            }

        // ── 7. La ligne du défaut existe ──────────────────────────────────────
        var defRow = rows.FirstOrDefault(r => r.Emulated
                        && PathEq(r.FilePath, def.Value.FilePath) && PathEq(r.RomPath, def.Value.RomPath))
                  ?? Adopt(rows, guid, platformId, def.Value.AppId, def.Value.FilePath,
                           def.Value.RomPath, def.Value.IsExtract);

        if (defRow.Disabled != 0) { defRow.Disabled = 0; defRow.DisabledUtc = null; defRow.Touch(); }
        if (defRow.AppId != def.Value.AppId) { defRow.AppId = def.Value.AppId; defRow.Touch(); }
        if (defRow.IsExtract != def.Value.IsExtract) { defRow.IsExtract = def.Value.IsExtract; defRow.Touch(); }
        if (defRow.PlatformId != platformId) { defRow.PlatformId = platformId; defRow.Touch(); }
        defRow.IsDefaultUtc = DateTime.UtcNow; defRow.Touch();

        foreach (var r in rows)
            if (!ReferenceEquals(r, defRow) && r.IsDefaultUtc != null) { r.IsDefaultUtc = null; r.Touch(); }

        // ── 8. Les clients ────────────────────────────────────────────────────
        SettleClients(rows, defRow, liveClients);

        res.Advertised = true;
        res.DefaultRomId = defRow.RomId;
        return res;
    }

    // ── Les étapes, en détail ─────────────────────────────────────────────────

    /// <summary>Does this row still name a file the game actually offers? Records only — the point of
    /// comparing what is RECORDED is that a missing drive does not invalidate a library.</summary>
    private static bool Validates(RommGameRow r, List<RommCandidate> candidates)
    {
        var c = candidates.FirstOrDefault(x => PathEq(x.FilePath, r.FilePath));
        if (c == null) return false;
        if (c.IsExtract != r.IsExtract) return false;
        if (!r.IsExtract) return r.RomPath.Length == 0;
        if (!c.Known) return false;
        return r.RomPath.Length > 0 && c.Roms.Contains(r.RomPath);
    }

    /// <summary>The file this game would be served by default: the version last launched, and within it
    /// the ROM last played — falling back to the ranking the desktop picker itself uses.</summary>
    private static (string AppId, string FilePath, string RomPath, bool IsExtract)?
        ComputeDefault(IGame game, List<RommCandidate> candidates, RommLaunchMemory history)
    {
        var last = history.For(RommLibrary.IdOf(game));

        var chosen = candidates.FirstOrDefault(c =>
                        last.AppId != null && string.Equals(c.AppId, last.AppId, StringComparison.Ordinal))
                  ?? candidates.FirstOrDefault(c => c.AppId.Length == 0)
                  ?? candidates.FirstOrDefault();
        if (chosen == null) return null;

        if (!chosen.IsExtract) return (chosen.AppId, chosen.FilePath, "", false);
        if (!chosen.Known || chosen.Roms.Count == 0) return null;

        // The entry actually played, if it is still in there; else the head of the ranking, which is
        // last-played, then favourites, then tag score — the desktop picker's own order.
        string? rom = last.RomEntry != null && chosen.Roms.Contains(last.RomEntry) ? last.RomEntry : null;
        rom ??= RommFiles.RankedFirst(game, chosen);
        return rom == null ? null : (chosen.AppId, chosen.FilePath, rom, true);
    }

    /// <summary>Every live client ends up on exactly one row. Those whose row was just disabled lost
    /// their place at step 1 and are picked up here by the default — which is the whole point: a client
    /// moves to another ROM only together with a change of rom_id.</summary>
    private static void SettleClients(List<RommGameRow> rows, RommGameRow defRow,
                                      IReadOnlyDictionary<int, int> liveClients)
    {
        var mentioned = new HashSet<int>();
        foreach (var r in rows.Where(r => r.IsValid))
        {
            int before = r.Clients.Count;
            r.Clients.RemoveAll(c => !liveClients.ContainsKey(c));
            if (r.Clients.Count != before) r.Touch();
            foreach (var c in r.Clients) mentioned.Add(c);
        }

        foreach (var c in liveClients.Keys)
            if (!mentioned.Contains(c)) { defRow.Clients.Add(c); defRow.Touch(); }
    }

    private static RommGameRow Adopt(List<RommGameRow> rows, string guid, int platformId,
                                     string appId, string filePath, string romPath, bool isExtract)
    {
        // A row for this file may exist from an older pass under a different platform id — the unique
        // key is (guid, filepath, rompath), so reuse it rather than colliding on insert.
        var existing = rows.FirstOrDefault(r => PathEq(r.FilePath, filePath) && PathEq(r.RomPath, romPath));
        if (existing != null) { existing.Touch(); return existing; }

        var row = new RommGameRow
        {
            GuidLb = guid, PlatformId = platformId, Emulated = true,
            AppId = appId, FilePath = filePath, RomPath = romPath, IsExtract = isExtract,
            Action = RommRowAction.Add,
        };
        rows.Add(row);
        return row;
    }

    private static void Disable(RommGameRow r, long gen)
    {
        if (r.Disabled == gen) return;
        r.Disabled = gen;
        r.DisabledUtc = DateTime.UtcNow;
        if (r.Clients.Count > 0) r.Clients.Clear();
        r.IsDefaultUtc = null;
        r.Touch();
    }

    /// <summary>Paths are compared as LaunchBox records them, case-insensitively and with separators
    /// normalised. Measured on this library: 9064 ApplicationPath, all relative, none absolute — but
    /// LaunchBox writes an absolute path the moment a file sits outside the LB root, so the comparison
    /// must not depend on the form.</summary>
    internal static bool PathEq(string? a, string? b)
        => string.Equals(Norm(a), Norm(b), StringComparison.OrdinalIgnoreCase);

    internal static string Norm(string? p)
        => (p ?? "").Replace('/', '\\').TrimEnd('\\');
}
