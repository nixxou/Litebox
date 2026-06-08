// Single-instance guard. LiteBox's write-back owns the LaunchBox Platform XMLs and the op-log
// (Core\LiteBox.pending.db) — only ONE process may write them at a time. When a second LiteBox is
// launched while one is already running we don't refuse to start (the user may want a read-only
// second view); instead we run that instance in a FORCED read-only mode:
//   • store.ReadOnly is set true IN MEMORY (the LiteBox.ini value is never touched);
//   • the GUI surfaces a warning (coloured caption + banner) and locks the options menu.
// The named mutex handle is kept alive for the whole process lifetime so a third instance also sees
// us. Per-session ("Local\") scope is what we want: two LiteBox in the same Windows session is the
// case to guard (each session has its own LB process / files anyway).

using System;
using System.Threading;

namespace LbApiHost.Host;

internal static class InstanceGuard
{
    private static Mutex _mutex;   // kept alive (static) → the name persists while we run

    /// <summary>True when another LiteBox instance was already running when this one started.</summary>
    public static bool AnotherInstanceRunning { get; private set; }

    /// <summary>Probe once at startup. Idempotent; any failure → behave as the sole instance.</summary>
    public static void Probe()
    {
        if (_mutex != null) return;
        try
        {
            _mutex = new Mutex(initiallyOwned: false, @"Local\LiteBox.SingleInstance", out bool createdNew);
            AnotherInstanceRunning = !createdNew;   // someone else already created the name
        }
        catch
        {
            AnotherInstanceRunning = false;   // can't tell → don't cripple the only instance we know of
        }
    }
}
