# Audit du stockage — LiteBox (+ ExtendDB) — 2026-07-26 (revu 2026-08-26, LiteBox 0.9.3)

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
  `Module.web`, `Module.monitors`, `Module.rules` (LbModules ; row absente = défaut du module).
- **Monitor Profiles** (Host\Monitors, ajouté 2026-08) : `MonitorProfiles` (JSON, la liste entière —
  chaque profil porte aussi ses scripts avant/après application : `ScriptBefore`/`ScriptAfter` +
  leur langue `cs`|`ahk`, 0.9.3),
  `MonitorRestorePoint` (JSON, l'état d'avant le 1er profil — persisté pour survivre à un crash),
  `MonitorRestoreHotkey` + `MonitorRestoreHotkeyGlobal`, `MonitorWebEndpoints`, `MonitorLaunchDelay`,
  `MonitorNvidiaApply` (opt-in, absent = OFF : capture NVIDIA toujours, écriture et UI verte seulement à true).

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

### scopes `game` / `emulator` / `version` — assignations Monitor Profiles
- `MonitorProfileAssign` = `"none"` | `"custom"` | un `MonitorProfile.Id` ; `MonitorProfileCustom` =
  JSON d'un profil stocké en ligne (utilisé quand l'assignation vaut `"custom"`). Résolution
  version → jeu → émulateur, précédée du one-shot « Run next game as » (mémoire vive uniquement).
  **Premier usage du scope `version`** (additional version), déclaré dans `LiteBoxOption`.

### scope `emulator` — Launch rules (module "rules", ajouté 2026-08)
- `LaunchRules` = JSON `LaunchRule[]` ordonné, appliqué à la ligne de commande avant le spawn
  (portage action-par-action de BigBoxProfile). **Attachement ÉMULATEUR SEUL** (décision 08/2026,
  converge sur la forme de BBP : un pipeline par émulateur) — le ciblage par jeu passe par un
  argument-marqueur dans les paramètres personnalisés du jeu, intercepté par les sondes, et les
  jeux de store sortent du périmètre par construction. Le stockage garde ses colonnes de scope,
  donc élargir plus tard reste trivial.
- Actions au 0.9.3 : Prefix, Suffix, ChangeExe, ChangeRomPath, Replace (+ variables), ReplaceInFile,
  CreateFile, HidDetect, CopyFile (option RAM disk via l'infra de l'extracteur), UseFileContent,
  MonitorProfile, Script (C#/Roslyn), AhkScript, RunAsAdmin, AdminCmd. Les charges lourdes vivent
  dans des champs JSON de la règle : `HidData` (réglages du détecteur + matchers), `VariablesData`
  (les variables de la règle), `MonitorCustomData` (profil moniteur en ligne), les corps de scripts.

### scope `game` — Run as administrator (0.9.3)
- `RunAsAdmin` = `"true"` (row absente = normal) — spawn élevé pour CE jeu, écrans start/pause/
  game-over désactivés pour ce lancement (murs UIPI). Le jumeau côté émulateur est une règle
  `RunAsAdmin`, sondée, donc pas de clé db.

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

## 4. Dossiers de cache — RANGÉS sous `cache\` (R3 FAIT 2026-07-27)

Tout cache rebuildable vit désormais sous `litebox\cache\`. Les 10 dossiers qui traînaient à la racine
(`romcache`, `emumovies`, `steam`, `ra-cache`, `ra-badges`, `store-ach-cache`, `store-ach-badges`,
`webview2-yt`, `webview2-yt-page`, `webview2-kiosk`) ont été relocalisés ; ils rejoignent ceux déjà en
place (`3d`, `thumbs`, `related-thumbs`, `thumbs\degraded`, staging ExtDb, snapshot suggester).
- **Chokepoint** : `LiteBoxPaths.CacheDir(name)` → `litebox\cache\<name>` (nouveau, à côté de `Cache`).
  Les 14 sites `Dir("x")` de ces dossiers sont passés à `CacheDir`. **Nouveaux caches = `CacheDir`,
  jamais `Dir`.**
- **Migration** : `Host/Install/CacheReorg.cs`, one-shot au boot (après LegacyCleanup, AVANT toute
  ouverture de cache). MOVE (pas delete — évite un re-téléchargement massif des badges après upgrade),
  atomique même-volume ; si le `cache\` cible existe déjà, la copie racine périmée est droppée.
  Vérif live G:\LB : 6 dossiers relocalisés, racine propre, 2ᵉ boot = no-op, `--debug` sans erreur.
- **DataMaintenance** : champ `Rel` sur les 10 rows (`FullPath` = `cache\<name>`, `Name` = affichage) ;
  la row parapluie `cache` couvre tout l'arbre (les rows individuelles restent pour un clear granulaire).
- Restent à la racine (NON-caches, correct) : `thirdparty`, `config`, `web`, `web-assets`,
  `admin-launch` (échange avec le helper élevé, 0.9.3 : `launch.cfg`/`launch.pid`/`launch.err`,
  éphémères, une paire par lancement admin).

### 4bis. Hors `Core\litebox\` — ce que le module "rules" pose ailleurs (0.9.3)

| Emplacement | Contenu | Cycle de vie |
|---|---|---|
| `<LB>\ThirdParty\Hid\` | `HidSharp.dll`, `SDL2-CS.dll`, `SDL2.dll` (natif), `InTheHand.Net.Personal.dll` | déployé par `NativeInstaller` (payload embarqué / `.api` en vrac), chargé à la demande ; **LiteBox-only** → supprimé par la désinstallation |
| `<LB>\ThirdParty\Roslyn\` | les 4 assemblies Microsoft.CodeAnalysis (scripts C#) | idem |
| *(rien dans `Core\`)* | SharpDX (DirectInput) et AutoHotkey sont ceux que **LaunchBox fournit** — référencés, jamais déployés : y écrire écraserait les siens | — |
| `%TEMP%\litebox-rules-ahk\` | scripts AHK générés (prélude + corps + épilogue) et leur fichier résultat | supprimés après exécution ; un script résident garde le sien jusqu'à sa mort |
| `%TEMP%\litebox-rules-m3u\<hash>\` | m3u temporaires réécrits (ChangeRomPath / CopyFile) — nom de fichier d'origine conservé | par lancement |
| Planificateur de tâches | `LiteBox_AdminLaunch_<hash>` et `LiteBox_RomExtractor_RamDisk_<hash>` (hash = FNV-1a du Core de CETTE install) | créées à la demande (1 UAC) ; la désinstallation propose de les retirer, sinon inertes |

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

**R1 / R4 / R5 / R6 : TERMINÉS** (détail ci-dessus). `ra-platform-overrides.json` et
`rom-selection.json` sont migrés ; il ne reste aucun store par-entité guid-keyé hors options-db.

**R2 — CLÔTURÉE (2026-07-27), sans migrer les globaux plats. Règle actée.**
Décision d'architecture (Mehdi) : la base (`litebox-options.db`) ne gagne son droit d'exister QUE pour
trois catégories — **ProblemKeys** (routage par version LB), clés **Hot** (cache RAM sur les chemins
liste/recherche/détail), et données **assignables à une entité** (game/plateforme/émulateur, l'EAV).
Un défaut GLOBAL plat, LiteBox-own et Cold, ne coche AUCUNE des trois → il reste dans `LiteBox.ini`.

Une première tentative (phase A) avait déplacé les défauts globaux de gameplay vers la db, au motif que
leur cascade jeu→ému→global était « à cheval sur deux stores ». **Revertée** : ce n'était pas une faute
— chaque store faisait ce pour quoi il est bon (l'EAV pour les paliers entité, l'ini pour le défaut
plat). La preuve que ces clés étaient ini-shaped : la db supprime les rows à valeur vide, ce qui avait
forcé une sentinelle `Disabled` pour distinguer « vidé » de « jamais défini » sur les hotkeys —
autrement dit, du code pour faire IMITER l'ini par la db. L'ini le fait nativement.

État final (vérifié live sur G:\LB, --debug, boot + idempotence) :
- **Paliers PAR-ENTITÉ (game/emulator)** des options de gameplay : restent en `options-db` (scope
  `GameEmu`) — c'était déjà le cas avant, c'est légitime (critère 3). Édités par
  `LiteBoxGameplayEditor` / `SmartCaptureEditor`.
- **Défaut GLOBAL de chacune** : dans `LiteBox.ini`, résolu par repli après les deux paliers db.
- **Nouveau `Host/Gameplay/GameplayDefaults.cs`** : SSOT (clé, défaut) + passe de boot `Seed(ini)` qui
  (a) **migre à l'envers** toute row global rémanente de phase A vers l'ini via
  `LiteBoxOptionsDb.DrainGlobalKeys` (contourne la validation car ces clés ne sont plus déclarées en
  global), en préservant la valeur perso, puis (b) **écrit le défaut** de toute clé absente. Garantit
  qu'AUCUNE clé n'est cachée : tout gameplay-global est visible dans l'ini (règle Mehdi : plus de clé
  « hidden » définie seulement en code, ex. `SmartCaptureShowBorder`, désormais `=false` dans l'ini).
  Vérif live : 20 défauts écrits + 8 valeurs perso restaurées, base réduite aux 2 ProblemKeys, 2ᵉ boot
  = no-op.
- **Deux exceptions non seedées** : `PauseHotkey` / `ScreenCaptureKey`. Leur défaut correct est
  « absent = hériter de la touche configurée dans LaunchBox » ; aucune valeur ini fixe ne peut
  l'exprimer (`=` vide voudrait dire « désactivé »). Restaurées si une row phase-A existe, jamais
  dotées d'un défaut. **Décision Mehdi 2026-07-27 : on garde l'héritage LB** (elles restent absentes
  de l'ini) — l'unique entorse assumée à la règle « tout visible avec défaut ».
- **Durcissement conservé** : un `Open()` raté relance sous `--debug` (trouvé à la dure — un ordre
  d'init statique dans `OptionKeys` s'était manifesté par une ligne de log pendant que toutes les
  options repartaient au défaut).

**RESTE À FAIRE** :

- **R2 — rien.** Les toggles globaux ordinaires (`UseImageCache`, `UseGameCache`, `CleanThumbs*`,
  `ThumbAlpha*`, `DetailLoadDelayMs`, `GameRunning*`…) sont des propriétés typées de `LiteBoxConfig`
  consommées via l'instance partagée `_cfg` : les migrer serait soit une inversion de couche, soit du
  churn sur des dizaines de sites, pour zéro palier par-entité à réunifier. **Écartée** (voir la règle
  ci-dessus). L'état d'UI (`Win*`, `Sort*`, `Col.*`…) reste évidemment dans l'ini.
- **R3 — FAIT (2026-07-27).** Tous les caches sous `litebox\cache\` (`LiteBoxPaths.CacheDir` +
  migration `CacheReorg` au boot). Voir §4. Standardisation du stockage : **plus rien en attente.**
- **ADS (R0)** : ne pas toucher — bon store pour la donnée par-fichier.
