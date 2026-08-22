// A message loop that belongs to the plugins, so their work never runs on ours.
//
// LaunchBox hands plugin events to its own UI thread, and a plugin is written for that: ThirdScreen answers
// PluginInitialized by enumerating displays, building a window on a second monitor and starting a VLC
// surface, all synchronously. Delivered on LiteBox's UI thread that is a visible freeze; delivered on a
// plain worker thread the window is built where nothing pumps, so it never appears at all (which is exactly
// what happened before this existed).
//
// So: a THIRD thread, STA, running a WinForms message loop of its own. Plugin windows created there are
// pumped there, and our window keeps painting throughout. The loop also pumps a WPF Dispatcher created on
// this thread — same-thread interop, the trick the GUI thread already relies on — which matters because
// plugin windows are often WPF.
//
// One thing this cannot fix: a plugin that marshals its own work to System.Windows.Application.Current
// .Dispatcher lands back on the GUI thread, because that is where the WPF Application was created. Nothing
// short of not having a WPF Application would change that, and ExtendDB needs it.

#nullable enable

using System;
using System.Threading;
using System.Windows.Forms;

namespace LbApiHost.Host;

internal static class PluginUiThread
{
    private static Thread? _thread;
    private static Control? _marshal;      // an HWND on that thread, purely to BeginInvoke onto it
    private static volatile bool _down;

    /// <summary>True once the loop is up and <see cref="Post"/> will actually reach it.</summary>
    public static bool Running => !_down && _marshal is { IsDisposed: false, IsHandleCreated: true };

    /// <summary>Start the loop and return once it can accept work. Idempotent.</summary>
    public static void Start()
    {
        if (_thread != null) return;
        using var ready = new ManualResetEventSlim(false);
        _thread = new Thread(() =>
        {
            try
            {
                // A control with a real handle is the cheapest marshalling target WinForms offers, and it
                // ties the queue to THIS thread's loop.
                var c = new Control();
                var _ = c.Handle;               // force creation before anyone posts
                _marshal = c;
                ready.Set();
                Application.Run(new ApplicationContext());   // pumps until ExitLoop
            }
            catch (Exception ex) { Console.WriteLine("[plugin-ui] loop ended: " + ex.Message); }
            finally { _down = true; }
        })
        { Name = "LbApiHost-PluginUI", IsBackground = true };   // background: a stuck plugin must not hold the process open
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        // Bounded: if the thread cannot start we carry on without it and callers fall back to running inline,
        // which is what happened for the whole of LiteBox's life until now.
        if (!ready.Wait(TimeSpan.FromSeconds(5)))
            Console.WriteLine("[plugin-ui] thread did not come up in time — plugin events will run inline");
    }

    /// <summary>Run <paramref name="work"/> on the plugin loop, without waiting for it. Falls back to running
    /// it inline when the loop is not available — losing the isolation, never the event.</summary>
    public static void Post(Action work)
    {
        if (work == null) return;
        var m = _marshal;
        if (!Running || m == null) { RunSafely(work); return; }
        try { m.BeginInvoke(work); }
        catch (Exception ex) { Console.WriteLine("[plugin-ui] post failed, running inline: " + ex.Message); RunSafely(work); }
    }

    private static void RunSafely(Action work)
    {
        try { work(); }
        catch (Exception ex) { Console.WriteLine("[plugin-ui] " + ex.GetType().Name + ": " + ex.Message); }
    }

    /// <summary>Block until everything posted so far has run, or <paramref name="ms"/> elapses. For shutdown:
    /// the last event a plugin gets is its chance to tear its windows down, and posting it and immediately
    /// killing the loop would deliver nothing.</summary>
    public static void Drain(int ms = 3000)
    {
        var m = _marshal;
        if (!Running || m == null) return;
        using var done = new ManualResetEventSlim(false);
        try { m.BeginInvoke((Action)(() => done.Set())); } catch { return; }
        if (!done.Wait(ms)) Console.WriteLine("[plugin-ui] still busy after " + ms + "ms — going down anyway");
    }

    /// <summary>Ask the loop to end. Best-effort: the thread is a background one, so a plugin that refuses to
    /// return cannot keep the process alive either way.</summary>
    public static void Stop()
    {
        _down = true;
        var m = _marshal;
        if (m == null) return;
        try { m.BeginInvoke((Action)(() => Application.ExitThread())); } catch { }
    }
}
