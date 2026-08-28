// Self-test for the saves of ROMs extracted from an archive — the cases we established for ordinary
// saves, re-asked for an entry.
//
// Everything here runs against a THROWAWAY tree: MediaResolver.SwapRootForTest points the vault at a
// temp folder, and the folder is deleted at the end. Nothing touches a real library, which is the whole
// point of being able to run it unattended.
//
// It deliberately stops where the integration plugins begin. Scanning and restoring go through a plugin
// and an emulator install, which a self-test cannot honestly stand in for; what it covers is the layer
// that is actually NEW for entries — where a copy is written, what it is named, what the padlock does to
// it, and which one retention takes.
//
//   --selftest-entry-saves

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LbApiHost.Generated;
using LbApiHost.Host.Data;
using LbApiHost.Host.Media;
using LbApiHost.Host.Saves;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Tools;

internal static class EntrySaveSelfTest
{
    private static int _pass, _fail;

    private static void Check(string what, bool ok, string? detail = null)
    {
        if (ok) { _pass++; Console.WriteLine($"  ok    {what}"); }
        else { _fail++; Console.WriteLine($"  FAIL  {what}" + (detail == null ? "" : $"\n          {detail}")); }
    }

    private static void Eq(string what, string expected, string actual)
        => Check(what, string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase),
                 $"attendu : {expected}\n          obtenu  : {actual}");

    // ── A game that answers like a LiteBox one, with its records in memory ────
    private sealed class FakeGame : DummyGame, ILiteBoxGame
    {
        private readonly string _id = Guid.NewGuid().ToString("D");
        private readonly Dictionary<string, List<Dictionary<string, string>>> _subs = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _fields = new(StringComparer.OrdinalIgnoreCase);

        public FakeGame(string title, string platform, string appPath)
        { _title = title; _platform = platform; _app = appPath; }

        private readonly string _title, _platform, _app;

        public override string Id => _id;
        public override string Title { get => _title; set { } }
        public override string Platform { get => _platform; set { } }
        public override string ApplicationPath { get => _app; set { } }

        public IReadOnlyCollection<string> SubEntityTypes => _subs.Keys;

        public IReadOnlyList<IReadOnlyDictionary<string, string>> GetSubEntities(string elementType)
            => _subs.TryGetValue(elementType, out var l)
                ? l.Select(d => (IReadOnlyDictionary<string, string>)d).ToList()
                : new List<IReadOnlyDictionary<string, string>>();

        public void SetSubEntities(string elementType, IEnumerable<IReadOnlyDictionary<string, string>> rows)
            => _subs[elementType] = rows.Select(r => new Dictionary<string, string>(r, StringComparer.Ordinal)).ToList();

        public string GetField(string name) => _fields.TryGetValue(name, out var v) ? v : "";
        public void SetField(string name, string value) => _fields[name] = value ?? "";
        public IReadOnlyCollection<string> ExtraFieldNames => _fields.Keys;
    }

    private static SaveEntry Entry(string fileName, string pathInArchive, string sig = "aabbccdd")
        => new() { FileName = fileName, PathInArchive = pathInArchive, ShortSignature = sig, ProbePath = @"C:\tmp\" + fileName, Played = true };

    private static SaveGroup Group(IGame game, SaveEntry? e, bool isState, int? slot = null)
        => new()
        {
            Game = game,
            GameId = game.Id,
            IsState = isState,
            Slot = slot,
            GroupId = e == null ? Guid.NewGuid().ToString("N") : e.Key + (isState ? ":s" + (slot ?? 0) : ""),
            GroupName = isState ? "My Save State" : "My Save File",
            EntryKey = e?.Key,
            EntryLabel = e?.FileName,
            EntryProbePath = e?.ProbePath,
        };

    public static int Run(string[] args)
    {
        string temp = Path.Combine(Path.GetTempPath(), "litebox-entrysaves-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        string? previousRoot = null;
        try
        {
            previousRoot = MediaResolver.SwapRootForTest(temp);
            Console.WriteLine("racine de test : " + temp);
            Console.WriteLine();

            Layout(temp);
            Naming(temp);
            VaultBoundary(temp);
            Manifests(temp);
            Padlock(temp);
            Retention(temp);
            ArchiveManifest(temp);
            KeyRoundTrip();
            DefaultNames();

            Console.WriteLine();
            Console.WriteLine($"=== {_pass} ok, {_fail} echec(s) ===");
            return _fail == 0 ? 0 : 1;
        }
        finally
        {
            if (previousRoot != null) MediaResolver.SwapRootForTest(previousRoot);
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
    }

    // 1. Où une copie atterrit ────────────────────────────────────────────────
    private static void Layout(string temp)
    {
        Console.WriteLine("-- emplacement --");
        var game = new FakeGame("Sonic Collection", "Sega Genesis", @"C:\roms\Sonic Collection.zip");
        var plain = Group(game, null, isState: false);
        var entry = Group(game, Entry("Sonic (USA).smd", "Sonic (USA).smd"), isState: false);

        string platDir = Path.Combine(temp, "Saves", "Sega Genesis");
        Eq("une save ordinaire reste dans le dossier de plateforme", platDir, SaveVault.GroupDir(plain));
        Eq("une save d'entree va dans un sous-dossier nomme d'apres l'ARCHIVE",
           Path.Combine(platDir, "Sonic Collection.zip"), SaveVault.GroupDir(entry));
        Check("le nom du sous-dossier est celui du fichier d'archive, extension comprise",
              SaveVault.ArchiveFolderName(entry) == "Sonic Collection.zip");
        Check("une save ordinaire n'a pas de sous-dossier d'archive",
              SaveVault.ArchiveFolderName(plain) == null);
    }

    // 2. Comment une copie s'appelle ──────────────────────────────────────────
    private static void Naming(string temp)
    {
        Console.WriteLine("-- nommage --");
        var game = new FakeGame("Sonic Collection", "Sega Genesis", @"C:\roms\Sonic Collection.zip");
        var a = Group(game, Entry("Sonic (USA).smd", "Sonic (USA).smd"), isState: false);
        var b = Group(game, Entry("Sonic (Japan).smd", "Sonic (Japan).smd"), isState: false);
        var plain = Group(game, null, isState: false);

        Eq("une copie d'entree porte le nom de l'ENTREE", "Sonic (USA)", SaveVault.BaseName(a));
        Eq("une copie ordinaire porte le nom de la ROM", "Sonic Collection", SaveVault.BaseName(plain));
        Check("deux entrees d'une meme archive ne se collisionnent pas",
              !string.Equals(SaveVault.BaseName(a), SaveVault.BaseName(b), StringComparison.OrdinalIgnoreCase),
              $"{SaveVault.BaseName(a)} vs {SaveVault.BaseName(b)}");

        // Le nom survit a une archive dont l'entree a disparu : il reste lisible dans la cle.
        var orphan = Group(game, null, isState: false);
        orphan.EntryKey = "entry:aabbccdd:sub/Sonic (Europe).smd";
        orphan.EntryLabel = null;
        Eq("le nom se relit dans la cle quand l'entree n'est plus dans l'archive",
           "Sonic (Europe)", SaveVault.BaseName(orphan));

        // Caracteres interdits sous Windows, presents dans de vrais noms de ROM.
        var dirty = new FakeGame("X", "Sega Genesis", @"C:\roms\Yoshi's: Island.zip");
        var dg = Group(dirty, Entry("A:B*C.smd", "A:B*C.smd"), isState: false);
        string folder = SaveVault.ArchiveFolderName(dg) ?? "";
        Check("le nom de dossier est assaini",
              folder.Length > 0 && folder.IndexOfAny(Path.GetInvalidFileNameChars()) < 0, folder);
        Check("le nom de fichier est assaini",
              SaveVault.BaseName(dg).IndexOfAny(Path.GetInvalidFileNameChars()) < 0, SaveVault.BaseName(dg));
    }

    // 3. Le sous-dossier reste DANS le vault ──────────────────────────────────
    private static void VaultBoundary(string temp)
    {
        Console.WriteLine("-- frontiere du vault --");
        string inside = Path.Combine(temp, "Saves", "Sega Genesis", "Sonic Collection.zip", "Sonic (USA).srm");
        string outside = Path.Combine(temp, "Emulators", "RetroArch", "saves", "Sonic (USA).srm");
        Check("un fichier du sous-dossier d'archive est reconnu comme copie", SaveVault.IsUnderVault(inside));
        Check("une save vivante ne l'est pas", !SaveVault.IsUnderVault(outside));
    }

    // 4. Les deux manifestes sortent des empreintes et des tailles ────────────
    private static void Manifests(string temp)
    {
        Console.WriteLine("-- manifestes exclus --");
        string dir = Path.Combine(temp, "folder-save");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "game.dat"), new byte[100]);
        File.WriteAllBytes(Path.Combine(dir, "banner.bin"), new byte[50]);

        string hashBefore = SaveManager.DirManifestMd5(dir);
        long sizeBefore = SaveVault.DirContentSize(dir);

        File.WriteAllText(Path.Combine(dir, SaveManager.DirManifestName), "peu importe");
        File.WriteAllText(Path.Combine(dir, SaveVault.ArchiveManifestName), "<x/>");

        Eq("l'empreinte ignore les deux manifestes", hashBefore, SaveManager.DirManifestMd5(dir));
        Check("la taille ignore les deux manifestes", sizeBefore == SaveVault.DirContentSize(dir),
              $"{sizeBefore} puis {SaveVault.DirContentSize(dir)}");
        Check("la taille d'un dossier n'est pas zero", sizeBefore == 150, sizeBefore.ToString());
    }

    // 5. Le cadenas, sur un fichier ET sur un dossier ─────────────────────────
    private static void Padlock(string temp)
    {
        Console.WriteLine("-- cadenas --");
        string file = Path.Combine(temp, "locked.srm");
        File.WriteAllBytes(file, new byte[10]);
        string dir = Path.Combine(temp, "locked-folder");
        Directory.CreateDirectory(dir);

        foreach (var (what, path) in new[] { ("fichier", file), ("dossier", dir) })
        {
            var before = Directory.Exists(path) ? Directory.GetCreationTimeUtc(path) : File.GetCreationTimeUtc(path);
            Check($"{what} : non verrouille au depart", !SaveVault.IsLockedPath(path));

            Check($"{what} : le verrouillage reussit", SaveVault.SetLocked(path, true) == null);
            Check($"{what} : reconnu comme verrouille", SaveVault.IsLockedPath(path));

            var after = Directory.Exists(path) ? Directory.GetCreationTimeUtc(path) : File.GetCreationTimeUtc(path);
            Check($"{what} : la date de creation est un siecle plus loin",
                  Math.Abs((after - before).TotalDays - 36524) < 3, $"{before:u} -> {after:u}");

            Check($"{what} : verrouiller deux fois ne decale pas deux fois", SaveVault.SetLocked(path, true) == null);
            var twice = Directory.Exists(path) ? Directory.GetCreationTimeUtc(path) : File.GetCreationTimeUtc(path);
            Check($"{what} : la date n'a pas bouge au second appel", twice == after);

            Check($"{what} : le deverrouillage reussit", SaveVault.SetLocked(path, false) == null);
            Check($"{what} : rendu a son etat", !SaveVault.IsLockedPath(path));
            var back = Directory.Exists(path) ? Directory.GetCreationTimeUtc(path) : File.GetCreationTimeUtc(path);
            Check($"{what} : la date d'origine est retrouvee", Math.Abs((back - before).TotalSeconds) < 2,
                  $"{before:u} -> {back:u}");
        }

        Check("verrouiller ce qui n'existe pas renvoie une erreur",
              SaveVault.SetLocked(Path.Combine(temp, "nulle-part.srm"), true) != null);

        // La date AFFICHEE d'une copie verrouillee est la vraie.
        var e = new VaultEntry { CreatedUtc = new DateTime(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc), Locked = false };
        Check("copie ordinaire : date affichee = date stockee", e.DisplayCreatedUtc == e.CreatedUtc);
        var locked = new VaultEntry { CreatedUtc = new DateTime(2126, 8, 28, 12, 0, 0, DateTimeKind.Utc), Locked = true };
        Check("copie verrouillee : le siecle est retire a l'affichage",
              locked.DisplayCreatedUtc.Year == 2026, locked.DisplayCreatedUtc.ToString("u"));
    }

    // 6. La retention : par date de creation, et le cadenas la tient en echec ─
    private static void Retention(string temp)
    {
        Console.WriteLine("-- retention --");
        var game = new FakeGame("Sonic Collection", "Sega Genesis", @"C:\roms\Sonic Collection.zip");
        var entry = Entry("Sonic (USA).smd", "Sonic (USA).smd");
        var g = Group(game, entry, isState: false);

        string dir = SaveVault.GroupDir(g);
        Directory.CreateDirectory(dir);

        // Quatre copies, creations echelonnees d'une heure. La plus ancienne en premier.
        var now = DateTime.UtcNow;
        var made = new List<(string name, string path)>();
        for (int i = 0; i < 4; i++)
        {
            string name = i == 0 ? "Sonic (USA).srm" : $"Sonic (USA)-{i:00}.srm";
            string path = Path.Combine(dir, name);
            File.WriteAllBytes(path, new byte[] { (byte)i });
            File.SetCreationTimeUtc(path, now.AddHours(-10 + i));
            made.Add((name, path));
            g.Backups.Add(new VaultEntry
            {
                GameId = game.Id, GroupId = g.GroupId, GroupName = g.GroupName,
                VaultPath = SaveVault.Rel(path), CreatedUtc = File.GetCreationTimeUtc(path),
                Ordinal = 3 - i,                       // ordre des records INVERSE, pour qu'il ne puisse pas expliquer le resultat
                Locked = false,
            });
        }

        var scan = new SaveScan();
        scan.Files.Add(g);
        SaveBackupService.Prune(game, scan, cap: 2);

        Check("la copie la plus ANCIENNE est supprimee", !File.Exists(made[0].path));
        Check("la deuxieme plus ancienne aussi", !File.Exists(made[1].path));
        Check("les deux plus recentes survivent", File.Exists(made[2].path) && File.Exists(made[3].path));
        Check("l'ordre des records n'y est pour rien (il etait inverse)", !File.Exists(made[0].path));

        // Meme scenario, mais la plus ancienne est verrouillee.
        foreach (var (_, path) in made) { try { File.Delete(path); } catch { } }
        g.Backups.Clear();
        made.Clear();
        for (int i = 0; i < 4; i++)
        {
            string path = Path.Combine(dir, $"Sonic (USA)-{i + 10:00}.srm");
            File.WriteAllBytes(path, new byte[] { (byte)i });
            File.SetCreationTimeUtc(path, now.AddHours(-10 + i));
            made.Add(("", path));
        }
        SaveVault.SetLocked(made[0].path, true);
        for (int i = 0; i < 4; i++)
            g.Backups.Add(new VaultEntry
            {
                GameId = game.Id, GroupId = g.GroupId, GroupName = g.GroupName,
                VaultPath = SaveVault.Rel(made[i].path),
                CreatedUtc = File.GetCreationTimeUtc(made[i].path),
                Locked = SaveVault.IsLockedPath(made[i].path),
            });

        var scan2 = new SaveScan();
        scan2.Files.Add(g);
        SaveBackupService.Prune(game, scan2, cap: 2);

        Check("une copie verrouillee survit a la purge", File.Exists(made[0].path));
        Check("c'est la plus ancienne NON verrouillee qui part a sa place", !File.Exists(made[1].path));

        // Tout verrouille : le plafond cede plutot que le cadenas.
        foreach (var (_, path) in made) if (File.Exists(path)) SaveVault.SetLocked(path, true);
        g.Backups.Clear();
        foreach (var (_, path) in made.Where(m => File.Exists(m.path)))
            g.Backups.Add(new VaultEntry
            {
                GameId = game.Id, GroupId = g.GroupId, GroupName = g.GroupName,
                VaultPath = SaveVault.Rel(path), CreatedUtc = File.GetCreationTimeUtc(path), Locked = true,
            });
        int before = g.Backups.Count;
        var scan3 = new SaveScan();
        scan3.Files.Add(g);
        SaveBackupService.Prune(game, scan3, cap: 1);
        Check("quand tout est verrouille, le plafond cede et rien n'est supprime",
              g.Backups.Count == before && made.Where(m => File.Exists(m.path)).Count() == before);
    }

    // 7. Le manifeste d'archive ───────────────────────────────────────────────
    private static void ArchiveManifest(string temp)
    {
        Console.WriteLine("-- manifeste d'archive --");
        // Sa propre archive, donc son propre dossier : la retention ci-dessus laisse des survivants, et
        // deux tests qui partagent un dossier ne mesurent plus ce qu'ils croient mesurer.
        var game = new FakeGame("Manifest Only", "Sega Genesis", @"C:\roms\Manifest Only.zip");
        var entry = Entry("Sonic (USA).smd", "disc1/Sonic (USA).smd");
        var g = Group(game, entry, isState: false);
        string dir = SaveVault.GroupDir(g);
        Directory.CreateDirectory(dir);

        string copy = Path.Combine(dir, "Sonic (USA).srm");
        File.WriteAllBytes(copy, new byte[8]);

        game.SetSubEntities("GameSave", new[]
        {
            new Dictionary<string, string>
            {
                ["GameId"] = game.Id, ["SaveGroupId"] = g.GroupId, ["SaveGroupName"] = "My Save File",
                ["Title"] = "Saved Game", ["FilePath"] = SaveVault.Rel(copy),
                ["OriginalFileName"] = "Sonic (USA).srm",
            },
        });

        SaveVault.WriteArchiveManifest(dir, game);
        string man = Path.Combine(dir, SaveVault.ArchiveManifestName);
        Check("le manifeste est ecrit", File.Exists(man));
        if (!File.Exists(man)) return;

        string xml = File.ReadAllText(man);
        Check("il est bien forme", TryParse(xml), xml.Length > 300 ? xml[..300] : xml);
        Check("il nomme l'archive", xml.Contains("Manifest Only.zip", StringComparison.OrdinalIgnoreCase));
        Check("il nomme l'entree dans l'archive", xml.Contains("disc1/Sonic (USA).smd", StringComparison.Ordinal));
        Check("il nomme le fichier de la copie", xml.Contains("Sonic (USA).srm", StringComparison.Ordinal));
        Check("il ne se compte pas lui-meme dans la taille du dossier",
              SaveVault.DirContentSize(dir) == 8, SaveVault.DirContentSize(dir).ToString());
    }

    private static bool TryParse(string xml)
    { try { System.Xml.Linq.XDocument.Parse(xml); return true; } catch { return false; } }

    // 9. Le nom par defaut d'un groupe ────────────────────────────────────────
    private static void DefaultNames()
    {
        Console.WriteLine("-- noms de groupe par defaut --");

        Eq("une save ordinaire garde le nom de LaunchBox", "My Save File",
           SaveManager.DefaultGroupName(false, Guid.NewGuid().ToString("N")));
        Eq("un savestate ordinaire aussi", "My Save State",
           SaveManager.DefaultGroupName(true, Guid.NewGuid().ToString("N")));

        var e = Entry("Sonic (USA).smd", "Sonic (USA).smd");
        Eq("une save d'entree nomme la ROM", "Sonic (USA) — My Save File",
           SaveManager.DefaultGroupName(false, e.Key));
        Eq("un savestate d'entree aussi, slot compris dans la cle", "Sonic (USA) — My Save State",
           SaveManager.DefaultGroupName(true, e.Key + ":s3"));

        var deep = Entry("Sonic (Japan).smd", "disc1/roms/Sonic (Japan).smd");
        Eq("le chemin dans l'archive est reduit au nom de fichier", "Sonic (Japan) — My Save File",
           SaveManager.DefaultGroupName(false, deep.Key));

        Console.WriteLine();
        Console.WriteLine("     exemples, une archive a trois ROMs :");
        foreach (var name in new[] { "Sonic (USA).smd", "Sonic (Japan).smd", "Sonic (Europe).smd" })
        {
            var x = Entry(name, name);
            Console.WriteLine($"       {SaveManager.DefaultGroupName(false, x.Key)}");
            Console.WriteLine($"       {SaveManager.DefaultGroupName(true, x.Key + ":s0")}");
        }
        Console.WriteLine();
    }

    // 8. L'identite d'entree fait l'aller-retour ──────────────────────────────
    private static void KeyRoundTrip()
    {
        Console.WriteLine("-- identite d'entree --");
        var e = Entry("Sonic (USA).smd", "disc1/Sonic (USA).smd");
        Eq("la cle est content-keyed", "entry:aabbccdd:disc1/Sonic (USA).smd", e.Key);
        Eq("un savestate suffixe son slot sans perdre la cle", e.Key, SaveManager.EntryKeyOf(e.Key + ":s3") ?? "");
        Eq("une save de jeu garde la cle telle quelle", e.Key, SaveManager.EntryKeyOf(e.Key) ?? "");
        Check("un SaveGroupId ordinaire n'a pas de cle d'entree",
              SaveManager.EntryKeyOf(Guid.NewGuid().ToString("N")) == null);
        Check("un SaveGroupId vide non plus", SaveManager.EntryKeyOf("") == null);
    }
}
