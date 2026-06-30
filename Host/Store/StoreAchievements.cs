// Store-achievements data model + badge image cache, shared by the per-store providers
// (GogAchievements today; a Steamworks-helper Steam provider later). Mirrors the RA layer
// (RaService/RaGameCache/RaBadges) but store-agnostic: no medians, no hardcore split, and
// the badge art comes from arbitrary URLs (GOG serves absolute image URLs) rather than RA's
// fixed Badge/<name>.png convention.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace LbApiHost.Host.Store;

/// <summary>Normalised, cached achievements + this user's unlock state for one store game
/// (Core\store-ach-cache\&lt;store&gt;-&lt;appId&gt;.json).</summary>
internal sealed class StoreAchCache
{
    public string? store { get; set; }        // "GOG" (later "Steam")
    public string? appId { get; set; }         // GogAppId / Steam appid
    public string? title { get; set; }
    public int total { get; set; }
    public int unlocked { get; set; }
    public int ver { get; set; }               // cache-shape version — old files are refetched
    public string? fetchedAt { get; set; }     // ISO-8601 UTC
    public string? sourceSig { get; set; }     // galaxy-2.0.db signature when fetched (GOG freshness)
    public List<StoreAch> achievements { get; set; } = new();
}

/// <summary>One achievement def + this user's unlock state.</summary>
internal sealed class StoreAch
{
    public string? id { get; set; }            // GOG apikey (stable per achievement)
    public string? title { get; set; }
    public string? description { get; set; }
    public bool unlocked { get; set; }
    public string? unlockedAt { get; set; }    // ISO-8601 UTC (null when locked/unknown)
    public double rarity { get; set; }         // % of players who unlocked it (0 = unknown)
    public string? badgeUnlocked { get; set; } // image URL (coloured)
    public string? badgeLocked { get; set; }   // image URL (greyed)
    public int order { get; set; }
}

/// <summary>Downloads + disk-caches store badge images under Core\store-ach-badges\, keyed by a
/// hash of the source URL (the providers hand us absolute URLs). Mirrors RaBadges.</summary>
internal static class StoreBadges
{
    private static readonly HttpClient Http = BuildClient();
    private static HttpClient BuildClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        try { c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)"); } catch { }
        return c;
    }

    private static string Dir
    {
        get { var d = Path.Combine(AppContext.BaseDirectory, "store-ach-badges"); try { Directory.CreateDirectory(d); } catch { } return d; }
    }

    /// <summary>Local path to the cached badge for <paramref name="url"/>, downloading it once if absent.
    /// Null when the URL is empty or the download fails. BLOCKING — call off the UI thread.</summary>
    public static string? Get(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        string path = Path.Combine(Dir, Hash(url!) + ".img");
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length > 0) return path;
            var bytes = Http.GetByteArrayAsync(url).GetAwaiter().GetResult();
            if (bytes == null || bytes.Length == 0) return null;
            File.WriteAllBytes(path, bytes);
            return path;
        }
        catch (Exception ex) { Console.WriteLine($"[storeach] badge fetch failed: {ex.Message}"); return null; }
    }

    private static string Hash(string s)
    {
        using var md5 = MD5.Create();
        var h = md5.ComputeHash(Encoding.UTF8.GetBytes(s));
        var sb = new StringBuilder(h.Length * 2);
        foreach (var b in h) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }
}
