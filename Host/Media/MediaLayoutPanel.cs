// The Options → Display → Right panel editor for the detail-pane image layout (MediaLayout):
//   • Immediate image family, separately for List view and Poster view.
//   • The ordered post-load image list — each row a FAMILY or an EXACT type + a count. It can be SHARED
//     between List and Poster views (default) or made INDEPENDENT per view via the "use same configuration"
//     checkbox; the "Editing" selector then picks which view's list you're editing.
//   • Per entry: "prefer the game's own region" (LaunchBox-identical region order) vs the global LB priority.
//   Apply() writes it back and saves.

#nullable enable

using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using LbApiHost.Host.UiKit;

namespace LbApiHost.Host.Media;

internal sealed class MediaLayoutPanel : Panel
{
    private readonly float _s;
    private int S(int px) => (int)Math.Round(px * _s);

    private readonly MediaLayout _layout;
    private readonly ComboBox _immList = null!, _immPoster = null!;
    private readonly CheckBox _posterSame = null!;
    private readonly ComboBox _editWhich = null!;
    private readonly ListBox _list = null!;
    private readonly ComboBox _addKind = null!, _addSel = null!;
    private readonly NumericUpDown _addCount = null!, _addDepth = null!;
    private readonly CheckBox _addCumul = null!, _addRegion = null!, _addAllRegions = null!;

    private bool _editingPoster;   // which post-load list the editor is currently showing

    private static Color Bg => LiteBoxTheme.Bg;
    private static Color Panel2 => LiteBoxTheme.Panel2;
    private static Color Fg => LiteBoxTheme.Fg;
    private static Color SubFg => LiteBoxTheme.SubFg;

    public MediaLayoutPanel()
    {
        _s = LiteBoxTheme.DpiScale(this);
        _layout = MediaLayout.Current.Clone();
        BackColor = Bg;
        AutoScroll = true;   // the editor is tall (immediate + sharing + list + add + region rows) — never clip

        Label Head(string t, int x, int y) { var l = new Label { Text = t, AutoSize = true, ForeColor = Fg, Location = new Point(S(x), S(y)), Font = new Font("Segoe UI Semibold", 9f) }; Controls.Add(l); return l; }
        Label Sub(string t, int x, int y, int w) { var l = new Label { Text = t, AutoSize = false, Size = new Size(S(w), S(16)), ForeColor = SubFg, Location = new Point(S(x), S(y)), Font = new Font("Segoe UI", 8.25f) }; Controls.Add(l); return l; }

        var families = MediaLayout.Families;

        // ── Immediate image (per view) ──
        Head("Immediate image (shown instantly, before the load delay)", 0, 0);
        Controls.Add(new Label { Text = "List view:", AutoSize = true, ForeColor = Fg, Location = new Point(S(0), S(26)) });
        _immList = FamilyCombo(families, _layout.ImmediateList, S(90), S(22));
        Controls.Add(_immList);
        Controls.Add(new Label { Text = "Poster view:", AutoSize = true, ForeColor = Fg, Location = new Point(S(240), S(26)) });
        _immPoster = FamilyCombo(families, _layout.ImmediatePoster, S(340), S(22));
        Controls.Add(_immPoster);

        // ── Per-view sharing of the post-load list ──
        _posterSame = new CheckBox { Text = "Poster view uses the same post-load images as List view", AutoSize = true, ForeColor = Fg, Location = new Point(S(0), S(54)), Checked = !_layout.PosterIndependent };
        _posterSame.CheckedChanged += (_, _) => OnPosterSameChanged();
        Controls.Add(_posterSame);

        Controls.Add(new Label { Text = "Editing:", AutoSize = true, ForeColor = SubFg, Location = new Point(S(0), S(81)) });
        _editWhich = new ComboBox { Location = new Point(S(56), S(78)), Size = new Size(S(150), S(22)), DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, BackColor = Panel2, ForeColor = Fg };
        _editWhich.Items.AddRange(new object[] { "List view", "Poster view" });
        _editWhich.SelectedIndex = 0;
        _editWhich.SelectedIndexChanged += (_, _) => OnEditWhichChanged();
        Controls.Add(_editWhich);
        _editWhich.Enabled = _layout.PosterIndependent;

        // ── Post-load ordered list ──
        Head("Post-load images — loaded after the delay, in this order (first = the main box)", 0, 108);
        _list = new ListBox { Location = new Point(S(0), S(132)), Size = new Size(S(360), S(168)), BackColor = Panel2, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle, IntegralHeight = false };
        Controls.Add(_list);
        RefreshList();

        Button SideBtn(string t, int y, Action onClick)
        {
            var b = new Button { Text = t, Size = new Size(S(90), S(26)), Location = new Point(S(372), S(y)), FlatStyle = FlatStyle.Flat, BackColor = Panel2, ForeColor = Fg, FlatAppearance = { BorderSize = 0 } };
            b.Click += (_, _) => onClick();
            Controls.Add(b); return b;
        }
        SideBtn("Move up", 132, () => Move(-1));
        SideBtn("Move down", 162, () => Move(+1));
        SideBtn("Remove", 192, RemoveSel);

        // ── Add row ──
        Sub("Add: choose a family or a specific image type, and how many to take.", 0, 312, 500);
        _addKind = new ComboBox { Location = new Point(S(0), S(332)), Size = new Size(S(120), S(22)), DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, BackColor = Panel2, ForeColor = Fg };
        _addKind.Items.AddRange(new object[] { "Family", "Specific type" });
        _addKind.SelectedIndex = 0;
        _addKind.SelectedIndexChanged += (_, _) => FillAddSel();
        Controls.Add(_addKind);

        _addSel = new ComboBox { Location = new Point(S(128), S(332)), Size = new Size(S(280), S(22)), DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, BackColor = Panel2, ForeColor = Fg };
        Controls.Add(_addSel);
        FillAddSel();

        Controls.Add(new Label { Text = "count:", AutoSize = true, ForeColor = SubFg, Location = new Point(S(416), S(335)) });
        _addCount = new NumericUpDown { Location = new Point(S(460), S(332)), Size = new Size(S(52), S(22)), Minimum = 1, Maximum = 99, Value = 99, BackColor = Panel2, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle };
        Controls.Add(_addCount);
        var add = new Button { Text = "Add", Size = new Size(S(66), S(24)), Location = new Point(S(520), S(331)), FlatStyle = FlatStyle.Flat, BackColor = LiteBoxTheme.Accent, ForeColor = Color.White, FlatAppearance = { BorderSize = 0 } };
        add.Click += (_, _) => AddEntry();
        Controls.Add(add);

        // Cumulative: the count is a TOTAL that also counts the images from the N entries above. The spinner
        // and trailing label are placed AFTER the checkbox's real text width (PreferredSize) so nothing
        // overlaps at any DPI / font.
        _addCumul = new CheckBox { Text = "Cumulative — count also the", AutoSize = true, ForeColor = Fg, Location = new Point(S(0), S(362)) };
        Controls.Add(_addCumul);
        int cumX = _addCumul.Location.X + _addCumul.PreferredSize.Width + S(8);
        _addDepth = new NumericUpDown { Location = new Point(cumX, S(360)), Size = new Size(S(48), S(22)), Minimum = 1, Maximum = 20, Value = 1, BackColor = Panel2, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle };
        Controls.Add(_addDepth);
        Controls.Add(new Label { Text = "entr(ies) above (so 'count' becomes a target total)", AutoSize = true, ForeColor = SubFg, Location = new Point(cumX + S(48) + S(8), S(364)) });

        // Per-entry region behaviour.
        _addRegion = new CheckBox { Text = "Ignore the game's own region — use only the global region priority (default: game region first, LaunchBox-identical)", AutoSize = true, ForeColor = Fg, Location = new Point(S(0), S(390)) };
        Controls.Add(_addRegion);
        _addAllRegions = new CheckBox { Text = "Add images from ALL regions  —  ⚠ may create duplicates (default: best region only)", AutoSize = true, ForeColor = Fg, Location = new Point(S(0), S(414)) };
        Controls.Add(_addAllRegions);

        Sub("Selection uses LaunchBox's automatic algorithm (type → region → number). Takes effect on the next game selection.", 0, 440, 600);
    }

    private ComboBox FamilyCombo((string Key, string Title)[] families, string current, int x, int y)
    {
        var cb = new ComboBox { Location = new Point(x, y), Size = new Size(S(140), S(22)), DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, BackColor = Panel2, ForeColor = Fg };
        foreach (var (_, title) in families) cb.Items.Add(title);
        int ix = Array.FindIndex(families, f => string.Equals(f.Key, current, StringComparison.OrdinalIgnoreCase));
        cb.SelectedIndex = ix >= 0 ? ix : 0;
        cb.Tag = families;
        return cb;
    }

    private static string FamilyKeyOf(ComboBox cb)
    {
        var families = ((string Key, string Title)[])cb.Tag!;
        return cb.SelectedIndex >= 0 && cb.SelectedIndex < families.Length ? families[cb.SelectedIndex].Key : "Front";
    }

    // The post-load list the editor is currently acting on (List's, or Poster's when editing independently).
    private System.Collections.Generic.List<MediaEntry> CurPostLoad()
        => _editingPoster ? _layout.PostLoadPoster : _layout.PostLoad;

    private void OnPosterSameChanged()
    {
        _layout.PosterIndependent = !_posterSame.Checked;
        if (_layout.PosterIndependent && _layout.PostLoadPoster.Count == 0)
            _layout.PostLoadPoster = _layout.PostLoad.Select(e => e.Clone()).ToList();   // seed Poster from List
        _editWhich.Enabled = _layout.PosterIndependent;
        if (!_layout.PosterIndependent && _editWhich.SelectedIndex != 0) _editWhich.SelectedIndex = 0;   // back to List
    }

    private void OnEditWhichChanged()
    {
        _editingPoster = _editWhich.SelectedIndex == 1;
        RefreshList();
    }

    private void FillAddSel()
    {
        _addSel.BeginUpdate();
        _addSel.Items.Clear();
        if (_addKind.SelectedIndex == 0) foreach (var (_, title) in MediaLayout.Families) _addSel.Items.Add(title);
        else foreach (var t in MediaLayout.ExactTypes()) _addSel.Items.Add(t);
        if (_addSel.Items.Count > 0) _addSel.SelectedIndex = 0;
        _addSel.EndUpdate();
    }

    private void RefreshList()
    {
        int sel = _list.SelectedIndex;
        _list.BeginUpdate(); _list.Items.Clear();
        foreach (var e in CurPostLoad()) _list.Items.Add(e.Label());
        _list.EndUpdate();
        if (_list.Items.Count > 0) _list.SelectedIndex = Math.Max(0, Math.Min(sel, _list.Items.Count - 1));
    }

    private void AddEntry()
    {
        var e = new MediaEntry { Count = (int)_addCount.Value, Cumulative = _addCumul.Checked, CumulativeDepth = (int)_addDepth.Value, IgnoreGameRegion = _addRegion.Checked, AllRegions = _addAllRegions.Checked };
        if (_addKind.SelectedIndex == 0)
        {
            e.ExactType = false;
            int ix = _addSel.SelectedIndex;
            e.Sel = ix >= 0 && ix < MediaLayout.Families.Length ? MediaLayout.Families[ix].Key : "Front";
        }
        else { e.ExactType = true; e.Sel = _addSel.SelectedItem as string ?? ""; if (string.IsNullOrEmpty(e.Sel)) return; }
        CurPostLoad().Add(e);
        RefreshList();
        _list.SelectedIndex = _list.Items.Count - 1;
    }

    private void RemoveSel()
    {
        var cur = CurPostLoad();
        int i = _list.SelectedIndex;
        if (i < 0 || i >= cur.Count) return;
        cur.RemoveAt(i);
        RefreshList();
    }

    private void Move(int d)
    {
        var cur = CurPostLoad();
        int i = _list.SelectedIndex, j = i + d;
        if (i < 0 || j < 0 || j >= cur.Count) return;
        (cur[i], cur[j]) = (cur[j], cur[i]);
        RefreshList();
        _list.SelectedIndex = j;
    }

    /// <summary>Write the edited layout back and persist (called by the Options window's Apply).</summary>
    public void Apply()
    {
        _layout.ImmediateList = FamilyKeyOf(_immList);
        _layout.ImmediatePoster = FamilyKeyOf(_immPoster);
        if (_layout.PostLoad.Count == 0) _layout.PostLoad.Add(new MediaEntry { Sel = "Front", Count = 1 });
        _layout.Save();
    }
}
