# LiteBox

A lightweight **host that implements the LaunchBox plugin API**
(`Unbroken.LaunchBox.Plugins`) so LaunchBox plugins (e.g. ExtendDB and its web
subsystem) can run **without the full LaunchBox.exe** — for a low-RAM web/kiosk
setup. LiteBox loads all user data from LaunchBox's native XMLs and exposes it
through its own implementation of the API, with no dependency on any plugin.

## What it does

- Loads the whole LaunchBox library from `Data\Platforms\*.xml` (games, platforms,
  categories, emulators, playlists incl. auto-populate filters, additional
  applications, alternate names) into a memory-frugal two-tier store.
- Resolves media (images/videos) the LaunchBox way — on-demand `Directory`
  enumeration — with a fast in-memory cache path:
  - delegates to **ExtendDB's `GameCache`** when that plugin is loaded and ready;
  - otherwise builds its **own backported GameCache** (config-gated, optionally
    backed by Voidtools **Everything** for instant file enumeration; falls back to
    `Directory` when Everything isn't available).
- Hosts LaunchBox plugins: injects `PluginHelper` services, fires
  `PluginInitialized`, exposes system/game menus, and launches games (emulator or
  direct) through `IGameLaunchingPlugin`.
- **WinForms GUI**, dark themed, 3-pane LaunchBox-like layout — **all native
  controls, no external UI library**:
  - source tree (`TreeView`): All Games / categories ▸ platforms / playlists;
  - game list (`GameListView`, a native virtual `ListView`): sortable, searchable,
    column show/hide (right-click the header), drag-reorder, all persisted;
  - **poster (grid) view** toggle — owner-drawn box-art tiles, centred;
  - details pane: clear logo + box/screenshot carousel, an expandable meta card,
    VNDB tag pills, and notes.
- Frees RAM while a game runs (drops the optional data tier + trims the working
  set, optionally drops the host GameCache) and can show a "game running" screen.
  The `GameSave` sub-object is kept resident through the launch (a save-sync plugin may
  read/write it while the game runs); the display-only optional data is dropped.
- Optional write-back: with `ReadOnly=false`, plugin/UI changes persist to the Platform
  XMLs via an append-only operation log (`Core\LiteBox.pending.db`, SQLite), applied at a
  safe time (`Save()`, close, or next boot) — only when LaunchBox/BigBox aren't running,
  since they own the XMLs while alive. Full `IDataManager` write parity — every entity
  type can be edited, added and removed:
  - **Games** — any modelled `IGame` field; add (`AddNewGame`, creating a new
    `Platforms\<name>.xml` if needed) / delete; move across platforms (relocates the
    `<Game>` node between files); child entities (additional applications/discs, custom
    fields, mounts, alternate names) add/remove/edit.
  - **Emulators** — every `IEmulator` field + per-platform command lines
    (`EmulatorPlatform`); add / delete.
  - **Platforms & platform categories** — every field; add / delete.
  - **Playlists** — fields, member games (`PlaylistGame`), auto-populate rules
    (`PlaylistFilter`); add (new file) / delete (whole file).

  Edits are surgical (only the touched nodes change; unknown/unmodelled fields are
  preserved), validated against LaunchBox (it ingests the rewritten files without
  complaint), and crash-safe (idempotent replay; the log is cleared only after every file
  is durably swapped). Before each write the pristine originals of just the affected files
  are zipped to `<LB>\Backups\LiteBox\` (sub-paths preserved; 50 most recent kept) — a
  targeted alternative to LB's full Data backups.
- Beyond the SDK: the official interfaces (`IGame`/`IEmulator`/`IPlatform`/…) expose only a subset
  of each entity's XML. Since LiteBox owns the data layer it doesn't bridle plugins to that subset —
  **every** host entity (game, emulator, emulator-platform, platform, category, playlist) implements
  the public `ILiteBoxFields { GetField / SetField / ExtraFieldNames }`, giving read/write access to
  every field LB writes (a game's GogAppId/Origin/Android/Missing/RetroAchievements; an emulator's
  UsePauseScreen/SkipVersionCheck/LoginToCheevoOnGameLaunch; etc.). Use it typed (LiteBox-native
  plugin) or by reflection on the public method names (a cross-LB plugin like ExtendDB).
  Games also expose their per-game **sub-objects** that LB writes as separate elements and the SDK
  hides entirely — `ModelSettings` (3D box/cart display override, ~14 options), `GameControllerSupport`,
  `GameSave`, and any future type — via `ILiteBoxGame.SubEntityTypes / GetSubEntities / SetSubEntities`
  (captured generically, round-tripped, and preserved on any write). `GameSave` is treated as
  resident (Tier-1) so it stays available during a game launch; the display-only ones
  (`ModelSettings`, `GameControllerSupport`) ride the droppable optional tier.

## Requirements

- A modern **.NET 9** LaunchBox install (the one with a self-contained flat
  runtime in `<LaunchBox>\Core`). LiteBox shares that runtime.
- `LiteBox.exe` **must live in `<LaunchBox>\Core`** (its paths key off the exe
  location; it switches the working directory to the LB root at startup).

## Build

Open with the .NET 9 SDK and `dotnet build -c Release`. The project references
`Unbroken.LaunchBox.Plugins.dll` and `Magick.NET` at compile time only
(`Private=false`) — adjust the `HintPath`s in `LiteBox.csproj` to your install.
Those assemblies are resolved at runtime from `Core` / the loaded ExtendDB copy,
so LiteBox is **version-agnostic** across LaunchBox versions. There is **no
external UI package** (the former ObjectListView dependency was removed; the game
list and source tree are native WinForms controls).

## Deploy

Copy these files into `<LaunchBox>\Core` (everything else is provided by
LaunchBox's runtime already there). This is exactly what the release zip ships:

Required (the app):

- `LiteBox.exe`
- `LiteBox.dll`
- `LiteBox.deps.json`
- `LiteBox.runtimeconfig.json`

Optional (extra standalone capabilities):

- `Magick.NET-Q16-AnyCPU.dll`, `Magick.NET.Core.dll`,
  `Magick.Native-Q16-x64.dll.api` — decode WebP clear logos that GDI+ can't.
  Absent these (and ExtendDB), WebP logos simply don't render.
- `Everything64.dll.api` — instant media enumeration for the host GameCache.
  Auto-deploys to `<LaunchBox>\ThirdParty\Everything\Everything64.dll` on first
  run; absent it (or the Everything service), the cache falls back to `Directory`.

Optional: a `whitelist.txt` in `Core` (one plugin folder name per line, subfolders
of `<LaunchBox>\Plugins`) to activate plugins; without it LiteBox runs standalone.
`LiteBox.ini` (config) is auto-created next to the exe on first run.

## Run

```
LiteBox.exe                 GUI (default)
LiteBox.exe --headless      diagnostics (--playlists --mediatest --gcdump --drop --play --drylaunch)
LiteBox.exe --plugins <dir> override the plugins root
LiteBox.exe --library <dir> override the Platforms XML directory
LiteBox.exe --selftest-writeback  round-trips the write-back op-log on temp files (no live data)
```

## Options (gear menu / `LiteBox.ini`)

- **ReadOnly** (default **true**): never write to the LaunchBox XMLs; edits stay in
  memory for the session. Set false to persist them via the operation log (see
  write-back above) — applied to the XMLs only when LB/BigBox aren't running.
  **Second instance:** if another LiteBox is already running, the new instance is forced
  read-only for the session (only one instance may own the XMLs / op-log). The change is
  in-memory only — `LiteBox.ini` is left untouched — and the window makes it explicit: a
  warning-coloured caption (Win11) + a banner, and the options menu is locked.
- **Show "game running" screen**, **Unload the list while a game runs**, **Use the
  image cache** (degraded thumbnails).
- **Use game cache** (when ExtendDB is absent) + **Unload game cache during game**.
- **Use 16:9 for the main media** (else poster ratio).

Pane widths, column layout (width / visibility / order), sort, selection and the
list/poster mode are all remembered between runs.
