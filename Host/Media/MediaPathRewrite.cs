// Les references par chemin SUIVENT les fichiers.
//
// <ManualPath>, <MusicPath>, <VideoPath>, <ThemeVideoPath> et l'ApplicationPath d'un document
// additionnel stockent un chemin en clair. Quand un renommage ou un combine deplace le fichier
// vise, laisser le champ en l'etat casse la reference. L'ancienne reponse — ne pas deplacer le
// fichier du tout (PinnedMedia sautait tout chemin stocke) — cassait l'autre moitie : la detection
// par convention perdait le fichier sous le nouveau titre, et le fichier restait fige sous un titre
// mort. La regle est donc celle de la specification : les fichiers bougent comme tout le reste, et
// les champs qui les nomment sont reecrits dans le meme geste.
//
// La reecriture balaie TOUS les jeux fournis, pas seulement celui qu'on renomme : un nom nominatif
// appartient a un TITRE, et rien n'interdit a un autre jeu de pointer sur le meme fichier.
//
// Pendant que LaunchBox tourne, le champ passe par le journal comme le titre : le XML sur disque
// rejoint les fichiers au vidage. Meme philosophie que le reste du renommage — les fichiers
// d'abord, le XML quand il redevient notre.
//
// La protection des chemins stockes n'a pas disparu, elle a change d'objet : elle ne vaut plus que
// contre l'EFFACEMENT (voir le balayage des orphelins du combine). Deplacer se repare en
// reecrivant le champ ; effacer ne se repare pas.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using Unbroken.LaunchBox.Plugins.Data;
using LbApiHost.Host.Data;

namespace LbApiHost.Host.Media;

internal static class MediaPathRewrite
{
    /// <summary>Reecrit, chez chaque jeu fourni, tout champ de chemin qui designait un fichier
    /// deplace. Renvoie le nombre de champs corriges. Les jeux arrivent en parametre plutot que par
    /// le DataManager pour que le test n'ait pas a en fabriquer un — meme raison que MediaCollision.</summary>
    public static int Apply(string lbRoot, IEnumerable<IGame>? games,
                            IReadOnlyCollection<(string From, string To)> moved)
    {
        if (string.IsNullOrEmpty(lbRoot) || games == null || moved == null || moved.Count == 0) return 0;

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (fromPath, toPath) in moved)
        {
            string? a = Abs(lbRoot, fromPath);
            if (a != null && !map.ContainsKey(a)) map[a] = toPath;
        }
        if (map.Count == 0) return 0;

        int done = 0;
        foreach (var g in games)
        {
            if (g == null) continue;
            try
            {
                done += Fix(lbRoot, map, () => g.ManualPath, v => g.ManualPath = v);
                done += Fix(lbRoot, map, () => g.MusicPath, v => g.MusicPath = v);
                done += Fix(lbRoot, map, () => g.VideoPath, v => g.VideoPath = v);
                done += Fix(lbRoot, map, () => g.ThemeVideoPath, v => g.ThemeVideoPath = v);
                foreach (var a in g.GetAllAdditionalApplications() ?? Array.Empty<IAdditionalApplication>())
                    if (a is HostAdditionalApplication h
                        && string.Equals(h.Section, HostAdditionalApplication.DocumentSection,
                                         StringComparison.OrdinalIgnoreCase))
                        done += Fix(lbRoot, map, () => h.ApplicationPath, v => h.ApplicationPath = v);
            }
            catch { /* un jeu illisible ne prive pas les autres de leur reecriture */ }
        }
        if (done > 0) Diag.LbLog.Info("media", $"{done} stored path(s) rewritten to follow moved files");
        return done;
    }

    private static int Fix(string lbRoot, Dictionary<string, string> map, Func<string?> get, Action<string> set)
    {
        string cur;
        try { cur = get() ?? ""; } catch { return 0; }
        if (cur.Length == 0) return 0;
        string? abs = Abs(lbRoot, cur);
        if (abs == null || !map.TryGetValue(abs, out var to)) return 0;
        try { set(Store(lbRoot, to)); return 1; } catch { return 0; }
    }

    private static string? Abs(string lbRoot, string stored)
    {
        try { return Path.GetFullPath(Path.IsPathRooted(stored) ? stored : Path.Combine(lbRoot, stored)); }
        catch { return null; }
    }

    /// <summary>La forme stockee de LaunchBox : relative quand le fichier est sous la racine,
    /// absolue ailleurs — la meme regle que l'editeur de documents.</summary>
    private static string Store(string lbRoot, string abs)
    {
        try
        {
            string full = Path.GetFullPath(abs), root = Path.GetFullPath(lbRoot).TrimEnd('\\');
            return full.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)
                ? Path.GetRelativePath(root, full)
                : full;
        }
        catch { return abs; }
    }
}
