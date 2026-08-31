// LaunchBox platform name → RomM platform slug — the join every RomM client depends on.
//
// RomM keys platforms by its UniversalPlatformSlug values (IGDB-ish: "nes", "snes", "psx"), and clients
// map slug → their local emulator/core config. LaunchBox names are free text. This is the hand-curated
// join, the exact shape of Host/Ra/RaPlatformMap (same matching rule: lowercase, alnum-only), with the
// slug side read from RomM's handler/metadata/base_handler.py at the pinned upstream commit.
//
// A per-platform override ("Romm.PlatformSlug" in the options DB, platform scope) wins over the table, so
// a custom-named platform can be bound from the options page without touching code. An unmapped platform
// is not exported at all — a wrong slug would make a client launch the wrong emulator, which is worse
// than absence.

#nullable enable

using System;
using System.Collections.Generic;
using System.Text;
using LbApiHost.Host.Data;

namespace LbApiHost.Host.Romm;

internal static class RommPlatformMap
{
    // LB canonical platform name → RomM slug. Grouped by family; alternate LB spellings share a row.
    private static readonly Dictionary<string, string> NameToSlug = new(StringComparer.Ordinal)
    {
        // ── Nintendo ──
        { "Nintendo Entertainment System",       "nes" },
        { "Nes - Super Mario Bros. Hacks",       "nes" },
        { "Nintendo Famicom",                    "famicom" },
        { "Nintendo Famicom Disk System",        "fds" },
        { "Super Nintendo Entertainment System", "snes" },
        { "Snes - Super Mario World Hacks",      "snes" },
        { "Super Nintendo MSU-1",                "snes" },
        { "Super Famicom",                       "sfam" },
        { "Nintendo Satellaview",                "satellaview" },
        { "Nintendo Sufami Turbo",               "sufami-turbo" },
        { "Nintendo 64",                         "n64" },
        { "Nintendo 64DD",                       "64dd" },
        { "Nintendo GameCube",                   "ngc" },
        { "Nintendo Wii",                        "wii" },
        { "Nintendo Wii U",                      "wiiu" },
        { "Nintendo Switch",                     "switch" },
        { "Nintendo Switch 2",                   "switch-2" },
        { "Nintendo Game Boy",                   "gb" },
        { "Super Game Boy",                      "gb" },
        { "Nintendo Game Boy Color",             "gbc" },
        { "Nintendo Game Boy Advance",           "gba" },
        { "Nintendo DS",                         "nds" },
        { "Nintendo DSi",                        "nintendo-dsi" },
        { "Nintendo 3DS",                        "3ds" },
        { "New Nintendo 3DS",                    "new-nintendo-3ds" },
        { "Nintendo Virtual Boy",                "virtualboy" },
        { "Nintendo Pokemon Mini",               "pokemon-mini" },
        { "Nintendo Game & Watch",               "g-and-w" },

        // ── Sony ──
        { "Sony Playstation",                    "psx" },
        { "Sony Playstation 2",                  "ps2" },
        { "Sony Playstation 3",                  "ps3" },
        { "Sony Playstation 4",                  "ps4" },
        { "Sony Playstation 5",                  "ps5" },
        { "Sony PSP",                            "psp" },
        { "Sony PSP Minis",                      "psp-minis" },
        { "Sony Playstation Vita",               "psvita" },
        { "Sony PocketStation",                  "pocketstation" },

        // ── Sega ──
        { "Sega Genesis",                        "genesis" },
        { "Sega Mega Drive",                     "genesis" },
        { "Sega Master System",                  "sms" },
        { "Sega Game Gear",                      "gamegear" },
        { "Sega CD",                             "segacd" },
        { "Sega CD 32X",                         "segacd32" },
        { "Sega 32X",                            "sega32" },
        { "Sega Saturn",                         "saturn" },
        { "Sega Dreamcast",                      "dc" },
        { "Sega Dreamcast VMU",                  "vmu" },
        { "Sega SG-1000",                        "sg1000" },
        { "Sega SC-3000",                        "sc3000" },
        { "Sega Pico",                           "sega-pico" },
        { "Sega Model 1",                        "model1" },
        { "Sega Model 2",                        "model2" },
        { "Sega Model 3",                        "model3" },
        { "Sega ST-V",                           "stv" },
        { "Sega Hikaru",                         "hikaru" },
        { "Sega System 16",                      "system16" },
        { "Sega System 32",                      "system32" },
        { "Othello Multivision",                 "multivision" },

        // ── NEC ──
        { "NEC TurboGrafx-16",                   "tg16" },
        { "NEC PC Engine",                       "tg16" },
        { "NEC TurboGrafx-CD",                   "turbografx-cd" },
        { "NEC PC Engine-CD",                    "turbografx-cd" },
        { "PC Engine SuperGrafx",                "supergrafx" },
        { "NEC PC-FX",                           "pc-fx" },
        { "NEC PC-8801",                         "pc-8800-series" },
        { "NEC PC-9801",                         "pc-9800-series" },
        { "NEC PC-6001",                         "pc-6001" },

        // ── Atari ──
        { "Atari 2600",                          "atari2600" },
        { "Atari 5200",                          "atari5200" },
        { "Atari 7800",                          "atari7800" },
        { "Atari 800",                           "atari800" },
        { "Atari XEGS",                          "atari-xegs" },
        { "Atari ST",                            "atari-st" },
        { "Atari Jaguar",                        "jaguar" },
        { "Atari Jaguar CD",                     "atari-jaguar-cd" },
        { "Atari Lynx",                          "lynx" },

        // ── SNK ──
        { "SNK Neo Geo AES",                     "neogeoaes" },
        { "SNK Neo Geo MVS",                     "neogeomvs" },
        { "SNK Neo Geo CD",                      "neo-geo-cd" },
        { "SNK Neo Geo Pocket",                  "neo-geo-pocket" },
        { "SNK Neo Geo Pocket Color",            "neo-geo-pocket-color" },

        // ── Microsoft ──
        { "Microsoft Xbox",                      "xbox" },
        { "Microsoft Xbox 360",                  "xbox360" },
        { "Microsoft Xbox One",                  "xboxone" },
        { "MS-DOS",                              "dos" },
        { "Windows",                             "win" },
        { "Microsoft MSX",                       "msx" },
        { "Microsoft MSX2",                      "msx2" },
        { "Microsoft MSX2+",                     "msx2plus" },

        // ── Commodore ──
        { "Commodore 64",                        "c64" },
        { "Commodore 128",                       "c128" },
        { "Commodore VIC-20",                    "vic-20" },
        { "Commodore Plus 4",                    "c-plus-4" },
        { "Commodore PET",                       "cpet" },
        { "Commodore 16",                        "c16" },
        { "Commodore Amiga",                     "amiga" },
        { "Commodore Amiga CD32",                "amiga-cd32" },
        { "Commodore CDTV",                      "commodore-cdtv" },

        // ── Home computers ──
        { "Amstrad CPC",                         "acpc" },
        { "Amstrad GX4000",                      "amstrad-gx4000" },
        { "Sinclair ZX Spectrum",                "zxs" },
        { "Sinclair ZX-81",                      "zx81" },
        { "Sinclair ZX Spectrum Next",           "zx-spectrum-next" },
        { "Apple II",                            "appleii" },
        { "Apple IIGS",                          "apple-iigs" },
        { "Apple Mac OS",                        "mac" },
        { "Tandy TRS-80",                        "trs-80" },
        { "TRS-80 Color Computer",               "trs-80-color-computer" },
        { "Texas Instruments TI 99/4A",          "ti-994a" },
        { "Dragon 32/64",                        "dragon-32-slash-64" },
        { "Acorn Electron",                      "acorn-electron" },
        { "Acorn Archimedes",                    "acorn-archimedes" },
        { "BBC Microcomputer System",            "bbcmicro" },
        { "Oric Atmos",                          "atmos" },
        { "Thomson MO5",                         "thomson-mo5" },
        { "Sharp X68000",                        "sharp-x68000" },
        { "Sharp X1",                            "x1" },
        { "Fujitsu FM Towns Marty",              "fm-towns" },
        { "Fujitsu FM-7",                        "fm-7" },
        { "Camputers Lynx",                      "camputers-lynx" },
        { "Memotech MTX512",                     "mtx512" },
        { "SAM Coupé",                           "sam-coupe" },
        { "Tatung Einstein",                     "tatung-einstein" },
        { "Elektronika BK",                      "bk" },
        { "Exidy Sorcerer",                      "exidy-sorcerer" },
        { "Jupiter Ace",                         "jupiter-ace" },
        { "Sord M5",                             "sord-m5" },
        { "Tomy Tutor",                          "tomy-tutor" },
        { "Enterprise",                          "enterprise" },
        { "VTech CreatiVision",                  "creativision" },

        // ── Classic consoles ──
        { "3DO Interactive Multiplayer",         "3do" },
        { "Philips CD-i",                        "philips-cd-i" },
        { "Philips Videopac+",                   "videopac-g7400" },
        { "ColecoVision",                        "colecovision" },
        { "Coleco ADAM",                         "colecoadam" },
        { "Mattel Intellivision",                "intellivision" },
        { "Magnavox Odyssey",                    "odyssey" },
        { "Magnavox Odyssey 2",                  "odyssey-2" },
        { "Fairchild Channel F",                 "fairchild-channel-f" },
        { "GCE Vectrex",                         "vectrex" },
        { "Emerson Arcadia 2001",                "arcadia-2001" },
        { "RCA Studio II",                       "rca-studio-ii" },
        { "Bally Astrocade",                     "astrocade" },
        { "APF Imagination Machine",             "apf" },
        { "Interton VC 4000",                    "vc-4000" },
        { "Nuon",                                "nuon" },
        { "XaviXPORT",                           "xavixport" },
        { "Amazon Fire TV",                      "amazon-fire-tv" },
        { "Ouya",                                "ouya" },
        { "Android",                             "android" },
        { "Apple iOS",                           "ios" },
        { "Evercade",                            "evercade" },

        // ── Handhelds ──
        { "WonderSwan",                          "wonderswan" },
        { "Bandai WonderSwan",                   "wonderswan" },
        { "WonderSwan Color",                    "wonderswan-color" },
        { "Bandai WonderSwan Color",             "wonderswan-color" },
        { "Watara Supervision",                  "supervision" },
        { "Mega Duck",                           "mega-duck-slash-cougar-boy" },
        { "Hartung Game Master",                 "hartung" },
        { "Bit Corporation Gamate",              "gamate" },
        { "Tiger Game.com",                      "game-dot-com" },
        { "Nokia N-Gage",                        "ngage" },
        { "GamePark GP32",                       "gp32" },
        { "GPD GP2X",                            "gp2x" },
        { "Epoch Super Cassette Vision",         "epoch-super-cassette-vision" },
        { "Epoch Game Pocket Computer",          "epoch-game-pocket-computer" },
        { "Entex Adventure Vision",              "adventure-vision" },
        { "Casio PV-1000",                       "casio-pv-1000" },
        { "Casio Loopy",                         "casio-loopy" },
        { "VTech V.Smile",                       "vsmile" },
        { "Super A'Can",                         "super-acan" },
        { "Playdate",                            "playdate" },
        { "Arduboy",                             "arduboy" },
        { "Pocket Challenge V2",                 "pocket-challenge-v2" },
        { "Pocket Challenge W",                  "pocket-challenge-w" },

        // ── Arcade & engines ──
        { "Arcade",                              "arcade" },
        { "MAME",                                "arcade" },
        { "Daphne",                              "arcade" },
        { "Sammy Atomiswave",                    "arcade" },
        { "Sega Naomi",                          "arcade" },
        { "Sega Naomi 2",                        "arcade" },
        { "Capcom CPS1",                         "cps1" },
        { "Capcom CPS2",                         "cps2" },
        { "Capcom CPS3",                         "cps3" },
        { "ScummVM",                             "scummvm" },
        { "OpenBOR",                             "openbor" },
        { "Uzebox",                              "uzebox" },
        { "TIC-80",                              "tic-80" },
        { "Pinball",                             "pinball" },
        { "Doom",                                "doom" },
    };

    private const string OverrideKey = "Romm.PlatformSlug";

    // Alnum-lowercase index for tolerant matching ("Sega Mega Drive" vs "sega megadrive").
    private static Dictionary<string, string>? _normIndex;
    private static readonly object _lock = new();

    /// <summary>The RomM slug for a LaunchBox platform name, or null when it is not exported. The options
    /// override ("Romm.PlatformSlug", platform scope) wins over the table; "-" disables the platform.</summary>
    public static string? SlugFor(string? lbPlatformName)
    {
        if (string.IsNullOrWhiteSpace(lbPlatformName)) return null;

        try
        {
            var over = LiteBoxOptionsDb.Get(LiteBoxOption.ScopePlatform, lbPlatformName!, OverrideKey);
            if (!string.IsNullOrWhiteSpace(over))
                return over!.Trim() == "-" ? null : over.Trim();
        }
        catch { }

        if (NameToSlug.TryGetValue(lbPlatformName!, out var exact)) return exact;

        var index = NormIndex();
        return index.TryGetValue(Normalize(lbPlatformName!), out var byNorm) ? byNorm : null;
    }

    private static Dictionary<string, string> NormIndex()
    {
        lock (_lock)
        {
            if (_normIndex != null) return _normIndex;
            var idx = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in NameToSlug)
                idx[Normalize(kv.Key)] = kv.Value;
            _normIndex = idx;
            return idx;
        }
    }

    private static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }
}
