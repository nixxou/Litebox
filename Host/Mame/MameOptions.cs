// MameOptions — the single read surface for the MAME community-leaderboard toggles LiteBox honours.
//
//   • Download / Upload: LaunchBox-NATIVE settings (Settings.xml, edited on the LB · Integrations → MAME tab).
//     Download gates the HIGH SCORES detail tab; Upload gates the auto-submit at game exit.
//   • FBNeo upload: a LiteBox-OWN option (LiteBox.ini) — LaunchBox has no such setting. FBNeo runs under
//     RetroArch and writes MAME-format .hi files, so we CAN submit them, but only when the emulator is in
//     RetroAchievements HARDCORE mode (a legitimacy gate mirroring how RA restricts certain unlocks).
//
// Bound to the live LbSettingsStore at boot (see MainWindow) so reads are cheap and always current — no
// per-selection re-parse of Settings.xml.

#nullable enable

using LbApiHost.Host.Data;

namespace LbApiHost.Host.Mame;

internal static class MameOptions
{
    private static LbSettingsStore? _s;

    /// <summary>Bind the live LaunchBox settings store (once, at boot).</summary>
    public static void Bind(LbSettingsStore settings) => _s = settings;

    /// <summary>LB-native: download the community leaderboards from the Games Database (gates the HIGH SCORES tab).</summary>
    public static bool DownloadEnabled => _s?.GetBool("DownloadMameCommunityHighScores") ?? false;

    /// <summary>LB-native: submit the user's own MAME high scores to the community leaderboards.</summary>
    public static bool UploadEnabled => _s?.GetBool("UploadMameCommunityHighScores") ?? false;

    /// <summary>LiteBox-own: also submit FBNeo (RetroArch) high scores. Only meaningful together with the
    /// hardcore-mode gate checked at submit time (see the auto-upload path).</summary>
    public static bool UploadFbneoEnabled => Host.LiteBoxConfig.LoadForExe().GetSecBool("Mame", "UploadFbneoHighScores", false);
}
