// ─────────────────────────────────────────────────────────────────────────────
// ROM extractor (ArchiveMGS) — native advanced ROM picker.
// ─────────────────────────────────────────────────────────────────────────────
//
// Dark-themed modal that lists an archive's playable entries in SCORED order
// (RomExtractor.ListEntriesDetailed) with the cumulative ↻ (last-played)
// ★ (favourite) ✓ (already cached) 🏆 (RetroAchievements) markers, a
// substring/wildcard filter, and clickable column sort. Interactive bits from
// the plugin's ArchiveListWindow: clicking the ★ cell (or the right-click menu)
// TOGGLES the entry's favourite (persisted per archive short-signature), and
// "Extract to…" pulls one entry to a chosen folder via LB's bundled 7-Zip.
// Returns the chosen entry's in-archive path (the host arms + launches).

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace LbApiHost.Host.Rom;

internal sealed class RomPickerWindow : Form
{
    private readonly List<RomEntryView> _entries;   // scored display order
    private List<RomEntryView> _sorted;
    private readonly string _shortSig;              // favourites key ("" → toggle disabled)
    private readonly string _archivePath;           // Extract-to… source ("" → disabled)
    private readonly TextBox _txtFilter;
    private readonly ListView _list;
    private readonly Label _lblStatus;

    private int _sortCol = -1;
    private bool _sortAsc = true;

    /// <summary>The chosen entry's in-archive path (null = cancelled).</summary>
    public string? ChosenEntry { get; private set; }

    public RomPickerWindow(string gameTitle, RomEntryListing listing)
    {
        _entries = (listing?.Entries ?? new List<RomEntryView>()).ToList();
        _sorted = _entries.ToList();
        _shortSig = listing?.ShortSignature ?? "";
        _archivePath = listing?.ArchivePath ?? "";

        Text = "Select ROM — " + (gameTitle ?? "?");
        Size = new Size(1030, 600);
        MinimumSize = new Size(700, 400);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(25, 25, 35);
        ForeColor = Color.FromArgb(220, 220, 220);
        KeyPreview = true;
        FormBorderStyle = FormBorderStyle.Sizable;

        // ── Header (filter) ──────────────────────────────────────────────
        var header = new Panel { Dock = DockStyle.Top, Height = 34, BackColor = Color.FromArgb(25, 25, 35), Padding = new Padding(10, 4, 10, 4) };
        Controls.Add(header);
        var lblFilter = new Label
        {
            Text = "Filter (wildcards * ?):", Dock = DockStyle.Left, Width = 140,
            TextAlign = ContentAlignment.MiddleLeft, Font = new Font("Segoe UI", 9f),
        };
        _txtFilter = new TextBox
        {
            Dock = DockStyle.Fill, BackColor = Color.FromArgb(40, 40, 50), ForeColor = Color.FromArgb(220, 220, 220),
            BorderStyle = BorderStyle.FixedSingle, Font = new Font("Segoe UI", 9f),
        };
        _txtFilter.TextChanged += (s, e) => Rebind();
        header.Controls.Add(_txtFilter);   // Fill added before Left so it fills the remaining space
        header.Controls.Add(lblFilter);

        // ── Footer (status + buttons) ────────────────────────────────────
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 50, BackColor = Color.FromArgb(20, 20, 28) };
        Controls.Add(footer);
        _lblStatus = new Label
        {
            Text = "", Dock = DockStyle.Left, Width = 320, TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(10, 0, 0, 0), Font = new Font("Consolas", 8.5f), ForeColor = Color.FromArgb(140, 140, 150),
        };
        footer.Controls.Add(_lblStatus);
        var btnCancel = MakeFlatButton("Cancel", Color.FromArgb(60, 60, 75));
        btnCancel.Dock = DockStyle.Right; btnCancel.Width = 100;
        btnCancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
        footer.Controls.Add(btnCancel);
        var btnSelect = MakeFlatButton("✔  Select", Color.FromArgb(50, 110, 65));
        btnSelect.Dock = DockStyle.Right; btnSelect.Width = 160;
        btnSelect.Click += (s, e) => SelectCurrent();
        footer.Controls.Add(btnSelect);

        // ── List ─────────────────────────────────────────────────────────
        _list = new ListView
        {
            Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false,
            HeaderStyle = ColumnHeaderStyle.Clickable, GridLines = false,
            BackColor = Color.FromArgb(35, 35, 45), ForeColor = Color.FromArgb(220, 220, 220),
            Font = new Font("Segoe UI", 9.5f), HideSelection = false, UseCompatibleStateImageBehavior = false,
        };
        _list.Columns.Add("★", 30, HorizontalAlignment.Center);
        _list.Columns.Add("↻", 28, HorizontalAlignment.Center);
        _list.Columns.Add("✓", 28, HorizontalAlignment.Center);
        _list.Columns.Add("Title", 480, HorizontalAlignment.Left);
        _list.Columns.Add("Size", 90, HorizontalAlignment.Right);
        _list.Columns.Add("Type", 56, HorizontalAlignment.Left);
        _list.Columns.Add("Points", 60, HorizontalAlignment.Right);
        _list.Columns.Add("RetroAchievements", 240, HorizontalAlignment.Left);
        _list.DoubleClick += (s, e) => SelectCurrent();
        _list.SelectedIndexChanged += (s, e) => UpdateStatus();
        _list.ColumnClick += List_ColumnClick;
        _list.MouseClick += List_MouseClick;
        _list.ContextMenuStrip = BuildContextMenu();
        Controls.Add(_list);
        _list.BringToFront();

        KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
            else if (e.KeyCode == Keys.Enter && _list.SelectedItems.Count > 0) SelectCurrent();
        };

        Rebind();
        SelectInitial();
    }

    private static Button MakeFlatButton(string text, Color back) => new()
    {
        Text = text, FlatStyle = FlatStyle.Flat, BackColor = back, ForeColor = Color.FromArgb(230, 230, 230),
        FlatAppearance = { BorderSize = 0 }, Font = new Font("Segoe UI", 9f, FontStyle.Bold),
    };

    // ── Favourite toggle (★ cell click / context menu) ──────────────────────

    private void List_MouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        var hit = _list.HitTest(e.Location);
        if (hit.Item?.Tag is not RomEntryView entry) return;
        int sub = hit.Item.SubItems.IndexOf(hit.SubItem!);
        if (sub == 0) ToggleFavorite(entry);
    }

    private void ToggleFavorite(RomEntryView entry)
    {
        if (string.IsNullOrEmpty(_shortSig)) { _lblStatus.Text = "Favourites unavailable (no archive signature)."; return; }
        bool newVal = !entry.IsFavorite;
        try { ArchiveHistory.ToggleFavorite(_shortSig, entry.PathInArchive, newVal); }
        catch (Exception ex) { _lblStatus.Text = "Favourite toggle failed: " + ex.Message; return; }
        entry.IsFavorite = newVal;
        Rebind(keepSelection: entry);
    }

    private ContextMenuStrip BuildContextMenu()
    {
        var menu = new ContextMenuStrip();
        var fav = new ToolStripMenuItem("Set favorite");
        fav.Click += (s, e) => { if (Selected() is { } en) ToggleFavorite(en); };
        var extract = new ToolStripMenuItem("Extract to…");
        extract.Click += (s, e) => { if (Selected() is { } en) ExtractTo(en); };
        menu.Items.Add(fav);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(extract);
        menu.Opening += (s, e) =>
        {
            var en = Selected();
            fav.Enabled = en != null && _shortSig.Length > 0;
            fav.Text = en?.IsFavorite == true ? "Unset favorite" : "Set favorite";
            extract.Enabled = en != null && _archivePath.Length > 0 && File.Exists(_archivePath);
        };
        return menu;
    }

    private RomEntryView? Selected()
        => _list.SelectedItems.Count > 0 && _list.SelectedItems[0].Tag is RomEntryView en ? en : null;

    /// <summary>Pulls ONE entry (flattened) into a user-chosen folder via LB's bundled 7-Zip —
    /// the plugin picker's "Extract To…". Best-effort; reports in the status line.</summary>
    private void ExtractTo(RomEntryView entry)
    {
        try
        {
            using var dlg = new FolderBrowserDialog { Description = "Extract \"" + entry.FileName + "\" to…" };
            if (dlg.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(dlg.SelectedPath)) return;

            string exe = RomPaths.SevenZipExe;
            if (!File.Exists(exe)) { _lblStatus.Text = "7z.exe not found."; return; }

            var psi = new ProcessStartInfo
            {
                FileName = exe, UseShellExecute = false, CreateNoWindow = true,
                ArgumentList = { "e", _archivePath, entry.PathInArchive, "-o" + dlg.SelectedPath, "-y" },
            };
            _lblStatus.Text = "Extracting \"" + entry.FileName + "\"…";
            using var p = Process.Start(psi);
            if (p == null) { _lblStatus.Text = "7z failed to start."; return; }
            if (!p.WaitForExit(120_000)) { try { p.Kill(); } catch { } _lblStatus.Text = "Extraction timed out."; return; }
            _lblStatus.Text = p.ExitCode == 0
                ? "Extracted to " + dlg.SelectedPath
                : "7z exited with code " + p.ExitCode + ".";
        }
        catch (Exception ex) { _lblStatus.Text = "Extract failed: " + ex.Message; }
    }

    // ── Sort / filter / bind ────────────────────────────────────────────────

    private void List_ColumnClick(object? sender, ColumnClickEventArgs e)
    {
        if (e.Column == 2) return;   // ✓ cached: display-only, not sortable (plugin parity)
        if (e.Column == _sortCol) _sortAsc = !_sortAsc;
        else { _sortCol = e.Column; _sortAsc = true; }
        ApplyColumnSort();
        Rebind();
    }

    private void ApplyColumnSort()
    {
        Comparison<RomEntryView>? cmp = _sortCol switch
        {
            0 => (a, b) => (b.IsFavorite ? 1 : 0) - (a.IsFavorite ? 1 : 0),
            1 => (a, b) => (b.IsLastPlayed ? 1 : 0) - (a.IsLastPlayed ? 1 : 0),
            3 => (a, b) => string.Compare(a.FileName, b.FileName, StringComparison.OrdinalIgnoreCase),
            4 => (a, b) => a.Size.CompareTo(b.Size),
            5 => (a, b) => string.Compare(a.Extension, b.Extension, StringComparison.OrdinalIgnoreCase),
            6 => (a, b) => a.Score.CompareTo(b.Score),
            7 => (a, b) => string.Compare(a.RaTitle, b.RaTitle, StringComparison.OrdinalIgnoreCase),
            _ => null,
        };
        if (cmp == null) return;
        _sorted.Sort(cmp);
        if (!_sortAsc) _sorted.Reverse();
    }

    private static bool MatchesFilter(string name, string filter)
    {
        if (string.IsNullOrEmpty(filter)) return true;
        name ??= "";
        if (filter.IndexOf('*') >= 0 || filter.IndexOf('?') >= 0)
            return Wildcard.Match(name.ToLowerInvariant(), ("*" + filter + "*").ToLowerInvariant());
        return name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private void Rebind(RomEntryView? keepSelection = null)
    {
        _list.BeginUpdate();
        try
        {
            _list.Items.Clear();
            var filter = (_txtFilter.Text ?? "").Trim();
            int shown = 0;
            foreach (var f in _sorted)
            {
                if (!MatchesFilter(f.FileName, filter)) continue;
                var lvi = new ListViewItem(new[]
                {
                    f.IsFavorite ? "★" : "",
                    f.IsLastPlayed ? "↻" : "",
                    f.IsCached ? "✓" : "",
                    f.FileName,
                    FormatSize((ulong)f.Size),
                    f.Extension,
                    f.Score.ToString(),
                    string.IsNullOrEmpty(f.RaTitle) ? (f.HasRa ? "🏆" : "") : f.RaTitle,
                })
                { Tag = f, UseItemStyleForSubItems = false };
                _list.Items.Add(lvi);
                if (f.IsFavorite) lvi.SubItems[0].ForeColor = Color.Gold;
                if (f.IsLastPlayed) lvi.SubItems[1].ForeColor = Color.FromArgb(120, 190, 255);
                if (f.IsCached) lvi.SubItems[2].ForeColor = Color.FromArgb(110, 200, 120);
                if (ReferenceEquals(f, keepSelection)) lvi.Selected = true;
                shown++;
            }
            _lblStatus.Text = shown + " entries shown  /  " + _sorted.Count + " total";
        }
        finally { _list.EndUpdate(); }
    }

    private void SelectInitial()
    {
        if (_list.Items.Count == 0) return;
        _list.Items[0].Selected = true;
        _list.Items[0].EnsureVisible();
    }

    private void UpdateStatus()
    {
        var sel = _list.SelectedItems.Count > 0 && _list.SelectedItems[0].Tag is RomEntryView se ? se.FileName : "(none)";
        _lblStatus.Text = _list.Items.Count + " shown / " + _sorted.Count + " total — selected: " + sel;
    }

    private void SelectCurrent()
    {
        if (_list.SelectedItems.Count == 0) { _lblStatus.Text = "Select an entry first."; return; }
        if (_list.SelectedItems[0].Tag is not RomEntryView entry) return;
        ChosenEntry = entry.PathInArchive;   // selection-mode identity = in-archive path
        DialogResult = DialogResult.OK;
        Close();
    }

    private static string FormatSize(ulong bytes)
    {
        if (bytes < 1024) return bytes + " B";
        if (bytes < 1024L * 1024) return (bytes / 1024.0).ToString("0.#") + " KB";
        if (bytes < 1024L * 1024 * 1024) return (bytes / 1024.0 / 1024.0).ToString("0.#") + " MB";
        return (bytes / 1024.0 / 1024.0 / 1024.0).ToString("0.##") + " GB";
    }
}
