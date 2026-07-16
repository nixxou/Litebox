// Parental module config panel — manages BigBox's NATIVE parental PIN (LockPin in BigBoxSettings.xml): one
// PIN shared with BigBox, set or cleared here, visible to BigBox on its next start.
//
// Split out of ModulesOptions verbatim; ModulesOptions.ModuleConfigPanel dispatches here for LbModule.Parental.

#nullable enable

using System;
using System.Linq;
using System.Drawing;
using System.Windows.Forms;
using LbApiHost.Host.UiKit;

namespace LbApiHost.Host.Options;

internal static class ParentalPanel
{
    public static (Control panel, Action? apply) Build(float dpiS, bool readOnly)
    {
        int S(int px) => (int)Math.Round(px * dpiS);
        var Bg = LiteBoxTheme.Bg; var Fg = LiteBoxTheme.Fg; var Sub = LiteBoxTheme.SubFg;
        var p = new Panel { Dock = DockStyle.Fill, BackColor = Bg, AutoScroll = true, Padding = new Padding(S(16), S(14), S(16), S(10)) };

        p.Controls.Add(new Label { Text = "BigBox parental PIN", AutoSize = true, ForeColor = Fg, BackColor = Bg, Location = new Point(S(4), S(6)), Font = new Font("Segoe UI", 10f, FontStyle.Bold) });
        p.Controls.Add(new Label
        {
            Text = "This is BigBox's own four-digit PIN (BigBoxSettings.xml), not a separate code. Set or clear it here; BigBox picks the change up on its next start. Leave empty for no PIN.",
            AutoSize = true, MaximumSize = new Size(S(640), 0), ForeColor = Sub, BackColor = Bg,
            Location = new Point(S(4), S(30)), Font = new Font("Segoe UI", 8.5f),
        });

        bool available = Data.BigBoxPin.Available;
        p.Controls.Add(new Label { Text = "PIN", AutoSize = true, ForeColor = Sub, BackColor = Bg, Location = new Point(S(4), S(78)), Font = new Font("Segoe UI", 8.5f) });
        var pin = new TextBox
        {
            Location = new Point(S(4), S(96)), Width = S(120), BackColor = LiteBoxTheme.PanelC, ForeColor = Fg,
            BorderStyle = BorderStyle.FixedSingle, Font = new Font("Segoe UI", 9f), UseSystemPasswordChar = true,
            MaxLength = 8, ReadOnly = readOnly || !available,
        };
        p.Controls.Add(pin);
        var show = new CheckBox { Text = "Show", AutoSize = true, ForeColor = Sub, BackColor = Bg, Location = new Point(S(136), S(97)), Font = new Font("Segoe UI", 8.5f) };
        show.CheckedChanged += (_, _) => pin.UseSystemPasswordChar = !show.Checked;
        p.Controls.Add(show);

        var status = new Label { AutoSize = true, MaximumSize = new Size(S(640), 0), ForeColor = Sub, BackColor = Bg, Location = new Point(S(4), S(130)), Font = new Font("Segoe UI", 8.5f, FontStyle.Italic) };
        p.Controls.Add(status);

        if (!available)
            status.Text = "BigBoxSettings.xml was not found (BigBox has never run on this install).";
        else
        {
            try { pin.Text = Data.BigBoxPin.Current(); } catch { }
            status.Text = pin.Text.Length > 0 ? "A PIN is currently set." : "No PIN is currently set.";
        }

        void Apply()
        {
            if (readOnly || !available) return;
            var v = (pin.Text ?? "").Trim();
            if (v.Length > 0 && (v.Length != 4 || !v.All(char.IsAsciiDigit))) return;   // BigBox PINs are 4 digits
            Data.BigBoxPin.Set(v);
        }
        return (p, Apply);
    }
}
