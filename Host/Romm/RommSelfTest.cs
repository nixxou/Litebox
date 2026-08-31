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
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
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
        RomPicks();
    }

    // ── The client → ROM bindings ─────────────────────────────────────────────
    //
    // The store, not the routes: the API behaviour needs a real multi-entry archive and a real emulator
    // plugin, neither of which a harness can honestly fake. What IS worth pinning here is the contract
    // every surface reads through — including the two properties that are easy to get wrong, that a
    // second download RE-POINTS rather than duplicating, and that identity is the pair (token, game).

    private static void RomPicks()
    {
        const int tokenA = 4001, tokenB = 4002;
        const string gameA = "game-aaa", gameB = "game-bbb";
        try
        {
            RommRomPicks.ClearToken(tokenA);
            RommRomPicks.ClearToken(tokenB);

            Check("an unbound client has no ROM binding", RommRomPicks.For(tokenA, gameA) == null);
            Check("a null token never resolves to a binding", RommRomPicks.For(null, gameA) == null);

            RommRomPicks.Set(tokenA, gameA, "roms/Sonic (Japan).md", "Sonic (Japan).md");
            var bound = RommRomPicks.For(tokenA, gameA);
            Check("a binding round-trips through the store",
                bound != null && bound.PathInArchive == "roms/Sonic (Japan).md");

            Check("another client is unaffected by it", RommRomPicks.For(tokenB, gameA) == null);
            Check("another game of the same client is unaffected", RommRomPicks.For(tokenA, gameB) == null);

            // Downloading a second entry moves the binding; it must not leave two rows for one game,
            // which would make For() return whichever came first.
            RommRomPicks.Set(tokenA, gameA, "roms/Sonic (USA).md", "Sonic (USA).md");
            Check("a second download re-points the binding",
                RommRomPicks.For(tokenA, gameA)?.PathInArchive == "roms/Sonic (USA).md");
            Check("re-pointing leaves exactly one row", RommRomPicks.OfToken(tokenA).Count == 1);

            RommRomPicks.Set(tokenA, gameB, "roms/Alex Kidd.md", "Alex Kidd.md");
            Check("bindings are counted per client", RommRomPicks.CountFor(tokenA) == 2);

            Check("clearing one game leaves the others", RommRomPicks.Clear(tokenA, gameA)
                && RommRomPicks.For(tokenA, gameA) == null && RommRomPicks.CountFor(tokenA) == 1);

            RommRomPicks.Set(tokenA, gameA, "roms/Sonic (USA).md", "Sonic (USA).md");
            Check("revoking a client forgets every binding it had",
                RommRomPicks.ClearToken(tokenA) == 2 && RommRomPicks.CountFor(tokenA) == 0);
        }
        catch (Exception ex) { Fail("rom-picks", ex.ToString()); }
        finally { try { RommRomPicks.ClearToken(tokenA); RommRomPicks.ClearToken(tokenB); } catch { } }
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

        var missing = Get(http, baseUrl + "/api/saves/999999", auth);
        Check("an unknown save id 404s", (int)missing.StatusCode == 404);

        using var form = new MultipartFormDataContent("----romm-selftest-save");
        form.Add(new ByteArrayContent(new byte[512]), "saveFile", "test.srm");
        var upload = Send(http, HttpMethod.Post, baseUrl + "/api/saves?rom_id=424242", form, auth);
        Check("an upload against an unknown rom 404s", (int)upload.StatusCode == 404);

        var badDelete = Post(http, baseUrl + "/api/saves/delete",
            new StringContent("{\"saves\":[]}", Encoding.UTF8, "application/json"), auth);
        Check("bulk delete with no ids is a 400", (int)badDelete.StatusCode == 400);

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

    // ── S3: the id ledger ─────────────────────────────────────────────────────

    private static void IdLedger()
    {
        var store = Path.Combine(Path.GetTempPath(), "litebox-romm-ids-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            RommIdMap.UseStore(store);

            int nes = RommIdMap.PlatformId("Nintendo Entertainment System");
            int snes = RommIdMap.PlatformId("Super Nintendo Entertainment System");
            Check("platform ids are allocated monotonically", nes == 1 && snes == 2);
            Check("asking again returns the SAME id", RommIdMap.PlatformId("Nintendo Entertainment System") == nes);

            int romA = RommIdMap.RomId("guid-a");
            int romB = RommIdMap.RomId("guid-b");
            int fileA = RommIdMap.FileId("guid-a", "main");
            int fileA2 = RommIdMap.FileId("guid-a", "entry:disc2.chd");
            Check("rom and file ids are independent sequences",
                romA == 1 && romB == 2 && fileA == 1 && fileA2 == 2);

            Check("reverse lookup finds the source key",
                RommIdMap.GameIdOf(romB) == "guid-b"
                && RommIdMap.PlatformNameOf(nes) == "Nintendo Entertainment System"
                && RommIdMap.GameIdOf(999) == null);

            // Persistence: flush, drop the in-memory state, reload from the same file.
            RommIdMap.Flush();
            RommIdMap.UseStore(store);
            Check("ids survive a reload from disk",
                RommIdMap.RomId("guid-a") == romA
                && RommIdMap.PlatformId("Super Nintendo Entertainment System") == snes);
            Check("a NEW entity after reload continues the sequence, never reuses",
                RommIdMap.RomId("guid-c") == 3);
        }
        finally
        {
            RommIdMap.UseStore(null);
            try { File.Delete(store); } catch { }
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
