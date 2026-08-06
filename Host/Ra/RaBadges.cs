#nullable enable

using System;
using System.IO;
using System.Net.Http;

namespace LbApiHost.Host.Ra;

/// <summary>Downloads + disk-caches RA imagery under Core\ra-badges\: achievement badges (a coloured one
/// at .../Badge/&lt;name&gt;.png and a greyed "locked" variant at .../Badge/&lt;name&gt;_lock.png — the card
/// asks for whichever matches the unlock state) and game box art (the ImageIcon path the API hands back).
/// Everything is cached by its own filename.</summary>
internal static class RaBadges
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private static string Dir => LiteBoxPaths.CacheDir("ra-badges");

    /// <summary>Local path to the badge image (coloured when unlocked, greyed _lock when not), downloading
    /// it once if absent. Null when badge is empty or the download fails. BLOCKING — call off the UI thread.</summary>
    public static string? Get(string? badge, bool unlocked)
    {
        if (string.IsNullOrWhiteSpace(badge)) return null;
        string name = unlocked ? badge! : badge + "_lock";
        string path = Path.Combine(Dir, name + ".png");
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length > 0) return path;
            var bytes = Http.GetByteArrayAsync("https://media.retroachievements.org/Badge/" + name + ".png")
                            .GetAwaiter().GetResult();
            if (bytes == null || bytes.Length == 0) return null;
            File.WriteAllBytes(path, bytes);
            return path;
        }
        catch (Exception ex) { Console.WriteLine($"[ra] badge {name} failed: {ex.Message}"); return null; }
    }

    /// <summary>Local path to a GAME's art from the RA-relative path the API returns ("/Images/106799.png"),
    /// downloading it once if absent. This is the stand-in thumb for a game the library doesn't have — an
    /// owned game draws its own art instead. Null when the path is empty or the download fails. BLOCKING —
    /// call off the UI thread.</summary>
    public static string? GameArt(string? imageIcon)
    {
        if (string.IsNullOrWhiteSpace(imageIcon)) return null;
        string rel = imageIcon!.Trim().TrimStart('/');
        // "Images/106799.png" → "img-106799.png": one flat cache dir, and the prefix keeps a game's art
        // from ever colliding with a badge whose name happens to be the same number.
        string file = "img-" + Path.GetFileName(rel);
        if (file.Length <= 4) return null;
        string path = Path.Combine(Dir, file);
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length > 0) return path;
            var bytes = Http.GetByteArrayAsync("https://media.retroachievements.org/" + rel)
                            .GetAwaiter().GetResult();
            if (bytes == null || bytes.Length == 0) return null;
            File.WriteAllBytes(path, bytes);
            return path;
        }
        catch (Exception ex) { Console.WriteLine($"[ra] game art {rel} failed: {ex.Message}"); return null; }
    }
}
