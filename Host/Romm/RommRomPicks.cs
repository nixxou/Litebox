// Which ROM of an archive a given client is bound to, per game.
//
// The problem this exists for: a game whose ROM is an archive holding several versions has ONE rom_id,
// therefore one pool of saves. The clients cannot tell those versions apart — Freegosy re-sorts the save
// list by date and takes the newest, and its SaveFile model does not even parse the file name — so a
// device that played the Japanese ROM pulls the USA save on top of it. No server-side ordering fixes
// that, because the client discards ours.
//
// So the server decides instead. A client that downloads one entry of an archive is BOUND to it: from
// then on that game advertises only that entry, and only that entry's saves. Before the binding exists
// the entries are all advertised (the client's own picker is what creates the binding) but NO save is —
// nothing to sync is a great deal better than the wrong thing to sync.
//
// Keyed on the client TOKEN, not on the device_id a client sends in a query string: the token is the
// credential the request actually authenticated with, and RommIdentity.TokenId is populated only for
// "Bearer rmm_…" — the pairing path. A caller using the account password has no binding and stays on the
// safe default for ever, which is a deliberate limit and is stated in the options panel.
//
// One JSON value in the options DB, exactly like RommDevices: a client binds only the games it actually
// downloads, so this is tens of rows, not thousands.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using LbApiHost.Host.Data;
using LbApiHost.Host.Diag;

namespace LbApiHost.Host.Romm;

/// <summary>One client's chosen ROM inside one game's archive.</summary>
internal sealed class RommRomPick
{
    public int TokenId { get; set; }

    /// <summary>The LaunchBox game id — the archive's game, not the entry.</summary>
    public string GameId { get; set; } = "";

    /// <summary>Path inside the archive. THE identity: two entries can share a file name in different
    /// folders, so the name alone would bind to the wrong one.</summary>
    public string PathInArchive { get; set; } = "";

    /// <summary>The entry's file name, for display only — the options panel shows it, nothing matches
    /// on it.</summary>
    public string EntryFileName { get; set; } = "";

    public DateTime BoundUtc { get; set; }
}

internal static class RommRomPicks
{
    private const string PicksKey = "Romm.RomPicks";
    private static readonly object _lock = new();

    public static List<RommRomPick> All()
    {
        try
        {
            var raw = LiteBoxOptionsDb.GetGlobal(PicksKey);
            if (string.IsNullOrEmpty(raw)) return new List<RommRomPick>();
            return JsonSerializer.Deserialize<List<RommRomPick>>(raw!) ?? new List<RommRomPick>();
        }
        catch { return new List<RommRomPick>(); }
    }

    private static void SaveAll(List<RommRomPick> picks)
    {
        try { LiteBoxOptionsDb.SetGlobal(PicksKey, JsonSerializer.Serialize(picks)); }
        catch (Exception ex) { LbLog.Warn("romm", "rom-pick store failed: " + ex.Message); }
    }

    /// <summary>The entry this client is bound to for this game, or null when it is free to choose.</summary>
    public static RommRomPick? For(int? tokenId, string gameId)
    {
        if (tokenId == null || string.IsNullOrEmpty(gameId)) return null;
        return All().FirstOrDefault(p => p.TokenId == tokenId.Value
                                      && string.Equals(p.GameId, gameId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Binds, or re-points an existing binding.
    ///
    /// Re-pointing rather than refusing is deliberate: "I want the Japanese one now" is a legitimate
    /// request, and since saves are partitioned by entry, switching destroys nothing — the previous
    /// entry's copies stay exactly where they are and come back if the client switches back.</summary>
    public static void Set(int tokenId, string gameId, string pathInArchive, string entryFileName)
    {
        if (string.IsNullOrEmpty(gameId) || string.IsNullOrEmpty(pathInArchive)) return;
        lock (_lock)
        {
            var all = All();
            all.RemoveAll(p => p.TokenId == tokenId
                            && string.Equals(p.GameId, gameId, StringComparison.OrdinalIgnoreCase));
            all.Add(new RommRomPick
            {
                TokenId = tokenId,
                GameId = gameId,
                PathInArchive = pathInArchive,
                EntryFileName = entryFileName,
                BoundUtc = DateTime.UtcNow,
            });
            SaveAll(all);
        }
    }

    /// <summary>Frees one client on one game — it sees every entry again, and no saves until it rebinds.</summary>
    public static bool Clear(int tokenId, string gameId)
    {
        lock (_lock)
        {
            var all = All();
            int n = all.RemoveAll(p => p.TokenId == tokenId
                                    && string.Equals(p.GameId, gameId, StringComparison.OrdinalIgnoreCase));
            if (n > 0) SaveAll(all);
            return n > 0;
        }
    }

    /// <summary>Drops every binding of one client — what "revoke and forget its ROM choices" does.</summary>
    public static int ClearToken(int tokenId)
    {
        lock (_lock)
        {
            var all = All();
            int n = all.RemoveAll(p => p.TokenId == tokenId);
            if (n > 0) SaveAll(all);
            return n;
        }
    }

    public static List<RommRomPick> OfToken(int tokenId)
        => All().Where(p => p.TokenId == tokenId).ToList();

    public static int CountFor(int tokenId) => All().Count(p => p.TokenId == tokenId);
}
