// Où vit un manuel, lequel est LE manuel, et lequel peut en détrôner un autre.
//
// LaunchBox n'expose qu'UN manuel détecté automatiquement, et il le choisit par ordre de parcours —
// sous-dossiers d'abord, alphabétiquement, première correspondance (mesuré). Cet ordre ne connaît pas
// les priorités de région. On ne cherche donc pas à le plier : on écrit <ManualPath> en dur, et c'est
// lui qui décide, chez LaunchBox comme chez nous.
//
// LA FORME. Un manuel téléchargé va dans Manuals\<Plateforme>\<Région>\<Titre>-NN.<ext>. La région
// vient du scraper ; à défaut c'est "World". Le sous-dossier est le SEUL endroit où la région peut
// vivre : un fichier dont le nom ne serait pas exactement <Titre> ou <Titre>-NN n'est pas reconnu par
// LaunchBox (mesuré — "<Titre>.FR.pdf" est invisible pour lui, ce que l'ancien code produisait).
//
// DÉTRÔNABLE. <ManualPath> pointant sur un fichier DE CETTE FORME, c'est nous qui l'avons posé : un
// manuel d'une région mieux placée le remplace. Pointant ailleurs, c'est un choix de l'utilisateur,
// et rien ici n'y touche. La distinction tient entièrement à la forme du chemin, ce qui est aussi ce
// qui la rend vérifiable sans rien stocker de plus.
//
// La musique n'a pas de région : <Titre>-NN.<ext> directement dans Music\<Plateforme>\.

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

    /// <summary>Ce chemin a-t-il la forme que NOUS produisons — Manuals\&lt;plat&gt;\&lt;Région connue&gt;\&lt;Titre&gt;-NN ?
    /// Si oui, il est à nous et donc détrônable ; sinon l'utilisateur l'a choisi et on n'y touche pas.</summary>
    public static bool IsManaged(string lbRoot, string platform, string title, string? path, out string region)
    {
        region = "";
        if (string.IsNullOrWhiteSpace(path)) return false;
        string abs;
        try { abs = Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(lbRoot, path)); }
        catch { return false; }

        string platDir;
        try { platDir = Path.GetFullPath(PlatformDir(lbRoot, platform)); }
        catch { return false; }

        string? dir = Path.GetDirectoryName(abs);
        if (dir == null) return false;
        // Exactement un niveau sous le dossier de la plateforme.
        if (!string.Equals(Path.GetDirectoryName(dir), platDir, StringComparison.OrdinalIgnoreCase)) return false;

        string folder = Path.GetFileName(dir);
        if (!LbRegions.Fallback.Any(k => string.Equals(k, folder, StringComparison.OrdinalIgnoreCase))) return false;
        if (!GameMediaRenamer.TryPlain(Path.GetFileNameWithoutExtension(abs), MediaResolver.Sanitize(title), out _))
            return false;

        region = folder;
        return true;
    }

    /// <summary>Rang d'une région dans la priorité effective. Plus petit = mieux. Une région absente de
    /// l'ordre passe après tout le reste plutôt que d'être traitée comme la meilleure.</summary>
    public static int Rank(string? region, IReadOnlyList<string> order)
    {
        string r = NormalizeRegion(region);
        for (int i = 0; i < order.Count; i++)
            if (string.Equals(order[i], r, StringComparison.OrdinalIgnoreCase)) return i;
        return int.MaxValue;
    }

    /// <summary>Tous les manuels du jeu, dossier de région par dossier de région.</summary>
    public static List<(string Path, string Region)> All(string lbRoot, string platform, string title)
    {
        var found = new List<(string, string)>();
        string sani = MediaResolver.Sanitize(title);
        if (sani.Length == 0) return found;
        string platDir = PlatformDir(lbRoot, platform);
        List<string> dirs;
        try { dirs = Directory.EnumerateDirectories(platDir).ToList(); }
        catch { return found; }
        foreach (var d in dirs)
        {
            string folder = Path.GetFileName(d);
            if (!LbRegions.Fallback.Any(k => string.Equals(k, folder, StringComparison.OrdinalIgnoreCase))) continue;
            try
            {
                foreach (var f in Directory.EnumerateFiles(d))
                    if (GameMediaRenamer.TryPlain(Path.GetFileNameWithoutExtension(f), sani, out _))
                        found.Add((f, folder));
            }
            catch { }
        }
        return found;
    }

    /// <summary>Le meilleur manuel disponible pour ce jeu, ou null. Sert aussi bien à choisir après un
    /// téléchargement qu'à recalculer après une suppression.</summary>
    public static string? Best(string lbRoot, string platform, string title, IReadOnlyList<string> order)
        => All(lbRoot, platform, title)
            .OrderBy(x => Rank(x.Region, order))
            .ThenBy(x => Path.GetFileName(x.Path), StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Path)
            .FirstOrDefault();
}
