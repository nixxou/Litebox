// Save management — the HOST side of LaunchBox's save-management feature (Edit Game → Game Saves),
// re-implemented for LiteBox on top of the SAME per-emulator logic LaunchBox uses.
//
// Architecture (mirrors LB 13.27, fully RE'd — see ExtendDB/docs/lb-save-management.md):
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
    public string Label { get; set; } = "";          // user label for this version ("Before Final Boss")
    public DateTime CreatedUtc { get; set; }
    public string Md5 { get; set; } = "";            // file md5, or folder-manifest md5
    public long SizeBytes { get; set; }
    public bool IsDirectory { get; set; }
    public bool Auto { get; set; }                   // created by an automatic backup (future)
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
    public IEmulator? Emulator;                      // the emulator whose scan produced this group
    public EmulatorPlugin? Plugin;                   // …and its integration plugin (used by all actions)
    public GameSaveBase? Active;                     // live scan result; null → record/backups only
    public string ActivePath = "";                   // Active.FileLocation (abs) or the record's FilePath
    public bool ActiveIsDirectory;
    public bool ActiveLive = true;                   // plugin.IsSaveActive
    public bool RecordOnly;                          // record exists but the file is gone (warning)
    public List<VaultEntry> Backups = new();
    public Dictionary<string, string>? Record;       // persisted row (null for orphan/vault-only groups)

    public DateTime? LastModified
    {
        get
        {
            try
            {
                if (Active?.LastModifiedDateTime is DateTime d) return d;
                if (ActivePath.Length > 0 && File.Exists(ActivePath)) return File.GetLastWriteTime(ActivePath);
                if (Backups.Count > 0) return Backups.Max(b => b.CreatedUtc).ToLocalTime();
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
                DateTime latest = Backups.Max(b => b.CreatedUtc);
                DateTime mtime = ActiveIsDirectory ? DirLastWriteUtc(ActivePath) : File.GetLastWriteTimeUtc(ActivePath);
                return mtime > latest.AddSeconds(2);
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

// ── Vault metadata store (LiteBox-owned JSON) ────────────────────────────────

internal static class SaveVault
{
    private static readonly object _lock = new();
    private static List<VaultEntry>? _entries;
    private static string FilePath => LiteBoxPaths.File("saves-vault.json");

    private static List<VaultEntry> Load()
    {
        lock (_lock)
        {
            if (_entries != null) return _entries;
            try
            {
                if (File.Exists(FilePath))
                    _entries = JsonSerializer.Deserialize<List<VaultEntry>>(File.ReadAllText(FilePath)) ?? new();
            }
            catch (Exception ex) { Console.WriteLine("[saves] vault json unreadable: " + ex.Message); }
            return _entries ??= new List<VaultEntry>();
        }
    }

    private static void Save()
    {
        lock (_lock)
        {
            try
            {
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_entries, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(tmp, FilePath, overwrite: true);
            }
            catch (Exception ex) { Console.WriteLine("[saves] vault json write failed: " + ex.Message); }
        }
    }

    public static List<VaultEntry> ForGame(string gameId)
    { lock (_lock) return Load().Where(e => string.Equals(e.GameId, gameId, StringComparison.OrdinalIgnoreCase)).ToList(); }

    public static void Add(VaultEntry e) { lock (_lock) { Load().Add(e); Save(); } }
    public static void Remove(VaultEntry e) { lock (_lock) { Load().Remove(e); Save(); } }
    public static void Changed() { lock (_lock) { if (_entries != null) Save(); } }

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
    public static string LbRoot => MediaResolver.LbRoot ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));

    // Diagnostic log (GUI has no console): every scan appends here so the real session's behaviour is
    // observable. Path: <LB>\Core\litebox\saves-diag.log.
    private static void Diag(string msg)
    {
        Console.WriteLine("[saves] " + msg);
        try { File.AppendAllText(LiteBoxPaths.File("saves-diag.log"), DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg + "\n"); }
        catch { }
    }

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
            bool sup = false; try { sup = p.SupportsSaveManagement(); } catch { }
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

    // ── Scan (BASE game view) ─────────────────────────────────────────────
    // LB semantics: GetSaves is called with the game + ALL its additional apps; the plugin attributes
    // each save to an app (pass 1) or to the game (pass 2, skipped when a twin app shares the game's
    // ApplicationPath). The BASE page shows the saves of the game's own ApplicationPath — i.e. entries
    // with no app, or attributed to the twin app.

    public static SaveScan ScanBase(IGame game)
    {
        var scan = new SaveScan();
        Diag($"=== ScanBase \"{SafeStr(() => game.Title)}\" ===  CWD={Try(() => Environment.CurrentDirectory)}  LbRoot={LbRoot}  MediaLbRoot={Try(() => MediaResolver.LbRoot ?? "<null>")}");
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
        if (gameAppPath.Length == 0) { scan.Error = "This game has no application path — nothing to scan saves for."; return scan; }

        IAdditionalApplication[] apps;
        try { apps = game.GetAllAdditionalApplications() ?? Array.Empty<IAdditionalApplication>(); }
        catch { apps = Array.Empty<IAdditionalApplication>(); }
        var twinAppIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in apps)
            if (PathEq(SafeStr(() => a.ApplicationPath), gameAppPath)) { var id = SafeStr(() => a.Id); if (id.Length > 0) twinAppIds.Add(id); }
        bool InBaseView(string? appId) => string.IsNullOrEmpty(appId) || twinAppIds.Contains(appId);

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
                var resp = cPlugin.GetSaves(new GetSavesArgs { Emulator = absEmu, Games = new[] { game }, AdditionalApplications = apps });
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
            try { byGroupId = !string.IsNullOrEmpty(save.SaveGroupId) && sPlugin.UseSaveGroupIdForPersistedMatch(save); } catch { }
            if (byGroupId)
                row = baseRows.FirstOrDefault(r => !usedRows.Contains(r) && string.Equals(r.GetValueOrDefault("SaveGroupId"), save.SaveGroupId, StringComparison.OrdinalIgnoreCase));
            row ??= baseRows.FirstOrDefault(r => !usedRows.Contains(r)
                        && PathEq(AbsPath(r.GetValueOrDefault("FilePath") ?? ""), abs)
                        && SlotOf(r) == (save as GameSaveState)?.Slot);
            if (row == null)
            {
                // First sighting → create the record exactly like LB does (new group + lineage ids).
                string gid = Guid.NewGuid().ToString("N");
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
                GroupName = row.GetValueOrDefault("SaveGroupName") is { Length: > 0 } n ? n : (save.SaveGroupName ?? DefaultName(save)),
                EmulatorFileName = row.GetValueOrDefault("EmulatorFileName") ?? sEmuFile,
                EmulatorCore = row.GetValueOrDefault("EmulatorCore") ?? (save.EmulatorCore ?? ""),
                Emulator = sEmu,
                Plugin = sPlugin,
                Active = save,
                ActivePath = abs,
                ActiveIsDirectory = save.IsDirectory || Directory.Exists(abs),
                Record = row,
            };
            try { g.ActiveLive = sPlugin.IsSaveActive(save, EmuAppPath(sEmu)); } catch { g.ActiveLive = true; }
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

        // 3. Records whose file is gone (still shown, with a warning — they may hold vault history).
        foreach (var row in baseRows.Where(r => !usedRows.Contains(r)))
        {
            var pair = PairFor(row.GetValueOrDefault("EmulatorFileName"));
            groups.Add(new SaveGroup
            {
                Game = game,
                GameId = SafeStr(() => game.Id),
                AppId = row.GetValueOrDefault("AdditionalApplicationId") is { Length: > 0 } a ? a : null,
                IsState = SlotOf(row) != null,
                Slot = SlotOf(row),
                GroupId = row.GetValueOrDefault("SaveGroupId") ?? "",
                GroupName = row.GetValueOrDefault("SaveGroupName") ?? "My Save File",
                EmulatorFileName = row.GetValueOrDefault("EmulatorFileName") ?? primaryEmuFile,
                EmulatorCore = row.GetValueOrDefault("EmulatorCore") ?? "",
                Emulator = pair.emu,
                Plugin = pair.plugin,
                ActivePath = AbsPath(row.GetValueOrDefault("FilePath") ?? ""),
                RecordOnly = true,
                Record = row,
            });
        }

        // 4. Vault entries: attach to their group, or surface orphan (vault-only) groups.
        var vault = SaveVault.ForGame(SafeStr(() => game.Id)).Where(e => InBaseView(e.AppId)).ToList();
        foreach (var e in vault)
        {
            var g = groups.FirstOrDefault(x => string.Equals(x.GroupId, e.GroupId, StringComparison.OrdinalIgnoreCase));
            if (g == null)
            {
                g = new SaveGroup
                {
                    Game = game, GameId = e.GameId, AppId = e.AppId,
                    IsState = e.IsState, Slot = e.Slot,
                    GroupId = e.GroupId, GroupName = e.GroupName.Length > 0 ? e.GroupName : "My Save File",
                    EmulatorFileName = primaryEmuFile,
                    Emulator = primary.emu, Plugin = primary.plugin,
                };
                groups.Add(g);
            }
            g.Backups.Add(e);
        }
        foreach (var g in groups) g.Backups.Sort((a, b) => b.CreatedUtc.CompareTo(a.CreatedUtc));

        // SetSubEntities is read-only-aware (op-log skipped when the store is ReadOnly) — safe to call.
        if (rowsDirty && lbg != null)
            try { lbg.SetSubEntities("GameSave", allRows); } catch (Exception ex) { Console.WriteLine("[saves] record persist failed: " + ex.Message); }

        scan.Files = groups.Where(x => !x.IsState).OrderBy(x => x.GroupName, StringComparer.OrdinalIgnoreCase).ToList();
        scan.States = groups.Where(x => x.IsState).OrderBy(x => x.Slot ?? int.MaxValue).ToList();
        foreach (var g in scan.Files.Concat(scan.States))
            Diag($"  group \"{g.GroupName}\" active={(g.Active != null)} live={g.ActiveLive} recordOnly={g.RecordOnly} backups={g.Backups.Count} path={g.ActivePath}");
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
        row["SaveGroupName"] = save.SaveGroupName is { Length: > 0 } n ? n : DefaultName(save);
        row["SaveGroupId"] = save.SaveGroupId is { Length: > 0 } sg ? sg : groupId;
        row["MatchLineageId"] = save.MatchLineageId is { Length: > 0 } ml ? ml : row["SaveGroupId"];
        row["FilePath"] = absPath;
        if (!string.IsNullOrEmpty(save.OriginalFileName)) row["OriginalFileName"] = save.OriginalFileName!;
        if (save is GameSaveState st && st.Slot != null) row["Slot"] = st.Slot.Value.ToString();
        return row;
    }

    private static string DefaultName(GameSaveBase save) => save is GameSaveState ? "My Save State" : "My Save File";
    private static int? SlotOf(Dictionary<string, string> row)
        => int.TryParse(row.GetValueOrDefault("Slot"), out int s) ? s : (int?)null;

    // ── Actions ───────────────────────────────────────────────────────────

    public sealed class BackupResult { public VaultEntry? Entry; public bool Identical; public string? Error; }

    /// <summary>Copies the group's active save into the vault (<LB>\Saves\<Platform>\, LB naming).
    /// Uses the plugin's TryBackupSave for container saves (PS2 memcards, …); plain copy otherwise.
    /// With <paramref name="force"/> false, an unchanged save (same md5 as the latest backup) is
    /// reported as Identical without creating a file.</summary>
    public static BackupResult Backup(SaveGroup g, bool force, bool auto = false)
    {
        var r = new BackupResult();
        if (g.Plugin is not EmulatorPlugin plugin || g.Emulator is not IEmulator emu) { r.Error = "No integration plugin for this save."; return r; }
        if (g.Active == null || g.ActivePath.Length == 0) { r.Error = "No active save file to back up."; return r; }

        string vaultDir = Path.Combine(LbRoot, "Saves", Sanitize(SafeStr(() => g.Game.Platform) is { Length: > 0 } p ? p : "Unknown"));
        try { Directory.CreateDirectory(vaultDir); } catch (Exception ex) { r.Error = "Cannot create the vault folder: " + ex.Message; return r; }

        // 1. Container saves (PS2 memcard dirs, …): let the plugin extract into a temp folder.
        string? sourceFile = null, sourceDir = null;
        string tempDir = Path.Combine(Path.GetTempPath(), "litebox-save-" + Guid.NewGuid().ToString("N"));
        try
        {
            bool isContainer = false;
            try { isContainer = plugin.IsSaveContainer(g.Active); } catch { }
            if (isContainer)
            {
                Directory.CreateDirectory(tempDir);
                bool ok = false; string? err = null;
                try { ok = plugin.TryBackupSave(g.Active, EmuAppPath(emu), tempDir, out err); } catch (Exception ex) { err = ex.Message; }
                if (!ok) { r.Error = "The plugin could not extract this save: " + (err ?? "unknown error"); return r; }
                sourceDir = tempDir;
            }
            else if (g.ActiveIsDirectory && Directory.Exists(g.ActivePath)) sourceDir = g.ActivePath;
            else if (File.Exists(g.ActivePath)) sourceFile = g.ActivePath;
            else { r.Error = "The active save file no longer exists."; return r; }

            // 2. Signature/md5 → skip identical backups (LB's dirty-check, TryComputeSaveSignature first).
            string md5;
            string? sig = null;
            try { if (plugin.TryComputeSaveSignature(g.Active, EmuAppPath(emu), out var s) && !string.IsNullOrEmpty(s)) sig = s; } catch { }
            md5 = sig ?? (sourceFile != null ? FileMd5(sourceFile) : DirManifestMd5(sourceDir!));
            var latest = g.Backups.OrderByDescending(b => b.CreatedUtc).FirstOrDefault();
            if (!force && latest != null && md5.Length > 0 && string.Equals(latest.Md5, md5, StringComparison.OrdinalIgnoreCase))
            { r.Identical = true; return r; }

            // 3. Copy into the vault under LB's naming (first backup keeps the plain name, then -01, -02…;
            //    also embed the group's short id when different groups share a file name).
            string baseName = g.Active.OriginalFileName is { Length: > 0 } ofn ? ofn : Path.GetFileName(g.ActivePath.TrimEnd('\\', '/'));
            if (string.IsNullOrEmpty(baseName)) baseName = "save";
            var entry = new VaultEntry
            {
                GameId = g.GameId, AppId = g.AppId, GroupId = g.GroupId, GroupName = g.GroupName,
                IsState = g.IsState, Slot = g.Slot,
                OriginalFileName = baseName,
                CreatedUtc = DateTime.UtcNow, Md5 = md5, Auto = auto,
            };
            if (sourceFile != null)
            {
                string target = UniqueFile(vaultDir, Path.GetFileNameWithoutExtension(baseName), Path.GetExtension(baseName));
                File.Copy(sourceFile, target, overwrite: false);
                entry.VaultPath = SaveVault.Rel(target);
                entry.SizeBytes = new FileInfo(target).Length;
            }
            else
            {
                string target = UniqueDir(vaultDir, Path.GetFileNameWithoutExtension(baseName));
                CopyDir(sourceDir!, target);
                entry.VaultPath = SaveVault.Rel(target);
                entry.IsDirectory = true;
                entry.SizeBytes = Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
            }
            SaveVault.Add(entry);
            g.Backups.Insert(0, entry);
            r.Entry = entry;
            return r;
        }
        catch (Exception ex) { r.Error = ex.Message; return r; }
        finally { try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { } }
    }

    /// <summary>Restores a vault backup as the ACTIVE save (LB's "Set as Active"): the plugin copies it
    /// to the emulator's live location under the emulator's expected name (AddSaveFile).</summary>
    public static string? Restore(SaveGroup g, VaultEntry e, Func<bool> confirmOverwrite)
    {
        if (g.Plugin is not EmulatorPlugin plugin) return "No integration plugin for this save.";
        string abs = SaveVault.Abs(e);
        if (!File.Exists(abs) && !Directory.Exists(abs)) return "The backup file is missing on disk:\n" + abs;
        GameSaveBase save = e.IsState
            ? new GameSaveState { GameId = g.GameId, AdditionalApplicationId = g.AppId, FileLocation = abs, Slot = e.Slot, SaveGroupId = g.GroupId, SaveGroupName = g.GroupName }
            : new GameSaveGame { GameId = g.GameId, AdditionalApplicationId = g.AppId, FileLocation = abs, SaveGroupId = g.GroupId, SaveGroupName = g.GroupName };
        try
        {
            var resp = AddSaveFileCwdSafe(plugin, new AddSaveArgs { SaveToAdd = save, ShouldOverwriteFunc = confirmOverwrite });
            return resp is { WasSuccess: true } ? null : (resp?.Message ?? "The plugin could not restore this save.");
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>Imports an external save/state file as the active save (LB's Import buttons).</summary>
    public static string? Import(IGame game, EmulatorPlugin plugin, string filePath, bool asState, int? slot, Func<bool> confirmOverwrite)
    {
        GameSaveBase save = asState
            ? new GameSaveState { GameId = SafeStr(() => game.Id), FileLocation = filePath, Slot = slot }
            : new GameSaveGame { GameId = SafeStr(() => game.Id), FileLocation = filePath };
        try
        {
            var resp = AddSaveFileCwdSafe(plugin, new AddSaveArgs { SaveToAdd = save, ShouldOverwriteFunc = confirmOverwrite });
            return resp is { WasSuccess: true } ? null : (resp?.Message ?? "The plugin could not import this file.");
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
            return plugin.AddSaveFile(args);
        }
        finally { try { if (prev != null) Environment.CurrentDirectory = prev; } catch { } }
    }

    /// <summary>Renames the group (SaveGroupName) — persisted record + vault entries stay in sync.</summary>
    public static void Rename(SaveGroup g, string newName)
    {
        g.GroupName = newName;
        if (g.Record != null) { g.Record["SaveGroupName"] = newName; PersistRows(g); }
        bool changed = false;
        foreach (var e in g.Backups) if (e.GroupName != newName) { e.GroupName = newName; changed = true; }
        if (changed) SaveVault.Changed();
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
                var resp = plugin.RemoveSave(g.Active);
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
    public static string? MakeNewSave(SaveGroup g)
    {
        if (g.Active == null) return "There is no active save to archive.";
        if (g.Plugin is not EmulatorPlugin plugin) return "No integration plugin for this save.";
        var b = Backup(g, force: false);
        if (b.Error != null) return "Backup failed — nothing was changed: " + b.Error;
        try
        {
            var resp = plugin.RemoveSave(g.Active);
            if (resp is { WasSuccess: false }) return resp.Message ?? "The plugin could not remove the active save.";
        }
        catch (Exception ex) { return ex.Message; }
        // The record row goes with the active file; the vault entries (old GroupId) survive as history.
        RemoveRow(g);
        return null;
    }

    /// <summary>Merges <paramref name="src"/>'s history into <paramref name="dst"/> (LB's "Combine With
    /// Another Save"). The source's vault entries are re-tagged; a still-active source save is first
    /// backed up into the destination group and then removed from disk.</summary>
    public static string? Combine(SaveGroup src, SaveGroup dst)
    {
        if (ReferenceEquals(src, dst) || string.Equals(src.GroupId, dst.GroupId, StringComparison.OrdinalIgnoreCase))
            return "Cannot combine a save with itself.";
        if (src.Active != null)
        {
            if (src.Plugin == null) return "No integration plugin for this save.";
            var moved = new SaveGroup   // back the source's active up INTO the destination group
            {
                Game = src.Game, GameId = src.GameId, AppId = src.AppId, IsState = src.IsState, Slot = src.Slot,
                GroupId = dst.GroupId, GroupName = dst.GroupName,
                Emulator = src.Emulator, Plugin = src.Plugin,
                Active = src.Active, ActivePath = src.ActivePath, ActiveIsDirectory = src.ActiveIsDirectory,
                Backups = dst.Backups,
            };
            var b = Backup(moved, force: false);
            if (b.Error != null) return "Could not archive the source save — nothing was combined: " + b.Error;
            try { src.Plugin.RemoveSave(src.Active); } catch { }
        }
        bool changed = false;
        foreach (var e in src.Backups) { e.GroupId = dst.GroupId; e.GroupName = dst.GroupName; changed = true; }
        if (changed) SaveVault.Changed();
        RemoveRow(src);
        return null;
    }

    public static string? DeleteBackup(VaultEntry e)
    {
        try
        {
            string abs = SaveVault.Abs(e);
            if (e.IsDirectory) { if (Directory.Exists(abs)) Directory.Delete(abs, recursive: true); }
            else if (File.Exists(abs)) File.Delete(abs);
            SaveVault.Remove(e);
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    private static void DeleteBackupsOf(SaveGroup g)
    {
        foreach (var e in g.Backups.ToList()) DeleteBackup(e);
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

    private static string SafeStr(Func<string?> f) { try { return f() ?? ""; } catch { return ""; } }
    private static string Try(Func<string?> f) { try { return f() ?? ""; } catch (Exception ex) { return "<threw:" + ex.Message + ">"; } }

    public static string AbsPath(string p)
    {
        if (string.IsNullOrEmpty(p)) return "";
        try { return Path.IsPathRooted(p) ? Path.GetFullPath(p) : Path.GetFullPath(Path.Combine(LbRoot, p)); } catch { return p; }
    }

    public static bool PathEq(string a, string b)
        => string.Equals(AbsPath(a).TrimEnd('\\', '/'), AbsPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private static string Sanitize(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Trim();
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

    /// <summary>Folder signature: md5 over "relpath|md5" lines, sorted — same idea as the plugins'
    /// folder manifests (Saturn/PCSX2), so unchanged folders dedupe too.</summary>
    public static string DirManifestMd5(string dir)
    {
        try
        {
            var sb = new StringBuilder();
            foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                                          .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                sb.Append(Path.GetRelativePath(dir, f).ToLowerInvariant()).Append('|').Append(FileMd5(f)).Append('\n');
            }
            return Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
        }
        catch { return ""; }
    }
}
