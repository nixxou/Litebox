// ─────────────────────────────────────────────────────────────────────────────
// ROM extractor (ArchiveMGS) — LRU eviction + size-band policy. Slice R3.
// ─────────────────────────────────────────────────────────────────────────────
//
// Cache layout:
//     <cacheRoot>\<SIG>\<P|F>[\<subdir>]\<file>   ← persistent, one folder per archive
//     <cacheRoot>\tmp\...                          ← ephemeral (out-of-band / Title rename)
//
// The eviction UNIT is a top-level <SIG> folder: it holds exactly one archive's
// extraction (one file for a standalone rom, cue+bin for a disc, …), so deleting
// the whole folder is always coherent. The "tmp" folder is ephemeral, never counted
// nor evicted here — it's purged explicitly on game exit (PurgeTmp).
//
// Ported verbatim from ExtendDB's ArchiveCacheEvictor; only the log sink changed
// (LbLog instead of the plugin's Log).

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LbApiHost.Host.Diag;

namespace LbApiHost.Host.Rom;

internal static class ArchiveCacheEvictor
{
    /// <summary>Ephemeral sub-folder under the cache root — holds out-of-band / Title-renamed
    /// extractions. Not counted, not evicted.</summary>
    public const string TmpFolderName = "tmp";

    /// <summary>Whether an unpacked size qualifies for the persistent cache, i.e. it is inside the
    /// [minMb, maxMb] band. Outside the band the caller extracts to <c>\tmp</c> instead. A zero/negative
    /// bound disables that side of the band.</summary>
    public static bool QualifiesForCache(long unpackedBytes, int minMb, int maxMb)
    {
        long min = (long)Math.Max(0, minMb) * 1024 * 1024;
        long max = (long)Math.Max(0, maxMb) * 1024 * 1024;
        if (min > 0 && unpackedBytes < min) return false;
        if (max > 0 && unpackedBytes >= max) return false;
        return true;
    }

    /// <summary>Deletes the oldest &lt;SIG&gt; folders (by LastWriteTimeUtc) until the cache total is
    /// back under <paramref name="maxBytes"/>. No-op when the budget is non-positive or the root is
    /// missing.</summary>
    public static void KeepCacheUnder(string cacheRoot, long maxBytes)
    {
        try
        {
            if (string.IsNullOrEmpty(cacheRoot) || maxBytes <= 0) return;
            if (!Directory.Exists(cacheRoot)) return;

            var dirs = new List<(string Path, long Size, DateTime When)>();
            long total = 0;
            foreach (var d in Directory.GetDirectories(cacheRoot))
            {
                if (string.Equals(Path.GetFileName(d), TmpFolderName, StringComparison.OrdinalIgnoreCase))
                    continue; // ephemeral
                long size = DirSize(d);
                DateTime when;
                try { when = Directory.GetLastWriteTimeUtc(d); } catch { when = DateTime.MinValue; }
                dirs.Add((d, size, when));
                total += size;
            }

            if (total <= maxBytes) return;

            foreach (var e in dirs.OrderBy(x => x.When))
            {
                if (total <= maxBytes) break;
                try
                {
                    Directory.Delete(e.Path, true);
                    total -= e.Size;
                    try { ArchiveCacheIndex.Remove(cacheRoot, Path.GetFileName(e.Path)); } catch { }
                    Log($"evicted {e.Path} ({e.Size / (1024 * 1024)} MB)");
                }
                catch (Exception ex) { Log("evict failed for " + e.Path + ": " + ex.Message); }
            }
        }
        catch (Exception ex) { Log("KeepCacheUnder failed: " + ex.Message); }
    }

    /// <summary>Deletes everything under <c>&lt;cacheRoot&gt;\tmp</c>. The tmp folder holds ephemeral,
    /// per-launch extractions (out-of-band size or Title-rename) that are re-produced on demand and never
    /// LRU-counted, so it must be purged explicitly — called on game exit once the emulator has released
    /// the files. Best-effort; never throws.</summary>
    public static void PurgeTmp(string cacheRoot)
    {
        try
        {
            if (string.IsNullOrEmpty(cacheRoot)) return;
            string tmp = Path.Combine(cacheRoot, TmpFolderName);
            if (!Directory.Exists(tmp)) return;
            long freed = DirSize(tmp);
            int n = 0;
            foreach (var sub in Directory.GetDirectories(tmp))
                try { Directory.Delete(sub, true); n++; } catch (Exception ex) { Log("PurgeTmp: " + sub + ": " + ex.Message); }
            foreach (var f in Directory.GetFiles(tmp))
                try { File.Delete(f); } catch { }
            if (n > 0 || freed > 0) Log($"purged tmp: {n} folder(s), {freed / (1024 * 1024)} MB freed");
        }
        catch (Exception ex) { Log("PurgeTmp failed: " + ex.Message); }
    }

    public static long DirSize(string dir)
    {
        long total = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(f).Length; } catch { }
            }
        }
        catch { }
        return total;
    }

    private static void Log(string msg) => LbLog.Info("rom", "evictor: " + msg);
}
