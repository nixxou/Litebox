/* ============================================================================
   BigBoxWeb — moteur (prototype)

   Navigation entre écrans (Fade / Slide Vertical desktop, swap mobile),
   sélection clavier + souris (survol/clic), surbrillance flottante animée
   (instantanée si touche maintenue), et ZONES de navigation :
     • Catégories : liste gauche  ↔ (→) rangée "Recent" (clic/Entrée = page jeu)
     • Jeux       : liste         ↔ (←) rail de saut (🔍 ☰ ★ #, A–Z)
   Contenu rempli depuis un dataset INLINE (données fausses).

   Clavier :  ↑/↓ sélection · ←/→ change de zone · Entrée/A entrer ·
              Échap/B retour · S system menu
   Vérif :    ?screen=games&i=1 · &zone=rail · &zone=recent
   Script classique (file://-safe).
   ========================================================================== */
(function () {
  "use strict";

  // ── Données ──────────────────────────────────────
  // Chargées via la couche BBW.get (engine/data.js) : dummy en standalone,
  // générées par le plugin en mode intégré. Remplies au boot (DOMContentLoaded).
  var DATA = { catTree: [], games: [], gamesAll: [], detailMenu: [], platform: "", platformLogo: "", platformLogoImg: "", platformTotal: 0, platformTotalAll: 0 };
  var relData = null;   // jeux liés du jeu courant (chargés à l'ouverture du popup Related)

  var RAIL = [{ k: "search", ic: "🔍" }, { k: "view", ic: "☰" }, { k: "fav", ic: "★" }, { k: "num", ic: "#" }]
    .concat("ABCDEFGHIJKLMNOPQRSTUVWXYZ".split("").map(function (c) { return { k: "letter", ic: c }; }));
  // ROM rail (sous-menu Select ROM, écran détail) : search / fav / recent / advanced / clear.
  // Recherche + Advanced sont des stubs pour l'instant — fav et recent appliquent un filtre
  // client sur la liste fetchée depuis /archive-entries. État vit sur la frame (frame.romFilters).
  var ROM_RAIL = [
    { k: "search",     ic: "🔍" },
    { k: "fav",        ic: "★" },
    { k: "recent",     ic: "↻" },
    { k: "adv",        ic: "☰" },
    { k: "clear",      ic: "⌫" }
  ];
  var romRailSel = 0;

  // ── Config centralisée (engine/config.js, chargé AVANT ce script) ─────────
  // C = tout le config ; G = son bloc `global` (raccourci très utilisé ci-dessous).
  // Toute valeur réglable du moteur vient de là — aucune constante en dur ici.
  var C = window.BBW.config, G = C.global;

  // ── Raccourcis clavier configurables (serveur) ────────────────────────────
  // Carte commande → touches. Les défauts reproduisent EXACTEMENT l'ancien
  // switch onKey ; le serveur (/bigbox/api/keybinds, alimenté par la config du
  // plugin → onglet Web) peut les surcharger. onKey consulte la table inverse
  // KEYCMD (touche → commande). mediaNext/mediaPrev restent dérivés de "right"
  // selon l'écran (non rebindables). Le fetch est asynchrone : tant qu'il n'a
  // pas répondu, les défauts s'appliquent (onKey ne tourne que sur appui, donc
  // après le chargement en pratique).
  var KEYCMD = (function (m) {
    var o = {}; for (var c in m) { var a = m[c] || []; for (var i = 0; i < a.length; i++) o[a[i]] = c; } return o;
  })({
    up: ["ArrowUp"], down: ["ArrowDown"], left: ["ArrowLeft"], right: ["ArrowRight"],
    pgup: ["PageUp"], pgdn: ["PageDown"],
    select: ["Enter", "a", "A"], back: ["Escape", "Backspace", "BrowserBack", "b", "B"],
    menu: ["s", "S"], poster: ["Tab"]
  });
  try {
    fetch("/bigbox/api/keybinds", { cache: "no-store" })
      .then(function (r) { return r.ok ? r.json() : null; })
      .then(function (j) {
        if (!j) return;
        var o = {};
        for (var c in j) { var a = j[c] || []; for (var i = 0; i < a.length; i++) if (a[i]) o[a[i]] = c; }
        // garde-fou : ne pas s'auto-verrouiller si le serveur renvoie une carte vide
        if (Object.keys(o).length) KEYCMD = o;
      })
      .catch(function () {});
  } catch (e) {}

  // Type de transition d'ENTRÉE d'écran : surcharge par page (pages.<écran>) sinon défaut global.
  function screenEnter(page) {
    var p = C.pages[page];
    return (p && p.screenTransition && p.screenTransition.enter) || G.screenTransition.enter;
  }

  // ── État ──────────────────────────────────────────────────────────────
  var sel = { games: 0, details: 0, system: 0 };  // catégories = via la pile catStack
  var zone = "list";          // "list" | "recent" | "rail"
  var recentSel = 0, railSel = 0;
  var current = "categories";
  var currentGame = 0, detailsReturn = "games";
  var shownCat = 0, catTimer = null;   // index affiché dans le niveau courant + timer dwell
  var recentItems = [];                // "recent" du nœud catégorie courant (items {id,t,thumb,…})
  var recentEpoch = 0;                 // cache-buster du recent (bumpé serveur à la sortie d'un jeu)
  var installEpoch = 0;                // bumpé serveur quand l'état installé d'un jeu store change
  var installPollTimer = null, installPollGi = -1;   // re-check live de l'état installé du jeu store sélectionné
  var gameIsRunning = false;           // état serveur : un jeu est-il en cours d'exécution ?
  var extractionInProgress = false;    // état serveur : une extraction d'archive est-elle en cours (Archive MGS) ? Bloque Play en parallèle même si LB n'a pas encore remonté isGameRunning.
  var playBlockUntilTs = 0;            // verrou client : Date.now() en dessous duquel le clic Play est ignoré (5 s post-clic)
  var epochPollTimer = null;           // polling périodique du heartbeat /api/recent/epoch
  var shownGame = 0, gameTimer = null; // idem pour la liste de jeux (transition par zone)
  var lastDir = 1;                     // dernier mouvement ↑(-1)/↓(+1) sur le menu
  var catStack = [];                   // pile des niveaux catégories : { nodes, sel, enterDir }
  var menuStack = [];                  // pile des niveaux du menu d'actions (page jeu) : { items, sel }
  var userRatings = {};                // note perso par index de jeu (0 = None, pas de demi-étoile)
  var ratingOpen = false, ratingVal = 0, ratingModalEl = null;   // popup Star Rating
  var ratingTouched = false, ratingHadUser = false;              // a-t-on ajusté ? / note perso préexistante ?
  var pinOpen = false, pinModalEl = null, pinKeys = [], pinFocus = 0, pinValue = "";   // popup digicode (Unlock)
  var pinPurpose = "unlock", pinInstallGi = -1;   // "unlock" = global parental unlock ; "install" = one-shot install authorization (no global unlock, no reload)
  // Recherche (écran jeux) : mini-clavier QWERTY + filtre live de la liste sur le compareName.
  var searchOpen = false, searchModalEl = null, searchKeyEls = [], searchR = 0, searchC = 0, searchQuery = "", kbMode = "quick";   // kbMode: "quick" (compareName) | "adv" (auto-complétion éditeur/dév)
  var favOnly = false;   // filtre "favoris seulement" (rail ★) : transitoire (cf. clearFavFilter)
  var gameStars = {};    // paliers qualité de la plateforme courante : { databaseID: 1|2|3 } (cf. loadPlatformStars)
  // Recherche avancée (rail ☰) : modale à onglets, filtrage client transitoire (Phase 1 : Année).
  var advOpen = false, advModalEl = null, advTab = 0, advFocus = 0, advActive = false, advTargets = [];
  var advCrit = null;          // critères courants (objet, cf. defaultCrit)
  var advTextKind = "publisher";   // champ texte courant pour le clavier adv ("publisher" | "developer")
  var ADV_TABS = ["general", "genre", "publisher", "developer", "orderby", "history"];
  // Dispositions de touches (lettres + chiffres) ; la rangée du bas est commune. Choix
  // QWERTY/AZERTY selon la langue LB (cf. chooseLayout). KB est (re)construit dans setupSearch.
  var KB_LAYOUTS = {
    qwerty: ["1234567890", "QWERTYUIOP", "ASDFGHJKL", "ZXCVBNM"],
    azerty: ["1234567890", "AZERTYUIOP", "QSDFGHJKLM", "WXCVBN"],
    qwertz: ["1234567890", "QWERTZUIOP", "ASDFGHJKL", "YXCVBNM"]
  };
  var KB_BOTTOM = ["{space}", "{bksp}", "{clr}", "{ok}"];
  var KB = KB_LAYOUTS.qwerty.map(function (s) { return s.split(""); }).concat([KB_BOTTOM.slice()]);
  // Contrôle parental (état renvoyé par /api/parental/state ; verrou = cookie côté serveur).
  var parental = { active: false, locked: false, canUnlock: false, bigBox: false, lockedOut: false, maxAttempts: 3, canRate: true, canFav: true, installNeedsUnlock: false };
  // canRate/canFav : autorisation effective côté client de modifier la note
  // ou le favori (réplique le contrat du backend BigBoxMutationApi.IsLockedAndDenied).
  //   not locked  → toujours true
  //   locked      → vrai uniquement si l'option correspondante est cochée
  //                  dans ParentalControlConfig (AllowLockedUserToModifyRatings /
  //                  AllowLockedUserToModifyFavorites).
  // Default true → si la config parentale n'est pas chargée ou que l'endpoint
  // ne renvoie pas le champ (cas vieux backend), on n'empêche rien.
  function canRateGame() { return !!parental.canRate; }
  function canFavGame()  { return !!parental.canFav; }
  var relOpen = false, relModalEl = null, relTab = 0, relSel = 0;   // popup Related Games (onglets)
  var vndbOpen = false, vndbModalEl = null;                          // popup Tags VNDB (panneau scrollable)
  var cfgOpen = false, cfgModalEl = null, cfgTab = 0, cfgFocus = 0, cfgTargets = [], cfgDirty = false;   // UI Réglages (Settings)
  var screens = {};

  var $ = function (s, r) { return (r || document).querySelector(s); };
  function curFrame() { return catStack[catStack.length - 1]; }
  function curNodes() { return curFrame().nodes; }
  function catNode(i) { return curNodes()[i]; }
  function detailLevel() { return menuStack[menuStack.length - 1]; }   // niveau courant du menu d'actions
  // Sélection courante d'un écran : catégories → pile catStack, détail → pile menuStack, sinon sel[id].
  function currentSel(id) {
    if (id === "categories") return curFrame().sel;
    if (id === "details") return detailLevel().sel;
    return sel[id];
  }
  // Roue de sélection : sur appareil tactile (phone OU tablette) pour les écrans à liste.
  function isWheel(id) { return window.BBW.isTouch() && (id === "categories" || id === "games"); }
  var wheelScroll = {};   // décalage logique de la roue (px) par écran

  // « Souris active » : faux au chargement / après une touche / un changement
  // d'écran ; vrai seulement après un VRAI mouvement (évite l'auto-survol de
  // l'élément sous le curseur quand un écran apparaît dessous). Le survol (hover)
  // n'agit que si mouseActive.
  var mouseActive = false, mLastX = null, mLastY = null, mAcc = 0, idleTimer = null;
  // hideCursor ne fait rien si la souris est désactivée → le curseur reste visible
  // (les clics directs sur les éléments fonctionnent toujours).
  function hideCursor() { if (G.mouse.enabled && document.body) document.body.classList.add("cursor-hidden"); }
  function showCursor() { if (document.body) document.body.classList.remove("cursor-hidden"); }
  function deactivateMouse() {
    mouseActive = false; mLastX = null; mLastY = null; mAcc = 0;
    if (idleTimer) { clearTimeout(idleTimer); idleTimer = null; }
    hideCursor();
  }
  document.addEventListener("mousemove", function (e) {
    if (!G.mouse.enabled || window.BBW.isTouch()) return;   // souris coupée ou tactile : pas de survol, pas de masquage
    showCursor();
    if (idleTimer) clearTimeout(idleTimer);
    if (G.mouse.cursorAutoHideMs > 0)        // 0 = pas de masquage auto en idle
      idleTimer = setTimeout(deactivateMouse, G.mouse.cursorAutoHideMs);
    if (mouseActive) return;
    if (mLastX !== null) mAcc += Math.abs(e.clientX - mLastX) + Math.abs(e.clientY - mLastY);
    mLastX = e.clientX; mLastY = e.clientY;
    if (mAcc > G.mouse.hoverMoveThresholdPx) mouseActive = true;   // mouvement réel cumulé
  });

  // Molette (desktop) : navigue le menu déroulant courant (liste, ou rail/Recent).
  var wheelAcc = 0, lastWheelTime = 0;
  document.addEventListener("wheel", function (e) {
    if (window.BBW.isTouch() || !G.mouse.enabled || !G.mouse.wheel.enabled) return;
    // Mode poster + curseur SUR la grille (pas le panneau détail à droite) :
    // on laisse le browser scroller la grille nativement (overflow-y:auto).
    // La sb custom .bbw-poster-sb suit via grid.addEventListener("scroll").
    // Sans ce by-pass, la molette est interceptée pour move(±1) et la grille
    // ne défile jamais à la molette.
    if (posterMode && current === "games" &&
        e.target && e.target.closest && e.target.closest(".poster-grid")) {
      return;
    }
    lastWheelTime = Date.now();
    // Normalise selon deltaMode : Firefox renvoie souvent des LIGNES (deltaMode 1),
    // Chrome des PIXELS (0). Sans ça, le seuil stepPx (en px) n'est jamais atteint
    // sous Firefox → molette inopérante.
    var dy = e.deltaY;
    if (e.deltaMode === 1) dy *= 40;                              // lignes → px (~1 cran = 1 pas)
    else if (e.deltaMode === 2) dy *= (window.innerHeight || 800); // pages → px
    wheelAcc += dy;
    if (Math.abs(wheelAcc) < G.mouse.wheel.stepPx) return;
    var d = wheelAcc > 0 ? 1 : -1; wheelAcc = 0; deactivateMouse();
    if (current === "games" && zone === "rail") { railSel = Math.max(0, Math.min(RAIL.length - 1, railSel + d)); paintRail(); }
    else { move(d, false); }
    e.preventDefault();
  }, { passive: false });

  // Clic droit = retour (et on masque le menu contextuel). Désactivable par config :
  // si coupé, on laisse le menu contextuel natif s'afficher.
  document.addEventListener("contextmenu", function (e) {
    if (window.BBW.isTouch() || !G.mouse.enabled || !G.mouse.rightClickBack) return;
    e.preventDefault();
    deactivateMouse(); goBack();
  });

  // Clic gauche sur une zone NON cliquable, peu après une molette = select.
  document.addEventListener("click", function (e) {
    if (window.BBW.isTouch() || !G.mouse.enabled || e.button !== 0) return;
    if (e.target.closest && e.target.closest(".list-item, .thumb, .rail-item, .bottombar")) return; // éléments cliquables : gérés par eux-mêmes
    if (Date.now() - lastWheelTime < G.mouse.clickAfterWheelMs) activateCurrent();
  });

  // ── Son des vidéos (politique autoplay) ─────────────────────────────────
  // Les navigateurs n'autorisent l'autoplay qu'en MUET. On démarre donc muet, et on
  // débloque le son au 1er VRAI geste utilisateur (clavier/souris/tactile) — un appui
  // manette ne compte pas comme geste pour le navigateur. On réessaie à chaque geste
  // pour les vidéos créées de façon asynchrone (navigation liste de jeux).
  var audioOn = false;
  function unlockAudio() {
    audioOn = true;
    var v = document.querySelector(".screen.active .media .rgn-inner:not(.rgn-clone) video");
    if (v && v.muted) { v.muted = false; var p = v.play(); if (p && p.catch) p.catch(function () { v.muted = true; }); }
  }
  document.addEventListener("keydown", unlockAudio);
  document.addEventListener("pointerdown", unlockAudio);

  // ── Sons de navigation (mp3 dans web/sounds/, set "Sci-Fi Set 3 by Clavius") ──
  // move = ↑/↓/←/→ · select = A/Entrée · back = B/Échap. Gardé sur audioOn (débloqué au
  // 1er geste — un appui manette ne compte pas). Restart à chaque appel (scroll rapide
  // = re-déclenche, comme BigBox). Config: G.sounds {enabled, volume}.
  var _navAudio = null;
  function navAudio() {
    if (!_navAudio) _navAudio = {
      move:   new Audio("sounds/move.mp3"),
      select: new Audio("sounds/select.mp3"),
      back:   new Audio("sounds/back.mp3")
    };
    return _navAudio;
  }
  function playNavSound(cmd) {
    var s = G.sounds || {};
    if (s.enabled === false || !audioOn) return;
    var na = navAudio(), a;
    if (cmd === "up" || cmd === "down" || cmd === "left" || cmd === "right") a = na.move;
    else if (cmd === "select") a = na.select;
    else if (cmd === "back") a = na.back;
    else return;
    try { a.volume = (s.volume != null ? s.volume : 0.35); a.currentTime = 0; var p = a.play(); if (p && p.catch) p.catch(function () {}); } catch (_) {}
  }
  document.addEventListener("touchstart", unlockAudio, { passive: true });

  // ── Construction des listes (une fois) ──────────────────────────────────
  // Bascule .compact-wheel sur le .screen quand le nb d'items dépasse le seuil
  // configuré (lists.compactEnabled + compactThresholdN). La CSS surcharge alors
  // les 4 variables wheel/list par leurs équivalents compactes — la roue, les
  // sous-menus détail et la roue mobile s'adaptent ensemble.
  function applyCompactWheel(screenId, count) {
    var el = screens[screenId]; if (!el) return;
    var L = (G && G.lists) || {};
    var on = !!L.compactEnabled && (count | 0) > (L.compactThresholdN | 0);
    el.classList.toggle("compact-wheel", on);
  }

  function setupList(screenId, texts) {
    var root = screens[screenId];
    var list = $(".list", root);
    var scroll = document.createElement("div"); scroll.className = "list-scroll";
    var itemCount = 0;
    if (texts) {
      texts.forEach(function (t) { var d = document.createElement("div"); d.className = "list-item"; d.innerHTML = t; scroll.appendChild(d); });
      itemCount = texts.length;
    } else {  // items statiques déjà dans le HTML (details/system) → on les déplace dans le scroll
      var statics = Array.prototype.slice.call(list.querySelectorAll(".list-item"));
      statics.forEach(function (it) { scroll.appendChild(it); });
      itemCount = statics.length;
    }
    applyCompactWheel(screenId, itemCount);
    var hl = document.createElement("div"); hl.className = "list-highlight";
    scroll.insertBefore(hl, scroll.firstChild);
    list.innerHTML = ""; list.appendChild(scroll);

    scroll.querySelectorAll(".list-item").forEach(function (it, i) {
      it.dataset.i = i;
      it.addEventListener("mouseenter", function () {
        if (current === screenId && zone === "list" && !window.BBW.isTouch() && mouseActive) setSelected(screenId, i, {});
      });
      it.addEventListener("click", function () {
        if (current !== screenId) return;
        zone = "list"; setSelected(screenId, i, { instant: true }); descend();
      });
    });
    // La grille poster reflète la liste de jeux : on la reconstruit quand la liste change
    // (changement de plateforme, filtre, recherche…) si la vue poster est active.
    if (screenId === "games" && posterMode) buildPosterGrid();
  }

  function positionHighlight(screenId, instant) {
    var list = $(".list", screens[screenId]);
    var hl = $(".list-highlight", list), it = $(".list-item.selected", list);
    if (!hl || !it) return;
    hl.classList.toggle("instant", !!instant);
    hl.style.width = it.offsetWidth + "px";
    hl.style.height = it.offsetHeight + "px";
    hl.style.transform = "translate(" + it.offsetLeft + "px," + it.offsetTop + "px)";
  }

  // Effets de la surbrillance : flash bref au changement, reflet périodique en idle.
  var shineTimer = null;
  function flashHighlight(screenId) {
    if (!G.highlight.flash.enabled) return;
    var hl = $(".list-highlight", screens[screenId]); if (!hl) return;
    hl.classList.remove("flash"); void hl.offsetWidth; hl.classList.add("flash");   // redéclenche l'anim
  }
  function scheduleShine() {
    document.querySelectorAll(".list-highlight.shine").forEach(function (h) { h.classList.remove("shine"); });
    if (shineTimer) clearTimeout(shineTimer);
    if (!G.highlight.shine.enabled) return;
    shineTimer = setTimeout(function () { var h = $(".list-highlight", screens[current]); if (h) h.classList.add("shine"); }, G.highlight.shine.delayMs);
  }

  // ── Défilement auto des descriptions longues ────────────────────────────
  // Si le texte dépasse sa hauteur d'affichage max : attente → descente lente →
  // pause en bas → remontée (plus rapide) → on reboucle (cf. config.descScroll).
  // Relancé à chaque changement de description ; le délai initial fait qu'on ne
  // défile pas tant que l'utilisateur parcourt rapidement les éléments.
  var descTimers = [], descEl = null;
  function descClear() {
    for (var i = 0; i < descTimers.length; i++) clearTimeout(descTimers[i]);
    descTimers = [];
    if (descEl) { descEl.style.transition = "none"; descEl.style.transform = "translateY(0)"; descEl = null; }
  }
  // Fenêtre clippée + contenu à translater, selon l'écran courant (LIVE, pas le clone).
  function descTarget() {
    var root = screens[current]; if (!root) return null;
    if (current === "games" || current === "details") {
      var v = $(".detail .rgn-inner:not(.rgn-clone) .desc", root);   // la zone détail VIVE (pas le clone de transition)
      return v ? { vp: v, content: $(".desc-inner", v) } : null;
    }
    if (current === "categories") {
      var box = $(".cat-detail .rgn-inner:not(.rgn-clone) .cat-box", root);
      return box ? { vp: box, content: $(".desc-text", box) } : null;   // null pour les playlists (stats)
    }
    return null;
  }
  function descPlay() {
    descClear();
    var ds = G.descScroll; if (!ds || !ds.enabled || window.BBW.isMobile()) return;
    var t = descTarget(); if (!t || !t.content || !t.vp.clientHeight) return;
    var content = t.content;
    content.style.transition = "none"; content.style.transform = "translateY(0)";
    var overflow = t.vp.scrollHeight - t.vp.clientHeight;   // distance cachée = ce qu'on fait défiler
    if (overflow <= 1) return;                              // tient dans la zone → rien à faire
    descEl = content;
    var down = Math.max(300, overflow / Math.max(1, ds.speedDownPxS) * 1000);
    var up   = Math.max(200, overflow / Math.max(1, ds.speedUpPxS) * 1000);
    function goDown() {
      content.style.transition = "transform " + down + "ms linear";
      content.style.transform = "translateY(" + (-overflow) + "px)";
      descTimers.push(setTimeout(goUp, down + ds.bottomPauseMs));
    }
    function goUp() {
      content.style.transition = "transform " + up + "ms linear";
      content.style.transform = "translateY(0)";
      descTimers.push(setTimeout(goDown, up + ds.startDelayMs));
    }
    descTimers.push(setTimeout(goDown, ds.startDelayMs));
  }

  // ── Marquee du label de l'item de menu sélectionné (s'il dépasse le bouton) ──
  // Inactivité → défile à gauche → pause au bout → retour (plus rapide) → reboucle.
  // Le texte est enveloppé dans un <span.lbl> (inline-block) translaté ; le bouton clippe
  // (overflow:hidden). On dé-enveloppe au reset → l'ellipsis revient pour les non-sélectionnés.
  var lblTimers = [], lblEl = null, lblItem = null;
  function labelScrollClear() {
    for (var i = 0; i < lblTimers.length; i++) clearTimeout(lblTimers[i]);
    lblTimers = [];
    if (lblEl) { lblEl.style.transition = "none"; lblEl.style.transform = "translateX(0)"; }
    if (lblItem) { lblItem.classList.remove("lbl-scrolling"); unwrapLabel(lblItem); }   // texte en place → étoile visible
    lblEl = null; lblItem = null;
  }
  function wrapLabel(item) {
    var s = item.querySelector(":scope > .lbl");
    if (!s) { s = document.createElement("span"); s.className = "lbl"; while (item.firstChild) s.appendChild(item.firstChild); item.appendChild(s); }
    return s;
  }
  function unwrapLabel(item) {
    var s = item.querySelector ? item.querySelector(":scope > .lbl") : null; if (!s) return;
    while (s.firstChild) item.insertBefore(s.firstChild, s); item.removeChild(s);
  }
  function labelScrollPlay(screenId) {
    // Appel pour un autre écran (init / préchargement des listes) : ne PAS toucher
    // au marquee de l'écran courant, sinon le boot le tue avant qu'il démarre.
    if (screenId !== current) return;
    labelScrollClear();
    var ms = G.menuScroll; if (!ms || !ms.enabled || window.BBW.isMobile()) return;
    var item = $(".list .list-item.selected", screens[screenId]); if (!item) return;
    var s = wrapLabel(item);
    s.style.transition = "none"; s.style.transform = "translateX(0)";
    var cs = getComputedStyle(item);
    // Fenêtre visible = le conteneur .list (clip 250px), pas la largeur de l'item :
    // l'item peut se dimensionner à son contenu, c'est .list qui clippe.
    var listEl = item.closest ? item.closest(".list") : null;
    var viewport = listEl ? listEl.clientWidth : item.clientWidth;
    var avail = viewport - (parseFloat(cs.paddingLeft) || 0) - (parseFloat(cs.paddingRight) || 0);
    var overflow = s.scrollWidth - avail;        // ce qui dépasse à droite
    if (overflow <= 1) { unwrapLabel(item); return; }   // tient → pas de marquee, on restaure
    lblItem = item; lblEl = s;
    var left = Math.max(300, overflow / Math.max(1, ms.speedLeftPxS) * 1000);
    var right = Math.max(200, overflow / Math.max(1, ms.speedRightPxS) * 1000);
    function goLeft() {
      if (lblItem) lblItem.classList.add("lbl-scrolling");   // le texte part → masque l'étoile favori
      s.style.transition = "transform " + left + "ms linear";
      s.style.transform = "translateX(" + (-overflow) + "px)";
      lblTimers.push(setTimeout(goRight, left + ms.endPauseMs));
    }
    function goRight() {
      s.style.transition = "transform " + right + "ms linear";
      s.style.transform = "translateX(0)";
      lblTimers.push(setTimeout(function () { if (lblItem) lblItem.classList.remove("lbl-scrolling"); }, right));   // texte revenu → réaffiche l'étoile
      lblTimers.push(setTimeout(goLeft, right + ms.startDelayMs));
    }
    lblTimers.push(setTimeout(goLeft, ms.startDelayMs));
  }

  // Défile la liste (en interne, desktop) pour garder l'item sélectionné visible.
  var listScrollY = {};
  function scrollListIntoView(screenId, instant) {
    var list = $(".list", screens[screenId]); if (!list) return;
    var scroll = $(".list-scroll", list); if (!scroll) return;
    if (isWheel(screenId)) {                       // ROUE mobile : centre l'item sélectionné
      var i0 = $(".list-item", scroll), pitch = i0 ? i0.offsetHeight : 56;
      var i = currentSel(screenId); wheelScroll[screenId] = i * pitch;
      scroll.classList.toggle("dragging", !!instant);
      scroll.style.transform = "translateY(" + (list.clientHeight / 2 - pitch / 2 - i * pitch) + "px)";
      return;
    }
    if (window.BBW.isMobile()) { scroll.style.transform = "none"; return; }  // details/system : flux
    var it = $(".list-item.selected", scroll); if (!it) return;
    var viewH = list.clientHeight, top = it.offsetTop, bot = top + it.offsetHeight;
    var cur = listScrollY[screenId] || 0;
    var maxS = Math.max(0, scroll.offsetHeight - viewH);
    if (top - cur < 0) cur = top;
    else if (bot - cur > viewH) cur = bot - viewH;
    cur = Math.max(0, Math.min(cur, maxS));
    listScrollY[screenId] = cur;
    scroll.classList.toggle("instant", !!instant);
    scroll.style.transform = "translateY(" + (-cur) + "px)";
  }

  // Sélection légère pendant le glissement de la roue (n'auto-centre pas).
  function setWheelSelected(screenId, i) {
    var n = listLength(screenId); if (n <= 0) return; i = Math.max(0, Math.min(n - 1, i));
    if (screenId === "categories") curFrame().sel = i; else sel[screenId] = i;
    screens[screenId].querySelectorAll(".list .list-item").forEach(function (it, k) { it.classList.toggle("selected", k === i); });
    // Tablette : le contenu classique reste affiché → il suit la roue (en phone il est masqué).
    if (window.BBW.isTablet()) {
      if (screenId === "categories") scheduleCatContent(i, false);
      else if (screenId === "games") {
        // scheduleGameContent (et PAS fillGamePanel seul) : la liste plugin est LÉGÈRE
        // (la vidéo vient de detail.json, pas de la liste). fillGamePanel seul ne posait
        // que la vignette → aucune vidéo ne se chargeait en parcourant la roue (elle
        // n'apparaissait qu'après être passé par le détail). Comme la nav clavier, on
        // programme le palier LOURD : vignette tout de suite, vidéo ~1 s après l'arrêt
        // (le timer se réarme à chaque cran), et cancelGameContentLoads coupe la vidéo
        // précédente à chaque changement.
        scheduleGameContent(i, true);
        $(".page", screens.games).textContent = (i + 1) + " / " + DATA.platformTotal;
      }
    }
    labelScrollPlay(screenId);   // marquee du label sélectionné (roue tablette)
  }

  // Roue tactile (mobile) : on traîne la liste au doigt, l'item au centre est
  // sélectionné, snap sur le plus proche au relâchement ; tap = sélectionner /
  // entrer si déjà centré.
  function setupWheel(screenId) {
    var list = $(".list", screens[screenId]);
    var startX = 0, startY = 0, startScroll = 0, dragging = false, moved = false, axis = "", pitch = 56;
    function apply(s, drag) {
      var sc = $(".list-scroll", list);
      sc.classList.toggle("dragging", !!drag);
      sc.style.transform = "translateY(" + (list.clientHeight / 2 - pitch / 2 - s) + "px)";
    }
    list.addEventListener("touchstart", function (e) {
      if (current !== screenId || !window.BBW.isTouch()) return;
      dragging = true; moved = false; axis = "";
      startX = e.touches[0].clientX; startY = e.touches[0].clientY; startScroll = wheelScroll[screenId] || 0;
      var i0 = $(".list-item", list); pitch = i0 ? i0.offsetHeight : 56;
    }, { passive: true });
    list.addEventListener("touchmove", function (e) {
      if (!dragging) return;
      var dx = e.touches[0].clientX - startX, dy = e.touches[0].clientY - startY;
      // verrou d'axe : "x" seulement si nettement horizontal (sinon roue verticale)
      if (!axis && (Math.abs(dx) > G.swipe.axisLockPx || Math.abs(dy) > G.swipe.axisLockPx))
        axis = (Math.abs(dx) > Math.abs(dy) * G.swipe.axisLockDominance) ? "x" : "y";
      if (Math.abs(dx) > 5 || Math.abs(dy) > 5) moved = true;
      if (axis === "y") {                            // glissement vertical = roue
        var n = listLength(screenId), s = Math.max(0, Math.min((n - 1) * pitch, startScroll - dy));
        wheelScroll[screenId] = s; apply(s, true);
        setWheelSelected(screenId, Math.round(s / pitch));
      }
      e.preventDefault();
    }, { passive: false });
    list.addEventListener("touchend", function (e) {
      if (!dragging) return; dragging = false;
      var n = listLength(screenId);
      if (axis === "x") {                            // swipe horizontal franc = navigation
        var dx = e.changedTouches[0].clientX - startX, dyy = e.changedTouches[0].clientY - startY;
        if (Math.abs(dx) > G.swipe.thresholdPx && Math.abs(dx) > Math.abs(dyy) * G.swipe.dominance) { if (dx > 0) goBack(); else descend(); return; }  // →=retour, ←=select
      }
      var idx = Math.max(0, Math.min(n - 1, Math.round((wheelScroll[screenId] || 0) / pitch)));
      if (!moved) {                                  // tap (peu de mouvement)
        var t = e.changedTouches[0], el = document.elementFromPoint(t.clientX, t.clientY);
        var it = el && el.closest ? el.closest(".list-item") : null;
        if (it) { var ti = +it.dataset.i; if (ti === currentSel(screenId)) { descend(); return; } idx = ti; }
      }
      wheelScroll[screenId] = idx * pitch; setWheelSelected(screenId, idx); apply(idx * pitch, false);
    });
  }

  // Swipe horizontal franc sur les écrans NON-roue (détail jeu, system) : →=retour, ←=select.
  // Laisse le défilement vertical natif (on n'intercepte que l'horizontal franc).
  function setupSwipeNav(screenId) {
    var el = screens[screenId]; if (!el) return;
    var sx = 0, sy = 0, tracking = false;
    el.addEventListener("touchstart", function (e) {
      if (current !== screenId || !window.BBW.isTouch()) return;
      tracking = true; sx = e.touches[0].clientX; sy = e.touches[0].clientY;
    }, { passive: true });
    el.addEventListener("touchend", function (e) {
      if (!tracking) return; tracking = false;
      var dx = e.changedTouches[0].clientX - sx, dy = e.changedTouches[0].clientY - sy;
      if (Math.abs(dx) > G.swipe.thresholdPx && Math.abs(dx) > Math.abs(dy) * G.swipe.dominance) { if (dx > 0) goBack(); else descend(); }
    }, { passive: true });
  }

  // Tablette : un swipe FRANC vers le bas démarré dans la TITLE BAR (haut), au centre (40–60 %),
  // bascule la vue (roue ↔ poster) — moyen clair et découvrable au tactile.
  function setupPosterGesture() {
    var el = screens.games; if (!el) return;
    var sx = 0, sy = 0, tracking = false;
    el.addEventListener("touchstart", function (e) {
      if (current !== "games" || !window.BBW.isTouch()) return;
      var x = e.touches[0].clientX, y = e.touches[0].clientY, w = window.innerWidth || 1;
      var bar = $(".topbar", el), barBot = bar ? bar.getBoundingClientRect().bottom : 40;
      if (y <= barBot + 18 && x > w * 0.40 && x < w * 0.60) { tracking = true; sx = x; sy = y; }   // depuis la title bar, au centre
    }, { passive: true });
    el.addEventListener("touchend", function (e) {
      if (!tracking) return; tracking = false;
      var dx = e.changedTouches[0].clientX - sx, dy = e.changedTouches[0].clientY - sy;
      if (dy > 60 && dy > Math.abs(dx) * 1.4) togglePoster();   // swipe franc vers le bas
    }, { passive: true });
  }
  // ── Rail de saut (écran jeux) ───────────────────────────────────────────
  function setupRail() {
    var root = screens.games;
    var rail = $(".rail", root);
    rail.innerHTML = "";
    RAIL.forEach(function (item, i) {
      var d = document.createElement("div");
      d.className = "rail-item"; d.textContent = item.ic; d.dataset.i = i;
      d.addEventListener("mouseenter", function () { if (current === "games" && mouseActive) { openRail(); railSel = i; paintRail(); } });
      d.addEventListener("click", function () { if (current === "games") { openRail(); railSel = i; paintRail(); railActivate(); } });
      rail.appendChild(d);
    });
    var trig = $(".rail-trigger", root);
    if (trig) trig.addEventListener("mouseenter", function () { if (current === "games" && !window.BBW.isTouch() && mouseActive) openRail(); });
  }
  function paintRail() {
    var items = screens.games.querySelectorAll(".rail .rail-item");
    items.forEach(function (it, i) { it.classList.toggle("active", zone === "rail" && i === railSel); });
  }
  function openRail() { zone = "rail"; screens.games.classList.add("rail-open"); positionHighlight("games", true); paintRail(); if (posterMode) { computePosterCols(); paintPoster(true); } }
  function exitRail() { zone = "list"; screens.games.classList.remove("rail-open"); positionHighlight("games", true); paintRail(); if (posterMode) { computePosterCols(); paintPoster(true); } }
  function railActivate() {
    var item = RAIL[railSel];
    if (item.k === "letter" || item.k === "num") {
      var idx = findGameByInitial(item.k === "num" ? "#" : item.ic);
      if (idx >= 0) { exitRail(); setSelected("games", idx, { instant: true }); }
      else exitRail();
    }
    else if (item.k === "search") { exitRail(); openSearch(); }
    else if (item.k === "fav") { exitRail(); applyFavFilter(!favOnly); }   // bascule "favoris seulement"
    else if (item.k === "view") { exitRail(); if (advActive) clearAdvanced(); else openAdvanced(); }   // recherche avancée (toggle)
  }

  // ── ROM rail (sous-menu Select ROM) ────────────────────────────────────
  // Même UX que le rail de la roue jeux mais ancré à .screen-details. Visible
  // uniquement quand on est dans le sous-menu Select ROM (frame.isRomMenu = true).
  // Items : 🔍 search (stub), ★ fav (toggle), ↻ recent (toggle), ☰ advanced
  // (stub — modale à venir), ⌫ clear (reset des filtres).
  function setupRomRail() {
    var root = screens.details; if (!root) return;
    var rail = $(".rom-rail", root); if (!rail) return;
    rail.innerHTML = "";
    ROM_RAIL.forEach(function (item, i) {
      var d = document.createElement("div");
      d.className = "rail-item"; d.textContent = item.ic; d.dataset.i = i;
      d.addEventListener("mouseenter", function () { if (current === "details" && _inRomMenu() && mouseActive) { openRomRail(); romRailSel = i; paintRomRail(); } });
      d.addEventListener("click", function () { if (current === "details" && _inRomMenu()) { openRomRail(); romRailSel = i; paintRomRail(); romRailActivate(); } });
      rail.appendChild(d);
    });
    var trig = $(".rom-rail-trigger", root);
    if (trig) trig.addEventListener("mouseenter", function () { if (current === "details" && _inRomMenu() && !window.BBW.isTouch() && mouseActive) openRomRail(); });
  }
  function _inRomMenu() {
    return !!(menuStack && menuStack.length > 1 && detailLevel() && detailLevel().isRomMenu);
  }
  function paintRomRail() {
    var screen = screens.details; if (!screen) return;
    var items = screen.querySelectorAll(".rom-rail .rail-item");
    var f = (_inRomMenu() && detailLevel().romFilters) || null;
    items.forEach(function (it, i) {
      var k = ROM_RAIL[i] && ROM_RAIL[i].k;
      var active = (zone === "rail" && i === romRailSel);
      var lit = f && (
        (k === "fav" && f.fav) ||
        (k === "recent" && f.recent) ||
        (k === "search" && f.query)
      );
      it.classList.toggle("active", active);
      it.classList.toggle("filtered", !!lit);
    });
  }
  function openRomRail() {
    if (!_inRomMenu()) return;
    zone = "rail"; screens.details.classList.add("rom-rail-open"); paintRomRail();
  }
  function exitRomRail() {
    zone = "list"; if (screens.details) screens.details.classList.remove("rom-rail-open"); paintRomRail();
  }
  function romRailActivate() {
    var item = ROM_RAIL[romRailSel]; if (!item || !_inRomMenu()) return;
    var lvl = detailLevel();
    if (item.k === "fav")    { lvl.romFilters.fav    = !lvl.romFilters.fav;    applyRomFilters(); exitRomRail(); }
    else if (item.k === "recent") { lvl.romFilters.recent = !lvl.romFilters.recent; applyRomFilters(); exitRomRail(); }
    else if (item.k === "clear")  { lvl.romFilters = { fav: false, recent: false, query: "" }; applyRomFilters(); exitRomRail(); }
    else if (item.k === "search") { exitRomRail(); openRomSearch(); }
    else if (item.k === "adv")    { /* zone passe à 'rom-adv' dans openRomAdv */ openRomAdv(); }
  }
  // Applique les filtres client (frame.romFilters + frame.romAdvCrit) sur la
  // liste BRUTE (frame.entries) → réécrit frame.items et re-render. La frame
  // conserve les entries brutes pour pouvoir basculer un filtre sans refetch
  // serveur. Les tags (region/language/type) sont mémoïsés sur chaque entry
  // (entry._tags = parseRomTags(fileName)) au 1er accès.
  function _romTagsOf(e) { if (!e._tags) e._tags = parseRomTags(e.fileName); return e._tags; }
  /* Client-side wildcard filter: plain query = case-insensitive substring; a
     query with * or ? = glob ("contains" semantics, * = any run, ? = one char),
     other regex metachars escaped so [!] / (USA) match literally. */
  function bbwWildcardMatch(name, query) {
    if (!query) return true;
    name = name || "";
    if (query.indexOf("*") < 0 && query.indexOf("?") < 0)
      return name.toLowerCase().indexOf(query.toLowerCase()) >= 0;
    var rx = query.replace(/[.+^${}()|[\]\\]/g, "\\$&").replace(/\*/g, ".*").replace(/\?/g, ".");
    try { return new RegExp(rx, "i").test(name); }
    catch (_) { return name.toLowerCase().indexOf(query.toLowerCase()) >= 0; }
  }
  function applyRomFilters() {
    var lvl = detailLevel(); if (!lvl || !lvl.isRomMenu) return;
    var src = lvl.entries || [];
    var f = lvl.romFilters || {};
    var c = lvl.romAdvCrit || null;
    // Aucun filtre actif (ni rail, ni avancé, ni tri) → vue « sélection
    // rapide » (1 dernière + favoris + 7 prio), identique à l'ouverture.
    var quickView = !f.fav && !f.recent && !f.query && !c;
    if (quickView) {
      var gq = DATA.games[currentGame];
      lvl.items = buildRomQuickItems(src, lvl.selRom, gq);
      lvl.items.unshift({ label: "✕ Clear", action: "select_rom", fileName: "" });
      if (lvl.sel >= lvl.items.length) lvl.sel = Math.max(0, lvl.items.length - 1);
      renderDetailMenu(0); setSelected("details", lvl.sel, { instant: true });
      return;
    }
    var sizeMinB = c ? c.sizeMin * 1024 * 1024 : 0;
    var sizeMaxB = c ? (c.sizeMax >= ROM_SIZE_B.max ? Infinity : c.sizeMax * 1024 * 1024) : Infinity;
    var out = src.filter(function (e) {
      // Filtres rapides (rail)
      if (f.fav && !e.isFavorite) return false;
      if (f.recent && !e.isLastPlayed) return false;
      if (f.query && !bbwWildcardMatch(String(e.fileName || ""), String(f.query))) return false;
      if (!c) return true;
      // Filtres avancés
      if (c.fav && !e.isFavorite) return false;
      if (c.recent && !e.isLastPlayed) return false;
      var sz = Number(e.size) || 0;
      if (sz < sizeMinB || sz > sizeMaxB) return false;
      var t = _romTagsOf(e);
      // Clauses Region + Language combinées via c.regionLanguageMode.
      // null = pas de contrainte sur ce côté (liste vide).
      var rm = null, lm = null;
      if (c.regions && c.regions.length) {
        rm = c.regionMode === "and"
          ? c.regions.every(function (x) { return t.regions.indexOf(x) >= 0; })
          : c.regions.some(function (x) { return t.regions.indexOf(x) >= 0; });
      }
      if (c.languages && c.languages.length) {
        lm = c.languageMode === "and"
          ? c.languages.every(function (x) { return t.languages.indexOf(x) >= 0; })
          : c.languages.some(function (x) { return t.languages.indexOf(x) >= 0; });
      }
      if (rm !== null && lm !== null) {
        // Les deux côtés contraints → combine via le connecteur.
        var combined = c.regionLanguageMode === "or" ? (rm || lm) : (rm && lm);
        if (!combined) return false;
      } else if (rm !== null) { if (!rm) return false; }
      else if (lm !== null)   { if (!lm) return false; }
      if (c.types && c.types.length) {
        if (!c.types.some(function (x) { return t.types.indexOf(x) >= 0; })) return false;
      }
      return true;
    });
    // Tri
    var sb = (c && c.sortBy) || "default";
    if (sb === "alpha") out = out.slice().sort(function (a, b) { return (a.fileName || "").localeCompare(b.fileName || ""); });
    else if (sb === "size-asc")  out = out.slice().sort(function (a, b) { return (a.size || 0) - (b.size || 0); });
    else if (sb === "size-desc") out = out.slice().sort(function (a, b) { return (b.size || 0) - (a.size || 0); });
    else if (sb === "fav-first")    out = out.slice().sort(function (a, b) { return (b.isFavorite ? 1 : 0) - (a.isFavorite ? 1 : 0); });
    else if (sb === "recent-first") out = out.slice().sort(function (a, b) { return (b.isLastPlayed ? 1 : 0) - (a.isLastPlayed ? 1 : 0); });
    // "default" = on garde l'ordre serveur (lastPlayed → fav → priority → alpha).
    var dup = {}; (function () { var c = {}; src.forEach(function (x) { var n = x.fileName || ""; c[n] = (c[n] || 0) + 1; if (c[n] > 1) dup[n] = 1; }); })();
    lvl.items = out.map(function (e) { return _romEntryToMenuItem(e, lvl.selRom, dup); });
    lvl.items.unshift({ label: "✕ Clear", action: "select_rom", fileName: "" });   // garde le Clear en tête après re-filtrage
    if (lvl.sel >= lvl.items.length) lvl.sel = Math.max(0, lvl.items.length - 1);
    renderDetailMenu(0); setSelected("details", lvl.sel, { instant: true });
  }

  // ── Vue POSTER (grille de jaquettes, bascule de la roue) ────────────────
  // posterMode : la roue de jeux est remplacée par une grille de jaquettes (partie principale)
  // + un panneau détail à droite. Le rail gauche reste actif. La sélection (posterSel) reste
  // synchronisée avec sel.games → l'aller-retour roue↔poster garde le jeu sélectionné.
  var posterMode = false, posterSel = 0, posterCols = 7;
  function posterEnabled() { return !window.BBW.isMobile() && (!G.posterView || G.posterView.enabled !== false); }
  function computePosterCols() {
    var grid = $(".poster-grid", screens.games); if (!grid) return posterCols;
    var pv = G.posterView || {}, cw = pv.cellWidthPx != null ? pv.cellWidthPx : 105, gap = pv.gapPx != null ? pv.gapPx : 12;
    var w = grid.clientWidth || 940;
    posterCols = Math.max(1, Math.floor((w + gap) / (cw + gap)));
    document.documentElement.style.setProperty("--bbw-poster-cols", posterCols);
    return posterCols;
  }
  // Stratégie de chargement des thumbs (poster mode) :
  // Chaque cell a un <img class="pc-img lazy" data-src="...">. La lib
  // vanilla-lazyload (vendor/lazyload.min.js) observe les cells via
  // IntersectionObserver (root: .poster-grid, threshold 1000px), pose
  // automatiquement le src à l'approche du viewport. cancel_on_exit:false
  // laisse les fetches en cours se terminer même si la cell quitte le
  // viewport — sur source locale (file:// ou HTTP local <100 ms), annuler
  // les loads provoque un re-fetch à chaque retour de cell sans jamais
  // alimenter le cache (churn réseau lors d'un browse clavier rapide).
  // Une seule instance LazyLoad par session poster ; détruite + recréée
  // au rebuild (platform switch).
  var TRANSPARENT_1PX = "data:image/gif;base64,R0lGODlhAQABAAAAACH5BAEKAAEALAAAAAABAAEAAAICTAEAOw==";
  function buildPosterGrid() {
    var grid = $(".poster-grid", screens.games); if (!grid) return;
    var scroll = $(".poster-scroll", grid); if (!scroll) return;
    scroll.innerHTML = ""; grid.scrollTop = 0;
    // Mode fluid (posterView.fluid:true) : .pc-img est un <img> avec
    // height:auto + min-height carré ; en mode fixe c'est aussi un <img>
    // mais hauteur figée par CSS (object-fit:cover) — un seul tag pour
    // simplifier le DOM + la lazy-load (im.src au lieu de backgroundImage).
    // La classe .fluid est portée par .poster-grid (toggle par
    // applyConfigCss), pas par chaque cellule → bascule dynamique sans
    // rebuild quand l'utilisateur coche/décoche dans les Settings.
    // GIF transparent 1×1, base64. Posé en src DEFAULT sur toutes les
    // .pc-img → l'<img> a toujours un "vrai" contenu (transparent), évite
    // que certains User-Agent stylesheets dessinent un border/indicator
    // UA-default pour les <img> avec src absent (bordure 1-2px claire
    // observée sur certaines versions d'Edge sur des cellules empty).
    DATA.games.forEach(function (g, i) {
      var cell = document.createElement("div");
      // Pas de thumb pour ce jeu → cellule "phantom" : un rectangle gris
      // qui mime un poster manquant (cf. LaunchBox desktop). La classe
      // .empty active le fond plus visible + le flex-grow de l'img pour
      // que le rectangle prenne la hauteur de la ligne (= image la plus
      // grande du grid row) en mode fluid.
      cell.className = "poster-cell" + (g.thumb ? "" : " empty");
      cell.dataset.i = i;
      var img = document.createElement("img");
      img.className = "pc-img lazy";
      img.alt = "";
      img.src = TRANSPARENT_1PX;    // placeholder, real URL via data-src
      if (g.thumb) img.dataset.src = g.thumb;
      img.decoding = "async";
      var t = document.createElement("div"); t.className = "pc-title"; t.textContent = g.t || "";
      var d = document.createElement("div"); d.className = "pc-dev"; d.textContent = g.dev || "";
      cell.appendChild(img); cell.appendChild(t); cell.appendChild(d);
      // Interaction souris (look LaunchBox desktop) :
      //   - hover : RIEN — ne change pas la sélection (le hover CSS pose
      //     juste un léger highlight pour signaler la cliquabilité).
      //   - clic sur une cellule NON-sélectionnée : sélectionne seulement.
      //   - clic sur la cellule DÉJÀ sélectionnée : navigue vers la fiche.
      //   - double-clic : navigue, peu importe la sélection précédente.
      cell.addEventListener("click", function () {
        if (!posterMode) return;
        if (posterSel === i) descend();
        else posterSelect(i);
      });
      cell.addEventListener("dblclick", function () {
        if (!posterMode) return;
        if (posterSel !== i) posterSelect(i);
        descend();
      });
      scroll.appendChild(cell);
    });
    computePosterCols();
    if (posterSel >= DATA.games.length) posterSel = Math.max(0, DATA.games.length - 1);
    paintPoster(true);
    if (window._posterLazyLoad) {
      try { window._posterLazyLoad.destroy(); } catch (_) {}
      window._posterLazyLoad = null;
    }
    window._posterLazyLoad = new LazyLoad({
      container: grid,
      elements_selector: ".pc-img.lazy",
      // Local source : on laisse les loads finir même si la cell sort du viewport.
      // Sinon, un browse clavier rapide annule chaque load avant complétion →
      // les thumbs ne sont jamais cachées et chaque retour de cell re-fetch.
      cancel_on_exit: false,
      use_native: false,
      // Élargi à 1000 px pour précharger plus aggresivement (cf. local source rapide).
      threshold: 1000
    });
  }
  function paintPoster(instant) {
    var grid = $(".poster-grid", screens.games); if (!grid) return;
    var scroll = $(".poster-scroll", grid), cells = scroll.children;
    for (var i = 0; i < cells.length; i++) cells[i].classList.toggle("selected", i === posterSel);
    var cell = cells[posterSel];
    if (cell) {
      // défilement NATIF : on pose scrollTop pour garder la sélection visible (le swipe tactile
      // utilise le même scroll). instant → sans animation (build) ; sinon scroll-behavior smooth.
      var viewH = grid.clientHeight, top = cell.offsetTop, bot = top + cell.offsetHeight, cur = grid.scrollTop;
      if (top < cur) cur = top;
      else if (bot > cur + viewH) cur = bot - viewH;
      if (cur !== grid.scrollTop) {
        if (instant) { var prev = grid.style.scrollBehavior; grid.style.scrollBehavior = "auto"; grid.scrollTop = cur; grid.style.scrollBehavior = prev; }
        else grid.scrollTop = cur;
      }
    }
  }
  function posterSelect(i) {
    var n = DATA.games.length; if (n <= 0) return;
    i = Math.max(0, Math.min(n - 1, i));
    posterSel = i; sel.games = i;            // synchro avec la roue
    paintPoster();
    var pg = $(".page", screens.games); if (pg) pg.textContent = (i + 1) + " / " + DATA.platformTotal;
    cancelGameContentLoads();                // coupe le détail précédent (token)
    currentGame = i; posterMediaIdx = 0;
    fillPosterSide(i);                       // panneau latéral (infos, dispo immédiatement)
    fillPosterMedia(i);                      // média : repli vignette ; vidéo/captures après le détail
    schedulePosterTint(DATA.games[i]);       // voile coloré sous la grille (debounce 500 ms)
    scheduleHeavy(i);                        // charge le détail (vidéo/captures…) après le palier
  }
  function posterMove(delta) { posterSelect(posterSel + delta); }
  function togglePoster() {
    if (!posterEnabled()) return;
    posterMode = !posterMode;
    screens.games.classList.toggle("poster", posterMode);
    // Persiste la préférence de vue (poster vs roue) dans le cookie bbw_cfg,
    // exactement comme lists.widthPx est persisté par le splitter.
    if (window.BBW && window.BBW.cfg) { try { window.BBW.cfg.set("posterView.startInPosterMode", posterMode); } catch (_) {} }
    if (posterMode) {
      if (zone === "rail") exitRail();
      zone = "list"; posterSel = sel.games || 0;
      buildPosterGrid();
      posterSelect(posterSel);
    } else {
      // retour roue : repositionne la roue sur la sélection courante + repose le panneau classique
      setSelected("games", sel.games || 0, { instant: true });
    }
    // Resync de la scrollbar custom du mode poster (no-op si non-montée,
    // ex: config off ou pas en desktop). Le swap wheel↔poster fait sauter
    // scrollHeight de 0 à plusieurs milliers → il faut repeindre le thumb.
    if (window.BBW && window.BBW.updatePosterScrollbar) {
      try { window.BBW.updatePosterScrollbar(); } catch (_) {}
    }
  }
  // Note (étoiles) façon LaunchBox : 5 étoiles, remplies à l'arrondi de la note.
  function posterStars(v) {
    var full = Math.round(v), s = "";
    for (var i = 1; i <= 5; i++) s += '<span class="ps-star' + (i <= full ? " f" : "") + '">★</span>';
    return s;
  }
  // Debounce du chargement du fanart hero (panneau droit) : on tire+pose
  // le fanart APRÈS le dernier appel à fillPosterSide (délai paramétrable
  // par posterView.heroFanartDelayMs, défaut 500 ms). Si une autre
  // sélection arrive avant l'échéance, on annule et on reprogramme — un
  // scroll rapide ne déclenche donc aucun chargement intermédiaire, ce qui
  // évite le coût réseau/disque + le clignotement à chaque touche.
  var posterFanartTimer    = null;   // fade-in (debounced, annulé+rescheduled à chaque selection)
  var posterFanartOutTimer = null;   // fade-out (schedule once, NON cancellable)
  var heroFanartActive     = 0;      // index (0|1) de la couche actuellement .on
  function schedulePosterFanart(g) {
    if (posterFanartTimer) { clearTimeout(posterFanartTimer); posterFanartTimer = null; }
    if (!g) return;
    var pv = G.posterView || {};

    // Fade-out timer : schedule UNE FOIS — pas reseté quand schedulePosterFanart
    // est rappelé (scroll continu). Permet à l'ancien fanart de commencer
    // à s'effacer N ms après la première deselection, sans attendre que
    // l'utilisateur s'arrête. activeIdx est capturé au moment du schedule ;
    // si fade-in fire entre temps (impossible avec mêmes delays), no-op.
    if (posterFanartOutTimer == null) {
      var activeIdx = heroFanartActive;
      var outDelayMs = (pv.heroFanartFadeOutDelayMs != null) ? pv.heroFanartFadeOutDelayMs : 500;
      posterFanartOutTimer = setTimeout(function () {
        posterFanartOutTimer = null;
        var sideX = $(".poster-side", screens.games); if (!sideX) return;
        var bgRootX = $(".ps-hero-bg", sideX); if (!bgRootX) return;
        var layersX = bgRootX.querySelectorAll(".ps-hero-bg-layer");
        if (layersX.length >= 2) layersX[activeIdx].classList.remove("on");
      }, outDelayMs);
    }

    var delayMs = (pv.heroFanartDelayMs != null) ? pv.heroFanartDelayMs : 500;
    posterFanartTimer = setTimeout(function () {
      posterFanartTimer = null;
      var side = $(".poster-side", screens.games); if (!side) return;
      var hero = $(".ps-hero", side), bgRoot = $(".ps-hero-bg", side);
      if (!hero || !bgRoot) return;
      var layers = bgRoot.querySelectorAll(".ps-hero-bg-layer");
      if (layers.length < 2) return;
      var fa = pickFanart(g);
      var nextIdx   = 1 - heroFanartActive;
      var nextLayer = layers[nextIdx];
      var prevLayer = layers[heroFanartActive];
      if (fa) {
        // Pose la nouvelle image sur la couche réserve (invisible),
        // puis ajoute .on → fade-in via --bbw-hero-fanart-fade-in.
        // Le fade-out de prevLayer est DÉJÀ en cours (déclenché en
        // immédiat plus haut) → recouvrement asymétrique propre.
        nextLayer.style.backgroundImage = 'url("' + String(fa).replace(/"/g, "%22") + '")';
        nextLayer.classList.add("on");
        prevLayer.classList.remove("on");   // idempotent (déjà retiré en immédiat)
        hero.classList.add("has-fanart");
        heroFanartActive = nextIdx;
      } else {
        // Pas de fanart disponible pour ce jeu : on confirme le fade-out.
        nextLayer.classList.remove("on");
        prevLayer.classList.remove("on");
        hero.classList.remove("has-fanart");
      }
    }, delayMs);
  }

  // Voile teinte ambiente derrière la grille — crossfade entre deux
  // couches pour donner un effet "morphing" entre les jeux (la lib
  // floue lourde rend les variations de couleur perceptibles comme un
  // glissement plutôt qu'un swap dur).
  //
  // Pattern : posterTintActive indexe la couche actuellement .on
  // (opacité cible). À chaque update, on dépose la nouvelle image sur
  // la couche RÉSERVE (l'autre), on lui ajoute .on (fade-in) et on
  // retire .on de la couche active (fade-out). Les deux transitions
  // CSS jouent en simultané = crossfade. On bascule ensuite l'index.
  var posterTintTimer  = null;
  var posterTintActive = 0;   // index (0 ou 1) de la couche actuellement .on
  function schedulePosterTint(g) {
    if (posterTintTimer) { clearTimeout(posterTintTimer); posterTintTimer = null; }
    if (!g) return;
    var pv = G.posterView || {};
    if (pv.gridTintEnabled === false) {
      // Désactivé : nettoie immédiatement les deux couches.
      var tintRoot = $(".poster-tint", screens.games);
      if (tintRoot) {
        var lyrs = tintRoot.querySelectorAll(".poster-tint-layer");
        for (var k = 0; k < lyrs.length; k++) {
          lyrs[k].classList.remove("on");
          lyrs[k].style.backgroundImage = "";
        }
      }
      return;
    }
    var delayMs = (pv.gridTintDelayMs != null) ? pv.gridTintDelayMs : 500;
    posterTintTimer = setTimeout(function () {
      posterTintTimer = null;
      var tint = $(".poster-tint", screens.games); if (!tint) return;
      var layers = tint.querySelectorAll(".poster-tint-layer");
      if (layers.length < 2) return;
      var src = g.thumb || g.shotThumb || g.boxImg;
      var nextIdx   = 1 - posterTintActive;
      var nextLayer = layers[nextIdx];
      var prevLayer = layers[posterTintActive];
      if (src) {
        // Pose l'image sur la couche réserve (qui est invisible).
        nextLayer.style.backgroundImage = 'url("' + String(src).replace(/"/g, "%22") + '")';
        // Démarre crossfade : on monte le nouveau, on baisse l'ancien.
        nextLayer.classList.add("on");
        prevLayer.classList.remove("on");
        posterTintActive = nextIdx;
      } else {
        // Pas de source dispo : fade-out des deux couches (cas rare).
        nextLayer.classList.remove("on");
        prevLayer.classList.remove("on");
      }
    }, delayMs);
  }

  // Panneau détail simplifié : clear logo (option), plateforme, titre, note, infos.
  // Le média (vidéo/captures) est posé séparément (fillPosterMedia, Phase 3).
  function fillPosterSide(gi) {
    var g = DATA.games[gi]; if (!g) return;
    var side = $(".poster-side", screens.games); if (!side) return;
    // Hero (haut du panneau) : fanart en filigrane derrière le clear logo.
    // L'application réelle (set backgroundImage + toggle .has-fanart) est
    // RETARDÉE de 500 ms par schedulePosterFanart → si l'utilisateur navigue
    // vite entre jeux on ne charge pas chaque fanart au passage. Le fanart
    // précédent reste visible pendant l'attente (pas de flash noir), puis
    // fade-in 1 s (transition CSS) sur le nouveau.
    schedulePosterFanart(g);
    // Clear logo (au-dessus du fanart). Si le jeu n'a pas de logo et que
    // l'option est active, on affiche le titre en texte dans le même slot
    // avec la même animation ps-logo-pulse. Le nœud est recréé à chaque
    // changement de jeu (innerHTML = "") → la timeline redémarre exactement
    // comme pour le chemin image.
    var logoEl = $(".ps-logo", side);
    if (logoEl) {
      logoEl.innerHTML = "";
      if (G.gameLogo && G.gameLogo.enabled !== false) {
        if (g.logo) {
          var im = document.createElement("img"); im.src = g.logo; im.alt = ""; logoEl.appendChild(im);
        } else {
          var tx = document.createElement("span"); tx.className = "ps-logo-text"; tx.textContent = g.t || ""; logoEl.appendChild(tx);
        }
      }
    }
    var plat = $(".ps-plat", side); if (plat) plat.textContent = (DATA.platform || "").toUpperCase();
    var title = $(".ps-title", side); if (title) title.textContent = g.t || "";
    var rat = $(".ps-rating", side);
    if (rat) paintRating(rat, gi);
    var fav = $(".ps-fav", side);
    if (fav) {
      // L'icône favori est TOUJOURS visible en mode poster afin que
      // l'état courant (cœur plein/vide) reste lisible même en mode
      // lock-children. Seul le clic est conditionné par canFavGame() :
      // quand le contrôle parental l'interdit, le bouton est rendu
      // non-interactif (pointer-events:none + opacité réduite) sans
      // disparaître. Quand il est autorisé, l'apparence et le handler
      // sont rétablis normalement.
      fav.style.display = "";
      fav.classList.toggle("on", !!g.fav);
      if (!canFavGame()) {
        // Visible mais non-cliquable en mode verrouillé.
        fav.classList.add("ps-fav-locked");
        fav.onclick = null;
      } else {
        fav.classList.remove("ps-fav-locked");
        // onclick (et pas addEventListener) → l'attribution réécrase la
        // précédente : pas d'accumulation entre les appels successifs de
        // fillPosterSide quand l'utilisateur change de jeu.
        fav.onclick = function (e) { e.stopPropagation(); togglePosterFavorite(gi); };
      }
    }
    // Store install badge (poster mode): round store logo + ring (green=installed,
    // orange=not). g.store / g.installed come straight from the list payload.
    var inst = $(".ps-install", side);
    if (inst) {
      if (g.store) {
        var psImg = (g.store === "Epic") ? "EpicGames" : (g.store === "Ubisoft") ? "Uplay" : g.store;   // GOG / Steam / EpicGames / Uplay
        inst.className = "ps-install store-badge " + (g.installed === false ? "notinstalled" : "installed");
        inst.title = (g.installed === false ? "Not installed" : "Installed") + " — " + g.store;
        inst.innerHTML = '<img src="/api/badges/' + encodeURIComponent(psImg) + '.png" alt="' + g.store + '" onerror="this.style.display=\'none\'">';
        inst.style.display = "";
      } else {
        inst.className = "ps-install";
        inst.style.display = "none";
        inst.innerHTML = "";
      }
    }
    var info = $(".ps-info", side);
    if (info) {
      info.innerHTML = "";
      var rows = [];
      if (g.y)    rows.push([tA("info.released"),  g.y]);
      if (g.dev)  rows.push([tA("info.developer"), g.dev]);
      if (g.pub)  rows.push([tA("info.publisher"), g.pub]);
      var rg = realGenres(g.g); if (rg) rows.push([tA("info.genre"), rg]);
      if (g.esrb) rows.push([tA("info.esrb"),      g.esrb]);
      rows.forEach(function (r) {
        var row = document.createElement("div"); row.className = "ps-row";
        var k = document.createElement("span"); k.className = "ps-k"; k.textContent = r[0];
        var v = document.createElement("span"); v.className = "ps-v"; v.textContent = r[1];
        row.appendChild(k); row.appendChild(v); info.appendChild(row);
      });
    }
  }

  // Note interactive du panneau droit : reconstruit le DOM (rnum + 5 étoiles
  // + tooltip), gère le hover-preview (les étoiles se remplissent en suivant
  // la souris) et le clic-pour-valider (postMutation "rating"). La classe
  // .user passée à .ps-rating bascule la teinte des étoiles du cyan
  // (communauté) au jaune (note du joueur).
  function paintRating(rat, gi) {
    var g = DATA.games[gi]; if (!g) return;
    var hasUser = (userRatings[gi] && userRatings[gi] > 0) || (g.ur != null && g.ur > 0);
    var userVal = hasUser ? (userRatings[gi] || g.ur) : 0;
    var commVal = parseFloat(g.r) || 0;
    var rv = hasUser ? userVal : commVal;
    rat.classList.toggle("user", hasUser);
    // Build : <ps-rnum><ps-stars><ps-rtooltip>
    var rounded = Math.round(rv);
    var html = '<span class="ps-rnum">' + (rv > 0 ? rv.toFixed(1) : "—") + "</span>";
    html += '<span class="ps-stars">';
    for (var i = 1; i <= 5; i++) {
      html += '<span class="ps-star' + (i <= rounded ? " f" : "") + '" data-v="' + i + '">★</span>';
    }
    html += "</span>";
    // Tooltip détail : TOUJOURS Your en 1re ligne (valeur ou "None"), puis
    // Community + Votes. Cohérent avec le fait que la box est cliquable —
    // l'utilisateur voit immédiatement quelle note il a posée (s'il en a posée).
    var tipLines = [];
    tipLines.push(tA("rating.your") + ": " + (hasUser ? Math.round(userVal) : tA("rating.none")));
    tipLines.push(tA("rating.community") + ": " + commVal.toFixed(2));
    var votes = communityVotes(gi);
    if (votes != null) tipLines.push(tA("rating.votes") + ": " + Number(votes).toLocaleString());
    html += '<div class="ps-rtooltip">' + tipLines.map(function (l) { return "<div>" + l + "</div>"; }).join("") + "</div>";
    rat.innerHTML = html;
    var stars = rat.querySelectorAll(".ps-star");
    // Interactivité conditionnée par le contrôle parental : si l'utilisateur
    // est verrouillé et que l'option AllowLockedUserToModifyRatings est
    // désactivée, on N'ATTACHE PAS les handlers hover/click → les étoiles
    // ne bougent plus au hover et le clic ne fait rien. Le tooltip et la
    // peinture statique restent (la note reste lisible, juste pas modifiable).
    // La classe .ps-rating reçoit .readonly pour permettre au CSS de
    // changer le cursor (default au lieu de pointer).
    if (!canRateGame()) {
      rat.classList.add("readonly");
      return;
    }
    rat.classList.remove("readonly");
    stars.forEach(function (s) {
      s.addEventListener("mouseenter", function () {
        var v = parseInt(s.dataset.v, 10);
        for (var j = 0; j < stars.length; j++) {
          stars[j].classList.toggle("preview", (j + 1) <= v);
          stars[j].classList.remove("f");        // override pendant le preview
        }
      });
      s.addEventListener("click", function (e) {
        e.stopPropagation();
        var v = parseInt(s.dataset.v, 10);
        commitPosterRating(gi, v);
      });
    });
    // Sortie souris : restaure la peinture "réelle" (rounded de rv).
    // onmouseleave (et pas addEventListener) → écrase la version précédente,
    // pas d'accumulation entre les appels de paintRating sur le même .ps-rating.
    rat.onmouseleave = function () {
      for (var j = 0; j < stars.length; j++) {
        stars[j].classList.remove("preview");
        stars[j].classList.toggle("f", (j + 1) <= rounded);
      }
    };
  }

  // Valide une note utilisateur : update local (userRatings + g.ur) puis
  // mutation serveur, et repeint la boîte note pour passer cyan → jaune.
  function commitPosterRating(gi, value) {
    var g = DATA.games[gi]; if (!g) return;
    userRatings[gi] = value;
    g.ur = value;
    postMutation("rating", { value: value });
    var rat = $(".ps-rating", $(".poster-side", screens.games));
    if (rat) paintRating(rat, gi);
  }

  // Bascule favori : update local g.fav + classe .on, puis mutation serveur.
  function togglePosterFavorite(gi) {
    var g = DATA.games[gi]; if (!g) return;
    g.fav = !g.fav;
    var fav = $(".ps-fav", $(".poster-side", screens.games));
    if (fav) fav.classList.toggle("on", !!g.fav);
    postMutation("favorite", { value: !!g.fav });
  }

  // Média du panneau poster : grand média (vidéo lue auto OU capture) + bande de vignettes
  // cliquables. Avant le chargement du détail (vidéo/captures), repli sur la vignette dégradée.
  var posterMediaIdx = 0;
  function fillPosterMedia(gi) {
    var g = DATA.games[gi]; if (!g) return;
    var side = $(".poster-side", screens.games); if (!side) return;
    var media = $(".ps-media .rgn-inner", side), thumbs = $(".ps-thumbs", side);
    if (!media) return;
    abortCurrentVideo();
    media.innerHTML = ""; if (thumbs) thumbs.innerHTML = "";
    var items = buildMediaItems(g);
    if (!items.length) {   // détail pas (encore) chargé → vignette dégradée
      var src0 = g.shotThumb || g.thumb;
      if (src0) { var im0 = document.createElement("img"); im0.className = "ps-mimg"; im0.src = src0; media.appendChild(im0); }
      return;
    }
    if (posterMediaIdx >= items.length) posterMediaIdx = 0;
    var main = items[posterMediaIdx];
    if (main.kind === "video") {
      var v = document.createElement("video");
      v.src = toFull(main.src); v.autoplay = true; v.loop = false; v.playsInline = true;
      v.controls = true; v.setAttribute("controlsList", "nodownload"); v.muted = !audioOn;
      if (main.poster) v.poster = main.poster;
      media.appendChild(v); currentVideoEl = v;
      var pr = v.play(); if (pr && pr.catch) pr.catch(function () { v.muted = true; v.play().catch(function () {}); });
    } else {
      var im = document.createElement("img"); im.className = "ps-mimg"; im.src = toFull(main.src); media.appendChild(im);
    }
    if (thumbs && items.length > 1) {
      items.forEach(function (it, j) {
        var c = document.createElement("div");
        c.className = "ps-thumb" + (j === posterMediaIdx ? " on" : "") + (it.kind === "video" ? " is-video" : "");
        // Intentional: thumbnails use native loading="lazy" (they are in the visible viewport and outside .poster-grid), not the LazyLoad instance which scopes to .poster-grid cells only.
        var tim = document.createElement("img"); tim.src = it.poster || it.src; tim.alt = ""; tim.loading = "lazy"; c.appendChild(tim);
        if (it.kind === "video") { var b = document.createElement("span"); b.className = "ps-vbadge"; b.textContent = "▶"; c.appendChild(b); }
        c.addEventListener("click", function () { posterMediaIdx = j; fillPosterMedia(gi); });
        thumbs.appendChild(c);
      });
    }
  }
  // Navigation entre médias du panneau poster (L/R gâchettes · Tab clavier).
  function posterMediaCycle(dir) {
    var g = DATA.games[currentGame]; if (!g) return;
    var items = buildMediaItems(g); if (items.length <= 1) return;
    posterMediaIdx = (posterMediaIdx + dir + items.length) % items.length;
    fillPosterMedia(currentGame);
  }
  function findGameByInitial(ch) {
    for (var i = 0; i < DATA.games.length; i++) {
      var c = gameCN(DATA.games[i]).charAt(0);   // initiale du COMPARE NAME (déjà en MAJ)
      if (ch === "#") { if (c >= "0" && c <= "9") return i; }
      else if (c === ch) return i;
    }
    return -1;
  }
  // Compare name d'un titre : réplique JS des règles de Normalizer.PerformSanitize (passe
  // « loose » du plugin). Retire le contenu entre () [] {}, mappe la ponctuation, convertit
  // les chiffres romains II..VIII, SUPPRIME les articles a/an/and/the, compacte et passe en
  // MAJ. Ainsi « The Legend of Zelda » → « LEGEND OF ZELDA » → trié/sauté sous la lettre L.
  function compareName(s) {
    s = (s || "")
      .replace(/\([^)]*\)/g, " ").replace(/\[[^\]]*\]/g, " ").replace(/\{[^}]*\}/g, " ")  // () [] {}
      .replace(/[-:&!,/\\?]/g, " ")                                                       // ponctuation → espace
      .replace(/['".]/g, "")                                                              // ' " . → vide
      .replace(/\s{2,}/g, " ").trim()
      .replace(/\bII\b/g, "2").replace(/\bIII\b/g, "3").replace(/\bIV\b/g, "4")           // chiffres romains
      .replace(/\bVIII\b/g, "8").replace(/\bVII\b/g, "7").replace(/\bVI\b/g, "6").replace(/\bV\b/g, "5")
      .replace(/\b(?:a|an|and|the)\b/gi, " ")                                             // articles
      .replace(/\s{2,}/g, " ").trim().toUpperCase();
    return s;
  }
  // compareName MÉMOÏSÉ par jeu (g._cn) : calculé une seule fois. Les objets jeu viennent du
  // cache de BBW.get (games.json sans TTL) → la même référence est réutilisée en revisitant la
  // plateforme, donc _cn (et le tri) survivent sans recalcul.
  function gameCN(g) { if (g && g._cn == null) g._cn = compareName(g && g.t); return (g && g._cn) || ""; }
  // Tri de la liste de jeux par COMPARE NAME (cf. compareName ci-dessus), uniforme quelle que
  // soit la source (Owned / ExtendedDb / dummy). numeric = « Jeu 2 » avant « Jeu 10 ». Garantit
  // que le saut par lettre (findGameByInitial) tombe sur le PREMIER jeu de la lettre. En place,
  // et MÉMOÏSÉ via arr._sorted : la liste ne bouge pas → on ne la retrie pas à chaque visite.
  function sortGamesAlpha(arr) {
    if (arr && arr.length > 1 && !arr._sorted) arr.sort(function (a, b) {
      return gameCN(a).localeCompare(gameCN(b), undefined, { numeric: true });
    });
    if (arr) arr._sorted = true;
    return arr;
  }

  // Filtre "bibliothèque" appliqué à la liste reçue (drapeaux par jeu de games.json). Retire
  // les jeux cassés si config.library.showBroken=false (les jeux « Hide » de LB sont déjà
  // exclus côté serveur). Les favoris ne sont PAS filtrés, juste marqués (cf. markFavorites).
  function applyLibraryFilter(arr) {
    var lib = G.library || {};
    if (lib.showBroken === false) arr = arr.filter(function (g) { return !(g && g.broken); });
    return arr;
  }
  // Étoile gauche par item : FAVORI (.fav → or pulsant, prioritaire) sinon PALIER QUALITÉ
  // (.tier1/2/3 → bronze/argent/or, depuis gameStars rempli par loadPlatformStars). Une seule
  // étoile affichée (le CSS donne la priorité au favori). Rejoué après chaque (re)build de liste.
  function markGameStars(screenId) {
    var lib = G.library || {};
    var favOn = lib.favoriteStar !== false, qOn = lib.qualityStars !== false;
    var items = screens[screenId].querySelectorAll(".list .list-item");
    for (var i = 0; i < items.length; i++) {
      var g = DATA.games[i]; if (!g) continue;
      if (favOn && g.fav) items[i].classList.add("fav");
      if (qOn && g.dbId != null) { var t = gameStars[g.dbId]; if (t) items[i].classList.add("tier" + t); }
    }
  }
  // Charge les paliers qualité (stars.json, PLATEFORMES uniquement) en arrière-plan (non bloquant),
  // puis les applique. Cache client (BBW.get) + cache serveur. Playlists/catégories → aucun palier.
  function loadPlatformStars(path) {
    gameStars = {};
    if (!(G.library && G.library.qualityStars !== false)) return;
    if (!path || path.indexOf("platforms/") !== 0) return;   // plateformes seulement
    window.BBW.get("data/" + path + "/stars.json").then(function (m) {
      gameStars = m || {};
      if (current === "games") {
        if (favOnly) applyFavFilter(true);   // filtre actif → ré-inclut les trophées maintenant chargés
        else markGameStars("games");          // sinon applique juste les marqueurs
      }
    });
  }
  // Un jeu est "marqué" s'il est favori OU s'il a un palier qualité (trophée) — sert au filtre du rail.
  function isStarred(g) {
    var lib = G.library || {};
    if (lib.favoriteStar !== false && g && g.fav) return true;
    if (lib.qualityStars !== false && g && g.dbId != null && gameStars[g.dbId]) return true;
    return false;
  }
  // Filtre du rail ★ : ne garde que les jeux MARQUÉS (favoris + trophées). Reconstruit la liste
  // depuis gamesAll. TRANSITOIRE : levé par clearFavFilter (retour liste depuis fiche / catégories).
  function applyFavFilter(on) {
    favOnly = on;
    DATA.games = on ? DATA.gamesAll.filter(isStarred) : DATA.gamesAll;
    DATA.platformTotal = DATA.games.length;
    setupList("games", DATA.games.map(function (g) { return g.t; })); markGameStars("games");
    shownGame = -1; setSelected("games", 0, { instant: true });
    // BUG #1 FIX: poster side panel only refreshes via posterSelect(); setSelected alone is not enough.
    if (posterMode) posterSelect(0);
  }
  // Lève le filtre favoris et restaure la liste complète. keepViewedGame=true → resélectionne
  // le jeu qu'on regardait (retour de fiche) ; sinon revient en tête.
  function clearFavFilter(keepViewedGame) {
    if (!favOnly) return;
    var g = keepViewedGame ? DATA.games[currentGame] : null;
    favOnly = false;
    DATA.games = DATA.gamesAll; DATA.platformTotal = DATA.platformTotalAll;
    setupList("games", DATA.games.map(function (g) { return g.t; })); markGameStars("games");
    var idx = g ? DATA.games.indexOf(g) : 0; if (idx < 0) idx = 0;
    shownGame = -1; setSelected("games", idx, { instant: true });
    // BUG #1 FIX: poster side panel only refreshes via posterSelect(); setSelected alone is not enough.
    if (posterMode) posterSelect(idx);
  }

  // ── Recherche AVANCÉE (rail ☰) : modale à onglets, filtrage client transitoire ─────────
  // Onglets aux gâchettes L/R (mediaPrev/Next + ArrowRight/Shift+ArrowRight) avec BOUCLAGE ; Haut/Bas = focus, Gauche/
  // Droite = ajuste, A = active, B = ferme. Dimensions : Général (année, note, type de sortie,
  // flags) · Genre (multi + OU/ET) · Éditeur-Dév (auto-complétion clavier) · Historique (10).
  // Filtrage CLIENT sur DATA.gamesAll ; cycle de vie transitoire (cf. navTo). Critères stockés en
  // forme NORMALISÉE (seuls les filtres réglés) → libellés simples + ré-application inter-plateforme.
  // Bornes FIXES avec « No-Limit » (∞) aux deux extrémités. Année : premier cran 1950, dernier
  // cran = année courante + 1 ; la position min = 1949 (∞ bas) et max = (année+1)+1 (∞ haut).
  // Note : 0..5 réels, ∞ à -0.5 / 5.5. Par défaut les deux poignées sont sur ∞ → aucun filtre.
  var YEAR_B = (function () { var hi = new Date().getFullYear() + 1; return { min: 1949, max: hi + 1, step: 1, fmt: function (v) { return (v <= 1949 || v >= hi + 1) ? "∞" : ("" + v); } }; })();
  var RATING_B = { min: -0.5, max: 5.5, step: 0.5, fmt: function (v) { return (v <= -0.5 || v >= 5.5) ? "∞" : v.toFixed(1); } };
  var advFacets = { genres: [], publishers: [], developers: [], releaseTypes: [] };
  var ADV_HIST_KEY = "bbw.advHistory", ADV_HIST_MAX = 10;
  function tA(k) { return window.BBW.t(k); }

  function computeAdvFacets() {
    var gs = {}, dp = {}, dv = {}, rt = {};   // dp=éditeurs(publishers), dv=développeurs
    DATA.gamesAll.forEach(function (g) {
      if (g && g.g) g.g.split(";").forEach(function (x) { x = x.trim(); if (x) gs[x] = 1; });
      if (g && g.pub) dp[g.pub.trim()] = 1;
      if (g && g.dev) dv[g.dev.trim()] = 1;
      if (g && g.rt) rt[g.rt.trim()] = 1;
    });
    var ci = function (a, b) { a = a.toLowerCase(); b = b.toLowerCase(); return a < b ? -1 : a > b ? 1 : 0; };
    advFacets = { genres: Object.keys(gs).sort(ci), publishers: Object.keys(dp).sort(ci), developers: Object.keys(dv).sort(ci), releaseTypes: Object.keys(rt).sort(ci) };
  }
  function defaultCrit() {
    return { yearMin: YEAR_B.min, yearMax: YEAR_B.max, ratingMin: RATING_B.min, ratingMax: RATING_B.max,
             releaseType: "", flagFav: false, flagInstalled: false, genres: [], genreMode: "or", publisher: "", developer: "", sortBy: "alpha" };
  }

  function setupAdvanced() {
    advModalEl = $('[data-modal="adv"]'); if (!advModalEl) return;
    advModalEl.addEventListener("click", function (e) {
      if (!advOpen) return;
      var tab = e.target.closest && e.target.closest(".adv-tab");
      if (tab) { advTab = +tab.dataset.i; advFocus = 0; renderAdv(); return; }
      var el = e.target.closest && e.target.closest("[data-advi]");
      if (el && mouseActive) { advFocus = +el.dataset.advi; paintAdv(); advActivate(); return; }
      if (!(e.target.closest && e.target.closest(".adv-panel"))) closeAdvanced();
    });
  }
  function openAdvanced() {
    if (!advModalEl || current !== "games") return;
    computeAdvFacets();
    if (!advCrit) advCrit = defaultCrit();
    advOpen = true; advTab = 0; advFocus = 0;
    renderAdv();
    advModalEl.classList.add("open");
  }
  function closeAdvanced() { advOpen = false; if (advModalEl) advModalEl.classList.remove("open"); zone = "list"; paintRail(); }

  function renderAdv() {
    if (!advModalEl) return;
    var tabsEl = $(".adv-tabs", advModalEl); tabsEl.innerHTML = "";
    ADV_TABS.forEach(function (id, i) {
      var t = document.createElement("div"); t.className = "adv-tab" + (i === advTab ? " active" : "");
      t.dataset.i = i; t.textContent = tA("adv." + id); tabsEl.appendChild(t);
    });
    var body = $(".adv-body", advModalEl); body.innerHTML = ""; advTargets = [];
    var id = ADV_TABS[advTab];
    if (id === "general") renderAdvGeneral(body);
    else if (id === "genre") renderAdvGenre(body);
    else if (id === "publisher" || id === "developer") renderAdvText(body, id);
    else if (id === "orderby") renderAdvOrderby(body);
    else if (id === "history") renderAdvHistory(body);
    var applyEl = $(".adv-apply", advModalEl);
    applyEl.style.display = (id === "history") ? "none" : "";
    if (id !== "history") { applyEl.textContent = tA("adv.apply"); applyEl.dataset.advi = advTargets.length; advTargets.push({ type: "apply", el: applyEl }); }
    if (advFocus >= advTargets.length) advFocus = Math.max(0, advTargets.length - 1);
    paintAdv();
  }
  // ── Constructeurs de contrôles (chaque helper ajoute le DOM + ses cibles focus) ──
  function addSlider(body, kind, label, bounds) {
    var wrap = document.createElement("div"); wrap.className = "adv-row";
    wrap.innerHTML = '<div class="adv-row-label"></div>' +
      '<div class="adv-track"><div class="adv-range"></div><div class="adv-handle lo"></div><div class="adv-handle hi"></div></div>';
    $(".adv-row-label", wrap).dataset.base = label;
    body.appendChild(wrap);
    var loI = advTargets.length;
    advTargets.push({ type: "slider", kind: kind, which: "lo", el: $(".adv-handle.lo", wrap), wrap: wrap, bounds: bounds });
    advTargets.push({ type: "slider", kind: kind, which: "hi", el: $(".adv-handle.hi", wrap), wrap: wrap, bounds: bounds });
    $(".adv-handle.lo", wrap).dataset.advi = loI; $(".adv-handle.hi", wrap).dataset.advi = loI + 1;
    paintSlider(advTargets[loI]);
  }
  function paintSlider(t) {
    var b = t.bounds, span = Math.max(b.step, b.max - b.min);
    var lo = advCrit[t.kind + "Min"], hi = advCrit[t.kind + "Max"];
    var loP = (lo - b.min) / span * 100, hiP = (hi - b.min) / span * 100;
    $(".adv-handle.lo", t.wrap).style.left = loP + "%"; $(".adv-handle.hi", t.wrap).style.left = hiP + "%";
    var r = $(".adv-range", t.wrap); r.style.left = loP + "%"; r.style.right = (100 - hiP) + "%";
    var vlbl = $(".adv-row-label", t.wrap);
    vlbl.textContent = vlbl.dataset.base + " : " + b.fmt(lo) + " – " + b.fmt(hi);
  }
  function addInline(body, cls, label) {
    var wrap = document.createElement("div"); wrap.className = "adv-row adv-inline";
    var l = document.createElement("span"); l.className = "adv-row-label"; l.textContent = label;
    var v = document.createElement("span"); v.className = cls;
    wrap.appendChild(l); wrap.appendChild(v); body.appendChild(wrap); return v;
  }
  function renderAdvGeneral(body) {
    addSlider(body, "year", tA("adv.year"), YEAR_B);
    addSlider(body, "rating", tA("adv.rating"), RATING_B);
    var rv = addInline(body, "adv-select", tA("adv.releaseType")); rv.innerHTML = '‹ <span class="adv-sval"></span> ›';
    var ri = advTargets.length; advTargets.push({ type: "select", kind: "rt", el: rv, wrap: rv }); rv.dataset.advi = ri; paintSelect(advTargets[ri]);
    [["flagFav", "adv.fav"], ["flagInstalled", "adv.installed"]].forEach(function (f) {
      var tv = addInline(body, "adv-toggle", tA(f[1]));
      var i = advTargets.length; advTargets.push({ type: "toggle", key: f[0], el: tv, wrap: tv }); tv.dataset.advi = i; paintToggle(advTargets[i]);
    });
  }
  function paintSelect(t) { $(".adv-sval", t.el).textContent = advCrit.releaseType || tA("adv.any"); }
  function paintToggle(t) { var on = !!advCrit[t.key]; t.el.textContent = on ? tA("adv.on") : "—"; t.el.classList.toggle("on", on); }

  function renderAdvGenre(body) {
    var mv = addInline(body, "adv-select", tA("adv.match")); mv.innerHTML = '‹ <span class="adv-mode"></span> ›';
    var mi = advTargets.length; advTargets.push({ type: "genremode", el: mv, wrap: mv }); mv.dataset.advi = mi; paintGenreMode(advTargets[mi]);
    var list = document.createElement("div"); list.className = "adv-list"; body.appendChild(list);
    advFacets.genres.forEach(function (name) {
      var on = advCrit.genres.indexOf(name) >= 0;
      var it = document.createElement("div"); it.className = "adv-item" + (on ? " on" : ""); it.textContent = (on ? "✓ " : "") + name;
      list.appendChild(it); var i = advTargets.length; advTargets.push({ type: "genre", name: name, el: it }); it.dataset.advi = i;
    });
  }
  function paintGenreMode(t) { $(".adv-mode", t.el).textContent = advCrit.genreMode === "and" ? tA("adv.and") : tA("adv.or"); }

  // Onglet texte (éditeur OU développeur) : le texte saisi EST le filtre (SOUS-CHAÎNE, insensible
  // casse) → « Capc » matche Capcom, Entreprise Capcom… La liste est une AIDE (suggestions) :
  // la sélectionner remplit juste le champ. kind = "publisher" | "developer".
  function renderAdvText(body, kind) {
    var val = advCrit[kind] || "";
    var field = document.createElement("div"); field.className = "adv-field" + (val ? "" : " placeholder");
    field.textContent = val || tA("adv.devpubField");
    body.appendChild(field); var fi = advTargets.length; advTargets.push({ type: "textfield", kind: kind, el: field }); field.dataset.advi = fi;
    var list = document.createElement("div"); list.className = "adv-list"; body.appendChild(list);
    var q = val.toUpperCase(), pool = advFacets[kind + "s"] || [];
    pool.filter(function (v) { return !q || v.toUpperCase().indexOf(q) >= 0; }).slice(0, 40).forEach(function (v) {
      var it = document.createElement("div"); it.className = "adv-item"; it.textContent = v;
      list.appendChild(it); var i = advTargets.length; advTargets.push({ type: "textitem", kind: kind, name: v, el: it }); it.dataset.advi = i;
    });
  }
  // Onglet "Trier par" : alphabétique (défaut) / date de sortie / note / récemment joué.
  var ADV_SORTS = ["alpha", "year", "rating", "lastplayed"];
  function renderAdvOrderby(body) {
    var list = document.createElement("div"); list.className = "adv-list"; body.appendChild(list);
    ADV_SORTS.forEach(function (opt) {
      var on = (advCrit.sortBy || "alpha") === opt;
      var it = document.createElement("div"); it.className = "adv-item" + (on ? " on" : ""); it.textContent = (on ? "✓ " : "") + tA("adv.sort." + opt);
      list.appendChild(it); var i = advTargets.length; advTargets.push({ type: "sortopt", opt: opt, el: it }); it.dataset.advi = i;
    });
  }
  // Tri du tableau filtré selon sortBy (desc pour date/note/joué ; alpha = ordre déjà en place).
  function sortGamesByAdv(arr, sb) {
    if (sb === "year") return arr.slice().sort(function (a, b) { return (parseInt(b && b.y, 10) || 0) - (parseInt(a && a.y, 10) || 0); });
    // Note effective : note user (g.ur > 0) sinon note communauté (g.r). g.ur est désormais
    // user-only (0 si pas de note perso) ; sans ce fallback, tous les jeux sans note user
    // tomberaient au score 0 et seraient indiscernables des jeux non notés.
    if (sb === "rating") return arr.slice().sort(function (a, b) {
      var ra = (a && a.ur > 0) ? a.ur : (parseFloat(a && a.r) || 0);
      var rb = (b && b.ur > 0) ? b.ur : (parseFloat(b && b.r) || 0);
      return rb - ra;
    });
    if (sb === "lastplayed") return arr.slice().sort(function (a, b) { return ((b && b.lp) || 0) - ((a && a.lp) || 0); });
    return arr;   // alpha : DATA.gamesAll est déjà trié, le filtre conserve l'ordre
  }
  function renderAdvHistory(body) {
    var hist = loadAdvHistory();
    if (!hist.length) { var p = document.createElement("div"); p.className = "adv-soon"; p.textContent = tA("adv.noHistory"); body.appendChild(p); return; }
    var list = document.createElement("div"); list.className = "adv-list"; body.appendChild(list);
    hist.forEach(function (crit) {
      var it = document.createElement("div"); it.className = "adv-item"; it.textContent = advHistoryLabel(crit);
      list.appendChild(it); var i = advTargets.length; advTargets.push({ type: "histitem", crit: crit, el: it }); it.dataset.advi = i;
    });
  }
  function paintAdv() {
    for (var i = 0; i < advTargets.length; i++) if (advTargets[i].el) advTargets[i].el.classList.toggle("focus", i === advFocus);
    var f = advTargets[advFocus]; if (f && f.el && f.el.scrollIntoView) f.el.scrollIntoView({ block: "nearest" });
  }
  function advMoveTab(dir) { advTab = (advTab + dir + ADV_TABS.length) % ADV_TABS.length; advFocus = 0; renderAdv(); }
  function advAdjust(delta) {
    var t = advTargets[advFocus]; if (!t) return;
    if (t.type === "slider") {
      var b = t.bounds, key = t.kind + (t.which === "lo" ? "Min" : "Max"), v = advCrit[key] + delta * b.step;
      if (t.which === "lo") v = Math.min(advCrit[t.kind + "Max"], Math.max(b.min, v));
      else v = Math.max(advCrit[t.kind + "Min"], Math.min(b.max, v));
      advCrit[key] = Math.round(v / b.step) * b.step; paintSlider(t);
    } else if (t.type === "select") {
      var arr = [""].concat(advFacets.releaseTypes), idx = arr.indexOf(advCrit.releaseType); if (idx < 0) idx = 0;
      advCrit.releaseType = arr[(idx + delta + arr.length) % arr.length]; paintSelect(t);
    } else if (t.type === "genremode") { advCrit.genreMode = advCrit.genreMode === "and" ? "or" : "and"; paintGenreMode(t); }
    else if (t.type === "toggle") { advCrit[t.key] = !advCrit[t.key]; paintToggle(t); }
  }
  function advActivate() {
    var t = advTargets[advFocus]; if (!t) return;
    if (t.type === "apply") applyAdvanced();
    else if (t.type === "toggle") { advCrit[t.key] = !advCrit[t.key]; paintToggle(t); }
    else if (t.type === "select") advAdjust(1);
    else if (t.type === "genremode") { advCrit.genreMode = advCrit.genreMode === "and" ? "or" : "and"; paintGenreMode(t); }
    else if (t.type === "genre") { var gi = advCrit.genres.indexOf(t.name); if (gi >= 0) advCrit.genres.splice(gi, 1); else advCrit.genres.push(t.name); renderAdv(); }
    else if (t.type === "textfield") openAdvKeyboard(t.kind);
    else if (t.type === "textitem") { advCrit[t.kind] = t.name; renderAdv(); }   // remplit le champ (toujours filtré en « contient »)
    else if (t.type === "sortopt") { advCrit.sortBy = t.opt; renderAdv(); }       // choix du tri (re-render = coche déplacée)
    else if (t.type === "histitem") applyAdvCrit(t.crit, true);
  }

  // ── Critères normalisés (seuls les filtres réglés) + prédicat + historique ──
  function normalizedCrit() {
    var n = {};
    if (advCrit.yearMin > YEAR_B.min) n.yearMin = advCrit.yearMin;
    if (advCrit.yearMax < YEAR_B.max) n.yearMax = advCrit.yearMax;
    if (advCrit.ratingMin > RATING_B.min) n.ratingMin = advCrit.ratingMin;
    if (advCrit.ratingMax < RATING_B.max) n.ratingMax = advCrit.ratingMax;
    if (advCrit.releaseType) n.releaseType = advCrit.releaseType;
    if (advCrit.flagFav) n.flagFav = true;
    if (advCrit.flagInstalled) n.flagInstalled = true;
    if (advCrit.genres.length) { n.genres = advCrit.genres.slice(); n.genreMode = advCrit.genreMode; }
    if (advCrit.publisher && advCrit.publisher.trim()) n.publisher = advCrit.publisher.trim();
    if (advCrit.developer && advCrit.developer.trim()) n.developer = advCrit.developer.trim();
    if (advCrit.sortBy && advCrit.sortBy !== "alpha") n.sortBy = advCrit.sortBy;
    return n;
  }
  function buildAdvPredicate(n) {
    var fns = [];
    if (n.yearMin != null) fns.push(function (g) { var y = parseInt(g && g.y, 10); return y >= n.yearMin; });
    if (n.yearMax != null) fns.push(function (g) { var y = parseInt(g && g.y, 10); return y <= n.yearMax; });
    // Note effective : note user (g.ur > 0) sinon note communauté (g.r).
    // g.ur est désormais user-only (SafeEffRating C# ne fallback plus sur CommunityStarRating) ;
    // le filtre doit reconstruire la sémantique "effective" lui-même pour ne pas exclure tous
    // les jeux avec note communauté mais sans note perso quand le slider est déplacé.
    if (n.ratingMin != null) fns.push(function (g) { var eff = (g && g.ur > 0) ? g.ur : (parseFloat(g && g.r) || 0); return eff >= n.ratingMin; });
    if (n.ratingMax != null) fns.push(function (g) { var eff = (g && g.ur > 0) ? g.ur : (parseFloat(g && g.r) || 0); return eff <= n.ratingMax; });
    if (n.releaseType) fns.push(function (g) { return (g && g.rt) === n.releaseType; });
    if (n.flagFav) fns.push(function (g) { return !!(g && g.fav); });
    if (n.flagInstalled) fns.push(function (g) { return !!(g && g.installed); });
    if (n.genres && n.genres.length) fns.push(function (g) {
      var gg = (g && g.g) || ""; return n.genreMode === "and"
        ? n.genres.every(function (x) { return gg.indexOf(x) >= 0; })
        : n.genres.some(function (x) { return gg.indexOf(x) >= 0; });
    });
    // Éditeur / développeur : SOUS-CHAÎNE insensible à la casse (« Capc » → Capcom, etc.).
    if (n.publisher) { var qp = n.publisher.toUpperCase(); fns.push(function (g) { return ((g && g.pub) || "").toUpperCase().indexOf(qp) >= 0; }); }
    if (n.developer) { var qd = n.developer.toUpperCase(); fns.push(function (g) { return ((g && g.dev) || "").toUpperCase().indexOf(qd) >= 0; }); }
    return fns;
  }
  function advHistoryLabel(n) {
    var p = [];
    if (n.yearMin != null || n.yearMax != null) p.push(tA("adv.year") + " " + (n.yearMin != null ? n.yearMin : "∞") + "–" + (n.yearMax != null ? n.yearMax : "∞"));
    if (n.ratingMin != null || n.ratingMax != null) p.push(tA("adv.rating") + " " + (n.ratingMin != null ? n.ratingMin : "∞") + "–" + (n.ratingMax != null ? n.ratingMax : "∞"));
    if (n.releaseType) p.push(n.releaseType);
    if (n.genres && n.genres.length) p.push((n.genreMode === "and" ? "& " : "") + n.genres.join(", "));
    if (n.publisher) p.push(n.publisher);
    if (n.developer) p.push(n.developer);
    if (n.flagFav) p.push(tA("adv.fav"));
    if (n.flagInstalled) p.push(tA("adv.installed"));
    if (n.sortBy && n.sortBy !== "alpha") p.push("↕ " + tA("adv.sort." + n.sortBy));
    return p.join(" · ") || "—";
  }
  function loadAdvHistory() { try { return JSON.parse(localStorage.getItem(ADV_HIST_KEY) || "[]") || []; } catch (e) { return []; } }
  function saveAdvHistory(n) {
    try {
      var key = JSON.stringify(n), h = loadAdvHistory().filter(function (c) { return JSON.stringify(c) !== key; });
      h.unshift(n); localStorage.setItem(ADV_HIST_KEY, JSON.stringify(h.slice(0, ADV_HIST_MAX)));
    } catch (e) {}
  }

  // Applique des critères normalisés `n` (depuis Appliquer ou l'historique) à la liste.
  function applyAdvCrit(n, save) {
    var fns = buildAdvPredicate(n);
    var sb = n.sortBy || "alpha";
    advActive = fns.length > 0 || sb !== "alpha";   // un tri non-alpha compte aussi comme filtre actif
    if (advActive && save) saveAdvHistory(n);
    favOnly = false; searchQuery = "";   // un seul filtre rapide à la fois
    var arr = fns.length ? DATA.gamesAll.filter(function (g) { return fns.every(function (f) { return f(g); }); }) : DATA.gamesAll;
    DATA.games = sortGamesByAdv(arr, sb);   // sortGamesByAdv slice() pour non-alpha → ne mute jamais gamesAll
    DATA.platformTotal = DATA.games.length;
    setupList("games", DATA.games.map(function (g) { return g.t; })); markGameStars("games");
    shownGame = -1; setSelected("games", 0, { instant: true });
    // BUG #1 FIX: poster side panel only refreshes via posterSelect(); setSelected alone is not enough.
    if (posterMode) posterSelect(0);
    updateAdvIndicator(); closeAdvanced();
  }
  function applyAdvanced() { applyAdvCrit(normalizedCrit(), true); }
  function clearAdvanced() {
    advActive = false; advCrit = null;
    DATA.games = DATA.gamesAll; DATA.platformTotal = DATA.platformTotalAll;
    setupList("games", DATA.games.map(function (g) { return g.t; })); markGameStars("games");
    shownGame = -1; setSelected("games", 0, { instant: true });
    // BUG #1 FIX: poster side panel only refreshes via posterSelect(); setSelected alone is not enough.
    if (posterMode) posterSelect(0);
    updateAdvIndicator();
  }
  // Indicateur "filtre actif" : marqueur topbar (avant l'horloge) + icône ☰ du rail colorée.
  function updateAdvIndicator() {
    var ind = $(".topbar .advind", screens.games); if (ind) ind.classList.toggle("on", advActive);
    var rv = screens.games.querySelector('.rail .rail-item[data-i="1"]'); if (rv) rv.classList.toggle("filtered", advActive);
  }
  // ── Recherche avancée ROM (sous-menu Select ROM) ────────────────────────
  // Parser côté client (port simplifié de Rom.SetTags + SetFiltersVars de BBP).
  // Extrait régions / langues / types / status flags depuis le filename à partir
  // des tokens entre [] et (). Pas de dépendance serveur — tout est dérivé du
  // fileName de chaque entry.
  var ROM_REGIONS = ["USA","Europe","France","Germany","Italy","Japan","Spain","Australia","World","Asia","Korea","Brazil","Netherlands","Russia"];
  var ROM_REGION_ALIASES = {
    "U":"USA","US":"USA","USA":"USA","UNL":"USA",
    "E":"Europe","EUR":"Europe","EUROPE":"Europe",
    "F":"France","FR":"France","FRA":"France","FRANCE":"France",
    "G":"Germany","GER":"Germany","GERMANY":"Germany",
    "I":"Italy","ITA":"Italy","ITALY":"Italy",
    "J":"Japan","JAP":"Japan","JAPAN":"Japan","JPN":"Japan",
    "A":"Australia","AUS":"Australia","AUSTRALIA":"Australia",
    "W":"World","WORLD":"World",
    "K":"Korea","KOR":"Korea","KOREA":"Korea",
    "S":"Spain","SPA":"Spain","SPAIN":"Spain",
    "BR":"Brazil","BRA":"Brazil","BRAZIL":"Brazil",
    "NL":"Netherlands","NETHERLANDS":"Netherlands",
    "RU":"Russia","RUS":"Russia","RUSSIA":"Russia",
    "ASIA":"Asia"
  };
  var ROM_LANGUAGES = ["English","French","German","Italian","Spanish","Japanese","Portuguese","Dutch","Russian"];
  var ROM_LANG_ALIASES = {
    "EN":"English","ENG":"English","ENGLISH":"English",
    "FR":"French","FRE":"French","FRENCH":"French",
    "DE":"German","GER":"German","GERMAN":"German",
    "IT":"Italian","ITA":"Italian","ITALIAN":"Italian",
    "ES":"Spanish","SPA":"Spanish","SPANISH":"Spanish",
    "JA":"Japanese","JP":"Japanese","JPN":"Japanese","JAPANESE":"Japanese",
    "PT":"Portuguese","POR":"Portuguese","PORTUGUESE":"Portuguese",
    "NL":"Dutch","DUT":"Dutch","DUTCH":"Dutch",
    "RU":"Russian","RUS":"Russian","RUSSIAN":"Russian"
  };
  var ROM_TYPES = ["Original","Hack","Translation","Trainer","Prototype","Beta","Demo","Sample","Unlicensed"];

  function _romTokens(fileName) {
    var name = String(fileName || "");
    var dotIdx = name.lastIndexOf(".");
    if (dotIdx > 0) name = name.substring(0, dotIdx);
    var tokens = [], re = /[\[\(]([^\]\[\(\)]+)[\]\)]/g, m;
    while ((m = re.exec(name)) !== null) tokens.push(m[1].trim());
    return tokens;
  }
  function _uniq(arr) { var s = {}, o = []; for (var i = 0; i < arr.length; i++) if (!s[arr[i]]) { s[arr[i]] = 1; o.push(arr[i]); } return o; }
  function parseRomTags(fileName) {
    var tokens = _romTokens(fileName);
    var regions = [], languages = [], types = [], statusFlags = [];
    tokens.forEach(function (tok) {
      var up = tok.toUpperCase();
      // Translation/hack prefixes BBP style
      if (/^T[.\+\-]/.test(up)) {
        types.push("Translation");
        var langPart = up.substring(2);
        var lang = ROM_LANG_ALIASES[langPart.substring(0, 3)] || ROM_LANG_ALIASES[langPart.substring(0, 2)];
        if (lang) languages.push(lang);
      }
      if (/^H[.\-]/.test(up)) types.push("Hack");
      // Status flags single-letter (TOSEC/GoodSet)
      if (up === "!") statusFlags.push("good");
      else if (/^B\d*$/.test(up)) statusFlags.push("bad");
      else if (/^A\d*$/.test(up)) statusFlags.push("alt");
      else if (/^T\d*$/.test(up)) statusFlags.push("trained");
      else if (/^O\d*$/.test(up)) statusFlags.push("overdump");
      else if (/^F\d*$/.test(up)) statusFlags.push("fixed");
      else if (up === "V" || up === "VERIFIED") statusFlags.push("verified");
      // Comma/plus-separated language list ("En,Fr,De" / "En+Fr")
      if (/^[A-Z]{2,3}([,+][A-Z]{2,3})+$/i.test(tok)) {
        tok.split(/[,+]/).forEach(function (l) { var nm = ROM_LANG_ALIASES[l.toUpperCase()]; if (nm) languages.push(nm); });
      }
      // Region / language single token (USA, Europe, En, Fr…)
      if (ROM_REGION_ALIASES[up]) regions.push(ROM_REGION_ALIASES[up]);
      if (ROM_LANG_ALIASES[up])   languages.push(ROM_LANG_ALIASES[up]);
      // Type keywords in any token
      if (up.indexOf("HACK") >= 0)        types.push("Hack");
      if (up.indexOf("TRANSLATION") >= 0) types.push("Translation");
      if (up.indexOf("TRAINER") >= 0)     types.push("Trainer");
      if (up.indexOf("PROTO") >= 0)       types.push("Prototype");
      if (up.indexOf("BETA") >= 0)        types.push("Beta");
      if (up.indexOf("DEMO") >= 0)        types.push("Demo");
      if (up.indexOf("SAMPLE") >= 0)      types.push("Sample");
      if (up === "UNL" || up.indexOf("UNLICENSED") >= 0) types.push("Unlicensed");
    });
    types = _uniq(types); if (!types.length) types = ["Original"];
    return { regions: _uniq(regions), languages: _uniq(languages), types: types, statusFlags: _uniq(statusFlags), tokens: tokens };
  }

  // État + bornes + tabs
  var romAdvOpen = false, romAdvModalEl = null, romAdvTab = 0, romAdvFocus = 0, romAdvTargets = [];
  // Sous-vue de l'onglet courant (pattern drill-in). null = vue principale. Quand
  // l'utilisateur active "Select Region(s):" / "Select Lang(s):", on bascule sur
  // la liste correspondante ; B/Left dans la liste revient à la vue principale.
  // romAdvParentFocus mémorise l'index focus parent pour le restaurer au drill-out.
  var romAdvSubView = null, romAdvParentFocus = 0;
  var ROM_ADV_TABS = ["general", "regionLang", "type", "sort", "history"];
  var ROM_ADV_HIST_KEY = "bbw.romAdvHistory", ROM_ADV_HIST_MAX = 10;
  var ROM_SORTS = ["default", "alpha", "size-asc", "size-desc", "fav-first", "recent-first"];
  // Bornes de taille en Mo : 0 .. 8192 (∞ aux extrémités, comme year/rating).
  var ROM_SIZE_B = { min: 0, max: 8192, step: 16, fmt: function (v) {
    if (v <= 0) return "0";
    if (v >= 8192) return "∞";
    return v >= 1024 ? (v / 1024).toFixed(1) + " GB" : v + " MB";
  }};
  function _bytesToMB(b) { return Math.round((Number(b) || 0) / (1024 * 1024)); }
  function defaultRomAdvCrit() {
    return {
      sizeMin: ROM_SIZE_B.min, sizeMax: ROM_SIZE_B.max,
      fav: false, recent: false,
      regions: [], regionMode: "or",
      languages: [], languageMode: "or",
      // Connecteur entre la clause Region et la clause Language. Permet
      // "Region USA OR Lang English" — utile parce que le parser ne dérive
      // pas systématiquement une langue d'un filename non-tagué.
      regionLanguageMode: "and",
      types: [],
      sortBy: "default",
    };
  }
  // Le critère vit sur la frame Select ROM (frame.romAdvCrit) → réinitialisé
  // à chaque entrée dans le sous-menu. Retourne null si pas dans le sous-menu.
  function _romAdvCrit() { var lvl = (_inRomMenu() && detailLevel()) || null; return lvl ? (lvl.romAdvCrit = lvl.romAdvCrit || defaultRomAdvCrit()) : null; }

  function setupRomAdv() {
    romAdvModalEl = $('[data-modal="rom-adv"]'); if (!romAdvModalEl) return;
    romAdvModalEl.addEventListener("click", function (e) {
      if (!romAdvOpen) return;
      var tab = e.target.closest && e.target.closest(".adv-tab");
      if (tab) { romAdvTab = +tab.dataset.i; romAdvFocus = 0; renderRomAdv(); return; }
      var el = e.target.closest && e.target.closest("[data-radvi]");
      if (el && mouseActive) { romAdvFocus = +el.dataset.radvi; paintRomAdv(); romAdvActivate(); return; }
      if (!(e.target.closest && e.target.closest(".adv-panel"))) closeRomAdv();
    });
  }
  function openRomAdv() {
    if (!romAdvModalEl || !_inRomMenu()) return;
    _romAdvCrit();   // s'assure que la frame en a un
    romAdvOpen = true; romAdvTab = 0; romAdvFocus = 0; romAdvSubView = null; zone = "rom-adv";
    renderRomAdv(); romAdvModalEl.classList.add("open");
  }
  function closeRomAdv() {
    romAdvOpen = false; romAdvSubView = null;
    if (romAdvModalEl) romAdvModalEl.classList.remove("open");
    zone = "list"; paintRomRail();
  }
  function romAdvDrillIn(subView) {
    romAdvParentFocus = romAdvFocus;
    romAdvSubView = subView; romAdvFocus = 0;
    renderRomAdv();
  }
  function romAdvDrillOut() {
    romAdvSubView = null; romAdvFocus = romAdvParentFocus || 0; romAdvParentFocus = 0;
    renderRomAdv();
  }
  function renderRomAdv() {
    if (!romAdvModalEl) return;
    var tabsEl = $(".adv-tabs", romAdvModalEl); tabsEl.innerHTML = "";
    ROM_ADV_TABS.forEach(function (id, i) {
      var t = document.createElement("div"); t.className = "adv-tab" + (i === romAdvTab ? " active" : "");
      t.textContent = ({general:"General",regionLang:"Region & Language",type:"Type",sort:"Sort",history:"History"})[id];
      t.dataset.i = i; tabsEl.appendChild(t);
    });
    var body = $(".adv-body", romAdvModalEl); body.innerHTML = ""; romAdvTargets = [];
    var id = ROM_ADV_TABS[romAdvTab];
    // Si on est dans une sous-vue (drill-in), on ne rend que la liste ciblée —
    // le label parent reste mémorisé dans romAdvParentFocus pour le drill-out.
    if (romAdvSubView === "regions")        renderRomAdvSubList(body, "regions",   ROM_REGIONS,   "Select Region(s)");
    else if (romAdvSubView === "languages") renderRomAdvSubList(body, "languages", ROM_LANGUAGES, "Select Lang(s)");
    else if (id === "general")     renderRomAdvGeneral(body);
    else if (id === "regionLang")  renderRomAdvRegionLang(body);
    else if (id === "type")        renderRomAdvMulti(body, "types", ROM_TYPES, false);
    else if (id === "sort")        renderRomAdvSort(body);
    else if (id === "history")     renderRomAdvHistory(body);
    var applyEl = $(".adv-apply", romAdvModalEl);
    // Onglet History : pas de bouton Apply (les items appliquent directement).
    applyEl.style.display = (id === "history") ? "none" : "";
    if (id !== "history") {
      applyEl.dataset.radvi = romAdvTargets.length;
      romAdvTargets.push({ type: "apply", el: applyEl });
    }
    if (romAdvFocus >= romAdvTargets.length) romAdvFocus = Math.max(0, romAdvTargets.length - 1);
    paintRomAdv();
  }
  function _addRomSlider(body, key, label) {
    var wrap = document.createElement("div"); wrap.className = "adv-row";
    wrap.innerHTML = '<div class="adv-row-label"></div>' +
      '<div class="adv-track"><div class="adv-range"></div><div class="adv-handle lo"></div><div class="adv-handle hi"></div></div>';
    $(".adv-row-label", wrap).dataset.base = label;
    body.appendChild(wrap);
    var loI = romAdvTargets.length;
    romAdvTargets.push({ type: "slider", kind: key, which: "lo", el: $(".adv-handle.lo", wrap), wrap: wrap, bounds: ROM_SIZE_B });
    romAdvTargets.push({ type: "slider", kind: key, which: "hi", el: $(".adv-handle.hi", wrap), wrap: wrap, bounds: ROM_SIZE_B });
    $(".adv-handle.lo", wrap).dataset.radvi = loI; $(".adv-handle.hi", wrap).dataset.radvi = loI + 1;
    _paintRomSlider(romAdvTargets[loI]);
  }
  function _paintRomSlider(t) {
    var c = _romAdvCrit(); if (!c) return;
    var b = t.bounds, span = Math.max(b.step, b.max - b.min);
    var lo = c.sizeMin, hi = c.sizeMax;
    var loP = (lo - b.min) / span * 100, hiP = (hi - b.min) / span * 100;
    $(".adv-handle.lo", t.wrap).style.left = loP + "%"; $(".adv-handle.hi", t.wrap).style.left = hiP + "%";
    var r = $(".adv-range", t.wrap); r.style.left = loP + "%"; r.style.right = (100 - hiP) + "%";
    var vlbl = $(".adv-row-label", t.wrap);
    vlbl.textContent = vlbl.dataset.base + " : " + b.fmt(lo) + " – " + b.fmt(hi);
  }
  function _addRomToggle(body, key, label) {
    var wrap = document.createElement("div"); wrap.className = "adv-row adv-inline";
    var l = document.createElement("span"); l.className = "adv-row-label"; l.textContent = label;
    var v = document.createElement("span"); v.className = "adv-toggle";
    wrap.appendChild(l); wrap.appendChild(v); body.appendChild(wrap);
    var i = romAdvTargets.length; romAdvTargets.push({ type: "toggle", key: key, el: v, wrap: v }); v.dataset.radvi = i;
    _paintRomToggle(romAdvTargets[i]);
  }
  function _paintRomToggle(t) { var c = _romAdvCrit(); if (!c) return; var on = !!c[t.key]; t.el.textContent = on ? "ON" : "—"; t.el.classList.toggle("on", on); }
  function renderRomAdvGeneral(body) {
    _addRomSlider(body, "size", "Size");
    _addRomToggle(body, "fav", "Favorites only");
    _addRomToggle(body, "recent", "Last played only");
  }
  // Multi-select FLAT (utilisé pour l'onglet Type). Pour Region/Language, voir
  // renderRomAdvRegionLang qui utilise un drill-in.
  function renderRomAdvMulti(body, key, options, withMode) {
    var c = _romAdvCrit(); if (!c) return;
    if (withMode) {
      _addRomModeToggle(body, key, "Match");
    }
    var list = document.createElement("div"); list.className = "adv-list"; body.appendChild(list);
    options.forEach(function (name) {
      var on = (c[key] || []).indexOf(name) >= 0;
      var it = document.createElement("div"); it.className = "adv-item" + (on ? " on" : ""); it.textContent = (on ? "✓ " : "") + name;
      list.appendChild(it);
      var i = romAdvTargets.length; romAdvTargets.push({ type: "multiitem", key: key, name: name, el: it });
      it.dataset.radvi = i;
    });
  }
  // Vue principale "Region & Language" : Match within regions + Select Region(s)
  // → drill-in ; connecteur AND/OR ; Match within languages + Select Lang(s) →
  // drill-in. Layout strictement aligné avec la demande utilisateur.
  function renderRomAdvRegionLang(body) {
    _addRomSection(body, "Region");
    _addRomModeToggle(body, "regions", "Match");
    _addRomDrillIn(body, "regions", "Select Region(s)");
    _addRomConnector(body, "Combine Region & Language");
    _addRomSection(body, "Specified Language");
    _addRomModeToggle(body, "languages", "Match");
    _addRomDrillIn(body, "languages", "Select Lang(s)");
  }
  // Sous-liste (drill-in) : juste les items sélectionnables, B/Left = drill-out.
  function renderRomAdvSubList(body, key, options, headerLabel) {
    var c = _romAdvCrit(); if (!c) return;
    _addRomSection(body, headerLabel + "  ·  B / ← to go back");
    var list = document.createElement("div"); list.className = "adv-list"; body.appendChild(list);
    options.forEach(function (name) {
      var on = (c[key] || []).indexOf(name) >= 0;
      var it = document.createElement("div"); it.className = "adv-item" + (on ? " on" : ""); it.textContent = (on ? "✓ " : "") + name;
      list.appendChild(it);
      var i = romAdvTargets.length; romAdvTargets.push({ type: "multiitem", key: key, name: name, el: it });
      it.dataset.radvi = i;
    });
  }
  function _addRomSection(body, label) {
    var h = document.createElement("div"); h.className = "adv-section"; h.textContent = label;
    body.appendChild(h);   // visuel uniquement — pas de focus target
  }
  function _addRomModeToggle(body, key, label) {
    var mv = document.createElement("div"); mv.className = "adv-row adv-inline";
    var l = document.createElement("span"); l.className = "adv-row-label"; l.textContent = label;
    var sv = document.createElement("span"); sv.className = "adv-select";
    sv.innerHTML = '‹ <span class="adv-mode"></span> ›';
    mv.appendChild(l); mv.appendChild(sv); body.appendChild(mv);
    var mi = romAdvTargets.length;
    romAdvTargets.push({ type: "multimode", key: key, el: sv, wrap: sv });
    sv.dataset.radvi = mi; _paintRomMultiMode(romAdvTargets[mi]);
  }
  function _addRomDrillIn(body, key, label) {
    var c = _romAdvCrit(); if (!c) return;
    var arr = c[key] || [];
    var summary = arr.length ? arr.join(", ") : "(none)";
    var wrap = document.createElement("div"); wrap.className = "adv-row adv-inline";
    var l = document.createElement("span"); l.className = "adv-row-label"; l.textContent = label;
    var sv = document.createElement("span"); sv.className = "adv-drillin";
    sv.textContent = summary + "  ›";
    wrap.appendChild(l); wrap.appendChild(sv); body.appendChild(wrap);
    var i = romAdvTargets.length;
    // subView = key tel quel ("regions" / "languages") — utilisé dans renderRomAdv.
    romAdvTargets.push({ type: "drillin", subView: key, el: sv, wrap: sv });
    sv.dataset.radvi = i;
  }
  function _addRomConnector(body, label) {
    var wrap = document.createElement("div"); wrap.className = "adv-row adv-inline adv-connector";
    var l = document.createElement("span"); l.className = "adv-row-label"; l.textContent = label;
    var sv = document.createElement("span"); sv.className = "adv-select";
    sv.innerHTML = '‹ <span class="adv-mode"></span> ›';
    wrap.appendChild(l); wrap.appendChild(sv); body.appendChild(wrap);
    var i = romAdvTargets.length;
    romAdvTargets.push({ type: "connector", el: sv, wrap: sv });
    sv.dataset.radvi = i;
    _paintRomConnector(romAdvTargets[i]);
  }
  function _paintRomConnector(t) {
    var c = _romAdvCrit(); if (!c) return;
    $(".adv-mode", t.el).textContent = (c.regionLanguageMode === "or") ? "OR" : "AND";
  }
  function _paintRomMultiMode(t) {
    var c = _romAdvCrit(); if (!c) return;
    var mode = (t.key === "regions" ? c.regionMode : c.languageMode) || "or";
    $(".adv-mode", t.el).textContent = mode === "and" ? "AND" : "OR";
  }
  function renderRomAdvSort(body) {
    var c = _romAdvCrit(); if (!c) return;
    var list = document.createElement("div"); list.className = "adv-list"; body.appendChild(list);
    ROM_SORTS.forEach(function (opt) {
      var on = (c.sortBy || "default") === opt;
      var labels = { "default":"Default (lastPlayed → fav → priority → alpha)", "alpha":"Alphabetical", "size-asc":"Size ↑", "size-desc":"Size ↓", "fav-first":"Favorites first", "recent-first":"Last played first" };
      var it = document.createElement("div"); it.className = "adv-item" + (on ? " on" : ""); it.textContent = (on ? "✓ " : "") + labels[opt];
      list.appendChild(it);
      var i = romAdvTargets.length; romAdvTargets.push({ type: "sortopt", opt: opt, el: it });
      it.dataset.radvi = i;
    });
  }
  function paintRomAdv() {
    for (var i = 0; i < romAdvTargets.length; i++) if (romAdvTargets[i].el) romAdvTargets[i].el.classList.toggle("focus", i === romAdvFocus);
    var f = romAdvTargets[romAdvFocus]; if (f && f.el && f.el.scrollIntoView) f.el.scrollIntoView({ block: "nearest" });
  }
  function romAdvMoveTab(dir) {
    // Quitter une sous-vue à coup de L/R d'onglet : on remonte d'abord.
    if (romAdvSubView) { romAdvSubView = null; romAdvParentFocus = 0; }
    romAdvTab = (romAdvTab + dir + ROM_ADV_TABS.length) % ROM_ADV_TABS.length; romAdvFocus = 0; renderRomAdv();
  }
  function romAdvAdjust(delta) {
    var t = romAdvTargets[romAdvFocus]; if (!t) return;
    var c = _romAdvCrit(); if (!c) return;
    if (t.type === "slider") {
      var b = t.bounds, key = t.which === "lo" ? "sizeMin" : "sizeMax", v = c[key] + delta * b.step;
      if (t.which === "lo") v = Math.min(c.sizeMax, Math.max(b.min, v));
      else v = Math.max(c.sizeMin, Math.min(b.max, v));
      c[key] = Math.round(v / b.step) * b.step; _paintRomSlider(t);
    } else if (t.type === "toggle") { c[t.key] = !c[t.key]; _paintRomToggle(t); }
    else if (t.type === "multimode") {
      var mk = t.key === "regions" ? "regionMode" : "languageMode";
      c[mk] = c[mk] === "and" ? "or" : "and"; _paintRomMultiMode(t);
    }
    else if (t.type === "connector") {
      c.regionLanguageMode = c.regionLanguageMode === "and" ? "or" : "and"; _paintRomConnector(t);
    }
    else if (t.type === "drillin" && delta > 0) {
      // Right sur un drill-in entre dans la sous-vue (équivaut à A).
      romAdvDrillIn(t.subView);
    }
  }
  function romAdvActivate() {
    var t = romAdvTargets[romAdvFocus]; if (!t) return;
    if (t.type === "apply") { applyRomAdv(); return; }
    var c = _romAdvCrit(); if (!c) return;
    if (t.type === "toggle") { c[t.key] = !c[t.key]; _paintRomToggle(t); }
    else if (t.type === "multimode") { romAdvAdjust(1); }
    else if (t.type === "connector") { romAdvAdjust(1); }
    else if (t.type === "drillin")   { romAdvDrillIn(t.subView); }
    else if (t.type === "multiitem") {
      var arr = c[t.key] || (c[t.key] = []);
      var idx = arr.indexOf(t.name);
      if (idx >= 0) arr.splice(idx, 1); else arr.push(t.name);
      renderRomAdv();
    } else if (t.type === "sortopt") { c.sortBy = t.opt; renderRomAdv(); }
    else if (t.type === "histitem") { applyNormalizedRomCrit(t.crit); closeRomAdv(); applyRomFilters(); }
  }
  // ── Historique des critères ROM (localStorage, partagé entre archives) ────
  // Le critère est NORMALISÉ avant sauvegarde : seuls les champs « non-défaut »
  // sont conservés → libellé court + comparaison stable pour la dédup.
  function normalizedRomCrit(c) {
    if (!c) return {};
    var n = {};
    if (c.sizeMin > ROM_SIZE_B.min) n.sizeMin = c.sizeMin;
    if (c.sizeMax < ROM_SIZE_B.max) n.sizeMax = c.sizeMax;
    if (c.fav) n.fav = true;
    if (c.recent) n.recent = true;
    if (c.regions && c.regions.length) { n.regions = c.regions.slice(); if (c.regionMode === "and") n.regionMode = "and"; }
    if (c.languages && c.languages.length) { n.languages = c.languages.slice(); if (c.languageMode === "and") n.languageMode = "and"; }
    if (n.regions && n.languages && c.regionLanguageMode === "or") n.regionLanguageMode = "or";
    if (c.types && c.types.length) n.types = c.types.slice();
    if (c.sortBy && c.sortBy !== "default") n.sortBy = c.sortBy;
    return n;
  }
  function isEmptyRomCrit(n) { for (var k in n) if (Object.prototype.hasOwnProperty.call(n, k)) return false; return true; }
  function loadRomAdvHistory() { try { return JSON.parse(localStorage.getItem(ROM_ADV_HIST_KEY) || "[]") || []; } catch (e) { return []; } }
  function saveRomAdvHistory(n) {
    if (!n || isEmptyRomCrit(n)) return;
    try {
      var key = JSON.stringify(n), h = loadRomAdvHistory().filter(function (c) { return JSON.stringify(c) !== key; });
      h.unshift(n); localStorage.setItem(ROM_ADV_HIST_KEY, JSON.stringify(h.slice(0, ROM_ADV_HIST_MAX)));
    } catch (e) {}
  }
  function romAdvHistoryLabel(n) {
    var p = [];
    if (n.sizeMin != null || n.sizeMax != null) {
      var b = ROM_SIZE_B;
      p.push("Size " + b.fmt(n.sizeMin != null ? n.sizeMin : b.min) + "–" + b.fmt(n.sizeMax != null ? n.sizeMax : b.max));
    }
    if (n.fav) p.push("★ Fav");
    if (n.recent) p.push("↻ Recent");
    if (n.regions && n.regions.length) p.push((n.regionMode === "and" ? "& " : "") + n.regions.join(", "));
    if (n.regions && n.languages) p.push(n.regionLanguageMode === "or" ? "OR" : "AND");
    if (n.languages && n.languages.length) p.push((n.languageMode === "and" ? "& " : "") + n.languages.join(", "));
    if (n.types && n.types.length) p.push(n.types.join(", "));
    if (n.sortBy) p.push("↕ " + n.sortBy);
    return p.join(" · ") || "—";
  }
  // Applique un critère normalisé à la frame courante (depuis l'onglet History).
  function applyNormalizedRomCrit(n) {
    var lvl = (_inRomMenu() && detailLevel()) || null; if (!lvl) return;
    lvl.romAdvCrit = defaultRomAdvCrit();
    var c = lvl.romAdvCrit;
    if (n.sizeMin != null) c.sizeMin = n.sizeMin;
    if (n.sizeMax != null) c.sizeMax = n.sizeMax;
    if (n.fav) c.fav = true;
    if (n.recent) c.recent = true;
    if (n.regions) { c.regions = n.regions.slice(); c.regionMode = n.regionMode || "or"; }
    if (n.languages) { c.languages = n.languages.slice(); c.languageMode = n.languageMode || "or"; }
    if (n.regionLanguageMode) c.regionLanguageMode = n.regionLanguageMode;
    if (n.types) c.types = n.types.slice();
    if (n.sortBy) c.sortBy = n.sortBy;
  }
  function renderRomAdvHistory(body) {
    var hist = loadRomAdvHistory();
    if (!hist.length) { var p = document.createElement("div"); p.className = "adv-soon"; p.textContent = "No saved searches yet — apply a filter to add one."; body.appendChild(p); return; }
    var list = document.createElement("div"); list.className = "adv-list"; body.appendChild(list);
    hist.forEach(function (crit) {
      var it = document.createElement("div"); it.className = "adv-item"; it.textContent = romAdvHistoryLabel(crit);
      list.appendChild(it);
      var i = romAdvTargets.length; romAdvTargets.push({ type: "histitem", crit: crit, el: it });
      it.dataset.radvi = i;
    });
  }
  function applyRomAdv() {
    // Sauvegarde dans l'historique (uniquement si non-vide) puis ferme + applique.
    var c = _romAdvCrit(); if (c) saveRomAdvHistory(normalizedRomCrit(c));
    closeRomAdv();
    applyRomFilters();
  }
  // Ouvre le clavier virtuel (réutilisé) en mode "adv" pour l'auto-complétion éditeur/dév.
  function openAdvKeyboard(kind) {
    if (!searchModalEl || !searchKeyboardEnabled()) return;
    advTextKind = kind || "publisher";
    kbMode = "adv"; searchOpen = true; searchR = 1; searchC = 0;
    searchModalEl.classList.add("open"); paintSearch();
  }

  // ── Recherche (écran jeux) : mini-clavier QWERTY + filtre LIVE sur le compareName ──────
  // Clé de recherche = compareName COMPACT (sans espace), mémoïsée par jeu (g._sk). La requête
  // est normalisée pareil ([A-Z0-9]) ; un jeu matche si sa clé CONTIENT la requête.
  function searchKeyOf(g) { if (g && g._sk == null) g._sk = gameCN(g).replace(/[^0-9A-Z]/g, ""); return (g && g._sk) || ""; }
  function normSearch(q) { return (q || "").toUpperCase().replace(/[^0-9A-Z]/g, ""); }

  // Choix de la disposition : config.search.layout ("qwerty"/"azerty" force, "auto"=langue).
  // En "auto" : langue LB (window.BBW.lang, depuis Settings.xml) → AZERTY si "fr", sinon QWERTY.
  // Repli sur navigator.language quand la langue LB est inconnue (standalone/dummy).
  // NB : LB stocke un code culture ("fr-FR", "de-DE", …) → on garde le sous-tag primaire
  // (slice 0,2), comme i18n.js, sinon "fr-fr" !== "fr" et l'AZERTY ne se déclenche jamais.
  function chooseLayout() {
    var s = (((G.search || {}).layout) || "auto").toString().toLowerCase();
    if (KB_LAYOUTS[s]) return s;   // disposition forcée (qwerty/azerty/qwertz)
    var lang = (("" + (window.BBW.lang || navigator.language || "")).toLowerCase()).slice(0, 2);
    if (lang === "fr") return "azerty";   // Français → AZERTY
    if (lang === "de") return "qwertz";   // Allemand → QWERTZ
    return "qwerty";                       // En/Es/It/Pt et défaut → QWERTY
  }
  function buildKB() {
    var rows = (KB_LAYOUTS[chooseLayout()] || KB_LAYOUTS.qwerty).map(function (s) { return s.split(""); });
    rows.push(KB_BOTTOM.slice());
    return rows;
  }

  function setupSearch() {
    searchModalEl = $(".search-modal"); if (!searchModalEl) return;
    KB = buildKB();   // disposition selon la langue LB (connue à ce stade du boot)
    var box = $(".search-keys", searchModalEl); box.innerHTML = ""; searchKeyEls = [];
    KB.forEach(function (row, r) {
      var rowEl = document.createElement("div"); rowEl.className = "skey-row";
      var rowEls = [];
      row.forEach(function (k, c) {
        var d = document.createElement("div"); d.className = "skey"; d.dataset.r = r; d.dataset.c = c; d.dataset.k = k;
        if (k === "{space}") { d.textContent = window.BBW.t("key.space"); d.classList.add("wide", "space"); }
        else if (k === "{bksp}") { d.textContent = "⌫"; d.classList.add("wide"); }
        else if (k === "{clr}") { d.textContent = "CLR"; d.classList.add("wide"); }
        else if (k === "{ok}") { d.textContent = "OK"; d.classList.add("wide"); }
        else d.textContent = k;
        d.addEventListener("mouseenter", function () { if (searchOpen && mouseActive) { searchR = r; searchC = c; paintSearch(); } });
        d.addEventListener("click", function () { if (searchOpen) { searchR = r; searchC = c; paintSearch(); searchPress(k); } });
        rowEl.appendChild(d); rowEls.push(d);
      });
      box.appendChild(rowEl); searchKeyEls.push(rowEls);
    });
  }
  // Affiche le clavier selon le mode courant (config.search.keyboard.<mode>).
  function searchKeyboardEnabled() {
    var kb = (G.search && G.search.keyboard) || {};
    var m = window.BBW.mode ? window.BBW.mode() : "desktop";
    return kb[m] !== false;
  }
  function openSearch() {
    if (!searchModalEl || current !== "games" || !searchKeyboardEnabled()) return;
    kbMode = "quick"; searchOpen = true; searchR = 1; searchC = 0;   // démarre sur le Q
    searchModalEl.classList.add("open");
    paintSearch();
  }
  // Réutilise le même clavier virtuel pour la recherche ROM : la frappe alimente
  // frame.romFilters.query et applyRomFilters() est appelé en live. Ouvert depuis
  // le ☰ du rom-rail. Le mode "rom" est géré par paintSearch / searchPress /
  // closeSearch en parallèle de "quick" (jeux) et "adv" (auto-complétion).
  function openRomSearch() {
    if (!searchModalEl || !_inRomMenu() || !searchKeyboardEnabled()) return;
    kbMode = "rom"; searchOpen = true; searchR = 1; searchC = 0;
    searchModalEl.classList.add("open");
    paintSearch();
  }
  function closeSearch() {
    searchOpen = false; if (searchModalEl) searchModalEl.classList.remove("open");
    if (kbMode === "rom")      { zone = "list"; paintRomRail(); }   // retour à la liste ROM
    else if (kbMode !== "adv") { zone = "list"; paintRail(); }      // en mode adv, on retombe sur la modale avancée
    kbMode = "quick";
  }
  // Réinitialise les filtres transitoires (changement de plateforme) : recherche vidée + favoris.
  function resetSearch() {
    searchQuery = ""; favOnly = false; if (searchOpen) closeSearch();
    advActive = false; advCrit = null; if (advOpen) closeAdvanced(); updateAdvIndicator();
  }
  function paintSearch() {
    if (!searchModalEl) return;
    for (var r = 0; r < searchKeyEls.length; r++)
      for (var c = 0; c < searchKeyEls[r].length; c++)
        searchKeyEls[r][c].classList.toggle("focus", r === searchR && c === searchC);
    var q = $(".search-q", searchModalEl); if (!q) return;
    if (kbMode === "adv") q.textContent = (advCrit && advCrit[advTextKind] || "");
    else if (kbMode === "rom") {
      var lvl = (_inRomMenu() && detailLevel()) || null;
      q.textContent = (lvl && lvl.romFilters && lvl.romFilters.query) || "";
    }
    else q.textContent = searchQuery;
  }
  function searchMove(cmd) {
    if (cmd === "down") searchR = Math.min(searchR + 1, KB.length - 1);
    else if (cmd === "up") searchR = Math.max(searchR - 1, 0);
    else if (cmd === "right") searchC += 1;
    else if (cmd === "left") searchC -= 1;
    searchC = Math.max(0, Math.min(searchC, KB[searchR].length - 1));   // borne à la ligne courante
    paintSearch();
  }
  function searchPress(k) {
    if (kbMode === "adv") {   // auto-complétion éditeur/dév : la frappe alimente advCrit[advTextKind]
      var cur = advCrit[advTextKind] || "";
      if (k === "{ok}") { closeAdvKeyboard(); return; }
      if (k === "{bksp}") cur = cur.slice(0, -1);
      else if (k === "{clr}") cur = "";
      else if (k === "{space}") cur += " ";
      else cur += k;
      advCrit[advTextKind] = cur;
      paintSearch(); renderAdv();   // re-filtre la liste de suggestions (visible au-dessus du clavier)
      return;
    }
    if (kbMode === "rom") {   // recherche ROM : la frappe alimente frame.romFilters.query
      var lvl = (_inRomMenu() && detailLevel()) || null;
      if (!lvl) { closeSearch(); return; }
      if (!lvl.romFilters) lvl.romFilters = { fav: false, recent: false, query: "" };
      if (k === "{ok}") { closeSearch(); return; }
      var rq = lvl.romFilters.query || "";
      if (k === "{bksp}") rq = rq.slice(0, -1);
      else if (k === "{clr}") rq = "";
      else if (k === "{space}") rq += " ";
      else rq += k;
      lvl.romFilters.query = rq;
      paintSearch();
      applyRomFilters();   // re-filtre la liste en live
      return;
    }
    if (k === "{ok}") { closeSearch(); return; }
    if (k === "{bksp}") searchQuery = searchQuery.slice(0, -1);
    else if (k === "{clr}") searchQuery = "";
    else if (k === "{space}") searchQuery += " ";   // espace VISIBLE (ignoré par la comparaison, qui se fait sur le compareName compact)
    else searchQuery += k;
    paintSearch();
    applySearchFilter();
  }
  // Ferme le clavier d'auto-complétion (mode adv) → on garde la modale avancée et on place le
  // focus sur le 1er résultat éditeur/dév (s'il y en a) pour pouvoir le choisir au gamepad.
  function closeAdvKeyboard() {
    searchOpen = false; if (searchModalEl) searchModalEl.classList.remove("open"); kbMode = "quick";
    var firstItem = -1;
    for (var i = 0; i < advTargets.length; i++) if (advTargets[i].type === "textitem") { firstItem = i; break; }
    if (firstItem >= 0) advFocus = firstItem;
    paintAdv();
  }
  // Reconstruit la liste de jeux filtrée (compareName CONTIENT la requête) et sélectionne le 1er.
  function applySearchFilter() {
    var nq = normSearch(searchQuery);
    DATA.games = nq ? DATA.gamesAll.filter(function (g) { return searchKeyOf(g).indexOf(nq) >= 0; }) : DATA.gamesAll;
    DATA.platformTotal = nq ? DATA.games.length : DATA.platformTotalAll;
    setupList("games", DATA.games.map(function (g) { return g.t; })); markGameStars("games");
    shownGame = -1; setSelected("games", 0, { instant: true });
    // BUG #1 FIX: poster side panel only refreshes via posterSelect(); setSelected alone is not enough.
    if (posterMode) posterSelect(0);
  }

  // ── Recent (écran catégories) — délégué (la rangée est re-rendue par catégorie) ──
  function setupRecent() {
    var rec = $(".recent", screens.categories);
    rec.addEventListener("mouseover", function (e) {
      if (current !== "categories" || window.BBW.isTouch() || !mouseActive) return;
      var th = e.target.closest && e.target.closest(".thumb"); if (!th || th.closest(".rgn-clone")) return;
      zone = "recent"; recentSel = +th.dataset.i; paintRecent();
    });
    rec.addEventListener("click", function (e) {
      if (current !== "categories") return;
      var th = e.target.closest && e.target.closest(".thumb"); if (!th || th.closest(".rgn-clone")) return;
      recentSel = +th.dataset.i; recentActivate();
    });
  }
  function visibleThumbs() { return screens.categories.querySelectorAll(".recent-clip .rgn-inner:not(.rgn-clone) .thumb"); }
  function paintRecent() { visibleThumbs().forEach(function (th, i) { th.classList.toggle("sel", zone === "recent" && i === recentSel); }); }
  function recentCount() { return visibleThumbs().length; }
  function recentActivate() {
    var it = recentItems[recentSel];
    if (!it) return;                         // placeholder vide → rien
    cancelGameContentLoads();                // coupe un détail de la liste encore en vol
    if (it.id == null) {
      // Repli dummy (libellé sans id réel) : ancien comportement (index dans la liste courante).
      currentGame = Math.min(Math.max(recentSel, 0), DATA.games.length - 1);
    } else {
      // Jeu récent réel (peut venir d'une AUTRE plateforme) : on l'injecte dans la liste
      // courante et on sélectionne son index — le flux détail est indexé sur DATA.games.
      DATA.games.push(it);
      currentGame = DATA.games.length - 1;
    }
    detailsReturn = "categories";
    // Entrée fraîche sur details → force le refetch du detail.json (pour récupérer
    // un éventuel lastLaunch mis à jour par un Play récent) + arme la sync.
    var _gFresh1 = DATA.games[currentGame]; if (_gFresh1) _gFresh1._det = false;
    _pendingLastLaunchSync = true;
    requestGameDetail(currentGame);
    fillGamePanel(screens.details, currentGame);
    resetDetailMenu();
    setSelected("details", 0, { instant: true });
    navTo("details", false);
  }

  // ── Rendu par zone (catégories) ─────────────────────────────────────────
  function fillCatDetailInner(inner, i) {
    var c = catNode(i);
    $(".cat-title", inner).textContent = c.name;
    var sub = $(".cat-sub", inner), box = $(".cat-box", inner);
    if (c.kind === "playlist") {
      sub.style.display = "none";
      box.innerHTML = c.stats.map(function (s) { return '<div class="stat">' + s + "</div>"; }).join("");
    } else {
      sub.style.display = "flex";
      sub.innerHTML = '<span class="year">' + c.sub[0] + "</span><span>" + c.sub[1] + "</span>";
      box.innerHTML = '<div class="desc-text">' + c.desc + "</div>";
    }
    $(".pill-count", inner).textContent = c.count + " GAMES";
  }
  function fillCatMediaInner(inner, i) {
    clearCatMedia(inner);                              // retire le média (video/img) du nœud précédent
    inner.style.background = catNode(i).media || "";   // gradient = placeholder + fallback (sous le média)
  }
  // Retire toute <video>/<img> de fond d'une zone cat-media (et stoppe le son de la vidéo).
  function clearCatMedia(inner) {
    if (!inner || !inner.querySelectorAll) return;
    var olds = inner.querySelectorAll("video, img.cat-bg");
    for (var k = 0; k < olds.length; k++) {
      var e = olds[k];
      if (e.tagName === "VIDEO") { try { e.pause(); e.removeAttribute("src"); e.load(); } catch (_) {} }
      if (e.parentNode) e.parentNode.removeChild(e);
    }
  }
  // Rendu IMMÉDIAT de la rangée recent (transition/instant) : libellés du dummy si le nœud
  // n'a pas de chemin lazy, sinon placeholders. Les vraies vignettes arrivent via
  // loadRecentLazy (fetch serveur + cache) une fois la sélection posée.
  function fillCatRecentInner(inner, i) {
    var node = catNode(i), row = $(".row", inner);
    if (node && !node.path && node.recent && node.recent.length) renderRecentRow(row, node.recent.map(toLegacyItem));
    else renderRecentRow(row, null);
  }
  function toLegacyItem(s) { return { id: null, t: s, thumb: null }; }
  // Une vignette par item ({id,t,thumb}); liste vide → 8 placeholders.
  function renderRecentRow(row, items) {
    if (!row) return;
    if (!items || !items.length) {
      var ph = ""; for (var k = 0; k < 8; k++) ph += '<div class="thumb" data-i="' + k + '"><div class="rc-img-ph"></div></div>';
      row.innerHTML = ph; return;
    }
    row.innerHTML = items.map(function (it, k) {
      var imgPart = it.thumb
        ? '<img class="rc-img" src="' + it.thumb + '" alt="">'
        : '<div class="rc-img-ph"><span class="ph-title">' + (it.t || "") + "</span></div>";
      var titlePart = '<span class="rc-title">' + (it.t || "") + "</span>";
      return '<div class="thumb" data-i="' + k + '">' + imgPart + titlePart + "</div>";
    }).join("");
  }
  // Charge le "recent" du nœud i (LAZY + cache BBW.get) puis remplit la rangée vive.
  // Le filtrage parental est appliqué CÔTÉ SERVEUR (recent.json). Repli sur les libellés
  // dummy si le serveur ne renvoie rien (mode standalone, ou nœud sans chemin).
  // TTL aléatoire du cache recent (config recent.cacheTtlMin/MaxMs) → étale les rechargements.
  function recentTtlMs() {
    var r = G.recent || {};
    var lo = r.cacheTtlMinMs || 600000, hi = r.cacheTtlMaxMs || 1200000;
    if (hi < lo) hi = lo;
    return Math.round(lo + Math.random() * (hi - lo));
  }
  function loadRecentLazy(i) {
    var node = catNode(i); if (!node) return;
    // ?e=<epoch> : change quand un jeu se termine (serveur) → nouvelle clé de cache
    // BBW.get → refetch frais ; sinon réutilise le cache mémoire (re-visite instantanée),
    // qui expire de lui-même après un TTL aléatoire (recentTtlMs).
    var path = node.path ? ("data/" + node.path + "/recent.json?e=" + recentEpoch) : null;
    if (!path) { recentItems = (node.recent || []).map(toLegacyItem); fillRecentRowLive(); return; }
    window.BBW.get(path, recentTtlMs()).then(function (data) {
      if (shownCat !== i || current !== "categories") return;   // sélection a changé → on jette
      recentItems = (data && data.recent && data.recent.length) ? data.recent
                  : (node.recent || []).map(toLegacyItem);       // repli dummy
      fillRecentRowLive();
    });
  }
  function fillRecentRowLive() {
    var row = $(".recent-clip .rgn-inner:not(.rgn-clone) .row", screens.categories);
    if (row) { renderRecentRow(row, recentItems); paintRecent(); }
  }
  // Charge le média de fond du nœud (lazy + cache) : vidéo/image résolue par le serveur
  // selon l'ordre de priorité config. Rendu PAR-DESSUS le gradient (gardé en fallback).
  function loadCatMediaLazy(i) {
    var node = catNode(i); if (!node || !node.path || !bbwHttp()) return;   // dummy / pas http → garde le gradient
    var cm = G.catMedia || {};
    var url = "data/" + node.path + "/catmedia.json?order=" + encodeURIComponent((cm.order || []).join(","));
    // PAS de cache client : on refetch à chaque passage pour que le tirage ALÉATOIRE
    // (randomGameVideo/Background) change. Les tiers déterministes (vidéo/background
    // plateforme) renvoient le même résultat → aucun souci. Payload minime ({type,url}).
    fetch(url, { cache: "no-store" })
      .then(function (r) { return r.ok ? r.json() : null; })
      .then(function (data) {
        if (shownCat !== i || current !== "categories") return;   // sélection a changé → on jette
        renderCatMediaLive(data, cm);
      })
      .catch(function () {});
  }
  function renderCatMediaLive(data, cm) {
    var inner = $(".cat-media .rgn-inner:not(.rgn-clone)", screens.categories);
    if (!inner) return;
    clearCatMedia(inner);
    if (!data || !data.url) return;   // rien de dispo → on garde le gradient
    if (data.type === "video") {
      var v = document.createElement("video");
      v.src = data.url; v.autoplay = true; v.playsInline = true; v.preload = "auto";
      v.loop = (cm.loop !== false);
      v.muted = (cm.muted !== false) || !audioOn;   // muet si config muted, ou tant que l'audio n'est pas débloqué
      inner.appendChild(v);
      var pr = v.play(); if (pr && pr.catch) pr.catch(function () { v.muted = true; v.play().catch(function () {}); });
    } else {
      var img = document.createElement("img"); img.className = "cat-bg"; img.src = data.url; img.alt = "";
      inner.appendChild(img);
    }
  }
  // Charge le contenu lazy d'un nœud catégorie : recent + média de fond.
  function loadCatLazy(i) { loadRecentLazy(i); loadCatMediaLazy(i); }
  // Lit l'epoch serveur (bumpé à la sortie d'un jeu). S'il a changé (retour de jeu),
  // le cache des recent.json est invalidé (via la clé ?e=) et on rafraîchit la rangée
  // visible. Appelé au boot + au retour de visibilité/focus (no-op en file://).
  function refreshRecentEpoch() {
    if (!bbwHttp()) return;
    fetch("/api/recent/epoch", { cache: "no-store" })
      .then(function (r) { return r.ok ? r.json() : null; })
      .then(function (d) {
        if (!d) return;
        // Running-state heartbeat (peut changer même sans changement d'epoch :
        // l'epoch est bumpé sur les transitions, mais on lit l'état à chaque poll).
        if (typeof d.isGameRunning === "boolean") gameIsRunning = d.isGameRunning;
        if (typeof d.extractionInProgress === "boolean") extractionInProgress = d.extractionInProgress;
        if (d.epoch != null && d.epoch !== recentEpoch) {
          recentEpoch = d.epoch;
          if (current === "categories") loadRecentLazy(shownCat);
        }
        // Store install state changed somewhere → refresh the currently-shown game's
        // pill/button (the selected store game also has its own faster poll; this is a
        // cheap backstop and keeps a just-changed badge in sync).
        if (d.installEpoch != null && d.installEpoch !== installEpoch) {
          installEpoch = d.installEpoch;
          refreshInstallUi(currentGame);
        }
      })
      .catch(function () {});
  }
  // Polling périodique : 2 s. Garde le state running synchronisé avec le serveur
  // sans dépendre des events focus/visibility. Boot armé après le 1er
  // refreshRecentEpoch (cf. DOMContentLoaded).
  function startEpochPolling() {
    if (epochPollTimer) return;
    epochPollTimer = setInterval(refreshRecentEpoch, 2000);
  }
  function fillCatAll(i) {
    var root = screens.categories;
    fillCatDetailInner($(".cat-detail .rgn-inner", root), i);
    fillCatMediaInner($(".cat-media .rgn-inner", root), i);
    fillCatRecentInner($(".recent-clip .rgn-inner", root), i);
  }

  // Programme la MAJ du contenu : surbrillance instantanée (déjà faite),
  // contenu après le délai d'attente (dwell), via transitions par zone si activées.
  function scheduleCatContent(toIdx, instant) {
    if (catTimer) { clearTimeout(catTimer); catTimer = null; }
    var t = G.contentTransition;
    if (instant || !t.enabled || t.durationMs <= 0) {
      fillCatAll(toIdx); shownCat = toIdx; if (current === "categories") descPlay();
      loadCatLazy(toIdx);
      return;
    }
    catTimer = setTimeout(function () {
      catTimer = null;
      if (shownCat !== toIdx) doCatTransition(toIdx, toIdx > shownCat ? 1 : -1);  // → loadCatLazy à l'intérieur
      else loadCatLazy(toIdx);   // déjà affiché (pas de transition) → charge quand même
    }, t.dwellMs);
  }

  // Transition générique d'une zone clippée.
  // type: "slide-v" | "slide-h" | "fade" · dir: +1 bas / -1 haut.
  // hsign (slide-h) : +1 → nouveau entre par la DROITE ; -1 → par la GAUCHE.
  function transitionRegion(region, type, dir, fillFn, freezeMid, hsign) {
    var inner = $(".rgn-inner", region); if (!inner) return;
    if (!freezeMid && (!G.contentTransition.enabled || G.contentTransition.durationMs <= 0)) { fillFn(inner); return; }
    region.querySelectorAll(".rgn-clone").forEach(function (n) { n.parentNode.removeChild(n); });
    var old = inner.cloneNode(true); old.classList.add("rgn-clone");
    // Le clone (ancien contenu qui sort) ne doit PAS relancer le téléchargement de
    // la vidéo : on retire son src (le poster/capture reste affiché pendant la sortie).
    if (old.querySelectorAll) { var ov = old.querySelectorAll("video"); for (var oi = 0; oi < ov.length; oi++) { try { ov[oi].removeAttribute("src"); ov[oi].load(); } catch (e) {} } }
    region.appendChild(old);
    fillFn(inner);                                   // nouveau contenu dans la couche vive
    var dur = G.contentTransition.durationMs;
    var W = region.offsetWidth, H = region.offsetHeight;
    var fade = (type === "fade"), horiz = (type === "slide-h"), axis = horiz ? "X" : "Y";
    var s = hsign || 1;
    var newStart = horiz ? (s * W) : (dir > 0 ? -H : H);   // entrée du nouveau
    var oldEnd   = horiz ? (-s * W) : (dir > 0 ? H : -H);  // sortie de l'ancien
    function T(el, v) { el.style.transform = "translate" + axis + "(" + v + "px)"; }
    inner.style.transition = "none"; old.style.transition = "none";
    if (fade) { inner.style.opacity = "0"; old.style.opacity = "1"; } else { T(inner, newStart); T(old, 0); }
    if (freezeMid) {                                  // vérif headless : fige à mi-course
      if (fade) { inner.style.opacity = "0.5"; old.style.opacity = "0.5"; }
      else { T(inner, newStart / 2); T(old, oldEnd / 2); }
      return;
    }
    void inner.offsetWidth;
    var prop = fade ? "opacity " : "transform ";
    inner.style.transition = prop + dur + "ms ease"; old.style.transition = prop + dur + "ms ease";
    if (fade) { inner.style.opacity = "1"; old.style.opacity = "0"; } else { T(inner, 0); T(old, oldEnd); }
    setTimeout(function () {
      if (old.parentNode) old.parentNode.removeChild(old);
      inner.style.transition = ""; inner.style.transform = ""; inner.style.opacity = "";
    }, dur + 40);
  }

  // Transition PAR ZONE :
  //  • détail  : glissement VERTICAL (sens nav)
  //  • aperçu  : glissement HORIZONTAL ; montée → nouveau par la droite, descente → inverse (hsign = -dir)
  //  • recent  : glissement HORIZONTAL, sens OPPOSÉ à l'aperçu ; montée → nouveau par la gauche (hsign = dir)
  function doCatTransition(toIdx, dir, freezeMid) {
    var root = screens.categories;
    transitionRegion($(".cat-detail", root), "slide-v", dir, function (n) { fillCatDetailInner(n, toIdx); }, freezeMid);
    transitionRegion($(".cat-media", root), "slide-h", dir, function (n) { fillCatMediaInner(n, toIdx); }, freezeMid, -dir);
    transitionRegion($(".recent-clip", root), "slide-h", dir, function (n) { fillCatRecentInner(n, toIdx); }, freezeMid, dir);
    shownCat = toIdx;
    paintRecent();
    if (!freezeMid && current === "categories") descPlay();   // (re)lance le défilement de la description
    if (!freezeMid) loadCatLazy(toIdx);   // recent + média de fond : couvre move/dwell, descente sous-menu, retour
  }

  // ── Transition par zone de la LISTE DE JEUX (même principe que les catégories) ──
  // boxart + média : glissement HORIZONTAL (montée → nouveau par la droite, hsign=-dir).
  // détail : glissement VERTICAL (sens nav). Tout s'inverse en descendant.
  function scheduleGameContent(toIdx, instant) {
    applyGameLogo(toIdx);       // zone clear-logo du jeu (haut de la roue) : MAJ immédiate, hors transition
    cancelGameContentLoads();   // coupe le LOURD précédent (les vignettes dégradées NE sont PAS annulées)
    applyFanart(toIdx);         // fond fanart (token frais) : posé si déjà chargé, sinon nettoyé en attendant le détail
    scheduleHeavy(toIdx);       // version complète + vidéo après le palier (~1 s)
    prefetchThumbWindow(toIdx); // précharge la fenêtre de vignettes dégradées (idle)
    if (gameTimer) { clearTimeout(gameTimer); gameTimer = null; }
    var t = G.contentTransition;
    if (instant || !t.enabled || t.durationMs <= 0 || window.BBW.isMobile()) {
      fillGamePanel(screens.games, toIdx); shownGame = toIdx;   // dégradée + texte tout de suite
      if (current === "games") descPlay();
      return;
    }
    gameTimer = setTimeout(function () {
      gameTimer = null;
      if (shownGame === toIdx) return;
      doGameTransition(toIdx, toIdx > shownGame ? 1 : -1);   // +1 = bas, -1 = haut
    }, t.dwellMs);
  }
  function doGameTransition(toIdx, dir, freezeMid) {
    var root = screens.games;
    transitionRegion($(".boxart", root), "slide-h", dir, function (n) { fillGameBoxartInner(n, toIdx); }, freezeMid, -dir);
    transitionRegion($(".detail", root), "slide-v", dir, function (n) { fillGameDetailInner(n, toIdx); }, freezeMid);
    transitionRegion($(".media", root), "slide-h", dir, function (n) { fillGameMediaInner(n, toIdx); }, freezeMid, -dir);
    shownGame = toIdx;
    if (!freezeMid && current === "games") descPlay();
  }

  // ── Navigation récursive des catégories (pile) ──────────────────────────
  // La liste de gauche est re-rendue à chaque niveau ; handlers délégués (une fois).
  function setupCatList() {
    var list = $(".list", screens.categories);
    list.addEventListener("mouseover", function (e) {
      if (current !== "categories" || zone !== "list" || window.BBW.isTouch() || !mouseActive) return;
      var it = e.target.closest && e.target.closest(".list-item"); if (!it) return;
      var i = +it.dataset.i;
      lastDir = (i > curFrame().sel) ? 1 : (i < curFrame().sel ? -1 : lastDir);
      setSelected("categories", i, {});
    });
    list.addEventListener("click", function (e) {
      if (current !== "categories") return;
      var it = e.target.closest && e.target.closest(".list-item"); if (!it) return;
      var i = +it.dataset.i; zone = "list";
      lastDir = (i > curFrame().sel) ? 1 : (i < curFrame().sel ? -1 : lastDir);
      setSelected("categories", i, { instant: true }); descend();
    });
  }
  function renderCatList() {
    var list = $(".list", screens.categories);
    var scroll = $(".list-scroll", list);
    if (!scroll) { scroll = document.createElement("div"); scroll.className = "list-scroll"; list.appendChild(scroll); }
    var hl = $(".list-highlight", scroll);
    scroll.innerHTML = "";
    if (!hl) { hl = document.createElement("div"); hl.className = "list-highlight"; }
    scroll.appendChild(hl);
    var catNodes = curNodes();
    catNodes.forEach(function (node, i) {
      var d = document.createElement("div"); d.className = "list-item"; d.textContent = node.name; d.dataset.i = i;
      scroll.appendChild(d);
    });
    applyCompactWheel("categories", catNodes.length);
    listScrollY.categories = 0; scroll.classList.add("instant"); scroll.style.transform = "translateY(0)";
  }
  function setCatHighlight(i, instant) {
    screens.categories.querySelectorAll(".list .list-item").forEach(function (it, k) { it.classList.toggle("selected", k === i); });
    positionHighlight("categories", instant);
    scrollListIntoView("categories", instant);
    labelScrollPlay("categories");   // marquee du label (entrée/retour de niveau)
  }
  // Descendre d'un niveau : liste swappée instantanément, contenu glissé dans le sens
  // du dernier mouvement (même transition que ↑/↓).
  function enterCatLevel(nodes, dir) {
    if (catTimer) { clearTimeout(catTimer); catTimer = null; }
    catStack.push({ nodes: nodes, sel: 0, enterDir: dir });
    renderCatList();
    if (isWheel("categories")) { setWheelSelected("categories", 0); scrollListIntoView("categories", true); }
    else setCatHighlight(0, true);
    // contenu : transition par zone en desktop ET tablette (en phone le contenu est masqué)
    if (!window.BBW.isMobile()) doCatTransition(0, dir, false);
  }
  function catBack() {
    if (catStack.length > 1) {
      var leaving = catStack.pop();
      renderCatList();
      if (isWheel("categories")) { setWheelSelected("categories", curFrame().sel); scrollListIntoView("categories", true); }
      else setCatHighlight(curFrame().sel, true);
      if (!window.BBW.isMobile()) doCatTransition(curFrame().sel, -(leaving.enterDir || 1), false);
    } else {
      navTo("system", false);   // racine → menu
    }
  }

  // ── Menu d'actions de la page jeu : sous-menus récursifs (pile menuStack) ──
  // Handlers délégués (une fois) ; la liste est re-rendue à chaque niveau.
  function setupDetailList() {
    var list = $(".list", screens.details);
    list.addEventListener("mouseover", function (e) {
      if (current !== "details" || window.BBW.isTouch() || !mouseActive) return;
      var it = e.target.closest && e.target.closest(".list-item"); if (!it) return;
      setSelected("details", +it.dataset.i, {});
    });
    list.addEventListener("click", function (e) {
      if (current !== "details") return;
      var it = e.target.closest && e.target.closest(".list-item"); if (!it) return;
      setSelected("details", +it.dataset.i, { instant: true }); detailDescend();
    });
  }
  function renderDetailMenu(dir) {
    var list = $(".list", screens.details);
    var scroll = $(".list-scroll", list);
    if (!scroll) { scroll = document.createElement("div"); scroll.className = "list-scroll"; list.appendChild(scroll); }
    var hl = $(".list-highlight", scroll);
    scroll.innerHTML = "";
    if (!hl) { hl = document.createElement("div"); hl.className = "list-highlight"; }
    scroll.appendChild(hl);
    var detailItems = detailLevel().items;
    detailItems.forEach(function (it, i) {
      var d = document.createElement("div"); d.className = "list-item"; d.dataset.i = i;
      // libellé dynamique pour la note : "Star Rating: None" / "Star Rating: 4.5"
      var label = (it.action === "rating") ? "Star Rating: " + ratingText(userRatings[currentGame] || 0) : it.label;
      d.innerHTML = (it.children && it.children.length)
        ? label + ' <span class="menu-arrow">▶</span>'   // flèche = ouvre un sous-menu
        : label;
      scroll.appendChild(d);
    });
    applyCompactWheel("details", detailItems.length);
    // .has-rom-menu : marque le screen quand le niveau courant est le sous-menu
    // Select ROM (frame.isRomMenu). Active le rom-rail-trigger (mouse) ; la
    // condition « pressLeft → openRomRail » est gardée par _inRomMenu().
    var screen = screens.details;
    if (screen) screen.classList.toggle("has-rom-menu", _inRomMenu());
    // Si on quitte le sous-menu ROM avec le rail encore ouvert, on le ferme.
    if (!_inRomMenu() && zone === "rail" && screen && screen.classList.contains("rom-rail-open")) exitRomRail();
    // Idem pour le clavier de recherche ROM : il devient orphelin si on quitte le sous-menu.
    if (!_inRomMenu() && searchOpen && kbMode === "rom") closeSearch();
    listScrollY.details = 0; scroll.classList.add("instant"); scroll.style.transform = "translateY(0)";
    if (dir) slideDetailList(dir);   // glissement horizontal de drill-in/back
    adaptDetailListWidth();           // élargit/rétrécit le menu pour absorber les labels longs (max 50vw)
  }

  // Adapte la largeur de la colonne menu sur l'écran Details quand on est
  // dans UN sous-menu (profondeur ≥ 1) — pour absorber les labels longs des
  // sous-menus Emulator (« RetroArch (swanstation_libretro) », …), Select
  // Version et Select ROM. Le menu racine reste à 250px même si le libellé
  // Play personnalisé (« Play <emu> (default) ») est long.
  //
  // Implémentation : on pilote la CSS variable `--bbw-details-list-w` posée
  // sur le screen-details root. La feuille de styles l'utilise pour calculer
  // .list.width, .boxart-wrap.left et .detail.left → les trois régions
  // glissent ensemble (transitions .2s synchronisées).
  //
  // Cap à 50% de la largeur fenêtre. Court-circuit mobile.
  function adaptDetailListWidth() {
    try {
      var screen = screens.details; if (!screen) return;
      if (window.BBW && window.BBW.isMobile && window.BBW.isMobile()) {
        screen.style.removeProperty("--bbw-details-list-w");
        return;
      }
      // Profondeur 0 (= racine) → on revient à la valeur par défaut (250px
      // côté CSS — on retire l'inline pour que la variable reprenne sa
      // valeur initiale et que .boxart-wrap + .detail reviennent à leur
      // place). Profondeur ≥ 1 (= sous-menu) → on mesure et on élargit.
      if (!menuStack || menuStack.length <= 1) {
        screen.style.removeProperty("--bbw-details-list-w");
        return;
      }
      var lvl = detailLevel();

      var items = (lvl && lvl.items) || [];
      var DEFAULT_WIDTH = 250;

      // Canvas une fois (lazy) — la mesure est précise pour le font UI courant.
      var canvas = adaptDetailListWidth._canvas || (adaptDetailListWidth._canvas = document.createElement("canvas"));
      var ctx = canvas.getContext("2d");
      // Family héritée du body. Taille = celle effectivement appliquée aux items
      // du menu détail (config: lists.itemFontSizePx → --bbw-item-fs). Sans ça,
      // la largeur de colonne reste calibrée pour 16px et serait trop étroite /
      // trop large quand l'utilisateur change la police.
      var fam = (getComputedStyle(document.body).fontFamily || "sans-serif");
      var fsRaw = getComputedStyle(document.documentElement).getPropertyValue("--bbw-item-fs").trim();
      var fsPx = parseFloat(fsRaw); if (!fsPx || isNaN(fsPx)) fsPx = 15;
      ctx.font = fsPx + "px " + fam;

      var max = 0;
      for (var i = 0; i < items.length; i++) {
        var label = (items[i] && items[i].label) || "";
        if (items[i] && items[i].children && items[i].children.length) label += "  ▶";
        var w = ctx.measureText(label).width;
        if (w > max) max = w;
      }
      // padding interne (36) + indent sélection (8) + confort (12).
      var natural = Math.ceil(max + 36 + 8 + 12);
      var maxVw = Math.floor(window.innerWidth * 0.5);
      var target = Math.min(maxVw, Math.max(DEFAULT_WIDTH, natural));
      // The Select-ROM submenu can hold the FULL list incl. very long hack names AND shows the
      // rominfo overlay to its right. Cap its width so the overlay always keeps room (long names
      // ellipsis via CSS), and never emit a non-finite/0 value — that would invalidate the overlay's
      // calc() width and collapse the iframe to its intrinsic 300px (looks like a broken render).
      if (lvl && lvl.isRomMenu) target = Math.min(target, 560);
      if (!isFinite(target) || target <= 0) target = DEFAULT_WIDTH;
      screen.style.setProperty("--bbw-details-list-w", target + "px");
    } catch (_) { /* silencieux — un échec de mesure ne doit pas casser le rendu */ }
  }
  // Glissement horizontal léger : drill-in (dir>0) entre par la droite, back par la gauche.
  function slideDetailList(dir) {
    if (window.BBW.isMobile()) return;   // phone : colonne, pas de glissement latéral
    var scroll = $(".list-scroll", screens.details); if (!scroll) return;
    scroll.style.transition = "none";
    scroll.style.transform = "translateX(" + (dir > 0 ? 30 : -30) + "px)"; scroll.style.opacity = "0";
    void scroll.offsetWidth;
    scroll.style.transition = "transform .2s ease, opacity .2s ease";
    scroll.style.transform = "translateX(0)"; scroll.style.opacity = "1";
    setTimeout(function () { scroll.style.transition = ""; scroll.style.opacity = ""; }, 240);
  }
  // Le jeu a-t-il des tags VNDB ? (au moins un groupe non vide.)
  function hasVndb(v) { return !!(v && ((v.cont && v.cont.length) || (v.tech && v.tech.length) || (v.ero && v.ero.length))); }
  // ── État "version sélectionnée" par jeu (persisté en localStorage par-session) ──
  // Sémantique : la version "Selected" remplace le ROM lancé par celui de la version.
  // Distinct des Discs (multi-disc d'un même release) — qui restent passés à PlayGame
  // comme `additionalAppId` au sens LB-natif.
  // Stocké comme map { gameId → version.appId }. Le défaut (`isDefault`) n'est PAS
  // mémorisé : il est l'absence d'entrée ; on émet alors pas de additionalAppId dans
  // le POST launch et LB fait son comportement standard.
  var SELECTED_VERSIONS_KEY = "bbw.selectedVersions";
  function selectedVersionsLoad() {
    try { return JSON.parse(localStorage.getItem(SELECTED_VERSIONS_KEY) || "{}") || {}; }
    catch (_) { return {}; }
  }
  function selectedVersionsSave(map) {
    try { localStorage.setItem(SELECTED_VERSIONS_KEY, JSON.stringify(map || {})); } catch (_) {}
  }
  // Retourne l'appId courant pour ce jeu, ou null si la default est active.
  function getSelectedVersionAppId(g) {
    if (!g || !g.id) return null;
    var map = selectedVersionsLoad();
    var stored = map[g.id];
    if (!stored) return null;
    // Si la version stockée n'existe plus côté DTO (refresh), purge.
    var vs = (g.launchOptions && g.launchOptions.versions) || [];
    for (var i = 0; i < vs.length; i++) if (vs[i].appId === stored) return stored;
    delete map[g.id]; selectedVersionsSave(map);
    return null;
  }
  function setSelectedVersionAppId(g, appId) {
    if (!g || !g.id) return;
    var map = selectedVersionsLoad();
    if (appId == null) delete map[g.id];
    else map[g.id] = appId;
    selectedVersionsSave(map);
  }
  // Helpers pour retrouver l'objet version courant + le label affiché.
  function findVersion(g, appId) {
    var vs = (g && g.launchOptions && g.launchOptions.versions) || [];
    for (var i = 0; i < vs.length; i++) if (vs[i].appId === appId) return vs[i];
    return null;
  }
  function findDefaultVersion(g) {
    var vs = (g && g.launchOptions && g.launchOptions.versions) || [];
    for (var i = 0; i < vs.length; i++) if (vs[i].isDefault) return vs[i];
    return null;
  }

  // ── Selected-emulator state ───────────────────────────────────────────────
  // Play is a direct-launch leaf; the emulator is chosen via a separate
  // "Emulator" sub-menu that only SELECTS (no launch). Persisted per game.
  // Default resolution: explicit pick → last launched (server lastLaunch) →
  // game default → first. selectedEmuAutoExtract drives ROM-menu visibility.
  var SELECTED_EMU_KEY = "bbw.selectedEmu";
  function selEmuLoad() {
    try { return JSON.parse(localStorage.getItem(SELECTED_EMU_KEY) || "{}") || {}; }
    catch (_) { return {}; }
  }
  function selEmuSave(map) {
    try { localStorage.setItem(SELECTED_EMU_KEY, JSON.stringify(map || {})); } catch (_) {}
  }
  function emusOf(g) { return (g && g.launchOptions && g.launchOptions.emulators) || []; }
  function findEmu(g, id) {
    var es = emusOf(g);
    for (var i = 0; i < es.length; i++) if (es[i].id === id) return es[i];
    return null;
  }
  // Sentinelle « lancement direct » : stockée comme pick explicite quand l'utilisateur
  // choisit l'entrée « Launch <exe> » d'un jeu sans émulateur par défaut (exécutable pur).
  var BBW_DIRECT = "__direct__";

  // Défaut EFFECTIF pour la sélection courante : l'émulateur PROPRE de la version
  // sélectionnée (quand elle lance via un ému), sinon celui du jeu. Null = lancement
  // DIRECT (exécutable pur). Miroir de LiteBox DefaultEmuIdForSelection.
  function effectiveDefaultEmuId(g) {
    var selVerId = getSelectedVersionAppId(g);
    var selVer = selVerId ? findVersion(g, selVerId) : null;
    if (selVer) {
      // Tri-état : useEmulator === false → la version est un exécutable pur, son défaut
      // est le lancement DIRECT (pas l'émulateur du jeu). DTO ancien (champ absent) →
      // comportement d'héritage inchangé.
      if (selVer.useEmulator === false) return null;
      if (selVer.emulatorId && findEmu(g, selVer.emulatorId)) return selVer.emulatorId;
    }
    var es = emusOf(g);
    for (var i = 0; i < es.length; i++) if (es[i].isDefault) return es[i].id;
    return null;
  }
  // Résolution de l'ému sélectionné (pick explicite → paire historique → défaut par
  // version). Null = lancement DIRECT : soit le défaut effectif est direct (rien
  // d'assigné — les émulateurs de la plateforme restent offerts en wrappers), soit
  // l'utilisateur a re-choisi l'entrée « Launch <exe> ». Miroir LiteBox.
  function resolveEmuId(g) {
    var es = emusOf(g);
    var def = effectiveDefaultEmuId(g);
    if (!es.length) return null;   // rien d'offert → l'hôte résout (exe direct)
    var stored = selEmuLoad()[g.id];
    if (stored === BBW_DIRECT) return def === null ? null : def;  // direct explicite — valable tant que le défaut EST direct
    if (stored && findEmu(g, stored)) return stored;
    // Paire historique : re-sélectionner la DERNIÈRE version jouée restaure son
    // émulateur ; toute autre version suit son propre défaut.
    var selVerId = getSelectedVersionAppId(g) || null;
    var lastVer = (g && g.lastLaunch && g.lastLaunch.appId) || null;
    var lastEmu = g && g.lastLaunch && g.lastLaunch.emulatorId;
    if (lastEmu && selVerId === lastVer && findEmu(g, lastEmu)) return lastEmu;
    return def;
  }
  function setSelectedEmuId(g, id) {
    if (!g || !g.id) return;
    var map = selEmuLoad(); map[g.id] = id; selEmuSave(map);
  }
  function selectedEmuAutoExtract(g) {
    var e = findEmu(g, resolveEmuId(g));
    return !!(e && e.autoExtract);
  }

  // ── Reset-to-default ──────────────────────────────────────────────────────
  // Quelque chose diffère des défauts d'usine ? Version non-Base, émulateur ≠ défaut
  // effectif, pick/Clear ROM, ou une entrée d'historique qui re-seederait un override.
  // Pilote la visibilité de l'entrée « Reset to defaults » du sous-menu Play.
  function bbwHasResettable(g) {
    if (!g || !g.id || g.store) return false;
    var selVerId = getSelectedVersionAppId(g);
    if (selVerId) return true;
    if ((resolveEmuId(g) || null) !== (effectiveDefaultEmuId(g) || null)) return true;
    var selVer = selVerId ? findVersion(g, selVerId) : null;
    if (getSelectedRomFor(g, selVer) || getRomForce(g, selVer)) return true;
    var ll = g.lastLaunch || {};
    return !!(ll.appId || ll.emulatorId || ll.archiveEntry);
  }
  // Restaure Base + défauts + aucun pick ROM, ET annule l'entrée d'historique côté
  // serveur (launch-history.db) plus les picks persistés côté client — re-sélectionner
  // le jeu réaffiche l'état par défaut pur, ici comme dans LiteBox / LaunchBox-Web.
  function bbwResetToDefaults(g) {
    if (!g || !g.id) return;
    try {
      fetch("/bigbox/api/games/" + encodeURIComponent(String(g.id)) + "/resethistory", { method: "POST" })
        .catch(function () {});
    } catch (_) {}
    var em = selEmuLoad(); delete em[g.id]; selEmuSave(em);
    setSelectedVersionAppId(g, null);
    var rm = selectedRomsLoad(); delete rm[g.id]; selectedRomsSave(rm);
    var fm = romForceLoad(); delete fm[g.id]; romForceSave(fm);
    g.lastLaunch = null;   // in-memory — le prochain detail.json n'en aura plus non plus
  }

  // ── Lazy Select ROM submenu opener ──────────────────────────────
  //
  // Appelée par detailDescend quand l'utilisateur clique sur "Select
  // ROM". On fetch /bigbox/api/games/{id}/archive-entries[?appId=…] —
  // l'endpoint cache déjà ses résultats par md5(path|size), donc les
  // ouvertures suivantes du même archive sont instantanées. À la
  // réception, on construit dynamiquement le sous-menu et on descend.
  // En cas d'erreur ou de liste vide, on log + on ne descend pas
  // (l'utilisateur revient au sous-menu Play sans surprise).
  // Map un entry brut (du JSON serveur) vers un menu item rendable par
  // renderDetailMenu. Extrait pour pouvoir re-mapper depuis applyRomFilters
  // après un changement de filtre client.
  function _romEntryToMenuItem(e, selRom, dup) {
    var name = e.fileName || "";
    var key  = e.pathInArchive || name;          // identity = in-archive path (fallback basename)
    var active = !!(selRom && selRom === key);
    var lead = active ? "● " : "  ";
    // Marqueurs CUMULÉS à GAUCHE, dans l'ordre : ↻ (dernière lancée) ★ (favori) 🏆 (RetroAchievements).
    var glyphs = "";
    if (e.isLastPlayed)      glyphs += "↻ ";
    if (e.isFavorite)        glyphs += "★ ";
    if (e.retroAchievements) glyphs += "🏆 ";
    return {
      // label stays the basename, except when the basename is duplicated in this archive → show the path.
      label: lead + glyphs + ((dup && dup[name]) ? key : name) + "  (" + _formatBytes(e.size) + ")",
      action: "select_rom",
      fileName: key,                              // value is the in-archive path; field name kept for callers
    };
  }

  // Liste ROM BigBox (alignée sur LB Web buildLbRomSubmenu, mais SANS cap) :
  // 1 dernière ROM lancée (↻), puis TOUS les favoris (★), puis TOUTES les autres
  // par score. Dédupliquée. Pas de « More… » : cette vue EST déjà la liste avancée
  // (le rail search/advanced filtre/trie par-dessus). Marqueurs ↻ ★ 🏆 cumulés à
  // gauche par _romEntryToMenuItem.
  function buildRomQuickItems(entries, selRom, g) {
    var out = [];
    var seen = {};
    var dup = {}; (function () { var c = {}; entries.forEach(function (x) { var n = x.fileName || ""; c[n] = (c[n] || 0) + 1; if (c[n] > 1) dup[n] = 1; }); })();
    var push = function (e) {
      if (!e) return;
      var key = e.pathInArchive || e.fileName || "";
      if (!key || seen[key]) return;
      seen[key] = 1;
      out.push(_romEntryToMenuItem(e, selRom, dup));
    };
    // 1. dernière ROM lancée (lastLaunch, sinon la + récemment jouée).
    var lastName = (g && g.lastLaunch && g.lastLaunch.archiveEntry) || null, lastEntry = null;
    if (lastName) { for (var i = 0; i < entries.length; i++) { if ((entries[i].pathInArchive || entries[i].fileName) === lastName || entries[i].fileName === lastName) { lastEntry = entries[i]; break; } } }
    if (!lastEntry) { for (var j = 0; j < entries.length; j++) { if (entries[j].isLastPlayed) { lastEntry = entries[j]; break; } } }
    push(lastEntry);
    // 2. tous les favoris.
    for (var f = 0; f < entries.length; f++) { if (entries[f].isFavorite) push(entries[f]); }
    // 3. TOUTES les autres, par score (PAS de cap : la vue ROM de BigBox EST déjà la liste avancée,
    //    il n'y a pas de « More… »). On ne saute PAS les recently-played non épinglées — sinon une
    //    recently-played haut-score (ex. le pick RA) manquerait ; seuls les favoris (déjà affichés
    //    au-dessus) et les entrées déjà vues (`seen`) sont exclus.
    for (var p = 0; p < entries.length; p++) {
      var e = entries[p];
      if (e.isFavorite) continue;
      if (seen[e.pathInArchive || e.fileName || ""]) continue;
      push(e);
    }
    return out;
  }
  function openSelectRomMenu() {
    var g = DATA.games[currentGame]; if (!g) return;
    var selVerId = getSelectedVersionAppId(g);
    var selVer = selVerId ? findVersion(g, selVerId) : null;
    var qs = selVer ? ("?appId=" + encodeURIComponent(selVer.appId)) : "";
    var url = "/bigbox/api/games/" + encodeURIComponent(g.id) + "/archive-entries" + qs;
    fetch(url).then(function (r) { return r.json(); }).then(function (res) {
      if (!res || !res.ok) { console.warn("[ArchiveMGS] archive-entries failed:", res && res.reason); return; }
      var entries = res.entries || [];
      if (!entries.length) { console.warn("[ArchiveMGS] archive has no playable entries"); return; }
      var selRom = getSelectedRomFor(g, selVer);
      // Vue par défaut = sélection rapide (1 dernière + favoris + 7 prio).
      // La liste complète reste accessible via le rail (search/advanced) :
      // dès qu'un filtre est posé, applyRomFilters affiche la liste complète.
      var children = buildRomQuickItems(entries, selRom, g);
      children.unshift({ label: "✕ Clear", action: "select_rom", fileName: "" });   // réinitialise (→ résolution auto)
      // Mark this frame so setSelected can route the archive-metadata
      // overlay updates to it (the metadata depends on the highlighted
      // ROM entry — refetches as the user moves the selection).
      // entries (raw) + selRom (id) gardés sur la frame pour que applyRomFilters
      // puisse re-mapper après un toggle de filtre (fav/recent/search/adv).
      menuStack.push({
        items: children, sel: 0, isRomMenu: true,
        archiveAppId: selVer ? selVer.appId : null,
        entries: entries, selRom: selRom,
        romFilters: { fav: false, recent: false, query: "" },
      });
      renderDetailMenu(1);
      setSelected("details", 0, { instant: true });
    }).catch(function (err) { console.warn("[ArchiveMGS] archive-entries error:", err); });
  }

  // ── Archive metadata overlay (Select ROM right pane) ──────────────
  //
  // Fetches the rendered HTML for the highlighted archive entry from
  // /bigbox/api/games/{id}/archive-metadata and injects it into the
  // .archive-meta element (positioned over .detail in styles.css).
  // Cached per (gameId, appId, entry) in-memory so navigating up/down
  // in the submenu is instant on the second pass.
  //
  // The overlay is hidden via setArchiveMetaVisible(false) whenever we
  // leave the rom submenu (detailBack, screen exit), and re-shown on
  // re-entry.
  var _archiveMetaCache = Object.create(null);    // "gameId|appId|entry" → html | "" (empty = no meta)
  var _archiveMetaInflight = null;                // last fetch's AbortController so we cancel stale ones
  function setArchiveMetaVisible(on) {
    var screen = screens.details; if (!screen) return;
    screen.classList.toggle("has-archive-meta", !!on);
    if (!on) {
      // Don't clobber innerHTML immediately — keep the previous render so
      // the next show is instant. We clear only when the data changes.
    }
  }
  function _archiveMetaSetHtml(html, entry) {
    var el = $(".archive-meta", screens.details);
    if (!el) {
      if (!_archiveMetaSetHtml._warned) {
        console.warn("[ArchiveMGS] .archive-meta iframe not found in screens.details — F5 the page to pick up the latest index.html.");
        _archiveMetaSetHtml._warned = true;
      }
      return;
    }
    // Composite key (entry + first 32 chars of html) — guards against
    // re-rendering the iframe when the user pauses on the same row.
    var key = (entry || "") + "|" + (html ? html.length : 0);
    if (el.dataset.lastKey === key) return;
    el.dataset.lastKey = key;

    // Inject window.SELECTED_ROM so the user's template can read the
    // currently-highlighted entry's filename without depending on a
    // location.hash convention. Also keeps location.hash set the same
    // way for templates that already expect it (typical ACM / BBP).
    // We inject right after <head> when possible, otherwise just before
    // <body>'s content, otherwise at the very top.
    var safeEntry = JSON.stringify(entry || "");
    var injection = "<script>window.SELECTED_ROM=" + safeEntry + ";try{window.location.hash=encodeURIComponent(window.SELECTED_ROM);}catch(_){}</script>";
    var src = html || "";
    if (/<head[^>]*>/i.test(src))      src = src.replace(/<head([^>]*)>/i, "<head$1>" + injection);
    else if (/<body[^>]*>/i.test(src)) src = src.replace(/<body([^>]*)>/i, "<body$1>" + injection);
    else                                src = injection + src;

    el.srcdoc = src;
  }
  function updateArchiveMetaOverlay() {
    var screen = screens.details; if (!screen) return;
    if (!menuStack || menuStack.length <= 1 || !detailLevel().isRomMenu) {
      setArchiveMetaVisible(false);
      return;
    }
    var g = DATA.games[currentGame]; if (!g) { setArchiveMetaVisible(false); return; }
    var lvl = detailLevel();
    var item = lvl.items[lvl.sel];
    var entry = (item && item.fileName) || "";
    var appId = lvl.archiveAppId || "";
    var key = g.id + "|" + appId + "|" + entry;
    var cached = _archiveMetaCache[key];
    if (cached != null) {
      if (cached) { _archiveMetaSetHtml(cached, entry); setArchiveMetaVisible(true); }
      else setArchiveMetaVisible(false);
      return;
    }
    // Fetch — cancel any previous in-flight to avoid out-of-order writes.
    try { if (_archiveMetaInflight) _archiveMetaInflight.abort(); } catch (_) {}
    var ac = (typeof AbortController !== "undefined") ? new AbortController() : null;
    _archiveMetaInflight = ac;
    var url = "/bigbox/api/games/" + encodeURIComponent(g.id) + "/archive-metadata"
            + "?entry=" + encodeURIComponent(entry)
            + (appId ? "&appId=" + encodeURIComponent(appId) : "");
    fetch(url, ac ? { signal: ac.signal } : undefined)
      .then(function (r) { return r.json(); })
      .then(function (res) {
        if (res && res.ok && typeof res.html === "string" && res.html.length > 0) {
          _archiveMetaCache[key] = res.html;
          // Only apply if we're still on the same row — user may have
          // moved during the request.
          var lvl2 = detailLevel();
          if (lvl2 && lvl2.isRomMenu && lvl2.items[lvl2.sel] && lvl2.items[lvl2.sel].fileName === entry) {
            _archiveMetaSetHtml(res.html, entry);
            setArchiveMetaVisible(true);
          }
        } else {
          _archiveMetaCache[key] = "";   // memo "no meta" so we don't refetch
          var lvl3 = detailLevel();
          if (lvl3 && lvl3.isRomMenu && lvl3.items[lvl3.sel] && lvl3.items[lvl3.sel].fileName === entry)
            setArchiveMetaVisible(false);
        }
      })
      .catch(function (err) {
        if (err && err.name === "AbortError") return;
        console.warn("[ArchiveMGS] archive-metadata error:", err);
      });
  }
  function _formatBytes(b) {
    if (b == null || b <= 0) return "?";
    if (b < 1024) return b + " B";
    if (b < 1024 * 1024) return (b / 1024).toFixed(1) + " KB";
    if (b < 1024 * 1024 * 1024) return (b / 1024 / 1024).toFixed(1) + " MB";
    return (b / 1024 / 1024 / 1024).toFixed(2) + " GB";
  }

  // ── ROM-in-archive selection (per game + per version) ─────────────
  //
  // Storage shape : { gameId → { versionKey → entryFileName } }
  //   versionKey = appId of the selected version, or "__default__" when
  //                no version override (launching the game's main path).
  //
  // The choice persists across sessions. When the user changes version,
  // the previous version's ROM choice stays — switching back restores it.
  // setSelectedRomFor(.., null) clears the entry for that (game, version).
  var SELECTED_ROMS_KEY = "bbw.selectedArchiveRoms";
  function selectedRomsLoad() {
    try { return JSON.parse(localStorage.getItem(SELECTED_ROMS_KEY) || "{}") || {}; }
    catch (_) { return {}; }
  }
  function selectedRomsSave(map) {
    try { localStorage.setItem(SELECTED_ROMS_KEY, JSON.stringify(map || {})); } catch (_) {}
  }
  function _romVersionKey(selVer) { return selVer ? selVer.appId : "__default__"; }
  /* « Force priority » (web ROM "Clear") par (jeu, version) : le lancement envoie
     forcePriority (le plugin ignore le dernier-joué → pure priorité) et on
     supprime la ré-injection de la ROM depuis lastLaunch. */
  var ROM_FORCE_KEY = "bbw.romForce";
  function romForceLoad() { try { return JSON.parse(localStorage.getItem(ROM_FORCE_KEY) || "{}") || {}; } catch (_) { return {}; } }
  function romForceSave(m) { try { localStorage.setItem(ROM_FORCE_KEY, JSON.stringify(m || {})); } catch (_) {} }
  function getRomForce(g, selVer) { if (!g || !g.id) return false; var pg = romForceLoad()[g.id]; return !!(pg && pg[_romVersionKey(selVer)]); }
  function setRomForce(g, selVer, on) {
    if (!g || !g.id) return;
    var m = romForceLoad(), key = _romVersionKey(selVer);
    if (on) { if (!m[g.id]) m[g.id] = {}; m[g.id][key] = true; }
    else if (m[g.id]) { delete m[g.id][key]; if (!Object.keys(m[g.id]).length) delete m[g.id]; }
    romForceSave(m);
  }
  function getSelectedRomFor(g, selVer) {
    if (!g || !g.id) return null;
    var map = selectedRomsLoad();
    var perGame = map[g.id];
    if (!perGame) return null;
    return perGame[_romVersionKey(selVer)] || null;
  }
  function setSelectedRomFor(g, selVer, fileName) {
    if (!g || !g.id) return;
    var map = selectedRomsLoad();
    var key = _romVersionKey(selVer);
    if (fileName == null || fileName === "") {
      if (map[g.id]) { delete map[g.id][key]; if (!Object.keys(map[g.id]).length) delete map[g.id]; }
    } else {
      if (!map[g.id]) map[g.id] = {};
      map[g.id][key] = fileName;
    }
    selectedRomsSave(map);
  }

  // Drapeau armé à chaque entrée sur l'écran details ; consommé une seule fois quand le
  // detail.json arrive (qui contient le champ lastLaunch). Une fois consommé, le menu
  // reste libre de bouger en in-session sans qu'un refresh ultérieur du detail.json
  // ne ré-écrase le choix in-session.
  var _pendingLastLaunchSync = false;

  // Aligne le Select Version local sur la dernière session de Play persistée côté
  // backend (LaunchHistoryDb → detail.json.lastLaunch). null/absent = revenir à la
  // version par défaut. Une version disparue (renamed, deleted) = revenir au défaut.
  function applyLastLaunchSync(g) {
    if (!g || !g.id) return;
    var last = g.lastLaunch;
    if (!last || !last.appId) { setSelectedVersionAppId(g, null); applyLastLaunchArchiveEntry(g); refreshDetailMenuVndb(); return; }
    var versions = (g.launchOptions && g.launchOptions.versions) || [];
    for (var i = 0; i < versions.length; i++) {
      if (versions[i].appId === last.appId) {
        setSelectedVersionAppId(g, versions[i].isDefault ? null : last.appId);
        applyLastLaunchArchiveEntry(g);
        refreshDetailMenuVndb();
        return;
      }
    }
    setSelectedVersionAppId(g, null);
    applyLastLaunchArchiveEntry(g);
    refreshDetailMenuVndb();
  }

  // Sync the per-(game, version) Select ROM choice from the server's
  // lastLaunch.archiveEntry. The server is authoritative for the
  // SINGLE most-recent (game, version, entry) tuple — it records every
  // launch (Plugin's LaunchHistoryDb), including LB-native flows where
  // the user never touched the Select ROM menu. localStorage acts as
  // a per-(game, version) cache + the user's pending pick between
  // "click on Select ROM > X" and "click Play"; it's NOT a source of
  // truth across page entries.
  //
  // So at page entry we always overwrite the slot keyed on the
  // currently-active version with what the server says. Other slots
  // (other versions of the same game) stay — they hold the user's
  // historical picks the server hasn't seen (server tracks only the
  // last (game, version) pair, not per-version history).
  function applyLastLaunchArchiveEntry(g) {
    if (!g || !g.id) return;
    var last = g.lastLaunch;
    var selVerId = getSelectedVersionAppId(g);
    var selVer = selVerId ? findVersion(g, selVerId) : null;
    // last.archiveEntry may be null (last launch wasn't an archive launch)
    // — in that case clear the slot too so we don't show a stale label.
    // If the user used « Clear » on this version (force-priority), DON'T re-seed
    // from lastLaunch → the selection stays cleared on re-entry.
    if (!getRomForce(g, selVer))
      setSelectedRomFor(g, selVer, last && last.archiveEntry ? last.archiveEntry : null);
  }

  // Items du menu d'actions pour le jeu courant : la base + « View VNDB Tags » si le jeu a des tags
  // + sous-menu Play (alt-emu, multi-disc, Select Version) si detail.json a fourni un launchOptions.
  function gameMenuItems() {
    var items = (DATA.detailMenu || []).slice();
    // Filtre parental : retire les actions de modification (rating / favorite)
    // quand l'utilisateur est verrouillé ET que l'option correspondante du
    // contrôle parental n'est pas cochée. L'UI cesse de proposer l'action ;
    // le serveur refuserait de toute façon (BigBoxMutationApi.IsLockedAndDenied).
    if (!canRateGame() || !canFavGame()) {
      items = items.filter(function (it) {
        if (it.action === "rating"   && !canRateGame()) return false;
        if (it.action === "favorite" && !canFavGame())  return false;
        return true;
      });
    }
    var g = DATA.games[currentGame];
    if (g && hasVndb(g.vndb)) items.push({ label: "View VNDB Tags", action: "vndbtags" });
    // Entrée "View on ... DB" :
    //   • INTERNE (iframe /games/{lbdbid}.html) si la web-db locale est active ET
    //     qu'elle peut servir ce jeu : base étendue active (tous les id), OU id
    //     dans le range LaunchBox (< 1e6) que la base de base contient aussi.
    //   • EXTERNE sinon : web-db désactivée, OU jeu hors-LaunchBox (Steam/VNDB/SS)
    //     alors que la base étendue est désactivée (ex. jeu ajouté via la base
    //     étendue puis celle-ci désactivée → absent de la base LaunchBox locale).
    //     L'URL est déduite CÔTÉ CLIENT du range de lbdbid (voir lbDbExternal()).
    if (g && g.lbdbid > 0) {
      if (g.webdb && (g.extdb || g.lbdbid < 1000000)) {
        items.push({ label: "View on LaunchBox DB", action: "lbdb" });
      } else {
        var ext = lbDbExternal(g.lbdbid);
        if (ext) items.push({ label: ext.label, action: "lbdbext" });
      }
    }
    // Enrichit l'entrée "Play" avec un sous-menu quand le jeu a des émulateurs alternatifs,
    // est multi-disque, ou a des versions alternatives. L'entrée parente garde action:"play" —
    // l'override de detailDescend descend dans children si présents avant de dispatcher l'action.
    if (g && g.launchOptions) {
      var lo = g.launchOptions;
      var versions = lo.versions || [];
      var selVerId = getSelectedVersionAppId(g);
      var selVer = selVerId ? findVersion(g, selVerId) : null;

      // ── Play : feuille à LANCEMENT DIRECT (pas de sous-menu). L'émulateur est
      //    choisi via un item « Emulator » séparé (plus bas) qui ne fait que
      //    SÉLECTIONNER. Label = « Play <emu> (default) » / « Play <emu> » /
      //    « Play » (aucun émulateur listé). ──
      var emus = lo.emulators || [];
      var selEmuId = resolveEmuId(g);
      var selEmu = findEmu(g, selEmuId);
      var defEmuId = effectiveDefaultEmuId(g);
      var playLabel;
      if (selEmu) {
        // « (default) » = défaut PAR SÉLECTION (une version porte son propre émulateur).
        playLabel = "Play " + selEmu.title + (selEmu.id === defEmuId ? " (default)" : "");
      } else {
        // Lancement DIRECT (aucun émulateur sélectionné) : « Launch <exe> » — ou
        // « DOSBox <exe> » quand le jeu/la version tourne sous DOSBox. « Play » nu
        // seulement quand le DTO ne fournit pas exeName (plugin plus ancien).
        var dExe = selVer ? (selVer.exeName || "") : (lo.mainExeName || "");
        var dDos = selVer ? !!selVer.useDosBox : !!lo.mainUseDosBox;
        playLabel = dExe ? ((dDos ? "DOSBox " : "Launch ") + dExe) : "Play";
      }
      var playLeaf = { label: playLabel, action: "play" };
      if (selEmuId) playLeaf.emulatorId = selEmuId;
      if (selVer) playLeaf.additionalAppId = selVer.appId;

      // Boutons de premier niveau, conditionnels, insérés juste après Play.
      var extraTop = [];

      // « Emulator » — visible seulement s'il y a un choix (≥2). Sous-menu de
      // SÉLECTION : on liste TOUS les émulateurs (défaut tagué + en tête tel que
      // fourni par le serveur), coche l'actif (résolu), et picker ne lance pas.
      if (emus.length > 1 || (emus.length > 0 && defEmuId === null)) {
        var emuChildren = [];
        // Jeu/version SANS émulateur par défaut : le LANCEMENT DIRECT prend la tête du
        // sous-menu comme défaut — les émulateurs de la plateforme restent en dessous
        // comme alternatives (wrappers/launchers). Miroir LiteBox ShowEmulatorMenu.
        if (defEmuId === null) {
          var dEx2 = selVer ? (selVer.exeName || "") : (lo.mainExeName || "");
          emuChildren.push({
            label:      (selEmuId === null ? "● " : "  ") + (dEx2 ? "Launch " + dEx2 : "Launch directly") + " (default)",
            action:     "select_emulator",
            emulatorId: BBW_DIRECT,
          });
        }
        for (var i = 0; i < emus.length; i++) {
          var e = emus[i];
          emuChildren.push({
            // « (default) » = défaut PAR SÉLECTION (une version porte son propre émulateur).
            label:      (e.id === selEmuId ? "● " : "  ") + e.title + (e.id === defEmuId ? " (default)" : ""),
            action:     "select_emulator",
            emulatorId: e.id,
          });
        }
        extraTop.push({
          label:    selEmu ? ("Emulator: " + selEmu.title) : "Emulator",
          action:   "emulator_menu",
          children: emuChildren,
        });
      }

      // « Version » — visible dès qu'il existe au moins une version alternative.
      // C'est une SÉLECTION mémorisée (select_version), elle ne lance pas.
      if (versions.length > 0) {
        // Pas de « Clear » ici : « Base : … » joue déjà ce rôle (= défaut).
        var versionChildren = [];
        for (var iv = 0; iv < versions.length; iv++) {
          var v = versions[iv];
          var isActive = selVer ? (v.appId === selVer.appId) : v.isDefault;
          versionChildren.push({
            label: (isActive ? "● " : "  ") + v.label,
            action: "select_version",
            appId: v.appId,
            isDefault: v.isDefault,
          });
        }
        extraTop.push({
          label: selVer ? ("Version: " + selVer.label) : "Version",
          action: "version_menu",
          children: versionChildren,
        });
      }

      // « Select ROM » — visible seulement si la source de lancement courante
      // (version sélectionnée, ou main path à défaut) est une archive reconnue
      // ET que l'émulateur sélectionné extrait réellement (AutoExtract ON).
      // Pour un émulateur qui lit l'archive nativement (MAME / arcade), on ne
      // peut pas lancer une ROM précise → on masque le menu ROM.
      // Chargement LAZY : action rom_menu_open → fetch + push à l'activation.
      var isCurrentArchive = (selVer ? !!selVer.isArchive : !!lo.mainPathIsArchive) && selectedEmuAutoExtract(g);
      if (isCurrentArchive) {
        var selRom = getSelectedRomFor(g, selVer);
        extraTop.push({
          label: selRom ? ("ROM: " + selRom) : "ROM",
          action: "rom_menu_open",
        });
      }

      // « Reset to defaults » — visible seulement quand la sélection (version /
      // émulateur / ROM) ou l'historique de lancement diffèrent des défauts.
      // Miroir du bouton ↺ LiteBox / LaunchBox-Web.
      if (bbwHasResettable(g)) {
        extraTop.push({ label: "Reset to defaults", action: "reset_defaults" });
      }

      // Remplace l'entrée « play » de base par la feuille à lancement direct,
      // puis insère Emulator + Version + ROM juste après.
      for (var k = 0; k < items.length; k++) {
        if (items[k] && items[k].action === "play") {
          items[k] = playLeaf;
          for (var t = 0; t < extraTop.length; t++) items.splice(k + 1 + t, 0, extraTop[t]);
          break;
        }
      }
    }
    // Store games (GOG/Steam/Epic): the main Play entry becomes "Play (<store>)"
    // when installed, or "Install on <store>" (action:install) when not. g.store /
    // g.installed arrive with detail.json (null/absent for non-store → untouched).
    if (g && g.store) {
      for (var pi = 0; pi < items.length; pi++) {
        if (items[pi] && (items[pi].action === "play" || items[pi].action === "install")) {
          if (g.installed === false) items[pi] = { label: "Install on " + g.store, action: "install" };
          else items[pi].label = "Play (" + g.store + ")";
          break;
        }
      }
    }
    return items;
  }
  function resetDetailMenu() { menuStack = [{ items: gameMenuItems(), sel: 0 }]; renderDetailMenu(0); }
  // detail.json arrivé après coup : si on est au niveau racine du menu, on le reconstruit pour
  // refléter les enrichissements (« View VNDB Tags » apparaît, sous-menu Play apparaît…).
  // Comparaison structurelle (label + action + nb d'enfants) pour ne re-rendre que si nécessaire,
  // tout en captant les changements qui ne touchent pas la longueur de la liste (ex. Play qui
  // gagne des children sans que items.length change).
  function refreshDetailMenuVndb() {
    if (current !== "details" || menuStack.length !== 1) return;
    var lvl = menuStack[0];
    var next = gameMenuItems();
    if (sameMenuShape(lvl.items, next)) return;   // rien de neuf
    lvl.items = next;
    lvl.sel = Math.min(lvl.sel, lvl.items.length - 1);
    renderDetailMenu(0); setSelected("details", lvl.sel, { instant: true });
  }
  // Reconstruit l'arbre du menu d'actions à partir de gameMenuItems() (qui capture l'état
  // courant — sélection de version, tags VNDB, etc.) et préserve la position de l'utilisateur
  // dans la pile autant que possible. Utilisé après les mutations locales qui changent la
  // forme des sous-menus (ex. select_version).
  function rebuildCurrentDetailLevel() {
    if (!menuStack || menuStack.length === 0) return;
    menuStack[0].items = gameMenuItems();
    menuStack[0].sel = Math.min(menuStack[0].sel, menuStack[0].items.length - 1);
    // Propage en bas : à chaque niveau, on suit la sélection du parent pour récupérer
    // ses nouveaux children. Si le sous-menu a disparu, on tronque la pile.
    for (var i = 1; i < menuStack.length; i++) {
      var parent = menuStack[i - 1];
      var parentItem = parent.items[parent.sel];
      if (parentItem && parentItem.children && parentItem.children.length) {
        menuStack[i].items = parentItem.children;
        menuStack[i].sel = Math.min(menuStack[i].sel, parentItem.children.length - 1);
      } else {
        menuStack.length = i;
        break;
      }
    }
    renderDetailMenu(0); setSelected("details", detailLevel().sel, { instant: true });
  }

  function sameMenuShape(a, b) {
    if (!a || !b || a.length !== b.length) return false;
    for (var i = 0; i < a.length; i++) {
      var x = a[i] || {}, y = b[i] || {};
      var xn = (x.children && x.children.length) || 0;
      var yn = (y.children && y.children.length) || 0;
      if (x.label !== y.label || x.action !== y.action || xn !== yn) return false;
    }
    return true;
  }
  function detailDescend() {
    var lvl = detailLevel(), item = lvl.items[lvl.sel];
    if (!item) return;
    // Select ROM : ouverture LAZY — fetch puis push du level.
    if (item.action === "rom_menu_open") { openSelectRomMenu(); return; }
    // Un item avec children est toujours un noeud d'arbre, indépendamment de son action :
    // on descend d'un niveau. Les actions terminales (play, rating, …) ne sont dispatchées
    // que sur les feuilles. Permet à "Play" parent de regrouper Play default + alt-emu + discs.
    if (item.children && item.children.length) {
      menuStack.push({ items: item.children, sel: 0 });
      renderDetailMenu(1); setSelected("details", 0, { instant: true });
      return;
    }
    if (item.action === "rating") { openRatingModal(); return; }   // popup de note (demi-étoiles)
    if (item.action === "unlock") {                                 // parental : verrou ↔ déverrou
      if (!parental.active) return;
      if (parental.bigBox) { openParentalInfo(); return; }          // BigBox : contrôle global, message
      if (parental.locked) openPinPad(); else lockNow();
      return;
    }
    if (item.action === "related") { openRelated(); return; }       // popup Related Games
    if (item.action === "vndbtags") { openVndbTags(); return; }      // popup Tags VNDB
    if (item.action === "lbdb") {                                    // modal iframe LB-DB (web-db interne)
      var gOpen = DATA.games[currentGame];
      if (gOpen && gOpen.lbdbid > 0) openLbDbModal(gOpen.lbdbid);
      return;
    }
    if (item.action === "lbdbext") {                                 // site externe (web-db désactivée)
      var gExt = DATA.games[currentGame];
      var ext = gExt ? lbDbExternal(gExt.lbdbid) : null;
      if (ext) { try { window.open(ext.url, "_blank", "noopener"); } catch (e) {} }
      return;
    }
    if (item.action === "favorite") { postMutation("favorite", { value: true }); return; }   // mutations (mode plugin)
    if (item.action === "hide") { postMutation("hide", { value: true }); return; }
    if (item.action === "broken") { postMutation("broken", { value: true }); return; }
    if (item.action === "install") {
      // Store game not installed → ask the server to shell-open the store's install
      // URI (goggalaxy:// / steam://install / epic ?action=install). Fire-and-forget;
      // the live poll flips the button to Play once the client reports it installed.
      if (gameIsRunning || extractionInProgress) return;
      // Parental: install gated behind the PIN while locked → show the PIN pad in
      // "install" mode. A correct PIN authorizes THIS install only (one-shot) — it does
      // NOT unlock parental globally and does NOT reload the catalog.
      if (parental.installNeedsUnlock) { pinInstallGi = currentGame; openPinPad("install"); return; }
      if (Date.now() < playBlockUntilTs) return;
      playBlockUntilTs = Date.now() + 5000;
      postMutation("install", {});
      var giIns = currentGame;
      if (installPollTimer) { clearTimeout(installPollTimer); installPollTimer = null; }
      installPollGi = giIns;
      installPollTimer = setTimeout(function () { pollInstallOnce(giIns); }, 2500);
      return;
    }
    if (item.action === "play") {
      // Anti-double-launch :
      //   • server-side : RecentState.IsGameRunning OU IsExtractionInProgress
      //     est vrai → on refuse (le MutationApi.Launch refuserait aussi côté
      //     serveur pour IsGameRunning ; l'extraction couvre la fenêtre entre
      //     le clic et OnBeforeGameLaunching → MarkRunning, qui peut être
      //     longue pour une grosse archive).
      //   • client-side : on vient de cliquer Play (< 5 s), on attend que le
      //     prochain poll d'epoch (2 s) propage le running-state. Évite la
      //     fenêtre de course entre le clic et le passage du flag serveur.
      if (gameIsRunning || extractionInProgress) {
        // Tentative pendant qu'un jeu tourne déjà ou pendant une extraction —
        // on ignore silencieusement (un toast/feedback visuel serait sympa
        // mais pas critique pour MVP).
        return;
      }
      if (Date.now() < playBlockUntilTs) {
        // Clic Play déjà effectué dans les 5 s — on bloque pour laisser le poll
        // serveur valider le running-state.
        return;
      }
      playBlockUntilTs = Date.now() + 5000;
      // Feuille du sous-menu Play : passe emulatorId / additionalAppId (s'ils sont définis)
      // au POST /bigbox/api/games/{id}/launch. Body vide ⇒ émulateur défaut + disc défaut.
      var body = {};
      if (item.emulatorId) body.emulatorId = item.emulatorId;
      if (item.additionalAppId) body.additionalAppId = item.additionalAppId;
      // Select ROM : si une ROM est cochée pour le couple (jeu, version
      // courante), on transmet le filename au plugin. Le launch côté
      // serveur arme alors ArchiveLaunchContextRegistry.SelectedEntryFileName
      // — la patch Process.Start extrait cette entrée précise (et la
      // génération m3u est suppressed).
      var gLaunch = DATA.games[currentGame];
      if (gLaunch) {
        // ROM pick only matters when the launched emulator extracts. For a
        // native-archive emulator (MAME) we send neither the ROM nor the
        // forcePriority flag — the whole archive goes to the emulator as-is.
        var launchEmu = findEmu(gLaunch, body.emulatorId || resolveEmuId(gLaunch));
        var emuExtracts = !launchEmu || !!launchEmu.autoExtract;
        if (emuExtracts) {
          var selVerIdLaunch = getSelectedVersionAppId(gLaunch);
          var selVerLaunch = selVerIdLaunch ? findVersion(gLaunch, selVerIdLaunch) : null;
          var selRomLaunch = getSelectedRomFor(gLaunch, selVerLaunch);
          if (selRomLaunch) body.archiveEntryFileName = selRomLaunch;
          else if (getRomForce(gLaunch, selVerLaunch)) body.forcePriority = true;   // ROM "Clear" → priorité pure
        }
      }
      postLaunch(body); return;
    }
    if (item.action === "select_emulator") {
      // Sélectionne cet émulateur pour le jeu courant (ne lance PAS), remonte
      // d'un niveau (= retour au menu racine) et rebuild pour rafraîchir le
      // label Play « Play <emu> (default) », « Emulator: X » et la visibilité
      // du menu ROM (qui dépend de l'AutoExtract de l'émulateur sélectionné).
      var ge = DATA.games[currentGame];
      if (!ge) return;
      setSelectedEmuId(ge, item.emulatorId);
      if (menuStack.length > 1) menuStack.pop();
      rebuildCurrentDetailLevel();
      return;
    }
    if (item.action === "select_version") {
      // Coche cette version pour le jeu courant, remonte d'un niveau (= sous-menu Play),
      // et rebuild ce niveau pour refléter le nouveau état (label "Play (X)", "Select
      // Version (X)" et coche dans le sous-menu).
      var g = DATA.games[currentGame];
      if (!g) return;
      setSelectedVersionAppId(g, item.isDefault ? null : item.appId);
      if (menuStack.length > 1) menuStack.pop();
      rebuildCurrentDetailLevel();
      return;
    }
    if (item.action === "reset_defaults") {
      // ↺ : annule l'historique serveur + purge les picks client, puis rebuild —
      // le label Play repasse au défaut et l'entrée disparaît d'elle-même.
      var gz = DATA.games[currentGame];
      if (!gz) return;
      bbwResetToDefaults(gz);
      rebuildCurrentDetailLevel();
      return;
    }
    if (item.action === "select_rom") {
      // Coche cette ROM pour le couple (jeu courant, version sélectionnée).
      // Re-toggle si la ROM cochée est cliquée → on désélectionne (revient à
      // la résolution automatique côté plugin). Remonte d'un niveau, rebuild
      // pour rafraîchir le label "Select ROM (X)" et la coche.
      var gr = DATA.games[currentGame];
      if (!gr) return;
      var selVerIdR = getSelectedVersionAppId(gr);
      var selVerR = selVerIdR ? findVersion(gr, selVerIdR) : null;
      var currentRom = getSelectedRomFor(gr, selVerR);
      var newRom = (currentRom && currentRom === item.fileName) ? null : item.fileName;
      setSelectedRomFor(gr, selVerR, newRom);
      setRomForce(gr, selVerR, item.fileName === "");   // « ✕ Clear » → force priorité ; vrai choix → annule
      if (menuStack.length > 1) menuStack.pop();
      rebuildCurrentDetailLevel();
      return;
    }
    // feuille sans handler connu — no-op silencieux.
  }

  // ── Popup note (Star Rating) : gauche/droite = ±½ étoile, A valide, B annule ──
  function ratingText(v) { return v > 0 ? String(v) : "None"; }
  function communityVotes(gi) { var g = DATA.games[gi]; if (g && g._votes != null) return g._votes; return null; }   // pas de fallback faux : tooltip ne montrera pas la ligne "votes" tant que detail.json n'est pas chargé
  function setupRatingModal() {
    ratingModalEl = $(".rating-modal");
    if (!ratingModalEl) return;
    ratingModalEl.addEventListener("click", function (e) {
      if (!ratingOpen) return;
      if (!(e.target.closest && e.target.closest(".modal-panel"))) closeRating();   // clic hors panneau = annuler
    });
    var stars = $(".rating-stars", ratingModalEl);
    if (stars) {
      stars.addEventListener("mousemove", function (e) {     // survol = aperçu de la note (½ étoile)
        if (!ratingOpen || !mouseActive) return;
        ratingTouched = true; ratingVal = ratingFromX(stars, e.clientX); renderRating();
      });
      stars.addEventListener("click", function (e) { if (ratingOpen) { ratingTouched = true; ratingVal = ratingFromX(stars, e.clientX); confirmRating(); } });
    }
  }
  function ratingFromX(stars, clientX) {
    var r = stars.getBoundingClientRect();
    var v = Math.round((clientX - r.left) / r.width * 10) / 2;   // 0..5 par pas de 0,5
    return Math.max(0, Math.min(5, v));
  }
  function openRatingModal() {
    if (!ratingModalEl) return;
    ratingOpen = true;
    var g = DATA.games[currentGame];
    var community = g ? (parseFloat(g.r) || 0) : 0;
    ratingHadUser = userRatings[currentGame] > 0;
    ratingTouched = false;
    // Défaut : note perso si définie, sinon le Community Star Rating arrondi au 0,5 le plus proche.
    ratingVal = ratingHadUser ? userRatings[currentGame] : Math.round(community * 2) / 2;
    $(".r-comm", ratingModalEl).textContent = g ? g.r : "—";
    // communityVotes peut être null tant que detail.json n'est pas chargé (cf. fallback retiré).
    var rv = communityVotes(currentGame);
    $(".r-votes", ratingModalEl).textContent = rv != null ? rv.toLocaleString() : "—";
    renderRating();
    ratingModalEl.classList.add("open");
  }
  function renderRating() {
    if (!ratingModalEl) return;
    $(".rating-stars-fill", ratingModalEl).style.width = (ratingVal / 5 * 100) + "%";
    // Les étoiles sont pré-réglées sur le community ; "Your" reste "None" tant qu'on n'a pas ajusté.
    $(".r-your", ratingModalEl).textContent = (ratingHadUser || ratingTouched) ? ratingText(ratingVal) : "None";
  }
  function ratingAdjust(d) { ratingTouched = true; ratingVal = Math.max(0, Math.min(5, ratingVal + d * 0.5)); renderRating(); }
  function confirmRating() {
    userRatings[currentGame] = ratingVal; postMutation("rating", { value: ratingVal }); closeRating();
    fillGamePanel(screens.details, currentGame);                                  // étoile d'entête
    renderDetailMenu(0); setSelected("details", detailLevel().sel, { instant: true });   // libellé "Star Rating: X"
  }
  function closeRating() { ratingOpen = false; if (ratingModalEl) ratingModalEl.classList.remove("open"); }

  // ── Popup digicode (Unlock) : pavé 3×4 ; flèches déplacent, A presse, B ferme ──
  var PIN_COLS = 3, PIN_ROWS = 4;
  function setupPinPad() {
    pinModalEl = $(".pin-modal"); if (!pinModalEl) return;
    pinKeys = Array.prototype.slice.call(pinModalEl.querySelectorAll(".pin-key"));
    pinModalEl.addEventListener("click", function (e) {
      if (!pinOpen) return;
      var key = e.target.closest && e.target.closest(".pin-key");
      if (key) { pinFocus = pinKeys.indexOf(key); paintPin(); pinPress(key.dataset.k); return; }
      if (!(e.target.closest && e.target.closest(".pin-panel"))) closePinPad();   // clic hors panneau = fermer
    });
    pinKeys.forEach(function (k, i) {
      k.addEventListener("mouseenter", function () { if (pinOpen && mouseActive) { pinFocus = i; paintPin(); } });
    });
  }
  function openPinPad(purpose) {
    if (!pinModalEl) return;
    pinPurpose = purpose || "unlock"; pinOpen = true; pinValue = ""; pinFocus = 0;
    // Explain WHY the pad is up: install is parental-blocked (enter the PIN to authorize just this install)
    // vs the plain global unlock.
    setPinMsg(pinPurpose === "install" ? "Install blocked by parental control — enter PIN to authorize." : "Enter your pin.");
    paintPin(); pinModalEl.classList.add("open");
  }
  function closePinPad() { pinOpen = false; if (pinModalEl) pinModalEl.classList.remove("open"); }
  function setPinMsg(msg) { if (pinModalEl) { var t = $(".pin-title", pinModalEl); if (t) t.textContent = msg; } }
  function paintPin() {
    if (!pinModalEl) return;
    pinKeys.forEach(function (k, i) { k.classList.toggle("focus", i === pinFocus); });
    $(".pin-display", pinModalEl).textContent = pinValue.replace(/./g, "*");
  }
  function pinPress(k) {
    if (k === "del") pinValue = pinValue.slice(0, -1);
    else if (k === "done") { submitPin(); return; }     // valide le PIN via /api/parental/unlock
    else if (pinValue.length < 8) pinValue += k;
    paintPin();
  }
  // ── Contrôle parental : état + verrou / déverrou ──────────────────────────
  function bbwHttp() { return (location.protocol === "http:" || location.protocol === "https:") && typeof fetch === "function"; }
  // Récupère l'état parental au boot (no-op en file:// / standalone). Promise → state|null.
  function fetchParental() {
    if (!bbwHttp()) return Promise.resolve(null);
    // Chemin ABSOLU : la page est sous /bigbox/ mais les routes parental sont à la
    // racine (/api/parental/*, partagées avec les pages desktop). Un chemin relatif
    // viserait /bigbox/api/parental/* → 404.
    return fetch("/api/parental/state").then(function (r) { return r.ok ? r.json() : null; }).catch(function () { return null; });
  }
  // Surveillance de l'état parental — UNIQUEMENT en mode BigBox. Là, le verrou est
  // piloté de l'EXTÉRIEUR (lock natif de BigBox sur la box) : la page n'a aucun autre
  // moyen de remarquer un re-verrouillage, donc elle re-vérifie périodiquement le
  // serveur et RECHARGE dès que l'état change (une page laissée déverrouillée, jeux
  // mature affichés, doit repasser en filtré sans attendre une navigation). Re-vérifie
  // aussi au retour de visibilité/focus (réveil tablette). Fail-closed : déverrouillé +
  // serveur injoignable → reload (sécurité).
  // En mode LaunchBox le verrou est par-client (cookie/PIN) et ne change que via les
  // actions de cette page (submitPin/lockNow), qui rechargent déjà → pas de polling.
  var _parentalFails = 0;
  function startParentalWatch(initial) {
    if (!bbwHttp()) return;
    if (!(initial && initial.bigBox)) return;   // BigBox seulement (LaunchBox = cookie auto-géré)
    var pollMs = (G.parental && G.parental.pollMs) || 4000;
    setInterval(checkParentalState, pollMs);
    document.addEventListener("visibilitychange", function () { if (!document.hidden) checkParentalState(); });
    window.addEventListener("focus", checkParentalState);
  }
  function checkParentalState() {
    if (!bbwHttp()) return;
    fetch("/api/parental/state", { cache: "no-store" })
      .then(function (r) { if (!r.ok) throw new Error("HTTP " + r.status); return r.json(); })
      .then(function (ps) {
        _parentalFails = 0;
        if (!ps) return;
        var changed = (!!ps.active !== parental.active) || (!!ps.locked !== parental.locked) ||
                      (!!ps.bigBox !== parental.bigBox) || (!!ps.canUnlock !== parental.canUnlock) ||
                      (!!ps.canRate !== parental.canRate) || (!!ps.canFav !== parental.canFav) ||
                      (!!ps.installNeedsUnlock !== parental.installNeedsUnlock);
        if (changed) location.reload();   // lock/unlock OU toggle d'une option parentale → on applique
      })
      .catch(function () {
        _parentalFails++;
        var maxFails = (G.parental && G.parental.failClosedAttempts) || 0;
        if (maxFails > 0 && parental.active && !parental.locked && _parentalFails >= maxFails) location.reload();
      });
  }
  // Soumet le PIN saisi ; succès → rechargement (le cookie côté serveur lève le filtre).
  function submitPin() {
    if (!bbwHttp()) { closePinPad(); return; }
    var pin = pinValue;
    setPinMsg("Checking…");
    if (pinPurpose === "install") {
      // One-shot install authorization: verify the PIN at the install endpoint. On success the
      // install fires and we keep browsing — parental stays locked (no global unlock, no reload).
      var gi = pinInstallGi;
      var gid = (DATA.games[gi] && DATA.games[gi].id);
      if (gid == null) { closePinPad(); return; }
      fetch("api/games/" + gid + "/install", {
        method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ pin: pin })
      }).then(function (r) { return r.json(); }).then(function (res) {
        if (res && res.ok) {
          closePinPad();
          playBlockUntilTs = Date.now() + 5000;
          if (installPollTimer) { clearTimeout(installPollTimer); installPollTimer = null; }
          installPollGi = gi;
          installPollTimer = setTimeout(function () { pollInstallOnce(gi); }, 2500);
          return;
        }
        var rsn = res && res.reason;
        if (rsn === "locked-out") setPinMsg("Locked out — restart required.");
        else if (res && typeof res.attemptsRemaining === "number") setPinMsg("Wrong PIN — " + res.attemptsRemaining + " left.");
        else setPinMsg("Wrong PIN.");
        pinValue = ""; paintPin();
      }).catch(function () { setPinMsg("Error — try again."); pinValue = ""; paintPin(); });
      return;
    }
    fetch("/api/parental/unlock", {
      method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ pin: pin })
    }).then(function (r) { return r.json(); }).then(function (res) {
      if (res && res.success) { closePinPad(); location.reload(); return; }
      var reason = res && res.reason;
      if (reason === "locked-out") setPinMsg("Locked out — restart required.");
      else if (res && typeof res.attemptsRemaining === "number") setPinMsg("Wrong PIN — " + res.attemptsRemaining + " left.");
      else if (reason === "not-allowed") setPinMsg("Unlock not available.");
      else setPinMsg("Wrong PIN.");
      pinValue = ""; paintPin();
    }).catch(function () { setPinMsg("Error — try again."); pinValue = ""; paintPin(); });
  }
  // Re-verrouille (efface le cookie) puis recharge → contenu de nouveau filtré.
  function lockNow() {
    if (!bbwHttp()) return;
    fetch("/api/parental/lock", { method: "POST" }).then(function () { location.reload(); }).catch(function () { location.reload(); });
  }
  // Ajuste l'item Verrou/Déverrou du System Menu AVANT setupList (sinon item invisible
  // dans la navigation) + le cadenas près de l'horloge. En mode BigBox l'item reste
  // visible (clic = message explicatif) ; en mode LaunchBox il n'apparaît que si un
  // PIN permet le déverrouillage.
  function applyParentalDom() {
    renderLockIndicators();
    var item = screens.system && screens.system.querySelector('[data-sys="lock"]');
    if (!item) return;
    if (!parental.active) { if (item.parentNode) item.parentNode.removeChild(item); return; }
    if (!parental.bigBox && !parental.canUnlock) { if (item.parentNode) item.parentNode.removeChild(item); return; }
    item.textContent = parental.locked ? "Unlock" : "Lock";
  }

  // ── Cadenas d'état près de l'horloge (toutes les topbars) ──────────────────
  // Rouge fermé = BigBox verrouillé · Vert fermé = LaunchBox verrouillé · Ouvert = déverrouillé.
  var LOCK_CLOSED_SVG = '<svg viewBox="0 0 24 24" width="16" height="16" aria-hidden="true"><path d="M8 10V7a4 4 0 0 1 8 0v3" fill="none" stroke="currentColor" stroke-width="2"/><rect x="5.5" y="10" width="13" height="9.5" rx="2" fill="currentColor"/></svg>';
  var LOCK_OPEN_SVG = '<svg viewBox="0 0 24 24" width="16" height="16" aria-hidden="true"><path d="M8 10V7a4 4 0 0 1 7.6-1.6" fill="none" stroke="currentColor" stroke-width="2"/><rect x="5.5" y="10" width="13" height="9.5" rx="2" fill="currentColor"/></svg>';
  // Retourne "red" | "green" | "open" | null (rien à afficher).
  function lockState() {
    if (!parental.active) return null;
    if (!parental.locked) return "open";
    return parental.bigBox ? "red" : "green";
  }
  function renderLockIndicators() {
    var st = lockState();
    var bars = document.querySelectorAll(".topbar");
    for (var i = 0; i < bars.length; i++) {
      var bar = bars[i];
      var ind = bar.querySelector(".lockind");
      if (st === null) { if (ind && ind.parentNode) ind.parentNode.removeChild(ind); continue; }
      if (!ind) {
        ind = document.createElement("span");
        var clock = bar.querySelector(".clock");
        if (clock && clock.nextSibling) bar.insertBefore(ind, clock.nextSibling);
        else if (clock) bar.appendChild(ind);
        else bar.appendChild(ind);
      }
      ind.className = "lockind " + st;
      ind.innerHTML = (st === "open") ? LOCK_OPEN_SVG : LOCK_CLOSED_SVG;
      ind.title = st === "red" ? window.BBW.t("lock.bigbox") : (st === "green" ? window.BBW.t("lock.locked") : window.BBW.t("lock.unlocked"));
    }
  }
  // Message info (mode BigBox) : le contrôle parental est géré globalement par BigBox.
  // Sélecteur précis sur data-modal="msg" depuis qu'on partage la classe .msg-modal
  // avec d'autres popups (cf. launchErr ci-dessous).
  var infoOpen = false, infoModalEl = null;
  function setupInfoModal() {
    infoModalEl = $(".msg-modal[data-modal='msg']"); if (!infoModalEl) return;
    infoModalEl.addEventListener("click", function () { closeParentalInfo(); });
  }
  function openParentalInfo() { if (!infoModalEl) return; infoOpen = true; infoModalEl.classList.add("open"); }
  function closeParentalInfo() { infoOpen = false; if (infoModalEl) infoModalEl.classList.remove("open"); }

  // Modal d'erreur de lancement (fichier introuvable, …). Ouvert par
  // postLaunch() quand le serveur répond { ok:false, reason:… }. Le titre/texte
  // sont mappés depuis i18n suivant reason ; reason inconnue → fallback sur
  // error.fileMissing (titre générique + reason brute en corps).
  var launchErrOpen = false, launchErrModalEl = null;
  function setupLaunchErrorModal() {
    launchErrModalEl = $(".msg-modal[data-modal='error']"); if (!launchErrModalEl) return;
    launchErrModalEl.addEventListener("click", function () { closeLaunchErrorModal(); });
  }
  function openLaunchErrorModal(reason) {
    if (!launchErrModalEl) return;
    var titleEl = $(".msg-title", launchErrModalEl);
    var textEl  = $(".msg-text",  launchErrModalEl);
    var hintEl  = $(".msg-hint",  launchErrModalEl);
    if (reason === "file_missing") {
      if (titleEl) titleEl.textContent = window.BBW.t("error.fileMissing.title");
      if (textEl)  textEl.textContent  = window.BBW.t("error.fileMissing.msg");
    } else {
      // Autres raisons (game already running, extraction in progress, …) :
      // on garde un titre générique et on affiche la raison brute en corps.
      if (titleEl) titleEl.textContent = window.BBW.t("error.fileMissing.title");
      if (textEl)  textEl.textContent  = String(reason || "");
    }
    if (hintEl) hintEl.textContent = window.BBW.t("error.fileMissing.hint");
    launchErrOpen = true;
    launchErrModalEl.classList.add("open");
  }
  function closeLaunchErrorModal() {
    launchErrOpen = false;
    if (launchErrModalEl) launchErrModalEl.classList.remove("open");
  }
  function pinMove(cmd) {
    var r = Math.floor(pinFocus / PIN_COLS), c = pinFocus % PIN_COLS;
    if (cmd === "up" && r > 0) pinFocus -= PIN_COLS;
    else if (cmd === "down" && r < PIN_ROWS - 1) pinFocus += PIN_COLS;
    else if (cmd === "left" && c > 0) pinFocus -= 1;
    else if (cmd === "right" && c < PIN_COLS - 1) pinFocus += 1;
    paintPin();
  }

  // ── Popup "Related Games" : onglets (←/→) + liste de cartes (↑/↓) ──────────
  var REL_TABS = ["recommended", "similar", "ports"];
  function setupRelatedModal() {
    relModalEl = $(".related-modal"); if (!relModalEl) return;
    relModalEl.addEventListener("click", function (e) {
      if (!relOpen) return;
      var tab = e.target.closest && e.target.closest(".rel-tab");
      if (tab) { relTab = +tab.dataset.tab; relSel = 0; renderRelated(); return; }
      var card = e.target.closest && e.target.closest(".rel-card");
      if (card) { relSel = +card.dataset.i; paintRelSel(); relActivate(); return; }
      if (!(e.target.closest && e.target.closest(".related-panel"))) closeRelated();   // clic hors panneau = fermer
    });
    setupLbDbModal();
    var box = $(".rel-list", relModalEl);
    if (box) box.addEventListener("mouseover", function (e) {
      if (!relOpen || !mouseActive) return;
      var c = e.target.closest && e.target.closest(".rel-card");
      if (c) { relSel = +c.dataset.i; paintRelSel(); }
    });
  }
  function openRelated() {
    if (!relModalEl) return;
    relOpen = true; relTab = 0; relSel = 0; relData = null;
    renderRelated();                       // affiche le cadre tout de suite (liste vide le temps du chargement)
    relModalEl.classList.add("open");
    var relKey = curGameId(); if (relKey == null) relKey = currentGame;   // plugin : par id réel ; dummy : par index
    // Nombre max d'items par onglet, configurable depuis l'UI Réglages
    // (config.global.related.perTab). Default 50 ; clamp côté serveur entre 1 et 200.
    var L = (G && G.related) || {};
    var perTab = (L.perTab != null ? L.perTab : 50) | 0;
    if (perTab < 1) perTab = 1;
    window.BBW.get("data/games/" + relKey + "/related.json?limit=" + perTab).then(function (r) {
      relData = r || { recommended: [], similar: [], ports: [] };
      if (relOpen) {
        renderRelated();
        // Remplissage async des descriptions — endpoint séparé qui passe
        // par notre chaîne de priorité Overview (cf. SqliteCommand_ExecuteReader_Patch
        // SECTION 1 qui voit le "g"."Overview" et applique le COALESCE
        // priorisé). Ne bloque pas l'affichage des cards (thumbs + titres
        // sont déjà rendus).
        fetchRelatedOverviews();
      }
    });
  }

  // Collecte les `dbid` de tous les onglets, fait UN seul GET batch, remplit
  // relData[*][i].d à la volée, re-paint la card si l'onglet courant change.
  function fetchRelatedOverviews() {
    if (!relData) return;
    var ids = [];
    ["recommended", "similar", "ports"].forEach(function (key) {
      var lst = relData[key] || [];
      for (var i = 0; i < lst.length; i++) {
        var d = lst[i] && lst[i].dbid;
        if (d && ids.indexOf(d) < 0) ids.push(d);
      }
    });
    if (ids.length === 0) return;
    // Cap dur côté serveur à 200, on cap aussi ici pour pas envoyer une URL géante.
    if (ids.length > 200) ids.length = 200;
    window.BBW.get("data/games/related/overviews.json?ids=" + ids.join(",")).then(function (map) {
      if (!map || !relData) return;
      ["recommended", "similar", "ports"].forEach(function (key) {
        var lst = relData[key] || [];
        for (var i = 0; i < lst.length; i++) {
          var it = lst[i]; if (!it) continue;
          var ov = it.dbid ? map[String(it.dbid)] : null;
          if (ov) it.d = ov;
        }
      });
      if (relOpen) renderRelated();
    });
  }
  function closeRelated() { relOpen = false; if (relModalEl) relModalEl.classList.remove("open"); }
  function relList() { return (relData && relData[REL_TABS[relTab]]) || []; }
  function renderRelated() {
    if (!relModalEl) return;
    relModalEl.querySelectorAll(".rel-tab").forEach(function (t, i) { t.classList.toggle("active", i === relTab); });
    var list = relList(); if (relSel >= list.length) relSel = Math.max(0, list.length - 1);
    var box = $(".rel-list", relModalEl);
    box.innerHTML = list.map(function (g, i) {
      var hue = (g.t.charCodeAt(0) * 7 + ((g.y && g.y.charCodeAt(3)) || 50) * 13) % 360;
      var pct = (g.pct != null)
        ? '<span class="rel-pct"><span class="rel-cloud">☁</span> ' + g.pct + '%</span>'
        : '<span class="rel-pct"><span class="rel-cloud">☁</span></span>';
      var thumbStyle = g.thumb            // plugin : vraie vignette (/api/media/<id>.jpg) ; dummy : dégradé
        ? "background-image:url('" + g.thumb + "');background-size:cover;background-position:center"
        : "background:linear-gradient(160deg,hsl(" + hue + ",45%,42%),hsl(" + hue + ",40%,16%))";
      return '<div class="rel-card' + (i === relSel ? ' sel' : '') + '" data-i="' + i + '">'
        + '<div class="rel-thumb" style="' + thumbStyle + '"></div>'
        + '<div class="rel-body"><div class="rel-head"><span class="rel-title">' + g.t + '</span>' + pct + '</div>'
        + '<div class="rel-sub">' + g.y + ' - ' + g.plat + '</div>'
        + '<div class="rel-desc">' + g.d + '</div></div></div>';
    }).join("");
    paintRelSel();
  }
  function paintRelSel() {
    var box = $(".rel-list", relModalEl); if (!box) return;
    Array.prototype.forEach.call(box.children, function (c, i) { c.classList.toggle("sel", i === relSel); });
    var s = box.children[relSel]; if (s) s.scrollIntoView({ block: "nearest" });
  }
  function relMove(d) { var n = relList().length; if (n <= 0) return; relSel = Math.max(0, Math.min(n - 1, relSel + d)); paintRelSel(); }
  function relTabMove(d) { relTab = Math.max(0, Math.min(REL_TABS.length - 1, relTab + d)); relSel = 0; renderRelated(); }

  // Activation d'une carte related : si le jeu est dans la lib du user
  // (local=true), on navigue à sa fiche détail (même flux que recentActivate
  // pour un jeu d'une autre plateforme — push + setSelected + navTo). Sinon
  // (DB-only), on ouvre la modal iframe sur /games/{dbid}.html.
  function relActivate() {
    var item = relList()[relSel]; if (!item) return;
    if (item.local && item.id) {
      closeRelated();
      // Tente d'abord la voie propre : si la plateforme du jeu est dans
      // notre menu, on la CHARGE (loadPlatform → refresh
      // DATA.platform/Logo/LogoImg + DATA.games + topbar title + wheel),
      // puis on sélectionne le jeu cible par son id dans la liste
      // fraîchement chargée. Tout le contexte (logo plateforme top-right,
      // wheel, etc.) est cohérent avec le jeu affiché.
      // Fallback : plateforme pas dans le menu → vieux trampoline stub.
      var node = item.plat ? findPlatformInCatTree(item.plat) : null;
      if (node) relActivateLocalIntoPlatform(item, node);
      else      relActivateLocalAsStub(item);
      return;
    }
    // DB-only : suit la décision SERVEUR (item.link, résolue par ExtendDbLinks) — lien
    // relatif = la web-db locale couvre l'id → iframe modal ; lien absolu = la base
    // active ne peut pas servir ce jeu → site source du range (nouvel onglet).
    // Payload sans link (ancien serveur) → comportement historique (iframe locale).
    if (item.dbid) {
      var lnk = item.link || "";
      if (lnk && lnk.charAt(0) !== "/") {
        try { window.open(lnk, "_blank", "noopener"); } catch (e) {}
        return;
      }
      openLbDbModal(item.dbid);
    }
  }

  // Charge la plateforme du jeu cible, puis sélectionne le jeu par id dans
  // la liste fraîchement chargée. Si le jeu n'y est PAS (filtre biblio,
  // jeu caché, etc.), on retombe sur la voie stub.
  function relActivateLocalIntoPlatform(item, node) {
    var path = node.path || ("platforms/" + (node.slug || slugify(node.name)));
    try {
      cancelGameContentLoads();
      loadPlatform(path, function () {
        var idx = -1;
        for (var i = 0; i < DATA.games.length; i++) {
          var g = DATA.games[i];
          if (g && g.id === item.id) { idx = i; break; }
        }
        if (idx < 0) { relActivateLocalAsStub(item); return; }
        currentGame = idx;
        shownGame = -1;
        detailsReturn = "games";   // B → wheel de CETTE plateforme
        var _gFresh = DATA.games[currentGame]; if (_gFresh) _gFresh._det = false;
        _pendingLastLaunchSync = true;
        requestGameDetail(currentGame);
        fillGamePanel(screens.details, currentGame);
        resetDetailMenu();
        setSelected("games", idx, { instant: true });   // sync la wheel
        setSelected("details", 0, { instant: true });
        navTo("details", false);
      });
    } catch (e) { relActivateLocalAsStub(item); }
  }

  // Voie fallback historique (cross-platform trampoline). Utilisée quand la
  // plateforme du jeu cible n'est PAS dans notre menu, OU quand le jeu a
  // été filtré de la liste fraîchement chargée. Le marqueur _fromRelated
  // active la logique platform-aware-back dans detailBack.
  function relActivateLocalAsStub(item) {
    try {
      cancelGameContentLoads();
      var inj = {
        id: item.id, t: item.t, plat: item.plat || "",
        y: item.y || "", thumb: item.thumb || "",
        dev: "", pub: "", g: "", r: "", esrb: "", d: "",
        boxImg: "", shotImg: "", video: "", file: "",
        _fromRelated: true,
      };
      DATA.games.push(inj);
      currentGame = DATA.games.length - 1;
      // detailsReturn préservé : on revient à l'écran d'origine du PREMIER
      // jeu via le fallback de detailBack si la plateforme reste hors menu.
      var _gFresh = DATA.games[currentGame]; if (_gFresh) _gFresh._det = false;
      _pendingLastLaunchSync = true;
      requestGameDetail(currentGame);
      fillGamePanel(screens.details, currentGame);
      resetDetailMenu();
      setSelected("details", 0, { instant: true });
      navTo("details", false);
    } catch (e) { /* ignore */ }
  }

  // ── Modal LB-DB iframe ────────────────────────────────────────────────
  var lbdbModalEl = null, lbdbOpen = false;
  function setupLbDbModal() {
    lbdbModalEl = $(".lbdb-modal"); if (!lbdbModalEl) return;
    lbdbModalEl.addEventListener("click", function (e) {
      if (!lbdbOpen) return;
      if (e.target.closest && e.target.closest(".lbdb-close")) { closeLbDbModal(); return; }
      // clic en dehors du panel = ferme (le backdrop est .lbdb-modal sans .lbdb-panel)
      if (!(e.target.closest && e.target.closest(".lbdb-panel"))) closeLbDbModal();
    });
  }
  // Site DB EXTERNE déduit du RANGE de l'id (= règle d'attribution des
  // DatabaseID de la base étendue) — AUCUN lookup, juste l'objet jeu. Bases :
  // ScreenScraper=1e6, VNDB=2e6, Steam=1e7 ; l'id source = dbid - base.
  //   [0, 1e6)       LaunchBox     → gamesdb.launchbox-app.com/games/dbid/{id}
  //   [1e6, 2e6)     ScreenScraper → screenscraper.fr/gameinfos.php?gameid={id-1e6}
  //   [2e6, 1e7)     VNDB          → vndb.org/v{id-2e6}
  //   [1e7, 1e9)     Steam         → store.steampowered.com/app/{id-1e7}
  //   >= 1e9         (formule IGDB, jamais une origine réelle) → pas de lien.
  function lbDbExternal(dbid) {
    dbid = +dbid || 0;
    if (dbid <= 0) return null;
    if (dbid < 1000000)    return { url: "https://gamesdb.launchbox-app.com/games/dbid/" + dbid,              label: "View on LaunchBox DB" };
    if (dbid < 2000000)    return { url: "https://screenscraper.fr/gameinfos.php?gameid=" + (dbid - 1000000), label: "View on ScreenScraper" };
    if (dbid < 10000000)   return { url: "https://vndb.org/v" + (dbid - 2000000),                            label: "View on VNDB" };
    if (dbid < 1000000000) return { url: "https://store.steampowered.com/app/" + (dbid - 10000000),          label: "View on Steam" };
    return null;
  }
  function openLbDbModal(dbid) {
    if (!lbdbModalEl) return;
    var iframe = $(".lbdb-frame", lbdbModalEl);
    if (iframe) iframe.src = "/games/" + dbid + ".html";
    lbdbModalEl.classList.add("open");
    lbdbOpen = true;
  }
  function closeLbDbModal() {
    if (!lbdbModalEl) return;
    lbdbModalEl.classList.remove("open");
    lbdbOpen = false;
    // Libère la mémoire iframe + arrête tout media en cours dans la fiche.
    var iframe = $(".lbdb-frame", lbdbModalEl);
    if (iframe) iframe.src = "about:blank";
  }

  // ── Popup "Tags VNDB" : panneau scrollable à droite, tags groupés (content / tech / ero) ──
  function setupVndbModal() {
    vndbModalEl = $(".vndb-modal"); if (!vndbModalEl) return;
    vndbModalEl.addEventListener("click", function (e) {
      if (!vndbOpen) return;
      if (!(e.target.closest && e.target.closest(".vndb-panel"))) closeVndbTags();   // clic hors panneau = fermer
    });
  }
  function openVndbTags() {
    if (!vndbModalEl) return;
    var g = DATA.games[currentGame]; if (!g || !hasVndb(g.vndb)) return;
    renderVndb(g.vndb);
    vndbOpen = true; vndbModalEl.classList.add("open");
    var list = $(".vndb-list", vndbModalEl); if (list) list.scrollTop = 0;
  }
  function closeVndbTags() { vndbOpen = false; if (vndbModalEl) vndbModalEl.classList.remove("open"); }
  function renderVndb(v) {
    if (!vndbModalEl) return;
    var title = $(".vndb-title", vndbModalEl); if (title) title.textContent = tA("vndb.title");
    var list = $(".vndb-list", vndbModalEl); if (!list) return;
    list.innerHTML = "";
    [["cont", v.cont], ["tech", v.tech], ["ero", v.ero]].forEach(function (grp) {
      var key = grp[0], tags = grp[1] || []; if (!tags.length) return;
      var h = document.createElement("div"); h.className = "vndb-group"; h.textContent = tA("vndb." + key); list.appendChild(h);
      var wrap = document.createElement("div"); wrap.className = "vndb-tags";
      tags.forEach(function (name) {
        var t = document.createElement("span"); t.className = "vndb-tag vndb-" + key; t.textContent = name; wrap.appendChild(t);
      });
      list.appendChild(wrap);
    });
  }
  function vndbScroll(d) {
    var list = $(".vndb-list", vndbModalEl); if (!list) return;
    list.scrollTop += d * 90;   // ↑/↓ défilent (répétition au maintien = défilement continu)
  }

  // ── UI Réglages (Settings) : menus à onglets + options (toggle/slider/select) ───────────
  // Pilotable au pad/tablette : L/R = onglets · Haut/Bas = focus · Gauche/Droite = ajuste ·
  // A = remet l'option par défaut (ou « Restore defaults » = toute la page) · B = ferme.
  // Sauvegarde en cookie (window.BBW.cfg). À la fermeture, si modifié → reload (applique tout).
  function setupConfig() {
    cfgModalEl = $(".config-modal"); if (!cfgModalEl) return;
    cfgModalEl.addEventListener("click", function (e) {
      if (!cfgOpen) return;
      var tab = e.target.closest && e.target.closest(".config-tab");
      if (tab) { cfgTab = +tab.dataset.i; cfgFocus = 0; renderConfig(); return; }
      if (!(e.target.closest && e.target.closest(".config-panel"))) closeConfig();   // clic hors panneau = fermer
    });
  }
  function openConfig() {
    if (!cfgModalEl) return;
    cfgOpen = true; cfgTab = 0; cfgFocus = 0; cfgDirty = false;
    renderConfig(); cfgModalEl.classList.add("open");
  }
  function closeConfig() {
    cfgOpen = false; if (cfgModalEl) cfgModalEl.classList.remove("open");
    if (cfgDirty) location.reload();   // recharge : toutes les surcharges (cookie) s'appliquent proprement
  }
  function cfgFmt(v, step) { var d = (("" + step).split(".")[1] || "").length; return Number(v).toFixed(d); }
  function updateCfgControl(t) {
    if (!t || t.type !== "opt") return;
    var opt = t.opt, v = window.BBW.cfg.get(opt.path), ctrl = $(".cfg-ctrl", t.el);
    if (opt.type === "bool") { var tg = $(".cfg-toggle", ctrl); tg.textContent = v ? "ON" : "OFF"; tg.classList.toggle("on", !!v); }
    else if (opt.type === "select") { $(".cfg-selval", ctrl).textContent = "" + v; }
    else {
      var pct = opt.max > opt.min ? (v - opt.min) / (opt.max - opt.min) * 100 : 0;
      pct = Math.max(0, Math.min(100, pct));
      $(".cfg-fill", ctrl).style.width = pct + "%"; $(".cfg-knob", ctrl).style.left = pct + "%";
      $(".cfg-val", ctrl).textContent = cfgFmt(v, opt.step);
    }
  }
  function renderConfig() {
    if (!cfgModalEl) return;
    var schema = window.BBW.configSchema || [];
    var tabsEl = $(".config-tabs", cfgModalEl); tabsEl.innerHTML = "";
    schema.forEach(function (menu, i) {
      var t = document.createElement("div"); t.className = "config-tab" + (i === cfgTab ? " active" : "");
      t.textContent = menu.title; t.dataset.i = i; tabsEl.appendChild(t);
    });
    var body = $(".config-body", cfgModalEl); body.innerHTML = ""; cfgTargets = [];
    var menu = schema[cfgTab]; if (!menu) return;
    function bindRow(row, idx) {
      row.dataset.i = idx;
      row.addEventListener("mouseenter", function () { if (cfgOpen && mouseActive) { cfgFocus = idx; paintCfg(); } });
      row.addEventListener("click", function () { if (cfgOpen) { cfgFocus = idx; paintCfg(); cfgActivate(); } });
    }
    menu.opts.forEach(function (opt) {
      var row = document.createElement("div"); row.className = "cfg-row";
      var label = document.createElement("span"); label.className = "cfg-label"; label.textContent = opt.label;
      var ctrl = document.createElement("span"); ctrl.className = "cfg-ctrl";
      if (opt.type === "bool") ctrl.innerHTML = '<span class="cfg-toggle"></span>';
      else if (opt.type === "select") ctrl.innerHTML = '<span class="cfg-select"><span class="cfg-arrow">‹</span><span class="cfg-selval"></span><span class="cfg-arrow">›</span></span>';
      else ctrl.innerHTML = '<span class="cfg-slider"><span class="cfg-track"><span class="cfg-fill"></span><span class="cfg-knob"></span></span><span class="cfg-val"></span></span>';
      row.appendChild(label); row.appendChild(ctrl); body.appendChild(row);
      var idx = cfgTargets.length; cfgTargets.push({ type: "opt", opt: opt, el: row });
      bindRow(row, idx); updateCfgControl(cfgTargets[idx]);
    });
    var rrow = document.createElement("div"); rrow.className = "cfg-row cfg-restore";
    rrow.textContent = "↺ Restore defaults (this page)"; body.appendChild(rrow);
    var ri = cfgTargets.length; cfgTargets.push({ type: "restore", el: rrow }); bindRow(rrow, ri);
    if (cfgFocus >= cfgTargets.length) cfgFocus = Math.max(0, cfgTargets.length - 1);
    paintCfg();
  }
  function paintCfg() {
    for (var i = 0; i < cfgTargets.length; i++) cfgTargets[i].el.classList.toggle("focus", i === cfgFocus);
    var f = cfgTargets[cfgFocus]; if (f && f.el.scrollIntoView) f.el.scrollIntoView({ block: "nearest" });
  }
  function cfgMoveTab(dir) {
    var n = (window.BBW.configSchema || []).length; if (!n) return;
    cfgTab = (cfgTab + dir + n) % n; cfgFocus = 0; renderConfig();
  }
  function cfgAdjust(dir) {
    var t = cfgTargets[cfgFocus]; if (!t || t.type !== "opt") return;
    var opt = t.opt, cur = window.BBW.cfg.get(opt.path), v;
    if (opt.type === "bool") v = dir > 0;
    else if (opt.type === "select") { var o = opt.options, idx = o.indexOf(cur); if (idx < 0) idx = 0; v = o[(idx + dir + o.length) % o.length]; }
    else {
      v = (typeof cur === "number" ? cur : opt.min) + dir * opt.step;
      v = Math.max(opt.min, Math.min(opt.max, v));
      v = Math.round(v / opt.step) * opt.step; v = Math.round(v * 1e6) / 1e6;
    }
    window.BBW.cfg.set(opt.path, v); window.BBW.applyConfigCss(); cfgDirty = true; updateCfgControl(t);
  }
  function cfgActivate() {
    var t = cfgTargets[cfgFocus]; if (!t) return;
    if (t.type === "restore") {
      var menu = (window.BBW.configSchema || [])[cfgTab]; if (!menu) return;
      menu.opts.forEach(function (o) { window.BBW.cfg.reset(o.path); });
      window.BBW.applyConfigCss(); cfgDirty = true; renderConfig();
    } else {   // A sur une option → remet sa valeur par défaut
      window.BBW.cfg.reset(t.opt.path); window.BBW.applyConfigCss(); cfgDirty = true; updateCfgControl(t);
    }
  }
  function detailBack() {
    if (menuStack.length > 1) {                                 // sous-menu → on remonte d'un niveau
      menuStack.pop();
      renderDetailMenu(-1); setSelected("details", detailLevel().sel, { instant: true });
      return;
    }
    // Cas spécial : jeu poussé via le trampoline related-popup (cross-platform).
    // Si sa plateforme existe dans notre menu (DATA.catTree), B charge cette
    // plateforme plutôt que detailsReturn — sinon le user retombe sur l'écran
    // d'origine du PREMIER jeu (souvent la mauvaise plateforme). Fallback
    // pur sur detailsReturn quand la plateforme n'est pas dans nos catégories
    // (jeu rare / plateforme cachée / playlist-only setup).
    var g = DATA.games[currentGame];
    if (g && g._fromRelated && g.plat) {
      var node = findPlatformInCatTree(g.plat);
      if (node) {
        var path = node.path || ("platforms/" + (node.slug || slugify(node.name)));
        loadPlatform(path, function () {
          shownGame = -1;
          setSelected("games", 0, { instant: true });
          // BUG #1 FIX: poster side panel only refreshes via posterSelect(); setSelected alone is not enough.
          if (posterMode) posterSelect(0);
          navTo("games", false);
        });
        return;
      }
    }
    navTo(detailsReturn, true);                              // racine du menu → retour liste de jeux
  }

  // Walk récursif sur DATA.catTree, retourne le premier leaf-node dont le
  // nom OU le slug matche `name`. Leaf = sans children (= plateforme ou
  // playlist). Groupes de catégories (avec children) sont traversés mais
  // pas matchés directement (ils ne sont pas loadable via loadPlatform).
  function findPlatformInCatTree(name) {
    if (!name || !DATA.catTree) return null;
    var nameLc = String(name).toLowerCase();
    var slugWanted = slugify(name);
    function walk(nodes) {
      if (!nodes) return null;
      for (var i = 0; i < nodes.length; i++) {
        var n = nodes[i]; if (!n) continue;
        if (n.children && n.children.length) {
          var found = walk(n.children); if (found) return found;
        } else {
          var nName = (n.name || "").toLowerCase();
          if (nName === nameLc) return n;
          var nSlug = n.slug || (n.name ? slugify(n.name) : "");
          if (nSlug === slugWanted) return n;
        }
      }
      return null;
    }
    return walk(DATA.catTree);
  }

  // Remplissage PAR ZONE (chaque zone glisse séparément, cf. doGameTransition).
  function fillGameBoxartInner(inner, gi) {
    var g = DATA.games[gi];
    var sub = $(".ba-sub", inner), title = $(".ba-title", inner);
    // On NE vide PAS le fond ici : le navigateur garde l'image courante jusqu'au
    // swap (zéro flash). Si la nouvelle image échoue, loadBg l'efface dans son
    // onerror → jamais l'image du jeu précédent. Le placeholder (aucune image)
    // efface lui-même.
    if (g.boxImg) {                    // version COMPLÈTE (palier 1 s) → annulable, contenue
      loadBg(inner, g.boxImg, "contain", mediaToken, true);
      sub.textContent = ""; title.textContent = "";
    } else if (g.thumb) {              // vignette DÉGRADÉE (instantanée, fenêtre cache) → NON annulable
      loadBg(inner, g.thumb, "contain", mediaToken, false);
      sub.textContent = ""; title.textContent = "";
    } else {                           // pas d'image → texte (placeholder), bloc pleine hauteur
      resetZoneHeight(inner.parentElement);
      inner.style.backgroundImage = ""; inner.style.backgroundSize = ""; inner.style.backgroundPosition = "";
      sub.textContent = (g.box && g.box[0]) || ""; title.textContent = (g.box && g.box[1]) || "";
    }
  }
  // Genres AFFICHÉS : on retire les tags VNDB (« vndb-cont / … », « vndb-tech / … »,
  // « vndb-ero / … ») qui rallongent la ligne — on ne garde que les vrais genres (« , » séparé).
  // Les tags VNDB restent dans g.g (panneau dédié possible plus tard).
  function realGenres(s) {
    if (!s) return "";
    return String(s).split(";").map(function (x) { return x.trim(); })
      .filter(function (x) { return x && x.indexOf("vndb-") !== 0; }).join(", ");
  }
  function fillGameDetailInner(inner, gi) {
    var g = DATA.games[gi];
    $(".game-title", inner).textContent = g.t;
    var star = (userRatings[gi] > 0) ? userRatings[gi] : g.r;   // note perso si définie, sinon note par défaut
    $(".meta", inner).innerHTML =
      '<span class="year">' + g.y + '</span><span>' + g.dev + '</span><span>' + g.pub +
      '</span><span>' + realGenres(g.g) + '</span><span class="star">★ ' + star + '</span>';
    // Ligne optionnelle "nom du fichier" (sans chemin), sous la ligne année/genre/note. Off par défaut.
    var fn = $(".filename", inner);
    if (fn) {
      var showFn = G.gameFilename && G.gameFilename.enabled && g.file;
      fn.textContent = showFn ? g.file : "";
      fn.style.display = showFn ? "" : "none";
    }
    $(".desc", inner).innerHTML = '<div class="desc-inner">' + (g.d || "") + "</div>";   // .desc = fenêtre clippée, .desc-inner = contenu défilé (vide tant que le détail n'est pas chargé)
    var b = '<span class="badge-ico" title="' + window.BBW.t("badge.players") + '">👤</span><span class="badge-ico" title="' + window.BBW.t("badge.region") + '">🌐</span>';
    if (g.esrb) b += '<span class="badge-pill outline">' + g.esrb + '</span>';
    // Store install state badge (store games only; g.store arrives with detail.json):
    // a round store-logo badge framed by a ring — green = installed, orange = not.
    // The logo is the existing LB badge image (GOG/Steam/EpicGames.png); if absent
    // the ring alone still conveys the state (img onerror hides the broken icon).
    if (g.store) {
      var bImg = (g.store === "Epic") ? "EpicGames" : (g.store === "Ubisoft") ? "Uplay" : g.store;   // GOG / Steam / EpicGames / Uplay
      var bCls = (g.installed === false) ? "store-badge notinstalled" : "store-badge installed";
      var bTitle = (g.installed === false ? "Not installed" : "Installed") + " — " + g.store;
      b += '<span class="' + bCls + '" title="' + bTitle + '">'
         + '<img src="/api/badges/' + encodeURIComponent(bImg) + '.png" alt="' + g.store + '" onerror="this.style.display=\'none\'">'
         + '</span>';
    }
    b += '<span class="badge-pill status"><span class="st-star">✦</span> Not Started / Unplayed</span>';
    $(".badges", inner).innerHTML = b;
    // La plateforme (logo/texte) vit DANS cette zone .detail .rgn-inner → elle glisse avec
    // le titre / la description lors de la transition (transitionRegion slide-v). On la
    // (re)pose et on mesure le chevauchement titre↔plateforme dans ce même conteneur.
    applyPlatformLogo($(".platform-logo", inner));
    fitPlatformLogo(inner);
  }
  // Le titre du jeu (gauche) et le nom de plateforme (haut-droite, plus petit) partagent
  // la même bande horizontale. Si le titre, déplié sur UNE ligne, atteindrait la zone de
  // la plateforme, on MASQUE la plateforme : mieux vaut la perdre (visible ailleurs) qu'un
  // chevauchement. On mesure le bord droit RÉEL du texte via un Range (pas scrollWidth,
  // qui retombe sur la largeur du bloc quand le texte est plus court) ; tout est en px
  // écran (post-scale) donc l'écart `gapPx` (mise en page) est converti via l'échelle.
  // `inner` = la couche vive .detail .rgn-inner (titre + plateforme y cohabitent).
  function fitPlatformLogo(inner) {
    if (!inner) return;
    var pf = G.platformFit || {};
    var gt = $(".game-title", inner), pl = $(".platform-logo", inner);
    if (!gt || !pl) return;
    pl.style.visibility = "";                                   // défaut : visible (et mesurable)
    if (pf.enabled === false || window.BBW.isMobile()) return;  // désactivé / phone (déjà masquée en CSS)
    // Plateforme = texte OU clear logo (img) ; rien à mesurer s'il n'y a aucun des deux.
    var hasPlat = pl.textContent.trim() || pl.querySelector("img.plat-logo-img");
    if (!gt.textContent || !hasPlat) return;
    var prevWs = gt.style.whiteSpace;
    gt.style.whiteSpace = "nowrap";                             // force 1 ligne pour mesurer la largeur naturelle
    var range = document.createRange(); range.selectNodeContents(gt);
    var titleRight = range.getBoundingClientRect().right;       // bord droit réel du texte (px écran)
    gt.style.whiteSpace = prevWs;
    var gtRect = gt.getBoundingClientRect();
    var scale = gt.offsetWidth ? (gtRect.width / gt.offsetWidth) : 1; if (!scale) scale = 1;
    var gap = (pf.gapPx != null ? pf.gapPx : 16) * scale;       // écart mise en page → px écran
    if (titleRight + gap > pl.getBoundingClientRect().left) pl.style.visibility = "hidden";
  }
  function fillGameMediaInner(inner, gi) {
    var g = DATA.games[gi];
    var zone = inner.parentElement;
    // Coupe la vidéo précédente AVANT tout re-remplissage. Un <video> simplement retiré
    // du DOM (innerHTML="" / removeChild) CONTINUE de jouer son son ; la roue tactile
    // (paysage) re-remplit le panneau SANS passer par cancelGameContentLoads → sans cet
    // arrêt explicite, le son du jeu précédent persiste en fond quand on passe au suivant.
    abortCurrentVideo();
    if (inner.querySelectorAll) {
      // Retire la <video> ET la grille fillScreenshot du jeu PRÉCÉDENT. La branche
      // « pas de vidéo » (utilisée en parcourant la roue, avant que le détail du
      // nouveau jeu soit chargé) ne nettoie pas le DOM sinon → la grille précédente
      // resterait affichée par-dessus/sous la nouvelle.
      var stale = inner.querySelectorAll("video, .shot-grid, .fill-main");
      for (var si = 0; si < stale.length; si++) {
        var el = stale[si];
        if (el.tagName === "VIDEO") { try { el.pause(); el.removeAttribute("src"); el.load(); } catch (e) {} }
        if (el.parentNode) el.parentNode.removeChild(el);
      }
    }
    zone.classList.remove("fill-anim");

    var items = buildMediaItems(g);
    var screenId = screenIdOf(zone);
    // Pas encore de média de détail (juste la vignette dégradée) → repli dégradé.
    // Zéro flash : loadBg pose le fond quand l'image est prête, et l'efface dans onerror.
    if (items.length === 0) {
      delete mediaCars[screenId];
      zone.classList.remove("fill", "fill-anim");
      inner.style.backgroundColor = "";
      if (g.shotImg) loadBg(inner, g.shotImg, "contain", mediaToken, true);
      else if (g.shotThumb) loadBg(inner, g.shotThumb, "contain", mediaToken, false);
      else { inner.style.backgroundImage = "none"; collapseZone(zone); }
      return;
    }

    // fillScreenshot (desktop / tablette paysage) ET ≥ 2 médias → main + grille de vignettes.
    var fill = !!(G.media && G.media.fillScreenshot && !window.BBW.isMobile() && items.length >= 2);
    // Plafonne au nb de cellules affichables + 1 (le main) : chaque item garde une
    // cellule visible → le swap en place reste cohérent. IMPORTANT : on applique .fill AVANT
    // de mesurer, sinon gridCapacity lit la zone NON-fill (plus étroite) → trop peu de colonnes
    // → on coupait les screenshots (ex. 3 au lieu de 6).
    if (fill) {
      zone.classList.add("fill");
      var cap = gridCapacity(zone);
      if (items.length > cap + 1) items = items.slice(0, cap + 1);
    }

    var car = { gi: gi, items: items, mainIdx: 0, cells: [], inner: inner, zone: zone, fill: fill };
    if (fill) for (var k = 1; k < items.length; k++) car.cells.push(k);
    mediaCars[screenId] = car;   // état PAR ÉCRAN (games + details ont chacun leur zone média)
    renderMediaCar(car, true);   // 1er affichage du jeu → animation d'apparition (glissement fill)
  }
  // ── Carrousel média : vidéo + screenshots, navigables (LB/RB manette · Tab clavier ·
  //    clic sur une vignette). `main` = média affiché en grand ; `cells` = indices des
  //    AUTRES items, en cellules de grille à positions FIXES → un swap échange main↔cellule
  //    sans déplacer les autres (« le screenshot qui devient actif est remplacé par l'ancien »).
  //    Marche que fillScreenshot soit activé ou non (sans fill : pas de grille, juste le main).
  //    État PAR ÉCRAN : la roue jeux et la fiche détail ont chacune leur zone média.
  var mediaCars = {};   // screenId ("games"/"details") -> { gi, items, mainIdx, cells, inner, zone, fill }
  function screenIdOf(zone) {
    var sc = (zone && zone.closest) ? zone.closest(".screen") : null;
    return sc ? sc.getAttribute("data-screen") : "_";
  }

  // Vignette dégradée (?q=thumb) → version complète (?q=full) pour l'affichage en grand.
  function toFull(url) { return url ? url.replace("q=thumb", "q=full") : url; }

  // Liste ordonnée des médias d'un jeu : [vidéo (si dispo & pas portrait), ...screenshots].
  function buildMediaItems(g) {
    var items = [];
    if (g.video && !window.BBW.isMobile()) items.push({ kind: "video", src: g.video, poster: g.shotThumb || g.shotImg || "" });
    var shots = g.shots || [];
    for (var i = 0; i < shots.length; i++) items.push({ kind: "img", src: shots[i], poster: shots[i] });
    if (!shots.length && g.shotImg) items.push({ kind: "img", src: g.shotImg, poster: g.shotThumb || g.shotImg });
    return items;
  }

  // Nb de cellules affichables dans la grille (même calcul que layoutGrid).
  function gridCapacity(zone) {
    var m = G.media || {};
    var tileW = m.fillShotWidthPx || 150, tileH = m.fillShotHeightPx || 84;
    var gap = (m.fillShotGapPx != null) ? m.fillShotGapPx : 6;
    var zw = zone.clientWidth || 640, zh = zone.clientHeight || 325;
    var cols = Math.max(1, Math.floor((Math.floor(zw * 0.5) + gap) / (tileW + gap)));
    var rows = Math.max(1, Math.floor((zh + gap) / (tileH + gap)));
    return cols * rows;
  }

  // (Re)construit le DOM média d'un carrousel `car`. animate=true → animation d'apparition
  // fill ; false → swap instantané (une vidéo redevenue main est relue depuis le début).
  function renderMediaCar(car, animate) {
    if (!car) return;
    var inner = car.inner, zone = car.zone, fill = car.fill;
    abortCurrentVideo();
    inner.innerHTML = "";
    inner.style.background = ""; inner.style.backgroundImage = "";
    inner.style.backgroundSize = ""; inner.style.backgroundRepeat = ""; inner.style.backgroundPosition = "";
    zone.classList.toggle("fill", fill);
    zone.classList.remove("fill-anim");

    var main = car.items[car.mainIdx];
    if (main.kind === "video") {
      inner.style.backgroundColor = fill ? "transparent" : "#000";   // fill : flotte sur le fond ; sinon panneau noir
      if (!fill) resetZoneHeight(zone);
      var v = document.createElement("video");
      v.src = main.src; v.loop = false; v.autoplay = true; v.playsInline = true; v.preload = "auto";
      v.controls = true; v.setAttribute("controlsList", "nodownload");
      if (main.poster) v.poster = main.poster;
      v.muted = !audioOn;
      inner.appendChild(v);
      if (!fill) v.addEventListener("loadedmetadata", function () {
        if (v.videoWidth && v.videoHeight) fitZoneHeight(zone, v.videoWidth, v.videoHeight);
      });
      currentVideoEl = v;
      var pr = v.play();
      if (pr && pr.catch) pr.catch(function () { v.muted = true; v.play().catch(function () {}); });
    } else if (fill) {
      inner.style.backgroundColor = "transparent";   // fill : capture principale sans fond noir (flotte)
      resetZoneHeight(zone);
      var mainImg = document.createElement("img");
      mainImg.className = "fill-main"; mainImg.src = toFull(main.src); mainImg.alt = "";
      inner.appendChild(mainImg);
    } else {
      inner.style.backgroundColor = "";
      loadBg(inner, toFull(main.src), "contain", mediaToken, true);   // ajuste la hauteur, sans flash
    }

    if (fill && car.cells.length) {
      var grid = document.createElement("div");
      grid.className = "shot-grid";
      inner.appendChild(grid);
      layoutGrid(grid, car, zone, animate);
    }
  }

  // Pose la grille (cellules = car.cells) : tailles/colonnes (config), vidéo en poster + ▶,
  // clic = swap. animate=true → glissement d'apparition ; false → instantané (--trans 0).
  function layoutGrid(grid, car, zone, animate) {
    var m = G.media || {};
    var tileW = m.fillShotWidthPx || 150, tileH = m.fillShotHeightPx || 84;
    var gap = (m.fillShotGapPx != null) ? m.fillShotGapPx : 6;
    var trans = (m.fillTransitionMs != null) ? m.fillTransitionMs : 1500;
    var zw = zone.clientWidth || 640, zh = zone.clientHeight || 325;
    var gridMaxW = Math.floor(zw * 0.5);                                  // la grille n'occupe pas plus que la moitié
    var cols = Math.max(1, Math.floor((gridMaxW + gap) / (tileW + gap)));
    var rows = Math.max(1, Math.floor((zh + gap) / (tileH + gap)));
    var n = Math.min(cols * rows, car.cells.length);
    if (n < 1) return;
    cols = Math.min(cols, n); rows = Math.ceil(n / cols);
    var gridW = cols * tileW + (cols - 1) * gap;
    grid.style.gridTemplateColumns = "repeat(" + cols + ", " + tileW + "px)";
    grid.style.gridTemplateRows = "repeat(" + rows + ", " + tileH + "px)";
    grid.style.width = gridW + "px";
    var videoW = Math.max(40, zw - gridW - gap);
    zone.style.setProperty("--bbw-fill-trans", (animate ? trans : 0) + "ms");
    zone.style.setProperty("--bbw-fill-gap", gap + "px");
    zone.style.setProperty("--bbw-fill-video-w", videoW + "px");
    zone.style.setProperty("--bbw-fill-video-shift", (gridW + gap) + "px");
    for (var j = 0; j < n; j++) {
      var item = car.items[car.cells[j]];
      var cell = document.createElement("div");
      cell.className = "shot-cell" + (item.kind === "video" ? " is-video" : "");
      var im = document.createElement("img");
      im.src = item.poster || item.src; im.alt = ""; im.loading = "lazy";
      cell.appendChild(im);
      if (item.kind === "video") { var badge = document.createElement("span"); badge.className = "vid-badge"; badge.textContent = "▶"; cell.appendChild(badge); }
      cell.addEventListener("click", (function (cc, jj) { return function () { activateCell(cc, jj); }; })(car, j));
      grid.appendChild(cell);
    }
    if (animate) {
      // 2× rAF : laisse peindre l'état initial avant d'activer .fill-anim (transition CSS).
      requestAnimationFrame(function () { requestAnimationFrame(function () {
        if (zone.classList.contains("fill")) zone.classList.add("fill-anim");
      }); });
    } else {
      zone.classList.add("fill-anim");   // --bbw-fill-trans = 0 → bascule instantanée en place
    }
  }

  // Échange main ↔ cellule j (clic ou L/R) → swap en place. Re-rendu instantané.
  function activateCell(car, j) {
    if (!car || j < 0 || j >= car.cells.length) return;
    var tmp = car.cells[j]; car.cells[j] = car.mainIdx; car.mainIdx = tmp;
    renderMediaCar(car, false);
  }

  // L/R (LB/RB) / Tab : amène l'item suivant/précédent (ordre naturel) en main ; l'ancien
  // main prend la cellule libérée. Sans grille (non-fill) : simple cycle du main. Agit sur
  // le carrousel de l'écran COURANT (games / details).
  function mediaCycle(dir) {
    var car = mediaCars[current]; if (!car) return;
    var n = car.items.length; if (n < 2) return;
    var target = (car.mainIdx + dir + n) % n;
    var j = car.cells.indexOf(target);
    if (j >= 0) activateCell(car, j);
    else { car.mainIdx = target; renderMediaCar(car, false); }
  }
  function fillGamePanel(root, gi) {
    // Garde-fou : liste de jeux vide (playlist sans jeux, ou games.json qui a
    // échoué / renvoyé 0 jeu) → DATA.games[gi] est undefined. On ne remplit pas
    // (sinon TypeError "reading 'boxImg' of undefined") — pas de crash, panneau
    // laissé en l'état.
    if (!DATA.games[gi]) return;
    // La plateforme (logo/texte) est posée par fillGameDetailInner : elle vit dans la zone
    // .detail .rgn-inner et glisse donc avec le titre / la description à la transition.
    fillGameBoxartInner($(".boxart .rgn-inner", root), gi);
    fillGameDetailInner($(".detail .rgn-inner", root), gi);
    fillGameMediaInner($(".media .rgn-inner", root), gi);
  }

  // Pose le contenu d'une zone .platform-logo : le CLEAR LOGO (image) de la plateforme si
  // disponible (DATA.platformLogoImg, fourni par games.json) ET activé en config, sinon le
  // texte « ▦ Nom ». L'<img> est RÉUTILISÉE et son src n'est réécrit que s'il change → pas
  // de rechargement ni de clignotement à chaque sélection de jeu (le logo ne change qu'au
  // changement de plateforme). Opacité fixe (config). Le déplacement/animation est porté par
  // la zone .detail elle-même (transitionRegion slide-v) puisque le logo y vit. À la fin de
  // chargement on relance fitPlatformLogo (largeur connue) sur la couche vive .rgn-inner.
  function applyPlatformLogo(el) {
    if (!el) return;
    var pf = G.platformLogoImage || {};
    var url = (pf.enabled !== false) ? DATA.platformLogoImg : "";
    if (url) {
      var img = el.querySelector("img.plat-logo-img");
      if (!img) {
        el.textContent = ""; img = document.createElement("img");
        img.className = "plat-logo-img"; img.alt = "";
        if (pf.maxHeightPx != null) img.style.maxHeight = pf.maxHeightPx + "px";
        if (pf.opacity != null) img.style.opacity = pf.opacity;
        img.onload = function () { fitPlatformLogo(el.closest ? el.closest(".rgn-inner") : null); };
        el.appendChild(img);
      }
      el.classList.add("has-img");
      if (img.getAttribute("src") !== url) img.src = url;
    } else {
      el.classList.remove("has-img");
      el.textContent = "▦ " + (DATA.platformLogo || DATA.platform || "");
    }
  }

  // Zone « clear logo du jeu sélectionné » en haut de la roue (option gameLogo). Sert la version
  // DÉGRADÉE mise en cache côté plugin (?q=logo → WebP avec transparence), préchargée dans la
  // MÊME fenêtre que les vignettes (cf. prefetchThumbWindow) → affichage instantané au défilement.
  // <img> réutilisée, src réécrit seulement s'il change (zéro flash, l'URL est déjà en cache).
  // Pas de clear logo pour le jeu → on affiche son TITRE en texte. No-op si l'option est coupée.
  function applyGameLogo(gi) {
    if (!G.gameLogo || G.gameLogo.enabled === false) return;
    var el = $(".game-logo", screens.games); if (!el) return;
    var g = DATA.games[gi]; if (!g) return;
    var url = g.logo || "";
    if (url) {
      var img = el.querySelector("img.game-logo-img");
      if (!img) {
        el.textContent = ""; img = document.createElement("img");
        img.className = "game-logo-img"; img.alt = "";
        el.appendChild(img);
      }
      el.classList.add("has-img");
      if (img.getAttribute("src") !== url) img.src = url;
    } else {
      el.classList.remove("has-img");
      el.textContent = g.t || "";
    }
  }

  // ── Détail jeu à la demande + ANNULATION (mode plugin) ──────────────────
  // En mode plugin la liste est LÉGÈRE (métadonnées seules, pas de description
  // ni média par jeu). À la sélection on charge data/games/<id>/detail.json, on
  // fusionne (d, box, boxImg, shotImg, video, votes) puis on re-remplit.
  // Quand on PASSE VITE d'un jeu à l'autre, on COUPE ce qui n'a pas fini :
  //   • le fetch detail.json en vol (AbortController),
  //   • les images en cours (jaquette/capture préchargées via Image(), annulables
  //     en mettant src=""),
  //   • la vidéo vivante (<video> : pause + retrait du src),
  // le tout protégé par un jeton de génération `mediaToken` : toute réponse d'une
  // génération périmée est jetée. Le detail.json n'est demandé qu'APRÈS le dwell
  // (l'utilisateur s'est posé) → le scroll rapide ne lance plus une rafale de
  // requêtes serveur. En mode dummy (pas d'`id`) → pas de fetch (no-op).
  var mediaToken = 0;          // génération du contenu jeu courant
  var detailAbort = null;      // AbortController du detail.json en vol
  var detailInFlightId = null; // id du jeu dont le detail.json est en cours (dédup)
  var pendingImgs = [];        // Image() COMPLÈTES en chargement (annulables)
  var currentVideoEl = null;   // <video> vivant courant (annulable)
  var heavyTimer = null;       // palier d'immobilité avant le LOURD (complète + vidéo)
  var prefetchedThumbs = {};   // URLs de vignettes dégradées déjà préchargées (fenêtre)

  function abortPendingImgs() {
    for (var i = 0; i < pendingImgs.length; i++) { var im = pendingImgs[i]; im.onload = im.onerror = null; try { im.src = ""; } catch (e) {} }
    pendingImgs = [];
  }
  function abortCurrentVideo() {
    if (currentVideoEl) { try { currentVideoEl.pause(); currentVideoEl.removeAttribute("src"); currentVideoEl.load(); } catch (e) {} currentVideoEl = null; }
  }
  // Coupe TOUT chargement média du jeu précédent encore en vol et invalide les
  // callbacks restants (via un nouveau jeton).
  function cancelGameContentLoads() {
    if (heavyTimer) { clearTimeout(heavyTimer); heavyTimer = null; }   // annule le palier en attente
    mediaToken++;
    if (detailAbort) { try { detailAbort.abort(); } catch (e) {} detailAbort = null; }
    detailInFlightId = null;
    abortPendingImgs();        // n'annule QUE les images COMPLÈTES (les dégradées sont gardées)
    abortCurrentVideo();
  }
  // Précharge une image puis l'applique en fond SI la génération est toujours la
  // bonne (sinon on jette). `cancellable` : true = version complète (annulable au
  // prochain changement) ; false = vignette dégradée (jamais annulée, juste posée
  // si encore d'actualité).
  function loadBg(el, url, sizeMode, token, cancellable) {
    var img = new Image();
    if (cancellable) pendingImgs.push(img);
    img.onload = function () {
      if (cancellable) { var k = pendingImgs.indexOf(img); if (k >= 0) pendingImgs.splice(k, 1); }
      if (token !== mediaToken || !el) return;
      if (el.querySelector("video")) return;   // Phase 2 already inserted a video — don't clobber it
      el.style.backgroundImage = "url('" + url + "')";
      el.style.backgroundSize = sizeMode; el.style.backgroundRepeat = "no-repeat"; el.style.backgroundPosition = "center";
      fitZoneHeight(el.parentElement, img.naturalWidth, img.naturalHeight);   // bloc à la hauteur réelle de l'image
    };
    img.onerror = function () {
      if (cancellable) { var k = pendingImgs.indexOf(img); if (k >= 0) pendingImgs.splice(k, 1); }
      if (token !== mediaToken || !el) return;
      // Échec (404/absent) → on retire le fond MAINTENANT pour ne jamais laisser
      // l'image du jeu précédent. (Effacer en AMONT causait un flash — surtout
      // Firefox — le temps que la nouvelle image se charge.)
      el.style.backgroundImage = "";
    };
    img.src = url;
  }

  // ── Hauteur des zones image ajustée au contenu ──────────────────────────
  // Largeur FIXE (CSS) ; la hauteur du bloc = largeur × ratio de l'image, capée à
  // la hauteur de design → plus de bande vide verticale (l'ombre/cadre épouse
  // l'image). `dataset.maxh` mémorise la hauteur de design (lue une seule fois,
  // avant tout resize ; valable même quand l'écran est display:none).
  function captureMaxH(zone) {
    if (!zone || zone.dataset.maxh) return;
    var cssH = parseFloat(getComputedStyle(zone).height);
    if (cssH > 0) zone.dataset.maxh = cssH;
  }
  function resetZoneHeight(zone) { if (zone) { zone.style.display = ""; zone.style.height = ""; } }   // hauteur de design
  function collapseZone(zone)    { if (zone) zone.style.display = "none"; }                            // bloc masqué (aucun média)
  function fitZoneHeight(zone, natW, natH) {
    if (!zone || !natW || !natH) return;
    captureMaxH(zone);
    var maxH = parseFloat(zone.dataset.maxh) || 0;
    var w = parseFloat(getComputedStyle(zone).width) || zone.clientWidth;
    if (maxH > 0 && w > 0) {
      zone.style.display = "";
      zone.style.height = Math.min(maxH, Math.round(w * natH / natW)) + "px";
    }
  }

  // ── Fanart en fond de la partie droite (option fanart) ──────────────────
  // detail.json fournit g.fanart (liste, via GameCache). On en tire UN AU HASARD — stable pour la
  // session via g._fanart (pas de scintillement à l'aller-retour games↔details) — et on le charge
  // APRÈS le reste (requestIdleCallback, faible priorité, AUCUN préchargement). Posé en fond des
  // couches .fanart (games + details) ; .has-fanart révèle l'opacité (config) avec un fondu.
  function pickFanart(g) {
    if (!g) return null;
    if (g._fanart) return g._fanart;                          // déjà tiré (truthy) → on garde le même
    // Source primaire : la liste fanart (regroupement "Background", LB).
    // Fallback : si aucune fanart, on tire au hasard parmi les screenshots
    // de gameplay (g.shots). Évite un hero/page-bg vide quand le jeu n'a
    // pas de fanart dédié mais a des captures.
    var list = (g.fanart && g.fanart.length) ? g.fanart
             : (g.shots  && g.shots.length)  ? g.shots
             : null;
    if (!list) return null;                                   // pas (encore) chargé : NE PAS mémoriser null,
                                                               // sinon une race (cache off → detail.json en retard
                                                               // par rapport au timer schedulePosterFanart) fige
                                                               // g._fanart = null à jamais.
    g._fanart = list[Math.floor(Math.random() * list.length)];
    return g._fanart;
  }
  function setFanartBg(url) {
    var img = 'url("' + String(url).replace(/"/g, "%22") + '")';
    [screens.games, screens.details].forEach(function (sc) {
      if (!sc) return; var el = $(".fanart", sc); if (!el) return;
      el.style.backgroundImage = img;
      // (re)démarre le fondu DEPUIS 0 : opacité 0 sans transition + reflow, puis .has-fanart
      // anime vers l'opacité config (fadeMs) → toujours un fondu complet, même en remplaçant un fanart.
      el.classList.remove("has-fanart");
      el.style.transition = "none"; el.style.opacity = "0";
      void el.offsetWidth;                                   // reflow : fige l'état "0"
      el.style.transition = ""; el.style.opacity = "";       // rend la main au CSS (transition var)
      el.classList.add("has-fanart");
    });
  }
  function clearFanart() {
    [screens.games, screens.details].forEach(function (sc) {
      if (!sc) return; var el = $(".fanart", sc); if (!el) return;
      el.classList.remove("has-fanart"); el.style.backgroundImage = "";
    });
  }
  function applyFanart(gi) {
    if (!G.fanart || G.fanart.enabled === false) { clearFanart(); return; }
    var g = DATA.games[gi];
    // pickFanart gère elle-même le fallback vers g.shots quand g.fanart
    // est vide → on lui passe le jeu sans pré-check, elle renvoie null
    // si vraiment rien à afficher.
    var url = g ? pickFanart(g) : null;
    if (!url) { clearFanart(); return; }                       // pas (encore) de fanart → fond nu
    var tok = mediaToken;
    var load = function () {
      if (tok !== mediaToken) return;                          // sélection changée → on abandonne
      var im = new Image();
      im.onload = function () { if (tok === mediaToken) setFanartBg(url); };
      im.src = url;                                            // 1ère charge ici (servi en ?q=full, pas de cache dégradé)
    };
    if (window.requestIdleCallback) requestIdleCallback(load, { timeout: 1500 });
    else setTimeout(load, 250);
  }

  function requestGameDetail(gi) {
    var g = DATA.games[gi];
    if (!g || g.id == null || g._det === true || detailInFlightId === g.id) return;   // dummy / déjà chargé / déjà en vol
    var myToken = mediaToken;
    detailInFlightId = g.id;
    detailAbort = (typeof AbortController !== "undefined") ? new AbortController() : null;
    var opt = detailAbort ? { signal: detailAbort.signal } : undefined;
    // option extraScreenshots → ?extra=1 (le serveur réordonne/complète les screenshots)
    var detailUrl = "data/games/" + g.id + "/detail.json" + ((G.extraScreenshots && G.extraScreenshots.enabled) ? "?extra=1" : "");
    fetch(detailUrl, opt)
      .then(function (r) { return r.ok ? r.json() : null; })
      .then(function (det) {
        if (detailInFlightId === g.id) detailInFlightId = null;
        if (myToken !== mediaToken) return;        // sélection changée → on jette (g._det non posé → refetch possible)
        if (!det) return;
        g._det = true;
        if (det.d != null) g.d = det.d;
        // Backfill des metas de base (dev/pub/genre/star/esrb/year/title).
        // Pour les jeux atteints via la roue, games.json les fournit déjà
        // avant l'arrivée du detail.json — ce merge est un no-op. Pour
        // les jeux poussés via relActivate (cross-platform trampoline),
        // c'est ce merge qui remplit le meta row (sinon vide / undefined).
        if (det.t   != null) g.t   = det.t;
        if (det.y   != null) g.y   = det.y;
        if (det.dev != null) g.dev = det.dev;
        if (det.pub != null) g.pub = det.pub;
        if (det.g   != null) g.g   = det.g;
        if (det.r   != null) g.r   = det.r;
        if (det.esrb!= null) g.esrb= det.esrb;
        if (det.lbdbid != null) g.lbdbid = det.lbdbid;
        // Real-time store install state: store kind (GOG/Steam/Epic, null = non-store)
        // + installed. Drives the pill (fillGameDetailInner) and the Play/Install action.
        if (det.store !== undefined) g.store = det.store;
        if (typeof det.installed === "boolean") g.installed = det.installed;
        if (det.box) g.box = det.box;
        if (det.boxImg) g.boxImg = det.boxImg;
        if (det.shotImg) g.shotImg = det.shotImg;
        if (det.shots) g.shots = det.shots;     // captures pour la grille fillScreenshot
        if (det.video) g.video = det.video;
        if (det.fanart) g.fanart = det.fanart;  // liste des fanart (fond, tirage au hasard côté client)
        // Si on est en mode poster sur CE jeu et qu'un fanart OU des shots
        // viennent d'arriver (cas typique cache off : detail.json livré après
        // que le timer 500 ms de schedulePosterFanart ait déjà tiré sur des
        // listes vides), on re-déclenche le scheduler pour faire apparaître
        // le fond hero (fanart prioritaire ; fallback screenshot via
        // pickFanart) sans avoir besoin de naviguer ailleurs.
        if ((det.fanart || det.shots) && posterMode && currentGame === gi) schedulePosterFanart(g);
        if (det.vndb) g.vndb = det.vndb;        // tags VNDB groupés (action "View VNDB Tags")
        if (det.votes != null) g._votes = det.votes;
        // launchOptions arrive avec le détail (jamais dans le payload léger games.json) :
        // alt-emu / multi-disc / versions alternatives qui enrichissent le menu Play.
        if (det.launchOptions) g.launchOptions = det.launchOptions;
        // lastLaunch : empreinte du dernier Play sur ce jeu (côté backend SQLite).
        // Persisté toujours pour pouvoir resynchroniser après une entrée fraîche.
        g.lastLaunch = det.lastLaunch || null;
        // Si on est arrivé sur la page details depuis un autre écran et que la
        // sync attend, on aligne le state local de Select Version sur ce que le
        // dernier Play a persisté (on écrase un éventuel choix in-session non
        // lancé — voir applyLastLaunchSync).
        if (_pendingLastLaunchSync && current === "details" && currentGame === gi) {
          applyLastLaunchSync(g);
          _pendingLastLaunchSync = false;
        }
        if (posterMode && current === "games" && currentGame === gi) fillPosterMedia(gi);   // vue poster : média latéral
        else if (current === "games" && shownGame === gi) { fillGamePanel(screens.games, gi); descPlay(); }
        else if (current === "details" && currentGame === gi) { fillGamePanel(screens.details, gi); descPlay(); refreshDetailMenuVndb(); }
        // Store game now identified → start the live install re-check while it stays selected.
        if (det.store && currentGame === gi) startInstallPoll(gi);
        applyFanart(gi);   // fond fanart : chargé APRÈS le reste (le détail vient d'arriver)
      })
      .catch(function () { if (detailInFlightId === g.id) detailInFlightId = null; });   // abort/erreur → refetch possible
  }
  // id réel du jeu courant (mode plugin) ou null (dummy / standalone).
  function curGameId() { var g = DATA.games[currentGame]; return (g && g.id != null) ? g.id : null; }
  // POST best-effort vers une route de mutation (rating / launch / favorite…).
  // No-op en standalone (file://) ou sans id (dummy) ; l'UI locale reste optimiste.
  function postMutation(kind, body) {
    var id = curGameId(); if (id == null) return;
    if (!(location.protocol === "http:" || location.protocol === "https:")) return;
    try {
      fetch("api/games/" + id + "/" + kind, {
        method: "POST", headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body || {})
      }).catch(function () {});
    } catch (e) {}
  }

  // ── Live install-state re-check (store games only) ───────────────────────
  // After a store game is selected (and identified via detail.json), poll the
  // lightweight installstate.json every few seconds WHILE it stays selected, so an
  // install/uninstall done in the background flips the pill + Play/Install button
  // without navigating. Server scan is debounced/single-flight, so this never hammers.
  function stopInstallPoll() {
    if (installPollTimer) { clearTimeout(installPollTimer); installPollTimer = null; }
    installPollGi = -1;
  }
  function startInstallPoll(gi) {
    if (!bbwHttp()) return;
    if (installPollGi === gi && installPollTimer) return;   // already polling this game
    stopInstallPoll();
    installPollGi = gi;
    installPollTimer = setTimeout(function () { pollInstallOnce(gi); }, 4000);
  }
  function pollInstallOnce(gi) {
    installPollTimer = null;
    if (gi !== currentGame) { stopInstallPoll(); return; }
    var g = DATA.games[gi];
    if (!g || g.id == null) { stopInstallPoll(); return; }
    fetch("data/games/" + g.id + "/installstate.json", { cache: "no-store" })
      .then(function (r) { return r.ok ? r.json() : null; })
      .then(function (d) {
        if (!d || gi !== currentGame) { stopInstallPoll(); return; }
        if (typeof d.epoch === "number") installEpoch = d.epoch;
        if (!d.store) { stopInstallPoll(); return; }   // not a store game → stop polling
        var changed = (g.store !== d.store) || (g.installed !== d.installed);
        g.store = d.store; g.installed = d.installed;
        if (changed) refreshInstallUi(gi);
        installPollTimer = setTimeout(function () { pollInstallOnce(gi); }, 4000);   // keep polling
      })
      .catch(function () { stopInstallPoll(); });
  }
  // Re-render the visible game's badge + rebuild its action menu (Play/Install label).
  function refreshInstallUi(gi) {
    try {
      if (current === "games") {
        if (posterMode && currentGame === gi) fillPosterSide(gi);   // poster grid side panel
        else if (shownGame === gi) fillGamePanel(screens.games, gi); // list-with-details panel
      } else if (current === "details" && currentGame === gi) {
        fillGamePanel(screens.details, gi); refreshDetailMenuVndb();
      }
    } catch (e) {}
  }

  // Variante de postMutation dédiée au launch : lit la réponse pour ouvrir
  // le modal d'erreur quand le serveur refuse ({ ok:false, reason:… } —
  // fichier introuvable, jeu déjà en cours, …). Les autres mutations
  // restent fire-and-forget via postMutation().
  function postLaunch(body) {
    var id = curGameId(); if (id == null) return;
    if (!(location.protocol === "http:" || location.protocol === "https:")) return;
    try {
      fetch("api/games/" + id + "/launch", {
        method: "POST", headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body || {})
      })
        .then(function (r) { return r.json().catch(function () { return null; }); })
        .then(function (j) {
          if (j && j.ok === false && j.reason) {
            // Échec rapide (file_missing, déjà en cours, …) : libère le
            // verrou client posé juste avant l'envoi pour que l'utilisateur
            // puisse retenter dès qu'il a fixé le problème, sans attendre
            // l'expiration du debounce 5 s.
            playBlockUntilTs = 0;
            openLaunchErrorModal(j.reason);
          }
        })
        .catch(function () { /* erreurs réseau silencieuses */ });
    } catch (e) {}
  }

  // Palier : charge la version COMPLÈTE + la vidéo (par-dessus la dégradée) après
  // un délai d'immobilité. Reprogrammé/annulé à chaque changement de sélection.
  function scheduleHeavy(gi) {
    if (heavyTimer) { clearTimeout(heavyTimer); heavyTimer = null; }
    // Selection changed → drop the previous game's live install poll. If THIS game's
    // store kind is already known (revisit), restart the poll now; otherwise
    // requestGameDetail will start it once detail.json identifies it as a store game.
    stopInstallPoll();
    var gg = DATA.games[gi];
    if (gg && gg.store) startInstallPoll(gi);
    var d = (G.media && G.media.heavyDelayMs != null) ? G.media.heavyDelayMs : 300;
    heavyTimer = setTimeout(function () { heavyTimer = null; requestGameDetail(gi); }, d);
  }
  // Précharge (chauffe le cache navigateur, immutable) les vignettes dégradées
  // d'une fenêtre autour de la sélection, asymétrique dans le sens du défilement
  // (lastDir). Jamais annulé — les dégradées sont petites, on les garde.
  function prefetchThumbWindow(centerIdx) {
    if (!DATA.games || !DATA.games.length) return;
    var m = G.media || {};
    var ahead = (m.prefetchAhead != null) ? m.prefetchAhead : 15;
    var behind = (m.prefetchBehind != null) ? m.prefetchBehind : 5;
    var dir = (lastDir >= 0) ? 1 : -1;
    var lo = centerIdx - (dir > 0 ? behind : ahead);
    var hi = centerIdx + (dir > 0 ? ahead : behind);
    var warm = function (u) {
      if (!u || prefetchedThumbs[u]) return;
      prefetchedThumbs[u] = true;
      var im = new Image(); im.src = u;   // pas de DOM : juste chauffer le cache
    };
    var logoOn = G.gameLogo && G.gameLogo.enabled !== false;   // zone clear-logo du jeu active ?
    var run = function () {
      for (var i = lo; i <= hi; i++) {
        if (i < 0 || i >= DATA.games.length) continue;
        var gg = DATA.games[i]; if (!gg) continue;
        warm(gg.thumb); warm(gg.shotThumb);   // jaquette + capture dégradées
        if (logoOn) warm(gg.logo);            // clear logo dégradé (même fenêtre que les vignettes)
      }
    };
    if (window.requestIdleCallback) window.requestIdleCallback(run, { timeout: 600 });
    else setTimeout(run, 60);
  }

  function listLength(s) { return screens[s].querySelectorAll(".list .list-item").length; }

  function setSelected(screenId, i, opts) {
    opts = opts || {};
    descClear();   // changement de sélection → on stoppe le défilement de description en cours
    var n = listLength(screenId); if (n <= 0) n = 1; i = (i % n + n) % n;
    if (screenId === "categories") curFrame().sel = i;
    else if (screenId === "details") detailLevel().sel = i;
    else sel[screenId] = i;
    var root = screens[screenId];
    root.querySelectorAll(".list .list-item").forEach(function (it, k) { it.classList.toggle("selected", k === i); });
    if (screenId === "games") {
      $(".page", root).textContent = (i + 1) + " / " + DATA.platformTotal;
      scheduleGameContent(i, opts.instant);   // transition par zone (ou remplissage instantané)
    } else if (screenId === "categories") {
      scheduleCatContent(i, opts.instant);
    }
    positionHighlight(screenId, opts.instant);
    scrollListIntoView(screenId, opts.instant);
    if (!opts.instant) flashHighlight(screenId);   // éclat bref au changement
    scheduleShine();                                // (re)programme le reflet idle
    // détail : contenu posé ci-dessus → (re)lance le défilement desc. (catégories +
    // jeux : contenu différé → relancé par leur scheduleXxxContent / doXxxTransition.)
    if (screenId === current && current !== "categories" && current !== "games") descPlay();
    labelScrollPlay(screenId);   // marquee du label sélectionné s'il dépasse le bouton
    // Archive MultiGame Selector overlay : si on est dans le sous-menu
    // Select ROM, refetch + affiche le template.html rendu pour l'entrée
    // surlignée. updateArchiveMetaOverlay() est un no-op (et masque
    // l'overlay) si on n'est pas dans ce niveau.
    if (screenId === "details") {
      try { updateArchiveMetaOverlay(); } catch (_) {}
    }
    // Synchronise le thumb de la scrollbar opt-in avec le nouveau sel
    // (clavier, manette OU drag du thumb lui-même). No-op si la scrollbar
    // n'a pas été initialisée (option off / mode non-desktop).
    if (window.BBW && window.BBW.updateListScrollbar) {
      try { window.BBW.updateListScrollbar(screenId); } catch (_) {}
    }
  }

  function move(delta, instant) {
    lastDir = delta > 0 ? 1 : -1;
    var cur = currentSel(current);
    setSelected(current, cur + delta, { instant: instant });
  }

  // ── Transitions / navigation d'écran ────────────────────────────────────
  function enterStyle(type, back) {
    if (type === "slide-v") { var p = G.screenTransition.slidePx; return { o: 0, t: "translateY(" + (back ? -p : p) + "px)" }; }
    return { o: 0, t: "none" };
  }
  function leaveStyle(type, back) {
    if (type === "slide-v") { var p = G.screenTransition.slidePx; return { o: 0, t: "translateY(" + (back ? p : -p) + "px)" }; }
    return { o: 0, t: "none" };
  }
  function navTo(target, back) {
    if (target === current) return;
    if (searchOpen) closeSearch();   // on quitte l'écran jeux → ferme le clavier de recherche
    if (advOpen) closeAdvanced();    // idem : ferme la modale de recherche avancée si ouverte
    // Filtres transitoires (rail ★ favoris / ☰ avancée) : ils appartiennent à l'« univers jeu »
    // (roue games + fiche details). On les GARDE tant qu'on y reste — y compris l'aller-retour
    // games→details→games (clic sur un jeu puis Précédent) — et on ne les lève QUE lorsqu'on
    // remonte AU-DESSUS de la roue (vers catégories / system).
    var inGameUniverse = function (s) { return s === "games" || s === "details"; };
    var leavingGameUniverse = inGameUniverse(current) && !inGameUniverse(target);
    if (favOnly && leavingGameUniverse) clearFavFilter(false);
    if (advActive && leavingGameUniverse) clearAdvanced();
    // En quittant l'univers "jeu" (vers catégories/system), on coupe les
    // chargements média encore en vol. On NE coupe PAS pour games↔details
    // (même jeu : on veut garder/poursuivre son média).
    if (target !== "games" && target !== "details") cancelGameContentLoads();
    // Quitter l'écran details → cache l'overlay Archive Metadata pour
    // ne pas le voir flasher au retour (le contenu est revalidé via
    // setSelected → updateArchiveMetaOverlay si on re-rentre dans le
    // sous-menu Select ROM).
    if (current === "details" && target !== "details") {
      try { setArchiveMetaVisible(false); } catch (_) {}
    }
    deactivateMouse();   // un changement d'écran : pas d'auto-survol sous le curseur
    descClear();         // stoppe le défilement de description de l'écran qu'on quitte
    if (catTimer) { clearTimeout(catTimer); catTimer = null; }   // pas de transition fantôme
    // réinitialise les zones quand on change d'écran
    if (current === "games") exitRail();
    zone = "list";
    var cur = screens[current], nxt = screens[target];

    if (window.BBW.isMobile()) {
      cur.classList.remove("active"); nxt.classList.add("active");
      window.scrollTo(0, 0); current = target;
      positionHighlight(target, true); scrollListIntoView(target, true);  // recentre une fois visible
      scheduleShine();
      return;
    }
    var type = screenEnter(target), st = enterStyle(type, back);
    nxt.style.transition = "none"; nxt.style.opacity = st.o; nxt.style.transform = st.t;
    nxt.classList.add("active"); void nxt.offsetWidth;
    nxt.style.transition = ""; nxt.style.opacity = 1; nxt.style.transform = "none";
    var lv = leaveStyle(type, back);
    cur.style.opacity = lv.o; cur.style.transform = lv.t;
    var leaving = cur;
    setTimeout(function () { leaving.classList.remove("active"); leaving.style.transform = "none"; }, G.screenTransition.durationMs + 20);
    current = target;
    scheduleShine();
    descPlay();   // (re)lance le défilement de description pour le nouvel écran
    labelScrollPlay(target);   // marquee du label sélectionné à l'arrivée sur l'écran
  }

  // slug d'une plateforme (même algo que data/dummy.js) → chemin data/platforms/<slug>/games.json
  function slugify(s) { return String(s).toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-|-$/g, ""); }
  // Charge la liste de jeux d'une feuille (plateforme OU playlist) via son sous-chemin
  // data (ex. "platforms/ms-dos" ou "playlists/favoris"), reconstruit la liste, puis cb().
  function loadPlatform(path, cb) {
    window.BBW.get("data/" + path + "/games.json").then(function (pl) {
      pl = pl || { games: [] };
      DATA.platform = pl.platform || ""; DATA.platformLogo = pl.platformLogo || "";
      DATA.platformLogoImg = pl.platformLogoImg || "";
      DATA.games = sortGamesAlpha(applyLibraryFilter(pl.games || []));   // filtre biblio + tri alphabétique
      DATA.gamesAll = DATA.games; DATA.platformTotal = DATA.games.length; DATA.platformTotalAll = DATA.platformTotal; resetSearch();   // nouvelle plateforme → recherche remise à zéro
      setupList("games", DATA.games.map(function (g) { return g.t; })); markGameStars("games");   // reconstruit la liste + marque favoris
      loadPlatformStars(path);   // paliers qualité en arrière-plan (plateformes seulement)
      var tt = $(".topbar .title", screens.games); if (tt) tt.textContent = DATA.platform || "";
      // La plateforme (logo/texte) est posée par le remplissage du détail (fillGameDetailInner) :
      // elle vit dans .detail .rgn-inner. cb() déclenche ce remplissage.
      if (cb) cb();
    });
  }

  function descend() {
    if (current === "categories") {
      var node = catNode(curFrame().sel);
      if (node && node.children && node.children.length) enterCatLevel(node.children, lastDir);   // groupe → sous-niveau
      else {   // feuille (plateforme ou playlist) → charge ses jeux puis l'écran jeux
        var path = node.path || ("platforms/" + (node.slug || slugify(node.name)));   // chemin serveur si fourni, sinon dérivé du nom
        loadPlatform(path, function () {
          shownGame = -1; setSelected("games", 0, { instant: true });
          // BUG #1 FIX: buildPosterGrid() (called by setupList inside loadPlatform) rebuilds
          // the grid thumbnails but never calls posterSelect(), so the poster side panel keeps
          // the previous platform's data. Force a refresh here for the newly selected game 0.
          if (posterMode) posterSelect(0);
          navTo("games", false);
        });
      }
    }
    else if (current === "games") {
      currentGame = sel.games; detailsReturn = "games";
      // Entrée fraîche sur details : invalide le cache local pour récupérer un
      // lastLaunch potentiellement mis à jour, et arme la sync (cf. requestGameDetail).
      var _gFresh2 = DATA.games[currentGame]; if (_gFresh2) _gFresh2._det = false;
      _pendingLastLaunchSync = true;
      requestGameDetail(currentGame); fillGamePanel(screens.details, currentGame);
      resetDetailMenu(); setSelected("details", 0, { instant: true }); navTo("details", false);
    }
    else if (current === "details") detailDescend();   // sous-menu d'action (récursif)
    else if (current === "system") {
      var sit = $(".list-item.selected", screens.system);
      var t = sit ? sit.textContent.trim() : "";
      if (sit && sit.getAttribute("data-sys") === "settings") { openConfig(); return; }   // → UI Réglages
      if (sit && sit.getAttribute("data-sys") === "exit") {
        // Mode kiosque embarqué : poste 'kiosk:exit' au host WebView2 qui
        // ferme la fenêtre. L'entrée est retirée du DOM en mode standalone
        // (cf. bloc isEmbedded au boot), donc on n'arrive ici qu'en kiosque.
        try {
          if (window.chrome && window.chrome.webview && window.chrome.webview.postMessage)
            window.chrome.webview.postMessage("kiosk:exit");
        } catch (e) { /* indispo (standalone) → no-op */ }
        return;
      }
      if (t === "Unlock" || t === "Lock") {
        if (parental.bigBox) openParentalInfo();      // BigBox : contrôle parental global → message
        else if (t === "Unlock") openPinPad();        // LaunchBox : digicode → /api/parental/unlock
        else lockNow();                               // LaunchBox : re-verrouille (efface le cookie)
      }
      else goBack();                       // Back / About → ferme le menu (stubs)
    }
  }
  function goBack() {
    if (current === "games") navTo("categories", true);
    else if (current === "details") detailBack();   // remonte d'un sous-menu, ou racine → liste de jeux
    else if (current === "system") navTo("categories", true);
    else if (current === "categories") catBack();   // dépile un niveau, ou racine → menu
  }
  // « Valider » en respectant la zone active (liste/rail/Recent).
  function activateCurrent() {
    if (current === "games" && zone === "rail") railActivate();
    else if (current === "categories" && zone === "recent") recentActivate();
    else descend();
  }

  // ── Turbo de défilement (maintien prolongé Haut/Bas) ─────────────────────
  // Quand la direction est maintenue (repeat=true) depuis ≥ turboDelayMs sans
  // changer de sens, on multiplie le pas de défilement par turboMultiplierPct/100
  // pour traverser plus vite les longues roues (10 k+). State global au scope du
  // module : reset au relâchement (repeat=false), au changement de direction, ou
  // à toute commande ≠ haut/bas — voir turboStep().
  var navTurboStart = 0;     // performance.now() au début de la phase maintenue
  var navTurboDir   = null;  // dernière direction ayant armé le turbo

  // amount > 0 (toujours), cmd = "up"|"down"|autre, repeat = direction maintenue.
  // Renvoie le pas effectif à appliquer (1, ou amount × multiplicateur).
  function turboStep(amount, cmd, repeat) {
    var c = (G && G.lists) || {};
    if (!c.turboEnabled || (cmd !== "up" && cmd !== "down")) {
      navTurboStart = 0; navTurboDir = null;
      return amount;
    }
    // Première pression OU changement de direction → re-arme le compteur.
    if (!repeat || navTurboDir !== cmd) {
      navTurboStart = performance.now();
      navTurboDir = cmd;
      return amount;
    }
    var delay = c.turboDelayMs != null ? c.turboDelayMs : 3000;
    var pct   = c.turboMultiplierPct != null ? c.turboMultiplierPct : 300;
    if (delay < 0 || pct <= 100) return amount;
    if (performance.now() - navTurboStart < delay) return amount;
    return amount * Math.max(1, Math.round(pct / 100));
  }

  // ── Saut de page (PgUp / PgDn clavier · L2 / R2 manette) ───────────────
  // Saute immédiatement N items (pageStepN, défaut 5) dès le 1er appui — sans
  // attendre le délai turbo. Si la touche/gâchette reste enfoncée, le multiplicateur
  // monte par paliers : ×1 immédiatement, ×2 après 500 ms, ×4 après 1500 ms.
  // State distinct du turbo Up/Down pour ne pas interférer avec lui.
  var pgHoldStart = 0;    // performance.now() quand le saut de page a commencé
  var pgHoldDir   = null; // "pgup" | "pgdn" | null

  // Renvoie le pas effectif (pageStepN × multiplicateur) pour la commande
  // pgup/pgdn. repeat=true quand la touche est maintenue (e.repeat ou boucle manette).
  function pageStep(cmd, repeat) {
    var c   = (G && G.lists) || {};
    var n   = c.pageStepN != null ? c.pageStepN : 5;
    if (cmd !== "pgup" && cmd !== "pgdn") { pgHoldStart = 0; pgHoldDir = null; return n; }
    if (!repeat || pgHoldDir !== cmd) {
      // Première pression ou changement de sens : arme le compteur, retourne n (×1).
      pgHoldStart = performance.now();
      pgHoldDir   = cmd;
      return n;
    }
    var held = performance.now() - pgHoldStart;
    // ×4 après 1500 ms, ×2 après 500 ms, ×1 sinon.
    var mult = held >= 1500 ? 4 : held >= 500 ? 2 : 1;
    return n * mult;
  }

  // ── Navigation logique (clavier + manette) ──────────────────────────────
  // cmd ∈ up | down | left | right | select | back | menu | pgup | pgdn.
  // repeat = direction maintenue (touche auto-répétée / stick tenu) → highlight
  // instantané (pas de glissement), comme un appui clavier maintenu.
  function handleNav(cmd, repeat) {
    if (!screens[current]) return;     // pas encore initialisé (entrée très précoce)
    deactivateMouse();                 // une entrée clavier/manette désactive la souris
    playNavSound(cmd);                 // son de navigation (move/select/back) — comme BigBox
    // Message info (BigBox) : n'importe quelle validation/retour le ferme.
    if (infoOpen) { if (cmd === "select" || cmd === "back") closeParentalInfo(); return; }
    // Modal erreur de lancement : A/B ferme (et plus rien ne se passe d'autre).
    if (launchErrOpen) { if (cmd === "select" || cmd === "back") closeLaunchErrorModal(); return; }
    // Popup note ouvert : gauche/droite = ±½ étoile, A valide, B annule (le reste est ignoré).
    if (ratingOpen) {
      if (cmd === "left") ratingAdjust(-1);
      else if (cmd === "right") ratingAdjust(1);
      else if (cmd === "select") confirmRating();
      else if (cmd === "back") closeRating();
      return;
    }
    // Popup digicode (Unlock) : flèches déplacent, A presse la touche, B ferme.
    if (pinOpen) {
      if (cmd === "select") pinPress(pinKeys[pinFocus].dataset.k);
      else if (cmd === "back") closePinPad();
      else pinMove(cmd);
      return;
    }
    // Modal LB-DB iframe (stack au-dessus du popup Related) : B ferme et
    // rend la main au Related encore visible derrière.
    if (lbdbOpen) {
      if (cmd === "back" || cmd === "select") closeLbDbModal();
      return;
    }
    // Popup Related Games : ←/→ changent d'onglet, ↑/↓ parcourent la liste,
    // A active (local → fiche, DB-only → iframe modal), B ferme.
    if (relOpen) {
      if (cmd === "left") relTabMove(-1);
      else if (cmd === "right") relTabMove(1);
      else if (cmd === "up") relMove(-1);
      else if (cmd === "down") relMove(1);
      else if (cmd === "select") relActivate();
      else if (cmd === "back") closeRelated();
      return;
    }
    // Popup Tags VNDB : ↑/↓ défilent, A/B ferment.
    if (vndbOpen) {
      if (cmd === "up") vndbScroll(-1);
      else if (cmd === "down") vndbScroll(1);
      else if (cmd === "back" || cmd === "select") closeVndbTags();
      return;
    }
    // UI Réglages : L/R onglets · Haut/Bas focus · Gauche/Droite ajuste · A remet par défaut · B ferme.
    if (cfgOpen) {
      if (cmd === "mediaNext") cfgMoveTab(1);
      else if (cmd === "mediaPrev") cfgMoveTab(-1);
      else if (cmd === "up") { cfgFocus = Math.max(0, cfgFocus - 1); paintCfg(); }
      else if (cmd === "down") { cfgFocus = Math.min(cfgTargets.length - 1, cfgFocus + 1); paintCfg(); }
      else if (cmd === "left") cfgAdjust(-1);
      else if (cmd === "right") cfgAdjust(1);
      else if (cmd === "select") cfgActivate();
      else if (cmd === "back") closeConfig();
      return;
    }
    // Clavier (recherche rapide OU auto-complétion avancée) : prioritaire sur la modale avancée.
    if (searchOpen) {
      if (cmd === "select") searchPress(KB[searchR][searchC]);
      else if (cmd === "back") { if (kbMode === "adv") closeAdvKeyboard(); else closeSearch(); }
      else if (cmd === "up" || cmd === "down" || cmd === "left" || cmd === "right") searchMove(cmd);
      return;
    }
    // Recherche AVANCÉE : L/R (gâchettes / Tab) changent d'onglet (bouclage), Haut/Bas déplacent
    // le focus, Gauche/Droite ajustent, A active, B ferme.
    if (advOpen) {
      if (cmd === "mediaNext") advMoveTab(1);
      else if (cmd === "mediaPrev") advMoveTab(-1);
      else if (cmd === "up") {
        // Onglets Éditeur/Dév : depuis la textbox (1er focus), Haut saute au bouton Appliquer
        // (la liste de suggestions peut être longue → raccourci pour valider sans tout parcourir).
        var up = advTargets[advFocus];
        if (up && up.type === "textfield" && advTargets.length && advTargets[advTargets.length - 1].type === "apply")
          advFocus = advTargets.length - 1;
        else advFocus = Math.max(0, advFocus - 1);
        paintAdv();
      }
      else if (cmd === "down") { advFocus = Math.min(advTargets.length - 1, advFocus + 1); paintAdv(); }
      else if (cmd === "left") advAdjust(-1);
      else if (cmd === "right") advAdjust(1);
      else if (cmd === "select") advActivate();
      else if (cmd === "back") closeAdvanced();
      return;
    }
    // Modale avancée ROM (Select ROM submenu). Drill-in pattern : un Right ou A
    // sur "Select Region(s)" / "Select Lang(s)" entre dans la liste (sous-vue).
    // Dans la sous-vue, B ou Left = retour à la vue principale (pas fermeture).
    if (romAdvOpen) {
      if (cmd === "mediaNext") romAdvMoveTab(1);
      else if (cmd === "mediaPrev") romAdvMoveTab(-1);
      else if (cmd === "up")   { romAdvFocus = Math.max(0, romAdvFocus - 1); paintRomAdv(); }
      else if (cmd === "down") { romAdvFocus = Math.min(romAdvTargets.length - 1, romAdvFocus + 1); paintRomAdv(); }
      else if (cmd === "left") {
        // Left dans une sous-vue = drill-out. Sinon = ajustement normal.
        if (romAdvSubView) romAdvDrillOut(); else romAdvAdjust(-1);
      }
      else if (cmd === "right") romAdvAdjust(1);
      else if (cmd === "select") romAdvActivate();
      else if (cmd === "back") {
        if (romAdvSubView) romAdvDrillOut(); else closeRomAdv();
      }
      return;
    }
    // Bascule VUE POSTER (Tab · bouton View/Select manette · swipe bas tablette) — écran jeux.
    if (cmd === "poster") { if (current === "games") togglePoster(); return; }
    // Navigation dans la grille POSTER (quand le rail n'est pas ouvert ; le rail garde sa branche).
    if (current === "games" && posterMode && zone !== "rail") {
      if (cmd === "up") posterMove(-posterCols);
      else if (cmd === "down") posterMove(posterCols);
      else if (cmd === "pgup") posterMove(-posterCols * pageStep(cmd, repeat)); // saut de page ← colonnes entières
      else if (cmd === "pgdn") posterMove( posterCols * pageStep(cmd, repeat));
      else if (cmd === "left") { if (posterSel % posterCols === 0) openRail(); else posterMove(-1); }
      else if (cmd === "right") posterMove(1);
      else if (cmd === "mediaNext") posterMediaCycle(1);    // L/R gâchettes · ArrowRight → captures du panneau
      else if (cmd === "mediaPrev") posterMediaCycle(-1);
      else if (cmd === "select") descend();
      else if (cmd === "back") goBack();
      return;   // consomme aussi "menu" en mode poster
    }
    // Carrousel média : ArrowRight / Shift+ArrowRight (clavier) · LB/RB (manette). Défile vidéo + screenshots.
    // Roue jeux (zone list, hors poster) + fiche détail.
    if (cmd === "mediaNext" || cmd === "mediaPrev") {
      if ((current === "games" && !posterMode) || current === "details") mediaCycle(cmd === "mediaNext" ? 1 : -1);
      return;
    }
    // Zone "recent" (catégories)
    if (current === "categories" && zone === "recent") {
      if (cmd === "right") { recentSel = Math.min(recentSel + 1, recentCount() - 1); paintRecent(); }
      else if (cmd === "left") { if (recentSel === 0) { zone = "list"; paintRecent(); } else { recentSel--; paintRecent(); } }
      else if (cmd === "up" || cmd === "down" || cmd === "back") { zone = "list"; paintRecent(); }
      else if (cmd === "select") recentActivate();
      return;
    }
    // Zone "rail" (jeux)
    if (current === "games" && zone === "rail") {
      if (cmd === "down")      { railSel = Math.min(railSel + turboStep(1, "down", repeat), RAIL.length - 1); paintRail(); }
      else if (cmd === "up")   { railSel = Math.max(railSel - turboStep(1, "up",   repeat), 0);                paintRail(); }
      else if (cmd === "right" || cmd === "back") exitRail();
      else if (cmd === "select") railActivate();
      return;
    }
    // Zone "rail" (details / Select ROM submenu)
    if (current === "details" && zone === "rail") {
      if (cmd === "down")      { romRailSel = Math.min(romRailSel + turboStep(1, "down", repeat), ROM_RAIL.length - 1); paintRomRail(); }
      else if (cmd === "up")   { romRailSel = Math.max(romRailSel - turboStep(1, "up",   repeat), 0);                   paintRomRail(); }
      else if (cmd === "right" || cmd === "back") exitRomRail();
      else if (cmd === "select") romRailActivate();
      return;
    }
    // Zone "list" (par défaut)
    switch (cmd) {
      case "down": move(turboStep(1, "down", repeat), repeat); break;
      case "up":   move(-turboStep(1, "up",   repeat), repeat); break;
      case "pgdn": move( pageStep(cmd, repeat), true); break;   // saut de page vers le bas (instant=true : pas d'animation sur maintien)
      case "pgup": move(-pageStep(cmd, repeat), true); break;   // saut de page vers le haut
      case "right": if (current === "categories") { zone = "recent"; recentSel = 0; paintRecent(); } break;
      case "left":
        if (current === "games") openRail();
        else if (current === "details" && _inRomMenu()) openRomRail();
        break;
      case "select": descend(); break;
      case "back": goBack(); break;
      case "menu": if (current === "categories") navTo("system", false); break;
    }
  }

  // Une modale/overlay capture-t-elle actuellement la navigation ? (sert à empêcher les
  // réécritures contextuelles de touches, ex. right→mediaNext, de voler les flèches aux modales.)
  function anyOverlayOpen() {
    return infoOpen || launchErrOpen || ratingOpen || pinOpen || lbdbOpen || relOpen ||
           vndbOpen || cfgOpen || advOpen || romAdvOpen || searchOpen;
  }

  // ── Clavier → commandes (selon écran + zone) ────────────────────────────
  function onKey(e) {
    var cmd;
    // Recherche ouverte : on tape AUSSI au clavier physique (lettres/chiffres → requête,
    // Backspace → efface, Échap/Entrée → ferme). Les flèches tombent dans le switch (déplacent
    // la touche via handleNav). Sinon « a » taperait « a » ET vaudrait select : on intercepte.
    if (searchOpen) {
      if (e.key === "Backspace") { searchPress("{bksp}"); e.preventDefault(); return; }
      if (e.key === "Escape" || e.key === "Enter") { closeSearch(); e.preventDefault(); return; }
      if (e.key === " " || e.key === "Spacebar") { searchPress("{space}"); e.preventDefault(); return; }
      if (e.key.length === 1 && /[0-9a-zA-Z]/.test(e.key)) { searchPress(e.key.toUpperCase()); e.preventDefault(); return; }
      // (flèches : laissées au switch ci-dessous → searchMove)
    }
    // Touche → commande via la table configurable (serveur). Espace normalisé en
    // "Spacebar". Touche inconnue → ignore.
    cmd = KEYCMD[(e.key === " ") ? "Spacebar" : e.key];
    if (!cmd) return;
    // "right" est contextuel : sur l'écran détail, ou sur l'écran jeux en mode roue
    // (zone list), il pilote le carrousel média (Shift = précédent). Partout ailleurs
    // c'est le déplacement "right" habituel. (mediaNext/mediaPrev non rebindables.)
    // MAIS quand une modale capture la navigation (PIN pad, note, Related…), gauche/droite
    // lui appartiennent : sans ce garde, "right" devient "mediaNext" et le pavé PIN, ouvert
    // depuis la fiche, ne peut plus se déplacer horizontalement.
    if (cmd === "right" && !anyOverlayOpen() && (current === "details" || (current === "games" && !posterMode && zone === "list"))) {
      cmd = e.shiftKey ? "mediaPrev" : "mediaNext";
    }
    handleNav(cmd, e.repeat);
    e.preventDefault();
  }

  // ── Barre du bas cliquable ──────────────────────────────────────────────
  // Horloge live : remplace le « 2:59 PM » figé du HTML par l'heure courante dans les .clock
  // des topbars. Format 12 h « h:MM AM/PM » par défaut (config.clock.ampm=false → 24 h
  // « HH:MM »). On ne réécrit le DOM que lorsque la chaîne change (pas de churn par seconde).
  function startClock() {
    var c = G.clock || {};
    if (c.enabled === false) return;
    var els = document.querySelectorAll(".topbar .clock");
    if (!els.length) return;
    function two(n) { return n < 10 ? "0" + n : "" + n; }
    var last = "";
    function tick() {
      var now = new Date(), h = now.getHours(), m = two(now.getMinutes()), s;
      if (c.ampm === false) { s = two(h) + ":" + m; }
      else { var ap = h >= 12 ? "PM" : "AM", h12 = h % 12; if (h12 === 0) h12 = 12; s = h12 + ":" + m + " " + ap; }
      if (s !== last) { last = s; for (var i = 0; i < els.length; i++) els[i].textContent = s; }
    }
    tick();
    setInterval(tick, 1000);
  }

  // ── Indicateurs kiosque (debug, opt-in) ─────────────────────────────────
  // Deux pills posées dans chaque topbar juste avant l'horloge — gated par
  // config.debug.kioskIndicators (off par défaut).
  //
  // FOCUS  : document.hasFocus() + events focus/blur. Permet de voir d'un
  //          coup d'œil si la fenêtre WebView2 a effectivement le focus
  //          quand le synthetic-gesture CDP s'exécute (sans focus, certaines
  //          versions d'Edge n'élèvent pas l'engagement de l'origin).
  // SON    : navigator.getAutoplayPolicy("mediaelement") (Chromium 102+
  //          dispo dans Edge récent / WebView2 récent). Valeurs Edge :
  //          "allowed" / "allowed-muted" / "disallowed". Fallback :
  //          navigator.userActivation.hasBeenActive (true dès qu'un user
  //          gesture, vrai ou CDP, a touché le document).
  //
  // Chaque transition est loggée à la console avec un timestamp
  // performance.now() pour corréler avec la synthèse CDP de l'hôte
  // (Forms/BigBoxWebKioskFormsWindow — Input.dispatchKeyEvent et
  // dispatchMouseEvent envoyés après NavigationCompleted).
  function startKioskIndicators() {
    var dbg = (G && G.debug) || {};
    if (!dbg.kioskIndicators) return;

    var clocks = document.querySelectorAll(".topbar .clock");
    if (!clocks.length) return;

    function mkPill(initialTxt) {
      var s = document.createElement("span");
      s.className = "kiosk-debug-ind";
      s.style.cssText =
        "display:inline-block;margin-right:6px;padding:2px 8px;border-radius:6px;" +
        "font-size:11px;font-weight:bold;letter-spacing:0.5px;vertical-align:middle;" +
        "background:#444;color:#fff;font-family:monospace;";
      s.textContent = initialTxt;
      return s;
    }

    var pairs = [];   // [{focus, sound}, ...]
    Array.prototype.forEach.call(clocks, function (clock) {
      var pFocus = mkPill("Foc?");
      var pSound = mkPill("Son?");
      // Ordre visuel : [Focus] [Son] [Heure]
      clock.parentNode.insertBefore(pFocus, clock);
      clock.parentNode.insertBefore(pSound, clock);
      pairs.push({ focus: pFocus, sound: pSound });
    });

    function probeSound() {
      try {
        if (navigator.getAutoplayPolicy) {
          var p = navigator.getAutoplayPolicy("mediaelement");
          if (p === "allowed")        return { t: "SonON",   c: "#1e7d39", raw: p };
          if (p === "allowed-muted")  return { t: "SonMUTE", c: "#b88300", raw: p };
          return                            { t: "SonOFF",  c: "#b80000", raw: p };
        }
      } catch (e) {}
      try {
        if (navigator.userActivation) {
          return navigator.userActivation.hasBeenActive
            ? { t: "Gest✓", c: "#1e7d39", raw: "userActivation.hasBeenActive" }
            : { t: "Gest✗", c: "#b80000", raw: "userActivation.none" };
        }
      } catch (e) {}
      return { t: "Son?", c: "#666", raw: "no-api" };
    }

    function probeFocus() {
      try {
        return document.hasFocus()
          ? { t: "Foc✓", c: "#1e7d39", raw: "hasFocus=true" }
          : { t: "Foc✗", c: "#b80000", raw: "hasFocus=false" };
      } catch (e) {
        return { t: "Foc?", c: "#666", raw: "exception:" + e };
      }
    }

    var lastSnd = "", lastFoc = "";
    function tick() {
      var snd = probeSound(), foc = probeFocus();
      var ts;
      if (snd.t !== lastSnd) {
        ts = Math.round(performance.now());
        try { console.log("[bbw-debug] sound -> " + snd.t + " (" + snd.raw + ") @" + ts + "ms"); } catch (_) {}
        lastSnd = snd.t;
        for (var i = 0; i < pairs.length; i++) {
          pairs[i].sound.textContent = snd.t;
          pairs[i].sound.style.background = snd.c;
        }
      }
      if (foc.t !== lastFoc) {
        ts = Math.round(performance.now());
        try { console.log("[bbw-debug] focus -> " + foc.t + " (" + foc.raw + ") @" + ts + "ms"); } catch (_) {}
        lastFoc = foc.t;
        for (var j = 0; j < pairs.length; j++) {
          pairs[j].focus.textContent = foc.t;
          pairs[j].focus.style.background = foc.c;
        }
      }
    }
    tick();
    setInterval(tick, 300);
    // Les events focus/blur garantissent un rafraîchissement immédiat sans
    // attendre le prochain tick — utile pour ne pas rater un blur très court.
    window.addEventListener("focus", tick);
    window.addEventListener("blur", tick);
  }

  // ── Splitter roue↔contenu (drag souris, desktop, opt-in) ──────────────
  // Active uniquement si lists.resizable=true ET BBW.mode()==="desktop".
  // Pose un .bbw-list-splitter dans chaque écran à roue principale (categories
  // + games + system). Le drag ajuste --bbw-list-w via document.documentElement,
  // ce qui fait reflow la roue + tous les panneaux droite (cf. styles.css).
  // La largeur finale est persistée via BBW.cfg.set("lists.widthPx", ...).
  function startListResizer() {
    if (!G || !G.lists || !G.lists.resizable) return;
    if (!window.BBW || !window.BBW.mode || window.BBW.mode() !== "desktop") return;

    document.body.classList.add("bbw-list-resize");

    // Pose une instance du splitter dans chaque écran qui utilise la roue
    // principale. Les autres écrans (poster mode est géré ailleurs ; mobile
    // est gated par la branche desktop ci-dessus) n'ont pas d'instance.
    var targets = ["categories", "games", "system"];
    var splitters = [];
    targets.forEach(function (id) {
      if (!screens[id]) return;
      var s = document.createElement("div");
      s.className = "bbw-list-splitter";
      s.setAttribute("aria-hidden", "true");
      s.title = "Drag to resize wheel";
      screens[id].appendChild(s);
      splitters.push(s);
      s.addEventListener("mousedown", onDown);
    });
    if (!splitters.length) return;

    var dragging = false, startX = 0, startW = 250, minW = 180, maxW = 480;

    function onDown(e) {
      if (e.button !== 0) return;
      // Lit les bornes courantes au cas où le user a modifié min/max.
      var L = (G && G.lists) || {};
      minW = L.minWidthPx != null ? L.minWidthPx : 180;
      maxW = L.maxWidthPx != null ? L.maxWidthPx : 480;
      startX = e.clientX;
      // Mesure la largeur courante depuis la variable CSS — évite la dérive
      // si l'utilisateur a changé widthPx via la modale pendant la session.
      var cssW = parseFloat(
        getComputedStyle(document.documentElement).getPropertyValue("--bbw-list-w")
      );
      startW = isFinite(cssW) ? cssW : (L.widthPx != null ? L.widthPx : 250);
      dragging = true;
      document.body.classList.add("bbw-list-dragging");
      document.addEventListener("mousemove", onMove);
      document.addEventListener("mouseup", onUp);
      e.preventDefault();
    }
    function onMove(e) {
      if (!dragging) return;
      var s = (window.BBW && window.BBW.scale) ? window.BBW.scale() : 1;
      if (s <= 0) s = 1;
      var w = startW + (e.clientX - startX) / s;
      if (w < minW) w = minW; else if (w > maxW) w = maxW;
      document.documentElement.style.setProperty("--bbw-list-w", w + "px");
    }
    function onUp() {
      if (!dragging) return;
      dragging = false;
      document.body.classList.remove("bbw-list-dragging");
      document.removeEventListener("mousemove", onMove);
      document.removeEventListener("mouseup", onUp);
      // Persiste la largeur finale (cookie bbw_cfg). Re-passe par
      // applyConfigCss côté config-save donne la même valeur clampée.
      try {
        var cur = parseFloat(
          getComputedStyle(document.documentElement).getPropertyValue("--bbw-list-w")
        );
        if (isFinite(cur) && window.BBW && window.BBW.cfg) {
          window.BBW.cfg.set("lists.widthPx", Math.round(cur));
        }
      } catch (_) {}
    }
  }

  // ── Scrollbar verticale draggable de la roue (desktop, opt-in) ──────────
  // Active si lists.scrollbarEnabled=true ET BBW.mode()==="desktop". Pose une
  // instance .bbw-list-sb { .bbw-list-sb-thumb } dans chaque écran à roue.
  // Le thumb est repositionné depuis setSelected() via window.BBW.updateListScrollbar
  // (à chaque changement de sélection — clavier, manette OU drag). Drag du thumb =
  // setSelected(target, {instant:true}) pour éviter la transition CSS du highlight
  // pendant le défilement rapide.
  var _bbwBars = {};   // {screenId: {sb, thumb}}
  function startListScrollbar() {
    if (!G || !G.lists || !G.lists.scrollbarEnabled) return;
    if (!window.BBW || !window.BBW.mode || window.BBW.mode() !== "desktop") return;

    document.body.classList.add("bbw-list-sb-on");

    var ids = ["categories", "games", "details", "system"];
    ids.forEach(function (id) {
      if (!screens[id]) return;
      var sb = document.createElement("div");
      sb.className = "bbw-list-sb";
      var thumb = document.createElement("div");
      thumb.className = "bbw-list-sb-thumb";
      sb.appendChild(thumb);
      // Append au .screen et non au .list : paintList() fait
      // list.innerHTML="" à chaque repaint et effacerait notre sb.
      // La position est rejouée en CSS pour suivre .list (cf. styles.css).
      screens[id].appendChild(sb);
      _bbwBars[id] = { sb: sb, thumb: thumb };
      bindThumbDrag(id, thumb);
    });

    // Expose un updater appelé par setSelected pour rafraîchir la position
    // du thumb à chaque changement (clavier, manette, drag thumb lui-même).
    window.BBW.updateListScrollbar = function (id) {
      var bar = _bbwBars[id]; if (!bar) return;
      var n = listLength(id);
      // Hauteur réelle du track : 600 px par défaut MAIS 520 px quand
      // .screen-games.has-gamelogo est actif (cf. styles.css). Lire le DOM
      // évite de redupliquer la logique CSS et le thumb ne déborde plus.
      var listH = bar.sb.clientHeight || 600;
      // Seuil de visibilité : on affiche le thumb dès que le contenu déborde
      // la fenêtre visible (overflow naturel). "1 page" = nb d'items qui
      // tiennent dans listH d'après la config courante (itemHeightPx + gapPx,
      // valeurs non-compact — évite de recalculer le seuil quand le mode
      // compact s'active automatiquement au-delà de compactThresholdN).
      var L = (G && G.lists) || {};
      var perPage = Math.floor(listH / ((L.itemHeightPx != null ? L.itemHeightPx : 44) + (L.gapPx != null ? L.gapPx : 11)));
      if (perPage < 1) perPage = 1;
      if (n <= perPage) { bar.thumb.style.display = "none"; return; }
      bar.thumb.style.display = "block";
      // Hauteur du thumb : proportionnelle (perPage items visibles), bornée
      // [20, listH * maxPct]. Cap config-driven (lists.scrollbarMaxThumbPct,
      // défaut 8 %) pour rester graspable et lisible comme indicateur de
      // position MÊME quand on a juste 1.5-2 pages de contenu (sans cap, à
      // n=2*perPage le thumb fait 50% du track et la notion de "j'en suis
      // à 20%" devient floue — cf. LaunchBox desktop qui plafonne autour de 30%).
      var maxPct = (G && G.lists && G.lists.scrollbarMaxThumbPct != null) ? G.lists.scrollbarMaxThumbPct : 0.08;
      var thumbH = Math.max(20, Math.min(listH * maxPct, listH * perPage / n));
      var maxTop = listH - thumbH;
      var sel = currentSel(id);
      // Snap to edge: clamp explicitly so floating-point drift or a future
      // currentSel returning e.g. 0.999 instead of exactly 1.0 never leaves
      // the thumb a pixel short of the track ends.
      var top;
      if (sel <= 0)          top = 0;
      else if (sel >= n - 1) top = maxTop;
      else                   top = maxTop * (sel / (n - 1));
      bar.thumb.style.height = thumbH + "px";
      bar.thumb.style.top    = top    + "px";
    };

    // Première peinture (les listes peuvent encore être vides au boot — la
    // mise à jour côté setSelected prendra le relais sitôt qu'elles le sont).
    ids.forEach(function (id) {
      if (_bbwBars[id]) try { window.BBW.updateListScrollbar(id); } catch (_) {}
    });
  }

  // ── Variante poster mode : scrollbar attachée à .poster-grid (scroll natif) ─
  // La grille scrolle nativement (overflow-y:auto) MAIS la bar native est masquée
  // (cf. styles.css .poster-grid { scrollbar-width: none }) — on monte cette
  // bar custom qui plafonne la hauteur du thumb à lists.scrollbarMaxThumbPct
  // du track (la native ne supporte pas de max-height sur le thumb → ~90% sur 1.5 page).
  function startPosterScrollbar() {
    if (!G || !G.lists || !G.lists.scrollbarEnabled) return;
    if (!window.BBW || !window.BBW.mode || window.BBW.mode() !== "desktop") return;
    var games = screens.games; if (!games) return;
    var grid = $(".poster-grid", games); if (!grid) return;

    var sb = document.createElement("div");
    sb.className = "bbw-poster-sb";
    var thumb = document.createElement("div");
    thumb.className = "bbw-list-sb-thumb";   // réutilise le style de la wheel scrollbar
    sb.appendChild(thumb);
    games.appendChild(sb);

    function update() {
      // Masquée hors mode poster (CSS gate aussi par .screen-games.poster).
      if (!posterMode) { thumb.style.display = "none"; return; }
      var ch = grid.clientHeight, sh = grid.scrollHeight;
      // Seuil : on n'affiche que sur overflow réel (= contenu > viewport).
      if (ch <= 0 || sh <= ch) { thumb.style.display = "none"; return; }
      thumb.style.display = "block";
      var trackH = sb.clientHeight; if (trackH <= 0) trackH = ch;
      // Cap config-driven (lists.scrollbarMaxThumbPct, cf. updateListScrollbar pour le rationnel).
      var maxPct = (G && G.lists && G.lists.scrollbarMaxThumbPct != null) ? G.lists.scrollbarMaxThumbPct : 0.08;
      var thumbH = Math.max(20, Math.min(trackH * maxPct, trackH * ch / sh));
      var maxTop = trackH - thumbH;
      var maxScroll = sh - ch;
      // Snap to edge: .poster-scroll has padding-top/bottom of 16 px, so the
      // first/last cell's offsetTop is never exactly 0 or maxScroll.  A 20 px
      // tolerance absorbs that padding AND any sub-pixel float drift from
      // native scrolling, keeping the thumb flush with the track ends when the
      // user is visually at the top or bottom.
      var top;
      if (maxScroll <= 0)                          top = 0;
      else if (grid.scrollTop <= 20)               top = 0;
      else if (grid.scrollTop >= maxScroll - 20)   top = maxTop;
      else top = (grid.scrollTop / maxScroll) * maxTop;
      thumb.style.height = thumbH + "px";
      thumb.style.top    = top    + "px";
    }
    window.BBW.updatePosterScrollbar = update;

    grid.addEventListener("scroll", update);
    window.addEventListener("resize", update);

    // Drag : map deltaY → deltaScrollTop, en tenant compte du scale stage.
    var dragging = false, startY = 0, startScroll = 0, sh = 0, ch = 0, trackH = 0, thumbH = 0;
    thumb.addEventListener("mousedown", function (e) {
      if (e.button !== 0) return;
      sh = grid.scrollHeight; ch = grid.clientHeight;
      if (ch <= 0 || sh <= ch) return;
      dragging    = true;
      startY      = e.clientY;
      startScroll = grid.scrollTop;
      trackH      = sb.clientHeight; if (trackH <= 0) trackH = ch;
      var maxPct = (G && G.lists && G.lists.scrollbarMaxThumbPct != null) ? G.lists.scrollbarMaxThumbPct : 0.08;
      thumbH      = Math.max(20, Math.min(trackH * maxPct, trackH * ch / sh));
      document.body.classList.add("bbw-list-sb-dragging");
      document.addEventListener("mousemove", onMove);
      document.addEventListener("mouseup", onUp);
      e.preventDefault();
      e.stopPropagation();
    });
    function onMove(e) {
      if (!dragging) return;
      var s = (window.BBW && window.BBW.scale) ? window.BBW.scale() : 1;
      if (s <= 0) s = 1;
      var maxTop = trackH - thumbH; if (maxTop <= 0) return;
      var maxScroll = sh - ch;      if (maxScroll <= 0) return;
      var deltaY      = (e.clientY - startY) / s;
      var scrollDelta = (deltaY / maxTop) * maxScroll;
      var target = startScroll + scrollDelta;
      if (target < 0) target = 0; else if (target > maxScroll) target = maxScroll;
      var prev = grid.style.scrollBehavior;
      grid.style.scrollBehavior = "auto";
      grid.scrollTop = target;
      grid.style.scrollBehavior = prev;
    }
    function onUp() {
      if (!dragging) return;
      dragging = false;
      document.body.classList.remove("bbw-list-sb-dragging");
      document.removeEventListener("mousemove", onMove);
      document.removeEventListener("mouseup", onUp);
    }
  }

  function bindThumbDrag(id, thumb) {
    var dragging = false, startY = 0, startSel = 0, total = 0, thumbH = 0;
    var listH = 600;
    thumb.addEventListener("mousedown", function (e) {
      if (e.button !== 0) return;
      total = listLength(id);
      // Le thumb est masqué (display:none) sous le seuil de 5 pages, donc
      // mousedown ne s'y déclenche pas — gard ceinture-bretelles au cas où.
      if (total <= 1) return;
      dragging = true;
      startY   = e.clientY;
      startSel = currentSel(id);
      thumbH   = Math.max(20, Math.min(listH, listH * 10 / total));
      document.body.classList.add("bbw-list-sb-dragging");
      document.addEventListener("mousemove", onMove);
      document.addEventListener("mouseup", onUp);
      e.preventDefault();
      e.stopPropagation();
    });
    function onMove(e) {
      if (!dragging) return;
      var s = (window.BBW && window.BBW.scale) ? window.BBW.scale() : 1;
      if (s <= 0) s = 1;
      var trackUsable = listH - thumbH;
      if (trackUsable <= 0) return;
      var deltaY     = (e.clientY - startY) / s;
      var itemsDelta = (deltaY / trackUsable) * (total - 1);
      var target = Math.round(startSel + itemsDelta);
      if (target < 0) target = 0; else if (target > total - 1) target = total - 1;
      if (target !== currentSel(id)) {
        // instant:true → highlight saute sans la transition CSS (fluide pour un drag rapide).
        setSelected(id, target, { instant: true });
      }
    }
    function onUp() {
      if (!dragging) return;
      dragging = false;
      document.body.classList.remove("bbw-list-sb-dragging");
      document.removeEventListener("mousemove", onMove);
      document.removeEventListener("mouseup", onUp);
    }
  }

  // i18n : traduit les éléments statiques [data-i18n] et pose le placeholder de recherche
  // (variable CSS). Appelée au boot une fois window.BBW.lang connue (anglais par défaut).
  function localize() {
    document.querySelectorAll("[data-i18n]").forEach(function (el) {
      el.textContent = window.BBW.t(el.getAttribute("data-i18n"));
    });
    document.documentElement.style.setProperty("--bbw-search-ph", JSON.stringify(window.BBW.t("search.placeholder")));
  }

  function wireBottomBars() {
    Object.keys(screens).forEach(function (id) {
      var bar = $(".bottombar", screens[id]); if (!bar) return;
      var left = $(".left", bar);
      if (left) left.addEventListener("click", function () {
        if (id !== current) return;
        if (id === "categories") navTo("system", false); else goBack();
      });
      bar.querySelectorAll(".right .hint").forEach(function (h) {
        var txt = h.textContent.toUpperCase(); h.style.cursor = "pointer";
        h.addEventListener("click", function () {
          if (id !== current) return;
          if (txt.indexOf("BACK") >= 0) goBack(); else if (txt.indexOf("SELECT") >= 0) descend();
        });
      });
    });
  }

  // ── Démarrage ───────────────────────────────────────────────────────────
  // Lit un param depuis la query string OU le hash. Hash gagne s'il est présent
  // (convention plus SPA-idiomatique pour le mode kiosque embarqué : #embedded=1&gameId=...).
  function param(k) {
    var rxQ = new RegExp("[?&]" + k + "=([^&]+)");
    var rxH = new RegExp("[#&]" + k + "=([^&]+)");
    var mH = rxH.exec(location.hash || "");
    if (mH) return decodeURIComponent(mH[1]);
    var mQ = rxQ.exec(location.search || "");
    return mQ ? decodeURIComponent(mQ[1]) : null;
  }

  window.BBW = window.BBW || {};
  // window.BBW.config est défini par engine/config.js (chargé avant ce script).
  window.BBW.nav = handleNav;   // navigation pilotable par la manette (engine/gamepad.js)
  window.BBW.onResize = function () { Object.keys(screens).forEach(function (id) { if (screens[id]) { positionHighlight(id, true); scrollListIntoView(id, true); } }); if (posterMode) { computePosterCols(); paintPoster(true); } };

  document.addEventListener("DOMContentLoaded", function () {
    ["categories", "games", "details", "system"].forEach(function (id) {
      screens[id] = document.querySelector('.screen[data-screen="' + id + '"]');
    });

    window.BBW.applyConfigCss();   // recopie les valeurs config → variables CSS (:root)
    // Zone clear-logo du jeu (haut de la roue) : on arme la classe qui RÉSERVE l'espace + affiche
    // la zone, plus les options visuelles (ombre portée / barre d'accent sous le logo). CSS coupe
    // en phone (body.mobile) ; sans clear logo, la zone montre le titre.
    if (screens.games && G.gameLogo && G.gameLogo.enabled !== false) {
      var glc = screens.games.classList;
      glc.add("has-gamelogo");
      glc.toggle("gl-glow", G.gameLogo.glow !== false);
      glc.toggle("gl-accent", G.gameLogo.accent !== false);
    }
    startClock();                  // horloge live tout de suite (indépendante des données)
    startKioskIndicators();        // debug (opt-in) : pills Focus + Son avant l'heure
    startListResizer();            // opt-in : splitter draggable de la roue (desktop)
    startListScrollbar();          // scrollbar verticale draggable de la roue (desktop) — affichée dès overflow
    startPosterScrollbar();        // scrollbar custom pour la grille poster (desktop) — thumb capé par lists.scrollbarMaxThumbPct

    // surcharges de config via URL (tests) : ?dur=280&dwell=160&anim=0 (transition de contenu)
    if (param("dur") !== null) G.contentTransition.durationMs = parseInt(param("dur"), 10);
    if (param("dwell") !== null) G.contentTransition.dwellMs = parseInt(param("dwell"), 10);
    if (param("anim") === "0") G.contentTransition.enabled = false;

    // Plateforme de démarrage : par défaut "windows" (compat historique
    // standalone), mais si un param "platform" est passé (mode kiosque
    // embarqué : GameLaunchHook ajoute le slug dans l'URL pour que le
    // theme atterrisse direct sur la bonne plateforme — sinon il
    // chercherait le gameId dans la mauvaise games.json et retomberait
    // sur l'écran catégories). slugify identique à celui du theme :
    // lowercase, non-alnum → "-", trim leading/trailing "-".
    var bootPlatformSlug = param("platform") || "windows";
    var bootPlatformPath = "data/platforms/" + bootPlatformSlug + "/games.json";

    // Charge les données du démarrage (dummy en standalone / JSON du plugin en
    // intégré) via la couche BBW.get, PUIS initialise l'UI.
    Promise.all([
      window.BBW.get("data/cattree.json"),
      window.BBW.get("data/detailmenu.json"),
      window.BBW.get(bootPlatformPath),
      fetchParental()
    ]).then(function (res) {
    DATA.catTree = res[0] || [];
    DATA.detailMenu = res[1] || [];
    var pl = res[2] || {};
    DATA.platform = pl.platform || ""; DATA.platformLogo = pl.platformLogo || "";
    DATA.platformLogoImg = pl.platformLogoImg || "";
    DATA.games = sortGamesAlpha(applyLibraryFilter(pl.games || []));   // filtre biblio + tri alphabétique
    DATA.gamesAll = DATA.games; DATA.platformTotal = DATA.games.length; DATA.platformTotalAll = DATA.platformTotal; resetSearch();
    // Contrôle parental : applique l'état AVANT setupList("system") (l'item Verrou
    // est retiré du DOM si inactif → la navigation ne tombe pas sur un item caché).
    var ps = res[3];
    if (ps) {
      parental.active = !!ps.active; parental.locked = !!ps.locked;
      parental.canUnlock = !!ps.canUnlock; parental.bigBox = !!ps.bigBox;
      parental.lockedOut = !!ps.lockedOut; parental.maxAttempts = ps.maxAttempts || 3;
      // canRate/canFav : si le backend ne les renvoie pas (ancienne version),
      // on retombe sur "autorisé" (true) — pas de régression du comportement.
      parental.canRate = (ps.canRate === undefined) ? true : !!ps.canRate;
      parental.canFav  = (ps.canFav  === undefined) ? true : !!ps.canFav;
      parental.installNeedsUnlock = !!ps.installNeedsUnlock;   // store install gated behind the PIN while locked
    }
    window.BBW.lang = (ps && ps.lang) || "";   // langue LB (Settings.xml) → i18n + choix du clavier (AZERTY si fr)
    localize();   // applique les traductions (anglais par défaut) maintenant que la langue est connue

    // ── Restauration de la vue poster depuis le cookie bbw_cfg ──────────
    // posterView.startInPosterMode est écrit par togglePoster() à chaque
    // bascule. On l'applique ICI, avant setupList("games"), parce que
    // setupList appelle buildPosterGrid() quand posterMode === true
    // (ligne 271). La classe CSS .poster doit aussi être posée avant
    // que la grille soit mesurée (computePosterCols lit clientWidth).
    if (posterEnabled() && window.BBW && window.BBW.cfg &&
        window.BBW.cfg.get("posterView.startInPosterMode")) {
      posterMode = true;
      if (screens.games) screens.games.classList.add("poster");
    }

    catStack = [{ nodes: DATA.catTree, sel: 0, enterDir: 1 }];
    setupCatList(); renderCatList();
    setupList("games", DATA.games.map(function (g) { return g.t; })); markGameStars("games");
    loadPlatformStars("platforms/windows");   // paliers qualité de la plateforme de démarrage
    setupDetailList(); resetDetailMenu();   // menu d'actions récursif (page jeu)
    applyParentalDom();   // ajuste l'item Verrou/Déverrou du System Menu (avant setupList)
    startParentalWatch(ps);   // re-vérifie périodiquement le verrou → reload si l'état change
    refreshRecentEpoch();     // epoch initial du "recent" (cache-buster post-lancement de jeu)
    startEpochPolling();      // heartbeat 2 s pour le running-state (anti-double-launch)
    // Au retour sur l'appli (après avoir joué), re-vérifie l'epoch → rafraîchit le recent.
    document.addEventListener("visibilitychange", function () { if (!document.hidden) refreshRecentEpoch(); });
    window.addEventListener("focus", refreshRecentEpoch);
    setupList("system");
    setupRail(); setupRomRail(); setupRecent(); wireBottomBars(); setupRatingModal(); setupPinPad(); setupRelatedModal(); setupVndbModal(); setupConfig(); setupInfoModal(); setupLaunchErrorModal(); setupSearch(); setupAdvanced(); setupRomAdv();
    setupWheel("categories"); setupWheel("games");   // roue tactile mobile
    setupSwipeNav("details"); setupSwipeNav("system");   // swipe horizontal = retour/select
    setupPosterGesture();   // tablette : swipe bas depuis la title bar (centre) = bascule de vue

    // ── Mode embarqué (kiosque WebView2 hébergé par ExtendDB) ─────────
    // Détecté via le hash de l'URL : …/bigbox#embedded=1 (éventuellement
    // suivi de &gameId=<guid> pour deep-linker vers une fiche de jeu
    // précise — utilisé par GameLaunchHook pour relancer la kiosque sur
    // le bon jeu après une partie). Le mode embarqué expose un bouton
    // exit (en haut à droite) qui post un WebMessage 'kiosk:exit' au host
    // WebView2, lequel ferme la fenêtre proprement.
    var isEmbedded = param("embedded") === "1";
    if (isEmbedded) {
      document.body.classList.add("bbw-embedded");
      var exitBtn = document.getElementById("bbw-exit-btn");
      if (exitBtn) {
        exitBtn.addEventListener("click", function () {
          try {
            if (window.chrome && window.chrome.webview && window.chrome.webview.postMessage) {
              window.chrome.webview.postMessage("kiosk:exit");
            }
          } catch (e) { /* WebMessage indispo (standalone) — pas critique */ }
        });
      }
    } else {
      // Standalone : aucun host WebView2 à fermer → retire l'entrée "Exit"
      // du System Menu pour éviter qu'elle apparaisse comme un no-op (le
      // wrap modulo de la sélection naviguerait dessus autrement).
      var exitItem = document.querySelector('.screen-system .list-item[data-sys="exit"]');
      if (exitItem && exitItem.parentNode) exitItem.parentNode.removeChild(exitItem);
    }

    // Deep-link vers une fiche jeu par GUID (game.Id). Cherche dans
    // DATA.games chargé juste au-dessus ; si trouvé, force screen=details
    // sur l'index correspondant. Ignoré si gameId absent ou jeu introuvable
    // dans la plateforme courante.
    var deepGameId = param("gameId");
    if (deepGameId) {
      var foundIdx = -1;
      for (var di = 0; di < DATA.games.length; di++) {
        if (DATA.games[di] && DATA.games[di].id === deepGameId) { foundIdx = di; break; }
      }
      if (foundIdx >= 0) {
        var __screenOverride = "details";
        var __indexOverride = foundIdx;
      }
    }

    var startScreen = (typeof __screenOverride !== "undefined") ? __screenOverride : (param("screen") || "categories");
    var startIndex = (typeof __indexOverride !== "undefined") ? __indexOverride : parseInt(param("i"), 10);
    if (isNaN(startIndex)) startIndex = (startScreen === "details") ? 1 : 0;
    if (startScreen === "details") currentGame = startIndex;

    fillGamePanel(screens.details, currentGame);
    setSelected("categories", startScreen === "categories" ? startIndex : 0, { instant: true });
    setSelected("games", startScreen === "games" ? startIndex : 0, { instant: true });
    // Si on a restauré la vue poster depuis le cookie, initialise le panneau
    // latéral pour le jeu sélectionné (posterSelect synchro posterSel + sel.games,
    // remplit fillPosterSide / fillPosterMedia et déclenche scheduleHeavy).
    // L'appel vient APRÈS setSelected("games") pour que cancelGameContentLoads()
    // ait déjà bumpé le mediaToken — sinon posterSelect reprogrammerait le heavy
    // load trop tôt et scheduleGameContent l'invaliderait juste derrière.
    if (posterMode) {
      // BUG #2 FIX: When relaunching via a gameId deep-link, startScreen is "details" and
      // currentGame was already set to foundIdx above. The old code used 0 as the fallback
      // for non-"games" startScreens, so posterSelect(0) would overwrite currentGame=foundIdx
      // with 0, showing the wrong game in the poster side panel. Use currentGame here so that
      // poster mode always lands on the same game as wheel mode / details mode.
      var _bootPosterIdx = startScreen === "games" ? startIndex : currentGame;
      posterSel = _bootPosterIdx; try { posterSelect(_bootPosterIdx); } catch (_) {}
    }
    setSelected("details", startScreen === "details" ? startIndex : 0, { instant: true });
    setSelected("system", startScreen === "system" ? startIndex : 0, { instant: true });

    // ── Bootstrap des données lazy pour l'écran de démarrage ──
    // IMPORTANT — ces appels viennent APRÈS tous les setSelected :
    // setSelected("games") au boot appelle scheduleGameContent() qui
    // appelle cancelGameContentLoads() qui bumpe mediaToken. Si on
    // lance requestGameDetail AVANT, son callback de réponse est
    // invalidé par le token check (myToken !== mediaToken) et le
    // repaint final ne tire pas → box / shots / video restent vides.
    if (startScreen === "details") {
      // Le flow normal descend() games → details (ligne ~3971) fait
      // requestGameDetail() pour tirer le fetch de détail lourd
      // (boxImg, shots, video, fanart, vndb, lastLaunch, …) ; le boot
      // sans ce call laissait la fiche avec juste le texte de
      // games.json et un panneau média vide.
      try { requestGameDetail(currentGame); } catch (e) { /* abort safe */ }
    }
    if (startScreen === "categories") {
      // Le flow normal appelle loadCatLazy(toIdx) à chaque change de
      // sélection (cf. lignes ~2055/2061/2114), mais au boot la
      // sélection est posée sans event de change → recent.json et le
      // média de fond restent vides la première fois.
      try { loadCatLazy(startIndex); } catch (e) { /* safe */ }
    }

    // zone initiale (pour vérif headless : &zone=rail / &zone=recent)
    var z = param("zone");
    if (z === "rail" && startScreen === "games") { railSel = 4; openRail(); }
    else if (z === "recent" && startScreen === "categories") { zone = "recent"; recentSel = 0; paintRecent(); }

    // vérif headless : ?enter=N descend instantanément dans le sous-niveau du nœud racine N
    var en = param("enter");
    if (en !== null && startScreen === "categories") {
      var node = DATA.catTree[parseInt(en, 10) || 0];
      if (node && node.children) { catStack.push({ nodes: node.children, sel: 0, enterDir: 1 }); renderCatList(); setSelected("categories", 0, { instant: true }); }
    }

    var stage = document.getElementById("stage");
    stage.classList.add("no-anim");
    screens[startScreen].classList.add("active"); screens[startScreen].style.opacity = 1;
    current = startScreen; void stage.offsetWidth;
    positionHighlight(startScreen, true);
    scrollListIntoView(startScreen, true);   // recentre la roue une fois l'écran visible (hauteur valide)
    stage.classList.remove("no-anim");
    descPlay();   // démarre le défilement de description de l'écran initial (si nécessaire)

    document.addEventListener("keydown", onKey);
    hideCursor();   // curseur masqué au chargement (jusqu'au premier mouvement)

    // vérif headless : fige une transition catégorie à mi-course
    // ?catmid=down (Consoles->Handhelds) ou ?catmid=up (Handhelds->Consoles)
    var cm = param("catmid");
    if (cm && startScreen === "categories") {
      var dir = (cm === "up") ? -1 : 1;
      var from = (dir > 0) ? 2 : 3, to = (dir > 0) ? 3 : 2;  // Consoles(2) <-> Handhelds(3)
      fillCatAll(from); shownCat = from;
      curFrame().sel = to;
      setCatHighlight(to, true);
      doCatTransition(to, dir, true /* freezeMid */);
    }
    // vérif headless : fige une transition de la LISTE DE JEUX à mi-course
    // ?gamemid=down (jeu 1->2) ou ?gamemid=up (jeu 2->1)
    var gmid = param("gamemid");
    if (gmid && startScreen === "games") {
      var gdir = (gmid === "up") ? -1 : 1;
      var gfrom = (gdir > 0) ? 1 : 2, gto = (gdir > 0) ? 2 : 1;
      fillGamePanel(screens.games, gfrom); shownGame = gfrom;
      sel.games = gto;
      screens.games.querySelectorAll(".list .list-item").forEach(function (it, k) { it.classList.toggle("selected", k === gto); });
      positionHighlight("games", true); scrollListIntoView("games", true);
      doGameTransition(gto, gdir, true /* freezeMid */);
    }
    });   // fin du .then (données chargées)
  });
})();
