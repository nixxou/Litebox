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

        Console.WriteLine(failures == 0
            ? $"[filter-parity] ALL PASS ({checks} combinations)"
            : $"[filter-parity] {failures} FAILURE(S)");
        return failures == 0 ? 0 : 1;
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
