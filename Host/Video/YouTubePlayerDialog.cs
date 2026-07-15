// In-app YouTube mini-player: a WebView2 window that shows ONLY the video, instead of throwing the user out to
// the system browser. WebView2 ships in LaunchBox 13.28's Core (13.27 does NOT) and needs the Evergreen runtime;
// IsAvailable() probes both and the caller falls back to the browser when it returns false.
//
// The video is loaded through the official IFrame Player API, inside a page served from a VIRTUAL HOST so it has
// a real https origin (a bare top-level /embed/ navigation trips "Error 153 — configuration error"). The API's
// onError fires when the owner has DISABLED embedding (many official trailers) — we then redirect this same
// window to the normal watch page, which always plays. A PERSISTENT profile keeps a Google sign-in between
// sessions, which is what unlocks age-restricted videos (and drops ads on a Premium account).
//
// Robustness with the ExtendDB plugin (which ALSO uses WebView2 in the same process): we try our own dedicated
// environment first, then fall back to the process default (reusing whatever WebView2 is already up), then to
// the browser — so a shared-process conflict degrades instead of failing outright.

#nullable enable

using System;
using System.Drawing;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace LbApiHost.Host.Video;

internal sealed class YouTubePlayerDialog : Form
{
    private const string VHost = "ytplayer.litebox";

    /// <summary>True when the WebView2 managed assemblies (Core-provided on LB 13.28) AND the Evergreen runtime
    /// are both present. Isolated + guarded so a Core without WebView2 (13.27) is caught, never fatal.</summary>
    public static bool IsAvailable()
    {
        try { return Probe(); } catch { return false; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool Probe() => !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString());

    /// <summary>Opens the player modally. Returns false (does nothing) when WebView2 isn't available, so the
    /// caller can fall back to the system browser.</summary>
    public static bool TryShow(IWin32Window owner, string videoId, string watchUrl, string title)
    {
        if (!IsAvailable()) return false;
        try
        {
            using var d = new YouTubePlayerDialog(videoId, watchUrl, title);
            d.ShowDialog(owner);
            return true;
        }
        catch { return false; }
    }

    private static readonly Color Bg = Color.FromArgb(18, 18, 22);
    private static readonly Color Fg = Color.FromArgb(228, 228, 232);

    private readonly WebView2 _web;
    private readonly string _videoId;
    private readonly string _watchUrl;
    private bool _mapped;

    private YouTubePlayerDialog(string videoId, string watchUrl, string title)
    {
        _videoId = videoId;
        _watchUrl = watchUrl;

        Text = title;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(1000, 620);
        MinimumSize = new Size(560, 360);
        BackColor = Bg;
        KeyPreview = true;
        ShowIcon = false;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };

        var bar = new Panel { Dock = DockStyle.Top, Height = 36, BackColor = Bg };
        var full = Btn("↗  Open full page (sign in / age-restricted)", 6);
        full.Click += (_, _) => Nav(_watchUrl);
        var embed = Btn("▶  Player only", full.Right + 6);
        embed.Click += (_, _) => NavigateEmbed();
        var browser = Btn("🌐  Browser", embed.Right + 6);
        browser.Click += (_, _) => OpenInBrowser();
        bar.Controls.Add(full); bar.Controls.Add(embed); bar.Controls.Add(browser);

        _web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.Black };

        Controls.Add(_web);   // Fill first …
        Controls.Add(bar);    // … Top last

        Shown += async (_, _) => await InitAsync();
    }

    private static Button Btn(string text, int x)
    {
        var b = new Button
        {
            Text = text, AutoSize = false, Height = 26, Top = 5, Left = x, FlatStyle = FlatStyle.Flat,
            ForeColor = Fg, BackColor = Color.FromArgb(48, 48, 58), Font = new Font("Segoe UI", 8.5f), Cursor = Cursors.Hand,
            TabStop = false,
        };
        b.FlatAppearance.BorderSize = 0;
        b.Width = TextRenderer.MeasureText(text, b.Font).Width + 24;
        return b;
    }

    private async Task InitAsync()
    {
        // Prefer our own persistent, autoplay-enabled environment; if that clashes with another WebView2 consumer
        // in the process (the ExtendDB plugin), reuse the process default; if even that fails, hand off to the browser.
        CoreWebView2Environment? env = null;
        try
        {
            var options = new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required");
            env = await CoreWebView2Environment.CreateAsync(null, LiteBoxPaths.Dir("webview2-yt"), options);
        }
        catch { }

        try { await _web.EnsureCoreWebView2Async(env); }          // env==null → the process default environment
        catch { OpenInBrowser(); Close(); return; }
        if (_web.CoreWebView2 == null) { OpenInBrowser(); Close(); return; }

        try
        {
            var s = _web.CoreWebView2.Settings;
            s.AreDefaultContextMenusEnabled = true;
            s.IsStatusBarEnabled = false;
            _web.CoreWebView2.NewWindowRequested += (_, e) => { e.Handled = true; _web.CoreWebView2.Navigate(e.Uri); };

            // Serve a tiny local page as a real https origin so the embed player has a valid origin (Error 153 fix).
            try
            {
                _web.CoreWebView2.SetVirtualHostNameToFolderMapping(VHost, LiteBoxPaths.Dir("webview2-yt-page"), CoreWebView2HostResourceAccessKind.Allow);
                _mapped = true;
            }
            catch { _mapped = false; }
        }
        catch { }

        NavigateEmbed();
    }

    // Load the video through the IFrame Player API on the virtual-host page. onError (embedding disabled) redirects
    // this window to the watch page. If the host mapping isn't available, go straight to the watch page.
    private void NavigateEmbed()
    {
        if (_web?.CoreWebView2 == null) return;
        if (!_mapped) { Nav(_watchUrl); return; }
        try
        {
            var dir = LiteBoxPaths.Dir("webview2-yt-page");
            File.WriteAllText(Path.Combine(dir, "player.html"), PageHtml.Replace("__VID__", _videoId));
            Nav($"https://{VHost}/player.html");
        }
        catch { Nav(_watchUrl); }
    }

    private void Nav(string url) { try { _web?.CoreWebView2?.Navigate(url); } catch { } }

    private void OpenInBrowser()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_watchUrl) { UseShellExecute = true }); }
        catch { }
    }

    private const string PageHtml = @"<!doctype html><html><head><meta charset=""utf-8"">
<style>html,body{margin:0;height:100%;background:#000;overflow:hidden}#p{width:100%;height:100%}</style></head>
<body><div id=""p""></div><script>
var VID=""__VID__"", WATCH=""https://www.youtube.com/watch?v=""+VID;
function onYouTubeIframeAPIReady(){
  new YT.Player('p',{videoId:VID,width:'100%',height:'100%',
    playerVars:{autoplay:1,rel:0,modestbranding:1,playsinline:1,fs:1},
    events:{onError:function(e){location.href=WATCH;}}});
}
var s=document.createElement('script');s.src='https://www.youtube.com/iframe_api';document.head.appendChild(s);
</script></body></html>";
}
