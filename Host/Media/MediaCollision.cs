// « Ce nouveau fichier doit-il porter le GUID du jeu, ou son titre ? »
//
// Une seule question, et jusqu'ici trois reponses differentes dans le code :
//
//   EditGameWindowImages.ImgNameCollision  un autre jeu de la plateforme porte le meme nom de
//                                          fichier -> GUID, sans regarder le DatabaseID ;
//   GameMediaSync                          meme nom de fichier ET DatabaseID DIFFERENT -> GUID ;
//                                          meme DatabaseID = deux fiches d'un meme jeu, donc une
//                                          consolidation, pas une collision ;
//   EditGameWindowDocuments.DocBaseName    « titre.<8 premiers caracteres du guid> », une forme
//                                          qu'aucun de nos analyseurs ne reconnait.
//
// C'est la deuxieme qui a raison, et c'est celle-ci. Mesure sur la bibliotheque reelle : 70 groupes
// de titres partages, dont 59 sont le MEME jeu saisi deux fois (meme DatabaseID) et 9 n'ont aucun
// DatabaseID. Deux seulement sont de vraies collisions. L'ancienne regle separait donc 68 groupes
// sur 70 sans raison, et separait en particulier des fiches qui doivent partager leurs fichiers.
//
// POURQUOI DEUX DatabaseID ABSENTS NE FONT PAS UNE COLLISION. « Inconnu » n'est pas une identite.
// Traiter deux jeux non identifies comme distincts reviendrait a leur interdire de partager des
// medias sur la foi d'un champ vide — alors qu'un nom nominatif appartient au TITRE, et que c'est
// precisement le comportement de LaunchBox.
//
// POURQUOI CE N'EST PAS SYMETRIQUE DU COMBINE. Le combine demande « ces deux fiches sont-elles le
// meme jeu ? » et exige donc deux DatabaseID presents et egaux. Ici on demande l'inverse : « ces
// deux fiches sont-elles a coup sur des jeux DIFFERENTS ? », ce qui exige deux DatabaseID presents
// et differents. Entre les deux, l'incertitude penche du meme cote : on partage.

#nullable enable

using System;
using System.Collections.Generic;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Media;

internal static class MediaCollision
{
    /// <summary>Un jeu reduit a ce que la question exige. Prendre des IGame ici obligerait le test
    /// a en fabriquer, c'est-a-dire a implementer une interface de plusieurs dizaines de membres
    /// pour en lire trois — et un test trop couteux a ecrire est un test qu'on n'ecrit pas.</summary>
    public readonly record struct Candidate(string Id, string Platform, string Title, int? DatabaseId);

    /// <summary>Vrai si un nouveau media de ce jeu doit prendre la forme GUID, parce qu'un AUTRE jeu
    /// de la plateforme est a coup sur un jeu different tout en visant le meme nom de fichier.
    ///
    /// <paramref name="candidates"/> est fourni par l'appelant pour qu'il puisse le calculer UNE
    /// fois plutot qu'a chaque fichier.</summary>
    public static bool NeedsGuidForm(string platform, string gameId, string sanitizedTitle,
                                     int? databaseId, IEnumerable<Candidate>? candidates)
    {
        // Sans DatabaseID, ce jeu ne peut etre declare different de personne : nominatif, sans
        // se poser d'autre question.
        if (!databaseId.HasValue || candidates == null) return false;
        if (string.IsNullOrEmpty(sanitizedTitle) || string.IsNullOrEmpty(platform)) return false;

        foreach (var c in candidates)
        {
            if (string.Equals(c.Id, gameId, StringComparison.OrdinalIgnoreCase)) continue;   // lui-meme
            if (!string.Equals(c.Platform, platform, StringComparison.OrdinalIgnoreCase)) continue;
            // Le nom SANITISE, parce que c'est lui qui atterrit sur le disque : deux titres ne
            // differant que par un caractere que le sanitiseur replie se heurtent quand meme.
            if (!string.Equals(MediaResolver.Sanitize(c.Title ?? ""), sanitizedTitle,
                               StringComparison.OrdinalIgnoreCase)) continue;
            if (c.DatabaseId.HasValue && c.DatabaseId.Value != databaseId.Value) return true;
        }
        return false;
    }

    /// <summary>Projection depuis les IGame de l'hote, une seule fois par appelant.</summary>
    public static List<Candidate> From(IEnumerable<IGame>? games)
    {
        var list = new List<Candidate>();
        if (games == null) return list;
        foreach (var g in games)
        {
            if (g == null) continue;
            list.Add(new Candidate(Try(() => g.Id) ?? "", Try(() => g.Platform) ?? "",
                                   Try(() => g.Title) ?? "", TryInt(() => g.LaunchBoxDbId)));
        }
        return list;
    }

    private static string? Try(Func<string?> f) { try { return f(); } catch { return null; } }
    private static int? TryInt(Func<int?> f) { try { return f(); } catch { return null; } }
}
