// Thin IGame over the compact store: just (store, index). Subclasses the
// generated DummyGame (which already implements all 86 props / 34 methods) and
// overrides ONLY the ~28 members we actually back from the store; the rest keep
// the generated defaults. No per-game fat object → 40k of these are ~24 bytes
// each instead of a full IGame.

using System;
using System.Linq;
using Unbroken.LaunchBox.Plugins.Data;
using LbApiHost.Generated;
using LbApiHost.Host.Media;

namespace LbApiHost.Host.Data;

internal sealed class HostGame : DummyGame
{
    private readonly GameStore _s;
    private readonly int _i;
    public HostGame(GameStore s, int i) { _s = s; _i = i; }

    private ref readonly GameRow R => ref _s.Rows[_i];

    public override string Id { get => R.Id.ToString(); set { } }
    public override string Title { get => _s.Str(R.TitleIdx); set { } }
    public override string SortTitle { get => _s.Str(R.SortTitleIdx); set { } }
    public override string Platform { get => _s.Str(R.PlatformIdx); set { } }
    public override string ApplicationPath { get => _s.Str(R.AppPathIdx); set { } }
    public override string EmulatorId { get => _s.Str(R.EmulatorIdIdx); set { } }
    public override string Developer { get => _s.Str(R.DeveloperIdx); set { } }
    public override string Publisher { get => _s.Str(R.PublisherIdx); set { } }
    public override string GenresString { get => _s.Str(R.GenresIdx); set { } }
    public override string Region { get => _s.Str(R.RegionIdx); set { } }
    public override string Rating { get => _s.Str(R.RatingIdx); set { } }
    public override string Status { get => _s.Str(R.StatusIdx); set { } }
    public override string PlayMode { get => _s.Str(R.PlayModeIdx); set { } }
    public override string Version { get => _s.Str(R.VersionIdx); set { } }
    public override string WikipediaUrl { get => _s.Str(R.WikipediaUrlIdx); set { } }
    public override string VideoUrl { get => _s.Str(R.VideoUrlIdx); set { } }

    public override float StarRatingFloat { get => R.StarRatingFloat; set => _s.SetStarRating(_i, value); }
    public override int StarRating { get => (int)Math.Round(R.StarRatingFloat); set => _s.SetStarRating(_i, value); }
    public override int PlayCount { get => R.PlayCount; set { } }
    public override int PlayTime { get => R.PlayTime; set { } }
    public override int CommunityStarRatingTotalVotes { get => R.CommunityVotes; set { } }

    public override bool Favorite { get => R.Favorite; set => _s.SetFavorite(_i, value); }
    public override bool Hide { get => R.Hide; set { } }
    public override bool Broken { get => R.Broken; set { } }
    public override bool Completed { get => R.Completed; set { } }

    public override int? LaunchBoxDbId { get => R.LaunchBoxDbId < 0 ? (int?)null : R.LaunchBoxDbId; set { } }
    public override int? MaxPlayers { get => R.MaxPlayers < 0 ? (int?)null : R.MaxPlayers; set { } }
    public override int? ReleaseYear { get => R.ReleaseYear < 0 ? (int?)null : R.ReleaseYear; set { } }

    public override bool? Installed
    {
        get => R.Installed switch { 1 => false, 2 => true, _ => (bool?)null };
        set { }
    }

    public override DateTime DateAdded
    {
        get => R.DateAddedTicks == 0 ? default : new DateTime(R.DateAddedTicks);
        set { }
    }
    public override DateTime? ReleaseDate
    {
        get => R.ReleaseDateTicks == 0 ? (DateTime?)null : new DateTime(R.ReleaseDateTicks);
        set { }
    }
    public override DateTime? LastPlayedDate
    {
        get => R.LastPlayedTicks == 0 ? (DateTime?)null : new DateTime(R.LastPlayedTicks);
        set { }
    }

    // Tier 2 — may be unloaded (returns "" when dropped).
    public override string Notes { get => _s.NotesFor(_i) ?? ""; set { } }

    // ── Extended fields (string-backed) ──────────────────────────────────────
    public override string CommandLine { get => _s.Str(R.CommandLineIdx); set { } }
    public override string ConfigurationCommandLine { get => _s.Str(R.ConfigCmdIdx); set { } }
    public override string ConfigurationPath { get => _s.Str(R.ConfigPathIdx); set { } }
    public override string DosBoxConfigurationPath { get => _s.Str(R.DosBoxCfgIdx); set { } }
    // Host-internal (not an IGame member): a custom DOSBox.exe path overriding the bundle.
    public string CustomDosBoxVersionPath => _s.Str(R.CustomDosBoxIdx);
    public override string ScummVmGameDataFolderPath { get => _s.Str(R.ScummDataIdx); set { } }
    public override string ScummVmGameType { get => _s.Str(R.ScummTypeIdx); set { } }
    public override string Series { get => _s.Str(R.SeriesIdx); set { } }
    public override string Source { get => _s.Str(R.SourceIdx); set { } }
    public override string ReleaseType { get => _s.Str(R.ReleaseTypeIdx); set { } }
    public override string RootFolder { get => _s.Str(R.RootFolderIdx); set { } }
    public override string CloneOf { get => _s.Str(R.CloneOfIdx); set { } }
    public override string Progress { get => _s.Str(R.ProgressIdx); set { } }
    // Stored media-path overrides (the computed resolvers are the Get*Path methods below).
    public override string VideoPath { get => _s.Str(R.VideoPathIdx); set { } }
    public override string ThemeVideoPath { get => _s.Str(R.ThemeVideoPathIdx); set { } }
    public override string ManualPath { get => _s.Str(R.ManualPathIdx); set { } }
    public override string MusicPath { get => _s.Str(R.MusicPathIdx); set { } }

    public override DateTime DateModified { get => R.DateModifiedTicks == 0 ? default : new DateTime(R.DateModifiedTicks); set { } }
    public override float CommunityStarRating { get => R.CommunityStarRating; set { } }
    public override float CommunityOrLocalStarRating { get => R.StarRatingFloat > 0 ? R.StarRatingFloat : R.CommunityStarRating; set { } }
    public override int StartupLoadDelay { get => R.StartupLoadDelay; set { } }

    // ── Extended fields (boolean flags, packed) ──────────────────────────────
    public override bool UseDosBox { get => Flag(GFlags.UseDosBox); set { } }
    public override bool UseScummVm { get => Flag(GFlags.UseScummVm); set { } }
    public override bool ScummVmAspectCorrection { get => Flag(GFlags.ScummAspect); set { } }
    public override bool ScummVmFullscreen { get => Flag(GFlags.ScummFull); set { } }
    public override bool Portable { get => Flag(GFlags.Portable); set { } }
    public override bool UseStartupScreen { get => Flag(GFlags.UseStartup); set { } }
    public override bool OverrideDefaultStartupScreenSettings { get => Flag(GFlags.OverrideStartup); set { } }
    public override bool HideAllNonExclusiveFullscreenWindows { get => Flag(GFlags.HideNonExclusive); set { } }
    public override bool HideMouseCursorInGame { get => Flag(GFlags.HideMouse); set { } }
    public override bool DisableShutdownScreen { get => Flag(GFlags.DisableShutdown); set { } }
    public override bool AggressiveWindowHiding { get => Flag(GFlags.AggressiveHiding); set { } }

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

    // Accessory entities (resident — additional apps are needed for launch).
    public override IAdditionalApplication[] GetAllAdditionalApplications()
        => _s.AddAppsFor(R.Id).Select(a => (IAdditionalApplication)new HostAdditionalApplication(a, R.Id.ToString())).ToArray();

    public override IAlternateName[] GetAllAlternateNames()
        => _s.AltNamesFor(R.Id).Select(a => (IAlternateName)new HostAlternateName(a, R.Id.ToString())).ToArray();

    public override IMount[] GetAllMounts()
        => _s.MountsFor(R.Id).Select(m => (IMount)new HostMount(m, R.Id.ToString())).ToArray();

    // Run the game's Configuration Application (DOSBox-aware for DOSBox games).
    public override string Configure() => LbApiHost.Host.HostLaunch.RunConfigTool(this);

    // All images on disk for this game (across all types, or one type), with details.
    public override ImageDetails[] GetAllImagesWithDetails()
        => MediaResolver.AllImages(_plat, R.Id, _title, null).ToArray();
    public override ImageDetails[] GetAllImagesWithDetails(string imageType)
        => MediaResolver.AllImages(_plat, R.Id, _title, imageType).ToArray();
}

/// <summary>IAdditionalApplication over an AddApp record.</summary>
internal sealed class HostAdditionalApplication : DummyAdditionalApplication
{
    private readonly AddApp _a;
    private readonly string _gameId;
    public HostAdditionalApplication(AddApp a, string gameId) { _a = a; _gameId = gameId; }

    public override string Id { get => _a.Id ?? ""; set { } }
    public override string GameId { get => _gameId; set { } }
    public override string ApplicationPath { get => _a.ApplicationPath ?? ""; set { } }
    public override string Name { get => _a.Name ?? ""; set { } }
    public override string CommandLine { get => _a.CommandLine ?? ""; set { } }
    public override string Developer { get => _a.Developer ?? ""; set { } }
    public override string Publisher { get => _a.Publisher ?? ""; set { } }
    public override string Region { get => _a.Region ?? ""; set { } }
    public override string Version { get => _a.Version ?? ""; set { } }
    public override string Status { get => _a.Status ?? ""; set { } }
    public override string EmulatorId { get => _a.EmulatorId ?? ""; set { } }
    public override int? Disc { get => _a.Disc; set { } }
    public override int Priority { get => _a.Priority; set { } }
    public override int PlayCount { get => _a.PlayCount; set { } }
    public override int PlayTime { get => _a.PlayTime; set { } }
    public override bool AutoRunBefore { get => _a.AutoRunBefore; set { } }
    public override bool AutoRunAfter { get => _a.AutoRunAfter; set { } }
    public override bool UseEmulator { get => _a.UseEmulator; set { } }
    public override bool UseDosBox { get => _a.UseDosBox; set { } }
    public override bool WaitForExit { get => _a.WaitForExit; set { } }
    public override bool SideA { get => _a.SideA; set { } }
    public override bool SideB { get => _a.SideB; set { } }
}

/// <summary>IMount over a GameMount record (DOSBox additional mount).</summary>
internal sealed class HostMount : DummyMount
{
    private readonly GameMount _m;
    private readonly string _gameId;
    public HostMount(GameMount m, string gameId) { _m = m; _gameId = gameId; }

    public override string GameId { get => _gameId; set { } }
    public override char DriveLetter { get => _m.DriveLetter; set { } }
    public override string Filesystem { get => _m.Filesystem ?? ""; set { } }
    public override string MountType { get => _m.MountType ?? ""; set { } }
    public override string Path { get => _m.Path ?? ""; set { } }
    public override string Type { get => _m.Type ?? ""; set { } }
}

/// <summary>IAlternateName over an AltName record.</summary>
internal sealed class HostAlternateName : DummyAlternateName
{
    private readonly AltName _a;
    private readonly string _gameId;
    public HostAlternateName(AltName a, string gameId) { _a = a; _gameId = gameId; }

    public override string GameId { get => _gameId; set { } }
    public override string Name { get => _a.Name ?? ""; set { } }
    public override string Region { get => _a.Region ?? ""; set { } }
}
