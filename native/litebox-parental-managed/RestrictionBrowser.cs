// Per-platform restriction browser (opened from Parental control ▸ Settings ▸ "Restricted games…").
//
// Pick a platform; every game shows colour-tag chips for EACH reason it is restricted — several can apply at
// once, like badges:
//   • RED    "manual"   — this game's ID is in the shared .dat's BlockedId set (toggle it here)
//   • AMBER  "platform" — its platform is on the locked hide-list (RuleEngine.IsNameHidden)
//   • PURPLE "rating"   — its rating fails the Whitelist/Blacklist rules (RuleEngine.IsRatingAllowed)
//
// The list is a virtual, owner-drawn ListView (Details) so even a 30k-game platform opens instantly. Manual
// restrictions are edited by SELECTION (one or many rows) + right-click → Restrict / Unrestrict (double-click
// toggles a single row) — the games' IDs are added/removed in the .dat's BlockedId set and the file is
// rewritten atomically once per action. Platform/rating reasons are config-derived (edit them in Settings).
// Changes take effect on the next LaunchBox launch (the native .bin reads the .dat at arm time).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Data;

namespace LiteBoxParental
{
    internal sealed class RestrictionBrowser : Form
    {
        private sealed class Row { public string Id, Title, Platform, Rating; }

        private static readonly Color ManualColor   = Color.FromArgb(220, 53, 69);   // red
        private static readonly Color PlatformColor = Color.FromArgb(230, 145, 0);   // amber
        private static readonly Color RatingColor   = Color.FromArgb(140, 90, 200);  // purple

        private readonly ParentalDat _dat;
        private readonly ComboBox _platform;
        private readonly TextBox _search;
        private readonly ListView _list;
        private readonly Label _summary;
        private List<Row> _all = new List<Row>();       // current platform, unfiltered
        private List<Row> _view = new List<Row>();       // after the search filter (what the ListView shows)
        private bool _dirty;                             // a manual restriction changed → warn on close

        /// <param name="shared">The config model to edit through (shared with the Settings form so chips reflect
        /// the just-saved rules). When null the browser loads its own copy from disk.</param>
        public RestrictionBrowser(ParentalDat shared = null)
        {
            _dat = shared ?? ParentalDat.Load();

            Text = "Restricted games";
            StartPosition = FormStartPosition.CenterScreen;
            MinimizeBox = false; ShowInTaskbar = false;
            ClientSize = new Size(760, 560);
            Font = new Font("Segoe UI", 9f);
            MinimumSize = new Size(520, 360);

            var top = new Panel { Dock = DockStyle.Top, Height = 40 };
            top.Controls.Add(new Label { Text = "Platform:", AutoSize = true, Location = new Point(12, 12) });
            _platform = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(74, 8), Width = 320 };
            _platform.SelectedIndexChanged += (_, __) => LoadPlatform();
            top.Controls.Add(_platform);
            top.Controls.Add(new Label { Text = "Find:", AutoSize = true, Location = new Point(410, 12) });
            _search = new TextBox { Location = new Point(448, 8), Width = 180 };
            _search.TextChanged += (_, __) => ApplyFilter();
            top.Controls.Add(_search);
            Controls.Add(top);

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 40 };
            _summary = new Label { AutoSize = true, Location = new Point(12, 12), ForeColor = Color.DimGray };
            bottom.Controls.Add(_summary);
            var hint = new Label { Text = "Select one or more games, then right-click to Restrict / Unrestrict.",
                AutoSize = true, Location = new Point(360, 12), ForeColor = Color.DimGray, Anchor = AnchorStyles.Right | AnchorStyles.Top };
            bottom.Controls.Add(hint);
            Controls.Add(bottom);

            var menu = new ContextMenuStrip();
            var miRestrict = new ToolStripMenuItem("Restrict selected");
            miRestrict.Click += (_, __) => SetSelected(true);
            var miUnrestrict = new ToolStripMenuItem("Unrestrict selected");
            miUnrestrict.Click += (_, __) => SetSelected(false);
            menu.Items.Add(miRestrict);
            menu.Items.Add(miUnrestrict);
            // Enable only the moves that make sense for the current selection (some restrictable / some clearable).
            menu.Opening += (_, e) =>
            {
                var sel = SelectedRows();
                if (sel.Count == 0) { e.Cancel = true; return; }
                miRestrict.Enabled = sel.Any(r => !_dat.BlockedIds.Contains(r.Id));
                miUnrestrict.Enabled = sel.Any(r => _dat.BlockedIds.Contains(r.Id));
                miRestrict.Text = sel.Count > 1 ? $"Restrict {sel.Count} games" : "Restrict";
                miUnrestrict.Text = sel.Count > 1 ? $"Unrestrict {sel.Count} games" : "Unrestrict";
            };

            _list = new ListView
            {
                Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, VirtualMode = true,
                OwnerDraw = true, HideSelection = false, MultiSelect = true, ContextMenuStrip = menu
            };
            _list.Columns.Add("Game", 380);
            _list.Columns.Add("Restrictions", 340);
            _list.RetrieveVirtualItem += OnRetrieve;
            _list.DrawColumnHeader += (_, e) => e.DrawDefault = true;
            _list.DrawItem += (_, e) => { };                       // Details view: subitems do the drawing
            _list.DrawSubItem += OnDrawSubItem;
            _list.DoubleClick += (_, __) => ToggleSingle();
            _list.Resize += (_, __) => { try { _list.Columns[1].Width = Math.Max(160, _list.ClientSize.Width - _list.Columns[0].Width - 4); } catch { } };
            Controls.Add(_list);
            _list.BringToFront();

            PopulatePlatforms();
        }

        // ── data ─────────────────────────────────────────────────────────────

        private void PopulatePlatforms()
        {
            try
            {
                var names = new List<string>();
                foreach (var p in PluginHelper.DataManager?.GetAllPlatforms() ?? Array.Empty<IPlatform>())
                {
                    try { var n = p?.Name; if (!string.IsNullOrWhiteSpace(n)) names.Add(n); } catch { }
                }
                names.Sort(StringComparer.OrdinalIgnoreCase);
                foreach (var n in names) _platform.Items.Add(n);
                if (_platform.Items.Count > 0) _platform.SelectedIndex = 0;
            }
            catch (Exception ex) { Log.Line("[Browser] platforms: " + ex.Message); }
        }

        private void LoadPlatform()
        {
            _all = new List<Row>();
            try
            {
                var name = _platform.SelectedItem?.ToString();
                var plat = PluginHelper.DataManager?.GetAllPlatforms()?.FirstOrDefault(p =>
                { try { return string.Equals(p?.Name, name, StringComparison.OrdinalIgnoreCase); } catch { return false; } });
                if (plat != null)
                {
                    foreach (var g in plat.GetAllGames(true, true) ?? Array.Empty<IGame>())
                    {
                        try
                        {
                            _all.Add(new Row
                            {
                                Id = g.Id ?? "",
                                Title = g.Title ?? "",
                                Platform = g.Platform ?? name ?? "",
                                Rating = g.Rating ?? ""
                            });
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex) { Log.Line("[Browser] load platform: " + ex.Message); }
            _all.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase));
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            var q = (_search.Text ?? "").Trim();
            _view = q.Length == 0
                ? new List<Row>(_all)
                : _all.Where(r => r.Title.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            _list.VirtualListSize = _view.Count;
            _list.Invalidate();
            UpdateSummary();
        }

        private void UpdateSummary()
        {
            int manual = _all.Count(r => IsManual(r));
            int plat = _all.Count(r => IsPlatformBanned(r));
            int rating = _all.Count(r => IsRatingBanned(r));
            _summary.Text = $"{_all.Count} games   ·   restricted: {manual} manual, {plat} platform, {rating} rating"
                            + (_view.Count != _all.Count ? $"   ·   {_view.Count} shown" : "");
        }

        // ── reason tests ─────────────────────────────────────────────────────
        private bool IsManual(Row r) => r.Id.Length > 0 && _dat.BlockedIds.Contains(r.Id);
        private bool IsPlatformBanned(Row r) => RuleEngine.IsNameHidden(r.Platform, _dat.HideOn);
        private bool IsRatingBanned(Row r) => _dat.Enabled && !RuleEngine.IsRatingAllowed(r.Rating, _dat.Blacklist, _dat.Rules);

        // ── virtual list ─────────────────────────────────────────────────────
        private void OnRetrieve(object sender, RetrieveVirtualItemEventArgs e)
        {
            var r = (e.ItemIndex >= 0 && e.ItemIndex < _view.Count) ? _view[e.ItemIndex] : null;
            var it = new ListViewItem(r?.Title ?? "");
            it.SubItems.Add("");                 // restrictions column, owner-drawn
            it.Tag = r?.Id ?? "";
            e.Item = it;
        }

        private void OnDrawSubItem(object sender, DrawListViewSubItemEventArgs e)
        {
            if (e.ColumnIndex == 0)
            {
                e.DrawBackground();
                if (e.Item.Selected) { using (var b = new SolidBrush(SystemColors.Highlight)) e.Graphics.FillRectangle(b, e.Bounds); }
                TextRenderer.DrawText(e.Graphics, e.Item.Text, _list.Font, e.Bounds,
                    e.Item.Selected ? SystemColors.HighlightText : _list.ForeColor,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                return;
            }

            // Restrictions column — coloured chips.
            using (var bg = new SolidBrush(e.Item.Selected ? SystemColors.Highlight : _list.BackColor))
                e.Graphics.FillRectangle(bg, e.Bounds);
            var row = (e.ItemIndex >= 0 && e.ItemIndex < _view.Count) ? _view[e.ItemIndex] : null;
            if (row == null) return;

            int x = e.Bounds.Left + 6;
            int cy = e.Bounds.Top + e.Bounds.Height / 2;
            if (IsManual(row))         x = DrawChip(e.Graphics, "manual",   ManualColor,   x, cy);
            if (IsPlatformBanned(row)) x = DrawChip(e.Graphics, "platform", PlatformColor, x, cy);
            if (IsRatingBanned(row))   x = DrawChip(e.Graphics, "rating",   RatingColor,   x, cy);
        }

        /// <summary>Draw a rounded pill at (x, centreY); returns the x for the next chip.</summary>
        private int DrawChip(Graphics g, string text, Color color, int x, int centreY)
        {
            var font = _list.Font;
            var sz = TextRenderer.MeasureText(g, text, font);
            int padX = 9, h = Math.Min(20, _list.Font.Height + 6);
            int w = sz.Width + padX * 2;
            var rect = new Rectangle(x, centreY - h / 2, w, h);
            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Rounded(rect, h / 2))
            using (var b = new SolidBrush(color))
                g.FillPath(b, path);
            g.SmoothingMode = old;
            TextRenderer.DrawText(g, text, font, rect, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            return x + w + 6;
        }

        private static GraphicsPath Rounded(Rectangle r, int radius)
        {
            int d = Math.Max(1, radius * 2);
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // ── manual restriction (selection-based, one write per action) ────────

        /// <summary>The distinct games currently selected (valid IDs only).</summary>
        private List<Row> SelectedRows()
        {
            var rows = new List<Row>();
            foreach (int i in _list.SelectedIndices)
                if (i >= 0 && i < _view.Count && !string.IsNullOrEmpty(_view[i].Id)) rows.Add(_view[i]);
            return rows;
        }

        /// <summary>Restrict (true) or unrestrict (false) every selected game, then save the .dat once.</summary>
        private void SetSelected(bool restrict)
        {
            var sel = SelectedRows();
            if (sel.Count == 0) return;

            var changed = new List<string>();
            foreach (var r in sel)
            {
                bool has = _dat.BlockedIds.Contains(r.Id);
                if (restrict && !has) { _dat.BlockedIds.Add(r.Id); changed.Add(r.Id); }
                else if (!restrict && has) { _dat.BlockedIds.Remove(r.Id); changed.Add(r.Id); }
            }
            if (changed.Count == 0) return;

            if (!_dat.Save())
            {
                // Roll back so the in-memory set matches the file that couldn't be written.
                foreach (var id in changed) { if (restrict) _dat.BlockedIds.Remove(id); else _dat.BlockedIds.Add(id); }
                MessageBox.Show("Couldn't write the config file (read-only, or the file is locked).",
                    "Parental control", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            _dirty = true;
            _list.Invalidate();
            UpdateSummary();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            if (_dirty) { _dirty = false; RestartNotice.Show(this); }
        }

        /// <summary>Double-click convenience: flip the single focused row.</summary>
        private void ToggleSingle()
        {
            var sel = SelectedRows();
            if (sel.Count != 1) return;
            SetSelected(!_dat.BlockedIds.Contains(sel[0].Id));
        }
    }
}
