// Edit Platform window — per-platform editor modelled on LaunchBox's "Edit Platform" dialog, built on the
// same OptionsWindow shell + catalog-setter pattern as EditEmulatorWindow. Every field writes through the
// HostPlatform SETTERS (SDK props + ILiteBoxFields for off-SDK fields) → the GameStore op-log → persisted to
// Data\Platforms.xml on FlushIfSafe (no-op while LaunchBox is running). Read-only mode disables inputs.
//
// Tabs (LB parity): Details · Folders · Notes · Documents · Parents · 3D Model Settings.
// Incremental build — Details + Notes are wired first; the remaining tabs are added next.

#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using LbApiHost.Host.Data;
using LbApiHost.Host.Media;
using LbApiHost.Host.Options;
using LbApiHost.Host.UiKit;
using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Platforms;

internal static class EditPlatformWindow
{
    private static readonly Color Bg = LiteBoxTheme.Bg;
    private static readonly Color Panel2 = LiteBoxTheme.Panel2;
    private static readonly Color Fg = LiteBoxTheme.Fg;
    private static readonly Color SubFg = LiteBoxTheme.SubFg;

    public static void Open(IPlatform plat, bool readOnly, IWin32Window? owner, string lbRoot)
    {
        if (plat == null) return;
        string name = Safe(() => plat.Name) ?? "Platform";
        // Details and Notes ride the op-log, so they stay editable while LaunchBox runs — the edits are
        // recorded and applied when it closes. The other four write their XML DIRECTLY, so they would be
        // refused at OK: they open locked instead, rather than letting the work be typed and thrown away.
        bool locked = WriteGuard.DirectWriteLocked(readOnly);
        string mark = WriteGuard.TabLockMark(readOnly);
        using var w = new OptionsWindow($"Edit Platform — {name}{WriteGuard.TitleMark(readOnly)}");
        float s = LiteBoxTheme.DpiScale(w);

        // Details tab = fields (left) + the Images panel (right), LB-style. The Images panel belongs to Details
        // only (the 3D Model Settings tab has its own preview; other tabs are full-width).
        var (details, applyDetails) = BuildDetails(plat, readOnly, lbRoot, s);
        w.AddSection("Details", details, applyDetails);

        var (folders, applyFolders) = EditPlatformFolders.Build(plat, locked, s);
        w.AddSection("Folders" + mark, folders, applyFolders);

        var (notes, applyNotes) = BuildNotes(plat, s);
        w.AddSection("Notes", notes, applyNotes);

        var (docs, applyDocs) = EditPlatformDocuments.Build(plat, locked, s);
        w.AddSection("Documents" + mark, docs, applyDocs);

        var (parents, applyParents) = EditPlatformParents.Build(plat, locked, s);
        w.AddSection("Parents" + mark, parents, applyParents);

        var (model, applyModel) = EditPlatformModel.Build(plat, locked, s);
        w.AddSection("3D Model Settings" + mark, model, applyModel);

        if (readOnly) DisableAllInputs(w);
        w.ShowDialog(owner);

        // Persist the journalled field writes now (no-op if LaunchBox is running).
        if (!readOnly) { try { (PluginHelper.DataManager as HostDataManagerXml)?.FlushIfSafe(); } catch { } }
    }

    /// <summary>Build every section as (title, control) WITHOUT the window shell — for the offscreen render
    /// probe (--edit-platform-render). Apply actions are discarded.</summary>
    internal static System.Collections.Generic.List<(string title, Control ctrl)> BuildSectionsForRender(IPlatform plat, string lbRoot, float s)
    {
        var list = new System.Collections.Generic.List<(string, Control)>();
        try { var (d, _) = BuildDetails(plat, false, lbRoot, s); list.Add(("Details", d)); } catch (Exception ex) { Console.WriteLine("[render] Details: " + ex.Message); }
        try { var (fo, _) = EditPlatformFolders.Build(plat, false, s); list.Add(("Folders", fo)); } catch (Exception ex) { Console.WriteLine("[render] Folders: " + ex.Message); }
        try { var (n, _) = BuildNotes(plat, s); list.Add(("Notes", n)); } catch (Exception ex) { Console.WriteLine("[render] Notes: " + ex.Message); }
        try { var (doc, _) = EditPlatformDocuments.Build(plat, false, s); list.Add(("Documents", doc)); } catch (Exception ex) { Console.WriteLine("[render] Documents: " + ex.Message); }
        try { var (pa, _) = EditPlatformParents.Build(plat, false, s); list.Add(("Parents", pa)); } catch (Exception ex) { Console.WriteLine("[render] Parents: " + ex.Message); }
        try { var (m, _) = EditPlatformModel.Build(plat, false, s); list.Add(("3D Model Settings", m)); } catch (Exception ex) { Console.WriteLine("[render] 3D: " + ex.Message); }
        return list;
    }

    // ── Details ─────────────────────────────────────────────────────────────────
    private static (Control panel, Action apply) BuildDetails(IPlatform plat, bool readOnly, string lbRoot, float s)
    {
        int S(int px) => (int)Math.Round(px * s);
        var fields = plat as ILiteBoxFields;

        // TableLayoutPanel so fields stretch to the (variable) content width — no fixed-pixel overflow / clipped
        // labels when the right-side Images panel narrows the content area.
        var tlp = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, AutoScroll = true, ColumnCount = 3, BackColor = Bg,
            Padding = new Padding(S(6), S(6), S(14), S(6)), GrowStyle = TableLayoutPanelGrowStyle.AddRows,
        };
        tlp.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(140)));
        tlp.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        tlp.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(98)));

        int row = 0;
        Label Lbl(string t) => new() { Text = t, ForeColor = SubFg, BackColor = Bg, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(S(4), S(8), S(4), 0) };
        TextBox Tb(string v) => new() { Text = v ?? "", BackColor = Panel2, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle, Dock = DockStyle.Fill, Margin = new Padding(0, S(3), 0, S(3)) };
        Button MiniBtn(string t, int w) => new() { Text = t, Size = new Size(S(w), S(23)), Anchor = AnchorStyles.Left, FlatStyle = FlatStyle.Flat, BackColor = Panel2, ForeColor = Fg, FlatAppearance = { BorderSize = 0 }, Font = new Font("Segoe UI", 8.5f), Margin = new Padding(S(4), S(4), 0, 0) };
        void AddRow(Control field, string label, Control? btn = null)
        {
            tlp.RowStyles.Add(new RowStyle(SizeType.Absolute, S(30)));
            tlp.Controls.Add(Lbl(label), 0, row);
            tlp.Controls.Add(field, 1, row);
            if (btn != null) tlp.Controls.Add(btn, 2, row); else tlp.SetColumnSpan(field, 2);
            row++;
        }
        CheckBox AddCheck(string text, bool chk)
        {
            var cb = new CheckBox { Text = text, AutoSize = true, ForeColor = Fg, BackColor = Bg, Checked = chk, Anchor = AnchorStyles.Left, Margin = new Padding(S(4), S(6), 0, S(2)) };
            tlp.RowStyles.Add(new RowStyle(SizeType.Absolute, S(28)));
            tlp.Controls.Add(cb, 0, row); tlp.SetColumnSpan(cb, 3); row++;
            return cb;
        }

        var name = Tb(Safe(() => plat.Name) ?? ""); name.ReadOnly = true; name.BackColor = Bg; AddRow(name, "Title:");
        var scrapeAs = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, FlatStyle = FlatStyle.Flat, BackColor = Panel2, ForeColor = Fg, Dock = DockStyle.Fill, Margin = new Padding(0, S(3), 0, S(3)) };
        scrapeAs.Items.Add(""); scrapeAs.Items.AddRange(ScrapeAsChoices()); scrapeAs.Text = Safe(() => plat.ScrapeAs) ?? "";
        { bool armed = false; scrapeAs.GotFocus += (_, _) => { if (armed) return; armed = true; scrapeAs.AutoCompleteMode = AutoCompleteMode.SuggestAppend; scrapeAs.AutoCompleteSource = AutoCompleteSource.ListItems; }; }
        AddRow(scrapeAs, "Scrape As:");
        var release = Tb(DateStr(Safe(() => plat.ReleaseDate)));
        var dateBtn = MiniBtn("▦", 28); dateBtn.Click += (_, _) => ShowDatePopup(dateBtn, release);
        AddRow(release, "Release Date:", dateBtn);
        var developer = Tb(Safe(() => plat.Developer) ?? ""); AddRow(developer, "Developer:");
        var manufacturer = Tb(Safe(() => plat.Manufacturer) ?? ""); AddRow(manufacturer, "Manufacturer:");
        var cpu = Tb(Safe(() => plat.Cpu) ?? ""); AddRow(cpu, "CPU:");
        var memory = Tb(Safe(() => plat.Memory) ?? ""); AddRow(memory, "Memory:");
        var graphics = Tb(Safe(() => plat.Graphics) ?? ""); AddRow(graphics, "Graphics:");
        var sound = Tb(Safe(() => plat.Sound) ?? ""); AddRow(sound, "Sound:");
        var display = Tb(Safe(() => plat.Display) ?? ""); AddRow(display, "Display:");
        var media = Tb(Safe(() => plat.Media) ?? ""); AddRow(media, "Media:");
        var maxCtrl = Tb(Safe(() => plat.MaxControllers) ?? ""); AddRow(maxCtrl, "Max Controllers:");
        var videoPath = Tb(Safe(() => plat.VideoPath) ?? "");
        var browseBtn = MiniBtn("Browse…", 90);
        browseBtn.Click += (_, _) => { using var d = new OpenFileDialog { Filter = "Video files|*.mp4;*.avi;*.mkv;*.wmv;*.mov|All files|*.*" }; if (d.ShowDialog() == DialogResult.OK) videoPath.Text = d.FileName; };
        AddRow(videoPath, "Video Path:", browseBtn);
        var sortTitle = Tb(Safe(() => plat.SortTitle) ?? ""); AddRow(sortTitle, "Sort Title:");
        var hide = AddCheck("Hide in Big Box (Does Not Hide Games)", Safe(() => plat.HideInBigBox));
        var noAutoImport = AddCheck("Disable ROM Auto-Import", string.Equals(fields?.GetField("DisableAutoImport"), "true", StringComparison.OrdinalIgnoreCase));
        // Spacer row absorbs all vertical slack at the BOTTOM so the fields stay top-packed (no gaps between rows).
        var spacer = new Panel { BackColor = Bg, Margin = new Padding(0) };
        tlp.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        tlp.Controls.Add(spacer, 0, row); tlp.SetColumnSpan(spacer, 3); row++;

        void Apply()
        {
            Set(() => plat.ScrapeAs = scrapeAs.Text.Trim(), Safe(() => plat.ScrapeAs), scrapeAs.Text.Trim());
            Set(() => plat.Developer = developer.Text.Trim(), Safe(() => plat.Developer), developer.Text.Trim());
            Set(() => plat.Manufacturer = manufacturer.Text.Trim(), Safe(() => plat.Manufacturer), manufacturer.Text.Trim());
            Set(() => plat.Cpu = cpu.Text.Trim(), Safe(() => plat.Cpu), cpu.Text.Trim());
            Set(() => plat.Memory = memory.Text.Trim(), Safe(() => plat.Memory), memory.Text.Trim());
            Set(() => plat.Graphics = graphics.Text.Trim(), Safe(() => plat.Graphics), graphics.Text.Trim());
            Set(() => plat.Sound = sound.Text.Trim(), Safe(() => plat.Sound), sound.Text.Trim());
            Set(() => plat.Display = display.Text.Trim(), Safe(() => plat.Display), display.Text.Trim());
            Set(() => plat.Media = media.Text.Trim(), Safe(() => plat.Media), media.Text.Trim());
            Set(() => plat.MaxControllers = maxCtrl.Text.Trim(), Safe(() => plat.MaxControllers), maxCtrl.Text.Trim());
            Set(() => plat.VideoPath = videoPath.Text.Trim(), Safe(() => plat.VideoPath), videoPath.Text.Trim());
            Set(() => plat.SortTitle = sortTitle.Text.Trim(), Safe(() => plat.SortTitle), sortTitle.Text.Trim());
            try { if (plat is Data.HostPlatform hp && hide.Checked != Safe(() => plat.HideInBigBox)) hp.HideInBigBox = hide.Checked; } catch { }
            // ScrapeAs date + DisableAutoImport (off-SDK) via the field escape-hatch.
            try
            {
                var rd = ParseDate(release.Text.Trim());
                if (rd != Safe(() => plat.ReleaseDate)) plat.ReleaseDate = rd;
            }
            catch { }
            try { fields?.SetField("DisableAutoImport", noAutoImport.Checked ? "true" : "false"); } catch { }
        }

        // Wrap: fields (fill) + the Images panel (right) — Details tab only.
        var container = new Panel { BackColor = Bg };
        tlp.Dock = DockStyle.Fill;
        var imgPanel = new Panel { Dock = DockStyle.Right, Width = S(370), BackColor = Bg, Padding = new Padding(S(6), 0, 0, 0) };
        var imgHeader = new Label { Dock = DockStyle.Top, Height = S(30), Text = "  Images", ForeColor = Fg, BackColor = Panel2, Font = new Font("Segoe UI", 10f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft };
        var imgBody = EditPlatformImages.Build(plat, readOnly, s); imgBody.Dock = DockStyle.Fill;
        imgPanel.Controls.Add(imgBody); imgPanel.Controls.Add(imgHeader);
        container.Controls.Add(tlp);
        container.Controls.Add(imgPanel);
        tlp.BringToFront();   // Fill fills the space left of the right panel (mirrors OptionsWindow's docking)
        return (container, Apply);
    }

    // ── Notes ───────────────────────────────────────────────────────────────────
    private static (Control panel, Action apply) BuildNotes(IPlatform plat, float s)
    {
        var p = new Panel { BackColor = Bg, Padding = new Padding((int)(12 * s)) };
        var box = new TextBox
        {
            Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true,
            BackColor = Panel2, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle,
            Text = (Safe(() => plat.Notes) ?? "").Replace("\n", "\r\n"),
        };
        p.Controls.Add(box);
        void Apply()
        {
            string v = box.Text.Replace("\r\n", "\n");
            try { if (v != (Safe(() => plat.Notes) ?? "")) plat.Notes = v; } catch { }
        }
        return (p, Apply);
    }

    // ── small layout helpers (local; mirror EditEmulatorWindow's idiom) ──────────
    private static void ShowDatePopup(Control anchor, TextBox target)
    {
        var pop = new Form { FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, ShowInTaskbar = false, TopMost = true, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        var cal = new MonthCalendar { MaxSelectionCount = 1 };
        if (ParseDate(target.Text) is DateTime d) { try { cal.SetDate(d); } catch { } }
        cal.DateSelected += (_, e) => { target.Text = e.Start.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture); pop.Close(); };
        pop.Controls.Add(cal);
        try { pop.Location = anchor.PointToScreen(new Point(0, anchor.Height)); } catch { }
        pop.Deactivate += (_, _) => pop.Close();
        pop.Show(anchor.FindForm());
    }

    // Master platform-name list for the Scrape As combo. Source = the ACTIVE metadata DB (ExtendDB's Extended DB
    // when it's the main DB, else LaunchBox's own — MetadataDb.WebDbPath) ∪ this library's own platform names.
    // The combo is EDITABLE, so a hand-typed value not in the list is still accepted and persists as plain text
    // (important when the Extended DB is later disabled/de-primaried). Not cached across opens: the active DB can
    // change (Extended toggled), so we rebuild each open — cheap (one Name query).
    internal static string[] ScrapeAsChoices()
    {
        var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string? db = MetadataDb.WebDbPath();
            if (db != null && File.Exists(db))
            {
                using var con = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db};Mode=ReadOnly");
                con.Open();
                using var cmd = con.CreateCommand();
                cmd.CommandText = "select Name from Platforms where Name is not null";
                using var r = cmd.ExecuteReader();
                while (r.Read()) { var n = r.IsDBNull(0) ? null : r.GetString(0)?.Trim(); if (!string.IsNullOrEmpty(n)) set.Add(n!); }
            }
        }
        catch { }
        try { foreach (var pl in PluginHelper.DataManager?.GetAllPlatforms() ?? Array.Empty<IPlatform>()) { var n = Safe(() => pl.Name); if (!string.IsNullOrEmpty(n)) set.Add(n!); } } catch { }
        return set.ToArray();
    }

    private static void Set(Action set, string? cur, string next)
    {
        try { if ((cur ?? "") != (next ?? "")) set(); } catch { }
    }

    private static void DisableAllInputs(Control root)
    {
        foreach (Control c in root.Controls)
        {
            if (c is TextBox tb) tb.ReadOnly = true;
            else if (c is CheckBox or ComboBox or Button or NumericUpDown) c.Enabled = false;
            if (c.HasChildren) DisableAllInputs(c);
        }
    }

    internal static string DateStr(DateTime? d) => d.HasValue ? d.Value.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture) : "";
    internal static DateTime? ParseDate(string s) => DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : (DateTime?)null;
    private static T? Safe<T>(Func<T> f) { try { return f(); } catch { return default; } }
}
