// Adding an image to a game: where it comes from, what part of it to keep, and how big to save it —
// in one window, instead of a type/region prompt followed by a bare file dialog.
//
// The crop frame is FREE: no forced aspect, unlike the badge picker's square (a badge lands in a square
// cell, a box scan does not). Dragging inside moves the frame, the eight handles resize it, and the
// whole image is the starting frame — so "I just want the file" stays one click away.
//
// The output size follows the crop and can be overridden. Typing one side recomputes the other while
// "Keep aspect ratio" is on; unticking it lets the two run free (a deliberate stretch is sometimes
// exactly what a marquee needs). What the file will actually contain is stated at all times.
//
// Several files can still be picked at once — that is how a batch of scans gets added — and then the
// editing controls stand down: cropping N images to one rectangle would be nonsense, so they are copied
// verbatim, exactly as the old dialog did.

#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using LbApiHost.Host.UiKit;

namespace LbApiHost.Host;

internal sealed class AddImageDialog : LiteBoxForm
{
    /// <summary>What the caller has to write. <see cref="Files"/> holds the picked paths when the image
    /// is taken as-is (one or many); <see cref="Processed"/> holds the encoded bytes when the user
    /// cropped or resized, in which case there is exactly one image and <see cref="Extension"/> names
    /// its format.</summary>
    internal sealed record Result(string Type, string Region, IReadOnlyList<string> Files,
                                  byte[]? Processed, string Extension);

    private static readonly System.Net.Http.HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly ComboBox _cboType, _cboRegion;
    private readonly IReadOnlyList<string> _regions;
    private readonly CropView _crop;
    private readonly NumericUpDown _numW, _numH;
    private readonly CheckBox _keepAspect;
    private readonly Label _info, _sourceLabel;
    private readonly Button _ok, _fromFile, _fromUrl;

    private Image? _source;          // the loaded image (null until a source is chosen)
    private string? _sourcePath;     // the file it came from (null for a URL)
    private string _sourceExt = ".png";
    private List<string> _files = new();   // multi-pick: added verbatim
    private bool _syncing;                 // guards the W/H mutual recompute

    private AddImageDialog(string defType, string defRegion, IReadOnlyList<string> types,
                           IReadOnlyList<string> regions)
    {
        _regions = regions;
        Text = "Add image";
        ClientSize = new Size(S(760), S(560));
        MinimumSize = new Size(S(680), S(500));
        StartPosition = FormStartPosition.CenterParent;

        Label L(string t, int x, int y) => new()
        { Text = t, Location = new Point(S(x), S(y)), AutoSize = true, ForeColor = LiteBoxTheme.Fg, BackColor = LiteBoxTheme.Bg };

        ComboBox Cbo(int x, int y, int w) => new()
        {
            Location = new Point(S(x), S(y)), Width = S(w), DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg, FlatStyle = FlatStyle.Flat,
        };

        // ── Slot: the two dropdowns the old dialog had, same meaning ──
        Controls.Add(L("Type:", 16, 18));
        _cboType = Cbo(84, 15, 270);
        foreach (var t in types) _cboType.Items.Add(t);
        if (_cboType.Items.Count > 0) { int i = _cboType.Items.IndexOf(defType); _cboType.SelectedIndex = i >= 0 ? i : 0; }
        Controls.Add(_cboType);

        Controls.Add(L("Region:", 380, 18));
        _cboRegion = Cbo(452, 15, 270);
        foreach (var r in regions) _cboRegion.Items.Add(string.Equals(r, "none", StringComparison.OrdinalIgnoreCase) ? "No Region" : r);
        int dr = regions.ToList().FindIndex(r => string.Equals(r, defRegion, StringComparison.OrdinalIgnoreCase));
        _cboRegion.SelectedIndex = dr >= 0 ? dr : 0;
        Controls.Add(_cboRegion);

        // ── Source ──
        _fromFile = Btn("From file…", 16, 56, 110);
        _fromFile.Click += (_, _) => LoadFromFile();
        Controls.Add(_fromFile);
        _fromUrl = Btn("From URL…", 134, 56, 110);
        _fromUrl.Click += (_, _) => LoadFromUrl();
        Controls.Add(_fromUrl);
        _sourceLabel = new Label
        {
            Location = new Point(S(256), S(62)), AutoSize = true, ForeColor = LiteBoxTheme.SubFg,
            BackColor = LiteBoxTheme.Bg, Text = "No image yet — pick a file or paste a URL.",
        };
        Controls.Add(_sourceLabel);

        // ── The image, with its frame ──
        _crop = new CropView
        {
            Location = new Point(S(16), S(92)),
            Size = new Size(S(728), S(346)),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
        };
        _crop.CropChanged += OnCropChanged;
        Controls.Add(_crop);

        // ── Output ──
        var outY = 448;
        var lblOut = L("Output:", 16, outY + 4);
        lblOut.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        Controls.Add(lblOut);
        _numW = Num(84, outY, outY);
        _numH = Num(196, outY, outY);
        Controls.Add(_numW);
        var lblX = L("×", 176, outY + 4);
        lblX.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        Controls.Add(lblX);
        Controls.Add(_numH);
        _keepAspect = new CheckBox
        {
            Text = "Keep aspect ratio", Checked = true, AutoSize = true,
            Location = new Point(S(310), S(outY + 2)), ForeColor = LiteBoxTheme.Fg, BackColor = LiteBoxTheme.Bg,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
        };
        _keepAspect.CheckedChanged += (_, _) => { if (_keepAspect.Checked) SyncFrom(_numW); UpdateInfo(); };
        Controls.Add(_keepAspect);

        var reset = Btn("Reset frame", 470, outY - 2, 110);
        reset.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        reset.Click += (_, _) => { _crop.ResetFrame(); };
        Controls.Add(reset);

        _info = new Label
        {
            Location = new Point(S(16), S(outY + 34)), AutoSize = true, ForeColor = LiteBoxTheme.SubFg,
            BackColor = LiteBoxTheme.Bg, Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
        };
        Controls.Add(_info);

        // ── OK / Cancel ──
        _ok = Btn("OK", 540, 512, 96);
        _ok.BackColor = Color.FromArgb(50, 110, 65);
        _ok.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        _ok.Enabled = false;
        _ok.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        Controls.Add(_ok);
        var cancel = Btn("Cancel", 646, 512, 96);
        cancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        Controls.Add(cancel);
        AcceptButton = _ok; CancelButton = cancel;

        UpdateInfo();
    }

    private Button Btn(string text, int x, int y, int w) => new()
    {
        Text = text, Location = new Point(S(x), S(y)), Size = new Size(S(w), S(26)),
        FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(60, 60, 72), ForeColor = LiteBoxTheme.Fg,
        FlatAppearance = { BorderSize = 0 }, Cursor = Cursors.Hand,
    };

    private NumericUpDown Num(int x, int y, int _)
    {
        var n = new NumericUpDown
        {
            Location = new Point(S(x), S(y)), Width = S(84), Minimum = 1, Maximum = 20000, Value = 1,
            BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg, BorderStyle = BorderStyle.FixedSingle,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left, Enabled = false,
        };
        n.ValueChanged += (s, _) => { if (!_syncing && _keepAspect.Checked) SyncFrom((NumericUpDown)s!); UpdateInfo(); };
        return n;
    }

    // ── Sources ───────────────────────────────────────────────────────────────
    private void LoadFromFile()
    {
        using var ofd = new OpenFileDialog
        { Title = "Add image(s)", Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp", CheckFileExists = true, Multiselect = true };
        if (ofd.ShowDialog(this) != DialogResult.OK || ofd.FileNames.Length == 0) return;

        _files = ofd.FileNames.ToList();
        if (_files.Count > 1)
        {
            // A batch: no single frame can mean anything across N pictures, so they go in untouched.
            SetSource(null, null, ".png");
            _sourceLabel.Text = $"{_files.Count} files — added as-is (crop and resize apply to a single image).";
            _ok.Enabled = true;
            UpdateInfo();
            return;
        }
        try
        {
            var img = LoadImage(File.ReadAllBytes(_files[0]));
            SetSource(img, _files[0], Path.GetExtension(_files[0]));
        }
        catch (Exception ex) { Warn("Couldn't read that image:\n" + ex.Message); }
    }

    /// <summary>Cap on what a URL may hand us. Box art is measured in megabytes; anything past this is
    /// a mistake or a trap, and reading it into memory first would be the expensive way to find out.</summary>
    private const int MaxDownloadBytes = 64 * 1024 * 1024;

    private async void LoadFromUrl()
    {
        string? url = PromptForUrl();
        if (string.IsNullOrWhiteSpace(url)) return;
        url = url.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
        { Warn("That doesn't look like an http(s) address."); return; }

        // OFF the UI thread: blocking on the fetch froze the window for as long as the server took
        // (up to the timeout), and the clicks made meanwhile queued up behind it — the same trap the
        // image viewer in EditGameWindowImages already documents.
        SetBusy(true);
        byte[]? bytes = null;
        string? error = null;
        try { bytes = await FetchCappedAsync(url); }
        catch (Exception ex) { error = ex.Message; }
        SetBusy(false);
        if (IsDisposed) return;
        if (error != null || bytes == null) { Warn("Download failed:\n" + (error ?? "no data")); return; }

        try
        {
            var img = LoadImage(bytes);
            // Extension from the URL when it carries one, else from what actually decoded.
            string ext = Path.GetExtension(uri.AbsolutePath);
            if (string.IsNullOrEmpty(ext) || ext.Length > 5) ext = ImageFormatExt(img);
            _files = new List<string>();
            SetSource(img, null, ext);
        }
        catch (Exception ex) { Warn("That address didn't return a readable image:\n" + ex.Message); }
    }

    /// <summary>GET with a hard ceiling: the declared length is refused up front when it is already too
    /// big, and the body is streamed so an undeclared one is cut off instead of being buffered whole.</summary>
    private static async System.Threading.Tasks.Task<byte[]> FetchCappedAsync(string url)
    {
        using var resp = await _http.GetAsync(url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        long? declared = resp.Content.Headers.ContentLength;
        if (declared is > MaxDownloadBytes)
            throw new InvalidOperationException($"The image is {declared / (1024 * 1024)} MB — over the {MaxDownloadBytes / (1024 * 1024)} MB limit.");

        using var stream = await resp.Content.ReadAsStreamAsync();
        using var ms = new MemoryStream(declared is > 0 and < MaxDownloadBytes ? (int)declared : 128 * 1024);
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            if (ms.Length + read > MaxDownloadBytes)
                throw new InvalidOperationException($"The image is over the {MaxDownloadBytes / (1024 * 1024)} MB limit.");
            ms.Write(buffer, 0, read);
        }
        return ms.ToArray();
    }

    /// <summary>Both source buttons off while a download runs — the window stays alive, but a second
    /// click must not start a race whose loser would overwrite the winner's image.</summary>
    private void SetBusy(bool busy)
    {
        UseWaitCursor = busy;
        if (_fromFile != null) _fromFile.Enabled = !busy;
        if (_fromUrl != null) _fromUrl.Enabled = !busy;
        if (busy) _sourceLabel.Text = "Downloading…";
    }

    private static Image LoadImage(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        using var probe = Image.FromStream(ms);
        return new Bitmap(probe);   // detach from the stream, which Image.FromStream keeps open
    }

    private static string ImageFormatExt(Image img)
        => img.RawFormat.Guid == ImageFormat.Jpeg.Guid ? ".jpg"
         : img.RawFormat.Guid == ImageFormat.Bmp.Guid ? ".bmp" : ".png";

    private void SetSource(Image? img, string? path, string ext)
    {
        var old = _source;
        _source = img;
        _sourcePath = path;
        _sourceExt = string.IsNullOrEmpty(ext) ? ".png" : ext.ToLowerInvariant();
        _crop.SetImage(img);
        old?.Dispose();

        bool has = img != null;
        _numW.Enabled = _numH.Enabled = _keepAspect.Enabled = has;
        _ok.Enabled = has || _files.Count > 0;
        if (has)
        {
            _sourceLabel.Text = (path != null ? Path.GetFileName(path) : "downloaded image")
                              + $"  ·  {img!.Width} × {img.Height}";
            OnCropChanged();
        }
        UpdateInfo();
    }

    // ── Size plumbing ─────────────────────────────────────────────────────────
    private void OnCropChanged()
    {
        // A new frame restates the natural output: its own pixel size. The user can still override it.
        var r = _crop.CropRectangle;
        if (r.Width <= 0 || r.Height <= 0) return;
        _syncing = true;
        _numW.Value = Math.Clamp(r.Width, (int)_numW.Minimum, (int)_numW.Maximum);
        _numH.Value = Math.Clamp(r.Height, (int)_numH.Minimum, (int)_numH.Maximum);
        _syncing = false;
        UpdateInfo();
    }

    private void SyncFrom(NumericUpDown edited)
    {
        var r = _crop.CropRectangle;
        if (r.Width <= 0 || r.Height <= 0) return;
        _syncing = true;
        try
        {
            if (edited == _numW)
            {
                int h = (int)Math.Round((double)_numW.Value * r.Height / r.Width);
                _numH.Value = Math.Clamp(h, (int)_numH.Minimum, (int)_numH.Maximum);
            }
            else
            {
                int w = (int)Math.Round((double)_numH.Value * r.Width / r.Height);
                _numW.Value = Math.Clamp(w, (int)_numW.Minimum, (int)_numW.Maximum);
            }
        }
        finally { _syncing = false; }
    }

    /// <summary>Ceiling on the output, in pixels. The spin boxes each go to 20000, and their PRODUCT is
    /// what gets allocated: 20000 × 20000 at 32bpp is 1.6 GB before the encoder even starts, so a
    /// mistyped digit — one is enough, since the locked ratio fills in the other side — could take the
    /// process down. 64 MP is far past any box scan (8000 × 8000) and safely under any allocation
    /// worth worrying about.</summary>
    private const long MaxOutputPixels = 64_000_000;

    private long OutputPixels => (long)_numW.Value * (long)_numH.Value;

    private void UpdateInfo()
    {
        if (_source == null)
        {
            _info.Text = _files.Count > 1 ? $"{_files.Count} files will be added unchanged." : "";
            _info.ForeColor = LiteBoxTheme.SubFg;
            return;
        }
        var r = _crop.CropRectangle;
        bool cropped = r.Width != _source.Width || r.Height != _source.Height;
        bool resized = (int)_numW.Value != r.Width || (int)_numH.Value != r.Height;
        string what = !cropped && !resized ? "saved unchanged"
                    : cropped && resized ? "cropped, then resized"
                    : cropped ? "cropped" : "resized";

        bool tooBig = OutputPixels > MaxOutputPixels;
        _ok.Enabled = !tooBig;
        _info.ForeColor = tooBig ? Color.FromArgb(235, 130, 120) : LiteBoxTheme.SubFg;
        _info.Text = tooBig
            ? $"{(int)_numW.Value} × {(int)_numH.Value} is {OutputPixels / 1_000_000} megapixels — over the {MaxOutputPixels / 1_000_000} MP limit. Lower one side."
            : $"Source {_source.Width} × {_source.Height}   ·   frame {r.Width} × {r.Height}"
              + $"   ·   file {(int)_numW.Value} × {(int)_numH.Value} ({what})";
    }

    // ── Result ────────────────────────────────────────────────────────────────
    private Result BuildResult()
    {
        string type = _cboType.SelectedItem as string ?? "";
        string region = _cboRegion.SelectedIndex >= 0 && _cboRegion.SelectedIndex < _regions.Count
                      ? _regions[_cboRegion.SelectedIndex] : "none";

        // Untouched single file, or a batch: hand back the paths and let the caller copy them — no
        // re-encode, so the bytes on disk stay bit-for-bit what the user chose.
        var r = _crop.CropRectangle;
        bool untouched = _source == null
                      || (r.Width == _source.Width && r.Height == _source.Height
                          && (int)_numW.Value == r.Width && (int)_numH.Value == r.Height);
        if (untouched && _files.Count > 0) return new Result(type, region, _files, null, _sourceExt);
        if (_source == null) return new Result(type, region, _files, null, _sourceExt);

        // The extension must name what the bytes ARE, not where they came from: the encoder only ever
        // writes JPEG or PNG, so a cropped .bmp (or a .gif/.webp URL) would otherwise land as PNG data
        // under a lying extension — readable to anything that sniffs, a puzzle to anything that does not.
        var bytes = Encode(r, out string ext);
        return new Result(type, region, Array.Empty<string>(), bytes, ext);
    }

    private byte[] Encode(Rectangle crop, out string ext)
    {
        int outW = (int)_numW.Value, outH = (int)_numH.Value;
        using var bmp = new Bitmap(outW, outH, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.DrawImage(_source!, new Rectangle(0, 0, outW, outH), crop, GraphicsUnit.Pixel);
        }
        using var ms = new MemoryStream();
        if (_sourceExt is ".jpg" or ".jpeg")
        {
            // Re-encoding a photo as PNG would triple its size for nothing; keep JPEG, at a quality
            // that survives one more generation.
            var enc = ImageCodecInfo.GetImageEncoders().FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);
            using var p = new EncoderParameters(1);
            p.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 92L);
            if (enc != null) bmp.Save(ms, enc, p); else bmp.Save(ms, ImageFormat.Jpeg);
            ext = ".jpg";
        }
        else { bmp.Save(ms, ImageFormat.Png); ext = ".png"; }
        return ms.ToArray();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    private void Warn(string msg) => MessageBox.Show(this, msg, "Add image", MessageBoxButtons.OK, MessageBoxIcon.Warning);

    private string? PromptForUrl()
    {
        using var f = new LiteBoxForm2("Image URL", S(460), S(150));
        var lbl = new Label { Text = "Paste the address of the image:", Location = new Point(S(14), S(14)), AutoSize = true, ForeColor = LiteBoxTheme.Fg, BackColor = LiteBoxTheme.Bg };
        var box = new TextBox { Location = new Point(S(14), S(40)), Width = S(420), BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg, BorderStyle = BorderStyle.FixedSingle };
        var ok = Btn("OK", 250, 78, 90); ok.BackColor = Color.FromArgb(50, 110, 65);
        var ko = Btn("Cancel", 350, 78, 90);
        ok.Click += (_, _) => { f.DialogResult = DialogResult.OK; f.Close(); };
        ko.Click += (_, _) => { f.DialogResult = DialogResult.Cancel; f.Close(); };
        f.Controls.Add(lbl); f.Controls.Add(box); f.Controls.Add(ok); f.Controls.Add(ko);
        f.AcceptButton = ok; f.CancelButton = ko;
        return f.ShowDialog(this) == DialogResult.OK ? box.Text : null;
    }

    /// <summary>Diagnostic (--addimage-probe): open the window on a local file, frame a middle chunk,
    /// and save a screenshot. A crop frame and a size row are paint and layout — they have to be seen.</summary>
    internal static void ProbeShot(string imagePath, string outPng, IReadOnlyList<string> types, IReadOnlyList<string> regions)
    {
        using var d = new AddImageDialog(types.Count > 0 ? types[0] : "Box - Front", "World", types, regions);
        d.StartPosition = FormStartPosition.Manual;
        d.Location = new Point(60, 60);
        d.Shown += (_, _) =>
        {
            try
            {
                if (File.Exists(imagePath))
                {
                    d._files = new List<string> { imagePath };
                    d.SetSource(LoadImage(File.ReadAllBytes(imagePath)), imagePath, Path.GetExtension(imagePath));
                    // Frame the middle half, so the shot shows a real crop rather than the full image.
                    var img = d._source!;
                    d._crop.SetFrame(new Rectangle(img.Width / 4, img.Height / 4, img.Width / 2, img.Height / 2));
                }
                Application.DoEvents();
                System.Threading.Thread.Sleep(400);
                Application.DoEvents();
                using var bmp = new Bitmap(d.Width, d.Height);
                d.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
                bmp.Save(outPng, ImageFormat.Png);
                Console.WriteLine("[addimg] wrote " + outPng);
            }
            catch (Exception ex) { Console.WriteLine("[addimg] " + ex.Message); }
            finally { d.Close(); }
        };
        d.ShowDialog();
    }

    /// <summary>Open the dialog. Null when the user cancelled or picked nothing.</summary>
    public static Result? Ask(IWin32Window owner, string defType, string defRegion,
                              IReadOnlyList<string> types, IReadOnlyList<string> regions)
    {
        using var d = new AddImageDialog(defType, defRegion, types, regions);
        while (true)
        {
            if (d.ShowDialog(owner) != DialogResult.OK) return null;
            try
            {
                var r = d.BuildResult();
                return r.Files.Count == 0 && r.Processed == null ? null : r;
            }
            catch (Exception ex)
            {
                // Encoding is where the memory actually goes, and a refusal there must not escape into
                // the message loop. The dialog reopens with its state intact so the size can be lowered
                // rather than the whole edit being lost.
                MessageBox.Show(owner as IWin32Window, "Couldn't produce that image:\n" + ex.Message
                    + "\n\nTry a smaller output size.", "Add image", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _source?.Dispose(); _source = null; }
        base.Dispose(disposing);
    }

    // ── The free crop frame ───────────────────────────────────────────────────
    /// <summary>The image, letterboxed into the control, with a movable/resizable frame drawn over it.
    /// Coordinates are kept in IMAGE pixels so the result never depends on how big the preview is.</summary>
    private sealed class CropView : Control
    {
        private Image? _img;
        private Rectangle _crop;        // image pixels
        private Rectangle _view;        // where the image is drawn, control pixels
        private int _drag = -1;         // -1 none, 0..7 handles, 8 = move
        private Point _dragStart;
        private Rectangle _cropStart;

        public event Action? CropChanged;

        public CropView()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                   | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = LiteBoxTheme.Panel2;
        }

        public Rectangle CropRectangle => _img == null ? Rectangle.Empty : _crop;

        public void SetImage(Image? img)
        {
            _img = img;
            _crop = img == null ? Rectangle.Empty : new Rectangle(0, 0, img.Width, img.Height);
            Recompute();
            Invalidate();
            CropChanged?.Invoke();
        }

        /// <summary>Set the frame explicitly, in image pixels (used by the probe).</summary>
        public void SetFrame(Rectangle r)
        {
            if (_img == null) return;
            _crop = Rectangle.Intersect(r, new Rectangle(0, 0, _img.Width, _img.Height));
            Invalidate();
            CropChanged?.Invoke();
        }

        public void ResetFrame()
        {
            if (_img == null) return;
            _crop = new Rectangle(0, 0, _img.Width, _img.Height);
            Invalidate();
            CropChanged?.Invoke();
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); Recompute(); Invalidate(); }

        private void Recompute()
        {
            if (_img == null || _img.Width <= 0 || _img.Height <= 0) { _view = Rectangle.Empty; return; }
            double sx = (double)(ClientSize.Width - 16) / _img.Width;
            double sy = (double)(ClientSize.Height - 16) / _img.Height;
            double s = Math.Min(sx, sy);
            if (s <= 0) { _view = Rectangle.Empty; return; }
            int w = Math.Max(1, (int)Math.Round(_img.Width * s)), h = Math.Max(1, (int)Math.Round(_img.Height * s));
            _view = new Rectangle((ClientSize.Width - w) / 2, (ClientSize.Height - h) / 2, w, h);
        }

        private Point ToImage(Point p)
        {
            if (_img == null || _view.Width == 0) return Point.Empty;
            double fx = (double)_img.Width / _view.Width, fy = (double)_img.Height / _view.Height;
            return new Point(
                (int)Math.Round((p.X - _view.X) * fx),
                (int)Math.Round((p.Y - _view.Y) * fy));
        }

        private Rectangle ToView(Rectangle r)
        {
            if (_img == null || _view.Width == 0) return Rectangle.Empty;
            double fx = (double)_view.Width / _img.Width, fy = (double)_view.Height / _img.Height;
            return new Rectangle(
                _view.X + (int)Math.Round(r.X * fx), _view.Y + (int)Math.Round(r.Y * fy),
                Math.Max(1, (int)Math.Round(r.Width * fx)), Math.Max(1, (int)Math.Round(r.Height * fy)));
        }

        private const int HandleSize = 8;

        private Rectangle[] Handles()
        {
            var v = ToView(_crop);
            int h = HandleSize, m = h / 2;
            int cx = v.X + v.Width / 2, cy = v.Y + v.Height / 2;
            return new[]
            {
                new Rectangle(v.Left - m, v.Top - m, h, h),      // 0 NW
                new Rectangle(cx - m, v.Top - m, h, h),          // 1 N
                new Rectangle(v.Right - m, v.Top - m, h, h),     // 2 NE
                new Rectangle(v.Right - m, cy - m, h, h),        // 3 E
                new Rectangle(v.Right - m, v.Bottom - m, h, h),  // 4 SE
                new Rectangle(cx - m, v.Bottom - m, h, h),       // 5 S
                new Rectangle(v.Left - m, v.Bottom - m, h, h),   // 6 SW
                new Rectangle(v.Left - m, cy - m, h, h),         // 7 W
            };
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (_img == null || e.Button != MouseButtons.Left) return;
            var hs = Handles();
            _drag = -1;
            for (int i = 0; i < hs.Length; i++) if (hs[i].Contains(e.Location)) { _drag = i; break; }
            if (_drag < 0 && ToView(_crop).Contains(e.Location)) _drag = 8;
            if (_drag >= 0) { _dragStart = e.Location; _cropStart = _crop; Capture = true; }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_img == null) return;
            if (_drag < 0)
            {
                var hs = Handles();
                Cursor c = Cursors.Default;
                for (int i = 0; i < hs.Length; i++)
                    if (hs[i].Contains(e.Location))
                    { c = i is 0 or 4 ? Cursors.SizeNWSE : i is 2 or 6 ? Cursors.SizeNESW : i is 1 or 5 ? Cursors.SizeNS : Cursors.SizeWE; break; }
                if (c == Cursors.Default && ToView(_crop).Contains(e.Location)) c = Cursors.SizeAll;
                Cursor = c;
                return;
            }

            var a = ToImage(_dragStart);
            var b = ToImage(e.Location);
            int dx = b.X - a.X, dy = b.Y - a.Y;
            var r = _cropStart;
            const int Min = 8;   // never let the frame collapse to nothing

            switch (_drag)
            {
                case 8: r.X += dx; r.Y += dy; break;
                case 0: r.X += dx; r.Y += dy; r.Width -= dx; r.Height -= dy; break;
                case 1: r.Y += dy; r.Height -= dy; break;
                case 2: r.Y += dy; r.Width += dx; r.Height -= dy; break;
                case 3: r.Width += dx; break;
                case 4: r.Width += dx; r.Height += dy; break;
                case 5: r.Height += dy; break;
                case 6: r.X += dx; r.Width -= dx; r.Height += dy; break;
                case 7: r.X += dx; r.Width -= dx; break;
            }
            if (r.Width < Min) { r.Width = Min; if (_drag is 0 or 6 or 7) r.X = _cropStart.Right - Min; }
            if (r.Height < Min) { r.Height = Min; if (_drag is 0 or 1 or 2) r.Y = _cropStart.Bottom - Min; }
            // Stay inside the image: moving slides, resizing clips.
            if (_drag == 8)
            {
                r.X = Math.Clamp(r.X, 0, Math.Max(0, _img.Width - r.Width));
                r.Y = Math.Clamp(r.Y, 0, Math.Max(0, _img.Height - r.Height));
            }
            else
            {
                int left = Math.Max(0, r.Left), top = Math.Max(0, r.Top);
                int right = Math.Min(_img.Width, r.Right), bottom = Math.Min(_img.Height, r.Bottom);
                r = Rectangle.FromLTRB(left, top, Math.Max(left + Min, right), Math.Max(top + Min, bottom));
            }
            if (r != _crop) { _crop = r; Invalidate(); CropChanged?.Invoke(); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_drag >= 0) { _drag = -1; Capture = false; }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            using (var bg = new SolidBrush(LiteBoxTheme.Panel2)) g.FillRectangle(bg, ClientRectangle);
            if (_img == null)
            {
                TextRenderer.DrawText(g, "No image", Font, ClientRectangle, LiteBoxTheme.SubFg,
                                      TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }
            if (_view.Width == 0) Recompute();
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(_img, _view);

            var v = ToView(_crop);
            // Everything outside the frame dimmed: what will be cut is visible, but visibly out.
            using (var shade = new SolidBrush(Color.FromArgb(150, 0, 0, 0)))
            {
                var outer = _view;
                g.FillRectangle(shade, outer.Left, outer.Top, outer.Width, v.Top - outer.Top);
                g.FillRectangle(shade, outer.Left, v.Bottom, outer.Width, outer.Bottom - v.Bottom);
                g.FillRectangle(shade, outer.Left, v.Top, v.Left - outer.Left, v.Height);
                g.FillRectangle(shade, v.Right, v.Top, outer.Right - v.Right, v.Height);
            }
            using (var pen = new Pen(LiteBoxTheme.Accent, 1.5f)) g.DrawRectangle(pen, v);
            using (var hb = new SolidBrush(LiteBoxTheme.Accent))
                foreach (var h in Handles()) g.FillRectangle(hb, h);
        }
    }

    /// <summary>A bare themed dialog for the URL prompt — LiteBoxForm with a fixed size.</summary>
    private sealed class LiteBoxForm2 : LiteBoxForm
    {
        public LiteBoxForm2(string title, int w, int h)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            ClientSize = new Size(w, h);
            StartPosition = FormStartPosition.CenterParent;
        }
    }
}
