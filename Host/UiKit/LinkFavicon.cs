// Favicons for the Media ▸ Links menu items, fetched DYNAMICALLY when the menu is built.
//
// Deliberately no disk cache: the fetch happens in the background on each right-click session and
// costs the UI nothing — the menu opens instantly with the generic link glyph, and each item's
// image flips to the site's favicon if/when its download lands (BeginInvoke back on the UI
// thread; a menu the user already closed just ignores the late image). A session-scoped
// in-memory map (host → image) makes the SECOND right-click on the same site instant and keeps
// repeated right-clicks from hammering anyone; nothing is ever persisted.
//
// Source: https://<authority>/favicon.ico — the one location that needs no HTML parsing. Sites
// that only declare their icon via <link rel="icon"> simply keep the generic glyph (fail-soft,
// never retried within the session).

#nullable enable

using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LbApiHost.Host.UiKit;

internal static class LinkFavicon
{
    // host authority → 16×16 image, or null for a known miss (no retry this session).
    private static readonly ConcurrentDictionary<string, Image?> Session = new(StringComparer.OrdinalIgnoreCase);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(4) };

    /// <summary>Give <paramref name="item"/> the favicon of <paramref name="url"/>'s site — now if
    /// this session already fetched it, else in the background (UI marshalled through
    /// <paramref name="ui"/>). Fail-soft: any miss just keeps the item's current image.</summary>
    public static void Attach(ToolStripMenuItem item, string url, Control ui)
    {
        if (item == null || ui == null || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return;
        string origin;
        try { origin = uri.GetLeftPart(UriPartial.Authority); } catch { return; }

        if (Session.TryGetValue(origin, out var cached))
        {
            if (cached != null) item.Image = cached;
            return;
        }

        _ = Task.Run(async () =>
        {
            Image? img = null;
            try
            {
                var bytes = await Http.GetByteArrayAsync(origin + "/favicon.ico").ConfigureAwait(false);
                img = Decode16(bytes);
            }
            catch { /* miss → generic glyph stays */ }
            Session[origin] = img;   // misses recorded too — one attempt per site per session
            if (img == null) return;
            try
            {
                if (!ui.IsDisposed && ui.IsHandleCreated)
                    ui.BeginInvoke(() => { try { if (!item.IsDisposed) item.Image = img; } catch { } });
            }
            catch { }
        });
    }

    /// <summary>Decode favicon bytes into a 16×16 image: .ico first (picks the best frame), then a
    /// plain image decode (plenty of sites serve a PNG at /favicon.ico). Null when neither works.</summary>
    private static Image? Decode16(byte[] bytes)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            using var ico = new Icon(ms, 16, 16);
            return ico.ToBitmap();
        }
        catch { }
        try
        {
            using var ms = new MemoryStream(bytes);
            using var raw = Image.FromStream(ms);
            var b = new Bitmap(16, 16);
            using var g = Graphics.FromImage(b);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(raw, 0, 0, 16, 16);
            return b;
        }
        catch { return null; }
    }
}
