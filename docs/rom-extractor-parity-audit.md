# ROM extractor parity audit — plugin (ArchiveMGS) vs LiteBox Rom module

Full comparison, snapshot 2026-07-18. Plugin refs relative to the ExtendDB plugin tree, LiteBox
refs to LbApiHost. Legend: **[LOSS]** unapproved regression · **[ADAPTED]** same behavior,
different mechanism · **[DECISION]** needs sign-off · **[BETTER]** LiteBox exceeds the plugin.

## 0. Overall verdict

Unlike the RA module, the ROM extractor WAS genuinely ported: all six plan slices (R1-R6) are
implemented, the reflection bridge is deleted, and the five extraction dimensions
(flat/preserve + selective companions, disc/CHD conversion, m3u rewrite, texture packs, RAM
disk) exist in native code sharing the plugin's engine semantics (same modes, same cache
layout `<root>[\tmp]\<SIG>\<P|F>[\subdir]`, same band+LRU eviction, same signatures, same
profile cascade and defaults, byte-identical web payloads, shared SortForDisplay across all
surfaces). The gaps below are real but localized.

## 1. Real losses

### 1.1 [LOSS] RetroAchievementsBonus never influences the LAUNCH auto-pick
Plugin passes `row.RetroAchievementsBonus` + `ArchiveCacheDb.GetRaMatchedPaths(sig)` into
`PickAutoLaunch` at every launch pick site (ProcessStartLogPatch.cs:606-608, 830-832, 984+ —
verified first-hand). LiteBox passes `raBonus: 0, raMatchedPaths: null` at its three sites
(RomExtractor.cs:228 main pick, :360 fast-path, :665 m3u) — so the achievement-bearing version
wins the DISPLAY order (fixed in the RA P5 work) but not the actual auto-extraction. Also the
picker's Points column shows the raw score without the bonus (plugin displayed score+bonus).

### 1.2 [LOSS] Launch gate uses a hardcoded {zip,7z,rar} archive test
`HostServices.IsArchive` (:736-738) ignores `RomConfig.ArchiveExtensions`
(`zip,7z,rar,tar,gz,bz2,xz`) — tar/gz/bz2/xz archives never reach `ResolveLaunch` even though
the config (and `RomExtractor.IsArchive`) advertise them. The RA resolver's own archive test
(`RaResolveLite.ArchiveExts`, `RaLaunchCorrect`) has the same narrow list, while the plugin's
RA scanner honoured the configured list.

### 1.3 [LOSS] Extraction state never signalled
`RecentState.MarkExtracting/MarkExtractionDone` exist but are NEVER called (verified) — the
plugin bracketed every extraction (ProcessStartLogPatch.cs:500,800). Consequences: the web
double-launch refusal (`BigBoxMutationApi` "extraction in progress") can't trigger, the RA
catalogue heartbeat's idle test never sees an extraction, and the web epoch never bumps during
one.

### 1.4 [LOSS] Desktop picker feature cuts (R2 "read-only" decision, never re-approved)
`RomPickerWindow` vs the plugin's `ArchiveListWindow`:
- no ★ favourite TOGGLE (click / right-click Set-Unset) — desktop can display but not pin;
  only the web favorite route toggles;
- no ✓ cached column (plugin probed the cache and tinted green; sortable columns shifted);
- no right-click "Extract To…";
- no Play split-button + per-emulator caret (LiteBox's picker is selection-only; LaunchButtons
  owns the launch — partially structural, but the plugin picker could launch directly with a
  chosen emulator);
- no Hi-Res Texture install/remove section (the ENGINE extracts textures at launch —
  RomExtractor.ExtractTextures — but the manual install/remove UI is desktop-picker-only in
  the plugin and absent here).

### 1.5 [DECISION] External tools not shipped
chdman.exe / DolphinTool.exe (conversion) and RamDiskHelper.exe (+ImDisk) are resolved from
`ThirdParty\RomExtractor` etc. but LiteBox deploys none of them (the plugin bundled all three
in its thirdparty). On a fresh LiteBox install: Convert/ConvertAfterExtract/disc-conversion
silently pass through, RAM disk silently degrades. Options: (a) embed in the NativeInstaller
payload like RahasherExtendDB (size: chdman ~15MB, DolphinTool ~30MB — repo/payload weight),
(b) fetch-on-first-use from a release asset, (c) document the manual drop-in. Needs a call.

### 1.6 [LOSS — minor] Config/cosmetic
- Global `ArchiveExtensions` / `DiscImageExtensions` / `MetadataExtensions` have no UI (ini
  hand-edit only); per-profile lists are editable.
- `SubDirScheme.PlatformCode` degrades to the plain platform name (PlatformMapper not ported —
  RaPlatformMap's frozen key table could serve the same role).
- `LbModules` Rom card description still says "defers to the ExtendDB plugin at launch" —
  false since R6.
- `RomLaunchResult.Handled` is set but never read (dead field).

## 2. Adapted (justified by the host change)

- **Interception**: Harmony Process.Start patch + PathSubstitution 4-rule cmdline rewrite +
  AutoExtractSuppressor (transient getter patch) → native `ResolveLaunchRomPath` returning the
  swapped path before args are built. LiteBox OWNS the spawn, so substitution/suppression have
  no object; %var% expansion + HideConsole are handled natively (they were LB's job before).
- **Pick carriers**: 60s cross-process `ArchiveLaunchContext`/`LaunchOverrideRegistry` →
  in-proc single-shot `RomLaunchPick` + persisted per-(game,version) `RomSelectionStore`
  [BETTER: survives restarts, explicit Clear] + op-log launch history (extracted_rom_path).
- **Config homes**: registry + ArchiveMgs.json → ini `[Rom]` + rom-profiles.json (same fields,
  same defaults, same cascade — verified field-by-field).
- **Metadata overlay**: reimplemented with System.Text.Json under `Core\litebox\rom-metadata\`
  [BETTER: `[[JSONDATA_ALL]]` + per-entry JSON narrowing].
- **7z listing**: `7z.exe l -slt` parser (plan option B) reproducing Path/Size/CRC.
- **Web payloads**: byte-identical shapes and gating semantics; LiteBox routes delegate to the
  single shared `ListEntriesDetailed` impl (plugin re-implemented inline per surface).

## 3. Parity verified OK

Modes flat(7z e)/preserve(7z x) with selective picked+companions (CompanionExtensions "bin"
double-duty rule), mode-switch hygiene (drop other mode's subdir), cache-hit fast-path with
companion validation, Title rename (flat only, launched file only), band `[CacheMinMb,
CacheMaxMb]` → `\tmp` ephemeral, LRU per-<SIG> under CacheMaxGb, tmp purge + RAM-disk unmount
on exit, listing fast-hit (skipped on ForcePriority/ConvertAfterExtract/textures), version-anchored
m3u disc/side selection plus rewrite (M3uInput) via per-line ProcessFileForM3u, disc Convert/Copy branch, ConvertAfterExtract
descriptor switch (.cue/.gdi over raw .bin), scoring/sort/pick chain (favourites display-only),
per-archive history (5 MRU + favourites on short content sig), profile version gates,
GoodSet/HackSet presets, filter/sort/keyboard in the picker.

## 4. Prioritized fix list (pending approval)

| # | Item | Ref | Size |
|---|---|---|---|
| E1 | Pass RetroAchievementsBonus + RaStore.GetRaMatchedPaths to the 3 launch pick sites; show bonus in the picker Points | 1.1 | S |
| E2 | Config-driven archive test on the extractor launch gate + align the RA resolver's archive test | 1.2 | S |
| E3 | Bracket extractions with RecentState.MarkExtracting/Done (ResolveLaunch + flat fallback) | 1.3 | S |
| E4 | Picker parity: ★ toggle + right-click menu (Set/Unset, Extract To…) + ✓ cached column; texture install section + Play-with split as a second step | 1.4 | M |
| E5 | Tools shipping decision: chdman/DolphinTool/RamDiskHelper (payload / fetch / doc) | 1.5 | decision |
| E6 | Global extension lists in the Rom panel header | 1.6 | S |
| E7 | PlatformCode subdir via the frozen platform-key table | 1.6 | S |
| E8 | Fix the Rom module card description; drop or use `Handled` | 1.6 | S |
