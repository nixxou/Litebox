// The display half of the notification center: where a popup lands, how many share the corner, and which
// mechanism shows it at all.
//
// PLACEMENT — bottom-right of the WORKING AREA of the monitor the LiteBox window is on. Working area, not
// bounds, so the card sits above the taskbar wherever the user keeps it; "the monitor the window is on",
// so on a multi-head setup notifications appear where you are looking, not on the primary screen.
//
// STACKING — the newest card takes the corner and the older ones slide UP. Past MaxPopups the rest queue
// and appear as slots free, so a burst of twenty notifications cannot paper over the screen. Every card
// is still in the bell list the whole time — the popup is a courtesy, the list is the record.
//
// ROUTING — LiteBox popups / Windows notifications (a tray balloon, which Windows renders as its own toast
// and files in the Action Center) / message boxes. The choice follows LaunchBox's own setting unless
// overridden; see NotificationSettings.

#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using LiteBox.Notifications;

namespace LbApiHost.Host.Notifications;

internal sealed class NotificationUi : INotificationSink
{
    private readonly Form _owner;
    private readonly List<NotificationToast> _toasts = new();   // oldest first; the LAST one owns the corner
    private readonly Queue<LiteBoxNotification> _queue = new();
    private NotifyIcon? _tray;
    /// <summary>The notification the balloon CURRENTLY on screen stands for. One NotifyIcon serves every
    /// balloon, so the click handler must resolve this at click time — see ShowBalloon.</summary>
    private LiteBoxNotification? _balloon;

    public static NotificationUi? Current { get; private set; }

    private NotificationUi(Form owner) { _owner = owner; }

    /// <summary>Hands the center a place to draw. Call once, from the main window's constructor.</summary>
    public static void Attach(Form owner)
    {
        var ui = new NotificationUi(owner);
        Current = ui;
        NotificationCenter.Sink = ui;
        // Boot raises notifications before there is a window (plugin init, migrations, the catalog). They
        // are all in the list already; put the unread ones on screen once there IS somewhere to draw,
        // oldest first, so nothing raised during startup is silently swallowed.
        owner.Shown += (_, _) =>
        {
            foreach (var n in NotificationCenter.All.Reverse().Where(n => !n.IsRead && !n.IsShowing))
                ui.ShowOnUi(n);
        };
        // A game launch takes the screen: sweep the popups that are already up (see OnGameStarted).
        Action<Unbroken.LaunchBox.Plugins.Data.IGame> onGameStarted = _ => ui.OnGameStarted();
        HostLaunch.GameStarted += onGameStarted;
        owner.FormClosed += (_, _) => { HostLaunch.GameStarted -= onGameStarted; Detach(); };
    }

    /// <summary>A game just launched — the screen belongs to it now. Close every visible popup and drop
    /// the queue: a topmost toast (sticky ones especially) can cost a fullscreen game its exclusive mode,
    /// the same trap the startup cover works around. Nothing is lost — the notifications stay in the
    /// center, unread unless already read, behind the bell; and new ones arriving DURING the game are
    /// gated by ShowOnUi, so arrival-before-launch doesn't earn screen time arrival-after wouldn't get.
    /// The "show popups while a game is running" option keeps everything up instead.</summary>
    private void OnGameStarted()
    {
        if (NotificationSettings.DuringGame) return;
        Post(() =>
        {
            foreach (var t in _toasts.ToArray()) t.FadeOut();   // FadeOut marks nothing read — bell keeps the badge
            _queue.Clear();
        });
    }

    public static void Detach()
    {
        var ui = Current;
        Current = null;
        if (NotificationCenter.Sink == ui) NotificationCenter.Sink = null;
        ui?.CloseAll();
    }

    private float Dpi => _owner.DeviceDpi / 96f;
    private int S(int px) => (int)Math.Round(px * Dpi);

    // ── INotificationSink ────────────────────────────────────────────────────────────────────────────

    public void Show(LiteBoxNotification n) => Post(() => ShowOnUi(n));

    public void Close(LiteBoxNotification n) => Post(() =>
    {
        var t = _toasts.FirstOrDefault(x => ReferenceEquals(x.Model, n));
        if (t != null) t.FadeOut();
        // Not on screen yet? Then it must not pop up later either.
        if (_queue.Contains(n))
        {
            var keep = _queue.Where(q => !ReferenceEquals(q, n)).ToArray();
            _queue.Clear();
            foreach (var q in keep) _queue.Enqueue(q);
        }
    });

    public void Refresh() => Post(() =>
    {
        foreach (var t in _toasts.ToArray()) t.Sync();
        Relayout();
    });

    // ── Showing ──────────────────────────────────────────────────────────────────────────────────────

    private void ShowOnUi(LiteBoxNotification n)
    {
        // A topmost popup over a fullscreen game can knock it out of exclusive mode (the trap the startup
        // cover works around by standing down). Default is therefore to stay quiet during a game — the
        // notification is in the list and the bell will show it afterwards.
        if (HostLaunch.GameRunning && !NotificationSettings.DuringGame) return;

        // The HOST KIOSK owns the screen: while one is up, every native route stays quiet (a topmost toast
        // fights the topmost kiosk; the balloon and the message box would sit behind it or steal its
        // focus). What happens instead depends on which kiosk:
        //   • LB-web kiosk — its page runs vendor\notify.js, so the notification shows THERE, as a web card;
        //   • BigBox-web kiosk — the couch UI shows nothing on purpose (no notify.js): the notification goes
        //     straight to the bell, unread, for after the session.
        // A plain BROWSER on the web site does NOT suppress the native popups — its page may show a web card
        // too, and a visible duplicate beats a notification lost behind a window we don't control. Either
        // way the notification is in the list until it is read or removed.
        try { if (KioskIsOpen()) { Console.WriteLine("[notify] popup suppressed — web kiosk open"); return; } } catch { }

        switch (NotificationSettings.System)
        {
            case NotificationSystem.Windows when n.Actions.Count == 0:
                ShowBalloon(n);
                return;
            case NotificationSystem.MessageBox when n.Actions.Count == 0:
                ShowMessageBox(n);
                return;
            // Actions have no equivalent in a balloon or a message box — those routes would silently drop
            // the user's choices, so a notification that asks something always gets a LiteBox popup.
            default:
                ShowToast(n);
                return;
        }
    }

    private void ShowToast(LiteBoxNotification n)
    {
        var existing = _toasts.FirstOrDefault(t => ReferenceEquals(t.Model, n));
        if (existing != null) { existing.Sync(); existing.RestartLife(); return; }   // already up: refresh in place

        if (_toasts.Count >= NotificationSettings.MaxPopups)
        {
            if (!_queue.Contains(n)) _queue.Enqueue(n);
            return;
        }

        var toast = new NotificationToast(n, OnToastClosed);
        _toasts.Add(toast);
        n.IsShowing = true;
        Relayout();          // position BEFORE the first paint, so it never flashes at (0,0)
        try { toast.Show(); } catch (Exception ex) { Console.WriteLine("[notify] popup failed: " + ex.Message); }
        Relayout();
    }

    private void OnToastClosed(NotificationToast toast)
    {
        _toasts.Remove(toast);
        Relayout();
        // Queued items re-enter through ShowOnUi, not straight onto the screen: the world may have
        // changed while they waited (a game started, a kiosk opened) and the gates must re-run. A gated
        // item simply drops out of the queue — it lives on in the bell, unread.
        while (_queue.Count > 0 && _toasts.Count < NotificationSettings.MaxPopups)
            ShowOnUi(_queue.Dequeue());
    }

    /// <summary>Newest card in the corner, older ones stacked upward. Re-run after every add/remove.</summary>
    private void Relayout()
    {
        Rectangle wa;
        try { wa = (Screen.FromControl(_owner) ?? Screen.PrimaryScreen!).WorkingArea; }
        catch { wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720); }

        int margin = S(12), gap = S(8);
        int y = wa.Bottom - margin;
        for (int i = _toasts.Count - 1; i >= 0; i--)
        {
            var t = _toasts[i];
            if (t.IsDisposed) continue;
            y -= t.Height;
            try { t.Location = new Point(wa.Right - margin - t.Width, Math.Max(wa.Top + margin, y)); } catch { }
            y -= gap;
        }
    }

    private void CloseAll()
    {
        foreach (var t in _toasts.ToArray()) { try { t.Close(); } catch { } }
        _toasts.Clear();
        _queue.Clear();
        try { if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; } } catch { }
    }

    // ── The two "not our popup" routes ───────────────────────────────────────────────────────────────

    /// <summary>Windows notifications. A NotifyIcon balloon is the one path that needs no AppUserModelID
    /// registration: Windows 10/11 turn it into a real toast and keep it in the Action Center. The icon
    /// only exists while a balloon is pending — LiteBox does not squat in the tray.</summary>
    private void ShowBalloon(LiteBoxNotification n)
    {
        try
        {
            if (_tray == null)
            {
                _tray = new NotifyIcon { Text = "LiteBox", Icon = _owner.Icon ?? SystemIcons.Application };
                _tray.BalloonTipClosed += (_, _) => { _balloon = null; HideTray(); };
                // Resolve the notification at CLICK time, through _balloon: the handlers are wired ONCE,
                // with the icon, but every later balloon reuses that icon. A handler closing over this
                // call's `n` would mark the FIRST notification read forever after, whichever balloon the
                // user actually clicked.
                _tray.BalloonTipClicked += (_, _) => { NotificationCenter.MarkRead(_balloon); _balloon = null; HideTray(); };
            }
            // Windows shows one balloon at a time — a new one replaces whatever is up, so the latest is
            // always the one a click refers to.
            _balloon = n;
            _tray.Visible = true;
            _tray.ShowBalloonTip(Math.Max(3, NotificationSettings.EffectiveSeconds(n)) * 1000,
                                 "LiteBox", n.Message, n.IsError ? ToolTipIcon.Error : ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[notify] balloon failed (" + ex.Message + ") — falling back to a LiteBox popup");
            ShowToast(n);
        }
    }

    private void HideTray() { try { if (_tray != null) _tray.Visible = false; } catch { } }

    /// <summary>Message boxes. Modal by nature — that IS the setting: it stops what you are doing.</summary>
    private void ShowMessageBox(LiteBoxNotification n)
    {
        try
        {
            MessageBox.Show(_owner, n.Message, "LiteBox", MessageBoxButtons.OK,
                n.IsError ? MessageBoxIcon.Error : MessageBoxIcon.Information);
            NotificationCenter.MarkRead(n);
        }
        catch (Exception ex) { Console.WriteLine("[notify] message box failed: " + ex.Message); }
    }

    /// <summary>Isolated + NoInlining: WebKioskWindow's fields reference WebView2 types, which are ABSENT
    /// on LaunchBox 13.27 — jitting a method that touches the type throws there, and the caller's catch
    /// must be able to turn that into "no kiosk" without this method having been inlined into it.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static bool KioskIsOpen() => Web.Kiosk.WebKioskWindow.IsOpen;

    // ── Plumbing ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Runs on the UI thread. Every entry point goes through here: a plugin raises notifications
    /// from whatever thread it likes.</summary>
    private void Post(Action a)
    {
        try
        {
            if (_owner.IsDisposed || !_owner.IsHandleCreated) return;
            if (_owner.InvokeRequired) _owner.BeginInvoke(a);
            else a();
        }
        catch { /* window going away mid-notification */ }
    }
}
