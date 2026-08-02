// MAME community leaderboards — native LiteBox READ client for the LaunchBox Games Database boards.
//
// RE'd (see memory reference-lb-mame-leaderboards): each board is a plain form-urlencoded POST with a single
// `romFile=<rom>` field and NO auth — public data. Response is a JSON array of {Name, Score, Date}, where Name
// already carries the rank prefix and player initials ("1.  GarbonZoni - BAB"). We only READ here (no upload,
// no account, no obfuscated-core dependency) — the 4 period endpoints, parsed into entry lists, short-cached
// per rom so re-opening the tab doesn't re-hit the network.
//
// Gating: the feature is offered only for a game whose EFFECTIVE emulator (its own, or its platform default, or
// any additional-app's) is MAME. rom = the ApplicationPath's file name without extension (the MAME short name,
// exactly what LB sends as romFile — e.g. "1943mii").

#nullable enable

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Mame;

/// <summary>One leaderboard row as returned by the LBGDB (Name keeps its "&lt;rank&gt;.  &lt;pseudo&gt; - &lt;ini&gt;" form).</summary>
internal sealed class MameLbEntry
{
    public string Name = "";
    public long Score;
    public string Date = "";
}

/// <summary>The four period boards for one rom.</summary>
internal sealed class MameLbBoards
{
    public List<MameLbEntry> AllTime = new();
    public List<MameLbEntry> Yearly = new();
    public List<MameLbEntry> Monthly = new();
    public List<MameLbEntry> Weekly = new();
    public bool Any => AllTime.Count + Yearly.Count + Monthly.Count + Weekly.Count > 0;
}

internal static class MameLeaderboards
{
    private const string Base = "https://gamesdb.launchbox-app.com/launchbox/";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    // ── MAME gating + rom-name ──────────────────────────────────────────

    public static bool IsMameEmulator(IEmulator? e)
    {
        if (e == null) return false;
        string t = Safe(() => e.Title) ?? "";
        string p = Safe(() => e.ApplicationPath) ?? "";
        return t.IndexOf("MAME", StringComparison.OrdinalIgnoreCase) >= 0
            || System.IO.Path.GetFileName(p).IndexOf("mame", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>True when the emulator is RetroArch running the FBNeo core — its .hi files feed the SAME
    /// community leaderboards as MAME (keyed by rom name). Core detected from the default or any per-platform
    /// command line (-L …fbneo…).</summary>
    public static bool IsFbneoRetroArch(IEmulator? e)
    {
        if (e == null) return false;
        string title = Safe(() => e.Title) ?? "";
        string appPath = Safe(() => e.ApplicationPath) ?? "";
        bool retroarch = title.IndexOf("RetroArch", StringComparison.OrdinalIgnoreCase) >= 0
                      || System.IO.Path.GetFileName(appPath).IndexOf("retroarch", StringComparison.OrdinalIgnoreCase) >= 0;
        if (!retroarch) return false;
        string all = Safe(() => e.CommandLine) ?? "";
        foreach (var ep in Safe(() => e.GetAllEmulatorPlatforms()) ?? Array.Empty<IEmulatorPlatform>())
            all += " " + (Safe(() => ep.CommandLine) ?? "");
        return all.IndexOf("fbneo", StringComparison.OrdinalIgnoreCase) >= 0
            || all.IndexOf("fbalpha", StringComparison.OrdinalIgnoreCase) >= 0
            || all.IndexOf("fb_neo", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>Any emulator whose scores feed the community leaderboards (MAME or FBNeo-under-RetroArch).</summary>
    public static bool IsLeaderboardEmulator(IEmulator? e) => IsMameEmulator(e) || IsFbneoRetroArch(e);

    private static IEmulator? EmuById(string? id)
        => string.IsNullOrEmpty(id) ? null : Safe(() => PluginHelper.DataManager?.GetEmulatorById(id));

    /// <summary>Platform-default emulator for a platform name (the one games with no explicit EmulatorId use).</summary>
    private static IEmulator? PlatformDefaultEmu(string? platform)
    {
        if (string.IsNullOrWhiteSpace(platform)) return null;
        foreach (var e in Safe(() => PluginHelper.DataManager?.GetAllEmulators()) ?? Array.Empty<IEmulator>())
            foreach (var ep in Safe(() => e.GetAllEmulatorPlatforms()) ?? Array.Empty<IEmulatorPlatform>())
                if (Safe(() => ep.IsDefault) && string.Equals(Safe(() => ep.Platform), platform, StringComparison.OrdinalIgnoreCase))
                    return e;
        return null;
    }

    /// <summary>True when the game launches (primary OR via an additional app) with a MAME emulator.</summary>
    public static bool IsMameGame(IGame? game)
    {
        if (game == null) return false;
        try
        {
            // primary: explicit EmulatorId, else the platform default
            var main = EmuById(Safe(() => game.EmulatorId)) ?? PlatformDefaultEmu(Safe(() => game.Platform));
            if (IsLeaderboardEmulator(main)) return true;
            // secondary (a): any additional application with its own MAME/FBNeo emulator
            foreach (var app in Safe(() => game.GetAllAdditionalApplications()) ?? Array.Empty<IAdditionalApplication>())
                if (IsLeaderboardEmulator(EmuById(Safe(() => app.EmulatorId)))) return true;
            // secondary (b): a MAME/FBNeo emulator merely ASSOCIATED with the game's platform (not the default).
            // The leaderboard is keyed by the rom name, which is the same whoever launches it — so a game that
            // COULD run on MAME/FBNeo (Arcade with such an emulator for its platform) qualifies.
            string plat = Safe(() => game.Platform) ?? "";
            if (plat.Length > 0)
                foreach (var e in Safe(() => PluginHelper.DataManager?.GetAllEmulators()) ?? Array.Empty<IEmulator>())
                {
                    if (!IsLeaderboardEmulator(e)) continue;
                    foreach (var ep in Safe(() => e.GetAllEmulatorPlatforms()) ?? Array.Empty<IEmulatorPlatform>())
                        if (string.Equals(Safe(() => ep.Platform), plat, StringComparison.OrdinalIgnoreCase)) return true;
                }
        }
        catch { }
        return false;
    }

    /// <summary>Ce jeu peut-il RÉELLEMENT avoir un high score ? Deux conditions, et la seconde est la vraie :
    ///   • il tourne sur l'intégration MAME/FBNeo — c'est ce qui fait de lui un jeu d'arcade ici, et cela
    ///     couvre autant la plateforme Arcade que FBNeo, dont le dat est déployé exprès pour elle ;
    ///   • le nom de sa rom est déclaré par un hiscore.dat INSTALLÉ. Sans cette ligne, l'émulateur n'écrira
    ///     jamais de .hi pour ce jeu : il n'y a pas de score à afficher, ni à soumettre.
    /// Aucun dat installé ⇒ faux pour tout le monde, ce qui est la vérité et non une dégradation.</summary>
    public static bool HasHiscoreSupport(IGame? game)
        => IsMameGame(game) && HiscoreDat.Supports(RomName(game));

    /// <summary>The MAME short rom name LB sends as romFile — the ApplicationPath file name without extension.</summary>
    public static string? RomName(IGame? game)
    {
        var p = Safe(() => game?.ApplicationPath);
        if (string.IsNullOrWhiteSpace(p)) return null;
        var n = System.IO.Path.GetFileNameWithoutExtension(p);
        return string.IsNullOrWhiteSpace(n) ? null : n.Trim();
    }

    // ── Fetch (short-cached per rom) ────────────────────────────────────

    private static readonly Dictionary<string, (DateTime at, MameLbBoards boards)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);
    private static readonly object _gate = new();

    /// <summary>Fetch all four boards for a rom. Cached for a few minutes so re-opening the tab is free.
    /// Never throws — a failed/empty board comes back as an empty list.</summary>
    public static async Task<MameLbBoards> FetchAsync(string rom)
    {
        lock (_gate)
            if (_cache.TryGetValue(rom, out var c) && (DateTime.UtcNow - c.at) < Ttl)
                return c.boards;

        var boards = new MameLbBoards
        {
            AllTime = await OneAsync("getmamegameleaderboard", rom).ConfigureAwait(false),
            Yearly  = await OneAsync("getmamegameleaderboardyearly", rom).ConfigureAwait(false),
            Monthly = await OneAsync("getmamegameleaderboardmonthly", rom).ConfigureAwait(false),
            Weekly  = await OneAsync("getmamegameleaderboardweekly", rom).ConfigureAwait(false),
        };

        lock (_gate) _cache[rom] = (DateTime.UtcNow, boards);
        return boards;
    }

    /// <summary>Drop a rom's cached boards so the next Fetch re-hits the network — call after we submit a score,
    /// so the HIGH SCORES tab reflects the new standing instead of the stale pre-upload list.</summary>
    public static void Invalidate(string? rom)
    {
        if (string.IsNullOrWhiteSpace(rom)) return;
        lock (_gate) _cache.Remove(rom.Trim());
    }

    /// <summary>Free the leaderboard cache at game launch, keeping only the game being launched (its board is
    /// the one the user returns to). Everything else browsed earlier is dropped to reclaim RAM during play.</summary>
    public static void ClearAllExcept(string? keepRom)
    {
        string keep = keepRom?.Trim() ?? "";
        lock (_gate)
        {
            var toRemove = new List<string>();
            foreach (var k in _cache.Keys)
                if (!string.Equals(k, keep, StringComparison.OrdinalIgnoreCase)) toRemove.Add(k);
            foreach (var k in toRemove) _cache.Remove(k);
        }
    }

    private static async Task<List<MameLbEntry>> OneAsync(string endpoint, string rom)
    {
        var outList = new List<MameLbEntry>();
        try
        {
            using var body = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("romFile", rom) });
            using var resp = await Http.PostAsync(Base + endpoint, body).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return outList;
            string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return outList;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var e = new MameLbEntry
                {
                    Name = el.TryGetProperty("Name", out var n) ? (n.GetString() ?? "") : "",
                    Date = el.TryGetProperty("Date", out var d) ? (d.GetString() ?? "") : "",
                };
                if (el.TryGetProperty("Score", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt64(out var v)) e.Score = v;
                outList.Add(e);
            }
        }
        catch { /* network/parse failure → empty board */ }
        return outList;
    }

    private static T? Safe<T>(Func<T> f) { try { return f(); } catch { return default; } }
}
