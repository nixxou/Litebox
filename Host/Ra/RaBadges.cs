#nullable enable

using System;
using System.IO;
using System.Net.Http;

namespace LbApiHost.Host.Ra;

/// <summary>Downloads + disk-caches RA achievement badge PNGs under Core\ra-badges\. RA serves a coloured
/// badge at .../Badge/&lt;name&gt;.png and a greyed "locked" variant at .../Badge/&lt;name&gt;_lock.png — the
/// card asks for whichever matches the unlock state; both are cached by their own filename.</summary>
internal static class RaBadges
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private static string Dir
    {
        get { var d = Path.Combine(AppContext.BaseDirectory, "ra-badges"); try { Directory.CreateDirectory(d); } catch { } return d; }
    }

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
}
