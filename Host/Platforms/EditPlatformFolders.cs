// The "Folders" tab of the Edit Platform window — custom media folders per media type. LaunchBox stores these
// as root-level <PlatformFolder> rows in Platforms.xml (<Platform>/<MediaType>/<FolderPath>). A grid of
// MediaType → FolderPath (the platform's existing custom folders, plus an empty row to add). Apply rewrites this
// platform's <PlatformFolder> rows surgically (leaving other platforms' rows and the <Platform> nodes untouched).

#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using System.Xml.Linq;
using LbApiHost.Host.Media;
using LbApiHost.Host.UiKit;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Platforms;

internal static class EditPlatformFolders
{
    private static readonly Color Bg = LiteBoxTheme.Bg, Panel2 = LiteBoxTheme.Panel2, Fg = LiteBoxTheme.Fg, SubFg = LiteBoxTheme.SubFg;
    private static string PlatformsFile => Path.Combine(MediaResolver.LbRoot ?? "", "Data", "Platforms.xml");

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
        var colType = new DataGridViewTextBoxColumn { HeaderText = "Media Type", FillWeight = 40 };
        var colPath = new DataGridViewTextBoxColumn { HeaderText = "Folder Path", FillWeight = 55 };
        var colBrowse = new DataGridViewButtonColumn { HeaderText = "", Text = "…", UseColumnTextForButtonValue = true, FillWeight = 6 };
        grid.Columns.AddRange(colType, colPath, colBrowse);
        grid.ReadOnly = readOnly;

        foreach (var f in Safe(() => plat.GetAllPlatformFolders()) ?? Array.Empty<IPlatformFolder>())
        {
            string mt = Safe(() => f.MediaType) ?? "", fp = Safe(() => f.FolderPath) ?? "";
            if (mt.Length > 0) grid.Rows.Add(mt, fp, "…");
        }

        grid.CellClick += (_, e) =>
        {
            if (readOnly || e.RowIndex < 0 || e.ColumnIndex != 2) return;
            using var d = new FolderBrowserDialog();
            if (d.ShowDialog() == DialogResult.OK) grid.Rows[e.RowIndex].Cells[1].Value = d.SelectedPath;
        };

        p.Controls.Add(grid);
        p.Controls.Add(info);

        void Apply()
        {
            if (readOnly) return;
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (DataGridViewRow r in grid.Rows)
            {
                if (r.IsNewRow) continue;
                string mt = (r.Cells[0].Value as string)?.Trim() ?? "";
                string fp = (r.Cells[1].Value as string)?.Trim() ?? "";
                if (mt.Length > 0 && fp.Length > 0) map[mt] = fp;
            }
            WritePlatformFolders(name, map);
        }
        return (p, Apply);
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
            doc.Save(PlatformsFile);
        }
        catch { }
    }

    private static T? Safe<T>(Func<T> f) { try { return f(); } catch { return default; } }
}
