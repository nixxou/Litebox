// EmuMovies (api2.emumovies.com), read side, reverse-engineered from LaunchBox's traffic + the offline scraper
// (scrapper-project/EmuMoviesDownloader). See docs/lb-settings-crypto-and-emumovies.md in the ExtendDB repo.
//
//   ValidateCredentials.ashx   POST UserID+CLEAR-pw            → "1"/"0"     (the Test button)
//   GetPlatforms.ashx          POST UserID+pw                 → [{Name, MediaTypes}]
//   GetMediaForPlatform.ashx   POST UserID+pw+Platform        → { gameName: [{MediaType,FileExtension,Crc,FileSize}] }
//   download (no auth)         GET media.emumovies.com/<Platform>/<MediaType>/<gameName><ext>   Referer: media.emumovies.com/
//
// There is NO per-game API — you pull the whole platform dict and match games locally (EmuMoviesCatalog). The
// dict is large (SNES ≈ 2.6 MB), so GetMediaForPlatform is cached on disk with a TTL.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LbApiHost.Host;

namespace LbApiHost.Host.Integrations;

internal sealed class EmuMoviesApi
{
    private const string Api = "https://api2.emumovies.com/";
    public const string MediaBase = "https://media.emumovies.com/";
    public const string MediaReferer = "https://media.emumovies.com/";

    private static readonly TimeSpan CacheTtl = TimeSpan.FromDays(7);

    private static readonly HttpClient _http = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(10),
        AutomaticDecompression = DecompressionMethods.All,
    })
    { Timeout = TimeSpan.FromSeconds(60) };

    private readonly string _userId;
    private readonly string _password;   // CLEAR (decrypt the LB blob before constructing)

    public EmuMoviesApi(string userId, string clearPassword)
    {
        _userId = userId ?? "";
        _password = clearPassword ?? "";
    }

    public bool HasCredentials => !string.IsNullOrWhiteSpace(_userId) && !string.IsNullOrWhiteSpace(_password);

    /// <summary>Build an API client from LaunchBox's own Settings.xml (User ID + the decrypted password blob).
    /// Null when there's no LB root or no credentials configured.</summary>
    public static EmuMoviesApi? FromLbSettings()
    {
        try
        {
            var root = Media.MediaResolver.LbRoot;
            if (string.IsNullOrEmpty(root)) return null;
            var s = new Data.LbSettingsStore(Path.Combine(root, "Data"), null);
            if (!s.Loaded) return null;
            string user = s.Get("EmuMoviesUserId");
            string pass = Data.LbSettingsCrypto.DecryptEmuMoviesPassword(s.Get("EmuMoviesPassword"));
            var api = new EmuMoviesApi(user, pass);
            return api.HasCredentials ? api : null;
        }
        catch { return null; }
    }

    private Dictionary<string, string> Form(params (string k, string v)[] extra)
    {
        var d = new Dictionary<string, string> { ["UserID"] = _userId, ["Password"] = _password };
        foreach (var (k, v) in extra) d[k] = v;
        return d;
    }

    // ── Test ──────────────────────────────────────────────────────────────────
    public enum TestResult { Ok, BadCredentials, NoCredentials, NetworkError }

    public async Task<(TestResult Result, string Message)> TestAsync(CancellationToken ct = default)
    {
        if (!HasCredentials) return (TestResult.NoCredentials, "Enter a User ID and password first.");
        try
        {
            using var content = new FormUrlEncodedContent(Form());
            using var resp = await _http.PostAsync(Api + "ValidateCredentials.ashx", content, ct).ConfigureAwait(false);
            string body = (await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Trim();
            if (!resp.IsSuccessStatusCode) return (TestResult.NetworkError, $"EmuMovies returned HTTP {(int)resp.StatusCode}.");
            return body == "1"
                ? (TestResult.Ok, "Successfully logged in to EmuMovies.")
                : (TestResult.BadCredentials, "EmuMovies rejected these credentials.");
        }
        catch (OperationCanceledException) { return (TestResult.NetworkError, "The test was cancelled."); }
        catch (Exception ex) { return (TestResult.NetworkError, "Couldn't reach EmuMovies: " + ex.Message); }
    }

    // ── Platforms ───────────────────────────────────────────────────────────────
    public sealed record PlatformInfo(string Name, IReadOnlyList<string> MediaTypes);

    public async Task<List<PlatformInfo>> GetPlatformsAsync(CancellationToken ct = default)
    {
        var list = new List<PlatformInfo>();
        if (!HasCredentials) return list;
        try
        {
            using var content = new FormUrlEncodedContent(Form());
            using var resp = await _http.PostAsync(Api + "GetPlatforms.ashx", content, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return list;
            string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                string name = el.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "";
                var types = new List<string>();
                if (el.TryGetProperty("MediaTypes", out var mt) && mt.ValueKind == JsonValueKind.Array)
                    foreach (var t in mt.EnumerateArray()) if (t.GetString() is { } s) types.Add(s);
                if (name.Length > 0) list.Add(new PlatformInfo(name, types));
            }
        }
        catch { }
        return list;
    }

    // ── Per-platform media (bulk, disk-cached) ────────────────────────────────
    public sealed record MediaItem(string MediaType, string FileExtension, long Crc, long FileSize);

    // In-memory parse cache — this instance lives for one edit-window (EditGameWindow._emuApi), so the parsed
    // dict is reused across every game of that platform in the session, and freed when the window closes. The
    // disk cache (below) is the cross-session tier.
    private readonly Dictionary<string, Dictionary<string, List<MediaItem>>> _mem = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every media entry for a platform, keyed by EmuMovies game name. In-memory for the window's
    /// lifetime, on disk for <see cref="CacheTtl"/> (the payload is multi-MB). Empty on failure.</summary>
    public async Task<Dictionary<string, List<MediaItem>>> GetMediaForPlatformAsync(string platform, CancellationToken ct = default)
    {
        var result = new Dictionary<string, List<MediaItem>>(StringComparer.OrdinalIgnoreCase);
        if (!HasCredentials || string.IsNullOrWhiteSpace(platform)) return result;
        lock (_mem) { if (_mem.TryGetValue(platform, out var hot)) return hot; }

        string? cache = CachePath(platform);
        string? json = ReadFreshCache(cache);
        if (json == null)
        {
            try
            {
                using var content = new FormUrlEncodedContent(Form(("Platform", platform)));
                using var resp = await _http.PostAsync(Api + "GetMediaForPlatform.ashx", content, ct).ConfigureAwait(false);
                Console.WriteLine($"[emu] GetMediaForPlatform({platform}) HTTP {(int)resp.StatusCode}");
                if (resp.IsSuccessStatusCode)
                {
                    json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    if (cache != null) { try { File.WriteAllText(cache, json); } catch { } }
                }
            }
            catch (Exception ex) { Console.WriteLine("[emu] GetMediaForPlatform failed: " + ex.Message); }
        }
        else Console.WriteLine($"[emu] GetMediaForPlatform({platform}) from cache");
        if (json == null) return result;

        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var game in doc.RootElement.EnumerateObject())
            {
                if (game.Value.ValueKind != JsonValueKind.Array) continue;
                var items = new List<MediaItem>();
                foreach (var it in game.Value.EnumerateArray())
                {
                    string mt = it.TryGetProperty("MediaType", out var a) ? a.GetString() ?? "" : "";
                    string ext = it.TryGetProperty("FileExtension", out var b) ? b.GetString() ?? "" : "";
                    long crc = it.TryGetProperty("Crc", out var c) && c.TryGetInt64(out var cv) ? cv : 0;
                    long fs = it.TryGetProperty("FileSize", out var d) && d.ValueKind == JsonValueKind.Number && d.TryGetInt64(out var fv) ? fv : 0;
                    if (mt.Length > 0) items.Add(new MediaItem(mt, ext, crc, fs));
                }
                if (items.Count > 0) result[game.Name] = items;
            }
        }
        catch { }
        lock (_mem) { _mem[platform] = result; }   // keep the parsed dict hot for this window
        return result;
    }

    private static string? CachePath(string platform)
    {
        try
        {
            string dir = LiteBoxPaths.Dir("emumovies");
            string safe = string.Concat(platform.Split(Path.GetInvalidFileNameChars()));
            return Path.Combine(dir, safe + ".json");
        }
        catch { return null; }
    }

    private static string? ReadFreshCache(string? path)
    {
        try
        {
            if (path == null || !File.Exists(path)) return null;
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > CacheTtl) return null;
            var s = File.ReadAllText(path);
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }
        catch { return null; }
    }
}
