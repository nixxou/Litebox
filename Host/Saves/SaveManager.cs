// Save management — the HOST side of LaunchBox's save-management feature (Edit Game → Game Saves),
// re-implemented for LiteBox on top of the SAME per-emulator logic LaunchBox uses.
//
// Architecture — see docs/saves.md for what is established and how, and docs/save-algorithms.md for
// each plugin's algorithm. (ExtendDB/docs/lb-save-management.md is the original RE; two of its
// conclusions about the vault have since been disproved — saves.md §2.4 says which.)
//   • All emulator-specific logic (where saves live, how they're named/backed-up/restored) is in the
//     NON-obfuscated "<Emulator> LaunchBox Integration" plugins (EmulatorPlugin subclasses). LiteBox
//     already hosts them (EmuPlugins) — this class just drives the same contract LB drives:
//       GetSaves → scan the ACTIVE saves; AddSaveFile → import/restore (copies TO the emulator's live
//       location under the emulator's expected name); RemoveSave; TryBackupSave (container extract);
//       TryComputeSaveSignature; IsSaveActive; IsSaveContainer.
//   • Persisted GROUP records: <GameSave> elements in the Platform XML — the exact schema LB 13.27
//     writes (SaveGroupId / SaveGroupName / MatchLineageId / FilePath / …), stored through the
//     ILiteBoxGame sub-entity API (Tier-1 "GameSave", op-log → surgical XML write). A library edited
//     by LiteBox therefore shows the same groups/names when opened in LaunchBox and vice-versa.
//   • Vault (backups): files under <LB>\Saves\<Platform>\ with LB's naming (name.ext, name-01.ext, …;
//     folder saves are copied as folders). Backup METADATA (which vault file belongs to which group,
//     labels, timestamps, md5) is LiteBox-owned in Core\litebox\saves-vault.json — LB 13.27's own vault
//     metadata format is not observable yet (no backups existed to RE), so we deliberately do not guess
//     at its <GameSave> shape for backups; only ACTIVE records are shared. Revisit when observed.
//
// Scope (v1): the BASE game's saves (Edit Game page). Additional-version pages and automatic backups
// (on game close / periodic) come later. ExtendDB multi-ROM archives: later (needs picked-ROM identity).

#nullable enable

using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Data;
using LbApiHost.Generated;
using LbApiHost.Host.Media;

namespace LbApiHost.Host.Saves;

/// <summary>IEmulator decorator that exposes an ABSOLUTE ApplicationPath while delegating everything
/// else to the wrapped emulator — so the integration plugins resolve retroarch.cfg / save dirs from an
/// absolute base, independent of the process CWD. Subclasses DummyEmulator (which supplies default
/// impls for the whole surface); we override only what a save scan reads.</summary>
internal sealed class AbsPathEmulator : DummyEmulator
{
    private readonly IEmulator _inner;
    private readonly string _absPath;
    public AbsPathEmulator(IEmulator inner, string absPath) { _inner = inner; _absPath = absPath; }

    public override string ApplicationPath { get => _absPath; set { } }
    public override string Id { get { try { return _inner.Id; } catch { return ""; } } set { } }
    public override string Title { get { try { return _inner.Title; } catch { return ""; } } set { } }
    public override string CommandLine { get { try { return _inner.CommandLine; } catch { return ""; } } set { } }
    public override string DefaultPlatform { get { try { return _inner.DefaultPlatform; } catch { return ""; } } set { } }
    public override IEmulatorPlatform[] GetAllEmulatorPlatforms()
    { try { return _inner.GetAllEmulatorPlatforms(); } catch { return System.Array.Empty<IEmulatorPlatform>(); } }
}

// ── Models ───────────────────────────────────────────────────────────────────

/// <summary>One vault backup (a file or folder copied under <LB>\Saves\<Platform>\).</summary>
internal sealed class VaultEntry
{
    public string GameId { get; set; } = "";
    public string? AppId { get; set; }
    public string GroupId { get; set; } = "";
    public string GroupName { get; set; } = "";      // kept in sync on rename (names orphan groups too)
    public bool IsState { get; set; }
    public int? Slot { get; set; }
    public string VaultPath { get; set; } = "";      // relative to the LB root when under it (portable)
    public string OriginalFileName { get; set; } = "";

    /// <summary>The record's Title, which is what LaunchBox prints as this entry's heading in Backup
    /// History — "Saved Game", "Save State 0"… and anything else, verbatim. Proven free text: a record
    /// planted with Title "Zorglub" is displayed as "Zorglub". So Title is a LABEL that happens to double
    /// as the "this file is in the vault" flag, not a vocabulary to parse.</summary>
    public string Title { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public string Md5 { get; set; } = "";            // file md5, or folder-manifest md5
    public long SizeBytes { get; set; }
    public bool IsDirectory { get; set; }

    /// <summary>Where this entry's record sits among the game's records — its creation order, since
    /// records are appended as backups are taken.
    ///
    /// This is what LaunchBox's retention evicts on, and it is NOT the file's date. Measured twice, each
    /// test pitting it against a criterion it agrees with in normal use:
    ///
    ///   • against the file's mtime — two copies planted with names and dates in opposite order. The one
    ///     whose FILE was newest died, because its RECORD came first.
    ///   • against alphabetical order — after an eviction the freed name is reused by the next backup, so
    ///     the NEWEST copy ends up carrying the suffix-less name. It survived; the oldest record died.
    ///
    /// Our Prune used to sort on CreatedUtc, which is exactly the criterion both tests rule out.</summary>
    public int Ordinal { get; set; }

    /// <summary>Taken out of the rotation: its creation date sits a century ahead, so retention — which
    /// evicts the oldest creation date first — can never reach it. See SaveVault's padlock section.</summary>
    public bool Locked { get; set; }

    /// <summary>The date to SHOW. For a locked copy that is the real one, with the century taken back
    /// off; printing the stored value would put the year 2126 on the card.</summary>
    public DateTime DisplayCreatedUtc => Locked ? CreatedUtc.AddYears(-100) : CreatedUtc;
    // No Auto/manual flag: LaunchBox's format has nowhere to record it and every copy now sits in the
    // same folder, so nothing on disk tells them apart. Prune says what that costs.
}

/// <summary>One save GROUP as shown on the page: the live/active version (from the plugin scan),
/// its persisted <GameSave> record, and its vault backups.</summary>
internal sealed class SaveGroup
{
    public IGame Game = null!;
    public string GameId = "";
    public string? AppId;                            // null → attributed to the game itself
    public bool IsState;
    public int? Slot;
    public string GroupId = "";
    public string GroupName = "";
    public string EmulatorFileName = "";
    public string EmulatorCore = "";

    /// <summary>A short label the plugin attaches to the group, shown as a chip beside its name.
    /// Dolphin sets "Disc Save" on a Wii NAND save.</summary>
    public string ChipText = "";
    public IEmulator? Emulator;                      // the emulator whose scan produced this group
    public EmulatorPlugin? Plugin;                   // …and its integration plugin (used by all actions)
    public GameSaveBase? Active;                     // live scan result; null → record/backups only
    public string ActivePath = "";                   // Active.FileLocation (abs) or the record's FilePath
    public bool ActiveIsDirectory;
    public bool ActiveLive = true;                   // plugin.IsSaveActive
    public bool RecordOnly;                          // record exists but the file is gone (warning)

    /// <summary>This record's file is NOT missing — it is already owned by another group in the same
    /// scan. LaunchBox leaves the old game-attributed record behind when a version starts covering the
    /// game's own ROM (its pass 2 skips a game whose path an app duplicates), so the fossil outlives the
    /// record that replaced it. Reporting that as "the file is gone" is simply false, and the two cases
    /// need different words and different remedies.</summary>
    public bool DuplicateRecord;

    /// <summary>This group has no live save: its record points into the vault, and the file is there.
    /// LaunchBox's "Make New Save" builds one of these, and shows it as "In Vault". It is NOT the same as
    /// RecordOnly, which means the file the record names is gone.</summary>
    public bool InVault;

    /// <summary>The archive entry this save belongs to, or null for the MAIN bucket — the saves named
    /// after the ApplicationPath itself, which is what an un-extracted launch (a core reading the .zip
    /// directly) legitimately produces. Not a fallback: with auto-extract off it is the normal mode.</summary>
    public string? EntryKey;
    /// <summary>The entry's file name, for the picker. Null for the main bucket.</summary>
    public string? EntryLabel;

    /// <summary>The path to hand a plugin so it computes what the emulator actually did for THIS entry —
    /// the extracted file, not the archive. Null for the main bucket.
    ///
    /// The scan already gives it to GetSaves; restoring needs it too, and could not reach it: AddSaveArgs
    /// carries no IGame, so the plugin resolves the game itself and rebuilds the destination from the
    /// archive's path. Carrying it on the group is what lets Restore answer that lookup correctly.</summary>
    public string? EntryProbePath;
    /// <summary>Found by matching an entry name rather than confirmed by a play session — a CANDIDATE.
    /// Session capture is what turns one into a fact; the UI must be able to tell them apart.</summary>
    public bool EntryInferred;
    public List<VaultEntry> Backups = new();

    /// <summary>How many recoverable copies this group has. The number on the card.
    ///
    /// It counts vault copies, and nothing else, because that is what the word means: open Backup
    /// History and you get this many restorable entries.
    ///
    /// LAUNCHBOX COUNTS DIFFERENTLY, and we no longer follow it. Its number is the copies PLUS ONE when
    /// the group is attributed to a version — its own live save is listed as an entry in that case, so a
    /// save that has never been backed up reads "1 Backup" with a green tick. Measured over eight groups,
    /// one variable at a time, with the control that separates attribution from the mere existence of
    /// versions: point the version at a different ROM and the count drops back to 0.
    ///
    /// We reproduced that for a while, on the argument that parity beats correctness for a number the
    /// user compares across both frontends. The argument does not hold, for two reasons:
    ///
    ///   • the number is not persisted anywhere — no field, no [DataTableExport] — so it is computed at
    ///     render time, in our code, in our window. Changing it alters nothing LaunchBox can read.
    ///     Parity is owed to what we WRITE, not to what we DRAW.
    ///   • we copied their number without copying their list. Our Backup History does not show the live
    ///     save as an entry, so the card said N+1 while the panel behind it listed N, and its own header
    ///     said N again. Three numbers for one thing — worse than either choice made consistently.
    ///
    /// So: one rule, applied to the card, the header and the rows alike. A user running both will see
    /// LaunchBox say "1 Backup" where we say "0" — and opening the history explains it immediately.</summary>
    public int DisplayBackupCount => Backups.Count;
    public Dictionary<string, string>? Record;       // persisted row (null for orphan/vault-only groups)

    public DateTime? LastModified
    {
        get
        {
            try
            {
                if (Active?.LastModifiedDateTime is DateTime d) return d;
                if (ActivePath.Length > 0 && File.Exists(ActivePath)) return File.GetLastWriteTime(ActivePath);
                // La date REELLE : une copie verrouillee porte un siecle d'avance, et la carte
                // afficherait 2126 comme date du groupe.
                if (Backups.Count > 0) return Backups.Max(b => b.DisplayCreatedUtc).ToLocalTime();
            }
            catch { }
            return null;
        }
    }

    public long? SizeBytes
    {
        get
        {
            try
            {
                if (Active?.ReportedFileSizeBytes is long r) return r;
                if (ActivePath.Length > 0 && File.Exists(ActivePath)) return new FileInfo(ActivePath).Length;
                if (ActivePath.Length > 0 && Directory.Exists(ActivePath)) return DirSize(ActivePath);
            }
            catch { }
            return null;
        }
    }

    /// <summary>The "No Backup" indicator: an active save with no vault copy, or one modified since the
    /// latest backup (LB's yellow ⚠ on the card).</summary>
    public bool NeedsBackup
    {
        get
        {
            if (Active == null) return false;
            if (Backups.Count == 0) return true;
            try
            {
                // Idem, et ici ca compte plus qu'un affichage : compare a une date de 2126, le
                // mtime de la save vivante ne serait JAMAIS plus recent, et la pastille dirait
                // "sauvegarde a jour" pour toujours des qu'une seule copie du groupe est verrouillee.
                DateTime latest = Backups.Max(b => b.DisplayCreatedUtc);
                DateTime mtime = ActiveIsDirectory ? DirLastWriteUtc(ActivePath) : File.GetLastWriteTimeUtc(ActivePath);
                if (mtime <= latest.AddSeconds(2)) return false;

                // The date says the file was WRITTEN, not that it changed. RetroArch rewrites a .srm
                // when it closes whether or not anything happened, so the mtime moves while the content
                // does not — and the dot warned about a backup that was already byte-identical, with the
                // same hash printed on both lines of Backup History.
                //
                // Compared against the latest copy BY ORDINAL, which is what Backup(force:false) compares
                // against too. That is the definition worth having: the dot is green exactly when pressing
                // Backup Save would create nothing. One hash of one small file, on a page the user opened.
                var newest = Backups.OrderByDescending(b => b.Ordinal).FirstOrDefault();
                if (newest == null) return true;
                string liveHash = ActiveIsDirectory ? SaveManager.DirManifestMd5(ActivePath) : SaveManager.FileMd5(ActivePath);
                if (liveHash.Length == 0) return true;
                string absNewest = SaveVault.Abs(newest);
                string copyHash = newest.IsDirectory ? SaveManager.DirManifestMd5(absNewest) : SaveManager.FileMd5(absNewest);
                return !string.Equals(liveHash, copyHash, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
    }

    private static long DirSize(string dir)
    { try { return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length); } catch { return 0; } }
    private static DateTime DirLastWriteUtc(string dir)
    { try { return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Select(File.GetLastWriteTimeUtc).DefaultIfEmpty(DateTime.MinValue).Max(); } catch { return DateTime.MinValue; } }
}

/// <summary>Result of a page scan: either an error string to display, or the groups.</summary>
internal sealed class SaveScan
{
    public string? Error;
    /// <summary>Primary pair — the GAME's emulator when it has a save-management plugin, else the
    /// first candidate. Used for the Import buttons; per-group actions use the group's own pair.</summary>
    public IEmulator? Emulator;
    public EmulatorPlugin? Plugin;
    /// <summary>Every (emulator, plugin) pair scanned — LB queries ALL integration plugins, not just
    /// the game's assigned emulator (verified against LB 13.27 behaviour).</summary>
    public List<(IEmulator emu, EmulatorPlugin plugin)> Candidates = new();
    /// <summary>Whether the game's OWN emulator has a save-management plugin (drives the empty-page
    /// hint, like LB's "unsupported emulator" message that only shows when nothing was found).</summary>
    public bool GameEmulatorSupported;
    public string GameEmulatorTitle = "";
    public List<SaveGroup> Files = new();
    public List<SaveGroup> States = new();
}

// ── Vault: read from the RECORDS ─────────────────────────────────────────────
//
// Settled by observation on 2026-08-27, and it overturns what every earlier note here said. LaunchBox's
// "Backup History" for a group lists exactly the <GameSave> rows that share its SaveGroupId — and NOT
// the other files sitting in the vault under a matching name. "Secret of Mana (USA)-01.srm" is on disk,
// matches the naming, and LaunchBox does not show it, because nothing recorded it.
//
// So the folder is not an index and never was. A record describes ONE FILE; a group is the set of records
// sharing a SaveGroupId; the ones with a Title live in <LB>\Saves\, the one without lives where the
// emulator put it. Nothing is discovered by listing a directory — not by LaunchBox, and no longer here.
//
// The practical consequence for us: a copy without a record is invisible, so every backup must write its
// own record. That is also why LaunchBox's own library sweep produces copies it cannot show afterwards —
// it writes files without records, and "-01.srm" is one of them.
//
// Naming still follows LaunchBox: the ROM's basename, extension normalised (a state is always ".state",
// whatever slot it came from), then -01, -02… with the real save file name kept in OriginalFileName.

internal static class SaveVault
{
    /// <summary>Raised when a game's backups changed (its Has Saved Game / Has Save States badges).
    /// The vault lives outside GameStore, so the badge engine cannot learn about it any other way.</summary>
    public static event Action<string>? VaultChanged;

    public static void Notify(string? gameId)
    { try { if (!string.IsNullOrEmpty(gameId)) VaultChanged?.Invoke(gameId!); } catch { } }

    /// <summary><LB>\Saves\&lt;Platform&gt; — where LaunchBox puts a platform's backups.</summary>
    public static string PlatformDir(IGame game)
    {
        string plat = "";
        try { plat = game.Platform ?? ""; } catch { }
        if (plat.Length == 0) plat = "Unknown";
        return Path.Combine(SaveManager.LbRoot, "Saves", SaveManager.SanitizeName(plat));
    }

    /// <summary>Where a group's copies live. The platform folder, flat, exactly like LaunchBox.
    /// (The per-archive sub-folder goes here when archive entries come back.)</summary>
    /// <summary>Where this group's copies live. The platform folder for an ordinary save; a sub-folder
    /// named after the ARCHIVE for a save that belongs to one of its entries.
    ///
    /// The sub-folder is what makes N entries of one archive legible: flat, they would all be named after
    /// the archive and only their records would tell them apart. It also puts them out of reach of
    /// LaunchBox's "Clear all and re-scan", which adopts vault files by enumerating the platform folder
    /// FLAT — measured, and the button that turns three groups into thirty.</summary>
    public static string GroupDir(SaveGroup g)
    {
        string plat = PlatformDir(g.Game);
        string? archive = ArchiveFolderName(g);
        return archive == null ? plat : Path.Combine(plat, archive);
    }

    /// <summary>The archive sub-folder's name — the archive's own file name, sanitised — or null when the
    /// group is not an archive entry.</summary>
    public static string? ArchiveFolderName(SaveGroup g)
    {
        if (string.IsNullOrEmpty(g.EntryKey)) return null;
        string rom = SaveManager.OwningRomPath(g) ?? "";
        string name = Path.GetFileName(rom.TrimEnd('\\', '/'));
        return name.Length > 0 ? SaveManager.SanitizeName(name) : null;
    }

    /// <summary>The entry's file name inside the archive, without extension. Read from the label the scan
    /// resolved; falls back to the path carried in the SaveGroupId when the entry is no longer in the
    /// archive — a renamed or rebuilt archive still shows what its copies were named after.</summary>
    public static string? EntryBaseName(SaveGroup g)
    {
        if (string.IsNullOrEmpty(g.EntryKey)) return null;
        string name = g.EntryLabel ?? "";
        if (name.Length == 0)
        {
            int sep = g.EntryKey!.LastIndexOf(':');
            if (sep >= 0 && sep + 1 < g.EntryKey.Length)
                name = Path.GetFileName(g.EntryKey.Substring(sep + 1).Replace('/', '\\'));
        }
        name = Path.GetFileNameWithoutExtension(name);
        return name.Length > 0 ? SaveManager.SanitizeName(name) : null;
    }

    /// <summary>The base name every copy of this group is built from: the ROM's — the game's, or the
    /// owning version's. LaunchBox names a vault copy after the ROM, never after the save file, which is
    /// exactly why OriginalFileName exists.
    /// An archive ENTRY is named after the entry instead. Naming N entries of one archive after the
    /// archive would collapse them into a single name, and the emulator named its save after the entry
    /// in the first place — so this is also the name a restore has to put back.</summary>
    public static string BaseName(SaveGroup g)
    {
        string? entry = EntryBaseName(g);
        if (entry != null) return entry;
        string b = Path.GetFileNameWithoutExtension(SaveManager.OwningRomPath(g) ?? "");
        return b.Length > 0 ? SaveManager.SanitizeName(b) : "save";
    }

    /// <summary>The extension every copy of this group carries. A save state is always ".state" — the
    /// slot lives in the record, not in the vault name (LaunchBox drops it too: a live ".state2" is
    /// copied as ".state").</summary>
    public static string Extension(SaveGroup g)
    {
        if (g.IsState) return ".state";
        var e = Path.GetExtension(g.ActivePath.TrimEnd('\\', '/'));
        return e.Length > 0 ? e : ".sav";
    }

    /// <summary>One entry per vault record of this group, newest first. <paramref name="rows"/> is the
    /// game's full record set; the group's own copies are the ones sharing its SaveGroupId and carrying a
    /// Title.
    ///
    /// The LIVE record is not one of them — it is the card itself, not a copy of it.
    ///
    /// ⚠ LaunchBox counts differently, and the two samples we have do not say which rule it follows: its
    /// "Backup History" for a group with a live file and one vault copy shows BOTH (it says "2 Backups"),
    /// while for a vault-resident group with one record it shows NOTHING. Counting all records gives 2
    /// and 1; counting vault records gives 1 and 0. Neither matches both, so the rule is not determined
    /// yet — a group with a second recorded copy would settle it. We count vault copies, which is what
    /// the word means.</summary>
    public static List<VaultEntry> FromRecords(SaveGroup g, IEnumerable<Dictionary<string, string>> rows)
    {
        var res = new List<VaultEntry>();
        int ordinal = -1;
        foreach (var row in rows)
        {
            ordinal++;                                                                 // position among ALL rows
            if (!string.Equals(row.GetValueOrDefault("SaveGroupId"), g.GroupId, StringComparison.OrdinalIgnoreCase)) continue;
            var abs0 = SaveManager.AbsPath(row.GetValueOrDefault("FilePath") ?? "");
            if (!IsUnderVault(abs0)) continue;                                         // the live file
            var abs = abs0;
            if (abs.Length == 0) continue;
            if (SaveManager.PathEq(abs, g.ActivePath)) continue;                       // the group's own file

            bool isDir = Directory.Exists(abs);
            if (!isDir && !File.Exists(abs)) continue;                                 // recorded but gone

            long size = 0; DateTime when = DateTime.UtcNow;
            try
            {
                // CREATION time, not last-write. For a vault copy the two normally agree -- the file
                // is written once -- but they part company the moment anything touches the content,
                // and it is the CREATION time that retention evicts on. Measured over six purges.
                if (isDir) { when = Directory.GetCreationTimeUtc(abs); size = DirContentSize(abs); }
                else { var fi = new FileInfo(abs); size = fi.Length; when = fi.CreationTimeUtc; }
            }
            catch { }

            res.Add(new VaultEntry
            {
                GameId = g.GameId, AppId = g.AppId, GroupId = g.GroupId, GroupName = g.GroupName,
                IsState = g.IsState, Slot = g.Slot,
                VaultPath = Rel(abs),
                OriginalFileName = row.GetValueOrDefault("OriginalFileName") ?? Path.GetFileName(abs),
                Title = row.GetValueOrDefault("Title") ?? "",
                CreatedUtc = when, SizeBytes = size, IsDirectory = isDir,
                Ordinal = ordinal, Locked = IsLockedPath(abs),
            });
        }
        // Newest first for DISPLAY. Retention must not use this order — see VaultEntry.Ordinal.
        // Tri d'AFFICHAGE, sur la date reelle : sans quoi les copies verrouillees remonteraient
        // toutes en tete de l'historique, un siecle devant les autres.
        res.Sort((x, y) => y.DisplayCreatedUtc.CompareTo(x.DisplayCreatedUtc));
        return res;
    }

    /// <summary>The group's OWN vault file, as an entry that can be restored — or null when the group
    /// has no such file.
    ///
    /// <see cref="FromRecords"/> deliberately skips it, and is right to: for a live group that file IS
    /// the live save, and listing a save as its own backup would be nonsense. But an In Vault group has
    /// no live save. Its record points into the vault, the card shows a real file, and that file is
    /// exactly what "Set as Active" promotes. The same exclusion left Backups empty there, which greyed
    /// the action out — and would have thrown on Backups.First() the moment it was enabled.
    ///
    /// Measured: LaunchBox enables "Set as Active" on precisely these cards. Promoting one moves the
    /// group's identity onto the emulator's file, while the save it displaces keeps its own identity and
    /// follows the copy it is archived into. Groups do not swap contents; they swap which file they
    /// point at.</summary>
    public static VaultEntry? SelfEntry(SaveGroup g)
    {
        if (!g.InVault || g.ActivePath.Length == 0) return null;
        string abs = g.ActivePath;
        bool isDir = Directory.Exists(abs);
        if (!isDir && !File.Exists(abs)) return null;

        long size = 0; DateTime when = DateTime.UtcNow;
        try
        {
            // CREATION time, not last-write. For a vault copy the two normally agree -- the file
            // is written once -- but they part company the moment anything touches the content,
            // and it is the CREATION time that retention evicts on. Measured over six purges.
            if (isDir) { when = Directory.GetCreationTimeUtc(abs); size = DirContentSize(abs); }
            else { var fi = new FileInfo(abs); size = fi.Length; when = fi.CreationTimeUtc; }
        }
        catch { }

        return new VaultEntry
        {
            GameId = g.GameId, AppId = g.AppId, GroupId = g.GroupId, GroupName = g.GroupName,
            IsState = g.IsState, Slot = g.Slot,
            VaultPath = Rel(abs),
            OriginalFileName = g.Record?.GetValueOrDefault("OriginalFileName") ?? Path.GetFileName(abs),
            Title = g.Record?.GetValueOrDefault("Title") ?? "",
            CreatedUtc = when, SizeBytes = size, IsDirectory = isDir,
            Locked = IsLockedPath(abs),
        };
    }

    // ── The padlock ───────────────────────────────────────────────────────────
    //
    // A locked copy is one whose CREATION date has been pushed a century into the future.
    //
    // That sounds like a trick until you see what retention actually does: it deletes by creation date,
    // oldest first — measured, with the three competing criteria separated in one shot (§3.4bis). A copy
    // dated a hundred years out is therefore always the LAST candidate, and it can only be reached once
    // every other copy in the group is locked too.
    //
    // The reason this beats every other carrier we tried:
    //
    //   • it survives a rewrite, unlike a field — nothing unknown lives through LaunchBox's
    //     re-serialisation of a <GameSave>;
    //   • it needs no subfolder, so the vault keeps LaunchBox's flat layout;
    //   • and it protects the copy from LAUNCHBOX'S OWN purge, because LaunchBox sorts on the very same
    //     date. Numbering the file high does not: the name plays no part, and a planted "-1001" was
    //     evicted like any other.
    //
    // The cost is that the date is visible. LaunchBox will print the year 2126 for a locked copy, and so
    // would we if we did not subtract the offset back out for display — which DisplayCreatedUtc does.
    private const int LockOffsetYears = 100;

    /// <summary>Anything created more than half the offset into the future is locked. A margin rather
    /// than an exact match, so a clock skew or a filesystem that rounds does not silently unlock.</summary>
    public static bool IsLockedPath(string abs)
    {
        try
        {
            if (abs.Length == 0) return false;
            DateTime c = Directory.Exists(abs) ? Directory.GetCreationTimeUtc(abs)
                                               : (File.Exists(abs) ? File.GetCreationTimeUtc(abs) : DateTime.MinValue);
            return c > DateTime.UtcNow.AddYears(LockOffsetYears / 2);
        }
        catch { return false; }
    }

    /// <summary>Set or clear the lock. Returns null on success, else a message for the user.</summary>
    public static string? SetLocked(string abs, bool locked)
    {
        try
        {
            bool isDir = Directory.Exists(abs);
            if (!isDir && !File.Exists(abs)) return "That backup no longer exists on disk.";

            DateTime c = isDir ? Directory.GetCreationTimeUtc(abs) : File.GetCreationTimeUtc(abs);
            bool already = c > DateTime.UtcNow.AddYears(LockOffsetYears / 2);
            if (already == locked) return null;

            DateTime moved = locked ? c.AddYears(LockOffsetYears) : c.AddYears(-LockOffsetYears);
            if (isDir) Directory.SetCreationTimeUtc(abs, moved);
            else File.SetCreationTimeUtc(abs, moved);
            return null;
        }
        catch (Exception ex) { return "Could not " + (locked ? "lock" : "unlock") + " this backup:\n" + ex.Message; }
    }

    /// <summary>Bytes in a folder backup, manifest EXCLUDED.
    ///
    /// The entries built from a record only ever set a size for files, so every folder copy — Wii,
    /// GameCube, PS2 — reported 0 B in Backup History while the live save above it showed its real size.
    ///
    /// The manifest is left out on purpose, for the same reason <see cref="DirManifestMd5"/> leaves it
    /// out: it exists only in the copy. Counting it would make a backup read as bigger than the save it
    /// is a copy of, and invite the question of what the extra bytes are.</summary>
    public static long DirContentSize(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                            .Where(f => !IsManifest(f))
                            .Sum(f => new FileInfo(f).Length);
        }
        catch { return 0; }
    }

    public const string ArchiveManifestName = "litebox-archive.xml";

    /// <summary>The two files a vault folder can carry that describe it rather than belong to the save —
    /// LaunchBox's manifest.sha256 and ours. Both must stay out of every hash and every size, or a copy
    /// reads as different from, and bigger than, the save it is a copy of.</summary>
    internal static bool IsManifest(string path)
    {
        var n = Path.GetFileName(path);
        return string.Equals(n, SaveManager.DirManifestName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(n, ArchiveManifestName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Writes the archive folder's manifest — what this folder is, which archive it belongs to,
    /// and which entry each copy was taken from.
    ///
    /// It is DESCRIPTIVE and never read back. That is the point: a second store describing the same files
    /// desynchronises, which is why the old saves-vault.json was deleted. Everything here is already
    /// carried by the records and by the file names; the manifest exists so the folder explains itself to
    /// a human — after LiteBox is uninstalled, or in a backup, or three years from now.
    ///
    /// Same role as the manifest.sha256 LaunchBox drops in a folder backup, and the same reason to keep
    /// it out of every hash and every size: it belongs to the copy, not to the save.</summary>
    public static void WriteArchiveManifest(string dir, IGame game)
    {
        try
        {
            if (!Directory.Exists(dir) || game is not ILiteBoxGame lbg) return;

            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n");
            sb.Append("<LiteBoxArchiveSaves>\r\n");
            sb.Append("  <!-- Written by LiteBox. Descriptive only: nothing reads this file back. -->\r\n");
            sb.Append("  <Game>").Append(Esc(SaveManager.SafeStr(() => game.Title))).Append("</Game>\r\n");
            sb.Append("  <GameId>").Append(Esc(SaveManager.SafeStr(() => game.Id))).Append("</GameId>\r\n");
            sb.Append("  <Platform>").Append(Esc(SaveManager.SafeStr(() => game.Platform))).Append("</Platform>\r\n");
            sb.Append("  <Archive>").Append(Esc(SaveManager.SafeStr(() => game.ApplicationPath))).Append("</Archive>\r\n");

            string full = Path.GetFullPath(dir);
            foreach (var row in lbg.GetSubEntities("GameSave"))
            {
                var fp = row.GetValueOrDefault("FilePath") ?? "";
                var abs = SaveManager.AbsPath(fp);
                if (abs.Length == 0) continue;
                if (!string.Equals(Path.GetDirectoryName(abs), full, StringComparison.OrdinalIgnoreCase)) continue;

                var key = SaveManager.EntryKeyOf(row.GetValueOrDefault("SaveGroupId")) ?? "";
                sb.Append("  <Copy>\r\n");
                sb.Append("    <File>").Append(Esc(Path.GetFileName(abs))).Append("</File>\r\n");
                sb.Append("    <EntryInArchive>").Append(Esc(EntryPathOfKey(key))).Append("</EntryInArchive>\r\n");
                sb.Append("    <Label>").Append(Esc(row.GetValueOrDefault("Title") ?? "")).Append("</Label>\r\n");
                sb.Append("    <SaveName>").Append(Esc(row.GetValueOrDefault("SaveGroupName") ?? "")).Append("</SaveName>\r\n");
                var slot = row.GetValueOrDefault("Slot");
                if (!string.IsNullOrEmpty(slot)) sb.Append("    <Slot>").Append(Esc(slot!)).Append("</Slot>\r\n");
                sb.Append("    <OriginalFileName>").Append(Esc(row.GetValueOrDefault("OriginalFileName") ?? "")).Append("</OriginalFileName>\r\n");
                sb.Append("  </Copy>\r\n");
            }
            sb.Append("</LiteBoxArchiveSaves>\r\n");

            File.WriteAllText(Path.Combine(dir, ArchiveManifestName), sb.ToString(), new UTF8Encoding(false));
        }
        catch { }
    }

    private static string EntryPathOfKey(string key)
    {
        int sep = key.LastIndexOf(':');
        return sep >= 0 && sep + 1 < key.Length ? key.Substring(sep + 1) : key;
    }

    private static string Esc(string v)
        => v.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>Is this path inside &lt;LB&gt;\Saves\ — that is, is it a vault copy rather than a save
    /// sitting where the emulator keeps it?</summary>
    public static bool IsUnderVault(string absPath)
    {
        if (string.IsNullOrEmpty(absPath)) return false;
        try
        {
            string root = Path.GetFullPath(Path.Combine(SaveManager.LbRoot, "Saves")).TrimEnd('\\', '/');
            return Path.GetFullPath(absPath).StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>Absolute path of an entry (VaultPath is stored LB-root-relative when possible).</summary>
    public static string Abs(VaultEntry e)
        => Path.IsPathRooted(e.VaultPath) ? e.VaultPath : Path.GetFullPath(Path.Combine(SaveManager.LbRoot, e.VaultPath));

    public static string Rel(string absPath)
    {
        try
        {
            string root = SaveManager.LbRoot;
            string full = Path.GetFullPath(absPath);
            if (root.Length > 0 && full.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                return full.Substring(root.TrimEnd('\\').Length + 1);
        }
        catch { }
        return absPath;
    }
}


// ── The manager ──────────────────────────────────────────────────────────────

internal static class SaveManager
{
    /// <summary>What LaunchBox writes in &lt;Title&gt; to mark a &lt;GameSave&gt; row as a VAULT BACKUP rather
    /// than an active save. Observed on a real library (2026-08-26) after LB created backups itself: the
    /// row also carries a LB-root-RELATIVE FilePath and the source's OriginalFileName. The earlier RE
    /// concluded LB kept no per-backup record, but it was written against an install that had none.</summary>
    // LaunchBox's Title on a backup row is "Saved Game" for a save file and "Save State <slot>" for a
    // state — recorded here for the day we write LB-compatible backup rows ourselves. Nothing reads a
    // literal: IsBackupRow tests for a Title being present at all, which is what actually discriminates.

    public static string LbRoot => MediaResolver.LbRoot ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));

    // Diagnostic log (GUI has no console): every scan appends here so the real session's behaviour is
    // observable. Path: <LB>\Core\litebox\saves-diag.log.
    private static void Diag(string msg)
    {
        Console.WriteLine("[saves] " + msg);
        try { File.AppendAllText(LiteBoxPaths.File("saves-diag.log"), DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg + "\n"); }
        catch { }
    }

    // ── Plugin-call gate ──────────────────────────────────────────────────
    // Every call into an integration plugin is serialized behind the PROCESS-WIDE EmuPlugins.CallGate
    // (see its doc: the obfuscated core's JIT-hook decryptor is not safe under concurrent first
    // compilations — a background scan racing the UI thread's GetApplicableEmulators can permanently
    // break it for the session). One gate for the whole process, shared with EmuPlugins.
    private static object _pluginGate => EmuPlugins.CallGate;
    /// <summary>Same gate, for the few plugin calls made by the UI layer (e.g. GetPotentialSaveSlots).</summary>
    internal static object PluginGate => _pluginGate;

    // ── Resolution ────────────────────────────────────────────────────────
    // LB does NOT limit the scan to the game's assigned emulator: it queries every emulator that has a
    // save-management integration plugin (each plugin self-filters — Dolphin/PCSX2 only scan games
    // assigned to THEIR emulator, RetroArch scans by save-file name regardless). Verified empirically:
    // a game assigned to a no-plugin emulator still shows its RetroArch saves in LB 13.27.

    /// <summary>All (emulator, plugin) pairs that can answer a save scan for this library.</summary>
    private static List<(IEmulator emu, EmulatorPlugin plugin)> Candidates()
    {
        var list = new List<(IEmulator, EmulatorPlugin)>();
        IEmulator[] emus;
        try { emus = PluginHelper.DataManager?.GetAllEmulators() ?? Array.Empty<IEmulator>(); } catch { emus = Array.Empty<IEmulator>(); }
        foreach (var e in emus)
        {
            if (e == null) continue;
            var p = EmuPlugins.ForEmulator(e);
            if (p == null) continue;
            bool sup = false; try { lock (_pluginGate) sup = p.SupportsSaveManagement(); } catch { }
            if (sup) list.Add((e, p));
        }
        return list;
    }

    private static string EmuAppPath(IEmulator emu)
    {
        string p = ""; try { p = emu.ApplicationPath ?? ""; } catch { }
        try { if (!Path.IsPathRooted(p)) p = Path.GetFullPath(Path.Combine(LbRoot, p)); } catch { }
        return p;
    }

    /// <summary>The integration plugins resolve retroarch.cfg / save dirs from
    /// <c>Path.Combine(Path.GetDirectoryName(emulator.ApplicationPath), …)</c>. When ApplicationPath is
    /// RELATIVE (as stored in Emulators.xml, e.g. "Emulators\RetroArch\retroarch.exe") that path is
    /// resolved against the process CWD — LaunchBox relies on CWD=<LB root>, but under LiteBox a loaded
    /// plugin (ExtendDB) may change the CWD, so GetSaves would silently find nothing. Wrapping the
    /// emulator to expose an ABSOLUTE ApplicationPath makes the plugin CWD-independent (more robust than
    /// LB itself). All other members delegate to the real emulator.</summary>
    private static IEmulator AbsEmu(IEmulator emu)
    {
        string abs = EmuAppPath(emu);
        string cur = ""; try { cur = emu.ApplicationPath ?? ""; } catch { }
        return string.Equals(abs, cur, StringComparison.Ordinal) ? emu : new AbsPathEmulator(emu, abs);
    }

    // ── Scan (BASE game view / one-version view) ──────────────────────────
    // LB semantics: GetSaves is called with the game + ALL its additional apps; the plugin attributes
    // each save to an app (pass 1) or to the game (pass 2, skipped when a twin app shares the game's
    // ApplicationPath). The BASE page shows the saves of the game's own ApplicationPath — i.e. entries
    // with no app, or attributed to the twin app. The VERSION view (Edit Additional Version → Game
    // Saves) is the same scan focused on ONE additional app's path instead of the game's.

    /// <summary>When set, the entry pass probes EVERY listed entry, not only the played ones — the
    /// recovery path for saves made outside LiteBox or before per-entry identity existed. Costs one
    /// plugin call per entry, so it is an explicit user action, never the default on opening a page.</summary>
    public static bool DeepScan;

    /// <summary>Per-ENTRY scanning — handing the plugin an archive entry's path so it derives the name
    /// the emulator actually used.
    ///
    /// It was OFF for the whole parity campaign, deliberately: it is the largest departure from what
    /// LaunchBox does, and it belonged on a base known to be correct rather than mixed into the work that
    /// made it so. That base exists now, so it is on.
    ///
    /// What it turns on, beyond the scan itself: a vault sub-folder per archive, copies named after the
    /// ENTRY rather than the archive (naming N entries after one archive collapses them into a single
    /// name), the "entry:" SaveGroupId — a form LaunchBox is measured to carry without interpreting, as
    /// three of its own plugins already do — the entry picker on the page, and the answer given to a
    /// plugin resolving the game during a restore, which is what stops it writing the save under the
    /// archive's name.</summary>
    public static bool EntryScan = true;

    public static SaveScan ScanBase(IGame game) => Scan(game, null);

    /// <summary>Saves attributed to one additional VERSION (its ApplicationPath) — the per-version
    /// "Game Saves" tab of the Edit Additional Version dialog.</summary>
    public static SaveScan ScanApp(IGame game, IAdditionalApplication focus) => Scan(game, focus);

    private static SaveScan Scan(IGame game, IAdditionalApplication? focus)
    {
        var scan = new SaveScan();
        Diag($"=== Scan{(focus == null ? "Base" : "App \"" + SafeStr(() => focus.Name) + "\"")} \"{SafeStr(() => game.Title)}\" ===  CWD={Try(() => Environment.CurrentDirectory)}  LbRoot={LbRoot}  MediaLbRoot={Try(() => MediaResolver.LbRoot ?? "<null>")}");
        scan.Candidates = Candidates();
        Diag($"candidates={scan.Candidates.Count}: {string.Join(", ", scan.Candidates.Select(c => Try(() => c.emu.Title) + "→" + c.plugin.GetType().Name))}");
        if (scan.Candidates.Count == 0)
        {
            scan.Error = "Save management isn't available: no emulator in this library has a LaunchBox integration plugin\n"
                       + "(RetroArch, Dolphin, PCSX2, …) that supports it.";
            return scan;
        }
        // Primary pair = the game's own emulator when it's a candidate, else the first candidate.
        string gameEmuId = SafeStr(() => game.EmulatorId);
        var primary = scan.Candidates.FirstOrDefault(c => string.Equals(SafeStr(() => c.emu.Id), gameEmuId, StringComparison.OrdinalIgnoreCase));
        scan.GameEmulatorSupported = primary.plugin != null;
        if (primary.plugin == null) primary = scan.Candidates[0];
        scan.Emulator = primary.emu; scan.Plugin = primary.plugin;
        try { scan.GameEmulatorTitle = PluginHelper.DataManager?.GetEmulatorById(gameEmuId)?.Title ?? ""; } catch { }

        string gameAppPath = SafeStr(() => game.ApplicationPath);
        // The path this scan is ABOUT: the game's own (base view) or the focused version's.
        string focusPath = focus == null ? gameAppPath : SafeStr(() => focus.ApplicationPath);
        if (focusPath.Length == 0)
        { scan.Error = focus == null ? "This game has no application path — nothing to scan saves for." : "This version has no ROM file — nothing to scan saves for."; return scan; }

        IAdditionalApplication[] apps;
        try { apps = game.GetAllAdditionalApplications() ?? Array.Empty<IAdditionalApplication>(); }
        catch { apps = Array.Empty<IAdditionalApplication>(); }
        // "Twins" = the apps sharing the focused path (the plugin attributes their saves to the APP,
        // pass 1 wins). In base view the twins are the apps duplicating the game's own path.
        var twinAppIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in apps)
            if (PathEq(SafeStr(() => a.ApplicationPath), focusPath)) { var id = SafeStr(() => a.Id); if (id.Length > 0) twinAppIds.Add(id); }
        // Base view: game-attributed saves + twins. Version view: that version's twins, plus
        // game-attributed saves only when the version shares the game's path.
        bool InBaseView(string? appId) => focus == null
            ? string.IsNullOrEmpty(appId) || twinAppIds.Contains(appId)
            : (!string.IsNullOrEmpty(appId) && twinAppIds.Contains(appId))
              || (string.IsNullOrEmpty(appId) && PathEq(focusPath, gameAppPath));

        // 1. Live scan through EVERY candidate plugin (LB parity). Each plugin self-filters, so a
        //    per-candidate failure only loses that emulator's results, never the whole page.
        var found = new List<(GameSaveBase save, IEmulator emu, EmulatorPlugin plugin)>();
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (cEmu, cPlugin) in scan.Candidates)
        {
            var absEmu = AbsEmu(cEmu);
            try
            {
                // WithCoreShim désactivé : le save-mgmt résout jeux/émulateurs via PluginHelper.DataManager
                // (= HostDataManagerXml, données réelles) et Root.Logging est null-gardé. Le seul accès à
                // Root.DataManager d'un chemin save (RetroArch IsSaturnSaveContext, fallback null-gardé) donne
                // le même résultat que le shim soit vide OU absent → aucun impact. Conservé en commentaire.
                // var resp = EmuInstall.WithCoreShim(() => cPlugin.GetSaves(new GetSavesArgs { Emulator = absEmu, Games = new[] { game }, AdditionalApplications = apps }));
                GetSavesResponse resp;
                lock (_pluginGate)
                    resp = cPlugin.GetSaves(new GetSavesArgs { Emulator = absEmu, Games = new[] { game }, AdditionalApplications = apps });
                int raw = resp?.FoundSaves?.Count ?? -1;
                int kept = 0;
                if (resp?.FoundSaves == null)
                {
                    Diag($"{cPlugin.GetType().Name}.GetSaves(absPath={Try(() => absEmu.ApplicationPath)}): success={resp?.WasSuccess} msg=\"{resp?.Message}\" found=null");
                    continue;
                }
                foreach (var s in resp.FoundSaves)
                {
                    if (s == null || !InBaseView(s.AdditionalApplicationId)) continue;
                    string key = $"{AbsPath(s.FileLocation ?? "")}|{(s as GameSaveState)?.Slot}|{s is GameSaveState}";
                    if (seenKeys.Add(key)) { found.Add((s, cEmu, cPlugin)); kept++; }
                }
                Diag($"{cPlugin.GetType().Name}.GetSaves(absPath={Try(() => absEmu.ApplicationPath)}): raw={raw} keptInBaseView={kept}");
            }
            catch (Exception ex)
            {
                Diag($"{cPlugin.GetType().Name}.GetSaves THREW: {ex.GetType().Name}: {ex.Message}");
                for (var inner = ex.InnerException; inner != null; inner = inner.InnerException)
                    Diag($"    inner: {inner.GetType().Name}: {inner.Message}");
                Diag("    trace: " + (ex.ToString().Length > 1200 ? ex.ToString().Substring(0, 1200) : ex.ToString()));
            }
        }
        // 1b. Per-ENTRY pass. The plugin derives everything from the path it is given, so handing it the
        //     entry's path makes it compute what the emulator actually did. Entries only — the main pass
        //     above already covered the ApplicationPath itself.
        var entries = EntryScan ? SaveEntries.For(game, focus) : new List<SaveEntry>();
        var entryOf = new Dictionary<string, SaveEntry>(StringComparer.OrdinalIgnoreCase);   // save path -> entry
        if (entries.Count > 0)
        {
            // Played entries only by default: a save exists only if something wrote it, and probing an
            // entry costs a plugin call plus a directory read. The rest is a deliberate deep search.
            var probe = entries.Where(e => e.Played).ToList();
            if (probe.Count == 0) probe = entries.Take(DeepScan ? entries.Count : 0).ToList();
            else if (DeepScan) probe = entries;
            Diag($"entries: {entries.Count} listed, {probe.Count} probed (deepScan={DeepScan})");

            foreach (var (cEmu, cPlugin) in scan.Candidates)
            {
                var absEmu = AbsEmu(cEmu);
                foreach (var entry in probe)
                {
                    try
                    {
                        var forged = new EntryGame(game, entry.ProbePath);
                        GetSavesResponse resp;
                        lock (_pluginGate)
                            resp = cPlugin.GetSaves(new GetSavesArgs
                            {
                                Emulator = absEmu,
                                Games = new[] { (IGame)forged },
                                AdditionalApplications = Array.Empty<IAdditionalApplication>(),
                            });
                        if (resp?.FoundSaves == null) continue;
                        foreach (var sv in resp.FoundSaves)
                        {
                            if (sv == null) continue;
                            string key = $"{AbsPath(sv.FileLocation ?? "")}|{(sv as GameSaveState)?.Slot}|{sv is GameSaveState}";
                            // Longest entry name wins: with "Sonic (USA)" and "Sonic (USA) Beta" both
                            // present, the shorter prefix would otherwise claim the longer one's save.
                            var abs2 = AbsPath(sv.FileLocation ?? "");
                            if (entryOf.TryGetValue(abs2, out var already)
                                && Path.GetFileNameWithoutExtension(already.FileName).Length
                                   >= Path.GetFileNameWithoutExtension(entry.FileName).Length)
                                continue;
                            entryOf[abs2] = entry;
                            if (seenKeys.Add(key)) found.Add((sv, cEmu, cPlugin));
                        }
                    }
                    catch (Exception ex) { Diag($"entry \"{entry.FileName}\" scan threw: {ex.Message}"); }
                }
            }
        }

        Diag($"total live saves found = {found.Count}");

        // 2. Persisted <GameSave> records (LB 13.27 schema) for this game, split base-view / others.
        var lbg = game as ILiteBoxGame;
        var allRows = lbg?.GetSubEntities("GameSave").Select(r => new Dictionary<string, string>(r, StringComparer.Ordinal)).ToList()
                      ?? new List<Dictionary<string, string>>();
        var baseRows = allRows.Where(r => InBaseView(r.GetValueOrDefault("AdditionalApplicationId"))).ToList();
        bool rowsDirty = false;

        string primaryEmuFile = Path.GetFileName(EmuAppPath(primary.emu));
        var groups = new List<SaveGroup>();
        var usedRows = new HashSet<Dictionary<string, string>>();

        foreach (var (save, sEmu, sPlugin) in found)
        {
            string abs = AbsPath(save.FileLocation ?? "");
            string sEmuFile = Path.GetFileName(EmuAppPath(sEmu));
            // Match a persisted row: by SaveGroupId when the plugin says so (PCSX2/Saturn), else by path.
            Dictionary<string, string>? row = null;
            bool byGroupId = false;
            try { if (!string.IsNullOrEmpty(save.SaveGroupId)) lock (_pluginGate) byGroupId = sPlugin.UseSaveGroupIdForPersistedMatch(save); } catch { }
            // !IsBackupRow: a backup row carries the SAME SaveGroupId as the active row it belongs to
            // (observed — one backup row per group, pointing at the newest vault copy). Without the
            // guard a group-id match could land on it and rewrite ITS FilePath to the live save's,
            // destroying LaunchBox's record of where the backup is.
            if (byGroupId)
                row = baseRows.FirstOrDefault(r => !usedRows.Contains(r) && !IsVaultRow(r)
                        && string.Equals(r.GetValueOrDefault("SaveGroupId"), save.SaveGroupId, StringComparison.OrdinalIgnoreCase));
            row ??= baseRows.FirstOrDefault(r => !usedRows.Contains(r)
                        && !IsVaultRow(r)
                        && PathEq(AbsPath(r.GetValueOrDefault("FilePath") ?? ""), abs)
                        && SlotOf(r) == (save as GameSaveState)?.Slot);
            if (row == null)
            {
                // First sighting → create the record exactly like LB does (new group + lineage ids).
                // Entry-derived groups key on the entry, not a fresh GUID: SaveGroupId is a field
                // LaunchBox preserves, and the plugins already use it as a namespaced string
                // ("saturn-…", "pcsx2:…"). That is what makes the identity survive a rewrite by LB.
                string gid = entryOf.TryGetValue(abs, out var entForRow)
                    ? entForRow.Key + ((save as GameSaveState)?.Slot is int sl ? ":s" + sl : "")
                    : Guid.NewGuid().ToString("N");
                row = NewRow(game, save, sEmuFile, gid, abs);
                allRows.Add(row); baseRows.Add(row); rowsDirty = true;
            }
            else if (!PathEq(AbsPath(row.GetValueOrDefault("FilePath") ?? ""), abs))
            { row["FilePath"] = abs; rowsDirty = true; }
            usedRows.Add(row);

            var g = new SaveGroup
            {
                Game = game,
                GameId = SafeStr(() => game.Id),
                AppId = string.IsNullOrEmpty(save.AdditionalApplicationId) ? null : save.AdditionalApplicationId,
                IsState = save is GameSaveState,
                Slot = (save as GameSaveState)?.Slot,
                GroupId = row.GetValueOrDefault("SaveGroupId") ?? "",
                GroupName = row.GetValueOrDefault("SaveGroupName") is { Length: > 0 } n ? n : (save.SaveGroupName ?? DefaultName(save, row.GetValueOrDefault("SaveGroupId"))),
                EmulatorFileName = row.GetValueOrDefault("EmulatorFileName") ?? sEmuFile,
                EmulatorCore = row.GetValueOrDefault("EmulatorCore") ?? (save.EmulatorCore ?? ""),
                ChipText = row.GetValueOrDefault("DisplayChipText") ?? (save.DisplayChipText ?? ""),
                Emulator = sEmu,
                Plugin = sPlugin,
                Active = save,
                ActivePath = abs,
                ActiveIsDirectory = save.IsDirectory || Directory.Exists(abs),
                Record = row,
                EntryKey = entryOf.TryGetValue(abs, out var ent) ? ent.Key : null,
                EntryLabel = entryOf.TryGetValue(abs, out var ent2) ? ent2.DisplayName : null,
                EntryProbePath = entryOf.TryGetValue(abs, out var ent3) ? ent3.ProbePath : null,
                EntryInferred = entryOf.ContainsKey(abs),
            };
            try { lock (_pluginGate) g.ActiveLive = sPlugin.IsSaveActive(save, EmuAppPath(sEmu)); } catch { g.ActiveLive = true; }
            groups.Add(g);
        }

        // A group without a live scan result still needs an (emulator, plugin) pair for its actions —
        // pick the candidate whose exe name matches the record, else the primary.
        (IEmulator emu, EmulatorPlugin plugin) PairFor(string? emulatorFileName)
        {
            foreach (var c in scan.Candidates)
                if (string.Equals(Path.GetFileName(EmuAppPath(c.emu)), emulatorFileName, StringComparison.OrdinalIgnoreCase)) return c;
            return primary;
        }

        // 3. Records with no live save behind them. Two very different situations share this branch, and
        //    they used to be reported identically:
        //      • the file really is gone — a warning worth showing, and the group may still hold backups;
        //      • the file is fine but ANOTHER group already claimed it. That happens by construction: once
        //        a version points at the game's own ROM, RetroArch's pass 2 skips the game entirely, so
        //        every save comes back attributed to the version — and LaunchBox never cleans up the old
        //        game-attributed record. Calling that "the file no longer exists" is false, and it made
        //        one save look like two.
        //    LaunchBox's BACKUP rows are excluded from both: they describe a vault copy, not a missing
        //    active save, and step 4 attaches them to their group instead.
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g0 in groups)
            if (g0.ActivePath.Length > 0)
                claimed.Add($"{AbsPath(g0.ActivePath)}|{g0.Slot}|{g0.IsState}");

        // A row whose file is in the VAULT and which no live save matched is not a leftover: it is a
        // group with no live save. LaunchBox's "Make New Save" creates exactly that and shows it as
        // "In Vault". Only rows already covered by a group of the same SaveGroupId are skipped — those
        // are the group's own copies, and step 4 attaches them as backups.
        var liveGroupIds = new HashSet<string>(groups.Select(x => x.GroupId), StringComparer.OrdinalIgnoreCase);

        // ONE group per SaveGroupId, not one per row. A group is the set of records sharing that id, so
        // several rows of the same id must collapse into a single card — as LaunchBox does. This is not
        // theoretical: importing a file writes the record TWICE, identical but for the
        // AdditionalApplicationId, and LaunchBox still shows one card for it.
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in baseRows.Where(r => !usedRows.Contains(r)))
        {
            var rowAbs = AbsPath(row.GetValueOrDefault("FilePath") ?? "");
            bool vaultRow = IsVaultRow(row);
            var rowGid = row.GetValueOrDefault("SaveGroupId") ?? "";
            if (vaultRow && liveGroupIds.Contains(rowGid))
                continue;                                   // a copy of a group already on the page
            if (rowGid.Length > 0 && !emitted.Add(rowGid))
                continue;                                   // same group, already has its card

            bool exists = rowAbs.Length > 0 && (File.Exists(rowAbs) || Directory.Exists(rowAbs));
            if (vaultRow && !exists) continue;              // a copy that is gone — nothing to show

            var pair = PairFor(row.GetValueOrDefault("EmulatorFileName"));
            bool dup = !vaultRow && rowAbs.Length > 0
                       && claimed.Contains($"{rowAbs}|{SlotOf(row)}|{SlotOf(row) != null}");
            groups.Add(new SaveGroup
            {
                DuplicateRecord = dup,
                InVault = vaultRow && exists,
                Game = game,
                GameId = SafeStr(() => game.Id),
                AppId = row.GetValueOrDefault("AdditionalApplicationId") is { Length: > 0 } a ? a : null,
                IsState = SlotOf(row) != null,
                Slot = SlotOf(row),
                GroupId = row.GetValueOrDefault("SaveGroupId") ?? "",
                GroupName = row.GetValueOrDefault("SaveGroupName") ?? DefaultGroupName(SlotOf(row) != null, row.GetValueOrDefault("SaveGroupId")),
                EmulatorFileName = row.GetValueOrDefault("EmulatorFileName") ?? primaryEmuFile,
                EmulatorCore = row.GetValueOrDefault("EmulatorCore") ?? "",
                Emulator = pair.emu,
                Plugin = pair.plugin,
                ActivePath = AbsPath(row.GetValueOrDefault("FilePath") ?? ""),
                RecordOnly = !exists,
                Record = row,
                EntryKey = EntryKeyOf(row.GetValueOrDefault("SaveGroupId")),
                EntryLabel = EntryLabelOf(row.GetValueOrDefault("SaveGroupId"), entries),
                EntryProbePath = EntryProbePathOf(row.GetValueOrDefault("SaveGroupId"), entries),
            });
        }

        // 4. Backups. Derived from the folder, never from an index: LaunchBox keeps none, and a second
        //    store describing the same files is a second thing to keep in sync. Each group asks the vault
        //    for its own copies — the flat platform folder (LaunchBox's), then Manual\ and Auto\ (ours).
        foreach (var g in groups)
        {
            try { g.Backups.AddRange(SaveVault.FromRecords(g, allRows)); }
            catch (Exception ex) { Diag($"vault read \"{g.GroupName}\" failed: {ex.Message}"); }
        }

        foreach (var g in groups) g.Backups.Sort((a, b) => b.DisplayCreatedUtc.CompareTo(a.DisplayCreatedUtc));

        // SetSubEntities is read-only-aware (op-log skipped when the store is ReadOnly) — safe to call.
        if (rowsDirty && lbg != null)
            try { lbg.SetSubEntities("GameSave", allRows); } catch (Exception ex) { Console.WriteLine("[saves] record persist failed: " + ex.Message); }

        scan.Files = groups.Where(x => !x.IsState).OrderBy(x => x.GroupName, StringComparer.OrdinalIgnoreCase).ToList();
        scan.States = groups.Where(x => x.IsState).OrderBy(x => x.Slot ?? int.MaxValue).ToList();
        foreach (var g in scan.Files.Concat(scan.States))
            Diag($"  group \"{g.GroupName}\" active={(g.Active != null)} live={g.ActiveLive} recordOnly={g.RecordOnly} inVault={g.InVault} dup={g.DuplicateRecord} copies={g.Backups.Count} shown={g.DisplayBackupCount} path={g.ActivePath}");
        return scan;
    }

    private static Dictionary<string, string> NewRow(IGame game, GameSaveBase save, string emuFile, string groupId, string absPath)
    {
        // Field order matches LB 13.27's <GameSave> serialisation (see docs/lb-save-management.md).
        var row = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GameId"] = SafeStr(() => game.Id),
        };
        if (!string.IsNullOrEmpty(save.AdditionalApplicationId)) row["AdditionalApplicationId"] = save.AdditionalApplicationId!;
        row["EmulatorFileName"] = save.EmulatorFileName is { Length: > 0 } ef ? ef : emuFile;
        if (!string.IsNullOrEmpty(save.EmulatorCore)) row["EmulatorCore"] = save.EmulatorCore!;
        row["SaveGroupName"] = save.SaveGroupName is { Length: > 0 } n ? n : DefaultName(save, groupId);
        // Supplied by the plugin and persisted by LaunchBox — Dolphin sets "Disc Save" on a Wii NAND
        // group. Long noted here as unused; it is not.
        if (!string.IsNullOrEmpty(save.DisplayChipText)) row["DisplayChipText"] = save.DisplayChipText!;
        row["SaveGroupId"] = save.SaveGroupId is { Length: > 0 } sg ? sg : groupId;
        row["MatchLineageId"] = save.MatchLineageId is { Length: > 0 } ml ? ml : row["SaveGroupId"];
        row["FilePath"] = absPath;
        if (!string.IsNullOrEmpty(save.OriginalFileName)) row["OriginalFileName"] = save.OriginalFileName!;
        if (save is GameSaveState st && st.Slot != null) row["Slot"] = st.Slot.Value.ToString();
        return row;
    }

    /// <summary>The entry identity encoded in a SaveGroupId, or null for a main-bucket group. The ":sN"
    /// slot suffix is stripped — a state's slot is its own field, not part of the entry.</summary>
    internal static string? EntryKeyOf(string? saveGroupId)
    {
        if (string.IsNullOrEmpty(saveGroupId) || !saveGroupId!.StartsWith("entry:", StringComparison.Ordinal))
            return null;
        var id = saveGroupId;

        // "#<guid>" marks a BRANCH: a second group on the same entry, made by Make New Save. It has to be
        // part of the id -- two groups cannot share one -- and it has to be invisible to everything that
        // asks "which ROM is this?", which is every caller here. Stripped before the slot suffix because
        // it is always last.
        int branch = id.IndexOf('#', StringComparison.Ordinal);
        if (branch > "entry:".Length) id = id.Substring(0, branch);

        int slot = id.LastIndexOf(":s", StringComparison.Ordinal);
        return slot > "entry:".Length ? id.Substring(0, slot) : id;
    }

    /// <summary>A group id for a NEW group on the same archive entry. Keeps the entry identity so the ROM
    /// filter still shows it and its copies still land beside their siblings; the suffix is what makes it
    /// a different group.</summary>
    private static string BranchGroupId(string? saveGroupId)
        => EntryKeyOf(saveGroupId) is string k && k.Length > 0
            ? k + "#" + Guid.NewGuid().ToString("N").Substring(0, 8)
            : Guid.NewGuid().ToString("N");

    /// <summary>The display name for an entry-keyed group. Falls back to the path stored in the key when
    /// the archive is no longer listed — a group must stay readable after its archive moved.</summary>
    /// <summary>The probe path of the entry a record belongs to, or null when the entry is not in the
    /// list — an archive that has changed, or a record left by a previous configuration. Null is the
    /// honest answer there: restoring would have to guess a path, and guessing is what put the archive's
    /// name on a save in the first place.</summary>
    private static string? EntryProbePathOf(string? saveGroupId, List<SaveEntry> entries)
    {
        var key = EntryKeyOf(saveGroupId);
        if (key == null) return null;
        return entries.FirstOrDefault(e => string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase))?.ProbePath;
    }

    private static string? EntryLabelOf(string? saveGroupId, List<SaveEntry> entries)
    {
        var key = EntryKeyOf(saveGroupId);
        if (key == null) return null;
        var hit = entries.FirstOrDefault(e => string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase));
        if (hit != null) return hit.DisplayName;
        int sep = key.LastIndexOf(':');
        return sep >= 0 && sep + 1 < key.Length ? Path.GetFileName(key.Substring(sep + 1)) : null;
    }

    /// <summary>A &lt;GameSave&gt; row whose file sits in the VAULT rather than at the emulator's live
    /// location. Decided by the PATH, which is the only thing that actually says where a file is.
    ///
    /// It used to test Title instead, on the observation that every vault row carried one and no live row
    /// did. That correlation held across fifteen records and then broke: "Restore Backup" labels the row
    /// it promotes, so a LIVE record came back carrying Title "Save State 0". Title is a free-text LABEL —
    /// the same thing "Edit Label" edits, and the same field that displayed "Zorglub" verbatim when a
    /// record was planted with it. It says nothing about location.
    ///
    /// The consequence of getting this wrong was not cosmetic: the live save would have been read as a
    /// copy, dropped from the page, and re-recorded under a new group — with the old record left behind
    /// as a phantom.</summary>
    private static bool IsVaultRow(Dictionary<string, string> row)
        => SaveVault.IsUnderVault(AbsPath(row.GetValueOrDefault("FilePath") ?? ""));


    /// <summary>Drops LaunchBox's &lt;GameSave&gt; row for a vault file we just deleted. Without it the row
    /// dangles and LaunchBox keeps offering a backup whose file is gone.</summary>
    private static void RemoveExternalRow(IGame game, string absVaultPath)
    {
        if (game is not ILiteBoxGame lbg) return;
        try
        {
            var rows = lbg.GetSubEntities("GameSave").Select(r => new Dictionary<string, string>(r, StringComparer.Ordinal)).ToList();
            int removed = rows.RemoveAll(r => IsVaultRow(r)
                && PathEq(AbsPath(r.GetValueOrDefault("FilePath") ?? ""), absVaultPath));
            if (removed > 0) lbg.SetSubEntities("GameSave", rows);
        }
        catch (Exception ex) { Console.WriteLine("[saves] external row cleanup failed: " + ex.Message); }
    }

    /// <summary>The entry's file name read out of an "entry:" SaveGroupId — the identity already carries
    /// it, so nothing has to be threaded through to reach it.</summary>
    private static string? EntryFileNameOf(string? saveGroupId)
    {
        var key = EntryKeyOf(saveGroupId);
        if (key == null) return null;
        int sep = key.LastIndexOf(':');
        if (sep < 0 || sep + 1 >= key.Length) return null;
        var name = Path.GetFileName(key.Substring(sep + 1).Replace('/', '\\'));
        return name.Length > 0 ? name : null;
    }

    /// <summary>The name a new group starts with: LaunchBox's "My Save File" / "My Save State", led by
    /// the ROM when the save belongs to an archive entry.
    ///
    /// The prefix is not decoration. LiteBox has an entry picker — LaunchBox has none, so every entry of
    /// an archive lands in one list there, and without it they all read "My Save File" and cannot be told
    /// apart. Leading with the ROM also groups them, since LaunchBox orders Save Files by name.
    ///
    /// A DEFAULT only. The moment the user renames a group the record carries their name, and this is
    /// never consulted for it again.</summary>
    internal static string DefaultGroupName(bool isState, string? saveGroupId)
    {
        string kind = isState ? "My Save State" : "My Save File";
        string rom = Path.GetFileNameWithoutExtension(EntryFileNameOf(saveGroupId) ?? "");
        return rom.Length > 0 ? rom + " \u2014 " + kind : kind;
    }

    private static string DefaultName(GameSaveBase save, string? groupId)
        => DefaultGroupName(save is GameSaveState,
                            save.SaveGroupId is { Length: > 0 } sg ? sg : groupId);
    private static int? SlotOf(Dictionary<string, string> row)
        => int.TryParse(row.GetValueOrDefault("Slot"), out int s) ? s : (int?)null;

    // ── Actions ───────────────────────────────────────────────────────────

    public sealed class BackupResult { public VaultEntry? Entry; public bool Identical; public string? Error; }

    /// <summary>Copies the group's active save into the vault (<LB>\Saves\<Platform>\, LB naming).
    /// Uses the plugin's TryBackupSave for container saves (PS2 memcards, …); plain copy otherwise.
    /// With <paramref name="force"/> false, an unchanged save (same md5 as the latest backup) is
    /// reported as Identical without creating a file.</summary>
    /// <param name="auto">Recorded in the log only, for now. Nothing on disk distinguishes an automatic
    /// copy from one asked for by hand — LaunchBox's format has no field for it. This is the value that
    /// will pick the folder again when the Manual\ / Auto\ split comes back.</param>
    public static BackupResult Backup(SaveGroup g, bool force, bool auto = false)
    {
        var r = new BackupResult();
        if (g.Plugin is not EmulatorPlugin plugin || g.Emulator is not IEmulator emu) { r.Error = "No integration plugin for this save."; return r; }
        if (g.Active == null || g.ActivePath.Length == 0) { r.Error = "No active save file to back up."; return r; }

        string vaultDir = SaveVault.GroupDir(g);
        try { Directory.CreateDirectory(vaultDir); } catch (Exception ex) { r.Error = "Cannot create the vault folder: " + ex.Message; return r; }

        // 1. Container saves (PS2 memcard dirs, …): let the plugin extract into a temp folder.
        string? sourceFile = null, sourceDir = null;
        string tempDir = Path.Combine(Path.GetTempPath(), "litebox-save-" + Guid.NewGuid().ToString("N"));
        try
        {
            bool isContainer = false;
            try { lock (_pluginGate) isContainer = plugin.IsSaveContainer(g.Active); } catch { }
            if (isContainer)
            {
                Directory.CreateDirectory(tempDir);
                bool ok = false; string? err = null;
                try { lock (_pluginGate) ok = plugin.TryBackupSave(g.Active, EmuAppPath(emu), tempDir, out err); } catch (Exception ex) { err = ex.Message; }
                if (!ok) { r.Error = "The plugin could not extract this save: " + (err ?? "unknown error"); return r; }
                sourceDir = tempDir;
            }
            else if (g.ActiveIsDirectory && Directory.Exists(g.ActivePath)) sourceDir = g.ActivePath;
            else if (File.Exists(g.ActivePath)) sourceFile = g.ActivePath;
            else { r.Error = "The active save file no longer exists."; return r; }

            // 2. Signature/md5 → skip identical backups (LB's dirty-check, TryComputeSaveSignature first).
            string md5;
            string? sig = null;
            try { bool okSig; string? sigv; lock (_pluginGate) okSig = plugin.TryComputeSaveSignature(g.Active, EmuAppPath(emu), out sigv); if (okSig && !string.IsNullOrEmpty(sigv)) sig = sigv; } catch { }
            // Two values, deliberately. The plugin's signature (when it offers one) is the better notion
            // of "has this save changed" for a container format, and it is what the entry records — but it
            // is computed from the LIVE save through the plugin, and there is no way to recompute it for a
            // file already sitting in the vault. Comparing a signature against a file hash never matches,
            // so the dirty-check would have said "changed" every single time and piled up a copy per pass,
            // which is precisely the defect we hold against LaunchBox's own sweep.
            string fileHash = sourceFile != null ? FileMd5(sourceFile) : DirManifestMd5(sourceDir!);
            md5 = sig ?? fileHash;

            // The newest copy is re-read rather than cached: the index that used to hold a hash is gone,
            // and this is what LaunchBox does too. Cheap in the case that matters — the file is still in
            // the OS cache moments after the emulator wrote it.
            //
            // "Newest" is the last RECORD, not the newest mtime. Records are appended as copies are made,
            // and the two part company as soon as a file is touched, restored or moved — the same reason
            // Prune stopped ordering on CreatedUtc.
            var latest = g.Backups.OrderByDescending(b => b.Ordinal).FirstOrDefault();
            if (!force && latest != null && fileHash.Length > 0)
            {
                string prev = "";
                try
                {
                    string labs = SaveVault.Abs(latest);
                    prev = latest.IsDirectory ? DirManifestMd5(labs) : (File.Exists(labs) ? FileMd5(labs) : "");
                }
                catch { }
                if (prev.Length > 0 && string.Equals(prev, fileHash, StringComparison.OrdinalIgnoreCase))
                { r.Identical = true; return r; }
            }

            // 3. Copy in, under LaunchBox's naming: the basename of the ROM — the game's, or the owning
            //    version's — with the extension normalised (a state is always ".state", whatever slot it
            //    came from), then -01, -02… The real save file name goes to OriginalFileName, which is the
            //    only thing that lets a restore put it back under the name the emulator reads.
            // Refuse rather than invent. A group whose ROM path has no file name cannot produce a
            // meaningful backup name, and "save.srm" beside real ones is worse than nothing — it is how
            // an unscannable entry leaves a trace that looks legitimate.
            string baseName = SaveVault.BaseName(g);
            if (baseName == "save" && Path.GetFileNameWithoutExtension(OwningRomPath(g) ?? "").Length == 0)
            { r.Error = "This save has no ROM to name a backup after."; return r; }
            string ext = SaveVault.Extension(g);
            string liveName = Path.GetFileName(g.ActivePath.TrimEnd('\\', '/'));
            if (liveName.Length == 0) liveName = baseName + ext;

            var entry = new VaultEntry
            {
                GameId = g.GameId, AppId = g.AppId, GroupId = g.GroupId, GroupName = g.GroupName,
                IsState = g.IsState, Slot = g.Slot,
                OriginalFileName = liveName,
                CreatedUtc = DateTime.UtcNow, Md5 = md5,
            };
            if (sourceFile != null)
            {
                    // Adopt instead of duplicating. LaunchBox's sweep leaves copies in the vault with no
                // record; we cannot see those (records are the index), so we would cheerfully write the
                // same bytes again beside them. If a file we are ABOUT to create is already sitting there
                // under a name we would have used, byte for byte identical, record that one instead.
                //
                // This is narrow on purpose and not "adopting orphans": same folder, same expected naming,
                // same content, AND no record already naming it. Without that last condition it matched
                // the group's own recorded copy and a forced backup produced nothing at all.
                string? twin = FindIdenticalInVault(vaultDir, baseName, ext, md5, RecordedVaultPaths(g.Game));
                string target = twin ?? UniqueFile(vaultDir, baseName, ext);
                if (twin == null) File.Copy(sourceFile, target, overwrite: false);
                else Diag($"adopted an unrecorded vault copy: {Path.GetFileName(twin)}");
                entry.VaultPath = SaveVault.Rel(target);
                entry.SizeBytes = new FileInfo(target).Length;
            }
            else
            {
                string target = UniqueDir(vaultDir, baseName);
                CopyDir(sourceDir!, target);
                WriteDirManifest(target);   // LaunchBox puts one in every folder backup
                entry.VaultPath = SaveVault.Rel(target);
                entry.IsDirectory = true;
                entry.SizeBytes = Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
            }

            Diag($"backup {(auto ? "auto" : "manual")} \"{g.GroupName}\" -> {entry.VaultPath}");
            WriteBackupRow(g, entry);
            // Le dossier d'archive se decrit lui-meme. Apres le record, pour que le manifeste liste la
            // copie qu'on vient d'ecrire.
            if (!string.IsNullOrEmpty(g.EntryKey)) SaveVault.WriteArchiveManifest(vaultDir, g.Game);
            g.Backups.Insert(0, entry);
            SaveVault.Notify(g.GameId);
            r.Entry = entry;
            return r;
        }
        catch (Exception ex) { r.Error = ex.Message; return r; }
        finally { try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { } }
    }

    /// <summary>Writes the group's backup row — LaunchBox's shape exactly: a Title ("Saved Game" for a
    /// save file, "Save State &lt;slot&gt;" for a state), the group's identity, a FilePath RELATIVE to the
    /// LaunchBox root, and OriginalFileName.
    ///
    /// One row per COPY, not per group: LaunchBox's Backup History lists a group's records, so a copy
    /// with no record of its own cannot be shown at all.
    ///
    /// This is called on EVERY backup, including from the library sweep, and that is the one place LiteBox
    /// deliberately does not follow LaunchBox. Measured: LaunchBox's own "Backup Now" copies a save it
    /// discovers into the vault and writes no record for it when the game had none — and a vault file with
    /// no record does not exist for LaunchBox, so the copy can never be listed, counted, or restored. It
    /// is write-only. Worse, the dirty-check compares against the group's newest RECORDED copy, so with
    /// no record there is nothing to compare and every sweep copies the same bytes again.
    ///
    /// That defect lands exactly where an automatic backup matters most: a game you have played but whose
    /// Game Saves page you never opened. Reproducing it would mean writing files the user can neither see
    /// nor recover, so we write the record instead — the same record, in the same format, that LaunchBox
    /// writes for a group it already knows. Nothing about the shape diverges; LaunchBox simply finds more
    /// records than it would have written itself, and reads them normally.
    ///
    /// (The badge engine also needs this: a row is the only signal it can read without touching the disk.
    /// That was the original reason. It is no longer the main one.)</summary>
    private static void WriteBackupRow(SaveGroup g, VaultEntry e)
    {
        if (g.Game is not ILiteBoxGame lbg) return;
        try
        {
            var rows = lbg.GetSubEntities("GameSave").Select(r => new Dictionary<string, string>(r, StringComparer.Ordinal)).ToList();

            // ONE ROW PER COPY. A record describes a file, so a new copy needs its own — reusing the
            // group's previous vault row would leave the older copy on disk with nothing pointing at it,
            // and a copy without a record is invisible. That is exactly how LaunchBox ends up with
            // "-01.srm" sitting in the vault that its own Backup History never shows.
            var row = rows.FirstOrDefault(r => IsVaultRow(r)
                && PathEq(AbsPath(r.GetValueOrDefault("FilePath") ?? ""), SaveVault.Abs(e)));
            bool isNew = row == null;
            row ??= new Dictionary<string, string>(StringComparer.Ordinal);

            row["GameId"] = g.GameId;
            if (!string.IsNullOrEmpty(g.AppId)) row["AdditionalApplicationId"] = g.AppId!;
            row["EmulatorFileName"] = g.EmulatorFileName;
            if (g.EmulatorCore.Length > 0) row["EmulatorCore"] = g.EmulatorCore;
            row["Title"] = g.IsState ? $"Save State {g.Slot ?? 0}" : "Saved Game";
            row["SaveGroupName"] = g.GroupName;
            row["SaveGroupId"] = g.GroupId;
            row["MatchLineageId"] = g.Record?.GetValueOrDefault("MatchLineageId") ?? g.GroupId;
            row["FilePath"] = SaveVault.Rel(SaveVault.Abs(e));   // relative — LaunchBox's form for a backup
            row["OriginalFileName"] = e.OriginalFileName;
            if (g.IsState && g.Slot != null) row["Slot"] = g.Slot.Value.ToString();

            if (isNew) rows.Add(row);
            lbg.SetSubEntities("GameSave", rows);
        }
        catch (Exception ex) { Console.WriteLine("[saves] backup row write failed: " + ex.Message); }
    }

    /// <summary>Restores a vault backup as the ACTIVE save (LB's "Set as Active"): the plugin copies it
    /// to the emulator's live location under the emulator's expected name (AddSaveFile).</summary>
    /// <param name="targetSlot">Where the caller wants the state to land, −1 for Auto. Null keeps the
    /// backup's own slot. LaunchBox always asks and defaults to Auto; passing the backup's slot silently,
    /// as we used to, restores to a slot the user never picked.</param>
    public static string? Restore(SaveGroup g, VaultEntry e, int? targetSlot, Func<bool> confirmOverwrite)
    {
        if (g.Plugin is not EmulatorPlugin plugin) return "No integration plugin for this save.";

        // Take a copy of what is about to be overwritten. Restoring is the one action on this page whose
        // whole point is to replace live progress, and until now it did so with no way back — the plugin
        // copies over the active file and the previous content is simply gone. The dirty-check makes this
        // free when the live save already matches its latest backup.
        //
        // This only covers g's OWN live save. When the file being displaced belongs to another group, the
        // caller archives it first — it is the one holding the scan. See SaveAction_SetActive.
        try { if (g.Active != null) Backup(g, force: false); } catch { }
        string abs = SaveVault.Abs(e);
        if (!File.Exists(abs) && !Directory.Exists(abs)) return "The backup file is missing on disk:\n" + abs;
        int? slot = targetSlot ?? e.Slot;
        GameSaveBase save = e.IsState
            ? new GameSaveState { GameId = g.GameId, AdditionalApplicationId = g.AppId, FileLocation = abs, Slot = slot, SaveGroupId = g.GroupId, SaveGroupName = g.GroupName }
            : new GameSaveGame { GameId = g.GameId, AdditionalApplicationId = g.AppId, FileLocation = abs, SaveGroupId = g.GroupId, SaveGroupName = g.GroupName };
        try
        {
            // For an archive entry, tell the data manager to answer this game's id with the ENTRY's path
            // while the plugin runs. Without it the plugin reads the archive's path and writes the save
            // under the archive's name — the one place the page was outright wrong on extracted ROMs.
            //
            // No probe path means we do not know where the entry was extracted (an archive that changed,
            // a record from another configuration). We let the plugin do what it would have done rather
            // than invent a location: a wrong guess here writes a file the emulator silently ignores.
            IDisposable? scope = null;
            if (!string.IsNullOrEmpty(g.EntryProbePath) && g.GameId.Length > 0)
                try { scope = LbApiHost.Host.Data.HostDataManagerXml.AnswerWithEntryPath(g.GameId, g.EntryProbePath!); } catch { }
            try
            {
                var resp = AddSaveFileCwdSafe(plugin, new AddSaveArgs { SaveToAdd = save, ShouldOverwriteFunc = confirmOverwrite });
                return resp is { WasSuccess: true } ? null : (resp?.Message ?? "The plugin could not restore this save.");
            }
            finally { scope?.Dispose(); }
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>Imports an external file — LaunchBox's "Import Save Game File" / "Import Save State File".
    ///
    /// It does NOT make the file active, and it does not touch the live save at all. Measured: importing
    /// a file called IMPORTME.srm, with content found nowhere else, left the live save byte-identical,
    /// left the source where it was, and produced a NEW GROUP whose file sits in the vault:
    ///
    ///   Saves\&lt;Platform&gt;\Secret of Mana (USA)-02.srm     &lt;- the ROM's basename, next free suffix
    ///   SaveGroupId       0125ff11…                        &lt;- brand new, lineage equal to it
    ///   SaveGroupName     "My Save File"                   &lt;- the default, not the source's name
    ///   OriginalFileName  IMPORTME.srm                     &lt;- the real source name, preserved
    ///   (no Title)
    ///
    /// So it is "Make New Save", seeded from a file you choose instead of from the live save. Making it
    /// active afterwards is a separate step — Restore Backup.
    ///
    /// This used to call AddSaveFile, which copies to the emulator's LIVE location under the ROM's name,
    /// overwriting whatever was there. That is a different operation with a different blast radius, and
    /// the UI compensated by backing every group up first — which is no longer needed, because nothing
    /// gets overwritten.
    ///
    /// Note the record carries no Title while pointing into the vault: one more reason location is read
    /// from the path and never from Title.</summary>
    /// <param name="entry">The archive entry the copy belongs to, or null for the game's own ROM. An
    /// import made while browsing an entry used to land in the MAIN bucket — visible nowhere near where
    /// it was made. The entry decides three things at once: the group's identity, the folder the copy
    /// goes into, and the name it takes.</param>
    public static string? Import(IGame game, string filePath, bool asState, int? slot,
                                 string? appId, string? platformOverride = null, SaveEntry? entry = null)
    {
        if (!File.Exists(filePath)) return "That file no longer exists.";
        if (game is not ILiteBoxGame lbg) return "This library is read-only.";
        try
        {
            string plat = platformOverride ?? SafeStr(() => game.Platform);
            if (plat.Length == 0) plat = "Unknown";
            string dir = Path.Combine(LbRoot, "Saves", Sanitize(plat));

            // The ROM the group is named after: the owning version's when the save belongs to one.
            string rom = SafeStr(() => game.ApplicationPath);
            if (!string.IsNullOrEmpty(appId))
                foreach (var a in game.GetAllAdditionalApplications() ?? Array.Empty<IAdditionalApplication>())
                    if (string.Equals(SafeStr(() => a.Id), appId, StringComparison.OrdinalIgnoreCase))
                    { rom = SafeStr(() => a.ApplicationPath); break; }
            string baseName = Sanitize(Path.GetFileNameWithoutExtension(rom));
            if (baseName.Length == 0) baseName = "save";

            // For an archive entry, everything hangs off the entry instead: its own sub-folder, its own
            // base name. Same layout a backup of that entry would produce, so the copy lands beside its
            // siblings rather than in the platform folder among unrelated saves.
            if (entry != null)
            {
                string archive = Sanitize(Path.GetFileName(rom.TrimEnd('\\', '/')));
                if (archive.Length > 0) dir = Path.Combine(dir, archive);
                string eb = Sanitize(Path.GetFileNameWithoutExtension(entry.FileName));
                if (eb.Length > 0) baseName = eb;
            }
            Directory.CreateDirectory(dir);

            string ext = asState ? ".state" : (Path.GetExtension(filePath) is { Length: > 0 } e ? e : ".srm");
            string target = UniqueFile(dir, baseName, ext);
            File.Copy(filePath, target, overwrite: false);

            // The group's identity. An entry save is keyed by the entry, exactly as a scanned one is —
            // otherwise the imported group would sit in the archive's folder while claiming to belong to
            // nothing, and the entry filter would never show it.
            string gid = entry != null
                ? entry.Key + (asState ? ":s" + (slot ?? 0) : "")
                : Guid.NewGuid().ToString("N");
            var row = new Dictionary<string, string>(StringComparer.Ordinal) { ["GameId"] = SafeStr(() => game.Id) };
            if (!string.IsNullOrEmpty(appId)) row["AdditionalApplicationId"] = appId!;
            row["EmulatorFileName"] = "";
            row["SaveGroupName"] = DefaultGroupName(asState, gid);
            row["SaveGroupId"] = gid;
            row["MatchLineageId"] = gid;
            row["FilePath"] = SaveVault.Rel(target);
            row["OriginalFileName"] = Path.GetFileName(filePath);
            if (asState) row["Slot"] = (slot ?? 0).ToString();

            var rows = lbg.GetSubEntities("GameSave").Select(r => new Dictionary<string, string>(r, StringComparer.Ordinal)).ToList();
            rows.Add(row);
            lbg.SetSubEntities("GameSave", rows);
            if (entry != null) SaveVault.WriteArchiveManifest(dir, game);
            SaveVault.Notify(SafeStr(() => game.Id));
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>AddSaveFile re-resolves the emulator itself via DataManager (relative ApplicationPath),
    /// so — unlike GetSaves where we inject an absolute-path emulator — its retroarch.cfg / save-dir
    /// resolution depends on the process CWD. LaunchBox guarantees CWD=<LB root>; assert it here for the
    /// duration of the call (UI-thread, user-initiated → no concurrent scan), then restore.</summary>
    private static AddSaveResponse AddSaveFileCwdSafe(EmulatorPlugin plugin, AddSaveArgs args)
    {
        string? prev = null;
        try { prev = Environment.CurrentDirectory; } catch { }
        try
        {
            try { if (Directory.Exists(LbRoot)) Environment.CurrentDirectory = LbRoot; } catch { }
            // WithCoreShim désactivé — voir la note sur GetSaves : AddSaveFile ne touche pas Root.DataManager
            // (Dolphin/PCSX2 install-only ; RetroArch AddSaveFile → IsSaturnSaveContext null-gardé). Aucun impact.
            // return EmuInstall.WithCoreShim(() => plugin.AddSaveFile(args));
            lock (_pluginGate) return plugin.AddSaveFile(args);
        }
        finally { try { if (prev != null) Environment.CurrentDirectory = prev; } catch { } }
    }

    /// <summary>Hands the file at <paramref name="absPath"/> over to <paramref name="to"/>.
    ///
    /// The other half of "Set as Active". Restore asks the plugin to write the bytes at the emulator's
    /// location and stops there — but the RECORD of that location still names the group that used to own
    /// it, so the next scan gives the file straight back and nothing appears to have happened. Measured
    /// on LaunchBox: promoting a copy "moves the group's identity onto the emulator's file"; groups do
    /// not swap contents, they swap which file they point at. This is that move.
    ///
    /// MatchLineageId follows the group. NOT measured — the campaign saw it survive a merge and ignore a
    /// rename, never a promotion — but leaving a record whose lineage names a group it no longer belongs
    /// to would be its own kind of wrong, and every other write here keeps the two together.
    ///
    /// A destination nothing records gets a record: an unrecorded live save exists (§2.2), and promoting
    /// onto one must not leave it nameless.</summary>
    internal static string? ReassignRecord(SaveGroup to, string absPath)
    {
        if (to.Game is not ILiteBoxGame lbg) return "This library is read-only.";
        if (string.IsNullOrEmpty(absPath)) return null;
        try
        {
            var rows = lbg.GetSubEntities("GameSave")
                .Select(r => new Dictionary<string, string>(r, StringComparer.Ordinal)).ToList();
            var row = rows.FirstOrDefault(r => PathEq(AbsPath(r.GetValueOrDefault("FilePath") ?? ""), absPath));
            if (row == null)
            {
                row = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["GameId"] = SafeStr(() => to.Game.Id),
                    ["EmulatorFileName"] = to.EmulatorFileName,
                    ["FilePath"] = SaveVault.Rel(absPath),
                    ["OriginalFileName"] = Path.GetFileName(absPath),
                };
                if (to.IsState) row["Slot"] = (to.Slot ?? 0).ToString();
                rows.Add(row);
            }
            if (!string.IsNullOrEmpty(to.AppId)) row["AdditionalApplicationId"] = to.AppId!;
            row["SaveGroupId"] = to.GroupId;
            row["MatchLineageId"] = to.GroupId;
            row["SaveGroupName"] = to.GroupName;
            lbg.SetSubEntities("GameSave", rows);
            SaveVault.Notify(SafeStr(() => to.Game.Id));
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>Sets a copy's label — LaunchBox's "Edit Label", which writes the record's Title.
    ///
    /// LiteBox used to have this, backed by a field in its own index, and it was dropped when that index
    /// went away on the grounds that LaunchBox had nowhere to put a label. It has: Title. The two are the
    /// same feature, and doing it this way means a label set here shows up in LaunchBox as well, which the
    /// old private one never did.</summary>
    public static string? SetBackupLabel(SaveGroup g, VaultEntry e, string label)
    {
        if (g.Game is not ILiteBoxGame lbg) return "This library is read-only.";
        try
        {
            var rows = lbg.GetSubEntities("GameSave").Select(r => new Dictionary<string, string>(r, StringComparer.Ordinal)).ToList();
            var row = rows.FirstOrDefault(r => PathEq(AbsPath(r.GetValueOrDefault("FilePath") ?? ""), SaveVault.Abs(e)));
            if (row == null) return "No record describes this copy — nothing to label.";
            row["Title"] = label ?? "";
            lbg.SetSubEntities("GameSave", rows);
            e.Title = label ?? "";
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>Is this record's file in the vault? Exposed for the maintenance tools.</summary>
    public static bool IsVaultPath(string filePath) => SaveVault.IsUnderVault(AbsPath(filePath));

    /// <summary>Gives a record to each vault file of this game that nothing references, so a copy left
    /// behind becomes visible again. Returns how many were adopted.
    ///
    /// This is LaunchBox's "Added vaulted saves", done without its two defects:
    ///
    ///   • it records an orphan as a COPY of the group it belongs to — Title set, group id shared — not
    ///     as a live save in a brand-new group. LaunchBox's version promotes every archived copy to an
    ///     independent save, which turns a handful of groups with their history into dozens of unrelated
    ///     ones. That is what makes its button destructive despite touching no file.
    ///
    ///   • it never matches on an EMPTY base name. An Additional Application of type Link holds a URL,
    ///     and a URL ending in "/" has no file name — so LaunchBox's "" + "*.*" pattern claims every file
    ///     in the platform's vault folder and hands them to whichever game holds the Link. Measured, with
    ///     a Link created through its own UI pointing at its own website.
    ///
    /// And it stays silent when it cannot be sure: a file is adopted only when EXACTLY ONE group of the
    /// game expects that name. Two save-state slots share a base name and an extension, so a "-01" file
    /// among them is genuinely ambiguous — inventing an owner there would be the same class of mistake,
    /// just quieter.</summary>
    public static int AdoptOrphanCopies(IGame game)
    {
        if (game is not ILiteBoxGame lbg) return 0;
        var scan = ScanBase(game);
        if (scan.Error != null) return 0;

        var groups = scan.Files.Concat(scan.States).Where(g => g.GroupId.Length > 0).ToList();
        if (groups.Count == 0) return 0;

        var rows = lbg.GetSubEntities("GameSave").Select(r => new Dictionary<string, string>(r, StringComparer.Ordinal)).ToList();
        var known = new HashSet<string>(
            rows.Select(r => AbsPath(r.GetValueOrDefault("FilePath") ?? "")).Where(p => p.Length > 0),
            StringComparer.OrdinalIgnoreCase);

        string dir = SaveVault.PlatformDir(game);
        if (!Directory.Exists(dir)) return 0;

        int added = 0;
        foreach (var g in groups)
        {
            string b = SaveVault.BaseName(g), ext = SaveVault.Extension(g);
            if (b.Length == 0 || b == "save") continue;              // the guard LaunchBox lacks
            // Ambiguous when another group would claim the very same names.
            if (groups.Count(o => SaveVault.BaseName(o) == b && SaveVault.Extension(o) == ext) > 1) continue;

            var rx = new Regex("^" + Regex.Escape(b) + @"(-\d+)?" + Regex.Escape(ext) + "$",
                               RegexOptions.IgnoreCase);
            foreach (var f in Directory.EnumerateFileSystemEntries(dir))
            {
                if (!rx.IsMatch(Path.GetFileName(f))) continue;
                var abs = AbsPath(f);
                if (known.Contains(abs) || PathEq(abs, g.ActivePath)) continue;

                var row = new Dictionary<string, string>(StringComparer.Ordinal) { ["GameId"] = g.GameId };
                if (!string.IsNullOrEmpty(g.AppId)) row["AdditionalApplicationId"] = g.AppId!;
                row["EmulatorFileName"] = g.EmulatorFileName;
                if (g.EmulatorCore.Length > 0) row["EmulatorCore"] = g.EmulatorCore;
                row["Title"] = g.IsState ? $"Save State {g.Slot ?? 0}" : "Saved Game";
                row["SaveGroupName"] = g.GroupName;
                row["SaveGroupId"] = g.GroupId;
                row["MatchLineageId"] = g.Record?.GetValueOrDefault("MatchLineageId") ?? g.GroupId;
                row["FilePath"] = SaveVault.Rel(abs);
                row["OriginalFileName"] = Path.GetFileName(abs);
                if (g.IsState && g.Slot != null) row["Slot"] = g.Slot.Value.ToString();
                rows.Add(row);
                known.Add(abs);
                added++;
            }
        }
        if (added > 0) { lbg.SetSubEntities("GameSave", rows); SaveVault.Notify(g0Id(game)); }
        return added;
    }

    private static string g0Id(IGame g) => SafeStr(() => g.Id);

    /// <summary>Renames the group — which means EVERY record sharing its SaveGroupId, the live one and
    /// each copy alike. Measured: renaming a group in LaunchBox rewrote SaveGroupName on both of its
    /// records. This used to touch only the group's own record, leaving its copies under the old name.
    ///
    /// <paramref name="activeLabel"/> is LaunchBox's second field ("Enter a label for the active save
    /// file"), which writes the live record's Title. Null leaves it alone — and leaving it alone is what
    /// LaunchBox does when the field is submitted unchanged: its prefill is a display default, not a
    /// stored value, and validating it wrote nothing.</summary>
    public static void Rename(SaveGroup g, string newName, string? activeLabel = null)
    {
        g.GroupName = newName;
        foreach (var e in g.Backups) e.GroupName = newName;
        if (g.Game is not ILiteBoxGame lbg) return;
        try
        {
            var rows = lbg.GetSubEntities("GameSave").Select(r => new Dictionary<string, string>(r, StringComparer.Ordinal)).ToList();
            bool dirty = false;
            foreach (var row in rows)
            {
                if (!string.Equals(row.GetValueOrDefault("SaveGroupId"), g.GroupId, StringComparison.OrdinalIgnoreCase)) continue;
                if (row.GetValueOrDefault("SaveGroupName") != newName) { row["SaveGroupName"] = newName; dirty = true; }
                if (activeLabel != null && !IsVaultRow(row) && row.GetValueOrDefault("Title") != activeLabel)
                { row["Title"] = activeLabel; dirty = true; }
            }
            if (dirty) { lbg.SetSubEntities("GameSave", rows); g.Record = rows.FirstOrDefault(r => r == g.Record) ?? g.Record; }
        }
        catch (Exception ex) { Console.WriteLine("[saves] rename failed: " + ex.Message); }
    }

    /// <summary>Deletes the group: active file via plugin.RemoveSave (container-aware), its record,
    /// and (optionally) its vault backups. LB warns that this deletes the real files — so do we.</summary>
    public static string? Delete(SaveGroup g, bool alsoBackups)
    {
        if (g.Active != null)
        {
            if (g.Plugin is not EmulatorPlugin plugin) return "No integration plugin for this save.";
            try
            {
                PluginResponse resp;
                lock (_pluginGate) resp = plugin.RemoveSave(g.Active);
                if (resp is { WasSuccess: false }) return resp.Message ?? "The plugin could not delete the save file.";
            }
            catch (Exception ex) { return ex.Message; }
        }
        RemoveRow(g);
        if (alsoBackups) DeleteBackupsOf(g);
        return null;
    }

    /// <summary>LB's "Make New Save": archive the current active into the vault, then remove the live
    /// file so the emulator starts a FRESH save. The old history stays as a vault-only group.</summary>
    /// <summary>LaunchBox's "Make New Save": a NEW group, seeded with a copy of this group's save.
    ///
    /// Measured 2026-08-28 (§4.1bis): the seed is the save of the group whose menu was used — not the
    /// most recent, not the first listed — the live save is NOT touched, the new group gets a fresh
    /// SaveGroupId and, uniquely, a MatchLineageId that differs from it, and the copy is marked
    /// "-NewSave-&lt;guid&gt;" in OriginalFileName.
    ///
    /// This used to archive the live save and then call plugin.RemoveSave to DELETE it, so the emulator
    /// would start fresh. Same name, opposite act: theirs branches, ours destroyed. Nobody asked for a
    /// destructive Make New Save, and the history it left behind was no consolation.
    ///
    /// One deliberate divergence. On a ROM extracted from an archive we KEEP the entry: the copy takes
    /// the entry's basename, lands in the archive's sub-folder, and the new group id stays entry-keyed
    /// (with a "#branch" suffix) so the per-ROM filter still shows it. LaunchBox cannot do this — it has
    /// no notion of entries, and its own Make New Save drops the attachment, naming the copy after the
    /// archive and filing it in the platform folder. Losing the entry here would undo §4.5 entirely.</summary>
    /// <param name="name">The new group's name, as asked for in the dialog.</param>
    public static string? MakeNewSave(SaveGroup g, string name)
    {
        if (g.Game is not ILiteBoxGame lbg) return "This library is read-only.";

        // The seed: the live save when there is one, else the group's own vault file (an In Vault group
        // has no live save, and LaunchBox offers Make New Save there too).
        string seed = g.ActivePath;
        if (seed.Length == 0 || (!File.Exists(seed) && !Directory.Exists(seed)))
            seed = g.Backups.Count > 0 ? SaveVault.Abs(g.Backups[0]) : "";
        if (seed.Length == 0 || !File.Exists(seed)) return "There is no save to seed a new one from.";

        try
        {
            string dir = SaveVault.GroupDir(g);
            Directory.CreateDirectory(dir);

            string baseName = SaveVault.BaseName(g);
            string ext = Path.GetExtension(seed) is { Length: > 0 } e ? e : SaveVault.Extension(g);
            string target = UniqueFile(dir, baseName, ext);
            File.Copy(seed, target, overwrite: false);

            string gid = BranchGroupId(g.GroupId);
            var row = new Dictionary<string, string>(StringComparer.Ordinal) { ["GameId"] = SafeStr(() => g.Game.Id) };
            if (!string.IsNullOrEmpty(g.AppId)) row["AdditionalApplicationId"] = g.AppId!;
            row["EmulatorFileName"] = g.EmulatorFileName;
            row["SaveGroupName"] = string.IsNullOrWhiteSpace(name) ? DefaultGroupName(g.IsState, gid) : name.Trim();
            row["SaveGroupId"] = gid;
            // A lineage of its own, which is what LaunchBox writes here and nowhere else (§1.4).
            row["MatchLineageId"] = Guid.NewGuid().ToString("N");
            row["FilePath"] = SaveVault.Rel(target);
            row["OriginalFileName"] = Path.GetFileNameWithoutExtension(target)
                                    + "-NewSave-" + Guid.NewGuid().ToString("N") + ext;
            row["Title"] = g.IsState ? "Save State " + (g.Slot ?? 0) : "Saved Game";
            if (g.IsState) row["Slot"] = (g.Slot ?? 0).ToString();

            var rows = lbg.GetSubEntities("GameSave")
                .Select(r => new Dictionary<string, string>(r, StringComparer.Ordinal)).ToList();
            rows.Add(row);
            lbg.SetSubEntities("GameSave", rows);
            SaveVault.Notify(SafeStr(() => g.Game.Id));
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>Merges <paramref name="src"/>'s history into <paramref name="dst"/> (LB's "Combine With
    /// Another Save"). The source's vault entries are re-tagged; a still-active source save is first
    /// backed up into the destination group and then removed from disk.</summary>
    /// <summary>Merges two groups into one. Measured against LaunchBox, and it is a pure RE-LABELLING:
    ///
    ///   • every record of the source takes the destination's SaveGroupId AND its MatchLineageId;
    ///   • every record of the resulting group takes the SOURCE's SaveGroupName — the name you were
    ///     looking at when you asked for the merge is the one that survives;
    ///   • nothing on disk moves, is copied, or is deleted.
    ///
    /// This used to do something else entirely: it archived the source's live save into the destination —
    /// creating a file — and then called RemoveSave on it, destroying the live save, before dropping the
    /// source record. Merging two groups is a bookkeeping operation; it has no business deleting a save.
    ///
    /// It is also the one place MatchLineageId has been seen to earn its name: the lineage follows the
    /// merge, so a record carries the identity of the group it now belongs to rather than the one it was
    /// created in.</summary>
    public static string? Combine(SaveGroup src, SaveGroup dst)
    {
        if (ReferenceEquals(src, dst) || string.Equals(src.GroupId, dst.GroupId, StringComparison.OrdinalIgnoreCase))
            return "Cannot combine a save with itself.";
        if (src.Game is not ILiteBoxGame lbg) return "This library is read-only.";
        try
        {
            var rows = lbg.GetSubEntities("GameSave").Select(r => new Dictionary<string, string>(r, StringComparer.Ordinal)).ToList();
            string keepName = src.GroupName;
            string dstLineage = dst.Record?.GetValueOrDefault("MatchLineageId") ?? dst.GroupId;

            foreach (var row in rows)
            {
                var gid = row.GetValueOrDefault("SaveGroupId") ?? "";
                bool isSrc = string.Equals(gid, src.GroupId, StringComparison.OrdinalIgnoreCase);
                bool isDst = string.Equals(gid, dst.GroupId, StringComparison.OrdinalIgnoreCase);
                if (!isSrc && !isDst) continue;
                if (isSrc) { row["SaveGroupId"] = dst.GroupId; row["MatchLineageId"] = dstLineage; }
                row["SaveGroupName"] = keepName;
            }
            lbg.SetSubEntities("GameSave", rows);

            foreach (var e in src.Backups) { e.GroupId = dst.GroupId; e.GroupName = keepName; }
            foreach (var e in dst.Backups) e.GroupName = keepName;
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>Deletes one vault version. <paramref name="owner"/> is only needed for a backup that is
    /// LaunchBox's (External): its record lives in the platform XML, and leaving it behind would have
    /// LaunchBox keep offering a backup whose file no longer exists.</summary>
    public static string? DeleteBackup(VaultEntry e, IGame? owner = null)
    {
        try
        {
            string abs = SaveVault.Abs(e);
            if (e.IsDirectory) { if (Directory.Exists(abs)) Directory.Delete(abs, recursive: true); }
            else if (File.Exists(abs)) File.Delete(abs);
            // The group's backup row points at ONE copy. If it was this one, the row now dangles —
            // both frontends would keep offering a backup whose file is gone.
            if (owner != null) RemoveExternalRow(owner, abs);
            SaveVault.Notify(e.GameId);
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    private static void DeleteBackupsOf(SaveGroup g)
    {
        foreach (var e in g.Backups.ToList()) DeleteBackup(e, g.Game);
        g.Backups.Clear();
    }

    // ── Record persistence helpers ───────────────────────────────────────

    private static void PersistRows(SaveGroup g)
    {
        if (g.Game is not ILiteBoxGame lbg) return;
        try
        {
            var rows = lbg.GetSubEntities("GameSave").Select(r => new Dictionary<string, string>(r, StringComparer.Ordinal)).ToList();
            var mine = rows.FirstOrDefault(r => string.Equals(r.GetValueOrDefault("SaveGroupId"), g.GroupId, StringComparison.OrdinalIgnoreCase));
            if (mine != null && g.Record != null) { rows[rows.IndexOf(mine)] = g.Record; }
            else if (g.Record != null) rows.Add(g.Record);
            lbg.SetSubEntities("GameSave", rows);
        }
        catch (Exception ex) { Console.WriteLine("[saves] record persist failed: " + ex.Message); }
    }

    private static void RemoveRow(SaveGroup g)
    {
        g.Record = null; g.Active = null; g.RecordOnly = false;
        if (g.Game is not ILiteBoxGame lbg) return;
        try
        {
            var rows = lbg.GetSubEntities("GameSave")
                .Select(r => new Dictionary<string, string>(r, StringComparer.Ordinal))
                .Where(r => !string.Equals(r.GetValueOrDefault("SaveGroupId"), g.GroupId, StringComparison.OrdinalIgnoreCase))
                .ToList();
            lbg.SetSubEntities("GameSave", rows);
        }
        catch (Exception ex) { Console.WriteLine("[saves] record remove failed: " + ex.Message); }
    }

    // ── Small utils ───────────────────────────────────────────────────────

    internal static string SafeStr(Func<string?> f) { try { return f() ?? ""; } catch { return ""; } }
    private static string Try(Func<string?> f) { try { return f() ?? ""; } catch (Exception ex) { return "<threw:" + ex.Message + ">"; } }

    public static string AbsPath(string p)
    {
        if (string.IsNullOrEmpty(p)) return "";
        try { return Path.IsPathRooted(p) ? Path.GetFullPath(p) : Path.GetFullPath(Path.Combine(LbRoot, p)); } catch { return p; }
    }

    public static bool PathEq(string a, string b)
        => string.Equals(AbsPath(a).TrimEnd('\\', '/'), AbsPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>The ROM this group's backups are named after: the owning version's path when the save
    /// belongs to one, the game's otherwise. LaunchBox names every vault copy from this, not from the
    /// save file — which is why OriginalFileName exists.</summary>
    public static string? OwningRomPath(SaveGroup g)
    {
        try
        {
            if (!string.IsNullOrEmpty(g.AppId))
                foreach (var a in g.Game.GetAllAdditionalApplications() ?? Array.Empty<IAdditionalApplication>())
                    if (string.Equals(SafeStr(() => a.Id), g.AppId, StringComparison.OrdinalIgnoreCase))
                        return SafeStr(() => a.ApplicationPath);
            return SafeStr(() => g.Game.ApplicationPath);
        }
        catch { return null; }
    }

    public static string SafeId(IGame? game) => game == null ? "" : SafeStr(() => game.Id);

    public static string SanitizeName(string name) => Sanitize(name);

    private static string Sanitize(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Trim();
    }

    /// <summary>A file already in the vault, under a name this group would have used, whose content is
    /// the one we are about to write. Null when there is none — which is the normal case.</summary>
    /// <summary>A vault file that is byte-identical to what we are about to write, sits under a name this
    /// group would have used, AND that no record already names.
    ///
    /// That last condition is the whole point and it used to be missing. The purpose is to reclaim the
    /// copies LaunchBox's sweep leaves behind with no record — invisible files we would otherwise
    /// duplicate. A file that already HAS a record is not one of those: adopting it means rewriting a row
    /// that already exists, creating nothing.
    ///
    /// Which is exactly what a forced "Backup Save" did. The user answers "the current save is identical
    /// to its latest backup — create another copy anyway?" with yes, and the group's own recorded copy
    /// matched, so the request was swallowed: no file, no new row, and the card unchanged. The dialog
    /// asked a question whose answer was then ignored.</summary>
    private static string? FindIdenticalInVault(string dir, string baseName, string ext, string md5,
                                                HashSet<string> recorded)
    {
        if (md5.Length == 0 || !Directory.Exists(dir)) return null;
        try
        {
            var rx = new System.Text.RegularExpressions.Regex(
                "^" + System.Text.RegularExpressions.Regex.Escape(baseName) + @"(-\d+)?"
                    + System.Text.RegularExpressions.Regex.Escape(ext) + "$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            foreach (var f in Directory.EnumerateFiles(dir))
                if (rx.IsMatch(Path.GetFileName(f))
                    && !recorded.Contains(AbsPath(f))
                    && string.Equals(FileMd5(f), md5, StringComparison.OrdinalIgnoreCase))
                    return f;
        }
        catch { }
        return null;
    }

    /// <summary>Every vault path this game already has a record for — the set an adoption must not touch.
    /// Read from the records because they are the index: a file nothing names is precisely what we are
    /// looking to reclaim.</summary>
    private static HashSet<string> RecordedVaultPaths(IGame game)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (game is not ILiteBoxGame lbg) return set;
        try
        {
            foreach (var r in lbg.GetSubEntities("GameSave"))
            {
                var abs = AbsPath(r.GetValueOrDefault("FilePath") ?? "");
                if (abs.Length > 0) set.Add(abs);
            }
        }
        catch { }
        return set;
    }

    private static string UniqueFile(string dir, string baseName, string ext)
    {
        string p = Path.Combine(dir, baseName + ext);
        if (!File.Exists(p) && !Directory.Exists(p)) return p;
        for (int i = 1; ; i++)
        {
            p = Path.Combine(dir, $"{baseName}-{i:00}{ext}");
            if (!File.Exists(p) && !Directory.Exists(p)) return p;
        }
    }

    private static string UniqueDir(string dir, string baseName)
    {
        string p = Path.Combine(dir, baseName);
        if (!Directory.Exists(p) && !File.Exists(p)) return p;
        for (int i = 1; ; i++)
        {
            p = Path.Combine(dir, $"{baseName}-{i:00}");
            if (!Directory.Exists(p) && !File.Exists(p)) return p;
        }
    }

    private static void CopyDir(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (string f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(src, f);
            string target = Path.Combine(dst, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(f, target, overwrite: true);
        }
    }

    public static string FileMd5(string path)
    {
        try
        {
            using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return Convert.ToHexString(MD5.HashData(s));
        }
        catch { return ""; }
    }

    /// <summary>The file LaunchBox drops inside every FOLDER backup: one "name|SHA256" line per file,
    /// uppercase, sorted, CRLF, no BOM. Measured on a Wii NAND backup made by LaunchBox itself.</summary>
    public const string DirManifestName = "manifest.sha256";

    private static void WriteDirManifest(string dir)
    {
        try
        {
            var sb = new StringBuilder();
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                                       .Where(f => !string.Equals(Path.GetFileName(f), DirManifestName, StringComparison.OrdinalIgnoreCase))
                                       .OrderBy(f => Path.GetRelativePath(dir, f), StringComparer.Ordinal))
            {
                using var st = File.OpenRead(f);
                sb.Append(Path.GetRelativePath(dir, f).Replace('\\', '/'))
                  .Append('|').Append(Convert.ToHexString(SHA256.HashData(st))).Append("\r\n");
            }
            File.WriteAllText(Path.Combine(dir, DirManifestName), sb.ToString(), new UTF8Encoding(false));
        }
        catch (Exception ex) { Console.WriteLine("[saves] manifest write failed: " + ex.Message); }
    }

    /// <summary>Folder signature: md5 over "relpath|md5" lines, sorted — same idea as the plugins'
    /// folder manifests (Saturn/PCSX2), so unchanged folders dedupe too.</summary>
    public static string DirManifestMd5(string dir)
    {
        try
        {
            var sb = new StringBuilder();
            // manifest.sha256 is EXCLUDED, and that exclusion is load-bearing. LaunchBox writes it inside
            // the backup folder and never in the live one, so hashing it would make a backup differ from
            // its own source for ever — and the dirty-check would copy the same folder again every pass.
            foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                                          .Where(x => !SaveVault.IsManifest(x))
                                          .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                sb.Append(Path.GetRelativePath(dir, f).ToLowerInvariant()).Append('|').Append(FileMd5(f)).Append('\n');
            }
            return Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
        }
        catch { return ""; }
    }
}
