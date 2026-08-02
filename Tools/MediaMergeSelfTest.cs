// The media merge, on a real tree with real files — nothing stubbed, because every rule here is
// about what is actually on disk.
//
// The three skip reasons are deliberately tested apart from each other: they look alike from a
// distance and are not the same test at all. A file the two games share, a file whose bytes are
// already in the destination under a different type, and a picture that merely LOOKS like one
// already there each have to be caught by their own rule, and a test that could not tell them apart
// would pass while the code got them backwards.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using LbApiHost.Host.Media;
using LbApiHost.Host.Media.Dedup;

namespace LbApiHost.Tools;

internal static class MediaMergeSelfTest
{
    public static int Run()
    {
        int fail = 0;
        fail += Case("un fichier partage n'est pas deplace", SharedFile);
        fail += Case("un contenu deja present ailleurs n'est pas deplace", AlreadyElsewhere);
        fail += Case("une image trop ressemblante n'est pas deplacee", TooSimilar);
        fail += Case("une video ressemblante EST deplacee (pas de test visuel)", VideoIgnoresSimilarity);
        fail += Case("le reste est bien deplace, numerotation continuee", MovesTheRest);
        fail += Case("la ressemblance ne franchit PAS les categories", SimilarityStaysInItsFolder);
        fail += Case("un media partage est copie, pas deplace", SharedSourceCopies);
        fail += Case("meme titre : les fichiers GUID sont bien deplaces", SameTitleGuidFiles);
        fail += Case("meme titre : les fichiers nominatifs sont laisses", SameTitlePlainUntouched);
        fail += Case("la forme de la destination est respectee", DestFormWins);
        fail += Case("meme numero dans deux regions : les deux sont conserves", NumbersArePerFolder);
        fail += Case("un media GUID reste visible apres un renommage differe", GuidSurvivesDeferredRename);
        fail += Case("un manuel au nom NU est fusionne comme les autres", BareManualIsMerged);
        fail += Case("un manuel nu de la destination compte dans l'inventaire CRC", BareDestFeedsTheCrcSet);
        fail += Case("orphelinage : les nominatifs d'un titre encore porte survivent", OrphansSpareTheAnsweredTitle);
        Console.WriteLine(fail == 0 ? "[mediamerge] ALL PASS" : $"[mediamerge] {fail} FAILED");
        return fail == 0 ? 0 : 1;
    }

    private static int Case(string name, Func<string, string> body)
    {
        string root = Path.Combine(Path.GetTempPath(), "mediamerge-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            string err = body(root);
            if (err == null) { Console.WriteLine($"[mediamerge] ok   {name}"); return 0; }
            Console.WriteLine($"[mediamerge] FAIL {name}: {err}");
            return 1;
        }
        catch (Exception ex) { Console.WriteLine($"[mediamerge] FAIL {name}: {ex.GetType().Name} {ex.Message}"); return 1; }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private const string Plat = "Test Platform";
    private const string A = "Alpha Game";      // source
    private const string B = "Beta Game";       // destination

    private static string Dir(string root, params string[] parts)
    {
        string d = Path.Combine(new[] { root }.Concat(parts).ToArray());
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>A solid-colour picture. Two colours far apart are two different pictures to any
    /// perceptual hash; two shades of the same are the "looks alike" case.</summary>
    private static void Picture(string path, Color c, int size = 64)
    {
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp)) g.Clear(c);
        bmp.Save(path, ImageFormat.Png);
    }

    private static void Noise(string path, int seed, int size = 64)
    {
        var rnd = new Random(seed);
        using var bmp = new Bitmap(size, size);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                bmp.SetPixel(x, y, Color.FromArgb(rnd.Next(256), rnd.Next(256), rnd.Next(256)));
        bmp.Save(path, ImageFormat.Png);
    }

    private static MergePlan Plan(string root) =>
        GameMediaMerge.Plan(root, Plat, A, B, DupEngineMode.DHash,
                            DedupEngine.DefaultThreshold(DupEngineMode.DHash));

    // ── the cases ────────────────────────────────────────────────────────

    private static string BareManualIsMerged(string root)
    {
        // "<titre>.pdf" est une forme legitime pour un manuel — mesuree sur LaunchBox — et l'unite
        // des manuels est "plate". FilesOf l'ignorait : le fichier n'etait ni deplace en
        // consolidation, ni orphelin quand les DatabaseID different — il restait derriere en
        // silence, invisible des deux cotes puisque le jeu absorbe n'existe plus.
        string man = Dir(root, "Manuals", Plat);
        File.WriteAllText(Path.Combine(man, A + ".pdf"), "contenu du manuel nu");

        var plan = Plan(root);
        var item = plan.Items.FirstOrDefault(i => i.From.EndsWith(A + ".pdf"));
        if (item == null) return "le manuel nu n'a pas ete examine du tout";
        if (item.Verdict != MergeVerdict.Move) return $"attendu Move, obtenu {item.Verdict}";
        // Il rejoint une collection : il recoit un numero, dans la forme de la destination.
        if (!item.To.EndsWith(B + "-01.pdf")) return $"destination inattendue : {item.To}";
        return null;
    }

    private static string BareDestFeedsTheCrcSet(string root)
    {
        // L'inventaire octet-identique de la destination doit voir ses fichiers nus, sinon un
        // doublon entrant passe le filtre et la collection gagne deux fois le meme contenu.
        string man = Dir(root, "Manuals", Plat);
        File.WriteAllText(Path.Combine(man, B + ".pdf"), "le meme contenu exactement");
        string sub = Dir(root, "Manuals", Plat, "World");
        File.WriteAllText(Path.Combine(sub, A + "-01.pdf"), "le meme contenu exactement");

        var plan = Plan(root);
        var item = plan.Items.FirstOrDefault(i => i.From.EndsWith(A + "-01.pdf"));
        if (item == null) return "le manuel source n'a pas ete examine";
        if (item.Verdict != MergeVerdict.AlreadyThere)
            return $"attendu AlreadyThere (le nu de la destination porte ces octets), obtenu {item.Verdict}";
        return null;
    }

    private static string SharedFile(string root)
    {
        // Both titles sanitize to something different, but the SAME file is listed for both because
        // the destination's own name is what is on disk. Contrived on purpose: this is the shape a
        // shared-title library takes.
        string box = Dir(root, "Images", Plat, "Box - Front");
        Picture(Path.Combine(box, A + "-01.png"), Color.Red);
        File.Copy(Path.Combine(box, A + "-01.png"), Path.Combine(box, B + "-01.png"));

        var plan = Plan(root);
        // A.png is not B.png, so it is not "the same file" — but its bytes are, so it must be caught
        // as already there. What must NOT happen is a move.
        var item = plan.Items.FirstOrDefault(i => i.From.EndsWith(A + "-01.png"));
        if (item == null) return "le fichier source n'a pas ete examine";
        if (item.Verdict != MergeVerdict.AlreadyThere)
            return $"attendu AlreadyThere (memes octets, chemins differents), obtenu {item.Verdict}";
        return null;
    }

    private static string AlreadyElsewhere(string root)
    {
        // The destination holds these bytes under ANOTHER type. Filing is irrelevant.
        string front = Dir(root, "Images", Plat, "Box - Front");
        string back = Dir(root, "Images", Plat, "Box - Back");
        Picture(Path.Combine(front, A + "-01.png"), Color.Lime);
        File.Copy(Path.Combine(front, A + "-01.png"), Path.Combine(back, B + "-01.png"));

        var plan = Plan(root);
        var item = plan.Items.FirstOrDefault(i => i.From.EndsWith(A + "-01.png"));
        if (item == null) return "le fichier source n'a pas ete examine";
        if (item.Verdict != MergeVerdict.AlreadyThere)
            return $"attendu AlreadyThere, obtenu {item.Verdict}";
        return null;
    }

    private static string TooSimilar(string root)
    {
        if (!DedupEngine.IsAvailable(DupEngineMode.DHash))
        { Console.WriteLine("[mediamerge]      (moteur dhash indisponible — cas non couvert)"); return null; }

        // Same picture re-encoded at another size: different bytes, same image.
        string front = Dir(root, "Images", Plat, "Box - Front");
        Picture(Path.Combine(front, A + "-01.png"), Color.FromArgb(40, 90, 200), 64);
        Picture(Path.Combine(front, B + "-01.png"), Color.FromArgb(40, 90, 200), 128);

        var plan = Plan(root);
        var item = plan.Items.FirstOrDefault(i => i.From.EndsWith(A + "-01.png"));
        if (item == null) return "le fichier source n'a pas ete examine";
        if (item.Verdict == MergeVerdict.AlreadyThere) return "attrape par le CRC, le cas visuel n'est pas teste";
        if (item.Verdict != MergeVerdict.TooSimilar) return $"attendu TooSimilar, obtenu {item.Verdict}";
        return null;
    }

    private static string VideoIgnoresSimilarity(string root)
    {
        // Same idea, but under Videos with a video extension: no visual test applies, and different
        // bytes mean it comes over.
        string vid = Dir(root, "Videos", Plat);
        File.WriteAllBytes(Path.Combine(vid, A + "-01.mp4"), new byte[] { 1, 2, 3, 4 });
        File.WriteAllBytes(Path.Combine(vid, B + "-01.mp4"), new byte[] { 5, 6, 7, 8 });

        var plan = Plan(root);
        var item = plan.Items.FirstOrDefault(i => i.From.EndsWith(A + "-01.mp4"));
        if (item == null) return "la video source n'a pas ete examinee";
        if (!item.Moves) return $"la video n'est pas deplacee (verdict {item.Verdict})";
        return null;
    }

    private static string SimilarityStaysInItsFolder(string root)
    {
        if (!DedupEngine.IsAvailable(DupEngineMode.DHash))
        { Console.WriteLine("[mediamerge]      (moteur dhash indisponible — cas non couvert)"); return null; }

        // La meme image, mais rangee sous un AUTRE type chez la destination. Elle ne doit pas
        // servir de reference : une jaquette qui ressemble a une capture n'en est pas un doublon.
        // Deux tailles differentes pour que le CRC ne tranche pas a la place du test visuel.
        string front = Dir(root, "Images", Plat, "Box - Front");
        string shot = Dir(root, "Images", Plat, "Screenshot");
        Picture(Path.Combine(front, A + "-01.png"), Color.FromArgb(200, 60, 30), 64);
        Picture(Path.Combine(shot, B + "-01.png"), Color.FromArgb(200, 60, 30), 128);

        var plan = Plan(root);
        var item = plan.Items.FirstOrDefault(i => i.From.EndsWith(A + "-01.png"));
        if (item == null) return "le fichier source n'a pas ete examine";
        if (item.Verdict == MergeVerdict.TooSimilar)
            return "ecarte a cause d'une image d'une AUTRE categorie";
        if (!item.Moves) return $"non deplace (verdict {item.Verdict})";
        return null;
    }

    private static string SharedSourceCopies(string root)
    {
        // Un troisieme jeu porte le meme titre que la source : ses medias sont aussi les siens,
        // donc on copie au lieu de deplacer.
        string front = Dir(root, "Images", Plat, "Box - Front");
        Noise(Path.Combine(front, A + "-01.png"), 7);

        var plan = Plan(root);
        if (plan.Moving != 1) return $"{plan.Moving} fichier a deplacer au lieu de 1";
        var res = GameMediaMerge.Apply(plan, root, Guid.NewGuid(), Plat, A, B, sharedSource: true);
        if (res.Copied != 1) return $"copie={res.Copied} au lieu de 1 ({res})";
        if (!File.Exists(Path.Combine(front, A + "-01.png"))) return "la source a ete supprimee malgre le partage";
        if (!File.Exists(Path.Combine(front, B + "-01.png"))) return "la destination n'a pas recu le fichier";
        return null;
    }

    private static readonly Guid SrcId = new("aaaaaaaa-1111-2222-3333-444444444444");
    private static readonly Guid DstId = new("bbbbbbbb-1111-2222-3333-444444444444");

    private static MergePlan PlanIds(string root, string srcTitle, string dstTitle) =>
        GameMediaMerge.Plan(root, Plat, SrcId, srcTitle, DstId, dstTitle,
                            DupEngineMode.DHash, DedupEngine.DefaultThreshold(DupEngineMode.DHash));

    private static string SameTitleGuidFiles(string root)
    {
        // Deux jeux du MEME titre : c'est precisement pourquoi le format GUID existe. Les fichiers
        // GUID de la source lui appartiennent en propre et doivent suivre.
        string front = Dir(root, "Images", Plat, "Box - Front");
        Noise(Path.Combine(front, $"{A}.{SrcId:D}-01.png"), 11);
        Noise(Path.Combine(front, $"{A}.{DstId:D}-01.png"), 12);

        var plan = PlanIds(root, A, A);
        var item = plan.Items.FirstOrDefault(i => i.From.Contains(SrcId.ToString("D")));
        if (item == null) return "le fichier GUID de la source n'a pas ete examine";
        if (!item.Moves) return $"non deplace (verdict {item.Verdict})";
        if (!item.To.Contains(DstId.ToString("D")))
            return $"cible {Path.GetFileName(item.To)} : ne porte pas le GUID de la destination";
        return null;
    }

    private static string SameTitlePlainUntouched(string root)
    {
        // Meme titre : les fichiers NOMINATIFS sont deja communs aux deux jeux. Les renommer
        // reviendrait a renommer ceux de la destination.
        string front = Dir(root, "Images", Plat, "Box - Front");
        Noise(Path.Combine(front, A + "-01.png"), 13);

        var plan = PlanIds(root, A, A);
        var item = plan.Items.FirstOrDefault(i => i.From.EndsWith(A + "-01.png"));
        if (item == null) return "le fichier nominatif n'a pas ete examine";
        // Le verdict est verifie, pas seulement l'absence de deplacement : trois regles peuvent
        // aboutir au meme "non deplace", et un test qui ne les distingue pas laisserait passer
        // du code qui les a interverties.
        if (item.Verdict != MergeVerdict.SameFile)
            return $"attendu SameFile (c'est litteralement le meme fichier), obtenu {item.Verdict}";
        return null;
    }

    private static string DestFormWins(string root)
    {
        // La destination n'utilise QUE le format nominatif ici : un fichier GUID qui arrive doit
        // devenir nominatif, sinon sa presence masquerait les fichiers nominatifs de la destination.
        string front = Dir(root, "Images", Plat, "Box - Front");
        Noise(Path.Combine(front, $"{A}.{SrcId:D}-01.png"), 21);
        Noise(Path.Combine(front, B + "-01.png"), 22);

        var plan = PlanIds(root, A, B);
        var item = plan.Items.FirstOrDefault(i => i.From.Contains(SrcId.ToString("D")));
        if (item == null) return "le fichier GUID n'a pas ete examine";
        if (!item.Moves) return $"non deplace (verdict {item.Verdict})";
        if (item.To.Contains(DstId.ToString("D")))
            return "converti au format GUID alors que la destination est nominative";
        if (!Path.GetFileName(item.To).StartsWith(B)) return $"cible inattendue : {Path.GetFileName(item.To)}";
        return null;
    }

    private static string NumbersArePerFolder(string root)
    {
        // Le motif le plus courant de la vraie bibliotheque : 625 jeux sur 652 presents dans
        // plusieurs regions d'un meme type y reutilisent le meme numero. Un renommage ordinaire ne
        // doit en decaler aucun — le plus petit -NN designe le media principal.
        string world = Dir(root, "Images", Plat, "Box - Front", "World");
        string na = Dir(root, "Images", Plat, "Box - Front", "North America");
        Noise(Path.Combine(world, A + "-20.png"), 31);
        Noise(Path.Combine(na, A + "-20.png"), 32);

        var moves = GameMediaRenamer.Plan(root, SrcId, Plat, A, B, MediaNameForm.Plain).ToList();
        if (moves.Count != 2) return $"{moves.Count} deplacements au lieu de 2";
        foreach (var m in moves)
            if (!Path.GetFileName(m.To).Equals(B + "-20.png", StringComparison.OrdinalIgnoreCase))
                return $"numero decale : {Path.GetFileName(m.From)} -> {Path.GetFileName(m.To)}";
        var res = GameMediaRenamer.Apply(moves);
        if (res.Reached != 2) return $"{res.Reached} fichiers arrives au lieu de 2";
        if (!File.Exists(Path.Combine(world, B + "-20.png"))) return "World n'a pas garde son -20";
        if (!File.Exists(Path.Combine(na, B + "-20.png"))) return "North America n'a pas garde son -20";
        return null;
    }

    private static string GuidSurvivesDeferredRename(string root)
    {
        // Le renommage differe ecrit "<ANCIEN titre>.<guid>-01.ext" pour un jeu deja renomme. Toute
        // la mecanique repose sur le fait qu'un nom GUID se reconnait au GUID SEUL. Un filtrage
        // prealable sur le titre l'annulait en silence : le manuel disparaissait entre le renommage
        // et l'ecriture. Les images survivaient par le cache indexe par id ; rien d'autre.
        var id = Guid.NewGuid();
        foreach (var (kind, ext) in new[] { ("Manuals", ".pdf"), ("Music", ".mp3") })
        {
            string dir = Dir(root, kind, Plat);
            File.WriteAllText(Path.Combine(dir, $"Ancien Titre.{id:D}-01{ext}"), "x");
        }
        string was = MediaResolver.SwapRootForTest(root);
        try
        {
            if (MediaResolver.Manual(Plat, id, "Nouveau Titre") == null)
                return "le manuel au format GUID est invisible sous le nouveau titre";
            if (MediaResolver.Music(Plat, id, "Nouveau Titre") == null)
                return "la musique au format GUID est invisible sous le nouveau titre";
        }
        finally { MediaResolver.SwapRootForTest(was); }
        return null;
    }

    private static string MovesTheRest(string root)
    {
        string front = Dir(root, "Images", Plat, "Box - Front");
        Noise(Path.Combine(front, A + "-01.png"), 1);
        Noise(Path.Combine(front, A + "-02.png"), 2);
        Noise(Path.Combine(front, B + "-01.png"), 3);
        string man = Dir(root, "Manuals", Plat);
        File.WriteAllText(Path.Combine(man, A + "-01.pdf"), "manuel alpha");

        var plan = Plan(root);
        if (plan.Moving != 3) return $"{plan.Moving} fichiers a deplacer au lieu de 3 ({plan})";

        var res = GameMediaMerge.Apply(plan, root, Guid.NewGuid(), Plat, A, B, sharedSource: false);
        if (res.Reached != 3) return $"{res.Reached} fichiers arrives au lieu de 3 ({res})";
        if (File.Exists(Path.Combine(front, A + "-01.png"))) return "le fichier source est encore la";
        if (!File.Exists(Path.Combine(front, B + "-01.png"))) return "le fichier de destination a ete ecrase";
        var beta = Directory.EnumerateFiles(front).Select(Path.GetFileName).OrderBy(x => x).ToList();
        if (beta.Count != 3) return "numerotation : " + string.Join(", ", beta);
        if (!File.Exists(Path.Combine(man, B + "-01.pdf"))) return "le manuel n'a pas suivi";
        return null;
    }

    // Le combine de deux jeux de MEME TITRE qui ne sont pas la meme entree DB : les nominatifs sont
    // aussi les fichiers du SURVIVANT, ils ne doivent jamais partir en orphelins — nom nu compris.
    // Les GUID de l'absorbe, eux, ne nomment que lui : ils partent. Et un chemin epingle survit a tout.
    private static string OrphansSpareTheAnsweredTitle(string root)
    {
        string sani = MediaResolver.Sanitize(A);
        string plain = Path.Combine(root, sani + "-01.png");
        string bare = Path.Combine(root, sani + ".pdf");
        string guid = Path.Combine(root, sani + "." + Guid.NewGuid().ToString("D") + "-01.png");
        var items = new[]
        {
            new MergeItem(plain, plain, MergeVerdict.SameFile),
            new MergeItem(bare, bare, MergeVerdict.SameFile),
            new MergeItem(guid, guid, MergeVerdict.Move),
        };
        var none = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Titre encore porte (par le root ou un tiers) : seuls les GUID sont orphelins.
        var kept = GameMediaMerge.OrphanCandidates(items, sani, titleStillAnswered: true, none);
        if (kept.Count != 1 || kept[0] != guid)
            return "titre porte : orphelins = " + string.Join(", ", kept.Select(Path.GetFileName));

        // Plus personne ne repond au titre : tout part.
        var all = GameMediaMerge.OrphanCandidates(items, sani, titleStillAnswered: false, none);
        if (all.Count != 3) return $"titre abandonne : {all.Count} orphelins au lieu de 3";

        // Un chemin epingle ne meurt jamais, meme titre abandonne.
        var pinned = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { bare };
        var spared = GameMediaMerge.OrphanCandidates(items, sani, titleStillAnswered: false, pinned);
        if (spared.Contains(bare)) return "un chemin epingle est parti en orphelin";
        return null;
    }
}
