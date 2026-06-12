// Manage Emulators window (LB parity, V3 v1: list + Edit). Columns: Name,
// Application Path, Default Command-Line Parameters, Version (async via the
// integration plugin) and a ✓ Status when a plugin claims the emulator.
// Add / Delete / Update All are deferred (entity creation + batch installs).
//
// Read-only mode: Edit opens the editor with every input disabled.

#nullable enable

using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Emulators;

internal sealed class ManageEmulatorsWindow : Form
{
    private static readonly Color Bg = Color.FromArgb(30, 30, 30);
    private static readonly Color Panel2 = Color.FromArgb(45, 45, 48);
    private static readonly Color Fg = Color.FromArgb(222, 222, 222);

    private readonly ListView _list;
    private readonly bool _readOnly;
    private readonly string _lbRoot;

    public ManageEmulatorsWindow(bool readOnly, string lbRoot)
    {
        _readOnly = readOnly; _lbRoot = lbRoot;
        Text = "Manage Emulators" + (readOnly ? "   [READ-ONLY]" : "");
        Size = new Size(900, 520);
        MinimumSize = new Size(640, 320);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Bg; ForeColor = Fg;
        Font = new Font("Segoe UI", 9.5f);
        ShowIcon = false; ShowInTaskbar = false; MinimizeBox = false; MaximizeBox = false;

        _list = new ListView
        {
            Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false,
            BackColor = Color.FromArgb(37, 37, 38), ForeColor = Fg, BorderStyle = BorderStyle.None,
            HeaderStyle = ColumnHeaderStyle.Nonclickable, HideSelection = false,
        };
        _list.Columns.Add("Name", 160);
        _list.Columns.Add("Application Path", 290);
        _list.Columns.Add("Default Command-Line Parameters", 220);
        _list.Columns.Add("Version", 110);
        _list.Columns.Add("Status", 60, HorizontalAlignment.Center);
        _list.DoubleClick += (_, _) => EditSelected();

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 44, BackColor = Panel2 };
        var edit = Btn("Edit…", new Point(12, 8));
        edit.Click += (_, _) => EditSelected();
        var close = Btn("Close", new Point(0, 8));
        close.Click += (_, _) => Close();
        footer.Resize += (_, _) => close.Left = footer.ClientSize.Width - close.Width - 12;
        footer.Controls.Add(edit); footer.Controls.Add(close);

        Controls.Add(_list);
        Controls.Add(footer);
        _list.BringToFront();

        Fill();
    }

    private void Fill()
    {
        _list.Items.Clear();
        IEmulator[] emus;
        try { emus = PluginHelper.DataManager?.GetAllEmulators() ?? Array.Empty<IEmulator>(); }
        catch { emus = Array.Empty<IEmulator>(); }

        foreach (var e in emus.OrderBy(x => Safe(() => x.Title) ?? "", StringComparer.OrdinalIgnoreCase))
        {
            var item = new ListViewItem(new[]
            {
                Safe(() => e.Title) ?? "?",
                Safe(() => e.ApplicationPath) ?? "",
                Safe(() => e.CommandLine) ?? "",
                "",   // version (async)
                "",   // status
            })
            { Tag = e };
            _list.Items.Add(item);
        }

        // Version + plugin status, async (GetCurrentVersion may probe the exe).
        System.Threading.Tasks.Task.Run(() =>
        {
            foreach (ListViewItem item in _list.Items)
            {
                if (item.Tag is not IEmulator e) continue;
                var plugin = EmuPlugins.ForEmulator(e);
                string ver = plugin != null ? (EmuPlugins.CurrentVersion(e) ?? "") : "";
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        if (IsDisposed || item.ListView == null) return;
                        item.SubItems[3].Text = ver;
                        item.SubItems[4].Text = plugin != null ? "✓" : "";
                    }));
                }
                catch { }
            }
        });
    }

    private void EditSelected()
    {
        if (_list.SelectedItems.Count == 0 || _list.SelectedItems[0].Tag is not IEmulator e) return;
        EditEmulatorWindow.Open(e, _readOnly, this, _lbRoot);
        Fill();   // labels may have changed
    }

    private static Button Btn(string text, Point loc) => new()
    {
        Text = text, Location = loc, Size = new Size(96, 28),
        FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(60, 60, 75), ForeColor = Color.White,
        FlatAppearance = { BorderSize = 0 }, Font = new Font("Segoe UI", 9f, FontStyle.Bold),
    };

    private static T? Safe<T>(Func<T?> f) { try { return f(); } catch { return default; } }
}
