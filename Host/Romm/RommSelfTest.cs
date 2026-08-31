// --selftest-romm: drives the RomM surface's own route table against a real socket, headless.
//
// The point is the CONTRACT, not the plumbing (HttpSelfTest covers that): a RomM client branches on the
// exact keys of /api/heartbeat, and a missing or misspelt one shows up as a client that silently offers to
// scrape metadata, or refuses to connect at all. So this asserts the shape a client actually reads.
//
// It registers the surface's real routes on its own HttpHost rather than starting RommServer, so it runs
// without the module being enabled or the options DB being open.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using LbApiHost.Host.Saves;
using LbApiHost.Host.Web;

namespace LbApiHost.Host.Romm;

internal static class RommSelfTest
{
    private static int _failed;
    private static int _passed;

    public static int Run()
    {
        Console.WriteLine("=== RomM surface self-test ===");

        var router = new Router();
        RommServer.RegisterRoutes(router);
        var host = new HttpHost("romm-selftest", router)
        {
            Intercept = req => string.Equals(req.Method, "OPTIONS", StringComparison.Ordinal) ? RommApi.Preflight() : null,
        };

        try
        {
            host.Start(0);
            var baseUrl = $"http://127.0.0.1:{host.CurrentPort}";
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

            Heartbeat(http, baseUrl);
            Config(http, baseUrl);
            Refusals(http, baseUrl);
            Preflight(http, baseUrl);
            Landing(http, baseUrl);
            Auth(http, baseUrl);
        }
        catch (Exception ex) { Fail("harness", ex.ToString()); }
        finally { try { host.Stop(); } catch { } CloseScratchDb(); }

        Console.WriteLine($"=== {_passed} passed, {_failed} failed ===");
        return _failed == 0 ? 0 : 1;
    }

    private static void Heartbeat(HttpClient http, string baseUrl)
    {
        var r = http.GetAsync(baseUrl + "/api/heartbeat").GetAwaiter().GetResult();
        Check("heartbeat answers 200 without auth", r.StatusCode == HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(r.Content.ReadAsStringAsync().GetAwaiter().GetResult());
        var root = doc.RootElement;

        foreach (var section in new[] { "SYSTEM", "METADATA_SOURCES", "FILESYSTEM", "EMULATION", "FRONTEND", "OIDC", "TASKS" })
            Check($"heartbeat carries {section}", root.TryGetProperty(section, out _));

        Check("heartbeat reports the RomM version it matches",
            root.GetProperty("SYSTEM").GetProperty("VERSION").GetString() == RommConfig.RommVersion);
        Check("heartbeat never asks for the setup wizard",
            root.GetProperty("SYSTEM").GetProperty("SHOW_SETUP_WIZARD").GetBoolean() == false);
        Check("heartbeat declares no metadata source",
            root.GetProperty("METADATA_SOURCES").GetProperty("ANY_SOURCE_ENABLED").GetBoolean() == false);
        Check("heartbeat declares OIDC off",
            root.GetProperty("OIDC").GetProperty("ENABLED").GetBoolean() == false);
        Check("heartbeat exposes a platform list",
            root.GetProperty("FILESYSTEM").GetProperty("FS_PLATFORMS").ValueKind == JsonValueKind.Array);

        var meta = http.GetAsync(baseUrl + "/api/heartbeat/metadata/igdb").GetAwaiter().GetResult();
        Check("per-source heartbeat answers a bare false",
            meta.StatusCode == HttpStatusCode.OK
            && meta.Content.ReadAsStringAsync().GetAwaiter().GetResult().Trim() == "false");
    }

    private static void Config(HttpClient http, string baseUrl)
    {
        var r = http.GetAsync(baseUrl + "/api/config").GetAwaiter().GetResult();
        Check("config answers 200", r.StatusCode == HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(r.Content.ReadAsStringAsync().GetAwaiter().GetResult());
        var root = doc.RootElement;
        foreach (var key in new[] { "PLATFORMS_BINDING", "PLATFORMS_VERSIONS", "EJS_NETPLAY_ENABLED", "SCAN_MEDIA" })
            Check($"config carries {key}", root.TryGetProperty(key, out _));
    }

    private static void Refusals(HttpClient http, string baseUrl)
    {
        var r = http.GetAsync(baseUrl + "/api/tasks").GetAwaiter().GetResult();
        var text = r.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        Check("an unimplemented /api route answers 501 in RomM's error shape",
            (int)r.StatusCode == 501 && text.Contains("\"detail\""));

        var miss = http.GetAsync(baseUrl + "/nope").GetAwaiter().GetResult();
        Check("a non-API path answers 404 in the same shape",
            miss.StatusCode == HttpStatusCode.NotFound
            && miss.Content.ReadAsStringAsync().GetAwaiter().GetResult().Contains("\"detail\""));
    }

    private static void Preflight(HttpClient http, string baseUrl)
    {
        // Must succeed even on a path the table refuses — a browser client never gets to the real request
        // otherwise.
        var r = http.SendAsync(new HttpRequestMessage(HttpMethod.Options, baseUrl + "/api/roms")).GetAwaiter().GetResult();
        Check("CORS preflight succeeds on a refused path", (int)r.StatusCode == 204);
    }

    private static void Landing(HttpClient http, string baseUrl)
    {
        var r = http.GetAsync(baseUrl + "/").GetAwaiter().GetResult();
        var text = r.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        Check("the root explains itself to a human",
            r.StatusCode == HttpStatusCode.OK && text.Contains("RomM"));
    }

    // ── Auth ──────────────────────────────────────────────────────────────────
    //
    // Needs somewhere to keep the password verifier and the signing key, so it opens a throwaway options
    // DB rather than the install's. Everything after this point runs against a real credential.

    private const string TestPassword = "correct-horse-battery-staple";
    private static string? _scratchDb;

    private static void Auth(HttpClient http, string baseUrl)
    {
        _scratchDb = Path.Combine(Path.GetTempPath(), "litebox-romm-selftest-" + Guid.NewGuid().ToString("N") + ".db");
        Data.LiteBoxOptionsDb.Open(_scratchDb);
        if (!Data.LiteBoxOptionsDb.Enabled) { Fail("auth", "could not open a scratch options DB"); return; }

        // Before a password exists, nothing but discovery answers.
        var closed = http.GetAsync(baseUrl + "/api/users/me").GetAwaiter().GetResult();
        Check("with no password set, even a valid-looking request is refused", (int)closed.StatusCode == 401);

        RommConfig.SetPassword(TestPassword);
        Check("the password round-trips through the verifier", RommConfig.VerifyPassword(TestPassword)
            && !RommConfig.VerifyPassword(TestPassword + "!"));

        BasicAuth(http, baseUrl);
        OAuthTokens(http, baseUrl);
        ClientTokens(http, baseUrl);
        Pairing(http, baseUrl);
        Pairing8628(http, baseUrl);
        SessionLogin(baseUrl);
        IdLedger();
        PlatformMap();
        Library(http, baseUrl);
        Devices(http, baseUrl);
        Assets(http, baseUrl);
        RomSlots();
        SyncDecisions();
        GamesTable();
        PushBundles();
    }

    // ── What a client actually pushed ─────────────────────────────────────────
    //
    // Freegosy sends one file under its own name, or a ZIP holding a sync marker plus every file under
    // ITS original name — and its background queue always sends the ZIP. It also rebuilds the archive
    // each time "to bypass server-side deduplication", so the upload's own bytes are worthless as a
    // comparison. Everything downstream depends on getting the pieces back out correctly.

    private static void PushBundles()
    {
        Check("a savestate is recognised by its name",
            RommPushPlanner.IsStateName("Sonic.state")
            && RommPushPlanner.IsStateName("Sonic.state3")
            && RommPushPlanner.IsStateName("Sonic.state.auto"));
        Check("a save file is not mistaken for one",
            !RommPushPlanner.IsStateName("Sonic.srm") && !RommPushPlanner.IsStateName("Sonic.sav"));

        var dir = Path.Combine(Path.GetTempPath(), "litebox-romm-bundle-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            var plain = Path.Combine(dir, "Sonic (Japan).srm");
            File.WriteAllText(plain, "battery");

            var single = RommPushPlanner.Expand(plain, "Sonic (Japan).srm", Path.Combine(dir, "w1"));
            Check("a lone file answers with itself",
                single.Count == 1 && single[0].FileName == "Sonic (Japan).srm" && !single[0].IsState);

            // A bundle the way Freegosy builds one: the marker, a save, and a state, with distinct dates.
            var srm = Path.Combine(dir, "src.srm"); File.WriteAllText(srm, "battery");
            var st = Path.Combine(dir, "src.state"); File.WriteAllText(st, "snapshot");
            var mark = Path.Combine(dir, "freegosy_sync.txt"); File.WriteAllText(mark, "2026-08-28T21:00:00");
            var zipPath = Path.Combine(dir, "Game.zip");
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                // The stamp has to be set BEFORE the entry is written: Create mode refuses to modify one
                // that has already been opened.
                void Add(string name, string from, DateTimeOffset when)
                {
                    var e = zip.CreateEntry(name);
                    e.LastWriteTime = when;
                    using var outS = e.Open();
                    using var inS = File.OpenRead(from);
                    inS.CopyTo(outS);
                }
                Add("freegosy_sync.txt", mark, DateTimeOffset.UtcNow);
                Add("Sonic (Japan).srm", srm, new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.Zero));
                Add("Sonic (Japan).state", st, new DateTimeOffset(2026, 8, 2, 10, 0, 0, TimeSpan.Zero));
            }

            var many = RommPushPlanner.Expand(zipPath, "Game.zip", Path.Combine(dir, "w2"));
            Check("a bundle is opened", many.Count == 2);
            Check("the sync marker is not a save", many.All(x => x.FileName != "freegosy_sync.txt"));
            Check("names inside the bundle are kept",
                many.Any(x => x.FileName == "Sonic (Japan).srm") && many.Any(x => x.FileName == "Sonic (Japan).state"));
            Check("save and state are told apart",
                many.Count(x => x.IsState) == 1 && many.Count(x => !x.IsState) == 1);
            Check("each piece keeps the date the ARCHIVE recorded, not the moment it arrived",
                many.First(x => !x.IsState).ModifiedUtc.Date == new DateTime(2026, 8, 1)
                && many.First(x => x.IsState).ModifiedUtc.Date == new DateTime(2026, 8, 2));
            Check("a save and a state of one ROM are planned apart",
                many.Select(x => x.Key).Distinct().Count() == 2);

            // Named anything at all: Freegosy sniffs bytes on its own restore path, and so do we.
            var misnamed = Path.Combine(dir, "not-a-zip.srm");
            File.Copy(zipPath, misnamed);
            Check("a bundle stored under a save's name is still opened",
                RommPushPlanner.Expand(misnamed, "not-a-zip.srm", Path.Combine(dir, "w3")).Count == 2);
        }
        catch (Exception ex) { Fail("push-bundles", ex.ToString()); }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    // ── The sync channel is a NAMED slot ──────────────────────────────────────
    //
    // RomM's clients put `slot` in front of the user, so these names are read by a person and have to
    // be stable — a client stores the slot it chose per ROM and would lose that choice if the name
    // drifted. The model (docs/romm-server-plan.md §5.6ter): "autosave" is the requester's own branch,
    // "romm-cN" another client's, "lb-…" a LiteBox group named by its core. The real thing is called
    // here, not a copy of it. The seed rule and the per-client view need a real scan with real
    // emulator plugins, which a harness cannot honestly fake — they pin at the HTTP layer instead.

    private static void RomSlots()
    {
        SaveGroup Lb(string core, string exe, bool state = false, int? slot = null) =>
            new SaveGroup { EmulatorCore = core, EmulatorFileName = exe, IsState = state, Slot = slot };

        // LiteBox groups: the channel says which line this is, in characters every client survives.
        Check("a RetroArch core names its line",
            RommAssetsApi.LbSlot(Lb("snes9x_libretro", "retroarch.exe")) == "lb-ra-snes9x");
        Check("a second core is a second line",
            RommAssetsApi.LbSlot(Lb("bsnes_libretro", "retroarch.exe")) == "lb-ra-bsnes");
        Check("an emulator without cores answers with itself",
            RommAssetsApi.LbSlot(Lb("", "Dolphin.exe")) == "lb-dolphin");
        Check("a cored emulator that is not RetroArch skips the ra prefix",
            RommAssetsApi.LbSlot(Lb("mGBA", "bizhawk.exe")) == "lb-mgba");
        Check("spaces and dots become dashes, nothing illegal survives",
            RommAssetsApi.LbSlot(Lb("", "Project64 3.0.exe")) == "lb-project64-3-0");

        // Savestate slots: two state slots of one ROM are two saves to choose between; the plain
        // ".state" is slot 0 and stays on the unqualified channel.
        Check("the plain .state stays unqualified",
            RommAssetsApi.StateSuffixed("autosave", true, 0) == "autosave");
        Check("a numbered savestate names its slot",
            RommAssetsApi.StateSuffixed("autosave", true, 2) == "autosave-state2");
        Check("the auto savestate says so",
            RommAssetsApi.StateSuffixed("lb-ra-snes9x", true, -1) == "lb-ra-snes9x-auto");
        Check("a save file takes no suffix",
            RommAssetsApi.StateSuffixed("autosave", false, 3) == "autosave");

        // Branches, seen from the vault view: the requester's own is its autosave, anybody else's is
        // named after its client.
        Check("my branch is my autosave",
            RommAssetsApi.SlotOfGroupId("abcdef#c5", "c5", false, null) == "autosave");
        Check("another client's branch carries its name",
            RommAssetsApi.SlotOfGroupId("abcdef#c7", "c5", false, null) == "romm-c7");
        Check("no requester, no autosave",
            RommAssetsApi.SlotOfGroupId("abcdef#c5", null, false, null) == "romm-c5");
        Check("a LiteBox group with no scan context says the family",
            RommAssetsApi.SlotOfGroupId(System.Guid.NewGuid().ToString("N"), "c5", false, null) == "lb");
        Check("a branch keeps its savestate slots apart",
            RommAssetsApi.SlotOfGroupId("abcdef#c5", "c5", true, 2) == "autosave-state2");

        // The wire name: an asset is advertised under its channel — Argosy seeds its slot table from
        // the file name — except on the autosave channel, which carries the file's real name: it is
        // what says "latest" to Argosy and what a name-blind client writes to disk verbatim.
        string Wire(string slot, string file, bool real) =>
            RommAssetsApi.WireName(new RommAssetView { FileName = file, SlotRomm = slot, ServeRealName = real });

        Check("a named channel names the file after itself",
            Wire("lb-ra-snes9x", "Zelda (France).srm", false) == "lb-ra-snes9x.srm");
        Check("the autosave channel keeps the real name",
            Wire("autosave", "Zelda (France).srm", true) == "Zelda (France).srm");
        Check("a savestate keeps the extension that says it is one",
            Wire("lb-ra-snes9x-state2", "Zelda (France).state2", false) == "lb-ra-snes9x-state2.state2");
        Check("a slot that sanitises to nothing falls back to the real name",
            Wire("///", "Zelda (France).srm", false) == "Zelda (France).srm");

        // The slot-blind detector: Freegosy's Flutter stack, and nothing else known.
        Check("a Dart agent gets the single-line view",
            RommAssetsApi.SlotBlind(new HttpRequest { Headers = { ["User-Agent"] = "Dart/3.3 (dart:io)" } }));
        Check("okhttp (Argosy) gets the full view",
            !RommAssetsApi.SlotBlind(new HttpRequest { Headers = { ["User-Agent"] = "okhttp/5.3.2" } }));
        Check("an unknown agent gets the full view",
            !RommAssetsApi.SlotBlind(new HttpRequest()));
    }

    // ── Negotiate: the decision core ──────────────────────────────────────────
    //
    // The inventory's content_hash is the hash of the client's LAST UPLOAD, not of its current file,
    // and updated_at is the phone's clock — so hash equality means "in sync", hash inequality decides
    // nothing alone, and CONFLICT is reserved for the one honest case. Pure function, pinned here;
    // the HTTP surface around it is pinned in the server block below.

    private static void SyncDecisions()
    {
        var t0 = new System.DateTime(2026, 8, 31, 12, 0, 0, System.DateTimeKind.Utc);
        System.DateTime T(int s2) => t0.AddSeconds(s2);
        string A(System.DateTime c, string? ch, System.DateTime sv, string? sh, bool held, System.DateTime? mark)
            => RommSyncApi.Decide(c, ch, sv, sh, held, mark).action;

        Check("same hash, same date: in sync",
            A(t0, "AB", t0, "AB", true, null) == "no_op");
        Check("same hash but the file moved since its upload: upload",
            A(T(60), "AB", t0, "AB", true, null) == "upload");
        Check("client newer, no mark: upload",
            A(T(60), "AB", t0, "CD", false, null) == "upload");
        Check("server newer, no mark: download",
            A(t0, "AB", T(60), "CD", false, null) == "download");
        Check("both moved since this device last synced: the one honest conflict",
            A(T(60), "AB", T(90), "CD", false, T(-60)) == "conflict");
        // "Did not move" means: not since the MARK. A file dated after the device's last sync has
        // moved, whatever the other side did — the first draft of these two put the unchanged side
        // AFTER the mark and earned the conflict it claimed to rule out.
        Check("server moved, client did not: download, not conflict",
            A(T(-90), "AB", T(90), "CD", false, T(-60)) == "download");
        Check("client moved, server did not: upload, not conflict",
            A(T(90), "AB", T(-90), "CD", false, T(-60)) == "upload");
        Check("same date, hash already held somewhere in the line: no_op",
            A(t0, "AB", t0, "CD", true, null) == "no_op");
        Check("same date, unknown content: accepted — doubt must not cost progress",
            A(t0, "AB", t0, "CD", false, null) == "upload");
        Check("within tolerance is the same date",
            A(t0.AddSeconds(1), "AB", t0, "AB", true, null) == "no_op");
    }

    // ── S5: devices ───────────────────────────────────────────────────────────

    private static void Devices(HttpClient http, string baseUrl)
    {
        var auth = Basic(RommConfig.Username, TestPassword);

        var payload = "{\"name\":\"muOS handheld\",\"platform\":\"muos\",\"mac_address\":\"aa:bb:cc:dd:ee:ff\",\"hostname\":\"rg35xx\"}";
        var created = Post(http, baseUrl + "/api/devices",
            new StringContent(payload, Encoding.UTF8, "application/json"), auth);
        Check("registering a device answers 201 with a device_id", (int)created.StatusCode == 201);
        string deviceId;
        using (var doc = JsonDocument.Parse(created.Content.ReadAsStringAsync().GetAwaiter().GetResult()))
            deviceId = doc.RootElement.GetProperty("device_id").GetString() ?? "";
        Check("the device id is a uuid", Guid.TryParse(deviceId, out _));

        var again = Post(http, baseUrl + "/api/devices",
            new StringContent(payload, Encoding.UTF8, "application/json"), auth);
        using (var doc = JsonDocument.Parse(again.Content.ReadAsStringAsync().GetAwaiter().GetResult()))
            Check("the same fingerprint returns the SAME device with 200",
                (int)again.StatusCode == 200
                && doc.RootElement.GetProperty("device_id").GetString() == deviceId);

        var strict = Post(http, baseUrl + "/api/devices",
            new StringContent(payload.TrimEnd('}') + ",\"allow_existing\":false}", Encoding.UTF8, "application/json"), auth);
        Check("allow_existing=false turns the collision into a 409", (int)strict.StatusCode == 409);

        var listed = Get(http, baseUrl + "/api/devices", auth);
        Check("devices list contains the registration",
            listed.StatusCode == HttpStatusCode.OK
            && listed.Content.ReadAsStringAsync().GetAwaiter().GetResult().Contains(deviceId));

        var renamed = Send(http, HttpMethod.Put, baseUrl + "/api/devices/" + deviceId,
            new StringContent("{\"name\":\"renamed\"}", Encoding.UTF8, "application/json"), auth);
        Check("a device can be renamed",
            renamed.StatusCode == HttpStatusCode.OK
            && renamed.Content.ReadAsStringAsync().GetAwaiter().GetResult().Contains("renamed"));

        var deleted = Send(http, HttpMethod.Delete, baseUrl + "/api/devices/" + deviceId, null, auth);
        Check("a device can be deleted", deleted.StatusCode == HttpStatusCode.OK);
        var gone = Get(http, baseUrl + "/api/devices/" + deviceId, auth);
        Check("a deleted device 404s", (int)gone.StatusCode == 404);
    }

    // ── S5: assets (no LB library here → empty but well-formed, and the refusals) ──

    private static void Assets(HttpClient http, string baseUrl)
    {
        var auth = Basic(RommConfig.Username, TestPassword);

        foreach (var kind in new[] { "saves", "states", "screenshots" })
        {
            var r = Get(http, baseUrl + $"/api/{kind}", auth);
            Check($"{kind} lists as a JSON array",
                r.StatusCode == HttpStatusCode.OK
                && JsonDocument.Parse(r.Content.ReadAsStringAsync().GetAwaiter().GetResult()).RootElement.ValueKind == JsonValueKind.Array);
        }

        // Saves and states are OUT OF SCOPE for now: nothing is served, nothing is accepted. A GET
        // answers with an empty list — truthful, and a client handles it — while a write answers 501,
        // which no client mistakes for "try again".
        var missing = Get(http, baseUrl + "/api/saves/999999", auth);
        Check("any save id reads as absent while saves are off", (int)missing.StatusCode == 404);

        using var form = new MultipartFormDataContent("----romm-selftest-save");
        form.Add(new ByteArrayContent(new byte[512]), "saveFile", "test.srm");
        var upload = Send(http, HttpMethod.Post, baseUrl + "/api/saves?rom_id=424242", form, auth);
        // Le push est rouvert : un rom_id inconnu ne nomme aucune ROM, donc 404 — et non 501, qui
        // voudrait dire « pas implemente ».
        Check("an upload against a rom that does not exist is a 404",
            (int)upload.StatusCode == 404);

        var badDelete = Post(http, baseUrl + "/api/saves/delete",
            new StringContent("{\"saves\":[]}", Encoding.UTF8, "application/json"), auth);
        Check("bulk delete is refused the same way", (int)badDelete.StatusCode == 501);

        // Negotiate: the route Grout REQUIRES — its save sync aborts on any error here.
        var negNoDev = Post(http, baseUrl + "/api/sync/negotiate",
            new StringContent("{\"saves\":[]}", Encoding.UTF8, "application/json"), auth);
        Check("negotiate without a device is refused", (int)negNoDev.StatusCode == 400);

        var neg = Post(http, baseUrl + "/api/sync/negotiate",
            new StringContent(
                "{\"device_id\":\"selftest-device\",\"saves\":[" +
                "{\"rom_id\":424242,\"file_name\":\"x.srm\",\"slot\":\"autosave\"," +
                "\"updated_at\":\"2026-08-31T10:00:00Z\",\"file_size_bytes\":8192}]}",
                Encoding.UTF8, "application/json"), auth);
        var negText = neg.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        long sessionId = 0;
        bool negShape = false, unknownRomIsNoOp = false;
        try
        {
            using var doc = JsonDocument.Parse(negText);
            var root = doc.RootElement;
            sessionId = root.GetProperty("session_id").GetInt64();
            var ops = root.GetProperty("operations");
            negShape = ops.ValueKind == JsonValueKind.Array && ops.GetArrayLength() == 1
                    && root.GetProperty("total_no_op").GetInt32() == 1;
            unknownRomIsNoOp = ops[0].GetProperty("action").GetString() == "no_op"
                            && ops[0].GetProperty("rom_id").GetInt32() == 424242;
        }
        catch { }
        Check("negotiate answers a session and one operation per save",
            neg.StatusCode == HttpStatusCode.OK && negShape && sessionId > 0);
        Check("a rom this server does not hold is a no_op, never an error", unknownRomIsNoOp);

        var done = Post(http, baseUrl + $"/api/sync/sessions/{sessionId}/complete",
            new StringContent("{\"operations_completed\":1,\"operations_failed\":0}",
                Encoding.UTF8, "application/json"), auth);
        var doneText = done.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        bool doneShape = false;
        try
        {
            using var doc = JsonDocument.Parse(doneText);
            var sess = doc.RootElement.GetProperty("session");
            doneShape = sess.GetProperty("status").GetString() == "completed"
                     && sess.GetProperty("operations_completed").GetInt32() == 1
                     && sess.GetProperty("created_at").ValueKind == JsonValueKind.String
                     && sess.GetProperty("updated_at").ValueKind == JsonValueKind.String;
        }
        catch { }
        Check("complete closes the session with the shape Grout models", doneShape);

        var ghost = Post(http, baseUrl + "/api/sync/sessions/999999999/complete",
            new StringContent("{}", Encoding.UTF8, "application/json"), auth);
        Check("an unknown session reads as absent", (int)ghost.StatusCode == 404);

        // S6: collections + the rom_user write-back.
        var cols = Get(http, baseUrl + "/api/collections", auth);
        var colsText = cols.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        Check("collections list carries the Favorites collection",
            cols.StatusCode == HttpStatusCode.OK && colsText.Contains("\"is_favorite\":true"));

        var putUser = Send(http, HttpMethod.Put, baseUrl + "/api/roms/424242/user",
            new StringContent("{\"rating\":8}", Encoding.UTF8, "application/json"), auth);
        Check("rom_user PUT on an unknown rom 404s", (int)putUser.StatusCode == 404);

        using (var colsDoc = JsonDocument.Parse(colsText))
        {
            int favId = colsDoc.RootElement.EnumerateArray()
                .First(c => c.GetProperty("is_favorite").GetBoolean()).GetProperty("id").GetInt32();
            var readOnly = Post(http, baseUrl + $"/api/collections/{favId}/roms",
                new StringContent("{\"rom_ids\":[]}", Encoding.UTF8, "application/json"), auth);
            Check("Favorites membership with no ids is a 400", (int)readOnly.StatusCode == 400);
        }
    }

    // ── S3: the id ledger, and what a rom_id names ────────────────────────────
    //
    // A rom_id names a GAME AND A FILE. The '*' row is the game's default slot — it names no file, which
    // is what lets a listing hand out ids without opening a single archive. A lock does not mint a
    // private id: it SELECTS which existing one a client is served, so the catalogue stays shared.

    private static void IdLedger()
    {
        var store = Path.Combine(Path.GetTempPath(), "litebox-romm-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            RommDb.UseStore(store);

            int nes = RommDb.PlatformId("Nintendo Entertainment System");
            int snes = RommDb.PlatformId("Super Nintendo Entertainment System");
            Check("platform ids are allocated monotonically", nes == 1 && snes == 2);
            Check("asking again returns the SAME id", RommDb.PlatformId("Nintendo Entertainment System") == nes);

            int defA = RommDb.RomId("guid-a", RommDb.DefaultKey);
            int defB = RommDb.RomId("guid-b", RommDb.DefaultKey);
            Check("a game's default slot gets an id without naming a file", defA == 1 && defB == 2);
            Check("asking for it again is the same id", RommDb.RomId("guid-a", RommDb.DefaultKey) == defA);

            int usa = RommDb.RomId("guid-a", "entry:Sonic (USA).md");
            int jap = RommDb.RomId("guid-a", "entry:Sonic (Japan).md");
            Check("each FILE of a game gets its own id", usa == 3 && jap == 4 && usa != defA);

            var named = RommDb.RomOf(usa);
            Check("a rom id names the game and the file",
                named?.GameGuid == "guid-a" && named?.FileKey == "entry:Sonic (USA).md");
            Check("the default row answers with the sentinel, not a file",
                RommDb.RomOf(defA)?.FileKey == RommDb.DefaultKey);
            Check("an id nobody allocated names nothing", RommDb.RomOf(999) == null);

            // Files, assets and collections keep their own sequences.
            int fileA = RommDb.FileId("guid-a", "main");
            int fileA2 = RommDb.FileId("guid-a", "entry:disc2.chd");
            Check("file ids are an independent sequence", fileA == 1 && fileA2 == 2);
            Check("a file id resolves back to its key", RommDb.FileKeyOf(fileA) == "guid-a|main");

            // ── Locks ─────────────────────────────────────────────────────────
            const int clientA = 11, clientB = 12;
            Check("locking answers with the file's id, not a new one",
                RommDb.Lock(clientA, "guid-a", "entry:Sonic (Japan).md") == jap);
            Check("a second client on the SAME file gets the SAME id — the catalogue is shared",
                RommDb.Lock(clientB, "guid-a", "entry:Sonic (Japan).md") == jap);
            Check("a client on another file gets that file's id",
                RommDb.Lock(clientB, "guid-a", "entry:Sonic (USA).md") == usa);

            var locks = RommDb.AllLocks();
            Check("re-locking replaces rather than accumulating",
                locks.Count == 2 && locks.Count(l => l.TokenId == clientB) == 1);
            Check("a lock carries the file it pins",
                locks.First(l => l.TokenId == clientA).FileKey == "entry:Sonic (Japan).md");

            Check("locking a file nobody had asked for allocates it once",
                RommDb.Lock(clientA, "guid-b", "main") == 5
                && RommDb.RomId("guid-b", "main") == 5);

            Check("unlocking drops the hold", RommDb.Unlock(clientA, "guid-a")
                && RommDb.AllLocks().Count(l => l.TokenId == clientA && l.GameGuid == "guid-a") == 0);
            Check("revoking a client drops every hold it had",
                RommDb.UnlockToken(clientA) == 1 && RommDb.AllLocks().All(l => l.TokenId != clientA));

            // ── Persistence ───────────────────────────────────────────────────
            RommDb.UseStore(store);          // drops the ready flag, reopens the same file
            Check("ids survive a reload", RommDb.RomId("guid-a", RommDb.DefaultKey) == defA
                && RommDb.PlatformId("Super Nintendo Entertainment System") == snes);
            Check("a NEW combination after reload continues the sequence, never reuses",
                RommDb.RomId("guid-c", RommDb.DefaultKey) == 6);

            var defaults = RommDb.AllDefaults();
            Check("the boot pass sees every default row and only those",
                defaults.Count == 3 && defaults.Any(d => d.GameGuid == "guid-a" && d.RomId == defA));
        }
        finally
        {
            RommDb.UseStore(null);
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                try { File.Delete(store + suffix); } catch { }
        }
    }

    // ── S3: romm_games ────────────────────────────────────────────────────────
    //
    // The pass itself needs a real IGame and a real emulator, which a harness cannot honestly fake. What
    // it CAN pin is everything the pass stands on, and those are the places a bug actually hides:
    //
    //   • the comma-bounded client list, where ",7," must not match client 17;
    //   • the string surgery that adds and removes a client, done in SQL;
    //   • the generations — including the rule that a pass must not revive what it just killed;
    //   • the unique key, whose NOT NULL columns are what stop "insert if missing" duplicating.

    private static void GamesTable()
    {
        var store = Path.Combine(Path.GetTempPath(), "litebox-rg-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            RommDb.UseStore(store);
            using var conn = RommDb.OpenForIndex();
            if (conn == null) { Check("romm_games: database opens", false); return; }

            // ── La liste bornee par des virgules ──────────────────────────────
            Check("an empty client list stays empty, not a stray separator",
                RommGamesTable.FormatClients(new int[0]) == "");
            Check("a list is bounded on both sides",
                RommGamesTable.FormatClients(new[] { 3, 7 }) == ",3,7,");
            Check("it round-trips",
                string.Join("|", RommGamesTable.ParseClients(",3,7,")) == "3|7");
            Check("an unbounded old form is still read",
                string.Join("|", RommGamesTable.ParseClients("3,7")) == "3|7");

            // THE trap: without the bounding commas, a LIKE for client 7 matches client 17.
            var seventeen = RommGamesTable.FormatClients(new[] { 17 });
            Check("client 17 does not contain client 7", !seventeen.Contains(",7,"));

            // ── Les generations ───────────────────────────────────────────────
            Check("a fresh table starts at generation 1", RommGamesTable.NextGeneration(conn) == 1);

            var rows = new List<RommGameRow>
            {
                new() { GuidLb = "g1", PlatformId = 1, FilePath = @"roms\a.7z", RomPath = "in/a.smc",
                        IsExtract = true, Action = RommRowAction.Add, IsDefaultUtc = DateTime.UtcNow },
                new() { GuidLb = "g1", PlatformId = 1, FilePath = @"roms\a.7z", RomPath = "in/b.smc",
                        IsExtract = true, Action = RommRowAction.Add },
            };
            RommGamesTable.Flush(conn, rows);
            Check("both rows were written with ids", rows[0].RomId > 0 && rows[1].RomId > rows[0].RomId);

            var back = RommGamesTable.ByGame(conn, "g1");
            Check("they read back", back.Count == 2);
            Check("the default is the one that carries the date",
                back.Count(r => r.IsDefaultUtc != null) == 1);

            // Disabling stamps the generation, so the NEXT pass gets a higher one.
            var victim = back.First(r => r.RomPath == "in/b.smc");
            victim.Disabled = 1; victim.DisabledUtc = DateTime.UtcNow; victim.Touch();
            RommGamesTable.Flush(conn, new[] { victim });
            Check("a disabled row pushes the next generation up", RommGamesTable.NextGeneration(conn) == 2);
            Check("the valid row is untouched",
                RommGamesTable.ByGame(conn, "g1").Count(r => r.IsValid) == 1);

            // ── La cle unique ─────────────────────────────────────────────────
            // Same (guid, filepath, rompath) as an existing row: the index must refuse it. Were rompath
            // nullable, two NULLs would read as distinct and every pass would add a duplicate.
            bool refused = false;
            try
            {
                RommGamesTable.Flush(conn, new[] { new RommGameRow
                {
                    GuidLb = "g1", PlatformId = 1, FilePath = @"roms\a.7z", RomPath = "in/a.smc",
                    Action = RommRowAction.Add,
                } });
            }
            catch { refused = true; }
            Check("the unique key refuses a second row for the same file", refused);

            // ── Les clients, en SQL ───────────────────────────────────────────
            int c1 = RommGamesTable.ClientIdFor(conn, tokenId: 101);
            int c17 = RommGamesTable.ClientIdFor(conn, tokenId: 117);
            Check("client indices are allocated once and reused",
                c1 == 1 && c17 == 2 && RommGamesTable.ClientIdFor(conn, 101) == c1);

            var def = RommGamesTable.ByGame(conn, "g1").First(r => r.IsDefaultUtc != null);
            def.Clients.Add(c1); def.Clients.Add(c17); def.Touch();
            RommGamesTable.Flush(conn, new[] { def });
            Check("both clients are on the default",
                RommGamesTable.ByGame(conn, "g1").First(r => r.IsDefaultUtc != null).Clients.Count == 2);

            // Retiring one must remove EXACTLY one — this is the surgery that eats separators.
            RommGamesTable.RetireClient(conn, c1);
            var after = RommGamesTable.ByGame(conn, "g1").First(r => r.IsDefaultUtc != null);
            Check("retiring a client removes it and only it",
                after.Clients.Count == 1 && after.Clients[0] == c17);
            Check("a retired client is out of the live set",
                !RommGamesTable.LiveClients(conn).ContainsKey(c1));

            RommGamesTable.RetireClient(conn, c17);
            var empty = RommGamesTable.ByGame(conn, "g1").First(r => r.IsDefaultUtc != null);
            Check("emptying the list leaves it EMPTY, not a lone comma", empty.Clients.Count == 0);

            // ── Les chemins ───────────────────────────────────────────────────
            Check("paths compare regardless of separator",
                RommIndexPass.PathEq(@"roms\a.7z", "roms/a.7z"));
            Check("a trailing separator does not make a different file",
                RommIndexPass.PathEq(@"roms\dir\", @"roms\dir"));
            Check("different files stay different",
                !RommIndexPass.PathEq(@"roms\a.7z", @"roms\b.7z"));
        }
        finally
        {
            RommDb.UseStore(null);
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                try { File.Delete(store + suffix); } catch { }
        }
    }

    // ── S3: the platform map ──────────────────────────────────────────────────

    private static void PlatformMap()
    {
        Check("canonical LB names resolve to the RomM slugs",
            RommPlatformMap.SlugFor("Nintendo Entertainment System") == "nes"
            && RommPlatformMap.SlugFor("Super Nintendo Entertainment System") == "snes"
            && RommPlatformMap.SlugFor("Sony Playstation") == "psx"
            && RommPlatformMap.SlugFor("Sega Genesis") == "genesis"
            && RommPlatformMap.SlugFor("Nintendo Game Boy Advance") == "gba"
            && RommPlatformMap.SlugFor("Arcade") == "arcade");

        Check("matching tolerates case and punctuation",
            RommPlatformMap.SlugFor("nintendo entertainment system") == "nes"
            && RommPlatformMap.SlugFor("SEGA  Mega-Drive") == "genesis");

        Check("an unknown platform is NOT exported (null, not a guess)",
            RommPlatformMap.SlugFor("My Custom Handheld") == null
            && RommPlatformMap.SlugFor("") == null
            && RommPlatformMap.SlugFor(null) == null);

        // The options override wins, and "-" disables the platform.
        try
        {
            Data.LiteBoxOptionsDb.Set(Data.LiteBoxOption.ScopePlatform, "My Custom Handheld", "Romm.PlatformSlug", "gba");
            Check("the per-platform override binds an unknown name",
                RommPlatformMap.SlugFor("My Custom Handheld") == "gba");
            Data.LiteBoxOptionsDb.Set(Data.LiteBoxOption.ScopePlatform, "Arcade", "Romm.PlatformSlug", "-");
            Check("the '-' override un-exports a mapped platform",
                RommPlatformMap.SlugFor("Arcade") == null);
        }
        finally
        {
            try { Data.LiteBoxOptionsDb.Set(Data.LiteBoxOption.ScopePlatform, "My Custom Handheld", "Romm.PlatformSlug", null); } catch { }
            try { Data.LiteBoxOptionsDb.Set(Data.LiteBoxOption.ScopePlatform, "Arcade", "Romm.PlatformSlug", null); } catch { }
        }
    }

    // ── S3: the library endpoints (no LB library loaded here → empty but well-formed) ──

    private static void Library(HttpClient http, string baseUrl)
    {
        var auth = Basic(RommConfig.Username, TestPassword);

        var platforms = Get(http, baseUrl + "/api/platforms", auth);
        Check("platforms answers 200 with a JSON array",
            platforms.StatusCode == HttpStatusCode.OK
            && JsonDocument.Parse(platforms.Content.ReadAsStringAsync().GetAwaiter().GetResult()).RootElement.ValueKind == JsonValueKind.Array);

        var anon = http.GetAsync(baseUrl + "/api/platforms").GetAwaiter().GetResult();
        Check("platforms requires authentication", (int)anon.StatusCode == 401);

        var roms = Get(http, baseUrl + "/api/roms?limit=25&offset=0", auth);
        Check("roms answers 200", roms.StatusCode == HttpStatusCode.OK);
        using (var doc = JsonDocument.Parse(roms.Content.ReadAsStringAsync().GetAwaiter().GetResult()))
        {
            var root = doc.RootElement;
            foreach (var key in new[] { "items", "total", "limit", "offset", "char_index", "rom_id_index", "filter_values" })
                Check($"the roms page carries {key}", root.TryGetProperty(key, out _));
            Check("the page echoes the requested limit", root.GetProperty("limit").GetInt32() == 25);
            Check("filter_values carries the platforms facet",
                root.GetProperty("filter_values").TryGetProperty("platforms", out var fp)
                && fp.ValueKind == JsonValueKind.Array);
        }

        var missing = Get(http, baseUrl + "/api/roms/424242", auth);
        Check("an unknown rom id answers 404 in RomM's error shape",
            (int)missing.StatusCode == 404
            && missing.Content.ReadAsStringAsync().GetAwaiter().GetResult().Contains("\"detail\""));

        var download = Get(http, baseUrl + "/api/roms/424242/content/whatever.zip", auth);
        Check("the download route exists and 404s an unknown rom (not 501)",
            (int)download.StatusCode == 404
            && download.Content.ReadAsStringAsync().GetAwaiter().GetResult().Contains("\"detail\""));

        var stats = Get(http, baseUrl + "/api/stats", auth);
        using (var doc = JsonDocument.Parse(stats.Content.ReadAsStringAsync().GetAwaiter().GetResult()))
            Check("stats answers the totals shape",
                stats.StatusCode == HttpStatusCode.OK
                && doc.RootElement.TryGetProperty("PLATFORMS", out _)
                && doc.RootElement.TryGetProperty("ROMS", out _));
    }

    private static void BasicAuth(HttpClient http, string baseUrl)
    {
        var anon = http.GetAsync(baseUrl + "/api/users/me").GetAwaiter().GetResult();
        Check("an unauthenticated call is 401 and says how to authenticate",
            (int)anon.StatusCode == 401 && anon.Headers.WwwAuthenticate.Count > 0);

        var wrong = Get(http, baseUrl + "/api/users/me", Basic(RommConfig.Username, "nope"));
        Check("a wrong password is 401", (int)wrong.StatusCode == 401);

        var ok = Get(http, baseUrl + "/api/users/me", Basic(RommConfig.Username, TestPassword));
        Check("Basic auth reaches /api/users/me", ok.StatusCode == HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(ok.Content.ReadAsStringAsync().GetAwaiter().GetResult());
        var root = doc.RootElement;
        Check("the account is user 1, admin, with its granted scopes",
            root.GetProperty("id").GetInt32() == RommAuthApi.UserId
            && root.GetProperty("role").GetString() == "admin"
            && root.GetProperty("username").GetString() == RommConfig.Username
            && root.GetProperty("oauth_scopes").GetArrayLength() == RommScopes.Granted.Length);

        // The scopes we never grant must not be advertised — a client that saw roms.write would offer to
        // edit the library and then be refused.
        var advertised = root.GetProperty("oauth_scopes").EnumerateArray().Select(e => e.GetString()).ToList();
        Check("the refused write scopes are absent from the advertised set",
            !advertised.Contains("roms.write") && !advertised.Contains("platforms.write")
            && !advertised.Contains("users.write") && !advertised.Contains("tasks.run"));
    }

    private static void OAuthTokens(HttpClient http, string baseUrl)
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["username"] = RommConfig.Username,
            ["password"] = TestPassword,
        });
        var r = http.PostAsync(baseUrl + "/api/token", form).GetAwaiter().GetResult();
        Check("the password grant issues a token pair", r.StatusCode == HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(r.Content.ReadAsStringAsync().GetAwaiter().GetResult());
        var access = doc.RootElement.GetProperty("access_token").GetString() ?? "";
        var refresh = doc.RootElement.GetProperty("refresh_token").GetString() ?? "";
        Check("the token response carries the shape a client reads",
            access.Split('.').Length == 3
            && doc.RootElement.GetProperty("token_type").GetString() == "bearer"
            && doc.RootElement.GetProperty("expires").GetInt32() > 0);

        var me = Get(http, baseUrl + "/api/users/me", "Bearer " + access);
        Check("the access token authenticates", me.StatusCode == HttpStatusCode.OK);

        var tampered = access.Substring(0, access.LastIndexOf('.') + 1) + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        var bad = Get(http, baseUrl + "/api/users/me", "Bearer " + tampered);
        Check("a tampered signature is rejected", (int)bad.StatusCode == 401);

        var refreshForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refresh,
        });
        var r2 = http.PostAsync(baseUrl + "/api/token", refreshForm).GetAwaiter().GetResult();
        Check("the refresh grant issues a fresh access token", r2.StatusCode == HttpStatusCode.OK);

        // A refresh token must not be usable AS an access token: the type claim is what stops it.
        var misuse = Get(http, baseUrl + "/api/users/me", "Bearer " + refresh);
        Check("a refresh token is not accepted as an access token", (int)misuse.StatusCode == 401);
    }

    private static void ClientTokens(HttpClient http, string baseUrl)
    {
        var body = new StringContent("{\"name\":\"handheld\"}", Encoding.UTF8, "application/json");
        var created = Post(http, baseUrl + "/api/client-tokens", body, Basic(RommConfig.Username, TestPassword));
        Check("minting a client token answers 201", (int)created.StatusCode == 201);

        using var doc = JsonDocument.Parse(created.Content.ReadAsStringAsync().GetAwaiter().GetResult());
        var raw = doc.RootElement.GetProperty("raw_token").GetString() ?? "";
        Check("the secret is returned once, prefixed the way clients expect",
            raw.StartsWith("rmm_", StringComparison.Ordinal) && raw.Length == 4 + 64);

        var me = Get(http, baseUrl + "/api/users/me", "Bearer " + raw);
        Check("the client token authenticates", me.StatusCode == HttpStatusCode.OK);

        var listed = Get(http, baseUrl + "/api/client-tokens", "Bearer " + raw);
        var listText = listed.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        Check("listing tokens never returns a secret",
            listed.StatusCode == HttpStatusCode.OK && !listText.Contains("rmm_") && listText.Contains("handheld"));

        // A narrowed token must actually be narrow.
        var narrowBody = new StringContent("{\"name\":\"downloader\",\"scopes\":[\"roms.read\"]}", Encoding.UTF8, "application/json");
        var narrowResp = Post(http, baseUrl + "/api/client-tokens", narrowBody, Basic(RommConfig.Username, TestPassword));
        using var narrowDoc = JsonDocument.Parse(narrowResp.Content.ReadAsStringAsync().GetAwaiter().GetResult());
        var narrowRaw = narrowDoc.RootElement.GetProperty("raw_token").GetString() ?? "";
        var refusedByScope = Get(http, baseUrl + "/api/users/me", "Bearer " + narrowRaw);
        Check("a token without me.read is refused with 403, not 401", (int)refusedByScope.StatusCode == 403);

        var junk = Get(http, baseUrl + "/api/users/me", "Bearer rmm_" + new string('0', 64));
        Check("an unknown client token is refused", (int)junk.StatusCode == 401);

        int narrowId = narrowDoc.RootElement.GetProperty("id").GetInt32();
        var deleted = Send(http, HttpMethod.Delete, baseUrl + $"/api/client-tokens/{narrowId}", null,
                           Basic(RommConfig.Username, TestPassword));
        Check("a token can be revoked", deleted.StatusCode == HttpStatusCode.OK);
        var afterDelete = Get(http, baseUrl + "/api/users/me", "Bearer " + narrowRaw);
        Check("a revoked token stops working immediately", (int)afterDelete.StatusCode == 401);
    }

    private static void Pairing(HttpClient http, string baseUrl)
    {
        var created = Post(http, baseUrl + "/api/client-tokens",
            new StringContent("{\"name\":\"to-pair\"}", Encoding.UTF8, "application/json"),
            Basic(RommConfig.Username, TestPassword));
        using var doc = JsonDocument.Parse(created.Content.ReadAsStringAsync().GetAwaiter().GetResult());
        int id = doc.RootElement.GetProperty("id").GetInt32();

        var paired = Post(http, baseUrl + $"/api/client-tokens/{id}/pair", null, Basic(RommConfig.Username, TestPassword));
        using var pairDoc = JsonDocument.Parse(paired.Content.ReadAsStringAsync().GetAwaiter().GetResult());
        var code = pairDoc.RootElement.GetProperty("code").GetString() ?? "";
        Check("pairing issues a short code", paired.StatusCode == HttpStatusCode.OK && code.Length >= 4);

        var pending = http.GetAsync(baseUrl + $"/api/client-tokens/pair/{code}/status").GetAwaiter().GetResult();
        Check("the code reads as pending while outstanding", pending.StatusCode == HttpStatusCode.OK);

        // The device redeems it WITHOUT credentials — the code is the credential.
        var exchanged = http.PostAsync(baseUrl + "/api/client-tokens/exchange",
            new StringContent($"{{\"code\":\"{code}\"}}", Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
        using var exDoc = JsonDocument.Parse(exchanged.Content.ReadAsStringAsync().GetAwaiter().GetResult());
        var raw = exDoc.RootElement.GetProperty("raw_token").GetString() ?? "";
        Check("the device exchanges the code for the secret",
            exchanged.StatusCode == HttpStatusCode.OK && raw.StartsWith("rmm_", StringComparison.Ordinal));

        var works = Get(http, baseUrl + "/api/users/me", "Bearer " + raw);
        Check("the paired token authenticates", works.StatusCode == HttpStatusCode.OK);

        var again = http.PostAsync(baseUrl + "/api/client-tokens/exchange",
            new StringContent($"{{\"code\":\"{code}\"}}", Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
        Check("a pair code is single-use", (int)again.StatusCode == 404);
    }

    // ── Device pairing (what Argosy runs first) ───────────────────────────────

    private static void Pairing8628(HttpClient http, string baseUrl)
    {
        var auth = Basic(RommConfig.Username, TestPassword);

        var initBody = "{\"client_device_identifier\":\"abc-123\",\"name\":\"Pixel 9\",\"client\":\"argosy\"," +
                       "\"platform\":\"android\",\"client_version\":\"1.2.3\"," +
                       "\"requested_scopes\":[\"me.read\",\"roms.read\",\"assets.write\",\"roms.write\"]}";
        var init = http.PostAsync(baseUrl + "/api/auth/device/init",
            new StringContent(initBody, Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
        Check("device init answers 201 WITHOUT credentials", (int)init.StatusCode == 201);

        string deviceCode, userCode;
        using (var doc = JsonDocument.Parse(init.Content.ReadAsStringAsync().GetAwaiter().GetResult()))
        {
            var root = doc.RootElement;
            deviceCode = root.GetProperty("device_code").GetString() ?? "";
            userCode = root.GetProperty("user_code").GetString() ?? "";
            Check("init returns the codes and the verification path in RomM's shape",
                deviceCode.Length == 64 && userCode.Length == 8
                && root.GetProperty("verification_path").GetString() == "/pair/device"
                && root.GetProperty("verification_path_complete").GetString()!.Contains(userCode)
                && root.GetProperty("expires_in").GetInt32() == 600
                && root.GetProperty("interval").GetInt32() == 5);
            Check("the user code avoids confusable characters",
                userCode.All(c => "ABCDEFGHJKMNPQRSTUVWXYZ23456789".Contains(c)));
        }

        // An unknown scope is a client bug and must be named, not silently dropped.
        var bad = http.PostAsync(baseUrl + "/api/auth/device/init", new StringContent(
            "{\"client_device_identifier\":\"x\",\"name\":\"n\",\"client\":\"c\",\"requested_scopes\":[\"not.a.scope\"]}",
            Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
        Check("an unknown requested scope is refused with 422", (int)bad.StatusCode == 422);

        // Before approval the device must be told to wait, in RFC 8628's own vocabulary.
        var poll = http.PostAsync(baseUrl + "/api/auth/device/token",
            new StringContent($"{{\"device_code\":\"{deviceCode}\"}}", Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
        var pollText = poll.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        Check("polling before approval answers 400 authorization_pending",
            (int)poll.StatusCode == 400 && pollText.Contains("authorization_pending"));

        var tooFast = http.PostAsync(baseUrl + "/api/auth/device/token",
            new StringContent($"{{\"device_code\":\"{deviceCode}\"}}", Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
        Check("polling again immediately answers slow_down",
            (int)tooFast.StatusCode == 400
            && tooFast.Content.ReadAsStringAsync().GetAwaiter().GetResult().Contains("slow_down"));

        // The approval side is credentialed — that is the whole security of the flow.
        var anon = http.GetAsync(baseUrl + "/api/auth/device/pending/" + userCode).GetAwaiter().GetResult();
        Check("the pending view refuses an unauthenticated caller", (int)anon.StatusCode == 401);

        var pending = Get(http, baseUrl + "/api/auth/device/pending/" + userCode, auth);
        using (var doc = JsonDocument.Parse(pending.Content.ReadAsStringAsync().GetAwaiter().GetResult()))
        {
            var allowed = doc.RootElement.GetProperty("allowed_scopes").EnumerateArray()
                .Select(e => e.GetString()).ToList();
            Check("the pending view narrows the request to what we actually grant",
                pending.StatusCode == HttpStatusCode.OK
                && allowed.Contains("roms.read") && allowed.Contains("assets.write")
                && !allowed.Contains("roms.write"));
        }

        var over = Post(http, baseUrl + "/api/auth/device/approve", new StringContent(
            $"{{\"user_code\":\"{userCode}\",\"approved_scopes\":[\"roms.write\"]}}", Encoding.UTF8, "application/json"), auth);
        Check("approving a scope we never grant is refused with 403", (int)over.StatusCode == 403);

        var ok = Post(http, baseUrl + "/api/auth/device/approve",
            new StringContent($"{{\"user_code\":\"{userCode}\"}}", Encoding.UTF8, "application/json"), auth);
        Check("approval succeeds and names the device", ok.StatusCode == HttpStatusCode.OK
            && ok.Content.ReadAsStringAsync().GetAwaiter().GetResult().Contains("device_id"));

        System.Threading.Thread.Sleep(5100);   // respect the poll interval we advertised
        var got = http.PostAsync(baseUrl + "/api/auth/device/token",
            new StringContent($"{{\"device_code\":\"{deviceCode}\"}}", Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
        string issued;
        using (var doc = JsonDocument.Parse(got.Content.ReadAsStringAsync().GetAwaiter().GetResult()))
        {
            issued = doc.RootElement.GetProperty("access_token").GetString() ?? "";
            Check("the approved poll returns a usable client token bound to a device",
                got.StatusCode == HttpStatusCode.OK
                && issued.StartsWith("rmm_", StringComparison.Ordinal)
                && !string.IsNullOrEmpty(doc.RootElement.GetProperty("device_id").GetString()));
        }

        var me = Get(http, baseUrl + "/api/users/me", "Bearer " + issued);
        Check("the paired token authenticates", me.StatusCode == HttpStatusCode.OK);

        var replay = http.PostAsync(baseUrl + "/api/auth/device/token",
            new StringContent($"{{\"device_code\":\"{deviceCode}\"}}", Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
        Check("the device code is single-use once collected",
            (int)replay.StatusCode == 400
            && replay.Content.ReadAsStringAsync().GetAwaiter().GetResult().Contains("expired_token"));

        var page = http.GetAsync(baseUrl + "/pair/device").GetAwaiter().GetResult();
        Check("the approval page is served on this listener",
            page.StatusCode == HttpStatusCode.OK
            && page.Content.ReadAsStringAsync().GetAwaiter().GetResult().Contains("Pair a device"));
    }

    private static void SessionLogin(string baseUrl)
    {
        // Its own client, so the cookie jar cannot leak into the other checks.
        using var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), UseCookies = true };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };

        var login = Post(http, baseUrl + "/api/login", null, Basic(RommConfig.Username, TestPassword));
        Check("login succeeds and sets a session cookie",
            login.StatusCode == HttpStatusCode.OK
            && handler.CookieContainer.GetCookies(new Uri(baseUrl))["romm_session"] != null);

        var me = http.GetAsync(baseUrl + "/api/users/me").GetAwaiter().GetResult();
        Check("the session cookie alone authenticates", me.StatusCode == HttpStatusCode.OK);

        var bye = http.PostAsync(baseUrl + "/api/logout", null).GetAwaiter().GetResult();
        Check("logout succeeds", bye.StatusCode == HttpStatusCode.OK);

        var after = http.GetAsync(baseUrl + "/api/users/me").GetAwaiter().GetResult();
        Check("the session is dead after logout", (int)after.StatusCode == 401);
    }

    private static void CloseScratchDb()
    {
        if (_scratchDb == null) return;
        try { File.Delete(_scratchDb); } catch { /* still open — it is a temp file */ }
    }

    // ── Request helpers ───────────────────────────────────────────────────────

    private static string Basic(string user, string pass)
        => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + pass));

    private static HttpResponseMessage Get(HttpClient http, string url, string auth)
        => Send(http, HttpMethod.Get, url, null, auth);

    private static HttpResponseMessage Post(HttpClient http, string url, HttpContent? content, string auth)
        => Send(http, HttpMethod.Post, url, content, auth);

    private static HttpResponseMessage Send(HttpClient http, HttpMethod method, string url, HttpContent? content, string auth)
    {
        var req = new HttpRequestMessage(method, url);
        if (content != null) req.Content = content;
        if (!string.IsNullOrEmpty(auth)) req.Headers.TryAddWithoutValidation("Authorization", auth);
        return http.SendAsync(req).GetAwaiter().GetResult();
    }

    private static void Check(string what, bool ok)
    {
        if (ok) { _passed++; Console.WriteLine("  PASS  " + what); }
        else { _failed++; Console.WriteLine("  FAIL  " + what); }
    }

    private static void Fail(string what, string detail)
    {
        _failed++;
        Console.WriteLine("  FAIL  " + what + ": " + detail);
    }
}
