// Options → Modules: the multi-tab module manager.
//   Tab 1 ("Modules")  — lists every module with an enable checkbox + description; the master on/off page.
//   Tabs 2..N          — one per module, its own settings (placeholder until each module's port lands).
//
// Native LiteBox UI (no ExtendDB dependency). Enable state is persisted by the section's apply callback through
// LbModules.SetOn, so it follows the standard OptionsWindow Apply/OK flow. Per-module config panels plug in here
// as they are ported (replace the placeholder in ModuleConfigPanel).

#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using LbApiHost.Host.Modules;
using LbApiHost.Host.UiKit;

namespace LbApiHost.Host.Options;

internal static class ModulesOptions
{
    public static (Control panel, Action apply) Build(float dpiS, bool readOnly)
    {
        int S(int px) => (int)Math.Round(px * dpiS);
        var Bg = LiteBoxTheme.Bg; var Fg = LiteBoxTheme.Fg; var Sub = LiteBoxTheme.SubFg;
        var Field = LiteBoxTheme.Panel2; var PanelC = LiteBoxTheme.PanelC;

        var root = new Panel { Dock = DockStyle.Fill, BackColor = Bg };
        var tabs = new TabControl
        {
            Dock = DockStyle.Fill, DrawMode = TabDrawMode.OwnerDrawFixed, SizeMode = TabSizeMode.Fixed,
            ItemSize = new Size(S(116), S(26)),
        };
        tabs.DrawItem += (_, e) =>
        {
            bool sel = e.Index == tabs.SelectedIndex;
            using var b = new SolidBrush(sel ? Field : PanelC);
            e.Graphics.FillRectangle(b, e.Bounds);
            TextRenderer.DrawText(e.Graphics, tabs.TabPages[e.Index].Text, tabs.Font, e.Bounds,
                sel ? Color.White : Sub, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        };

        // ── Tab 1: the module list ────────────────────────────────────────────────
        var listPage = new TabPage("Modules") { BackColor = Bg, UseVisualStyleBackColor = false };
        var scroll = new Panel { Dock = DockStyle.Fill, BackColor = Bg, AutoScroll = true, Padding = new Padding(S(14), S(12), S(14), S(8)) };
        var flow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Bg,
        };
        flow.Controls.Add(new Label
        {
            Text = "Enable the ExtendDB features you want, natively in LiteBox. Each is independent; use the tabs above for a module's own settings.",
            AutoSize = true, MaximumSize = new Size(S(680), 0), ForeColor = Sub, BackColor = Bg,
            Font = new Font("Segoe UI", 8.5f), Margin = new Padding(0, 0, 0, S(14)),
        });

        var checks = new List<(LbModule module, CheckBox cb)>();
        foreach (var m in LbModules.Catalog)
        {
            var cb = new CheckBox
            {
                Text = m.Title + (m.Ready ? "" : "   (coming soon)"),
                AutoSize = true, Checked = LbModules.On(m.Module), Enabled = !readOnly,
                ForeColor = Fg, BackColor = Bg, Font = new Font("Segoe UI", 9.75f, FontStyle.Bold),
                Margin = new Padding(0, 0, 0, S(1)),
            };
            var desc = new Label
            {
                Text = m.Description, AutoSize = true, MaximumSize = new Size(S(680), 0),
                ForeColor = Sub, BackColor = Bg, Font = new Font("Segoe UI", 8.5f),
                Margin = new Padding(S(22), 0, 0, S(14)),
            };
            flow.Controls.Add(cb);
            flow.Controls.Add(desc);
            checks.Add((m.Module, cb));
        }
        scroll.Controls.Add(flow);
        listPage.Controls.Add(scroll);
        tabs.TabPages.Add(listPage);

        // ── Tabs 2..N: one per module (its own settings) ──────────────────────────
        foreach (var m in LbModules.Catalog)
        {
            var page = new TabPage(m.Title.Length > 16 ? m.Key : m.Title) { BackColor = Bg, UseVisualStyleBackColor = false };
            page.Controls.Add(ModuleConfigPanel(m, dpiS));
            tabs.TabPages.Add(page);
        }

        root.Controls.Add(tabs);

        void Apply()
        {
            if (readOnly) return;
            foreach (var (module, cb) in checks) LbModules.SetOn(module, cb.Checked);
        }
        return (root, Apply);
    }

    /// <summary>The per-module settings panel. A placeholder for now; each module's real config replaces this
    /// as it is ported (media sources + credentials for Base, ArchiveMGS options for Rom, and so on).</summary>
    private static Control ModuleConfigPanel(LbModules.Info m, float dpiS)
    {
        int S(int px) => (int)Math.Round(px * dpiS);
        var p = new Panel { Dock = DockStyle.Fill, BackColor = LiteBoxTheme.Bg, AutoScroll = true, Padding = new Padding(S(16), S(14), S(16), S(8)) };
        p.Controls.Add(new Label
        {
            Text = m.Title, AutoSize = true, ForeColor = LiteBoxTheme.Fg, BackColor = LiteBoxTheme.Bg,
            Location = new Point(S(4), S(6)), Font = new Font("Segoe UI", 10f, FontStyle.Bold),
        });
        p.Controls.Add(new Label
        {
            Text = m.Description, AutoSize = true, MaximumSize = new Size(S(640), 0),
            ForeColor = LiteBoxTheme.SubFg, BackColor = LiteBoxTheme.Bg, Location = new Point(S(4), S(32)),
            Font = new Font("Segoe UI", 8.5f),
        });
        p.Controls.Add(new Label
        {
            Text = "This module's settings will appear here as it is ported into LiteBox.",
            AutoSize = true, ForeColor = LiteBoxTheme.SubFg, BackColor = LiteBoxTheme.Bg,
            Location = new Point(S(4), S(72)), Font = new Font("Segoe UI", 8.5f, FontStyle.Italic),
        });
        return p;
    }
}
