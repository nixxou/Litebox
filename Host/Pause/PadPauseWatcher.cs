// Watches XInput controller 0 for the configured pause button/combo WHILE a game runs and
// fires the pause toggle — the controller twin of the keyboard pause hotkey. Deliberately
// gentle: one XInputGetState every ~66 ms (~15 Hz) on a background thread, NOT a tight loop
// (the game owns the CPU). Rising-edge on the FULL combo, so a held combo fires once.
//
// No input framework, no library — it reuses XInputPad's static reader (a single P/Invoke to
// the system xinput DLL). Started/stopped by PauseManager around the game's lifetime.

#nullable enable

using System.Threading;

namespace LbApiHost.Host.Pause;

internal static class PadPauseWatcher
{
    private const int PollMs = 66;   // ~15 Hz — responsive enough for a menu trigger, easy on the CPU

    private static Thread? _thread;
    private static volatile bool _run;
    private static ushort _mask;
    private static Action? _onPause;

    /// <summary>Start watching for <paramref name="combo"/> (e.g. "Back+Start"). No-op when the
    /// combo is empty/unparseable. Idempotent — a running watcher is replaced.</summary>
    public static void Start(string? combo, Action onPause)
    {
        Stop();
        _mask = XInputPad.ComboToMask(combo);
        if (_mask == 0) { Console.WriteLine("[pad-pause] no valid combo — controller pause off"); return; }
        _onPause = onPause;
        _run = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "LiteBox-padpause" };
        _thread.Start();
        Console.WriteLine($"[pad-pause] watching controller 0 for '{combo}'");
    }

    public static void Stop()
    {
        _run = false;
        var t = _thread;
        _thread = null;
        // Join (bounded — PollMs is 66ms, so the loop notices _run=false almost immediately) so a
        // fast Stop()-then-Start() (Start() always calls Stop() first) can't leave the old Loop()
        // thread still alive when the new one spawns and _mask/_onPause get reassigned underneath it —
        // that would let both threads poll concurrently and double-fire a single combo press.
        if (t != null && t.IsAlive) { try { t.Join(500); } catch { } }
    }

    private static void Loop()
    {
        ushort prev = 0;
        while (_run)
        {
            try
            {
                ushort b = XInputPad.ReadButtons0();
                // Fire on the rising edge of the WHOLE combo (all its buttons newly pressed together).
                if ((b & _mask) == _mask && (prev & _mask) != _mask)
                { try { _onPause?.Invoke(); } catch { } }
                prev = b;
            }
            catch { }
            Thread.Sleep(PollMs);
        }
    }
}
