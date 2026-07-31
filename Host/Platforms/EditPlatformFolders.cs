// The "Folders" tab of the Edit Platform window — custom media folders per media type, LB-parity. LaunchBox
// stores these as root-level <PlatformFolder> rows in Platforms.xml (<Platform>/<MediaType>/<FolderPath>) with
// paths RELATIVE to the LB root when under it (portable installs) — the grid displays and writes that relative
// form (HostPlatform.GetAllPlatformFolders returns them resolved absolute, so we re-relativize for display).
// LB's row order: Game, Video, Manual, Music, then the rest alphabetically. A status dot per row shows whether
// the folder exists on disk. Browse… picks a folder (re-relativized); Open shows it in Explorer (LiteBox bonus).
// Apply rewrites this platform's <PlatformFolder> rows surgically (other platforms' rows untouched).

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using System.Xml.Linq;
using LbApiHost.Host.Media;
using LbApiHost.Host.UiKit;
using Unbroken.LaunchBox.Plugins.Data;
using LbApiHost.Host.Data;

namespace LbApiHost.Host.Platforms;

internal static class EditPlatformFolders
{
    private static readonly Color Bg = LiteBoxTheme.Bg, Panel2 = LiteBoxTheme.Panel2, Fg = LiteBoxTheme.Fg, SubFg = LiteBoxTheme.SubFg;
    private static readonly Color OkDot = Color.FromArgb(100, 200, 100), BadDot = Color.FromArgb(220, 130, 120);
    private static string PlatformsFile => Path.Combine(MediaResolver.LbRoot ?? "", "Data", "Platforms.xml");

    // LB lists these first, in this order; everything else follows alphabetically.
    private static readonly string[] PriorityTypes = { "Game", "Video", "Manual", "Music" };

    public static (Control panel, Action apply) Build(IPlatform plat, bool readOnly, float s)
    {
        int S(int px) => (int)Math.Round(px * s);
        string name = Safe(() => plat.Name) ?? "";
        var p = new Panel { Dock = DockStyle.Fill, BackColor = Bg, Padding = new Padding(S(10)) };

        var info = new Label { Dock = DockStyle.Top, Height = S(24), ForeColor = SubFg, BackColor = Bg, Text = "Custom media folders for this platform (leave empty to use the default location)." };

        var grid = new DataGridView
        {
            Dock = DockStyle.Fill, BackgroundColor = Panel2, ForeColor = Fg, GridColor = Color.FromArgb(70, 70, 74),
            BorderStyle = BorderStyle.None, AllowUserToResizeRows = false, RowHeadersVisible = false,
            EnableHeadersVisualStyles = false, AllowUserToAddRows = !readOnly,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.CellSelect,
        };
        grid.ColumnHeadersDefaultCellStyle.BackColor = Panel2;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Fg;
        grid.DefaultCellStyle.BackColor = Panel2; grid.DefaultCellStyle.ForeColor = Fg;
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(60, 90, 130); grid.DefaultCellStyle.SelectionForeColor = Color.White;
        var colStatus = new DataGridViewTextBoxColumn { HeaderText = "Status", FillWeight = 7, ReadOnly = true };
        colStatus.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
        var colType = new DataGridViewTextBoxColumn { HeaderText = "Media Type", FillWeight = 30 };
        var colPath = new DataGridViewTextBoxColumn { HeaderText = "Folder Path", FillWeight = 43 };
        var colBrowse = new DataGridViewButtonColumn { HeaderText = "Browse", Text = "Browse…", UseColumnTextForButtonValue = true, FillWeight = 10 };
        var colOpen = new DataGridViewButtonColumn { HeaderText = "", Text = "Open", UseColumnTextForButtonValue = true, FillWeight = 8 };
        grid.Columns.AddRange(colStatus, colType, colPath, colBrowse, colOpen);
        grid.ReadOnly = readOnly;

        // LB order: Game, Video, Manual, Music first, then the image types alphabetically. The 4 special rows
        // come from Platform-element FIELDS (<Folder>, <VideosFolder>, <ManualsFolder>, <MusicFolder>; empty
        // field = default location), NOT from <PlatformFolder> rows — write-back mirrors that split.
        string san = Sanitize(name);
        string DefaultFor(string mt) => mt switch
        {
            "Video" => Path.Combine("Videos", san),
            "Manual" => Path.Combine("Manuals", san),
            "Music" => Path.Combine("Music", san),
            _ => "",
        };
        string vids = Safe(() => plat.VideosFolder) ?? "", mans = Safe(() => plat.ManualsFolder) ?? "", mus = Safe(() => plat.MusicFolder) ?? "";
        grid.Rows.Add("●", "Game", RelPath(Safe(() => plat.Folder) ?? ""));
        grid.Rows.Add("●", "Video", RelPath(vids.Length > 0 ? vids : DefaultFor("Video")));
        grid.Rows.Add("●", "Manual", RelPath(mans.Length > 0 ? mans : DefaultFor("Manual")));
        grid.Rows.Add("●", "Music", RelPath(mus.Length > 0 ? mus : DefaultFor("Music")));
        var rows = (Safe(() => plat.GetAllPlatformFolders()) ?? Array.Empty<IPlatformFolder>())
            .Select(f => (mt: Safe(() => f.MediaType) ?? "", fp: Safe(() => f.FolderPath) ?? ""))
            .Where(x => x.mt.Length > 0 && Array.FindIndex(PriorityTypes, t => t.Equals(x.mt, StringComparison.OrdinalIgnoreCase)) < 0)
            .OrderBy(x => x.mt, StringComparer.OrdinalIgnoreCase);
        foreach (var (mt, fp) in rows)
            grid.Rows.Add("●", mt, RelPath(fp));

        void UpdateStatus(DataGridViewRow r)
        {
            if (r.IsNewRow) return;
            string fp = (r.Cells[2].Value as string)?.Trim() ?? "";
            bool ok = fp.Length > 0 && Directory.Exists(ResolveAbs(fp));
            r.Cells[0].Value = "●";
            r.Cells[0].Style.ForeColor = ok ? OkDot : BadDot;
            r.Cells[0].Style.SelectionForeColor = ok ? OkDot : BadDot;
        }
        foreach (DataGridViewRow r in grid.Rows) UpdateStatus(r);
        grid.CellValueChanged += (_, e) => { if (e.RowIndex >= 0 && e.ColumnIndex == 2) UpdateStatus(grid.Rows[e.RowIndex]); };

        grid.CellClick += (_, e) =>
        {
            if (e.RowIndex < 0) return;
            var row = grid.Rows[e.RowIndex];
            if (e.ColumnIndex == 3 && !readOnly)          // Browse… → pick a folder, store it LB-style (relative under root)
            {
                using var d = new FolderBrowserDialog();
                string curAbs = ResolveAbs((row.Cells[2].Value as string)?.Trim() ?? "");
                if (curAbs.Length > 0 && Directory.Exists(curAbs)) d.SelectedPath = curAbs;
                if (d.ShowDialog() == DialogResult.OK) { row.Cells[2].Value = RelPath(d.SelectedPath); UpdateStatus(row); }
            }
            else if (e.ColumnIndex == 4)                  // Open → show the folder in Explorer
            {
                string abs = ResolveAbs((row.Cells[2].Value as string)?.Trim() ?? "");
                if (abs.Length > 0 && Directory.Exists(abs))
                    try { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + abs + "\"") { UseShellExecute = true }); } catch { }
            }
        };

        p.Controls.Add(grid);
        p.Controls.Add(info);

        void Apply()
        {
            if (readOnly) return;
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var specials = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (DataGridViewRow r in grid.Rows)
            {
                if (r.IsNewRow) continue;
                string mt = (r.Cells[1].Value as string)?.Trim() ?? "";
                string fp = (r.Cells[2].Value as string)?.Trim() ?? "";
                if (mt.Length == 0) continue;
                if (Array.FindIndex(PriorityTypes, t => t.Equals(mt, StringComparison.OrdinalIgnoreCase)) >= 0) specials[mt] = fp;
                else if (fp.Length > 0) map[mt] = fp;
            }
            // The 4 specials go to the Platform-element fields (op-log → surgical <Platform> write on OK).
            // Video/Manual/Music: a value equal to the default location stores as EMPTY (LB semantics).
            if (plat is Data.HostPlatform hp)
            {
                if (specials.TryGetValue("Game", out var g)) hp.Folder = g;
                void SetOrDefault(string mt, Action<string> set)
                { if (specials.TryGetValue(mt, out var v)) set(string.Equals(v, DefaultFor(mt), StringComparison.OrdinalIgnoreCase) ? "" : v); }
                SetOrDefault("Video", v => hp.VideosFolder = v);
                SetOrDefault("Manual", v => hp.ManualsFolder = v);
                SetOrDefault("Music", v => hp.MusicFolder = v);
            }
            WritePlatformFolders(name, map);
        }
        return (p, Apply);
    }

    // Display/store form — LB's actual rule (evidenced by its own writes, e.g. a GameCube <Folder> of
    // "..\..\..\..\Downloads"): relative to the LB root whenever possible, INCLUDING ..\ segments for paths
    // outside it on the same volume; a different volume stays absolute (GetRelativePath returns it unchanged).
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

    // Rewrite this platform's <PlatformFolder> rows (root-level) to exactly `map`; other platforms untouched.
    private static void WritePlatformFolders(string platform, Dictionary<string, string> map)
    {
        try
        {
            if (!File.Exists(PlatformsFile)) return;
            var doc = XDocument.Load(PlatformsFile);
            var root = doc.Root; if (root == null) return;
            foreach (var e in root.Elements("PlatformFolder").Where(e => string.Equals((string?)e.Element("Platform"), platform, StringComparison.OrdinalIgnoreCase)).ToList())
                e.Remove();
            foreach (var kv in map)
                root.Add(new XElement("PlatformFolder",
                    new XElement("MediaType", kv.Key),
                    new XElement("FolderPath", kv.Value),
                    new XElement("Platform", platform)));
            LbXml.Save(doc, PlatformsFile);
        }
        catch { }
    }

    private static string Sanitize(string sn)
    {
        if (string.IsNullOrEmpty(sn)) return sn;
        foreach (var c in Path.GetInvalidFileNameChars()) sn = sn.Replace(c, '_');
        return sn.Trim();
    }
    private static T? Safe<T>(Func<T> f) { try { return f(); } catch { return default; } }
}
