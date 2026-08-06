// Badge artwork — resolved from the user's LaunchBox badge media pack, never shipped with LiteBox.
//
// The images come from a media pack the user installs, <LB>\Images\Media Packs\Badges\<pack>\<name>.png,
// plus a Progress\ subfolder for the play-progress states. LiteBox reads those files at runtime — the
// same source, the same file names, whatever pack the user picked — exactly like the web side's
// Host/Web/BadgeApi.cs does for the browser clients. No pack installed = no badge drawn, silently.
// (LaunchBox does carry a built-in set as embedded Badge* bitmaps inside
// Unbroken.LaunchBox.Windows.dll, used when no pack is installed. We don't read those: they are
// LaunchBox's own art, and a pack is the case that matters here. If the no-pack case ever needs to
// show something, that DLL — in the user's own install — is where it would come from.)
//
// Resolution repeats BadgeApi's rule so both surfaces agree: scan every pack folder, first match
// wins, case-insensitive, and an '_' in the requested name also matches a space on disk ("Not
// Installed"). Progress states are asked for as "Progress/<state>". The one refinement over the web
// side: the pack named by LaunchBox's BadgePack setting is scanned FIRST, so a user with several
// packs installed gets the one they chose.
//
// The index (name → file) is built once and the decoded bitmaps are cached ALREADY SCALED to the
// height the caller asked for: badges are painted on every hero repaint, and a 40px source rescaled
// per paint would be both slower and blurrier than one good bicubic pass kept around. Scaling uses
// each image's own max(w,h) as its design box, so the set keeps its relative proportions (the pack's
// keyboard is 40×24 and must stay wider than tall) and a future 64px pack scales correctly too.

#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using LbApiHost.Host.Diag;
using LbApiHost.Host.Media;

namespace LbApiHost.Host.Badges;

/// <summary>How a badge is coloured. <see cref="Required"/> paints a controller badge deep red — the
/// pack's controller art is white monochrome, so multiplying its channels by the tint turns the glyph
/// red while the black keyline stays black.</summary>
internal enum BadgeTint { None, Required }

internal static class BadgeImages
{
    private static readonly object _lock = new();
    private static Dictionary<string, string>? _index;                 // normalised name → file path
    private static readonly Dictionary<(string name, int h, BadgeTint t, int opacity), Image?> _cache = new();

    // Deep red ("bordeaux") for a REQUIRED controller.
    private static readonly float[] RequiredRgb = { 0.62f, 0.09f, 0.13f };

    /// <summary>The folder LaunchBox keeps badge packs in, or null when the LB root isn't known yet.</summary>
    public static string? PacksRoot
    {
        get
        {
            var lb = MediaResolver.LbRoot;
            return string.IsNullOrEmpty(lb) ? null : Path.Combine(lb, "Images", "Media Packs", "Badges");
        }
    }

    /// <summary>True when at least one pack file was found — the UI can tell "no badges apply" from
    /// "no pack installed".</summary>
    public static bool HavePack { get { lock (_lock) { return Index().Count > 0; } } }

    /// <summary>Does the installed pack have this image? (Lets a caller pick between candidate names
    /// — Progress's "Category _ Value" then "Category" — without decoding anything.)</summary>
    public static bool Has(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        lock (_lock) { return Index().ContainsKey(Norm(name!)); }
    }

    /// <summary>The badge image scaled to fit a <paramref name="height"/>-pixel box, or null when the
    /// pack has no such file (or no pack is installed at all). The returned Image is CACHE-OWNED —
    /// draw it, never dispose it.</summary>
    /// <param name="opacityPct">5–100. Baked into the cached bitmap rather than applied per draw:
    /// the list and the tiles composite their badges once, so the alpha belongs in the source.</param>
    public static Image? Get(string name, int height, BadgeTint tint = BadgeTint.None, int opacityPct = 100)
    {
        if (string.IsNullOrWhiteSpace(name) || height <= 0) return null;
        opacityPct = Math.Clamp(opacityPct, 5, 100);
        var key = (Norm(name), height, tint, opacityPct);
        lock (_lock)
        {
            if (_cache.TryGetValue(key, out var cached)) return cached;
            Image? img = null;
            try
            {
                if (Index().TryGetValue(key.Item1, out var path)) img = Load(path, height, tint, opacityPct);
            }
            catch (Exception ex) { LbLog.Warn("badges", $"load {name} failed: {ex.Message}"); }
            _cache[key] = img;
            return img;
        }
    }

    /// <summary>Drop the SCALED bitmaps but keep the pack index (no directory rescan).
    ///
    /// The cache is keyed (name, height, tint, opacity), and the three surfaces each have their own
    /// size and opacity settings — so every value the user tries leaves a full set of bitmaps behind,
    /// ~0.2–0.7 MB a time, which nothing ever reclaimed. At the moment a size or opacity option is
    /// applied, the entries for the old value are dead: this is where they go. Re-decoding what is
    /// still needed costs a handful of milliseconds, on the ~44 images of a pack.</summary>
    public static void DropScaled()
    {
        lock (_lock)
        {
            if (_cache.Count == 0) return;
            int n = _cache.Count;
            _cache.Clear();     // NOT disposed — see the note on Reset()
            LbLog.Info("badges", $"scaled icons dropped: {n}");
        }
    }

    /// <summary>Drop the index and every cached bitmap (pack changed, LB root discovered late, DPI
    /// change). The next Get rebuilds from disk.
    ///
    /// The bitmaps are RELEASED, never disposed. Callers hold them — the detail pane keeps the
    /// selected game's badges between paints — and this runs from the background pass, so there is no
    /// moment at which "refresh the surfaces first" would be safe: a repaint on the UI thread can land
    /// between the dispose and the refresh, and drawing a disposed Image throws out of OnPaint. Letting
    /// them go costs nothing real: a few hundred small bitmaps, collected with their finalizers.</summary>
    public static void Reset()
    {
        lock (_lock)
        {
            _cache.Clear();
            _index = null;
        }
    }

    // ── internals ────────────────────────────────────────────────────────────

    // Lower-cased, with '_' and apostrophes folded to ' ' — so "Not_Installed", "Not Installed" and
    // "not installed" are one key, and the progress value "Not Started / Won't Play" reaches the
    // pack's "Not Started _ Won_t Play.png" (pack authors can't put an apostrophe in a file name the
    // way LaunchBox's value has one). The folder prefix ("progress/") rides through untouched.
    private static string Norm(string name)
        => name.Replace('_', ' ').Replace('\'', ' ').Replace('’', ' ').Trim().ToLowerInvariant();

    private static Dictionary<string, string> Index()
    {
        if (_index != null) return _index;
        var idx = new Dictionary<string, string>(StringComparer.Ordinal);
        var root = PacksRoot;
        // No LB root yet (asked before MediaResolver.Init) — answer "nothing" WITHOUT caching it, so
        // the next call re-scans instead of staying empty for the rest of the session.
        if (string.IsNullOrEmpty(root)) return idx;
        try
        {
            if (Directory.Exists(root))
                foreach (var pack in PacksInOrder(root))
                {
                    AddFolder(idx, pack, prefix: "");
                    var progress = Path.Combine(pack, "Progress");
                    if (Directory.Exists(progress)) AddFolder(idx, progress, prefix: "progress/");
                }
        }
        catch (Exception ex) { LbLog.Warn("badges", $"pack scan failed: {ex.Message}"); }
        LbLog.Info("badges", $"pack index: {idx.Count} images under {root ?? "(no LB root)"}");
        return _index = idx;
    }

    // The pack LaunchBox's BadgePack setting names comes first; the rest follow in directory order.
    private static IEnumerable<string> PacksInOrder(string root)
    {
        var packs = Directory.EnumerateDirectories(root).ToList();
        var chosen = BadgeSettings.Pack;
        if (string.IsNullOrEmpty(chosen)) return packs;
        return packs.OrderByDescending(p =>
            string.Equals(Path.GetFileName(p), chosen, StringComparison.OrdinalIgnoreCase));
    }

    // First pack wins — TryAdd, never overwrite, so the scan order decides exactly like BadgeApi's
    // "first match wins" does on the web side.
    private static void AddFolder(Dictionary<string, string> idx, string dir, string prefix)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.png"))
                idx.TryAdd(prefix + Norm(Path.GetFileNameWithoutExtension(file)), file);
        }
        catch (Exception ex) { LbLog.Warn("badges", $"scan {dir} failed: {ex.Message}"); }
    }

    private static Image? Load(string path, int height, BadgeTint tint, int opacityPct)
    {
        using var src = Image.FromFile(path);
        // Design box = the image's own longest side (40 for LaunchBox's packs). Scaling by that,
        // not by the height, keeps a wide badge wide and a tall one tall inside a uniform row.
        int box = Math.Max(src.Width, src.Height);
        if (box <= 0) return null;
        float scale = height / (float)box;
        int w = Math.Max(1, (int)Math.Round(src.Width * scale));
        int h = Math.Max(1, (int)Math.Round(src.Height * scale));
        var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            var dst = new Rectangle(0, 0, w, h);
            if (tint == BadgeTint.None && opacityPct >= 100) g.DrawImage(src, dst);
            else
            {
                // One matrix does both. The tint MULTIPLIES, it doesn't replace: a white glyph becomes
                // the tint, the black keyline stays black — which is why it is only used on the white
                // controller family. Matrix33 scales the existing alpha, so transparent stays
                // transparent and the halo fades with the rest.
                var m = new System.Drawing.Imaging.ColorMatrix
                {
                    Matrix00 = tint == BadgeTint.None ? 1f : RequiredRgb[0],
                    Matrix11 = tint == BadgeTint.None ? 1f : RequiredRgb[1],
                    Matrix22 = tint == BadgeTint.None ? 1f : RequiredRgb[2],
                    Matrix33 = opacityPct / 100f,
                };
                using var ia = new System.Drawing.Imaging.ImageAttributes();
                ia.SetColorMatrix(m);
                g.DrawImage(src, dst, 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, ia);
            }
        }
        return bmp;
    }
}
