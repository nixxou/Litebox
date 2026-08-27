# Emulator save algorithms — what each integration plugin actually does

> **Point 1 of the archive/save investigation.** Establishes, per plugin and to the line, where a save
> lives, what identifies it, and what happens to that identity when the ROM LiteBox launches is a file
> **extracted from an archive** rather than the path stored in the library.
>
> This continues `ExtendDB/docs/lb-save-management.md` (the LaunchBox-side RE: the 3-layer
> architecture, the `<GameSave>` records, the vault, the host lifecycle). That document is not repeated
> here — this one is only about the algorithms, and about the extraction question it left open in §8.

---

## 0. Sources and how far they can be trusted

| Source | Path | Status |
|---|---|---|
| Decompiled plugins (13.27) | `scrapper-project\dumpedlb-savemgmt\1327\` | authoritative — line references below are into these |
| Shipped plugins (LB 14 install) | `G:\LB1326\System\Plugins\<Emu> LaunchBox Integration\` | the DLLs that actually run |
| LiteBox's driver | `Host/Saves/SaveManager.cs` | calls the same plugins in-process |

**Version check performed.** The RetroArch DLL in `G:\LB1326` is 138 752 bytes against 135 168 for the
decompiled 13.27 one — it moved. A string-table diff of the two shows **no new save-, state-, sort- or
config-key strings**, so the save algorithm below is unchanged and the 13.27 decompilation stands. The
size delta comes from elsewhere in the plugin. Re-run that diff after a LaunchBox update before trusting
this document again.

Everything below is read off the decompiled source, not inferred from behaviour.

---

## 1. RetroArch — identity is the **file name**

`Unbroken.LaunchBox.Windows.RetroArch.decompiled.cs`. This is the plugin the archive problem is about;
the other two are structurally immune (§2, §3).

### 1.1 Which emulators it claims

`GetApplicableEmulators`: `Path.GetFileName(emulator.ApplicationPath) == "retroarch.exe"`. Nothing else
is examined — a renamed RetroArch is invisible to save management.

Note it does **not** filter by the game's assigned emulator: RetroArch scans every game handed to it,
matching purely by file name. (Dolphin and PCSX2 do filter — see below.) That is why a game assigned to
an emulator with no plugin still shows RetroArch saves.

### 1.2 Where the save directory comes from — `GetGameSaveDirectory` (l. 1517)

Reads `retroarch.cfg` **beside the exe** (a `--config` override is not honoured), then, in order:

1. `savefiles_in_content_dir == "true"` → **`Path.GetDirectoryName(ApplicationPath)`**
   — i.e. the folder of the path *stored in the library*.
   Otherwise → `savefile_directory`; a `:\` prefix is rebased onto RetroArch's folder resolved against
   `NamingHelper.RootFolder`.
2. `sort_savefiles_by_content_enable == "true"` → append **`Path.GetFileName(Path.GetDirectoryName(ApplicationPath))`**
   — the *name of the parent folder* of that same stored path.
3. `sort_savefiles_enable == "true"` → append the **core display name**: the core is pulled from the
   effective command line by `-L "cores\(?<core>[^.]+).dll"`, then `info\<core>.info` is read for
   `corename = "..."` (`snes9x_libretro` → `Snes9x`).

States use the same shape with `savestate_directory` / `savestates_in_content_dir` /
`sort_savestates_by_content_enable` / `sort_savestates_enable` (l. 1669).

> **The trap already paid for once:** `NamingHelper.RootFolder` is a public static of the obfuscated
> core that LaunchBox.exe sets at boot. Unset, a `:\saves` directory resolves to nothing and `GetSaves`
> returns empty *silently*. LiteBox sets it by reflection in `HostBoot.SetLaunchBoxCoreRootFolder`.

### 1.3 How files are matched — the decisive line

```csharp
string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(item.ApplicationPath);
string[] files = Directory.GetFiles(gameSaveDirectory, fileNameWithoutExtension + "*.*");   // l. 1209
```

A **prefix wildcard**, not an exact match. Then:

- exactly one file → that is the save;
- several → prefer `.srm`, then `.mcr`, else take them all;
- states: `fileNameWithoutExtension + ".state*"` (l. 1276), slot parsed from the suffix —
  `.state` = 0, `.state.auto` = -1, `.stateN` = N. **Only slots 0–9 exist** in
  `GetPotentialSaveSlots`, so `.state10` and up are never scanned.
- Pass 1 over `AdditionalApplications` (l. 1190), pass 2 over `Games` whose `ApplicationPath` no app
  already covered (l. 1323) — that is the deduplication.
- Default group names come from the plugin, not the user: `"My Save File"` / `"My Save State"`.

Everything therefore rests on one assumption: **the basename of the library's `ApplicationPath` is the
name RetroArch used to name the save.** That assumption is what extraction breaks.

### 1.4 Writing back — `AddSaveFile` (l. 853–884)

Destination is rebuilt from the same basename:

```csharp
string text = additionalApplication?.ApplicationPath ?? gameById.ApplicationPath;
// state:
text3 = Path.Combine(saveStateDirectory, Path.GetFileNameWithoutExtension(text) + ".state" | ".stateN" | ".state.auto");
// save:
text3 = Path.Combine(gameSaveDirectory, Path.GetFileNameWithoutExtension(text) + Path.GetExtension(source));
```

So restoring **renames the file to the library path's basename**. Under extraction that writes a name
the emulator will not read back (§4).

### 1.5 Saturn — the multi-file model

`.bcr` primary plus `.bkr`/companions: `IsSecondarySaveFile`, `GetCompanionSaveFiles`,
`TryGetSaveGroupInfo` with `saveGroupId = "saturn-<basename>"` (l. 1448), and
`TryComputeSaveSignature` = MD5 of a sorted `name|MD5` manifest. Still name-derived, so it inherits
every extraction consequence below.

---

## 2. Dolphin — identity is the **disc content**

`TryGetDiscId(romPath, …)` (l. 2025): reads the disc ID **out of the file's bytes**, falling back to
running `DolphinTool.exe` against it (l. 2109). Saves are then addressed by that ID:

- GameCube: `User\GC\<region>\<DiscId>\*.gci` and Card A/B (`<DiscId>*.gci`);
- Wii: NAND folders `User\Wii\title\<high>\<low>\` → `IsDirectory = true` (folder backups become `.7z`);
- states: `User\StateSaves\<DiscId>.sNN`.

It filters to its own emulator (`ShouldScanAdditionalApplication` tests `EmulatorId`).

**Consequence:** renaming, moving or extracting the ROM changes nothing — the same bytes yield the same
disc ID, and saves never live next to the ROM. Dolphin is immune to the naming problem *provided the
file it is handed can be read as a disc image*. See §4 for the case where it cannot.

---

## 3. PCSX2 — identity is the **memory card**

Saves live inside memcards (`.ps2` file or folder); the plugin parses the card and enumerates each PS2
save directory. Identity is `SaveGroupId = "pcsx2:<card>:<dirInCard>"` with
`UseSaveGroupIdForPersistedMatch = true`, because the `FilePath` (the card) does not identify a save.
`IsSaveContainer = true` keeps the card alive through a delete; `TryBackupSave` extracts the directory;
`IsSaveActive` asks whether that card is the one PCSX2 is currently configured with.

The ROM path is used **only as a display-name fallback** when the game title is blank or a corrupted
numeric string (l. 3834). Nothing about save identity touches it.

**Consequence:** PCSX2 is fully immune. The card is configured in the emulator, not derived from what
was launched.

---

## 4. What happens when the ROM is extracted from an archive

This is the part `lb-save-management.md` §8 flagged and did not resolve. Two facts make it concrete:

- LiteBox launches the emulator against `<cacheRoot>[\tmp]\<SIG>\<P|F>[\<subdir>]\<innerName>`
  (`ArchiveCachePlacement`), while `SaveManager` hands the plugin the real `IGame` — whose
  `ApplicationPath` is **the archive**.
- `RomExtractor` contains no save logic whatsoever (grep over `Host/Rom/`: zero matches).

So two different paths are in play at once, and the plugins react differently.

### 4.1 RetroArch, `savefile_directory` set (the common setup)

| | path used |
|---|---|
| RetroArch at runtime writes | `<savefile_directory>\<innerName>.srm` — named after the **content it loaded** |
| the plugin looks in | `<savefile_directory>` for `<archiveBasename>*.*` |

Because the match is a **prefix** wildcard, this *accidentally works* whenever the inner name begins
with the archive name — `Secret of Mana.zip` does find `Secret of Mana (USA).srm`. It fails whenever it
does not (`Sonic 1.zip` → `Sonic The Hedgehog (USA).srm`), and it is wrong by construction for a
multi-entry archive, where one prefix collects **every** entry's save into a single group.

The save file itself is safe. What is unreliable is finding it, and what is outright wrong is grouping it.

### 4.2 RetroArch, `savefiles_in_content_dir = true`

| | path used |
|---|---|
| RetroArch at runtime writes | beside the **extracted** ROM → inside `<cacheRoot>[\tmp]\<SIG>\…` |
| the plugin looks in | `Path.GetDirectoryName(ApplicationPath)` → the **library folder** holding the .zip |

Never a match — the two directories are unrelated. And the save now lives in a folder LiteBox deletes:

- `ArchiveCacheEvictor.PurgeTmp` → `Directory.Delete(sub, recursive: true)` on **game exit**;
- the LRU evictor → `Directory.Delete(<SIG>, recursive: true)` when the cache is over budget;
- a RAM-disk extraction disappears at unmount.

`\tmp` is used when the unpacked size falls outside `[CacheMinMb = 100, CacheMaxMb = 8000]`. **The floor
is 100 MB**, so NES, SNES, Game Boy, Mega Drive and most arcade sets are below it and land in `\tmp` by
default. In that configuration the save is destroyed seconds after quitting, every time, with no error.

`sort_savefiles_by_content_enable` makes it strictly worse: the subfolder is the *parent folder name* of
the content — the `<P|F>` or `<subdir>` segment for RetroArch, the platform folder for the plugin.

### 4.3 Restore / set-as-active, either RetroArch configuration

`AddSaveFile` writes `<archiveBasename> + ext` (§1.4). RetroArch will look for `<innerName> + ext`. So a
restore performed through Edit Game → Game Saves, the web themes, or the RomM API **lands a correctly
copied file under a name the emulator never reads**. It reports success and changes nothing.

### 4.4 Dolphin

`TryGetDiscId` is handed `ApplicationPath` — the archive. A `.zip` is not a disc image: the byte read at
the fixed offset yields garbage, `TryNormalizeDiscId` fails, and `DolphinTool.exe` fails too. The plugin
logs *"Failed to detect Disc ID"* and returns nothing.

So archived GameCube/Wii games get **no save management at all** — but nothing is lost either, because
Dolphin writes into its own `User\` tree regardless of where the ROM came from. Handing the plugin the
*extracted* path would make it work immediately.

### 4.5 PCSX2

Unaffected. The memcard is configured in the emulator and has no relationship to the launched path.

### 4.6 Summary

| Plugin / config | Save found? | Save survives? | Restore works? |
|---|---|---|---|
| RetroArch, `savefile_directory` | by luck (prefix), wrong groups on multi-entry | yes | no (wrong name) |
| RetroArch, `savefiles_in_content_dir` | never | **no — deleted with the cache** | no |
| Dolphin, archived ROM | never (no disc ID from a zip) | yes | no |
| PCSX2 | yes | yes | yes |

Three distinct defects, not one: **identity** (the name the plugin derives ≠ the name the emulator used),
**durability** (saves written into a folder LiteBox deletes), and **multiplicity** (one library entry,
N inner ROMs, one `<GameSave>` slot).

---

## 5. What this pins down for a fix

Not a proposal — just what the algorithms make true or false, so a design cannot be argued against them:

1. **The whole problem is one substitution.** Every RetroArch defect above comes from `ApplicationPath`
   being the archive at the moment the plugin is asked. `SaveManager` already wraps `IEmulator` in
   `AbsPathEmulator` to force an absolute path; the same trick on `IGame` with the extracted path makes
   RetroArch and Dolphin both correct with no change to either plugin.
2. **That substitution needs the extracted name when nothing is extracted.** A scan happens with the
   game not running and the cache possibly evicted, so the last-launched inner name has to be
   remembered per (game, entry). `ArchiveHistory` already persists exactly that shape, keyed by the
   archive's short signature.
3. **Identity fixes do not fix durability.** §4.2 destroys saves regardless of what the scanner
   believes. That is a separate mechanism — leave the save out of a folder we delete, or take a copy
   before deleting it.
4. **Renaming the extracted file to the archive's name is not the shortcut it looks like.** It would
   align RetroArch, but MAME needs the romset name intact, it collapses every entry of a multi-entry
   archive onto one save, and `OutputName = Title` already forces `\tmp` precisely because a renamed
   extraction cannot be shared in the persistent cache.
5. **Per-entry identity already exists in LiteBox** — `ShortSignature` + `PathInArchive`, used by
   favourites and last-played. Saves are the only ROM-scoped feature not using it.
