// One notification popup: the dark rounded card that fades into the bottom-right corner of the monitor
// the LiteBox window is on, above the taskbar.
//
// Shape (LaunchBox's, near enough that the two look like siblings):
//
//     ╭──────────────────────────────────────────╮
//     │ 2026-08-04 7:55 PM                     × │
//     │  🎮  One or more of your installed       │
//     │      plugins have updates pending!       │
//     │  ╭────────────────────────────────────╮  │
//     │  │          Open Plugin Manager       │  │
//     │  ╰────────────────────────────────────╯  │
//     ╰──────────────────────────────────────────╯
//
// Rounded corners (Region) + a CS_DROPSHADOW class style stand in for LaunchBox's WPF chrome; the icon is
// the sender's if it gave one, the LiteBox app icon otherwise, and a red warning triangle for errors —
// mirroring how LaunchBox shows the raising app's colourful icon rather than a semantic glyph.
//
// The window NEVER activates (WS_EX_NOACTIVATE, baked into CreateParams because WinForms loses TopMost
// when ShowWithoutActivation rides the activation path — the same lesson as Gameplay\InfoOverlay). So a
// popup can appear while you type in the search box without stealing a keystroke, and its buttons still
// click: a no-activate window receives mouse input, it just doesn't take the foreground.
//
// Lifetime: a countdown that PAUSES while the pointer is over the card (you cannot lose a notification by
// reading it). Expiring on its own leaves it UNREAD — the bell keeps the badge; closing it by hand marks
// it read, because you clearly saw it.

#nullable enable

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using LbApiHost.Host.UiKit;
using LiteBox.Notifications;

namespace LbApiHost.Host.Notifications;

internal sealed class NotificationToast : LiteBoxForm
{
    public LiteBoxNotification Model { get; }

    private readonly Action<NotificationToast> _onClosed;
    private readonly Label _message;
    private readonly Label _time;
    private readonly Label _close;
    private readonly System.Windows.Forms.Timer _life = new() { Interval = 250 };
    private readonly System.Windows.Forms.Timer _fade = new() { Interval = 15 };
    private readonly int _radius;
    private readonly Rectangle _iconBox;
    private int _remainingMs;
    private bool _hovered;
    private bool _fadingOut;

    public NotificationToast(LiteBoxNotification model, Action<NotificationToast> onClosed)
    {
        Model = model;
        _onClosed = onClosed;
        _radius = S(8);

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        BackColor = NotificationStyle.CardBack;
        Opacity = 0;                      // faded in by _fade
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);

        int pad = S(14);
        int width = S(380);
        int iconEdge = S(34);
        int textLeft = pad + iconEdge + S(12);
        int textWidth = width - textLeft - pad;

        _time = new Label
        {
            Text = model.DateRaised.ToString("yyyy-MM-dd h:mm tt"),
            AutoSize = true, ForeColor = LiteBoxTheme.SubFg, BackColor = NotificationStyle.CardBack,
            Font = new Font("Segoe UI", 8f), Location = new Point(pad, pad - S(2)),
        };
        Controls.Add(_time);

        _close = new Label
        {
            Text = "✕", AutoSize = false, Size = new Size(S(18), S(18)),
            TextAlign = ContentAlignment.MiddleCenter, ForeColor = LiteBoxTheme.SubFg, BackColor = NotificationStyle.CardBack,
            Font = new Font("Segoe UI", 9f), Cursor = Cursors.Hand,
            Location = new Point(width - pad - S(16), pad - S(4)),
        };
        _close.MouseEnter += (_, _) => _close.ForeColor = Color.White;
        _close.MouseLeave += (_, _) => _close.ForeColor = LiteBoxTheme.SubFg;
        _close.Click += (_, _) => { NotificationCenter.MarkRead(Model); FadeOut(); };
        Controls.Add(_close);

        int top = pad + S(22);
        int msgHeight = MeasureHeight(model.Message, textWidth);
        // Single-liners centre on the icon; a wrapped message top-aligns with it (centring a tall block
        // around a 34px icon pushes the first line above it).
        int msgTop = msgHeight <= S(22) ? top + (iconEdge - msgHeight) / 2 : top;
        _iconBox = new Rectangle(pad, top, iconEdge, iconEdge);

        _message = new Label
        {
            Text = model.Message, AutoSize = false, UseMnemonic = false,
            ForeColor = LiteBoxTheme.Fg, BackColor = NotificationStyle.CardBack,
            Font = new Font("Segoe UI", 9.5f),
            Location = new Point(textLeft, msgTop),
            Size = new Size(textWidth, msgHeight),
        };
        _message.Click += (_, _) => NotificationCenter.MarkRead(Model);
        Controls.Add(_message);

        int bottom = Math.Max(_message.Bottom, _iconBox.Bottom);

        // Actions: full-width outlined buttons, in the order the sender listed them.
        foreach (var action in Model.Actions)
        {
            var a = action;
            var btn = new CardButton(a.Label)
            {
                Location = new Point(pad, bottom + S(10)),
                Size = new Size(width - pad * 2, S(30)),
            };
            btn.Click += (_, _) =>
            {
                NotificationCenter.MarkRead(Model);
                if (a.DismissOnClick) FadeOut();
                // The callback belongs to whoever raised the notification (often a plugin): run it AFTER
                // the popup has started closing, and never let it take the UI down with it.
                try { a.Run(); }
                catch (Exception ex) { Console.WriteLine("[notify] action '" + a.Label + "' threw: " + ex); }
            };
            Controls.Add(btn);
            bottom = btn.Bottom;
        }

        ClientSize = new Size(width, bottom + pad);
        NotificationStyle.Round(this, _radius);

        // Hover pauses the countdown — for the card AND everything on it (a child control eats the
        // form's mouse events, so each one re-reports).
        HookHover(this);
        foreach (Control c in Controls) HookHover(c);

        _life.Tick += (_, _) =>
        {
            if (_hovered || _fadingOut) return;
            _remainingMs -= _life.Interval;
            if (_remainingMs <= 0) { _life.Stop(); FadeOut(); }   // timed out ⇒ stays UNREAD on purpose
        };
        _fade.Tick += OnFadeTick;
    }

    /// <summary>Re-reads the model after an Update()/Complete() — text, size, colours. Deliberately does
    /// NOT touch the countdown: this runs on EVERY change to the list (another notification arriving,
    /// the bell marking everything read…), and restarting the timer there would leave a popup up forever
    /// on a busy session. The re-show path restarts it explicitly.</summary>
    public void Sync()
    {
        if (IsDisposed || _message.Text == Model.Message) return;
        _message.Text = Model.Message;
        int h = MeasureHeight(Model.Message, _message.Width);
        if (h != _message.Height)
        {
            int delta = h - _message.Height;
            _message.Height = h;
            foreach (Control c in Controls) if (c is CardButton b) b.Top += delta;
            ClientSize = new Size(ClientSize.Width, ClientSize.Height + delta);
            NotificationStyle.Round(this, _radius);   // the region doesn't follow a resize on its own
        }
        Invalidate();
    }

    /// <summary>(Re)starts the countdown from the model's effective lifespan. Sticky ⇒ no timer at all.</summary>
    public void RestartLife()
    {
        int secs = NotificationSettings.EffectiveSeconds(Model);
        _life.Stop();
        if (secs <= 0) { _remainingMs = 0; return; }
        _remainingMs = secs * 1000;
        _life.Start();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        RestartLife();
        _fadeTarget = 1.0;
        _fade.Start();
    }

    /// <summary>Fades out and closes. Safe to call twice.</summary>
    public void FadeOut()
    {
        if (_fadingOut || IsDisposed) return;
        _fadingOut = true;
        _life.Stop();
        _fadeTarget = 0.0;
        _fade.Start();
    }

    private double _fadeTarget = 1.0;

    private void OnFadeTick(object? sender, EventArgs e)
    {
        if (IsDisposed) { _fade.Stop(); return; }
        double step = 0.12;
        double o = Opacity;
        if (_fadeTarget > o) o = Math.Min(_fadeTarget, o + step);
        else o = Math.Max(_fadeTarget, o - step);
        try { Opacity = o; } catch { }
        if (Math.Abs(o - _fadeTarget) > 0.001) return;
        _fade.Stop();
        if (_fadeTarget == 0.0) { try { Close(); } catch { } }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _life.Stop(); _fade.Stop();
        _life.Dispose(); _fade.Dispose();
        Model.IsShowing = false;
        base.OnFormClosed(e);
        try { _onClosed(this); } catch { }
    }

    private void HookHover(Control c)
    {
        c.MouseEnter += (_, _) => _hovered = true;
        c.MouseLeave += (_, _) =>
        {
            // MouseLeave on a child fires when the pointer moves onto a sibling: re-test against the real
            // cursor position instead of trusting the event.
            _hovered = !IsDisposed && Bounds.Contains(Cursor.Position);
        };
    }

    private int MeasureHeight(string text, int width)
        => Math.Max(S(20), TextRenderer.MeasureText(text ?? "", new Font("Segoe UI", 9.5f),
                    new Size(width, int.MaxValue),
                    TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height + S(4));

    /// <summary>WS_EX_NOACTIVATE + WS_EX_TOPMOST at birth (see the file header), WS_EX_TOOLWINDOW so the
    /// popup never shows in Alt-Tab, and CS_DROPSHADOW for the soft edge a borderless card needs to sit
    /// visually above the window.</summary>
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000   // WS_EX_NOACTIVATE
                        | 0x00000008   // WS_EX_TOPMOST
                        | 0x00000080;  // WS_EX_TOOLWINDOW
            cp.ClassStyle |= 0x00020000;   // CS_DROPSHADOW
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        using (var b = new SolidBrush(NotificationStyle.CardBack)) g.FillRectangle(b, ClientRectangle);
        NotificationStyle.PaintBorder(g, ClientSize, _radius,
            Model.IsError ? Color.FromArgb(120, LiteBoxTheme.Danger) : NotificationStyle.CardBorder);
        DrawIcon(g, _iconBox);
    }

    /// <summary>The card's icon: the sender's if it provided one, a warning triangle for errors, the
    /// LiteBox app icon otherwise — the same "who is talking" role LaunchBox's colourful plugin icon
    /// plays, instead of a flat semantic glyph.</summary>
    private void DrawIcon(Graphics g, Rectangle box)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (Model.Icon != null)
        {
            try { g.DrawImage(Model.Icon, box); return; } catch { }
        }

        if (Model.IsError)
        {
            using var path = new GraphicsPath();
            path.AddPolygon(new[]
            {
                new Point(box.Left + box.Width / 2, box.Top),
                new Point(box.Right, box.Bottom),
                new Point(box.Left, box.Bottom),
            });
            using (var b = new SolidBrush(LiteBoxTheme.Danger)) g.FillPath(b, path);
            using var f = new Font("Segoe UI", 10f, FontStyle.Bold);
            TextRenderer.DrawText(g, "!", f, new Rectangle(box.Left, box.Top + box.Height / 5, box.Width, box.Height),
                Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }

        var app = NotificationStyle.AppIcon(box.Width);
        if (app != null) { try { g.DrawImage(app, box); return; } catch { } }

        // Last resort (icon resource unreadable): the accent disc.
        using (var b = new SolidBrush(LiteBoxTheme.Accent)) g.FillEllipse(b, box);
        using var bold = new Font("Segoe UI", 11f, FontStyle.Bold);
        TextRenderer.DrawText(g, Model.IsInProgress ? "…" : "i", bold, box, Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}
