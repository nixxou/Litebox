// Add / edit a custom badge: a name, an image, and the conditions under which it shows.
//
// The conditions grid is NOT a new editor — it is the very control Edit Playlist ▸ Auto-Populate
// uses (EditPlaylistPopulate.BuildRuleGrid), so the field list, the per-type comparisons and the
// custom fields all behave exactly as they do there, and a rule written here means the same thing a
// rule written on a playlist means. Rules on different fields AND together, repeated rules on the
// same field OR together.
//
// The live "shows on N games" is the reason to bother: a rule set you can't count is a rule set you
// can't trust. Compiling the plan and walking the library costs a few milliseconds.

#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using LbApiHost.Host.Platforms;
using LbApiHost.Host.UiKit;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Badges;

internal sealed class CustomBadgeDialog : LiteBoxForm
{
    private readonly BadgeCustom _badge;
    private readonly Func<IReadOnlyList<IGame>> _games;
    private readonly TextBox _name;
    private readonly PictureBox _preview;
    private readonly Label _count;
    private readonly DataGridView _grid;
    private readonly Func<List<Data.PlaylistFilterDef>> _readRules;
    private string? _pendingImage;   // a PNG written by the image editor, not yet committed

    private CustomBadgeDialog(BadgeCustom badge, Func<IReadOnlyList<IGame>> games, float dpi)
    {
        _badge = badge;
        _games = games;

        Text = string.IsNullOrWhiteSpace(badge.Id) ? "Add Custom Badge" : "Edit Custom Badge";
        ClientSize = new Size(S(720), S(520));
        MinimumSize = new Size(S(560), S(420));
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false; MaximizeBox = false;

        // ── top: name + image ────────────────────────────────────────────────
        var top = new Panel { Dock = DockStyle.Top, Height = S(110), BackColor = LiteBoxTheme.Bg };
        var nameLabel = new Label
        { Text = "Name", AutoSize = true, ForeColor = LiteBoxTheme.SubFg, Location = new Point(S(12), S(14)) };
        _name = new TextBox
        {
            Text = badge.Name, Location = new Point(S(12), S(34)), Width = S(420),
            BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg, BorderStyle = BorderStyle.FixedSingle,
        };
        var imgLabel = new Label
        { Text = "Image", AutoSize = true, ForeColor = LiteBoxTheme.SubFg, Location = new Point(S(470), S(14)) };
        _preview = new PictureBox
        {
            Location = new Point(S(470), S(34)), Size = new Size(S(56), S(56)),
            BackColor = LiteBoxTheme.Panel2, SizeMode = PictureBoxSizeMode.Zoom,
        };
        var pick = ActionButton("Choose Image…");
        pick.Location = new Point(S(538), S(40));
        pick.Click += (_, _) => PickImage();

        top.Controls.AddRange(new Control[] { nameLabel, _name, imgLabel, _preview, pick });

        // ── middle: the rule grid ────────────────────────────────────────────
        var rulesLabel = new Label
        {
            Dock = DockStyle.Top, Height = S(24), ForeColor = LiteBoxTheme.SubFg, Padding = new Padding(S(12), S(4), 0, 0),
            Text = "Shows on a game when — rules on different fields must ALL match, rules on the same field match if ANY does",
        };
        (_grid, _readRules) = EditPlaylistPopulate.BuildRuleGrid(
            badge.Rules.Select(r => r.ToDef()), readOnly: false, dpiScale: dpi);
        _grid.Dock = DockStyle.Fill;
        _grid.CellValueChanged += (_, _) => UpdateCount();
        _grid.RowsRemoved += (_, _) => UpdateCount();
        _grid.CurrentCellDirtyStateChanged += (_, _) => { if (_grid.IsCurrentCellDirty) UpdateCount(); };

        var mid = new Panel { Dock = DockStyle.Fill, BackColor = LiteBoxTheme.Bg, Padding = new Padding(S(12), 0, S(12), 0) };
        mid.Controls.Add(_grid);
        mid.Controls.Add(rulesLabel);

        // ── footer ───────────────────────────────────────────────────────────
        var footer = new Panel { Dock = DockStyle.Bottom, BackColor = LiteBoxTheme.PanelC, Height = S(44) };
        _count = new Label
        {
            Dock = DockStyle.Left, AutoSize = false, Width = S(300), ForeColor = LiteBoxTheme.SubFg,
            TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(S(12), 0, 0, 0),
        };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Right, FlowDirection = FlowDirection.RightToLeft, WrapContents = false,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = LiteBoxTheme.PanelC,
            Padding = new Padding(0, S(8), S(12), 0),
        };
        var ok = ActionButton("OK");
        ok.BackColor = LiteBoxTheme.Ok;
        ok.Click += (_, _) => Commit();
        var cancel = ActionButton("Cancel");
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        footer.Controls.Add(_count);
        footer.Controls.Add(buttons);

        Controls.Add(mid);
        Controls.Add(top);
        Controls.Add(footer);
        CancelButton = cancel;

        ShowPreview();
        UpdateCount();
    }

    /// <summary>Edits the badge IN PLACE (name, rules) and returns OK when the user confirmed — and
    /// only then applies the rename, so a No or a Cancel after typing a new name leaves the stored
    /// badge untouched. The image, if one was chosen, has already been written to the pack folder
    /// (harmless on cancel: an image file with no definition is just an unused file).</summary>
    public static DialogResult Edit(IWin32Window owner, BadgeCustom badge,
                                    Func<IReadOnlyList<IGame>> games, float dpi)
    {
        using var dlg = new CustomBadgeDialog(badge, games, dpi);
        var result = dlg.ShowDialog(owner);
        if (result == DialogResult.OK) dlg.Apply();
        return result;
    }

    private void PickImage()
    {
        // The id is what names the file, and a brand-new badge doesn't have one yet — mint it from
        // the name typed so far, so the image lands on its final path right away.
        var probe = new BadgeCustom { Id = _badge.Id, Name = _name.Text.Trim() };
        string? path = BadgeImageDialog.Choose(this, probe, DpiScale);
        if (path == null) return;
        _badge.Id = probe.Id;      // minted by the dialog when it was empty
        _pendingImage = path;
        BadgeImages.Reset();
        ShowPreview();
    }

    private void ShowPreview()
    {
        try
        {
            var path = _pendingImage ?? BadgeCustomStore.ImagePath(_badge);
            _preview.Image?.Dispose();
            _preview.Image = path != null && System.IO.File.Exists(path)
                ? Image.FromStream(new System.IO.MemoryStream(System.IO.File.ReadAllBytes(path)))
                : null;
        }
        catch { _preview.Image = null; }
    }

    private void UpdateCount()
    {
        try
        {
            var rules = _readRules().Select(BadgeRule.From).ToList();
            if (rules.Count == 0) { _count.Text = "No condition yet — the badge would never show."; return; }

            // Some fields exist in the vocabulary but have no local answer in LiteBox (Steam/GOG
            // achievements, alternate names…). They compile away to nothing, so a badge resting on
            // them matches no game — silently, unless we say so here.
            var dead = rules.Select(r => r.Field).Distinct(StringComparer.OrdinalIgnoreCase)
                            .Where(f => PlaylistFilterCatalog.Find(f) is { Evaluable: false })
                            .ToList();
            if (dead.Count > 0)
            {
                _count.ForeColor = LiteBoxTheme.Danger;
                _count.Text = $"LiteBox cannot evaluate {string.Join(", ", dead)} — this badge will never show.";
                return;
            }
            _count.ForeColor = LiteBoxTheme.SubFg;
            int n = BadgeCustomStore.CountMatches(rules, _games());
            _count.Text = $"Shows on {n:n0} game{(n == 1 ? "" : "s")}.";
        }
        catch { _count.Text = ""; }
    }

    private void Commit()
    {
        string name = _name.Text.Trim();
        if (name.Length == 0)
        {
            MessageBox.Show(this, "Give the badge a name.", "Custom badge",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (BadgeCustomStore.NameTaken(name, _badge.Id))
        {
            MessageBox.Show(this, $"Another badge is already called \"{name}\".\n\n"
                + "Badge names are unique: the name is what names the image file too.",
                "Custom badge", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        _badge.Rules = _readRules().Select(BadgeRule.From).ToList();
        if (_badge.Rules.Count == 0)
        {
            var r = MessageBox.Show(this,
                "This badge has no condition, so it will never show on any game.\n\nSave it anyway?",
                "Custom badge", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes) return;
        }
        // NOTHING is applied from inside the dialog. Rename deletes the old definition, moves the
        // image and rewrites the settings the moment it runs — so run here, it had already destroyed
        // the badge when the user answered No above and then cancelled, with the outer Save never
        // reached. The dialog only records what was asked; Apply() does it after OK, next to the Save.
        _requestedName = name;
        DialogResult = DialogResult.OK;
        Close();
    }

    private string? _requestedName;

    /// <summary>Apply what the dialog collected — the rename last of all, once the caller is
    /// committed to saving. Call ONLY after ShowDialog returned OK, and follow with Save.</summary>
    public void Apply()
    {
        string name = _requestedName ?? _badge.Name;
        // The name IS the identity: renaming carries the id, the image file, the place in the order
        // and the enabled state along with it.
        if (string.IsNullOrWhiteSpace(_badge.Id)) _badge.Name = name;
        else BadgeCustomStore.Rename(_badge, name);
    }
}
