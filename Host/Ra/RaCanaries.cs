// Hardcoded RetroAchievements CANARY table — verbatim port of the plugin's RaCanaries (gathered
// 2026-06-28 from API_GetGameList?i&h=1, verified, then FROZEN — never re-derive at runtime from
// the current pull; that would defeat the guard).
//
// Per RA console: known (rahash → raid) pairs for flagship games guaranteed to stay on RA. Used
// by the catalogue refresh as a GENUINENESS guard before any destructive drop of a game's RA id:
//   • require ONE canary present in the freshly-pulled hash set → the response is genuine RA data;
//   • a SEPARATE count-delta guard (new list not drastically smaller than the last known) handles
//     TRUNCATION — the canary alone does not.
// Activation (adding a raid) is never gated; only the DROP is. Consoles with no entry here are
// never dropped for (safe default). Hashes are lowercase; compare case-insensitively.

#nullable enable

using System;
using System.Collections.Generic;

namespace LbApiHost.Host.Ra;

/// <summary>Frozen per-console RA canary (hash → raid) pairs. See file header.</summary>
internal static class RaCanaries
{
    // consoleId (RC_CONSOLE_*) → known (hash, raid) pairs. require-ONE present = data is genuine.
    public static readonly IReadOnlyDictionary<int, (string hash, int raid)[]> ByConsole =
        new Dictionary<int, (string hash, int raid)[]>
    {
        [1]  = new[] { ("1b1d9ac862c387367e904036114c4825", 1),     ("8e2c29a1e65111fe2078359e685e7943", 10) },    // MD — Sonic the Hedgehog / Sonic the Hedgehog 2
        [2]  = new[] { ("20b854b239203baf6c961b850a4a51a2", 10003), ("753437d0d8ada1d12f3f9cf0f0a5171f", 10133) }, // N64 — Super Mario 64 / F-Zero X
        [3]  = new[] { ("38bb405ba6c6714697b48fb0ad15a2a1", 228),   ("241b6f268d58326679afb56d80f2bdf8", 3174) },  // SNES — Super Mario World / Kyuuyaku Megami Tensei
        [4]  = new[] { ("3132056c8f17e4088b95e4264ca59575", 724),   ("4ab78239e79ba059d0d7aa7a629a9474", 2384) },  // GB — Pokemon Red / Bomberman GB
        [5]  = new[] { ("3cded5508b03afb52b41dfbe9955628b", 559),   ("21f934f5738b6f975709e46646b83e86", 668) },   // GBA — Zelda: The Minish Cap / Pokemon Emerald
        [6]  = new[] { ("301899b8087289a6436b0a241fbbb474", 810),   ("825de040ea4dff66661693f8712b1bdb", 710) },   // GBC — Pokemon Crystal / Zelda: Oracle of Ages
        [7]  = new[] { ("3b74debfb73b6c2df4e986ea7a44ac34", 1675) },   // NES — Eliminator Boat Duel
        [8]  = new[] { ("319ec97f9d3ae11e1d76c959421d19bb", 5515) },   // PCE — Ninja Ryuuken Den
        [9]  = new[] { ("fbd25c6c1f42973b214ace4646c8632a", 14144) },  // SCD — Earthworm Jim: Special Edition
        [10] = new[] { ("08cae0a96d9ee07001c6e1c247d407c6", 11823) },  // 32X — Virtua Racing Deluxe
        [11] = new[] { ("28110d3027da306210a9d1fb41010bbf", 9962),  ("3d9a8d5c2d6d3f8ff63a8f7c77ffa983", 9998) },  // SMS — Sonic the Hedgehog / Alex Kidd in Miracle World
        [12] = new[] { ("12d8b9270b6fef6d957aeb5c1371815e", 11242), ("6357455df44bf720f1feba6b251c39d5", 14373) }, // PS1 — Final Fantasy VII / Arc the Lad II
        [13] = new[] { ("b4acbd3c544a0d92cc8ad1380bf8a810", 10669) },  // Lynx — Crystal Mines II
        [14] = new[] { ("3c6c6e42d10b76bbccacf125f5d2b769", 14397) },  // NGP — SNK vs. Capcom: Card Fighters 2
        [15] = new[] { ("e9b45d6455e0753b8e0e825a36458253", 14330) },  // GG — Ninku Gaiden
        [16] = new[] { ("326d2c2de5c8957637780da332ab9dbb", 9602),  ("0d2a8a5d60ab2a761bbf7fe39ec844ac", 6928) },  // GC — Super Smash Bros. Melee / Mega Man X Collection
        [17] = new[] { ("602bc9953d3737b1ba52b2a0d9932f7c", 834) },    // JAG — Tempest 2000
        [18] = new[] { ("5cbdd195886b551e759f7e592317404a", 8376) },   // DS — Mega Man Zero Collection
        [19] = new[] { ("4e0d0d2f2c5d3c13d758b027bbcc059f", 189),   ("9b943b134b9fd557975ff3f09de75e1d", 195) },   // Wii — Super Mario Galaxy / Super Smash Bros. Brawl
        [21] = new[] { ("e906d0678f2ca07476e578c6879de0c5", 20255) },  // PS2 — Sonic Mega Collection Plus
        [23] = new[] { ("ffa665173861c544219565e000f79173", 18377) },  // MO2 — Pick Axe Pete
        [24] = new[] { ("f100caf74574aeabe5084f3b5c9e03d2", 14717) },  // MINI — Pokemon Shock Tetris
        [25] = new[] { ("fca4a5be1251927027f2c24774a02160", 11611) },  // 2600 — H.E.R.O.
        [27] = new[] { ("8db1903d29deae4b52b4cbdcaa48c3c7", 15882) },  // ARC — Tetris: The Absolute - The Grand Master
        [28] = new[] { ("05ba60f0ac1aacedd57321f0ed5f62ca", 11700) },  // VB — Jack Bros.
        [29] = new[] { ("01ec86d19248514baf263f0b60df4d0c", 10504) },  // MSX — Metal Gear 2: Solid Snake
        [33] = new[] { ("2cbd1f9f4927f9618390340f56f116a6", 6787) },   // SG1K — Star Force
        [37] = new[] { ("bd4f7a1b5104a56116b5df7b5e568af3", 10823) },  // CPC — Nemesis
        [38] = new[] { ("d641fc4c79ec5628f3ecaad5b82b85b3", 27575) },  // A2 — Ultima V: Warriors of Destiny
        [39] = new[] { ("4cc79d222e326fe2ba906930673c7466", 14548) },  // SAT — Sonic Jam
        [40] = new[] { ("d98b50bdfabec5be349bb8ef4ce4fabf", 19152) },  // DC — Sega Smash Pack: Volume 1
        [41] = new[] { ("8b9e9a82fe447607cf3c07d6852e21b1", 17976) },  // PSP — Monster Hunter Portable 3rd
        [43] = new[] { ("71841df1c47503cce8a735dffd3898db", 16872) },  // 3DO — Wolfenstein 3D
        [44] = new[] { ("91c7b5d4ea94092f6e18da4775656d4b", 12769) },  // CV — Time Pilot
        [45] = new[] { ("2828ce0b76c7f8a5e3a4569fac5c8eb9", 18532) },  // INTV — Pinball
        [46] = new[] { ("95370908561a782edf138df41ccb8f18", 7495) },   // VECT — Scramble
        [47] = new[] { ("8086bfe5e0a905e77ffef517aca77da1", 13705) },  // 80/88 — Dragon Slayer Level 1.1
        [49] = new[] { ("7112e47698346656e5ba5656e11c0a86", 9786) },   // PC-FX — Pia Carrot e Youkoso!!
        [51] = new[] { ("f18b3b897a25ab3885b43b4bd141b396", 13364) },  // 7800 — Joust
        [53] = new[] { ("12c3cca0ef52343509dda1c1fb4c1b65", 4965) },   // WS — SD Gundam: Operation U.C.
        [56] = new[] { ("4c8fe6d79fb876a41e5c1609267366d4", 23879) },  // NGCD — Super Sidekicks 3
        [57] = new[] { ("f7bf7d55a7660ffa80d08ad1ba903ff7", 8990) },   // CHF — Videocart-26: Alien Invasion
        [63] = new[] { ("1cce14e748212e75c21966f12ac5fe1a", 17620) },  // WSV — Witty Cat
        [69] = new[] { ("e07e5a8e3c65cb78d7dc39d905de8147", 10794) },  // DUCK — Bomb Disposer
        [71] = new[] { ("6d764ed131ab107fe1e30df996d88bf5", 8676) },   // ARD — Paqman
        [72] = new[] { ("020732709672a60b148bb38f12c7df35", 19297) },  // WASM4 — wasm4nia
        [73] = new[] { ("7391a9bd972e5eeaaeeeca76879abda4", 21580) },  // A2001 — Blackjack and Poker
        [74] = new[] { ("4ff7209dd64f98e6adbbf94888871026", 21634) },  // VC4000 — Golf
        [75] = new[] { ("fb8c205c7aa983c379068e9581364c2c", 37507) },  // ELEK — Submarine + Racing
        [76] = new[] { ("e9f07a66e2e793bdbf8538ac110bfb7c", 6700) },   // PCCD — Ys: Book I & II
        [77] = new[] { ("76af127fbb4f621d9c128df809b27d4c", 21663) },  // JCD — Baldies
        [78] = new[] { ("03754af05487d3dd1914d641051e4a2c", 2902) },   // DSi — Cave Story
        [80] = new[] { ("0361bd8008d583670210627eda1fe482", 24717) },  // UZE — Atomix
        [81] = new[] { ("31559f43a673a178b2f9498f7ddefc79", 7187) },   // FDS — Final Commando: Akai Yousai
    };

    /// <summary>Canary pairs for a console, or empty (→ never drop for that console).</summary>
    public static (string hash, int raid)[] For(int consoleId) =>
        ByConsole.TryGetValue(consoleId, out var v) ? v : Array.Empty<(string, int)>();
}
