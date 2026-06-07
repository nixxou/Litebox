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
- Optional write-back: with `ReadOnly=false`, favorites/ratings/play stats are
  journalled and written to the XMLs only when LaunchBox/BigBox aren't running.

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
```

## Options (gear menu / `LiteBox.ini`)

- **ReadOnly** (default **true**): never write to the LaunchBox XMLs; favorite /
  rating / play changes stay in memory for the session. Set false to persist them
  (journalled, written only when LB/BigBox aren't running).
- **Show "game running" screen**, **Unload the list while a game runs**, **Use the
  image cache** (degraded thumbnails).
- **Use game cache** (when ExtendDB is absent) + **Unload game cache during game**.
- **Use 16:9 for the main media** (else poster ratio).

Pane widths, column layout (width / visibility / order), sort, selection and the
list/poster mode are all remembered between runs.
