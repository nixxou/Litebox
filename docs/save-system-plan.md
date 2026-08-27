# Save system — repair plan

> Branch `fix-save-system`, based on `main`, ahead of the RomM work (`romm-server`, frozen at `be6434a`)
> which will be rebased and back-ported once this lands.
>
> Reading order: `docs/save-algorithms.md` establishes what each integration plugin does and what breaks
> under extraction. This plan does not repeat it — it decides what to do about it.

---

## 0. What this branch is for

Three defects, established from the decompiled plugins, not from behaviour:

| | Defect | Effect |
|---|---|---|
| **D1** | **Identity** — the plugin derives the save name from `basename(ApplicationPath)` (the archive), the emulator used the extracted ROM's name | saves exist and are not found; found by luck when the prefix happens to match |
| **D2** | **Durability** — with `savefiles_in_content_dir` the emulator writes *inside* the extraction cache | `PurgeTmp` deletes it on game exit; LRU eviction deletes it later. Silent data loss |
| **D3** | **Multiplicity** — one library entry, N inner ROMs, one `<GameSave>` slot with no field for "entry" | N saves collapse into indistinguishable groups all named "My Save File" |

Plus one property nobody asked for but which the code does not provide: **nothing prevents the same save
file being claimed by two different games.** Records are read per game (`game.GetSubEntities("GameSave")`);
there is no global `FilePath` index and no cross-check. Today that is rare. The moment D1 is fixed by
widening what a scan matches, it stops being rare.

**Out of scope, deliberately:** the RomM projection. Upstream has confirmed (Discord, 2026-08-26) that a
save is tied to `rom_id` alone and that per-version saves are a known, unsolved gap on their side. The
only carrier available there is the free-text `slot` field. That is a mapping decision for the back-port,
and it needs the identity this branch produces before it can be made.

---

## 1. What has to be observed before it is designed

The plugins are known. **LaunchBox's own behaviour around archives is not**, and three of its answers
change what we build. These are manual tests (§7 protocol), not automated ones.

| Question | What it decides |
|---|---|
| With LB's own "extract archive" option, does it extract under the **inner** name or rename to the archive's? | whether LB has an implicit convention we should match rather than invent |
| Does LB's save management actually work at all on an archived game? | whether we are repairing a feature or building one |
| **Does LB preserve an unknown child element inside `<GameSave>` on rewrite?** | whether entry identity can use a new field, or must be carried in `SaveGroupId` |

The third is the load-bearing one. `SaveGroupId` is already used as a namespaced string by the plugins
themselves — `saturn-<basename>` in RetroArch, `pcsx2:<card>:<dir>` in PCSX2 — and the SDK exposes
`UseSaveGroupIdForPersistedMatch` precisely so the host matches on it instead of `FilePath`. So a
namespaced `SaveGroupId` is not a workaround; it is the mechanism, used twice already. A new element
would be cleaner to read but is worthless if LaunchBox drops it on its next write.

---

## 2. The decision that shapes everything: substitute, or rewrite

Mehdi's stated intent is to rewrite RetroArch's save function and substitute ours. Both routes work;
they cost differently and it is worth being explicit before committing.

**Substitution — feed the plugin a different identity.** Every RetroArch defect traces to one value:
`ApplicationPath` being the archive at the moment the plugin is asked. `SaveManager` already wraps
`IEmulator` in `AbsPathEmulator` for exactly this class of problem. The same wrapper on `IGame`, carrying
the extracted ROM's path, makes `GetSaves` **and** `AddSaveFile` correct without touching either plugin —
and fixes Dolphin at the same time, which today cannot identify a `.zip` as a disc image at all.

**Rewrite — reimplement the scanner.** Full control over grouping and naming, no dependence on upstream.
The cost is larger than the earlier estimate of ~200 lines suggests: it means reimplementing the four-key
config resolution, the core-name lookup through `info\<core>.info`, the slot parsing, *and* the Saturn
multi-file branch (`.bcr` primary + `.bkr`/`.smpc` companions, `IsSecondarySaveFile`,
`GetCompanionSaveFiles`, `TryComputeSaveSignature` over a sorted manifest). It also forfeits whatever
upstream fixes next — and the shipped RetroArch DLL has already moved once since our decompilation.

**Recommendation: substitution first, rewrite only where it proves insufficient.** Substitution is a
wrapper class plus a resolver for "which entry"; it covers scan, restore, and Dolphin. What it cannot do
is control **grouping** — the plugin always returns `SaveGroupName = "My Save File"` and never knows about
entries. But grouping is the host's job, and the host is ours: `SaveManager` already creates the
`<GameSave>` records and assigns `SaveGroupId`. So per-entry identity is applied on our side, after the
scan, with no plugin change at all.

If the manual tests show substitution cannot express something we need, the rewrite is still open — and
by then we will know exactly which part needs it.

---

## 3. Identity: what a save belongs to

The entity that owns a save becomes **(game, entry)** instead of **(game, version)**.

An entry already has a stable identity in LiteBox, used by favourites and last-played:
`ShortSignature` (content-derived, survives rename and move) + `PathInArchive`. That pair is what goes
into the group identity — subject to §1's third question deciding *where* it goes.

Three cases must keep working unchanged:

- a plain (non-archive) ROM — no entry, identity stays (game, version) as today;
- an archive with a single playable entry — one entry, so per-entry identity is a no-op in practice;
- a game with additional-application versions — unchanged; entry identity composes with, not replaces,
  `AdditionalApplicationId`.

---

## 4. Session capture: the only moment attribution is known

At the end of a session we know, with certainty rather than inference: the game, the emulator, the
launched entry (`LaunchHistoryDb.RecordLaunchRomEntry` already persists it per game), and the extracted
path. At any later scan all of that has to be guessed from file names.

That makes end-of-session the natural place to record what a save belongs to — and it also happens to
solve D2, because the capture can run **before the deletion**.

**Ordering constraint, exact:** `RomExtractor.OnGameExitCleanup()` — which calls `PurgeTmp`, a recursive
delete of the whole `\tmp` band — is invoked at `HostServices.cs:707`. Capture must be inserted before
that line. It also has to run before the LRU evictor can take a persistent `<SIG>` folder, but that only
fires on the *next* extraction, so exit-time capture is early enough.

Note what this does **not** need: it does not need the widened scan of §5. The launched entry is known
from the launch record, so the extracted identity is available directly.

---

## 5. Widened scan: recovering what already exists

Session capture makes new sessions correct. It does nothing for saves already on disk — made before this
work, or made outside LiteBox. That is what widening the scan is for: alongside `basename(ApplicationPath)`,
also match the basenames of the archive's inner ROMs.

Mehdi's question was whether this is possible. It is, and more cheaply than expected: **the inner names
are already persisted without extracting.** `ArchiveListingCache` keeps `FileName`, `PathInArchive` and
`Size` per entry in `rom-archive-cache.db`. Neither the extraction cache nor the archive itself needs to
be touched.

Three constraints on the implementation:

1. **One enumeration, not N.** The obvious version runs `Directory.GetFiles(dir, basename + "*.*")` per
   entry — 200 globs for a 200-ROM arcade set, per scan. Enumerate the save directory **once** and match
   in memory against the set of basenames. Same answer, constant cost.
2. **Never list a cold archive from a save scan.** A never-listed archive would require opening it with
   7z. Acceptable on a page the user opened; not acceptable on anything library-wide. Use only what the
   listing cache already knows, and say so in the UI rather than silently doing work.
3. **Mark provenance.** A group found via the ApplicationPath basename and one found via an entry
   basename are not equally trustworthy. The second is a *candidate* until a session confirms it. The UI
   should be able to tell them apart; silently mixing them is how the current prefix-accident became
   invisible in the first place.

---

## 6. Collisions

Once §5 widens the net, two games whose archives share an inner ROM name will both match the same file.
Nothing detects that today.

Minimum bar: **detect and surface, never silently resolve.** A save file claimed by more than one game is
a fact the user should see, not something we pick a winner for. Session capture then resolves it
naturally — the game that ran is the one that owns what changed — but only for games actually played.

Whether that warrants a global index of claimed paths, or a check performed at scan time, is a design
question worth deciding after §5 shows how often it actually happens.

---

## 7. Slices

| # | Slice | Ends with | Depends on |
|---|---|---|---|
| **S0** | **Observe** — the manual LaunchBox tests of §1, under supervision, on a purpose-built test library rather than the real one | the three answers, written down | — |
| **S1** | **Identity primitive** — the `IGame` wrapper carrying an entry's extracted path; the entry-identity resolver; where identity is stored (per S0) | a save scan that finds an extracted ROM's save, on one hand-made case | S0 |
| **S2** | **Session capture** — the hook before `HostServices.cs:707`, attribution from the launch record, copy into the vault before anything is deleted | playing an archived game leaves a correctly attributed save that survives exit | S1 |
| **S3** | **Widened scan** — cold discovery from the listing cache, single enumeration, provenance marked | saves made before this work become visible and correctly grouped | S1 |
| **S4** | **Collisions** — detection and surfacing of a file claimed by more than one game | ambiguity is visible instead of silent | S3 |
| **S5** | **Backup policy** — triggers (on close, periodic), retention, dirty-check; the settings LB exposes and we currently ignore | automatic backups, bounded | S2 |
| **S6** | **Remote sync** (Drive or other) — separate, opt-in, its own decision | — | S5 |
| **S7** | **RomM back-port** — rebase `romm-server`, map entry identity onto `slot` | per-version saves reach the clients | S3 |

S0 is not ceremony. Three of its answers change what S1 builds, and one of them (does LaunchBox preserve
an unknown field?) cannot be reasoned about from the plugin sources at all — only the obfuscated host
knows.

---

## 8. Open, deliberately

**Objective 3 — richer in-memory ROM data.** Not specified enough to design. What exists today:
`ArchiveListingCache` (entries, sizes), `ArchiveCacheIndex` (what is extracted where),
`ArchiveHistory` (favourites, last played, by archive signature), and `GameSave` sub-entities already
Tier-1 resident. What is missing depends on what S1–S3 turn out to need — building a cache before that is
known would be guessing at the requirement.

**Objective 4 — where backups go.** Deliberately split from *when* they happen (S5). Drive means OAuth,
quotas, cross-machine conflict and revocation; binding a reliability fix to a network subsystem would make
the fix as fragile as the network. And synchronising badly-attributed saves is worse than not
synchronising: S5 before S6, and S6 only once the vault is trustworthy.

**Objective 5 — RomM per-version saves.** Answered upstream: no association exists, it is a known gap,
and `slot` (free text, 255 chars, displayed as a chip, filterable, and — the useful part — the scope of
the device-sync conflict check) is the only carrier. Deferred to S7, where it is a projection choice, not
a modelling problem.

## 9. Risks

1. **LaunchBox interop.** Everything here writes `<GameSave>` records LB also reads and rewrites. If S0
   shows LB drops unknown fields, identity must live in `SaveGroupId` — and even then, an LB rewrite of a
   record we authored has to leave it intact. Worth re-testing after each LaunchBox update, like the
   plugin string-diff in `save-algorithms.md`.
2. **Session capture races the emulator.** Some emulators flush saves at exit; capturing too early gets a
   stale file. The exit sequence already waits on the process, but "the process is gone" and "the file is
   final" are not the same claim.
3. **The prefix accident is load-bearing for someone.** Today a `Secret of Mana.zip` finds
   `Secret of Mana (USA).srm` by accident. Tightening identity will make some currently-visible saves
   move groups. That is a correction, not a regression — but it will look like one, and the migration
   deserves to be visible rather than silent.
4. **`savefiles_in_content_dir` may be nobody's configuration.** D2's severity is real but unmeasured on
   any actual install. S0 should record which RetroArch configuration is in use before D2 drives design.
