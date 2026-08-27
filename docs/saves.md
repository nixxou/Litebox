# Les sauvegardes — ce qu'on sait, et comment on le sait

> Document unique de référence, écrit le 2026-08-27 après une campagne de mesures sur une install réelle.
> Il remplace `save-system-guide.md` et `save-system-plan.md`, supprimés : ils avaient accumulé
> correction sur correction et plusieurs de leurs affirmations centrales se sont révélées fausses.
>
> **Le document voisin qui reste :** `docs/save-algorithms.md` — l'algorithme de chaque plugin
> d'intégration, lu dans la source décompilée, à la ligne près. Il n'est pas périmé et ce document-ci ne
> le répète pas.
>
> **Le document de l'autre dépôt :** `ExtendDB/docs/lb-save-management.md`. Sa section « Le vault —
> OBSERVÉ » est fausse sur deux points majeurs (voir §2.4). Il porte une note de correction non commitée.

---

## 0. Comment lire ce document

Chaque affirmation porte son statut. Il n'y en a que trois, et ils sont tenus strictement parce que la
confusion entre les deux premiers a coûté plusieurs jours :

| | |
|---|---|
| **[SOURCE]** | lu dans du code décompilé non obfusqué. Certain. |
| **[MESURÉ]** | observé sur une install réelle, avec le protocole du §6. Le nombre de mesures est donné. |
| **[OUVERT]** | pas établi. Rien n'en dépend dans le code, ou ce qui en dépend est marqué comme provisoire. |

Tout ce qui n'a pas de statut est du raisonnement à partir de ce qui précède.

---

## 1. Le modèle de données

### 1.1 Un record décrit UN FICHIER

C'est le point de départ, et il a été le plus long à admettre.

```xml
<GameSave>
  <GameId>5925b287-…</GameId>                  <!-- le jeu propriétaire -->
  <AdditionalApplicationId>c0f567b1-…</AdditionalApplicationId>   <!-- ou absent -->
  <EmulatorFileName>retroarch.exe</EmulatorFileName>
  <EmulatorCore>snes9x_libretro</EmulatorCore>
  <Title>Save State 0</Title>                  <!-- un LIBELLÉ, voir 1.3 -->
  <SaveGroupName>My Save State</SaveGroupName>
  <SaveGroupId>d5d27709…</SaveGroupId>         <!-- l'identité du groupe -->
  <MatchLineageId>d5d27709…</MatchLineageId>   <!-- la lignée, voir 1.4 -->
  <FilePath>…</FilePath>
  <OriginalFileName>…</OriginalFileName>
  <Slot>0</Slot>
</GameSave>
```

**[SOURCE]** Les `<GameSave>` sont des éléments de **premier niveau**, frères de `<Game>`, reliés
uniquement par `<GameId>`. Ordre dans le fichier : `Game`, `AdditionalApplication`, `AlternateName`,
`GameSave`.

**Un GROUPE est l'ensemble des records partageant un `SaveGroupId`.** Un groupe a une save vivante (ou
zéro) et N copies. Chaque fichier a son propre record.

### 1.2 Ce qui distingue une save vivante d'une copie : le CHEMIN

**[MESURÉ]** Un record dont le `FilePath` est sous `<LB>\Saves\` décrit une copie ; ailleurs, c'est la
save vivante. C'est le **seul** critère fiable.

> **Ce qui était faux.** J'ai longtemps classé d'après la présence d'un `Title`, sur la constatation que
> tout record vault en portait un et aucun record vivant. La corrélation a tenu sur quinze records puis a
> lâché : *Restore Backup* libelle la ligne qu'il promeut, et un record **vivant** est revenu portant
> `Title = Save State 0`.
>
> Ce n'était pas cosmétique : la save active aurait été lue comme une copie, aurait disparu de la page, et
> se serait fait ré-enregistrer sous un groupe neuf en laissant un fantôme.

### 1.3 `Title` est un libellé libre

**[MESURÉ]** Un record planté avec `<Title>Zorglub</Title>` s'affiche **« Zorglub »**, verbatim, comme
intitulé de la ligne dans Backup History. `Saved Game` et `Save State <slot>` ne sont donc pas un
vocabulaire : ce sont les libellés **par défaut**, et **Edit Label** les remplace.

Le libellé sert aussi de sous-titre sur la carte du groupe.

### 1.4 `MatchLineageId` — vu servir une seule fois

**[MESURÉ]** Il vaut le `SaveGroupId` dans tous les records observés, **sauf un** : la graine créée par
*Make New Save*, qui reçoit une lignée neuve, distincte de son propre `SaveGroupId`.

**[MESURÉ]** Un renommage ne le touche **pas**. Une fusion, si : les records de la source adoptent la
lignée de la destination. C'est donc bien un identifiant de groupe qui **survit à la fusion**, et pas
au renommage — le nom du champ (« lineage ») est trompeur.

**[OUVERT]** Ce que LaunchBox en fait ensuite. On sait qu'il l'écrit et le propage ; on n'a encore vu
aucun comportement qui le *lise*.

### 1.5 Ce qui n'est PAS écrit dans le XML

**[SOURCE]** La classe `GameSave` du cœur porte 16 propriétés. Celles destinées au XML ont l'attribut
`[DataTableExport]`. Trois ne l'ont pas :

`ReportedFileSizeBytes` · `ReportedLastModifiedUtc` · **`Md5`**

Donc **LaunchBox calcule un hash mais ne le persiste jamais**. Vérifié aussi côté disque : aucun sidecar,
aucune base dans `LB\Saves\`.

**[MESURÉ]** Le hash affiché à côté du cadenas, qui ressemble à un CRC32, est le **MD5 tronqué à 4 octets** :

```
LaunchBox   C791049A
MD5         C791049A5E84674715F52A28D0E4E26B     ← les 8 premiers caractères
CRC32 réel  992A7EBF                             ← rien à voir
```

Notre `FileMd5` produit donc exactement la même valeur que le sien.

### 1.6 Chemins relatifs ou absolus

**[MESURÉ]** LaunchBox écrit un `FilePath` **relatif** à la racine LB quand il **met à jour** un record, et
**absolu** quand il en **crée** un. LiteBox écrit toujours absolu. Les deux formes se relisent des deux
côtés, et LiteBox préserve la forme de LaunchBox au lieu de la reconvertir.

---

## 2. Le vault

### 2.1 Emplacement et nommage

`<LB>\Saves\<Plateforme>\`, à plat.

**[MESURÉ]** Le nom d'une copie est le **basename de la ROM** — celle du jeu, ou celle de la version qui
possède la save — avec l'**extension normalisée**, puis `-01`, `-02`… si le nom est pris.

- Un savestate est toujours `.state`, quel que soit le slot : un `.state2` vivant est copié en `.state`.
- Le vrai nom du fichier de save part dans `OriginalFileName`.
- Le suffixe `-NN` n'est utilisé que si le nom nu est déjà pris.

La preuve que c'est la ROM et non le titre : le jeu s'appelle `Super Mario World 2: Yoshi's Island`
(deux-points) et sa copie `Super Mario World 2 - Yoshi's Island.srm` (tiret) — le tiret ne peut venir que
du nom de fichier de la ROM.

**Curiosité non expliquée** : LaunchBox assainit l'apostrophe en underscore dans `OriginalFileName`
(`Yoshi_s`) mais pas dans le nom du fichier qu'il crée (`Yoshi's`). **[MESURÉ]** Renommer le fichier pour
coller à la forme assainie ne change rien à sa découverte, donc ce n'est pas un critère de recherche.

### 2.1bis Deux bugs de LaunchBox repérés en passant

**L'import écrit son record DEUX FOIS.** Deux lignes identiques — même `SaveGroupId`, même fichier — à
ceci près que l'une porte l'`AdditionalApplicationId` et l'autre non. LaunchBox n'affiche qu'une carte,
donc il regroupe bien par `SaveGroupId` ; c'est l'écriture qui est en double.

**Un groupe attribué à une version compte sa propre save vivante comme un backup** (§2.3), et un record
fossile fait perdre un point au groupe légitime (§2.3 aussi).

### 2.2 Un fichier sans record n'existe pas

**[MESURÉ], trois fois, sur deux jeux.** Une copie posée dans le vault, au nom exact attendu, contenu
identique à la save vivante, sans record : elle n'est **pas listée** dans Backup History, **pas comptée**,
et LaunchBox en **refait une** au lieu de la reconnaître.

C'est la démonstration que **le dossier n'est pas un index**. Cinq orthographes candidates ont été plantées
simultanément dans le vault, chacune avec un contenu distinct : aucune n'a été ramassée.

> **Ce qui était faux.** `lb-save-management.md` affirme que LaunchBox « re-dérive les métadonnées du
> système de fichiers au moment du scan ». C'est faux, et j'ai traîné ce modèle plusieurs jours.

### 2.3 Le nombre affiché sur la carte

**[MESURÉ], huit groupes.** Ce n'est pas le nombre de copies.

```
compte = copies archivées
       + 1  si le groupe est attribué à une VERSION (AdditionalApplicationId) et a une save vivante
```

Un groupe attribué au **jeu** ne compte pas sa ligne vivante ; un groupe attribué à une **version**, si.
Backup History montre la même chose : la ligne vivante n'y apparaît que dans le cas « version ».

Établi par élimination, une variable à la fois. Ce qui a été écarté au passage, chacun par sa mesure : le
nom du fichier, l'apostrophe, et la présence d'une copie sans record.

**La mesure décisive** : un jeu, vault vide, un seul record vivant → **0**. On ajoute une version pointant
sur la ROM du jeu, rien d'autre ne bouge → **1**, et l'historique liste la save vivante.

**Le contrôle qui sépare l'attribution de la simple existence de versions** : la même version repointée sur
une **autre** ROM → retour à **0**. Une version ne change donc la page du jeu que lorsqu'elle lui **prend**
ses saves.

C'est très probablement **un bug chez eux** : une save jamais sauvegardée affiche « 1 Backup » avec une
pastille verte. Reproduit exprès — la parité est l'objectif, et un nombre qui contredit le leur serait pire.

**[MESURÉ]** Un **record fossile** — un second record nommant la même save vivante, laissé là quand une
version s'est mise à couvrir la ROM du jeu — fait perdre **un point** au groupe légitime. Retiré, le compte
redevient juste. Un fossile ne fait donc pas qu'encombrer : il fausse le nombre.

### 2.4 Deux affirmations de la RE d'origine qui sont fausses

De `ExtendDB/docs/lb-save-management.md`, section « Le vault — OBSERVÉ (2026-07-04) » :

| affirmation | statut |
|---|---|
| « Il n'y a PAS un record `<GameSave>` par backup » | **partiellement vrai** : il n'y a pas un record par *backup pris*, mais il y a bien un record par *fichier référencé*, y compris dans le vault |
| « LB dérive les métadonnées d'un backup du système de fichiers au moment du scan » | **faux** (§2.2) |

L'observation d'origine avait été faite alors qu'aucun backup n'existait sur l'install : LaunchBox n'avait
rien écrit parce qu'il n'y avait rien à écrire.

---

## 3. Les comportements

Chaque ligne a été déclenchée dans LaunchBox et l'effet lu dans le XML et le dossier.

| action | ce qu'elle fait | statut |
|---|---|---|
| **Backup Save** | copie la save vivante dans le vault et écrit son record | [MESURÉ] |
| **dédoublonnage** | existe et fonctionne. Sur un contenu inchangé : *« This save file was already backed up! »* | [MESURÉ] |
| **rétention** | `MaxAutoBackupsPerGame` est appliqué et supprime les **fichiers**, pas seulement les records. Plafond à 2 avec 5 copies : il en reste 2, les plus récentes | [MESURÉ] |
| **Backup Now** (balayage) | crée les copies **et** écrit leurs records | [MESURÉ] |
| **fermeture de partie** | fait les TROIS : crée le record de la save vivante, copie dans le vault, et écrit le record de la copie. Sans qu'on ait ouvert aucune page. La save est attribuée à la **version qui a lancé**, pas au jeu. `LastLibrarySaveScanUtc` n'est pas touché : ce n'est pas un balayage | [MESURÉ] |
| **ouvrir la page** | peut créer des copies pour les groupes qui n'en ont aucune — et dans ce cas **sans écrire de record** | [MESURÉ], voir §5 |
| **Restore Backup** | archive d'abord la save courante (« *the current active save will be moved into backup history* »), demande un **slot cible**, écrit sous le basename de la ROM, et **libelle** les deux lignes | [MESURÉ] |
| **Delete Save** | supprime le **fichier** et le record. *« This will permanently delete the save file. This cannot be undone. »* | [MESURÉ] |
| **Make New Save** | crée un **groupe** neuf : nom demandé, `SaveGroupId` neuf, **lignée neuve**, et une graine marquée `-NewSave-<guid>` dans `OriginalFileName` | [MESURÉ] |
| **Edit Label** | écrit le `Title` du record | [MESURÉ] |
| **Import Save Game/State File** | ne rend PAS le fichier actif et ne touche pas à la save vivante. Il **copie** le fichier (la source reste en place) dans le vault sous le basename de la ROM au prochain suffixe libre, et crée un **groupe neuf** : `SaveGroupId` neuf, lignée égale, nom par défaut, pas de `Title`, `OriginalFileName` = le vrai nom du fichier source. Aucun dialogue. C'est « Make New Save » ensemencé par un fichier choisi | [MESURÉ] |
| **Combine With Another Save** | pur Ré-ÉTIQUETAGE. Les records de la source prennent le `SaveGroupId` **et** le `MatchLineageId` de la destination ; tous les records du groupe résultant prennent le `SaveGroupName` de la **source** ; **aucun fichier n'est touché** | [MESURÉ] |
| **Edit Name** | s'appelle *Edit Names*, deux champs : le nom du groupe, et le libellé de la save active (= le `Title` du record vivant). Le nom est propagé à **tous** les records du groupe, copies comprises. `MatchLineageId` ne bouge pas. Le champ libellé pré-remplit un défaut, et le valider inchangé n'écrit rien | [MESURÉ] |

### 3.1 La capture à la fermeture d'une partie

**[MESURÉ]** Le test le plus net de la campagne : Road Rash remis à zéro total — aucun record sur toute la
plateforme, aucun fichier de save, vault vide — puis une partie, un savestate, et **rien d'autre**. La page
Game Saves n'a jamais été ouverte, ce qui est tout l'intérêt.

Résultat, sans aucune intervention :

```
states\Gambatte\Road Rash (USA, Europe).state             md5 0DBB6FB4   14:34:28   <- l'émulateur
Saves\Nintendo Game Boy\Road Rash (USA, Europe).state     md5 0DBB6FB4   14:34:28   <- la copie
Nintendo Game Boy.xml                                                    14:34:33   <- 2 records
```

Deux records écrits : celui de la save vivante (sans `Title`) et celui de la copie (`Title = Save State 0`),
tous deux portant l'`AdditionalApplicationId` de **la version qui a lancé le jeu**.

La fermeture de partie fait donc les trois choses : découvrir, copier, enregistrer. C'est exactement ce que
fait `SaveBackupService.OnGameClosed`, et ce point passe d'ajout non vérifié à **parité**.

`LastLibrarySaveScanUtc` n'a pas bougé — la fermeture de partie n'est pas un balayage de bibliothèque.

**Le moment de la capture**, qui était la question ouverte : les fichiers portent 14:34:28 et le XML
14:34:33, soit **cinq secondes après**. LaunchBox capture donc après que l'émulateur a fini d'écrire. Notre
hook part dès la sortie du processus — plus tôt, mais nécessairement : il doit passer **avant** la purge du
cache d'extraction, qui supprime la bande `\tmp` où la save peut se trouver.

La vignette `.state.png` écrite par RetroArch n'a reçu aucun record : le décodage du slot la filtre.

### 3.2 Les slots

**[MESURÉ]** `.state` → 0 · `.state.auto` → −1, affiché **« Slot Auto »** · `.stateN` → N.

**Il n'y a pas de plafond à 9.** Un `.state10` posé dans le dossier states est bien remonté, en slot 10,
par les deux interfaces. `save-algorithms.md` affirmait le contraire d'après `GetPotentialSaveSlots` ;
cette méthode sert à proposer une cible à la restauration, pas à borner le scan.

---

## 4. Côté LiteBox

### 4.1 L'architecture

Les deux couches basses sont **partagées, pas réimplémentées** : le SDK
(`Unbroken.LaunchBox.Plugins.dll`) et les vrais plugins d'intégration, hébergés en process via
`EmuPlugins`. Seule la couche hôte est à nous (`Host\Saves\SaveManager.cs`).

Conséquence : tout ce qui concerne *où vivent les saves et comment elles se nomment* est identique par
construction. Ce qui a demandé du travail, c'est la couche au-dessus — le groupement, les records, le
vault, l'affichage.

### 4.2 État de la parité

Aligné et vérifié : nommage du vault, absence d'index, écriture des lignes de backup, horodatage et
curseur de scan (aux clés de LaunchBox), rétention, dédoublonnage, compte affiché, `Title` comme libellé,
*Edit Label*, backup avant restauration, *Delete Save*, décodage des slots.

**Un écart assumé, un seul.** LaunchBox écrit parfois des copies sans record (ouverture de page).
LiteBox écrit **toujours** la ligne. La raison est le badge : c'est le seul signal lisible **sans toucher
au disque**, et la contrainte de perf des badges interdit l'I/O par jeu. Le format ne diverge pas — seule
la fréquence. Suivre LaunchBox ici rendrait invisibles des backups qui viennent d'être pris.

### 4.3 Ce qui est mis de côté

Deux fonctionnalités développées puis **retirées volontairement**, pour établir la parité sur une base
saine avant de les réintroduire. Rien n'est supprimé, tout est documenté à l'endroit où ça revient.

**La passe par entrée d'archive** — `SaveManager.EntryScan`, à `false`. Elle donne au plugin le chemin
d'une entrée d'archive pour qu'il calcule le nom que l'émulateur a réellement utilisé, et porte l'identité
dans un `SaveGroupId` de la forme `entry:<signature>:<chemin dans l'archive>`. `SaveEntries`, `EntryGame`
et le sélecteur d'entrées sont intacts : remettre le drapeau à `true` ranime l'ensemble.

> Un acquis à ne pas perdre : **LaunchBox relit, réécrit et propage un `SaveGroupId` de cette forme sans
> l'interpréter**, y compris dans `MatchLineageId` et dans les backups qu'il crée. Vérifié sur données
> réelles. Les plugins s'en servent déjà comme espace de noms (`saturn-<base>`, `pcsx2:<carte>:<dossier>`).

**Les sous-dossiers `Manual\` / `Auto\`** — seul porteur possible de la distinction automatique/manuel,
que le format de LaunchBox n'a nulle part. Conséquence immédiate de leur retrait : **la rétention ne peut
plus épargner un backup fait à la main**. C'est dit dans l'aide du réglage et dans `Prune`.

### 4.4 Les outils

**`--probe-saves <titre> [--lbroot <LB>]`** — rejoue exactement le pipeline de la page Game Saves, en
lecture seule, et imprime chaque maillon : `NamingHelper.RootFolder`, les lignes de commande effectives,
le résultat brut de chaque plugin, les répertoires résolus, puis le résultat de `SaveManager.ScanBase`.
C'est l'outil qui a permis toutes les comparaisons de cette campagne.

**`Core\litebox\saves-diag.log`** — trace de chaque scan, écrite au fil de l'eau.

---

## 5. Ce qu'il reste à creuser

Par ordre d'utilité.

**Les saves-DOSSIERS.** `lb-save-management.md` affirme que LaunchBox les archive en `.7z`. Ça vient de la
section dont deux autres conclusions se sont révélées fausses, et **aucun backup de dossier n'a jamais été
observé**. LiteBox copie le dossier tel quel plutôt que d'inventer un format. Il faut un jeu Dolphin (NAND
Wii, dossiers GCI) ou PCSX2 pour trancher. **C'est le plus gros trou restant.**

**Backup Now, précisément.** On l'a déclenché une fois et on sait qu'il crée les copies, écrit leurs
records et applique la rétention. Ce qu'on ne sait pas : quels jeux il parcourt (toute la bibliothèque,
ou seulement ceux qui ont déjà des saves), s'il écrit les clés de curseur pendant qu'il tourne, s'il
met à jour `LastLibrarySaveScanUtc` à la fin, et ce qu'il fait d'un jeu sans aucun record.

**La sauvegarde périodique.** `PeriodicSaveBackupEnabled` est à `true` et on n'a **jamais** vu la tâche
partir. Quand se déclenche-t-elle, à quel intervalle, sous quelles conditions ? LaunchBox n'expose aucun
réglage d'intervalle, donc la valeur est en dur quelque part. Nos deux réglages (intervalle, inactivité)
sont des ajouts assumés — mais on ne sait pas de quoi ils divergent.

**Les deux boutons de maintenance.** *Repair Save Metadata* et *Clear All and Re-scan Save Metadata*.
Je les avais rangés en « non observable de l'extérieur » : **c'est faux**, il suffit de les cliquer et de
lire le diff du XML. Ce sont les deux boutons dont nos équivalents portent le nom sans revendiquer la
parité, et le drapeau one-shot `SaveVaultMetadataRepairComplete` suggère une migration plutôt qu'un
nettoyage répétable — vérifiable en regardant s'il passe à `true`.

**Pourquoi l'ouverture d'une page crée parfois des copies sans record.** Observé une fois, sur trois
groupes neufs simultanés, dans un état qui empilait trop de variables pour conclure. À reproduire
proprement, sur un seul groupe.

**Le défaut de `AddSaveFile` sur une ROM extraite.** `AddSaveArgs` ne transporte aucun `IGame` : le plugin
résout le jeu lui-même via `PluginHelper.DataManager.GetGameById` et reconstruit la destination depuis
`gameById.ApplicationPath`. Sur une ROM extraite il écrit donc le nom de l'**archive**, que l'émulateur ne
relira jamais. **[SOURCE]** Le chemin de correction est identifié — `HostDataManagerXml.GetGameById` est
`override`, on peut lui faire renvoyer un `EntryGame` le temps de l'appel — mais ça n'a de sens qu'une fois
`EntryScan` réactivé.

---

## 6. Le protocole qui a fini par marcher

Écrit ici parce que les trois premières journées de cette campagne ont été perdues à ne pas le suivre.

**Une variable par lancement.** Empiler un record fantôme, un `Title` bizarre, un plafond de rétention et
trois groupes neufs dans le même test produit un état illisible dont on ne peut rien tirer — et toute
règle qu'on en extrait est un ajustement sur des données polluées.

**Noter l'état d'avant, y compris ce que LiteBox répond.** `--probe-saves` avant, capture LaunchBox après,
puis relecture du XML et du dossier. Sans le « avant », un changement est indistinguable d'un état
préexistant.

**Des contenus distincts, jamais des copies identiques.** Chaque fichier planté reçoit quelques octets
modifiés, donc un md5 unique. L'interface affiche ce md5 : le fichier que LaunchBox a ramassé se dénonce
tout seul, sans qu'on ait à le deviner.

**Poser tous les candidats d'un coup quand l'hypothèse porte sur un nom.** Cinq orthographes plantées
ensemble répondent en une manipulation, là où cinq essais successifs prennent cinq lancements et laissent
l'état dériver entre chaque.

**Concevoir le test pour qu'il réponde quelle que soit la réponse.** Un test dont un seul résultat est
informatif ne vaut pas le lancement.

**Chercher le contrôle qui sépare deux causes possibles.** Ajouter une version pointant sur la ROM du jeu
a changé le compte — mais ça changeait deux choses à la fois. Repointer la même version ailleurs a séparé
l'attribution de la simple existence de versions.
