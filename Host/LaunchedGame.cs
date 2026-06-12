// Snapshot of the launched game, captured at launch time BEFORE the memory drop.
//
// Why: when a game starts, the host frees the optional data tier and clears the
// game cache ("free RAM at launch") — so anything that wants game info DURING the
// game (the pause screen's fanart background, logo, session time, a future
// notification overlay, …) must not touch the store or the cache. This object
// captures everything up front: plain strings + file PATHS (the files stay on
// disk; only the in-memory indexes go away).
//
// Media paths are resolved through MediaResolver (ExtendDB GameCache fast-path
// when ready, IO walk otherwise) at capture time, then never re-resolved.

#nullable enable

using LbApiHost.Host.Media;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host;

internal sealed class LaunchedGame
{
    public string GameId = "";
    public string Title = "";
    public string Platform = "";
    public string Developer = "";
    public int ReleaseYear;
    public DateTime LaunchedAtUtc;

    // Resolved at capture (absolute paths, may be null when the game has no such media).
    public string? FanartPath;
    public string? ClearLogoPath;
    public string? BoxFrontPath;
    public string? ManualPath;   // pause screen "View Manual"

    // Per-GAME pause-screen overrides (<OverrideDefaultPauseScreenSettings> +
    // the three toggles on the <Game> element). The game's _extra fields are
    // Tier-2 (dropped at launch), so they MUST be captured here. When
    // PauseOverride is false the emulator-level settings apply.
    public bool PauseOverride;
    public bool PauseUse, PauseSuspend, PauseForceful;

    /// <summary>The game currently running, or null. Set by HostLaunch at launch,
    /// cleared in its exit finally.</summary>
    public static LaunchedGame? Current { get; private set; }

    public static void Clear() => Current = null;

    /// <summary>Capture everything the in-game surfaces may need. Call BEFORE
    /// DropOptional / GameCache clear. Never throws.</summary>
    public static void Capture(IGame game)
    {
        try
        {
            var lg = new LaunchedGame
            {
                GameId = Safe(() => game.Id) ?? "",
                Title = Safe(() => game.Title) ?? "",
                Platform = Safe(() => game.Platform) ?? "",
                Developer = Safe(() => game.Developer) ?? "",
                ReleaseYear = SafeYear(game),
                LaunchedAtUtc = DateTime.UtcNow,
            };

            string plat = lg.Platform;
            bool hasId = Guid.TryParse(lg.GameId, out var id);
            if (!string.IsNullOrEmpty(plat) && hasId)
            {
                lg.FanartPath = FirstOrNull(MediaResolver.AllOfType(plat, id, lg.Title, "Fanart - Background"));
                lg.ClearLogoPath = MediaResolver.Image(plat, id, lg.Title, MediaResolver.ClearLogo);
                lg.BoxFrontPath = MediaResolver.Image(plat, id, lg.Title, MediaResolver.Front);
                lg.ManualPath = MediaResolver.Manual(plat, id, lg.Title);
            }
            // IGame property fallbacks (also cover non-GUID ids).
            lg.FanartPath ??= NonEmpty(Safe(() => game.BackgroundImagePath)) ?? NonEmpty(Safe(() => game.ScreenshotImagePath));
            lg.ClearLogoPath ??= NonEmpty(Safe(() => game.ClearLogoImagePath));
            lg.BoxFrontPath ??= NonEmpty(Safe(() => game.FrontImagePath)) ?? NonEmpty(Safe(() => game.Box3DImagePath));
            lg.ManualPath ??= NonEmpty(Safe(() => game.ManualPath));

            // Per-game pause overrides (read via ILiteBoxFields — these XML fields
            // aren't on the SDK IGame, and the backing _extra dict is dropped at launch).
            try
            {
                if (game is LbApiHost.ILiteBoxFields lf
                    && string.Equals(lf.GetField("OverrideDefaultPauseScreenSettings"), "true", StringComparison.OrdinalIgnoreCase))
                {
                    lg.PauseOverride = true;
                    lg.PauseUse = string.Equals(lf.GetField("UsePauseScreen"), "true", StringComparison.OrdinalIgnoreCase);
                    lg.PauseSuspend = string.Equals(lf.GetField("SuspendProcessOnPause"), "true", StringComparison.OrdinalIgnoreCase);
                    lg.PauseForceful = string.Equals(lf.GetField("ForcefulPauseScreenActivation"), "true", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { }

            Current = lg;
            Console.WriteLine($"[launched] snapshot \"{lg.Title}\" fanart={(lg.FanartPath != null ? "yes" : "no")} logo={(lg.ClearLogoPath != null ? "yes" : "no")} box={(lg.BoxFrontPath != null ? "yes" : "no")}");
        }
        catch (Exception ex)
        {
            Console.WriteLine("[launched] capture failed: " + ex.Message);
            Current = new LaunchedGame { Title = Safe(() => game.Title) ?? "", LaunchedAtUtc = DateTime.UtcNow };
        }
    }

    private static int SafeYear(IGame g) { try { return g.ReleaseYear ?? 0; } catch { return 0; } }
    private static string? FirstOrNull(List<string>? l) => l is { Count: > 0 } ? l[0] : null;
    private static string? NonEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
    private static T? Safe<T>(Func<T?> f) { try { return f(); } catch { return default; } }
}
