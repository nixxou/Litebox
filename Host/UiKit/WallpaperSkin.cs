// Applies UiKit.Wallpaper to a window's controls, and keeps them in step when the window changes shape.
//
// Three ways a control can receive its slice, decided by what the control actually honours — established by
// screenshotting a mock-up of the real layout (Tools/BackgroundProbe), not by assumption:
//
//   Painted  — containers we draw ourselves (Panel, TableLayoutPanel, SplitContainer panels). A Paint handler
//              blits the slice and lays the panel tint over it.
//   Sliced   — native controls that DO honour BackgroundImage. ListView does (with LVM_SETTEXTBKCOLOR set to
//              CLR_NONE, else every caption sits in an opaque box). TreeView does too — the assumption that a
//              TreeView cannot be transparent turned out to be wrong, which is why the left panel gets the
//              real picture instead of a flat stand-in.
//   Sampled  — controls that cannot show an image behind their content at all (TextBox and friends). They get
//              the wallpaper's local average as a flat BackColor, which against a blurred picture reads as a
//              continuation rather than a patch.
//
// Slices are bitmaps sized to their control, so every size or position change (resize, splitter drag, a pane
// hidden) invalidates them. Rebuilds are debounced: a splitter drag fires hundreds of layout events and
// recomposing a 2 Mpx wallpaper on each one would make dragging feel like wading.

#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace LbApiHost.Host.UiKit;

internal static class WallpaperSkin
{
    private const int LVM_FIRST = 0x1000;
    private const int LVM_SETTEXTBKCOLOR = LVM_FIRST + 38;
    private const uint CLR_NONE = 0xFFFFFFFF;

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private enum Mode { Painted, Sliced, Sampled }

    private sealed class Entry
    {
        public Control C = null!;
        public Color Tint;
        public Mode How;
        public Bitmap? Slice;       // Sliced only — owned here, disposed on rebuild
        public Color OriginalBack;  // to restore when the wallpaper is switched off
    }

    private static readonly List<Entry> Entries = new();
    private static Form? _root;
    private static int _opacity = 65;
    private static System.Windows.Forms.Timer? _debounce;
    private static Size _lastRootSize;

    /// <summary>Register the window itself. Call once, before the per-control registrations.</summary>
    public static void Bind(Form root, Color tint, int tintOpacity)
    {
        _root = root;
        _opacity = tintOpacity;
        _lastRootSize = root.ClientSize;

        root.Resize += (_, _) => Schedule();
        Painted(root, tint);   // the form is just another painted container; Painted attaches the handler
    }

    /// <summary>A container we paint ourselves.</summary>
    public static void Painted(Control c, Color tint) => Add(c, tint, Mode.Painted);

    /// <summary>A native control that honours BackgroundImage (ListView, TreeView).</summary>
    public static void Sliced(Control c, Color tint) => Add(c, tint, Mode.Sliced);

    /// <summary>A control that can only take a flat colour (TextBox and friends).</summary>
    public static void Sampled(Control c, Color tint) => Add(c, tint, Mode.Sampled);

    private static void Add(Control c, Color tint, Mode how)
    {
        if (c == null) return;
        var e = new Entry { C = c, Tint = tint, How = how, OriginalBack = c.BackColor };
        Entries.Add(e);

        if (how == Mode.Painted)
        {
            // Paint AFTER the control's own background: handlers run at the end of OnPaint, so the slice
            // covers whatever BackColor was cleared to. Double buffering stops that showing as a flicker.
            DoubleBuffer(c);
            c.Paint += (_, ev) => { if (Wallpaper.Enabled) Wallpaper.Paint(ev.Graphics, c, e.Tint, _opacity); };
        }

        // Any geometry change makes an existing slice wrong (it is sized and positioned for the old layout).
        if (how != Mode.Painted)
        {
            c.SizeChanged += (_, _) => Schedule();
            c.LocationChanged += (_, _) => Schedule();
        }
        Refresh(e);
    }

    /// <summary>Re-read the settings, drop every composition and repaint. Call after the options change.</summary>
    public static void Reconfigure(int tintOpacity)
    {
        _opacity = tintOpacity;
        Wallpaper.Drop();
        RebuildNow();
    }

    private static void Schedule()
    {
        if (_root == null || _root.IsDisposed) return;

        // A window resize changes the composed wallpaper itself, not just the slices.
        if (_root.ClientSize != _lastRootSize) { _lastRootSize = _root.ClientSize; Wallpaper.Drop(); }

        _debounce ??= new System.Windows.Forms.Timer { Interval = 120 };
        _debounce.Tick -= OnDebounce;
        _debounce.Tick += OnDebounce;
        _debounce.Stop();
        _debounce.Start();
    }

    private static void OnDebounce(object? s, EventArgs e)
    {
        _debounce?.Stop();
        RebuildNow();
    }

    private static void RebuildNow()
    {
        foreach (var en in Entries)
        {
            if (en.C.IsDisposed) continue;
            Refresh(en);
        }
        if (_root is { IsDisposed: false }) _root.Invalidate(true);
    }

    private static void Refresh(Entry e)
    {
        try
        {
            switch (e.How)
            {
                case Mode.Painted:
                    e.C.Invalidate();
                    break;

                case Mode.Sliced:
                    var old = e.Slice;
                    e.Slice = Wallpaper.Enabled ? Wallpaper.Slice(e.C, e.Tint, _opacity) : null;
                    // Assign before disposing the previous bitmap: the control keeps using whatever it was
                    // given until it is handed something else, and drawing from a disposed bitmap throws
                    // inside a paint cycle, where there is no good way to recover.
                    e.C.BackgroundImage = e.Slice;
                    if (e.Slice != null && e.C is ListView lv && lv.IsHandleCreated)
                    {
                        lv.BackgroundImageTiled = false;
                        SendMessage(lv.Handle, LVM_SETTEXTBKCOLOR, IntPtr.Zero, (IntPtr)unchecked((int)CLR_NONE));
                    }
                    old?.Dispose();
                    break;

                case Mode.Sampled:
                    e.C.BackColor = Wallpaper.Enabled
                        ? Wallpaper.AverageColor(e.C, e.Tint, _opacity)
                        : e.OriginalBack;
                    break;
            }
        }
        catch { }   // a paint-adjacent helper must never take the window down
    }

    private static void DoubleBuffer(Control c)
    {
        try
        {
            typeof(Control).GetProperty("DoubleBuffered",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?.SetValue(c, true);
        }
        catch { }
    }
}
