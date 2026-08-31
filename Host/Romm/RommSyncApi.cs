// The sync orchestrator — RomM's negotiate contract, the one path Grout REQUIRES.
//
// A modern client does not decide alone what to do with its saves: it sends its whole inventory
// (every local save it holds, all games at once, savestates excluded) and the server answers one
// operation per save — upload | download | conflict | no_op, each with a free-text reason — inside a
// numbered session the client closes with its counts. Grout has NO other sync path: its ResolveSaveSync
// starts with this call and aborts on any error, so before this file existed its save sync failed
// outright against LiteBox. Argosy planned an empty reconcile on our 501 and fell back to its own
// per-game logic; now it gets real answers. Freegosy never calls this.
//
// The decisions are deliberately permissive (docs/romm-server-plan.md §5.6ter): nothing a client
// uploads can overwrite a live save — the push lands in its branch, and promotion is manual — so an
// upload accepted in doubt costs one vault copy, while an upload refused in doubt costs progress or a
// retry loop. All the caution sits at the promotion step, which stays in Game Saves, with the user.
//
// The data is weak by construction and treated as such: `content_hash` in the inventory is the hash
// of the client's LAST UPLOAD, not of its current file (Argosy sends lastUploadedHash), and
// `updated_at` is the phone's clock. So equality of hashes means "in sync", but inequality decides
// nothing by itself — the dates and the device's own sync marks arbitrate, and CONFLICT is reserved
// for the one honest case: both sides moved since this device last synced.
//
// Downloads are only volunteered for ROMs the client mentioned. A fresh device with an empty
// inventory gets an empty plan: Grout's discovery fallback queries /api/saves directly for ROMs with
// no local save (it documents this exact division of labour), and Argosy seeds from the listing too.

#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using LbApiHost.Host.Diag;
using LbApiHost.Host.Web;
using Microsoft.Data.Sqlite;

namespace LbApiHost.Host.Romm;

internal static class RommSyncApi
{
    private const int ToleranceSeconds = 2;

    // ── The decision core, pure so the self-test can pin it ──────────────────

    /// <summary>One negotiate decision. <paramref name="clientUtc"/> is the phone's word for when its
    /// file changed; <paramref name="serverUtc"/>/<paramref name="serverHash"/> describe the newest
    /// asset of the requester's own line; <paramref name="hashHeld"/> says whether the client's hash
    /// matches ANY copy of that line; <paramref name="lastSyncUtc"/> is this device's last sync mark
    /// on that newest asset, when it has one.</summary>
    internal static (string action, string reason) Decide(
        DateTime clientUtc, string? clientHash,
        DateTime serverUtc, string? serverHash,
        bool hashHeld, DateTime? lastSyncUtc)
    {
        var tol = TimeSpan.FromSeconds(ToleranceSeconds);

        bool sameContent = !string.IsNullOrEmpty(clientHash)
                        && string.Equals(clientHash, serverHash, StringComparison.OrdinalIgnoreCase);
        if (sameContent)
        {
            // Their copy IS ours. Only a file that moved since its last upload has anything to send —
            // and the hash is the LAST UPLOAD's, so a newer date is exactly that signal.
            if (clientUtc > serverUtc + tol) return ("upload", "your file changed since its last upload");
            return ("no_op", "in sync");
        }

        // Both sides moved since this device last synced: the one honest conflict. Without a mark the
        // dates arbitrate — a first contact is not a conflict, it is a comparison.
        if (lastSyncUtc is DateTime mark
            && serverUtc > mark + tol
            && clientUtc > mark + tol)
            return ("conflict", "both sides changed since this device last synced");

        if (clientUtc > serverUtc + tol) return ("upload", "yours is newer");
        if (serverUtc > clientUtc + tol) return ("download", "the server line is newer");

        // Same date, different (or unknown) content. A copy we already hold has nothing to teach us;
        // anything else is accepted — it lands in the branch, overwrites nothing, and doubt must not
        // cost the client its progress.
        if (hashHeld) return ("no_op", "already held");
        return ("upload", "same date, unknown content — it will be filed, nothing overwritten");
    }

    // ── HTTP: POST /api/sync/negotiate ────────────────────────────────────────

    public static HttpResponse Negotiate(RouteContext ctx)
    {
        var refused = RommAuthApi.Require(ctx, RommScopes.AssetsRead, out var identity);
        if (refused != null) return refused;
        var req = ctx.Request!;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(req.Body); }
        catch { return RommApi.Error(400, "Malformed negotiate payload"); }

        using (doc)
        {
            var root = doc.RootElement;
            var deviceId = root.TryGetProperty("device_id", out var d) ? d.GetString() ?? "" : "";
            if (deviceId.Length == 0) return RommApi.Error(400, "Missing device_id");
            RommDevices.Touch(deviceId);

            int tokenId0 = identity?.TokenId ?? 0;
            bool slotBlind = RommAssetsApi.SlotBlind(req);

            var ops = new List<Dictionary<string, object?>>();
            int up = 0, down = 0, conflict = 0, noop = 0;

            if (root.TryGetProperty("saves", out var saves) && saves.ValueKind == JsonValueKind.Array)
            {
                // The per-game listing drives a plugin scan, so one scan per distinct ROM, reused
                // across that ROM's entries — a phone's inventory repeats games freely.
                var viewCache = new Dictionary<int, List<RommAssetView>>();

                foreach (var el in saves.EnumerateArray())
                {
                    int romId = el.TryGetProperty("rom_id", out var r) && r.TryGetInt32(out var ri) ? ri : 0;
                    var fileName = el.TryGetProperty("file_name", out var fn) ? fn.GetString() ?? "" : "";
                    var slot = el.TryGetProperty("slot", out var sl) ? sl.GetString() : null;
                    var emulator = el.TryGetProperty("emulator", out var em) ? em.GetString() : null;
                    var clientHash = el.TryGetProperty("content_hash", out var ch) ? ch.GetString() : null;
                    DateTime clientUtc = DateTime.MinValue;
                    if (el.TryGetProperty("updated_at", out var ua)
                        && DateTime.TryParse(ua.GetString(), CultureInfo.InvariantCulture,
                                             DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                                             out var parsed))
                        clientUtc = parsed;

                    string action, reason; int? saveId = null;
                    string? srvTime = null, srvHash = null; string opSlot = RommAssetsApi.DefaultSlot;
                    string? opFile = fileName;

                    var romRow = RommIndexer.RowOf(romId);
                    Unbroken.LaunchBox.Plugins.Data.IGame? game = null;
                    if (romRow != null)
                        try { game = Unbroken.LaunchBox.Plugins.PluginHelper.DataManager.GetGameById(romRow.GuidLb); }
                        catch { }
                    if (game == null || !romRow!.Emulated)
                    {
                        (action, reason) = ("no_op", "this server holds no such rom");
                    }
                    else
                    {
                        if (!viewCache.TryGetValue(romId, out var view))
                        {
                            try { view = RommAssetsApi.ListForGame(game, states: false, identity?.TokenId, romRow, slotBlind); }
                            catch { view = new List<RommAssetView>(); }
                            viewCache[romId] = view;
                        }

                        // The requester's own line — its autosave channel, branch or seed.
                        var line = view.Where(a => a.ServeRealName).ToList();
                        var newest = line.OrderByDescending(a => a.UpdatedUtc).FirstOrDefault();
                        if (newest == null)
                        {
                            (action, reason) = ("upload", "no save held for this rom yet — your line starts with it");
                        }
                        else
                        {
                            bool held = !string.IsNullOrEmpty(clientHash)
                                     && line.Any(a => string.Equals(a.Md5, clientHash, StringComparison.OrdinalIgnoreCase));
                            DateTime? mark = null;
                            try
                            {
                                var sync = RommDevices.SyncsForAsset(newest.Id)
                                    .FirstOrDefault(sy => sy.DeviceId == deviceId);
                                if (sync != null) mark = sync.LastSyncedUtc;
                            }
                            catch { }

                            (action, reason) = Decide(clientUtc, clientHash, newest.UpdatedUtc,
                                                      newest.Md5, held, mark);
                            if (action == "download")
                            {
                                saveId = newest.Id;
                                opFile = RommAssetsApi.WireName(newest);
                                opSlot = newest.SlotRomm ?? RommAssetsApi.DefaultSlot;
                                emulator = newest.Emulator ?? emulator;
                            }
                            srvTime = RommAuthApi.Iso(newest.UpdatedUtc);
                            srvHash = newest.Md5 is { Length: > 0 } m ? m : null;
                        }
                    }

                    switch (action)
                    {
                        case "upload": up++; break;
                        case "download": down++; break;
                        case "conflict": conflict++; break;
                        default: noop++; break;
                    }
                    ops.Add(new Dictionary<string, object?>
                    {
                        ["action"] = action,
                        ["rom_id"] = romId,
                        ["save_id"] = saveId,
                        ["file_name"] = opFile,
                        // The channel the client should use for what follows: its own is always
                        // "autosave" — the slot it reported only tells us which save it meant.
                        ["slot"] = action == "download" ? opSlot
                                 : string.IsNullOrEmpty(slot) ? RommAssetsApi.DefaultSlot : slot,
                        ["emulator"] = emulator,
                        ["reason"] = reason,
                        ["server_updated_at"] = srvTime,
                        ["server_content_hash"] = srvHash,
                    });
                }
            }

            long sessionId = OpenSession(deviceId, tokenId0, ops.Count);
            RommTrace.Note($"negotiate: session #{sessionId}, {ops.Count} save(s) → "
                         + $"{up} upload, {down} download, {conflict} conflict, {noop} no-op");

            return RommApi.Json(new Dictionary<string, object?>
            {
                ["session_id"] = sessionId,
                ["operations"] = ops,
                ["total_upload"] = up,
                ["total_download"] = down,
                ["total_conflict"] = conflict,
                ["total_no_op"] = noop,
            });
        }
    }

    // ── HTTP: POST /api/sync/sessions/{id}/complete ──────────────────────────

    public static HttpResponse Complete(RouteContext ctx)
    {
        var refused = RommAuthApi.Require(ctx, RommScopes.AssetsRead, out _);
        if (refused != null) return refused;

        long id = ctx.GetRouteInt("id", -1);
        int done = 0, failed = 0;
        try
        {
            using var doc = JsonDocument.Parse(ctx.Request!.Body);
            if (doc.RootElement.TryGetProperty("operations_completed", out var c) && c.TryGetInt32(out var ci)) done = ci;
            if (doc.RootElement.TryGetProperty("operations_failed", out var f) && f.TryGetInt32(out var fi)) failed = fi;
            // play_sessions ride the same payload (Argosy); we do not track playtime, and saying so
            // with a null ingest is the truthful answer its schema accepts.
        }
        catch { }

        var session = CloseSession(id, done, failed);
        if (session == null) return RommApi.Error(404, "Sync session not found");
        RommTrace.Note($"sync session #{id} complete: {done} done, {failed} failed");

        return RommApi.Json(new Dictionary<string, object?>
        {
            ["session"] = session,
            ["play_session_ingest"] = null,
        });
    }

    // ── The session ledger ────────────────────────────────────────────────────
    //
    // Sessions persist in the romm db rather than in memory: a client may finish its operations after
    // a LiteBox restart, and a complete answered 404 makes Argosy drop its local rows "to avoid
    // zombies" — correct on its side, needless data loss on ours.

    private static long OpenSession(string deviceId, int tokenId, int planned)
    {
        try
        {
            using var conn = RommDb.OpenForIndex();
            if (conn == null) return 0;
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "INSERT INTO sync_session(device_id, client_token, ops_planned, initiated_utc) " +
                "VALUES($d, $t, $p, $now); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$d", deviceId);
            cmd.Parameters.AddWithValue("$t", tokenId);
            cmd.Parameters.AddWithValue("$p", planned);
            cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
            return Convert.ToInt64(cmd.ExecuteScalar());
        }
        catch (Exception ex) { LbLog.Warn("romm", "sync session open failed: " + ex.Message); return 0; }
    }

    private static Dictionary<string, object?>? CloseSession(long id, int done, int failed)
    {
        try
        {
            using var conn = RommDb.OpenForIndex();
            if (conn == null) return null;
            using (var up = conn.CreateCommand())
            {
                up.CommandText =
                    "UPDATE sync_session SET status='completed', ops_completed=$c, ops_failed=$f, " +
                    "completed_utc=$now WHERE session_id=$id";
                up.Parameters.AddWithValue("$c", done);
                up.Parameters.AddWithValue("$f", failed);
                up.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
                up.Parameters.AddWithValue("$id", id);
                if (up.ExecuteNonQuery() == 0) return null;
            }
            using var q = conn.CreateCommand();
            q.CommandText = "SELECT device_id, status, initiated_utc, completed_utc, ops_planned, " +
                            "ops_completed, ops_failed FROM sync_session WHERE session_id=$id";
            q.Parameters.AddWithValue("$id", id);
            using var r = q.ExecuteReader();
            if (!r.Read()) return null;

            DateTime P(int i, DateTime fb) =>
                DateTime.TryParse(r.IsDBNull(i) ? "" : r.GetString(i), CultureInfo.InvariantCulture,
                                  DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? t : fb;
            var initiated = P(2, DateTime.UtcNow);
            var completed = P(3, DateTime.UtcNow);

            // Grout's SyncSessionSchema wants created_at/updated_at as REAL times (non-pointer in Go);
            // they mirror the two we actually track.
            return new Dictionary<string, object?>
            {
                ["id"] = id,
                ["device_id"] = r.GetString(0),
                ["user_id"] = RommAuthApi.UserId,
                ["status"] = r.GetString(1),
                ["initiated_at"] = RommAuthApi.Iso(initiated),
                ["completed_at"] = RommAuthApi.Iso(completed),
                ["operations_planned"] = r.GetInt32(4),
                ["operations_completed"] = r.GetInt32(5),
                ["operations_failed"] = r.GetInt32(6),
                ["error_message"] = null,
                ["created_at"] = RommAuthApi.Iso(initiated),
                ["updated_at"] = RommAuthApi.Iso(completed),
            };
        }
        catch (Exception ex) { LbLog.Warn("romm", "sync session close failed: " + ex.Message); return null; }
    }
}
