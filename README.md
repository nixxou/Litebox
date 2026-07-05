# LiteBox

A lightweight **host that implements the LaunchBox plugin API**
(`Unbroken.LaunchBox.Plugins`) so LaunchBox plugins (e.g. ExtendDB and its web
subsystem) can run **without the full LaunchBox.exe** — a low-RAM alternative
front-end for a web/kiosk or desktop setup. LiteBox loads all user data from
LaunchBox's native XMLs and exposes it through its own implementation of the API,
with no dependency on any plugin. Where it helps, it also **drives LaunchBox's own
obfuscated integrations unmodified** — the emulator-integration plugins and the
GOG/Steam/Epic store hooks — so those features work without reimplementing them.

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
    VNDB tag pills, notes, launch buttons and (when available) a **RetroAchievements
    card** with the game's badge set and completion progress;
  - a per-game **Edit** window (context menu) for metadata, notes, custom fields and
    a **Game Saves** page.
- Frees RAM while a game runs (drops the optional data tier + trims the working
  set, optionally drops the host GameCache) and can show a "game running" screen.
  The `GameSave` sub-object is kept resident through the launch (a save-sync plugin may
  read/write it while the game runs); the display-only optional data is dropped.

### Runs LaunchBox's emulator-integration plugins — unmodified

LiteBox drives the **official emulator-integration plugins** (RetroArch, Dolphin,
PCSX2, MAME, ScummVM, BigPEmu, Xemu) — the very same obfuscated DLLs LaunchBox
ships, run **untouched**. LiteBox only implements the calling side of their contract:

- **Install an emulator from LiteBox** — in the Add-Emulator window, a **Download**
  button (shown when a matching plugin exists) fetches and sets up the emulator and
  registers its platforms, exactly as LaunchBox's own "download emulator" does.
- **Per-game save management** — the **Game Saves** page drives the plugin to
  discover, back up and restore saves (validated on RetroArch, Dolphin, PCSX2);
  records are written as LaunchBox-compatible `<GameSave>` nodes.
- **RetroAchievements at launch** — when enabled on the emulator, LiteBox hands the
  plugin the user's RetroAchievements credentials so the emulator logs in and
  achievements track (RetroArch, via an appended config).
- **Command-line normalisation** — the plugin's per-executable fixups are applied
  before each launch.

### Store games (GOG / Steam / Epic / Ubisoft)

Store-tied games get proper **Install / Play** buttons that use the store's own URIs
(`goggalaxy://`, `steam://`, `com.epicgames.launcher://`, `uplay://`), a background
**installed-state sync** that reads each client's manifests to keep the library's
installed flag current, and — for GOG and Steam — an **achievements card**.

### Write-back (optional)

With `ReadOnly=false`, plugin/UI changes persist to the Platform XMLs via an
append-only operation log (`Core\LiteBox.pending.db`, SQLite), applied at a safe time
(`Save()`, close, or next boot) — only when LaunchBox/BigBox aren't running, since
they own the XMLs while alive. Full `IDataManager` write parity — every entity type
can be edited, added and removed:

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

### Beyond the SDK — full-fidelity fields

The official interfaces (`IGame`/`IEmulator`/`IPlatform`/…) expose only a subset of each
entity's XML. Since LiteBox owns the data layer it doesn't bridle plugins to that subset —
**every** host entity (game, emulator, emulator-platform, platform, category, playlist)
implements the public `ILiteBoxFields { GetField / SetField / ExtraFieldNames }`, giving
read/write access to every field LB writes (a game's GogAppId/Origin/Android/Missing/
RetroAchievements; an emulator's UsePauseScreen/SkipVersionCheck/LoginToCheevoOnGameLaunch;
etc.). Use it typed (LiteBox-native plugin) or by reflection on the public method names (a
cross-LB plugin like ExtendDB). Games also expose their per-game **sub-objects** that LB
writes as separate elements and the SDK hides entirely — `ModelSettings` (3D box/cart
display override), `GameControllerSupport`, `GameSave`, and any future type — via
`ILiteBoxGame.SubEntityTypes / GetSubEntities / SetSubEntities` (captured generically,
round-tripped, and preserved on any write). `GameSave` is treated as resident (Tier-1) so
it stays available during a game launch; the display-only ones (`ModelSettings`,
`GameControllerSupport`) ride the droppable optional tier.

## Requirements

- A LaunchBox install with the self-contained flat runtime in `<LaunchBox>\Core`.
  Both runtimes are supported — **.NET 9** (LaunchBox 13.27) and **.NET 10**
  (LaunchBox 13.28+); LiteBox borrows whichever one `Core` provides.
- `LiteBox.exe` **must live in `<LaunchBox>\Core`** (its paths key off the exe
  location; it switches the working directory to the LB root at startup).

## Build

Requires the **.NET 10 SDK** (the project targets both `net9.0-windows` and
`net10.0-windows`). A plain `dotnet build -c Release` compiles it. The project
references `Unbroken.LaunchBox.Plugins.dll` and `Magick.NET` at compile time only
(`Private=false`), so LiteBox stays **version-agnostic** across LaunchBox versions;
adjust the LaunchBox reference roots in `LiteBox.csproj` to your installs. There is
**no external UI package** (the game list and source tree are native WinForms
controls).

To produce the shippable artifacts, run **`build-release.bat`** (double-click) or
`build-release.ps1` — see **BUILD-RELEASE.md** for the full explanation. It writes to
`release\`:

- `LiteBox-Setup-<ver>.exe` — one universal installer (works on 13.27 net9 and
  13.28+ net10);
- `light\LiteBox-<ver>-net9.zip` and `-net10.zip` — the manual "extract into Core"
  alternative for each runtime.

> The **host is multi-file** by necessity: LaunchBox's core assemblies use a
> method-body-encryption obfuscator whose native reads crash under a .NET single-file
> bundle. So a single-file exe can only be the **installer**, never the host (see
> BUILD-RELEASE.md).

## Deploy

Easiest: run **`LiteBox-Setup-<ver>.exe`** — it detects `Core`'s .NET, extracts the
matching build into `<LaunchBox>\Core`, deploys the native helpers and launches.

Manual: copy these files into `<LaunchBox>\Core` (everything else is provided by
LaunchBox's runtime already there) — exactly what each light zip ships:

Required (the app):

- `LiteBox.exe`
- `LiteBox.dll`
- `LiteBox.deps.json`
- `LiteBox.runtimeconfig.json`

Optional (extra standalone capabilities, under `<LaunchBox>\ThirdParty` / `Core`):

- `Magick.NET-Q16-AnyCPU.dll`, `Magick.NET.Core.dll`,
  `Magick.Native-Q16-x64.dll.api` — decode WebP clear logos that GDI+ can't.
  Absent these (and ExtendDB), WebP logos simply don't render.
- `Everything64.dll.api` — instant media enumeration for the host GameCache.
  Auto-deploys to `<LaunchBox>\ThirdParty\Everything\Everything64.dll` on first
  run; absent it (or the Everything service), the cache falls back to `Directory`.

Plugins are chosen in **Options ▸ Plugins** (a checklist of the subfolders of
`<LaunchBox>\Plugins`); the selection is saved to `LiteBox.ini` and applied at the
next restart. Until you change it, LiteBox enables every plugin folder it finds (a
DLL in an immediate subfolder is enough — e.g. `Plugins\ExtendDB\ExtendDB.dll`).
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

- **ReadOnly** (default **false**): when **true**, never write to the LaunchBox XMLs;
  edits stay in memory for the session. Left false (default) they persist via the
  operation log (see write-back above) — applied to the XMLs only when LB/BigBox
  aren't running.
  **Second instance:** if another LiteBox is already running, the new instance is forced
  read-only for the session (only one instance may own the XMLs / op-log). The change is
  in-memory only — `LiteBox.ini` is left untouched — and the window makes it explicit: a
  warning-coloured caption (Win11) + a banner, and the options menu is locked.
- **Show "game running" screen**, **Unload the list while a game runs**, **Use the
  image cache** (degraded thumbnails).
- **Use game cache** (when ExtendDB is absent) + **Unload game cache during game**.
- **Use 16:9 for the main media** (else poster ratio).

The options window also carries **LaunchBox-replica categories** that round-trip to
LaunchBox's own `Settings.xml` (real LB field names, so LB/BigBox stay in sync):

- **Plugins** — checklist of the `<LaunchBox>\Plugins` subfolders to load; applied
  at the next restart (see *Deploy* above).
- **LB · Gameplay** — three tabs that drive LiteBox's own game-launch screens:
  - *Game Startup* — the startup ("NOW LOADING…") screen and the matching end
    ("GAME OVER") screen (min display times, hide cursor);
  - *Game Pause* — the pause screen + a **rebindable pause key**;
  - *Screen Capture* — a **screenshot hotkey** that saves a PNG of the game's
    monitor to `<LaunchBox>\Screenshots`.

  The pause and screenshot keys are click-to-capture combo fields (Esc clears) and
  are registered as **global hotkeys** — they fire even while the emulator has
  focus. Gameplay changes apply on the next game launch. (Theme pickers are omitted
  — LiteBox has no themes.)
- **LB · Integrations** — LaunchBox-parity toggles such as the **DOSBox** command
  options (show commands / don't exit / pause before each command / before exit),
  honoured by LiteBox's built-in DOSBox launch.

Pane widths, column layout (width / visibility / order), sort, selection and the
list/poster mode are all remembered between runs.
