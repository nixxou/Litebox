// Shared "Parents" picker — LB parity for the Edit Platform / Edit Platform Category / Edit Playlist windows,
// SINGLE or MULTI edit (same code path; single is the one-child case).
// LB's rules (decoded from the real dialogs + Data\Parents.xml ground truth):
//   platform parents = Root or CATEGORIES only (no Root checkbox — ZERO checks writes an explicit Root row,
//                      as LB does, e.g. "Nintendo Game BoyX");
//   category parents = Root, categories or PLATFORMS;
//   playlist parents = Root, categories or PLATFORMS (a playlist can NEVER be a parent).
// Parent links are PER OBJECT (confirmed): a candidate appearing at several tree spots (multi-parent) has ONE
// state synchronized across its occurrences.
// Boxes are TRI-STATE (StateImageList): checked = ALL edited children have this parent, unchecked = none,
// partial = some (applying keeps each child's own value). Cycle on click: uniform 0↔1; originally-partial
// 2→1→0→2. In single mode partial can never occur.
// Row format (all SIX elements, LB-style): child = PlatformName | PlaylistId | PlatformCategoryName; parent =
// ParentPlatformName | ParentPlatformCategoryName; all parent fields EMPTY = explicit Root membership.

#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using System.Xml.Linq;
using LbApiHost.Host.Media;
using LbApiHost.Host.UiKit;
using LbApiHost.Host.Data;

namespace LbApiHost.Host.Platforms;

internal enum ParentChildKind { Platform, Category, Playlist }

internal static class ParentsPicker
{
    private static readonly Color Bg = LiteBoxTheme.Bg, Panel2 = LiteBoxTheme.Panel2, Fg = LiteBoxTheme.Fg, SubFg = LiteBoxTheme.SubFg;
    private static string ParentsFile => Path.Combine(MediaResolver.LbRoot ?? "", "Data", "Parents.xml");
    private static string PlatformsFile => Path.Combine(MediaResolver.LbRoot ?? "", "Data", "Platforms.xml");

    // Candidate key: kind 'c' (category) or 'p' (platform) + name.
    private readonly record struct Key(char K, string Name);
    private const int StUnchecked = 0, StChecked = 1, StPartial = 2;

    public static (Control panel, Action apply) Build(ParentChildKind kind, string childKey, bool readOnly, float s, Func<string>? childKeyAtApply = null)
        => BuildCore(kind, new List<string> { childKey }, readOnly, s, childKeyAtApply);

    /// <summary>Multi-edit: one tree for SEVERAL children of the same kind (tri-state boxes).</summary>
    public static (Control panel, Action apply) BuildMulti(ParentChildKind kind, List<string> childKeys, bool readOnly, float s)
        => BuildCore(kind, childKeys, readOnly, s, null);

    private static (Control panel, Action apply) BuildCore(ParentChildKind kind, List<string> childKeys, bool readOnly, float s, Func<string>? childKeyAtApply)
    {
        int S(int px) => (int)Math.Round(px * s);
        var p = new Panel { Dock = DockStyle.Fill, BackColor = Bg, Padding = new Padding(S(12)) };
        int n = childKeys.Count;

        string noun = kind switch { ParentChildKind.Category => "category", ParentChildKind.Playlist => "playlist", _ => "platform" };
        string nounN = n > 1 ? noun switch { "category" => "categories", "playlist" => "playlists", _ => "platforms" } : noun;
        var info = new Label
        {
            Dock = DockStyle.Top, Height = S(40), ForeColor = SubFg, BackColor = Bg,
            Text = n > 1
                ? $"Editing {n} {nounN}. Checked = parent of ALL, unchecked = parent of none, filled = mixed (left as-is per {noun})."
                : $"This tab specifies where to display this {noun} in Platform Categories lists. Select all parents below which should contain this {noun}.",
        };

        // ── load candidates + hierarchy + per-child current parents ──
        var catNames = LoadNames("PlatformCategory");
        var platNames = LoadNames("Platform");
        var candidates = new HashSet<Key>(catNames.Select(nm => new Key('c', nm)).Concat(platNames.Select(nm => new Key('p', nm))));
        var canon = new Dictionary<string, Key>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in candidates) canon[k.K + "|" + k.Name] = k;
        Key Canon(char k, string name) => canon.TryGetValue(k + "|" + name, out var c) ? c : new Key(k, name);

        var childSet = new HashSet<string>(childKeys, StringComparer.OrdinalIgnoreCase);
        var childrenOf = new Dictionary<Key, List<Key>>();
        var hasParent = new HashSet<Key>();
        var explicitRoot = new HashSet<Key>();
        var parentsByChild = new Dictionary<string, HashSet<Key>>(StringComparer.OrdinalIgnoreCase);
        var rootByChild = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var ck in childKeys) { parentsByChild[ck] = new HashSet<Key>(); rootByChild[ck] = false; }
        try
        {
            if (File.Exists(ParentsFile))
                foreach (var e in XDocument.Load(ParentsFile).Root?.Elements("Parent") ?? Enumerable.Empty<XElement>())
                {
                    string cPlat = ((string?)e.Element("PlatformName") ?? "").Trim();
                    string cPlay = ((string?)e.Element("PlaylistId") ?? "").Trim();
                    string cCat = ((string?)e.Element("PlatformCategoryName") ?? "").Trim();
                    string pPlat = ((string?)e.Element("ParentPlatformName") ?? "").Trim();
                    string pCat = ((string?)e.Element("ParentPlatformCategoryName") ?? "").Trim();
                    Key? parent = pCat.Length > 0 ? Canon('c', pCat) : pPlat.Length > 0 ? Canon('p', pPlat) : (Key?)null;

                    // Hierarchy among CANDIDATES (categories/platforms as children; playlists don't render here).
                    Key? child = cCat.Length > 0 ? Canon('c', cCat) : cPlat.Length > 0 ? Canon('p', cPlat) : (Key?)null;
                    if (child != null && candidates.Contains(child.Value))
                    {
                        if (parent != null && candidates.Contains(parent.Value))
                        {
                            if (!childrenOf.TryGetValue(parent.Value, out var l)) childrenOf[parent.Value] = l = new List<Key>();
                            l.Add(child.Value);
                            hasParent.Add(child.Value);
                        }
                        else explicitRoot.Add(child.Value);
                    }

                    // Edited children: collect their current parents.
                    string cid = kind switch { ParentChildKind.Platform => cPlat, ParentChildKind.Category => cCat, _ => cPlay };
                    if (cid.Length > 0 && childSet.Contains(cid))
                    {
                        if (parent != null) parentsByChild[cid].Add(parent.Value);
                        else rootByChild[cid] = true;
                    }
                }
        }
        catch { }

        int StateOf(Key key)
        {
            int cnt = parentsByChild.Values.Count(set => set.Contains(key));
            return cnt == 0 ? StUnchecked : cnt == n ? StChecked : StPartial;
        }
        int rootState = rootByChild.Values.Count(v => v) switch { 0 => StUnchecked, var c when c == n => StChecked, _ => StPartial };

        // ── tree (tri-state via StateImageList) ──
        var tree = new TreeView
        {
            Dock = DockStyle.Fill, BackColor = Panel2, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle,
            HideSelection = true, ShowLines = true, StateImageList = TriStateImages(S(14)),
        };
        var occurrences = new Dictionary<Key, List<TreeNode>>();
        var origState = new Dictionary<Key, int>();
        bool ShowPlatforms = kind != ParentChildKind.Platform;   // platform edit shows categories only
        bool IsSelf(Key key) => kind == ParentChildKind.Category && key.K == 'c' && childSet.Contains(key.Name);

        void AddNode(TreeNode parentNode, Key key, HashSet<Key> path)
        {
            if (IsSelf(key)) return;                                            // never self-parent
            if (key.K == 'p' && !ShowPlatforms) { RecurseOnlyCats(parentNode, key, path); return; }
            if (!origState.ContainsKey(key)) origState[key] = StateOf(key);
            var tn = new TreeNode($"{key.Name} ({(key.K == 'c' ? "Category" : "Platform")})")
            { Tag = key, StateImageIndex = origState[key] };
            parentNode.Nodes.Add(tn);
            if (!occurrences.TryGetValue(key, out var l)) occurrences[key] = l = new List<TreeNode>();
            l.Add(tn);
            if (path.Contains(key)) return;                                     // cycle guard
            path.Add(key);
            foreach (var child in Kids(key)) AddNode(tn, child, path);
            path.Remove(key);
        }
        // Platform edit: platforms themselves are hidden, but a category nested UNDER a platform must still
        // surface — recurse through the platform without rendering it.
        void RecurseOnlyCats(TreeNode parentNode, Key platKey, HashSet<Key> path)
        {
            if (path.Contains(platKey)) return;
            path.Add(platKey);
            foreach (var child in Kids(platKey)) AddNode(parentNode, child, path);
            path.Remove(platKey);
        }
        IEnumerable<Key> Kids(Key k)
            => childrenOf.TryGetValue(k, out var l)
                ? l.Distinct().OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                : Enumerable.Empty<Key>();

        // Root node: no box at all for platforms (LB); tri-state box otherwise.
        var rootNode = new TreeNode("Root") { Tag = null, StateImageIndex = kind == ParentChildKind.Platform ? -1 : rootState };
        tree.Nodes.Add(rootNode);
        var rootKeys = candidates
            .Where(k => explicitRoot.Contains(k) || !hasParent.Contains(k))
            .OrderBy(k => k.K == 'c' ? 0 : 1)    // categories first at root, LB-style
            .ThenBy(k => k.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var k in rootKeys) AddNode(rootNode, k, new HashSet<Key>());
        tree.ExpandAll();

        int origRootState = rootState;
        int Cycle(int cur, int orig) => orig == StPartial
            ? (cur == StPartial ? StChecked : cur == StChecked ? StUnchecked : StPartial)
            : (cur == StChecked ? StUnchecked : StChecked);
        tree.NodeMouseClick += (_, e) =>
        {
            if (readOnly || e.Button != MouseButtons.Left || e.Node == null) return;
            if (e.Node == rootNode)
            {
                if (kind == ParentChildKind.Platform) return;                    // no Root box for platforms
                rootState = Cycle(rootState, origRootState);
                rootNode.StateImageIndex = rootState;
                return;
            }
            if (e.Node.Tag is not Key key) return;
            int next = Cycle(e.Node.StateImageIndex, origState[key]);
            foreach (var tn in occurrences[key]) tn.StateImageIndex = next;      // sync every occurrence
        };

        p.Controls.Add(tree);
        p.Controls.Add(info);

        void Apply()
        {
            if (readOnly) return;
            var addAll = occurrences.Where(kv => kv.Value[0].StateImageIndex == StChecked).Select(kv => kv.Key).ToHashSet();
            var keep = occurrences.Where(kv => kv.Value[0].StateImageIndex == StPartial).Select(kv => kv.Key).ToHashSet();
            foreach (var ck in childKeys)
            {
                var parents = parentsByChild[ck].Where(keep.Contains).Concat(addAll).Distinct().ToList();
                bool root = kind == ParentChildKind.Platform
                    ? parents.Count == 0
                    : rootState == StChecked || (rootState == StPartial && rootByChild[ck]);
                string writeKey = n == 1 ? (childKeyAtApply?.Invoke() ?? ck) : ck;
                WriteParents(kind, writeKey, root, parents);
            }
        }
        return (p, Apply);
    }

    // ── Parents.xml write: replace ALL of this child's rows with the chosen set (other children untouched) ──
    private static void WriteParents(ParentChildKind kind, string childKey, bool rootChecked, List<Key> parents)
    {
        try
        {
            if (!File.Exists(ParentsFile)) return;
            var doc = XDocument.Load(ParentsFile);
            var root = doc.Root; if (root == null) return;
            string childField = kind switch { ParentChildKind.Platform => "PlatformName", ParentChildKind.Category => "PlatformCategoryName", _ => "PlaylistId" };
            foreach (var e in root.Elements("Parent")
                         .Where(e => string.Equals(((string?)e.Element(childField) ?? "").Trim(), childKey, StringComparison.OrdinalIgnoreCase))
                         .ToList())
                e.Remove();

            XElement Row(string parentPlat, string parentCat) => new("Parent",
                new XElement("PlatformName", kind == ParentChildKind.Platform ? childKey : ""),
                new XElement("PlaylistId", kind == ParentChildKind.Playlist ? childKey : ""),
                new XElement("PlatformCategoryName", kind == ParentChildKind.Category ? childKey : ""),
                new XElement("ParentPlatformName", parentPlat),
                new XElement("ParentPlaylistId", ""),
                new XElement("ParentPlatformCategoryName", parentCat));

            foreach (var k in parents.DistinctBy(k => (char.ToLowerInvariant(k.K), k.Name.ToLowerInvariant())))
                root.Add(k.K == 'c' ? Row("", k.Name) : Row(k.Name, ""));
            if (rootChecked) root.Add(Row("", ""));   // explicit Root membership row
            LbXml.Save(doc, ParentsFile);
        }
        catch { }
    }

    private static List<string> LoadNames(string element)
    {
        var list = new List<string>();
        try
        {
            if (!File.Exists(PlatformsFile)) return list;
            foreach (var c in XDocument.Load(PlatformsFile).Root?.Elements(element) ?? Enumerable.Empty<XElement>())
            {
                var n = ((string?)c.Element("Name") ?? "").Trim();
                if (n.Length > 0) list.Add(n);
            }
        }
        catch { }
        return list;
    }

    // 3 state images: 0 = empty box, 1 = checked box, 2 = partially-filled box.
    private static ImageList TriStateImages(int sz)
    {
        var il = new ImageList { ImageSize = new Size(sz + 2, sz + 2), ColorDepth = ColorDepth.Depth32Bit };
        for (int st = 0; st < 3; st++)
        {
            var bmp = new Bitmap(sz + 2, sz + 2);
            using var g = Graphics.FromImage(bmp);
            g.Clear(Color.Transparent);
            var rect = new Rectangle(1, 1, sz - 1, sz - 1);
            using (var bg = new SolidBrush(Panel2)) g.FillRectangle(bg, rect);
            using (var pen = new Pen(SubFg)) g.DrawRectangle(pen, rect);
            if (st == StChecked)
            {
                using var chk = new Pen(Color.FromArgb(120, 200, 120), 2f);
                g.DrawLines(chk, new[] { new Point(3, sz / 2), new Point(sz / 2 - 1, sz - 4), new Point(sz - 3, 3) });
            }
            else if (st == StPartial)
            {
                using var fill = new SolidBrush(Color.FromArgb(120, 160, 210));
                g.FillRectangle(fill, Rectangle.Inflate(rect, -(sz / 4), -(sz / 4)));
            }
            il.Images.Add(bmp);
        }
        return il;
    }
}
