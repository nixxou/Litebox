# RA engine migration plan — promote the plugin engine to the LiteBox base

Companion to `ra-parity-audit.md`. Goal: replace the native lite RA producer with a full port of
the ExtendDB plugin's RA engine, exposed as the existing "RetroAchievements" base option. The
DISPLAY layer (RaService / RetroAchievementsCard / WebRa / RaBadges / RaXmlWriter) is out of
scope and keeps consuming the same two game fields — the seam between the layers is, and must
remain, `RetroAchievementsHash` / `RetroAchievementsId` on the game.

## Storage decision (the "different .db" question, settled)

Today: the plugin stores everything RA in its own `cache\extenddb-cache.db` (merged rom_hash /
archive_entry+RA / parse_state / ra_game / ra_hash / ra_system / platform); LiteBox has NO RA
SQLite — per-console JSON catalogs + game-XML fields — and its OWN archive DB
(`rom-archive-cache.db`) deliberately without RA columns. Two parallel archive caches exist on
disk when the plugin runs under LiteBox.

Decision: **extend LiteBox's `rom-archive-cache.db`** (single file, following the plugin's own
merge rationale — cheap joins, one path-signature scheme, one delete-to-rebuild):
- `archive_entry` += `RetroAchievementsHash TEXT`, `RetroAchievementsId INTEGER` (+ index on id).
- `archive` += `parse_state INTEGER DEFAULT 0` (0 unparsed / 1 ok / 2 failed) — RA full-parse
  state, distinct from the listing's own flags.
- New tables (schema copied from the plugin's CacheDb):
  `rom_hash(signature PK, path, size, RetroAchievementsHash, RetroAchievementsId, computed_at)`,
  `ra_game(id PK, console_id, title, console_name, image_icon, num_achievements,
  num_leaderboards, points, date_modified, forum_topic_id)`,
  `ra_hash(hash PK, game_id)`,
  `ra_console(id PK, key, name, games_refreshed_at, next_refresh_at)` (the plugin's `ra_system`
  minus the RAHasher-usage parsing — our frozen RaPlatformMap already carries hashability).
- NOT ported: the plugin's derived `platform` table — `RaPlatformMap` + `ra-platform-overrides.json`
  already play that role.
- Signatures use the existing portable path scheme (ArchiveListingCache.PortablePath chokepoint)
  so keys match the ROM module's rows.
- Migration: `user_version` bump with additive ALTERs (existing rows untouched). One-time import
  of fresh `ra-cache\catalog-<console>.json` files into ra_game/ra_hash (avoids refetching every
  console), then the JSON catalogs are retired. `ra-cache\<raid>.json` (display layer) untouched.

## Phases

**P0 — Store.** Schema migration + accessors (port of RetroAchievementsDb/ArchiveCacheDb RA
surface: UpsertEntryRetroAchievements, SetRetroAchievements*, LookupGameId, ReplaceConsoleCatalog,
ReResolveScannedIds, ClearScannedHashes, GetEntryRaTitles, GetRaMatchedPaths). JSON catalog import.

**P1 — Catalogue engine.** Port RaCatalog + RaCanaries (frozen table copied verbatim) +
RaRefreshScheduler: 30-min tick paused while a game runs or an extraction is in progress
(LiteBox signals: `_gameRunning`, RomExtractor state), per-console TTL 20h + random(0..8h),
error backoff 2h + random(0..1h), apply guards (empty reject, count-delta <0.8× reject, canary
gate). Panel "Refresh"/"Refresh all" rewired. `RaGamesRefreshHours` option.
**Drop policy: [DECISION]** A = full plugin parity (activate new ids + canary-gated drop of dead
raids at refresh — recommended, the machinery is ported anyway) / B = keep never-drop.

**P2 — Scanner.** Port RaScanner: gather base ApplicationPath + additional-application versions
(excluding run-before/after), dedupe; dispatch arcade (MD5 basename, id 27) / archive
(`--arc-details --arc-ext <platform ROM extensions>`) / plain file; incremental skip
(rom_hash present / parse_state==ok → id re-resolve only); store write-through + IGame writes
(SetField, single flush). Panel scan buttons rewired; manual scans gated on the module flag;
panel caption text fixed (no LaunchBox to stop).

**P3 — On-select.** Resolve consults the STORE first (by signature) before ever spawning
RAHasher; `COULDNTFILEHASH` treated as absent everywhere; multi-ROM pick = two-pass
(raid-bearing entries ordered by the ROM module's SortForDisplay priorities, else first hashed).
Honours mode + per-platform enable as today.

**P4 — Launch correction (the plugin's Phase 3/3b).** On the native launch path
(OnGameStarted / launch context), resolve the EXACT launched entry — the Select-ROM pick
(RomSelectionStore / launch history) when armed, else the archive pick — and overwrite the IGame
hash/raid on mismatch. Post-extraction bytes-authoritative heal hook in the extraction path,
raid-only guard (never downgrade a valid raid). "On launch" mode becomes real: correction at
launch, no on-select hashing.

**P5 — Surfaces + cleanup.** Feed per-entry raids to: desktop RomPickerWindow RA column, web
ArchiveListingApi `retroAchievements` titles, `RetroAchievementsBonus` auto-pick (now live).
Retire RaResolveLite/RaScanLite internals (thin shells delegating to the engine), RelinkRaid →
store-based ReResolveScannedIds. `RaHasherPath` override option.

**P6 — Plugin coexistence.** Keep the defer bridge unchanged (plugin present + its RA module
active → plugin produces, LiteBox engine idles): single producer at a time, both write the same
game fields. The two archive DBs continue to coexist; no attempt to share files with the plugin.

## Validation checklist
- Multi-ROM archive where the launched entry ≠ first raid-bearing entry → IGame corrected at launch.
- Library imported from LaunchBox with `COULDNTFILEHASH` → healed on select/scan.
- Additional-application version hashed and pickable.
- Incremental rescan of an unchanged platform ≈ instant (no RAHasher spawns).
- Catalogue refresh with simulated 500/empty/truncated responses → backoff, no destructive apply.
- Picker shows RA titles; achievement-bearing version wins auto-pick.
- Module off → no hashing anywhere (incl. manual buttons); display layer still shows stored raids.
