// --selftest-filter-match : la sémantique de FilterCriteria.Matches sur les dimensions ajoutées.
//
// Ce qui mérite un test ici n'est pas « le champ est lu » mais les DEUX règles qu'on ne voit pas en
// lisant le code : OU à l'intérieur d'une dimension / ET entre dimensions, et le découpage en jetons
// des champs multi-valués (« Europe; France ») — sans lui, filtrer sur France raterait tous les jeux
// dont la région est écrite en liste. Le piège inverse compte autant : « Europe » ne doit PAS répondre
// pour un jeu marqué « Eastern Europe ».

using System;
using LbApiHost.Generated;
using LbApiHost.Host.Search;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Tools;

internal static class FilterMatchSelfTest
{
    /// <summary>Un jeu minimal : seuls les champs que le filtre lit sont peuplés.</summary>
    private sealed class FakeGame : DummyGame
    {
        public string P = "", R = "", M = "", St = "", Pr = "", Es = "";
        public int? Mp;
        public override string Platform { get => P; set { } }
        public override string Region { get => R; set { } }
        public override string PlayMode { get => M; set { } }
        public override string Status { get => St; set { } }
        public override string Progress { get => Pr; set { } }
        public override string Rating { get => Es; set { } }
        public override int? MaxPlayers { get => Mp; set { } }
    }

    public static int Run()
    {
        int fail = 0;

        // ── OU dans une dimension ──
        var f = new FilterCriteria { Platforms = { "Arcade", "FBNeo" } };
        fail += Check("OR inside a dimension: first value matches", true, f.Matches(new FakeGame { P = "Arcade" }));
        fail += Check("OR inside a dimension: second value matches", true, f.Matches(new FakeGame { P = "FBNeo" }));
        fail += Check("OR inside a dimension: anything else is out", false, f.Matches(new FakeGame { P = "Sony Playstation" }));

        // ── ET entre dimensions ──
        var both = new FilterCriteria { Platforms = { "Arcade" }, Regions = { "Japan" } };
        fail += Check("AND across dimensions: both match", true, both.Matches(new FakeGame { P = "Arcade", R = "Japan" }));
        fail += Check("AND across dimensions: one dimension failing rejects", false, both.Matches(new FakeGame { P = "Arcade", R = "Europe" }));

        // ── Champs multi-valués ──
        var reg = new FilterCriteria { Regions = { "France" } };
        fail += Check("a ';'-separated region list matches on one token", true, reg.Matches(new FakeGame { R = "Europe; France" }));
        fail += Check("a ','-separated region list matches too", true, reg.Matches(new FakeGame { R = "Europe,France" }));
        var eur = new FilterCriteria { Regions = { "Europe" } };
        fail += Check("a token must match WHOLE (not 'Eastern Europe')", false, eur.Matches(new FakeGame { R = "Eastern Europe" }));
        var mode = new FilterCriteria { PlayModes = { "Cooperative" } };
        fail += Check("play mode is tokenised as well", true, mode.Matches(new FakeGame { M = "Single Player; Cooperative" }));

        // ── Champs simples ──
        fail += Check("status is an exact (case-insensitive) match", true,
                      new FilterCriteria { Statuses = { "playable" } }.Matches(new FakeGame { St = "Playable" }));
        fail += Check("ESRB is its own dimension", false,
                      new FilterCriteria { Esrb = { "E" } }.Matches(new FakeGame { Es = "M" }));
        fail += Check("max players is exact", true, new FilterCriteria { MaxPlayers = 2 }.Matches(new FakeGame { Mp = 2 }));
        fail += Check("max players: a different count is out", false, new FilterCriteria { MaxPlayers = 2 }.Matches(new FakeGame { Mp = 4 }));
        fail += Check("max players: unknown count is out when the bound is set", false,
                      new FilterCriteria { MaxPlayers = 2 }.Matches(new FakeGame { Mp = null }));

        // ── Une dimension vide ne contraint rien ──
        fail += Check("an empty criteria matches everything", true, new FilterCriteria().Matches(new FakeGame()));
        fail += Check("an empty dimension does not reject a game with no value", true,
                      new FilterCriteria { Platforms = { "Arcade" } }.Matches(new FakeGame { P = "Arcade", R = "" }));

        // ── IsActive suit les nouvelles dimensions ──
        fail += Check("IsActive sees a facet selection", true, new FilterCriteria { Regions = { "Japan" } }.IsActive);
        fail += Check("IsActive sees the high-score flag", true, new FilterCriteria { HighScores = true }.IsActive);
        fail += Check("IsActive stays false on a fresh criteria", false, new FilterCriteria().IsActive);

        // ── Clone : une dimension ajoutée doit voyager (sinon Apply perd la sélection) ──
        var src = new FilterCriteria { Regions = { "Japan" }, Controllers = { "Generic Controller" }, MaxPlayers = 4, Saves = "state" };
        var cl = src.Clone();
        fail += Check("Clone carries the facets", true, cl.Regions.Count == 1 && cl.Controllers.Count == 1);
        fail += Check("Clone carries the scalars", true, cl.MaxPlayers == 4 && cl.Saves == "state");
        cl.Regions.Add("Europe");
        fail += Check("Clone is deep (lists are not shared)", 1, src.Regions.Count);

        Console.WriteLine(fail == 0 ? "[filter-match] ALL PASS" : $"[filter-match] {fail} FAILURE(S)");
        return fail;
    }

    private static int Check(string what, bool expected, bool got)
    {
        if (expected == got) { Console.WriteLine("[filter-match] PASS " + what); return 0; }
        Console.WriteLine($"[filter-match] FAIL {what} — expected {expected}, got {got}");
        return 1;
    }

    private static int Check(string what, int expected, int got)
    {
        if (expected == got) { Console.WriteLine("[filter-match] PASS " + what); return 0; }
        Console.WriteLine($"[filter-match] FAIL {what} — expected {expected}, got {got}");
        return 1;
    }
}
