// The game list INDEX — LaunchBox 14's "Game List Index" rebuilt for LiteBox's native list. A
// narrow strip standing where the list's scrollbar was (the owner hides it), showing one marker per
// group of the CURRENT sort: click or drag to jump straight to a group.
//
// Grouping follows the sort, not the alphabet: sorted by Title the groups are letters, sorted by
// Genre or Play Mode they are the values themselves ("Fighter / 2D", "(None)", …) — same rule as LB.
//
// A label is drawn only where there is vertical room for its text; a group squeezed out keeps its
// marker as a small tick, and stays hoverable and clickable. That is exactly why LaunchBox's own
// index can skip a thin letter (a J with two games) on a crowded list — nothing is missing, the
// letter just has no room to spell itself, and the tooltip still names it.
//
// The dark backdrop appears only while the index is being used (hover / drag), like LB's. With
// AlwaysShow off the strip keeps a slim collapsed width and stays blank until the pointer arrives,
// then widens to fit its labels while in use.

#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using LbApiHost.Host.UiKit;

namespace LbApiHost.Host;

internal sealed class GameListIndexBar : Control
{
    /// <summary>One entry per group, in display order: the text to draw, the full value for the
    /// tooltip, the group's first row, and whether this group may SPELL its text (only the first
    /// group of each first-segment family does — the rest stay dots whatever the room). Supplied by
    /// the owner so the bar never has to know how the list is sorted or filtered.</summary>
    public Func<IReadOnlyList<(string Label, string Tip, int Index, bool Spell)>>? GroupsProvider;
    /// <summary>Rows in the current view — the denominator for every marker position.</summary>
    public Func<int>? RowCount;
    /// <summary>Scroll the list so this row lands at the top.</summary>
    public Action<int>? JumpToRow;
    /// <summary>Select this row in the central view. Fired on GROUP clicks (label or dot) only —
    /// a continuous thumb drag scrolls without touching the selection.</summary>
    public Action<int>? SelectRow;
    /// <summary>The row currently at the top of the list, for the position indicator.</summary>
    public Func<int>? TopRow;
    /// <summary>Rows one viewport holds. The thumb's range is rows − page — that is what lets a
    /// drag place the view anywhere, ends included, exactly like a scrollbar.</summary>
    public Func<int>? PageRows;
    /// <summary>Park the view at a fraction (0..1) of its scroll range. Pixel-precise where the
    /// view allows (the poster grid), which is what makes a drag CONTINUOUS on a short list —
    /// row-index scrolling moved it in visible notches.</summary>
    public Action<double>? ScrollToFraction;
    /// <summary>Where the view sits in its scroll range (0..1) — the thumb follows this, so it
    /// tracks a free drag instead of snapping to whole rows.</summary>
    public Func<double>? ScrollFraction;
    /// <summary>Scroll the view by N wheel lines (negative = up). Only the owner knows what a
    /// line means in the active view (a row here, a grid row there).</summary>
    public Action<int>? ScrollLines;

    /// <summary>On: markers stay painted and the strip keeps its fitted width. Off: a slim blank
    /// strip until the pointer reaches it — LaunchBox's "Always show the Game List Index" toggle.</summary>
    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool AlwaysShow
    {
        get => _alwaysShow;
        set { if (_alwaysShow == value) return; _alwaysShow = value; FitWidth(); Invalidate(); }
    }
    private bool _alwaysShow = true;

    /// <summary>With AlwaysShow OFF: the retracted sliver still shows a dots-only MINI index (the
    /// thumb included) instead of sitting blank. Its space is the reserved sliver either way.</summary>
    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool MiniWhenCollapsed
    {
        get => _mini;
        set { if (_mini == value) return; _mini = value; Invalidate(); }
    }
    private bool _mini = true;

    /// <summary>The width the LAYOUT must reserve for the strip — constant per mode (fitted when
    /// always-shown, the slim collapsed sliver otherwise). Hover expansion is a pure OVERLAY above
    /// the content and never touches this: reflowing the grid mid-hover moved the tiles under the
    /// pointer, so a drag no longer matched where things would land once the hover ended.</summary>
    public int ReservedWidth => _alwaysShow ? _fit : CollapsedW;
    /// <summary>Raised when ReservedWidth changes (option flip, labels refit) — the owner re-pads
    /// the views then. Never raised by hover.</summary>
    public event Action? ReservedWidthChanged;

    private IReadOnlyList<(string Label, string Tip, int Index, bool Spell)> _groups
        = Array.Empty<(string, string, int, bool)>();
    private int _rows;
    private int _hover = -1;          // group under the pointer (-1 none)
    private bool _dragging;
    private bool _pointerIn;
    private int _fit = CollapsedW;    // width the current labels need
    private readonly ToolTip _tip = new() { ShowAlways = true, InitialDelay = 120, ReshowDelay = 60 };
    private string _tipText = "";

    private const int CollapsedW = 14;
    private const int MinW = 30;
    private const int MaxW = 220;
    private const int Pad = 6;

    public GameListIndexBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        // No double-click semantics: the second press of a fast pair must be an ordinary MouseDown
        // (another jump / grab), not a WM_LBUTTONDBLCLK that the bar has no handler for.
        SetStyle(ControlStyles.StandardDoubleClick, false);
        // Never take focus: clicking the strip must not pull it off the list, or the arrow keys
        // (and the wheel, which Windows delivers to the FOCUSED control) stop reaching the games.
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;   // pointer stays the plain arrow, like LB's index
        Width = MinW;
        _leaveWatch.Tick += (_, _) =>
        {
            try { if (ClientRectangle.Contains(PointToClient(Cursor.Position))) { _leaveWatch.Stop(); return; } }
            catch { }   // back over the strip itself — ordinary enter/leave tracking takes over
            if (StillNear()) return;
            _leaveWatch.Stop();
            _pointerIn = false; _hover = -1;
            Place();
            Invalidate();
        };
    }

    /// <summary>Re-read the groups from the owner and refit the width. Cheap enough to call on
    /// every view change (one pass over precomputed labels).</summary>
    public void RefreshGroups()
    {
        try
        {
            _groups = GroupsProvider?.Invoke() ?? Array.Empty<(string, string, int, bool)>();
            _rows = Math.Max(0, RowCount?.Invoke() ?? 0);
        }
        catch { _groups = Array.Empty<(string, string, int, bool)>(); _rows = 0; }
        FitWidth();
        Invalidate();
    }

    private void FitWidth()
    {
        int reservedBefore = ReservedWidth;
        int w = MinW;
        foreach (var (label, _, _, spell) in _groups)
            if (spell)
                w = Math.Max(w, TextRenderer.MeasureText(label, Font).Width + DotRail + 8);
        _fit = Math.Min(w, MaxW);
        if (ReservedWidth != reservedBefore)
            try { ReservedWidthChanged?.Invoke(); } catch { }
        Place();
    }

    /// <summary>Own layout — the strip is NOT docked (a dock reflows its siblings on every width
    /// change, hover included). It pins itself to the parent's right edge at its DISPLAY width:
    /// the reserved width normally, the fitted width while hovered or dragging — that expansion
    /// overlays the content, exactly LB's dark band.</summary>
    private void Place()
    {
        if (Parent == null) return;
        int disp = _alwaysShow || _pointerIn || _dragging ? _fit : CollapsedW;
        disp = Math.Min(disp, Math.Max(1, Parent.ClientSize.Width));
        var b = new Rectangle(Parent.ClientSize.Width - disp, 0, disp, Parent.ClientSize.Height);
        if (Bounds != b) Bounds = b;
    }

    protected override void OnParentChanged(EventArgs e)
    {
        base.OnParentChanged(e);
        if (Parent != null) { Parent.Resize += (_, _) => Place(); Place(); }
    }

    private bool MarkersVisible => _alwaysShow || _pointerIn || _dragging;
    private bool InUse => _dragging || (_pointerIn && _hover >= 0);

    // ── geometry ──────────────────────────────────────────────────────────────
    private int YOf(int rowIndex)
    {
        int h = Math.Max(1, ClientSize.Height - Pad * 2);
        double f = _rows <= 1 ? 0 : Math.Clamp(rowIndex / (double)(_rows - 1), 0, 1);
        return Pad + (int)Math.Round(f * (h - 1));
    }

    private int GroupAt(int y)
    {
        if (_groups.Count == 0) return -1;
        int best = 0, bestD = int.MaxValue;
        for (int i = 0; i < _groups.Count; i++)
        {
            int d = Math.Abs(YOf(_groups[i].Index) - y);
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    private const int MarkerTol = 9;   // a click must land ON a marker (+/- this) to count as one

    /// <summary>The group whose marker is within tolerance of <paramref name="y"/> — or -1: between
    /// two distant markers NOTHING is eligible, clicking there must not snap to the nearest one.
    /// Used for CLICKS and hover only; a drag never consults it.</summary>
    private int GroupNear(int y)
    {
        int i = GroupAt(y);
        return i >= 0 && Math.Abs(YOf(_groups[i].Index) - y) <= MarkerTol ? i : -1;
    }

    /// <summary>What a press at (<paramref name="x"/>, <paramref name="y"/>) hits, deciding by the
    /// LANE the pointer is in. Dots live in the rail along the right edge, labels in the text area
    /// left of it — two different columns — so a dot sitting a few pixels above a label no longer
    /// steals that label's click: aim at the text and the text answers, whatever is beside it.
    /// The label's whole lane counts, not just its glyphs, so clicking well left of a short label
    /// still reaches it.</summary>
    private int GroupHit(int x, int y)
    {
        int railLeft = ClientSize.Width - 4 - DotRail;   // exactly where the label rects stop (OnPaint)
        if (x >= railLeft) return GroupNear(y);          // in the rail: the dots answer, by proximity
        foreach (var (gi, top, bottom) in _bands)        // in the text lane: only what is spelled there
            if (y >= top && y <= bottom) return gi;
        return -1;                                       // bare space: a free position, not a snap
    }

    /// <summary>The label bands of the LAST paint (group index + its text rect's vertical span) —
    /// hit-testing reads the layout that was actually drawn instead of recomputing the eviction
    /// pass, so what the eye sees and what the click lands on can never disagree.</summary>
    private readonly List<(int gi, int top, int bottom)> _bands = new();

    /// <summary>Diagnostic (see MainWindow's LB_INDEX_DIAG driver): the raw numbers behind the
    /// thumb and the markers, so a "the letter and the position disagree" report can be measured
    /// instead of eyeballed off a screenshot.</summary>
    public string DiagDump()
    {
        var sb = new System.Text.StringBuilder();
        int top = -1, page = -1;
        try { top = TopRow?.Invoke() ?? -1; page = PageRows?.Invoke() ?? -1; } catch { }
        double sf = double.NaN;
        try { if (ScrollFraction != null) sf = ScrollFraction(); } catch { }
        sb.AppendLine($"[index] rows={_rows} top={top} page={page} scrollFrac={sf:0.0000} thumbY={ThumbY()} barH={ClientSize.Height} pad={Pad}");
        foreach (var (label, tip, idx, spell) in _groups)
            sb.AppendLine($"[index]   '{label}' idx={idx} y={YOf(idx)} frac={(_rows <= 1 ? 0 : idx / (double)(_rows - 1)):0.0000} spell={spell}");
        return sb.ToString();
    }

    private const int DotRail = 9;   // the dots' lane along the right edge; text ends before it

    /// <summary>The thumb's centre, on the SCROLL scale: top / (rows − page), so it reaches both
    /// ends of the strip. Group markers live on the row scale — the two agree at the top and drift
    /// a little toward the bottom, the same tension every scrollbar-with-marks has.</summary>
    private int ThumbY()
    {
        int top = -1, page = 1;
        try { top = TopRow?.Invoke() ?? -1; page = Math.Max(1, PageRows?.Invoke() ?? 1); } catch { }
        if (top < 0) return -1;
        int maxTop = Math.Max(0, _rows - page);
        if (maxTop == 0) return -1;   // everything fits — no scroll position to show
        int h = Math.Max(1, ClientSize.Height - Pad * 2);
        // The view's OWN fraction when it can report one: a pixel-scrolled grid sits between rows,
        // and a thumb computed from the top row would tick from row to row under a smooth drag.
        double f = double.NaN;
        if (ScrollFraction != null) try { f = ScrollFraction(); } catch { f = double.NaN; }
        if (double.IsNaN(f)) f = top / (double)maxTop;
        return Pad + (int)Math.Round(Math.Clamp(f, 0, 1) * (h - 1));
    }

    /// <summary>Where a press or a drag at <paramref name="y"/> takes the view: exactly where the
    /// pointer is. NO magnet here — a drag follows the hand and nothing else. Snapping belongs to
    /// a CLICK on a marker (see OnMouseDown), where the intent is unambiguous.</summary>
    private void DragTo(int y)
    {
        if (_rows <= 0) return;
        int h = Math.Max(1, ClientSize.Height - Pad * 2);
        double f = Math.Clamp((y - Pad) / (double)(h - 1), 0, 1);
        // Fractional scrolling keeps a short list CONTINUOUS: mapping to a top row instead meant a
        // 11-row grid had eight stops for a 600px strip, and the drag moved in visible jumps.
        if (ScrollToFraction != null) { try { ScrollToFraction(f); } catch { } Invalidate(); return; }
        int page = 1;
        try { page = Math.Max(1, PageRows?.Invoke() ?? 1); } catch { }
        try { JumpToRow?.Invoke((int)Math.Round(f * Math.Max(0, _rows - page))); } catch { }
        Invalidate();
    }

    // ── paint ─────────────────────────────────────────────────────────────────
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        using (var bg = new SolidBrush(BackColor)) g.FillRectangle(bg, ClientRectangle);

        // The dark backdrop only while the index is in use — LB's rule, so the strip never sits as
        // a permanent dark band beside the list.
        if (InUse)
            using (var bg = new SolidBrush(LiteBoxTheme.PanelC))
                g.FillRectangle(bg, ClientRectangle);

        if (_groups.Count == 0 || _rows == 0) return;
        if (!MarkersVisible)
        {
            // Retracted mini index: ONLY the accent position line — a slim scrollbar silhouette.
            // (It first drew the dot rail too; the dots crowding the sliver read as clutter.)
            if (!_mini) return;
            int mty = ThumbY();
            if (mty >= 0)
                using (var pos = new SolidBrush(Color.FromArgb(210, LiteBoxTheme.Accent)))
                    g.FillRectangle(pos, 2, mty - 1, ClientSize.Width - 4, 3);
            return;
        }

        g.SmoothingMode = SmoothingMode.AntiAlias;

        // Current position: a thin accent line, LB's look — the drag handle for free scrolling.
        int ty = ThumbY();
        if (ty >= 0)
        {
            using var pos = new SolidBrush(_dragging ? LiteBoxTheme.Accent
                                                     : Color.FromArgb(210, LiteBoxTheme.Accent));
            g.FillRectangle(pos, 2, ty - 1, ClientSize.Width - 4, 3);
        }

        using var tick = new SolidBrush(Color.FromArgb(150, LiteBoxTheme.SubFg));
        using var tickHot = new SolidBrush(LiteBoxTheme.Fg);

        // Dots form a rail along the RIGHT edge; each label ends a small gap BEFORE the rail so
        // text never crushes into the dots — LB's alignment.
        int lineH = Font.Height;
        int rx = ClientSize.Width - 4;

        // Pass 1 — which spell-eligible labels actually get room. NOT first-come-first-served: a
        // label that lacks room but whose family holds ≥50% more games than the one occupying the
        // slice EVICTS it (cascading upward while it keeps out-weighing), so a three-game family
        // can't silence the three-hundred-game one right under it.
        var placed = new List<(int gi, int top, int bottom, int weight)>();
        for (int i = 0; i < _groups.Count; i++)
        {
            if (!_groups[i].Spell) continue;
            int next = _rows;
            for (int j = i + 1; j < _groups.Count; j++)
                if (_groups[j].Spell) { next = _groups[j].Index; break; }
            int weight = Math.Max(1, next - _groups[i].Index);
            int y = YOf(_groups[i].Index);
            int top = y - lineH / 2, bottom = y + lineH / 2;
            if (top < 0 || bottom > ClientSize.Height) continue;
            while (placed.Count > 0 && top <= placed[^1].bottom
                   && (long)weight * 2 > (long)placed[^1].weight * 3)
                placed.RemoveAt(placed.Count - 1);
            if (placed.Count == 0 || top > placed[^1].bottom)
                placed.Add((i, top, bottom, weight));
        }
        var spelled = new HashSet<int>();
        _bands.Clear();
        foreach (var p in placed) { spelled.Add(p.gi); _bands.Add((p.gi, p.top, p.bottom)); }

        // Pass 2 — draw: the chosen labels as text, everything else as the dot rail. The hovered
        // group always shows its text (a transient emphasis, allowed to overlap).
        for (int i = 0; i < _groups.Count; i++)
        {
            var (label, _, row, spell) = _groups[i];
            int y = YOf(row);
            bool hot = i == _hover && _pointerIn;
            if (spelled.Contains(i) || (hot && spell))
            {
                var r = new Rectangle(0, y - lineH / 2, rx - DotRail, lineH);
                TextRenderer.DrawText(g, label, Font, r, hot ? LiteBoxTheme.Fg : LiteBoxTheme.SubFg,
                                      TextFormatFlags.Right | TextFormatFlags.VerticalCenter
                                      | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            }
            else
            {
                int d = hot ? 5 : 3;
                g.FillEllipse(hot ? tickHot : tick, rx - d, y - d / 2f, d, d);
            }
        }
    }

    // ── input ─────────────────────────────────────────────────────────────────
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _dragging = true;
        Capture = true;
        _downY = e.Y;
        _dragMoved = false;
        // A quick SECOND press is not another grab: it means "put the thumb right here" — the
        // pointer position becomes the scroll position, no grab offset, no group snap.
        if (e.Clicks >= 2)
        {
            _grabOffset = 0;
            _dragMoved = true;
            DragTo(e.Y);
            return;
        }
        // Pressing NEAR the thumb (not just dead on the 3px line) grabs it where it stands — no
        // snap, so a drag can start from a position between two markers without the list lurching
        // first.
        int ty = ThumbY();
        if (ty >= 0 && Math.Abs(e.Y - ty) <= GrabZone) { _grabOffset = e.Y - ty; Invalidate(); return; }
        _grabOffset = 0;
        // On a marker (+/- tolerance) the press is a GROUP CLICK: scroll there AND select. In the
        // empty space between two markers it is a plain position — the view goes exactly where the
        // pointer is, instead of the press being swallowed as it used to be.
        int gi = GroupHit(e.X, e.Y);
        if (gi >= 0) JumpTo(gi);
        else { _dragMoved = true; DragTo(e.Y); }   // already moved: no tremor guard on what follows
        Invalidate();
    }
    private int _grabOffset;             // pointer-to-thumb offset while dragging (0 = jumped)
    private int _downY;                  // where the press landed — the tremor guard anchor
    private bool _dragMoved;             // the press became a real drag (movement past the guard)
    private const int GrabZone = 18;     // px around the 3px thumb line that count as grabbing it —
                                         // wider than the line (nobody aims at 3px), 26 was too greedy

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int i = GroupHit(e.X, e.Y);   // hover follows the same lanes as clicks — what lights up is what answers
        if (i != _hover)
        {
            _hover = i;
            string t = i >= 0 && i < _groups.Count ? _groups[i].Tip : "";
            if (t != _tipText) { _tipText = t; if (t.Length > 0) _tip.SetToolTip(this, t); }
            Invalidate();
        }
        // Movement while pressed is a CONTINUOUS drag — past a small tremor guard, so a click on
        // a label does not turn into a drag because the hand shivered a pixel or two.
        if (_dragging)
        {
            if (!_dragMoved && Math.Abs(e.Y - _downY) <= 4) return;
            _dragMoved = true;
            DragTo(e.Y - _grabOffset);
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_dragging) return;
        _dragging = false;
        Capture = false;
        // A drag routinely ends with the pointer off the strip: hand it to the grace band rather
        // than collapsing under the hand that was just using it.
        if (StillNear()) _leaveWatch.Start(); else _pointerIn = false;
        Place();
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _leaveWatch.Stop();   // back on the strip before the grace band ran out
        _pointerIn = true;
        RefreshGroups();   // groups may be stale (scroll/sort since last hover) — one cheap pass
        Place();           // expand OVER the content; the layout underneath must not move
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1;
        // Leaving by a few pixels must not slam the strip shut: an expanded index is a target the
        // hand is still aiming at, and the labels sit at its LEFT edge — the side you drift over
        // on the way in. Keep it open while the pointer stays in the grace band and poll for the
        // real exit (there is no MouseLeave to wait for out there — we are already outside).
        if (!_dragging && StillNear()) { _leaveWatch.Start(); Invalidate(); return; }
        if (!_dragging) { _pointerIn = false; Place(); }
        Invalidate();
    }

    private const int LeaveSlack = 40;   // px LEFT of the strip that still count as being on it
    private readonly System.Windows.Forms.Timer _leaveWatch = new() { Interval = 60 };

    /// <summary>Is the pointer still on the strip or in its grace band? A drag always counts —
    /// the pointer routinely runs outside the strip while the button is down.</summary>
    private bool StillNear()
    {
        if (_dragging) return true;
        if (!IsHandleCreated) return false;
        try
        {
            var p = PointToClient(Cursor.Position);
            return p.X >= -LeaveSlack && p.X <= ClientSize.Width
                && p.Y >= -4 && p.Y <= ClientSize.Height + 4;
        }
        catch { return false; }
    }

    /// <summary>The wheel belongs to the list beside the strip — the index is a jump control, not a
    /// scrollbar, and swallowing the wheel would make it a dead zone at the list's edge.
    ///
    /// Scrolls the view through the owner's own plumbing rather than re-posting WM_MOUSEWHEEL to
    /// it: both views run with their scrollbar style stripped, and a list view in that state is
    /// free to ignore a wheel message — which is exactly what made the strip a dead zone.</summary>
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        int notches = e.Delta / 120;
        if (notches == 0 || ScrollLines == null) { base.OnMouseWheel(e); return; }
        int lines = SystemInformation.MouseWheelScrollLines;
        if (lines <= 0) lines = 3;   // "one screen at a time" (-1) — treat as a sane default
        try { ScrollLines(-notches * lines); } catch { }
        Invalidate();   // the position indicator moved
    }

    private void JumpTo(int i)
    {
        if (i < 0 || i >= _groups.Count) return;
        // Scroll first: the group head goes to the TOP row (list top / poster top grid row); when
        // the tail is shorter than a page the native clamp leaves it lower — bottom at worst,
        // exactly the wanted exception. THEN select, so the detail pane follows and the
        // selection code sees an already-visible item (its EnsureVisible is a no-op).
        try { JumpToRow?.Invoke(_groups[i].Index); } catch { }
        try { SelectRow?.Invoke(_groups[i].Index); } catch { }
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _tip.Dispose(); _leaveWatch.Dispose(); }
        base.Dispose(disposing);
    }

}
