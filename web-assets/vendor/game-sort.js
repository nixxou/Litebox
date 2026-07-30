(function () {
  "use strict";

  var defs = [
    ["dateadded", "Date Added", "DateAdded"],
    ["datemodified", "Date Modified", "DateModified"],
    ["developer", "Developer", "Developer"],
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
    ["rating", "Rating", "Rating"],
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
    return key === "default" ? "Default" : "Title";
  }

  function value(g, key) {
    g = g || {};
    if (String(key || "").indexOf("custom:") === 0) {
      var n = key.substring(7), cf = g.cf || {};
      for (var k in cf) if (Object.prototype.hasOwnProperty.call(cf, k) && k.toLowerCase() === n.toLowerCase()) return String(cf[k] || "").toUpperCase();
      return "";
    }
    switch (key) {
      case "dateadded": return +g.da || 0;
      case "datemodified": return +g.dm || 0;
      case "developer": return String(g.dev || "").toUpperCase();
      case "favorite": return g.fav ? 1 : 0;
      case "genre": return String(g.g || "").toUpperCase();
      case "installed": return g.installed ? 1 : 0;
      case "lastplayed": return +g.lp || 0;
      case "launchboxid": return +g.dbId || -1;
      case "mamehighscores": return g.mameHs ? 1 : 0;
      case "maxplayers": return +g.maxPlayers || -1;
      case "platform": return String(g.platform || "").toUpperCase();
      case "playcount": return +g.playCount || 0;
      case "playmode": return String(g.playMode || "").toUpperCase();
      case "playtime": return +g.playTime || 0;
      case "portable": return g.portable ? 1 : 0;
      case "progress": return String(g.progress || "").toUpperCase();
      case "publisher": return String(g.pub || "").toUpperCase();
      case "rating": return String(g.esrb || "").toUpperCase();
      case "region": return String(g.region || "").toUpperCase();
      case "releasedate": return +g.rd || 0;
      case "releaseyear": return parseInt(g.y, 10) || 0;
      case "releasetype": return String(g.rt || "").toUpperCase();
      case "series": return String(g.series || "").toUpperCase();
      case "source": return String(g.source || "").toUpperCase();
      case "starrating": return +g.ur || 0;
      case "status": return String(g.status || "").toUpperCase();
      case "version": return String(g.version || "").toUpperCase();
      case "manual": return g.mo == null ? 2147483647 : (+g.mo || 0);
      default: return String(g.cn != null ? g.cn : (g.t || "")).toUpperCase();
    }
  }

  function sorted(games, state) {
    state = state || { key: "title", dir: "asc" };
    var dir = state.dir === "desc" ? -1 : 1;
    return (games || []).map(function (g, i) { return { g: g, i: i }; }).sort(function (a, b) {
      var av = value(a.g, state.key), bv = value(b.g, state.key);
      if (av < bv) return -dir;
      if (av > bv) return dir;
      // LaunchBox-like deterministic fallback, then source order for exact duplicates.
      var at = value(a.g, "title"), bt = value(b.g, "title");
      if (at < bt) return -1;
      if (at > bt) return 1;
      return a.i - b.i;
    }).map(function (x) { return x.g; });
  }

  function stateForPayload(payload, bigBox, globalState) {
    payload = payload || {};
    var custom = payload.customSorts || [];
    var raw = payload.nodeKind === "playlist"
      ? (bigBox && payload.bigBoxSortBy && compact(payload.bigBoxSortBy) !== "default"
          ? payload.bigBoxSortBy : payload.sortBy)
      : "Default";
    var key = parse(raw, custom);
    if (key === "manual" && (payload.autoPopulate || !payload.manualAvailable)) key = "default";
    if (key === "default") return { key: globalState.key, dir: globalState.dir, forced: false };
    return { key: key, dir: "asc", forced: true };
  }

  function options(payload) {
    var out = [];
    if (payload && payload.manualAvailable) out.push({ key: "manual", label: "Manual" });
    defs.forEach(function (d) { out.push({ key: d.key, label: d.label }); });
    (payload && payload.customSorts || []).forEach(function (n) { out.push({ key: "custom:" + n, label: n, custom: true }); });
    return out;
  }

  window.LBGameSort = {
    defs: defs,
    parse: parse,
    label: label,
    value: value,
    sorted: sorted,
    stateForPayload: stateForPayload,
    options: options
  };
}());
