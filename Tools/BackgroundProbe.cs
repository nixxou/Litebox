// Which controls actually honour a background picture? — the second unknown of the wallpaper design, after
// the ListView scroll cost measured in ListViewBenchProbe.
//
// The assumption going in was that a TreeView cannot be made transparent, which would force the left panel to
// fall back to a flat sampled colour. That is worth verifying rather than believing: WinForms implements
// BackgroundImage on some native controls and silently ignores it on others, and the difference decides how
// much of the window the picture can actually cover.
//
// So this builds the real three-panel layout — SplitContainer, TreeView, virtual LargeIcon ListView, detail
// panel with labels — points every surface at UiKit.Wallpaper, and SCREENSHOTS the result to a PNG. The image
// is the answer: whatever renders flat in the picture is a surface that needs the average-colour fallback.
// It doubles as the first look at the design before MainWindow is touched at all.
//
//   LiteBox.exe --bgprobe <out.png> [image] [blur] [darken] [tint]

#nullable enable

using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using LbApiHost.Host.UiKit;

namespace LbApiHost.Tools;

internal static class BackgroundProbe
{
    private const int LVM_FIRST = 0x1000;
    private const int LVM_SETTEXTBKCOLOR = LVM_FIRST + 38;
    private const uint CLR_NONE = 0xFFFFFFFF;

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    public static int Run(string[] args, int idx)
    {
        string outPng = idx + 1 < args.Length ? args[idx + 1] : Path.Combine(Path.GetTempPath(), "bgprobe.png");
        string img = idx + 2 < args.Length ? args[idx + 2] : "";
        int blur = idx + 3 < args.Length && int.TryParse(args[idx + 3], out var b) ? b : 55;
        int dark = idx + 4 < args.Length && int.TryParse(args[idx + 4], out var d) ? d : 40;
        int tint = idx + 5 < args.Length && int.TryParse(args[idx + 5], out var t) ? t : 65;

        var t2 = new Thread(() =>
        {
            try { Shoot(outPng, img, blur, dark, tint); }
            catch (Exception ex) { try { File.WriteAllText(outPng + ".err.txt", ex.ToString()); } catch { } }
        });
        t2.SetApartmentState(ApartmentState.STA);
        t2.Start();
        t2.Join();
        return 0;
    }

    private static void Shoot(string outPng, string img, int blur, int dark, int tint)
    {
        Wallpaper.Configure(true, img, blur, dark);

        var bg = Color.FromArgb(30, 30, 30);
        var panelC = Color.FromArgb(32, 33, 40);    // side panels, as LiteBoxTheme has them
        var centre = Color.FromArgb(42, 43, 52);    // centre column

        var form = new Form
        {
            Width = 1600, Height = 950, StartPosition = FormStartPosition.Manual, Location = Point.Empty,
            Text = "bgprobe", BackColor = bg, FormBorderStyle = FormBorderStyle.None,
        };

        // ── the layout MainWindow builds: outer split (tree | rest), inner split (list | details) ──
        var outer = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterWidth = 4, BackColor = bg };
        var inner = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterWidth = 4, BackColor = bg };

        var tree = new TreeView
        {
            Dock = DockStyle.Fill, BackColor = panelC, ForeColor = Color.FromArgb(222, 222, 222),
            BorderStyle = BorderStyle.None, ItemHeight = 24, HideSelection = false,
        };
        for (int i = 0; i < 14; i++)
        {
            var n = tree.Nodes.Add($"Platform {i + 1}");
            if (i < 3) { n.Nodes.Add("Sub A"); n.Nodes.Add("Sub B"); n.Expand(); }
        }

        var imgs = new ImageList { ImageSize = new Size(100, 150), ColorDepth = ColorDepth.Depth32Bit };
        for (int i = 0; i < 12; i++)
        {
            var bmp = new Bitmap(100, 150);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.FromArgb(60 + i * 11 % 150, 50, 70 + i * 7 % 150));
                g.FillRectangle(Brushes.DimGray, 6, 110, 88, 32);
            }
            imgs.Images.Add(bmp);
        }
        var list = new ListView
        {
            Dock = DockStyle.Fill, View = View.LargeIcon, VirtualMode = true, VirtualListSize = 300,
            LargeImageList = imgs, BackColor = centre, ForeColor = Color.White, BorderStyle = BorderStyle.None,
        };
        list.RetrieveVirtualItem += (_, e) => e.Item = new ListViewItem("Game " + (e.ItemIndex + 1), e.ItemIndex % 12);

        var detail = new Panel { Dock = DockStyle.Fill, BackColor = panelC };
        var title = new Label
        {
            Text = "Final Fantasy VII", AutoSize = true, ForeColor = Color.White, BackColor = Color.Transparent,
            Font = new Font("Segoe UI", 18f, FontStyle.Bold), Location = new Point(16, 14),
        };
        var meta = new Label
        {
            Text = "Sony Playstation · 1997 · Square\nRating 4.8 · Played 12 times",
            AutoSize = true, ForeColor = Color.FromArgb(200, 200, 205), BackColor = Color.Transparent,
            Font = new Font("Segoe UI", 10f), Location = new Point(16, 60),
        };
        var notes = new TextBox
        {
            Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None,
            Text = "A TextBox is a native edit control: it cannot be transparent, so it needs the sampled colour.",
            BackColor = Color.FromArgb(45, 45, 48), ForeColor = Color.FromArgb(222, 222, 222),
            Location = new Point(16, 120), Size = new Size(380, 60),
        };
        detail.Controls.Add(title);
        detail.Controls.Add(meta);
        detail.Controls.Add(notes);

        outer.Panel1.Controls.Add(tree);
        inner.Panel1.Controls.Add(list);
        inner.Panel2.Controls.Add(detail);
        outer.Panel2.Controls.Add(inner);
        form.Controls.Add(outer);
        form.Show();
        form.Activate();
        Pump();
        outer.SplitterDistance = 260;
        inner.SplitterDistance = 700;
        Pump();

        // ── point every surface at the wallpaper ──
        // Containers we own: paint the slice, then the panel tint over it.
        form.Paint += (_, e) => Wallpaper.Paint(e.Graphics, form, bg, tint);
        Hook(outer.Panel1, panelC, tint);
        Hook(outer.Panel2, bg, tint);
        Hook(inner.Panel1, centre, tint);
        Hook(inner.Panel2, panelC, tint);
        Hook(detail, panelC, tint);

        // Native controls: hand them a pre-tinted slice as BackgroundImage. Whether each one HONOURS it is
        // precisely what the screenshot reveals — the ListView is known to, the TreeView is the open question.
        var listSlice = Wallpaper.Slice(list, centre, tint);
        if (listSlice != null)
        {
            list.BackgroundImage = listSlice;
            list.BackgroundImageTiled = false;
            SendMessage(list.Handle, LVM_SETTEXTBKCOLOR, IntPtr.Zero, (IntPtr)unchecked((int)CLR_NONE));
        }
        // TreeView hides BackgroundImageTiled (it does not compile) — a first hint that it handles the image
        // differently from ListView. Whether it draws it at all is what the screenshot settles.
        var treeSlice = Wallpaper.Slice(tree, panelC, tint);
        if (treeSlice != null) tree.BackgroundImage = treeSlice;

        form.Invalidate(true);
        Pump();
        Pump();

        var shot = new Bitmap(form.Width, form.Height);
        using (var g = Graphics.FromImage(shot))
            g.CopyFromScreen(form.Location, Point.Empty, form.Size);   // as composited on screen, not as drawn
        shot.Save(outPng, System.Drawing.Imaging.ImageFormat.Png);

        shot.Dispose();
        listSlice?.Dispose();
        treeSlice?.Dispose();
        form.Close();
        form.Dispose();
    }

    private static void Hook(Control c, Color tint, int opacity)
    {
        c.BackColor = tint;
        c.Paint += (_, e) => Wallpaper.Paint(e.Graphics, c, tint, opacity);
        c.Invalidate();
    }

    private static void Pump()
    {
        for (int i = 0; i < 6; i++) { Application.DoEvents(); Thread.Sleep(30); }
    }
}
