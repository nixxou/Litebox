using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LbApiHost.Generated;
using LbApiHost.Host;
using LbApiHost.Host.Data;

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
        deferred.DesktopSelection(ref sessionKey, ref sessionAscending, false, "manual", true);
        failures += Check("node-local desktop sort cancels staged kiosk order",
            !deferred.Pending && sessionKey == "title" && sessionAscending);
        deferred.Stage(ref sessionKey, ref sessionAscending, "publisher", false);
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
            failures += Check("Playlist XML loads SortBy and BigBox direction",
                loaded.SortBy == "Manual" && loaded.BigBoxSortByOverride == "PlayCount"
                && loaded.BigBoxSortDescendingOverride);
            failures += Check("Playlist XML ManualOrder controls resolved games",
                loaded.GetAllGames(false).SequenceEqual(new[] { b, a }));
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { }
        }

        Console.WriteLine(failures == 0 ? "[game-sort-selftest] ALL PASS" : $"[game-sort-selftest] {failures} FAILURE(S)");
        return failures == 0 ? 0 : 1;
    }

    private static int Check(string name, bool ok)
    {
        Console.WriteLine($"[game-sort-selftest] {(ok ? "PASS" : "FAIL")} {name}");
        return ok ? 0 : 1;
    }
}
