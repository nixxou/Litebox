// Edit Game → "Game Saves" — LiteBox's replica of LaunchBox 13.27's save-management page
// (see docs/saves.md). The whole UI lives in the reusable
// SavesPane control (cards, status dots, per-card action menu, Backup History dialog, the two
// Import buttons) so the SAME pane serves two hosts at full parity:
//   • the Edit Game "Game Saves" page  → Rescan(game, null)      — base-game view (single-game only)
//   • the Edit Additional Version tab  → Rescan(game, version)   — that version's ROM only
// The heavy lifting (plugin scan, <GameSave> records, vault) is Host\Saves\SaveManager; the pane is
// pure UI: two sections (Save Files / Save States) of cards, each with LB's action menu
//   Edit Name / Backup History / Combine With Another Save… / Set as Active / Backup Save /
//   Make New Save / Open Folder / Delete Save
// plus the Import buttons. Every action rescans the pane (cheap — one plugin GetSaves).

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Data;
using LbApiHost.Host.Saves;

namespace LbApiHost.Host;

internal sealed partial class EditGameWindow
{
    private SavesPane? _savesPane;          // cached in _pages["GameSaves"]

    private IGame SavesGame => _editGames[0];

    private Control BuildGameSavesPage()
    {
        _savesPane = new SavesPane(this);
        _savesPane.Rescan(SavesGame, null);
        return _savesPane;
    }

    private void ReloadGameSavesIfBuilt() { if (_savesPane != null && !IsMulti) _savesPane.Rescan(SavesGame, null); }

    // ── Generic dark-dialog helpers (shared with the Additional Versions/Apps dialogs) ──

    private Form NewDialog(string title, int w, int h)
    {
        return new Form
        {
            Text = title, Size = new Size(S(w), S(h)), StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
            ShowIcon = false, ShowInTaskbar = false, BackColor = Bg, ForeColor = Fg, Font = new Font("Segoe UI", 9.5f),
        };
    }

    private Button DlgBtn(string text, Color back)
    {
        var b = new Button
        {
            Text = text, AutoSize = true, Padding = new Padding(S(10), S(2), S(10), S(2)), FlatStyle = FlatStyle.Flat,
            BackColor = back, ForeColor = Color.White, Cursor = Cursors.Hand, Height = S(30),
            FlatAppearance = { BorderSize = 0 },
        };
        return b;
    }

    // ── The reusable saves pane ───────────────────────────────────────────────

    internal sealed class SavesPane : Panel
    {
        private readonly EditGameWindow _w;
        private readonly Panel _content;
        private readonly Button _importFile, _importState;
        private readonly ToolTip _tips = new();
        private SaveScan? _scan;
        private int _seq;                    // guards against stale async scans (navigation)
        private bool _pending;               // Rescan requested before the handle existed
        private IGame? _game;
        private IAdditionalApplication? _focus;   // null → base-game view; else that version's ROM
        private string? _entryFilter;             // null → the MAIN bucket; else a SaveEntry.Key
        private Panel? _entryBar;                 // hidden entirely when the game has only a main bucket

        private int S(int v) => _w.S(v);
        private bool ReadOnlyMode => _w._readOnly;

        public SavesPane(EditGameWindow w)
        {
            _w = w;
            BackColor = Bg;
            Dock = DockStyle.Fill;

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = S(46), BackColor = Bg, Padding = new Padding(S(3)) };
            _importFile = w.FooterBtn("Import Save Game File…", Color.FromArgb(60, 60, 72));
            _importState = w.FooterBtn("Import Save State File…", Color.FromArgb(60, 60, 72));
            _importFile.AutoSize = false;
            _importState.AutoSize = false;
            _importFile.Click += (_, _) => SaveAction_Import(asState: false);
            _importState.Click += (_, _) => SaveAction_Import(asState: true);
            bottom.Controls.AddRange(new Control[] { _importFile, _importState });
            bottom.Resize += (_, _) =>
            {
                int w2 = (bottom.ClientSize.Width - S(18)) / 2;
                _importFile.SetBounds(S(6), S(8), w2, S(30));
                _importState.SetBounds(S(12) + w2, S(8), w2, S(30));
            };

            _content = new Panel { Dock = DockStyle.Fill, BackColor = Bg, AutoScroll = true, Padding = new Padding(S(10), S(6), S(10), S(6)) };
            Controls.Add(_content);
            Controls.Add(bottom);
            _content.BringToFront();

            // A pane inside a tab page has no handle until the tab is first shown — defer the scan.
            HandleCreated += (_, _) => { if (_pending) { _pending = false; Reload(); } };
        }

        /// <summary>Point the pane at a game (and optionally ONE of its versions) and rescan.</summary>
        public void Rescan(IGame game, IAdditionalApplication? focus)
        {
            _game = game;
            _focus = focus;
            _entryFilter = null;
            if (IsHandleCreated) Reload();
            else _pending = true;
        }

        private void Reload()
        {
            if (_game == null) return;
            int seq = ++_seq;
            _scan = null;
            SetMessage("Scanning saves…", italic: true);
            SetImportEnabled(false);

            var game = _game;
            var focus = _focus;
            Task.Run(() =>
            {
                SaveScan scan;
                try { scan = focus == null ? SaveManager.ScanBase(game) : SaveManager.ScanApp(game, focus); }
                catch (Exception ex) { scan = new SaveScan { Error = "Save scan failed:\n" + ex.Message }; }
                try
                {
                    if (IsHandleCreated && !IsDisposed)
                        BeginInvoke(new Action(() => { if (seq == _seq) { _scan = scan; Render(); } }));
                }
                catch { }
            });
        }

        private void SetImportEnabled(bool on)
        {
            bool ok = on && !ReadOnlyMode;
            _importFile.Enabled = ok;
            _importState.Enabled = ok;
        }

        private void SetMessage(string text, bool italic = false, Color? color = null)
        {
            _content.SuspendLayout();
            _content.Controls.Clear();
            _content.Controls.Add(new Label
            {
                Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = color ?? SubFg, BackColor = Bg,
                Font = new Font("Segoe UI", 10f, italic ? FontStyle.Italic : FontStyle.Regular),
                Text = text,
            });
            _content.ResumeLayout();
        }

        // ── Rendering ─────────────────────────────────────────────────────────
        private void Render()
        {
            if (_scan == null) return;
            var scan = _scan;
            if (scan.Error != null) { SetMessage(scan.Error); SetImportEnabled(false); return; }
            SetImportEnabled(scan.Plugin != null);

            // LB parity: the "unsupported emulator" hint only shows when NOTHING was found at all —
            // saves are always searched through every integration plugin, whatever the game's emulator.
            if (scan.Files.Count == 0 && scan.States.Count == 0 && !scan.GameEmulatorSupported)
            {
                string t = scan.GameEmulatorTitle.Length > 0 ? $" ({scan.GameEmulatorTitle})" : "";
                SetMessage($"No saves found for this {(_focus == null ? "game" : "version")}.\n\n"
                    + $"Its emulator{t} has no LaunchBox integration plugin; saves were still searched through the\n"
                    + "supported emulators (RetroArch, Dolphin, PCSX2, …) but none matched this game's files.");
                return;
            }

            BuildEntryBar(scan);
            RenderContent();
        }

        /// <summary>The two lists for the currently selected bucket. Separate from Render so switching
        /// ROM in the picker never rebuilds — and never disposes — the picker that raised the event.</summary>
        private void RenderContent()
        {
            var scan = _scan;
            if (scan == null) return;

            _content.SuspendLayout();
            _content.Controls.Clear();

            void Stack(Control c) { _content.Controls.Add(c); c.Dock = DockStyle.Top; c.BringToFront(); }
            void Header(string text) => Stack(new Label
            {
                Height = S(36), Text = text, ForeColor = Fg, BackColor = Bg,
                Font = new Font("Segoe UI", 12f, FontStyle.Bold), TextAlign = ContentAlignment.BottomLeft, Padding = new Padding(S(2), S(0), S(0), S(6)),
            });
            void Empty(string text) => Stack(new Label
            {
                Height = S(28), Text = text, ForeColor = SubFg, BackColor = Bg,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Italic), TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(S(6), S(0), S(0), S(0)),
            });

            // The MAIN bucket is the saves named after the ApplicationPath and matched by no entry —
            // what a core reading the .zip directly legitimately produces. Not a leftover: with
            // auto-extract off it stays the normal mode.
            bool InBucket(SaveGroup g) => string.Equals(g.EntryKey, _entryFilter, StringComparison.OrdinalIgnoreCase);

            // The ACTIVE group first — it is the one the emulator reads, so it is the one you came to
            // look at. Read off a side-by-side comparison, where it led the list while carrying the
            // OLDEST date of the three, which is what rules out a plain sort by date.
            //
            // Then by SLOT, then newest first. The slot key matters and the comparison could not see it:
            // all three groups in that sample were Slot 0, so "by date" and "by slot" were
            // indistinguishable there. Dropping it — which an earlier version of this did — scattered the
            // slots of a multi-slot game into date order, and the scan had been grouping them by slot all
            // along. Save FILES carry no slot, so they fall straight through to the date.
            static IEnumerable<SaveGroup> Ordered(IEnumerable<SaveGroup> gs) => gs
                .OrderByDescending(g => g.Active != null && g.ActiveLive)
                .ThenBy(g => g.Slot ?? int.MaxValue)
                .ThenByDescending(g => g.LastModified ?? DateTime.MinValue);

            var files = Ordered(scan.Files.Where(InBucket)).ToList();
            var states = Ordered(scan.States.Where(InBucket)).ToList();

            Header("Save Files");
            if (files.Count == 0) Empty("No save files found.");
            else foreach (var g in files) Stack(BuildSaveCard(g));

            Header("Save States");
            if (states.Count == 0) Empty("No save states found.");
            else foreach (var g in states) Stack(BuildSaveCard(g));

            _content.ResumeLayout();
        }

        /// <summary>The ROM picker above the lists. It exists only when this game HAS more than one
        /// bucket — a plain ROM, or an archive whose saves all belong to the main path, shows nothing at
        /// all. Its entries come from the groups actually found, never from the archive listing: a
        /// 200-ROM set must not produce a 200-line dropdown of ROMs that have no save.</summary>
        private void BuildEntryBar(SaveScan scan)
        {
            if (_entryBar != null) { Controls.Remove(_entryBar); _entryBar.Dispose(); _entryBar = null; }


            var all = scan.Files.Concat(scan.States).ToList();
            var buckets = all.Where(g => g.EntryKey != null)
                             .GroupBy(g => g.EntryKey!, StringComparer.OrdinalIgnoreCase)
                             .Select(grp => (Key: grp.Key,
                                             Label: grp.Select(x => x.EntryLabel).FirstOrDefault(l => !string.IsNullOrEmpty(l)) ?? grp.Key,
                                             Count: grp.Count()))
                             .OrderBy(b => b.Label, StringComparer.OrdinalIgnoreCase)
                             .ToList();
            if (buckets.Count == 0) return;   // nothing to choose between — stay invisible

            int mainCount = all.Count(g => g.EntryKey == null);

            var bar = new Panel { Dock = DockStyle.Top, Height = S(40), BackColor = Bg, Padding = new Padding(S(2), S(4), S(2), S(4)) };
            bar.Controls.Add(new Label
            {
                Text = "ROM:", AutoSize = true, ForeColor = SubFg, BackColor = Bg,
                Location = new Point(S(4), S(11)), Font = new Font("Segoe UI", 9f),
            });

            var combo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(S(44), S(7)), Width = S(430),
                BackColor = Field, ForeColor = Fg, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 9f),
            };
            var keys = new List<string?> { null };
            combo.Items.Add($"Main version — {mainCount} save(s)");
            foreach (var b in buckets) { keys.Add(b.Key); combo.Items.Add($"{b.Label} — {b.Count} save(s)"); }

            int sel = keys.FindIndex(k => string.Equals(k, _entryFilter, StringComparison.OrdinalIgnoreCase));
            combo.SelectedIndex = sel >= 0 ? sel : 0;
            combo.SelectedIndexChanged += (_, _) =>
            {
                int i = combo.SelectedIndex;
                if (i < 0 || i >= keys.Count) return;
                if (string.Equals(keys[i], _entryFilter, StringComparison.OrdinalIgnoreCase)) return;
                _entryFilter = keys[i];
                RenderContent();                // re-filter only; no rescan, the groups are already in hand
            };
            bar.Controls.Add(combo);

            Controls.Add(bar);
            bar.BringToFront();
            _content.BringToFront();
            _entryBar = bar;
        }

        private Control BuildSaveCard(SaveGroup g)
        {
            // The record's Title, printed under the group name the way LaunchBox does. Decided here
            // because the card has to be a line taller when there is one.
            string subtitle = g.Record?.GetValueOrDefault("Title") ?? "";
            var card = new Panel { Height = S(subtitle.Length > 0 ? 113 : 96), BackColor = PanelC, Margin = new Padding(S(0)), Padding = new Padding(S(12), S(8), S(10), S(8)) };
            card.Paint += (_, e) =>
            {
                using var pen = new Pen(Color.FromArgb(58, 58, 70));
                e.Graphics.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1);
            };
            // A little breathing room between cards: a transparent spacer painted by the parent Bg.
            var wrap = new Panel { Height = card.Height + S(8), BackColor = Bg, Padding = new Padding(S(0), S(0), S(0), S(8)) };
            wrap.Controls.Add(card);
            card.Dock = DockStyle.Fill;

            // Row 1 — name + slot chip; right-aligned: ⚠ no-backup, [Active] pill, ⋯ menu.
            var name = new Label
            {
                AutoSize = true, ForeColor = Fg, BackColor = PanelC, Location = new Point(S(10), S(8)),
                Font = new Font("Segoe UI", 11.5f, FontStyle.Bold), Text = g.GroupName, UseMnemonic = false,
            };
            card.Controls.Add(name);

            // The chip beside the name: the slot for a save state, otherwise whatever the plugin put in
            // DisplayChipText — Dolphin sets "Disc Save" on a Wii NAND group. Both are LaunchBox's.
            Label? chip = null;
            string chipText = g.IsState
                ? "Slot " + (g.Slot is -1 ? "Auto" : (g.Slot?.ToString() ?? "?"))
                : g.ChipText;
            if (chipText.Length > 0)
            {
                chip = new Label
                {
                    AutoSize = true, ForeColor = SubFg, BackColor = Field, Padding = new Padding(S(6), S(2), S(6), S(2)),
                    Font = new Font("Segoe UI", 8.5f), Text = chipText,
                };
                card.Controls.Add(chip);
            }

            var menuBtn = new Button
            {
                Text = "…", Size = new Size(S(34), S(26)), FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
                BackColor = Field, ForeColor = Fg, Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                FlatAppearance = { BorderColor = Color.FromArgb(70, 70, 84), BorderSize = 1 },
                Enabled = !ReadOnlyMode,
            };
            menuBtn.Click += (_, _) => BuildSaveMenu(g).Show(menuBtn, new Point(0, menuBtn.Height));
            card.Controls.Add(menuBtn);

            // The status pill. LaunchBox shows "★ Active" on the group the emulator actually reads, and a
            // violet "In Vault" on a group that lives only as a copy. We computed InVault from the start
            // and never drew it, so those cards showed nothing at all and looked like a live save with a
            // strange path.
            Label? pill = null;
            if (g.Active != null && g.ActiveLive)
                pill = StatusPill("★ Active", Color.FromArgb(120, 220, 130), Color.FromArgb(80, 160, 95));
            else if (g.InVault)
                pill = StatusPill("◆ In Vault", Color.FromArgb(186, 150, 235), Color.FromArgb(126, 95, 175));
            if (pill != null) card.Controls.Add(pill);

            // Round status dot (LB parity): green ✓ = active save with an up-to-date backup; yellow ! =
            // no/stale backup; red ✕ = record whose file is gone. Absent for pure vault-only groups.
            StatusDot? dot = null;
            if (g.DuplicateRecord)
                dot = new StatusDot(StatusKind.Warn,
                    "Duplicate record. This file is already listed above, under the version that owns it — "
                    + "LaunchBox left this older record behind when a version started covering the game's "
                    + "own ROM. The save itself is fine. Repair save metadata will NOT remove it: LaunchBox "
                    + "keeps these, so we do too. Delete Save on this card drops the record if you want it "
                    + "gone.", S(22));
            else if (g.RecordOnly)
                dot = new StatusDot(StatusKind.Error, "The save file this record points to no longer exists on disk.", S(22));
            else if (g.Active != null)
                dot = g.NeedsBackup
                    ? new StatusDot(StatusKind.Warn, "No up-to-date backup in the vault — use Backup Save to protect it.", S(22))
                    : new StatusDot(StatusKind.Ok, "This save has an up-to-date backup in the vault.", S(22));
            else if (g.InVault)
                // LaunchBox marks these too. Nothing is at risk — the file is in the vault and the
                // emulator cannot touch it — so the dot says "present", not "protected".
                dot = new StatusDot(StatusKind.Ok, "This copy is safe in the vault. Use Set as Active to put it back in play.", S(22));
            if (dot != null) { dot.BackColor = PanelC; card.Controls.Add(dot); }

            // Row 1bis — the record's Title, under the group name. It is a free-text LABEL (a record
            // planted with "Zorglub" displays as "Zorglub"), and LaunchBox prints it here as a subtitle:
            // "Save State 0", "Saved Game", or whatever Edit Label put there. We computed it and never
            // drew it, so a group renamed by the user lost the only clue to which slot it came from.
            Label? sub = null;
            if (subtitle.Length > 0)
            {
                sub = new Label
                {
                    AutoSize = false, ForeColor = SubFg, BackColor = PanelC, Height = S(16),
                    Font = new Font("Segoe UI", 8.5f), AutoEllipsis = true, UseMnemonic = false,
                    Text = subtitle,
                };
                card.Controls.Add(sub);
            }

            // Row 2 — the file path, shown LB-style (relative to the LaunchBox root; full path in the tooltip).
            var path = new Label
            {
                AutoSize = false, ForeColor = SubFg, BackColor = PanelC, Height = S(18),
                Font = new Font("Segoe UI", 9f), AutoEllipsis = true, UseMnemonic = false,
                Text = g.ActivePath.Length > 0 ? DisplaySavePath(g.ActivePath) : "(no file)",
            };
            _tips.SetToolTip(path, g.ActivePath);
            card.Controls.Add(path);

            // Row 3 — date · emulator (core) · size · backups.
            string date = g.LastModified?.ToString("G") ?? "—";
            string emu = g.EmulatorFileName + (g.EmulatorCore.Length > 0 ? $" ({g.EmulatorCore})" : "");
            string size = FmtSize(g.SizeBytes);
            int nb = g.DisplayBackupCount;   // LaunchBox's count, not Backups.Count — see the property
            string backups = nb == 1 ? "1 Backup" : $"{nb} Backups";
            var info = new Label
            {
                AutoSize = false, ForeColor = SubFg, BackColor = PanelC, Height = S(18),
                Font = new Font("Segoe UI", 9f), AutoEllipsis = true, UseMnemonic = false,
                Text = $"🗓 {date}      🕹 {emu}      💾 {size}      🗂 {backups}",
            };
            card.Controls.Add(info);

            void Layout()
            {
                int right = card.ClientSize.Width - S(10);
                menuBtn.Location = new Point(right - menuBtn.Width, S(8));
                int x = menuBtn.Left - S(8);
                if (pill != null) { pill.Location = new Point(x - pill.Width, S(9)); x = pill.Left - S(8); }
                if (dot != null) { dot.Location = new Point(x - dot.Width, S(10)); }
                if (chip != null) chip.Location = new Point(name.Right + S(8), S(13));
                // The subtitle takes a line of its own between the name and the path; without it the
                // path simply moves up, as before.
                int y = S(40);
                if (sub != null) { sub.SetBounds(S(12), y, card.ClientSize.Width - S(24), S(16)); y += S(17); }
                path.SetBounds(S(12), y, card.ClientSize.Width - S(24), S(18));
                info.SetBounds(S(12), y + S(22), card.ClientSize.Width - S(24), S(18));
            }
            card.Resize += (_, _) => Layout();
            Layout();
            return wrap;
        }

        // ── Round status indicator (green ✓ / yellow ! / red ✕), LB-style ────────
        private enum StatusKind { Ok, Warn, Error }

        private sealed class StatusDot : Panel
        {
            private readonly StatusKind _kind;
            public StatusDot(StatusKind kind, string tip, int size)
            {
                _kind = kind;
                Size = new Size(size, size);
                SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer
                         | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
                _tip.SetToolTip(this, tip);
            }
            private static readonly ToolTip _tip = new();
            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                (Color ring, Color glyph, string ch) = _kind switch
                {
                    StatusKind.Ok    => (Color.FromArgb(80, 170, 95),  Color.FromArgb(120, 220, 130), "✓"),
                    StatusKind.Warn  => (Color.FromArgb(200, 160, 40), Color.FromArgb(235, 200, 90),  "!"),
                    _                => (Color.FromArgb(180, 70, 65),  Color.FromArgb(235, 120, 110), "✕"),
                };
                var r = new Rectangle(1, 1, Width - 3, Height - 3);
                using (var fill = new SolidBrush(Color.FromArgb(40, ring)))
                using (var pen = new Pen(ring, 1.6f))
                { e.Graphics.FillEllipse(fill, r); e.Graphics.DrawEllipse(pen, r); }
                TextRenderer.DrawText(e.Graphics, ch, new Font("Segoe UI", 10.5f, FontStyle.Bold), ClientRectangle,
                    glyph, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }

        private static string FmtSize(long? bytes)
        {
            if (bytes is not long b || b < 0) return "—";
            if (b < 1024) return b + " B";
            if (b < 1024 * 1024) return (b / 1024.0).ToString("0.0") + " KB";   // LB shows "8.0 KB"
            return (b / 1024.0 / 1024.0).ToString("0.0") + " MB";
        }

        /// <summary>LB shows save paths relative to the LaunchBox root ("Emulators\RetroArch\saves\…").</summary>
        private static string DisplaySavePath(string abs)
        {
            try
            {
                string root = SaveManager.LbRoot.TrimEnd('\\', '/');
                if (root.Length > 0 && abs.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))
                    return abs.Substring(root.Length + 1);
            }
            catch { }
            return abs;
        }

        // ── The card menu (LB parity) ─────────────────────────────────────────
        private ContextMenuStrip BuildSaveMenu(SaveGroup g)
        {
            var scan = _scan!;
            var m = new ContextMenuStrip();
            bool hasActive = g.Active != null;
            bool hasBackups = g.Backups.Count > 0;
            // Meme bucket seulement. Deux entrees d'une archive sont deux ROMs differentes : les fusionner
            // mettrait les saves de l'une dans le groupe de l'autre, et le groupe resultant ne pourrait
            // plus etre restaure correctement pour aucune des deux.
            var others = (g.IsState ? scan.States : scan.Files)
                .Where(x => !ReferenceEquals(x, g)
                            && string.Equals(x.EntryKey, g.EntryKey, StringComparison.OrdinalIgnoreCase))
                .ToList();

            ToolStripMenuItem Add(string text, bool enabled, Action act)
            {
                var it = new ToolStripMenuItem(text) { Enabled = enabled };
                it.Click += (_, _) => { try { act(); } catch (Exception ex) { SavesError(ex.Message); } };
                m.Items.Add(it);
                return it;
            }

            // An IN VAULT group — record in the vault, no live save — is the case this menu used to get
            // wrong. Its restorable file is not in Backups (FromRecords skips a group's own file, rightly),
            // so hasBackups was false, "Set as Active" greyed out, and Backups.First() would have thrown
            // the moment it was enabled. Measured on LaunchBox: it enables Set as Active AND Make New Save
            // there, and greys only Backup Save — there is no live save to back up.
            var self = SaveVault.SelfEntry(g);
            var promote = self ?? g.Backups.FirstOrDefault();

            // LB parity: History/Combine stay enabled even when empty; "Set as Active" greys out on the
            // card that IS already active.
            Add("Edit Name", true, () => SaveAction_EditName(g));
            Add("Backup History", true, () => SaveAction_History(g));
            Add("Combine With Another Save…", true, () => SaveAction_Combine(g, others));
            Add("Set as Active", !hasActive && promote != null, () => SaveAction_SetActive(g, promote!, scan));
            m.Items.Add(new ToolStripSeparator());
            Add("Backup Save", hasActive, () => SaveAction_Backup(g));
            Add("Make New Save", hasActive || g.InVault, () => SaveAction_MakeNew(g));
            m.Items.Add(new ToolStripSeparator());
            Add("Open Folder", g.ActivePath.Length > 0 || hasBackups, () => SaveAction_OpenFolder(g));
            var del = Add("Delete Save", true, () => SaveAction_Delete(g));
            del.ForeColor = Color.FromArgb(230, 120, 110);
            return m;
        }

        /// <summary>One of the card's status pills — same shape, colour says which state.</summary>
        private Label StatusPill(string text, Color fg, Color border)
        {
            var pill = new Label
            {
                AutoSize = true, ForeColor = fg, BackColor = PanelC,
                Padding = new Padding(S(8), S(3), S(8), S(3)),
                Font = new Font("Segoe UI", 9f, FontStyle.Bold), Text = text,
            };
            pill.Paint += (_, e) =>
            {
                using var pen = new Pen(border);
                e.Graphics.DrawRectangle(pen, 0, 0, pill.Width - 1, pill.Height - 1);
            };
            return pill;
        }

        private void SavesError(string message)
            => MessageBox.Show(FindForm(), message, "Game Saves", MessageBoxButtons.OK, MessageBoxIcon.Warning);

        // ── Actions ───────────────────────────────────────────────────────────

        /// <summary>LaunchBox's "Edit Names" — two fields, not one: the group's name, and the label of
        /// its ACTIVE save (the live record's Title). The label box shows a default when the record
        /// carries none, and submitting that default unchanged writes nothing.</summary>
        private void SaveAction_EditName(SaveGroup g)
        {
            string defLabel = g.Record != null && g.Record.TryGetValue("Title", out var t0) && t0.Length > 0
                ? t0
                : (g.IsState ? $"Save State {g.Slot ?? 0}" : "Saved Game");
            if (!PromptTwo("Edit Names",
                           "Enter a name for this save group.", g.GroupName,
                           "Enter a label for the active save file.", defLabel,
                           out string name, out string label)) return;

            name = name.Trim();
            if (name.Length == 0) return;
            // Only pass the label on when it was actually edited — LaunchBox leaves the record's Title
            // untouched when its prefilled default comes back unchanged.
            string? lbl = (label.Trim() == defLabel || g.Active == null) ? null : label.Trim();
            if (name == g.GroupName && lbl == null) return;
            SaveManager.Rename(g, name, lbl);
            Reload();
        }

        private void SaveAction_Backup(SaveGroup g)
        {
            var r = SaveManager.Backup(g, force: false);
            if (r.Identical)
            {
                if (MessageBox.Show(FindForm(), "The current save is identical to its latest backup.\nCreate another copy anyway?",
                        "Backup Save", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                r = SaveManager.Backup(g, force: true);
            }
            if (r.Error != null) { SavesError(r.Error); return; }
            Reload();
        }

        /// <summary>LaunchBox's "Set as Active" — measured end to end, with every file made identifiable
        /// beforehand so there was no guessing about what landed where.
        ///
        ///   • it asks which SLOT to restore a save state into, and offers "Auto" by default — not the
        ///     slot the backup came from;
        ///   • it archives the save it is about to displace, and the promise on its confirmation
        ///     ("the current active save will be moved into backup history") is kept;
        ///   • identities do not swap. Each group keeps its own and simply changes which file it points
        ///     at: the promoted group takes over the live record, the displaced one follows its save into
        ///     the copy it is archived as.
        ///
        /// That last point is why this needs the scan. The save being displaced can belong to a DIFFERENT
        /// group — which is exactly the In Vault case — and SaveManager.Restore only ever knew about g, so
        /// it would have archived nothing and let the plugin overwrite another group's live save without
        /// a trace.</summary>
        private void SaveAction_SetActive(SaveGroup g, VaultEntry e, SaveScan scan)
        {
            int? slot = null;
            if (g.IsState)
            {
                slot = PromptSlot(e.Slot);
                if (slot == null) return;                       // cancelled
            }

            string destination = "";

            // Archive whatever currently owns the destination, under ITS identity, before the plugin
            // overwrites it.
            foreach (var other in (g.IsState ? scan.States : scan.Files))
            {
                if (ReferenceEquals(other, g) || other.Active == null) continue;
                // Same attribution: the destination is derived from the OWNING ROM — the version's when
                // the save belongs to one, the game's otherwise — so only a group sharing g's owner can
                // be sitting on the file about to be overwritten. Without this, a save FILE (which has no
                // slot to narrow by) made every live group of the game get backed up. The dirty-check
                // made that mostly harmless, but "mostly harmless" is not a reason to touch groups that
                // were never at risk — each copy taken costs one of their retention slots.
                if (!string.Equals(other.AppId ?? "", g.AppId ?? "", StringComparison.OrdinalIgnoreCase)) continue;
                // Et la meme ENTREE : deux entrees d'une archive partagent le meme AppId, donc
                // l'attribution seule ne les separe pas. Elles ecrivent pourtant dans des fichiers
                // differents, et aucune n'est menacee par la restauration de l'autre.
                if (!string.Equals(other.EntryKey, g.EntryKey, StringComparison.OrdinalIgnoreCase)) continue;
                if (g.IsState && other.Slot != slot) continue;
                try { SaveManager.Backup(other, force: false); } catch { }
                // The file about to be overwritten. Remembered here because this loop is the only place
                // that knows which group holds it — after the restore, the record of that path must
                // change hands, or the next scan gives the file straight back to its old owner.
                if (destination.Length == 0) destination = other.ActivePath;
            }

            string? err = SaveManager.Restore(g, e, slot,
                confirmOverwrite: () => MessageBox.Show(FindForm(),
                    "A save file already exists at the emulator's location.\nOverwrite it with this backup?",
                    "Set as Active", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes);
            if (err != null) { SavesError(err); return; }

            // Move the identity, not just the bytes. Without this the promoted group keeps pointing at
            // its vault copy and the card never changes — which is exactly what it did.
            if (destination.Length > 0)
            {
                var rerr = SaveManager.ReassignRecord(g, destination);
                if (rerr != null) SavesError(rerr);
            }
            Reload();
        }

        /// <summary>Asks which ROM inside the archive an imported save belongs to. Returns false when the
        /// user cancels; leaves <paramref name="entry"/> null for the archive itself.
        ///
        /// Silent when there is nothing to choose between — a game that is not an archive, or one whose
        /// entries we cannot read. The question only exists because one archive holds several ROMs, and
        /// an imported save belongs to exactly one of them.
        ///
        /// The default is picked from the FILE NAME, and that is not a guess: an emulator names a save
        /// after the ROM's basename, so "Sonic (USA).srm" names "Sonic (USA).smd" by the same rule that
        /// created it. When the name says nothing, the entry currently being browsed is the better
        /// assumption than the archive.</summary>
        private bool PromptImportEntry(string importedFile, out SaveEntry? entry)
        {
            entry = null;
            List<SaveEntry> entries;
            try { entries = SaveEntries.For(_game!, _focus); } catch { return true; }
            if (entries.Count == 0) return true;                 // rien a demander

            using var f = _w.NewDialog("Which ROM?", 520, 190);
            f.Controls.Add(new Label
            {
                AutoSize = false, ForeColor = Fg, BackColor = Bg, Location = new Point(S(16), S(12)),
                Size = new Size(S(478), S(34)), Font = new Font("Segoe UI", 9.5f), UseMnemonic = false,
                Text = "This game is an archive holding several ROMs.\nWhich one is this save for?",
            });

            var combo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(S(16), S(54)),
                Width = S(478), BackColor = Field, ForeColor = Fg, FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9.5f),
            };
            var picks = new List<SaveEntry?> { null };
            combo.Items.Add("The archive itself (main version)");
            foreach (var e in entries) { picks.Add(e); combo.Items.Add(e.DisplayName); }

            // 1. le nom du fichier importe, qui designe la ROM par la regle qui l'a nomme
            string stem = Path.GetFileNameWithoutExtension(importedFile);
            int def = picks.FindIndex(e => e != null && string.Equals(
                Path.GetFileNameWithoutExtension(e.FileName), stem, StringComparison.OrdinalIgnoreCase));
            // 2. sinon, l'entree qu'on est en train de parcourir
            if (def < 0 && _entryFilter != null)
                def = picks.FindIndex(e => e != null && string.Equals(e.Key, _entryFilter, StringComparison.OrdinalIgnoreCase));
            combo.SelectedIndex = def >= 0 ? def : 0;

            var ok = _w.DlgBtn("OK", Color.FromArgb(60, 120, 70));
            var cancel = _w.DlgBtn("Cancel", Color.FromArgb(70, 70, 82));
            ok.DialogResult = DialogResult.OK; cancel.DialogResult = DialogResult.Cancel;
            ok.Location = new Point(S(300), S(100)); cancel.Location = new Point(S(400), S(100));
            f.Controls.Add(combo); f.Controls.Add(ok); f.Controls.Add(cancel);
            f.AcceptButton = ok; f.CancelButton = cancel;

            if (f.ShowDialog(FindForm()) != DialogResult.OK) return false;
            int i = combo.SelectedIndex;
            entry = i >= 0 && i < picks.Count ? picks[i] : null;
            return true;
        }

        /// <summary>"Which slot would you like to restore this save state to?" — LaunchBox's own wording,
        /// and like it we default to Auto rather than to the backup's own slot.</summary>
        private int? PromptSlot(int? backupSlot)
        {
            using var f = _w.NewDialog("Pick a slot", 420, 170);
            var lbl = new Label
            {
                AutoSize = true, ForeColor = Fg, BackColor = Bg, Location = new Point(S(16), S(14)),
                Font = new Font("Segoe UI", 9.5f), UseMnemonic = false,
                Text = "Which slot would you like to restore this save state to?",
            };
            var combo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(S(16), S(44)),
                Width = S(380), BackColor = Field, ForeColor = Fg, FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9.5f),
            };
            // Auto, then 0-9 — and the backup's own slot when it sits ABOVE that range. There is no
            // ceiling at 9: a .state10 is read back as slot 10 (save-algorithms.md claimed otherwise,
            // from a method that proposes restore targets rather than bounding the scan). Without this
            // last entry, a backup taken from slot 12 could not be restored to slot 12.
            var slots = new List<int> { -1 };                       // -1 = Auto
            for (int i = 0; i <= 9; i++) slots.Add(i);
            if (backupSlot is int bs && bs > 9) slots.Add(bs);
            foreach (var v in slots) combo.Items.Add(v < 0 ? "Auto" : "Slot " + v);
            combo.SelectedIndex = 0;                                // Auto by default, like LaunchBox

            var ok = _w.DlgBtn("OK", Color.FromArgb(60, 120, 70));
            var cancel = _w.DlgBtn("Cancel", Color.FromArgb(70, 70, 82));
            ok.DialogResult = DialogResult.OK; cancel.DialogResult = DialogResult.Cancel;
            ok.Location = new Point(S(200), S(84)); cancel.Location = new Point(S(300), S(84));

            f.Controls.Add(lbl); f.Controls.Add(combo); f.Controls.Add(ok); f.Controls.Add(cancel);
            f.AcceptButton = ok; f.CancelButton = cancel;
            if (f.ShowDialog(FindForm()) != DialogResult.OK) return null;
            // Read the value out of the list rather than deriving it from the index: with a slot above 9
            // appended, index-1 would name the wrong slot.
            int i2 = combo.SelectedIndex;
            return i2 >= 0 && i2 < slots.Count ? slots[i2] : -1;
        }

        private void SaveAction_History(SaveGroup g) => ShowBackupHistory(g);

        private void SaveAction_Combine(SaveGroup g, List<SaveGroup> others)
        {
            if (others.Count == 0)
            {
                MessageBox.Show(FindForm(), "There is no other save group of this type to combine with.",
                    "Combine With Another Save", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var dst = PromptCombine(g, others);
            if (dst == null) return;
            // No file is touched. Measured: Combine is pure re-labelling — the source's records take the
            // destination's SaveGroupId and MatchLineageId, every record of the result takes the SOURCE's
            // name, and nothing moves on disk. This text used to promise an archive-then-delete that our
            // first implementation really did perform, and that we removed once we measured theirs.
            const string extra = "\n\nNo file is moved or deleted — only the grouping changes.";
            if (MessageBox.Show(FindForm(),
                    $"Merge \"{g.GroupName}\" into \"{dst.GroupName}\"?\nBoth histories become one save group.{extra}",
                    "Combine With Another Save", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            string? err = SaveManager.Combine(g, dst);
            if (err != null) { SavesError(err); return; }
            Reload();
        }

        private void SaveAction_MakeNew(SaveGroup g)
        {
            // A name, like LaunchBox asks for, and nothing else. It used to warn that the live save was
            // about to be deleted — which it was, and which is no longer what this does (§4.1bis).
            var name = PromptText("New Save Group", "Enter a name for the new save group.",
                                  SaveManager.DefaultGroupName(g.IsState, g.GroupId));
            if (name == null) return;

            string? err = SaveManager.MakeNewSave(g, name);
            if (err != null) { SavesError(err); return; }
            Reload();
        }

        private void SaveAction_OpenFolder(SaveGroup g)
        {
            string p = g.ActivePath;
            if ((p.Length == 0 || (!File.Exists(p) && !Directory.Exists(p))) && g.Backups.Count > 0)
                p = SaveVault.Abs(g.Backups[0]);
            if (p.Length > 0) OpenIn(p);
        }

        private void SaveAction_Delete(SaveGroup g)
        {
            bool alsoBackups = false;
            if (g.Backups.Count > 0)
            {
                var res = ConfirmDelete(g, out alsoBackups);
                if (!res) return;
            }
            else if (MessageBox.Show(FindForm(),
                         $"Delete \"{g.GroupName}\"?\n\nThis permanently deletes the underlying save file(s) on disk — not just the entry.",
                         "Delete Save", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            string? err = SaveManager.Delete(g, alsoBackups);
            if (err != null) { SavesError(err); return; }
            Reload();
        }

        private void SaveAction_Import(bool asState)
        {
            var scan = _scan;
            if (scan?.Plugin == null || _game == null) return;
            using var dlg = new OpenFileDialog
            {
                Title = asState ? "Import Save State File" : "Import Save Game File",
                Filter = "All files (*.*)|*.*",
                CheckFileExists = true,
            };
            if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;

            int? slot = null;
            if (asState)
            {
                slot = PromptSlot(scan.Plugin);
                if (slot == null) return;
            }
            // Nothing is overwritten: the import lands in the vault as a new group, so the live save is
            // untouched and there is nothing to protect first.
            //
            // The group inherits the AdditionalApplicationId this game's saves are already attributed to —
            // the focused version in a version view, otherwise whatever the existing groups carry, which
            // is the version covering the game's own ROM whenever one exists.
            string? appId = null;
            try { appId = _focus?.Id; } catch { }
            if (string.IsNullOrEmpty(appId))
                appId = (asState ? scan.States : scan.Files).FirstOrDefault(x => x.AppId != null)?.AppId
                        ?? scan.Files.Concat(scan.States).FirstOrDefault(x => x.AppId != null)?.AppId;

            // Which ROM does this save belong to? Only asked when the game IS an archive we can read.
            SaveEntry? entry = null;
            if (!PromptImportEntry(dlg.FileName, out entry)) return;

            string? err = SaveManager.Import(_game, dlg.FileName, asState, slot, appId, entry: entry);
            if (err != null) { SavesError(err); return; }
            Reload();
        }

        // ── Dialogs ───────────────────────────────────────────────────────────

        private string? PromptText(string title, string label, string initial)
        {
            using var f = _w.NewDialog(title, 460, 170);
            f.Controls.Add(new Label { Text = label, AutoSize = true, Location = new Point(S(16), S(16)), ForeColor = Fg });
            var tb = new TextBox
            {
                Location = new Point(S(16), S(42)), Width = S(410), Text = initial,
                BackColor = Field, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle,
            };
            f.Controls.Add(tb);
            var ok = _w.DlgBtn("OK", Color.FromArgb(50, 110, 65)); ok.Location = new Point(S(16), S(84)); ok.DialogResult = DialogResult.OK;
            var cancel = _w.DlgBtn("Cancel", Color.FromArgb(70, 70, 82)); cancel.Location = new Point(S(96), S(84)); cancel.DialogResult = DialogResult.Cancel;
            f.Controls.Add(ok); f.Controls.Add(cancel);
            f.AcceptButton = ok; f.CancelButton = cancel;
            tb.SelectAll();
            return f.ShowDialog(FindForm()) == DialogResult.OK ? tb.Text : null;
        }

        /// <summary>Two labelled text boxes in one dialog — LaunchBox's "Edit Names" shape.</summary>
        private bool PromptTwo(string title, string label1, string initial1, string label2, string initial2,
                               out string value1, out string value2)
        {
            value1 = initial1; value2 = initial2;
            using var f = _w.NewDialog(title, 460, 236);
            TextBox Box(int y, string init)
            {
                var t = new TextBox
                {
                    Location = new Point(S(16), y), Width = S(410), Text = init,
                    BackColor = Field, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle,
                };
                f.Controls.Add(t);
                return t;
            }
            f.Controls.Add(new Label { Text = label1, AutoSize = true, Location = new Point(S(16), S(16)), ForeColor = Fg });
            var t1 = Box(S(42), initial1);
            f.Controls.Add(new Label { Text = label2, AutoSize = true, Location = new Point(S(16), S(84)), ForeColor = Fg });
            var t2 = Box(S(110), initial2);
            var ok = _w.DlgBtn("OK", Color.FromArgb(50, 110, 65)); ok.Location = new Point(S(16), S(152)); ok.DialogResult = DialogResult.OK;
            var cancel = _w.DlgBtn("Cancel", Color.FromArgb(70, 70, 82)); cancel.Location = new Point(S(96), S(152)); cancel.DialogResult = DialogResult.Cancel;
            f.Controls.Add(ok); f.Controls.Add(cancel);
            f.AcceptButton = ok; f.CancelButton = cancel;
            t1.SelectAll();
            if (f.ShowDialog(FindForm()) != DialogResult.OK) return false;
            value1 = t1.Text; value2 = t2.Text;
            return true;
        }

        private SaveGroup? PromptCombine(SaveGroup src, List<SaveGroup> others)
        {
            using var f = _w.NewDialog("Combine With Another Save", 500, 190);
            f.Controls.Add(new Label
            {
                // The merged group keeps the SOURCE's name, so "into" would read backwards.
                Text = "Choose the save file you want to combine with.", AutoSize = true,
                Location = new Point(S(16), S(16)), ForeColor = Fg,
            });
            var combo = new ComboBox
            {
                Location = new Point(S(16), S(42)), Width = S(450), DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Field, ForeColor = Fg, FlatStyle = FlatStyle.Flat,
            };
            foreach (var o in others)
            {
                string tag = o.Active != null ? "active" : "vault-only";
                combo.Items.Add($"{o.GroupName}  —  {o.Backups.Count} backup(s), {tag}");
            }
            combo.SelectedIndex = 0;
            f.Controls.Add(combo);
            var ok = _w.DlgBtn("Combine", Color.FromArgb(50, 110, 65)); ok.Location = new Point(S(16), S(96)); ok.DialogResult = DialogResult.OK;
            var cancel = _w.DlgBtn("Cancel", Color.FromArgb(70, 70, 82)); cancel.Location = new Point(S(116), S(96)); cancel.DialogResult = DialogResult.Cancel;
            f.Controls.Add(ok); f.Controls.Add(cancel);
            f.AcceptButton = ok; f.CancelButton = cancel;
            return f.ShowDialog(FindForm()) == DialogResult.OK && combo.SelectedIndex >= 0 ? others[combo.SelectedIndex] : null;
        }

        private int? PromptSlot(EmulatorPlugin plugin)
        {
            Dictionary<int, string> slots = new();
            try { lock (SaveManager.PluginGate) foreach (var kv in plugin.GetPotentialSaveSlots() ?? new Dictionary<int, string>()) slots[kv.Key] = kv.Value; } catch { }
            if (slots.Count == 0) return 0;   // emulator without slot notion → slot 0

            using var f = _w.NewDialog("Import Save State", 380, 170);
            f.Controls.Add(new Label { Text = "Import into slot:", AutoSize = true, Location = new Point(S(16), S(16)), ForeColor = Fg });
            var combo = new ComboBox
            {
                Location = new Point(S(16), S(42)), Width = S(330), DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Field, ForeColor = Fg, FlatStyle = FlatStyle.Flat,
            };
            var keys = slots.Keys.OrderBy(k => k).ToList();
            foreach (int k in keys) combo.Items.Add(slots[k]);
            combo.SelectedIndex = keys.Count > 1 && keys[0] < 0 ? 1 : 0;   // default to slot 0, not "Auto"
            f.Controls.Add(combo);
            var ok = _w.DlgBtn("Import", Color.FromArgb(50, 110, 65)); ok.Location = new Point(S(16), S(84)); ok.DialogResult = DialogResult.OK;
            var cancel = _w.DlgBtn("Cancel", Color.FromArgb(70, 70, 82)); cancel.Location = new Point(S(106), S(84)); cancel.DialogResult = DialogResult.Cancel;
            f.Controls.Add(ok); f.Controls.Add(cancel);
            f.AcceptButton = ok; f.CancelButton = cancel;
            return f.ShowDialog(FindForm()) == DialogResult.OK && combo.SelectedIndex >= 0 ? keys[combo.SelectedIndex] : (int?)null;
        }

        private bool ConfirmDelete(SaveGroup g, out bool alsoBackups)
        {
            alsoBackups = false;
            using var f = _w.NewDialog("Delete Save", 520, 210);
            // Spell out WHICH file goes. For an In Vault group the card's own file is a vault copy, and
            // the checkbox below counts zero — saying only "its N vault backups" made the dialog look
            // harmless while it was about to delete the group's only copy.
            string what = g.InVault
                ? "This permanently deletes this group's vault copy on disk — not just the entry."
                : "This permanently deletes the underlying save file(s) on disk — not just the entry.";
            f.Controls.Add(new Label
            {
                Text = $"Delete \"{g.GroupName}\"?\n\n{what}",
                AutoSize = false, Location = new Point(S(16), S(14)), Size = new Size(S(475), S(66)), ForeColor = Fg,
            });
            var cb = new CheckBox
            {
                Text = $"Also delete its {g.Backups.Count} vault backup(s)", AutoSize = true,
                Location = new Point(S(18), S(92)), ForeColor = Fg, Checked = false,
            };
            f.Controls.Add(cb);
            var ok = _w.DlgBtn("Delete", Color.FromArgb(150, 55, 50)); ok.Location = new Point(S(16), S(128)); ok.DialogResult = DialogResult.OK;
            var cancel = _w.DlgBtn("Cancel", Color.FromArgb(70, 70, 82)); cancel.Location = new Point(S(106), S(128)); cancel.DialogResult = DialogResult.Cancel;
            f.Controls.Add(ok); f.Controls.Add(cancel);
            f.AcceptButton = cancel; f.CancelButton = cancel;
            bool res = f.ShowDialog(FindForm()) == DialogResult.OK;
            alsoBackups = cb.Checked;
            return res;
        }

        // ── Backup History dialog (LB parity: header + one card per version) ─────
        private void ShowBackupHistory(SaveGroup g)
        {
            using var f = _w.NewDialog($"Backup History — {g.GroupName}", 720, 470);
            f.FormBorderStyle = FormBorderStyle.Sizable;
            f.MinimumSize = new Size(S(560), S(320));

            // The live save's line. LaunchBox puts the same three facts in its own header — date, hash,
            // size — and the hash is what tells two copies of the same day apart, so we show it too.
            string Summary()
            {
                string n = g.Backups.Count == 1 ? "1 backup" : $"{g.Backups.Count} backups";
                string when = g.LastModified?.ToString("G") ?? "—";
                // Same hash the rows below print, same eight characters, same casing — the point is to
                // compare them at a glance, so a different format would defeat it. Computed on demand
                // like the rows: one file, in a dialog the user opened.
                string h = "";
                try
                {
                    if (g.ActivePath.Length > 0)
                    {
                        var raw = g.ActiveIsDirectory ? SaveManager.DirManifestMd5(g.ActivePath)
                                                      : SaveManager.FileMd5(g.ActivePath);
                        if (raw.Length >= 8) h = $"   ·   # {raw.Substring(0, 8).ToUpperInvariant()}";
                    }
                }
                catch { }
                return g.Active != null
                    ? $"Active: {when}{h}   ·   {FmtSize(g.SizeBytes)}   ·   {n} in the vault"
                    : $"In vault, no live active save   ·   {when}{h}   ·   {FmtSize(g.SizeBytes)}   ·   {n}";
            }

            var header = new Panel { Dock = DockStyle.Top, Height = S(58), BackColor = Bg };
            var hTitle = new Label { AutoSize = true, ForeColor = Fg, BackColor = Bg, Location = new Point(S(16), S(10)), Font = new Font("Segoe UI", 13f, FontStyle.Bold), Text = g.GroupName, UseMnemonic = false };
            var hSub = new Label { AutoSize = true, ForeColor = SubFg, BackColor = Bg, Location = new Point(S(16), S(36)), Font = new Font("Segoe UI", 9f), UseMnemonic = false, Text = Summary() };
            header.Controls.Add(hTitle); header.Controls.Add(hSub);

            var list = new Panel { Dock = DockStyle.Fill, BackColor = Bg, AutoScroll = true, Padding = new Padding(S(10), S(4), S(10), S(8)) };

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = S(46), BackColor = PanelC };
            var close = _w.DlgBtn("Close", Color.FromArgb(70, 70, 82));
            close.DialogResult = DialogResult.Cancel;
            bottom.Controls.Add(close);
            bottom.Resize += (_, _) => close.Location = new Point(bottom.ClientSize.Width - close.Width - S(10), S(8));
            close.Location = new Point(bottom.ClientSize.Width - close.Width - S(10), S(8));

            void Rebuild()
            {
                list.SuspendLayout();
                list.Controls.Clear();
                // Vault copies only, newest first. The live save is NOT a row: it is described in the
                // header instead.
                //
                // It used to be listed on top as context, which put two rows under a card that said
                // "1 Backup" — the very inconsistency this pass set out to remove. One rule now holds on
                // all three surfaces: the card's number, the header's number and the row count are the
                // same thing, the copies you can restore.
                //
                // It also matches what LaunchBox shows for a game-attributed group: only the vault entry.
                // (For a version-attributed one it does list the live save, which is the same quirk that
                // inflates its count by one — see SaveGroup.DisplayBackupCount for why we do not follow.)
                var cards = new List<Control>();
                foreach (var e in g.Backups.OrderByDescending(x => x.DisplayCreatedUtc)) cards.Add(BuildVersionCard(g, e, f, Rebuild));
                if (cards.Count == 0)
                    list.Controls.Add(new Label { Dock = DockStyle.Top, Height = S(40), Text = "No versions.", ForeColor = SubFg, BackColor = Bg, Font = new Font("Segoe UI", 9.5f, FontStyle.Italic), TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(S(6), S(0), S(0), S(0)) });
                // Newest at the top. For docked children WinForms lays out from the BACK of the
                // z-order, so the card that must sit highest is the one sent to back — the old
                // loop brought each to FRONT, which put the newest at the bottom and the whole
                // list upside down despite the descending sort above.
                else foreach (var c in cards) { list.Controls.Add(c); c.Dock = DockStyle.Top; c.SendToBack(); }
                list.ResumeLayout();
                hSub.Text = Summary();
            }
            Rebuild();

            f.Controls.Add(list);
            f.Controls.Add(header);
            f.Controls.Add(bottom);
            list.BringToFront();
            f.CancelButton = close;
            f.ShowDialog(FindForm());
            Reload();   // labels/deletions/restores may have changed the cards
        }

        /// <summary>One version card inside Backup History: the live active save (entry == null) or a vault
        /// backup. Same look as the main page's cards, with a per-version ⋯ menu.</summary>
        private Control BuildVersionCard(SaveGroup g, VaultEntry? entry, Form owner, Action refresh)
        {
            bool isActive = entry == null;
            string abs = isActive ? g.ActivePath : SaveVault.Abs(entry!);

            // The hash, computed here rather than during the scan. FromRecords never filled Md5, so the
            // line that was meant to print it never had anything to print — and the hash is the ONLY way
            // to tell two copies of the same date apart. The whole measurement campaign leant on exactly
            // that. Doing it on demand keeps the page load free of I/O: this dialog holds a handful of
            // rows, and LaunchBox recomputes it too, having nowhere to persist it either.
            string md5 = "";
            try
            {
                if (abs.Length > 0)
                    md5 = (entry?.IsDirectory ?? Directory.Exists(abs))
                        ? SaveManager.DirManifestMd5(abs) : SaveManager.FileMd5(abs);
            }
            catch { }
            // DisplayCreatedUtc, pas CreatedUtc : une copie verrouillee porte une date de creation
            // avancee d'un siecle, et l'afficher telle quelle mettrait 2126 sur la ligne.
            DateTime? when = isActive ? g.LastModified : entry!.DisplayCreatedUtc.ToLocalTime();
            long? size = isActive ? g.SizeBytes : entry!.SizeBytes;
            string title = isActive
                ? (g.ActivePath.Length > 0 ? Path.GetFileName(g.ActivePath) : g.GroupName)
                // LaunchBox heads an archived entry with its record's Title, verbatim — not with the file
                // name. Falls back to the file name for a record that carries none.
                : (entry!.Title.Length > 0 ? entry.Title : Path.GetFileName(abs.TrimEnd('\\', '/')));

            // Three lines now (title, path, facts) instead of two.
            var card = new Panel { Height = S(88), BackColor = PanelC, Padding = new Padding(S(12), S(8), S(10), S(8)) };
            card.Paint += (_, e) => { using var pen = new Pen(Color.FromArgb(58, 58, 70)); e.Graphics.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1); };
            var wrap = new Panel { Height = card.Height + S(8), BackColor = Bg, Padding = new Padding(S(0), S(0), S(0), S(8)) };
            wrap.Controls.Add(card); card.Dock = DockStyle.Fill;

            var name = new Label { AutoSize = true, ForeColor = Fg, BackColor = PanelC, Location = new Point(S(10), S(8)), Font = new Font("Segoe UI", 10.5f, FontStyle.Bold), Text = title, UseMnemonic = false };
            card.Controls.Add(name);

            var menuBtn = new Button
            {
                Text = "…", Size = new Size(S(32), S(24)), FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
                BackColor = Field, ForeColor = Fg, Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                FlatAppearance = { BorderColor = Color.FromArgb(70, 70, 84), BorderSize = 1 }, Enabled = !ReadOnlyMode,
            };
            card.Controls.Add(menuBtn);

            var pill = new Label
            {
                AutoSize = true, BackColor = PanelC, Padding = new Padding(S(8), S(2), S(8), S(2)), Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                ForeColor = isActive ? Color.FromArgb(120, 220, 130)
                          : (entry!.Locked ? Color.FromArgb(226, 186, 96) : Color.FromArgb(150, 180, 235)),
                Text = isActive ? "★ Active" : (entry!.Locked ? "🔒 Locked" : "Vault"),
            };
            Color pillBorder = isActive ? Color.FromArgb(80, 160, 95)
                             : (entry!.Locked ? Color.FromArgb(170, 135, 60) : Color.FromArgb(90, 120, 175));
            pill.Paint += (_, e) => { using var pen = new Pen(pillBorder); e.Graphics.DrawRectangle(pen, 0, 0, pill.Width - 1, pill.Height - 1); };
            card.Controls.Add(pill);

            // The four facts LaunchBox prints on a row, in its order: where the file is, then when, its
            // hash, the emulator that wrote it, and its size. Ours carried only the date and the size,
            // which is not enough to tell two copies apart or to know which emulator a copy belongs to.
            var pathLbl = new Label
            {
                AutoSize = false, ForeColor = SubFg, BackColor = PanelC, Height = S(17),
                Font = new Font("Segoe UI", 8.5f), AutoEllipsis = true, UseMnemonic = false,
                Text = abs.Length > 0 ? DisplaySavePath(abs) : "(no file)",
            };
            _tips.SetToolTip(pathLbl, abs);
            card.Controls.Add(pathLbl);

            string emu = g.EmulatorFileName + (g.EmulatorCore.Length > 0 ? $" ({g.EmulatorCore})" : "");
            var info = new Label
            {
                AutoSize = false, ForeColor = SubFg, BackColor = PanelC, Height = S(18), Font = new Font("Segoe UI", 9f),
                AutoEllipsis = true, UseMnemonic = false,
                Text = $"🗓 {when?.ToString("G") ?? "—"}"
                     + (md5.Length >= 8 ? $"      # {md5.Substring(0, 8).ToUpperInvariant()}" : "")
                     + (emu.Trim().Length > 0 ? $"      🕹 {emu}" : "")
                     + $"      💾 {FmtSize(size)}",
            };
            _tips.SetToolTip(info, abs);
            card.Controls.Add(info);

            menuBtn.Click += (_, _) =>
            {
                var m = new ContextMenuStrip();
                void Add(string text, bool en, Action act) { var it = new ToolStripMenuItem(text) { Enabled = en }; it.Click += (_, _) => { try { act(); } catch (Exception ex) { SavesError(ex.Message); } }; m.Items.Add(it); }
                if (isActive)
                {
                    Add("Backup Save", true, () => { var r = SaveManager.Backup(g, force: true); if (r.Error != null) SavesError(r.Error); else refresh(); });
                    Add("Open Folder", abs.Length > 0, () => OpenIn(abs));
                }
                else
                {
                    Add("Set as Active", true, () => { SaveAction_SetActive(g, entry!, _scan!); owner.DialogResult = DialogResult.OK; owner.Close(); });
                    Add(entry!.Locked ? "Unlock" : "Lock (never delete)", true, () =>
                    {
                        bool target = !entry.Locked;
                        var err = SaveVault.SetLocked(SaveVault.Abs(entry), target);
                        if (err != null) { SavesError(err); return; }
                        // Mettre l'entree en memoire au diapason du disque. refresh() ne fait que
                        // redessiner a partir de ces objets-la : sans ca le cadenas n'apparaissait
                        // qu'apres fermeture et reouverture du dialogue, ce qui laissait croire que
                        // l'action avait echoue alors qu'elle avait reussi.
                        entry.CreatedUtc = entry.CreatedUtc.AddYears(target ? 100 : -100);
                        entry.Locked = target;
                        refresh();
                    });
                    Add("Edit Label…", true, () =>
                    {
                        string? l = PromptText("Edit Label", "Label for this backup:", entry!.Title);
                        if (l == null) return;
                        var err = SaveManager.SetBackupLabel(g, entry, l.Trim());
                        if (err != null) { SavesError(err); return; }
                        refresh();
                    });
                    Add("Open Folder", true, () => OpenIn(abs));
                    m.Items.Add(new ToolStripSeparator());
                    var del = new ToolStripMenuItem("Delete") { ForeColor = Color.FromArgb(230, 120, 110) };
                    del.Click += (_, _) =>
                    {
                        if (MessageBox.Show(owner, "Delete this backup from the vault?\nThe backup file is removed from disk.", "Delete Backup", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                        string? err = SaveManager.DeleteBackup(entry!, g.Game);
                        if (err != null) { SavesError(err); return; }
                        g.Backups.Remove(entry!); refresh();
                    };
                    m.Items.Add(del);
                }
                m.Show(menuBtn, new Point(0, menuBtn.Height));
            };

            void Layout()
            {
                int right = card.ClientSize.Width - S(10);
                menuBtn.Location = new Point(right - menuBtn.Width, S(8));
                pill.Location = new Point(menuBtn.Left - S(8) - pill.Width, S(9));
                pathLbl.SetBounds(S(12), S(36), card.ClientSize.Width - S(24), S(17));
                info.SetBounds(S(12), S(58), card.ClientSize.Width - S(24), S(18));
            }
            card.Resize += (_, _) => Layout();
            Layout();
            return wrap;
        }

        private void OpenIn(string path)
        {
            try
            {
                if (File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
                else if (Directory.Exists(path)) Process.Start("explorer.exe", $"\"{path}\"");
                else if (Directory.Exists(Path.GetDirectoryName(path) ?? "")) Process.Start("explorer.exe", $"\"{Path.GetDirectoryName(path)}\"");
            }
            catch (Exception ex) { SavesError(ex.Message); }
        }
    }
}
