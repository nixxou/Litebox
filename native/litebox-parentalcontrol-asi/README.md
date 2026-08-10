# litebox-parentalcontrol.asi

Native ASI plugin (loaded by Ultimate ASI Loader via a `winhttp.dll` proxy) that enforces
LiteBox parental control inside **vanilla LaunchBox.exe and BigBox.exe**. WS5.1 of the
parental revamp — see `../../docs/parental-revamp-plan.md`.

## What it does

Hooks `CreateFileW`; every read of `LB\Data\Platforms\*.xml` is redirected to an in-memory
anonymous pipe streaming a **filtered** copy, keeping only games that pass parental control
and pruning their orphan related rows. A game is kept iff **both**:

- its `<Rating>` passes the rules (Whitelist/Blacklist + wildcard `Rule=` patterns), and
- its `<ID>` is **not** in the per-game blocked set (`BlockedId=`, the "requires parental" flag).

No temp files; only the small kept-ID set is cached per platform. Writes are the managed
plugin's job (WS5.2). Adapted from the proven `extenddb.asi` (same MinHook + streaming-pipe
machinery), ExtendDB cruft removed, LaunchBox.exe activation added.

## Config — `LB\Core\litebox-parental.dat`

The flat file LiteBox writes (`Host/Parental/ParentalNativeExport`). One `key=value` per line,
UTF-8 no BOM:

```
LaunchBoxEnabled=1     BigBoxEnabled=1     PinSet=1
Mode=Whitelist         (or Blacklist)
Rule=M*                (repeated)
BlockedId=<guid>       (repeated)
```

Cold-start gate (before the managed plugin speaks), per process: LaunchBox filters iff
`LaunchBoxEnabled` (no "boots locked" notion — config alone decides); BigBox filters iff
`BigBoxEnabled` AND a non-empty `<LockPin>` is set (BigBox boots locked only then) and also
gets its `<Allow*WhileLocked>` flags forced false. Once the managed plugin calls
`litebox_parental_set_filtering()` it is the sole authority.

## Exports (managed control channel — WS5.2)

- `litebox_parental_set_filtering(int)` — enable/disable filtering at runtime.
- `litebox_parental_open_real_file(LPCWSTR)` — open the UNFILTERED file (bypasses our hook).

## Build

VS 2022 with **Desktop development with C++**, Release | x64. First build runs `setup.bat`
(downloads MinHook into `external\minhook\` via curl; drop a checkout there manually if curl
is absent). Or from the shell:

```
MSBuild litebox-parentalcontrol-asi.vcxproj /p:Configuration=Release /p:Platform=x64
```

Output: `bin\Release\litebox-parentalcontrol.asi`.

## Install (done by LiteBox's install flow — WS6)

Drop `litebox-parentalcontrol.asi` + `winhttp.dll` (the ASI loader) into `LB\Core\`, next to
LaunchBox.exe / BigBox.exe. Logging goes to `LB\Core\litebox-parental.log`, active only once
`litebox-parental.dat` is present.

## Status

Builds and exports verified. **Runtime not yet tested in LaunchBox/BigBox** — the read filter,
the blocked-ID pruning, and the BigBox cold-start hardening need a live pass (WS0's probe
approach on a copy, or with the managed write-guard of WS5.2 in place so saves can't persist a
filtered subset).
