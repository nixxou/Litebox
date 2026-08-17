// The game right-click menu, shaped like LaunchBox's.
//
//   ▶ Play  ·  Play Version ▸  ·  Play ROM ▸  ·  Play With ▸  ·  Launch <emulator>  ·  Configure
//   ─────
//   Edit ▸  ·  Media ▸  ·  File Management ▸
//   ─────
//   Add
//
// Two things behave differently from the detail pane's launch group, on purpose:
//   • Play Version LAUNCHES the version. The right pane SELECTS one (two-step: pick, then Play);
//     here a menu entry is a verb, so picking a version runs it.
//   • Play ROM likewise arms the in-archive entry and launches in one click. The quick list is the
//     same one the ROM button builds (last launched, favourites, then priority order, then More…).
//
// A MULTI-selection gets LaunchBox's flat menu instead — no submenus, only what applies to a set.
//
// Icons are decoration: MenuIcons.Get returns null for an unknown name and a ToolStripMenuItem with
// a null Image simply renders without one. Nothing here depends on an icon existing.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using LbApiHost.Host.Data;
using LbApiHost.Host.Media;
using LbApiHost.Host.UiKit;
using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host;

internal sealed partial class MainWindow
{
    // Icons pulled from emulator executables (Launch <emu>), cached by path — the shipped set has no
    // per-emulator art, and an exe knows what it looks like.
    private static readonly Dictionary<string, Image> ExeIcons = new(StringComparer.OrdinalIgnoreCase);

    private ContextMenuStrip BuildGameContextMenu(IGame[] games)
    {
        var menu = new ContextMenuStrip { Renderer = new DarkRenderer(), BackColor = Panel2, ForeColor = Fg };
        if (games == null || games.Length == 0) return menu;

        // Admin actions are unavailable in read-only mode AND in parental LIMITED mode (locked): every
        // item already gated on `ro` (Edit/Delete/Add/Combine/Expand/Reset/playlist…) greys out at once.
        bool ro = ((_dm as HostDataManagerXml)?.ReadOnly ?? true) || Media.ParentalBridge.Active;

        if (games.Length == 1) BuildSingleGameMenu(menu, games[0], ro);
        else BuildMultiGameMenu(menu, games, ro);

        // Add — creates a game from nothing, so it belongs to both menus. The games it was invoked
        // from say which platform is meant, as long as they agree on one.
        string samePlatform = games.Select(g => S(Safe(() => g.Platform))).Distinct(StringComparer.OrdinalIgnoreCase)
                                   .Take(2).ToArray() is [var only] ? only : "";
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Item("Add", MenuIcons.Add, () => AddGameFromDraft(samePlatform), !ro));

        AddPluginItems(menu, games);
        return menu;
    }

    // ── the two shapes ───────────────────────────────────────────────────────
    private void BuildSingleGameMenu(ContextMenuStrip menu, IGame g, bool ro)
    {
        var play = new ToolStripMenuItem("Play") { Font = new Font(Font, FontStyle.Bold), Image = MenuIcons.Get(MenuIcons.Play) };
        play.Click += (_, _) => LaunchSelected();
        menu.Items.Add(play);

        var apps = SafeAddApps(g);
        if (apps.Length > 0)
        {
            var pv = new ToolStripMenuItem("Play Version") { Image = MenuIcons.Get(MenuIcons.AdditionalVersions) };
            foreach (var a in apps)
            {
                var ca = a;
                string cap = S(Safe(() => a.Name));
                var it = new ToolStripMenuItem(cap.Length > 0 ? cap : "(version)");
                it.Click += (_, _) => Safe(() =>
                {
                    string emuId = !string.IsNullOrEmpty(Safe(() => ca.EmulatorId)) ? ca.EmulatorId : g.EmulatorId;
                    var emu = _dm.GetEmulatorById(emuId);
                    PluginHelper.LaunchBoxMainViewModel.PlayGame(g, ca, emu, null);
                });
                pv.DropDownItems.Add(it);
            }
            menu.Items.Add(pv);
        }

        AddPlayRomItem(menu, g);

        var emus = SafeEmulatorsForPlatform(S(Safe(() => g.Platform)), g);
        if (emus.Count > 0)
        {
            var pw = new ToolStripMenuItem("Play With") { Image = MenuIcons.Get(MenuIcons.LaunchWith) };
            foreach (var e in emus)
            {
                var ce = e;
                var it = new ToolStripMenuItem(S(Safe(() => e.Title)));
                it.Click += (_, _) => Safe(() => PluginHelper.LaunchBoxMainViewModel.PlayGame(g, null, ce, null));
                pw.DropDownItems.Add(it);
            }
            menu.Items.Add(pw);
        }

        // One "Launch <emulator>" per emulator that can run this game — the emulator alone, no game,
        // no arguments (its own front-end / menu / config UI).
        foreach (var e in emus)
        {
            var ce = e;
            string exe = ResolveEmulatorExe(ce);
            string title = S(Safe(() => ce.Title));
            if (title.Length == 0) continue;
            var it = new ToolStripMenuItem("Launch " + title) { Image = ExeIcon(exe), Enabled = exe.Length > 0 && File.Exists(exe) };
            it.Click += (_, _) => LaunchEmulatorAlone(exe, title);
            menu.Items.Add(it);
        }

        if (!string.IsNullOrEmpty(S(Safe(() => g.ConfigurationPath))))
        {
            var cfg = new ToolStripMenuItem("Configure");
            cfg.Click += (_, _) => Safe(() => g.Configure());
            menu.Items.Add(cfg);
        }

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(BuildEditSubmenu(new[] { g }, ro));
        menu.Items.Add(BuildMediaSubmenu(g));
        menu.Items.Add(BuildFileSubmenu(g));
    }

    private void BuildMultiGameMenu(ContextMenuStrip menu, IGame[] games, bool ro)
    {
        foreach (var it in EditActions(games, ro)) menu.Items.Add(it);
        menu.Items.Add(RefreshImagesItem(games));
    }

    private ToolStripMenuItem BuildEditSubmenu(IGame[] games, bool ro)
    {
        var edit = new ToolStripMenuItem("Edit") { Image = MenuIcons.Get(MenuIcons.Edit) };
        foreach (var it in EditActions(games, ro)) edit.DropDownItems.Add(it);
        return edit;
    }

    /// <summary>The actions that change the games themselves — a submenu in single selection, the
    /// whole menu in multi.</summary>
    private List<ToolStripItem> EditActions(IGame[] games, bool ro)
    {
        var items = new List<ToolStripItem>();

        items.Add(Item(games.Length > 1 ? $"Edit {games.Length} Games…" : "Edit…", MenuIcons.Edit,
            () => OpenEditGame(games), !ro));

        items.Add(BuildPlaylistSubmenu(games, ro));

        // Nothing to reset when the whole selection is already at zero — the entry goes dead rather
        // than writing zeros over zeros. When there IS something, it is history that only ever grew:
        // the confirmation names what is about to be thrown away.
        int playCount = games.Sum(g => Safe(() => g.PlayCount));
        int playTime = games.Sum(g => Safe(() => g.PlayTime));
        items.Add(Item("Reset Play Count & Time", MenuIcons.ResetCounts, () =>
        {
            if (!ConfirmResetCounts(games, playCount, playTime)) return;
            foreach (var g in games)
            {
                if (Safe(() => g.PlayCount) != 0) Safe(() => g.PlayCount = 0);
                if (Safe(() => g.PlayTime) != 0) Safe(() => g.PlayTime = 0);
            }
            FlushAndReload();
        }, !ro && (playCount > 0 || playTime > 0)));

        bool anyPlayed = games.Any(g => Safe(() => g.LastPlayedDate).HasValue);
        items.Add(Item("Reset Last Played", MenuIcons.ResetLastPlayed, () =>
        {
            if (!ConfirmResetLastPlayed(games)) return;
            foreach (var g in games) Safe(() => g.LastPlayedDate = null);
            FlushAndReload();
        }, !ro && anyPlayed));

        // Combine needs at least two games and a root to fold them into; Expand needs a game that
        // actually carries versions. Both are hidden rather than shown dead when meaningless.
        if (games.Length > 1)
            items.Add(Item($"Combine {games.Length} Selected Games…", MenuIcons.Combine,
                () => CombineSelectedGames(games), !ro));

        var expandable = games.Where(Games.GameCombiner.CanExpand).ToArray();
        if (expandable.Length > 0)
            items.Add(Item(expandable.Length > 1 ? $"Expand {expandable.Length} Selected Games…" : "Expand Selected Game…",
                MenuIcons.Expand, () => ExpandSelectedGames(expandable), !ro));

        items.Add(Item("Delete", MenuIcons.Delete, () => DeleteGames(games), !ro));

        // Refresh Images is a MEDIA action: it lives under Media ▸ in single selection, and the flat
        // multi menu appends it itself — never here, or a single selection would show it twice.
        return items;
    }

    private ToolStripMenuItem BuildMediaSubmenu(IGame g) => Lazy("Media", MenuIcons.Media, media =>
    {
        var one = new[] { g };

        var images = GameImageFiles(g);
        media.DropDownItems.Add(Item("View Images…", MenuIcons.ViewImages, () => ViewImages(g), images.Count > 0));

        // Resolve answers for any titled game — HasArt is the question that matters: are the source
        // images the display rule demands actually on disk? Same test the media strip and the
        // fullscreen viewer use, so the entry is live exactly when the model can be shown.
        bool has3d = Safe(() => Model3d.Model3dCache.Resolve(g)) is { HasArt: true };
        media.DropDownItems.Add(Item("View 3D Box Model…", MenuIcons.View3dBox, () => OpenFullscreen3d(g), has3d));

        // Manual + documents open through DocOpener (shell, or LB 14's Reader when the
        // UseLbReaderForDocs ini key is on — one opener for every surface).
        string manual = S(Safe(() => g.GetManualPath()));
        media.DropDownItems.Add(Item("View Manual…", MenuIcons.ViewManual, () => Media.DocOpener.Open(manual),
            manual.Length > 0 && File.Exists(manual)));

        // LB parity (Media ▸, same slots): the game's Document records opened via the shell, and its
        // Link records opened in the browser — both routed by the EFFECTIVE section, exactly like the
        // v14 right-click menu (verified against the real install). Submenu disabled when empty.
        var docs = new List<Data.HostAdditionalApplication>();
        var links = new List<Data.HostAdditionalApplication>();
        try
        {
            foreach (var a in g.GetAllAdditionalApplications() ?? Array.Empty<IAdditionalApplication>())
            {
                if (a is not Data.HostAdditionalApplication h) continue;
                if (h.IsDocument) docs.Add(h);
                else if (h.IsLink) links.Add(h);
            }
        }
        catch { }
        var docsMenu = new ToolStripMenuItem("Additional Documents") { Image = MenuIcons.Get(MenuIcons.AdditionalDocuments), Enabled = docs.Count > 0 };
        foreach (var d in docs)
        {
            string abs = Safe(() => EditGameWindow.DocResolve(d.ApplicationPath)) ?? "";
            docsMenu.DropDownItems.Add(Item(S(Safe(() => d.Name)), null, () => Media.DocOpener.Open(abs),
                abs.Length > 0 && File.Exists(abs)));
        }
        media.DropDownItems.Add(docsMenu);
        var linksMenu = new ToolStripMenuItem("Links") { Image = MenuIcons.Get(MenuIcons.Link), Enabled = links.Count > 0 };
        foreach (var l in links)
        {
            string url = S(Safe(() => l.ApplicationPath));
            // Chain glyph now; the site's real favicon swaps in when its background fetch lands
            // (session-cached in memory only — see UiKit/LinkFavicon).
            var li = Item(S(Safe(() => l.Name)), MenuIcons.Link, () => ShellOpen(url), url.Length > 0);
            UiKit.LinkFavicon.Attach(li, url, this);
            linksMenu.DropDownItems.Add(li);
        }
        media.DropDownItems.Add(linksMenu);

        string music = S(Safe(() => g.GetMusicPath()));
        media.DropDownItems.Add(Item("Play Music", MenuIcons.PlayMusic, () => ShellOpen(music),
            music.Length > 0 && File.Exists(music)));

        // Flip Box turns the picture the detail pane is showing — it only means something when this
        // game IS the subject and it has both faces.
        string front = S(Safe(() => g.FrontImagePath)), back = S(Safe(() => g.BackImagePath));
        bool canFlip = ReferenceEquals(_detailsShown, g) && front.Length > 0 && back.Length > 0;
        media.DropDownItems.Add(Item("Flip Box", MenuIcons.FlipBox, () => FlipBox(front, back), canFlip));

        media.DropDownItems.Add(Item("Save Image As…", MenuIcons.SaveImageAs, () => SaveImageAs(g, images),
            images.Count > 0));

        media.DropDownItems.Add(RefreshImagesItem(one));
    });

    private ToolStripMenuItem BuildFileSubmenu(IGame g) => Lazy("File Management", MenuIcons.FileManagement, files =>
    {
        string app = ResolveGamePath(S(Safe(() => g.ApplicationPath)));
        files.DropDownItems.Add(Item("Open Game Folder", MenuIcons.OpenGameFolder, () => RevealInExplorer(app),
            app.Length > 0 && (File.Exists(app) || Directory.Exists(app))));

        var images = GameImageFiles(g);
        string imgTarget = images.Count > 0 ? images[0].path : PlatformImagesFolder(S(Safe(() => g.Platform)));
        files.DropDownItems.Add(Item("Open Images Folder", MenuIcons.OpenImagesFolder, () => RevealInExplorer(imgTarget),
            imgTarget.Length > 0 && (File.Exists(imgTarget) || Directory.Exists(imgTarget))));
    });

    /// <summary>A submenu whose contents are built the first time it opens. Right-clicking a game
    /// must not walk the image folders or crack open an archive — only asking for the submenu does.
    /// The placeholder child is what makes the arrow appear before there is anything behind it.</summary>
    private static ToolStripMenuItem Lazy(string text, string icon, Action<ToolStripMenuItem> fill)
    {
        var m = new ToolStripMenuItem(text) { Image = MenuIcons.Get(icon) };
        m.DropDownItems.Add(new ToolStripMenuItem("…") { Enabled = false });
        bool built = false;
        m.DropDownOpening += (_, _) =>
        {
            if (built) return;
            built = true;
            m.DropDownItems.Clear();
            try { fill(m); } catch (Exception ex) { Console.WriteLine("[gamemenu] " + ex.Message); }
            if (m.DropDownItems.Count == 0) m.DropDownItems.Add(new ToolStripMenuItem("(nothing here)") { Enabled = false });
        };
        return m;
    }

    // ── Play ROM ─────────────────────────────────────────────────────────────
    // Same quick list as the detail pane's ROM button (LaunchButtons.OnRomClick), except every entry
    // launches: the pick is armed for the single next launch, then the game starts.
    private const int RomMenuMax = 7;

    private void AddPlayRomItem(ContextMenuStrip menu, IGame g)
    {
        if (!Rom.RomExtractor.Available) return;
        if (!Rom.RomExtractor.IsArchive(S(Safe(() => g.ApplicationPath)))) return;

        var emu = Safe(() => _dm.GetEmulatorById(Safe(() => g.EmulatorId)));
        if (emu == null || !LaunchButtons.ResolveEffectiveAutoExtract(emu, S(Safe(() => g.Platform)))) return;

        // Listing means opening the archive — deferred to the moment the submenu is asked for.
        menu.Items.Add(Lazy("Play ROM", null, rom => FillRomMenu(rom, g)));
    }

    private void FillRomMenu(ToolStripMenuItem rom, IGame g)
    {
        var entries = Safe(() => Rom.RomExtractor.ListEntries(g, null));
        if (entries == null || entries.Count == 0) return;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // The label is the basename, except where the archive holds several files with the same one.
        var dup = entries.GroupBy(e => e.FileName, StringComparer.OrdinalIgnoreCase)
                         .Where(grp => grp.Count() > 1).Select(grp => grp.Key)
                         .ToHashSet(StringComparer.OrdinalIgnoreCase);

        void Push(Rom.RomEntryView e)
        {
            if (e == null || !seen.Add(e.PathInArchive)) return;
            string prefix = "";
            if (e.IsLastPlayed) prefix += "↻ ";
            if (e.IsFavorite) prefix += "★ ";
            var it = new ToolStripMenuItem(prefix + (dup.Contains(e.FileName) ? e.PathInArchive : e.FileName));
            string entry = e.PathInArchive;
            it.Click += (_, _) => PlayWithRom(g, entry);
            rom.DropDownItems.Add(it);
        }

        foreach (var e in entries.Where(e => e.IsLastPlayed)) Push(e);
        foreach (var e in entries.Where(e => e.IsFavorite)) Push(e);
        foreach (var e in entries)
        {
            if (seen.Count >= RomMenuMax + 2) break;
            Push(e);
        }

        if (entries.Count > seen.Count)
        {
            rom.DropDownItems.Add(new ToolStripSeparator());
            var more = new ToolStripMenuItem($"More…  ({entries.Count})");
            more.Click += (_, _) =>
            {
                var chosen = Safe(() => Rom.RomExtractor.PickRomModal(g, null));
                if (!string.IsNullOrEmpty(chosen)) PlayWithRom(g, chosen);
            };
            rom.DropDownItems.Add(more);
        }
    }

    /// <summary>Arms the in-archive entry for the single next launch (the extractor consumes it once,
    /// same call stack) and plays.</summary>
    private void PlayWithRom(IGame g, string entry)
    {
        Safe(() => Rom.RomLaunchPick.Arm(g, null, entry, false));
        Safe(() => PluginHelper.LaunchBoxMainViewModel.PlayGame(g, null, Safe(() => _dm.GetEmulatorById(g.EmulatorId)), null));
    }

    // ── Launch <emulator> ────────────────────────────────────────────────────
    private string ResolveEmulatorExe(IEmulator e)
    {
        string p = S(Safe(() => e.ApplicationPath));
        if (p.Length == 0) return "";
        try
        {
            if (!Path.IsPathRooted(p))
            {
                string root = MediaResolver.LbRoot;
                if (!string.IsNullOrEmpty(root)) p = Path.GetFullPath(Path.Combine(root, p));
            }
        }
        catch { }
        return p;
    }

    private void LaunchEmulatorAlone(string exe, string title)
    {
        if (exe.Length == 0 || !File.Exists(exe))
        {
            MessageBox.Show(this, $"{title} isn't where LaunchBox says it is:\n\n{exe}",
                "Launch " + title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't start {title}.\n\n{ex.Message}",
                "Launch " + title, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static Image ExeIcon(string exe)
    {
        if (string.IsNullOrEmpty(exe)) return null;
        if (ExeIcons.TryGetValue(exe, out var cached)) return cached;
        Image img = null;
        try
        {
            if (File.Exists(exe))
                using (var ico = Icon.ExtractAssociatedIcon(exe))
                    if (ico != null)
                    {
                        using var bmp = ico.ToBitmap();
                        var dst = new Bitmap(16, 16);
                        using (var gr = Graphics.FromImage(dst))
                        {
                            gr.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                            gr.DrawImage(bmp, new Rectangle(0, 0, 16, 16));
                        }
                        img = dst;
                    }
        }
        catch { }
        ExeIcons[exe] = img;
        return img;
    }

    // ── Add to Playlist ──────────────────────────────────────────────────────
    private ToolStripMenuItem BuildPlaylistSubmenu(IGame[] games, bool ro)
    {
        var root = new ToolStripMenuItem("Add to Playlist") { Image = MenuIcons.Get(MenuIcons.Playlist) };
        // Auto-populated playlists are defined by their rules, not by a hand-picked list: adding a
        // game to one would write a manual row the next rule pass ignores. They are left out.
        var playlists = SafePlaylists().OfType<HostPlaylist>()
                                       .Where(p => Safe(() => p.AutoPopulate) != true).ToList();
        if (playlists.Count == 0)
        {
            root.DropDownItems.Add(new ToolStripMenuItem("(no manual playlist)") { Enabled = false });
            return root;
        }

        var ids = games.Select(g => S(Safe(() => g.Id))).Where(s => s.Length > 0).ToArray();
        foreach (var pl in playlists.OrderBy(p => S(Safe(() => p.Name)), StringComparer.CurrentCultureIgnoreCase))
        {
            var cp = pl;
            var have = new HashSet<string>(GameIdsOf(cp), StringComparer.OrdinalIgnoreCase);
            bool all = ids.Length > 0 && ids.All(have.Contains);
            var it = new ToolStripMenuItem(S(Safe(() => cp.Name))) { Checked = all, Enabled = !ro && !all };
            it.Click += (_, _) => AddToPlaylist(cp, games);
            root.DropDownItems.Add(it);
        }
        return root;
    }

    private static IEnumerable<string> GameIdsOf(HostPlaylist pl)
    {
        var rows = Safe(() => pl.GetAllPlaylistGames());
        if (rows == null) yield break;
        foreach (var r in rows.OfType<HostPlaylistGame>())
            if (!string.IsNullOrEmpty(r.GameIdValue)) yield return r.GameIdValue;
    }

    private void AddToPlaylist(HostPlaylist pl, IGame[] games)
    {
        try
        {
            var rows = (pl.GetAllPlaylistGames() ?? Array.Empty<IPlaylistGame>())
                       .OfType<HostPlaylistGame>().OrderBy(r => r.ManualOrderValue).ToList();
            var have = new HashSet<string>(rows.Select(r => r.GameIdValue ?? ""), StringComparer.OrdinalIgnoreCase);
            int added = 0;
            foreach (var g in games)
            {
                string id = S(Safe(() => g.Id));
                if (id.Length == 0 || !have.Add(id)) continue;
                rows.Add(new HostPlaylistGame
                {
                    GameIdValue = id,
                    GameTitleValue = S(Safe(() => g.Title)),
                    GamePlatformValue = S(Safe(() => g.Platform)),
                    GameFileNameValue = S(Safe(() => g.ApplicationPath)),
                });
                added++;
            }
            if (added == 0) return;
            pl.ReplaceGames(rows);
            try { (_dm as HostDataManagerXml)?.FlushIfSafe(); } catch { }
            PopulateSources();   // the playlist node's count changed
        }
        catch (Exception ex) { Console.WriteLine("[gamemenu] playlist: " + ex.Message); }
    }

    // ── Delete ───────────────────────────────────────────────────────────────
    /// <summary>Deletes the entries, and whatever media the user ticked along with them. The plan is
    /// built first so the dialog can name the exact file counts — and it only ever counts files no
    /// other game would resolve.</summary>
    private void DeleteGames(IGame[] games)
    {
        if (Media.ParentalBridge.Active) return;   // limited mode
        if (_dm is not HostDataManagerXml dm || games.Length == 0) return;

        Games.GameMediaDeleter.Plan plan;
        try { Cursor = Cursors.WaitCursor; plan = Games.GameMediaDeleter.Build(games, dm); }
        finally { Cursor = Cursors.Default; }

        var files = Games.DeleteGameWindow.Ask(this, games, plan);
        if (files == null) return;   // cancelled

        // The platforms whose media cache this invalidates — read BEFORE the games go, since the
        // entries are what carries the platform name.
        var touched = games.Select(g => S(Safe(() => g.Platform))).Where(p => p.Length > 0)
                           .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        // Entries FIRST, flushed to the XML, and only then the files: a removal that failed leaves
        // a live game, and a live game keeps its media — the plan assumed every selected game was
        // going, so a partial removal invalidates it wholesale rather than per game.
        int failed = 0, done = 0;
        bool journalFaulted = false;
        try
        {
            Cursor = Cursors.WaitCursor;
            foreach (var g in games) if (Safe(() => dm.TryRemoveGame(g))) done++;
            if (done > 0) { try { dm.FlushIfSafe(); } catch { } }
            // La suppression des FICHIERS exige que la suppression des jeux ait une trace durable :
            // le journal (rejoué au prochain boot) suffit, le flush XML n'est qu'un rattrapage. Mais
            // un journal en panne (ouverture ou append raté — disque plein, DB corrompue) laisserait
            // les jeux revenir au redémarrage, leurs médias en moins. Effacer ne se répare pas :
            // dans ce cas les fichiers restent, et on le dit.
            journalFaulted = Safe(() => dm.Store?.JournalFaulted) ?? false;
            if (files.Count > 0 && done == games.Length && !journalFaulted)
                (_, failed) = Games.GameMediaDeleter.Delete(files);
        }
        finally { Cursor = Cursors.Default; }
        if (journalFaulted && files.Count > 0)
            MessageBox.Show(this,
                "The games were removed from this session, but the change journal could not record the "
                + "deletion (Core\\LiteBox.pending.db — disk full or damaged?).\n\n"
                + "The media files were NOT deleted: without a durable record, the games would come back "
                + "on the next start while their files would be gone.",
                "Delete", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        if (done > 0) ReloadAfterGameChange();

        // The game cache indexes files AND games, and both just changed under it: one de-duplicated
        // rebuild per touched platform, non-blocking — the same thing the image editor does when it
        // closes after touching media (EditGameWindowImages.OnFormClosed).
        if (done > 0 || files.Count > 0)
            foreach (var p in touched)
                Safe(() => GameCacheBridge.RebuildPlatform(PluginHelper.DataManager?.GetPlatformByName(p)));

        if (failed > 0)
            MessageBox.Show(this, $"{failed} file(s) could not be deleted — in use, or read-only.",
                "Delete", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    // ── Add (a draft, materialised only on Apply) ────────────────────────────
    /// <summary>Opens the editor on a game that does not exist yet. <paramref name="platform"/> is
    /// the platform of the game(s) the menu was opened on — adding from a game means "another one of
    /// these", so it wins; failing that, the tree node answers.</summary>
    private void AddGameFromDraft(string platform = "")
    {
        if (Media.ParentalBridge.Active) return;   // limited mode
        if (_dm is not HostDataManagerXml dm) return;
        // The tree fallback: categories and playlists also surface as IPlatform adapters, so they
        // are ruled out first (same order as LoadNode's dispatch) — their name is not a platform.
        if (platform.Length == 0)
            platform = _currentNode is not IPlatformCategory && _currentNode is not IPlaylist
                    && _currentNode is IPlatform p ? S(Safe(() => p.Name)) : "";
        var draft = new DraftGame(platform);

        var created = EditGameWindow.Open(new IGame[] { draft }, Array.Empty<IGame>(), false, this,
                                          null, d => DraftGame.Materialize(d, dm));
        if (created == null) return;    // cancelled before Apply — nothing was ever created
        FlushAndReload();
        try
        {
            var again = _current.FirstOrDefault(x => string.Equals(Safe(() => x.Id), Safe(() => created.Id), StringComparison.OrdinalIgnoreCase));
            if (again != null) { _games.SelectGame(again, true); ShowDetails(again); }
        }
        catch { }
    }

    // ── Media actions ────────────────────────────────────────────────────────
    private List<(string path, string type, string region)> GameImageFiles(IGame g)
    {
        try
        {
            string plat = S(Safe(() => g.Platform));
            string title = S(Safe(() => g.Title));
            if (plat.Length == 0 || !Guid.TryParse(S(Safe(() => g.Id)), out var id)) return new();
            return MediaResolver.AllImageFiles(plat, id, title) ?? new();
        }
        catch { return new(); }
    }

    /// <summary>The fullscreen image viewer over this game's images — for ANY game, not only the one
    /// the detail pane is showing (which is what OpenFullscreenImage covers).</summary>
    private void ViewImages(IGame g)
    {
        var paths = GameImageFiles(g).Select(x => x.path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (paths.Count == 0) return;
        // Start on what the detail pane has selected when it is this game — otherwise on the first.
        int start = 0;
        if (ReferenceEquals(_detailsShown, g) && _mediaItems != null && _mediaSel >= 0 && _mediaSel < _mediaItems.Count)
        {
            int ix = paths.FindIndex(p => string.Equals(p, _mediaItems[_mediaSel], StringComparison.OrdinalIgnoreCase));
            if (ix >= 0) start = ix;
        }
        using var v = new FullscreenImageViewer(paths, start, LoadImage);
        v.ShowDialog(this);
    }

    /// <summary>Shows the other face of the box in the detail pane: front → back, back → front.</summary>
    private void FlipBox(string front, string back)
    {
        string cur = _mediaItems != null && _mediaSel >= 0 && _mediaSel < _mediaItems.Count ? _mediaItems[_mediaSel] : "";
        string want = string.Equals(cur, front, StringComparison.OrdinalIgnoreCase) ? back : front;
        if (want.Length == 0) return;
        SetMainMedia(want, full: true, _detailsLoadToken);
    }

    private void SaveImageAs(IGame g, List<(string path, string type, string region)> images)
    {
        // The picture on screen when this game is the subject, else its front box art.
        string src = "";
        if (ReferenceEquals(_detailsShown, g) && _mediaItems != null && _mediaSel >= 0 && _mediaSel < _mediaItems.Count
            && File.Exists(_mediaItems[_mediaSel]))
            src = _mediaItems[_mediaSel];
        if (src.Length == 0) src = S(Safe(() => g.FrontImagePath));
        if (src.Length == 0 && images.Count > 0) src = images[0].path;
        if (src.Length == 0 || !File.Exists(src)) return;

        string type = images.FirstOrDefault(x => string.Equals(x.path, src, StringComparison.OrdinalIgnoreCase)).type ?? "";
        string ext = Path.GetExtension(src);
        string name = Sanitize(S(Safe(() => g.Title)) + (type.Length > 0 ? " - " + type : "")) + ext;

        using var dlg = new SaveFileDialog
        {
            Title = "Save Image As",
            FileName = name,
            Filter = $"Image (*{ext})|*{ext}|All files (*.*)|*.*",
            OverwritePrompt = true,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try { File.Copy(src, dlg.FileName, true); }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Couldn't save the image.\n\n" + ex.Message, "Save Image As",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // Same flow as View ▸ Media ▸ "Generate Image Cache (Selected Games)..." — the options dialog +
    // phased run, scoped to this menu's games. (It replaced the old whole-platform data-cache rebuild;
    // the label follows this menu's count convention, like "Edit N Games…".)
    private ToolStripItem RefreshImagesItem(IGame[] games) => Item(
        games.Length > 1 ? $"Generate Image Cache ({games.Length} Games)..." : "Generate Image Cache...",
        MenuIcons.RefreshImages, () => GenerateCachedImages(games));

    // ── File management ──────────────────────────────────────────────────────
    private static string ResolveGamePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        try
        {
            if (Path.IsPathRooted(path)) return path;
            string root = MediaResolver.LbRoot;
            return string.IsNullOrEmpty(root) ? path : Path.GetFullPath(Path.Combine(root, path));
        }
        catch { return path; }
    }

    private static string PlatformImagesFolder(string platform)
    {
        try
        {
            string root = MediaResolver.ImagesRoot;
            if (string.IsNullOrEmpty(root)) return "";
            string dir = platform.Length > 0 ? Path.Combine(root, Sanitize(platform)) : root;
            return Directory.Exists(dir) ? dir : (Directory.Exists(root) ? root : "");
        }
        catch { return ""; }
    }

    /// <summary>Opens Explorer with the file selected, or the folder itself when handed a directory.</summary>
    private static void RevealInExplorer(string target)
    {
        try
        {
            if (File.Exists(target))
                Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + target + "\"") { UseShellExecute = true });
            else if (Directory.Exists(target))
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + target + "\"") { UseShellExecute = true });
            else
            {
                string dir = Path.GetDirectoryName(target) ?? "";
                if (Directory.Exists(dir))
                    Process.Start(new ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = true });
            }
        }
        catch (Exception ex) { Console.WriteLine("[gamemenu] explorer: " + ex.Message); }
    }

    private static void ShellOpen(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { Console.WriteLine("[gamemenu] open: " + ex.Message); }
    }

    private static string Sanitize(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Trim();
    }

    // ── Confirmations ────────────────────────────────────────────────────────
    /// <summary>Confirms a play count / play time reset, spelling out the values it destroys.
    /// Games already at zero are counted out of the total so the question matches what will change.</summary>
    private bool ConfirmResetCounts(IGame[] games, int playCount, int playTime)
    {
        static string Time(int seconds) => seconds > 0 ? FormatPlayTime(seconds) : "0";

        int affected = games.Count(g => Safe(() => g.PlayCount) != 0 || Safe(() => g.PlayTime) != 0);
        return MessageBox.Show(this,
            $"Reset the play count and play time of {Subject(games, affected)}?\n\n"
            + $"Play count: {playCount} → 0\n"
            + $"Play time: {Time(playTime)} → 0",
            "Reset Play Count & Time", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;
    }

    private bool ConfirmResetLastPlayed(IGame[] games)
    {
        var dates = games.Select(g => Safe(() => g.LastPlayedDate)).Where(d => d.HasValue).ToArray();
        int affected = dates.Length;
        string was = games.Length == 1 && affected == 1
            ? $"\n\nLast played: {dates[0]!.Value:g} → never"
            : $"\n\nTheir last-played date goes back to never.";
        return MessageBox.Show(this,
            $"Clear the last-played date of {Subject(games, affected)}?" + was,
            "Reset Last Played", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;
    }

    private static string Subject(IGame[] games, int affected)
        => games.Length == 1 ? $"\"{S(Safe(() => games[0].Title))}\""
         : affected < games.Length ? $"{affected} of the {games.Length} selected games"
         : $"the {games.Length} selected games";

    // ── Plumbing ─────────────────────────────────────────────────────────────
    private ToolStripMenuItem Item(string text, string icon, Action run, bool enabled = true)
    {
        var it = new ToolStripMenuItem(text) { Image = MenuIcons.Get(icon), Enabled = enabled };
        it.Click += (_, _) => { try { run(); } catch (Exception ex) { Console.WriteLine("[gamemenu] " + ex.Message); } };
        return it;
    }

    private void FlushAndReload()
    {
        try { (_dm as HostDataManagerXml)?.FlushIfSafe(); } catch { }
        ReloadAfterGameChange();
    }

    /// <summary>The plugin-contributed entries, appended after LiteBox's own (unchanged behaviour).</summary>
    private void AddPluginItems(ContextMenuStrip menu, IGame[] games)
    {
        var plugin = new List<ToolStripItem>();

        foreach (var gm in _reg.GameMenus)
        {
            bool valid, show; string cap;
            try
            {
                cap = gm.Caption;
                show = gm.ShowInLaunchBox;
                valid = games.Length == 1 ? gm.GetIsValidForGame(games[0])
                                          : (gm.SupportsMultipleGames && gm.GetIsValidForGames(games));
            }
            catch { continue; }
            if (!show || !valid) continue;

            var captured = gm; var gs = games;
            var it = new ToolStripMenuItem(cap);
            it.Click += (_, _) => Safe(() =>
            {
                if (gs.Length == 1) captured.OnSelected(gs[0]); else captured.OnSelected(gs);
            });
            plugin.Add(it);
        }

        foreach (var gmm in _reg.GameMultiMenus)
        {
            IEnumerable<IGameMenuItem> items;
            try { items = gmm.GetMenuItems(games); } catch { continue; }
            if (items == null) continue;
            foreach (var mi in items) plugin.Add(BuildGameMenuItem(mi, games));
        }

        if (plugin.Count == 0) return;   // no plugin entries → no dangling separator
        menu.Items.Add(new ToolStripSeparator());
        foreach (var it in plugin) menu.Items.Add(it);
    }
}
