// The "HIGH SCORES" detail tab content — MAME community leaderboards for the selected game, mirroring the
// RelatedGamesPanel lifecycle (ShowFor / EnsureLoaded / ClearAll) so MainWindow drives it identically to the
// RELATED GAMES tab. Lazy: nothing is fetched until the tab is the visible one (or becomes visible on flip),
// which is why the leaderboards cost no network on plain game browsing.
//
// Four sub-tabs (ALL-TIME / YEARLY / MONTHLY / WEEKLY) over a single dark ListView. Data comes from the
// native READ client (MameLeaderboards) — public LBGDB boards, no auth, no obfuscated core.

#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows.Forms;
using LbApiHost.Host.Similar;   // DetailTabStrip
using LbApiHost.Host.UiKit;     // LiteBoxTheme
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Mame;

internal sealed class HighScoresPanel : Panel
{
    private readonly DetailTabStrip _subTabs;
    private readonly ListView _list;
    private readonly Label _status;

    private IGame? _game;
    private string? _rom;
    private string? _loadedRom;
    private MameLbBoards? _boards;
    private bool _running;
    private int _token;

    public HighScoresPanel()
    {
        DoubleBuffered = true;
        _subTabs = new DetailTabStrip(primary: false) { Dock = DockStyle.Top, Height = 24, BackColor = LiteBoxTheme.PanelC };
        _subTabs.SetTabs("ALL-TIME", "YEARLY", "MONTHLY", "WEEKLY");
        _subTabs.SelectedChanged = _ => Render();

        _list = new ListView
        {
            Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable,
            BackColor = LiteBoxTheme.PanelC, ForeColor = LiteBoxTheme.Fg, BorderStyle = BorderStyle.None, GridLines = false,
            MultiSelect = false, OwnerDraw = false,
        };
        _list.Columns.Add("#", 34, HorizontalAlignment.Right);
        _list.Columns.Add("Player", 140, HorizontalAlignment.Left);
        _list.Columns.Add("Score", 84, HorizontalAlignment.Right);
        _list.Columns.Add("Date", 80, HorizontalAlignment.Left);
        _list.Resize += (_, _) => FitColumns();

        _status = new Label
        {
            Dock = DockStyle.Top, Height = 46, TextAlign = ContentAlignment.MiddleLeft, Visible = false,
            ForeColor = LiteBoxTheme.SubFg, BackColor = LiteBoxTheme.PanelC, Font = new Font("Segoe UI", 9f),
            Padding = new Padding(2, 0, 0, 0),
        };

        Controls.Add(_list);
        Controls.Add(_status);
        Controls.Add(_subTabs);
    }

    public Control ScrollHost => _list;

    // ── Lifecycle (UI thread), same contract as RelatedGamesPanel ───────

    /// <summary>New subject. Drops stale results; fetches now only when the tab is visible (<paramref name="active"/>).</summary>
    public void ShowFor(IGame? g, bool active)
    {
        _game = g;
        _rom = MameLeaderboards.RomName(g);
        if (!string.Equals(_rom, _loadedRom, StringComparison.OrdinalIgnoreCase))
        {
            _boards = null; _loadedRom = null; _running = false;
            _token++;
            _list.Items.Clear();
        }
        if (active) EnsureLoaded();
    }

    /// <summary>Drop this panel's cached board for <paramref name="rom"/> (if it's the one shown) so it re-fetches
    /// the updated standing after we submitted a score. Re-fetches now when the tab is visible.</summary>
    public void InvalidateRom(string? rom, bool reloadIfVisible)
    {
        if (string.IsNullOrEmpty(rom)) return;
        bool mine = string.Equals(rom, _rom, StringComparison.OrdinalIgnoreCase)
                 || string.Equals(rom, _loadedRom, StringComparison.OrdinalIgnoreCase);
        if (!mine) return;
        _boards = null; _loadedRom = null; _running = false; _token++;
        _list.Items.Clear();
        if (reloadIfVisible) EnsureLoaded();
    }

    public void ClearAll()
    {
        _game = null; _rom = null; _boards = null; _loadedRom = null; _running = false;
        _token++;
        _list.Items.Clear();
        SetStatus(null);
    }

    /// <summary>Fetch the boards for the current rom if not already in. Called when the tab becomes visible.</summary>
    public void EnsureLoaded()
    {
        if (string.IsNullOrEmpty(_rom))
        {
            _list.Items.Clear();
            SetStatus("No MAME rom for this game.");
            return;
        }
        if (_boards != null && string.Equals(_rom, _loadedRom, StringComparison.OrdinalIgnoreCase)) { Render(); return; }
        if (_running) return;

        _running = true;
        int token = ++_token;
        string rom = _rom!;
        SetStatus("Loading leaderboards…");

        Task.Run(async () =>
        {
            MameLbBoards? boards = null;
            try { boards = await MameLeaderboards.FetchAsync(rom).ConfigureAwait(false); }
            catch { }
            try
            {
                if (IsDisposed) return;
                BeginInvoke((Action)(() =>
                {
                    if (token != _token) return;   // a newer game/rom superseded this run
                    _running = false;
                    _boards = boards;
                    _loadedRom = rom;
                    Render();
                }));
            }
            catch { }
        });
    }

    // ── Render ──────────────────────────────────────────────────────────

    private List<MameLbEntry> Current()
    {
        var b = _boards;
        if (b == null) return new List<MameLbEntry>();
        return _subTabs.Selected switch
        {
            1 => b.Yearly,
            2 => b.Monthly,
            3 => b.Weekly,
            _ => b.AllTime,
        };
    }

    private void Render()
    {
        if (_boards == null) return;
        var list = Current();
        _list.BeginUpdate();
        try
        {
            _list.Items.Clear();
            foreach (var e in list)
            {
                var (rank, player) = SplitName(e.Name);
                var it = new ListViewItem(rank);
                it.SubItems.Add(player);
                it.SubItems.Add(e.Score.ToString("N0", CultureInfo.InvariantCulture));
                it.SubItems.Add(ShortDate(e.Date));
                _list.Items.Add(it);
            }
        }
        finally { _list.EndUpdate(); }

        FitColumns();
        SetStatus(list.Count == 0 ? "No scores in this period yet." : null);
    }

    // Size columns to the panel width so Score/Date stay visible (no horizontal scroll): # / Score / Date are
    // fixed, Player absorbs the rest.
    private void FitColumns()
    {
        if (_list.Columns.Count < 4) return;
        int w = _list.ClientSize.Width;
        const int rank = 34, score = 84, date = 80;
        _list.Columns[0].Width = rank;
        _list.Columns[2].Width = score;
        _list.Columns[3].Width = date;
        _list.Columns[1].Width = Math.Max(80, w - rank - score - date - 2);
    }

    private void SetStatus(string? msg)
    {
        if (string.IsNullOrEmpty(msg)) { _status.Visible = false; return; }
        _status.Text = msg; _status.Visible = true;
    }

    // "19.  Malkav - ZZY" → ("19", "Malkav - ZZY"); falls back to the whole string as the player.
    private static (string rank, string player) SplitName(string name)
    {
        name ??= "";
        int i = name.IndexOf(".  ", StringComparison.Ordinal);
        if (i > 0 && i <= 5) return (name.Substring(0, i), name.Substring(i + 3).Trim());
        return ("", name.Trim());
    }

    private static string ShortDate(string iso)
    {
        if (DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d))
            return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return iso?.Length >= 10 ? iso.Substring(0, 10) : (iso ?? "");
    }
}
