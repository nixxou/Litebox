// Saves / states / screenshots over SaveManager, plus the device-sync verbs — the asset story.
//
// The mapping (the plan's §5.6, honoured literally):
//   • a LIVE save group (SaveManager.ScanBase → plugin scan)  = the asset a client downloads/uploads
//   • every VAULT backup                                       = an older version, listed alongside
//   • upload = SaveManager.Import (which lands a VERSION in the vault and records it, and never
//     writes a live save) THEN SaveManager.Restore, the one path that does write one — through the
//     emulator's own integration plugin, exactly as Edit Game → Game Saves does.
//   • no integration plugin for the game's emulator → the upload stops after the version and the
//     live save is never touched (format honesty: silently copying a client's bytes over a real save
//     is the one outcome this feature must never produce).
//   • bulk delete deletes VAULT entries only; the live save is never deleted through this API.
//   • a game whose ROM is a multi-entry archive answers ONLY a client bound to one of those entries
//     (RommRomPicks), and then only with that entry's saves. Unbound gets an empty list: the versions
//     share a rom id, the client sorts by date and takes the newest, so anything else is a coin toss
//     between somebody else's progress and your own.
//
// Asset identity in the ledger: "live|{gameId}|{groupId}|{s|f}" for a live group,
// "vault|{gameId}|{vaultPath}" for a backup, "screen|{relPath}" for a screenshot. The game id is
// part of a vault key because it has to be: a copy is described by a record, records belong to a
// game, and there is no library-wide index to look a bare path up in. Devices mark what they hold (RommDevices) and
// is_current falls out of comparing timestamps, which is all Grout needs to decide push vs pull.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using LbApiHost.Host.Diag;
using LbApiHost.Host.Saves;
using LbApiHost.Host.Web;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Romm;

/// <summary>One asset as the API sees it, wherever it lives.</summary>
internal sealed class RommAssetView
{
    public int Id;
    public int RomId;
    public string GameId = "";
    public string FileName = "";
    public string AbsPath = "";
    public bool IsDirectory;
    public bool IsState;
    public bool IsLive;
    public long Size;
    public DateTime UpdatedUtc;
    public DateTime CreatedUtc;
    public string? Emulator;
    public string? Slot;
    public string? Md5;
    public bool Missing;
}

internal static class RommAssetsApi
{
    // ── Listing ───────────────────────────────────────────────────────────────

    /// <summary>Every save OR state of one game: the live groups plus their vault versions. Drives the
    /// plugin scan, so it is per-game by design — a whole-library scan would hammer every emulator
    /// integration at once.</summary>
    public static List<RommAssetView> ListForGame(IGame game, bool states, int? tokenId = null)
    {
        var result = new List<RommAssetView>();
        var gameId = RommLibrary.IdOf(game);
        int romId = RommIdMap.RomId(gameId);

        // A multi-entry archive holds versions that share nothing but a rom id. The client cannot tell
        // them apart — it re-sorts this list by date and takes the newest — so the server decides, and
        // an unbound client is served NOTHING rather than a coin toss between somebody else's saves.
        string? boundEntry = null;
        if (IsMultiEntryArchive(game))
        {
            boundEntry = RommRomPicks.For(tokenId, gameId)?.PathInArchive;
            if (boundEntry == null) return result;
        }

        SaveScan scan;
        try { scan = SaveManager.ScanBase(game); }
        catch (Exception ex) { LbLog.Warn("romm", "save scan failed: " + ex.Message); return result; }
        if (scan.Error != null) return result;

        foreach (var g in states ? scan.States : scan.Files)
        {
            if (boundEntry != null && !BelongsToEntry(g, boundEntry)) continue;

            if (g.Active != null && g.ActivePath.Length > 0)
            {
                var mtime = g.LastModified?.ToUniversalTime() ?? DateTime.UtcNow;
                string md5 = "";
                try { md5 = g.ActiveIsDirectory ? SaveManager.DirManifestMd5(g.ActivePath) : SaveManager.FileMd5(g.ActivePath); }
                catch { }
                result.Add(new RommAssetView
                {
                    Id = RommIdMap.AssetId($"live|{gameId}|{g.GroupId}|{(states ? "s" : "f")}"),
                    RomId = romId,
                    GameId = gameId,
                    FileName = Path.GetFileName(g.ActivePath.TrimEnd('\\', '/')),
                    AbsPath = g.ActivePath,
                    IsDirectory = g.ActiveIsDirectory,
                    IsState = states,
                    IsLive = true,
                    Size = g.SizeBytes ?? 0,
                    UpdatedUtc = mtime,
                    CreatedUtc = mtime,
                    Emulator = EmulatorLabel(g),
                    Slot = g.Slot?.ToString(),
                    Md5 = md5,
                    Missing = !(g.ActiveIsDirectory ? Directory.Exists(g.ActivePath) : File.Exists(g.ActivePath)),
                });
            }

            foreach (var b in g.Backups)
            {
                if (b.IsState != states) continue;
                var abs = SaveVault.Abs(b);
                result.Add(new RommAssetView
                {
                    Id = VaultAssetId(b),
                    RomId = romId,
                    GameId = gameId,
                    FileName = b.OriginalFileName is { Length: > 0 } ofn ? ofn : Path.GetFileName(abs),
                    AbsPath = abs,
                    IsDirectory = b.IsDirectory,
                    IsState = states,
                    IsLive = false,
                    Size = b.SizeBytes,
                    // DisplayCreatedUtc, not CreatedUtc: a padlocked copy carries its date a
                    // century ahead on purpose, and a device told a save was made in 2126 would
                    // hold it newest for ever and never pull again.
                    UpdatedUtc = b.DisplayCreatedUtc,
                    CreatedUtc = b.DisplayCreatedUtc,
                    Emulator = EmulatorLabel(g),
                    Slot = b.Slot?.ToString(),
                    Md5 = b.Md5,
                    Missing = !(b.IsDirectory ? Directory.Exists(abs) : File.Exists(abs)),
                });
            }
        }
        return result;
    }

    /// <summary>Does this game's ROM hold several playable entries? Uses the same listing the rom DTO
    /// advertises from, so what we gate on and what the client was shown cannot disagree.</summary>
    private static bool IsMultiEntryArchive(IGame game)
    {
        try
        {
            if (!Rom.RomExtractor.Available) return false;
            var abs = RommLibrary.RomAbsPath(game);
            if (abs == null || !Rom.RomExtractor.IsArchive(abs)) return false;
            return Rom.RomExtractor.ListEntriesDetailed(game, null).Entries.Count > 1;
        }
        catch { return false; }
    }

    /// <summary>Is this save group the bound entry's? A group's SaveGroupId carries the entry as
    /// "entry:{signature}:{path in archive}", so the path is compared, never the file name — two entries
    /// can share a name in different folders.
    ///
    /// A group with no entry key belongs to the archive as a whole and is deliberately excluded: it is
    /// not the bound ROM's save, and serving it is the very mix-up this gate exists to stop.</summary>
    private static bool BelongsToEntry(SaveGroup g, string pathInArchive)
    {
        var key = SaveManager.EntryKeyOf(g.GroupId);
        if (key == null) return false;
        int sep = key.IndexOf(':', "entry:".Length);
        if (sep < 0) return false;
        return string.Equals(key.Substring(sep + 1), pathInArchive, StringComparison.OrdinalIgnoreCase);
    }

    private static string? EmulatorLabel(SaveGroup g)
    {
        if (!string.IsNullOrEmpty(g.EmulatorCore)) return g.EmulatorCore;
        try { return g.Emulator?.Title; } catch { return null; }
    }

    /// <summary>Resolves one asset id back to its current on-disk truth. Null when the id is unknown or
    /// the entity is gone.</summary>
    public static RommAssetView? ById(int assetId)
    {
        var key = RommIdMap.AssetKeyOf(assetId);
        if (key == null) return null;

        if (key.StartsWith("live|", StringComparison.Ordinal))
        {
            var parts = key.Split('|');
            if (parts.Length != 4) return null;
            var game = SafeGame(parts[1]);
            if (game == null) return null;
            bool states = parts[3] == "s";
            return ListForGame(game, states).FirstOrDefault(a => a.Id == assetId);
        }

        if (key.StartsWith("vault|", StringComparison.Ordinal))
        {
            // "vault|{gameId}|{relPath}". A two-field key is one an older build issued, before the
            // game id was part of it; nothing is left to resolve it against, so it reads as gone.
            var parts = key.Split('|');
            if (parts.Length < 3) return null;
            var e = FindVaultEntry(parts[1], string.Join("|", parts, 2, parts.Length - 2));
            return e == null ? null : ViewOf(e, assetId);
        }

        if (key.StartsWith("screen|", StringComparison.Ordinal))
        {
            var rel = key.Substring(7);
            var abs = Path.Combine(ScreensRoot, rel);
            if (!File.Exists(abs)) return null;
            var fi = new FileInfo(abs);
            var gameId = rel.Replace('\\', '/').Split('/')[0];
            return new RommAssetView
            {
                Id = assetId, RomId = RommIdMap.RomId(gameId), GameId = gameId,
                FileName = Path.GetFileName(abs), AbsPath = abs, Size = fi.Length,
                UpdatedUtc = fi.LastWriteTimeUtc, CreatedUtc = fi.CreationTimeUtc, IsLive = false,
            };
        }
        return null;
    }

    /// <summary>Every vault copy of one game, read from its <c>&lt;GameSave&gt;</c> records — no plugin
    /// scan, no emulator touched, so this is the cheap way to see the vault.
    ///
    /// It reads records rather than walking the folder because the folder is not an index: a file
    /// nothing records does not exist for LaunchBox. And there is no library-wide shortcut to read
    /// instead — a game's copies live in that game's records and nowhere else.</summary>
    private static List<VaultEntry> VaultEntriesOf(IGame game)
    {
        var res = new List<VaultEntry>();
        if (game is not ILiteBoxGame lbg) return res;
        var gameId = RommLibrary.IdOf(game);
        int ordinal = -1;
        try
        {
            foreach (var row in lbg.GetSubEntities("GameSave"))
            {
                ordinal++;                                                     // position among ALL rows
                var abs = SaveManager.AbsPath(row.GetValueOrDefault("FilePath") ?? "");
                if (abs.Length == 0 || !SaveVault.IsUnderVault(abs)) continue; // the live file
                bool isDir = Directory.Exists(abs);
                if (!isDir && !File.Exists(abs)) continue;                     // recorded but gone

                long size = 0; DateTime when = DateTime.UtcNow;
                try
                {
                    // CREATION time, as everywhere else in the save layer: it is what retention evicts
                    // on, and what the padlock shifts.
                    if (isDir) { when = Directory.GetCreationTimeUtc(abs); size = SaveVault.DirContentSize(abs); }
                    else { var fi = new FileInfo(abs); size = fi.Length; when = fi.CreationTimeUtc; }
                }
                catch { }

                // A record carrying a Slot is a state. Measured rule, the one the badge cache uses too.
                var slotText = row.GetValueOrDefault("Slot");
                bool isState = !string.IsNullOrWhiteSpace(slotText);
                var gid = row.GetValueOrDefault("SaveGroupId") ?? "";
                res.Add(new VaultEntry
                {
                    GameId = gameId,
                    AppId = row.GetValueOrDefault("AdditionalApplicationId"),
                    GroupId = gid,
                    GroupName = row.GetValueOrDefault("SaveGroupName") is { Length: > 0 } gn
                        ? gn : SaveManager.DefaultGroupName(isState, gid),
                    IsState = isState,
                    Slot = int.TryParse(slotText, out var sl) ? sl : null,
                    VaultPath = SaveVault.Rel(abs),
                    OriginalFileName = row.GetValueOrDefault("OriginalFileName") ?? Path.GetFileName(abs),
                    Title = row.GetValueOrDefault("Title") ?? "",
                    CreatedUtc = when, SizeBytes = size, IsDirectory = isDir,
                    Ordinal = ordinal, Locked = SaveVault.IsLockedPath(abs),
                });
            }
        }
        catch (Exception ex) { LbLog.Warn("romm", "could not read the save records: " + ex.Message); }
        return res;
    }

    private static VaultEntry? FindVaultEntry(string gameId, string vaultPath)
    {
        var game = SafeGame(gameId);
        if (game == null) return null;
        foreach (var e in VaultEntriesOf(game))
            if (string.Equals(e.VaultPath, vaultPath, StringComparison.OrdinalIgnoreCase)) return e;
        return null;
    }

    private static int VaultAssetId(VaultEntry e) =>
        RommIdMap.AssetId($"vault|{e.GameId}|{e.VaultPath}");

    /// <summary>One vault copy as the API sees it. Shared by the per-game listing, the bare listing and
    /// id lookup, which used to build the same object three times over and drift.</summary>
    private static RommAssetView ViewOf(VaultEntry e, int? assetId = null)
    {
        var abs = SaveVault.Abs(e);
        return new RommAssetView
        {
            Id = assetId ?? VaultAssetId(e),
            RomId = RommIdMap.RomId(e.GameId),
            GameId = e.GameId,
            FileName = e.OriginalFileName is { Length: > 0 } ofn ? ofn : Path.GetFileName(abs),
            AbsPath = abs,
            IsDirectory = e.IsDirectory,
            IsState = e.IsState,
            IsLive = false,
            Size = e.SizeBytes,
            UpdatedUtc = e.DisplayCreatedUtc,
            CreatedUtc = e.DisplayCreatedUtc,
            Slot = e.Slot?.ToString(),
            Md5 = e.Md5,
            Missing = !(e.IsDirectory ? Directory.Exists(abs) : File.Exists(abs)),
        };
    }

    private static IGame? SafeGame(string gameId)
    {
        try { return Unbroken.LaunchBox.Plugins.PluginHelper.DataManager.GetGameById(gameId); }
        catch { return null; }
    }

    private static string ScreensRoot => LiteBoxPaths.Dir("romm-screens");

    // ── DTO ───────────────────────────────────────────────────────────────────

    public static object AssetDto(RommAssetView a, string kind, string? deviceId = null)
    {
        var noExt = Path.GetFileNameWithoutExtension(a.FileName);
        var syncPairs = new List<(string deviceId, object dto)>();
        foreach (var s in RommDevices.SyncsForAsset(a.Id))
        {
            var d = RommDevices.ById(s.DeviceId);
            syncPairs.Add((s.DeviceId, new
            {
                device_id = s.DeviceId,
                device_name = d?.Name,
                last_synced_at = RommAuthApi.Iso(s.LastSyncedUtc),
                is_untracked = s.Untracked,
                is_current = s.LastSyncedUtc >= a.UpdatedUtc,
            }));
        }
        // The caller's device sorts first (stable ordering, old-client compat) — upstream's rule.
        var ordered = deviceId == null ? (IEnumerable<(string deviceId, object dto)>)syncPairs
                                       : syncPairs.OrderBy(p => p.deviceId != deviceId);
        var syncs = ordered.Select(p => p.dto).ToList();

        var dto = new Dictionary<string, object?>
        {
            ["id"] = a.Id,
            ["rom_id"] = a.RomId,
            ["user_id"] = RommAuthApi.UserId,
            ["file_name"] = a.FileName,
            ["file_name_no_tags"] = noExt,
            ["file_name_no_ext"] = noExt,
            ["file_extension"] = Path.GetExtension(a.FileName).TrimStart('.'),
            ["file_path"] = kind,
            ["file_size_bytes"] = a.Size,
            ["full_path"] = kind + "/" + a.FileName,
            ["download_path"] = $"/api/{kind}/{a.Id}/content?timestamp={a.UpdatedUtc:yyyyMMddHHmmss}",
            ["missing_from_fs"] = a.Missing,
            ["created_at"] = RommAuthApi.Iso(a.CreatedUtc),
            ["updated_at"] = RommAuthApi.Iso(a.UpdatedUtc),
        };
        if (kind != "screenshots")
        {
            dto["emulator"] = a.Emulator;
            dto["is_public"] = false;
            dto["screenshot"] = null;
        }
        if (kind == "saves")
        {
            dto["slot"] = a.Slot;
            dto["content_hash"] = a.Md5;
            dto["origin_device_id"] = null;
            dto["device_syncs"] = syncs;
        }
        if (kind == "screenshots")
        {
            dto["is_gallery"] = false;
            dto["is_public"] = false;
        }
        return dto;
    }

    // ── HTTP: saves + states (one implementation, kind-switched) ─────────────

    public static HttpResponse SavesCollection(RouteContext ctx) => Collection(ctx, states: false);
    public static HttpResponse StatesCollection(RouteContext ctx) => Collection(ctx, states: true);

    private static HttpResponse Collection(RouteContext ctx, bool states)
    {
        bool post = string.Equals(ctx.Request?.Method, "POST", StringComparison.OrdinalIgnoreCase);
        var refused = RommAuthApi.Require(ctx, post ? RommScopes.AssetsWrite : RommScopes.AssetsRead, out var identity);
        if (refused != null) return refused;

        return post ? Upload(ctx, states, identity?.TokenId) : List(ctx, states, identity?.TokenId);
    }

    private static HttpResponse List(RouteContext ctx, bool states, int? tokenId)
    {
        var kind = states ? "states" : "saves";
        var req = ctx.Request!;
        var deviceId = req.GetQuery("device_id");
        if (deviceId != null) RommDevices.Touch(deviceId);

        int romId = req.GetQueryInt("rom_id", -1);
        if (romId > 0)
        {
            var game = RommLibrary.GameByRomId(romId);
            if (game == null) return RommApi.Error(404, "Rom not found");
            var list = ListForGame(game, states, tokenId);
            var slot = req.GetQuery("slot");
            if (!string.IsNullOrEmpty(slot)) list = list.Where(a => a.Slot == slot).ToList();
            return RommApi.Json(list.Select(a => AssetDto(a, kind, deviceId)).ToArray());
        }

        // No rom filter: the vault view only. Answering a bare listing with every game's live saves
        // would drive every emulator integration in the install at once; the vault is the cheap
        // subset, because a copy is described by a record and the records are already in memory.
        var all = new List<object>();
        IGame[] games;
        try { games = Unbroken.LaunchBox.Plugins.PluginHelper.DataManager?.GetAllGames() ?? Array.Empty<IGame>(); }
        catch (Exception ex) { return RommApi.Error(500, "Could not read the library: " + ex.Message); }
        foreach (var g in games)
        {
            // Same gate as the per-rom listing: a multi-entry archive answers only a bound client, and
            // this bare listing carries no rom to bind against.
            if (IsMultiEntryArchive(g) && RommRomPicks.For(tokenId, RommLibrary.IdOf(g)) == null) continue;
            foreach (var e in VaultEntriesOf(g))
            {
                if (e.IsState != states) continue;
                all.Add(AssetDto(ViewOf(e), kind, deviceId));
            }
        }
        return RommApi.Json(all.ToArray());
    }

    public static HttpResponse SaveById(RouteContext ctx) => AssetById(ctx, "saves");
    public static HttpResponse StateById(RouteContext ctx) => AssetById(ctx, "states");

    private static HttpResponse AssetById(RouteContext ctx, string kind)
    {
        bool put = string.Equals(ctx.Request?.Method, "PUT", StringComparison.OrdinalIgnoreCase);
        var refused = RommAuthApi.Require(ctx, put ? RommScopes.AssetsWrite : RommScopes.AssetsRead, out var identity);
        if (refused != null) return refused;

        var a = ById(ctx.GetRouteInt("id", -1));
        if (a == null) return RommApi.Error(404, "Asset not found");

        if (put) return UpdateContent(ctx, a, identity?.TokenId);
        return RommApi.Json(AssetDto(a, kind, ctx.Request!.GetQuery("device_id")));
    }

    public static HttpResponse Content(RouteContext ctx)
    {
        var refused = RommAuthApi.Require(ctx, RommScopes.AssetsRead, out _);
        if (refused != null) return refused;

        var a = ById(ctx.GetRouteInt("id", -1));
        if (a == null) return RommApi.Error(404, "Asset not found");

        if (a.IsDirectory)
        {
            // A folder save (memcard dirs) ships as one zip so a client gets a single artifact.
            var resp = HttpResponse.FromChunked(stream =>
            {
                using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
                foreach (var f in Directory.GetFiles(a.AbsPath, "*", SearchOption.AllDirectories))
                {
                    var entry = zip.CreateEntry(Path.GetRelativePath(a.AbsPath, f).Replace('\\', '/'), CompressionLevel.NoCompression);
                    using var es = entry.Open();
                    using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    fs.CopyTo(es);
                }
            }, "application/zip");
            resp.Headers["Content-Disposition"] = HttpResponse.BuildDisposition(a.FileName + ".zip");
            return resp;
        }

        if (!File.Exists(a.AbsPath)) return RommApi.Error(404, "Asset file missing on disk");
        return HttpResponse.FromFile(a.AbsPath, "application/octet-stream", ctx.Request, a.FileName);
    }

    // ── Upload ────────────────────────────────────────────────────────────────

    private static HttpResponse Upload(RouteContext ctx, bool states, int? tokenId)
    {
        var req = ctx.Request!;
        var kind = states ? "states" : "saves";
        int romId = req.GetQueryInt("rom_id", -1);
        var game = RommLibrary.GameByRomId(romId);
        if (game == null) return RommApi.Error(404, "Rom not found");

        var deviceId = req.GetQuery("device_id");
        bool overwrite = req.GetQueryBool("overwrite");
        var slot = req.GetQuery("slot");

        using var form = MultipartReader.Parse(req);
        var filePart = form?.File(states ? "stateFile" : "saveFile");
        if (filePart == null || string.IsNullOrEmpty(filePart.FileName))
            return RommApi.Error(400, "Save file has no filename");
        var fileName = Path.GetFileName(filePart.FileName!);

        // Device-conflict guard (upstream's 409): the device pushes over an asset that moved since its
        // last sync, and did not say overwrite → refuse, the device pulls first.
        var existing = ListForGame(game, states).FirstOrDefault(a =>
            a.IsLive && string.Equals(a.FileName, fileName, StringComparison.OrdinalIgnoreCase));
        if (deviceId != null && existing != null && !overwrite)
        {
            var sync = RommDevices.SyncsForAsset(existing.Id).FirstOrDefault(s => s.DeviceId == deviceId);
            if (sync != null && sync.LastSyncedUtc < existing.UpdatedUtc.AddSeconds(-2))
                return RommApi.Error(409, "Save has been updated since your last sync");
        }

        // Land the bytes in a temp file under the client's name (the plugins resolve the TARGET name
        // themselves; the source name matters for extension detection).
        var tmpDir = Path.Combine(Path.GetTempPath(), "litebox-romm-up-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        var tmpFile = Path.Combine(tmpDir, fileName);
        try
        {
            if (!filePart.SaveTo(tmpFile)) return RommApi.Error(500, "Could not store the upload");

            SaveScan scan;
            try { scan = SaveManager.ScanBase(game); }
            catch (Exception ex) { return RommApi.Error(500, "Save scan failed: " + ex.Message); }

            int? slotInt = int.TryParse(slot, out var si) ? si : null;
            var (fresh, target, lerr) = LandUpload(game, scan, tmpFile, fileName, states, slotInt, tokenId);
            if (lerr != null) return RommApi.Error(500, lerr);

            if (target == null)
            {
                // Nothing to promote the version onto — no plugin owns this emulator, or the game
                // has no live save by that name. The version is the whole result, and it is a real
                // one: it is recorded, it is listed, and Game Saves can restore it by hand.
                var v = ViewOf(fresh!);
                if (deviceId != null) RommDevices.MarkSynced(deviceId, v.Id);
                return RommApi.Json(AssetDto(v, kind, deviceId), 201);
            }

            var after = ListForGame(game, states).FirstOrDefault(a =>
                a.IsLive && string.Equals(a.FileName, fileName, StringComparison.OrdinalIgnoreCase))
                ?? ListForGame(game, states).FirstOrDefault(a => a.IsLive);
            if (after == null) return RommApi.Error(500, "The save was applied but no live save was found");

            if (deviceId != null) RommDevices.MarkSynced(deviceId, after.Id);
            return RommApi.Json(AssetDto(after, kind, deviceId), 201);
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { }
        }
    }

    /// <summary>The write path both POST and PUT go through: the version first, the live save second.
    ///
    /// Import only ever lands a copy in the vault and records it — it does not write a live save, and
    /// that separation is the point. Restore is what writes one, through the emulator's own plugin, and
    /// it archives whatever it displaces on the way, so the pre-upload state stays one click away in
    /// Game Saves. With no plugin the second half simply does not happen, and the caller is told.</summary>
    private static (VaultEntry? entry, SaveGroup? group, string? error) LandUpload(
        IGame game, SaveScan scan, string tmpFile, string fileName, bool states, int? slot, int? tokenId = null)
    {
        // Which ROM of the archive this save belongs to. The binding is authoritative — it is what the
        // client was actually given — and the file name is the fallback, the same rule the Game Saves
        // dialog pre-selects with. Without one of the two the copy lands in the main bucket under a
        // fresh group, which is where every RomM upload used to go.
        var entry = ResolveEntry(game, fileName, tokenId);

        // The group this upload is FOR: the live save carrying the same name, as before.
        var target = (states ? scan.States : scan.Files).FirstOrDefault(g =>
            g.Active != null && g.Plugin != null &&
            string.Equals(Path.GetFileName(g.ActivePath.TrimEnd('\\', '/')), fileName, StringComparison.OrdinalIgnoreCase));

        // Import returns an error or nothing, never the copy it made — so the new record is the one
        // that was not there a moment ago.
        var before = new HashSet<string>(VaultEntriesOf(game).Select(e => e.VaultPath), StringComparer.OrdinalIgnoreCase);
        var err = SaveManager.Import(game, tmpFile, states, slot, target?.AppId, entry: entry);
        if (err != null) return (null, null, "Import failed: " + err);

        var fresh = VaultEntriesOf(game).FirstOrDefault(e => !before.Contains(e.VaultPath));
        if (fresh == null) return (null, null, "Import reported success but no version was recorded");
        if (target == null) return (fresh, null, null);

        Adopt(game, fresh, target);
        var rerr = SaveManager.Restore(target, fresh, slot, () => true);
        if (rerr != null) return (fresh, target, "Could not apply the save: " + rerr);
        return (fresh, target, null);
    }

    /// <summary>The archive entry an uploaded save belongs to, or null when the game is not an archive
    /// or nothing identifies the entry.
    ///
    /// The binding first: it records the ROM this very client downloaded, so it is a fact rather than an
    /// inference. Then the file name, because both ends derive it from the ROM — a handheld that ran
    /// "Sonic (Japan).md" writes "Sonic (Japan).srm" — which is exactly how PromptImportEntry
    /// pre-selects in the desktop dialog.</summary>
    private static SaveEntry? ResolveEntry(IGame game, string fileName, int? tokenId)
    {
        try
        {
            var entries = SaveEntries.For(game, null);
            if (entries.Count == 0) return null;

            var pick = RommRomPicks.For(tokenId, RommLibrary.IdOf(game));
            if (pick != null)
            {
                var bound = entries.FirstOrDefault(e =>
                    string.Equals(e.PathInArchive, pick.PathInArchive, StringComparison.OrdinalIgnoreCase));
                if (bound != null) return bound;
            }

            var stem = Path.GetFileNameWithoutExtension(fileName);
            return entries.FirstOrDefault(e => string.Equals(
                Path.GetFileNameWithoutExtension(e.FileName), stem, StringComparison.OrdinalIgnoreCase));
        }
        catch { return null; }
    }

    /// <summary>Moves a freshly imported copy into the group it is a version of.
    ///
    /// Import gives a standalone import a fresh group id of its own — right for Add Save File, wrong
    /// here: a device pushing its save every night would leave one orphan group per push on the game's
    /// save page. The record is rewritten through the same primitive Edit Label uses, so LaunchBox reads
    /// the result as one more copy of the group, which is exactly what it is.</summary>
    private static void Adopt(IGame game, VaultEntry e, SaveGroup g)
    {
        try
        {
            if (game is not ILiteBoxGame lbg) return;
            var rows = lbg.GetSubEntities("GameSave")
                          .Select(r => new Dictionary<string, string>(r, StringComparer.Ordinal)).ToList();
            var abs = SaveVault.Abs(e);
            var row = rows.FirstOrDefault(r =>
                SaveManager.PathEq(SaveManager.AbsPath(r.GetValueOrDefault("FilePath") ?? ""), abs));
            if (row == null) return;
            row["SaveGroupId"] = g.GroupId;
            row["MatchLineageId"] = g.GroupId;
            row["SaveGroupName"] = g.GroupName;
            row["Title"] = "Uploaded via RomM";          // LaunchBox prints Title as the copy's heading
            if (!string.IsNullOrEmpty(g.AppId)) row["AdditionalApplicationId"] = g.AppId!;
            lbg.SetSubEntities("GameSave", rows);
            e.GroupId = g.GroupId; e.GroupName = g.GroupName; e.Title = row["Title"];
            SaveVault.Notify(e.GameId);
        }
        catch (Exception ex) { LbLog.Warn("romm", "could not adopt the uploaded copy: " + ex.Message); }
    }

    private static HttpResponse UpdateContent(RouteContext ctx, RommAssetView a, int? tokenId)
    {
        // PUT replaces the CONTENT of a live save (multipart, same fields as the POST). Vault versions
        // are history — history does not get edited, a new upload makes a new version.
        if (!a.IsLive) return RommApi.Error(422, "Vault versions are immutable; upload a new save instead");

        var game = SafeGame(a.GameId);
        if (game == null) return RommApi.Error(404, "Rom not found");

        using var form = MultipartReader.Parse(ctx.Request!);
        var filePart = form?.File();
        if (filePart == null) return RommApi.Error(400, "No file in the request");

        SaveScan scan;
        try { scan = SaveManager.ScanBase(game); }
        catch (Exception ex) { return RommApi.Error(500, "Save scan failed: " + ex.Message); }
        if (scan.Plugin == null) return RommApi.Error(422, "No integration plugin for this game's emulator");

        var tmpDir = Path.Combine(Path.GetTempPath(), "litebox-romm-up-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        var tmpFile = Path.Combine(tmpDir, a.FileName);
        try
        {
            if (!filePart.SaveTo(tmpFile)) return RommApi.Error(500, "Could not store the upload");

            // PUT replaces a LIVE save, so the version alone is not an acceptable outcome here — unlike
            // POST, which is happy to leave one when no plugin can promote it.
            var (_, target, lerr) = LandUpload(game, scan, tmpFile, a.FileName, a.IsState,
                                               int.TryParse(a.Slot, out var si) ? si : null, tokenId);
            if (lerr != null) return RommApi.Error(500, lerr);
            if (target == null) return RommApi.Error(422, "No integration plugin could apply this save");

            var after = ById(a.Id) ?? a;
            var deviceId = ctx.Request!.GetQuery("device_id");
            if (deviceId != null) RommDevices.MarkSynced(deviceId, after.Id);
            return RommApi.Json(AssetDto(after, a.IsState ? "states" : "saves", deviceId));
        }
        finally { try { Directory.Delete(tmpDir, recursive: true); } catch { } }
    }

    // ── Bulk delete + sync verbs ─────────────────────────────────────────────

    public static HttpResponse DeleteSaves(RouteContext ctx) => BulkDelete(ctx, "saves");
    public static HttpResponse DeleteStates(RouteContext ctx) => BulkDelete(ctx, "states");

    private static HttpResponse BulkDelete(RouteContext ctx, string bodyKey)
    {
        var refused = RommAuthApi.Require(ctx, RommScopes.AssetsWrite, out _);
        if (refused != null) return refused;
        if (!string.Equals(ctx.Request?.Method, "POST", StringComparison.OrdinalIgnoreCase))
            return RommApi.Error(405, "Method not allowed");

        List<int> ids = new();
        try
        {
            using var doc = JsonDocument.Parse(ctx.Request!.Body);
            if (doc.RootElement.TryGetProperty(bodyKey, out var arr) && arr.ValueKind == JsonValueKind.Array)
                ids = arr.EnumerateArray().Where(e => e.TryGetInt32(out _)).Select(e => e.GetInt32()).ToList();
        }
        catch { return RommApi.Error(400, "Malformed body"); }
        if (ids.Count == 0) return RommApi.Error(400, $"No {bodyKey} were provided");

        var deleted = new List<int>();
        foreach (var id in ids)
        {
            var key = RommIdMap.AssetKeyOf(id);
            // Vault versions only: the LIVE save is never deleted through this API — a client bug must
            // not be able to erase somebody's real progress.
            if (key == null || !key.StartsWith("vault|", StringComparison.Ordinal)) continue;
            var parts = key.Split('|');
            if (parts.Length < 3) continue;
            var e = FindVaultEntry(parts[1], string.Join("|", parts, 2, parts.Length - 2));
            if (e == null) continue;
            // Hand the owner over: deleting a copy also removes the record that describes it, and the
            // record lives on the game. Re-resolving it from the path alone is exactly what is no
            // longer possible.
            if (SaveManager.DeleteBackup(e, SafeGame(e.GameId)) == null) deleted.Add(id);
        }
        return RommApi.Json(deleted.ToArray());
    }

    public static HttpResponse ConfirmDownloaded(RouteContext ctx)
    {
        var refused = RommAuthApi.Require(ctx, RommScopes.DevicesWrite, out _);
        if (refused != null) return refused;

        var a = ById(ctx.GetRouteInt("id", -1));
        if (a == null) return RommApi.Error(404, "Asset not found");

        string? deviceId = null;
        try
        {
            using var doc = JsonDocument.Parse(ctx.Request!.Body);
            if (doc.RootElement.TryGetProperty("device_id", out var d)) deviceId = d.GetString();
        }
        catch { }
        if (string.IsNullOrEmpty(deviceId)) return RommApi.Error(400, "Missing device_id");
        if (RommDevices.ById(deviceId!) == null) return RommApi.Error(404, $"Device with ID {deviceId} not found");

        RommDevices.MarkSynced(deviceId!, a.Id);
        return RommApi.Json(AssetDto(a, a.IsState ? "states" : "saves", deviceId));
    }

    public static HttpResponse Track(RouteContext ctx) => SetTracking(ctx, tracked: true);
    public static HttpResponse Untrack(RouteContext ctx) => SetTracking(ctx, tracked: false);

    private static HttpResponse SetTracking(RouteContext ctx, bool tracked)
    {
        var refused = RommAuthApi.Require(ctx, RommScopes.DevicesWrite, out _);
        if (refused != null) return refused;

        var a = ById(ctx.GetRouteInt("id", -1));
        if (a == null) return RommApi.Error(404, "Asset not found");

        string? deviceId = null;
        try
        {
            using var doc = JsonDocument.Parse(ctx.Request!.Body ?? "{}");
            if (doc.RootElement.TryGetProperty("device_id", out var d)) deviceId = d.GetString();
        }
        catch { }

        RommDevices.SetTracked(deviceId, a.Id, tracked);
        return RommApi.Json(AssetDto(a, a.IsState ? "states" : "saves", deviceId));
    }

    // ── Screenshots ───────────────────────────────────────────────────────────

    public static HttpResponse ScreenshotsCollection(RouteContext ctx)
    {
        bool post = string.Equals(ctx.Request?.Method, "POST", StringComparison.OrdinalIgnoreCase);
        var refused = RommAuthApi.Require(ctx, post ? RommScopes.AssetsWrite : RommScopes.AssetsRead, out _);
        if (refused != null) return refused;

        if (post)
        {
            int romId = ctx.Request!.GetQueryInt("rom_id", -1);
            var game = RommLibrary.GameByRomId(romId);
            if (game == null) return RommApi.Error(404, "Rom not found");

            using var form = MultipartReader.Parse(ctx.Request);
            var part = form?.File("screenshotFile");
            if (part == null || string.IsNullOrEmpty(part.FileName)) return RommApi.Error(400, "No screenshot in the request");

            var gameId = RommLibrary.IdOf(game);
            var dir = Path.Combine(ScreensRoot, gameId);
            Directory.CreateDirectory(dir);
            var target = Path.Combine(dir, Path.GetFileName(part.FileName!));
            if (!part.SaveTo(target)) return RommApi.Error(500, "Could not store the screenshot");

            var rel = Path.GetRelativePath(ScreensRoot, target);
            var view = ById(RommIdMap.AssetId("screen|" + rel));
            return view == null ? RommApi.Error(500, "Stored but unreadable") : RommApi.Json(AssetDto(view, "screenshots"), 201);
        }

        int filterRom = ctx.Request!.GetQueryInt("rom_id", -1);
        var list = new List<object>();
        try
        {
            foreach (var f in Directory.Exists(ScreensRoot)
                     ? Directory.GetFiles(ScreensRoot, "*", SearchOption.AllDirectories)
                     : Array.Empty<string>())
            {
                var rel = Path.GetRelativePath(ScreensRoot, f);
                var view = ById(RommIdMap.AssetId("screen|" + rel));
                if (view == null) continue;
                if (filterRom > 0 && view.RomId != filterRom) continue;
                list.Add(AssetDto(view, "screenshots"));
            }
        }
        catch { }
        return RommApi.Json(list.ToArray());
    }

    public static HttpResponse ScreenshotById(RouteContext ctx)
    {
        var refused = RommAuthApi.Require(ctx, RommScopes.AssetsRead, out _);
        if (refused != null) return refused;
        var a = ById(ctx.GetRouteInt("id", -1));
        return a == null ? RommApi.Error(404, "Screenshot not found") : RommApi.Json(AssetDto(a, "screenshots"));
    }
}
