// Le graphe de Data\Parents.xml, en LECTURE seule et réutilisable.
//
// Trois endroits reconstruisaient déjà cet index à la volée, chacun mêlé à autre chose :
// ParentsPicker (états tri-state), NodeDeleter.Depth (profondeur maximale) et
// HostDataManagerXml.ReloadHierarchy (rattachement des objets vivants). Aucun n'est réutilisable tel
// quel. Celui-ci ne fait qu'une chose : dire qui sont les parents d'un nœud, et quelles PLATEFORMES
// on atteint en remontant — ce dont le copieur de playlists a besoin pour deviner la plateforme
// « source » d'une playlist, qui n'est stockée nulle part.
//
// Multi-parent assumé (un nœud peut avoir plusieurs lignes), cycles coupés par l'ensemble des
// visités. Fichier absent ou illisible = index vide : l'appelant dégrade, il ne plante pas.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using LbApiHost.Host.Media;

namespace LbApiHost.Host.Platforms;

/// <summary>Un nœud de la hiérarchie : 'p' plateforme (par nom), 'c' catégorie (par nom),
/// 'l' playlist (par PlaylistId — c'est ainsi que Parents.xml la désigne, jamais par son nom).</summary>
internal readonly record struct ParentKey(char Kind, string Name);

internal sealed class ParentsIndex
{
    // Les noms de plateformes/catégories sont comparés sans casse (LaunchBox les traite ainsi), mais
    // la casse de première apparition est conservée pour être restituée telle quelle.
    private sealed class KeyComparer : IEqualityComparer<ParentKey>
    {
        public static readonly KeyComparer I = new();
        public bool Equals(ParentKey a, ParentKey b)
            => a.Kind == b.Kind && string.Equals(a.Name ?? "", b.Name ?? "", StringComparison.OrdinalIgnoreCase);
        public int GetHashCode(ParentKey k)
            => HashCode.Combine(k.Kind, (k.Name ?? "").ToLowerInvariant());
    }

    private readonly Dictionary<ParentKey, List<ParentKey>> _parentsOf = new(KeyComparer.I);

    private static string ParentsFile => Path.Combine(MediaResolver.LbRoot ?? "", "Data", "Parents.xml");

    public static ParentsIndex Load() => LoadFrom(ParentsFile);

    /// <summary>Chemin explicite — le self-test travaille sur un arbre temporaire.</summary>
    internal static ParentsIndex LoadFrom(string file)
    {
        var idx = new ParentsIndex();
        try
        {
            if (!File.Exists(file)) return idx;
            foreach (var e in XDocument.Load(file).Root?.Elements("Parent") ?? Enumerable.Empty<XElement>())
            {
                string cPlat = Txt(e, "PlatformName"), cPlay = Txt(e, "PlaylistId"), cCat = Txt(e, "PlatformCategoryName");
                string pPlat = Txt(e, "ParentPlatformName"), pCat = Txt(e, "ParentPlatformCategoryName");

                ParentKey? child = cPlay.Length > 0 ? new ParentKey('l', cPlay)
                                 : cCat.Length > 0 ? new ParentKey('c', cCat)
                                 : cPlat.Length > 0 ? new ParentKey('p', cPlat) : null;
                if (child == null) continue;

                // Tous les champs parent vides = appartenance explicite à Root : aucun parent à retenir.
                ParentKey? parent = pCat.Length > 0 ? new ParentKey('c', pCat)
                                  : pPlat.Length > 0 ? new ParentKey('p', pPlat) : null;
                if (parent == null) continue;

                if (!idx._parentsOf.TryGetValue(child.Value, out var l))
                    idx._parentsOf[child.Value] = l = new List<ParentKey>();
                l.Add(parent.Value);
            }
        }
        catch (Exception ex) { Console.WriteLine("[parents] index: " + ex.Message); }
        return idx;
    }

    private static string Txt(XElement e, string name) => ((string?)e.Element(name) ?? "").Trim();

    /// <summary>Les parents DIRECTS de ce nœud (hors Root).</summary>
    public IReadOnlyList<ParentKey> ParentsOf(ParentKey child)
        => _parentsOf.TryGetValue(child, out var l) ? l : Array.Empty<ParentKey>();

    /// <summary>Toutes les plateformes atteintes en remontant depuis ce nœud : les parents plateformes
    /// directs, puis celles trouvées au-dessus des parents catégories. Ordre = du plus proche au plus
    /// lointain, sans doublon.</summary>
    public List<string> PlatformAncestorsOf(ParentKey child)
    {
        var found = new List<string>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<ParentKey>(KeyComparer.I) { child };
        var queue = new Queue<ParentKey>();
        queue.Enqueue(child);
        while (queue.Count > 0)
        {
            foreach (var p in ParentsOf(queue.Dequeue()))
            {
                if (p.Kind == 'p' && seenNames.Add(p.Name)) found.Add(p.Name);
                if (visited.Add(p)) queue.Enqueue(p);   // on remonte AUSSI au-dessus d'une plateforme
            }
        }
        return found;
    }
}
