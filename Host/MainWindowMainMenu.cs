// The top menu bar, shaped like LaunchBox's desktop menu.
//
//   MENU  TOOLS  VIEW  ARRANGE BY  IMAGE GROUP  BADGES  |  Displaying 29 of 5075 total games.
//
// The tree, the labels, the check marks and the icons are LaunchBox's. Entries go live one at a
// time (MENU's Big Box / Achievements / Quit are wired; the rest is still inert), and the shortcuts
// LaunchBox shows are left out entirely (they would advertise keys that do nothing). The existing
// toolbar keeps driving the features it already owns (Arrange By, Image Group, Emulators, Options…);
// this bar will take them over one at a time.
//
// Labels are LaunchBox's own, read out of its localized string table (Label*Menu keys) so the
// wording matches exactly — minus the '_' mnemonics, which WinForms would turn into '&' accelerators.
//
// Icons are decoration: MenuIcons.Get returns null for a name whose PNG has not been drawn yet, and
// a ToolStripMenuItem with a null Image simply renders without one.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using LbApiHost.Host.Data;
using LbApiHost.Host.Media;
using LbApiHost.Host.Modules;
using LbApiHost.Host.UiKit;
using LbApiHost.Host.Web;
using LbApiHost.Host.Web.Kiosk;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host;

internal sealed partial class MainWindow
{
    // "Displaying N of M total games." — LaunchBox puts the count in the menu bar, not the toolbar.
    private ToolStripLabel _menuStatus;

    // The notification bell (far right of the bar, like LaunchBox's): unread badge + the drop-down list.
    private Notifications.NotificationBell _bell;

    private MenuStrip BuildMainMenu()
    {
        var menu = new MenuStrip
        {
            Dock = DockStyle.Top, BackColor = Panel2, ForeColor = Fg,
            Renderer = new DarkRenderer(), Padding = new Padding(6, 2, 6, 2),
        };

        var bigBox = M("Big Box...", MenuIcons.BigBox);
        bigBox.Click += (_, _) => Safe(OpenBigBoxKiosk);
        var achievements = M("View Achievements Profile...", MenuIcons.Trophy);
        achievements.Click += (_, _) => Safe(OpenAchievementsProfile);
        var quit = M("Quit LiteBox", MenuIcons.Exit);
        quit.Click += (_, _) => Close();   // the FormClosing chain saves layout/selection and flushes

        menu.Items.Add(Top("Menu",
            bigBox,
            achievements,
            ViewMenu("View"),
            ToolsMenu("Tools"),
            HelpMenu("Help"),
            Sep(),
            quit));

        // The bar repeats the submenus LaunchBox considers worth one click.
        menu.Items.Add(Top("Tools", Children(ToolsMenu("Tools"))));
        menu.Items.Add(Top("View", Children(ViewMenu("View"))));
        // Arrange By fills itself on open (live catalog), so the bar entry is wired, not cloned.
        var arrangeTop = Top("Arrange By");
        WireArrangeDropDown(arrangeTop);
        menu.Items.Add(arrangeTop);
        menu.Items.Add(Top("Image Group", Children(ImageGroupMenu("Image Group"))));
        menu.Items.Add(Top("Badges", Children(BadgesMenu("Badges"))));

        menu.Items.Add(new ToolStripSeparator());
        _menuStatus = new ToolStripLabel("") { ForeColor = SubFg, Margin = new Padding(6, 0, 0, 0) };
        menu.Items.Add(_menuStatus);
        UpdateMenuStatus();

        // Far right: the bell. Right-aligned, so it stays in the corner however wide the bar's own items get.
        _bell = new Notifications.NotificationBell(this, menu);
        menu.Items.Add(_bell.Item);

        // The achievement points, to the bell's LEFT. Right-aligned items are placed from the right edge
        // inward IN ADD ORDER, so the first one added takes the corner — this one has to come AFTER the
        // bell to sit beside it, not before. (Same ordering as the toolbar's ExtendDB indicator.)
        _raPoints = new ToolStripLabel("")
        {
            ForeColor = SubFg, Alignment = ToolStripItemAlignment.Right, Visible = false,
            Margin = new Padding(0, 0, 10, 0),
            ToolTipText = "Your RetroAchievements profile",
            // The label is user-written, so its width is unknown until it is filled: AutoSize measures the
            // text it actually got. Overflow.Never keeps it on the bar rather than letting a long template
            // push it into the chevron menu, where an indicator is no indicator at all.
            AutoSize = true, Overflow = ToolStripItemOverflow.Never,
        };
        _raPoints.Click += (_, _) => Safe(OpenAchievementsProfile);
        menu.Items.Add(_raPoints);

        StartAchievementPoints();
        return menu;
    }

    // ── "HARDCORE POINTS: 30" ────────────────────────────────────────────────
    // LaunchBox keeps the number in its menu bar and opens the RetroAchievements window from it.
    // The label paints from the CACHE, so it is there the moment the bar is built; the network
    // refresh runs behind it and re-labels when it lands (or never, and the cached number stands).

    private ToolStripLabel _raPoints;

    private void StartAchievementPoints()
    {
        if (!Ra.RaProfileService.Configured) return;      // no RA account → no indicator at all
        UpdateAchievementPoints();
        Ra.RaProfileService.Changed += OnAchievementPointsChanged;
        FormClosed += (_, _) => Ra.RaProfileService.Changed -= OnAchievementPointsChanged;
        // Only if the cache has aged out: restarting LiteBox twice in a minute must not spend five
        // requests re-fetching numbers that cannot have moved.
        System.Threading.Tasks.Task.Run(() => { try { Ra.RaProfileService.RefreshIfStale(); } catch { } });
    }

    private void OnAchievementPointsChanged()
    {
        // Raised on the fetching thread.
        try
        {
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke((Action)(() => { if (!IsDisposed) UpdateAchievementPoints(); }));
        }
        catch { }
    }

    /// <summary>The RetroAchievements window. It gets the same three hooks RELATED GAMES has — the library
    /// to match its recent games against, the cover resolver, and the "select this game" bridge — so a
    /// recent game we actually own is clickable there exactly as a suggestion card is in the detail pane.
    /// The click brings LiteBox back to the front — SelectGameById raises the window itself — but leaves
    /// the RetroAchievements window open behind it, so a second game is one click away, not a reopen.</summary>
    private void OpenAchievementsProfile()
    {
        Ra.RaProfileWindow.Open(this,
            () => Safe(() => (IReadOnlyList<IGame>)(_dm?.GetAllGames() ?? Array.Empty<IGame>())) ?? Array.Empty<IGame>(),
            RelatedLocalArt,
            id => { try { SelectGameById(id); } catch { } });
    }

    /// <summary>A game just exited — if it was one with achievements, the points on the bar are almost
    /// certainly stale, so refetch. Called from OnGameEnded.
    ///
    /// Refresh, NOT RefreshIfStale: the 30-minute TTL exists to stop pointless traffic at boot, and this
    /// is the one moment we have positive reason to believe the numbers moved. Games with no RA id are
    /// skipped outright — nothing can have changed, and a five-request round trip for every launch of
    /// every unscored game in the library is exactly the traffic the TTL was protecting.</summary>
    private void RefreshAchievementPointsAfterGame(IGame game)
    {
        if (!Ra.RaProfileService.Configured) return;
        if (Ra.RaFields.Raid(game) <= 0) return;
        System.Threading.Tasks.Task.Run(() => { try { Ra.RaProfileService.Refresh(); } catch { } });
    }

    /// <summary>The default label — LaunchBox's wording, as a template.</summary>
    internal const string RaPointsDefault = "HARDCORE POINTS: %HP";

    /// <summary>The tokens the label understands, in the order the help lists them.</summary>
    private static readonly (string token, string what)[] RaPointsTokens =
    {
        ("%HP",   "hardcore points"),
        ("%RP",   "RetroPoints (the white ones)"),
        ("%SP",   "softcore points"),
        ("%AU",   "achievements unlocked"),
        ("%GB",   "games beaten"),
        ("%RANK", "site rank (blank while RA hasn't ranked the account)"),
        ("%USER", "RetroAchievements username"),
    };

    private void UpdateAchievementPoints()
    {
        if (_raPoints == null) return;
        var p = Ra.RaProfileService.Cached();
        if (p == null) { _raPoints.Visible = false; return; }   // nothing fetched yet — no empty label
        string text = RenderRaPoints(_cfg.Get("AchievementsBarLabel", RaPointsDefault), p);
        // An empty template is how you turn the indicator off, rather than needing a separate toggle.
        _raPoints.Text = text;
        _raPoints.Visible = text.Length > 0;
    }

    /// <summary>Substitute the tokens in the user's template. Longest token first, so %RANK is not eaten
    /// by %R-something; numbers get thousands separators, and an absent rank renders empty rather than 0.</summary>
    internal static string RenderRaPoints(string? template, Ra.RaProfile p)
    {
        string s = template ?? "";
        if (s.Length == 0) return "";
        s = s.Replace("%RANK", p.rank?.ToString("N0") ?? "")
             .Replace("%USER", p.user ?? "")
             .Replace("%HP", p.hardcorePoints.ToString("N0"))
             .Replace("%RP", p.retroPoints.ToString("N0"))
             .Replace("%SP", p.softcorePoints.ToString("N0"))
             .Replace("%AU", p.achievementsUnlocked.ToString("N0"))
             .Replace("%GB", p.gamesBeaten.ToString("N0"));
        return s.Trim();
    }

    /// <summary>The Display option for the menu-bar label. Lives with the other Display options so it is
    /// edited where the rest of the bar's appearance is.</summary>
    internal Options.OptionItem AchievementPointsOption()
        => Options.OptionItem.Text("Display", "Achievements: menu bar label",
            () => _cfg.Get("AchievementsBarLabel", RaPointsDefault),
            v => _cfg.Set("AchievementsBarLabel", v),
            "What the RetroAchievements indicator writes in the menu bar. Default \"" + RaPointsDefault
            + "\". Tokens: " + string.Join(", ", RaPointsTokens.Select(t => t.token + " = " + t.what))
            + ". Anything else is written as-is, so \"%HP hardcore / %SP softcore\" works. The label sizes "
            + "itself to whatever it ends up saying. Empty hides the indicator; the profile window is still "
            + "reachable from MENU ▸ View Achievements Profile. Only shown when a RetroAchievements account "
            + "is configured.",
            applyLive: UpdateAchievementPoints);

    /// <summary>"Big Box..." — the fullscreen BigBox Web kiosk. With ExtendDB loaded its own kiosk wins
    /// (same rule as the F11 hotkey); otherwise the host kiosk runs on the embedded web server, which
    /// lives behind the Web frontends module — so an OFF module asks to be enabled first (yes/no), and
    /// the server is started on demand exactly like ModulesOptions.ReconcileRuntime does.</summary>
    private void OpenBigBoxKiosk()
    {
        if (KioskBridge.Available) { KioskBridge.ToggleBigBox(); return; }

        if (!LbModules.On(LbModule.Web))
        {
            var r = MessageBox.Show(this,
                "Big Box runs on the embedded web server, and the Web frontends module is currently disabled.\n\n" +
                "Enable the Web frontends module now?",
                "Big Box", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes) return;
            LbModules.SetOn(LbModule.Web, true);
        }

        if (!EmbeddedWebServer.IsRunning)
        {
            try { WebAssets.EnsureDeployed(); } catch { }
            int port = int.TryParse(LiteBoxConfig.LoadForExe().GetSec("Web", "Port"), out var p) ? p : 8080;
            EmbeddedWebServer.Start(port);
        }
        if (!EmbeddedWebServer.IsRunning)
        {
            MessageBox.Show(this, "The embedded web server failed to start — see the log for details.",
                "Big Box", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (!WebKioskWindow.IsAvailable())
        {
            MessageBox.Show(this, "The WebView2 runtime is not available on this system, so the kiosk window cannot open.",
                "Big Box", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        WebKioskWindow.ToggleBigBox();
    }

    /// <summary>The count LaunchBox shows in its menu bar. Called whenever the visible set changes.</summary>
    private void UpdateMenuStatus()
    {
        if (_menuStatus == null) return;
        try { _menuStatus.Text = $"Displaying {_games.VisibleGames.Count} of {_games.TotalCount} total games."; }
        catch { _menuStatus.Text = ""; }
    }

    // ── The submenus ─────────────────────────────────────────────────────────

    // Every built instance of the view-switch pair (the tree exists twice: MENU ▸ View and the bar's
    // VIEW) — SyncViewSwitchChecks stamps the active mode on all of them at once.
    private readonly List<ToolStripMenuItem> _miImagesView = new();
    private readonly List<ToolStripMenuItem> _miListView = new();

    /// <summary>Route a menu view switch through the toolbar's List/Poster toggle when it exists, so
    /// the button's checked state (and its "view you'd switch TO" label) stays truthful. The button's
    /// CheckedChanged then drives SetPosterMode; a same-value write fires nothing, matching the
    /// SetPosterMode no-op guard.</summary>
    private void SwitchView(bool poster)
    {
        if (_posterBtn != null) _posterBtn.Checked = poster;
        else SetPosterMode(poster);
    }

    /// <summary>Reflect the active view on every Images View / List View menu entry (called by
    /// SetPosterMode, and safe before the menu exists).</summary>
    private void SyncViewSwitchChecks()
    {
        foreach (var it in _miImagesView) it.Checked = _posterMode;
        foreach (var it in _miListView) it.Checked = !_posterMode;
    }

    // ── Hide Games — LaunchBox's Settings.xml rules, shared both ways ────────
    // The 7 entries map onto the exact keys LaunchBox persists, so a box ticked here is a box
    // ticked in LaunchBox and vice-versa. The two marked-* keys are stored INVERTED (LB models
    // them as Show*), the five missing-media keys are stored as-is.
    private static readonly (string Label, string Key, bool Inverted)[] HideGamesRules =
    {
        ("Marked Hidden", "ShowHiddenGames", true),
        ("Marked Broken", "ShowBrokenGames", true),
        ("Missing Videos", "HideGamesMissingVideos", false),
        ("Missing Box Front Image", "HideGamesMissingBoxFrontImage", false),
        ("Missing Screenshot Image", "HideGamesMissingScreenshotImage", false),
        ("Missing Clear Logo Image", "HideGamesMissingClearLogoImage", false),
        ("Missing Background Image", "HideGamesMissingBackgroundImage", false),
    };

    private readonly List<(ToolStripMenuItem Item, string Key, bool Inverted)> _miHideGames = new();

    private LbApiHost.Host.Data.LbSettingsStore LbSettings => (_dm as Data.HostDataManagerXml)?.LbSettings;

    /// <summary>True when the RULE is active (those games are hidden). LB defaults: marked
    /// hidden/broken games hidden, missing-media rules off.</summary>
    private bool HideGameRuleOn(string key, bool inverted)
    {
        bool v = LbSettings?.GetBool(key, false) ?? false;
        return inverted ? !v : v;
    }

    private void ToggleHideGameRule(string key, bool inverted)
    {
        var s = LbSettings;
        if (s == null || !s.Loaded) return;
        if ((_dm as Data.HostDataManagerXml)?.ReadOnly ?? true) return;   // options are locked in read-only mode
        bool newOn = !HideGameRuleOn(key, inverted);
        s.SetBool(key, inverted ? !newOn : newOn);
        foreach (var (it, k, inv) in _miHideGames) if (k == key) it.Checked = HideGameRuleOn(k, inv);
        ApplyFilter();                                            // the list reflects the rule immediately
        (_dm as Data.HostDataManagerXml)?.FlushLbSettingsIfSafe();   // → Settings.xml now (when LB/BB closed)
    }

    /// <summary>The predicate ApplyFilter ANDs into the view, or null when every rule is off.
    /// Flag reads are cheap; the media probes only run for rules that are actually on.</summary>
    private Func<IGame, bool> HideGamesFilterOrNull()
    {
        var s = LbSettings;
        if (s == null || !s.Loaded) return null;
        bool hideHidden = !s.GetBool("ShowHiddenGames", false);
        bool hideBroken = !s.GetBool("ShowBrokenGames", false);
        bool exVideo = s.GetBool("HideGamesMissingVideos", false);
        bool exFront = s.GetBool("HideGamesMissingBoxFrontImage", false);
        bool exShot  = s.GetBool("HideGamesMissingScreenshotImage", false);
        bool exLogo  = s.GetBool("HideGamesMissingClearLogoImage", false);
        bool exBg    = s.GetBool("HideGamesMissingBackgroundImage", false);
        if (!hideHidden && !hideBroken && !exVideo && !exFront && !exShot && !exLogo && !exBg) return null;
        return g =>
        {
            if (hideHidden && Safe(() => g.Hide)) return false;
            if (hideBroken && Safe(() => g.Broken)) return false;
            if (exVideo && string.IsNullOrEmpty(Safe(() => g.GetVideoPath(false)))) return false;
            if (exFront && string.IsNullOrEmpty(Safe(() => g.FrontImagePath))) return false;
            if (exShot && string.IsNullOrEmpty(Safe(() => g.ScreenshotImagePath))) return false;
            if (exLogo && string.IsNullOrEmpty(Safe(() => g.ClearLogoImagePath))) return false;
            if (exBg && string.IsNullOrEmpty(Safe(() => g.BackgroundImagePath))) return false;
            return true;
        };
    }

    private ToolStripMenuItem HideGamesMenu()
    {
        var sub = new ToolStripMenuItem("Hide Games") { Image = MenuIcons.Get(MenuIcons.HideGames) };
        foreach (var (label, key, inverted) in HideGamesRules)
        {
            if (key == "HideGamesMissingVideos") sub.DropDownItems.Add(Sep());   // LB's gap: marked-* | missing-*
            var it = new ToolStripMenuItem(label) { Checked = HideGameRuleOn(key, inverted) };
            it.Click += (_, _) => Safe(() => ToggleHideGameRule(key, inverted));
            _miHideGames.Add((it, key, inverted));
            sub.DropDownItems.Add(it);
        }
        return sub;
    }

    // ── Media — autoplay toggles + cache refreshes ───────────────────────────
    // Auto-Play Videos is the SAME option as Options ▸ Right panel ▸ "Videos: play automatically
    // when selected" (LiteBox.ini VideoAutoplay) — one flag, two surfaces. The two music toggles are
    // LaunchBox's AutoPlayMusic / ShuffleMusic Settings.xml keys (shared both ways, like Hide Games).
    // Check marks are re-read when the submenu opens, so a change made in the options window (or in
    // LaunchBox) is always reflected.
    private readonly List<(ToolStripMenuItem Item, string Key)> _miMedia = new();

    private bool MediaToggleOn(string key) => key switch
    {
        "autovideo" => _cfg.VideoAutoplay,
        "automusic" => LbSettings?.GetBool("AutoPlayMusic", true) ?? true,
        "shuffle"   => LbSettings?.GetBool("ShuffleMusic", true) ?? true,
        _ => false,
    };

    private void RefreshMediaChecks()
    {
        foreach (var (it, k) in _miMedia) it.Checked = MediaToggleOn(k);
    }

    private void ToggleMediaOption(string key)
    {
        if ((_dm as Data.HostDataManagerXml)?.ReadOnly ?? true) return;   // options are locked in read-only mode
        bool on = !MediaToggleOn(key);
        switch (key)
        {
            case "autovideo":
                _cfg.VideoAutoplay = on;
                try { _cfg.Save(); } catch { }
                if (_mediaVideo != null) _mediaVideo.Autoplay = on;       // live, like the option's applyLive
                break;
            case "automusic":
                LbSettings?.SetBool("AutoPlayMusic", on);
                (_dm as Data.HostDataManagerXml)?.FlushLbSettingsIfSafe();
                if (on) UpdateGameMusic(_detailsShown as IGame); else Media.GameMusicPlayer.Stop();
                break;
            case "shuffle":
                LbSettings?.SetBool("ShuffleMusic", on);
                (_dm as Data.HostDataManagerXml)?.FlushLbSettingsIfSafe();
                UpdateGameMusic(_detailsShown as IGame);                  // re-hand the list with the new flag
                break;
        }
        RefreshMediaChecks();
    }

    private ToolStripMenuItem MediaMenu()
    {
        var sub = new ToolStripMenuItem("Media") { Image = MenuIcons.Get(MenuIcons.Media) };
        ToolStripMenuItem Toggle(string label, string key)
        {
            var it = new ToolStripMenuItem(label) { Checked = MediaToggleOn(key) };
            it.Click += (_, _) => Safe(() => ToggleMediaOption(key));
            _miMedia.Add((it, key));
            return it;
        }
        sub.DropDownItems.Add(Toggle("Auto-Play Videos", "autovideo"));
        sub.DropDownItems.Add(Toggle("Auto-Play Music", "automusic"));
        sub.DropDownItems.Add(Toggle("Shuffle Music", "shuffle"));
        sub.DropDownItems.Add(Sep());

        // Named for what they actually DO here (LaunchBox says "Refresh …", but our action is the
        // thumbnail-cache generation run): the same flow as the toolbar's Generate Image Cache,
        // scoped to the selection or the whole library.
        var refreshSel = new ToolStripMenuItem("Generate Image Cache (Selected Games)...") { Image = MenuIcons.Get(MenuIcons.Refresh) };
        refreshSel.Click += (_, _) => Safe(() => GenerateCachedImages(_games?.SelectedGames));
        sub.DropDownItems.Add(refreshSel);
        var refreshAll = new ToolStripMenuItem("Generate Image Cache (All Games)...") { Image = MenuIcons.Get(MenuIcons.RefreshImages) };
        refreshAll.Click += (_, _) => Safe(GenerateAllCachedImages);
        sub.DropDownItems.Add(refreshAll);
        sub.DropDownItems.Add(Sep());

        // The GAME IMAGE CACHE (Host/Gc — the media-file index, not the thumbnails above): inspect
        // it, or re-scan it when files changed behind LiteBox's back.
        var gcViewer = new ToolStripMenuItem("Game Image Cache Viewer...") { Image = MenuIcons.Get(MenuIcons.Audit) };
        gcViewer.Click += (_, _) => Safe(OpenGameImageCacheViewer);
        sub.DropDownItems.Add(gcViewer);
        var gcCur = new ToolStripMenuItem("Rebuild Game Image Cache (Current Platform)")
        { Image = MenuIcons.Get(MenuIcons.Refresh), ToolTipText = "Re-scan the media index of the selected games' platforms (or the selected platform node)" };
        gcCur.Click += (_, _) => Safe(() => RebuildGameImageCache(all: false));
        sub.DropDownItems.Add(gcCur);
        var gcAll = new ToolStripMenuItem("Rebuild Game Image Cache (All Platforms)")
        { Image = MenuIcons.Get(MenuIcons.RefreshImages), ToolTipText = "Re-scan the media index of every platform" };
        gcAll.Click += (_, _) => Safe(() => RebuildGameImageCache(all: true));
        sub.DropDownItems.Add(gcAll);

        sub.DropDownOpening += (_, _) =>
        {
            RefreshMediaChecks();
            refreshSel.Enabled = (_games?.SelectedGames?.Length ?? 0) > 0;
            bool gcOn = Gc.HostGameCache.Enabled;
            gcCur.Enabled = gcOn && CachePlatformTargets().Count > 0;
            gcAll.Enabled = gcOn;   // the viewer stays enabled — its status line explains an OFF cache
        };
        return sub;
    }

    // ── Game Image Cache (the Gc media-file index) ───────────────────────────

    private Gc.GameImageCacheViewer _gcViewerLive;

    /// <summary>Open (or re-focus) the non-modal cache viewer — single live instance.</summary>
    private void OpenGameImageCacheViewer()
    {
        if (_gcViewerLive is { IsDisposed: false } live) { try { live.Activate(); } catch { } return; }
        var v = new Gc.GameImageCacheViewer();
        _gcViewerLive = v;
        v.FormClosed += (_, _) => _gcViewerLive = null;
        v.Show(this);
    }

    /// <summary>The platforms a "Current Platform" rebuild targets: the selected games' platforms
    /// when there is a selection, else the platform node the tree is on (a category/playlist/All
    /// node without a selection targets nothing — the entry greys out).</summary>
    private List<string> CachePlatformTargets()
    {
        var res = (_games?.SelectedGames ?? Array.Empty<IGame>())
            .Select(g => Safe(() => g.Platform) ?? "").Where(p => p.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (res.Count == 0 && _currentNode is IPlatform p)
        {
            var n = Safe(() => p.Name) ?? "";
            if (n.Length > 0) res.Add(n);
        }
        return res;
    }

    /// <summary>Re-scan the Gc media index — every platform, or the CachePlatformTargets ones. The
    /// build is async (per-platform jobs); once done the view reloads so resolutions pick the fresh
    /// index up.</summary>
    private void RebuildGameImageCache(bool all)
    {
        if (!Gc.HostGameCache.Enabled) return;
        System.Threading.Tasks.Task task;
        string what;                       // what the completion notification names
        if (all) { task = Safe(() => Gc.GameCache.RebuildAll()); what = "all platforms"; }
        else
        {
            var targets = CachePlatformTargets();
            var tasks = targets
                .Select(n => Safe(() => Gc.GameCache.RebuildPlatform(Unbroken.LaunchBox.Plugins.PluginHelper.DataManager?.GetPlatformByName(n))))
                .Where(t => t != null).ToArray();
            task = tasks.Length == 0 ? null : System.Threading.Tasks.Task.WhenAll(tasks);
            what = targets.Count == 1 ? targets[0] : $"{targets.Count} platforms";
        }
        task?.ContinueWith(t =>
        {
            try { if (!IsDisposed && !_closing) BeginInvoke((Action)ReloadAfterGameChange); } catch { }
            // Completion notification. It belongs to THIS method, not to GameCache.RebuildAll/RebuildPlatform:
            // those also run automatically (at boot, and after an image edit via GameCacheBridge), and an
            // automatic rebuild must stay silent. This method is reached only from the two Tools ▸ Rebuild
            // Game Image Cache entries — a rebuild the user asked for, whose only visible effect today is
            // the list quietly reloading whenever it happens to finish.
            if (t.IsFaulted)
                LiteBox.Notifications.NotificationCenter.Error($"Game image cache rebuild failed ({what}).");
            else
                LiteBox.Notifications.NotificationCenter.Info($"Game image cache rebuilt — {what}.");
        });
    }

    // ── Game music (Auto-Play Music / Shuffle Music) ─────────────────────────

    /// <summary>Selection landed on <paramref name="g"/> (null = a node / nothing): stop the previous
    /// game's music and start this one's when Auto-Play Music is on. The same game keeps its track
    /// running. A video that autoplays WITH SOUND silences music at ITS start (VideoBlock.SetMuted),
    /// not here — at settle the main media is the box, not the video.
    ///
    /// The work runs OFF the UI thread, alongside the other post-load actions: resolving the tracks
    /// walks the platform's Music folder (IO) and the first Play may pay the lazy libvlc init — the
    /// details pane must not wait on either. The load token gates a stale start (selection moved on
    /// while the walk was in flight — the newer call already owns the player).
    ///
    /// <paramref name="mainIsVideo"/> is null while the media list is still being built (the settle
    /// call), and the answer once it lands (ScheduleMedia). The main media only becomes a video half
    /// a second later, so with autoplay-WITH-SOUND on we would otherwise start a burst of music just
    /// to have the video silence it — hence: hold the music back until the media list says whether a
    /// sounded video is taking the audio.</summary>
    private void UpdateGameMusic(IGame g, bool? mainIsVideo = null)
    {
        try
        {
            if (g == null) { Media.GameMusicPlayer.Stop(); return; }
            var s = LbSettings;
            if (s == null || !s.GetBool("AutoPlayMusic", true)) { Media.GameMusicPlayer.Stop(); return; }
            // A video only claims the audio when it autoplays WITH SOUND — muted, or waiting behind
            // its ▶, it leaves the music alone. So stay silent while that is possible: confirmed
            // (mainIsVideo true) or not yet known (null).
            if (_cfg.VideoAutoplay && _cfg.VideoAutoplaySound && mainIsVideo != false)
            { Media.GameMusicPlayer.Stop(); return; }
            bool shuffle = s.GetBool("ShuffleMusic", true);
            string id = Safe(() => g.Id) ?? "";
            var captured = g;
            int token = _detailsLoadToken;
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var tracks = MusicTracksFor(captured);
                    if (token != _detailsLoadToken || _closing) return;   // selection moved on mid-walk
                    Media.GameMusicPlayer.Play(id, tracks, shuffle);
                }
                catch { }
            });
        }
        catch { }
    }

    /// <summary>The game's music files: a pinned MusicPath designates ONE track; otherwise every
    /// matched file (same walk as GetMusicPath's auto pick) — shuffle draws from all of them.</summary>
    private List<string> MusicTracksFor(IGame g)
    {
        var pinned = Safe(() => Media.MediaResolver.Override(g.MusicPath));
        if (!string.IsNullOrEmpty(pinned)) return new List<string> { pinned };
        Guid id = Guid.TryParse(Safe(() => g.Id) ?? "", out var x) ? x : Guid.Empty;
        return Safe(() => Media.MediaResolver.MusicsAll(Safe(() => g.Platform) ?? "", id, Safe(() => g.Title) ?? ""))
               ?? new List<string>();
    }

    private ToolStripMenuItem ViewMenu(string text)
    {
        // Both entries keep their glyph; the ACTIVE one is Checked, which with an Image renders as
        // the highlight frame around the icon (not a check mark) — the LaunchBox look.
        var images = M("Images View", MenuIcons.ViewImages);
        images.Checked = _posterMode;
        images.Click += (_, _) => Safe(() => SwitchView(poster: true));
        _miImagesView.Add(images);

        var list = M("List View", MenuIcons.ListView);
        list.Checked = !_posterMode;
        list.Click += (_, _) => Safe(() => SwitchView(poster: false));
        _miListView.Add(list);

        return Sub(text, MenuIcons.View,
            images, list,
            HideGamesMenu(),
            MediaMenu(),
            BadgesMenu("Badges"),
            ImageGroupMenu("Image Group"),
            ArrangeByMenu("Arrange By"));
    }

    private ToolStripMenuItem ToolsMenu(string text)
    {
        var sub = Sub(text, MenuIcons.Tools,
        Sub("Import", MenuIcons.Import,
            M("MS-DOS Games...", null),
            M("Steam Games...", null),
            M("Epic Games...", null),
            M("GOG...", null),
            M("EA...", null),
            M("Amazon Games...", null),
            M("Uplay/Ubisoft Connect Games...", null),
            M("Xbox/Microsoft Store Games...", null),
            M("Windows Games...", null),
            Sep(),
            M("ROM Files...", null),
            M("MAME Arcade Full Set...", null),
            M("Install DOS Game...", null)),
        Sub("Manage", MenuIcons.Manage,
            ManageEmulatorsItem(),
            ManagePlatformsItem(),
            ManageControllersItem(),
            ManageBadgesItem(),
            Sep(),
            M("Open Bulk Edit Wizard...", MenuIcons.Edit)),
        Sub("Download", MenuIcons.Download,
            M("Download Metadata and Media...", null),
            M("Download Platform/Playlist Theme Videos...", null),
            M("Download Updated Community Star Ratings...", null),
            Sep(),
            M("Force Update Games Database Metadata...", null)),
        Sub("Image Packs", MenuIcons.ImagePacks,
            M("Import Image Pack...", null),
            M("Export Image Pack...", null)),
        Sub("Audit", MenuIcons.Audit,
            M("Audit Current Platform...", null),
            M("For All Platforms...", null)),
        Sub("Scan", MenuIcons.Scan,
            M("Scan for Added ROMs", null),
            M("Scan for Removed ROMs", null)),
        Sub("Achievements", MenuIcons.Trophy,
            M("Scan for All Games...", null),
            M("Scan for Current Platform...", null),
            M("View Achievements Profile...", null)),
        Sub("Clean Up Media", MenuIcons.CleanUpMedia,
            M("Clean Up Current Platform Media...", null),
            M("Clean Up Media for All Platforms...", null)),
        Sub("File Management", MenuIcons.FileManagement,
            M("Consolidate ROMs for Current Platform...", null),
            M("Change ROMs Folder Path for Selected Games...", null)),
        Sub("Cloud", MenuIcons.Cloud,
            M("Sync to My Collection...", null),
            M("Browse My Collection...", null),
            Sep(),
            M("Connect to the LaunchBox Games Database...", null),
            M("Disconnect from the LaunchBox Games Database...", null)),
        Sep(),
        RandomGameItem(),
        M("Export to Android...", MenuIcons.ExportAndroid),
        OptionsItem());

        // LiteBox-only diagnostics (no LaunchBox counterpart) — below its tree, behind a separator.
        sub.DropDownItems.Add(Sep());
        var viewer = new ToolStripMenuItem("Game Image Cache Viewer...") { Image = MenuIcons.Get(MenuIcons.Audit) };
        viewer.Click += (_, _) => Safe(OpenGameImageCacheViewer);
        sub.DropDownItems.Add(viewer);
        return sub;
    }

    private static ToolStripMenuItem HelpMenu(string text) => Sub(text, MenuIcons.Help,
        M("Welcome...", MenuIcons.Welcome),
        M("Tutorials...", MenuIcons.Tutorials),
        M("Forums...", MenuIcons.Forums),
        Sep(),
        M("Changelog...", MenuIcons.Changelog),
        M("Report Issue...", MenuIcons.ReportIssue),
        M("Send Feedback...", MenuIcons.SendFeedback),
        Sep(),
        M("Get Premium...", MenuIcons.GetPremium),
        M("License Registration...", MenuIcons.LicenseRegistration),
        M("Check for Updates...", MenuIcons.CheckUpdates),
        M("About...", MenuIcons.About));

    // The image groups LaunchBox offers for the tiles, in its order. Ours are a subset (the toolbar's
    // Image Group button lists the regroupements LiteBox actually caches) — this bar shows the full
    // LaunchBox set for now, and will be reconciled when the entries start doing something.
    // ── Image Group — the poster tiles' image type ───────────────────────────
    // The toolbar dropdown's own list, not LaunchBox's: ours is limited to the regroupements LiteBox
    // manages and caches (CacheRegroupements). The menu drives the SAME state through
    // SelectPosterGroup, which re-stamps every surface — this tree exists twice (MENU ▸ View ▸ Image
    // Group and the bar's IMAGE GROUP) plus the toolbar button.
    private readonly List<ToolStripMenuItem> _miImageGroup = new();

    /// <summary>Reflect the active image group on every menu entry (called by SelectPosterGroup).</summary>
    private void SyncImageGroupChecks()
    {
        foreach (var it in _miImageGroup)
            if (it.Tag is string k) it.Checked = string.Equals(k, _posterGroup, StringComparison.OrdinalIgnoreCase);
    }

    private ToolStripMenuItem ImageGroupMenu(string text)
    {
        var sub = new ToolStripMenuItem(text) { Image = MenuIcons.Get(MenuIcons.ImageGroup) };
        ToolStripMenuItem GroupItem(string key, string label)
        {
            var mi = new ToolStripMenuItem(label)
            { Tag = key, Checked = string.Equals(_posterGroup, key, StringComparison.OrdinalIgnoreCase) };
            mi.Click += (_, _) => Safe(() => SelectPosterGroup(key));
            _miImageGroup.Add(mi);
            return mi;
        }
        sub.DropDownItems.Add(GroupItem("Front", "Use Default (Box fronts)"));
        sub.DropDownItems.Add(Sep());
        foreach (var (key, title) in CacheRegroupements)
            if (key != "Front") sub.DropDownItems.Add(GroupItem(key, title));
        return sub;
    }

    // ── Badges — LaunchBox's tree, live ──────────────────────────────────────
    // Show Badges + three group submenus (Game Attributes / Storefronts / Controller Support), each
    // entry composed the way LaunchBox composes it: LabelEnableSomething ("Enable {0}") + the badge's
    // own Indicator wording. State lives in LaunchBox's Settings.xml (Badges.BadgeSettings), so the
    // two apps agree; the catalog (Badges.BadgeCatalog) owns the list, the labels and the predicates.
    //
    // The tree is built TWICE (the bar's BADGES entry and MENU ▸ View ▸ Badges), so every checkable
    // item registers in _miBadges and SyncBadgeChecks re-stamps them all after any toggle — same
    // pattern as _miImageGroup.
    private readonly List<ToolStripMenuItem> _miBadges = new();
    private ToolStripMenuItem _miShowBadges1, _miShowBadges2;   // the two "Show Badges" copies

    private void SyncBadgeChecks()
    {
        foreach (var it in _miBadges)
            if (it.Tag is string id) it.Checked = Badges.BadgeSettings.IsEnabled(id);
        foreach (var it in new[] { _miShowBadges1, _miShowBadges2 })
            if (it != null) it.Checked = Badges.BadgeSettings.ShowBadges;
    }

    private ToolStripMenuItem BadgesMenu(string text)
    {
        var show = new ToolStripMenuItem("Show Badges") { Checked = Badges.BadgeSettings.ShowBadges };
        show.Click += (_, _) => Safe(() =>
        {
            Badges.BadgeSettings.ShowBadges = !Badges.BadgeSettings.ShowBadges;
            SyncBadgeChecks();
        });
        // Two live copies of the tree exist at once; remember both so either click updates both.
        if (_miShowBadges1 == null) _miShowBadges1 = show; else _miShowBadges2 = show;

        var change = M("Change Badge Images...", null);
        change.Click += (_, _) => Safe(OpenBadgeImagesFolder);
        var manage = M("Manage Badges...", MenuIcons.Badges);
        manage.Click += (_, _) => Safe(OpenManageBadges);
        // The engine follows every change it can be told about; this is for the rest — files dropped
        // in the media folders from Explorer, a badge pack edited outside the app.
        var recompute = M("Recompute Badges", MenuIcons.Refresh);
        recompute.Click += (_, _) => Safe(() => { Badges.BadgeImages.Reset(); Badges.BadgeEngine.InvalidateAll(); });

        var groups = new List<ToolStripItem>
        {
            show,
            BadgeGroupMenu("Game Attributes", Badges.BadgeGroup.GameAttributes),
            BadgeGroupMenu("Storefronts", Badges.BadgeGroup.Storefronts),
            BadgeGroupMenu("Controller Support", Badges.BadgeGroup.ControllerSupport),
        };
        // The Custom family only earns a submenu once the user has made one — an always-empty entry
        // would just be a dead end next to LaunchBox's own three.
        if (Badges.BadgeCatalog.Of(Badges.BadgeGroup.Custom).Any())
            groups.Add(BadgeGroupMenu("Custom Badges", Badges.BadgeGroup.Custom));
        groups.Add(Sep());
        groups.Add(manage);
        groups.Add(recompute);
        groups.Add(change);

        return Sub(text, MenuIcons.Badges, groups.ToArray());
    }

    private ToolStripMenuItem BadgeGroupMenu(string text, Badges.BadgeGroup group)
    {
        var sub = new ToolStripMenuItem(text);
        foreach (var def in Badges.BadgeCatalog.Of(group))
        {
            var mi = new ToolStripMenuItem($"Enable {def.Label}")
            { Tag = def.Id, Checked = Badges.BadgeSettings.IsEnabled(def.Id) };
            var id = def.Id;
            mi.Click += (_, _) => Safe(() =>
            {
                Badges.BadgeSettings.SetEnabled(id, !Badges.BadgeSettings.IsEnabled(id));
                SyncBadgeChecks();
            });
            _miBadges.Add(mi);
            sub.DropDownItems.Add(mi);
        }
        return sub;
    }

    private ToolStripMenuItem ManageBadgesItem()
    {
        var mi = M("Manage Badges...", MenuIcons.Badges);
        mi.Click += (_, _) => Safe(OpenManageBadges);
        return mi;
    }

    /// <summary>The Manage Badges window: which badges show, in which order, and the user's own ones.
    /// Its Display tab shows the very same OptionItems as Options ▸ Display — one set of settings,
    /// two places to reach them.</summary>
    private void OpenManageBadges()
    {
        Badges.ManageBadgesWindow.Open(this,
            (_dm as HostDataManagerXml)?.ReadOnly ?? false,
            () => Safe(() => (IReadOnlyList<IGame>)(_dm?.GetAllGames() ?? Array.Empty<IGame>())) ?? Array.Empty<IGame>(),
            () => BadgeHeroOptions().Concat(BadgeListOptions()).Concat(BadgePosterOptions()).ToArray());
        SyncBadgeChecks();
    }

    /// <summary>LaunchBox's "Change Badge Images..." opens a pack picker; ours opens the folder those
    /// packs live in — LiteBox reads whatever is there, it never ships badge art of its own.</summary>
    private void OpenBadgeImagesFolder()
    {
        var dir = Badges.BadgeImages.PacksRoot;
        if (string.IsNullOrEmpty(dir))
        {
            MessageBox.Show(this, "The LaunchBox folder isn't known yet.", "Badges",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        try { System.IO.Directory.CreateDirectory(dir); } catch { }
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true }); }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not open " + dir + "\n\n" + ex.Message, "Badges",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>The Manage Emulators window — one path for the toolbar's Emulators button and
    /// Tools ▸ Manage ▸ Manage Emulators… Opportunistic SCOPED flush on close: only the
    /// Emulators.xml ops go to disk now (when safe — LB/BB closed); game/playlist ops stay pending
    /// until the close-time flush. Matches the natural "I closed the editor, it's saved" expectation
    /// without committing unrelated half-done edits.</summary>
    /// <summary>Tools ▸ Select Random Game… — jump to a random game of the list as it currently
    /// stands (the selected node, minus whatever search/filter/parental rules hide). Drawing from the
    /// VISIBLE set is also what makes the jump work at all: selecting a game the view filtered out
    /// would find no row to land on.</summary>
    private ToolStripMenuItem RandomGameItem()
    {
        var it = M("Select Random Game...", MenuIcons.SelectRandomGame);
        it.Click += (_, _) => Safe(SelectRandomGame);
        it.Enabled = true;
        return it;
    }

    private void SelectRandomGame()
    {
        var view = _games?.VisibleGames;
        if (view == null || view.Count == 0) return;
        var g = view[Random.Shared.Next(view.Count)];
        // Focus goes to whichever control is actually showing: in poster mode the list is hidden,
        // and focusing it there would freeze the poster's arrow navigation (same rule as
        // MirrorPosterToList's focus:false).
        _games.SelectGame(g, focus: !_posterMode);
        if (_posterMode) SelectPosterGame(g);
        ShowDetails(g);
    }

    /// <summary>Move the poster's own selection + scroll onto a game (the poster is virtual, its index
    /// IS the position in the visible list). No-op when the game isn't in the current view.</summary>
    private void SelectPosterGame(IGame g)
    {
        if (_poster == null) return;
        var view = _games?.VisibleGames;
        if (view == null) return;
        int ix = -1;
        for (int i = 0; i < view.Count; i++) if (ReferenceEquals(view[i], g)) { ix = i; break; }
        if (ix < 0) return;
        try
        {
            _poster.SelectedIndices.Clear();
            _poster.SelectedIndices.Add(ix);
            _poster.EnsureVisible(ix);
            _poster.Focus();
        }
        catch { }
    }

    /// <summary>Tools ▸ Options… — the same sectioned options window as the toolbar's gear, locked
    /// under a second instance for the same reason (that instance is read-only).</summary>
    private ToolStripMenuItem OptionsItem()
    {
        var it = M("Options...", MenuIcons.Options);
        if (_secondInstance)
        {
            it.Enabled = false;
            it.ToolTipText = "Options locked — another LiteBox instance is open (read-only)";
        }
        else it.Click += (_, _) => Safe(OpenOptionsWindow);
        return it;
    }

    /// <summary>Tools ▸ Manage ▸ Manage Game Controllers… — the GameControllers.xml catalog window,
    /// the very one Edit Game ▸ Controller Support opens.</summary>
    private ToolStripMenuItem ManageControllersItem()
    {
        var it = M("Manage Game Controllers...", null);
        it.Click += (_, _) => Safe(() =>
        {
            bool ro = (_dm as Data.HostDataManagerXml)?.ReadOnly ?? true;
            using var w = new Controllers.ManageControllersWindow(ro);
            w.ShowDialog(this);
        });
        return it;
    }

    private ToolStripMenuItem ManageEmulatorsItem()
    {
        var it = M("Manage Emulators...", null);
        it.Click += (_, _) => Safe(OpenManageEmulators);
        return it;
    }

    /// <summary>The sectioned options window — one path for the toolbar's gear and Tools ▸ Options…
    /// Scoped flush on close: the LB-settings ops go to Settings.xml right away (when safe); LiteBox
    /// INI options were already saved by ApplyFinished.</summary>
    private void OpenOptionsWindow()
    {
        using var w = BuildOptionsWindow();
        w.ShowDialog(this);
        (_dm as Data.HostDataManagerXml)?.FlushLbSettingsIfSafe();
    }

    private void OpenManageEmulators()
    {
        bool ro = (_dm as Data.HostDataManagerXml)?.ReadOnly ?? true;
        using var w = new Emulators.ManageEmulatorsWindow(ro, Media.MediaResolver.LbRoot ?? "");
        w.ShowDialog(this);
        (_dm as Data.HostDataManagerXml)?.FlushEmulatorsIfSafe();
    }

    /// <summary>Tools ▸ Manage ▸ Manage Platforms… — the list window (twin of Manage Emulators).
    /// A platform edited or deleted there changes the hierarchy, so the tree is reloaded on close.</summary>
    private ToolStripMenuItem ManagePlatformsItem()
    {
        var it = M("Manage Platforms...", null);
        it.Click += (_, _) => Safe(() =>
        {
            bool ro = (_dm as Data.HostDataManagerXml)?.ReadOnly ?? true;
            using var w = new Platforms.ManagePlatformsWindow(ro, Media.MediaResolver.LbRoot ?? "");
            w.ShowDialog(this);
            if (!w.Changed) return;
            (_dm as Data.HostDataManagerXml)?.ReloadHierarchy();
            PopulateSources();
        });
        return it;
    }

    private ToolStripMenuItem ArrangeByMenu(string text)
    {
        var sub = new ToolStripMenuItem(text) { Image = MenuIcons.Get(MenuIcons.ArrangeBy) };
        WireArrangeDropDown(sub);
        return sub;
    }

    /// <summary>Hang the live sort catalog off a drop-down (the toolbar button's own list, via
    /// PopulateArrangeItems). Rebuilt on every open — the entries depend on the current node, the
    /// active column and the library's custom fields, none of which hold still. The placeholder is
    /// what makes the ► show before the first open (an empty drop-down never opens).</summary>
    private void WireArrangeDropDown(ToolStripMenuItem host)
    {
        host.DropDownItems.Add(new ToolStripMenuItem("…") { Enabled = false });
        host.DropDownOpening += (_, _) =>
        {
            host.DropDownItems.Clear();
            Safe(() => PopulateArrangeItems(host.DropDownItems));
        };
    }

    // ── Plumbing ─────────────────────────────────────────────────────────────
    // Every entry is inert for now: no Click handler, no shortcut. Enabled stays true so the bar reads
    // the way LaunchBox's does (only entries LaunchBox itself greys out are disabled here).

    private static ToolStripMenuItem Top(string text, params ToolStripItem[] children)
    {
        // LaunchBox renders the bar in capitals; the submenu entries keep their normal casing.
        var top = new ToolStripMenuItem(text.ToUpperInvariant());
        foreach (var c in children) top.DropDownItems.Add(c);
        return top;
    }

    /// <summary>Steals a submenu's children so the same tree can hang off a top-level bar entry.</summary>
    private static ToolStripItem[] Children(ToolStripMenuItem built)
    {
        var kids = new ToolStripItem[built.DropDownItems.Count];
        built.DropDownItems.CopyTo(kids, 0);
        built.DropDownItems.Clear();
        return kids;
    }

    private static ToolStripMenuItem Sub(string text, string icon, params ToolStripItem[] children)
    {
        var m = new ToolStripMenuItem(text) { Image = MenuIcons.Get(icon) };
        foreach (var c in children) m.DropDownItems.Add(c);
        return m;
    }

    private static ToolStripMenuItem M(string text, string icon, bool enabled = true)
        => new(text) { Image = MenuIcons.Get(icon), Enabled = enabled };

    private static ToolStripMenuItem Check(string text, string icon, bool @checked)
        => new(text) { Image = MenuIcons.Get(icon), Checked = @checked, CheckOnClick = false };

    private static ToolStripSeparator Sep() => new();
}
