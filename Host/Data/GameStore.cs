// Two-tier, memory-frugal game store loaded from LaunchBox's native Platform
// XMLs (LB\Data\Platforms\*.xml) — the authoritative source (user edits live
// there), with ZERO dependency on ExtendDB or its SQLite.
//
// Tier 1 (essential, always resident): a blittable `GameRow[]` (native fields +
//   int indices into a deduped string pool). No per-game heap object → the GC
//   doesn't even scan it. This is what keeps IGame functional during a game
//   launch (the launching plugin reads Title/ApplicationPath/EmulatorId here).
//
// Tier 2 (optional, droppable): `Notes` text in a separate string[], the non-modelled
//   <Game> fields (_extra), and the display-only per-game sub-entities (ModelSettings,
//   GameControllerSupport) — all dropped at game launch (DropOptional) and reloaded
//   after (ReloadOptional / lazy re-parse). Wikipedia/Video URLs are short → kept in the
//   pool. EXCEPTION: the GameSave sub-entity is Tier-1 (resident), since a launch /
//   save-sync plugin may read or write it while the game runs (see _tier1SubEntities).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace LbApiHost.Host.Data;

/// <summary>Essential per-game fields. Blittable: only value types + pool indices.</summary>
internal struct GameRow
{
    public Guid Id;
    public int TitleIdx, SortTitleIdx, PlatformIdx, AppPathIdx, EmulatorIdIdx;
    public int DeveloperIdx, PublisherIdx, GenresIdx, RegionIdx, RatingIdx, StatusIdx, PlayModeIdx, VersionIdx;
    public int WikipediaUrlIdx, VideoUrlIdx;
    public float StarRatingFloat;
    public int PlayCount, PlayTime, CommunityVotes;
    public int LaunchBoxDbId;  // -1 = none
    public int MaxPlayers;     // -1 = none
    public int ReleaseYear;    // -1 = none
    public long DateAddedTicks, ReleaseDateTicks, LastPlayedTicks; // 0 = none
    public bool Favorite, Hide, Broken, Completed;
    public byte Installed;     // 0 = null, 1 = false, 2 = true

    // ── Extended fields (added 2026-06-06) ───────────────────────────────────
    public int CommandLineIdx, ConfigCmdIdx, ConfigPathIdx;
    public int DosBoxCfgIdx, CustomDosBoxIdx, ScummDataIdx, ScummTypeIdx;   // DosBox / ScummVM
    public int SeriesIdx, SourceIdx, ReleaseTypeIdx, RootFolderIdx, CloneOfIdx, ProgressIdx;
    public int RaHashIdx;      // RetroAchievementsHash (debug column)
    public int VideoPathIdx, ThemeVideoPathIdx, ManualPathIdx, MusicPathIdx; // stored overrides
    public long DateModifiedTicks;
    public float CommunityStarRating;
    public int StartupLoadDelay;
    public int Flags;          // packed booleans, see GFlags
}

/// <summary>Bit flags packed into <see cref="GameRow.Flags"/>.</summary>
internal static class GFlags
{
    public const int UseDosBox = 1, UseScummVm = 2, ScummAspect = 4, ScummFull = 8,
        Portable = 16, UseStartup = 32, OverrideStartup = 64, HideNonExclusive = 128,
        HideMouse = 256, DisableShutdown = 512, AggressiveHiding = 1024;
}

internal sealed class GameStore
{
    public GameRow[] Rows = Array.Empty<GameRow>();
    public List<string> Pool = new() { "" };        // index 0 = empty; grows at runtime via InternRuntime
    private Dictionary<string, int> _poolMap = new(StringComparer.Ordinal) { [""] = 0 };
    private string[] _notes;                        // tier 2 (droppable), index-aligned with Rows
    private string _platformsDir;

    private readonly Dictionary<Guid, int> _byId = new();
    public IReadOnlyDictionary<Guid, int> ById => _byId;

    // platform name -> game row indices
    private readonly Dictionary<string, List<int>> _byPlatform = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, List<int>> ByPlatform => _byPlatform;

    // platform name -> source XML file (for write-back)
    private readonly Dictionary<string, string> _platformFile = new(StringComparer.OrdinalIgnoreCase);

    // Games created this session via AddNewGame (their <Game> node is created at flush, not edited
    // in place). Their Platform field is settable (no cross-file move concern — the node is new).
    private readonly HashSet<Guid> _addedIds = new();
    public bool IsAddedGame(Guid id) => _addedIds.Contains(id);

    // Sparse store of <Game> fields NOT modelled by GameRow / IGame (Gog*, Android*, Missing*,
    // RetroAchievements*, pause AHK, …). Most games have none. Read/written generically via
    // ILiteBoxGame; persisted as ordinary modify ops. Modelled field names are skipped here.
    private readonly Dictionary<Guid, Dictionary<string, string>> _extra = new();
    // Lazy (NOT a field initializer) — _gameFieldParsers is declared later in the file, so a static
    // field initializer here would run first and see it null.
    private static HashSet<string> _modeledNamesCache;
    private static HashSet<string> ModeledNames => _modeledNamesCache ??= new HashSet<string>(_gameFieldParsers.Keys, StringComparer.Ordinal) { "ID" };

    public string GetExtraField(Guid id, string name)
        => _extra.TryGetValue(id, out var m) && m.TryGetValue(name, out var v) ? (v ?? "") : "";
    public IReadOnlyCollection<string> ExtraFieldNames(Guid id)
        => _extra.TryGetValue(id, out var m) ? (IReadOnlyCollection<string>)m.Keys : Array.Empty<string>();
    public bool IsModeledField(string name) => ModeledNames.Contains(name);

    // ── Generic per-game sub-entities (ModelSettings / GameControllerSupport / GameSave / future) ──
    // Top-level XML elements that carry a GameId/GameID and aren't one of the typed child entities.
    // Captured raw (ordered field maps) so they round-trip and stay accessible, even though the SDK
    // exposes none of them. gameId -> elementType -> list of field maps.
    private readonly Dictionary<Guid, Dictionary<string, List<Dictionary<string, string>>>> _subEntities = new();

    // Sub-entity types that may be needed WHILE a game runs (a launch / save-sync plugin reads or
    // writes the game's <GameSave>) → kept resident (Tier-1), never freed by DropOptional. The rest
    // (ModelSettings, GameControllerSupport, …) are display-only and stay Tier-2 (droppable).
    private static readonly HashSet<string> _tier1SubEntities = new(StringComparer.Ordinal) { "GameSave" };
    internal static bool IsTier1SubEntity(string type) => _tier1SubEntities.Contains(type);

    // Stash a game's non-modelled <Game> fields (Gog*/Android*/Missing*/RetroAchievements*/pause/…)
    // sparsely & pooled. Shared by Build and ReloadOptional (Tier-2 reload).
    private void CaptureExtra(Guid id, XElement g)
    {
        Dictionary<string, string> extra = null;
        foreach (var ce in g.Elements())
        {
            string en2 = ce.Name.LocalName;
            if (ModeledNames.Contains(en2)) continue;
            string ev = ce.Value;
            if (string.IsNullOrEmpty(ev)) continue;
            (extra ??= new Dictionary<string, string>(StringComparer.Ordinal))[Pooled(en2)] = Pooled(ev);
        }
        if (extra != null) _extra[id] = extra;
    }

    private void CaptureSubEntity(string type, XElement el, bool reload = false)
    {
        // On a Tier-2 reload the Tier-1 sub-entities (GameSave) were never dropped — they are still
        // resident (possibly with un-flushed edits), so re-reading them from disk would duplicate /
        // clobber. Skip them here; the initial Build (reload == false) captures them normally.
        if (reload && IsTier1SubEntity(type)) return;
        string gidStr = (string)(el.Element("GameId") ?? el.Element("GameID"));
        if (!Guid.TryParse(gidStr, out var sgid)) return;   // only per-game sub-entities
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var c in el.Elements()) map[Pooled(c.Name.LocalName)] = Pooled(c.Value);
        if (!_subEntities.TryGetValue(sgid, out var byType)) _subEntities[sgid] = byType = new(StringComparer.Ordinal);
        if (!byType.TryGetValue(type, out var lst)) byType[type] = lst = new();
        lst.Add(map);
    }

    public IReadOnlyCollection<string> SubEntityTypes(Guid gid)
        => _subEntities.TryGetValue(gid, out var m) ? (IReadOnlyCollection<string>)m.Keys : Array.Empty<string>();
    public IReadOnlyList<IReadOnlyDictionary<string, string>> GetSubEntities(Guid gid, string type)
        => _subEntities.TryGetValue(gid, out var m) && m.TryGetValue(type, out var l)
            ? l.ConvertAll(d => (IReadOnlyDictionary<string, string>)d)
            : (IReadOnlyList<IReadOnlyDictionary<string, string>>)Array.Empty<IReadOnlyDictionary<string, string>>();
    public void SetSubEntities(Guid gid, string type, IEnumerable<IReadOnlyDictionary<string, string>> rows)
    {
        if (string.IsNullOrEmpty(type)) return;
        var list = rows == null ? new List<Dictionary<string, string>>()
            : rows.Select(r => new Dictionary<string, string>(r, StringComparer.Ordinal)).ToList();
        if (!_subEntities.TryGetValue(gid, out var m)) _subEntities[gid] = m = new(StringComparer.Ordinal);
        m[type] = list;
        if (ReadOnly || _oplog == null) return;
        _oplog.Append("replace", type, null, gid.ToString(), null, JsonSerializer.Serialize(list));
    }

    // Memory rebuild from a generic sub-entity replace op (boot replay).
    private void ApplySubEntityReplaceToMemory(Guid gid, string type, string json)
    {
        List<Dictionary<string, string>> list;
        try { list = JsonSerializer.Deserialize<List<Dictionary<string, string>>>(json) ?? new(); } catch { return; }
        if (!_subEntities.TryGetValue(gid, out var m)) _subEntities[gid] = m = new(StringComparer.Ordinal);
        m[type] = list;
    }

    // DOM apply: drop the game's existing <type> sub-entities, re-emit from JSON (field order as stored).
    private static void ApplySubEntityReplaceToDoc(XDocument doc, string type, string gameId, string json)
    {
        foreach (var e in doc.Root.Elements(type).Where(e => (string)(e.Element("GameId") ?? e.Element("GameID")) == gameId).ToList()) e.Remove();
        List<Dictionary<string, string>> list;
        try { list = JsonSerializer.Deserialize<List<Dictionary<string, string>>>(json) ?? new(); } catch { return; }
        foreach (var rec in list)
        {
            var el = new XElement(type);
            foreach (var kv in rec) if (!string.IsNullOrEmpty(kv.Value)) el.Add(new XElement(kv.Key, kv.Value));
            doc.Root.Add(el);
        }
    }

    // Write-back operation log (WAL): every mutation (user-state OR plugin) is appended as an
    // ordered event and flushed to the XMLs at a safe time. See OpLog. Opened in Load().
    private OpLog _oplog;
    private string _opDbOverride;   // set only by the self-test, to isolate from the real log
    private string OpDbPath => _opDbOverride ?? Path.Combine(AppContext.BaseDirectory, "LiteBox.pending.db");
    // Legacy positional journal (pre-WAL); migrated into the op-log + deleted on first boot.
    private string LegacyJournalPath => Path.Combine(AppContext.BaseDirectory, "LiteBox.pending");

    /// <summary>Read-only mode (config, default true): NOTHING is ever written to disk — neither
    /// the journal nor the Platform XMLs. Mutations update the in-memory Rows only, for this run.</summary>
    public bool ReadOnly = true;

    // Per-game accessory entities (resident — needed for launch/disc selection).
    private readonly Dictionary<Guid, List<AddApp>> _addApps = new();
    private readonly Dictionary<Guid, List<AltName>> _altNames = new();
    private readonly Dictionary<Guid, List<GameMount>> _mounts = new();   // DOSBox additional mounts
    private readonly Dictionary<Guid, List<CustomField>> _customFields = new();
    public IReadOnlyList<AddApp> AddAppsFor(Guid id) => _addApps.TryGetValue(id, out var l) ? l : (IReadOnlyList<AddApp>)Array.Empty<AddApp>();
    public IReadOnlyList<AltName> AltNamesFor(Guid id) => _altNames.TryGetValue(id, out var l) ? l : (IReadOnlyList<AltName>)Array.Empty<AltName>();
    public IReadOnlyList<GameMount> MountsFor(Guid id) => _mounts.TryGetValue(id, out var l) ? l : (IReadOnlyList<GameMount>)Array.Empty<GameMount>();
    public IReadOnlyList<CustomField> CustomFieldsFor(Guid id) => _customFields.TryGetValue(id, out var l) ? l : (IReadOnlyList<CustomField>)Array.Empty<CustomField>();

    // Mutable list accessors for child-entity write-back (create the list on demand).
    internal List<AddApp> AddAppsMutable(Guid id) => _addApps.TryGetValue(id, out var l) ? l : (_addApps[id] = new List<AddApp>());
    internal List<AltName> AltNamesMutable(Guid id) => _altNames.TryGetValue(id, out var l) ? l : (_altNames[id] = new List<AltName>());
    internal List<GameMount> MountsMutable(Guid id) => _mounts.TryGetValue(id, out var l) ? l : (_mounts[id] = new List<GameMount>());
    internal List<CustomField> CustomFieldsMutable(Guid id) => _customFields.TryGetValue(id, out var l) ? l : (_customFields[id] = new List<CustomField>());

    public int Count => Rows.Length;
    public string Str(int idx) => (idx > 0 && idx < Pool.Count) ? Pool[idx] : "";

    /// <summary>Interns a string into the pool at runtime (write-back setters). Append-only;
    /// orphaned old values are acceptable. Returns 0 for null/empty.</summary>
    public int InternRuntime(string v)
    {
        if (string.IsNullOrEmpty(v)) return 0;
        if (_poolMap.TryGetValue(v, out var idx)) return idx;
        idx = Pool.Count; Pool.Add(v); _poolMap[v] = idx; return idx;
    }

    /// <summary>Returns the shared/pooled instance of a string (dedups the highly-repetitive extra
    /// field names + values like "true"/"false" so they don't blow up memory across many games).</summary>
    public string Pooled(string s) => string.IsNullOrEmpty(s) ? s : Pool[InternRuntime(s)];
    public string NotesFor(int i) => (_notes != null && i >= 0 && i < _notes.Length) ? _notes[i] : null;
    public bool OptionalLoaded => _notes != null;

    // ── Load ────────────────────────────────────────────────────────────────
    public static GameStore Load(string platformsDir, string opDbPathOverride = null)
    {
        var s = new GameStore { _platformsDir = platformsDir, _opDbOverride = opDbPathOverride };
        s.Build(includeNotes: true);
        s._oplog = OpLog.Open(s.OpDbPath);
        return s;
    }

    /// <summary>Releases the op-log connection (clean shutdown / test teardown).</summary>
    public void CloseLog() { try { _oplog?.Dispose(); } catch { } _oplog = null; }

    private void Build(bool includeNotes)
    {
        Pool = new List<string> { "" };
        _poolMap = new Dictionary<string, int>(StringComparer.Ordinal) { [""] = 0 };
        int Intern(string v) => InternRuntime(v);

        var rows = new List<GameRow>(1024);
        var notes = includeNotes ? new List<string>(1024) : null;
        _byId.Clear(); _byPlatform.Clear();

        var settings = new XmlReaderSettings { IgnoreComments = true, IgnoreWhitespace = true, DtdProcessing = DtdProcessing.Prohibit };

        foreach (var file in Directory.EnumerateFiles(_platformsDir, "*.xml"))
        {
            using var reader = XmlReader.Create(file, settings);
            if (!reader.ReadToFollowing("LaunchBox")) continue;
            // NOTE: XNode.ReadFrom leaves the reader ON the next sibling, so we
            // must NOT call Read() again before re-checking — else every other
            // <Game> is skipped. Read() only in the non-Game branch.
            while (!reader.EOF)
            {
                if (reader.NodeType != XmlNodeType.Element) { reader.Read(); continue; }
                string en = reader.Name;
                if (en != "Game" && en != "AdditionalApplication" && en != "AlternateName" && en != "Mount" && en != "CustomField")
                {
                    if (reader.Depth == 0) { reader.Read(); continue; }   // <LaunchBox> root → descend, don't slurp
                    // Unknown per-game sub-entity (ModelSettings / GameControllerSupport / GameSave / future)
                    // — capture raw so LiteBox neither loses nor hides it. Skip non-game-linked elements.
                    try { CaptureSubEntity(en, (XElement)XNode.ReadFrom(reader)); } catch { reader.Read(); }
                    continue;
                }

                XElement g;
                try { g = (XElement)XNode.ReadFrom(reader); } catch { reader.Read(); continue; }
                string V(string n) => g.Element(n)?.Value;

                if (en == "AdditionalApplication")
                {
                    if (Guid.TryParse(V("GameID"), out var agid))
                    {
                        if (!_addApps.TryGetValue(agid, out var al)) _addApps[agid] = al = new List<AddApp>();
                        al.Add(new AddApp
                        {
                            Id = V("Id"), ApplicationPath = V("ApplicationPath"), Name = V("Name"),
                            CommandLine = V("CommandLine"), Developer = V("Developer"), Publisher = V("Publisher"),
                            Region = V("Region"), Version = V("Version"), Status = V("Status"), EmulatorId = V("EmulatorId"),
                            Disc = ParseIntN(V("Disc")), Priority = ParseInt(V("Priority"), 0),
                            PlayCount = ParseInt(V("PlayCount"), 0), PlayTime = ParseInt(V("PlayTime"), 0),
                            AutoRunBefore = ParseBool(V("AutoRunBefore")), AutoRunAfter = ParseBool(V("AutoRunAfter")),
                            UseEmulator = ParseBool(V("UseEmulator")), UseDosBox = ParseBool(V("UseDosBox")),
                            WaitForExit = ParseBool(V("WaitForExit")), SideA = ParseBool(V("SideA")), SideB = ParseBool(V("SideB")),
                            ReleaseDate = ParseDateN(V("ReleaseDate")), LastPlayed = ParseDateN(V("LastPlayed")), Installed = ParseBoolN(V("Installed")),
                        });
                    }
                    continue;
                }
                if (en == "AlternateName")
                {
                    if (Guid.TryParse(V("GameID"), out var ngid))
                    {
                        if (!_altNames.TryGetValue(ngid, out var nl)) _altNames[ngid] = nl = new List<AltName>();
                        nl.Add(new AltName { Name = V("Name"), Region = V("Region") });
                    }
                    continue;
                }
                if (en == "Mount")
                {
                    if (Guid.TryParse(V("GameID"), out var mgid))
                    {
                        if (!_mounts.TryGetValue(mgid, out var ml)) _mounts[mgid] = ml = new List<GameMount>();
                        var dl = V("DriveLetter");
                        ml.Add(new GameMount
                        {
                            DriveLetter = !string.IsNullOrEmpty(dl) ? char.ToUpperInvariant(dl[0]) : 'C',
                            Filesystem = V("Filesystem"), MountType = V("MountType"),
                            Path = V("Path"), Type = V("Type"),
                        });
                    }
                    continue;
                }
                if (en == "CustomField")
                {
                    if (Guid.TryParse(V("GameID"), out var cgid))
                    {
                        if (!_customFields.TryGetValue(cgid, out var cl)) _customFields[cgid] = cl = new List<CustomField>();
                        cl.Add(new CustomField { Name = V("Name"), Value = V("Value") });
                    }
                    continue;
                }

                // en == "Game"
                if (!Guid.TryParse(V("ID"), out var id)) continue;

                var row = new GameRow
                {
                    Id = id,
                    TitleIdx = Intern(V("Title")),
                    SortTitleIdx = Intern(V("SortTitle")),
                    PlatformIdx = Intern(V("Platform")),
                    AppPathIdx = Intern(V("ApplicationPath")),
                    EmulatorIdIdx = Intern(V("Emulator")),
                    DeveloperIdx = Intern(V("Developer")),
                    PublisherIdx = Intern(V("Publisher")),
                    GenresIdx = Intern(V("Genre")),
                    RegionIdx = Intern(V("Region")),
                    RatingIdx = Intern(V("Rating")),
                    StatusIdx = Intern(V("Status")),
                    PlayModeIdx = Intern(V("PlayMode")),
                    VersionIdx = Intern(V("Version")),
                    WikipediaUrlIdx = Intern(V("WikipediaURL")),
                    VideoUrlIdx = Intern(V("VideoUrl")),
                    StarRatingFloat = ParseFloat(V("StarRatingFloat") ?? V("StarRating")),
                    PlayCount = ParseInt(V("PlayCount"), 0),
                    PlayTime = ParseInt(V("PlayTime"), 0),
                    CommunityVotes = ParseInt(V("CommunityStarRatingTotalVotes"), 0),
                    LaunchBoxDbId = ParseInt(V("DatabaseID"), -1),
                    MaxPlayers = ParseInt(V("MaxPlayers"), -1),
                    DateAddedTicks = ParseTicks(V("DateAdded")),
                    ReleaseDateTicks = ParseTicks(V("ReleaseDate")),
                    LastPlayedTicks = ParseTicks(V("LastPlayedDate")),
                    Favorite = ParseBool(V("Favorite")),
                    Hide = ParseBool(V("Hide")),
                    Broken = ParseBool(V("Broken")),
                    Completed = ParseBool(V("Completed")),
                    Installed = ParseTri(V("Installed")),

                    // ── extended ────────────────────────────────────────────
                    CommandLineIdx = Intern(V("CommandLine")),
                    ConfigCmdIdx = Intern(V("ConfigurationCommandLine")),
                    ConfigPathIdx = Intern(V("ConfigurationPath")),
                    DosBoxCfgIdx = Intern(V("DosBoxConfigurationPath")),
                    CustomDosBoxIdx = Intern(V("CustomDosBoxVersionPath")),
                    ScummDataIdx = Intern(V("ScummVMGameDataFolderPath")),
                    ScummTypeIdx = Intern(V("ScummVMGameType")),
                    SeriesIdx = Intern(V("Series")),
                    SourceIdx = Intern(V("Source")),
                    ReleaseTypeIdx = Intern(V("ReleaseType")),
                    RootFolderIdx = Intern(V("RootFolder")),
                    CloneOfIdx = Intern(V("CloneOf")),
                    ProgressIdx = Intern(V("Progress")),
                    RaHashIdx = Intern(V("RetroAchievementsHash")),
                    VideoPathIdx = Intern(V("VideoPath")),
                    ThemeVideoPathIdx = Intern(V("ThemeVideoPath")),
                    ManualPathIdx = Intern(V("ManualPath")),
                    MusicPathIdx = Intern(V("MusicPath")),
                    DateModifiedTicks = ParseTicks(V("DateModified")),
                    CommunityStarRating = ParseFloat(V("CommunityStarRating")),
                    StartupLoadDelay = ParseInt(V("StartupLoadDelay"), 0),
                    Flags =
                        (ParseBool(V("UseDosBox")) ? GFlags.UseDosBox : 0)
                      | (ParseBool(V("UseScummVM")) ? GFlags.UseScummVm : 0)
                      | (ParseBool(V("ScummVMAspectCorrection")) ? GFlags.ScummAspect : 0)
                      | (ParseBool(V("ScummVMFullscreen")) ? GFlags.ScummFull : 0)
                      | (ParseBool(V("Portable")) ? GFlags.Portable : 0)
                      | (ParseBool(V("UseStartupScreen")) ? GFlags.UseStartup : 0)
                      | (ParseBool(V("OverrideDefaultStartupScreenSettings")) ? GFlags.OverrideStartup : 0)
                      | (ParseBool(V("HideAllNonExclusiveFullscreenWindows")) ? GFlags.HideNonExclusive : 0)
                      | (ParseBool(V("HideMouseCursorInGame")) ? GFlags.HideMouse : 0)
                      | (ParseBool(V("DisableShutdownScreen")) ? GFlags.DisableShutdown : 0)
                      | (ParseBool(V("AggressiveWindowHiding")) ? GFlags.AggressiveHiding : 0),
                };
                row.ReleaseYear = row.ReleaseDateTicks != 0 ? new DateTime(row.ReleaseDateTicks).Year : -1;

                int i = rows.Count;
                rows.Add(row);
                notes?.Add(V("Notes"));
                _byId[id] = i;

                CaptureExtra(id, g);   // non-modelled <Game> fields → Tier-2 _extra (droppable)

                string plat = V("Platform") ?? "";
                if (!_byPlatform.TryGetValue(plat, out var list)) _byPlatform[plat] = list = new List<int>();
                list.Add(i);
                _platformFile[plat] = file; // remember the source file for write-back
            }
        }

        Rows = rows.ToArray();
        _notes = notes?.ToArray();
    }

    // ── Tier-2 drop / reload ─────────────────────────────────────────────────
    /// <summary>Free the optional (Notes) tier. Call before a game launch.</summary>
    public void DropOptional()
    {
        _notes = null;
        _extra.Clear();             // full-field cache (Missing*/Gog*/…) — non-essential during a game
        DropOptionalSubEntities();  // ModelSettings / GameControllerSupport / … — but KEEP GameSave (Tier-1)
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }

    // Free every per-game sub-entity except the Tier-1 types (GameSave), which a launch / save-sync
    // plugin may need while the game runs. Leaves the GameSave maps resident; drops the display-only rest.
    private void DropOptionalSubEntities()
    {
        if (_subEntities.Count == 0) return;
        List<Guid> emptied = null;
        foreach (var kv in _subEntities)
        {
            var byType = kv.Value;
            foreach (var t in byType.Keys.Where(t => !IsTier1SubEntity(t)).ToList()) byType.Remove(t);
            if (byType.Count == 0) (emptied ??= new List<Guid>()).Add(kv.Key);
        }
        if (emptied != null) foreach (var g in emptied) _subEntities.Remove(g);
    }

    /// <summary>Re-read the optional tier from the XMLs (after a game exits): Notes + the full-field
    /// extras + the per-game sub-entities.</summary>
    public void ReloadOptional()
    {
        if (_notes != null) return;
        var notes = new string[Rows.Length];
        var settings = new XmlReaderSettings { IgnoreComments = true, IgnoreWhitespace = true, DtdProcessing = DtdProcessing.Prohibit };
        foreach (var file in Directory.EnumerateFiles(_platformsDir, "*.xml"))
        {
            using var reader = XmlReader.Create(file, settings);
            if (!reader.ReadToFollowing("LaunchBox")) continue;
            while (!reader.EOF)
            {
                if (reader.NodeType != XmlNodeType.Element) { reader.Read(); continue; }
                if (reader.Name != "Game")
                {
                    if (reader.Depth == 0) { reader.Read(); continue; }   // <LaunchBox> root → descend
                    try { CaptureSubEntity(reader.Name, (XElement)XNode.ReadFrom(reader), reload: true); } catch { reader.Read(); }
                    continue;
                }
                XElement g;
                try { g = (XElement)XNode.ReadFrom(reader); } catch { reader.Read(); continue; }
                if (!Guid.TryParse(g.Element("ID")?.Value, out var id) || !_byId.TryGetValue(id, out var i)) continue;
                notes[i] = g.Element("Notes")?.Value;
                CaptureExtra(id, g);
            }
        }
        _notes = notes;
        // The reload read pristine XML — overlay only the un-flushed todo ops that touch the DROPPED
        // tier (Notes / extra fields / sub-entities). Tier-1 ops (add/delete/move, modelled-field
        // modifies) already live in GameRow/_byId which were never dropped, so we skip them.
        var pending = _oplog?.ReadAll();
        if (pending != null && pending.Count > 0) ReplayOptionalTierOps(pending);
    }

    // Re-apply ONLY the pending ops affecting the droppable Tier-2 (Notes, non-modelled extra fields,
    // per-game sub-entities). Skips everything that lives in the always-resident Tier-1.
    private void ReplayOptionalTierOps(List<Op> ops)
    {
        foreach (var op in ops)
        {
            if (op.Entity == "Game" && op.OpType == "modify")
            {
                if (op.Field != "Notes" && IsModeledField(op.Field)) continue;   // modelled = Tier-1, not dropped
                if (Guid.TryParse(op.Id, out var gid) && _byId.TryGetValue(gid, out var i))
                    ApplyFieldToRow(i, op.Field, op.Value);                       // Notes -> _notes, extra -> _extra
            }
            else if (op.OpType == "replace" && !IsChildEntity(op.Entity) && !IsTier1SubEntity(op.Entity)
                     && Guid.TryParse(op.ParentId, out var sg) && _byId.ContainsKey(sg))
            {
                ApplySubEntityReplaceToMemory(sg, op.Entity, op.Value);           // dropped sub-entities only
            }
            // add / delete / move / child-entity replace / modelled-field modify / GameSave → Tier-1, skip.
        }
    }

    // ── Mutations → operation log (WAL) ──────────────────────────────────────
    // Every setter updates the in-memory Row immediately (UI reflects the change) and, when not
    // ReadOnly, appends a "modify" op. The XMLs are rewritten later, at a safe time (FlushOpsToXml).

    public void JournalFavorite(int i, bool v)
    { if (i < 0 || i >= Rows.Length || Rows[i].Favorite == v) return; RecordModify(i, "Favorite", v ? "true" : "false"); }

    public void JournalStarRating(int i, float v)
    { if (i < 0 || i >= Rows.Length || Math.Abs(Rows[i].StarRatingFloat - v) < 0.001f) return; RecordModify(i, "StarRatingFloat", v.ToString(CultureInfo.InvariantCulture)); }

    /// <summary>A launch begins: bump play count + stamp last-played now.</summary>
    public void JournalPlayStart(int i)
    {
        if (i < 0 || i >= Rows.Length) return;
        RecordModify(i, "PlayCount", (Rows[i].PlayCount + 1).ToString(CultureInfo.InvariantCulture));
        RecordModify(i, "LastPlayedDate", new DateTime(DateTime.Now.Ticks, DateTimeKind.Local).ToString("o", CultureInfo.InvariantCulture));
    }

    /// <summary>A launch ended: add the elapsed seconds to total play time.</summary>
    public void JournalPlayTime(int i, int addSeconds)
    { if (i < 0 || i >= Rows.Length || addSeconds <= 0) return; RecordModify(i, "PlayTime", (Rows[i].PlayTime + addSeconds).ToString(CultureInfo.InvariantCulture)); }

    /// <summary>Generic IGame scalar write-back: <paramref name="xmlName"/> is the XML element name
    /// (e.g. "Developer"), <paramref name="value"/> the serialized value ("" = clear). Updates memory
    /// + logs the op. Unknown field names are ignored (logged once).</summary>
    public void SetGameField(int i, string xmlName, string value)
    { if (i < 0 || i >= Rows.Length || string.IsNullOrEmpty(xmlName)) return; RecordModify(i, xmlName, value ?? ""); }

    // Apply to memory (always, for UI) + append the op (only when persisting).
    private void RecordModify(int i, string xmlName, string value)
    {
        ApplyFieldToRow(i, xmlName, value);
        if (ReadOnly || _oplog == null) return;
        _oplog.Append("modify", "Game", Rows[i].Id.ToString(), null, xmlName, value);
    }

    private void ApplyFieldToRow(int i, string xmlName, string value)
    {
        if (_gameFieldParsers.TryGetValue(xmlName, out var apply)) { try { apply(this, i, value); } catch { } return; }
        // non-IGame field → sparse extra store ("" clears it). Flushes via the same modify op path.
        var id = Rows[i].Id;
        if (string.IsNullOrEmpty(value)) { if (_extra.TryGetValue(id, out var m0)) m0.Remove(xmlName); return; }
        if (!_extra.TryGetValue(id, out var m)) _extra[id] = m = new Dictionary<string, string>(StringComparer.Ordinal);
        m[Pooled(xmlName)] = Pooled(value);
    }

    /// <summary>At boot: migrate any legacy journal, re-apply the surviving op-log to memory (UI
    /// correct), then flush to XML if LaunchBox/BigBox aren't running. If they're running, keep the
    /// log for later.</summary>
    public void RecoverJournalOnLoad()
    {
        MigrateLegacyJournal();
        var ops = _oplog?.ReadAll();
        if (ops != null && ops.Count > 0)
        {
            ReplayOpsToMemory(ops);
            Console.WriteLine($"[store] recovered op-log ({ops.Count} op(s))");
        }
        if (!ReadOnly && !IsLaunchBoxRunning()) FlushOpsToXml();
    }

    /// <summary>Re-applies every pending op-log entry to the in-memory model. Used at boot recovery
    /// AND after a Tier-2 reload (so un-flushed edits/adds/deletes survive a game-launch drop+reload —
    /// the reload re-reads pristine XML, then this overlays the pending todo). Fully idempotent: adds
    /// are guarded, modifies/replaces are last-write-wins, deletes/moves no-op if already applied.</summary>
    private void ReplayOpsToMemory(List<Op> ops)
    {
        foreach (var op in ops)   // seq order: an "add" precedes its field "modify"s
        {
            if (op.Entity == "Game" && op.OpType == "add")
            { if (Guid.TryParse(op.Id, out var ag)) AddGameRowForReplay(ag); }
            else if (op.Entity == "Game" && op.OpType == "modify")
            { if (Guid.TryParse(op.Id, out var gid) && _byId.TryGetValue(gid, out var i)) ApplyFieldToRow(i, op.Field, op.Value); }
            else if (op.Entity == "Game" && op.OpType == "delete")
            { if (Guid.TryParse(op.Id, out var dg)) { _byId.Remove(dg); _addedIds.Remove(dg); } }
            else if (op.Entity == "Game" && op.OpType == "move")
            { if (Guid.TryParse(op.Id, out var mg) && _byId.TryGetValue(mg, out var mi)) MoveInMemory(mi, op.Value); }
            else if (op.OpType == "replace" && IsChildEntity(op.Entity))
            { if (Guid.TryParse(op.ParentId, out var pgid)) ApplyChildReplace(op.Entity, pgid, op.Value); }
            else if (op.OpType == "replace" && Guid.TryParse(op.ParentId, out var sg) && _byId.ContainsKey(sg))
            { ApplySubEntityReplaceToMemory(sg, op.Entity, op.Value); }   // generic per-game sub-entity
        }
    }

    // Old positional journal (guid|fav|rating|playcount|lastplayedticks|playtime): apply to memory,
    // and when not ReadOnly re-emit as ops + delete the file. In ReadOnly it is left untouched.
    private void MigrateLegacyJournal()
    {
        try
        {
            if (!File.Exists(LegacyJournalPath)) return;
            foreach (var line in File.ReadAllLines(LegacyJournalPath))
            {
                var p = line.Split('|');
                if (p.Length < 6 || !Guid.TryParse(p[0], out var id) || !_byId.TryGetValue(id, out var i)) continue;
                RecordModify(i, "Favorite", p[1] == "1" ? "true" : "false");
                RecordModify(i, "StarRatingFloat", p[2]);
                RecordModify(i, "PlayCount", p[3]);
                if (long.TryParse(p[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var lp) && lp != 0)
                    RecordModify(i, "LastPlayedDate", new DateTime(lp, DateTimeKind.Local).ToString("o", CultureInfo.InvariantCulture));
                RecordModify(i, "PlayTime", p[5]);
            }
            if (!ReadOnly) { File.Delete(LegacyJournalPath); Console.WriteLine("[store] migrated legacy journal → op-log"); }
        }
        catch (Exception ex) { Console.WriteLine("[store] legacy journal migrate: " + ex.Message); }
    }

    /// <summary>At close: flush the op-log to XML if safe, else keep it for next time.</summary>
    public void FlushJournalIfSafe()
    {
        if (ReadOnly) return;                  // never write in read-only
        if (IsLaunchBoxRunning()) return;      // LB/BB own the XMLs → keep the log
        FlushOpsToXml();
    }

    public static bool IsLaunchBoxRunning()
    {
        try { return Process.GetProcessesByName("LaunchBox").Length > 0 || Process.GetProcessesByName("BigBox").Length > 0; }
        catch { return false; }
    }

    /// <summary>Plugin Save() path: flush now if safe, returning the number of games written.</summary>
    public int Flush()
    {
        if (ReadOnly || IsLaunchBoxRunning()) return 0;
        return FlushOpsToXml();
    }

    // Group ops by target XML file, apply in seq order on the loaded DOM (preserving unknown
    // fields), write each touched doc to .tmp, atomically swap ALL, then clear the log. WAL golden
    // rule: NEVER clear before every swap succeeds. Returns the number of distinct games written.
    private int FlushOpsToXml()
    {
        var ops = _oplog?.ReadAll();
        if (ops == null || ops.Count == 0) return 0;

        var docs = new Dictionary<string, XDocument>(StringComparer.OrdinalIgnoreCase);
        var index = new Dictionary<string, Dictionary<Guid, XElement>>(StringComparer.OrdinalIgnoreCase);
        var touched = new HashSet<Guid>();

        void EnsureDoc(string file)
        {
            if (docs.ContainsKey(file)) return;
            var d = File.Exists(file) ? XDocument.Load(file) : new XDocument(new XElement("LaunchBox"));
            docs[file] = d;
            var byId = new Dictionary<Guid, XElement>();
            foreach (var ge in d.Root.Elements("Game"))
                if (Guid.TryParse((string)ge.Element("ID"), out var gid)) byId[gid] = ge;
            index[file] = byId;
        }

        // Added games: their <Game> node is created at the end from the accumulated field ops
        // (op-driven, so a partial-crash replay rebuilds the same node). Order preserved.
        var addedFields = new Dictionary<Guid, Dictionary<string, string>>();
        var addedOrder = new List<Guid>();
        var deletedExisting = new List<(Guid id, string platform)>();
        // Keyed top-level entities (Emulator by ID in Emulators.xml; Platform / PlatformCategory by
        // Name in Platforms.xml) — per-entity accumulators for adds/deletes.
        var tlAdded = new Dictionary<string, Dictionary<string, Dictionary<string, string>>>(StringComparer.Ordinal);
        var tlOrder = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var tlDeleted = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        Dictionary<string, Dictionary<string, string>> TLAdded(string e) => tlAdded.TryGetValue(e, out var m) ? m : (tlAdded[e] = new(StringComparer.OrdinalIgnoreCase));
        List<string> TLOrder(string e) => tlOrder.TryGetValue(e, out var l) ? l : (tlOrder[e] = new());
        List<string> TLDeleted(string e) => tlDeleted.TryGetValue(e, out var l) ? l : (tlDeleted[e] = new());
        // Playlists are one-per-file (the op carries the file in ParentId); whole-file deletes deferred.
        var playlistDeletes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var op in ops)
        {
            try
            {
                if (op.Entity == "Game")
                {
                    if (!Guid.TryParse(op.Id, out var gid)) continue;
                    if (op.OpType == "add")
                    {
                        if (!addedFields.ContainsKey(gid)) { addedFields[gid] = new() { ["ID"] = gid.ToString() }; addedOrder.Add(gid); }
                        continue;
                    }
                    if (op.OpType == "modify")
                    {
                        if (addedFields.TryGetValue(gid, out var fld))                          // field of an added game
                        { if (string.IsNullOrEmpty(op.Value)) fld.Remove(op.Field); else fld[op.Field] = op.Value; continue; }
                        string file = FileForGame(gid);                                          // existing game
                        if (file == null || !File.Exists(file)) continue;
                        EnsureDoc(file);
                        var ge = index[file].TryGetValue(gid, out var el) ? el : null;
                        if (ge == null) continue;
                        ApplyModify(ge, op.Field, op.Value);
                        touched.Add(gid);
                        continue;
                    }
                    if (op.OpType == "delete")
                    {
                        if (addedFields.Remove(gid)) { addedOrder.Remove(gid); continue; }       // added then deleted → no node
                        deletedExisting.Add((gid, op.Value));                                    // op.Value = platform
                        continue;
                    }
                    if (op.OpType == "move")   // op.Field = old platform, op.Value = new platform
                    {
                        if (addedFields.TryGetValue(gid, out var afld)) { afld["Platform"] = op.Value; continue; } // not yet on disk
                        string oldFile = _platformFile.TryGetValue(op.Field ?? "", out var of) ? of : null;
                        string newFile = PlatformFile(op.Value);
                        if (newFile == null) continue;
                        XElement node = null;
                        if (oldFile != null && File.Exists(oldFile)) { EnsureDoc(oldFile); if (index[oldFile].TryGetValue(gid, out node)) { node.Remove(); index[oldFile].Remove(gid); } }
                        EnsureDoc(newFile);
                        if (node == null) continue;                                              // already moved / not found
                        SetOrRemove(node, "Platform", op.Value);
                        if (index[newFile].TryGetValue(gid, out var dup)) { dup.Remove(); index[newFile].Remove(gid); }
                        docs[newFile].Root.Add(node);
                        index[newFile][gid] = node;
                        touched.Add(gid);
                        continue;
                    }
                }
                else if (IsChildEntity(op.Entity) && op.OpType == "replace")
                {
                    if (!Guid.TryParse(op.ParentId, out var pgid)) continue;
                    string file = FileForGame(pgid);
                    if (file == null || !File.Exists(file)) continue;
                    EnsureDoc(file);
                    ApplyChildReplaceToDoc(docs[file], op.Entity, op.ParentId, op.Value);
                    touched.Add(pgid);
                }
                else if (IsTopLevelEntity(op.Entity))
                {
                    var (tfile, tkey, _) = TopLevelSpec(op.Entity);
                    if (tfile == null || !File.Exists(tfile) || string.IsNullOrEmpty(op.Id)) continue;
                    EnsureDoc(tfile);
                    if (op.OpType == "add")
                    { var m = TLAdded(op.Entity); if (!m.ContainsKey(op.Id)) { m[op.Id] = new(StringComparer.Ordinal) { [tkey] = op.Id }; TLOrder(op.Entity).Add(op.Id); } continue; }
                    if (op.OpType == "modify")
                    {
                        var m = TLAdded(op.Entity);
                        if (m.TryGetValue(op.Id, out var tfld)) { if (string.IsNullOrEmpty(op.Value)) tfld.Remove(op.Field); else tfld[op.Field] = op.Value; continue; }
                        var node = FindByChild(docs[tfile], op.Entity, tkey, op.Id);
                        if (node != null) ApplyModify(node, op.Field, op.Value);
                        continue;
                    }
                    if (op.OpType == "delete")
                    { var m = TLAdded(op.Entity); if (m.Remove(op.Id)) TLOrder(op.Entity).Remove(op.Id); else TLDeleted(op.Entity).Add(op.Id); continue; }
                }
                else if (op.Entity == "EmulatorPlatform" && op.OpType == "replace")
                {
                    string file = EmulatorsFile;
                    if (file == null || !File.Exists(file) || string.IsNullOrEmpty(op.ParentId)) continue;
                    EnsureDoc(file);
                    ApplyCollectionReplaceToDoc(docs[file], "EmulatorPlatform", "Emulator", op.ParentId, _emuPlatOrder, op.Value);
                }
                else if (op.Entity == "Playlist" || op.Entity == "PlaylistGame" || op.Entity == "PlaylistFilter")
                {
                    string file = op.ParentId;                       // playlist ops carry their source file
                    if (string.IsNullOrEmpty(file)) continue;
                    if (op.Entity == "Playlist" && op.OpType == "delete") { playlistDeletes.Add(file); continue; }
                    EnsureDoc(file);
                    var pdoc = docs[file];
                    if (op.Entity == "Playlist")
                    {
                        var pnode = FindOrCreatePlaylist(pdoc, op.Id);
                        if (op.OpType == "modify") SetOrRemove(pnode, op.Field, op.Value);   // "add" just ensures the node
                    }
                    else if (op.OpType == "replace")   // PlaylistGame / PlaylistFilter — one playlist per file
                    {
                        foreach (var e in pdoc.Root.Elements(op.Entity).ToList()) e.Remove();
                        var order = op.Entity == "PlaylistGame" ? _playlistGameOrder : _playlistFilterOrder;
                        List<Dictionary<string, string>> list;
                        try { list = JsonSerializer.Deserialize<List<Dictionary<string, string>>>(op.Value) ?? new(); } catch { list = new(); }
                        foreach (var rec in list) pdoc.Root.Add(BuildElement(op.Entity, rec, order));
                    }
                }
                else if (op.OpType == "replace" && Guid.TryParse(op.ParentId, out var subgid))
                {
                    // Generic per-game sub-entity (ModelSettings / GameControllerSupport / GameSave / future).
                    string file = FileForGame(subgid);
                    if (file == null || !File.Exists(file)) continue;
                    EnsureDoc(file);
                    ApplySubEntityReplaceToDoc(docs[file], op.Entity, op.ParentId, op.Value);
                    touched.Add(subgid);
                }
            }
            catch (Exception ex) { Console.WriteLine("[store] apply op seq=" + op.Seq + ": " + ex.Message); }
        }

        // Create added games (resolve/create the platform file, replace any same-id node → idempotent).
        foreach (var gid in addedOrder)
        {
            try
            {
                var fld = addedFields[gid];
                string file = PlatformFile(fld.TryGetValue("Platform", out var p) ? p : null);
                if (file == null) continue;
                EnsureDoc(file);
                if (index[file].TryGetValue(gid, out var old)) { old.Remove(); index[file].Remove(gid); }
                var el = BuildGameElement(fld);
                docs[file].Root.Add(el);
                index[file][gid] = el;
                touched.Add(gid);
            }
            catch (Exception ex) { Console.WriteLine("[store] add game " + gid + ": " + ex.Message); }
        }

        // Delete existing games (platform from the delete op → its file).
        foreach (var (gid, platform) in deletedExisting)
        {
            try
            {
                string file = !string.IsNullOrEmpty(platform) && _platformFile.TryGetValue(platform, out var f) ? f : FileForGame(gid);
                if (file == null || !File.Exists(file)) continue;
                EnsureDoc(file);
                if (index[file].TryGetValue(gid, out var ge)) { ge.Remove(); index[file].Remove(gid); touched.Add(gid); }
            }
            catch (Exception ex) { Console.WriteLine("[store] delete game " + gid + ": " + ex.Message); }
        }

        // Create / delete keyed top-level entities (Emulator / Platform / PlatformCategory).
        foreach (var e in tlOrder.Keys.Concat(tlDeleted.Keys).Distinct().ToList())
        {
            var (tfile, tkey, torder) = TopLevelSpec(e);
            if (tfile == null || !File.Exists(tfile)) continue;
            EnsureDoc(tfile);
            var tdoc = docs[tfile];
            if (tlOrder.TryGetValue(e, out var addList))
                foreach (var id in addList)
                    try { FindByChild(tdoc, e, tkey, id)?.Remove(); tdoc.Root.Add(BuildElement(e, tlAdded[e][id], torder)); }
                    catch (Exception ex) { Console.WriteLine($"[store] add {e} {id}: {ex.Message}"); }
            if (tlDeleted.TryGetValue(e, out var delList))
                foreach (var id in delList)
                    try
                    {
                        FindByChild(tdoc, e, tkey, id)?.Remove();
                        if (e == "Emulator")   // also drop the emulator's per-platform rows
                            foreach (var ep in tdoc.Root.Elements("EmulatorPlatform").Where(x => (string)x.Element("Emulator") == id).ToList()) ep.Remove();
                    }
                    catch (Exception ex) { Console.WriteLine($"[store] delete {e} {id}: {ex.Message}"); }
        }

        // Deleted playlists are whole-file removals — don't write those files.
        foreach (var df in playlistDeletes) docs.Remove(df);
        if (docs.Count == 0 && playlistDeletes.Count == 0) { _oplog.Clear(); return 0; }

        // Snapshot the pristine originals (still untouched on disk) before any swap.
        BackupBeforeWrite(docs.Keys);

        // Phase 1: write every touched doc to .tmp.
        var swaps = new List<(string tmp, string file)>();
        foreach (var kv in docs)
        {
            string tmp = kv.Key + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { kv.Value.Save(tmp); swaps.Add((tmp, kv.Key)); }
            catch (Exception ex) { Console.WriteLine("[store] save tmp " + kv.Key + ": " + ex.Message); }
        }
        // Phase 2: swap all .tmp → real file (atomic per file).
        foreach (var (tmp, file) in swaps) ReplaceAtomic(tmp, file);
        // Phase 2b: whole-file playlist deletes.
        foreach (var df in playlistDeletes)
            try { if (File.Exists(df)) File.Delete(df); } catch (Exception ex) { Console.WriteLine("[store] delete playlist file " + df + ": " + ex.Message); }
        // Phase 3: ONLY now clear the log.
        _oplog.Clear();

        Console.WriteLine($"[store] flushed {touched.Count} game(s) across {docs.Count} file(s)");
        return touched.Count;
    }

    private string FileForGame(Guid gid)
    {
        if (!_byId.TryGetValue(gid, out var i)) return null;
        string plat = Str(Rows[i].PlatformIdx);
        return _platformFile.TryGetValue(plat, out var f) ? f : null;
    }

    // ── Game add / delete (Palier 3) ─────────────────────────────────────────
    /// <summary>Creates a new in-memory game row (grows the compact store) and logs an "add" op.
    /// The &lt;Game&gt; node itself is created at flush time from the accumulated field ops. Returns
    /// the new row index; <paramref name="id"/> is the minted GUID (reused on replay → idempotent).</summary>
    public int AddGameRow(string title, out Guid id)
    {
        id = Guid.NewGuid();
        int idx = GrowRow(id);
        Rows[idx].TitleIdx = InternRuntime(title);
        if (!ReadOnly && _oplog != null)
        {
            _oplog.Append("add", "Game", id.ToString(), null, null, null);
            _oplog.Append("modify", "Game", id.ToString(), null, "Title", title);
        }
        return idx;
    }

    // Replay path: recreate an added game's row from an "add" op (no re-logging).
    private void AddGameRowForReplay(Guid id) { if (!_byId.ContainsKey(id)) GrowRow(id); }

    private int GrowRow(Guid id)
    {
        int idx = Rows.Length;
        Array.Resize(ref Rows, idx + 1);
        Rows[idx] = new GameRow { Id = id, LaunchBoxDbId = -1, MaxPlayers = -1, ReleaseYear = -1 };
        if (_notes != null) { Array.Resize(ref _notes, idx + 1); _notes[idx] = null; }
        _byId[id] = idx;
        _addedIds.Add(id);
        return idx;
    }

    /// <summary>Moves an existing game to another platform: updates memory (+ the per-platform index)
    /// and logs a "move" op (field = old platform, value = new) so the flush relocates the &lt;Game&gt;
    /// node between Platform files, preserving all its fields.</summary>
    public void MoveGamePlatform(int i, string newPlatform)
    {
        if (i < 0 || i >= Rows.Length) return;
        string old = Str(Rows[i].PlatformIdx);
        newPlatform ??= "";
        if (string.Equals(old, newPlatform, StringComparison.OrdinalIgnoreCase)) return;
        MoveInMemory(i, newPlatform);
        if (!ReadOnly && _oplog != null) _oplog.Append("move", "Game", Rows[i].Id.ToString(), null, old, newPlatform);
    }

    // Memory + per-platform index only (used by MoveGamePlatform and by boot replay, which must not re-log).
    private void MoveInMemory(int i, string newPlatform)
    {
        string old = Str(Rows[i].PlatformIdx);
        if (string.Equals(old, newPlatform, StringComparison.OrdinalIgnoreCase)) return;
        if (_byPlatform.TryGetValue(old, out var ol)) ol.Remove(i);
        Rows[i].PlatformIdx = InternRuntime(newPlatform);
        if (!_byPlatform.TryGetValue(newPlatform, out var nl)) _byPlatform[newPlatform] = nl = new List<int>();
        if (!nl.Contains(i)) nl.Add(i);
    }

    /// <summary>Removes a game in memory and logs a "delete" op (carrying its platform so the flush
    /// can find the file). Returns false if unknown.</summary>
    public bool DeleteGameRow(Guid id)
    {
        if (!_byId.TryGetValue(id, out var i)) return false;
        string platform = Str(Rows[i].PlatformIdx);
        _byId.Remove(id);
        _addedIds.Remove(id);
        if (!ReadOnly && _oplog != null) _oplog.Append("delete", "Game", id.ToString(), null, "Platform", platform);
        return true;
    }

    /// <summary>The XML file for a platform; for a brand-new platform, creates an empty skeleton
    /// &lt;LaunchBox/&gt; file under the Platforms dir and remembers it. Null only on failure.</summary>
    private string PlatformFile(string platform)
    {
        if (string.IsNullOrEmpty(platform)) platform = "Unknown";
        if (_platformFile.TryGetValue(platform, out var f)) return f;
        try
        {
            string name = platform;
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            string path = Path.Combine(_platformsDir, name + ".xml");
            if (!File.Exists(path))
                File.WriteAllText(path, "<?xml version=\"1.0\" standalone=\"yes\"?>\n<LaunchBox>\n</LaunchBox>");
            _platformFile[platform] = path;
            return path;
        }
        catch (Exception ex) { Console.WriteLine("[store] PlatformFile failed for '" + platform + "': " + ex.Message); return null; }
    }

    // ── Child-entity write-back (AdditionalApplication / AlternateName / Mount / CustomField) ──
    // A game's child collection is the source of truth: any add/remove/edit records a single
    // "replace" op carrying the whole current collection as JSON. On flush we drop every existing
    // <Entity> with that GameID and re-emit from the JSON in canonical field order. Naturally
    // idempotent (replace = last-write-wins), and sidesteps the lack of stable IDs on alt-names /
    // mounts / custom fields. Caller mutates the in-memory list first, then calls this.
    private static readonly string[] _childEntities = { "AdditionalApplication", "AlternateName", "Mount", "CustomField" };
    internal static bool IsChildEntity(string e) => Array.IndexOf(_childEntities, e) >= 0;

    internal static readonly Dictionary<string, string[]> ChildFieldOrder = new(StringComparer.Ordinal)
    {
        ["AdditionalApplication"] = new[] { "Id", "GameID", "ApplicationPath", "Name", "CommandLine", "Developer", "Publisher", "Region", "Version", "Status", "EmulatorId", "Disc", "Priority", "PlayCount", "PlayTime", "AutoRunBefore", "AutoRunAfter", "UseEmulator", "UseDosBox", "WaitForExit", "SideA", "SideB", "ReleaseDate", "LastPlayed", "Installed" },
        ["AlternateName"] = new[] { "GameID", "Name", "Region" },
        ["Mount"] = new[] { "GameID", "DriveLetter", "Filesystem", "MountType", "Path", "Type" },
        ["CustomField"] = new[] { "GameID", "Name", "Value" },
    };

    /// <summary>Records the current (already-mutated) child collection of <paramref name="entity"/> for
    /// game <paramref name="gid"/> as a single replace op. Always usable in-memory; persists only when not ReadOnly.</summary>
    public void RecordChildReplace(Guid gid, string entity)
    {
        if (ReadOnly || _oplog == null) return;
        _oplog.Append("replace", entity, null, gid.ToString(), null, SerializeChildren(entity, gid));
    }

    private static string CB(bool v) => v ? "true" : "false";
    private string SerializeChildren(string entity, Guid gid)
    {
        var list = new List<Dictionary<string, string>>();
        string g = gid.ToString();
        switch (entity)
        {
            case "AdditionalApplication":
                foreach (var a in AddAppsFor(gid))
                    list.Add(new Dictionary<string, string>
                    {
                        ["Id"] = a.Id, ["GameID"] = g, ["ApplicationPath"] = a.ApplicationPath, ["Name"] = a.Name,
                        ["CommandLine"] = a.CommandLine, ["Developer"] = a.Developer, ["Publisher"] = a.Publisher,
                        ["Region"] = a.Region, ["Version"] = a.Version, ["Status"] = a.Status, ["EmulatorId"] = a.EmulatorId,
                        ["Disc"] = a.Disc?.ToString(CultureInfo.InvariantCulture),
                        ["Priority"] = a.Priority.ToString(CultureInfo.InvariantCulture),
                        ["PlayCount"] = a.PlayCount.ToString(CultureInfo.InvariantCulture),
                        ["PlayTime"] = a.PlayTime.ToString(CultureInfo.InvariantCulture),
                        ["AutoRunBefore"] = CB(a.AutoRunBefore), ["AutoRunAfter"] = CB(a.AutoRunAfter),
                        ["UseEmulator"] = CB(a.UseEmulator), ["UseDosBox"] = CB(a.UseDosBox), ["WaitForExit"] = CB(a.WaitForExit),
                        ["SideA"] = CB(a.SideA), ["SideB"] = CB(a.SideB),
                        ["ReleaseDate"] = a.ReleaseDate?.ToString("o", CultureInfo.InvariantCulture),
                        ["LastPlayed"] = a.LastPlayed?.ToString("o", CultureInfo.InvariantCulture),
                        ["Installed"] = a.Installed.HasValue ? CB(a.Installed.Value) : null,
                    });
                break;
            case "AlternateName":
                foreach (var a in AltNamesFor(gid)) list.Add(new() { ["GameID"] = g, ["Name"] = a.Name, ["Region"] = a.Region });
                break;
            case "Mount":
                foreach (var m in MountsFor(gid)) list.Add(new() { ["GameID"] = g, ["DriveLetter"] = m.DriveLetter.ToString(), ["Filesystem"] = m.Filesystem, ["MountType"] = m.MountType, ["Path"] = m.Path, ["Type"] = m.Type });
                break;
            case "CustomField":
                foreach (var c in CustomFieldsFor(gid)) list.Add(new() { ["GameID"] = g, ["Name"] = c.Name, ["Value"] = c.Value });
                break;
        }
        return JsonSerializer.Serialize(list);
    }

    /// <summary>Rebuilds the in-memory child list of <paramref name="entity"/> for <paramref name="gid"/>
    /// from a replace op's JSON (used at boot replay).</summary>
    public void ApplyChildReplace(string entity, Guid gid, string json)
    {
        List<Dictionary<string, string>> list;
        try { list = JsonSerializer.Deserialize<List<Dictionary<string, string>>>(json) ?? new(); } catch { return; }
        string G(Dictionary<string, string> d, string k) => d.TryGetValue(k, out var v) ? v : null;
        switch (entity)
        {
            case "AdditionalApplication":
                _addApps[gid] = list.Select(r => new AddApp
                {
                    Id = G(r, "Id"), ApplicationPath = G(r, "ApplicationPath"), Name = G(r, "Name"), CommandLine = G(r, "CommandLine"),
                    Developer = G(r, "Developer"), Publisher = G(r, "Publisher"), Region = G(r, "Region"), Version = G(r, "Version"),
                    Status = G(r, "Status"), EmulatorId = G(r, "EmulatorId"), Disc = ParseIntN(G(r, "Disc")), Priority = ParseInt(G(r, "Priority"), 0),
                    PlayCount = ParseInt(G(r, "PlayCount"), 0), PlayTime = ParseInt(G(r, "PlayTime"), 0),
                    AutoRunBefore = ParseBool(G(r, "AutoRunBefore")), AutoRunAfter = ParseBool(G(r, "AutoRunAfter")),
                    UseEmulator = ParseBool(G(r, "UseEmulator")), UseDosBox = ParseBool(G(r, "UseDosBox")), WaitForExit = ParseBool(G(r, "WaitForExit")),
                    SideA = ParseBool(G(r, "SideA")), SideB = ParseBool(G(r, "SideB")),
                    ReleaseDate = ParseDateN(G(r, "ReleaseDate")), LastPlayed = ParseDateN(G(r, "LastPlayed")), Installed = ParseBoolN(G(r, "Installed")),
                }).ToList();
                break;
            case "AlternateName":
                _altNames[gid] = list.Select(r => new AltName { Name = G(r, "Name"), Region = G(r, "Region") }).ToList();
                break;
            case "Mount":
                _mounts[gid] = list.Select(r => { var dl = G(r, "DriveLetter"); return new GameMount { DriveLetter = !string.IsNullOrEmpty(dl) ? char.ToUpperInvariant(dl[0]) : 'C', Filesystem = G(r, "Filesystem"), MountType = G(r, "MountType"), Path = G(r, "Path"), Type = G(r, "Type") }; }).ToList();
                break;
            case "CustomField":
                _customFields[gid] = list.Select(r => new CustomField { Name = G(r, "Name"), Value = G(r, "Value") }).ToList();
                break;
        }
    }

    // Apply a child replace op onto a loaded DOM: drop existing <entity> for this GameID, re-emit.
    private static void ApplyChildReplaceToDoc(XDocument doc, string entity, string gameId, string json)
    {
        foreach (var e in doc.Root.Elements(entity).Where(e => (string)e.Element("GameID") == gameId).ToList()) e.Remove();
        List<Dictionary<string, string>> list;
        try { list = JsonSerializer.Deserialize<List<Dictionary<string, string>>>(json) ?? new(); } catch { return; }
        var order = ChildFieldOrder[entity];
        foreach (var rec in list)
        {
            var el = new XElement(entity);
            foreach (var fld in order)
                if (rec.TryGetValue(fld, out var v) && !string.IsNullOrEmpty(v))
                    el.Add(new XElement(fld, v));
            doc.Root.Add(el);
        }
    }

    // Before overwriting any XML, snapshot the pristine originals into a small timestamped zip —
    // ONLY the dirty files, with their sub-path relative to <LB>\Data preserved (so identically
    // named files in different folders don't collide). Lives under <LB>\Backups\LiteBox. Unlike
    // LB's automatic backups this is targeted (a few KB), not a full Data dump. Best-effort:
    // any failure is logged and skipped — a backup problem must never block the write.
    private void BackupBeforeWrite(ICollection<string> files)
    {
        if (files == null || files.Count == 0) return;
        try
        {
            string dataRoot = Path.GetDirectoryName(_platformsDir);                       // <LB>\Data
            string lbRoot = dataRoot != null ? Path.GetDirectoryName(dataRoot) : null;    // <LB>
            if (string.IsNullOrEmpty(dataRoot) || string.IsNullOrEmpty(lbRoot)) return;
            string dir = Path.Combine(lbRoot, "Backups", "LiteBox");
            Directory.CreateDirectory(dir);

            var now = DateTime.Now;
            string zipPath = Path.Combine(dir, $"LiteBox Data Backup {now:yyyy-MM-dd HH-mm-ss}.zip");
            int n = 1;
            while (File.Exists(zipPath))   // two flushes in the same second
                zipPath = Path.Combine(dir, $"LiteBox Data Backup {now:yyyy-MM-dd HH-mm-ss} ({n++}).zip");

            int added = 0;
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
                foreach (var f in files)
                {
                    if (!File.Exists(f)) continue;
                    string rel = Path.GetRelativePath(dataRoot, f).Replace('\\', '/');
                    zip.CreateEntryFromFile(f, rel, CompressionLevel.Optimal);
                    added++;
                }

            PruneBackups(dir, 50);
            Console.WriteLine($"[store] backed up {added} file(s) → {zipPath}");
        }
        catch (Exception ex) { Console.WriteLine("[store] backup skipped: " + ex.Message); }
    }

    private static void PruneBackups(string dir, int keep)
    {
        try
        {
            var files = Directory.GetFiles(dir, "LiteBox Data Backup *.zip");
            if (files.Length <= keep) return;
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);   // timestamped name sorts chronologically
            for (int i = 0; i < files.Length - keep; i++) { try { File.Delete(files[i]); } catch { } }
        }
        catch { }
    }

    private static void ApplyModify(XElement ge, string field, string value)
    {
        if (string.IsNullOrEmpty(field)) return;
        if (field == "StarRatingFloat")   // keep the StarRating/StarRatingFloat pair in sync
        {
            SetOrRemove(ge, "StarRatingFloat", value);
            SetOrRemove(ge, "StarRating", string.IsNullOrEmpty(value)
                ? "" : ((int)Math.Round(ParseFloat(value))).ToString(CultureInfo.InvariantCulture));
            return;
        }
        SetOrRemove(ge, field, value);
    }

    // LB omits empty/default fields, so a cleared value removes the element rather than writing it empty.
    private static void SetOrRemove(XElement parent, string name, string value)
    {
        var e = parent.Element(name);
        if (string.IsNullOrEmpty(value)) { e?.Remove(); return; }
        if (e == null) parent.Add(new XElement(name, value)); else e.Value = value;
    }

    // Canonical-ish field order for a newly created <Game> (modelled fields; LB tolerates order).
    private static readonly string[] _gameAddOrder =
    {
        "ID", "Title", "SortTitle", "Platform", "ApplicationPath", "Emulator", "Developer", "Publisher",
        "Genre", "Region", "Rating", "Status", "PlayMode", "Version", "Series", "Source", "ReleaseType",
        "RootFolder", "CloneOf", "Progress", "WikipediaURL", "VideoUrl", "Notes", "CommandLine",
        "ConfigurationCommandLine", "ConfigurationPath", "DosBoxConfigurationPath", "CustomDosBoxVersionPath",
        "ScummVMGameDataFolderPath", "ScummVMGameType", "VideoPath", "ThemeVideoPath", "ManualPath", "MusicPath",
        "RetroAchievementsHash", "DateAdded", "DateModified", "ReleaseDate", "LastPlayedDate", "StarRatingFloat",
        "StarRating", "CommunityStarRating", "CommunityStarRatingTotalVotes", "DatabaseID", "MaxPlayers",
        "StartupLoadDelay", "PlayCount", "PlayTime", "Favorite", "Hide", "Broken", "Completed", "Installed",
        "Portable", "UseDosBox", "UseScummVM", "ScummVMAspectCorrection", "ScummVMFullscreen", "UseStartupScreen",
        "OverrideDefaultStartupScreenSettings", "HideAllNonExclusiveFullscreenWindows", "HideMouseCursorInGame",
        "DisableShutdownScreen", "AggressiveWindowHiding",
    };

    // Builds a fresh <Game> element from an added game's accumulated field map, in canonical order.
    private static XElement BuildGameElement(Dictionary<string, string> fld)
    {
        if (fld.TryGetValue("StarRatingFloat", out var srf) && !string.IsNullOrEmpty(srf) && !fld.ContainsKey("StarRating"))
            fld["StarRating"] = ((int)Math.Round(ParseFloat(srf))).ToString(CultureInfo.InvariantCulture);
        return BuildElement("Game", fld, _gameAddOrder);
    }

    // ── Top-level non-game entity write-back (Emulator, later Platform/Category/Playlist) ─────
    // These live as keyed <Entity> nodes in a single file; we record/apply modify/add/delete by key,
    // and ID-less collections (EmulatorPlatform) via the same "replace" pattern as child entities.
    private string DataDir => Path.GetDirectoryName(_platformsDir);
    private string EmulatorsFile => DataDir != null ? Path.Combine(DataDir, "Emulators.xml") : null;
    private string PlatformsFile => DataDir != null ? Path.Combine(DataDir, "Platforms.xml") : null;

    // Keyed top-level entities → (file, key child element, canonical field order for adds).
    internal static bool IsTopLevelEntity(string e) => e == "Emulator" || e == "Platform" || e == "PlatformCategory";
    private (string file, string key, string[] order) TopLevelSpec(string entity) => entity switch
    {
        "Emulator" => (EmulatorsFile, "ID", _emulatorAddOrder),
        "Platform" => (PlatformsFile, "Name", _platformAddOrder),
        "PlatformCategory" => (PlatformsFile, "Name", _platformCategoryAddOrder),
        _ => (null, null, null),
    };

    public void RecordEntityModify(string entity, string id, string field, string value)
    { if (!ReadOnly && _oplog != null) _oplog.Append("modify", entity, id, null, field, value); }
    public void RecordEntityAdd(string entity, string id)
    { if (!ReadOnly && _oplog != null) _oplog.Append("add", entity, id, null, null, null); }
    public void RecordEntityDelete(string entity, string id)
    { if (!ReadOnly && _oplog != null) _oplog.Append("delete", entity, id, null, null, null); }
    /// <summary>Replace the whole &lt;EmulatorPlatform&gt; collection of one emulator (its rows have no
    /// stable ID). <paramref name="json"/> is a list of field maps.</summary>
    public void RecordEntityReplace(string entity, string parentId, string json)
    { if (!ReadOnly && _oplog != null) _oplog.Append("replace", entity, null, parentId, null, json); }

    private static readonly string[] _emulatorAddOrder =
    {
        "ApplicationPath", "CommandLine", "DefaultPlatform", "ID", "Title", "NoQuotes", "NoSpace", "HideConsole",
        "FileNameWithoutExtensionAndPath", "AutoHotkeyScript", "AutoExtract", "UseStartupScreen",
        "HideAllNonExclusiveFullscreenWindows", "StartupLoadDelay", "HideMouseCursorInGame", "DisableShutdownScreen",
        "AggressiveWindowHiding", "PauseAutoHotkeyScript", "ResumeAutoHotkeyScript", "LoadStateAutoHotkeyScript",
        "SaveStateAutoHotkeyScript", "ResetAutoHotkeyScript", "SwapDiscsAutoHotkeyScript", "ExitAutoHotkeyScript",
        "EnableHardcoreAchievements",
    };
    private static readonly string[] _emuPlatOrder = { "Emulator", "Platform", "CommandLine", "Default", "M3uDiscLoadEnabled", "AutoExtract" };
    private static readonly string[] _platformAddOrder =
    {
        "Name", "NestedName", "ReleaseDate", "Developer", "Manufacturer", "Cpu", "Memory", "Graphics", "Sound",
        "Display", "Media", "MaxControllers", "Folder", "Notes", "VideosFolder", "FrontImagesFolder",
        "BackImagesFolder", "ClearLogoImagesFolder", "FanartImagesFolder", "ScreenshotImagesFolder",
        "BannerImagesFolder", "SteamBannerImagesFolder", "ManualsFolder", "MusicFolder", "ScrapeAs", "VideoPath",
        "ImageType", "SortTitle", "LastGameId", "BigBoxView", "BigBoxTheme", "HideInBigBox",
    };
    private static readonly string[] _platformCategoryAddOrder = { "Name", "NestedName", "Notes", "VideoPath", "SortTitle", "HideInBigBox" };
    private static readonly string[] _playlistGameOrder = { "GameId", "LaunchBoxDbId", "GameTitle", "GameFileName", "GamePlatform", "ManualOrder" };
    private static readonly string[] _playlistFilterOrder = { "Value", "FieldKey", "ComparisonTypeKey" };

    // Playlists live one-per-file under Data\Playlists\; ops carry the source file in ParentId.
    public void RecordPlaylistModify(string playlistId, string file, string field, string value)
    { if (!ReadOnly && _oplog != null) _oplog.Append("modify", "Playlist", playlistId, file, field, value); }
    public void RecordPlaylistAdd(string playlistId, string file)
    { if (!ReadOnly && _oplog != null) _oplog.Append("add", "Playlist", playlistId, file, null, null); }
    public void RecordPlaylistDelete(string playlistId, string file)
    { if (!ReadOnly && _oplog != null) _oplog.Append("delete", "Playlist", playlistId, file, null, null); }
    public void RecordPlaylistChildReplace(string entity, string playlistId, string file, string json)
    { if (!ReadOnly && _oplog != null) _oplog.Append("replace", entity, playlistId, file, null, json); }
    /// <summary>The conventional file for a new playlist: Data\Playlists\&lt;sanitised name&gt;.xml.</summary>
    public string PlaylistFileFor(string name)
    {
        if (DataDir == null) return null;
        string n = string.IsNullOrEmpty(name) ? "Playlist" : name;
        foreach (var c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
        return Path.Combine(DataDir, "Playlists", n + ".xml");
    }

    private static XElement FindOrCreatePlaylist(XDocument d, string pid)
    {
        var n = d.Root.Elements("Playlist").FirstOrDefault(e => (string)e.Element("PlaylistId") == pid);
        if (n == null) { n = new XElement("Playlist", new XElement("PlaylistId", pid)); d.Root.Add(n); }
        return n;
    }

    private static XElement BuildElement(string name, Dictionary<string, string> fld, string[] order)
    {
        var el = new XElement(name);
        var emitted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in order)
            if (fld.TryGetValue(f, out var v) && !string.IsNullOrEmpty(v)) { el.Add(new XElement(f, v)); emitted.Add(f); }
        foreach (var kv in fld)
            if (!emitted.Contains(kv.Key) && !string.IsNullOrEmpty(kv.Value)) el.Add(new XElement(kv.Key, kv.Value));
        return el;
    }

    private static XElement FindByChild(XDocument doc, string elem, string childName, string childValue)
        => doc.Root.Elements(elem).FirstOrDefault(e => (string)e.Element(childName) == childValue);

    private static void ApplyCollectionReplaceToDoc(XDocument doc, string elem, string keyChild, string keyValue, string[] order, string json)
    {
        foreach (var e in doc.Root.Elements(elem).Where(e => (string)e.Element(keyChild) == keyValue).ToList()) e.Remove();
        List<Dictionary<string, string>> list;
        try { list = JsonSerializer.Deserialize<List<Dictionary<string, string>>>(json) ?? new(); } catch { return; }
        foreach (var rec in list) doc.Root.Add(BuildElement(elem, rec, order));
    }

    // XML element name → applies the value into Rows[i]. Used by setters (live) and replay (boot).
    private static readonly Dictionary<string, Action<GameStore, int, string>> _gameFieldParsers = BuildParsers();

    private static void SetFlag(GameStore s, int i, int bit, bool on)
    { s.Rows[i].Flags = on ? (s.Rows[i].Flags | bit) : (s.Rows[i].Flags & ~bit); }

    private static Dictionary<string, Action<GameStore, int, string>> BuildParsers()
    {
        var d = new Dictionary<string, Action<GameStore, int, string>>(StringComparer.Ordinal);
        // strings
        d["Title"]                     = (s, i, v) => s.Rows[i].TitleIdx = s.InternRuntime(v);
        d["Platform"]                  = (s, i, v) => s.Rows[i].PlatformIdx = s.InternRuntime(v);   // settable on added games only
        d["SortTitle"]                 = (s, i, v) => s.Rows[i].SortTitleIdx = s.InternRuntime(v);
        d["ApplicationPath"]           = (s, i, v) => s.Rows[i].AppPathIdx = s.InternRuntime(v);
        d["Emulator"]                  = (s, i, v) => s.Rows[i].EmulatorIdIdx = s.InternRuntime(v);
        d["Developer"]                 = (s, i, v) => s.Rows[i].DeveloperIdx = s.InternRuntime(v);
        d["Publisher"]                 = (s, i, v) => s.Rows[i].PublisherIdx = s.InternRuntime(v);
        d["Genre"]                     = (s, i, v) => s.Rows[i].GenresIdx = s.InternRuntime(v);
        d["Region"]                    = (s, i, v) => s.Rows[i].RegionIdx = s.InternRuntime(v);
        d["Rating"]                    = (s, i, v) => s.Rows[i].RatingIdx = s.InternRuntime(v);
        d["Status"]                    = (s, i, v) => s.Rows[i].StatusIdx = s.InternRuntime(v);
        d["PlayMode"]                  = (s, i, v) => s.Rows[i].PlayModeIdx = s.InternRuntime(v);
        d["Version"]                   = (s, i, v) => s.Rows[i].VersionIdx = s.InternRuntime(v);
        d["WikipediaURL"]              = (s, i, v) => s.Rows[i].WikipediaUrlIdx = s.InternRuntime(v);
        d["VideoUrl"]                  = (s, i, v) => s.Rows[i].VideoUrlIdx = s.InternRuntime(v);
        d["CommandLine"]               = (s, i, v) => s.Rows[i].CommandLineIdx = s.InternRuntime(v);
        d["ConfigurationCommandLine"]  = (s, i, v) => s.Rows[i].ConfigCmdIdx = s.InternRuntime(v);
        d["ConfigurationPath"]         = (s, i, v) => s.Rows[i].ConfigPathIdx = s.InternRuntime(v);
        d["DosBoxConfigurationPath"]   = (s, i, v) => s.Rows[i].DosBoxCfgIdx = s.InternRuntime(v);
        d["CustomDosBoxVersionPath"]   = (s, i, v) => s.Rows[i].CustomDosBoxIdx = s.InternRuntime(v);
        d["ScummVMGameDataFolderPath"] = (s, i, v) => s.Rows[i].ScummDataIdx = s.InternRuntime(v);
        d["ScummVMGameType"]           = (s, i, v) => s.Rows[i].ScummTypeIdx = s.InternRuntime(v);
        d["Series"]                    = (s, i, v) => s.Rows[i].SeriesIdx = s.InternRuntime(v);
        d["Source"]                    = (s, i, v) => s.Rows[i].SourceIdx = s.InternRuntime(v);
        d["ReleaseType"]               = (s, i, v) => s.Rows[i].ReleaseTypeIdx = s.InternRuntime(v);
        d["RootFolder"]                = (s, i, v) => s.Rows[i].RootFolderIdx = s.InternRuntime(v);
        d["CloneOf"]                   = (s, i, v) => s.Rows[i].CloneOfIdx = s.InternRuntime(v);
        d["Progress"]                  = (s, i, v) => s.Rows[i].ProgressIdx = s.InternRuntime(v);
        d["RetroAchievementsHash"]     = (s, i, v) => s.Rows[i].RaHashIdx = s.InternRuntime(v);
        d["VideoPath"]                 = (s, i, v) => s.Rows[i].VideoPathIdx = s.InternRuntime(v);
        d["ThemeVideoPath"]            = (s, i, v) => s.Rows[i].ThemeVideoPathIdx = s.InternRuntime(v);
        d["ManualPath"]                = (s, i, v) => s.Rows[i].ManualPathIdx = s.InternRuntime(v);
        d["MusicPath"]                 = (s, i, v) => s.Rows[i].MusicPathIdx = s.InternRuntime(v);
        d["Notes"]                     = (s, i, v) => { if (s._notes != null && i < s._notes.Length) s._notes[i] = v; };
        // numerics
        d["StarRatingFloat"]           = (s, i, v) => s.Rows[i].StarRatingFloat = ParseFloat(v);
        d["StarRating"]                = (s, i, v) => s.Rows[i].StarRatingFloat = ParseFloat(v);
        d["PlayCount"]                 = (s, i, v) => s.Rows[i].PlayCount = ParseInt(v, 0);
        d["PlayTime"]                  = (s, i, v) => s.Rows[i].PlayTime = ParseInt(v, 0);
        d["CommunityStarRatingTotalVotes"] = (s, i, v) => s.Rows[i].CommunityVotes = ParseInt(v, 0);
        d["CommunityStarRating"]       = (s, i, v) => s.Rows[i].CommunityStarRating = ParseFloat(v);
        d["DatabaseID"]                = (s, i, v) => s.Rows[i].LaunchBoxDbId = ParseInt(v, -1);
        d["MaxPlayers"]                = (s, i, v) => s.Rows[i].MaxPlayers = ParseInt(v, -1);
        d["StartupLoadDelay"]          = (s, i, v) => s.Rows[i].StartupLoadDelay = ParseInt(v, 0);
        // dates
        d["DateAdded"]                 = (s, i, v) => s.Rows[i].DateAddedTicks = ParseTicks(v);
        d["DateModified"]              = (s, i, v) => s.Rows[i].DateModifiedTicks = ParseTicks(v);
        d["LastPlayedDate"]            = (s, i, v) => s.Rows[i].LastPlayedTicks = ParseTicks(v);
        d["ReleaseDate"]               = (s, i, v) =>
        {
            s.Rows[i].ReleaseDateTicks = ParseTicks(v);
            s.Rows[i].ReleaseYear = s.Rows[i].ReleaseDateTicks != 0 ? new DateTime(s.Rows[i].ReleaseDateTicks).Year : -1;
        };
        // bools
        d["Favorite"]                  = (s, i, v) => s.Rows[i].Favorite = ParseBool(v);
        d["Hide"]                      = (s, i, v) => s.Rows[i].Hide = ParseBool(v);
        d["Broken"]                    = (s, i, v) => s.Rows[i].Broken = ParseBool(v);
        d["Completed"]                 = (s, i, v) => s.Rows[i].Completed = ParseBool(v);
        d["Installed"]                 = (s, i, v) => s.Rows[i].Installed = ParseTri(v);
        // packed flags
        d["UseDosBox"]                 = (s, i, v) => SetFlag(s, i, GFlags.UseDosBox, ParseBool(v));
        d["UseScummVM"]                = (s, i, v) => SetFlag(s, i, GFlags.UseScummVm, ParseBool(v));
        d["ScummVMAspectCorrection"]   = (s, i, v) => SetFlag(s, i, GFlags.ScummAspect, ParseBool(v));
        d["ScummVMFullscreen"]         = (s, i, v) => SetFlag(s, i, GFlags.ScummFull, ParseBool(v));
        d["Portable"]                  = (s, i, v) => SetFlag(s, i, GFlags.Portable, ParseBool(v));
        d["UseStartupScreen"]          = (s, i, v) => SetFlag(s, i, GFlags.UseStartup, ParseBool(v));
        d["OverrideDefaultStartupScreenSettings"] = (s, i, v) => SetFlag(s, i, GFlags.OverrideStartup, ParseBool(v));
        d["HideAllNonExclusiveFullscreenWindows"] = (s, i, v) => SetFlag(s, i, GFlags.HideNonExclusive, ParseBool(v));
        d["HideMouseCursorInGame"]     = (s, i, v) => SetFlag(s, i, GFlags.HideMouse, ParseBool(v));
        d["DisableShutdownScreen"]     = (s, i, v) => SetFlag(s, i, GFlags.DisableShutdown, ParseBool(v));
        d["AggressiveWindowHiding"]    = (s, i, v) => SetFlag(s, i, GFlags.AggressiveHiding, ParseBool(v));
        return d;
    }

    // ── Atomic disk write (tmp → replace) ────────────────────────────────────
    private static void ReplaceAtomic(string tmp, string dest)
    {
        try
        {
            if (File.Exists(dest)) File.Replace(tmp, dest, null);
            else File.Move(tmp, dest);
        }
        catch { try { File.Copy(tmp, dest, true); File.Delete(tmp); } catch { } }
    }

    // ── Stats ────────────────────────────────────────────────────────────────
    public void LogStats()
    {
        long rowBytes = (long)Rows.Length * System.Runtime.InteropServices.Marshal.SizeOf<GameRow>();
        long poolChars = Pool.Sum(s => (long)(s?.Length ?? 0));
        long notesChars = _notes?.Sum(s => (long)(s?.Length ?? 0)) ?? 0;
        Console.WriteLine($"[store] games={Rows.Length} platforms={_byPlatform.Count} pool={Pool.Count} entries (~{poolChars * 2 / 1048576.0:F1}MB chars) " +
                          $"rows~{rowBytes / 1048576.0:F1}MB notes~{notesChars * 2 / 1048576.0:F1}MB ({(_notes == null ? "dropped" : "loaded")})");

        // Footprint of the newer full-field / sub-entity stores (the bits that grew memory).
        int exGames = _extra.Count; long exEntries = 0, exChars = 0;
        foreach (var m in _extra.Values) { exEntries += m.Count; foreach (var kv in m) exChars += (kv.Key?.Length ?? 0) + (kv.Value?.Length ?? 0); }
        int seGames = _subEntities.Count; long seEntries = 0, seChars = 0;
        foreach (var byType in _subEntities.Values) foreach (var lst in byType.Values) foreach (var rec in lst) { seEntries += rec.Count; foreach (var kv in rec) seChars += (kv.Key?.Length ?? 0) + (kv.Value?.Length ?? 0); }
        // Strings are pooled (shared), so the real cost is the dict structure (~32 B/field) not the chars.
        Console.WriteLine($"[store] extra: {exGames} games, {exEntries} fields (strings pooled; ~{exEntries * 32 / 1048576.0:F1}MB dict overhead)  " +
                          $"subEntities: {seGames} games, {seEntries} fields");
    }

    // ── Parsers ──────────────────────────────────────────────────────────────
    private static int ParseInt(string s, int def) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : def;
    private static int? ParseIntN(string s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : (int?)null;
    private static bool? ParseBoolN(string s) => string.IsNullOrWhiteSpace(s) ? (bool?)null : s.Equals("true", StringComparison.OrdinalIgnoreCase);
    private static DateTime? ParseDateN(string s) => DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d) ? d : (DateTime?)null;
    private static float ParseFloat(string s) => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0f;
    private static bool ParseBool(string s) => s != null && s.Equals("true", StringComparison.OrdinalIgnoreCase);
    private static byte ParseTri(string s) => s == null ? (byte)0 : (s.Equals("true", StringComparison.OrdinalIgnoreCase) ? (byte)2 : (byte)1);
    private static long ParseTicks(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0;
        return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt) ? dt.Ticks : 0;
    }
}

/// <summary>A game's additional application (disc/version), from the Platform XML.</summary>
internal sealed class AddApp
{
    public string Id, ApplicationPath, Name, CommandLine, Developer, Publisher, Region, Version, Status, EmulatorId;
    public int? Disc;
    public int Priority, PlayCount, PlayTime;
    public bool AutoRunBefore, AutoRunAfter, UseEmulator, UseDosBox, WaitForExit, SideA, SideB;
    public DateTime? ReleaseDate, LastPlayed;
    public bool? Installed;
}

/// <summary>A game's alternate (regional) name, from the Platform XML.</summary>
internal sealed class AltName
{
    public string Name, Region;
}

/// <summary>A DOSBox additional mount (folder or disk image), from the Platform XML.</summary>
internal sealed class GameMount
{
    public char DriveLetter;
    public string Filesystem, MountType, Path, Type;
}

/// <summary>A game's custom field (name/value), from the Platform XML.</summary>
internal sealed class CustomField
{
    public string Name, Value;
}
