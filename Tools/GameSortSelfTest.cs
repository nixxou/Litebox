using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LbApiHost.Generated;
using LbApiHost.Host;
using LbApiHost.Host.Data;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Tools;

internal static class GameSortSelfTest
{
    public static int Run()
    {
        int failures = 0;
        failures += Check("LaunchBoxId XML alias", GameSortCatalog.Parse("LaunchBoxId") == "launchboxid");
        failures += Check("display label alias", GameSortCatalog.Parse("Release Date Year") == "releaseyear");
        failures += Check("empty is Default", GameSortCatalog.Parse("") == GameSortCatalog.Default);
        failures += Check("Manual", GameSortCatalog.Parse("Manual") == GameSortCatalog.Manual);
        failures += Check("custom field", GameSortCatalog.Parse("custom1", new[] { "abc", "custom1" }) == "custom:custom1");
        failures += Check("round-trip standard values", GameSortCatalog.Standard.All(d =>
            GameSortCatalog.Parse(GameSortCatalog.ToLaunchBoxValue(d.Key)) == d.Key));
        // "ESRB" is a LiteBox LABEL. What reaches <SortBy> must stay LaunchBox's own "Rating",
        // and a file written by LaunchBox must still resolve to the same key.
        failures += Check("ESRB is a label only — XML value stays LaunchBox's",
            GameSortCatalog.Label("rating") == "ESRB"
            && GameSortCatalog.ToLaunchBoxValue("rating") == "Rating"
            && GameSortCatalog.Parse("Rating") == "rating"
            && GameSortCatalog.Parse("ESRB") == "rating");
        failures += Check("no catalog entry writes its label instead of its LaunchBox value",
            GameSortCatalog.Standard.All(d => GameSortCatalog.ToLaunchBoxValue(d.Key) == d.LaunchBoxValue));
        failures += Check("Manual never replaces session sort",
            !GameSortCatalog.UpdatesSession(false, GameSortCatalog.Manual));
        failures += Check("configured playlist override keeps temporary sort local",
            !GameSortCatalog.UpdatesSession(true, "developer"));
        failures += Check("Default playlist field sort updates session",
            GameSortCatalog.UpdatesSession(false, "developer"));
        var deferred = new DeferredGameSort();
        string sessionKey = "title";
        bool sessionAscending = true;
        deferred.Stage(ref sessionKey, ref sessionAscending, "developer", false);
        failures += Check("kiosk sort is staged as next session order",
            deferred.Pending && sessionKey == "developer" && !sessionAscending);
        // Picking Manual (or re-sorting inside a playlist that imposes its order) is NOT a change
        // of the session order, so it must leave the staged kiosk choice untouched.
        deferred.DesktopSelection(ref sessionKey, ref sessionAscending, false, "manual", true);
        failures += Check("node-local desktop sort keeps staged kiosk order",
            deferred.Pending && sessionKey == "developer" && !sessionAscending);
        deferred.DesktopSelection(ref sessionKey, ref sessionAscending, true, "playcount", true);
        failures += Check("global desktop sort replaces staged kiosk order",
            !deferred.Pending && sessionKey == "playcount" && sessionAscending);
        deferred.Stage(ref sessionKey, ref sessionAscending, "genre", false);
        deferred.AppliedOnNodeLoad();
        failures += Check("next node load consumes staged kiosk order",
            !deferred.Pending && sessionKey == "genre" && !sessionAscending);
        var equalManual = new[]
        {
            new HostPlaylistGame { GameIdValue = "z", ManualOrderValue = 0 },
            new HostPlaylistGame { GameIdValue = "a", ManualOrderValue = 0 },
        };
        var manualRanks = GameSortCatalog.ManualRanks(equalManual);
        failures += Check("equal ManualOrder retains PlaylistGame source order",
            manualRanks["z"] == 0 && manualRanks["a"] == 1);

        var a = new DummyGame
        {
            Title = "The Legend of Zelda", SortTitle = "", Developer = "Nintendo",
            ReleaseDate = new DateTime(1986, 2, 21), PlayCount = 4, Favorite = true,
        };
        var b = new DummyGame
        {
            Title = "A Boy and His Blob", SortTitle = "", Developer = "Imagineering",
            ReleaseDate = new DateTime(1989, 1, 1), PlayCount = 2, Favorite = false,
        };
        var title = GameSortCatalog.Getter("title", TitleSortNormalization.Simple);
        failures += Check("Title uses selected normalization", string.CompareOrdinal((string)title(b), (string)title(a)) < 0);
        var plays = GameSortCatalog.Getter("playcount", TitleSortNormalization.Simple);
        failures += Check("Play Count numeric", Convert.ToInt32(plays(a)) > Convert.ToInt32(plays(b)));
        var dates = GameSortCatalog.Getter("releasedate", TitleSortNormalization.Simple);
        failures += Check("Release Date chronological", (DateTime)dates(a) < (DateTime)dates(b));

        var byId = new Dictionary<string, DummyGame>
        {
            ["a"] = a,
            ["b"] = b,
        };
        var playlist = new HostPlaylist();
        playlist.SetResolver(id => byId.TryGetValue(id, out var game) ? game : null);
        playlist.Add(new HostPlaylistGame { GameIdValue = "a", ManualOrderValue = 8 });
        playlist.Add(new HostPlaylistGame { GameIdValue = "b", ManualOrderValue = 2 });
        failures += Check("Manual playlist follows ManualOrder",
            playlist.GetAllGames(false).SequenceEqual(new[] { b, a }));

        var temp = Path.Combine(Path.GetTempPath(), "LiteBoxSortTest_" + Guid.NewGuid().ToString("N"));
        try
        {
            var playlists = Path.Combine(temp, "Playlists");
            Directory.CreateDirectory(playlists);
            File.WriteAllText(Path.Combine(playlists, "Sort.xml"),
                "<LaunchBox><Playlist><PlaylistId>p</PlaylistId><Name>P</Name><SortBy>Manual</SortBy>"
                + "<BigBoxSortByOverride>PlayCount</BigBoxSortByOverride>"
                + "<BigBoxSortDescendingOverride>true</BigBoxSortDescendingOverride></Playlist>"
                + "<PlaylistGame><GameId>a</GameId><ManualOrder>7</ManualOrder></PlaylistGame>"
                + "<PlaylistGame><GameId>b</GameId><ManualOrder>3</ManualOrder></PlaylistGame></LaunchBox>");
            var loaded = PlaylistCatalog.Load(temp, "").Single();
            loaded.SetResolver(id => byId.TryGetValue(id, out var game) ? game : null);
            // BigBoxSortByOverride is NOT modelled — no real playlist file carries one. It must
            // still survive the round-trip, through the generic extra-field channel.
            failures += Check("Playlist XML loads SortBy",
                loaded.SortBy == "Manual");
            failures += Check("Unmodelled playlist element is preserved verbatim",
                loaded.GetField("BigBoxSortByOverride") == "PlayCount");
            failures += Check("Playlist XML ManualOrder controls resolved games",
                loaded.GetAllGames(false).SequenceEqual(new[] { b, a }));
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { }
        }

        failures += FilterChecks();
        failures += MultiEditChecks();

        Console.WriteLine(failures == 0 ? "[game-sort-selftest] ALL PASS" : $"[game-sort-selftest] {failures} FAILURE(S)");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>Auto-Populate: typed comparisons, and how repeated rules on one field combine.</summary>
    private static int FilterChecks()
    {
        int f = 0;
        var arcade = new DummyGame
        {
            Id = "x", Title = "Street Fighter II", Platform = "Arcade", GenresString = "Fighter / Versus",
            Broken = false, PlayCount = 12, MaxPlayers = 3, Source = "Steam",
        };

        f += Check("Bool field offers Is True / Is False",
            PlaylistFilterCatalog.Comparisons(PlaylistFieldKind.Bool).Select(c => c.Key)
                .SequenceEqual(new[] { "IsTrue", "IsFalse", "IsEmpty", "IsNotEmpty" }));
        f += Check("Text field offers the fourteen LaunchBox comparisons",
            PlaylistFilterCatalog.Comparisons(PlaylistFieldKind.Text).Length == 14);
        f += Check("Is True / Is False take no operand",
            !PlaylistFilterCatalog.Comparisons(PlaylistFieldKind.Bool).First(c => c.Key == "IsTrue").UsesValue);
        f += Check("Custom fields sit inline in the alphabetical field list",
            PlaylistFilterCatalog.Fields(new[] { "abc" })[0].Label == "abc");

        // Same deal as Arrange By: "ESRB" is the label, "Rating" is what <FieldKey> must carry so
        // LaunchBox still understands the rule. Both spellings must resolve to that one field.
        var esrb = PlaylistFilterCatalog.Find("ESRB");
        f += Check("Auto-Populate ESRB is a label only — FieldKey stays LaunchBox's",
            esrb != null && esrb.Key == "Rating" && esrb.Label == "ESRB"
            && PlaylistFilterCatalog.Find("Rating")?.Key == "Rating");
        f += Check("A custom field cannot shadow a built-in under either spelling",
            PlaylistFilterCatalog.Fields(new[] { "Rating", "ESRB" })
                .Count(x => x.Key == "Rating") == 1);

        // The exact rule from the screenshots: Broken / Is False, value unused.
        var broken = PlaylistFilterCatalog.Find("Broken");
        var isFalse = PlaylistFilterCatalog.FindComparison("IsFalse", PlaylistFieldKind.Bool);
        f += Check("Broken Is False matches an unbroken game",
            PlaylistFilterCatalog.Compare(PlaylistFilterCatalog.Read(arcade, broken), isFalse, ""));

        // Repeated POSITIVE rules on one field are ORed — the stock Arcade playlists rely on it.
        var orRules = new[]
        {
            new PlaylistFilterDef("Platform", "EqualTo", "Arcade"),
            new PlaylistFilterDef("Genre", "Contains", "Fighter / 2D"),
            new PlaylistFilterDef("Genre", "Contains", "Fighter / Versus"),
        };
        f += Check("Repeated Contains on one field is OR", HostPlaylist.MatchesFilters(arcade, orRules));

        // Repeated ORDERING rules on one field are ANDed — a range must stay a range.
        var inRange = new[]
        {
            new PlaylistFilterDef("PlayCount", "GreaterThan", "5"),
            new PlaylistFilterDef("PlayCount", "LessThan", "20"),
        };
        var outOfRange = new[]
        {
            new PlaylistFilterDef("PlayCount", "GreaterThan", "5"),
            new PlaylistFilterDef("PlayCount", "LessThan", "10"),
        };
        f += Check("Numeric range is AND (inside)", HostPlaylist.MatchesFilters(arcade, inRange));
        f += Check("Numeric range is AND (outside)", !HostPlaylist.MatchesFilters(arcade, outOfRange));

        // A rule LiteBox cannot evaluate must not empty the playlist on its own.
        var unsupported = new[]
        {
            new PlaylistFilterDef("Platform", "EqualTo", "Arcade"),
            new PlaylistFilterDef("SteamAchievements", "IsTrue", ""),
        };
        f += Check("Unsupported field is skipped, not failed",
            HostPlaylist.MatchesFilters(arcade, unsupported));

        f += Check("Storefront flag reads Source", PlaylistFilterCatalog.Compare(
            PlaylistFilterCatalog.Read(arcade, PlaylistFilterCatalog.Find("Steam")),
            PlaylistFilterCatalog.FindComparison("IsTrue", PlaylistFieldKind.Bool), ""));

        f += Check("Has At Least One Of splits on ';'", PlaylistFilterCatalog.Compare(
            "Fighter / Versus;Action", PlaylistFilterCatalog.FindComparison("HasAtLeastOneOf", PlaylistFieldKind.Text),
            "Puzzle;Action"));
        f += Check("Is Similar To ignores punctuation and case", PlaylistFilterCatalog.Compare(
            "Spider-Man 2", PlaylistFilterCatalog.FindComparison("SimilarTo", PlaylistFieldKind.Text), "spider man 2"));
        return f;
    }

    /// <summary>Editing several playlists at once. The rule under test is the one whose failure
    /// costs the most: what the grid does NOT show must survive the apply.</summary>
    private static int MultiEditChecks()
    {
        int f = 0;

        HostPlaylist Make(string id, params (string field, string cmp, string val)[] rules)
        {
            var pl = new HostPlaylist { PlaylistIdValue = id, NameValue = id, AutoPopulateValue = true };
            foreach (var r in rules) pl.AddFilter(new PlaylistFilterDef(r.field, r.cmp, r.val));
            return pl;
        }

        // Two rules shared by all three; each playlist also has one of its own.
        var a = Make("A", ("Platform", "EqualTo", "Arcade"), ("Genre", "Contains", "Fighter"), ("Publisher", "EqualTo", "Capcom"));
        var b = Make("B", ("Platform", "EqualTo", "Arcade"), ("Genre", "Contains", "Fighter"), ("Region", "EqualTo", "Japan"));
        var c = Make("C", ("Genre", "Contains", "Fighter"), ("Platform", "EqualTo", "Arcade"), ("PlayCount", "GreaterThan", "5"));
        var all = new[] { a, b, c };

        var common = PlaylistMultiEdit.CommonFilters(all);
        f += Check("Multi: only the rules every playlist has are shown",
            common.Count == 2
            && common.Any(x => x.FieldKey == "Platform" && x.Value == "Arcade")
            && common.Any(x => x.FieldKey == "Genre" && x.Value == "Fighter"));

        // Drop one shared rule, add a brand new one.
        var after = new List<PlaylistFilterDef>
        {
            new("Genre", "Contains", "Fighter"),
            new("Status", "EqualTo", "Playable"),
        };
        PlaylistMultiEdit.ApplyFilterDifference(all, common, after);

        f += Check("Multi: the removed shared rule leaves every playlist",
            all.All(p => !PlaylistMultiEdit.FiltersOf(p).Any(x => x.FieldKey == "Platform")));
        f += Check("Multi: the added rule joins every playlist",
            all.All(p => PlaylistMultiEdit.FiltersOf(p).Any(x => x.FieldKey == "Status" && x.Value == "Playable")));
        f += Check("Multi: rules the grid never showed are untouched",
            PlaylistMultiEdit.FiltersOf(a).Any(x => x.FieldKey == "Publisher")
            && PlaylistMultiEdit.FiltersOf(b).Any(x => x.FieldKey == "Region")
            && PlaylistMultiEdit.FiltersOf(c).Any(x => x.FieldKey == "PlayCount"));
        f += Check("Multi: the untouched shared rule is not duplicated",
            all.All(p => PlaylistMultiEdit.FiltersOf(p).Count(x => x.FieldKey == "Genre") == 1));

        // Applying with nothing changed must not rewrite anything.
        var before2 = PlaylistMultiEdit.CommonFilters(all);
        PlaylistMultiEdit.ApplyFilterDifference(all, before2, before2);
        f += Check("Multi: an apply with no edit changes nothing",
            PlaylistMultiEdit.FiltersOf(a).Count == 3 && PlaylistMultiEdit.FiltersOf(b).Count == 3);

        // Case only differs → same rule, not a duplicate.
        var caseOnly = new List<PlaylistFilterDef> { new("genre", "contains", "fighter"), new("Status", "EqualTo", "Playable") };
        PlaylistMultiEdit.ApplyFilterDifference(all, before2, caseOnly);
        f += Check("Multi: a rule differing only in case is not added twice",
            PlaylistMultiEdit.FiltersOf(a).Count(x => string.Equals(x.FieldKey, "Genre", StringComparison.OrdinalIgnoreCase)) == 1);

        // ── Games: intersection, union, and removal across the set ──
        var g1 = new DummyGame { Id = "g1", Title = "One" };
        var g2 = new DummyGame { Id = "g2", Title = "Two" };
        var g3 = new DummyGame { Id = "g3", Title = "Three" };
        var byId = new Dictionary<string, IGame> { ["g1"] = g1, ["g2"] = g2, ["g3"] = g3 };

        HostPlaylist Manual(string id, params string[] gameIds)
        {
            var pl = new HostPlaylist { PlaylistIdValue = id, NameValue = id, AutoPopulateValue = false };
            pl.SetResolver(x => byId.TryGetValue(x, out var game) ? game : null);
            for (int i = 0; i < gameIds.Length; i++)
                pl.Add(new HostPlaylistGame { GameIdValue = gameIds[i], ManualOrderValue = i });
            return pl;
        }

        var m1 = Manual("M1", "g1", "g2");
        var m2 = Manual("M2", "g2", "g3");
        var manuals = new[] { m1, m2 };

        f += Check("Multi: only games present in every playlist are listed",
            PlaylistMultiEdit.CommonGameIds(manuals).SequenceEqual(new[] { "g2" }));
        f += Check("Multi: the hidden count is union minus common",
            PlaylistMultiEdit.UnionGameCount(manuals) - PlaylistMultiEdit.CommonGameIds(manuals).Count == 2);

        PlaylistMultiEdit.RemoveGames(manuals, new[] { "g2" });
        f += Check("Multi: removing a common game removes it everywhere",
            !PlaylistMultiEdit.CommonGameIds(manuals).Any()
            && m1.GetAllGames(false).Select(x => x.Id).SequenceEqual(new[] { "g1" })
            && m2.GetAllGames(false).Select(x => x.Id).SequenceEqual(new[] { "g3" }));
        return f;
    }

    private static int Check(string name, bool ok)
    {
        Console.WriteLine($"[game-sort-selftest] {(ok ? "PASS" : "FAIL")} {name}");
        return ok ? 0 : 1;
    }
}
