// The integer ids RomM clients see, for everything that is not a rom.
//
// This used to BE the ledger — five string→int dictionaries in Core\litebox\romm-ids.json, all of them
// resident. It is now a thin face over romm.db, which keeps the same guarantees where they matter:
// allocated on first sight, monotonic, and never reused, because clients persist these integers and a
// re-added game must not inherit somebody else's history.
//
// Roms are NOT here any more. A rom_id names a game AND a file, and which file a given client is served
// depends on whether it is locked — see RommRoms. Keeping a second way to resolve one would be a second
// thing to keep true.

#nullable enable

namespace LbApiHost.Host.Romm;

internal static class RommIdMap
{
    /// <summary>Platform id for a LaunchBox platform NAME (the platform's identity in LB).</summary>
    public static int PlatformId(string lbPlatformName) => RommDb.PlatformId(lbPlatformName ?? "");

    /// <summary>File id for one playable entry of a game: the main ROM ("main"), an additional-app disc
    /// ("app:{appId}") or an archive member ("entry:{path}"). Scoped by the game GUID.</summary>
    public static int FileId(string gameId, string entryKey) => RommDb.FileId(gameId ?? "", entryKey ?? "");

    /// <summary>Asset id for a save / state / screenshot, keyed by its vault identity.</summary>
    public static int AssetId(string vaultKey) => RommDb.AssetId(vaultKey ?? "");

    /// <summary>Collection id for a LaunchBox playlist name (or the synthetic "Favorites").</summary>
    public static int CollectionId(string name) => RommDb.CollectionId(name ?? "");

    // ── Reverse lookups (a client hands the int back) ─────────────────────────

    public static string? PlatformNameOf(int id) => RommDb.PlatformNameOf(id);
    public static string? AssetKeyOf(int assetId) => RommDb.AssetKeyOf(assetId);
    public static string? CollectionNameOf(int id) => RommDb.CollectionNameOf(id);

    /// <summary>The "{gameId}|{entryKey}" a file id was minted for.</summary>
    public static string? FileKeyOf(int fileId) => RommDb.FileKeyOf(fileId);
}
