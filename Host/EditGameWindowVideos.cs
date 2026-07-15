// Edit Game → Media → Videos. The same shape as the image editor, because LaunchBox's own Videos page (a
// Type/Path table) tells you nothing about what a video actually IS. Here every video is a THUMBNAIL — a frame
// taken 20% in (see VideoThumbnailer; the first frame is nearly always a logo or a black fade).
//
// ONE page for the lot, grouped by LaunchBox video TYPE (SDK VideoTypes: Video Snap, Trailer, Theme Video,
// Recording, Marquee — each a sub-folder of <LB>\Videos\<Platform>\, "Video Snap" being the root). No per-type
// tree nodes: a game has a handful of videos, so sub-nodes would only add clicks.
//
// Left-click plays IN LiteBox (VideoPlayerDialog / libvlc), right-click opens the menu (Play, Move To Type,
// Info, Show in Explorer, Delete). Thumbnails are decoded lazily on a background worker and cached on disk, so
// a video is decoded once ever (~42 ms) and the page opens instantly afterwards. libvlc itself is created on
// first use and released while a game runs — a LiteBox that never opens this page pays nothing.

#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Unbroken.LaunchBox.Plugins.Data;
using LbApiHost.Host.Media;
using LbApiHost.Host.Video;
using LbApiHost.Host.Integrations;

namespace LbApiHost.Host;

internal sealed partial class EditGameWindow
{
    private readonly struct VidFile
    {
        public readonly string Path, Type;
        public readonly int NumVal;
        public readonly bool HasGuid;
        public VidFile(string p, string t, int n, bool g) { Path = p; Type = t; NumVal = n; HasGuid = g; }
    }

    // The decoded frames, OWNED here and shared by every video page (a video shows on its type page AND on the
    // "Videos" parent). Pages must therefore only ever DETACH them (VidDetachPics), never dispose them; they
    // are freed once, on close.
    private readonly Dictionary<string, Image?> _vidThumbs = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _vidLoading = new(StringComparer.OrdinalIgnoreCase);

    private int VidCellW => S(200);
    /// <summary>Thumbnail + three caption lines: file name, #num/size, then the ADS provenance.</summary>
    private int VidCellH => S(186);
    private int VidThumbH => S(120);

    /// <summary>The five LaunchBox video types, in SDK order.</summary>
    private static IReadOnlyList<string> VidTypes()
        => MediaResolver.VideoTypeDirs.Select(v => v.Type).ToList();

    // ── Page ──────────────────────────────────────────────────────────────────
    // ONE page for every type (the tree has no per-type children): a game has a handful of videos, and they're
    // already grouped by type here. The type still exists on disk (a folder) and in the "Move To Type" menu.
    private Control BuildVideosPage()
    {
        var container = new Panel { BackColor = Bg, Dock = DockStyle.Fill };
        var host = new Panel { BackColor = Bg, AutoScroll = true, Dock = DockStyle.Fill };

        var bar = new Panel { Dock = DockStyle.Top, Height = S(40), BackColor = Bg };
        var add = DlgBtn("＋ Add Video…", Color.FromArgb(45, 95, 60));
        add.AutoSize = false; add.SetBounds(S(4), S(6), S(120), S(28)); add.Enabled = !_readOnly;
        add.Click += (_, _) => VidAdd(null);
        bar.Controls.Add(add);

        // Source toggles — filled-when-on chips (see SourceChip), laid out left-to-right after the Add button.
        // Purple = the offline database (where video rows live; LaunchBox's own Metadata.db has none), blue =
        // EmuMovies (live, user's account), green = Steam (live, appid games), red = YouTube (yt-dlp, any game).
        int chipX = S(134);
        void AddChip(CheckBox c, int w) { c.SetBounds(chipX, S(8), w, S(26)); bar.Controls.Add(c); chipX += w + S(10); }

        if (VidWebAvailable)
            AddChip(SourceChip("Web (database)", WebPurple, _vidShowWeb, on =>
                { _vidShowWeb = on; VidSourceToggle("web", on); VidPopulate(host, null); }), S(158));
        if (VidEmuAvailable(ImgGame))
            AddChip(SourceChip("EmuMovies", EmuBlue, _vidShowEmu, on =>
                { _vidShowEmu = on; VidSourceToggle("emu", on); VidPopulate(host, null); }), S(124));
        if (VidSteamAvailable(ImgGame))
            AddChip(SourceChip("Steam", SteamGreen, _vidShowSteam, on =>
                { _vidShowSteam = on; VidSourceToggle("steam", on); VidPopulate(host, null); }), S(100));
        AddChip(SourceChip("YouTube", YtRed, _vidShowYt, on =>
            { _vidShowYt = on; VidPopulate(host, null); }), S(112));

        if (!VlcService.Available)
        {
            var warn = new Label
            {
                Text = "⚠  libvlc isn't installed — videos are listed, but without thumbnails.",
                ForeColor = Color.FromArgb(235, 180, 100), BackColor = Bg, AutoSize = true,
                Font = new Font("Segoe UI", 8.5f), Location = new Point(S(912), S(12)),
            };
            bar.Controls.Add(warn);
        }

        container.Controls.Add(host);   // Fill first …
        container.Controls.Add(bar);    // … Top last
        VidPopulate(host, null);
        return container;
    }

    private Panel? _vidHost;   // the video scroll host currently on screen (tree page OR the MvOpenCell modal)

    private void VidPopulate(Panel host, string? onlyType)
    {
        _vidHost = host;   // remember it so async completions re-populate THIS host (the modal, when open), not the tree page
        foreach (Control c in host.Controls) VidDetachPics(c);   // the frames belong to _vidThumbs — detach, don't dispose
        host.Controls.Clear();

        var g = ImgGame;
        var all = VidScan(g);
        var types = onlyType != null ? new List<string> { onlyType } : VidTypes().ToList();
        bool web = VidWebAvailable && _vidShowWeb;
        bool emu = _vidShowEmu && VidEmuAvailable(g);
        bool steam = _vidShowSteam && VidSteamAvailable(g);

        if (!all.Any(v => types.Contains(v.Type, StringComparer.OrdinalIgnoreCase)) && !web && !emu && !steam && !_vidShowYt)
        {
            host.Controls.Add(new Label
            {
                Text = onlyType != null ? $"No {onlyType} video for this game" : "This game has no video",
                Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = SubFg, BackColor = Bg,
                Font = new Font("Segoe UI", 11f, FontStyle.Italic),
            });
            return;
        }

        var inner = new Panel { BackColor = Bg, Location = Point.Empty, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowOnly };
        host.Controls.Add(inner);
        int y = S(10);

        foreach (var type in types)
        {
            var vids = all.Where(v => string.Equals(v.Type, type, StringComparison.OrdinalIgnoreCase))
                          .OrderBy(v => v.NumVal).ToList();
            if (vids.Count == 0) continue;

            var th = new Label
            {
                Text = $"━━  {type}", ForeColor = Fg, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                AutoSize = false, BackColor = Bg,
            };
            th.SetBounds(S(12), y, S(600), S(26));
            inner.Controls.Add(th);
            y += S(30);

            int x = S(12);
            foreach (var v in vids)
            {
                var cell = VidCell(v);
                cell.Location = new Point(x, y);
                inner.Controls.Add(cell);
                x += VidCellW;
            }
            y += VidCellH + S(8);
        }

        VidAppendMergedWeb(g, all, inner, ref y);
        if (_vidShowYt) VidAppendYouTube(g, inner, ref y);
    }

    // Track the order the user turns web video sources on, so the merged view interleaves them by check-order
    // (like the multi-select grid / the images page). "web" = database (purple) · "emu" (blue) · "steam" (green).
    private readonly List<string> _vidSourceOrder = new();
    private void VidSourceToggle(string src, bool on)
    {
        if (on) { if (!_vidSourceOrder.Contains(src)) _vidSourceOrder.Add(src); }
        else _vidSourceOrder.Remove(src);
    }

    private void VidRebuildIfCurrent()
    {
        // Re-populate the ACTIVE video host — the MvOpenCell modal's host when one is open, else the tree page's.
        // (ShowPage("Videos") rebuilt the TREE page, which in multi-select is the MATRIX, so a modal's async fetch
        // completion never refreshed the modal — it stayed on "Querying…".)
        if (_vidHost != null && !_vidHost.IsDisposed && _vidHost.IsHandleCreated)
        {
            try { VidPopulate(_vidHost, null); return; } catch { }
        }
        if (_tree.SelectedNode?.Tag?.ToString() == "Videos") { _pages.Remove("Videos"); ShowPage("Videos"); }
    }

    // ── Merged "videos you don't own": database + EmuMovies + Steam in ONE flow — no per-source section headers,
    // the tile border colour tells the origin (purple/blue/green). Sources interleave in the order the user
    // checked them (_vidSourceOrder). Grouped by video type only when more than one is present (Video / VideoAdvert).
    // Async sources show a compact "Querying…" note and rebuild the page when their fetch lands.
    private void VidAppendMergedWeb(IGame g, List<VidFile> local, Panel inner, ref int y)
    {
        bool webOn   = VidWebAvailable && _vidShowWeb;
        bool emuOn   = _vidShowEmu && VidEmuAvailable(g);
        bool steamOn = _vidShowSteam && VidSteamAvailable(g);
        if (!webOn && !emuOn && !steamOn) return;

        bool On(string s) => (s == "web" && webOn) || (s == "emu" && emuOn) || (s == "steam" && steamOn);
        var order = _vidSourceOrder.Where(On).ToList();
        foreach (var s in new[] { "web", "emu", "steam" }) if (On(s) && !order.Contains(s)) order.Add(s);

        int dbId = Safe(() => g.LaunchBoxDbId) ?? -1;
        string emuKey = Safe(() => g.Id) ?? Safe(() => g.Title) ?? "";
        string steamKey = "steam:" + (Safe(() => g.Id) ?? Safe(() => g.Title) ?? "");

        var entries = new List<(string type, int rank, Panel cell)>();
        var loading = new List<string>();

        for (int rank = 0; rank < order.Count; rank++)
        {
            switch (order[rank])
            {
                case "web":
                    foreach (var w in VidWebCandidates(dbId, local))
                        entries.Add((string.IsNullOrEmpty(w.Type) ? "Video" : w.Type, rank, VidWebCell(w)));
                    break;

                case "emu":
                    if (!_vidEmuCache.TryGetValue(emuKey, out var em)) { VidTriggerEmuFetch(g, emuKey); loading.Add("EmuMovies"); }
                    else if (em == null) loading.Add("EmuMovies");
                    else
                    {
                        var owned = BuildEmuOwned(local.Select(v => v.Path));
                        foreach (var m in em.Where(m => !EmuOwns(owned, m.Crc, m.FileSize)))
                            entries.Add((string.IsNullOrEmpty(m.LbType) ? "Video" : m.LbType, rank, VidEmuCell(m)));
                    }
                    break;

                case "steam":
                    if (!_vidSteamCache.TryGetValue(steamKey, out var st)) { VidTriggerSteamFetch(g, dbId, steamKey); loading.Add("Steam"); }
                    else if (st == null) loading.Add("Steam");
                    else
                        foreach (var w in st)
                            entries.Add((string.IsNullOrEmpty(w.Type) ? "Video" : w.Type, rank, VidSteamCell(w)));
                    break;
            }
        }

        if (entries.Count == 0 && loading.Count == 0) return;

        y += S(12);
        var hdr = new Label
        {
            Text = "⬇  Videos you don't own — left-click to stream, right-click to download  (border colour = source)",
            ForeColor = SubFg, Font = new Font("Segoe UI", 9f, FontStyle.Italic), AutoSize = false, BackColor = Bg,
        };
        hdr.SetBounds(S(12), y, S(800), S(24)); inner.Controls.Add(hdr); y += S(30);

        // Wrap tiles to the visible width so a merged (multi-source) row never forces horizontal scrolling.
        int hostW = inner.Parent?.ClientSize.Width ?? 0;
        int avail = (hostW > S(200) ? hostW : S(1100)) - S(12) - S(24);
        int cols = Math.Max(1, avail / VidCellW);

        var typeOrder = entries.Select(e => e.type).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t.Equals("Video", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();
        bool showTypeHeaders = typeOrder.Count > 1;   // "Video" only ⇒ a plain grid; add headers when adverts too

        foreach (var type in typeOrder)
        {
            var cells = entries.Where(e => string.Equals(e.type, type, StringComparison.OrdinalIgnoreCase))
                               .OrderBy(e => e.rank).Select(e => e.cell).ToList();   // OrderBy is stable → same-source order kept
            if (cells.Count == 0) continue;
            if (showTypeHeaders)
            {
                var th = new Label { Text = $"━━  {type}", ForeColor = Fg, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), AutoSize = false, BackColor = Bg };
                th.SetBounds(S(12), y, S(600), S(26)); inner.Controls.Add(th); y += S(30);
            }
            int x = S(12), col = 0;
            foreach (var cell in cells)
            {
                if (col == cols) { col = 0; x = S(12); y += VidCellH + S(8); }
                cell.Location = new Point(x, y); inner.Controls.Add(cell); x += VidCellW; col++;
            }
            y += VidCellH + S(8);
        }

        if (loading.Count > 0)
        {
            inner.Controls.Add(new Label { Text = "Querying " + string.Join(", ", loading) + "…", ForeColor = SubFg, BackColor = Bg, AutoSize = true, Font = new Font("Segoe UI", 9f, FontStyle.Italic), Location = new Point(S(16), y) });
            y += S(26);
        }
    }

    /// <summary>Database web-video stand-ins for this game, filtered against owned (ADS CRC, then byte size).</summary>
    private List<MetadataDb.WebImage> VidWebCandidates(int dbId, List<VidFile> local)
    {
        if (dbId <= 0) return new();
        List<MetadataDb.WebImage> cands;
        try { cands = MetadataDb.VideosForGame(dbId); } catch { cands = new List<MetadataDb.WebImage>(); }
        if (cands.Count == 0) return new();
        var ownedCrc = new HashSet<uint>();
        var ownedSize = new HashSet<long>();
        foreach (var v in local)
        {
            var s = FileMetaStore.Read(v.Path, FileMetaStore.StreamCrc32);
            if (!string.IsNullOrEmpty(s) && long.TryParse(s, out var c) && c != 0) ownedCrc.Add(unchecked((uint)c));
            try { ownedSize.Add(new FileInfo(v.Path).Length); } catch { }
        }
        return cands.Where(w => !ownedCrc.Contains(unchecked((uint)w.Crc32))
                             && !(w.FileSize > 0 && ownedSize.Contains(w.FileSize))).ToList();
    }

    private void VidTriggerEmuFetch(IGame g, string idKey)
    {
        _vidEmuCache[idKey] = null;   // in-flight sentinel — a rebuild before the fetch lands won't re-trigger
        string plat = Safe(() => g.Platform) ?? "";
        string romPath = Safe(() => g.ApplicationPath) ?? "";
        string title = Safe(() => g.Title) ?? "";
        System.Threading.Tasks.Task.Run(async () =>
        {
            List<EmuMoviesCatalog.EmuMedia> found = new();
            try { var api = EmuApi(); if (api != null) found = await EmuMoviesCatalog.ResolveForGameAsync(api, title, romPath, plat); }
            catch { }
            found = found.Where(m => m.LbType == "Video" || m.LbType == "VideoAdvert").ToList();
            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke(new Action(() => { _vidEmuCache[idKey] = found; VidRebuildIfCurrent(); }));
            }
            catch { }
        });
    }

    private void VidTriggerSteamFetch(IGame g, int dbId, string idKey)
    {
        _vidSteamCache[idKey] = null;
        string appPath = Safe(() => g.ApplicationPath) ?? "";
        System.Threading.Tasks.Task.Run(async () =>
        {
            List<MetadataDb.WebImage> found = new();
            try { found = (await SteamCatalog.ResolveForGameAsync(dbId, appPath)).Where(w => string.Equals(w.Type, "Video", StringComparison.OrdinalIgnoreCase)).ToList(); } catch { }
            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke(new Action(() => { _vidSteamCache[idKey] = found; VidRebuildIfCurrent(); }));
            }
            catch { }
        });
    }

    // The ADS :info origin of an owned media file (null when never stamped / no ExtendDB reader).
    private static string? VidAdsOrigin(string path)
    {
        try { return ImageInfoBridge.ReadAny(path) is ImageInfo i ? i.Origin : null; }
        catch { return null; }
    }

    // Border colour by the SOURCE we got the file from: live Steam / EmuMovies downloads are stamped "lb-steam" /
    // "lb-emumovies" (distinct from the DB's own "steam" / "emumovies" copies, which — like every other DB origin
    // — read as the database). null = never stamped (hand-added) → no border.
    private static Color? VidSourceColor(string? origin)
    {
        if (string.IsNullOrEmpty(origin)) return null;
        if (origin.Equals("lb-steam", StringComparison.OrdinalIgnoreCase)) return SteamGreen;
        if (origin.Equals("lb-emumovies", StringComparison.OrdinalIgnoreCase)) return EmuBlue;
        if (origin.Equals("youtube", StringComparison.OrdinalIgnoreCase)) return YtRed;
        return Color.FromArgb(150, 90, 200);   // any web-database origin → purple
    }

    private Panel VidCell(VidFile v)
    {
        var cell = new Panel { Size = new Size(VidCellW, VidCellH), BackColor = Bg };

        var pic = new PictureBox
        {
            SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(18, 18, 24), Cursor = Cursors.Hand,
            Tag = v.Path,   // VidStore paints the frame into every box waiting for this video
        };
        pic.SetBounds(S(4), S(4), VidCellW - S(12), VidThumbH);
        VidLoadThumb(pic, v.Path);

        pic.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Right) { VidMenu(v).Show(pic, e.Location); return; }
            if (e.Button == MouseButtons.Left) VidPlay(v.Path);   // left = play in the default player
        };
        cell.Controls.Add(pic);

        // DOTTED source-coloured border on OWNED videos (green = your Steam · blue = EmuMovies · red = YouTube ·
        // purple = database), so you see where each came from. Dotted (vs the SOLID stand-in borders) = owned.
        if (VidSourceColor(VidAdsOrigin(v.Path)) is Color srcColor)
            cell.Paint += (_, e) =>
            {
                using var pen = new Pen(srcColor, S(2)) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot };
                e.Graphics.DrawRectangle(pen, S(2), S(2), VidCellW - S(8), VidThumbH + S(4));
            };

        var name = new Label
        {
            Text = Path.GetFileName(v.Path), ForeColor = SubFg, BackColor = Bg, Font = new Font("Segoe UI", 8f),
            AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true,
        };
        name.SetBounds(S(4), VidThumbH + S(8), VidCellW - S(12), S(18));
        cell.Controls.Add(name);

        string size = "";
        try { size = $"{new FileInfo(v.Path).Length / (1024.0 * 1024.0):0.#} MB"; } catch { }
        var meta = new Label
        {
            Text = $"#{v.NumVal}" + (v.HasGuid ? "  [G]" : "") + (size.Length > 0 ? "   " + size : ""),
            ForeColor = Color.FromArgb(120, 126, 142), BackColor = Bg, Font = new Font("Segoe UI", 8f),
            AutoSize = false, TextAlign = ContentAlignment.MiddleLeft,
        };
        meta.SetBounds(S(4), VidThumbH + S(26), VidCellW - S(12), S(16));
        cell.Controls.Add(meta);

        // Where this video came from, per its ADS :info (empty when it was never stamped).
        cell.Controls.Add(ProvLabel(v.Path, S(4), VidThumbH + S(44), VidCellW - S(12)));

        return cell;
    }

    /// <summary>Decode the frame on a worker (it's ~42 ms the first time, then it comes from the disk cache).
    /// The result lands in the CACHE, not on this particular box — a page rebuilt mid-decode (an add, a delete)
    /// would otherwise strand the frame on a dead PictureBox and never show it again.</summary>
    private void VidLoadThumb(PictureBox pic, string path)
    {
        if (_vidThumbs.TryGetValue(path, out var have)) { pic.Image = have; return; }
        if (!VlcService.Available) return;
        if (!_vidLoading.Add(path)) return;   // already decoding — VidStore will paint this box too

        System.Threading.Tasks.Task.Run(() =>
        {
            Image? thumb = null;
            try { thumb = VideoThumbnailer.Get(path); } catch { }
            try
            {
                if (IsDisposed || !IsHandleCreated) { thumb?.Dispose(); return; }
                BeginInvoke(new Action(() => VidStore(path, thumb)));
            }
            catch { thumb?.Dispose(); }
        });
    }

    // UI thread: keep the frame (the cache owns it) and paint it into every box still waiting for this video —
    // the same one can be on the type page and on the "Videos" parent.
    private void VidStore(string path, Image? thumb)
    {
        if (IsDisposed) { thumb?.Dispose(); return; }
        _vidThumbs[path] = thumb;
        _vidLoading.Remove(path);
        if (thumb == null) return;

        void Paint(Control c)
        {
            if (c is PictureBox pb && pb.Image == null && string.Equals(pb.Tag as string, path, StringComparison.OrdinalIgnoreCase))
                pb.Image = thumb;
            foreach (Control ch in c.Controls) Paint(ch);
        }
        try { Paint(_host); } catch { }
    }

    // ── Context menu ──────────────────────────────────────────────────────────
    private ContextMenuStrip VidMenu(VidFile v)
    {
        var m = ThemedMenu();
        m.Items.Add(new ToolStripMenuItem("▶  Play").WithClick(() => VidPlay(v.Path)));
        m.Items.Add(new ToolStripSeparator());

        var move = new ToolStripMenuItem("Move To Type");
        foreach (var t in VidTypes())
        {
            string tt = t;
            move.DropDownItems.Add(new ToolStripMenuItem(tt) { Checked = string.Equals(tt, v.Type, StringComparison.OrdinalIgnoreCase) }
                .WithClick(() => VidTransfer(v, tt)));
        }
        m.Items.Add(move);

        var copy = new ToolStripMenuItem("Copy To Type");
        foreach (var t in VidTypes()) { string tt = t; copy.DropDownItems.Add(new ToolStripMenuItem(tt).WithClick(() => VidCopyType(v, tt))); }
        m.Items.Add(copy);

        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(new ToolStripMenuItem("🗑  Delete all except this").WithClick(() => VidDeleteAllExcept(v)));
        m.Items.Add(new ToolStripMenuItem("Set Number…").WithClick(() => VidSetNumber(v)));
        m.Items.Add(new ToolStripMenuItem(v.HasGuid ? "Remove GUID naming" : "Enable GUID naming").WithClick(() => VidToggleGuid(v)));
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(new ToolStripMenuItem("ℹ  Info").WithClick(() => VidInfo(v)));
        m.Items.Add(new ToolStripMenuItem("📂  Show in Explorer").WithClick(() => VidReveal(v.Path)));
        m.Items.Add(new ToolStripMenuItem("🗑  Delete Video").WithClick(() => VidDelete(v)));
        return m;
    }

    /// <summary>Plays in OUR window (libvlc), never in the system's player: a foreign app popping over a modal
    /// edit window and stealing focus is not something we want to invite in. The player can also TRIM the file
    /// (keyframe cut, no re-encode) — when it did, the cached thumbnail and the page are stale.</summary>
    private void VidPlay(string path)
    {
        if (!VideoPlayerDialog.Play(this, path)) return;
        _vidThumbs.Remove(path);   // NOT disposed: a page still on screen may be holding it (freed on close)
        VidAfterOp(Safe(() => ImgGame.Platform) ?? "");
    }

    private void VidInfo(VidFile v)
    {
        string size = "?";
        try { size = $"{new FileInfo(v.Path).Length / (1024.0 * 1024.0):0.##} MB"; } catch { }

        var p = VideoProbe.Get(v.Path);
        string dur = p != null ? VideoProbe.Duration(p.Value.DurationMs) : "?";
        string dims = (p != null && p.Value.Width > 0) ? $"{p.Value.Width} × {p.Value.Height}" : "?";
        string fps = (p != null && p.Value.Fps > 0) ? $"{p.Value.Fps:0.##} fps" : "?";
        string codec = (p != null && p.Value.Codec.Length > 0) ? p.Value.Codec : "?";

        string text =
            $"Type:  {v.Type}\nNumber:  #{v.NumVal}\nGUID naming:  {(v.HasGuid ? "Yes" : "No")}\n" +
            $"Duration:  {dur}\nResolution:  {dims}\nFrame rate:  {fps}\nCodec:  {codec}\nSize:  {size}\n";

        // Same ADS block as the image Info: the ExtendDB-format provenance, read through ExtendDB's own reader
        // when it's loaded. Videos we download later will carry it; a hand-copied file simply shows "(none)".
        string crc32 = FileMetaStore.Read(v.Path, FileMetaStore.StreamCrc32);
        var info = ImageInfoBridge.ReadAny(v.Path);
        text += "\n── ADS metadata " + (ImageInfoBridge.Available ? "(via ExtendDB reader)" : "(native)") + " ──\n";
        text += $"CRC32 (:crc32):  {(string.IsNullOrEmpty(crc32) ? "(none)" : crc32)}\n";
        if (info is ImageInfo i)
        {
            text +=
                $"Origin:  {(string.IsNullOrEmpty(i.Origin) ? "(none)" : i.Origin)}\n" +
                $"Database Id:  {i.DatabaseId}\n" +
                $"CRC32 (:info):  {i.Crc32}\n" +
                $"Duplicate:  {i.Duplicate}\n" +
                $"File type:  {(string.IsNullOrEmpty(i.FileType) ? "(none)" : i.FileType)}\n" +
                $"File size:  {i.FileSize}\n" +
                $"Source:  {(string.IsNullOrEmpty(i.OriginalUrl) ? "(none)" : i.OriginalUrl)}\n";
        }
        else text += "(:info):  (none)\n";

        text += $"\n{v.Path}";
        MessageBox.Show(this, text, "Video info", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void VidReveal(string path)
    {
        try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\""); } catch { }
    }

    // ── Operations ────────────────────────────────────────────────────────────
    private void VidAdd(string? presetType)
    {
        if (_readOnly) return;
        var g = ImgGame;
        string plat = Safe(() => g.Platform) ?? "";
        string idStr = Safe(() => g.Id) ?? "";
        if (string.IsNullOrEmpty(plat) || string.IsNullOrEmpty(idStr))
        { MessageBox.Show(this, "This game has no platform / id.", "LiteBox", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }

        // Pick the type FIRST (like the images Add), then the file(s).
        string? type = presetType ?? VidPickType("Video Snap");
        if (type == null) return;

        string filter = "Videos (" + string.Join(";", MediaResolver.VideoExtensions.Select(e => "*" + e)) + ")|"
                      + string.Join(";", MediaResolver.VideoExtensions.Select(e => "*" + e));
        using var ofd = new OpenFileDialog { Title = "Add video(s)", Filter = filter, CheckFileExists = true, Multiselect = true };
        if (ofd.ShowDialog(this) != DialogResult.OK) return;

        // Copied, never referenced — like images: LaunchBox finds media by CONVENTION (folder + name), so a
        // file left outside the Videos tree would simply never be seen.
        int ok = 0;
        foreach (var file in ofd.FileNames) if (VidPlace(g, file, type, move: false)) ok++;
        if (ok > 0) VidAfterOp(plat);
    }

    private void VidTransfer(VidFile v, string targetType)
    {
        if (_readOnly || string.Equals(v.Type, targetType, StringComparison.OrdinalIgnoreCase)) return;
        var g = ImgGame;
        if (VidPlace(g, v.Path, targetType, move: true)) VidAfterOp(Safe(() => g.Platform) ?? "");
    }

    /// <summary>Copy/move a video into a type's folder under the LaunchBox naming convention
    /// ({sani}[.{guid}]-{NN}.ext) — the same rules the image editor uses.</summary>
    private bool VidPlace(IGame g, string source, string targetType, bool move)
    {
        try
        {
            string plat = Safe(() => g.Platform) ?? "";
            string idStr = Safe(() => g.Id) ?? "";
            string sani = MediaResolver.Sanitize(Safe(() => g.Title) ?? "");
            string? dir = MediaResolver.VideoTypeFolder(plat, targetType);
            if (string.IsNullOrEmpty(dir)) return false;

            Directory.CreateDirectory(dir);
            string prefix = ImgPrefix(plat, idStr, sani, move ? source : null, dir);
            int num = ImgMaxNum(dir, prefix) + 1;
            string target = Path.Combine(dir, $"{prefix}-{num:D2}{Path.GetExtension(source)}");

            if (move) File.Move(source, target, overwrite: false);
            else File.Copy(source, target, overwrite: false);
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, (move ? "Move" : "Add") + " failed:\n" + ex.Message, "LiteBox", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    private void VidDelete(VidFile v)
    {
        if (_readOnly) return;
        if (MessageBox.Show(this, $"Delete this video from disk?\n\n{v.Type}\n{Path.GetFileName(v.Path)}\n\nThis cannot be undone.",
                "Delete Video", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        try { File.Delete(v.Path); }
        catch (Exception ex) { MessageBox.Show(this, "Delete failed:\n" + ex.Message, "LiteBox", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        _vidThumbs.Remove(v.Path);
        VidAfterOp(Safe(() => ImgGame.Platform) ?? "");
    }

    private void VidCopyType(VidFile v, string targetType)
    {
        if (_readOnly) return;
        var g = ImgGame;
        if (VidPlace(g, v.Path, targetType, move: false)) VidAfterOp(Safe(() => g.Platform) ?? "");
    }

    private void VidDeleteAllExcept(VidFile keep)
    {
        if (_readOnly) return;
        var victims = VidScan(ImgGame).Where(f => !string.Equals(f.Path, keep.Path, StringComparison.OrdinalIgnoreCase)).ToList();
        if (victims.Count == 0) return;
        if (MessageBox.Show(this, $"This will delete {victims.Count} video(s).\n\nThis cannot be undone.",
                "Delete all except this", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        int fail = 0;
        foreach (var v in victims) { try { File.Delete(v.Path); _vidThumbs.Remove(v.Path); } catch { fail++; } }
        VidAfterOp(Safe(() => ImgGame.Platform) ?? "");
        if (fail > 0) MessageBox.Show(this, $"{fail} file(s) couldn't be deleted (locked / in use).", "LiteBox", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private void VidToggleGuid(VidFile v)
    {
        if (_readOnly) return;
        var g = ImgGame;
        string idStr = Safe(() => g.Id) ?? "";
        string sani = MediaResolver.Sanitize(Safe(() => g.Title) ?? "");
        string dir = Path.GetDirectoryName(v.Path) ?? "";
        string ext = Path.GetExtension(v.Path);
        try
        {
            string prefix = v.HasGuid ? sani : $"{sani}.{idStr}";   // toggled form
            int num = ImgMaxNum(dir, prefix) + 1;
            string target = Path.Combine(dir, $"{prefix}-{num:D2}{ext}");
            File.Move(v.Path, target, overwrite: false);
            _vidThumbs.Remove(v.Path);
            VidAfterOp(Safe(() => g.Platform) ?? "");
        }
        catch (Exception ex) { MessageBox.Show(this, "Toggle GUID failed:\n" + ex.Message, "LiteBox", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    // Drop the in-memory thumbnails for every file in a directory — the frames are keyed by PATH, so after a
    // rename/renumber cascade a path now points at a DIFFERENT video and its cached frame is stale. (The disk
    // cache is keyed by path+size+mtime, so a re-decode gets the right frame.)
    private void VidForgetThumbsIn(string dir)
    {
        if (string.IsNullOrEmpty(dir)) return;
        var stale = _vidThumbs.Keys.Where(k => string.Equals(Path.GetDirectoryName(k) ?? "", dir, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var k in stale) _vidThumbs.Remove(k);
    }

    private void VidSetNumber(VidFile v)
    {
        if (_readOnly) return;
        int? target = ImgPromptNumber(v.NumVal);
        if (target == null) return;
        var g = ImgGame;
        string idStr = Safe(() => g.Id) ?? "";
        string sani = MediaResolver.Sanitize(Safe(() => g.Title) ?? "");
        string dir = Path.GetDirectoryName(v.Path) ?? "";
        string ext = Path.GetExtension(v.Path);
        string prefix = v.HasGuid ? $"{sani}.{idStr}" : sani;
        string prefixLower = prefix.ToLowerInvariant();
        try
        {
            string temp = v.Path + ".litebox-tmp";
            File.Move(v.Path, temp, overwrite: true);
            // Cascade: bump every file whose number >= target, highest first (avoid clobber).
            var toShift = new List<(string path, int num)>();
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                string name = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
                if (!name.StartsWith(prefixLower)) continue;
                int d = name.LastIndexOf('-'); if (d < 0 || d >= name.Length - 1) continue;
                if (int.TryParse(name.Substring(d + 1), out int n) && n >= target.Value) toShift.Add((f, n));
            }
            toShift.Sort((a, b) => b.num.CompareTo(a.num));
            foreach (var (path, num) in toShift)
            {
                string fe = Path.GetExtension(path);
                string on = Path.GetFileNameWithoutExtension(path);
                int d = on.LastIndexOf('-');
                string np = Path.Combine(dir, $"{on.Substring(0, d)}-{(num + 1):D2}{fe}");
                if (path != np) File.Move(path, np, overwrite: true);
            }
            File.Move(temp, Path.Combine(dir, $"{prefix}-{target:D2}{ext}"), overwrite: true);
            VidForgetThumbsIn(dir);   // the cascade renamed several files → the path-keyed memory thumbs are now stale
            VidAfterOp(Safe(() => g.Platform) ?? "");
        }
        catch (Exception ex) { MessageBox.Show(this, "Set number failed:\n" + ex.Message, "LiteBox", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private string? VidPickType(string def)
    {
        using var f = NewDialog("Video type", 420, 160);
        var lbl = new Label { Text = "Add as video type:", Location = new Point(S(14), S(16)), AutoSize = true, ForeColor = Fg, BackColor = Bg };
        var cbo = new ComboBox { Location = new Point(S(14), S(42)), Width = S(380), DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Field, ForeColor = Fg, FlatStyle = FlatStyle.Flat };
        foreach (var t in VidTypes()) cbo.Items.Add(t);
        int di = cbo.Items.IndexOf(def); cbo.SelectedIndex = di >= 0 ? di : 0;
        string? chosen = null;
        DialogButtons(f, out var ok, out var cancel);
        ok.Click += (_, _) => { chosen = cbo.SelectedItem as string; f.DialogResult = DialogResult.OK; f.Close(); };
        cancel.Click += (_, _) => { f.DialogResult = DialogResult.Cancel; f.Close(); };
        f.Controls.Add(lbl); f.Controls.Add(cbo);
        return f.ShowDialog(this) == DialogResult.OK ? chosen : null;
    }

    /// <summary>Rebuild the video page from the TREE — never from a remembered panel: the pages are cached in
    /// _pages, so a held reference goes stale as soon as you navigate away and back (which is why an added
    /// video only showed up after reopening the window).</summary>
    private void VidAfterOp(string plat)
    {
        if (!string.IsNullOrEmpty(plat)) _imgTouchedPlatforms.Add(plat);   // same deferred cache rebuild as images

        // Refresh the ACTIVE video host — the MvOpenCell modal when one is open (so a download/delete shows at once),
        // else the tree page. ShowPage alone rebuilt the tree page, which in multi-select is the matrix, so a
        // download/delete inside the modal never updated the modal's local-file list.
        if (_vidHost != null && !_vidHost.IsDisposed && _vidHost.IsHandleCreated)
        {
            try { VidPopulate(_vidHost, null); return; } catch { }
        }
        _pages.Remove("Videos");
        if (_tree.SelectedNode?.Tag?.ToString() == "Videos") ShowPage("Videos");
    }

    /// <summary>Unhook the thumbnails from a page's PictureBoxes WITHOUT disposing them (unlike ImgDisposePics):
    /// the same Image instance is shown by several pages and lives in _vidThumbs until the window closes.</summary>
    private static void VidDetachPics(Control c)
    {
        if (c is PictureBox pb) pb.Image = null;
        foreach (Control ch in c.Controls) VidDetachPics(ch);
    }

    /// <summary>Free the decoded frames (called from OnFormClosed).</summary>
    private void VidDisposeThumbs()
    {
        foreach (var im in _vidThumbs.Values) { try { im?.Dispose(); } catch { } }
        _vidThumbs.Clear();
    }

    // ── Web videos (from ExtendDB's extended database) ────────────────────────
    //
    // The rows live in the extended DB's GameImages table under Type 'Video' / 'VideoAdvert' — 146k of them,
    // from screenscraper, steam and emumovies. LaunchBox's own Metadata.db has none, which is why we read the
    // extended DB directly (exactly like ExtendDB's own video downloader does).
    //
    // OWNED detection follows ExtendDB's video rule, NOT the image one: the CRC of a local video is read from
    // its ADS and NEVER computed (these files are tens to hundreds of MB — ExtendMediaDownloader.BuildVideoInventory
    // is explicit about it), so the fallback key is the FILE SIZE, which the extended DB always carries.
    //
    // PLAYING vs DOWNLOADING are two different paths, on purpose:
    //   • Play      → ExtendDB's per-origin URL CHAIN (MediaApiBridge.ListUrls) handed to libvlc. This is what
    //                 makes the fake Steam mp4s work: 5,943 rows have a FileName ending in ".m3u8.mp4", and the
    //                 real resource is the HLS manifest you get by dropping the ".mp4" — VLC plays it natively.
    //   • Download  → MediaApi.FetchForWizard, the exact primitive the metadata wizard uses (credential
    //                 injection, mirror fallback, Steam retry). It SKIPS HLS URLs — raw manifest bytes are
    //                 useless on disk — so an HLS-only Steam trailer falls through to the extenddb mirror, and
    //                 if the mirror hasn't got it either, the video stays stream-only. We say so plainly.

    private bool _vidShowWeb;

    private static bool VidWebAvailable => MediaApiBridge.UseWizardPath && MetadataDb.ExtendedDbPath != null;

    private Panel VidWebCell(MetadataDb.WebImage w)
    {
        var cell = new Panel { Size = new Size(VidCellW, VidCellH), BackColor = Bg };

        var frame = new Panel { BackColor = Color.FromArgb(150, 90, 200), Padding = new Padding(S(2)) };   // purple = not owned
        frame.SetBounds(S(4), S(4), VidCellW - S(12), VidThumbH);
        var pic = new PictureBox
        {
            Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(18, 18, 24),
            Cursor = Cursors.Hand,
        };
        VidLoadThumbWeb(pic, w);
        var wi = w;
        pic.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Right) { VidWebMenu(wi).Show(pic, e.Location); return; }
            if (e.Button == MouseButtons.Left) VidPlayWeb(wi);
        };
        frame.Controls.Add(pic);
        cell.Controls.Add(frame);

        var name = new Label
        {
            Text = "web · " + w.Origin, ForeColor = Color.FromArgb(190, 150, 230), BackColor = Bg,
            Font = new Font("Segoe UI", 8f), AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true,
        };
        name.SetBounds(S(4), VidThumbH + S(8), VidCellW - S(12), S(18));
        cell.Controls.Add(name);

        string size = w.FileSize > 0 ? $"{w.FileSize / (1024.0 * 1024.0):0.#} MB" : "";
        bool hls = VidIsHlsRow(w);
        var meta = new Label
        {
            Text = (string.IsNullOrEmpty(w.Region) ? "World" : w.Region) + (size.Length > 0 ? "   " + size : "") + (hls ? "   HLS" : ""),
            ForeColor = Color.FromArgb(120, 126, 142), BackColor = Bg, Font = new Font("Segoe UI", 8f),
            AutoSize = false, TextAlign = ContentAlignment.MiddleLeft,
        };
        meta.SetBounds(S(4), VidThumbH + S(26), VidCellW - S(12), S(16));
        cell.Controls.Add(meta);

        return cell;
    }

    /// <summary>A Steam row whose FileName is a FAKE mp4: "…/hls_264_master.m3u8.mp4". The real resource is the
    /// HLS manifest under the stripped name — a plain GET can't save it, but yt-dlp CAN (VidDownloadHls).</summary>
    private static bool VidIsHlsRow(MetadataDb.WebImage w)
        => (w.FileName ?? "").EndsWith(".m3u8.mp4", StringComparison.OrdinalIgnoreCase)
        || (w.FileName ?? "").EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);

    /// <summary>The real HLS manifest URL — strips the fake trailing ".mp4" that Steam rows carry.</summary>
    private static string VidHlsUrl(MetadataDb.WebImage w)
    {
        var fn = w.FileName ?? "";
        return fn.EndsWith(".m3u8.mp4", StringComparison.OrdinalIgnoreCase) ? fn.Substring(0, fn.Length - 4) : fn;
    }

    /// <summary>Save an HLS video (a Steam .m3u8 trailer) via yt-dlp — it downloads the manifest and merges the
    /// streams with ffmpeg. Returns true on success. yt-dlp must be installed. Writes the ExtendDB ADS.</summary>
    private bool VidDownloadHls(IGame g, MetadataDb.WebImage w, string targetType)
    {
        if (!YtDlp.Available) return false;
        try
        {
            string plat = Safe(() => g.Platform) ?? "";
            string idStr = Safe(() => g.Id) ?? "";
            int dbId = Safe(() => g.LaunchBoxDbId) ?? -1;
            if (string.IsNullOrEmpty(plat) || string.IsNullOrEmpty(idStr)) return false;
            string? dir = MediaResolver.VideoTypeFolder(plat, targetType);
            if (string.IsNullOrEmpty(dir)) return false;
            Directory.CreateDirectory(dir);
            string sani = MediaResolver.Sanitize(Safe(() => g.Title) ?? "");
            string prefix = ImgPrefix(plat, idStr, sani, null, dir);
            int num = ImgMaxNum(dir, prefix) + 1;
            string outNoExt = Path.Combine(dir, $"{prefix}-{num:D2}");
            var outcome = YtDlp.DownloadAsync(VidHlsUrl(w), "Best", outNoExt, YtDlp.CookieBrowser.None).GetAwaiter().GetResult();
            if (outcome.Path == null || !File.Exists(outcome.Path)) return false;
            long fs = 0; try { fs = new FileInfo(outcome.Path).Length; } catch { }
            ImageAdsWriter.WriteForDownload(outcome.Path, new MetadataDb.WebImage(dbId, w.FileName, w.Type, w.Region, w.Crc32, w.Origin, w.Duplicate, "mp4", fs), dbId, plat);
            return true;
        }
        catch { return false; }
    }

    private ContextMenuStrip VidWebMenu(MetadataDb.WebImage w)
    {
        var m = ThemedMenu();
        m.Items.Add(new ToolStripMenuItem("▶  Play (stream)").WithClick(() => VidPlayWeb(w)));
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(new ToolStripMenuItem("⬇  Download").WithClick(() => VidDownloadWeb(w, "Video Snap")));
        var asType = new ToolStripMenuItem("⬇  Download As");
        foreach (var t in VidTypes()) { string tt = t; asType.DropDownItems.Add(new ToolStripMenuItem(tt).WithClick(() => VidDownloadWeb(w, tt))); }
        m.Items.Add(asType);
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(new ToolStripMenuItem("ℹ  Info").WithClick(() => VidWebInfo(w)));
        return m;
    }

    /// <summary>Decode a frame from the UPSTREAM (no download): libvlc range-requests its way to the 20% mark.</summary>
    private void VidLoadThumbWeb(PictureBox pic, MetadataDb.WebImage w)
    {
        if (!VlcService.Available) return;
        var srcs = MediaApiBridge.ListUrls(w);
        if (srcs.Count == 0) return;

        System.Threading.Tasks.Task.Run(() =>
        {
            Image? thumb = null;
            foreach (var s in srcs)
            {
                try { thumb = VideoThumbnailer.GetFromUrl(s.Url, s.Referer, w.Crc32.ToString()); } catch { }
                if (thumb != null) break;
            }
            if (thumb == null) return;
            try
            {
                if (pic.IsDisposed || !pic.IsHandleCreated) { thumb.Dispose(); return; }
                pic.BeginInvoke(new Action(() =>
                {
                    if (pic.IsDisposed) { thumb.Dispose(); return; }
                    var old = pic.Image; pic.Image = thumb; old?.Dispose();
                }));
            }
            catch { thumb.Dispose(); }
        });
    }

    private void VidPlayWeb(MetadataDb.WebImage w)
    {
        var srcs = MediaApiBridge.ListUrls(w)
            .Select(c => new VideoPlayerDialog.Source(c.Url, c.Referer)).ToList();
        VideoPlayerDialog.PlayWeb(this, $"{w.Type} · {w.Origin}", srcs);
    }

    /// <summary>Download through ExtendDB's wizard fetcher — the same chain, credentials and mirror fallback the
    /// metadata wizard uses — then name the file the LaunchBox way and stamp the ExtendDB-format ADS.</summary>
    private void VidDownloadWeb(MetadataDb.WebImage w, string targetType)
    {
        if (_readOnly) return;
        var g = ImgGame;
        string plat = Safe(() => g.Platform) ?? "";
        string idStr = Safe(() => g.Id) ?? "";
        int dbId = Safe(() => g.LaunchBoxDbId) ?? -1;
        if (string.IsNullOrEmpty(plat) || string.IsNullOrEmpty(idStr)) return;

        // A Steam HLS trailer (".m3u8") has no plain bytes — hand it to yt-dlp (manifest + ffmpeg merge).
        if (VidIsHlsRow(w))
        {
            UseWaitCursor = true;
            bool okHls; try { okHls = VidDownloadHls(g, w, targetType); } finally { UseWaitCursor = false; }
            if (okHls) VidAfterOp(plat);
            else MessageBox.Show(this, YtDlp.Available
                    ? "The HLS download failed."
                    : "This is an HLS stream (a Steam trailer). Install yt-dlp (a game's YouTube options) to save it.",
                "LiteBox", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        byte[]? bytes = null;
        UseWaitCursor = true;
        try { bytes = MediaApiBridge.FetchBytes(w, plat); }
        finally { UseWaitCursor = false; }

        // An HLS manifest is not a video file. FetchForWizard already skips HLS URLs, but if a mirror ever hands
        // one back we must not write a 200-byte ".mp4" full of text.
        bool manifest = bytes != null && bytes.Length > 7
                        && System.Text.Encoding.ASCII.GetString(bytes, 0, 7) == "#EXTM3U";

        if (bytes == null || bytes.Length == 0 || manifest)
        {
            MessageBox.Show(this, "The download failed: no source in the chain returned this video.",
                "LiteBox", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            string ext = ImageFileType.Extract(w.FileName);
            if (string.IsNullOrEmpty(ext)) ext = "mp4";
            string? dir = MediaResolver.VideoTypeFolder(plat, targetType);
            if (string.IsNullOrEmpty(dir)) return;
            Directory.CreateDirectory(dir);

            string sani = MediaResolver.Sanitize(Safe(() => g.Title) ?? "");
            string prefix = ImgPrefix(plat, idStr, sani, null, dir);
            int num = ImgMaxNum(dir, prefix) + 1;
            string target = Path.Combine(dir, $"{prefix}-{num:D2}.{ext.TrimStart('.')}");

            File.WriteAllBytes(target, bytes);
            ImageAdsWriter.WriteForDownload(target, w, dbId, plat);   // :crc32 + :info, ExtendDB format
            VidAfterOp(plat);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Download failed:\n" + ex.Message, "LiteBox", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void VidWebInfo(MetadataDb.WebImage w)
    {
        var srcs = MediaApiBridge.ListUrls(w);
        string chain = srcs.Count == 0 ? "(none)" : string.Join("\n", srcs.Select((s, i) => $"  {i + 1}. [{s.Kind}]  {s.Url}"));
        string text =
            $"Type:  {w.Type}\nOrigin:  {w.Origin}\nRegion:  {(string.IsNullOrEmpty(w.Region) ? "World" : w.Region)}\n" +
            $"Duplicate:  {w.Duplicate}\nCRC32:  {unchecked((uint)w.Crc32)}\n" +
            $"Size:  {(w.FileSize > 0 ? $"{w.FileSize / (1024.0 * 1024.0):0.##} MB ({w.FileSize} bytes)" : "?")}\n" +
            $"File type:  {(string.IsNullOrEmpty(w.FileType) ? "?" : w.FileType)}\n" +
            (VidIsHlsRow(w) ? "\n⚠  HLS stream (fake .mp4) — plays, but can only be saved if the mirror has a copy.\n" : "") +
            $"\nDB FileName:\n  {w.FileName}\n\nSource chain (tried in order):\n{chain}\n";
        MessageBox.Show(this, text, "Web video info", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // ── EmuMovies videos (blue) — LIVE, the user's own account ────────────────
    // A source distinct from the database (purple): queries EmuMovies live via the user's credentials
    // (EmuMoviesApi/EmuMoviesCatalog), matches this game by title + MAME rom-stem, and lists the video assets.
    // The media.emumovies.com file GET needs no auth (only a Referer), so streaming/downloading is a plain GET.
    // Best-effort: unmatched games simply show nothing.

    private static readonly Color EmuBlue = Color.FromArgb(90, 150, 220);
    private bool _vidShowEmu;
    private EmuMoviesApi? _emuApi;
    private bool _emuApiProbed;
    // Per-game cache of the resolved EmuMovies assets (null value = fetch in flight / not started).
    private readonly Dictionary<string, List<EmuMoviesCatalog.EmuMedia>?> _vidEmuCache = new(StringComparer.Ordinal);

    /// <summary>EmuMovies is usable for a game: credentials configured AND its platform maps to EmuMovies (else
    /// the checkbox would resolve to nothing). Same gate as the image side (ImgEmuAvailable).</summary>
    private static bool VidEmuAvailable(IGame g)
    {
        try { return EmuMoviesCatalog.SupportsPlatform(Safe(() => g.Platform) ?? "") && EmuMoviesApi.FromLbSettings() != null; }
        catch { return false; }
    }

    // ── Owned detection for a web/EmuMovies source (shared by images + videos) ──
    // Priority, per the user: the ADS-recorded values first (they survive a re-cut — a trimmed video's on-disk
    // size changes but its :info FileSize keeps the original), then the on-disk size ONLY for files that carry
    // no ADS size. CRC comes from the ADS too (never recomputed — Cloudflare re-compresses EmuMovies images in
    // transit, so a recomputed CRC wouldn't match the API's anyway).
    internal readonly record struct EmuOwnedIndex(HashSet<uint> Crc, HashSet<long> AdsFs, HashSet<long> DiskFsNoAds);

    internal static EmuOwnedIndex BuildEmuOwned(IEnumerable<string> paths)
    {
        var crc = new HashSet<uint>();
        var adsFs = new HashSet<long>();
        var diskNoAds = new HashSet<long>();
        foreach (var p in paths)
        {
            var s = FileMetaStore.Read(p, FileMetaStore.StreamCrc32);
            if (!string.IsNullOrEmpty(s) && long.TryParse(s, out var c) && c != 0) crc.Add(unchecked((uint)c));
            long adsSize = 0;
            var info = ImageInfoBridge.ReadAny(p);
            if (info is ImageInfo i && i.FileSize > 0) { adsSize = i.FileSize; adsFs.Add(i.FileSize); }
            if (adsSize == 0) { try { var len = new FileInfo(p).Length; if (len > 0) diskNoAds.Add(len); } catch { } }
        }
        return new EmuOwnedIndex(crc, adsFs, diskNoAds);
    }

    internal static bool EmuOwns(EmuOwnedIndex idx, long crc, long fileSize)
        => idx.Crc.Contains(unchecked((uint)crc))
        || (fileSize > 0 && idx.AdsFs.Contains(fileSize))          // ADS size — priority, survives a re-cut
        || (fileSize > 0 && idx.DiskFsNoAds.Contains(fileSize));   // disk size — only when no ADS size

    private EmuMoviesApi? EmuApi()
    {
        if (!_emuApiProbed) { _emuApiProbed = true; _emuApi = EmuMoviesApi.FromLbSettings(); }
        return _emuApi;
    }

    private Panel VidEmuCell(EmuMoviesCatalog.EmuMedia m)
    {
        var cell = new Panel { Size = new Size(VidCellW, VidCellH), BackColor = Bg };

        var frame = new Panel { BackColor = EmuBlue, Padding = new Padding(S(2)) };   // blue = EmuMovies
        frame.SetBounds(S(4), S(4), VidCellW - S(12), VidThumbH);
        var pic = new PictureBox
        {
            Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(18, 18, 24),
            Cursor = Cursors.Hand,
        };
        VidLoadThumbEmu(pic, m);
        var mi = m;
        pic.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Right) { VidEmuMenu(mi).Show(pic, e.Location); return; }
            if (e.Button == MouseButtons.Left) VidPlayEmu(mi);
        };
        frame.Controls.Add(pic);
        cell.Controls.Add(frame);

        var name = new Label
        {
            Text = "EmuMovies · " + m.MediaType, ForeColor = EmuBlue, BackColor = Bg, Font = new Font("Segoe UI", 8f),
            AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true,
        };
        name.SetBounds(S(4), VidThumbH + S(8), VidCellW - S(12), S(18));
        cell.Controls.Add(name);

        string size = m.FileSize > 0 ? $"{m.FileSize / (1024.0 * 1024.0):0.#} MB" : "";
        var meta = new Label
        {
            Text = (string.IsNullOrEmpty(m.Region) ? "World" : m.Region) + (size.Length > 0 ? "   " + size : ""),
            ForeColor = Color.FromArgb(120, 126, 142), BackColor = Bg, Font = new Font("Segoe UI", 8f),
            AutoSize = false, TextAlign = ContentAlignment.MiddleLeft,
        };
        meta.SetBounds(S(4), VidThumbH + S(26), VidCellW - S(12), S(16));
        cell.Controls.Add(meta);
        return cell;
    }

    private ContextMenuStrip VidEmuMenu(EmuMoviesCatalog.EmuMedia m)
    {
        var menu = ThemedMenu();
        menu.Items.Add(new ToolStripMenuItem("▶  Play (stream)").WithClick(() => VidPlayEmu(m)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("⬇  Download").WithClick(() => VidDownloadEmu(m, "Video Snap")));
        var asType = new ToolStripMenuItem("⬇  Download As");
        foreach (var t in VidTypes()) { string tt = t; asType.DropDownItems.Add(new ToolStripMenuItem(tt).WithClick(() => VidDownloadEmu(m, tt))); }
        menu.Items.Add(asType);
        return menu;
    }

    private void VidLoadThumbEmu(PictureBox pic, EmuMoviesCatalog.EmuMedia m)
    {
        if (!VlcService.Available) return;
        // Defer until the box has a window handle (a page built before it's shown has none — the decoded frame
        // would otherwise be dropped by BeginInvoke below). Same fix as the image tiles.
        if (!pic.IsHandleCreated)
        {
            void OnCreated(object? _, EventArgs __) { pic.HandleCreated -= OnCreated; VidLoadThumbEmu(pic, m); }
            pic.HandleCreated += OnCreated;
            return;
        }
        var mi = m;
        System.Threading.Tasks.Task.Run(() =>
        {
            Image? thumb = null;
            try { thumb = VideoThumbnailer.GetFromUrl(mi.Url, EmuMoviesApi.MediaReferer, "emu:" + mi.Crc + ":" + mi.Url); } catch { }
            if (thumb == null) return;
            try
            {
                if (pic.IsDisposed || !pic.IsHandleCreated) { thumb.Dispose(); return; }
                pic.BeginInvoke(new Action(() => { if (pic.IsDisposed) { thumb.Dispose(); return; } var old = pic.Image; pic.Image = thumb; old?.Dispose(); }));
            }
            catch { thumb.Dispose(); }
        });
    }

    private void VidPlayEmu(EmuMoviesCatalog.EmuMedia m)
        => VideoPlayerDialog.PlayWeb(this, $"EmuMovies · {m.MediaType}",
               new[] { new VideoPlayerDialog.Source(m.Url, EmuMoviesApi.MediaReferer) });

    private void VidDownloadEmu(EmuMoviesCatalog.EmuMedia m, string targetType)
    {
        if (_readOnly) return;
        var g = ImgGame;
        string plat = Safe(() => g.Platform) ?? "";
        string idStr = Safe(() => g.Id) ?? "";
        int dbId = Safe(() => g.LaunchBoxDbId) ?? -1;
        if (string.IsNullOrEmpty(plat) || string.IsNullOrEmpty(idStr)) return;

        byte[]? bytes = null;
        UseWaitCursor = true;
        try
        {
            using var http = NewHttp();
            using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, m.Url);
            req.Headers.Referrer = new Uri(EmuMoviesApi.MediaReferer);
            using var resp = http.Send(req);
            if (resp.IsSuccessStatusCode) bytes = resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
        }
        catch { }
        finally { UseWaitCursor = false; }

        if (bytes == null || bytes.Length == 0)
        {
            MessageBox.Show(this, "The EmuMovies download failed.", "LiteBox", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            string ext = string.IsNullOrEmpty(m.Ext) ? "mp4" : m.Ext;
            string? dir = MediaResolver.VideoTypeFolder(plat, targetType);
            if (string.IsNullOrEmpty(dir)) return;
            Directory.CreateDirectory(dir);
            string sani = MediaResolver.Sanitize(Safe(() => g.Title) ?? "");
            string prefix = ImgPrefix(plat, idStr, sani, null, dir);
            int num = ImgMaxNum(dir, prefix) + 1;
            string target = Path.Combine(dir, $"{prefix}-{num:D2}.{ext.TrimStart('.')}");
            File.WriteAllBytes(target, bytes);

            // ADS in ExtendDB format — origin "lb-emumovies": a LIVE EmuMovies download, distinct from the DB's
            // own "emumovies" rows so the owned-video border can tell them apart (blue vs purple).
            var wi = new MetadataDb.WebImage(dbId, m.Url, m.LbType, m.Region, m.Crc, "lb-emumovies", 0, m.Ext, m.FileSize);
            ImageAdsWriter.WriteForDownload(target, wi, dbId, plat);
            VidAfterOp(plat);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Download failed:\n" + ex.Message, "LiteBox", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ── Steam videos (green) — LIVE, appdetails, only for games with a Steam appid ──
    private bool _vidShowSteam;
    private readonly Dictionary<string, List<MetadataDb.WebImage>?> _vidSteamCache = new(StringComparer.Ordinal);

    private static bool VidSteamAvailable(IGame g)
    {
        try { return SteamCatalog.AppIdOf(Safe(() => g.ApplicationPath) ?? "", Safe(() => g.LaunchBoxDbId) ?? -1) != null; }
        catch { return false; }
    }

    private Panel VidSteamCell(MetadataDb.WebImage w)
    {
        var cell = new Panel { Size = new Size(VidCellW, VidCellH), BackColor = Bg };
        var frame = new Panel { BackColor = SteamGreen, Padding = new Padding(S(2)) };
        frame.SetBounds(S(4), S(4), VidCellW - S(12), VidThumbH);
        var pic = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(18, 18, 24), Cursor = Cursors.Hand };
        VidLoadThumbSteam(pic, w);
        var wi = w;
        pic.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Right)
            {
                var menu = ThemedMenu();
                menu.Items.Add(new ToolStripMenuItem("▶  Play (stream)").WithClick(() => VidPlaySteam(wi)));
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add(new ToolStripMenuItem("⬇  Download").WithClick(() => VidDownloadSteam(wi, "Video Snap")));
                var asType = new ToolStripMenuItem("⬇  Download As");
                foreach (var t in VidTypes()) { string tt = t; asType.DropDownItems.Add(new ToolStripMenuItem(tt).WithClick(() => VidDownloadSteam(wi, tt))); }
                menu.Items.Add(asType);
                menu.Show(pic, e.Location); return;
            }
            if (e.Button == MouseButtons.Left) VidPlaySteam(wi);
        };
        frame.Controls.Add(pic);
        cell.Controls.Add(frame);

        var name = new Label { Text = "Steam trailer", ForeColor = SteamGreen, BackColor = Bg, Font = new Font("Segoe UI", 8f), AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
        name.SetBounds(S(4), VidThumbH + S(8), VidCellW - S(12), S(18));
        cell.Controls.Add(name);
        return cell;
    }

    private void VidLoadThumbSteam(PictureBox pic, MetadataDb.WebImage w)
    {
        if (!VlcService.Available) return;
        if (!pic.IsHandleCreated) { void OnCreated(object? _, EventArgs __) { pic.HandleCreated -= OnCreated; VidLoadThumbSteam(pic, w); } pic.HandleCreated += OnCreated; return; }
        var url = w.FileName;
        System.Threading.Tasks.Task.Run(() =>
        {
            Image? thumb = null;
            try { thumb = VideoThumbnailer.GetFromUrl(url, SteamApi.Referer, "steam:" + url); } catch { }
            if (thumb == null) return;
            try
            {
                if (pic.IsDisposed || !pic.IsHandleCreated) { thumb.Dispose(); return; }
                pic.BeginInvoke(new Action(() => { if (pic.IsDisposed) { thumb.Dispose(); return; } var old = pic.Image; pic.Image = thumb; old?.Dispose(); }));
            }
            catch { thumb.Dispose(); }
        });
    }

    // ── Steam trailer: yt-dlp fallback for HLS-only trailers ──────────────────
    // Newer Steam trailers are HLS-only: the reconstructed movie_max.mp4 is a dead 404, so libvlc can't open it.
    // yt-dlp's Steam extractor still resolves them; we play/download the HLS MASTER manifest through it. The direct
    // mp4 stays the fast path for older trailers that still have it.
    private string? VidSteamAppId(IGame g)
    {
        try { return SteamCatalog.AppIdOf(Safe(() => g.ApplicationPath) ?? "", Safe(() => g.LaunchBoxDbId) ?? -1); }
        catch { return null; }
    }

    private static bool VidUrlOk(string url)
    {
        try
        {
            using var h = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(6) };
            using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Head, url);
            if (Uri.TryCreate(SteamApi.Referer, UriKind.Absolute, out var r)) req.Headers.Referrer = r;
            using var resp = h.Send(req);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    /// <summary>The playable URL for a Steam trailer: the direct mp4 when it still exists, else the yt-dlp-resolved
    /// HLS master (video+audio), else null.</summary>
    private string? VidSteamPlayUrl(IGame g, MetadataDb.WebImage w)
    {
        if (VidUrlOk(w.FileName)) return w.FileName;                 // older trailers still have the direct mp4
        var appid = VidSteamAppId(g);
        if (appid != null && YtDlp.Available)
            try { return YtDlp.SteamTrailerMasterAsync(appid).GetAwaiter().GetResult(); } catch { }
        return null;
    }

    /// <summary>Download a Steam trailer via yt-dlp (the HLS master) when there are no direct bytes. True on success.</summary>
    private bool VidDownloadSteamHls(IGame g, MetadataDb.WebImage w, string targetType)
    {
        var appid = VidSteamAppId(g);
        if (appid == null || !YtDlp.Available) return false;
        try
        {
            string plat = Safe(() => g.Platform) ?? "";
            string idStr = Safe(() => g.Id) ?? "";
            int dbId = Safe(() => g.LaunchBoxDbId) ?? -1;
            if (string.IsNullOrEmpty(plat) || string.IsNullOrEmpty(idStr)) return false;
            string? master = YtDlp.SteamTrailerMasterAsync(appid).GetAwaiter().GetResult();
            if (master == null) return false;
            string? dir = MediaResolver.VideoTypeFolder(plat, targetType);
            if (string.IsNullOrEmpty(dir)) return false;
            Directory.CreateDirectory(dir);
            string sani = MediaResolver.Sanitize(Safe(() => g.Title) ?? "");
            string prefix = ImgPrefix(plat, idStr, sani, null, dir);
            int num = ImgMaxNum(dir, prefix) + 1;
            string outNoExt = Path.Combine(dir, $"{prefix}-{num:D2}");
            var outcome = YtDlp.DownloadAsync(master, "Best", outNoExt, YtDlp.CookieBrowser.None).GetAwaiter().GetResult();
            if (outcome.Path == null || !File.Exists(outcome.Path)) return false;
            long fs = 0; try { fs = new FileInfo(outcome.Path).Length; } catch { }
            ImageAdsWriter.WriteForDownload(outcome.Path, new MetadataDb.WebImage(dbId, w.FileName, w.Type, w.Region, w.Crc32, "lb-steam", w.Duplicate, "mp4", fs), dbId, plat);
            return true;
        }
        catch { return false; }
    }

    private void VidPlaySteam(MetadataDb.WebImage w)
    {
        UseWaitCursor = true;
        string? url; try { url = VidSteamPlayUrl(ImgGame, w); } finally { UseWaitCursor = false; }
        VideoPlayerDialog.PlayWeb(this, "Steam trailer", new[] { new VideoPlayerDialog.Source(url ?? w.FileName, SteamApi.Referer) });
    }

    private void VidDownloadSteam(MetadataDb.WebImage w, string targetType)
    {
        if (_readOnly) return;
        var g = ImgGame;
        string plat = Safe(() => g.Platform) ?? "";
        string idStr = Safe(() => g.Id) ?? "";
        int dbId = Safe(() => g.LaunchBoxDbId) ?? -1;
        if (string.IsNullOrEmpty(plat) || string.IsNullOrEmpty(idStr)) return;

        byte[]? bytes = null;
        UseWaitCursor = true;
        try { bytes = WebGetBytes(w.FileName, SteamApi.Referer); } catch { } finally { UseWaitCursor = false; }

        // Direct mp4 gone (newer HLS-only trailer) → let yt-dlp fetch the HLS master.
        if (bytes == null || bytes.Length == 0)
        {
            UseWaitCursor = true;
            bool okHls; try { okHls = VidDownloadSteamHls(g, w, targetType); } finally { UseWaitCursor = false; }
            if (okHls) { VidAfterOp(plat); return; }
            MessageBox.Show(this, "The Steam download failed.", "LiteBox", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            string ext = string.IsNullOrEmpty(w.FileType) ? "mp4" : w.FileType;
            string? dir = MediaResolver.VideoTypeFolder(plat, targetType);
            if (string.IsNullOrEmpty(dir)) return;
            Directory.CreateDirectory(dir);
            string sani = MediaResolver.Sanitize(Safe(() => g.Title) ?? "");
            string prefix = ImgPrefix(plat, idStr, sani, null, dir);
            int num = ImgMaxNum(dir, prefix) + 1;
            string target = Path.Combine(dir, $"{prefix}-{num:D2}.{ext.TrimStart('.')}");
            File.WriteAllBytes(target, bytes);
            // origin "lb-steam": a LIVE Steam download, distinct from the DB's "steam" rows (green vs purple border).
            ImageAdsWriter.WriteForDownload(target, new MetadataDb.WebImage(dbId, w.FileName, w.Type, w.Region, w.Crc32, "lb-steam", w.Duplicate, w.FileType, w.FileSize), dbId, plat);
            VidAfterOp(plat);
        }
        catch (Exception ex) { MessageBox.Show(this, "Download failed:\n" + ex.Message, "LiteBox", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    // ── Disk scan ─────────────────────────────────────────────────────────────
    private List<VidFile> VidScan(IGame g)
    {
        var list = new List<VidFile>();
        string plat = Safe(() => g.Platform) ?? "";
        string idLower = (Safe(() => g.Id) ?? "").ToLowerInvariant();
        Guid.TryParse(Safe(() => g.Id) ?? "", out var id);
        string title = Safe(() => g.Title) ?? "";

        List<(string path, string type)> files;
        try { files = MediaResolver.AllVideoFiles(plat, id, title); } catch { files = new(); }

        foreach (var (path, type) in files)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            string nl = name.ToLowerInvariant();
            bool hasGuid = !string.IsNullOrEmpty(idLower) && nl.Contains($".{idLower}-");
            int numVal = 0;
            int dash = nl.LastIndexOf('-');
            if (dash >= 0 && dash < nl.Length - 1) int.TryParse(nl.Substring(dash + 1), out numVal);
            list.Add(new VidFile(path, type, numVal, hasGuid));
        }
        return list;
    }
}
