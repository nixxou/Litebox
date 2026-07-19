/* ============================================================================
   BigBoxWeb — manette (Gamepad API / XInput)

   Pilote la MÊME navigation que le clavier via window.BBW.nav(cmd, repeat).
   L'API Gamepad n'émet pas d'événement par bouton → on interroge l'état à chaque
   frame (requestAnimationFrame). Le polling démarre quand une manette se connecte
   (ou si une est déjà présente) et tourne tant qu'au moins une est branchée.

   Mapping "standard" (manette Xbox / XInput sous Edge/Chrome Windows) :
     A(0)=select · B(1)=retour · Start(9)=menu système ·
     croix directionnelle (12-15) + stick gauche (axes 0/1) = directions.

   Réglages : config.global.gamepad {enabled, deadzone, repeatDelayMs, repeatRateMs}.
   Chargé APRÈS engine/app.js (a besoin de window.BBW.nav). Script classique (file://-safe).
   ========================================================================== */
(function () {
  "use strict";
  function cfg() { return (window.BBW && window.BBW.config && window.BBW.config.global.gamepad) || {}; }
  function nav(cmd, repeat) { if (window.BBW && window.BBW.nav) window.BBW.nav(cmd, repeat); }

  var prevBtn = {};                  // état précédent des boutons (détection de front montant)
  var dirHeld = null, dirNext = 0;   // direction maintenue + horodatage de la prochaine répétition
  var dirRepeatStart = 0;            // début de la phase de répétition (base de l'accélération)
  // Gâchettes L2 (button 6) / R2 (button 7) → pgup / pgdn.
  // Chaque gâchette a son propre compteur de répétition, indépendant du stick.
  var trgHeld  = { pgup: false, pgdn: false }; // gâchette actuellement enfoncée ?
  var trgNext  = { pgup: 0,     pgdn: 0     }; // horodatage de la prochaine émission
  var trgRepeatStart = { pgup: 0, pgdn: 0 };   // début du maintien (base de l'accélération)
  var running = false;

  function activePad() {
    var gps = navigator.getGamepads ? navigator.getGamepads() : [];
    for (var i = 0; i < gps.length; i++) if (gps[i] && gps[i].connected) return gps[i];
    return null;
  }
  function down(gp, i) { return !!(gp.buttons[i] && gp.buttons[i].pressed); }

  // Direction unique (priorité vertical puis horizontal) depuis croix + stick gauche.
  function direction(gp, dead) {
    var x = gp.axes[0] || 0, y = gp.axes[1] || 0;
    if (down(gp, 12) || y < -dead) return "up";
    if (down(gp, 13) || y > dead) return "down";
    if (down(gp, 14) || x < -dead) return "left";
    if (down(gp, 15) || x > dead) return "right";
    return null;
  }
  // Bouton d'action : déclenche au front montant uniquement (pas de répétition).
  function edge(gp, i, cmd) {
    var p = down(gp, i);
    if (p && !prevBtn[i]) nav(cmd, false);
    prevBtn[i] = p;
  }

  function loop() {
    var c = cfg();
    if (c.enabled === false) { running = false; return; }   // désactivée → on arrête le polling
    var gp = activePad();
    if (gp) {
      var now = performance.now();
      // Directions : 1er déclenchement immédiat, puis auto-répétition ACCÉLÉRÉE.
      var dir = direction(gp, c.deadzone != null ? c.deadzone : 0.5);
      if (dir) {
        if (dir !== dirHeld) {
          // Nouvelle direction : action immédiate, puis délai avant la répétition.
          dirHeld = dir;
          dirRepeatStart = now + (c.repeatDelayMs || 400);
          dirNext = dirRepeatStart;
          nav(dir, false);
        } else if (now >= dirNext) {
          // Intervalle dégressif repeatRateMs → repeatRateMinMs sur accelMs : plus on
          // maintient, plus ça défile vite (jusqu'à ≈ une touche clavier enfoncée).
          var slow = c.repeatRateMs || 120;
          var fast = c.repeatRateMinMs != null ? c.repeatRateMinMs : 30;
          var ramp = c.accelMs != null ? c.accelMs : 1000;
          var t = ramp > 0 ? Math.min(1, Math.max(0, (now - dirRepeatStart) / ramp)) : 1;
          dirNext = now + (slow + (fast - slow) * t);
          nav(dir, true);
        }
      } else { dirHeld = null; }
      // Boutons d'action.
      edge(gp, 0, "select");   // A
      edge(gp, 1, "back");     // B
      edge(gp, 9, "menu");     // Start
      edge(gp, 8, "poster");   // View / Select (petit bouton gauche) → bascule vue poster
      edge(gp, 4, "mediaPrev");// LB (épaule gauche) → média précédent
      edge(gp, 5, "mediaNext");// RB (épaule droite) → média suivant
      // Gâchettes analogiques L2 (button 6) → pgup · R2 (button 7) → pgdn.
      // Seuil configurable (triggerThreshold, défaut 0.4) pour éviter les faux
      // déclenchements au repos. Répétition accélérée indépendante du stick.
      var thr = c.triggerThreshold != null ? c.triggerThreshold : 0.4;
      var slow = c.repeatRateMs || 120;
      var fast = c.repeatRateMinMs != null ? c.repeatRateMinMs : 30;
      var ramp = c.accelMs != null ? c.accelMs : 1000;
      var trigMap = [{ btn: 6, cmd: "pgup" }, { btn: 7, cmd: "pgdn" }];
      for (var ti = 0; ti < trigMap.length; ti++) {
        var tm   = trigMap[ti];
        var tval = gp.buttons[tm.btn] ? gp.buttons[tm.btn].value : 0;
        var tpressed = tval > thr;
        if (tpressed) {
          if (!trgHeld[tm.cmd]) {
            // Front montant : premier appui immédiat, puis délai avant répétition.
            trgHeld[tm.cmd] = true;
            trgRepeatStart[tm.cmd] = now + (c.repeatDelayMs || 400);
            trgNext[tm.cmd]  = trgRepeatStart[tm.cmd];
            nav(tm.cmd, false);
          } else if (now >= trgNext[tm.cmd]) {
            // Répétition accélérée (même rampe que le stick).
            var t2 = ramp > 0 ? Math.min(1, Math.max(0, (now - trgRepeatStart[tm.cmd]) / ramp)) : 1;
            trgNext[tm.cmd] = now + (slow + (fast - slow) * t2);
            nav(tm.cmd, true);
          }
        } else {
          trgHeld[tm.cmd] = false;
        }
      }
    } else { dirHeld = null; trgHeld.pgup = false; trgHeld.pgdn = false; }
    requestAnimationFrame(loop);
  }
  function start() { if (running) return; running = true; requestAnimationFrame(loop); }

  // Chrome/Edge n'exposent la manette qu'après le 1er input → l'événement est la
  // voie principale ; on tente aussi au cas où une est déjà visible.
  window.addEventListener("gamepadconnected", start);
  if (navigator.getGamepads) {
    var g = navigator.getGamepads();
    for (var i = 0; i < g.length; i++) if (g[i]) { start(); break; }
  }
})();
