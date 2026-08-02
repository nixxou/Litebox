// Copier-coller de playlists dans l'arbre des sources : on copie une ou plusieurs playlists, on colle
// sur une plateforme (ou une catégorie sous une plateforme), et LiteBox crée de VRAIES nouvelles
// playlists transposées vers la plateforme de destination.
//
// LA PLATEFORME SOURCE N'EST PAS UNE DONNÉE — il faut la deviner, et c'est tout le sel :
//   1. une règle auto-populate « Platform Is Equal To X » la donne directement ;
//   2. sinon, parmi les PARENTS de la playlist (Parents.xml) et les plateformes au-dessus de ceux qui
//      sont des catégories, on retient celle dont le nom apparaît dans le Unique Name — la plus longue
//      gagne, sans quoi "Nintendo 64" attraperait les playlists de "Nintendo 64DD".
// Aucune des deux ⇒ la playlist n'est pas copiable : rien ne dirait quoi remplacer par quoi.
//
// LE NOM EST L'IDENTITÉ, ET LE FICHIER EN DÉCOULE. GameStore.PlaylistFileFor dérive le chemin du nom, et
// PlaylistCatalog.Load ne lit que le PREMIER <Playlist> d'un fichier : deux playlists dans le même
// fichier et la seconde n'existe plus. Cette classe est le premier chemin de l'app qui CRÉE des
// playlists, donc c'est ici que la garde se pose — deux fois : le nom doit être libre (modal si besoin),
// et même deux noms distincts qui convergeraient vers un même fichier sont séparés (FreePlaylistFile).
//
// L'écriture passe par l'op-log (AddNewPlaylistAt + setters), pas par du XML direct : c'est le seul
// chemin qui garde l'objet vivant dans _playlists/_playlistById — donc l'arbre montre la copie tout de
// suite — et qui hérite du backup + écriture atomique du flush.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using LbApiHost.Host.Data;
using LbApiHost.Host.Media;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Platforms;

internal static class PlaylistCopier
{
    // Presse-papier de session. On garde les OBJETS (une playlist sans PlaylistId reste copiable via la
    // règle 1, or GetPlaylistById ne la retrouverait pas) ; les disparues sont filtrées au collage.
    private static readonly List<HostPlaylist> _clip = new();

    internal static int ClipboardCount => _clip.Count;

    // ── Copier ────────────────────────────────────────────────────────────────
    /// <summary>Vrai quand on sait de quelle plateforme cette playlist parle — seule condition pour la copier.</summary>
    internal static bool CanCopy(HostPlaylist pl, HostDataManagerXml? dm)
        => pl != null && dm != null && SourcePlatformOf(pl, ParentsIndex.Load(), PlatformsByNorm(dm), out _) != null;

    /// <summary>Met dans le presse-papier celles dont la plateforme source est identifiable. Renvoie le
    /// nombre retenu (0 = rien copié, l'appelant n'a rien à faire).</summary>
    internal static int Copy(IReadOnlyList<HostPlaylist> playlists, HostDataManagerXml? dm, IWin32Window? owner)
    {
        if (playlists == null || playlists.Count == 0 || dm == null) return 0;
        var idx = ParentsIndex.Load();
        var plats = PlatformsByNorm(dm);

        var kept = new List<HostPlaylist>();
        var skipped = new List<string>();
        foreach (var pl in playlists)
        {
            if (SourcePlatformOf(pl, idx, plats, out string why) != null) kept.Add(pl);
            else skipped.Add($"• {Safe(() => pl.Name) ?? "?"} — {why}");
        }

        if (kept.Count == 0)
        {
            MessageBox.Show(owner,
                "Nothing to copy: LiteBox can't tell which platform these playlists belong to.\n\n"
                + string.Join("\n", skipped.Take(12))
                + "\n\nA playlist qualifies when it has a \"Platform Is Equal To\" rule, or when one of its "
                + "parent platforms is named in its Unique Name.",
                "Copy Playlists", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        _clip.Clear();
        _clip.AddRange(kept);
        if (skipped.Count > 0)
            MessageBox.Show(owner,
                $"Copied {kept.Count} playlist(s). {skipped.Count} skipped — no source platform:\n\n"
                + string.Join("\n", skipped.Take(12)),
                "Copy Playlists", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return kept.Count;
    }

    // ── Destination ───────────────────────────────────────────────────────────
    /// <summary>La plateforme de destination : le nœud lui-même s'il en est une, sinon la première en
    /// REMONTANT l'arbre affiché. On suit la chaîne des TreeNode et pas le graphe Parents.xml : une
    /// playlist/catégorie peut avoir plusieurs parents, et l'occurrence cliquée dit laquelle l'utilisateur
    /// avait sous les yeux. Null ⇒ « Paste » n'est pas proposé.</summary>
    internal static IPlatform? ResolveDestPlatform(TreeNode? node)
    {
        for (var n = node; n != null; n = n.Parent)
            if (n.Tag is IPlatform p) return p;
        return null;
    }

    // ── Coller ────────────────────────────────────────────────────────────────
    private sealed class Plan
    {
        public HostPlaylist Src = null!;
        public string SrcPlatform = "", SrcName = "";
        public string Name = "", NestedName = "";
    }

    /// <summary>Exécute le collage. Renvoie les playlists créées (vide = rien fait ou annulé) ; c'est
    /// l'appelant, dans MainWindow, qui rafraîchit l'arbre — même partage des rôles que NodeDeleter.</summary>
    internal static List<HostPlaylist> Paste(object? destNode, IPlatform? destPlatform,
                                             HostDataManagerXml? dm, IWin32Window? owner)
    {
        var made = new List<HostPlaylist>();
        if (dm == null || destPlatform == null || dm.ReadOnly) return made;

        string lbRoot = MediaResolver.LbRoot ?? "";
        if (lbRoot.Length == 0) return made;

        // LaunchBox/BigBox possèdent les XML quand ils tournent : le flush serait retenu, et les ops
        // playlist ne sont PAS rejouées en mémoire au démarrage — la copie n'apparaîtrait qu'après deux
        // redémarrages. Autant refuser franchement.
        if (GameStore.IsLaunchBoxRunning())
        {
            MessageBox.Show(owner, "Close LaunchBox / BigBox first — they own the XML files while running.",
                            "Paste Playlists", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return made;
        }

        // 1. Résolution (lecture seule) — recalculée ICI : Parents.xml a pu bouger depuis le copier.
        var alive = new HashSet<HostPlaylist>(dm.GetAllPlaylists().OfType<HostPlaylist>());
        var clip = _clip.Where(alive.Contains).ToList();
        if (clip.Count == 0)
        {
            MessageBox.Show(owner, "The copied playlists are no longer in the library.",
                            "Paste Playlists", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return made;
        }

        var idx = ParentsIndex.Load();
        var plats = PlatformsByNorm(dm);
        string destPlatName = Safe(() => destPlatform.Name) ?? "";
        if (destPlatName.Length == 0) return made;

        var plans = new List<Plan>();
        foreach (var pl in clip)
        {
            string? src = SourcePlatformOf(pl, idx, plats, out _);
            if (src == null) continue;
            plans.Add(new Plan { Src = pl, SrcPlatform = src, SrcName = Safe(() => pl.Name) ?? "" });
        }
        if (plans.Count == 0)
        {
            MessageBox.Show(owner, "None of the copied playlists has an identifiable source platform any more.",
                            "Paste Playlists", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return made;
        }

        // 2. Projection des noms — tout en mémoire, rien n'est écrit tant que le modal n'est pas validé.
        var takenNames = new HashSet<string>(
            dm.GetAllPlaylists().OfType<HostPlaylist>().Select(p => p.NameValue ?? "").Where(s => s.Length > 0),
            StringComparer.OrdinalIgnoreCase);
        var takenImages = new HashSet<string>(
            dm.GetAllPlaylists().OfType<HostPlaylist>().Select(p => ImageKey(p.NameValue ?? "")).Where(s => s.Length > 0),
            StringComparer.OrdinalIgnoreCase);

        // Chaque ligne du modal reste attachée à SON plan : l'appariement est une référence, pas une
        // correspondance par nom (deux playlists peuvent porter le même nom source).
        var unresolved = new List<(Plan Plan, PlaylistNameFix Fix)>();
        foreach (var p in plans)
        {
            p.Name = Substitute(p.SrcName, p.SrcPlatform, destPlatName);
            p.NestedName = Substitute(Safe(() => p.Src.NestedName) ?? "", p.SrcPlatform, destPlatName);

            if (p.Name.Length > 0 && !takenNames.Contains(p.Name) && !takenImages.Contains(ImageKey(p.Name)))
            {
                takenNames.Add(p.Name);
                takenImages.Add(ImageKey(p.Name));
            }
            else
            {
                unresolved.Add((p, new PlaylistNameFix
                {
                    SourceName = p.SrcName,
                    Name = p.Name,
                    NestedName = p.NestedName,
                    SameAsSource = string.Equals(p.SrcPlatform, destPlatName, StringComparison.OrdinalIgnoreCase),
                }));
            }
        }

        if (unresolved.Count > 0)
        {
            var rows = unresolved.Select(u => u.Fix).ToList();
            if (!PlaylistNameFixWindow.Ask(owner, rows, takenNames, takenImages)) return made;   // annulé : rien d'écrit
            foreach (var (plan, fix) in unresolved)
            {
                plan.Name = (fix.Name ?? "").Trim();
                plan.NestedName = (fix.NestedName ?? "").Trim();
                takenNames.Add(plan.Name);
                takenImages.Add(ImageKey(plan.Name));
            }
        }

        // 3. Création en mémoire + journal — premier effet de bord.
        try { Directory.CreateDirectory(Path.Combine(lbRoot, "Data", "Playlists")); } catch { }
        var reservedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var imageJobs = new List<(string From, string To)>();
        foreach (var p in plans)
        {
            var dst = CopyOne(p, destPlatName, dm, reservedFiles);
            if (dst == null) continue;
            made.Add(dst);
            imageJobs.Add((p.SrcName, p.Name));
        }
        if (made.Count == 0) return made;

        // 4. Parents.xml — la copie est neuve, SetSingleParent remplace un ensemble vide.
        bool destIsCategory = destNode is HostPlatformCategory;
        string destNodeName = destIsCategory
            ? (Safe(() => ((HostPlatformCategory)destNode!).Name) ?? "")
            : destPlatName;
        foreach (var dst in made)
            ParentsPicker.SetSingleParent(ParentChildKind.Playlist, dst.PlaylistIdValue, destIsCategory, destNodeName);

        // 5. Flush — c'est ici que les fichiers playlist naissent réellement.
        try { dm.FlushIfSafe(); } catch (Exception ex) { Console.WriteLine("[plcopy] flush: " + ex.Message); }

        // 6. Images en dernier : le plus lent, et le moins grave à rater.
        try
        {
            var old = Cursor.Current;
            Cursor.Current = Cursors.WaitCursor;
            try { foreach (var (from, to) in imageJobs) CopyImages(MediaResolver.ImagesRoot ?? "", from, to); }
            finally { Cursor.Current = old; }
        }
        catch (Exception ex) { Console.WriteLine("[plcopy] images: " + ex.Message); }

        return made;
    }

    // ── Une copie ─────────────────────────────────────────────────────────────
    private static HostPlaylist? CopyOne(Plan p, string destPlatform, HostDataManagerXml dm, ISet<string> reservedFiles)
    {
        try
        {
            var src = p.Src;
            string file = FreePlaylistFile(dm, p.Name, reservedFiles);
            if (string.IsNullOrEmpty(file)) return null;
            reservedFiles.Add(file);

            var dst = dm.AddNewPlaylistAt(p.Name, file);   // Name déjà journalisé

            // Champs modélisés. NestedName / SortTitle transposés, le reste verbatim ; LastGameId est un
            // pointeur de session ("dernier jeu joué ici"), il ne se duplique pas.
            dst.NestedName = p.NestedName;
            dst.SortTitle = Substitute(Safe(() => src.SortTitle) ?? "", p.SrcPlatform, destPlatform);
            dst.Notes = Safe(() => src.Notes) ?? "";
            dst.SortBy = Safe(() => src.SortBy) ?? "";
            dst.Category = Safe(() => src.Category) ?? "";
            dst.VideoPath = Safe(() => src.VideoPath) ?? "";
            dst.ImageType = Safe(() => src.ImageType) ?? "";
            dst.BigBoxView = Safe(() => src.BigBoxView) ?? "";
            dst.BigBoxTheme = Safe(() => src.BigBoxTheme) ?? "";
            dst.AutoPopulate = src.AutoPopulateValue;
            dst.IncludeWithPlatforms = src.IncludeWithPlatformsValue;
            dst.HideInBigBox = src.HideInBigBoxValue;

            // Champs que LiteBox ne modélise pas (BigBoxSortByOverride…) : recopiés tels quels.
            foreach (var f in src.ExtraFieldNames.ToList())
            {
                if (string.Equals(f, "IsAutogenerated", StringComparison.OrdinalIgnoreCase)) continue;
                dst.SetField(f, src.GetField(f));
            }
            // La copie est une création de l'utilisateur, pas une playlist que LaunchBox régénère.
            dst.SetField("IsAutogenerated", "false");

            // Règles : AddFilter + RecordFilters, PAS ReplaceFilters — celle-ci reconstruit chaque règle
            // et perd son dictionnaire Extra.
            foreach (var f in src.FiltersRaw)
            {
                if (f == null || string.IsNullOrWhiteSpace(f.FieldKey)) continue;
                string value = IsPlatformEqualRule(f) && Norm(f.Value) == Norm(p.SrcPlatform) ? destPlatform : (f.Value ?? "");
                dst.AddFilter(new PlaylistFilterDef(f.FieldKey, f.ComparisonTypeKey, value)
                {
                    Extra = f.Extra == null ? null : new Dictionary<string, string>(f.Extra, StringComparer.Ordinal),
                });
            }
            dst.RecordFilters();

            // Jeux : CLONER. ReplaceGames mute ce qu'on lui passe (owner, PlaylistId, ManualOrder) —
            // lui donner les instances de la source la ferait basculer sur la copie.
            var kept = src.GamesRaw
                .Where(g => g != null && !string.Equals(g.GamePlatformValue ?? "", p.SrcPlatform, StringComparison.OrdinalIgnoreCase))
                .Select(g => new HostPlaylistGame
                {
                    GameIdValue = g.GameIdValue,
                    GameTitleValue = g.GameTitleValue,
                    GamePlatformValue = g.GamePlatformValue,
                    GameFileNameValue = g.GameFileNameValue,
                    LaunchBoxDbIdValue = g.LaunchBoxDbIdValue,
                    Extra = g.Extra == null ? null : new Dictionary<string, string>(g.Extra, StringComparer.Ordinal),
                })
                .ToList();
            if (kept.Count > 0) dst.ReplaceGames(kept);

            return dst;
        }
        catch (Exception ex) { Console.WriteLine("[plcopy] copy \"" + p.SrcName + "\": " + ex.Message); return null; }
    }

    /// <summary>Un chemin de fichier LIBRE pour une nouvelle playlist. Le nom de fichier n'est plus lié au
    /// nom affiché dans ce codebase (renommer une playlist laisse son fichier tel quel, et le chargement
    /// énumère *.xml en se fiant au contenu), donc un suffixe est invisible pour l'utilisateur.</summary>
    private static string FreePlaylistFile(HostDataManagerXml dm, string name, ISet<string> reserved)
    {
        string first = dm.Store?.PlaylistFileFor(name) ?? "";
        if (first.Length == 0) return first;
        var used = new HashSet<string>(
            dm.GetAllPlaylists().OfType<HostPlaylist>().Select(p => p.FileValue ?? "").Where(s => s.Length > 0),
            StringComparer.OrdinalIgnoreCase);
        bool Free(string path) => !File.Exists(path) && !reserved.Contains(path) && !used.Contains(path);
        if (Free(first)) return first;

        string dir = Path.GetDirectoryName(first) ?? "", stem = Path.GetFileNameWithoutExtension(first);
        for (int i = 2; i < 1000; i++)
        {
            string candidate = Path.Combine(dir, $"{stem} ({i}).xml");
            if (Free(candidate)) return candidate;
        }
        return Path.Combine(dir, stem + " " + Guid.NewGuid().ToString("N").Substring(0, 8) + ".xml");
    }

    // ── Images ────────────────────────────────────────────────────────────────
    /// <summary>Duplique Images\Playlists\&lt;source&gt;\ vers le dossier de la copie. Les fichiers nommés
    /// d'après la playlist sont renommés au passage : MediaResolver cherche &lt;nom sanitizé&gt;.&lt;ext&gt;
    /// avant de se rabattre sur n'importe quel fichier du dossier.</summary>
    private static void CopyImages(string imagesRoot, string srcName, string dstName)
    {
        if (imagesRoot.Length == 0) return;
        string srcKey = ImageKey(srcName), dstKey = ImageKey(dstName);
        if (srcKey.Length == 0 || dstKey.Length == 0 || string.Equals(srcKey, dstKey, StringComparison.OrdinalIgnoreCase)) return;

        string from = Path.Combine(imagesRoot, "Playlists", srcKey);
        string to = Path.Combine(imagesRoot, "Playlists", dstKey);
        if (!Directory.Exists(from)) return;

        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            try
            {
                string rel = Path.GetRelativePath(from, file);
                string leaf = Path.GetFileNameWithoutExtension(rel);
                string target = string.Equals(leaf, srcKey, StringComparison.OrdinalIgnoreCase)
                    ? Path.Combine(to, Path.GetDirectoryName(rel) ?? "", dstKey + Path.GetExtension(rel))
                    : Path.Combine(to, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (!File.Exists(target)) File.Copy(file, target);
            }
            catch (Exception ex) { Console.WriteLine("[plcopy] image: " + ex.Message); }
        }
    }

    private static string ImageKey(string name) => string.IsNullOrWhiteSpace(name) ? "" : MediaResolver.Sanitize(name);

    // ── Plateforme source ─────────────────────────────────────────────────────
    private static bool IsPlatformEqualRule(PlaylistFilterDef f)
        => PlaylistFilterCatalog.Find(f.FieldKey)?.Key == "Platform"
           && PlaylistFilterCatalog.FindComparison(f.ComparisonTypeKey, PlaylistFieldKind.Text)?.Key == "EqualTo";

    /// <summary>De quelle plateforme cette playlist parle-t-elle ? Null + raison quand on ne peut pas le dire.</summary>
    internal static string? SourcePlatformOf(HostPlaylist pl, ParentsIndex idx,
                                             IReadOnlyDictionary<string, string> platformsByNorm, out string why)
    {
        why = "";
        if (pl == null) { why = "null"; return null; }
        string name = Safe(() => pl.Name) ?? "";

        // 1. La règle le dit. La valeur peut nommer une plateforme absente de Platforms.xml : elle reste
        //    utilisable, c'est la chaîne à substituer.
        var ruleValues = pl.FiltersRaw
            .Where(f => f != null && IsPlatformEqualRule(f) && !string.IsNullOrWhiteSpace(f.Value))
            .Select(f => f.Value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (ruleValues.Count == 1) return Canonical(ruleValues[0], platformsByNorm);
        if (ruleValues.Count > 1)
        {
            string? pick = BestByName(ruleValues, name);
            if (pick != null) return Canonical(pick, platformsByNorm);
            why = "several \"Platform Is Equal To\" rules, none of them named in the playlist name";
            return null;
        }

        // 2. Le nom, parmi les plateformes atteintes en remontant depuis ses parents.
        string id = pl.PlaylistIdValue ?? "";
        if (id.Length == 0) { why = "no PlaylistId"; return null; }
        var candidates = idx.PlatformAncestorsOf(new ParentKey('l', id));
        if (candidates.Count == 0) { why = "no platform among its parents"; return null; }
        string? best = BestByName(candidates, name);
        if (best == null) { why = "no parent platform is named in \"" + name + "\""; return null; }
        return Canonical(best, platformsByNorm);
    }

    /// <summary>Le candidat dont le nom apparaît dans le titre — le PLUS LONG gagne, et une occurrence en
    /// frontière de mot prime sur une occurrence noyée dans un autre mot.</summary>
    private static string? BestByName(IEnumerable<string> candidates, string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        return candidates
            .Where(c => !string.IsNullOrWhiteSpace(c) && name.IndexOf(c, StringComparison.OrdinalIgnoreCase) >= 0)
            .OrderByDescending(c => HasBoundaryHit(name, c) ? 1 : 0)
            .ThenByDescending(c => c.Length)
            .FirstOrDefault();
    }

    private static string Canonical(string platform, IReadOnlyDictionary<string, string> byNorm)
        => byNorm.TryGetValue(Norm(platform), out var canonical) ? canonical : platform;

    private static Dictionary<string, string> PlatformsByNorm(HostDataManagerXml dm)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (var p in dm.GetAllPlatforms() ?? Array.Empty<IPlatform>())
            {
                string n = Safe(() => p.Name) ?? "";
                if (n.Length > 0) map.TryAdd(Norm(n), n);
            }
        }
        catch { }
        return map;
    }

    private static string Norm(string? s) => PlaylistFilterCatalog.Norm(s);

    // ── Substitution ──────────────────────────────────────────────────────────
    /// <summary>Remplace le nom de la plateforme source par celui de la destination. Insensible à la casse
    /// pour DÉTECTER (le titre peut écrire "Playstation" là où la bibliothèque a "PlayStation"), mais on
    /// ÉCRIT la casse canonique de la destination. Les occurrences en frontière de mot sont préférées :
    /// sans cela "Nintendo 64" mangerait le préfixe de "Nintendo 64DD".</summary>
    internal static string Substitute(string text, string from, string to)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(from) || to == null) return text ?? "";
        return Replace(text, from, to, boundaryOnly: HasBoundaryHit(text, from));
    }

    private static bool HasBoundaryHit(string text, string needle)
    {
        for (int i = text.IndexOf(needle, StringComparison.OrdinalIgnoreCase); i >= 0;
             i = i + 1 <= text.Length - 1 ? text.IndexOf(needle, i + 1, StringComparison.OrdinalIgnoreCase) : -1)
            if (IsBoundary(text, i, needle.Length)) return true;
        return false;
    }

    private static bool IsBoundary(string text, int at, int len)
        => (at == 0 || !char.IsLetterOrDigit(text[at - 1]))
           && (at + len >= text.Length || !char.IsLetterOrDigit(text[at + len]));

    private static string Replace(string text, string from, string to, bool boundaryOnly)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            int hit = text.IndexOf(from, i, StringComparison.OrdinalIgnoreCase);
            if (hit < 0) break;
            sb.Append(text, i, hit - i);
            if (!boundaryOnly || IsBoundary(text, hit, from.Length)) sb.Append(to);
            else sb.Append(text, hit, from.Length);
            i = hit + from.Length;
        }
        sb.Append(text, i, text.Length - i);
        return sb.ToString();
    }

    private static T? Safe<T>(Func<T> f) { try { return f(); } catch { return default; } }
}
