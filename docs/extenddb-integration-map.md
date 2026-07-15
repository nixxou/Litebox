# ExtendDB → LiteBox native integration — reference map

> Branch: `integration-extenddb`. Goal: fold ExtendDB's functionality into LiteBox so the plugin is never
> loaded. Step 1 (done): LiteBox never loads `Plugins\ExtendDB`; it shows greyed in Options → Plugins.
> This document maps **every** point where LiteBox references ExtendDB, so the port (Option B) is planned,
> not discovered mid-way.

## Legend
- 🔴 **Hard** — reflection into the `ExtendDB` assembly. Breaks (or degrades to a no-op) without the plugin.
- 🟢 **Native fallback exists** — the reflection is only a *preferred* path; LiteBox already has a native
  implementation that runs when ExtendDB is absent. Porting = drop the reflection preference.
- 🟡 **Soft** — a path / file-format / naming convention. Works standalone; only matters for interop.
- ⚪ **Out of scope** — BigBox-web / kiosk only; a desktop frontend (LiteBox) doesn't need it.

---

## 1. Reflection touchpoints (the hard couplings)

Every place that reflects into the `ExtendDB` assembly. This is the real dependency surface.

| # | File:line | ExtendDB target | Feature | Fallback today | Port approach | Effort |
|---|-----------|-----------------|---------|----------------|---------------|--------|
| 1 | `Host/Media/MediaApiBridge.cs:47-81` | `Web.Api.MediaApi`, `Web.Backend.MediaSourcePolicy`, `Module(s)`, `ExtendDBPlugin.CustomDatabaseExist` | **Credentialed per-origin media fetch** (ScreenScraper w/ dev-id+login, Steam CDN, EmuMovies) + extended-DB module state (`UseWizardPath`) | 🔴 none — native `ImgFetchWebBytes` only hits the LaunchBox CDN (launchbox-origin rows). ScreenScraper/EmuMovies rows are un-fetchable. | Port `MediaApi` + `MediaResolver` (per-origin URL builders, auth, Steam-CDN, HLS) + credentials source. **This is the crux of Option B.** | **HIGH** |
| 2 | `Host/Media/MetadataDb.cs:93-94` | `ExtendDBPlugin.ExtendedDbPath` | Path of the extended DB (`LaunchBox.Extended.Metadata.db`) | 🟢 conventional path fallback already present (`Plugins\ExtendDB\...`, line 104) | Keep the conventional path; add a native **download/version/swap** for the DB (port `LbDbUpdater`) so it exists without ExtendDB | LOW (path) + MED (downloader) |
| 3 | `Host/Media/GameCacheBridge.cs:51-54` | `ExtendDB.GameCache` / `ExtendDB.Cache.GameCache` | Per-platform media cache | 🟢 **`Host/Gc/HostGameCache.cs`** (native port) — `else if (HostGameCache.Enabled)` at :99 | Make HostGameCache the sole path; delete the bridge | LOW |
| 4 | `Host/Media/ImageInfoBridge.cs:37-41` | `Utility.ImageInfoAds` (+ `ImageInfoData`) | Read/write image `:info` ADS provenance | 🟢 native parse via **`FileMetaStore`** (:77 — "native parse of the compact short-key JSON") | Drop the ExtendDB-reader preference; keep the native `FileMetaStore` path | LOW |
| 5 | `Host/Media/FileMetaStore.cs:46-47` | `Utility.FileMetadataStorage` | `:crc32` / `:info` NTFS ADS store | 🟢 **`FileMetaStore` IS a native, byte-compatible port** already (:1) | Drop the reflection; native already writes/reads | LOW |
| 6 | `Host/Media/ImageLockBridge.cs:26-27` | `Utility.ImageLockAds` | Image "locked" flag ADS | 🔴/🟡 no native writer yet (small) | Port `ImageLockAds` (a one-key ADS read/write, ~1 file) | LOW |
| 7 | `Host/Media/RomBridge.cs:38-39` | `ExtendDB.HostRomBridge` | Keep ExtendDB's ROM-extraction/RA-hash state in sync with LiteBox's | 🔴 no-op without ExtendDB (:14) — LiteBox owns ROM handling via its own op-log | Drop the sync; LiteBox is the single source of truth once ExtendDB is gone | LOW |
| 8 | `Host/Media/ParentalBridge.cs:86-103` | `ParentalControlManager`, `Modules`, `ParentalLockPopupForm` | BigBox parental PIN / lock | ⚪ BigBox-only; LiteBox has the native `extenddb.asi` read-filter | Decide scope. If LiteBox needs parental, port the manager; else drop | MED / drop |
| 9 | `Host/Media/HostGameNavBridge.cs:31-32` | `ExtendDB.HostGameNavigation` | Game-navigation hook for the web frontend | ⚪ no-op without ExtendDB (:7); web-kiosk related | Out of scope for desktop LiteBox → drop | LOW (drop) |
| 10 | `Host/Media/KioskBridge.cs:32-33` | `Forms.BigBoxWebKioskFormsWindow` | The BigBox **web kiosk** window | ⚪ no-op without ExtendDB (:11); BigBox-web only | Out of scope for desktop LiteBox → drop | LOW (drop) |
| 11 | `Host/Diag/GameCacheProbe.cs:18-21` | `ExtendDB.GameCache` | Diagnostic probe of ExtendDB's cache | 🟢 diagnostic only | Point at HostGameCache or delete | LOW |

**Also:** `Host/Install/NativeInstaller.cs:91` checks `Plugins\ExtendDB\ExtendDB.dll` exists — used only to decide whether a *refresh* should prompt before touching a shared native. Not reflection; keep or simplify.

---

## 2. Soft references (paths / formats / conventions — work standalone)

These read/write ExtendDB's on-disk conventions. They don't need the plugin loaded; they only matter for
**interop** (a still-installed ExtendDB and LiteBox sharing the same files). Keep as-is unless we relocate.

- **Extended DB location**: `Plugins\ExtendDB\LaunchBox.Extended.Metadata.db` — `MetadataDb` (read), see §1.2.
- **ADS format** (`:info` / `:crc32` / lock) — `FileMetaStore`, `ImageInfoBridge`, `ImageAdsWriter`, `ImageLockBridge`. Byte-compatible with ExtendDB; LiteBox already reads/writes it natively.
- **Thumbnail cache**: `Plugins\ExtendDB\cache\thumbs` (+ our `video/`, `webimg/`, `docs/` subfolders) — `ThumbCache`, `VideoThumbnailer`, `EditGameWindowImages`.
- **Shared native lib dir**: `ThirdParty\ExtendDB\Magick.Native-*.dll` — `ThumbCache.MagickSupport`, `NativeInstaller` (deploys it there, shared with ExtendDB).
- **UI**: the "ExtendDB" loaded indicator label (`MainWindow` `_extDbInd`) + the uninstall dialog's "shared with ExtendDB" options.
- **Kiosk window-title detection**: `GameScreens.cs:227` (`t.StartsWith("ExtendDB") && t.Contains("Web")`) — detects the ExtendDB web window; ⚪ kiosk-related.

---

## 3. The extended metadata DB (distribution)

The 3.8 GB `LaunchBox.Extended.Metadata.db` is **built** by ExtendDB (scraping ScreenScraper/VNDB/IGDB/Steam +
merge) and **distributed prebuilt** as a GitHub release. LiteBox only ever *reads* it.

- **Do NOT port the build/scrape/merge pipeline** — keep consuming the published DB.
- **Do port**: a native download + version-check + swap (from `ExtendDB/Database/LbDbUpdater.cs` — the download
  orchestrator) so the DB is present without the plugin. This is the second real chunk of Option B.

---

## 4. Scope decisions (settle before porting)

- **Web kiosk / BigBox web** (KioskBridge, HostGameNavBridge, GameScreens kiosk detection): LiteBox is a
  **desktop** frontend — almost certainly OUT of scope. Dropping these bridges is likely correct.
- **Parental control** (ParentalBridge): BigBox parental. Keep only if LiteBox wants a desktop parental gate.
- **RomBridge sync**: once ExtendDB is gone, LiteBox is the sole ROM-state owner → the sync becomes dead.

---

## 5. Recommended migration order (Option B)

1. **ADS + FileMeta + ImageLock** — drop the ExtendDB-reader preference; keep/finish the native ADS (already
   byte-compatible). Small, self-contained, unblocks provenance everywhere. (§1.4, 1.5, 1.6)
2. **GameCache** — make `HostGameCache` the only path; delete `GameCacheBridge` + `GameCacheProbe`. (§1.3, 1.11)
3. **Extended DB access** — replace `ExtendedDbPath` reflection with the conventional path + a native
   download/version/swap (port `LbDbUpdater`). (§1.2, §3)
4. **MediaApiBridge → native fetcher** — port `MediaApi` + `MediaResolver` (per-origin URL builders, ScreenScraper
   auth from ExtendDB's config, Steam CDN, EmuMovies, HLS). **The big one.** (§1.1)
5. **RomBridge** — remove the ExtendDB sync. (§1.7)
6. **Kiosk / Nav / Parental** — drop (or port parental if wanted). (§1.8, 1.9, 1.10)
7. Cleanup: retire each bridge as its consumers move to native; the ExtendDB grey-out note becomes literally true.

At every step the branch stays runnable: each bridge already degrades to native/no-op, so we replace them one
at a time without a broken intermediate state.
