// LiteBox-native RA resolution — the FALLBACK that fills a game's RetroAchievementsHash/Id when ExtendDB
// is absent or its RA module is off. Deliberately simple (see the RA fallback notes):
//
//   • Triggers ONLY when the game has no RetroAchievementsHash yet (so it never re-runs for a resolved game,
//     and never spends IO twice).
//   • Computes the hash from the game's MAIN ApplicationPath: arcade → MD5(name); archive → the entries
//     (prefer the first that maps to a raid, else the first entry); plain file → single-file hash.
//   • Sets RetroAchievementsHash, looks the hash up in the console catalogue, and sets RetroAchievementsId
//     when found. Then LiteBox's normal RA panel takes over from the raid.
//
// Known, accepted limits: it does NOT correct a wrong existing hash, and a raid that appears in RA only
// AFTER a game was resolved won't be picked up here — both are jobs for the (future) RA options scan.
// BLOCKING (RAHasher + maybe a catalogue download) — call from a background thread.

#nullable enable

using System;
using System.IO;
using LbApiHost.Host.Data;
using LbApiHost.Host.Media;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Ra;

internal static class RaResolveLite
{
    private static readonly string[] ArchiveExts = { "zip", "7z", "rar" };

    /// <summary>Fills hash+raid for a game that has none. Returns true when it set at least the hash.
    /// Caller gates this to "ExtendDB isn't handling RA" (RomBridge.RaActive == false).</summary>
    public static bool Resolve(IGame game)
    {
        if (game is not ILiteBoxFields fields) return false;
        try
        {
            string title = Safe(() => game.Title) ?? "?";

            // Trigger only if no hash yet.
            string cur = fields.GetField("RetroAchievementsHash") ?? "";
            if (!string.IsNullOrEmpty(cur)) return false;

            string? platform = Safe(() => game.Platform);
            int? cid = RaPlatformMap.ConsoleIdFor(platform);
            if (cid == null) { Log($"\"{title}\" platform \"{platform}\" not RA-mapped → skip."); return false; }
            int consoleId = cid.Value;

            string? appPath = Safe(() => game.ApplicationPath);
            if (string.IsNullOrWhiteSpace(appPath)) { Log($"\"{title}\" no ApplicationPath → skip."); return false; }
            string abs = ResolveAbsolute(appPath!);
            if (string.IsNullOrEmpty(abs) || !File.Exists(abs)) { Log($"\"{title}\" ROM file missing ({abs}) → skip."); return false; }

            string? hash = null; int raid = 0; string how;

            if (consoleId == RaPlatformMap.ArcadeConsoleId)
            {
                hash = RaHasherLite.ArcadeNameHash(abs);
                raid = RaCatalogLite.LookupRaid(consoleId, hash);
                how = "arcade name-hash";
            }
            else if (IsArchive(abs))
            {
                Log($"\"{title}\" [{platform}/{consoleId}] archive → hashing entries (RAHasher --arc-details)…");
                var entries = RaHasherLite.ComputeArchiveEntries(consoleId, abs, "");   // hash all; prefer-raid below
                if (entries.Count == 0) { Log($"\"{title}\" archive yielded no entry → skip."); return false; }
                // Prefer-raid: first entry that maps to a raid, else the first entry (hash only).
                foreach (var e in entries)
                {
                    int r = RaCatalogLite.LookupRaid(consoleId, e.Hash);
                    if (r > 0) { hash = e.Hash; raid = r; break; }
                }
                if (hash == null) hash = entries[0].Hash;   // none had a raid → first entry, hash only
                how = $"archive ({entries.Count} entr{(entries.Count == 1 ? "y" : "ies")}, {(raid > 0 ? "raid-bearing entry" : "first entry")})";
            }
            else
            {
                Log($"\"{title}\" [{platform}/{consoleId}] file → single-file hash (RAHasher)…");
                hash = RaHasherLite.ComputeSingle(consoleId, abs);
                if (!string.IsNullOrEmpty(hash)) raid = RaCatalogLite.LookupRaid(consoleId, hash!);
                how = "single-file";
            }

            if (string.IsNullOrEmpty(hash)) { Log($"\"{title}\" [{platform}/{consoleId}] hash failed (via {how}) → nothing set."); return false; }

            fields.SetField("RetroAchievementsHash", hash);
            if (raid > 0)
            {
                fields.SetField("RetroAchievementsId", raid.ToString());
                Log($"\"{title}\" [{platform}/{consoleId}] → SET hash={Short(hash!)} raid={raid} (via {how}).");
            }
            else
            {
                Log($"\"{title}\" [{platform}/{consoleId}] → SET hash={Short(hash!)} (no raid match, via {how}).");
            }
            return true;
        }
        catch (Exception ex) { Log("Resolve failed: " + ex.Message); return false; }
    }

    private static bool IsArchive(string path)
    {
        var ext = (Path.GetExtension(path) ?? "").TrimStart('.').ToLowerInvariant();
        foreach (var e in ArchiveExts) if (ext == e) return true;
        return false;
    }

    private static string ResolveAbsolute(string p)
    {
        try { return Path.IsPathRooted(p) ? p : Path.GetFullPath(Path.Combine(MediaResolver.LbRoot ?? "", p)); }
        catch { return p; }
    }

    private static string Short(string hash) => hash.Length <= 8 ? hash : hash.Substring(0, 8) + "…";
    private static T? Safe<T>(Func<T> f) { try { return f(); } catch { return default; } }
    private static void Log(string msg) => Console.WriteLine("[ra-lite] resolve: " + msg);
}
