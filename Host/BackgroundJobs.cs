// "Is LiteBox busy with work the USER asked for?" — the flag that stops a game launch from pulling the
// rug out from under it.
//
// Launching a game normally frees everything LiteBox can spare (the game cache, libvlc, the dup-check
// CNN session): LiteBox is idle then and the RAM belongs to the game. That reasoning collapses when the
// user has started a long job from the UI — Generate Media Cache — and then launches something while it
// runs: those are the very resources the job is using, and dropping them would at best restart work, at
// worst break the job (a video-thumb phase whose libvlc vanished, a valid-set built from an emptied
// cache).
//
// So: while a job is registered, the launch drops are SKIPPED. And because they were skipped, the exit
// path must NOT "restore" them either — rebuilding a cache that was never cleared would be pointless
// churn on top of the still-running job. HostServices remembers what it actually dropped.

#nullable enable

using System;
using System.Threading;

namespace LbApiHost.Host;

internal static class BackgroundJobs
{
    private static int _active;

    /// <summary>A user-initiated long job is running (Generate Media Cache…).</summary>
    public static bool Busy => Volatile.Read(ref _active) > 0;

    /// <summary>Register a job for the lifetime of the returned token. Dispose (or let `using` do it)
    /// when it ends — including on cancel or failure.</summary>
    public static IDisposable Enter(string what) => new Token(what);

    private sealed class Token : IDisposable
    {
        private readonly string _what;
        private int _done;

        public Token(string what)
        {
            _what = what;
            int n = Interlocked.Increment(ref _active);
            Console.WriteLine($"[jobs] '{_what}' started ({n} running) — launch-time unloading suspended");
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 1) return;   // idempotent
            int n = Interlocked.Decrement(ref _active);
            Console.WriteLine($"[jobs] '{_what}' finished ({n} still running)");
        }
    }
}
