// Sectioned settings window shell (LB "Edit Emulator"-style): section list on the
// left, one panel per section on the right, OK / Apply / Cancel footer. Reused by
// the global options today and by the emulator / game editors later — a section
// can be auto-generated from OptionItems (checkbox / textbox / combo stack) or be
// ANY custom Control with its own apply callback.
//
// Apply semantics: every section's apply runs, then ApplyFinished (e.g. save the
// INI / flush the op-log). OK = Apply + close. Cancel = close, nothing written
// (controls hold the edits until Apply).

#nullable enable

namespace LbApiHost.Host.Options;

internal sealed class OptionsWindow : Form
{
    private static readonly Color Bg = Color.FromArgb(30, 30, 30);
    private static readonly Color PanelC = Color.FromArgb(37, 37, 38);
    private static readonly Color Panel2 = Color.FromArgb(45, 45, 48);
    private static readonly Color Fg = Color.FromArgb(222, 222, 222);
    private static readonly Color SubFg = Color.FromArgb(150, 150, 152);
    private static readonly Color Accent = Color.FromArgb(0, 122, 204);

    private readonly ListBox _nav;
    private readonly Panel _host;
    private readonly List<(string title, Control panel, Action? apply)> _sections = new();

    /// <summary>Runs once after every section applied (e.g. cfg.Save()).</summary>
    public Action? ApplyFinished;

    public OptionsWindow(string title)
    {
        Text = title;
        Size = new Size(860, 560);
        MinimumSize = new Size(700, 420);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Bg; ForeColor = Fg;
        Font = new Font("Segoe UI", 9.5f);
        ShowIcon = false; ShowInTaskbar = false;
        MinimizeBox = false; MaximizeBox = false;
        KeyPreview = true;

        _nav = new ListBox
        {
            Dock = DockStyle.Left, Width = 190,
            BackColor = PanelC, ForeColor = Fg, BorderStyle = BorderStyle.None,
            ItemHeight = 30, DrawMode = DrawMode.OwnerDrawFixed,
            Font = new Font("Segoe UI", 10f),
        };
        _nav.DrawItem += (_, e) =>
        {
            if (e.Index < 0) return;
            bool sel = (e.State & DrawItemState.Selected) != 0;
            using var bg = new SolidBrush(sel ? Accent : PanelC);
            e.Graphics.FillRectangle(bg, e.Bounds);
            using var br = new SolidBrush(sel ? Color.White : Fg);
            e.Graphics.DrawString(_sections[e.Index].title, _nav.Font, br,
                e.Bounds.X + 12, e.Bounds.Y + (e.Bounds.Height - _nav.Font.Height) / 2f);
        };
        _nav.SelectedIndexChanged += (_, _) => ShowSection(_nav.SelectedIndex);

        _host = new Panel { Dock = DockStyle.Fill, BackColor = Bg, Padding = new Padding(18, 14, 18, 8), AutoScroll = true };

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 46, BackColor = PanelC };
        var ok = FooterBtn("OK", Color.FromArgb(50, 110, 65));
        var apply = FooterBtn("Apply", Accent);
        var cancel = FooterBtn("Cancel", Color.FromArgb(60, 60, 75));
        ok.Click += (_, _) => { ApplyAll(); DialogResult = DialogResult.OK; Close(); };
        apply.Click += (_, _) => ApplyAll();
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        void Place() { int r = ClientSize.Width - 14; cancel.Left = r - cancel.Width; apply.Left = cancel.Left - apply.Width - 8; ok.Left = apply.Left - ok.Width - 8; }
        footer.Resize += (_, _) => Place();
        footer.Controls.AddRange(new Control[] { ok, apply, cancel });

        Controls.Add(_host);
        Controls.Add(_nav);
        Controls.Add(footer);
        _host.BringToFront();

        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); } };
        Shown += (_, _) => Place();
    }

    private static Button FooterBtn(string text, Color back) => new()
    {
        Text = text, Size = new Size(92, 28), Top = 9,
        FlatStyle = FlatStyle.Flat, BackColor = back, ForeColor = Color.White,
        FlatAppearance = { BorderSize = 0 },
        Font = new Font("Segoe UI", 9f, FontStyle.Bold),
    };

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
    /// combo stack; the apply callback writes every control back through its item).</summary>
    public void AddSection(string title, IEnumerable<OptionItem> items)
    {
        var (panel, apply) = BuildAutoPanel(items);
        AddSection(title, panel, apply);
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

    // ── Auto panel from OptionItems ──────────────────────────────────

    private (Control panel, Action apply) BuildAutoPanel(IEnumerable<OptionItem> items)
    {
        var panel = new Panel { BackColor = Bg, AutoScroll = true };
        var applies = new List<Action>();
        int y = 4;

        foreach (var it in items)
        {
            switch (it.Kind)
            {
                case OptionKind.Bool:
                {
                    var cb = new CheckBox
                    {
                        Text = it.Label, Location = new Point(4, y), AutoSize = true,
                        ForeColor = Fg, BackColor = Bg,
                        Checked = string.Equals(it.Get(), "true", StringComparison.OrdinalIgnoreCase),
                    };
                    panel.Controls.Add(cb);
                    y += 26;
                    applies.Add(() => ApplyIfChanged(it, cb.Checked ? "true" : "false"));
                    break;
                }
                case OptionKind.Text:
                {
                    var lbl = new Label { Text = it.Label, Location = new Point(4, y + 3), AutoSize = true, ForeColor = Fg, BackColor = Bg };
                    var tb = new TextBox
                    {
                        Location = new Point(250, y), Width = 280,
                        BackColor = Panel2, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle,
                        Text = it.Get(),
                    };
                    panel.Controls.Add(lbl); panel.Controls.Add(tb);
                    y += 30;
                    applies.Add(() => ApplyIfChanged(it, tb.Text));
                    break;
                }
                case OptionKind.Choice:
                {
                    var lbl = new Label { Text = it.Label, Location = new Point(4, y + 3), AutoSize = true, ForeColor = Fg, BackColor = Bg };
                    var cmb = new ComboBox
                    {
                        Location = new Point(250, y), Width = 280,
                        DropDownStyle = ComboBoxStyle.DropDownList,
                        BackColor = Panel2, ForeColor = Fg, FlatStyle = FlatStyle.Flat,
                    };
                    cmb.Items.AddRange(it.Choices);
                    var cur = it.Get();
                    int ix = Array.FindIndex(it.Choices, c => string.Equals(c, cur, StringComparison.OrdinalIgnoreCase));
                    cmb.SelectedIndex = ix >= 0 ? ix : (it.Choices.Length > 0 ? 0 : -1);
                    panel.Controls.Add(lbl); panel.Controls.Add(cmb);
                    y += 30;
                    applies.Add(() => { if (cmb.SelectedItem is string s) ApplyIfChanged(it, s); });
                    break;
                }
            }

            if (!string.IsNullOrEmpty(it.Help))
            {
                var help = new Label
                {
                    Text = it.Help, Location = new Point(22, y), AutoSize = true,
                    ForeColor = SubFg, BackColor = Bg, Font = new Font("Segoe UI", 8.25f),
                    MaximumSize = new Size(560, 0),
                };
                panel.Controls.Add(help);
                y += help.GetPreferredSize(new Size(560, 0)).Height + 8;
            }
            else y += 4;
        }

        return (panel, () => { foreach (var a in applies) a(); });
    }

    private static void ApplyIfChanged(OptionItem it, string newValue)
    {
        if (string.Equals(it.Get(), newValue, StringComparison.Ordinal)) return;
        it.Set(newValue);
        try { it.ApplyLive?.Invoke(); } catch { }
    }
}
