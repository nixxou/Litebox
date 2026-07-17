// Fills the web "Related Games" routes (/bigbox/data/games/<id>/related.json and its companion
// related/overviews.json) by driving the native LiteBox suggester engine (Host/Similar/GameSuggester).
//
// This closes the S5 gap: OwnedDataProvider.Related used to serve an empty { recommended, similar, ports }
// payload because the engine wasn't ported. It now is — this provider is the thin shaping layer between the
// engine's ranked CandidateGame results and the exact JSON card shape the BigBox/LaunchBox theme JS reads.
//
// Self-contained: it only uses the public GameSuggester API + PluginHelper + WebParentalState + ThemeFormat,
// so the coordinator wires it in with a one-line delegate from the shared handler(s). No plugin/reflection.
//
// Card shape (must stay byte-identical to what the theme's related-card renderer expects):
//   { id, dbid, local, t, plat, y, d, pct, thumb }
//     id    : IGame GUID (local) or DatabaseID string (DB-only) — JS branches on `local`.
//     dbid  : cloud DatabaseID (0 when a local game has none) — keys the async overview fill.
//     local : in the user's library → navigate to detail; DB-only → cloud modal.
//     t/plat/y : title / platform / year string.
//     d     : description, filled async by the overviews endpoint (empty here).
//     pct   : match score as 0..100 % of the config's max weight.
//     thumb : /api/media/{dbid}.jpg when a cloud id exists (extenddb thumb → GameImages cover fallback), else "".

using System;
using System.Collections.Generic;
using System.IO;
using LbApiHost.Host.Media;
using LbApiHost.Host.Similar;
using Microsoft.Data.Sqlite;
using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Web;

internal static class RelatedProvider
{
    /// <summary>The related.json payload for a game: three ranked lists (recommended / similar / ports).
    /// Empty payload when the game is unknown, parental-blocked, or the engine finds nothing.</summary>
    public static object Related(string id, WebParentalState st, int limit)
    {
        if (limit < 1) limit = 1;
        if (limit > 200) limit = 200;

        IGame game;
        try { game = PluginHelper.DataManager.GetGameById(id); } catch { game = null; }
        if (game == null) return Empty();

        // Never surface a related list for a game the user isn't allowed to see.
        if (st != null && st.IsLocked)
        {
            var rating = Safe(() => game.Rating);
            var platform = Safe(() => game.Platform);
            if (!st.IsRatingAllowed(rating)) return Empty();
            if (!string.IsNullOrEmpty(platform) && st.IsHidden(platform)) return Empty();
        }

        // Parental candidate filter — mirror the rules above onto each CandidateGame.
        Func<CandidateGame, bool> filter = c =>
        {
            if (st == null || !st.IsLocked) return true;
            if (!st.IsRatingAllowed(c.Rating)) return false;
            if (!string.IsNullOrEmpty(c.Platform) && st.IsHidden(c.Platform)) return false;
            return true;
        };

        var runs = GameSuggester.RunAll(game, limit, filter);

        return new
        {
            recommended = ToCards(Find(runs, SuggesterCategory.RecommendedGames)),
            similar     = ToCards(Find(runs, SuggesterCategory.SimilarGames)),
            ports       = ToCards(Find(runs, SuggesterCategory.PossiblePorts)),
        };
    }

    /// <summary>Companion overviews.json: description text for the given cloud DatabaseIDs, read straight from
    /// the Extended DB. Adult (AO) overviews are withheld when the web is locked. Keyed by DatabaseID string.</summary>
    public static Dictionary<string, string> Overviews(string idsCsv, WebParentalState st)
    {
        var result = new Dictionary<string, string>();
        if (string.IsNullOrWhiteSpace(idsCsv)) return result;

        var ids = new List<int>();
        foreach (var tok in idsCsv.Split(','))
            if (int.TryParse(tok.Trim(), out int v) && v > 0) { ids.Add(v); if (ids.Count >= 200) break; }
        if (ids.Count == 0) return result;

        string dbPath = MetadataDb.ExtendedDbPath;
        if (string.IsNullOrEmpty(dbPath) || !File.Exists(dbPath)) return result;
        try { SQLitePCL.Batteries.Init(); } catch { }

        bool locked = st != null && st.IsLocked;
        string inList = string.Join(",", ids);

        try
        {
            var csb = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false, Cache = SqliteCacheMode.Private,
            };
            using var con = new SqliteConnection(csb.ToString());
            con.Open();
            using var cmd = con.CreateCommand();
            // The priority-resolved description: the precomputed defaultOverview column when valid,
            // else the dynamic COALESCE over the source priority (Host/Data/OverviewCache).
            var ovExpr = Data.OverviewCache.ReadExpression(dbPath);
            cmd.CommandText =
                $"SELECT DatabaseID, {ovExpr} AS Overview, ESRB FROM Games " +
                $"WHERE DatabaseID IN ({inList}) AND {ovExpr} IS NOT NULL AND {ovExpr} != ''";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                int dbId = r.GetInt32(0);
                string ov = r.IsDBNull(1) ? "" : r.GetString(1);
                string esrb = r.IsDBNull(2) ? "" : r.GetString(2);
                if (locked && esrb.StartsWith("AO", StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrEmpty(ov)) result[dbId.ToString()] = ov;
            }
        }
        catch { /* older schema / unreadable → whatever we got */ }
        return result;
    }

    // ── shaping ──

    private static object[] ToCards(CategorySuggestions run)
    {
        if (run?.Top == null || run.Top.Count == 0) return Array.Empty<object>();
        var arr = new object[run.Top.Count];
        for (int i = 0; i < run.Top.Count; i++) arr[i] = Card(run.Top[i]);
        return arr;
    }

    private static object Card(SuggestionEntry e)
    {
        var c = e.Cand;
        string id = c.IsLocal ? (c.Id ?? "") : (c.LbDbId > 0 ? c.LbDbId.ToString() : (c.Id ?? ""));

        // Owned game → the local disk-cache thumb proxy (same pipeline as the grid cards); the numeric
        // id endpoint is the fallback for GameCache misses and the only path for DB-only games —
        // plugin-parity (OwnedDataProvider.BuildRelItem).
        string thumb = "";
        if (c.IsLocal && !string.IsNullOrEmpty(c.Id))
        {
            IGame ig = null;
            try { ig = PluginHelper.DataManager.GetGameById(c.Id); } catch { }
            if (ig != null) thumb = OwnedDataProvider.RelatedLocalThumb(ig) ?? "";
        }
        if (thumb.Length == 0 && c.LbDbId > 0) thumb = "/api/media/" + c.LbDbId + ".jpg";

        string year = (c.Year.HasValue && c.Year.Value > 0) ? c.Year.Value.ToString() : "";
        return new
        {
            id,
            dbid  = c.LbDbId,
            local = c.IsLocal,
            t     = c.Title ?? "",
            plat  = c.Platform ?? "",
            y     = year,
            d     = "",
            pct   = e.Pct,
            thumb,
        };
    }

    private static CategorySuggestions Find(List<CategorySuggestions> runs, SuggesterCategory cat)
    {
        if (runs != null) foreach (var r in runs) if (r.Category == cat) return r;
        return null;
    }

    private static object Empty() => new
    {
        recommended = Array.Empty<object>(),
        similar = Array.Empty<object>(),
        ports = Array.Empty<object>(),
    };

    private static T Safe<T>(Func<T> f) { try { return f(); } catch { return default; } }
}
