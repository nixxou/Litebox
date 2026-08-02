// Edit Game → Media → Music. Le principe des Documents, en plus simple :
//
//   • la COLLECTION : tous les fichiers audio dont le NOM designe le jeu (<Titre> / <Titre>-NN,
//     forme GUID — meme regle d'appartenance que les manuels, toute profondeur), a PLAT dans
//     Music\<Platform>\ : pas de region, pas de sous-dossier impose, on ajoute a la suite de la
//     numerotation (ManualLibrary.FreeMusicDestination).
//   • la DESIGNATION : <MusicPath> pose PAR-DESSUS la selection auto (le premier du parcours,
//     MediaResolver.MusicsAll — exactement ce que GetMusicPath() rend). Or PLEIN = designe
//     ("Music · main"), or POINTILLE = choix auto ("Music · auto").
//   • PAS de documents additionnels : la collection EST la liste.
//
// Lecture integree : clic gauche = play via le LibVLC partage (VlcService — celui des videos),
// audio seul, avec une barre de controles (play/pause, stop, seek, temps, volume). Sans libvlc,
// le clic ouvre le lecteur systeme. Le lecteur s'arrete et se libere quand un jeu se lance
// (VlcService.Stopping) et quand la page est detruite.
//
// Telechargements : lignes GameImages Type='Music' (ExtendDB) + EmuMovies LbType 'Music' — meme
// plomberie que les documents (WebImage → ImgFetchWebBytes + ADS), destination a plat.

#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using LibVLCSharp.Shared;
using Unbroken.LaunchBox.Plugins.Data;
using LbApiHost.Host.Integrations;
using LbApiHost.Host.Media;
using LbApiHost.Host.Video;
using VlcMedia = LibVLCSharp.Shared.Media;

namespace LbApiHost.Host;

internal sealed partial class EditGameWindow
{
    private static readonly HashSet<string> MusExts = new(StringComparer.OrdinalIgnoreCase)
    { ".mp3", ".ogg", ".wav", ".flac", ".m4a" };

    private int MusCellW => S(150);
    private int MusCellH => S(150);
    private int MusThumbH => MusCellH - S(52);

    private Panel? _musHost;
    private bool _musShowWeb, _musShowEmu;

    // ── Lecteur (LibVLC partage — audio seul, pas de surface) ──────────────────
    private MediaPlayer? _musPlayer;
    private string _musPlaying = "";
    private bool _musSeekDrag;
    private int _musVolume = 80;
    private System.Windows.Forms.Timer? _musTimer;
    private Button? _musBtnPlay;
    private TrackBar? _musSeek;
    private Label? _musTime, _musTrack;
    private Action? _musStoppingHandler;   // pour se desabonner de VlcService.Stopping

    // ── Page ────────────────────────────────────────────────────────────────
    private Control BuildMusicPage()
    {
        var container = new Panel { BackColor = Bg, Dock = DockStyle.Fill };
        var host = new Panel { BackColor = Bg, AutoScroll = true, Dock = DockStyle.Fill };

        var bar = new Panel { Dock = DockStyle.Top, Height = S(40), BackColor = Bg };
        var add = DlgBtn("＋ Add Music…", Color.FromArgb(45, 95, 60));
        add.AutoSize = false; add.SetBounds(S(4), S(6), S(150), S(28)); add.Enabled = !_readOnly;
        add.Click += (_, _) => MusAdd();
        bar.Controls.Add(add);

        int chipX = S(164);
        void AddChip(CheckBox c, int w) { c.SetBounds(chipX, S(8), w, S(26)); bar.Controls.Add(c); chipX += w + S(10); }
        int dbId0 = Safe(() => DocGame.LaunchBoxDbId) ?? -1;
        if (MediaApiBridge.ModuleActive && dbId0 > 0)
            AddChip(SourceChip("ExtendDB", WebPurple, _musShowWeb, on => { _musShowWeb = on; MusRefresh(); }), S(112));
        if (ImgEmuAvailable(DocGame))
            AddChip(SourceChip("EmuMovies", EmuBlue, _musShowEmu, on => { _musShowEmu = on; MusRefresh(); }), S(124));

        container.Controls.Add(host);
        container.Controls.Add(MusBuildPlayerStrip());
        container.Controls.Add(bar);
        container.Disposed += (_, _) => MusTeardown();
        MusPopulate(host);
        return container;
    }

    // La barre de lecture : ▶/⏸ · ⏹ · [seek] · 0:00 / 0:00 · vol · piste en cours.
    private Panel MusBuildPlayerStrip()
    {
        var strip = new Panel { Dock = DockStyle.Top, Height = S(38), BackColor = Color.FromArgb(24, 24, 30) };

        _musBtnPlay = DlgBtn("▶", Color.FromArgb(45, 75, 110));
        _musBtnPlay.AutoSize = false; _musBtnPlay.SetBounds(S(8), S(5), S(36), S(28));
        _musBtnPlay.Click += (_, _) => MusTogglePause();
        var stop = DlgBtn("⏹", Color.FromArgb(70, 60, 60));
        stop.AutoSize = false; stop.SetBounds(S(50), S(5), S(36), S(28));
        stop.Click += (_, _) => MusStop();

        _musSeek = new TrackBar { Minimum = 0, Maximum = 1000, TickStyle = TickStyle.None, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top, AutoSize = false };
        _musSeek.SetBounds(S(94), S(7), S(360), S(24));
        _musSeek.MouseDown += (_, _) => _musSeekDrag = true;
        _musSeek.MouseUp += (_, _) =>
        {
            _musSeekDrag = false;
            try { if (_musPlayer != null && _musSeek != null && _musPlayer.IsSeekable) _musPlayer.Position = _musSeek.Value / 1000f; } catch { }
        };

        _musTime = new Label { Text = "0:00 / 0:00", ForeColor = SubFg, BackColor = strip.BackColor, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, Anchor = AnchorStyles.Right | AnchorStyles.Top, Font = new Font("Segoe UI", 8.5f) };
        _musTime.SetBounds(S(460), S(9), S(86), S(20));

        var vol = new TrackBar { Minimum = 0, Maximum = 100, Value = _musVolume, TickStyle = TickStyle.None, AutoSize = false, Anchor = AnchorStyles.Right | AnchorStyles.Top };
        vol.SetBounds(S(550), S(7), S(80), S(24));
        vol.ValueChanged += (_, _) => { _musVolume = vol.Value; try { if (_musPlayer != null) _musPlayer.Volume = _musVolume; } catch { } };
        new ToolTip().SetToolTip(vol, "Volume");

        _musTrack = new Label { Text = "", ForeColor = Fg, BackColor = strip.BackColor, AutoSize = false, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top, Font = new Font("Segoe UI", 8.5f, FontStyle.Italic) };
        _musTrack.SetBounds(S(640), S(9), S(240), S(20));

        strip.Controls.Add(_musBtnPlay); strip.Controls.Add(stop); strip.Controls.Add(_musSeek);
        strip.Controls.Add(_musTime); strip.Controls.Add(vol); strip.Controls.Add(_musTrack);

        _musTimer = new System.Windows.Forms.Timer { Interval = 250 };
        _musTimer.Tick += (_, _) => MusPollPosition();

        // Un jeu se lance → libvlc va etre libere : lacher NOTRE MediaPlayer d'abord (meme regle
        // que VideoBlock — disposer l'instance sous un player vivant crashe dans les threads vlc).
        _musStoppingHandler = () =>
        {
            var p = _musPlayer; _musPlayer = null; _musPlaying = "";
            try { p?.Stop(); p?.Dispose(); } catch { }
            MusOnUi(() => MusResetUi());
        };
        VlcService.Stopping += _musStoppingHandler;
        return strip;
    }

    private void MusTeardown()
    {
        if (_musStoppingHandler != null) { VlcService.Stopping -= _musStoppingHandler; _musStoppingHandler = null; }
        try { _musTimer?.Stop(); _musTimer?.Dispose(); } catch { }
        _musTimer = null;
        var p = _musPlayer; _musPlayer = null; _musPlaying = "";
        try { p?.Stop(); p?.Dispose(); } catch { }
        _musHost = null; _musBtnPlay = null; _musSeek = null; _musTime = null; _musTrack = null;
    }

    private void MusOnUi(Action a)
    {
        var c = _musBtnPlay;
        try { if (c != null && c.IsHandleCreated && !c.IsDisposed) c.BeginInvoke(a); } catch { }
    }

    private void MusResetUi()
    {
        _musTimer?.Stop();
        if (_musBtnPlay != null) _musBtnPlay.Text = "▶";
        if (_musSeek != null) _musSeek.Value = 0;
        if (_musTime != null) _musTime.Text = "0:00 / 0:00";
        if (_musTrack != null) _musTrack.Text = "";
    }

    private static string MusFmt(long ms)
    {
        if (ms < 0) ms = 0;
        var t = TimeSpan.FromMilliseconds(ms);
        return $"{(int)t.TotalMinutes}:{t.Seconds:D2}";
    }

    private void MusPollPosition()
    {
        var p = _musPlayer;
        if (p == null) return;
        try
        {
            if (_musSeek != null && !_musSeekDrag)
                _musSeek.Value = Math.Min(1000, Math.Max(0, (int)(p.Position * 1000)));
            if (_musTime != null) _musTime.Text = MusFmt(p.Time) + " / " + MusFmt(p.Length);
        }
        catch { }
    }

    private void MusPlay(string absPath)
    {
        var lib = VlcService.Instance;
        if (lib == null) { DocOpen(absPath); return; }   // pas de libvlc → lecteur systeme
        try
        {
            if (_musPlayer == null)
            {
                _musPlayer = new MediaPlayer(lib) { Volume = _musVolume };
                // Callback VLC (thread vlc) : ne PAS rappeler le player dedans, juste l'UI.
                _musPlayer.EndReached += (_, _) => MusOnUi(() => { _musPlaying = ""; MusResetUi(); });
            }
            using var media = new VlcMedia(lib, absPath, FromType.FromPath);
            _musPlayer.Play(media);
            _musPlaying = absPath;
            if (_musBtnPlay != null) _musBtnPlay.Text = "⏸";
            if (_musTrack != null) _musTrack.Text = Path.GetFileName(absPath);
            _musTimer?.Start();
        }
        catch (Exception ex) { Console.WriteLine("[music] play failed: " + ex.Message); DocOpen(absPath); }
    }

    private void MusTogglePause()
    {
        var p = _musPlayer;
        if (p == null || _musPlaying.Length == 0) return;
        try
        {
            if (p.IsPlaying) { p.SetPause(true); if (_musBtnPlay != null) _musBtnPlay.Text = "▶"; }
            else { p.SetPause(false); if (_musBtnPlay != null) _musBtnPlay.Text = "⏸"; }
        }
        catch { }
    }

    private void MusStop()
    {
        try { _musPlayer?.Stop(); } catch { }
        _musPlaying = "";
        MusResetUi();
    }

    // ── Populate ────────────────────────────────────────────────────────────
    private void MusPopulate(Panel host)
    {
        _musHost = host;
        foreach (Control c in host.Controls) DocDisposePics(c);
        host.Controls.Clear();

        var inner = new Panel { BackColor = Bg, Location = Point.Empty, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowOnly };
        host.Controls.Add(inner);
        int y = S(10);

        // MusicPath readout — le champ brut, toujours visible (meme principe que ManualPath).
        string mpStored = Safe(() => DocGame.MusicPath) ?? "";
        bool mpSet = !string.IsNullOrWhiteSpace(mpStored);
        bool mpMissing = mpSet && !File.Exists(DocResolve(mpStored));
        var mpLbl = new Label
        {
            Text = "MusicPath :  " + (!mpSet ? "(not set — auto selection)" : mpStored + (mpMissing ? "   (missing)" : "")),
            ForeColor = !mpSet ? SubFg : mpMissing ? Color.FromArgb(200, 110, 100) : DocManualAccent,
            BackColor = Bg, Font = new Font("Segoe UI", 8.5f, mpSet ? FontStyle.Regular : FontStyle.Italic),
            AutoSize = false, AutoEllipsis = true,
        };
        mpLbl.SetBounds(S(12), y, DocAvailWidth(host), S(20));
        if (mpSet) new ToolTip().SetToolTip(mpLbl, DocResolve(mpStored));
        inner.Controls.Add(mpLbl); y += S(26);

        var tracks = MusAll();
        string pinnedAbs = MediaResolver.Override(mpStored) ?? "";
        string storedAbs = DocResolve(mpStored);
        string selected = pinnedAbs.Length > 0 ? pinnedAbs : (tracks.Count > 0 ? tracks[0] : "");
        bool designated = pinnedAbs.Length > 0;

        var mh = new Label
        {
            Text = "━━  Music" + (selected.Length == 0 ? ""
                   : designated ? "   ·   designated" : "   ·   auto — first of the collection plays (right-click one to set it)"),
            ForeColor = Fg, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), AutoSize = false, BackColor = Bg,
        };
        mh.SetBounds(S(12), y, S(700), S(26)); inner.Controls.Add(mh); y += S(30);

        int cols = Math.Max(1, DocAvailWidth(host) / MusCellW);
        int x = S(16), col = 0; bool any = false;
        void Place(Panel cell)
        {
            if (col == cols) { col = 0; x = S(16); y += MusCellH; }
            cell.Location = new Point(x, y); inner.Controls.Add(cell);
            x += MusCellW; col++; any = true;
        }
        if (designated && !tracks.Any(p => DocPathEq(p, pinnedAbs)))
            Place(MusTile(pinnedAbs, selected: true, designated: true));       // designe hors collection (externe)
        else if (!designated && storedAbs.Length > 0)
            Place(MusTile(storedAbs, selected: false, designated: true));      // designe mais INTROUVABLE
        foreach (var p in tracks)
            Place(MusTile(p, DocPathEq(p, selected), designated && DocPathEq(p, pinnedAbs)));
        if (!any)
        {
            var none = new Label
            {
                Text = "No music — use “＋ Add Music…” or download below.", AutoSize = false,
                ForeColor = SubFg, BackColor = Bg, Font = new Font("Segoe UI", 10f, FontStyle.Italic),
            };
            none.SetBounds(S(16), y + S(4), S(560), S(26)); inner.Controls.Add(none);
            y += S(34);
        }
        else y += MusCellH + S(12);

        MusAppendWeb(inner, host, ref y);
    }

    private void MusRefresh() { if (_musHost != null && !_musHost.IsDisposed) MusPopulate(_musHost); }

    /// <summary>Toute la collection, dans l ordre de resolution (element 0 = choix auto).</summary>
    private List<string> MusAll()
    {
        string plat = Safe(() => DocGame.Platform) ?? "", title = Safe(() => DocGame.Title) ?? "";
        Guid.TryParse(Safe(() => DocGame.Id) ?? "", out var gid);
        if (plat.Length == 0 || title.Length == 0) return new();
        try { return MediaResolver.MusicsAll(plat, gid, title); } catch { return new(); }
    }

    private string MusDir()
    {
        string root = DocLbRoot(), plat = Safe(() => DocGame.Platform) ?? "";
        return (root.Length > 0 && plat.Length > 0) ? Media.ManualLibrary.MusicDir(root, plat) : "";
    }

    private bool MusIsManaged(string abs)
    {
        string dir = MusDir();
        if (dir.Length == 0 || string.IsNullOrEmpty(abs)) return false;
        try { return abs.StartsWith(Path.GetFullPath(dir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && MusExts.Contains(Path.GetExtension(abs)); }
        catch { return false; }
    }

    private void MusDesignate(string abs) { try { DocGame.MusicPath = string.IsNullOrEmpty(abs) ? "" : DocStore(abs); } catch { } }

    // ── Tuile ───────────────────────────────────────────────────────────────
    private Panel MusTile(string absPath, bool selected, bool designated)
    {
        var cell = new Panel { Size = new Size(MusCellW, MusCellH), BackColor = Bg };
        bool exists = !string.IsNullOrEmpty(absPath) && File.Exists(absPath);
        bool managed = exists && MusIsManaged(absPath);
        Color? src = exists && !selected ? DocSourceColor(DocAdsOrigin(absPath)) : null;
        Color border = selected || designated ? DocManualAccent : (src ?? (managed ? DocManagedColor : DocExternalColor));
        DashStyle style = !exists ? DashStyle.Dash
                        : selected ? (designated ? DashStyle.Solid : DashStyle.Dot)
                        : src != null ? DashStyle.Dot
                        : managed ? DashStyle.Solid : DashStyle.Dash;

        cell.Paint += (_, e) =>
        {
            using var pen = new Pen(border, S(2)) { DashStyle = style };
            e.Graphics.DrawRectangle(pen, S(4), S(4), MusCellW - S(8), MusThumbH);
        };
        var pic = new PictureBox { SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(18, 18, 24), Cursor = Cursors.Hand };
        pic.SetBounds(S(6), S(6), MusCellW - S(12), MusThumbH - S(4));
        pic.Image = MusBadge(exists ? Path.GetExtension(absPath) : ".missing", MusCellW - S(16), MusThumbH - S(8));
        cell.Controls.Add(pic);
        // Clic GAUCHE = lecture (c'est une page musique) ; DROIT = menu.
        pic.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) { if (exists) MusPlay(absPath); }
            else if (e.Button == MouseButtons.Right) MusMenu(absPath, exists, managed, designated).Show(pic, e.Location);
        };

        var name = new Label { Text = Path.GetFileName(absPath), ForeColor = Fg, BackColor = Bg, Font = new Font("Segoe UI", 8f), AutoSize = false, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
        name.SetBounds(S(4), MusThumbH + S(6), MusCellW - S(8), S(16));
        cell.Controls.Add(name);

        string tag = "Music" + (designated ? " · main" : selected ? " · auto" : "");
        string loc = !exists ? "missing" : (managed ? "managed" : "external");
        string infoText = $"{tag}  ·  {Path.GetExtension(absPath).TrimStart('.').ToUpperInvariant()}  ·  {loc}";
        var info = new Label { Text = infoText, ForeColor = !exists ? Color.FromArgb(200, 110, 100) : selected || designated ? DocManualAccent : managed ? DocManagedColor : DocExternalColor, BackColor = Bg, Font = new Font("Segoe UI", 7.5f), AutoSize = false, AutoEllipsis = true };
        info.SetBounds(S(4), MusThumbH + S(24), MusCellW - S(8), S(16));
        cell.Controls.Add(info);
        new ToolTip().SetToolTip(info, infoText);
        return cell;
    }

    /// <summary>Vignette audio : une note sur fond sombre + l'extension (les fichiers audio n'ont
    /// pas d'apercu a rendre).</summary>
    private Bitmap MusBadge(string ext, int w, int h)
    {
        w = Math.Max(1, w); h = Math.Max(1, h);
        var bmp = new Bitmap(w, h);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.FromArgb(30, 30, 38));
        Color accent = ext == ".missing" ? Color.FromArgb(200, 110, 100)
                     : ext.Equals(".flac", StringComparison.OrdinalIgnoreCase) || ext.Equals(".wav", StringComparison.OrdinalIgnoreCase)
                        ? Color.FromArgb(120, 185, 170) : Color.FromArgb(110, 150, 220);
        using (var nf = new Font("Segoe UI Symbol", Math.Max(10f, h * 0.34f)))
        using (var nb = new SolidBrush(accent))
        {
            var sz = g.MeasureString("♪", nf);
            g.DrawString("♪", nf, nb, (w - sz.Width) / 2, h * 0.08f);
        }
        string label = ext == ".missing" ? "?" : ext.TrimStart('.').ToUpperInvariant();
        using var lf = new Font("Segoe UI", Math.Max(6f, h * 0.12f), FontStyle.Bold);
        var lsz = g.MeasureString(label, lf);
        using var lb = new SolidBrush(accent);
        g.DrawString(label, lf, lb, (w - lsz.Width) / 2, h - lsz.Height - S(4));
        return bmp;
    }

    // ── Menu ────────────────────────────────────────────────────────────────
    private ContextMenuStrip MusMenu(string absPath, bool exists, bool managed, bool designated)
    {
        var m = ThemedMenu();
        if (exists)
        {
            m.Items.Add(new ToolStripMenuItem("Play").WithClick(() => MusPlay(absPath)));
            m.Items.Add(new ToolStripMenuItem("Stop").WithClick(MusStop));
            m.Items.Add(new ToolStripMenuItem("Show in Explorer").WithClick(() => DocReveal(absPath)));
            m.Items.Add(new ToolStripMenuItem("Info…").WithClick(() => DocShowInfo(absPath, "Music")));
        }
        if (_readOnly) return m;

        m.Items.Add(new ToolStripSeparator());
        if (designated)
            m.Items.Add(new ToolStripMenuItem(exists ? "Clear designation (back to auto)" : "Unlink missing music")
                .WithClick(() => { MusDesignate(""); MusRefresh(); }));
        else if (exists)
            m.Items.Add(new ToolStripMenuItem("Set as music").WithClick(() => { MusDesignate(absPath); MusRefresh(); }));

        if (exists && managed)
        {
            string sani = MediaResolver.Sanitize(Safe(() => DocGame.Title) ?? "");
            if (GameMediaRenamer.TryPlain(Path.GetFileNameWithoutExtension(absPath), sani, out int curNum, allowUnnumbered: true))
                m.Items.Add(new ToolStripMenuItem("Change number…").WithClick(() => MusChangeNumber(absPath, sani, curNum)));
        }
        if (exists && !managed && MusDir().Length > 0)
        {
            m.Items.Add(new ToolStripMenuItem("Move into Music folder").WithClick(() => MusRelocate(absPath, move: true)));
            m.Items.Add(new ToolStripMenuItem("Copy into Music folder").WithClick(() => MusRelocate(absPath, move: false)));
        }
        if (exists && managed)
        {
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(new ToolStripMenuItem("Delete file").WithClick(() => MusDeleteFile(absPath)));
        }
        return m;
    }

    private void MusChangeNumber(string absPath, string sani, int curNum)
    {
        string cur = curNum == GameMediaRenamer.Unnumbered ? "" : curNum.ToString();
        if (!DocPrompt("Change number", "Number (1–99, empty = bare name):", cur, out string v)) return;
        v = v.Trim();
        int n = GameMediaRenamer.Unnumbered;
        if (v.Length > 0 && (!int.TryParse(v, out n) || n < 1 || n > 99))
        { MessageBox.Show(this, "Enter a number between 1 and 99, or leave empty for the bare name.", "LiteBox", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        if (n == curNum) return;
        string dest = Path.Combine(Path.GetDirectoryName(absPath) ?? "",
            (n == GameMediaRenamer.Unnumbered ? sani : $"{sani}-{n:D2}") + Path.GetExtension(absPath));
        if (File.Exists(dest))
        { MessageBox.Show(this, "That name is taken:\n" + Path.GetFileName(dest), "LiteBox", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        if (DocPathEq(_musPlaying, absPath)) MusStop();   // ne pas renommer sous le lecteur
        try { File.Move(absPath, dest); }
        catch (Exception ex) { MessageBox.Show(this, "Rename failed:\n" + ex.Message, "LiteBox", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        if (DocPathEq(DocResolve(Safe(() => DocGame.MusicPath)), absPath)) MusDesignate(dest);   // la designation suit
        MusRefresh();
    }

    private void MusDeleteFile(string absPath)
    {
        var res = MessageBox.Show(this, $"Delete this music file from disk?\n\n{Path.GetFileName(absPath)}",
            "Delete music", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
        if (res != DialogResult.OK) return;
        if (DocPathEq(_musPlaying, absPath)) MusStop();
        try { File.Delete(absPath); } catch { }
        if (DocPathEq(DocResolve(Safe(() => DocGame.MusicPath)), absPath)) MusDesignate("");
        MusRefresh();
    }

    private void MusRelocate(string abs, bool move)
    {
        string dest = MusApplyPlacement(abs, move ? 1 : 2);
        if (!string.Equals(dest, abs, StringComparison.OrdinalIgnoreCase)) { MusDesignate(dest); MusRefresh(); }
    }

    // ── Ajout ───────────────────────────────────────────────────────────────
    private void MusAdd()
    {
        if (_readOnly) return;
        using var ofd = new OpenFileDialog
        {
            Title = "Add music", Multiselect = true, CheckFileExists = true,
            Filter = "Audio (*.mp3;*.ogg;*.wav;*.flac;*.m4a)|*.mp3;*.ogg;*.wav;*.flac;*.m4a|All files (*.*)|*.*",
        };
        if (ofd.ShowDialog(this) != DialogResult.OK || ofd.FileNames.Length == 0) return;

        bool mainSet = !string.IsNullOrWhiteSpace(Safe(() => DocGame.MusicPath));
        if (!MusAskAddOptions(ofd.FileNames.Length, mainSet, out bool asMain, out int place)) return;

        bool firstIsMain = asMain;
        foreach (var src in ofd.FileNames)
        {
            string dest = MusApplyPlacement(src, place);
            if (string.IsNullOrEmpty(dest)) continue;
            if (firstIsMain) { MusDesignate(dest); firstIsMain = false; }
        }
        MusRefresh();
    }

    // place: 0 = use here · 1 = move · 2 = copy — destination A PLAT au prochain numero libre.
    private string MusApplyPlacement(string src, int place)
    {
        if (place == 0) return src;
        string root = DocLbRoot(), plat = Safe(() => DocGame.Platform) ?? "";
        string title = Safe(() => DocGame.Title) ?? "";
        if (root.Length == 0 || plat.Length == 0 || title.Length == 0) return src;
        try
        {
            string dest = Media.ManualLibrary.FreeMusicDestination(root, plat, title, Path.GetExtension(src));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            if (place == 2) File.Copy(src, dest, overwrite: true);
            else File.Move(src, dest, overwrite: true);
            return dest;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Couldn't place the file into the Music folder:\n" + ex.Message + "\n\nReferencing it in place instead.", "LiteBox", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return src;
        }
    }

    private bool MusAskAddOptions(int count, bool mainSet, out bool asMain, out int place)
    {
        asMain = false; place = 2;
        bool canManage = MusDir().Length > 0;
        using var f = NewDialog("Add music", 460, canManage ? 280 : 210);

        var lblRole = new Label { Text = "Add as:", Location = new Point(S(16), S(16)), AutoSize = true, ForeColor = Fg, BackColor = Bg };
        f.Controls.Add(lblRole);
        // Chaque groupe de radios dans SON conteneur (a plat, les cinq formeraient un seul groupe).
        var roleGrp = new Panel { Location = new Point(S(120), S(14)), Size = new Size(S(320), S(52)), BackColor = Bg };
        var rbTrack = new RadioButton { Text = count > 1 ? "Tracks (collection)" : "Track (collection)", Location = new Point(0, 0), AutoSize = true, ForeColor = Fg, BackColor = Bg, Checked = true };
        var rbMain = new RadioButton { Text = mainSet ? "Main music (replaces the current designation)" : "Main music (designated)", Location = new Point(0, S(26)), AutoSize = true, ForeColor = Fg, BackColor = Bg };
        roleGrp.Controls.Add(rbTrack); roleGrp.Controls.Add(rbMain);
        f.Controls.Add(roleGrp);
        if (count > 1) { var hint = new Label { Text = "(the first file becomes the main music, the rest join the collection)", Location = new Point(S(120), S(68)), AutoSize = true, ForeColor = SubFg, BackColor = Bg, Font = new Font("Segoe UI", 8f) }; f.Controls.Add(hint); }

        RadioButton rbHere = null!, rbMove = null!, rbCopy = null!;
        if (canManage)
        {
            var lblP = new Label { Text = "Location:", Location = new Point(S(16), S(104)), AutoSize = true, ForeColor = Fg, BackColor = Bg };
            f.Controls.Add(lblP);
            var locGrp = new Panel { Location = new Point(S(120), S(102)), Size = new Size(S(320), S(78)), BackColor = Bg };
            rbHere = new RadioButton { Text = "Use the file where it is (external)", Location = new Point(0, 0), AutoSize = true, ForeColor = Fg, BackColor = Bg };
            rbCopy = new RadioButton { Text = "Copy into Music\\" + (Safe(() => DocGame.Platform) ?? ""), Location = new Point(0, S(26)), AutoSize = true, ForeColor = Fg, BackColor = Bg, Checked = true };
            rbMove = new RadioButton { Text = "Move into Music\\" + (Safe(() => DocGame.Platform) ?? ""), Location = new Point(0, S(52)), AutoSize = true, ForeColor = Fg, BackColor = Bg };
            locGrp.Controls.Add(rbHere); locGrp.Controls.Add(rbCopy); locGrp.Controls.Add(rbMove);
            f.Controls.Add(locGrp);
        }

        bool ok = false;
        DialogButtons(f, out var okBtn, out var cancel);
        okBtn.Click += (_, _) => { ok = true; f.DialogResult = DialogResult.OK; f.Close(); };
        cancel.Click += (_, _) => { f.DialogResult = DialogResult.Cancel; f.Close(); };
        if (f.ShowDialog(this) != DialogResult.OK || !ok) return false;

        asMain = rbMain.Checked;
        place = !canManage ? 0 : (rbHere.Checked ? 0 : rbMove.Checked ? 1 : 2);
        return true;
    }

    // ── Telechargements (ExtendDB Type='Music' + EmuMovies 'Music') ──────────
    private IEnumerable<string> MusOwnedPaths()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string m = DocResolve(Safe(() => DocGame.MusicPath));
        if (!string.IsNullOrEmpty(m) && File.Exists(m) && seen.Add(m)) yield return m;
        foreach (var p in MusAll()) if (File.Exists(p) && seen.Add(p)) yield return p;
    }

    private void MusAppendWeb(Panel inner, Panel host, ref int y)
    {
        var g = DocGame;
        int dbId = Safe(() => g.LaunchBoxDbId) ?? -1;
        bool webOn = _musShowWeb && MediaApiBridge.ModuleActive && dbId > 0;
        bool emuOn = _musShowEmu && ImgEmuAvailable(g);
        if (!webOn && !emuOn) return;

        var cands = new List<(MetadataDb.WebImage w, string source)>();
        bool loading = false, dbNeedsExtend = false;
        var owned = BuildEmuOwned(MusOwnedPaths());

        if (webOn)
            try
            {
                var rows = MetadataDb.MusicForGame(MetadataDb.ExtendedDbPath, dbId);
                int total = rows.Count;
                if (!MediaApiBridge.Available) rows = rows.Where(r => r.IsLaunchbox).ToList();
                foreach (var w in rows) if (!EmuOwns(owned, w.Crc32, w.FileSize)) cands.Add((w, "web"));
                if (rows.Count == 0 && total > 0) dbNeedsExtend = true;
            }
            catch { }

        if (emuOn)
        {
            string key = Safe(() => g.Id) ?? Safe(() => g.Title) ?? "";
            if (!_docEmuCache.TryGetValue(key, out var em)) { MusTriggerEmuFetch(g, key); loading = true; }
            else if (em == null) loading = true;
            else foreach (var m in em.Where(m => string.Equals(m.LbType, "Music", StringComparison.OrdinalIgnoreCase)))
                 {
                     var w = ImgEmuToWeb(m, dbId);
                     if (!EmuOwns(owned, w.Crc32, w.FileSize)) cands.Add((w, "emu"));
                 }
        }

        if (cands.Count == 0 && !loading && !dbNeedsExtend) return;

        y += S(6);
        var hdr = new Label { Text = "⬇  Download music — left-click downloads into the collection · right-click for options", ForeColor = SubFg, Font = new Font("Segoe UI", 9f, FontStyle.Italic), AutoSize = false, BackColor = Bg };
        hdr.SetBounds(S(12), y, DocAvailWidth(host), S(24)); inner.Controls.Add(hdr); y += S(30);

        if (dbNeedsExtend)
        {
            inner.Controls.Add(new Label { Text = "This game's database music is ScreenScraper/EmuMovies — downloading it needs the ExtendDB plugin loaded (API credentials). Use the EmuMovies source, or load ExtendDB.", AutoSize = false, ForeColor = Color.FromArgb(220, 170, 90), BackColor = Bg, Font = new Font("Segoe UI", 8.5f), Bounds = new Rectangle(S(16), y, DocAvailWidth(host), S(34)) });
            y += S(38);
        }

        int cols = Math.Max(1, DocAvailWidth(host) / MusCellW);
        int x = S(16), col = 0;
        foreach (var (w, source) in cands)
        {
            if (col == cols) { col = 0; x = S(16); y += MusCellH; }
            var cell = MusWebTile(w, source);
            cell.Location = new Point(x, y); inner.Controls.Add(cell);
            x += MusCellW; col++;
        }
        if (cands.Count > 0) y += MusCellH;

        if (loading)
        {
            inner.Controls.Add(new Label { Text = "Querying EmuMovies…", ForeColor = SubFg, BackColor = Bg, AutoSize = true, Font = new Font("Segoe UI", 9f, FontStyle.Italic), Location = new Point(S(16), y) });
            y += S(24);
        }
    }

    private Panel MusWebTile(MetadataDb.WebImage w, string source)
    {
        var cell = new Panel { Size = new Size(MusCellW, MusCellH), BackColor = Bg };
        Color border = source == "emu" ? EmuBlue : WebPurple;
        var frame = new Panel { BackColor = Color.FromArgb(18, 18, 24) };
        frame.SetBounds(S(4), S(4), MusCellW - S(8), MusThumbH);
        frame.Paint += (_, e) => { using var pen = new Pen(border, S(2)); e.Graphics.DrawRectangle(pen, 1, 1, frame.Width - 3, frame.Height - 3); };
        string ext = MusWebExt(w);
        var pic = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(18, 18, 24), Cursor = Cursors.Hand };
        pic.Image = MusBadge(ext, MusCellW - S(16), MusThumbH - S(8));
        frame.Controls.Add(pic); cell.Controls.Add(frame);

        void Menu(Point pt)
        {
            var m = ThemedMenu();
            m.Items.Add(new ToolStripMenuItem("Download").WithClick(() => MusDownloadWeb(w, asMain: false)));
            m.Items.Add(new ToolStripMenuItem("Download and set as music").WithClick(() => MusDownloadWeb(w, asMain: true)));
            if (w.IsLaunchbox) m.Items.Add(new ToolStripMenuItem("Open in browser").WithClick(() => DocOpenUrl(w.Url)));
            m.Show(pic, pt);
        }
        pic.MouseUp += (_, e) => { if (_readOnly) return; if (e.Button == MouseButtons.Left) MusDownloadWeb(w, asMain: false); else if (e.Button == MouseButtons.Right) Menu(e.Location); };

        var cap = new Label { Text = (source == "emu" ? "EmuMovies" : "ExtendDB") + (string.IsNullOrEmpty(w.Region) ? "" : "  ·  " + w.Region), ForeColor = border, BackColor = Bg, Font = new Font("Segoe UI", 8f), AutoSize = false, AutoEllipsis = true };
        cap.SetBounds(S(4), MusThumbH + S(6), MusCellW - S(8), S(16)); cell.Controls.Add(cap);
        var info = new Label { Text = "download  ·  " + ext.TrimStart('.').ToUpperInvariant(), ForeColor = SubFg, BackColor = Bg, Font = new Font("Segoe UI", 7.5f), AutoSize = false };
        info.SetBounds(S(4), MusThumbH + S(24), MusCellW - S(8), S(16)); cell.Controls.Add(info);
        return cell;
    }

    /// <summary>Extension audio reelle d'une ligne web (meme heuristique que DocWebExt) ; .mp3 par defaut.</summary>
    private static string MusWebExt(MetadataDb.WebImage w)
    {
        string? cand = null;
        var m = System.Text.RegularExpressions.Regex.Match(w.FileName ?? "", @"[?&]filetype=([A-Za-z0-9]{2,5})\b");
        if (m.Success) cand = "." + m.Groups[1].Value.ToLowerInvariant();
        if (cand == null && !string.IsNullOrEmpty(w.FileType))
            cand = (w.FileType.StartsWith(".", StringComparison.Ordinal) ? w.FileType : "." + w.FileType).ToLowerInvariant();
        if (cand == null) { var e = Path.GetExtension(w.FileName ?? ""); if (!string.IsNullOrEmpty(e)) cand = e.ToLowerInvariant(); }
        return (cand != null && MusExts.Contains(cand)) ? cand : ".mp3";
    }

    private void MusDownloadWeb(MetadataDb.WebImage w, bool asMain)
    {
        if (_readOnly) return;
        var g = DocGame;
        string plat = Safe(() => g.Platform) ?? "", title = Safe(() => g.Title) ?? "";
        int dbId = Safe(() => g.LaunchBoxDbId) ?? -1;
        string root = DocLbRoot();
        if (root.Length == 0 || plat.Length == 0 || title.Length == 0)
        { MessageBox.Show(this, "This game has no platform / title — can't store managed music.", "LiteBox", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }

        byte[]? bytes;
        UseWaitCursor = true;
        try { bytes = ImgFetchWebBytes(w); } catch { bytes = null; } finally { UseWaitCursor = false; }
        if (bytes == null || bytes.Length == 0) { MessageBox.Show(this, "Download failed.", "LiteBox", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }

        string dest;
        try
        {
            dest = Media.ManualLibrary.FreeMusicDestination(root, plat, title, MusWebExt(w));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.WriteAllBytes(dest, bytes);
            try { ImageAdsWriter.WriteForDownload(dest, w, dbId, plat); } catch { }   // provenance ADS
        }
        catch (Exception ex) { MessageBox.Show(this, "Save failed:\n" + ex.Message, "LiteBox", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }

        if (asMain) MusDesignate(dest);
        MusRefresh();
    }

    // Meme cache que la page Documents (une requete EmuMovies sert les deux pages), mais le
    // rafraichissement vise CETTE page — avec garde IsDisposed.
    private void MusTriggerEmuFetch(IGame g, string key)
    {
        _docEmuCache[key] = null;
        string romPath = Safe(() => g.ApplicationPath) ?? "";
        string title = Safe(() => g.Title) ?? "";
        string plat = Safe(() => g.Platform) ?? "";
        System.Threading.Tasks.Task.Run(async () =>
        {
            List<EmuMoviesCatalog.EmuMedia> found = new();
            try { var api = EmuApi(); if (api != null) found = await EmuMoviesCatalog.ResolveForGameAsync(api, title, romPath, plat); }
            catch { }
            try { if (!IsDisposed && IsHandleCreated) BeginInvoke(new Action(() => { _docEmuCache[key] = found; MusRefresh(); })); }
            catch { }
        });
    }
}
