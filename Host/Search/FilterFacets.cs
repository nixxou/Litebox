// Les valeurs que le dialogue de filtre propose, par dimension.
//
// Un simple sac de listes triées, rempli par MainWindow.ComputeFacets en un seul passage sur la
// bibliothèque. Il existe pour que l'ajout d'une dimension au filtre soit UNE ligne ici et une ligne
// dans le dialogue, au lieu d'allonger un tuple à sept éléments que personne ne peut plus lire.
//
// Les listes sont volontairement globales à la bibliothèque, pas au nœud affiché : voir le commentaire
// de FilterCriteria sur l'écart assumé avec LaunchBox.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace LbApiHost.Host.Search;

internal sealed class FilterFacets
{
    private static readonly StringComparer Ci = StringComparer.OrdinalIgnoreCase;

    public readonly HashSet<string> Genres = new(Ci);
    public readonly HashSet<string> Publishers = new(Ci);
    public readonly HashSet<string> Developers = new(Ci);
    public readonly HashSet<string> ReleaseTypes = new(Ci);
    public readonly HashSet<string> Platforms = new(Ci);
    public readonly HashSet<string> Regions = new(Ci);
    public readonly HashSet<string> PlayModes = new(Ci);
    public readonly HashSet<string> Statuses = new(Ci);
    public readonly HashSet<string> Progresses = new(Ci);
    public readonly HashSet<string> Esrb = new(Ci);
    public readonly HashSet<string> Controllers = new(Ci);
    public readonly HashSet<int> MaxPlayers = new();

    public void Add(HashSet<string> into, string? value)
    {
        var v = (value ?? "").Trim();
        if (v.Length > 0) into.Add(v);
    }

    /// <summary>Découpe une valeur multi-valuée (« Europe; France ») avant de l'ajouter — sans quoi la
    /// liste proposerait la combinaison au lieu de chacune de ses régions.</summary>
    public void AddTokens(HashSet<string> into, string? value)
    {
        foreach (var tok in (value ?? "").Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries))
            Add(into, tok);
    }

    public static List<string> Sorted(HashSet<string> h) => h.OrderBy(x => x, Ci).ToList();
    public List<int> SortedPlayers() => MaxPlayers.OrderBy(x => x).ToList();
}
