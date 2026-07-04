// Edit Game → "Game Saves" page — LiteBox's replica of LaunchBox 13.27's save-management page
// (see ExtendDB/docs/lb-save-management.md for the full RE). Single-game only (multi shows a
// placeholder). The heavy lifting (plugin scan, <GameSave> records, vault) is Host\Saves\SaveManager;
// this file is pure UI: two sections (Save Files / Save States) of cards, each with LB's action menu
//   Edit Name / Backup History / Combine With Another Save… / Set as Active / Backup Save /
//   Make New Save / Open Folder / Delete Save
// plus the two bottom Import buttons. Every action rescans the page (cheap — one plugin GetSaves).

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
    private Panel? _savesPage;          // page root (cached in _pages["GameSaves"])
    private Panel? _savesContent;       // scrollable list area, rebuilt on every scan
    private Button? _savesImportFile, _savesImportState;
    private SaveScan? _savesScan;
    private int _savesScanSeq;          // guards against stale async scans (navigation)

    private IGame SavesGame => _editGames[0];

    // ── Page shell ────────────────────────────────────────────────────────
    private Control BuildGameSavesPage()
    {
        _savesPage = new Panel { BackColor = Bg };

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = S(46), BackColor = Bg, Padding = new Padding(S(3)) };
        _savesImportFile = FooterBtn("Import Save Game File…", Color.FromArgb(60, 60, 72));
        _savesImportState = FooterBtn("Import Save State File…", Color.FromArgb(60, 60, 72));
        _savesImportFile.AutoSize = false;
        _savesImportState.AutoSize = false;
        _savesImportFile.Click += (_, _) => SaveAction_Import(asState: false);
        _savesImportState.Click += (_, _) => SaveAction_Import(asState: true);
        bottom.Controls.AddRange(new Control[] { _savesImportFile, _savesImportState });
        bottom.Resize += (_, _) =>
        {
            int w = (bottom.ClientSize.Width - S(18)) / 2;
            _savesImportFile.SetBounds(S(6), S(8), w, S(30));
            _savesImportState.SetBounds(S(12) + w, S(8), w, S(30));
        };

        _savesContent = new Panel { Dock = DockStyle.Fill, BackColor = Bg, AutoScroll = true, Padding = new Padding(S(10), S(6), S(10), S(6)) };
        _savesPage.Controls.Add(_savesContent);
        _savesPage.Controls.Add(bottom);
        _savesContent.BringToFront();

        ReloadGameSaves();
        return _savesPage;
    }

    private void ReloadGameSavesIfBuilt() { if (_savesPage != null && !IsMulti) ReloadGameSaves(); }

    private void ReloadGameSaves()
    {
        if (_savesContent == null) return;
        int seq = ++_savesScanSeq;
        _savesScan = null;
        SetSavesMessage("Scanning saves…", italic: true);
        SetImportEnabled(false);

        var game = SavesGame;
        Task.Run(() =>
        {
            SaveScan scan;
            try { scan = SaveManager.ScanBase(game); }
            catch (Exception ex) { scan = new SaveScan { Error = "Save scan failed:\n" + ex.Message }; }
            try
            {
                if (IsHandleCreated && !IsDisposed)
                    BeginInvoke(new Action(() => { if (seq == _savesScanSeq) { _savesScan = scan; RenderGameSaves(); } }));
            }
            catch { }
        });
    }

    private void SetImportEnabled(bool on)
    {
        bool ok = on && !_readOnly;
        if (_savesImportFile != null) _savesImportFile.Enabled = ok;
        if (_savesImportState != null) _savesImportState.Enabled = ok;
    }

    private void SetSavesMessage(string text, bool italic = false, Color? color = null)
    {
        if (_savesContent == null) return;
        _savesContent.SuspendLayout();
        _savesContent.Controls.Clear();
        _savesContent.Controls.Add(new Label
        {
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = color ?? SubFg, BackColor = Bg,
            Font = new Font("Segoe UI", 10f, italic ? FontStyle.Italic : FontStyle.Regular),
            Text = text,
        });
        _savesContent.ResumeLayout();
    }

    // ── Rendering ─────────────────────────────────────────────────────────
    private void RenderGameSaves()
    {
        if (_savesContent == null || _savesScan == null) return;
        var scan = _savesScan;
        if (scan.Error != null) { SetSavesMessage(scan.Error); SetImportEnabled(false); return; }
        SetImportEnabled(scan.Plugin != null);

        // LB parity: the "unsupported emulator" hint only shows when NOTHING was found at all —
        // saves are always searched through every integration plugin, whatever the game's emulator.
        if (scan.Files.Count == 0 && scan.States.Count == 0 && !scan.GameEmulatorSupported)
        {
            string t = scan.GameEmulatorTitle.Length > 0 ? $" ({scan.GameEmulatorTitle})" : "";
            SetSavesMessage("No saves found for this game.\n\n"
                + $"Its emulator{t} has no LaunchBox integration plugin; saves were still searched through the\n"
                + "supported emulators (RetroArch, Dolphin, PCSX2, …) but none matched this game's files.");
            return;
        }

        _savesContent.SuspendLayout();
        _savesContent.Controls.Clear();

        void Stack(Control c) { _savesContent!.Controls.Add(c); c.Dock = DockStyle.Top; c.BringToFront(); }
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

        Header("Save Files");
        if (scan.Files.Count == 0) Empty("No save files found.");
        else foreach (var g in scan.Files) Stack(BuildSaveCard(g));

        Header("Save States");
        if (scan.States.Count == 0) Empty("No save states found.");
        else foreach (var g in scan.States) Stack(BuildSaveCard(g));

        _savesContent.ResumeLayout();
    }

    private Control BuildSaveCard(SaveGroup g)
    {
        var card = new Panel { Height = S(96), BackColor = PanelC, Margin = new Padding(S(0)), Padding = new Padding(S(12), S(8), S(10), S(8)) };
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

        Label? chip = null;
        if (g.IsState)
        {
            chip = new Label
            {
                AutoSize = true, ForeColor = SubFg, BackColor = Field, Padding = new Padding(S(6), S(2), S(6), S(2)),
                Font = new Font("Segoe UI", 8.5f), Text = "Slot " + (g.Slot is -1 ? "Auto" : (g.Slot?.ToString() ?? "?")),
            };
            card.Controls.Add(chip);
        }

        var menuBtn = new Button
        {
            Text = "…", Size = new Size(S(34), S(26)), FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
            BackColor = Field, ForeColor = Fg, Font = new Font("Segoe UI", 10f, FontStyle.Bold),
            FlatAppearance = { BorderColor = Color.FromArgb(70, 70, 84), BorderSize = 1 },
            Enabled = !_readOnly,
        };
        menuBtn.Click += (_, _) => BuildSaveMenu(g).Show(menuBtn, new Point(0, menuBtn.Height));
        card.Controls.Add(menuBtn);

        Label? pill = null;
        if (g.Active != null && g.ActiveLive)
        {
            pill = new Label
            {
                AutoSize = true, ForeColor = Color.FromArgb(120, 220, 130), BackColor = PanelC,
                Padding = new Padding(S(8), S(3), S(8), S(3)), Font = new Font("Segoe UI", 9f, FontStyle.Bold), Text = "★ Active",
            };
            pill.Paint += (_, e) =>
            {
                using var pen = new Pen(Color.FromArgb(80, 160, 95));
                e.Graphics.DrawRectangle(pen, 0, 0, pill.Width - 1, pill.Height - 1);
            };
            card.Controls.Add(pill);
        }

        // Round status dot (LB parity): green ✓ = active save with an up-to-date backup; yellow ! =
        // no/stale backup; red ✕ = record whose file is gone. Absent for pure vault-only groups.
        StatusDot? dot = null;
        if (g.RecordOnly)
            dot = new StatusDot(StatusKind.Error, "The save file this record points to no longer exists on disk.", S(22));
        else if (g.Active != null)
            dot = g.NeedsBackup
                ? new StatusDot(StatusKind.Warn, "No up-to-date backup in the vault — use Backup Save to protect it.", S(22))
                : new StatusDot(StatusKind.Ok, "This save has an up-to-date backup in the vault.", S(22));
        if (dot != null) { dot.BackColor = PanelC; card.Controls.Add(dot); }

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
        string backups = g.Backups.Count == 1 ? "1 Backup" : $"{g.Backups.Count} Backups";
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
            path.SetBounds(S(12), S(40), card.ClientSize.Width - S(24), S(18));
            info.SetBounds(S(12), S(62), card.ClientSize.Width - S(24), S(18));
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
        var scan = _savesScan!;
        var m = new ContextMenuStrip();
        bool hasActive = g.Active != null;
        bool hasBackups = g.Backups.Count > 0;
        var others = (g.IsState ? scan.States : scan.Files).Where(x => !ReferenceEquals(x, g)).ToList();

        ToolStripMenuItem Add(string text, bool enabled, Action act)
        {
            var it = new ToolStripMenuItem(text) { Enabled = enabled };
            it.Click += (_, _) => { try { act(); } catch (Exception ex) { SavesError(ex.Message); } };
            m.Items.Add(it);
            return it;
        }

        // LB parity: History/Combine stay enabled even when empty; only "Set as Active" greys out
        // on the card that IS already active.
        Add("Edit Name", true, () => SaveAction_EditName(g));
        Add("Backup History", true, () => SaveAction_History(g));
        Add("Combine With Another Save…", true, () => SaveAction_Combine(g, others));
        Add("Set as Active", !hasActive && hasBackups, () => SaveAction_SetActive(g, g.Backups.First()));
        m.Items.Add(new ToolStripSeparator());
        Add("Backup Save", hasActive, () => SaveAction_Backup(g));
        Add("Make New Save", hasActive, () => SaveAction_MakeNew(g));
        m.Items.Add(new ToolStripSeparator());
        Add("Open Folder", g.ActivePath.Length > 0 || hasBackups, () => SaveAction_OpenFolder(g));
        var del = Add("Delete Save", true, () => SaveAction_Delete(g));
        del.ForeColor = Color.FromArgb(230, 120, 110);
        return m;
    }

    private void SavesError(string message)
        => MessageBox.Show(this, message, "Game Saves", MessageBoxButtons.OK, MessageBoxIcon.Warning);

    // ── Actions ───────────────────────────────────────────────────────────

    private void SaveAction_EditName(SaveGroup g)
    {
        string? name = PromptText("Edit Name", "Save group name:", g.GroupName);
        if (name == null || name.Trim().Length == 0 || name.Trim() == g.GroupName) return;
        SaveManager.Rename(g, name.Trim());
        ReloadGameSaves();
    }

    private void SaveAction_Backup(SaveGroup g)
    {
        var r = SaveManager.Backup(g, force: false);
        if (r.Identical)
        {
            if (MessageBox.Show(this, "The current save is identical to its latest backup.\nCreate another copy anyway?",
                    "Backup Save", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            r = SaveManager.Backup(g, force: true);
        }
        if (r.Error != null) { SavesError(r.Error); return; }
        ReloadGameSaves();
    }

    private void SaveAction_SetActive(SaveGroup g, VaultEntry e)
    {
        string? err = SaveManager.Restore(g, e,
            confirmOverwrite: () => MessageBox.Show(this,
                "A save file already exists at the emulator's location.\nOverwrite it with this backup?",
                "Set as Active", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes);
        if (err != null) { SavesError(err); return; }
        ReloadGameSaves();
    }

    private void SaveAction_History(SaveGroup g) => ShowBackupHistory(g);

    private void SaveAction_Combine(SaveGroup g, List<SaveGroup> others)
    {
        if (others.Count == 0)
        {
            MessageBox.Show(this, "There is no other save group of this type to combine with.",
                "Combine With Another Save", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var dst = PromptCombine(g, others);
        if (dst == null) return;
        string extra = g.Active != null
            ? "\n\nThe current active file of the source will first be archived into the destination's history, then removed from disk."
            : "";
        if (MessageBox.Show(this,
                $"Merge \"{g.GroupName}\" into \"{dst.GroupName}\"?\nBoth histories become one save group.{extra}",
                "Combine With Another Save", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        string? err = SaveManager.Combine(g, dst);
        if (err != null) { SavesError(err); return; }
        ReloadGameSaves();
    }

    private void SaveAction_MakeNew(SaveGroup g)
    {
        if (MessageBox.Show(this,
                "Make a new save?\n\nThe current save is archived into the vault, then the live file is removed so the "
                + "emulator starts a brand-new save on next launch. The old history stays available under Backup History.",
                "Make New Save", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        string? err = SaveManager.MakeNewSave(g);
        if (err != null) { SavesError(err); return; }
        ReloadGameSaves();
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
        else if (MessageBox.Show(this,
                     $"Delete \"{g.GroupName}\"?\n\nThis permanently deletes the underlying save file(s) on disk — not just the entry.",
                     "Delete Save", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        string? err = SaveManager.Delete(g, alsoBackups);
        if (err != null) { SavesError(err); return; }
        ReloadGameSaves();
    }

    private void SaveAction_Import(bool asState)
    {
        var scan = _savesScan;
        if (scan?.Plugin == null) return;
        using var dlg = new OpenFileDialog
        {
            Title = asState ? "Import Save State File" : "Import Save Game File",
            Filter = "All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        int? slot = null;
        if (asState)
        {
            slot = PromptSlot(scan.Plugin);
            if (slot == null) return;
        }
        string? err = SaveManager.Import(SavesGame, scan.Plugin, dlg.FileName, asState, slot,
            confirmOverwrite: () => MessageBox.Show(this,
                "A save already exists at the emulator's location.\nOverwrite it with the imported file?",
                "Import Save", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes);
        if (err != null) { SavesError(err); return; }
        ReloadGameSaves();
    }

    // ── Dialogs ───────────────────────────────────────────────────────────

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

    private string? PromptText(string title, string label, string initial)
    {
        using var f = NewDialog(title, 460, 170);
        f.Controls.Add(new Label { Text = label, AutoSize = true, Location = new Point(S(16), S(16)), ForeColor = Fg });
        var tb = new TextBox
        {
            Location = new Point(S(16), S(42)), Width = S(410), Text = initial,
            BackColor = Field, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle,
        };
        f.Controls.Add(tb);
        var ok = DlgBtn("OK", Color.FromArgb(50, 110, 65)); ok.Location = new Point(S(16), S(84)); ok.DialogResult = DialogResult.OK;
        var cancel = DlgBtn("Cancel", Color.FromArgb(70, 70, 82)); cancel.Location = new Point(S(96), S(84)); cancel.DialogResult = DialogResult.Cancel;
        f.Controls.Add(ok); f.Controls.Add(cancel);
        f.AcceptButton = ok; f.CancelButton = cancel;
        tb.SelectAll();
        return f.ShowDialog(this) == DialogResult.OK ? tb.Text : null;
    }

    private SaveGroup? PromptCombine(SaveGroup src, List<SaveGroup> others)
    {
        using var f = NewDialog("Combine With Another Save", 500, 190);
        f.Controls.Add(new Label
        {
            Text = $"Merge \"{src.GroupName}\" into:", AutoSize = true, Location = new Point(S(16), S(16)), ForeColor = Fg,
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
        var ok = DlgBtn("Combine", Color.FromArgb(50, 110, 65)); ok.Location = new Point(S(16), S(96)); ok.DialogResult = DialogResult.OK;
        var cancel = DlgBtn("Cancel", Color.FromArgb(70, 70, 82)); cancel.Location = new Point(S(116), S(96)); cancel.DialogResult = DialogResult.Cancel;
        f.Controls.Add(ok); f.Controls.Add(cancel);
        f.AcceptButton = ok; f.CancelButton = cancel;
        return f.ShowDialog(this) == DialogResult.OK && combo.SelectedIndex >= 0 ? others[combo.SelectedIndex] : null;
    }

    private int? PromptSlot(EmulatorPlugin plugin)
    {
        Dictionary<int, string> slots = new();
        try { foreach (var kv in plugin.GetPotentialSaveSlots() ?? new Dictionary<int, string>()) slots[kv.Key] = kv.Value; } catch { }
        if (slots.Count == 0) return 0;   // emulator without slot notion → slot 0

        using var f = NewDialog("Import Save State", 380, 170);
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
        var ok = DlgBtn("Import", Color.FromArgb(50, 110, 65)); ok.Location = new Point(S(16), S(84)); ok.DialogResult = DialogResult.OK;
        var cancel = DlgBtn("Cancel", Color.FromArgb(70, 70, 82)); cancel.Location = new Point(S(106), S(84)); cancel.DialogResult = DialogResult.Cancel;
        f.Controls.Add(ok); f.Controls.Add(cancel);
        f.AcceptButton = ok; f.CancelButton = cancel;
        return f.ShowDialog(this) == DialogResult.OK && combo.SelectedIndex >= 0 ? keys[combo.SelectedIndex] : (int?)null;
    }

    private bool ConfirmDelete(SaveGroup g, out bool alsoBackups)
    {
        alsoBackups = false;
        using var f = NewDialog("Delete Save", 520, 210);
        f.Controls.Add(new Label
        {
            Text = $"Delete \"{g.GroupName}\"?\n\nThis permanently deletes the underlying save file(s) on disk — not just the entry.",
            AutoSize = false, Location = new Point(S(16), S(14)), Size = new Size(S(475), S(66)), ForeColor = Fg,
        });
        var cb = new CheckBox
        {
            Text = $"Also delete its {g.Backups.Count} vault backup(s)", AutoSize = true,
            Location = new Point(S(18), S(92)), ForeColor = Fg, Checked = false,
        };
        f.Controls.Add(cb);
        var ok = DlgBtn("Delete", Color.FromArgb(150, 55, 50)); ok.Location = new Point(S(16), S(128)); ok.DialogResult = DialogResult.OK;
        var cancel = DlgBtn("Cancel", Color.FromArgb(70, 70, 82)); cancel.Location = new Point(S(106), S(128)); cancel.DialogResult = DialogResult.Cancel;
        f.Controls.Add(ok); f.Controls.Add(cancel);
        f.AcceptButton = cancel; f.CancelButton = cancel;
        bool res = f.ShowDialog(this) == DialogResult.OK;
        alsoBackups = cb.Checked;
        return res;
    }

    // ── Backup History dialog (LB parity: header + one card per version) ─────
    private void ShowBackupHistory(SaveGroup g)
    {
        using var f = NewDialog($"Backup History — {g.GroupName}", 720, 470);
        f.FormBorderStyle = FormBorderStyle.Sizable;
        f.MinimumSize = new Size(S(560), S(320));

        string Summary() => g.Active != null
            ? $"Active: {g.LastModified?.ToString("G") ?? "—"}   ·   {FmtSize(g.SizeBytes)}   ·   {g.Backups.Count} backup(s)"
            : $"{g.Backups.Count} backup(s) in the vault — no live active save";

        var header = new Panel { Dock = DockStyle.Top, Height = S(58), BackColor = Bg };
        var hTitle = new Label { AutoSize = true, ForeColor = Fg, BackColor = Bg, Location = new Point(S(16), S(10)), Font = new Font("Segoe UI", 13f, FontStyle.Bold), Text = g.GroupName, UseMnemonic = false };
        var hSub = new Label { AutoSize = true, ForeColor = SubFg, BackColor = Bg, Location = new Point(S(16), S(36)), Font = new Font("Segoe UI", 9f), UseMnemonic = false, Text = Summary() };
        header.Controls.Add(hTitle); header.Controls.Add(hSub);

        var list = new Panel { Dock = DockStyle.Fill, BackColor = Bg, AutoScroll = true, Padding = new Padding(S(10), S(4), S(10), S(8)) };

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = S(46), BackColor = PanelC };
        var close = DlgBtn("Close", Color.FromArgb(70, 70, 82));
        close.DialogResult = DialogResult.Cancel;
        bottom.Controls.Add(close);
        bottom.Resize += (_, _) => close.Location = new Point(bottom.ClientSize.Width - close.Width - S(10), S(8));
        close.Location = new Point(bottom.ClientSize.Width - close.Width - S(10), S(8));

        void Rebuild()
        {
            list.SuspendLayout();
            list.Controls.Clear();
            // The live active version (context, top) + every vault backup (newest first).
            var cards = new List<Control>();
            if (g.Active != null) cards.Add(BuildVersionCard(g, null, f, Rebuild));
            foreach (var e in g.Backups.OrderByDescending(x => x.CreatedUtc)) cards.Add(BuildVersionCard(g, e, f, Rebuild));
            if (cards.Count == 0)
                list.Controls.Add(new Label { Dock = DockStyle.Top, Height = S(40), Text = "No versions.", ForeColor = SubFg, BackColor = Bg, Font = new Font("Segoe UI", 9.5f, FontStyle.Italic), TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(S(6), S(0), S(0), S(0)) });
            else for (int i = cards.Count - 1; i >= 0; i--) { list.Controls.Add(cards[i]); cards[i].Dock = DockStyle.Top; cards[i].BringToFront(); }
            list.ResumeLayout();
            hSub.Text = Summary();
        }
        Rebuild();

        f.Controls.Add(list);
        f.Controls.Add(header);
        f.Controls.Add(bottom);
        list.BringToFront();
        f.CancelButton = close;
        f.ShowDialog(this);
        ReloadGameSaves();   // labels/deletions/restores may have changed the cards
    }

    /// <summary>One version card inside Backup History: the live active save (entry == null) or a vault
    /// backup. Same look as the main page's cards, with a per-version ⋯ menu.</summary>
    private Control BuildVersionCard(SaveGroup g, VaultEntry? entry, Form owner, Action refresh)
    {
        bool isActive = entry == null;
        string abs = isActive ? g.ActivePath : SaveVault.Abs(entry!);
        string md5 = isActive ? "" : entry!.Md5;
        DateTime? when = isActive ? g.LastModified : entry!.CreatedUtc.ToLocalTime();
        long? size = isActive ? g.SizeBytes : entry!.SizeBytes;
        string title = isActive
            ? (g.ActivePath.Length > 0 ? Path.GetFileName(g.ActivePath) : g.GroupName)
            : (entry!.Label.Length > 0 ? entry.Label : Path.GetFileName(abs.TrimEnd('\\', '/')));

        var card = new Panel { Height = S(72), BackColor = PanelC, Padding = new Padding(S(12), S(8), S(10), S(8)) };
        card.Paint += (_, e) => { using var pen = new Pen(Color.FromArgb(58, 58, 70)); e.Graphics.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1); };
        var wrap = new Panel { Height = card.Height + S(8), BackColor = Bg, Padding = new Padding(S(0), S(0), S(0), S(8)) };
        wrap.Controls.Add(card); card.Dock = DockStyle.Fill;

        var name = new Label { AutoSize = true, ForeColor = Fg, BackColor = PanelC, Location = new Point(S(10), S(8)), Font = new Font("Segoe UI", 10.5f, FontStyle.Bold), Text = title, UseMnemonic = false };
        card.Controls.Add(name);

        var menuBtn = new Button
        {
            Text = "…", Size = new Size(S(32), S(24)), FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
            BackColor = Field, ForeColor = Fg, Font = new Font("Segoe UI", 10f, FontStyle.Bold),
            FlatAppearance = { BorderColor = Color.FromArgb(70, 70, 84), BorderSize = 1 }, Enabled = !_readOnly,
        };
        card.Controls.Add(menuBtn);

        var pill = new Label
        {
            AutoSize = true, BackColor = PanelC, Padding = new Padding(S(8), S(2), S(8), S(2)), Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            ForeColor = isActive ? Color.FromArgb(120, 220, 130) : Color.FromArgb(150, 180, 235),
            Text = isActive ? "★ Active" : "Vault",
        };
        Color pillBorder = isActive ? Color.FromArgb(80, 160, 95) : Color.FromArgb(90, 120, 175);
        pill.Paint += (_, e) => { using var pen = new Pen(pillBorder); e.Graphics.DrawRectangle(pen, 0, 0, pill.Width - 1, pill.Height - 1); };
        card.Controls.Add(pill);

        var info = new Label
        {
            AutoSize = false, ForeColor = SubFg, BackColor = PanelC, Height = S(18), Font = new Font("Segoe UI", 9f),
            AutoEllipsis = true, UseMnemonic = false,
            Text = $"🗓 {when?.ToString("G") ?? "—"}      💾 {FmtSize(size)}" + (md5.Length >= 8 ? $"      🔑 {md5.Substring(0, 8).ToLowerInvariant()}" : ""),
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
                Add("Set as Active", true, () => { SaveAction_SetActive(g, entry!); owner.DialogResult = DialogResult.OK; owner.Close(); });
                Add("Edit Label…", true, () =>
                {
                    string? l = PromptText("Edit Label", "Label for this backup:", entry!.Label);
                    if (l == null) return; entry.Label = l.Trim(); SaveVault.Changed(); refresh();
                });
                Add("Open Folder", true, () => OpenIn(abs));
                m.Items.Add(new ToolStripSeparator());
                var del = new ToolStripMenuItem("Delete") { ForeColor = Color.FromArgb(230, 120, 110) };
                del.Click += (_, _) =>
                {
                    if (MessageBox.Show(owner, "Delete this backup from the vault?\nThe backup file is removed from disk.", "Delete Backup", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                    string? err = SaveManager.DeleteBackup(entry!);
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
            info.SetBounds(S(12), S(42), card.ClientSize.Width - S(24), S(18));
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
