// romm_games — une ligne par fichier jouable, et le romm_id est celui de la ligne.
//
// The whole identity model lives in this table. A row names ONE playable file of ONE game: its ROM, one
// of its versions, or one ROM extracted from an archive. Rows are never rewritten to mean something else
// and never deleted by the normal course of business, so a rom_id a client persisted keeps its meaning
// for as long as that client exists.
//
// ── Pourquoi les lignes ne meurent pas ────────────────────────────────────────
//
// A row that stops being valid — the archive changed, the version was removed, the extractor was turned
// off for that platform — is DISABLED, not deleted, and its clients fall back to the game's default row.
// That fallback is a DIFFERENT row, therefore a different rom_id: a client is never handed the wrong
// file under an identifier it already knows. It sees a new identifier and the old one leaves its list.
// This is the invariant the rest of the design hangs off.
//
// ── Les générations ──────────────────────────────────────────────────────────
//
// `disabled` is not a boolean. Each pass takes MAX(disabled)+1 as its generation and writes that number,
// so we always know WHEN a row was invalidated and can tell what this pass just did from what was
// already dead. It also makes a crashed pass harmless: a half-applied generation is simply an older one,
// and the next pass re-validates it like any other.
//
// ── Deux pièges désamorcés dans le schéma ────────────────────────────────────
//
//   • filepath and rompath are NOT NULL DEFAULT ''. SQLite treats two NULLs as distinct inside a UNIQUE
//     index, so a nullable column would let "insert if missing" create a duplicate on every pass for
//     every game that is not an extracted rom.
//
//   • clients is comma-BOUNDED — ",3,7," and not "3,7" — so LIKE '%,7,%' cannot match client 17.

#nullable enable

using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Data.Sqlite;

namespace LbApiHost.Host.Romm;

/// <summary>One row of romm_games, as the pass manipulates it.</summary>
internal sealed class RommGameRow
{
    public long RomId;
    public string GuidLb = "";
    public int PlatformId;
    public bool Emulated = true;
    public string AppId = "";
    public string FilePath = "";
    public string RomPath = "";
    public bool IsExtract;
    public List<int> Clients = new();
    public long Disabled;                 // 0 = valid, else the generation that killed it
    public DateTime? IsDefaultUtc;
    public DateTime? DisabledUtc;

    /// <summary>What the pass decided to do with it. Never persisted.</summary>
    public RommRowAction Action = RommRowAction.None;

    public bool IsValid => Disabled == 0;

    /// <summary>The identity of the FILE this row names — the unique key, minus the game.</summary>
    public (string FilePath, string RomPath) Key => (FilePath, RomPath);

    public void Touch()
    {
        if (Action == RommRowAction.None) Action = RommRowAction.Modify;
    }
}

internal enum RommRowAction { None, Add, Modify }

internal static class RommGamesTable
{
    // ── Schema ────────────────────────────────────────────────────────────────

    public const string Schema =
        "CREATE TABLE IF NOT EXISTS romm_games(" +
        "  romm_id      INTEGER PRIMARY KEY AUTOINCREMENT," +
        "  guid_lb      TEXT    NOT NULL," +
        "  platform_id  INTEGER NOT NULL," +
        "  emulated     INTEGER NOT NULL DEFAULT 1," +
        "  app_id       TEXT    NOT NULL DEFAULT ''," +
        "  filepath     TEXT    NOT NULL DEFAULT ''," +
        "  rompath      TEXT    NOT NULL DEFAULT ''," +
        "  is_extract   INTEGER NOT NULL DEFAULT 0," +
        "  clients      TEXT    NOT NULL DEFAULT ''," +
        "  disabled     INTEGER NOT NULL DEFAULT 0," +
        "  is_default   TEXT," +
        "  disabled_utc TEXT);" +
        "CREATE UNIQUE INDEX IF NOT EXISTS ix_rg_key      ON romm_games(guid_lb, filepath, rompath);" +
        "CREATE        INDEX IF NOT EXISTS ix_rg_platform ON romm_games(platform_id);" +
        "CREATE        INDEX IF NOT EXISTS ix_rg_guid     ON romm_games(guid_lb);" +

        // The paired clients, by a small persistent index of our own. A revoked client keeps its row so
        // its number is never handed to somebody else — the same reason rom ids are not reused.
        "CREATE TABLE IF NOT EXISTS client(" +
        "  client_id    INTEGER PRIMARY KEY AUTOINCREMENT," +
        "  token_id     INTEGER NOT NULL UNIQUE," +
        "  removed_utc  TEXT);";

    // ── The comma-bounded client list ─────────────────────────────────────────

    /// <summary>Parses ",3,7," into {3,7}. Tolerates the unbounded form an older row might carry.</summary>
    public static List<int> ParseClients(string? s)
    {
        var res = new List<int>();
        if (string.IsNullOrEmpty(s)) return res;
        foreach (var part in s!.Split(',', StringSplitOptions.RemoveEmptyEntries))
            if (int.TryParse(part.Trim(), out var v) && v > 0 && !res.Contains(v)) res.Add(v);
        return res;
    }

    /// <summary>Formats {3,7} as ",3,7," — bounded, so a LIKE on ",7," cannot match 17. Empty stays
    /// empty rather than becoming "," , so "no clients" reads as falsy everywhere.</summary>
    public static string FormatClients(IEnumerable<int> ids)
    {
        var sb = new StringBuilder();
        bool any = false;
        foreach (var id in ids)
        {
            if (id <= 0) continue;
            if (!any) { sb.Append(','); any = true; }
            sb.Append(id).Append(',');
        }
        return any ? sb.ToString() : "";
    }

    // ── Reading ───────────────────────────────────────────────────────────────

    private static RommGameRow Read(SqliteDataReader r) => new()
    {
        RomId = r.GetInt64(0),
        GuidLb = r.GetString(1),
        PlatformId = r.GetInt32(2),
        Emulated = r.GetInt32(3) != 0,
        AppId = r.GetString(4),
        FilePath = r.GetString(5),
        RomPath = r.GetString(6),
        IsExtract = r.GetInt32(7) != 0,
        Clients = ParseClients(r.GetString(8)),
        Disabled = r.GetInt64(9),
        IsDefaultUtc = r.IsDBNull(10) ? null : ParseUtc(r.GetString(10)),
        DisabledUtc = r.IsDBNull(11) ? null : ParseUtc(r.GetString(11)),
    };

    private const string Columns =
        "romm_id, guid_lb, platform_id, emulated, app_id, filepath, rompath, is_extract, " +
        "clients, disabled, is_default, disabled_utc";

    private static DateTime? ParseUtc(string s)
        => DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d : null;

    /// <summary>Every row of one platform, grouped by game. ONE query for the whole platform — the pass
    /// is per platform precisely so this stays one query and not one per game.</summary>
    public static Dictionary<string, List<RommGameRow>> ByPlatform(SqliteConnection conn, int platformId)
    {
        var res = new Dictionary<string, List<RommGameRow>>(StringComparer.OrdinalIgnoreCase);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM romm_games WHERE platform_id=$p";
        cmd.Parameters.AddWithValue("$p", platformId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var row = Read(r);
            if (!res.TryGetValue(row.GuidLb, out var list)) res[row.GuidLb] = list = new List<RommGameRow>();
            list.Add(row);
        }
        return res;
    }

    /// <summary>Every row of one game — the shape a trigger needs, when only one game changed.</summary>
    public static List<RommGameRow> ByGame(SqliteConnection conn, string guidLb)
    {
        var res = new List<RommGameRow>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM romm_games WHERE guid_lb=$g";
        cmd.Parameters.AddWithValue("$g", guidLb);
        using var r = cmd.ExecuteReader();
        while (r.Read()) res.Add(Read(r));
        return res;
    }

    /// <summary>The rows behind a page of rom ids — ONE query for the page, which is what keeps the file
    /// name off the hot path and out of memory. Holding a name per game would cost tens of megabytes on
    /// a large library; a query per page costs nothing and resides nowhere.</summary>
    public static Dictionary<long, RommGameRow> ByIds(SqliteConnection conn, IReadOnlyCollection<long> ids)
    {
        var res = new Dictionary<long, RommGameRow>();
        if (ids.Count == 0) return res;
        var sb = new StringBuilder($"SELECT {Columns} FROM romm_games WHERE romm_id IN (");
        bool first = true;
        foreach (var id in ids) { if (!first) sb.Append(','); sb.Append(id); first = false; }
        sb.Append(')');
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sb.ToString();
        using var r = cmd.ExecuteReader();
        while (r.Read()) { var row = Read(r); res[row.RomId] = row; }
        return res;
    }

    /// <summary>The generation this pass will stamp on whatever it invalidates.</summary>
    public static long NextGeneration(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(disabled), 0) + 1 FROM romm_games";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    // ── Writing ───────────────────────────────────────────────────────────────

    /// <summary>Flushes what a pass decided. One transaction for the batch: a platform's worth of games
    /// is one commit, not one per row.</summary>
    public static void Flush(SqliteConnection conn, IEnumerable<RommGameRow> rows)
    {
        using var tx = conn.BeginTransaction();
        foreach (var row in rows)
        {
            if (row.Action == RommRowAction.None) continue;
            using var cmd = conn.CreateCommand();
            if (row.Action == RommRowAction.Add)
            {
                cmd.CommandText =
                    "INSERT INTO romm_games(guid_lb, platform_id, emulated, app_id, filepath, rompath, " +
                    "                       is_extract, clients, disabled, is_default, disabled_utc) " +
                    "VALUES($g,$p,$e,$a,$f,$r,$x,$c,$d,$i,$du); SELECT last_insert_rowid();";
                Bind(cmd, row);
                row.RomId = Convert.ToInt64(cmd.ExecuteScalar());
            }
            else
            {
                cmd.CommandText =
                    "UPDATE romm_games SET platform_id=$p, emulated=$e, app_id=$a, filepath=$f, " +
                    "  rompath=$r, is_extract=$x, clients=$c, disabled=$d, is_default=$i, disabled_utc=$du " +
                    "WHERE romm_id=$id";
                Bind(cmd, row);
                cmd.Parameters.AddWithValue("$id", row.RomId);
                cmd.ExecuteNonQuery();
            }
            row.Action = RommRowAction.None;
        }
        tx.Commit();
    }

    private static void Bind(SqliteCommand cmd, RommGameRow row)
    {
        cmd.Parameters.AddWithValue("$g", row.GuidLb);
        cmd.Parameters.AddWithValue("$p", row.PlatformId);
        cmd.Parameters.AddWithValue("$e", row.Emulated ? 1 : 0);
        cmd.Parameters.AddWithValue("$a", row.AppId ?? "");
        cmd.Parameters.AddWithValue("$f", row.FilePath ?? "");
        cmd.Parameters.AddWithValue("$r", row.RomPath ?? "");
        cmd.Parameters.AddWithValue("$x", row.IsExtract ? 1 : 0);
        cmd.Parameters.AddWithValue("$c", FormatClients(row.Clients));
        cmd.Parameters.AddWithValue("$d", row.Disabled);
        cmd.Parameters.AddWithValue("$i", (object?)row.IsDefaultUtc?.ToString("o") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$du", (object?)row.DisabledUtc?.ToString("o") ?? DBNull.Value);
    }

    // ── Clients ───────────────────────────────────────────────────────────────

    /// <summary>Our own client index for a paired token, allocated on first sight and never reused.</summary>
    public static int ClientIdFor(SqliteConnection conn, int tokenId)
    {
        using (var sel = conn.CreateCommand())
        {
            sel.CommandText = "SELECT client_id FROM client WHERE token_id=$t";
            sel.Parameters.AddWithValue("$t", tokenId);
            var got = sel.ExecuteScalar();
            if (got != null && got != DBNull.Value) return Convert.ToInt32(got);
        }
        using (var ins = conn.CreateCommand())
        {
            ins.CommandText = "INSERT INTO client(token_id) VALUES($t); SELECT last_insert_rowid();";
            ins.Parameters.AddWithValue("$t", tokenId);
            return Convert.ToInt32(ins.ExecuteScalar());
        }
    }

    /// <summary>The client indices still in service, and the token each one stands for.</summary>
    public static Dictionary<int, int> LiveClients(SqliteConnection conn)
    {
        var res = new Dictionary<int, int>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT client_id, token_id FROM client WHERE removed_utc IS NULL";
        using var r = cmd.ExecuteReader();
        while (r.Read()) res[r.GetInt32(0)] = r.GetInt32(1);
        return res;
    }

    /// <summary>Retires a client: its row stays so its index is never handed out again, and it is struck
    /// from every list. Nobody else changes file because one client left, so no pass is needed.</summary>
    public static void RetireClient(SqliteConnection conn, int clientId)
    {
        using var tx = conn.BeginTransaction();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "UPDATE client SET removed_utc=$t WHERE client_id=$c AND removed_utc IS NULL";
            cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("$c", clientId);
            cmd.ExecuteNonQuery();
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "UPDATE romm_games SET clients = REPLACE(clients, $needle, ',') " +
                              "WHERE clients LIKE $like";
            cmd.Parameters.AddWithValue("$needle", "," + clientId + ",");
            cmd.Parameters.AddWithValue("$like", "%," + clientId + ",%");
            cmd.ExecuteNonQuery();
        }
        // ",3," minus ",3," leaves "," — an empty list must read as empty, not as a stray separator.
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "UPDATE romm_games SET clients='' WHERE clients=','";
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }
}
