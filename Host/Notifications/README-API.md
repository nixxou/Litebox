# LiteBox notifications — plugin API

A notification is a dark card in the bottom-right corner of the monitor LiteBox is on (above the taskbar),
plus a permanent entry behind the bell in the menu bar. Popups stack upward, the newest in the corner; past
the configured maximum the rest queue. The countdown pauses while the pointer is over a card, and a popup
that times out on its own stays **unread**, so the bell keeps its badge until you look.

There are two ways in. Both end up in the same center.

---

## 1. The LaunchBox way (nothing to change)

A plugin written for LaunchBox reaches its notification center by reflection, because the plugin SDK never
exposed one:

```csharp
var asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "LaunchBox");
var t   = asm?.GetType("Unbroken.LaunchBox.Windows.Desktop.Notifications.NotificationCenter");
t?.GetMethod("SendInfoNotification")?.Invoke(null, new object[] { "hello", 0 });
```

That works **as-is under LiteBox**. LiteBox registers a compatibility assembly whose identity is
`LaunchBox`, carrying LaunchBox's public Notifications API, forwarded to the center here. Nothing in the
plugin changes, and the same DLL keeps working under real LaunchBox.

Mirrored surface (`Unbroken.LaunchBox.Windows.Desktop.Notifications`):

| Member | Behaviour under LiteBox |
| --- | --- |
| `NotificationCenter.SendInfoNotification(msg, minimumLifeSpan)` | Info popup. `minimumLifeSpan` is **seconds**; `0` = the user's default. |
| `NotificationCenter.SendErrorNotification(msg, minimumLifeSpan)` | Error popup (red accent, longer default). |
| `NotificationCenter.SendInputNotification(msg, actions, minimumLifeSpan)` | Popup with a full-width button per action. Sticky — it waits for a choice. Returns the `Notification`. |
| `AddPassiveNotification(...)` / `AddNotificationCenterNotification(...)` | Same, the latter list-only (no popup). `markUnread`/`isInProgress` honoured. |
| `MarkNotificationRead` / `MarkNotificationUnread` / `RemoveNotification` | Route to the center. |
| `Notification.Dismiss()` | Closes the popup; the entry stays in the list. |
| `Notifications`, `NotificationRaised` / `Added` / `Read` / `Unread` / `Dismissed` | Live mirror of LiteBox's list, so a listener plugin sees LiteBox's own notifications too. |
| `SetBusy` / `IsBusy` / `BusyStateChanged` | Accepted; LiteBox has no global busy indicator, so nothing is shown. |
| `Notification.Icon` (`ImageSource`) | Accepted and kept; the WinForms popup draws its own glyph. |
| `Notification.Buttons` | `ObservableCollection<NotificationButton>`, not Caliburn's `BindableCollection<T>` (its base class). Only matters to code that names the type. |

Everything degrades to a no-op rather than throwing, so a plugin can call this on either frontend without
version checks. `ShimBridge.IsConnected` (in the same assembly) is `true` when the host is LiteBox.

Source: `LbShim/`. It is embedded in LiteBox.exe and loaded from memory — never written next to the exe,
which lives in `<LB>\Core` beside the real `LaunchBox.dll`.

---

## 2. The LiteBox way (more control)

The center is public in the LiteBox assembly, under `LiteBox.Notifications`. Reach it the same way:

```csharp
var lb = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "LiteBox");
var nc = lb?.GetType("LiteBox.Notifications.NotificationCenter");

nc?.GetMethod("Info",  new[] { typeof(string), typeof(int) })?.Invoke(null, new object[] { "Done.", 0 });
nc?.GetMethod("Error", new[] { typeof(string), typeof(int) })?.Invoke(null, new object[] { "Failed.", 0 });
```

| Member | Notes |
| --- | --- |
| `LiteBoxNotification Info(string, int lifeSpanSeconds = 0)` | `0` = default, negative = sticky. |
| `LiteBoxNotification Error(string, int lifeSpanSeconds = 0)` | |
| `LiteBoxNotification Progress(string)` | Sticky. Update it as work advances. |
| `LiteBoxNotification Input(string, IEnumerable<KeyValuePair<string, Action>>, int)` | Buttons; always sticky. |
| `LiteBoxNotification Send(message, kind, lifeSpanSeconds, actions, icon, popup, markUnread)` | The full form — `icon` is a `System.Drawing.Image`, `popup:false` records without showing. |
| `void Update(n, string)` / `void Complete(n, string, bool error, int)` | Rewrite in place; `Complete` turns a progress card into its result and restarts its countdown. |
| `void MarkRead / MarkUnread / MarkAllRead / Dismiss / Remove / Clear` | `Dismiss` closes the popup, `Remove` forgets the notification. |
| `IReadOnlyList<LiteBoxNotification> All`, `int UnreadCount`, `int Count` | Newest first. |
| `event Action<LiteBoxNotification> Raised / Read / Unread / Dismissed / Removed / Updated`, `event Action Changed` | Fire on the calling thread — marshal before touching UI. |

`LiteBoxNotification` carries `Id`, `Message`, `Kind`, `DateRaised`, `DateRead`, `IsRead`, `IsError`,
`IsInProgress`, `LifeSpanSeconds`, `Icon`, `Actions`, `IsShowing`, a free `Tag`, and the same verbs as
instance methods (`MarkRead()`, `Dismiss()`, `Remove()`, `Update(msg)`).

Any thread may call any of it: the center is lock-guarded and marshals display work to the UI thread
itself.

---

## The web frontends

Notifications reach LB web too: `vendor\notify.js` — included by the **litebox** surface only, kiosk or
plain browser — polls `GET /api/notifications/events?since=<seq>` every ~2.5 s (the recent-epoch cadence)
and renders arriving notifications as bottom-right cards styled like the native ones. Buttons work:
`POST /api/notifications/<id>/action/<n>` runs the plugin callback on the host's UI thread;
`<id>/read|unread|dismiss|remove` cover the rest, and `POST /api/notifications/test` raises the same demo
pair as Options ▸ "Send a test notification". These routes are mounted with the LiteBox surface, so
`[Web] EnableLiteBoxWeb=false` removes them entirely.

A freshly opened page starts clean (baseline seq, no backlog replay) — history is the native bell's job.
The kiosk is the exception: its window silences the native popups from the moment it opens, seconds
before its page can poll, so its first poll replays from that moment. Nothing raised in the gap is lost
to both displays.

**While a HOST KIOSK window is open**, the native popups are suppressed (a topmost toast fights the
topmost kiosk). What shows instead depends on the kiosk:

* **LB-web kiosk** — its page runs `notify.js`, so the notification appears there as a web card;
* **BigBox-web kiosk** — the couch UI shows nothing on purpose (no `notify.js`): the notification goes
  **straight to the bell**, unread, for after the session.

A plain browser on the site does **not** suppress the native popups — its page may show a web card too;
a visible duplicate beats a notification lost behind a window LiteBox doesn't control. Either way the
notification stays in the center until it is read or removed, so nothing is lost when the kiosk closes.

## Where it shows up

`Options ▸ Notifications`:

* **Notification system** — *Follow LaunchBox* (default; reads LaunchBox's own
  `Options ▸ General ▸ Notifications` choice), *LiteBox popups*, *Windows notifications* (a tray balloon,
  which Windows renders as its own toast and files in the Action Center), *Message boxes*. A notification
  carrying actions always gets a LiteBox popup — the other two have nowhere to put buttons.
* **Popup duration** / **Error popup duration** / **Maximum popups on screen**.
* **Show popups while a game is running** — off by default: an always-on-top popup over a fullscreen game
  can knock it out of exclusive mode. So while a game runs, a notification goes **straight to the bell**
  (unread); popups already on screen are swept when the game launches, and queued ones stay queued-out —
  everything is behind the bell after the session.
* **Send a test notification** / **…with buttons** / **…in 10 seconds** — one question each: does a plain
  one work, do the actions work, and does it reach whatever you put on screen in the meantime (the delayed
  one returns immediately so you can close Options and open a kiosk or launch a game first).

`LiteBox.exe --notify-demo` raises one of each shape at startup (and walks the LaunchBox-compat path by
reflection, logging each step) — the fastest way to check the stack after a change.
