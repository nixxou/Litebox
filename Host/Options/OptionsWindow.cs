// Sectioned settings window shell (LB "Edit Emulator"-style): section list on the
// left, one panel per section on the right, OK / Apply / Cancel footer. Reused by
// the global options today and by the emulator / game editors later — a section
// can be auto-generated from OptionItems (checkbox / textbox / combo stack) or be
// ANY custom Control with its own apply callback.
//
// Apply semantics: every section's apply runs, then ApplyFinished (e.g. save the
// INI / flush the op-log). OK = Apply + close. Cancel = close, nothing written
// (controls hold the edits until Apply).
//
// Theme, DPI scaling, the footer, and the OptionItem row layout all come from
// Host.UiKit - this window used to carry its own copy of all four, which is how the
// original DPI overlap bug (and its footer-clipping/horizontal-scroll follow-ups)
// ended up fixed piecemeal here instead of fixed once for every LiteBox dialog.

#nullable enable

using LbApiHost.Host.UiKit;

namespace LbApiHost.Host.Options;

internal sealed class OptionsWindow : LiteBoxForm
{
    private readonly ListBox _nav;
    private readonly Panel _host;
    private readonly List<(string title, Control panel, Action? apply)> _sections = new();

    /// <summary>Runs once after every section applied (e.g. cfg.Save()).</summary>
    public Action? ApplyFinished;

    public OptionsWindow(string title)
    {
        Text = title;
        Size = new Size(S(860), S(560));
        MinimumSize = new Size(S(700), S(420));
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false; MaximizeBox = false;

        _nav = new ListBox
        {
            Dock = DockStyle.Left, Width = S(190),
            BackColor = LiteBoxTheme.PanelC, ForeColor = LiteBoxTheme.Fg, BorderStyle = BorderStyle.None,
            ItemHeight = S(30), DrawMode = DrawMode.OwnerDrawFixed,
            Font = new Font("Segoe UI", 10f),
        };
        _nav.DrawItem += (_, e) =>
        {
            if (e.Index < 0) return;
            bool sel = (e.State & DrawItemState.Selected) != 0;
            using var bg = new SolidBrush(sel ? LiteBoxTheme.Accent : LiteBoxTheme.PanelC);
            e.Graphics.FillRectangle(bg, e.Bounds);
            using var br = new SolidBrush(sel ? Color.White : LiteBoxTheme.Fg);
            e.Graphics.DrawString(_sections[e.Index].title, _nav.Font, br,
                e.Bounds.X + 12, e.Bounds.Y + (e.Bounds.Height - _nav.Font.Height) / 2f);
        };
        _nav.SelectedIndexChanged += (_, _) => ShowSection(_nav.SelectedIndex);

        _host = new Panel { Dock = DockStyle.Fill, BackColor = LiteBoxTheme.Bg, Padding = new Padding(S(18), S(14), S(18), S(8)), AutoScroll = true };

        var footer = new FooterBar();
        var cancel = footer.AddButton("Cancel", LiteBoxTheme.CancelBtn, (_, _) => { DialogResult = DialogResult.Cancel; Close(); });
        var apply = footer.AddButton("Apply", LiteBoxTheme.Accent, (_, _) => ApplyAll());
        footer.AddButton("OK", LiteBoxTheme.Ok, (_, _) => { ApplyAll(); DialogResult = DialogResult.OK; Close(); });

        Controls.Add(_host);
        Controls.Add(_nav);
        Controls.Add(footer);
        _host.BringToFront();

        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); } };
    }

    // ── Sections ─────────────────────────────────────────────────────

    /// <summary>Adds a CUSTOM section panel with its own apply callback.</summary>
    public void AddSection(string title, Control panel, Action? apply = null)
    {
        panel.Dock = DockStyle.Fill;
        _sections.Add((title, panel, apply));
        _nav.Items.Add(title);
        if (_sections.Count == 1) _nav.SelectedIndex = 0;
    }

    /// <summary>Adds a section auto-generated from OptionItems (checkbox / textbox /
    /// combo stack; the apply callback writes every control back through its item).
    /// <paramref name="disabled"/> greys the whole panel (read-only mode).</summary>
    public void AddSection(string title, IEnumerable<OptionItem> items, bool disabled = false)
    {
        var (panel, apply) = OptionRows.Build(items, S);
        Action? applyOrNull = disabled ? null : apply;
        if (disabled) panel.Enabled = false;
        AddSection(title, panel, applyOrNull);
    }

    private void ShowSection(int i)
    {
        if (i < 0 || i >= _sections.Count) return;
        _host.SuspendLayout();
        _host.Controls.Clear();
        _host.Controls.Add(_sections[i].panel);
        _host.ResumeLayout();
    }

    private void ApplyAll()
    {
        foreach (var (_, _, apply) in _sections) { try { apply?.Invoke(); } catch { } }
        try { ApplyFinished?.Invoke(); } catch { }
    }
}
