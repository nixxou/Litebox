// RA LAUNCH correction — P4 of the RA-engine migration (plugin RaOnLaunch.CorrectIGame parity).
//
// At game launch, the IGame's RetroAchievementsHash/Id are corrected to the entry that ACTUALLY
// launches — the select-time pick is optimistic (prefer-raid over the whole archive) and can
// differ from what the user launched (Select-ROM pick, multi-version archive). Runs in EVERY
// auto-update mode; in "On launch" mode it is also the only auto-resolver, which makes that mode
// real instead of a silent no-op.
//
//   • The exact launched entry comes from LiteBox's launch history (OpLog): the ROM extractor
//     records the in-archive identity at resolve time (RecordLaunchRomEntry), and the version
//     (additional application) launched comes from the same row — both are written BEFORE the
//     process starts, so they are readable when OnGameStarted fires.
//   • Store-first: the archive's per-entry hashes were persisted at select/scan; correcting is a
//     dictionary lookup. An archive never parsed (RA module just turned on, direct launch) is
//     parsed now — bytes-authoritative, the plugin's HealLaunchedEntry spirit.
//   • RAID-ONLY GUARD (plugin parity): the hash is corrected on any mismatch, but a valid stored
//     raid is never downgraded to none — dropping raids is the canary-gated refresh's job.
//
// Fire-and-forget from the launch path (background thread; RAHasher only on a cold archive).

#nullable enable

using System;
using System.IO;
using LbApiHost.Host.Data;
using LbApiHost.Host.Media;
using LbApiHost.Host.Modules;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Ra;

internal static class RaLaunchCorrect
{
    /// <summary>Correct the game's RA fields to the launched entry. Call on a background thread
    /// right after launch; <paramref name="dm"/> supplies the launch-history row.</summary>
    public static void OnGameLaunched(IGame game, HostDataManagerXml? dm)
    {
        try
        {
            if (game is not ILiteBoxFields fields) return;
            if (!LbModules.On(LbModule.RetroAchievements)) return;

            string? platform = Safe(() => game.Platform);
            int? cid = RaPlatformMap.ConsoleIdFor(platform);
            if (cid == null || !RaPlatformState.IsPlatformEnabled(platform ?? "")) return;

            var last = dm?.GetLastLaunchFull(Safe(() => game.Id) ?? "");

            // The launched FILE: the version's path when a version (additional app) launched, else the main ROM.
            string? appPath = Safe(() => game.ApplicationPath);
            if (last?.additionalAppId is { Length: > 0 } appId)
            {
                try
                {
                    foreach (var a in game.GetAllAdditionalApplications())
                        if (string.Equals(Safe(() => a.Id), appId, StringComparison.OrdinalIgnoreCase))
                        { appPath = Safe(() => a.ApplicationPath) ?? appPath; break; }
                }
                catch { }
            }
            if (string.IsNullOrWhiteSpace(appPath)) return;
            string abs;
            try { abs = Path.IsPathRooted(appPath) ? appPath! : Path.GetFullPath(Path.Combine(MediaResolver.LbRoot ?? "", appPath!)); }
            catch { return; }
            if (!File.Exists(abs)) return;

            string? hash = null; int raid = 0;

            string ext = (Path.GetExtension(abs) ?? "").TrimStart('.').ToLowerInvariant();
            bool isArchive = ext is "zip" or "7z" or "rar";
            string launchedEntry = last?.extractedRomPath ?? "";

            if (isArchive && cid.Value != RaPlatformMap.ArcadeConsoleId && launchedEntry.Length > 0)
            {
                long size = 0; try { size = new FileInfo(abs).Length; } catch { }
                string sig = ArchiveSig(abs, size);

                var entries = RaStore.GetEntriesRa(sig);
                if (entries.Count == 0)
                {
                    // Cold archive (never parsed under the engine) → parse it now, then re-read.
                    RaResolveLite.ResolvePath(cid.Value, platform ?? "", abs, force: false);
                    entries = RaStore.GetEntriesRa(sig);
                }

                if (entries.TryGetValue(launchedEntry, out var exact)) { hash = exact.Hash; raid = exact.Raid; }
                else
                {
                    // Separator/base-name drift between the extractor's identity and the listing key.
                    string leaf = Path.GetFileName(launchedEntry);
                    foreach (var kv in entries)
                        if (string.Equals(Path.GetFileName(kv.Key), leaf, StringComparison.OrdinalIgnoreCase))
                        { hash = kv.Value.Hash; raid = kv.Value.Raid; break; }
                }
            }

            // No archive entry identified (plain file, single-rom archive, no history row) → the
            // normal store-first resolution of the launched FILE.
            if (hash == null)
            {
                var picked = RaResolveLite.ResolvePath(cid.Value, platform ?? "", abs, force: false);
                if (picked == null) return;
                hash = picked.Value.Hash; raid = picked.Value.Raid;
            }
            if (!RaResolveLite.IsRealHash(hash)) return;

            string curHash = fields.GetField("RetroAchievementsHash") ?? "";
            string curRaid = fields.GetField("RetroAchievementsId") ?? "";

            if (!string.Equals(curHash, hash, StringComparison.OrdinalIgnoreCase))
            {
                fields.SetField("RetroAchievementsHash", hash);
                Log($"\"{Safe(() => game.Title)}\" launch-corrected hash → {Short(hash!)}.");
            }
            // Raid-only guard: set when we have one; never clear an existing raid here.
            if (raid > 0 && curRaid != raid.ToString())
            {
                fields.SetField("RetroAchievementsId", raid.ToString());
                Log($"\"{Safe(() => game.Title)}\" launch-corrected raid → {raid}.");
            }
        }
        catch (Exception ex) { Log("failed: " + ex.Message); }
    }

    private static string ArchiveSig(string path, long size) => Rom.ArchiveSig.ComputePathSignature(path, size);

    private static string Short(string hash) => hash.Length <= 8 ? hash : hash.Substring(0, 8) + "…";
    private static T? Safe<T>(Func<T> f) { try { return f(); } catch { return default; } }
    private static void Log(string msg) => Console.WriteLine("[ra] launch-correct: " + msg);
}
