# RetroAchievements parity audit — plugin RA MODULE vs LiteBox

Comparison of the ExtendDB plugin's RetroAchievements MODULE against LiteBox's RetroAchievements
implementation. Snapshot 2026-07-18. Refs: plugin paths relative to the ExtendDB plugin tree,
LiteBox paths to LbApiHost.

Legend: **[LOSS]** unapproved regression · **[ADAPTED]** same behavior, different mechanism ·
**[WAIVED?]** documented drop needing explicit sign-off · **[BETTER]** LiteBox exceeds the plugin.

---

## 0. Structural verdict (read first)

The plugin's RA module is a full engine (~2,800 lines: SQLite storage, scanner, catalogue with
guarded refresh, on-select supplier, on-launch corrector, canaries, scheduler). In LiteBox, the
`LbModule.RetroAchievements` "module" is **a gating flag + a config panel over the NATIVE lite
layer** (Host/Ra/*). The module engine as such was never ported; the native layer covers a subset
of it and adds a rich user-progress surface the plugin never had. Everything below itemizes that
subset relationship.

## 1. Real losses

### 1.1 [LOSS — severe] No launch-time correction; "On launch" mode is a silent no-op
- Plugin: `RaOnLaunch.CorrectIGame` from `GameLaunchHook.OnBeforeGameLaunching` (every mode)
  resolves the EXACT entry being launched (honouring the Select-ROM context) and overwrites the
  IGame hash/raid on mismatch; plus `HealLaunchedEntryAsync` (bytes-authoritative, raid-only
  guard) from the Process.Start extraction patch. Phases 3/3b of docs/ra-hashing-redesign.md.
- LiteBox: NO RA code on any launch path (verified: HostServices/OnBeforeGameLaunching has none;
  `RaResolveLite.Resolve` has exactly two callers — select + scan). The RaPanel "Auto-update:
  On launch" combo value exists (`RaPanelSupport.ModeOnLaunch`) and makes on-select a no-op
  (`MainWindow.LoadRaPanel` honours it) — but nothing fires at launch, so choosing it disables
  auto-resolution entirely.
- Consequence: multi-ROM archives keep the select-time prefer-raid entry's hash even when the
  user launches a different entry; the mode combo is misleading.

### 1.2 [LOSS — severe] Per-entry RA storage never ported → three visible features dead
- Plugin: `rom_hash` + `archive_entry.RetroAchievementsHash/Id` + `archive.parse_state` in
  `cache\extenddb-cache.db`. Feeds: (a) Select-ROM per-entry RA game titles (desktop
  ArchiveListWindow + web ArchiveListingApi `retroAchievements` field → 🏆 glyph/RA column),
  (b) auto-pick `RetroAchievementsBonus` (default 10000) so the achievement-bearing version
  auto-extracts, (c) incremental scan skip (`parse_state==ParseOk` → no re-hash), (d)
  `ReResolveScannedIds` (re-link ids after a catalogue refresh without re-hashing).
- LiteBox: `rom-archive-cache.db` header says "deliberately carries NO RA columns"; RomExtractor
  `RaTitle` stays "" natively; web ArchiveListingApi emits `retroAchievements: ""`;
  `RomConfig.RetroAchievementsBonus` (10000) is dead weight; every forced resolve re-hashes.
- Consequence: picker RA column/glyph dead, RA-aware auto-pick dead, scans re-hash needlessly.

### 1.3 [LOSS] Catalogue refresh guards & scheduler reduced
- Plugin: 30-min heartbeat (`RaRefreshScheduler`), paused while a game runs or an extraction is
  in progress; per-console TTL 20h + random(0..8h) jitter; error backoff 2h + random(0..1h)
  without touching last-success; apply guards before a destructive replace: empty-result reject,
  count-delta < 0.8× reject, per-console CANARY (frozen hash→raid must be present) gating the
  right to DROP raids; Phase 4 `RaGameSync.RefreshPlatform` activates new ids and drops dead
  ones under that gate.
- LiteBox: 24h TTL on file mtime + non-empty-JSON guard + optional startup refresh (≤3 consoles
  older than 48h). No scheduler, no jitter, no error backoff (a failing console retries at next
  opportunity), no count-delta, no canaries — and no drop at all: `RelinkRaid` never clears a
  raid, so ids dead on RA's side live forever.
- Nuance: never-drop makes canaries less critical (their role is authorizing drops), but the
  plugin's activate+guarded-drop lifecycle is simply absent.

### 1.4 [LOSS] Per-version hashing absent
- Plugin `RaScanner.GatherFiles`: base ApplicationPath PLUS every additional-application
  "version" (excluding run-before/after helpers), deduped. LiteBox hashes ApplicationPath only.

### 1.5 [LOSS] `--arc-ext` filter absent
- Plugin passes the platform's RomExtensions CSV so RAHasher hashes only real ROM entries.
  LiteBox passes "" → every entry hashed (slower, parasite entries can win prefer-raid… only if
  they map to a raid, which is unlikely but the filter also cuts scan time).

### 1.6 [LOSS] `COULDNTFILEHASH` sentinel never healed
- Plugin: scan + select + launch all treat LB's sentinel as "no hash" and heal it.
- LiteBox: `RaResolveLite.Resolve(force:false)` skips on ANY non-empty hash — a library imported
  from a real LaunchBox install carrying `COULDNTFILEHASH` is never repaired (verified: zero
  occurrences of the sentinel in LbApiHost). Lite scan same (`has hash → RelinkRaid` only).

### 1.7 [LOSS — minor] Config/gating details
- Manual RaPanel scans run without re-checking `LbModules.On(RetroAchievements)` (tab reachable
  regardless) — the plugin gated every RA action on the module.
- Plugin config absent in LiteBox: `RaHasherPath` override (exe path pinned to ThirdParty),
  `RaGamesRefreshHours` (24h hardcoded), plugin-own `RetroAchievementsApiKey` with LB-settings
  fallback (LiteBox reads LB Settings.xml only — acceptable since LiteBox IS the LB-side host,
  but no UI to enter a key without a LaunchBox install having written it).

## 2. Adapted (same behavior, different mechanism — justified)

- **Blocking LB's scan-on-select hashing** (AchievementHashAction wrapper + Process.Start
  safety-net patch): no LaunchBox hasher exists inside LiteBox — the host's own resolver IS the
  replacement. The RaPanel caption still advertises "stops LaunchBox extracting your ROMs" —
  cosmetic text to fix.
- **Storage layout**: SQLite catalogue (`ra_game`/`ra_hash`/`ra_system`/`platform`) → per-console
  JSON maps + frozen in-code `RaPlatformMap` (verbatim copy of the plugin hardlist) + user
  overrides JSON. Functionally equivalent for lookup; the per-ENTRY half is 1.2, a real loss.
- **IGame writes**: plugin dual-host reflection writer → `ILiteBoxFields.SetField` op-log only
  (LiteBox is the only host); surgical change-only writes preserved; single flush per scan.
- **Hashing engine**: same `RahasherExtendDB.exe` (RVZ-capable), same arcade MD5(basename) rule
  (id 27), same `--arc-details` in-memory archive hashing, same GC/Wii-through-RAHasher approach
  (the plugin's DolphinTool concern was about BLOCKING LB's DolphinTool, not using it).
- **Prefer-raid archive pick**: plugin two-pass with SortForDisplay priority ordering; LiteBox
  inline first-raid-bearing-else-first. Same intent; ordering nuance only matters when several
  entries have raids (plugin picks by display priority, LiteBox by archive order) — worth
  aligning when 1.2 lands.

## 3. OUT OF MODULE SCOPE — the display layer (host-owned on both sides)

Scope correction: the module's contract, on BOTH sides, ends at writing
`RetroAchievementsHash`/`RetroAchievementsId` on the game. What displays that id is a separate,
host-owned layer:
- Under LaunchBox/BigBox: LB's NATIVE RA panel consumes the id the plugin module wrote — the
  module displays nothing itself.
- Under LiteBox: LB's panel doesn't exist, so the host carries its own replacement —
  `RaService` (`API_GetGameInfoAndUserProgress` + `API_GetGameProgression`), the desktop
  `RetroAchievementsCard`, `WebRa`'s `ra` block, badge cache, `RaXmlWriter` medians (a
  display-side cache in the XML). This is NOT a module superset; it is the host-side analogue of
  LB's native panel, consumer-only, indifferent to which engine produced the raid.
- Note: no theme JS (plugin's or ours) consumes the rich `ra` block yet — the desktop card is
  the only rich consumer. Candidate future theme work, not a bug.
- Also host-side niceties independent of the engine comparison: startup rolling refresh,
  platform-mapping dialog, guarded catalogue fetch.

Practical consequence for the migration plan (§4): promoting the plugin engine into the base
only touches the PRODUCER layer; the display layer is untouched and keeps reading the same two
game fields. The only entanglement to unpick in LiteBox is that `MainWindow.LoadRaPanel`
currently triggers both layers in one place — the seam must stay the game fields.

## 4. Prioritized fix plan (pending approval)

| # | Item | Ref | Size |
|---|---|---|---|
| R1 | Launch-time correction: resolve the EXACT launched entry (launch history / Select-ROM pick) in the launch path and overwrite IGame hash/raid on mismatch; make "On launch" mode real | 1.1 | M |
| R2 | Per-entry RA store (hash+raid per archive entry + parse_state equivalent) feeding: picker RaTitle (desktop+web), RetroAchievementsBonus auto-pick, incremental scan skip, relink-without-rehash | 1.2 | L |
| R3 | Catalogue lifecycle: error backoff + jitter, optional scheduler tick (or keep startup-only, documented), count-delta guard; decide drop policy (port canaries+drop, or keep never-drop documented) | 1.3 | M / decision |
| R4 | Hash additional-application versions in scans | 1.4 | S |
| R5 | `--arc-ext` from the platform's ROM extensions | 1.5 | S |
| R6 | Treat `COULDNTFILEHASH` as absent everywhere (select, scan, relink) | 1.6 | S |
| R7 | Gate manual scans on the module flag; fix the RaPanel caption text; optional `RaHasherPath`/`RaGamesRefreshHours` config | 1.7, §2 | S |
| R8 | Align archive pick ordering with SortForDisplay priorities (with R2) | §2 | S |
