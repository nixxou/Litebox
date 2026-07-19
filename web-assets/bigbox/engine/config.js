/* ============================================================================
   BigBoxWeb — CONFIGURATION CENTRALE
   ----------------------------------------------------------------------------
   Toutes les options réglables du thème sont ici, en un seul endroit.

   Deux niveaux :
     • global : valeurs par défaut, valables pour tout le thème.
     • pages  : surcharges par écran (categories / games / details / system).
                N'importe quelle clé de `global` peut être redéfinie pour une
                page en reproduisant le même chemin sous pages.<écran>.
                Exemple : pages.games.contentTransition.durationMs = 400.

   Qui lit quoi :
     • Le moteur (engine/app.js) lit ces valeurs au runtime.
     • Certaines valeurs pilotent des ANIMATIONS CSS : elles sont recopiées en
       variables CSS sur :root par applyConfigCss() (appelée au démarrage).
       → si tu changes ces valeurs à chaud, rappelle window.BBW.applyConfigCss().

   Réglage à chaud (console) : window.BBW.config.global.<...> = ...
   Réglage par URL (tests)   : ?dur=280  ?dwell=160  ?anim=0   (transition contenu)

   Ce fichier doit être chargé AVANT engine/app.js.
   ========================================================================== */
(function () {
  "use strict";
  window.BBW = window.BBW || {};

  window.BBW.config = {

    /* ─────────────────────────────── GLOBAL ─────────────────────────────── */
    global: {

      /* DISPOSITION / appareil — choisit l'agencement ET le mode d'entrée :
           "desktop" : agencement classique + souris (survol / clic).
           "tablet"  : agencement classique MAIS menu en ROUE tactile (glisser / snap /
                       swipe) ; souris coupée. Le contenu (détail / aperçu / récents)
                       reste affiché à droite — c'est l'affichage classique.
           "phone"   : agencement compact en COLONNE + roue tactile (le contenu se replie).
         mode "auto" (défaut) : largeur < phoneMaxWidthPx → phone ; sinon écran tactile
         → tablet ; sinon desktop. Augmente phoneMaxWidthPx pour garder les grands
         téléphones (en paysage) en mode phone plutôt que tablet.
         Test (les écrans headless n'ont pas de tactile) : ?mode=tablet | desktop | phone */
      layout: {
        mode:            "auto",
        phoneMaxWidthPx: 820,
        // Tente le plein écran (cache la barre d'URL) au 1er geste tactile.
        // Android Chrome/Edge : OK. iOS iPhone : non supporté par l'API → installer
        // via « Sur l'écran d'accueil » (manifest display:fullscreen). false = ne pas tenter.
        fullscreenOnTap: true
      },

      /* Transition de CONTENU : glissement PAR ZONE (détail / aperçu / recent)
         lors d'un changement de sélection sur une page catégories/plateforme/genre.
         (La surbrillance, elle, bouge toujours instantanément — voir highlight.) */
      contentTransition: {
        enabled:    true,   // false = bascule instantanée du contenu (aucun glissement)
        durationMs: 280,    // durée du glissement de chaque zone
        dwellMs:    160     // délai APRÈS le déplacement de la surbrillance avant de
                            // lancer le glissement du contenu. Anti-clignotement en
                            // défilement rapide (on ne transite qu'une fois "posé").
                            // 0 = immédiat.
      },

      /* Transition d'ÉCRAN : passage entre catégories ↔ jeux ↔ détail ↔ system.
         Le TYPE se règle surtout par page (pages.<écran>.screenTransition.enter) ;
         ici c'est le défaut + les paramètres communs. */
      screenTransition: {
        enter:      "fade", // "fade" | "slide-v"  (type d'entrée par défaut)
        durationMs: 320,    // durée (pilote aussi le CSS via --bbw-screen-dur)
        slidePx:    45      // amplitude du slide vertical (pour enter = "slide-v")
      },

      /* Surbrillance de sélection : le bloc qui suit l'élément actif. */
      highlight: {
        slideMs: 100,  // durée du glissement de la surbrillance d'un item à l'autre
                       // (CSS via --bbw-hl-slide). Coupé pendant un appui maintenu.
        flash: {       // « flash » plein-cadre bref au changement (haut/bas)
          enabled:    true,
          peak:       0.22, // opacité max du voile blanc (CSS --bbw-hl-flash-peak)
          durationMs: 450   // durée du flash (CSS --bbw-hl-flash-dur)
        },
        shine: {       // « reflet » qui balaie la surbrillance quand elle reste idle
          enabled:  true,
          delayMs:  2000,  // 1er reflet : après ce délai sans navigation
          periodMs: 10000  // répétition (CSS --bbw-hl-shine-period)
        }
      },

      /* Listes : défilement, recalage de la roue tactile, et ESPACEMENT des éléments. */
      lists: {
        desktopScrollMs: 180, // défilement interne (garde l'item sélectionné visible) — CSS
        wheelSnapMs:     260, // snap de la roue mobile au relâchement — CSS

        /* Espacement vertical des éléments de menu (listes catégories / plateformes /
           genres et jeux, en desktop ; piloté côté CSS via --bbw-box-h / --bbw-item-h
           / --bbw-item-gap).
           Par défaut itemHeightPx == boxHeightPx → tous les éléments occupent la
           hauteur de la box de sélection : liste régulière, comme à l'origine.
           Réduire itemHeightPx (et/ou gapPx) RESSERRE les éléments NON sélectionnés ;
           l'élément SÉLECTIONNÉ garde toujours boxHeightPx, donc lui seul conserve
           l'espace nécessaire à la box de sélection.
           (n'affecte ni le menu détail / system, ni la roue tactile mobile.) */
        boxHeightPx:     44, // hauteur de la box = hauteur de l'élément sélectionné (garder = hauteur visuelle de la surbrillance)
        itemHeightPx:    44, // hauteur des éléments NON sélectionnés (≤ boxHeightPx pour resserrer la liste)
        gapPx:           11, // espace vertical entre deux éléments
        itemFontSizePx:  15, // taille de police des labels (roue catégories / plateformes / playlists / jeux + menus détail / system)

        /* Mode COMPACT auto : quand le nb d'items affichés dans une roue (catégories,
           jeux) ou un sous-menu détail dépasse `compactThresholdN`, on bascule sur
           des valeurs plus serrées (boxHeight/itemHeight/gap/fontSize compactes). Le
           moteur pose .compact-wheel sur l'élément .screen ; la CSS remappe les
           variables wheel/list vers leurs équivalents compactes (cf. styles.css). */
        compactEnabled:       false,
        compactThresholdN:    20,
        compactBoxHeightPx:   28,
        compactItemHeightPx:  18,
        compactGapPx:         9,
        compactFontSizePx:    13,

        /* TURBO : un maintien prolongé de Haut/Bas (clavier OU manette) multiplie le
           pas de défilement par turboMultiplierPct/100 après turboDelayMs ms, pour
           parcourir plus vite les longues roues (10 k+ entrées). Le compteur reset au
           relâchement de la touche ou au changement de direction. Désactivé si
           turboEnabled=false, turboDelayMs<0, ou turboMultiplierPct≤100. Appliqué aux
           roues principales (liste catégories/jeux/détails, rail plateformes, rail
           ROM de la fiche). */
        turboEnabled:        true,
        turboDelayMs:        3000,
        turboMultiplierPct:  300,

        /* PAGE STEP (PgUp / PgDn clavier · L2 / R2 manette) : nombre d'items
           sautés par pression. La gâchette/touche est accélérée dès le 1er appui
           (pas d'attente turboDelayMs) : ×1 immédiatement, puis ×2 après 500 ms,
           puis ×4 après 1500 ms de maintien continu. */
        pageStepN:           5,

        /* SPLITTER : largeur de la roue (CSS --bbw-list-w), drag-redimensionnable
           depuis la frontière roue↔contenu en mode desktop. resizable=false coupe
           le splitter ET force la largeur par défaut (250 px) — widthPx reste
           préservé dans le cookie pour être restauré à la réactivation. Désactivé
           en mode tablet/phone (la roue est tactile). */
        resizable:    false,
        widthPx:      250,
        minWidthPx:   180,
        maxWidthPx:   480,

        /* SCROLLBAR de la ROUE : barre verticale draggable au bord droit de la
           roue (desktop). Affichée AUTOMATIQUEMENT dès que le nombre d'items
           dépasse la hauteur visible (overflow). Drag du thumb = setSelected
           immédiat à l'index correspondant (utile sur les longues roues avec
           10 k+ entrées). Hover éclaircit le thumb ; pendant le drag il revient
           à la couleur normale (matche LB). Désactivée en tablet/phone. Mettre
           à false pour la cacher totalement (la roue se navigue toujours au
           clavier/manette).
           NB : les AUTRES surfaces scrollables (poster grid, related list,
           VNDB, advanced search, config modal) utilisent la scrollbar NATIVE
           du navigateur (styled dans styles.css) — visible automatiquement
           dès overflow, non gated par ce flag. */
        scrollbarEnabled: true,

        /* SCROLLBAR — cap maximum de la hauteur du thumb, en % du track
           visible (0..1). Évite le "thumb géant" quand l'overflow est
           faible (sur 1.5 page de contenu, sans cap le thumb ferait
           ~90% du track et l'indicateur de position devient illisible).
           Default 0.056 = 5.6 % du track (~34 px sur les 600 px du wheel,
           ~34 px sur la hauteur visible du poster grid). */
        scrollbarMaxThumbPct: 0.056
      },

      /* SOURIS (desktop). mouse.enabled=false coupe le survol auto, la molette,
         le clic droit et le masquage du curseur (les CLICS sur éléments restent
         actifs ; navigation clavier/manette inchangée). */
      mouse: {
        enabled:              true,
        hoverMoveThresholdPx: 6,    // mouvement réel requis avant que le survol agisse
                                    // (évite l'auto-survol de l'élément sous le curseur
                                    //  quand un écran apparaît dessous).
        cursorAutoHideMs:     2000, // curseur masqué après ce temps d'immobilité (0 = pas de masquage auto)
        rightClickBack:       true, // clic droit = retour
        clickAfterWheelMs:    2000, // clic gauche sur zone vide ≤ ce délai après une molette = select
        wheel: {
          enabled: true,
          stepPx:  40           // molette : 1 cran de sélection par stepPx accumulés
        }
      },

      /* TACTILE (mobile) : swipe horizontal franc = retour (→ droite) / select (← gauche).
         Seuils volontairement « francs » pour éviter les faux positifs en défilement. */
      swipe: {
        thresholdPx:       70,  // distance mini d'un swipe horizontal pour agir
        dominance:         1.5, // |dx| doit dépasser |dy| × dominance (vraiment horizontal)
        axisLockPx:        10,  // mouvement avant de verrouiller l'axe (roue verticale vs swipe)
        axisLockDominance: 1.3  // verrou en "x" seulement si |dx| > |dy| × ce facteur
      },

      /* Défilement AUTO des blocs de description (catégories / jeux) quand le texte
         dépasse sa hauteur d'affichage max. Cycle : attente → descente LENTE → pause
         en bas → remontée (plus RAPIDE) → on reboucle. Les hauteurs max (choisies pour
         ne pas empiéter sur la vidéo en dessous) sont poussées en CSS
         (--bbw-desc-max-game / --bbw-desc-max-cat). N'agit qu'en desktop/tablette. */
      descScroll: {
        enabled:         true,
        startDelayMs:    8000, // attente avant de défiler (et avant de reboucler depuis le haut)
        speedDownPxS:    18,   // vitesse de descente (px/s, lente)
        bottomPauseMs:   2000, // attente une fois arrivé en bas
        speedUpPxS:      48,   // vitesse de remontée (px/s, plus rapide que la descente)
        maxHeightGamePx: 140,  // hauteur d'affichage max de la description JEU (reste au-dessus de la vidéo)
        maxHeightCatPx:  175   // hauteur d'affichage max de la description CATÉGORIE / PLATEFORME
      },

      /* Fenêtre "Related Games" : hauteur des box de description des cartes, en % de la
         hauteur de la scène (720 px design). Plus grand = plus de texte par carte (mais
         moins de cartes visibles) ; plus petit = cartes plus compactes. CSS via
         --bbw-rel-desc-h. (5 % ≈ 36 px ≈ 2 lignes.) */
      related: {
        descHeightPct: 5,
        // Nombre maximal de jeux retournés PAR ONGLET (Jeux recommandés /
        // Jeux similaires / Ports possibles). Passé au backend via le
        // query param ?limit=N de /data/games/{id}/related.json — la
        // pré-filtration côté serveur (scoring + sort + cap) tient la
        // taille de la réponse linéaire, donc augmenter ne coûte que
        // l'enrichissement Overview (fetch séparé / lazy). Clamp serveur
        // [1, 200].
        perTab: 50
      },

      /* IMAGES / MÉDIAS — chargement à deux niveaux pour rester réactif (mode plugin) :
           1. Vignette DÉGRADÉE (JPG réduit, générée + cachée côté plugin, immutable) :
              affichée immédiatement à la sélection ; une FENÊTRE est préchargée autour
              de la sélection (jamais annulée — elles sont petites, on les garde).
           2. Version COMPLÈTE + VIDÉO : chargées PAR-DESSUS la dégradée seulement après
              `heavyDelayMs` d'immobilité (le « palier »), avec un cache court côté
              navigateur. Annulées si on bouge avant la fin.
         (En standalone/dummy, sans `thumb`, on retombe sur l'image inline — pas de régression.) */
      media: {
        heavyDelayMs:   300,  // palier d'immobilité avant de charger COMPLÈTE + vidéo
        prefetchAhead:  15,   // vignettes dégradées préchargées DANS le sens du défilement
        prefetchBehind: 5,    // …et à CONTRE-sens (fenêtre ≈ 20, persistante)
        // fillScreenshot : quand une VIDÉO joue (au palier) et qu'il y a la place
        // (desktop / tablette paysage — jamais en portrait), élargir la zone média
        // dans l'espace libre : vidéo à GAUCHE + grille de screenshots à DROITE
        // (≈ 2 colonnes, nombre d'images calculé selon la place ; uniquement les
        // screenshots disponibles). false = comportement classique (vidéo seule).
        fillScreenshot: true,
        // Apparition de la grille : la case vidéo (à taille CONSTANTE) GLISSE de sa
        // position d'origine (droite, là où était le screenshot) vers la GAUCHE, et les
        // screenshots entrent depuis le BORD DROIT, sur cette durée (ms). 0 = instantané.
        fillTransitionMs: 1500,
        // Vignettes screenshot : blocs de taille FIXE (px "design", base 1280×720).
        // 16:9 par défaut. Le moteur calcule le nb de COLONNES et de LIGNES selon la
        // place dispo (≥ 1 colonne ; autant de lignes qu'il en rentre). L'image garde
        // son propre ratio (object-fit:contain) à l'intérieur du bloc.
        fillShotWidthPx:  150,  // largeur d'un bloc
        fillShotHeightPx: 84,   // hauteur d'un bloc (≈ 150 × 9/16)
        fillShotGapPx:    6     // espace entre blocs
      },

      /* MANETTE (Gamepad API / XInput) — même navigation que le clavier.
         Mapping "standard" (Xbox/XInput) : A=select, B=retour, Start=menu système,
         croix directionnelle + stick gauche = directions. */
      gamepad: {
        enabled:          true,
        deadzone:         0.5,   // seuil du stick gauche (au-delà = direction)
        triggerThreshold: 0.4,   // seuil des gâchettes L2/R2 (0..1) → pgup / pgdn
        repeatDelayMs:    400,   // délai avant la 1re auto-répétition (direction maintenue)
        // Auto-répétition ACCÉLÉRÉE : l'intervalle se réduit de repeatRateMs (départ
        // contrôlé) vers repeatRateMinMs (vitesse max) au fil du maintien, sur accelMs.
        // → maintien long = défilement aussi rapide qu'une touche clavier enfoncée.
        repeatRateMs:    120,   // intervalle de répétition INITIAL
        repeatRateMinMs: 30,    // intervalle MINIMAL après accélération (≈ clavier maintenu)
        accelMs:         1000   // durée pour passer de repeatRateMs à repeatRateMinMs (0 = pas d'accélération)
      },

      /* CONTRÔLE PARENTAL — surveillance UNIQUEMENT en mode BigBox.
         En mode BigBox le verrou est piloté de l'EXTÉRIEUR (lock natif de BigBox sur la
         box) : la page ne peut pas le savoir autrement, donc elle RE-VÉRIFIE l'état du
         serveur périodiquement et RECHARGE dès qu'il change — indispensable au
         re-verrouillage (une page laissée déverrouillée doit repasser en filtré sans
         attendre une navigation). FAIL-CLOSED : déverrouillé + serveur injoignable → on
         recharge après failClosedAttempts échecs (pas de contenu déverrouillé non validé).
         En mode LaunchBox le verrou est par-client (cookie/PIN) et ne change que via les
         actions de la page elle-même (qui rechargent déjà) → ces réglages ne s'appliquent pas. */
      parental: {
        pollMs:             4000, // intervalle de re-vérification de l'état (ms). Plus court = re-verrouillage plus rapide.
        failClosedAttempts: 2     // échecs consécutifs (en étant déverrouillé) avant rechargement forcé. 0 = jamais.
      },

      /* DEBUG — indicateurs visibles dans la topbar du kiosk + logs console.
         Off par défaut : utile pour diagnostiquer les états autoplay / focus
         (ex. autoplay-policy bloqué après le synthetic-gesture CDP de l'hôte).
         Activable depuis l'onglet General de l'UI Réglages. */
      debug: {
        kioskIndicators: false   // pills [Focus] [Son] avant l'heure + console.log à chaque transition
      },

      /* RECENT — rangée "jeux récents" des pages catégorie / plateforme / playlist.
         Chargée en LAZY par nœud (au dwell) + cachée côté client. Le cache est invalidé
         à la sortie d'un jeu (epoch serveur) ET expire après une durée ALÉATOIRE tirée
         entre cacheTtlMinMs et cacheTtlMaxMs (étale les rechargements ; évite que toutes
         les entrées expirent en même temps). Mets min = max pour une durée fixe. */
      recent: {
        cacheTtlMinMs: 600000,    // 10 min
        cacheTtlMaxMs: 1200000    // 20 min
      },

      /* MÉDIA de fond de la page catégorie/plateforme/playlist (zone en haut à droite,
         remplace le placeholder en gardant la transition). Chargé en LAZY par nœud.
         `order` = priorité : le moteur prend le PREMIER qui existe. Retire un élément
         pour le désactiver ; réordonne pour changer la priorité.
           platformVideo        : vidéo de la plateforme/catégorie/playlist
           platformBackground   : image de fond (Background) de la plateforme/catégorie/playlist
           randomGameVideo      : vidéo d'un jeu au hasard du nœud
           randomGameBackground : background d'un jeu au hasard du nœud
         muted = vidéo silencieuse · loop = vidéo en boucle (ambiance). */
      catMedia: {
        order: ["platformVideo", "platformBackground", "randomGameVideo", "randomGameBackground"],
        muted: true,
        loop:  true
      },

      /* SONS de navigation (set "Sci-Fi Set 3 by Clavius", convertis en mp3 dans
         web/sounds/). move = ↑/↓/←/→ · select = A/Entrée · back = B/Échap. Le son ne
         démarre qu'après le 1er geste utilisateur (politique autoplay du navigateur). */
      sounds: {
        enabled: true,
        volume:  0.35   // 0..1
      },

      /* MARQUEE des labels de menu (liste jeux + catégories/plateformes/playlists) :
         si le label de l'item SÉLECTIONNÉ dépasse la largeur du bouton, après un délai
         d'inactivité il défile vers la GAUCHE, marque une pause au bout, puis revient
         (plus vite). Relancé à chaque changement de sélection. (desktop/tablette.) */
      menuScroll: {
        enabled:       true,
        startDelayMs:  2000,   // inactivité avant de défiler (et avant de reboucler)
        speedLeftPxS:  30,     // vitesse aller (vers la gauche), px/s
        endPauseMs:    2000,   // pause une fois au bout
        speedRightPxS: 60      // vitesse retour (plus rapide), px/s
      },

      /* Titre de jeu vs nom de plateforme (page jeux / page détail). Le titre (gauche)
         et la plateforme (haut-droite) partagent une bande : si le titre déplié sur une
         ligne empiète sur la plateforme (à `gapPx` près), on masque la plateforme. */
      platformFit: {
        enabled: true,
        gapPx:   16            // marge mini (px mise en page) entre fin de titre et plateforme
      },

      /* Clear logo de la plateforme à la place du texte (roue + fiche jeu), quand l'image
         est disponible (games.json → platformLogoImg). enabled:false → toujours le texte.
         Opacité fixe (1 = opaque, 0 = transparent ; 0.6 ≈ atténué à ~60 %). Le logo vit
         DANS la zone .detail → il glisse avec le titre / la description (même animation de
         contenu, cf. contentTransition). */
      platformLogoImage: {
        enabled:     true,
        maxHeightPx: 28,       // hauteur max du logo (px mise en page)
        opacity:     0.6       // opacité du logo (~60 %)
      },

      /* Clear logo du JEU sélectionné dans une petite zone réservée EN HAUT DE LA ROUE de
         jeux (games.json → games[].logo). Sert la version DÉGRADÉE mise en cache côté plugin
         (WebP avec transparence, ?q=logo) et la précharge dans la MÊME fenêtre que les
         vignettes (cf. media.prefetchAhead/Behind) → affichage instantané au défilement.
         Pas de clear logo pour le jeu → on affiche son TITRE en texte. enabled:false → zone
         masquée et roue à sa position normale. (Masqué en mode phone.) */
      gameLogo: {
        enabled:     true,
        topPx:       38,       // haut de la zone logo = marge intérieure RÉDUITE (le bloc remonte sous la topbar)
        listTopPx:   158,      // haut de la roue : INCHANGÉ → le menu ne bouge pas. ↑ pour donner encore
                               //   plus de place au logo (la zone logo remplit l'espace topPx→listTopPx).
        maxHeightPx: 90,       // hauteur max du logo (agrandi)
        opacity:     0.8,      // opacité du logo (~80 %)
        glow:        true,     // ombre portée douce sous le logo (le décolle du fond sombre)
        accent:      true      // fine barre d'accent (1 px) SOUS le logo (en-tête du jeu ; clear logo seul)
      },

      /* RÉCENTS (rangée "jeux récents") — options d'affichage.
         fluid:true (défaut) → object-fit:contain, image entière visible, aspect ratio préservé.
         fluid:false → object-fit:cover, forcé au ratio poster (crop si besoin). */
      recentsView: {
        fluid: true
      },

      /* Vue POSTER (bascule de la roue de jeux) : grille de jaquettes (partie principale) + panneau
         détail simplifié à droite. Bascule via la touche Tab, le bouton View/Select de la manette,
         ou un swipe vers le bas depuis le bas-centre (tablette). Le rail gauche reste utilisable.
         enabled:false → bascule désactivée. cellWidthPx = largeur d'une jaquette (les colonnes
         s'ajustent à la place) ; cellRatio = hauteur image / largeur (jaquette portrait). */
      posterView: {
        enabled:     true,
        cellWidthPx: 105,
        cellRatio:   1.4,
        gapPx:       18,
        sideWidthPx: 345,      // largeur du panneau détail à droite
        // fluid:true (défaut) → l'image conserve son ratio natif
        //   (object-fit:contain), la case réserve un MINIMUM carré
        //   (cellWidthPx × cellWidthPx) ; la hauteur réelle d'une ligne du
        //   grid = hauteur du plus grand élément de la ligne. cellRatio est
        //   ignoré dans ce mode. Look proche du LaunchBox original.
        // fluid:false → mode "fixe" historique : .pc-img a la dimension
        //   cellWidthPx × cellRatio, l'image est croppée (object-fit:cover)
        //   pour remplir tout le cadre.
        fluid:       true,
        // Animations du HERO du panneau droit (fanart en filigrane + clear logo).
        //   heroFanartDelayMs       : attente avant de tirer le NOUVEAU fanart
        //     (debounce du fade-in — annulé+rescheduled à chaque selection).
        //   heroFanartFadeOutDelayMs: attente avant de démarrer le fade-out
        //     de l'ancien fanart (PAS debounce — schedule UNE fois sur la
        //     première deselection ; les changements ultérieurs ne le
        //     repoussent pas → permet à l'ancien de s'effacer pendant un
        //     scroll long sans rester collé à 0.28 jusqu'à l'arrêt).
        //   heroFanartFadeInMs      : durée du fade-in du nouveau fanart.
        //   heroFanartFadeOutMs     : durée du fade-out de l'ancien fanart.
        //   heroFanartOpacity       : opacité finale du fanart (0..1).
        //   heroLogoDelayMs         : délai avant le pulse du clear logo.
        //   heroLogoPulseMs         : durée du pulse (montée + descente).
        //   heroLogoPulseScale      : facteur d'agrandissement au pic.
        heroFanartDelayMs:        500,
        heroFanartFadeOutDelayMs: 500,
        heroFanartFadeInMs:       300,
        heroFanartFadeOutMs:      800,
        heroFanartOpacity:        0.28,
        heroLogoDelayMs:    500,
        heroLogoPulseMs:    1000,
        heroLogoPulseScale: 1.10,
        // Mise à l'échelle PERSISTANTE de l'image poster quand sa cellule
        //   est :hover ou .selected. Transition bidirectionnelle : la cellule
        //   grossit en entrant dans l'état, dégrossit en le quittant.
        // cellHoverScale  : facteur de scale en hover/selected (1.0 = aucun)
        // cellHoverMs     : durée de la transition transform (ms)
        cellHoverScale:  1.035,
        cellHoverMs:     250,
        // Voile coloré derrière la grille de posters : reprend la jaquette
        //   du jeu sélectionné avec un blur fort + faible opacité → donne
        //   une teinte ambiente à toute la zone grid (look LB desktop).
        // gridTintEnabled : active/désactive la teinte.
        // gridTintDelayMs : debounce avant chargement (évite charger une
        //   teinte par jeu traversé en scroll rapide).
        // gridTintFadeMs  : durée de la transition d'opacité à l'apparition.
        // gridTintOpacity : opacité du voile (0..1).
        // gridTintBlurPx  : intensité du flou (px) — fort = on ne voit que
        //   les couleurs dominantes.
        gridTintEnabled: true,
        gridTintDelayMs: 500,
        gridTintFadeMs:  1000,
        gridTintOpacity: 0.30,
        gridTintBlurPx:  70
      },

      /* FANART en fond de la partie droite (hors menu + barres haut/bas). detail.json fournit
         la liste des fanart (regroupement "Background", via GameCache) ; le client en tire UN
         AU HASARD, le charge APRÈS le reste (faible priorité, pas de préchargement) et l'affiche
         en fond très atténué. enabled:false → pas de fanart. opacity = opacité du fond (0..1). */
      fanart: {
        enabled: true,
        opacity: 0.07,         // ~7 % (fond discret)
        fadeMs:  2000          // fondu progressif de 0 → opacity à l'apparition (vitesse, ms)
      },

      /* Affiche, sous la ligne année/genre/note (roue de jeux + fiche détail), une petite ligne
         avec le NOM DU FICHIER (sans le chemin, ex. « Game (USA).iso »). Désactivé par défaut. */
      gameFilename: {
        enabled: false
      },

      /* Screenshots ENRICHIS : ajoute ?extra=1 à l'appel detail.json. Le serveur réordonne alors
         les médias d'un jeu : 1er screenshot, puis l'écran-titre (« Screenshot - Game Title »),
         puis la box front (sinon « Fanart - Box - Front »), puis le reste des screenshots
         (chacun choisi en priorité région). S'il y a moins de 6 vignettes, on complète avec des
         « Screenshot - Gameplay » des AUTRES régions. Activé par défaut. */
      extraScreenshots: {
        enabled: true
      },

      /* Horloge live des topbars (remplace le « 2:59 PM » figé). ampm:false → format 24 h. */
      clock: {
        enabled: true,
        ampm:    true          // true → « h:MM AM/PM » · false → « HH:MM »
      },

      /* Recherche (loupe du rail, écran jeux) : mini-clavier navigable aux flèches, la liste
         se filtre en direct sur le compareName. `keyboard` = afficher le clavier à l'écran,
         par mode d'affichage. `layout` = disposition : "auto" suit la langue de LaunchBox
         (Français → AZERTY, Allemand → QWERTZ, sinon QWERTY) ; ou forcer "qwerty"/"azerty"/
         "qwertz". (Les libellés d'UI sont traduits séparément, cf. engine/i18n.js.) */
      search: {
        keyboard: { desktop: true, tablet: true, phone: true },
        layout:   "auto"
      },

      /* Drapeaux de bibliothèque par jeu (état utilisateur LaunchBox, mode "Owned" seulement).
         games.json fournit fav/broken/completed/installed/portable. (Les jeux « Hide » de LB
         restent exclus côté serveur.) showBroken:false retire les jeux cassés de la liste ;
         favoriteStar:true affiche une ★ sur les favoris. completed/installed/portable sont
         ramenés mais pas encore exploités. */
      library: {
        showBroken:   true,
        favoriteStar: true,   // ★ or pulsante sur les favoris (prioritaire)
        qualityStars: true    // ★ bronze/argent/or (paliers SQLite, plateformes uniquement)
      }
    },

    /* ───────────────────────── SURCHARGES PAR PAGE ──────────────────────────
       Chaque écran peut redéfinir n'importe quelle clé de `global` (même chemin).
       Pour l'instant : seulement le type de transition d'entrée d'écran. */
    pages: {
      categories: { screenTransition: { enter: "fade" } },
      games:      { screenTransition: { enter: "slide-v" } },
      details:    { screenTransition: { enter: "slide-v" } },
      system:     { screenTransition: { enter: "fade" } }
    }
  };

  /* Recopie vers le CSS (:root) les valeurs qui pilotent des animations CSS. */
  window.BBW.applyConfigCss = function () {
    var g = window.BBW.config.global, r = document.documentElement.style;
    r.setProperty("--bbw-screen-dur",      g.screenTransition.durationMs + "ms");
    r.setProperty("--bbw-hl-slide",        g.highlight.slideMs + "ms");
    r.setProperty("--bbw-hl-flash-dur",    g.highlight.flash.durationMs + "ms");
    r.setProperty("--bbw-hl-flash-peak",   g.highlight.flash.peak);
    r.setProperty("--bbw-hl-shine-period", g.highlight.shine.periodMs + "ms");
    r.setProperty("--bbw-list-scroll",     g.lists.desktopScrollMs + "ms");
    r.setProperty("--bbw-wheel-snap",      g.lists.wheelSnapMs + "ms");
    // Largeur de la roue (clampée [min, max]). Pilote --bbw-list-w consommé
    // par .list, .list-highlight, .game-logo et les `left:` du contenu
    // (boxart-wrap, cat-detail, recent, screen-games .fanart).
    // Quand lists.resizable=false, on FORCE la largeur par défaut (250) —
    // sinon une largeur custom enregistrée resterait visible alors que
    // l'option est désactivée. La valeur lists.widthPx reste préservée
    // dans le cookie pour être restaurée à la réactivation.
    var lw;
    if (g.lists.resizable === false) {
      lw = 250;
    } else {
      lw = g.lists.widthPx != null ? g.lists.widthPx : 250;
      var lmin = g.lists.minWidthPx != null ? g.lists.minWidthPx : 180;
      var lmax = g.lists.maxWidthPx != null ? g.lists.maxWidthPx : 480;
      if (lw < lmin) lw = lmin; if (lw > lmax) lw = lmax;
    }
    r.setProperty("--bbw-list-w", lw + "px");
    r.setProperty("--bbw-box-h",           g.lists.boxHeightPx + "px");
    r.setProperty("--bbw-item-h",          g.lists.itemHeightPx + "px");
    r.setProperty("--bbw-item-gap",        g.lists.gapPx + "px");
    r.setProperty("--bbw-item-fs",         (g.lists.itemFontSizePx != null ? g.lists.itemFontSizePx : 15) + "px");
    r.setProperty("--bbw-compact-box-h",   (g.lists.compactBoxHeightPx   != null ? g.lists.compactBoxHeightPx   : 28) + "px");
    r.setProperty("--bbw-compact-item-h",  (g.lists.compactItemHeightPx  != null ? g.lists.compactItemHeightPx  : 18) + "px");
    r.setProperty("--bbw-compact-gap",     (g.lists.compactGapPx         != null ? g.lists.compactGapPx         : 9)  + "px");
    r.setProperty("--bbw-compact-fs",      (g.lists.compactFontSizePx    != null ? g.lists.compactFontSizePx    : 13) + "px");
    r.setProperty("--bbw-desc-max-game",   g.descScroll.maxHeightGamePx + "px");
    r.setProperty("--bbw-desc-max-cat",    g.descScroll.maxHeightCatPx + "px");
    r.setProperty("--bbw-rel-desc-h",      (g.related.descHeightPct / 100 * 720) + "px");
    var gl = g.gameLogo || {};
    r.setProperty("--bbw-gamelogo-top",     (gl.topPx != null ? gl.topPx : 38) + "px");
    r.setProperty("--bbw-gamelogo-listtop", (gl.listTopPx != null ? gl.listTopPx : 158) + "px");
    r.setProperty("--bbw-gamelogo-maxh",    (gl.maxHeightPx != null ? gl.maxHeightPx : 90) + "px");
    r.setProperty("--bbw-gamelogo-op",      (gl.opacity != null ? gl.opacity : 0.8));
    var fa = g.fanart || {};
    r.setProperty("--bbw-fanart-op",        (fa.opacity != null ? fa.opacity : 0.07));
    r.setProperty("--bbw-fanart-fade",      (fa.fadeMs != null ? fa.fadeMs : 2000) + "ms");
    var pv = g.posterView || {};
    var pcw = pv.cellWidthPx != null ? pv.cellWidthPx : 105;
    r.setProperty("--bbw-poster-cell",      pcw + "px");
    r.setProperty("--bbw-poster-imgh",      Math.round(pcw * (pv.cellRatio != null ? pv.cellRatio : 1.4)) + "px");
    r.setProperty("--bbw-poster-gap",       (pv.gapPx != null ? pv.gapPx : 12) + "px");
    r.setProperty("--bbw-poster-side",      (pv.sideWidthPx != null ? pv.sideWidthPx : 345) + "px");
    // Animations du hero du panneau droit (fanart + clear logo pulse).
    // Les valeurs JS (delay du setTimeout) sont lues directement par
    // schedulePosterFanart depuis G.posterView ; on expose ici les
    // paramètres CSS (transition-duration / animation-* / opacité finale)
    // en custom properties pour qu'un toggle des sliders soit reflété
    // sans rebuild.
    r.setProperty("--bbw-hero-fanart-opacity",   (pv.heroFanartOpacity   != null ? pv.heroFanartOpacity   : 0.28));
    r.setProperty("--bbw-hero-fanart-fade-in",   (pv.heroFanartFadeInMs  != null ? pv.heroFanartFadeInMs  : 300) + "ms");
    r.setProperty("--bbw-hero-fanart-fade-out",  (pv.heroFanartFadeOutMs != null ? pv.heroFanartFadeOutMs : 800) + "ms");
    r.setProperty("--bbw-hero-logo-delay",       (pv.heroLogoDelayMs   != null ? pv.heroLogoDelayMs   : 500)  + "ms");
    r.setProperty("--bbw-hero-logo-pulse",       (pv.heroLogoPulseMs   != null ? pv.heroLogoPulseMs   : 1000) + "ms");
    r.setProperty("--bbw-hero-logo-pulse-scale", (pv.heroLogoPulseScale!= null ? pv.heroLogoPulseScale: 1.10));
    r.setProperty("--bbw-poster-cell-hover-scale", (pv.cellHoverScale != null ? pv.cellHoverScale : 1.035));
    r.setProperty("--bbw-poster-cell-hover-ms",    (pv.cellHoverMs    != null ? pv.cellHoverMs    : 250) + "ms");
    r.setProperty("--bbw-poster-tint-opacity",     (pv.gridTintOpacity!= null ? pv.gridTintOpacity: 0.30));
    r.setProperty("--bbw-poster-tint-fade",        (pv.gridTintFadeMs != null ? pv.gridTintFadeMs : 700) + "ms");
    r.setProperty("--bbw-poster-tint-blur",        (pv.gridTintBlurPx != null ? pv.gridTintBlurPx : 80)  + "px");
    // Toggle .fluid sur .poster-grid → bascule du mode FIXE ↔ FLUID sans
    // rebuild du DOM (les <img>.pc-img existent déjà, seul leur sizing CSS
    // change). Robuste à un appel avant que .poster-grid soit construit
    // (querySelector renvoie null, on no-op).
    var pgrid = document.querySelector(".poster-grid");
    if (pgrid) pgrid.classList.toggle("fluid", !!pv.fluid);
    // Toggle .fluid sur .recent → bascule object-fit:contain (fluid) ↔ cover
    // (poster ratio) pour les vignettes de la rangée "jeux récents".
    var pv2 = g.recentsView || {};
    var rec = document.querySelector(".recent");
    if (rec) rec.classList.toggle("fluid", !!pv2.fluid);
  };

  /* ============================================================================
     CONFIG UTILISATEUR (cookie) + SCHÉMA des options
     - configDefaults : copie des valeurs par défaut (avant surcharge cookie).
     - window.BBW.cfg : get/def/set/reset + sauvegarde cookie des surcharges.
     - configSchema : description des menus/options pour l'UI de réglages (engine/app.js).
     Les surcharges du cookie sont appliquées MAINTENANT (avant engine/app.js) → tout le
     thème lit déjà les valeurs réglées par l'utilisateur au démarrage.
     ========================================================================== */
  function getByPath(o, p) { var k = p.split("."); for (var i = 0; i < k.length; i++) { if (o == null) return undefined; o = o[k[i]]; } return o; }
  function setByPath(o, p, v) { var k = p.split("."); for (var i = 0; i < k.length - 1; i++) { if (o[k[i]] == null) o[k[i]] = {}; o = o[k[i]]; } o[k[k.length - 1]] = v; }
  function readCookie(n) { var m = document.cookie.match(new RegExp("(?:^|; )" + n + "=([^;]*)")); return m ? decodeURIComponent(m[1]) : null; }
  function writeCookie(n, v, days) { var d = new Date(); d.setTime(d.getTime() + days * 864e5); document.cookie = n + "=" + encodeURIComponent(v) + ";expires=" + d.toUTCString() + ";path=/"; }

  window.BBW.configDefaults = JSON.parse(JSON.stringify(window.BBW.config.global));   // défauts figés
  window.BBW.cfg = {
    _ov: {},                                                   // surcharges {path: value}
    get: function (p) { return getByPath(window.BBW.config.global, p); },
    def: function (p) { return getByPath(window.BBW.configDefaults, p); },
    set: function (p, v) {
      if (JSON.stringify(v) === JSON.stringify(this.def(p))) delete this._ov[p]; else this._ov[p] = v;
      setByPath(window.BBW.config.global, p, v); this.save();
    },
    reset: function (p) { delete this._ov[p]; setByPath(window.BBW.config.global, p, this.def(p)); this.save(); },
    save: function () { try { writeCookie("bbw_cfg", JSON.stringify(this._ov), 365); } catch (e) {} }
  };
  // Charge les surcharges du cookie et les applique sur la config (avant le moteur).
  (function () {
    try {
      var raw = readCookie("bbw_cfg");
      if (raw) { var ov = JSON.parse(raw) || {}; window.BBW.cfg._ov = ov; for (var p in ov) setByPath(window.BBW.config.global, p, ov[p]); }
    } catch (e) {}
  })();

  /* Menus + options exposés dans l'UI de réglages. type: bool | slider | select.
     (Tout est manipulable au pad/tablette : ←/→ ajuste, A remet par défaut.) */
  window.BBW.configSchema = [
    { title: "General", opts: [
      { path: "layout.mode",        type: "select", label: "Layout mode", options: ["auto", "desktop", "tablet", "phone"] },
      { path: "sounds.enabled",     type: "bool",   label: "Navigation sounds" },
      { path: "sounds.volume",      type: "slider", label: "Sound volume", min: 0, max: 1, step: 0.05 },
      { path: "clock.enabled",      type: "bool",   label: "Topbar clock" },
      { path: "clock.ampm",         type: "bool",   label: "12-hour clock (AM/PM)" },
      { path: "search.layout",      type: "select", label: "Keyboard layout", options: ["auto", "qwerty", "azerty", "qwertz"] },
      { path: "mouse.enabled",      type: "bool",   label: "Mouse (desktop)" },
      { path: "gamepad.deadzone",   type: "slider", label: "Gamepad deadzone", min: 0.1, max: 0.9, step: 0.05 },
      { path: "debug.kioskIndicators", type: "bool", label: "Kiosk debug indicators (focus + sound)" }
    ] },
    { title: "Wheel", opts: [
      { path: "lists.boxHeightPx",      type: "slider", label: "Selected item height (px)", min: 24, max: 80, step: 1 },
      { path: "lists.itemHeightPx",     type: "slider", label: "Item height (px)",          min: 16, max: 80, step: 1 },
      { path: "lists.gapPx",            type: "slider", label: "Item vertical gap (px)",    min: 0,  max: 30, step: 1 },
      { path: "lists.itemFontSizePx",   type: "slider", label: "Item font size (px)",       min: 10, max: 28, step: 1 },
      { path: "lists.desktopScrollMs",  type: "slider", label: "Wheel scroll (ms)",         min: 0,  max: 600, step: 20 },
      { path: "lists.wheelSnapMs",      type: "slider", label: "Mobile wheel snap (ms)",    min: 0,  max: 600, step: 20 },
      { path: "menuScroll.enabled",     type: "bool",   label: "Label marquee" },
      { path: "menuScroll.startDelayMs",type: "slider", label: "Marquee start delay (ms)",  min: 0,  max: 8000, step: 100 },
      { path: "menuScroll.speedLeftPxS",type: "slider", label: "Marquee speed (px/s)",      min: 5,  max: 120, step: 5 },

      { path: "lists.compactEnabled",       type: "bool",   label: "Use compact size when crowded" },
      { path: "lists.compactThresholdN",    type: "slider", label: "Compact threshold (items)",   min: 5,  max: 200, step: 1 },
      { path: "lists.compactBoxHeightPx",   type: "slider", label: "Compact selected item (px)",  min: 14, max: 60, step: 1 },
      { path: "lists.compactItemHeightPx",  type: "slider", label: "Compact item height (px)",    min: 10, max: 60, step: 1 },
      { path: "lists.compactGapPx",         type: "slider", label: "Compact gap (px)",            min: 0,  max: 20, step: 1 },
      { path: "lists.compactFontSizePx",    type: "slider", label: "Compact item font size (px)", min: 8,  max: 22, step: 1 },

      { path: "lists.turboEnabled",         type: "bool",   label: "Hold up/down turbo" },
      { path: "lists.turboDelayMs",         type: "slider", label: "Turbo activation (ms)",       min: 500, max: 10000, step: 100 },
      { path: "lists.turboMultiplierPct",   type: "slider", label: "Turbo multiplier (%)",        min: 100, max: 1000, step: 25 },
      { path: "lists.pageStepN",            type: "slider", label: "PgUp/PgDn step (items)",      min: 1,   max: 50,   step: 1 },

      { path: "lists.resizable",            type: "bool",   label: "Resizable wheel (desktop drag)" },
      { path: "lists.widthPx",              type: "slider", label: "Wheel width (px)",            min: 180, max: 480, step: 5 },
      { path: "lists.scrollbarEnabled",     type: "bool",   label: "Wheel scrollbar (desktop, on overflow)" },
      { path: "lists.scrollbarMaxThumbPct", type: "slider", label: "Scrollbar max thumb size", min: 0.04, max: 0.5, step: 0.01 }
    ] },
    { title: "Animations", opts: [
      { path: "screenTransition.enter",      type: "select", label: "Screen transition", options: ["fade", "slide-v"] },
      { path: "screenTransition.durationMs", type: "slider", label: "Screen duration (ms)", min: 0, max: 800, step: 20 },
      { path: "contentTransition.enabled",   type: "bool",   label: "Content slide" },
      { path: "contentTransition.durationMs",type: "slider", label: "Content duration (ms)", min: 0, max: 800, step: 20 },
      { path: "contentTransition.dwellMs",   type: "slider", label: "Content dwell (ms)", min: 0, max: 600, step: 20 },
      { path: "highlight.slideMs",           type: "slider", label: "Highlight slide (ms)", min: 0, max: 400, step: 10 },
      { path: "highlight.flash.enabled",     type: "bool",   label: "Highlight flash" },
      { path: "highlight.flash.peak",        type: "slider", label: "Highlight flash peak", min: 0, max: 0.6, step: 0.02 },
      { path: "highlight.flash.durationMs",  type: "slider", label: "Highlight flash (ms)", min: 0, max: 1000, step: 25 },
      { path: "highlight.shine.enabled",     type: "bool",   label: "Highlight shine" },
      { path: "highlight.shine.periodMs",    type: "slider", label: "Highlight shine period (ms)", min: 2000, max: 20000, step: 500 },
      { path: "descScroll.enabled",          type: "bool",   label: "Description auto-scroll" },
      { path: "descScroll.maxHeightGamePx",  type: "slider", label: "Game desc max height (px)", min: 60, max: 280, step: 5 },
      { path: "descScroll.maxHeightCatPx",   type: "slider", label: "Category desc max height (px)", min: 60, max: 320, step: 5 }
    ] },
    { title: "Games", opts: [
      { path: "gameLogo.enabled",        type: "bool",   label: "Game clear logo (wheel)" },
      { path: "gameLogo.opacity",        type: "slider", label: "Logo opacity", min: 0, max: 1, step: 0.05 },
      { path: "gameLogo.maxHeightPx",    type: "slider", label: "Logo max height (px)", min: 30, max: 140, step: 2 },
      { path: "gameLogo.topPx",          type: "slider", label: "Logo zone top (px)", min: 0, max: 160, step: 2 },
      { path: "gameLogo.listTopPx",      type: "slider", label: "Wheel top below logo (px)", min: 80, max: 280, step: 2 },
      { path: "gameLogo.glow",           type: "bool",   label: "Logo glow" },
      { path: "gameLogo.accent",         type: "bool",   label: "Logo accent line" },
      { path: "platformLogoImage.enabled",    type: "bool",   label: "Platform clear logo" },
      { path: "platformLogoImage.opacity",    type: "slider", label: "Platform logo opacity", min: 0, max: 1, step: 0.05 },
      { path: "platformLogoImage.maxHeightPx",type: "slider", label: "Platform logo max height (px)", min: 16, max: 64, step: 1 },
      { path: "platformFit.enabled",     type: "bool",   label: "Hide platform on overlap" },
      { path: "platformFit.gapPx",       type: "slider", label: "Title-platform gap (px)", min: 0, max: 80, step: 2 },
      { path: "related.descHeightPct",   type: "slider", label: "Related desc height (%)", min: 2, max: 15, step: 0.5 },
      { path: "related.perTab",          type: "slider", label: "Related games per tab",   min: 5, max: 200, step: 5 },
      { path: "library.showBroken",      type: "bool",   label: "Show broken games" },
      { path: "library.favoriteStar",    type: "bool",   label: "Favorite star" },
      { path: "library.qualityStars",    type: "bool",   label: "Quality trophies" },
      { path: "gameFilename.enabled",    type: "bool",   label: "Show file name" }
    ] },
    { title: "Media", opts: [
      { path: "media.heavyDelayMs",   type: "slider", label: "Full/video delay (ms)", min: 0, max: 3000, step: 100 },
      { path: "media.prefetchAhead",  type: "slider", label: "Prefetch ahead", min: 0, max: 40, step: 1 },
      { path: "media.prefetchBehind", type: "slider", label: "Prefetch behind", min: 0, max: 20, step: 1 },
      { path: "media.fillScreenshot", type: "bool",   label: "Screenshot grid by video" },
      { path: "extraScreenshots.enabled", type: "bool", label: "Extra screenshots order" },
      { path: "fanart.enabled",       type: "bool",   label: "Fanart background" },
      { path: "fanart.opacity",       type: "slider", label: "Fanart opacity", min: 0, max: 0.5, step: 0.01 },
      { path: "fanart.fadeMs",        type: "slider", label: "Fanart fade (ms)", min: 0, max: 5000, step: 100 }
    ] },
    { title: "Poster", opts: [
      { path: "posterView.enabled",     type: "bool",   label: "Poster view available" },
      { path: "posterView.fluid",       type: "bool",   label: "Fluid size (keep image aspect ratio)" },
      { path: "posterView.cellWidthPx", type: "slider", label: "Poster width (px)", min: 70, max: 200, step: 5 },
      { path: "posterView.cellRatio",   type: "slider", label: "Poster height ratio (fixed mode)", min: 1, max: 2, step: 0.05 },
      { path: "posterView.gapPx",       type: "slider", label: "Poster gap (px)", min: 0, max: 30, step: 1 },
      { path: "posterView.sideWidthPx", type: "slider", label: "Detail panel width (px)", min: 240, max: 480, step: 10 },
      { path: "posterView.heroFanartDelayMs",        type: "slider", label: "Hero fanart load delay (ms)",       min: 0, max: 2000, step: 50 },
      { path: "posterView.heroFanartFadeOutDelayMs", type: "slider", label: "Hero fanart fade-out start (ms)",   min: 0, max: 2000, step: 50 },
      { path: "posterView.heroFanartFadeInMs",       type: "slider", label: "Hero fanart fade-in (ms)",          min: 0, max: 3000, step: 50 },
      { path: "posterView.heroFanartFadeOutMs",      type: "slider", label: "Hero fanart fade-out (ms)",         min: 0, max: 3000, step: 50 },
      { path: "posterView.heroFanartOpacity",   type: "slider", label: "Hero fanart opacity",           min: 0,    max: 0.6,  step: 0.01 },
      { path: "posterView.heroLogoDelayMs",    type: "slider", label: "Hero logo pulse delay (ms)",     min: 0,    max: 2000, step: 50 },
      { path: "posterView.heroLogoPulseMs",    type: "slider", label: "Hero logo pulse duration (ms)",  min: 200,  max: 3000, step: 50 },
      { path: "posterView.heroLogoPulseScale", type: "slider", label: "Hero logo pulse scale (x)",      min: 1,    max: 1.5,  step: 0.01 },
      { path: "posterView.cellHoverScale",     type: "slider", label: "Cell hover/select scale (x)",   min: 1,    max: 1.2,  step: 0.005 },
      { path: "posterView.cellHoverMs",        type: "slider", label: "Cell hover/select duration (ms)", min: 0,  max: 1000, step: 25 },
      { path: "posterView.gridTintEnabled",    type: "bool",   label: "Grid background tint (from poster)" },
      { path: "posterView.gridTintDelayMs",    type: "slider", label: "Grid tint load delay (ms)",     min: 0,    max: 2000, step: 50 },
      { path: "posterView.gridTintFadeMs",     type: "slider", label: "Grid tint fade (ms)",           min: 0,    max: 3000, step: 50 },
      { path: "posterView.gridTintOpacity",    type: "slider", label: "Grid tint opacity",             min: 0,    max: 0.6,  step: 0.01 },
      { path: "posterView.gridTintBlurPx",     type: "slider", label: "Grid tint blur (px)",           min: 20,   max: 200,  step: 5 }
    ] },
    { title: "Recents", opts: [
      { path: "recentsView.fluid", type: "bool", label: "Recents: fit images (aspect ratio)" }
    ] }
  ];
})();
