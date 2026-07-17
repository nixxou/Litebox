# ROM Extractor (ArchiveMGS) — native port plan for LiteBox

Recon + staged plan for folding ExtendDB's **Archive MultiGame Selector / ROM extractor** into
the LiteBox host **natively** — no plugin, no reflection, no Harmony. When done, `Host/Media/RomBridge.cs`
(the reflection bridge into `ExtendDB.HostRomBridge`) is deleted and every consumer calls a native
`RomExtractor` facade.

> **Scope note.** This is a planning doc only. No production code here. Line estimates are for the
> engineer who executes the slices below. All paths absolute.

---

## 0. The one architectural insight that shrinks this port

The plugin does archive extraction from a **Harmony `Process.Start` prefix** because LaunchBox.exe —
not the plugin — spawns the emulator; the plugin can only intercept the spawn, read `psi.Arguments`,
extract, and rewrite the command line (`PathSubstitution`). That machinery exists *solely* because the
plugin does not own the launch.

**LiteBox owns the launch.** The emulator is spawned from `LbApiHost.Host.HostServices` after
`ResolveLaunchRomPath(...)` computes the ROM path. So the entire interception apparatus is **dropped**,
not ported:

| Plugin machinery (drop) | Why unneeded in LiteBox |
|---|---|
| `Patches/ProcessStartLogPatch.cs` (~1050 lines) | LiteBox calls the extractor directly in `ResolveLaunchRomPath`; no `Process.Start` prefix. |
| `Web/Backend/PathSubstitution.cs` (~500 lines) | No command-line rewrite — the host substitutes the ROM path *before* building the emulator args. |
| `Web/Backend/ArchiveLaunchContext.cs` (registry, 60 s freshness) | No cross-process arming; the picked entry is passed in-process from the Play button → `HostLaunch` as a parameter. |
| `Patches/AutoExtractSuppressor.cs` | LiteBox decides `AutoExtract` natively (`ResolveLaunchRomPath` already branches on `ep.AutoExtract`). |
| `GameLaunchHook.cs` arming half (`ArmArchiveContextIfAny`) | LiteBox's own `OnBeforeGameLaunching` / launch entry is the arm point, in-process. |
| `HostRomBridge.cs` (reflection surface) | Replaced by the native `RomExtractor` facade the host calls directly. |

That is **~2,100 lines dropped**, and it removes the single highest-risk, timing-sensitive part of the
plugin (a `Process.Start` prefix that must never throw for *any* child process). The net-to-port
number below is *after* removing this.

---

## 1. Engine dependency map

Source root: `c:\Users\mehdi\source\repos\scrapper-project\project\ExtendDB\ExtendDB\`
Target root: `c:\Users\mehdi\source\repos\scrapper-project\project\ExtendDB\LbApiHost\Host\` (suggest a new `Host/Rom/` folder).

### Keep / port (rewire plugin globals → LiteBox natives)

| File | ~Lines | Role | Plugin deps to rewire | Slice |
|---|---:|---|---|---|
| `Web/Backend/ArchiveMgsConfig.cs` | 413 | Config model: `ArchiveMode`/`OutputNameMode`/`CacheSubDirScheme` enums, `ArchivePriorityRow` profile, cascade `Resolve`, global bands/ext lists | `PluginPaths.Config` → `LiteBoxPaths`; `ExtendDBVersion.GateJsonConfig`/`Current` → LiteBox versioning; `ExtendDBPlugin.Log` → `LbLog` | R1 |
| `Web/Backend/ArchiveCache.cs` | 162 | 7z.dll/7z.exe path resolve, `ComputeSignature`, `ComputePathSignature` (`<SIG>`) | `ExtendDBPlugin.LBPath`/`PluginPath`; `ExtendDBConfigManager` cache-root; `SevenZipBase.SetLibraryPath` | R2 |
| `Web/Backend/ArchiveListingCache.cs` | 109 | `ComputeKey` (md5 of portable path\|size), `PortablePath` (LB-relative), facade over `ArchiveCacheDb` | `ExtendDBPlugin.LBPath` → LiteBox LB root | R2 |
| `Web/Backend/ArchiveAnalyzer.cs` | 430 | Open archive (SevenZipSharp), 3-way classify (rom/companion/metadata), `SortForDisplay`, `PickAutoLaunch`/`PickByWeights`/`ScoreEntry`/`PickByPriority` | `SevenZipExtractor` (7z.dll); `Wildcard`; `PlatformMapper` | R2 |
| `Web/Backend/ArchiveCacheDb.cs` | ~600 | SQLite backing: listing rows, cache manifest, per-entry RA hash/id, pick entries — keyed on 10-hex `<SIG>` | `ExtendDBPlugin` paths; `Microsoft.Data.Sqlite` (LiteBox already uses SQLite) | R2 (listing) / R3 (cache) |
| `Web/Backend/ArchiveCacheIndex.cs` | 58 | Facade over `ArchiveCacheDb` cache manifest (occupancy, evict list) | — | R3 |
| `Web/Backend/ArchiveCacheEvictor.cs` | 128 | `QualifiesForCache` band check, LRU `KeepCacheUnder`, `PurgeTmp` | — | R3 |
| `Web/Backend/ArchiveCachePlacement.cs` | 93 | Pure placement logic: `<cacheRoot>[\tmp]\<SIG>\<P\|F>[\<subdir>]\<file>` | `PlatformMapper.PlatformCode` | R3 |
| `Web/Backend/ArchiveExtractor.cs` | 235 | `ExtractOrReuse`: `7z x`/`7z e` selective (picked + companions), cache-hit fast path, size-guarded, title-rename | `ArchiveCache.SevenZipExePath`; `ExtendDBPlugin.Log` | R3 |
| `Database/ArchiveHistory.cs` | ~175 | Favourites + last-played per `<ShortSignature>` (`RecordPlayed`, `ToggleFavorite`, `GetFavorites`, `GetLastPlayed`) | `CacheDb` sqlite | R3 |
| `Web/Backend/CacheDb.cs` | ~300 | SQLite backing for `ArchiveHistory` | `ExtendDBPlugin` paths | R3 |
| `Web/Backend/RaArchivePick.cs` | 54 | Two-pass RA-preferring (hash, raid) resolution for a parsed archive | `ArchiveCacheDb`; `ArchiveMgsConfig`; `ArchiveAnalyzer.SortForDisplay` | R3 (RA-adjacent; may already be partly satisfied by RA module) |
| `Web/Backend/ArchiveConvert.cs` | 213 | Convert/Copy backends (chdman, DolphinTool bundled) | `ExtendDBPlugin.PluginPath/thirdparty` → LiteBox thirdparty | R4 |
| `Web/Backend/ArchiveRamDisk.cs` | 247 | ImDisk RAM-disk mount (P/Invoke `GlobalMemoryStatusEx`, `schtasks` elevated helper) | `ExtendDBPlugin.PluginPath`; bundled `RamDiskHelper.exe` | R4 |

### Reuse LiteBox native (do NOT re-port)

| Need | LiteBox native | Note |
|---|---|---|
| 7z.exe path | `HostServices.ResolvePath("ThirdParty/7-Zip/7z.exe")` | Same bundled 7-Zip. |
| Flat extract-everything fallback | `HostServices.TryExtractArchive` / `PickPrimaryFile` (lines 740/775) | **Replace** with the selective engine — see §5. Keep it as the "extractor absent/off" no-op path. |
| Launch-history row | see §3 — LiteBox has an op-log launch history (`OnBeforeGameLaunching`) | Prefer wiring `RecordArchiveEntry`/detection-ms into the existing op-log rather than porting `LaunchHistoryDb.cs` whole (thin adapter ~80 lines vs 230). |
| Paths under `Core\litebox\` | `LiteBoxPaths.Dir/File` | Cache root default → `LiteBoxPaths.Dir("romcache")`. |
| INI config | `LiteBoxConfig.GetSec/SetSec/Save` | `[Rom]` section. |
| Module gate | `LbModules.On(LbModule.Rom)` | Already defined (`Ready=false`, "coming soon"). Flip `Ready=true` in R1. |
| Logging | `LbLog.Info("rom", …)` | |
| SQLite | LiteBox already depends on `Microsoft.Data.Sqlite` (options db, gamecache) | Reuse for `ArchiveCacheDb`/`CacheDb`. |
| Options UI shell | `Options/ModulesOptions.cs` + `OptionsWindow.AddSection` | Add a `RomConfigPanel`. |

### Drop entirely — see §0 and §5

`ProcessStartLogPatch.cs`, `PathSubstitution.cs`, `ArchiveLaunchContext.cs`, `AutoExtractSuppressor.cs`,
`GameLaunchHook.cs` (arming half), `HostRomBridge.cs`, `LaunchOverrideRegistry.cs` (version-override
registry — LiteBox already resolves versions natively via `LaunchButtons` appId).

`M3uBuilder.cs` (234) is the **version-override multi-disc m3u builder** (keyed off `LaunchOverride`
+ `IAdditionalApplication.Disc`) — *not* the launch-time m3u rewrite (that lives inline in
`ProcessStartLogPatch`). LiteBox already has `TryBuildM3u` / `M3uDiscLoadEnabled` handling in
`ResolveLaunchRomPath`. **Do not port M3uBuilder**; the per-archive m3u *rewrite* (running each listed
file through the pipeline) is re-implemented small in R4.

**Net-to-port (after drops): ≈ 4,200 lines** — ≈ 2,750 engine (R2/R3) + ≈ 460 R4 (convert + ramdisk +
m3u rewrite) + ≈ 250 native `RomExtractor` facade + ≈ 400 config UI + ≈ 350 for the web routes (R5).
The ~2,100 lines of Harmony/`PathSubstitution`/registry machinery are **not** in this number.

### External dependency decision — archive *listing*

`ArchiveAnalyzer.Analyze` reads entries via **SevenZipSharp** (`SevenZipExtractor`, needs `7z.dll`).
LiteBox has no SevenZipSharp reference and is sensitive to assembly-load under self-contained publish
(see memory: *litebox-selfcontained-assembly-load*). **Two options — pick in R2:**

- **(A) Add SevenZipSharp NuGet** — highest fidelity, least code, but a managed+native interop dep to
  validate under `dotnet publish -r win-x64 --self-contained`.
- **(B) Parse `7z.exe l -slt`** — no new dependency, reuses the already-bundled 7z.exe and LiteBox's
  process-run helpers; ~120 lines of parser, must reproduce `Crc`/`Size`/`FileName`/`IsDirectory`
  exactly (the signature depends on `Crc`+`Size`). **Recommended** given LiteBox's publish constraints.

---

## 2. Config model → `[Rom]` ini + per-profile store + Options tab

### 2a. Two-tier model (mirror the plugin's split)

The plugin splits config across registry (bare on/off + cache root) and a JSON sidecar (everything
else). LiteBox mirror:

| Plugin location | Field(s) | LiteBox target |
|---|---|---|
| `ExtendDBConfig` (registry) | `ArchiveMgsEnabled` | **`LbModules.On(LbModule.Rom)`** (already the master switch — no separate flag) |
| `ExtendDBConfig` (registry) | `ArchiveMgsPath` (cache root) | `LiteBox.ini` `[Rom] CachePath` (default `LiteBoxPaths.Dir("romcache")`) |
| `ArchiveMgs.json` globals | `CacheMaxGb/CacheMinMb/CacheMaxMb`, `MetadataExtensions`, `ArchiveExtensions`, `DiscImageExtensions`, `AdvancedMode` | `LiteBox.ini` `[Rom]` keys (flat scalars) |
| `ArchiveMgs.json` `GlobalDefault` + `Priorities[]` | full per-(platform, emulator) profiles | **JSON under `Core\litebox\`** (see 2b) |

### 2b. Where the profiles live — **JSON, not ini**

A profile (`ArchivePriorityRow`) has nested collections (`List<TagWeight>`, `List<ConvertRule>`) and a
cascade — ini is a poor fit. **Store profiles as one JSON file** `LiteBoxPaths.File("rom-profiles.json")`
holding `{ GlobalDefault, Priorities[] }`, deserialized with the same shapes as `ArchiveMgsConfig`.
This is the least-friction port: `ArchiveMgsConfig` becomes a LiteBox config type whose *scalar globals*
are read/written to `[Rom]` and whose *profiles* persist to `rom-profiles.json`. Keep `Resolve(platform,
emulator)` verbatim (exact → (platform,"All") → GlobalDefault). Keep `EnsureDefaults` (seeds
`DefaultRomExtensions`/`DefaultIgnoredExtensions`/`DefaultTagWeights`).

Suggested `[Rom]` keys:

```
[Rom]
CachePath   = <LB>\Core\litebox\romcache   ; default; folder browse in the tab
CacheMaxGb  = 50
CacheMinMb  = 100
CacheMaxMb  = 8000
MetadataExtensions = nfo, txt, dat, xml, json, htc, hts
ArchiveExtensions  = zip, 7z, rar, tar, gz, bz2, xz
DiscImageExtensions= chd, rvz, wia, gcz, iso, cue, gdi, cso, zso
```

(`AdvancedMode` is force-true in the current plugin — the Simple/Advanced toggle was removed — so it
need not be a user key; the cascade always applies.)

### 2c. Options → Modules "ROM extractor" tab

Add to `Options/ModulesOptions.cs`: in `ModuleConfigPanel(...)` add
`if (m.Module == LbModule.Rom) return RomConfigPanel(dpiS, readOnly);` and write `RomConfigPanel`
following the `BaseConfigPanel` pattern (line 191): scrolling `Panel`, `S(px)` DPI helper,
`LiteBoxTheme` colors, prefill from config, return an `Apply` closure writing `SetSec` + profiles JSON.
The plugin's full editor is `Forms/ArchiveMgsConfigPanel.cs` (1250 lines, WinForms) — port a
**pragmatic subset**, staged:

| Control group (plugin) | Bound field | R1 (MVP) | Later |
|---|---|---|---|
| Cache path + Browse | `[Rom] CachePath` | ✔ | |
| Max GB / Min MB / Max MB | cache band | ✔ | |
| Global ext lists (metadata/archive/disc) | globals | ✔ | |
| Reset to default / Clear archive data / Manage cache | actions | Reset ✔ | Manage-cache window R3 |
| Live usage label, ImDisk capability card | computed / runtime | — | R4 (ramdisk) |
| **Left cascade** (platform/emulator pickers, tri-state "use default") | profile selection | — | R2/R3 advanced editor |
| **Right fiche** (Mode combo, sub-dir scheme, per-mode strip, File-rules grid, Tag-priority grid, RA bonus, Convert grid, Texture, M3u) | `ArchivePriorityRow` | — | R2 (rules/tags) → R4 (convert/texture) |

MVP (R1) = the header/global band only, editing just `GlobalDefault`. The full per-profile cascade
editor is the big UI lift and lands alongside the engine that consumes it (R2/R3). "Advanced mode" =
the platform/emulator cascade being editable (there is no separate simple toggle).

---

## 3. Launch-integration seam (highest risk — be concrete)

### Where it hooks

`c:\...\LbApiHost\Host\HostServices.cs` → **`ResolveLaunchRomPath(IGame game, IEmulator emulator,
IEmulatorPlatform ep, string romAbs, string label)`** (line 684). Today, lines 696–715:

```
bool autoExtract = (ep?.AutoExtract) ?? SafeBool(() => emulator.AutoExtract);
if (autoExtract && IsArchive(romAbs)) {
    if (Media.RomBridge.Available) {           // ← DEFERS to the plugin today
        return romAbs;                          //   (leaves the archive on the cmdline)
    }
    var extracted = TryExtractArchive(romAbs, label);   // flat extract-everything fallback
    if (!string.IsNullOrEmpty(extracted)) return extracted;
}
return romAbs;
```

**The port replaces the `RomBridge.Available` branch with a native call** that returns the extracted /
picked / converted loose-ROM path, so the emulator is spawned against it. Sketch:

```
if (autoExtract && (IsArchive(romAbs) || RomExtractor.IsDiscImage(romAbs) || IsM3u(romAbs))) {
    if (LbModules.On(LbModule.Rom)) {
        var picked = RomLaunchPick.Consume(game, /*appId*/…);   // the entry armed by the Play button (in-proc)
        var res = RomExtractor.ResolveLaunch(game, emulator, ep, romAbs, picked, label);
        if (res.Success) { AutoExtractHandledNatively = true; return res.OutputFilePath; }
    }
    // module off / failed → LiteBox's existing flat fallback
    var extracted = TryExtractArchive(romAbs, label);
    if (!string.IsNullOrEmpty(extracted)) return extracted;
}
```

### Resolving the *armed* entry — in-process, no registry

The plugin arms a 60 s `ArchiveLaunchContextRegistry` because Play-click (web/UI) and the emulator spawn
(LaunchBox) are decoupled across process boundaries. In LiteBox they are the **same call stack**:
`LaunchButtons.OnPlay` (line 613, currently `RomBridge.ArmSelectedRom(_game, _selVerAppId, _selRom,
_forcePriority)`) → `HostLaunch.Launch(...)` → `ResolveLaunchRomPath`. So pass the selection **as a
parameter / small launch-scoped struct**, not a timed registry:

- Add `SelectedRomEntry` + `ForcePriority` fields onto the launch request the Play button already builds,
  OR a thread-static `RomLaunchPick` set immediately before `HostLaunch.Launch` and consumed once inside
  `ResolveLaunchRomPath` (mirrors the plugin's single-shot semantics without the 60 s expiry).
- `_selRom` may be null with `_forcePriority=true` (the "Clear → pure priority" path) — preserve that.

### The `ResolveLaunch` body (ported from `ApplyArchiveExtractionIfAny`, minus Harmony/PathSubstitution)

1. Resolve profile `row = RomConfig.Resolve(platformName, emulatorTitle)`. `Mode==DoNothing` → return
   `romAbs` unchanged (emulator reads the archive natively).
2. Branch by extension:
   - **m3u** (+ `row.M3uInput`): read lines, run each through the pipeline, write a rewritten
     `<cacheRoot>\tmp\<SIG>.m3u`, return it. (R4)
   - **archive**: fast listing-cache hit (`ArchiveCacheDb` — relaunch without opening the archive) →
     else `ArchiveAnalyzer.Analyze` + pick.
   - **disc image**: Convert / Copy backend. (R4)
3. **Pick**: explicit `SelectedRomEntry` (match `PathInArchive` then basename) → else
   `ArchiveAnalyzer.PickAutoLaunch(standalone, EffectiveWeights(row), row.Priority, lastPlayed, raBonus,
   raPaths)`; `ForcePriority` ignores last-played.
4. **Placement**: `outOfBand = !ArchiveCacheEvictor.QualifiesForCache(analysis.UnpackedSize, min, max)`;
   optional RAM disk (R4). `ArchiveCachePlacement.Compute(...)` → `<cacheRoot>[\tmp]\<SIG>\<P|F>\…`.
5. **Extract**: `ArchiveExtractor.ExtractOrReuse(analysis, target, row, cacheRoot, sig, oob, title,
   platform, emulator)` — already `Task.Run`+`WaitForExit` internally (the LiteBox launch thread has
   already shown its launching UI, same as LB's splash — a few seconds' block is acceptable, matching
   the existing `TryExtractArchive` synchronous behaviour).
6. **Side effects**: `ArchiveHistory.RecordPlayed(shortSig, entry)`; record the launched entry into the
   op-log launch history; `ArchiveCacheIndex.Record(...)` for persistent placements;
   `ArchiveCacheEvictor.KeepCacheUnder(cacheRoot, maxGb)`; (RA heal of the launched entry is the RA
   module's job — see §7).
7. Return `OutputFilePath` — the host builds the emulator command line with it. **No `PathSubstitution`**:
   the substitution is simply *returning a different path*.

### Cleanup after exit

The plugin purges `\tmp` and unmounts ram disks in `OnGameExited`. LiteBox already has an
`OnGameExited`-equivalent (detection latency is recorded there today, `HostServices.cs:293/458`). Hook:
`ArchiveCacheEvictor.PurgeTmp(cacheRoot)` + `ArchiveRamDisk.UnmountForGame(gameId)` there. Persistent
`<SIG>` cache entries survive (LRU-evicted on next extraction).

---

## 4. Consumer surfaces — native `RomExtractor` facade (replaces `RomBridge` method-by-method)

`Host/Media/RomBridge.cs` (reflection) → new `Host/Rom/RomExtractor.cs` (native static facade). Every
current call site (from the survey) maps 1:1:

| `RomBridge` member | Caller(s) | Native replacement |
|---|---|---|
| `Available` (get) | `LaunchButtons.cs:187`, `HostServices.cs:708`, `MainWindow.cs:1517` | `LbModules.On(LbModule.Rom)` (drop the reflection probe) |
| `GetLaunchInfoJson(game)` | `LaunchButtons.cs:188` | `RomExtractor.GetLaunchInfoJson(game)` — build from native emulator/version resolution + op-log last-launch. **JSON field names must stay byte-identical** to what `LaunchButtons` parses. |
| `GetArchiveEntriesJson(game, appId)` | `LaunchButtons.cs:551` (dropdown) | `RomExtractor.GetArchiveEntriesJson(game, appId)` — listing-cache → `SortForDisplay` → decorate favourites/last-played/RA. |
| `PickRomModal(game, appId)` | `LaunchButtons.cs:541` (advanced picker) | Native WinForms picker `Host/Rom/RomPickerWindow.cs` (ports `Forms/ArchiveListWindow` selection mode). |
| `ArmSelectedRom(game, appId, entry, forcePriority)` | `LaunchButtons.cs:613` (OnPlay) | Set the in-process `RomLaunchPick` (see §3) — no registry. |
| `ClearLaunchHistory(game)` | `LaunchButtons.cs:379` (reset) | Clear the op-log launch-history row for the game. |
| `RecordDetection(game, ms)` | `HostServices.cs:293/458` | Write detection-ms into the op-log launch history. |
| `RaActive` (get) | `MainWindow.cs:1517/3388`, `Ra/RaStartupRefresh.cs:34` | `LbModules.On(LbModule.RetroAchievements)` (native). |
| `HealRa` / `HealRaSync(game)` | `MainWindow.cs:3388` | RA module's native heal (LiteBox already has `Ra/RaResolveLite`); the extractor exposes the per-archive `(hash, raid)` via `RaArchivePick`. |

### One shared listing/scoring implementation

Per memory (*rom-list-surfaces-sync*), four surfaces reimplement the in-archive ROM list (LiteBox picker,
lb-web, bb-web, desktop). **All must call one native method** — e.g.
`RomExtractor.ListEntries(game, appId) → IReadOnlyList<RomEntryView>` — that runs `ArchiveAnalyzer`/
listing-cache + `SortForDisplay` (score = tag weights + RA bonus; cumulative ↻★🏆 markers; last-played
kept, not excluded). The picker UI, `GetArchiveEntriesJson` (dropdown), and the web routes (R5) all
render from that single list so scoring never drifts.

---

## 5. Seams to cut (plugin/Program/Harmony/reflection → LiteBox)

| Cut | Replacement |
|---|---|
| `ProcessStartLogPatch` Harmony prefix | Direct call in `ResolveLaunchRomPath` (§3). |
| `PathSubstitution.Detect` cmdline rewrite | Return the resolved path; host builds args. |
| `ArchiveLaunchContextRegistry` (60 s) | In-process `RomLaunchPick` param/thread-static (§3). |
| `AutoExtractSuppressor` (patched `get_AutoExtract`) | Host reads `ep.AutoExtract` and simply doesn't native-extract when the module handles it. |
| `GameLaunchHook.ArmArchiveContextIfAny` | Play-button already arms in-process. |
| `HostRomBridge` + `Host/Media/RomBridge.cs` reflection | Native `RomExtractor` facade (§4). |
| `ExtendDBPlugin.Log` | `LbLog.Info("rom", …)` |
| `ExtendDBPlugin.LBPath` / `PluginPath` | `HostServices.ResolvePath` / LB root; `LiteBoxPaths` |
| `PluginPaths.Config(...)` / JSON gating via `ExtendDBVersion` | `LiteBoxPaths.File(...)` + LiteBox versioning (memory: *extenddb-versioning*) |
| `ExtendDBConfigManager` (registry) | `LiteBoxConfig` `[Rom]` + `rom-profiles.json` |
| `ArchiveHistory`/`LaunchHistoryDb` (`ExtendDB.Database`) | Port `ArchiveHistory`+`CacheDb`; splice launch history into LiteBox op-log |
| `PluginHelper.DataManager.GetAllPlatforms/GetAllEmulators` (config UI pickers) | LiteBox's native platform/emulator enumeration |
| `SevenZipExtractor` (7z.dll) | SevenZipSharp NuGet **or** `7z.exe l -slt` parser (decide R2 — recommend the parser) |

---

## 6. Staged slices (smallest testable first)

| Slice | Delivers | ~Lines | Verify |
|---|---:|---|---|
| **R1 — Config model + `[Rom]` tab** | Port `ArchiveMgsConfig` as a LiteBox config type (globals→`[Rom]`, profiles→`rom-profiles.json`); flip `LbModule.Rom.Ready=true`; MVP `RomConfigPanel` (cache path, band, global ext lists, Reset). No launch behaviour yet. | ~700 | Open Options→Modules→ROM extractor; edit + save; reload persists to `LiteBox.ini`/`rom-profiles.json`; toggling the module writes the options-db key. |
| **R2 — Native listing/analyzer + read surfaces** | Port `ArchiveCache`, `ArchiveListingCache`, `ArchiveCacheDb` (listing rows only), `ArchiveAnalyzer` (+ `Wildcard`, `PlatformMapper` subset), archive-listing via chosen 7z path; native `RomExtractor.GetArchiveEntriesJson` + `PickRomModal`. Repoint `LaunchButtons.cs:541/551`. | ~1,100 | Select a game whose ROM is a multi-game `.zip`; the quick dropdown and advanced picker list entries in the same scored order as the plugin; listing cache hit on second open (no archive re-read). |
| **R3 — Extractor + cache + launch substitution** | Port `ArchiveExtractor`, `ArchiveCachePlacement`, `ArchiveCacheEvictor`, `ArchiveCacheIndex`, cache manifest half of `ArchiveCacheDb`, `ArchiveHistory`+`CacheDb`; wire `RomExtractor.ResolveLaunch` into `ResolveLaunchRomPath`; in-process `RomLaunchPick`; `PurgeTmp` on exit. **The crux.** | ~1,300 | Launch a game from a multi-game archive with an explicit pick → the *picked* ROM boots (not the first alphabetical); cache-hit relaunch is instant; `\tmp` purged after exit; cache stays under the GB band. |
| **R4 — RAM disk / texture / m3u / convert** | Port `ArchiveConvert` (chdman/DolphinTool), `ArchiveRamDisk` (ImDisk + elevated helper), per-archive m3u rewrite, texture-pack extract; extend `RomConfigPanel` (convert grid, texture, ramdisk capability card). | ~900 | Convert-after-extract cue/bin→chd launches; ramdisk mount when driver present + degrades gracefully when not; m3u multi-disc launch rewrites each entry. |
| **R5 — Web archive routes (web-plan S6)** | Native `ArchiveListingApi`/`ArchiveMetadataApi` + `archive-entries`/`archive-favorite`/`archive-metadata` routes for lb-web + bb-web, sharing the §4 single listing impl; enable the routes gated on the module. | ~800 | In BigBox/LaunchBox Web, Select-ROM sub-menu lists entries, pin/favourite persists, launching a chosen entry boots it. |
| **R6 — Delete the reflection bridge** | Remove `Host/Media/RomBridge.cs`, drop the reflection probes at all call sites, delete plugin `HostRomBridge`/`ProcessStartLogPatch`/`PathSubstitution`/`ArchiveLaunchContext`/`AutoExtractSuppressor` from the LiteBox launch path. | ~-2,100 | Full launch + picker + web flows work with ExtendDB plugin absent; no `RomBridge.` references remain (`grep`); build clean net9+net10. |

R1 is independently shippable (config only). R2 adds read-only value (pickers) with no launch risk. R3
is the risk gate. R4/R5 are additive. R6 is cleanup once R2–R5 cover every consumer.

---

## 7. Risks / open questions

1. **Launch-substitution timing & thread (R3) — highest.** The plugin extracts inside a `Process.Start`
   Harmony prefix; LiteBox extracts *synchronously* inside `ResolveLaunchRomPath` on the launch thread.
   `ArchiveExtractor.ExtractOrReuse` already wraps 7z in `Task.Run`+`WaitForExit`, and LiteBox's existing
   `TryExtractArchive` is already synchronous there — so the pattern holds — but a large disc image can
   block for seconds. Confirm the launching UI is shown *before* `ResolveLaunchRomPath` (it is, for the
   flat fallback) and that no WPF dispatcher deadlock arises. Open: should extraction show progress /
   be cancellable in the host UI (the plugin has none)?

2. **Armed-entry plumbing (R3).** Replacing the 60 s cross-process registry with an in-process
   `RomLaunchPick` must be single-shot and cover the three producers: quick-dropdown pick, advanced
   `PickRomModal`, and "Clear→forcePriority" (`entry=null, forcePriority=true`). If any launch path
   reaches `ResolveLaunchRomPath` *without* passing through the Play button (e.g. web Play, autoplay,
   command-line launch), the pick must still resolve (fall back to auto-pick). Audit every `HostLaunch`
   entry.

3. **Archive-listing dependency (R2).** SevenZipSharp (7z.dll) vs `7z.exe l -slt` parser under LiteBox's
   self-contained publish (memory warns of framework-assembly load conflicts). The parser (B) avoids the
   native interop risk but must reproduce `Crc`+`Size`+`FileName`+`IsDirectory` *exactly* — the cache
   `<SIG>` (`ComputeSignature`) hashes CRC+Size, so any deviation splits the cache and desyncs from the
   RA module's stored hashes. Recommend (B); validate signatures byte-match the plugin on a sample set.

4. **Cache concurrency + SQLite + portable keys.** `ArchiveCacheDb`/`CacheDb` are now hit by three
   surfaces (launch, picker, web routes) possibly concurrently. LiteBox must use one connection strategy
   (per-call connections like the plugin, or a shared serialized one). Critically, the RA module already
   stores `rom_hash` in a **split** DB keyed on the same `<SIG>` (memory: *retroarch-ra-module*,
   *portable-path-keys*) — the ported `ArchiveListingCache.PortablePath` + `ComputePathSignature` must
   produce **identical** `<SIG>` to the plugin/RA side or achievements silently detach from ROMs.

5. **RAM-disk P/Invoke portability (R4).** ImDisk needs admin; the plugin's non-elevated mount degrades
   to disk. The elevated `schtasks` + `RamDiskHelper.exe` trick and bundled binaries must be re-homed
   under LiteBox `thirdparty\`. Confirm ImDisk detection + graceful fallback under LiteBox's desktop-heap
   launch context (memory: *litebox-desktop-heap-launch*) — mounting a drive from a heap-starved session
   could fail differently than under LaunchBox.

6. **Config UI scope.** The plugin editor is 1,250 lines of WinForms (cascade + fiche + tri-state combos).
   R1 ships a global-only MVP; the full per-profile cascade editor is a large lift deferred to R2/R3.
   Open: is the advanced per-(platform,emulator) editor required for first user validation, or is
   `GlobalDefault`-only acceptable initially?

7. **Launch-history source of truth.** Port `LaunchHistoryDb` whole, or splice `RecordArchiveEntry` /
   `UpdateDetectionMs` / last-launch read into LiteBox's existing op-log launch history? The latter is
   less code but must expose the same `GetLaunchInfoJson` shape `LaunchButtons` parses. Decide in R3.
