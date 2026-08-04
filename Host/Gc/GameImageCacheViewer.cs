// The Game Image Cache viewer — native port of ExtendDB's GameCacheDebugForm, over the
// host-backported GameCache (Host/Gc/GameCache.cs).
//
// Three-pane read-only window: pick a platform on the left, see its sanitized-name game keys in the
// middle, see one game's images and videos on the right with type/region/num/size/crc and the
// resolved on-disk path. Strictly an inspection aid — the REGENERATION actions live in the menu
// (View ▸ Media ▸ Rebuild Game Image Cache …), not here; the only button is Refresh.
//
// Same knowingly-accepted limits as the plugin's form: several games can share a sanitized key and
// only the first is surfaced; the view is a live snapshot (hit Refresh to re-pull). When the host
// cache is off (option disabled, or ExtendDB's own cache serves the app) the status line says so
// instead of showing empty panes.
//
// Opened non-modally (Show) from the menu — single live instance owned by MainWindow.

#nullable enable

using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace LbApiHost.Host.Gc;

internal sealed class GameImageCacheViewer : Form
{
    private readonly ListBox _lstPlatforms;
    private readonly ListBox _lstGames;
    private readonly ListBox _lstMedia;
    private readonly Label _lblStatus;

    public GameImageCacheViewer()
    {
        Text = "LiteBox — Game Image Cache";
        Size = new Size(1100, 600);
        MinimumSize = new Size(800, 400);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(25, 25, 35);
        ForeColor = Color.FromArgb(200, 200, 200);

        var topPanel = new Panel { Dock = DockStyle.Top, Height = 36 };
        Controls.Add(topPanel);

        _lblStatus = new Label
        {
            Text = "...",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(8, 0, 0, 0),
            Font = new Font("Consolas", 9f),
        };
        topPanel.Controls.Add(_lblStatus);

        var btnRefresh = new Button
        {
            Text = "Refresh",
            Dock = DockStyle.Right,
            Width = 90,
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.FromArgb(200, 200, 200),
            BackColor = Color.FromArgb(50, 50, 60),
        };
        btnRefresh.Click += (_, _) => LoadPlatforms();
        topPanel.Controls.Add(btnRefresh);

        var split1 = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterDistance = 200,
            BackColor = Color.FromArgb(25, 25, 35),
        };
        Controls.Add(split1);
        split1.BringToFront();

        var split2 = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterDistance = 250,
            BackColor = Color.FromArgb(25, 25, 35),
        };
        split1.Panel2.Controls.Add(split2);

        _lstPlatforms = CreateListBox();
        split1.Panel1.Controls.Add(WrapWithLabel("Platforms", _lstPlatforms));

        _lstGames = CreateListBox();
        split2.Panel1.Controls.Add(WrapWithLabel("Games (sanitized)", _lstGames));

        _lstMedia = CreateListBox();
        _lstMedia.Font = new Font("Consolas", 8.5f);
        _lstMedia.HorizontalScrollbar = true;
        split2.Panel2.Controls.Add(WrapWithLabel("Images / Videos", _lstMedia));

        _lstPlatforms.SelectedIndexChanged += (_, _) => OnPlatformSelected();
        _lstGames.SelectedIndexChanged += (_, _) => OnGameSelected();

        LoadPlatforms();
    }

    private void LoadPlatforms()
    {
        _lstPlatforms.Items.Clear();
        _lstGames.Items.Clear();
        _lstMedia.Items.Clear();

        if (!HostGameCache.Enabled)
        {
            _lblStatus.Text = "Host Game Image Cache is OFF (option disabled, or ExtendDB serves its own cache).";
            return;
        }
        if (!GameCache.IsGlobalReady)
        {
            _lblStatus.Text = "Game Image Cache not ready yet...";
            return;
        }

        foreach (var kvp in GameCache.Platforms.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            int gameCount = kvp.Value.GamesBySanitizedName.Count;
            int imageCount = kvp.Value.GamesByUUID.Values.Sum(g => g.Images.Length);
            int videoCount = kvp.Value.GamesByUUID.Values.Sum(g => g.Videos.Length);
            _lstPlatforms.Items.Add(new PlatformItem(
                kvp.Key,
                $"{kvp.Key}  ({gameCount} games, {imageCount} img, {videoCount} vid)"));
        }

        _lblStatus.Text = $"Global ready: {GameCache.IsGlobalReady} | Platforms: {GameCache.Platforms.Count}";
    }

    private void OnPlatformSelected()
    {
        _lstGames.Items.Clear();
        _lstMedia.Items.Clear();

        if (_lstPlatforms.SelectedItem is not PlatformItem pi) return;
        if (!GameCache.Platforms.TryGetValue(pi.Name, out var platform)) return;

        _lblStatus.Text = $"{pi.Name} | ready: {GameCache.IsPlatformReady(pi.Name)} | {platform.GamesBySanitizedName.Count} game keys";

        foreach (var kvp in platform.GamesBySanitizedName.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var first = kvp.Value.FirstOrDefault();
            _lstGames.Items.Add(new GameItem(
                kvp.Value,
                $"{kvp.Key}  [{first?.Title ?? "?"}]  ({first?.Images.Length ?? 0} img, {first?.Videos.Length ?? 0} vid)"));
        }
    }

    private void OnGameSelected()
    {
        _lstMedia.Items.Clear();

        if (_lstGames.SelectedItem is not GameItem gi) return;
        var game = gi.Games.FirstOrDefault();
        if (game == null) return;

        _lblStatus.Text = $"{game.Title} | {game.Images.Length} images, {game.Videos.Length} videos";

        foreach (var img in game.Images)
        {
            string sizeStr = img.FileSize >= 0 ? $"{img.FileSize / 1024}KB" : "?";
            string crcStr = img.Crc >= 0 ? img.Crc.ToString() : "-";
            _lstMedia.Items.Add(
                $"[IMG] {img.GetImageTypeName() ?? "?"} | {img.Region} | #{img.GetNumText()} | {sizeStr} | crc={crcStr} | {game.ResolveImagePath(img)}");
        }
        foreach (var vid in game.Videos)
        {
            string sizeStr = vid.FileSize >= 0 ? $"{vid.FileSize / 1024}KB" : "?";
            _lstMedia.Items.Add(
                $"[VID] {vid.SubDir ?? "(root)"} | #{vid.GetNumText()} | {vid.Ext} | {sizeStr} | {game.ResolveVideoPath(vid)}");
        }
        if (game.Images.Length == 0 && game.Videos.Length == 0)
            _lstMedia.Items.Add("(no media)");
    }

    private static ListBox CreateListBox() => new()
    {
        Dock = DockStyle.Fill,
        BackColor = Color.FromArgb(30, 30, 42),
        ForeColor = Color.FromArgb(200, 200, 200),
        BorderStyle = BorderStyle.None,
        Font = new Font("Segoe UI", 9f),
        IntegralHeight = false,
    };

    private static Panel WrapWithLabel(string text, ListBox listBox)
    {
        var panel = new Panel { Dock = DockStyle.Fill };
        var label = new Label
        {
            Text = text,
            Dock = DockStyle.Top,
            Height = 22,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(4, 0, 0, 0),
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            ForeColor = Color.FromArgb(140, 140, 160),
            BackColor = Color.FromArgb(35, 35, 48),
        };
        panel.Controls.Add(listBox);
        panel.Controls.Add(label);
        return panel;
    }

    // ListBox renders items via ToString — these keep full control of the line format.
    private sealed record PlatformItem(string Name, string Display) { public override string ToString() => Display; }
    private sealed record GameItem(GameCacheGame[] Games, string Display) { public override string ToString() => Display; }
}
