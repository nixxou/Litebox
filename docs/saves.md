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

### 1.5bis `LastLibrarySaveScanUtc` est mal sérialisé, et il faut le reproduire

**[MESURÉ]** LaunchBox écrit le nombre **UTC** mais l'étiquette avec le décalage **local**, puis le relit
en ignorant l'étiquette. Un balayage lancé à 14:50:42 locales (UTC+2) a été stocké ainsi :

```
2026-08-27T12:50:42.6874085+02:00      <- 12:50 est l'heure UTC, "+02:00" est le décalage local
```

…et sa propre interface a affiché « 2:50 PM ». Il relit donc le nombre comme de l'UTC et jette le décalage
qu'il venait d'écrire.

Conséquence directe : écrire un horodatage local **correct** (`14:50:42+02:00`, le même instant) le ferait
apparaître deux heures en retard dans LaunchBox. Il faut donc reproduire le défaut — écrire le nombre UTC
en `Unspecified` via `XmlConvert` en mode `Local`, ce qui appose le décalage local sans convertir, et le
relire comme de l'UTC en jetant le décalage.

**[MESURÉ]** Les deux clés voisines `LastLibrarySaveScanCursorGameId` et `LastLibrarySaveScanCursorAdditionalAppId` sont **effacées du fichier quand un balayage se termine**, mais elles **survivent à un balayage interrompu** : trouvées des heures après, pointant sur un jeu Arcade et sa version, puis disparues dès qu'un *Backup Now* est allé au bout. Elles ne sont donc pas un état transitoire mais un vrai point de reprise persistant.

### 1.5ter LaunchBox RESERIALISE le fichier entier — rien d'inconnu ne survit

**[MESURÉ]** Trois formes de donnée inconnue plantées d'un coup dans deux `<GameSave>` : un attribut sur
la balise (`LiteBoxAttr`), un élément à nous (`<LiteBoxOrigin>`), et un élément au nom plausible
(`<BackupOrigin>`) pour voir si le filtrage se ferait sur le nom.

Un des deux records était la **cible** — celui dont LaunchBox allait réécrire le `SaveGroupName` via
*Edit Name*. L'autre était le **témoin** : un savestate d'un autre jeu, sur la même plateforme, que
personne n'a touché. Sans ce témoin, une disparition n'aurait pas distingué « il réécrit ce qu'il
modifie » de « il resérialise tout ».

Après une seule action et une fermeture propre : **les six plants ont disparu**, cible et témoin
compris.

**Ré-établi sur instrumentation propre (M4).** Ce premier relevé souffrait d'un défaut d'outillage (§1.5quater) qui aurait pu tout expliquer. Refait avec un plant au niveau octet — **+66 octets, CRLF conservés, sans BOM** — et l'action la plus minimale qui écrive (ouvrir la page, *OK*, sans rien modifier) : l'attribut et l'élément ont **disparu quand même**. La conclusion ne doit donc rien au défaut.

```
avant   md5 A696D386…  32603 o   cible + témoin : attribut + 2 éléments
apres   md5 2261D4E9…  33030 o   cible + témoin : ABSENT, ABSENT, ABSENT
```

Ce n'est donc ni un filtrage par nom, ni une différence élément/attribut : LaunchBox désérialise **tous**
les records de la plateforme vers son modèle objet et les réécrit. Ce qui n'est pas dans le modèle est
perdu, y compris sur des records qu'aucune action ne visait.

> **Conséquence de conception, définitive.** On ne peut rien stocker à nous dans un `<GameSave>`. Le
> marqueur automatique/manuel du §4.3 ne peut donc pas vivre dans le record : il ira dans le **chemin**
> (un sous-dossier `Auto\`), seul porteur qu'une réécriture ne peut pas effacer.

### 1.5quater L'« absorption » d'une save vivante était un ARTEFACT — le mien

Un relevé antérieur montrait deux saves vivantes changeant de groupe toutes seules pour rejoindre le
groupe du vault partageant leur basename, en adoptant son `SaveGroupId` **et** son `MatchLineageId`.
J'en avais fait un comportement de LaunchBox, et le seul jamais vu qui *lise* `MatchLineageId`.

**C'était faux, et la cause était mon outillage.** Mes scripts de plantage lisaient le XML en texte
`utf-8-sig` puis le réécrivaient avec `newline=''`. Effet mesuré :

```
fichier écrit par LaunchBox   33014 o   CRLF=707   LF seuls=0     BOM=non
après passage de mon script   32377 o   CRLF=0     LF seuls=708   BOM=OUI
```

Les 707 fins de ligne converties, un BOM ajouté que LaunchBox n'écrit jamais. Ce n'était donc pas
« j'ai ajouté un élément inconnu » mais **le fichier entier réécrit dans une autre convention** — un
écart autrement plus lourd, et le candidat évident pour déclencher une réconciliation.

Quatre mesures l'ont établi, une variable à la fois :

| mesure | action | écriture ? | rapprochement ? |
|---|---|---|---|
| M1 | ouvrir la page, **Cancel** | non | non |
| M2 | ouvrir la page, **OK** | **oui** (chemin absolu → relatif) | non |
| M3 | ouvrir, *Edit Name* sur un groupe, OK | oui | non — **strictement cantonné au groupe édité** |
| M4 | plant **propre** (+66 o, CRLF gardés, sans BOM), ouvrir, OK | oui | **non** |

Le sujet était à chaque fois un jeu réellement éclaté — Yoshi's Island, une save vivante et deux copies
dans trois `SaveGroupId` distincts. Aucun rapprochement, jamais. **Rien dans ce document ne repose plus
sur l'absorption**, et la scission entre une save vivante et ses copies ne se répare donc PAS toute
seule : c'est une raison de plus pour que notre *Clear and re-scan* restitue les identités.

> **La leçon, parce qu'elle s'est reproduite deux fois.** J'avais d'abord attribué le phénomène à
> l'ouverture de la page, en le déduisant d'un relevé qui enchaînait trois actions. M1 l'a réfuté. J'ai
> alors accusé *Edit Name* : M3 l'a réfuté. Ce n'est qu'en mesurant **mon propre instrument** que la
> cause est apparue. Une mesure ne vaut que si l'outil qui la prend ne modifie que ce qu'il prétend
> modifier — et ça se vérifie, ça ne se suppose pas : ici, un simple compte d'octets suffisait.

### 1.5quinquies Ce que fait *OK* et ce que fait *Cancel*

**[MESURÉ]** Fermer la page Game Saves par **Cancel** n'écrit **rien**, malgré le bandeau
« *Navigating will save immediately* ». Fermer par **OK** écrit : les records concernés sont réécrits —
c'est là qu'un `FilePath` absolu devient relatif (§1.6) — et le `<DateModified>` du **jeu** est
réhorodaté. En M4, à part `DateModified` et la disparition du plant, le fichier est ressorti identique
à l'octet près.

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

**Curiosité expliquée depuis** : LaunchBox assainit l'apostrophe en underscore dans `OriginalFileName`
(`Yoshi_s`) mais pas dans le nom du fichier qu'il crée (`Yoshi's`). **[MESURÉ]** L'assainissement se fait
à la **réécriture** : lors du relevé du §1.5ter, un record que personne ne visait est passé tout seul de
`Super Mario World 2 - Yoshi's Island.state` à `… - Yoshi_s Island.state`. C'est donc une normalisation
appliquée par la sérialisation, pas une règle de nommage. **[MESURÉ]** Renommer le fichier pour
coller à la forme assainie ne change rien à sa découverte, donc ce n'est pas un critère de recherche.

### 2.1bis Deux bugs de LaunchBox repérés en passant

**L'import écrit son record DEUX FOIS.** Deux lignes identiques — même `SaveGroupId`, même fichier — à
ceci près que l'une porte l'`AdditionalApplicationId` et l'autre non. LaunchBox n'affiche qu'une carte,
donc il regroupe bien par `SaveGroupId` ; c'est l'écriture qui est en double.

**Un groupe attribué à une version compte sa propre save vivante comme un backup** (§2.3), et un record
fossile fait perdre un point au groupe légitime (§2.3 aussi).

### 2.1ter Les saves-DOSSIERS : une copie de dossier, plus un manifeste

**[MESURÉ]** La question est tranchée, et la réponse contredit la RE d'origine : **ce n'est pas un `.7z`**.
LaunchBox copie le dossier tel quel, sous le basename de la ROM, et **y ajoute un fichier** :

```
Saves\Nintendo Wii\Animal-Crossing-Lets-Go-to-the-City-Europe-En-Fr-De-Es-It\
    banner.bin          120   (copié verbatim)
    game.dat            527
    nature.dat          265
    manifest.sha256     229   ← ajouté par LaunchBox
```

Le manifeste, une ligne par fichier, trié, **CRLF**, sans BOM, SHA-256 en majuscules :

```
banner.bin|A651A9C222A8E681863BD374798C720874465DC1EC4F604A52D7BF04AA2A1A39
game.dat|64205692A9E024C1FE48D92A5E7F222CE2426EFD1309214C9331C9AD74E9FC56
nature.dat|2670C258DBB5E6E3EC8A7C3179740A78AA967B4F3819C06BB0B97B198B877C78
```

Les 229 octets se recomptent exactement, ce qui confirme qu'il y a bien un CRLF **après la dernière
ligne** aussi.

> **Le piège, et il l'évite.** Le manifeste vit dans la copie et jamais dans le dossier vivant : un
> contrôle de doublon qui le hacherait trouverait une copie éternellement différente de sa propre source,
> et recopierait le dossier à chaque passage.
>
> **[MESURÉ]** LaunchBox n'y tombe pas. Sur une save Wii dont le dossier vivant et la copie sont
> identiques **hors manifeste** et différents **avec**, son *Backup Save* répond
> *« This save file was already backed up! »* et n'écrit rien. Son dédoublonnage ignore donc le manifeste.
>
> L'exclusion dans notre `DirManifestMd5` est par conséquent de la **parité**, pas une correction — la
> première rédaction de ce paragraphe laissait entendre l'inverse.

Le record d'une save Wii apporte deux choses de plus :

```
SaveGroupId       dolphin:wii:<gameId>:00010000:52555550    ← identité fournie par le plugin
DisplayChipText   Disc Save                                 ← une pastille, affichée à côté du nom
OriginalFileName  data                                      ← le nom du dossier source
```

### 2.3bis Les copies SUCCESSIVES d'une save-dossier

**[MESURÉ]** On n'avait vu qu'une seule copie de dossier. La deuxième se comporte comme un fichier sur
tous les points qui comptent :

```
                              sans manifeste   CREATION              manifeste
VIVANT   data                 BADF014B         27/08 15:11:38        non
vault    <base>               8F41BF4C         27/08 15:13:45        OUI
vault    <base>-01            BADF014B         27/08 23:43:09        OUI
```

- **le nommage** prend le suffixe `-01`, exactement comme pour un fichier ;
- **le manifeste est écrit dans CHAQUE copie**, pas seulement la première ;
- **le contenu** de la nouvelle copie est celui de la save vivante, au bit près ;
- et **la date de création du dossier copié est celle du BACKUP**, pas celle de la source.

Ce dernier point était le vrai risque. La rétention **et** le cadenas reposent tous deux sur la date de
création (§3.4bis, §3.4ter), et on ne les avait mesurés que sur des fichiers. Si un dossier copié héritait
de la date de son original, l'ordre d'éviction serait faux pour tous les formats Wii, GameCube et PS2, et
un cadenas posé dessus ne tiendrait pas. Il tient.

**Une limite de notre côté**, notée sans être corrigée : la récupération d'une copie orpheline
(`FindIdenticalInVault`) n'énumère que des **fichiers**. Un dossier de vault abandonné sans record par
leur balayage ne sera donc pas récupéré mais dupliqué. Le cas est étroit — il faut un balayage de leur
côté sur un jeu à save-dossier que notre page n'a jamais ouvert — et le remède est le même que pour les
fichiers si le besoin se présente.

Le `SaveGroupId` de Dolphin est **namespacé**, comme ceux de PCSX2 (`pcsx2:<carte>:<dossier>`) et de
Saturn (`saturn-<base>`). Troisième plugin à s'en servir — c'est bien un champ texte que LaunchBox
transporte sans l'interpréter.

### 2.3ter Les saves CONTENEUR — PCSX2 produit aussi un dossier

**[SOURCE]** Une save PCSX2 ne vit pas dans un dossier : elle vit **dans** la carte mémoire, un fichier
unique contenant une arborescence. Mais le plugin pose `IsSaveContainer = true`, et son `TryBackupSave`
**extrait** le répertoire du jeu hors de la carte.

Notre `Backup` a donc trois cas, pas deux :

```
IsSaveContainer      -> le plugin extrait vers un dossier temporaire  -> copie DOSSIER
ActiveIsDirectory    -> le dossier vivant lui-meme (Dolphin, NAND Wii) -> copie DOSSIER
sinon                -> le fichier vivant (RetroArch .srm/.state)      -> copie FICHIER
```

Une sauvegarde PCSX2 emprunte donc **exactement le chemin validé sur Dolphin** (§2.3bis) : nommage `-01`,
manifeste écrit dans chaque copie, taille calculée, et date de création du **répertoire** — donc rétention
et cadenas corrects.

**[OUVERT]** Rien de tout ça n'est mesuré : l'install de test n'a ni plateforme PS2 ni PCSX2. C'est déduit
du contrat du plugin et de notre code, pas observé. Une vérification demanderait un PCSX2 configuré, une
carte mémoire avec au moins deux jeux, et deux sauvegardes successives.

**Deux subtilités propres au conteneur**, à connaître avant de mesurer :

**La taille** vient d'abord de `ReportedFileSizeBytes`, que le plugin renseigne — la taille de la *save*.
Le repli sur la taille du fichier ne joue que s'il ne dit rien, et afficherait alors la carte entière.

**Le contrôle « sauvegarde à jour »** (`NeedsBackup`) lit le mtime de `ActivePath`, c'est-à-dire **la
carte**. Elle change dès qu'un *autre* jeu de la même carte est joué : un jeu PS2 peut donc porter le ⚠
sans que sa propre save ait bougé. C'est cosmétique — le dédoublonnage compare le contenu **extrait**,
donc aucune copie parasite n'est créée. Le rendre exact demanderait de hacher l'extraction à chaque
affichage, ce qui coûterait un appel de plugin par carte à chaque ouverture de page.

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
| **rétention** | `MaxAutoBackupsPerGame` s'applique **PAR GROUPE** (§3.4) et fait bien la fenêtre glissante que sa doc décrit : une copie arrive, la plus ancienne part. La victime est la copie dont la **date de création** est la plus ancienne — ni le mtime, ni le nom, ni l'ordre des records (§3.4bis) | [MESURÉ] |
| **la capture saute une partie sur deux** | le défaut le plus coûteux de tous : la décision est prise en comparant la save **du début** de partie au dernier backup, mais c'est le contenu de **fin** qui est archivé. La partie suivante est donc systématiquement ignorée (§3.7) | [MESURÉ] |
| **Backup Now** (balayage) | copie, dédoublonne, efface les clés de curseur en fin de course et met `LastLibrarySaveScanUtc` à jour. Mais il n'écrit un record que pour un groupe qui en a **déjà** un : une save vivante qu'aucun record ne nomme est copiée dans le vault **sans** record — c'est-à-dire qu'il fabrique lui-même les orphelins invisibles du §2.2 | [MESURÉ] |
| **fermeture de partie** | fait les TROIS : crée le record de la save vivante, copie dans le vault, et écrit le record de la copie. Sans qu'on ait ouvert aucune page. La save est attribuée à la **version qui a lancé**, pas au jeu. `LastLibrarySaveScanUtc` n'est pas touché : ce n'est pas un balayage | [MESURÉ] |
| **ouvrir la page** | peut créer des copies pour les groupes qui n'en ont aucune — et dans ce cas **sans écrire de record** | [MESURÉ], voir §5 |
| **Restore Backup** | archive d'abord la save courante (« *the current active save will be moved into backup history* »), demande un **slot cible**, écrit sous le basename de la ROM, et **libelle** les deux lignes | [MESURÉ] |
| **Delete Save** | supprime le **fichier** et le record. *« This will permanently delete the save file. This cannot be undone. »* | [MESURÉ] |
| **Make New Save** | crée un **groupe** neuf : nom demandé, `SaveGroupId` neuf, **lignée neuve**, et une graine marquée `-NewSave-<guid>` dans `OriginalFileName` | [MESURÉ] |
| **Clear All and Re-scan** | efface **tous** les records puis reconstruit en balayant les émulateurs **et le vault**. Ne touche à aucun fichier (vérifié : 20 fichiers, 0 modifié, 0 supprimé) et ne change aucun réglage — pas même `LastLibrarySaveScanUtc`. Mais il détruit la structure : voir §3.3 | [MESURÉ] |
| **Repair Save Metadata** | trois choses : supprime les records dont le fichier est absent ou dont le `FilePath` est vide, supprime les doublons EXACTS (même groupe, même fichier), et enregistre les saves vivantes qu'aucun record ne nommait. Il **garde** un fossile (même fichier, groupe différent) et **n'adopte pas** un fichier vault orphelin. Son message ne compte que la première catégorie — trois records ont disparu sur un run annonçant « Removed missing records: 1 » | [MESURÉ] |
| **Edit Label** | écrit le `Title` du record | [MESURÉ] |
| **Import Save Game/State File** | ne rend PAS le fichier actif et ne touche pas à la save vivante. Il **copie** le fichier (la source reste en place) dans le vault sous le basename de la ROM au prochain suffixe libre, et crée un **groupe neuf** : `SaveGroupId` neuf, lignée égale, nom par défaut, pas de `Title`, `OriginalFileName` = le vrai nom du fichier source. Aucun dialogue. C'est « Make New Save » ensemencé par un fichier choisi | [MESURÉ] |
| **Combine With Another Save** | pur Ré-ÉTIQUETAGE. Les records de la source prennent le `SaveGroupId` **et** le `MatchLineageId` de la destination ; tous les records du groupe résultant prennent le `SaveGroupName` de la **source** ; **aucun fichier n'est touché** | [MESURÉ] |
| **Edit Name** | s'appelle *Edit Names*, deux champs : le nom du groupe, et le libellé de la save active (= le `Title` du record vivant). Le nom est propagé à **tous** les records du groupe, copies comprises. `MatchLineageId` ne bouge pas. Le champ libellé pré-remplit un défaut, et le valider inchangé n'écrit rien | [MESURÉ] |

### 3.0 « Clear All and Re-scan » détruit la structure des backups

**[MESURÉ]** Sa confirmation promet : *« This will clear all LaunchBox save metadata, then re-scan
emulator and vaulted save files to rebuild it. Your save files on disk will not be deleted. »*

La promesse sur les fichiers est tenue — 20 fichiers vérifiés au md5, aucun modifié, aucun supprimé. Ce
qu'elle ne dit pas, c'est ce qu'il advient de la structure. Sur 22 records de départ :

```
Removed old records: 23     Added emulator saves: 10
Added vaulted saves: 32     Games updated: 6
```

22 records deviennent **42**, et — le point grave — **aucun des 42 ne porte de `Title`**. Chaque copie
archivée est donc réenregistrée comme une save **vivante**, dans son propre groupe, avec un `SaveGroupId`
neuf. L'historique de sauvegarde n'existe plus : là où l'utilisateur avait trois groupes avec leurs
copies, il a maintenant une trentaine de groupes indépendants pointant chacun sur un fichier du vault.

Les noms personnalisés partent avec (`ZZTEST` a disparu), ce qui est attendu — la perte de structure ne
l'est pas.

**Et une deuxième anomalie, indépendante.** L'adoption des fichiers du vault se fait par correspondance de
nom, sans garde contre un **nom de base vide**. Une *Additional Application* de type Link, dont
l'`ApplicationPath` est une URL (`https://www.example.com/`), donne un basename vide — et le joker
`"" + "*.*"` correspond alors à **tout le dossier**. Résultat mesuré : 14 des 36 records SNES, tous
pointant sur des sauvegardes de *Secret of Mana*, ont été attribués à **Lylatwars** via cette
application-là, avec `EmulatorFileName = yt-dlp (2).exe`.

> **Ce qu'on en fait.** On garde l'intention — récupérer les fichiers orphelins est utile, et c'est même
> la seule façon de rattraper ceux que son propre *Backup Now* abandonne — mais sans les deux défauts.
>
> Notre bouton efface les records **actifs**, les reconstruit en interrogeant les plugins, puis adopte les
> fichiers du vault que rien ne référence. Trois différences, toutes délibérées :
>
> 1. **un orphelin est adopté comme COPIE**, `Title` posé et `SaveGroupId` du groupe auquel il appartient,
>    pas comme une save vivante dans un groupe neuf. L'historique survit à la reconstruction ;
> 2. **aucun rapprochement sur un nom de base vide** — la garde qui manque chez eux. Elle vaut à deux
>    endroits, et on l'a appris à nos dépens : à l'adoption, **et** avant de passer une entrée à un
>    plugin. Le plugin RetroArch dérive sa recherche de `GetFileNameWithoutExtension` ; un nom vide y
>    produit le motif `*.*` et l'entrée réclame tout le dossier. Tant que le balayage ne regardait que la
>    vue de base, ces résultats étaient écartés au filtrage ; le jour où il a commencé à scanner chaque
>    version séparément, **on a reproduit le bug qu'on venait de leur signaler** ;
> 3. **l'identité du groupe est rendue** après la reconstruction : rebâtir un record lui donne un
>    `SaveGroupId` neuf, et un identifiant neuf détacherait les copies qu'on vient de préserver. On note
>    ce que chaque record effacé disait de son fichier, et on le restitue à celui qui revient sur le même
>    fichier ;
> 4. **on s'abstient quand c'est ambigu** : un fichier n'est adopté que si **exactement un** groupe du jeu
>    attend ce nom. Deux slots de savestate partagent nom et extension, donc un `-01` parmi eux n'a pas de
>    propriétaire déterminable — lui en inventer un serait la même erreur, en plus discret.

### 3.4 La rétention est PAR GROUPE

**[MESURÉ]** Plafond ramené à **1**, puis un balayage. Le test avait cette fois un **témoin positif** —
un groupe à 2 copies, que la purge est obligée de toucher si elle tourne — et un **témoin négatif** : un
jeu à 3 groupes d'une copie chacun, donc 3 copies pour le jeu mais 1 par groupe.

```
Secret of Mana, groupe 8500ba4a   2 copies  ->  1     purgé
Yoshi's Island, 3 groupes × 1     3 copies  ->  3     INTACT
```

Si la portée était le **jeu**, Yoshi (3 > 1) aurait dû tomber à une copie. Il n'a pas bougé. Si c'était
la **version**, idem — les groupes de Yoshi n'ont aucune version. La portée est donc bien le **groupe**,
et notre `Prune`, qui boucle sur les groupes, était juste depuis le début.

> **La mesure précédente ne valait rien**, et pour une raison qui mérite d'être notée : plafond à 2 sur
> une bibliothèque dont **aucun** groupe n'avait plus de 2 copies. « Rien n'est supprimé » y était donc
> compatible avec « par groupe » **et** avec « la rétention ne tourne pas ». Un test dont une issue
> n'informe pas ne vaut pas le lancement — c'est écrit au §6, et je ne l'avais pas appliqué.

**Un défaut qui ne vaut que pour UN des deux chemins.** Quand la purge est déclenchée par *Backup Now*,
elle supprime le **fichier** et **laisse le record** : le groupe garde une ligne morte jusqu'au prochain
*Repair*. Mesuré deux fois. Mais quand elle est déclenchée par la **capture de fin de partie**, elle
supprime les deux proprement — mesuré trois fois sur RodLand. C'est donc un défaut du balayage, pas de la
rétention elle-même.

### 3.4bis Elle évince par la DATE DE CRÉATION du fichier

**[MESURÉ]** Dix copies pour un plafond de dix, créées proprement et espacées d'une heure, si bien que le
nom, l'ordre des records et la date de création se suivent tous les trois. Deux anomalies isolées y sont
plantées, chacune sur **un seul** axe et sur un fichier différent :

```
-05   date de CREATION vieillie      mtime normal pour sa position
-07   MTIME vieilli                  date de creation normale pour sa position
```

Une onzième sauvegarde force une éviction. **`-05` est mort.**

| candidat | verdict |
|---|---|
| **date de création** | **c'est lui** — le fichier vieilli sur cet axe est parti |
| date de modification | écarté — `-07` a survécu |
| nom du fichier | écarté — `-01` a survécu |
| ordre des records | écarté — `-01` était aussi le premier record |

Les trois hypothèses concurrentes sont donc tombées dans la même mesure, ce qu'aucune des précédentes
n'avait réussi.

> **Trois conclusions fausses avant celle-là, toutes dues au même défaut d'instrumentation.** J'ai
> successivement écrit que le critère était le mtime, puis l'ordre des records, puis le nom du fichier.
>
> La cause : mes copies plantées étaient antidatées avec `os.utime()`, qui déplace la date de
> **modification** et laisse intacte la date de **création** Windows. Tous mes tests « date » portaient
> donc sur un axe que LaunchBox ne regarde pas. Et comme mes scripts écrivaient plusieurs fichiers dans la
> même milliseconde, leurs dates de création étaient **à égalité** — le départage de cette égalité, arbitraire,
> se lisait tour à tour comme « le nom décide » puis « le record décide ».
>
> Le remède n'était pas de mesurer plus, mais de mesurer **le bon axe** : forcer la date de création
> demande `SetFileTime`, pas `utime`. Une fois les trois axes réellement séparables, une seule mesure a
> suffi.

**Sur un dossier, c'est la date du RÉPERTOIRE.** **[MESURÉ]** Trois copies d'une même save Wii, chacune
décalée sur **un seul** axe : `-01` sur la date de création du **répertoire**, `-02` sur celle de ses
**fichiers internes**, le témoin sur rien. Plafond à 2, quatre copies, deux mortes :

```
mortes     le temoin (tout normal)  et  -02 (fichiers decales)
survivent  -01 (repertoire decale)  et  -03
```

Seule l'hypothèse « répertoire » prédisait ce couple ; « fichiers internes » et « mtime » désignaient tous
deux `-01`. Le décalage du seul répertoire suffit donc, et c'est ce que le cadenas fait.

**La rétention ne se déclenche PAS sur un *Backup Save* manuel.** **[MESURÉ]** Quatre copies pour un
plafond de 2, un backup manuel de plus : la cinquième est créée et **rien n'est supprimé**. Toutes nos
évictions venaient jusque-là d'une fermeture de partie ou d'un *Backup Now*.

Ça colle au nom du réglage — `Max**Auto**BackupsPerGame` — et notre `Prune` fait pareil sans qu'on l'ait
voulu : il n'est appelé que depuis la capture de fin de partie et le balayage, jamais depuis le bouton.

**Conséquence pratique, et elle ferme une piste.** Le nom n'entre pas dans le choix, donc **on ne peut pas
protéger une copie en la numérotant haut**. L'idée d'une plage réservée — `-1001` et au-delà pour les
copies verrouillées, hors d'atteinte de la purge des deux côtés — a été testée puis mesurée morte : le
`-1001` planté a été évincé comme les autres. Le seul porteur possible d'un verrou reste donc le chemin.

**Notre `Prune`** trie sur `CreatedUtc`, qui lit désormais la date de **création** du fichier et non son
mtime. Les deux coïncident pour une copie ordinaire — elle est écrite une fois — et divergent dès que
quelque chose touche au contenu.

### 3.4ter Le cadenas : le critère devient le verrou

**Ajout LiteBox.** Une copie **verrouillée** est une copie dont la date de création a été avancée d'un
siècle.

Ça ressemble à une astuce jusqu'à ce qu'on relise le §3.4bis : la purge supprime par date de création, de
la plus ancienne à la plus récente. Une copie datée de 2126 est donc **toujours la dernière candidate**, et
elle n'est atteignable que si toutes les autres du groupe sont verrouillées aussi.

Ce que ça a de mieux que tous les porteurs qu'on a essayés :

- **ça survit à une réécriture**, contrairement à un champ — rien d'inconnu ne vit à travers la
  resérialisation d'un `<GameSave>` (§1.5ter) ;
- **ça ne demande aucun sous-dossier**, donc le vault garde la disposition à plat de LaunchBox ;
- et surtout **ça protège de la purge de LaunchBox lui-même**, puisqu'il trie sur cette date-là. Ni le
  sous-dossier ni le numéro n'y arrivaient : le sous-dossier échappe à son balayage mais pas à sa purge,
  et le numéro n'entre nulle part.

Deux conséquences à assumer :

**La date est visible.** LaunchBox affichera l'année 2126 sur une copie verrouillée. LiteBox retranche le
décalage à l'affichage (`VaultEntry.DisplayCreatedUtc`), lui n'en sait rien.

**Le plafond cède devant le cadenas.** Quand tenir le plafond exigerait de supprimer une copie
verrouillée, `Prune` s'abstient et le dit dans le journal. Garder ce que l'utilisateur a explicitement
protégé est tout l'objet du mécanisme, et un vault avec un fichier de trop vaut mieux qu'un cadenas rompu
en silence.

**[MESURÉ] Il tient aussi sur les saves-dossier, et contre LEUR purge.** Une copie Wii verrouillée a
survécu à un *Backup Now* qui a évincé deux de ses voisines, dont une dont les fichiers internes portaient
le même décalage — preuve que c'est bien la date du **répertoire** qui compte et que le cadenas vise le bon
axe. C'est la seule protection qu'on ait trouvée qui vaille des deux côtés.

Interface : *Backup History* → menu d'une ligne → **Lock (never delete)**, et une pastille `🔒 Locked`.
L'empreinte affichée sur la ligne a perdu son icône de cadenas au passage, qui voulait désormais dire
autre chose.

### 3.7 Une partie sur deux n'est JAMAIS sauvegardée

**[MESURÉ], huit sessions sur deux jeux.** C'est le défaut le plus coûteux qu'on ait trouvé, et le plus
discret : il ne produit aucun message, aucune trace, et laisse l'utilisateur croire que ses parties sont
protégées.

Le protocole tient en une phrase : lancer le jeu, faire une savestate, quitter, recommencer. Rien de
planté, rien de modifié — que du jeu normal.

```
session 1   save au lancement  (aucune)   ≠ dernier backup   ->  BACKUP  F4FB91F0
session 2   save au lancement  F4FB91F0   = dernier backup   ->  ignorée   (65CAF55B perdu)
session 3   save au lancement  7F1501B1   ≠ dernier backup   ->  BACKUP  A2C25921
session 4   save au lancement  A2C25921   = dernier backup   ->  ignorée   (702EB2C6 perdu)
session 5   save au lancement  702EB2C6   ≠ dernier backup   ->  BACKUP  EADC2CBD
```

**LaunchBox décide en comparant la save telle qu'elle était au LANCEMENT, mais archive le contenu de
FERMETURE.** Or au lancement, la save est exactement celle qu'il vient d'archiver à la partie précédente.
La comparaison conclut donc « déjà sauvegardée » et la session est perdue.

Le modèle a été posé après quatre sessions, puis **vérifié en prédisant à l'avance** le résultat des
sessions 5 et 6 : les deux prédictions sont tombées juste. Il explique aussi les trois sessions de Road
Rash, mesurées avant qu'on le formule.

> **Trois fausses pistes**, notées parce qu'elles ont coûté des lancements et qu'elles se ressemblent :
>
> 1. « le plafond bloque les nouvelles sauvegardes » — attribué à une paire où le plafond **et** le temps
>    écoulé changeaient tous les deux. Faux : le plafond fait exactement la fenêtre glissante décrite par
>    leur documentation (§3.4bis).
> 2. « il existe un verrou de fréquence » — les durées collaient (bloqué à 90 s et 2 min, autorisé à
>    5 min), mais c'était une coïncidence avec l'alternance. Un antidatage d'un an de tous les fichiers
>    n'a rien changé au motif.
> 3. « c'est le nombre de copies » — Road Rash à 2 copies passait, RodLand à 2 copies non. Même plafond,
>    même compte, résultats opposés : le compte n'y était pour rien.
>
> Aucune de ces trois n'aurait survécu à une prédiction faite à l'avance. C'est le test qu'on aurait dû
> exiger d'elles tout de suite.

### 3.5 *Backup Now* recopie les mêmes octets à CHAQUE passage

**[MESURÉ]** Deux balayages consécutifs, à trois minutes d'intervalle, **sans qu'aucun jeu soit lancé ni
aucune save modifiée entre les deux** :

```
passage 1    Ring Rage (USA)-01.state              5411 o   16CCBC6A
             Road Rash (USA, Europe)-01.state      5411 o   0DBB6FB4
             Yoshi's Island (Japan)-01.state     824414 o   51318835

passage 2    Ring Rage (USA)-02.state              5411 o   16CCBC6A     <- octet pour octet
             Road Rash (USA, Europe)-02.state      5411 o   0DBB6FB4     <- octet pour octet
             Yoshi's Island (Japan)-02.state     824414 o   51318835     <- octet pour octet

records écrits : ZÉRO, aux deux passages
```

C'était la conséquence **déduite** au §4.2 — « sans record, il n'y a rien à comparer, donc chaque passage
recopie les mêmes octets ». Elle est maintenant **mesurée**, sur trois jeux et deux plateformes.

Et c'est pire qu'un simple gâchis : la rétention travaille sur les **records**, or ces fichiers n'en ont
aucun. **Rien ne les plafonne.** Deux balayages en trois minutes ont produit 1,6 Mo de doublons pour le
seul Yoshi's Island. Sur une bibliothèque réelle avec la sauvegarde périodique active, ça croît sans
limite.

Le nom des fichiers dit d'où ça vient : `Yoshi's Island (Japan)-NN.state` alors que le jeu s'appelle
`Super Mario World 2: Yoshi's Island`. Le balayage archive la save vivante sous le basename de la ROM
d'une **version**, et n'écrit pas le record qui permettrait de la reconnaître au passage suivant.

### 3.6 *Set as Active* — ce qu'il fait exactement

**[MESURÉ]** Sur un groupe **In Vault** (aucune save vivante), avec chaque fichier rendu identifiable au
préalable :

```
avant    VIVANT  …states\Snes9x\Island.state      E0A33B67
         vault   Island.state      (groupe d5d27709) 51318835   <- la cible
         vault   Island-01.state   (groupe b235cc78) EADA26F9

apres    VIVANT  …states\Snes9x\Island.state      51318835   <- la copie a pris la place
         vault   Island-02.state   CRÉÉ             E0A33B67   <- l'ancienne vivante, ARCHIVÉE
```

La promesse de sa confirmation — *« The current active save will be moved into backup history »* — est
donc **tenue**, y compris pour *Set as Active* et pas seulement pour *Restore Backup*.

**Les identités ne se mélangent pas** : chaque groupe garde la sienne, seul le fichier qu'il désigne
change.

```
d5d27709   In Vault  ->  groupe VIVANT (1 backup)
0a45b056   vivant    ->  In Vault, son identité suit le fichier archivé
b235cc78   inchangé
```

Deux détails qui comptent pour l'implémentation :

- il demande un **slot cible** (*« Which slot would you like to restore this save state to? »*) et propose
  **Auto** par défaut, pas le slot d'origine du backup ;
- le record vivant créé reçoit un `Title` (`Save State 0`) et un `OriginalFileName` **assaini** —
  `Yoshi_s Island.state` pour une ROM nommée `Yoshi's Island`. C'est enfin une mesure propre de
  l'assainissement du §2.1 : sur une valeur que LaunchBox écrit de zéro, pas sur une réécriture.

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

### Le tableau des écarts

| # | écart | pourquoi |
|---|---|---|
| 1 | **on écrit toujours le record de la copie** | correction du défaut de *Backup Now* (§5.2). Détaillé plus bas. |
| 2 | **le compte affiché ne compte que les copies** | leur nombre ajoute la save vivante quand le groupe est attribué à une version, si bien qu'une save jamais sauvegardée affiche « 1 Backup ». On l'a reproduit un temps, à tort : voir ci-dessous. |
| 3 | **on ne reproduit PAS le saut d'une partie sur deux** | §3.7. C'est le défaut le plus coûteux qu'on ait mesuré, et il n'y a aucune raison de parité qui tienne face à une perte de données. Notre capture compare l'état de **fermeture**, celui qu'elle archive. |
| 4 | **notre *Clear and re-scan* préserve la structure** | §3.0. |
| 5 | **option « ne jamais écraser le plus ancien backup »** | LiteBox seul, décrite plus bas. |
| 6 | **on capture plus tôt à la fermeture** | doit passer avant la purge du cache d'extraction. |
| 7 | **réglages de périodicité et d'inactivité** | LaunchBox n'en expose aucun. |

### 4.2bis Le compte : pourquoi on a cessé de reproduire le leur

**On l'a d'abord répliqué**, sur l'argument que la parité vaut mieux que l'exactitude pour un nombre que
l'utilisateur compare d'un frontend à l'autre. L'argument ne tient pas, pour deux raisons.

**Ce nombre n'est persisté nulle part.** Aucun champ, aucun `[DataTableExport]` : il est calculé au
moment du rendu, dans notre code, dans notre fenêtre. Le changer ne modifie rien que LaunchBox puisse
lire. **La parité est due à ce qu'on ÉCRIT, pas à ce qu'on DESSINE** — et j'avais transporté l'argument
de l'horodatage (§1.7), où il est valable parce que la valeur part sur le disque, à un cas où il ne l'est
pas.

**Et on avait copié leur nombre sans copier leur liste.** Leur Backup History affiche la save vivante
comme une ligne dans le cas « version » ; la nôtre ne l'a jamais fait. Résultat : la carte annonçait N+1,
le panneau derrière listait N, et son propre en-tête affichait N. **Trois nombres pour une seule chose** —
pire que l'un ou l'autre choix tenu jusqu'au bout.

Désormais une seule règle, appliquée à la carte, à l'en-tête et aux lignes : le nombre de copies
récupérables. Quelqu'un qui utilise les deux verra LaunchBox dire « 1 Backup » là où on dit « 0 », et
ouvrir l'historique le lui expliquera immédiatement.

### 4.2ter L'option « ne jamais écraser le plus ancien backup »

**LiteBox seul**, `[Saves] ProtectOldestBackup`, désactivée par défaut. Elle sort la **première** copie de
chaque groupe de la rotation, définitivement.

Elle existe parce que rien dans le format de LaunchBox ne distingue une copie demandée d'une copie
automatique — même dossier, même nommage, aucun marqueur — et que la mesure du §1.5ter a fermé la
dernière porte : **rien d'inconnu ne survit dans un `<GameSave>`**, donc on ne peut pas y inscrire
l'origine d'une copie. Le seul porteur possible serait le chemin, c'est-à-dire des sous-dossiers, ce qui
est un écart de disposition autrement plus lourd.

Protéger « la plus ancienne » n'est pas protéger « les manuelles », et l'aide du réglage le dit. C'est
l'approximation utile : la copie qu'on regrette est presque toujours la première — la save propre, l'état
d'avant que ça tourne mal.

La copie protégée **compte toujours dans le plafond**, pour que le nombre de fichiers reste celui que le
réglage annonce : avec un plafond de 3, une permanente et deux tournantes. Avec un plafond de 1, plus
rien ne tourne — c'est dit dans l'aide.

Sur le disque, c'est un backup ordinaire qu'on choisit simplement de ne jamais supprimer. LaunchBox le lit
normalement — et le supprimerait à son tour si c'était lui qui passait la purge.

### 4.2quater Ce qui a été ALIGNÉ après mesure

Ces points-là n'étaient pas des écarts assumés mais des erreurs, trouvées en mesurant :

| ce qui était faux chez nous | corrigé en |
|---|---|
| `Prune` triait par **mtime du fichier** | ordre des **records**, le critère mesuré (§3.4bis) |
| *Set as Active* grisé sur un groupe In Vault, et plantait si activé | `SaveVault.SelfEntry` fournit le fichier propre du groupe |
| *Set as Active* n'archivait pas la save déplacée quand elle appartenait à un **autre** groupe | l'appelant, qui tient le scan, l'archive sous **son** identité |
| aucun slot demandé à la restauration | dialogue « Pick a slot », **Auto** par défaut comme eux |
| *Make New Save* grisé sur un groupe In Vault | activé, comme eux |
| la pastille **In Vault** était calculée et jamais affichée | affichée |
| le badge « Has Backup » testait `Title` | teste le **chemin**, seul critère fiable (§1.2) |
| la confirmation de *Combine* promettait un archivage puis une suppression | elle dit qu'aucun fichier ne bouge, ce qui est le cas |
| « Also delete its N vault backup(s) » annonçait 0 sur un groupe In Vault | le texte nomme la copie du vault qui va disparaître |

**Un écart assumé, le premier : on écrit toujours le record de la copie.**

C'est une correction délibérée d'un défaut de LaunchBox, pas une négligence, et elle est mesurée. Son
*Backup Now* copie dans le vault une save qu'il découvre et **n'écrit aucun record** quand le jeu n'en
avait pas. Or un fichier vault sans record n'existe pas pour lui (§2.2) : la copie ne peut être ni listée,
ni comptée, ni restaurée. Elle est en écriture seule.

Et le contrôle de doublon compare la save vivante à la dernière copie **enregistrée** du groupe : sans
record, il n'y a rien à comparer, donc chaque passage recopie les mêmes octets. **[MESURÉ]** Ce n'est
plus une déduction : deux balayages consécutifs sans rien changer entre les deux ont produit des copies
identiques octet pour octet, sur trois jeux — et rien ne les plafonne, la rétention travaillant sur des
records qu'ils n'ont pas. Voir §3.5.

Le défaut frappe exactement le cas où une sauvegarde automatique compte le plus : **un jeu auquel on a
joué mais dont on n'a jamais ouvert la page Game Saves**.

On écrit donc le record — le même, au même format, que celui que LaunchBox écrit pour un groupe qu'il
connaît déjà. Rien ne diverge dans la forme : LaunchBox trouve simplement plus de records qu'il n'en
aurait écrit, et les lit normalement. Aucun risque d'interopérabilité.

Une précaution va avec, sinon la correction serait incomplète : avant de copier, on regarde si le fichier
qu'on s'apprête à écrire **est déjà là** — même dossier, nom que ce groupe aurait utilisé, contenu
identique au bit près — et dans ce cas on lui écrit son record au lieu d'en poser un double à côté. Sans
ça, le premier passage sur un orphelin laissé par LaunchBox en créerait une copie de plus.

**Ce qu'on ne fait pas**, et c'est une abstention volontaire : on n'**adopte** pas les fichiers orphelins
en général. Leur écrire un record les rendrait visibles, mais rien ne garantit
qu'un fichier au bon nom appartienne vraiment au groupe — et LaunchBox ne le fait pas non plus, son
*Repair* les laisse en place (§3). À rouvrir si le besoin se présente.

### 4.3 Ce qui est mis de côté

Deux fonctionnalités développées puis **retirées volontairement**, pour établir la parité sur une base
saine avant de les réintroduire. Rien n'est supprimé, tout est documenté à l'endroit où ça revient.

**La passe par entrée d'archive** est désormais **active**. Elle a été tenue à `false` toute la campagne
de parité — c'est le plus grand écart avec LaunchBox, et il devait se poser sur une base connue pour juste
plutôt que se mêler au travail qui la rendait telle. Cette base existe.

### 4.5 Les saves des ROMs extraites d'une archive

**Le problème.** Une archive contient N ROMs. L'émulateur nomme la save d'après la ROM **extraite** ; le
vault, lui, nomme d'après la ROM du jeu, c'est-à-dire l'**archive**. Les N entrées se retrouvent donc à
partager un seul nom, et une restauration réécrit sous le nom de l'archive — que l'émulateur ne relira
jamais.

**La disposition.** Un sous-dossier de vault par archive, et des copies nommées d'après l'entrée :

```
Saves\<Plateforme>\
    Sonic Collection.zip\           <- le nom du fichier d'archive, assaini
        Sonic (USA).srm
        Sonic (USA)-01.srm
        Sonic (Japan).srm
        litebox-archive.xml
```

Bénéfice qu'on n'avait pas cherché : le *Clear and re-scan* de LaunchBox adopte les fichiers du vault en
énumérant le dossier de plateforme **à plat** (§3.0). Il n'entre pas dans un sous-dossier, donc les saves
d'entrées échappent au bouton qui transforme trois groupes en trente.

**L'identité** reste le `SaveGroupId` de forme `entry:<signature>:<chemin dans l'archive>`, et rien ne
justifiait d'inventer autre chose : on a mesuré que LaunchBox transporte ce champ sans jamais l'interpréter
(§1.5ter, §2.4), et trois de ses propres plugins s'en servent déjà comme espace de noms. La signature est
celle du **contenu** de l'archive, donc l'identité survit à son renommage.

**Le manifeste `litebox-archive.xml`** dit ce qu'est le dossier, de quelle archive il vient et quelle
entrée correspond à quelle copie. Il est **descriptif et jamais relu** — c'est délibéré : un second magasin
décrivant les mêmes fichiers se désynchronise, ce qui a coûté la suppression de `saves-vault.json`. Tout
ce qu'il contient est déjà porté par les records et les noms de fichiers ; il existe pour que le dossier
s'explique à un humain, après une désinstallation ou dans une sauvegarde. Même rôle que le
`manifest.sha256` de LaunchBox, et même raison de le tenir hors de toute empreinte et de toute taille.

**La restauration**, qui était le seul bouton réellement cassé, l'est encore le temps d'une mesure : le
correctif est en place mais n'a pas été observé. `AddSaveArgs` ne transporte aucun `IGame`, donc le plugin
résout le jeu lui-même et reconstruit la destination depuis son `ApplicationPath` — l'archive. Le temps de
l'appel, `HostDataManagerXml` répond désormais pour cet identifiant avec un `EntryGame` portant le chemin
de l'entrée. Portée à un fil et à un identifiant, posée juste avant l'appel et retirée dans un `finally`.

**Le nom par défaut d'un groupe** nomme la ROM extraite : `Sonic (USA) — My Save File`. Une save
ordinaire garde le nom de LaunchBox à l'identique. Le préfixe n'est pas décoratif — LiteBox a un sélecteur
d'entrées, LaunchBox n'en a aucun, donc toutes les entrées d'une archive y tombent dans une seule liste et
s'y appelleraient toutes « My Save File ». Mettre la ROM en tête les groupe aussi, LaunchBox triant les
Save Files par nom.

Le suffixe est conservé parce que le nom réapparaît dans le titre de *Backup History* et dans la liste de
*Combine*, où la section ne dit plus de quoi il s'agit. C'est un **défaut** : dès qu'un groupe est renommé,
le record porte le nom de l'utilisateur. Il se dérive du `SaveGroupId`, donc les quatre endroits qui
produisaient un nom sont servis par une seule fonction.

**Trois frontières entre entrées** ont été fermées : *Combine* ne propose plus que des groupes de la même
entrée — deux entrées sont deux ROMs différentes —, la pré-sauvegarde de *Set as Active* ne réveille plus
les voisines, et *Import* demande **à quelle ROM** la save appartient. Son défaut prend la ROM dont le
basename correspond à celui du fichier importé, ce qui n'est pas une devinette mais la règle de nommage
prise à l'envers ; à défaut, l'entrée qu'on est en train de parcourir.

### 4.6 Ce que le harnais couvre, et ce qu'il ne couvre pas

`--selftest-entry-saves` — **54 vérifications**, sur un arbre jetable via `MediaResolver.SwapRootForTest`,
supprimé à la fin. Aucune bibliothèque réelle n'est touchée, ce qui est la condition pour qu'il tourne
sans surveillance.

Il couvre l'emplacement et le nommage des copies, l'absence de collision entre entrées, l'assainissement
des caractères interdits, la frontière du vault sur un sous-dossier, l'exclusion des **deux** manifestes
de toute empreinte et de toute taille, le cadenas sur un fichier **et** sur un dossier — pose, relecture,
idempotence, retrait, date affichée —, la rétention par date de création avec l'ordre des records
délibérément **inversé** pour qu'il ne puisse pas expliquer le résultat, la survie d'une copie verrouillée,
le plafond qui cède quand tout est verrouillé, le manifeste, et l'aller-retour de l'identité d'entrée.

**Il s'arrête où commencent les plugins.** Scanner et restaurer passent par un plugin et une install
d'émulateur, qu'un test ne peut pas honnêtement simuler. Ce qui reste donc à mesurer sur une vraie
install : qu'une partie lancée depuis une entrée produise bien une copie dans le bon dossier, et surtout
qu'une **restauration** réécrive sous le nom de l'entrée et non sous celui de l'archive.

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

### 5.0 À MESURER SUR UNE VRAIE INSTALL — le reliquat du lot « ROMs extraites »

Tout ce qui suit est **écrit, compilé, déployé et couvert par `--selftest-entry-saves`** (59
vérifications). Rien n'y est observé face à un vrai plugin, et le harnais s'arrête là par construction :
scanner et restaurer passent par une install d'émulateur qu'un test ne peut pas honnêtement simuler.

**Il faut un jeu dont la ROM est une archive à plusieurs entrées.** Protocole habituel : une variable par
lancement, l'état d'avant relevé, et la prédiction écrite AVANT la mesure.

| # | à mesurer | pourquoi ça compte |
|---|---|---|
| 1 | Une partie lancée depuis une entrée produit une copie dans le sous-dossier de l'archive, sous le nom de l'ENTRÉE | c'est toute la disposition du §4.5 |
| 2 | Une **restauration** réécrit sous le nom de l'entrée, **pas** sous celui de l'archive | le correctif le plus important du lot, et le seul dont on n'ait aucune preuve d'exécution : le mécanisme `GetGameById` → `EntryGame` est en place mais n'a jamais tourné face à un plugin |
| 3 | Le **cadenas** posé depuis l'interface LiteBox survit à une purge | il a été éprouvé côté LaunchBox (§3.4ter) et dans le harnais, jamais par un clic dans LiteBox suivi d'une éviction |
| 4 | **PCSX2** produit bien un dossier, et la rétention y trie comme ailleurs | §2.3ter est déduit du contrat du plugin ; l'install n'a ni plateforme PS2 ni émulateur |

Les deux premiers se mesurent ensemble sur un même jeu. Le troisième tient en un lancement une fois qu'un
groupe a deux copies. Le quatrième demande d'installer PCSX2 et une carte mémoire portant au moins deux
jeux — sans quoi on ne verra pas ce qui distingue un conteneur d'un dossier.

### 5.1 Le reste

Par ordre d'utilité.

**La sauvegarde périodique.** `PeriodicSaveBackupEnabled` est à `true` et on n'a **jamais** vu la tâche
partir. Quand se déclenche-t-elle, à quel intervalle, sous quelles conditions ? LaunchBox n'expose aucun
réglage d'intervalle, donc la valeur est en dur quelque part. Nos deux réglages (intervalle, inactivité)
sont des ajouts assumés — mais on ne sait pas de quoi ils divergent.

**À quoi sert `SaveVaultMetadataRepairComplete`.** Ce n'est **pas** le drapeau de *Repair Save Metadata* :
il est resté à `false` à travers un run complet et réussi. Mon hypothèse de la migration one-shot liée à
ce bouton était fausse.

**Pourquoi l'ouverture d'une page crée parfois des copies sans record.** **[MESURÉ]** Un contre-exemple propre existe désormais : sur un jeu à trois groupes, ouvrir la page puis annuler n'a créé aucun fichier ni aucun record. La condition n'est donc pas « ouvrir une page », elle est plus étroite. Observé une fois, sur trois
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
