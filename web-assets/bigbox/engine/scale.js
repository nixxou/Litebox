// Met à l'échelle la scène 1280×720 pour remplir la fenêtre, et choisit le MODE
// d'affichage/d'entrée. Équivalent web du ScaleWithResolutionConverter.
// Script classique (file://-safe). Doit être chargé APRÈS engine/config.js.
//
// Trois modes (cf. config.global.layout) :
//   • "desktop" : agencement classique + souris (survol/clic). Scène mise à l'échelle.
//   • "tablet"  : agencement classique MAIS menu en ROUE tactile ; souris coupée.
//                 Le contenu (détail/aperçu/récents) reste affiché. Scène à l'échelle.
//   • "phone"   : agencement compact en COLONNE + roue tactile (le contenu se replie).
//                 Pas de mise à l'échelle (mise en page fluide).
//
// API exposée au moteur :
//   window.BBW.mode()     → "desktop" | "tablet" | "phone"
//   window.BBW.isMobile() → true en "phone"            (= agencement colonne compact)
//   window.BBW.isTablet() → true en "tablet"
//   window.BBW.isTouch()  → true en "phone" OU "tablet" (= entrée tactile / roue)
(function () {
  function cfg() {
    return (window.BBW && window.BBW.config && window.BBW.config.global.layout) || {};
  }
  // Appareil tactile = pointeur primaire grossier (doigt) ET aucun pointeur fin
  // (souris/trackpad) disponible. Le `&& !(any-pointer: fine)` est crucial pour les
  // PC à écran tactile utilisés à la souris : Chrome y répond (pointer:fine), MAIS
  // FIREFOX répond (pointer:coarse=true) même avec une souris → il basculait à tort
  // en mobile. Tant qu'une souris existe (any-pointer:fine), on reste en desktop.
  // (maxTouchPoints/ontouchstart abandonnés : trop larges. Headless : pas de coarse
  //  → false → desktop ; pour tester un vrai mode tactile : ?mode=.)
  function isTouchDevice() {
    if (!window.matchMedia) return navigator.maxTouchPoints > 0;   // repli très vieux navigateurs
    return window.matchMedia("(pointer: coarse)").matches &&
           !window.matchMedia("(any-pointer: fine)").matches;
  }
  function urlMode() {
    var m = /[?&]mode=(desktop|tablet|phone)\b/.exec(location.search);
    return m ? m[1] : null;
  }
  function resolveMode() {
    var c = cfg();
    var forced = urlMode() || (c.mode && c.mode !== "auto" ? c.mode : null);
    if (forced) return forced;
    // Souris / desktop : TOUJOURS "desktop" — une fenêtre étroite ne bascule plus en
    // colonne mobile (la scène se met simplement à l'échelle). Seul un appareil
    // TACTILE passe en phone/tablet.
    if (!isTouchDevice()) return "desktop";
    var phoneMax = c.phoneMaxWidthPx || 820;
    return window.innerWidth < phoneMax ? "phone" : "tablet";   // tactile : étroit → phone, sinon tablette
  }

  var MODE = "desktop";
  var SCALE = 1;
  function fit() {
    var stage = document.getElementById("stage"); if (!stage) return;
    MODE = resolveMode();
    document.body.classList.toggle("mobile", MODE === "phone");   // .mobile = agencement colonne (inchangé)
    document.body.classList.toggle("tablet", MODE === "tablet");
    if (MODE === "phone") {
      stage.style.transform = "none";
      stage.style.width = "";            // laisse le CSS mobile (width:100%)
      SCALE = 1;
    } else {
      var s = Math.min(window.innerWidth / 1280, window.innerHeight / 720);
      // La scène garde 720 de haut, mais s'ÉLARGIT (≥1280) pour remplir toute la
      // largeur de la fenêtre : le contenu flexible (bloc de description) absorbe la
      // largeur en plus → plein écran en paysage, sans bandes latérales noires.
      stage.style.width = (window.innerWidth / s) + "px";
      // translate(-50%,-50%) : centre la scène quelle que soit la taille.
      stage.style.transform = "translate(-50%, -50%) scale(" + s + ")";
      SCALE = s;
    }
    // laisse le moteur repositionner surbrillance / roue après un changement de mode
    if (window.BBW && window.BBW.onResize) window.BBW.onResize(MODE === "phone");
  }

  window.BBW = window.BBW || {};
  window.BBW.mode     = function () { return MODE; };
  // Facteur d'échelle courant du #stage (transform: scale(s)) — utilisé par
  // les handlers de drag pour convertir un delta clientX en px scène.
  window.BBW.scale    = function () { return SCALE; };
  window.BBW.isMobile = function () { return MODE === "phone"; };
  window.BBW.isTablet = function () { return MODE === "tablet"; };
  window.BBW.isTouch  = function () { return MODE === "phone" || MODE === "tablet"; };

  window.addEventListener("resize", fit);
  document.addEventListener("DOMContentLoaded", fit);
  fit();

  // Plein écran (cache la barre d'URL) au PREMIER geste tactile — l'API exige un
  // geste utilisateur. Android Chrome/Edge : OK. iOS iPhone : non supporté → passer
  // par « Sur l'écran d'accueil » (cf. manifest). Désactivable : layout.fullscreenOnTap=false.
  function tryFullscreen() {
    if (cfg().fullscreenOnTap === false || !window.BBW.isTouch()) return;
    if (document.fullscreenElement) return;
    var el = document.documentElement;
    var req = el.requestFullscreen || el.webkitRequestFullscreen;
    if (!req) return;
    try { var p = req.call(el); if (p && p.catch) p.catch(function () {}); } catch (e) {}
  }
  document.addEventListener("touchend", tryFullscreen, { once: true });
  document.addEventListener("click", tryFullscreen, { once: true });
})();
