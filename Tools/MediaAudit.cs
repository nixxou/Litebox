// Reads a real library and asks one question of every game: does the code that MOVES media agree
// with the code that FINDS it?
//
// Two independent notions of "this file belongs to this game" live in the tree — MediaResolver's,
// used to display, and GameMediaRenamer's, used to rename and merge. Nothing forces them to agree,
// and a disagreement is invisible until someone renames a game:
//
//   found but not moved   the game displays it, a rename leaves it behind → the game loses it
//   moved but not found   a rename takes a file the game never showed → it may belong elsewhere
//
// Read-only: nothing is written, nothing is moved. Run it against the real library.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LbApiHost.Host.Media;

namespace LbApiHost.Tools;

internal static class MediaAudit
{
    public static int Run(string lbRoot)
    {
        if (string.IsNullOrEmpty(lbRoot) || !Directory.Exists(Path.Combine(lbRoot, "Data")))
        { Console.WriteLine($"[audit] pas de bibliotheque sous {lbRoot}"); return 1; }

        var games = LoadGames(Path.Combine(lbRoot, "Data", "Platforms"));
        Console.WriteLine($"[audit] {games.Count} jeux, {games.Select(g => g.Platform).Distinct().Count()} plateformes");

        int foundNotMoved = 0, movedNotFound = 0, agreed = 0;
        var byReason = new Dictionary<string, int>(StringComparer.Ordinal);
        var samples = new List<string>();

        foreach (var g in games)
        {
            if (g.Id == Guid.Empty || g.Title.Length == 0 || g.Platform.Length == 0) continue;
            string sani = MediaResolver.Sanitize(g.Title);
            if (sani.Length == 0) continue;

            // What the MOVER would take.
            var moved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var unit in GameMediaRenamer.Units(lbRoot, g.Platform))
                foreach (var dir in unit)
                {
                    List<string> files;
                    try { files = Directory.EnumerateFiles(dir).ToList(); } catch { continue; }
                    foreach (var f in files)
                    {
                        string n = Path.GetFileNameWithoutExtension(f);
                        if (GameMediaRenamer.TryGuid(n, g.Id, out _, out _)
                            || GameMediaRenamer.TryPlain(n, sani, out _)) moved.Add(f);
                    }
                }

            // What the FINDER attributes: every file in those folders that TryMatch claims.
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var unit in GameMediaRenamer.Units(lbRoot, g.Platform))
                foreach (var dir in unit)
                {
                    List<string> files;
                    try { files = Directory.EnumerateFiles(dir).ToList(); } catch { continue; }
                    foreach (var f in files)
                        if (MediaResolver.BelongsTo(Path.GetFileNameWithoutExtension(f), g.Id, sani))
                            found.Add(f);
                }

            foreach (var f in found.Except(moved))
            {
                foundNotMoved++;
                Bump(byReason, "trouve mais PAS deplace");
                if (samples.Count < 8) samples.Add($"    trouve, pas deplace : {Short(lbRoot, f)}   (jeu {g.Title})");
            }
            foreach (var f in moved.Except(found))
            {
                movedNotFound++;
                Bump(byReason, "deplace mais PAS trouve");
                if (samples.Count < 8) samples.Add($"    deplace, pas trouve : {Short(lbRoot, f)}   (jeu {g.Title})");
            }
            agreed += moved.Intersect(found).Count();
        }

        Console.WriteLine($"[audit] {agreed} fichiers ou les deux sont d'accord");
        foreach (var kv in byReason) Console.WriteLine($"[audit] {kv.Value} {kv.Key}");
        foreach (var s in samples) Console.WriteLine("[audit] " + s);
        GlobCheck();

        Console.WriteLine(foundNotMoved + movedNotFound == 0
            ? "[audit] ACCORD TOTAL entre le resolveur et le renommeur"
            : $"[audit] {foundNotMoved + movedNotFound} DESACCORD(S)");
        return 0;
    }

    /// <summary>BestInDir narrows with a glob on the TITLE before TryMatch ever runs, and TryMatch
    /// deliberately ignores the title part of a GUID name. If the glob wins, a GUID file carrying an
    /// OLD title is invisible — which is exactly the shape the deferred-rename transit produces.
    /// Manuals and music always go through BestInDir.</summary>
    private static void GlobCheck()
    {
        string tmp = Path.Combine(Path.GetTempPath(), "globcheck-" + Guid.NewGuid().ToString("N"));
        string was = MediaResolver.SwapRootForTest(tmp);
        try
        {
            var id = Guid.NewGuid();
            string dir = Path.Combine(tmp, "Manuals", "Test Platform");
            Directory.CreateDirectory(dir);
            // Le fichier tel que le transit differe le produit : ANCIEN titre + GUID du jeu.
            File.WriteAllText(Path.Combine(dir, $"Ancien Titre.{id:D}-01.pdf"), "x");

            string found = MediaResolver.Manual("Test Platform", id, "Nouveau Titre");
            Console.WriteLine(found != null
                ? "[audit] glob BestInDir : le fichier GUID est retrouve malgre l'ancien titre"
                : "[audit] glob BestInDir : ECHEC — fichier GUID INVISIBLE apres renommage differe");
        }
        catch (Exception ex) { Console.WriteLine($"[audit] glob BestInDir : {ex.GetType().Name}"); }
        finally
        {
            MediaResolver.SwapRootForTest(was);
            try { Directory.Delete(tmp, true); } catch { }
        }
    }

    private static void Bump(Dictionary<string, int> d, string k) => d[k] = d.TryGetValue(k, out var v) ? v + 1 : 1;
    private static string Short(string root, string p) => p.StartsWith(root) ? p.Substring(root.Length).TrimStart('\\', '/') : p;

    private sealed record GameRow(Guid Id, string Title, string Platform);

    private static List<GameRow> LoadGames(string platformsDir)
    {
        var list = new List<GameRow>();
        if (!Directory.Exists(platformsDir)) return list;
        foreach (var f in Directory.EnumerateFiles(platformsDir, "*.xml"))
        {
            System.Xml.Linq.XDocument doc;
            try { doc = System.Xml.Linq.XDocument.Load(f); } catch { continue; }
            foreach (var g in doc.Root?.Elements("Game") ?? Enumerable.Empty<System.Xml.Linq.XElement>())
                if (Guid.TryParse((string)g.Element("ID") ?? "", out var id))
                    list.Add(new GameRow(id, (string)g.Element("Title") ?? "", (string)g.Element("Platform") ?? ""));
        }
        return list;
    }
}
