// Per-file metadata store for the ":crc32" and ":info" streams, byte-compatible with ExtendDB's
// ExtendDB.Utility.FileMetadataStorage. Two goals:
//   1. READ metadata ExtendDB (or a previous LiteBox download) wrote — so owned-detection reads the stored
//      CRC instead of recomputing.
//   2. WRITE metadata in ExtendDB's exact on-disk format — so a file LiteBox downloads is indistinguishable
//      from one ExtendDB downloaded, and a later ExtendDB load reads it back.
//
// Backend selection mirrors ExtendDB: NTFS Alternate Data Streams ("<file>:<stream>") on volumes that
// support named streams, else a JSON sidecar at "<dir>/.ads/<name>.json" holding {"crc32","info","lock"}.
// When the ExtendDB assembly is present we DELEGATE to its FileMetadataStorage by reflection, so its
// ForceSidecarStorage flag and cached volume-capability map stay authoritative and we never disagree
// with it about where a given file's metadata lives.

#nullable enable

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LbApiHost.Host.Media;

internal static class FileMetaStore
{
    public const string StreamCrc32 = ":crc32";
    public const string StreamInfo = ":info";

    private const string SidecarFolder = ".ads";
    private const string SidecarSuffix = ".json";

    // ── ExtendDB delegation (reflected once) ─────────────────────────────────
    private static bool _probed;
    private static MethodInfo? _extRead, _extWrite, _extHas;

    private static void Probe()
    {
        if (_probed) return;
        _probed = true;
        try
        {
            var asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "ExtendDB");
            var t = asm?.GetType("ExtendDB.Utility.FileMetadataStorage");
            if (t != null)
            {
                _extRead = t.GetMethod("Read", new[] { typeof(string), typeof(string) });
                _extWrite = t.GetMethod("Write", new[] { typeof(string), typeof(string), typeof(string) });
                _extHas = t.GetMethod("Has", new[] { typeof(string), typeof(string) });
            }
        }
        catch { }
    }

    /// <summary>Reads one stream, or null when absent. Delegates to ExtendDB when loaded.</summary>
    public static string? Read(string filePath, string streamName)
    {
        if (string.IsNullOrEmpty(filePath) || string.IsNullOrEmpty(streamName)) return null;
        Probe();
        if (_extRead != null)
        {
            try { return _extRead.Invoke(null, new object[] { filePath, streamName }) as string; } catch { }
        }
        return SupportsAds(filePath) ? ReadAds(filePath, streamName) : ReadSidecar(filePath, streamName);
    }

    /// <summary>Writes one stream (best-effort). Delegates to ExtendDB when loaded.</summary>
    public static bool Write(string filePath, string streamName, string content)
    {
        if (string.IsNullOrEmpty(filePath) || string.IsNullOrEmpty(streamName)) return false;
        Probe();
        if (_extWrite != null)
        {
            try { var r = _extWrite.Invoke(null, new object[] { filePath, streamName, content }); return r is not bool b || b; } catch { }
        }
        return SupportsAds(filePath) ? WriteAds(filePath, streamName, content) : WriteSidecar(filePath, streamName, content);
    }

    /// <summary>Presence test without reading. Delegates to ExtendDB when loaded.</summary>
    public static bool Has(string filePath, string streamName)
    {
        if (string.IsNullOrEmpty(filePath) || string.IsNullOrEmpty(streamName)) return false;
        Probe();
        if (_extHas != null) { try { return _extHas.Invoke(null, new object[] { filePath, streamName }) is bool b && b; } catch { } }
        return SupportsAds(filePath) ? File.Exists(filePath + streamName) : ReadSidecar(filePath, streamName) != null;
    }

    // ── Native ADS backend ───────────────────────────────────────────────────
    private static string? ReadAds(string filePath, string streamName)
    {
        try
        {
            string p = filePath + streamName;
            if (!File.Exists(p)) return null;
            string c = File.ReadAllText(p).Trim();
            return string.IsNullOrEmpty(c) ? null : c;
        }
        catch { return null; }
    }

    private static bool WriteAds(string filePath, string streamName, string content)
    {
        try { File.WriteAllText(filePath + streamName, content); return true; }
        catch { return false; }
    }

    // ── Native sidecar backend (same shape as ExtendDB's SidecarPayload) ──────
    private sealed class Sidecar
    {
        [JsonPropertyName("crc32")] public string? Crc32 { get; set; }
        [JsonPropertyName("info")] public string? Info { get; set; }
        [JsonPropertyName("lock")] public string? Lock { get; set; }

        public string? Get(string stream) => stream switch { StreamCrc32 => Crc32, StreamInfo => Info, ":lock" => Lock, _ => null };
        public void Set(string stream, string? v) { if (stream == StreamCrc32) Crc32 = v; else if (stream == StreamInfo) Info = v; else if (stream == ":lock") Lock = v; }
        public bool IsEmpty => string.IsNullOrEmpty(Crc32) && string.IsNullOrEmpty(Info) && string.IsNullOrEmpty(Lock);
    }

    private static readonly JsonSerializerOptions SidecarOpts = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
    };

    private static string? SidecarPath(string filePath)
    {
        string? dir = Path.GetDirectoryName(filePath);
        string name = Path.GetFileName(filePath);
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(name)) return null;
        return Path.Combine(dir, SidecarFolder, name + SidecarSuffix);
    }

    private static Sidecar? LoadSidecar(string filePath)
    {
        try
        {
            string? p = SidecarPath(filePath);
            if (p == null || !File.Exists(p)) return null;
            string json = File.ReadAllText(p);
            return string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<Sidecar>(json, SidecarOpts);
        }
        catch { return null; }
    }

    private static string? ReadSidecar(string filePath, string streamName)
    {
        var s = LoadSidecar(filePath);
        var v = s?.Get(streamName);
        return string.IsNullOrEmpty(v) ? null : v;
    }

    private static bool WriteSidecar(string filePath, string streamName, string content)
    {
        try
        {
            string? p = SidecarPath(filePath);
            if (p == null) return false;
            var s = LoadSidecar(filePath) ?? new Sidecar();
            s.Set(streamName, content);
            string? folder = Path.GetDirectoryName(p);
            if (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder))
            {
                var di = Directory.CreateDirectory(folder);
                try { di.Attributes |= FileAttributes.Hidden | FileAttributes.System; } catch { }
            }
            if (s.IsEmpty) { if (File.Exists(p)) File.Delete(p); return true; }
            File.WriteAllText(p, JsonSerializer.Serialize(s, SidecarOpts));
            return true;
        }
        catch { return false; }
    }

    // ── Volume "supports ADS" probe (native path only) ───────────────────────
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeInformationW(
        string rootPathName, StringBuilder? volumeNameBuffer, int volumeNameSize,
        out uint volumeSerialNumber, out uint maximumComponentLength, out uint fileSystemFlags,
        StringBuilder fileSystemNameBuffer, int fileSystemNameSize);

    private const uint FILE_NAMED_STREAMS = 0x00040000;
    private static readonly ConcurrentDictionary<char, bool> _driveAds = new();

    /// <summary>Test-only: force the sidecar backend regardless of the volume (exercises the non-NTFS path).</summary>
    internal static bool ForceSidecarForTests;

    /// <summary>Whether this file's volume supports named streams (cached per drive letter). Exposed for
    /// LiteBox-own streams (DupCheckAds) that use the same ADS-else-sidecar strategy but their OWN sidecar
    /// file — the shared .ads JSON has ExtendDB's fixed {crc32,info,lock} shape and must not grow fields
    /// ExtendDB would drop on rewrite.</summary>
    internal static bool VolumeSupportsAds(string filePath) => SupportsAds(filePath);

    /// <summary>The ".ads" sidecar folder path for a file (shared with the ExtendDB-format sidecars), or
    /// null when the path can't be split. The folder is created hidden+system on first write.</summary>
    internal static string? SidecarDirOf(string filePath)
    {
        string? dir = Path.GetDirectoryName(filePath);
        return string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, SidecarFolder);
    }

    private static bool SupportsAds(string filePath)
    {
        if (ForceSidecarForTests) return false;
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(filePath));
            if (string.IsNullOrEmpty(root) || root.Length < 2 || root[1] != ':') return false;   // UNC/unknown → sidecar
            char letter = char.ToUpperInvariant(root[0]);
            if (_driveAds.TryGetValue(letter, out var known)) return known;
            bool ads = false;
            try
            {
                string probe = letter + ":\\";
                var fsName = new StringBuilder(64);
                if (GetVolumeInformationW(probe, null, 0, out _, out _, out uint flags, fsName, fsName.Capacity))
                    ads = (flags & FILE_NAMED_STREAMS) != 0;
            }
            catch { }
            _driveAds[letter] = ads;
            return ads;
        }
        catch { return false; }
    }
}
