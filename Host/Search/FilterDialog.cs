// The advanced-search modal for the WinForms game list — a desktop port of BigBox-web's tabbed filter
// dialog (General / Genre / Publisher / Developer / Order by / History). Edits a working copy of the
// FilterCriteria; Apply returns it, Clear returns a default (inactive) criteria, Cancel returns nothing.
// Facet lists (genres/publishers/developers/release types) are computed by the caller from the library.

#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using LbApiHost.Host.UiKit;

namespace LbApiHost.Host.Search;

internal sealed class FilterDialog : Form
{
    private static Color Bg => LiteBoxTheme.Bg;
    private static Color Panel => LiteBoxTheme.PanelC;
    private static Color Panel2 => LiteBoxTheme.Panel2;
    private static Color Fg => LiteBoxTheme.Fg;
    private static Color SubFg => LiteBoxTheme.SubFg;
    private static Color Accent => LiteBoxTheme.Accent;

    private readonly float _s;
    private int S(int px) => (int)Math.Round(px * _s);

    private readonly FilterCriteria _c;                 // working copy
    private readonly FilterFacets _f;

    /// <summary>Set on Apply (the edited criteria) or Clear (a fresh default = inactive). Null on Cancel.</summary>
    public FilterCriteria? Result;

    private readonly Dictionary<string, Panel> _tabPanels = new();
    private readonly List<Button> _tabButtons = new();

    private RangeSlider _year = null!, _rating = null!;
    private ComboBox _type = null!, _sort = null!, _ach = null!, _saves = null!, _players = null!;
    private CheckBox _fav = null!, _inst = null!, _hiscore = null!;
    private Button _genreMode = null!;
    private CheckedListBox _genreList = null!;
    private TextBox _pubText = null!, _devText = null!;
    private ListBox _pubSug = null!, _devSug = null!, _histList = null!;
    private readonly List<FilterCriteria> _history;
    private Button _apply = null!;

    // Les listes multi-sélection, par clé de dimension : construites et relues par le même code, pour
    // qu'ajouter une dimension reste une ligne de table (voir Facets ci-dessous).
    private readonly Dictionary<string, CheckedListBox> _facetLists = new();

    private static readonly (string key, string label)[] Sorts =
    {
        ("alpha", "Alphabetical"), ("year", "Release date"), ("rating", "Rating"), ("lastplayed", "Recently played"),
    };

    /// <summary>Les dimensions multi-sélection, chacune un onglet. Une ligne ici = un onglet complet :
    /// la construction et la relecture passent par la même table, donc rien à câbler ailleurs.</summary>
    private (string key, string label, Func<List<string>> values, Func<List<string>> selected)[] Facets => new[]
    {
        ("platform", "Platform", (Func<List<string>>)(() => FilterFacets.Sorted(_f.Platforms)), (Func<List<string>>)(() => _c.Platforms)),
        ("region",   "Region",   () => FilterFacets.Sorted(_f.Regions),    () => _c.Regions),
        ("playmode", "Play mode",() => FilterFacets.Sorted(_f.PlayModes),  () => _c.PlayModes),
        ("status",   "Status",   () => FilterFacets.Sorted(_f.Statuses),   () => _c.Statuses),
        ("progress", "Progress", () => FilterFacets.Sorted(_f.Progresses), () => _c.Progresses),
        ("esrb",     "ESRB",     () => FilterFacets.Sorted(_f.Esrb),       () => _c.Esrb),
        ("pad",      "Controller",() => FilterFacets.Sorted(_f.Controllers),() => _c.Controllers),
    };

    public FilterDialog(FilterCriteria current, FilterFacets facets, List<FilterCriteria> history)
    {
        _c = current.Clone();
        _f = facets;
        _history = history;
        _s = DeviceDpi / 96f;

        Text = "Search filter";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        BackColor = Bg; ForeColor = Fg; Font = new Font("Segoe UI", 9f);
        // Plus large qu'avant : le bandeau d'onglets s'enroule désormais sur deux rangs, et les listes
        // de valeurs ont besoin de la place.
        ClientSize = new Size(S(720), S(520));

        BuildTabs();
        BuildFooter();
        ShowTab("general");
    }

    // ── Tab strip ─────────────────────────────────────────────────────────────
    private void BuildTabs()
    {
        // Add the Fill content FIRST (back-most) so the Top strip and Bottom footer claim their edges and
        // this fills the middle — WinForms docking resolves front-to-back.
        var content = new Panel { Dock = DockStyle.Fill, BackColor = Bg, Padding = new Padding(S(16), S(14), S(16), S(8)) };
        Controls.Add(content);

        // WrapContents : il y a maintenant treize onglets, ils tiennent sur deux rangs plutôt que de
        // déborder hors de la fenêtre (l'ancien strip coupait déjà « History » à six onglets).
        var strip = new FlowLayoutPanel { Dock = DockStyle.Top, Height = S(72), BackColor = Panel, Padding = new Padding(S(6), S(6), 0, 0), WrapContents = true };
        Controls.Add(strip);

        void Tab(string key, string label, Control body)
        {
            var host = new Panel { Dock = DockStyle.Fill, BackColor = Bg, Visible = false };
            body.Dock = DockStyle.Fill;
            host.Controls.Add(body);
            content.Controls.Add(host);
            _tabPanels[key] = host;

            var b = new Button
            {
                Text = label, AutoSize = false, Size = new Size(S(96), S(28)), Margin = new Padding(0, 0, S(4), 0),
                FlatStyle = FlatStyle.Flat, BackColor = Panel, ForeColor = SubFg, Font = new Font("Segoe UI", 9f),
                FlatAppearance = { BorderSize = 0 }, Tag = key,
            };
            b.Click += (_, _) => ShowTab(key);
            _tabButtons.Add(b);
            strip.Controls.Add(b);
        }

        Tab("general", "General", BuildGeneral());
        Tab("genre", "Genre", BuildGenre());
        foreach (var (key, label, values, selected) in Facets) Tab(key, label, BuildFacet(key, values(), selected()));
        Tab("publisher", "Publisher", BuildText(isPub: true));
        Tab("developer", "Developer", BuildText(isPub: false));
        Tab("orderby", "Order by", BuildOrderBy());
        Tab("history", "History", BuildHistory());
    }

    private void ShowTab(string key)
    {
        foreach (var kv in _tabPanels) kv.Value.Visible = kv.Key == key;
        foreach (var b in _tabButtons)
        {
            bool on = (string)b.Tag == key;
            b.ForeColor = on ? Color.White : SubFg;
            b.BackColor = on ? Accent : Panel;
        }
        // Apply is meaningless on the History tab (activating a row applies directly).
        if (_apply != null) _apply.Visible = key != "history";
    }

    // ── General ───────────────────────────────────────────────────────────────
    private Control BuildGeneral()
    {
        var p = new Panel { BackColor = Bg };
        int y = 0;

        Label Head(string t, int yy) { var l = new Label { Text = t, AutoSize = true, ForeColor = Fg, Location = new Point(0, S(yy)), Font = new Font("Segoe UI Semibold", 9f) }; p.Controls.Add(l); return l; }

        Head("Release year", y);
        _year = new RangeSlider { Location = new Point(0, S(y + 20)), Size = new Size(S(500), S(34)), BackColor = Bg };
        _year.Configure(FilterCriteria.YearLo, FilterCriteria.YearHi, 1, _c.YearMin, _c.YearMax,
            v => (v <= FilterCriteria.YearLo || v >= FilterCriteria.YearHi) ? "∞" : ((int)v).ToString());
        p.Controls.Add(_year);
        y += 66;

        Head("Rating", y);
        _rating = new RangeSlider { Location = new Point(0, S(y + 20)), Size = new Size(S(500), S(34)), BackColor = Bg };
        _rating.Configure(FilterCriteria.RatingLo, FilterCriteria.RatingHi, 0.5, _c.RatingMin, _c.RatingMax,
            v => (v <= FilterCriteria.RatingLo || v >= FilterCriteria.RatingHi) ? "∞" : v.ToString("0.#"));
        p.Controls.Add(_rating);
        y += 70;

        // Les listes déroulantes « (Any) + valeurs », posées en colonne à droite du libellé.
        ComboBox Combo(string head, int yy, params string[] items)
        {
            Head(head, yy);
            var cb = new ComboBox
            {
                Location = new Point(S(160), S(yy - 2)), Size = new Size(S(300), S(24)),
                DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, BackColor = Panel2, ForeColor = Fg,
            };
            cb.Items.Add("(Any)");
            foreach (var i in items) cb.Items.Add(i);
            cb.SelectedIndex = 0;
            p.Controls.Add(cb);
            return cb;
        }

        // Une valeur DÉJÀ sélectionnée qui n'existe plus dans la bibliothèque (historique, jeu modifié
        // entre-temps) reste proposée : sinon « Apply » sur un autre onglet l'effacerait en silence.
        _type = Combo("Release type", y, WithSelected(FilterFacets.Sorted(_f.ReleaseTypes), _c.ReleaseType));
        _type.SelectedIndex = string.IsNullOrEmpty(_c.ReleaseType) ? 0 : Math.Max(0, _type.Items.IndexOf(_c.ReleaseType));
        y += 32;

        var playerItems = _f.SortedPlayers().Select(n => n.ToString()).ToList();
        if (_c.MaxPlayers > 0 && !playerItems.Contains(_c.MaxPlayers.ToString())) playerItems.Add(_c.MaxPlayers.ToString());
        _players = Combo("Max players", y, playerItems.ToArray());
        if (_c.MaxPlayers > 0) { int ix = _players.Items.IndexOf(_c.MaxPlayers.ToString()); if (ix > 0) _players.SelectedIndex = ix; }
        y += 32;

        _ach = Combo("Achievements", y, "Yes", "No");
        _ach.SelectedIndex = _c.Achievements == "yes" ? 1 : _c.Achievements == "no" ? 2 : 0;
        y += 32;

        _saves = Combo("Game saves", y, "Has any saved game", "Has any save state");
        _saves.SelectedIndex = _c.Saves == "game" ? 1 : _c.Saves == "state" ? 2 : 0;
        y += 38;

        _fav = new CheckBox { Text = "Favorite only", AutoSize = true, ForeColor = Fg, Location = new Point(0, S(y)), Checked = _c.Fav };
        p.Controls.Add(_fav);
        y += 26;
        _inst = new CheckBox { Text = "Installed only", AutoSize = true, ForeColor = Fg, Location = new Point(0, S(y)), Checked = _c.Installed };
        p.Controls.Add(_inst);
        y += 26;
        _hiscore = new CheckBox { Text = "High scores only", AutoSize = true, ForeColor = Fg, Location = new Point(0, S(y)), Checked = _c.HighScores };
        _tips.SetToolTip(_hiscore, "Only games an installed hiscore.dat says can produce a high score (MAME / FBNeo).");
        p.Controls.Add(_hiscore);

        return p;
    }

    // Un ToolTip est un Component, pas un Control : hors IContainer, Form.Dispose ne le libère pas —
    // d'où la libération explicite à la fermeture (sinon un handle natif fuit par ouverture).
    private readonly ToolTip _tips = new();
    protected override void Dispose(bool disposing)
    {
        if (disposing) _tips.Dispose();
        base.Dispose(disposing);
    }

    // ── Une dimension multi-sélection (Platform, Region, Play mode, …) ────────
    private Control BuildFacet(string key, List<string> values, List<string> selected)
    {
        var p = new Panel { BackColor = Bg };
        var list = new CheckedListBox
        {
            Location = new Point(0, S(28)), Size = new Size(S(660), S(346)), BackColor = Panel2, ForeColor = Fg,
            BorderStyle = BorderStyle.FixedSingle, CheckOnClick = true, IntegralHeight = false,
        };
        var shown = Union(values, selected);
        foreach (var v in shown) list.Items.Add(v, selected.Contains(v, StringComparer.OrdinalIgnoreCase));
        _facetLists[key] = list;

        var note = new Label
        {
            Text = shown.Count == 0
                ? "No value found in the library for this field."
                : $"{shown.Count} value(s) — a game matches when it has ANY of the ticked ones.",
            AutoSize = false, Size = new Size(S(500), S(22)), ForeColor = SubFg, Location = new Point(0, 0),
        };
        var clear = new Button
        {
            Text = "Untick all", Size = new Size(S(90), S(22)), Location = new Point(S(570), S(-1)),
            FlatStyle = FlatStyle.Flat, BackColor = Panel2, ForeColor = Fg, FlatAppearance = { BorderSize = 0 },
        };
        clear.Click += (_, _) => { for (int i = 0; i < list.Items.Count; i++) list.SetItemChecked(i, false); };

        p.Controls.Add(note); p.Controls.Add(clear); p.Controls.Add(list);
        return p;
    }

    // ── Genre ─────────────────────────────────────────────────────────────────
    private Control BuildGenre()
    {
        var p = new Panel { BackColor = Bg };
        _genreMode = new Button
        {
            AutoSize = false, Size = new Size(S(150), S(26)), Location = new Point(0, 0),
            FlatStyle = FlatStyle.Flat, BackColor = Panel2, ForeColor = Fg, FlatAppearance = { BorderSize = 0 },
        };
        void PaintMode() => _genreMode.Text = "Match: " + (_c.GenreMode == "and" ? "ALL selected" : "ANY selected");
        PaintMode();
        _genreMode.Click += (_, _) => { _c.GenreMode = _c.GenreMode == "and" ? "or" : "and"; PaintMode(); };
        p.Controls.Add(_genreMode);

        _genreList = new CheckedListBox
        {
            Location = new Point(0, S(34)), Size = new Size(S(500), S(320)), BackColor = Panel2, ForeColor = Fg,
            BorderStyle = BorderStyle.FixedSingle, CheckOnClick = true, IntegralHeight = false,
        };
        foreach (var gname in Union(FilterFacets.Sorted(_f.Genres), _c.Genres))
            _genreList.Items.Add(gname, _c.Genres.Contains(gname, StringComparer.OrdinalIgnoreCase));
        p.Controls.Add(_genreList);
        return p;
    }

    /// <summary>values ∪ selected, trié — une sélection devenue orpheline reste visible ET cochable,
    /// au lieu d'être silencieusement perdue au prochain Apply.</summary>
    private static List<string> Union(List<string> values, List<string> selected)
    {
        var set = new HashSet<string>(values, StringComparer.OrdinalIgnoreCase);
        foreach (var s in selected) if (!string.IsNullOrWhiteSpace(s)) set.Add(s);
        return set.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string[] WithSelected(List<string> values, string selected)
        => string.IsNullOrEmpty(selected) || values.Contains(selected, StringComparer.OrdinalIgnoreCase)
            ? values.ToArray()
            : values.Append(selected).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();

    // ── Publisher / Developer (text substring + autocomplete list) ────────────
    private Control BuildText(bool isPub)
    {
        var p = new Panel { BackColor = Bg };
        var note = new Label { Text = "Type to filter (matches when the " + (isPub ? "publisher" : "developer") + " contains the text). The list below is autocomplete.", AutoSize = false, Size = new Size(S(500), S(34)), ForeColor = SubFg, Location = new Point(0, 0) };
        p.Controls.Add(note);

        var tb = new TextBox { Location = new Point(0, S(38)), Size = new Size(S(500), S(24)), BackColor = Panel2, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle, Text = isPub ? _c.Publisher : _c.Developer };
        p.Controls.Add(tb);

        var sug = new ListBox { Location = new Point(0, S(70)), Size = new Size(S(500), S(280)), BackColor = Panel2, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle, IntegralHeight = false };
        p.Controls.Add(sug);

        var all = (isPub ? FilterFacets.Sorted(_f.Publishers) : FilterFacets.Sorted(_f.Developers)).ToArray();
        void Refresh()
        {
            string q = tb.Text.Trim();
            var items = (string.IsNullOrEmpty(q) ? all : all.Where(x => x.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)).Take(40).ToArray();
            sug.BeginUpdate(); sug.Items.Clear(); sug.Items.AddRange(items); sug.EndUpdate();
        }
        tb.TextChanged += (_, _) => Refresh();
        sug.DoubleClick += (_, _) => { if (sug.SelectedItem is string s) tb.Text = s; };
        Refresh();

        if (isPub) { _pubText = tb; _pubSug = sug; } else { _devText = tb; _devSug = sug; }
        return p;
    }

    // ── Order by ──────────────────────────────────────────────────────────────
    private Control BuildOrderBy()
    {
        var p = new Panel { BackColor = Bg };
        _sort = new ComboBox { Location = new Point(0, 0), Size = new Size(S(300), S(24)), DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, BackColor = Panel2, ForeColor = Fg };
        foreach (var (_, label) in Sorts) _sort.Items.Add(label);
        int ix = Array.FindIndex(Sorts, s => s.key == _c.SortBy);
        _sort.SelectedIndex = ix >= 0 ? ix : 0;
        p.Controls.Add(new Label { Text = "Sort the results by:", AutoSize = true, ForeColor = Fg, Location = new Point(0, S(-2)) });
        _sort.Location = new Point(0, S(24));
        p.Controls.Add(_sort);
        p.Controls.Add(new Label { Text = "Alphabetical keeps the list's own sort; the others sort descending (newest / highest / most recent first).", AutoSize = false, Size = new Size(S(500), S(40)), ForeColor = SubFg, Location = new Point(0, S(58)) });
        return p;
    }

    // ── History ───────────────────────────────────────────────────────────────
    private Control BuildHistory()
    {
        var p = new Panel { BackColor = Bg };
        _histList = new ListBox { Location = new Point(0, 0), Size = new Size(S(500), S(360)), BackColor = Panel2, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle, IntegralHeight = false };
        if (_history.Count == 0) _histList.Items.Add("(no recent searches)");
        else foreach (var h in _history) _histList.Items.Add(h.Summary());
        _histList.DoubleClick += (_, _) =>
        {
            int i = _histList.SelectedIndex;
            if (i >= 0 && i < _history.Count) { Result = _history[i].Clone(); DialogResult = DialogResult.OK; Close(); }
        };
        p.Controls.Add(_histList);
        p.Controls.Add(new Label { Text = "Double-click a past search to apply it.", AutoSize = true, ForeColor = SubFg, Location = new Point(0, S(366)) });
        return p;
    }

    // ── Footer (Apply / Clear / Cancel) ───────────────────────────────────────
    private void BuildFooter()
    {
        var footer = new Panel { Dock = DockStyle.Bottom, Height = S(48), BackColor = Panel };
        int w = ClientSize.Width;   // le pied suit la largeur réelle du dialogue, plus une constante figée
        _apply = new Button { Text = "Apply", Size = new Size(S(90), S(28)), Location = new Point(w - S(100), S(10)), FlatStyle = FlatStyle.Flat, BackColor = Accent, ForeColor = Color.White, FlatAppearance = { BorderSize = 0 } };
        var clear = new Button { Text = "Clear", Size = new Size(S(90), S(28)), Location = new Point(w - S(198), S(10)), FlatStyle = FlatStyle.Flat, BackColor = Panel2, ForeColor = Fg, FlatAppearance = { BorderSize = 0 } };
        var cancel = new Button { Text = "Cancel", Size = new Size(S(90), S(28)), Location = new Point(S(12), S(10)), FlatStyle = FlatStyle.Flat, BackColor = LiteBoxTheme.CancelBtn, ForeColor = Color.White, FlatAppearance = { BorderSize = 0 } };
        _apply.Click += (_, _) => { ReadInto(_c); Result = _c; DialogResult = DialogResult.OK; Close(); };
        clear.Click += (_, _) => { Result = new FilterCriteria(); DialogResult = DialogResult.OK; Close(); };
        cancel.Click += (_, _) => { Result = null; DialogResult = DialogResult.Cancel; Close(); };
        footer.Controls.Add(_apply); footer.Controls.Add(clear); footer.Controls.Add(cancel);
        Controls.Add(footer);
        footer.BringToFront();
        AcceptButton = _apply; CancelButton = cancel;
    }

    private void ReadInto(FilterCriteria c)
    {
        c.YearMin = (int)Math.Round(_year.Low); c.YearMax = (int)Math.Round(_year.High);
        c.RatingMin = _rating.Low; c.RatingMax = _rating.High;
        c.ReleaseType = _type.SelectedIndex <= 0 ? "" : (string)_type.SelectedItem!;
        c.Fav = _fav.Checked; c.Installed = _inst.Checked; c.HighScores = _hiscore.Checked;
        c.Genres = _genreList.CheckedItems.Cast<string>().ToList();
        c.Publisher = _pubText.Text.Trim(); c.Developer = _devText.Text.Trim();
        c.SortBy = _sort.SelectedIndex >= 0 ? Sorts[_sort.SelectedIndex].key : "alpha";

        c.MaxPlayers = _players.SelectedIndex <= 0 ? 0 : int.TryParse((string)_players.SelectedItem!, out var n) ? n : 0;
        c.Achievements = _ach.SelectedIndex switch { 1 => "yes", 2 => "no", _ => "" };
        c.Saves = _saves.SelectedIndex switch { 1 => "game", 2 => "state", _ => "" };

        // Les dimensions multi-sélection se relisent par la même table qui les a construites.
        List<string> Checked(string key) => _facetLists.TryGetValue(key, out var l)
            ? l.CheckedItems.Cast<string>().ToList() : new List<string>();
        c.Platforms = Checked("platform"); c.Regions = Checked("region"); c.PlayModes = Checked("playmode");
        c.Statuses = Checked("status"); c.Progresses = Checked("progress"); c.Esrb = Checked("esrb");
        c.Controllers = Checked("pad");
    }
}
