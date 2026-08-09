// Degraded-thumbnail cache for the GUI.
//
//   • On-disk directory       : <LB>\Core\litebox\cache\thumbs\degraded — LiteBox-OWN, like every file
//     LiteBox creates (integration-extenddb: nothing lives under Plugins\ExtendDB anymore). The legacy
//     shared dir is left alone for a real LaunchBox+ExtendDB install; thumbs regenerate on demand.
//   • KEY algorithm           : unchanged (historically byte-identical to ExtendDB's KeyFor).
//   • Output                  : JPEG q72 (keepAlpha=false) / WebP q82 w/ alpha
//     (keepAlpha=true, used for clear logos). "WxH>" = shrink-to-fit, never upscale.
//
// Magick is an optional dependency: a cache HIT returns the file with no Magick at
// all; only a MISS needs Magick to generate (Decode/Generate is isolated so a missing
// Magick.NET — standalone, no ExtendDB — is caught and the call returns null).
//
// MagickSupport.Init mirrors ExtendDB's Utils.InstallDlls for the native lib: it
// deploys the bundled Magick.Native-Q16-x64.dll.api (next to the exe) to
// <LB>\ThirdParty\ExtendDB\Magick.Native-Q16-x64.dll if absent and points the DLL
// search path there, so LiteBox works WITH or WITHOUT ExtendDB loaded.

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LbApiHost.Host.Media;

internal static class MagickSupport
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectory(string lpPathName);

    /// <summary>Deploy the bundled native ImageMagick lib to ExtendDB's ThirdParty
    /// folder (if not already there) and add it to the DLL search path. Idempotent;
    /// safe to call before ExtendDB (which does the same thing) loads.</summary>
    public static void Init(string lbRoot)
    {
        try
        {
            // Magick.Native-Q16-x64.dll is deployed by NativeInstaller (embedded → ThirdParty\ExtendDB);
            // here we only add that folder to the native search path.
            string nativeDir = Path.Combine(lbRoot, "ThirdParty", "ExtendDB");
            Directory.CreateDirectory(nativeDir);
            SetDllDirectory(nativeDir);   // one slot; ExtendDB sets the same dir later — no conflict
        }
        catch { }
    }
}

internal static class ThumbCache
{
    /// <summary>Longest-edge of the degraded thumbnail (px). MUST match ExtendDB's.</summary>
    public const int DefaultMaxDim = 360;

    private static string _dir;

    /// <summary>Point the cache at LiteBox's own thumbs dir, Core\litebox\cache\thumbs (created on demand).
    /// <paramref name="lbRoot"/> is kept for signature stability; the dir derives from the exe location.</summary>
    public static void Init(string lbRoot)
        => _dir = Path.Combine(LiteBoxPaths.Data, "cache", "thumbs");

    /// <summary>The thumbs directory (ROOT), created on demand. Pure container: every thumbnail family
    /// lives in its own SUB-folder (<see cref="DegradedFolder"/> for the game's own image thumbnails,
    /// <see cref="VideoFolder"/> / <see cref="WebImgFolder"/> / <see cref="DocFolder"/> for the rest).</summary>
    public static string Folder => Dir;

    /// <summary>Sub-folder for the game's OWN degraded image thumbnails (the GetOrCreate/GetCachedOnly set).</summary>
    public static string DegradedFolder => Sub("degraded");

    /// <summary>Sub-folder for VIDEO thumbnails (local frames + web-video frames). See Host.Video.VideoThumbnailer.</summary>
    public static string VideoFolder => Sub("video");

    /// <summary>Sub-folder for WEB-IMAGE preview thumbnails (database / EmuMovies / Steam stand-ins in the editor).</summary>
    public static string WebImgFolder => Sub("webimg");

    /// <summary>Sub-folder for DOCUMENT thumbnails (PDF first-page renders, text/comic previews). See the Documents editor.</summary>
    public static string DocFolder => Sub("docs");

    private static string Sub(string name)
    {
        var d = Path.Combine(Dir, name);
        try { Directory.CreateDirectory(d); } catch { }
        return d;
    }

    private static string Dir
    {
        get
        {
            // Init and the no-Init fallback both land under Core\litebox\cache\thumbs — everything LiteBox
            // creates lives under Core\litebox\, never loose in Core and never under Plugins\ExtendDB.
            var d = _dir ?? Path.Combine(LiteBoxPaths.Data, "cache", "thumbs");
            try { Directory.CreateDirectory(d); } catch { }
            return d;
        }
    }

    // ── Format policy ────────────────────────────────────────────────────────
    // A regroupement maps to one of THREE policies (Options → Caches; two ini csv lists drive it):
    //   • Png   — always .png  (ThumbPngRegroupements, default "ClearLogo"; e.g. disc art later). Alpha
    //             preserved AND decoded NATIVELY by GDI+ (no Magick on read) — as fast as JPEG on scroll,
    //             which lossy WebP-via-Magick was NOT. Larger on disk; the size-budget sweep covers it.
    //   • Jpg   — always .jpg  (ThumbJpgRegroupements, default "Front,Back,Screenshots"). Any alpha is
    //             flattened; the transparency check is skipped entirely (fastest generation).
    //   • Auto  — everything else. A JPEG source has no alpha → .jpg. A PNG (or anything with an alpha
    //             channel) is inspected on the RESIZED image (IsOpaque + the plugin's 3 px anti-aliased-rim
    //             tolerance): real transparency → .png, else → .jpg.
    // KEY namespace = Png ("a") vs not-Png ("o"): Jpg and Auto share the "o" key, so they never double-
    // generate — whoever runs first picks an extension, later callers HIT it (reader probes .jpg then .png).
    public enum ThumbFormat { Auto, Jpg, Png }

    private static HashSet<string> _pngRegs, _jpgRegs;
    private static HashSet<string> Regs(ref HashSet<string> cache, string key, string def)
        => cache ??= new HashSet<string>(
            (LiteBoxConfig.LoadForExe().Get(key, null) ?? def)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>Drop the cached policy sets so a live ini edit (Options → Caches) takes effect without a
    /// restart — the next FormatFor re-reads the csv lists.</summary>
    public static void InvalidateFormatCache() { _pngRegs = null; _jpgRegs = null; }

    /// <summary>The format policy of a regroupement (Png / Jpg / Auto). Unlisted → Auto.</summary>
    public static ThumbFormat FormatFor(string regroupement)
    {
        if (regroupement == null) return ThumbFormat.Auto;
        if (Regs(ref _pngRegs, "ThumbAlphaRegroupements", "ClearLogo").Contains(regroupement)) return ThumbFormat.Png;
        if (Regs(ref _jpgRegs, "ThumbJpgRegroupements", "Front,Back,Screenshots").Contains(regroupement)) return ThumbFormat.Jpg;
        return ThumbFormat.Auto;
    }

    // A format uses the alpha ("a") key namespace iff it preserves transparency (historical keepAlpha seed).
    private static bool IsAlphaKey(ThumbFormat fmt) => fmt == ThumbFormat.Png;

    // ── Alpha container (global) ──────────────────────────────────────────────
    // The transparency-preserving thumbs (Png-office AND Auto's transparent case) are stored as PNG or
    // WebP per ThumbAlphaFormat (Options → Caches; default "png"). PNG decodes NATIVELY via GDI+ — fast on
    // scroll, no Magick to read; WebP is smaller but only decodes through Magick (the scroll-stutter cause).
    private static string _alphaFmt;
    /// <summary>".png" (default) or ".webp" — the on-disk container for transparent thumbnails.</summary>
    public static string AlphaExt()
        => (_alphaFmt ??= (LiteBoxConfig.LoadForExe().Get("ThumbAlphaFormat", null) ?? "png").Trim().ToLowerInvariant())
           == "webp" ? ".webp" : ".png";
    private static string AlphaOtherExt() => AlphaExt() == ".png" ? ".webp" : ".png";
    public static void InvalidateAlphaFormat() { _alphaFmt = null; }

    // Every extension a transparent thumb of this KEY could sit under (current + the stale one after a
    // format switch) — the reader probes both; the GC marks only the CURRENT one (stale ⇒ obsolete).
    private static readonly string[] AlphaExtsAll = { ".png", ".webp" };

    /// <summary>Path of a cached resized thumbnail, generating it synchronously on first use (see the
    /// format policy). Returns null on failure. A cache HIT needs no Magick; a MISS needs Magick.</summary>
    public static string GetOrCreate(string sourcePath, int maxDim = DefaultMaxDim, bool keepAlpha = false)
        => GetOrCreate(sourcePath, keepAlpha ? ThumbFormat.Png : ThumbFormat.Auto, maxDim);

    public static string GetOrCreate(string sourcePath, ThumbFormat fmt, int maxDim = DefaultMaxDim)
    {
        KickSweep();
        var hit = CachedPath(sourcePath, maxDim, IsAlphaKey(fmt), out string targetBase);
        if (hit != null) return hit;                   // shared cache HIT — no Magick needed
        if (targetBase == null) return null;
        try { return Generate(sourcePath, targetBase, maxDim, fmt); }
        catch { return null; }                          // missing Magick (standalone) → null
    }

    /// <summary>Force-rebuild a thumbnail: drop whatever already sits under the key (any container) and
    /// generate it again from the source. The bulk generator's "Regenerate everything" path — GetOrCreate
    /// is a HIT for anything cached, and Generate itself never overwrites an existing target, so the entry
    /// has to go first. Returns null on failure (missing Magick, unreadable source).</summary>
    public static string Rebuild(string sourcePath, ThumbFormat fmt, int maxDim = DefaultMaxDim)
    {
        KickSweep();
        var targetBase = TargetBaseFor(sourcePath, maxDim, IsAlphaKey(fmt));
        if (targetBase == null) return null;
        try { File.Delete(targetBase + ".jpg"); } catch { }
        foreach (var ext in AlphaExtsAll) { try { File.Delete(targetBase + ext); } catch { } }
        try { return Generate(sourcePath, targetBase, maxDim, fmt); }
        catch { return null; }
    }

    // HIT probe. Alpha namespace → .png/.webp (either). Jpg/Auto → .jpg first (common, fastest), then the
    // alpha exts (Auto may have produced a transparent thumb). Robust to a format switch: finds whichever
    // extension is actually on disk.
    private static string CachedPath(string sourcePath, int maxDim, bool alphaKey, out string targetBase)
    {
        targetBase = TargetBaseFor(sourcePath, maxDim, alphaKey);
        if (targetBase == null) return null;
        if (!alphaKey)
        {
            string j = targetBase + ".jpg";
            if (File.Exists(j)) return j;
        }
        string cur = targetBase + AlphaExt();
        if (File.Exists(cur)) return cur;               // current alpha container first
        string other = targetBase + AlphaOtherExt();    // then the stale one (pre-switch), still displayable
        return File.Exists(other) ? other : null;
    }

    // ── Size-budget sweep ────────────────────────────────────────────────────
    // Neither the plugin nor LiteBox ever pruned this cache (the plugin's header documented the
    // missing sweep as a TODO) — the resized set AND the /thumbs webimg source set grew forever.
    // One background sweep per process, kicked on first cache use: when the whole tree (root +
    // video/webimg/docs) exceeds MaxBytes, the oldest files (LastWriteTimeUtc) are deleted down to
    // TrimToBytes. A deleted entry simply regenerates on next use — the key scheme is untouched.
    private const long SweepMaxBytes = 500L * 1024 * 1024;
    private const long SweepTrimToBytes = 400L * 1024 * 1024;
    private static int _sweepStarted;

    private static void KickSweep()
    {
        if (Interlocked.Exchange(ref _sweepStarted, 1) == 1) return;
        _ = Task.Run(() =>
        {
            try
            {
                if (!LiteBoxConfig.LoadForExe().GetBool("CleanThumbsBudget", true)) return;   // Options → Caches opt-out
                var files = new DirectoryInfo(Dir).GetFiles("*", SearchOption.AllDirectories);
                long total = 0;
                foreach (var f in files) total += f.Length;
                if (total <= SweepMaxBytes) return;
                Array.Sort(files, (a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
                foreach (var f in files)
                {
                    if (total <= SweepTrimToBytes) break;
                    try { long len = f.Length; f.Delete(); total -= len; } catch { }
                }
            }
            catch { }
        });
    }

    /// <summary>Cached thumbnail path if it ALREADY exists, else null. Never runs
    /// Magick — instant, safe in a hot UI path (.jpg probed first for the Jpg/Auto class).</summary>
    public static string GetCachedOnly(string sourcePath, int maxDim = DefaultMaxDim, bool keepAlpha = false)
        => CachedPath(sourcePath, maxDim, keepAlpha, out _);
    public static string GetCachedOnly(string sourcePath, ThumbFormat fmt, int maxDim = DefaultMaxDim)
        => CachedPath(sourcePath, maxDim, IsAlphaKey(fmt), out _);

    // ── Async generation queue ───────────────────────────────────────────────
    // On a MISS the UI shows the full original immediately and enqueues the thumb
    // here, so it exists (HIT) next time without ever blocking the display. Dedup
    // by target path; bounded concurrency so a fast browse doesn't saturate the CPU.
    private static readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim _gate = new(Math.Max(1, Environment.ProcessorCount / 2));

    /// <summary>Queue background generation of a thumbnail (no-op if it already
    /// exists or is already queued). Fire-and-forget; never throws.</summary>
    public static void EnqueueGenerate(string sourcePath, int maxDim = DefaultMaxDim, bool keepAlpha = false)
        => EnqueueGenerate(sourcePath, keepAlpha ? ThumbFormat.Png : ThumbFormat.Auto, maxDim);

    public static void EnqueueGenerate(string sourcePath, ThumbFormat fmt, int maxDim = DefaultMaxDim)
    {
        KickSweep();
        bool alphaKey = IsAlphaKey(fmt);
        var hit = CachedPath(sourcePath, maxDim, alphaKey, out string targetBase);
        if (hit != null || targetBase == null) return;
        if (!_pending.TryAdd(targetBase, 0)) return;    // already generating/queued (dedupe on the ext-less base)
        _ = Task.Run(async () =>
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            // A game is running: this queue is OPPORTUNISTIC (a thumb the UI wanted and will ask for again
            // next time it displays that image), so drop it rather than decode behind the game. Checked
            // after the gate, so a queue built up just before the launch drains instead of grinding on.
            // The BULK generator is unaffected — it calls GetOrCreate synchronously, never this queue.
            try { if (!HostLaunch.GameRunning && CachedPath(sourcePath, maxDim, alphaKey, out _) == null) Generate(sourcePath, targetBase, maxDim, fmt); }
            catch { }
            finally { _gate.Release(); _pending.TryRemove(targetBase, out _); }
        });
    }

    // Resolve the on-disk thumbnail path WITHOUT extension (size-versioned key); null if the source
    // is missing/unreadable. No Magick, no generation.
    private static string TargetBaseFor(string sourcePath, int maxDim, bool keepAlpha)
    {
        if (string.IsNullOrEmpty(sourcePath)) return null;
        long size;
        try { var fi = new FileInfo(sourcePath); if (!fi.Exists) return null; size = fi.Length; }
        catch { return null; }
        try { return Path.Combine(DegradedFolder, KeyFor(sourcePath, size, maxDim, keepAlpha)); }
        catch { return null; }
    }

    // Isolated so the JIT-time assembly-not-found (Magick absent) is caught by GetOrCreate.
    // Format: Png → always keep alpha (container = AlphaExt); Jpg → always jpg (no transparency check);
    // Auto → keep alpha only when the RESIZED image has real transparency (beyond the 3 px rim), else jpg.
    private static string Generate(string sourcePath, string targetBase, int maxDim, ThumbFormat fmt)
    {
        string target;
        var tmpGuid = Guid.NewGuid().ToString("N");
        using (var img = new ImageMagick.MagickImage(sourcePath))
        {
            img.Thumbnail(new ImageMagick.MagickGeometry($"{maxDim}x{maxDim}>"));  // shrink-to-fit only
            bool alpha = fmt == ThumbFormat.Png || (fmt == ThumbFormat.Auto && HasRealTransparency(img));
            if (alpha)
            {
                bool webp = AlphaExt() == ".webp";
                img.Format = webp ? ImageMagick.MagickFormat.WebP : ImageMagick.MagickFormat.Png;
                if (webp) img.Quality = 82;
                target = targetBase + AlphaExt();
            }
            else { img.Format = ImageMagick.MagickFormat.Jpeg; img.Quality = 72; target = targetBase + ".jpg"; }
            img.Strip();
            img.Write(target + "." + tmpGuid + ".tmp");
        }
        var tmp = target + "." + tmpGuid + ".tmp";
        try { File.Move(tmp, target, overwrite: false); }
        catch { try { File.Delete(tmp); } catch { } }
        return File.Exists(target) ? target : null;
    }

    // Ported from ExtendDB's ImageSaveWithFormatPatch (IsOpaque + CheckOnlyBorderTransparency): real
    // transparency = an alpha < 210 pixel OUTSIDE the 3 px border rim (a thin anti-aliased transparent
    // ring around an opaque body is safe to JPEG). Runs on the already-resized ≤360 px image — cheap.
    private static bool HasRealTransparency(ImageMagick.MagickImage img)
    {
        try
        {
            if (!img.HasAlpha) return false;
            if (img.IsOpaque) return false;
            int w = (int)img.Width, h = (int)img.Height;
            const int border = 3;
            const byte threshold = 210;
            using var pixels = img.GetPixels();
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    byte a = (byte)(pixels.GetPixel(x, y).GetChannel(3) >> 8);
                    if (a >= threshold) continue;
                    if (y >= border && y < h - border && x >= border && x < w - border) return true;
                }
            return false;
        }
        catch { return true; }   // undecidable → keep the safe (alpha-preserving) format
    }

    /// <summary>Refresh a cache file's LastWriteTimeUtc — throttled to once a day — so AGE-based purges
    /// (the webimg 30-day TTL) measure last USE, not creation. Only meaningful for families whose key
    /// does NOT include the mtime (webimg); cheap metadata write, failures swallowed.</summary>
    internal static void TouchForLru(string path)
    {
        try
        {
            var now = DateTime.UtcNow;
            if (now - File.GetLastWriteTimeUtc(path) < TimeSpan.FromDays(1)) return;
            File.SetLastWriteTimeUtc(path, now);
        }
        catch { }
    }

    /// <summary>The VALID cache filenames a source maps to under the CURRENT alpha format — the GC's
    /// mark. Png → the single alpha thumb (current container); Jpg → the single .jpg; Auto → .jpg plus the
    /// current alpha container. A stale-container file (e.g. a .webp left after switching to PNG) is NOT
    /// returned here, so the GC treats it as obsolete (see <see cref="KeyBaseFor"/>).</summary>
    internal static string[] FileNamesFor(string sourcePath, long size, int maxDim, ThumbFormat fmt)
    {
        string key = KeyFor(sourcePath, size, maxDim, IsAlphaKey(fmt));
        return fmt switch
        {
            ThumbFormat.Png => new[] { key + AlphaExt() },
            ThumbFormat.Jpg => new[] { key + ".jpg" },
            _ => new[] { key + ".jpg", key + AlphaExt() },
        };
    }

    /// <summary>The ext-less key base a source maps to — the GC collects these so it can tell an OBSOLETE
    /// file (known key, wrong/stale extension → delete now) from an UNKNOWN key (grace, may be a fresh
    /// source). Returns both the alpha and opaque namespace bases (a regroupement uses one, but the GC
    /// marks by regroupement anyway; supplying both is harmless and keeps callers simple).</summary>
    internal static string KeyBaseFor(string sourcePath, long size, int maxDim, ThumbFormat fmt)
        => KeyFor(sourcePath, size, maxDim, IsAlphaKey(fmt));

    // Byte-identical to ExtendDB.Web.Theme.ThumbCache.KeyFor — do NOT change.
    private static string KeyFor(string sourcePath, long size, int maxDim, bool keepAlpha)
    {
        var seed = (sourcePath.ToLowerInvariant()) + "|" + size + "|" + maxDim + "|" + (keepAlpha ? "a" : "o");
        byte[] hash = SHA1.HashData(Encoding.UTF8.GetBytes(seed));
        var sb = new StringBuilder(24);
        for (int i = 0; i < 8; i++) sb.Append(hash[i].ToString("x2")); // 16 hex chars
        sb.Append('_').Append(size).Append('_').Append(maxDim).Append(keepAlpha ? "a" : "");
        return sb.ToString();
    }
}
