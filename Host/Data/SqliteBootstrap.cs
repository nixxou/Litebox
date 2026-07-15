// Shared SQLite connection-open boilerplate for LiteBox's two local databases (the write-back op-log
// and the options store): the same provider init + connection-string shape (Pooling=false) + Open(),
// so a future change to either (WAL mode, busy_timeout, a shared SQLitePCL provider registration) is
// one edit instead of two.

using Microsoft.Data.Sqlite;

namespace LbApiHost.Host.Data;

internal static class SqliteBootstrap
{
    /// <summary>Open (create) a SQLite connection at <paramref name="dbPath"/>. Throws on failure — every
    /// caller already wraps its own Open() in a try/catch that disables the feature on any error, so this
    /// stays a thin, un-defensive helper rather than duplicating that fallback.</summary>
    public static SqliteConnection OpenConnection(string dbPath)
    {
        try { SQLitePCL.Batteries_V2.Init(); } catch { /* may already be initialised */ }
        var csb = new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false };
        var conn = new SqliteConnection(csb.ToString());
        conn.Open();
        return conn;
    }
}
