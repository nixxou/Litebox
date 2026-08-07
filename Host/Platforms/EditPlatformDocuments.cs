// The "Documents" tab of the Edit Platform window — LB parity. LaunchBox persists platform documents as
// root-level <PlatformDocument> rows in Platforms.xml (<Name>/<FilePath>/<Platform>), with FilePath stored
// RELATIVE to the LB root (..\ segments allowed — e.g. "..\..\Documents\notes.txt"); the row order in the file
// is the display order (Up/Down buttons reorder). Verified empirically on 13.28 (an earlier assumption that
// platform documents were plain files under the Manuals folder was WRONG — that grid never showed LB's docs).
// Apply rewrites this platform's <PlatformDocument> rows surgically, preserving grid order.

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Forms;
using System.Xml.Linq;
using LbApiHost.Host.Media;
using LbApiHost.Host.UiKit;
using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Data;
using LbApiHost.Host.Data;

namespace LbApiHost.Host.Platforms;

internal static class EditPlatformDocuments
{
    private static readonly Color Bg = LiteBoxTheme.Bg, Panel2 = LiteBoxTheme.Panel2, Fg = LiteBoxTheme.Fg, SubFg = LiteBoxTheme.SubFg;
    private static string PlatformsFile => Path.Combine(MediaResolver.LbRoot ?? "", "Data", "Platforms.xml");

    /// <summary>This platform's documents as (display name, absolute path) — for the source-tree context menu.</summary>
    public static List<(string name, string absPath)> GetDocuments(string platformName)
    {
        var list = new List<(string, string)>();
        // The live platform first: the write is journalled, so the XML may still hold the previous set
        // and the menu would offer documents the user had just removed. XML only as a fallback.
        try
        {
            if (PluginHelper.DataManager?.GetPlatformByName(platformName) is Data.HostPlatform hp && hp.Documents != null)
            {
                foreach (var (nm, fp) in hp.Documents)
                {
                    string abs0 = ResolveAbs((fp ?? "").Trim());
                    if (abs0.Length == 0) continue;
                    list.Add((!string.IsNullOrEmpty(nm) ? nm : Path.GetFileName(abs0), abs0));
                }
                return list;
            }
        }
        catch { }
        try
        {
            if (!File.Exists(PlatformsFile)) return list;
            var doc = XDocument.Load(PlatformsFile);
            foreach (var e in doc.Root?.Elements("PlatformDocument")
                         .Where(e => string.Equals((string?)e.Element("Platform"), platformName, StringComparison.OrdinalIgnoreCase))
                     ?? Enumerable.Empty<XElement>())
            {
                string nm = (string?)e.Element("Name") ?? "";
                string abs = ResolveAbs(((string?)e.Element("FilePath") ?? "").Trim());
                if (abs.Length == 0) continue;
                list.Add((nm.Length > 0 ? nm : Path.GetFileName(abs), abs));
            }
        }
        catch { }
        return list;
    }

    public static (Control panel, Action apply) Build(IPlatform plat, bool readOnly, float s)
    {
        int S(int px) => (int)Math.Round(px * s);
        string name = Safe(() => plat.Name) ?? "";
        var p = new Panel { Dock = DockStyle.Fill, BackColor = Bg, Padding = new Padding(S(10)) };

        // Right-hand Up/Down column (LB layout).
        var side = new Panel { Dock = DockStyle.Right, Width = S(70), BackColor = Bg, Padding = new Padding(S(8), 0, 0, 0) };
        Button SideBtn(string t, int top) => new() { Text = t, Location = new Point(S(8), S(top)), Size = new Size(S(58), S(26)), FlatStyle = FlatStyle.Flat, BackColor = Panel2, ForeColor = Fg, FlatAppearance = { BorderSize = 0 } };
        var up = SideBtn("Up", 0);
        var down = SideBtn("Down", 32);
        side.Controls.AddRange(new Control[] { up, down });

        var grid = new DataGridView
        {
            Dock = DockStyle.Fill, BackgroundColor = Panel2, ForeColor = Fg, GridColor = Color.FromArgb(70, 70, 74),
            BorderStyle = BorderStyle.None, AllowUserToResizeRows = false, RowHeadersVisible = false,
            EnableHeadersVisualStyles = false, AllowUserToAddRows = !readOnly, AllowUserToDeleteRows = !readOnly,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.CellSelect,
        };
        grid.ColumnHeadersDefaultCellStyle.BackColor = Panel2;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Fg;
        grid.DefaultCellStyle.BackColor = Panel2; grid.DefaultCellStyle.ForeColor = Fg;
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(60, 90, 130); grid.DefaultCellStyle.SelectionForeColor = Color.White;
        var colName = new DataGridViewTextBoxColumn { HeaderText = "Name", FillWeight = 28 };
        var colPath = new DataGridViewTextBoxColumn { HeaderText = "File Path", FillWeight = 57 };
        var colBrowse = new DataGridViewButtonColumn { HeaderText = "Browse", Text = "Browse…", UseColumnTextForButtonValue = true, FillWeight = 15 };
        grid.Columns.AddRange(colName, colPath, colBrowse);
        grid.ReadOnly = readOnly;

        // Load this platform's <PlatformDocument> rows in file order (= LB's display order).
        try
        {
            if (File.Exists(PlatformsFile))
            {
                var doc = XDocument.Load(PlatformsFile);
                foreach (var e in doc.Root?.Elements("PlatformDocument")
                             .Where(e => string.Equals((string?)e.Element("Platform"), name, StringComparison.OrdinalIgnoreCase))
                         ?? Enumerable.Empty<XElement>())
                    grid.Rows.Add((string?)e.Element("Name") ?? "", (string?)e.Element("FilePath") ?? "", "Browse…");
            }
        }
        catch { }

        grid.CellClick += (_, e) =>
        {
            if (readOnly || e.RowIndex < 0 || e.ColumnIndex != 2) return;
            using var d = new OpenFileDialog { Filter = "Documents|*.pdf;*.txt;*.doc;*.docx;*.rtf;*.htm;*.html|All files|*.*" };
            // Existing document → start the picker in its folder, current file preselected.
            if (!grid.Rows[e.RowIndex].IsNewRow)
            {
                string curAbs = ResolveAbs((grid.Rows[e.RowIndex].Cells[1].Value as string)?.Trim() ?? "");
                if (curAbs.Length > 0 && File.Exists(curAbs))
                { d.InitialDirectory = Path.GetDirectoryName(curAbs) ?? ""; d.FileName = Path.GetFileName(curAbs); }
            }
            if (d.ShowDialog() != DialogResult.OK) return;
            if (grid.Rows[e.RowIndex].IsNewRow)
            {
                // Browse on the empty bottom row = add: materialize a real row from the picked file.
                grid.Rows.Add(Path.GetFileNameWithoutExtension(d.FileName), RelPath(d.FileName), "Browse…");
                return;
            }
            var row = grid.Rows[e.RowIndex];
            row.Cells[1].Value = RelPath(d.FileName);
            if (string.IsNullOrWhiteSpace(row.Cells[0].Value as string))
                row.Cells[0].Value = Path.GetFileNameWithoutExtension(d.FileName);   // convenience: prefill the name
        };
        // Delete: Suppr key (outside cell edit) or the context menu — CellSelect mode never yields the
        // full-row selection DataGridView's built-in row deletion requires, so both paths are explicit.
        void RemoveCurrent()
        {
            if (readOnly || grid.CurrentCell == null) return;
            var row = grid.Rows[grid.CurrentCell.RowIndex];
            if (!row.IsNewRow) grid.Rows.Remove(row);
        }
        grid.KeyDown += (_, e) => { if (e.KeyCode == Keys.Delete && !grid.IsCurrentCellInEditMode) { RemoveCurrent(); e.Handled = true; } };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Remove Document", null, (_, _) => RemoveCurrent());
        string CurrentAbs()
        {
            if (grid.CurrentCell == null || grid.Rows[grid.CurrentCell.RowIndex].IsNewRow) return "";
            return ResolveAbs((grid.Rows[grid.CurrentCell.RowIndex].Cells[1].Value as string)?.Trim() ?? "");
        }
        menu.Items.Add("Open", null, (_, _) =>
        {
            string abs = CurrentAbs();
            if (abs.Length > 0 && File.Exists(abs)) try { Process.Start(new ProcessStartInfo(abs) { UseShellExecute = true }); } catch { }
        });
        menu.Items.Add("Open Containing Folder", null, (_, _) =>
        {
            string abs = CurrentAbs();
            if (abs.Length == 0) return;
            try
            {
                if (File.Exists(abs)) Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + abs + "\"") { UseShellExecute = true });
                else { string dir = Path.GetDirectoryName(abs) ?? ""; if (Directory.Exists(dir)) Process.Start(new ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = true }); }
            }
            catch { }
        });
        grid.CellMouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Right || e.RowIndex < 0 || grid.Rows[e.RowIndex].IsNewRow) return;
            grid.CurrentCell = grid.Rows[e.RowIndex].Cells[e.ColumnIndex == 2 ? 0 : e.ColumnIndex];
            menu.Show(Cursor.Position);
        };
        // Double-click a non-button cell → open the document.
        grid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex == 2 || grid.Rows[e.RowIndex].IsNewRow) return;
            string abs = ResolveAbs((grid.Rows[e.RowIndex].Cells[1].Value as string)?.Trim() ?? "");
            if (abs.Length > 0 && File.Exists(abs))
                try { Process.Start(new ProcessStartInfo(abs) { UseShellExecute = true }); } catch { }
        };

        void Move(int delta)
        {
            if (readOnly || grid.CurrentCell == null) return;
            int i = grid.CurrentCell.RowIndex, j = i + delta, col = grid.CurrentCell.ColumnIndex;
            int last = grid.AllowUserToAddRows ? grid.Rows.Count - 2 : grid.Rows.Count - 1;   // exclude the new-row template
            if (i < 0 || i > last || j < 0 || j > last) return;
            var r = grid.Rows[i];
            grid.Rows.RemoveAt(i);
            grid.Rows.Insert(j, r);
            grid.CurrentCell = grid.Rows[j].Cells[col == 2 ? 0 : col];
        }
        up.Click += (_, _) => Move(-1);
        down.Click += (_, _) => Move(+1);
        up.Enabled = !readOnly; down.Enabled = !readOnly;

        p.Controls.Add(grid);
        p.Controls.Add(side);

        void Apply()
        {
            if (readOnly) return;
            var docs = new List<(string nm, string fp)>();
            foreach (DataGridViewRow r in grid.Rows)
            {
                if (r.IsNewRow) continue;
                string nm = (r.Cells[0].Value as string)?.Trim() ?? "";
                string fp = (r.Cells[1].Value as string)?.Trim() ?? "";
                if (nm.Length > 0 || fp.Length > 0) docs.Add((nm, fp));
            }
            WritePlatformDocuments(name, docs);
        }
        return (p, Apply);
    }

    // LB stores document paths relative to the LB root wherever possible, INCLUDING ..\ segments for files
    // outside it (same volume). A different volume stays absolute (Path.GetRelativePath returns it unchanged).
    private static string RelPath(string path)
    {
        string root = MediaResolver.LbRoot ?? "";
        if (root.Length == 0 || path.Length == 0 || !Path.IsPathRooted(path)) return path;
        try { return Path.GetRelativePath(root, Path.GetFullPath(path)); } catch { return path; }
    }

    private static string ResolveAbs(string path)
    {
        if (path.Length == 0) return "";
        try { return Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(MediaResolver.LbRoot ?? "", path)); }
        catch { return path; }
    }

    // This platform's <PlatformDocument> rows, replaced wholesale in grid order; others untouched.
    // Journalled like the folders, and applied to the live platform so GetDocuments — which feeds the
    // tree's Documents submenu — reflects the edit before the XML catches up.
    private static void WritePlatformDocuments(string platform, List<(string nm, string fp)> docs)
    {
        try
        {
            var store = (PluginHelper.DataManager as Data.HostDataManagerXml)?.Store;
            var rows = docs.Select(d => new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Name"] = d.nm,
                ["FilePath"] = d.fp,
                ["Platform"] = platform,
            }).ToList();
            store?.RecordKeyedReplace("PlatformDocument", PlatformsFile, "Platform", platform,
                                      JsonSerializer.Serialize(rows));
            if (PluginHelper.DataManager?.GetPlatformByName(platform) is Data.HostPlatform hp)
                hp.SetPlatformDocuments(docs.Select(d => (d.nm, d.fp)).ToList());
        }
        catch (Exception ex) { Console.WriteLine("[platdocs] " + ex.Message); }
    }

    private static T? Safe<T>(Func<T> f) { try { return f(); } catch { return default; } }
}
