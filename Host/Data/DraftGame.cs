// A game that does not exist yet.
//
// The editor wants an IGame to bind to, the store must not gain a row until the user says so: a
// DraftGame is a full IGame that lives in memory only — no store row, no op-log entry, no <Game>
// node, nothing to undo if the user walks away. DummyGame already implements every IGame member as
// an auto-property, so the draft IS the value bag; this type only adds an identity, the extra-field
// map, and the one door that turns a draft into a real game.
//
// Materialize is deliberately form-agnostic: a directory scan that produces two hundred drafts
// commits them through the same call the Add menu entry uses.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using LbApiHost.Generated;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Data;

internal sealed class DraftGame : DummyGame, ILiteBoxFields
{
    private readonly Dictionary<string, string> _extra = new(StringComparer.Ordinal);

    public DraftGame(string? platform = null)
    {
        Id = Guid.NewGuid().ToString();
        Title = "";
        Platform = platform ?? "";
        DateAdded = DateModified = DateTime.Now;
    }

    // No sub-entities on a draft: the pages that would produce them (versions, apps, saves, media)
    // stay out of the editor until the game exists.
    public override IAdditionalApplication[] GetAllAdditionalApplications() => Array.Empty<IAdditionalApplication>();

    public string GetField(string xmlElementName)
        => xmlElementName != null && _extra.TryGetValue(xmlElementName, out var v) ? (v ?? "") : "";

    public void SetField(string xmlElementName, string value)
    {
        if (string.IsNullOrEmpty(xmlElementName)) return;
        if (string.IsNullOrEmpty(value)) _extra.Remove(xmlElementName);
        else _extra[xmlElementName] = value;
    }

    public IReadOnlyCollection<string> ExtraFieldNames => _extra.Keys.ToArray();

    /// <summary>Creates the real game and copies everything the draft carries onto it. Only values
    /// the draft actually holds are written — an untouched field stays absent from the XML rather
    /// than being stamped with a default. The row is created with the title already set, so the
    /// media layer sees no rename on a game that has never had a name.</summary>
    public static IGame Materialize(DraftGame draft, IDataManager dm)
    {
        if (draft == null || dm == null) return null!;
        var g = dm.AddNewGame(draft.Title ?? "");
        if (g == null) return null!;

        void Str(Action<string> set, string? v) { if (!string.IsNullOrEmpty(v)) Try(() => set(v!)); }
        void Flag(Action<bool> set, bool v) { if (v) Try(() => set(true)); }
        void Num(Action<int> set, int v) { if (v != 0) Try(() => set(v)); }
        void Real(Action<float> set, float v) { if (v != 0f) Try(() => set(v)); }
        void Date(Action<DateTime> set, DateTime v) { if (v != default) Try(() => set(v)); }
        void DateN(Action<DateTime?> set, DateTime? v) { if (v.HasValue) Try(() => set(v)); }
        void IntN(Action<int?> set, int? v) { if (v.HasValue) Try(() => set(v)); }
        void BoolN(Action<bool?> set, bool? v) { if (v.HasValue) Try(() => set(v)); }

        // Identity / classification
        Str(v => g.Title = v, draft.Title);
        Str(v => g.SortTitle = v, draft.SortTitle);
        Str(v => g.Platform = v, draft.Platform);
        Str(v => g.Series = v, draft.Series);
        Str(v => g.Developer = v, draft.Developer);
        Str(v => g.Publisher = v, draft.Publisher);
        Str(v => g.GenresString = v, draft.GenresString);
        Str(v => g.Region = v, draft.Region);
        Str(v => g.Rating = v, draft.Rating);
        Str(v => g.ReleaseType = v, draft.ReleaseType);
        Str(v => g.Status = v, draft.Status);
        Str(v => g.PlayMode = v, draft.PlayMode);
        Str(v => g.Version = v, draft.Version);
        Str(v => g.Source = v, draft.Source);
        Str(v => g.Progress = v, draft.Progress);
        Str(v => g.CloneOf = v, draft.CloneOf);
        Str(v => g.Notes = v, draft.Notes);
        Str(v => g.WikipediaUrl = v, draft.WikipediaUrl);
        Str(v => g.VideoUrl = v, draft.VideoUrl);

        // Launching
        Str(v => g.ApplicationPath = v, draft.ApplicationPath);
        Str(v => g.EmulatorId = v, draft.EmulatorId);
        Str(v => g.CommandLine = v, draft.CommandLine);
        Str(v => g.ConfigurationPath = v, draft.ConfigurationPath);
        Str(v => g.ConfigurationCommandLine = v, draft.ConfigurationCommandLine);
        Str(v => g.RootFolder = v, draft.RootFolder);
        Str(v => g.DosBoxConfigurationPath = v, draft.DosBoxConfigurationPath);
        Str(v => g.ScummVmGameDataFolderPath = v, draft.ScummVmGameDataFolderPath);
        Str(v => g.ScummVmGameType = v, draft.ScummVmGameType);
        Flag(v => g.UseDosBox = v, draft.UseDosBox);
        Flag(v => g.UseScummVm = v, draft.UseScummVm);
        Flag(v => g.ScummVmAspectCorrection = v, draft.ScummVmAspectCorrection);
        Flag(v => g.ScummVmFullscreen = v, draft.ScummVmFullscreen);
        Flag(v => g.UseStartupScreen = v, draft.UseStartupScreen);
        Flag(v => g.OverrideDefaultStartupScreenSettings = v, draft.OverrideDefaultStartupScreenSettings);
        Flag(v => g.HideAllNonExclusiveFullscreenWindows = v, draft.HideAllNonExclusiveFullscreenWindows);
        Flag(v => g.HideMouseCursorInGame = v, draft.HideMouseCursorInGame);
        Flag(v => g.DisableShutdownScreen = v, draft.DisableShutdownScreen);
        Flag(v => g.AggressiveWindowHiding = v, draft.AggressiveWindowHiding);
        Num(v => g.StartupLoadDelay = v, draft.StartupLoadDelay);

        // Media path overrides
        Str(v => g.ManualPath = v, draft.ManualPath);
        Str(v => g.MusicPath = v, draft.MusicPath);
        Str(v => g.VideoPath = v, draft.VideoPath);
        Str(v => g.ThemeVideoPath = v, draft.ThemeVideoPath);

        // Flags / counters / dates
        Flag(v => g.Favorite = v, draft.Favorite);
        Flag(v => g.Portable = v, draft.Portable);
        Flag(v => g.Hide = v, draft.Hide);
        Flag(v => g.Broken = v, draft.Broken);
        // (Completed is obsolete in the SDK — Progress carries it, and it is copied above.)
        BoolN(v => g.Installed = v, draft.Installed);
        IntN(v => g.MaxPlayers = v, draft.MaxPlayers);
        IntN(v => g.LaunchBoxDbId = v, draft.LaunchBoxDbId);
        Num(v => g.PlayCount = v, draft.PlayCount);
        Num(v => g.PlayTime = v, draft.PlayTime);
        Num(v => g.CommunityStarRatingTotalVotes = v, draft.CommunityStarRatingTotalVotes);
        Real(v => g.StarRatingFloat = v, draft.StarRatingFloat);
        Real(v => g.CommunityStarRating = v, draft.CommunityStarRating);
        Date(v => g.DateAdded = v, draft.DateAdded);
        Date(v => g.DateModified = v, draft.DateModified);
        DateN(v => g.ReleaseDate = v, draft.ReleaseDate);
        DateN(v => g.LastPlayedDate = v, draft.LastPlayedDate);

        // Fields the SDK doesn't model — replayed verbatim through the same escape hatch.
        if (g is ILiteBoxFields lf)
            foreach (var kv in draft._extra) Try(() => lf.SetField(kv.Key, kv.Value));

        return g;
    }

    private static void Try(Action a) { try { a(); } catch { } }
}
