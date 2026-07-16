# ExtendDB media core — vendoring map (deep-dive of integration steps 3–4)

> Companion to `extenddb-integration-map.md`. That doc maps the whole dependency surface; this one
> answers "copy or rewrite?" for the two big chunks (the credentialed media fetcher and the extended-DB
> downloader) by measuring their REAL dependency closure inside the ExtendDB source tree.
>
> **Verdict: vendor (copy into a LiteBox namespace) — the closure is small and the wires to cut are
> few and concentrated.** Rewriting ~10k lines of proven multi-origin fetch/download logic would be
> slower and riskier than cutting ~5 seams.

## Method

Transitive type-reference closure over the ExtendDB sources (208 files / 91k lines), seeded from the
exact symbols LiteBox's `MediaApiBridge` reflects into (`MediaApi.FetchForWizard`,
`MediaSourcePolicy.ContextFromType`, `ImageMetadata`) plus `LbDbUpdater`, with traversal CUT at the
out-of-scope subsystems (Forms, Patches, kiosk Web/Pages|Theme|LaunchBox, RA, archives, stores,
watchers). Un-cut, the closure explodes to 181 files — one incidental mention of `EmbeddedWebServer`
pulls the whole kiosk — which is precisely why the boundaries below matter.

## Package 1 — media fetcher (~4.4k lines to vendor)

| File | Lines | Role |
|---|---|---|
| `Web/Api/MediaApi.cs` | 1495 | `FetchForWizard` + per-origin fetch orchestration (the bridge's entry point) |
| `Web/Backend/MediaResolver.cs` | 1002 | per-origin URL builders (ScreenScraper, EmuMovies, Steam CDN, HLS) |
| `Web/Backend/MediaSourcePolicy.cs` | 412 | source policy + `ContextFromType` (the bridge's 2nd entry point) |
| `Web/Backend/PolicyStore.cs` | 346 | persisted per-source policy |
| `Web/Backend/ScreenscraperApiBreaker.cs` | 271 | SS auth/quota — **contains hardcoded DevId/DevPassword, see Secrets** |
| `Utility/ImageMetadata.cs` | 261 | the meta type the bridge marshals |
| `Web/Backend/Models.cs` | 224 | shared DTOs |
| `Web/Backend/ImageHelper.cs` | 190 | image sniffing/helpers |
| `Web/Backend/MediaTokenSecret.cs` | 171 | kiosk-URL token — may drop if only the kiosk path uses it |
| `Web/HttpRequest.cs` + `HttpResponse.cs` | 436 | kiosk HTTP wrappers — the WIZARD path returns `HttpResponseMessage`; trim if only the serving path needs them |

## Package 2 — extended-DB downloader (~7.2k lines to vendor)

Verified real dependencies (not incidental): download → restore → merge is one pipeline.

| File | Lines | Role |
|---|---|---|
| `Database/LbDbUpdater.cs` | 1997 | orchestrator: version check, download, swap |
| `Database/SqliteBackupLib.cs` | 2052 | restore/patch the downloaded archive (`RestoreAsync`, `RestorePatchAsync`, `GetVersionAsync`) |
| `Database/LbDbMerger.cs` | 1370 | `MergedTableNames` + merge of LB's own Metadata.db into the extended DB |
| `Database/LbExtendedSync.cs` | 727 | `RunStructureSync` / `RunDataMergeNow` post-update |
| `Database/SqlGitHubReleaseClientLib.cs` | 561 | GitHub-release download client |
| `Database/SqlQueryCache.cs` | 518 | query cache (check if the updater path really needs it) |
| `Database/ExtendDBState.cs` + `ExtendedDbStatus`? | 231+ | state/fingerprint |
| `Database/DbFingerprint.cs` | 151 | DB fingerprint |

## Already native in LiteBox — do NOT vendor (dedupe against the closure)

- `Cache/GameCache.cs` (1835) → `Host/Gc/HostGameCache`
- `Utility/FileMetadataStorage.cs` (628) → `Host/Media/FileMetaStore` (byte-compatible)
- `Utility/Everything.cs` (643) → host's own Everything wrapper (HostGameCache)
- `Configuration/ExtendDBConfig.cs` (820) → LiteBox.ini / litebox-options.db
- `Cache/DefaultOverviewCache.cs`, `Utility/Utils.cs`, `VolumeCapabilities`, `CrcCache`, `LbRegions` —
  dragged into the closure by the above; take only the helpers the vendored files actually call.

## Wires to cut (all mentions from the vendored set into out-of-scope code)

| Target | Mentions | Replace with |
|---|---|---|
| `Program.cs` (Program.Config / Modules.Active / plugin state) | 92 | LiteBox module state + options (the ONLY big rewiring) |
| `Patches/SqliteConnectionPatches.cs` (Harmony SQLite intercept) | 33 | plain `Microsoft.Data.Sqlite` connections (LiteBox owns its SQLite) |
| `Web/Backend/StoreInstallState.cs` | 20 | LiteBox's own `StoreSupport`/install-state sync |
| `Patches/HttpInterceptPatches.cs` (Harmony HTTP intercept) | 15 | delete (LB-only interception) |
| `Watchers/*` (settings/metadata watchers) | 13 | LiteBox equivalents or drop |
| misc (Router, ThumbCache, DbRepository, CoverPicker) | 6 | serving-path only — trim with the kiosk path |

## Secrets (hard requirement — LiteBox repo is PUBLIC)

`ScreenscraperApiBreaker.cs:69-71` hardcodes the ScreenScraper `DevId`/`DevPassword`/`SoftName`.
These must NEVER land in the public repo: before vendoring, move them to an encrypted/gitignored
credentials source under `Core\litebox\` (same treatment as the LB settings crypto), and vendor the
file with the constants stripped. Audit the rest of the vendored set for anything similar
(`MediaTokenSecret` appears to be a per-install token generator, not a shipped secret — verify).

## NuGet deps inherited by the vendored set

Keep: `Microsoft.Data.Sqlite` (already in LiteBox), `Newtonsoft.Json` or migrate to System.Text.Json,
`SharpCompress`/`ZstdSharp` (DB archive restore), `CRC.Fast.Net` (check usage).
Cut with the out-of-scope code: `Lib.Harmony`, `CefSharp`, `Microsoft.Web.WebView2`, `MessagePack`
(verify — may be the kiosk serialization), `Magick.NET` (LiteBox drives Magick.Native directly).

## Suggested order

1. Vendor Package 1 into `Host/Media/Ext/` (namespace `LbApiHost.Host.Media.Ext`), strip secrets,
   cut the 5 seams, wire `MediaApiBridge` to call it directly (keep the reflection path as fallback
   during the transition, then delete).
2. Vendor Package 2 into `Host/Data/ExtDb/`, same treatment; gives LiteBox the native
   download/version/swap of `LaunchBox.Extended.Metadata.db`.
3. Then resume the outer map's order (§5): drop the remaining bridges one by one.
