// Single owner of native-payload deployment into <LB>\ThirdParty\… for the LiteBox host.
//
// The payload = the native tools LiteBox needs (RAHasher + runtime deps, Everything64, Magick.Native,
// steam_api64). Its source depends on how the exe was published:
//   • self-contained single-file  → the files are EMBEDDED as resources "natives/<src>" (see LiteBox.csproj,
//     which embeds them only when $(SelfContained)); the exe is self-sufficient.
//   • framework-dependent "zip"    → the files ship LOOSE in the zip under Core\litebox\thirdparty\<src>
//     (kept there as the re-deploy source), so the exe stays small.
// EnsureDeployed picks whichever source is present, per payload entry, and writes it to
// <lbRoot>\ThirdParty\<sub>\<dst>. Normal mode = only-if-absent (never clobbers an ExtendDB copy). Refresh
// mode (run once after a version bump — see Migration) updates a changed file: identical → skip; different
// → if it's a tool SHARED with an INSTALLED ExtendDB, ask once before replacing; else overwrite.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows.Forms;

namespace LbApiHost.Host.Install;

internal static class NativeInstaller
{
    private const StringComparison OIC = StringComparison.OrdinalIgnoreCase;

    // (embedded/loose source name, ThirdParty subdir, final on-disk name). Steam is LiteBox-only; the rest
    // are SHARED with the ExtendDB plugin (same paths). KEEP in sync with the csproj EmbeddedResource list.
    private static readonly (string src, string sub, string dst)[] Payload =
    {
        ("Everything64.dll.api",          "Everything",        "Everything64.dll"),
        ("Magick.Native-Q16-x64.dll.api", "ExtendDB",          "Magick.Native-Q16-x64.dll"),
        ("RahasherExtendDB.exe",          "RetroAchievements", "RahasherExtendDB.exe"),
        ("7z.dll.api",                    "RetroAchievements", "7z.dll"),
        ("MSVCP140.dll.api",              "RetroAchievements", "MSVCP140.dll"),
        ("VCRUNTIME140.dll.api",          "RetroAchievements", "VCRUNTIME140.dll"),
        ("VCRUNTIME140_1.dll.api",        "RetroAchievements", "VCRUNTIME140_1.dll"),
        ("steam_api64.dll.api",           "Steam",             "steam_api64.dll"),
        ("pdfium.dll.api",                "Pdfium",            "pdfium.dll"),        // LiteBox-only: PDF document thumbnails
        // HID device detector (Launch rules): the exact assemblies BigBoxProfile used, loaded from
        // ThirdParty\Hid by HidThirdParty's resolver (managed) / full-path preload (native SDL2).
        ("SDL2.dll.api",                  "Hid",               "SDL2.dll"),
        ("SDL2-CS.dll.api",               "Hid",               "SDL2-CS.dll"),
        ("HidSharp.dll.api",              "Hid",               "HidSharp.dll"),
        ("InTheHand.Net.Personal.dll.api","Hid",               "InTheHand.Net.Personal.dll"),
        // ROM extractor tools (the plugin bundled these in its own thirdparty): disc-image conversion +
        // the elevated RAM-disk mount helper. Resolved by RomToolRunner / ArchiveRamDisk from
        // ThirdParty\RomExtractor[\ramdisk].
        ("chdman.exe",                    "RomExtractor",             "chdman.exe"),
        ("DolphinTool.exe",               "RomExtractor",             "DolphinTool.exe"),
        ("RamDiskHelper.exe",             @"RomExtractor\ramdisk",    "RamDiskHelper.exe"),
        ("RamDiskHelper.dll",             @"RomExtractor\ramdisk",    "RamDiskHelper.dll"),
        ("RamDiskHelper.deps.json",       @"RomExtractor\ramdisk",    "RamDiskHelper.deps.json"),
        ("RamDiskHelper.runtimeconfig.json", @"RomExtractor\ramdisk", "RamDiskHelper.runtimeconfig.json"),
        // Duplicate-image detection (LiteBox-only): ONNX Runtime + DirectML natives + the MobileNetV3
        // embedding model. Preloaded by full path from here (Host\Media\Dedup\CnnEmbedder) — keep the
        // native version in lockstep with the Microsoft.ML.OnnxRuntime.Managed package reference.
        ("onnxruntime.dll.api",           "ImageDedup",        "onnxruntime.dll"),
        ("DirectML.dll.api",              "ImageDedup",        "DirectML.dll"),
        ("mobilenetv3s_embed.onnx",       "ImageDedup",        "mobilenetv3s_embed.onnx"),
    };

    // ThirdParty sub-folders that are LiteBox-only (not shared with ExtendDB) — a refresh overwrites them
    // freely instead of prompting. The rest (ExtendDB / RetroAchievements / Everything) are shared.
    private static bool IsLiteBoxOnlySub(string sub)
        => sub is "Steam" or "Pdfium" or "ImageDedup" or "Hid" || sub.StartsWith("RomExtractor", OIC);

    // ── Uninstall support: the deployed ThirdParty targets, split by ownership (single source of truth) ──
    private static string TopSub(string sub) => sub.Split('\\', '/')[0];

    /// <summary>Relative (to lbRoot) paths of the ThirdParty native files this host deploys that are
    /// LiteBox-ONLY (Steam / Pdfium / RomExtractor) — safe for the uninstaller to always delete.</summary>
    public static IEnumerable<string> LiteBoxOnlyNativeFiles()
        => Payload.Where(e => IsLiteBoxOnlySub(e.sub)).Select(e => Path.Combine("ThirdParty", e.sub, e.dst));

    /// <summary>Relative paths of the ThirdParty native files SHARED with the ExtendDB plugin (Everything /
    /// ExtendDB\Magick / RetroAchievements) — the uninstaller removes these only when the user opts in.</summary>
    public static IEnumerable<string> SharedNativeFiles()
        => Payload.Where(e => !IsLiteBoxOnlySub(e.sub)).Select(e => Path.Combine("ThirdParty", e.sub, e.dst));

    /// <summary>Distinct top-level ThirdParty sub-dirs that are LiteBox-only (safe to remove recursively).</summary>
    public static IEnumerable<string> LiteBoxOnlySubDirs()
        => Payload.Where(e => IsLiteBoxOnlySub(e.sub)).Select(e => Path.Combine("ThirdParty", TopSub(e.sub))).Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>Distinct ThirdParty sub-dirs SHARED with ExtendDB (remove empty-only — never nuke plugin content).</summary>
    public static IEnumerable<string> SharedSubDirs()
        => Payload.Where(e => !IsLiteBoxOnlySub(e.sub)).Select(e => Path.Combine("ThirdParty", e.sub)).Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// libvlc is NOT part of our payload: LaunchBox already ships a full libvlc 3.0.23 (366 plugins) at
    /// &lt;LB&gt;\ThirdParty\VLC\x64 — the one it plays videos with. VlcService points straight at it, which is
    /// why LiteBox adds 0 MB of native video code. Nothing to deploy; this only says where it lives.
    /// </summary>
    public static string VlcDir(string lbRoot) => Path.Combine(lbRoot, "ThirdParty", "VLC", "x64");

    /// <summary>
    /// Same story for ffmpeg/ffprobe: LaunchBox ships a full build (8.1.1) at &lt;LB&gt;\ThirdParty\FFMPEG.
    /// FfmpegService points at it (video trimming / keyframe indexing). Nothing to deploy.
    /// </summary>
    public static string FfmpegDir(string lbRoot) => Path.Combine(lbRoot, "ThirdParty", "FFMPEG");

    /// <summary>Deployed pdfium.dll (PDF document thumbnails). Loaded by full path (PdfThumbnailer) so it need
    /// not sit on the DLL search path. Empty lbRoot → empty (caller treats PDF rendering as unavailable).</summary>
    public static string PdfiumPath(string? lbRoot)
        => string.IsNullOrEmpty(lbRoot) ? "" : Path.Combine(lbRoot, "ThirdParty", "Pdfium", "pdfium.dll");

    /// <summary>Deploys the payload into &lt;lbRoot&gt;\ThirdParty\… . Only-if-absent unless
    /// <paramref name="refresh"/>. Safe + cheap to call repeatedly.</summary>
    public static void EnsureDeployed(string? lbRoot, bool refresh = false)
    {
        if (string.IsNullOrEmpty(lbRoot)) return;
        try
        {
            var asm = typeof(NativeInstaller).Assembly;
            var resNames = asm.GetManifestResourceNames();
            var sharedDiff = new List<(string src, string target)>();
            bool? extendDbPresent = null;

            foreach (var (src, sub, dst) in Payload)
            {
                string target = Path.Combine(lbRoot, "ThirdParty", sub, dst);
                if (!HasSource(asm, resNames, src)) continue;   // neither embedded nor loose → nothing to do

                if (!File.Exists(target)) { Deploy(asm, resNames, src, target); continue; }   // absent → deploy
                if (!refresh) continue;                                                        // present + normal → skip
                if (SameContent(asm, resNames, src, target)) continue;                         // present + identical → skip

                if (!IsLiteBoxOnlySub(sub))   // shared with ExtendDB
                {
                    extendDbPresent ??= File.Exists(Path.Combine(lbRoot, "Plugins", "ExtendDB", "ExtendDB.dll"));
                    if (extendDbPresent == true) { sharedDiff.Add((src, target)); continue; }  // ask before touching
                }
                Deploy(asm, resNames, src, target);   // LiteBox-only, or no ExtendDB → overwrite
            }

            if (sharedDiff.Count > 0 && AskReplaceShared(sharedDiff.Select(x => Path.GetFileName(x.target))))
                foreach (var (src, target) in sharedDiff) Deploy(asm, resNames, src, target);
        }
        catch (Exception ex) { Console.WriteLine("[installer] EnsureDeployed failed: " + ex.Message); }
    }

    // ── source = embedded "natives/<src>" (self-contained) OR Core\litebox\thirdparty\<src> (framework-dependent zip) ──
    private static string? ResName(string[] resNames, string src)
        => resNames.FirstOrDefault(n => n.Equals("natives/" + src, OIC));

    private static string LoosePath(string src)
        => Path.Combine(LbApiHost.Host.LiteBoxPaths.Data, "thirdparty", src);

    private static bool HasSource(Assembly asm, string[] resNames, string src)
        => ResName(resNames, src) != null || File.Exists(LoosePath(src));

    private static Stream? OpenSource(Assembly asm, string[] resNames, string src)
    {
        var res = ResName(resNames, src);
        if (res != null) return asm.GetManifestResourceStream(res);
        var loose = LoosePath(src);
        return File.Exists(loose) ? File.OpenRead(loose) : null;
    }

    private static long SourceLen(Assembly asm, string[] resNames, string src)
    {
        var res = ResName(resNames, src);
        if (res != null) { using var s = asm.GetManifestResourceStream(res); return s?.Length ?? -1; }
        var loose = LoosePath(src);
        return File.Exists(loose) ? new FileInfo(loose).Length : -1;
    }

    private static void Deploy(Assembly asm, string[] resNames, string src, string target)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var rs = OpenSource(asm, resNames, src);
            if (rs == null) return;
            using var fs = new FileStream(target, FileMode.Create, FileAccess.Write);
            rs.CopyTo(fs);
            Console.WriteLine("[installer] deployed " + Path.GetFileName(target));
        }
        catch (Exception ex) { Console.WriteLine($"[installer] {Path.GetFileName(target)} failed: {ex.Message}"); }
    }

    // True when the source is byte-for-byte the on-disk file (length first, then MD5).
    private static bool SameContent(Assembly asm, string[] resNames, string src, string target)
    {
        try
        {
            long sl = SourceLen(asm, resNames, src);
            if (sl >= 0 && sl != new FileInfo(target).Length) return false;   // fast reject
            using var ss = OpenSource(asm, resNames, src);
            if (ss == null) return false;
            using var md5 = MD5.Create();
            byte[] srcHash = md5.ComputeHash(ss);
            using var fsr = File.OpenRead(target);
            return srcHash.SequenceEqual(md5.ComputeHash(fsr));
        }
        catch { return false; }   // on any doubt, treat as different
    }

    private static bool AskReplaceShared(IEnumerable<string> names)
    {
        try
        {
            string list = string.Join("\n  • ", names);
            return MessageBox.Show(
                "LiteBox has different versions of these tools it shares with the ExtendDB plugin:\n\n  • " + list +
                "\n\nReplace them with LiteBox's versions? (ExtendDB re-creates its own on next run if needed.)",
                "LiteBox — update shared tools?", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
        }
        catch { return false; }   // no UI available → leave them
    }
}
