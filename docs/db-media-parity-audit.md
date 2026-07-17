# DB / Media / Thumbs parity audit — plugin vs LiteBox

Full functional + structural comparison of the database access layers, Extended-DB lifecycle,
thumbnail/media pipelines and web endpoints between the original ExtendDB plugin (reference,
read-only) and the LiteBox host. Snapshot as of 2026-07-17. File:line refs: plugin refs are
relative to the ExtendDB plugin tree, LiteBox refs to LbApiHost.

Legend: **[LOSS]** = unapproved parity regression (must fix or explicitly waive) ·
**[ADAPTED]** = same behavior, different mechanism (justified by environment) ·
**[WAIVED]** = deliberate, documented exclusion · **[DEAD]** = ported but unwired ·
**[BETTER]** = LiteBox exceeds the plugin.

---

## 1. Thumbnail pipeline

### 1.1 [LOSS] `/api/media/{id}.jpg` lost the Thumb-context chain
- Plugin `MediaApi.HandleThumbById` (MediaApi.cs:255-292): context **fixed to `MediaContext.Thumb`**
  → chain `MalkavThumb → ExtenddbThumb → per-origin` (MediaSourcePolicy.cs:309-312). The first two
  kinds are **id-only** — a thumb is served without ever querying the DB; the images row (token) is
  materialised **lazily**, only if the id-only kinds fail.
- LiteBox `MediaProxy.HandleThumbById` (MediaProxy.cs:130-144): always queries
  `MetadataDb.ImagesForGame(id)` + `PickCover`, then `MediaFetch.FetchBytes` whose context is derived
  from the row Type → `GalleryImage` → **full-size cover chain** (LaunchboxCdn/ExtenddbImage).
- Impact: every consumer of the id endpoint downloads a full-size cover instead of a few-KB
  pre-made thumb, plus a DB images query per request. Consumers: database-site paginated grid
  (DbApi.cs:194), Related cards of both web themes (RelatedProvider.cs:135), desktop Related cards
  (Host/Similar/RelatedGamesUi.cs, same functions called directly).
- The Thumb chain **is ported** in MediaFetch (MediaFetch.cs:119-125) but nothing selects
  `MediaContext.Thumb` — `ContextFromType` never returns it and `ListUrls` skips it. **[DEAD]** until
  wired.
- Fix: add a thumb-by-id entry point in MediaFetch (context=Thumb, id-only kinds first, lazy token
  for the tail + cover fallback), mirroring MediaApi.cs:255-292; use it from MediaProxy.HandleThumbById
  and the desktop Related cards.

### 1.2 [LOSS] Related cards: local games lost the local-disk thumb path
- Plugin `OwnedDataProvider.BuildRelItem` (OwnedDataProvider.cs:787-843): owned game → signed
  `?q=thumb` token (degraded JPEG from local disk via ThumbCache), fallback `/api/media/{id}.jpg`
  on cache miss; DB-only game → `/api/media/{id}.jpg`.
- LiteBox `RelatedProvider.Card` (RelatedProvider.cs:134-135): **every** card with a cloud id gets
  `/api/media/{dbid}.jpg` — owned games included → network fetch where the plugin used local disk.
- Fix: mirror the plugin: owned → local ThumbProxy path, DB-only → id endpoint.

### 1.3 [ADAPTED] `/thumbs/{id}.jpg`
- Plugin: bare **302 redirect** to `{RemoteImageBaseUrl host}/thumbs/{id}.jpg`
  (ThumbHandler.cs:30-35, ImageHelper.cs:78-99). No local work, no fallback; browser follows.
- LiteBox: self-rendered — cover materialised once to `Core\litebox\cache\thumbs\webimg\{id}.ext`,
  degraded to 360px JPEG via ThumbCache, served with 24h cache (ThumbHandler.cs).
- Trade-off: works offline / no dependency on the remote pre-made set, but first hit costs a
  full-size download, and the webimg source cache grows unbounded (§1.5). Acceptable adaptation;
  revisit once 1.1 lands (the degraded step could start from the pre-made thumb instead of the
  full cover).

### 1.4 [LOSS] Desktop Similar viewer thumb parity
- Plugin `SimilarGamesViewerForm` (QueueThumb :474-516): owned → local Front path; DB-only →
  direct `{host}/thumbs/{id}.jpg`; **in-memory URL-keyed Image cache per form** (:518-526); 4-wide
  semaphore; no disk cache.
- LiteBox `RelatedGamesUi` (current): owned → local Front path (parity OK); DB-only → full-size
  cover chain (regression, same root as 1.1); per-card Image only, no cross-render cache → sub-tab
  flips re-download.
- Fix: thumb-by-id first (1.1), plus a small in-memory cache keyed by dbid (parity) — a bounded
  disk cache would be [BETTER] than the plugin (see §1.5).

### 1.5 [ADAPTED, shared TODO] ThumbCache eviction
- Neither side evicts. Plugin documents "size-budget sweep — TODO" (ThumbCache.cs:23-28). LiteBox
  ThumbCache (Host/Media/ThumbCache.cs) has no delete/LRU/cap either, and the /thumbs webimg source
  cache is also unbounded. Faithful parity, but both grow forever — candidate improvement.

### 1.6 [WAIVED, revisit] PolicyStore (remote policy refresh)
- Plugin: chains overridable at runtime from `media-web.json` / `media-helper.json`
  (PolicyStore.cs — 5s fetch, 10-min TTL, atomic swap, builtin fallback). Gave a remote kill-switch
  when a CDN dies or changes layout.
- LiteBox: chains are compiled into MediaFetch (docs/web-module-port-plan.md:139 documents the
  drop). Consequence: repolicying requires a release. Decision to revisit.

### 1.7 Parity OK (verified)
- Chain tables per (context, origin) byte-equivalent (MediaSourcePolicy.cs:300-365 vs
  MediaFetch.cs:104-143), incl. Manual/screenscraper leading with the credentialed API.
- URL builders equivalent (permuted-md5 extenddb image URL, malkav, LB CDN, ThumbnailRedirect,
  screenscraper php/media/api, NormalCdn/SteamCdn `.m3u8.mp4` strip).
- Circuit breakers: malkav 403 → 1h blackout; screenscraper 401/403/430/431 → 1h + quota gate.
- Signed-token proxy: token shape frozen (p/f/c/o/t), HMAC-verify-first, disk-first with
  `?q=thumb|logo|full`, Range support, same cache headers.
- Credentials: never in source; ScreenscraperApi self-skips without creds.
- Steam wizard retry loop: plugin-only (MediaApi.cs:594-629) but its consumer is the LB
  metadata-download wizard — **[WAIVED]** with the wizard (Harmony surface not hosted by LiteBox).

## 2. Database access layer

### 2.1 [LOSS] Search lost the normalized alternate-name path
- Plugin web `DbRepository.Search` (:706-808): extended mode matches on `AltNameCompareValue*`
  via `Normalizer.PerformSanitize` + `NormalizeCompareName`, with raw-LIKE fallback.
- LiteBox `Host/Web/Db/DbRepository.Search` (:473): substring LIKE only, no Normalizer port.
- Impact: web search misses accents/punctuation/alt-name variants the plugin matched.

### 2.2 [ADAPTED] Repository mechanics
- Plugin: Harmony coupling (BypassRedirect, ExecuteReader-patch overview rewrite for
  GetOverviewsForGames), Pooling=true. LiteBox: direct Microsoft.Data.Sqlite, Pooling=false (so a
  mid-session swap isn't pinned), overview resolution via DTO helpers instead of a patch rewrite.
  Same query surfaces, same native-DB fallback shape (extended-only columns NULL-aliased, roms and
  origins empty on base).
- Parental filtering moved from SQL fragments (ExtraWhere) to the API layer — equivalent output.
- Extended-vs-native gates differ structurally: plugin = file-ready probe (`_ext`), LiteBox =
  `ModuleActive && UseExtendedAsMain` (module system is LiteBox-only). Note the LiteBox suggester
  reads `ExtendedDbPath` regardless of `UseAsMainDb` (own analysis; deliberate — `AllowDbGames` is
  the per-category gate).

### 2.3 SqlQueryCache — equivalent by replacement
- Plugin: two-layer (in-mem + msgpack disk) cache whose ONLY consumer is ExecuteReader-patch
  Section 2c serving **LB's native GameSuggester SELECTs**.
- LiteBox: no Harmony, no native LB suggester — replaced by the native engine's own 3-layer
  candidate-pool cache (Host/Similar/GameSuggester.cs). No action.

## 3. Extended-DB lifecycle

### 3.1 [WAIVED, documented] Merge machinery
LB→Extended data merge (Phase 2), ForceMerge, UndoLastMerge, Unmerge, EF structure sync,
PrepareTmpDb — all absent by decision: LiteBox uses the Extended DB **read-only**
(ExtDbDownloader.cs:20-24; BasePanel.cs:7-8 shows the disabled actions with the gap note).

### 3.2 [LOSS, minor] Update-check outcome cache
Plugin caches the GitHub check outcome for 5h (GitHubCheckOutcome, LbDbUpdater.cs:82-152);
LiteBox `CheckAsync` hits GitHub every call (incl. every boot with AutoUpdateDb).

### 3.3 [ADAPTED] Swap retry
Plugin: 12 attempts exponential (≤~2h13) suspended while the LB wizard is active. LiteBox:
5×1s then park `.todo`, applied next boot. The wizard guard is moot (no wizard in LiteBox) and
`.todo`-at-boot covers the rest.

### 3.4 [BETTER] LiteBox-only capabilities
SHA-256 manifest verification of archives; adoption of a legacy plugin DB without re-download
(AdoptLegacyAsync); own-first/legacy-fallback path resolution; in-process SQL-dump restore;
single-flight shared operation with progress fan-in.

## 4. Web endpoints

- Route tables match 1:1 with the port plan (docs/web-module-port-plan.md §1); slices S1-S5 landed,
  S6 (archive/Select-ROM backend) tracked separately.
- Quirk: the LiteBox theme's related panel fetches `/bigbox/data/games/{id}/related.json`
  (litebox app.js:879) — cross-mount but same payload; harmless, could be repointed.
- Kiosk F10/F11/F12 + Exit parity handled natively (HostHotKeys/KioskBridge + WebMessages).

## 5. Prioritized fix list — ALL IMPLEMENTED (same day)

| # | Item | Ref | Where it landed |
|---|---|---|---|
| P1 ✔ | `MediaFetch.FetchThumbById` (context=Thumb, id-only kinds first, lazy cover token, origin-switch restart — plugin RunChain parity); wired into `MediaProxy.HandleThumbById` and the desktop Related cards. Beyond-plugin: full-size cover as last resort when every thumb source fails | §1.1, §1.4 | MediaFetch.cs / MediaProxy.cs / RelatedGamesUi.cs |
| P2 ✔ | RelatedProvider: owned games → `OwnedDataProvider.RelatedLocalThumb` (`?q=thumb` signed proxy), id endpoint as fallback/DB-only path | §1.2 | RelatedProvider.cs / OwnedDataProvider.cs |
| P3 ✔ | Desktop card thumbs: in-memory LRU (300 entries) + bounded disk layer `Core\litebox\cache\related-thumbs\{dbid}.jpg` (500-file cap, oldest-mtime sweep) | §1.4 | RelatedGamesUi.cs (RelatedThumbCache) |
| P4 ✔ | Normalized search path: `TitleNormalizer` (faithful Normalizer port) + AltNameCompareValue/Fallback query branch, `_ext`-gated | §2.1 | TitleNormalizer.cs / DbRepository.Search |
| P5 ✔ | 5h GitHub-check outcome cache (in-memory, invalidated on install/adopt/todo, `force` bypass) | §3.2 | ExtDbDownloader.CheckAsync |
| P6 ✔ | PolicyStore ported: `MediaPolicyStore` (web+helper slots, `[Base] MediaPolicyUrlWeb/Helper`, 10-min TTL, validate-or-keep, atomic swap, builtin fallback) + `BlockIfScreenscraperFail` enforcement; bootstrapped at web-server Start; all chain walks now take a per-request policy snapshot | §1.6 | MediaPolicyStore.cs / MediaFetch.cs / EmbeddedWebServer.cs |
| P7 ✔ | ThumbCache size-budget sweep: 500 MB cap → trim to 400 MB by oldest mtime across root + video/webimg/docs, once per process in the background | §1.5 | ThumbCache.cs (KickSweep) |
