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
    private readonly CheckBox _dupOn = null!, _dupGpu = null!;
    private readonly ComboBox _dupEngine = null!;
    private readonly NumericUpDown _dupThr = null!;
    private readonly ComboBox _immFallback = null!;
    private readonly (string Key, string Title)[] _famWith3d;

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
        // The family catalogs + the pseudo-families (immediate combos and the post-load add list — NOT the
        // fallback combo, which must resolve to a real image family):
        //   • "3D Model" — the baked case model;
        //   • "Videos" — every video type, then one entry per LaunchBox video sub-folder (Trailer, Theme,
        //     Marquee, Recordings) plus the platform root, so a layout can ask for exactly one kind.
        _famWith3d = families
            .Append((Media3dItem.FamilyKey, Media3dItem.FamilyTitle))
            .Append((MediaVideoItem.FamilyKey, MediaVideoItem.FamilyTitle))
            .Concat(MediaVideoItem.Types.Select(t => (MediaVideoItem.TypeKey(t.SubDir), t.Title)))
            .ToArray();

        // ── Immediate image (per view) ──
        Head("Immediate image (shown instantly, before the load delay)", 0, 0);
        Controls.Add(new Label { Text = "List view:", AutoSize = true, ForeColor = Fg, Location = new Point(S(0), S(26)) });
        _immList = FamilyCombo(_famWith3d, _layout.ImmediateList, S(90), S(22));
        _immList.SelectedIndexChanged += (_, _) => Update3dFallbackEnabled();
        Controls.Add(_immList);
        Controls.Add(new Label { Text = "Poster view:", AutoSize = true, ForeColor = Fg, Location = new Point(S(240), S(26)) });
        _immPoster = FamilyCombo(_famWith3d, _layout.ImmediatePoster, S(340), S(22));
        _immPoster.SelectedIndexChanged += (_, _) => Update3dFallbackEnabled();
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

        // ── Prevent duplicates (applies to BOTH views; results cached per image in its :lb.dupcheck ADS,
        //    keyed on the view's config hash + the game's image-pool signature + these params) ──
        _dupOn = new CheckBox { Text = "Prevent duplicate images — skip a post-load image that duplicates one already in the list", AutoSize = true, ForeColor = Fg, Location = new Point(S(0), S(444)), Checked = _layout.PreventDuplicates };
        _dupOn.CheckedChanged += (_, _) => UpdateDupEnabled();
        Controls.Add(_dupOn);

        Controls.Add(new Label { Text = "Engine:", AutoSize = true, ForeColor = SubFg, Location = new Point(S(18), S(471)) });
        _dupEngine = new ComboBox { Location = new Point(S(70), S(468)), Size = new Size(S(150), S(22)), DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, BackColor = Panel2, ForeColor = Fg };
        _dupEngine.Items.AddRange(new object[] { "CNN (deep, best)", "pHash (fast)", "dHash (fastest)" });
        _dupEngine.SelectedIndex = _layout.DupEngine?.ToLowerInvariant() switch { "phash" => 1, "dhash" => 2, _ => 0 };
        _dupEngine.SelectedIndexChanged += (_, _) => OnDupEngineChanged();
        Controls.Add(_dupEngine);

        Controls.Add(new Label { Text = "threshold:", AutoSize = true, ForeColor = SubFg, Location = new Point(S(232), S(471)) });
        _dupThr = new NumericUpDown { Location = new Point(S(296), S(468)), Size = new Size(S(64), S(22)), Minimum = 0, Maximum = 64, DecimalPlaces = 2, Increment = 0.05m, BackColor = Panel2, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle };
        _dupThr.Value = (decimal)Math.Min(64.0, Math.Max(0.0, _layout.EffectiveDupThreshold()));
        Controls.Add(_dupThr);

        _dupGpu = new CheckBox { Text = "use GPU (DirectML, auto CPU fallback)", AutoSize = true, ForeColor = Fg, Location = new Point(S(372), S(469)), Checked = _layout.DupGpu };
        Controls.Add(_dupGpu);

        Sub("CNN: duplicate when cosine similarity ≥ threshold (default 0.85). Hashes: when Hamming distance ≤ threshold (default 10). "
            + "First visit of a game computes once; results are then cached per image (ADS).", 18, 494, 640);
        UpdateDupEnabled();

        // ── 3D-model immediate fallback (only meaningful when an immediate combo is "3D Model":
        //    instant shows the baked snapshot PNG ONLY — no bake, no viewport — so when the model can't
        //    exist or isn't baked yet, this real image family is shown instead) ──
        Controls.Add(new Label { Text = "If '3D Model' is the immediate image but no baked model exists yet, show instead:", AutoSize = true, ForeColor = Fg, Location = new Point(S(0), S(527)) });
        _immFallback = FamilyCombo(families, _layout.Immediate3dFallback, S(452), S(524));
        Controls.Add(_immFallback);
        Update3dFallbackEnabled();

        // NOTE: what makes a 3D model "worth showing" (front required, optional Back/Spine, Box - Full
        // branch) is NOT here — it gates the bake, the bulk generation and the key index too, well beyond
        // this pane, so it lives in Display → General.
        Sub("Selection uses LaunchBox's automatic algorithm (type → region → number). Takes effect on the next game selection.", 0, 552, 600);
    }

    private void UpdateDupEnabled()
    {
        bool on = _dupOn.Checked;
        _dupEngine.Enabled = on;
        _dupThr.Enabled = on;
        _dupGpu.Enabled = on && _dupEngine.SelectedIndex == 0;   // GPU is a CNN-only knob
    }

    // The fallback combo only matters when an immediate combo is set to the 3D pseudo-family.
    private void Update3dFallbackEnabled()
    {
        if (_immFallback == null) return;
        bool any3d = string.Equals(FamilyKeyOf(_immList), Media3dItem.FamilyKey, StringComparison.OrdinalIgnoreCase)
                  || string.Equals(FamilyKeyOf(_immPoster), Media3dItem.FamilyKey, StringComparison.OrdinalIgnoreCase);
        _immFallback.Enabled = any3d;
    }

    // Switching engine swaps the threshold to that engine's default (the scales are unrelated:
    // cosine 0..1 vs Hamming 0..64) unless the user had typed a custom value for the SAME scale.
    private void OnDupEngineChanged()
    {
        bool cnn = _dupEngine.SelectedIndex == 0;
        _dupThr.Value = (decimal)Dedup.DedupEngine.DefaultThreshold(cnn ? Dedup.DupEngineMode.Cnn : Dedup.DupEngineMode.PHash);
        _dupThr.Increment = cnn ? 0.05m : 1m;
        UpdateDupEnabled();
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
        if (_addKind.SelectedIndex == 0) foreach (var (_, title) in _famWith3d) _addSel.Items.Add(title);
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
            e.Sel = ix >= 0 && ix < _famWith3d.Length ? _famWith3d[ix].Key : "Front";
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
        _layout.PreventDuplicates = _dupOn.Checked;
        _layout.DupEngine = _dupEngine.SelectedIndex switch { 1 => "phash", 2 => "dhash", _ => "cnn" };
        _layout.DupThreshold = (double)_dupThr.Value;
        _layout.DupGpu = _dupGpu.Checked;
        _layout.Immediate3dFallback = FamilyKeyOf(_immFallback);
        _layout.Save();
    }
}
