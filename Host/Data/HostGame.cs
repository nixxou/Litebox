// Thin IGame over the compact store: just (store, index). Subclasses the
// generated DummyGame (which already implements all 86 props / 34 methods) and
// overrides ONLY the ~28 members we actually back from the store; the rest keep
// the generated defaults. No per-game fat object → 40k of these are ~24 bytes
// each instead of a full IGame.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Unbroken.LaunchBox.Plugins.Data;
using LbApiHost.Generated;
using LbApiHost.Host.Media;

namespace LbApiHost.Host.Data;

internal sealed class HostGame : DummyGame, ILiteBoxGame
{
    private readonly GameStore _s;
    private readonly int _i;
    public HostGame(GameStore s, int i) { _s = s; _i = i; }

    // ── ILiteBoxGame: generic access to EVERY <Game> field, incl. those IGame doesn't expose ──
    public string GetField(string xmlElementName) => _s.GetExtraField(R.Id, xmlElementName);
    public void SetField(string xmlElementName, string value)
    {
        if (string.IsNullOrEmpty(xmlElementName)) return;
        if (xmlElementName == "Platform" && !_s.IsAddedGame(R.Id)) { _s.MoveGamePlatform(_i, value); return; }
        _s.SetGameField(_i, xmlElementName, value);   // modelled → typed store, else → sparse extra
    }
    public IReadOnlyCollection<string> ExtraFieldNames => _s.ExtraFieldNames(R.Id);

    private ref readonly GameRow R => ref _s.Rows[_i];

    public override string Id { get => R.Id.ToString(); set { } }
    public override string Title { get => _s.Str(R.TitleIdx); set => _s.SetGameField(_i, "Title", value); }
    public override string SortTitle { get => _s.Str(R.SortTitleIdx); set => _s.SetGameField(_i, "SortTitle", value); }
    // Platform: on an added game it's a normal field (node created in the right file at flush); on an
    // existing game it relocates the <Game> node between Platform files (MoveGamePlatform).
    public override string Platform
    {
        get => _s.Str(R.PlatformIdx);
        set { if (_s.IsAddedGame(R.Id)) _s.SetGameField(_i, "Platform", value); else _s.MoveGamePlatform(_i, value); }
    }
    public override string ApplicationPath { get => _s.Str(R.AppPathIdx); set => _s.SetGameField(_i, "ApplicationPath", value); }
    public override string EmulatorId { get => _s.Str(R.EmulatorIdIdx); set => _s.SetGameField(_i, "Emulator", value); }
    public override string Developer { get => _s.Str(R.DeveloperIdx); set => _s.SetGameField(_i, "Developer", value); }
    public override string Publisher { get => _s.Str(R.PublisherIdx); set => _s.SetGameField(_i, "Publisher", value); }
    public override string GenresString { get => _s.Str(R.GenresIdx); set => _s.SetGameField(_i, "Genre", value); }
    public override string Region { get => _s.Str(R.RegionIdx); set => _s.SetGameField(_i, "Region", value); }
    public override string Rating { get => _s.Str(R.RatingIdx); set => _s.SetGameField(_i, "Rating", value); }
    public override string Status { get => _s.Str(R.StatusIdx); set => _s.SetGameField(_i, "Status", value); }
    public override string PlayMode { get => _s.Str(R.PlayModeIdx); set => _s.SetGameField(_i, "PlayMode", value); }
    public override string Version { get => _s.Str(R.VersionIdx); set => _s.SetGameField(_i, "Version", value); }
    public override string WikipediaUrl { get => _s.Str(R.WikipediaUrlIdx); set => _s.SetGameField(_i, "WikipediaURL", value); }
    public override string VideoUrl { get => _s.Str(R.VideoUrlIdx); set => _s.SetGameField(_i, "VideoUrl", value); }

    public override float StarRatingFloat { get => R.StarRatingFloat; set => _s.JournalStarRating(_i, value); }
    public override int StarRating { get => (int)Math.Round(R.StarRatingFloat); set => _s.JournalStarRating(_i, value); }
    public override int PlayCount { get => R.PlayCount; set => _s.SetGameField(_i, "PlayCount", N(value)); }
    public override int PlayTime { get => R.PlayTime; set => _s.SetGameField(_i, "PlayTime", N(value)); }
    public override int CommunityStarRatingTotalVotes { get => R.CommunityVotes; set => _s.SetGameField(_i, "CommunityStarRatingTotalVotes", N(value)); }

    public override bool Favorite { get => R.Favorite; set => _s.JournalFavorite(_i, value); }
    public override bool Hide { get => R.Hide; set => _s.SetGameField(_i, "Hide", B(value)); }
    public override bool Broken { get => R.Broken; set => _s.SetGameField(_i, "Broken", B(value)); }
    public override bool Completed { get => R.Completed; set => _s.SetGameField(_i, "Completed", B(value)); }

    public override int? LaunchBoxDbId { get => R.LaunchBoxDbId < 0 ? (int?)null : R.LaunchBoxDbId; set => _s.SetGameField(_i, "DatabaseID", NN(value)); }
    public override int? MaxPlayers { get => R.MaxPlayers < 0 ? (int?)null : R.MaxPlayers; set => _s.SetGameField(_i, "MaxPlayers", NN(value)); }
    // ReleaseYear is derived from ReleaseDate in LB — set ReleaseDate instead. No-op.
    public override int? ReleaseYear { get => R.ReleaseYear < 0 ? (int?)null : R.ReleaseYear; set { } }

    public override bool? Installed
    {
        get => R.Installed switch { 1 => false, 2 => true, _ => (bool?)null };
        set => _s.SetGameField(_i, "Installed", TriB(value));
    }

    public override DateTime DateAdded
    {
        get => R.DateAddedTicks == 0 ? default : new DateTime(R.DateAddedTicks);
        set => _s.SetGameField(_i, "DateAdded", D(value));
    }
    public override DateTime? ReleaseDate
    {
        get => R.ReleaseDateTicks == 0 ? (DateTime?)null : new DateTime(R.ReleaseDateTicks);
        set => _s.SetGameField(_i, "ReleaseDate", DN(value));
    }
    public override DateTime? LastPlayedDate
    {
        get => R.LastPlayedTicks == 0 ? (DateTime?)null : new DateTime(R.LastPlayedTicks);
        set => _s.SetGameField(_i, "LastPlayedDate", DN(value));
    }

    // Tier 2 — may be unloaded (returns "" when dropped).
    public override string Notes { get => _s.NotesFor(_i) ?? ""; set => _s.SetGameField(_i, "Notes", value); }

    // ── Extended fields (string-backed) ──────────────────────────────────────
    public override string CommandLine { get => _s.Str(R.CommandLineIdx); set => _s.SetGameField(_i, "CommandLine", value); }
    public override string ConfigurationCommandLine { get => _s.Str(R.ConfigCmdIdx); set => _s.SetGameField(_i, "ConfigurationCommandLine", value); }
    public override string ConfigurationPath { get => _s.Str(R.ConfigPathIdx); set => _s.SetGameField(_i, "ConfigurationPath", value); }
    public override string DosBoxConfigurationPath { get => _s.Str(R.DosBoxCfgIdx); set => _s.SetGameField(_i, "DosBoxConfigurationPath", value); }
    // Host-internal (not an IGame member): a custom DOSBox.exe path overriding the bundle.
    public string CustomDosBoxVersionPath => _s.Str(R.CustomDosBoxIdx);
    // Host-internal (not an IGame member): the RetroAchievements ROM hash from the XML (debug column).
    public string RetroAchievementsHash => _s.Str(R.RaHashIdx);
    public override string ScummVmGameDataFolderPath { get => _s.Str(R.ScummDataIdx); set => _s.SetGameField(_i, "ScummVMGameDataFolderPath", value); }
    public override string ScummVmGameType { get => _s.Str(R.ScummTypeIdx); set => _s.SetGameField(_i, "ScummVMGameType", value); }
    public override string Series { get => _s.Str(R.SeriesIdx); set => _s.SetGameField(_i, "Series", value); }
    public override string Source { get => _s.Str(R.SourceIdx); set => _s.SetGameField(_i, "Source", value); }
    public override string ReleaseType { get => _s.Str(R.ReleaseTypeIdx); set => _s.SetGameField(_i, "ReleaseType", value); }
    public override string RootFolder { get => _s.Str(R.RootFolderIdx); set => _s.SetGameField(_i, "RootFolder", value); }
    public override string CloneOf { get => _s.Str(R.CloneOfIdx); set => _s.SetGameField(_i, "CloneOf", value); }
    public override string Progress { get => _s.Str(R.ProgressIdx); set => _s.SetGameField(_i, "Progress", value); }
    // Stored media-path overrides (the computed resolvers are the Get*Path methods below).
    public override string VideoPath { get => _s.Str(R.VideoPathIdx); set => _s.SetGameField(_i, "VideoPath", value); }
    public override string ThemeVideoPath { get => _s.Str(R.ThemeVideoPathIdx); set => _s.SetGameField(_i, "ThemeVideoPath", value); }
    public override string ManualPath { get => _s.Str(R.ManualPathIdx); set => _s.SetGameField(_i, "ManualPath", value); }
    public override string MusicPath { get => _s.Str(R.MusicPathIdx); set => _s.SetGameField(_i, "MusicPath", value); }

    public override DateTime DateModified { get => R.DateModifiedTicks == 0 ? default : new DateTime(R.DateModifiedTicks); set => _s.SetGameField(_i, "DateModified", D(value)); }
    public override float CommunityStarRating { get => R.CommunityStarRating; set => _s.SetGameField(_i, "CommunityStarRating", F(value)); }
    // Computed (max of community / local) — not a stored field. No-op.
    public override float CommunityOrLocalStarRating { get => R.StarRatingFloat > 0 ? R.StarRatingFloat : R.CommunityStarRating; set { } }
    public override int StartupLoadDelay { get => R.StartupLoadDelay; set => _s.SetGameField(_i, "StartupLoadDelay", N(value)); }

    // ── Extended fields (boolean flags, packed) ──────────────────────────────
    public override bool UseDosBox { get => Flag(GFlags.UseDosBox); set => _s.SetGameField(_i, "UseDosBox", B(value)); }
    public override bool UseScummVm { get => Flag(GFlags.UseScummVm); set => _s.SetGameField(_i, "UseScummVM", B(value)); }
    public override bool ScummVmAspectCorrection { get => Flag(GFlags.ScummAspect); set => _s.SetGameField(_i, "ScummVMAspectCorrection", B(value)); }
    public override bool ScummVmFullscreen { get => Flag(GFlags.ScummFull); set => _s.SetGameField(_i, "ScummVMFullscreen", B(value)); }
    public override bool Portable { get => Flag(GFlags.Portable); set => _s.SetGameField(_i, "Portable", B(value)); }
    public override bool UseStartupScreen { get => Flag(GFlags.UseStartup); set => _s.SetGameField(_i, "UseStartupScreen", B(value)); }
    public override bool OverrideDefaultStartupScreenSettings { get => Flag(GFlags.OverrideStartup); set => _s.SetGameField(_i, "OverrideDefaultStartupScreenSettings", B(value)); }
    public override bool HideAllNonExclusiveFullscreenWindows { get => Flag(GFlags.HideNonExclusive); set => _s.SetGameField(_i, "HideAllNonExclusiveFullscreenWindows", B(value)); }
    public override bool HideMouseCursorInGame { get => Flag(GFlags.HideMouse); set => _s.SetGameField(_i, "HideMouseCursorInGame", B(value)); }
    public override bool DisableShutdownScreen { get => Flag(GFlags.DisableShutdown); set => _s.SetGameField(_i, "DisableShutdownScreen", B(value)); }
    public override bool AggressiveWindowHiding { get => Flag(GFlags.AggressiveHiding); set => _s.SetGameField(_i, "AggressiveWindowHiding", B(value)); }

    // ── Computed (no storage) ────────────────────────────────────────────────
    public override string SortTitleOrTitle
    { get { var s = _s.Str(R.SortTitleIdx); return s.Length > 0 ? s : _s.Str(R.TitleIdx); } set { } }
    public override string[] PlayModes { get => Split(_s.Str(R.PlayModeIdx)); set { } }
    public override string[] Developers { get => Split(_s.Str(R.DeveloperIdx)); set { } }
    public override string[] Publishers { get => Split(_s.Str(R.PublisherIdx)); set { } }
    public override string[] SeriesValues { get => Split(_s.Str(R.SeriesIdx)); set { } }
    public override System.Collections.Concurrent.BlockingCollection<string> Genres
    {
        get { var bc = new System.Collections.Concurrent.BlockingCollection<string>(); foreach (var g in Split(_s.Str(R.GenresIdx))) bc.Add(g); return bc; }
        set { }
    }

    private bool Flag(int bit) => (R.Flags & bit) != 0;
    private static string[] Split(string s)
        => string.IsNullOrEmpty(s) ? Array.Empty<string>() : s.Split(';').Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();

    // ── Write-back value serialisers (typed → XML string; "" = clear the field) ──
    private static string B(bool v) => v ? "true" : "false";
    private static string TriB(bool? v) => v.HasValue ? (v.Value ? "true" : "false") : "";
    private static string N(int v) => v.ToString(CultureInfo.InvariantCulture);
    private static string NN(int? v) => v.HasValue ? v.Value.ToString(CultureInfo.InvariantCulture) : "";
    private static string F(float v) => v.ToString(CultureInfo.InvariantCulture);
    private static string D(DateTime v) => v == default ? "" : v.ToString("o", CultureInfo.InvariantCulture);
    private static string DN(DateTime? v) => v.HasValue ? v.Value.ToString("o", CultureInfo.InvariantCulture) : "";

    // ── Media (classic IO resolution; fast path via ExtendDB GameCache if ready) ──
    // These are COMPUTED in LaunchBox (not stored), so we resolve them on read.
    private string _plat => _s.Str(R.PlatformIdx);
    private string _title => _s.Str(R.TitleIdx);

    public override string FrontImagePath { get => MediaResolver.Image(_plat, R.Id, _title, MediaResolver.Front) ?? ""; set { } }
    public override string BackImagePath { get => MediaResolver.Image(_plat, R.Id, _title, MediaResolver.Back) ?? ""; set { } }
    public override string Box3DImagePath { get => MediaResolver.Image(_plat, R.Id, _title, MediaResolver.Box3D) ?? ""; set { } }
    public override string CartFrontImagePath { get => MediaResolver.Image(_plat, R.Id, _title, MediaResolver.CartFront) ?? ""; set { } }
    public override string CartBackImagePath { get => MediaResolver.Image(_plat, R.Id, _title, MediaResolver.CartBack) ?? ""; set { } }
    public override string Cart3DImagePath { get => MediaResolver.Image(_plat, R.Id, _title, MediaResolver.Cart3D) ?? ""; set { } }
    public override string ClearLogoImagePath { get => MediaResolver.Image(_plat, R.Id, _title, MediaResolver.ClearLogo) ?? ""; set { } }
    public override string ScreenshotImagePath { get => MediaResolver.Image(_plat, R.Id, _title, MediaResolver.Screenshot) ?? ""; set { } }
    public override string MarqueeImagePath { get => MediaResolver.Image(_plat, R.Id, _title, MediaResolver.Marquee) ?? ""; set { } }
    public override string BackgroundImagePath { get => MediaResolver.Image(_plat, R.Id, _title, MediaResolver.Background) ?? ""; set { } }

    public override string GetVideoPath(bool prioritizeThemeVideos)
        => MediaResolver.Video(_plat, R.Id, _title, prioritizeThemeVideos) ?? "";
    public override string GetVideoPath(string videoType)
        => MediaResolver.VideoIn(_plat, R.Id, _title, NormalizeVideoSubDir(videoType)) ?? "";
    public override string GetThemeVideoPath()
        => MediaResolver.VideoIn(_plat, R.Id, _title, "Theme") ?? "";
    public override string GetManualPath()
        => MediaResolver.Manual(_plat, R.Id, _title) ?? "";
    public override string GetMusicPath()
        => MediaResolver.Music(_plat, R.Id, _title) ?? "";

    private static string NormalizeVideoSubDir(string videoType)
    {
        if (string.IsNullOrWhiteSpace(videoType)) return null; // root
        return videoType; // "Trailer" / "Theme" / "Marquee" / "Recordings"
    }

    // Accessory entities (resident — additional apps are needed for launch). Add/remove/edit are
    // wired through the op-log (replace-collection ops) so they persist to the Platform XML.
    public override IAdditionalApplication[] GetAllAdditionalApplications()
        => _s.AddAppsFor(R.Id).Select(a => (IAdditionalApplication)new HostAdditionalApplication(_s, a, R.Id)).ToArray();
    public override IAdditionalApplication AddNewAdditionalApplication()
    {
        var a = new AddApp { Id = Guid.NewGuid().ToString() };
        _s.AddAppsMutable(R.Id).Add(a);
        _s.RecordChildReplace(R.Id, "AdditionalApplication");
        return new HostAdditionalApplication(_s, a, R.Id);
    }
    public override bool TryRemoveAdditionalApplication(IAdditionalApplication app)
    {
        int n = _s.AddAppsMutable(R.Id).RemoveAll(x => x.Id == app?.Id);
        if (n > 0) _s.RecordChildReplace(R.Id, "AdditionalApplication");
        return n > 0;
    }

    public override IAlternateName[] GetAllAlternateNames()
        => _s.AltNamesFor(R.Id).Select(a => (IAlternateName)new HostAlternateName(_s, a, R.Id)).ToArray();
    public override IAlternateName AddNewAlternateName()
    {
        var a = new AltName();
        _s.AltNamesMutable(R.Id).Add(a);
        _s.RecordChildReplace(R.Id, "AlternateName");
        return new HostAlternateName(_s, a, R.Id);
    }
    public override bool TryRemoveAlternateNames(IAlternateName alternateName)
    {
        int n = _s.AltNamesMutable(R.Id).RemoveAll(x => x.Name == alternateName?.Name && x.Region == alternateName?.Region);
        if (n > 0) _s.RecordChildReplace(R.Id, "AlternateName");
        return n > 0;
    }

    public override IMount[] GetAllMounts()
        => _s.MountsFor(R.Id).Select(m => (IMount)new HostMount(_s, m, R.Id)).ToArray();
    public override IMount AddNewMount()
    {
        var m = new GameMount { DriveLetter = 'C' };
        _s.MountsMutable(R.Id).Add(m);
        _s.RecordChildReplace(R.Id, "Mount");
        return new HostMount(_s, m, R.Id);
    }
    public override bool TryRemoveMount(IMount mount)
    {
        int n = _s.MountsMutable(R.Id).RemoveAll(x => x.Path == mount?.Path && x.DriveLetter == (mount?.DriveLetter ?? '\0'));
        if (n > 0) _s.RecordChildReplace(R.Id, "Mount");
        return n > 0;
    }

    public override ICustomField[] GetAllCustomFields()
        => _s.CustomFieldsFor(R.Id).Select(c => (ICustomField)new HostCustomField(_s, c, R.Id)).ToArray();
    public override ICustomField AddNewCustomField()
    {
        var c = new CustomField();
        _s.CustomFieldsMutable(R.Id).Add(c);
        _s.RecordChildReplace(R.Id, "CustomField");
        return new HostCustomField(_s, c, R.Id);
    }
    public override bool TryRemoveCustomField(ICustomField customField)
    {
        int n = _s.CustomFieldsMutable(R.Id).RemoveAll(x => x.Name == customField?.Name && x.Value == customField?.Value);
        if (n > 0) _s.RecordChildReplace(R.Id, "CustomField");
        return n > 0;
    }

    // Run the game's Configuration Application (DOSBox-aware for DOSBox games).
    public override string Configure() => LbApiHost.Host.HostLaunch.RunConfigTool(this);

    // All images on disk for this game (across all types, or one type), with details.
    public override ImageDetails[] GetAllImagesWithDetails()
        => MediaResolver.AllImages(_plat, R.Id, _title, null).ToArray();
    public override ImageDetails[] GetAllImagesWithDetails(string imageType)
        => MediaResolver.AllImages(_plat, R.Id, _title, imageType).ToArray();
}

/// <summary>IAdditionalApplication over an AddApp record. Setters mutate the record and record a
/// replace-collection op (the whole list is the source of truth).</summary>
internal sealed class HostAdditionalApplication : DummyAdditionalApplication
{
    private readonly GameStore _s;
    private readonly AddApp _a;
    private readonly Guid _gid;
    public HostAdditionalApplication(GameStore s, AddApp a, Guid gid) { _s = s; _a = a; _gid = gid; }
    private void Rec() => _s.RecordChildReplace(_gid, "AdditionalApplication");

    public override string Id { get => _a.Id ?? ""; set { _a.Id = value; Rec(); } }
    public override string GameId { get => _gid.ToString(); set { } }
    public override string ApplicationPath { get => _a.ApplicationPath ?? ""; set { _a.ApplicationPath = value; Rec(); } }
    public override string Name { get => _a.Name ?? ""; set { _a.Name = value; Rec(); } }
    public override string CommandLine { get => _a.CommandLine ?? ""; set { _a.CommandLine = value; Rec(); } }
    public override string Developer { get => _a.Developer ?? ""; set { _a.Developer = value; Rec(); } }
    public override string Publisher { get => _a.Publisher ?? ""; set { _a.Publisher = value; Rec(); } }
    public override string Region { get => _a.Region ?? ""; set { _a.Region = value; Rec(); } }
    public override string Version { get => _a.Version ?? ""; set { _a.Version = value; Rec(); } }
    public override string Status { get => _a.Status ?? ""; set { _a.Status = value; Rec(); } }
    public override string EmulatorId { get => _a.EmulatorId ?? ""; set { _a.EmulatorId = value; Rec(); } }
    public override int? Disc { get => _a.Disc; set { _a.Disc = value; Rec(); } }
    public override int Priority { get => _a.Priority; set { _a.Priority = value; Rec(); } }
    public override int PlayCount { get => _a.PlayCount; set { _a.PlayCount = value; Rec(); } }
    public override int PlayTime { get => _a.PlayTime; set { _a.PlayTime = value; Rec(); } }
    public override bool AutoRunBefore { get => _a.AutoRunBefore; set { _a.AutoRunBefore = value; Rec(); } }
    public override bool AutoRunAfter { get => _a.AutoRunAfter; set { _a.AutoRunAfter = value; Rec(); } }
    public override bool UseEmulator { get => _a.UseEmulator; set { _a.UseEmulator = value; Rec(); } }
    public override bool UseDosBox { get => _a.UseDosBox; set { _a.UseDosBox = value; Rec(); } }
    public override bool WaitForExit { get => _a.WaitForExit; set { _a.WaitForExit = value; Rec(); } }
    public override bool SideA { get => _a.SideA; set { _a.SideA = value; Rec(); } }
    public override bool SideB { get => _a.SideB; set { _a.SideB = value; Rec(); } }
    public override DateTime? ReleaseDate { get => _a.ReleaseDate; set { _a.ReleaseDate = value; Rec(); } }
    public override DateTime? LastPlayed { get => _a.LastPlayed; set { _a.LastPlayed = value; Rec(); } }
    public override bool? Installed { get => _a.Installed; set { _a.Installed = value; Rec(); } }
}

/// <summary>ICustomField over a CustomField record.</summary>
internal sealed class HostCustomField : DummyCustomField
{
    private readonly GameStore _s;
    private readonly CustomField _c;
    private readonly Guid _gid;
    public HostCustomField(GameStore s, CustomField c, Guid gid) { _s = s; _c = c; _gid = gid; }
    private void Rec() => _s.RecordChildReplace(_gid, "CustomField");

    public override string GameId { get => _gid.ToString(); set { } }
    public override string Name { get => _c.Name ?? ""; set { _c.Name = value; Rec(); } }
    public override string Value { get => _c.Value ?? ""; set { _c.Value = value; Rec(); } }
}

/// <summary>IMount over a GameMount record (DOSBox additional mount).</summary>
internal sealed class HostMount : DummyMount
{
    private readonly GameStore _s;
    private readonly GameMount _m;
    private readonly Guid _gid;
    public HostMount(GameStore s, GameMount m, Guid gid) { _s = s; _m = m; _gid = gid; }
    private void Rec() => _s.RecordChildReplace(_gid, "Mount");

    public override string GameId { get => _gid.ToString(); set { } }
    public override char DriveLetter { get => _m.DriveLetter; set { _m.DriveLetter = value; Rec(); } }
    public override string Filesystem { get => _m.Filesystem ?? ""; set { _m.Filesystem = value; Rec(); } }
    public override string MountType { get => _m.MountType ?? ""; set { _m.MountType = value; Rec(); } }
    public override string Path { get => _m.Path ?? ""; set { _m.Path = value; Rec(); } }
    public override string Type { get => _m.Type ?? ""; set { _m.Type = value; Rec(); } }
}

/// <summary>IAlternateName over an AltName record.</summary>
internal sealed class HostAlternateName : DummyAlternateName
{
    private readonly GameStore _s;
    private readonly AltName _a;
    private readonly Guid _gid;
    public HostAlternateName(GameStore s, AltName a, Guid gid) { _s = s; _a = a; _gid = gid; }
    private void Rec() => _s.RecordChildReplace(_gid, "AlternateName");

    public override string GameId { get => _gid.ToString(); set { } }
    public override string Name { get => _a.Name ?? ""; set { _a.Name = value; Rec(); } }
    public override string Region { get => _a.Region ?? ""; set { _a.Region = value; Rec(); } }
}
