/* ============================================================================
   LaunchBox Web — config.js
   ----------------------------------------------------------------------------
   Completely independent configuration infrastructure for the /launchbox/
   frontend. Namespaced under window.LBW (NOT window.BBW).

   Cookie : 'lbw_cfg'  (NOT 'bbw_cfg' — independent from BigBoxWeb settings).

   Structure:
     window.LBW.configDefaults — frozen deep clone of initial defaults
     window.LBW.config         — live config; receives user overrides
     window.LBW.configSchema   — empty array; settings modal populates later
     window.LBW.cfg            — { _ov, get, def, set, reset, save }

   Pattern modelled on BigBoxWeb/web/engine/config.js but uses window.LBW
   and cookie 'lbw_cfg' throughout.

   This file MUST be loaded BEFORE app.js.
   ============================================================================ */
(function () {
  "use strict";

  window.LBW = window.LBW || {};

  /* ── Default configuration ────────────────────────────────────────────── */
  window.LBW.configDefaults = {

    layout: {
      treeWidthPx:  270,
      panelWidthPx: 340
    },

    gridView: {
      cellWidthPx:    105,
      cellRatio:      1.4,
      gapPx:          12,
      cellHoverScale: 1.035,
      cellHoverMs:    250,
      fluid:          true
    },

    /* ── posterView — hero fanart + crossfade timing ──────────────────────────
       Mirrors BigBoxWeb/web/engine/config.js :: posterView section.
       CSS custom properties are written at boot from these values; see the
       DOMContentLoaded block in app.js (C6).
       (ref: BigBoxWeb/web/engine/app.js :: schedulePosterFanart ~line 848) */
    posterView: {
      heroFanartDelayMs:       500,   /* debounce before fade-in starts */
      heroFanartFadeOutDelayMs: 500,  /* one-shot delay before old layer fades */
      heroFanartFadeInMs:      300,   /* CSS transition duration for fade-in */
      heroFanartFadeOutMs:     800,   /* CSS transition duration for fade-out */
      heroFanartOpacity:       0.28,  /* opacity of .on layer */
      heavyDelayMs:            300    /* debounce (ms) before loadLbDetail fires after selectCell
                                         (mirrors BBW engine/config.js :: media.heavyDelayMs) */
    },

    /* ── Parental control defaults ──────────────────────────────────────────
       These mirror the shape of the /api/parental/state response and of
       BBW's module-scope parental{} object (app.js:89).  Safe defaults:
       everything disabled/unlocked so nothing is hidden if the endpoint
       has not yet been fetched or the server does not support parental.
       The boot fetchParental() call overwrites every field from the
       server response before the first tree render.
       (ref: BigBoxWeb/web/engine/app.js :: parental object ~line 89) */
    parental: {
      active:     false,    /* parental-control feature is configured */
      locked:     false,    /* content is currently filtered */
      canUnlock:  false,    /* a PIN unlock is possible from this client */
      bigBox:     false,    /* lock is controlled by BigBox (not PIN) */
      lockedOut:  false,    /* too many wrong PINs — unlock disabled */
      maxAttempts: 3,       /* PIN attempts before lockout */
      canRate:    true,     /* locked users may modify ratings */
      canFav:     true,     /* locked users may toggle favorites */
      lang:       "en"      /* UI language hint from server */
    },

    /* ── theme — CSS accent colour ──────────────────────────────────────────
       actionColor drives the --lb-action-color CSS custom property applied
       by applyLbCfgToDom() in app.js.  Default matches the orange used in
       the legacy hard-coded stylesheet.
       (ref: BBW engine/config.js — no direct analogue; LBW-only section) */
    theme: {
      actionColor: "#c8732f"
    },

    /* ── misc — debug / developer options ──────────────────────────────────
       debug: true enables verbose console.log output in app.js where calls
       are guarded by window.LBW.config.misc.debug.
       (ref: BBW engine/config.js :: debug.kioskIndicators) */
    misc: {
      debug: false
    },

    /* ── view — centre pane mode: Image (poster grid) vs List (columns) ──── */
    view: { mode: "image" },   /* "image" | "list" */

    /* ── listView — column order / visibility / sort for the List view ─────
       order:  display order of every known column key.
       hidden: map of key -> true for columns toggled OFF.
       sort:   { key, dir } ; dir "asc" | "desc" ; key "" = unsorted (file order).
       Inspired by LiteBox's GameListView (LbApiHost): same default-visible set
       (Title, Developer, Genre, Year, Fav), the rest available-but-hidden. */
    listView: {
      order:  ["title", "dev", "genre", "year", "fav", "publisher", "rating", "votes", "esrb", "platform", "lastplayed", "playtime"],
      hidden: { publisher: true, rating: true, votes: true, esrb: true, platform: true, lastplayed: true, playtime: true },
      sort:   { key: "", dir: "asc" },
      /* widths: map of key -> width as a PERCENT of the list viewport, set by
         resizing a column. Stored as % (not px) so columns keep their
         proportions across window sizes. Absent key = default/flex width. */
      widths: {}
    },

    /* ── input.gamepad — Gamepad API tuning (read by gamepad.js) ───────────────
       Mirrors BigBoxWeb/web/engine/config.js :: global.gamepad.  enabled:false
       turns polling off.  deadzone is the analog-stick threshold; the repeat
       fields drive the accelerated auto-repeat for held directions / triggers. */
    input: {
      gamepad: {
        enabled:          true,
        deadzone:         0.5,
        repeatDelayMs:    400,   /* delay before a held direction starts repeating */
        repeatRateMs:     120,   /* initial repeat interval */
        repeatRateMinMs:  30,    /* fastest repeat interval (fully accelerated) */
        accelMs:          1000,  /* ramp time from slow → fast */
        triggerThreshold: 0.4    /* analog L2/R2 press threshold */
      }
    }

  };

  /* ── Live config — deep clone of defaults (receives cookie overrides) ─── */
  window.LBW.config = JSON.parse(JSON.stringify(window.LBW.configDefaults));

  /* ── Schema — describes every option exposed in the settings modal ──────
     Structure: object keyed by top-level config section name.
     Each leaf: { type, label, [min, max, step] }
       type   "range"  → <input type="range"> with live numeric readout
              "bool"   → <input type="checkbox">
              "color"  → <input type="color">
     Parental fields are intentionally absent — they are server-driven
     and must not be user-writable.
     (ref: BBW engine/config.js :: configSchema array ~line 631) */
  window.LBW.configSchema = {

    layout: {
      treeWidthPx:  { type: "range", min: 180, max: 420, step: 5,  label: "Tree column width (px)" },
      panelWidthPx: { type: "range", min: 240, max: 500, step: 5,  label: "Panel column width (px)" }
    },

    gridView: {
      cellWidthPx:    { type: "range", min: 90,   max: 220,  step: 5,     label: "Cell width (px)" },
      cellRatio:      { type: "range", min: 1.0,  max: 1.8,  step: 0.05,  label: "Cell ratio (h/w)" },
      gapPx:          { type: "range", min: 6,    max: 32,   step: 1,     label: "Gap (px)" },
      cellHoverScale: { type: "range", min: 1.00, max: 1.10, step: 0.005, label: "Hover scale" },
      cellHoverMs:    { type: "range", min: 80,   max: 600,  step: 10,    label: "Hover transition (ms)" },
      fluid:          { type: "bool",  label: "Fluid mode (fit ratio to image)" }
    },

    posterView: {
      heavyDelayMs:             { type: "range", min: 0, max: 1500, step: 50,   label: "Heavy media delay (ms)" },
      heroFanartDelayMs:        { type: "range", min: 0, max: 1500, step: 50,   label: "Fanart fade-in delay (ms)" },
      heroFanartFadeOutDelayMs: { type: "range", min: 0, max: 1500, step: 50,   label: "Fanart fade-out delay (ms)" },
      heroFanartFadeInMs:       { type: "range", min: 0, max: 1500, step: 50,   label: "Fanart fade-in duration (ms)" },
      heroFanartFadeOutMs:      { type: "range", min: 0, max: 2000, step: 50,   label: "Fanart fade-out duration (ms)" },
      heroFanartOpacity:        { type: "range", min: 0, max: 0.6,  step: 0.02, label: "Fanart opacity" }
    },

    theme: {
      actionColor: { type: "color", label: "Action button color (Play)" }
    },

    misc: {
      debug: { type: "bool", label: "Verbose console logging" }
    }

  };

  /* ── Internal helpers ─────────────────────────────────────────────────── */

  /**
   * Read a dot-path from an object.
   * getByPath({ a: { b: 1 } }, "a.b") → 1
   */
  function getByPath(o, p) {
    var k = p.split(".");
    for (var i = 0; i < k.length; i++) {
      if (o == null) { return undefined; }
      o = o[k[i]];
    }
    return o;
  }

  /**
   * Write a dot-path on an object, creating intermediate objects as needed.
   * setByPath(obj, "a.b", 1) → obj.a.b = 1
   */
  function setByPath(o, p, v) {
    var k = p.split(".");
    for (var i = 0; i < k.length - 1; i++) {
      if (o[k[i]] == null) { o[k[i]] = {}; }
      o = o[k[i]];
    }
    o[k[k.length - 1]] = v;
  }

  /**
   * Read a cookie value by name. Returns null if not found.
   */
  function readCookie(n) {
    var m = document.cookie.match(new RegExp("(?:^|; )" + n + "=([^;]*)"));
    return m ? decodeURIComponent(m[1]) : null;
  }

  /**
   * Write a cookie with a given name, value, and lifetime in days.
   */
  function writeCookie(n, v, days) {
    var d = new Date();
    d.setTime(d.getTime() + days * 864e5);
    document.cookie = n + "=" + encodeURIComponent(v) + ";expires=" + d.toUTCString() + ";path=/";
  }

  /* ── cfg object ───────────────────────────────────────────────────────── */

  window.LBW.cfg = {

    /** Active overrides map: { "dot.path": value } */
    _ov: {},

    /**
     * Get the current (live) value for a dot-path from window.LBW.config.
     * @param {string} p  Dot-path, e.g. "gridView.cellWidthPx"
     */
    get: function (p) {
      return getByPath(window.LBW.config, p);
    },

    /**
     * Get the default value for a dot-path from window.LBW.configDefaults.
     * @param {string} p  Dot-path
     */
    def: function (p) {
      return getByPath(window.LBW.configDefaults, p);
    },

    /**
     * Set a value. If v equals the default, the override entry is removed
     * (equivalent to a reset). Updates config in-place then saves to cookie.
     * @param {string} p  Dot-path
     * @param {*}      v  New value
     */
    set: function (p, v) {
      if (JSON.stringify(v) === JSON.stringify(this.def(p))) {
        delete this._ov[p];
      } else {
        this._ov[p] = v;
      }
      setByPath(window.LBW.config, p, v);
      this.save();
    },

    /**
     * Reset a single path to its default value and save.
     * @param {string} p  Dot-path
     */
    reset: function (p) {
      delete this._ov[p];
      setByPath(window.LBW.config, p, this.def(p));
      this.save();
    },

    /**
     * Persist the current override map to cookie 'lbw_cfg' (365-day expiry).
     */
    save: function () {
      try {
        writeCookie("lbw_cfg", JSON.stringify(this._ov), 365);
      } catch (e) {}
    }

  };

  /* ── Apply cookie overrides at startup ────────────────────────────────── */
  (function () {
    try {
      var raw = readCookie("lbw_cfg");
      if (raw) {
        var ov = JSON.parse(raw) || {};
        window.LBW.cfg._ov = ov;
        for (var p in ov) {
          if (Object.prototype.hasOwnProperty.call(ov, p)) {
            setByPath(window.LBW.config, p, ov[p]);
          }
        }
      }
    } catch (e) {}
  })();

})();
