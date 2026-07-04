// Manage Emulators window (LB parity, V3). Columns: Name, Application Path,
// Default Command-Line Parameters, Version (async via the integration plugin)
// and a ✓ Status when a plugin claims the emulator. Add… uses LB's own preset
// DB (AddEmulatorWindow); Delete confirms then routes through the op-log
// (the flush also drops the emulator's EmulatorPlatform rows).
//
// The zero-GUID emulator (LB's hidden "unassigned" placeholder row) is
// filtered out, like LaunchBox's own Manage Emulators does.
//
// Read-only mode: Edit opens the editor with every input disabled.

#nullable enable

using LbApiHost.Host.UiKit;
using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Emulators;

internal sealed class ManageEmulatorsWindow : LiteBoxForm
{
    private readonly ListView _list;
    private readonly bool _readOnly;
    private readonly string _lbRoot;

    public ManageEmulatorsWindow(bool readOnly, string lbRoot)
    {
        _readOnly = readOnly; _lbRoot = lbRoot;
        Text = "Manage Emulators" + (readOnly ? "   [READ-ONLY]" : "");
        ClientSize = new Size(S(900), S(520));
        MinimumSize = new Size(S(640), S(320));
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false; MaximizeBox = false;

        _list = new ListView
        {
            Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false,
            BackColor = LiteBoxTheme.PanelC, ForeColor = LiteBoxTheme.Fg, BorderStyle = BorderStyle.None,
            HeaderStyle = ColumnHeaderStyle.Nonclickable, HideSelection = false,
        };
        _list.Columns.Add("Name", S(160));
        _list.Columns.Add("Application Path", S(290));
        _list.Columns.Add("Default Command-Line Parameters", S(220));
        _list.Columns.Add("Version", S(110));
        _list.Columns.Add("Status", S(60), HorizontalAlignment.Center);
        _list.DoubleClick += (_, _) => EditSelected();

        // Mixed-alignment footer (action buttons on the left, Close on the right) doesn't fit
        // FooterBar's single-direction layout, so it's built directly here - but still DPI-scaled
        // and Dock/FlowLayoutPanel-based (no manual Point positions, no manual resize-repositioning
        // of Close - Dock=Right handles that for free instead of the old footer.Resize handler).
        var footer = new Panel { Dock = DockStyle.Bottom, BackColor = LiteBoxTheme.PanelC, Height = S(44) };
        var leftGroup = new FlowLayoutPanel
        {
            Dock = DockStyle.Left, FlowDirection = FlowDirection.LeftToRight, WrapContents = false,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = LiteBoxTheme.PanelC,
            Padding = new Padding(S(12), S(8), 0, 0),
        };
        var rightGroup = new FlowLayoutPanel
        {
            Dock = DockStyle.Right, FlowDirection = FlowDirection.RightToLeft, WrapContents = false,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = LiteBoxTheme.PanelC,
            Padding = new Padding(0, S(8), S(12), 0),
        };

        var edit = Btn("Edit…");
        edit.Click += (_, _) => EditSelected();
        var add = Btn("Add…");
        add.Enabled = !readOnly;
        if (readOnly) add.Text = "Add 🔒";
        add.Click += (_, _) => AddEmulator();
        var del = Btn("Delete");
        del.Enabled = !readOnly;
        if (readOnly) del.Text = "Delete 🔒";
        del.Click += (_, _) => DeleteSelected();
        // Update All: every plugin-managed emulator with a newer installable
        // version gets InstallEmulator run sequentially (per-emulator progress
        // dialog).
        var updAll = Btn("Update All");
        updAll.Enabled = !readOnly;
        if (readOnly) updAll.Text = "Update All 🔒";
        updAll.Click += (_, _) => UpdateAll();
        leftGroup.Controls.Add(edit); leftGroup.Controls.Add(add); leftGroup.Controls.Add(del); leftGroup.Controls.Add(updAll);

        var close = Btn("Close");
        close.Click += (_, _) => Close();
        rightGroup.Controls.Add(close);

        footer.Controls.Add(leftGroup);
        footer.Controls.Add(rightGroup);

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
            // LB hides its internal "unassigned" placeholder (Guid.Empty) — so do we.
            if (Guid.TryParse(Safe(() => e.Id) ?? "", out var gid) && gid == Guid.Empty) continue;
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
        // SNAPSHOT the items on the UI thread first — enumerating _list.Items
        // from the worker is a cross-thread access that can die mid-loop.
        var snapshot = _list.Items.Cast<ListViewItem>()
            .Where(i => i.Tag is IEmulator)
            .Select(i => (item: i, emu: (IEmulator)i.Tag!))
            .ToList();
        System.Threading.Tasks.Task.Run(() =>
        {
            foreach (var (item, e) in snapshot)
            {
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

    private void AddEmulator()
    {
        using var dlg = new AddEmulatorWindow(_lbRoot);
        if (dlg.ShowDialog(this) != DialogResult.OK || dlg.Created == null) { return; }
        // Open the full editor right away so the user can review/adjust the preset.
        EditEmulatorWindow.Open(dlg.Created, _readOnly, this, _lbRoot);
        Fill();
    }

    private void DeleteSelected()
    {
        if (_list.SelectedItems.Count == 0 || _list.SelectedItems[0].Tag is not IEmulator e) return;
        var name = Safe(() => e.Title) ?? "?";
        if (MessageBox.Show(this,
                $"Delete \"{name}\"?\n\nIts platform associations are removed too; games assigned to it lose their emulator.",
                "Delete Emulator", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        bool ok = false;
        try { ok = PluginHelper.DataManager?.TryRemoveEmulator(e) ?? false; } catch { }
        if (!ok)
            MessageBox.Show(this, "Could not delete the emulator.", "Delete Emulator", MessageBoxButtons.OK, MessageBoxIcon.Error);
        Fill();
    }

    private void EditSelected()
    {
        if (_list.SelectedItems.Count == 0 || _list.SelectedItems[0].Tag is not IEmulator e) return;
        EditEmulatorWindow.Open(e, _readOnly, this, _lbRoot);
        Fill();   // labels may have changed
    }

    /// <summary>Sequentially update every plugin-managed emulator whose latest
    /// installable version differs from the current one.</summary>
    private void UpdateAll()
    {
        var work = new List<(EmulatorPlugin plugin, IEmulator emu, string version, string title)>();
        foreach (ListViewItem item in _list.Items)
        {
            if (item.Tag is not IEmulator e) continue;
            var plugin = EmuPlugins.ForEmulator(e);
            if (plugin == null) continue;
            string? cur = EmuPlugins.CurrentVersion(e);
            EmulatorControllerVersion? latest = null;
            try { latest = plugin.GetInstallableVersions()?.FirstOrDefault(); } catch { }
            var id = Safe(() => latest?.Identifier);
            var label = Safe(() => latest?.Label) ?? id;
            if (id == null) continue;
            bool upToDate = cur != null && (cur == id || cur == label);
            if (!upToDate) work.Add((plugin, e, id!, Safe(() => e.Title) ?? "?"));
        }
        if (work.Count == 0)
        {
            MessageBox.Show(this, "Every plugin-managed emulator is up to date.", "Update All", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var namesList = string.Join("\n", work.Select(w => "  • " + w.title + "  →  " + w.version));
        if (MessageBox.Show(this, $"Update {work.Count} emulator(s)?\n\n{namesList}", "Update All",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        foreach (var (plugin, e, version, _) in work)
            EditEmulatorWindow.RunInstall(plugin, e, version, this);
        Fill();
    }

    private Button Btn(string text) => new()
    {
        Text = text, Size = new Size(S(96), S(28)), Margin = new Padding(0, 0, S(8), 0),
        FlatStyle = FlatStyle.Flat, BackColor = LiteBoxTheme.CancelBtn, ForeColor = Color.White,
        FlatAppearance = { BorderSize = 0 }, Font = new Font("Segoe UI", 9f, FontStyle.Bold),
    };

    private static T? Safe<T>(Func<T?> f) { try { return f(); } catch { return default; } }
}
