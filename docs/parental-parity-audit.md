# Parental control parity audit — plugin (+ ASI) vs LiteBox

Snapshot 2026-07-18. Legend: **[LOSS]** unapproved regression · **[ADAPTED]** same behavior,
different mechanism · **[REPLACED]** superseded by an explicit user decision · **[DECISION]**
needs sign-off · **[BETTER]** LiteBox exceeds the plugin.

## 0. The PIN — [REPLACED, user-mandated, verified honoured]

The plugin ran TWO credentials: its own salted-SHA256 PIN (ExtendDBParental.dat) for LB-side
config/web unlock, plus a mere presence-check of BigBox's encrypted LockPin (the ASI's cold-boot
gate). Per explicit instruction, LiteBox uses **BigBox's own LockPin as the single credential**
(LbSettingsCrypto Rijndael-256 with the dedicated LockPin key/seed; BigBoxPin read/verify/set/
remove edits BigBoxSettings.xml surgically; the panel sets/clears it and BigBox sees the change).
The plugin's separate PIN is retired by decision — everything below is evaluated under that rule.

## 1. Real losses

### 1.1 [LOSS — severe] Parental configuration is not PIN-gated
Plugin: the parental config UI was PIN-gated (PinPromptForm when HasPin); a locked child could
not open it. LiteBox: the Options window (→ Modules grid → Parental card/panel) opens with **no
PIN check while parental is locked** (verified: MainWindow options button → ShowDialog, no gate).
The PIN fields are masked, but a locked user can **disable the Parental module, empty the rules,
or change the PIN** freely. The whole protection is bypassable in four clicks.

### 1.2 [LOSS — security] Kiosk unlock persists
Plugin: kiosk (embedded WebView) unlocks set an IN-MEMORY per-process flag, never the cookie —
closing the kiosk re-locks, a child can't inherit an adult's unlock. LiteBox: the kiosk
WebKioskWindow uses the DEFAULT WebView2 environment (`EnsureCoreWebView2Async(null)`, verified)
→ persistent profile → the signed 12h `litebox_unlocked` cookie survives kiosk close AND host
restart.

### 1.3 [LOSS — security] No web unlock lockout
Plugin: the 3-attempt process-wide lockout also covered `/api/parental/unlock` (reason
`locked-out`, attemptsRemaining). LiteBox: the lockout exists desktop-side only — the web unlock
endpoint accepts unlimited tries against a 4-digit PIN.

### 1.4 [LOSS] No-PIN state fails OPEN on the web
Plugin: configured-but-no-PIN → forced locked, canUnlock=false (fail-closed). LiteBox: same
state → active but NOT locked (fail-open; documented as deliberate since there is nothing to
unlock against). The panel forces a PIN on first enable, so the state is an edge (PIN cleared
externally, e.g. by BigBox) — but the safe default is the plugin's.

### 1.5 [LOSS] Database-site grid ignores the rating RULES
Plugin: the wildcard rating rules were pushed into the web-db SQL (`BuildEsrbSqlFilter`,
GLOB/LIKE, whitelist "0"-short-circuit) so lists, counts and paging matched. LiteBox: the
db-site grid applies only the AO gate (`EffectiveAdult`); search post-filters ratings and the
detail 403s, but a locked child browsing a platform grid still SEES cards for games outside the
whitelist (titles + covers).

### 1.6 [LOSS — scoped] ForceWebHideAll does nothing on the web
Plugin: force-web short-circuited the LB-side SQL to match no row (and blanked the desktop).
LiteBox: `ForceAll` empties the DESKTOP list only; the embedded web keeps serving the (rule-
filtered) library while forced.

### 1.7 [LOSS — minor] PIN compare is not constant-time
Plugin used `CryptographicOperations.FixedTimeEquals`; LiteBox `string.Equals(Ordinal)`.
Local-only relevance, trivial fix.

## 2. [DECISION] BigBox-side coverage (the ASI)

The plugin's BigBox story: extenddb.asi (CreateFileW hook filtering Platforms\*.xml reads by
rating at BigBox boot, cold-start gated on LockPin presence), ~30 `Allow*WhileLocked` flags
forced false in BigBoxSettings.xml, the platform-XML write-guard (Block/Merge), the text-mode
filter-page hider, anti-tamper. LiteBox ships NONE of it: it writes the PIN (BigBox boots
locked) but a BigBox session's library is NOT rating-filtered. Note the ASI reads plain files
(`ExtendDBParental.dat` + `ExtendDBConfig.json`) — LiteBox could deploy the vendored
`extenddb.asi` and WRITE compatible files without the plugin. Options:
(a) deploy the ASI + emit the .dat/.json from ParentalConfig (BigBox parity without the plugin);
(b) declare BigBox-side filtering out of LiteBox's scope and reword the panel (today
`BigBoxEnabled` and the two "BigBox" hide-lists suggest BigBox is covered when only LiteBox's
own surfaces consume them).

## 3. Adapted / replaced (justified)

- Desktop filtering: WPF ICollectionView.Filter + force-web media blanker → native list/tree
  predicates + padlock + StateChanged rebuilds. Same outcomes on LiteBox's own UI.
- Web write-guard: platform-XML File.Copy guard (BigBox) → op-log web mutation guard
  (rate/favorite, Merge degraded to Block — documented). LiteBox never bulk-writes XML.
- LB-shell `?_unlock` ProcessUnlockToken + CefSharp intercepts → N/A (no embedded LB shell).
- Anti-tamper terminate → N/A (nothing to tamper out of).
- SQL rule engine: plugin GLOB via the read-intercept → LiteBox API-layer predicates (owned
  library lists are correctly filtered; the db-site gap is 1.5).
- Hidden-platform lists (locked/unlocked variants), wildcard match semantics, whitelist/
  blacklist modes, 3-attempt desktop lockout, boot-locked default, hotkey popup, one-shot
  install PIN, AllowLockedModify* toggles: byte-equivalent ports.

## 4. [BETTER]

Signed HMAC unlock cookie (the plugin's cookie was a plain `"1"` — forgeable); single BigBox
PIN (mandate); DPAPI-protected cookie key; padlock indicator + live StateChanged wiring.

## 5. Proposed fixes (pending approval)

| # | Item | Ref | Size |
|---|---|---|---|
| X1 | PIN-gate the parental surface while Active: opening the Parental panel AND toggling the Parental module card require VerifyPin (shared lockout) | 1.1 | S/M |
| X2 | Kiosk: ephemeral unlock — kiosk WebView2 marked (UA suffix) + in-memory per-window flag server-side, cookie never set for kiosk requests | 1.2 | S/M |
| X3 | Web unlock shares the 3-attempt lockout (reuse ParentalFilter counters; surface locked-out + attemptsRemaining like the plugin) | 1.3 | S |
| X4 | No-PIN + enabled → fail-closed everywhere (locked, canUnlock=false) | 1.4 | S |
| X5 | Push the rating rules into the db-site SQL (port BuildEsrbSqlFilter → GameListOptions extra WHERE; counts/paging correct) | 1.5 | M |
| X6 | ForceWebHideAll also blocks the embedded web (state short-circuit → empty lists/404s) | 1.6 | S |
| X7 | FixedTimeEquals for PIN compares | 1.7 | S |
| X8 | DECISION: BigBox coverage — (a) deploy ASI + write compatible .dat/.json, or (b) waive + reword panel | §2 | M / decision |
