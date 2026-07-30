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
// Edit Playlist — LaunchBox-style Details / Notes / Auto-Populate / Games / Parents sections with one
// persistent Images panel. Auto rules and manual membership live in EditPlaylistPopulate.cs.

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
        w.SetSidePanel(BuildImagesSide(pl, readOnly, s), 350);
        var state = new PlaylistEditorState(Safe(() => pl.AutoPopulate));

        var (details, applyDetails) = BuildDetails(pl, readOnly, s);
        w.AddSection("Details", details, applyDetails);

        var (notes, applyNotes) = BuildNotes(pl, s, readOnly);
        w.AddSection("Notes", notes, applyNotes);

        var (autoPopulate, applyAutoPopulate) = EditPlaylistPopulate.BuildAutoPopulate(pl, readOnly, s, state);
        w.AddSection("Auto-Populate", autoPopulate, applyAutoPopulate);

        var (games, applyGames) = EditPlaylistPopulate.BuildGames(pl, readOnly, s, state);
        w.AddSection("Games", games, applyGames);

        var (parents, applyParents) = ParentsPicker.Build(ParentChildKind.Playlist, id, readOnly, s);
        w.AddSection("Parents", parents, applyParents);

        if (readOnly) DisableAllInputs(w);
        w.ShowDialog(owner);
        // Playlist edits journal per-file (RecordPlaylistModify keyed by PlaylistId + file) — flush like the rest.
        if (!readOnly) { try { (PluginHelper.DataManager as HostDataManagerXml)?.FlushIfSafe(); } catch { } }
    }

    /// <summary>Build all playlist sections without the shell for the offscreen render probe.</summary>
    internal static System.Collections.Generic.List<(string title, Control ctrl)> BuildSectionsForRender(HostPlaylist pl, float s)
    {
        var result = new System.Collections.Generic.List<(string, Control)>();
        var state = new PlaylistEditorState(Safe(() => pl.AutoPopulate));
        Control WithImages(Control body)
        {
            var host = new Panel { BackColor = Bg };
            body.Dock = DockStyle.Fill;
            var images = BuildImagesSide(pl, false, s);
            images.Dock = DockStyle.Right;
            images.Width = (int)Math.Round(350 * s);
            host.Controls.Add(body);
            host.Controls.Add(images);
            body.BringToFront();
            return host;
        }
        try { var (p, _) = BuildDetails(pl, false, s); result.Add(("Playlist Details", WithImages(p))); } catch (Exception ex) { Console.WriteLine("[render] Playlist Details: " + ex.Message); }
        try { var (p, _) = BuildNotes(pl, s, false); result.Add(("Playlist Notes", WithImages(p))); } catch (Exception ex) { Console.WriteLine("[render] Playlist Notes: " + ex.Message); }
        try { var (p, _) = EditPlaylistPopulate.BuildAutoPopulate(pl, false, s, state); result.Add(("Playlist Auto-Populate", WithImages(p))); } catch (Exception ex) { Console.WriteLine("[render] Playlist Auto: " + ex.Message); }
        try { var (p, _) = EditPlaylistPopulate.BuildGames(pl, false, s, state); result.Add(("Playlist Games", WithImages(p))); } catch (Exception ex) { Console.WriteLine("[render] Playlist Games: " + ex.Message); }
        try { var (p, _) = ParentsPicker.Build(ParentChildKind.Playlist, pl.PlaylistIdValue, false, s); result.Add(("Playlist Parents", WithImages(p))); } catch (Exception ex) { Console.WriteLine("[render] Playlist Parents: " + ex.Message); }
        return result;
    }

    // ── Details (LB layout: Unique/Nested Name, Sort Title, Video Path, Sort Games By, 2 checkboxes) ──
    private static (Control panel, Action apply) BuildDetails(HostPlaylist pl, bool readOnly, float s)
    {
        int S(int px) => (int)Math.Round(px * s);
        var left = new Panel { Dock = DockStyle.Fill, BackColor = Bg, Padding = new Padding(S(12)) };

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
        var sortBy = new ComboBox { Location = new Point(S(110), y), Width = S(300), DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, BackColor = Panel2, ForeColor = Fg };
        sortBy.Items.Add("Default");
        sortBy.Items.Add("Manual");
        foreach (var d in GameSortCatalog.Standard) sortBy.Items.Add(d.Label);
        var customNames = GameSortCatalog.CustomFieldNames(Safe(() => PluginHelper.DataManager.GetAllGames()) ?? Array.Empty<Unbroken.LaunchBox.Plugins.Data.IGame>());
        foreach (var custom in customNames) sortBy.Items.Add(custom);
        string curSort = Safe(() => pl.SortBy) ?? "";
        string curLabel = GameSortCatalog.Label(GameSortCatalog.Parse(curSort, customNames));
        if (!sortBy.Items.Contains(curLabel)) sortBy.Items.Add(curLabel);
        sortBy.SelectedItem = curLabel;
        if (sortBy.SelectedIndex < 0) sortBy.SelectedIndex = 0;
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
            try { pl.SortBy = GameSortCatalog.ToLaunchBoxValue(GameSortCatalog.Parse(sortBy.Text, customNames)); } catch { }
            try { pl.IncludeWithPlatforms = includeChk.Checked; } catch { }
            try { pl.HideInBigBox = hideChk.Checked; } catch { }
        }
        return (left, Apply);
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

    private static Control BuildImagesSide(HostPlaylist pl, bool readOnly, float s)
    {
        int S(int px) => (int)Math.Round(px * s);
        var panel = new Panel { BackColor = Bg, Padding = new Padding(S(6), S(14), S(8), S(8)) };
        var header = new Label
        {
            Dock = DockStyle.Top,
            Height = S(30),
            Text = "  Images",
            ForeColor = Fg,
            BackColor = Panel2,
            Font = new Font("Segoe UI", 10f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
        };
        var body = EditPlatformImages.BuildForPlaylist(Safe(() => pl.Name) ?? "", readOnly, s);
        body.Dock = DockStyle.Fill;
        panel.Controls.Add(body);
        panel.Controls.Add(header);
        body.BringToFront();
        return panel;
    }

    private static void DisableAllInputs(Control root)
    {
        foreach (Control c in root.Controls)
        {
            if (c is TextBox tb) tb.ReadOnly = true;
            else if (c is CheckBox or ComboBox or Button or NumericUpDown or DataGridView) c.Enabled = false;
            if (c.HasChildren) DisableAllInputs(c);
        }
    }

    private static T? Safe<T>(Func<T> f) { try { return f(); } catch { return default; } }
}
