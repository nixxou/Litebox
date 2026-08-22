/* ============================================================================
   BigBoxWeb — couche d'accès aux données

   Le moteur ne connaît QUE des chemins logiques (data/cattree.json,
   data/platforms/<slug>/games.json, data/games/<id>.json,
   data/games/<id>/related.json, …). Deux origines possibles, transparentes
   pour le moteur :

     • Servi en HTTP (par le plugin)  → fetch(path)  : le serveur génère le JSON
       à la volée depuis GameCache / la DB, et sert les vrais médias.
     • Ouvert en file:// ou servi sans backend (dev) → repli sur des données
       « dummy » enregistrées par data/dummy.js (chargé en <script>, donc OK en
       file:// où fetch() est bloqué).

   API :
     window.BBW.get(path[, ttlMs]) -> Promise(objet)   (cache mémoire + repli dummy)
     window.BBW.dummy(path, obj)                        (appelé par data/dummy.js)

   Doit être chargé AVANT data/dummy.js et avant engine/app.js.
   ========================================================================== */
(function () {
  "use strict";
  window.BBW = window.BBW || {};
  window.BBW.DUMMY = window.BBW.DUMMY || {};
  window.BBW.dummy = function (path, obj) { window.BBW.DUMMY[path] = obj; };

  var cache = {};   // path -> { p: Promise, exp: 0|timestamp }   (exp = 0 → pas d'expiration)
  var http = (location.protocol === "http:" || location.protocol === "https:");

  /* ─── Signal de sélection (kiosque uniquement) ──────────────────────────────
     L'hôte déduit « quel jeu / quelle liste est sélectionné » des requêtes qu'il
     voit passer : pas d'endpoint à appeler, pas de contrat à tenir. Sauf qu'un
     cache-hit ne produit AUCUNE requête — revenir sur une plateforme déjà visitée
     devenait donc invisible, et l'écran secondaire restait sur l'affichage
     précédent.
     Alors on ne le prévient QUE dans ce cas : jamais en plus d'un fetch réel,
     donc aucun appel dupliqué. Et seulement depuis un kiosque, reconnu au
     marqueur d'agent utilisateur — un navigateur ordinaire n'émet rien du tout.
     Tir sans attente ni gestion d'erreur : ça ne doit jamais retarder un
     affichage, et un échec ne coûte qu'un rafraîchissement en retard. */
  var isKiosk = false;
  try { isKiosk = http && (navigator.userAgent || "").indexOf("LiteBoxKiosk") >= 0; } catch (e) {}
  var SEL_RX = /\/data\/(?:games\/[^/]+\/detail\.json|(?:platforms|playlists|categories)\/[^/]+\/games\.json)$/;

  function pingSelection(path) {
    if (!isKiosk) return;
    try {
      var abs = new URL(path, location.href).pathname;
      if (!SEL_RX.test(abs)) return;
      fetch("/api/kiosk/selection?p=" + encodeURIComponent(abs), { keepalive: true }).catch(function () {});
    } catch (e) {}
  }

  // Supprime les entrées expirées (purge opportuniste, évite l'accumulation).
  function sweep(now) {
    for (var k in cache) { var e = cache[k]; if (e && e.exp !== 0 && e.exp < now) delete cache[k]; }
  }

  // Retourne une Promise résolue avec l'objet à `path`. En HTTP on tente le serveur
  // (vrai JSON) et on retombe sur le dummy si absent ; en file:// on sert le dummy.
  // `ttlMs` (optionnel) : durée de vie de l'entrée — passé ce délai, un nouvel appel
  // refetch. Sans ttlMs → pas d'expiration (cache de session, comportement d'origine).
  window.BBW.get = function (path, ttlMs) {
    var now = Date.now();
    var e = cache[path];
    if (e && (e.exp === 0 || now < e.exp)) { pingSelection(path); return e.p; }   // entrée encore valide
    var p;
    if (http && typeof fetch === "function") {
      p = fetch(path)
        .then(function (r) { if (!r.ok) throw new Error("HTTP " + r.status); return r.json(); })
        .catch(function () { return window.BBW.DUMMY[path]; });
    } else {
      p = Promise.resolve(window.BBW.DUMMY[path]);
    }
    cache[path] = { p: p, exp: ttlMs ? (now + ttlMs) : 0 };
    if (ttlMs) sweep(now);   // ne balaie que quand une entrée à TTL est posée (cas du recent)
    return p;
  };
})();
