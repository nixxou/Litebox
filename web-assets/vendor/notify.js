// LiteBox web notifications — the native toast stack, mirrored into the LB-web frontend.
//
// Included by the LITEBOX theme only (the LB-web surface, kiosk or plain browser); everything is
// self-contained: this file injects its own styles and container, polls
// GET /api/notifications/events?since=<seq> every ~2.5 s (the recent-epoch cadence), and renders arriving
// notifications as bottom-right cards styled like the native ones. The BIGBOX theme deliberately does NOT
// load this file — it is the couch/gamepad surface, and a notification raised while its kiosk is up goes
// straight to the native bell instead.
//
// While the HOST'S LB-web KIOSK window is up, the host suppresses its native popups — this stack is the
// display then. A plain browser on this page shows the cards too, but does not silence the native ones.
//
// The first poll passes since=-1 and normally gets a seq baseline only: a freshly opened page starts
// clean, history is the bell's job. The SERVER makes one exception, for the kiosk — its window has been
// silencing native popups since before this page existed (WebView2 takes seconds to boot), so its first
// poll replays from the moment the kiosk opened. Nothing to do here: the reply just carries events.
//
// Buttons post back (/action/<n>) and the host runs the plugin callback on its UI thread. The countdown
// pauses on hover, like the native card; a card that times out on its own is NOT marked read (the bell
// keeps its badge), while the ✕ marks it read — the user clearly saw it.

(function () {
  'use strict';

  var API = '/api/notifications';
  var POLL_MS = 2500;
  var seq = -1;
  var cards = new Map();   // id -> {el, msgEl, timer, remainMs, hover}

  // ── chrome ─────────────────────────────────────────────────────────────────────────────────────────

  var CSS = [
    '#lbx-notify{position:fixed;right:12px;bottom:12px;z-index:2147483000;display:flex;flex-direction:column;gap:8px;align-items:flex-end;pointer-events:none;font-family:"Segoe UI",system-ui,sans-serif}',
    '.lbx-toast{pointer-events:auto;width:360px;max-width:calc(100vw - 24px);box-sizing:border-box;background:#202127;border:1px solid #464854;border-radius:8px;box-shadow:0 6px 24px rgba(0,0,0,.55);padding:11px 14px 12px;color:#dedede;opacity:0;transform:translateY(6px);transition:opacity .18s ease,transform .18s ease;text-align:left}',
    '.lbx-toast.on{opacity:1;transform:none}',
    '.lbx-toast.err{border-color:rgba(225,95,95,.55)}',
    '.lbx-toast .lbx-t{font-size:11px;color:#969698;display:flex;justify-content:space-between;align-items:center;margin-bottom:7px}',
    '.lbx-toast .lbx-x{cursor:pointer;color:#969698;font-size:12px;padding:0 2px;line-height:1;background:none;border:none}',
    '.lbx-toast .lbx-x:hover{color:#fff}',
    '.lbx-toast .lbx-b{display:flex;gap:11px}',
    '.lbx-toast .lbx-ic{flex:0 0 30px;height:30px;border-radius:50%;background:#007acc;color:#fff;display:flex;align-items:center;justify-content:center;font-weight:700;font-size:15px;font-style:normal;align-self:center}',
    '.lbx-toast.err .lbx-ic{background:transparent;color:#e15f5f;font-size:26px}',
    '.lbx-toast .lbx-m{font-size:13.5px;line-height:1.35;align-self:center;overflow-wrap:break-word;min-width:0;white-space:normal}',
    '.lbx-toast .lbx-a{display:block;width:100%;box-sizing:border-box;margin-top:8px;padding:6px 10px;background:#2b2d36;border:1px solid #5e6170;border-radius:4px;color:#fff;font-family:inherit;font-size:12.5px;cursor:pointer;text-align:center}',
    '.lbx-toast .lbx-a:hover{background:#3c404e}',
  ].join('\n');

  var root = null;
  function ensureRoot() {
    if (root && document.body && document.body.contains(root)) return root;
    if (!document.body) return null;
    var style = document.createElement('style');
    style.textContent = CSS;
    document.head.appendChild(style);
    root = document.createElement('div');
    root.id = 'lbx-notify';
    document.body.appendChild(root);
    return root;
  }

  // ── cards ──────────────────────────────────────────────────────────────────────────────────────────

  function post(id, tail) {
    try { fetch(API + '/' + id + '/' + tail, { method: 'POST' }).catch(function () {}); } catch (e) {}
  }

  function stamp(ms) {
    var d = new Date(ms);
    var h = d.getHours(), ap = h >= 12 ? 'PM' : 'AM';
    h = h % 12 || 12;
    function p(n) { return (n < 10 ? '0' : '') + n; }
    return d.getFullYear() + '-' + p(d.getMonth() + 1) + '-' + p(d.getDate()) + ' ' + h + ':' + p(d.getMinutes()) + ' ' + ap;
  }

  function close(id) {
    var c = cards.get(id);
    if (!c) return;
    cards.delete(id);
    if (c.timer) clearTimeout(c.timer);
    c.el.classList.remove('on');
    setTimeout(function () { if (c.el.parentNode) c.el.parentNode.removeChild(c.el); }, 200);
  }

  function arm(id) {
    var c = cards.get(id);
    if (!c || c.remainMs <= 0) return;         // sticky
    if (c.timer) clearTimeout(c.timer);
    c.armedAt = Date.now();
    c.timer = setTimeout(function () {
      // Timed out on its own → just disappears; deliberately NOT marked read (native parity).
      if (!c.hover) close(id);
    }, c.remainMs);
  }

  function show(item) {
    var host = ensureRoot();
    if (!host) return;
    if (cards.has(item.id)) { update(item); return; }

    var el = document.createElement('div');
    el.className = 'lbx-toast' + (item.error ? ' err' : '');

    var top = document.createElement('div');
    top.className = 'lbx-t';
    top.appendChild(document.createTextNode(stamp(item.raisedMs)));
    var x = document.createElement('button');
    x.className = 'lbx-x';
    x.textContent = '✕';
    x.addEventListener('click', function () { post(item.id, 'read'); post(item.id, 'dismiss'); close(item.id); });
    top.appendChild(x);
    el.appendChild(top);

    var body = document.createElement('div');
    body.className = 'lbx-b';
    var ic = document.createElement('i');
    ic.className = 'lbx-ic';
    ic.textContent = item.error ? '⚠' : (item.progress ? '…' : 'i');
    body.appendChild(ic);
    var msg = document.createElement('div');
    msg.className = 'lbx-m';
    msg.textContent = item.message;
    body.appendChild(msg);
    el.appendChild(body);

    (item.actions || []).forEach(function (label, i) {
      var btn = document.createElement('button');
      btn.className = 'lbx-a';
      btn.textContent = label;
      btn.addEventListener('click', function () {
        post(item.id, 'action/' + i);   // the host marks read + dismisses (per the action) on its UI thread
        close(item.id);                 // optimistic — the dismissed event would close it anyway
      });
      el.appendChild(btn);
    });

    var card = { el: el, msgEl: msg, timer: null, remainMs: item.lifeSpan > 0 ? item.lifeSpan * 1000 : 0, hover: false, armedAt: 0 };
    cards.set(item.id, card);

    // Hover pauses the countdown, mirroring the native card.
    el.addEventListener('mouseenter', function () {
      card.hover = true;
      if (card.timer) { clearTimeout(card.timer); card.timer = null; card.remainMs = Math.max(1000, card.remainMs - (Date.now() - card.armedAt)); }
    });
    el.addEventListener('mouseleave', function () { card.hover = false; arm(item.id); });

    host.appendChild(el);
    requestAnimationFrame(function () { el.classList.add('on'); });
    arm(item.id);
  }

  function update(item) {
    var c = cards.get(item.id);
    if (!c) return;
    c.msgEl.textContent = item.message;
    c.el.classList.toggle('err', !!item.error);
  }

  // ── the poll ───────────────────────────────────────────────────────────────────────────────────────

  function tick() {
    fetch(API + '/events?since=' + seq)
      .then(function (r) { return r.json(); })
      .then(function (d) {
        if (seq < 0) { seq = d.seq; return; }   // baseline only — no backlog replay on page load
        seq = d.seq;
        var items = {};
        (d.items || []).forEach(function (i) { items[i.id] = i; });
        (d.events || []).forEach(function (ev) {
          var it = items[ev.id];
          if (ev.type === 'raised' && it) show(it);
          else if (ev.type === 'updated' && it) update(it);
          else if (ev.type === 'dismissed' || ev.type === 'removed') close(ev.id);
        });
      })
      .catch(function () {})
      .then(function () { setTimeout(tick, POLL_MS); });
  }

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', tick);
  else tick();
})();
