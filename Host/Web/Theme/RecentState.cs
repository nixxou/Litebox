// A monotonic "the Recent rows / running heartbeat may have changed" counter for the theme surfaces.
//
// The theme client caches recent.json per node in-memory (no TTL). It reads this epoch (/api/recent/epoch) and
// appends it to its recent.json URLs as a cache-buster, so a bump makes the NEXT visit of any node refetch. It
// also carries the "is a game currently running" flag so the theme can disable Play while a launch is in
// flight, and an extraction flag as defence-in-depth.
//
// Clean-room LiteBox rewrite of ExtendDB's Web/Backend/RecentState.cs. The launch lifecycle here is LiteBox's
// own HostLaunch.GameStarted / GameEnded (WinForms-native), not the plugin's LB hook — subscribed once via the
// static ctor so the flag + epoch stay live without any per-request wiring.

using System;
using System.Threading;
using LbApiHost.Host;

namespace LbApiHost.Host.Web;

internal static class RecentState
{
    private static long _epoch;
    private static int _isGameRunning;     // 0 = idle, 1 = running (Interlocked).
    private static int _extractionDepth;   // 0 = idle, >0 = at least one extraction in flight (Interlocked).

    static RecentState()
    {
        // Wire LiteBox's native launch lifecycle to the running heartbeat. Idempotent-ish: the ctor runs once.
        try
        {
            HostLaunch.GameStarted += _ => MarkRunning();
            HostLaunch.GameEnded += _ => MarkIdle();
        }
        catch { /* best-effort: absent events just leave the flag at idle */ }
    }

    /// <summary>Current epoch. Changes ⇒ clients refetch their recent rows AND the running heartbeat.</summary>
    public static long Epoch => Interlocked.Read(ref _epoch);

    /// <summary>True between a game launch start and its exit — the theme refuses a second Play meanwhile.</summary>
    public static bool IsGameRunning => Interlocked.CompareExchange(ref _isGameRunning, 0, 0) != 0;

    /// <summary>True while an archive extraction is in flight (defence-in-depth on top of IsGameRunning).</summary>
    public static bool IsExtractionInProgress => Interlocked.CompareExchange(ref _extractionDepth, 0, 0) > 0;

    /// <summary>Bump the epoch (called on any lifecycle change).</summary>
    public static void Bump() => Interlocked.Increment(ref _epoch);

    public static void MarkRunning() { Interlocked.Exchange(ref _isGameRunning, 1); Bump(); }
    public static void MarkIdle() { Interlocked.Exchange(ref _isGameRunning, 0); Bump(); }

    public static void MarkExtracting() { Interlocked.Increment(ref _extractionDepth); Bump(); }
    public static void MarkExtractionDone()
    {
        int v = Interlocked.Decrement(ref _extractionDepth);
        if (v < 0) Interlocked.Exchange(ref _extractionDepth, 0);
        Bump();
    }

    /// <summary>Touch the static ctor so the HostLaunch subscription is wired at server start.</summary>
    public static void EnsureWired() { /* referencing the type runs the static ctor */ }
}
