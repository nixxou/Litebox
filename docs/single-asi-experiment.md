# Experiment: one atomic artifact — the read-filter AND the write-guard in a single DLL

Branch: `test-single-asi-no-dll`

## Why

Today the native parental control is TWO artifacts that can diverge:
- `litebox-parentalcontrol.asi` — native C++, loaded EARLY by the ASI loader (winhttp proxy),
  installs the `CreateFileW` hook that filters platform-XML READS.
- `litebox-parentalcontrol.dll` — managed C# plugin, loaded by LaunchBox's plugin scanner,
  is the WRITE-GUARD (Harmony `File.Copy` block) + lock/unlock lifecycle.

The whole class of data-loss bugs the Codex reviews kept surfacing comes from the two being able
to disagree: **the ASI filters (reads amputated) while the guard is missing/wrong/failed-to-load
(writes not blocked) → a save overwrites the real library with the filtered subset.** Every fix so
far (file-existence interlock, fail-closed migration, guard-delete disarm) patches one way the two
can drift.

**Goal:** make the filter and the guard the SAME loaded artifact. Then they cannot diverge — either
the artifact loads (both work) or it doesn't (neither works). There is no "filtered but unguarded"
state to defend against, and the whole interlock/migration machinery becomes unnecessary.

## The mechanism: a mixed-mode (C++/CLI) assembly

A single DLL compiled as **C++/CLI** (`/clr:netcore`) is BOTH:
- a **native** module — it has a real native `DllMain`, so `LoadLibrary` runs it (early, via the ASI
  loader), where it installs the MinHook `CreateFileW` filter — exactly what the current `.asi` does;
- a **managed** assembly — it exposes a `ref class` implementing LaunchBox's
  `ISystemMenuItemPlugin` / `ISystemEventsPlugin`, so LaunchBox's plugin scanner loads it as a normal
  plugin — exactly what the current `.dll` does.

The managed guard and the native hook now live in the same module, so "set filtering on/off" is an
in-process function call, not a cross-DLL bridge.

## The two hard constraints (from research)

1. **`DllMain` must run NO managed code.** In .NET Core / .NET 5+, if a C++/CLI assembly's FIRST
   managed-code execution comes from a native caller, the assembly is loaded into an *isolated*
   `AssemblyLoadContext`, and LaunchBox's later `Assembly.LoadFrom` would get a different instance.
   Our `DllMain` already only does native work (MinHook + `CreateFileW`), so the FIRST managed touch
   must be LaunchBox's plugin scan → default ALC → normal plugin. Keeping `DllMain` 100% native is
   mandatory and is the #1 thing to verify at runtime.
   Refs: [dotnet/runtime #61105](https://github.com/dotnet/runtime/issues/61105),
   [discussion #94279](https://github.com/dotnet/runtime/discussions/94279),
   [Mixed-mode init (MS Learn)](https://learn.microsoft.com/en-us/cpp/dotnet/initialization-of-mixed-assemblies?view=msvc-170),
   [thewover — Mixed Assemblies](https://thewover.github.io/Mixed-Assemblies/).

2. **`Assembly.LoadFrom` of a mixed assembly works only at matching architecture (x64) and can even
   run code from `DllMain`** — but it "cannot be loaded from memory" (irrelevant here; we load from
   disk). So x64-only, which LaunchBox/BigBox already are.

## Packaging for TRUE atomicity (one physical file)

The catch: the ASI loader scans `Core\*.asi`; LaunchBox scans `Plugins\**\*.dll`. Different location
+ extension. Two copies of the same build (one `.asi` in Core, one `.dll` in Plugins) would REINTRODUCE
the divergence (one present, the other missing) — so that's rejected.

**Chosen shape:** keep a tiny, generic **winhttp proxy** whose only job is to `LoadLibrary` the ONE
mixed DLL from its Plugins path early, then chain to the real winhttp. LaunchBox's scanner loads that
SAME file as the plugin. So:
- `Core\winhttp.dll` — tiny generic loader stub (native, TFM-agnostic, never changes → can't diverge).
- `Plugins\litebox-parental\litebox-parental.dll` — the ONE mixed DLL = filter + guard.

If the mixed DLL is missing/corrupt: the proxy's `LoadLibrary` fails (no hook, no filtering) AND
LaunchBox finds no plugin (no guard) → neither half runs → **atomic, fail-safe.** The proxy loading a
Plugins path is the only custom bit; the current Ultimate ASI Loader loads `Core\*.asi`, so either we
configure it to also load the Plugins DLL or ship a ~30-line custom winhttp shim.

## What still needs a RUNTIME test (I can't run LaunchBox, and C++/CLI isn't installed here)

The toolchain gap: `Microsoft.VisualStudio.Component.VC.CLI.Support` is NOT installed on this machine,
so the mixed DLL can't be built/compiled here yet. Install that VS component (Individual components →
"C++/CLI support for v143 build tools") to build the POC below.

Test plan (once buildable), in increasing risk:

1. **Managed-plugin-from-mixed-assembly.** Build a minimal C++/CLI DLL with a `ref class` implementing
   `ISystemMenuItemPlugin` (a menu item that pops a MessageBox). Drop it in
   `Plugins\test-single\test-single.dll`. Launch LaunchBox. **Does the menu item appear?**
   → proves LaunchBox loads a mixed assembly as a plugin (the isolated-ALC risk).

2. **Early native `DllMain`.** Add a native `DllMain` that writes a line to `Core\single-asi.log` with
   a timestamp. Have the winhttp proxy `LoadLibrary` the DLL. Launch LaunchBox. **Is the `DllMain` line
   written BEFORE LaunchBox reads the platform XMLs?** (compare timestamps / add a hook-fired log on the
   first `Platforms\*.xml` read) → proves the native half is early enough to filter.

3. **Both at once, same file.** With the winhttp proxy early-loading the Plugins DLL AND LaunchBox
   scanning it: **does the menu item STILL appear (managed half OK) AND the hook fire (native half OK)?**
   → the decisive test: the early `LoadLibrary` must NOT poison the managed load into an isolated ALC.
   If the menu vanishes when the proxy pre-loads it, constraint #1 was violated (something ran managed
   from `DllMain`) or .NET Core isolates it anyway → fall back (see below).

4. **Guard wiring.** Port the Harmony `File.Copy` guard + lock/unlock into the managed `ref class`, and
   the MinHook `CreateFileW` filter into the native side; have the managed guard toggle the native
   filter via an internal call. Verify a locked save is blocked and an unlock restores the library.

## Fallbacks if the mixed-mode double-load fails the ALC test

- **B1 — managed plugin installs the hook itself.** A pure managed plugin P/Invokes a bundled MinHook
  (or an inline VirtualProtect+JMP trampoline) to hook `CreateFileW` at `ModuleInitializer` time. ONE
  managed DLL, dual-TFM free — but only viable if plugin-load is EARLY enough (the current design
  assumes it is NOT; test #2's timing answers this too).
- **B2 — keep two artifacts but bind them harder** (the current branch's fail-closed/guard-delete) — the
  status quo; only if atomicity proves impossible.

## Success criterion

A single DLL that, when present, gives BOTH the read filter and the write guard, and when absent gives
NEITHER — with no separate interlock needed. That removes `ParentalNativeInstall`'s dual-TFM guard
juggling, the boot migration, and the whole "filtered-but-unguarded" defense.

---

# RESULT (2026-08-11): PROVEN via `DOTNET_STARTUP_HOOKS` — no C++/CLI needed

The mixed-mode C++/CLI route above was NOT needed. A cleaner mechanism works and is proven on the live
net10 LaunchBox (13.28):

**`DOTNET_STARTUP_HOOKS`.** .NET runs a top-level `internal class StartupHook { static void Initialize() }`
in an assembly BEFORE the app's `Main`. A tiny native stub (loaded early by winhttp) sets the env var; the
CLR then runs our managed `Initialize()` before LaunchBox reads anything.

Measured (probe logs): native trigger set the var → `StartupHook.Initialize` ran **before Main** → it armed
a MinHook `CreateFileW` hook → the hook then caught **100% of the platform-XML reads** (~190 platforms, all
~0.8 s AFTER the hook armed). And the SAME assembly, placed in `Plugins\`, ALSO loaded as a LaunchBox plugin
(`ISystemMenuItemPlugin`) — its menu showed `early:True reads:190`, i.e. the plugin instance and the
startup-hook instance **share statics = same AssemblyLoadContext = one instance doing both roles.**

Two gotchas found:
- A startup hook that **fails to load fails FAST → crashes LaunchBox.** So `Initialize()` must use ONLY BCL +
  kernel32 (zero external managed deps), or its dep resolution can brick startup. (Harmony + an ALC resolver
  in `Initialize` crashed it; removing both fixed it.)
- `DOTNET_STARTUP_HOOKS` is **inherited by child .NET processes** — LaunchBox self-relaunched once, so the hook
  ran twice. Harmless here; the guard must be idempotent (hook-once).

## Chosen target architecture

Two physical files, but only ONE carries logic:
- **`winhttp.dll`** — a tiny GENERIC native stub (Ultimate ASI Loader or a ~15-line proxy): its only job is to
  `SetEnvironmentVariable("DOTNET_STARTUP_HOOKS", <path to the managed dll>)`. Immutable, no business logic →
  can never diverge. (It also still needs to exist for the loader; that's fine.)
- **`litebox-parental.dll`** (managed, in `Plugins\litebox-parental\`) — does EVERYTHING, atomically:
  - `StartupHook.Initialize()` (early, before Main): installs the `CreateFileW` hook. BCL + kernel32 only.
  - the hook **filters reads** (`Platforms\*.xml` → filtered stream) AND **blocks writes** into `Data\`
    (`File.Copy` → `CreateFileW(dest, GENERIC_WRITE)` — the same hook sees it). **No Harmony needed** — the
    write-guard is the same native hook, so there is no `0Harmony.dll` early-dependency to brick startup.
  - `ISystemMenuItemPlugin` / `ISystemEventsPlugin` (loaded later by the plugin scanner, SAME instance/statics):
    the lock/unlock UI + BigBox lock events; shares state with the early hook.

**The native hook needs MinHook** (to hook `CreateFileW`). Options: ship a `MinHook`/native `.bin` beside the
managed dll and `LoadLibrary` it from `Initialize` (as the probe does), or a pure-managed inline hook. The
`.bin` route is proven (the probe uses it) and keeps the atomicity (if the managed dll is absent, nothing
runs; if the `.bin` is absent, the hook fails → the guard can refuse to filter → fail-safe).

**Atomicity achieved:** if `litebox-parental.dll` loads → read-filter + write-guard + UI all active. If it
fails/absent → NONE of them → LaunchBox runs on the real, unfiltered library with no guard needed. There is no
"filtered-but-unguarded" state to defend, so `ParentalNativeInstall`'s interlock + dual-TFM guard migration +
fail-closed machinery all go away. Dual-TFM is free (a net9 managed dll loads on net10).

## Do we need Harmony? — No.

WS0's write chokepoint (`File.Copy` into `Data\`) surfaces at the `CreateFileW` level as
`CreateFileW(dest, GENERIC_WRITE, CREATE_ALWAYS)`, which the read-filter's hook already sees. So the write-guard
lives in the same hook — refuse the open (File.Copy throws) or redirect it to a throwaway (File.Copy silently
no-ops, like the old Harmony prefix). Since edits are UI-blocked while locked (WS3), the write-guard is a
last-resort net, so even a hard refuse is acceptable. Dropping Harmony removes the early dependency that bricked
startup and makes the single artifact fully self-contained (BCL + kernel32 + the MinHook `.bin`).

## Write path CONFIRMED (2026-08-11): CopyFileExW, not CreateFileW

A LaunchBox save does NOT surface at the hooked `CreateFileW` (0 write-opens caught on an edit+close) — its
`File.Copy(temp -> Data\X.xml)` goes through the Win32 **`CopyFileExW`** export, which opens the destination
internally (bypassing the exported CreateFileW). Hooking `CopyFileExW` (+ `CopyFile2` as a belt) caught the
save cleanly: on an edit of a Windows game + close, 12 `[Copy] CopyFileExW -> …\Data\…` were logged, including
`…\Data\Platforms\Windows.xml` (the edited game's platform file) plus Settings.xml, ListCache.xml, etc.

So the two hooks the single managed DLL installs early are:
- **`CreateFileW`** → the READ filter (`Platforms\*.xml` → filtered stream), and
- **`CopyFileExW`** (+ `CopyFile2`) → the WRITE guard (block/redirect a copy whose destination is under
  `Data\` while locked). Both native (MinHook), both from `StartupHook.Initialize`, zero managed deps.

**No Harmony, confirmed.** The write path is a hookable Win32 export sitting right next to the read hook.

Block form (implementation choice): return TRUE from the `CopyFileExW` hook WITHOUT calling the original →
the copy silently doesn't happen and `File.Copy` sees success (gentle, like the old Harmony prefix); or return
FALSE → `File.Copy` throws. Since edits are UI-blocked while locked (WS3), the guard is a last-resort net, so
either is acceptable. Scope: gate on lock state; WS0 covered all of `Data\`, but the filtered library only
lives in `Platforms\*.xml` / the `Platforms.xml` index / `Playlists\*.xml`, so those are the must-block set.

## Status: fully proven — ready to build the real single artifact.
