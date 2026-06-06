# LiteBox

A lightweight, headless **host that implements the LaunchBox plugin API**
(`Unbroken.LaunchBox.Plugins`) so LaunchBox plugins (e.g. ExtendDB and its web
subsystem) can run **without the full LaunchBox.exe** — for a low-RAM web/kiosk
setup. LiteBox loads all user data from LaunchBox's native XMLs and exposes it
through its own implementation of the API, with no dependency on any plugin.

## What it does

- Loads the whole LaunchBox library from `Data\Platforms\*.xml` (games, platforms,
  categories, emulators, playlists incl. auto-populate filters, additional
  applications, alternate names) into a memory-frugal two-tier store.
- Resolves media (images/videos) the LaunchBox way — on-demand `Directory`
  enumeration, with a fast path that delegates to ExtendDB's `GameCache` when it
  is loaded and ready.
- Hosts LaunchBox plugins: injects `PluginHelper` services, fires
  `PluginInitialized`, exposes system/game menus, and launches games (emulator or
  direct) through `IGameLaunchingPlugin`.
- WinForms GUI: 3-pane LaunchBox-like layout (source tree / sortable game list via
  ObjectListView / details with art + notes), dark themed.
- Frees RAM while a game runs (drops the optional data tier + trims the working
  set) and can show a "game running" screen.

## Requirements

- A modern **.NET 9** LaunchBox install (the one with a self-contained flat
  runtime in `<LaunchBox>\Core`). LiteBox shares that runtime.
- `LiteBox.exe` **must live in `<LaunchBox>\Core`** (its paths key off the exe
  location; it switches the working directory to the LB root at startup).

## Build

Open in the .NET 9 SDK and `dotnet build -c Debug`. The project references
`Unbroken.LaunchBox.Plugins.dll` at compile time only (`Private=false`) — adjust
the `HintPath` in `LbApiHost.csproj` to your `<LaunchBox>\Core\` copy. The SDK
assembly is resolved at runtime from `Core`, so LiteBox is **version-agnostic**
across LaunchBox versions.

## Deploy

Publish self-contained and copy **only these 5 files** into `<LaunchBox>\Core`
(everything else is provided by LaunchBox's runtime already there):

```
dotnet publish -c Debug -r win-x64 --self-contained true -o publish-sc
```

- `LiteBox.exe`
- `LiteBox.dll`
- `LiteBox.deps.json`
- `LiteBox.runtimeconfig.json`
- `ObjectListView2022NET.dll`  ← the only external dependency

Optional: a `whitelist.txt` in `Core` (one plugin folder name per line, subfolders
of `<LaunchBox>\Plugins`) to activate plugins; without it LiteBox runs standalone.
`LiteBox.ini` (config) is auto-created next to the exe on first run.

## Run

```
LiteBox.exe                 GUI (default)
LiteBox.exe --headless      diagnostics (--playlists --mediatest --gcdump --drop --play --drylaunch)
LiteBox.exe --plugins <dir> override the plugins root
```
