// Options → Modules: the multi-tab module manager.
//   Tab 1 ("Modules")  — lists every module with an enable checkbox + description; the master on/off page.
//   Tabs 2..N          — one per ENABLED module, its own settings. A module's config tab appears only while
//                        the module is on; toggling its checkbox adds / removes the tab live.
//
// Native LiteBox UI (no ExtendDB dependency). Enable state is persisted by the section's apply callback through
// LbModules.SetOn, so it follows the standard OptionsWindow Apply/OK flow. Each per-module config panel lives in
// its own file under Host/Options/Modules/ (BasePanel, RomPanel, ParentalPanel, WebPanel, RaPanel); this class
// only lays out the tabs and dispatches to XxxPanel.Build by module.

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
            Text = "Enable the ExtendDB features you want, natively in LiteBox. Each is independent; a module's own settings tab appears here while it is enabled.",
            AutoSize = true, MaximumSize = new Size(S(680), 0), ForeColor = Sub, BackColor = Bg,
            Font = new Font("Segoe UI", 8.5f), Margin = new Padding(0, 0, 0, S(14)),
        });

        // ── Config tabs (dynamic): one per ENABLED module, in catalog order ───────
        // configTabs maps a live module → its open tab + apply. Adding/removing keeps Apply's collection current.
        var configTabs = new Dictionary<LbModule, (TabPage page, Action? apply)>();

        void AddConfigTab(LbModule module)
        {
            if (configTabs.ContainsKey(module)) return;
            var meta = LbModules.Meta(module);
            var page = new TabPage(meta.Title.Length > 16 ? meta.Key : meta.Title) { BackColor = Bg, UseVisualStyleBackColor = false };
            var (cfgPanel, cfgApply) = ModuleConfigPanel(module, dpiS, readOnly);
            cfgPanel.Dock = DockStyle.Fill;
            page.Controls.Add(cfgPanel);

            // Insert after the list tab, preserving catalog order among the currently-open config tabs.
            int idx = 1;
            foreach (var c in LbModules.Catalog)
            {
                if (c.Module == module) break;
                if (configTabs.ContainsKey(c.Module)) idx++;
            }
            tabs.TabPages.Insert(idx, page);
            configTabs[module] = (page, cfgApply);
        }

        void RemoveConfigTab(LbModule module)
        {
            if (!configTabs.TryGetValue(module, out var t)) return;
            tabs.TabPages.Remove(t.page);
            t.page.Dispose();
            configTabs.Remove(module);
        }

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
            var module = m.Module;
            cb.CheckedChanged += (_, _) =>
            {
                if (cb.Checked) AddConfigTab(module);
                else RemoveConfigTab(module);
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

        // Seed the config tabs for modules that are already enabled.
        foreach (var m in LbModules.Catalog)
            if (LbModules.On(m.Module)) AddConfigTab(m.Module);

        root.Controls.Add(tabs);

        void Apply()
        {
            if (readOnly) return;
            foreach (var (module, cb) in checks) LbModules.SetOn(module, cb.Checked);
            foreach (var kv in configTabs) { try { kv.Value.apply?.Invoke(); } catch { } }
        }
        return (root, Apply);
    }

    /// <summary>Dispatches to the module's own config panel file. Base/Parental/Rom have real settings; Web and
    /// RetroAchievements are placeholders until their ports land.</summary>
    private static (Control panel, Action? apply) ModuleConfigPanel(LbModule module, float dpiS, bool readOnly) => module switch
    {
        LbModule.Base             => BasePanel.Build(dpiS, readOnly),
        LbModule.Parental         => ParentalPanel.Build(dpiS, readOnly),
        LbModule.Rom              => RomPanel.Build(dpiS, readOnly),
        LbModule.Web              => WebPanel.Build(dpiS, readOnly),
        LbModule.RetroAchievements => RaPanel.Build(dpiS, readOnly),
        _                         => (new Panel { Dock = DockStyle.Fill, BackColor = LiteBoxTheme.Bg }, (Action?)null),
    };
}
