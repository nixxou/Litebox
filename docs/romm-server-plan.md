# RomM server module + EmulatorJS in BigBox Web — recon & plan

> Two related deliverables, one shared prerequisite.
>
> **A. `romm-server` module** — LiteBox impersonates a RomM server well enough that the
> official RomM clients (Argosy / Grout / the Playnite plugin) connect to the LaunchBox
> library and play from it, saves included.
> **B. EmulatorJS in BigBox Web** (no module, always available on the non-kiosk surface) —
> play the supported platforms in the browser, with the desktop save files imported into
> the emulator and written back when they have not moved underneath us.
>
> Reference implementation (READ-ONLY): `ExtendDB/romm/` — RomM master, FastAPI + Vue,
> pinned at upstream `76bc157` (2026-08-23). Every contract detail below was read from that
> tree, not from the public docs.
> Target: `LbApiHost/Host/Romm/` (new) + `Host/Web/` (existing) + `web-assets/bigbox/`.

---

## 0. Executive summary

| Item | Value |
|---|---|
| RomM API surface (upstream) | ~175 HTTP routes, 24 routers, 11 socket handlers |
| Surface actually needed for the 3 chosen clients | **~40 routes** (§5.9 lists what we skip and why) |
| Biggest hidden cost | **Not the API** — it is the HTTP core (§3.1). Today's server cannot read a body over 64 KB, cannot parse multipart, cannot stream a response, and 405s on PUT/DELETE. Every save upload and every ROM download needs all four. |
| Second biggest | The **identity + slug mapping** (§5.2, §5.3): RomM is integer-keyed and IGDB-slug-keyed, LaunchBox is GUID-keyed and free-text-platform-keyed |
| Free lunch | `SaveManager` (vault + `EmulatorPlugin` scan), `RomExtractor` (listing + extraction + LRU cache), `OwnedDataProvider` (library projection), `MediaProxy`/`ThumbCache` (covers), `RaPlatformMap` (the mapping precedent) |
| Recommended first slice | **S0 — the HTTP core upgrade**, shared by A and B, verifiable on the existing surfaces alone |

The honest framing: A is *mostly a projection problem, not a protocol problem*. RomM's JSON is
wide but forgiving (nearly every field is nullable), and the clients care about a small core.
The work is deciding what a LaunchBox game **is** in RomM's model, and making that decision
stable across restarts.

---

## 1. What RomM actually is (recon)

### 1.1 Stack

Python 3.13 / FastAPI / SQLAlchemy / MariaDB / Redis / Socket.IO, frontend Vue 3 (two UIs, v1
frozen and v2 active). We reimplement **none** of that — we reimplement its *wire contract*.

### 1.2 The contract points that matter

**Discovery.** `GET /api/heartbeat` is what every client hits first. It returns
`SYSTEM.VERSION`, `SYSTEM.SHOW_SETUP_WIZARD`, a `METADATA_SOURCES` block of booleans, an
`EMULATION` block (`DISABLE_EMULATOR_JS`, `DISABLE_RUFFLE_RS`), and `FILESYSTEM.FS_PLATFORMS`.
Clients gate features on these booleans, so answering it correctly (all sources `false`, no
setup wizard) is how we tell a client "I am a plain library server, do not offer scraping".

**Auth** (`handler/auth`, `endpoints/auth.py`, `endpoints/client_tokens.py`). Four ways in:

| Mechanism | Wire | Notes |
|---|---|---|
| HTTP Basic | `Authorization: Basic` on any route | Simplest; bypasses CSRF entirely |
| Session | `POST /api/login` (Basic) → `romm_session` cookie + `romm_csrftoken` | Browser clients only |
| OAuth2 | `POST /api/token` (form: `grant_type=password\|refresh_token`, `username`, `password`, `scope`) → JWT HS256, 30 min access + 7 day refresh | What Argosy uses |
| Client token | `Authorization: Bearer rmm_<64 hex>`, created via `/api/client-tokens`, paired via `/api/client-tokens/{id}/pair` + `/exchange` | What Grout-style devices use |

CSRF is *skipped* whenever `Authorization: Bearer` or `Basic` is present — so a token-only
implementation never has to implement CSRF at all.

Scopes are strings (`roms.read`, `assets.write`, `me.read`, …) with three roles
(VIEWER / EDITOR / ADMIN). Clients read `GET /api/users/me` and hide UI per scope.

**Library.** `GET /api/platforms` (list) and `GET /api/roms` (limit/offset paginated, filtered
by `platform_id`, `search_term`, `order_by`, …). `RomSchema` (`endpoints/responses/rom.py`,
690 lines) is wide: ~12 external-provider ids, 9 metadata blobs, cover/manual/video paths,
hashes, region/language/tag lists, `rom_user` (per-user flags), `files[]`, `sibling_roms[]`.
Nearly all of it is nullable — a minimal but *correct* projection is maybe 40 fields.

**Download.** `GET /api/roms/{id}/content/{file_name}?file_ids=1,2` —
single file ⇒ the raw file with `Content-Disposition`; multiple files ⇒ **a zip built on the
fly containing those files plus a generated `.m3u`** (upstream streams it through nginx
mod_zip; we build it ourselves). `?hidden_folder=` toggles muOS's multi-disc folder layout.

**Assets.** `POST /api/saves` (multipart: `romId`, `emulator`, file), `GET /api/saves?rom_id=`,
`GET /api/saves/{id}/content`, `PUT /api/saves/{id}`, `POST /api/saves/delete`. Same shape for
`/api/states` and `/api/screenshots`. `SaveSchema` carries `emulator`, `slot`, `content_hash`,
an optional `screenshot`, `origin_device_id` and `device_syncs[]`.

**Devices.** `POST /api/devices` registers a fingerprint; saves then carry per-device sync rows
(`is_current`, `is_untracked`) and `POST /api/saves/{id}/downloaded` acknowledges a pull. This
is the Grout path — the whole point is "this handheld already has this save version".

### 1.3 The three clients we target

| Client | What it needs | What it never calls |
|---|---|---|
| **Argosy** (Android, official) | heartbeat, `/api/token`, users/me, platforms, roms (paginated + covers), rom content download, saves + states up/download, collections | scanning, feeds, netplay, tasks |
| **Grout** (muOS/Knulli/ROCKNIX/…) | heartbeat, client token or basic, platforms, roms, download (with `hidden_folder`), devices, saves sync verbs | metadata search, uploads, admin |
| **Playnite plugin** | heartbeat, auth, platforms, roms + covers/metadata, download | assets, devices |

Union ≈ 40 routes. Everything else gets an honest 404/501 (§5.9).

---

## 2. What LiteBox already gives us

| Asset | File | Reused for |
|---|---|---|
| HTTP server + router + static | `Host/Web/EmbeddedWebServer.cs`, `Router.cs`, `StaticFileHandler.cs` | second listener (A), existing surface (B) |
| Library projection | `Host/Web/Theme/OwnedDataProvider.cs` | platforms + roms lists |
| Parental gating per client | `Host/Web/WebParentalState.cs` | hide restricted games from RomM clients too |
| Covers / media | `Host/Web/MediaProxy.cs`, `ThumbHandler.cs`, `Host/Media/ThumbCache` | `path_cover_small/large` |
| Save engine | `Host/Saves/SaveManager.cs` | the entire asset story, both A and B |
| Archive engine | `Host/Rom/RomExtractor.cs` (`ListEntriesDetailed`, `ResolveLaunch`, LRU cache, RAM disk) | `files[]` projection + on-demand extraction |
| Mapping precedent | `Host/Ra/RaPlatformMap.cs` (hand-curated LB platform → RA console, 273 lines) | the same shape for RomM slugs |
| Module gate + options | `Host/Modules/LbModules.cs`, `Host/Options/ModulesOptions.cs` | `LbModule.RommServer` |
| Launch seam | `Host/Web/Theme/BigBoxMutationApi.cs` → `HostLaunch.Launch("web", …)` | where B's "play in browser" branches off |
| Asset pipeline | `deploy-dev.ps1` (staging + served folders), `build-release.ps1` (`web-assets` → `Core\litebox\web-assets`) | shipping EmulatorJS + the new theme JS |

`SaveManager` deserves emphasis: it already drives the same `EmulatorPlugin` contract
LaunchBox drives (`GetSaves`, `AddSaveFile`, `TryBackupSave`, `TryComputeSaveSignature`,
`IsSaveActive`), keeps `<GameSave>` records LaunchBox itself reads back, and owns a versioned
vault (`Core\litebox\saves-vault.json`) with an md5 per entry. **Every save requirement in
this project is a thin adapter over it** — including the "only write back if untouched"
guard, which is exactly `TryComputeSaveSignature` / `FileMd5`.

---

## 3. The gaps — what does not exist yet

### 3.1 The HTTP core (blocking, shared by A and B)

`Host/Web/HttpRequest.cs` and `HttpResponse.cs` were written for a JSON+images theme server
and show it:

| Limit | Where | Why it blocks us |
|---|---|---|
| Request body capped at **64 KB**, decoded to `string` | `HttpRequest.TryRead`, `MaxBodyBytes` | a save is 32 KB–8 MB, a savestate 1–20 MB, both binary |
| No `multipart/form-data` parsing | — | every RomM asset upload is multipart |
| Response body is a fully buffered `byte[]` | `HttpResponse.Body` | a ROM download would load a 4 GB ISO into RAM |
| No `Range` / 206 on arbitrary files | only `MediaProxy` slices, by hand | resumable downloads; EmulatorJS asks for ranges |
| Methods hard-limited to GET/HEAD/POST | `EmbeddedWebServer.HandleClient` | RomM uses PUT and DELETE throughout |
| No chunked transfer-encoding (in or out) | — | an on-the-fly zip has no known length |

**This is slice S0** and it is worth doing on the shared core rather than forking a second
HTTP stack for RomM: B needs binary bodies too (an EmulatorJS savestate POST is megabytes),
and the existing theme surfaces get resumable media for free.

### 3.2 Identity

RomM is `int` everywhere: `rom.id`, `platform.id`, `file.id`, `save.id`. LaunchBox is GUID
strings. Clients persist those ints (Grout remembers which rom ids it has downloaded), so a
value that changes across restarts corrupts every client's local state. We need a **persistent
id ledger**, not a hash (§5.2).

### 3.3 Slugs

RomM keys platforms by IGDB-ish slug (`nes`, `snes`, `n64`, `psx`, `gba`, …) and the clients
map slug → their own emulator/core. LaunchBox platform names are free text
("Nintendo Entertainment System", "Nes - Super Mario Bros. Hacks"). We need a curated map.
`RaPlatformMap` is the exact precedent — same file shape, different target table. The RomM
side of the table can be lifted from `backend/utils/platforms.py` plus the EmulatorJS core map
in `frontend/src/utils/index.ts` (which doubles as B's core table, §6.3).

### 3.4 Users

LiteBox is single-user by construction. RomM's model is multi-user with roles. We ship **one
account** (configurable username/password) presented as an ADMIN user with a fixed id of 1;
`/api/users` returns that single row. Anything multi-user is out (§5.9).

### 3.5 No auth stack at all

No password hashing, no JWT, no token store. bcrypt is not referenced anywhere in the project
today — and we do **not** need to match RomM's bcrypt (nobody validates our hashes but us), so
PBKDF2 via `Rfc2898DeriveBytes` is the right call. JWT HS256 is ~40 lines with `HMACSHA256`
plus base64url. No third-party package.

---

## 4. Decisions taken

| Question | Decision |
|---|---|
| Clients | **Argosy, Grout, Playnite plugin.** Not the RomM Vue frontend (a Node build, Socket.IO, CSRF and sessions for zero gain — LiteBox already has two web themes). |
| Write scope | **Read + assets + game properties.** Clients may push saves/states/screenshots/last-played and may set favorite / rating / hidden / completion / status (`PUT /api/roms/{id}/user`). **No** ROM upload, **no** deletion, **no** platform mutation. |
| Hosting | **Its own `TcpListener` on its own port** (`[RommServer] Port`, default 8998). The database site already owns `/api/platforms`, `/api/games/{id}` and `/api/search` on the existing server — a shared mount would be a permanent collision, and every RomM client expects the API at the base URL's root. |
| Archives / multi-disc | **One LaunchBox game = one RomM rom.** Its playable entries — archive members, additional-app discs, m3u parts — become `files[]`, i.e. RomM *versions*, invisible as separate games. Downloads are served through **RomExtractor** in the background (listing from `ListEntriesDetailed`, extraction through the existing LRU / RAM-disk cache). |

---

## 5. Design — module A (`romm-server`)

### 5.1 Hosting and gating

New `LbModule.RommServer` (key `romm`), default **off**, catalogued in `LbModules.Catalog` so
it appears on Options → Modules. `Host/Romm/RommConfig.cs` snapshots a `[RommServer]` ini
section (`Port`, `AllowedIps`, `Username`, `PasswordHash`, `ExposeHiddenGames`,
`PlatformFilter`). A second listener, `Host/Romm/RommServer.cs`, mirrors
`EmbeddedWebServer`'s lifecycle exactly (idempotent `Start`, one task per connection,
allow-list IP filter, per-request concurrency gate) with its own router.

LAN exposure is the whole point here — unlike the theme server, this one is useless on
loopback. The allow-list therefore still defaults to empty (**loopback only**) and the options
page says plainly that opening it puts the library on the network behind one password.

### 5.2 Identity — the ledger

`Host/Romm/RommIdMap.cs`, persisted in `litebox-options.db` (global key/value scope, the same
store the modules use):

```
romm.platform.<lb-platform-name>  -> int
romm.rom.<lb-game-guid>           -> int
romm.file.<lb-game-guid>|<entry>  -> int
romm.save.<vault-entry-guid>      -> int
```

Monotonic counters per kind, allocated on first sight, **never reused**. A game removed from
the library keeps its id reserved so a re-added game does not inherit a client's stale state.
Deleted roms are reported through `missing_from_fs: true` rather than vanishing — that is
RomM's own convention and clients handle it.

### 5.3 Platform mapping

`Host/Romm/RommPlatformMap.cs`, same shape as `RaPlatformMap`: a hand-curated
`Dictionary<string,string>` from LB platform name → RomM slug, matched case- and
punctuation-insensitively, plus a per-platform user override in options. An unmapped platform
is simply not exported, and the options page lists the unmapped ones so the user can bind
them. Source tables: `romm/backend/utils/platforms.py` and the EJS core map (§6.3).

### 5.4 The library projection

`Host/Romm/RommLibrary.cs` — one file, the whole "LaunchBox → RomM" translation, layered on
`OwnedDataProvider` so it inherits parental filtering and the owned/installed logic:

- **Platform** → `PlatformSchema` (`id` from the ledger, `slug` from the map, `name`,
  `rom_count`, `logo_path` pointing at our media proxy).
- **Game** → `RomSchema`: `fs_name` from the ROM file name, `name`/`summary` from the LB
  fields, `path_cover_small` / `path_cover_large` served by `MediaProxy`,
  `regions` / `languages` / `tags` parsed from the filename tags the way LB already does,
  hashes filled **only when we already know them** — the RA module computes hashes for its own
  purposes; we do not hash 40 000 files to answer a listing (see §8.4 for the lazy path).
- **Files** → for a plain ROM, one entry. For an archive, `RomExtractor.ListEntriesDetailed`
  gives the scored playable entries, each becoming a `RomFileSchema` carrying its
  `archive_members`; for a multi-disc game, the additional-app versions become files and we
  synthesize the `.m3u` at download time exactly as RomM does. `has_multiple_files` follows.
- **rom_user** → favorite / rating / hidden / last-played read from the LB game, so Argosy
  shows the same flags BigBox does.

Pagination is `limit`/`offset` over the sorted projection with a cached snapshot invalidated
by the existing library-change events (`EventBus`), so a 40 000-game library does not rebuild
per request.

### 5.5 Download

`GET /api/roms/{id}/content/{file_name}?file_ids=…&hidden_folder=`:

1. Resolve the game and the requested file ids through the ledger.
2. **One file, plain ROM** → stream from disk with `Range` support (S0 gives us this).
3. **One file, inside an archive** → `RomExtractor` extracts that entry into its existing LRU
   cache and we stream the extracted file. The cache means a second client, or a resumed
   download, costs nothing. Cleanup stays RomExtractor's business.
4. **Several files** → stream a zip built on the fly (store, not deflate — ROMs are already
   compressed) containing each file plus a generated `.m3u`, honouring `hidden_folder`.

`HEAD` answers the same headers without a body — Grout uses it to size a download.

### 5.6 Assets ↔ SaveManager

The adapter is `Host/Romm/RommAssets.cs`:

| RomM | LiteBox |
|---|---|
| `GET /api/saves?rom_id=` | `SaveManager.ScanBase(game)` groups + `SaveVault.ForGame(id)` versions, flattened to `SaveSchema[]` (ledger ids, `content_hash` = the vault md5, `emulator` = the LB emulator title) |
| `GET /api/saves/{id}/content` | stream the active file or the vault copy |
| `POST /api/saves` (multipart) | `SaveManager.Backup` the current state first (never overwrite blind), then `SaveManager.Import(game, plugin, tmpFile, asState:false, slot, …)` — the same path Edit Game → Game Saves uses, so LaunchBox sees it too |
| `PUT /api/saves/{id}` | rename / relabel the vault entry |
| `POST /api/saves/delete` | `SaveManager.DeleteBackup` (vault only — never the live save) |
| states | same, `asState: true` |
| screenshots | stored beside the vault entry; RomM's screenshot is just an attached image |
| devices + `device_syncs` | a small table in options: device id → last synced md5 per save. `is_current` = the device's recorded md5 equals the current one. That is all Grout needs to decide push vs pull. |

**Format honesty.** A save uploaded by a handheld running RetroArch is a libretro `.srm`; the
desktop group it lands in may belong to a standalone emulator with a different format.
`SaveManager` already knows which `EmulatorPlugin` owns a group, so the import proceeds when
the target is libretro-compatible and otherwise **lands in the vault as a version without
touching the live save**, with the reason reported. Silent corruption of a real save file is
the one outcome this feature must never produce.

### 5.7 Auth

`Host/Romm/RommAuth.cs`:

- One account. Password stored as PBKDF2-SHA256 (100k iterations, per-install salt) in the
  options DB; set from the options page, never in the ini in clear.
- `Authorization: Basic` verified against it.
- `POST /api/token` → JWT HS256 access (30 min) + refresh (7 days), signing key generated per
  install and kept in the options DB. `MediaTokenSecret.cs` is the existing precedent for
  "a secret LiteBox generates and keeps".
- `Bearer rmm_<hex>` client tokens: created / listed / revoked from the options page and via
  `/api/client-tokens`, stored as SHA-256, with the pair-code flow (`/pair`,
  `/pair/{code}/status`, `/exchange`) since that is how a handheld enrolls without typing a
  password.
- Sessions / CSRF / OIDC: **not implemented** — no browser client is in scope, and both are
  skipped upstream whenever a Bearer or Basic header is present.

Every route is scope-checked against the single ADMIN account, so the checks are trivial but
real (a client asking `me.read` gets a truthful answer from `/api/users/me`).

### 5.8 Write scope

Allowed: `PUT /api/roms/{id}/user` (favorite, rating, hidden, completion, status, backlogged,
now_playing, last_played), asset POST/PUT/DELETE, device registration, and collection
membership if we expose playlists as collections. Everything else that mutates answers **403
with a RomM-shaped error body** rather than pretending to succeed — a client that thinks it
deleted a ROM and did not is worse than one told no.

### 5.6bis What changed when the save layer was rebuilt

This module was written against a save layer that has since been replaced, and the parts that
moved are worth naming because each was a wrong assumption, not a refactor:

- **`SaveVault.All()` is gone, and there is no replacement.** It read a JSON index of the vault
  folder. There is no index: a copy exists for LaunchBox only because a `<GameSave>` record
  describes it, and those records belong to a game. `RommAssetsApi.VaultEntriesOf(game)` reads
  them, and the bare listing walks the library to do it — cheap, since no plugin is scanned.
- **A vault asset key now carries its game id** (`vault|{gameId}|{relPath}`). It has to: a bare
  path is no longer resolvable against anything. Keys issued by the old build read as gone.
- **`SaveManager.Import` no longer writes a live save.** It lands a version in the vault and
  records it; `SaveManager.Restore` is the only path that writes a live file, and it goes
  through the emulator's own plugin. So an upload does both, in that order, and the version it
  makes is adopted into the group it belongs to so a nightly push does not leave one orphan
  group per night on the game's save page.
- **Dates reported to clients are `DisplayCreatedUtc`, not `CreatedUtc`.** A padlocked copy
  carries its creation date a century ahead on purpose; a device told a save was made in 2126
  would hold it newest for ever and never pull again.

### 5.6ter The save contract — settled 2026-08-31, implemented the same day

Settled over a long session, then implemented and verified against the three clients' sources
(argosy-launcher, grout and Freegosy checkouts). What follows is the contract as SHIPPED.

**Slot naming — one channel per group, never per copy.** A group is a line the emulator keeps
writing to; a vault copy is frozen history that retention evicts, and a slot pointing at one would
vanish under a client that had pinned it. Per REQUESTING client:

| slot | what it is |
|---|---|
| `autosave` | the requester's own branch — the only place its pushes land. With no branch yet, the game's primary LiteBox line stands in (real id, real file name), so the first pull→play→push round trip never leaves the default channel. |
| `romm-cN` | another client's branch, a read-only extra (a deliberate reversal of the old hiding — with named slots the choice now has a meaning). |
| `lb-ra-<core>` / `lb-<emu>` | a LiteBox group. Inactive groups are served too (their line lives in the vault); when several groups answer to one name — Make New Save builds a second In-Vault group on the same core — the one in play wins, else the most recently written, and the trace names the ones set aside. |

No entry names in slots: with extraction off the archive IS the ROM (one main-bucket group), with
extraction on the `rom_id` already narrowed the listing to one entry — either way the component is
constant, hence mute. Character set: no `:` (illegal in a Windows file name), no `.` (Argosy cuts
channel names at the last one), no `#` (an unencoded `?slot=` truncates at the fragment), `@` avoided
as a precaution. Slots are not machine-parseable and need not be: `emulator` carries the core verbatim.

**The wire file name follows the slot — except on autosave.** Argosy seeds its slot table from the
file name alone (`parseServerChannelNameForSync` never reads `slot`), so named lines must be
channel-named. The autosave channel carries the file's REAL name instead: a name equal to the ROM's
base name is precisely what says "the latest save" to Argosy, and it is the name Freegosy writes to
disk verbatim, where only the real one lets the emulator find the save.

**Per-client view.** A slot-blind client (Freegosy: takes the newest of whatever it is shown) is
recognised by its Dart User-Agent and served ONLY the autosave line — for it a multi-line view would
not read as a choice but as the truth. Unknown agents get the full view.

**Origin, not activity.** `#cN` on a group id IS the origin marker; a promoted branch stays
`romm-cN`/`autosave`, which makes `ReassignRecord` keeping the suffix correct rather than a bug.

**Write rules (the push, reopened).** A push lands in the pusher's branch — always. The announced
slot never picks the target (a client that restored from `lb-ra-snes9x` pushes under that name, and a
refusal would be retried in a loop); the guarantee is held by our targeting, never by refusing. The
live save is written ONLY when the user promoted that client's branch in Game Saves — overwriting the
game in play is a per-game permission granted by promoting, and it does not exist by default. Even
promoted: strictly newer only (a phone clock gone backwards cannot put an old game back in play — the
copy is still filed), the displaced save is secured FIRST and a failed net stops the act
(`PreserveBeforeOverwrite` returns bool), and the incoming copy is archived labelled even when it goes
live — it is what the user inspects before trusting a client further. No paired client → 422. PUT
accepts only the requester's own channel. The per-client push mode is gone (UI, plumbing; the
`push_mode` column stays in the schema, unread).

**Negotiate (implemented — Grout REQUIRES it).** `POST /api/sync/negotiate` +
`POST /api/sync/sessions/{id}/complete`. The client sends its whole inventory (saves only); the
server answers one op per save — `upload|download|conflict|no_op` with a reason — in a numbered,
DB-persisted session (a complete answered 404 makes Argosy drop local rows; a LiteBox restart must
not cause that). Decisions are permissive by design: nothing a client uploads can overwrite a live
save, so doubt costs one vault copy, not progress. The data is weak and treated as such —
`content_hash` is the client's LAST-UPLOAD hash, `updated_at` its clock; hash equality means "in
sync", inequality decides nothing alone, and `conflict` is reserved for the one honest case (both
sides moved since THIS device's last sync mark). No downloads volunteered beyond the inventory:
Grout's discovery fallback queries `/api/saves` for ROMs with no local save, by its own design.
`/api/saves/summary` (per-slot digest) is served for Grout's menus.

**The three clients, verified against their sources:**

| | slots | restore naming | negotiate | verdict |
|---|---|---|---|---|
| Argosy | yes (seeding path reads file names — covered by channel-named files) | computes its own paths | optional; empty plan on error | works |
| Grout | yes, cleanest | ROM basename + server extension | **mandatory, no fallback** | works now that negotiate exists |
| Freegosy | none — newest of the list, writes served names verbatim | served name, verbatim | never calls it | works via the single-line autosave view |

**Known leftovers, deliberate:**

- `/api/roms/{id}` embedded saves now carry the rom row and the token (they used to cover the whole
  game and filter the requester's own branch, contradicting `/api/saves`).
- The seed's asset id changes when a client's first push creates its branch; device sync marks follow
  the POST answer, so `is_current` stays coherent.
- A slot name can flip lines when the user activates a different group of the same core — the name is
  "that core's line as of now", accepted; the trace records set-aside groups if it ever matters.
- Freegosy's `pruneOldSaves` calls the (closed) bulk delete → 501, swallowed by the client; its
  `autocleanup` query param is ignored — our vault retention (cap 25, locked copies never evicted) is
  the retention.

### 5.9 Deliberately not implemented

Scanning and metadata providers (`/api/search`, `/api/tasks`, all `METADATA_SOURCES` flags
reported `false`), feeds (Tinfoil / PKGi / WebRcade / Kekatsu), netplay, Socket.IO `/ws`,
OIDC, multi-user, invite links, firmware CRUD (BIOS listing may come later), the music API,
export (gamelist.xml / Pegasus), ROM upload, streaming. Each answers 404 or 501, and
`/api/heartbeat` advertises them off so a well-behaved client never asks.

---

## 6. Design — module B (EmulatorJS in BigBox Web) — **WITHDRAWN**

> **This half was built and then removed (2026-08-28).** The effort goes to server-side RomM
> emulation instead, which is the part with clients that exist. What was deleted: `WebPlayApi`
> and its seven routes, `RommEjsCores`, `web-assets/bigbox/play/index.html`, the *Play in Browser*
> detail-menu entry and its `engine/app.js` handler. It never had its browser pass, so nothing
> measured is being thrown away — only code.
>
> The section stays because §6.2–6.5 are a read of RomM's own `Player.vue`, and that recon is the
> expensive part. Restoring the feature means writing the page again from this design, not
> rediscovering how EmulatorJS behaves.
>
> The `EJS_*` keys in `RommApi` are **not** part of this and stay: they belong to the RomM API
> contract, which RomM's own frontend reads.

Not a module: it belongs to the existing Web module's BigBox surface, gated to **non-kiosk**
only. `WebKioskWindow` marks the kiosk client and `WebSelectionBridge` already distinguishes
them; the kiosk is a controlled TV surface where an in-browser emulator has no business.

### 6.1 Where it plugs in

BigBox Web's detail view already has a play action posting to
`/bigbox/api/games/{id}/play` → `HostLaunch.Launch("web", …)`, which launches on the *host*.
We add a sibling verb and a player page:

- `GET /bigbox/data/games/{id}/webplay.json` → `{ supported, cores[], defaultCore, files[], saves[], states[] }`
- `GET /bigbox/play/{id}` → the player page (its own small HTML/JS under `web-assets/bigbox/play/`)
- `GET /bigbox/api/games/{id}/rom?file=…` → the ROM bytes, via RomExtractor (same path as §5.5)
- `POST /bigbox/api/games/{id}/save` and `/state` → the write-back (§6.4)

The detail page shows "Play in browser" only when `supported` is true, so unsupported
platforms are unchanged.

### 6.2 EmulatorJS hosting

The same strategy RomM uses: `EJS_pathtodata` points at a **local** copy first
(`Core\litebox\web\vendor\emulatorjs\data`, shipped through the existing `web-assets` pipeline
when present) and falls back to `https://cdn.emulatorjs.org/4.2.3/data` on load failure. Local
is optional — the CDN path means the feature works out of the box, the local path means it
works offline.

**The SharedArrayBuffer caveat, stated up front.** Threaded cores (`dosbox_pure`, `ppsspp`,
`azahar`) need `SharedArrayBuffer`, which needs a *secure context* plus COOP/COEP headers.
`http://127.0.0.1` is a secure context; `http://192.168.x.x` is **not**. So: on the host
machine everything works once we send `Cross-Origin-Opener-Policy: same-origin` and
`Cross-Origin-Embedder-Policy: require-corp` on the player page (plus
`Cross-Origin-Resource-Policy` on the ROM and asset responses); over LAN without TLS the
threaded cores are unavailable and the player must say so rather than hang on a black screen.
The non-threaded cores — which is most of the list — are fine either way.

### 6.3 Core mapping

`_EJS_CORES_MAP` in `romm/frontend/src/utils/index.ts` is ~90 slugs → core lists, keyed by the
*same slugs* as §5.3. One table serves both deliverables: LB platform name → RomM slug → EJS
cores. It lives in `Host/Romm/RommPlatformMap.cs` and is emitted as JSON for the theme JS, so
the browser does not carry a second copy of the truth.

### 6.4 Saves — import, export, and the untouched guard

This is the requirement with teeth.

**On launch (import).** `SaveManager.ScanBase(game)` finds the live save groups. The chosen
group's file is served to the player, which writes it into EmulatorJS's FS at
`gameManager.getSaveFilePath()` then calls `loadSaveFiles()` — exactly what RomM's
`loadEmulatorJSSave` does. Along with the bytes we hand the client an **origin token**:
`{ groupId, md5, mtimeUtc, size }` of the file as we read it.

**On save (export).** `EJS_onSaveSave` posts the SRAM back with its origin token. The host:

1. Recomputes the live file's md5 (`SaveManager.FileMd5`, already there).
2. **Token matches** → the original has not been touched since we handed it out → back it up
   to the vault (a labelled version, so the pre-web state is always recoverable), then write
   the new bytes through `SaveManager.Import`; LaunchBox and BigBox see the update.
3. **Token does not match** → someone played on the desktop meanwhile. We do **not**
   overwrite. The upload lands in the vault as a version labelled "from browser, &lt;date&gt;",
   and the player is told the desktop save moved on. Restoring it is one click on the existing
   Game Saves page.

Savestates get the same round trip with one difference stated plainly in the UI: **a savestate
is core- and version-specific.** An EmulatorJS `snes9x` state is not interchangeable with a
desktop standalone Snes9x state, and often not even with a different RetroArch core build. So
web states live in their **own namespace** in the vault (marked `web`), are offered back to the
web player, and are never written over a desktop state file. SRAM saves *are* interchangeable
for libretro-backed groups, which is why they get the full write-back path and states do not.

### 6.5 What the player page is

A small self-contained page in the BigBox theme's own idiom (no framework, matching
`web-assets/bigbox/engine/`): cover + title, save/state pickers fed by `webplay.json`, a core
picker when a platform has several, then the EmulatorJS canvas. The EJS wiring — `EJS_core`,
`EJS_gameUrl`, `EJS_biosUrl`, `EJS_onSaveSave`, `EJS_onLoadSave`, `EJS_onSaveState`,
`EJS_defaultOptions["save-state-location"] = "browser"`, and the `waitForGameManager` poll
before injecting a save — is a direct read of
`romm/frontend/src/views/Player/EmulatorJS/Player.vue`. That file is the best documentation of
EmulatorJS's real behaviour that exists; its gameManager-poll and `STATE_APPLY_SETTLE_MS`
lessons are worth copying rather than re-learning.

---

## 7. Staged plan

Each slice ends somewhere demonstrable.

| # | Slice | Ends with | Rough size |
|---|---|---|---|
| **S0** | **HTTP core**: streamed request bodies (spilling to disk past a threshold), a multipart/form-data parser, streamed responses (`HttpResponse.FromFile` + chunked), `Range`/206 on any file response, PUT/DELETE/OPTIONS admitted | existing theme surfaces unchanged, media served with working `Range` | ~900 l |
| **S1** | Module registration + second listener + `[RommServer]` config + options page (port, LAN, account, tokens) | server starts/stops from Options, answers `/api/heartbeat` | ~700 l |
| **S2** | Auth: PBKDF2 account, Basic, `/api/token` JWT, client tokens + pair flow, `/api/users/me`, scope checks | `curl -u` and a Bearer token both work end to end | ~700 l |
| **S3** | Id ledger + platform map + `/api/platforms` + `/api/roms` paginated + covers | **the Playnite plugin lists the library** | ~1200 l |
| **S4** | `files[]` projection via RomExtractor + `/api/roms/{id}/content/{file}` (single, archive entry, multi-file zip + m3u) + HEAD | **Grout downloads and runs a game** | ~900 l |
| **S5** | Saves / states / screenshots over SaveManager + devices + sync verbs | **Argosy round-trips a save** | ~1100 l |
| **S6** | `PUT /api/roms/{id}/user` write-backs + collections from playlists | a favorite set on the phone shows in BigBox | ~400 l |
| **S7** | BigBox Web: `webplay.json`, ROM serving, core map, player page, EmulatorJS loader + COOP/COEP | **a game runs in the browser**, no saves yet | ~900 l |
| **S8** | Save/state import + origin-token write-back + vault versioning + the "desktop moved on" path | the full requirement | ~700 l |
| **S9** | Deploy/release wiring (the four deploy lists, `web-assets` additions), docs, options recap entry | shippable | ~200 l |

S0 → S6 is module A; S7 → S8 is B and depends only on S0. They can be interleaved, and S7/S8
are the smaller, more visible half — worth doing early if you want something to look at.

### Progress

| # | State | Proof |
|---|---|---|
| S0 | **done** | `LiteBox.exe --selftest-http` — 15/15 |
| S1 | **done** | `LiteBox.exe --selftest-romm` |
| S2 | **done** | `--selftest-romm` (auth section) |
| S3 | **done** | `--selftest-romm` (ledger + map + library sections) |
| S4 | **done** | code + route check; **needs a live-library pass** (Grout, muOS `hidden_folder`) |
| S5 | **done** | `--selftest-romm` (devices + assets sections) — save round-trip **needs a live-library pass** |
| S6 | **done** | `--selftest-romm` (collections + rom_user checks) |
| S7 | **removed** | built, never given its browser pass, then withdrawn — see §6 |
| S8 | **removed** | idem: the origin-token guard went with `WebPlayApi` |
| S9 | mostly | the four deploy lists no longer name the player page; EmulatorJS local copy moot |

`--selftest-romm` totals **110 checks**, `--selftest-http` **17**, `--selftest-entry-saves` **59**,
all green.

Landed: `Host/Web/{HttpRequest,HttpResponse,HttpHost,MultipartReader,HttpSelfTest}.cs`,
`Host/Romm/{RommConfig,RommServer,RommApi,RommAuth,RommAuthApi,RommIdMap,RommPlatformMap,RommLibrary,
RommLibraryApi,RommDownloadApi,RommDevices,RommDevicesApi,RommAssetsApi,RommUserApi,
RommSelfTest}.cs`, `Host/Options/Modules/RommPanel.cs`, `RomExtractor.ExtractEntryForDownload`,
the `LbModule.RommServer` catalog entry, eight `Romm.*` rows in `OptionKeys.All`, the HostBoot
start block and the ModulesOptions tab + reconcile.

Withdrawn with module B: `RommEjsCores`, `Host/Web/Theme/WebPlayApi.cs`,
`web-assets/bigbox/play/index.html`, the `webplay` detail-menu entry (server + `engine/app.js`
dispatch). Also gone, but for a different reason: `SaveVault.All()` — see §5.6.

Things worth knowing:

* `EmbeddedWebServer` no longer owns its socket loop — that moved to `HttpHost`, shared by both
  surfaces. Its `Intercept` hook is what makes a CORS preflight succeed on a refused path.
* Every new `litebox-options.db` key must be declared in `OptionKeys.All` first or the write is
  silently refused at runtime (one log line). This bit twice during this build.
* The COOP/COEP headers on the player page are stamped ONLY when a local EmulatorJS copy exists
  (`webendor\emulatorjs\data\loader.js`): `require-corp` would otherwise break the CDN fallback
  for every core to maybe enable the two threaded ones.
* RomM's "favorite" is NOT a rom_user field — it is membership of the `is_favorite` collection.
  `RommUserApi` wires that collection to `IGame.Favorite`, which is what makes the phone's heart
  show in BigBox.

What no self-test can cover (needs a real install + clients):
1. Playnite plugin listing, Argosy save round-trip, Grout download + `hidden_folder` zip layout.
2. The browser player end-to-end (EJS boot, save import, origin-token write-back both branches).
3. Cover volume behaviour under a scrolling client (MediaProxy `q=thumb` path).
4. Archive-extraction budget under bulk download (risk #3) — deliberately NOT implemented yet.

---

## 8. Risks and open points

1. **RomM version drift.** We pin the contract to the checked-out `romm/` tree. Clients
   negotiate on `SYSTEM.VERSION`, so we report a real RomM version — the one we match — and
   this doc should record which upstream commit that was. When RomM moves, re-read the diff.
2. **Cover art volume.** Argosy fetches a cover per row while scrolling. `ThumbCache` handles
   this today for the themes; the RomM listener must reuse it rather than growing a second
   cache.
3. **Archive extraction on download.** A client bulk-downloading a platform would extract the
   whole platform through the LRU cache. Needs a cap — a per-request extraction budget with a
   "serve the archive as-is" fallback beyond it. Decided in S4, not before.
4. **Hash fields.** Grout matches saves by ROM hash on some setups. We can fill `md5_hash` /
   `crc_hash` lazily — hash on first download, remember in the ledger — rather than never or
   eagerly.
5. **Multi-disc naming.** muOS's `hidden_folder` convention changes the file names inside the
   generated zip. Worth testing against a real Grout install before calling S4 done.
6. **The LAN password is the only wall.** No TLS. The options page must say so, and the
   allow-list must stay opt-in and default-empty.
