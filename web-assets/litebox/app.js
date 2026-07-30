/* ============================================================================
   LaunchBox Web — app.js
   ----------------------------------------------------------------------------
   Responsibilities:
     a. Log boot confirmation with live config.
     b. Fetch /launchbox/data/cattree.json and render the left tree.
     c. Wire the Settings cog button and modal close actions.
     d. Initialise vanilla-LazyLoad scoped to the poster grid.
     e. (Grid section) Load platform games and build the poster-style grid,
        mirroring buildPosterGrid / paintPoster / posterSelect from BigBoxWeb.

   Tree rendering (workflow C):
     - Categories (path prefix "categories/") → collapsible group rows.
     - Platforms  (path prefix "platforms/")  → selectable leaf rows.
     - Playlists  (path prefix "playlists/")  → selectable leaf rows.
     - Icons loaded from /api/launchbox/icons/<name>.png; 404 hides img.
     - .lb-search-input filters visible nodes (case-insensitive substring).
     - First category auto-expanded on load.

   Style: plain ES5 (var, function declarations) consistent with the
   BigBoxWeb engine codebase.
   ============================================================================ */
(function () {
  "use strict";

  /* ══════════════════════════════════════════════════════════════════════════
     GRID SECTION — module-scope state
     Mirrors the poster-mode state vars in BigBoxWeb/web/engine/app.js.
     ══════════════════════════════════════════════════════════════════════════ */

  /* Current platform's games (populated on tree leaf click → loadPlatform).
     Shape mirrors BBW: array of { t, dev, thumb, ... }
     (copied from BigBoxWeb/web/engine/app.js :: DATA declaration ~line 22) */
  var DATA = { games: [] };

  /* Index of the currently selected cell in the grid; -1 = none.
     (copied from BigBoxWeb/web/engine/app.js :: posterSel ~line 54) */
  var posterSel = -1;

  /* Live store install-state re-check (store games only) — mirrors BBW
     installPollTimer/startInstallPoll. Polls installstate.json while a store
     game stays selected so an install/uninstall flips the badge + Play button. */
  var lbInstallPollTimer = null, lbInstallPollGi = -1;

  /* ── Raccourcis clavier configurables (serveur) ─────────────────────────────
     Carte commande → touches. Défauts identiques à l'ancien switch lbOnKey ;
     le serveur (/launchbox/api/keybinds, alimenté par la config du plugin) peut
     surcharger. lbOnKey consulte la table inverse LBKEYCMD (touche → commande).
     Espace est normalisé en "Spacebar" (e.key === " "). */
  var LBKEYCMD = (function (m) {
    var o = {}; for (var c in m) { var a = m[c] || []; for (var i = 0; i < a.length; i++) o[a[i]] = c; } return o;
  })({
    up: ["ArrowUp"], down: ["ArrowDown"], left: ["ArrowLeft"], right: ["ArrowRight"],
    pgup: ["PageUp"], pgdn: ["PageDown"], home: ["Home"], end: ["End"],
    select: ["Enter", "Spacebar"], zone: ["Tab"]
  });
  try {
    fetch("/launchbox/api/keybinds", { cache: "no-store" })
      .then(function (r) { return r.ok ? r.json() : null; })
      .then(function (j) {
        if (!j) return;
        var o = {};
        for (var c in j) { var a = j[c] || []; for (var i = 0; i < a.length; i++) if (a[i]) o[a[i]] = c; }
        if (Object.keys(o).length) LBKEYCMD = o;   // garde-fou : pas de carte vide
      })
      .catch(function () {});
  } catch (e) {}

  /* ── PART A state ──────────────────────────────────────────────────────────
     Mirrors BBW's mediaToken (requestGameDetail) and the detail cache pattern.
     (ref: BigBoxWeb/web/engine/app.js :: mediaToken + requestGameDetail ~line 4319) */

  /* Increment to cancel in-flight detail fetches when selection changes.
     On each new selectCell call, lbDetailToken is bumped and the current
     fetch checks myToken !== lbDetailToken to decide whether to discard its
     response — identical to BBW's mediaToken guard. */
  var lbDetailToken = 0;

  /* Session-scoped detail cache: gameId → merged detail JSON.
     Avoids a redundant network round-trip when the user navigates back to a
     game they already visited within the same page load.
     (mirrors BBW g._det boolean; here we keep the full cached response so
     we can re-merge without re-fetching) */
  var lbDetailCache = {};

  /* ── Games-list cache ─────────────────────────────────────────────────────
     path → { games: [...], asOf: counter }
     'path' is the loadPlatform argument (e.g. "platforms/snes").
     'asOf' records the lbCacheCounter value at store time — useful for
     debugging but not used for cache validity decisions.
     Primary validity signal: presence of the key in the object.
     The cache is wiped entirely on every parental-state flip
     (reloadLbAfterParental) so the server's ESRB filter is always reflected.
     (ref: req §1) */
  var lbGamesCache = {};

  /* Monotonic counter — incremented on each successful cache store.
     (ref: req §2 asOf field) */
  var lbCacheCounter = 0;

  /* Path of the platform whose games are currently rendered in DATA.games.
     Set at the top of loadPlatform() so toggleLbFavorite can find the
     matching cache entry without scanning all keys.
     (ref: req §2, §4) */
  var lbCurrentPlatformPath = "";

  /* Parental epoch — incremented in reloadLbAfterParental() so in-flight
     games.json fetches that complete AFTER a parental flip can detect
     staleness and discard their response instead of re-populating the cache.
     Mirrors the lbDetailToken pattern for detail.json fetches.
     (ref: req §6) */
  var lbParentalEpoch = 0;

  /* ── PART B state ──────────────────────────────────────────────────────────
     Mirrors BBW's posterCols variable.
     (ref: BigBoxWeb/web/engine/app.js :: posterCols ~line 686) */

  /* Column count for keyboard arrow navigation — recomputed on grid build
     and on window resize (debounced 200 ms).  Default 7 matches BBW default. */
  var lbPosterCols = 7;

  /* Display name of the currently loaded platform or playlist.
     Set by loadPlatform() when it receives the node name from selectLeaf.
     Used by fillLbPanel() to populate .lb-panel-plat.
     (mirrors BBW DATA.platform used in fillPosterSide ~line 982) */
  var currentPlatformName = "";

  /* Arrange By is deliberately page/session scoped. A playlist may impose an
     effective order, but temporary re-sorts inside it never overwrite this global state. */
  var lbGlobalSort = { key: "title", dir: "asc" };
  var lbActiveSort = { key: "title", dir: "asc", forced: false };
  var lbSortPayload = { nodeKind: "platform", sortBy: "Default", customSorts: [], manualAvailable: false };
  var lbRawGames = [];
  var lbKnownCustomSorts = [];

  /* Crossfade state for the hero fanart — mirrors BBW heroFanartActive.
     0 or 1: index of the .lb-panel-hero-bg-layer currently carrying .on.
     (ref: BigBoxWeb/web/engine/app.js :: heroFanartActive ~line 847) */
  var lbHeroFanartActive = 0;

  /* Debounce timer for hero fanart fade-in — mirrors BBW posterFanartTimer.
     (ref: BigBoxWeb/web/engine/app.js :: posterFanartTimer ~line 845) */
  var lbHeroFanartTimer = null;

  /* One-shot fade-out timer — mirrors BBW posterFanartOutTimer.
     Scheduled ONCE per deselection and NOT cancellable (intentionally asymmetric).
     null means no out-timer is pending.
     (ref: BigBoxWeb/web/engine/app.js :: posterFanartOutTimer ~line 846) */
  var lbFanartOutTimer = null;

  /* Debounce timer for the heavy detail fetch — mirrors BBW heavyTimer (app.js ~line 4189).
     Armed in selectCell, cleared by loadPlatform and reloadLbAfterParental.
     (ref: BigBoxWeb/web/engine/app.js :: heavyTimer + scheduleHeavy ~line 4429) */
  var lbHeavyTimer = null;

  /* ── Audio unlock state — mirrors BBW audioOn / unlockAudio (app.js:189-226) */

  /* True after the first user gesture (key/pointer/touch) that unblocked audio. */
  var lbAudioOn = false;

  /* ── User star ratings — local optimistic state ──────────────────────────────
     Maps gameId → user-chosen rating value (1-5).  Populated by commitLbRating.
     No server endpoint yet; kept module-scope so repaint can read it.
     (ref: BBW userRatings map ~line 69 context) */
  var userRatings = {};

  /* Anti-UA-border transparent 1×1 GIF placeholder.  Placed as src DEFAULT
     on every .rc-img so the <img> always has a real (transparent) content;
     avoids the UA-default border some Edge versions draw on <img> with no src.
     (copied from BigBoxWeb/web/engine/app.js :: TRANSPARENT_1PX ~line 707) */
  var TRANSPARENT_1PX = "data:image/gif;base64,R0lGODlhAQABAAAAACH5BAEKAAEALAAAAAABAAEAAAICTAEAOw==";

  /* ══════════════════════════════════════════════════════════════════════════
     PARENTAL CONTROL SUBSYSTEM
     Mirrors BigBoxWeb/web/engine/app.js :: parental section ~line 88.
     Shared state lives in a cookie 'extenddb_unlocked' written server-side
     by /api/parental/unlock and /api/parental/lock.  This module reads that
     state via GET /api/parental/state (same endpoint as BBW).
     ══════════════════════════════════════════════════════════════════════════ */

  /* ── Module-scope parental state object ────────────────────────────────────
     Initialised from config defaults (safe values) before the first fetch.
     Overwritten by fetchParental() immediately at boot.
     (ref: BigBoxWeb/web/engine/app.js :: parental ~line 89) */
  var parental = (window.LBW.configDefaults && window.LBW.configDefaults.parental)
    ? JSON.parse(JSON.stringify(window.LBW.configDefaults.parental))
    : { active: false, locked: false, canUnlock: false, bigBox: false,
        lockedOut: false, maxAttempts: 3, canRate: true, canFav: true, installNeedsUnlock: false, lang: "en" };

  /* Guard: has the polling interval already been started?  Never start twice.
     (mirrors BBW: startParentalWatch sets interval once) */
  var _lbParentalWatchStarted = false;

  /* SVG strings for padlock icon — closed vs open shackle.
     (ref: BigBoxWeb/web/engine/app.js :: LOCK_CLOSED_SVG / LOCK_OPEN_SVG ~line 3347) */
  var LB_LOCK_CLOSED_SVG = '<svg viewBox="0 0 24 24" width="18" height="18" aria-hidden="true"><path d="M8 10V7a4 4 0 0 1 8 0v3" fill="none" stroke="currentColor" stroke-width="2"/><rect x="5.5" y="10" width="13" height="9.5" rx="2" fill="currentColor"/></svg>';
  var LB_LOCK_OPEN_SVG   = '<svg viewBox="0 0 24 24" width="18" height="18" aria-hidden="true"><path d="M8 10V7a4 4 0 0 1 7.6-1.6" fill="none" stroke="currentColor" stroke-width="2"/><rect x="5.5" y="10" width="13" height="9.5" rx="2" fill="currentColor"/></svg>';

  /* ── lbUnlockAudio ──────────────────────────────────────────────────────────
     Called on first user gesture (keydown / pointerdown / touchstart).
     Sets lbAudioOn=true then attempts to unmute the current panel video.
     Mirrors BBW unlockAudio (BigBoxWeb/web/engine/app.js:189-226).
     (ref: BigBoxWeb/web/engine/app.js :: unlockAudio ~line 189) */
  function lbUnlockAudio() {
    lbAudioOn = true;
    var v = document.querySelector(".lb-panel-media video");
    if (v && v.muted) {
      v.muted = false;
      var p = v.play();
      if (p && p.catch) {
        p.catch(function () { v.muted = true; });
      }
    }
  }

  /* ── canRateGame / canFavGame ───────────────────────────────────────────────
     Mirrors BBW's helper functions; default true so that if parental config
     is absent nothing is blocked.
     (ref: BigBoxWeb/web/engine/app.js :: canRateGame / canFavGame ~line 98) */
  function canRateGame() { return !!parental.canRate; }
  function canFavGame()  { return !!parental.canFav;  }

  /* ── lbHttp — true when running over HTTP/S and fetch is available ─────────
     (ref: BigBoxWeb/web/engine/app.js :: bbwHttp ~line 3265) */
  function lbHttp() {
    return (location.protocol === "http:" || location.protocol === "https:") &&
           typeof fetch === "function";
  }

  /* ── fetchParental ──────────────────────────────────────────────────────────
     GETs /api/parental/state, updates the module-scope parental{} object, then
     calls applyParentalDom() to refresh header indicator + menu items.
     Returns a Promise that resolves to the state object (or null on failure).
     Called at boot (before first cattree fetch) and after unlock/lock.
     (ref: BigBoxWeb/web/engine/app.js :: fetchParental ~line 3267) */
  function fetchParental() {
    if (!lbHttp()) { return Promise.resolve(null); }
    return fetch("/api/parental/state", { cache: "no-store" })
      .then(function (r) { return r.ok ? r.json() : null; })
      .then(function (ps) {
        if (!ps) { return null; }
        /* Overwrite every field; use safe default if field missing from server.
           (ref: BigBoxWeb/web/engine/app.js :: boot parental assignment ~line 5431) */
        parental.active     = !!ps.active;
        parental.locked     = !!ps.locked;
        parental.canUnlock  = !!ps.canUnlock;
        parental.bigBox     = !!ps.bigBox;
        parental.lockedOut  = !!ps.lockedOut;
        parental.maxAttempts= ps.maxAttempts || 3;
        parental.canRate    = (ps.canRate === undefined) ? true : !!ps.canRate;
        parental.canFav     = (ps.canFav  === undefined) ? true : !!ps.canFav;
        parental.installNeedsUnlock = !!ps.installNeedsUnlock;
        parental.lang       = ps.lang || "en";
        applyParentalDom();
        return ps;
      })
      .catch(function () { return null; });
  }

  /* ── lockState ──────────────────────────────────────────────────────────────
     Returns "red" | "green" | "open" | null (nothing to show).
     (ref: BigBoxWeb/web/engine/app.js :: lockState ~line 3350) */
  function lbLockState() {
    if (!parental.active) { return null; }
    if (!parental.locked) { return "open"; }
    return parental.bigBox ? "red" : "green";
  }

  /* ── renderLbLockIndicator ──────────────────────────────────────────────────
     Updates the <span id="lb-lockind"> element with the correct SVG + class.
     (ref: BigBoxWeb/web/engine/app.js :: renderLockIndicators ~line 3355) */
  function renderLbLockIndicator() {
    var ind = document.getElementById("lb-lockind");
    if (!ind) { return; }
    var st = lbLockState();
    /* Remove all state classes */
    ind.classList.remove("red", "green", "open");
    if (st === null) {
      ind.innerHTML = "";
      return;
    }
    ind.classList.add(st);
    ind.innerHTML = (st === "open") ? LB_LOCK_OPEN_SVG : LB_LOCK_CLOSED_SVG;
    ind.title = (st === "red")   ? "BigBox parental lock — controlled by BigBox"
              : (st === "green") ? "Content filtered — click MENU to unlock"
              :                    "Parental control active — content unlocked";
  }

  /* ── applyParentalDom ───────────────────────────────────────────────────────
     Refreshes all parental-related DOM state:
       1. Lock indicator in the header.
       2. MENU dropdown items (Unlock / Lock visibility).
     Must be called after every parental state change.
     (ref: BigBoxWeb/web/engine/app.js :: applyParentalDom ~line 3336) */
  function applyParentalDom() {
    /* 1. Header lock indicator */
    renderLbLockIndicator();

    /* 2. MENU dropdown items */
    var unlockItem   = document.getElementById("lb-menu-unlock");
    var lockItem     = document.getElementById("lb-menu-lock");

    if (!unlockItem || !lockItem) { return; }

    if (!parental.active) {
      /* Parental feature entirely inactive: hide both Unlock and Lock entries */
      unlockItem.classList.add("lb-menu-hidden");
      lockItem.classList.add("lb-menu-hidden");
      return;
    }

    if (parental.locked) {
      /* Currently locked — show Unlock, hide Lock */
      lockItem.classList.add("lb-menu-hidden");
      if (parental.canUnlock || parental.bigBox) {
        unlockItem.classList.remove("lb-menu-hidden", "lb-menu-disabled");
      } else {
        /* Locked but can't unlock from this client */
        unlockItem.classList.remove("lb-menu-hidden");
        unlockItem.classList.add("lb-menu-disabled");
      }
    } else {
      /* Currently unlocked — show Lock, hide Unlock */
      unlockItem.classList.add("lb-menu-hidden");
      lockItem.classList.remove("lb-menu-hidden", "lb-menu-disabled");
    }
  }

  /* ── reloadLbAfterParental ──────────────────────────────────────────────────
     Called after a lock-state flip (unlock success, lockNow success, or poll
     detecting a changed state).  Re-fetches cattree.json so the server's
     updated filter is reflected, resets the grid and right panel.
     (ref: req §6 — reloadLbAfterParental helper) */
  function reloadLbAfterParental() {
    /* Wipe the games-list cache — every cached array was fetched under the
       previous parental state and must not be served again.  Cleared before
       the cattree re-fetch so no subsequent loadPlatform call can get a hit
       from the old state.
       (ref: req §3) */
    lbGamesCache = {};

    /* Bump the parental epoch so any in-flight games.json fetch that
       completes after this wipe can detect staleness and discard its
       response rather than re-polluting the now-empty cache.
       (ref: req §6) */
    lbParentalEpoch += 1;

    /* Reset detail cache — stale entries now have wrong visibility */
    lbDetailCache = {};
    lbDetailToken += 1;

    /* Cancel any pending heavy-detail debounce timer — the game it was
       targeting belongs to the previous parental state. */
    if (lbHeavyTimer) { clearTimeout(lbHeavyTimer); lbHeavyTimer = null; }

    /* Cancel any in-flight selection */
    posterSel = -1;

    /* Close play menu and hide play group — game context is now stale */
    closeLbPlayMenu();
    refreshLbPlayLabel(null);

    /* Clear right panel */
    var emptyEl   = document.querySelector(".lb-panel-empty");
    var contentEl = document.querySelector(".lb-panel-content");
    if (emptyEl)   { emptyEl.style.display = ""; }
    if (contentEl) { contentEl.setAttribute("hidden", ""); }

    /* Clear grid */
    DATA.games = [];
    var gridScroll = document.querySelector(".lb-grid-scroll");
    if (gridScroll) { gridScroll.innerHTML = ""; }

    /* Update grid header count */
    var countEl = document.querySelector(".lb-grid-count");
    if (countEl) { countEl.textContent = ""; }

    /* Re-fetch and re-render the tree (server now applies updated filter) */
    fetch("/launchbox/data/cattree.json", { cache: "no-store" })
      .then(function (res) {
        if (!res.ok) { throw new Error("HTTP " + res.status); }
        return res.json();
      })
      .then(function (data) {
        console.log("[LBW] reloadLbAfterParental → cattree re-fetched");
        renderTree(data);
      })
      .catch(function (err) {
        console.error("[LBW] reloadLbAfterParental cattree error:", err);
        var treeScroll = document.querySelector(".lb-tree-scroll");
        if (treeScroll) {
          treeScroll.innerHTML =
            '<div class="lb-tree-empty">Tree reload failed: ' +
            escapeHtml(String(err)) + "</div>";
        }
      });
  }

  /* ── startLbParentalWatch ───────────────────────────────────────────────────
     BigBox-only polling: re-checks parental state every 4 s.  If the locked
     flag changes, reloads the tree/grid immediately.
     Also re-checks on visibility and focus events (tablet wake-up).
     No-op in LaunchBox mode (cookie is only changed by submitPin / lockNow
     which already trigger reloadLbAfterParental).
     (ref: BigBoxWeb/web/engine/app.js :: startParentalWatch ~line 3284) */
  var _lbParentalFails = 0;
  function startLbParentalWatch() {
    if (!lbHttp()) { return; }
    if (!parental.bigBox)         { return; }   /* LaunchBox: cookie self-managed */
    if (_lbParentalWatchStarted)  { return; }   /* only once */
    _lbParentalWatchStarted = true;

    var pollMs = 4000;
    setInterval(checkLbParentalState, pollMs);
    document.addEventListener("visibilitychange", function () {
      if (!document.hidden) { checkLbParentalState(); }
    });
    window.addEventListener("focus", checkLbParentalState);
  }

  function checkLbParentalState() {
    if (!lbHttp()) { return; }
    fetch("/api/parental/state", { cache: "no-store" })
      .then(function (r) {
        if (!r.ok) { throw new Error("HTTP " + r.status); }
        return r.json();
      })
      .then(function (ps) {
        _lbParentalFails = 0;
        if (!ps) { return; }
        var changed = (!!ps.active !== parental.active) ||
                      (!!ps.locked !== parental.locked) ||
                      (!!ps.bigBox !== parental.bigBox) ||
                      (!!ps.canUnlock !== parental.canUnlock) ||
                      (!!ps.canRate !== parental.canRate) ||
                      (!!ps.canFav  !== parental.canFav) ||
                      (!!ps.installNeedsUnlock !== parental.installNeedsUnlock);
        if (changed) {
          /* Update state then reload tree/grid */
          parental.active    = !!ps.active;
          parental.locked    = !!ps.locked;
          parental.bigBox    = !!ps.bigBox;
          parental.canUnlock = !!ps.canUnlock;
          parental.canRate   = (ps.canRate === undefined) ? true : !!ps.canRate;
          parental.canFav    = (ps.canFav  === undefined) ? true : !!ps.canFav;
          parental.installNeedsUnlock = !!ps.installNeedsUnlock;
          applyParentalDom();
          reloadLbAfterParental();
        }
      })
      .catch(function () {
        _lbParentalFails++;
        /* Fail-closed: if we were unlocked and server is unreachable, reload
           to re-apply filter (safety measure mirroring BBW).
           (ref: BigBoxWeb/web/engine/app.js :: checkParentalState ~line 3304) */
        if (parental.active && !parental.locked && _lbParentalFails >= 3) {
          _lbParentalFails = 0;
          reloadLbAfterParental();
        }
      });
  }

  /* ══════════════════════════════════════════════════════════════════════════
     PIN PAD — openPinPad / closePinPad / submitPin
     Mirrors BigBoxWeb/web/engine/app.js :: openPinPad / closePinPad /
     submitPin ~line 3250.
     The PIN pad is the modal #lb-pinpad-modal defined in index.html.
     ══════════════════════════════════════════════════════════════════════════ */

  /* Module-scope PIN pad state — mirrors BBW pinOpen / pinValue ~line 69 */
  var lbPinOpen  = false;
  var lbPinValue = "";
  /* "unlock" = global parental unlock ; "install" = one-shot install authorization
     (verifies the PIN but does NOT unlock parental globally, so no catalog reload). */
  var lbPinPurpose = "unlock", lbPinInstallGi = -1;

  /* ── openPinPad ─────────────────────────────────────────────────────────── */
  function openPinPad(purpose) {
    if (lbPinOpen) { return; }
    lbPinPurpose = purpose || "unlock";
    lbPinOpen  = true;
    lbPinValue = "";
    var modal = document.getElementById("lb-pinpad-modal");
    if (modal) { modal.removeAttribute("hidden"); }
    /* Explain WHY the pad is up: install parental-blocked (PIN authorizes just this install) vs global unlock. */
    setLbPinTitle(lbPinPurpose === "install" ? "Install blocked by parental control — enter PIN to authorize" : "Enter your PIN");
    setLbPinError("");
    paintLbPin();
    /* Focus the modal panel so keyboard events reach it */
    if (modal) { modal.setAttribute("tabindex", "-1"); modal.focus(); }
  }

  /* ── closePinPad ────────────────────────────────────────────────────────── */
  function closePinPad() {
    lbPinOpen = false;
    var modal = document.getElementById("lb-pinpad-modal");
    if (modal) { modal.setAttribute("hidden", ""); }
  }

  /* ── setLbPinTitle ──────────────────────────────────────────────────────── */
  function setLbPinTitle(msg) {
    var el = document.getElementById("lb-pinpad-title");
    if (el) { el.textContent = msg; }
  }

  /* ── setLbPinError ──────────────────────────────────────────────────────── */
  function setLbPinError(msg) {
    var el = document.getElementById("lb-pinpad-error");
    if (el) { el.textContent = msg; }
  }

  /* ── paintLbPin ─────────────────────────────────────────────────────────── */
  /* Writes the asterisk-masked PIN into the display element.
     (ref: BigBoxWeb/web/engine/app.js :: paintPin ~line 3253) */
  function paintLbPin() {
    var el = document.getElementById("lb-pinpad-display");
    if (el) { el.textContent = lbPinValue.replace(/./g, "•"); } /* bullet */
  }

  /* ── submitPin ──────────────────────────────────────────────────────────── */
  /* POSTs {pin} to /api/parental/unlock; on success reloads tree/grid.
     (ref: BigBoxWeb/web/engine/app.js :: submitPin ~line 3310) */
  function submitPin() {
    if (!lbHttp()) { closePinPad(); return; }
    var pin = lbPinValue;
    setLbPinTitle("Checking…");
    setLbPinError("");
    if (lbPinPurpose === "install") {
      /* One-shot install authorization: verify the PIN at the install endpoint. On success
         the install fires and we keep browsing — parental stays locked (no unlock, no reload). */
      var gi = lbPinInstallGi;
      var g = DATA.games[gi];
      if (!g || g.id == null) { closePinPad(); return; }
      fetch("/launchbox/api/games/" + encodeURIComponent(String(g.id)) + "/install", {
        method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ pin: pin })
      })
      .then(function (r) { return r.json().catch(function () { return null; }); })
      .then(function (j) {
        if (j && j.ok) {
          closePinPad();
          lbPlayBlockUntilTs = Date.now() + 5000;
          startLbInstallPoll(gi, 2500);
          return;
        }
        var reason = j && j.reason;
        if (reason === "locked-out") { setLbPinTitle("Locked out"); setLbPinError("Too many attempts — restart required."); parental.lockedOut = true; applyParentalDom(); }
        else if (j && typeof j.attemptsRemaining === "number") { setLbPinTitle("Wrong PIN"); setLbPinError(j.attemptsRemaining + " attempt(s) remaining."); }
        else { setLbPinTitle("Wrong PIN"); setLbPinError("Please try again."); }
        lbPinValue = ""; paintLbPin();
      })
      .catch(function () { setLbPinTitle("Error"); setLbPinError("Try again."); lbPinValue = ""; paintLbPin(); });
      return;
    }
    fetch("/api/parental/unlock", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ pin: pin })
    })
    .then(function (r) { return r.json(); })
    .then(function (res) {
      if (res && res.success) {
        closePinPad();
        fetchParental().then(function () { reloadLbAfterParental(); });
        return;
      }
      var reason = res && res.reason;
      if (reason === "locked-out") {
        setLbPinTitle("Locked out");
        setLbPinError("Too many attempts — restart required.");
        parental.lockedOut = true;
        applyParentalDom();
      } else if (res && typeof res.attemptsRemaining === "number") {
        setLbPinTitle("Wrong PIN");
        setLbPinError(res.attemptsRemaining + " attempt(s) remaining.");
      } else if (reason === "not-allowed") {
        setLbPinTitle("Not available");
        setLbPinError("Unlock not permitted from this client.");
      } else {
        setLbPinTitle("Wrong PIN");
        setLbPinError("Please try again.");
      }
      lbPinValue = ""; paintLbPin();
    })
    .catch(function () {
      setLbPinTitle("Error");
      setLbPinError("Network error — please try again.");
      lbPinValue = ""; paintLbPin();
    });
  }

  /* ── lockNow ────────────────────────────────────────────────────────────── */
  /* POSTs to /api/parental/lock to re-lock (clear cookie); always reloads.
     (ref: BigBoxWeb/web/engine/app.js :: lockNow ~line 3327) */
  function lockNow() {
    if (!lbHttp()) { return; }
    fetch("/api/parental/lock", { method: "POST" })
      .then(function () {
        return fetchParental();
      })
      .then(function () {
        reloadLbAfterParental();
      })
      .catch(function () {
        fetchParental().then(function () { reloadLbAfterParental(); });
      });
  }

  /* ── openParentalInfo (BigBox info modal) ─────────────────────────────── */
  function openParentalInfo() {
    var modal = document.getElementById("lb-info-modal");
    if (modal) { modal.removeAttribute("hidden"); }
  }

  function closeParentalInfo() {
    var modal = document.getElementById("lb-info-modal");
    if (modal) { modal.setAttribute("hidden", ""); }
  }

  /* ── setupLbParentalModals ──────────────────────────────────────────────── */
  /* Wires all click + keyboard handlers for the PIN pad and BigBox info modal.
     Called once from DOMContentLoaded.
     (ref: BigBoxWeb/web/engine/app.js :: setupPinPad ~line 3237
            + setupInfoModal ~line 3378) */
  function setupLbParentalModals() {
    /* ── PIN pad buttons ──────────────────────────────────────────────── */
    var pinModal = document.getElementById("lb-pinpad-modal");
    if (pinModal) {
      /* Digit and action buttons */
      var keys = pinModal.querySelectorAll(".lb-pinpad-key");
      keys.forEach(function (key) {
        key.addEventListener("click", function (e) {
          e.stopPropagation();
          if (!lbPinOpen) { return; }
          var k = key.dataset.k;
          if (k === "del") {
            lbPinValue = lbPinValue.slice(0, -1);
          } else if (k === "done") {
            submitPin(); return;
          } else if (lbPinValue.length < 8) {
            lbPinValue += k;
          }
          paintLbPin();
        });
      });

      /* Cancel button */
      var cancelBtn = document.getElementById("lb-pinpad-cancel");
      if (cancelBtn) {
        cancelBtn.addEventListener("click", function (e) {
          e.stopPropagation();
          closePinPad();
        });
      }

      /* Click on the overlay scrim (outside the panel) closes the pad */
      pinModal.addEventListener("click", function (e) {
        if (e.target === pinModal) { closePinPad(); }
      });
    }

    /* ── BigBox info modal ────────────────────────────────────────────── */
    var infoModal = document.getElementById("lb-info-modal");
    if (infoModal) {
      document.getElementById("lb-info-close") &&
        document.getElementById("lb-info-close").addEventListener("click", closeParentalInfo);
      infoModal.addEventListener("click", function (e) {
        if (e.target === infoModal) { closeParentalInfo(); }
      });
    }
  }

  /* ══════════════════════════════════════════════════════════════════════════
     MENU DROPDOWN WIRING
     The MENU button in the header reveals a small dropdown with parental
     Unlock / Lock items and a Settings shortcut.
     (ref: req §3 — MENU dropdown)
     ══════════════════════════════════════════════════════════════════════════ */

  function setupLbMenuDropdown() {
    var menuWrap     = document.getElementById("lb-menu-wrap");
    var menuBtn      = document.getElementById("lb-menu-btn");
    var menuDropdown = document.getElementById("lb-menu-dropdown");
    var unlockItem   = document.getElementById("lb-menu-unlock");
    var lockItem     = document.getElementById("lb-menu-lock");
    var settingsItem = document.getElementById("lb-menu-settings");

    if (!menuBtn || !menuDropdown) { return; }

    function openMenu() {
      menuDropdown.classList.add("open");
      menuBtn.setAttribute("aria-expanded", "true");
    }

    function closeMenu() {
      menuDropdown.classList.remove("open");
      menuBtn.setAttribute("aria-expanded", "false");
    }

    /* Toggle on MENU click */
    menuBtn.addEventListener("click", function (e) {
      e.stopPropagation();
      if (menuDropdown.classList.contains("open")) { closeMenu(); }
      else { openMenu(); }
    });

    /* Stop propagation on the dropdown itself so document-level handler
       doesn't immediately close it on the same click */
    menuDropdown.addEventListener("click", function (e) {
      e.stopPropagation();
    });

    /* Close on any click outside */
    document.addEventListener("click", function () { closeMenu(); });

    /* Close on Escape */
    document.addEventListener("keydown", function (e) {
      if (e.key === "Escape") { closeMenu(); }
    });

    /* ── Unlock item ──────────────────────────────────────────────────── */
    if (unlockItem) {
      unlockItem.addEventListener("click", function () {
        closeMenu();
        if (!parental.active) { return; }
        if (parental.bigBox) { openParentalInfo(); return; }
        openPinPad();
      });
    }

    /* ── Lock item ────────────────────────────────────────────────────── */
    if (lockItem) {
      lockItem.addEventListener("click", function () {
        closeMenu();
        if (!parental.active) { return; }
        if (parental.bigBox) { openParentalInfo(); return; }
        lockNow();
      });
    }

    /* ── Settings item ────────────────────────────────────────────────── */
    if (settingsItem) {
      settingsItem.addEventListener("click", function () {
        closeMenu();
        openLbCfgModal();
      });
    }
  }

  /* ── lbParam ─────────────────────────────────────────────────────────────
     Reads a param from the URL hash OR query string (hash wins). Mirrors the
     BigBoxWeb engine/app.js :: param() helper. Used for the kiosk #embedded=1
     flag and the #platform=&gameId= deep-link planted by GameLaunchHook. */
  function lbParam(k) {
    var mH = new RegExp("[#&]" + k + "=([^&]+)").exec(location.hash || "");
    if (mH) { return decodeURIComponent(mH[1]); }
    var mQ = new RegExp("[?&]" + k + "=([^&]+)").exec(location.search || "");
    return mQ ? decodeURIComponent(mQ[1]) : null;
  }

  /* ── findLbLeafByPath ────────────────────────────────────────────────────
     Returns the .lb-tree-node leaf row whose attached node.path equals `path`
     (e.g. "platforms/nintendo-64"), or null. Matches even when the row sits in
     a collapsed category (querySelectorAll sees hidden rows). */
  function findLbLeafByPath(path) {
    var rows = document.querySelectorAll(".lb-tree-node");
    for (var i = 0; i < rows.length; i++) {
      if (rows[i]._lbNode && rows[i]._lbNode.path === path) { return rows[i]; }
    }
    return null;
  }

  /* ── maybeDeepLinkToGame ─────────────────────────────────────────────────
     Kiosk-restore deep-link. When the URL carries #platform=<slug>&gameId=<guid>
     (planted by Forms/GameLaunchHook.RestoreKioskAfterGame after a game launched
     from the LaunchBox kiosk exits), navigate to that platform and select the
     just-played game's cell so the user lands back where they were. No-op when
     either param is absent or the game isn't found. Runs once, after
     renderTree() has built the tree.
     (mirrors BigBoxWeb/web/engine/app.js :: deepGameId block ~line 5505) */
  function maybeDeepLinkToGame() {
    var slug = lbParam("platform");
    var gid  = lbParam("gameId");
    if (!slug || !gid) { return; }

    var path = "platforms/" + slug;
    console.log("[LBW] deep-link: platform=" + path + " gameId=" + gid);

    /* Fires after the platform's grid is built (passed to loadPlatform as its
       onReady) — locate the game by GUID and select its cell + panel. */
    function selectWhenLoaded() {
      var idx = -1;
      for (var i = 0; i < DATA.games.length; i++) {
        if (DATA.games[i] && DATA.games[i].id === gid) { idx = i; break; }
      }
      if (idx >= 0) {
        console.log("[LBW] deep-link: selecting cell", idx);
        selectCell(idx);
      } else {
        console.log("[LBW] deep-link: gameId not in platform games — grid left at top.");
      }
    }

    /* Prefer driving the matching tree leaf (gives tree highlight + grid header
       + proper platform name). Fall back to a bare loadPlatform if the leaf
       isn't in the tree (e.g. filtered out by a parental rule). */
    var leaf = findLbLeafByPath(path);
    if (leaf && leaf._lbNode) {
      /* Expand the leaf's category if collapsed, so the highlighted leaf is
         actually visible in the tree (only the first category auto-expands).
         header + children are siblings (buildCategoryGroup appends header then
         the .lb-tree-children wrap). */
      var childrenWrap = leaf.closest(".lb-tree-children");
      if (childrenWrap) {
        var catHeader = childrenWrap.previousElementSibling;
        if (catHeader && catHeader.classList.contains("lb-tree-cat") &&
            catHeader.classList.contains("collapsed")) {
          toggleCategoryCollapsed(catHeader);
        }
      }
      selectLeaf(leaf, leaf._lbNode.name || "", leaf._lbNode.count, path, selectWhenLoaded);
    } else {
      loadPlatform(path, slug, selectWhenLoaded);
    }
  }

  /* ══════════════════════════════════════════════════════════════════════════
     Detail-pane tabs : Overview / Related Games
     Adds a discreet "Related Games" tab to the right detail pane, reusing the
     SAME engine output as the BigBox web popup (/bigbox/data/games/{id}/
     related.json — same server, same id, parental cookie sent automatically,
     gated on the Similar module server-side). Overview = the existing content.
     ══════════════════════════════════════════════════════════════════════════ */
  var LB_REL_TABS = ["recommended", "similar", "ports"];
  var lbRelTab   = "recommended";   // active sub-tab
  var lbRelData  = null;            // last fetched { recommended, similar, ports }
  var lbRelKey   = null;            // game id the data belongs to (avoid refetch)
  var lbRelToken = 0;               // supersede stale fetches

  function setupLbDetailTabs() {
    var tabs = document.getElementById("lb-detail-tabs");
    if (tabs) tabs.addEventListener("click", function (e) {
      var b = e.target.closest(".lb-detail-tab"); if (!b) return;
      lbSwitchDetailPane(b.getAttribute("data-pane"));
    });
    var sub = document.querySelector(".lb-rel-subtabs");
    if (sub) sub.addEventListener("click", function (e) {
      var b = e.target.closest(".lb-rel-subtab"); if (!b) return;
      lbRelTab = b.getAttribute("data-rel") || "recommended";
      var all = document.querySelectorAll(".lb-rel-subtab");
      for (var i = 0; i < all.length; i++)
        all[i].classList.toggle("active", all[i].getAttribute("data-rel") === lbRelTab);
      lbRenderRel();
    });
    var list = document.getElementById("lb-rel-list");
    if (list) list.addEventListener("click", function (e) {
      var card = e.target.closest(".lb-rel-card"); if (!card) return;
      lbOpenRelated(card.getAttribute("data-id"), parseInt(card.getAttribute("data-dbid"), 10) || 0,
                    card.getAttribute("data-link") || "");
    });
  }

  /* Called from fillLbPanel on every selection: back to Overview, drop cache. */
  function resetLbDetailTabs() {
    lbSwitchDetailPane("overview");
    lbRelData = null; lbRelKey = null;
    var list = document.getElementById("lb-rel-list");
    if (list) list.innerHTML = "";
  }

  function lbSwitchDetailPane(pane) {
    pane = (pane === "related") ? "related" : "overview";
    var tabs = document.querySelectorAll(".lb-detail-tab");
    for (var i = 0; i < tabs.length; i++)
      tabs[i].classList.toggle("active", tabs[i].getAttribute("data-pane") === pane);
    var panes = document.querySelectorAll(".lb-detail-pane");
    for (var j = 0; j < panes.length; j++) {
      if (panes[j].getAttribute("data-pane") === pane) panes[j].removeAttribute("hidden");
      else panes[j].setAttribute("hidden", "");
    }
    if (pane === "related") lbLoadRelated();
  }

  function lbLoadRelated() {
    var g = DATA.games[posterSel]; if (!g || !g.id) return;
    if (lbRelKey === g.id && lbRelData) { lbRenderRel(); return; }   // already loaded for this game
    var list = document.getElementById("lb-rel-list");
    if (list) list.innerHTML = '<div class="lb-rel-empty">Loading…</div>';
    var key = g.id; var tok = ++lbRelToken;
    fetch("/bigbox/data/games/" + encodeURIComponent(String(g.id)) + "/related.json?limit=50",
          { cache: "no-store", credentials: "same-origin" })
      .then(function (r) { return r.ok ? r.json() : null; })
      .then(function (j) {
        if (tok !== lbRelToken) return;                 // a newer selection won
        lbRelData = j || { recommended: [], similar: [], ports: [] };
        lbRelKey = key;
        lbRenderRel();
        lbFillRelOverviews();
      })
      .catch(function () {
        if (tok !== lbRelToken) return;
        if (list) list.innerHTML = '<div class="lb-rel-empty">Couldn’t load related games.</div>';
      });
  }

  function lbRenderRel() {
    var list = document.getElementById("lb-rel-list"); if (!list) return;
    var arr = (lbRelData && lbRelData[lbRelTab]) || [];
    if (!arr.length) { list.innerHTML = '<div class="lb-rel-empty">No suggestions.</div>'; return; }
    var html = "";
    for (var i = 0; i < arr.length; i++) {
      var it = arr[i];
      var meta = [it.y, it.plat].filter(Boolean).join("  ·  ");
      var cloud = it.local ? "" : '<span class="lb-rel-cloud" title="Database game">☁</span>';
      html += '<div class="lb-rel-card" data-id="' + lbEsc(it.id) + '" data-dbid="' + (it.dbid || 0) + '"'
            +      ' data-link="' + lbEsc(it.link || "") + '">'
            +   '<img class="lb-rel-thumb" loading="lazy" src="' + lbEsc(it.thumb || "") + '" alt=""'
            +        ' onerror="this.style.visibility=\'hidden\'">'
            +   '<div class="lb-rel-body">'
            +     '<div class="lb-rel-top">'
            +       '<span class="lb-rel-title">' + lbEsc(it.t || "") + '</span>'
            +       '<span class="lb-rel-score">' + cloud + (it.pct != null ? (it.pct + "%") : "") + '</span>'
            +     '</div>'
            +     '<div class="lb-rel-meta">' + lbEsc(meta) + '</div>'
            +     '<div class="lb-rel-desc" data-dbid="' + (it.dbid || 0) + '">' + lbEsc(it.d || "") + '</div>'
            +   '</div>'
            + '</div>';
    }
    list.innerHTML = html;
  }

  /* Batch-fill the card descriptions for DB ids that came back empty (mirrors the
     BigBox popup's overview fill). */
  function lbFillRelOverviews() {
    if (!lbRelData) return;
    var seen = {}, order = [];
    for (var t = 0; t < LB_REL_TABS.length; t++) {
      var arr = lbRelData[LB_REL_TABS[t]] || [];
      for (var i = 0; i < arr.length; i++) {
        var it = arr[i];
        if (it.dbid > 0 && !it.d && !seen[it.dbid]) { seen[it.dbid] = 1; order.push(it.dbid); }
      }
    }
    if (!order.length) return;
    var tok = lbRelToken;
    fetch("/bigbox/data/games/related/overviews.json?ids=" + order.slice(0, 200).join(","),
          { cache: "no-store", credentials: "same-origin" })
      .then(function (r) { return r.ok ? r.json() : null; })
      .then(function (map) {
        if (!map || tok !== lbRelToken) return;
        var els = document.querySelectorAll("#lb-rel-list .lb-rel-desc");
        for (var i = 0; i < els.length; i++) {
          var id = els[i].getAttribute("data-dbid");
          if (id && map[id]) els[i].textContent = map[id];
        }
      })
      .catch(function () {});
  }

  /* Click a related card: if the game is in the current list, jump to it (resets
     the pane to Overview); otherwise follow the SERVER-resolved link — the local
     web DB page when the active DB covers the id, else the id-range source site
     (ScreenScraper/VNDB/Steam/gamesdb). Legacy payloads without a link keep the
     old local-page behavior. */
  function lbOpenRelated(id, dbid, link) {
    if (id) {
      for (var i = 0; i < DATA.games.length; i++) {
        if (DATA.games[i] && String(DATA.games[i].id) === String(id)) { selectCell(i); return; }
      }
    }
    if (link) { try { window.open(link, "_blank", "noopener"); } catch (e) {} return; }
    if (dbid > 0) { try { window.open("/games/" + dbid + ".html", "_blank"); } catch (e) {} }
  }

  function lbEsc(s) {
    return String(s == null ? "" : s)
      .replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;").replace(/'/g, "&#39;");
  }

  /* ── DOMContentLoaded entry point ───────────────────────────────────────── */
  document.addEventListener("DOMContentLoaded", function () {

    /* ── a. Boot log ──────────────────────────────────────────────────────── */
    console.log("LB Web booted", window.LBW.config);

    /* ── a1. Audio unlock gesture listeners (A2) ──────────────────────────────
       Three listeners so the first real user interaction unmutes the video.
       lbOnKey is registered separately and is NOT replaced by this — both
       coexist on the 'keydown' event.
       (ref: BigBoxWeb/web/engine/app.js :: unlockAudio listeners ~line 224) */
    document.addEventListener("keydown",     lbUnlockAudio);
    document.addEventListener("pointerdown", lbUnlockAudio);
    document.addEventListener("touchstart",  lbUnlockAudio, { passive: true });

    /* ── a2. Parental state — fetch BEFORE cattree so the server's filter
             applies to the very first tree render.
             (ref: BigBoxWeb/web/engine/app.js :: boot Promise.all fetchParental
              ~line 5419; here done first so it settles before cattree fetch) */
    var parentalReady = fetchParental();

    /* ── a3. Apply cfg CSS vars / column widths from cookie-restored config ──
             Must run AFTER parental (which may update lang etc.) and BEFORE
             the cattree fetch so column widths and grid params are applied
             before the first paint.
             (ref: BBW engine/app.js :: applyConfigCss call at DOMContentLoaded
              ~line 600; BBW calls it once synchronously at boot) */
    applyLbCfgToDom();

    /* Detail-pane Overview / Related Games tabs (static markup → wire once). */
    try { setupLbDetailTabs(); } catch (e) { console.error("[LBW] setupLbDetailTabs:", e); }

    /* ── b. Fetch cattree and render tree ────────────────────────────────── */
    parentalReady.then(function () {
      return fetch("/launchbox/data/cattree.json");
    })
      .then(function (res) {
        if (!res.ok) {
          throw new Error("HTTP " + res.status + " " + res.statusText);
        }
        return res.json();
      })
      .then(function (data) {
        console.log("[LBW] cattree.json →", data);
        renderTree(data);
        /* Start polling only after first fetch (so parental.bigBox is set).
           (ref: BigBoxWeb/web/engine/app.js :: startParentalWatch call ~line 5461) */
        startLbParentalWatch();
        /* Kiosk-restore deep-link: jump to #platform=&gameId= if present. Must
           run after renderTree so the target tree leaf exists to be found. */
        maybeDeepLinkToGame();
      })
      .catch(function (err) {
        console.error("[LBW] cattree.json fetch error:", err);
        var treeScroll = document.querySelector(".lb-tree-scroll");
        if (treeScroll) {
          treeScroll.innerHTML =
            '<div class="lb-tree-empty">Tree load failed: ' +
            escapeHtml(String(err)) +
            "</div>";
        }
      });

    /* ── c. Settings modal wiring ─────────────────────────────────────────── */
    /* Open / close wired to the full openLbCfgModal / closeLbCfgModal
       functions defined in the CFG MODAL section below.
       (ref: BBW engine/app.js :: setupConfig / openConfig / closeConfig
        ~line 3677) */
    var cfgBtn = document.querySelector(".lb-hbar-cfg");
    if (cfgBtn) { cfgBtn.addEventListener("click", openLbCfgModal); }

    var cfgCloseBtn  = document.getElementById("lb-cfg-close");
    var cfgOkBtn     = document.getElementById("lb-cfg-ok");
    var cfgResetBtn  = document.getElementById("lb-cfg-reset");
    var cfgOverlay   = document.getElementById("lb-cfg-overlay");

    if (cfgCloseBtn) { cfgCloseBtn.addEventListener("click",  closeLbCfgModal); }
    if (cfgOkBtn)    { cfgOkBtn.addEventListener("click",     closeLbCfgModal); }
    if (cfgOverlay)  { cfgOverlay.addEventListener("click",   closeLbCfgModal); }
    if (cfgResetBtn) { cfgResetBtn.addEventListener("click",  lbCfgReset); }

    /* ── d. LazyLoad — initial instance scoped to the poster grid ─────────── */
    /* The instance is REPLACED by buildLbGrid() on each platform switch.
       We create a bootstrap instance here so the reference always exists.
       (pattern: window._lbLazyLoad mirrors window._posterLazyLoad in BBW
        BigBoxWeb/web/engine/app.js ~line 763) */
    var gridScroll = document.querySelector(".lb-grid-scroll");

    window._lbLazyLoad = new LazyLoad({
      container:          gridScroll,
      elements_selector:  ".lb-cell .rc-img.lazy",
      cancel_on_exit:     false,
      use_native:         false,
      threshold:          1000
    });

    /* ── e. Parental modal + menu dropdown wiring ─────────────────────────── */
    setupLbParentalModals();
    setupLbRomModal();
    setupLbMenuDropdown();
    /* Apply the persisted Image/List view mode to <body> + wire the VIEW menu
       (before the first game render so renderGames() builds the right view). */
    initLbViewMode();
    setupLbArrangeMenu();

    /* ── f. Play button + caret wiring ────────────────────────────────────── */
    /* Play button: direct launch (uses selected version + ROM from state).
       (mirrors BBW play action handler ~line 3110) */
    var playBtn  = document.getElementById("lb-panel-play");
    var caretBtn = document.getElementById("lb-panel-play-caret");

    if (playBtn) {
      playBtn.addEventListener("click", function (e) {
        e.stopPropagation();
        /* Close any open menu before launching */
        closeLbPlayMenu();
        /* Store game not installed → trigger the store's install instead of launch. */
        var g = DATA.games[posterSel];
        if (g && g.store && g.installed === false) {
          /* Parental: install gated behind the PIN while locked → PIN pad in "install"
             mode. A correct PIN authorizes THIS install only (one-shot): no global
             parental unlock, no catalog reload. */
          if (parental.installNeedsUnlock) { lbPinInstallGi = posterSel; openPinPad("install"); return; }
          postLbInstall(); return;
        }
        /* Launch the SELECTED emulator (explicit pick → last → default). The
           caret menu only selects; the actual launch happens here. */
        var emuId = g ? lbResolveEmuId(g) : null;
        postLbLaunch(emuId ? { emulatorId: emuId } : {});
      });
    }

    /* ↺ reset-to-default : annule l'historique serveur + purge les picks client
       et re-résout les défauts. Le bouton se masque tout seul via le refresh. */
    var resetBtn0 = document.getElementById("lb-panel-play-reset");
    if (resetBtn0) {
      resetBtn0.addEventListener("click", function (e) {
        e.stopPropagation();
        closeLbPlayMenu();
        var gR = DATA.games[posterSel];
        if (gR) { lbResetToDefaults(gR); }
      });
    }

    /* Caret: toggle dropdown; stop propagation so document click doesn't
       immediately close it via the outside-click handler below. */
    if (caretBtn) {
      caretBtn.addEventListener("click", function (e) {
        e.stopPropagation();
        toggleLbPlayMenu();
      });
    }

    /* Version / ROM selection buttons — open the shared dropdown seeded with
       the version / ROM list. These are SELECTIONS (remembered), not launches. */
    var verSelBtn = document.getElementById("lb-panel-version");
    if (verSelBtn) {
      verSelBtn.addEventListener("click", function (e) { e.stopPropagation(); openLbVersionMenu(); });
    }
    var romSelBtn = document.getElementById("lb-panel-rom");
    if (romSelBtn) {
      romSelBtn.addEventListener("click", function (e) { e.stopPropagation(); openLbRomMenu(); });
    }

    /* Document-level click outside #lb-play-group closes the menu.
       (mirrors BBW document-level click outside detail menu) */
    document.addEventListener("click", function (e) {
      if (!lbPlayMenuOpen) { return; }
      var group = document.getElementById("lb-play-group");
      if (group && !group.contains(e.target)) {
        closeLbPlayMenu();
      }
    });

    /* Initial state: hide the play group until a game is selected */
    (function () {
      var pg = document.getElementById("lb-play-group");
      if (pg) { pg.style.display = "none"; }
    })();

    /* ── g. Focus-zone wiring ─────────────────────────────────────────────────
       Default focus = center grid (set the body class without a flash).
       Clicking inside a column focuses it.  mousedown is used so the zone is set
       even though the inner row / cell handlers stop the click; fromMouse skips
       the left-zone auto-activate (the clicked row's own handler loads it). */
    setLbZone("center", { noFlash: true });

    var zoneTree  = document.getElementById("lb-tree");
    var zoneGrid  = document.getElementById("lb-grid");
    var zonePanel = document.getElementById("lb-panel");
    if (zoneTree)  { zoneTree.addEventListener("mousedown",  function () { setLbZone("left",   { fromMouse: true }); }); }
    if (zoneGrid)  { zoneGrid.addEventListener("mousedown",  function () { setLbZone("center", { fromMouse: true }); }); }
    if (zonePanel) { zonePanel.addEventListener("mousedown", function () { setLbZone("right",  { fromMouse: true }); }); }

    /* ── h. Mode embarqué (kiosque WebView2 hébergé par ExtendDB) ──────────────
       Détecté via le hash de l'URL : …/launchbox#embedded=1. En mode embarqué,
       expose le bouton exit (#lb-exit-btn, haut-droite) qui poste un WebMessage
       'kiosk:exit' au host WebView2, lequel ferme la fenêtre proprement. Le host
       injecte par ailleurs son propre listener clavier (F10/F11/F12) — voir
       Forms/BigBoxWebKioskFormsWindow.InitAsync.
       (ref: BigBoxWeb/web/engine/app.js :: bloc isEmbedded ~line 5480) */
    (function () {
      if (lbParam("embedded") !== "1") return;
      document.body.classList.add("lb-embedded");
      var exitBtn = document.getElementById("lb-exit-btn");
      if (exitBtn) {
        exitBtn.addEventListener("click", function () {
          try {
            if (window.chrome && window.chrome.webview && window.chrome.webview.postMessage) {
              window.chrome.webview.postMessage("kiosk:exit");
            }
          } catch (e) { /* WebMessage indispo (standalone) — pas critique */ }
        });
      }
    })();

  }); // DOMContentLoaded


  /* ══════════════════════════════════════════════════════════════════════════
     TREE RENDERING
     ══════════════════════════════════════════════════════════════════════════ */

  /* Currently-selected leaf node element (platform or playlist). */
  var _selectedNode = null;

  /* ── escapeHtml ──────────────────────────────────────────────────────────── */
  function escapeHtml(str) {
    return String(str)
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;");
  }

  /* ── makeIcon ────────────────────────────────────────────────────────────── */
  /* Returns an <img> element for the given display name. */
  function makeIcon(name) {
    var img = document.createElement("img");
    img.className = "lb-tree-icon";
    img.alt = "";
    /* Use the display name directly — the browser encodes spaces etc. */
    img.src = "/api/launchbox/icons/" + encodeURIComponent(name) + ".png";
    img.onerror = function () { this.style.display = "none"; };
    return img;
  }

  /* ── selectLeaf ─────────────────────────────────────────────────────────── */
  /* Modified (workflow D): also calls loadPlatform so the grid is populated
     when a leaf is clicked.
     (original version had only the header update) */
  function selectLeaf(nodeEl, name, count, path, onReady) {
    if (_selectedNode) {
      _selectedNode.classList.remove("sel");
    }
    nodeEl.classList.add("sel");
    _selectedNode = nodeEl;

    /* Update the grid header */
    var titleEl = document.querySelector(".lb-grid-title");
    var countEl = document.querySelector(".lb-grid-count");
    if (titleEl) { titleEl.textContent = name; }
    if (countEl) {
      countEl.textContent = (typeof count === "number" && !isNaN(count))
        ? count + " games"
        : "";
    }

    /* Trigger grid load for this leaf's path.
       Pass the platform name so loadPlatform can store it for fillLbPanel.
       onReady (optional) fires after the grid is built — used by the
       kiosk-restore deep-link to select a specific game once it's loaded.
       (added workflow D — mirrors BigBoxWeb catStack descend → loadPlatform) */
    if (path) {
      loadPlatform(path, name || "", onReady);
    }
  }

  /* ── buildLeafRow ────────────────────────────────────────────────────────── */
  /* Builds a .lb-tree-node row for a platform or playlist node. */
  function buildLeafRow(node, indented) {
    var row = document.createElement("div");
    row.className = "lb-tree-node";
    if (indented) { row.classList.add("lb-tree-node--child"); }

    /* Store the label on the element so the search filter can read it. */
    row.dataset.label = node.name || "";

    /* Keep the node so keyboard / gamepad tree navigation (lbNavLeft) can
       activate this row without re-deriving its data from the DOM. */
    row._lbNode = node;

    row.appendChild(makeIcon(node.name));

    var label = document.createElement("span");
    label.className = "lb-tree-label";
    label.textContent = node.name || "";
    row.appendChild(label);

    if (typeof node.count === "number") {
      var count = document.createElement("span");
      count.className = "lb-tree-count";
      count.textContent = node.count;
      row.appendChild(count);
    }

    row.addEventListener("click", function () {
      selectLeaf(row, node.name || "", node.count, node.path || "");
    });

    return row;
  }

  /* ── appendLeafWithKids ─────────────────────────────────────────────────── */
  /* Appends a platform/playlist leaf row. When the node NESTS children
     (Parents.xml: playlists — or categories — under a platform), the row gets
     its own expand CHEVRON (toggle only) and a collapsed-by-default
     sub-container of deeper-indented child rows — mirroring the desktop tree.
     Clicking the PLATFORM row itself still loads the platform's FULL games
     list (only BigBox turns such a platform into a category screen);
     clicking a nested playlist loads its subset. */
  function appendLeafWithKids(container, node, indented) {
    var kids = Array.isArray(node.children) ? node.children : [];
    var subKids = [];
    for (var i = 0; i < kids.length; i++) {
      var p = kids[i].path || "";
      if (p.indexOf("platforms/") === 0 || p.indexOf("playlists/") === 0) {
        subKids.push(kids[i]);
      }
    }
    var row = buildLeafRow(node, indented);
    if (subKids.length === 0) { container.appendChild(row); return; }

    row.classList.add("lb-tree-node--parent", "collapsed");
    var chev = document.createElement("span");
    chev.className = "lb-tree-chev";
    chev.textContent = "▾"; /* ▾ */
    chev.setAttribute("aria-hidden", "true");
    chev.title = "Expand / collapse";
    chev.addEventListener("click", function (e) {
      /* The chevron only toggles the sub-tree — it must not select/load. */
      e.stopPropagation();
      row.classList.toggle("collapsed");
    });
    row.insertBefore(chev, row.firstChild);
    container.appendChild(row);

    var wrap = document.createElement("div");
    wrap.className = "lb-tree-subchildren";
    for (var j = 0; j < subKids.length; j++) {
      var sub = buildLeafRow(subKids[j], true);
      sub.classList.add("lb-tree-node--sub");
      wrap.appendChild(sub);
    }
    container.appendChild(wrap);
  }

  /* ── buildCategoryGroup ─────────────────────────────────────────────────── */
  /* Builds a collapsible .lb-tree-cat + .lb-tree-children block. */
  function buildCategoryGroup(node, isFirst) {
    var frag = document.createDocumentFragment();

    /* Header row */
    var header = document.createElement("div");
    header.className = "lb-tree-cat";
    header.dataset.label = node.name || "";

    /* Keep the node for keyboard / gamepad tree navigation (lbNavLeft). */
    header._lbNode = node;

    /* Toggle handle = chevron + icon, wrapped in one clickable zone that
       covers the whole left gutter before the label text.  Clicking anywhere
       in this zone expands / collapses the category; clicking the label / count
       selects the category and shows its aggregated games (header handler
       below).  (Wider than the bare arrow so it is an easy target.) */
    var handle = document.createElement("span");
    handle.className = "lb-tree-cat-handle";
    handle.title = "Expand / collapse";

    var chev = document.createElement("span");
    chev.className = "lb-tree-chev";
    chev.textContent = "▾"; /* ▾ */
    chev.setAttribute("aria-hidden", "true");
    handle.appendChild(chev);

    handle.appendChild(makeIcon(node.name));

    handle.addEventListener("click", function (e) {
      /* Stop the bubble so the header's select-category handler does NOT also
         fire — the handle only toggles the tree, never loads games. */
      e.stopPropagation();
      toggleCategoryCollapsed(header);
    });
    header.appendChild(handle);

    var label = document.createElement("span");
    label.className = "lb-tree-label";
    label.textContent = (node.name || "").toUpperCase();
    header.appendChild(label);

    if (typeof node.count === "number") {
      var countSpan = document.createElement("span");
      countSpan.className = "lb-tree-count";
      countSpan.textContent = node.count;
      header.appendChild(countSpan);
    }

    /* Children container */
    var children = document.createElement("div");
    children.className = "lb-tree-children";

    /* Start collapsed; first category will be expanded after render */
    header.classList.add("collapsed");

    /* Build children */
    var childNodes = Array.isArray(node.children) ? node.children : [];
    for (var i = 0; i < childNodes.length; i++) {
      var child = childNodes[i];
      var childPath = child.path || "";
      if (childPath.indexOf("platforms/") === 0 ||
          childPath.indexOf("playlists/") === 0) {
        appendLeafWithKids(children, child, true);
      }
      /* Nested categories inside a category are treated as leaf rows
         (the spec only shows one level of categories). */
    }

    /* Click the header itself (anywhere but the chevron) → select the whole
       category and show the aggregated games of every platform / playlist it
       contains, recursively.  Expand / collapse is the chevron's job only. */
    header.addEventListener("click", function () {
      selectCategory(header, node);
    });

    frag.appendChild(header);
    frag.appendChild(children);
    return frag;
  }

  /* ── toggleCategoryCollapsed ─────────────────────────────────────────────
     Flips a category header between expanded and collapsed and rotates its
     chevron.  Shared by the chevron click handler and by renderTree's
     auto-expand of the first category (so the auto-expand never loads games). */
  function toggleCategoryCollapsed(header) {
    /* Chevron rotation follows the .collapsed class via CSS, so toggling the
       class is enough (and keeps it correct on the initial render). */
    return header.classList.toggle("collapsed");
  }

  /* ── collectCategoryLeafPaths ────────────────────────────────────────────
     Walks a category node depth-first and returns the data paths of every
     platform / playlist leaf underneath it (nested categories are descended
     into).  These are the paths whose games.json get fetched + merged when the
     category is selected. */
  function collectCategoryLeafPaths(node) {
    var out = [];
    (function walk(n) {
      var kids = Array.isArray(n.children) ? n.children : [];
      for (var i = 0; i < kids.length; i++) {
        var k = kids[i];
        var p = k.path || "";
        if (p.indexOf("platforms/") === 0 || p.indexOf("playlists/") === 0) {
          out.push(p);
        } else if (p.indexOf("categories/") === 0) {
          walk(k);
        }
      }
    })(node);
    return out;
  }

  /* ── selectCategory ──────────────────────────────────────────────────────
     Marks a category header as the selected tree node and loads the merged
     games of all its descendant platforms / playlists into the grid.
     Mirrors selectLeaf, but for a category header instead of a leaf row. */
  function selectCategory(headerEl, node) {
    if (_selectedNode) {
      _selectedNode.classList.remove("sel");
    }
    headerEl.classList.add("sel");
    _selectedNode = headerEl;

    var name = node.name || "";

    /* Update the grid header (count is refined by buildLbGrid once the merged
       list is built — node.count is the server's recursive total). */
    var titleEl = document.querySelector(".lb-grid-title");
    var countEl = document.querySelector(".lb-grid-count");
    if (titleEl) { titleEl.textContent = name; }
    if (countEl) {
      countEl.textContent = (typeof node.count === "number" && !isNaN(node.count))
        ? node.count + " games"
        : "";
    }

    loadCategory(node.path || "", collectCategoryLeafPaths(node), name);
  }

  /* ── renderTree ──────────────────────────────────────────────────────────── */
  function renderTree(nodes) {
    var treeScroll = document.querySelector(".lb-tree-scroll");
    if (!treeScroll) { return; }

    /* Clear "Loading…" placeholder */
    treeScroll.innerHTML = "";

    if (!Array.isArray(nodes) || nodes.length === 0) {
      treeScroll.innerHTML = '<div class="lb-tree-empty">No platforms found</div>';
      return;
    }

    var firstCatHeader = null;

    for (var i = 0; i < nodes.length; i++) {
      var node = nodes[i];
      var path = node.path || "";

      if (path.indexOf("categories/") === 0) {
        /* Category group */
        var frag = buildCategoryGroup(node, false);
        /* Capture the header element (first child of frag) before append. */
        var header = frag.firstChild;
        treeScroll.appendChild(frag);
        if (firstCatHeader === null) { firstCatHeader = header; }

      } else if (path.indexOf("platforms/") === 0 ||
                 path.indexOf("playlists/") === 0) {
        /* Top-level leaf (+ its nested playlist sub-rows when the platform has any) */
        appendLeafWithKids(treeScroll, node, false);
      }
    }

    /* Auto-expand the first category (visual only — does not load any games) */
    if (firstCatHeader && firstCatHeader.classList.contains("collapsed")) {
      toggleCategoryCollapsed(firstCatHeader);
    }

    /* Wire search now that DOM is populated */
    wireSearch();
  }


  /* ══════════════════════════════════════════════════════════════════════════
     SEARCH FILTER
     ══════════════════════════════════════════════════════════════════════════ */

  function wireSearch() {
    var input = document.querySelector(".lb-search-input");
    if (!input) { return; }

    var _debounce = null;

    input.addEventListener("input", function () {
      clearTimeout(_debounce);
      _debounce = setTimeout(function () {
        applyFilter(input.value.trim());
      }, 50);
    });
  }

  function applyFilter(query) {
    var treeScroll = document.querySelector(".lb-tree-scroll");
    if (!treeScroll) { return; }

    var lq = query.toLowerCase();
    var clearing = lq === "";

    /* Collect all category headers and their sibling children containers. */
    var cats = treeScroll.querySelectorAll(".lb-tree-cat");

    /* First pass: show/hide leaf nodes. */
    var allLeaves = treeScroll.querySelectorAll(".lb-tree-node");
    for (var i = 0; i < allLeaves.length; i++) {
      var leaf = allLeaves[i];
      if (clearing) {
        leaf.style.display = "";
      } else {
        var lbl = (leaf.dataset.label || "").toLowerCase();
        leaf.style.display = lbl.indexOf(lq) !== -1 ? "" : "none";
      }
    }

    /* Second pass: show/hide category headers.
       A category is visible if:
         a) Clearing — always show.
         b) Filter active — its own label matches OR at least one child is visible. */
    for (var j = 0; j < cats.length; j++) {
      var cat = cats[j];
      var nextSib = cat.nextElementSibling;
      /* nextSib is the .lb-tree-children container when it exists. */
      var hasVisibleChild = false;

      if (!clearing && nextSib && nextSib.classList.contains("lb-tree-children")) {
        var childLeaves = nextSib.querySelectorAll(".lb-tree-node");
        for (var k = 0; k < childLeaves.length; k++) {
          if (childLeaves[k].style.display !== "none") {
            hasVisibleChild = true;
            break;
          }
        }
      }

      if (clearing) {
        cat.style.display = "";
        if (nextSib && nextSib.classList.contains("lb-tree-children")) {
          nextSib.style.display = "";
        }
      } else {
        var catLbl = (cat.dataset.label || "").toLowerCase();
        var catMatch = catLbl.indexOf(lq) !== -1 || hasVisibleChild;
        cat.style.display = catMatch ? "" : "none";
        if (nextSib && nextSib.classList.contains("lb-tree-children")) {
          /* Always show the children container itself when the cat is visible
             (individual children are already filtered above). */
          nextSib.style.display = catMatch ? "" : "none";
        }
      }
    }

    /* Third pass: handle top-level leaves that are direct children of
       treeScroll (not inside a .lb-tree-children). Already handled by the
       allLeaves pass above — nothing extra needed. */
  }

  /* ══════════════════════════════════════════════════════════════════════════
     GRID SECTION — loadPlatform / buildLbGrid / selectCell
     Mirrors buildPosterGrid / paintPoster / posterSelect from BigBoxWeb.
     References:
       BigBoxWeb/web/engine/app.js :: buildPosterGrid   ~line 708
       BigBoxWeb/web/engine/app.js :: paintPoster       ~line 779
       BigBoxWeb/web/engine/app.js :: posterSelect      ~line 796
       BigBoxWeb/web/engine/app.js :: TRANSPARENT_1PX   ~line 707
       BigBoxWeb/web/engine/app.js :: _posterLazyLoad   ~line 763
     ══════════════════════════════════════════════════════════════════════════ */

  /* ── loadPlatform ───────────────────────────────────────────────────────── */
  /* Fetches /launchbox/data/<path>/games.json, populates DATA.games, then
     calls buildLbGrid().
     Path is one of:
       "platforms/<slug>"   (leaf platform node)
       "playlists/<slug>"   (leaf playlist node)
     platformName (optional) is stored in currentPlatformName so fillLbPanel
     can populate .lb-panel-plat without re-deriving it.
     (mirrors BigBoxWeb/web/engine/app.js :: loadPlatform ~line 3785 area) */
  function loadPlatform(path, platformName, onReady) {
    if (!path) { return; }
    /* Store for use by fillLbPanel() — mirrors BBW DATA.platform ~line 982 */
    currentPlatformName = (typeof platformName === "string") ? platformName : "";

    /* Defensive: close any open play menu when the platform changes — the
       game context it was built for is no longer current.
       (mirrors BBW closeLbPlayMenu() on platform change) */
    closeLbPlayMenu();

    /* (C7) Reset fanart timer machinery on platform switch so the two-timer
       state starts fresh.  Cancel pending in-flight timers, clear active layer
       tracking, and remove .on / .has-fanart from the DOM.
       (ref: BigBoxWeb/web/engine/app.js :: loadPlatform fanart reset ~line 3785) */
    if (lbHeroFanartTimer)  { clearTimeout(lbHeroFanartTimer);  lbHeroFanartTimer  = null; }
    /* Note: lbFanartOutTimer is intentionally NOT cancelled here (it is
       non-cancellable by design) but we null it so the next selection can
       re-arm it — the previous out-animation will still complete via closure. */
    lbFanartOutTimer = null;
    /* Cancel any pending heavy-detail debounce timer — it targets a game
       from the platform we are leaving and must not fire into the new one. */
    if (lbHeavyTimer) { clearTimeout(lbHeavyTimer); lbHeavyTimer = null; }
    lbHeroFanartActive = 0;
    (function () {
      var bgRoot = document.querySelector(".lb-panel-hero-bg");
      if (bgRoot) {
        var layers = bgRoot.querySelectorAll(".lb-panel-hero-bg-layer");
        layers.forEach(function (l) { l.classList.remove("on"); });
      }
      var hero = document.querySelector(".lb-panel-hero");
      if (hero) { hero.classList.remove("has-fanart"); }
    })();

    /* Track which platform path is currently active so that fav mutations
       (toggleLbFavorite) can update the corresponding cache entry.
       Must be set here, after the play-menu close and fanart reset, so it
       reflects the path that will be rendered by this call.
       (ref: req §2, §4) */
    lbCurrentPlatformPath = path;

    /* ── Games-list cache hit ─────────────────────────────────────────────
       If we already fetched this platform's games.json under the current
       parental state, serve from memory instead of making a network request.
       We use a defensive .slice() copy so in-place mutations of DATA.games
       (e.g. local fav toggles via toggleLbFavorite) do not corrupt the
       cached array.  The trade-off: the cached objects themselves are shared
       references, so shallow mutations (g.fav = …) mutate both the cache
       entry's object AND DATA.games[i] simultaneously.  toggleLbFavorite
       exploits this to keep both in sync with a single assignment; the
       slice() only protects against add/remove/reorder-level mutations.
       (ref: req §2) */
    if (lbGamesCache[path]) {
      console.log("[LBW] lbw games cache hit:", path);

      /* Reset the detail cache on platform switch — same reason as below. */
      lbDetailCache = {};
      lbDetailToken += 1;

      lbApplySortPayload(lbGamesCache[path].payload || { games: lbGamesCache[path].games.slice() });

      /* Render the grid immediately — no fetch needed. */
      renderGames();
      if (typeof onReady === "function") { onReady(); }
      return;
    }

    /* ── Cache miss — proceed with fetch ─────────────────────────────────── */
    console.log("[LBW] lbw games cache miss:", path);

    /* Reset the detail cache on platform switch.  The cache is per-session
       and per-gameId, so games that appear in multiple platforms would get
       stale detail data after a platform change.  A simple clear on every
       loadPlatform call keeps things consistent.
       (Note: lbDetailToken is NOT reset here — the token serves only to
        cancel in-flight fetches, not to invalidate the cache.) */
    lbDetailCache = {};

    /* Cancel any in-flight detail fetch from the previous platform by
       bumping the token.  Any pending callback will discard its response. */
    lbDetailToken += 1;

    var emptyEl  = document.querySelector(".lb-grid-empty");
    var titleEl  = document.querySelector(".lb-grid-title");

    /* Show loading state */
    if (emptyEl) {
      emptyEl.textContent = "Loading…";
      emptyEl.style.display = "";
    }

    /* Capture parental epoch at fetch start.  If reloadLbAfterParental fires
       while the response is in flight, the epoch will have been bumped and we
       must discard the response instead of storing stale data.
       (ref: req §6) */
    var myEpoch = lbParentalEpoch;

    var url = "/launchbox/data/" + path + "/games.json";
    fetch(url)
      .then(function (res) {
        if (!res.ok) {
          throw new Error("HTTP " + res.status + " " + res.statusText);
        }
        return res.json();
      })
      .then(function (json) {
        /* Epoch guard: if parental state flipped since we started this fetch,
           the server filter may have changed.  Drop the response — the user
           will trigger a fresh load by selecting a platform after the tree
           has re-rendered.
           (ref: req §6) */
        if (myEpoch !== lbParentalEpoch) {
          console.log("[LBW] lbw games fetch discarded (epoch changed):", path);
          return;
        }

        /* Accept both { games: [...] } and a bare array, mirroring the BBW
           data shape (BBW uses json.games when the server wraps in an object).
           (shape ref: BigBoxWeb/web/engine/app.js :: DATA.games fill ~line 22) */
        lbApplySortPayload(Array.isArray(json) ? { games: json } : json);

        /* Store a defensive copy in the cache so future visits avoid the
           network round-trip.  The stored objects are shared with DATA.games
           (shallow copy only at the array level), meaning toggleLbFavorite's
           direct mutation of g.fav propagates to the cache automatically.
           (ref: req §2) */
        lbGamesCache[path] = {
          games: lbRawGames.slice(),
          payload: Array.isArray(json) ? { games: lbRawGames.slice() } : json,
          asOf: lbCacheCounter++
        };
        console.log("[LBW] lbw games cache store:", path, "(", DATA.games.length, "games, asOf:", lbGamesCache[path].asOf, ")");

        renderGames();
        if (typeof onReady === "function") { onReady(); }
      })
      .catch(function (err) {
        console.error("[LBW] loadPlatform error:", err);
        DATA.games = [];
        if (emptyEl) {
          emptyEl.textContent = "Failed to load games: " + String(err);
          emptyEl.style.display = "";
        }
      });
  }

  /* ── loadCategory ───────────────────────────────────────────────────────── */
  /* Selecting a category loads the MERGED games of every platform / playlist it
     contains (collected recursively by collectCategoryLeafPaths).  Each leaf's
     games.json is fetched once and reused from lbGamesCache; the merged result
     is itself cached under the category path so re-selecting it is instant and
     so toggleLbFavorite can propagate fav mutations (shared object refs).
     The prologue mirrors loadPlatform's reset (fanart timers, detail cache,
     lbCurrentPlatformPath) — kept inline so loadPlatform stays untouched. */
  function loadCategory(catPath, leafPaths, categoryName) {
    /* Store for fillLbPanel() — same as loadPlatform. */
    currentPlatformName = (typeof categoryName === "string") ? categoryName : "";

    closeLbPlayMenu();

    /* Fanart / heavy-detail timer reset (identical to loadPlatform). */
    if (lbHeroFanartTimer) { clearTimeout(lbHeroFanartTimer); lbHeroFanartTimer = null; }
    lbFanartOutTimer = null;
    if (lbHeavyTimer) { clearTimeout(lbHeavyTimer); lbHeavyTimer = null; }
    lbHeroFanartActive = 0;
    (function () {
      var bgRoot = document.querySelector(".lb-panel-hero-bg");
      if (bgRoot) {
        var layers = bgRoot.querySelectorAll(".lb-panel-hero-bg-layer");
        layers.forEach(function (l) { l.classList.remove("on"); });
      }
      var hero = document.querySelector(".lb-panel-hero");
      if (hero) { hero.classList.remove("has-fanart"); }
    })();

    /* The category path is what toggleLbFavorite will look up in lbGamesCache. */
    lbCurrentPlatformPath = catPath;

    /* Reset detail cache + cancel in-flight detail fetch on selection switch. */
    lbDetailCache = {};
    lbDetailToken += 1;

    var emptyEl = document.querySelector(".lb-grid-empty");

    /* ── Merged-list cache hit ────────────────────────────────────────────── */
    if (catPath && lbGamesCache[catPath]) {
      console.log("[LBW] lbw category cache hit:", catPath);
      lbApplySortPayload({ games: lbGamesCache[catPath].games.slice(), nodeKind: "platform" });
      renderGames();
      return;
    }

    console.log("[LBW] lbw category cache miss:", catPath,
                "(", (leafPaths ? leafPaths.length : 0), "leaves )");

    if (emptyEl) {
      emptyEl.textContent = "Loading…";
      emptyEl.style.display = "";
    }

    /* Empty category — nothing to fetch. */
    if (!leafPaths || leafPaths.length === 0) {
      lbApplySortPayload({ games: [], nodeKind: "platform" });
      renderGames();
      return;
    }

    /* Epoch guard: discard the merged result if parental state flips mid-flight
       (same mechanism as loadPlatform). */
    var myEpoch = lbParentalEpoch;

    /* Fetch every leaf, reusing per-leaf cache entries where present. */
    var jobs = leafPaths.map(function (p) {
      if (lbGamesCache[p]) {
        return Promise.resolve(lbGamesCache[p].games);
      }
      return fetch("/launchbox/data/" + p + "/games.json")
        .then(function (res) {
          if (!res.ok) {
            throw new Error("HTTP " + res.status + " " + res.statusText + " (" + p + ")");
          }
          return res.json();
        })
        .then(function (json) {
          var games = Array.isArray(json) ? json
                    : (Array.isArray(json.games) ? json.games : []);
          if (!Array.isArray(json) && Array.isArray(json.customSorts)) {
            var known = {};
            lbKnownCustomSorts.concat(json.customSorts).forEach(function (n) { known[n] = true; });
            lbKnownCustomSorts = Object.keys(known).sort();
          }
          /* Cache the leaf too, so picking that platform directly is instant.
             Guard the store on the epoch so a stale response can't repopulate
             a cache that reloadLbAfterParental just wiped. */
          if (myEpoch === lbParentalEpoch) {
            lbGamesCache[p] = {
              games: games.slice(),
              payload: Array.isArray(json) ? { games: games.slice() } : json,
              asOf: lbCacheCounter++
            };
          }
          return games;
        })
        .catch(function (err) {
          /* A single failed leaf contributes nothing rather than failing the
             whole category. */
          console.error("[LBW] loadCategory leaf error:", p, err);
          return [];
        });
    });

    Promise.all(jobs)
      .then(function (lists) {
        if (myEpoch !== lbParentalEpoch) {
          console.log("[LBW] lbw category fetch discarded (epoch changed):", catPath);
          return;
        }

        /* Merge + de-dup by id. The shared session Arrange By order is applied below. */
        var seen = Object.create(null);
        var merged = [];
        for (var li = 0; li < lists.length; li++) {
          var list = lists[li];
          for (var gi = 0; gi < list.length; gi++) {
            var g = list[gi];
            var id = (g && g.id != null) ? String(g.id) : null;
            if (id !== null) {
              if (seen[id]) { continue; }
              seen[id] = true;
            }
            merged.push(g);
          }
        }
        lbApplySortPayload({ games: merged, nodeKind: "platform" });

        /* Cache the merged list under the category path (shared object refs with
           the per-leaf caches, so fav toggles stay in sync). */
        if (catPath) {
          lbGamesCache[catPath] = { games: merged.slice(), payload: { games: merged.slice(), nodeKind: "platform" }, asOf: lbCacheCounter++ };
        }
        console.log("[LBW] lbw category merged:", catPath, "(", merged.length, "games )");

        renderGames();
      })
      .catch(function (err) {
        console.error("[LBW] loadCategory error:", err);
        DATA.games = [];
        if (emptyEl) {
          emptyEl.textContent = "Failed to load games: " + String(err);
          emptyEl.style.display = "";
        }
      });
  }

  /* ══════════════════════════════════════════════════════════════════════════
     LIST VIEW — columnar game list (View ▸ List)
     ----------------------------------------------------------------------------
     Row-per-game alternative to the poster grid. Columns are reorderable (drag
     headers), toggleable (right-click header → chooser) and sortable (click
     header). Order / visibility / sort persist via window.LBW.cfg (cookie
     lbw_cfg). Inspired by LiteBox's GameListView (LbApiHost): same column set +
     interactions, mapped to the launchbox light game shape. Selection + the
     right panel reuse the grid pipeline (selectCell / fillLbPanel) so both views
     share ONE selection model (module-scope posterSel). Rows carry data-i = the
     original DATA.games index, so sorting only reorders the DISPLAY (DATA.games
     is never mutated) and type-ahead / deep-link stay consistent.
     ══════════════════════════════════════════════════════════════════════════ */

  /* Rating cell text — user rating (g.ur) preferred over community (g.r). */
  function lbColRating(g) {
    var v = (g.ur != null && g.ur !== "") ? g.ur : (g.r != null ? g.r : null);
    if (v == null || v === "") { return ""; }
    return String(v) + " ★";
  }
  function lbRatingSortVal(g) {
    var v = (g.ur != null && g.ur !== "") ? g.ur : g.r;
    var n = parseFloat(v);
    return isNaN(n) ? -1 : n;
  }

  /* CompareName — the loose normalized title used as the Title-column sort key.
     Faithful port of Utility/Normalizer.PerformSanitize (the pass that fills the
     Extended DB "CompareName" column — NOT the strict NormalizeCompareName /
     CompareNameFallback, which strips spaces+diacritics):
       1. strip (), [], {} annotations — whitespace-aware: an annotation glued
          between two non-spaces fuses (""), otherwise becomes a single space;
       2. char blacklist — " : ? \ / - & ! ,  and control chars → space ;
          ' and . → removed ; (* | < > ~ ` pass through, exactly as the C#);
       3. compress space runs + trim;
       4. Roman numerals II..VIII (whole space-delimited tokens, UPPERCASE only)
          → 2..8 ;
       5. drop English articles a / an / and / the (case-insensitive) ;
       6. compress + trim + UPPERCASE.
     e.g. "The Legend of Zelda" → "LEGEND OF ZELDA" ; "Final Fantasy VII" →
     "FINAL FANTASY 7" ; "Spider-Man 2" → "SPIDER MAN 2". (Diacritics are kept:
     "Pokémon" → "POKÉMON", matching PerformSanitize.) */
  function lbBracketRepl(m, offset, str) {
    var before = offset > 0 ? str.charAt(offset - 1) : " ";
    var after  = (offset + m.length < str.length) ? str.charAt(offset + m.length) : " ";
    return (!/\s/.test(before) && !/\s/.test(after)) ? "" : " ";
  }
  var LB_ROMAN = { "II": "2", "III": "3", "IV": "4", "VIII": "8", "VII": "7", "VI": "6", "V": "5" };
  function lbCompareName(name) {
    if (name == null) { return ""; }
    var s = String(name);

    /* 1. bracket annotations (applied in sequence, like the C#) */
    s = s.replace(/\([^)]*\)/g, lbBracketRepl);
    s = s.replace(/\[[^\]]*\]/g, lbBracketRepl);
    s = s.replace(/\{[^}]*\}/g, lbBracketRepl);

    /* 2. char blacklist (first match wins: to-space before to-void, so a
          double-quote — present in both C# sets — becomes a space) */
    var out = "";
    for (var i = 0; i < s.length; i++) {
      var c = s.charAt(i), code = s.charCodeAt(i);
      if (code <= 0x1f || c === '"' || c === ':' || c === '?' || c === '\\' ||
          c === '/' || c === '-' || c === '&' || c === '!' || c === ',') { out += " "; }
      else if (c === "'" || c === ".") { /* to void — drop */ }
      else { out += c; }
    }
    s = out.replace(/ {2,}/g, " ").trim();   /* 3. compress + trim */

    /* 4 + 5. Roman numerals (case-sensitive whole tokens) + article drop */
    var toks = s.length ? s.split(" ") : [];
    var res = [];
    for (var t = 0; t < toks.length; t++) {
      var tok = toks[t];
      if (tok === "") { continue; }
      if (LB_ROMAN.hasOwnProperty(tok)) { res.push(LB_ROMAN[tok]); continue; }
      var lt = tok.toLowerCase();
      if (lt === "a" || lt === "an" || lt === "and" || lt === "the") { continue; }
      res.push(tok);
    }

    /* 6. compress + trim + uppercase */
    return res.join(" ").replace(/ {2,}/g, " ").trim().toUpperCase();
  }

  /* Column catalog. width = default px ; align = cell text-align ; get(g) =
     display text ; sortGet(g) (optional) = comparable value for sorting ;
     cls (optional) = extra class on header + cells. */
  var LB_COL_DEFS = {
    title:      { label: "Title",       align: "left",   width: 320, get: function (g) { return g.t || ""; },
                  sortGet: function (g) {
                    var key = (g.cn != null) ? String(g.cn) : (lbCompareName(g.t) || String(g.t || ""));
                    return key.toUpperCase();
                  } },
    dev:        { label: "Developer",   align: "left",   width: 160, get: function (g) { return g.dev || ""; } },
    publisher:  { label: "Publisher",   align: "left",   width: 160, get: function (g) { return g.pub || ""; } },
    genre:      { label: "Genre",       align: "left",   width: 150, get: function (g) { return g.g   || ""; } },
    year:       { label: "Year",        align: "right",  width: 64,  get: function (g) { return g.y   || ""; },
                  sortGet: function (g) { var n = parseInt(g.y, 10); return isNaN(n) ? -1 : n; } },
    platform:   { label: "Platform",    align: "left",   width: 150, get: function (g) { return g.platform || currentPlatformName || ""; } },
    esrb:       { label: "ESRB",        align: "left",   width: 72,  get: function (g) { return g.esrb || ""; } },
    rating:     { label: "Rating",      align: "right",  width: 80,  get: lbColRating, sortGet: lbRatingSortVal, cls: "lb-lc-rating" },
    votes:      { label: "Votes",       align: "right",  width: 64,  get: function (g) { return (g.votes != null && g.votes !== "") ? String(g.votes) : ""; },
                  sortGet: function (g) { var n = parseInt(g.votes, 10); return isNaN(n) ? -1 : n; } },
    lastplayed: { label: "Last Played", align: "right",  width: 112, get: function (g) { return g.lp ? formatDate(g.lp) : ""; },
                  sortGet: function (g) { return g.lp || 0; } },
    playtime:   { label: "Play Time",   align: "right",  width: 92,  get: function (g) { return (g._playtime != null && g._playtime !== "") ? String(g._playtime) : ""; },
                  sortGet: function (g) { return g._playTimeSec || 0; } },
    fav:        { label: "Fav",         align: "center", width: 46,  get: function (g) { return g.fav ? "★" : ""; }, cls: "lb-lc-fav" }
  };

  /* Fallback column order when config is missing (also defines the canonical
     order; unknown/new keys are appended at runtime by lbVisibleColumns). */
  var LB_COL_ORDER = ["title", "dev", "genre", "year", "fav", "publisher", "rating", "votes", "esrb", "platform", "lastplayed", "playtime"];
  var LB_COL_HIDDEN_DEFAULT = { publisher: true, rating: true, votes: true, esrb: true, platform: true, lastplayed: true, playtime: true };

  function lbCfgGet(path, fallback) {
    try { var v = window.LBW.cfg.get(path); return (v == null) ? fallback : v; }
    catch (e) { return fallback; }
  }
  function lbCfgSet(path, v) {
    try { window.LBW.cfg.set(path, v); } catch (e) {}
  }

  /* Current centre-pane mode: "image" | "list". */
  function lbViewMode() {
    return (lbCfgGet("view.mode", "image") === "list") ? "list" : "image";
  }

  /* Ordered list of visible column keys (config order minus hidden). Always
     returns at least ["title"] so the list can never render zero columns. */
  function lbVisibleColumns() {
    var order  = (lbCfgGet("listView.order", null) || LB_COL_ORDER).slice();
    var hidden = lbCfgGet("listView.hidden", null) || LB_COL_HIDDEN_DEFAULT;
    for (var k in LB_COL_DEFS) {
      if (LB_COL_DEFS.hasOwnProperty(k) && order.indexOf(k) < 0) { order.push(k); }
    }
    var out = [];
    for (var i = 0; i < order.length; i++) {
      if (LB_COL_DEFS[order[i]] && !hidden[order[i]]) { out.push(order[i]); }
    }
    if (!out.length) { out.push("title"); }
    return out;
  }

  var LB_COL_MIN_W = 48;   /* px floor for any column */

  /* listView.widths : map key -> width as a PERCENT of the list viewport, set
     by resizing. Stored as % (not px) so columns keep their proportions when
     the window resizes (the user's requirement). */
  function lbColWidths() { return lbCfgGet("listView.widths", null) || {}; }

  /* grid-template-columns string built from the visible columns. Per column:
       • overridePx[key] present → "<px>px"            (live during a drag-resize)
       • saved width %           → "minmax(MINpx, N%)" (responsive, never collapses)
       • title (unsized)         → "minmax(220px, 1fr)" (flexes to fill)
       • otherwise               → "<defaultpx>px". */
  function lbColTemplate(cols, overridePx) {
    overridePx = overridePx || {};
    var widths = lbColWidths();
    var parts = [];
    for (var i = 0; i < cols.length; i++) {
      var key = cols[i];
      if (overridePx[key] != null) {
        parts.push(Math.max(LB_COL_MIN_W, overridePx[key] | 0) + "px");
      } else if (widths[key] != null && !isNaN(widths[key])) {
        parts.push("minmax(" + LB_COL_MIN_W + "px, " + widths[key] + "%)");
      } else if (key === "title") {
        parts.push("minmax(220px, 1fr)");
      } else {
        parts.push(LB_COL_DEFS[key].width + "px");
      }
    }
    return parts.join(" ");
  }

  /* DATA.games is already the one shared Arrange By order used by list and poster.
     The list only needs the corresponding identity indices for selection handling. */
  function lbListDisplayOrder() {
    var idx = [];
    for (var i = 0; i < DATA.games.length; i++) { idx.push(i); }
    return idx;
  }

  function lbSortKeyForColumn(key) {
    return {
      dev: "developer", fav: "favorite", year: "releaseyear", esrb: "rating",
      rating: "starrating", lastplayed: "lastplayed", playtime: "playtime"
    }[key] || key;
  }

  /* ── buildLbList — render the header row + one row per game ───────────────── */
  function buildLbList() {
    var host = document.querySelector(".lb-list-scroll");
    if (!host) { return; }

    posterSel = -1;
    host.innerHTML = "";

    var cols = lbVisibleColumns();
    var tmpl = lbColTemplate(cols);
    var sort = lbActiveSort;

    /* One shared CSS var (--lb-tmpl on the scroll host) drives the column
       template for the header AND every row, so a live resize updates a single
       property instead of writing grid-template-columns on N rows. */
    host.style.setProperty("--lb-tmpl", tmpl);

    /* Header */
    var head = document.createElement("div");
    head.className = "lb-list-head";
    cols.forEach(function (key) {
      var def = LB_COL_DEFS[key];
      var hc = document.createElement("div");
      hc.className = "lb-lh-cell" + (def.cls ? " " + def.cls : "");
      hc.style.textAlign = def.align;
      hc.dataset.key = key;
      hc.draggable = true;
      hc.title = def.label + "  —  click: sort · drag: reorder · drag edge: resize · right-click: columns";
      var lbl = document.createElement("span");
      lbl.className = "lb-lh-label";
      lbl.textContent = def.label;
      hc.appendChild(lbl);
      if (sort.key === lbSortKeyForColumn(key)) {
        var sind = document.createElement("span");
        sind.className = "lb-lh-sort";
        sind.textContent = (sort.dir === "desc") ? " ▼" : " ▲";
        hc.appendChild(sind);
      }
      /* Right-edge drag handle to resize this column (persists width as %). */
      var rz = document.createElement("div");
      rz.className = "lb-lh-resize";
      rz.addEventListener("mousedown", function (e) { lbResizeStart(e, key, hc); });
      hc.appendChild(rz);
      head.appendChild(hc);
    });
    host.appendChild(head);
    lbWireListHeader(head);

    /* Body */
    var order = lbListDisplayOrder();
    var body = document.createElement("div");
    body.className = "lb-list-body";
    order.forEach(function (gi) {
      var g = DATA.games[gi];
      if (!g) { return; }
      var row = document.createElement("div");
      row.className = "lb-row";
      row.dataset.i = String(gi);
      cols.forEach(function (key) {
        var def = LB_COL_DEFS[key];
        var cell = document.createElement("div");
        cell.className = "lb-lc" + (def.cls ? " " + def.cls : "");
        cell.style.textAlign = def.align;
        var txt = def.get(g);
        cell.textContent = (txt == null) ? "" : txt;
        if (key === "rating" && txt) {
          cell.classList.add((g.ur != null && g.ur !== "") ? "lb-lc-rating-user" : "lb-lc-rating-comm");
        }
        row.appendChild(cell);
      });
      row.addEventListener("click", function () { selectCell(gi); });
      row.addEventListener("dblclick", function () {
        selectCell(gi);
        var pb = document.getElementById("lb-panel-play");
        if (pb) { pb.click(); }
      });
      body.appendChild(row);
    });
    host.appendChild(body);

    /* Empty-state placeholder (shared with the grid view). */
    var emptyEl = document.querySelector(".lb-grid-empty");
    if (emptyEl) {
      if (DATA.games.length === 0) { emptyEl.textContent = "No games found"; emptyEl.style.display = ""; }
      else { emptyEl.style.display = "none"; }
    }
    var countEl = document.querySelector(".lb-grid-count");
    if (countEl && DATA.games.length > 0) { countEl.textContent = DATA.games.length + " games"; }
  }

  /* Approx number of visible rows in the list scroll (for PageUp/PageDown). */
  function lbListVisibleRows() {
    var sc = document.querySelector(".lb-list-scroll");
    var row = sc ? sc.querySelector(".lb-row") : null;
    if (!sc || !row) { return 12; }
    var rh = row.offsetHeight || 24;
    return Math.max(1, Math.floor((sc.clientHeight - 30) / rh));
  }

  /* ── Column resize (drag the right edge of a header cell) ─────────────────
     Live feedback updates ONLY the shared --lb-tmpl CSS var (one property → the
     header and every row realign, no per-row DOM writes), using a px width
     during the drag. On release the px width is converted to a PERCENT of the
     list viewport and saved to listView.widths (cookie lbw_cfg) so the column
     keeps its proportion across window sizes. */
  var _lbResize = null;              /* { key, startX, startW, cols, lastW } */
  var _lbSuppressHeaderClick = false;

  function lbResizeStart(e, key, hc) {
    e.preventDefault();
    e.stopPropagation();
    _lbResize = {
      key: key,
      startX: e.clientX,
      startW: (hc && hc.offsetWidth) ? hc.offsetWidth : 120,   /* current rendered px */
      cols: lbVisibleColumns(),
      lastW: null
    };
    document.addEventListener("mousemove", lbResizeMove, true);
    document.addEventListener("mouseup",   lbResizeEnd,  true);
    document.body.classList.add("lb-col-resizing");
  }
  function lbResizeMove(e) {
    if (!_lbResize) { return; }
    var w = Math.max(LB_COL_MIN_W, (_lbResize.startW + (e.clientX - _lbResize.startX)) | 0);
    _lbResize.lastW = w;
    var ov = {}; ov[_lbResize.key] = w;
    var host = document.querySelector(".lb-list-scroll");
    if (host) { host.style.setProperty("--lb-tmpl", lbColTemplate(_lbResize.cols, ov)); }
  }
  function lbResizeEnd() {
    document.removeEventListener("mousemove", lbResizeMove, true);
    document.removeEventListener("mouseup",   lbResizeEnd,  true);
    document.body.classList.remove("lb-col-resizing");
    if (_lbResize && _lbResize.lastW != null) {
      var host = document.querySelector(".lb-list-scroll");
      var hostW = host ? host.clientWidth : 0;
      if (hostW > 0) {
        /* px → percent of the list viewport, clamped to a sane band. */
        var pct = Math.round((_lbResize.lastW / hostW) * 1000) / 10;
        pct = Math.max(2, Math.min(95, pct));
        var widths = {}, cur = lbColWidths();
        for (var k in cur) { if (cur.hasOwnProperty(k)) { widths[k] = cur[k]; } }
        widths[_lbResize.key] = pct;
        lbCfgSet("listView.widths", widths);   /* persisted to cookie lbw_cfg */
      }
      /* Swallow the click the browser fires right after this mouseup so the
         resize doesn't also trigger a sort. Cleared next tick so a later
         genuine header click still sorts. */
      _lbSuppressHeaderClick = true;
      setTimeout(function () { _lbSuppressHeaderClick = false; }, 0);
    }
    _lbResize = null;
  }

  /* ── Header interactions: sort (click) · reorder (drag) · chooser (rt-click) ─ */
  var _lbDragKey = null;
  function lbWireListHeader(head) {
    head.addEventListener("click", function (e) {
      if (_lbSuppressHeaderClick) { return; }
      var hc = e.target.closest ? e.target.closest(".lb-lh-cell") : null;
      if (!hc) { return; }
      var key = hc.dataset.key;
      lbChooseArrange(lbSortKeyForColumn(key));
    });

    head.addEventListener("dragstart", function (e) {
      if (_lbResize) { return; }   /* a resize drag must not start a reorder */
      var hc = e.target.closest ? e.target.closest(".lb-lh-cell") : null;
      if (!hc) { return; }
      _lbDragKey = hc.dataset.key;
      hc.classList.add("lb-lh-drag");
      try { e.dataTransfer.effectAllowed = "move"; e.dataTransfer.setData("text/plain", _lbDragKey); } catch (_) {}
    });
    head.addEventListener("dragover", function (e) {
      if (!_lbDragKey) { return; }
      e.preventDefault();
      try { e.dataTransfer.dropEffect = "move"; } catch (_) {}
      var hc = e.target.closest ? e.target.closest(".lb-lh-cell") : null;
      head.querySelectorAll(".lb-lh-over").forEach(function (n) { n.classList.remove("lb-lh-over"); });
      if (hc && hc.dataset.key !== _lbDragKey) { hc.classList.add("lb-lh-over"); }
    });
    head.addEventListener("drop", function (e) {
      if (!_lbDragKey) { return; }
      e.preventDefault();
      var hc = e.target.closest ? e.target.closest(".lb-lh-cell") : null;
      if (hc && hc.dataset.key && hc.dataset.key !== _lbDragKey) { lbReorderColumn(_lbDragKey, hc.dataset.key); }
      _lbDragKey = null;
    });
    head.addEventListener("dragend", function () {
      _lbDragKey = null;
      head.querySelectorAll(".lb-lh-drag, .lb-lh-over").forEach(function (n) {
        n.classList.remove("lb-lh-drag"); n.classList.remove("lb-lh-over");
      });
    });

    head.addEventListener("contextmenu", function (e) {
      e.preventDefault();
      lbOpenColMenu(e.clientX, e.clientY);
    });
  }

  /* Move column `key` to just before `beforeKey` in the saved order. */
  function lbReorderColumn(key, beforeKey) {
    var order = (lbCfgGet("listView.order", null) || LB_COL_ORDER).slice();
    for (var k in LB_COL_DEFS) { if (order.indexOf(k) < 0) { order.push(k); } }
    var from = order.indexOf(key);
    if (from < 0) { return; }
    order.splice(from, 1);
    var to = order.indexOf(beforeKey);
    if (to < 0) { to = order.length; }
    order.splice(to, 0, key);
    lbCfgSet("listView.order", order);
    lbRebuildListKeepSel();
  }

  /* ── Column chooser (right-click header) ─────────────────────────────────── */
  function lbCloseColMenu() {
    var m = document.getElementById("lb-colmenu");
    if (m && m.parentNode) { m.parentNode.removeChild(m); }
    document.removeEventListener("click", lbColMenuDismiss, true);
    document.removeEventListener("keydown", lbColMenuKey, true);
  }
  function lbColMenuDismiss(e) {
    var m = document.getElementById("lb-colmenu");
    if (m && m.contains(e.target)) { return; }
    lbCloseColMenu();
  }
  function lbColMenuKey(e) { if (e.key === "Escape") { lbCloseColMenu(); } }
  function lbOpenColMenu(x, y) {
    lbCloseColMenu();
    var order = (lbCfgGet("listView.order", null) || LB_COL_ORDER).slice();
    for (var k in LB_COL_DEFS) { if (order.indexOf(k) < 0) { order.push(k); } }
    var hidden = lbCfgGet("listView.hidden", null) || LB_COL_HIDDEN_DEFAULT;

    var menu = document.createElement("div");
    menu.id = "lb-colmenu";
    menu.className = "lb-colmenu";
    order.forEach(function (key) {
      var def = LB_COL_DEFS[key];
      if (!def) { return; }
      var item = document.createElement("div");
      item.className = "lb-colmenu-item" + (hidden[key] ? "" : " checked");
      item.textContent = def.label;
      item.addEventListener("click", function (e) {
        e.stopPropagation();
        lbToggleColumn(key);
        var nowHidden = lbCfgGet("listView.hidden", null) || {};
        item.className = "lb-colmenu-item" + (nowHidden[key] ? "" : " checked");
      });
      menu.appendChild(item);
    });
    document.body.appendChild(menu);
    var w = menu.offsetWidth, h = menu.offsetHeight;
    menu.style.left = Math.max(4, Math.min(x, window.innerWidth  - w - 4)) + "px";
    menu.style.top  = Math.max(4, Math.min(y, window.innerHeight - h - 4)) + "px";
    setTimeout(function () {
      document.addEventListener("click", lbColMenuDismiss, true);
      document.addEventListener("keydown", lbColMenuKey, true);
    }, 0);
  }
  function lbToggleColumn(key) {
    var cur = lbCfgGet("listView.hidden", null) || LB_COL_HIDDEN_DEFAULT;
    var hidden = {};
    for (var k in cur) { if (cur.hasOwnProperty(k)) { hidden[k] = cur[k]; } }
    if (hidden[key]) { delete hidden[key]; }
    else {
      if (lbVisibleColumns().length <= 1) { return; }   /* never hide the last column */
      hidden[key] = true;
    }
    lbCfgSet("listView.hidden", hidden);
    lbRebuildListKeepSel();
  }

  /* Rebuild the list after a column/sort change, re-selecting the same game. */
  function lbRebuildListKeepSel() {
    var selId = (posterSel >= 0 && DATA.games[posterSel]) ? DATA.games[posterSel].id : null;
    buildLbList();
    if (selId != null) {
      for (var i = 0; i < DATA.games.length; i++) {
        if (DATA.games[i] && DATA.games[i].id === selId) { selectCell(i); break; }
      }
    }
  }

  /* ── Selection painters (view-aware; called by selectCell) ───────────────── */
  function lbPaintGridSelection(i, instant) {
    var scroll = document.querySelector(".lb-grid-scroll");
    if (!scroll) { return; }
    var prev = scroll.querySelector(".lb-cell.selected");
    if (prev) { prev.classList.remove("selected"); }
    var cells = scroll.querySelectorAll(".lb-cell");
    if (cells[i]) {
      cells[i].classList.add("selected");
      cells[i].scrollIntoView({ block: "nearest", behavior: instant ? "auto" : "smooth" });
    }
  }
  function lbPaintListSelection(i, instant) {
    var scroll = document.querySelector(".lb-list-scroll");
    if (!scroll) { return; }
    var prev = scroll.querySelector(".lb-row.selected");
    if (prev) { prev.classList.remove("selected"); }
    var row = scroll.querySelector('.lb-row[data-i="' + i + '"]');
    if (row) {
      row.classList.add("selected");
      row.scrollIntoView({ block: "nearest", behavior: instant ? "auto" : "smooth" });
    }
  }

  /* ── renderGames — view dispatcher (replaces direct buildLbGrid() calls) ──── */
  function renderGames() {
    if (lbViewMode() === "list") { buildLbList(); }
    else { buildLbGrid(); }
  }

  /* ── setLbViewMode — switch Image/List, persist, rebuild, keep selection ──── */
  function setLbViewMode(mode) {
    mode = (mode === "list") ? "list" : "image";
    var keepSel = posterSel;
    document.body.classList.toggle("lb-view-list",  mode === "list");
    document.body.classList.toggle("lb-view-image", mode !== "list");
    lbCfgSet("view.mode", mode);
    lbReflectViewMenu(mode);
    if (mode === "list") { buildLbList(); } else { buildLbGrid(); }
    if (keepSel >= 0 && keepSel < DATA.games.length) { selectCell(keepSel); }
  }

  /* Tick the active item in the VIEW dropdown. */
  function lbReflectViewMenu(mode) {
    var img = document.getElementById("lb-view-image");
    var lst = document.getElementById("lb-view-list");
    if (img) { img.classList.toggle("checked", mode !== "list"); }
    if (lst) { lst.classList.toggle("checked", mode === "list"); }
  }

  /* Wire the VIEW dropdown (mirrors setupLbMenuDropdown for the MENU button). */
  function setupLbViewMenu() {
    var btn = document.getElementById("lb-view-btn");
    var dd  = document.getElementById("lb-view-dropdown");
    if (!btn || !dd) { return; }

    function open() {
      var mdd = document.getElementById("lb-menu-dropdown");
      if (mdd) { mdd.classList.remove("open"); }   /* don't leave MENU open */
      dd.classList.add("open"); btn.setAttribute("aria-expanded", "true");
    }
    function close() { dd.classList.remove("open"); btn.setAttribute("aria-expanded", "false"); }

    btn.addEventListener("click", function (e) {
      e.stopPropagation();
      if (dd.classList.contains("open")) { close(); } else { open(); }
    });
    dd.addEventListener("click", function (e) { e.stopPropagation(); });
    document.addEventListener("click", function () { close(); });
    document.addEventListener("keydown", function (e) { if (e.key === "Escape") { close(); } });

    var img = document.getElementById("lb-view-image");
    var lst = document.getElementById("lb-view-list");
    if (img) { img.addEventListener("click", function () { close(); setLbViewMode("image"); }); }
    if (lst) { lst.addEventListener("click", function () { close(); setLbViewMode("list"); }); }
  }

  /* Apply the persisted view mode to <body> + VIEW menu at boot. Called from
     DOMContentLoaded before the first game render. */
  function initLbViewMode() {
    var mode = lbViewMode();
    document.body.classList.toggle("lb-view-list",  mode === "list");
    document.body.classList.toggle("lb-view-image", mode !== "list");
    lbReflectViewMenu(mode);
    setupLbViewMenu();
  }

  function lbApplySortPayload(payload) {
    payload = payload || {};
    var names = {};
    lbKnownCustomSorts.forEach(function (n) { names[n] = true; });
    (payload.customSorts || []).forEach(function (n) { names[n] = true; });
    (payload.games || []).forEach(function (g) {
      var cf = g && g.cf || {};
      for (var n in cf) if (Object.prototype.hasOwnProperty.call(cf, n)) names[n] = true;
    });
    lbKnownCustomSorts = Object.keys(names).sort();
    payload.customSorts = lbKnownCustomSorts.slice();
    lbSortPayload = payload;
    lbRawGames = Array.isArray(payload.games) ? payload.games.slice() : [];
    lbActiveSort = window.LBGameSort.stateForPayload(payload, false, lbGlobalSort);
    DATA.games = window.LBGameSort.sorted(lbRawGames, lbActiveSort);
    lbReflectArrange();
  }

  function lbChooseArrange(key) {
    var next = window.LBGameSort.select(lbActiveSort, key, lbGlobalSort);
    lbActiveSort = next.active;
    lbGlobalSort = next.global;
    var selectedId = posterSel >= 0 && DATA.games[posterSel] ? DATA.games[posterSel].id : null;
    DATA.games = window.LBGameSort.sorted(lbRawGames, lbActiveSort);
    renderGames();
    if (selectedId != null) {
      for (var i = 0; i < DATA.games.length; i++) {
        if (String(DATA.games[i].id) === String(selectedId)) { selectCell(i); break; }
      }
    }
    lbReflectArrange();
  }

  function lbReflectArrange() {
    var btn = document.getElementById("lb-arrange-btn");
    if (btn) btn.textContent = "ARRANGE BY: " + window.LBGameSort.label(lbActiveSort.key).toUpperCase() +
      (lbActiveSort.dir === "desc" ? " ▼" : " ▲");
  }

  function lbBuildArrangeMenu() {
    var dd = document.getElementById("lb-arrange-dropdown");
    if (!dd) return;
    dd.innerHTML = "";
    var opts = window.LBGameSort.options(lbSortPayload);
    if (!opts.some(function (o) { return o.key === lbActiveSort.key; })) {
      opts.unshift({ key: lbActiveSort.key, label: window.LBGameSort.label(lbActiveSort.key), contextual: true });
    }
    var insertedCustomSeparator = false;
    opts.forEach(function (opt, index) {
      if ((index > 0 && opts[index - 1].contextual)
          || (opt.custom && !insertedCustomSeparator)
          || (opt.key !== "manual" && index > 0 && opts[index - 1].key === "manual")) {
        var sep = document.createElement("li");
        sep.className = "lb-menu-sep"; sep.setAttribute("role", "separator");
        dd.appendChild(sep);
        if (opt.custom) insertedCustomSeparator = true;
      }
      var li = document.createElement("li");
      li.className = "lb-menu-item" + (lbActiveSort.key === opt.key ? " checked" : "");
      li.setAttribute("role", "menuitemradio");
      li.textContent = opt.label + (lbActiveSort.key === opt.key ? (lbActiveSort.dir === "desc" ? "  ▼" : "  ▲") : "");
      li.addEventListener("click", function () { lbChooseArrange(opt.key); dd.classList.remove("open"); });
      dd.appendChild(li);
    });
  }

  function setupLbArrangeMenu() {
    var btn = document.getElementById("lb-arrange-btn");
    var dd = document.getElementById("lb-arrange-dropdown");
    if (!btn || !dd) return;
    btn.addEventListener("click", function (e) {
      e.stopPropagation();
      if (dd.classList.contains("open")) dd.classList.remove("open");
      else { lbBuildArrangeMenu(); dd.classList.add("open"); }
    });
    dd.addEventListener("click", function (e) { e.stopPropagation(); });
    document.addEventListener("click", function () { dd.classList.remove("open"); });
    document.addEventListener("keydown", function (e) { if (e.key === "Escape") dd.classList.remove("open"); });
    lbReflectArrange();
  }

  /* ── buildLbGrid — poster grid (image view) ───────────────────────────────
     Builds the poster-style grid from DATA.games. Direct port of
     buildPosterGrid() in BigBoxWeb/web/engine/app.js ~line 708. Container is
     .lb-grid-scroll, cell class .lb-cell, image class .rc-img lazy; LazyLoad
     instance kept in window._lbLazyLoad. */
  function buildLbGrid() {
    var scroll = document.querySelector(".lb-grid-scroll");
    if (!scroll) { return; }

    var emptyEl = scroll.parentElement
                  ? scroll.parentElement.querySelector(".lb-grid-empty")
                  : null;
    /* .lb-grid-empty lives as a sibling of .lb-grid-scroll inside #lb-grid */
    if (!emptyEl) { emptyEl = document.querySelector(".lb-grid-empty"); }

    /* ── Clear previous content and reset scroll position ─────────────────
       (copied from BigBoxWeb/web/engine/app.js :: buildPosterGrid ~line 711) */
    scroll.innerHTML = "";
    scroll.scrollTop = 0;

    /* ── Destroy previous LazyLoad instance ───────────────────────────────
       (copied from BigBoxWeb/web/engine/app.js :: buildPosterGrid ~line 763) */
    if (window._lbLazyLoad) {
      try { window._lbLazyLoad.destroy(); } catch (_) {}
      window._lbLazyLoad = null;
    }

    /* ── Reset selection ──────────────────────────────────────────────────── */
    posterSel = -1;

    /* ── Build cells ──────────────────────────────────────────────────────── */
    /* Each cell mirrors a .poster-cell in BBW buildPosterGrid():
         <div class="lb-cell [empty]" data-i="i">
           <img class="rc-img lazy" src="TRANSPARENT_1PX" [data-src="thumb"]
                decoding="async" alt="">
           <div class="lb-title">…</div>
           <div class="lb-dev">…</div>
         </div>
       (copied from BigBoxWeb/web/engine/app.js :: buildPosterGrid ~line 724) */
    DATA.games.forEach(function (g, i) {
      var cell = document.createElement("div");
      /* .empty when no thumb — phantom grey rectangle, mirrors BBW
         (copied from BigBoxWeb/web/engine/app.js :: buildPosterGrid ~line 731) */
      cell.className = "lb-cell" + (g.thumb ? "" : " empty");
      cell.dataset.i = String(i);

      var img = document.createElement("img");
      /* Empty cells: no data-src, no lazy class — LazyLoad has nothing to do.
         The grey-gradient placeholder is painted purely by CSS on .lb-cell.empty .rc-img.
         The <img> element still exists so the CSS rule has a target to paint.
         Non-empty cells get the "lazy" class so vanilla-lazyload swaps in data-src.
         (K: fix from style-pass-1 — previously all cells got "lazy") */
      img.className = g.thumb ? "rc-img lazy" : "rc-img";
      img.alt = "";
      /* GIF placeholder prevents UA border on empty src
         (copied from BigBoxWeb/web/engine/app.js :: buildPosterGrid ~line 736) */
      img.src = TRANSPARENT_1PX;
      if (g.thumb) { img.dataset.src = g.thumb; }
      img.decoding = "async";

      var titleDiv = document.createElement("div");
      titleDiv.className = "lb-title";
      titleDiv.textContent = g.t || "";

      var devDiv = document.createElement("div");
      devDiv.className = "lb-dev";
      devDiv.textContent = g.dev || "";

      cell.appendChild(img);
      cell.appendChild(titleDiv);
      cell.appendChild(devDiv);

      /* ── Click handler: first click selects; second click on same cell
         is a placeholder for workflow E (game detail navigation).
         (pattern from BigBoxWeb/web/engine/app.js :: buildPosterGrid ~line 748) */
      cell.addEventListener("click", function () {
        if (posterSel === i) {
          /* Second click on already-selected: workflow E placeholder */
          console.log("[LBW] game open →", g);
        } else {
          selectCell(i);
        }
      });

      scroll.appendChild(cell);
    });

    /* ── Re-arm LazyLoad scoped to the grid scroll container ──────────────
       (copied from BigBoxWeb/web/engine/app.js :: buildPosterGrid ~line 767) */
    window._lbLazyLoad = new LazyLoad({
      container:         scroll,
      elements_selector: ".lb-cell .rc-img.lazy",
      /* Local source: let loads finish even if cell leaves viewport.
         (copied from BigBoxWeb/web/engine/app.js :: buildPosterGrid ~line 773) */
      cancel_on_exit: false,
      use_native:     false,
      /* Pre-load aggressively; local HTTP is fast.
         (copied from BigBoxWeb/web/engine/app.js :: buildPosterGrid ~line 776) */
      threshold: 1000
    });

    /* ── Recompute column count for keyboard navigation ──────────────────
       Must run after the grid has been laid out in the DOM.
       (mirrors BigBoxWeb/web/engine/app.js :: computePosterCols() call at
        the end of buildPosterGrid → window.BBW.onResize ~line 5375) */
    computeLbPosterCols();

    /* ── Reaffirm grid header count ────────────────────────────────────────
       selectLeaf already set title; we confirm count after actual game load. */
    var countEl = document.querySelector(".lb-grid-count");
    if (countEl && DATA.games.length > 0) {
      countEl.textContent = DATA.games.length + " games";
    }

    /* ── Hide the empty-state placeholder ─────────────────────────────────── */
    if (emptyEl) {
      if (DATA.games.length === 0) {
        emptyEl.textContent = "No games found";
        emptyEl.style.display = "";
      } else {
        emptyEl.style.display = "none";
      }
    }
  }

  /* ── selectCell ─────────────────────────────────────────────────────────── */
  /* Selects a grid cell by index: removes .selected from the previous cell,
     adds it to the clicked one, updates posterSel, then populates the right
     panel via fillLbPanel().
     instant (optional bool): pass true when called from a key auto-repeat
     (e.repeat === true) so scrollIntoView uses behavior:'auto' instead of
     'smooth'.  behavior:'smooth' during auto-repeat causes the browser to
     queue overlapping scroll animations that compete with the class-swap
     repaint, which can visually suppress the white border.
     (mirrors BigBoxWeb/web/engine/app.js :: posterSelect ~line 796) */
  function selectCell(i, instant) {
    var n = DATA.games.length;
    if (n <= 0) { return; }
    i = Math.max(0, Math.min(n - 1, i));

    posterSel = i;

    /* Paint + scroll the selection in whichever view is active. Both the poster
       grid and the columns list share one selection index (posterSel); the
       painters resolve the matching DOM element (.lb-cell[i] vs
       .lb-row[data-i=i]) and scroll it into view. */
    if (lbViewMode() === "list") { lbPaintListSelection(i, instant); }
    else { lbPaintGridSelection(i, instant); }

    /* Populate the right panel with light data (immediate, from games.json).
       (mirrors BigBoxWeb/web/engine/app.js :: posterSelect → fillPosterSide ~line 804) */
    fillLbPanel(i);

    /* Update play button label / caret state for the newly-selected game. */
    refreshLbPlayLabel(DATA.games[i]);

    /* Live install re-check: drop the previous game's poll; (re)start it for a
       store game (g.store is in the list payload, so this fires immediately). */
    stopLbInstallPoll();
    if (DATA.games[i] && DATA.games[i].store) { startLbInstallPoll(i); }

    /* Debounce the heavy detail fetch — mirrors BBW scheduleHeavy (app.js ~line 4429).
       fillLbPanel above runs synchronously (light data, immediate skeleton).
       loadLbDetail is deferred so rapid navigation through cells does not
       fire a network request for every intermediate game.
       (ref: BigBoxWeb/web/engine/app.js :: scheduleHeavy + posterSelect) */
    if (lbHeavyTimer) { clearTimeout(lbHeavyTimer); lbHeavyTimer = null; }
    var _lbHeavyDelay = (window.LBW.config.posterView && window.LBW.config.posterView.heavyDelayMs != null)
        ? window.LBW.config.posterView.heavyDelayMs : 300;
    lbHeavyTimer = setTimeout(function () { lbHeavyTimer = null; loadLbDetail(i); }, _lbHeavyDelay);
  }


  /* ══════════════════════════════════════════════════════════════════════════
     PART A — Detail JSON fetch + heavy panel re-render
     Mirrors requestGameDetail() + scheduleHeavy() from BigBoxWeb.
     References:
       BigBoxWeb/web/engine/app.js :: requestGameDetail ~line 4319
       BigBoxWeb/web/engine/app.js :: scheduleHeavy     ~line 4429
       BigBoxWeb/web/engine/app.js :: fillPosterMedia   ~line 1118
     ══════════════════════════════════════════════════════════════════════════ */

  /* ── loadLbDetail ───────────────────────────────────────────────────────── */
  /* Fetches /launchbox/data/games/<id>/detail.json for the game at index gi,
     merges the response into DATA.games[gi], then calls fillLbPanelHeavy(gi).
     Uses lbDetailToken for in-flight cancellation: a newer selectCell call
     bumps the token, making the current fetch's callback a no-op.
     Uses lbDetailCache to skip the network round-trip on repeat visits.
     (mirrors BigBoxWeb/web/engine/app.js :: requestGameDetail ~line 4319
              and scheduleHeavy ~line 4429) */
  function loadLbDetail(gi) {
    var g = DATA.games[gi];
    if (!g || !g.id) { return; }

    /* Bump token — any previously-in-flight fetch for a different game will
       see myToken !== lbDetailToken and discard its response. */
    lbDetailToken += 1;
    var myToken = lbDetailToken;

    /* Cache hit: merge synchronously and update the panel immediately. */
    if (lbDetailCache[g.id]) {
      mergeDetail(g, lbDetailCache[g.id]);
      applyLbLastLaunchSync(g);
      fillLbPanelHeavy(gi);
      return;
    }

    /* Cache miss: fetch from the server. */
    var url = "/launchbox/data/games/" + encodeURIComponent(String(g.id)) + "/detail.json";
    fetch(url)
      .then(function (res) {
        if (!res.ok) { throw new Error("HTTP " + res.status); }
        return res.json();
      })
      .then(function (det) {
        /* Token check: if the user has moved to another game, discard. */
        if (myToken !== lbDetailToken) { return; }
        if (!det) { return; }
        /* Cache before merge so repeat visits skip the network. */
        lbDetailCache[g.id] = det;
        mergeDetail(g, det);
        applyLbLastLaunchSync(g);
        fillLbPanelHeavy(gi);
      })
      .catch(function (err) {
        /* Log but leave the panel as-is — light data is already visible. */
        console.error("[LBW] loadLbDetail error (game " + g.id + "):", err);
      });
  }

  /* ── mergeDetail ────────────────────────────────────────────────────────── */
  /* Assigns fields from det onto g without replacing existing truthy values
     that detail.json does not supply (null/undefined guard on each field).
     Mirrors the field-by-field assignment in BBW requestGameDetail callback.
     (ref: BigBoxWeb/web/engine/app.js :: requestGameDetail ~lines 4335-4366) */
  function mergeDetail(g, det) {
    if (det.t    != null) { g.t    = det.t;    }
    if (det.y    != null) { g.y    = det.y;    }
    if (det.dev  != null) { g.dev  = det.dev;  }
    if (det.pub  != null) { g.pub  = det.pub;  }
    if (det.g    != null) { g.g    = det.g;    }
    if (det.r    != null) { g.r    = det.r;    }
    /* Community Star Rating vote count (server: CommunityStarRatingTotalVotes).
       detail.json only — the light games.json has no votes.  Without this copy
       g.votes stays undefined and the rating tooltip's "Total Community Star
       Rating Votes" line never shows. */
    if (det.votes != null) { g.votes = det.votes; }
    if (det.esrb != null) { g.esrb = det.esrb; }
    /* Real-time store install state (also in the list payload; re-merged here for
       cross-platform recents/relations that may arrive detail-first). */
    if (det.store !== undefined)         { g.store     = det.store; }
    if (typeof det.installed === "boolean") { g.installed = det.installed; }
    if (det.d    != null) { g.d    = det.d;    }
    /* description is an alternate field name some detail.json records use */
    if (det.description != null && det.d == null) { g.d = det.description; }
    if (det.shotImg)  { g.shotImg  = det.shotImg;  }
    if (det.shotThumb){ g.shotThumb= det.shotThumb; }
    if (det.shots)    { g.shots    = det.shots;    }
    if (det.video)    { g.video    = det.video;    }
    if (det.fanart)   { g.fanart   = det.fanart;   }
    if (det.logo)     { g.logo     = det.logo;     }
    /* playTime: seconds (integer) from the server */
    if (det.playTime != null) { g._playTimeSec = det.playTime; }
    /* launchOptions: versions / emulators / archive flags supplied by detail.json.
       Merge only when present; play button state depends on this.
       (ref: BBW launchOptions DTO shape used in play menu builder ~line 2962) */
    if (det.launchOptions != null) { g.launchOptions = det.launchOptions; }
    /* lastLaunch: the server's authoritative last (version, emulator, archive
       entry) for this game — drives applyLbLastLaunchSync to pre-select the
       Version / ROM buttons on entry (mirrors BBW). */
    if (det.lastLaunch != null) { g.lastLaunch = det.lastLaunch; }
  }

  /* ── applyLbLastLaunchSync ──────────────────────────────────────────────────
     Seeds the selected Version + ROM from the SERVER's last launch
     (detail.json.lastLaunch → LaunchHistoryDb). The server is authoritative for
     the single most-recent (game, version, entry) tuple — including LB-native
     launches the web UI never saw — so on every detail entry we overwrite the
     localStorage slot for the active version with what the server reports.
     localStorage stays the user's *pending* pick within a page visit only.
     (mirrors BigBoxWeb/web/engine/app.js :: applyLastLaunchSync ~line 2894) */
  function applyLbLastLaunchSync(g) {
    if (!g || !g.id) { return; }
    var last = g.lastLaunch;
    /* Version: the recorded additional-app if it still exists, else default. */
    var appId = (last && last.appId) ? last.appId : null;
    if (appId && lbFindVersion(g, appId)) { setSelectedVersionAppId(g, appId); }
    else                                  { setSelectedVersionAppId(g, null); }
    /* ROM: keyed on the now-active version. null when the last launch wasn't an
       archive launch → clears any stale slot so the button shows "ROM". On a
       version where the user used « Clear » (force-priority set), we DON'T
       re-seed from lastLaunch → the selection stays cleared on re-entry. */
    var selVerId = getSelectedVersionAppId(g);
    var selVer   = selVerId ? lbFindVersion(g, selVerId) : null;
    if (!getRomForce(g, selVer)) {
      setSelectedRomFor(g, selVer, (last && last.archiveEntry) ? last.archiveEntry : null);
    }
  }

  /* ── formatPlayTime ─────────────────────────────────────────────────────── */
  /* Converts total seconds to "Xh Ym" string.  Returns "—" when <= 0. */
  function formatPlayTime(seconds) {
    if (!seconds || seconds <= 0) { return "—"; }
    var h = Math.floor(seconds / 3600);
    var m = Math.floor((seconds % 3600) / 60);
    if (h > 0) { return h + "h " + m + "m"; }
    return m + "m";
  }

  /* ── fillLbPanelHeavy ───────────────────────────────────────────────────── */
  /* Updates the rich / data-heavy sections of the right panel for game gi.
     Called by loadLbDetail() after detail.json has been merged into g.
     Only updates elements that benefit from the richer detail data; the
     full light render (fillLbPanel) already ran first via selectCell().
     (mirrors the tail of BBW requestGameDetail callback + fillPosterMedia) */
  function fillLbPanelHeavy(gi) {
    var g = DATA.games[gi];
    if (!g) { return; }

    /* Guard: only update if this game is still the selected one */
    if (posterSel !== gi) { return; }

    var DASH = "—";

    /* ── 1. Description ───────────────────────────────────────────────────
       g.d now holds the detail-quality description (may contain HTML entities).
       (ref: BigBoxWeb/web/engine/app.js :: requestGameDetail g.d merge ~line 4335) */
    var descEl = document.querySelector(".lb-panel-desc");
    if (descEl) {
      descEl.textContent = g.d ? decodeHtmlEntities(g.d) : DASH;
    }

    /* ── 2. Main media — upgrade to full-quality shotImg if available ─────
       If a video is present use it; else prefer the full shotImg from detail
       over the degraded thumb that the light render may have used.
       (ref: BigBoxWeb/web/engine/app.js :: fillPosterMedia ~line 1118) */
    var mediaEl = document.querySelector(".lb-panel-media");
    if (mediaEl) {
      /* Only rebuild when we actually have richer data than the light render
         already placed — avoids a flicker when detail adds nothing new. */
      var needsRebuild = false;
      if (g.video) {
        /* If there's a video but the current child isn't a <video>, rebuild */
        var existingVid = mediaEl.querySelector("video");
        needsRebuild = !existingVid || existingVid.src !== g.video;
      } else if (g.shotImg) {
        /* If we now have a full-res screenshot (from detail) but only a thumb
           was shown by the light render, upgrade */
        var existingImg = mediaEl.querySelector("img.lb-panel-media-img");
        needsRebuild = !existingImg ||
          (existingImg.src.indexOf(g.shotImg) === -1 &&
           existingImg.src.indexOf(encodeURIComponent(g.shotImg)) === -1);
      }
      if (needsRebuild) {
        mediaEl.innerHTML = "";
        if (g.video) {
          /* (A3) Audio unlock: mute only when user has not yet unlocked audio.
             (ref: BigBoxWeb/web/engine/app.js :: unlockAudio / v.muted ~line 200) */
          var vid = document.createElement("video");
          vid.src = g.video;
          vid.autoplay = true;
          vid.muted = !lbAudioOn;
          vid.loop = false;
          vid.playsInline = true;
          vid.controls = true;
          vid.setAttribute("controlsList", "nodownload");
          vid.className = "lb-panel-media-video";
          mediaEl.appendChild(vid);
          var pr = vid.play();
          if (pr && pr.catch) {
            pr.catch(function () { vid.muted = true; vid.play().catch(function () {}); });
          }
        } else if (g.shotImg || g.shotThumb) {
          var mim = document.createElement("img");
          mim.className = "lb-panel-media-img";
          mim.src = g.shotImg || g.shotThumb;
          mim.alt = "";
          mediaEl.appendChild(mim);
        }
      }
    }

    /* ── 3. Screenshot thumbs strip ───────────────────────────────────────
       Populate .lb-panel-thumbs with up to 10 entries from g.shots.
       Each entry is a .lb-panel-thumb containing an <img>.  src set directly
       (panel area is small; LazyLoad would add complexity for minimal gain).
       (mirrors BBW fillPosterMedia thumbs section ~line 1143) */
    var thumbsEl = document.querySelector(".lb-panel-thumbs");
    if (thumbsEl && Array.isArray(g.shots) && g.shots.length > 0) {
      thumbsEl.innerHTML = "";
      var maxThumbs = Math.min(g.shots.length, 10);
      for (var si = 0; si < maxThumbs; si++) {
        var shot = g.shots[si];
        if (!shot) { continue; }
        /* shots entries can be { src, poster, kind } objects or bare strings */
        var shotSrc = (typeof shot === "string") ? shot : (shot.poster || shot.src || shot.thumb || "");
        if (!shotSrc) { continue; }
        var thumb = document.createElement("div");
        thumb.className = "lb-panel-thumb";
        /* First thumb = the media currently shown, so it starts selected and
           gives keyboard / gamepad stepping (lbPanelMediaStep) a start point. */
        if (si === 0) { thumb.classList.add("sel"); }
        var timg = document.createElement("img");
        timg.src = shotSrc;
        /* Full-size source used by lbSelectPanelThumb to swap the main media. */
        var shotFull = (typeof shot === "string")
          ? shot
          : (shot.src || shot.poster || shot.thumb || shotSrc);
        if (shotFull) { timg.setAttribute("data-full", shotFull); }
        timg.alt = "";
        /* Inline load=lazy: these are inside the panel viewport so native
           lazy loading is fine here (not under the grid LazyLoad instance).
           (pattern: BBW fillPosterMedia uses native loading="lazy" for thumbs
            ref: BigBoxWeb/web/engine/app.js ~line 1147) */
        timg.loading = "lazy";
        thumb.appendChild(timg);
        /* Click a thumb → make it the main media (mouse path; the right-zone
           keyboard / gamepad nav reuses lbSelectPanelThumb via lbPanelMediaStep). */
        thumb.addEventListener("click", (function (t) {
          return function () { lbSelectPanelThumb(t); };
        })(thumb));
        thumbsEl.appendChild(thumb);
      }
    }

    /* ── 4. Hero fanart — crossfade now that we have the fanart list ──────
       detail.json may supply g.fanart (array) that was absent at light-render
       time.  Re-invoke scheduleLbHeroFanart (passing g) to pick it up.
       (ref: BBW requestGameDetail ~line 4361 re-triggers schedulePosterFanart
        when fanart arrives after the initial timer already fired) */
    if (g.fanart && Array.isArray(g.fanart) && g.fanart.length > 0) {
      scheduleLbHeroFanart(g);
    }

    /* ── 4b. Rating — repaint after detail merge may have updated g.r/g.ur ─
       (ref: BigBoxWeb/web/engine/app.js :: paintRating call in heavy render) */
    var ratHeavyEl = document.querySelector(".lb-panel-rating");
    paintLbRating(ratHeavyEl, gi);

    /* ── 5. Play time ─────────────────────────────────────────────────────
       g._playTimeSec is set by mergeDetail() from detail.json.playTime.
       Format as "Xh Ym" or "Ym".  Leave "—" if not available. */
    var ptEl = document.querySelector(".lbp-pt");
    if (ptEl) {
      ptEl.textContent = (g._playTimeSec != null)
        ? formatPlayTime(g._playTimeSec)
        : (g._playtime != null ? g._playtime : DASH);
    }

    /* ── 6. Re-update metadata rows in case detail has more accurate values */
    var yearEl  = document.querySelector(".lbp-year");
    var devEl   = document.querySelector(".lbp-dev");
    var pubEl   = document.querySelector(".lbp-pub");
    var genreEl = document.querySelector(".lbp-genre");
    var esrbEl  = document.querySelector(".lbp-esrb");

    if (yearEl  && g.y)    { yearEl.textContent  = g.y;    }
    if (devEl   && g.dev)  { devEl.textContent   = g.dev;  }
    if (pubEl   && g.pub)  { pubEl.textContent   = g.pub;  }
    if (genreEl && g.g)    { genreEl.textContent = g.g;    }
    if (esrbEl  && g.esrb) { esrbEl.textContent  = g.esrb; }

    /* ── 7. Re-update logo if detail supplies a better one ───────────────── */
    if (g.logo) {
      var logoEl = document.querySelector(".lb-panel-logo");
      if (logoEl) {
        /* Only replace if logo element currently holds a text fallback */
        var existingLogoImg = logoEl.querySelector("img");
        if (!existingLogoImg) {
          logoEl.innerHTML = "";
          var newLogoImg = document.createElement("img");
          newLogoImg.src = g.logo;
          newLogoImg.alt = "";
          logoEl.appendChild(newLogoImg);
        }
      }
    }

    /* ── 8. Refresh play button label/state — detail may have supplied
       launchOptions (versions, emulators, archive flags) that were absent
       at light-render time.
       (ref: mergeDetail launchOptions merge above; refreshLbPlayLabel reads
        g.launchOptions to decide caret visibility) */
    refreshLbPlayLabel(g);
  }


  /* ══════════════════════════════════════════════════════════════════════════
     PART B — Keyboard navigation
     Mirrors BBW onKey() + handleNav() poster branch + computePosterCols().
     References:
       BigBoxWeb/web/engine/app.js :: onKey           ~line 4881
       BigBoxWeb/web/engine/app.js :: handleNav       ~line 4711
       BigBoxWeb/web/engine/app.js :: computePosterCols ~line 688
       BigBoxWeb/web/engine/app.js :: posterCols      ~line 686
     ══════════════════════════════════════════════════════════════════════════ */

  /* ── computeLbPosterCols ────────────────────────────────────────────────── */
  /* Reads the CSS custom properties from :root and the scroll container width
     to determine how many columns the auto-fill grid has laid out.
     Called after buildLbGrid() and on debounced window resize.
     (mirrors BigBoxWeb/web/engine/app.js :: computePosterCols ~line 688) */
  function computeLbPosterCols() {
    var scroll = document.querySelector(".lb-grid-scroll");
    if (!scroll) { return lbPosterCols; }

    /* Read CSS custom properties; fall back to the :root defaults. */
    var rootStyle = getComputedStyle(document.documentElement);
    var cellWStr  = rootStyle.getPropertyValue("--lb-cell-w").trim()  || "150px";
    var gapStr    = rootStyle.getPropertyValue("--lb-gap").trim()     || "20px";
    var cellW     = parseFloat(cellWStr)  || 150;
    var gap       = parseFloat(gapStr)    || 20;

    /* Available width = container clientWidth minus 16 px padding on each side. */
    var availW = (scroll.clientWidth || 0) - 32;   /* 2 × 16 px padding */

    lbPosterCols = Math.max(1, Math.floor((availW + gap) / (cellW + gap)));
    return lbPosterCols;
  }

  /* ── Debounced resize handler ────────────────────────────────────────────── */
  var _lbResizeTimer = null;
  function _onLbResize() {
    if (_lbResizeTimer) { clearTimeout(_lbResizeTimer); }
    _lbResizeTimer = setTimeout(function () {
      _lbResizeTimer = null;
      computeLbPosterCols();
    }, 200);
  }
  window.addEventListener("resize", _onLbResize);


  /* ══════════════════════════════════════════════════════════════════════════
     FOCUS ZONES — left tree · center grid · right panel
     ----------------------------------------------------------------------------
     One of the three columns holds the "focus".  Keyboard (lbOnKey) and gamepad
     (gamepad.js) both reduce their input to a small set of commands and feed the
     single router lbNav(cmd, repeat), which routes to the focused zone.
     Mirrors the BigBoxWeb engine's `zone` state machine + handleNav dispatcher
     (engine/app.js :: zone ~line 51, handleNav ~line 4711).
     ══════════════════════════════════════════════════════════════════════════ */

  /* Active zone: "left" | "center" | "right".  Default = center (grid). */
  var lbZone = "center";

  /* Debounce timer for the left-zone "load on move" (so holding a direction
     does not fire a fetch storm; the games cache makes settles instant). */
  var _lbTreeLoadTimer = null;

  function lbZoneEl(zone) {
    if (zone === "left")  { return document.getElementById("lb-tree"); }
    if (zone === "right") { return document.getElementById("lb-panel"); }
    return document.getElementById("lb-grid");
  }

  /* Very light flash on the zone element.  Same remove → reflow → add pattern
     as the engine's flashHighlight (engine/app.js ~line 285). */
  function flashLbZone(el) {
    if (!el) { return; }
    el.classList.remove("lb-zone-flash");
    void el.offsetWidth;            /* force reflow so the anim can re-trigger */
    el.classList.add("lb-zone-flash");
  }

  /* Move focus to a zone.  opts.noFlash skips the flash (boot); opts.fromMouse
     skips the left-zone auto-activate (the click handler already selects a row). */
  function setLbZone(zone, opts) {
    opts = opts || {};
    var body = document.getElementById("lb-body");
    if (!body) { return; }

    var changed = (zone !== lbZone);
    lbZone = zone;
    body.classList.remove("lb-zone-left", "lb-zone-center", "lb-zone-right");
    body.classList.add("lb-zone-" + zone);

    /* Entering the tree with no live selection → highlight + load the first
       visible row so arrow keys have a starting point. */
    if (changed && zone === "left" && !opts.fromMouse) {
      if (!_selectedNode || _selectedNode.offsetParent === null) {
        var rows = lbVisibleTreeRows();
        if (rows.length) { lbActivateTreeRow(rows[0], false); }
      } else {
        try { _selectedNode.scrollIntoView({ block: "nearest" }); } catch (_) {}
      }
    }

    if (changed && !opts.noFlash) {
      flashLbZone(lbZoneEl(zone));
    }
  }

  function cycleLbZone() {
    var order = ["left", "center", "right"];
    var i = order.indexOf(lbZone);
    setLbZone(order[(i + 1) % order.length]);
  }

  /* ── Type-ahead (incremental title search on the grid) ───────────────────
     When the center (grid) zone is focused, typing printable characters jumps
     the selection to the first game whose title starts with the typed prefix.
     Characters accumulate; an 800 ms pause resets the buffer — so typing
     "secre" quickly lands on "Secret of Mana". Pure client-side: DATA.games is
     already in memory, no server round-trip. Mirrors the desktop LaunchBox /
     Windows list-view type-ahead convention.

     Assumes DATA.games is in display order (games.json is title-sorted), so the
     first prefix match is the alphabetically-first hit. */
  var _lbTypeBuf = "";
  var _lbTypeAt  = 0;
  var LB_TYPE_RESET_MS = 800;

  /* True for a single printable character (letter / digit / punctuation) with
     no modifier. Space is excluded — it is the grid "select" command. */
  function lbIsTypeAheadKey(e) {
    return !!e.key && e.key.length === 1 && e.key !== " " &&
           !e.ctrlKey && !e.altKey && !e.metaKey;
  }

  function lbTypeAhead(ch) {
    var games = DATA.games;
    if (!games || !games.length) { return; }

    var now = Date.now();
    if (now - _lbTypeAt > LB_TYPE_RESET_MS) { _lbTypeBuf = ""; }
    _lbTypeAt = now;
    _lbTypeBuf += ch.toLowerCase();

    var hit = lbFirstTitlePrefix(_lbTypeBuf);

    /* Fallback: if the accumulated prefix matches nothing (e.g. a stray key
       broke the run), restart the buffer from just this character so a fresh
       letter still jumps somewhere. */
    if (hit < 0 && _lbTypeBuf.length > 1) {
      _lbTypeBuf = ch.toLowerCase();
      hit = lbFirstTitlePrefix(_lbTypeBuf);
    }

    if (hit >= 0) { selectCell(hit); }
  }

  /* Index of the first game whose lowercased title starts with `prefix`, or -1. */
  function lbFirstTitlePrefix(prefix) {
    var games = DATA.games;
    for (var i = 0; i < games.length; i++) {
      var t = (games[i] && games[i].t) ? String(games[i].t).toLowerCase() : "";
      if (t.indexOf(prefix) === 0) { return i; }
    }
    return -1;
  }

  /* ── lbNav — the single command router ───────────────────────────────────── */
  function lbNav(cmd, repeat) {
    /* Global commands (independent of the focused zone). */
    if (cmd === "zone") { cycleLbZone(); return; }
    if (cmd === "menu") {
      var mb = document.getElementById("lb-menu-btn");
      if (mb) { mb.click(); }
      return;
    }
    if (cmd === "mediaPrev") { lbPanelMediaStep(-1); return; }
    if (cmd === "mediaNext") { lbPanelMediaStep(1);  return; }
    if (cmd === "back") {
      /* Modals are intercepted earlier in lbOnKey; here B/back just closes the
         play menu when open, otherwise it is a no-op. */
      if (lbPlayMenuOpen) { closeLbPlayMenu(); }
      return;
    }

    switch (lbZone) {
      case "left":  lbNavLeft(cmd, repeat);  break;
      case "right": lbNavRight(cmd, repeat); break;
      default:      lbNavCenter(cmd, repeat); break;
    }
  }

  /* ── Center zone: poster grid (unchanged behaviour, moved out of lbOnKey) ── */
  function lbNavCenter(cmd, repeat) {
    if (DATA.games.length === 0) { return; }

    var n   = DATA.games.length;
    var cur = Math.max(0, posterSel);   /* treat -1 as 0 for the first keypress */
    var nxt;

    /* Enter / Space launches the selected game (both views). */
    if (cmd === "select") {
      if (posterSel >= 0) {
        var pb = document.getElementById("lb-panel-play");
        if (pb) { pb.click(); }
      }
      return;
    }

    /* First navigation key with nothing selected → land on the first item. */
    if (posterSel < 0) { selectCell(0, repeat); return; }

    if (lbViewMode() === "list") {
      /* List view: one row per step; a "page" ≈ the visible row count. */
      var page = lbListVisibleRows();
      switch (cmd) {
        case "down": nxt = Math.min(n - 1, cur + 1);    break;
        case "up":   nxt = Math.max(0, cur - 1);        break;
        case "home": nxt = 0;                           break;
        case "end":  nxt = n - 1;                       break;
        case "pgdn": nxt = Math.min(n - 1, cur + page); break;
        case "pgup": nxt = Math.max(0, cur - page);     break;
        case "left": case "right": return;   /* no horizontal nav in the list */
        default: return;
      }
    } else {
      switch (cmd) {
        case "right": nxt = Math.min(n - 1, cur + 1);                break;
        case "left":  nxt = Math.max(0, cur - 1);                   break;
        case "down":  nxt = Math.min(n - 1, cur + lbPosterCols);     break;
        case "up":    nxt = Math.max(0, cur - lbPosterCols);         break;
        case "home":  nxt = 0;                                       break;
        case "end":   nxt = n - 1;                                   break;
        case "pgdn":  nxt = Math.min(n - 1, cur + lbPosterCols * 5); break;
        case "pgup":  nxt = Math.max(0, cur - lbPosterCols * 5);     break;
        default: return;
      }
    }
    if (nxt === undefined) { return; }
    /* Pass repeat as the 'instant' flag so held-key auto-repeat uses
       behavior:'auto' scroll (see selectCell). */
    selectCell(nxt, repeat);
  }

  /* ── Left zone: tree navigation (loads the row as you move onto it) ──────── */

  /* Ordered list of the rows the user can land on: category headers + the
     currently-VISIBLE leaf rows (collapsed children and search-filtered rows
     report offsetParent === null and are skipped).  DOM order == visual order. */
  function lbVisibleTreeRows() {
    var scroll = document.querySelector(".lb-tree-scroll");
    if (!scroll) { return []; }
    var all = scroll.querySelectorAll(".lb-tree-cat, .lb-tree-node");
    var out = [];
    for (var i = 0; i < all.length; i++) {
      if (all[i].offsetParent !== null) { out.push(all[i]); }
    }
    return out;
  }

  /* Move the .sel highlight immediately (cheap); load the row's games now on a
     single press, or debounced when a direction key auto-repeats. */
  function lbHighlightTreeRow(el) {
    if (!el) { return; }
    if (_selectedNode && _selectedNode !== el) {
      _selectedNode.classList.remove("sel");
    }
    el.classList.add("sel");
    _selectedNode = el;
    try { el.scrollIntoView({ block: "nearest" }); } catch (_) {}
  }

  function lbLoadTreeRow(el) {
    if (!el) { return; }
    var node = el._lbNode || {};
    if (el.classList.contains("lb-tree-cat")) {
      selectCategory(el, node);
    } else {
      selectLeaf(el, node.name || "", node.count, node.path || "");
    }
  }

  function lbActivateTreeRow(el, repeat) {
    lbHighlightTreeRow(el);
    if (_lbTreeLoadTimer) { clearTimeout(_lbTreeLoadTimer); _lbTreeLoadTimer = null; }
    if (repeat) {
      _lbTreeLoadTimer = setTimeout(function () {
        _lbTreeLoadTimer = null;
        lbLoadTreeRow(el);
      }, 120);
    } else {
      lbLoadTreeRow(el);
    }
  }

  function lbNavLeft(cmd, repeat) {
    var rows = lbVisibleTreeRows();
    if (rows.length === 0) { return; }

    var cur = _selectedNode;
    var idx = cur ? rows.indexOf(cur) : -1;
    var isCat = cur && cur.classList.contains("lb-tree-cat");

    switch (cmd) {
      case "down":
        lbActivateTreeRow(rows[idx < 0 ? 0 : Math.min(rows.length - 1, idx + 1)], repeat);
        break;
      case "up":
        lbActivateTreeRow(rows[idx <= 0 ? 0 : idx - 1], repeat);
        break;
      case "pgdn":
        lbActivateTreeRow(rows[idx < 0 ? 0 : Math.min(rows.length - 1, idx + 5)], repeat);
        break;
      case "pgup":
        lbActivateTreeRow(rows[idx <= 0 ? 0 : Math.max(0, idx - 5)], repeat);
        break;
      case "home":
        lbActivateTreeRow(rows[0], repeat);
        break;
      case "end":
        lbActivateTreeRow(rows[rows.length - 1], repeat);
        break;
      case "right":
        if (isCat) {
          if (cur.classList.contains("collapsed")) {
            toggleCategoryCollapsed(cur);                 /* expand, no load */
          } else if (rows[idx + 1] && rows[idx + 1].classList.contains("lb-tree-node")) {
            lbActivateTreeRow(rows[idx + 1], repeat);       /* descend to 1st child */
          }
        }
        break;
      case "left":
        if (isCat) {
          if (!cur.classList.contains("collapsed")) {
            toggleCategoryCollapsed(cur);                 /* collapse */
          }
        } else if (cur) {
          /* On a child leaf → jump up to the parent category header and load it. */
          for (var j = idx - 1; j >= 0; j--) {
            if (rows[j].classList.contains("lb-tree-cat")) {
              lbActivateTreeRow(rows[j], repeat);
              break;
            }
          }
        }
        break;
      case "select":
        if (isCat) { toggleCategoryCollapsed(cur); }       /* leaf select = no-op */
        break;
      default: break;
    }
  }

  /* ── Right zone: detail panel (scroll · media carousel · Play) ───────────── */
  function lbNavRight(cmd, repeat) {
    var scroll = document.querySelector(".lb-panel-scroll");
    switch (cmd) {
      case "up":    if (scroll) { scroll.scrollTop -= 80; } break;
      case "down":  if (scroll) { scroll.scrollTop += 80; } break;
      case "pgup":  if (scroll) { scroll.scrollTop -= scroll.clientHeight * 0.8; } break;
      case "pgdn":  if (scroll) { scroll.scrollTop += scroll.clientHeight * 0.8; } break;
      case "home":  if (scroll) { scroll.scrollTop = 0; } break;
      case "end":   if (scroll) { scroll.scrollTop = scroll.scrollHeight; } break;
      case "left":  lbPanelMediaStep(-1); break;
      case "right": lbPanelMediaStep(1);  break;
      case "select": {
        var pb = document.getElementById("lb-panel-play");
        if (pb) { pb.click(); }
        break;
      }
      default: break;
    }
  }

  /* ── Panel media carousel ────────────────────────────────────────────────
     The thumbs in .lb-panel-thumbs are made clickable in fillLbPanelHeavy;
     selecting one swaps the main .lb-panel-media image.  Stepping reuses the
     same selection so keyboard / gamepad / mouse stay in sync. */
  function lbSelectPanelThumb(thumb) {
    if (!thumb) { return; }
    var thumbsEl = document.querySelector(".lb-panel-thumbs");
    if (thumbsEl) {
      var sels = thumbsEl.querySelectorAll(".lb-panel-thumb.sel");
      for (var i = 0; i < sels.length; i++) { sels[i].classList.remove("sel"); }
    }
    thumb.classList.add("sel");
    try { thumb.scrollIntoView({ block: "nearest", inline: "nearest" }); } catch (_) {}

    var img = thumb.querySelector("img");
    var src = img ? (img.getAttribute("data-full") || img.src) : "";
    if (!src) { return; }
    var mediaEl = document.querySelector(".lb-panel-media");
    if (!mediaEl) { return; }
    mediaEl.innerHTML = "";
    var big = document.createElement("img");
    big.className = "lb-panel-media-img";
    big.src = src;
    big.alt = "";
    mediaEl.appendChild(big);
  }

  function lbPanelMediaStep(dir) {
    var thumbsEl = document.querySelector(".lb-panel-thumbs");
    if (!thumbsEl) { return; }
    var thumbs = thumbsEl.querySelectorAll(".lb-panel-thumb");
    if (!thumbs.length) { return; }
    var cur = -1;
    for (var i = 0; i < thumbs.length; i++) {
      if (thumbs[i].classList.contains("sel")) { cur = i; break; }
    }
    var next = (cur < 0)
      ? (dir > 0 ? 0 : thumbs.length - 1)
      : Math.min(thumbs.length - 1, Math.max(0, cur + dir));
    lbSelectPanelThumb(thumbs[next]);
  }

  /* Expose the router so gamepad.js (loaded after app.js) can drive the same
     navigation as the keyboard. */
  if (window.LBW) { window.LBW.nav = lbNav; }


  /* ── lbOnKey ─────────────────────────────────────────────────────────────── */
  /* Keyboard handler for the poster grid + parental modals.
     Priority order (from highest to lowest):
       1. PIN pad open → route digit/backspace/enter/escape to the PIN pad.
       2. Settings (cfg) modal open → Escape closes it.
       3. Play menu open → Escape closes it.
       4. BigBox info modal open → escape/enter closes it.
       5. Focused <input>/<textarea> → let browser handle it (tree search).
       6. Grid navigation (arrows, home/end, page, enter, escape).
     (mirrors BigBoxWeb/web/engine/app.js :: onKey ~line 4881
              + handleNav PIN pad guard ~line 4727
              + handleNav poster branch ~line 4820) */
  function lbOnKey(e) {

    /* ── 1. PIN pad takes over all keyboard input when open ───────────────
       (ref: BigBoxWeb/web/engine/app.js :: handleNav pinOpen guard ~line 4728) */
    if (lbPinOpen) {
      switch (e.key) {
        case "0": case "1": case "2": case "3": case "4":
        case "5": case "6": case "7": case "8": case "9":
          e.preventDefault();
          if (lbPinValue.length < 8) { lbPinValue += e.key; paintLbPin(); }
          return;
        case "Backspace":
          e.preventDefault();
          lbPinValue = lbPinValue.slice(0, -1);
          paintLbPin();
          return;
        case "Enter":
          e.preventDefault();
          submitPin();
          return;
        case "Escape":
          e.preventDefault();
          closePinPad();
          return;
        default:
          /* Swallow all other keys while PIN pad is open — do NOT fall
             through to grid navigation.
             (ref: BigBoxWeb/web/engine/app.js :: handleNav ~line 4732 return) */
          return;
      }
    }

    /* ── 2. Settings modal — Escape closes it immediately after the pinpad.
       Priority: pinpad > cfg modal > play menu > info modal > grid.
       Checks the hidden attribute on #lb-cfg-modal (index.html uses `hidden`
       attribute to show/hide the modal, consistent with the other modals). */
    if (e.key === "Escape") {
      var cfgModalEarly = document.getElementById("lb-cfg-modal");
      if (cfgModalEarly && !cfgModalEarly.hasAttribute("hidden")) {
        closeLbCfgModal();
        e.preventDefault();
        return;
      }
    }

    /* ── 3. Play menu — Escape closes it before grid nav sees the key.
       Priority: pinpad > cfg modal > play menu > info modal > grid.
       (mirrors BBW menuStack Escape handling) */
    if (lbPlayMenuOpen && e.key === "Escape") {
      e.preventDefault();
      closeLbPlayMenu();
      return;
    }

    /* ── 3b. Play menu open: arrows move the highlight (no commit), Enter/Right
       activates, Left backs out. Swallow every mapped nav key so the grid behind
       stays put while the ROM / version / emulator picker is open. */
    if (lbPlayMenuOpen) {
      if (e.key === "ArrowDown")  { e.preventDefault(); lbPlayMenuMove(1);  return; }
      if (e.key === "ArrowUp")    { e.preventDefault(); lbPlayMenuMove(-1); return; }
      if (e.key === "Enter" || e.key === " " || e.key === "Spacebar" || e.key === "ArrowRight") {
        e.preventDefault(); lbPlayMenuActivate(); return;
      }
      if (e.key === "ArrowLeft") {
        e.preventDefault();
        if (lbPlayMenuStack.length > 1) { lbPlayMenuStack.pop(); renderLbPlayMenu(); }
        else { closeLbPlayMenu(); }
        return;
      }
      var lkm = (e.key === " " || e.key === "Spacebar") ? "Spacebar" : e.key;
      if (LBKEYCMD[lkm]) { e.preventDefault(); return; }
    }

    /* ── 4. BigBox info modal ─────────────────────────────────────────────
       (ref: BigBoxWeb/web/engine/app.js :: handleNav infoOpen guard ~line 4715) */
    var infoModal = document.getElementById("lb-info-modal");
    if (infoModal && !infoModal.hasAttribute("hidden")) {
      if (e.key === "Escape" || e.key === "Enter" || e.key === " ") {
        e.preventDefault();
        closeParentalInfo();
      }
      return;
    }

    /* ── 5. Don't intercept while the user is typing into the search box */
    var active = document.activeElement;
    if (active && (active.tagName === "INPUT" || active.tagName === "TEXTAREA")) {
      return;
    }

    /* ── 5b. Type-ahead: a printable key jump-selects a game when the grid
       (center) zone is focused.  Must run before the command switch below so
       letters/digits don't fall through to its default `return`. */
    if (lbZone === "center" && lbIsTypeAheadKey(e)) {
      e.preventDefault();
      lbTypeAhead(e.key);
      return;
    }

    /* ── 6. Zone-routed navigation ────────────────────────────────────────────
       Translate the key into a command and hand it to lbNav, which routes it to
       the focused zone (left tree · center grid · right panel).  Tab / Shift+Tab
       switch zones (same as gamepad Y). */
    /* Touche → commande via la table configurable (serveur). Espace normalisé en
       "Spacebar". Touche non mappée (dont Escape, traité en priorité 2) → ignore. */
    var lk = (e.key === " " || e.key === "Spacebar") ? "Spacebar" : e.key;
    var cmd = LBKEYCMD[lk];
    if (!cmd) return;   /* Don't intercept any other key */

    /* Prevent the browser default (page scroll on arrows / Space, focus move on
       Tab) for every key we consume. */
    e.preventDefault();
    lbNav(cmd, e.repeat);
  }

  document.addEventListener("keydown", lbOnKey);


  /* ══════════════════════════════════════════════════════════════════════════
     CFG MODAL — settings engine
     Mirrors the BBW config-modal engine (app.js:3677–3768) but uses the
     window.LBW namespace, lbw_cfg cookie, and LBW-prefixed CSS classes.

     Public API (all module-scoped):
       openLbCfgModal()            — show modal, populate from live config
       closeLbCfgModal()           — hide modal
       populateLbCfgUi()           — rebuild #lb-cfg-body from configSchema
       bindLbCfgControl(el,s,k,t)  — wire input events → commitLbCfgValue
       commitLbCfgValue(s,k,v)     — write to config + cookie + DOM
       applyLbCfgToDom()           — push all CSS vars / DOM side-effects
       lbCfgReset()                — restore all defaults

     References:
       BBW engine/app.js :: setupConfig       ~line 3677
       BBW engine/app.js :: openConfig        ~line 3686
       BBW engine/app.js :: closeConfig       ~line 3691
       BBW engine/app.js :: renderConfig      ~line 3708
       BBW engine/app.js :: cfgAdjust         ~line 3748
       BBW engine/app.js :: cfgActivate       ~line 3760
       BBW engine/config.js :: applyConfigCss ~line 515
     ══════════════════════════════════════════════════════════════════════════ */

  /* ── applyLbCfgToDom ────────────────────────────────────────────────────────
     Reads window.LBW.config and pushes all visual effects to the DOM:
       • CSS custom properties on document.documentElement.style
       • .fluid class on .lb-grid-scroll
       • Inline widths on column elements (layout.treeWidthPx / panelWidthPx)
     Called once at boot (a3 in DOMContentLoaded) and after every
     commitLbCfgValue / lbCfgReset call.
     (ref: BBW engine/config.js :: applyConfigCss ~line 515) */
  function applyLbCfgToDom() {
    var cfg = window.LBW && window.LBW.config;
    if (!cfg) { return; }
    var r = document.documentElement.style;

    /* ── posterView / hero fanart ── */
    var pv = cfg.posterView || {};
    r.setProperty("--lb-hero-fanart-opacity",
      (pv.heroFanartOpacity != null ? pv.heroFanartOpacity : 0.28));
    r.setProperty("--lb-hero-fanart-fade-in",
      (pv.heroFanartFadeInMs  != null ? pv.heroFanartFadeInMs  : 300) + "ms");
    r.setProperty("--lb-hero-fanart-fade-out",
      (pv.heroFanartFadeOutMs != null ? pv.heroFanartFadeOutMs : 800) + "ms");

    /* ── gridView ── */
    var gv = cfg.gridView || {};
    var cellW = gv.cellWidthPx != null ? gv.cellWidthPx : 150;
    var cellRatio = gv.cellRatio != null ? gv.cellRatio : 1.4;
    r.setProperty("--lb-cell-w",           cellW + "px");
    r.setProperty("--lb-cell-imgh",        (cellW * cellRatio) + "px");
    if (gv.gapPx          != null) { r.setProperty("--lb-gap",              gv.gapPx + "px"); }
    if (gv.cellHoverScale != null) { r.setProperty("--lb-cell-hover-scale", gv.cellHoverScale); }
    if (gv.cellHoverMs    != null) { r.setProperty("--lb-cell-hover-ms",    gv.cellHoverMs + "ms"); }

    /* fluid class on grid scroll container
       index.html: .lb-grid-scroll is a class-only element (no id) */
    var gridScroll = document.querySelector(".lb-grid-scroll");
    if (gridScroll) {
      gridScroll.classList.toggle("fluid", !!(gv.fluid));
    }

    /* ── layout column widths ──
       index.html: left sidebar is <aside id="lb-tree">,
                   right sidebar is <aside id="lb-panel"> — both have IDs. */
    var lv = cfg.layout || {};
    if (lv.treeWidthPx != null) {
      var treeCol = document.getElementById("lb-tree");
      if (treeCol) { treeCol.style.width = lv.treeWidthPx + "px"; }
    }
    if (lv.panelWidthPx != null) {
      var panelCol = document.getElementById("lb-panel");
      if (panelCol) { panelCol.style.width = lv.panelWidthPx + "px"; }
    }

    /* ── theme — action colour ── */
    var th = cfg.theme || {};
    if (th.actionColor) {
      r.setProperty("--lb-action-color", th.actionColor);
      /* Simple lightened hover: increase lightness by ~10% via opacity overlay;
         set a slightly lighter tint as the hover var for pure-CSS fallback */
      r.setProperty("--lb-action-color-hover", th.actionColor);
    }
  }

  /* ── commitLbCfgValue ───────────────────────────────────────────────────────
     Write a single config value, persist to cookie, push CSS/DOM side-effects.
     sectionKey : top-level key in window.LBW.config (e.g. "gridView")
     optionKey  : leaf key within that section (e.g. "cellWidthPx")
     value      : parsed value (number, boolean, or string)
     (ref: BBW engine/app.js :: cfgAdjust → cfg.set → applyConfigCss ~line 3748) */
  function commitLbCfgValue(sectionKey, optionKey, value) {
    var cfg = window.LBW && window.LBW.config;
    if (!cfg) { return; }
    if (!cfg[sectionKey]) { cfg[sectionKey] = {}; }
    cfg[sectionKey][optionKey] = value;
    if (window.LBW.cfg && typeof window.LBW.cfg.set === "function") {
      window.LBW.cfg.set(sectionKey + "." + optionKey, value);
    }
    applyLbCfgToDom();
  }

  /* ── bindLbCfgControl ──────────────────────────────────────────────────────
     Wire a single form control so every change commits immediately (live
     preview, no Save step — mirrors BBW's cfgAdjust live-write pattern).
     el         : the <input> or <select> element
     sectionKey : config section key
     optionKey  : config option key
     type       : "range" | "bool" | "color"
     valueEl    : (optional) sibling <span> showing the numeric value (range only)
     (ref: BBW engine/app.js :: renderConfig bindRow + updateCfgControl ~line 3720) */
  function bindLbCfgControl(el, sectionKey, optionKey, type, valueEl) {
    function read() {
      if (type === "bool")  { return el.checked; }
      if (type === "range") { return parseFloat(el.value); }
      return el.value;   /* color, select */
    }
    function sync() {
      var v = read();
      if (type === "range" && valueEl) {
        /* Round display to the same precision as the step */
        var schema = (window.LBW.configSchema || {})[sectionKey];
        var meta   = schema ? schema[optionKey] : null;
        var step   = meta  ? meta.step : 1;
        var dec    = ((String(step).split(".")[1]) || "").length;
        valueEl.textContent = Number(v).toFixed(dec);
      }
      commitLbCfgValue(sectionKey, optionKey, v);
    }
    el.addEventListener("input",  sync);   /* live preview while dragging */
    el.addEventListener("change", sync);   /* final commit on release / select */
  }

  /* ── populateLbCfgUi ────────────────────────────────────────────────────────
     Iterates configSchema (object form) and builds one <fieldset> per
     top-level section and one .lb-cfg-row per option.  Pre-populates every
     control from the current window.LBW.config values.
     Called on every openLbCfgModal() so values always reflect live config.
     (ref: BBW engine/app.js :: renderConfig ~line 3708) */
  function populateLbCfgUi() {
    var body = document.getElementById("lb-cfg-body");
    if (!body) { return; }
    body.innerHTML = "";

    var schema = (window.LBW && window.LBW.configSchema) || {};
    var cfg    = (window.LBW && window.LBW.config) || {};

    var sectionKeys = Object.keys(schema);
    for (var si = 0; si < sectionKeys.length; si++) {
      var sectionKey = sectionKeys[si];
      var sectionDef = schema[sectionKey];

      /* Defensive: skip sections without a matching live config object */
      if (!cfg[sectionKey] || typeof sectionDef !== "object") {
        if (window.LBW.config.misc && window.LBW.config.misc.debug) {
          console.warn("[LBW cfg] skipping schema section with no config match:", sectionKey);
        }
        continue;
      }

      var fs = document.createElement("fieldset");
      fs.className = "lb-cfg-section";

      var legend = document.createElement("legend");
      /* Capitalise first letter; replace camelCase with spaces for readability */
      legend.textContent = sectionKey.replace(/([A-Z])/g, " $1").replace(/^./, function (c) { return c.toUpperCase(); });
      fs.appendChild(legend);

      var optionKeys = Object.keys(sectionDef);
      for (var oi = 0; oi < optionKeys.length; oi++) {
        var optionKey = optionKeys[oi];
        var meta      = sectionDef[optionKey];

        /* Defensive: skip options missing from live config */
        if (cfg[sectionKey][optionKey] === undefined && typeof meta !== "object") {
          if (window.LBW.config.misc && window.LBW.config.misc.debug) {
            console.warn("[LBW cfg] skipping option not in live config:", sectionKey + "." + optionKey);
          }
          continue;
        }

        var liveVal = cfg[sectionKey][optionKey];

        var row = document.createElement("div");
        row.className = "lb-cfg-row";

        var label = document.createElement("label");
        label.className = "lb-cfg-label";
        label.textContent = meta.label || optionKey;

        var ctrl = document.createElement("span");
        ctrl.className = "lb-cfg-control";

        var input, valueSpan;

        if (meta.type === "range") {
          input = document.createElement("input");
          input.type      = "range";
          input.className = "lb-cfg-range";
          input.min       = meta.min  != null ? meta.min  : 0;
          input.max       = meta.max  != null ? meta.max  : 100;
          input.step      = meta.step != null ? meta.step : 1;
          input.value     = (liveVal != null) ? liveVal : meta.min;

          valueSpan = document.createElement("span");
          valueSpan.className = "lb-cfg-value";
          var dec = ((String(meta.step || 1).split(".")[1]) || "").length;
          valueSpan.textContent = Number(input.value).toFixed(dec);

          ctrl.appendChild(input);
          ctrl.appendChild(valueSpan);
          label.htmlFor = "lb-cfg-" + sectionKey + "-" + optionKey;
          input.id = label.htmlFor;
          bindLbCfgControl(input, sectionKey, optionKey, "range", valueSpan);

        } else if (meta.type === "bool") {
          input = document.createElement("input");
          input.type      = "checkbox";
          input.className = "lb-cfg-check";
          input.checked   = !!(liveVal);
          label.htmlFor = "lb-cfg-" + sectionKey + "-" + optionKey;
          input.id = label.htmlFor;
          ctrl.appendChild(input);
          bindLbCfgControl(input, sectionKey, optionKey, "bool", null);

        } else if (meta.type === "color") {
          input = document.createElement("input");
          input.type      = "color";
          input.className = "lb-cfg-color";
          input.value     = liveVal || "#c8732f";
          label.htmlFor = "lb-cfg-" + sectionKey + "-" + optionKey;
          input.id = label.htmlFor;
          ctrl.appendChild(input);
          bindLbCfgControl(input, sectionKey, optionKey, "color", null);
        }

        row.appendChild(label);
        row.appendChild(ctrl);
        fs.appendChild(row);
      }

      body.appendChild(fs);
    }
  }

  /* ── lbCfgReset ─────────────────────────────────────────────────────────────
     Reset ALL user-configurable sections to their defaults, save, repopulate
     the UI, and apply DOM side-effects.
     Parental keys are excluded because they are server-driven.
     (ref: BBW engine/app.js :: cfgActivate restore-all branch ~line 3763) */
  function lbCfgReset() {
    var schema   = (window.LBW && window.LBW.configSchema) || {};
    var defaults = (window.LBW && window.LBW.configDefaults) || {};
    var cfg      = (window.LBW && window.LBW.config) || {};

    var sectionKeys = Object.keys(schema);
    for (var si = 0; si < sectionKeys.length; si++) {
      var sk = sectionKeys[si];
      if (!defaults[sk]) { continue; }
      /* Deep-clone the defaults section into live config */
      cfg[sk] = JSON.parse(JSON.stringify(defaults[sk]));
      /* Remove all cookie overrides for this section */
      if (window.LBW.cfg && window.LBW.cfg._ov) {
        var ov = window.LBW.cfg._ov;
        var keys = Object.keys(ov);
        for (var ki = 0; ki < keys.length; ki++) {
          if (keys[ki].indexOf(sk + ".") === 0) {
            delete ov[keys[ki]];
          }
        }
      }
    }
    if (window.LBW.cfg && typeof window.LBW.cfg.save === "function") {
      window.LBW.cfg.save();
    }
    applyLbCfgToDom();
    populateLbCfgUi();
  }

  /* ── openLbCfgModal / closeLbCfgModal ───────────────────────────────────────
     (ref: BBW engine/app.js :: openConfig / closeConfig ~line 3686) */
  function openLbCfgModal() {
    var el = document.getElementById("lb-cfg-modal");
    if (!el) { return; }
    populateLbCfgUi();
    el.removeAttribute("hidden");
  }

  function closeLbCfgModal() {
    var el = document.getElementById("lb-cfg-modal");
    if (el) { el.setAttribute("hidden", ""); }
  }


  /* ══════════════════════════════════════════════════════════════════════════
     PANEL POPULATION — fillLbPanel
     Mirrors fillPosterSide() + fillPosterMedia() from BigBoxWeb.
     References:
       BigBoxWeb/web/engine/app.js :: fillPosterSide   ~line 956
       BigBoxWeb/web/engine/app.js :: fillPosterMedia  ~line 1118
       BigBoxWeb/web/engine/app.js :: paintRating      ~line 1032
       BigBoxWeb/web/engine/app.js :: schedulePosterFanart ~line 848
     ══════════════════════════════════════════════════════════════════════════ */

  /* ── pickLbFanart ────────────────────────────────────────────────────────── */
  /* Returns the first entry of g.fanart (array) or null.
     Matches the shape detail.json delivers: g.fanart = string[] | null.
     (ref: BigBoxWeb/web/engine/app.js :: schedulePosterFanart fanart pick ~line 857) */
  function pickLbFanart(g) {
    if (!g) { return null; }
    if (g.fanart && Array.isArray(g.fanart) && g.fanart.length > 0) {
      return g.fanart[0];
    }
    return null;
  }

  /* ── scheduleLbHeroFanart ────────────────────────────────────────────────── */
  /* Asymmetric two-timer crossfade — ported verbatim from BBW schedulePosterFanart.
     lbHeroFanartTimer  (fade-in)  : cancellable debounce — cleared on every call.
     lbFanartOutTimer   (fade-out) : one-shot, scheduled ONCE per deselection,
                                      NOT cancellable (mirrors BBW asymmetry).
     lbHeroFanartActive : 0|1 — which .lb-panel-hero-bg-layer is currently .on.
     The out-timer delay and in-timer delay are read from config.posterView so
     they can be tuned without touching JS.
     (ref: BigBoxWeb/web/engine/app.js :: schedulePosterFanart ~line 845-899) */
  function scheduleLbHeroFanart(g) {
    /* Cancel pending fade-in — always (debounce logic). */
    if (lbHeroFanartTimer) { clearTimeout(lbHeroFanartTimer); lbHeroFanartTimer = null; }

    if (!g) { return; }

    var pv = (window.LBW && window.LBW.config && window.LBW.config.posterView) || {};

    /* Schedule fade-out ONCE — if already pending, leave it alone (non-cancellable). */
    if (lbFanartOutTimer == null) {
      var activeIdx   = lbHeroFanartActive;   /* capture at schedule time */
      var outDelayMs  = (pv.heroFanartFadeOutDelayMs != null) ? pv.heroFanartFadeOutDelayMs : 500;
      lbFanartOutTimer = setTimeout(function () {
        lbFanartOutTimer = null;
        var bgRoot = document.querySelector(".lb-panel-hero-bg");
        if (!bgRoot) { return; }
        var layers = bgRoot.querySelectorAll(".lb-panel-hero-bg-layer");
        if (layers.length >= 2) { layers[activeIdx].classList.remove("on"); }
      }, outDelayMs);
    }

    /* Schedule fade-in (debounced — previous timer was cleared above). */
    var delayMs = (pv.heroFanartDelayMs != null) ? pv.heroFanartDelayMs : 500;
    lbHeroFanartTimer = setTimeout(function () {
      lbHeroFanartTimer = null;
      var hero   = document.querySelector(".lb-panel-hero");
      var bgRoot = document.querySelector(".lb-panel-hero-bg");
      if (!hero || !bgRoot) { return; }
      var layers = bgRoot.querySelectorAll(".lb-panel-hero-bg-layer");
      if (layers.length < 2) { return; }

      var fa        = pickLbFanart(g);
      var nextIdx   = 1 - lbHeroFanartActive;
      var nextLayer = layers[nextIdx];
      var prevLayer = layers[lbHeroFanartActive];

      if (fa) {
        nextLayer.style.backgroundImage = 'url("' + String(fa).replace(/"/g, "%22") + '")';
        nextLayer.classList.add("on");
        prevLayer.classList.remove("on");
        hero.classList.add("has-fanart");
        lbHeroFanartActive = nextIdx;
      } else {
        nextLayer.classList.remove("on");
        prevLayer.classList.remove("on");
        hero.classList.remove("has-fanart");
      }
    }, delayMs);
  }

  /* ── commitLbRating ──────────────────────────────────────────────────────── */
  /* Persists the user's star choice to the module-scope userRatings map then
     repaints the widget.  No backend POST yet — optimistic local state only.
     TODO: persist via /api/games/<id>/rating when endpoint exists.
     (ref: BigBoxWeb/web/engine/app.js :: paintRating click handler ~line 1081) */
  function commitLbRating(gi, value) {
    var g = DATA.games[gi];
    if (!g) { return; }
    userRatings[g.id] = value;
    var ratEl = document.querySelector(".lb-panel-rating");
    if (ratEl) { paintLbRating(ratEl, gi); }
  }

  /* ── paintLbRating ───────────────────────────────────────────────────────── */
  /* Full interactive star rating widget — ported from BBW paintRating.
     rat  : the .lb-panel-rating DOM element.
     gi   : DATA.games index for the current game.
     Renders a numeric label + 5 star spans.  Stars are BLUE for community
     rating and YELLOW (.lb-rating-user) for personal (user) rating.
     Parental gate: if !canRateGame() the widget is .readonly with no handlers.
     (ref: BigBoxWeb/web/engine/app.js :: paintRating ~line 1032-1093) */
  function paintLbRating(rat, gi) {
    if (!rat) { return; }
    var g = DATA.games[gi];
    if (!g) { rat.innerHTML = ""; return; }

    /* Determine which value to display */
    var hasUser  = (userRatings[g.id] > 0) || (g.ur != null && g.ur > 0);
    var userVal  = hasUser ? (userRatings[g.id] || g.ur || 0) : 0;
    var commVal  = parseFloat(g.r) || 0;
    var rv       = hasUser ? userVal : commVal;
    var rounded  = Math.round(rv);   /* 1-5 for filled star count */

    /* Toggle user-rating class (yellow stars vs blue) */
    rat.classList.toggle("lb-rating-user", hasUser);

    /* Build inner HTML */
    var numHtml  = '<span class="lb-rnum">' + (rv > 0 ? rv.toFixed(1) : "") + "</span>";

    var starsHtml = '<span class="lb-stars">';
    for (var s = 1; s <= 5; s++) {
      starsHtml += '<span class="lb-star' + (s <= rounded ? " f" : "") + '" data-v="' + s + '">&#9733;</span>';
    }
    starsHtml += "</span>";

    /* Tooltip — wording mirrors BigBoxWeb (engine i18n rating.* keys):
       "Your Star Rating" is always shown ("None" when unrated), then the
       community average, then the vote count (only once detail.json supplies
       g.votes — omitted while still null on the light render). */
    var ttHtml = '<span class="lb-rtooltip">';
    ttHtml += "Your Star Rating: " + (hasUser ? Math.round(userVal) : "None") + "<br>";
    ttHtml += "Community Star Rating: " + commVal.toFixed(2);
    if (g.votes != null) {
      ttHtml += "<br>Total Community Star Rating Votes: " + Number(g.votes).toLocaleString();
    }
    ttHtml += "</span>";

    rat.innerHTML = numHtml + starsHtml + ttHtml;

    /* Parental gate — readonly mode: add class, skip interaction handlers */
    if (!canRateGame()) {
      rat.classList.add("readonly");
      return;
    }
    rat.classList.remove("readonly");

    /* Wire hover + click on each star span */
    var stars = rat.querySelectorAll(".lb-star");
    var starsContainer = rat.querySelector(".lb-stars");
    var numEl = rat.querySelector(".lb-rnum");

    stars.forEach(function (starEl) {
      /* Preview on mouseenter */
      starEl.addEventListener("mouseenter", function () {
        var hv = parseInt(starEl.dataset.v, 10) || 0;
        stars.forEach(function (s) {
          var sv = parseInt(s.dataset.v, 10) || 0;
          s.classList.remove("f");
          if (sv <= hv) { s.classList.add("preview"); }
          else          { s.classList.remove("preview"); }
        });
        if (numEl) { numEl.textContent = hv > 0 ? hv.toFixed(1) : ""; }
      });

      /* Commit on click */
      starEl.addEventListener("click", function (e) {
        e.stopPropagation();
        var cv = parseInt(starEl.dataset.v, 10) || 0;
        commitLbRating(gi, cv);
      });
    });

    /* Restore on mouseleave from the stars container */
    if (starsContainer) {
      starsContainer.addEventListener("mouseleave", function () {
        /* Re-render to committed state without rebuilding the full element */
        stars.forEach(function (s) {
          var sv = parseInt(s.dataset.v, 10) || 0;
          s.classList.remove("preview");
          if (sv <= rounded) { s.classList.add("f"); }
          else               { s.classList.remove("f"); }
        });
        if (numEl) { numEl.textContent = rv > 0 ? rv.toFixed(1) : ""; }
      });
    }
  }

  /* ── decodeHtmlEntities ──────────────────────────────────────────────────── */
  /* Decodes HTML entities in a string (e.g. &#39; → ').  Uses DOMParser so
     all named + numeric entities are handled without a manual replace map.
     (needed because g.d may contain HTML entities from the BBW data pipeline) */
  function decodeHtmlEntities(str) {
    if (!str) { return ""; }
    try {
      var doc = new DOMParser().parseFromString(
        "<!DOCTYPE html><html><body>" + str + "</body></html>", "text/html"
      );
      return doc.body ? doc.body.textContent : str;
    } catch (e) {
      return str;
    }
  }

  /* ── formatDate ──────────────────────────────────────────────────────────── */
  /* Formats a millisecond epoch to "M/D/YYYY".  Returns "—" if epoch <= 0.
     (mirrors date formatting used in BBW detail panels) */
  function formatDate(ms) {
    if (!ms || ms <= 0) { return "—"; }
    var d = new Date(ms);
    return (d.getMonth() + 1) + "/" + d.getDate() + "/" + d.getFullYear();
  }

  /* ── fillLbPanel ─────────────────────────────────────────────────────────── */
  /* Populates the right panel for the game at index gi.
     Called by selectCell() after .selected is applied to the grid cell.
     (mirrors BigBoxWeb/web/engine/app.js :: fillPosterSide ~line 956
              + fillPosterMedia ~line 1118) */
  function fillLbPanel(gi) {
    var g = DATA.games[gi];
    if (!g) { return; }

    /* ── 1. Reveal panel content, hide empty placeholder ──────────────────
       (BBW equivalent: poster-side is always visible once in poster mode;
        here we toggle between .lb-panel-empty and .lb-panel-content) */
    var emptyEl   = document.querySelector(".lb-panel-empty");
    var contentEl = document.querySelector(".lb-panel-content");
    if (emptyEl)   { emptyEl.style.display = "none"; }
    if (contentEl) { contentEl.removeAttribute("hidden"); }

    /* New game selected → back to the Overview tab, drop any related cache. */
    resetLbDetailTabs();

    /* ── 2. Hero fanart crossfade ─────────────────────────────────────────
       Pass the full game object so scheduleLbHeroFanart can call pickLbFanart.
       The asymmetric two-timer function handles debounce + fade-out internally.
       (ref: BigBoxWeb/web/engine/app.js :: schedulePosterFanart ~line 848) */
    scheduleLbHeroFanart(g);

    /* Store install badge (top-right of the hero): round store logo + ring,
       green = installed / orange = not. Store games only. */
    paintLbInstallBadge(g);

    /* ── 3. Clear logo ────────────────────────────────────────────────────
       Recreate inner node so css animation timeline restarts each selection.
       (ref: BigBoxWeb/web/engine/app.js :: fillPosterSide ~line 971) */
    var logoEl = document.querySelector(".lb-panel-logo");
    if (logoEl) {
      logoEl.innerHTML = "";
      if (g.logo) {
        var im = document.createElement("img");
        im.src = g.logo;
        im.alt = "";
        logoEl.appendChild(im);
      } else {
        var tx = document.createElement("span");
        tx.className = "lb-panel-logo-text";
        tx.textContent = g.t || "";
        logoEl.appendChild(tx);
      }
    }

    /* ── 4. Rating — full interactive star widget ────────────────────────
       paintLbRating mirrors BBW paintRating; handles community vs user state,
       hover preview, and click-to-rate (local optimistic commit only for now).
       (ref: BigBoxWeb/web/engine/app.js :: paintRating ~line 1032) */
    var ratEl = document.querySelector(".lb-panel-rating");
    paintLbRating(ratEl, gi);

    /* ── 5. Favorite button ───────────────────────────────────────────────
       Reflect g.fav via .on class.  onclick (not addEventListener) so
       re-assignment on each selectCell replaces the previous handler —
       no handler accumulation between game changes.
       Parental gate: if canFavGame() is false the button is rendered visible
       but non-interactive (mirrors BBW fillPosterSide ps-fav-locked ~line 998).
       (ref: BigBoxWeb/web/engine/app.js :: fillPosterSide ~line 986) */
    var favEl = document.querySelector(".lb-panel-fav");
    if (favEl) {
      favEl.classList.toggle("on", !!g.fav);
      if (!canFavGame()) {
        /* Visible but non-clickable in locked mode.
           (ref: BigBoxWeb/web/engine/app.js :: ps-fav-locked ~line 1000) */
        favEl.classList.add("lb-fav-locked");
        favEl.onclick = null;
      } else {
        favEl.classList.remove("lb-fav-locked");
        /* Use closure-captured gi so the mutation targets the right game */
        (function (capturedGi) {
          favEl.onclick = function (e) {
            e.stopPropagation();
            toggleLbFavorite(capturedGi);
          };
        })(gi);
      }
    }

    /* ── 6. Main media display ────────────────────────────────────────────
       Video preferred; fallback to screenshot images.
       (ref: BigBoxWeb/web/engine/app.js :: fillPosterMedia ~line 1118) */
    var mediaEl = document.querySelector(".lb-panel-media");
    if (mediaEl) {
      mediaEl.innerHTML = "";
      if (g.video) {
        /* (A3) Audio unlock: mute only when user has not yet unlocked audio.
           (ref: BigBoxWeb/web/engine/app.js :: unlockAudio / v.muted ~line 200) */
        var vid = document.createElement("video");
        vid.src = g.video;
        vid.autoplay = true;
        vid.muted = !lbAudioOn;
        vid.loop = false;
        vid.playsInline = true;
        vid.controls = true;
        vid.setAttribute("controlsList", "nodownload");
        vid.className = "lb-panel-media-video";
        mediaEl.appendChild(vid);
        var pr = vid.play();
        if (pr && pr.catch) {
          pr.catch(function () { vid.muted = true; vid.play().catch(function () {}); });
        }
      } else if (g.shotImg || g.shotThumb) {
        var mim = document.createElement("img");
        mim.className = "lb-panel-media-img";
        mim.src = g.shotImg || g.shotThumb;
        mim.alt = "";
        mediaEl.appendChild(mim);
      }
    }

    /* ── 7. Platform + title header ──────────────────────────────────────
       (ref: BigBoxWeb/web/engine/app.js :: fillPosterSide ~line 982) */
    var platEl  = document.querySelector(".lb-panel-plat");
    var titleEl = document.querySelector(".lb-panel-title");
    if (platEl)  { platEl.textContent  = currentPlatformName.toUpperCase(); }
    if (titleEl) { titleEl.textContent = g.t || ""; }

    /* ── 8. Info rows ─────────────────────────────────────────────────────
       (ref: BigBoxWeb/web/engine/app.js :: fillPosterSide ~line 1009) */
    var DASH = "—";

    var yearEl  = document.querySelector(".lbp-year");
    var devEl   = document.querySelector(".lbp-dev");
    var pubEl   = document.querySelector(".lbp-pub");
    var genreEl = document.querySelector(".lbp-genre");
    var esrbEl  = document.querySelector(".lbp-esrb");
    var lpEl    = document.querySelector(".lbp-lp");
    var ptEl    = document.querySelector(".lbp-pt");

    if (yearEl)  { yearEl.textContent  = g.y    || DASH; }
    if (devEl)   { devEl.textContent   = g.dev  || DASH; }
    if (pubEl)   { pubEl.textContent   = g.pub  || DASH; }
    if (genreEl) { genreEl.textContent = g.g    || DASH; }
    if (esrbEl)  { esrbEl.textContent  = g.esrb || DASH; }
    if (lpEl)    { lpEl.textContent    = formatDate(g.lp); }
    if (ptEl)    { ptEl.textContent    = (g._playtime != null) ? g._playtime : DASH; }

    /* ── 9. Thumbnails placeholder ────────────────────────────────────────
       Full thumb row (last/most/random + screenshots) is deferred.
       (ref: BigBoxWeb/web/engine/app.js :: fillPosterMedia thumbs ~line 1143) */
    var thumbsEl = document.querySelector(".lb-panel-thumbs");
    if (thumbsEl) {
      thumbsEl.innerHTML = "";
      var noMedia = document.createElement("span");
      noMedia.style.cssText = "font-size:11px;color:rgba(255,255,255,0.35);padding:0 2px;";
      noMedia.textContent = "No additional media";
      thumbsEl.appendChild(noMedia);
    }

    /* ── 10. Description ─────────────────────────────────────────────────
       Decode HTML entities (g.d may contain &#39; etc.) then set as
       textContent to prevent XSS.
       (ref: BigBoxWeb/web/engine/app.js :: fillPosterSide info block ~line 1009) */
    var descEl = document.querySelector(".lb-panel-desc");
    if (descEl) {
      descEl.textContent = g.d ? decodeHtmlEntities(g.d) : DASH;
    }
  }

  /* ══════════════════════════════════════════════════════════════════════════
     PLAY BUTTON + CARET DROPDOWN
     Mirrors BigBoxWeb/web/engine/app.js selected-version state machine
     (BBW lines 2641-2685, 2847-2883, 2962-3030, 3110-3149, 3151-3161).
     New endpoint: POST /launchbox/api/games/<id>/launch
                   GET  /launchbox/api/games/<id>/archive-entries
     ══════════════════════════════════════════════════════════════════════════ */

  /* ── Module-scope state ──────────────────────────────────────────────────── */

  /* localStorage keys — mirrors BBW SELECTED_VERSIONS_KEY / SELECTED_ROMS_KEY.
     (ref: BigBoxWeb/web/engine/app.js :: SELECTED_VERSIONS_KEY ~line 2641) */
  var LB_SEL_VER_KEY = "lbw.selectedVersions";
  var LB_SEL_ROM_KEY = "lbw.selectedRoms";

  /* Stacked menu state.  Each frame: { title, items }.
     (mirrors BBW menuStack — app.js ~line 2962 play menu builder) */
  var lbPlayMenuStack = [];
  var lbPlayMenuOpen  = false;
  var lbPlayMenuHi        = 0;     // keyboard-highlighted row (index into lbPlayMenuEls)
  var lbPlayMenuEls       = [];    // [{ el, item }] selectable rows of the current frame
  var lbPlayMenuLastFrame = null;  // reset the highlight to 0 whenever the frame changes

  /* Per-game archive-entries cache: "gameId|appId" → array of entry objects.
     Populated on first "rom_menu_open" action; invalidated when version changes.
     (ref: BBW openSelectRomMenu / lbArchiveEntriesCache) */
  var lbArchiveEntriesCache = {};

  /* Max ROM entries shown inline in the Select ROM dropdown; beyond this a
     "More…" item opens the full-list modal (#lb-rom-modal). */
  var LB_ROM_MAX = 7;

  /* Anti-double-launch client-side debounce (5 s), mirrors BBW playBlockUntilTs.
     (ref: BigBoxWeb/web/engine/app.js :: playBlockUntilTs ~line 3110) */
  var lbPlayBlockUntilTs = 0;

  /* ── Selected-version state helpers ─────────────────────────────────────── */
  /* Mirror of BBW selectedVersionsLoad/Save/get/set (app.js:2641-2685).       */

  function lbSelVerLoad() {
    try { return JSON.parse(localStorage.getItem(LB_SEL_VER_KEY) || "{}") || {}; }
    catch (_) { return {}; }
  }
  function lbSelVerSave(map) {
    try { localStorage.setItem(LB_SEL_VER_KEY, JSON.stringify(map || {})); } catch (_) {}
  }
  /* Returns the saved appId for game g, or null if default is active.
     Also purges stale entries when the version list has changed.
     (ref: BBW getSelectedVersionAppId ~line 2662) */
  function getSelectedVersionAppId(g) {
    if (!g || !g.id) { return null; }
    var map    = lbSelVerLoad();
    var stored = map[g.id];
    if (!stored) { return null; }
    var vs = (g.launchOptions && g.launchOptions.versions) || [];
    for (var i = 0; i < vs.length; i++) { if (vs[i].appId === stored) { return stored; } }
    /* Stored version no longer in DTO — purge */
    delete map[g.id];
    lbSelVerSave(map);
    return null;
  }
  function setSelectedVersionAppId(g, appId) {
    if (!g || !g.id) { return; }
    var map = lbSelVerLoad();
    if (appId == null) { delete map[g.id]; }
    else               { map[g.id] = appId; }
    lbSelVerSave(map);
  }
  /* Find version object by appId. (ref: BBW findVersion ~line 2679) */
  function lbFindVersion(g, appId) {
    var vs = (g && g.launchOptions && g.launchOptions.versions) || [];
    for (var i = 0; i < vs.length; i++) { if (vs[i].appId === appId) { return vs[i]; } }
    return null;
  }

  /* ── Selected-emulator state helpers ────────────────────────────────────── */
  /* Play is now a TWO-STEP control: picking an emulator in the caret menu only
     SELECTS it (updates the Play label + ROM-button visibility); a click on the
     main Play button launches the selected emulator. The selection is persisted
     per game so it survives navigation. Default resolution when nothing is
     explicitly stored: the last-launched emulator (server lastLaunch) if still
     valid, else the game's default emulator, else the first in the list. */
  var LB_SEL_EMU_KEY = "lbw.selEmu";
  function lbSelEmuLoad() {
    try { return JSON.parse(localStorage.getItem(LB_SEL_EMU_KEY) || "{}") || {}; }
    catch (_) { return {}; }
  }
  function lbSelEmuSave(map) {
    try { localStorage.setItem(LB_SEL_EMU_KEY, JSON.stringify(map || {})); } catch (_) {}
  }
  function lbEmus(g) { return (g && g.launchOptions && g.launchOptions.emulators) || []; }
  function lbFindEmu(g, id) {
    var es = lbEmus(g);
    for (var i = 0; i < es.length; i++) { if (es[i].id === id) { return es[i]; } }
    return null;
  }
  /* "Direct launch" sentinel: stored as the explicit pick when the user selects the
     "Launch <exe>" entry of a no-default-emulator game (a plain executable). */
  var LB_DIRECT = "__direct__";

  /* EFFECTIVE default emulator id for the CURRENT selection — the selected VERSION's own
     emulatorId when it launches through one, else the game's default. Null = DIRECT launch
     (the game/version is a plain executable). Mirrors LiteBox DefaultEmuIdForSelection. */
  function lbEffectiveDefaultEmuId(g) {
    var selVerId = getSelectedVersionAppId(g);
    var selVer   = selVerId ? lbFindVersion(g, selVerId) : null;
    if (selVer) {
      /* Tri-état : useEmulator === false → la version est un exécutable pur, son défaut
         est le lancement DIRECT (pas l'émulateur du jeu). Un DTO plus ancien (champ
         absent → undefined) garde l'ancien comportement d'héritage. */
      if (selVer.useEmulator === false) { return null; }
      if (selVer.emulatorId && lbFindEmu(g, selVer.emulatorId)) { return selVer.emulatorId; }
    }
    var es = lbEmus(g);
    for (var i = 0; i < es.length; i++) { if (es[i].isDefault) { return es[i].id; } }
    return null;
  }

  /* Resolved EFFECTIVE selected emulator id (explicit pick → last-launched pair →
     per-version default). Null = DIRECT launch: either the effective default is direct
     (nothing assigned — the platform's emulators stay offered as wrappers) or the user
     re-picked the "Launch <exe>" entry. Mirrors LiteBox ResolveInitialEmu. */
  function lbResolveEmuId(g) {
    var es  = lbEmus(g);
    var def = lbEffectiveDefaultEmuId(g);
    if (!es.length) { return null; }   // nothing offered → the host resolves (direct exe launch)
    var stored = lbSelEmuLoad()[g.id];
    if (stored === LB_DIRECT) { return def === null ? null : def; }  // explicit direct — only valid while the default IS direct
    if (stored && lbFindEmu(g, stored)) { return stored; }
    /* History pair: re-selecting the LAST-PLAYED version restores its emulator (the
       pair stays the source of truth); any other version follows its own default. */
    var selVerId = getSelectedVersionAppId(g) || null;
    var lastVer  = (g.lastLaunch && g.lastLaunch.appId) || null;
    var lastEmu  = g.lastLaunch && g.lastLaunch.emulatorId;
    if (lastEmu && selVerId === lastVer && lbFindEmu(g, lastEmu)) { return lastEmu; }
    return def;
  }
  function setSelectedEmuId(g, id) {
    if (!g || !g.id) { return; }
    var map = lbSelEmuLoad();
    map[g.id] = id;
    lbSelEmuSave(map);
  }
  /* Effective AutoExtract of the currently-selected emulator (false when none
     or when the emulator reads archives natively → ROM selection unavailable). */
  function lbSelectedEmuAutoExtract(g) {
    var emu = lbFindEmu(g, lbResolveEmuId(g));
    return !!(emu && emu.autoExtract);
  }

  /* ── Selected-ROM state helpers ─────────────────────────────────────────── */
  /* Storage shape: { gameId → { versionKey → fileName } }
     versionKey = appId or "__default__" when no version override.
     (ref: BigBoxWeb/web/engine/app.js :: SELECTED_ROMS_KEY ~line 2847) */

  function lbSelRomLoad() {
    try { return JSON.parse(localStorage.getItem(LB_SEL_ROM_KEY) || "{}") || {}; }
    catch (_) { return {}; }
  }
  function lbSelRomSave(map) {
    try { localStorage.setItem(LB_SEL_ROM_KEY, JSON.stringify(map || {})); } catch (_) {}
  }
  function _lbRomVerKey(selVer) { return selVer ? selVer.appId : "__default__"; }
  /* (ref: BBW getSelectedRomFor ~line 2872) */
  function getSelectedRomFor(g, selVer) {
    if (!g || !g.id) { return null; }
    var map = lbSelRomLoad();
    var pg  = map[g.id];
    if (!pg) { return null; }
    return pg[_lbRomVerKey(selVer)] || null;
  }
  function setSelectedRomFor(g, selVer, fileName) {
    if (!g || !g.id) { return; }
    var map = lbSelRomLoad();
    var key = _lbRomVerKey(selVer);
    if (fileName == null || fileName === "") {
      if (map[g.id]) {
        delete map[g.id][key];
        if (!Object.keys(map[g.id]).length) { delete map[g.id]; }
      }
    } else {
      if (!map[g.id]) { map[g.id] = {}; }
      map[g.id][key] = fileName;
    }
    lbSelRomSave(map);
  }

  /* ── « Force priority » flag (web ROM "Clear") — per (game, version) ───────────
     Quand il est posé : le lancement envoie forcePriority (le plugin ignore le
     dernier-joué et choisit par pure priorité) ET on supprime la ré-injection de
     la ROM depuis lastLaunch (donc le bouton reste « ROM » en revenant). */
  var LB_ROM_FORCE_KEY = "lbw.romForce";
  function lbRomForceLoad() { try { return JSON.parse(localStorage.getItem(LB_ROM_FORCE_KEY) || "{}") || {}; } catch (_) { return {}; } }
  function lbRomForceSave(m) { try { localStorage.setItem(LB_ROM_FORCE_KEY, JSON.stringify(m || {})); } catch (_) {} }
  function getRomForce(g, selVer) {
    if (!g || !g.id) { return false; }
    var pg = lbRomForceLoad()[g.id];
    return !!(pg && pg[_lbRomVerKey(selVer)]);
  }
  function setRomForce(g, selVer, on) {
    if (!g || !g.id) { return; }
    var m = lbRomForceLoad(), key = _lbRomVerKey(selVer);
    if (on) { if (!m[g.id]) { m[g.id] = {}; } m[g.id][key] = true; }
    else if (m[g.id]) { delete m[g.id][key]; if (!Object.keys(m[g.id]).length) { delete m[g.id]; } }
    lbRomForceSave(m);
  }

  /* ── postLbLaunch ────────────────────────────────────────────────────────── */
  /* Fires POST /launchbox/api/games/<id>/launch.
     Merges selected version + selected ROM into body if not overridden by extra.
     Anti-double-launch: 5 s client-side hold mirrors BBW playBlockUntilTs.
     (ref: BigBoxWeb/web/engine/app.js :: play action handler ~line 3110
           BigBoxWeb/web/engine/app.js :: postLaunch ~line 4404) */
  function postLbLaunch(extra) {
    var g = DATA.games[posterSel];
    if (!g || !g.id) { return; }
    if (!lbHttp()) { return; }

    var now = Date.now();
    if (now < lbPlayBlockUntilTs) { return; }
    lbPlayBlockUntilTs = now + 5000;

    var body = {};
    /* Merge caller overrides first, then fill from selected state */
    if (extra) {
      if (extra.emulatorId)       { body.emulatorId       = extra.emulatorId; }
      if (extra.additionalAppId)  { body.additionalAppId  = extra.additionalAppId; }
    }
    /* Selected version — only if caller did not already set additionalAppId */
    if (!body.additionalAppId) {
      var selVerId = getSelectedVersionAppId(g);
      if (selVerId) { body.additionalAppId = selVerId; }
    }
    /* Fill from the resolved selection (explicit pick → history pair → per-version
       default). Null = DIRECT launch → no emulatorId in the body; the host launches
       the plain executable itself. */
    if (!body.emulatorId) {
      var ridSel = lbResolveEmuId(g);
      if (ridSel) { body.emulatorId = ridSel; }
    }
    /* Selected ROM for the active (game, version) pair — only meaningful when
       the emulator being launched actually extracts (AutoExtract ON). For a
       native-archive emulator (MAME) we send neither the picked ROM nor the
       forcePriority flag: the whole archive goes to the emulator as-is. */
    var launchEmu  = lbFindEmu(g, body.emulatorId || lbResolveEmuId(g));
    var emuExtracts = !launchEmu || !!launchEmu.autoExtract;   // unknown emu → keep prior behaviour
    if (emuExtracts) {
      var selVerId2 = getSelectedVersionAppId(g);
      var selVer2   = selVerId2 ? lbFindVersion(g, selVerId2) : null;
      var selRom    = getSelectedRomFor(g, selVer2);
      if (selRom)   { body.archiveEntryFileName = selRom; }
      else if (getRomForce(g, selVer2)) { body.forcePriority = true; }   // ROM "Clear" → priorité pure
    }

    fetch("/launchbox/api/games/" + encodeURIComponent(String(g.id)) + "/launch", {
      method:  "POST",
      headers: { "Content-Type": "application/json" },
      body:    JSON.stringify(body)
    })
      .then(function (r) { return r.json().catch(function () { return null; }); })
      .then(function (j) {
        if (j && j.ok === false) {
          lbPlayBlockUntilTs = 0;
          console.error("[LBW] launch failed:", j.reason || "(no reason)");
        }
      })
      .catch(function () { lbPlayBlockUntilTs = 0; });
  }

  /* ── Store install: badge paint + install POST + live re-check ───────────── */

  /* Paints the top-right hero install badge for a store game (round store logo +
     green/orange ring). Hidden for non-store games. */
  function paintLbInstallBadge(g) {
    var el = document.querySelector(".lb-panel-install");
    if (!el) { return; }
    if (g && g.store) {
      var img = (g.store === "Epic") ? "EpicGames" : (g.store === "Ubisoft") ? "Uplay" : g.store;   // GOG / Steam / EpicGames / Uplay
      el.className = "lb-panel-install store-badge " + (g.installed === false ? "notinstalled" : "installed");
      el.title = (g.installed === false ? "Not installed" : "Installed") + " — " + g.store;
      el.innerHTML = '<img src="/api/badges/' + encodeURIComponent(img) + '.png" alt="' + g.store + '" onerror="this.style.display=\'none\'">';
      el.style.display = "";
    } else {
      el.className = "lb-panel-install";
      el.style.display = "none";
      el.innerHTML = "";
    }
  }

  /* POST /launchbox/api/games/<id>/install — shell-opens the store install URI. */
  function postLbInstall() {
    var g = DATA.games[posterSel];
    if (!g || !g.id || !lbHttp()) { return; }
    var now = Date.now();
    if (now < lbPlayBlockUntilTs) { return; }
    lbPlayBlockUntilTs = now + 5000;
    fetch("/launchbox/api/games/" + encodeURIComponent(String(g.id)) + "/install", {
      method: "POST", headers: { "Content-Type": "application/json" }, body: "{}"
    })
      .then(function (r) { return r.json().catch(function () { return null; }); })
      .then(function (j) { if (j && j.ok === false) { lbPlayBlockUntilTs = 0; console.error("[LBW] install failed:", j.reason || "(no reason)"); } })
      .catch(function () { lbPlayBlockUntilTs = 0; });
    /* Re-check soon so the button flips to Play once the client reports installed. */
    startLbInstallPoll(posterSel, 2500);
  }

  function stopLbInstallPoll() {
    if (lbInstallPollTimer) { clearTimeout(lbInstallPollTimer); lbInstallPollTimer = null; }
    lbInstallPollGi = -1;
  }
  function startLbInstallPoll(gi, delayMs) {
    if (!lbHttp()) { return; }
    if (lbInstallPollGi === gi && lbInstallPollTimer) { return; }   // already polling this game
    stopLbInstallPoll();
    lbInstallPollGi = gi;
    lbInstallPollTimer = setTimeout(function () { pollLbInstallOnce(gi); }, delayMs != null ? delayMs : 4000);
  }
  function pollLbInstallOnce(gi) {
    lbInstallPollTimer = null;
    if (gi !== posterSel) { stopLbInstallPoll(); return; }
    var g = DATA.games[gi];
    if (!g || g.id == null) { stopLbInstallPoll(); return; }
    fetch("/launchbox/data/games/" + encodeURIComponent(String(g.id)) + "/installstate.json", { cache: "no-store" })
      .then(function (r) { return r.ok ? r.json() : null; })
      .then(function (d) {
        if (!d || gi !== posterSel) { stopLbInstallPoll(); return; }
        if (!d.store) { stopLbInstallPoll(); return; }   // not a store game → stop
        var changed = (g.store !== d.store) || (g.installed !== d.installed);
        g.store = d.store; g.installed = d.installed;
        if (changed) { paintLbInstallBadge(g); refreshLbPlayLabel(g); }
        lbInstallPollTimer = setTimeout(function () { pollLbInstallOnce(gi); }, 4000);   // keep polling
      })
      .catch(function () { stopLbInstallPoll(); });
  }

  /* ── Reset-to-default (bouton ↺) ─────────────────────────────────────────── */
  /* Quelque chose diffère des défauts d'usine ? Version non-Base, émulateur ≠ défaut
     effectif, pick/Clear ROM, ou une entrée d'historique qui re-seederait un override
     au prochain passage. Pilote la visibilité du bouton ↺ (miroir LiteBox). */
  function lbHasResettable(g) {
    if (!g || !g.id || g.store) { return false; }
    var selVerId = getSelectedVersionAppId(g);
    if (selVerId) { return true; }
    if ((lbResolveEmuId(g) || null) !== (lbEffectiveDefaultEmuId(g) || null)) { return true; }
    var selVer = selVerId ? lbFindVersion(g, selVerId) : null;
    if (getSelectedRomFor(g, selVer) || getRomForce(g, selVer)) { return true; }
    var ll = g.lastLaunch || {};
    return !!(ll.appId || ll.emulatorId || ll.archiveEntry);
  }

  /* Restaure la version Base + l'émulateur par défaut (ou le lancement direct) + aucun
     pick ROM, ET annule l'entrée d'historique côté serveur (launch-history.db, endpoint
     resethistory) plus les picks persistés côté client (émulateur, version, ROM/force) —
     re-sélectionner le jeu réaffiche l'état par défaut pur, ici comme dans LiteBox. */
  function lbResetToDefaults(g) {
    if (!g || !g.id) { return; }
    if (lbHttp()) {
      fetch("/launchbox/api/games/" + encodeURIComponent(String(g.id)) + "/resethistory", { method: "POST" })
        .catch(function () {});
    }
    var em = lbSelEmuLoad(); delete em[g.id]; lbSelEmuSave(em);
    setSelectedVersionAppId(g, null);
    var rm = lbSelRomLoad(); delete rm[g.id]; lbSelRomSave(rm);
    var fm = lbRomForceLoad(); delete fm[g.id]; lbRomForceSave(fm);
    g.lastLaunch = null;   /* in-memory — le prochain detail.json n'en aura plus non plus */
    refreshLbPlayLabel(g);
  }

  /* ── refreshLbPlayLabel ──────────────────────────────────────────────────── */
  /* Updates the main play button label and disabled state.
     Called by selectCell, fillLbPanel, and after version/ROM changes.
     g may be null (no game selected) → disable everything.
     (ref: BBW play label pattern in buildLbPlayMenuRoot / playLeaf.label) */
  function refreshLbPlayLabel(g) {
    var playBtn   = document.getElementById("lb-panel-play");
    var caretBtn  = document.getElementById("lb-panel-play-caret");
    var playGroup = document.getElementById("lb-play-group");

    if (!playBtn || !caretBtn) { return; }

    /* Hide entire group when no game selected */
    if (!g) {
      if (playGroup) { playGroup.style.display = "none"; }
      return;
    }
    if (playGroup) { playGroup.style.display = ""; }

    var selVerId = getSelectedVersionAppId(g);
    var selVer   = selVerId ? lbFindVersion(g, selVerId) : null;

    var lo = g.launchOptions || {};

    /* Play label reflects the SELECTED emulator (two-step Play). Format:
         « Play <emu> (default) » when the selected emu IS the game's default,
         « Play <emu> »          otherwise,
         « Play »                when the game exposes no emulator list. */
    var labelEl = playBtn.querySelector(".lb-panel-play-label");
    var iconEl  = playBtn.querySelector(".lb-panel-play-icon");
    if (labelEl) {
      var defEmuId = lbEffectiveDefaultEmuId(g);
      var selEmu = lbFindEmu(g, lbResolveEmuId(g));
      if (selEmu) {
        labelEl.textContent = "Play " + selEmu.title + (selEmu.id === defEmuId ? " (default)" : "");
      } else {
        /* DIRECT launch (no emulator selected): "Launch <exe>" — or "DOSBox <exe>" when
           the game/version runs in DOSBox. Plain "Play" when the DTO predates exeName. */
        var dExe = selVer ? (selVer.exeName || "") : (lo.mainExeName || "");
        var dDos = selVer ? !!selVer.useDosBox : !!lo.mainUseDosBox;
        labelEl.textContent = dExe ? ((dDos ? "DOSBox " : "Launch ") + dExe) : "Play";
      }
      /* Store games (GOG/Steam/Epic): "Play (<store>)" when installed, or
         "Install on <store>" (↓ icon) when not — the click handler routes the
         not-installed case to postLbInstall(). */
      if (g.store) {
        if (g.installed === false) {
          labelEl.textContent = "Install on " + g.store;
          if (iconEl) { iconEl.innerHTML = "&#x2193;"; }   // ↓
        } else {
          labelEl.textContent = "Play (" + g.store + ")";
          if (iconEl) { iconEl.innerHTML = "&#x25B6;"; }   // ▶
        }
      } else if (iconEl) {
        iconEl.innerHTML = "&#x25B6;";
      }
      playBtn.title = labelEl.textContent;
    }

    /* Caret (Play) opens the alt-emulator picker → useful when there is more than one
       compatible emulator, OR when the default is DIRECT and at least one emulator is
       offered as an alternative (wrapper/launcher — mirrors LiteBox). */
    var emusN = (lo.emulators && lo.emulators.length) || 0;
    var hasAltEmu = emusN > 1 || (emusN > 0 && lbEffectiveDefaultEmuId(g) === null);
    if (hasAltEmu) { caretBtn.classList.remove("disabled"); caretBtn.disabled = false; caretBtn.style.display = ""; }
    else           { caretBtn.classList.add("disabled");    caretBtn.disabled = true;  caretBtn.style.display = "none"; }

    /* Override cue (mirrors LiteBox's lighter green): a lighter tint when the current
       selection differs from the effective default for the selected version — a user
       pick or launch-history restore is in effect. Not for store games. */
    var isOverride = !g.store && ((lbResolveEmuId(g) || null) !== (lbEffectiveDefaultEmuId(g) || null));
    playBtn.classList.toggle("lb-play-override", isOverride);
    caretBtn.classList.toggle("lb-play-override", isOverride);

    /* ↺ reset-to-default : visible seulement quand quelque chose diffère des défauts. */
    var resetBtn = document.getElementById("lb-panel-play-reset");
    if (resetBtn) { resetBtn.hidden = !lbHasResettable(g); }

    /* « Version » button — only when alternative versions exist. Selection-only.
       Default selected → plain "Version" + muted ; an alt → "Version: X". */
    var verBtn = document.getElementById("lb-panel-version");
    if (verBtn) {
      var versions = lo.versions || [];
      if (versions.length > 0) {
        verBtn.textContent = selVer ? ("Version: " + selVer.label) : "Version";
        verBtn.title = verBtn.textContent;
        verBtn.classList.toggle("muted", !selVer);
        verBtn.removeAttribute("hidden");
      } else {
        verBtn.setAttribute("hidden", "");
      }
    }

    /* « ROM » button — only when the current launch source is an archive AND
       the selected emulator actually extracts (AutoExtract ON). When the
       emulator reads archives natively (e.g. MAME / arcade), a specific
       in-archive ROM can't be launched, so the ROM button is hidden.
       Nothing picked → plain "ROM" + muted ; a pick → "ROM: X". */
    var romBtn = document.getElementById("lb-panel-rom");
    if (romBtn) {
      var isCurrentArchive = (selVer ? !!selVer.isArchive : !!lo.mainPathIsArchive) && lbSelectedEmuAutoExtract(g);
      if (isCurrentArchive) {
        var selRom = getSelectedRomFor(g, selVer);
        romBtn.textContent = selRom ? ("ROM: " + selRom.split(/[\\/]/).pop()) : "ROM";
        romBtn.title = romBtn.textContent;
        romBtn.classList.toggle("muted", !selRom);
        romBtn.removeAttribute("hidden");
      } else {
        romBtn.setAttribute("hidden", "");
      }
    }
  }

  /* ── buildLbPlayMenuRoot ─────────────────────────────────────────────────── */
  /* Returns the root-level menu items array for game g.
     (mirrors BBW play menu builder ~line 2962) */
  function buildLbPlayMenuRoot(g) {
    var items = [];
    if (!g || !g.launchOptions) { return items; }

    var lo      = g.launchOptions;
    var selVerId = getSelectedVersionAppId(g);
    var selVer   = selVerId ? lbFindVersion(g, selVerId) : null;
    var selRom   = getSelectedRomFor(g, selVer);
    var versions = lo.versions || [];
    var emus     = lo.emulators || [];

    /* Play caret menu = emulator SELECTION (two-step Play). Picking an emulator
       only selects it (updates the Play label + ROM-button visibility); the
       launch happens on the main Play button. We list ALL emulators (default
       first, as the server orders them) and bullet the currently-selected one
       (resolved: explicit pick → last launched → default → first). The default
       emulator is tagged "(default)". */
    var selEmuId = lbResolveEmuId(g);
    var defEmuId = lbEffectiveDefaultEmuId(g);

    /* Exe-based game/version (no default emulator): DIRECT LAUNCH heads the menu as the
       default — the platform's emulators stay below as alternatives (wrappers/launchers).
       Mirrors LiteBox ShowEmulatorMenu. */
    if (defEmuId === null && emus.length) {
      var dExe = selVer ? (selVer.exeName || "") : (lo.mainExeName || "");
      items.push({
        label:      (selEmuId === null ? "● " : "") + (dExe ? "Launch " + dExe : "Launch directly") + " (default)",
        action:     "select_emulator",
        emulatorId: LB_DIRECT,
      });
    }
    for (var i = 0; i < emus.length; i++) {
      var e = emus[i];
      items.push({
        /* "(default)" tags the PER-SELECTION default (a version carries its own emulator),
           not the game-level flag. */
        label:      (e.id === selEmuId ? "● " : "") + e.title + (e.id === defEmuId ? " (default)" : ""),
        action:     "select_emulator",
        emulatorId: e.id,
      });
    }

    return items;
  }

  /* ── buildLbVersionSubmenu ───────────────────────────────────────────────── */
  /* Returns version-selection items + a Back item.
     (mirrors BBW versionChildren builder ~line 2985) */
  function buildLbVersionSubmenu(g) {
    var items    = [];
    var lo       = g && g.launchOptions;
    var versions = (lo && lo.versions) || [];
    var selVerId = getSelectedVersionAppId(g);

    /* Pas de « Clear » ici : « Base : … » joue déjà ce rôle (= défaut). */
    for (var i = 0; i < versions.length; i++) {
      var v        = versions[i];
      var isActive = selVerId ? (v.appId === selVerId) : !!v.isDefault;
      items.push({
        label:     v.label,
        active:    isActive,
        action:    "select_version",
        appId:     v.appId,
        isDefault: !!v.isDefault,
      });
    }
    items.push({ label: "Back", action: "submenu_back" });
    return items;
  }

  /* ── buildLbRomSubmenu ───────────────────────────────────────────────────── */
  /* Returns ROM-selection items from cache, or triggers a lazy fetch and
     returns a Loading placeholder in the meantime.
     (mirrors BBW openSelectRomMenu lazy-fetch pattern ~line 2711) */
  function buildLbRomSubmenu(g) {
    var selVerId  = getSelectedVersionAppId(g);
    var selVer    = selVerId ? lbFindVersion(g, selVerId) : null;
    var selRom    = getSelectedRomFor(g, selVer);
    var cacheKey  = g.id + "|" + (selVerId || "default");

    if (lbArchiveEntriesCache[cacheKey]) {
      var entries = lbArchiveEntriesCache[cacheKey];
      var items   = [];
      /* « Clear » en tête : enlève la sélection ROM (→ résolution auto). */
      items.push({ label: "✕ Clear", action: "select_rom", romName: "" });

      /* Composition : la DERNIÈRE ROM lancée, puis TOUS les favoris (★), puis
         jusqu'à LB_ROM_MAX ROMs en pure priorité. Au-delà → « More… ».
         (Les entrées arrivent triées du serveur : dernier-joué → favori →
         priorité → alpha, avec isFavorite / isLastPlayed.) */
      var seen = {};
      /* Identity = in-archive path (fallback basename). Label stays the basename EXCEPT entries whose
         basename is duplicated within this archive — those show the path to disambiguate. */
      var dup = {}; (function () { var c = {}; for (var di = 0; di < entries.length; di++) { var dn = entries[di].fileName || ""; c[dn] = (c[dn] || 0) + 1; if (c[dn] > 1) { dup[dn] = 1; } } })();
      var pushRom = function (e) {
        if (!e) { return; }
        var name = e.fileName || String(e);
        var key  = e.pathInArchive || name;
        if (seen[key]) { return; }
        seen[key] = 1;
        /* Marqueurs CUMULÉS, dans l'ordre : ↻ (dernière lancée) ★ (favori) 🏆 (RetroAchievements). */
        var prefix = "";
        if (e.isLastPlayed || (lastName && key === lastName)) { prefix += "↻ "; }
        if (e.isFavorite)        { prefix += "★ "; }
        if (e.retroAchievements) { prefix += "🏆 "; }
        items.push({
          label:   prefix + (dup[name] ? key : name),
          active:  selRom === key,
          action:  "select_rom",
          romName: key,
        });
      };

      /* 1. dernière ROM lancée (lastLaunch, sinon la + récemment jouée). */
      var lastName = (g.lastLaunch && g.lastLaunch.archiveEntry) || null, lastEntry = null;
      for (var i = 0; i < entries.length; i++) {
        if (lastName && ((entries[i].pathInArchive || entries[i].fileName) === lastName || entries[i].fileName === lastName)) { lastEntry = entries[i]; break; }
      }
      if (!lastEntry) { for (var j = 0; j < entries.length; j++) { if (entries[j].isLastPlayed) { lastEntry = entries[j]; break; } } }
      pushRom(lastEntry);

      /* 2. tous les favoris. */
      for (var f = 0; f < entries.length; f++) { if (entries[f].isFavorite) { pushRom(entries[f]); } }

      /* 3. jusqu'à LB_ROM_MAX de plus, par score. Les favoris sont déjà affichés et la seule ROM
         épinglée (dernière lancée) est déjà dans `seen` — mais on ne saute PAS les AUTRES recently-played
         ici, sinon une recently-played haut-score (ex. le pick RA) disparaîtrait dans « More… ». */
      var prioAdded = 0;
      for (var p = 0; p < entries.length && prioAdded < LB_ROM_MAX; p++) {
        var e = entries[p];
        if (e.isFavorite) { continue; }
        if (seen[e.pathInArchive || e.fileName || String(e)]) { continue; }
        pushRom(e); prioAdded++;
      }

      if (entries.length > Object.keys(seen).length) {
        items.push({ label: "More… (" + entries.length + ")", action: "rom_more" });
      }
      items.push({ label: "Back", action: "submenu_back" });
      return items;
    }

    /* Not cached yet — start a lazy fetch and return a Loading... placeholder */
    _lbFetchRomEntries(g, selVer, selVerId, cacheKey);
    return [
      { label: "Loading…", action: "noop" },
      { label: "Back",          action: "submenu_back" },
    ];
  }

  function _lbFetchRomEntries(g, selVer, selVerId, cacheKey) {
    var qs  = selVerId ? ("?appId=" + encodeURIComponent(selVerId)) : "";
    var url = "/launchbox/api/games/" + encodeURIComponent(String(g.id)) + "/archive-entries" + qs;
    fetch(url)
      .then(function (r) { return r.json().catch(function () { return null; }); })
      .then(function (res) {
        if (!res || !res.ok) {
          console.warn("[LBW] archive-entries failed:", res && res.reason);
          return;
        }
        var raw = res.entries || [];
        lbArchiveEntriesCache[cacheKey] = raw;
        /* If the ROM submenu is still at the top of the stack, re-render */
        if (lbPlayMenuOpen && lbPlayMenuStack.length > 0) {
          var top = lbPlayMenuStack[lbPlayMenuStack.length - 1];
          if (top && top.submenuKey === "rom") {
            lbPlayMenuStack[lbPlayMenuStack.length - 1] = {
              title:       top.title,
              submenuKey:  "rom",
              items:       buildLbRomSubmenu(g),
            };
            renderLbPlayMenu();
          }
        }
      })
      .catch(function (err) {
        console.warn("[LBW] archive-entries error:", err);
      });
  }

  /* ── openLbPlayMenu / closeLbPlayMenu / toggleLbPlayMenu ─────────────────── */
  function openLbPlayMenu() {
    var g = DATA.games[posterSel];
    if (!g) { return; }
    var rootItems = buildLbPlayMenuRoot(g);
    if (rootItems.length === 0) { return; }   /* nothing to show */
    lbPlayMenuStack = [{ title: "Launch Options", items: rootItems }];
    lbPlayMenuOpen  = true;
    renderLbPlayMenu();
    var menu = document.getElementById("lb-panel-play-menu");
    if (menu) { menu.removeAttribute("hidden"); }
  }

  /* Open the shared dropdown seeded directly with the version list (opened from
     the standalone « Version » button). Selection-only — see select_version. */
  function openLbVersionMenu() {
    var g = DATA.games[posterSel];
    if (!g) { return; }
    lbPlayMenuStack = [{ title: "Select Version", submenuKey: "version", items: buildLbVersionSubmenu(g) }];
    lbPlayMenuOpen  = true;
    renderLbPlayMenu();
    var menu = document.getElementById("lb-panel-play-menu");
    if (menu) { menu.removeAttribute("hidden"); }
  }

  /* Same, seeded with the ROM list (lazy fetch inside buildLbRomSubmenu). */
  function openLbRomMenu() {
    var g = DATA.games[posterSel];
    if (!g) { return; }
    lbPlayMenuStack = [{ title: "Select ROM", submenuKey: "rom", items: buildLbRomSubmenu(g) }];
    lbPlayMenuOpen  = true;
    renderLbPlayMenu();
    var menu = document.getElementById("lb-panel-play-menu");
    if (menu) { menu.removeAttribute("hidden"); }
  }

  function closeLbPlayMenu() {
    lbPlayMenuOpen = false;
    lbPlayMenuStack = [];
    var menu = document.getElementById("lb-panel-play-menu");
    if (menu) { menu.setAttribute("hidden", ""); }
  }

  /* ── ROM "More" modal — table triable + recherche (liste complète) ─────────── */
  var _lbRomModalState = null;
  function openLbRomModal(g) {
    if (!g) { return; }
    var selVerId = getSelectedVersionAppId(g);
    var selVer   = selVerId ? lbFindVersion(g, selVerId) : null;
    var selRom   = getSelectedRomFor(g, selVer);
    var cacheKey = g.id + "|" + (selVerId || "default");
    var entries  = lbArchiveEntriesCache[cacheKey] || [];
    _lbRomModalState = { g: g, selVer: selVer, selVerId: selVerId, selRom: selRom, entries: entries, sortCol: "", sortAsc: true, query: "" };
    renderLbRomModalList();
    /* Aperçu initial : la ROM active (ou la première). */
    var first = selRom || (entries[0] && (entries[0].fileName || String(entries[0]))) || "";
    _lbRomMetaShow(g, selVerId, first);
    var modal = document.getElementById("lb-rom-modal");
    if (modal) { modal.removeAttribute("hidden"); }
  }

  function _lbFmtBytes(b) {
    if (!b) { return ""; }
    if (b >= 1048576) { return (b / 1048576).toFixed(0) + " MB"; }
    if (b >= 1024)    { return (b / 1024).toFixed(0) + " KB"; }
    return b + " B";
  }
  function _lbRomSort(rows, col, asc) {
    if (!col) { return rows; }   // "" = ordre serveur (priorité)
    var out = rows.slice();
    if (col === "name")      { out.sort(function (a, b) { return (a.fileName || "").localeCompare(b.fileName || ""); }); }
    else if (col === "size") { out.sort(function (a, b) { return (a.size || 0) - (b.size || 0); }); }
    else if (col === "fav")  { out.sort(function (a, b) { return (b.isFavorite ? 1 : 0) - (a.isFavorite ? 1 : 0); }); }
    else if (col === "ra")   { out.sort(function (a, b) { return (a.retroAchievements || "").localeCompare(b.retroAchievements || ""); }); }
    if (!asc) { out.reverse(); }
    return out;
  }
  function renderLbRomModalList() {
    var st = _lbRomModalState; if (!st) { return; }
    var list = document.getElementById("lb-rom-modal-list");
    if (!list) { return; }
    list.innerHTML = "";
    /* Recherche PERSISTANTE : à chaque frappe on ne reconstruit que la table
       (sinon l'input perdrait le focus). */
    var wrap = document.createElement("div"); wrap.className = "lb-rom-search-wrap";
    var search = document.createElement("input");
    search.className = "lb-rom-search"; search.type = "text"; search.placeholder = "Search (wildcards * ?)…"; search.value = st.query;
    search.addEventListener("input", function () { st.query = search.value; renderLbRomTable(); });
    wrap.appendChild(search); list.appendChild(wrap);
    var tc = document.createElement("div"); tc.id = "lb-rom-table-container"; list.appendChild(tc);
    renderLbRomTable();
  }
  /* Client-side wildcard filter (no API call): a plain query is a
     case-insensitive substring; a query with * or ? becomes a glob matched
     with "contains" semantics (* = any run, ? = one char). Escapes every
     other regex metachar so literal ROM tags like [!] / (USA) match as text. */
  function lbWildcardMatch(name, query) {
    if (!query) { return true; }
    name = name || "";
    if (query.indexOf("*") < 0 && query.indexOf("?") < 0) {
      return name.toLowerCase().indexOf(query.toLowerCase()) >= 0;
    }
    var rx = query.replace(/[.+^${}()|[\]\\]/g, "\\$&").replace(/\*/g, ".*").replace(/\?/g, ".");
    try { return new RegExp(rx, "i").test(name); }
    catch (_) { return name.toLowerCase().indexOf(query.toLowerCase()) >= 0; }
  }
  /* Toggle an in-archive entry favourite (server write). Invalidates the
     cached entry list for this game so the Play ▸ ROM dropdown reflects the
     change on its next open. cb(ok) fires after the round-trip. */
  function lbToggleRomFav(g, appId, entry, value, cb) {
    if (!g || !g.id) { if (cb) cb(false); return; }
    var body = { entry: entry, value: !!value };
    if (appId) { body.appId = appId; }
    fetch("/launchbox/api/games/" + encodeURIComponent(String(g.id)) + "/archive-favorite", {
      method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body)
    })
      .then(function (r) { return r.json().catch(function () { return null; }); })
      .then(function (j) {
        var ok = !!(j && j.ok);
        if (ok) {
          var prefix = g.id + "|";
          Object.keys(lbArchiveEntriesCache).forEach(function (k) {
            if (k.indexOf(prefix) === 0) { delete lbArchiveEntriesCache[k]; }
          });
        }
        if (cb) cb(ok);
      })
      .catch(function () { if (cb) cb(false); });
  }
  function renderLbRomTable() {
    var st = _lbRomModalState; if (!st) { return; }
    var tc = document.getElementById("lb-rom-table-container"); if (!tc) { return; }
    tc.innerHTML = "";

    var q = st.query || "";
    var rows = st.entries.filter(function (e) { return lbWildcardMatch(e.fileName || "", q); });
    rows = _lbRomSort(rows, st.sortCol, st.sortAsc);

    var table = document.createElement("table"); table.className = "lb-rom-table";
    var thead = document.createElement("thead"); var htr = document.createElement("tr");
    [{ k: "fav", label: "★" }, { k: "name", label: "Name" }, { k: "size", label: "Size" }, { k: "ra", label: "RetroAchievements" }].forEach(function (c) {
      var th = document.createElement("th"); th.className = "c-" + c.k;
      th.textContent = c.label + (st.sortCol === c.k ? (st.sortAsc ? " ▲" : " ▼") : "");
      th.addEventListener("click", function () {
        if (st.sortCol === c.k) { st.sortAsc = !st.sortAsc; }
        else { st.sortCol = c.k; st.sortAsc = (c.k !== "size"); }
        renderLbRomTable();
      });
      htr.appendChild(th);
    });
    thead.appendChild(htr); table.appendChild(thead);

    var tbody = document.createElement("tbody");
    var dup = {}; (function () { var c = {}; (st.entries || []).forEach(function (x) { var n = x.fileName || ""; c[n] = (c[n] || 0) + 1; if (c[n] > 1) { dup[n] = 1; } }); })();
    rows.forEach(function (e) {
      var name = e.fileName || String(e);
      var key  = e.pathInArchive || name;
      var tr = document.createElement("tr"); tr.className = "lb-rom-trow" + (st.selRom === key ? " active" : "");
      var tdFav = document.createElement("td");
      tdFav.className = "c-fav lb-rom-fav" + (e.isFavorite ? " on" : "");
      tdFav.textContent = e.isFavorite ? "★" : "☆";
      tdFav.title = e.isFavorite ? "Unset favorite" : "Set favorite";
      tdFav.addEventListener("click", function (ev) {
        ev.stopPropagation();   /* don't select/launch the row */
        var nv = !e.isFavorite;
        lbToggleRomFav(st.g, st.selVerId, key, nv, function (ok) {
          if (ok) { e.isFavorite = nv; renderLbRomTable(); }
        });
      });
      var tdName = document.createElement("td"); tdName.className = "c-name"; tdName.textContent = dup[name] ? key : name; tdName.title = key;
      var tdSize = document.createElement("td"); tdSize.className = "c-size"; tdSize.textContent = _lbFmtBytes(e.size || 0);
      var tdRa = document.createElement("td"); tdRa.className = "c-ra"; tdRa.textContent = e.retroAchievements || ""; tdRa.title = e.retroAchievements || "";
      tr.appendChild(tdFav); tr.appendChild(tdName); tr.appendChild(tdSize); tr.appendChild(tdRa);
      tr.addEventListener("click", function () {
        setSelectedRomFor(st.g, st.selVer, key);
        setRomForce(st.g, st.selVer, false);
        closeLbRomModal();
        closeLbPlayMenu();
        refreshLbPlayLabel(st.g);
      });
      tr.addEventListener("mouseenter", function () { _lbRomMetaShow(st.g, st.selVerId, key); });
      tbody.appendChild(tr);
    });
    table.appendChild(tbody); tc.appendChild(table);
  }

  function closeLbRomModal() {
    var modal = document.getElementById("lb-rom-modal");
    if (modal) { modal.setAttribute("hidden", ""); modal.classList.remove("has-meta"); }
  }

  /* ── ROM "More" modal : aperçu métadonnées (template hackset) ─────────────────
     Reprend l'API + le rendu iframe de BigBox (engine/app.js :: updateArchive
     MetaOverlay / _archiveMetaSetHtml). Cache par (gameId, appId, entry). */
  var _lbRomMetaCache    = Object.create(null);
  var _lbRomMetaInflight = null;
  function _lbRomMetaSet(html, entry) {
    var ifr = document.getElementById("lb-rom-modal-meta");
    if (!ifr) { return; }
    /* Élargit la modale dès qu'on a des métadonnées (et n'enlève pas la classe
       ensuite, pour éviter un redimensionnement saccadé au survol). */
    if (html) { var modal = document.getElementById("lb-rom-modal"); if (modal) modal.classList.add("has-meta"); }
    if (!html) { ifr.srcdoc = ""; return; }
    /* Injecte window.SELECTED_ROM (+ hash) comme BigBox pour les templates. */
    var safeEntry = JSON.stringify(entry || "");
    var inj = "<scr" + "ipt>window.SELECTED_ROM=" + safeEntry
            + ";try{window.location.hash=encodeURIComponent(window.SELECTED_ROM);}catch(_){}</scr" + "ipt>";
    var src = html;
    if (/<head[^>]*>/i.test(src))      src = src.replace(/<head([^>]*)>/i, "<head$1>" + inj);
    else if (/<body[^>]*>/i.test(src)) src = src.replace(/<body([^>]*)>/i, "<body$1>" + inj);
    else                                src = inj + src;
    ifr.srcdoc = src;
  }
  function _lbRomMetaShow(g, appId, entry) {
    if (!g || !entry) { return; }
    var key = g.id + "|" + (appId || "") + "|" + entry;
    var cached = _lbRomMetaCache[key];
    if (cached != null) { _lbRomMetaSet(cached || "", entry); return; }
    try { if (_lbRomMetaInflight) _lbRomMetaInflight.abort(); } catch (_) {}
    var ac = (typeof AbortController !== "undefined") ? new AbortController() : null;
    _lbRomMetaInflight = ac;
    var url = "/bigbox/api/games/" + encodeURIComponent(g.id) + "/archive-metadata"
            + "?entry=" + encodeURIComponent(entry)
            + (appId ? "&appId=" + encodeURIComponent(appId) : "");
    fetch(url, ac ? { signal: ac.signal } : undefined)
      .then(function (r) { return r.json(); })
      .then(function (res) {
        var html = (res && res.ok && typeof res.html === "string") ? res.html : "";
        _lbRomMetaCache[key] = html;
        _lbRomMetaSet(html, entry);
      })
      .catch(function () {});
  }

  /* Wire the ROM modal's close affordances once (close button, backdrop, Esc). */
  function setupLbRomModal() {
    var closeBtn = document.getElementById("lb-rom-modal-close");
    if (closeBtn) { closeBtn.addEventListener("click", closeLbRomModal); }
    var backdrop = document.getElementById("lb-rom-modal-backdrop");
    if (backdrop) { backdrop.addEventListener("click", closeLbRomModal); }
    document.addEventListener("keydown", function (e) {
      if (e.key !== "Escape") { return; }
      var modal = document.getElementById("lb-rom-modal");
      if (modal && !modal.hasAttribute("hidden")) { closeLbRomModal(); }
    });

    /* Séparateur draggable liste | aperçu (20–80 %). On coupe les events de
       l'iframe pendant le drag, sinon elle avale les mousemove. */
    var divider = document.getElementById("lb-rom-modal-divider");
    var split   = document.querySelector("#lb-rom-modal .lb-rom-modal-split");
    var listEl  = document.getElementById("lb-rom-modal-list");
    var ifrEl   = document.getElementById("lb-rom-modal-meta");
    if (divider && split && listEl) {
      var dragging = false;
      divider.addEventListener("mousedown", function (e) {
        dragging = true; e.preventDefault();
        document.body.style.userSelect = "none";
        if (ifrEl) { ifrEl.style.pointerEvents = "none"; }
      });
      document.addEventListener("mousemove", function (e) {
        if (!dragging) { return; }
        var rect = split.getBoundingClientRect();
        var pct = ((e.clientX - rect.left) / rect.width) * 100;
        pct = Math.max(20, Math.min(80, pct));
        listEl.style.flexBasis = pct + "%";
      });
      document.addEventListener("mouseup", function () {
        if (!dragging) { return; }
        dragging = false;
        document.body.style.userSelect = "";
        if (ifrEl) { ifrEl.style.pointerEvents = ""; }
      });
    }
  }

  function toggleLbPlayMenu() {
    if (lbPlayMenuOpen) { closeLbPlayMenu(); }
    else                { openLbPlayMenu();  }
  }

  /* ── renderLbPlayMenu ────────────────────────────────────────────────────── */
  /* Paints the current top-of-stack menu frame into #lb-panel-play-menu.
     Each item dispatched by action; active items get .active class (bullet). */
  function renderLbPlayMenu() {
    var menu = document.getElementById("lb-panel-play-menu");
    if (!menu) { return; }

    menu.innerHTML = "";

    if (!lbPlayMenuStack.length) { menu.setAttribute("hidden", ""); return; }
    var frame = lbPlayMenuStack[lbPlayMenuStack.length - 1];
    var g     = DATA.games[posterSel];
    if (frame !== lbPlayMenuLastFrame) { lbPlayMenuHi = 0; lbPlayMenuLastFrame = frame; }
    lbPlayMenuEls = [];

    /* Header */
    if (frame.title) {
      var hdr = document.createElement("div");
      hdr.className = "lb-play-menu-header";
      hdr.textContent = frame.title;
      menu.appendChild(hdr);
    }

    /* Items */
    for (var i = 0; i < frame.items.length; i++) {
      (function (item) {
        if (item.action === "submenu_back") {
          var backEl = document.createElement("div");
          backEl.className = "lb-play-menu-back";
          backEl.textContent = item.label || "Back";
          backEl.addEventListener("click", function (e) {
            e.stopPropagation();
            lbPlayMenuStack.pop();
            if (lbPlayMenuStack.length === 0) { closeLbPlayMenu(); }
            else { renderLbPlayMenu(); }
          });
          var bidx = lbPlayMenuEls.length;
          backEl.addEventListener("mouseenter", function () { lbPlayMenuHi = bidx; lbPlayMenuPaintHi(); });
          lbPlayMenuEls.push({ el: backEl, item: item });
          menu.appendChild(backEl);
          return;
        }

        var el = document.createElement("div");
        el.className = "lb-play-menu-item" + (item.active ? " active" : "");
        el.textContent = item.label || "";
        el.title       = item.label || "";

        el.addEventListener("click", function (ev) {
          ev.stopPropagation();
          _lbMenuDispatch(item, g);
        });

        /* Selectable (keyboard-navigable) unless it's a pure placeholder. */
        if (item.action !== "noop") {
          var idx = lbPlayMenuEls.length;
          el.addEventListener("mouseenter", function () { lbPlayMenuHi = idx; lbPlayMenuPaintHi(); });
          lbPlayMenuEls.push({ el: el, item: item });
        }

        menu.appendChild(el);
      })(frame.items[i]);
    }

    if (lbPlayMenuHi >= lbPlayMenuEls.length) { lbPlayMenuHi = Math.max(0, lbPlayMenuEls.length - 1); }
    lbPlayMenuPaintHi();
  }

  /* ── Play-menu keyboard highlight ─────────────────────────────────────────── */
  function lbPlayMenuPaintHi() {
    for (var i = 0; i < lbPlayMenuEls.length; i++) {
      lbPlayMenuEls[i].el.style.background = (i === lbPlayMenuHi) ? "rgba(120,150,210,0.30)" : "";
    }
    var cur = lbPlayMenuEls[lbPlayMenuHi];
    if (cur && cur.el.scrollIntoView) { try { cur.el.scrollIntoView({ block: "nearest" }); } catch (e) {} }
  }
  function lbPlayMenuMove(d) {
    if (!lbPlayMenuEls.length) { return; }
    lbPlayMenuHi = (lbPlayMenuHi + d + lbPlayMenuEls.length) % lbPlayMenuEls.length;
    lbPlayMenuPaintHi();
  }
  function lbPlayMenuActivate() {
    var cur = lbPlayMenuEls[lbPlayMenuHi];
    if (cur) { _lbMenuDispatch(cur.item, DATA.games[posterSel]); }
  }

  /* ── _lbMenuDispatch ─────────────────────────────────────────────────────── */
  /* Routes a click on a menu item to the appropriate action.
     (mirrors BBW detailDescend action dispatch ~line 3110-3161) */
  function _lbMenuDispatch(item, g) {
    switch (item.action) {

      case "select_emulator":
        if (!g) { break; }
        setSelectedEmuId(g, item.emulatorId);
        /* Selection-only: close the picker and refresh the 3 buttons (Play
           label, and ROM visibility which depends on the emulator's AutoExtract). */
        closeLbPlayMenu();
        refreshLbPlayLabel(g);
        break;

      case "open_submenu":
        if (!g) { break; }
        if (item.submenuKey === "version") {
          var verItems = buildLbVersionSubmenu(g);
          lbPlayMenuStack.push({ title: "Select Version", submenuKey: "version", items: verItems });
          renderLbPlayMenu();
        } else if (item.submenuKey === "rom") {
          /* buildLbRomSubmenu triggers a lazy fetch if needed, returns placeholder */
          var romItems = buildLbRomSubmenu(g);
          lbPlayMenuStack.push({ title: "Select ROM", submenuKey: "rom", items: romItems });
          renderLbPlayMenu();
        }
        break;

      case "select_version":
        if (!g) { break; }
        setSelectedVersionAppId(g, item.isDefault ? null : item.appId);
        /* Invalidate the cached ROM list for this game — archive flag may change */
        var prefix = g.id + "|";
        Object.keys(lbArchiveEntriesCache).forEach(function (k) {
          if (k.indexOf(prefix) === 0) { delete lbArchiveEntriesCache[k]; }
        });
        /* Selection-only: close the picker and refresh the 3 buttons (Play label,
           Version label, and ROM button visibility which depends on the version). */
        closeLbPlayMenu();
        refreshLbPlayLabel(g);
        break;

      case "select_rom":
        if (!g) { break; }
        var sVerId = getSelectedVersionAppId(g);
        var sVer   = sVerId ? lbFindVersion(g, sVerId) : null;
        setSelectedRomFor(g, sVer, item.romName);
        setRomForce(g, sVer, !item.romName);   // « Clear » (romName vide) → force priorité ; vrai choix → annule
        closeLbPlayMenu();
        refreshLbPlayLabel(g);
        break;

      case "rom_more":
        /* Open the full-list modal; close the dropdown behind it. */
        openLbRomModal(g);
        closeLbPlayMenu();
        break;

      case "submenu_back":
        lbPlayMenuStack.pop();
        if (lbPlayMenuStack.length === 0) { closeLbPlayMenu(); }
        else { renderLbPlayMenu(); }
        break;

      case "noop":
      default:
        break;
    }
  }

  /* ── toggleLbFavorite ────────────────────────────────────────────────────── */
  /* Toggles g.fav locally and POST-mutates the server (best-effort / fire-and-forget).
     Parental gate: if canFavGame() returns false, the toggle is silently blocked.
     The UI already hides/disables the button via fillLbPanel, but this is a
     belt-and-suspenders server-side guard mirror.
     (mirrors BigBoxWeb/web/engine/app.js :: togglePosterFavorite ~line 1107
              + postMutation ~line 4389
              + canFavGame gate in fillPosterSide ~line 998) */
  function toggleLbFavorite(gi) {
    /* Parental gate — (ref: BigBoxWeb/web/engine/app.js :: canFavGame ~line 998) */
    if (!canFavGame()) { return; }
    var g = DATA.games[gi];
    if (!g) { return; }
    g.fav = !g.fav;
    var favEl = document.querySelector(".lb-panel-fav");
    if (favEl) { favEl.classList.toggle("on", !!g.fav); }

    /* ── Propagate fav mutation into the games cache ─────────────────────────
       Because lbGamesCache stores a shallow-copied array of the same game
       objects that populate DATA.games (both arrays hold the same object
       references), the g.fav assignment above already mutates the cached
       object in place.  The explicit update below is therefore defensive:
       it guards against the edge case where the cache was populated from an
       earlier .slice() that created NEW object references (e.g. if we
       ever switch from shared-reference to deep-copy semantics).

       Defensive preconditions before touching the cache:
         1. lbCurrentPlatformPath must be set (it is set at the top of
            loadPlatform before each render, so it always reflects the path
            whose games are in DATA.games).
         2. A cache entry for that path must exist.
         3. The cached array length must match DATA.games.length (guards
            against a stale path surviving a platform switch in a race).
         4. The game id at index gi must match (guards against g being from
            a different render if selection and mutation races somehow diverge).

       TODO: when a userRating mutation endpoint is wired in, apply the same
       pattern here for g.ur (currently userRatings is local-only per audit).
       (ref: req §4) */
    var cachePath = lbCurrentPlatformPath;
    var cached    = cachePath && lbGamesCache[cachePath];
    if (cached &&
        cached.games.length === DATA.games.length &&
        cached.games[gi] &&
        cached.games[gi].id === g.id) {
      cached.games[gi].fav = g.fav;
    }

    /* Fire-and-forget mutation — mirrors BBW postMutation("favorite", ...).
       No-op when id is absent or not running over HTTP.
       (ref: BigBoxWeb/web/engine/app.js :: postMutation ~line 4389) */
    if (g.id != null &&
        (location.protocol === "http:" || location.protocol === "https:")) {
      try {
        fetch("/bigbox/api/games/" + g.id + "/favorite", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ value: !!g.fav })
        }).catch(function () {});
      } catch (e) {}
    }
  }

})();
