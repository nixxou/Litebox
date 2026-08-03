// --selftest-filter-parity: the desktop and both web clients must agree on what a search matches.
//
// Same shape as the sort parity test: the C# side runs GameTextFilter, the JS side runs the real
// web-assets/vendor/game-sort.js under node, over the same titles and queries, in both modes
// (contains for a deliberate search, starts-with for the transient one). A rule that drifts on one
// side fails here with the title and query that expose it.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using LbApiHost.Host;
using LbApiHost.Generated;

namespace LbApiHost.Tools;

internal static class FilterParitySelfTest
{
    private static readonly string[] Titles =
    {
        "The Legend of Zelda",
        "Street Fighter II",
        "Spider-Man 2",
        "Pokémon Snap",
        "Café International",
        "A Boy and His Blob",
        "Metal Slug 3",
        "F-Zero",
        "Sonic & Knuckles",
        "Zed Blade",
        "The Witcher 3: Wild Hunt",
        "",
    };

    private static readonly string[] Queries =
    {
        "the legend", "legend", "zelda",
        "street fighter", "street-fighter", "streetfighter", "STREETFIGHTER",
        "spider man", "spiderman", "spider-man",
        "pokemon", "pokémon", "pokmon",
        "cafe", "café",
        "a boy", "boy",
        "f zero", "fzero", "f-zero",
        "sonic &", "sonic and", "sonic",
        "z", "s", "3", "!", "  ", "",
        // Lettres seules : le cas du rail A-Z. "l" doit atteindre "The Legend of Zelda",
        // "b" "A Boy and His Blob", et "t"/"a" doivent encore les atteindre par la forme brute.
        "l", "b", "t", "a", "m", "f",
        // Cas remonté à l'usage : "wit" doit trouver "The Witcher", article de tête ou pas.
        "wit", "witcher", "the wit", "wild",
    };

    public static int Run()
    {
        string script = FindGameSortJs();
        if (script == null) { Console.WriteLine("[filter-parity] SKIP game-sort.js not found"); return 0; }
        if (!NodeAvailable()) { Console.WriteLine("[filter-parity] SKIP node not on PATH"); return 0; }

        var web = RunNode(script);
        if (web == null) { Console.WriteLine("[filter-parity] FAIL node driver produced no output"); return 1; }

        int failures = 0, checks = 0;
        foreach (bool prefix in new[] { false, true })
            foreach (var q in Queries)
                foreach (var t in Titles)
                {
                    bool desktop = GameTextFilter.Matches(t, q, prefix);
                    string key = Key(t, q, prefix);
                    if (!web.TryGetValue(key, out bool js))
                    {
                        Console.WriteLine($"[filter-parity] FAIL no web result for {key}");
                        failures++;
                        continue;
                    }
                    checks++;
                    if (desktop == js) continue;
                    Console.WriteLine($"[filter-parity] FAIL {(prefix ? "startsWith" : "contains")} "
                        + $"title=\"{t}\" query=\"{q}\" desktop={desktop} web={js}");
                    failures++;
                }

        failures += AdvancedParity(script);

        Console.WriteLine(failures == 0
            ? $"[filter-parity] ALL PASS ({checks} combinations + advanced)"
            : $"[filter-parity] {failures} FAILURE(S)");
        return failures == 0 ? 0 : 1;
    }

    // ── Phase 2 : le filtre AVANCÉ — FilterCriteria.Matches (C#) vs LBGameSort.matchesAdvanced
    // (le vrai vendor sous node), sur les mêmes jeux et les mêmes critères. Les dimensions à
    // discriminant externe (achievements/hiscore/saves/manettes : cast HostGame, hiscore.dat,
    // sous-entités) sont couvertes par leurs tests unitaires respectifs ; ici on prouve les
    // SÉMANTIQUES pures — jetons entiers, ET/OU, égalité exacte, casse. ─────────────────────────
    private sealed class FakeAdvGame : DummyGame
    {
        public string P = "", Reg = "", Pm = "", St = "", Pr = "", Es = "", Rt = "", Gen = "", Pub = "", Dev = "";
        public int? Mp, Year;
        public double Sr;
        public bool FavV;
        public bool? InstV;   // tri-état, comme la case Installed réelle : null = jamais renseignée
        public override string Platform { get => P; set { } }
        public override string Region { get => Reg; set { } }
        public override string PlayMode { get => Pm; set { } }
        public override string Status { get => St; set { } }
        public override string Progress { get => Pr; set { } }
        public override string Rating { get => Es; set { } }
        public override string ReleaseType { get => Rt; set { } }
        public override string GenresString { get => Gen; set { } }
        public override string Publisher { get => Pub; set { } }
        public override string Developer { get => Dev; set { } }
        public override int? MaxPlayers { get => Mp; set { } }
        public override int? ReleaseYear { get => Year; set { } }
        public override float CommunityOrLocalStarRating { get => (float)Sr; set { } }
        public override bool Favorite { get => FavV; set { } }
        public override bool? Installed { get => InstV; set { } }
    }

    // La sémantique « Installed only » sur les TROIS états de la case — le point qu'un fixture ne
    // servant que `installed` masquait : null (jamais renseignée) = présente, false = exclue.
    // `installed` est servi comme le vrai payload le sert : Installed ?? true (WebStoreState).

    // Un seul littéral JSON par critère, consommé par LES DEUX côtés : désérialisé en
    // FilterCriteria (camelCase) pour le desktop, passé verbatim au vendor pour le web.
    // Toute divergence de sémantique éclate ici avec le jeu et le critère qui l'exposent.
    private static readonly string[] AdvCriteria =
    {
        "{}",
        "{\"platforms\":[\"Arcade\",\"FBNeo\"]}",
        "{\"regions\":[\"France\"]}",
        "{\"regions\":[\"Europe\"]}",
        "{\"playModes\":[\"Cooperative\"]}",
        "{\"statuses\":[\"playable\"]}",
        "{\"progresses\":[\"Not Started\"]}",
        "{\"esrb\":[\"E\"]}",
        "{\"maxPlayers\":2}",
        "{\"maxPlayers\":4}",
        "{\"yearMin\":2000}",
        "{\"yearMax\":1999}",
        "{\"ratingMin\":3.0}",
        "{\"releaseType\":\"physical\"}",
        // Deux clés pour la même dimension : le C# la nomme fav/installed, la forme web (l'historique
        // de BB-web préexiste à ce travail) flagFav/flagInstalled. Chaque côté lit la sienne.
        "{\"fav\":true,\"flagFav\":true}",
        "{\"installed\":true,\"flagInstalled\":true}",
        "{\"genres\":[\"Action\"],\"genreMode\":\"or\"}",
        "{\"genres\":[\"Action\",\"RPG\"],\"genreMode\":\"and\"}",
        "{\"publisher\":\"cap\"}",
        "{\"developer\":\"soft\"}",
        "{\"platforms\":[\"Arcade\"],\"regions\":[\"Japan\"]}",
    };

    private static int AdvancedParity(string script)
    {
        var games = new[]
        {
            new FakeAdvGame { P = "Arcade", Reg = "Europe; France", Pm = "Cooperative", St = "Playable",
                              Pr = "Not Started", Es = "E", Rt = "Physical", Gen = "Action; Platform",
                              Pub = "Capcom", Dev = "Capcom", Mp = 2, Year = 1995, Sr = 4.0, FavV = true, InstV = true },
            new FakeAdvGame { P = "FBNeo", Reg = "Eastern Europe", Pm = "Single Player", Es = "M", InstV = false },
            new FakeAdvGame { P = "Sony Playstation", Reg = "Japan", Pm = "Multiplayer; Cooperative",
                              Rt = "Digital", Gen = "RPG", Pub = "Square", Dev = "SquareSoft",
                              Mp = 4, Year = 2001, Sr = 2.5, InstV = null },
        };
        // La forme PAYLOAD de chacun — les clés que LightItem sert et que le vendor lit. `inst` (le
        // tri-état brut) et `installed` (Installed ?? true) divergent VOLONTAIREMENT sur le jeu 3 :
        // si le vendor lisait le mauvais champ, la parité éclaterait ici.
        var payload = games.Select(g => new Dictionary<string, object>
        {
            ["platform"] = g.P, ["region"] = g.Reg, ["playMode"] = g.Pm, ["status"] = g.St,
            ["progress"] = g.Pr, ["esrb"] = g.Es, ["rt"] = g.Rt, ["g"] = g.Gen,
            ["pub"] = g.Pub, ["dev"] = g.Dev, ["maxPlayers"] = g.Mp, ["ry"] = g.Year,
            ["sr"] = g.Sr > 0 ? g.Sr : (double?)null, ["fav"] = g.FavV,
            ["inst"] = g.InstV, ["installed"] = g.InstV ?? true,
        }).ToArray();

        var camel = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var web = RunNodeAdvanced(script, JsonSerializer.Serialize(payload), "[" + string.Join(",", AdvCriteria) + "]");
        if (web == null) { Console.WriteLine("[filter-parity] FAIL advanced node driver produced no output"); return 1; }

        int failures = 0, checks = 0;
        for (int ci = 0; ci < AdvCriteria.Length; ci++)
        {
            var crit = JsonSerializer.Deserialize<Host.Search.FilterCriteria>(AdvCriteria[ci], camel);
            for (int gi = 0; gi < games.Length; gi++)
            {
                bool desktop = crit.Matches(games[gi]);
                if (!web.TryGetValue(ci + "|" + gi, out bool js))
                { Console.WriteLine($"[filter-parity] FAIL advanced: no web result for crit#{ci} game#{gi}"); failures++; continue; }
                checks++;
                if (desktop == js) continue;
                Console.WriteLine($"[filter-parity] FAIL advanced crit={AdvCriteria[ci]} game#{gi} desktop={desktop} web={js}");
                failures++;
            }
        }
        if (failures == 0) Console.WriteLine($"[filter-parity] advanced: {checks} combinations agree");
        return failures;
    }

    private static Dictionary<string, bool> RunNodeAdvanced(string scriptPath, string gamesJson, string critsJson)
    {
        string work = Path.Combine(Path.GetTempPath(), "LiteBoxAdvParity_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var utf8 = new UTF8Encoding(false);
            File.WriteAllText(Path.Combine(work, "games.json"), gamesJson, utf8);
            File.WriteAllText(Path.Combine(work, "crits.json"), critsJson, utf8);
            string driver = Path.Combine(work, "driver.js");
            File.WriteAllText(driver,
                "const fs=require('fs'),vm=require('vm');\n" +
                "const s={window:{},location:{hash:''},console};s.globalThis=s;vm.createContext(s);\n" +
                "vm.runInContext(fs.readFileSync(" + Js(scriptPath) + ",'utf8'),s);\n" +
                "const S=s.window.LBGameSort;\n" +
                "const games=JSON.parse(fs.readFileSync(" + Js(Path.Combine(work, "games.json")) + ",'utf8'));\n" +
                "const crits=JSON.parse(fs.readFileSync(" + Js(Path.Combine(work, "crits.json")) + ",'utf8'));\n" +
                "const out={};\n" +
                "crits.forEach((c,ci)=>games.forEach((g,gi)=>{ out[ci+'|'+gi]=S.matchesAdvanced(g,c); }));\n" +
                "process.stdout.write(JSON.stringify(out));\n", utf8);

            using var p = Process.Start(new ProcessStartInfo("node", "\"" + driver + "\"")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
                StandardOutputEncoding = utf8,
            });
            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(60000);
            if (p.ExitCode != 0) { Console.WriteLine("[filter-parity] advanced node exited " + p.ExitCode + ": " + stderr.Trim()); return null; }
            return JsonSerializer.Deserialize<Dictionary<string, bool>>(stdout);
        }
        catch (Exception ex) { Console.WriteLine("[filter-parity] advanced node driver error: " + ex.Message); return null; }
        finally { try { Directory.Delete(work, true); } catch { } }
    }

    private static string Key(string title, string query, bool prefix)
        => (prefix ? "p" : "c") + "" + title + "" + query;

    private static Dictionary<string, bool> RunNode(string scriptPath)
    {
        string work = Path.Combine(Path.GetTempPath(), "LiteBoxFilterParity_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var utf8 = new UTF8Encoding(false);
            File.WriteAllText(Path.Combine(work, "titles.json"), JsonSerializer.Serialize(Titles), utf8);
            File.WriteAllText(Path.Combine(work, "queries.json"), JsonSerializer.Serialize(Queries), utf8);
            string driver = Path.Combine(work, "driver.js");
            File.WriteAllText(driver,
                "const fs=require('fs'),vm=require('vm');\n" +
                "const s={window:{},location:{hash:''},console};s.globalThis=s;vm.createContext(s);\n" +
                "vm.runInContext(fs.readFileSync(" + Js(scriptPath) + ",'utf8'),s);\n" +
                "const S=s.window.LBGameSort;\n" +
                "const titles=JSON.parse(fs.readFileSync(" + Js(Path.Combine(work, "titles.json")) + ",'utf8'));\n" +
                "const queries=JSON.parse(fs.readFileSync(" + Js(Path.Combine(work, "queries.json")) + ",'utf8'));\n" +
                "const out={};\n" +
                "for (const prefix of [false,true]) for (const q of queries) for (const t of titles)\n" +
                "  out[(prefix?'p':'c')+'\\u0001'+t+'\\u0001'+q] = S.titleMatches(t,q,prefix);\n" +
                "process.stdout.write(JSON.stringify(out));\n", utf8);

            using var p = Process.Start(new ProcessStartInfo("node", "\"" + driver + "\"")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
                StandardOutputEncoding = utf8,
            });
            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(60000);
            if (p.ExitCode != 0) { Console.WriteLine("[filter-parity] node exited " + p.ExitCode + ": " + stderr.Trim()); return null; }
            return JsonSerializer.Deserialize<Dictionary<string, bool>>(stdout);
        }
        catch (Exception ex) { Console.WriteLine("[filter-parity] node driver error: " + ex.Message); return null; }
        finally { try { Directory.Delete(work, true); } catch { } }
    }

    private static string Js(string s) => JsonSerializer.Serialize(s);

    private static string FindGameSortJs()
    {
        string fallback = null;
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "web-assets", "vendor", "game-sort.js");
            if (!File.Exists(candidate)) continue;
            if (!candidate.Replace('\\', '/').Contains("/bin/", StringComparison.OrdinalIgnoreCase)) return candidate;
            fallback ??= candidate;
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
}
