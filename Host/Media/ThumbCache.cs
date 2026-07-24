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
    // keepAlpha=true ("alpha class" — regroupements in ThumbWebpRegroupements, ClearLogo by default,
    // configurable to add e.g. disc art later): ALWAYS .webp, readers check .webp directly.
    // keepAlpha=false: ADAPTIVE — the generator inspects the resized image and writes .webp only when it
    // carries REAL transparency (Magick IsOpaque + the plugin's border heuristic: alpha < 210 tolerated in
    // a 3 px rim), .jpg otherwise. Readers therefore check .jpg FIRST (the overwhelmingly common, fastest
    // to decode), then .webp. Same key seed either way — only the extension varies by content.
    private static HashSet<string> _alphaRegs;

    /// <summary>True when the regroupement belongs to the always-WebP class (ThumbWebpRegroupements
    /// csv in LiteBox.ini, default "ClearLogo").</summary>
    public static bool IsAlphaRegroupement(string regroupement)
    {
        var set = _alphaRegs ??= new HashSet<string>(
            (LiteBoxConfig.LoadForExe().Get("ThumbWebpRegroupements", null) ?? "ClearLogo")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);
        return regroupement != null && set.Contains(regroupement);
    }

    /// <summary>Path of a cached resized thumbnail for <paramref name="sourcePath"/>,
    /// generating it synchronously on first use (see the format policy above). Returns null on any
    /// failure (caller serves the original). A cache HIT needs no Magick; a MISS needs Magick.</summary>
    public static string GetOrCreate(string sourcePath, int maxDim = DefaultMaxDim, bool keepAlpha = false)
    {
        KickSweep();
        var hit = CachedPath(sourcePath, maxDim, keepAlpha, out string targetBase);
        if (hit != null) return hit;                   // shared cache HIT — no Magick needed
        if (targetBase == null) return null;
        try { return Generate(sourcePath, targetBase, maxDim, keepAlpha); }
        catch { return null; }                          // missing Magick (standalone) → null
    }

    // HIT probe: .webp direct for the alpha class; .jpg then .webp for the adaptive class.
    private static string CachedPath(string sourcePath, int maxDim, bool keepAlpha, out string targetBase)
    {
        targetBase = TargetBaseFor(sourcePath, maxDim, keepAlpha);
        if (targetBase == null) return null;
        if (keepAlpha)
        {
            string w = targetBase + ".webp";
            return File.Exists(w) ? w : null;
        }
        string j = targetBase + ".jpg";
        if (File.Exists(j)) return j;
        string w2 = targetBase + ".webp";
        return File.Exists(w2) ? w2 : null;
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
    /// Magick — instant, safe in a hot UI path (.jpg probed first for the adaptive class).</summary>
    public static string GetCachedOnly(string sourcePath, int maxDim = DefaultMaxDim, bool keepAlpha = false)
        => CachedPath(sourcePath, maxDim, keepAlpha, out _);

    // ── Async generation queue ───────────────────────────────────────────────
    // On a MISS the UI shows the full original immediately and enqueues the thumb
    // here, so it exists (HIT) next time without ever blocking the display. Dedup
    // by target path; bounded concurrency so a fast browse doesn't saturate the CPU.
    private static readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim _gate = new(Math.Max(1, Environment.ProcessorCount / 2));

    /// <summary>Queue background generation of a thumbnail (no-op if it already
    /// exists or is already queued). Fire-and-forget; never throws.</summary>
    public static void EnqueueGenerate(string sourcePath, int maxDim = DefaultMaxDim, bool keepAlpha = false)
    {
        KickSweep();
        var hit = CachedPath(sourcePath, maxDim, keepAlpha, out string targetBase);
        if (hit != null || targetBase == null) return;
        if (!_pending.TryAdd(targetBase, 0)) return;    // already generating/queued (dedupe on the ext-less base)
        _ = Task.Run(async () =>
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try { if (CachedPath(sourcePath, maxDim, keepAlpha, out _) == null) Generate(sourcePath, targetBase, maxDim, keepAlpha); }
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
    // Adaptive class (keepAlpha=false): the format is decided from the RESIZED pixels — .webp only when
    // real transparency survives (beyond the plugin's 3 px anti-aliased-rim tolerance), else .jpg.
    private static string Generate(string sourcePath, string targetBase, int maxDim, bool keepAlpha)
    {
        string target;
        var tmpGuid = Guid.NewGuid().ToString("N");
        using (var img = new ImageMagick.MagickImage(sourcePath))
        {
            img.Thumbnail(new ImageMagick.MagickGeometry($"{maxDim}x{maxDim}>"));  // shrink-to-fit only
            bool webp = keepAlpha || HasRealTransparency(img);
            if (webp) { img.Format = ImageMagick.MagickFormat.WebP; img.Quality = 82; }
            else { img.Format = ImageMagick.MagickFormat.Jpeg; img.Quality = 72; }
            target = targetBase + (webp ? ".webp" : ".jpg");
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

    /// <summary>The cache FILENAME candidates a source maps to — used by the mark-and-sweep GC
    /// (ThumbGc) to build its valid-set without touching the disk. Alpha class → the single .webp;
    /// adaptive class → both extensions (the generator picks one by content, the GC marks either).</summary>
    internal static string[] FileNamesFor(string sourcePath, long size, int maxDim, bool keepAlpha)
    {
        string key = KeyFor(sourcePath, size, maxDim, keepAlpha);
        return keepAlpha ? new[] { key + ".webp" } : new[] { key + ".jpg", key + ".webp" };
    }

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
