// Choosing a custom badge's image: from a file or from a URL, with a square crop frame and both
// previews — the source with the frame on it, and what will actually be saved.
//
// Why a SQUARE frame: a badge is drawn in a square cell everywhere (list strip, tile grid, hero
// row). Letting the user place that square themselves is what turns "any picture" into a usable
// icon; a plain contain-fit would letterbox a wide logo down to nothing.
//
// The saved PNG is 128×128 — not the 40 px LaunchBox's own packs use. The size options go up to
// 200%, and the list/tile/hero renderers scale from the source: 40 px enlarged is mush, 128 px
// downscaled is sharp. It goes into the "LiteBox Custom" badge pack, so the normal image index
// finds it with no special case.

#nullable enable

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;
using LbApiHost.Host.Diag;
using LbApiHost.Host.UiKit;

namespace LbApiHost.Host.Badges;

internal sealed class BadgeImageDialog : LiteBoxForm
{
    private const int Saved = 128;          // stored PNG side
    private const int SourceBox = 300;      // the source preview is bounded to this

    private readonly BadgeCustom _badge;
    private readonly CropView _crop;
    private readonly PictureBox _resultBig, _resultSmall;
    private readonly Label _info;
    private Image? _source;
    private string? _savedPath;
    private static readonly System.Net.Http.HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private BadgeImageDialog(BadgeCustom badge)
    {
        _badge = badge;

        Text = "Badge Image";
        ClientSize = new Size(S(640), S(430));
        MinimumSize = new Size(S(560), S(380));
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false; MaximizeBox = false;

        var top = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, Height = S(44), BackColor = LiteBoxTheme.Bg,
            FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Padding = new Padding(S(12), S(8), 0, 0),
        };
        var fromFile = ActionButton("From File…");
        fromFile.Click += (_, _) => LoadFromFile();
        var fromUrl = ActionButton("From URL…");
        fromUrl.Click += (_, _) => LoadFromUrl();
        top.Controls.Add(fromFile);
        top.Controls.Add(fromUrl);

        _crop = new CropView
        {
            Location = new Point(S(12), S(56)), Size = new Size(S(SourceBox), S(SourceBox)),
            BackColor = LiteBoxTheme.Panel2,
        };
        _crop.CropChanged += UpdateResult;

        var resLabel = new Label
        {
            Text = "Saved as", AutoSize = true, ForeColor = LiteBoxTheme.SubFg,
            Location = new Point(S(SourceBox) + S(32), S(56)),
        };
        _resultBig = new PictureBox
        {
            Location = new Point(S(SourceBox) + S(32), S(78)), Size = new Size(S(128), S(128)),
            BackColor = LiteBoxTheme.Panel2, SizeMode = PictureBoxSizeMode.Zoom,
        };
        var smallLabel = new Label
        {
            Text = "At list size", AutoSize = true, ForeColor = LiteBoxTheme.SubFg,
            Location = new Point(S(SourceBox) + S(32), S(214)),
        };
        _resultSmall = new PictureBox
        {
            Location = new Point(S(SourceBox) + S(32), S(236)), Size = new Size(S(24), S(24)),
            BackColor = LiteBoxTheme.Panel2, SizeMode = PictureBoxSizeMode.Zoom,
        };
        _info = new Label
        {
            Location = new Point(S(12), S(SourceBox) + S(62)), Size = new Size(S(SourceBox), S(40)),
            ForeColor = LiteBoxTheme.SubFg,
            Text = "Pick a file or paste a URL. Drag the square to frame the badge, drag a corner or use the wheel to resize it.",
        };

        var footer = new Panel { Dock = DockStyle.Bottom, BackColor = LiteBoxTheme.PanelC, Height = S(44) };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Right, FlowDirection = FlowDirection.RightToLeft, WrapContents = false,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = LiteBoxTheme.PanelC,
            Padding = new Padding(0, S(8), S(12), 0),
        };
        var ok = ActionButton("Use This Image");
        ok.BackColor = LiteBoxTheme.Ok;
        ok.Click += (_, _) => Commit();
        var cancel = ActionButton("Cancel");
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        footer.Controls.Add(buttons);

        Controls.AddRange(new Control[] { _crop, resLabel, _resultBig, smallLabel, _resultSmall, _info, top, footer });
        CancelButton = cancel;

        // An existing badge opens on its current image, so "edit" means edit.
        var current = BadgeCustomStore.ImagePath(badge);
        if (current != null && File.Exists(current)) SetSource(LoadFile(current));
    }

    /// <summary>Runs the dialog; on OK the PNG is already written and its path returned. The badge's
    /// Id is minted here when it was still empty, since the id IS the file name.</summary>
    public static string? Choose(IWin32Window owner, BadgeCustom badge, float dpi)
    {
        using var dlg = new BadgeImageDialog(badge);
        return dlg.ShowDialog(owner) == DialogResult.OK ? dlg._savedPath : null;
    }

    // ── sources ──────────────────────────────────────────────────────────────

    private void LoadFromFile()
    {
        using var d = new OpenFileDialog
        {
            Title = "Badge image",
            Filter = "Images (*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp)|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp|All files (*.*)|*.*",
        };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        var img = LoadFile(d.FileName);
        if (img == null) MessageBox.Show(this, "That file could not be read as an image.", "Badge image",
                                         MessageBoxButtons.OK, MessageBoxIcon.Warning);
        else SetSource(img);
    }

    private void LoadFromUrl()
    {
        string? url = Prompt("Image URL", "Paste the address of the image:");
        if (string.IsNullOrWhiteSpace(url)) return;
        Image? img = null;
        try
        {
            Cursor = Cursors.WaitCursor;
            var bytes = _http.GetByteArrayAsync(url.Trim()).GetAwaiter().GetResult();
            if (bytes is { Length: > 0 }) img = Image.FromStream(new MemoryStream(bytes));
        }
        catch (Exception ex) { LbLog.Warn("badges", "image download failed: " + ex.Message); }
        finally { Cursor = Cursors.Default; }

        if (img == null) MessageBox.Show(this, "Could not download an image from that address.", "Badge image",
                                         MessageBoxButtons.OK, MessageBoxIcon.Warning);
        else SetSource(img);
    }

    private static Image? LoadFile(string path)
    {
        // Decode through a byte[] so the file isn't left locked by GDI+.
        try { return Image.FromStream(new MemoryStream(File.ReadAllBytes(path))); } catch { return null; }
    }

    private void SetSource(Image? img)
    {
        if (img == null) return;
        _source?.Dispose();
        _source = img;
        _crop.SetImage(img);
        _info.Text = $"Source: {img.Width}×{img.Height}. Drag the square to frame the badge, "
                   + $"drag a corner or use the wheel to resize it. Saved at {Saved}×{Saved}.";
        UpdateResult();
    }

    private void UpdateResult()
    {
        var bmp = Render();
        var oldBig = _resultBig.Image; var oldSmall = _resultSmall.Image;
        _resultBig.Image = bmp;
        _resultSmall.Image = bmp == null ? null : (Image)bmp.Clone();
        oldBig?.Dispose(); oldSmall?.Dispose();
    }

    /// <summary>The cropped square, scaled to the stored size.</summary>
    private Bitmap? Render()
    {
        if (_source == null) return null;
        var src = _crop.CropRectangle;
        if (src.Width <= 0 || src.Height <= 0) return null;
        var bmp = new Bitmap(Saved, Saved, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(_source, new Rectangle(0, 0, Saved, Saved), src, GraphicsUnit.Pixel);
        return bmp;
    }

    private void Commit()
    {
        if (_source == null)
        {
            MessageBox.Show(this, "Choose a file or a URL first.", "Badge image",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (string.IsNullOrWhiteSpace(_badge.Id))
        {
            // Mint the id now: it names the PNG we are about to write.
            var probe = new BadgeCustom { Name = _badge.Name };
            BadgeCustomStore.MintId(probe);
            _badge.Id = probe.Id;
        }
        var dir = BadgeCustomStore.ImageFolder;
        var path = BadgeCustomStore.ImagePath(_badge);
        if (dir == null || path == null)
        {
            MessageBox.Show(this, "The LaunchBox folder isn't known yet, so there is nowhere to save the image.",
                "Badge image", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        try
        {
            Directory.CreateDirectory(dir);
            using var bmp = Render();
            bmp?.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            _savedPath = path;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not write the image:\n\n" + ex.Message, "Badge image",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        DialogResult = DialogResult.OK;
        Close();
    }

    private string? Prompt(string title, string label)
    {
        using var f = new PromptForm();
        f.Text = title;
        f.ClientSize = new Size(S(460), S(120));
        f.StartPosition = FormStartPosition.CenterParent;
        f.MinimizeBox = false; f.MaximizeBox = false;
        var lbl = new Label { Text = label, AutoSize = true, ForeColor = LiteBoxTheme.SubFg, Location = new Point(S(12), S(14)) };
        var box = new TextBox
        {
            Location = new Point(S(12), S(38)), Width = S(436),
            BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg, BorderStyle = BorderStyle.FixedSingle,
        };
        var ok = new Button
        {
            Text = "OK", DialogResult = DialogResult.OK, FlatStyle = FlatStyle.Flat,
            BackColor = LiteBoxTheme.Ok, ForeColor = Color.White,
            Location = new Point(S(268), S(74)), Size = new Size(S(84), S(28)),
        };
        var cancel = new Button
        {
            Text = "Cancel", DialogResult = DialogResult.Cancel, FlatStyle = FlatStyle.Flat,
            BackColor = LiteBoxTheme.CancelBtn, ForeColor = Color.White,
            Location = new Point(S(360), S(74)), Size = new Size(S(88), S(28)),
        };
        f.Controls.AddRange(new Control[] { lbl, box, ok, cancel });
        f.AcceptButton = ok; f.CancelButton = cancel;
        return f.ShowDialog(this) == DialogResult.OK ? box.Text : null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _source?.Dispose(); _resultBig.Image?.Dispose(); _resultSmall.Image?.Dispose(); }
        base.Dispose(disposing);
    }

    /// <summary>A one-line input box in the app's chrome — LiteBoxForm's constructor is protected
    /// (it is meant to be inherited), so the prompt gets the smallest possible subclass.</summary>
    private sealed class PromptForm : LiteBoxForm { }

    // ── the crop frame ───────────────────────────────────────────────────────
    // Draws the image fitted into the control, with a square selection on top: drag inside to move,
    // drag a corner to resize, wheel to grow/shrink around the centre. The square is kept inside the
    // image, so the saved PNG is never part-transparent by accident.
    private sealed class CropView : Control
    {
        private Image? _img;
        private Rectangle _fit;          // where the image is drawn, in control space
        private Rectangle _sel;          // the selection, in control space (always square)
        private Point _dragFrom;
        private bool _moving, _sizing;
        private const int Handle = 10;

        public event Action? CropChanged;

        public CropView()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        }

        public void SetImage(Image img)
        {
            _img = img;
            Layout();
            int side = Math.Min(_fit.Width, _fit.Height);
            _sel = new Rectangle(_fit.X + (_fit.Width - side) / 2, _fit.Y + (_fit.Height - side) / 2, side, side);
            Invalidate();
            CropChanged?.Invoke();
        }

        /// <summary>The selection in SOURCE pixels — what gets cropped.</summary>
        public Rectangle CropRectangle
        {
            get
            {
                if (_img == null || _fit.Width <= 0) return Rectangle.Empty;
                float sx = _img.Width / (float)_fit.Width, sy = _img.Height / (float)_fit.Height;
                return new Rectangle(
                    (int)Math.Round((_sel.X - _fit.X) * sx), (int)Math.Round((_sel.Y - _fit.Y) * sy),
                    Math.Max(1, (int)Math.Round(_sel.Width * sx)), Math.Max(1, (int)Math.Round(_sel.Height * sy)));
            }
        }

        private void Layout()
        {
            if (_img == null) { _fit = Rectangle.Empty; return; }
            float r = Math.Min(Width / (float)_img.Width, Height / (float)_img.Height);
            int w = Math.Max(1, (int)(_img.Width * r)), h = Math.Max(1, (int)(_img.Height * r));
            _fit = new Rectangle((Width - w) / 2, (Height - h) / 2, w, h);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            var old = _fit;
            Layout();
            if (old.Width > 0 && _fit.Width > 0)
            {
                float k = _fit.Width / (float)old.Width;
                _sel = new Rectangle(_fit.X + (int)((_sel.X - old.X) * k), _fit.Y + (int)((_sel.Y - old.Y) * k),
                                     (int)(_sel.Width * k), (int)(_sel.Height * k));
            }
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            if (_img == null)
            {
                TextRenderer.DrawText(g, "No image", Font, ClientRectangle, LiteBoxTheme.SubFg,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(_img, _fit);

            // Everything outside the square is dimmed, so the framing reads at a glance.
            using (var veil = new SolidBrush(Color.FromArgb(140, 0, 0, 0)))
            {
                var outer = new Region(ClientRectangle);
                outer.Exclude(_sel);
                g.FillRegion(veil, outer);
                outer.Dispose();
            }
            using var pen = new Pen(Color.White, 1.5f);
            g.DrawRectangle(pen, _sel);
            using var hb = new SolidBrush(Color.White);
            foreach (var p in Corners()) g.FillRectangle(hb, p);
        }

        private Rectangle[] Corners() => new[]
        {
            new Rectangle(_sel.Right - Handle, _sel.Bottom - Handle, Handle, Handle),
            new Rectangle(_sel.X, _sel.Bottom - Handle, Handle, Handle),
            new Rectangle(_sel.Right - Handle, _sel.Y, Handle, Handle),
            new Rectangle(_sel.X, _sel.Y, Handle, Handle),
        };

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (_img == null) return;
            _dragFrom = e.Location;
            _sizing = Corners()[0].Contains(e.Location);            // bottom-right corner resizes
            _moving = !_sizing && _sel.Contains(e.Location);
            Capture = _sizing || _moving;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_img == null) return;
            Cursor = Corners()[0].Contains(e.Location) ? Cursors.SizeNWSE
                   : _sel.Contains(e.Location) ? Cursors.SizeAll : Cursors.Default;
            if (!_moving && !_sizing) return;

            int dx = e.X - _dragFrom.X, dy = e.Y - _dragFrom.Y;
            _dragFrom = e.Location;
            if (_moving) _sel.Offset(dx, dy);
            else
            {
                int d = Math.Max(dx, dy);                            // stays square
                _sel.Width = Math.Max(16, _sel.Width + d);
                _sel.Height = _sel.Width;
            }
            Clamp();
            Invalidate();
            CropChanged?.Invoke();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _moving = _sizing = false; Capture = false;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (_img == null) return;
            int d = e.Delta > 0 ? 12 : -12;
            int side = Math.Max(16, _sel.Width + d);
            _sel = new Rectangle(_sel.X + (_sel.Width - side) / 2, _sel.Y + (_sel.Height - side) / 2, side, side);
            Clamp();
            Invalidate();
            CropChanged?.Invoke();
        }

        // The square never leaves the image: shrink it first if it got bigger than the picture, then
        // push it back inside.
        private void Clamp()
        {
            if (_fit.Width <= 0) return;
            int side = Math.Min(_sel.Width, Math.Min(_fit.Width, _fit.Height));
            _sel.Width = _sel.Height = Math.Max(8, side);
            _sel.X = Math.Max(_fit.X, Math.Min(_sel.X, _fit.Right - _sel.Width));
            _sel.Y = Math.Max(_fit.Y, Math.Min(_sel.Y, _fit.Bottom - _sel.Height));
        }
    }
}
