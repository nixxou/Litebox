// Shared, ExtendDB-style dark-themed UI factory for the module config panels.
//
// Every module panel (Base / Rom / Parental / Web / Ra / …) builds its controls through this kit so they
// share ONE consistent look — dark GroupBoxes, section headers, labeled fields, a themed grid — instead of
// each panel hand-rolling its own colors and spacing. Colors come from LiteBoxTheme (the live palette), so a
// palette tweak in Options → Colors reflows through here too.
//
// All factories take the panel's DPI scale (dpiS) and scale their own metrics; call Sc(dpiS, px) for any
// extra pixel math. Nothing here persists anything — panels own their apply callbacks.

#nullable enable

using System;
using System.Drawing;
using System.Windows.Forms;
using LbApiHost.Host.UiKit;

namespace LbApiHost.Host.Options;

/// <summary>Dark-themed control factories shared by every module config panel. Static + stateless.</summary>
internal static class ModulePanelKit
{
    // ── Live palette shortcuts ────────────────────────────────────────────────
    public static Color Bg     => LiteBoxTheme.Bg;
    public static Color Panel  => LiteBoxTheme.PanelC;
    public static Color Field  => LiteBoxTheme.Panel2;
    public static Color Fg     => LiteBoxTheme.Fg;
    public static Color Sub    => LiteBoxTheme.SubFg;
    public static Color Accent => LiteBoxTheme.Accent;

    /// <summary>DPI-scale a pixel value (rounded).</summary>
    public static int Sc(float dpiS, int px) => (int)Math.Round(px * dpiS);

    // ── Root / containers ─────────────────────────────────────────────────────

    /// <summary>The standard scrolling, padded dark panel a module fills. Dock = Fill.</summary>
    public static Panel Root(float dpiS) => new()
    {
        Dock = DockStyle.Fill, BackColor = Bg, AutoScroll = true,
        Padding = new Padding(Sc(dpiS, 16), Sc(dpiS, 14), Sc(dpiS, 16), Sc(dpiS, 10)),
    };

    /// <summary>A dark GroupBox with a titled border, ready to host a child layout. Set its Location / Size
    /// (or Dock) after creation.</summary>
    public static GroupBox Group(string title, float dpiS) => new()
    {
        Text = title, ForeColor = Fg, BackColor = Bg,
        Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
        Padding = new Padding(Sc(dpiS, 10), Sc(dpiS, 8), Sc(dpiS, 10), Sc(dpiS, 8)),
        AutoSize = false,
    };

    // ── Text bits ─────────────────────────────────────────────────────────────

    /// <summary>A bold section header (auto-size). Position via .Location.</summary>
    public static Label Header(string text, float dpiS) => new()
    {
        Text = text, AutoSize = true, ForeColor = Fg, BackColor = Bg,
        Font = new Font("Segoe UI", 10f, FontStyle.Bold),
    };

    /// <summary>A wrapped secondary caption (auto-size, capped width). Position via .Location.</summary>
    public static Label Caption(string text, float dpiS, int maxWidth = 640) => new()
    {
        Text = text, AutoSize = true, MaximumSize = new Size(Sc(dpiS, maxWidth), 0),
        ForeColor = Sub, BackColor = Bg, Font = new Font("Segoe UI", 8.5f),
    };

    // ── Fields ────────────────────────────────────────────────────────────────

    /// <summary>A themed single-line TextBox (optionally password). Position / width via caller.</summary>
    public static TextBox TextField(float dpiS, bool readOnly = false, bool password = false, int width = 300) => new()
    {
        Width = Sc(dpiS, width), BackColor = Field, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle,
        Font = new Font("Segoe UI", 9f), UseSystemPasswordChar = password, ReadOnly = readOnly,
    };

    /// <summary>A labeled TextBox: returns the caption Label (place it above) and the box.</summary>
    public static (Label label, TextBox box) LabeledTextField(string label, float dpiS, bool readOnly = false, bool password = false, int width = 300)
        => (Caption(label, dpiS), TextField(dpiS, readOnly, password, width));

    /// <summary>A themed drop-down ComboBox (DropDownList by default).</summary>
    public static ComboBox Combo(float dpiS, bool readOnly = false, int width = 300, ComboBoxStyle style = ComboBoxStyle.DropDownList) => new()
    {
        Width = Sc(dpiS, width), BackColor = Field, ForeColor = Fg, FlatStyle = FlatStyle.Flat,
        DropDownStyle = style, Font = new Font("Segoe UI", 9f), Enabled = !readOnly,
    };

    /// <summary>A labeled ComboBox: returns the caption Label and the combo.</summary>
    public static (Label label, ComboBox combo) LabeledCombo(string label, float dpiS, bool readOnly = false, int width = 300, ComboBoxStyle style = ComboBoxStyle.DropDownList)
        => (Caption(label, dpiS), Combo(dpiS, readOnly, width, style));

    /// <summary>A themed CheckBox.</summary>
    public static CheckBox Check(string text, float dpiS, bool value = false, bool readOnly = false) => new()
    {
        Text = text, AutoSize = true, Checked = value, Enabled = !readOnly,
        ForeColor = Fg, BackColor = Bg, Font = new Font("Segoe UI", 9f),
    };

    /// <summary>A themed flat Button (auto-size).</summary>
    public static Button Button(string text, float dpiS, bool readOnly = false)
    {
        var b = new System.Windows.Forms.Button
        {
            Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat,
            BackColor = Field, ForeColor = Fg, Enabled = !readOnly, Font = new Font("Segoe UI", 9f),
        };
        b.FlatAppearance.BorderColor = Field;
        return b;
    }

    // ── Grid ──────────────────────────────────────────────────────────────────

    /// <summary>A dark, read-friendly DataGridView (no add-rows glyph, full-row select, themed headers).
    /// Add columns and rows after creation.</summary>
    public static DataGridView Grid(float dpiS, bool readOnly = false)
    {
        var g = new DataGridView
        {
            BackgroundColor = Panel, ForeColor = Fg, GridColor = LiteBoxTheme.Panel2,
            BorderStyle = BorderStyle.None, EnableHeadersVisualStyles = false,
            AllowUserToAddRows = false, AllowUserToResizeRows = false, RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false,
            ReadOnly = readOnly, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            Font = new Font("Segoe UI", 9f), ColumnHeadersHeight = Sc(dpiS, 26),
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
        };
        g.ColumnHeadersDefaultCellStyle.BackColor = Field;
        g.ColumnHeadersDefaultCellStyle.ForeColor = Fg;
        g.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
        g.DefaultCellStyle.BackColor = Panel;
        g.DefaultCellStyle.ForeColor = Fg;
        g.DefaultCellStyle.SelectionBackColor = Accent;
        g.DefaultCellStyle.SelectionForeColor = Color.White;
        g.RowsDefaultCellStyle.BackColor = Panel;
        g.AlternatingRowsDefaultCellStyle.BackColor = LiteBoxTheme.Panel2;
        return g;
    }

    // ── Layout ────────────────────────────────────────────────────────────────

    /// <summary>A 2-column TableLayoutPanel (left = auto-size labels, right = fill fields), dark-themed and
    /// auto-growing vertically. Add rows with Controls.Add(ctl, col, row).</summary>
    public static TableLayoutPanel TwoColumn(float dpiS, int rows = 0)
    {
        var t = new TableLayoutPanel
        {
            ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Bg, GrowStyle = TableLayoutPanelGrowStyle.AddRows,
            Padding = new Padding(0), Margin = new Padding(0),
        };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        for (int i = 0; i < rows; i++) t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        return t;
    }

    // ── Stub ──────────────────────────────────────────────────────────────────

    /// <summary>The shared "not yet ported" placeholder a stub module panel returns.</summary>
    public static (Control panel, Action? apply) Placeholder(string title, string description, float dpiS)
    {
        var p = Root(dpiS);
        var head = Header(title, dpiS); head.Location = new Point(Sc(dpiS, 4), Sc(dpiS, 6)); p.Controls.Add(head);
        var desc = Caption(description, dpiS); desc.Location = new Point(Sc(dpiS, 4), Sc(dpiS, 32)); p.Controls.Add(desc);
        var note = new Label
        {
            Text = "This module's settings will appear here as it is ported into LiteBox.",
            AutoSize = true, ForeColor = Sub, BackColor = Bg,
            Location = new Point(Sc(dpiS, 4), Sc(dpiS, 72)), Font = new Font("Segoe UI", 8.5f, FontStyle.Italic),
        };
        p.Controls.Add(note);
        return (p, null);
    }
}
