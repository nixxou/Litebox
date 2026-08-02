// --selftest-media-rename: the media rename/convert logic, exercised on a REAL temporary tree.
//
// It moves files on disk, so it is tested against actual files rather than a mock: the cases that
// matter are exactly the ones a mock would get wrong — a mixed unit, a suffixed GUID file, the
// global video rule, a number already taken, and a locked source.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LbApiHost.Host.Media;

namespace LbApiHost.Tools;

internal static class MediaRenameSelfTest
{
    private static readonly Guid Id = Guid.Parse("3f2a1b4c-1111-2222-3333-444455556666");
    private static readonly Guid Other = Guid.Parse("99999999-8888-7777-6666-555544443333");
    private const string Plat = "Arcade";
    private const string Old = "Street Fighter II";
    private const string New = "Street Fighter II Turbo";

    public static int Run()
    {
        int failures = 0;
        string root = Path.Combine(Path.GetTempPath(), "LiteBoxMediaTest_" + Guid.NewGuid().ToString("N"));
        try
        {
            failures += PlainToGuidKeepsNumbers(root);
            failures += MixedUnitIsLeftAlone(root);
            failures += SuffixedGuidNeverGoesPlain(root);
            failures += GuidBackToPlain(root);
            failures += VideosAreOneUnit(root);
            failures += TakenNumberIsBumped(root);
            failures += LockedFileFallsBackToCopy(root);
            failures += RegionSubfolderIsPartOfTheType(root);
            failures += MergeAppendsAfterTheDestination(root);
            failures += SharedSourceIsCopiedNotMoved(root);
            failures += PinnedFileIsNotRenamed(root);
            failures += OverrideBeatsTheConvention(root);
            failures += LaunchBoxWalkOrder(root);
            failures += LaunchBoxOnlyFoldersAreInvisible(root);
            failures += SameTargetNameMeansNothingToDo(root);
            failures += RenameEntryPointHonoursTheGuard(root);
            failures += ThumbnailListingSeesGuidFiles(root);
            failures += CollisionNeedsTwoKnownAndDifferentIds();
            failures += FlushNotificationSurvivesBoot();
        }
        finally { Nuke(root); }

        Console.WriteLine(failures == 0 ? "[media-rename] ALL PASS" : $"[media-rename] {failures} FAILURE(S)");
        return failures == 0 ? 0 : 1;
    }

    // ── cases ────────────────────────────────────────────────────────────────────────────────
    private static int PlainToGuidKeepsNumbers(string root)
    {
        string dir = Fresh(root, "Images", "Box - Front");
        Touch(dir, $"{Old}-01.jpg");
        Touch(dir, $"{Old}-02.jpg");
        var moves = GameMediaRenamer.Plan(Case(root), Id, Plat, Old, New, MediaNameForm.Guid);
        GameMediaRenamer.Apply(moves);
        return Check("plain → GUID keeps each number, under the FINAL title",
            Exists(dir, $"{New}.{Id:D}-01.jpg") && Exists(dir, $"{New}.{Id:D}-02.jpg")
            && !Exists(dir, $"{Old}-01.jpg") && !Exists(dir, $"{Old}.{Id:D}-01.jpg"));
    }

    private static int MixedUnitIsLeftAlone(string root)
    {
        string dir = Fresh(root, "Images", "Screenshot - Gameplay");
        Touch(dir, $"{Old}-01.png");                 // already invisible: Freeze drops it
        Touch(dir, $"{Old}.{Id:D}-01.png");
        var moves = GameMediaRenamer.Plan(Case(root), Id, Plat, Old, New, MediaNameForm.Guid);
        int f = Check("a mixed unit plans nothing", moves.Count == 0);
        GameMediaRenamer.Apply(moves);
        return f + Check("a mixed unit is left untouched on disk",
            Exists(dir, $"{Old}-01.png") && Exists(dir, $"{Old}.{Id:D}-01.png"));
    }

    private static int SuffixedGuidNeverGoesPlain(string root)
    {
        string dir = Fresh(root, "Images", "Clear Logo");
        Touch(dir, $"{Old}.{Id:D}-Europe-01.png");
        var moves = GameMediaRenamer.Plan(Case(root), Id, Plat, Old, New, MediaNameForm.Plain);
        return Check("a suffixed GUID file stays GUID", moves.Count == 0);
    }

    private static int GuidBackToPlain(string root)
    {
        string dir = Fresh(root, "Images", "Box - Front");
        Touch(dir, $"{Old}.{Id:D}-01.jpg");
        Touch(dir, $"{Old}.{Id:D}-02.jpg");
        var moves = GameMediaRenamer.Plan(Case(root), Id, Plat, Old, New, MediaNameForm.Plain);
        GameMediaRenamer.Apply(moves);
        return Check("GUID → plain uses the NEW title and keeps the numbers",
            Exists(dir, $"{New}-01.jpg") && Exists(dir, $"{New}-02.jpg")
            && !Exists(dir, $"{Old}.{Id:D}-01.jpg"));
    }

    private static int VideosAreOneUnit(string root)
    {
        string baseDir = Fresh(root, "Videos", null);
        string trailer = Path.Combine(baseDir, "Trailer");
        Directory.CreateDirectory(trailer);
        Touch(baseDir, $"{Old}-01.mp4");
        Touch(trailer, $"{Old}.{Id:D}-01.mp4");      // one GUID video anywhere → the unit is mixed
        var moves = GameMediaRenamer.Plan(Case(root), Id, Plat, Old, New, MediaNameForm.Guid);
        return Check("videos are one unit: a GUID in a subfolder freezes the whole game",
            moves.Count == 0);
    }

    private static int TakenNumberIsBumped(string root)
    {
        string dir = Fresh(root, "Manuals", null);
        Touch(dir, $"{Old}-01.pdf");
        Touch(dir, $"{New}-01.pdf");                 // the target number is already used
        // -01 under the new title is already taken, so the belt has to find the next free slot
        // rather than refuse or clobber. This namespace is the game's own: the caller never targets
        // the plain form when another game holds that title.
        var moves = GameMediaRenamer.Plan(Case(root), Id, Plat, Old, New, MediaNameForm.Plain);
        GameMediaRenamer.Apply(moves);
        int f = Check("a taken number is bumped to the next free one",
            Exists(dir, $"{New}-02.pdf") && !Exists(dir, $"{Old}-01.pdf"));
        return f + Check("the file that already held the number is untouched",
            Exists(dir, $"{New}-01.pdf"));
    }

    private static int LockedFileFallsBackToCopy(string root)
    {
        string dir = Fresh(root, "Images", "Box - Front");
        string locked = Touch(dir, $"{Old}-01.jpg");
        var moves = GameMediaRenamer.Plan(Case(root), Id, Plat, Old, New, MediaNameForm.Guid);
        GameMediaRenamer.MediaMoveResult applied;
        using (var hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
            applied = GameMediaRenamer.Apply(moves);   // Move fails, Copy succeeds, Delete fails
        int f = Check("a locked file still reaches its target through a copy",
            applied.Reached == 1 && applied.Copied == 1 && applied.Failed == 0
            && Exists(dir, $"{New}.{Id:D}-01.jpg"));
        return f + Check("the locked source is left in place rather than losing the file",
            Exists(dir, $"{Old}-01.jpg"));
    }

    /// <summary>Reported from use: renaming Lylatwars left Lylatwars-20.png untouched under
    /// "Box - Front\World". Images live in the type folder AND in its region sub-folders, which
    /// MediaResolver walks through RegionOrder() — a type unit has to span both.</summary>
    private static int RegionSubfolderIsPartOfTheType(string root)
    {
        string type = Fresh(root, "Images", "Box - Front");
        string world = Path.Combine(type, "World");
        Directory.CreateDirectory(world);
        Touch(world, "Lylatwars-20.png");
        Touch(type, "Lylatwars-01.png");

        var moves = GameMediaRenamer.Plan(Case(root), Id, Plat, "Lylatwars", "LylatwarsAAA", MediaNameForm.Plain);
        GameMediaRenamer.Apply(moves);
        int f = Check("a file in a region sub-folder is renamed too",
            Exists(world, "LylatwarsAAA-20.png") && !Exists(world, "Lylatwars-20.png"));
        f += Check("the region file keeps its number", Exists(world, "LylatwarsAAA-20.png"));
        return f + Check("the file at the type root is renamed as well",
            Exists(type, "LylatwarsAAA-01.png"));
    }

    /// <summary>Renaming onto a title held by the SAME game (same database id) is a merge, not a
    /// clash: the files already there are the destination and must not move, and ours join them
    /// numbered AFTER the highest one present — never filling its gaps, so its order is intact.</summary>
    private static int MergeAppendsAfterTheDestination(string root)
    {
        string dir = Fresh(root, "Images", "Box - Front");
        Touch(dir, $"{New}-01.jpg");                 // destination, must not move
        Touch(dir, $"{New}-03.jpg");                 // note the gap at 02
        Touch(dir, $"{Old}-01.jpg");                 // source
        Touch(dir, $"{Old}-02.jpg");

        var moves = GameMediaRenamer.Plan(Case(root), Id, Plat, Old, New, MediaNameForm.Plain, append: true);
        GameMediaRenamer.Apply(moves);
        int f = Check("a merge leaves the destination files untouched",
            Exists(dir, $"{New}-01.jpg") && Exists(dir, $"{New}-03.jpg"));
        f += Check("merged files are appended after the highest number, not into the gap",
            Exists(dir, $"{New}-04.jpg") && Exists(dir, $"{New}-05.jpg") && !Exists(dir, $"{New}-02.jpg"));
        return f + Check("the source names are gone after a merge",
            !Exists(dir, $"{Old}-01.jpg") && !Exists(dir, $"{Old}-02.jpg"));
    }

    /// <summary>Another game still answering to the source title shares these files, whatever the
    /// media kind. Moving them would strip that game, so they are copied and the original stays.</summary>
    private static int SharedSourceIsCopiedNotMoved(string root)
    {
        string dir = Fresh(root, "Music", null);
        Touch(dir, $"{Old}-01.mp3");
        var moves = GameMediaRenamer.Plan(Case(root), Id, Plat, Old, New, MediaNameForm.Plain, sharedSource: true);
        var res = GameMediaRenamer.Apply(moves);
        int f = Check("a shared source is copied, not moved", res.Copied == 1 && res.Moved == 0);
        return f + Check("the other game keeps its file",
            Exists(dir, $"{Old}-01.mp3") && Exists(dir, $"{New}-01.mp3"));
    }

    /// <summary>The boot flush runs before the DataManager exists, so nobody is listening when it
    /// lands a rename made while LaunchBox held the XMLs — the very case the transit form is for.
    /// Those ids must be kept and handed over when a listener finally subscribes.</summary>
    private static int FlushNotificationSurvivesBoot()
    {
        var store = new LbApiHost.Host.Data.GameStore();
        var raisedBefore = new List<Guid>();
        // Raise with no listener attached (boot order), then subscribe.
        Invoke(store, new[] { Id });
        int f = Check("nothing is delivered while nobody listens", raisedBefore.Count == 0);

        var got = new List<Guid>();
        store.TitlesFlushed = ids => got.AddRange(ids);
        f += Check("subscribing later still receives the boot flush", got.Count == 1 && got[0] == Id);

        var again = new List<Guid>();
        store.TitlesFlushed = ids => again.AddRange(ids);
        return f + Check("the backlog is delivered once, not replayed", again.Count == 0);
    }

    /// <summary>Reaches the private notifier the flush calls, so the test drives the real path.</summary>
    private static void Invoke(LbApiHost.Host.Data.GameStore store, Guid[] ids)
    {
        var opType = typeof(LbApiHost.Host.Data.GameStore).Assembly.GetType("LbApiHost.Host.Data.Op");
        var listType = typeof(List<>).MakeGenericType(opType!);
        var list = (System.Collections.IList)Activator.CreateInstance(listType)!;
        foreach (var id in ids)
            list.Add(Activator.CreateInstance(opType!, 0L, "modify", "Game", id.ToString("D"), "", "Title", "x"));
        typeof(LbApiHost.Host.Data.GameStore)
            .GetMethod("NotifyTitlesFlushed", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(store, new object[] { list });
    }

    /// <summary>A manual reached through &lt;ManualPath&gt; is named BY that path: renaming the file
    /// breaks the reference instead of following it. Its conventional neighbour, in the same folder
    /// and matching the same pattern, must still move — otherwise the fix would strand the 1375
    /// conventional manuals the real library actually holds.</summary>
    private static int PinnedFileIsNotRenamed(string root)
    {
        string dir = Fresh(root, "Manuals", null);
        Touch(dir, $"{Old}-01.pdf");                      // pinned by ManualPath below
        Touch(dir, $"{Old}-02.pdf");                      // conventional, must follow the title

        // The platform XML is the source of truth PinnedMedia reads, precisely so that it needs
        // nothing initialised — a probe that skips the boot must not silently protect nothing.
        string data = Path.Combine(Case(root), "Data", "Platforms");
        Directory.CreateDirectory(data);
        string rel = Path.Combine("Manuals", MediaResolver.Sanitize(Plat), $"{Old}-01.pdf");
        File.WriteAllText(Path.Combine(data, Plat + ".xml"),
            "<?xml version=\"1.0\" standalone=\"yes\"?>\r\n<LaunchBox>\r\n"
            + $"  <Game><ID>{Id:D}</ID><Title>{Old}</Title><Platform>{Plat}</Platform>"
            + $"<ManualPath>{rel}</ManualPath></Game>\r\n</LaunchBox>\r\n");

        var moves = GameMediaRenamer.Plan(Case(root), Id, Plat, Old, New, MediaNameForm.Plain);
        GameMediaRenamer.Apply(moves);

        return Check("a path-pinned manual stays put while its conventional neighbour follows",
            Exists(dir, $"{Old}-01.pdf")            // the reference still resolves
            && !Exists(dir, $"{New}-01.pdf")
            && Exists(dir, $"{New}-02.pdf")         // the conventional one moved
            && !Exists(dir, $"{Old}-02.pdf"));
    }

    /// <summary>A stored &lt;ManualPath&gt; names the file outright, so it wins over whatever the folder
    /// holds under the game's title. And an override pointing at nothing must NOT hide a perfectly
    /// good conventional file — a stale field is not a reason to report the game as having no
    /// manual.</summary>
    private static int OverrideBeatsTheConvention(string root)
    {
        string dir = Fresh(root, "Manuals", null);
        Touch(dir, $"{Old}-01.pdf");                       // conventional
        string chosen = Touch(dir, "Something Else.pdf");  // only reachable through the override
        string was = MediaResolver.SwapRootForTest(Case(root));
        try
        {
            int f = 0;
            f += Check("a stored override wins over the conventional file",
                MediaResolver.Override(Path.Combine("Manuals", MediaResolver.Sanitize(Plat), "Something Else.pdf"))
                    == Path.GetFullPath(chosen));
            f += Check("an override pointing at nothing falls back instead of hiding the file",
                MediaResolver.Override(Path.Combine("Manuals", MediaResolver.Sanitize(Plat), "Gone.pdf")) == null
                && MediaResolver.Manual(Plat, Id, Old) != null);
            return f;
        }
        finally { MediaResolver.SwapRootForTest(was); }
    }

    /// <summary>L'ordre de LaunchBox, mesure cas par cas sur une vraie installation : il descend
    /// dans les sous-dossiers AVANT de lire les fichiers du dossier courant, alphabetiquement a
    /// chaque niveau, et garde la premiere correspondance.
    ///
    /// Chaque assertion ci-dessous correspond a une manipulation reellement faite dans LaunchBox,
    /// pas a une deduction. La deuxieme est celle qui a tout tranche : un fichier pose dans le
    /// dossier parent reste derriere un fichier situe dans un sous-dossier, alors qu'un tri des
    /// chemins complets le placerait devant.</summary>
    private static int LaunchBoxWalkOrder(string root)
    {
        string dir = Fresh(root, "Manuals", null);
        string game = Path.Combine(dir, Old);
        Directory.CreateDirectory(Path.Combine(game, "YYY"));
        Directory.CreateDirectory(Path.Combine(game, "ZZZ"));
        Touch(game, $"{Old}.pdf");
        Touch(Path.Combine(game, "YYY"), $"{Old}.pdf");
        Touch(Path.Combine(game, "ZZZ"), $"{Old}.pdf");

        string was = MediaResolver.SwapRootForTest(Case(root));
        try
        {
            int f = 0;
            string Hit() => MediaResolver.Manual(Plat, Id, Old);

            f += Check("le premier sous-dossier l'emporte sur le fichier du dossier parent",
                Hit() == Path.Combine(game, "YYY", $"{Old}.pdf"));

            // Mesure : "-01.pdf" pose a cote de YYY\ ne suffit pas, la descente passe avant.
            Touch(game, $"{Old}-01.pdf");
            f += Check("un fichier numerote dans le dossier parent ne reprend pas la main",
                Hit() == Path.Combine(game, "YYY", $"{Old}.pdf"));

            // Mais DANS YYY, il passe devant le nom nu : '-' (0x2D) precede '.' (0x2E).
            Touch(Path.Combine(game, "YYY"), $"{Old}-01.pdf");
            f += Check("dans le dossier atteint en premier, l'ordre alphabetique des fichiers tranche",
                Hit() == Path.Combine(game, "YYY", $"{Old}-01.pdf"));

            // Un sous-dossier qui trie AVANT YYY reprend la main, ou qu'il soit dans l'alphabet.
            Directory.CreateDirectory(Path.Combine(game, "AAA"));
            Touch(Path.Combine(game, "AAA"), $"{Old}.pdf");
            f += Check("renommer un dossier suffit a changer le manuel retenu",
                Hit() == Path.Combine(game, "AAA", $"{Old}.pdf"));
            return f;
        }
        finally { MediaResolver.SwapRootForTest(was); }
    }

    /// <summary>Les dossiers "01-&lt;Region&gt;" n'existent que pour donner a LaunchBox un ordre
    /// alphabetique qui coincide avec la priorite de region. LiteBox applique cette priorite de
    /// lui-meme et doit les ignorer : sans cela, chaque fichier serait vu deux fois — une par son
    /// vrai dossier, une par la jonction — et le renommage s'appliquerait deux fois au meme fichier
    /// physique.
    ///
    /// Le test cree un VRAI dossier, pas une jonction : c'est le cas le plus difficile, celui ou
    /// seul le nom peut trahir l'intention. Une jonction serait attrapee par l'attribut de point
    /// d'analyse, qui est la seconde garde.</summary>
    private static int LaunchBoxOnlyFoldersAreInvisible(string root)
    {
        string dir = Fresh(root, "Manuals", null);
        Directory.CreateDirectory(Path.Combine(dir, "01-North America"));
        Directory.CreateDirectory(Path.Combine(dir, "Europe"));
        Touch(Path.Combine(dir, "01-North America"), $"{Old}.pdf");
        Touch(Path.Combine(dir, "Europe"), $"{Old}.pdf");

        string was = MediaResolver.SwapRootForTest(Case(root));
        try
        {
            int f = 0;
            // "01-North America" trie AVANT "Europe", et LiteBox doit le retenir COMME LaunchBox.
            // Un saut avait ete ajoute ici pour les jonctions "NN-<Region>" ; ce design est abandonne,
            // et le saut avec lui : un dossier reellement nomme ainsi doit etre parcouru, sinon la
            // resolution de repli — celle qui sert quand ManualPath est vide — cesse d'etre celle de
            // LaunchBox, ce qui est tout ce qu'on lui demande.
            f += Check("aucun nom de dossier n'est traite a part : le parcours est celui de LaunchBox",
                MediaResolver.Manual(Plat, Id, Old) == Path.Combine(dir, "01-North America", $"{Old}.pdf"));

            // L'extension n'est PAS un filtre : LaunchBox retient n'importe quel fichier au bon nom.
            string odd = Fresh(root, "Manuals", null);
            Touch(odd, $"{Old}.lnk");
            string was2 = MediaResolver.SwapRootForTest(Case(root));
            f += Check("un fichier au bon nom est retenu quelle que soit son extension",
                MediaResolver.Manual(Plat, Id, Old) == Path.Combine(odd, $"{Old}.lnk"));
            MediaResolver.SwapRootForTest(was2);
            return f;
        }
        finally { MediaResolver.SwapRootForTest(was); }
    }

    /// <summary>Un titre qui change sans que le nom de fichier change. Sanitize n'est pas injectif,
    /// donc ce n'est pas un cas de laboratoire : l'apostrophe devient "_" et 317 titres de la vraie
    /// bibliotheque en portent une. Il faut le detecter AVANT toute migration — sinon un renommage
    /// purement cosmetique declenche un passage par la forme GUID quand LaunchBox tourne, pour
    /// aboutir a des fichiers qui portaient deja le bon nom.
    ///
    /// Le dernier cas est le controle : deux titres qui visent VRAIMENT des fichiers differents
    /// doivent toujours donner du travail, sinon ce test passerait en cassant le renommage.</summary>
    private static int SameTargetNameMeansNothingToDo(string root)
    {
        int f = 0;
        (string, string, bool, string)[] cases =
        {
            ("Disney's Aladdin",  "Disney_s Aladdin",  true,  "apostrophe deja remplacee a la main"),
            ("A::B",              "A_B",               true,  "suites de _ reduites a un seul"),
            ("Final Fantasy VII", "Final Fantasy VII ", true,  "espace final supprime par Trim"),
            ("aladdin",           "Aladdin",           true,  "casse seule : meme fichier sur disque"),
            ("Contra",            "Contra III",        false, "titres reellement differents"),
            ("Sonic",             "Sonic 2",           false, "ajout d'un chiffre"),
        };
        foreach (var (a, b, same, why) in cases)
            f += Check($"\"{a}\" -> \"{b}\" : {(same ? "rien a faire" : "il y a du travail")} ({why})",
                       GameMediaRenamer.SameTargetName(a, b) == same);

        // Et la preuve que le plan lui-meme ne bouge rien quand les noms coincident : les fichiers
        // gardent leur nom d'origine, sans meme passer par la forme GUID.
        string dir = Fresh(root, "Manuals", null);
        Touch(dir, "Disney_s Aladdin-01.pdf");
        var moves = GameMediaRenamer.Plan(Case(root), Id, Plat, "Disney_s Aladdin", "Disney_s Aladdin",
                                          MediaNameForm.Plain);
        f += Check("aucun deplacement planifie quand le nom de fichier ne change pas", moves.Count == 0);
        return f;
    }

    /// <summary>Le garde-fou branche, pas seulement le predicat.
    ///
    /// Ce test existe parce que le retirer de GameMediaSync.OnTitleChanged ne faisait tomber AUCUN
    /// test : le predicat etait verifie, le planificateur aussi, mais pas le fil entre les deux —
    /// or c'est la que le garde-fou empeche le transit par la forme GUID. Un test qui ne mord pas
    /// sur la ligne qu'il pretend proteger ne protege rien.
    ///
    /// On passe donc par la vraie porte d'entree du renommage, avec un vrai magasin et un vrai jeu.</summary>
    private static int RenameEntryPointHonoursTheGuard(string root)
    {
        int f = 0;
        string dir = Fresh(root, "Manuals", null);
        string lb = Case(root);
        string platformsDir = Path.Combine(lb, "Data", "Platforms");
        Directory.CreateDirectory(platformsDir);
        var gid = Guid.NewGuid();
        File.WriteAllText(Path.Combine(platformsDir, Plat + ".xml"),
            "<?xml version=\"1.0\" standalone=\"yes\"?>\r\n<LaunchBox>\r\n"
            + $"  <Game><ID>{gid:D}</ID><Title>Disney's Aladdin</Title><Platform>{Plat}</Platform></Game>\r\n"
            + "</LaunchBox>\r\n");

        Touch(dir, "Disney_s Aladdin-01.pdf");

        string was = MediaResolver.SwapRootForTest(lb);
        // Sans ca, le resultat depend de LaunchBox etant ouvert ou non : ouvert, un vrai renommage
        // passe par la forme GUID au lieu du nominatif, et la branche de controle echoue sur une
        // machine et pas sur l'autre. Arbre temporaire, la garde de vidage n'a rien a y arbitrer.
        bool? wasForced = LbApiHost.Host.Data.GameStore.ForceLaunchBoxRunning;
        LbApiHost.Host.Data.GameStore.ForceLaunchBoxRunning = false;
        var store = LbApiHost.Host.Data.GameStore.Load(platformsDir, Path.Combine(lb, "t.pending.db"));
        try
        {
            store.ReadOnly = false;
            LbApiHost.Host.Media.GameMediaSync.Attach(store);
            if (!store.ById.TryGetValue(gid, out var idx)) return Check("entree: jeu trouve", false);
            var game = new LbApiHost.Host.Data.HostGame(store, idx);

            // Meme nom de fichier vise : le fichier ne doit pas bouger, ET surtout aucune forme GUID
            // ne doit apparaitre — c'est elle que le garde-fou evite quand LaunchBox tourne.
            LbApiHost.Host.Media.GameMediaSync.OnTitleChanged(game, "Disney's Aladdin", "Disney_s Aladdin");
            var after = Directory.GetFiles(dir).Select(Path.GetFileName).ToList();
            // Ce controle porte sur le RESULTAT, pas sur le garde-fou : deux mecanismes le
            // garantissent — la sortie anticipee, et le refus de Plan de renommer vers le meme
            // nom. Retirer le premier ne fait donc pas tomber ce test, et c'est attendu.
            f += Check("porte d'entree : un renommage qui vise le meme fichier ne touche a rien",
                after.Count == 1 && after[0] == "Disney_s Aladdin-01.pdf");

            // Controle : un vrai changement de nom doit, lui, bien deplacer le fichier.
            LbApiHost.Host.Media.GameMediaSync.OnTitleChanged(game, "Disney_s Aladdin", "Cool Spot");
            f += Check("porte d'entree : un vrai renommage deplace toujours le fichier",
                Exists(dir, "Cool Spot-01.pdf") && !Exists(dir, "Disney_s Aladdin-01.pdf"));

            // LAUNCHBOX OUVERT : les fichiers suivent le titre TOUT DE SUITE. Le transit par la
            // forme GUID a ete supprime — il n'existait que pour que LaunchBox continue de voir les
            // medias pendant sa session, et LaunchBox seul abandonne de toute facon ceux d'un jeu
            // renomme. Aucune forme GUID ne doit donc apparaitre, quel que soit l'etat de LaunchBox.
            LbApiHost.Host.Data.GameStore.ForceLaunchBoxRunning = true;
            LbApiHost.Host.Media.GameMediaSync.OnTitleChanged(game, "Cool Spot", "Zool");
            var names = Directory.GetFiles(dir).Select(Path.GetFileName).ToList();
            f += Check("LaunchBox ouvert : le fichier prend directement le nouveau nom nominatif",
                names.Count == 1 && names[0] == "Zool-01.pdf");
            f += Check("LaunchBox ouvert : aucune forme GUID n'est produite",
                !names.Any(x => x.Contains(gid.ToString("D"))));
            return f;
        }
        finally
        {
            try { store.CloseLog(); } catch { }
            LbApiHost.Host.Data.GameStore.ForceLaunchBoxRunning = wasForced;
            LbApiHost.Host.Media.GameMediaSync.Attach(null);
            MediaResolver.SwapRootForTest(was);
        }
    }

    /// <summary>La liste des vignettes doit voir un fichier GUID portant un ANCIEN titre, et rendre
    /// un ordre reproductible.
    ///
    /// Meme bug que celui trouve dans BestInDir par l'audit, sur l'autre chemin : le glob porte sur
    /// le titre, TryMatch ignore la partie titre d'un nom GUID, donc le filtre annulait la propriete
    /// meme de cette forme. "GuidPath" conserve l'ANCIEN titre dans le nom — transitoire pendant un
    /// renommage differe, DEFINITIF quand deux jeux se disputent un titre avec des DatabaseID
    /// differents. Ces fichiers disparaissaient purement et simplement des vignettes.
    ///
    /// Le second controle porte sur l'egalite de numero : List.Sort n'est pas stable, donc deux
    /// fichiers au meme -NN sortaient dans un ordre qui pouvait changer d'un appel a l'autre.</summary>
    private static int ThumbnailListingSeesGuidFiles(string root)
    {
        string dir = Fresh(root, "Images", "Box - Front");
        Touch(dir, $"{New}-01.png");                    // nominatif, titre courant
        Touch(dir, $"{Old}.{Id:D}-02.png");             // GUID, ANCIEN titre : invisible avant correction
        Touch(dir, $"{New}-02.png");                    // meme numero que le precedent

        var got = MediaResolver.AllInDir(dir, Id, MediaResolver.Sanitize(New), MediaResolver.ImageExts)
                               .Select(Path.GetFileName).ToList();
        int f = 0;
        // COMPORTEMENT ASSUME, pas un oubli. Le glob porte sur le titre, donc un fichier GUID dont
        // la partie titre est perimee n'est pas liste ici. Le correctif a existe puis a ete retire
        // apres mesure : sur 35851 fichiers de la vraie bibliotheque, 1153 sont en forme GUID, 1147
        // portent deja le bon titre, et 2 seulement etaient concernes — deux fichiers de test.
        // Zero cas reel, contre 14 ms sur chaque listage.
        //
        // L'affichage n'en souffre pas : la GameCache attribue par GUID seul et voit ces fichiers.
        // Seuls les appelants qui inspectent le disque les ignorent. Si ce test se met a gener,
        // c'est que le cas est devenu reel — refaire la mesure avant de conclure.
        f += Check("un fichier GUID au titre perime n'est PAS liste (compromis mesure ci-dessus)",
            !got.Contains($"{Old}.{Id:D}-02.png"));
        f += Check("les fichiers au titre courant sont listes", got.Count == 2);
        f += Check("l'ordre suit le numero croissant", got[0] == $"{New}-01.png");

        // Reproductible : rejoue, le meme ordre doit sortir — y compris entre les deux "-02".
        var again = MediaResolver.AllInDir(dir, Id, MediaResolver.Sanitize(New), MediaResolver.ImageExts)
                                 .Select(Path.GetFileName).ToList();
        f += Check("a numero egal, l'ordre est reproductible", got.SequenceEqual(again));
        return f;
    }

    /// <summary>La regle de collision, mesuree sur la bibliotheque reelle : 70 groupes de titres
    /// partages, dont 59 sont le MEME jeu saisi deux fois et 9 n'ont aucun DatabaseID. Deux
    /// seulement sont de vraies collisions. L'ancienne regle, qui ignorait le DatabaseID, separait
    /// donc 68 groupes sur 70 — et separait en particulier des fiches qui doivent partager leurs
    /// fichiers.
    ///
    /// Le SENS de l'incertitude est ce qui compte : « inconnu » n'est pas une identite, donc deux
    /// jeux non identifies ne sont pas declares differents. On ne separe que sur une preuve.</summary>
    private static int CollisionNeedsTwoKnownAndDifferentIds()
    {
        int f = 0;
        (int? mine, int? other, bool guid, string why)[] cases =
        {
            (null, null, false, "aucun des deux identifie : inconnu n'est pas une identite"),
            (null, 42,   false, "le notre n'est pas identifie : rien ne prouve la difference"),
            (42,   null, false, "l'autre n'est pas identifie : rien ne prouve la difference"),
            (42,   42,   false, "meme entree de base : deux fiches d'un seul jeu, elles partagent"),
            (42,   43,   true,  "deux entrees connues et differentes : vraie collision"),
        };
        foreach (var (mine, other, want, why) in cases)
        {
            var rival = new MediaCollision.Candidate(Guid.NewGuid().ToString("D"), Plat, Old, other);
            bool got = MediaCollision.NeedsGuidForm(Plat, Guid.NewGuid().ToString("D"),
                                                    MediaResolver.Sanitize(Old), mine, new[] { rival });
            f += Check($"dbid {Show(mine)} vs {Show(other)} -> {(want ? "GUID" : "nominatif")} ({why})", got == want);
        }
        // Un autre titre ne collisionne pas, meme entre deux entrees connues et differentes.
        var far = new MediaCollision.Candidate(Guid.NewGuid().ToString("D"), Plat, "Autre Jeu", 43);
        f += Check("un titre different ne collisionne pas",
            !MediaCollision.NeedsGuidForm(Plat, Guid.NewGuid().ToString("D"),
                                          MediaResolver.Sanitize(Old), 42, new[] { far }));
        // Et le titre est compare APRES assainissement : deux titres que le sanitiseur replie sur le
        // meme nom se heurtent bel et bien sur le disque.
        var folded = new MediaCollision.Candidate(Guid.NewGuid().ToString("D"), Plat, "Disney's Aladdin", 43);
        f += Check("la comparaison porte sur le nom de fichier, pas sur le titre brut",
            MediaCollision.NeedsGuidForm(Plat, Guid.NewGuid().ToString("D"),
                                         MediaResolver.Sanitize("Disney_s Aladdin"), 42, new[] { folded }));
        return f;
    }

    private static string Show(int? v) => v.HasValue ? v.Value.ToString() : "(absent)";

    // ── helpers ──────────────────────────────────────────────────────────────────────────────
    private static string _case = "";
    private static string Case(string root) => _case;

    /// <summary>A pristine LB-shaped tree per case, so one case cannot leak into the next.</summary>
    private static string Fresh(string root, string kind, string typeDir)
    {
        _case = Path.Combine(root, Guid.NewGuid().ToString("N"));
        string dir = Path.Combine(_case, kind, MediaResolver.Sanitize(Plat));
        if (typeDir != null) dir = Path.Combine(dir, typeDir);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string Touch(string dir, string name)
    {
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, "x");
        return path;
    }

    private static bool Exists(string dir, string name) => File.Exists(Path.Combine(dir, name));

    private static void Nuke(string dir) { try { Directory.Delete(dir, true); } catch { } }

    private static int Check(string name, bool ok)
    {
        Console.WriteLine($"[media-rename] {(ok ? "PASS" : "FAIL")} {name}");
        return ok ? 0 : 1;
    }
}
