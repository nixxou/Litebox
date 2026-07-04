// Phase 2 of the LiteBox-native RA fallback: batch resolution over a platform (the RA options page).
//
//   • RaScanLite.Scan(games, full, ...) — runs RaResolveLite over a set of games, sequentially (RAHasher
//     spawns + op-log writes; sequential keeps it simple and avoids hammering both). The console catalogue
//     is fetched once per console and cached, so a platform scan downloads at most one list per console.
//       - LITE (full=false): only games that have no RetroAchievementsHash yet (fills the gaps).
//       - FULL (full=true) : recomputes every game — picks up a raid that appeared in RA after a game was
//                            first resolved, and re-derives a wrong/changed hash.
//   • RaScanProgress — the modal progress dialog the options page shows (mirrors GenerateCacheForm).
//
// The caller (MainWindow's RA options section) gathers the IGame[] on the UI thread, shows the dialog, then
// flushes the data manager.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using LbApiHost.Host.Data;
using LbApiHost.Host.UiKit;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Ra;

internal static class RaScanLite
{
    public struct Counts { public int Processed, Changed, Matched; }

    /// <summary>Resolves RA for each game (sequential). <paramref name="onProgress"/>(processed, matched) is
    /// called after each game. Honours <paramref name="ct"/>. Returns the totals.
    /// <para>FULL: re-hash + re-lookup every game. LITE: for a game that already has a hash, cheap re-link
    /// (pick up a raid added to RA later, no RAHasher); for a game with no hash, resolve it (RAHasher). So a
    /// lite scan both fills the gaps AND refreshes raids — and its lookups refresh a console catalogue that's
    /// past its 24h TTL.</para></summary>
    public static Counts Scan(IReadOnlyList<IGame> games, bool full, Action<int, int> onProgress, CancellationToken ct)
    {
        var c = new Counts();
        if (games == null) return c;
        foreach (var g in games)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var f = g as ILiteBoxFields;
                bool hasHash = f != null && !string.IsNullOrEmpty(f.GetField("RetroAchievementsHash"));
                bool changed;
                if (full) changed = RaResolveLite.Resolve(g, force: true);
                else if (hasHash) changed = RaResolveLite.RelinkRaid(g);   // cheap: pick up a newly-available raid
                else changed = RaResolveLite.Resolve(g, force: false);     // RAHasher: resolve a game with no hash
                if (changed) c.Changed++;
                if (f != null && !string.IsNullOrEmpty(f.GetField("RetroAchievementsId"))) c.Matched++;
            }
            catch { }
            c.Processed++;
            try { onProgress(c.Processed, c.Matched); } catch { }
        }
        return c;
    }
}

/// <summary>Modal progress dialog for a per-platform RA scan. Built from a pre-gathered IGame[] (the caller
/// enumerates on the UI thread). Runs the scan on a background thread, cancellable.</summary>
internal sealed class RaScanProgress : LiteBoxForm
{
    private readonly IReadOnlyList<IGame> _games;
    private readonly bool _full;
    private readonly ProgressBar _bar;
    private readonly Label _label;
    private readonly Button _close;
    private readonly CancellationTokenSource _cts = new();
    private RaScanLite.Counts _result;
    private bool _done;

    public RaScanProgress(IReadOnlyList<IGame> games, bool full, string scopeLabel)
    {
        _games = games ?? Array.Empty<IGame>();
        _full = full;

        Text = (full ? "Full scan" : "Lite scan") + " — RetroAchievements";
        ClientSize = new Size(S(460), S(170));
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false; MaximizeBox = false;

        var head = new Label
        {
            Text = $"{scopeLabel} — {_games.Count} game(s)\n{(full ? "Recomputing every hash/raid." : "Resolving games with no hash yet.")}",
            ForeColor = LiteBoxTheme.SubFg, AutoSize = false, Dock = DockStyle.Top, Height = S(44),
            Padding = new Padding(S(16), S(12), S(16), 0),
        };
        _bar = new ProgressBar { Dock = DockStyle.Top, Height = S(22), Maximum = Math.Max(1, _games.Count), Margin = new Padding(S(16)) };
        var barHost = new Panel { Dock = DockStyle.Top, Height = S(34), Padding = new Padding(S(16), S(6), S(16), S(6)) };
        barHost.Controls.Add(_bar);
        _label = new Label { Text = "Preparing…", ForeColor = LiteBoxTheme.Fg, Dock = DockStyle.Top, Height = S(26), Padding = new Padding(S(16), 0, S(16), 0) };
        _close = new Button { Text = "Cancel", Dock = DockStyle.Bottom, Height = S(34), FlatStyle = FlatStyle.Flat, ForeColor = LiteBoxTheme.Fg };
        _close.FlatAppearance.BorderColor = Color.FromArgb(70, 70, 75);
        _close.Click += (_, _) => { if (_done) Close(); else _cts.Cancel(); };

        Controls.Add(_label);
        Controls.Add(barHost);
        Controls.Add(head);
        Controls.Add(_close);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Task.Run(Run);
    }

    private void Run()
    {
        _result = RaScanLite.Scan(_games, _full, (proc, matched) => Report(proc, matched), _cts.Token);
        Finish();
    }

    private void Report(int processed, int matched)
    {
        if (IsDisposed) return;
        try
        {
            BeginInvoke((Action)(() =>
            {
                if (IsDisposed) return;
                _bar.Value = Math.Min(processed, _bar.Maximum);
                _label.Text = $"Scanning…  {processed} / {_games.Count}   ·   {matched} raid(s) found";
            }));
        }
        catch { }
    }

    private void Finish()
    {
        if (IsDisposed) return;
        try
        {
            BeginInvoke((Action)(() =>
            {
                if (IsDisposed) return;
                _done = true;
                bool cancelled = _cts.IsCancellationRequested;
                _bar.Value = Math.Min(_result.Processed, _bar.Maximum);
                _label.Text = (cancelled ? "Cancelled. " : "Done. ")
                            + $"{_result.Processed} processed · {_result.Changed} updated · {_result.Matched} raid(s).";
                _close.Text = "Close";
            }));
        }
        catch { }
    }
}
