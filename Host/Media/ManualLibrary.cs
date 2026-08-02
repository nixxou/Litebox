// Ou ecrire un manuel telecharge ou ajoute.
//
// LA FORME. Manuals\<Plateforme>\<Region>\<NomJeu>-NN.<ext>. La region vient du scraper ; a defaut
// c'est "World". Le sous-dossier est le SEUL endroit ou la region peut vivre : un fichier dont le nom
// n'est pas exactement <NomJeu> ou <NomJeu>-NN n'est reconnu par personne — mesure sur LaunchBox,
// "<Titre>.FR.pdf" lui est invisible, et c'est pourtant ce que produisait l'ancien code.
//
// CE QUI N'EST PAS ICI, ET POURQUOI. Il y avait une logique de « detronement » : choisir quel manuel
// merite d'etre dans <ManualPath> selon les priorites de region, et le reecrire quand un meilleur
// arrive. Elle a ete retiree sans avoir jamais servi. <ManualPath> ne s'ecrit plus que sur une
// DESIGNATION EXPLICITE de l'utilisateur — « manuel principal » dans le selecteur, « Download as
// Manual » dans le menu web — jamais sur un calcul de notre part. C'est aussi son role chez
// LaunchBox : une surcouche manuelle, pas un champ qu'un programme entretient.
//
// Tout ce qui entre dans la bibliotheque suit donc la convention, y compris ce que l'utilisateur
// designe comme principal : <ManualPath> se pose PAR-DESSUS. Efface plus tard, le fichier reste
// trouvable au lieu de devenir orphelin.
//
// La musique n'a pas de region : <NomJeu>-NN.<ext> directement dans Music\<Plateforme>\.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace LbApiHost.Host.Media;

internal static class ManualLibrary
{
    /// <summary>Région retenue quand le scraper n'en donne pas. "World" est la première de la liste
    /// de repli de LaunchBox, donc le choix le moins surprenant pour un fichier sans origine.</summary>
    public const string DefaultRegion = "World";

    /// <summary>Ramène un libellé de région quelconque au vocabulaire de LaunchBox. Un libellé inconnu
    /// devient <see cref="DefaultRegion"/> plutôt que de créer un dossier que rien ne saura classer.</summary>
    public static string NormalizeRegion(string? region)
    {
        if (string.IsNullOrWhiteSpace(region)) return DefaultRegion;
        string r = region.Trim();
        foreach (var known in LbRegions.Fallback)
            if (string.Equals(known, r, StringComparison.OrdinalIgnoreCase)) return known;
        return DefaultRegion;
    }

    public static string PlatformDir(string lbRoot, string platform)
        => Path.Combine(lbRoot, "Manuals", MediaResolver.Sanitize(platform));

    public static string RegionDir(string lbRoot, string platform, string? region)
        => Path.Combine(PlatformDir(lbRoot, platform), NormalizeRegion(region));

    /// <summary>Le chemin libre pour un nouveau manuel. La numérotation est PAR DOSSIER et part de 1 —
    /// un "-00" ne serait jamais retrouvé.</summary>
    public static string FreeDestination(string lbRoot, string platform, string title, string? region, string ext)
    {
        string dir = RegionDir(lbRoot, platform, region);
        string sani = MediaResolver.Sanitize(title);
        var taken = new HashSet<int>();
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir))
                if (GameMediaRenamer.TryPlain(Path.GetFileNameWithoutExtension(f), sani, out int n)) taken.Add(n);
        }
        catch { }
        int k = 1;
        while (taken.Contains(k)) k++;
        return Path.Combine(dir, $"{sani}-{k:D2}{ext}");
    }
}
