// --selftest-selection [items]: the poster's select-all and its selection read, headless.
//
// Written to confirm a diagnosis, and it refuted it — which is the whole reason it exists.
//
// The claim was that reading the poster's selection was quadratic: a virtual-mode ListView's
// SelectedIndices INDEXER keeps no cursor, so element i replays i LVM_GETNEXTITEM messages and reading n
// costs n²/2. The indexer part is true and measurable here (~450 ms for 3000). But the code that was
// blamed used foreach, and the ENUMERATOR is linear — 5000 indices in about a millisecond. The reported
// freeze cannot have come from there, and the numbers below say so on every run.
//
// What is measured, therefore, is a floor rather than a fix: select-all, the range set by Shift+click,
// and both ways of reading the result. If a future change makes any of them scale with the library
// again, this fails. Where the reported freeze DOES come from is still open — the surviving suspect is
// the owner-drawn tiles being recomposed (which also explains the memory climbing, something no
// selection bookkeeping would do).

#nullable enable

using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace LbApiHost.Host.Diag;

internal static class SelectionSelfTest
{
    private static int _fail;

    private static void Check(bool ok, string what)
    {
        if (!ok) _fail++;
        Console.WriteLine($"[selftest-selection] {(ok ? "ok  " : "FAIL")}  {what}");
    }

    public static int Run(int items)
    {
        int rc = 0;
        var th = new Thread(() =>
        {
            try { rc = RunSta(items); }
            catch (Exception ex) { Console.WriteLine("[selftest-selection] FAILED: " + ex); rc = 1; }
        });
        th.SetApartmentState(ApartmentState.STA);
        th.Start(); th.Join();
        return rc;
    }

    private static int RunSta(int items)
    {
        Console.WriteLine($"[selftest-selection] {items} virtual items");

        using var form = new Form { ShowInTaskbar = false, WindowState = FormWindowState.Minimized };
        var lv = new MainWindow.PosterListView
        {
            View = View.LargeIcon, VirtualMode = true, MultiSelect = true, Dock = DockStyle.Fill,
        };
        // Virtual mode insists on being able to produce an item; nothing here ever paints one, but the
        // control asks anyway as soon as it has a handle.
        lv.RetrieveVirtualItem += (_, e) => e.Item = new ListViewItem("");
        form.Controls.Add(lv);
        form.CreateControl();
        _ = form.Handle; _ = lv.Handle;
        lv.VirtualListSize = items;

        // ── select-all ───────────────────────────────────────────────────────
        var sw = Stopwatch.StartNew();
        lv.SelectAllItems();
        sw.Stop();
        long selectMs = sw.ElapsedMilliseconds;
        int nativeCount = lv.SelectedIndices.Count;   // LVM_GETSELECTEDCOUNT — cheap, unlike the indexer
        Check(nativeCount == items, $"select-all selects every item ({nativeCount}/{items}, {selectMs} ms)");
        Check(selectMs < 500, $"select-all does not depend on the item count ({selectMs} ms)");

        // ── reading it back ──────────────────────────────────────────────────
        sw.Restart();
        var fast = lv.SelectedIndicesFast();
        sw.Stop();
        long fastMs = sw.ElapsedMilliseconds;
        Check(fast.Count == items, $"the walk returns every selected index ({fast.Count}/{items}, {fastMs} ms)");

        bool ordered = true;
        for (int i = 0; i < fast.Count; i++) if (fast[i] != i) { ordered = false; break; }
        Check(ordered, "the walk returns them in display order");

        // The collection this replaces, measured both ways: the enumerator (what foreach uses) and the
        // INDEXER (what a for loop uses). They are not the same cost, and assuming they were is how one
        // ends up blaming the wrong line.
        sw.Restart();
        int viaEnum = 0;
        foreach (int _ in lv.SelectedIndices) viaEnum++;
        sw.Stop();
        long enumMs = sw.ElapsedMilliseconds;

        int probe = Math.Min(items, 3000);
        sw.Restart();
        long sum = 0;
        var sel = lv.SelectedIndices;
        for (int i = 0; i < probe; i++) sum += sel[i];
        sw.Stop();
        long idxMs = sw.ElapsedMilliseconds;

        Console.WriteLine($"[selftest-selection] SelectedIndices foreach: {enumMs} ms for {viaEnum}");
        Console.WriteLine($"[selftest-selection] SelectedIndices indexer: {idxMs} ms for {probe} (sum {sum})");
        Console.WriteLine($"[selftest-selection] cursor walk:              {fastMs} ms for all {fast.Count}");
        long naiveMs = Math.Max(enumMs, idxMs);

        // Compared against the ENUMERATOR, because that is what the replaced code used (foreach). Measuring
        // against the indexer would flatter the walk with a 400 ms baseline nothing was ever paying.
        Check(fastMs <= Math.Max(50, enumMs * 4), $"the walk costs no more than the foreach it replaces ({fastMs} vs {enumMs} ms)");
        Check(idxMs > enumMs * 4 || idxMs < 50,
              $"note: the indexer is the quadratic one ({idxMs} ms/{probe} vs foreach {enumMs} ms/{viaEnum})");

        // ── the Shift+click range, which is what the user actually reported ──
        // SelectRange sets the state item by item (the native virtual-mode range is wrong), so unlike
        // select-all it IS linear in the range — the question is what that constant costs at library size.
        lv.ClearSelection();
        sw.Restart();
        lv.SelectRange(0, items - 1);
        sw.Stop();
        long rangeMs = sw.ElapsedMilliseconds;
        int rangeCount = lv.SelectedIndices.Count;
        Console.WriteLine($"[selftest-selection] SelectRange(0..{items - 1}): {rangeMs} ms, {rangeCount} selected");
        Check(rangeCount == items, $"the range selects every item ({rangeCount}/{items})");
        Check(rangeMs < 2000, $"the range does not stall ({rangeMs} ms for {items})");

        // ── clearing ─────────────────────────────────────────────────────────
        sw.Restart();
        lv.ClearSelection();
        sw.Stop();
        Check(lv.SelectedIndices.Count == 0, $"clear deselects everything ({sw.ElapsedMilliseconds} ms)");

        Console.WriteLine(_fail == 0 ? "[selftest-selection] PASS" : $"[selftest-selection] FAILED ({_fail} check(s))");
        return _fail == 0 ? 0 : 1;
    }
}
