// Shared, change-detected snapshot of GOG Galaxy's local SQLite DB
// (%ProgramData%\GOG.com\Galaxy\storage\galaxy-2.0.db).
//
// Galaxy holds the DB open in WAL mode, so reading it in place is unreliable (the
// on-disk -shm lags Galaxy's in-memory WAL index → stale reads). We copy db + -wal
// to a throwaway file and open the COPY ReadWrite (forces full WAL recovery → live
// state). That DB is tens of MB, so this layer copies it AT MOST ONCE per source
// change (size+mtime signature) and lets every consumer (install-state, and any
// future reader such as achievements) query the SAME snapshot — adding a reader
// costs a connection + a query, never another copy.

#nullable enable

using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Data.Sqlite;

namespace LbApiHost.Host;

internal static class GalaxyDb
{
    private static readonly object _lock = new();
    private static string? _snapSig;    // source signature when the snapshot was taken
    private static string? _snapPath;   // throwaway snapshot file (valid while _snapSig matches)

    // This process's snapshot file. PID-scoped: a parallel LaunchBox/BigBox/LiteBox session must
    // never collide with — nor clean up — another's snapshot.
    private static readonly string _snapFile =
        Path.Combine(Path.GetTempPath(), $"litebox-galaxy-{Environment.ProcessId}.db");

    static GalaxyDb()
    {
        // Startup: if NO other LaunchBox/BigBox/LiteBox is running, sweep ALL galaxy snapshots
        // (orphans from crashed runs); if a parallel host IS running, touch only our own pid. On
        // exit: always remove just our own pid's snapshot.
        try { CleanupAtStartup(); AppDomain.CurrentDomain.ProcessExit += (_, __) => Cleanup(); } catch { }
    }

    /// <summary>Remove THIS process's snapshot (.db + -wal + -shm). Own-pid only.</summary>
    public static void Cleanup()
    {
        lock (_lock)
        {
            foreach (var suf in new[] { "", "-wal", "-shm" })
                try { if (File.Exists(_snapFile + suf)) File.Delete(_snapFile + suf); } catch { }
            _snapSig = null; _snapPath = null;
        }
    }

    private static void CleanupAtStartup()
    {
        bool others = false;
        try
        {
            int self = Environment.ProcessId;
            foreach (var name in new[] { "LaunchBox", "BigBox", "LiteBox" })
            {
                foreach (var p in Process.GetProcessesByName(name))
                {
                    try { if (p.Id != self) others = true; } catch { } finally { try { p.Dispose(); } catch { } }
                }
                if (others) break;
            }
        }
        catch { others = true; }   // can't tell → be conservative (own-pid only)

        if (others) { Cleanup(); return; }

        // Only host running → safe to delete every leftover galaxy snapshot (both prefixes).
        try
        {
            var dir = Path.GetTempPath();
            foreach (var pat in new[] { "extenddb-galaxy-*", "litebox-galaxy-*" })
                foreach (var f in Directory.EnumerateFiles(dir, pat))
                    try { File.Delete(f); } catch { }
        }
        catch { }
        lock (_lock) { _snapSig = null; _snapPath = null; }
    }

    /// <summary>Galaxy's live DB path, or null when GOG Galaxy isn't installed.</summary>
    public static string? SourceDbPath()
    {
        try
        {
            var p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                                 "GOG.com", "Galaxy", "storage", "galaxy-2.0.db");
            return File.Exists(p) ? p : null;
        }
        catch { return null; }
    }

    /// <summary>Change signature of the DB + its -wal (size + last-write). Identical ⇒ nothing changed
    /// ⇒ a cached snapshot/result is still valid.</summary>
    public static string Sig(string src)
    {
        string One(string p) { try { var fi = new FileInfo(p); return fi.Exists ? fi.Length + ":" + fi.LastWriteTimeUtc.Ticks : "-"; } catch { return "?"; } }
        return One(src) + "|" + One(src + "-wal");
    }

    /// <summary>Runs <paramref name="read"/> against a ReadWrite connection to a fresh snapshot of
    /// galaxy-2.0.db. The DB is re-copied ONLY when its source signature changed since the last
    /// snapshot, so consumers share one copy. Returns false if GOG isn't installed or the snapshot
    /// couldn't be made.</summary>
    public static bool Read(Action<SqliteConnection> read)
    {
        var src = SourceDbPath();
        if (src == null) return false;
        lock (_lock)
        {
            try
            {
                string sig = Sig(src);
                var tmp = _snapFile;

                // (Re)copy only when the source changed (or first use / snapshot lost).
                if (_snapSig != sig || _snapPath == null || !File.Exists(_snapPath))
                {
                    foreach (var suf in new[] { "", "-wal", "-shm" })
                        try { if (File.Exists(tmp + suf)) File.Delete(tmp + suf); } catch { }
                    bool copiedMain = false, copiedWal = false;
                    try { File.Copy(src, tmp, true); copiedMain = true; } catch { }
                    bool srcHasWal = File.Exists(src + "-wal");
                    if (srcHasWal) try { File.Copy(src + "-wal", tmp + "-wal", true); copiedWal = true; } catch { }
                    if (!copiedMain || !File.Exists(tmp) || (srcHasWal && !copiedWal)) { _snapSig = null; _snapPath = null; return false; }
                    _snapPath = tmp;
                    _snapSig = sig;
                    // Verification aid: fires ONLY on an actual copy (source changed). Its absence
                    // while browsing means the snapshot is being reused (optimisation working).
                    try { var len = new FileInfo(tmp).Length; Console.WriteLine($"[galaxydb] snapshot copied ({len / 1024} KB) — source changed"); } catch { }
                }

                try { SQLitePCL.Batteries_V2.Init(); } catch { }
                // Pooling=false so Dispose truly closes the handle (else the temp files stay locked and
                // the next re-copy fails). ReadWrite (on the copy) lets SQLite recover the WAL.
                using var con = new SqliteConnection(
                    new SqliteConnectionStringBuilder { DataSource = _snapPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
                con.Open();
                try { using var ck = con.CreateCommand(); ck.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)"; ck.ExecuteNonQuery(); } catch { }
                read(con);
                return true;
            }
            catch (Exception ex) { Console.WriteLine("[galaxydb] read: " + ex.Message); return false; }
        }
    }
}
