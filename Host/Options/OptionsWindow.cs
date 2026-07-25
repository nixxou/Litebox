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
        Size = new Size(S(1160), S(800));
        MinimumSize = new Size(S(860), S(560));
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

    /// <summary>The preferred size (1160×800) can exceed a small screen, so clamp it to the target screen's
    /// WORKING AREA (which excludes the taskbar) and nudge the window fully inside it — otherwise the bottom
    /// (the footer buttons) hides behind the taskbar. Runs after CenterParent has placed us. The content host
    /// is AutoScroll, so a clamp just makes it scroll.</summary>
    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        try
        {
            var wa = Screen.FromControl(Owner ?? (Control)this).WorkingArea;
            int w = Math.Min(Width, wa.Width);
            int h = Math.Min(Height, wa.Height);
            if (w != Width || h != Height) Size = new Size(w, h);   // MinimumSize still floors it
            int x = Math.Max(wa.Left, Math.Min(Left, wa.Right - Width));
            int y = Math.Max(wa.Top,  Math.Min(Top,  wa.Bottom - Height));
            Location = new Point(x, y);
        }
        catch { }
    }

    /// <summary>Dock a persistent panel to the RIGHT of the window, always visible across sections (e.g. the
    /// Edit Platform / Edit Game images panel). Added before the content host is re-fronted so the host keeps
    /// filling the middle; the footer (docked bottom earlier) stays under it.</summary>
    public void SetSidePanel(Control panel, int width)
    {
        int S(int px) => (int)Math.Round(px * LiteBoxTheme.DpiScale(this));
        panel.Dock = DockStyle.Right;
        panel.Width = S(width);
        Controls.Add(panel);
        _host.BringToFront();   // Fill host docks last → fills the space between _nav (left) and the side panel
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

    /// <summary>Adds ONE nav section whose content is split across internal TABS (a flat tab strip over
    /// swappable OptionItem stacks) — e.g. Display → General / Middle / Right panel. A tab's body may be an
    /// OptionItem list OR a custom Control (with its own apply). The section's apply runs every tab's apply.</summary>
    public void AddTabbedSection(string title, IEnumerable<(string tab, object body, Action? apply)> tabs)
    {
        var container = new Panel { Dock = DockStyle.Fill, BackColor = LiteBoxTheme.Bg };

        var strip = new FlowLayoutPanel { Dock = DockStyle.Top, Height = S(38), BackColor = LiteBoxTheme.PanelC, Padding = new Padding(S(4), S(5), 0, 0), WrapContents = false };
        var content = new Panel { Dock = DockStyle.Fill, BackColor = LiteBoxTheme.Bg };
        container.Controls.Add(content);
        container.Controls.Add(strip);

        var applies = new List<Action>();
        var buttons = new List<Button>();
        var hosts = new List<Panel>();

        foreach (var (tab, body, apply) in tabs)
        {
            Control panel;
            if (body is IEnumerable<OptionItem> items)
            {
                var (p, ap) = OptionRows.Build(items, S);
                panel = p; if (ap != null) applies.Add(ap);
            }
            else { panel = (Control)body; if (apply != null) applies.Add(apply); }

            var host = new Panel { Dock = DockStyle.Fill, BackColor = LiteBoxTheme.Bg, Visible = hosts.Count == 0 };
            panel.Dock = DockStyle.Fill;
            host.Controls.Add(panel);
            content.Controls.Add(host);
            hosts.Add(host);

            int ix = hosts.Count - 1;
            var b = new Button
            {
                Text = tab, AutoSize = false, Size = new Size(S(120), S(26)), Margin = new Padding(0, 0, S(3), 0),
                FlatStyle = FlatStyle.Flat, ForeColor = ix == 0 ? Color.White : LiteBoxTheme.SubFg,
                BackColor = ix == 0 ? LiteBoxTheme.Accent : LiteBoxTheme.PanelC, FlatAppearance = { BorderSize = 0 },
                Font = new Font("Segoe UI", 9f),
            };
            buttons.Add(b);
            b.Click += (_, _) =>
            {
                for (int i = 0; i < hosts.Count; i++)
                {
                    hosts[i].Visible = i == ix;
                    buttons[i].ForeColor = i == ix ? Color.White : LiteBoxTheme.SubFg;
                    buttons[i].BackColor = i == ix ? LiteBoxTheme.Accent : LiteBoxTheme.PanelC;
                }
            };
            strip.Controls.Add(b);
        }

        content.Controls.SetChildIndex(hosts[0], 0);   // first host at back so docking fills correctly
        AddSection(title, container, () => { foreach (var a in applies) a(); });
    }

    private void ShowSection(int i)
    {
        if (i < 0 || i >= _sections.Count) return;
        _host.SuspendLayout();
        _host.Controls.Clear();
        _host.Controls.Add(_sections[i].panel);
        _host.ResumeLayout();
    }

    /// <summary>Selects a section by (fuzzy) name — exact normalized match first, then contains.
    /// "gameplay" → "LB · Gameplay". Case, spaces and punctuation are ignored. False when no match.</summary>
    public bool SelectSection(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        string norm = Norm(name);
        for (int pass = 0; pass < 2; pass++)
            for (int i = 0; i < _sections.Count; i++)
            {
                string t = Norm(_sections[i].title);
                if (pass == 0 ? t == norm : t.Contains(norm, StringComparison.Ordinal))
                { _nav.SelectedIndex = i; return true; }
            }
        return false;

        static string Norm(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
    }

    private void ApplyAll()
    {
        foreach (var (_, _, apply) in _sections) { try { apply?.Invoke(); } catch { } }
        try { ApplyFinished?.Invoke(); } catch { }
    }
}
