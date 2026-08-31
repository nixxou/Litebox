// What a client actually pushed, and where each piece of it belongs.
//
// A RomM upload is not always one save. Freegosy sends a single file under its real basename when it has
// exactly one and it is not a directory; otherwise it builds a ZIP holding a "freegosy_sync.txt" marker
// plus every file under ITS original basename (read from its source: save_sync_service.dart). Its
// background queue always sends a zip, even for one save. And it deliberately rebuilds the archive with a
// millisecond stamp in the name "to bypass server-side deduplication" — so two pushes of the same save
// are two different sets of bytes, and comparing the upload's bytes tells us nothing at all.
//
// So the archive is opened and the pieces inside are what gets judged. Each carries the name of the ROM
// it came from (the emulator names a save after the file it ran) and its own modification time (ZIP
// stores one), which is the only honest date available: the time the SAVE was written, not the time the
// client happened to package it.
//
// The plan per (ROM, kind) — saves and states are different groups and never mix:
//
//   • drop anything whose content we already hold, live or archived;
//   • the newest of what remains is the candidate for the live save;
//   • it replaces the live save only if it is NEWER; the displaced save is archived first, labelled;
//   • the rest become vault copies dated by their own content, so they sit where they belong in the
//     history rather than at the top just because they arrived last;
//   • and a copy older than everything the cap already holds is skipped instead of evicting a newer one.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using LbApiHost.Host.Diag;
using LbApiHost.Host.Saves;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Romm;

/// <summary>One file a client pushed, once the packaging is off.</summary>
internal sealed class PushCandidate
{
    /// <summary>Basename as the client sent it, or as it sits inside the archive.</summary>
    public string FileName = "";

    /// <summary>Where it has been written for us to work with. Deleted with the request's temp folder.</summary>
    public string TempPath = "";

    /// <summary>When the CONTENT was written, from the archive entry or the file itself.</summary>
    public DateTime ModifiedUtc;

    public bool IsState;
    public string Md5 = "";

    /// <summary>The archive entry it belongs to, or null for a game that is not an archive.</summary>
    public SaveEntry? Entry;

    /// <summary>What groups candidates together: one ROM, one kind.</summary>
    public string Key => (Entry?.Key ?? "") + "|" + (IsState ? "s" : "f");
}

internal static class RommPushPlanner
{
    private const string BundleMarker = "freegosy_sync.txt";

    /// <summary>Every save inside what was uploaded. A single file answers with itself; an archive is
    /// opened and its pieces are returned, the sync marker excluded.
    ///
    /// The bytes decide, not the name: a client may store a zip under any name, and Freegosy sniffs the
    /// bytes on its own restore path for exactly that reason.</summary>
    public static List<PushCandidate> Expand(string uploadedPath, string uploadedName, string workDir)
    {
        var list = new List<PushCandidate>();
        try
        {
            if (!LooksLikeZip(uploadedPath))
            {
                list.Add(Describe(uploadedName, uploadedPath, File.GetLastWriteTimeUtc(uploadedPath)));
                return list;
            }

            Directory.CreateDirectory(workDir);
            using var zip = ZipFile.OpenRead(uploadedPath);
            foreach (var e in zip.Entries)
            {
                if (e.Length == 0 && e.Name.Length == 0) continue;                    // a directory entry
                if (string.Equals(e.Name, BundleMarker, StringComparison.OrdinalIgnoreCase)) continue;
                if (e.Name.Length == 0) continue;

                var dest = Path.Combine(workDir, Guid.NewGuid().ToString("N") + "_" + Sanitize(e.Name));
                e.ExtractToFile(dest, overwrite: true);
                // The archive's own timestamp is the save's, not the packaging moment.
                var when = e.LastWriteTime.UtcDateTime;
                try { File.SetLastWriteTimeUtc(dest, when); } catch { }
                list.Add(Describe(e.Name, dest, when));
            }
            RommTrace.Note($"bundle: {list.Count} file(s) inside");
        }
        catch (Exception ex)
        {
            LbLog.Warn("romm", "could not open the upload: " + ex.Message);
            RommTrace.Note("could not open the upload: " + ex.Message);
            if (list.Count == 0)
                try { list.Add(Describe(uploadedName, uploadedPath, File.GetLastWriteTimeUtc(uploadedPath))); }
                catch { }
        }
        return list;
    }

    private static PushCandidate Describe(string name, string path, DateTime when)
    {
        var c = new PushCandidate { FileName = Path.GetFileName(name), TempPath = path, ModifiedUtc = when };
        c.IsState = IsStateName(c.FileName);
        try { c.Md5 = SaveManager.FileMd5(path); } catch { }
        return c;
    }

    /// <summary>A savestate by its name — the same shapes Freegosy refuses to restore as a save:
    /// ".state", ".state." somewhere inside, ".stateN".</summary>
    public static bool IsStateName(string name)
    {
        var n = (name ?? "").ToLowerInvariant();
        if (n.EndsWith(".state", StringComparison.Ordinal)) return true;
        if (n.Contains(".state.", StringComparison.Ordinal)) return true;
        int i = n.LastIndexOf(".state", StringComparison.Ordinal);
        return i >= 0 && i + 6 < n.Length && n.Substring(i + 6).All(char.IsDigit);
    }

    private static bool LooksLikeZip(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            return fs.Length >= 4 && fs.ReadByte() == 'P' && fs.ReadByte() == 'K'
                && fs.ReadByte() == 3 && fs.ReadByte() == 4;
        }
        catch { return false; }
    }

    private static string Sanitize(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        var s = new string((name ?? "").Where(c => !bad.Contains(c)).ToArray()).Trim();
        return s.Length == 0 ? "save" : s;
    }
}
