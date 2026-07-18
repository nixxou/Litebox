// ─────────────────────────────────────────────────────────────────────────────
// ROM extractor (ArchiveMGS) — native archive LISTING via 7z.exe. Slice R2.
// ─────────────────────────────────────────────────────────────────────────────
//
// The plugin reads an archive's entry table with SevenZipSharp (needs 7z.dll).
// LiteBox deliberately takes no native-interop dependency (it is sensitive to
// assembly-load under self-contained publish), so instead we shell out to the
// SAME bundled 7-Zip binary the host already uses for its flat-extract fallback
// (<LB>\ThirdParty\7-Zip\7z.exe) and parse the machine-readable listing:
//
//     7z.exe l -slt -sccUTF-8 "<archive>"
//
// Each file appears as a "Path = … / Size = … / Attributes = … / CRC = …" block.
// We reproduce EXACTLY the four fields the signature depends on — Path, Size,
// CRC (hex → uint) and IsDirectory (Attributes contains 'D') — in archive
// enumeration order, because ArchiveSig.ComputeSignature hashes CRC+Size per
// entry and any deviation would split the cache <SIG> and desync the RA feature.

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using LbApiHost.Host.Diag;
using LbApiHost.Host.Media;

namespace LbApiHost.Host.Rom;

/// <summary>One raw entry as read from <c>7z.exe l -slt</c> — just the fields the
/// signature + analyzer need. <see cref="Path"/> is the full in-archive path
/// (e.g. <c>subdir/foo.smc</c>).</summary>
internal sealed class ArchiveEntry
{
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public uint Crc { get; set; }
    public bool IsDirectory { get; set; }
}

/// <summary>Common path helpers for the ROM engine: the bundled 7z.exe and the
/// LB-relative → absolute resolution (mirrors HostServices.ResolvePath /
/// ExtendDB's ResolveAbsolute).</summary>
internal static class RomPaths
{
    /// <summary>LaunchBox install root (parent of Core), or the process base dir.</summary>
    public static string LbRoot => MediaResolver.LbRoot ?? AppContext.BaseDirectory;

    /// <summary>Absolute path of the bundled 7z.exe (&lt;LB&gt;\ThirdParty\7-Zip\7z.exe).</summary>
    public static string SevenZipExe => Path.Combine(LbRoot, "ThirdParty", "7-Zip", "7z.exe");

    /// <summary>Resolve a possibly LB-relative path to an absolute one. Never throws.</summary>
    public static string ResolveAbsolute(string? p)
    {
        if (string.IsNullOrEmpty(p)) return "";
        try { return Path.IsPathRooted(p) ? p! : Path.GetFullPath(Path.Combine(LbRoot, p!)); }
        catch { return p!; }
    }
}

internal static class SevenZipList
{
    /// <summary>Runs <c>7z.exe l -slt</c> on the archive and parses its entry table.
    /// Returns entries in archive-enumeration order (the order the signature relies
    /// on). Throws <see cref="FileNotFoundException"/> when the archive or 7z.exe is
    /// missing, or <see cref="InvalidOperationException"/> when 7z exits non-zero —
    /// the caller decides whether to fall through or skip.</summary>
    public static List<ArchiveEntry> List(string archivePath)
    {
        if (string.IsNullOrEmpty(archivePath)) throw new ArgumentException("archivePath is empty");
        if (!File.Exists(archivePath)) throw new FileNotFoundException("Archive not found", archivePath);
        var exe = RomPaths.SevenZipExe;
        if (!File.Exists(exe)) throw new FileNotFoundException("7z.exe not found", exe);

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
        };
        psi.ArgumentList.Add("l");
        psi.ArgumentList.Add("-slt");        // technical, one field per line
        psi.ArgumentList.Add("-sccUTF-8");   // emit the listing as UTF-8 (correct unicode ROM names)
        psi.ArgumentList.Add(archivePath);

        string stdout;
        int exit;
        using (var proc = Process.Start(psi) ?? throw new InvalidOperationException("could not start 7z.exe"))
        {
            stdout = proc.StandardOutput.ReadToEnd();
            proc.StandardError.ReadToEnd();   // drain so a full stderr pipe can't deadlock
            proc.WaitForExit(120000);
            exit = proc.HasExited ? proc.ExitCode : -1;
        }
        // 7-Zip: 0 = OK, 1 = warning (e.g. a locked file) — both give a usable listing.
        if (exit != 0 && exit != 1)
            throw new InvalidOperationException("7z.exe exited with code " + exit);

        return Parse(stdout);
    }

    /// <summary>Parses the <c>-slt</c> body. The block BEFORE the first "----------"
    /// separator is the archive's own header (skipped); every "Path = " line after it
    /// opens a new entry.</summary>
    internal static List<ArchiveEntry> Parse(string sltOutput)
    {
        var list = new List<ArchiveEntry>();
        if (string.IsNullOrEmpty(sltOutput)) return list;

        bool started = false;
        ArchiveEntry? cur = null;
        string? attrs = null;

        void Flush()
        {
            if (cur == null) return;
            cur.IsDirectory = attrs != null && attrs.IndexOf('D') >= 0;
            list.Add(cur);
            cur = null; attrs = null;
        }

        foreach (var raw in sltOutput.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (!started)
            {
                if (line.Trim() == "----------") started = true;
                continue;
            }

            if (line.StartsWith("Path = ", StringComparison.Ordinal))
            {
                Flush();
                cur = new ArchiveEntry { Path = line.Substring(7) };
            }
            else if (cur == null) continue;
            else if (line.StartsWith("Size = ", StringComparison.Ordinal))
            {
                cur.Size = long.TryParse(line.Substring(7).Trim(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var sz) ? sz : 0;
            }
            else if (line.StartsWith("Attributes = ", StringComparison.Ordinal))
            {
                attrs = line.Substring(13).Trim();
            }
            else if (line.StartsWith("CRC = ", StringComparison.Ordinal))
            {
                var hex = line.Substring(6).Trim();
                cur.Crc = uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var c) ? c : 0u;
            }
        }
        Flush();
        return list;
    }
}
