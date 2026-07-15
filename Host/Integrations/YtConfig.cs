// YouTube / yt-dlp settings, stored as JSON (Core\litebox\youtube.json) — NOT in LiteBox.ini, because the
// default-searches value is a multi-LINE list and an ini is single-line-per-key (it would truncate). Shared by
// the video editor's gear dialog AND the "YT-DLP" tab under Options → LB · Integrations, so a change in either
// sticks. Search lines support the tags {GameName}, {Platform} and {AltName1}, {AltName2}, … (1-based index into
// the game's Alternate Names); a line whose {AltNameN} the game doesn't have is skipped.

#nullable enable

using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace LbApiHost.Host.Integrations;

internal sealed class YtConfig
{
    public List<string> Searches { get; set; } = new() { "{GameName} trailer" };
    public string Quality { get; set; } = "1080p";
    public string Cookies { get; set; } = "None";

    public static readonly string[] QualityPresets = { "Best", "1080p", "720p", "480p" };
    public static string[] CookieNames => System.Enum.GetNames(typeof(YtDlp.CookieBrowser));

    private static string PathOnDisk => LiteBoxPaths.File("youtube.json");

    public static YtConfig Load()
    {
        try
        {
            if (File.Exists(PathOnDisk))
            {
                var c = JsonSerializer.Deserialize<YtConfig>(File.ReadAllText(PathOnDisk));
                if (c != null) { c.Normalize(); return c; }
            }
        }
        catch { }
        return new YtConfig();
    }

    public void Save()
    {
        try { Normalize(); File.WriteAllText(PathOnDisk, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })); }
        catch { }
    }

    private void Normalize()
    {
        Searches ??= new List<string>();
        Searches.RemoveAll(string.IsNullOrWhiteSpace);
        if (Searches.Count == 0) Searches.Add("{GameName} trailer");
        if (string.IsNullOrWhiteSpace(Quality)) Quality = "1080p";
        if (string.IsNullOrWhiteSpace(Cookies)) Cookies = "None";
    }

    public YtDlp.CookieBrowser CookieBrowser
        => System.Enum.TryParse<YtDlp.CookieBrowser>((Cookies ?? "").Trim(), true, out var c) ? c : YtDlp.CookieBrowser.None;
}
