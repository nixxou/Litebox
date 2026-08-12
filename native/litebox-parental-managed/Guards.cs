// One entry point that arms both plugin-side guards (SOFT admin-window lock + HARD managed write-guard), each
// independently fail-safe. The soft lock needs WPF, which may not be loaded yet at the very first call (plugin
// ctor); so if it hasn't armed, we poll briefly in the background until it does — no dependency on a later
// LaunchBox event or user action. The poll is one-shot and bounded, and stops as soon as AdminGuard is done
// (patched, or a real Harmony failure).

using System.Threading;
using System.Threading.Tasks;

namespace LiteBoxParental
{
    internal static class Guards
    {
        private static int _pollStarted;

        public static void Arm()
        {
            try { AdminGuard.Install(); } catch { }          // SOFT: block admin windows while locked
            try { ManagedHardGuard.Install(); } catch { }    // HARD (managed twin): block library writes while locked

            if (!AdminGuard.Installed && Interlocked.Exchange(ref _pollStarted, 1) == 0)
            {
                Task.Run(() =>
                {
                    for (int i = 0; i < 40 && !AdminGuard.Installed; i++)   // ~20 s max, then give up
                    {
                        try { Thread.Sleep(500); } catch { }
                        try { AdminGuard.Install(); } catch { }
                    }
                });
            }
        }
    }
}
