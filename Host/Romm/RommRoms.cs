// La moitié résidente du modèle d'identité.
//
// Two structures, and the asymmetry is the design:
//
//   • EVERY game carries one integer, GameRow.RommRomId (Tier 1) — the rom_id of its DEFAULT row. On its
//     own it means "this is what everybody gets". A game nobody has moved costs four bytes and nothing
//     else: no dictionary entry, no object, no string. The absence of information IS the information.
//
//   • The FEW games where somebody sits on another row carry a small list, grouped by row rather than by
//     client, because several clients usually land on the same file.
//
// Resolution never consults both:
//
//     rom_id  =  la ligne où ce client figure   s'il y en a une
//                GameRow.RommRomId              sinon
//
// Tier 1 for the same reason as BadgeCombo: the server answers while a game is RUNNING — a handheld
// syncing mid-session — which is exactly when the higher tiers are dropped.
//
// Nothing here is the truth. romm.db is; this is rebuilt from it, and every write goes through
// RommIndexer so there is one writer and one order of events.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using LbApiHost.Host.Data;
using LbApiHost.Host.Diag;
using Microsoft.Data.Sqlite;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Romm;

/// <summary>One row somebody sits on that is not the game's default.</summary>
internal sealed class RommPinnedRow
{
    public long RomId;
    public string AppId = "";
    public string FilePath = "";
    public string RomPath = "";
    public List<int> ClientIds = new();
}

internal static class RommRoms
{
    private static readonly object _gate = new();

    /// <summary>guid → the non-default rows people sit on. Sparse: absent means "everybody on default".</summary>
    private static Dictionary<string, List<RommPinnedRow>> _pinned = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>token id → our client index. Small; one entry per paired client.</summary>
    private static Dictionary<int, int> _clientOf = new();

    /// <summary>guid → default rom id. The fallback for a game the game store could not be stamped for.</summary>
    private static Dictionary<string, long> _defaults = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Games whose default moved OUTSIDE a full pass. Memory only, empty at boot by
    /// construction — which is why it needs no column and no migration.</summary>
    private static readonly HashSet<string> _dirtyDefault = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>False until the first full pass has finished. The server does not listen before that.</summary>
    public static bool Ready { get; private set; }

    // ── Chargement ────────────────────────────────────────────────────────────

    /// <summary>Rebuilds the resident state from the table. Called at the end of a full pass.</summary>
    internal static void Reload(SqliteConnection conn)
    {
        var pinned = new Dictionary<string, List<RommPinnedRow>>(StringComparer.OrdinalIgnoreCase);
        var defaults = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT guid_lb, romm_id, app_id, filepath, rompath, clients, is_default " +
                "FROM romm_games WHERE disabled = 0";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var guid = r.GetString(0);
                long id = r.GetInt64(1);
                bool isDefault = !r.IsDBNull(6);
                var clients = RommGamesTable.ParseClients(r.GetString(5));

                if (isDefault) defaults[guid] = id;

                // Only NON-default rows with somebody on them need to be resident: a client on the
                // default is answered by the game row's integer.
                if (isDefault || clients.Count == 0) continue;
                if (!pinned.TryGetValue(guid, out var list)) pinned[guid] = list = new List<RommPinnedRow>();
                list.Add(new RommPinnedRow
                {
                    RomId = id, AppId = r.GetString(2), FilePath = r.GetString(3),
                    RomPath = r.GetString(4), ClientIds = clients,
                });
            }
        }
        catch (Exception ex) { LbLog.Warn("romm", "index: reload failed: " + ex.Message); return; }

        var clientOf = new Dictionary<int, int>();
        try { foreach (var kv in RommGamesTable.LiveClients(conn)) clientOf[kv.Value] = kv.Key; }
        catch { }

        lock (_gate)
        {
            _pinned = pinned;
            _defaults = defaults;
            _clientOf = clientOf;
            _dirtyDefault.Clear();          // a full pass has just settled every default authoritatively
            Ready = true;
        }
        Stamp(defaults);
        LbLog.Info("romm", $"index: {defaults.Count} default(s) resident, {pinned.Count} game(s) with a pin");
    }

    /// <summary>Refreshes one game after a trigger, without rebuilding the world.</summary>
    internal static void ReloadGame(SqliteConnection conn, string guid)
    {
        try
        {
            var rows = RommGamesTable.ByGame(conn, guid).Where(r => r.IsValid).ToList();
            var list = new List<RommPinnedRow>();
            long? def = null;
            foreach (var row in rows)
            {
                if (row.IsDefaultUtc != null) { def = row.RomId; continue; }
                if (row.Clients.Count == 0) continue;
                list.Add(new RommPinnedRow
                {
                    RomId = row.RomId, AppId = row.AppId, FilePath = row.FilePath,
                    RomPath = row.RomPath, ClientIds = row.Clients.ToList(),
                });
            }

            lock (_gate)
            {
                if (list.Count > 0) _pinned[guid] = list; else _pinned.Remove(guid);
                if (def is long d) _defaults[guid] = d; else _defaults.Remove(guid);
                _dirtyDefault.Add(guid);     // settled outside a full pass: a new client re-runs it
            }
            if (def is long id2) StampOne(guid, id2);
        }
        catch (Exception ex) { LbLog.Warn("romm", "index: reload of one game failed: " + ex.Message); }
    }

    private static void Stamp(Dictionary<string, long> defaults)
    {
        try
        {
            int n = 0;
            foreach (var kv in defaults)
            {
                var g = SafeGame(kv.Key);
                if (g is HostGame hg) { hg.RommRomId = (int)kv.Value; n++; }
            }
            LbLog.Info("romm", $"index: {n} rom id(s) stamped on the game rows");
        }
        catch (Exception ex) { LbLog.Warn("romm", "index: stamping failed: " + ex.Message); }
    }

    private static void StampOne(string guid, long id)
    {
        if (SafeGame(guid) is HostGame hg) hg.RommRomId = (int)id;
    }

    private static IGame? SafeGame(string guid)
    {
        try { return Unbroken.LaunchBox.Plugins.PluginHelper.DataManager?.GetGameById(guid); }
        catch { return null; }
    }

    // ── Résolution ────────────────────────────────────────────────────────────

    /// <summary>The rom id this client is served for this game. Memory only — this runs for every row of
    /// every listing.</summary>
    public static long RomIdFor(IGame game, int? tokenId)
    {
        var guid = RommLibrary.IdOf(game);
        if (guid.Length == 0) return 0;

        lock (_gate)
        {
            if (tokenId is int tid && _clientOf.TryGetValue(tid, out var cid)
                && _pinned.TryGetValue(guid, out var rows))
                foreach (var row in rows)
                    if (row.ClientIds.Contains(cid)) return row.RomId;
        }
        return DefaultRomId(game, guid);
    }

    /// <summary>The game's default rom id — the integer on its row, else the map.</summary>
    public static long DefaultRomId(IGame game, string? guid = null)
    {
        if (game is HostGame hg && hg.RommRomId > 0) return hg.RommRomId;
        guid ??= RommLibrary.IdOf(game);
        lock (_gate) return _defaults.TryGetValue(guid, out var id) ? id : 0;
    }

    /// <summary>Is this game advertisable to this client? A game the index never settled has no default
    /// row, so it answers 0 — and nothing answering 0 is offered. That covers, with one rule, the archive
    /// we cannot name and the game with no playable file at all.</summary>
    public static bool Advertisable(IGame game, int? tokenId) => RomIdFor(game, tokenId) > 0;

    /// <summary>The pinned row a client sits on, or null when it follows the default.</summary>
    public static RommPinnedRow? PinnedFor(IGame game, int? tokenId)
    {
        if (tokenId is not int tid) return null;
        var guid = RommLibrary.IdOf(game);
        lock (_gate)
        {
            if (!_clientOf.TryGetValue(tid, out var cid)) return null;
            if (!_pinned.TryGetValue(guid, out var rows)) return null;
            return rows.FirstOrDefault(r => r.ClientIds.Contains(cid));
        }
    }

    /// <summary>Every non-default row somebody sits on, for the assignment screen.</summary>
    public static IReadOnlyList<RommPinnedRow> PinsOf(string guid)
    {
        lock (_gate)
            return _pinned.TryGetValue(guid, out var rows)
                ? rows.Select(r => new RommPinnedRow
                  { RomId = r.RomId, AppId = r.AppId, FilePath = r.FilePath, RomPath = r.RomPath,
                    ClientIds = r.ClientIds.ToList() }).ToList()
                : (IReadOnlyList<RommPinnedRow>)Array.Empty<RommPinnedRow>();
    }

    /// <summary>How many games this client sits on somewhere other than the default.</summary>
    public static int PinCountFor(int tokenId)
    {
        lock (_gate)
        {
            if (!_clientOf.TryGetValue(tokenId, out var cid)) return 0;
            return _pinned.Values.Count(rows => rows.Any(r => r.ClientIds.Contains(cid)));
        }
    }

    /// <summary>Our client index for a paired token, or 0.</summary>
    public static int ClientIndexOf(int tokenId)
    {
        lock (_gate) return _clientOf.TryGetValue(tokenId, out var cid) ? cid : 0;
    }

    /// <summary>How this client's pushes land: 1 replaces the save in play, 2 keeps to its own line.
    /// One query, and only when the Clients grid is drawn — never on a request path.
    ///
    /// 2 is the default, here and in the schema: a client that syncs on its own schedule should not be
    /// able to overwrite the save someone is playing before anyone has said it may.</summary>
    // Le mode de push n'existe plus : un push atterrit toujours dans la branche du client, et la save
    // en jeu n'est touchee que via une branche PROMUE dans Game Saves. La colonne push_mode reste dans
    // le schema (une colonne se retire mal), plus rien ne la lit.

    /// <summary>Games whose default moved outside a full pass — a new client cannot take the fast path
    /// for these and must go through the procedure.</summary>
    public static IReadOnlyCollection<string> DirtyDefaults()
    {
        lock (_gate) return _dirtyDefault.ToList();
    }
}
