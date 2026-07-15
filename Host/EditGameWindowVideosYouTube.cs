// Edit Game → Media → Videos → the YouTube source (its own dedicated section, red). Unlike the database /
// EmuMovies / Steam sources this one is SEARCH-DRIVEN and rides on yt-dlp (Host/Integrations/YtDlp.cs):
//
//   • Initial list (when the checkbox is first turned on): the game's own Video URL if it's a YouTube link,
//     then GOG store trailers (GogAppId), then a "{GameName} trailer" search — see YouTubeCatalog.
//   • The search box REPLACES the list with a specific search (a URL is probed as a single video); the "+"
//     button APPENDS its content instead of replacing.
//   • A result opens in the browser on left-click; right-click downloads it (yt-dlp → <Videos>\<type>\, + ADS).
//   • The gear opens the YouTube options (default searches / quality / cookies / yt-dlp download+update).
//
// Defaults (search templates, quality preset, cookies-from-browser) live in youtube.json via YtConfig — shared
// with the "YT-DLP" tab under Options → LB · Integrations.

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Unbroken.LaunchBox.Plugins.Data;
using LbApiHost.Host.Integrations;
using LbApiHost.Host.Media;
using LbApiHost.Host.Video;

namespace LbApiHost.Host;

internal sealed partial class EditGameWindow
{
    private static readonly Color YtRed = Color.FromArgb(210, 66, 60);

    private bool _vidShowYt;
    private bool _ytInit;
    private string _ytQuery = "";
    private string _ytQuality = "1080p";
    private YtDlp.CookieBrowser _ytCookies = YtDlp.CookieBrowser.None;
    private List<YtDlp.Result>? _ytResults;   // null = not resolved yet; non-null (even empty) = done
    private bool _ytResolving;
    private bool _ytVerified;                 // availability pass done for the current results
    private bool _ytVerifying;
    private readonly HashSet<string> _ytAge18 = new(StringComparer.Ordinal);   // ids to badge "18+"
    private List<string> _ytSearches = new();   // the expanded, valid default searches (run in order at first load)

    // Called from Navigate(): the cached Videos page belongs to the previous game, so drop it and rebuild for the
    // new one. The web/EmuMovies/Steam caches are keyed by game id (safe to keep); YouTube's results/query are a
    // single game-agnostic field, so reset them — the query re-derives from the new game's name on next render.
    private void ReloadVideosIfBuilt()
    {
        if (!_pages.Remove("Videos")) return;
        _ytInit = false; _ytResults = null; _ytResolving = false; _ytVerified = false; _ytVerifying = false; _ytAge18.Clear();
        if (_tree.SelectedNode?.Tag?.ToString() == "Videos") ShowPage("Videos");
    }

    private string? _ytForGameId;   // which game the YouTube state below belongs to

    private void VidYtInitDefaults(IGame g)
    {
        // The YouTube state (query / results / verify) is a single set of fields, NOT keyed by game — so it MUST
        // re-init whenever the game changes (matrix cell → modal, ◄ ► navigation), else the previous game's search
        // and results bleed into the new game's page.
        string gid = Safe(() => g.Id) ?? Safe(() => g.Title) ?? "";
        if (_ytInit && string.Equals(gid, _ytForGameId, StringComparison.Ordinal)) return;
        _ytInit = true; _ytForGameId = gid;
        _ytResults = null; _ytResolving = false; _ytVerified = false; _ytVerifying = false; _ytAge18.Clear();

        var cfg = YtConfig.Load();
        _ytQuality = cfg.Quality;
        _ytCookies = cfg.CookieBrowser;
        _ytSearches = YtExpandSearches(cfg.Searches, g);
        _ytQuery = _ytSearches.Count > 0 ? _ytSearches[0] : ((Safe(() => g.Title) ?? "") + " trailer").Trim();
    }

    // Expand each default-search template for THIS game: {GameName}, {Platform}, {AltName1}, {AltName2}, … (1-based
    // into the game's Alternate Names). A line whose {AltNameN} the game doesn't have (no alt, or no alt at that
    // index) is DROPPED — so an alt-name search silently disappears for games without that alt.
    private List<string> YtExpandSearches(List<string> templates, IGame g)
    {
        var alts = new List<string>();
        try
        {
            var an = g.GetAllAlternateNames();
            if (an != null) foreach (var a in an) { var n = Safe(() => a.Name); if (!string.IsNullOrWhiteSpace(n)) alts.Add(n!.Trim()); }
        }
        catch { }
        string title = Safe(() => g.Title) ?? "";
        string plat = Safe(() => g.Platform) ?? "";
        var rx = new System.Text.RegularExpressions.Regex(@"\{AltName(\d+)\}", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        var outq = new List<string>();
        foreach (var raw in templates ?? new List<string>())
        {
            var line = (raw ?? "").Trim();
            if (line.Length == 0) continue;
            bool skip = false;
            line = rx.Replace(line, m =>
            {
                if (int.TryParse(m.Groups[1].Value, out int i) && i >= 1 && i <= alts.Count) return alts[i - 1];
                skip = true; return "";
            });
            if (skip) continue;   // references an alt name this game doesn't have → drop the line
            line = line.Replace("{GameName}", title, StringComparison.OrdinalIgnoreCase)
                       .Replace("{Platform}", plat, StringComparison.OrdinalIgnoreCase).Trim();
            if (line.Length > 0 && !outq.Contains(line, StringComparer.OrdinalIgnoreCase)) outq.Add(line);
        }
        return outq;
    }

    // ── Section ────────────────────────────────────────────────────────────────
    private void VidAppendYouTube(IGame g, Panel inner, ref int y)
    {
        VidYtInitDefaults(g);

        y += S(14);
        inner.Controls.Add(new Label
        {
            Text = "▶  YouTube — left-click plays it in LiteBox, right-click downloads it",
            ForeColor = YtRed, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), AutoSize = false, BackColor = Bg,
            Bounds = new Rectangle(S(12), y, S(760), S(24)),
        });
        y += S(30);

        // Control row: [search box] [Search] [+] [quality] [gear]
        int rx = S(12);
        var box = new TextBox
        {
            Text = _ytQuery, BackColor = Color.FromArgb(30, 30, 38), ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle,
            Bounds = new Rectangle(rx, y, S(320), S(26)),
        };
        box.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; VidYtRunSearch(box.Text, append: false); } };
        inner.Controls.Add(box); rx += S(320) + S(6);

        var search = DlgBtn("🔍 Search", Color.FromArgb(60, 60, 72)); search.AutoSize = false; search.SetBounds(rx, y, S(78), S(26));
        search.Click += (_, _) => VidYtRunSearch(box.Text, append: false); inner.Controls.Add(search); rx += S(84);

        var plus = DlgBtn("+", Color.FromArgb(45, 95, 60)); plus.AutoSize = false; plus.SetBounds(rx, y, S(30), S(26));
        plus.Click += (_, _) => VidYtRunSearch(box.Text, append: true); inner.Controls.Add(plus); rx += S(36);

        var quality = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Color.FromArgb(30, 30, 38), ForeColor = Fg,
            FlatStyle = FlatStyle.Flat, Bounds = new Rectangle(rx, y, S(96), S(26)),
        };
        quality.Items.AddRange(YtConfig.QualityPresets);
        quality.SelectedItem = YtConfig.QualityPresets.Contains(_ytQuality) ? _ytQuality : "1080p";
        quality.SelectedIndexChanged += (_, _) => { if (quality.SelectedItem is string q) _ytQuality = q; };
        inner.Controls.Add(quality); rx += S(102);

        var gear = DlgBtn("⚙", Color.FromArgb(60, 60, 72)); gear.AutoSize = false; gear.SetBounds(rx, y, S(30), S(26));
        gear.Click += (_, _) => VidYtOpenOptions(); inner.Controls.Add(gear);
        y += S(34);

        // yt-dlp missing → offer to fetch it, nothing else works without it.
        if (!YtDlp.Available)
        {
            inner.Controls.Add(new Label
            {
                Text = "yt-dlp isn't installed yet — it's needed to search and download from YouTube.",
                ForeColor = SubFg, BackColor = Bg, AutoSize = true, Font = new Font("Segoe UI", 9f, FontStyle.Italic),
                Location = new Point(S(16), y),
            });
            y += S(26);
            var dl = DlgBtn("⬇  Download yt-dlp", Color.FromArgb(45, 95, 60)); dl.AutoSize = false; dl.SetBounds(S(16), y, S(160), S(28));
            dl.Click += async (_, _) =>
            {
                dl.Enabled = false; dl.Text = "Downloading…"; UseWaitCursor = true;
                bool ok = false; try { ok = await YtDlp.EnsureAsync(); } catch { }
                UseWaitCursor = false;
                if (ok) VidRebuildIfCurrent();
                else { dl.Enabled = true; dl.Text = "⬇  Download yt-dlp"; MessageBox.Show(this, "Couldn't download yt-dlp.", "LiteBox", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            };
            inner.Controls.Add(dl); y += S(36);
            return;
        }

        // Kick off the initial (Video URL → GOG → search) resolution the first time.
        if (_ytResults == null && !_ytResolving) VidYtResolveInitial(g);

        // Once results are in, verify them once (drop the un-downloadable ones, flag the 18+ ones).
        if (_ytResults != null && _ytResults.Count > 0 && !_ytVerified && !_ytVerifying) VidYtVerify();

        if (_ytResolving)
        {
            inner.Controls.Add(new Label { Text = "Querying YouTube…", ForeColor = SubFg, BackColor = Bg, AutoSize = true, Font = new Font("Segoe UI", 9f, FontStyle.Italic), Location = new Point(S(16), y) });
            y += S(26);
        }
        else if (_ytVerifying)
        {
            inner.Controls.Add(new Label { Text = "Checking availability…", ForeColor = SubFg, BackColor = Bg, AutoSize = true, Font = new Font("Segoe UI", 9f, FontStyle.Italic), Location = new Point(S(16), y) });
            y += S(26);
        }

        if (_ytResults != null)
        {
            if (_ytResults.Count == 0 && !_ytResolving)
            {
                inner.Controls.Add(new Label { Text = "No YouTube results.", ForeColor = SubFg, BackColor = Bg, AutoSize = true, Font = new Font("Segoe UI", 9f, FontStyle.Italic), Location = new Point(S(16), y) });
                y += S(26);
            }
            else
            {
                int hostW = inner.Parent?.ClientSize.Width ?? 0;
                int avail = (hostW > S(200) ? hostW : S(1100)) - S(12) - S(24);
                int cols = Math.Max(1, avail / VidCellW);
                int x = S(12), col = 0;
                foreach (var r in _ytResults)
                {
                    if (col == cols) { col = 0; x = S(12); y += VidCellH + S(8); }
                    var cell = VidYtCell(r); cell.Location = new Point(x, y); inner.Controls.Add(cell);
                    x += VidCellW; col++;
                }
                y += VidCellH + S(8);
            }
        }
    }


    // ── Resolve / search ────────────────────────────────────────────────────────
    private void VidYtResolveInitial(IGame g)
    {
        _ytResolving = true;
        string? videoUrl = Safe(() => g.VideoUrl);
        string? gogId = Safe(() => (g as ILiteBoxGame)?.GetField("GogAppId"));
        var searches = _ytSearches.Count > 0 ? _ytSearches : new List<string> { _ytQuery };
        var cookies = _ytCookies;
        System.Threading.Tasks.Task.Run(async () =>
        {
            List<YtDlp.Result> res = new();
            try { res = await YouTubeCatalog.ResolveInitialAsync(videoUrl, gogId, searches, 12, cookies); } catch { }
            YtDeliver(res, append: false);
        });
    }

    // Replace (append:false) or extend (append:true) the result list from a query — a URL is probed as one video.
    private void VidYtRunSearch(string text, bool append)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) return;
        if (!append) { _ytResults = null; _ytQuery = text; }
        _ytResolving = true;
        var cookies = _ytCookies; bool url = YouTubeCatalog.LooksLikeUrl(text);
        try { if (IsHandleCreated) BeginInvoke(new Action(VidRebuildIfCurrent)); } catch { }   // show "Querying…" now
        System.Threading.Tasks.Task.Run(async () =>
        {
            List<YtDlp.Result> res = new();
            try { res = url ? await YtDlp.ProbeUrlAsync(text, cookies) : await YtDlp.SearchAsync(text, 12, cookies); } catch { }
            YtDeliver(res, append);
        });
    }

    private void YtDeliver(List<YtDlp.Result> res, bool append)
    {
        try
        {
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke(new Action(() =>
            {
                if (append)
                {
                    var list = _ytResults ??= new List<YtDlp.Result>();
                    var have = new HashSet<string>(list.Select(r => r.Id), StringComparer.OrdinalIgnoreCase);
                    foreach (var r in res) if (have.Add(r.Id)) list.Add(r);
                }
                else _ytResults = res;
                _ytResolving = false;
                _ytVerified = false; _ytVerifying = false; _ytAge18.Clear();   // new/changed list → verify again (cache makes seen ids instant)
                VidRebuildIfCurrent();
            }));
        }
        catch { }
    }

    // Background availability pass: drop the results yt-dlp can't actually download (unavailable / storyboard-only)
    // and flag the age-restricted ones that DO work, so they get an "18+" badge instead of a silent removal.
    private void VidYtVerify()
    {
        if (_ytResults == null || _ytResults.Count == 0) return;
        _ytVerifying = true;
        var ids = _ytResults.Select(r => r.Id).ToList();
        var cookies = _ytCookies;
        System.Threading.Tasks.Task.Run(async () =>
        {
            Dictionary<string, YtDlp.Verdict> verdicts = new();
            try { verdicts = await YtDlp.VerifyAsync(ids, cookies); } catch { }
            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke(new Action(() =>
                {
                    if (_ytResults != null)
                    {
                        _ytResults = _ytResults.Where(r => !(verdicts.TryGetValue(r.Id, out var v) && !v.Available)).ToList();   // keep available + unverdicted
                        _ytAge18.Clear();
                        foreach (var r in _ytResults)
                            if (verdicts.TryGetValue(r.Id, out var v) && v.Available && v.AgeRestricted) _ytAge18.Add(r.Id);
                    }
                    _ytVerified = true; _ytVerifying = false;
                    VidRebuildIfCurrent();
                }));
            }
            catch { }
        });
    }

    // ── Tile ─────────────────────────────────────────────────────────────────────
    private Panel VidYtCell(YtDlp.Result r)
    {
        var cell = new Panel { Size = new Size(VidCellW, VidCellH), BackColor = Bg };
        var frame = new Panel { BackColor = YtRed, Padding = new Padding(S(2)) };
        frame.SetBounds(S(4), S(4), VidCellW - S(12), VidThumbH);
        var pic = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(18, 18, 24), Cursor = Cursors.Hand };
        VidLoadThumbUrl(pic, r.ThumbUrl);
        var ri = r;
        pic.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Right) { VidYtMenu(ri).Show(pic, e.Location); return; }
            if (e.Button == MouseButtons.Left) VidYtOpen(ri);
        };
        frame.Controls.Add(pic);
        cell.Controls.Add(frame);

        // "18+" badge for an age-restricted video that IS downloadable (verified with cookies).
        if (_ytAge18.Contains(r.Id))
        {
            var badge = new Label
            {
                Text = "18+", ForeColor = Color.White, BackColor = Color.FromArgb(200, 40, 40), Font = new Font("Segoe UI", 7.5f, FontStyle.Bold),
                AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, Size = new Size(S(28), S(16)), Location = new Point(S(8), S(8)),
            };
            pic.Controls.Add(badge); badge.BringToFront();
        }

        var name = new Label
        {
            Text = r.Title, ForeColor = SubFg, BackColor = Bg, Font = new Font("Segoe UI", 8f),
            AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true,
        };
        name.SetBounds(S(4), VidThumbH + S(8), VidCellW - S(12), S(18));
        cell.Controls.Add(name);

        string dur = r.DurationSec > 0 ? $"{r.DurationSec / 60}:{r.DurationSec % 60:D2}" : "";
        var meta = new Label
        {
            Text = (dur.Length > 0 ? dur + "   " : "") + r.Uploader, ForeColor = Color.FromArgb(120, 126, 142),
            BackColor = Bg, Font = new Font("Segoe UI", 8f), AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true,
        };
        meta.SetBounds(S(4), VidThumbH + S(26), VidCellW - S(12), S(16));
        cell.Controls.Add(meta);

        return cell;
    }

    private ContextMenuStrip VidYtMenu(YtDlp.Result r)
    {
        var m = ThemedMenu();
        m.Items.Add(new ToolStripMenuItem("▶  Play in LiteBox").WithClick(() => VidYtOpen(r)));
        m.Items.Add(new ToolStripMenuItem("🌐  Open in browser").WithClick(() => VidYtOpenBrowser(r)));
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(new ToolStripMenuItem("⬇  Download").WithClick(() => VidYtDownload(r, "Video Snap")));
        var asType = new ToolStripMenuItem("⬇  Download As");
        foreach (var t in VidTypes()) { string tt = t; asType.DropDownItems.Add(new ToolStripMenuItem(tt).WithClick(() => VidYtDownload(r, tt))); }
        m.Items.Add(asType);
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(new ToolStripMenuItem("🔗  Copy URL").WithClick(() => { try { Clipboard.SetText(r.WatchUrl); } catch { } }));
        return m;
    }

    // Left-click / "Play in LiteBox" → the in-app WebView2 mini-player (just the embed). Falls back to the system
    // browser when WebView2 isn't available (LB 13.27, or no Evergreen runtime).
    private void VidYtOpen(YtDlp.Result r)
    {
        try { if (YouTubePlayerDialog.TryShow(this, r.Id, r.WatchUrl, r.Title)) return; } catch { }
        VidYtOpenBrowser(r);
    }

    private void VidYtOpenBrowser(YtDlp.Result r)
    {
        try { Process.Start(new ProcessStartInfo(r.WatchUrl) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, "Couldn't open the browser:\n" + ex.Message, "LiteBox", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    // Direct GET of a (static) thumbnail URL into a PictureBox — deferred to HandleCreated, like the other loaders.
    private void VidLoadThumbUrl(PictureBox pic, string url)
    {
        if (!pic.IsHandleCreated)
        {
            void OnCreated(object? _, EventArgs __) { pic.HandleCreated -= OnCreated; VidLoadThumbUrl(pic, url); }
            pic.HandleCreated += OnCreated;
            return;
        }
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var bytes = WebGetBytes(url, null);
                if (bytes == null || bytes.Length == 0) return;
                using var ms = new MemoryStream(bytes);
                using var tmp = Image.FromStream(ms);
                const int maxDim = 360;
                double scale = Math.Min(1.0, (double)maxDim / Math.Max(tmp.Width, tmp.Height));
                var bmp = new Bitmap(tmp, new Size(Math.Max(1, (int)(tmp.Width * scale)), Math.Max(1, (int)(tmp.Height * scale))));
                try { if (pic.IsHandleCreated) pic.BeginInvoke(new Action(() => { if (!pic.IsDisposed) { var o = pic.Image; pic.Image = bmp; o?.Dispose(); } else bmp.Dispose(); })); else bmp.Dispose(); }
                catch { bmp.Dispose(); }
            }
            catch { }
        });
    }

    // ── Download ─────────────────────────────────────────────────────────────────
    private void VidYtDownload(YtDlp.Result r, string targetType)
    {
        if (_readOnly) return;
        var g = ImgGame;
        string plat = Safe(() => g.Platform) ?? "";
        string idStr = Safe(() => g.Id) ?? "";
        int dbId = Safe(() => g.LaunchBoxDbId) ?? -1;
        if (string.IsNullOrEmpty(plat) || string.IsNullOrEmpty(idStr)) return;

        string? dir = MediaResolver.VideoTypeFolder(plat, targetType);
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        string sani = MediaResolver.Sanitize(Safe(() => g.Title) ?? "");
        string prefix = ImgPrefix(plat, idStr, sani, null, dir);
        int num = ImgMaxNum(dir, prefix) + 1;
        string outNoExt = Path.Combine(dir, $"{prefix}-{num:D2}");
        string quality = _ytQuality; var cookies = _ytCookies; string watch = r.WatchUrl;

        // Downloading (+ possibly a ffmpeg merge) can take a while — run it behind a small modal with real progress.
        using var dlg = NewDialog("Downloading from YouTube…", 460, 156);
        var lbl = new Label { Text = r.Title, ForeColor = Fg, BackColor = Bg, AutoSize = false, Location = new Point(S(16), S(14)), Size = new Size(S(424), S(20)) };
        var status = new Label { Text = "Starting…", ForeColor = SubFg, BackColor = Bg, AutoSize = false, Font = new Font("Segoe UI", 8.5f), Location = new Point(S(16), S(38)), Size = new Size(S(320), S(18)) };
        var pb = new ProgressBar { Style = ProgressBarStyle.Continuous, Minimum = 0, Maximum = 100, Location = new Point(S(16), S(62)), Size = new Size(S(424), S(16)) };
        var cancel = DlgBtn("Cancel", Color.FromArgb(70, 70, 82)); cancel.AutoSize = false; cancel.SetBounds(S(344), S(90), S(96), S(28));
        dlg.Controls.Add(lbl); dlg.Controls.Add(status); dlg.Controls.Add(pb); dlg.Controls.Add(cancel);

        var cts = new CancellationTokenSource();
        YtDlp.DownloadOutcome? outcome = null;
        bool cancelled = false;
        cancel.Click += (_, _) => { cancelled = true; cts.Cancel(); };
        dlg.FormClosing += (_, _) => { if (outcome == null) cancelled = true; cts.Cancel(); };   // closed before finish = user closed it

        // Progress<> is created on the UI thread → its callbacks marshal back here safely.
        var progress = new Progress<double>(p => { if (!dlg.IsDisposed) { try { pb.Value = (int)Math.Clamp(p * 100, 0, 100); } catch { } } });
        var phase = new Progress<string>(s => { if (!dlg.IsDisposed) { status.Text = s; if (s == "Merging…") pb.Style = ProgressBarStyle.Marquee; } });

        dlg.Shown += (_, _) => System.Threading.Tasks.Task.Run(async () =>
        {
            var o = await YtDlp.DownloadAsync(watch, quality, outNoExt, cookies, progress, phase, cts.Token);
            outcome = o;
            try { if (!dlg.IsDisposed && dlg.IsHandleCreated) dlg.BeginInvoke(new Action(() => { if (!dlg.IsDisposed) dlg.Close(); })); } catch { }
        });
        dlg.ShowDialog(this);
        cts.Cancel();

        if (cancelled) return;
        if (outcome?.Path == null || !File.Exists(outcome.Path))
        {
            string err = outcome?.Error ?? "";
            string extra = string.IsNullOrWhiteSpace(err) ? "" : "\n\n" + err;
            bool gated = err.IndexOf("format is not available", StringComparison.OrdinalIgnoreCase) >= 0
                      || err.IndexOf("Sign in", StringComparison.OrdinalIgnoreCase) >= 0
                      || err.IndexOf("age", StringComparison.OrdinalIgnoreCase) >= 0;
            string hint = gated
                ? "\n\nYouTube is gating this video (age-restricted / protected). Set Firefox cookies in ⚙ Options — or just use “▶ Play in LiteBox” to watch it (some videos YouTube won't let yt-dlp download at all)."
                : "";
            MessageBox.Show(this, "The YouTube download failed." + extra + hint, "LiteBox", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        string made = outcome.Path;
        try
        {
            long fs = 0; try { fs = new FileInfo(made).Length; } catch { }
            var web = new MetadataDb.WebImage(dbId, watch, "Video", "", 0, "youtube", 0, "mp4", fs);
            ImageAdsWriter.WriteForDownload(made, web, dbId, plat);   // provenance ADS (origin=youtube)
        }
        catch { }
        VidAfterOp(plat);
    }

    // ── Options (gear) ───────────────────────────────────────────────────────────
    private void VidYtOpenOptions()
    {
        var cfg = YtConfig.Load();
        using var dlg = NewDialog("YouTube options", 520, 430);

        Label Head(string t, int y) { var l = new Label { Text = t, ForeColor = YtRed, BackColor = Bg, AutoSize = true, Font = new Font("Segoe UI", 9f, FontStyle.Bold), Location = new Point(S(16), y) }; dlg.Controls.Add(l); return l; }
        Label Note(string t, int y) { var l = new Label { Text = t, ForeColor = SubFg, BackColor = Bg, AutoSize = true, Font = new Font("Segoe UI", 8f), Location = new Point(S(16), y) }; dlg.Controls.Add(l); return l; }

        Head("Default searches — one per line, tags {GameName} / {Platform} / {AltName1}…", S(12));
        var searches = new TextBox
        {
            Multiline = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.FromArgb(30, 30, 38), ForeColor = Fg,
            BorderStyle = BorderStyle.FixedSingle, Text = string.Join("\r\n", cfg.Searches),
            Bounds = new Rectangle(S(16), S(34), S(480), S(84)),
        };
        dlg.Controls.Add(searches);
        Note("Tried in order. A line whose {AltNameN} the game doesn't have is skipped.", S(122));

        Head("Quality", S(148));
        var quality = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Color.FromArgb(30, 30, 38), ForeColor = Fg, FlatStyle = FlatStyle.Flat, Bounds = new Rectangle(S(120), S(146), S(120), S(26)) };
        quality.Items.AddRange(YtConfig.QualityPresets); quality.SelectedItem = YtConfig.QualityPresets.Contains(cfg.Quality) ? cfg.Quality : "1080p";
        dlg.Controls.Add(quality);

        Head("Cookies from", S(184));
        var cookies = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Color.FromArgb(30, 30, 38), ForeColor = Fg, FlatStyle = FlatStyle.Flat, Bounds = new Rectangle(S(120), S(182), S(120), S(26)) };
        foreach (var n in YtConfig.CookieNames) cookies.Items.Add(n);
        cookies.SelectedItem = Enum.GetName(typeof(YtDlp.CookieBrowser), cfg.CookieBrowser);
        dlg.Controls.Add(cookies);
        Note("For age-gated / region-locked videos (uses your browser's login). Firefox is the most reliable;", S(212));
        Note("Chrome/Edge often fail (locked/encrypted cookie store) — close the browser or use Firefox.", S(226));

        // yt-dlp binary
        Head("yt-dlp", S(252));
        var ver = new Label { Text = YtVersionText(), ForeColor = SubFg, BackColor = Bg, AutoSize = true, Font = new Font("Segoe UI", 8.5f), Location = new Point(S(120), S(254)) };
        dlg.Controls.Add(ver);
        var dl = DlgBtn("Download", Color.FromArgb(45, 95, 60)); dl.AutoSize = false; dl.SetBounds(S(16), S(280), S(110), S(28));
        var up = DlgBtn("Update", Color.FromArgb(60, 60, 82)); up.AutoSize = false; up.SetBounds(S(132), S(280), S(110), S(28));
        async void Fetch(Button b, Func<CancellationToken, System.Threading.Tasks.Task<bool>> op)
        {
            dl.Enabled = up.Enabled = false; string old = b.Text; b.Text = "…"; UseWaitCursor = true;
            bool ok = false; try { ok = await op(CancellationToken.None); } catch { }
            UseWaitCursor = false; dl.Enabled = up.Enabled = true; b.Text = old;
            ver.Text = YtVersionText();
            if (!ok) MessageBox.Show(dlg, "yt-dlp download failed.", "LiteBox", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        dl.Click += (_, _) => Fetch(dl, YtDlp.EnsureAsync);
        up.Click += (_, _) => Fetch(up, YtDlp.UpdateAsync);
        dlg.Controls.Add(dl); dlg.Controls.Add(up);

        var ok = DlgBtn("OK", Color.FromArgb(45, 95, 60)); ok.AutoSize = false; ok.SetBounds(S(300), S(340), S(96), S(30));
        var ca = DlgBtn("Cancel", Color.FromArgb(70, 70, 82)); ca.AutoSize = false; ca.SetBounds(S(404), S(340), S(96), S(30));
        ok.Click += (_, _) =>
        {
            cfg.Searches = searches.Text.Replace("\r\n", "\n").Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            cfg.Quality = quality.SelectedItem as string ?? "1080p";
            cfg.Cookies = cookies.SelectedItem as string ?? "None";
            cfg.Save();
            _ytQuality = cfg.Quality; _ytCookies = cfg.CookieBrowser;
            // Re-expand the (possibly changed) default searches for THIS game so a re-open picks them up.
            _ytSearches = YtExpandSearches(cfg.Searches, ImgGame);
            dlg.DialogResult = DialogResult.OK; dlg.Close();
        };
        ca.Click += (_, _) => { dlg.DialogResult = DialogResult.Cancel; dlg.Close(); };
        dlg.Controls.Add(ok); dlg.Controls.Add(ca);

        dlg.ShowDialog(this);
    }

    private static string YtVersionText()
        => YtDlp.Available ? ("installed" + (YtDlp.Version() is { } v ? "  ·  " + v : "")) : "not installed";
}
