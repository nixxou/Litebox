# Les sauvegardes, de bout en bout

> Document de référence. Il répond à « comment ça marche, exactement », côté LaunchBox et côté LiteBox,
> avec les formats de fichiers réels et des scénarios pas à pas.
>
> **Les trois autres documents du dossier, et ce qu'ils font :**
> - `ExtendDB/docs/lb-save-management.md` — la rétro-ingénierie initiale de LaunchBox (13.26/13.27).
>   ⚠ Sa section « Le vault — OBSERVÉ » est **périmée** : voir §3.5 ici.
> - `docs/save-algorithms.md` — l'algorithme de chaque plugin, à la ligne près.
> - `docs/save-system-plan.md` — le plan de réparation (S0→S7) et où on en est.
>
> Ce document-ci ne remplace aucun des trois : il les relie.

---

> ## ⚠ État au 2026-08-27 : la partie LiteBox est en cours de réalignement
>
> Décision prise ce jour : **coller à 100 % à LaunchBox d'abord**, et ne réintroduire les écarts qu'ensuite,
> sur une base dont on sait qu'elle est juste.
>
> Ce qui a été fait dans cette direction :
> - `saves-vault.json` est **supprimé**. Le dossier est la vérité, comme chez LaunchBox. Le §4.2 qui décrit
>   ce fichier ne décrit plus rien d'existant — il reste comme trace de ce qui a été essayé et pourquoi.
> - Le nommage du fichier vault suit LaunchBox : basename de la **ROM**, extension normalisée, le vrai nom
>   dans `OriginalFileName`.
> - On écrit la ligne `<GameSave>` de backup à sa forme.
> - Le `Label` par version a disparu (LaunchBox n'a pas ce champ) et le `Md5` n'est plus en cache.
>
> Ce qui est **mis de côté**, pas abandonné :
> - la passe par **entrée d'archive** (`SaveManager.EntryScan`, à `false`). Toute la machinerie est intacte.
> - les sous-dossiers **`Manual\` / `Auto\`**, seul porteur possible de la distinction automatique/manuel.
>   Conséquence immédiate : **la rétention ne peut plus épargner un backup fait à la main.**
>
> Le §5 (les différences) décrit donc un état dépassé sur plusieurs lignes. Il sera réécrit quand la parité
> sera établie et vérifiée, pas avant — le réécrire pendant que la conception bouge ne servirait à rien.

---

## 1. Le vocabulaire, d'abord

Quatre mots reviennent partout et se ressemblent. Les confondre rend tout le reste incompréhensible.

| Terme | Ce que c'est | Où ça vit |
|---|---|---|
| **Save active** | Le fichier que l'émulateur lit et écrit *maintenant*. `Secret of Mana (USA).srm` dans le dossier saves de RetroArch. | Chez l'émulateur |
| **Groupe** | L'unité d'affichage et d'identité. « My Save File » de tel jeu. Un groupe a **une** save active (ou zéro) et **N** backups. | Un `<GameSave>` dans le XML de plateforme |
| **Backup / version** | Une copie figée d'une save active, prise à un instant donné. | `LB\Saves\<Plateforme>\` |
| **Entrée** (*entry*) | Une ROM **à l'intérieur** d'une archive. `Sonic (USA).smd` dans `Sonic.zip`. | Dans l'archive ; extraite au lancement |

Le piège central du dossier tient en une phrase : **une save appartient à une entrée, mais LaunchBox
raisonne en jeux.** Tout ce qui suit découle de ça.

---

## 2. L'architecture en trois couches

C'est valable pour LaunchBox comme pour LiteBox, parce que LiteBox réutilise littéralement les deux
couches du bas.

```
┌─────────────────────────────────────────────────────────────────┐
│  COUCHE 3 — L'HÔTE          LaunchBox.exe     │     LiteBox     │
│  Groupe, nomme, persiste, sauvegarde, restaure. Pilote la 2.    │
│  → obfusqué, non observable │  → Host\Saves\SaveManager.cs      │
├─────────────────────────────────────────────────────────────────┤
│  COUCHE 2 — LES PLUGINS D'INTÉGRATION        (partagée)         │
│  "RetroArch LaunchBox Integration", Dolphin, PCSX2…             │
│  Savent OÙ vivent les saves d'un ému et COMMENT elles se nomment│
│  → NON obfusqués. LiteBox les héberge en process (EmuPlugins)   │
├─────────────────────────────────────────────────────────────────┤
│  COUCHE 1 — LE SDK           Unbroken.LaunchBox.Plugins.dll     │
│  Le contrat : EmulatorPlugin, GameSaveBase, GetSavesArgs…       │
└─────────────────────────────────────────────────────────────────┘
```

**Pourquoi ça compte :** les plugins ne sont pas obfusqués, donc on connaît leur algorithme *exactement*.
L'hôte l'est, donc tout ce qui est décrit ici comme « ce que fait LaunchBox » est soit lu dans le modèle
de données décompilé, soit observé sur disque — jamais deviné. Ce qui reste inconnu est listé au §6.

### 2.1 Le contrat SDK, méthode par méthode

C'est ce que l'hôte peut demander à un plugin. Rien de plus.

| Méthode | Question posée | Qui l'implémente vraiment |
|---|---|---|
| `GetSaves(args)` | « Quelles saves actives trouves-tu pour ces jeux ? » | tous |
| `AddSaveFile(args)` | « Copie ce fichier à l'emplacement live, sous le bon nom. » | tous |
| `RemoveSave` | « Supprime cette save active. » | tous |
| `IsSaveActive` | « Cette save est-elle celle que l'ému utiliserait là, maintenant ? » | tous |
| `IsSaveContainer` | « Est-ce un conteneur (carte mémoire) plutôt qu'un fichier ? » | PCSX2 |
| `TryBackupSave` | « Extrais le contenu du conteneur dans ce dossier temporaire. » | PCSX2 |
| `TryComputeSaveSignature` | « Donne-moi une signature de ce groupe multi-fichiers. » | **Saturn uniquement** |
| `UseSaveGroupIdForPersistedMatch` | « Dois-je te retrouver par SaveGroupId plutôt que par chemin ? » | PCSX2, Saturn |
| `GetPotentialSaveSlots` | « Quels slots de savestate existent ? » | RetroArch (0–9) |
| `IsSecondarySaveFile` / `GetCompanionSaveFiles` | Le modèle multi-fichiers | Saturn |

Note ce qui **n'existe pas** dans ce contrat : rien ne permet de dire à un plugin « cette save appartient
à l'entrée n° 3 de l'archive ». Le plugin ne connaît que des `IGame` et leur `ApplicationPath`. C'est la
contrainte structurante de tout le dossier.

---

## 3. Côté LaunchBox

### 3.1 Le modèle `GameSave` — et la réponse à la question du hash

La classe `GameSave` du cœur LaunchBox (`dumpedlb-savemgmt/1327/Unbroken.LaunchBox`) porte 16 propriétés.
Chacune destinée au XML porte l'attribut `[DataTableExport]` :

| Propriété | Exporté au XML | Ce que c'est |
|---|---|---|
| `GameId` | ✅ | le `<Game>` propriétaire — **le seul lien**, les `<GameSave>` sont des frères de `<Game>`, pas des enfants |
| `AdditionalApplicationId` | ✅ | la version, quand la save appartient à une version plutôt qu'au jeu |
| `EmulatorFileName` | ✅ | `retroarch.exe` |
| `EmulatorCore` | ✅ | `snes9x_libretro` |
| `Title` | ✅ | **vide pour une save active, RENSEIGNÉ pour un backup** — c'est le discriminant. `Saved Game` pour un fichier de save, `Save State <slot>` pour un savestate |
| `SaveGroupName` | ✅ | le nom affiché, `My Save File` par défaut |
| `DisplayChipText` | ✅ | une pastille de métadonnée à côté du titre. **Inutilisée aujourd'hui** |
| `SaveGroupId` | ✅ | l'identité du groupe |
| `MatchLineageId` | ✅ | la lignée — suit le groupe à travers les renommages |
| `MigrationFamilyId` | ✅ | **inutilisé aujourd'hui** |
| `FilePath` | ✅ | **relatif à la racine LB** quand le fichier est dessous, absolu sinon — voir l'encadré ci-dessous |
| `OriginalFileName` | ✅ | le nom d'origine du fichier sauvegardé |
| `Slot` | ✅ | le slot du savestate |
| `ReportedFileSizeBytes` | ❌ | runtime |
| `ReportedLastModifiedUtc` | ❌ | runtime |
| **`Md5`** | ❌ | **runtime** |

**Donc oui, LaunchBox hashe. Et non, il ne l'écrit nulle part — c'est délibéré.** `Md5` existe sur le
modèle mais n'a pas `[DataTableExport]`, exactement comme la taille et la date. Vérifié aussi côté
disque : `LB\Saves\<Plateforme>\` ne contient que les fichiers copiés, aucun sidecar, aucune base.

Deux confirmations indirectes :

- `Md5` n'existe **que** sur le `GameSave` du cœur, pas sur le `GameSaveBase` du SDK. C'est donc l'hôte
  qui hashe, pas les plugins. (`TryComputeSaveSignature` côté plugin est autre chose : le moyen pour un
  plugin de dire « ce groupe, c'est plusieurs fichiers, voilà sa signature ». Seul Saturn l'implémente,
  en MD5 d'un manifeste `nom|MD5` trié.)
- Il existe une chaîne de ressource `ErrorSaveFileAlreadyBackedUp`. LaunchBox ne se contente pas
  d'ignorer le doublon : il le dit à l'utilisateur.

Ce qu'on ne peut pas montrer : *quand* il recalcule et contre quoi il compare. Cette logique est dans
`LaunchBox.exe`, pas dans l'assembly décompilée. Comme rien n'est persisté, il n'a que deux options —
rehasher le dernier backup sur disque, ou garder le `Md5` en mémoire depuis le scan. La présence de
`ReportedFileSizeBytes` et `ReportedLastModifiedUtc` juste à côté suggère un pré-filtre taille+date avant
de payer le hash, mais c'est une hypothèse, pas un fait.

### 3.2 Format : `Data\Settings.xml`

Les réglages de la page Save Management. Valeurs réelles lues sur `G:\LB1326` :

```xml
<DisableSaveManagement>false</DisableSaveManagement>       <!-- inversé ! false = activé -->
<EnableAutomaticSaveBackups>false</EnableAutomaticSaveBackups>
<SaveBackupOnGameClose>true</SaveBackupOnGameClose>
<PeriodicSaveBackupEnabled>true</PeriodicSaveBackupEnabled>
<MaxAutoBackupsPerGame>25</MaxAutoBackupsPerGame>
<SaveVaultMigrationComplete>true</SaveVaultMigrationComplete>
<SaveVaultMetadataRepairComplete>false</SaveVaultMetadataRepairComplete>
<LastLibrarySaveScanUtc>2026-08-27T04:39:05.13416+02:00</LastLibrarySaveScanUtc>
<LastLibrarySaveScanCursorGameId />               <!-- n'existe QUE pendant un scan -->
<LastLibrarySaveScanCursorAdditionalAppId />
```

Trois choses à retenir :

1. **`DisableSaveManagement` est inversé.** L'UI affiche « Enable Save Management », le fichier stocke le
   contraire. LiteBox affiche dans le bon sens et stocke inversé, pour rester lisible par LaunchBox.
2. **Les deux clés `LastLibrarySaveScanCursor*` sont un curseur de reprise.** Le scan de bibliothèque est
   interruptible et reprend là où il s'est arrêté — parce qu'il fait tourner tous les plugins sur tous les
   jeux, et qu'un tel job ne peut pas se permettre de repartir de zéro.
3. **`LastLibrarySaveScanUtc` est daté en heure LOCALE** malgré son nom : la valeur porte un décalage
   (`+02:00`). Et les deux clés `LastLibrarySaveScanCursor*` **disparaissent du fichier** entre deux
   scans — ce sont des curseurs de reprise, pas un état permanent. C'est l'équivalent LaunchBox de notre
   `Saves.LastScanUtc`, mais les deux ne mesurent pas la même chose : voir §3.4bis.
4. **`SaveVaultMetadataRepairComplete=false` est un drapeau one-shot.** C'est très probablement ce que
   coche le bouton « Repair Save Metadata ». Ce qui suggère que le Repair de LaunchBox est une passe de
   *migration* à exécuter une fois, pas un nettoyage répétable. Voir §6.

### 3.3 Format : `Data\Platforms\<Plateforme>.xml`

Les `<GameSave>` sont des éléments **de premier niveau**, frères de `<Game>`, reliés uniquement par
`<GameId>`. Ordre observé dans le fichier : `Game`, `AdditionalApplication`, `AlternateName`, `GameSave`.

**Une save active** (deux records réels, tirés de ton `Super Nintendo Entertainment System.xml`) :

```xml
<GameSave>
  <GameId>5925b287-c122-4ba8-96e0-7ca6d6d9fb20</GameId>
  <EmulatorFileName>retroarch.exe</EmulatorFileName>
  <EmulatorCore>snes9x_libretro</EmulatorCore>
  <SaveGroupName>My Save File</SaveGroupName>
  <SaveGroupId>36ff9a2cc3ca49848bddd78dd772df12</SaveGroupId>
  <MatchLineageId>36ff9a2cc3ca49848bddd78dd772df12</MatchLineageId>
  <FilePath>G:\LB1326\Emulators\RetroArch\saves\Snes9x\Super Mario World 2 - Yoshi's Island.srm</FilePath>
</GameSave>
```

Pas de `<Title>`. `SaveGroupId` = un GUID sans tirets. Le `FilePath` est ici **absolu** — c'est
LiteBox qui a écrit ce record.

> ### ⚠ Correction (observé le 2026-08-26, 20h29)
>
> Ce document affirmait au départ : « `FilePath` absolu pour une save active, relatif pour un backup ».
> **C'est faux.** Le critère n'est pas actif/backup, c'est **qui a écrit le record**.
>
> Preuve : les deux records de Secret of Mana étaient absolus le matin, et sont devenus
> `Emulators\RetroArch\saves\Snes9x\Secret of Mana (USA).srm` — relatifs — après une session
> LaunchBox. Or LiteBox n'écrit **jamais** de chemin relatif dans un record : ses deux seuls points
> d'écriture (`SaveManager.cs:545` et `:692`) posent `abs`. Et LiteBox n'a jamais tourné sur cette
> install — `Core\litebox\` ne contient aucun état. Donc c'est LaunchBox qui a relativisé.
>
> **La règle réelle :**
>
> | | écrit | lit |
> |---|---|---|
> | LaunchBox | **relatif** quand le fichier est sous la racine LB | les deux |
> | LiteBox | **absolu**, toujours | les deux |
>
> **Aucune conséquence fonctionnelle**, vérifié dans le code : `SaveManager.AbsPath` résout un chemin
> relatif contre `LbRoot`, et `PathEq` compare les deux côtés résolus. Un record relatif est donc
> apparié correctement, aucun doublon n'est créé — et comme la réécriture de `FilePath` (l. 545) est
> gardée par `!PathEq(…)`, LiteBox **préserve** la forme relative de LaunchBox au lieu de la
> reconvertir. Les deux frontends peuvent alterner sur la même bibliothèque sans se battre.
>
> **Ce qui reste inexpliqué :** dans la même session, LaunchBox a écrit les deux records *neufs* de la
> version en **absolu** tout en relativisant les deux anciens du jeu. L'hypothèse qui colle aux données
> est que la relativisation vit sur le chemin « mettre à jour un record existant » et pas sur « en créer
> un ». Ce n'est qu'une hypothèse : l'hôte est obfusqué.

**Un backup**, pour le même jeu :

```xml
<GameSave>
  <GameId>5925b287-c122-4ba8-96e0-7ca6d6d9fb20</GameId>
  <EmulatorFileName>retroarch.exe</EmulatorFileName>
  <EmulatorCore>snes9x_libretro</EmulatorCore>
  <Title>Saved Game</Title>                             <!-- ← le discriminant -->
  <SaveGroupName>My Save File</SaveGroupName>
  <SaveGroupId>36ff9a2cc3ca49848bddd78dd772df12</SaveGroupId>   <!-- ← même groupe -->
  <MatchLineageId>36ff9a2cc3ca49848bddd78dd772df12</MatchLineageId>
  <FilePath>Saves\Super Nintendo Entertainment System\Super Mario World 2 - Yoshi's Island-01.srm</FilePath>
  <OriginalFileName>Super Mario World 2 - Yoshi_s Island.srm</OriginalFileName>
</GameSave>
```

`FilePath` **relatif** cette fois (donc portable), et un `OriginalFileName`.

Deux choses non évidentes dans ce record, toutes deux établies le 2026-08-26 sur 18 records réels :

**Le nom du fichier vault vient de la ROM, pas de la save.** Le backup s'appelle
`Super Mario World 2 - Yoshi's Island-01.srm` — le basename de l'`ApplicationPath` du **jeu** — alors
que le fichier sauvegardé est `Super Mario World 2-YI [USA][Rev 1][!!].srm`. C'est précisément le rôle
d'`OriginalFileName` : conserver le vrai nom pour pouvoir restaurer. (LiteBox nomme le fichier vault
d'après la **save** — divergence, voir §5.)

**Il y a UN record de backup par GROUPE, pas un par backup.** Le record pointe sur la copie la **plus
récente**. Observé : le vault contient `Secret of Mana (USA).state`, `-01`, `-02`, `-03`, `-04` — cinq
fichiers — et un seul record, qui pointe sur `-04`. Les quatre autres n'ont aucun record : LaunchBox les
retrouve en balayant le dossier vault par basename, comme le décrivait la RE d'origine.

> ⚠ Cette précision corrige ma propre correction du §3.5. La RE d'origine disait « il n'y a PAS un record
> par backup » — **elle avait raison sur ce point**. Ce qu'elle disait de faux, c'est « zéro record
> supplémentaire pour les backups » : il y en a bien un par groupe.

**Et le troisième cas, le nôtre** — même fichier, même jeu :

```xml
<GameSave>
  ...
  <SaveGroupId>entry:Super Mari_91DE0B99:Super Mario World 2-YI [USA][Rev 1][!!].sfc</SaveGroupId>
  <MatchLineageId>entry:Super Mari_91DE0B99:Super Mario World 2-YI [USA][Rev 1][!!].sfc</MatchLineageId>
  <FilePath>G:\LB1326\Emulators\RetroArch\saves\Snes9x\Super Mario World 2-YI [USA][Rev 1][!!].srm</FilePath>
</GameSave>
```

**C'est la preuve empirique la plus importante du dossier.** Ce `SaveGroupId` est le nôtre, au format
`entry:<signature>:<chemin dans l'archive>`. LaunchBox l'a lu, l'a réécrit intact, et l'a même propagé
dans `MatchLineageId`. Puis il a créé son propre backup de ce groupe en conservant l'identifiant.

Ça valide toute l'approche : `SaveGroupId` est un champ texte que LaunchBox transporte sans l'interpréter.
Les plugins s'en servaient déjà comme espace de noms (`saturn-<base>`, `pcsx2:<carte>:<dossier>`), et
`UseSaveGroupIdForPersistedMatch` existe précisément pour qu'un hôte matche dessus. Notre `entry:` n'est
pas un détournement, c'est le mécanisme prévu, utilisé une troisième fois.

### 3.4 Format : le vault

`LB\Saves\<Plateforme>\`, avec cette convention de nommage :

```
Secret of Mana (USA).srm        ← premier backup : nom nu
Secret of Mana (USA)-01.srm     ← deuxième
Secret of Mana (USA)-02.srm     ← troisième
```

Le nom de base est **celui de la ROM** (`ApplicationPath` du jeu ou de la version), pas celui du fichier
de save — **et l'extension est normalisée, donc le slot est perdu** : un savestate slot 2 vivant sous
`Secret of Mana (France).state2` est copié en `Secret of Mana (France).state`. Deux slots du même jeu se
disputent donc le même nom de vault (le second devient `-01`). Seuls `OriginalFileName` et `<Slot>` dans
le record conservent l'information. LiteBox utilise celui de la save — c'est pourquoi le même vault peut contenir
`Super Mario World 2 - Yoshi's Island.srm` (LaunchBox) et
`Super Mario World 2-YI [USA][Rev 1][!!].srm` (LiteBox) pour la même sauvegarde.

Les saves-dossiers (NAND Wii, dossiers GCI, contenu extrait d'une carte PS2) sont archivées en `.7z` par
LaunchBox. **LiteBox les copie en dossiers** — divergence assumée, voir §5.

À ne pas confondre avec `LB\Backups\`, qui contient les sauvegardes de la *base de données* LaunchBox
(des `.7z` Startup/Shutdown). Aucun rapport.

### 3.4bis Les deux chemins de backup ne produisent PAS la même chose

Observé le 2026-08-27 sur une base repartie de zéro : un scan de bibliothèque a tourné à 04:39, puis
trois parties ont été jouées à 06:37–06:38.

| | fichier dans le vault | record `Title` |
|---|---|---|
| Road Rash, Yoshi — **pas joués**, seulement balayés par le scan | ✅ | ❌ |
| Secret of Mana ×3 — **joués**, fermeture de jeu | ✅ | ✅ |

**Un scan de bibliothèque copie les fichiers sans écrire de record.** Seule la fermeture de jeu écrit
les deux. C'est une asymétrie interne à LaunchBox, et elle a une conséquence directe pour nous : ses
backups issus d'un scan sont **invisibles à la lecture des records** — on ne peut les retrouver qu'en
balayant `LB\Saves\<Plateforme>\` par nom de base, exactement comme LaunchBox le fait lui-même.

C'est aussi la raison de ne pas écrire dans son `LastLibrarySaveScanUtc` : nos passes écrivent les
records, les siennes non ; partager l'horodatage ferait croire à l'un que le travail de l'autre a été
fait.

### 3.5 ⚠ Correction d'une conclusion périmée

`lb-save-management.md` affirme, section « Le vault — OBSERVÉ (2026-07-04) » :

> **Il n'y a PAS un record `<GameSave>` par backup.**

**C'est faux, et ça a été observé le 2026-08-26.** La conclusion venait d'une observation faite alors
qu'aucun backup n'existait sur l'install : LaunchBox n'avait rien écrit parce qu'il n'y avait rien à
écrire. Dès qu'on en crée un, la ligne `Title="Saved Game"` apparaît.

Ce que ça change : la justification originelle de `saves-vault.json` (« LB ne persiste rien pour les
backups, donc on est libres ») ne tient plus telle quelle. Le fichier reste justifié, mais pour d'autres
raisons — voir §5.

### 3.6 Les algorithmes par plugin, en résumé

Détail complet dans `docs/save-algorithms.md`. L'essentiel :

**RetroArch — l'identité est le NOM DE FICHIER.** Il lit `retroarch.cfg` à côté de l'exe, en déduit le
dossier des saves, puis fait :

```csharp
string base = Path.GetFileNameWithoutExtension(item.ApplicationPath);
string[] files = Directory.GetFiles(saveDir, base + "*.*");   // joker de PRÉFIXE
```

Le dossier lui-même dépend de trois réglages en cascade :

| Réglage `retroarch.cfg` | Effet sur le dossier |
|---|---|
| `savefiles_in_content_dir = true` | le dossier **du contenu**, donc `Path.GetDirectoryName(ApplicationPath)` |
| sinon `savefile_directory` | ce dossier-là (un préfixe `:\` est rebasé sur le dossier RetroArch) |
| `sort_savefiles_by_content_enable = true` | + le nom du dossier **parent** du contenu |
| `sort_savefiles_enable = true` | + le nom d'affichage du core (`snes9x_libretro` → `Snes9x`, lu dans `info\<core>.info`) |

Les savestates suivent la même forme avec `savestate_directory` / `savestates_in_content_dir` /
`sort_savestates_by_content_enable` / `sort_savestates_enable`, et le suffixe encode le slot :
`.state` = 0, `.state.auto` = −1, `.stateN` = N. **Seuls les slots 0 à 9 sont scannés.**

#### La règle qui décide À QUI la save est attribuée

`GetSaves` fait deux passes : d'abord sur les `AdditionalApplications`, ensuite sur les `Games`. La
seconde commence par ceci :

```csharp
foreach (IGame game in args.Games) {
    var apps = game.GetAllAdditionalApplications();
    if (apps != null && apps.Any(a => a.ApplicationPath == game.ApplicationPath))
        continue;                      // ← le jeu est ENTIÈREMENT sauté
    …
}
```

**Dès qu'un jeu possède une version pointant sur sa propre ROM, le jeu lui-même n'a plus jamais de save
à son nom** : tout remonte par la passe 1, attribué à cette version. Ce n'est pas un cas de bord — c'est
le cas normal d'une bibliothèque importée avec des versions (« Play (USA) Version… » et consorts pointent
sur le fichier du jeu).

Vérifié le 2026-08-27 : après trois parties, les 12 nouveaux records de *Secret of Mana* portent tous un
`AdditionalApplicationId`. Aucun n'est attribué au jeu, y compris ceux du fichier `Secret of Mana (USA).srm`
qui est pourtant l'`ApplicationPath` du jeu.

> **Le test est un `==` sur chaînes, pas sur chemins.** Une différence de casse, un `.\` en trop, une barre
> oblique inversée : le saut ne se déclenche pas, et le même fichier est alors remonté **deux fois** — une
> fois pour la version, une fois pour le jeu.

**Dolphin — l'identité est le CONTENU DU DISQUE.** Il lit un Disc ID dans les octets du fichier. Renommer,
déplacer, extraire : rien ne change. Immunisé — *à condition* que le fichier qu'on lui tend soit lisible
comme une image disque. Un `.zip` ne l'est pas.

**PCSX2 — l'identité est la CARTE MÉMOIRE.** `SaveGroupId = "pcsx2:<carte>:<dossier>"`. La carte est
configurée dans l'émulateur, sans rapport avec ce qui a été lancé. Totalement immunisé.

**Saturn n'est pas un plugin séparé** — c'est une branche *à l'intérieur* du plugin RetroArch, avec son
modèle multi-fichiers (`.bcr` primaire + compagnons `.bkr`/`.smpc`) et son `SaveGroupId = "saturn-<base>"`.

---

## 4. Côté LiteBox

### 4.1 Ce qu'on réutilise, ce qu'on remplace

| Couche | LiteBox |
|---|---|
| SDK (1) | **identique** — le vrai `Unbroken.LaunchBox.Plugins.dll` |
| Plugins (2) | **identiques** — les vrais plugins, hébergés en process via `EmuPlugins` |
| Hôte (3) | **réimplémenté** — `Host\Saves\SaveManager.cs` |

Décision de fond : on ne réécrit pas les plugins. Ils ne sont pas faux, on leur **pose la mauvaise
question**. Toute la casse liée aux archives vient d'une seule valeur : `ApplicationPath`, qui est
l'archive au moment où le plugin est interrogé. Lui donner le chemin de l'entrée le fait calculer
exactement ce que l'émulateur a fait — sans toucher au plugin, et ça répare RetroArch et Dolphin d'un
coup.

C'est ce que fait `EntryGame` (`Host\Saves\SaveEntries.cs`) : un `IGame` qui répond avec le chemin
**d'une entrée** tout en restant, pour tout le reste — son `Id` surtout — le vrai jeu, pour que le plugin
attribue ce qu'il trouve au bon jeu. Le jumeau côté `IEmulator` (`AbsPathEmulator`) existait déjà dans
`SaveManager` pour le même genre de problème.

### 4.2 Format : `Core\litebox\saves-vault.json`

Notre index de backups. Racine versionnée :

```json
{
  "ConfigVersion": "0.9.3",
  "Entries": [
    {
      "GameId": "5925b287-c122-4ba8-96e0-7ca6d6d9fb20",
      "AppId": null,
      "GroupId": "entry:Super Mari_91DE0B99:Super Mario World 2-YI [USA][Rev 1][!!].sfc",
      "GroupName": "My Save File",
      "IsState": false,
      "Slot": null,
      "VaultPath": "Saves\\Super Nintendo Entertainment System\\Super Mario World 2-YI [USA][Rev 1][!!]-01.srm",
      "OriginalFileName": "Super Mario World 2-YI [USA][Rev 1][!!].srm",
      "Label": "Avant le boss final",
      "CreatedUtc": "2026-08-26T14:32:11.0000000Z",
      "Md5": "9F86D081884C7D659A2FEAA0C55AD015",
      "SizeBytes": 32768,
      "IsDirectory": false,
      "Auto": true
    }
  ]
}
```

Ce que ce fichier apporte, que le XML de LaunchBox n'a pas :

- **`Label`** — un nom par version (« avant le boss final »). LaunchBox n'a pas ce champ.
- **`Md5` persisté** — le contrôle de doublon est gratuit. LaunchBox doit relire le dernier backup.
- **`CreatedUtc`** — une date propre, pas la date de fichier (qui bouge à la copie).
- **`Auto`** — distingue les versions automatiques des tiennes. C'est ce qui permet à la rétention de ne
  jamais supprimer un backup que tu as demandé à la main.
- **Le lien groupe→backup explicite**, au lieu du regroupement par nom de fichier.

Il accepte encore l'ancien format (un tableau JSON nu, estampillé `0.0.0`) et le réécrit au format objet
à la première sauvegarde. Il n'est **jamais réinitialisé** sur un décalage de version — c'est de la donnée
utilisateur, elle ne se migre que vers l'avant.

`VaultPath` est relatif à la racine LB quand c'est possible, donc l'install reste portable.

### 4.3 L'identité `entry:` — comment elle est fabriquée

```
entry:<ShortSignature>:<PathInArchive>[:sN]
       │                │               └── slot, pour un savestate
       │                └── chemin dans l'archive : deux entrées peuvent partager
       │                    un nom de fichier dans des dossiers différents
       └── signature de contenu de l'archive : survit à un renommage ET à un
           déplacement de l'archive elle-même
```

Elle est calculée sans jamais ouvrir l'archive. `ArchiveListingCache` persiste déjà `FileName`,
`PathInArchive` et `Size` par entrée dans `rom-archive-cache.db`. Un scan de saves ne doit pas payer un
listing 7z ; une archive jamais listée dégrade proprement vers le comportement d'avant.

**Quelles entrées sont sondées.** Par défaut, seulement celles réellement jouées (MRU
`ArchiveHistory.GetLastPlayed`) : une save n'existe que si quelque chose l'a écrite, et sonder une entrée
coûte un appel plugin plus une lecture de dossier. Le reste est un *deep scan* explicite
(`SaveManager.DeepScan`).

**Le plus long nom gagne.** Avec `Sonic (USA)` et `Sonic (USA) Beta` dans la même archive, le préfixe le
plus court réclamerait la save du plus long. Le tri par longueur décroissante règle ça.

### 4.4 Le pipeline de scan, étape par étape

`SaveManager.Scan(game, focus)` — `focus` est la version quand on regarde une page de version.

1. **Interroger TOUS les plugins**, pas seulement celui de l'émulateur du jeu. C'est la parité LaunchBox :
   RetroArch ne filtre pas par émulateur assigné, donc un jeu assigné ailleurs peut quand même montrer des
   saves RetroArch. Chaque plugin s'auto-filtre ; un échec ne perd que ses résultats à lui.
2. **La passe par entrée** (1b). Pour chaque entrée sondée, on refait `GetSaves` avec un `EntryGame`. On
   note au passage quelle save vient de quelle entrée (`entryOf`).
3. **Charger les records `<GameSave>`** du jeu et les filtrer sur la vue courante (jeu / version).
4. **Apparier** chaque save trouvée à un record : par `SaveGroupId` quand le plugin le demande
   (`UseSaveGroupIdForPersistedMatch`), sinon par chemin + slot. Sans record → on en crée un.

   C'est ici que l'identité d'entrée est posée : le nouveau record prend `entry:…` comme `SaveGroupId` au
   lieu d'un GUID neuf.
5. **Les records orphelins** (fichier disparu) restent affichés, avec un avertissement — ils peuvent
   encore porter un historique de backups. Les lignes `Title="Saved Game"` sont exclues de cette passe :
   elles décrivent un backup, pas une save active manquante.
6. **Attacher les backups.** Les nôtres depuis `saves-vault.json`, ceux de LaunchBox depuis ses lignes
   `Title="Saved Game"`, dédoublonnés par chemin résolu. Un backup vu des deux côtés n'apparaît qu'une
   fois.

### 4.5 `SaveBackupService` — les sauvegardes automatiques

Jusqu'à très récemment, **la seule chose qui écrivait un backup était le bouton manuel.** Les cinq
réglages de LaunchBox étaient sur disque et personne ne les lisait.

Trois déclencheurs :

**À la fermeture du jeu.** Inséré dans `HostServices` **avant** `RomExtractor.OnGameExitCleanup()`.
L'ordre n'est pas cosmétique : cette méthode fait un `Directory.Delete(recursive: true)` de toute la bande
`\tmp`. Avec `savefiles_in_content_dir`, la save vient d'y être écrite. Après cette ligne, elle n'existe
plus.

C'est aussi le seul moment où l'appartenance d'une save est **connue** et non devinée : le jeu qui vient
de tourner est celui dont la save a changé, et l'entrée lancée est au dossier (`LaunchHistoryDb`).

**La passe de bibliothèque.** Reprenable via un curseur persisté après chaque jeu. Elle attend que la
machine soit inactive **et** qu'aucun jeu ne tourne — `GetLastInputInfo` seul ne suffit pas : une session
à la manette paraît parfaitement inactive à Windows.

**Au démarrage**, si la dernière passe est en retard, après un délai de 2 minutes.

Chaque backup passe par `Backup(force: false)`, donc le contrôle de doublon décide : une save identique à
son dernier backup ne produit rien. C'est ça qui rend l'exécution fréquente inoffensive.

**La rétention** (`MaxAutoBackupsPerGame`, 25 par défaut) ne supprime que les versions `Auto`. Un réglage
qui parle de copies automatiques n'a pas à effacer des backups demandés à la main. Elle ne s'applique
qu'au moment où un backup est créé : baisser le plafond ne purge pas rétroactivement.

---

## 5. Les différences, en un tableau

| | LaunchBox | LiteBox | Pourquoi |
|---|---|---|---|
| **Records de saves actives** | `<GameSave>` dans le XML | **identique** | interop : une bibliothèque éditée d'un côté s'ouvre correctement de l'autre |
| **Identité de groupe** | GUID | GUID, **ou `entry:<sig>:<chemin>`** | un GUID ne dit pas *quelle ROM* de l'archive |
| **Métadonnées de backup** | rien de persisté (dérivé du FS au scan) | `saves-vault.json` | label, md5, date propre, distinction auto/manuel |
| **Backups visibles par l'autre** | les siens **et les nôtres** ? ❌ | les siens **et ceux de LB** ✅ | **asymétrique** — voir ci-dessous |
| **Saves-dossiers dans le vault** | archivées en `.7z` | copiées en dossiers | divergence non résolue — voir §6 |
| **Hash de doublon** | `Md5` runtime, recalculé | `Md5` persisté dans le JSON | évite de relire le dernier backup |
| **Rétention** | `MaxAutoBackupsPerGame`, portée inconnue | même clé, **versions auto uniquement** | ne jamais supprimer un backup manuel |
| **Backup avant restauration** | inconnu | **oui, systématique** | restaurer écrase la progression en cours ; il n'y avait aucun retour arrière |
| **Sauvegarde à la fermeture** | oui (réglage) | oui, **avant la purge d'extraction** | rattrape le cas `savefiles_in_content_dir` |
| **Intervalle de la passe périodique** | non exposé | configurable (`LiteBox.ini [Saves]`) | ajout, pas de contrepartie LB |
| **Seuil d'inactivité** | inconnu | configurable, + garde « aucun jeu ne tourne » | idem |

### L'asymétrie des backups, en clair

- LaunchBox écrit une ligne `<GameSave Title="Saved Game">` par backup. **On la lit** : ses backups
  apparaissent dans notre UI, marqués `External`, avec le label « LaunchBox ».
- Nos backups vont dans `saves-vault.json`. **On n'écrit pas la ligne XML correspondante.** Donc
  LaunchBox ne voit pas nos backups.

C'est une décision ouverte, pas un oubli. Écrire la ligne rendrait la symétrie parfaite, au prix de deux
magasins qui décrivent la même chose et qui peuvent diverger (supprimer un backup côté LB laisserait notre
JSON dangling, et inversement). Le bouton « Repair save metadata » existe justement pour ce genre de
dérive, mais mieux vaut décider franchement que réparer en boucle.

### Les divergences délibérées, et leur justification

**Backup avant restauration.** `Restore` prend d'abord une copie de ce qui va être écrasé. Restaurer est
la seule action de cette page dont le but *est* de remplacer la progression en cours, et jusqu'ici elle le
faisait sans retour arrière possible. Le contrôle de doublon rend l'opération gratuite quand la save
correspond déjà à son dernier backup. **Je n'ai pas pu vérifier si LaunchBox fait la même chose** — donc
je ne peux pas qualifier ça de parité, ni de divergence délibérée. C'est un ajout dont je ne sais pas
s'il diverge.

**Import : sauvegarder les groupes existants d'abord.** Même raisonnement.

**Refuser de renommer un backup externe.** Un backup lu depuis une ligne LaunchBox n'est pas dans notre
magasin. Le renommer donnerait l'illusion d'une modification persistée.

---

## 6. Ce qu'on n'a PAS pu établir

Cette section est aussi importante que les autres. Chacun de ces points a été cherché et pas trouvé — ce
ne sont pas des oublis.

| Question | Pourquoi c'est ouvert | Impact |
|---|---|---|
| **Quand LaunchBox recalcule son `Md5`, et contre quoi il compare** | la logique est dans `LaunchBox.exe`, obfusqué et non décompilé | aucun — notre approche est plus stricte de toute façon |
| **Ce que fait exactement « Repair Save Metadata »** | idem. Indice : `SaveVaultMetadataRepairComplete` est un drapeau one-shot, donc c'est probablement une *migration*, pas un nettoyage | nos boutons portent les noms de LB mais **ne revendiquent pas la parité** — leur sémantique est écrite dans l'aide |
| **Ce que fait « Clear All and Re-scan »** | idem | idem |
| **Si LaunchBox sauvegarde avant d'écraser lors d'une restauration** | idem | on le fait ; on ne sait pas si c'est un ajout ou une parité |
| **La portée de `MaxAutoBackupsPerGame` chez LB** (auto seulement, ou tout ?) | idem | on a choisi « auto seulement », qui est le choix sûr |
| **Comment LaunchBox nomme un backup de dossier** | aucun backup de dossier n'a encore été observé sur une install réelle | on copie en dossier, LB fait des `.7z`. **Divergence non résolue** : à trancher quand un cas réel existera |
| **Si LaunchBox préserve un élément enfant inconnu dans `<GameSave>`** | non testé — mais **la question est devenue sans objet** : `SaveGroupId` transporte notre identité et le round-trip est prouvé | aucun |
| ~~Quelle configuration RetroArch est réellement en usage~~ | **RÉPONDU le 2026-08-27** : `savefile_directory = :\saves`, `savestate_directory = :\states`, `savefiles_in_content_dir = false`, `sort_save*_enable = true`, `sort_*_by_content_enable = false` | **D2 ne se produit pas sur cette install.** Le scénario D reste un vrai défaut, mais la config qui le déclenche n'est pas celle en usage : ici c'est D1 qui mord |

Deux champs LaunchBox existent, sont exportés au XML, et ne sont utilisés par personne aujourd'hui :
`DisplayChipText` (une pastille de métadonnée à côté du titre du groupe — potentiellement l'endroit prévu
pour afficher *quelle ROM* de l'archive, ce qu'on fait via un combobox) et `MigrationFamilyId`. À creuser.

---

## 7. Scénarios pas à pas

### Scénario A — Le cas simple : ROM nue, RetroArch

**Décor.** `Secret of Mana (USA).sfc` posé dans `G:\Roms\SNES\`. RetroArch avec
`savefile_directory = ":\saves"` et `sort_savefiles_enable = true`. Core snes9x.

**1. Tu joues, tu sauvegardes en jeu, tu quittes.**

RetroArch écrit, en se basant sur le nom du contenu qu'il a chargé :

```
G:\LB1326\Emulators\RetroArch\saves\Snes9x\Secret of Mana (USA).srm
                              └─ :\saves  └─ core (sort_savefiles_enable)
```

**2. LiteBox ferme le jeu.** `SaveBackupService.OnGameClosed` se déclenche (si les backups automatiques
sont activés) :

- scan complet du jeu → le plugin trouve la save ;
- `Backup(force: false)` → aucun backup existant, donc copie ;
- fichier créé : `G:\LB1326\Saves\Super Nintendo Entertainment System\Secret of Mana (USA).srm` ;
- entrée ajoutée dans `saves-vault.json`, `Auto: true`, avec son MD5.

**3. Tu ouvres Edit Game → Game Saves.**

Le plugin fait `Directory.GetFiles(saveDir, "Secret of Mana (USA)" + "*.*")`. Le basename vient de
`ApplicationPath` — qui est bien `Secret of Mana (USA).sfc`. Ça matche exactement.

Aucun record n'existe encore → on en crée un, avec un GUID neuf :

```xml
<GameSave>
  <GameId>425d9525-…</GameId>
  <EmulatorFileName>retroarch.exe</EmulatorFileName>
  <EmulatorCore>snes9x_libretro</EmulatorCore>
  <SaveGroupName>My Save File</SaveGroupName>
  <SaveGroupId>54d807eea24a4074bef0fbd035b6aab7</SaveGroupId>
  <MatchLineageId>54d807eea24a4074bef0fbd035b6aab7</MatchLineageId>
  <FilePath>G:\LB1326\Emulators\RetroArch\saves\Snes9x\Secret of Mana (USA).srm</FilePath>
</GameSave>
```

L'UI affiche un groupe « My Save File », une save active, un backup.

**4. Tu rejoues, tu sauvegardes à nouveau, tu quittes.**

`Backup(force: false)` recalcule le MD5, le compare à celui stocké dans le JSON : différent. Nouveau
fichier `Secret of Mana (USA)-01.srm`. Deuxième version.

**5. Tu quittes sans avoir sauvegardé en jeu.**

MD5 identique → `Identical = true`, rien n'est écrit. Aucun fichier, aucune entrée JSON.

---

### Scénario B — Archive mono-ROM, nom différent : l'accident du préfixe

**Décor.** `Sonic 1.zip` dans la bibliothèque, contenant `Sonic The Hedgehog (USA, Europe).md`.
Le module d'extraction est actif.

**1. Tu lances.** LiteBox extrait vers `<cache>\<SIG>\P\Sonic The Hedgehog (USA, Europe).md` et lance
RetroArch dessus.

**2. RetroArch écrit** `…\saves\Genesis Plus GX\Sonic The Hedgehog (USA, Europe).srm` — d'après le nom du
**contenu**.

**3. Le scan, sans la passe par entrée.** Le plugin reçoit le vrai `IGame`, dont l'`ApplicationPath` est
`G:\Roms\Genesis\Sonic 1.zip`. Il cherche `Sonic 1*.*`.

**Rien.** `Sonic The Hedgehog…` ne commence pas par `Sonic 1`. La save existe, elle est intacte, et
l'interface dit qu'il n'y en a pas.

> C'est ici que se joue l'« accident du préfixe » : si l'archive s'était appelée `Sonic The Hedgehog.zip`,
> le joker `Sonic The Hedgehog*.*` aurait trouvé la save **par pur hasard**. Ça marche pour une partie des
> bibliothèques, ce qui rend le défaut invisible jusqu'à ce qu'il ne marche plus.

**4. Le scan, avec la passe par entrée.**

- `SaveEntries.For(game, null)` lit le cache de listing : une entrée, `Sonic The Hedgehog (USA, Europe).md`.
- Elle est marquée jouée (MRU) → elle est sondée.
- On refait `GetSaves` avec un `EntryGame` portant le chemin de l'entrée.
- Le plugin calcule `Sonic The Hedgehog (USA, Europe)*.*`. **Trouvé.**
- Nouveau record, avec l'identité d'entrée :

```xml
<SaveGroupId>entry:Sonic 1_A3F91C02:Sonic The Hedgehog (USA, Europe).md</SaveGroupId>
```

**5. Ce que tu vois.** Un groupe, avec une pastille indiquant de quelle ROM il vient. Marqué
`EntryInferred` : trouvé par correspondance de nom, pas confirmé par une session. C'est un **candidat**
jusqu'à ce qu'une partie le confirme.

---

### Scénario C — Archive multi-ROM : l'effondrement des groupes

**Décor.** `Sonic Collection.zip` contenant :

```
Sonic The Hedgehog (USA).md
Sonic The Hedgehog 2 (World).md
Sonic 3D Blast (USA).md
```

Tu as joué aux trois. RetroArch a écrit trois `.srm` distincts.

**Sans la passe par entrée.** Le plugin cherche `Sonic Collection*.*` → **zéro résultat** (aucun `.srm` ne
commence par « Sonic Collection »). Trois saves, invisibles.

Variante pire : si l'archive s'appelait `Sonic.zip`, le joker `Sonic*.*` ramènerait **les trois fichiers
d'un coup**. Le plugin les remonte, et comme il n'a aucun moyen de savoir qu'il s'agit de trois jeux
différents, ils atterrissent dans un seul groupe indistinct nommé « My Save File ». Restaurer l'un
écraserait n'importe lequel.

**Avec la passe par entrée.** Trois sondages, trois basenames, trois groupes :

| Groupe | `SaveGroupId` |
|---|---|
| Sonic The Hedgehog (USA) | `entry:Sonic Coll_7B22:Sonic The Hedgehog (USA).md` |
| Sonic The Hedgehog 2 (World) | `entry:Sonic Coll_7B22:Sonic The Hedgehog 2 (World).md` |
| Sonic 3D Blast (USA) | `entry:Sonic Coll_7B22:Sonic 3D Blast (USA).md` |

Même signature d'archive, chemins internes différents. L'UI affiche un combobox pour naviguer entre les
ROMs de l'archive.

> **Le piège du plus long nom.** `Sonic The Hedgehog (USA)` est un préfixe de rien d'autre ici, mais si
> l'archive contenait aussi `Sonic The Hedgehog (USA) (Beta).md`, le joker de la première ramènerait la
> save de la seconde. D'où le tri par longueur décroissante : la plus longue correspondance gagne.

---

### Scénario D — `savefiles_in_content_dir` : la destruction silencieuse

**Décor.** Même archive, mais `savefiles_in_content_dir = true` dans `retroarch.cfg`.

**1. Tu lances.** L'archive fait 4 Mo décompressée. Le plancher du cache est `CacheMinMb = 100`, donc elle
part dans la bande **éphémère** : `<cache>\tmp\<SIG>\P\Sonic The Hedgehog (USA).md`.

> Ce plancher est la raison pour laquelle ce scénario est courant plutôt qu'exotique : NES, SNES, Game Boy,
> Mega Drive et la plupart des sets arcade passent tous en dessous.

**2. Tu joues, tu sauvegardes en jeu.** RetroArch écrit **à côté du contenu** :

```
<cache>\tmp\<SIG>\P\Sonic The Hedgehog (USA).srm
```

**3. Tu quittes.** Voici la séquence exacte dans `HostServices` :

```
… écrans de fin, restauration des moniteurs, arrêt des watchers …
→ SaveBackupService.OnGameClosed(game)          ← NOTRE HOOK
→ RomExtractor.OnGameExitCleanup()  →  PurgeTmp  →  Directory.Delete(<tmp>, recursive: true)
→ CleanupFlatExtract()
```

**Sans le hook**, la save est détruite quelques secondes après la partie, à chaque fois, sans le moindre
message. Le plugin, lui, aurait cherché dans `Path.GetDirectoryName(ApplicationPath)` — c'est-à-dire
`G:\Roms\Genesis\`, le dossier du `.zip`. Les deux dossiers n'ont aucun rapport. Il n'aurait jamais rien
trouvé, même avant la suppression.

**Avec le hook**, `OnGameClosed` tourne pendant que le fichier existe encore : la save est scannée, copiée
dans le vault, indexée. La ligne suivante peut supprimer le dossier.

> À noter : `sort_savefiles_by_content_enable` aggrave encore le cas, puisque le sous-dossier devient le
> nom du dossier **parent** du contenu — donc le segment `P` ou `<subdir>` du cache côté RetroArch, et le
> dossier de plateforme côté plugin.

---

### Scénario E — Restaurer un backup : le défaut qui reste

**Décor.** Scénario B. Tu as un backup, tu veux revenir en arrière.

**1. Tu cliques « Set as Active ».**

**2. LiteBox sauvegarde d'abord** ce qui va être écrasé (`Backup(force: false)`) — gratuit si la save
courante correspond déjà à son dernier backup.

**3. LiteBox appelle `plugin.AddSaveFile(...)`.** Et c'est là que ça coince.

Le contrat SDK est le suivant :

```csharp
public class AddSaveArgs {
    public GameSaveBase? SaveToAdd { get; set; }     // porte un GameId, pas un IGame
    public Func<bool>?  ShouldOverwriteFunc { get; set; }
}
```

Le plugin résout le jeu lui-même :

```csharp
IGame gameById = PluginHelper.DataManager.GetGameById(args.SaveToAdd.GameId);   // RetroArch, l. 828
```

Puis reconstruit la destination à partir du basename de **ce** jeu :

```csharp
text3 = Path.Combine(saveDir, Path.GetFileNameWithoutExtension(gameById.ApplicationPath) + ext);
```

Donc il écrit `Sonic 1.srm`, alors que RetroArch relira `Sonic The Hedgehog (USA, Europe).srm`.

**Le fichier est copié correctement, sous un nom que l'émulateur ne lit jamais. L'opération rapporte un
succès et ne change rien.**

**Pourquoi la substitution ne sauve pas ce cas.** Pour `GetSaves`, on passe le `IGame` nous-mêmes — on
peut donc glisser un `EntryGame`. Pour `AddSaveFile`, `AddSaveArgs` ne transporte **aucun** `IGame` : le
plugin va le chercher lui-même dans le `DataManager`. Il n'y a pas de point d'injection dans les
arguments.

**Le chemin de correction identifié** (pas encore fait) : `PluginHelper.DataManager` *est* notre
`HostDataManagerXml`, et `GetGameById` y est `override`. On peut donc faire renvoyer un `EntryGame` pour
la durée de l'appel — même astuce, un cran plus haut. C'est du travail à faire, et c'est écrit ici pour
que ce soit clair que **restaurer sur une ROM extraite ne marche pas encore**.

---

### Scénario F — Un savestate

**Décor.** Scénario A, tu appuies sur la touche savestate de RetroArch, slot 3.

**1. RetroArch écrit** `…\states\Snes9x\Secret of Mana (USA).state3`.

**2. Le plugin scanne** `Secret of Mana (USA)` + `.state*`, et décode le suffixe :

| Fichier | Slot |
|---|---|
| `.state` | 0 |
| `.state.auto` | −1 |
| `.state3` | 3 |
| `.state10` | **jamais scanné** — `GetPotentialSaveSlots` s'arrête à 9 |

**3. Le record** porte un `<Slot>3</Slot>` et un `SaveGroupName` de « My Save State ». Le backup
correspondant, lui, portera `<Title>Save State 3</Title>` — et *pas* `Saved Game`.

> **Le piège des vignettes.** RetroArch écrit un PNG à côté de chaque savestate :
> `Secret of Mana (USA).state.png`. Le joker du plugin est `basename + ".state*"`, donc **il ramène ce
> PNG**. Ce qui l'élimine, c'est uniquement le décodage du slot : `.png` n'est pas un suffixe de slot
> valide. Autrement dit la vignette est filtrée par accident, à la toute dernière étape. Quiconque
> touchera un jour au parseur de slots doit le savoir : assouplir cette étape fait apparaître un groupe
> « savestate » par vignette.

**4. L'identité d'entrée**, si c'était une archive, ajoute le slot en suffixe :

```
entry:<sig>:<chemin dans l'archive>:s3
```

Le slot est un champ à part dans le record (`<Slot>`), et il est retiré de la clé quand on relit
l'identité d'entrée — un slot n'appartient pas à l'entrée, il appartient au groupe.

---

### Scénario G — Dolphin, ROM archivée

**Décor.** `Zelda Twilight Princess.zip` contenant une ISO GameCube.

**1. Le plugin Dolphin appelle `TryGetDiscId(ApplicationPath, …)`** — c'est-à-dire sur le `.zip`.

**2. Un `.zip` n'est pas une image disque.** L'octet lu à l'offset fixe est du bruit, `TryNormalizeDiscId`
échoue, et le repli `DolphinTool.exe` échoue aussi. Le plugin log *« Failed to detect Disc ID »* et ne
renvoie rien.

**3. Résultat : aucune gestion de save du tout** pour les jeux GameCube/Wii archivés.

**Mais rien n'est perdu.** Dolphin écrit dans sa propre arborescence `User\`, indépendamment d'où venait la
ROM. La save existe, elle est en sécurité, elle est juste invisible.

**Et la même substitution répare ce cas gratuitement** : tendre au plugin le chemin de l'ISO **extraite**
lui donne un fichier lisible comme image disque, le Disc ID sort correctement, et tout fonctionne. C'est
l'argument le plus fort en faveur de la substitution plutôt que d'une réécriture du plugin RetroArch : une
seule modification répare deux plugins.

---

### Scénario H — La passe périodique se déclenche

**Décor.** LiteBox tourne depuis ce matin. Il est 3h du matin, personne n'a touché la machine depuis 4h.
Réglages : sauvegardes automatiques activées, périodiques activées, 24h d'intervalle, 60 min d'inactivité.

**Toutes les 5 minutes, le timer vérifie**, dans cet ordre :

1. Save management activé ? Sauvegardes auto activées ? Périodiques activées ? — sinon, on sort.
2. Une passe tourne déjà ? — sinon, on sort.
3. `RecentState.IsGameRunning` ? `IsExtractionInProgress` ? `BackgroundJobs.Busy` ? — si oui, on sort.
4. La dernière passe date de moins de 24h ? — si oui, on sort.
5. `GetLastInputInfo` donne moins de 60 min ? — si oui, on sort.

Tout est passé → la passe démarre en tâche de fond.

**Elle parcourt les jeux**, et pour chacun : scan, `Backup(force: false)` sur chaque groupe ayant une save
active, puis rétention si quelque chose a été créé. Le curseur est écrit après chaque jeu.

**Si tu fermes LiteBox au milieu**, le curseur reste. La prochaine passe reprend à ce jeu-là.

**Ce qu'elle coûte quand rien n'a changé** : un scan par jeu (des appels plugin et des lectures de
dossier), et zéro écriture — tous les MD5 correspondent. C'est le contrôle de doublon qui rend la chose
supportable.

> **Pourquoi la double garde inactivité + jeu.** Une passe fait tourner tous les plugins d'intégration sur
> tous les jeux de la bibliothèque. La lancer au milieu d'une session est exactement le mauvais moment. Or
> `GetLastInputInfo` mesure clavier et souris : quelqu'un qui joue à la manette depuis deux heures est
> « inactif » pour Windows. D'où la condition supplémentaire, qui n'est **jamais** contournable, quel que
> soit le réglage d'inactivité — même à 0.

---

## 8. Les défauts, et où on en est

Rappel du plan (`docs/save-system-plan.md`), avec l'état réel :

| | Défaut | État |
|---|---|---|
| **D1** | **Identité** — le plugin dérive le nom depuis `basename(ApplicationPath)` (l'archive), l'émulateur a utilisé le nom de la ROM extraite | ✅ **réparé en lecture** (passe par entrée + identité `entry:`) — ❌ **pas en écriture** : voir Scénario E |
| **D2** | **Durabilité** — avec `savefiles_in_content_dir`, la save est écrite dans le cache d'extraction, qui est supprimé à la sortie | ✅ **réparé** par le hook de fermeture, *à condition que les sauvegardes automatiques soient activées* |
| **D3** | **Multiplicité** — une entrée de bibliothèque, N ROMs internes, des groupes indistincts | ✅ **réparé** — un groupe par entrée, combobox dans l'UI |
| **D4** | **Collisions** — rien n'empêche qu'une même save soit réclamée par deux entités | ❌ **ouvert — et observé pour de vrai, chez LaunchBox**, voir ci-dessous |

### D4 n'est plus théorique — observé le 2026-08-26

Décor : *Secret of Mana*, `ApplicationPath = Secret of Mana (USA).sfc`, plus une version
*Secret of Mana (Germany)* pointant sur `Secret of Mana (Germany).sfc`. Rien d'archivé, rien d'exotique.
Backups automatiques activés dans LaunchBox, une partie jouée sur la version.

Résultat dans le XML :

| # | attribué à | `FilePath` |
|---|---|---|
| 3 | **la version Germany** | `…\saves\Snes9x\Secret of Mana (USA).srm` |
| 9 | le jeu | `Emulators\RetroArch\saves\Snes9x\Secret of Mana (USA).srm` |

**Le même fichier porte deux records**, l'un attribué au jeu, l'autre à la version — et LaunchBox l'a
donc sauvegardé **deux fois**, sous deux noms : `Secret of Mana (USA)-02.srm` et
`Secret of Mana (Germany)-01.srm`. Mêmes octets, même date de modification (la copie préserve la mtime),
deux entrées de vault. Idem pour le savestate.

**Ce n'est pas le plugin.** La source décompilée de la passe 1 est sans ambiguïté :

```csharp
string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(item.ApplicationPath);
…
string[] files = Directory.GetFiles(gameSaveDirectory, fileNameWithoutExtension + "*.*");
```

Pour la version, `item.ApplicationPath` est `Secret of Mana (Germany).sfc`, donc le joker est
`Secret of Mana (Germany)*.*`. Il **ne peut pas** ramener `Secret of Mana (USA).srm`. C'est donc l'hôte
qui a créé ces records — comment exactement, on ne peut pas le voir, il est obfusqué.

### D4, deuxième observation — 2026-08-27 : le record fossile

Le lendemain, sur une base repartie de zéro, deux versions de plus (`Play Version…` sur le chemin du jeu,
`Play (France) Version…`) et trois parties jouées. Le mécanisme devient limpide — et il est cette fois
**entièrement expliqué par la source du plugin**, contrairement au cas de la veille.

`Secret of Mana (USA).state` porte deux records :

| # | `AdditionalApplicationId` | `SaveGroupId` | chemin |
|---|---|---|---|
| 5 | `c0f567b1` — *Play Version…* | `c7e36417…` | absolu |
| 13 | *aucun* | `18163948…` | **relatif** |

Le record 13 est celui d'**avant** l'ajout de la version : même `SaveGroupId`, même chemin relatif que
dans la photo de référence prise une heure plus tôt. Depuis, la règle du saut de passe 2 (§3.6) fait que
le jeu n'est plus scanné du tout, et toutes ses saves reviennent attribuées à `Play Version…`.

**LaunchBox n'a pas nettoyé l'ancien record.** Le doublon n'est donc pas produit en continu : c'est un
fossile de migration, qui survit indéfiniment à côté du record courant.

#### Le vrai défaut, côté LiteBox : « fichier disparu » est un diagnostic faux

Dans la vue de base, notre scan apparie le fichier au record 5 (premier trouvé dans l'ordre du document),
et le record 13 reste non apparié. Il tombe alors dans la branche de l'étape 3 — *« record dont le fichier
a disparu »* — et s'affiche en groupe fantôme avec un avertissement.

Or le fichier n'a pas disparu : il est **déjà réclamé par un autre groupe du même scan**. Ce sont deux
situations différentes qui méritent deux messages différents, et on a tout ce qu'il faut pour les
distinguer : au moment où un record reste non apparié, on sait si son `FilePath` a été consommé par un
autre groupe. C'est précisément ce que D4 demande — *détecter et exposer, jamais résoudre en silence* —
mais encore faut-il exposer la bonne chose.

---

**Ce que ça implique pour nous.** LiteBox filtre correctement en vue de base (`InBaseView` rejette un
record dont l'`AdditionalApplicationId` n'appartient pas à une version partageant le chemin du jeu).
En vue **version**, en revanche, ces records sont dans le périmètre alors que le scan live ne les
produit pas : ils tombent dans la branche « record dont le fichier a disparu » et s'affichent en groupes
fantômes. C'est désagréable, mais c'est exactement ce que D4 demande — *détecter et exposer, jamais
résoudre en silence*. Reste à leur donner une présentation honnête plutôt que l'apparence d'un bug.

---

Limitation Saturn connue et **écartée par décision explicite** : le plugin impose son propre
`SaveGroupId = "saturn-<base>"`, qui gagne à la création du record. L'identité d'entrée persistée est donc
perdue pour Saturn (le regroupement à l'écran fonctionne quand même, parce que `EntryKey` vient de la
passe de scan, pas du record). Cas mineur : on ne fait pas d'archives multi-jeux sur des ISO.

Reste au programme : l'export/import de la totalité des backups d'un jeu dans un fichier unique avec ses
métadonnées, la détection des collisions (D4), la correction du chemin d'écriture (Scénario E), et le
back-port vers RomM — où le seul porteur d'identité disponible est le champ texte libre `slot`, confirmé
par les mainteneurs RomM comme un manque connu de leur côté.
