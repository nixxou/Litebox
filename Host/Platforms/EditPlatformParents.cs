// The "Parents" tab of the Edit Platform window — which Platform Categories contain this platform. LaunchBox
// stores membership in Data\Parents.xml as <Parent> rows (child <PlatformName> ↔ <ParentPlatformCategoryName>).
// A checkbox tree of categories; apply rewrites this platform's category <Parent> rows.

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
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Platforms;

internal static class EditPlatformParents
{
    private static readonly Color Bg = LiteBoxTheme.Bg, Panel2 = LiteBoxTheme.Panel2, Fg = LiteBoxTheme.Fg, SubFg = LiteBoxTheme.SubFg;
    private static string ParentsFile => Path.Combine(MediaResolver.LbRoot ?? "", "Data", "Parents.xml");
    private static string PlatformsFile => Path.Combine(MediaResolver.LbRoot ?? "", "Data", "Platforms.xml");

    public static (Control panel, Action apply) Build(IPlatform plat, bool readOnly, float s)
    {
        int S(int px) => (int)Math.Round(px * s);
        string name = Safe(() => plat.Name) ?? "";
        var p = new Panel { Dock = DockStyle.Fill, BackColor = Bg, Padding = new Padding(S(12)) };

        var info = new Label
        {
            Dock = DockStyle.Top, Height = S(40), ForeColor = SubFg, BackColor = Bg,
            Text = "This tab specifies where to display this platform in Platform Categories lists. Check all parents below which should contain this platform.",
        };

        var tree = new TreeView { Dock = DockStyle.Fill, CheckBoxes = true, BackColor = Panel2, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle, HideSelection = true, ShowLines = true };
        var current = ParentCategories(name);
        var rootNode = new TreeNode("Root") { Tag = null };
        foreach (var cat in AllCategoryNames().OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            rootNode.Nodes.Add(new TreeNode(cat + " (Category)") { Tag = cat, Checked = current.Contains(cat) });
        rootNode.Expand();
        tree.Nodes.Add(rootNode);

        p.Controls.Add(tree);
        p.Controls.Add(info);

        void Apply()
        {
            if (readOnly) return;
            var chosen = rootNode.Nodes.Cast<TreeNode>().Where(n => n.Checked && n.Tag is string).Select(n => (string)n.Tag!).ToList();
            SetParentCategories(name, chosen);
        }
        return (p, Apply);
    }

    // ── Parents.xml I/O ──
    private static HashSet<string> ParentCategories(string platform)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(ParentsFile)) return set;
            var doc = XDocument.Load(ParentsFile);
            foreach (var e in doc.Root?.Elements("Parent") ?? Enumerable.Empty<XElement>())
                if (string.Equals((string?)e.Element("PlatformName"), platform, StringComparison.OrdinalIgnoreCase))
                {
                    var cat = (string?)e.Element("ParentPlatformCategoryName");
                    if (!string.IsNullOrWhiteSpace(cat)) set.Add(cat!);
                }
        }
        catch { }
        return set;
    }

    private static void SetParentCategories(string platform, IReadOnlyCollection<string> categories)
    {
        try
        {
            if (!File.Exists(ParentsFile)) return;
            var doc = XDocument.Load(ParentsFile);
            var root = doc.Root; if (root == null) return;
            // Remove this platform's existing category-parent rows (keep playlist/other rows untouched).
            foreach (var e in root.Elements("Parent").Where(e =>
                         string.Equals((string?)e.Element("PlatformName"), platform, StringComparison.OrdinalIgnoreCase)
                         && !string.IsNullOrWhiteSpace((string?)e.Element("ParentPlatformCategoryName"))).ToList())
                e.Remove();
            foreach (var cat in categories.Distinct(StringComparer.OrdinalIgnoreCase))
                root.Add(new XElement("Parent",
                    new XElement("PlatformName", platform),
                    new XElement("PlaylistId", ""),
                    new XElement("PlatformCategoryName", ""),
                    new XElement("ParentPlatformName", ""),
                    new XElement("ParentPlaylistId", ""),
                    new XElement("ParentPlatformCategoryName", cat)));
            doc.Save(ParentsFile);
        }
        catch { }
    }

    // Category names — from Platforms.xml <PlatformCategory> (independent of the plugin DataManager, so the
    // render probe works too).
    private static List<string> AllCategoryNames()
    {
        var list = new List<string>();
        try
        {
            if (!File.Exists(PlatformsFile)) return list;
            var doc = XDocument.Load(PlatformsFile);
            foreach (var c in doc.Root?.Elements("PlatformCategory") ?? Enumerable.Empty<XElement>())
            {
                var n = (string?)c.Element("Name");
                if (!string.IsNullOrWhiteSpace(n)) list.Add(n!);
            }
        }
        catch { }
        return list;
    }

    private static T? Safe<T>(Func<T> f) { try { return f(); } catch { return default; } }
}
