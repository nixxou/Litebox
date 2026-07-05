// Edit Game → the "Launching" branch — LB-parity pages, wired to the <Game> XML fields
// (RE'd against a real 13.28 library; the SDK-less ones go through ILiteBoxFields):
//   • Launching     ApplicationPath (+Browse), CommandLine, ConfigurationPath (+Browse),
//                   ConfigurationCommandLine. With an emulator assigned, everything but the ROM
//                   file is disabled (LB's "Some fields have been disabled…" note).
//   • DOSBox        UseDosBox (disabled while emulation is on), DosBoxConfigurationPath
//                   (+Browse/Create), CustomDosBoxVersionPath (+Browse).
//   • Mounts        the <Mount> child entities (Path / DriveLetter / Type / Filesystem) — grid
//                   with the always-empty last row; Add Folder… / Add Disk Image… / Remove.
//   • Emulation     "Use an emulator" ↔ the game's <Emulator> id + the emulator picker; the
//                   Add…/Edit…/Delete buttons reuse LiteBox's emulator windows. "Use Custom
//                   Command-line Parameters" edits the same <CommandLine> the Launching page
//                   shows (LB's dual-meaning field: app params without emulator, emulator
//                   override with one).
//   • Root Folder   RootFolder (+Browse), disabled while emulation is on.
//   • Startup/Pause OverrideDefaultStartupScreenSettings / OverrideDefaultPauseScreenSettings +
//                   the two Customize… dialogs. Startup: UseStartupScreen, !DisableShutdownScreen,
//                   HideMouseCursorInGame, AggressiveWindowHiding, HideAllNonExclusiveFullscreen-
//                   Windows, StartupScreenPostLaunchDisplayTime (ms; LB shows seconds — 2000 ⇒
//                   "2 Second(s)") and StartupLoadDelay (ms). Pause: UsePauseScreen,
//                   SuspendProcessOnPause, ForcefulPauseScreenActivation + the six per-game
//                   AutoHotkey scripts (Pause/Resume/Reset/SaveState/LoadState/SwapDiscs).
//
// This is the REPLICATE-AND-STORE pass: every control round-trips to the XML through the game's
// field chokepoint (typed cell for modelled fields, extra tier otherwise — one SetField API for
// both); nothing here changes LiteBox's launch behaviour yet. Writes happen on OK / navigation,
// and only for fields whose value actually changed. Single-game only (multi shows placeholders).

#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Data;
using LbApiHost.Host.Saves;

namespace LbApiHost.Host;

internal sealed partial class EditGameWindow
{
    // UI-bound field values pending a save (field → value); overlays the loaded snapshot.
    private readonly Dictionary<string, string> _lchPending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _lchLoaded = new(StringComparer.Ordinal);

    private Label? _lchAppCaption, _lchLaunchNote, _lchDosNote, _lchRootNote;
    private TextBox? _lchAppPath, _lchCmd, _lchCfgPath, _lchCfgCmd, _lchDosConf, _lchDosExe, _lchRoot, _lchCustomCmd;
    private Button? _lchCfgBrowse;
    private CheckBox? _lchUseDos, _lchUseEmu, _lchCustomCmdChk, _lchOvrStart, _lchOvrPause;
    private ComboBox? _lchEmuCombo;
    private List<(string id, string title)> _lchEmus = new();
    private DataGridView? _lchMountsGrid;
    private Button? _lchMountAdd, _lchMountImg, _lchMountDel;

    private static readonly string[] LchStartupFields =
    {
        "UseStartupScreen", "DisableShutdownScreen", "HideMouseCursorInGame", "AggressiveWindowHiding",
        "HideAllNonExclusiveFullscreenWindows", "StartupScreenPostLaunchDisplayTime", "StartupLoadDelay",
    };
    private static readonly string[] LchPauseFields =
    {
        "UsePauseScreen", "SuspendProcessOnPause", "ForcefulPauseScreenActivation",
        "PauseAutoHotkeyScript", "ResumeAutoHotkeyScript", "ResetAutoHotkeyScript",
        "SaveStateAutoHotkeyScript", "LoadStateAutoHotkeyScript", "SwapDiscsAutoHotkeyScript",
    };

    // ── Field IO (one chokepoint for modelled + extra fields) ─────────────

    private string LchGet(string field)
        => _lchPending.TryGetValue(field, out var v) ? v
         : _lchLoaded.TryGetValue(field, out var l) ? l
         : ((AppsGame as ILiteBoxFields)?.GetField(field) ?? "");

    private static bool LchBool(string v) => string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
    private static string LchB(bool v) => v ? "true" : "false";

    private bool _lchSnapped;

    /// <summary>Snapshot ONCE per game: a second launching page building must not clear the
    /// pending edits (e.g. a Customize… done before that page was first opened).</summary>
    private void LchSnapshotIfNeeded()
    {
        if (_lchSnapped) return;
        _lchSnapped = true;
        LchSnapshot();
    }

    private void LchSnapshot()
    {
        _lchLoaded.Clear();
        _lchPending.Clear();
        var g = AppsGame;
        // MODELLED fields must be read through the typed IGame props: HostGame.GetField only serves
        // the sparse EXTRA tier (+ two special cases), so it returns empty for them. Writes are fine
        // either way (SetField routes modelled names to the typed store).
        string Sv(Func<string?> f) { try { return f() ?? ""; } catch { return ""; } }
        string Bv(Func<bool> f) { try { return f() ? "true" : "false"; } catch { return "false"; } }
        _lchLoaded["ApplicationPath"] = Sv(() => g.ApplicationPath);
        _lchLoaded["CommandLine"] = Sv(() => g.CommandLine);
        _lchLoaded["ConfigurationPath"] = Sv(() => g.ConfigurationPath);
        _lchLoaded["ConfigurationCommandLine"] = Sv(() => g.ConfigurationCommandLine);
        _lchLoaded["DosBoxConfigurationPath"] = Sv(() => g.DosBoxConfigurationPath);
        _lchLoaded["RootFolder"] = Sv(() => g.RootFolder);
        _lchLoaded["Emulator"] = Sv(() => g.EmulatorId);
        _lchLoaded["UseDosBox"] = Bv(() => g.UseDosBox);
        _lchLoaded["UseStartupScreen"] = Bv(() => g.UseStartupScreen);
        _lchLoaded["OverrideDefaultStartupScreenSettings"] = Bv(() => g.OverrideDefaultStartupScreenSettings);
        _lchLoaded["DisableShutdownScreen"] = Bv(() => g.DisableShutdownScreen);
        _lchLoaded["HideMouseCursorInGame"] = Bv(() => g.HideMouseCursorInGame);
        _lchLoaded["AggressiveWindowHiding"] = Bv(() => g.AggressiveWindowHiding);
        _lchLoaded["HideAllNonExclusiveFullscreenWindows"] = Bv(() => g.HideAllNonExclusiveFullscreenWindows);
        try { _lchLoaded["StartupLoadDelay"] = g.StartupLoadDelay.ToString(CultureInfo.InvariantCulture); }
        catch { _lchLoaded["StartupLoadDelay"] = "0"; }
        // True extras (not on the SDK IGame) — the sparse tier serves them.
        var f2 = g as ILiteBoxFields;
        foreach (var name in new[]
        {
            "CustomDosBoxVersionPath", "StartupScreenPostLaunchDisplayTime",
            "OverrideDefaultPauseScreenSettings", "UsePauseScreen", "SuspendProcessOnPause",
            "ForcefulPauseScreenActivation", "PauseAutoHotkeyScript", "ResumeAutoHotkeyScript",
            "ResetAutoHotkeyScript", "SaveStateAutoHotkeyScript", "LoadStateAutoHotkeyScript",
            "SwapDiscsAutoHotkeyScript",
        })
            try { _lchLoaded[name] = f2?.GetField(name) ?? ""; } catch { _lchLoaded[name] = ""; }
    }

    private bool LchEmuOn => (_lchUseEmu?.Checked ?? (LchGet("Emulator") is { Length: > 0 } e && e != Guid.Empty.ToString()));

    // ── Shared row helpers (caption above a full-width field, LB layout) ──

    private Label LchCap(Panel p, string text, ref int y)
    {
        var l = new Label { Text = text, AutoSize = true, Location = new Point(S(14), y), ForeColor = Fg, BackColor = Bg };
        p.Controls.Add(l);
        y += S(20);
        return l;
    }

    private TextBox LchTxt(Panel p, string value, ref int y, out Button? browseBtn, bool browse = false, Action<TextBox>? onBrowse = null)
    {
        int w = S(700);
        var t = new TextBox
        {
            Text = value, Location = new Point(S(14), y), Width = browse ? w - S(90) : w,
            BackColor = Field, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle,
        };
        p.Controls.Add(t);
        browseBtn = null;
        if (browse)
        {
            var b = DlgBtn("Browse…", Color.FromArgb(60, 60, 72));
            b.Location = new Point(t.Right + S(8), y - S(2));
            b.Enabled = !_readOnly;
            b.Click += (_, _) => onBrowse?.Invoke(t);
            p.Controls.Add(b);
            browseBtn = b;
        }
        y += S(34);
        return t;
    }

    private Label LchHelp(Panel p, string text, ref int y, int lines = 2)
    {
        var l = new Label
        {
            Text = text, Location = new Point(S(14), y), Size = new Size(S(700), S(16 * lines)),
            ForeColor = SubFg, BackColor = Bg, AutoSize = false,
        };
        p.Controls.Add(l);
        y += S(16 * lines + 10);
        return l;
    }

    private void LchBrowseFile(TextBox target, string title)
    {
        using var dlg = new OpenFileDialog { Title = title, CheckFileExists = false };
        try
        {
            string cur = target.Text.Trim();
            string abs = cur.Length > 0 ? (Path.IsPathRooted(cur) ? cur : Path.Combine(SaveManager.LbRoot, cur)) : SaveManager.LbRoot;
            var dir = cur.Length > 0 ? Path.GetDirectoryName(abs) : abs;
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) dlg.InitialDirectory = dir;
        }
        catch { }
        if (dlg.ShowDialog(this) == DialogResult.OK) target.Text = dlg.FileName;
    }

    private void LchBrowseFolder(TextBox target, string title)
    {
        using var dlg = new FolderBrowserDialog { Description = title, UseDescriptionForTitle = true };
        try
        {
            string cur = target.Text.Trim();
            if (cur.Length > 0) dlg.SelectedPath = Path.IsPathRooted(cur) ? cur : Path.Combine(SaveManager.LbRoot, cur);
        }
        catch { }
        if (dlg.ShowDialog(this) == DialogResult.OK) target.Text = dlg.SelectedPath;
    }

    // ── Page: Launching ────────────────────────────────────────────────────

    private Control BuildLaunchingPage()
    {
        LchSnapshotIfNeeded();
        var p = new Panel { BackColor = Bg, Padding = new Padding(S(6)) };
        int y = S(14);

        _lchAppCaption = LchCap(p, "Application Path:", ref y);
        _lchAppPath = LchTxt(p, LchGet("ApplicationPath"), ref y, out _, browse: true, onBrowse: t => LchBrowseFile(t, "Select the application / ROM file"));

        LchCap(p, "Application Command-Line Parameters:", ref y);
        _lchCmd = LchTxt(p, LchGet("CommandLine"), ref y, out _);
        _lchCmd.TextChanged += (_, _) => { if (_lchCustomCmd != null && !_lchCustomCmd.Focused) _lchCustomCmd.Text = _lchCmd.Text; };

        LchCap(p, "Configuration Application Path:", ref y);
        _lchCfgPath = LchTxt(p, LchGet("ConfigurationPath"), ref y, out _lchCfgBrowse, browse: true, onBrowse: t => LchBrowseFile(t, "Select the configuration application"));

        LchCap(p, "Configuration Command-Line Parameters:", ref y);
        _lchCfgCmd = LchTxt(p, LchGet("ConfigurationCommandLine"), ref y, out _);

        _lchLaunchNote = new Label
        {
            Dock = DockStyle.Bottom, Height = S(26), ForeColor = SubFg, BackColor = Bg,
            Font = new Font("Segoe UI", 9f, FontStyle.Italic),
            Text = "Some fields have been disabled because Emulation is active.",
        };
        p.Controls.Add(_lchLaunchNote);
        UpdateLaunchingEnablement();
        return p;
    }

    // ── Page: DOSBox ───────────────────────────────────────────────────────

    private Control BuildDosBoxPage()
    {
        LchSnapshotIfNeeded();
        var p = new Panel { BackColor = Bg, Padding = new Padding(S(6)) };
        int y = S(14);

        _lchUseDos = new CheckBox
        {
            Text = "Use DOSBox to play this game (only for old MS-DOS games)", AutoSize = true,
            Location = new Point(S(14), y), ForeColor = Fg, BackColor = Bg,
            Checked = LchBool(LchGet("UseDosBox")),
        };
        p.Controls.Add(_lchUseDos);
        _lchUseDos.CheckedChanged += (_, _) => UpdateLaunchingEnablement();
        y += S(32);

        LchCap(p, "Custom DOSBox Configuration File (dosbox.conf):", ref y);
        _lchDosConf = LchTxt(p, LchGet("DosBoxConfigurationPath"), ref y, out var confBrowse, browse: true,
            onBrowse: t => LchBrowseFile(t, "Select a dosbox.conf file"));
        var create = DlgBtn("Create…", Color.FromArgb(60, 60, 72));
        if (confBrowse != null)
        {
            _lchDosConf.Width -= S(84);
            confBrowse.Left -= S(84);
            create.Location = new Point(confBrowse.Right + S(8), confBrowse.Top);
            create.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            create.Enabled = !_readOnly;
            create.Click += (_, _) => LchCreateDosBoxConf();
            p.Controls.Add(create);
        }
        LchHelp(p,
            "Leave blank for the default DOSBox configuration. Click the Browse button to browse for and select an "
            + "existing dosbox.conf file. Click the Create button to create a new dosbox.conf file and base it off of "
            + "the default DOSBox configuration.", ref y, 3);

        LchCap(p, "Custom DOSBox Version EXE Path:", ref y);
        _lchDosExe = LchTxt(p, LchGet("CustomDosBoxVersionPath"), ref y, out _, browse: true,
            onBrowse: t => LchBrowseFile(t, "Select a DOSBox executable"));
        LchHelp(p,
            "Leave blank for the default version of DOSBox that comes with LaunchBox. Click the Browse button to "
            + "browse for and select a custom version/copy of DOSBox.", ref y, 2);

        _lchDosNote = new Label
        {
            Dock = DockStyle.Bottom, Height = S(26), ForeColor = SubFg, BackColor = Bg,
            Font = new Font("Segoe UI", 9f, FontStyle.Italic),
            Text = "DOSBox cannot be enabled while Emulation is active.",
        };
        p.Controls.Add(_lchDosNote);
        UpdateLaunchingEnablement();
        return p;
    }

    private void LchCreateDosBoxConf()
    {
        if (_lchDosConf == null) return;
        using var dlg = new SaveFileDialog
        {
            Title = "Create a dosbox.conf", FileName = "dosbox.conf",
            Filter = "DOSBox configuration (*.conf)|*.conf|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            // Base the new file off LaunchBox's default DOSBox configuration when present.
            string def = Path.Combine(SaveManager.LbRoot, "DOSBox", "dosbox.conf");
            if (File.Exists(def)) File.Copy(def, dlg.FileName, overwrite: true);
            else File.WriteAllText(dlg.FileName, "");
            _lchDosConf.Text = dlg.FileName;
        }
        catch (Exception ex) { MessageBox.Show(this, "Could not create the file: " + ex.Message, "DOSBox", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    // ── Page: Mounts ───────────────────────────────────────────────────────

    private Control BuildMountsPage()
    {
        var p = new Panel { BackColor = Bg, Padding = new Padding(S(6)) };
        var blurb = new Label
        {
            Dock = DockStyle.Top, Height = S(34), BackColor = Bg, ForeColor = SubFg,
            Padding = new Padding(S(2), S(4), S(2), 0),
            Text = "The application path folder will be automatically mounted.  You can mount additional folders or "
                 + "disk image files as needed in order to access them from inside DOSBox.",
        };

        var grid = NewDarkGrid();
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Path", FillWeight = 440, SortMode = DataGridViewColumnSortMode.NotSortable });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Drive Letter", FillWeight = 120, SortMode = DataGridViewColumnSortMode.NotSortable });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Type", FillWeight = 120, SortMode = DataGridViewColumnSortMode.NotSortable });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Filesystem", FillWeight = 130, SortMode = DataGridViewColumnSortMode.NotSortable });

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = S(66), BackColor = Bg, Padding = new Padding(0, S(6), 0, 0) };
        var addFolder = _lchMountAdd = FooterBtn("Add Folder…", Color.FromArgb(60, 60, 72));
        var addImage = _lchMountImg = FooterBtn("Add Disk Image…", Color.FromArgb(60, 60, 72));
        var remove = _lchMountDel = FooterBtn("Remove", Color.FromArgb(60, 60, 72));
        addFolder.AutoSize = addImage.AutoSize = remove.AutoSize = false;
        addFolder.Enabled = addImage.Enabled = remove.Enabled = !_readOnly;
        addFolder.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog { Description = "Select a folder to mount", UseDescriptionForTitle = true };
            if (dlg.ShowDialog(this) == DialogResult.OK) grid.Rows.Add(dlg.SelectedPath, NextMountLetter(grid), "", "");
        };
        addImage.Click += (_, _) =>
        {
            using var dlg = new OpenFileDialog
            {
                Title = "Select a disk image to mount",
                Filter = "Disk images (*.iso;*.img;*.cue;*.bin;*.ima;*.vhd)|*.iso;*.img;*.cue;*.bin;*.ima;*.vhd|All files (*.*)|*.*",
            };
            if (dlg.ShowDialog(this) == DialogResult.OK) grid.Rows.Add(dlg.FileName, NextMountLetter(grid), "", "");
        };
        remove.Click += (_, _) => { var r = grid.CurrentRow; if (r != null && !r.IsNewRow) grid.Rows.Remove(r); };
        var note = new Label
        {
            AutoSize = true, ForeColor = SubFg, BackColor = Bg, Font = new Font("Segoe UI", 9f, FontStyle.Italic),
            Text = "Mounts are used only for DOSBox games.",
        };
        bottom.Controls.AddRange(new Control[] { addFolder, addImage, remove, note });
        bottom.Resize += (_, _) =>
        {
            addFolder.SetBounds(0, S(4), S(140), S(28));
            addImage.SetBounds(S(146), S(4), S(160), S(28));
            remove.SetBounds(S(312), S(4), S(110), S(28));
            note.Location = new Point(S(2), S(40));
        };

        p.Controls.Add(grid);
        p.Controls.Add(blurb);
        p.Controls.Add(bottom);
        grid.BringToFront();
        _lchMountsGrid = grid;
        LoadMounts();
        UpdateLaunchingEnablement();
        return p;
    }

    private static string NextMountLetter(DataGridView grid)
    {
        var used = new HashSet<char>(StringComparer.OrdinalIgnoreCase.Equals("", "") ? new List<char>() : new List<char>());
        foreach (DataGridViewRow r in grid.Rows)
            if (!r.IsNewRow && (r.Cells[1].Value as string)?.Trim() is { Length: > 0 } d) used.Add(char.ToUpperInvariant(d[0]));
        for (char c = 'D'; c <= 'Z'; c++) if (!used.Contains(c) && c != 'C') return c.ToString();
        return "D";
    }

    private void LoadMounts()
    {
        if (_lchMountsGrid == null || IsMulti) return;
        _lchMountsGrid.Rows.Clear();
        try
        {
            foreach (var m in AppsGame.GetAllMounts() ?? Array.Empty<IMount>())
                if (m != null)
                    _lchMountsGrid.Rows.Add(Safe(() => m.Path) ?? "", Safe(() => m.DriveLetter.ToString()) ?? "",
                                            Safe(() => m.Type) ?? "", Safe(() => m.Filesystem) ?? "");
        }
        catch { }
    }

    private void SaveMounts()
    {
        if (_readOnly || _lchMountsGrid == null || IsMulti) return;
        try { _lchMountsGrid.EndEdit(); } catch { }
        var intended = new List<(string path, char letter, string type, string fs)>();
        foreach (DataGridViewRow r in _lchMountsGrid.Rows)
        {
            if (r.IsNewRow) continue;
            string path = (r.Cells[0].Value as string ?? "").Trim();
            if (path.Length == 0) continue;                                   // empty row → dropped
            string letter = (r.Cells[1].Value as string ?? "").Trim();
            intended.Add((path, letter.Length > 0 ? char.ToUpperInvariant(letter[0]) : 'D',
                          (r.Cells[2].Value as string ?? "").Trim(), (r.Cells[3].Value as string ?? "").Trim()));
        }
        var g = AppsGame;
        IMount[] current;
        try { current = g.GetAllMounts() ?? Array.Empty<IMount>(); } catch { current = Array.Empty<IMount>(); }
        bool same = current.Length == intended.Count && current.Zip(intended).All(z =>
            string.Equals(Safe(() => z.First.Path) ?? "", z.Second.path, StringComparison.Ordinal)
            && Safe(() => z.First.DriveLetter) == z.Second.letter
            && string.Equals(Safe(() => z.First.Type) ?? "", z.Second.type, StringComparison.Ordinal)
            && string.Equals(Safe(() => z.First.Filesystem) ?? "", z.Second.fs, StringComparison.Ordinal));
        if (same) return;
        try
        {
            foreach (var m in current) g.TryRemoveMount(m);
            foreach (var (path, letter, type, fs) in intended)
            {
                var m = g.AddNewMount();
                if (m == null) continue;
                m.Path = path;
                m.DriveLetter = letter;
                m.Type = type;
                m.Filesystem = fs;
            }
        }
        catch (Exception ex) { Console.WriteLine("[mounts] save failed: " + ex.Message); }
    }

    // ── Page: Emulation ────────────────────────────────────────────────────

    private Control BuildEmulationPage()
    {
        LchSnapshotIfNeeded();
        var p = new Panel { BackColor = Bg, Padding = new Padding(S(6)) };
        int y = S(14);

        string curEmu = LchGet("Emulator");
        bool emuOn = curEmu.Length > 0 && curEmu != Guid.Empty.ToString();
        _lchUseEmu = new CheckBox
        {
            Text = "Use an emulator to play this game (primarily for console games)", AutoSize = true,
            Location = new Point(S(14), y), ForeColor = Fg, BackColor = Bg, Checked = emuOn, Enabled = !_readOnly,
        };
        p.Controls.Add(_lchUseEmu);
        y += S(30);

        LchCap(p, "Choose an emulator:", ref y);
        _lchEmuCombo = new ComboBox
        {
            Location = new Point(S(14), y), Width = S(700), DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = Field, ForeColor = Fg, FlatStyle = FlatStyle.Flat,
        };
        p.Controls.Add(_lchEmuCombo);
        RefreshEmulatorCombo(curEmu);
        y += S(32);

        var add = DlgBtn("Add…", Color.FromArgb(60, 60, 72));
        var edit = DlgBtn("Edit…", Color.FromArgb(60, 60, 72));
        var del = DlgBtn("Delete", Color.FromArgb(60, 60, 72));
        add.Location = new Point(S(14), y);
        edit.Location = new Point(S(90), y);
        del.Location = new Point(S(166), y);
        add.Enabled = edit.Enabled = del.Enabled = !_readOnly;
        add.Click += (_, _) =>
        {
            using var w = new Emulators.AddEmulatorWindow(SaveManager.LbRoot);
            w.ShowDialog(this);
            RefreshEmulatorCombo(SelectedEmulatorId());
        };
        edit.Click += (_, _) =>
        {
            var emu = SelectedEmulator();
            if (emu == null) return;
            Emulators.EditEmulatorWindow.Open(emu, _readOnly, this, SaveManager.LbRoot);
            RefreshEmulatorCombo(SelectedEmulatorId());
        };
        del.Click += (_, _) =>
        {
            var emu = SelectedEmulator();
            if (emu == null) return;
            if (MessageBox.Show(this, $"Delete the emulator \"{Safe(() => emu.Title)}\"?", "Delete Emulator",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            try { PluginHelper.DataManager?.TryRemoveEmulator(emu); } catch { }
            RefreshEmulatorCombo("");
        };
        p.Controls.AddRange(new Control[] { add, edit, del });
        y += S(40);

        _lchCustomCmdChk = new CheckBox
        {
            Text = "Use Custom Command-line Parameters:", AutoSize = true,
            Location = new Point(S(14), y), ForeColor = Fg, BackColor = Bg,
            Checked = LchGet("CommandLine").Trim().Length > 0, Enabled = !_readOnly,
        };
        p.Controls.Add(_lchCustomCmdChk);
        y += S(26);
        _lchCustomCmd = LchTxt(p, LchGet("CommandLine"), ref y, out _);
        _lchCustomCmd.TextChanged += (_, _) => { if (_lchCmd != null && !_lchCmd.Focused) _lchCmd.Text = _lchCustomCmd.Text; };
        _lchCustomCmdChk.CheckedChanged += (_, _) => UpdateLaunchingEnablement();

        _lchUseEmu.CheckedChanged += (_, _) => UpdateLaunchingEnablement();
        UpdateLaunchingEnablement();
        return p;
    }

    private void RefreshEmulatorCombo(string selectId)
    {
        if (_lchEmuCombo == null) return;
        _lchEmus = new List<(string, string)>();
        try
        {
            foreach (var e in PluginHelper.DataManager?.GetAllEmulators() ?? Array.Empty<IEmulator>())
            {
                if (e == null) continue;
                string id = Safe(() => e.Id) ?? "";
                if (id.Length == 0 || id == Guid.Empty.ToString()) continue;
                _lchEmus.Add((id, Safe(() => e.Title) ?? id));
            }
        }
        catch { }
        _lchEmus.Sort((a, b) => string.Compare(a.title, b.title, StringComparison.OrdinalIgnoreCase));
        _lchEmuCombo.Items.Clear();
        foreach (var e in _lchEmus) _lchEmuCombo.Items.Add(e.title);
        int ix = _lchEmus.FindIndex(e => string.Equals(e.id, selectId, StringComparison.OrdinalIgnoreCase));
        if (ix >= 0) _lchEmuCombo.SelectedIndex = ix;
        else if (_lchEmuCombo.Items.Count > 0) _lchEmuCombo.SelectedIndex = 0;
    }

    private string SelectedEmulatorId()
        => _lchEmuCombo != null && _lchEmuCombo.SelectedIndex >= 0 && _lchEmuCombo.SelectedIndex < _lchEmus.Count
            ? _lchEmus[_lchEmuCombo.SelectedIndex].id : "";

    private IEmulator? SelectedEmulator()
    {
        string id = SelectedEmulatorId();
        if (id.Length == 0) return null;
        try { return PluginHelper.DataManager?.GetEmulatorById(id); } catch { return null; }
    }

    // ── Page: Root Folder ──────────────────────────────────────────────────

    private Control BuildRootFolderPage()
    {
        LchSnapshotIfNeeded();
        var p = new Panel { BackColor = Bg, Padding = new Padding(S(6)) };
        int y = S(14);
        LchCap(p, "Root Folder:", ref y);
        _lchRoot = LchTxt(p, LchGet("RootFolder"), ref y, out _, browse: true, onBrowse: t => LchBrowseFolder(t, "Select the root folder"));
        LchHelp(p,
            "The root folder is used when mounting the C drive in DOSBox and when importing and exporting games. "
            + "It is automatically populated, but can be changed.", ref y, 2);
        _lchRootNote = new Label
        {
            Dock = DockStyle.Bottom, Height = S(26), ForeColor = SubFg, BackColor = Bg,
            Font = new Font("Segoe UI", 9f, FontStyle.Italic),
            Text = "Root Folder has been disabled because Emulation is active.",
        };
        p.Controls.Add(_lchRootNote);
        UpdateLaunchingEnablement();
        return p;
    }

    // ── Page: Startup/Pause ────────────────────────────────────────────────

    private Control BuildStartupPausePage()
    {
        LchSnapshotIfNeeded();
        var p = new Panel { BackColor = Bg, Padding = new Padding(S(6)) };
        int y = S(16);

        _lchOvrStart = new CheckBox
        {
            Text = "Override Default Startup Screen Settings", AutoSize = true,
            Location = new Point(S(14), y), ForeColor = Fg, BackColor = Bg,
            Checked = LchBool(LchGet("OverrideDefaultStartupScreenSettings")), Enabled = !_readOnly,
        };
        p.Controls.Add(_lchOvrStart);
        y += S(26);
        var custStart = DlgBtn("Customize…", Color.FromArgb(60, 60, 72));
        custStart.Location = new Point(S(14), y);
        custStart.Click += (_, _) => ShowStartupCustomizeDialog();
        p.Controls.Add(custStart);
        y += S(46);

        _lchOvrPause = new CheckBox
        {
            Text = "Override Default Pause Screen Settings", AutoSize = true,
            Location = new Point(S(14), y), ForeColor = Fg, BackColor = Bg,
            Checked = LchBool(LchGet("OverrideDefaultPauseScreenSettings")), Enabled = !_readOnly,
        };
        p.Controls.Add(_lchOvrPause);
        y += S(26);
        var custPause = DlgBtn("Customize…", Color.FromArgb(60, 60, 72));
        custPause.Location = new Point(S(14), y);
        custPause.Click += (_, _) => ShowPauseCustomizeDialog();
        p.Controls.Add(custPause);

        // Customize… is only reachable while its Override checkbox is checked (LB behaviour).
        void SyncCust()
        {
            custStart.Enabled = !_readOnly && _lchOvrStart.Checked;
            custPause.Enabled = !_readOnly && _lchOvrPause.Checked;
        }
        _lchOvrStart.CheckedChanged += (_, _) => SyncCust();
        _lchOvrPause.CheckedChanged += (_, _) => SyncCust();
        SyncCust();
        return p;
    }

    /// <summary>LB's "Override Default Startup Screen Settings" dialog — edits the pending values
    /// (written on the page save). Times are STORED in milliseconds, shown in seconds like LB.</summary>
    private void ShowStartupCustomizeDialog()
    {
        using var f = NewDialog("Override Default Startup Screen Settings", 700, 430);
        int x = S(16), y = S(14);
        CheckBox Chk(string text, string field, bool invert, int cx, int cy)
        {
            bool v = LchBool(LchGet(field));
            var cb = new CheckBox
            {
                Text = text, AutoSize = true, Location = new Point(cx, cy), ForeColor = Fg, BackColor = Bg,
                Checked = invert ? !v : v, Enabled = !_readOnly,
            };
            f.Controls.Add(cb);
            return cb;
        }
        var enStart = Chk("Enable Game Startup Screen", "UseStartupScreen", false, x, y);
        var enShut = Chk("Enable Game Shutdown Screen", "DisableShutdownScreen", true, S(360), y);
        y += S(26);
        var hideMouse = Chk("Hide Mouse Cursor During Game", "HideMouseCursorInGame", false, x, y);
        var aggressive = Chk("Aggressive Startup Window Hiding", "AggressiveWindowHiding", false, S(360), y);
        y += S(34);

        (TrackBar bar, Label cap) Slider(string caption, string field, int max, int step, Func<int, string> fmt, ref int sy)
        {
            int val = int.TryParse(LchGet(field), out var v) ? Math.Max(0, Math.Min(max, v)) : 0;
            var cap = new Label { AutoSize = true, Location = new Point(x, sy), ForeColor = Fg, BackColor = Bg, Text = caption + fmt(val) };
            f.Controls.Add(cap);
            sy += S(22);
            var bar = new TrackBar
            {
                Location = new Point(x, sy), Width = S(640), Minimum = 0, Maximum = max, SmallChange = step,
                LargeChange = step, TickFrequency = max / 20, Value = val, Enabled = !_readOnly,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, BackColor = Bg,
            };
            bar.ValueChanged += (_, _) => cap.Text = caption + fmt(bar.Value);
            f.Controls.Add(bar);
            sy += S(48);
            return (bar, cap);
        }

        var (postBar, _) = Slider("Post-Launch Display Time: ", "StartupScreenPostLaunchDisplayTime",
            10000, 250, ms => $"{ms / 1000.0:0.###} Second(s)", ref y);

        var hideAll = Chk("Hide All Windows that are not in Exclusive Fullscreen Mode", "HideAllNonExclusiveFullscreenWindows", false, x, y);
        y += S(24);
        var help1 = new Label
        {
            Location = new Point(x, y), Size = new Size(S(640), S(48)), ForeColor = SubFg, BackColor = Bg,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            Text = "This option may make for a cleaner startup sequence for some emulators that use exclusive fullscreen "
                 + "mode. If you're seeing a black screen and the game never shows up after loading, you'll need to uncheck "
                 + "this box. If you're setting up a new or unknown emulator, it's worth trying to check this box to see if "
                 + "it makes the startup experience smoother without causing issues.",
        };
        f.Controls.Add(help1);
        y += S(56);

        var (delayBar, _) = Slider("Startup Load Delay: ", "StartupLoadDelay",
            60000, 250, ms => $"{ms / 1000.0:0.000} second(s)", ref y);
        var help2 = new Label
        {
            Location = new Point(x, y), Size = new Size(S(640), S(34)), ForeColor = SubFg, BackColor = Bg,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            Text = "The startup load delay generally defines how long the emulator takes to load before the game is playing. "
                 + "This is ultimately how long LaunchBox will wait after launching the emulator's EXE file before showing the game, if possible.",
        };
        f.Controls.Add(help2);

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = S(46), BackColor = Bg };
        var ok = DlgBtn("✔ OK", Color.FromArgb(50, 110, 65));
        var cancel = DlgBtn("✘ Cancel", Color.FromArgb(70, 70, 82));
        ok.Enabled = !_readOnly;
        cancel.DialogResult = DialogResult.Cancel;
        ok.Location = new Point(S(16), S(8));
        cancel.Location = new Point(S(100), S(8));
        bottom.Controls.AddRange(new Control[] { ok, cancel });
        f.Controls.Add(bottom);
        f.AcceptButton = ok; f.CancelButton = cancel;
        ok.Click += (_, _) =>
        {
            _lchPending["UseStartupScreen"] = LchB(enStart.Checked);
            _lchPending["DisableShutdownScreen"] = LchB(!enShut.Checked);
            _lchPending["HideMouseCursorInGame"] = LchB(hideMouse.Checked);
            _lchPending["AggressiveWindowHiding"] = LchB(aggressive.Checked);
            _lchPending["HideAllNonExclusiveFullscreenWindows"] = LchB(hideAll.Checked);
            _lchPending["StartupScreenPostLaunchDisplayTime"] = postBar.Value.ToString(CultureInfo.InvariantCulture);
            _lchPending["StartupLoadDelay"] = delayBar.Value.ToString(CultureInfo.InvariantCulture);
            f.DialogResult = DialogResult.OK; f.Close();
        };
        cancel.Click += (_, _) => { f.DialogResult = DialogResult.Cancel; f.Close(); };
        f.ShowDialog(this);
    }

    /// <summary>LB's "Override Default Pause Screen Settings" dialog — three switches + the six
    /// per-game AutoHotkey scripts, tabbed like LB.</summary>
    private void ShowPauseCustomizeDialog()
    {
        using var f = NewDialog("Override Default Pause Screen Settings", 700, 470);
        int x = S(16), y = S(14);
        CheckBox Chk(string text, string field, int cx, int cy)
        {
            var cb = new CheckBox
            {
                Text = text, AutoSize = true, Location = new Point(cx, cy), ForeColor = Fg, BackColor = Bg,
                Checked = LchBool(LchGet(field)), Enabled = !_readOnly,
            };
            f.Controls.Add(cb);
            return cb;
        }
        var enPause = Chk("Enable Game Pause Screen", "UsePauseScreen", x, y);
        var suspend = Chk("Suspend Emulator Process While Paused", "SuspendProcessOnPause", S(360), y);
        y += S(26);
        var forceful = Chk("Forceful Pause Screen Activation (enable this if the pause screen is not showing)", "ForcefulPauseScreenActivation", x, y);
        y += S(30);

        f.Controls.Add(new Label { Text = "AutoHotkey Scripts:", AutoSize = true, Location = new Point(x, y), ForeColor = Fg, BackColor = Bg });
        y += S(22);

        var tabs = new TabControl
        {
            Location = new Point(x, y), Size = new Size(S(650), S(250)),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            DrawMode = TabDrawMode.OwnerDrawFixed, SizeMode = TabSizeMode.Fixed, ItemSize = new Size(S(92), S(24)),
        };
        tabs.DrawItem += (_, e) =>
        {
            bool sel = e.Index == tabs.SelectedIndex;
            using var b = new SolidBrush(sel ? Field : PanelC);
            e.Graphics.FillRectangle(b, e.Bounds);
            TextRenderer.DrawText(e.Graphics, tabs.TabPages[e.Index].Text, f.Font, e.Bounds,
                sel ? Color.White : SubFg, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        };
        var scriptTabs = new (string title, string field)[]
        {
            ("On Pause", "PauseAutoHotkeyScript"), ("On Resume", "ResumeAutoHotkeyScript"),
            ("Reset Game", "ResetAutoHotkeyScript"), ("Save State", "SaveStateAutoHotkeyScript"),
            ("Load State", "LoadStateAutoHotkeyScript"), ("Swap Discs", "SwapDiscsAutoHotkeyScript"),
        };
        var editors = new Dictionary<string, TextBox>(StringComparer.Ordinal);
        foreach (var (title, field) in scriptTabs)
        {
            var page = new TabPage(title) { BackColor = Bg, UseVisualStyleBackColor = false };
            var tb = new TextBox
            {
                Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true,
                BackColor = PanelC, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Consolas", 9.5f), ReadOnly = _readOnly,
                Text = LchGet(field).Replace("\r\n", "\n").Replace("\n", "\r\n"),
            };
            page.Controls.Add(tb);
            tabs.TabPages.Add(page);
            editors[field] = tb;
        }
        f.Controls.Add(tabs);

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = S(46), BackColor = Bg };
        var ok = DlgBtn("✔ OK", Color.FromArgb(50, 110, 65));
        var cancel = DlgBtn("✘ Cancel", Color.FromArgb(70, 70, 82));
        ok.Enabled = !_readOnly;
        cancel.DialogResult = DialogResult.Cancel;
        ok.Location = new Point(S(16), S(8));
        cancel.Location = new Point(S(100), S(8));
        bottom.Controls.AddRange(new Control[] { ok, cancel });
        f.Controls.Add(bottom);
        f.AcceptButton = ok; f.CancelButton = cancel;
        ok.Click += (_, _) =>
        {
            _lchPending["UsePauseScreen"] = LchB(enPause.Checked);
            _lchPending["SuspendProcessOnPause"] = LchB(suspend.Checked);
            _lchPending["ForcefulPauseScreenActivation"] = LchB(forceful.Checked);
            foreach (var (_, field) in scriptTabs)
                _lchPending[field] = editors[field].Text.Replace("\r\n", "\n");
            f.DialogResult = DialogResult.OK; f.Close();
        };
        cancel.Click += (_, _) => { f.DialogResult = DialogResult.Cancel; f.Close(); };
        f.ShowDialog(this);
    }

    // ── Enablement (LB's emulation-driven greying, live across the pages) ──
    // A truly DISABLED TextBox/CheckBox paints near-black text on the dark theme (unreadable).
    // "Disabled" text boxes therefore go READ-ONLY with grey text on a darker back, and check
    // boxes keep painting normally but stop accepting clicks (AutoCheck off) — same signal,
    // readable text.

    private static void LchEnable(TextBox? t, bool on)
    {
        if (t == null) return;
        t.ReadOnly = !on;
        t.ForeColor = on ? Fg : SubFg;
        t.BackColor = on ? Field : PanelC;
        t.TabStop = on;
    }

    private static void LchEnable(CheckBox? c, bool on)
    {
        if (c == null) return;
        c.AutoCheck = on;
        c.ForeColor = on ? Fg : SubFg;
        c.TabStop = on;
    }

    private void UpdateLaunchingEnablement()
    {
        bool emuOn = LchEmuOn;
        bool w = !_readOnly;
        if (_lchAppCaption != null) _lchAppCaption.Text = emuOn ? "ROM File (Emulation is enabled):" : "Application Path:";
        LchEnable(_lchAppPath, w);
        LchEnable(_lchCmd, w && !emuOn);
        LchEnable(_lchCfgPath, w && !emuOn);
        if (_lchCfgBrowse != null) _lchCfgBrowse.Enabled = w && !emuOn;
        LchEnable(_lchCfgCmd, w && !emuOn);
        if (_lchLaunchNote != null) _lchLaunchNote.Visible = emuOn;

        LchEnable(_lchUseDos, w && !emuOn);
        if (_lchDosNote != null) _lchDosNote.Visible = emuOn;

        LchEnable(_lchRoot, w && !emuOn);
        if (_lchRootNote != null) _lchRootNote.Visible = emuOn;

        bool useEmu = _lchUseEmu?.Checked ?? emuOn;
        if (_lchEmuCombo != null) _lchEmuCombo.Enabled = w && useEmu;
        LchEnable(_lchCustomCmdChk, w && useEmu);
        LchEnable(_lchCustomCmd, w && useEmu && (_lchCustomCmdChk?.Checked ?? false));

        // Mounts only make sense for a DOSBox game (LB's note says as much) — lock the whole
        // surface unless DOSBox is on (which also implies emulation is off).
        bool dosOn = !emuOn && (_lchUseDos?.Checked ?? LchBool(LchGet("UseDosBox")));
        if (_lchMountsGrid != null)
        {
            _lchMountsGrid.ReadOnly = _readOnly || !dosOn;
            _lchMountsGrid.AllowUserToAddRows = w && dosOn;
            _lchMountsGrid.AllowUserToDeleteRows = w && dosOn;
            _lchMountsGrid.DefaultCellStyle.ForeColor = dosOn ? Fg : SubFg;
        }
        if (_lchMountAdd != null) _lchMountAdd.Enabled = w && dosOn;
        if (_lchMountImg != null) _lchMountImg.Enabled = w && dosOn;
        if (_lchMountDel != null) _lchMountDel.Enabled = w && dosOn;
    }

    // ── Save / reload ──────────────────────────────────────────────────────

    private void SaveLaunching()
    {
        if (_readOnly || IsMulti) return;
        var f = AppsGame as ILiteBoxFields;
        if (f == null) return;

        // UI-bound fields → pending (only pages that were actually built contribute).
        if (_lchAppPath != null) _lchPending["ApplicationPath"] = _lchAppPath.Text.Trim();
        if (_lchCfgPath != null) _lchPending["ConfigurationPath"] = _lchCfgPath.Text.Trim();
        if (_lchCfgCmd != null) _lchPending["ConfigurationCommandLine"] = _lchCfgCmd.Text.Trim();
        if (_lchUseDos != null) _lchPending["UseDosBox"] = LchB(_lchUseDos.Checked);
        if (_lchDosConf != null) _lchPending["DosBoxConfigurationPath"] = _lchDosConf.Text.Trim();
        if (_lchDosExe != null) _lchPending["CustomDosBoxVersionPath"] = _lchDosExe.Text.Trim();
        if (_lchRoot != null) _lchPending["RootFolder"] = _lchRoot.Text.Trim();
        if (_lchOvrStart != null) _lchPending["OverrideDefaultStartupScreenSettings"] = LchB(_lchOvrStart.Checked);
        if (_lchOvrPause != null) _lchPending["OverrideDefaultPauseScreenSettings"] = LchB(_lchOvrPause.Checked);

        // The emulator assignment + the dual-meaning CommandLine (see the header).
        if (_lchUseEmu != null)
            _lchPending["Emulator"] = _lchUseEmu.Checked ? SelectedEmulatorId() : "";
        bool emuOn = _lchUseEmu?.Checked ?? LchEmuOn;
        if (emuOn && _lchCustomCmdChk != null && _lchCustomCmd != null)
            _lchPending["CommandLine"] = _lchCustomCmdChk.Checked ? _lchCustomCmd.Text.Trim() : "";
        else if (!emuOn && _lchCmd != null)
            _lchPending["CommandLine"] = _lchCmd.Text.Trim();

        foreach (var kv in _lchPending)
        {
            string was = _lchLoaded.TryGetValue(kv.Key, out var l) ? l : "";
            if (string.Equals(kv.Value, was, StringComparison.Ordinal)) continue;
            try { f.SetField(kv.Key, kv.Value); _lchLoaded[kv.Key] = kv.Value; } catch { }
        }
        _lchPending.Clear();
        SaveMounts();
    }

    private void ReloadLaunchingIfBuilt()
    {
        if (IsMulti) return;
        LchSnapshot();
        if (_lchAppPath != null) _lchAppPath.Text = LchGet("ApplicationPath");
        if (_lchCmd != null) _lchCmd.Text = LchGet("CommandLine");
        if (_lchCfgPath != null) _lchCfgPath.Text = LchGet("ConfigurationPath");
        if (_lchCfgCmd != null) _lchCfgCmd.Text = LchGet("ConfigurationCommandLine");
        if (_lchUseDos != null) _lchUseDos.Checked = LchBool(LchGet("UseDosBox"));
        if (_lchDosConf != null) _lchDosConf.Text = LchGet("DosBoxConfigurationPath");
        if (_lchDosExe != null) _lchDosExe.Text = LchGet("CustomDosBoxVersionPath");
        if (_lchRoot != null) _lchRoot.Text = LchGet("RootFolder");
        string emu = LchGet("Emulator");
        if (_lchUseEmu != null) _lchUseEmu.Checked = emu.Length > 0 && emu != Guid.Empty.ToString();
        if (_lchEmuCombo != null) RefreshEmulatorCombo(emu);
        if (_lchCustomCmdChk != null) _lchCustomCmdChk.Checked = LchGet("CommandLine").Trim().Length > 0;
        if (_lchCustomCmd != null) _lchCustomCmd.Text = LchGet("CommandLine");
        if (_lchOvrStart != null) _lchOvrStart.Checked = LchBool(LchGet("OverrideDefaultStartupScreenSettings"));
        if (_lchOvrPause != null) _lchOvrPause.Checked = LchBool(LchGet("OverrideDefaultPauseScreenSettings"));
        LoadMounts();
        UpdateLaunchingEnablement();
    }
}
