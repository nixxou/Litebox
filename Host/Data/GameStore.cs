// Two-tier, memory-frugal game store loaded from LaunchBox's native Platform
// XMLs (LB\Data\Platforms\*.xml) — the authoritative source (user edits live
// there), with ZERO dependency on ExtendDB or its SQLite.
//
// Tier 1 (essential, always resident): a blittable `GameRow[]` (native fields +
//   int indices into a deduped string pool). No per-game heap object → the GC
//   doesn't even scan it. This is what keeps IGame functional during a game
//   launch (the launching plugin reads Title/ApplicationPath/EmulatorId here).
//
// Tier 2 (optional, droppable): `Notes` text in a separate string[] that can be
//   dropped at game launch (DropOptional) and reloaded after (ReloadOptional /
//   lazy re-parse). Wikipedia/Video URLs are short → kept in the pool.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
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
                if (en != "Game" && en != "AdditionalApplication" && en != "AlternateName" && en != "Mount" && en != "CustomField") { reader.Read(); continue; }

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
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }

    /// <summary>Re-read the optional tier from the XMLs (after a game exits).</summary>
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
                if (reader.NodeType != XmlNodeType.Element || reader.Name != "Game") { reader.Read(); continue; }
                XElement g;
                try { g = (XElement)XNode.ReadFrom(reader); } catch { reader.Read(); continue; }
                if (Guid.TryParse(g.Element("ID")?.Value, out var id) && _byId.TryGetValue(id, out var i))
                    notes[i] = g.Element("Notes")?.Value;
            }
        }
        _notes = notes;
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

    private readonly HashSet<string> _warnedFields = new(StringComparer.Ordinal);
    private void ApplyFieldToRow(int i, string xmlName, string value)
    {
        if (_gameFieldParsers.TryGetValue(xmlName, out var apply)) { try { apply(this, i, value); } catch { } }
        else if (_warnedFields.Add(xmlName)) Console.WriteLine("[store] write-back: unmapped field '" + xmlName + "' (ignored)");
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
            foreach (var op in ops)
            {
                if (op.OpType != "modify" || op.Entity != "Game") continue;
                if (Guid.TryParse(op.Id, out var gid) && _byId.TryGetValue(gid, out var i))
                    ApplyFieldToRow(i, op.Field, op.Value);
            }
            Console.WriteLine($"[store] recovered op-log ({ops.Count} op(s))");
        }
        if (!ReadOnly && !IsLaunchBoxRunning()) FlushOpsToXml();
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
            var d = XDocument.Load(file);
            docs[file] = d;
            var byId = new Dictionary<Guid, XElement>();
            foreach (var ge in d.Root.Elements("Game"))
                if (Guid.TryParse((string)ge.Element("ID"), out var gid)) byId[gid] = ge;
            index[file] = byId;
        }

        foreach (var op in ops)
        {
            try
            {
                if (op.Entity != "Game" || !Guid.TryParse(op.Id, out var gid)) continue;   // child entities: future
                string file = FileForGame(gid);
                if (file == null || !File.Exists(file)) continue;                           // unresolved → drop
                EnsureDoc(file);
                var ge = index[file].TryGetValue(gid, out var el) ? el : null;
                if (op.OpType == "delete")
                { if (ge != null) { ge.Remove(); index[file].Remove(gid); touched.Add(gid); } continue; }
                if (op.OpType != "modify" || ge == null) continue;
                ApplyModify(ge, op.Field, op.Value);
                touched.Add(gid);
            }
            catch (Exception ex) { Console.WriteLine("[store] apply op seq=" + op.Seq + ": " + ex.Message); }
        }

        if (docs.Count == 0) { _oplog.Clear(); return 0; }

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

    // XML element name → applies the value into Rows[i]. Used by setters (live) and replay (boot).
    private static readonly Dictionary<string, Action<GameStore, int, string>> _gameFieldParsers = BuildParsers();

    private static void SetFlag(GameStore s, int i, int bit, bool on)
    { s.Rows[i].Flags = on ? (s.Rows[i].Flags | bit) : (s.Rows[i].Flags & ~bit); }

    private static Dictionary<string, Action<GameStore, int, string>> BuildParsers()
    {
        var d = new Dictionary<string, Action<GameStore, int, string>>(StringComparer.Ordinal);
        // strings
        d["Title"]                     = (s, i, v) => s.Rows[i].TitleIdx = s.InternRuntime(v);
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
    }

    // ── Parsers ──────────────────────────────────────────────────────────────
    private static int ParseInt(string s, int def) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : def;
    private static int? ParseIntN(string s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : (int?)null;
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
