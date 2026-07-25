// The Options → Display → Right panel editor for the detail-pane image layout (MediaLayout):
//   • Immediate image family, separately for List view and Poster view.
//   • The ordered post-load image list — each row a FAMILY or an EXACT type + a count; add / remove /
//     move up / move down. Apply() writes it back and saves.
// Selection mode (auto vs weighted) is reserved in the model; this first UI edits families/types/counts.

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
    private readonly ListBox _list = null!;
    private readonly ComboBox _addKind = null!, _addSel = null!;
    private readonly NumericUpDown _addCount = null!;

    private static Color Bg => LiteBoxTheme.Bg;
    private static Color Panel2 => LiteBoxTheme.Panel2;
    private static Color Fg => LiteBoxTheme.Fg;
    private static Color SubFg => LiteBoxTheme.SubFg;

    public MediaLayoutPanel()
    {
        _s = LiteBoxTheme.DpiScale(this);
        _layout = MediaLayout.Current.Clone();
        BackColor = Bg;

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

        // ── Post-load ordered list ──
        Head("Post-load images — loaded after the delay, in this order (first = the main box)", 0, 64);
        _list = new ListBox { Location = new Point(S(0), S(88)), Size = new Size(S(360), S(210)), BackColor = Panel2, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle, IntegralHeight = false };
        Controls.Add(_list);
        RefreshList();

        Button SideBtn(string t, int y, Action onClick)
        {
            var b = new Button { Text = t, Size = new Size(S(90), S(26)), Location = new Point(S(372), S(y)), FlatStyle = FlatStyle.Flat, BackColor = Panel2, ForeColor = Fg, FlatAppearance = { BorderSize = 0 } };
            b.Click += (_, _) => onClick();
            Controls.Add(b); return b;
        }
        SideBtn("Move up", 88, () => Move(-1));
        SideBtn("Move down", 118, () => Move(+1));
        SideBtn("Remove", 148, RemoveSel);

        // ── Add row ──
        Sub("Add: choose a family or a specific image type, and how many to take.", 0, 306, 500);
        _addKind = new ComboBox { Location = new Point(S(0), S(326)), Size = new Size(S(120), S(22)), DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, BackColor = Panel2, ForeColor = Fg };
        _addKind.Items.AddRange(new object[] { "Family", "Specific type" });
        _addKind.SelectedIndex = 0;
        _addKind.SelectedIndexChanged += (_, _) => FillAddSel();
        Controls.Add(_addKind);

        _addSel = new ComboBox { Location = new Point(S(128), S(326)), Size = new Size(S(280), S(22)), DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, BackColor = Panel2, ForeColor = Fg };
        Controls.Add(_addSel);
        FillAddSel();

        _addCount = new NumericUpDown { Location = new Point(S(416), S(326)), Size = new Size(S(60), S(22)), Minimum = 1, Maximum = 99, Value = 99, BackColor = Panel2, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle };
        Controls.Add(_addCount);
        var add = new Button { Text = "Add", Size = new Size(S(70), S(24)), Location = new Point(S(484), S(325)), FlatStyle = FlatStyle.Flat, BackColor = LiteBoxTheme.Accent, ForeColor = Color.White, FlatAppearance = { BorderSize = 0 } };
        add.Click += (_, _) => AddEntry();
        Controls.Add(add);

        Sub("Selection uses LaunchBox's automatic algorithm (type → region → number). Takes effect on the next game selection.", 0, 360, 560);
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
        foreach (var e in _layout.PostLoad) _list.Items.Add(e.Label());
        _list.EndUpdate();
        if (_list.Items.Count > 0) _list.SelectedIndex = Math.Max(0, Math.Min(sel, _list.Items.Count - 1));
    }

    private void AddEntry()
    {
        var e = new MediaEntry { Count = (int)_addCount.Value };
        if (_addKind.SelectedIndex == 0)
        {
            e.ExactType = false;
            int ix = _addSel.SelectedIndex;
            e.Sel = ix >= 0 && ix < MediaLayout.Families.Length ? MediaLayout.Families[ix].Key : "Front";
        }
        else { e.ExactType = true; e.Sel = _addSel.SelectedItem as string ?? ""; if (string.IsNullOrEmpty(e.Sel)) return; }
        _layout.PostLoad.Add(e);
        RefreshList();
        _list.SelectedIndex = _list.Items.Count - 1;
    }

    private void RemoveSel()
    {
        int i = _list.SelectedIndex;
        if (i < 0 || i >= _layout.PostLoad.Count) return;
        _layout.PostLoad.RemoveAt(i);
        RefreshList();
    }

    private void Move(int d)
    {
        int i = _list.SelectedIndex, j = i + d;
        if (i < 0 || j < 0 || j >= _layout.PostLoad.Count) return;
        (_layout.PostLoad[i], _layout.PostLoad[j]) = (_layout.PostLoad[j], _layout.PostLoad[i]);
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
