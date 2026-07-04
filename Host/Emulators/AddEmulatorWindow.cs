// Add Emulator dialog (LB parity). A preset combo — seeded from LB's own
// Add-Emulator preset DB (see EmuPresets) — auto-fills the name, default
// command line, behaviour flags and the per-platform command lines (RetroArch
// cores etc.). Recommended platforms come pre-checked; BIOS hints are shown
// inline. Nothing is created until OK: then the emulator + checked platforms
// go through the normal write path (AddNewEmulator → op-log → XML flush) and
// the full editor opens for review.

#nullable enable

using System.IO;
using LbApiHost.Host.UiKit;
using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Emulators;

internal sealed class AddEmulatorWindow : LiteBoxForm
{
    private readonly string _lbRoot;
    private readonly ComboBox _preset;
    private readonly TextBox _name;
    private readonly TextBox _path;
    private readonly TextBox _cmd;
    private readonly CheckBox _noQuotes, _noSpace, _hideConsole, _fileNameOnly, _autoExtract;
    private readonly Label _hint;
    private readonly CheckedListBox _platforms;
    private List<EmuPresetPlatform> _platformRows = new();

    /// <summary>The emulator created on OK (null if cancelled).</summary>
    public IEmulator? Created { get; private set; }

    public AddEmulatorWindow(string lbRoot)
    {
        _lbRoot = lbRoot;
        Text = "Add Emulator";
        ClientSize = new Size(S(680), S(640));
        MinimumSize = new Size(S(620), S(520));
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false; MaximizeBox = false;
        FormBorderStyle = FormBorderStyle.FixedDialog;

        var presets = EmuPresets.Load(lbRoot);

        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(S(12)), BackColor = LiteBoxTheme.Bg };
        int y = S(10);

        Lbl(body, S(8), y, "Preset");
        _preset = new ComboBox
        {
            Location = new Point(S(8), y + S(20)), Width = S(320), DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg, FlatStyle = FlatStyle.Flat,
        };
        _preset.Items.Add("(Custom emulator)");
        foreach (var p in presets) _preset.Items.Add(p.Name);
        _preset.SelectedIndex = 0;
        _preset.SelectedIndexChanged += (_, _) => ApplyPreset(
            _preset.SelectedIndex > 0 ? presets[_preset.SelectedIndex - 1] : null);
        body.Controls.Add(_preset);

        _hint = new Label { Location = new Point(S(340), y + S(23)), AutoSize = true, ForeColor = LiteBoxTheme.SubFg, BackColor = LiteBoxTheme.Bg };
        body.Controls.Add(_hint);
        y += S(52);

        Lbl(body, S(8), y, "Name");
        _name = Txt(body, new Point(S(8), y + S(20)), S(320));
        y += S(52);

        Lbl(body, S(8), y, "Application Path");
        _path = Txt(body, new Point(S(8), y + S(20)), S(524));
        var browse = new Button
        {
            Text = "Browse…", Location = new Point(S(540), y + S(18)), Size = new Size(S(88), S(26)),
            FlatStyle = FlatStyle.Flat, BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg,
            FlatAppearance = { BorderSize = 0 }, Font = new Font("Segoe UI", 8.5f),
        };
        browse.Click += (_, _) => BrowseExe();
        body.Controls.Add(browse);
        y += S(52);

        Lbl(body, S(8), y, "Default Command-Line Parameters");
        _cmd = Txt(body, new Point(S(8), y + S(20)), S(620));
        y += S(52);

        _noQuotes = Chk(body, new Point(S(8), y), "Don't use quotes");
        _noSpace = Chk(body, new Point(S(160), y), "No space before ROM");
        _hideConsole = Chk(body, new Point(S(340), y), "Attempt to hide console");
        y += S(26);
        _fileNameOnly = Chk(body, new Point(S(8), y), "Use file name only (no path/extension)");
        _autoExtract = Chk(body, new Point(S(340), y), "Extract ROM archives");
        y += S(34);

        Lbl(body, S(8), y, "Associated Platforms   (check the ones you use — BIOS requirements shown inline)");
        _platforms = new CheckedListBox
        {
            Location = new Point(S(8), y + S(20)), Size = new Size(S(620), S(240)),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
            BackColor = LiteBoxTheme.PanelC, ForeColor = LiteBoxTheme.Fg, BorderStyle = BorderStyle.FixedSingle,
            CheckOnClick = true, IntegralHeight = false,
        };
        body.Controls.Add(_platforms);

        var footer = new FooterBar();
        var cancel = footer.AddButton("Cancel", LiteBoxTheme.CancelBtn, (_, _) => { DialogResult = DialogResult.Cancel; Close(); });
        var ok = footer.AddButton("Add", LiteBoxTheme.Ok, (_, _) => { if (CreateEmulator()) { DialogResult = DialogResult.OK; Close(); } });
        AcceptButton = ok; CancelButton = cancel;

        Controls.Add(body);
        Controls.Add(footer);
        body.BringToFront();
    }

    private void ApplyPreset(EmuPreset? p)
    {
        _name.Text = p?.Name ?? "";
        _cmd.Text = p?.CommandLine ?? "";
        _noQuotes.Checked = p?.NoQuotes ?? false;
        _noSpace.Checked = p?.NoSpace ?? false;
        _hideConsole.Checked = p?.HideConsole ?? false;
        _fileNameOnly.Checked = p?.FileNameOnly ?? false;
        _autoExtract.Checked = p?.AutoExtract ?? false;
        _hint.Text = p == null ? "" : "Executable: " + p.BinaryFileName + (p.Url.Length > 0 ? "   —   " + p.Url : "");

        _platformRows = p?.Platforms ?? new List<EmuPresetPlatform>();
        _platforms.Items.Clear();
        // Pre-check the recommended rows; when the preset flags none as recommended
        // (4DO, …), pre-check everything — an empty association list helps nobody.
        bool anyRecommended = _platformRows.Any(r => r.Recommended);
        foreach (var row in _platformRows)
        {
            string text = row.Platform;
            if (row.RequiredBiosFile.Length > 0) text += "   [BIOS: " + row.RequiredBiosFile + "]";
            if (anyRecommended && !row.Recommended) text += "   (not recommended)";
            _platforms.Items.Add(text, row.Recommended || !anyRecommended);
        }
    }

    private void BrowseExe()
    {
        using var dlg = new OpenFileDialog { Filter = "Executables (*.exe)|*.exe|All files (*.*)|*.*" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var sel = dlg.FileName;
        // Warn (don't block) when the chosen exe doesn't match the preset's expected binary.
        if (_preset.SelectedIndex > 0)
        {
            var expected = _hint.Text.Length > 0 ? EmuPresets.Load(_lbRoot)[_preset.SelectedIndex - 1].BinaryFileName : "";
            if (expected.Length > 0 && !string.Equals(Path.GetFileName(sel), expected, StringComparison.OrdinalIgnoreCase))
                MessageBox.Show(this, $"The preset expects \"{expected}\" — you selected \"{Path.GetFileName(sel)}\".\nKeeping your choice; double-check it is the right emulator.",
                    "Executable name mismatch", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        // Keep LB-relative when the exe sits under the LB root (LB convention).
        try
        {
            if (_lbRoot.Length > 0 && sel.StartsWith(_lbRoot, StringComparison.OrdinalIgnoreCase))
                sel = sel.Substring(_lbRoot.Length).TrimStart('\\', '/');
        }
        catch { }
        _path.Text = sel;
    }

    private bool CreateEmulator()
    {
        var name = _name.Text.Trim();
        if (name.Length == 0)
        {
            MessageBox.Show(this, "Please give the emulator a name.", "Add Emulator", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        IEmulator? e;
        try { e = PluginHelper.DataManager?.AddNewEmulator(); }
        catch (Exception ex) { Console.WriteLine("[addemu] AddNewEmulator failed: " + ex.Message); e = null; }
        if (e == null)
        {
            MessageBox.Show(this, "Could not create the emulator (data manager unavailable).", "Add Emulator", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        Set(() => e.Title = name);
        Set(() => e.ApplicationPath = _path.Text.Trim());
        Set(() => e.CommandLine = _cmd.Text.Trim());
        Set(() => e.NoQuotes = _noQuotes.Checked);
        Set(() => e.NoSpace = _noSpace.Checked);
        Set(() => e.HideConsole = _hideConsole.Checked);
        Set(() => e.FileNameWithoutExtensionAndPath = _fileNameOnly.Checked);
        Set(() => e.AutoExtract = _autoExtract.Checked);

        string? defaultPlatform = null;
        for (int i = 0; i < _platforms.Items.Count && i < _platformRows.Count; i++)
        {
            if (!_platforms.GetItemChecked(i)) continue;
            var row = _platformRows[i];
            defaultPlatform ??= row.Platform;
            Set(() =>
            {
                var ep = e.AddNewEmulatorPlatform();
                ep.Platform = row.Platform;
                ep.CommandLine = row.CommandLine;
            });
        }
        if (defaultPlatform != null) Set(() => e.DefaultPlatform = defaultPlatform);

        Created = e;
        return true;
    }

    private void Lbl(Control p, int x, int y, string text)
        => p.Controls.Add(new Label { Text = text, Location = new Point(x, y), AutoSize = true, ForeColor = LiteBoxTheme.Fg, BackColor = LiteBoxTheme.Bg });

    private TextBox Txt(Control p, Point loc, int width)
    {
        var tb = new TextBox
        {
            Location = loc, Width = width,
            BackColor = LiteBoxTheme.Panel2, ForeColor = LiteBoxTheme.Fg, BorderStyle = BorderStyle.FixedSingle,
        };
        p.Controls.Add(tb);
        return tb;
    }

    private CheckBox Chk(Control p, Point loc, string text)
    {
        var cb = new CheckBox { Text = text, Location = loc, AutoSize = true, ForeColor = LiteBoxTheme.Fg, BackColor = LiteBoxTheme.Bg };
        p.Controls.Add(cb);
        return cb;
    }

    private static void Set(Action a) { try { a(); } catch (Exception ex) { Console.WriteLine("[addemu] write failed: " + ex.Message); } }
}
