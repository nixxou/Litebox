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
using System.Globalization;
using System.IO;
using System.Linq;
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
    public string[] Pool = { "" };                 // index 0 = empty
    private string[] _notes;                        // tier 2 (droppable), index-aligned with Rows
    private string _platformsDir;

    private readonly Dictionary<Guid, int> _byId = new();
    public IReadOnlyDictionary<Guid, int> ById => _byId;

    // platform name -> game row indices
    private readonly Dictionary<string, List<int>> _byPlatform = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, List<int>> ByPlatform => _byPlatform;

    // platform name -> source XML file (for write-back)
    private readonly Dictionary<string, string> _platformFile = new(StringComparer.OrdinalIgnoreCase);

    // rows whose essential fields were mutated and not yet flushed to XML
    private readonly HashSet<int> _dirty = new();

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
    public string Str(int idx) => (idx > 0 && idx < Pool.Length) ? Pool[idx] : "";
    public string NotesFor(int i) => (_notes != null && i >= 0 && i < _notes.Length) ? _notes[i] : null;
    public bool OptionalLoaded => _notes != null;

    // ── Load ────────────────────────────────────────────────────────────────
    public static GameStore Load(string platformsDir)
    {
        var s = new GameStore { _platformsDir = platformsDir };
        s.Build(includeNotes: true);
        return s;
    }

    private void Build(bool includeNotes)
    {
        var pool = new List<string> { "" };
        var poolMap = new Dictionary<string, int>(StringComparer.Ordinal) { [""] = 0 };
        int Intern(string v)
        {
            if (string.IsNullOrEmpty(v)) return 0;
            if (poolMap.TryGetValue(v, out var idx)) return idx;
            idx = pool.Count; pool.Add(v); poolMap[v] = idx; return idx;
        }

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
        Pool = pool.ToArray();
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

    // ── Mutations + write-back ───────────────────────────────────────────────
    public void SetFavorite(int i, bool v)
    {
        if (i < 0 || i >= Rows.Length || Rows[i].Favorite == v) return;
        Rows[i].Favorite = v;
        lock (_dirty) _dirty.Add(i);
    }

    public void SetStarRating(int i, float v)
    {
        if (i < 0 || i >= Rows.Length || Math.Abs(Rows[i].StarRatingFloat - v) < 0.001f) return;
        Rows[i].StarRatingFloat = v;
        lock (_dirty) _dirty.Add(i);
    }

    /// <summary>Writes mutated rows back into their source Platform XML. Returns rows written.</summary>
    public int Flush()
    {
        int[] dirty;
        lock (_dirty) { if (_dirty.Count == 0) return 0; dirty = _dirty.ToArray(); _dirty.Clear(); }

        int written = 0;
        foreach (var grp in dirty.GroupBy(i => Str(Rows[i].PlatformIdx)))
        {
            if (!_platformFile.TryGetValue(grp.Key, out var file) || !File.Exists(file)) continue;
            try
            {
                var doc = XDocument.Load(file);
                var want = grp.ToDictionary(i => Rows[i].Id);
                foreach (var ge in doc.Root.Elements("Game"))
                {
                    if (!Guid.TryParse((string)ge.Element("ID"), out var id) || !want.TryGetValue(id, out var i)) continue;
                    SetEl(ge, "Favorite", Rows[i].Favorite ? "true" : "false");
                    SetEl(ge, "StarRatingFloat", Rows[i].StarRatingFloat.ToString(CultureInfo.InvariantCulture));
                    SetEl(ge, "StarRating", ((int)Math.Round(Rows[i].StarRatingFloat)).ToString(CultureInfo.InvariantCulture));
                    written++;
                }
                doc.Save(file);
            }
            catch (Exception ex) { Console.WriteLine($"[store] flush error {file}: {ex.Message}"); }
        }
        return written;
    }

    private static void SetEl(XElement parent, string name, string value)
    {
        var e = parent.Element(name);
        if (e == null) parent.Add(new XElement(name, value));
        else e.Value = value;
    }

    // ── Stats ────────────────────────────────────────────────────────────────
    public void LogStats()
    {
        long rowBytes = (long)Rows.Length * System.Runtime.InteropServices.Marshal.SizeOf<GameRow>();
        long poolChars = Pool.Sum(s => (long)(s?.Length ?? 0));
        long notesChars = _notes?.Sum(s => (long)(s?.Length ?? 0)) ?? 0;
        Console.WriteLine($"[store] games={Rows.Length} platforms={_byPlatform.Count} pool={Pool.Length} entries (~{poolChars * 2 / 1048576.0:F1}MB chars) " +
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
