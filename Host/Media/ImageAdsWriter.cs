// Writes ExtendDB-format per-file metadata after LiteBox downloads a web image, so the file is
// indistinguishable from one ExtendDB downloaded (and a later ExtendDB load reads it back):
//   • ":crc32" ADS = the DB-provided CRC32 as a decimal-long string (no recompute — same as CrcCache).
//   • ":info"  ADS = compact JSON of ImageInfoData with ExtendDB's short keys
//     (db,t,r,nr,crc,o,dup,ft,p,url,fs,x,y,ar). Dimensions (x/y/ar) AND the file size (fs) are the things
//     the base LaunchBox metadata DB doesn't carry, so we read them from the downloaded file here —
//     "compute the missing data".
//
// FileSize (fs): ExtendDB treats it as scrape-time provenance and doesn't compute it at Write, because its
// merged DB already supplies it. The base-LB DB has no size column, so rather than store 0 ("unknown") we
// record the downloaded file's actual length. That's the best provenance available here AND it keeps the
// "compare fs to the live FileInfo.Length to detect a post-download change" use case working (0 would break
// it). No ExtendDB path relies on fs==0 — its dedup / Image Query Tool use the live disk size, not info.fs.

#nullable enable

using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LbApiHost.Host.Media;

internal static class ImageAdsWriter
{
    private sealed class InfoDto
    {
        [JsonPropertyName("db")] public int Db { get; set; }
        [JsonPropertyName("t")] public string T { get; set; } = "";
        [JsonPropertyName("r")] public string R { get; set; } = "";
        [JsonPropertyName("nr")] public string Nr { get; set; } = "";
        [JsonPropertyName("crc")] public long Crc { get; set; }
        [JsonPropertyName("o")] public string O { get; set; } = "";
        [JsonPropertyName("dup")] public int Dup { get; set; }
        [JsonPropertyName("ft")] public string Ft { get; set; } = "";
        [JsonPropertyName("p")] public string P { get; set; } = "";
        [JsonPropertyName("url")] public string Url { get; set; } = "";
        [JsonPropertyName("fs")] public long Fs { get; set; }
        [JsonPropertyName("x")] public int X { get; set; }
        [JsonPropertyName("y")] public int Y { get; set; }
        [JsonPropertyName("ar")] public double Ar { get; set; }
    }

    private static readonly JsonSerializerOptions Opts = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The DB types that are NOT images. Their :info carries no dimensions — and must not: reading
    /// them would mean handing a whole video/PDF to GDI+ (ExtendDB writes 0/0 for these too).</summary>
    private static bool IsNonImageType(string? type) => type switch
    {
        "Video" or "VideoAdvert" or "Manual" or "Music" => true,
        _ => false,
    };

    /// <summary>Stamp a freshly-downloaded file at <paramref name="path"/> with ExtendDB-format ADS.</summary>
    public static void WriteForDownload(string path, MetadataDb.WebImage web, int dbId, string platform)
    {
        try
        {
            // CRC-32 is 32-bit UNSIGNED (0..4294967295). Normalize to the canonical unsigned value widened to
            // long (always positive), matching CrcCache's ":crc32" convention — guards against a DB that stored
            // it as a signed 32-bit int (negative for values > 0x7FFFFFFF).
            long crc = unchecked((uint)web.Crc32);
            FileMetaStore.Write(path, FileMetaStore.StreamCrc32, crc.ToString(System.Globalization.CultureInfo.InvariantCulture));

            bool nonImage = IsNonImageType(web.Type);
            var (w, h) = nonImage ? (0, 0) : ImageDims(path);
            long fs = web.FileSize;                                // extended DB knows it; base-LB doesn't
            if (fs <= 0) { try { fs = new FileInfo(path).Length; } catch { } }
            var dto = new InfoDto
            {
                Db = dbId,
                T = web.Type ?? "",
                R = web.Region ?? "",
                Nr = web.Region ?? "",
                Crc = crc,
                O = web.Origin ?? "launchbox",                     // "launchbox" for base-LB rows (the ctor defaults it)
                Dup = web.Duplicate,
                Ft = !string.IsNullOrEmpty(web.FileType) ? web.FileType : (Path.GetExtension(path) ?? ""),
                P = platform ?? "",
                Url = web.FileName ?? "",
                Fs = fs,
                X = w,
                Y = h,
                Ar = (w > 0 && h > 0) ? (double)w / h : 0.0,
            };
            FileMetaStore.Write(path, FileMetaStore.StreamInfo, JsonSerializer.Serialize(dto, Opts));

            CrcBridge.Seed(path, unchecked((uint)crc));            // owned-detection is instant right after
        }
        catch { }
    }

    // ── Header-only dimension reader for the LaunchBox image formats ──────────
    private static (int w, int h) ImageDims(string path)
    {
        try
        {
            byte[] b;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                int cap = (int)Math.Min(fs.Length, 512 * 1024);    // header lives in the first few KB
                b = new byte[cap];
                int off = 0, n;
                while (off < cap && (n = fs.Read(b, off, cap - off)) > 0) off += n;
            }

            // PNG: 89 50 4E 47 … IHDR width@16 height@20 (big-endian).
            if (b.Length >= 24 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47)
                return (BE32(b, 16), BE32(b, 20));

            // GIF: "GIF8" width@6 height@8 (little-endian, 16-bit).
            if (b.Length >= 10 && b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x38)
                return (LE16(b, 6), LE16(b, 8));

            // BMP: "BM" width@18 height@22 (little-endian, 32-bit signed).
            if (b.Length >= 26 && b[0] == 0x42 && b[1] == 0x4D)
                return (LE32(b, 18), Math.Abs(LE32(b, 22)));

            // WEBP: "RIFF"…"WEBP".
            if (b.Length >= 30 && b[0] == 0x52 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x46
                && b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50)
            {
                string fourcc = System.Text.Encoding.ASCII.GetString(b, 12, 4);
                if (fourcc == "VP8 " && b.Length >= 30)
                    return (LE16(b, 26) & 0x3FFF, LE16(b, 28) & 0x3FFF);
                if (fourcc == "VP8L" && b.Length >= 25)
                {
                    int bits = b[21] | (b[22] << 8) | (b[23] << 16) | (b[24] << 24);
                    return ((bits & 0x3FFF) + 1, ((bits >> 14) & 0x3FFF) + 1);
                }
                if (fourcc == "VP8X" && b.Length >= 30)
                    return (((b[24] | (b[25] << 8) | (b[26] << 16)) + 1), ((b[27] | (b[28] << 8) | (b[29] << 16)) + 1));
            }

            // JPEG: scan segments for a Start-Of-Frame marker.
            if (b.Length >= 4 && b[0] == 0xFF && b[1] == 0xD8)
            {
                int pos = 2;
                while (pos + 9 < b.Length)
                {
                    if (b[pos] != 0xFF) { pos++; continue; }
                    byte marker = b[pos + 1];
                    if (marker == 0xFF) { pos++; continue; }                         // fill byte
                    if (marker == 0xD8 || marker == 0xD9 || (marker >= 0xD0 && marker <= 0xD7) || marker == 0x01)
                    { pos += 2; continue; }                                           // standalone, no length
                    bool sof = (marker >= 0xC0 && marker <= 0xC3) || (marker >= 0xC5 && marker <= 0xC7)
                             || (marker >= 0xC9 && marker <= 0xCB) || (marker >= 0xCD && marker <= 0xCF);
                    if (sof) return (BE16(b, pos + 7), BE16(b, pos + 5));             // width@+7, height@+5
                    int seg = BE16(b, pos + 2);
                    if (seg <= 0) break;
                    pos += 2 + seg;
                }
            }
        }
        catch { }

        // Last resort: let GDI+ read it (already a dependency of the image editor).
        try { using var ms = new MemoryStream(File.ReadAllBytes(path)); using var img = System.Drawing.Image.FromStream(ms, false, false); return (img.Width, img.Height); }
        catch { return (0, 0); }
    }

    private static int BE16(byte[] b, int i) => (b[i] << 8) | b[i + 1];
    private static int BE32(byte[] b, int i) => (b[i] << 24) | (b[i + 1] << 16) | (b[i + 2] << 8) | b[i + 3];
    private static int LE16(byte[] b, int i) => b[i] | (b[i + 1] << 8);
    private static int LE32(byte[] b, int i) => b[i] | (b[i + 1] << 8) | (b[i + 2] << 16) | (b[i + 3] << 24);
}
