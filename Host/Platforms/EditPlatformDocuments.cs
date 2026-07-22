// The "Documents" tab of the Edit Platform window. LaunchBox does not persist a platform document LIST in
// Platforms.xml (no <PlatformDocument> element exists); platform documents are simply files under the platform's
// manuals folder (Manuals\<Platform>\, or the platform's custom ManualsFolder). So this tab is a file manager for
// that folder — Add copies a file in, Remove deletes it, Open launches it — which is data-compatible with LB
// (both just read the folder). No invented XML is written.

#nullable enable

using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using LbApiHost.Host.Media;
using LbApiHost.Host.UiKit;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Platforms;

internal static class EditPlatformDocuments
{
    private static readonly Color Bg = LiteBoxTheme.Bg, Panel2 = LiteBoxTheme.Panel2, Fg = LiteBoxTheme.Fg, SubFg = LiteBoxTheme.SubFg;

    public static Control Build(IPlatform plat, bool readOnly, float s)
    {
        int S(int px) => (int)Math.Round(px * s);
        string name = Safe(() => plat.Name) ?? "";
        string manuals = Safe(() => plat.ManualsFolder) ?? "";
        string dir = !string.IsNullOrWhiteSpace(manuals)
            ? (Path.IsPathRooted(manuals) ? manuals : Path.Combine(MediaResolver.LbRoot ?? "", manuals))
            : Path.Combine(MediaResolver.LbRoot ?? "", "Manuals", Sanitize(name));

        var p = new Panel { Dock = DockStyle.Fill, BackColor = Bg, Padding = new Padding(S(10)) };

        var bar = new Panel { Dock = DockStyle.Top, Height = S(32), BackColor = Bg };
        Button Btn(string t, int x, int w) => new() { Text = t, Location = new Point(S(x), S(3)), Size = new Size(S(w), S(24)), FlatStyle = FlatStyle.Flat, BackColor = Panel2, ForeColor = Fg, FlatAppearance = { BorderSize = 0 } };
        var add = Btn("✚ Add…", 0, 80); add.ForeColor = Color.FromArgb(150, 210, 150);
        var open = Btn("Open", 88, 70);
        var remove = Btn("✕ Remove", 164, 90); remove.ForeColor = Color.FromArgb(220, 130, 120);
        bar.Controls.AddRange(new Control[] { add, open, remove });

        var list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, BackColor = Panel2, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle, HideSelection = false, OwnerDraw = true };
        list.Columns.Add("Name", S(320));
        list.Columns.Add("File Path", S(560));
        // Dark headers (WinForms ListView headers otherwise render white / system-themed).
        list.DrawColumnHeader += (_, e) => { e.Graphics.FillRectangle(new SolidBrush(Panel2), e.Bounds); TextRenderer.DrawText(e.Graphics, e.Header!.Text, list.Font, e.Bounds, Fg, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.LeftAndRightPadding); };
        list.DrawItem += (_, e) => e.DrawDefault = true;
        list.DrawSubItem += (_, e) => e.DrawDefault = true;
        // Fill the remaining width with the File Path column (no white residual header area to the right).
        void FillCols() { if (list.Columns.Count >= 2) list.Columns[1].Width = Math.Max(S(200), list.ClientSize.Width - list.Columns[0].Width - S(4)); }
        list.SizeChanged += (_, _) => FillCols();
        list.HandleCreated += (_, _) => FillCols();

        void Reload()
        {
            list.Items.Clear();
            try
            {
                if (Directory.Exists(dir))
                    foreach (var f in Directory.EnumerateFiles(dir).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                    {
                        var it = new ListViewItem(Path.GetFileName(f)); it.SubItems.Add(f); it.Tag = f; list.Items.Add(it);
                    }
            }
            catch { }
        }
        string? Sel() => list.SelectedItems.Count > 0 ? list.SelectedItems[0].Tag as string : null;
        add.Enabled = !readOnly; remove.Enabled = !readOnly;
        add.Click += (_, _) =>
        {
            using var d = new OpenFileDialog { Filter = "Documents|*.pdf;*.txt;*.doc;*.docx;*.rtf;*.htm;*.html|All files|*.*", Multiselect = true };
            if (d.ShowDialog() != DialogResult.OK) return;
            try { Directory.CreateDirectory(dir); } catch { }
            foreach (var src in d.FileNames) { try { File.Copy(src, Path.Combine(dir, Path.GetFileName(src)), false); } catch { } }
            Reload();
        };
        open.Click += (_, _) => { var f = Sel(); if (f != null) try { Process.Start(new ProcessStartInfo(f) { UseShellExecute = true }); } catch { } };
        remove.Click += (_, _) =>
        {
            var f = Sel(); if (f == null || readOnly) return;
            if (MessageBox.Show($"Delete document?\n{Path.GetFileName(f)}", "Remove Document", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            try { File.Delete(f); } catch { }
            Reload();
        };
        list.DoubleClick += (_, _) => { var f = Sel(); if (f != null) try { Process.Start(new ProcessStartInfo(f) { UseShellExecute = true }); } catch { } };

        p.Controls.Add(list);
        p.Controls.Add(bar);
        Reload();
        return p;
    }

    private static string Sanitize(string sn)
    {
        if (string.IsNullOrEmpty(sn)) return sn;
        foreach (var c in Path.GetInvalidFileNameChars()) sn = sn.Replace(c, '_');
        return sn.Trim();
    }
    private static T? Safe<T>(Func<T> f) { try { return f(); } catch { return default; } }
}
