# ExtendDB embedded web server → LiteBox host — port plan

> Recon + staged execution plan for porting ExtendDB's `ExtendDB/Web/` subsystem
> (~22.2k lines) into the LiteBox host (`LbApiHost`) natively — no plugin, no
> reflection, no Harmony. Read this whole doc before writing any code.
>
> Source (private, READ-ONLY): `ExtendDB/ExtendDB/Web/`
> Target: `LbApiHost/Host/Web/` (new folder), assets under `Core\litebox\web\`.

## 0. Executive summary

| Item | Value |
|---|---|
| Gross source size | **22,161 lines** across `ExtendDB/Web/**` |
| Realistic **net to-port** (after reuse/drop) | **~13–15k lines** (see §2 tally) |
| Recommended first slice | **S1 — server skeleton + static serving of the three folders + `/vendor` + `/robots.txt`** (visibly serves the BigBox/LiteBox SPA shell on the theme's own dummy data; no DB, no LB library) |
| Heaviest single risk | The **Archive/Select-ROM subsystem (~3,450 lines)** has no native LiteBox backend yet (RomBridge currently reflects into the plugin) |

The single biggest structural advantage: **LiteBox already re-implements the
LaunchBox plugin SDK** (`Unbroken.LaunchBox.Plugins.PluginHelper.DataManager`
returns LiteBox's `HostDataManagerXml : DummyDataManager`, and `IGame` /
`IPlatform` / `IPlaylist` are real types in-process). Every Web backend file that
reads the library through `PluginHelper.DataManager` + `IGame` **compiles and runs
essentially as-is** — the port is mostly *cutting plugin-only seams* (§5), not
rewriting data access.

The Web subsystem is already **Harmony-free** (grep: zero `Harmony`/`HarmonyPatch`
references under `Web/`), so there is no patch-removal work — only three plugin
statics to redirect: `ExtendDBPlugin.Log/PluginPath`, `ExtendDBConfigManager`, and
`SqliteConnectionPatches.BypassRedirect`.

---

## 1. Route inventory (from `EmbeddedWebServer.RegisterRoutes`)

Order = registration order (first-match-wins dispatch). `Dep` column: **N** = handler's
data path is already native in LiteBox (reuse); **P** = backend still to port; **D** =
plugin/kiosk-only, drop.

### Shared / always-on

| Route (regex) | Handler | Purpose | Dep |
|---|---|---|---|
| `/robots.txt` | `RobotsHandler.Handle` | static robots (disallow all) | P (trivial) |
| `/api/parental/state` | `ParentalApi.HandleState` | per-client lock snapshot | N (`ParentalBridge`) + P (cookie adapter) |
| `/api/parental/unlock` | `ParentalApi.HandleUnlock` | PIN unlock → sets cookie | N + P |
| `/api/parental/lock` | `ParentalApi.HandleLock` | re-lock | N + P |
| `/thumbs/{id}.jpg` | `ThumbHandler.Handle` | degraded cover by DB id | N (`Host/Media/ThumbCache`) |
| `/api/media/{id}.{ext}` | `MediaApi.HandleThumbById` | cover-by-id, extenddb.com→DB fallback | N (`MediaFetch`) + P (handler shell) |
| `/api/media/{token}.{sig}.{ext}` | `MediaApi.Handle` | **signed-token media proxy** (disk-first, Range, upstream fallback) | N (fetch/policy) + P (token+Range handler) |
| `/vendor/{path}` | `VendorStaticHandler.Handle` | shared JS/CSS libs for both themes | P (trivial static) |
| `/api/recent/epoch` | `BigBoxThemeApi.RecentEpoch` | cache-buster epoch for "recent" rows | P (small) |
| `/api/badges/{name}.png` | `BadgeApi.Handle` | LB badge PNG from media packs | P (small; reads `LBPath\Images`) |

### Database site (`webDb`, mounted at `/`) → `web\database\`

| Route | Handler | Purpose | Dep |
|---|---|---|---|
| `/`, `/index.html` | `HomeHandler.Handle` | platform grid, grouped by category | P (`DbRepository`) |
| `/platforms.html` | `PlatformsListHandler.Handle` | all-platforms list | P |
| `/platforms/{slug}.html` | `PlatformDetailHandler.Handle` | platform page shell | P |
| `/games/{id}.html` | `GameDetailHandler.Handle` | game detail page shell | P |
| `/api/platforms` | `PlatformsApi.Handle` | platforms JSON | P |
| `/api/platforms/{slug}` | `PlatformDetailApi.Handle` | one platform JSON | P |
| `/api/platforms/{slug}/games` | `PlatformGamesApi.Handle` | paginated games (sort/filter) | P |
| `/api/platforms/{slug}/{genres\|developers\|publishers\|release-types\|origins}` | `PlatformFiltersApi.*` | filter facet lists | P |
| `/api/games/{id}` | `GameDetailApi.Handle` | game JSON (uses `GameCache` for media) | P + N (media) |
| `/api/search` | `SearchApi.Handle` | search index query | P |

**Note:** the database site is **100 % server-rendered HTML** — `HtmlShared.Head`
emits inline CSS + inline JS; the only external asset references are `/thumbs/`,
`/api/search`, and Google Fonts (CDN). There are **no static files** for this site
today.

### LiteBox Web theme (was "LaunchBox Web", `lbWeb`, `/launchbox/`) → `web\litebox\`

| Route | Handler | Purpose | Dep |
|---|---|---|---|
| `/launchbox` | `LaunchBoxPageHandler.RedirectToRoot` | 301 → `/launchbox/` | P (trivial) |
| `/launchbox/` | `LaunchBoxPageHandler.Index` | serve `launchbox/index.html` | P (static) |
| `/launchbox/data/cattree.json` | `LaunchBoxDataApi.CatTree` | category tree | N (`OwnedDataProvider` via SDK) |
| `/launchbox/data/platforms/{slug}/games.json` | `.PlatformGames` | platform games | N |
| `/launchbox/data/playlists/{slug}/games.json` | `.PlaylistGames` | playlist games | N |
| `/launchbox/data/games/{id}/detail.json` | `.GameDetail` | per-game detail | N |
| `/launchbox/data/games/{id}/installstate.json` | `.InstallState` | store install state | N (`StoreInstallState`) |
| `/launchbox/data/{kind}/{slug}/recent.json` | `.Recent` | recent row | N |
| `/launchbox/data/{kind}/{slug}/catmedia.json` | `.CatMedia` | category bg media | N |
| `/launchbox/data/platforms/{slug}/stars.json` | `.Stars` | quality tiers | N |
| `/api/launchbox/icons/{name}.{ext}` | `LaunchBoxIconsApi.Handle` | platform clear-logo icons | N (media) |
| `/api/launchbox/platforms/stats` | `LaunchBoxStatsApi.Handle` | per-platform counts | N |
| `/launchbox/api/games/{id}/archive-entries` | `LaunchBoxMutationApi.ArchiveEntries` | Select-ROM entry list | **P (archive)** |
| `/launchbox/api/games/{id}/archive-favorite` | `ArchiveListingApi.HandleFavorite` | pin an archive entry | **P (archive)** |
| `/launchbox/api/games/{id}/{kind}` | `LaunchBoxMutationApi.Handle` | play/favorite/hide/… mutations | N + **P (play seam)** |
| `/launchbox/api/keybinds` | `WebKeyBindsApi.HandleLaunchBox` | rebindable nav keys | P (config) |
| `/launchbox/{path}` | `LaunchBoxPageHandler.Static` | static catch-all | P (static) |

### BigBox Web theme (`bbWeb`, `/bigbox/`) → `web\bigbox\`

| Route | Handler | Purpose | Dep |
|---|---|---|---|
| `/bigbox` | `BigBoxThemeApi.RedirectToRoot` | 301 → `/bigbox/` | P |
| `/bigbox/data/cattree.json` | `.CatTree` | category tree | N |
| `/bigbox/data/detailmenu.json` | `.DetailMenu` | static action menu | N |
| `/bigbox/data/system.json` | `.SystemMenu` | system menu | N |
| `/bigbox/data/platforms/{slug}/games.json` | `.PlatformGames` | platform games | N |
| `/bigbox/data/playlists/{slug}/games.json` | `.PlaylistGames` | playlist games | N |
| `/bigbox/data/platforms/{slug}/stars.json` | `.PlatformStars` | quality tiers | N |
| `/bigbox/data/{platforms\|playlists\|categories}/{slug}/recent.json` | `.*Recent` | recent rows | N |
| `/bigbox/data/{platforms\|playlists\|categories}/{slug}/catmedia.json` | `.*CatMedia` | bg media | N |
| `/bigbox/data/games/{id}/installstate.json` | `.InstallState` | store install state | N |
| `/bigbox/data/games/{id}/detail.json` | `.GameDetail` | per-game detail | N |
| `/bigbox/data/games/{id}/related.json` | `.Related` | related/similar/ports | N |
| `/bigbox/data/games/related/overviews.json` | `.RelatedOverviews` | batch descriptions | N |
| `/bigbox/api/games/{id}/archive-favorite` | `ArchiveListingApi.HandleFavorite` | pin archive entry | **P (archive)** |
| `/bigbox/api/games/{id}/archive-entries` | `ArchiveListingApi.Handle` | Select-ROM entry list | **P (archive)** |
| `/bigbox/api/games/{id}/archive-metadata` | `ArchiveMetadataApi.Handle` | per-entry overlay HTML | **P (archive)** |
| `/bigbox/api/games/{id}/{kind}` | `BigBoxMutationApi.Handle` | play/favorite/hide mutations | N + **P (play seam)** |
| `/bigbox/api/keybinds` | `WebKeyBindsApi.Handle` | rebindable nav keys | P (config) |
| `/bigbox/{path}` | `BigBoxThemeApi.Static` | static catch-all | P (static) |

Per-surface enable flags (`EnableWebDb`/`EnableLaunchBoxWeb`/`EnableBigBoxWeb`)
re-read on every `Start`, so the route table is rebuilt to add/remove a surface's
routes live. Keep that behaviour.

---

## 2. Dependency map + to-port tally

Grouped by subsystem. **Reuse** = already native in LiteBox (call it, don't port).
**Port** = bring the file over, cut seams. **Drop** = plugin/kiosk-only.

| Group | Key files (lines) | Status | Notes |
|---|---|---|---|
| **Core plumbing** (~1,015) | `EmbeddedWebServer` 467, `Router` 114, `HttpRequest` 248, `HttpResponse` 186 | **Port near-verbatim** | Only seams: `ExtendDBPlugin.Log`→`LbLog.Info("web",…)`, `ExtendDBConfigManager`→`LiteBoxConfig [Web]`, `Modules.Active(Module.Web)`→`LbModules.On(LbModule.Web)`. |
| **Database site** (~4,045) | `DbRepository` 1243, `HtmlShared` 827, `GameDetailHandler` 564, `PlatformDetailHandler` 401, `Home/PlatformsList` 274, `Pages Api/*` ~490, `OwnedLookup` 158, `ExtendDbLinks` 75 | **Port** | Reads Extended DB directly. Cut `SqliteConnectionPatches.BypassRedirect` (no read-intercept patch in LiteBox → open DB directly). DB path ← `MetadataDb.ExtendedDbPath`. |
| **Theme shared** (~5,618) | `OwnedDataProvider` 1221, `BigBoxThemeApi` 268, `BigBoxMutationApi` 424, `LaunchBox/*` ~640, `GameLauncher` 790, `StoreInstallState` 668 (**reuse-heavy**), `PlatformMapper` 365, `RecentState` 95, `CoverPicker` 86, `ThemeFormat`/`WebKeyBindsApi` ~175, static handlers ~250 | **Port (data via SDK, cut 1 seam)** | Data access = `PluginHelper.DataManager` + `IGame` → **works as-is on LiteBox**. `StoreInstallState` is a straight port of LiteBox's own `StoreInstallStateSync` — may consolidate. **One true seam: `GameLauncher.Play` → §5.** |
| **Archive / Select-ROM** (~3,450) | `ArchiveCacheDb` 658, `ArchiveAnalyzer` 438, `ArchiveMgsConfig` 413, `ArchiveExtractor`/`RamDisk`/`Convert`/`Cache*`/`LaunchContext` ~1,150, `M3uBuilder` 234, `CacheDb` 210, `LaunchOverrideRegistry` 136, `Theme/ArchiveListingApi`+`ArchiveMetadataApi` 487 | **Port — heaviest, deferrable** | **No native LiteBox backend today** (RomBridge reflects into the plugin). Overlaps the in-flight *romextractor redesign*. Recommend **gate these routes off in v1** and land after (or alongside) native ArchiveMGS. |
| **RA web backend** (~2,826) | `RetroAchievementsDb` 691, `RaScanner` 440, `RaOnLaunch` 371, `RaCatalog` 320, `RaSystems`/`RaGameSync`/`RaGameWriter`/`RaOnSelect`/`RaCanaries`/`RaRefreshScheduler`/`RaArchivePick`/`RaCatalogGenerator` ~800, `BadgeApi` 88 | **Mostly reuse** | LiteBox already ported RA natively (`Host/Ra/*`: `RaService`, `RaScanLite`, `RaResolveLite`, `RaCatalogLite`, `RaXmlWriter`, `RaPlatformMap`). Web needs only **glue** to surface RA state in JSON — most of these files are **not** re-ported; wire the themes' RA fields to `Host/Ra`. |
| **Media / policy / parental** (~5,138 gross, mostly reuse) | `MediaApi` 1494, `MediaResolver` 1001, `MediaSourcePolicy` 411, `ImageHelper` 189, `ScreenscraperApiBreaker` 270, `MediaTokenSecret` 170, `PolicyStore` 345, `WebParentalState` 350, `Thumb/ThumbHandler` 37, `GalaxyDb` 171, `Models` 223 | **Reuse fetch, port handlers** | Per-origin fetch/policy = already `Host/Media/MediaFetch` (via `MediaApiBridge`) → **drop** `MediaResolver`/`MediaSourcePolicy`/`ImageHelper`/`ScreenscraperApiBreaker`/`PolicyStore` (~2,216). **Port** the thin HTTP shells: `MediaApi.Handle` token-decode + disk-first + **HTTP Range** (~500–700), `MediaTokenSecret` (or reuse existing secret store), `ParentalApi` 192, `WebParentalState`→adapter over `ParentalBridge`, `ThumbHandler` 37, `Models` 223. |

### Headline estimate

- **Gross source:** 22,161 lines.
- **Drop (reuse native equivalent):** media-fetch/policy (~2,216) + RA backend re-port avoided (~2,400 of 2,826, keep ~400 glue) + thumb cache (~169) ≈ **~4,800 lines not ported**.
- **Realistic net to-port:** **~13,000–15,000 lines**, of which **~3,450 (Archive)** is deferrable → **v1 net ≈ 9,500–11,500 lines**.

---

## 3. Static-asset story

### Where assets live today

All three sites share **one on-disk tree**: `<PluginPath>\BigBoxWeb\web\` (copied
next to `ExtendDB.dll` by a csproj `<None Include="BigBoxWeb\web\**">` rule — **not**
embedded resources). Sub-layout:

```
BigBoxWeb/web/                ← BigBox theme root (index.html, engine/*.js, styles) → ThemeStaticFiles
BigBoxWeb/web/launchbox/      ← LaunchBox theme root                                → LaunchBoxPageHandler
BigBoxWeb/web/vendor/         ← shared JS libs (referenced as /vendor/…)            → VendorStaticHandler
BigBoxWeb/web/data|images|videos|renders|screens|sounds|test-elements  ← DUMMY demo data (standalone file:// mode)
```

Total 39 MB — but the bulk (`images/`, `videos/`, `renders/` 129 files, `screens/`,
`test-elements/`, `data/`) is **demo/dummy content** used only when the SPA is opened
from `file://`. In server mode those logical paths are intercepted (`*.json` →
`BigBoxThemeApi`) and media flows through `/api/media`. The **database site has no
static files at all** (server-rendered HTML with inline CSS/JS).

### Recommended packaging for LiteBox — **bundled files, copied on deploy**

Consistent with how LiteBox already ships `thirdparty/` and native DLLs (copy next to
the exe / into `Core`), **not** embedded resources (39 MB embedded is wasteful; and
the themes rely on `Cache-Control: no-cache` dev-reload of on-disk files). Ship the
shell assets, **drop the demo media**.

Target layout under `LiteBoxPaths.Data` (`<LB>\Core\litebox\`):

```
Core\litebox\web\bigbox\      ← from BigBoxWeb/web/ (index.html + engine/ + css + sounds/), MINUS launchbox/, vendor/, and all dummy media dirs
Core\litebox\web\litebox\     ← from BigBoxWeb/web/launchbox/   (the "LiteBox Web" theme — renamed concept)
Core\litebox\web\database\    ← (near-empty) database site is 100% server-rendered; reserve for favicon / optional search-index.json overrides
Core\litebox\web\vendor\      ← shared JS libs; keep route /vendor/ unchanged (referenced relatively by both themes)
```

Resolver: add `LiteBoxPaths.Web(site)` = `Path.Combine(Data, "web", site)`. The three
static handlers each root at their own folder; `/vendor/` stays a separate shared
root. **Route change:** `/launchbox/` → keep the URL prefix `/launchbox/` internally
(the theme's own JS hardcodes relative `data/…` and `../vendor/…`, so changing the URL
mount would require editing the shipped JS) **but serve from `web\litebox\`**. i.e.
rename the *folder + concept*, keep the *URL mount* `/launchbox/` unless we also
rewrite the theme's fetch base. Flag as an open question (§7).

Deploy step: extract the pruned asset tree to `Core\litebox\web\` **on boot if
absent/version-stamped** (same pattern as `MagickSupport.Init` deploying the native
lib, and `LightPayload`/`NativeInstaller`). Version the extraction with a stamp file
so theme edits ship on update.

---

## 4. Config plan — `LiteBox.ini` `[Web]` section

Read via `LiteBoxConfig.GetSec/GetSecBool/SetSec`. Maps 1:1 to `ExtendDBConfig`:

| `[Web]` key | Type | Default | ExtendDBConfig source | Meaning |
|---|---|---|---|---|
| `Enabled` | bool | (module gate) | `EnableLocalWebServer` | Master on/off (redundant with `LbModule.Web`; prefer the module gate, keep key for parity). |
| `Port` | int | 8080 | `LocalWebServerPort` | TCP listen port. |
| `AllowedIps` | string | "" | `BigBoxWebAllowedIps` | Comma/space wildcard patterns; non-empty ⇒ bind `0.0.0.0` + per-conn filter (loopback always allowed). |
| `EnableDatabaseSite` | bool | true | `EnableWebDb` | Mount `/` database site. |
| `EnableLiteBoxWeb` | bool | true | `EnableLaunchBoxWeb` | Mount `/launchbox/` (LiteBox Web theme). |
| `EnableBigBoxWeb` | bool | true | `EnableBigBoxWeb` | Mount `/bigbox/`. |
| `GzipJson` | bool | true | `WebGzipJsonEnabled` | gzip `application/json` ≥1 KB when client sends `Accept-Encoding: gzip`. |
| `MediaPolicyUrl` | string | (default) | `MediaPolicyUrlWeb` | Already owned by `MediaFetch`/policy — reuse, don't re-add. |
| `KeyBinds*` | json | — | `WebEmbedKeys` / `WebEmbedKeysLaunchBox` | Rebindable nav; port as a small `[Web]` JSON blob or reuse `LiteBoxOptionsDb`. |

Gate: `LbModules.On(LbModule.Web)` is the mechanism switch (mirrors
`Modules.Active(Module.Web)` in `EmbeddedWebServer.Start`). The per-site keys select
which routes register.

**Secrets rule:** the media-token HMAC key and any credentials **must not** be
literals — route through the existing `Host/Media/BaseCredentials` / encrypted store
(same as `MediaFetch` already does). `MediaTokenSecret` (DPAPI per-user) should reuse
LiteBox's secret store rather than re-introducing its own.

---

## 5. Seams to cut

| Seam in source | Where | LiteBox replacement |
|---|---|---|
| `ExtendDBPlugin.Log("[Tag] …")` | everywhere | `LbLog.Info("web", …)` / `LbLog.Warn` |
| `ExtendDBConfigManager.ExtendDBConfig.<field>` | server + handlers | `LiteBoxConfig.GetSec("Web", …)` (§4) |
| `Modules.Active(Module.Web)` | `EmbeddedWebServer.Start` | `LbModules.On(LbModule.Web)` |
| `ExtendDBPlugin.PluginPath` (asset roots) | `ThemeStaticFiles`, `LaunchBoxPageHandler`, `VendorStaticHandler` | `LiteBoxPaths.Web("bigbox"/"litebox"/"vendor")` |
| `ExtendDBPlugin.ExtendedDbPath` | `DbRepository` | `MetadataDb.ExtendedDbPath` (already resolved by LiteBox) |
| `SqliteConnectionPatches.BypassRedirect` (ThreadStatic) | `DbRepository` open dance | **Drop** — LiteBox has no `ExecuteReader` read-intercept, so open the Extended DB directly. |
| `PluginHelper.DataManager` / `IGame` / `IPlatform` | `OwnedDataProvider`, `OwnedLookup`, mutation APIs, `StoreInstallState`, `PlatformMapper`, `LaunchBoxStatsApi` | **No change** — `PluginHelper.DataManager` returns LiteBox's `HostDataManagerXml`; SDK types are real in-process. Verify `HostDataManagerXml` parity (§7). |
| `PluginHelper.{LaunchBox\|BigBox}MainViewModel.PlayGame` | `GameLauncher.Play` | **Re-point** to LiteBox's own launch path (`Host/LaunchButtons` / `MainWindow` launch + `RomBridge`-equivalent arming), marshalled onto the **WinForms** UI thread (`UiThread`), not WPF `Application.Current.Dispatcher`. |
| WPF `Application.Current.Dispatcher` marshaling | `GameLauncher`, kiosk toggles | LiteBox is WinForms → `Host/UiThread` / `Control.BeginInvoke`. |
| `GameCache` (ExtendDB static) | `MediaResolver`, `GameDetailApi`, `OwnedDataProvider`, `BigBoxThemeApi` | `Host/Gc/GameCache` / `GameCacheBridge` (native). |
| ExtendDB parental (`ParentalControlManager`) | `WebParentalState` | `Host/Media/ParentalBridge` (already mirrors the same state) + a thin per-client cookie adapter. |
| `PolicyStore.Bootstrap()` fire-and-forget | `Start` | Reuse `MediaFetch` policy bootstrap if it exists; else drop (built-in defaults). |
| Kiosk toggle handlers (`BigBoxWebKioskFormsWindow`, WPF F10/F11/F12) | — | **Drop** — LiteBox already routes hotkeys via `Host/HostHotKeys` + `KioskBridge`; the WebView2 kiosk itself is a separate concern outside this server port. |

---

## 6. Staged port order (independently committable slices)

Smallest end-to-end-testable first. Each slice compiles, boots, and is visibly
verifiable.

| Slice | Delivers | Rough size | Verify |
|---|---|---|---|
| **S1 — Server skeleton + static** | `EmbeddedWebServer`+`Router`+`HttpRequest`+`HttpResponse` ported; module/config gate; `LiteBoxPaths.Web`; three static handlers + `/vendor` + `/robots.txt`; asset deploy-on-boot of the three folders | **~1,600** | Boot LiteBox, hit `http://127.0.0.1:8080/bigbox/` → theme shell renders on its **own dummy JS** (no DB/library needed). Also `/launchbox/`. |
| **S2 — Media proxy + thumbs + parental** | `MediaApi.Handle`/`HandleThumbById` (token decode, disk-first, **Range**), `MediaTokenSecret` (reuse secret store), `/thumbs/*`, `/api/badges/*`, `ParentalApi` + `WebParentalState` adapter over `ParentalBridge`, `/api/recent/epoch` | **~1,400** | Media/thumbs load in the S1 shells; parental lock/unlock cycle via cookie. |
| **S3 — Database site** | `DbRepository` (drop `BypassRedirect`), `HtmlShared`, `Pages/*`, `Api/*` (platforms/games/search/filters), `OwnedLookup` | **~4,000** | `/` renders real platform grid from Extended DB; platform/game pages + search work. Independent of the LB library. |
| **S4 — Theme data (BigBox + LiteBox Web)** | `OwnedDataProvider`, `BigBoxThemeApi`, `LaunchBox/*DataApi`, `PlatformMapper`, `RecentState`, `CoverPicker`, `WebKeyBindsApi`, mutation APIs **minus** play/archive; **`GameLauncher.Play` re-point** | **~4,500** | `/bigbox/` + `/launchbox/` browse the **real** library (via `PluginHelper.DataManager`), media via S2 proxy, install-state, favorite/hide mutations, **Play** launches a game. |
| **S5 — RA web glue** | Wire theme RA fields + badges to native `Host/Ra/*`; RA state in `detail.json`/badges (no re-port of `RetroAchievementsDb`) | **~500–800** | RA badges + progress appear on game cards, sourced from `Host/Ra`. |
| **S6 — Archive / Select-ROM (deferred)** | Port `ArchiveCacheDb`/`ArchiveAnalyzer`/`Archive*`/`M3uBuilder` + `ArchiveListingApi`/`ArchiveMetadataApi`; enable archive routes | **~3,450** | Select-ROM sub-menu lists archive entries; pin/favorite; launch chosen entry. **Blocked on native ArchiveMGS** (romextractor redesign). |

S6 is intentionally last and gated behind the archive routes being registered only
when a native archive backend exists — S1–S5 ship a fully usable web frontend without
it (Play falls back to the flat launch, no Select-ROM).

---

## 7. Open questions / risks

**Top 3 risks**

1. **Archive/Select-ROM has no native backend (~3,450 lines, blocked).** LiteBox's
   `RomBridge` currently *reflects into the ExtendDB plugin* for the Archive
   MultiGame Selector; there is no in-process `ArchiveCacheDb`/`ArchiveAnalyzer` in
   `LbApiHost`. Porting the web archive routes requires first landing native
   ArchiveMGS — which overlaps the in-flight *romextractor redesign* (separate
   chantier). Mitigation: ship S1–S5 with archive routes disabled; Play uses the flat
   path.

2. **`GameLauncher.Play` re-point is the one non-mechanical port.** Source calls WPF
   `PluginHelper.{LaunchBox|BigBox}MainViewModel.PlayGame` on
   `Application.Current.Dispatcher`. LiteBox is **WinForms** with its own launch
   pipeline (`LaunchButtons`/`MainWindow`/`RomBridge` arming, launch-history written
   in `OnBeforeGameLaunching`). Getting web-initiated launch to match the GUI's launch
   (extraction deferral, RA-on-launch, launch history single-source-of-truth) is the
   subtle part — see the ROM-selection-bridge memo.

3. **`HostDataManagerXml` parity.** The themes assume a *faithful* BigBox library
   shape: `GetRootPlatformsCategoriesPlaylists()`, `IPlatform.GetChildren()` returning
   a mix of `IPlatformCategory`/`IPlaylist`, plus per-game `LastPlayedDate`, favorite/
   hide flags, playlists. `HostDataManagerXml` is a `DummyDataManager` subclass —
   confirm it implements all of these (categories tree, playlists, last-played,
   favorite/hide) or the cattree/recent/related routes return empty/partial data.

**Other open questions**

- **Does the BigBox theme need BigBox.exe running?** **No.** The theme is a
  self-contained SPA served over HTTP; its data comes from `PluginHelper.DataManager`
  + `GameCache`, both in-process in LiteBox. (In ExtendDB it also ran inside
  BigBox.exe, but the data path never depended on the BigBox UI.)
- **Is the database site independent of the LB library?** **Yes** — it reads the
  Extended metadata DB directly via `DbRepository`, not the user library. It can ship
  in S3 before any theme work. Requires the Extended DB to be downloaded
  (`MetadataDb.ExtendedDbPath != null`); `HomeHandler` already degrades gracefully via
  `DbRepository.AnyDbReady()`.
- **URL mount vs folder name for the LiteBox Web theme.** The theme's shipped JS
  hardcodes relative `data/…` and `../vendor/…`, so the **URL mount must stay
  `/launchbox/`** even though the *folder* and *concept* are renamed to "LiteBox Web"
  (`web\litebox\`). Changing the URL to `/litebox/` means editing the theme's fetch
  base — decide whether to do that rewrite now or keep the legacy mount.
- **`web\database\` will be near-empty.** The database site is 100 % server-rendered
  (inline CSS/JS in `HtmlShared`). The folder is reserved for a favicon / optional
  `search-index.json` override only. Confirm the user still wants the empty folder for
  symmetry.
- **Parental cookie under LiteBox.** LiteBox → ExtendDB is a "LaunchBox" host
  (not BigBox), so the web uses the **cookie + PIN** unlock path (not the BigBox
  global-lock mirror). `ParentalBridge` defaults to LOCKED with no unlock UI; the web
  `ParentalApi` must own the per-client cookie unlock against the configured PIN
  (`BigBoxPin`/`LbSettingsCrypto`).
- **`StoreInstallState` duplication.** The web's `StoreInstallState` is a straight
  port of LiteBox's own `StoreInstallStateSync`. Consolidate to one implementation
  rather than shipping two galaxy-2.0.db/acf scanners.
- **Concurrency + SQLite.** Source caps at 20 concurrent handlers and opens the
  Extended DB read-only + WAL. Preserve both under LiteBox (no read-intercept means
  direct opens — keep WAL/read-only).
