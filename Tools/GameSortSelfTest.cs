using System;
using System.Collections.Generic;
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

        Console.WriteLine(failures == 0 ? "[game-sort-selftest] ALL PASS" : $"[game-sort-selftest] {failures} FAILURE(S)");
        return failures == 0 ? 0 : 1;
    }

    private static int Check(string name, bool ok)
    {
        Console.WriteLine($"[game-sort-selftest] {(ok ? "PASS" : "FAIL")} {name}");
        return ok ? 0 : 1;
    }
}
