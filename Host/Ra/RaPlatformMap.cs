// LiteBox-native, FROZEN platform → RA console mapping — the fallback used when ExtendDB is absent or
// its RetroAchievements module is off. Full-auto, NOT user-configurable (by design — see the RA fallback
// notes). It is a verbatim copy of ExtendDB's hand-curated hardlist (PlatformMapper.HARD_READABLE,
// platform name → RAHasher console KEY) joined with RAHasher's own console table (KEY → numeric console id,
// the RC_CONSOLE_* ids). Lookup is case/punctuation-insensitive (lowercase, alnum-only).
//
// To refresh: re-copy HARD_READABLE from the plugin and re-run `RahasherExtendDB.exe` (no args) for the
// KEY→id list. Console ids are stable, so this rarely changes.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace LbApiHost.Host.Ra;

internal static class RaPlatformMap
{
    /// <summary>RA console id of Arcade (RC_CONSOLE_ARCADE) — the MD5(name) special case.</summary>
    public const int ArcadeConsoleId = 27;

    // LB/ExtendDB platform name → RAHasher console KEY (verbatim copy of PlatformMapper.HARD_READABLE).
    private static readonly Dictionary<string, string> NameToKey = new(StringComparer.Ordinal)
    {
        // ── Nintendo ──
        { "Nintendo Entertainment System",       "NES" },
        { "Nes - Super Mario Bros. Hacks",       "NES" },
        { "Nintendo Famicom Disk System",        "FDS" },
        { "Super Nintendo Entertainment System", "SNES" },
        { "Super Nintendo MSU-1",                "SNES" },
        { "Snes - Super Mario World Hacks",      "SNES" },
        { "Nintendo Satellaview",                "SNES" },
        { "Nintendo 64",                         "N64" },
        { "Nintendo 64DD",                       "N64" },
        { "Nintendo GameCube",                   "GC" },
        { "Nintendo Wii",                        "Wii" },
        { "Nintendo Wii U",                      "WiiU" },
        { "Nintendo Game Boy",                   "GB" },
        { "Super Game Boy",                      "GB" },
        { "Nintendo Game Boy Color",             "GBC" },
        { "Nintendo Game Boy Advance",           "GBA" },
        { "Nintendo DS",                         "DS" },
        { "Nintendo 3DS",                        "3DS" },
        { "Nintendo Virtual Boy",                "VB" },
        { "Nintendo Pokemon Mini",               "MINI" },
        { "Nintendo Game & Watch",               "G&W" },

        // ── Sony ──
        { "Sony Playstation",                    "PS1" },
        { "Sony Playstation 2",                  "PS2" },
        { "Sony PSP",                            "PSP" },
        { "Sony PSP Minis",                      "PSP" },

        // ── Microsoft ──
        { "Microsoft Xbox",                      "Xbox" },
        { "MS-DOS",                              "DOS" },

        // ── Sega ──
        { "Sega Genesis",                            "MD" },
        { "Megadrive - Sonic The Hedgehog 2 Hacks",  "MD" },
        { "Sega CD",                                 "SCD" },
        { "Sega 32X",                                "32X" },
        { "Sega CD 32X",                             "32X" },
        { "Sega Master System",                      "SMS" },
        { "Sega Game Gear",                          "GG" },
        { "Sega SG-1000",                            "SG1K" },
        { "Sega SC-3000",                            "SG1K" },
        { "Othello Multivision",                     "SG1K" },
        { "Sega Saturn",                             "SAT" },
        { "Sega Dreamcast",                          "DC" },
        { "Sega Pico",                               "Pico" },

        // ── Atari ──
        { "Atari 2600",                          "2600" },
        { "Atari 2600 Supercharger",             "2600" },
        { "Atari 5200",                          "5200" },
        { "Atari 7800",                          "7800" },
        { "Atari Jaguar",                        "JAG" },
        { "Atari Jaguar CD",                     "JCD" },
        { "Atari Lynx",                          "Lynx" },
        { "Atari ST",                            "AST" },

        // ── NEC ──
        { "NEC TurboGrafx-16",                   "PCE" },
        { "PC Engine SuperGrafx",                "PCE" },
        { "NEC TurboGrafx-CD",                   "PCCD" },
        { "NEC PC-FX",                           "PC-FX" },
        { "NEC PC-8801",                         "80/88" },
        { "NEC PC-9801",                         "9800" },

        // ── SNK ──
        { "SNK Neo Geo CD",                      "NGCD" },
        { "SNK Neo Geo Pocket",                  "NGP" },
        { "SNK Neo Geo Pocket Color",            "NGP" },
        { "SNK Neo Geo MVS",                     "ARC" },
        { "SNK Neo Geo AES",                     "ARC" },

        // ── Computers ──
        { "Amstrad CPC",                         "CPC" },
        { "Amstrad GX4000",                      "CPC" },
        { "Apple II",                            "A2" },
        { "Commodore 64",                        "C64" },
        { "Commodore Amiga",                     "Amiga" },
        { "Commodore VIC-20",                    "VIC-20" },
        { "Microsoft MSX",                       "MSX" },
        { "Microsoft MSX2",                      "MSX" },
        { "Microsoft MSX2+",                     "MSX" },
        { "MSX Turbo R",                         "MSX" },
        { "Fujitsu FM Towns Marty",              "FMTowns" },
        { "Sharp X1",                            "X1" },
        { "Sharp X68000",                        "X68K" },
        { "Oric Atmos",                          "Oric" },
        { "Sinclair ZX Spectrum",                "ZXS" },
        { "Sinclair ZX-81",                      "ZX81" },
        { "Thomson MO/TO",                       "TO8" },

        // ── Other consoles / handhelds / arcade ──
        { "Arcade",                              "ARC" },
        { "GCE Vectrex",                         "VECT" },
        { "Mattel Intellivision",                "INTV" },
        { "ColecoVision",                        "CV" },
        { "Magnavox Odyssey 2",                  "MO2" },
        { "Philips Videopac+",                   "MO2" },
        { "Fairchild Channel F",                 "CHF" },
        { "Emerson Arcadia 2001",                "A2001" },
        { "Interton VC 4000",                    "VC4000" },
        { "Elektor TV Games Computer",           "ELEK" },
        { "3DO Interactive Multiplayer",         "3DO" },
        { "Philips CD-i",                        "CD-i" },
        { "Mega Duck",                           "DUCK" },
        { "Watara Supervision",                  "WSV" },
        { "WonderSwan",                          "WS" },
        { "WonderSwan Color",                    "WS" },
        { "Epoch Super Cassette Vision",         "ESCV" },
        { "Nokia N-Gage",                        "N-Gage" },
        { "Arduboy",                             "ARD" },
        { "Uzebox",                              "UZE" },
        { "WASM-4",                              "WASM4" },
        { "TIC-80",                              "TIC-80" },
    };

    // RAHasher console KEY → numeric console id (from `RahasherExtendDB.exe` no-args console table).
    private static readonly Dictionary<string, int> KeyToId = new(StringComparer.OrdinalIgnoreCase)
    {
        { "NES", 7 }, { "FDS", 81 }, { "SNES", 3 }, { "N64", 2 }, { "GC", 16 }, { "Wii", 19 },
        { "GB", 4 }, { "GBC", 6 }, { "GBA", 5 }, { "DS", 18 }, { "DSi", 78 }, { "MINI", 24 },
        { "VB", 28 }, { "G&W", 60 }, { "3DS", 62 }, { "WiiU", 20 },
        { "PS1", 12 }, { "PS2", 21 }, { "PSP", 41 },
        { "2600", 25 }, { "7800", 51 }, { "JAG", 17 }, { "JCD", 77 }, { "Lynx", 13 }, { "5200", 50 }, { "AST", 36 },
        { "SG1K", 33 }, { "SMS", 11 }, { "MD", 1 }, { "SCD", 9 }, { "32X", 10 }, { "SAT", 39 },
        { "DC", 40 }, { "GG", 15 }, { "Pico", 68 },
        { "80/88", 47 }, { "PCE", 8 }, { "PCCD", 76 }, { "PC-FX", 49 }, { "9800", 48 },
        { "NGCD", 56 }, { "NGP", 14 },
        { "3DO", 43 }, { "CPC", 37 }, { "A2", 38 }, { "ARC", 27 }, { "A2001", 73 }, { "ARD", 71 },
        { "CV", 44 }, { "ELEK", 75 }, { "CHF", 57 }, { "INTV", 45 }, { "VC4000", 74 }, { "MO2", 23 },
        { "DUCK", 69 }, { "MSX", 29 }, { "UZE", 80 }, { "VECT", 46 }, { "WASM4", 72 }, { "WSV", 63 },
        { "WS", 53 }, { "Amiga", 35 }, { "ESCV", 55 }, { "C64", 30 }, { "FMTowns", 58 }, { "N-Gage", 61 },
        { "Oric", 32 }, { "CD-i", 42 }, { "X1", 64 }, { "X68K", 52 }, { "TO8", 66 }, { "TI83", 79 },
        { "TIC-80", 65 }, { "VIC-20", 34 }, { "Zeebo", 70 }, { "ZX81", 31 }, { "ZXS", 59 },
        { "DOS", 26 }, { "Xbox", 22 },
    };

    // Normalized (lowercase, alnum-only) view of NameToKey, built once.
    private static readonly Dictionary<string, string> NormToKey = BuildNorm();
    private static Dictionary<string, string> BuildNorm()
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in NameToKey) { var n = Normalize(kv.Key); if (n.Length > 0) d[n] = kv.Value; }
        return d;
    }

    /// <summary>lowercase + alnum-only (e.g. "Sony Playstation 2" → "sonyplaystation2").</summary>
    private static string Normalize(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var arr = s.ToLowerInvariant().Where(c => (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')).ToArray();
        return new string(arr);
    }

    /// <summary>The RA console id for an LB platform name (matched on the hardlist, case/punct-insensitive),
    /// or null when not mapped. Pass the game's Platform (or ScrapeAs when available).</summary>
    public static int? ConsoleIdFor(string? platformName)
    {
        if (string.IsNullOrWhiteSpace(platformName)) return null;
        if (!NormToKey.TryGetValue(Normalize(platformName), out var key)) return null;
        return KeyToId.TryGetValue(key, out var id) ? id : (int?)null;
    }
}
