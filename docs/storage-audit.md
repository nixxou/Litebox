# Audit du stockage — LiteBox (+ ExtendDB) — 2026-07-26

État des lieux exhaustif de TOUT ce que LiteBox persiste, où, sous quelle forme, et les pistes de
standardisation. Déclencheur : la découverte tardive de `litebox-options.db` pendant le chantier
locks/3D — trop de stores épars pour garder le fil.

---

## 1. Les canaux de persistance (du plus structurel au plus local)

| # | Canal | Fichier(s) | Nature | Règle d'usage |
|---|-------|-----------|--------|----------------|
| 1 | **XML LaunchBox** | `Data\*.xml`, `Data\Platforms\*.xml`, `Settings.xml` | bibliothèque partagée | UNIQUEMENT les champs du **schéma LB** (le save du vrai LB reconstruit à schéma fixe → tout élément étranger est JETÉ) |
| 2 | **Op-log** | `Core\litebox\LiteBox.pending.db` | journal WAL | tampon des écritures vers les XML ; rejoué au boot, flushé à un moment sûr, vidé. PAS un stockage |
| 3 | **Options-db** | `Core\litebox\litebox-options.db` | store durable EAV | LA bdd guid-keyée pour les données par-entité LiteBox-own + clés Settings « problématiques » selon la version LB. Jamais flushée vers les XML |
| 4 | **LiteBox.ini** | `Core\litebox\LiteBox.ini` | config globale | ~28+ clés plates (LiteBoxConfig, merge-save) + sections (`[Rom]`…) |
| 5 | **JSON par feature** | `Core\litebox\*.json` | configs/données | 1 fichier / feature (détail §3) |
| 6 | **SQLite par feature** | `launch-history.db`, `rom-archive-cache.db`, `rom-archive-history.db` | données/caches | dbs dédiées, `user_version` gaté |
| 7 | **Caches disque** | `cache\`, + dirs racine (§4) | rebuildable | thumbs, GLB 3D, RA, badges, webview2… |
| 8 | **ADS par fichier** | streams NTFS sur les images/roms | méta du FICHIER | `:crc32`, `:info`, `:lock` (partagé plugin), `:lb.dupcheck` ; fallback sidecar `.ads\` |
| 9 | **Logs** | `litebox-debug.log`, `litebox-store.log`, `mame-submit.log`, `saves-diag.log` | diagnostic | |

---

## 2. Inventaire d'options-db (clés actuellement possibles)

`options(scope, entity_id, key, value)` — sparse, row = option posée. Consommateurs et clés :

### scope `global`
- **ProblemKeys** (routage conditionnel : en db SEULEMENT si le LB détecté < 13.28, sinon Settings.xml
  partagé) : `StartupScreenPostLaunchDisplayTime`, `ShutdownScreenPostReadyDisplayTime`,
  `ForceFrontendFocusOnShutdown`, `MonitorStartupShutdownWithProcess`.
- **Modules** : `Module.base`, `Module.rom`, `Module.retroachievements`, `Module.parental`,
  `Module.web` (LbModules ; row absente = défaut du module).

### scopes `game` / `emulator` (overrides tri-state : pas de row = hérite)
Résolution jeu → émulateur → global (`LiteBoxOption.ResolveBool/ResolveString`) :
- **Startup/Shutdown** : `StartupStayOnTop`, `StartupLoadDelay`, `StartupProgressBar`,
  `HideMouseCursorOnStartupScreens`, `StartupScreenPostLaunchDisplayTime`,
  `ShutdownScreenPostReadyDisplayTime`, `ForceFrontendFocusOnShutdown`,
  `HideAllNonExclusiveFullscreenWindows`, `AggressiveWindowHiding`, `HideMouseCursorInGame`,
  `ExitScreenEagerMs`, `ExitAutoHotkeyScript`.
- **Pause** : `PauseHotkey`, `PadPauseEnabled`, `PadPauseButton`, `PauseTarget`, `PauseFreezeTree`,
  `PauseScreenFading`, `PauseScreenMuting`, `PauseScreenFreezeTiming`, `PauseScreenFreezeOffsetMs`,
  `PauseExitKill`, `PauseExitKillAhk`, `PauseAutoHotkeyScript`, `ForcefulPauseScreenActivation`.
- **Capture** : `ScreenCaptureKey`, + SmartCapture (9) : `SmartCaptureEnabled`, `SmartCaptureUseFps`,
  `SmartCaptureUseSize`, `SmartCaptureCombine`, `SmartCaptureMinFps`, `SmartCaptureSustainMs`,
  `SmartCaptureMinSizePct`, `SmartCaptureTitle`, `SmartCaptureStopOnWindowClose`.

### scope `game` — données (pas des overrides d'option)
- `FieldLocks` = JSON `{"title":"…","genre":""}` — locks de métadonnées, **contrat partagé avec
  ExtendDB** (LockStorage l'attaque en SQLite direct sous vrai LB quand `Core\litebox\` existe).
- `Model3dImages` = JSON `{"front":"Images\\…"}` (chemins relatifs racine LB) — sélections d'images 3D.

### scope `platform`
Déclaré (`LiteBoxOption.ScopePlatform`) mais **aucune clé en service à ce jour**.

⚠ Aucun GC : les rows d'entités supprimées restent (bénin, à balayer un jour post-GameCache).

---

## 3. Les JSON de `Core\litebox\` (le « épars » n°1)

| Fichier | Contenu | Keying | Verdict standardisation |
|---------|---------|--------|--------------------------|
| `media-layout.json` | layout images panel droit + options dup + Show3dBox | global | OK (config structurée) |
| `youtube.json` | config intégration YouTube | global | OK |
| `parental-lists.json` | listes parental | global | OK |
| `ra-panel.json` | config panneau RA | global | OK |
| `game-suggester.json` | config suggester | global | OK |
| `rom-profiles.json` | profils ROM par (plateforme, émulateur), structures imbriquées | (platform, emu) | OK (trop structuré pour l'EAV) |
| `model-defaults.json` | presets 3D par plateforme — table FIGÉE shippée | nom plateforme | OK (read-only) |
| `ra-platform-overrides.json` | plateforme → clé console RAHasher | **NOM** de plateforme | **→ MIGRER en options-db scope `platform`** (suivrait les renames, 1er usage du scope) |
| `rom-selection.json` | pick ROM en attente par (jeu, version) | guid jeu | **→ candidat options-db scope `game`** (sémantique « par client » à trancher : web = localStorage) |
| `search-history.json` | historique de recherche | global (état client) | OK |
| `saves-vault.json` | index des backups vault (pointe des zips) | entrées | OK (journal d'artefacts) |
| `metadata-locks.json` | (ancien store des locks) | guid jeu | **MIGRÉ** → options-db (`.migrated`) |

Autres fichiers racine `litebox\` : `LiteBox.ini` (§1.4), `pending-cleanup.txt` (état interne),
`LaunchBox.Extended.Metadata.db` (DB étendue téléchargée), logs (§1.9).

## 4. Dossiers de cache — INCOHÉRENCE de placement

Sous `cache\` : téléchargements ExtDb, `related-thumbs`, snapshot suggester, `thumbs\degraded`,
`3d` (GLB). **Mais à la RACINE de `litebox\`** : `romcache`, `emumovies`, `steam`, `ra-cache`,
`ra-badges`, `store-ach-cache`, `store-ach-badges`, `webview2-yt`, `webview2-yt-page`,
`webview2-kiosk` (+ `thirdparty`, `config`, `web` qui eux n'y sont pas des caches).
→ **Standardiser : tout cache rebuildable sous `cache\`** (DataMaintenance et « clear cache »
deviennent triviaux). Migration = move au boot + LegacyCleanup.

## 5. ADS par fichier (sain, ne pas toucher)

`:crc32`, `:info` (FileMetaStore), `:lock` (lock d'image, partagé avec le FileDeletePatch du plugin),
`:lb.dupcheck` (anti-dup). La donnée appartient au FICHIER (suit les renames/moves de LB — leçon Set
Number) — c'est le bon store. Fallback non-NTFS : sidecars `.ads\`.

## 6. Côté plugin ExtendDB (sous vrai LB uniquement)

Racine plugin : `ExtendDBConfig.json`, `LaunchBox.Extended.Metadata.db`, `MediaSecret.bin`,
`debug.log`. `config\` : `ArchiveMgs.json`, `GameSuggester.json`, `metadata-locks.db.migrated`
(migré vers options-db LiteBox). `cache\` : `extenddb-cache.db`, `launch-history.db`.
**Partagés avec LiteBox** : `litebox-options.db` (row `FieldLocks`), ADS `:lock`, PIN BigBox
(Settings LB), format sidecar `.ads`.

## 7. Le triple-home des réglages GLOBAUX (le « épars » n°2)

Un réglage global peut vivre dans **trois** endroits aujourd'hui :
1. `Settings.xml` partagé (via LbSettingsStore — visible/éditable par le vrai LB) ;
2. options-db `global` (clés problématiques + modules) ;
3. `LiteBox.ini` (~28+ clés LiteBox-only : debug, GenCacheSelection, CleanThumbs*, ThumbAlpha*, …).

Règle actuelle implicite : partagé-avec-LB → 1 ; version-sensible → 2 ; LiteBox-only → 3 (ini).
**Incohérence** : les flags de modules (LiteBox-only) sont en 2, les CleanThumbs* (LiteBox-only) en 3.

## 8. Pistes de standardisation — état

**FAIT (2026-07-26)** : R1 (partiel), R5, R6 + le registre de clés.
- `Host/Data/OptionKeys.cs` : registre (scopes, type, **Hot/Cold**, owner, SharedWithPlugin) ; `LiteBoxOptionsDb`
  valide chaque accès (log en prod, **throw sous `--debug`**), accesseurs typés, `AllOf`, `user_version`.
- Cache Hot write-through (préchargé au boot, un SELECT par clé Hot) + `RevalidateHotCache` sur activation.
  Règle : Hot = lu en liste/recherche/détail ; Cold = launch-time/ponctuel (défaut). `FieldLocks` DOIT rester
  Cold (le plugin écrit la row depuis un autre process).
- GC `SweepOrphans` par scope depuis ThumbGc (opt-out `CleanOptionsDb`), **durci** : un scope n'est balayé que
  si son live-set a été énuméré avec succès ET est non vide (sinon une exception avalée effacerait toutes les
  rows du scope).
- Migrés vers l'options-db : `rom-selection.json` → clé `RomSelection` (option B, sémantique par-client
  inchangée), `ra-platform-overrides.json` → `RaConsoleKey` (scope platform, sentinelle `"-"`),
  `metadata-locks.json` → `FieldLocks` (déjà fait au chantier locks). Chaque migration est one-shot,
  no-clobber, avec rename `*.migrated`.
- Vérification : 9 sous-agents indépendants (un par clé/famille) → tous SAFE ; plus un diff exhaustif
  registre↔littéraux du code qui a trouvé le seul trou réel (`SmartCaptureShowBorder`, corrigé) ; run live
  `--debug` (Strict) : aucune clé inconnue, aucune exception, migrations RA + locks vérifiées en base.

**RESTE À FAIRE** (chacun son chantier) :

- **R1 — Par-entité guid-keyé → options-db.** Fait pour locks/3D. À migrer :
  `ra-platform-overrides.json` (scope platform) ; à trancher : `rom-selection.json`.
- **R2 — Global LiteBox-only : UNE maison.** Proposition : options-db `global` devient la cible et
  `LiteBox.ini` est réduit au bootstrap (flags lus avant l'ouverture de la db : debug, chemins).
  Migration progressive clé par clé, `ini` → db, avec seed à la ProblemKeys.
- **R3 — Caches : tout sous `cache\`.** Move one-shot + LegacyCleanup (§4).
- **R4 — Config structurée par feature : JSON assumé**, mais avec `ConfigVersion` systématique
  (pattern ExtendDB versioning) — aujourd'hui inégal.
- **R5 — Cache RAM write-through dans LiteBoxOptionsDb** (chargement intégral au boot, table sparse
  → ~ms ; Get = hit dico, Set = dico + db sous verrou ; recheck mtime pour le cas LB-et-LiteBox
  simultanés). Accès objet optionnel ensuite (`HostGame.GetOption/SetOption`).
- **R6 — GC options-db** : sweep des rows orphelines (guid disparu) dans le passage de nettoyage
  auto post-GameCache.
- **ADS (R0)** : ne pas toucher — bon store pour la donnée par-fichier.
