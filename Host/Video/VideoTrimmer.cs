// Cut a video down to [start, end] WITHOUT re-encoding: ffmpeg demuxes the original packets and remuxes them
// into a new container (-c copy). Nothing is decoded, nothing is re-compressed — the picture is bit-for-bit the
// one that was there, and a 30 s trailer is cut in ~50 ms.
//
// THE KEYFRAME CONSTRAINT is not a limitation of this code, it's what "no re-encode" means: a stream copy can
// only begin at a keyframe (everything after one is decoded RELATIVE to it), so the caller must hand us cut
// points that are already snapped to the file's own keyframes (FfmpegService.Keyframes / Snap). We snap the END
// too, so the last GOP is complete — cutting mid-GOP leaves a tail of frames that reference data we dropped.
//
// THE ADS IS PRESERVED VERBATIM. The trimmed file is a NEW file taking the old one's place, so its NTFS streams
// would be born empty; we capture ":crc32" and ":info" before and write the exact same bytes back after. That
// is deliberate, and not just because it was asked for: ":crc32" holds the CRC of the DATABASE ROW this video
// came from — the key ExtendDB and LiteBox dedup on. Recomputing it against the trimmed bytes would make the
// file a stranger, and the next metadata scan would happily download the untrimmed original all over again.
// A trimmed video therefore keeps saying "I am database row X, already owned", which is exactly true.

#nullable enable

using System;
using System.Globalization;
using System.IO;
using LbApiHost.Host.Media;

namespace LbApiHost.Host.Video;

internal static class VideoTrimmer
{
    /// <summary>Shortest clip we'll produce. Below this the user is almost certainly mis-clicking.</summary>
    public const double MinLengthSec = 0.5;

    /// <summary>
    /// The only containers we offer to trim. A stream copy is only as good as the target muxer: mp4 and mkv
    /// take the original packets back cleanly, whereas the older containers a LaunchBox video folder can hold
    /// (avi, wmv, flv…) each have their own timestamp quirks and turn a "lossless" cut into a desync bug hunt.
    /// Everything still PLAYS — only the ✂ button is withheld.
    /// </summary>
    public static bool CanTrim(string path)
    {
        string e = Path.GetExtension(path ?? "").ToLowerInvariant();
        return e == ".mp4" || e == ".mkv";
    }

    /// <summary>
    /// Replace <paramref name="path"/> with its [start, end] slice, in place, keeping the ADS. The original is
    /// only deleted once the new file has been produced AND probed — a failed ffmpeg leaves the video untouched.
    /// Returns false with a human-readable <paramref name="error"/> on any failure.
    /// </summary>
    public static bool Cut(string path, double startSec, double endSec, out string error)
    {
        error = "";
        if (!FfmpegService.Available) { error = "ffmpeg isn't available (LaunchBox\\ThirdParty\\FFMPEG)."; return false; }
        if (!File.Exists(path)) { error = "the file is gone."; return false; }
        if (!CanTrim(path)) { error = "only mp4 and mkv can be cut without re-encoding."; return false; }
        if (endSec - startSec < MinLengthSec) { error = $"the selection is shorter than {MinLengthSec:0.#}s."; return false; }

        string dir = Path.GetDirectoryName(path) ?? "";
        string ext = Path.GetExtension(path);
        // Same folder (so the swap is a rename, not a cross-volume copy) but a name that CANNOT be mistaken for
        // one of the game's videos: the scan matches on the "<sanitized title>-NN" prefix, and this matches
        // nothing. A crash mid-cut therefore leaves litter, never a phantom video on the page.
        string tmp = Path.Combine(dir, "~litebox-trim-" + Guid.NewGuid().ToString("N") + ext);

        try
        {
            // Read the ADS BEFORE anything touches the file (raw strings — we never re-interpret them).
            string crc = FileMetaStore.Read(path, FileMetaStore.StreamCrc32);
            string info = FileMetaStore.Read(path, FileMetaStore.StreamInfo);

            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }

            // -ss / -to BEFORE -i: both are then on the INPUT's original timeline, which is the timeline our
            // keyframe timestamps live on. (As output options they're relative to the shifted output clock and
            // silently mean something else — measured.) -map 0 keeps every stream, -c copy re-encodes nothing.
            string args =
                $"-v error -y -ss {S(startSec)} -to {S(endSec)} -i \"{path}\" " +
                $"-map 0 -c copy -avoid_negative_ts make_zero \"{tmp}\"";

            var (code, _, err) = FfmpegService.Run(FfmpegService.FfmpegExe!, args, 180_000);
            if (code != 0 || !File.Exists(tmp) || new FileInfo(tmp).Length == 0)
            {
                error = "ffmpeg failed" + (string.IsNullOrWhiteSpace(err) ? "." : ":\n" + err.Trim());
                TryDelete(tmp);
                return false;
            }

            // Prove the result is a real, playable container before we throw the original away.
            if (FfmpegService.Duration(tmp) <= 0)
            {
                error = "the cut produced an unplayable file — the original was left untouched.";
                TryDelete(tmp);
                return false;
            }

            File.Delete(path);
            File.Move(tmp, path);

            // Put the provenance back, byte for byte. (On the sidecar backend the record is keyed by PATH and
            // never went away — rewriting the same values is simply a no-op there.)
            if (!string.IsNullOrEmpty(crc)) FileMetaStore.Write(path, FileMetaStore.StreamCrc32, crc);
            if (!string.IsNullOrEmpty(info)) FileMetaStore.Write(path, FileMetaStore.StreamInfo, info);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            TryDelete(tmp);
            return false;
        }
    }

    private static string S(double sec) => sec.ToString("0.###", CultureInfo.InvariantCulture);
    private static void TryDelete(string p) { try { if (File.Exists(p)) File.Delete(p); } catch { } }
}
