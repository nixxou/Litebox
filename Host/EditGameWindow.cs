// Edit Game window — per-game editor modelled on LaunchBox's "Edit Game" dialog and heavily
// inspired by ExtendDB's Editgameform (the data-entry / "saisie" part), but re-homed onto the
// LiteBox data layer: every field writes through the HostGame SETTERS (SDK props + ILiteBoxFields),
// so each change lands in the GameStore op-log → persisted to the Platform XML, exactly like any
// other host write. Read-only mode disables inputs and never writes.
//
// Deliberately DIFFERENT from ExtendDB's editor:
//   • NO lock system (that stays ExtendDB-specific for now) — fields are plain, no 🔓 buttons.
//
// Multi-select (adapted from ExtendDB): the editor takes 1..N games. Each field shows the common value,
// or a "‹multiple values›" placeholder when they differ. Only fields the user actually TOUCHES are
// written, to ALL selected games — untouched fields keep each game's own value. (A "touched" set stands
// in for ExtendDB's read-only-until-double-click lock; it also avoids re-journalling unchanged fields.)
//
// Shell: left navigation TREE (full LB Edit-Game hierarchy), right content panel, bottom bar with
// OK / Cancel + ◄ ► (navigate the visible list; navigating saves first, like LB). Only the Metadata
// page has content today; every other node is present in the tree with a placeholder panel.

#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Data;
using LbApiHost.Host.UiKit;

namespace LbApiHost.Host;

internal sealed partial class EditGameWindow : Form   // Game Saves page lives in EditGameWindowSaves.cs
{
    // Palette — harmonized onto the shared LiteBoxTheme (was a local, slightly bluer near-duplicate).
    // One source of truth so the Options → Colors editor drives this window too. ModifiedColor stays
    // local (it isn't part of the shared theme).
    private static readonly Color Bg = LiteBoxTheme.Bg;
    private static readonly Color PanelC = LiteBoxTheme.PanelC;
    private static readonly Color Field = LiteBoxTheme.Panel2;
    private static readonly Color Fg = LiteBoxTheme.Fg;
    private static readonly Color SubFg = LiteBoxTheme.SubFg;
    private static readonly Color Accent = LiteBoxTheme.Accent;
    private static readonly Color ModifiedColor = Color.FromArgb(235, 150, 135);   // slightly-red tint for a changed field

    // Layout constants (two-column grid) - DPI-scaled instance fields, not compile-time consts:
    // nearly the entire Metadata page (47 usages) is built purely from these ten numbers, so
    // scaling them once here fixes the bulk of this file's DPI correctness without individually
    // touching every place that reads them.
    private readonly int Lx, Lw, LFx, FW;   // left column: label x/w, field x, field width
    private readonly int Rx, Rw, RFx;       // right column: label x/w, field x
    private readonly int FullW;             // full-width field (Title / URLs)
    private readonly int RowH, FieldH;
    private readonly float _s;
    private int S(int px) => (int)Math.Round(px * _s);

    private readonly IReadOnlyList<IGame> _visible;
    private readonly bool _readOnly;
    private int _index;
    private IReadOnlyList<IGame> _editGames;   // the game(s) being edited (1 for single, N for multi)
    private bool IsMulti => _editGames.Count > 1;
    private bool _loading;                     // suppress dirty-tracking while LoadMetadata sets values
    private bool _restoring;                   // suppress dirty-tracking while a revert restores a value
    private const string Multi = "‹multiple values›";

    // Dirty-field tracking: baseline (loaded) value-string per control, a per-field ↺ revert button, and
    // the list of trackable controls. A field is "modified" when its current value differs from the
    // baseline — shown reddish with the ↺ button; on save it is written (to every edited game) UNLESS it
    // still holds the Multi placeholder. Reverting restores the baseline (so it never writes the placeholder).
    private readonly Dictionary<Control, string> _baseline = new();
    private readonly Dictionary<Control, Button> _revert = new();
    private readonly List<Control> _fields = new();
    private readonly ToolTip _tips = new();

    // Custom Fields page (lazy-built). Names are a library-wide vocabulary; values are per game.
    private DataGridView? _cfGrid;
    private const int CfName = 0, CfValue = 1;
    private Dictionary<string, string[]> _cfValuesByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _cfLoaded = new(StringComparer.OrdinalIgnoreCase);
    private string? _cfRenameFrom;

    // Shell.
    private readonly TreeView _tree;
    private readonly Panel _host;
    private readonly Label _titleBar;
    private readonly Button _prev, _next;
    private readonly Dictionary<string, Control> _pages = new(StringComparer.Ordinal);

    // Metadata controls (kept so navigation just reloads values).
    private TextBox _title = null!, _releaseDate = null!, _lastPlayed = null!, _videoUrl = null!, _wikiUrl = null!, _version = null!, _notes = null!, _sortTitle = null!;
    private ComboBox _rating = null!, _releaseType = null!, _genre = null!, _platform = null!, _developer = null!,
                     _publisher = null!, _series = null!, _region = null!, _playMode = null!, _status = null!,
                     _source = null!, _progress = null!;
    private NumericUpDown _maxPlayers = null!;
    private CheckBox _favorite = null!, _portable = null!, _installed = null!, _hide = null!, _broken = null!;
    private Label _dateAdded = null!, _dateModified = null!, _playCount = null!;
    private StarBar _starBar = null!;

    public static void Open(IReadOnlyList<IGame> games, IReadOnlyList<IGame> visible, bool readOnly, IWin32Window? owner)
    {
        if (games == null || games.Count == 0) return;
        using var w = new EditGameWindow(games, visible, readOnly);
        w.ShowDialog(owner);
    }

    private EditGameWindow(IReadOnlyList<IGame> games, IReadOnlyList<IGame> visible, bool readOnly)
    {
        var _h = Handle;   // force handle creation so DeviceDpi reflects the real monitor
        _s = DeviceDpi / 96f;
        Lx = S(16); Lw = S(104); LFx = S(126); FW = S(290);
        Rx = S(436); Rw = S(96); RFx = S(540);
        FullW = S(704);
        RowH = S(32); FieldH = S(24);

        _editGames = games;
        _visible = visible ?? Array.Empty<IGame>();
        _readOnly = readOnly;
        _index = games.Count > 1 ? -1 : IndexOf(games[0]);

        Size = new Size(S(1080), S(730));
        MinimumSize = new Size(S(880), S(600));
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Bg; ForeColor = Fg;
        Font = new Font("Segoe UI", 9.5f);
        ShowIcon = false; ShowInTaskbar = false;
        MaximizeBox = false; MinimizeBox = false;
        KeyPreview = true;

        // ── Left navigation tree ─────────────────────────────────────────
        _tree = new TreeView
        {
            Dock = DockStyle.Left, Width = S(210),
            BackColor = PanelC, ForeColor = Fg, BorderStyle = BorderStyle.None,
            HideSelection = false, FullRowSelect = true, ShowLines = false, ShowPlusMinus = true,
            ItemHeight = S(26), DrawMode = TreeViewDrawMode.OwnerDrawText, Indent = S(18),
            Font = new Font("Segoe UI", 9.5f),
        };
        _tree.DrawNode += OnDrawNode;
        _tree.AfterSelect += (_, e) => ShowPage(e.Node?.Tag as string ?? "Metadata");
        BuildTree();

        // ── Right: title bar + content host ──────────────────────────────
        var right = new Panel { Dock = DockStyle.Fill, BackColor = Bg };
        _titleBar = new Label
        {
            Dock = DockStyle.Top, Height = S(34), BackColor = PanelC, ForeColor = Fg,
            Text = "Metadata", TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 10.5f, FontStyle.Bold),
        };
        _host = new Panel { Dock = DockStyle.Fill, BackColor = Bg, AutoScroll = true, Padding = new Padding(S(6), S(6), S(6), S(6)) };
        right.Controls.Add(_host);
        right.Controls.Add(_titleBar);

        // ── Bottom bar: OK / Cancel + hint + ◄ ► ─────────────────────────
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = S(46), BackColor = PanelC };
        var ok = FooterBtn("OK", Color.FromArgb(50, 110, 65));
        var cancel = FooterBtn("Cancel", Color.FromArgb(70, 70, 82));
        ok.Location = new Point(S(12), S(9));
        cancel.Location = new Point(S(112), S(9));
        ok.Click += (_, _) => { SaveCurrent(); SaveCustomFields(); DialogResult = DialogResult.OK; Close(); };
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };

        var hint = new Label
        {
            AutoSize = true, ForeColor = SubFg, BackColor = PanelC,
            Text = _readOnly ? "Read-only — changes are not saved"
                 : _editGames.Count > 1 ? $"Editing {_editGames.Count} games — only fields you change are applied to all"
                 : "Navigating will save immediately",
            Font = new Font("Segoe UI", 9f),
        };
        _prev = NavBtn("◄"); _next = NavBtn("►");
        _prev.Click += (_, _) => Navigate(-1);
        _next.Click += (_, _) => Navigate(+1);
        bottom.Controls.AddRange(new Control[] { ok, cancel, hint, _prev, _next });
        bottom.Resize += (_, _) =>
        {
            int r = bottom.ClientSize.Width - S(12);
            _next.Left = r - _next.Width; _prev.Left = _next.Left - _prev.Width - S(6);
            hint.Left = _prev.Left - hint.Width - S(12); hint.Top = (bottom.Height - hint.Height) / 2;
        };

        Controls.Add(right);
        Controls.Add(_tree);
        Controls.Add(bottom);
        right.BringToFront();

        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); } };

        // Build + show the Metadata page, then load the current game.
        _pages["Metadata"] = BuildMetadataPage();
        _pages["Notes"] = BuildNotesPage();
        if (!IsMulti) _pages["SortTitle"] = BuildSortTitlePage();   // single-game only
        LoadMetadata();
        _tree.SelectedNode = _tree.Nodes[0];   // Metadata
        ShowPage("Metadata");
        UpdateChrome();

        // Open with a clean look: no field pre-selected. Setting a combo's .Text to a value that exists in
        // its Items highlights the editor text, and the first tab-stop field would otherwise grab focus and
        // select-all. Park focus on the tree and clear every field's selection.
        Shown += (_, _) =>
        {
            try
            {
                foreach (var c in _fields)
                {
                    if (c is TextBox tb) tb.Select(0, 0);
                    else if (c is ComboBox cb) cb.SelectionLength = 0;
                }
                ActiveControl = _tree;
            }
            catch { }
        };
        if (_readOnly) { DisableInputs(_pages["Metadata"]); DisableInputs(_pages["Notes"]); if (!IsMulti) DisableInputs(_pages["SortTitle"]); }
    }

    // ── Navigation tree ──────────────────────────────────────────────────
    private void BuildTree()
    {
        TreeNode N(string text, string tag) => new(text) { Tag = tag };

        var metadata = N("Metadata", "Metadata");
        metadata.Nodes.Add(N("Notes", "Notes"));
        metadata.Nodes.Add(N("Custom Fields", "CustomFields"));
        if (!IsMulti) metadata.Nodes.Add(N("Sort Title", "SortTitle"));   // single-game only — hidden in multi
        metadata.Nodes.Add(N("Additional Apps", "AdditionalApps"));
        metadata.Nodes.Add(N("Alternate Names", "AlternateNames"));
        metadata.Nodes.Add(N("Controller Support", "ControllerSupport"));
        metadata.Nodes.Add(N("Game Saves", "GameSaves"));

        var media = N("Media", "Media");
        media.Nodes.AddRange(new[]
        {
            N("Images", "Images"),
            N("Videos", "Videos"),
            N("3D Model Settings", "ModelSettings"),
        });

        var launching = N("Launching", "Launching");
        var dosbox = N("DOSBox", "DOSBox");
        dosbox.Nodes.Add(N("Mounts", "Mounts"));
        launching.Nodes.Add(dosbox);
        launching.Nodes.AddRange(new[]
        {
            N("Emulation", "Emulation"),
            N("Root Folder", "RootFolder"),
            N("Startup/Pause", "StartupPause"),
        });

        _tree.Nodes.AddRange(new[] { metadata, media, launching });
        _tree.ExpandAll();
    }

    private void OnDrawNode(object? sender, DrawTreeNodeEventArgs e)
    {
        // OwnerDrawText: the system draws the background (incl. selection) + the ± glyph; we draw
        // only the label — so we never paint over the expand glyph. Top nodes brighter than children.
        if (e.Node == null) { e.DrawDefault = true; return; }
        bool sel = (e.State & TreeNodeStates.Selected) != 0;
        var color = sel ? Color.White : (e.Node.Level == 0 ? Fg : SubFg);
        TextRenderer.DrawText(e.Graphics, e.Node.Text, _tree.Font, e.Bounds, color,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }

    private void ShowPage(string key)
    {
        _titleBar.Text = _tree.SelectedNode?.Text ?? key;
        if (!_pages.TryGetValue(key, out var page))
        {
            page = key switch
            {
                "Metadata" => BuildMetadataPage(),
                "CustomFields" => BuildCustomFieldsPage(),
                "GameSaves" => IsMulti ? Placeholder("Game Saves") : BuildGameSavesPage(),
                _ => Placeholder(_tree.SelectedNode?.Text ?? key),
            };
            _pages[key] = page;
            if (_readOnly) DisableInputs(page);
        }
        _host.SuspendLayout();
        _host.Controls.Clear();
        page.Dock = DockStyle.Fill;
        _host.Controls.Add(page);
        _host.ResumeLayout();
    }

    private Control Placeholder(string title)
    {
        var p = new Panel { BackColor = Bg };
        var l = new Label
        {
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = SubFg, BackColor = Bg, Font = new Font("Segoe UI", 10f, FontStyle.Italic),
            Text = $"{title}\n\n(not implemented yet)",
        };
        p.Controls.Add(l);
        return p;
    }

    // ── Notes page ───────────────────────────────────────────────────────
    // A single big multiline box bound to IGame.Notes. Same dirty-tracking as the Metadata fields:
    // in multi mode it merges (or shows the "‹multiple values›" placeholder), goes reddish when changed,
    // and its ↺ (top-right) restores the loaded text. Whitespace is preserved (unlike the trimmed fields).
    private Control BuildNotesPage()
    {
        var p = new Panel { BackColor = Bg, Padding = new Padding(S(6)) };
        _notes = new TextBox
        {
            Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical, WordWrap = true,
            Dock = DockStyle.Fill, BackColor = Field, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Segoe UI", 9.5f),
        };
        _notes.TextChanged += (_, _) => OnField(_notes);
        p.Controls.Add(_notes);
        _fields.Add(_notes);

        var rb = new Button
        {
            Text = "↺", Size = new Size(S(18), S(18)), Visible = false, TabStop = false, Cursor = Cursors.Hand,
            FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(92, 46, 42), ForeColor = Color.FromArgb(255, 180, 165),
            Font = new Font("Segoe UI Symbol", 9.5f), FlatAppearance = { BorderSize = 1 },
        };
        rb.FlatAppearance.BorderColor = Color.FromArgb(150, 72, 64);
        rb.Click += (_, _) => RevertField(_notes);
        _tips.SetToolTip(rb, "Restore the original value");
        p.Controls.Add(rb); rb.BringToFront();
        _revert[_notes] = rb;
        void Place() { if (p.ClientSize.Width > S(60)) rb.Location = new Point(p.ClientSize.Width - rb.Width - S(24), S(12)); }
        p.Resize += (_, _) => { Place(); rb.BringToFront(); };
        Place();

        return p;
    }

    // ── Sort Title page ──────────────────────────────────────────────────
    // Single-game only. Hardcodes the name used to ORDER this game: it overrides Title as the base of the
    // list sort (which then strips a leading article + normalises — see MainWindow.CompareName), so a
    // Sort Title lets you keep a series together or force any custom order. Written straight to
    // IGame.SortTitle; the list re-sorts on close (RebuildView). The node is hidden entirely in
    // multi-select (single-game only), so this page is only ever built when !IsMulti.
    private Control BuildSortTitlePage()
    {
        var p = new Panel { BackColor = Bg, Padding = new Padding(16), AutoScroll = true };

        var lbl = new Label { Text = "Sort Title:", AutoSize = true, ForeColor = Fg, Location = new Point(16, 18), Font = new Font("Segoe UI", 9.5f) };
        p.Controls.Add(lbl);

        _sortTitle = new TextBox
        {
            Location = new Point(16, 42), Width = 620, BackColor = Field, ForeColor = Fg,
            BorderStyle = BorderStyle.FixedSingle, Font = new Font("Segoe UI", 9.5f),
        };
        _sortTitle.TextChanged += (_, _) => OnField(_sortTitle);
        p.Controls.Add(_sortTitle);
        _fields.Add(_sortTitle);

        var rb = new Button
        {
            Text = "↺", Size = new Size(18, 18), Visible = false, TabStop = false, Cursor = Cursors.Hand,
            FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(92, 46, 42), ForeColor = Color.FromArgb(255, 180, 165),
            Font = new Font("Segoe UI Symbol", 9.5f), FlatAppearance = { BorderSize = 1 },
        };
        rb.FlatAppearance.BorderColor = Color.FromArgb(150, 72, 64);
        rb.Location = new Point(_sortTitle.Right + 6, _sortTitle.Top + (_sortTitle.Height - rb.Height) / 2);
        rb.Click += (_, _) => RevertField(_sortTitle);
        _tips.SetToolTip(rb, "Restore the original value");
        p.Controls.Add(rb); rb.BringToFront();
        _revert[_sortTitle] = rb;

        var help = new Label
        {
            AutoSize = false, Location = new Point(16, 80), Size = new Size(660, 170), ForeColor = SubFg,
            Font = new Font("Segoe UI", 9f),
            Text = "The Sort Title field is used for custom arrangement of your games. It can be used to keep a series "
                 + "together, reorder games in a series, or for any other changes to the order in which games are displayed.\r\n\r\n"
                 + "For example:\r\n\r\nKing's Quest 1\r\nKing's Quest 1.5\r\nKing's Quest 2\r\nKing's Quest 3",
        };
        p.Controls.Add(help);
        return p;
    }

    // ── Custom Fields page (Name/Value grid) ─────────────────────────────
    // Names are a LIBRARY-WIDE vocabulary (union across every game): adding a name shows it for all games,
    // but nothing is stored for a game until it gets a value (empty values are never written — no bloat).
    // Value = free-text combo seeded with that name's distinct library values. Renaming or deleting a name
    // asks to apply it across the whole library. Multi-select: a value merges ("‹multiple values›") and is
    // applied to every edited game.
    private Control BuildCustomFieldsPage()
    {
        var p = new Panel { BackColor = Bg, Padding = new Padding(S(6)) };
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill, BackgroundColor = Bg, GridColor = Color.FromArgb(60, 60, 70),
            BorderStyle = BorderStyle.None, EnableHeadersVisualStyles = false, RowHeadersVisible = false,
            AllowUserToAddRows = !_readOnly, AllowUserToDeleteRows = !_readOnly, ReadOnly = _readOnly,
            // Fill (not None+fixed Width): a fixed pixel column width never scales for DPI and can
            // leave a dead gap or overflow relative to the grid's real width - see GameListView's
            // StretchColumn fix and EditEmulatorWindow's platform grid for the same reasoning.
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, Font = new Font("Segoe UI", 9.5f),
        };
        grid.ColumnHeadersDefaultCellStyle.BackColor = PanelC;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Fg;
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = PanelC;
        grid.DefaultCellStyle.BackColor = Field;
        grid.DefaultCellStyle.ForeColor = Fg;
        grid.DefaultCellStyle.SelectionBackColor = Accent;
        grid.DefaultCellStyle.SelectionForeColor = Color.White;

        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Name", FillWeight = 260, SortMode = DataGridViewColumnSortMode.NotSortable });
        var valCol = new DataGridViewComboBoxColumn
        {
            HeaderText = "Value", FillWeight = 560, FlatStyle = FlatStyle.Flat,
            DisplayStyle = DataGridViewComboBoxDisplayStyle.Nothing, SortMode = DataGridViewColumnSortMode.NotSortable,
        };
        grid.Columns.Add(valCol);
        grid.DataError += (_, e) => e.ThrowException = false;

        // Value cell → an editable combo seeded with that row-name's library values.
        grid.EditingControlShowing += (_, e) =>
        {
            if (grid.CurrentCell?.ColumnIndex == CfValue && e.Control is ComboBox cb)
            {
                cb.DropDownStyle = ComboBoxStyle.DropDown;
                cb.AutoCompleteMode = AutoCompleteMode.SuggestAppend; cb.AutoCompleteSource = AutoCompleteSource.ListItems;
                cb.Items.Clear();
                string name = (grid.CurrentRow?.Cells[CfName].Value as string ?? "").Trim();
                if (_cfValuesByName.TryGetValue(name, out var vals)) cb.Items.AddRange(vals);
            }
        };
        // Accept free text: register any typed value so the combo commit never DataErrors.
        grid.CellValidating += (_, e) =>
        {
            if (e.ColumnIndex != CfValue) return;
            var v = e.FormattedValue as string ?? "";
            if (v.Length > 0 && !valCol.Items.Contains(v)) valCol.Items.Add(v);
        };
        // Rename an EXISTING field → confirm library-wide (else revert).
        grid.CellBeginEdit += (_, e) => { if (e.ColumnIndex == CfName && e.RowIndex >= 0) _cfRenameFrom = grid.Rows[e.RowIndex].Cells[CfName].Value as string; };
        grid.CellEndEdit += (_, e) =>
        {
            if (e.ColumnIndex != CfName || e.RowIndex < 0) return;
            var row = grid.Rows[e.RowIndex];
            string orig = row.Tag as string ?? "";                         // the library name this row started as ("" = new row)
            string now = (row.Cells[CfName].Value as string ?? "").Trim();
            if (string.IsNullOrEmpty(orig) || string.Equals(orig, now, StringComparison.Ordinal)) return;
            if (now.Length == 0) { row.Cells[CfName].Value = orig; return; }
            var ans = MessageBox.Show(this,
                $"Rename the custom field \"{orig}\" to \"{now}\" for ALL games in your library?",
                "Rename Custom Field", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (ans == DialogResult.OK) { RenameCustomFieldLibraryWide(orig, now); row.Tag = now; RebuildCfVocab(); }
            else row.Cells[CfName].Value = orig;
        };
        // The Delete key removes the whole custom-field row (with the library-wide warning), rather than
        // just clearing the value cell. Ignored while editing a cell (there Delete edits the text).
        grid.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Delete || _readOnly || grid.IsCurrentCellInEditMode) return;
            var row = grid.CurrentRow;
            if (row == null || row.IsNewRow) return;
            e.Handled = true; e.SuppressKeyPress = true;
            DeleteCfRow(row);
        };

        p.Controls.Add(grid);
        _cfGrid = grid;
        LoadCustomFields();
        return p;
    }

    private void LoadCustomFields()
    {
        if (_cfGrid == null) return;
        RebuildCfVocab();
        _cfLoaded.Clear();
        var valCol = (DataGridViewComboBoxColumn)_cfGrid.Columns[CfValue];
        _cfGrid.Rows.Clear();
        valCol.Items.Clear();
        foreach (var name in _cfValuesByName.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            string val = MergeCfValue(name);
            _cfLoaded[name] = val;
            if (val.Length > 0 && !valCol.Items.Contains(val)) valCol.Items.Add(val);   // so the cell can display it
            int i = _cfGrid.Rows.Add(name, val);
            _cfGrid.Rows[i].Tag = name;
        }
    }

    private void SaveCustomFields()
    {
        if (_readOnly || _cfGrid == null) return;
        try { _cfGrid.EndEdit(); } catch { }
        // Grid → intended name→value (last row wins on a duplicate name).
        var intended = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DataGridViewRow r in _cfGrid.Rows)
        {
            if (r.IsNewRow) continue;
            string name = (r.Cells[CfName].Value as string ?? "").Trim();
            if (name.Length == 0) continue;
            intended[name] = r.Cells[CfValue].Value as string ?? "";
        }
        foreach (var g in _editGames)
        {
            var existing = Safe(() => g.GetAllCustomFields()) ?? Array.Empty<ICustomField>();
            foreach (var (name, value) in intended)
            {
                if (value == Multi) continue;                                                     // untouched multi → keep this game's value
                if (_cfLoaded.TryGetValue(name, out var loaded) && string.Equals(value, loaded, StringComparison.Ordinal)) continue;  // unchanged
                var cur = existing.FirstOrDefault(c => string.Equals(Safe(() => c.Name), name, StringComparison.OrdinalIgnoreCase));
                if (value.Length > 0)
                {
                    if (cur != null) { try { cur.Value = value; } catch { } }
                    else { try { var nc = g.AddNewCustomField(); nc.Name = name; nc.Value = value; } catch { } }
                }
                else if (cur != null) { try { g.TryRemoveCustomField(cur); } catch { } }          // cleared → remove (no bloat)
            }
        }
        RebuildCfVocab();
    }

    // The library-wide vocabulary: every distinct custom-field name, and each name's distinct values.
    private void RebuildCfVocab()
    {
        var byName = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var g in PluginHelper.DataManager?.GetAllGames() ?? Array.Empty<IGame>())
                foreach (var c in Safe(() => g.GetAllCustomFields()) ?? Array.Empty<ICustomField>())
                {
                    string n = (Safe(() => c.Name) ?? "").Trim(); if (n.Length == 0) continue;
                    if (!byName.TryGetValue(n, out var set)) byName[n] = set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                    string v = (Safe(() => c.Value) ?? "").Trim(); if (v.Length > 0) set.Add(v);
                }
        }
        catch { }
        _cfValuesByName = byName.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
    }

    private string MergeCfValue(string name)
    {
        string? first = null;
        foreach (var g in _editGames)
        {
            string v = CfValueOf(g, name);
            if (first == null) first = v; else if (first != v) return Multi;
        }
        return first ?? "";
    }
    private static string CfValueOf(IGame g, string name)
    {
        try { foreach (var c in g.GetAllCustomFields()) if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) return c.Value ?? ""; }
        catch { }
        return "";
    }

    // Delete a grid row: an existing field → confirm + remove library-wide; an unsaved new row → drop it.
    // The DataGridView keeps a fresh empty new-row at the bottom automatically (AllowUserToAddRows).
    private void DeleteCfRow(DataGridViewRow row)
    {
        if (_cfGrid == null || row.IsNewRow) return;
        string orig = row.Tag as string ?? "";
        if (!string.IsNullOrEmpty(orig))
        {
            var ans = MessageBox.Show(this,
                $"Delete the custom field \"{orig}\" from ALL games in your library?",
                "Delete Custom Field", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
            if (ans != DialogResult.OK) return;
            DeleteCustomFieldLibraryWide(orig);
            RebuildCfVocab();
        }
        try { _cfGrid.Rows.Remove(row); } catch { }
    }

    private void RenameCustomFieldLibraryWide(string oldName, string newName)
    {
        foreach (var g in PluginHelper.DataManager?.GetAllGames() ?? Array.Empty<IGame>())
            foreach (var c in Safe(() => g.GetAllCustomFields()) ?? Array.Empty<ICustomField>())
                if (string.Equals(Safe(() => c.Name), oldName, StringComparison.OrdinalIgnoreCase))
                    try { c.Name = newName; } catch { }
    }
    private void DeleteCustomFieldLibraryWide(string name)
    {
        foreach (var g in PluginHelper.DataManager?.GetAllGames() ?? Array.Empty<IGame>())
            foreach (var c in Safe(() => g.GetAllCustomFields()) ?? Array.Empty<ICustomField>())
                if (string.Equals(Safe(() => c.Name), name, StringComparison.OrdinalIgnoreCase))
                    try { g.TryRemoveCustomField(c); } catch { }
    }

    // ── Metadata page ────────────────────────────────────────────────────
    private Control BuildMetadataPage()
    {
        var p = new Panel { BackColor = Bg, AutoScroll = true };
        int y = 10;

        // Title (full width) + a disabled "Search for Metadata" placeholder (scraping is ExtendDB's job).
        Cap("Title", Lx, y, Lw, p);
        _title = Txt("", LFx, y, FullW - 168, p);
        var search = MiniBtn("Search for Metadata", new Point(LFx + FullW - 158, y - 1), 158);
        search.Enabled = false;
        var tip = new ToolTip(); tip.SetToolTip(search, "Metadata scraping is provided by the ExtendDB plugin.");
        p.Controls.Add(search);
        y += RowH;

        // Release Date | Rating
        Cap("Release Date", Lx, y, Lw, p); _releaseDate = DateField(LFx, y, FW, p);
        Cap("Rating", Rx, y, Rw, p); _rating = Cbo("", "Rating", RFx, y, FW, p);
        y += RowH;

        // Release Type | Max Players
        Cap("Release Type", Lx, y, Lw, p); _releaseType = Cbo("", "ReleaseType", LFx, y, FW, p);
        Cap("Max Players", Rx, y, Rw, p); _maxPlayers = Num(0, 0, 64, RFx, y, 80, p);
        y += RowH;

        // Genre | Platform
        Cap("Genre", Lx, y, Lw, p); _genre = Cbo("", "Genre", LFx, y, FW, p);
        Cap("Platform", Rx, y, Rw, p); _platform = Cbo("", "Platform", RFx, y, FW, p);
        y += RowH;

        // Developer | Publisher
        Cap("Developer", Lx, y, Lw, p); _developer = Cbo("", "Developer", LFx, y, FW, p);
        Cap("Publisher", Rx, y, Rw, p); _publisher = Cbo("", "Publisher", RFx, y, FW, p);
        y += RowH;

        // Series | Region
        Cap("Series", Lx, y, Lw, p); _series = Cbo("", "Series", LFx, y, FW, p);
        Cap("Region", Rx, y, Rw, p); _region = Cbo("", "Region", RFx, y, FW, p);
        y += RowH;

        // Play Mode | Version
        Cap("Play Mode", Lx, y, Lw, p); _playMode = Cbo("", "PlayMode", LFx, y, FW, p);
        Cap("Version", Rx, y, Rw, p); _version = Txt("", RFx, y, FW, p);
        y += RowH;

        // Status | Source
        Cap("Status", Lx, y, Lw, p); _status = Cbo("", "Status", LFx, y, FW, p);
        Cap("Source", Rx, y, Rw, p); _source = Cbo("", "Source", RFx, y, FW, p);
        y += RowH;

        // Last Played | Progress
        Cap("Last Played", Lx, y, Lw, p); _lastPlayed = DateField(LFx, y, FW, p);
        Cap("Progress", Rx, y, Rw, p); _progress = Cbo("", "Progress", RFx, y, FW, p);
        y += RowH;

        // Video URL (full) | Wikipedia URL (full)
        Cap("Video URL", Lx, y, Lw, p); _videoUrl = Txt("", LFx, y, FullW, p); y += RowH;
        Cap("Wikipedia URL", Lx, y, Lw, p); _wikiUrl = Txt("", LFx, y, FullW, p); y += RowH;

        // Star rating — same widget language as the main window's hero panel: cyan = community/default
        // rating, yellow = the user's own rating. Click a star to set your rating; click the current
        // value again to clear it (reverts to the community rating shown in cyan).
        Cap("Star Rating", Lx, y, Lw, p);
        _starBar = new StarBar { Location = new Point(LFx, y - 2), ReadOnly = _readOnly };
        _starBar.Changed = () => OnField(_starBar);
        p.Controls.Add(_starBar); Track(_starBar, p);
        y += RowH + 6;

        // ── Read-only info + flags ───────────────────────────────────────
        _dateAdded = InfoLabel("Date Added:", Lx, y, p);
        var favorite = ChkBox("Favorite", RFx + 8, y); _favorite = favorite;
        var portable = ChkBox("Portable", RFx + 130, y); _portable = portable;
        y += 24;
        _dateModified = InfoLabel("Date Modified:", Lx, y, p);
        _installed = ChkBox("Installed", RFx + 8, y);
        _hide = ChkBox("Hide", RFx + 130, y);
        y += 24;
        _playCount = InfoLabel("Play Count(Time):", Lx, y, p);
        _broken = ChkBox("Broken", RFx + 8, y);
        foreach (var cb in new[] { _favorite, _portable, _installed, _hide, _broken }) p.Controls.Add(cb);

        return p;
    }

    // Star-rating widget mirroring MainWindow.HeroPanel: cyan when showing the community/default
    // rating, yellow once the user sets their own; empty stars are faint white; a numeric to the left.
    // Click a star to set the user rating; click the current value to clear it (back to community).
    private sealed class StarBar : Panel
    {
        private static readonly Color Community = Color.FromArgb(0x38, 0xD6, 0xE6);   // cyan (default/community)
        private static readonly Color User      = Color.FromArgb(0xF6, 0xC3, 0x44);   // yellow (user-set)
        private static readonly Color Empty     = Color.FromArgb(105, 255, 255, 255); // faint white
        // A self-contained custom control (its own Panel subclass nested in EditGameWindow) - it
        // can't reach the outer class's _s/S(), so it computes its own from DeviceDpi, same idea.
        private readonly float _s;
        private int S(int px) => (int)Math.Round(px * _s);
        private readonly int StarW, NumW;

        // Ratings are HALF-STAR precise (0, 0.5, 1, … 5) — StarRatingFloat is a float.
        private double _userValue;   // 0..5 in .5 steps — persisted (0 = not rated → show community)
        private double _community;   // community average (display fallback, drawn to its exact fraction)
        private double _hoverValue = -1;
        private readonly Rectangle[] _rects = new Rectangle[5];
        private Rectangle _clearRect;   // the ✕ hit-area (only present when a user rating is set)
        private bool _hoverClear;

        public bool ReadOnly;
        public Action? Changed;   // fired when the user rating changes (drives the parent's touched-set)

        public StarBar()
        {
            DoubleBuffered = true;
            _s = DeviceDpi / 96f;
            StarW = S(22); NumW = S(34);
            Height = S(26); Width = NumW + 5 * StarW + S(34);   // room for the ✕ clear button
            BackColor = Bg;
        }

        public double UserValue => _userValue;
        public bool Dirty;   // reddish numeric when the user rating differs from its loaded value
        public void SetFrom(double userRating, double community)
        {
            _userValue = Math.Round(userRating * 2) / 2.0;   // snap the user rating to the nearest half
            _community = community;
            Invalidate();
        }
        public void SetUserValue(double v) { _userValue = Math.Round(v * 2) / 2.0; Invalidate(); }

        private bool IsUser => _userValue > 0;
        private double Effective => IsUser ? _userValue : _community;

        // The half-precise value under the mouse: left half of star i → i+0.5, right half → i+1; -1 outside.
        private double ValueAt(int mouseX)
        {
            for (int i = 0; i < 5; i++)
                if (mouseX >= _rects[i].X && mouseX < _rects[i].Right)
                    return mouseX < _rects[i].X + StarW / 2.0 ? i + 0.5 : i + 1;
            return -1;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var numFont = new Font("Segoe UI", 9f, FontStyle.Bold);
            using var starFont = new Font("Segoe UI Symbol", 13f);
            var sfNum = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center };
            var sfStar = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

            bool hovering = !ReadOnly && _hoverValue > 0;
            double eff = hovering ? _hoverValue : Effective;
            var fill = hovering ? User : (IsUser ? User : Community);

            string num = eff > 0 ? eff.ToString("0.0", CultureInfo.InvariantCulture) : "—";
            using (var tb = new SolidBrush(Dirty ? ModifiedColor : Color.FromArgb(200, 200, 205)))
                g.DrawString(num, numFont, tb, new RectangleF(0, 0, NumW, Height), sfNum);

            int sx = NumW + S(4);
            for (int i = 0; i < 5; i++)
            {
                _rects[i] = new Rectangle(sx + i * StarW, 0, StarW, Height);
                var cell = new RectangleF(sx + i * StarW, 0, StarW, Height);
                using (var be = new SolidBrush(Empty)) g.DrawString("★", starFont, be, cell, sfStar);   // empty base

                double frac = Math.Max(0, Math.Min(1, eff - i));   // 0, .5 (half) or partial for community
                if (frac > 0)
                {
                    var saved = g.Clip;
                    g.SetClip(new RectangleF(cell.X, cell.Y, (float)(StarW * frac), Height));
                    using (var bf = new SolidBrush(fill)) g.DrawString("★", starFont, bf, cell, sfStar);
                    g.Clip = saved;
                }
            }

            // Clear affordance — shown only when the user has set their own rating (not read-only):
            // removes it, reverting to the community rating (cyan). Reddens on hover.
            if (IsUser && !ReadOnly)
            {
                int cx = sx + 5 * StarW + S(6);
                int clearW = S(20);
                _clearRect = new Rectangle(cx, 0, clearW, Height);
                using var cb = new SolidBrush(_hoverClear ? Color.FromArgb(235, 120, 120) : Color.FromArgb(140, 140, 150));
                g.DrawString("✕", numFont, cb, new RectangleF(cx, 0, clearW, Height), sfStar);
            }
            else _clearRect = Rectangle.Empty;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (ReadOnly) return;
            bool oc = _clearRect.Contains(e.Location);
            double v = oc ? -1 : ValueAt(e.X);
            Cursor = (v > 0 || oc) ? Cursors.Hand : Cursors.Default;
            if (v != _hoverValue || oc != _hoverClear) { _hoverValue = v; _hoverClear = oc; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hoverValue != -1 || _hoverClear) { _hoverValue = -1; _hoverClear = false; Invalidate(); }
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (ReadOnly) return;
            if (_clearRect.Contains(e.Location)) { if (_userValue > 0) { _userValue = 0; Invalidate(); Changed?.Invoke(); } return; }
            double v = ValueAt(e.X);
            if (v <= 0) return;
            _userValue = Math.Abs(_userValue - v) < 0.01 ? 0 : v;   // click the current value → clear (revert to community)
            Invalidate(); Changed?.Invoke();
        }
    }

    // ── Load / Save ──────────────────────────────────────────────────────
    // Loads the common value of each field across ALL edited games (or the "‹multiple values›"
    // placeholder when they differ). Touch-tracking is suppressed during the load.
    private void LoadMetadata()
    {
        _loading = true;
        try
        {
            SetText(_title, MergeStr(g => g.Title));
            SetText(_version, MergeStr(g => g.Version));
            SetText(_videoUrl, MergeStr(g => g.VideoUrl));
            SetText(_wikiUrl, MergeStr(g => g.WikipediaUrl));
            SetText(_notes, DisplayNl(MergeRaw(g => (g.Notes ?? "").Replace("\r\n", "\n"))));
            if (!IsMulti) SetText(_sortTitle, MergeStr(g => g.SortTitle));   // single-game only; multi leaves it empty/disabled
            SetText(_releaseDate, MergeDate(g => g.ReleaseDate));
            SetText(_lastPlayed, MergeDate(g => g.LastPlayedDate));
            SetCombo(_rating, MergeStr(g => g.Rating));
            SetCombo(_releaseType, MergeStr(g => g.ReleaseType));
            SetCombo(_genre, MergeStr(g => g.GenresString));
            SetCombo(_platform, MergeStr(g => g.Platform));
            SetCombo(_developer, MergeStr(g => g.Developer));
            SetCombo(_publisher, MergeStr(g => g.Publisher));
            SetCombo(_series, MergeStr(g => g.Series));
            SetCombo(_region, MergeStr(g => g.Region));
            SetCombo(_playMode, MergeStr(g => g.PlayMode));
            SetCombo(_status, MergeStr(g => g.Status));
            SetCombo(_source, MergeStr(g => g.Source));
            SetCombo(_progress, MergeStr(g => g.Progress));

            _maxPlayers.Value = Clamp(MergeVal(g => g.MaxPlayers ?? 0) ?? 0, 0, 64);
            _starBar.SetFrom(MergeVal(g => (double)g.StarRatingFloat) ?? 0, MergeVal(g => (double)g.CommunityStarRating) ?? 0);

            SetCheck(_favorite, MergeVal(g => g.Favorite));
            SetCheck(_portable, MergeVal(g => g.Portable));
            SetCheck(_hide, MergeVal(g => g.Hide));
            SetCheck(_broken, MergeVal(g => g.Broken));
            SetCheck(_installed, MergeVal(g => g.Installed == true));

            if (IsMulti)
            {
                _dateAdded.Text = $"{_editGames.Count} games selected";
                _dateModified.Text = "";
                _playCount.Text = "";
            }
            else
            {
                var g0 = _editGames[0];
                _dateAdded.Text = "Date Added:   " + FmtDateTime(Safe(() => g0.DateAdded));
                _dateModified.Text = "Date Modified:   " + FmtDateTime(Safe(() => g0.DateModified));
                int pc = Safe(() => g0.PlayCount); int pt = Safe(() => g0.PlayTime);
                _playCount.Text = $"Play Count(Time):   {pc} ({FmtDuration(pt)})";
            }
        }
        finally
        {
            // Baseline = the just-loaded value of every field; then paint each field's dirty state
            // (all clean at this point → normal colour, ↺ hidden).
            foreach (var c in _fields) { _baseline[c] = ValueStr(c); RefreshFieldState(c); }
            _loading = false;
        }

        void SetText(TextBox t, string v) { t.Text = v; t.ForeColor = v == Multi ? SubFg : Fg; }
        void SetCombo(ComboBox c, string v) { c.Text = v; c.ForeColor = v == Multi ? SubFg : Fg; }
        void SetCheck(CheckBox cb, bool? merged)
        {
            cb.ThreeState = IsMulti;
            cb.CheckState = merged.HasValue ? (merged.Value ? CheckState.Checked : CheckState.Unchecked) : CheckState.Indeterminate;
        }
        string MergeStr(Func<IGame, string> get)
        {
            string? first = null;
            foreach (var g in _editGames) { var v = (Safe(() => get(g)) ?? "").Trim(); if (first == null) first = v; else if (first != v) return Multi; }
            return first ?? "";
        }
        string MergeRaw(Func<IGame, string> get)   // like MergeStr but keeps whitespace (Notes)
        {
            string? first = null;
            foreach (var g in _editGames) { var v = Safe(() => get(g)) ?? ""; if (first == null) first = v; else if (first != v) return Multi; }
            return first ?? "";
        }
        string MergeDate(Func<IGame, DateTime?> get)
        {
            string? first = null;
            foreach (var g in _editGames) { var v = FmtDate(Safe(() => get(g))); if (first == null) first = v; else if (first != v) return Multi; }
            return first ?? "";
        }
        T? MergeVal<T>(Func<IGame, T> get) where T : struct
        {
            bool first = true; T acc = default;
            foreach (var g in _editGames) { T v = Safe(() => get(g)); if (first) { acc = v; first = false; } else if (!acc.Equals(v)) return null; }
            return first ? (T?)null : acc;
        }
    }

    // Writes ONLY the fields the user actually changed (current != loaded baseline), to EVERY edited game.
    // A field still holding the "‹multiple values›" placeholder is never written — so untouched (or
    // reverted) multi-value fields keep each game's own value.
    private void SaveCurrent()
    {
        if (_readOnly) return;
        foreach (var g in _editGames)
        {
            if (Writable(_title)) W(() => g.Title = _title.Text.Trim());
            if (Writable(_rating)) W(() => g.Rating = _rating.Text.Trim());
            if (Writable(_releaseType)) W(() => g.ReleaseType = _releaseType.Text.Trim());
            if (Writable(_genre)) W(() => g.GenresString = _genre.Text.Trim());
            if (Writable(_platform)) W(() => g.Platform = _platform.Text.Trim());
            if (Writable(_developer)) W(() => g.Developer = _developer.Text.Trim());
            if (Writable(_publisher)) W(() => g.Publisher = _publisher.Text.Trim());
            if (Writable(_series)) W(() => g.Series = _series.Text.Trim());
            if (Writable(_region)) W(() => g.Region = _region.Text.Trim());
            if (Writable(_playMode)) W(() => g.PlayMode = _playMode.Text.Trim());
            if (Writable(_version)) W(() => g.Version = _version.Text.Trim());
            if (Writable(_status)) W(() => g.Status = _status.Text.Trim());
            if (Writable(_source)) W(() => g.Source = _source.Text.Trim());
            if (Writable(_progress)) W(() => g.Progress = _progress.Text.Trim());
            if (Writable(_videoUrl)) W(() => g.VideoUrl = _videoUrl.Text.Trim());
            if (Writable(_wikiUrl)) W(() => g.WikipediaUrl = _wikiUrl.Text.Trim());
            if (Writable(_notes)) W(() => g.Notes = _notes.Text.Replace("\r\n", "\n"));
            if (!IsMulti && Writable(_sortTitle)) W(() => g.SortTitle = _sortTitle.Text.Trim());
            if (Writable(_releaseDate)) W(() => g.ReleaseDate = ParseDate(_releaseDate.Text));
            if (Writable(_lastPlayed)) W(() => g.LastPlayedDate = ParseDate(_lastPlayed.Text));
            if (Writable(_maxPlayers)) { int v = (int)_maxPlayers.Value; int? mp = v <= 0 ? (int?)null : v; W(() => g.MaxPlayers = mp); }
            if (Writable(_starBar)) W(() => g.StarRatingFloat = (float)_starBar.UserValue);
            if (Writable(_favorite) && _favorite.CheckState != CheckState.Indeterminate) W(() => g.Favorite = _favorite.Checked);
            if (Writable(_portable) && _portable.CheckState != CheckState.Indeterminate) W(() => g.Portable = _portable.Checked);
            if (Writable(_hide) && _hide.CheckState != CheckState.Indeterminate) W(() => g.Hide = _hide.Checked);
            if (Writable(_broken) && _broken.CheckState != CheckState.Indeterminate) W(() => g.Broken = _broken.Checked);
            if (Writable(_installed) && _installed.CheckState != CheckState.Indeterminate) W(() => g.Installed = _installed.Checked);
        }

        bool Writable(Control c) => Modified(c) && !IsPlaceholder(c);
        static void W(Action a) { try { a(); } catch { } }
    }

    // Prev/Next walk the visible list — single-game mode only (multi edits the whole set at once).
    private void Navigate(int delta)
    {
        if (IsMulti || _visible.Count == 0) return;
        int ni = _index + delta;
        if (ni < 0 || ni >= _visible.Count) return;
        SaveCurrent(); SaveCustomFields();
        _index = ni;
        _editGames = new[] { _visible[_index] };
        LoadMetadata();
        if (_cfGrid != null) LoadCustomFields();
        ReloadGameSavesIfBuilt();   // Game Saves page is per-game — rescan for the new game
        UpdateChrome();
    }

    private void UpdateChrome()
    {
        Text = IsMulti ? $"Edit {_editGames.Count} Games" : "Edit Game: " + (Safe(() => _editGames[0].Title) ?? "");
        bool nav = !IsMulti && _visible.Count > 1 && _index >= 0;
        _prev.Enabled = nav && _index > 0;
        _next.Enabled = nav && _index < _visible.Count - 1;
    }

    // ── Dirty-field tracking (reddish text + per-field ↺ revert) ─────────
    // Registers a control for dirty-tracking and gives it an overlay ↺ button (hidden until modified)
    // at its right edge, which restores the loaded value. Checkboxes register via _fields directly
    // (no ↺ — trivial to toggle back).
    private void Track(Control c, Panel p)
    {
        _fields.Add(c);
        var b = new Button
        {
            Text = "↺", Size = new Size(S(18), S(18)), Visible = false, TabStop = false, Cursor = Cursors.Hand,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(92, 46, 42),          // dark-red fill → clearly reads as a button
            ForeColor = Color.FromArgb(255, 180, 165),
            Font = new Font("Segoe UI Symbol", 9.5f),        // symbol font so the ↺ renders (plain Segoe UI shows tofu)
            FlatAppearance = { BorderSize = 1, MouseOverBackColor = Color.FromArgb(120, 58, 52) },
        };
        b.FlatAppearance.BorderColor = Color.FromArgb(150, 72, 64);
        int cy = c.Top + Math.Max(0, (c.Height - S(18)) / 2);
        b.Location = c switch
        {
            ComboBox => new Point(c.Right - S(36), cy),        // left of the dropdown arrow
            NumericUpDown => new Point(c.Right - S(34), cy),   // left of the spin buttons
            StarBar => new Point(c.Right + S(3), cy),          // just right of the star widget
            _ => new Point(c.Right - S(18), cy),               // over a text box's right edge
        };
        b.Click += (_, _) => RevertField(c);
        _tips.SetToolTip(b, "Restore the original value");
        p.Controls.Add(b); b.BringToFront();
        _revert[c] = b;
    }

    private void OnField(Control c) { if (!_loading && !_restoring) RefreshFieldState(c); }

    private void RefreshFieldState(Control c)
    {
        bool mod = Modified(c);
        if (c is StarBar s) { s.Dirty = mod; s.Invalidate(); }
        else c.ForeColor = mod ? ModifiedColor : (IsPlaceholder(c) ? SubFg : Fg);
        if (_revert.TryGetValue(c, out var b)) b.Visible = mod && !_readOnly;
    }

    private void RevertField(Control c)
    {
        if (!_baseline.TryGetValue(c, out var b)) return;
        _restoring = true;
        try
        {
            switch (c)
            {
                case ComboBox cb: cb.Text = b; break;
                case TextBox t: t.Text = b; break;
                case NumericUpDown n: n.Value = decimal.TryParse(b, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? Math.Max(n.Minimum, Math.Min(n.Maximum, d)) : n.Minimum; break;
                case CheckBox ck: ck.CheckState = Enum.TryParse<CheckState>(b, out var cs) ? cs : CheckState.Unchecked; break;
                case StarBar s: s.SetUserValue(double.TryParse(b, NumberStyles.Any, CultureInfo.InvariantCulture, out var uv) ? uv : 0); break;
            }
        }
        finally { _restoring = false; }
        RefreshFieldState(c);
    }

    private bool Modified(Control c) => _baseline.TryGetValue(c, out var b) && b != ValueStr(c);
    private bool IsPlaceholder(Control c) => (c is TextBox t && t.Text == Multi) || (c is ComboBox cb && cb.Text == Multi);
    private static string ValueStr(Control c) => c switch
    {
        ComboBox cb => cb.Text,
        TextBox t => t.Text,
        NumericUpDown n => n.Value.ToString(CultureInfo.InvariantCulture),
        CheckBox ck => ck.CheckState.ToString(),
        StarBar s => s.UserValue.ToString(CultureInfo.InvariantCulture),
        _ => "",
    };

    private int IndexOf(IGame g)
    {
        string? id = Safe(() => g.Id);
        for (int i = 0; i < _visible.Count; i++)
            if (string.Equals(Safe(() => _visible[i].Id), id, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    // ── Distinct-value choice lists (one pass over the library) ──────────
    // (Combo choice-lists now come from the shared, dirty-tracked MetadataChoicesCache — see Cbo.)

    // ── UI helpers ───────────────────────────────────────────────────────
    private Label Cap(string text, int x, int y, int w, Panel p)
    {
        var l = new Label { Text = text, Location = new Point(x, y + 3), Size = new Size(w, FieldH), ForeColor = SubFg, BackColor = Bg, TextAlign = ContentAlignment.MiddleLeft };
        p.Controls.Add(l); return l;
    }

    private TextBox Txt(string v, int x, int y, int w, Panel p)
    {
        var t = new TextBox { Text = v, Location = new Point(x, y), Width = w, BackColor = Field, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle };
        t.TextChanged += (_, _) => OnField(t);
        p.Controls.Add(t); Track(t, p); return t;
    }

    private ComboBox Cbo(string v, string choiceKey, int x, int y, int w, Panel p)
    {
        var c = new ComboBox
        {
            Location = new Point(x, y), Width = w, DropDownStyle = ComboBoxStyle.DropDown,
            BackColor = Field, ForeColor = Fg, FlatStyle = FlatStyle.Flat,
            // AutoComplete is deferred to first focus (below). Building a ListItems autocomplete index over the
            // library-wide value lists (thousands of Developers/Publishers) at handle-creation froze the window
            // open for ~5s. We now pay it once, per field, only when the user actually edits that field.
        };
        var items = MetadataChoicesCache.Get(choiceKey, PluginHelper.DataManager);   // cached; a rebuild runs only for dirty keys
        if (items.Length > 0) c.Items.AddRange(items);
        c.Text = v;
        bool acDone = false;
        c.Enter += (_, _) =>
        {
            if (acDone || c.Items.Count == 0) return; acDone = true;
            c.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
            c.AutoCompleteSource = AutoCompleteSource.ListItems;
        };
        c.TextChanged += (_, _) => OnField(c);
        p.Controls.Add(c); Track(c, p); return c;
    }

    private NumericUpDown Num(int v, int min, int max, int x, int y, int w, Panel p)
    {
        var n = new NumericUpDown { Location = new Point(x, y), Width = w, Minimum = min, Maximum = max, Value = Clamp(v, min, max), BackColor = Field, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle };
        n.ValueChanged += (_, _) => OnField(n);
        p.Controls.Add(n); Track(n, p); return n;
    }

    private CheckBox ChkBox(string text, int x, int y)
    {
        var cb = new CheckBox { Text = text, Location = new Point(x, y), AutoSize = true, ForeColor = Fg, BackColor = Bg };
        cb.CheckStateChanged += (_, _) => OnField(cb);
        _fields.Add(cb);   // tracked for the reddish "modified" text + save (no ↺ button — a checkbox is trivial to toggle back)
        return cb;
    }

    private Label InfoLabel(string text, int x, int y, Panel p)
    {
        var l = new Label { Text = text, Location = new Point(x, y + 2), AutoSize = true, ForeColor = SubFg, BackColor = Bg };
        p.Controls.Add(l); return l;
    }

    private TextBox DateField(int x, int y, int w, Panel p)
    {
        var t = new TextBox { Location = new Point(x, y), Width = w - 28, BackColor = Field, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle };
        t.TextChanged += (_, _) => OnField(t);
        var b = MiniBtn("▦", new Point(x + w - 26, y - 1), 26);
        b.Click += (_, _) => { if (!_readOnly) ShowDatePopup(b, t); };
        p.Controls.Add(t); Track(t, p); p.Controls.Add(b);
        return t;
    }

    private void ShowDatePopup(Control anchor, TextBox target)
    {
        var pop = new Form { FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, ShowInTaskbar = false, TopMost = true, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        var cal = new MonthCalendar { MaxSelectionCount = 1 };
        if (ParseDate(target.Text) is DateTime d) try { cal.SetDate(d); } catch { }
        cal.DateSelected += (_, e) => { target.Text = e.Start.ToString("d", CultureInfo.CurrentCulture); pop.Close(); };
        pop.Controls.Add(cal);
        try { pop.Location = anchor.PointToScreen(new Point(0, anchor.Height)); } catch { }
        pop.Deactivate += (_, _) => pop.Close();
        pop.Show(this);
    }

    private Button MiniBtn(string text, Point loc, int w) => new()
    {
        Text = text, Location = loc, Size = new Size(S(w), FieldH + S(2)),
        FlatStyle = FlatStyle.Flat, BackColor = Field, ForeColor = Fg,
        FlatAppearance = { BorderSize = 0 }, Font = new Font("Segoe UI", 8.5f),
    };

    private Button FooterBtn(string text, Color back) => new()
    {
        Text = text, Size = new Size(S(92), S(28)),
        FlatStyle = FlatStyle.Flat, BackColor = back, ForeColor = Color.White,
        FlatAppearance = { BorderSize = 0 }, Font = new Font("Segoe UI", 9f, FontStyle.Bold),
    };

    private Button NavBtn(string text) => new()
    {
        Text = text, Size = new Size(S(40), S(28)), Top = S(9),
        FlatStyle = FlatStyle.Flat, BackColor = Field, ForeColor = Fg,
        FlatAppearance = { BorderSize = 0 }, Font = new Font("Segoe UI", 10f),
    };

    private static void DisableInputs(Control root)
    {
        foreach (Control c in root.Controls)
        {
            if (c is TextBox or ComboBox or NumericUpDown or CheckBox) c.Enabled = false;
            else if (c is Button b && b.Text != "▦") { /* keep buttons visible but inert handled by _readOnly */ }
            if (c.HasChildren) DisableInputs(c);
        }
    }

    // ── value helpers ────────────────────────────────────────────────────
    private static string FmtDate(DateTime? d) => d.HasValue ? d.Value.ToString("d", CultureInfo.CurrentCulture) : "";
    private static string FmtDateTime(DateTime d) => d == default ? "" : d.ToString("g", CultureInfo.CurrentCulture);
    private static string FmtDuration(int seconds)
    {
        if (seconds <= 0) return "0h 00m 00s";
        var t = TimeSpan.FromSeconds(seconds);
        return $"{(int)t.TotalHours}h {t.Minutes:00}m {t.Seconds:00}s";
    }
    private static DateTime? ParseDate(string s)
        => DateTime.TryParse((s ?? "").Trim(), CultureInfo.CurrentCulture, DateTimeStyles.None, out var d) ? d.Date : (DateTime?)null;
    private static int Clamp(int v, int min, int max) => Math.Max(min, Math.Min(max, v));
    // WinForms multiline TextBox only breaks on CRLF — normalise any \n to \r\n for display.
    private static string DisplayNl(string s) => s == Multi ? s : s.Replace("\r\n", "\n").Replace("\n", "\r\n");
    private static T? Safe<T>(Func<T?> f) { try { return f(); } catch { return default; } }
}
