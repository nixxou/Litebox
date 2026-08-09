# Parental control revamp — battle plan

Branch: `parentalcontrolrevamp`. This is the goal + roadmap doc for the branch; it
survives sessions and context compaction. Read it first each session.

Companion: [`parental-parity-audit.md`](parental-parity-audit.md) — the concrete
desktop/web gap list (findings 1.1–1.7, fixes X1–X8). This plan does **not** repeat
those; it folds them into the relevant workstream.

---

## Why the revamp

The parental subsystem was designed as an ExtendDB **plugin** grafted onto LaunchBox
— its assumptions were "how does a managed plugin hook the host". ExtendDB is now
deprecated and being retired, and parental control lives **natively inside LiteBox**.
The question is no longer "how does a plugin attach" but "what does LiteBox do
itself, and how does it reach out to the two third-party apps it does not own".

Nothing here modifies the ExtendDB or `natifblock-test` projects. They are
**reference implementations to port from**, nothing more.

---

## The surfaces model (this is the spine — everything hangs off it)

Four surfaces, three enforcement mechanisms:

| Surface | What it is | Mechanism | Owner |
|---|---|---|---|
| **LiteBox desktop** | our WinForms host | native in-process (`ParentalFilter`) | us |
| **LiteBox web** (bigbox-web + launchbox-web) | web UIs LiteBox serves | native, server-side (`WebParentalState` + `ParentalWebWriteGuard`) | us |
| **LaunchBox.exe vanilla** | third-party | **NEW: ASI read-filter + plugin write-guard** | third-party |
| **BigBox.exe vanilla** | third-party | ASI read-filter + plugin write-guard | third-party |

The **new direction** vs the ExtendDB era: LaunchBox vanilla is filtered at the
**XML source via the ASI**, exactly like BigBox — *not* by altering WPF views. The
old view-filter (`ParentalViewFilter`) + SQL-filter (`ParentalSqlFilter`) pair was
cosmetic by construction (two mechanisms that had to agree); filtering at the source
is one application point instead of two.

The single credential everywhere is **BigBox's own `<LockPin>`** (already the case —
`Host/Data/BigBoxPin`, Rijndael-256). Not an objective; the current state.

---

## What already exists (reuse map — do NOT rebuild)

**Native LiteBox (done):**
- `Host/Parental/ParentalConfig.cs` — model; scalars in `LiteBox.ini [Parental]`, the
  three lists (rating rules + 2 BigBox hide-lists) in `parental-lists.json`.
- `Host/Parental/ParentalFilter.cs` — runtime lock state (boot-locked), derived state,
  wildcard match, PIN gate + 3-strike lockout.
- `Host/Parental/ParentalWebWriteGuard.cs` — **already enforces** the two allow-flags
  (rating/favorite) for web mutations. `DenyReason(isLocked, kind)`.
- `Host/Media/ParentalBridge.cs` — façade (padlock, filters, install gate).
- `Host/Options/Modules/ParentalPanel.cs` — the options UI (the screenshots).
- `Host/Data/BigBoxPin.cs` — the shared PIN.

**Reference implementations to port (ExtendDB — read, don't touch):**
- `ExtendDB/extenddb-asi/src/main.cpp` (1029 l.) — the production ASI. Prefer this over
  `natifblock-test` (which still carries the dead CefSharp `disable-gpu*` patch).
- `ExtendDB/Patches/PlatformXmlWriteGuard.cs` — the `File.Copy` write-guard + the
  `BigBoxWritesUnsafe` anti-corruption latch.
- `ExtendDB/Watchers/ExtendDbAsiBridge.cs` — managed↔native control channel.
- `ExtendDB/Utility/PlatformXmlMerger.cs` — the (experimental) Merge worker.
- `ExtendDB/thirdparty/winhttp.dll.api` — the vendored Ultimate ASI Loader, deployed
  into `LB\Core` by renaming. Reusable as-is.

---

## Data-safety invariants (violate one and you delete a user's library — non-negotiable)

1. **Never write filtered content to disk.** The library on disk must always stay the
   full library. If this holds, a crash mid-lock is harmless.
2. **The write-guard is the critical piece, not the read-filter.** A wrong read is
   reversible; a filtered save is permanent deletion of every hidden game.
3. **Arm before the first filtered read; disarm only after the real reload.** The ASI
   read-filter must be active before the host's first read, and the latch
   (`BigBoxWritesUnsafe` equivalent) must stay armed through the *entire* unlock
   window until an unfiltered `ForceReload` has settled the in-memory library —
   otherwise a save in the unlock micro-window persists the filtered subset.
4. **Backups are poisoned by our OWN read-filter, not by writes.** Any routine that
   reads `Data\Platforms\*.xml` while locked gets the filtered stream and writes a
   valid-looking but truncated backup. This is the subtlest failure mode — see WS0.
5. **Anti-tamper.** Removing the plugin/ASI must not silently unlock. ExtendDB aborted
   the program (`TerminateProcess`) when the recorded plugin DLL was gone.

---

## Workstreams (ordered by dependency + risk)

### WS0 — De-risk spike **[GO/NO-GO GATE — do this before any real code]**
The whole "filter LaunchBox vanilla via ASI" direction rests on two unverified facts.
Verify experimentally, not by reasoning:
- **W0.1 — Write interception under LaunchBox.exe.** `PlatformXmlWriteGuard` already
  installs in both processes (only its *gate* was BigBox-only). But LaunchBox is the
  **editor**: it saves on every edit (not just at exit) and touches more than
  `Platforms\*.xml` (playlists, `Settings.xml`, favorites). Confirm the `File.Copy →
  Data\Platforms` chokepoint is the same under LB, and enumerate what *else* LB writes
  while locked that could persist filtered state.
- **W0.2 — Backup poisoning.** Determine HOW LaunchBox reads the platform XMLs when it
  backs up: `File.Copy`/`CopyFileExW` (does NOT go through `CreateFileW` → copies the
  real file → safe) vs `FileStream`/zip (goes through `CreateFileW` → captures the
  filtered stream → silent truncated backup). A zipped `Data\` backup is the dangerous
  case. Fallback if dangerous: refuse the backup while locked — but first find *all*
  readers.

**Deliverable:** a short findings note appended here. If W0.1/W0.2 come back bad, the
ASI-for-LaunchBox direction changes shape before we commit to it.

### WS1 — Options panel cleanup (independent, low-risk, do early)
- Strip the activation blabla → one line: *LiteBox uses BigBox's parental PIN; set /
  unset it here.* Keep only PIN + confirm + Show.
- Remove the périmé BigBox bloc (write-guard dropdown, "Force Web hide all", "Require
  PIN to install", the "web" wording on star/favorite) — pending the vanilla plan
  below, these get redesigned, not kept.
- Keep the **star / favorites** permission checkboxes but rescope: two checkboxes
  governing **three surfaces** (LiteBox desktop, bigbox-web, launchbox-web), not "web"
  alone.

### WS2 — Star/favorites permission across the 3 LiteBox surfaces
Web already enforces via `ParentalWebWriteGuard` (both flags). Extend the same two
allow-flags to **LiteBox desktop** mutation paths (star rating + favorite toggle from
the desktop UI honour `AllowLockedModify*`). One decision point, mirrors the web guard.

### WS3 — Admin lockdown in limited mode **[large — needs an inventory, not a guess]**
While `ParentalFilter.Active`, every administrative action is disabled: edit a game,
edit a platform, delete anything, bulk edits, etc.
- **W3.1 — Exhaustive entry-point inventory.** Not just the main menu: context menus,
  double-click, keyboard shortcuts, drag & drop, buttons inside already-open windows.
  A greyed menu with a live shortcut is a lock on an open door.
- **W3.2 — Gate them all** on `ParentalFilter.Active`.
- **W3.3 — Server-side refusal for web endpoints.** Greying UI is cosmetic; the
  launchbox-web/bigbox-web edit/delete endpoints must refuse in limited mode
  (defense in depth). This is the same shape as `ParentalWebWriteGuard.DenyReason`.

### WS4 — Per-game "requires parental rights" flag
- **W4.1 — UI:** a checkbox in the Edit Game flag lot, next to Broken/Hide/Favorite
  ([`EditGameWindow.cs:893-902`](../Host/EditGameWindow.cs#L893)). NOT a native LB
  property (unlike `g.Broken`), so:
- **W4.2 — Storage:** source of truth = **Options DB** (new per-game key, Game scope,
  Bool — auto-cleaned when the game disappears, never touches LB's XML). The Options DB
  is SQLite EAV → **the C++ ASI cannot read it**, therefore:
- **W4.3 — Export:** LiteBox exports the blocked-ID set to a **flat sidecar** the ASI
  consumes (newline-delimited IDs — trivial to parse in C++). This is the same
  "source-of-truth + derived flat artifact" pattern ExtendDB used for its rules. Note:
  a potentially-large ID list does NOT belong in `LiteBox.ini` (scalars only there).
- **W4.4 — Enforcement:** the ASI keeps games out by ID (it already works on an ID set
  — adding a blocked-ID list on top of the rating filter is nearly free); the native
  LiteBox surfaces read the same flag.

### WS5 — The two new native projects (`litebox-parentalcontrol.asi` + `.dll`)
New directories, inspired by ExtendDB, not sharing its build.
- **W5.1 — `litebox-parentalcontrol.asi`:** port the extenddb-asi skeleton — CreateFileW
  hook, streaming pipe, ID-cache, recursion guard, anti-tamper (plugin-presence), the
  ~30 `Allow*WhileLocked` BigBoxSettings hardening. **New vs reference:**
  - filter on the **blocked-ID list** (WS4) in addition to `<Rating>`;
  - **active in LaunchBox.exe** as a read-filter (today it is inert there);
  - reads a **LiteBox-owned flat config** (rename from `ExtendDBParental.dat`, new
    exports for name/pin/hotkey);
  - **cold-start gate for LaunchBox:** LB vanilla has no `<LockPin>` "boots locked"
    notion — our config alone decides, so LB starts filtered whenever the scope is on.
- **W5.2 — `litebox-parentalcontrol.dll`:** the managed plugin — `File.Copy` write-guard
  + latch for **both** LB and BB; lock/unlock → `SetFiltering` + `ForceReload` +
  GameCache concerns; the **PIN entry UI under LaunchBox** (small dialog to lock/unlock);
  anti-tamper target = the LiteBox-owned artifact, not ExtendDB's.
- **W5.3 — Backup guard** (from WS0 findings): prevent poisoned backups.

### WS6 — Install / uninstall flow
- A button in LiteBox to deploy `litebox-parentalcontrol.asi` + `winhttp.dll` (ASI
  loader) into `LB\Core` (reuse ExtendDB's `.api`-suffix + rename-on-deploy pattern),
  plus the plugin DLL into `Plugins\`.
- Clean **uninstall** path (we are dropping a DLL injector into a third-party install).
- Resilience notes: AV false-positives, LaunchBox self-updates overwriting `Core`.

### WS7 — Config as single source of truth + flat export
- LiteBox is the **sole writer**. Decide: keep scalars in `LiteBox.ini [Parental]` and
  emit the flat `.dat` + blocked-ID list as derived artifacts the ASI/DLL read (favored
  — one writer, regenerable), or move everything native reads into flat files.
- The parity-audit fixes X1–X8 (PIN-gate the panel, ephemeral kiosk unlock, web
  lockout, fail-closed no-PIN, db-site SQL rules, force-web on web, FixedTimeEquals)
  land alongside — X8 ("BigBox coverage: deploy ASI or waive") is **superseded** by
  this plan's decision to ship the ASI for both LB and BB.

---

## Open decisions (need sign-off before the WS they block)

- **D1 — Merge mode.** Keep the experimental Block/Merge write-guard policy, or ship
  **Block only**? Block never rewrites the live library; Merge folds the filtered subset
  back in (risky). LiteBox has no filtered-library rewrite to merge against today. → gates WS5.2.
- **D2 — ExtendDB coexistence.** Must the new plugin+ASI coexist with ExtendDB still
  installed during the transition (two hooks fighting the same `CreateFileW`), or does
  it assume it is the only one? → gates WS5, WS6.
- **D3 — LaunchBox cold-start behavior.** Confirm LB starting filtered-by-default
  whenever the scope is on (no LockPin equivalent) is the intended UX. → gates WS5.1.
- **D4 — ini vs flat files for the blocked-ID list.** Sidecar list (favored) vs
  something else. → gates WS4.3, WS7.

---

## Suggested execution order

WS0 (gate) → WS1 (quick win, independent) → WS7 decision D4 + config plumbing → WS4
(per-game flag, needs the export) → WS5 (the native projects, the bulk) → WS3 (admin
lockdown, large but independent of the natives) → WS2 (desktop allow-flags) → WS6
(install) → fold in X1–X8. WS2/WS3 can run in parallel with WS5.
