// RA engine RESOLVER — P2/P3 of the RA-engine migration (plugin RaScanner/RaOnSelect semantics on
// top of RaStore). Fills a game's RetroAchievementsHash/Id, store-first:
//
//   • STORE-FIRST: a standalone ROM whose path signature already has a rom_hash row, or an archive
//     whose parse_state is OK, never spawns RAHasher again — the cached hashes are reused (also
//     dedupes files shared by several games / versions). force=true recomputes regardless.
//   • SENTINEL: LaunchBox's "COULDNTFILEHASH" (imported libraries) is treated as NO hash — it is
//     healed, never preserved.
//   • ARCHIVES: every entry hashed in one --arc-details call, filtered to the platform's ROM
//     extensions (--arc-ext, from the ROM module's per-platform profile); each entry persisted with
//     its raid; the game's hash = the TWO-PASS pick (plugin RaArchivePick): raid-bearing entries
//     ordered by the ROM module's SortForDisplay priorities, else the first hashed entry.
//   • ARCADE: MD5(basename), console 27, no RAHasher spawn.
//   • CATALOGUE ON DEMAND: an unknown hash on a console with no catalogue triggers one guarded
//     engine refresh (RaCatalogEngine.RefreshOne) then retries the lookup — first game of a console
//     pays the pull, like it always did.
//
// BLOCKING (RAHasher + maybe a catalogue pull) — call from a background thread.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LbApiHost.Host.Data;
using LbApiHost.Host.Media;
using LbApiHost.Host.Rom;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Ra;

internal static class RaResolveLite
{
    public const string Sentinel = "COULDNTFILEHASH";

    /// <summary>A usable hash: non-empty and not LaunchBox's couldn't-hash sentinel.</summary>
    public static bool IsRealHash(string? h)
        => !string.IsNullOrEmpty(h) && !h!.Equals(Sentinel, StringComparison.OrdinalIgnoreCase);

    /// <summary>Fills hash+raid for a game. Returns true when it set at least the hash.
    /// force=false (auto on-select) acts only when the game has no REAL hash yet (the sentinel
    /// counts as none); force=true (full scan) recomputes and overwrites.</summary>
    public static bool Resolve(IGame game, bool force = false)
    {
        if (game is not ILiteBoxFields fields) return false;
        try
        {
            string title = Safe(() => game.Title) ?? "?";

            if (!force && IsRealHash(fields.GetField("RetroAchievementsHash"))) return false;

            string? platform = Safe(() => game.Platform);
            int? cid = RaPlatformMap.ConsoleIdFor(platform);
            if (cid == null) { Log($"\"{title}\" platform \"{platform}\" not RA-mapped → skip."); return false; }
            int consoleId = cid.Value;

            string? appPath = Safe(() => game.ApplicationPath);
            if (string.IsNullOrWhiteSpace(appPath)) { Log($"\"{title}\" no ApplicationPath → skip."); return false; }
            string abs = ResolveAbsolute(appPath!);
            if (string.IsNullOrEmpty(abs) || !File.Exists(abs)) { Log($"\"{title}\" ROM file missing ({abs}) → skip."); return false; }

            var picked = ResolvePath(consoleId, platform ?? "", abs, force);
            if (picked == null || !IsRealHash(picked.Value.Hash))
            { Log($"\"{title}\" [{platform}/{consoleId}] hash failed → nothing set."); return false; }

            var (hash, raid) = picked.Value;
            fields.SetField("RetroAchievementsHash", hash);
            if (raid > 0)
            {
                fields.SetField("RetroAchievementsId", raid.ToString());
                Log($"\"{title}\" [{platform}/{consoleId}] → SET hash={Short(hash)} raid={raid}.");
            }
            else Log($"\"{title}\" [{platform}/{consoleId}] → SET hash={Short(hash)} (no raid match).");
            return true;
        }
        catch (Exception ex) { Log("Resolve failed: " + ex.Message); return false; }
    }

    /// <summary>Resolve one FILE (main ROM or an additional-application version) into the store and
    /// return the game-level (hash, raid) pick — null when hashing failed. Store-first unless force.</summary>
    public static (string Hash, int Raid)? ResolvePath(int consoleId, string platform, string abs, bool force)
    {
        long size = 0; try { size = new FileInfo(abs).Length; } catch { }
        string sig = ArchiveSig.ComputePathSignature(abs, size);

        if (consoleId == RaPlatformMap.ArcadeConsoleId)
        {
            // Arcade: name-hash, stored as a standalone rom_hash row (the .zip IS the unit).
            if (!force && RaStore.GetRomHash(sig) is { } cachedA && IsRealHash(cachedA.Hash))
                return (cachedA.Hash, Relookup(consoleId, cachedA.Hash, cachedA.Raid));
            string hash = RaHasherLite.ArcadeNameHash(abs);
            int raid = LookupEnsuringCatalogue(consoleId, hash);
            RaStore.UpsertRomHash(sig, abs, size, hash, raid);
            return (hash, raid);
        }

        if (IsArchive(abs))
            return ResolveArchive(consoleId, platform, abs, size, sig, force);

        // Plain file / disc image.
        if (!force && RaStore.GetRomHash(sig) is { } cached && IsRealHash(cached.Hash))
            return (cached.Hash, Relookup(consoleId, cached.Hash, cached.Raid));
        var single = RaHasherLite.ComputeSingle(consoleId, abs);
        if (!IsRealHash(single)) return null;
        int sRaid = LookupEnsuringCatalogue(consoleId, single!);
        RaStore.UpsertRomHash(sig, abs, size, single!, sRaid);
        return (single!, sRaid);
    }

    private static (string Hash, int Raid)? ResolveArchive(int consoleId, string platform, string abs, long size, string sig, bool force)
    {
        // Store-first: an OK parse reuses the per-entry rows — no RAHasher spawn.
        Dictionary<string, (string Hash, int Raid)> entries;
        if (!force && RaStore.GetParseState(sig) == RaStore.ParseOk && (entries = RaStore.GetEntriesRa(sig)).Count > 0)
            return PickFromEntries(consoleId, platform, entries);

        Log($"archive {Path.GetFileName(abs)} [{consoleId}] → RAHasher --arc-details…");
        var hashed = RaHasherLite.ComputeArchiveEntries(consoleId, abs, RomExtensionsFor(platform));
        if (hashed.Count == 0)
        {
            RaStore.SetParseState(sig, RaStore.ParseFailed);
            return null;
        }

        bool catalogueEnsured = false;
        var map = new Dictionary<string, (string, int)>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in hashed)
        {
            if (!IsRealHash(e.Hash) || string.IsNullOrEmpty(e.Name)) continue;
            int raid = RaStore.LookupRaid(e.Hash);
            if (raid == 0 && !catalogueEnsured)
            {
                raid = LookupEnsuringCatalogue(consoleId, e.Hash);
                catalogueEnsured = true;
            }
            RaStore.UpsertEntryRa(sig, e.Name, 0, e.Hash, raid);
            map[e.Name] = (e.Hash, raid);
        }
        if (map.Count == 0) { RaStore.SetParseState(sig, RaStore.ParseFailed); return null; }
        RaStore.SetParseState(sig, RaStore.ParseOk);
        return PickFromEntries(consoleId, platform, map);
    }

    /// <summary>The plugin's two-pass pick: pass 1 = raid-bearing entries ordered by the ROM module's
    /// SortForDisplay (per-platform priority CSV, no RA bonus — it would be circular); pass 2 = any
    /// hashed entry (stable path order), hash only.</summary>
    private static (string Hash, int Raid)? PickFromEntries(int consoleId, string platform,
        Dictionary<string, (string Hash, int Raid)> entries)
    {
        if (entries.Count == 0) return null;

        var withRaid = entries.Where(kv => kv.Value.Raid > 0).ToList();
        if (withRaid.Count > 0)
        {
            if (withRaid.Count == 1) return withRaid[0].Value;
            try
            {
                var infos = withRaid.Select((kv, i) => new ArchiveEntryInfo
                {
                    Index = i,
                    PathInArchive = kv.Key,
                    FileName = Path.GetFileName(kv.Key),
                    Extension = (Path.GetExtension(kv.Key) ?? "").TrimStart('.').ToLowerInvariant(),
                }).ToList();
                var sorted = ArchiveAnalyzer.SortForDisplay(
                    infos, PriorityCsvFor(platform),
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase), Array.Empty<string>());
                foreach (var s in sorted)
                    if (entries.TryGetValue(s.PathInArchive, out var v) && v.Raid > 0) return v;
            }
            catch { /* sort unavailable → fall through to stable order */ }
            return withRaid.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).First().Value;
        }

        // No raid anywhere: first hashed entry (stable order), hash only.
        var first = entries.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).First();
        return first.Value;
    }

    /// <summary>Raid lookup with the on-demand catalogue pull: unknown hash + empty catalogue for the
    /// console → one guarded engine refresh, then retry.</summary>
    private static int LookupEnsuringCatalogue(int consoleId, string hash)
    {
        int raid = RaStore.LookupRaid(hash);
        if (raid > 0) return raid;
        if (RaStore.CatalogueCount(consoleId) == 0)
        {
            RaCatalogEngine.RefreshOne(consoleId);
            raid = RaStore.LookupRaid(hash);
        }
        return raid;
    }

    /// <summary>Freshen a cached raid against the current catalogue WITHOUT clearing (never-downgrade;
    /// drops are the canary-gated refresh sync's job).</summary>
    private static int Relookup(int consoleId, string hash, int cachedRaid)
    {
        int raid = LookupEnsuringCatalogue(consoleId, hash);
        return raid > 0 ? raid : cachedRaid;
    }

    /// <summary>Cheap re-link for an already-hashed game: current catalogue lookup, set/update the id
    /// when one is found. NO RAHasher; never CLEARS an existing raid. Sentinel counts as no hash.</summary>
    public static bool RelinkRaid(IGame game)
    {
        if (game is not ILiteBoxFields fields) return false;
        try
        {
            string hash = fields.GetField("RetroAchievementsHash") ?? "";
            if (!IsRealHash(hash)) return false;
            int cid = RaPlatformMap.ConsoleIdFor(Safe(() => game.Platform)) ?? 0;
            if (cid <= 0) return false;
            int raid = LookupEnsuringCatalogue(cid, hash);
            if (raid <= 0) return false;   // still no match → leave as-is (never clear)
            string cur = fields.GetField("RetroAchievementsId") ?? "";
            if (cur == raid.ToString()) return false;
            fields.SetField("RetroAchievementsId", raid.ToString());
            Log($"\"{Safe(() => game.Title) ?? "?"}\" re-link → raid {raid} (was {(string.IsNullOrEmpty(cur) ? "<none>" : cur)}).");
            return true;
        }
        catch (Exception ex) { Log("RelinkRaid failed: " + ex.Message); return false; }
    }

    /// <summary>The platform's ROM-extension CSV for --arc-ext (from the ROM module's profile row);
    /// "" (hash everything) when no profile applies.</summary>
    private static string RomExtensionsFor(string platform)
    {
        try { return RomConfig.Instance.Resolve(platform, "")?.RomExtensions ?? ""; }
        catch { return ""; }
    }

    private static string PriorityCsvFor(string platform)
    {
        try { return RomConfig.Instance.Resolve(platform, "")?.Priority ?? ""; }
        catch { return ""; }
    }

    // Config-driven (RomConfig.ArchiveExtensions), like the plugin's RA scanner — tar/gz/bz2/xz
    // archives are hashed too when configured.
    private static bool IsArchive(string path)
    {
        try { return RomExtractor.IsArchive(path); } catch { return false; }
    }

    private static string ResolveAbsolute(string p)
    {
        try { return Path.IsPathRooted(p) ? p : Path.GetFullPath(Path.Combine(MediaResolver.LbRoot ?? "", p)); }
        catch { return p; }
    }

    private static string Short(string hash) => hash.Length <= 8 ? hash : hash.Substring(0, 8) + "…";
    private static T? Safe<T>(Func<T> f) { try { return f(); } catch { return default; } }
    private static void Log(string msg) => Console.WriteLine("[ra] resolve: " + msg);
}
