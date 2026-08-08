// --selftest-selection [items]: the poster's select-all and its selection read, headless.
//
// A FLOOR for the poster's selection, and a caution about what this cannot see.
//
// The original claim was that reading the selection was quadratic: a virtual-mode ListView's
// SelectedIndices INDEXER keeps no cursor, so element i replays i LVM_GETNEXTITEM messages and reading n
// costs n²/2. Measured here, the indexer part is true (~470 ms for 3000) — but the code that was blamed
// iterated with foreach, and the ENUMERATOR is linear (5000 in about a millisecond). Count and Clear cost
// nothing either. On the strength of that this test was first written to say the freeze could not have
// come from the selection at all.
//
// That conclusion was wrong: the fix demonstrably removed the freeze in the running app. What this drives
// is a BARE control — no ImageList, no owner-draw, a RetrieveVirtualItem handing back an empty item, and
// nothing downstream. The real poster composes a GDI tile per item and mirrors every selection change
// into the game list. The cost lives somewhere in that chain, which none of the numbers below reach.
//
// So: these figures are a floor, not a verdict. If select-all, the Shift+click range, or the read ever
// start scaling with the library again, this fails — and that is all it is entitled to claim. Reproducing
// the freeze itself would take a populated poster, which this deliberately is not.

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

        // The two members the replaced code touched besides the enumerator: OnPosterSelectionChanged and
        // MirrorPosterToList asked for Count three times between them, and two call sites cleared through
        // the collection. Neither was measured when the walk was blamed.
        sw.Restart();
        int c1 = 0;
        for (int i = 0; i < 3; i++) c1 = lv.SelectedIndices.Count;
        sw.Stop();
        Console.WriteLine($"[selftest-selection] SelectedIndices.Count x3: {sw.ElapsedMilliseconds} ms (= {c1})");

        sw.Restart();
        lv.SelectedIndices.Clear();
        sw.Stop();
        long collClearMs = sw.ElapsedMilliseconds;
        Console.WriteLine($"[selftest-selection] SelectedIndices.Clear(): {collClearMs} ms");
        lv.SelectAllItems();

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
