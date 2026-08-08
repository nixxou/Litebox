// "Image Selection" tab of the Edit Game 3D Model Settings page: per texture slot (Front / Back / Spine /
// Clear Logo / Full Scan), a horizontal thumbnail list of the game's available images of that type with an
// "Auto" tile first — pick the exact file the 3D case uses instead of the automatic type→region→number pick.
//
// Rules (see Model3dImageStore): "full" is exclusive with front/spine/back (picking one side clears the
// other); a selection referencing a file that disappeared is dropped at load (the store keeps it raw, the
// resolution layer ignores the whole override until it's valid again). Selection changes fire
// SelectionChanged so the live 3D preview re-renders with the current (unsaved) picks.

#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using LbApiHost.Host.UiKit;

namespace LbApiHost.Host.Model3d;

internal sealed class Model3dImagesPanel : Panel
{
    private static Color Bg => LiteBoxTheme.Bg;
    private static Color Panel2 => LiteBoxTheme.Panel2;
    private static Color Fg => LiteBoxTheme.Fg;
    private static Color SubFg => LiteBoxTheme.SubFg;
    private static Color Accent => LiteBoxTheme.Accent;

    private readonly float _s;
    private int S(int px) => (int)Math.Round(px * _s);

    private readonly Unbroken.LaunchBox.Plugins.Data.IGame _game;
    private readonly bool _readOnly;
    private readonly Dictionary<string, string> _sel = new(StringComparer.OrdinalIgnoreCase);   // slot → path
    private readonly Dictionary<string, List<Tile>> _tiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Control> _rowControls = new();          // everything the Override checkbox gates
    private readonly CheckBox _override;
    private readonly List<Tile> _pendingThumbs = new();           // decoded once the handle exists

    /// <summary>Fired on every pick change (the composite page re-renders the 3D preview).</summary>
    public Action? SelectionChanged;

    /// <summary>The CURRENT (possibly unsaved) selection — what the live preview should render with.
    /// Null while Override is unchecked (default automatic behaviour).</summary>
    public Dictionary<string, string>? CurrentSelection
        => !_override.Checked || _sel.Count == 0 ? null : new Dictionary<string, string>(_sel, StringComparer.OrdinalIgnoreCase);

    /// <summary>Persist the selection as the game's LiteBox3dImage* fields (called by the page's apply).
    /// Override unchecked → fields cleared (back to the automatic pick).</summary>
    public void Apply() { if (!_readOnly) { Model3dImageStore.Write(_game, CurrentSelection); Model3dKeyIndex.DropGame(_game); } }

    private sealed class Tile : Panel
    {
        public string? Path;          // null = the Auto tile
        public PictureBox? Pic;
    }

    public Model3dImagesPanel(string platform, Unbroken.LaunchBox.Plugins.Data.IGame game, string title, float s, bool readOnly)
    {
        _s = s; _game = game; _readOnly = readOnly;
        string gameId = "";
        try { gameId = game.Id ?? ""; } catch { }
        BackColor = Bg;
        AutoScroll = true;

        // Available images per slot — the SAME enumeration as the Images pages, filtered by the type
        // chains the renderer resolves with.
        var bySlot = new Dictionary<string, List<(string path, string type, string region)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var slot in Model3dImageStore.Slots) bySlot[slot] = new();
        try
        {
            if (Guid.TryParse(gameId, out var guid))
            {
                var chains = new (string slot, string[] types)[]
                {
                    ("front", Media.MediaResolver.FrontChain()),
                    ("back", Media.MediaResolver.BackChain()),
                    ("spine", new[] { "Box - Spine" }),
                    ("logo", Media.MediaResolver.ClearLogo),
                    ("full", new[] { "Box - Full" }),
                };
                foreach (var (path, type, region) in Media.MediaResolver.AllImageFiles(platform, guid, title))
                    foreach (var (slot, types) in chains)
                        if (types.Contains(type, StringComparer.OrdinalIgnoreCase))
                            bySlot[slot].Add((path, type, region));
            }
        }
        catch (Exception ex) { Console.WriteLine("[model3d] image list: " + ex.Message); }

        // Stored selection, minus picks whose file vanished or is no longer in the game's image set
        // (mirrors the resolution layer's invalidation; the raw fields are only rewritten on Apply).
        var stored = Model3dImageStore.Read(game);
        if (stored != null)
            foreach (var (slot, path) in stored)
                if (bySlot.TryGetValue(slot, out var list) && list.Any(e => string.Equals(e.path, path, StringComparison.OrdinalIgnoreCase)))
                    _sel[slot] = path;

        // Master gate — mirrors the Settings tab's "Override Default Settings": unchecked = automatic picks
        // (the rows grey out and nothing is persisted); checking re-enables the stored/current selection.
        _override = new CheckBox
        {
            Text = "Override Image Selection", AutoSize = true, Location = new Point(S(10), S(8)),
            ForeColor = Fg, BackColor = Bg, Checked = _sel.Count > 0, Enabled = !readOnly,
        };
        _override.CheckedChanged += (_, _) => { UpdateEnabled(); SelectionChanged?.Invoke(); };
        Controls.Add(_override);

        var head = new Label
        {
            Text = "Force the exact image each face of the 3D case uses (Auto = LaunchBox's type → region → number pick). "
                 + "Picking a Full Scan replaces Front / Spine / Back. If a chosen file disappears, the whole selection "
                 + "is ignored until it is valid again.",
            AutoSize = false, Size = new Size(S(660), S(34)), Location = new Point(S(10), S(32)),
            ForeColor = SubFg, BackColor = Bg, Font = new Font("Segoe UI", 8.25f),
        };
        Controls.Add(head);

        int y = S(72);
        var rows = new (string slot, string label)[]
        { ("front", "Box Front"), ("back", "Box Back"), ("spine", "Box Spine"), ("logo", "Clear Logo"), ("full", "Box Full Scan") };
        foreach (var (slot, label) in rows)
            y = BuildRow(slot, label, bySlot[slot], y);
        UpdateEnabled();
    }

    private void UpdateEnabled()
    {
        bool on = _override.Checked && !_readOnly;
        foreach (var c in _rowControls) c.Enabled = on;
    }

    private int BuildRow(string slot, string label, List<(string path, string type, string region)> images, int y)
    {
        var rowLbl = new Label
        {
            Text = label + (images.Count == 0 ? "   (no image of this type)" : ""),
            AutoSize = true, Location = new Point(S(10), y), ForeColor = Fg, BackColor = Bg,
            Font = new Font("Segoe UI Semibold", 9f),
        };
        Controls.Add(rowLbl);
        _rowControls.Add(rowLbl);
        y += S(24);

        var flow = new FlowLayoutPanel
        {
            Location = new Point(S(10), y), Size = new Size(S(680), S(168)),
            BackColor = Bg, WrapContents = false, AutoScroll = true,
            FlowDirection = FlowDirection.LeftToRight,
        };
        Controls.Add(flow);
        _rowControls.Add(flow);
        var tiles = new List<Tile>();
        _tiles[slot] = tiles;

        Tile MakeTile(string? path, string caption)
        {
            var t = new Tile
            {
                Path = path, Size = new Size(S(112), S(152)), Margin = new Padding(0, 0, S(8), 0),
                BackColor = Panel2, Cursor = _readOnly ? Cursors.Default : Cursors.Hand,
            };
            var pic = new PictureBox
            {
                Location = new Point(S(4), S(4)), Size = new Size(S(104), S(122)),
                SizeMode = PictureBoxSizeMode.Zoom, BackColor = Panel2,
                Cursor = t.Cursor,
            };
            t.Pic = pic;
            var cap = new Label
            {
                Text = caption, AutoSize = false, Size = new Size(S(104), S(20)), Location = new Point(S(4), S(128)),
                ForeColor = SubFg, BackColor = Panel2, Font = new Font("Segoe UI", 7.5f),
                TextAlign = ContentAlignment.MiddleCenter, AutoEllipsis = true, Cursor = t.Cursor,
            };
            if (path == null)
            {
                var auto = new Label
                {
                    Text = "Auto", Dock = DockStyle.None, AutoSize = false,
                    Location = pic.Location, Size = pic.Size,
                    ForeColor = Fg, BackColor = Panel2, Font = new Font("Segoe UI Semibold", 10f),
                    TextAlign = ContentAlignment.MiddleCenter, Cursor = t.Cursor,
                };
                t.Controls.Add(auto);
                if (!_readOnly) auto.Click += (_, _) => Pick(slot, null);
            }
            else t.Controls.Add(pic);
            t.Controls.Add(cap);
            if (!_readOnly)
            {
                t.Click += (_, _) => Pick(slot, path);
                pic.Click += (_, _) => Pick(slot, path);
                cap.Click += (_, _) => Pick(slot, path);
            }
            flow.Controls.Add(t);
            tiles.Add(t);
            return t;
        }

        MakeTile(null, "automatic pick");
        foreach (var (path, _, region) in images)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            int dash = name.LastIndexOf('-');
            string num = dash > 0 && int.TryParse(name.Substring(dash + 1), out var n) ? "#" + n.ToString("00") : "";
            MakeTile(path, (region.Length > 0 ? region : "root") + (num.Length > 0 ? "  " + num : ""));
        }
        PaintRow(slot);
        // Thumbs decode once the panel HAS a handle — it starts hidden behind the Settings tab, and the
        // BeginInvoke hand-off needs one (loading at ctor time silently dropped every thumb).
        _pendingThumbs.AddRange(tiles.Where(t => t.Path != null));
        if (IsHandleCreated) KickThumbs();
        return y + S(176);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        KickThumbs();
    }

    private void KickThumbs()
    {
        if (_pendingThumbs.Count == 0) return;
        var batch = _pendingThumbs.ToList();
        _pendingThumbs.Clear();
        LoadThumbsAsync(batch);
    }

    private void Pick(string slot, string? path)
    {
        if (path == null) _sel.Remove(slot);
        else
        {
            _sel[slot] = path;
            // Exclusivity: a full scan replaces the three per-face scans, and vice-versa.
            if (slot == "full") { _sel.Remove("front"); _sel.Remove("spine"); _sel.Remove("back"); }
            else if (slot is "front" or "spine" or "back") _sel.Remove("full");
        }
        foreach (var s in _tiles.Keys) PaintRow(s);
        SelectionChanged?.Invoke();
    }

    private void PaintRow(string slot)
    {
        if (!_tiles.TryGetValue(slot, out var tiles)) return;
        string? cur = _sel.TryGetValue(slot, out var p) ? p : null;
        foreach (var t in tiles)
        {
            bool on = string.Equals(t.Path, cur, StringComparison.OrdinalIgnoreCase) || (t.Path == null && cur == null);
            t.BackColor = on ? Accent : Panel2;
        }
    }

    // Thumbnails decoded in the background (full files can be multi-MB scans) — bytes → Image so no file
    // lock is kept; each tile gets a small pre-scaled bitmap, the original is disposed immediately.
    private void LoadThumbsAsync(List<Tile> tiles)
    {
        System.Threading.Tasks.Task.Run(() =>
        {
            foreach (var t in tiles)
            {
                if (IsDisposed) return;
                Image? thumb = null;
                try
                {
                    using var ms = new MemoryStream(File.ReadAllBytes(t.Path!));
                    using var full = Image.FromStream(ms);
                    double k = Math.Min((double)S(104) / full.Width, (double)S(122) / full.Height);
                    int w = Math.Max(1, (int)(full.Width * k)), h = Math.Max(1, (int)(full.Height * k));
                    var bmp = new Bitmap(w, h);
                    using (var gr = Graphics.FromImage(bmp))
                    {
                        gr.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        gr.DrawImage(full, 0, 0, w, h);
                    }
                    thumb = bmp;
                }
                catch { }
                if (thumb == null) continue;
                try
                {
                    if (IsDisposed || !IsHandleCreated) { thumb.Dispose(); return; }
                    BeginInvoke((Action)(() =>
                    {
                        if (t.IsDisposed || t.Pic == null) { thumb.Dispose(); return; }
                        t.Pic.Image = thumb;
                    }));
                }
                catch { thumb.Dispose(); }
            }
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            foreach (var tiles in _tiles.Values)
                foreach (var t in tiles)
                    try { t.Pic?.Image?.Dispose(); } catch { }
        base.Dispose(disposing);
    }
}
