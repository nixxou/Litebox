// Multi-edit windows for platforms / categories / playlists — same conventions as the game editor's
// multi-select mode: a field shows the COMMON value across the selection, or the "‹multiple values›"
// placeholder when they differ; only fields the user actually CHANGES are written (a field still holding the
// placeholder or its initial text is never applied). UNIQUE fields (Unique Name / Title) are not editable in
// multi mode. Checkboxes are THREE-STATE (checked = set all, unchecked = clear all, indeterminate = mixed,
// left as-is). The Parents tab is the shared ParentsPicker in tri-state mode. Per user scope: Details +
// Parents only (no Notes / Folders / 3D / Documents in multi mode for now).

#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using LbApiHost.Host.Data;
using LbApiHost.Host.Options;
using LbApiHost.Host.UiKit;
using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Platforms;

internal static class MultiEditWindow
{
    private static readonly Color Bg = LiteBoxTheme.Bg, Panel2 = LiteBoxTheme.Panel2, Fg = LiteBoxTheme.Fg, SubFg = LiteBoxTheme.SubFg;
    private const string Multi = "‹multiple values›";

    // ── shared row machinery ──
    private sealed class Fields
    {
        public Panel Panel = null!;
        public readonly List<Action> Appliers = new();
        public int Y;
        public float Scale;
        public int S(int px) => (int)Math.Round(px * Scale);
    }

    private static Fields NewFields(float s)
    {
        var f = new Fields { Scale = s };
        f.Panel = new Panel { Dock = DockStyle.Fill, BackColor = Bg, AutoScroll = true, Padding = new Padding(f.S(12)) };
        f.Y = f.S(10);
        return f;
    }

    private static string CommonValue(IEnumerable<string> values)
    {
        string? first = null; bool any = false;
        foreach (var v in values)
        {
            var t = v ?? "";
            if (!any) { first = t; any = true; }
            else if (!string.Equals(first, t, StringComparison.Ordinal)) return Multi;
        }
        return first ?? "";
    }

    private static TextBox AddText(Fields f, string label, string common, Action<string> setAll)
    {
        f.Panel.Controls.Add(new Label { Text = label, Location = new Point(f.S(6), f.Y + f.S(4)), AutoSize = true, ForeColor = SubFg, BackColor = Bg });
        var tb = new TextBox { Location = new Point(f.S(130), f.Y), Width = f.S(320), BackColor = Panel2, ForeColor = common == Multi ? SubFg : Fg, BorderStyle = BorderStyle.FixedSingle, Text = common };
        tb.TextChanged += (_, _) => tb.ForeColor = tb.Text == Multi ? SubFg : Fg;
        f.Panel.Controls.Add(tb);
        f.Y += f.S(34);
        string init = common;
        f.Appliers.Add(() => { var v = tb.Text; if (v != init && v != Multi) setAll(v.Trim()); });
        return tb;
    }

    private static ComboBox AddCombo(Fields f, string label, string common, string[] choices, Action<string> setAll)
    {
        f.Panel.Controls.Add(new Label { Text = label, Location = new Point(f.S(6), f.Y + f.S(4)), AutoSize = true, ForeColor = SubFg, BackColor = Bg });
        var cb = new ComboBox { Location = new Point(f.S(130), f.Y), Width = f.S(320), DropDownStyle = ComboBoxStyle.DropDown, FlatStyle = FlatStyle.Flat, BackColor = Panel2, ForeColor = Fg };
        cb.Items.Add("");
        foreach (var c in choices) cb.Items.Add(c);
        cb.Text = common;
        f.Panel.Controls.Add(cb);
        f.Y += f.S(34);
        string init = common;
        f.Appliers.Add(() => { var v = cb.Text; if (v != init && v != Multi) setAll(v.Trim()); });
        return cb;
    }

    private static void AddTriCheck(Fields f, string label, IEnumerable<bool> values, Action<bool> setAll)
    {
        int cnt = 0, total = 0;
        foreach (var v in values) { total++; if (v) cnt++; }
        var state = cnt == 0 ? CheckState.Unchecked : cnt == total ? CheckState.Checked : CheckState.Indeterminate;
        var chk = new CheckBox { Text = label, Location = new Point(f.S(6), f.Y), AutoSize = true, ForeColor = Fg, BackColor = Bg, ThreeState = true, CheckState = state };
        f.Panel.Controls.Add(chk);
        f.Y += f.S(28);
        var init = state;
        f.Appliers.Add(() => { if (chk.CheckState != init && chk.CheckState != CheckState.Indeterminate) setAll(chk.CheckState == CheckState.Checked); });
    }

    private static void RunAppliers(Fields f) { foreach (var a in f.Appliers) { try { a(); } catch { } } }

    // ── platforms ──
    public static void OpenPlatforms(List<IPlatform> plats, bool readOnly, IWin32Window? owner)
    {
        if (plats == null || plats.Count < 2) return;
        using var w = new OptionsWindow($"Edit {plats.Count} Platforms{(readOnly ? "   [READ-ONLY]" : "")}");
        float s = LiteBoxTheme.DpiScale(w);
        var f = NewFields(s);

        string C(Func<IPlatform, string?> get) => CommonValue(plats.Select(pl => Safe(() => get(pl)) ?? ""));
        void All(Action<IPlatform> set) { foreach (var pl in plats) try { set(pl); } catch { } }

        AddCombo(f, "Scrape As:", C(pl => pl.ScrapeAs), EditPlatformWindow.ScrapeAsChoices(), v => All(pl => pl.ScrapeAs = v));
        AddText(f, "Release Date:", CommonValue(plats.Select(pl => EditPlatformWindow.DateStr(Safe(() => pl.ReleaseDate)))), v => All(pl => pl.ReleaseDate = EditPlatformWindow.ParseDate(v)));
        AddText(f, "Developer:", C(pl => pl.Developer), v => All(pl => pl.Developer = v));
        AddText(f, "Manufacturer:", C(pl => pl.Manufacturer), v => All(pl => pl.Manufacturer = v));
        AddText(f, "CPU:", C(pl => pl.Cpu), v => All(pl => pl.Cpu = v));
        AddText(f, "Memory:", C(pl => pl.Memory), v => All(pl => pl.Memory = v));
        AddText(f, "Graphics:", C(pl => pl.Graphics), v => All(pl => pl.Graphics = v));
        AddText(f, "Sound:", C(pl => pl.Sound), v => All(pl => pl.Sound = v));
        AddText(f, "Display:", C(pl => pl.Display), v => All(pl => pl.Display = v));
        AddText(f, "Media:", C(pl => pl.Media), v => All(pl => pl.Media = v));
        AddText(f, "Max Controllers:", C(pl => pl.MaxControllers), v => All(pl => pl.MaxControllers = v));
        AddText(f, "Video Path:", C(pl => pl.VideoPath), v => All(pl => pl.VideoPath = v));
        AddText(f, "Sort Title:", C(pl => pl.SortTitle), v => All(pl => pl.SortTitle = v));
        AddTriCheck(f, "Hide in Big Box (Does Not Hide Games)", plats.Select(pl => Safe(() => pl.HideInBigBox)),
            v => All(pl => { if (pl is Data.HostPlatform hp) hp.HideInBigBox = v; }));
        AddTriCheck(f, "Disable ROM Auto-Import", plats.Select(pl => string.Equals((pl as ILiteBoxFields)?.GetField("DisableAutoImport"), "true", StringComparison.OrdinalIgnoreCase)),
            v => All(pl => (pl as ILiteBoxFields)?.SetField("DisableAutoImport", v ? "true" : "false")));

        w.AddSection("Details", f.Panel, () => { if (!readOnly) RunAppliers(f); });
        var names = plats.Select(pl => Safe(() => pl.Name) ?? "").Where(nm => nm.Length > 0).ToList();
        var (parents, applyParents) = ParentsPicker.BuildMulti(ParentChildKind.Platform, names, readOnly, s);
        w.AddSection("Parents", parents, applyParents);

        w.ShowDialog(owner);
        if (!readOnly) { try { (PluginHelper.DataManager as HostDataManagerXml)?.FlushIfSafe(); } catch { } }
    }

    // ── categories ──
    public static void OpenCategories(List<HostPlatformCategory> cats, bool readOnly, IWin32Window? owner)
    {
        if (cats == null || cats.Count < 2) return;
        using var w = new OptionsWindow($"Edit {cats.Count} Platform Categories{(readOnly ? "   [READ-ONLY]" : "")}");
        float s = LiteBoxTheme.DpiScale(w);
        var f = NewFields(s);

        void All(Action<HostPlatformCategory> set) { foreach (var c in cats) try { set(c); } catch { } }
        AddText(f, "Nested Name:", CommonValue(cats.Select(c => Safe(() => c.NestedName) ?? "")), v => All(c => c.NestedName = v));
        AddText(f, "Sort Title:", CommonValue(cats.Select(c => Safe(() => c.SortTitle) ?? "")), v => All(c => c.SortTitle = v));
        AddText(f, "Video Path:", CommonValue(cats.Select(c => Safe(() => c.VideoPath) ?? "")), v => All(c => c.VideoPath = v));
        AddTriCheck(f, "Hide in Big Box (Does Not Hide Games)", cats.Select(c => Safe(() => c.HideInBigBox)), v => All(c => c.HideInBigBox = v));

        w.AddSection("Details", f.Panel, () => { if (!readOnly) RunAppliers(f); });
        var names = cats.Select(c => Safe(() => c.Name) ?? "").Where(nm => nm.Length > 0).ToList();
        var (parents, applyParents) = ParentsPicker.BuildMulti(ParentChildKind.Category, names, readOnly, s);
        w.AddSection("Parents", parents, applyParents);

        w.ShowDialog(owner);
        if (!readOnly) { try { (PluginHelper.DataManager as HostDataManagerXml)?.FlushIfSafe(); } catch { } }
    }

    // ── playlists ──
    public static void OpenPlaylists(List<HostPlaylist> pls, bool readOnly, IWin32Window? owner)
    {
        if (pls == null || pls.Count < 2) return;
        using var w = new OptionsWindow($"Edit {pls.Count} Playlists{(readOnly ? "   [READ-ONLY]" : "")}");
        float s = LiteBoxTheme.DpiScale(w);
        var f = NewFields(s);

        void All(Action<HostPlaylist> set) { foreach (var pl in pls) try { set(pl); } catch { } }
        AddText(f, "Nested Name:", CommonValue(pls.Select(pl => Safe(() => pl.NestedName) ?? "")), v => All(pl => pl.NestedName = v));
        AddText(f, "Sort Title:", CommonValue(pls.Select(pl => Safe(() => pl.SortTitle) ?? "")), v => All(pl => pl.SortTitle = v));
        AddText(f, "Video Path:", CommonValue(pls.Select(pl => Safe(() => pl.VideoPath) ?? "")), v => All(pl => pl.VideoPath = v));
        // The whole Arrange By vocabulary, not just "Default" — the same list the single-playlist
        // editor offers, so a selection is not restricted to the one value that used to be here.
        var sortLabels = new List<string> { "Default", "Manual" };
        sortLabels.AddRange(GameSortCatalog.Standard.Select(d => d.Label));
        sortLabels.AddRange(GameSortCatalog.CustomFieldNames(
            Safe(() => PluginHelper.DataManager.GetAllGames()) ?? Array.Empty<IGame>()));
        AddCombo(f, "Sort Games By:",
            CommonValue(pls.Select(pl => GameSortCatalog.Label(GameSortCatalog.Parse(Safe(() => pl.SortBy))))),
            sortLabels.ToArray(),
            v => All(pl => pl.SortBy = GameSortCatalog.ToLaunchBoxValue(GameSortCatalog.Parse(v))));
        AddTriCheck(f, "Include this Playlist with Platforms", pls.Select(pl => Safe(() => pl.IncludeWithPlatforms)), v => All(pl => pl.IncludeWithPlatforms = v));
        AddTriCheck(f, "Hide in Big Box (Does Not Hide Games)", pls.Select(pl => Safe(() => pl.HideInBigBox)), v => All(pl => pl.HideInBigBox = v));

        w.AddSection("Details", f.Panel, () => { if (!readOnly) RunAppliers(f); });

        // Auto-Populate and Games, restricted to what the selection has in COMMON. The merge and
        // the difference-based write-back live in PlaylistMultiEdit.
        var (autoPanel, applyAuto) = EditPlaylistPopulate.BuildAutoPopulateMulti(pls, readOnly, s);
        w.AddSection("Auto-Populate", autoPanel, applyAuto);
        var (gamesPanel, applyGames) = EditPlaylistPopulate.BuildGamesMulti(pls, readOnly, s);
        w.AddSection("Games", gamesPanel, applyGames);

        var ids = pls.Select(pl => Safe(() => pl.PlaylistIdValue) ?? "").Where(id => id.Length > 0).ToList();
        var (parents, applyParents) = ParentsPicker.BuildMulti(ParentChildKind.Playlist, ids, readOnly, s);
        w.AddSection("Parents", parents, applyParents);

        w.ShowDialog(owner);
        if (!readOnly) { try { (PluginHelper.DataManager as HostDataManagerXml)?.FlushIfSafe(); } catch { } }
    }

    private static T? Safe<T>(Func<T> f) { try { return f(); } catch { return default; } }
}
