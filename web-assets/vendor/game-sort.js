(function () {
  "use strict";

  var defs = [
    ["dateadded", "Date Added", "DateAdded"],
    ["datemodified", "Date Modified", "DateModified"],
    ["developer", "Developer", "Developer"],
    // LaunchBox calls the ESRB/PEGI field "Rating"; LiteBox shows "ESRB" (its own column name) and
    // places it where that label sorts. Label only — the XML value stays "Rating".
    ["rating", "ESRB", "Rating"],
    ["favorite", "Favorite", "Favorite"],
    ["genre", "Genre", "Genre"],
    ["installed", "Installed", "Installed"],
    ["lastplayed", "Last Played", "LastPlayed"],
    ["launchboxid", "LaunchBox Database ID", "LaunchBoxId"],
    ["mamehighscores", "MAME High Scores Supported", "MameHighScoresSupported"],
    ["maxplayers", "Max Players", "MaxPlayers"],
    ["platform", "Platform", "Platform"],
    ["playcount", "Play Count", "PlayCount"],
    ["playmode", "Play Mode", "PlayMode"],
    ["playtime", "Play Time", "PlayTime"],
    ["portable", "Portable", "Portable"],
    ["progress", "Progress", "Progress"],
    ["publisher", "Publisher", "Publisher"],
    ["region", "Region", "Region"],
    ["releasedate", "Release Date", "ReleaseDate"],
    ["releaseyear", "Release Date Year", "ReleaseDateYear"],
    ["releasetype", "Release Type", "ReleaseType"],
    ["series", "Series", "Series"],
    ["source", "Source", "Source"],
    ["starrating", "Star Rating", "StarRating"],
    ["status", "Status", "Status"],
    ["title", "Title", "Title"],
    ["version", "Version", "Version"]
  ].map(function (x) { return { key: x[0], label: x[1], value: x[2] }; });
  var extraLabels = {
    community: "Community", votes: "Votes", completed: "Done", broken: "Broken",
    apppath: "Application Path", rahash: "RA Hash"
  };
  var browserStorageKey = "";
  var sessionConnected = false;
  var sessionListeners = [];

  function compact(v) {
    return String(v == null ? "" : v).toLowerCase().replace(/[^a-z0-9]/g, "");
  }

  function parse(raw, customNames) {
    var text = String(raw == null ? "" : raw).trim();
    var c = compact(text);
    if (!c || c === "default") return "default";
    if (c === "manual") return "manual";
    for (var i = 0; i < defs.length; i++) {
      var d = defs[i];
      if (c === compact(d.key) || c === compact(d.label) || c === compact(d.value)) return d.key;
    }
    var aliases = {
      name: "title", comparename: "title", year: "releaseyear", plays: "playcount",
      players: "maxplayers", dbid: "launchboxid", databaseid: "launchboxid",
      launchboxdatabaseid: "launchboxid", fav: "favorite", esrb: "rating"
    };
    if (aliases[c]) return aliases[c];
    customNames = customNames || [];
    for (var j = 0; j < customNames.length; j++) {
      if (String(customNames[j]).toLowerCase() === text.toLowerCase()) return "custom:" + customNames[j];
    }
    return "default";
  }

  function label(key) {
    if (String(key || "").indexOf("custom:") === 0) return key.substring(7);
    if (key === "manual") return "Manual";
    for (var i = 0; i < defs.length; i++) if (defs[i].key === key) return defs[i].label;
    return key === "default" ? "Default" : (extraLabels[key] || key || "Title");
  }

  // "No value" is null, never a sentinel like 0 or -1. sorted() ranks null last ascending and
  // first descending, mirroring GameListView.ValueComparer, so a game with no year / no rating /
  // never played lands in the same block on the desktop and here.
  function num(v) { return v == null ? null : (isNaN(+v) ? null : +v); }
  // Epoch 0 is how the payload spells "no date" — no game is dated 1970-01-01T00:00Z.
  function date(v) { return +v > 0 ? +v : null; }
  function text(v) { return String(v == null ? "" : v).toUpperCase(); }
  function flag(v) { return v ? 1 : 0; }

  function value(g, key) {
    g = g || {};
    if (String(key || "").indexOf("custom:") === 0) {
      var n = key.substring(7), cf = g.cf || {};
      for (var k in cf) if (Object.prototype.hasOwnProperty.call(cf, k) && k.toLowerCase() === n.toLowerCase()) return String(cf[k] || "").toUpperCase();
      return "";
    }
    switch (key) {
      case "dateadded": return date(g.da);
      case "datemodified": return date(g.dm);
      case "developer": return text(g.dev);
      case "favorite": return flag(g.fav);
      case "genre": return text(g.g);
      // g.inst is IGame.Installed, TRI-STATE: null = the user never said, and ranks last.
      // NOT g.installed, which is the presence verdict the badge shows.
      case "installed": return g.inst == null ? null : (g.inst ? 1 : 0);
      case "lastplayed": return date(g.lp);
      case "launchboxid": return num(g.dbId);
      case "mamehighscores": return flag(g.mameHs);
      case "maxplayers": return num(g.maxPlayers);
      case "platform": return text(g.platform);
      case "playcount": return +g.playCount || 0;
      case "playmode": return text(g.playMode);
      case "playtime": return +g.playTime || 0;
      case "portable": return flag(g.portable);
      case "progress": return text(g.progress);
      case "publisher": return text(g.pub);
      case "rating": return text(g.esrb);
      case "region": return text(g.region);
      case "releasedate": return date(g.rd);
      // g.ry is the effective year (ReleaseYear, else the year inside ReleaseDate) — g.y is the
      // formatted display string and must not drive the order.
      case "releaseyear": return num(g.ry);
      case "releasetype": return text(g.rt);
      case "series": return text(g.series);
      case "source": return text(g.source);
      // g.sr is the local-or-community score the desktop sorts on; g.ur is user-only display.
      case "starrating": return num(g.sr);
      case "community": return num(g.community);
      case "votes": return +g.votes || 0;
      case "completed": return flag(g.completed);
      case "broken": return flag(g.broken);
      case "apppath": return text(g.appPath);
      case "rahash": return text(g.raHash);
      case "status": return text(g.status);
      case "version": return text(g.version);
      case "manual": return num(g.mo);
      default: return String(g.cn != null ? g.cn : (g.t || "")).toUpperCase();
    }
  }

  // Keys are computed ONCE per game, before sorting — not inside the comparator, which would
  // recompute (and re-allocate, value() upper-cases strings) O(n log n) times instead of O(n).
  // On a 10k-game platform that was the difference between a visible hitch and an unnoticeable one.
  function sorted(games, state) {
    state = state || { key: "title", dir: "asc" };
    var key = state.key, dir = state.dir === "desc" ? -1 : 1;
    var manual = key === "manual", isTitle = key === "title";
    var src = games || [], n = src.length;
    var rows = new Array(n);
    for (var i = 0; i < n; i++) {
      var g = src[i], k = value(g, key);
      rows[i] = {
        g: g, i: i, k: k,
        // The tie key is the title key; when that IS the primary, reuse it rather than recompute.
        t: manual ? null : (isTitle ? k : value(g, "title")),
      };
    }
    rows.sort(function (a, b) {
      var av = a.k, bv = b.k;
      // null = greatest, exactly like GameListView.ValueComparer: blanks sit at the bottom
      // ascending and at the top descending, in both surfaces.
      if (av == null || bv == null) {
        if (av != null) return -dir;
        if (bv != null) return dir;
      } else {
        if (av < bv) return -dir;
        if (av > bv) return dir;
      }
      // Manual ranks are dense and unique (GameSortCatalog.ManualRanks); a tie can only mean a
      // legacy payload with a raw ManualOrder, where the source sequence IS the manual order.
      if (manual) return (a.i - b.i) * dir;
      // Tie key: title, ASCENDING even under a descending primary — GameListView applies
      // .ThenBy (never .ThenByDescending) for the same reason.
      if (a.t < b.t) return -1;
      if (a.t > b.t) return 1;
      return a.i - b.i;
    });
    var out = new Array(n);
    for (var j = 0; j < n; j++) out[j] = rows[j].g;
    return out;
  }

  // One rule for every surface: a playlist's SortBy, applied ascending. The `bigBox` argument is
  // kept so callers stay symmetrical, but BB-Web deliberately resolves the SAME order as LB-Web and
  // the desktop — no per-client override exists in any real playlist file.
  function stateForPayload(payload, bigBox, globalState) {
    payload = payload || {};
    var raw = payload.nodeKind === "playlist" ? payload.sortBy : "Default";
    var key = parse(raw, payload.customSorts || []);
    if (key === "manual" && (payload.autoPopulate || !payload.manualAvailable)) key = "default";
    if (key === "default") return { key: globalState.key, dir: globalState.dir, forced: false };
    return { key: key, dir: "asc", forced: true };
  }

  function select(state, key, globalState) {
    state = state || { key: "title", dir: "asc", forced: false };
    globalState = globalState || { key: "title", dir: "asc" };
    var active = {
      key: key,
      dir: state.key === key && state.dir === "asc" ? "desc" : "asc",
      forced: !!state.forced
    };
    // A configured non-Default playlist keeps every temporary choice local.
    // Manual is also always local, even in a Default playlist: it is meaningless
    // after leaving that playlist and must never replace the session-wide sort.
    var global = active.forced || key === "manual"
      ? { key: globalState.key, dir: globalState.dir }
      : { key: active.key, dir: active.dir };
    return { active: active, global: global };
  }

  function options(payload) {
    var out = [];
    if (payload && payload.manualAvailable) out.push({ key: "manual", label: "Manual" });
    defs.forEach(function (d) { out.push({ key: d.key, label: d.label }); });
    (payload && payload.customSorts || []).forEach(function (n) { out.push({ key: "custom:" + n, label: n, custom: true }); });
    return out;
  }

  function cleanState(state) {
    var key = state && String(state.key || "").trim();
    if (!key || key === "default" || key === "manual") key = "title";
    return { key: key, dir: state && state.dir === "desc" ? "desc" : "asc" };
  }

  function embedded() {
    return /(?:^|[#&])embedded=1(?:&|$)/.test(String(location.hash || ""));
  }

  function notifySession(state) {
    var clean = cleanState(state);
    sessionListeners.slice().forEach(function (fn) {
      try { fn({ key: clean.key, dir: clean.dir }); } catch (_) {}
    });
  }

  function readBrowserSession() {
    if (!browserStorageKey) return { key: "title", dir: "asc" };
    try { return cleanState(JSON.parse(localStorage.getItem(browserStorageKey) || "null")); }
    catch (_) { return { key: "title", dir: "asc" }; }
  }

  // The storage key carries the host's process token, so every LiteBox restart mints a new one.
  // Without this sweep the browser would accumulate one dead entry per launch, forever.
  function pruneForeignSessions(token) {
    try {
      var prefix = "litebox.game-sort.", stale = [];
      for (var i = 0; i < localStorage.length; i++) {
        var k = localStorage.key(i);
        if (k && k.indexOf(prefix) === 0 && k !== prefix + token) stale.push(k);
      }
      stale.forEach(function (k) { localStorage.removeItem(k); });
    } catch (_) {}
  }

  // One global order per host execution and per browser profile. The process token
  // prevents yesterday's sort from surviving a LiteBox restart. LB-Web and BB-Web
  // use the same origin/key, so navigation or separate tabs in that browser agree.
  // Embedded kiosk pages bypass browser storage and exchange the state with desktop.
  function connectSession(processToken, listener) {
    if (typeof listener === "function" && sessionListeners.indexOf(listener) < 0)
      sessionListeners.push(listener);

    if (!embedded()) {
      // No token means the first payload was a synthetic one (an empty category, a merged
      // category, a bare array). Refusing to connect leaves the caller on its default and lets a
      // LATER payload — one that does carry the host's process token — establish the session.
      // Falling back to a fixed key instead would persist the sort across LiteBox restarts, which
      // is exactly what the per-execution scoping is there to prevent.
      if (!processToken) return { key: "title", dir: "asc", deferred: true };
      var token = String(processToken);
      browserStorageKey = "litebox.game-sort." + token;
      pruneForeignSessions(token);
      if (!sessionConnected) {
        window.addEventListener("storage", function (e) {
          if (e.key === browserStorageKey && e.newValue) {
            try { notifySession(JSON.parse(e.newValue)); } catch (_) {}
          }
        });
        sessionConnected = true;
      }
      return readBrowserSession();
    }

    if (!sessionConnected) {
      try {
        if (window.chrome && window.chrome.webview) {
          window.chrome.webview.addEventListener("message", function (e) {
            var msg = e && e.data;
            if (msg && msg.type === "kiosk:sort")
              notifySession({ key: msg.key, dir: msg.dir });
          });
          window.chrome.webview.postMessage("kiosk:sort:get");
        }
      } catch (_) {}
      sessionConnected = true;
    }
    return { key: "title", dir: "asc" }; // replaced asynchronously by the host reply
  }

  function publishSession(state) {
    var clean = cleanState(state);
    if (embedded()) {
      try {
        if (window.chrome && window.chrome.webview)
          window.chrome.webview.postMessage("kiosk:sort:set:" +
            encodeURIComponent(clean.key) + ":" + clean.dir);
      } catch (_) {}
      return clean;
    }
    if (browserStorageKey) {
      try { localStorage.setItem(browserStorageKey, JSON.stringify(clean)); } catch (_) {}
    }
    return clean;
  }

  // ── Recherche texte ───────────────────────────────────────────────────────────────────────
  // Miroir exact de Host/GameTextFilter.cs — voir ce fichier pour le raisonnement. Titre seul,
  // testé sous DEUX formes (brute et normalisée) ; deux modes (contient / commence par).
  // --selftest-filter-parity compare les deux implémentations sur le même échantillon.
  function normalizeText(v) {
    var s = String(v == null ? "" : v);
    // NFD sépare la lettre de son accent, \p{M} retire l'accent : "Pokémon" → "pokemon".
    try { s = s.normalize("NFD").replace(/\p{M}/gu, ""); } catch (_) {}
    return s.toLowerCase().replace(/[^0-9a-z]/g, "");
  }

  function titleMatches(title, query, prefix) {
    var q = String(query == null ? "" : query).trim();
    if (!q) return true;
    var t = String(title == null ? "" : title);
    var tl = t.toLowerCase(), ql = q.toLowerCase();
    if (prefix ? tl.lastIndexOf(ql, 0) === 0 : tl.indexOf(ql) >= 0) return true;
    // Une requête faite uniquement de ponctuation se normalise en chaîne vide : sans cette garde
    // elle passerait sur tous les jeux via la forme normalisée.
    var nq = normalizeText(q);
    if (!nq) return false;
    var nt = normalizeText(t);
    return prefix ? nt.lastIndexOf(nq, 0) === 0 : nt.indexOf(nq) >= 0;
  }

  /* Le titre d'un jeu du payload : t (brut). cn est la clé de TRI, articles retirés — elle ne doit
     pas servir ici, sinon "the legend" ne trouverait plus rien. */
  function gameMatches(g, query, prefix) {
    return titleMatches(g && g.t, query, prefix);
  }

  function filterGames(games, query, prefix) {
    if (!String(query == null ? "" : query).trim()) return games || [];
    return (games || []).filter(function (g) { return gameMatches(g, query, prefix); });
  }

  window.LBGameSort = {
    defs: defs,
    normalizeText: normalizeText,
    titleMatches: titleMatches,
    gameMatches: gameMatches,
    filterGames: filterGames,
    parse: parse,
    label: label,
    value: value,
    sorted: sorted,
    stateForPayload: stateForPayload,
    select: select,
    options: options,
    connectSession: connectSession,
    publishSession: publishSession
  };
}());
