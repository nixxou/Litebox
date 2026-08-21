// Does a native ListView survive a background image? — the risk that decides whether the "wallpaper behind
// the three panels" idea is viable at all.
//
// The poster grid is a Win32 ListView (LargeIcon + VirtualMode, thousands of items). Giving such a control a
// BackgroundImage costs far more than the blit: it takes the control off its scroll fast path, where only the
// newly exposed band is repainted and the rest is bitblt-shifted. A fixed background cannot be shifted, so
// every scroll repaints the whole client area. No amount of design work is worth anything if scrolling a
// library of covers turns to treacle, so this measures it BEFORE anything is built.
//
// Runs on its own STA thread with a real window (a ListView cannot be measured headless), so it is launched
// through an interactive scheduled task — never from a service-side shell, which has too little desktop heap
// to create window handles. Results go to a FILE, since the task's console is not ours to read.
//
//   LiteBox.exe --lvbench <out.txt> [items] [steps] [WxH ...]
//
// Methodology, learned the hard way — a first version ran "all plain passes, then all image passes" at four
// requested sizes and produced nonsense (the same configuration measured 1.40 ms/step in one run and 15.11 in
// the next; a 4K pass reported 0.20 ms/step, i.e. nothing was repainted at all). Three causes, all fixed here:
//
//   * A window larger than the physical screen is clipped, and Windows does not paint what is off-screen —
//     so the "bigger window" passes measured less work, not more. Sizes are now clamped to the work area and
//     the requested-vs-actual size is reported.
//   * Sequential A-then-B passes attribute any background load (indexer, defender, DWM) to whichever pass it
//     landed in. Passes are now INTERLEAVED round by round, so drift hits both sides equally.
//   * A mean is hostage to a single 300 ms outlier. Each round reports its MEDIAN step, and the verdict uses
//     the median of the per-round medians.
//
// Double buffering stays ON throughout: that is the configuration the app actually ships (MainWindow's
// EnableListViewDoubleBuffer, GameListView's LVS_EX_DOUBLEBUFFER), and it is precisely the fast path a
// background image forfeits — measuring without it compares two already-slow things.

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace LbApiHost.Tools;

internal static class ListViewBenchProbe
{
    private const int LVM_FIRST = 0x1000;
    private const int LVM_SCROLL = LVM_FIRST + 20;
    private const int LVM_SETTEXTBKCOLOR = LVM_FIRST + 38;
    private const int LVM_SETEXTENDEDLISTVIEWSTYLE = LVM_FIRST + 54;
    private const int LVM_GETORIGIN = LVM_FIRST + 41;
    private const int LVS_EX_DOUBLEBUFFER = 0x00010000;
    private const uint CLR_NONE = 0xFFFFFFFF;

    private const int Rounds = 6;   // interleaved A/B rounds; the median of these is the reported figure

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref POINT pt);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    public static int Run(string[] args, int idx)
    {
        string outPath = idx + 1 < args.Length ? args[idx + 1] : Path.Combine(Path.GetTempPath(), "lvbench.txt");
        int items = idx + 2 < args.Length && int.TryParse(args[idx + 2], out var it) ? it : 5000;
        int steps = idx + 3 < args.Length && int.TryParse(args[idx + 3], out var st) ? st : 60;

        var sizes = new List<Size>();
        for (int i = idx + 4; i < args.Length; i++)
        {
            var p = args[i].Split('x', 'X');
            if (p.Length == 2 && int.TryParse(p[0], out var w) && int.TryParse(p[1], out var h))
                sizes.Add(new Size(w, h));
        }

        var log = new List<string>();
        void L(string s) { log.Add(s); Console.WriteLine(s); }

        var t = new Thread(() =>
        {
            var work = Screen.PrimaryScreen?.WorkingArea.Size ?? new Size(1920, 1040);
            L($"écran utile : {work.Width}x{work.Height} — les tailles demandées sont bornées à cette surface");
            L($"{items} items, {steps} pas/round, {Rounds} rounds entrelacés, double-buffer toujours actif");
            L("");
            if (sizes.Count == 0) sizes.Add(work);

            foreach (var sz in sizes)
            {
                try { Measure(items, steps, sz, work, L); }
                catch (Exception ex) { L("FAILED: " + ex); }
                L("");
            }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();

        try { File.WriteAllLines(outPath, log); } catch { }
        return 0;
    }

    private static void Measure(int items, int steps, Size want, Size work, Action<string> L)
    {
        var size = new Size(Math.Min(want.Width, work.Width), Math.Min(want.Height, work.Height));
        string clamp = size == want ? "" : $" (demandé {want.Width}x{want.Height}, borné à l'écran)";
        L($"=== {size.Width}x{size.Height} — {size.Width * size.Height / 1_000_000.0:0.00} Mpx{clamp} ===");

        // A poster grid as the app builds it: LargeIcon + VirtualMode + an ImageList of cover-sized bitmaps.
        var imgs = new ImageList { ImageSize = new Size(100, 150), ColorDepth = ColorDepth.Depth32Bit };
        for (int i = 0; i < 24; i++)   // a handful of distinct covers, reused — the paint cost is what matters
        {
            var bmp = new Bitmap(100, 150);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.FromArgb(30 + i * 7 % 200, 40, 60 + i * 5 % 180));
                g.FillRectangle(Brushes.DimGray, 6, 100, 88, 40);
            }
            imgs.Images.Add(bmp);
        }

        var form = new Form
        {
            Width = size.Width, Height = size.Height, StartPosition = FormStartPosition.Manual,
            Location = Point.Empty, Text = "lvbench", FormBorderStyle = FormBorderStyle.None,
        };
        var lv = new ListView
        {
            Dock = DockStyle.Fill, View = View.LargeIcon, VirtualMode = true, VirtualListSize = items,
            LargeImageList = imgs, OwnerDraw = false, BackColor = Color.FromArgb(24, 24, 28),
            ForeColor = Color.White,
        };
        lv.RetrieveVirtualItem += (_, e) => e.Item = new ListViewItem("Game " + e.ItemIndex, e.ItemIndex % 24);
        form.Controls.Add(lv);
        form.Show();
        form.Activate();
        Pump();

        DoubleBuffer(lv, true);   // the shipped configuration, both sides of the comparison

        // The design under test: a window-sized wallpaper slice as the control's background, with the label
        // background made transparent (otherwise every caption sits on an opaque box — the ugly result the
        // whole idea is meant to avoid). Built once; the A/B toggle just attaches or detaches it.
        var wall = new Bitmap(size.Width, size.Height);
        using (var g = Graphics.FromImage(wall))
        {
            using var br = new System.Drawing.Drawing2D.LinearGradientBrush(
                new Rectangle(Point.Empty, size), Color.FromArgb(40, 20, 60), Color.FromArgb(10, 30, 40), 45f);
            g.FillRectangle(br, 0, 0, size.Width, size.Height);
            for (int i = 0; i < 400; i++)   // some texture so the blit is not a trivial flat fill
                g.FillEllipse(Brushes.MediumPurple, (i * 37) % size.Width, (i * 53) % size.Height, 6, 6);
        }

        Warm(lv, steps);   // handle creation, image-list realisation and first-touch page faults, once

        var plain = new List<double>();
        var image = new List<double>();
        for (int r = 0; r < Rounds; r++)
        {
            SetWall(lv, null);
            plain.Add(Pass(lv, steps));
            SetWall(lv, wall);
            image.Add(Pass(lv, steps));
        }

        double p = Median(plain), q = Median(image);
        L($"sans image : {p,7:0.00} ms/pas   (rounds {string.Join(" ", plain.Select(v => v.ToString("0.0")))})");
        L($"AVEC image : {q,7:0.00} ms/pas   (rounds {string.Join(" ", image.Select(v => v.ToString("0.0")))})");

        double ratio = q / Math.Max(0.001, p);
        L($"verdict : {ratio:0.00}x plus lent avec l'image ({p:0.00} -> {q:0.00} ms/pas, "
          + $"soit {1000.0 / Math.Max(0.001, q):0} fps de défilement continu)");
        L(ratio > 2.5 ? "  => rédhibitoire pour le ListView natif"
          : ratio > 1.4 ? "  => surcoût net mais peut-être acceptable"
          : "  => surcoût négligeable, l'idée passe");

        try { form.Close(); form.Dispose(); wall.Dispose(); imgs.Dispose(); } catch { }
    }

    private static void SetWall(ListView lv, Bitmap? wall)
    {
        lv.BackgroundImage = wall;
        if (wall != null)
        {
            lv.BackgroundImageTiled = false;
            SendMessage(lv.Handle, LVM_SETTEXTBKCOLOR, IntPtr.Zero, (IntPtr)unchecked((int)CLR_NONE));
        }
        lv.Invalidate();
        Pump();
    }

    private static void Warm(ListView lv, int steps)
    {
        for (int i = 0; i < Math.Min(20, steps); i++) { Scroll(lv, 160); lv.Update(); }
        Pump();
    }

    // One round: scroll from the top with a net downward drift, forcing a synchronous repaint at each step
    // (Update processes WM_PAINT now), so the figure is paint cost and not message-queue latency. Returns the
    // MEDIAN step — a mean would be dictated by one stray 300 ms hitch from elsewhere in the system.
    //
    // Guards against measuring nothing: if the scroll origin never moved (already at the bottom, or the
    // content fits), every step is a no-op repaint and the round is reported as -1 rather than as "fast".
    private static double Pass(ListView lv, int steps)
    {
        Scroll(lv, -100_000);   // back to the top so every round covers the same ground
        lv.Update();
        Pump();

        var before = Origin(lv);
        var each = new List<double>(steps);
        for (int i = 0; i < steps; i++)
        {
            var one = Stopwatch.StartNew();
            Scroll(lv, i % 2 == 0 ? 160 : -140);   // net downward drift, both directions exercised
            lv.Update();
            one.Stop();
            each.Add(one.Elapsed.TotalMilliseconds);
        }
        return Origin(lv).Y == before.Y ? -1 : Median(each);
    }

    private static double Median(List<double> v)
    {
        var s = v.Where(x => x >= 0).OrderBy(x => x).ToList();
        if (s.Count == 0) return -1;
        return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2;
    }

    private static POINT Origin(ListView lv)
    {
        var pt = new POINT();
        SendMessage(lv.Handle, LVM_GETORIGIN, IntPtr.Zero, ref pt);
        return pt;
    }

    private static void Pump()
    {
        for (int i = 0; i < 3; i++) { Application.DoEvents(); Thread.Sleep(15); }
    }

    private static void Scroll(ListView lv, int dy)
        => SendMessage(lv.Handle, LVM_SCROLL, IntPtr.Zero, (IntPtr)dy);

    private static void DoubleBuffer(ListView lv, bool on)
        => SendMessage(lv.Handle, LVM_SETEXTENDEDLISTVIEWSTYLE,
                       (IntPtr)LVS_EX_DOUBLEBUFFER, (IntPtr)(on ? LVS_EX_DOUBLEBUFFER : 0));
}
