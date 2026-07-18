// Modal card for a Related-games entry — the desktop counterpart of the web theme's cloud modal.
//
// Opened by a click on a Related card (RelatedGamesPanel.OpenCard) whenever the game can be described:
// screenshot (async), title, platform · year line, a compact fact line (dev/pub, genres, ESRB/rating)
// and the resolved overview, then a link row:
//   • "View in library" when the game is OWNED (navigates via MainWindow.SelectGameById and closes);
//   • one button per RELATED SITE — from the Extended-DB row's source ids (ScreenscraperId / VNDBID /
//     SteamId / IgdbSlug) when available, always including the id-range site (ExtendDbLinks);
//   • "ExtendDB Web" when the Web module is up and the active DB covers the id (local database site).
// Esc (or the Close button) cancels.
//
// Data comes from the ACTIVE site DB via DbRepository.GetGameById — extended when the Base module has
// it as main DB, else the native LaunchBox DB (LaunchBox-range ids only). A null row degrades to the
// card's own seed data (title/sub/description), never blocks the modal.

#nullable disable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading.Tasks;
using System.Windows.Forms;
using LbApiHost.Host.Media;
using LbApiHost.Host.UiKit;
using LbApiHost.Host.Web;

namespace LbApiHost.Host.Similar;

internal sealed class RelatedGameModal : Form
{
    private readonly int _dbId;
    private readonly string _seedDesc;
    private readonly bool _owned;
    private readonly string _gameId;
    private readonly Action<string> _openLocal;

    private readonly ShotPanel _shot;
    private readonly Label _title, _sub, _fiche;
    private readonly TextBox _overview;
    private readonly FlowLayoutPanel _links;
    private readonly Button _close;
    private bool _dead;

    public RelatedGameModal(int dbId, string title, string sub, string desc,
                            bool owned, string gameId, Action<string> openLocal)
    {
        _dbId = dbId; _seedDesc = desc ?? ""; _owned = owned; _gameId = gameId ?? ""; _openLocal = openLocal;

        Text = string.IsNullOrEmpty(title) ? "Game" : title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false; ShowIcon = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = LiteBoxTheme.Bg;
        ClientSize = new Size(640, 560);
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };

        _shot = new ShotPanel { Bounds = new Rectangle(12, 12, 616, 280), BackColor = LiteBoxTheme.Bg };
        Controls.Add(_shot);

        _title = new Label
        {
            Text = title ?? "", AutoEllipsis = true, ForeColor = LiteBoxTheme.Fg, BackColor = LiteBoxTheme.Bg,
            Font = new Font("Segoe UI Semibold", 13f), Bounds = new Rectangle(12, 300, 616, 26),
        };
        Controls.Add(_title);

        _sub = new Label
        {
            Text = sub ?? "", AutoEllipsis = true, ForeColor = LiteBoxTheme.SubFg, BackColor = LiteBoxTheme.Bg,
            Font = new Font("Segoe UI", 9f), Bounds = new Rectangle(12, 328, 616, 17),
        };
        Controls.Add(_sub);

        _fiche = new Label
        {
            Text = "", ForeColor = LiteBoxTheme.SubFg, BackColor = LiteBoxTheme.Bg,
            Font = new Font("Segoe UI", 8.5f), Bounds = new Rectangle(12, 347, 616, 32),
        };
        Controls.Add(_fiche);

        _overview = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None,
            BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg, Font = new Font("Segoe UI", 9f),
            Bounds = new Rectangle(12, 384, 616, 124),
            Text = _seedDesc.Replace("\n", "\r\n"),
        };
        Controls.Add(_overview);

        _links = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoScroll = false,
            Bounds = new Rectangle(8, 516, 624, 36), BackColor = LiteBoxTheme.Bg, Padding = new Padding(0),
        };
        Controls.Add(_links);

        _close = MakeButton("Close");
        _close.Click += (_, _) => Close();
        CancelButton = _close;   // Esc

        BuildLinks(null);   // seed row: library button + range site + local web + Close
        Shown += (_, _) => LoadAsync();
        FormClosed += (_, _) => _dead = true;
    }

    // ── Async fill (DB row + screenshot) ────────────────────────────────────────

    private void LoadAsync()
    {
        int dbId = _dbId;
        Task.Run(() =>
        {
            DbGame row = null;
            try { if (dbId > 0) row = new DbRepository().GetGameById(dbId); } catch { }

            // The USER-PRIORITY overview (same SQL resolution as the Related cards: the precomputed
            // defaultOverview column, else the dynamic COALESCE over [Base] OverviewSources). Extended
            // DB only — on a native-DB row the PickOverview fallback below serves the plain Overview.
            string priorityOverview = null;
            try
            {
                if (dbId > 0 && RelatedProvider.Overviews(dbId.ToString(), null)
                        is { } ov && ov.TryGetValue(dbId.ToString(), out var v))
                    priorityOverview = v;
            }
            catch { }

            Image img = null;
            try
            {
                if (dbId > 0)
                {
                    var pick = PickShot(MetadataDb.ImagesForGame(dbId));
                    if (pick != null)
                    {
                        var bytes = MediaFetch.FetchBytes(pick.Value, platform: null);
                        if (bytes != null && bytes.Length > 0) img = DecodeScaled(bytes, 1232, 560);
                    }
                }
            }
            catch { }

            try
            {
                BeginInvoke(new Action(() =>
                {
                    if (_dead) { img?.Dispose(); return; }
                    Apply(row, priorityOverview, img);
                }));
            }
            catch { img?.Dispose(); }
        });
    }

    private void Apply(DbGame row, string priorityOverview, Image img)
    {
        if (img != null) _shot.SetImage(img);
        if (row != null)
        {
            if (!string.IsNullOrWhiteSpace(row.Name)) { _title.Text = row.Name; Text = row.Name; }

            string year = row.ReleaseYear is > 0 ? row.ReleaseYear.ToString() : "";
            string subLine = Join(" · ", row.Platform, year);
            if (subLine.Length > 0) _sub.Text = subLine;

            string rating = row.CommunityRating is > 0
                ? $"★ {row.CommunityRating:0.0}" + (row.CommunityRatingCount > 0 ? $" ({row.CommunityRatingCount})" : "")
                : "";
            _fiche.Text = Join("\n",
                Join(" · ", Join(" / ", row.Developer, row.Publisher), row.ESRB, rating),
                row.Genres);

            // User-priority resolution first (matches the cards); PickOverview is only the fallback
            // for rows the extended-DB query can't serve (native LaunchBox rows).
            var overview = !string.IsNullOrWhiteSpace(priorityOverview) ? priorityOverview : row.PickOverview();
            if (!string.IsNullOrWhiteSpace(overview)) _overview.Text = overview.Replace("\n", "\r\n");
        }
        else if (!string.IsNullOrWhiteSpace(priorityOverview))
        {
            _overview.Text = priorityOverview.Replace("\n", "\r\n");
        }
        BuildLinks(row);
    }

    /// <summary>Screenshot-first pick for the modal's hero image; cover as the fallback.</summary>
    private static MetadataDb.WebImage? PickShot(List<MetadataDb.WebImage> imgs)
    {
        if (imgs == null || imgs.Count == 0) return null;
        string[] pref = { "Screenshot - Gameplay", "Screenshot - Game Title", "Fanart - Background" };
        foreach (var p in pref)
            foreach (var w in imgs)
                if (string.Equals(w.Type, p, StringComparison.OrdinalIgnoreCase)) return w;
        return MediaProxy.PickCover(imgs);
    }

    // ── Link row ────────────────────────────────────────────────────────────────

    private void BuildLinks(DbGame row)
    {
        _links.SuspendLayout();
        _links.Controls.Clear();

        if (_owned && _gameId.Length > 0 && _openLocal != null)
        {
            var lib = MakeButton("View in library");
            lib.ForeColor = Color.White; lib.BackColor = LiteBoxTheme.Accent;
            lib.Click += (_, _) => { try { _openLocal(_gameId); } catch { } Close(); };
            _links.Controls.Add(lib);
        }

        var links = new List<(string Name, string Url)>();
        void Add(string name, string url)
        {
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(url)) return;
            foreach (var l in links) if (l.Name == name) return;
            links.Add((name, url));
        }

        // Sites from the row's source ids (extended DB), then the id-range site as the constant floor.
        if (row != null)
        {
            if (row.ScreenscraperId is > 0) Add("ScreenScraper", $"https://screenscraper.fr/gameinfos.php?gameid={row.ScreenscraperId}");
            if (row.VNDBID is > 0) Add("VNDB", $"https://vndb.org/v{row.VNDBID}");
            var steam = row.SteamId is > 0 ? row.SteamId : row.SteamAppId;
            if (steam is > 0) Add("Steam", $"https://store.steampowered.com/app/{steam}");
            if (!string.IsNullOrEmpty(row.IgdbSlug)) Add("IGDB", $"https://www.igdb.com/games/{row.IgdbSlug}");
        }
        Add(ExtendDbLinks.SiteName(_dbId), ExtendDbLinks.ExternalUrl(_dbId));
        if (ExtendDbLinks.LocalWebDbCanServe(_dbId)) Add("ExtendDB Web", ExtendDbLinks.LocalWebDbUrl(_dbId));

        foreach (var (name, url) in links)
        {
            var b = MakeButton(name);
            string u = url;
            b.Click += (_, _) => OpenUrl(u);
            _links.Controls.Add(b);
        }

        _links.Controls.Add(_close);
        _links.ResumeLayout();
    }

    private static Button MakeButton(string text) => new()
    {
        Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
        FlatStyle = FlatStyle.Flat, ForeColor = LiteBoxTheme.Fg, BackColor = LiteBoxTheme.Panel2,
        Font = new Font("Segoe UI", 9f), Padding = new Padding(8, 4, 8, 4), Margin = new Padding(4, 2, 4, 2),
    };

    private static void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            { FileName = url, UseShellExecute = true });
        }
        catch { }
    }

    private static string Join(string sep, params string[] parts)
    {
        var outp = new List<string>();
        foreach (var p in parts) if (!string.IsNullOrWhiteSpace(p)) outp.Add(p.Trim());
        return string.Join(sep, outp);
    }

    private static Image DecodeScaled(byte[] bytes, int maxW, int maxH)
    {
        using var ms = new System.IO.MemoryStream(bytes);
        using var src = Image.FromStream(ms);
        double k = Math.Min((double)maxW / src.Width, (double)maxH / src.Height);
        if (k >= 1) return new Bitmap(src);
        var bmp = new Bitmap(Math.Max(1, (int)(src.Width * k)), Math.Max(1, (int)(src.Height * k)));
        using var g = Graphics.FromImage(bmp);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(src, 0, 0, bmp.Width, bmp.Height);
        return bmp;
    }

    /// <summary>Letterboxed hero image with a dark placeholder while loading.</summary>
    private sealed class ShotPanel : Panel
    {
        private Image _img;

        public ShotPanel()
        {
            DoubleBuffered = true; ResizeRedraw = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        }

        public void SetImage(Image img) { _img?.Dispose(); _img = img; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            using (var bg = new SolidBrush(Color.FromArgb(24, 24, 26))) g.FillRectangle(bg, ClientRectangle);
            if (_img == null) return;
            double k = Math.Min((double)ClientSize.Width / _img.Width, (double)ClientSize.Height / _img.Height);
            int w = Math.Max(1, (int)(_img.Width * k)), h = Math.Max(1, (int)(_img.Height * k));
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(_img, (ClientSize.Width - w) / 2, (ClientSize.Height - h) / 2, w, h);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _img?.Dispose(); _img = null; }
            base.Dispose(disposing);
        }
    }
}
