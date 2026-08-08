// --selftest-bakeleak [bakes]: the bake pool must not grow with the number of models it produces.
//
// It used to, by about 5 MB each. Touching a Viewport3D or a RenderTargetBitmap makes WPF attach a
// Dispatcher to the thread, and a static WPF table holds that dispatcher — with its MediaContext and
// everything rendered through it — until it is shut down. On workers that never ended, generating the
// library climbed past 3 GB and finished on a run of "Insufficient memory" thumb failures. Workers now
// retire every few bakes, which is the only way to let a dispatcher go.
//
// Measured as a PLATEAU, not as a total: the first bakes legitimately cost memory (WPF spins up, the
// shipped case models and preset images decode once, the GC has no reason to run yet). What must not
// happen is a floor that keeps rising. So it bakes a warm-up, takes a baseline, bakes as much again, and
// compares — a leak of the old size shows up as hundreds of megabytes between the two marks, while a
// healthy pool wanders around a fixed level.

#nullable enable

using System;
using System.Linq;
using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Diag;

internal static class BakeLeakSelfTest
{
    public static int Run(int bakes)
    {
        IGame[] all;
        try { all = PluginHelper.DataManager?.GetAllGames() ?? Array.Empty<IGame>(); }
        catch (Exception ex) { Console.WriteLine("[selftest-bakeleak] no catalogue: " + ex.Message); return 1; }

        // DIFFERENT games, spread across the library — which is what Generate Media Cache does, and what
        // makes the difference here. Baking one game repeatedly reuses the same decoded art and the same
        // composed textures, so it barely grows even with the leak present: a first version of this test
        // did exactly that and passed with worker recycling disabled. The leak is in what the RENDER
        // leaves behind, and that is per model.
        var work = new System.Collections.Generic.List<Model3d.Model3dCache.Identity>();
        int step = Math.Max(1, all.Length / Math.Max(1, bakes * 2));
        for (int i = 0; i < all.Length && work.Count < bakes * 2; i += step)
        {
            Model3d.Model3dCache.Identity? one = null;
            try { one = Model3d.Model3dCache.Resolve(all[i]); } catch { }
            if (one != null && one.HasArt) work.Add(one);
        }
        if (work.Count < 4) { Console.WriteLine("[selftest-bakeleak] not enough games with case art — cannot judge"); return 1; }

        int warm = Math.Max(20, bakes / 4);
        Console.WriteLine($"[selftest-bakeleak] {work.Count} distinct models available — {warm} warm-up + {bakes} measured bakes"
                        + $", {Model3d.Model3dBaker.WorkerCount} worker(s)");

        int cursor = 0;
        long Bake(int n)
        {
            for (int i = 0; i < n; i++)
            {
                var it = work[cursor++ % work.Count];
                if (Model3d.Model3dBaker.Run(() => Model3d.Model3dBaker.Bake(it.Map, it.Title, it.Art)) == null)
                { Console.WriteLine($"[selftest-bakeleak] bake returned null for \"{it.Title}\""); return -1; }
            }
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            var p = System.Diagnostics.Process.GetCurrentProcess();
            p.Refresh();
            return p.PrivateMemorySize64 / (1024 * 1024);
        }

        long baseline = Bake(warm);
        if (baseline < 0) return 1;
        Console.WriteLine($"[selftest-bakeleak] baseline after warm-up: {baseline} MB");

        long after = Bake(bakes);
        if (after < 0) return 1;
        long growth = after - baseline;
        Console.WriteLine($"[selftest-bakeleak] after {bakes} more: {after} MB  (growth {growth:+#;-#;0} MB)");

        // The leak was ~5 MB per bake; anything near that is unmistakable at this count. The allowance is
        // a fixed slack plus a deliberately generous per-bake margin — enough that ordinary churn passes,
        // far too little for a dispatcher that keeps every render it ever made.
        long allowed = 60 + bakes / 4;
        bool ok = growth <= allowed;
        Console.WriteLine($"[selftest-bakeleak] {(ok ? "ok  " : "FAIL")}  the pool holds a plateau (growth {growth} MB, allowed {allowed} MB)");
        Console.WriteLine(ok ? "[selftest-bakeleak] PASS" : "[selftest-bakeleak] FAILED");
        return ok ? 0 : 1;
    }
}
