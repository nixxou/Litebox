// Remote-refreshable media-source policy — LiteBox port of the plugin's PolicyStore +
// MediaSourcePolicyTable pair (Web/Backend/{PolicyStore,MediaSourcePolicy}.cs).
//
// The chains MediaFetch walks (per context × origin) are normally the compiled-in defaults, but an
// operator can override them SERVER-SIDE: two JSON documents — [Base] MediaPolicyUrlWeb (consumed by
// the fetch paths: web proxy, thumbs, desktop cards) and [Base] MediaPolicyUrlHelper (consumed by
// ListUrls, the editor grids' URL lister) — are fetched on a use-driven 10-minute timer and swapped
// in atomically. That's the remote kill-switch the plugin had: when a CDN dies or changes layout,
// the chain order can be fixed for every install without shipping a release.
//
// Contract kept from the plugin:
//   • Use-driven refresh only — no force-refresh API, one in-flight fetch per slot (CAS), the 10-min
//     slot is reserved up front so a failing remote is retried at most every 10 minutes.
//   • A fetch/parse/validation failure keeps the current snapshot; the built-in table is the pinned
//     fallback when the URL is blank.
//   • Validation: all 7 context tables present and non-empty, each with a "default" entry, no empty
//     chains — a policy that fails any of this is rejected wholesale.
//   • JSON shape: flat object, properties Thumb/Cover/GalleryImage/Manual/Music/Video/OtherNonMedia,
//     each an { "origin": ["KindName", ...] } dictionary (enum member names), plus the optional
//     BlockIfScreenscraperFail flag. Same documents the plugin consumes — one server serves both.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace LbApiHost.Host.Media;

/// <summary>One policy snapshot: the 7 per-context (origin → ordered kinds) tables + flags.
/// Immutable once published (whole-instance swap); see the file header for the JSON shape.</summary>
internal sealed class MediaPolicyTable
{
    public Dictionary<string, MediaFetch.MediaSourceKind[]> Thumb { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, MediaFetch.MediaSourceKind[]> Cover { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, MediaFetch.MediaSourceKind[]> GalleryImage { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, MediaFetch.MediaSourceKind[]> Manual { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, MediaFetch.MediaSourceKind[]> Music { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, MediaFetch.MediaSourceKind[]> Video { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, MediaFetch.MediaSourceKind[]> OtherNonMedia { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>When true, a chain containing ScreenscraperApi hard-fails (no fallthrough) while the
    /// SS API is unusable — the operator wants the personal quota consumed, not silently bypassed.</summary>
    public bool BlockIfScreenscraperFail { get; set; }

    private static readonly MediaFetch.MediaSourceKind[] Empty = Array.Empty<MediaFetch.MediaSourceKind>();

    /// <summary>Ordered chain for (context, origin). "default" is the catch-all; empty/"local" origin →
    /// empty chain. The literal origin "default" resolves the catch-all directly (thumb-by-id's
    /// no-origin start).</summary>
    public IReadOnlyList<MediaFetch.MediaSourceKind> ChainFor(MediaFetch.MediaContext ctx, string? originRaw)
    {
        var origin = (originRaw ?? "").Trim().ToLowerInvariant();
        if (origin.Length == 0 || origin == "local") return Empty;

        var table = ctx switch
        {
            MediaFetch.MediaContext.Thumb => Thumb,
            MediaFetch.MediaContext.Cover => Cover,
            MediaFetch.MediaContext.GalleryImage => GalleryImage,
            MediaFetch.MediaContext.Manual => Manual,
            MediaFetch.MediaContext.Music => Music,
            MediaFetch.MediaContext.Video => Video,
            MediaFetch.MediaContext.OtherNonMedia => OtherNonMedia,
            _ => GalleryImage,
        };
        if (table == null) return Empty;
        if (table.TryGetValue(origin, out var chain)) return chain;
        return table.TryGetValue("default", out var fallback) ? fallback : Empty;
    }

    /// <summary>Parse + validate a remote policy document. Throws on malformed JSON, unknown kind
    /// names, or a table that fails <see cref="Validate"/> — the caller keeps its current snapshot.</summary>
    public static MediaPolicyTable FromJson(string json)
    {
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        opts.Converters.Add(new JsonStringEnumConverter());
        var t = JsonSerializer.Deserialize<MediaPolicyTable>(json, opts) ?? new MediaPolicyTable();

        t.Thumb = Rewrap(t.Thumb);
        t.Cover = Rewrap(t.Cover);
        t.GalleryImage = Rewrap(t.GalleryImage);
        t.Manual = Rewrap(t.Manual);
        t.Music = Rewrap(t.Music);
        t.Video = Rewrap(t.Video);
        t.OtherNonMedia = Rewrap(t.OtherNonMedia);

        t.Validate();
        return t;
    }

    /// <summary>Reject unusable tables (see header) — a bad swap would 404 arbitrary requests.</summary>
    public void Validate()
    {
        ValidateTable(nameof(Thumb), Thumb);
        ValidateTable(nameof(Cover), Cover);
        ValidateTable(nameof(GalleryImage), GalleryImage);
        ValidateTable(nameof(Manual), Manual);
        ValidateTable(nameof(Music), Music);
        ValidateTable(nameof(Video), Video);
        ValidateTable(nameof(OtherNonMedia), OtherNonMedia);
    }

    private static void ValidateTable(string name, Dictionary<string, MediaFetch.MediaSourceKind[]> t)
    {
        if (t == null || t.Count == 0) throw new InvalidDataException($"table '{name}' is missing or empty");
        if (!t.ContainsKey("default")) throw new InvalidDataException($"table '{name}' has no 'default' key");
        foreach (var kv in t)
            if (kv.Value == null || kv.Value.Length == 0)
                throw new InvalidDataException($"table '{name}[{kv.Key}]' chain is null or empty");
    }

    private static Dictionary<string, MediaFetch.MediaSourceKind[]> Rewrap(Dictionary<string, MediaFetch.MediaSourceKind[]>? d)
        => d == null ? new(StringComparer.OrdinalIgnoreCase) : new(d, StringComparer.OrdinalIgnoreCase);

    /// <summary>The compiled-in policy (MediaFetch's chain tables), built once. Keep immutable —
    /// it's the known-good fallback for every failed remote fetch.</summary>
    public static MediaPolicyTable BuiltIn { get; } = FromBuiltIn();

    private static MediaPolicyTable FromBuiltIn()
    {
        var src = MediaFetch.BuiltInTables;
        Dictionary<string, MediaFetch.MediaSourceKind[]> Copy(MediaFetch.MediaContext ctx)
            => src.TryGetValue(ctx, out var t)
                ? new Dictionary<string, MediaFetch.MediaSourceKind[]>(t, StringComparer.OrdinalIgnoreCase)
                : new(StringComparer.OrdinalIgnoreCase);
        return new MediaPolicyTable
        {
            Thumb = Copy(MediaFetch.MediaContext.Thumb),
            Cover = Copy(MediaFetch.MediaContext.Cover),
            GalleryImage = Copy(MediaFetch.MediaContext.GalleryImage),
            Manual = Copy(MediaFetch.MediaContext.Manual),
            Music = Copy(MediaFetch.MediaContext.Music),
            Video = Copy(MediaFetch.MediaContext.Video),
            OtherNonMedia = Copy(MediaFetch.MediaContext.OtherNonMedia),
        };
    }
}

/// <summary>Holder of the two refreshable policy slots (web fetch paths / helper URL lister).</summary>
internal static class MediaPolicyStore
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(5);

    // Dedicated client so this subsystem's failure modes stay isolated from MediaFetch's.
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(3),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    })
    { Timeout = FetchTimeout };

    private sealed class Slot
    {
        public volatile MediaPolicyTable Current = MediaPolicyTable.BuiltIn;
        public long LastRefreshUtcTicks;
        public int Refreshing;
        public readonly string Label, ConfigKey, DefaultUrl;
        public Slot(string label, string configKey, string defaultUrl)
        { Label = label; ConfigKey = configKey; DefaultUrl = defaultUrl; }

        public string Url
        {
            get
            {
                try { return LiteBoxConfig.LoadForExe().GetSec("Base", ConfigKey, DefaultUrl) ?? ""; }
                catch { return DefaultUrl; }
            }
        }
    }

    private static readonly Slot _web = new("web", "MediaPolicyUrlWeb", "http://extenddb.com/media-web.json");
    private static readonly Slot _helper = new("helper", "MediaPolicyUrlHelper", "http://extenddb.com/media-helper.json");

    /// <summary>Policy for the FETCH paths (web media proxy, thumbs, desktop cards). Non-blocking
    /// use-driven refresh when stale.</summary>
    public static MediaPolicyTable GetForWeb() { MaybeRefresh(_web); return _web.Current; }

    /// <summary>Policy for ListUrls (editor download grids). Non-blocking refresh when stale.</summary>
    public static MediaPolicyTable GetForHelper() { MaybeRefresh(_helper); return _helper.Current; }

    /// <summary>Warm both slots in the background (web-server Start) so the first user-facing
    /// request usually sees a fresh policy. No-op when the URLs are blank.</summary>
    public static void Bootstrap() { MaybeRefresh(_web); MaybeRefresh(_helper); }

    private static void MaybeRefresh(Slot s)
    {
        if (string.IsNullOrWhiteSpace(s.Url)) return;   // blank URL = pinned to built-in

        var last = new DateTime(Interlocked.Read(ref s.LastRefreshUtcTicks), DateTimeKind.Utc);
        if (DateTime.UtcNow - last <= StaleAfter) return;
        if (Interlocked.CompareExchange(ref s.Refreshing, 1, 0) != 0) return;

        // Reserve the slot up front: success or failure, no retry before StaleAfter elapses.
        Interlocked.Exchange(ref s.LastRefreshUtcTicks, DateTime.UtcNow.Ticks);
        _ = Task.Run(() => Fetch(s));
    }

    private static void Fetch(Slot s)
    {
        try
        {
            using var cts = new CancellationTokenSource(FetchTimeout);
            using var resp = Http.GetAsync(s.Url, cts.Token).GetAwaiter().GetResult();
            if (!resp.IsSuccessStatusCode)
            {
                Console.WriteLine($"[mediapolicy] {s.Label} fetch → {(int)resp.StatusCode}, keeping current");
                return;
            }
            var body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();

            MediaPolicyTable parsed;
            try { parsed = MediaPolicyTable.FromJson(body); }
            catch (Exception ex)
            {
                Console.WriteLine($"[mediapolicy] {s.Label} rejected: {ex.Message}; keeping current");
                return;
            }

            s.Current = parsed;   // atomic whole-instance swap
            Console.WriteLine($"[mediapolicy] {s.Label} policy refreshed from {s.Url}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[mediapolicy] {s.Label} fetch failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally { s.Refreshing = 0; }
    }
}
