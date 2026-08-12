// Show the LIVE parental-control status where LaunchBox prints "Licensed to …" / "Free Version" (top-right).
//
// No obfuscated property to patch: we walk the main window's visual tree, find the TextBlock carrying the
// licence string, cut its binding (so LaunchBox can't overwrite our text) and set the status. Cached once found;
// re-applied on a short startup timer, on every menu open, and on each lock/unlock — so it always reflects state.
// Purely cosmetic and opt-out (SoftStatusCorner); only touches the UI while parental is actually configured.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace LiteBoxParental
{
    internal static class Branding
    {
        private static bool _started;
        private static WeakReference<TextBlock> _cached;

        /// <summary>Kick off the startup re-apply timer (call once WPF is up, from the UI thread ideally).</summary>
        public static void Start()
        {
            if (_started) return;
            _started = true;
            try
            {
                var app = Application.Current;
                if (app == null) return;
                app.Dispatcher.BeginInvoke(new Action(() =>
                {
                    var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
                    int ticks = 0;
                    timer.Tick += (s, e) => { ticks++; Reapply(); if (ticks >= 30) timer.Stop(); };   // ~30 s of startup attempts
                    timer.Start();
                }));
            }
            catch { }
        }

        /// <summary>Re-apply the status text now. Cheap once the element is cached. Safe to call from the UI thread;
        /// marshals if not. Called from menu opens and lock/unlock so the corner tracks the state live.</summary>
        public static void Reapply()
        {
            try
            {
                var app = Application.Current;
                if (app == null) return;
                if (app.CheckAccess()) ApplyCore();
                else app.Dispatcher.BeginInvoke(new Action(ApplyCore));
            }
            catch { }
        }

        private static void ApplyCore()
        {
            try
            {
                if (!TestConfig.SoftStatusCorner) return;
                var text = StatusText();
                if (text == null) return;   // parental not configured → leave the licence text alone

                TextBlock tb = null;
                if (_cached != null && _cached.TryGetTarget(out var c) && c != null && PresentationSource.FromVisual(c) != null)
                    tb = c;
                if (tb == null)
                {
                    tb = FindLicenceTextBlock();
                    if (tb != null) _cached = new WeakReference<TextBlock>(tb);
                }
                if (tb == null) return;

                WireClick(tb);   // clicking the status toggles lock/unlock instead of opening License Registration

                if (tb.Text != text)
                {
                    try { BindingOperations.ClearBinding(tb, TextBlock.TextProperty); } catch { }
                    tb.Text = text;
                }
            }
            catch { }
        }

        private static string StatusText()
        {
            if (!LockState.ScopeActive) return null;
            return LockState.Locked ? "\U0001F512 Parental control: LOCKED"
                                    : "\U0001F513 Parental control: unlocked";
        }

        // The licence corner normally opens LaunchBox's "License Registration" window on click. Since we've turned
        // it into the parental-status line, intercept the click and toggle lock/unlock instead — swallowing the
        // event (PreviewMouseLeftButtonDown, tunnelling) BEFORE the licence command fires, so that window never
        // opens from here. Wired once per TextBlock (a theme rebuild makes a fresh element → re-wired). When
        // parental isn't scoped to this host, we DON'T handle the click, so LaunchBox's licence dialog still works.
        private static readonly ConditionalWeakTable<TextBlock, object> _wired = new ConditionalWeakTable<TextBlock, object>();

        private static void WireClick(TextBlock tb)
        {
            if (_wired.TryGetValue(tb, out _)) return;
            _wired.Add(tb, s_marker);
            try
            {
                tb.Background = Brushes.Transparent;      // make the whole element hit-testable, not just the glyphs
                tb.Cursor = Cursors.Hand;
                tb.PreviewMouseLeftButtonDown += OnStatusClick;
            }
            catch { }
        }
        private static readonly object s_marker = new object();

        private static void OnStatusClick(object sender, MouseButtonEventArgs e)
        {
            if (!LockState.ScopeActive) return;   // not our line here — let LaunchBox open its licence dialog
            e.Handled = true;                      // preempt the licence-registration command
            try { UnlockMenuItem.Toggle(); } catch (Exception ex) { Log.Line("[Branding] toggle: " + ex.Message); }
            Reapply();                             // reflect the new lock state in the corner immediately
        }

        // Language-INDEPENDENT: the licence label's Text is bound to the "LicenseText" view-model property (found
        // via the discovery dump). Match on that binding path, not on the displayed string, so it works in any
        // LaunchBox language. Once we set our own text the binding is gone — but the WeakReference cache holds the
        // live element; a fresh one (theme/rebuild) is re-created WITH the binding, so it's findable again.
        private const string LicensePath = "LicenseText";
        private static TextBlock FindLicenceTextBlock()
        {
            var win = Application.Current?.MainWindow;
            if (win == null) return null;
            foreach (var tb in Walk(win))
            {
                try
                {
                    var path = tb.GetBindingExpression(TextBlock.TextProperty)?.ParentBinding?.Path?.Path;
                    if (string.Equals(path, LicensePath, StringComparison.Ordinal)) return tb;
                }
                catch { }
            }
            return null;
        }

        private static IEnumerable<TextBlock> Walk(DependencyObject root)
        {
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is TextBlock tb) yield return tb;
                foreach (var d in Walk(child)) yield return d;
            }
        }
    }
}
