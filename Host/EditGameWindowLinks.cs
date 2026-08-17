// Edit Game → "Links" page — LiteBox's replica of LaunchBox 14's Links tab (Edit Game → Media →
// Links): web links attached to a game, stored as <AdditionalApplication> records whose
// <Section> is Link (the v14 model; see HostAdditionalApplication.EffectiveSection).
//
// The list shows every EFFECTIVE link — the explicitly stamped Section=Link records AND legacy
// records whose ApplicationPath is an http(s) URL (LB 14 routes those to its Links tab the same
// way). Records created here are stamped Section=Link explicitly, like LB 14's Add Link does.
//
// Layout mirrors the v14 tab: a Name | Path list, Add/Edit/Delete buttons, Move Up / Move Down.
// The Edit dialog is deliberately minimal — Name + URL — because a link has no launch semantics
// (no emulator, no autorun, no command line). URLs without a scheme get https:// prepended, so
// what lands in the XML always matches LB's own http(s) link detection.

#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using LbApiHost.Host.Data;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host;

internal partial class EditGameWindow
{
    private Panel? _linkPage;
    private ListView? _linkList;
    private Button? _linkAdd, _linkEdit, _linkDel, _linkUp, _linkDown;

    private Control BuildLinksPage()
    {
        _linkPage = new Panel { BackColor = Bg };
        _linkList = NewAppListView();
        _linkList.DoubleClick += (_, _) => { var a = SelectedLink(); if (a != null && ShowLinkDialog(a)) ReloadLinks(); };
        _linkList.SelectedIndexChanged += (_, _) => UpdateLinkButtons();

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = S(46), BackColor = Bg };
        _linkAdd = FooterBtn("Add Link…", Color.FromArgb(60, 60, 72));
        _linkEdit = FooterBtn("Edit Link…", Color.FromArgb(60, 60, 72));
        _linkDel = FooterBtn("Delete Link", Color.FromArgb(60, 60, 72));
        _linkUp = FooterBtn("Move Up", Color.FromArgb(60, 60, 72));
        _linkDown = FooterBtn("Move Down", Color.FromArgb(60, 60, 72));
        _linkAdd.Click += (_, _) => { if (ShowLinkDialog(null)) ReloadLinks(); };
        _linkEdit.Click += (_, _) => { var a = SelectedLink(); if (a != null && ShowLinkDialog(a)) ReloadLinks(); };
        _linkDel.Click += (_, _) => DeleteLink(SelectedLink());
        _linkUp.Click += (_, _) => MoveLink(-1);
        _linkDown.Click += (_, _) => MoveLink(+1);
        bottom.Controls.AddRange(new Control[] { _linkAdd, _linkEdit, _linkDel, _linkUp, _linkDown });
        bottom.Resize += (_, _) =>
        {
            _linkAdd.SetBounds(S(6), S(8), S(130), S(30));
            _linkEdit.SetBounds(S(142), S(8), S(130), S(30));
            _linkDel.SetBounds(S(278), S(8), S(130), S(30));
            _linkDown.SetBounds(bottom.ClientSize.Width - S(136), S(8), S(130), S(30));
            _linkUp.SetBounds(bottom.ClientSize.Width - S(272), S(8), S(130), S(30));
        };

        _linkPage.Controls.Add(_linkList);
        _linkPage.Controls.Add(bottom);
        _linkList.BringToFront();
        ReloadLinks();
        return _linkPage;
    }

    /// <summary>The game's links, in file order — effective Section (explicit Link + legacy URL rows).</summary>
    private List<HostAdditionalApplication> LinkRecords()
    {
        var list = new List<HostAdditionalApplication>();
        try
        {
            foreach (var a in AppsGame.GetAllAdditionalApplications() ?? Array.Empty<IAdditionalApplication>())
                if (a is HostAdditionalApplication { IsLink: true } h) list.Add(h);
        }
        catch { }
        return list;
    }

    private HostAdditionalApplication? SelectedLink()
        => _linkList?.SelectedItems.Count > 0 ? _linkList.SelectedItems[0].Tag as HostAdditionalApplication : null;

    private void ReloadLinks()
    {
        if (_linkList == null) return;
        FillAppList(_linkList, LinkRecords());
        UpdateLinkButtons();
    }

    private void UpdateLinkButtons()
    {
        bool has = SelectedLink() != null;
        if (_linkEdit != null) _linkEdit.Enabled = has && !_readOnly;
        if (_linkDel != null) _linkDel.Enabled = has && !_readOnly;
        if (_linkUp != null) _linkUp.Enabled = has && !_readOnly;
        if (_linkDown != null) _linkDown.Enabled = has && !_readOnly;
        if (_linkAdd != null) _linkAdd.Enabled = !_readOnly;
    }

    private void DeleteLink(HostAdditionalApplication? a)
    {
        if (a == null || _readOnly) return;
        string name = Safe(() => a.Name) ?? "";
        if (MessageBox.Show(this, $"Delete the link \"{name}\"?", "Delete",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        try { AppsGame.TryRemoveAdditionalApplication(a); } catch (Exception ex) { Console.WriteLine("[links] delete failed: " + ex.Message); }
        ReloadLinks();
        ReloadAddAppsIfBuilt();   // links are double-listed on the Apps page (LB 14 parity)
    }

    /// <summary>Swap the selected link with its neighbour in the filtered list (the swap happens on
    /// the two records' positions in the game's add-app list — the XML/display order).</summary>
    private void MoveLink(int dir)
    {
        var sel = SelectedLink();
        if (sel == null || _readOnly) return;
        var links = LinkRecords();
        int i = links.FindIndex(l => ReferenceEquals(l, sel));
        int j = i + dir;
        if (i < 0 || j < 0 || j >= links.Count) return;
        try { sel.SwapPositionWith(links[j]); } catch (Exception ex) { Console.WriteLine("[links] move failed: " + ex.Message); }
        ReloadLinks();
        // keep the moved row selected
        if (_linkList != null)
            foreach (ListViewItem it in _linkList.Items)
                if (ReferenceEquals(it.Tag, sel)) { it.Selected = true; it.EnsureVisible(); break; }
    }

    // ── "Edit Link" dialog — Name + URL, nothing else ─────────────────────
    private bool ShowLinkDialog(HostAdditionalApplication? link)
    {
        var g = AppsGame;
        using var f = NewDialog(link == null ? "Add Link" : "Edit Link", 560, 240);

        var body = new Panel { Dock = DockStyle.Fill, BackColor = Bg };
        f.Controls.Add(body);

        int x = S(16), w = S(500), y = S(14);
        DlgCap(body, "Link Name:", x, y); y += S(20);
        var name = DlgTxt(body, Safe(() => link?.Name) ?? "", x, y, w); y += S(34);
        DlgCap(body, "URL:", x, y); y += S(20);
        var url = DlgTxt(body, Safe(() => link?.ApplicationPath) ?? "", x, y, w);

        return RunAddAppDialog(f, link, () =>
        {
            string u = url.Text.Trim();
            if (u.Length == 0) return false;
            // No scheme → https://, so the stored path always matches LB's http(s) link detection.
            if (!u.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                u = "https://" + u;
            var a = link ?? g.AddNewAdditionalApplication() as HostAdditionalApplication;
            if (a == null) return false;
            ApplyStr(v => a.Name = v, Safe(() => a.Name), name.Text.Trim());
            ApplyStr(v => a.ApplicationPath = v, Safe(() => a.ApplicationPath), u);
            if (!string.Equals(a.Section, HostAdditionalApplication.LinkSection, StringComparison.OrdinalIgnoreCase))
                a.Section = HostAdditionalApplication.LinkSection;   // explicit stamp, like LB 14's Add Link
            ReloadAddAppsIfBuilt();   // double-listed on the Apps page (LB 14 parity)
            return true;
        });
    }
}
