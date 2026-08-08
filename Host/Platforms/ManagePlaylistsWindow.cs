// Manage Playlists window — the playlist twin of ManagePlatformsWindow, built on the same shape as it
// and as Manage Emulators: same list, same mixed-alignment footer, same read-only rules.
//
// It fills a real gap. A playlist could only be reached through the tree — find it in the hierarchy,
// right-click, Edit — so there was no way to see them all at once, or to spot the two that share a
// Sort Title, or the auto-populate one whose rules resolve to nothing.
//
// Columns: Unique Name (a playlist's identity — Parents.xml and every image folder key on it), Nested
// Name, Sort Title, Auto (rule-driven vs hand-picked membership), Associated Games.
//
// Edit… opens the full playlist editor (EditPlaylistWindow); Delete routes through the shared
// NodeDeleter, so a playlist deleted here disappears exactly like one deleted from the tree. Add… is
// not implemented — same as the platforms window, and for the same reason: creating one is a different
// job from managing the ones that exist.
//
// <see cref="Changed"/> tells the caller whether the hierarchy needs reloading once the window closes.
//
// Read-only mode: Edit opens the editor with every input disabled; Delete and Add are locked.

#nullable enable

using LbApiHost.Host.Data;
using LbApiHost.Host.UiKit;
using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Platforms;

internal sealed class ManagePlaylistsWindow : LiteBoxForm
{
    private readonly ListView _list;
    private readonly bool _readOnly;

    /// <summary>True once a playlist was edited or deleted — the caller reloads the tree.</summary>
    public bool Changed { get; private set; }

    public ManagePlaylistsWindow(bool readOnly)
    {
        _readOnly = readOnly;
        Text = "Manage Playlists" + (readOnly ? "   [READ-ONLY]" : "");
        ClientSize = new Size(S(880), S(520));
        MinimumSize = new Size(S(640), S(320));
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false; MaximizeBox = false;

        _list = new ListView
        {
            Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false,
            BackColor = LiteBoxTheme.PanelC, ForeColor = LiteBoxTheme.Fg, BorderStyle = BorderStyle.None,
            HeaderStyle = ColumnHeaderStyle.Nonclickable, HideSelection = false,
        };
        _list.Columns.Add("Unique Name", S(240));
        _list.Columns.Add("Nested Name", S(180));
        _list.Columns.Add("Sort Title", S(180));
        _list.Columns.Add("Auto", S(60));
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
            "Add Playlist", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
        IPlaylist[] all;
        try { all = PluginHelper.DataManager?.GetAllPlaylists() ?? Array.Empty<IPlaylist>(); }
        catch { all = Array.Empty<IPlaylist>(); }

        foreach (var pl in all.OfType<HostPlaylist>()
                              .OrderBy(x => Safe(() => x.NameValue) ?? "", StringComparer.OrdinalIgnoreCase))
        {
            string name = Safe(() => pl.NameValue) ?? "?";
            bool auto = Safe(() => pl.AutoPopulateValue);
            // Hidden AND broken games included, like the platforms window: this is "how many games does
            // this playlist hold", not what the current view filters down to. For an auto playlist that
            // means running its rules — the compiled plan is cached on the playlist, so it is one pass.
            int games = Safe(() => pl.GetGameCount(true, true));
            _list.Items.Add(new ListViewItem(new[]
            {
                name,
                Safe(() => pl.NestedNameValue) ?? "",
                Safe(() => pl.SortTitleValue) ?? "",
                auto ? "Yes" : "",
                games.ToString(),
            })
            { Tag = pl });
        }

        if (keep != null)
            foreach (ListViewItem it in _list.Items)
                if (string.Equals(it.Text, keep, StringComparison.OrdinalIgnoreCase)) { it.Selected = true; it.EnsureVisible(); break; }
    }

    private void EditSelected()
    {
        if (_list.SelectedItems.Count == 0 || _list.SelectedItems[0].Tag is not HostPlaylist pl) return;
        try { EditPlaylistWindow.Open(pl, _readOnly, this); }
        catch (Exception ex) { Console.WriteLine("[managepl] edit: " + ex.Message); }
        if (!_readOnly) Changed = true;   // name / nesting / membership may have moved
        Fill();
    }

    private void DeleteSelected()
    {
        if (_list.SelectedItems.Count == 0 || _list.SelectedItems[0].Tag is not HostPlaylist pl) return;
        bool did = false;
        try { did = NodeDeleter.DeletePlaylists(new List<HostPlaylist> { pl }, PluginHelper.DataManager as HostDataManagerXml, this); }
        catch (Exception ex) { Console.WriteLine("[managepl] delete: " + ex.Message); }
        if (!did) return;
        Changed = true;
        Fill();
    }

    private static T? Safe<T>(Func<T?> f) { try { return f(); } catch { return default; } }
}
