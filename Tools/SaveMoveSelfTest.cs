// The one thing LiteBox deliberately does that LaunchBox does not: keeping save games attached to
// the rom they were made on. Everything else about combine and expand reproduces LaunchBox, so this
// is the piece with no reference implementation to compare against — which is exactly why it needs
// a test of its own rather than a manual probe someone remembers to run.
//
// Each case builds a real platform file, runs the real operations through the real store, and
// checks where the saves ended up. Nothing is stubbed.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using LbApiHost.Host.Data;
using LbApiHost.Host.Games;
using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Tools;

internal static class SaveMoveSelfTest
{
    public static int Run()
    {
        int fail = 0;
        fail += Case("combine puis expand : rien ne se perd", CombineThenExpand);
        fail += Case("expand : la version qui EST la racine ne laisse rien de pendant", RootVersionDetaches);
        fail += Case("make default : les sauvegardes suivent le rom demote", MakeDefaultFollows);
        Console.WriteLine(fail == 0 ? "[savemove] ALL PASS" : $"[savemove] {fail} FAILED");
        return fail == 0 ? 0 : 1;
    }

    private static int Case(string name, Func<string> body)
    {
        string work = Path.Combine(Path.GetTempPath(), "savemove-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(work, "Data", "Platforms"));
            Environment.CurrentDirectory = work;
            string err = body();
            if (err == null) { Console.WriteLine($"[savemove] ok   {name}"); return 0; }
            Console.WriteLine($"[savemove] FAIL {name}: {err}");
            return 1;
        }
        catch (Exception ex) { Console.WriteLine($"[savemove] FAIL {name}: {ex.GetType().Name} {ex.Message}"); return 1; }
        finally { try { Environment.CurrentDirectory = Path.GetTempPath(); Directory.Delete(work, true); } catch { } }
    }

    // ── the fixture ──────────────────────────────────────────────────────

    private static XElement Game(string id, string title, string path) =>
        new("Game",
            new XElement("ID", id), new XElement("Title", title),
            new XElement("ApplicationPath", path), new XElement("Platform", "Test"));

    private static XElement Save(string gameId, string versionId, string file) =>
        new("GameSave",
            new XElement("GameId", gameId),
            versionId == null ? null : new XElement("AdditionalApplicationId", versionId),
            new XElement("SaveGroupId", Guid.NewGuid().ToString("N")),
            new XElement("FilePath", file));

    private static XElement Version(string id, string gameId, string path, int priority) =>
        new("AdditionalApplication",
            new XElement("Id", id), new XElement("GameID", gameId),
            new XElement("ApplicationPath", path), new XElement("Section", "Version"),
            new XElement("Priority", priority.ToString()), new XElement("UseEmulator", "true"));

    private static HostDataManagerXml Open(params object[] rows)
    {
        var doc = new XDocument(new XElement("LaunchBox", rows.Cast<XElement>().Where(x => x != null)));
        string dir = Path.Combine(Environment.CurrentDirectory, "Data", "Platforms");
        LbXml.Save(doc, Path.Combine(dir, "Test.xml"));

        var store = GameStore.Load(dir, Path.Combine(Environment.CurrentDirectory, "ops.db"));
        store.ReadOnly = false;
        var dm = new HostDataManagerXml(store, Path.Combine(Environment.CurrentDirectory, "Data"),
                                        Path.Combine(Environment.CurrentDirectory, "Images")) { ReadOnly = false };
        PluginHelper.DataManager = dm;
        return dm;
    }

    private static List<(string Game, string Version, string File)> Saves(HostDataManagerXml dm)
    {
        var all = new List<(string, string, string)>();
        foreach (var g in dm.GetAllGames())
        {
            if (!Guid.TryParse(g.Id ?? "", out var gid)) continue;
            foreach (var r in dm.Store.GetSubEntities(gid, "GameSave"))
            {
                r.TryGetValue("AdditionalApplicationId", out var v);
                r.TryGetValue("FilePath", out var f);
                all.Add((g.Id, v ?? "", f ?? ""));
            }
        }
        return all;
    }

    // ── the cases ────────────────────────────────────────────────────────

    private const string A = "11111111-1111-1111-1111-111111111111";
    private const string B = "22222222-2222-2222-2222-222222222222";

    private static string CombineThenExpand()
    {
        var dm = Open(Game(A, "Root", "roms/a.iso"), Game(B, "Other", "roms/b.iso"),
                      Save(A, null, "saves/a1.srm"), Save(B, null, "saves/b1.srm"), Save(B, null, "saves/b2.srm"));
        var root = dm.GetGameById(A);
        var other = dm.GetGameById(B);
        if (GameCombiner.Combine(new[] { root, other }, root, dm) != 1) return "le combine n'a pas absorbe le jeu";

        var afterCombine = Saves(dm);
        if (afterCombine.Count != 3) return $"{afterCombine.Count} sauvegardes apres le combine au lieu de 3";
        if (afterCombine.Count(s => s.Version.Length > 0) != 2)
            return "les deux sauvegardes du jeu absorbe ne sont pas rattachees a sa version";

        if (GameCombiner.Expand(root, dm) != 1) return "l'expand n'a pas restaure le jeu";
        var afterExpand = Saves(dm);
        if (afterExpand.Count != 3) return $"{afterExpand.Count} sauvegardes apres l'expand au lieu de 3";
        if (afterExpand.Any(s => s.Version.Length > 0)) return "une sauvegarde reste rattachee a une version disparue";

        var restored = dm.GetAllGames().FirstOrDefault(g => (g.ApplicationPath ?? "").EndsWith("b.iso"));
        if (restored == null) return "le jeu restaure est introuvable";
        if (afterExpand.Count(s => s.Game == restored.Id) != 2)
            return "les deux sauvegardes ne sont pas revenues sur le jeu restaure";
        return null;
    }

    private static string RootVersionDetaches()
    {
        // The root already has a version pointing at its own rom, carrying saves — the shape a
        // combine leaves behind. Expanding must not leave them naming a row that is about to go.
        const string V = "33333333-3333-3333-3333-333333333333";
        var dm = Open(Game(A, "Root", "roms/a.iso"),
                      Version(V, A, "roms/a.iso", 1),
                      Save(A, V, "saves/a1.srm"), Save(A, null, "saves/a2.srm"));
        var root = dm.GetGameById(A);
        GameCombiner.Expand(root, dm);

        var saves = Saves(dm);
        if (saves.Count != 2) return $"{saves.Count} sauvegardes au lieu de 2";
        if (saves.Any(s => s.Version.Length > 0)) return "une sauvegarde nomme encore la version supprimee";
        if (saves.Any(s => s.Game != A)) return "une sauvegarde a quitte la racine";
        return null;
    }

    private static string MakeDefaultFollows()
    {
        // Only the mover is exercised here: the editor's dialog cannot run headless, but the rule it
        // applies is this one call, and this is the rule worth pinning.
        const string V = "44444444-4444-4444-4444-444444444444";
        var dm = Open(Game(A, "Root", "roms/a.iso"),
                      Version(V, A, "roms/a.iso", 1),
                      Save(A, null, "saves/made-on-a.srm"), Save(A, V, "saves/already-tagged.srm"));
        var root = dm.GetGameById(A);
        var demoted = GameCombiner.VersionsOf(root).FirstOrDefault();
        if (demoted == null) return "la version temoin est introuvable";

        GameSaveMover.FollowDemotedRom(root, demoted, dm);
        var saves = Saves(dm);
        if (saves.Count != 2) return $"{saves.Count} sauvegardes au lieu de 2";
        if (saves.Any(s => s.Version != V))
            return "la sauvegarde de niveau jeu n'a pas suivi le rom demote";
        return null;
    }
}
