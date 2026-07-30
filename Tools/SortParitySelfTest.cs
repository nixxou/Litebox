// --selftest-sort-parity: the desktop and the two web clients must order the SAME games the SAME
// way, for every Arrange By field, in both directions.
//
// This is not a re-implementation check. The C# side runs GameSortCatalog.Getter through
// SortValueComparer exactly as GameListView does; the JS side runs the real
// web-assets/vendor/game-sort.js under node, fed the payload shape OwnedDataProvider emits. If the
// two ever disagree — a getter changed on one side, a payload field renamed, a tie rule drifting —
// this fails and names the field.
//
// The sample is built to be hostile: duplicate values so ties matter, missing years / ratings /
// dates so the null rule matters, and titles whose alphabetical order contradicts the source order
// so a missing tie key cannot pass by luck. Skipped (not failed) when node is unavailable.

using System;
using System.Collections.Generic;
using System.Diagnostics;

using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using LbApiHost.Generated;
using LbApiHost.Host;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Tools;

internal static class SortParitySelfTest
{
    private const TitleSortNormalization Mode = TitleSortNormalization.Simple;

    public static int Run()
    {
        var games = Sample();
        string script = FindGameSortJs();
        if (script == null)
        {
            Console.WriteLine("[sort-parity] SKIP web-assets/vendor/game-sort.js not found");
            return 0;
        }
        if (!NodeAvailable())
        {
            Console.WriteLine("[sort-parity] SKIP node not on PATH");
            return 0;
        }

        var keys = GameSortCatalog.Standard.Select(d => d.Key).ToList();
        string payload = JsonSerializer.Serialize(games.Select(PayloadItem).ToArray());
        var jsOrders = RunNode(script, payload, keys);
        if (jsOrders == null)
        {
            Console.WriteLine("[sort-parity] FAIL node driver produced no output");
            return 1;
        }

        int failures = 0;
        foreach (var key in keys)
            foreach (bool ascending in new[] { true, false })
            {
                string label = key + (ascending ? " asc" : " desc");
                var desktop = DesktopOrder(games, key, ascending);
                if (!jsOrders.TryGetValue(label, out var web))
                {
                    Console.WriteLine($"[sort-parity] FAIL {label} — no web result");
                    failures++;
                    continue;
                }
                bool ok = desktop.SequenceEqual(web, StringComparer.Ordinal);
                Console.WriteLine($"[sort-parity] {(ok ? "PASS" : "FAIL")} {label}");
                if (!ok)
                {
                    Console.WriteLine("             desktop: " + string.Join(", ", desktop));
                    Console.WriteLine("             web    : " + string.Join(", ", web));
                    failures++;
                }
            }

        Console.WriteLine(failures == 0
            ? "[sort-parity] ALL PASS"
            : $"[sort-parity] {failures} FAILURE(S)");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>The desktop rule, verbatim from GameListView.RebuildView: primary key through
    /// SortValueComparer, then the title key ASCENDING in both directions.</summary>
    private static string[] DesktopOrder(IReadOnlyList<IGame> games, string key, bool ascending)
    {
        var getter = GameSortCatalog.Getter(key, Mode);
        Func<IGame, object> tie = g => TitleSortNormalizer.Normalize(g, Mode);
        var ordered = ascending
            ? games.OrderBy(g => getter(g), SortValueComparer.Instance)
            : games.OrderByDescending(g => getter(g), SortValueComparer.Instance);
        if (!string.Equals(key, "title", StringComparison.OrdinalIgnoreCase))
            ordered = ordered.ThenBy(tie, SortValueComparer.Instance);
        return ordered.Select(g => g.Id).ToArray();
    }

    // Mirrors the sortable half of OwnedDataProvider.LightItem. Any rename there must land here too
    // — which is the point: this test is what makes such a rename fail loudly.
    private static object PayloadItem(IGame g) => new Dictionary<string, object>
    {
        ["id"] = g.Id,
        ["t"] = g.Title,
        ["cn"] = TitleSortNormalizer.Normalize(g, Mode),
        ["dev"] = g.Developer ?? "",
        ["pub"] = g.Publisher ?? "",
        ["g"] = g.GenresString ?? "",
        ["platform"] = g.Platform ?? "",
        ["esrb"] = g.Rating ?? "",
        ["rt"] = g.ReleaseType ?? "",
        ["region"] = g.Region ?? "",
        ["playMode"] = g.PlayMode ?? "",
        ["progress"] = g.Progress ?? "",
        ["series"] = g.Series ?? "",
        ["source"] = g.Source ?? "",
        ["status"] = g.Status ?? "",
        ["version"] = g.Version ?? "",
        ["sr"] = g.CommunityOrLocalStarRating > 0 ? g.CommunityOrLocalStarRating : (float?)null,
        ["ry"] = GameSortCatalog.EffectiveYear(g),
        ["inst"] = g.Installed == true,
        ["fav"] = g.Favorite,
        ["portable"] = g.Portable,
        ["mameHs"] = GameSortCatalog.MameHighScoresSupported(g),
        ["playCount"] = g.PlayCount,
        ["playTime"] = g.PlayTime,
        ["maxPlayers"] = g.MaxPlayers,
        ["dbId"] = g.LaunchBoxDbId,
        ["da"] = Ms(g.DateAdded),
        ["dm"] = Ms(g.DateModified),
        ["lp"] = Ms(g.LastPlayedDate),
        ["rd"] = Ms(g.ReleaseDate),
    };

    private static readonly DateTime Epoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static double Ms(DateTime? value)
        => value.HasValue && value.Value != default
            ? (value.Value.ToUniversalTime() - Epoch).TotalMilliseconds : 0;

    private static IReadOnlyList<IGame> Sample()
    {
        // Source order is deliberately NOT alphabetical: with a correct title tie key the two
        // surfaces agree, without one they cannot.
        return new IGame[]
        {
            new DummyGame
            {
                Id = "metal", Title = "Metal Slug", Developer = "Nazca", Publisher = "SNK",
                GenresString = "Shooter", Platform = "Arcade", Region = "Japan", PlayMode = "Co-op",
                Rating = "T", ReleaseType = "Released", Series = "Metal Slug", Source = "Steam",
                Status = "Playable", Version = "Rev A", Progress = "",
                ReleaseDate = new DateTime(1996, 4, 19), ReleaseYear = 1996,
                DateAdded = new DateTime(2020, 1, 5), DateModified = new DateTime(2021, 3, 1),
                LastPlayedDate = new DateTime(2024, 6, 1),
                PlayCount = 12, PlayTime = 3600, MaxPlayers = 2, LaunchBoxDbId = 900,
                StarRatingFloat = 4.5f, CommunityStarRating = 4.5f, Favorite = true, Installed = true, Portable = false,
            },
            new DummyGame
            {
                // Same genre as "metal" and no year at all — exercises both the tie key and null.
                Id = "aero", Title = "Aero Fighters", Developer = "Video System", Publisher = "SNK",
                GenresString = "Shooter", Platform = "Arcade", Region = "Japan", PlayMode = "Co-op",
                Rating = "T", ReleaseType = "Released", Series = "", Source = "Steam",
                Status = "Playable", Version = "", Progress = "",
                DateAdded = new DateTime(2020, 1, 5), DateModified = new DateTime(2019, 2, 2),
                PlayCount = 12, PlayTime = 0, LaunchBoxDbId = null,
                Favorite = true, Installed = null, Portable = false,
            },
            new DummyGame
            {
                // Third of the same genre, alphabetically last, first in nothing else.
                Id = "zed", Title = "Zed Blade", Developer = "NMK", Publisher = "SNK",
                GenresString = "Shooter", Platform = "Arcade", Region = "World", PlayMode = "Single",
                Rating = "", ReleaseType = "Released", Series = "", Source = "GOG",
                Status = "", Version = "", Progress = "In Progress",
                ReleaseDate = new DateTime(1994, 11, 1),
                DateAdded = new DateTime(2020, 1, 5),
                PlayCount = 0, PlayTime = 0, MaxPlayers = 2, LaunchBoxDbId = 12,
                StarRatingFloat = 0f, CommunityStarRating = 0f, Favorite = false, Installed = false, Portable = true,
            },
            new DummyGame
            {
                // Article-stripping matters here: "A Boy…" sorts under B, not A.
                Id = "boy", Title = "A Boy and His Blob", Developer = "Imagineering", Publisher = "Absolute",
                GenresString = "Platform", Platform = "NES", Region = "USA", PlayMode = "Single",
                Rating = "E", ReleaseType = "Released", Series = "Blob", Source = "",
                Status = "Playable", Version = "1.0", Progress = "",
                ReleaseDate = new DateTime(1989, 1, 1), ReleaseYear = 1989,
                DateAdded = new DateTime(2019, 6, 6), DateModified = new DateTime(2019, 6, 6),
                LastPlayedDate = new DateTime(2023, 1, 1),
                PlayCount = 2, PlayTime = 60, MaxPlayers = 1, LaunchBoxDbId = 33,
                StarRatingFloat = 3f, CommunityStarRating = 3f, Favorite = false, Installed = true, Portable = false,
            },
            new DummyGame
            {
                // Year only through ReleaseDate — the effective-year rule must agree on both sides.
                Id = "zelda", Title = "The Legend of Zelda", Developer = "Nintendo", Publisher = "Nintendo",
                GenresString = "Action", Platform = "NES", Region = "USA", PlayMode = "Single",
                Rating = "E", ReleaseType = "Released", Series = "Zelda", Source = "",
                Status = "Playable", Version = "", Progress = "Finished",
                ReleaseDate = new DateTime(1986, 2, 21),
                DateAdded = new DateTime(2018, 1, 1), DateModified = new DateTime(2022, 5, 5),
                PlayCount = 4, PlayTime = 900, MaxPlayers = 1, LaunchBoxDbId = 1,
                StarRatingFloat = 5f, CommunityStarRating = 5f, Favorite = true, Installed = true, Portable = false,
            },
        };
    }

    /// <summary>Prefers the file in the SOURCE tree over the build-output copy: editing the asset
    /// and re-running the test without a rebuild must exercise the edit, not last build's copy.</summary>
    private static string FindGameSortJs()
    {
        string fallback = null;
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "web-assets", "vendor", "game-sort.js");
            if (!File.Exists(candidate)) continue;
            bool underBin = candidate.Replace('\\', '/').Contains("/bin/", StringComparison.OrdinalIgnoreCase);
            if (!underBin) return candidate;
            fallback ??= candidate;   // self-contained deployment: only the output copy exists
        }
        return fallback;
    }

    private static bool NodeAvailable()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("node", "--version")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            p.WaitForExit(10000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>Loads the real game-sort.js in node and asks it to order the payload by every key,
    /// both directions. Returns "key asc"/"key desc" → the resulting id sequence.</summary>
    private static Dictionary<string, string[]> RunNode(string scriptPath, string payloadJson, IEnumerable<string> keys)
    {
        string work = Path.Combine(Path.GetTempPath(), "LiteBoxSortParity_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            string driver = Path.Combine(work, "driver.js");
            File.WriteAllText(Path.Combine(work, "payload.json"), payloadJson, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(work, "keys.json"),
                JsonSerializer.Serialize(keys.ToArray()), new UTF8Encoding(false));
            File.WriteAllText(driver, DriverSource(scriptPath, work), new UTF8Encoding(false));

            using var p = Process.Start(new ProcessStartInfo("node", "\"" + driver + "\"")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(60000);
            if (p.ExitCode != 0)
            {
                Console.WriteLine("[sort-parity] node exited " + p.ExitCode + ": " + stderr.Trim());
                return null;
            }
            return JsonSerializer.Deserialize<Dictionary<string, string[]>>(stdout);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[sort-parity] node driver error: " + ex.Message);
            return null;
        }
        finally
        {
            try { Directory.Delete(work, true); } catch { }
        }
    }

    // game-sort.js is a browser IIFE assigning window.LBGameSort; node only needs a window and a
    // location for it to attach to.
    private static string DriverSource(string scriptPath, string work) =>
        "const fs = require('fs');\n" +
        "const vm = require('vm');\n" +
        "const sandbox = { window: {}, location: { hash: '' }, localStorage: null, console };\n" +
        "sandbox.globalThis = sandbox;\n" +
        "vm.createContext(sandbox);\n" +
        "vm.runInContext(fs.readFileSync(" + JsLiteral(scriptPath) + ", 'utf8'), sandbox);\n" +
        "const S = sandbox.window.LBGameSort;\n" +
        "const games = JSON.parse(fs.readFileSync(" + JsLiteral(Path.Combine(work, "payload.json")) + ", 'utf8'));\n" +
        "const keys = JSON.parse(fs.readFileSync(" + JsLiteral(Path.Combine(work, "keys.json")) + ", 'utf8'));\n" +
        "const out = {};\n" +
        "for (const key of keys) for (const dir of ['asc', 'desc'])\n" +
        "  out[key + ' ' + dir] = S.sorted(games, { key, dir }).map(g => g.id);\n" +
        "process.stdout.write(JSON.stringify(out));\n";

    private static string JsLiteral(string path)
        => JsonSerializer.Serialize(path, new JsonSerializerOptions { Encoder = null });
}
