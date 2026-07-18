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
            ItemSize = new Size(S(144), S(27)),
        };
        tabs.DrawItem += (_, e) =>
        {
            bool sel = e.Index == tabs.SelectedIndex;
            using var b = new SolidBrush(sel ? Field : PanelC);
            e.Graphics.FillRectangle(b, e.Bounds);
            TextRenderer.DrawText(e.Graphics, tabs.TabPages[e.Index].Text, tabs.Font, e.Bounds,
                sel ? Color.White : Sub, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        };

        // ── Tab 1: the module list, as a card grid (ExtendDB-style) ───────────────
        var listPage = new TabPage("Modules") { BackColor = Bg, UseVisualStyleBackColor = false };
        var ok = LiteBoxTheme.Ok;                       // enabled accent (green)
        var borderCol = Color.FromArgb(64, 64, 68);     // disabled card border

        // ── Config tabs (dynamic): one per ENABLED module, in catalog order ───────
        var configTabs = new Dictionary<LbModule, (TabPage page, Action? apply)>();

        void AddConfigTab(LbModule module)
        {
            if (configTabs.ContainsKey(module)) return;
            var page = new TabPage(TabLabel(module)) { BackColor = Bg, UseVisualStyleBackColor = false };
            var (cfgPanel, cfgApply) = ModuleConfigPanel(module, dpiS, readOnly);
            cfgPanel.Dock = DockStyle.Fill;
            page.Controls.Add(cfgPanel);

            int idx = 1;                                 // after the list tab, in catalog order
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

        var intro = new Label
        {
            Text = "Enable the ExtendDB features you want, natively in LiteBox. Each is independent; a module's own settings tab appears above while it is enabled. Click a card to toggle it.",
            Dock = DockStyle.Top, AutoSize = false, Height = S(46), ForeColor = Sub, BackColor = Bg,
            Font = new Font("Segoe UI", 8.5f), Padding = new Padding(S(16), S(12), S(16), S(6)),
        };
        var grid = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, AutoScroll = true, WrapContents = true, FlowDirection = FlowDirection.LeftToRight,
            BackColor = Bg, Padding = new Padding(S(12), S(4), S(12), S(8)),
        };

        var state = new Dictionary<LbModule, bool>();
        foreach (var m in LbModules.Catalog)
        {
            var module = m.Module;
            state[module] = LbModules.On(module);

            var card = new Panel
            {
                Width = S(524), BackColor = PanelC, Margin = new Padding(S(4), S(4), S(4), S(10)),
                Cursor = readOnly ? Cursors.Default : Cursors.Hand,
            };
            var title = new Label
            {
                Text = m.Title + (m.Ready ? "" : "   (coming soon)"), AutoSize = true, ForeColor = Fg, BackColor = PanelC,
                Font = new Font("Segoe UI", 10f, FontStyle.Bold), Location = new Point(S(14), S(11)),
            };
            var badge = new Label
            {
                AutoSize = true, BackColor = PanelC, Font = new Font("Segoe UI", 8.25f, FontStyle.Bold), Location = new Point(S(430), S(13)),
            };
            var desc = new Label
            {
                Text = m.Description, AutoSize = true, MaximumSize = new Size(S(496), 0), ForeColor = Sub, BackColor = PanelC,
                Font = new Font("Segoe UI", 8.5f), Location = new Point(S(14), S(36)),
            };

            void Repaint()
            {
                bool en = state[module];
                badge.Text = en ? "● ENABLED" : "● DISABLED";
                badge.ForeColor = en ? ok : Sub;
                badge.Location = new Point(card.Width - S(14) - badge.PreferredWidth, S(13));
                card.Invalidate();
            }
            card.Paint += (_, e) =>
            {
                bool en = state[module];
                using var pen = new Pen(en ? ok : borderCol, en ? 2f : 1f);
                float o = en ? 1f : 0.5f;
                e.Graphics.DrawRectangle(pen, o, o, card.Width - 1 - o, card.Height - 1 - o);
            };
            void Toggle()
            {
                if (readOnly) return;
                state[module] = !state[module];
                if (state[module]) AddConfigTab(module); else RemoveConfigTab(module);
                Repaint();
            }
            EventHandler click = (_, _) => Toggle();
            card.Click += click; title.Click += click; desc.Click += click; badge.Click += click;

            card.Controls.Add(title); card.Controls.Add(badge); card.Controls.Add(desc);
            card.Height = desc.Location.Y + desc.PreferredHeight + S(12);
            Repaint();
            grid.Controls.Add(card);
        }

        listPage.Controls.Add(grid);
        listPage.Controls.Add(intro);
        tabs.TabPages.Add(listPage);

        // Seed the config tabs for modules that are already enabled.
        foreach (var m in LbModules.Catalog)
            if (LbModules.On(m.Module)) AddConfigTab(m.Module);

        root.Controls.Add(tabs);

        void Apply()
        {
            if (readOnly) return;
            foreach (var kv in state)
            {
                // PIN gate (plugin parity): a LOCKED session cannot switch the Parental module off —
                // that would be the one-click bypass of the whole protection. Unlock first.
                if (kv.Key == LbModule.Parental && !kv.Value
                    && Parental.ParentalFilter.Active && Parental.ParentalFilter.HasPin)
                {
                    try
                    {
                        MessageBox.Show("Parental control is locked — unlock it (padlock / hotkey) before disabling the module.",
                            "Parental control", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                    catch { }
                    continue;
                }
                LbModules.SetOn(kv.Key, kv.Value);
            }
            foreach (var kv in configTabs) { try { kv.Value.apply?.Invoke(); } catch { } }
            ReconcileRuntime();   // apply toggles that own a live service (the web server) without a restart
        }
        return (root, Apply);
    }

    /// <summary>Apply module toggles that own a live background service so they take effect WITHOUT a restart.
    /// Only the Web module runs a persistent server (started once at boot); the others gate their behaviour at
    /// call time (they read LbModules.On live), so toggling them needs no reconcile here.</summary>
    private static void ReconcileRuntime()
    {
        try
        {
            bool want = LbModules.On(LbModule.Web);
            bool running = Web.EmbeddedWebServer.IsRunning;
            if (want && !running)
            {
                Web.WebAssets.EnsureDeployed();
                int port = int.TryParse(LiteBoxConfig.LoadForExe().GetSec("Web", "Port"), out var p) ? p : 8080;
                Web.EmbeddedWebServer.Start(port);
            }
            else if (!want && running) Web.EmbeddedWebServer.Stop();
        }
        catch { }
    }

    /// <summary>Short, consistent tab label for a module's config tab (the catalog Title is often too long).</summary>
    private static string TabLabel(LbModule m) => m switch
    {
        LbModule.Base              => "Base",
        LbModule.Rom               => "ROM extractor",
        LbModule.RetroAchievements => "RetroAchievements",
        LbModule.Parental          => "Parental",
        LbModule.Web               => "Web",
        _                          => m.ToString(),
    };

    /// <summary>Dispatches to the module's own config panel file, each ported to native parity.</summary>
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
