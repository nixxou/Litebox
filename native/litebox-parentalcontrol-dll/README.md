# litebox-parentalcontrol.dll

Managed LaunchBox/BigBox plugin — the runtime half of LiteBox parental control (WS5.2). Its
counterpart is the native ASI (`../litebox-parentalcontrol-asi/`) that filters platform-XML
**reads**; this plugin guards **writes** and drives the ASI. See `../../docs/parental-revamp-plan.md`.

## What it does

- **Write-guard (safety-critical).** A Harmony prefix on `File.Copy` blocks any copy whose
  destination is under `LB\Data\` while the in-memory library may be the filtered subset
  (`LockState.WritesUnsafe`). WS0 proved LaunchBox/BigBox persist every data file as
  temp→`File.Copy(temp, Data\X.xml)`→delete, and that a sync rewrites the whole library — so
  dropping the copy into `Data\` is what stops a filtered save from ever hitting disk. Block
  only (no Merge); fails **open** on our own error so LB's save/exit is never broken.
- **ASI control channel** (`AsiBridge`) — calls the ASI's `litebox_parental_set_filtering` /
  `litebox_parental_open_real_file` exports.
- **Lock wiring** — on BigBox lock/unlock (and the startup handoff) it flips the ASI filter,
  reloads the library (`DataManager.ForceReload`), and moves the anti-corruption latch: armed
  BEFORE re-filtering on lock, cleared AFTER the real reload on unlock, so the unlock
  micro-window still reads writes-unsafe.

Starts **locked** (filtered) so a restart never leaves the library exposed. Inert unless
parental is configured for this process in `LB\Core\litebox-parental.dat`.

## Build

`dotnet build -c Release`. References `0Harmony.dll` + the LaunchBox SDK by HintPath (net10 →
LB 13.28). Output: `bin\Release\litebox-parentalcontrol.dll`.

## Install (LiteBox's install flow — WS6)

Deploy `litebox-parentalcontrol.dll` + `0Harmony.dll` into `LB\Plugins\litebox-parentalcontrol\`.
The ASI + `winhttp.dll` go into `LB\Core\`. Log: `litebox-parentalcontrol.log` next to the dll.

## Status / remaining

Builds. **Runtime not yet tested in LB/BB.** Deliberately scoped to the safety-critical core
(write-guard + bridge + BigBox lock wiring). Still to add, and to validate live:
- **LaunchBox unlock UI** — LaunchBox has no lock/unlock events, so it stays filtered until an
  unlock. A Tools-menu PIN dialog (verify against BigBox's `<LockPin>`) → `SetLocked(false)` is
  the follow-up; today unlocking a LaunchBox session is done from LiteBox.
- **Anti-tamper** — the ASI checks a `PluginPath` from the `.dat`; the WS7 export doesn't emit
  one yet. Add `PluginPath=` to `ParentalNativeExport` + the deploy path to enable it.
- Confirm `PatchAll` arms early enough (ISystemEventsPlugin is instantiated at boot) and that
  `ForceReload` behaves under LaunchBox as it does under BigBox.
