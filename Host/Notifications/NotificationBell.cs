// The bell in the top menu bar, and the list that drops out of it.
//
// LaunchBox puts a bell with an unread dot in its top bar; clicking it opens a "Notifications" panel with
// every notification it still remembers and a "Clear All" button. Same idea here, with the popup drawn in
// LiteBox's palette.
//
// The glyph is DRAWN (GDI+), not shipped as art: it has to carry a live unread count, and a badge baked
// into a PNG would be a sprite sheet of eleven variants. Drawing it also means it follows the theme colours
// and any DPI for free.
//
// Opening the list marks everything read — that is what "I looked at my notifications" means, and it is
// what LaunchBox does. The entries stay in the list until they are removed or cleared.

#nullable enable

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using LbApiHost.Host.UiKit;
using LiteBox.Notifications;

namespace LbApiHost.Host.Notifications;

internal sealed class NotificationBell
{
    private readonly ToolStrip _bar;
    private readonly ToolStripLabel _item;
    private readonly Form _owner;
    private NotificationListPopup? _popup;
    private Image? _glyph;
    private int _shownCount = -1;

    public ToolStripItem Item => _item;

    public NotificationBell(Form owner, ToolStrip bar)
    {
        _owner = owner;
        _bar = bar;
        _item = new ToolStripLabel("")
        {
            Alignment = ToolStripItemAlignment.Right,
            DisplayStyle = ToolStripItemDisplayStyle.Image,
            ImageScaling = ToolStripItemImageScaling.None,
            Margin = new Padding(6, 0, 6, 0),
            ToolTipText = "Notifications",
        };
        _item.Click += (_, _) => Toggle();
        Repaint();

        NotificationCenter.Changed += OnChanged;
        owner.FormClosed += (_, _) =>
        {
            NotificationCenter.Changed -= OnChanged;
            try { _popup?.Close(); } catch { }
            _glyph?.Dispose();
        };
    }

    private void OnChanged()
    {
        try
        {
            if (_bar.IsDisposed || !_bar.IsHandleCreated) return;
            if (_bar.InvokeRequired) _bar.BeginInvoke(new Action(Refresh));
            else Refresh();
        }
        catch { }
    }

    private void Refresh()
    {
        Repaint();
        _popup?.Rebuild();
    }

    private void Repaint()
    {
        int unread = NotificationCenter.UnreadCount;
        if (unread == _shownCount && _glyph != null) return;
        _shownCount = unread;

        int size = (int)Math.Round(22 * (_bar.DeviceDpi / 96f));
        var old = _glyph;
        _glyph = DrawBell(size, unread, _bar.BackColor);
        _item.Image = _glyph;
        old?.Dispose();

        _item.ToolTipText = unread == 0
            ? (NotificationCenter.Count == 0 ? "Notifications" : $"Notifications ({NotificationCenter.Count})")
            : $"{unread} unread notification{(unread == 1 ? "" : "s")}";
    }

    /// <summary>The glyph: a bell in the lower-left of the box, its unread badge in the upper-right with a
    /// ring in the BAR's own colour — without that ring the red disc melts into the bell's silhouette and
    /// the whole thing reads as a smudge at 22px.</summary>
    private static Image DrawBell(int size, int unread, Color barBack)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        float s = size / 22f;                       // the shape below is authored on a 22px grid
        float cx = 9f * s;                          // left of centre: the badge owns the right-hand corner
        var color = unread > 0 ? LiteBoxTheme.Fg : LiteBoxTheme.SubFg;

        using (var path = new GraphicsPath())
        {
            path.AddArc(cx - 5.5f * s, 5f * s, 11 * s, 11 * s, 180, 180);     // the dome
            path.AddLine(cx + 5.5f * s, 10.5f * s, cx + 7f * s, 15.5f * s);   // right flare
            path.AddLine(cx + 7f * s, 15.5f * s, cx - 7f * s, 15.5f * s);     // rim
            path.AddLine(cx - 7f * s, 15.5f * s, cx - 5.5f * s, 10.5f * s);   // left flare
            path.CloseFigure();
            using var b = new SolidBrush(color);
            g.FillPath(b, path);
            g.FillEllipse(b, cx - 1.2f * s, 3f * s, 2.4f * s, 2.4f * s);      // the knob on top
            g.FillEllipse(b, cx - 1.8f * s, 16f * s, 3.6f * s, 3.6f * s);     // the clapper
        }

        if (unread > 0)
        {
            float d = 11f * s;
            var box = new RectangleF(size - d, 0, d, d);
            using (var ring = new Pen(barBack, 2f * s)) g.DrawEllipse(ring, box);
            using (var b = new SolidBrush(LiteBoxTheme.Danger)) g.FillEllipse(b, box);
            // Pixel-sized font: a point-sized one drifts against the circle as the DPI changes, and the
            // digit has ~8px of room. Past 9 the count stops being a number and becomes "lots".
            using var f = new Font("Segoe UI", d * 0.62f, FontStyle.Bold, GraphicsUnit.Pixel);
            var text = unread > 9 ? "+" : unread.ToString();
            var m = g.MeasureString(text, f);
            g.DrawString(text, f, Brushes.White,
                box.Left + (box.Width - m.Width) / 2f, box.Top + (box.Height - m.Height) / 2f);
        }
        return bmp;
    }

    private void Toggle()
    {
        if (_popup is { IsDisposed: false })
        {
            var p = _popup; _popup = null;
            try { p.Close(); } catch { }
            return;
        }

        var popup = new NotificationListPopup(_owner);
        _popup = popup;
        popup.FormClosed += (_, _) => { if (ReferenceEquals(_popup, popup)) _popup = null; Repaint(); };

        // Under the bell, right edges aligned, clamped to the monitor's working area.
        Point anchor;
        try { anchor = _bar.PointToScreen(new Point(_item.Bounds.Right, _item.Bounds.Bottom + 2)); }
        catch { anchor = Cursor.Position; }
        var wa = (Screen.FromControl(_owner) ?? Screen.PrimaryScreen!).WorkingArea;
        int x = Math.Max(wa.Left + 4, Math.Min(anchor.X - popup.Width, wa.Right - popup.Width - 4));
        int y = Math.Max(wa.Top + 4, Math.Min(anchor.Y, wa.Bottom - popup.Height - 4));
        popup.Location = new Point(x, y);
        popup.Show(_owner);

        NotificationCenter.MarkAllRead();
    }
}

/// <summary>The dropdown list: every remembered notification, newest first, with its actions still live.
/// Closes when it loses focus (a dropdown, not a window you manage).</summary>
internal sealed class NotificationListPopup : LiteBoxForm
{
    private readonly FlowLayoutPanel _flow;
    private readonly Label _empty;
    private readonly int _headerHeight;
    private readonly int _maxHeight;

    public NotificationListPopup(Form owner)
    {
        Owner = owner;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        BackColor = NotificationStyle.CardBack;

        int w = S(400);
        var wa = (Screen.FromControl(owner) ?? Screen.PrimaryScreen!).WorkingArea;
        _maxHeight = Math.Min(S(520), (int)(wa.Height * 0.7));
        ClientSize = new Size(w, _maxHeight);

        var header = new Panel { Dock = DockStyle.Top, Height = S(40), BackColor = NotificationStyle.CardBack };
        _headerHeight = header.Height;
        header.Controls.Add(new Label
        {
            Text = "Notifications", AutoSize = true, ForeColor = LiteBoxTheme.Fg, BackColor = NotificationStyle.CardBack,
            Font = new Font("Segoe UI", 10f, FontStyle.Bold), Location = new Point(S(12), S(10)),
        });
        var clear = new CardButton("Clear All")
        {
            Font = new Font("Segoe UI", 8.5f),
            Location = new Point(w - S(92), S(7)), Size = new Size(S(80), S(26)),
        };
        clear.Click += (_, _) => { NotificationCenter.Clear(); Rebuild(); };
        header.Controls.Add(clear);

        _flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false,
            AutoScroll = true, BackColor = NotificationStyle.CardBack, Padding = new Padding(S(8), 0, S(8), S(4)),
        };
        _empty = new Label
        {
            Text = "No notifications.", AutoSize = true, ForeColor = LiteBoxTheme.SubFg,
            BackColor = NotificationStyle.CardBack, Margin = new Padding(S(6), S(10), 0, 0),
        };

        Controls.Add(_flow);
        Controls.Add(header);
        // Same chrome as the toast: rounded card + hairline border + drop shadow, so the dropdown reads
        // as the same family of surface. The region must chase FitToContent's resizes.
        Paint += (_, e) => NotificationStyle.PaintBorder(e.Graphics, ClientSize, S(8), NotificationStyle.CardBorder);
        Resize += (_, _) => NotificationStyle.Round(this, S(8));
        NotificationStyle.Round(this, S(8));
        Deactivate += (_, _) => { try { Close(); } catch { } };
        Rebuild();
    }

    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ClassStyle |= 0x00020000; return cp; }   // CS_DROPSHADOW
    }

    /// <summary>Rebuilds the whole list. It is at most a couple of hundred short cards and only ever runs
    /// while the panel is open, so a full rebuild beats diffing.</summary>
    public void Rebuild()
    {
        if (IsDisposed) return;
        try
        {
            _flow.SuspendLayout();
            foreach (Control c in _flow.Controls.Cast<Control>().ToArray()) { _flow.Controls.Remove(c); c.Dispose(); }

            var all = NotificationCenter.All;
            if (all.Count == 0) _flow.Controls.Add(_empty);
            else foreach (var n in all) _flow.Controls.Add(BuildCard(n));
        }
        finally { _flow.ResumeLayout(); FitToContent(); }
    }

    /// <summary>Shrink to the cards actually listed — a half-empty 520px panel hanging off the bell looks
    /// broken. Grows back up to the cap as notifications arrive while it is open.</summary>
    private void FitToContent()
    {
        int content = _flow.Padding.Vertical;
        foreach (Control c in _flow.Controls) content += c.Height + c.Margin.Vertical;
        int target = Math.Max(S(90), Math.Min(_maxHeight, _headerHeight + content + S(4)));
        if (target != ClientSize.Height) ClientSize = new Size(ClientSize.Width, target);
    }

    private Control BuildCard(LiteBoxNotification n)
    {
        int w = _flow.ClientSize.Width - S(24);   // room for the scrollbar
        var card = new Panel
        {
            Width = w, BackColor = Color.FromArgb(40, 41, 49), Margin = new Padding(0, 0, 0, S(6)),
            Padding = new Padding(S(10), S(8), S(10), S(8)),
        };

        var time = new Label
        {
            Text = n.DateRaised.ToString("yyyy-MM-dd h:mm tt") + (n.IsInProgress ? "  ·  in progress" : ""),
            AutoSize = true, ForeColor = LiteBoxTheme.SubFg, BackColor = card.BackColor,
            Font = new Font("Segoe UI", 8f), Location = new Point(S(10), S(6)),
        };
        card.Controls.Add(time);

        var kill = new Label
        {
            Text = "✕", AutoSize = false, Size = new Size(S(16), S(16)), TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = LiteBoxTheme.SubFg, BackColor = card.BackColor, Cursor = Cursors.Hand,
            Location = new Point(w - S(24), S(5)), Font = new Font("Segoe UI", 8f),
        };
        kill.MouseEnter += (_, _) => kill.ForeColor = Color.White;
        kill.MouseLeave += (_, _) => kill.ForeColor = LiteBoxTheme.SubFg;
        kill.Click += (_, _) => { NotificationCenter.Remove(n); Rebuild(); };
        card.Controls.Add(kill);

        int textWidth = w - S(30);
        var msg = new Label
        {
            Text = n.Message, AutoSize = false, UseMnemonic = false,
            ForeColor = n.IsError ? LiteBoxTheme.Danger : LiteBoxTheme.Fg, BackColor = card.BackColor,
            Font = new Font("Segoe UI", 9.5f), Location = new Point(S(10), S(24)),
            Size = new Size(textWidth, TextRenderer.MeasureText(n.Message, new Font("Segoe UI", 9.5f),
                            new Size(textWidth, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height + S(2)),
        };
        card.Controls.Add(msg);

        int bottom = msg.Bottom;
        foreach (var action in n.Actions)
        {
            var a = action;
            var btn = new CardButton(a.Label)
            {
                Font = new Font("Segoe UI", 8.5f), BackColor = card.BackColor,
                Location = new Point(S(10), bottom + S(6)), Size = new Size(textWidth, S(26)),
            };
            btn.Click += (_, _) =>
            {
                if (a.DismissOnClick) NotificationCenter.Dismiss(n);
                try { a.Run(); } catch (Exception ex) { Console.WriteLine("[notify] action '" + a.Label + "' threw: " + ex); }
                Rebuild();
            };
            card.Controls.Add(btn);
            bottom = btn.Bottom;
        }

        card.Height = bottom + S(10);
        NotificationStyle.Round(card, S(5));
        return card;
    }
}
