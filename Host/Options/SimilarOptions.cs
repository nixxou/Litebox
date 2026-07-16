// Options → Similar Games — the native LiteBox editor for the suggester rule sets.
//
// "Similar Games" is a standalone feature (NOT one of the LbModules); it gets its own top-level options
// SECTION, registered next to "Modules". This panel reproduces LaunchBox's "Related Games" config as a
// nested TabControl: a Global tab (scoring layer + debug) plus one tab each for Similar / Recommended /
// Possible Ports.
//
// THE DOUBLE-REGISTRATION (the crux — see Host/Similar/GameSuggesterConfig.cs):
//   Every category has a "Use LaunchBox's config (read-only mirror)" switch. CHECKED (default) → that
//   category MIRRORS LaunchBox's Settings.xml, grid read-only. UNCHECKED → LiteBox keeps its OWN editable
//   copy in Core\litebox\game-suggester.json; toggling seeds it from LaunchBox so it starts identical,
//   then diverges as the user edits. The resolver (SuggesterResolver) reads the same switch, so the UI
//   and the engine always agree.
//
// Look: built entirely through ModulePanelKit so it matches the other module panels (dark GroupBoxes,
// themed grid, live palette). Grids are read-only when mirroring, editable when overridden.

#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using LbApiHost.Host.Similar;
using LbApiHost.Host.UiKit;

namespace LbApiHost.Host.Options;

internal static class SimilarOptions
{
    public static (Control panel, Action apply) Build(float dpiS, bool readOnly)
    {
        var view = new View(dpiS, readOnly);
        return (view.Panel, view.Apply);
    }

    // ── Label ↔ data maps ───────────────────────────────────────────────────────────────────────

    // The 14 fields the (future) engine will actually evaluate, in LB display order. Exposing LB's full
    // ~48-field list would let users author silently-ignored rules.
    private static readonly (string key, string label)[] Fields =
    {
        ("Title", "Title"), ("AlternateName", "Alternate Name"), ("Series", "Series"),
        ("Genre", "Genre"), ("PlayMode", "Play Mode"), ("MaxPlayers", "Max Players"),
        ("Platform", "Platform"), ("Rating", "Rating"), ("Developer", "Developer"),
        ("Publisher", "Publisher"), ("Notes", "Notes"), ("ReleaseType", "Release Type"),
        ("StarRating", "Star Rating"), ("Storefront", "Storefront"),
    };

    private static readonly (ComparisonType type, string label)[] Comparisons =
    {
        (ComparisonType.EqualTo, "Is Equal To"), (ComparisonType.NotEqualTo, "Is Not Equal To"),
        (ComparisonType.Contains, "Contains"), (ComparisonType.NotContains, "Does Not Contain"),
        (ComparisonType.StartsWith, "Starts With"), (ComparisonType.StartsWithNone, "Starts With None"),
        (ComparisonType.IsEmpty, "Is Empty"), (ComparisonType.IsNotEmpty, "Is Not Empty"),
        (ComparisonType.AtLeastOneOf, "At Least One Of"), (ComparisonType.NoneOf, "None Of"),
        (ComparisonType.ContainsAnyValue, "Contains Any Value"), (ComparisonType.ContainsNoValue, "Contains No Value"),
        (ComparisonType.IsSimilarTo, "Is Similar To"), (ComparisonType.IsNotSimilarTo, "Is Not Similar To"),
        (ComparisonType.GreaterThan, "Is Greater Than"), (ComparisonType.LessThan, "Is Less Than"),
        (ComparisonType.IsAmazon, "Is Amazon"), (ComparisonType.IsSteam, "Is Steam"),
        (ComparisonType.IsGog, "Is GOG"), (ComparisonType.IsEpic, "Is Epic"),
        (ComparisonType.IsEa, "Is EA"), (ComparisonType.IsUbisoft, "Is Ubisoft"),
        (ComparisonType.IsMicrosoft, "Is Microsoft"),
    };

    private static readonly (FilterScope scope, string label)[] Scopes =
    {
        (FilterScope.AllGames, "All Games"),
        (FilterScope.LocalGamesOnly, "Local Games Only"),
        (FilterScope.DatabaseGamesOnly, "Database Games Only"),
    };

    private const string ValGame = "Game Value";
    private const string ValCustom = "Custom Value";
    private const string WeightRequired = "Required";

    private static readonly Color Warn = Color.FromArgb(220, 170, 90);

    // ── The view ────────────────────────────────────────────────────────────────────────────────

    private sealed class CatUi
    {
        public SuggesterCategory Cat;
        public bool RevertRequested;
        public SuggesterConfig? LbConfig;          // LaunchBox's Settings.xml config, or null when invalid
        public CheckBox UseLb = null!;             // per-tab: mirror LB (checked) vs own config
        public DataGridView Grid = null!;
        public CheckBox AllowDb = null!;
        public NumericUpDown MinScore = null!;
        public Button Revert = null!;
        public Button CopyFromLb = null!;
        public Label Banner = null!;
    }

    private sealed class View
    {
        private readonly float _dpiS;
        private readonly bool _readOnly;
        public readonly Control Panel;

        private readonly Dictionary<SuggesterCategory, CatUi> _cats = new();
        private Label _statusSimilar = null!, _statusRecommended = null!, _statusPorts = null!;

        private CheckBox _cGraded = null!;
        private CheckBox _bLocal = null!;
        private NumericUpDown _nLocalCap = null!;
        private NumericUpDown _nSim = null!;
        private CheckBox _cShowReport = null!;

        private int S(int px) => ModulePanelKit.Sc(_dpiS, px);
        private static bool OwnMode(CatUi ui) => ui.UseLb is { Checked: false };

        public View(float dpiS, bool readOnly)
        {
            _dpiS = dpiS;
            _readOnly = readOnly;

            var host = new System.Windows.Forms.Panel { Dock = DockStyle.Fill, BackColor = ModulePanelKit.Bg };
            var tabs = new TabControl { Dock = DockStyle.Fill };
            tabs.TabPages.Add(BuildGlobalTab());
            tabs.TabPages.Add(BuildCategoryTab(SuggesterCategory.SimilarGames, "Similar Games"));
            tabs.TabPages.Add(BuildCategoryTab(SuggesterCategory.RecommendedGames, "Recommended Games"));
            tabs.TabPages.Add(BuildCategoryTab(SuggesterCategory.PossiblePorts, "Possible Ports"));
            host.Controls.Add(tabs);
            Panel = host;

            LoadAll();
        }

        // ── Global tab ────────────────────────────────────────────────────────────────────────

        private TabPage BuildGlobalTab()
        {
            var page = new TabPage("Global options") { BackColor = ModulePanelKit.Bg };
            var stack = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false,
                AutoScroll = true, BackColor = ModulePanelKit.Bg, Padding = new Padding(S(6)),
            };
            page.Controls.Add(stack);

            var head = ModulePanelKit.Header("Similar Games", _dpiS);
            head.Margin = new Padding(S(8), S(10), 0, S(2));
            stack.Controls.Add(head);

            var intro = ModulePanelKit.Caption(
                "These rules drive the \"Related Games\" suggestions (Similar, Recommended, Possible Ports). "
                + "LiteBox keeps its own copy of each category's rules that, by default, mirrors LaunchBox's. "
                + "Each category tab has a \"Use LaunchBox's config\" switch — uncheck it to edit that "
                + "category's own independent copy (the built-in default preset is used when LaunchBox has none).",
                _dpiS, 720);
            intro.Margin = new Padding(S(8), 0, 0, S(8));
            stack.Controls.Add(intro);

            stack.Controls.Add(BuildStatusGroup());
            stack.Controls.Add(BuildGenreGroup());
            stack.Controls.Add(BuildScoringGroup());
            stack.Controls.Add(BuildDebugGroup());
            return page;
        }

        private GroupBox BuildStatusGroup()
        {
            var grp = ModulePanelKit.Group("Current source per category", _dpiS);
            grp.Size = new Size(S(720), S(120));
            grp.Margin = new Padding(S(8), S(4), 0, S(6));
            _statusSimilar     = StatusLine(S(26));
            _statusRecommended = StatusLine(S(52));
            _statusPorts       = StatusLine(S(78));
            grp.Controls.Add(_statusSimilar);
            grp.Controls.Add(_statusRecommended);
            grp.Controls.Add(_statusPorts);
            return grp;
        }

        private Label StatusLine(int y) => new()
        {
            AutoSize = true, ForeColor = ModulePanelKit.Sub, BackColor = ModulePanelKit.Bg,
            Location = new Point(S(14), y), Font = new Font("Segoe UI", 9f),
        };

        private GroupBox BuildGenreGroup()
        {
            var grp = ModulePanelKit.Group("Genre matching", _dpiS);
            grp.Size = new Size(S(720), S(96));
            grp.Margin = new Padding(S(8), S(4), 0, S(6));

            _cGraded = ModulePanelKit.Check("Graded genre score (LiteBox) — uncheck for strict LaunchBox-style match", _dpiS, false, _readOnly);
            _cGraded.Location = new Point(S(14), S(26));
            grp.Controls.Add(_cGraded);

            var note = ModulePanelKit.Caption(
                "Graded: full weight when the candidate has ALL the target's genres, half when it shares "
                + "≥ 50% of them, plus a tag bonus. Strict: full weight only when it has all of them, "
                + "else 0 — what LaunchBox's \"Equal To\" does.", _dpiS, 690);
            note.Location = new Point(S(30), S(50));
            grp.Controls.Add(note);
            return grp;
        }

        private GroupBox BuildScoringGroup()
        {
            var grp = ModulePanelKit.Group("Scoring", _dpiS);
            grp.Size = new Size(S(720), S(108));
            grp.Margin = new Padding(S(8), S(4), 0, S(6));

            _bLocal = ModulePanelKit.Check("Boost games already in my library", _dpiS, false, _readOnly);
            _bLocal.Location = new Point(S(14), S(28));
            grp.Controls.Add(_bLocal);

            grp.Controls.Add(new Label { Text = "max:", AutoSize = true, ForeColor = ModulePanelKit.Sub, BackColor = ModulePanelKit.Bg, Location = new Point(S(320), S(30)), Font = new Font("Segoe UI", 9f) });
            _nLocalCap = Numeric(0m, 20m, 1m, 0, S(362), S(27), 60);
            grp.Controls.Add(_nLocalCap);
            _bLocal.CheckedChanged += (_, _) => _nLocalCap.Enabled = _bLocal.Checked && !_readOnly;

            grp.Controls.Add(new Label { Text = "\"Is Similar To\" match threshold (0–1, lower = looser):", AutoSize = true, ForeColor = ModulePanelKit.Fg, BackColor = ModulePanelKit.Bg, Location = new Point(S(14), S(66)), Font = new Font("Segoe UI", 9f) });
            _nSim = Numeric(0.05m, 1.00m, 0.05m, 2, S(362), S(63), 60);
            grp.Controls.Add(_nSim);
            return grp;
        }

        private GroupBox BuildDebugGroup()
        {
            var grp = ModulePanelKit.Group("Debug", _dpiS);
            grp.Size = new Size(S(720), S(64));
            grp.Margin = new Padding(S(8), S(4), 0, S(10));
            _cShowReport = ModulePanelKit.Check("Show \"Write Similar Games Report\" right-click entry (debug)", _dpiS, false, _readOnly);
            _cShowReport.Location = new Point(S(14), S(26));
            grp.Controls.Add(_cShowReport);
            return grp;
        }

        private NumericUpDown Numeric(decimal min, decimal max, decimal inc, int dp, int x, int y, int w) => new()
        {
            Minimum = min, Maximum = max, Increment = inc, DecimalPlaces = dp,
            Location = new Point(x, y), Width = S(w), BackColor = ModulePanelKit.Field,
            ForeColor = ModulePanelKit.Fg, BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Segoe UI", 9f), Enabled = !_readOnly,
        };

        // ── Category tab ──────────────────────────────────────────────────────────────────────

        private TabPage BuildCategoryTab(SuggesterCategory cat, string title)
        {
            var page = new TabPage(title) { BackColor = ModulePanelKit.Bg };
            var ui = new CatUi { Cat = cat };

            var header = new System.Windows.Forms.Panel { Dock = DockStyle.Top, Height = S(96), BackColor = ModulePanelKit.Panel };

            ui.UseLb = ModulePanelKit.Check("Use LaunchBox's config for this category (read-only mirror)", _dpiS, true, _readOnly);
            ui.UseLb.BackColor = ModulePanelKit.Panel;
            ui.UseLb.Location = new Point(S(14), S(10));
            ui.UseLb.CheckedChanged += (_, _) => ApplyCategoryMode(ui);
            header.Controls.Add(ui.UseLb);

            ui.AllowDb = ModulePanelKit.Check("Include games not in my library", _dpiS, false, _readOnly);
            ui.AllowDb.BackColor = ModulePanelKit.Panel;
            ui.AllowDb.Location = new Point(S(14), S(36));
            ui.AllowDb.CheckedChanged += (_, _) => ui.RevertRequested = false;
            header.Controls.Add(ui.AllowDb);

            header.Controls.Add(new Label { Text = "Minimum score:", AutoSize = true, ForeColor = ModulePanelKit.Sub, BackColor = ModulePanelKit.Panel, Location = new Point(S(268), S(40)), Font = new Font("Segoe UI", 9f) });
            ui.MinScore = new NumericUpDown
            {
                Minimum = 0, Maximum = 1000, Location = new Point(S(372), S(37)), Width = S(70),
                BackColor = ModulePanelKit.Field, ForeColor = ModulePanelKit.Fg, BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Segoe UI", 9f),
            };
            ui.MinScore.ValueChanged += (_, _) => ui.RevertRequested = false;
            header.Controls.Add(ui.MinScore);

            ui.Revert = ModulePanelKit.Button("Revert to Default", _dpiS, _readOnly);
            ui.Revert.Location = new Point(S(14), S(64));
            ui.Revert.Click += (_, _) => RevertToDefault(ui);
            header.Controls.Add(ui.Revert);

            ui.CopyFromLb = ModulePanelKit.Button("Copy config from LB", _dpiS, _readOnly);
            ui.CopyFromLb.Location = new Point(S(160), S(64));
            ui.CopyFromLb.Click += (_, _) => CopyFromLb(ui);
            header.Controls.Add(ui.CopyFromLb);

            ui.Banner = new Label
            {
                Text = "", ForeColor = Warn, BackColor = ModulePanelKit.Panel, AutoSize = false,
                Location = new Point(S(320), S(66)), Size = new Size(S(380), S(26)),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Font = new Font("Segoe UI", 8.25f, FontStyle.Italic),
            };
            header.Controls.Add(ui.Banner);

            ui.Grid = BuildGrid(ui);
            page.Controls.Add(ui.Grid);
            page.Controls.Add(header);

            _cats[cat] = ui;
            return page;
        }

        private DataGridView BuildGrid(CatUi ui)
        {
            var g = ModulePanelKit.Grid(_dpiS);
            g.Dock = DockStyle.Fill;
            g.RowHeadersVisible = true;
            g.RowHeadersWidth = S(28);
            g.SelectionMode = DataGridViewSelectionMode.CellSelect;
            g.EditMode = DataGridViewEditMode.EditOnEnter;
            g.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle { BackColor = ModulePanelKit.Panel, ForeColor = ModulePanelKit.Fg };

            g.Columns.Add(ComboCol("Field", "Field", Fields.Select(f => f.label)));
            g.Columns.Add(ComboCol("Comparison", "Comparison", Comparisons.Select(c => c.label)));
            g.Columns.Add(ComboCol("ValueType", "Value Type", new[] { ValGame, ValCustom }));
            g.Columns.Add(new DataGridViewTextBoxColumn { Name = "CustomValue", HeaderText = "Custom Value" });
            g.Columns.Add(new DataGridViewTextBoxColumn { Name = "Weight", HeaderText = "Required / Weight" });
            g.Columns.Add(ComboCol("Scope", "Which Games?", Scopes.Select(s => s.label)));

            g.DataError += (_, e) => { e.ThrowException = false; };
            g.CellValueChanged += (_, _) => ui.RevertRequested = false;
            g.UserAddedRow += (_, _) => ui.RevertRequested = false;
            g.UserDeletedRow += (_, _) => ui.RevertRequested = false;
            g.CurrentCellDirtyStateChanged += (_, _) =>
            {
                if (g.IsCurrentCellDirty) g.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            return g;
        }

        private DataGridViewComboBoxColumn ComboCol(string name, string header, IEnumerable<string> items)
        {
            var c = new DataGridViewComboBoxColumn
            {
                Name = name, HeaderText = header, FlatStyle = FlatStyle.Flat, DropDownWidth = S(160),
            };
            foreach (var it in items) c.Items.Add(it);
            return c;
        }

        // ── Load ──────────────────────────────────────────────────────────────────────────────

        private void LoadAll()
        {
            LoadBonuses();
            var store = SuggesterStore.Instance;
            foreach (var cat in new[] { SuggesterCategory.SimilarGames, SuggesterCategory.RecommendedGames, SuggesterCategory.PossiblePorts })
            {
                if (!_cats.TryGetValue(cat, out var ui)) continue;
                ui.LbConfig = LaunchBoxSuggester.GetOrNull(cat);
                ui.UseLb.Checked = store.GetUseLaunchBox(cat);
                ApplyCategoryMode(ui);
            }
        }

        private void LoadBonuses()
        {
            var b = SuggesterStore.Instance.Bonuses;
            _cGraded.Checked = b.GradedGenreScoring;
            _bLocal.Checked = b.LocalLibraryBonusEnabled;
            _nLocalCap.Value = ClampI(_nLocalCap, b.LocalLibraryBonusMax);
            _nSim.Value = ClampD(_nSim, (decimal)b.SimilarityThreshold);
            _nLocalCap.Enabled = _bLocal.Checked && !_readOnly;
            _cShowReport.Checked = SuggesterStore.Instance.ShowReportMenuItem;
        }

        private static decimal ClampI(NumericUpDown n, int v) => Math.Max(n.Minimum, Math.Min(n.Maximum, v));
        private static decimal ClampD(NumericUpDown n, decimal v) => Math.Max(n.Minimum, Math.Min(n.Maximum, v));

        private void FillForMode(CatUi ui)
        {
            SuggesterConfig cfg;
            if (!OwnMode(ui))
                cfg = ui.LbConfig ?? SuggesterDefaults.Default(ui.Cat);
            else
                cfg = SuggesterStore.Instance.Get(ui.Cat)?.ToConfig(ui.Cat)
                      ?? ui.LbConfig ?? SuggesterDefaults.Default(ui.Cat);

            ui.AllowDb.Checked = cfg.AllowDbGames;
            ui.MinScore.Value = ClampI(ui.MinScore, cfg.MinimumScore);
            FillGrid(ui, cfg.Criteria);
            ui.RevertRequested = false;
        }

        private void FillGrid(CatUi ui, List<CriteriaRecord> criteria)
        {
            var g = ui.Grid;
            g.Rows.Clear();
            if (criteria == null) return;

            foreach (var c in criteria)
            {
                int i = g.Rows.Add();
                var row = g.Rows[i];

                var fieldLabel = Fields.FirstOrDefault(f => f.key == c.FieldKey).label ?? c.FieldKey;
                EnsureItem(g, "Field", fieldLabel);
                row.Cells["Field"].Value = fieldLabel;

                var cmpLabel = Comparisons.FirstOrDefault(x => x.type == c.ComparisonTypeKey).label ?? c.ComparisonTypeKey.ToString();
                EnsureItem(g, "Comparison", cmpLabel);
                row.Cells["Comparison"].Value = cmpLabel;

                row.Cells["ValueType"].Value = c.UseGameValue ? ValGame : ValCustom;
                row.Cells["CustomValue"].Value = c.UseGameValue ? "" : c.ComparisonValue;
                row.Cells["Weight"].Value = c.Weight.HasValue ? c.Weight.Value.ToString() : WeightRequired;

                var scopeLabel = Scopes.FirstOrDefault(s => s.scope == c.FilterType).label ?? "All Games";
                EnsureItem(g, "Scope", scopeLabel);
                row.Cells["Scope"].Value = scopeLabel;
            }
        }

        private static void EnsureItem(DataGridView g, string col, string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            if (g.Columns[col] is DataGridViewComboBoxColumn c && !c.Items.Contains(value))
                c.Items.Add(value);
        }

        private void ApplyCategoryMode(CatUi ui)
        {
            FillForMode(ui);

            bool own = OwnMode(ui) && !_readOnly;
            ui.Grid.ReadOnly = !own;
            ui.Grid.AllowUserToAddRows = own;
            ui.Grid.AllowUserToDeleteRows = own;
            ui.AllowDb.Enabled = own;
            ui.MinScore.Enabled = own;
            ui.Revert.Enabled = own;
            ui.CopyFromLb.Enabled = own && ui.LbConfig != null;
            ui.Banner.Text = !OwnMode(ui)
                ? "Mirroring LaunchBox (read-only)."
                : (ui.LbConfig == null ? "Editable. LaunchBox has no valid config for this category." : "Editable.");

            UpdateStatusLine(ui);
        }

        private void UpdateStatusLine(CatUi ui)
        {
            string label = ui.Cat switch
            {
                SuggesterCategory.SimilarGames => "Similar Games",
                SuggesterCategory.RecommendedGames => "Recommended Games",
                SuggesterCategory.PossiblePorts => "Possible Ports",
                _ => ui.Cat.ToString(),
            };
            string srcText;
            if (!OwnMode(ui))
                srcText = ui.LbConfig != null ? "LaunchBox config — mirrored, read-only"
                                              : "Default preset (LaunchBox config invalid) — read-only";
            else
                srcText = SuggesterStore.Instance.Get(ui.Cat) != null
                          ? "Your LiteBox config (editable)"
                          : "Default preset (editable)";

            var lbl = ui.Cat switch
            {
                SuggesterCategory.SimilarGames => _statusSimilar,
                SuggesterCategory.RecommendedGames => _statusRecommended,
                SuggesterCategory.PossiblePorts => _statusPorts,
                _ => null,
            };
            if (lbl != null) lbl.Text = $"{label,-18} :  {srcText}";
        }

        // ── Buttons ───────────────────────────────────────────────────────────────────────────

        private void RevertToDefault(CatUi ui)
        {
            if (!OwnMode(ui) || _readOnly) return;
            var def = SuggesterDefaults.Default(ui.Cat);
            ui.AllowDb.Checked = def.AllowDbGames;
            ui.MinScore.Value = ClampI(ui.MinScore, def.MinimumScore);
            FillGrid(ui, def.Criteria);
            ui.RevertRequested = true;   // Save() drops the override → preset
        }

        private void CopyFromLb(CatUi ui)
        {
            if (!OwnMode(ui) || _readOnly || ui.LbConfig == null) return;
            ui.AllowDb.Checked = ui.LbConfig.AllowDbGames;
            ui.MinScore.Value = ClampI(ui.MinScore, ui.LbConfig.MinimumScore);
            FillGrid(ui, ui.LbConfig.Criteria);
            ui.RevertRequested = false;
        }

        // ── Save ──────────────────────────────────────────────────────────────────────────────

        public void Apply()
        {
            if (_readOnly) return;

            var store = SuggesterStore.Instance;
            store.Bonuses = ReadBonuses();
            store.ShowReportMenuItem = _cShowReport.Checked;

            foreach (var ui in _cats.Values)
            {
                store.SetUseLaunchBox(ui.Cat, ui.UseLb.Checked);
                if (OwnMode(ui))
                {
                    if (ui.RevertRequested)
                        store.Set(ui.Cat, null);            // drop override → default preset
                    else
                        store.Set(ui.Cat, ReadGrid(ui));    // freeze the grid as the own config
                }
                // Mirror mode: leave this category's override untouched (dormant; LB owns the rules).
            }

            store.Save();
            SuggesterStore.Invalidate();   // next resolve/reopen reloads from disk
        }

        private SuggesterStore.BonusSettings ReadBonuses() => new()
        {
            GradedGenreScoring = _cGraded.Checked,
            LocalLibraryBonusEnabled = _bLocal.Checked,
            LocalLibraryBonusMax = (int)_nLocalCap.Value,
            SimilarityThreshold = (double)_nSim.Value,
        };

        private SuggesterStore.CategoryOverride ReadGrid(CatUi ui)
        {
            var result = new SuggesterStore.CategoryOverride
            {
                AllowDbGames = ui.AllowDb.Checked,
                MinimumScore = (int)ui.MinScore.Value,
                Criteria = new List<CriteriaRecord>(),
            };

            foreach (DataGridViewRow row in ui.Grid.Rows)
            {
                if (row.IsNewRow) continue;

                var fieldLabel = row.Cells["Field"].Value as string;
                if (string.IsNullOrWhiteSpace(fieldLabel)) continue;   // skip incomplete rows

                var fieldKey = Fields.FirstOrDefault(f => f.label == fieldLabel).key ?? fieldLabel;

                var cmpLabel = row.Cells["Comparison"].Value as string;
                var cmp = Comparisons.FirstOrDefault(c => c.label == cmpLabel).type;
                if (string.IsNullOrEmpty(cmpLabel)) cmp = ComparisonType.Unknown;

                var valType = row.Cells["ValueType"].Value as string;
                bool useGame = valType != ValCustom;   // default to Game Value when unset

                var custom = useGame ? "" : (row.Cells["CustomValue"].Value as string ?? "");

                var weightTxt = (row.Cells["Weight"].Value as string ?? "").Trim();
                int? weight = ParseWeight(weightTxt);

                var scopeLabel = row.Cells["Scope"].Value as string;
                var scope = Scopes.FirstOrDefault(s => s.label == scopeLabel).scope;

                result.Criteria.Add(new CriteriaRecord
                {
                    FieldKey = fieldKey,
                    ComparisonTypeKey = cmp,
                    UseGameValue = useGame,
                    ComparisonValue = custom,
                    Weight = weight,
                    FilterType = scope,
                });
            }
            return result;
        }

        private static int? ParseWeight(string txt)
        {
            if (string.IsNullOrEmpty(txt)) return null;
            if (string.Equals(txt, WeightRequired, StringComparison.OrdinalIgnoreCase)) return null;
            return int.TryParse(txt, out var w) ? w : (int?)null;
        }
    }
}
