// Single-instance guard. LiteBox's write-back owns the LaunchBox Platform XMLs and the op-log
// (Core\LiteBox.pending.db) — only ONE process may write them at a time.
//
// The ordinary double-click never gets here any more: HostBoot.Run refuses an ARGUMENT-LESS launch
// outright when another host from the same install is already up, and says so with a tray balloon. What
// this guard still covers is everything that carries arguments — a --headless diagnostic, a --drylaunch
// session — started alongside a running host. Those we don't refuse (they are deliberate, and often the
// point is to observe the running one); instead we run them in a FORCED read-only mode:
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
