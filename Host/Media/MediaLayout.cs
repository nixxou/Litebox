// User-configurable right-detail-pane image layout (Options → Display → Right panel).
//
//   • Immediate image family PER VIEW (list vs poster) — the box shown the instant a game is selected,
//     before the detail-load delay. Defaults to "Front" for both (the previous hard-wired behaviour).
//   • Post-load ordered image LIST — replaces the hard-coded BuildMediaList. Each entry names a FAMILY
//     (regroupement, e.g. "Screenshots") OR an EXACT LaunchBox image type (e.g. "Screenshot - Game Title")
//     and a Count (max images to take from it). The list is ordered = priority; entry[0]'s first image is
//     the main box that the delay upgrades to full-res. Default reproduces the old list.
//
// Selection within an entry is "auto" (LaunchBox's type→region→numeric algorithm) for now; the Mode/Weights
// fields are reserved for a future weighted picker (region / type / numeric / aspect-ratio) — the config
// round-trips them so the UI and storage are ready, but the resolver currently ignores them.
//
// Stored as JSON at Core\litebox\media-layout.json (LiteBox-own, like every other config).

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LbApiHost.Host.Media;

internal sealed class MediaEntry
{
    /// <summary>Family regroupement key (Front, Screenshots, …) or an exact LB image type when <see cref="ExactType"/>.</summary>
    public string Sel { get; set; } = "Front";
    /// <summary>True → <see cref="Sel"/> is an exact LB image type ("Screenshot - Game Title"); false → a family.</summary>
    public bool ExactType { get; set; }
    /// <summary>Max images to take from this entry (1 = a single best image).</summary>
    public int Count { get; set; } = 99;
    /// <summary>"auto" (LB algorithm) or "weighted" (reserved — not yet resolved).</summary>
    public string Mode { get; set; } = "auto";
    /// <summary>Reserved weighted-picker weights (region / type / numeric / aspect). Unused for now.</summary>
    public int WRegion { get; set; } = 1;
    public int WType { get; set; } = 1;
    public int WNumeric { get; set; } = 1;
    public int WAspect { get; set; }

    public MediaEntry Clone() => (MediaEntry)MemberwiseClone();
    public string Label() => (ExactType ? "🎞 " : "") + Sel + (Count < 99 ? $"  ×{Count}" : "");
}

internal sealed class MediaLayout
{
    public string ImmediateList { get; set; } = "Front";     // family shown instantly when selecting in LIST view
    public string ImmediatePoster { get; set; } = "Front";   // …in POSTER view
    public List<MediaEntry> PostLoad { get; set; } = new();

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault };
    private static string Path => LiteBoxPaths.File("media-layout.json");

    /// <summary>The default layout — byte-for-byte the previous hard-coded BuildMediaList behaviour.</summary>
    public static MediaLayout Default() => new()
    {
        ImmediateList = "Front", ImmediatePoster = "Front",
        PostLoad = new()
        {
            new MediaEntry { Sel = "Front", ExactType = false, Count = 1 },
            new MediaEntry { Sel = "Screenshot - Game Title", ExactType = true, Count = 99 },
            new MediaEntry { Sel = "Screenshot - Gameplay", ExactType = true, Count = 99 },
            new MediaEntry { Sel = "Fanart - Background", ExactType = true, Count = 99 },
        },
    };

    private static MediaLayout? _cached;
    public static MediaLayout Current => _cached ??= Load();

    public static MediaLayout Load()
    {
        try
        {
            if (File.Exists(Path))
            {
                var l = JsonSerializer.Deserialize<MediaLayout>(File.ReadAllText(Path));
                if (l != null && l.PostLoad.Count > 0) return l;
            }
        }
        catch { }
        return Default();
    }

    public void Save()
    {
        try { File.WriteAllText(Path, JsonSerializer.Serialize(this, Json)); _cached = this; }
        catch { }
    }

    public MediaLayout Clone() => new()
    {
        ImmediateList = ImmediateList, ImmediatePoster = ImmediatePoster,
        PostLoad = PostLoad.Select(e => e.Clone()).ToList(),
    };

    // ── Catalogs for the config UI ────────────────────────────────────────────
    /// <summary>Family regroupements (Key = stored value, Title = display).</summary>
    public static (string Key, string Title)[] Families => Host.MainWindow.CacheRegroupements;

    /// <summary>Every known exact LB image type (for "specific media" entries).</summary>
    public static string[] ExactTypes()
    {
        try { return MediaResolver.ImageTypeNames().OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray(); }
        catch { return Array.Empty<string>(); }
    }
}
