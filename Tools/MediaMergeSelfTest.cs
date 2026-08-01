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
}
