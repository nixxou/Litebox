// DTOs for the database site — the shapes DbRepository reads out of the Extended DB and the page / API
// handlers serialise. Clean-room LiteBox rewrite of ExtendDB's Web/Backend/Models.cs (data shapes only; no
// plugin types). Lives in namespace LbApiHost.Host.Web so the handlers map cleanly.

using System;
using System.Collections.Generic;

namespace LbApiHost.Host.Web;

/// <summary>One game row (full detail) from the Extended DB's Games table.</summary>
internal sealed class DbGame
{
    public int DatabaseID { get; set; }
    public string Name { get; set; } = "";
    public string ReleaseDate { get; set; }
    public int? ReleaseYear { get; set; }
    public string Overview { get; set; }
    public int? MaxPlayers { get; set; }
    public string ReleaseType { get; set; }
    public bool Cooperative { get; set; }
    public string VideoURL { get; set; }
    public double? CommunityRating { get; set; }
    public int CommunityRatingCount { get; set; }
    public string WikipediaURL { get; set; }
    public string Platform { get; set; } = "";
    public string ESRB { get; set; }
    public string Genres { get; set; }
    public string Developer { get; set; }
    public string Publisher { get; set; }

    public int? SteamId { get; set; }
    public int? SteamAppId { get; set; }
    public int? VNDBID { get; set; }
    public int? ScreenscraperId { get; set; }
    public string IgdbSlug { get; set; }
    public string Origin { get; set; } = "launchbox";

    public string OverviewAiFr { get; set; }
    public string OverviewAiEn { get; set; }
    public string OverviewAiDe { get; set; }
    public string OverviewAiEs { get; set; }
    public string OverviewAiIt { get; set; }
    public string OverviewAiPt { get; set; }

    public string OverviewSteamFr { get; set; }
    public string OverviewScFr { get; set; }
    public string OverviewSteamEn { get; set; }
    public string OverviewScEn { get; set; }

    /// <summary>Best single overview across the AI / store / classic columns (first non-blank), or "".</summary>
    public string PickOverview()
    {
        foreach (var v in new[] { OverviewAiFr, OverviewAiEn, OverviewSteamFr,
                                   OverviewSteamEn, OverviewScFr, OverviewScEn, Overview })
            if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
        return "";
    }

    public bool IsAdult => ESRB is not null
        && ESRB.StartsWith("AO", StringComparison.OrdinalIgnoreCase);

    /// <summary>The per-language AI overviews present on this row, keyed by 2-letter code.</summary>
    public Dictionary<string, string> GetAiOverviews()
    {
        var d = new Dictionary<string, string>(6);
        if (!string.IsNullOrWhiteSpace(OverviewAiFr)) d["fr"] = OverviewAiFr.Trim();
        if (!string.IsNullOrWhiteSpace(OverviewAiEn)) d["en"] = OverviewAiEn.Trim();
        if (!string.IsNullOrWhiteSpace(OverviewAiDe)) d["de"] = OverviewAiDe.Trim();
        if (!string.IsNullOrWhiteSpace(OverviewAiEs)) d["es"] = OverviewAiEs.Trim();
        if (!string.IsNullOrWhiteSpace(OverviewAiIt)) d["it"] = OverviewAiIt.Trim();
        if (!string.IsNullOrWhiteSpace(OverviewAiPt)) d["pt"] = OverviewAiPt.Trim();
        return d;
    }
}

/// <summary>One platform row (+ its game count) from the Extended DB's Platforms table.</summary>
internal sealed class DbPlatform
{
    public int PlatformKey { get; set; }
    public string Name { get; set; } = "";
    public bool Emulated { get; set; }
    public string ReleaseDate { get; set; }
    public string Developer { get; set; }
    public string Manufacturer { get; set; }
    public string Cpu { get; set; }
    public string Memory { get; set; }
    public string Graphics { get; set; }
    public string Sound { get; set; }
    public string Display { get; set; }
    public string Media { get; set; }
    public string MaxControllers { get; set; }
    public string Notes { get; set; }
    public string Category { get; set; }
    public int GameCount { get; set; }
}

/// <summary>One image row from the Extended DB's GameImages table.</summary>
internal sealed class DbGameImage
{
    public string FileName { get; set; } = "";
    public int DatabaseId { get; set; }
    public string Type { get; set; } = "";
    public string Region { get; set; }
    public long CRC32 { get; set; }
    public string Origin { get; set; } = "launchbox";
    public int Sex { get; set; }
    public long FileSize { get; set; }
    public bool NeedsBlur =>
        Origin.Equals("steam", StringComparison.OrdinalIgnoreCase)
        || (Origin.Equals("vndb", StringComparison.OrdinalIgnoreCase) && Sex == 1);
}

/// <summary>One ROM row from the Extended DB's GameRoms side-table.</summary>
internal sealed class DbGameRom
{
    public string FileName { get; set; } = "";
    public long FileSize { get; set; }
    public long CRC32 { get; set; }
    public string Origin { get; set; } = "launchbox";
    public string Crc32Hex => ((uint)CRC32).ToString("X8");
    public string FileSizeHuman => FileSize switch
    {
        >= 1_073_741_824 => $"{FileSize / 1_073_741_824.0:F1} GB",
        >= 1_048_576 => $"{FileSize / 1_048_576.0:F1} MB",
        >= 1_024 => $"{FileSize / 1_024.0:F1} KB",
        _ => $"{FileSize} B"
    };
}

/// <summary>One alternate-title row from GameAlternateTitles.</summary>
internal sealed class DbAlternateTitle
{
    public string AlternateName { get; set; } = "";
    public int DatabaseID { get; set; }
    public string Region { get; set; } = "";
}

/// <summary>A paginated-list game row (trimmed projection used by the grid).</summary>
internal sealed class DbGameSummary
{
    public int DatabaseID { get; set; }
    public string Name { get; set; } = "";
    public string Platform { get; set; } = "";
    public int? ReleaseYear { get; set; }
    public string ReleaseDate { get; set; }
    public string Genres { get; set; }
    public string ESRB { get; set; }
    public string CompareName { get; set; }
    public double? CommunityRating { get; set; }
    public int CommunityRatingCount { get; set; }
    public int? MaxPlayers { get; set; }
    public bool Cooperative { get; set; }
    public string Origin { get; set; }
    public string ReleaseType { get; set; }
    public string Developer { get; set; }
    public string Publisher { get; set; }
    public string CoverFileName { get; set; }
    public long CoverCrc32 { get; set; }
    public bool CoverNeedsBlur { get; set; }

    public bool IsAdult => ESRB is not null
        && ESRB.StartsWith("AO", StringComparison.OrdinalIgnoreCase);

    /// <summary>A plausible display year, falling back from ReleaseYear to the ReleaseDate prefix.</summary>
    public int? EffectiveYear
    {
        get
        {
            if (ReleaseYear.HasValue && ReleaseYear.Value > 1950 && ReleaseYear.Value < 2050)
                return ReleaseYear;
            if (!string.IsNullOrEmpty(ReleaseDate) && ReleaseDate.Length >= 4
                && int.TryParse(ReleaseDate.Substring(0, 4), out var y) && y > 1950 && y < 2050)
                return y;
            return null;
        }
    }
}

/// <summary>Stable URL-friendly slug from a platform name (ASCII-only so the router captures it undecoded).</summary>
internal static class PlatformSlug
{
    public static string For(string name)
    {
        if (string.IsNullOrEmpty(name)) return "platform";
        var sb = new System.Text.StringBuilder(name.Length);
        bool lastDash = false;
        foreach (var ch in name)
        {
            bool ascii = (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9');
            if (ascii)
            {
                sb.Append(char.ToLowerInvariant(ch));
                lastDash = false;
            }
            else if (!lastDash && sb.Length > 0)
            {
                sb.Append('-');
                lastDash = true;
            }
        }
        var s = sb.ToString().TrimEnd('-');
        return string.IsNullOrEmpty(s) ? "platform" : s;
    }
}
