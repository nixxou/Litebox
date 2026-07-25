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
    private readonly string[] _genres, _publishers, _developers, _releaseTypes;

    /// <summary>Set on Apply (the edited criteria) or Clear (a fresh default = inactive). Null on Cancel.</summary>
    public FilterCriteria? Result;

    private readonly Dictionary<string, Panel> _tabPanels = new();
    private readonly List<Button> _tabButtons = new();

    private RangeSlider _year = null!, _rating = null!;
    private ComboBox _type = null!, _sort = null!;
    private CheckBox _fav = null!, _inst = null!;
    private Button _genreMode = null!;
    private CheckedListBox _genreList = null!;
    private TextBox _pubText = null!, _devText = null!;
    private ListBox _pubSug = null!, _devSug = null!, _histList = null!;
    private readonly List<FilterCriteria> _history;
    private Button _apply = null!;

    private static readonly (string key, string label)[] Sorts =
    {
        ("alpha", "Alphabetical"), ("year", "Release date"), ("rating", "Rating"), ("lastplayed", "Recently played"),
    };

    public FilterDialog(FilterCriteria current, IEnumerable<string> genres, IEnumerable<string> publishers,
                        IEnumerable<string> developers, IEnumerable<string> releaseTypes, List<FilterCriteria> history)
    {
        _c = current.Clone();
        _genres = genres.ToArray(); _publishers = publishers.ToArray();
        _developers = developers.ToArray(); _releaseTypes = releaseTypes.ToArray();
        _history = history;
        _s = DeviceDpi / 96f;

        Text = "Search filter";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        BackColor = Bg; ForeColor = Fg; Font = new Font("Segoe UI", 9f);
        ClientSize = new Size(S(560), S(460));

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

        var strip = new FlowLayoutPanel { Dock = DockStyle.Top, Height = S(40), BackColor = Panel, Padding = new Padding(S(6), S(6), 0, 0), WrapContents = false };
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

        Head("Release type", y);
        _type = new ComboBox { Location = new Point(S(160), S(y - 2)), Size = new Size(S(300), S(24)), DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, BackColor = Panel2, ForeColor = Fg };
        _type.Items.Add("(Any)");
        foreach (var rt in _releaseTypes) _type.Items.Add(rt);
        _type.SelectedIndex = string.IsNullOrEmpty(_c.ReleaseType) ? 0 : Math.Max(0, _type.Items.IndexOf(_c.ReleaseType));
        p.Controls.Add(_type);
        y += 34;

        _fav = new CheckBox { Text = "Favorite only", AutoSize = true, ForeColor = Fg, Location = new Point(0, S(y)), Checked = _c.Fav };
        p.Controls.Add(_fav);
        y += 26;
        _inst = new CheckBox { Text = "Installed only", AutoSize = true, ForeColor = Fg, Location = new Point(0, S(y)), Checked = _c.Installed };
        p.Controls.Add(_inst);

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
        foreach (var gname in _genres) _genreList.Items.Add(gname, _c.Genres.Contains(gname, StringComparer.OrdinalIgnoreCase));
        p.Controls.Add(_genreList);
        return p;
    }

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

        var all = isPub ? _publishers : _developers;
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
        _apply = new Button { Text = "Apply", Size = new Size(S(90), S(28)), Location = new Point(S(560 - 100), S(10)), FlatStyle = FlatStyle.Flat, BackColor = Accent, ForeColor = Color.White, FlatAppearance = { BorderSize = 0 } };
        var clear = new Button { Text = "Clear", Size = new Size(S(90), S(28)), Location = new Point(S(560 - 198), S(10)), FlatStyle = FlatStyle.Flat, BackColor = Panel2, ForeColor = Fg, FlatAppearance = { BorderSize = 0 } };
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
        c.Fav = _fav.Checked; c.Installed = _inst.Checked;
        c.Genres = _genreList.CheckedItems.Cast<string>().ToList();
        c.Publisher = _pubText.Text.Trim(); c.Developer = _devText.Text.Trim();
        c.SortBy = _sort.SelectedIndex >= 0 ? Sorts[_sort.SelectedIndex].key : "alpha";
    }
}
