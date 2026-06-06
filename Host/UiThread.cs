// A dedicated STA thread running a WinForms message loop, so plugins that open
// dialogs from menu actions (e.g. a config window) have a UI thread to live on.
// Marshal work onto it via Invoke().

using System;
using System.Threading;
using System.Windows.Forms;

namespace LbApiHost.Host;

internal static class UiThread
{
    private static Thread _thread;
    private static Control _marshal;
    private static readonly ManualResetEventSlim _ready = new(false);

    public static void Start()
    {
        if (_thread != null) return;
        _thread = new Thread(() =>
        {
            _marshal = new Control();
            _ = _marshal.Handle;       // force the window handle on this thread
            _ready.Set();
            Application.Run();          // pump until ExitThread
        })
        { IsBackground = true, Name = "LbApiHost-UI" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(5000);
    }

    public static void Invoke(Action action)
    {
        if (_marshal == null) { action(); return; }
        try { _marshal.Invoke(action); }
        catch (Exception ex) { Console.WriteLine("[ui] Invoke failed: " + ex.Message); }
    }
}
