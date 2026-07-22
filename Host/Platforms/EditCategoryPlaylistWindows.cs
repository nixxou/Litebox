// Edit Platform Category / Edit Playlist windows on the OptionsWindow shell.
//
// Edit Platform Category — LB parity (tabs Details / Notes / Parents + right-side Images panel on Details):
//   Details: Unique Name (RENAME — see below), Nested Name, Sort Title, Video Path (+Browse…),
//            "Hide in Big Box (Does Not Hide Games)". Images panel = LB's four category types
//            (Banner / Clear Logo (Override) / Device / Fanart), files under Images\Platform Categories\<name>.
//   Rename is SURGICAL direct-XML (a category's <Name> is its identity everywhere): Platforms.xml
//   <PlatformCategory> node, every Parents.xml reference (child + parent side), and the images folder are all
//   renamed, THEN the live object is re-pointed (SetNameInternal) so later op-log records key on the new name.
//   Other fields go through the HostPlatformCategory setters → op-log → FlushIfSafe on close.
//
// Edit Playlist — Parents tab only for now (Details/Auto-Populate/Games are future work).

#nullable enable

using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using System.Xml.Linq;
using LbApiHost.Host.Data;
using LbApiHost.Host.Media;
using LbApiHost.Host.Options;
using LbApiHost.Host.UiKit;
using Unbroken.LaunchBox.Plugins;

namespace LbApiHost.Host.Platforms;

internal static class EditCategoryWindow
{
    private static readonly Color Bg = LiteBoxTheme.Bg, Panel2 = LiteBoxTheme.Panel2, Fg = LiteBoxTheme.Fg, SubFg = LiteBoxTheme.SubFg;
    private static string PlatformsFile => Path.Combine(MediaResolver.LbRoot ?? "", "Data", "Platforms.xml");
    private static string ParentsFile => Path.Combine(MediaResolver.LbRoot ?? "", "Data", "Parents.xml");

    public static void Open(HostPlatformCategory cat, bool readOnly, IWin32Window? owner)
    {
        if (cat == null) return;
        string name = Safe(() => cat.Name) ?? "Category";
        using var w = new OptionsWindow($"Edit Platform Category — {name}{(readOnly ? "   [READ-ONLY]" : "")}");
        float s = LiteBoxTheme.DpiScale(w);

        var (details, applyDetails) = BuildDetails(cat, readOnly, s);
        w.AddSection("Details", details, applyDetails);

        var (notes, applyNotes) = BuildNotes(cat, s, readOnly);
        w.AddSection("Notes", notes, applyNotes);

        // Deferred child key: Details may have renamed the category by the time the appliers run.
        var (parents, applyParents) = ParentsPicker.Build(ParentChildKind.Category, name, readOnly, s, () => cat.Name);
        w.AddSection("Parents", parents, applyParents);

        w.ShowDialog(owner);
        if (!readOnly) { try { (PluginHelper.DataManager as HostDataManagerXml)?.FlushIfSafe(); } catch { } }
    }

    /// <summary>Sections as (title, control) WITHOUT the shell — for the offscreen render probe.</summary>
    internal static System.Collections.Generic.List<(string title, Control ctrl)> BuildSectionsForRender(HostPlatformCategory cat, float s)
    {
        var list = new System.Collections.Generic.List<(string, Control)>();
        try { var (d, _) = BuildDetails(cat, false, s); list.Add(("Details", d)); } catch (Exception ex) { Console.WriteLine("[render] Cat Details: " + ex.Message); }
        try { var (n, _) = BuildNotes(cat, s, false); list.Add(("Notes", n)); } catch (Exception ex) { Console.WriteLine("[render] Cat Notes: " + ex.Message); }
        try { var (p, _) = ParentsPicker.Build(ParentChildKind.Category, cat.Name, false, s); list.Add(("Parents", p)); } catch (Exception ex) { Console.WriteLine("[render] Cat Parents: " + ex.Message); }
        return list;
    }

    // ── Details (left fields + right Images panel) ──
    private static (Control panel, Action apply) BuildDetails(HostPlatformCategory cat, bool readOnly, float s)
    {
        int S(int px) => (int)Math.Round(px * s);
        var container = new Panel { Dock = DockStyle.Fill, BackColor = Bg };
        var images = EditPlatformImages.BuildForCategory(Safe(() => cat.Name) ?? "", readOnly, s);
        images.Dock = DockStyle.Right; images.Width = S(300);
        var left = new Panel { Dock = DockStyle.Fill, BackColor = Bg, Padding = new Padding(S(12)) };
        container.Controls.Add(left);
        container.Controls.Add(images);

        int y = S(10);
        TextBox Row(string label, string value)
        {
            left.Controls.Add(new Label { Text = label, Location = new Point(S(6), y + S(4)), AutoSize = true, ForeColor = SubFg, BackColor = Bg });
            var tb = new TextBox { Location = new Point(S(110), y), Width = S(300), BackColor = Panel2, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle, Text = value };
            left.Controls.Add(tb);
            y += S(34);
            return tb;
        }
        var nameTb = Row("Unique Name:", Safe(() => cat.Name) ?? "");
        var nestedTb = Row("Nested Name:", Safe(() => cat.NestedName) ?? "");
        var sortTb = Row("Sort Title:", Safe(() => cat.SortTitle) ?? "");
        var videoTb = Row("Video Path:", Safe(() => cat.VideoPath) ?? "");
        videoTb.Width = S(240);
        var browse = new Button { Text = "Browse…", Location = new Point(S(356), videoTb.Top - S(1)), Size = new Size(S(76), S(25)), FlatStyle = FlatStyle.Flat, BackColor = Panel2, ForeColor = Fg, FlatAppearance = { BorderSize = 0 } };
        browse.Click += (_, _) => { using var d = new OpenFileDialog { Filter = "Videos|*.mp4;*.mkv;*.avi;*.wmv;*.flv|All files|*.*" }; if (d.ShowDialog() == DialogResult.OK) videoTb.Text = d.FileName; };
        left.Controls.Add(browse);
        y += S(10);
        var hideChk = new CheckBox { Text = "Hide in Big Box (Does Not Hide Games)", Location = new Point(S(6), y), AutoSize = true, ForeColor = Fg, BackColor = Bg, Checked = Safe(() => cat.HideInBigBox) };
        left.Controls.Add(hideChk);

        void Apply()
        {
            if (readOnly) return;
            // 1) Rename FIRST (surgical XML + live object) so later op-log records key on the new name.
            string oldName = Safe(() => cat.Name) ?? "";
            string newName = nameTb.Text.Trim();
            if (newName.Length > 0 && !string.Equals(newName, oldName, StringComparison.Ordinal))
            {
                if (!RenameCategory(oldName, newName))
                { MessageBox.Show($"Cannot rename to \"{newName}\" (name already in use?).", "Rename Category", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
                else cat.SetNameInternal(newName);
            }
            // 2) Field edits → op-log (flushed on close).
            try { cat.NestedName = nestedTb.Text.Trim(); } catch { }
            try { cat.SortTitle = sortTb.Text.Trim(); } catch { }
            try { cat.VideoPath = videoTb.Text.Trim(); } catch { }
            try { cat.HideInBigBox = hideChk.Checked; } catch { }
        }
        return (container, Apply);
    }

    // ── Notes ──
    private static (Control panel, Action apply) BuildNotes(HostPlatformCategory cat, float s, bool readOnly)
    {
        int S(int px) => (int)Math.Round(px * s);
        var p = new Panel { Dock = DockStyle.Fill, BackColor = Bg, Padding = new Padding(S(12)) };
        var tb = new TextBox
        {
            Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical,
            BackColor = Panel2, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle,
            Text = Safe(() => cat.Notes) ?? "",
        };
        p.Controls.Add(tb);
        void Apply() { if (!readOnly) try { cat.Notes = tb.Text; } catch { } }
        return (p, Apply);
    }

    // ── surgical rename: Platforms.xml node + all Parents.xml refs + images folder ──
    private static bool RenameCategory(string oldName, string newName)
    {
        try
        {
            if (!File.Exists(PlatformsFile)) return false;
            var pdoc = XDocument.Load(PlatformsFile);
            var nodes = pdoc.Root?.Elements("PlatformCategory").Where(e => string.Equals((string?)e.Element("Name"), oldName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (nodes == null || nodes.Count == 0) return false;
            bool taken = pdoc.Root!.Elements("PlatformCategory").Any(e => string.Equals((string?)e.Element("Name"), newName, StringComparison.OrdinalIgnoreCase));
            if (taken) return false;
            foreach (var n in nodes) n.Element("Name")!.Value = newName;
            pdoc.Save(PlatformsFile);

            if (File.Exists(ParentsFile))
            {
                var rdoc = XDocument.Load(ParentsFile);
                bool changed = false;
                foreach (var e in rdoc.Root?.Elements("Parent") ?? Enumerable.Empty<XElement>())
                {
                    var c = e.Element("PlatformCategoryName");
                    if (c != null && string.Equals(c.Value, oldName, StringComparison.OrdinalIgnoreCase)) { c.Value = newName; changed = true; }
                    var pa = e.Element("ParentPlatformCategoryName");
                    if (pa != null && string.Equals(pa.Value, oldName, StringComparison.OrdinalIgnoreCase)) { pa.Value = newName; changed = true; }
                }
                if (changed) rdoc.Save(ParentsFile);
            }

            try
            {
                string root = Path.Combine(MediaResolver.LbRoot ?? "", "Images", "Platform Categories");
                string src = Path.Combine(root, Sanitize(oldName)), dst = Path.Combine(root, Sanitize(newName));
                if (Directory.Exists(src) && !Directory.Exists(dst)) Directory.Move(src, dst);
            }
            catch { }
            return true;
        }
        catch { return false; }
    }

    private static string Sanitize(string sn)
    {
        if (string.IsNullOrEmpty(sn)) return sn;
        foreach (var c in Path.GetInvalidFileNameChars()) sn = sn.Replace(c, '_');
        return sn.Replace('\'', '_').Trim();
    }
    private static T? Safe<T>(Func<T> f) { try { return f(); } catch { return default; } }
}

internal static class EditPlaylistWindow
{
    private static readonly Color Bg = LiteBoxTheme.Bg, Panel2 = LiteBoxTheme.Panel2, Fg = LiteBoxTheme.Fg, SubFg = LiteBoxTheme.SubFg;

    public static void Open(HostPlaylist pl, bool readOnly, IWin32Window? owner)
    {
        if (pl == null) return;
        string name = Safe(() => pl.Name) ?? "Playlist";
        string id = Safe(() => pl.PlaylistIdValue) ?? "";
        if (string.IsNullOrEmpty(id)) return;   // parents are keyed by PlaylistId
        using var w = new OptionsWindow($"Edit Playlist — {name}{(readOnly ? "   [READ-ONLY]" : "")}");
        float s = LiteBoxTheme.DpiScale(w);

        var (details, applyDetails) = BuildDetails(pl, readOnly, s);
        w.AddSection("Details", details, applyDetails);

        var (notes, applyNotes) = BuildNotes(pl, s, readOnly);
        w.AddSection("Notes", notes, applyNotes);

        // (LB also has Auto-Populate and Games tabs — future work.)
        var (parents, applyParents) = ParentsPicker.Build(ParentChildKind.Playlist, id, readOnly, s);
        w.AddSection("Parents", parents, applyParents);

        w.ShowDialog(owner);
        // Playlist edits journal per-file (RecordPlaylistModify keyed by PlaylistId + file) — flush like the rest.
        if (!readOnly) { try { (PluginHelper.DataManager as HostDataManagerXml)?.FlushIfSafe(); } catch { } }
    }

    // ── Details (LB layout: Unique/Nested Name, Sort Title, Video Path, Sort Games By, 2 checkboxes) + Images ──
    private static (Control panel, Action apply) BuildDetails(HostPlaylist pl, bool readOnly, float s)
    {
        int S(int px) => (int)Math.Round(px * s);
        var container = new Panel { Dock = DockStyle.Fill, BackColor = Bg };
        var images = EditPlatformImages.BuildForPlaylist(Safe(() => pl.Name) ?? "", readOnly, s);
        images.Dock = DockStyle.Right; images.Width = S(300);
        var left = new Panel { Dock = DockStyle.Fill, BackColor = Bg, Padding = new Padding(S(12)) };
        container.Controls.Add(left);
        container.Controls.Add(images);

        int y = S(10);
        TextBox Row(string label, string value)
        {
            left.Controls.Add(new Label { Text = label, Location = new Point(S(6), y + S(4)), AutoSize = true, ForeColor = SubFg, BackColor = Bg });
            var tb = new TextBox { Location = new Point(S(110), y), Width = S(300), BackColor = Panel2, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle, Text = value };
            left.Controls.Add(tb);
            y += S(34);
            return tb;
        }
        var nameTb = Row("Unique Name:", Safe(() => pl.Name) ?? "");
        var nestedTb = Row("Nested Name:", Safe(() => pl.NestedName) ?? "");
        var sortTb = Row("Sort Title:", Safe(() => pl.SortTitle) ?? "");
        var videoTb = Row("Video Path:", Safe(() => pl.VideoPath) ?? "");
        videoTb.Width = S(240);
        var browse = new Button { Text = "Browse…", Location = new Point(S(356), videoTb.Top - S(1)), Size = new Size(S(76), S(25)), FlatStyle = FlatStyle.Flat, BackColor = Panel2, ForeColor = Fg, FlatAppearance = { BorderSize = 0 } };
        browse.Click += (_, _) => { using var d = new OpenFileDialog { Filter = "Videos|*.mp4;*.mkv;*.avi;*.wmv;*.flv|All files|*.*" }; if (d.ShowDialog() == DialogResult.OK) videoTb.Text = d.FileName; };
        left.Controls.Add(browse);

        // Sort Games By — editable combo (observed stored value: "Default"); free text persists verbatim.
        left.Controls.Add(new Label { Text = "Sort Games By:", Location = new Point(S(6), y + S(4)), AutoSize = true, ForeColor = SubFg, BackColor = Bg });
        var sortBy = new ComboBox { Location = new Point(S(110), y), Width = S(300), DropDownStyle = ComboBoxStyle.DropDown, FlatStyle = FlatStyle.Flat, BackColor = Panel2, ForeColor = Fg };
        sortBy.Items.Add("Default");
        string curSort = Safe(() => pl.SortBy) ?? "";
        if (curSort.Length > 0 && !sortBy.Items.Contains(curSort)) sortBy.Items.Add(curSort);
        sortBy.Text = curSort;
        left.Controls.Add(sortBy);
        y += S(44);

        var includeChk = new CheckBox { Text = "Include this Playlist with Platforms", Location = new Point(S(6), y), AutoSize = true, ForeColor = Fg, BackColor = Bg, Checked = Safe(() => pl.IncludeWithPlatforms) };
        left.Controls.Add(includeChk);
        y += S(28);
        var hideChk = new CheckBox { Text = "Hide in Big Box (Does Not Hide Games)", Location = new Point(S(6), y), AutoSize = true, ForeColor = Fg, BackColor = Bg, Checked = Safe(() => pl.HideInBigBox) };
        left.Controls.Add(hideChk);

        void Apply()
        {
            if (readOnly) return;
            // Playlist identity is the PlaylistId GUID (Parents.xml + journal key) — renaming is a plain field
            // write; the on-disk file keeps its name (the flush targets the file recorded at load).
            try { var v = nameTb.Text.Trim(); if (v.Length > 0) pl.Name = v; } catch { }
            try { pl.NestedName = nestedTb.Text.Trim(); } catch { }
            try { pl.SortTitle = sortTb.Text.Trim(); } catch { }
            try { pl.VideoPath = videoTb.Text.Trim(); } catch { }
            try { pl.SortBy = sortBy.Text.Trim(); } catch { }
            try { pl.IncludeWithPlatforms = includeChk.Checked; } catch { }
            try { pl.HideInBigBox = hideChk.Checked; } catch { }
        }
        return (container, Apply);
    }

    private static (Control panel, Action apply) BuildNotes(HostPlaylist pl, float s, bool readOnly)
    {
        int S(int px) => (int)Math.Round(px * s);
        var p = new Panel { Dock = DockStyle.Fill, BackColor = Bg, Padding = new Padding(S(12)) };
        var tb = new TextBox
        {
            Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical,
            BackColor = Panel2, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle,
            Text = Safe(() => pl.Notes) ?? "",
        };
        p.Controls.Add(tb);
        void Apply() { if (!readOnly) try { pl.Notes = tb.Text; } catch { } }
        return (p, Apply);
    }

    private static T? Safe<T>(Func<T> f) { try { return f(); } catch { return default; } }
}
