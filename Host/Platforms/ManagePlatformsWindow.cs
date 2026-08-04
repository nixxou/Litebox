// Manage Platforms window (LB parity). Columns: Platform Name, Folder (the platform's ROM folder as
// stored) and Associated Games. Built on the same shape as Manage Emulators — same list, same
// mixed-alignment footer, same read-only rules.
//
// Edit… opens the full platform editor (EditPlatformWindow); Delete routes through the shared
// NodeDeleter, so a platform deleted here disappears exactly like one deleted from the tree
// (Platforms.xml + Parents.xml + its games, media and ROM files left on disk). Add… is not
// implemented yet.
//
// <see cref="Changed"/> tells the caller whether the hierarchy needs reloading once the window closes.
//
// Read-only mode: Edit opens the editor with every input disabled; Delete and Add are locked.

#nullable enable

using LbApiHost.Host.Data;
using LbApiHost.Host.Media;
using LbApiHost.Host.UiKit;
using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Platforms;

internal sealed class ManagePlatformsWindow : LiteBoxForm
{
    private readonly ListView _list;
    private readonly bool _readOnly;
    private readonly string _lbRoot;

    /// <summary>True once a platform was edited or deleted — the caller reloads the tree.</summary>
    public bool Changed { get; private set; }

    public ManagePlatformsWindow(bool readOnly, string lbRoot)
    {
        _readOnly = readOnly; _lbRoot = lbRoot;
        Text = "Manage Platforms" + (readOnly ? "   [READ-ONLY]" : "");
        ClientSize = new Size(S(820), S(520));
        MinimumSize = new Size(S(620), S(320));
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false; MaximizeBox = false;

        _list = new ListView
        {
            Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false,
            BackColor = LiteBoxTheme.PanelC, ForeColor = LiteBoxTheme.Fg, BorderStyle = BorderStyle.None,
            HeaderStyle = ColumnHeaderStyle.Nonclickable, HideSelection = false,
        };
        _list.Columns.Add("Platform Name", S(230));
        _list.Columns.Add("Folder", S(330));
        _list.Columns.Add("Associated Games", S(130));
        _list.DoubleClick += (_, _) => EditSelected();

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

        var add = ActionButton("Add…", MenuIcons.Add);
        add.Enabled = !readOnly;
        if (readOnly) add.Text = "Add 🔒";
        add.Click += (_, _) => MessageBox.Show(this, "Not implemented yet.",
            "Add Platform", MessageBoxButtons.OK, MessageBoxIcon.Information);
        var edit = ActionButton("Edit…", MenuIcons.Edit);
        edit.Click += (_, _) => EditSelected();
        var del = ActionButton("Delete", MenuIcons.Delete);
        del.Enabled = !readOnly;
        if (readOnly) del.Text = "Delete 🔒";
        del.Click += (_, _) => DeleteSelected();
        leftGroup.Controls.Add(add); leftGroup.Controls.Add(edit); leftGroup.Controls.Add(del);

        var close = ActionButton("Close", MenuIcons.Exit);
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
        string? keep = _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Text : null;
        _list.Items.Clear();
        IPlatform[] plats;
        try { plats = PluginHelper.DataManager?.GetAllPlatforms() ?? Array.Empty<IPlatform>(); }
        catch { plats = Array.Empty<IPlatform>(); }

        foreach (var p in plats.OrderBy(x => Safe(() => x.Name) ?? "", StringComparer.OrdinalIgnoreCase))
        {
            string name = Safe(() => p.Name) ?? "?";
            // Hidden AND broken games included: this is the "how many games does this platform own"
            // figure, not what the current view filters down to.
            int games = Safe(() => p.GetGameCount(true, true));
            _list.Items.Add(new ListViewItem(new[] { name, Safe(() => p.Folder) ?? "", games.ToString() }) { Tag = p });
        }

        if (keep != null)
            foreach (ListViewItem it in _list.Items)
                if (string.Equals(it.Text, keep, StringComparison.OrdinalIgnoreCase)) { it.Selected = true; it.EnsureVisible(); break; }
    }

    private void EditSelected()
    {
        if (_list.SelectedItems.Count == 0 || _list.SelectedItems[0].Tag is not IPlatform p) return;
        try { EditPlatformWindow.Open(p, _readOnly, this, _lbRoot); }
        catch (Exception ex) { Console.WriteLine("[manageplat] edit: " + ex.Message); }
        if (!_readOnly) Changed = true;   // name / folder / media paths may have moved
        Fill();
    }

    private void DeleteSelected()
    {
        if (_list.SelectedItems.Count == 0 || _list.SelectedItems[0].Tag is not IPlatform p) return;
        bool did = false;
        try { did = NodeDeleter.DeletePlatforms(new List<IPlatform> { p }, PluginHelper.DataManager as HostDataManagerXml, this); }
        catch (Exception ex) { Console.WriteLine("[manageplat] delete: " + ex.Message); }
        if (!did) return;
        Changed = true;
        Fill();
    }

    private static T? Safe<T>(Func<T?> f) { try { return f(); } catch { return default; } }
}
