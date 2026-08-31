// Saves / states / screenshots over SaveManager, plus the device-sync verbs — the asset story.
//
// The mapping (the plan's §5.6, honoured literally):
//   • a LIVE save group (SaveManager.ScanBase → plugin scan)  = the asset a client downloads/uploads
//   • every VAULT backup                                       = an older version, listed alongside
//   • upload = SaveManager.Import (which lands a VERSION in the vault and records it, and never
//     writes a live save) THEN SaveManager.Restore, the one path that does write one — through the
//     emulator's own integration plugin, exactly as Edit Game → Game Saves does.
//   • no integration plugin for the game's emulator → the upload stops after the version and the
//     live save is never touched (format honesty: silently copying a client's bytes over a real save
//     is the one outcome this feature must never produce).
//   • bulk delete deletes VAULT entries only; the live save is never deleted through this API.
//   • before a sync overwrites a live save, what is there is copied into the vault and LABELLED with
//     the client that caused it. Restore takes a copy of its own, but silently and unnamed; doing it
//     first means the history says where the overwrite came from, which is the difference between a
//     recoverable accident and an unexplained one.
//   • every save group answers on its own RomM `slot` — the NAMED channel the ecosystem's clients put
//     in front of the user (Grout shows a picker the first time a ROM offers several; the default one
//     is spelled "autosave" across the ecosystem). One channel per GROUP, never per copy: a group is a
//     line the emulator keeps writing to, a vault copy is frozen history that retention evicts, and a
//     slot pointing at one would vanish under a client that had pinned it.
//
//     The channels, per REQUESTING CLIENT (docs/romm-server-plan.md §5.6ter):
//       autosave    the requester's own branch — the only place its pushes ever land. When it has no
//                   branch yet for a ROM, the game's primary LiteBox line stands in under this name
//                   (real asset id, real file name), so a fresh client pulls something by default and
//                   its pull→play→push round trip never leaves its own channel.
//       romm-cN     another client's branch, a read-only extra.
//       lb-…        a LiteBox group: "lb-ra-<core>" under RetroArch, "lb-<emulator>" elsewhere. Groups
//                   with nothing in play are served too (their line lives in the vault); when several
//                   groups answer to one name, the one in play wins, else the most recently written.
//     A client that cannot read slots at all (Freegosy takes the newest of whatever it is shown, and
//     writes the served file name to disk VERBATIM) is detected by its User-Agent and given only the
//     autosave line — for it, the multi-line view would not read as a choice but as the truth.
//   • the `file_name` an asset is ADVERTISED under follows its slot — Argosy seeds its slot table from
//     the file name alone (parseServerChannelNameForSync never reads `slot`) — EXCEPT on the autosave
//     channel, which carries the file's real name: a name equal to the ROM's base name is precisely
//     what says "the latest save" to Argosy, and it is the name Freegosy writes to disk, where only
//     the real one lets the emulator find the save. Bytes and paths on disk are untouched either way.
//
// Asset identity in the ledger: "live|{gameId}|{groupId}|{s|f}" for a live group,
// "vault|{gameId}|{vaultPath}" for a backup, "screen|{relPath}" for a screenshot. The game id is
// part of a vault key because it has to be: a copy is described by a record, records belong to a
// game, and there is no library-wide index to look a bare path up in. Devices mark what they hold (RommDevices) and
// is_current falls out of comparing timestamps, which is all Grout needs to decide push vs pull.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using LbApiHost.Host.Diag;
using LbApiHost.Host.Saves;
using LbApiHost.Host.Web;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Romm;

/// <summary>One asset as the API sees it, wherever it lives.</summary>
internal sealed class RommAssetView
{
    public int Id;
    public int RomId;
    public string GameId = "";
    public string FileName = "";
    public string AbsPath = "";
    public bool IsDirectory;
    public bool IsState;
    public bool IsLive;
    public long Size;
    public DateTime UpdatedUtc;
    public DateTime CreatedUtc;
    public string? Emulator;
    /// <summary>LaunchBox's savestate NUMBER, or null for a save file. Internal: it is what Import and
    /// Restore need, and it is NOT what RomM calls a slot.</summary>
    public int? Slot;

    /// <summary>RomM's slot: the free-form channel name a client pushed to. This is what the DTO
    /// reports and what /api/saves?slot= filters on.</summary>
    public string? SlotRomm;

    /// <summary>Serve this asset under its REAL file name rather than its slot's. True on the autosave
    /// channel: the real name is what tells Argosy "this is the latest save", and what a name-blind
    /// client (Freegosy) can safely write to disk.</summary>
    public bool ServeRealName;

    public string? Md5;
    public bool Missing;
}

internal static class RommAssetsApi
{
    // ── Listing ───────────────────────────────────────────────────────────────

    /// <summary>Every save OR state of one ROM: the group in play, plus this client's own branch.
    ///
    /// Two timelines, deliberately. The ACTIVE group is what the desktop plays; the BRANCH is where a
    /// client in mode 2 lands its pushes. A client sees both in either mode — the mode decides only
    /// where a push goes, never what is visible, so a client's own progress is always reachable.
    ///
    /// Another client's branch is never shown: it is somebody else's timeline for the same ROM, and
    /// offering it would put a choice with no meaning in front of a client that sorts by date.
    ///
    /// Drives the plugin scan, so it is per-game by design — a whole-library scan would hammer every
    /// emulator integration at once.</summary>
    /// <param name="rom">The row the rom_id names. Null lists every group of the game, which is what the
    /// bare vault view wants; non-null narrows to that ROM.</param>
    /// <param name="slotBlind">The requester takes the newest of whatever it is shown (Freegosy): serve
    /// only the autosave line, because extra lines would not read as choices but as the truth.</param>
    public static List<RommAssetView> ListForGame(IGame game, bool states, int? tokenId = null,
                                                  RommGameRow? rom = null, bool slotBlind = false)
    {
        var result = new List<RommAssetView>();
        var gameId = RommLibrary.IdOf(game);
        int romId = rom != null ? (int)rom.RomId : (int)RommRoms.DefaultRomId(game, gameId);

        // "#c3" — this client's branch on any ROM. Deterministic, so it survives restarts.
        int clientIdx = tokenId is int tid ? RommRoms.ClientIndexOf(tid) : 0;
        string? mine = clientIdx > 0 ? "c" + clientIdx : null;

        SaveScan scan;
        try { scan = SaveManager.ScanBase(game); }
        catch (Exception ex) { LbLog.Warn("romm", "save scan failed: " + ex.Message); return result; }
        if (scan.Error != null) return result;

        // Which groups this client sees, and under which channel — settled BEFORE anything is emitted,
        // because the choices depend on each other: the client's own branch is its autosave, and only
        // when it has none does the primary LiteBox line stand in under that name.
        var candidates = new List<SaveGroup>();
        foreach (var g in states ? scan.States : scan.Files)
        {
            if (rom != null && !BelongsToRom(g, rom)) continue;
            // Nothing recoverable behind these: RecordOnly's file is gone, DuplicateRecord is a fossil
            // another group already owns.
            if (g.RecordOnly || g.DuplicateRecord) continue;
            candidates.Add(g);
        }

        // (group, channel, served under its real file name). The real name travels with the autosave
        // channel and only with it.
        var chosen = new List<(SaveGroup g, string slot, bool real)>();

        // The client's own branch — its autosave.
        var own = new List<SaveGroup>();
        if (mine != null)
            own.AddRange(candidates.Where(g =>
                string.Equals(BranchOf(g.GroupId), mine, StringComparison.OrdinalIgnoreCase)));
        foreach (var g in own) chosen.Add((g, StateSuffixed(DefaultSlot, g), true));

        // LiteBox's groups, one winner per channel name: LaunchBox's "Make New Save" builds a second
        // In-Vault group on the same core, and a client sorting a channel by date must not be handed
        // both. The one IN PLAY owns the name, else the most recently written; the others are set
        // aside and the trace says so — losing a genuinely-kept second line silently would be worse.
        var lbGroups = candidates
            .Where(g => BranchOf(g.GroupId) == null && (g.Active != null || g.InVault)).ToList();
        var primary = PickPrimary(lbGroups);
        foreach (var lot in lbGroups.GroupBy(LbSlot, StringComparer.OrdinalIgnoreCase))
        {
            var members = lot.ToList();
            var winner = PickPrimary(members)!;
            foreach (var loser in members)
                if (!ReferenceEquals(loser, winner))
                    RommTrace.Note($"slot {lot.Key}: group \"{loser.GroupName}\" set aside (another group owns the name)");
            // The seed: with no branch of its own, the client's autosave IS the primary line — real
            // id, real file name — so its first pull, and the push that follows, stay on the default
            // channel for the whole round trip.
            bool seeds = own.Count == 0 && ReferenceEquals(winner, primary);
            chosen.Add((winner, StateSuffixed(seeds ? DefaultSlot : lot.Key, winner), seeds));
        }

        // Other clients' branches: read-only extras, by name.
        foreach (var g in candidates)
        {
            var br = BranchOf(g.GroupId);
            if (br != null && !string.Equals(br, mine, StringComparison.OrdinalIgnoreCase))
                chosen.Add((g, StateSuffixed("romm-" + br.ToLowerInvariant(), g), false));
        }

        // A slot-blind client gets the autosave line and nothing else.
        if (slotBlind) chosen.RemoveAll(t => !t.real);

        foreach (var (g, romSlot, slotIsRom) in chosen)
        {

            // Hoisted: the copies below are compared against it.
            string liveMd5 = "";
            if (g.Active != null && g.ActivePath.Length > 0)
            {
                var mtime = g.LastModified?.ToUniversalTime() ?? DateTime.UtcNow;
                string md5 = "";
                try { md5 = g.ActiveIsDirectory ? SaveManager.DirManifestMd5(g.ActivePath) : SaveManager.FileMd5(g.ActivePath); }
                catch { }
                liveMd5 = md5;
                result.Add(new RommAssetView
                {
                    Id = RommIdMap.AssetId($"live|{gameId}|{g.GroupId}|{(states ? "s" : "f")}"),
                    RomId = romId,
                    GameId = gameId,
                    FileName = Path.GetFileName(g.ActivePath.TrimEnd('\\', '/')),
                    AbsPath = g.ActivePath,
                    IsDirectory = g.ActiveIsDirectory,
                    IsState = states,
                    IsLive = true,
                    Size = g.SizeBytes ?? 0,
                    UpdatedUtc = mtime,
                    CreatedUtc = mtime,
                    Emulator = EmulatorLabel(g),
                    Slot = g.Slot,
                    SlotRomm = romSlot,
                    ServeRealName = slotIsRom,
                    Md5 = md5,
                    Missing = !(g.ActiveIsDirectory ? Directory.Exists(g.ActivePath) : File.Exists(g.ActivePath)),
                });
            }

            // Un groupe sans save en jeu detient quand meme un fichier : celui que son enregistrement
            // designe. FromRecords l'ecarte des copies — « the group's own file » — ce qui est juste
            // pour un groupe vivant, ou ce fichier est celui de l'emulateur et n'est pas une copie.
            // Ici c'en est une comme les autres, et la SEULE dont personne ne serait jamais informe.
            if (g.Active == null && g.ActivePath.Length > 0 && SaveVault.IsUnderVault(g.ActivePath))
            {
                var ownFile = FindVaultEntry(gameId, SaveVault.Rel(g.ActivePath));
                if (ownFile != null && ownFile.IsState == states)
                {
                    // Under the channel this listing chose for the group — ViewOf alone only knows
                    // the family, and two names for one line would split it across two slots.
                    var v = ViewOf(ownFile);
                    v.SlotRomm = romSlot; v.ServeRealName = slotIsRom;
                    result.Add(v);
                }
            }

            foreach (var b in g.Backups)
            {
                if (b.IsState != states) continue;
                var abs = SaveVault.Abs(b);

                // A copy identical to the live save is not a version of it, it IS it — offering both
                // gives a client two entries for one state of the game and a choice with no meaning.
                // FromRecords never fills Md5, so this is computed here; the file is small and the list
                // is one group's history.
                string bMd5 = "";
                try { bMd5 = b.IsDirectory ? SaveManager.DirManifestMd5(abs) : SaveManager.FileMd5(abs); }
                catch { }
                if (bMd5.Length > 0 && liveMd5.Length > 0
                    && string.Equals(bMd5, liveMd5, StringComparison.OrdinalIgnoreCase)) continue;

                result.Add(new RommAssetView
                {
                    Id = VaultAssetId(b),
                    RomId = romId,
                    GameId = gameId,
                    FileName = b.OriginalFileName is { Length: > 0 } ofn ? ofn : Path.GetFileName(abs),
                    AbsPath = abs,
                    IsDirectory = b.IsDirectory,
                    IsState = states,
                    IsLive = false,
                    Size = b.SizeBytes,
                    // The CONTENT's date, not the copy's. A client sorts by this and takes the newest,
                    // and it has no idea which save is the active one — so a copy dated when it was
                    // TAKEN could outrank the very save it was taken from, and the device would pull its
                    // own history back over the game in play. File.Copy preserves the modification time,
                    // so a copy carries the date of the save it captured and can never overtake it.
                    //
                    // It also answers the padlock, which shifts the CREATION date a century ahead: that
                    // date never reaches a client now, so a locked copy cannot pin itself as newest.
                    UpdatedUtc = ContentTimeUtc(abs, b.DisplayCreatedUtc),
                    CreatedUtc = ContentTimeUtc(abs, b.DisplayCreatedUtc),
                    Emulator = EmulatorLabel(g),
                    Slot = b.Slot,
                    SlotRomm = romSlot,          // the channel names the group, not the copy
                    ServeRealName = slotIsRom,
                    // Now that it is computed, report it: the field went out empty before.
                    Md5 = bMd5,
                    Missing = !(b.IsDirectory ? Directory.Exists(abs) : File.Exists(abs)),
                });
            }
        }
        return result;
    }

    /// <summary>When this file's CONTENT was written. Falls back to the supplied date when the file
    /// cannot be read — a listing must not fail because one copy is locked or gone.</summary>
    private static DateTime ContentTimeUtc(string abs, DateTime fallback)
    {
        try
        {
            if (Directory.Exists(abs))
                return Directory.EnumerateFiles(abs, "*", SearchOption.AllDirectories)
                    .Select(File.GetLastWriteTimeUtc).DefaultIfEmpty(fallback).Max();
            return File.Exists(abs) ? File.GetLastWriteTimeUtc(abs) : fallback;
        }
        catch { return fallback; }
    }

    /// <summary>Does this game's ROM hold several playable entries? Uses the same listing the rom DTO
    /// advertises from, so what we decide on and what the client was shown cannot disagree.</summary>
    /// <summary>Does this save group belong to the ROM the rom_id names?
    ///
    /// Three shapes, and the row already says which: an extracted ROM compares the PATH inside the
    /// archive — two entries can share a file name in different folders; a version compares the
    /// additional application; the game's own ROM is the main bucket, neither of the two.
    ///
    /// No name matching anywhere. That is the whole gain of the rom_id naming a file: the question
    /// "which ROM is this save for" is answered before the save layer is opened.</summary>
    private static bool BelongsToRom(SaveGroup g, RommGameRow rom)
    {
        var key = SaveManager.EntryKeyOf(g.GroupId);

        if (rom.RomPath.Length > 0)
        {
            if (key == null) return false;
            int sep = key.IndexOf(':', "entry:".Length);
            if (sep < 0) return false;
            return RommIndexPass.PathEq(key.Substring(sep + 1), rom.RomPath);
        }

        if (key != null) return false;                       // an entry group, but the rom names no entry
        var app = g.AppId ?? "";
        return string.Equals(app, rom.AppId ?? "", StringComparison.Ordinal);
    }

    /// <summary>The branch a group id carries, or null for the ROM's own line. "#c3" is client 3's.</summary>
    private static string? BranchOf(string? groupId)
    {
        if (string.IsNullOrEmpty(groupId)) return null;
        int i = groupId!.IndexOf('#');
        return i >= 0 && i + 1 < groupId.Length ? groupId.Substring(i + 1) : null;
    }

    /// <summary>The group id a client's own line takes for a ROM: the ROM's group, plus its branch.</summary>
    internal static string BranchGroupIdFor(string romGroupId, int clientIdx)
        => romGroupId + "#c" + clientIdx;

    /// <summary>The name RomM's default channel carries. Clients special-case it — a game with this one
    /// slot and no other is the ordinary case and gets no picker — so it has to be spelled their way.</summary>
    internal const string DefaultSlot = "autosave";

    /// <summary>A slot family suffixed with the savestate number: two state slots of one ROM are two
    /// different saves, and telling them apart is what a picker is for. Slot 0 — the plain ".state",
    /// the default one — stays unqualified.</summary>
    internal static string StateSuffixed(string name, SaveGroup g)
        => StateSuffixed(name, g.IsState, g.Slot);

    internal static string StateSuffixed(string name, bool isState, int? stateSlot)
    {
        if (isState && stateSlot is int n && n != 0)
            return name + (n < 0 ? "-auto" : "-state" + n);
        return name;
    }

    /// <summary>The channel of a LiteBox-owned group: "lb-ra-&lt;core&gt;" under RetroArch, "lb-&lt;core&gt;"
    /// for another cored emulator, "lb-&lt;emulator&gt;" for one with no cores. Only characters every
    /// client survives: no ':' (illegal in a Windows file name — the wire name follows the slot), no '.'
    /// (Argosy cuts channel names at the last one), no '#' (an unencoded ?slot= would truncate at the
    /// fragment), and '@' left out as a precaution against naive sanitisers.</summary>
    internal static string LbSlot(SaveGroup g)
    {
        var core = (g.EmulatorCore ?? "").Trim();
        string label;
        if (core.Length > 0)
        {
            if (core.EndsWith("_libretro", StringComparison.OrdinalIgnoreCase))
                core = core.Substring(0, core.Length - "_libretro".Length);
            var exe = Path.GetFileNameWithoutExtension(g.EmulatorFileName ?? "");
            bool retroarch = exe.IndexOf("retroarch", StringComparison.OrdinalIgnoreCase) >= 0;
            label = (retroarch ? "ra-" : "") + core;
        }
        else
        {
            string emu = "";
            try { emu = (g.Emulator?.Title ?? "").Trim(); } catch { }
            if (emu.Length == 0) emu = Path.GetFileNameWithoutExtension(g.EmulatorFileName ?? "");
            label = emu;
        }
        var safe = SlotSafe(label);
        return safe.Length == 0 ? "lb" : "lb-" + safe;
    }

    /// <summary>A label as a slot spells it: lowercase, spaces to dashes, and only characters that
    /// survive a file name, a query string and every client's channel parser.</summary>
    private static string SlotSafe(string label)
    {
        var sb = new System.Text.StringBuilder(label.Length);
        foreach (var c in label.Trim().ToLowerInvariant())
        {
            if (c >= 'a' && c <= 'z' || c >= '0' && c <= '9' || c == '-' || c == '_') sb.Append(c);
            else if (c == ' ' || c == '.') sb.Append('-');
        }
        return sb.ToString().Trim('-');
    }

    /// <summary>The winner of a channel name: the group in play, else the one most recently written.
    /// Deterministic, so the name stays on the same line between two listings.</summary>
    private static SaveGroup? PickPrimary(IReadOnlyList<SaveGroup> groups)
    {
        SaveGroup? best = null; var bestKey = (active: false, when: DateTime.MinValue);
        foreach (var g in groups)
        {
            var when = g.LastModified?.ToUniversalTime() ?? DateTime.MinValue;
            if (g.Active == null)
                foreach (var b in g.Backups)
                    if (b.DisplayCreatedUtc > when) when = b.DisplayCreatedUtc;
            var key = (active: g.Active != null, when);
            if (best == null || key.active && !bestKey.active
                || key.active == bestKey.active && key.when > bestKey.when)
            { best = g; bestKey = key; }
        }
        return best;
    }

    /// <summary>The channel of a bare vault row, where no scanned group gives context: the requester's
    /// own branch is its autosave, another's branch is named, and a LiteBox group is "lb" — with no
    /// emulator to read, the family is all this view can say.</summary>
    internal static string SlotOfGroupId(string? groupId, string? mine, bool isState, int? stateSlot)
    {
        var br = BranchOf(groupId);
        string name = br == null ? "lb"
            : string.Equals(br, mine, StringComparison.OrdinalIgnoreCase) ? DefaultSlot
            : "romm-" + br.ToLowerInvariant();
        return StateSuffixed(name, isState, stateSlot);
    }

    /// <summary>The name an asset is advertised and downloaded under: its channel, plus the real file's
    /// extension.
    ///
    /// Not cosmetic. Argosy seeds its slot table from the file name alone
    /// (SaveSyncApiClient.parseServerChannelNameForSync never looks at `slot`), and a name equal to the
    /// ROM's base name means "the latest save" to it — so two groups of one ROM both named after the ROM
    /// arrived as one autosave channel no matter what `slot` said. Naming the file after the channel is
    /// what its own uploads already do: the copy this device pushed is called "autosave.srm".
    ///
    /// Nothing on disk moves. A client resolves its own write target from the emulator and the ROM, and
    /// reads only the extension out of this name — the folder case (".zip") and the savestate case
    /// (".stateN"), both of which the extension carries unchanged.
    ///
    /// The autosave channel is left alone: the real name is what tells Argosy "this is the latest
    /// save", and what a name-blind client can safely write to disk.</summary>
    internal static string WireName(RommAssetView a)
    {
        if (a.ServeRealName || string.IsNullOrWhiteSpace(a.SlotRomm)) return a.FileName;
        var safe = SanitizeName(a.SlotRomm!);
        return safe.Length == 0 ? a.FileName : safe + Path.GetExtension(a.FileName);
    }

    /// <summary>Does this asset answer to that name — the one on disk, or the one it was served under?</summary>
    private static bool NameMatches(RommAssetView a, string fileName)
        => string.Equals(a.FileName, fileName, StringComparison.OrdinalIgnoreCase)
        || string.Equals(WireName(a), fileName, StringComparison.OrdinalIgnoreCase);

    /// <summary>A slot as a file name. Client names reach this — a branch is named after its client —
    /// so what a person typed on a handheld has to survive being a name here.</summary>
    private static string SanitizeName(string s)
    {
        var bad = Path.GetInvalidFileNameChars();
        var kept = new string(s.Where(c => Array.IndexOf(bad, c) < 0).ToArray()).Trim();
        return kept.TrimEnd('.');
    }

    private static string? EmulatorLabel(SaveGroup g)
    {
        if (!string.IsNullOrEmpty(g.EmulatorCore)) return g.EmulatorCore;
        try { return g.Emulator?.Title; } catch { return null; }
    }

    /// <summary>Resolves one asset id back to its current on-disk truth. Null when the id is unknown or
    /// the entity is gone.</summary>
    public static RommAssetView? ById(int assetId, int? tokenId = null)
    {
        var key = RommIdMap.AssetKeyOf(assetId);
        if (key == null) return null;

        if (key.StartsWith("live|", StringComparison.Ordinal))
        {
            var parts = key.Split('|');
            if (parts.Length != 4) return null;
            var game = SafeGame(parts[1]);
            if (game == null) return null;
            bool states = parts[3] == "s";
            return ListForGame(game, states, tokenId).FirstOrDefault(a => a.Id == assetId);
        }

        if (key.StartsWith("vault|", StringComparison.Ordinal))
        {
            // "vault|{gameId}|{relPath}". A two-field key is one an older build issued, before the
            // game id was part of it; nothing is left to resolve it against, so it reads as gone.
            var parts = key.Split('|');
            if (parts.Length < 3) return null;
            var e = FindVaultEntry(parts[1], string.Join("|", parts, 2, parts.Length - 2));
            if (e == null) return null;

            // The LISTING's view first: it knows the group, so it names the channel and the wire file
            // the way every other route does — ViewOf alone can only say the family ("lb"), and one
            // asset answering under two identities depending on the route is how a client's merge
            // breaks. ViewOf remains the fallback for copies the listing does not carry (a set-aside
            // group's history).
            var g2 = SafeGame(parts[1]);
            if (g2 != null)
                try
                {
                    var fromList = ListForGame(g2, e.IsState, tokenId).FirstOrDefault(a => a.Id == assetId);
                    if (fromList != null) return fromList;
                }
                catch { }
            int idx = tokenId is int t ? RommRoms.ClientIndexOf(t) : 0;
            return ViewOf(e, assetId, idx > 0 ? "c" + idx : null);
        }

        if (key.StartsWith("screen|", StringComparison.Ordinal))
        {
            var rel = key.Substring(7);
            var abs = Path.Combine(ScreensRoot, rel);
            if (!File.Exists(abs)) return null;
            var fi = new FileInfo(abs);
            var gameId = rel.Replace('\\', '/').Split('/')[0];
            return new RommAssetView
            {
                Id = assetId, RomId = RomIdOfGame(gameId), GameId = gameId,
                FileName = Path.GetFileName(abs), AbsPath = abs, Size = fi.Length,
                UpdatedUtc = fi.LastWriteTimeUtc, CreatedUtc = fi.CreationTimeUtc, IsLive = false,
            };
        }
        return null;
    }

    /// <summary>Every vault copy of one game, read from its <c>&lt;GameSave&gt;</c> records — no plugin
    /// scan, no emulator touched, so this is the cheap way to see the vault.
    ///
    /// It reads records rather than walking the folder because the folder is not an index: a file
    /// nothing records does not exist for LaunchBox. And there is no library-wide shortcut to read
    /// instead — a game's copies live in that game's records and nowhere else.</summary>
    private static List<VaultEntry> VaultEntriesOf(IGame game)
    {
        var res = new List<VaultEntry>();
        if (game is not ILiteBoxGame lbg) return res;
        var gameId = RommLibrary.IdOf(game);
        int ordinal = -1;
        try
        {
            foreach (var row in lbg.GetSubEntities("GameSave"))
            {
                ordinal++;                                                     // position among ALL rows
                var abs = SaveManager.AbsPath(row.GetValueOrDefault("FilePath") ?? "");
                if (abs.Length == 0 || !SaveVault.IsUnderVault(abs)) continue; // the live file
                bool isDir = Directory.Exists(abs);
                if (!isDir && !File.Exists(abs)) continue;                     // recorded but gone

                long size = 0; DateTime when = DateTime.UtcNow;
                try
                {
                    // CREATION time, as everywhere else in the save layer: it is what retention evicts
                    // on, and what the padlock shifts.
                    if (isDir) { when = Directory.GetCreationTimeUtc(abs); size = SaveVault.DirContentSize(abs); }
                    else { var fi = new FileInfo(abs); size = fi.Length; when = fi.CreationTimeUtc; }
                }
                catch { }

                // A record carrying a Slot is a state. Measured rule, the one the badge cache uses too.
                var slotText = row.GetValueOrDefault("Slot");
                bool isState = !string.IsNullOrWhiteSpace(slotText);
                var gid = row.GetValueOrDefault("SaveGroupId") ?? "";
                res.Add(new VaultEntry
                {
                    GameId = gameId,
                    AppId = row.GetValueOrDefault("AdditionalApplicationId"),
                    GroupId = gid,
                    GroupName = row.GetValueOrDefault("SaveGroupName") is { Length: > 0 } gn
                        ? gn : SaveManager.DefaultGroupName(isState, gid),
                    IsState = isState,
                    Slot = int.TryParse(slotText, out var sl) ? sl : null,
                    VaultPath = SaveVault.Rel(abs),
                    OriginalFileName = row.GetValueOrDefault("OriginalFileName") ?? Path.GetFileName(abs),
                    Title = row.GetValueOrDefault("Title") ?? "",
                    CreatedUtc = when, SizeBytes = size, IsDirectory = isDir,
                    Ordinal = ordinal, Locked = SaveVault.IsLockedPath(abs),
                });
            }
        }
        catch (Exception ex) { LbLog.Warn("romm", "could not read the save records: " + ex.Message); }
        return res;
    }

    private static VaultEntry? FindVaultEntry(string gameId, string vaultPath)
    {
        var game = SafeGame(gameId);
        if (game == null) return null;
        foreach (var e in VaultEntriesOf(game))
            if (string.Equals(e.VaultPath, vaultPath, StringComparison.OrdinalIgnoreCase)) return e;
        return null;
    }

    private static int VaultAssetId(VaultEntry e) =>
        RommIdMap.AssetId($"vault|{e.GameId}|{e.VaultPath}");

    /// <summary>One vault copy as the API sees it. Shared by the per-game listing, the bare listing and
    /// id lookup, which used to build the same object three times over and drift.</summary>
    private static RommAssetView ViewOf(VaultEntry e, int? assetId = null, string? mine = null)
    {
        var abs = SaveVault.Abs(e);
        return new RommAssetView
        {
            Id = assetId ?? VaultAssetId(e),
            RomId = RomIdOfGame(e.GameId),
            GameId = e.GameId,
            FileName = e.OriginalFileName is { Length: > 0 } ofn ? ofn : Path.GetFileName(abs),
            AbsPath = abs,
            IsDirectory = e.IsDirectory,
            IsState = e.IsState,
            IsLive = false,
            Size = e.SizeBytes,
            UpdatedUtc = ContentTimeUtc(abs, e.DisplayCreatedUtc),
            CreatedUtc = ContentTimeUtc(abs, e.DisplayCreatedUtc),
            Slot = e.Slot,
            SlotRomm = SlotOfGroupId(e.GroupId, mine, e.IsState, e.Slot),
            ServeRealName = BranchOf(e.GroupId) != null
                         && string.Equals(BranchOf(e.GroupId), mine, StringComparison.OrdinalIgnoreCase),
            Md5 = e.Md5,
            Missing = !(e.IsDirectory ? Directory.Exists(abs) : File.Exists(abs)),
        };
    }

    private static IGame? SafeGame(string gameId)
    {
        try { return Unbroken.LaunchBox.Plugins.PluginHelper.DataManager.GetGameById(gameId); }
        catch { return null; }
    }

    private static string ScreensRoot => LiteBoxPaths.Dir("romm-screens");

    /// <summary>A client that cannot read slots: it takes the newest of whatever it is shown and
    /// writes the served file name to disk verbatim, so it gets the single-line view. Freegosy is the
    /// one known case — and it NAMES ITSELF: "Freegosy/0.5.11", measured live, where the assumed
    /// Flutter default ("Dart/x.y (dart:io)") only shows on its side requests. Both spellings match,
    /// so a build that drops the custom header stays covered. Unknown agents get the full view — the
    /// protocol documents `slot`, and both other clients read it.</summary>
    internal static bool SlotBlind(HttpRequest? req)
    {
        var ua = req?.GetHeader("User-Agent") ?? "";
        return ua.IndexOf("freegosy", StringComparison.OrdinalIgnoreCase) >= 0
            || ua.IndexOf("dart", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    // ── DTO ───────────────────────────────────────────────────────────────────

    public static object AssetDto(RommAssetView a, string kind, string? deviceId = null)
    {
        // Saves and states are advertised under their channel; a screenshot has no channel and keeps
        // the name it was stored under.
        var wire = kind == "screenshots" ? a.FileName : WireName(a);
        var noExt = Path.GetFileNameWithoutExtension(wire);
        var syncPairs = new List<(string deviceId, object dto)>();
        foreach (var s in RommDevices.SyncsForAsset(a.Id))
        {
            var d = RommDevices.ById(s.DeviceId);
            syncPairs.Add((s.DeviceId, new
            {
                device_id = s.DeviceId,
                device_name = d?.Name,
                last_synced_at = RommAuthApi.Iso(s.LastSyncedUtc),
                is_untracked = s.Untracked,
                is_current = s.LastSyncedUtc >= a.UpdatedUtc,
            }));
        }
        // The caller's device sorts first (stable ordering, old-client compat) — upstream's rule.
        var ordered = deviceId == null ? (IEnumerable<(string deviceId, object dto)>)syncPairs
                                       : syncPairs.OrderBy(p => p.deviceId != deviceId);
        var syncs = ordered.Select(p => p.dto).ToList();

        var dto = new Dictionary<string, object?>
        {
            ["id"] = a.Id,
            ["rom_id"] = a.RomId,
            ["user_id"] = RommAuthApi.UserId,
            ["file_name"] = wire,
            ["file_name_no_tags"] = noExt,
            ["file_name_no_ext"] = noExt,
            ["file_extension"] = Path.GetExtension(wire).TrimStart('.'),
            ["file_path"] = kind,
            ["file_size_bytes"] = a.Size,
            ["full_path"] = kind + "/" + wire,
            ["download_path"] = $"/api/{kind}/{a.Id}/content?timestamp={a.UpdatedUtc:yyyyMMddHHmmss}",
            ["missing_from_fs"] = a.Missing,
            ["created_at"] = RommAuthApi.Iso(a.CreatedUtc),
            ["updated_at"] = RommAuthApi.Iso(a.UpdatedUtc),
        };
        if (kind != "screenshots")
        {
            dto["emulator"] = a.Emulator;
            dto["is_public"] = false;
            dto["screenshot"] = null;
        }
        // The channel names the ROM, so a state needs it exactly as much as a save does: a game with
        // three playable versions answers with three states too.
        if (kind is "saves" or "states") dto["slot"] = a.SlotRomm;
        if (kind == "saves")
        {
            dto["content_hash"] = a.Md5;
            dto["origin_device_id"] = null;
            dto["device_syncs"] = syncs;
        }
        if (kind == "screenshots")
        {
            dto["is_gallery"] = false;
            dto["is_public"] = false;
        }
        return dto;
    }

    // ── HTTP: saves + states (one implementation, kind-switched) ─────────────

    // ── Saves and states are OUT OF SCOPE for now ─────────────────────────────
    //
    // Nothing is served and nothing is accepted. The machinery below stays — it is correct, it was
    // expensive to measure against two real clients, and it is what the next chapter builds on — but the
    // doors are shut here, in one place, so there is no doubt about what is exposed.
    //
    // An empty list rather than an error on GET: a client asking what saves exist gets a truthful "none",
    // which it handles, instead of a failure it retries. Writes answer 501 — "not implemented", which is
    // exactly what this is, and which no client mistakes for "try again".
    private const string SavesOffMessage =
        "Save synchronisation is not available on this server yet";

    // ── Le PUSH : la branche du client, et elle seule ─────────────────────────
    //
    // Un push atterrit dans la branche du client qui pousse — toujours. Le slot annonce n'entre pas
    // dans le choix de la cible : un client qui a restaure depuis « lb-ra-snes9x » repoussera sous ce
    // nom-la (restaurer change son canal actif), et refuser serait compte comme un envoi rate puis
    // reessaye. La garantie est tenue par NOTRE ciblage, jamais par un refus.
    //
    // La save en jeu n'est touchee que si l'utilisateur a promu la branche de CE client dans Game
    // Saves — ecraser le jeu en cours est une permission qui s'accorde jeu par jeu, en promouvant, et
    // qui par defaut n'existe pas. Meme promue : strictement plus recent seulement, et jamais sans que
    // la save deplacee ait ete mise a l'abri d'abord.
    private const bool PushEnabled = true;

    private const string PushOffMessage =
        "Save uploads are paused on this server; downloads are unaffected";

    private static HttpResponse PushOff(string kind)
    {
        RommTrace.Note($"{kind}: upload refused — the push is paused");
        return RommApi.Error(501, PushOffMessage);
    }

    public static HttpResponse SavesCollection(RouteContext ctx) => Collection(ctx, states: false);
    public static HttpResponse StatesCollection(RouteContext ctx) => Collection(ctx, states: true);

    /// <summary>GET /api/saves/summary?rom_id= — the per-slot digest Grout's menus read: one row per
    /// channel with its count and newest asset. Same view as the listing, so the two can never
    /// disagree about which slots exist.</summary>
    public static HttpResponse SavesSummary(RouteContext ctx)
    {
        var refused = RommAuthApi.Require(ctx, RommScopes.AssetsRead, out var identity);
        if (refused != null) return refused;
        var req = ctx.Request!;
        int romId = req.GetQueryInt("rom_id", -1);
        var romRow = RommIndexer.RowOf(romId);
        var game = romRow == null ? null : SafeGame(romRow.GuidLb);
        if (game == null) return RommApi.Error(404, "Rom not found");

        var list = romRow!.Emulated
            ? ListForGame(game, states: false, identity?.TokenId, romRow, SlotBlind(req))
            : new List<RommAssetView>();
        var deviceId = req.GetQuery("device_id");
        var slots = list
            .GroupBy(a => a.SlotRomm ?? DefaultSlot, StringComparer.OrdinalIgnoreCase)
            .Select(gr => (object)new Dictionary<string, object?>
            {
                ["slot"] = gr.Key,
                ["count"] = gr.Count(),
                ["latest"] = AssetDto(gr.OrderByDescending(a => a.UpdatedUtc).First(), "saves", deviceId),
            })
            .ToArray();
        return RommApi.Json(new Dictionary<string, object?>
        {
            ["total_count"] = list.Count,
            ["slots"] = slots,
        });
    }

    private static HttpResponse AssetsOff(RouteContext ctx, string kind)
    {
        var refused = RommAuthApi.Require(ctx, RommScopes.AssetsRead, out _);
        if (refused != null) return refused;
        bool reading = string.Equals(ctx.Request?.Method, "GET", StringComparison.OrdinalIgnoreCase);
        RommTrace.Note(reading ? $"{kind}: not served (out of scope)" : $"{kind}: not accepted (out of scope)");
        return reading ? RommApi.Json(Array.Empty<object>()) : RommApi.Error(501, SavesOffMessage);
    }

    /// <summary>La LECTURE est ouverte, l'ECRITURE est en pause. Un GET repond honnetement ; un POST
    /// repond 501, que nul client ne confond avec « reessaie ».</summary>
    private static HttpResponse Collection(RouteContext ctx, bool states)
    {
        bool post = string.Equals(ctx.Request?.Method, "POST", StringComparison.OrdinalIgnoreCase);
        var refused = RommAuthApi.Require(ctx, post ? RommScopes.AssetsWrite : RommScopes.AssetsRead,
                                          out var identity);
        if (refused != null) return refused;
        // Avant toute lecture du multipart : un envoi refuse ne doit pas dependre de ce qu'il contient.
        if (post && !PushEnabled) return PushOff(states ? "states" : "saves");
        return post ? Upload(ctx, states, identity?.TokenId) : List(ctx, states, identity?.TokenId);
    }

    private static HttpResponse List(RouteContext ctx, bool states, int? tokenId)
    {
        var kind = states ? "states" : "saves";
        var req = ctx.Request!;
        var deviceId = req.GetQuery("device_id");
        if (deviceId != null) RommDevices.Touch(deviceId);

        int romId = req.GetQueryInt("rom_id", -1);
        if (romId > 0)
        {
            // Le rom_id NOMME la ROM : on liste son groupe, pas ceux du jeu entier.
            var romRow = RommIndexer.RowOf(romId);
            var game = romRow == null ? null : SafeGame(romRow.GuidLb);
            if (game == null) return RommApi.Error(404, "Rom not found");
            if (!romRow!.Emulated) return RommApi.Json(Array.Empty<object>());

            var list = ListForGame(game, states, tokenId, romRow, SlotBlind(req));
            // Filtered on RomM's channel, the thing the client actually named.
            var slot = req.GetQuery("slot");
            if (!string.IsNullOrEmpty(slot))
                list = list.Where(a => string.Equals(a.SlotRomm, slot, StringComparison.OrdinalIgnoreCase)).ToList();
            // What we answered, in the terms a client sorts on. Without it, "the client ignores my newest
            // save" can only be argued about; with it, the log says whether we offered it at all.
            RommTrace.Note(list.Count == 0 ? $"{kind}: nothing to offer"
                : $"{kind}: " + string.Join(", ", list.Take(6).Select(a =>
                    $"#{a.Id} {(a.IsLive ? "live" : "vault")} {a.UpdatedUtc:MM-dd HH:mm} slot={a.SlotRomm ?? "-"}"))
                  + (list.Count > 6 ? $" (+{list.Count - 6})" : ""));
            return RommApi.Json(list.Select(a => AssetDto(a, kind, deviceId)).ToArray());
        }

        // No rom filter: the vault view only. Answering a bare listing with every game's live saves
        // would drive every emulator integration in the install at once; the vault is the cheap
        // subset, because a copy is described by a record and the records are already in memory.
        var all = new List<object>();
        IGame[] games;
        try { games = Unbroken.LaunchBox.Plugins.PluginHelper.DataManager?.GetAllGames() ?? Array.Empty<IGame>(); }
        catch (Exception ex) { return RommApi.Error(500, "Could not read the library: " + ex.Message); }
        int clientIdx0 = tokenId is int tid0 ? RommRoms.ClientIndexOf(tid0) : 0;
        string? mine0 = clientIdx0 > 0 ? "c" + clientIdx0 : null;
        foreach (var g in games)
        {
            if (!RommConfig.PlatformIncluded(RommLibrary.PlatformOf(g))) continue;
            foreach (var e in VaultEntriesOf(g))
            {
                if (e.IsState != states) continue;
                all.Add(AssetDto(ViewOf(e, mine: mine0), kind, deviceId));
            }
        }
        return RommApi.Json(all.ToArray());
    }

    // Un client qui voit #54 dans une liste demande ensuite /api/saves/54 : laisser cette route fermee
    // pendant que la liste est ouverte lui montre un identifiant qu'il ne peut pas suivre.
    public static HttpResponse SaveById(RouteContext ctx) => AssetById(ctx, "saves");
    public static HttpResponse StateById(RouteContext ctx) => AssetById(ctx, "states");

    private static HttpResponse AssetById(RouteContext ctx, string kind)
    {
        bool put = string.Equals(ctx.Request?.Method, "PUT", StringComparison.OrdinalIgnoreCase);
        var refused = RommAuthApi.Require(ctx, put ? RommScopes.AssetsWrite : RommScopes.AssetsRead, out var identity);
        if (refused != null) return refused;
        // Meme porte que le POST, et avant la resolution de l'asset : une ecriture en pause se refuse
        // pour ce qu'elle est, pas parce que la cible manque.
        if (put && !PushEnabled) return PushOff(kind);

        var a = ById(ctx.GetRouteInt("id", -1), identity?.TokenId);
        if (a == null) return RommApi.Error(404, "Asset not found");

        if (put) return UpdateContent(ctx, a, identity?.TokenId);
        return RommApi.Json(AssetDto(a, kind, ctx.Request!.GetQuery("device_id")));
    }

    public static HttpResponse Content(RouteContext ctx)
    {
        var refused = RommAuthApi.Require(ctx, RommScopes.AssetsRead, out var identity);
        if (refused != null) return refused;

        var a = ById(ctx.GetRouteInt("id", -1), identity?.TokenId);
        if (a == null) return RommApi.Error(404, "Asset not found");

        if (a.IsDirectory)
        {
            // A folder save (memcard dirs) ships as one zip so a client gets a single artifact.
            var resp = HttpResponse.FromChunked(stream =>
            {
                using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
                foreach (var f in Directory.GetFiles(a.AbsPath, "*", SearchOption.AllDirectories))
                {
                    var entry = zip.CreateEntry(Path.GetRelativePath(a.AbsPath, f).Replace('\\', '/'), CompressionLevel.NoCompression);
                    using var es = entry.Open();
                    using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    fs.CopyTo(es);
                }
            }, "application/zip");
            resp.Headers["Content-Disposition"] = HttpResponse.BuildDisposition(WireName(a) + ".zip");
            return resp;
        }

        if (!File.Exists(a.AbsPath)) return RommApi.Error(404, "Asset file missing on disk");
        return HttpResponse.FromFile(a.AbsPath, "application/octet-stream", ctx.Request, WireName(a));
    }

    // ── Upload ────────────────────────────────────────────────────────────────

    private static HttpResponse Upload(RouteContext ctx, bool states, int? tokenId)
    {
        var req = ctx.Request!;
        var kind = states ? "states" : "saves";
        int romId = req.GetQueryInt("rom_id", -1);

        // Le rom_id NOMME la ROM. Plus aucune deduction depuis le nom du fichier recu : c'est ce qui
        // faisait atterrir "autosave.srm" et "… (Beta) [horodatage].state.auto" dans un groupe invente.
        var romRow = RommIndexer.RowOf(romId);
        var game = romRow == null ? null : SafeGame(romRow.GuidLb);
        if (game == null) return RommApi.Error(404, "Rom not found");
        if (!romRow!.Emulated)
            return RommApi.Error(422, "This game is not distributed as a file, so it has no saves here");

        var deviceId = req.GetQuery("device_id");
        bool overwrite = req.GetQueryBool("overwrite");
        var slot = req.GetQuery("slot");

        using var form = MultipartReader.Parse(req);
        var filePart = form?.File(states ? "stateFile" : "saveFile");
        if (filePart == null || string.IsNullOrEmpty(filePart.FileName))
            return RommApi.Error(400, "Save file has no filename");
        var fileName = Path.GetFileName(filePart.FileName!);

        // Device-conflict guard (upstream's 409): the device pushes over an asset that moved since its
        // last sync, and did not say overwrite → refuse, the device pulls first.
        // Either name: a client pushes back the name it was SERVED, which is the channel's, while a
        // client that read the disk name is just as right.
        var existing = ListForGame(game, states, tokenId).FirstOrDefault(a => a.IsLive && NameMatches(a, fileName));
        if (deviceId != null && existing != null && !overwrite)
        {
            var sync = RommDevices.SyncsForAsset(existing.Id).FirstOrDefault(s => s.DeviceId == deviceId);
            if (sync != null && sync.LastSyncedUtc < existing.UpdatedUtc.AddSeconds(-2))
                return RommApi.Error(409, "Save has been updated since your last sync");
        }

        // Land the bytes in a temp file under the client's name (the plugins resolve the TARGET name
        // themselves; the source name matters for extension detection).
        var tmpDir = Path.Combine(Path.GetTempPath(), "litebox-romm-up-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        var tmpFile = Path.Combine(tmpDir, fileName);
        try
        {
            if (!filePart.SaveTo(tmpFile)) return RommApi.Error(500, "Could not store the upload");

            SaveScan scan;
            try { scan = SaveManager.ScanBase(game); }
            catch (Exception ex) { return RommApi.Error(500, "Save scan failed: " + ex.Message); }

            // Both readings of one word. A numeric channel ("0", "1") doubles as LaunchBox's savestate
            // slot, which is a happy accident rather than a rule; "freegosy" is a channel and nothing
            // more, and used to be dropped here for failing to parse.
            int? slotInt = int.TryParse(slot, out var si) ? si : null;
            var (fresh, target, lerr) = LandUpload(game, scan, tmpFile, fileName, states, slotInt,
                                                   tokenId, ClientLabel(tokenId, deviceId), romRow);
            if (lerr != null) return RommApi.Error(422, lerr);

            // What the client gets back is the live save, because that is what a push becomes in every
            // case but one. The name it was uploaded under only narrows the search: a bundle is stored
            // under the archive's name, and its saves under their own.
            var after = LiveOf(game, states, tokenId, fileName);

            if (after == null)
            {
                // Nothing in play to answer with — no plugin owns this emulator. The version is then
                // the whole result, and it is a real one: recorded,
                // listed, and restorable by hand from Game Saves.
                if (fresh == null) return RommApi.Error(409, "Nothing was stored: the upload held only saves this game already has");
                int mineIdx = tokenId is int tidv ? RommRoms.ClientIndexOf(tidv) : 0;
                var v = ViewOf(fresh, mine: mineIdx > 0 ? "c" + mineIdx : null);
                if (deviceId != null) RommDevices.MarkSynced(deviceId, v.Id);
                return RommApi.Json(AssetDto(v, kind, deviceId), 201);
            }

            if (deviceId != null) RommDevices.MarkSynced(deviceId, after.Id);
            return RommApi.Json(AssetDto(after, kind, deviceId), 201);
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { }
        }
    }

    /// <summary>The write path both POST and PUT go through: the version first, the live save second.
    ///
    /// Import only ever lands a copy in the vault and records it — it does not write a live save, and
    /// that separation is the point. Restore is what writes one, through the emulator's own plugin, and
    /// it archives whatever it displaces on the way, so the pre-upload state stays one click away in
    /// Game Saves. With no plugin the second half simply does not happen, and the caller is told.</summary>
    private static (VaultEntry? entry, SaveGroup? group, string? error) LandUpload(
        IGame game, SaveScan scan, string tmpFile, string fileName, bool states, int? slot,
        int? tokenId, string? client, RommGameRow rom)
    {
        // L'entree d'archive que la ligne nomme, s'il y en a une. Une recherche, une fois, contre une
        // egalite de chemin — pas une deduction depuis le nom de ce que le client a envoye.
        SaveEntry? romEntry = null;
        if (rom.RomPath.Length > 0)
        {
            try
            {
                romEntry = SaveEntries.For(game, null)
                    .FirstOrDefault(e => RommIndexPass.PathEq(e.PathInArchive, rom.RomPath));
            }
            catch { }
            if (romEntry == null)
                return (null, null, "The ROM this rom_id names is no longer inside the archive");
        }

        // Sans client appaire, pas de branche pour recevoir : c'est un 422 clair plutot qu'une
        // ecriture dans une ligne qui n'appartiendrait a personne. Les trois clients vises passent
        // tous par l'appairage ; seul un identifiant de mot de passe brut arrive ici sans index.
        int clientIdx = tokenId is int tid ? RommRoms.ClientIndexOf(tid) : 0;
        if (clientIdx <= 0)
            return (null, null, "Uploads need a paired client: this credential has no save line of its own");
        string branch = "c" + clientIdx;

        // What was pushed, unpacked. One upload can hold several saves for several ROMs — Freegosy
        // bundles whenever it has more than one file, and its background queue bundles always.
        var work = Path.Combine(Path.GetDirectoryName(tmpFile) ?? Path.GetTempPath(), "unpack");
        var candidates = RommPushPlanner.Expand(tmpFile, fileName, work);
        if (candidates.Count == 0) return (null, null, "Nothing usable in the upload");

        // Toutes les pieces sont de CETTE ROM : c'est le rom_id qui le dit, pas leur nom.
        foreach (var c in candidates) c.Entry = romEntry;

        VaultEntry? firstFresh = null;
        SaveGroup? firstTarget = null;
        string? firstError = null;

        // One ROM and one kind at a time: a bundle carries a save AND a state for the same ROM, and they
        // belong to different groups.
        foreach (var lot in candidates.GroupBy(c => c.Key))
        {
            var ordered = lot.OrderByDescending(c => c.ModifiedUtc).ToList();
            bool lotStates = ordered[0].IsState;
            var entry = ordered[0].Entry;

            // La cible : la branche de CE client sur CETTE ROM. Le nom du fichier recu n'entre pas
            // dans le choix, et le slot annonce non plus — il ne sert qu'a distinguer une save d'un
            // savestate, ce que RommPushPlanner a deja fait.
            var target = (lotStates ? scan.States : scan.Files).FirstOrDefault(g =>
                BelongsToRom(g, rom)
                && string.Equals(BranchOf(g.GroupId), branch, StringComparison.OrdinalIgnoreCase));

            // La branche EN JEU — l'utilisateur l'a promue dans Game Saves — est le seul cas ou un
            // push touche la save que l'emulateur lit.
            bool promoted = target?.Active != null;

            if (target == null)
                RommTrace.Note("this client has no line of its own yet for this ROM — the push starts it");

            // What we already hold for this group, live and archived. A push that brings back what is
            // already here is the normal end of a round trip and must cost nothing.
            var known = KnownHashes(target);

            bool tookLive = false;
            foreach (var c in ordered)
            {
                if (c.Md5.Length > 0 && known.Contains(c.Md5))
                {
                    RommTrace.Note($"already held: {c.FileName}");
                    continue;
                }

                // The newest genuinely-new piece is the one that may also go live — and only when
                // the user promoted this client's line. Everything else is history at its own date.
                bool liveEligible = promoted && !tookLive;
                tookLive = true;

                var (fresh, err) = Land(game, c, lotStates, slot, target, entry,
                                        client, liveEligible, known, branch, BranchName(client));
                if (err != null) { firstError ??= err; continue; }
                // Held now whatever became of it — live save or version. A bundle carrying the same
                // bytes twice must not be filed twice.
                if (c.Md5.Length > 0) known.Add(c.Md5);
                if (fresh != null) firstFresh ??= fresh;
                firstTarget ??= target;
            }
        }

        if (firstFresh == null && firstError != null) return (null, null, firstError);
        if (firstFresh == null) return (null, firstTarget, null);   // everything was already held
        return (firstFresh, firstTarget, null);
    }

    /// <summary>Files one candidate into the client's branch: a labelled vault copy always, and the
    /// live save too when the branch is the save in play — the user promoted it — and the piece is
    /// strictly newer than what plays.
    ///
    /// The copy is filed FIRST. On the promoted line it doubles the history (the displaced save is
    /// secured separately), which is deliberate: the labelled copy is the record of what arrived and
    /// from whom, and it is what the user looks at in Game Saves before trusting a client further.</summary>
    private static (VaultEntry? fresh, string? error) Land(
        IGame game, PushCandidate c, bool states, int? slot, SaveGroup? target, SaveEntry? entry,
        string? client, bool liveEligible, HashSet<string> known, string branch, string? branchName)
    {
        // An older copy only earns a vault place if the cap has one to give. Evicting a newer save to
        // file an older one would be the opposite of what a retention limit is for.
        if (!liveEligible && target != null && !HasRoomFor(target, c.ModifiedUtc))
        {
            RommTrace.Note($"skipped (older than everything the cap holds): {c.FileName}");
            return (null, null);
        }

        var before = new HashSet<string>(VaultEntriesOf(game).Select(e => e.VaultPath), StringComparer.OrdinalIgnoreCase);
        var err = SaveManager.Import(game, c.TempPath, states, slot, target?.AppId, entry: entry,
                                     originalName: c.FileName,
                                     branch: target == null ? branch : null,
                                     groupName: branchName);
        if (err != null) return (null, "Import failed: " + err);

        var fresh = VaultEntriesOf(game).FirstOrDefault(e => !before.Contains(e.VaultPath));
        if (fresh == null) return (null, "Import reported success but no version was recorded");

        // Dated by its own content, so the history reads chronologically instead of by arrival. It is
        // also what retention evicts on, which is the point: the oldest SAVE goes, not the oldest copy.
        try { File.SetCreationTimeUtc(SaveVault.Abs(fresh), c.ModifiedUtc); } catch { }

        if (target != null) Adopt(game, fresh, target, client);

        if (!liveEligible)
        {
            RommTrace.Note($"archived as a version: {c.FileName} ({c.ModifiedUtc:HH:mm:ss})");
            return (fresh, null);
        }

        // The branch is the save in play. Strictly newer only: a phone clock that drifted backwards
        // must not put an old game back in play — the copy stays in the history either way.
        var liveTime = target!.LastModified?.ToUniversalTime() ?? DateTime.MinValue;
        if (c.ModifiedUtc <= liveTime)
        {
            RommTrace.Note($"older than the line in play — filed, not applied: {c.FileName}");
            return (fresh, null);
        }
        if (target.Plugin == null)
        {
            RommTrace.Note("no integration plugin — filed, not applied: " + c.FileName);
            return (fresh, null);
        }
        // The displaced save is secured BEFORE anything overwrites it, and a failed net stops the act:
        // a full disk or a file the emulator holds must never turn into a silent overwrite.
        if (!PreserveBeforeOverwrite(target))
            return (fresh, "The save in play could not be secured first, so it was not replaced; " +
                           "the upload was archived as a version instead");
        var perr = SaveManager.RestoreFrom(target, c.TempPath, slot, states, () => true,
                                           RommLabel(client));
        if (perr != null)
        {
            RommTrace.Note("could not apply the save: " + perr);
            return (fresh, "Could not apply the save: " + perr);
        }
        RommTrace.Note($"replaced the save in play: {c.FileName} ({c.ModifiedUtc:HH:mm:ss})");
        return (fresh, null);
    }

    /// <summary>The group a just-imported copy landed in, re-read from disk.
    ///
    /// The scan the request started with predates the import and has no group for a ROM that had no save
    /// at all, so there is nothing to promote onto without asking again.</summary>
    private static SaveGroup? FreshGroupFor(IGame game, VaultEntry fresh)
    {
        try
        {
            var scan = SaveManager.ScanBase(game);
            return scan.Files.Concat(scan.States).FirstOrDefault(g =>
                g.Plugin != null && !g.RecordOnly &&
                string.Equals(g.GroupId, fresh.GroupId, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) { RommTrace.Note("rescan failed: " + ex.Message); return null; }
    }

    /// <summary>Does this group belong to <paramref name="entry"/>? Both null means the game is not an
    /// archive and there is nothing to match.</summary>
    private static bool SameEntry(SaveGroup g, SaveEntry? entry)
    {
        var key = SaveManager.EntryKeyOf(g.GroupId);
        if (entry == null) return key == null;
        return key != null && string.Equals(key, entry.Key, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Every content hash the group already holds, live and archived.</summary>
    private static HashSet<string> KnownHashes(SaveGroup? g)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (g == null) return set;
        try
        {
            if (g.Active != null && g.ActivePath.Length > 0 && !g.ActiveIsDirectory && File.Exists(g.ActivePath))
                set.Add(SaveManager.FileMd5(g.ActivePath));
            foreach (var b in g.Backups)
            {
                var abs = SaveVault.Abs(b);
                if (!b.IsDirectory && File.Exists(abs)) set.Add(SaveManager.FileMd5(abs));
            }
        }
        catch { }
        set.Remove("");
        return set;
    }

    /// <summary>Is there room for a copy of this age? Under the cap, always. At the cap, only if it is
    /// newer than the oldest copy already there — which will then make way for it.</summary>
    private static bool HasRoomFor(SaveGroup g, DateTime when)
    {
        try
        {
            int cap = SaveBackupService.MaxVersionsPerGame;
            if (cap <= 0 || g.Backups.Count < cap) return true;
            var oldest = g.Backups.Min(b => b.DisplayCreatedUtc);
            return when > oldest;
        }
        catch { return true; }
    }

    /// <summary>The archive entry an uploaded save belongs to, or null when the game is not an archive
    /// or nothing identifies the entry.
    ///
    /// The file name is the whole evidence, because both ends derive it from the ROM — a handheld that
    /// ran "Sonic (Japan).md" writes "Sonic (Japan).srm" — which is exactly how PromptImportEntry
    /// pre-selects in the desktop dialog.</summary>
    private static SaveEntry? ResolveEntry(IGame game, string fileName, int? tokenId)
    {
        try
        {
            var entries = SaveEntries.For(game, null);
            if (entries.Count == 0) return null;

            // The FILE NAME. Both ends derive it from the ROM that actually ran — a handheld that
            // played "Sonic (Japan).md" writes "Sonic (Japan).srm" — so it is evidence about THIS file,
            // which is what no record of a past download could ever be.
            var stem = Path.GetFileNameWithoutExtension(fileName);
            var byName = entries.FirstOrDefault(e => string.Equals(
                Path.GetFileNameWithoutExtension(e.FileName), stem, StringComparison.OrdinalIgnoreCase));

            if (byName != null) return byName;

            // No name match: a client that renames its saves, or a name mangled on the way. There is
            // nothing else to go on now that clients are not bound to an entry, and guessing would file
            // the save under a ROM it was never played on. The main bucket is the honest answer.
            RommTrace.Note($"no entry matches \"{fileName}\"");
            return null;
        }
        catch { return null; }
    }

    /// <summary>Would writing <paramref name="incoming"/> actually change the live save? Compared by
    /// content, not by date or size: a client that pushes the same bytes back — which is the normal end
    /// of a sync round trip — must not leave a copy behind every time.
    ///
    /// Unreadable, missing, or a folder save on the other side all answer true. Assuming a change costs
    /// one redundant copy; assuming none could lose the only record of what was there.</summary>
    private static bool WouldChange(string incoming, SaveGroup g)
    {
        try
        {
            if (g.Active == null || g.ActivePath.Length == 0) return true;
            if (g.ActiveIsDirectory || !File.Exists(g.ActivePath)) return true;
            return !string.Equals(SaveManager.FileMd5(incoming), SaveManager.FileMd5(g.ActivePath),
                                  StringComparison.OrdinalIgnoreCase);
        }
        catch { return true; }
    }

    /// <summary>Copies the live save into the vault before a sync overwrites it, labelled with the
    /// client that asked for the write.
    ///
    /// Restore already backs up what it displaces, so this is not about whether a copy exists — it is
    /// about whether the history can be read six months later. An unnamed copy among a dozen others
    /// tells you nothing; "RomM sync · Steam Deck" tells you a device pushed over your desktop progress
    /// and which one. The dirty check means an unchanged save costs nothing: the original is already in
    /// the vault, and no second copy is made.</summary>
    /// <summary>Keeps the save about to be overwritten, unless the vault already holds it.
    ///
    /// It is NOT relabelled. It did not come from RomM — it is what was here before, pushed aside — and
    /// whatever it was called while in play goes into the vault with it, which WriteBackupRow now does on
    /// its own. Labelling it after the client that displaced it was the mistake: a label says what a file
    /// IS, not what happened to be running when it moved.</summary>
    /// <summary>Secures the save a write is about to displace. FALSE means the net is NOT in place —
    /// a full disk, a file the emulator holds — and the caller must not overwrite: a failed backup
    /// that still lets the write through is a silent overwrite with extra steps.</summary>
    private static bool PreserveBeforeOverwrite(SaveGroup g)
    {
        try
        {
            if (g.Active == null || g.ActivePath.Length == 0) return true;

            var res = SaveManager.Backup(g, force: false, auto: true);
            if (res == null) return true;
            if (res.Error != null) { RommTrace.Note("could not keep the live save: " + res.Error); return false; }
            if (res.Entry == null)
            {
                // Identical to the latest copy — the original is already in the vault, under whatever
                // label it was given then. Nothing to add, and a duplicate would only clutter.
                RommTrace.Note("live save already archived, nothing to keep");
                return true;
            }
            RommTrace.Note("kept the live save as \"" + res.Entry.Title + "\"");
            return true;
        }
        catch (Exception ex) { RommTrace.Note("could not keep the live save: " + ex.Message); return false; }
    }

    /// <summary>The save in play for this game, preferring the one named like what was uploaded — a
    /// bundle is stored under the archive's name and its pieces under theirs, so the name only narrows
    /// the search and never decides alone.</summary>
    private static RommAssetView? LiveOf(IGame game, bool states, int? tokenId, string? fileName)
    {
        var live = ListForGame(game, states, tokenId).Where(a => a.IsLive).ToList();
        return live.FirstOrDefault(a => NameMatches(a, fileName))
            ?? live.FirstOrDefault();
    }

    /// <summary>The heading a client's own save line carries in Game Saves. Its own name, so it reads
    /// as a timeline rather than as a stray copy.</summary>
    private static string BranchName(string? client)
        => string.IsNullOrWhiteSpace(client) ? "RomM client" : client!;

    /// <summary>Who caused a write, in the terms the save history should show: the paired client's name
    /// when it authenticated with a token, else the device id it volunteered, else nothing useful.</summary>
    /// <summary>The default rom id of a game known only by its guid. Assets hang off a game, and the
    /// one they name is its default slot — a locked client reads its own id from the listing, never from
    /// an asset.</summary>
    private static int RomIdOfGame(string gameId)
    {
        var g = SafeGame(gameId);
        return g == null ? 0 : (int)RommRoms.DefaultRomId(g, gameId);
    }

    private static string? ClientLabel(int? tokenId, string? deviceId)
    {
        try
        {
            if (tokenId is int id)
            {
                var name = RommAuth.ListTokens().FirstOrDefault(t => t.Id == id)?.Name;
                if (!string.IsNullOrWhiteSpace(name)) return name;
                return "client #" + id;
            }
        }
        catch { }
        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            try
            {
                var dev = RommDevices.ById(deviceId!);
                if (!string.IsNullOrWhiteSpace(dev?.Name)) return dev!.Name;
            }
            catch { }
            return "device " + deviceId;
        }
        return null;
    }

    /// <summary>What a file received from a client is called, wherever it ends up. One label, because
    /// there is one fact to record: these bytes came from that client. Whether they are in play or in the
    /// history is not the label's business — that is what the path says.</summary>
    /// <summary>Moves the row whose FilePath is <paramref name="freshAbs"/> in front of the first row
    /// of <paramref name="groupId"/>, making it the row the scan reads as the group's record. Pure list
    /// surgery so the self-test can pin it; true when something actually moved.</summary>
    internal static bool ReanchorRows(List<Dictionary<string, string>> rows, string groupId, string freshAbs)
    {
        int anchor = -1, freshIdx = -1;
        for (int i = 0; i < rows.Count; i++)
        {
            if (!string.Equals(rows[i].GetValueOrDefault("SaveGroupId"), groupId, StringComparison.OrdinalIgnoreCase))
                continue;
            if (anchor < 0) anchor = i;
            if (SaveManager.PathEq(SaveManager.AbsPath(rows[i].GetValueOrDefault("FilePath") ?? ""), freshAbs))
            { freshIdx = i; break; }
        }
        if (anchor < 0 || freshIdx < 0 || freshIdx == anchor) return false;
        var moved = rows[freshIdx];
        rows.RemoveAt(freshIdx);
        rows.Insert(anchor, moved);
        return true;
    }

    /// <summary>Deletes a revoked client's save lines across the library: every branch group of that
    /// client — saves and savestates, records AND files, history included. A PROMOTED branch is the
    /// game's save in play; it is only touched when <paramref name="includePromoted"/> says so, and an
    /// un-included promoted line is left WHOLE — half-deleting a line the emulator reads would leave a
    /// live save with no history for no one's benefit. Called BEFORE the token goes: the client index
    /// resolves through it.</summary>
    internal static (int groups, int files) DeleteClientLines(int tokenId, bool includePromoted)
    {
        int idx;
        try { idx = RommRoms.ClientIndexOf(tokenId); } catch { return (0, 0); }
        if (idx <= 0) return (0, 0);
        string mine = "c" + idx;

        int groups = 0, files = 0;
        Unbroken.LaunchBox.Plugins.Data.IGame[] games;
        try { games = Unbroken.LaunchBox.Plugins.PluginHelper.DataManager?.GetAllGames() ?? Array.Empty<Unbroken.LaunchBox.Plugins.Data.IGame>(); }
        catch { return (0, 0); }

        foreach (var game in games)
        {
            if (game is not ILiteBoxGame lbg) continue;
            List<Dictionary<string, string>> rows;
            try
            {
                rows = lbg.GetSubEntities("GameSave")
                          .Select(r => new Dictionary<string, string>(r, StringComparer.Ordinal)).ToList();
            }
            catch { continue; }
            if (rows.Count == 0) continue;

            // The client's groups on this game, and whether each is promoted (a record whose file lives
            // OUTSIDE the vault is the save the emulator reads).
            var mineRows = rows.Where(r => string.Equals(BranchOf(r.GetValueOrDefault("SaveGroupId")), mine,
                                                         StringComparison.OrdinalIgnoreCase)).ToList();
            if (mineRows.Count == 0) continue;

            var byGroup = mineRows.GroupBy(r => r.GetValueOrDefault("SaveGroupId") ?? "",
                                           StringComparer.OrdinalIgnoreCase);
            var doomed = new List<Dictionary<string, string>>();
            foreach (var grp in byGroup)
            {
                bool promoted = grp.Any(r =>
                {
                    var abs = SaveManager.AbsPath(r.GetValueOrDefault("FilePath") ?? "");
                    return abs.Length > 0 && !SaveVault.IsUnderVault(abs);
                });
                if (promoted && !includePromoted) continue;   // the line in play stays whole
                doomed.AddRange(grp);
                groups++;
            }
            if (doomed.Count == 0) continue;

            foreach (var r in doomed)
            {
                var abs = SaveManager.AbsPath(r.GetValueOrDefault("FilePath") ?? "");
                if (abs.Length == 0) continue;
                try
                {
                    if (Directory.Exists(abs)) { Directory.Delete(abs, recursive: true); files++; }
                    else if (File.Exists(abs)) { File.Delete(abs); files++; }
                }
                catch (Exception ex) { LbLog.Warn("romm", "could not delete a client save file: " + ex.Message); }
            }
            try
            {
                var kept = rows.Where(r => !doomed.Contains(r)).ToList();
                lbg.SetSubEntities("GameSave", kept);
                SaveVault.Notify(RommLibrary.IdOf(game));
            }
            catch (Exception ex) { LbLog.Warn("romm", "could not drop a client's save records: " + ex.Message); }
        }
        if (groups > 0) LbLog.Info("romm", $"revoked client: {groups} save line(s) deleted, {files} file(s)");
        return (groups, files);
    }

    /// <summary>Carries a client's rename into the library: the branch groups still bearing the OLD
    /// name take the new one — a group the user renamed by hand in Game Saves is left alone — and every
    /// « RomM · old » label follows, vault copies and promoted live records alike. Without this, Game
    /// Saves keeps speaking the old name and the rename creates the very confusion it was meant to end.
    /// One user-triggered walk over in-memory records; returns how many rows moved.</summary>
    internal static int RenameClientMarks(int tokenId, string oldName, string newName)
    {
        int idx;
        try { idx = RommRoms.ClientIndexOf(tokenId); } catch { return 0; }
        if (idx <= 0 || string.IsNullOrWhiteSpace(oldName) || string.IsNullOrWhiteSpace(newName)) return 0;
        string mine = "c" + idx;
        string oldLabel = RommLabel(oldName), newLabel = RommLabel(newName);

        int moved = 0;
        Unbroken.LaunchBox.Plugins.Data.IGame[] games;
        try { games = Unbroken.LaunchBox.Plugins.PluginHelper.DataManager?.GetAllGames() ?? Array.Empty<Unbroken.LaunchBox.Plugins.Data.IGame>(); }
        catch { return 0; }
        foreach (var game in games)
        {
            if (game is not ILiteBoxGame lbg) continue;
            List<Dictionary<string, string>> rows;
            try
            {
                rows = lbg.GetSubEntities("GameSave")
                          .Select(r => new Dictionary<string, string>(r, StringComparer.Ordinal)).ToList();
            }
            catch { continue; }
            if (rows.Count == 0) continue;

            bool dirty = false;
            foreach (var row in rows)
            {
                bool isMine = string.Equals(BranchOf(row.GetValueOrDefault("SaveGroupId")), mine,
                                            StringComparison.OrdinalIgnoreCase);
                if (isMine && string.Equals(row.GetValueOrDefault("SaveGroupName"), oldName, StringComparison.Ordinal))
                { row["SaveGroupName"] = newName; dirty = true; moved++; }
                if (string.Equals(row.GetValueOrDefault("Title"), oldLabel, StringComparison.Ordinal))
                { row["Title"] = newLabel; dirty = true; moved++; }
            }
            if (!dirty) continue;
            try
            {
                lbg.SetSubEntities("GameSave", rows);
                SaveVault.Notify(RommLibrary.IdOf(game));
            }
            catch (Exception ex) { LbLog.Warn("romm", "rename propagation failed on a game: " + ex.Message); }
        }
        if (moved > 0) LbLog.Info("romm", $"client rename: {moved} record(s) now say \"{newName}\"");
        return moved;
    }

    private static string RommLabel(string? client)
        => "RomM · " + (string.IsNullOrWhiteSpace(client) ? "unknown client" : client);

    /// <summary>Moves a freshly imported copy into the group it is a version of, and names it.
    ///
    /// Import gives a standalone import a fresh group id of its own — right for Add Save File, wrong
    /// here: a device pushing its save every night would leave one orphan group per push on the game's
    /// save page. The record is rewritten through the same primitive Edit Label uses, so LaunchBox reads
    /// the result as one more copy of the group, which is exactly what it is.</summary>
    private static void Adopt(IGame game, VaultEntry e, SaveGroup g, string? client)
    {
        try
        {
            if (game is not ILiteBoxGame lbg) return;
            var rows = lbg.GetSubEntities("GameSave")
                          .Select(r => new Dictionary<string, string>(r, StringComparer.Ordinal)).ToList();
            var abs = SaveVault.Abs(e);
            var row = rows.FirstOrDefault(r =>
                SaveManager.PathEq(SaveManager.AbsPath(r.GetValueOrDefault("FilePath") ?? ""), abs));
            if (row == null) return;
            row["SaveGroupId"] = g.GroupId;
            row["MatchLineageId"] = g.GroupId;
            row["SaveGroupName"] = g.GroupName;
            row["Title"] = RommLabel(client);           // LaunchBox prints Title as the copy's heading
            if (!string.IsNullOrEmpty(g.AppId)) row["AdditionalApplicationId"] = g.AppId!;

            // Re-anchor an In-Vault branch onto its newest copy. The scan takes the FIRST row of a
            // SaveGroupId as the group's record, so a branch created by the first push stayed pinned
            // to it for ever: the card read the oldest date while the branch's life was in the
            // backups. A PROMOTED branch needs none of this — its face is the live file, which the
            // push just rewrote. Only strictly-newer content moves the anchor: the pieces of a bundle
            // arrive newest first, and the older ones must not drag it back.
            if (g.Active == null && g.ActivePath.Length > 0 && SaveVault.IsUnderVault(g.ActivePath)
                && ContentTimeUtc(abs, DateTime.MinValue) >= ContentTimeUtc(g.ActivePath, DateTime.MaxValue))
                ReanchorRows(rows, g.GroupId, abs);

            lbg.SetSubEntities("GameSave", rows);
            e.GroupId = g.GroupId; e.GroupName = g.GroupName; e.Title = row["Title"];
            SaveVault.Notify(e.GameId);
        }
        catch (Exception ex) { LbLog.Warn("romm", "could not adopt the uploaded copy: " + ex.Message); }
    }

    private static HttpResponse UpdateContent(RouteContext ctx, RommAssetView a, int? tokenId)
    {
        // PUT lands in the requester's branch like every other write. It is honest only when the asset
        // addressed IS the requester's own channel — its autosave view, live or not: a PUT against
        // somebody else's line would "succeed" into a different group than the one addressed, and the
        // client would then re-read an asset its write never touched.
        if (!a.ServeRealName)
            return RommApi.Error(422, "This save line is read-only for this client; upload to your own channel instead");

        var game = SafeGame(a.GameId);
        if (game == null) return RommApi.Error(404, "Rom not found");

        using var form = MultipartReader.Parse(ctx.Request!);
        var filePart = form?.File();
        if (filePart == null) return RommApi.Error(400, "No file in the request");

        SaveScan scan;
        try { scan = SaveManager.ScanBase(game); }
        catch (Exception ex) { return RommApi.Error(500, "Save scan failed: " + ex.Message); }

        var tmpDir = Path.Combine(Path.GetTempPath(), "litebox-romm-up-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        var tmpFile = Path.Combine(tmpDir, a.FileName);
        try
        {
            if (!filePart.SaveTo(tmpFile)) return RommApi.Error(500, "Could not store the upload");

            // The asset already names its rom, so the row comes from there rather than from a query
            // string: a PUT addresses something that exists, and what it names is not in doubt.
            var putRow = RommIndexer.RowOf(a.RomId);
            if (putRow == null) return RommApi.Error(404, "Rom not found");

            var (fresh, _, lerr) = LandUpload(game, scan, tmpFile, a.FileName, a.IsState,
                                              a.Slot, tokenId,
                                              ClientLabel(tokenId, ctx.Request!.GetQuery("device_id")),
                                              putRow);
            if (lerr != null) return RommApi.Error(422, lerr);

            // Answer with the requester's view of what its channel holds now: the addressed asset when
            // it still resolves, else the newest live match, else the copy just filed.
            var live = LiveOf(game, a.IsState, tokenId, a.FileName);
            int mineIdxPut = tokenId is int tidPv ? RommRoms.ClientIndexOf(tidPv) : 0;
            var after = ById(a.Id, tokenId) ?? live
                     ?? (fresh != null ? ViewOf(fresh, mine: mineIdxPut > 0 ? "c" + mineIdxPut : null) : null);
            if (after == null) return RommApi.Error(409, "Nothing was stored: the upload held only saves this game already has");
            var deviceId = ctx.Request!.GetQuery("device_id");
            if (deviceId != null) RommDevices.MarkSynced(deviceId, after.Id);
            return RommApi.Json(AssetDto(after, a.IsState ? "states" : "saves", deviceId));
        }
        finally { try { Directory.Delete(tmpDir, recursive: true); } catch { } }
    }

    // ── Bulk delete + sync verbs ─────────────────────────────────────────────

    public static HttpResponse DeleteSaves(RouteContext ctx) => AssetsOff(ctx, "saves");
    public static HttpResponse DeleteStates(RouteContext ctx) => AssetsOff(ctx, "states");

    private static HttpResponse BulkDeleteUnused(RouteContext ctx, string k) => BulkDelete(ctx, k);

    private static HttpResponse BulkDelete(RouteContext ctx, string bodyKey)
    {
        var refused = RommAuthApi.Require(ctx, RommScopes.AssetsWrite, out _);
        if (refused != null) return refused;
        if (!string.Equals(ctx.Request?.Method, "POST", StringComparison.OrdinalIgnoreCase))
            return RommApi.Error(405, "Method not allowed");

        List<int> ids = new();
        try
        {
            using var doc = JsonDocument.Parse(ctx.Request!.Body);
            if (doc.RootElement.TryGetProperty(bodyKey, out var arr) && arr.ValueKind == JsonValueKind.Array)
                ids = arr.EnumerateArray().Where(e => e.TryGetInt32(out _)).Select(e => e.GetInt32()).ToList();
        }
        catch { return RommApi.Error(400, "Malformed body"); }
        if (ids.Count == 0) return RommApi.Error(400, $"No {bodyKey} were provided");

        var deleted = new List<int>();
        foreach (var id in ids)
        {
            var key = RommIdMap.AssetKeyOf(id);
            // Vault versions only: the LIVE save is never deleted through this API — a client bug must
            // not be able to erase somebody's real progress.
            if (key == null || !key.StartsWith("vault|", StringComparison.Ordinal)) continue;
            var parts = key.Split('|');
            if (parts.Length < 3) continue;
            var e = FindVaultEntry(parts[1], string.Join("|", parts, 2, parts.Length - 2));
            if (e == null) continue;
            // Hand the owner over: deleting a copy also removes the record that describes it, and the
            // record lives on the game. Re-resolving it from the path alone is exactly what is no
            // longer possible.
            if (SaveManager.DeleteBackup(e, SafeGame(e.GameId)) == null) deleted.Add(id);
        }
        return RommApi.Json(deleted.ToArray());
    }

    public static HttpResponse ConfirmDownloaded(RouteContext ctx)
    {
        var refused = RommAuthApi.Require(ctx, RommScopes.DevicesWrite, out var identity);
        if (refused != null) return refused;

        var a = ById(ctx.GetRouteInt("id", -1), identity?.TokenId);
        if (a == null) return RommApi.Error(404, "Asset not found");

        string? deviceId = null;
        try
        {
            using var doc = JsonDocument.Parse(ctx.Request!.Body);
            if (doc.RootElement.TryGetProperty("device_id", out var d)) deviceId = d.GetString();
        }
        catch { }
        if (string.IsNullOrEmpty(deviceId)) return RommApi.Error(400, "Missing device_id");
        if (RommDevices.ById(deviceId!) == null) return RommApi.Error(404, $"Device with ID {deviceId} not found");

        RommDevices.MarkSynced(deviceId!, a.Id);
        return RommApi.Json(AssetDto(a, a.IsState ? "states" : "saves", deviceId));
    }

    public static HttpResponse Track(RouteContext ctx) => SetTracking(ctx, tracked: true);
    public static HttpResponse Untrack(RouteContext ctx) => SetTracking(ctx, tracked: false);

    private static HttpResponse SetTracking(RouteContext ctx, bool tracked)
    {
        var refused = RommAuthApi.Require(ctx, RommScopes.DevicesWrite, out var identity);
        if (refused != null) return refused;

        var a = ById(ctx.GetRouteInt("id", -1), identity?.TokenId);
        if (a == null) return RommApi.Error(404, "Asset not found");

        string? deviceId = null;
        try
        {
            using var doc = JsonDocument.Parse(ctx.Request!.Body ?? "{}");
            if (doc.RootElement.TryGetProperty("device_id", out var d)) deviceId = d.GetString();
        }
        catch { }

        RommDevices.SetTracked(deviceId, a.Id, tracked);
        return RommApi.Json(AssetDto(a, a.IsState ? "states" : "saves", deviceId));
    }

    // ── Screenshots ───────────────────────────────────────────────────────────

    public static HttpResponse ScreenshotsCollection(RouteContext ctx)
    {
        bool post = string.Equals(ctx.Request?.Method, "POST", StringComparison.OrdinalIgnoreCase);
        var refused = RommAuthApi.Require(ctx, post ? RommScopes.AssetsWrite : RommScopes.AssetsRead, out _);
        if (refused != null) return refused;

        if (post)
        {
            int romId = ctx.Request!.GetQueryInt("rom_id", -1);
            var game = RommLibrary.GameByRomId(romId);
            if (game == null) return RommApi.Error(404, "Rom not found");

            using var form = MultipartReader.Parse(ctx.Request);
            var part = form?.File("screenshotFile");
            if (part == null || string.IsNullOrEmpty(part.FileName)) return RommApi.Error(400, "No screenshot in the request");

            var gameId = RommLibrary.IdOf(game);
            var dir = Path.Combine(ScreensRoot, gameId);
            Directory.CreateDirectory(dir);
            var target = Path.Combine(dir, Path.GetFileName(part.FileName!));
            if (!part.SaveTo(target)) return RommApi.Error(500, "Could not store the screenshot");

            var rel = Path.GetRelativePath(ScreensRoot, target);
            var view = ById(RommIdMap.AssetId("screen|" + rel));
            return view == null ? RommApi.Error(500, "Stored but unreadable") : RommApi.Json(AssetDto(view, "screenshots"), 201);
        }

        int filterRom = ctx.Request!.GetQueryInt("rom_id", -1);
        var list = new List<object>();
        try
        {
            foreach (var f in Directory.Exists(ScreensRoot)
                     ? Directory.GetFiles(ScreensRoot, "*", SearchOption.AllDirectories)
                     : Array.Empty<string>())
            {
                var rel = Path.GetRelativePath(ScreensRoot, f);
                var view = ById(RommIdMap.AssetId("screen|" + rel));
                if (view == null) continue;
                if (filterRom > 0 && view.RomId != filterRom) continue;
                list.Add(AssetDto(view, "screenshots"));
            }
        }
        catch { }
        return RommApi.Json(list.ToArray());
    }

    public static HttpResponse ScreenshotById(RouteContext ctx)
    {
        var refused = RommAuthApi.Require(ctx, RommScopes.AssetsRead, out _);
        if (refused != null) return refused;
        var a = ById(ctx.GetRouteInt("id", -1));
        return a == null ? RommApi.Error(404, "Screenshot not found") : RommApi.Json(AssetDto(a, "screenshots"));
    }
}
