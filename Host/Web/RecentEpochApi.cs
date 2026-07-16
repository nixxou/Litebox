// GET /api/recent/epoch — the cache-buster the themes poll (~2 s) to know when a "recently played" row or a
// store install-state badge may have changed, so they can refresh those tiles without a full reload.
//
// Small by design for S2: a monotonic in-process counter that later slices bump when they mutate recent /
// install state. isGameRunning / extractionInProgress / installEpoch are reported with placeholder values
// until their subsystems land (theme-data slice) — the JSON SHAPE matches ExtendDB's so the shipped theme JS
// reads it unchanged.

#nullable enable

using System.Text.Json;
using System.Threading;

namespace LbApiHost.Host.Web;

internal static class RecentEpoch
{
    private static long _epoch;

    /// <summary>Current cache-buster value.</summary>
    public static long Value => Interlocked.Read(ref _epoch);

    /// <summary>Bump the epoch — call after changing a game's recent / play state so clients refresh.</summary>
    public static void Bump() => Interlocked.Increment(ref _epoch);
}

internal static class RecentEpochApi
{
    // S4 wired the placeholders to real state: RecentState (bumped on HostLaunch start/exit) drives the
    // recent-row cache-buster + the running/extraction heartbeat; WebStoreState.Epoch bumps when a store
    // install-state change is detected. The JSON shape is unchanged so the shipped theme JS reads it as-is.
    public static HttpResponse Handle(RouteContext ctx)
    {
        RecentState.EnsureWired();   // ensure the HostLaunch subscription is live
        return HttpResponse.Json(JsonSerializer.Serialize(new
        {
            epoch = RecentState.Epoch,
            isGameRunning = RecentState.IsGameRunning,
            extractionInProgress = RecentState.IsExtractionInProgress,
            installEpoch = (long)WebStoreState.Epoch,
        }));
    }
}
