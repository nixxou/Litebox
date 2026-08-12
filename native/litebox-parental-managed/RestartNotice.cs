// A deliberately loud RED "restart LaunchBox" notice, shown by the plugin's config + browser after a change
// that only the native .bin can apply — and the .bin reads the shared .dat + filters the platform XMLs ONLY
// when LaunchBox loads them at startup. So a config change made while LaunchBox is running does NOT take effect
// until the next launch. (This is a LaunchBox/BigBox-only limitation — LiteBox re-filters live and never shows
// this.) Kept unmistakable on purpose: a red banner, not a stock yellow warning triangle.

using System;
using System.Drawing;
using System.Windows.Forms;

namespace LiteBoxParental
{
    internal static class RestartNotice
    {
        public static void Show(IWin32Window owner)
        {
            try { using (var f = new NoticeForm()) f.ShowDialog(owner); }
            catch (Exception ex) { Log.Line("[RestartNotice] " + ex.Message); }
        }

        private sealed class NoticeForm : Form
        {
            public NoticeForm()
            {
                Text = "Restart required";
                FormBorderStyle = FormBorderStyle.FixedDialog;
                StartPosition = FormStartPosition.CenterParent;
                MaximizeBox = MinimizeBox = false; ShowInTaskbar = false;
                ClientSize = new Size(460, 172);
                Font = new Font("Segoe UI", 9f);
                BackColor = Color.White;

                var banner = new Panel { Dock = DockStyle.Top, Height = 44, BackColor = Color.FromArgb(200, 32, 32) };
                banner.Controls.Add(new Label
                {
                    Text = "⚠  Restart LaunchBox",
                    ForeColor = Color.White, Font = new Font("Segoe UI", 12f, FontStyle.Bold),
                    AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
                    Padding = new Padding(14, 0, 0, 0)
                });
                Controls.Add(banner);

                Controls.Add(new Label
                {
                    Text = "Your parental-control change is saved, but the library is filtered only when "
                         + "LaunchBox loads it at startup.\n\nClose and reopen LaunchBox for it to take effect. "
                         + "Until then the games stay visible.",
                    AutoSize = false, Location = new Point(16, 58), Size = new Size(428, 74),
                    ForeColor = Color.FromArgb(40, 40, 40)
                });

                var ok = new Button
                {
                    Text = "OK", DialogResult = DialogResult.OK, Width = 90,
                    Location = new Point(354, 134)
                };
                Controls.Add(ok);
                AcceptButton = ok; CancelButton = ok;
            }
        }
    }
}
