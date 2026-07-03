// LEDBlinky integration — drives an external LEDBlinky.exe the exact way LaunchBox does, so an
// arcade cabinet's button/joystick LEDs light per-game. LiteBox impersonates a first-class
// LaunchBox front-end here: the user must set FE = "LaunchBox" in the LEDBlinky Configuration app.
//
// LEDBlinky itself does all the real work (reads controls.ini / MAME.xml / RocketBlinky profiles,
// lights the right ports, speaks button names, previews-then-reverts). Our ONLY job is to fire the
// right numeric command at the right lifecycle moment. Each event = one quick fire-and-forget
// Process.Start of LEDBlinky.exe (it does its thing and exits; only one instance stays resident).
// Nothing is awaited; a missing/bad path never blocks the UI or a launch.
//
// Command set — reverse-engineered from a real LaunchBox↔LEDBlinky session (ExtendDB ProcStart
// capture) and cross-checked against ledblinky.net's command-line docs:
//   1                      Front-End Start          (doc ✓)
//   2                      Front-End Quit           (doc ✓)
//   3                      Game Start               (LaunchBox-private; relies on the last "9")
//   4                      Game Stop                (doc ✓)
//   5 / 6                  Screensaver Start / Stop (doc ✓) — NOT WIRED yet (no screensaver in LiteBox)
//   7 <emulator>          List / platform change   (LaunchBox-private; the doc's generic form is "8")
//   9 "<rom>" <emulator>  Game Select (highlight)  (LaunchBox-private; ≈ the doc's "15" Set-Game)
//
// Argument rules (the crux — confirmed by both the capture and the doc's "How does LEDBlinky
// determine which LEDs to light for a MAME game" section):
//   • Arcade game → name = ROM short name (ApplicationPath file name w/o extension, e.g. "ddragon3"),
//                   emulator = "MAME"  (NOT "Arcade", NOT the real emulator such as "RetroArch").
//   • Other       → name = game Title, emulator = platform name (e.g. "MS-DOS", "Windows").
//   • The name is quoted (may contain spaces); the emulator token is passed bare (matches LB).
//   • "3"/"4" take no args — LEDBlinky uses the last "9" as the current game, so we always send a
//     "9" for the launched game right before "3".

using System;
using System.Diagnostics;
using System.IO;
using LbApiHost.Host.Data;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host;

internal static class LedBlinky
{
    private static LbSettingsStore _settings;

    /// <summary>Wire the LB Settings.xml store (holds EnableLedBlinky / LedBlinkyPath /
    /// LedBlinkyUseAdvanced). Called once at boot; every read below is live, so toggling the
    /// option in the Options window takes effect without a restart.</summary>
    public static void Bind(LbSettingsStore settings) => _settings = settings;

    private static bool Enabled => _settings != null && _settings.GetBool("EnableLedBlinky");
    private static string ExePath => _settings?.Get("LedBlinkyPath") ?? "";
    // "Use advanced logic for filter lists" — reserved for the 7-vs-8 list-change split (see ListChange).
    private static bool Advanced => _settings != null && _settings.GetBool("LedBlinkyUseAdvanced");

    // ── Front-end lifecycle ────────────────────────────────────────────────
    public static void FrontendStart() => Fire("1");
    public static void FrontendQuit()  => Fire("2");

    // ── Game lifecycle ─────────────────────────────────────────────────────
    /// <summary>Emit "3" (Game Start). The caller must have sent a GameSelect for THIS game first —
    /// LEDBlinky's "3" is argument-less and lights whatever the last "9" selected.</summary>
    public static void GameStart() => Fire("3");
    public static void GameStop()  => Fire("4");

    // ── Screensaver / attract — PLACEHOLDER (not wired) ─────────────────────
    // LiteBox has no screensaver / attract mode yet. These are implemented and ready: the day
    // LiteBox gains one, call ScreensaverStart() when the saver kicks in and ScreensaverStop()
    // when it ends. (The "Don't start screensaver when entering attract mode" option —
    // LedBlinkyDontStartScreensaver — is a BigBox attract-mode concern, likewise N/A for now.)
    public static void ScreensaverStart() => Fire("5");
    public static void ScreensaverStop()  => Fire("6");

    // ── List / platform navigation ─────────────────────────────────────────
    /// <summary>Emit "7 &lt;emulator&gt;" when the current platform/list changes. <paramref name="platform"/>
    /// is the LaunchBox platform name; arcade is mapped to "MAME".</summary>
    public static void ListChange(string platform)
    {
        string emu = MapEmulator(platform);
        // LaunchBox emits "7" for platform navigation. Its "8" variant shows up for filtered /
        // playlist lists (seemingly gated by LedBlinkyUseAdvanced) — left as a TODO until that
        // path is confirmed; today ListChange is only called for real platforms.
        Fire(string.IsNullOrEmpty(emu) ? "7" : $"7 {emu}");
    }

    // ── Highlighted game changed ───────────────────────────────────────────
    /// <summary>Emit "9 \"&lt;rom-or-title&gt;\" &lt;emulator&gt;" when the highlighted game changes.</summary>
    public static void GameSelect(IGame game)
    {
        if (game == null) return;
        string platform = Safe(() => game.Platform) ?? "";
        string emu = MapEmulator(platform);
        string name = IsArcade(platform)
            ? Path.GetFileNameWithoutExtension(Safe(() => game.ApplicationPath) ?? "")   // MAME ROM short name
            : (Safe(() => game.Title) ?? "");
        if (string.IsNullOrWhiteSpace(name)) return;
        Fire(string.IsNullOrEmpty(emu) ? $"9 \"{name}\"" : $"9 \"{name}\" {emu}");
    }

    // Arcade → "MAME" (LEDBlinky keys arcade games off the MAME controls.ini by ROM name).
    private static bool IsArcade(string platform)
        => string.Equals(platform, "Arcade", StringComparison.OrdinalIgnoreCase);
    private static string MapEmulator(string platform)
        => IsArcade(platform) ? "MAME" : (platform ?? "");

    private static void Fire(string args)
    {
        string exe = ExePath;
        if (!Enabled || string.IsNullOrWhiteSpace(exe) || !File.Exists(exe)) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            Console.WriteLine($"[ledblinky] {exe} {args}");
        }
        catch (Exception ex) { Console.WriteLine($"[ledblinky] fire \"{args}\" failed: {ex.Message}"); }
    }

    private static T Safe<T>(Func<T> f) { try { return f(); } catch { return default; } }
}
