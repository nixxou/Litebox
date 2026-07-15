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
        "ExitAutoHotkeyScript",
    };
    // The per-game LiteBox-only pause overrides (litebox-options.db) — cleared when the pause override is off.
    private static readonly string[] LchPauseLiteBoxKeys =
    {
        "PauseHotkey", "ScreenCaptureKey", "PadPauseEnabled", "PadPauseButton",
        "PauseScreenFreezeTiming", "PauseScreenFreezeOffsetMs", "PauseTarget", "PauseFreezeTree",
    };

    // ── Field IO (one chokepoint for modelled + extra fields) ─────────────

    private string LchGet(string field)
        => _lchPending.TryGetValue(field, out var v) ? v
         : _lchLoaded.TryGetValue(field, out var l) ? l
         : ((AppsGame as ILiteBoxFields)?.GetField(field) ?? "");

    private static bool LchBool(string v) => string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
    private static string LchB(bool v) => v ? "true" : "false";

    /// <summary>Symmetric to the wipe-on-uncheck (SaveLaunching): the instant the user ENABLES the Startup
    /// override on a game that didn't have it, seed the modal's native fields with the DEFAULT (global)
    /// settings. Without this, a stale leftover field — e.g. an imported UseStartupScreen=false — would show
    /// through and silently disable the screen the moment the override goes on. Solo only. Writes into the
    /// pending dict (LchGet reads it first), so the Customize modal opens on a clean default slate.</summary>
    private void LchSeedStartupDefaults()
    {
        var r = Safe(() => Gameplay.GameplaySettings.Resolve(null));
        _lchPending["UseStartupScreen"] = LchB(r?.UseStartup ?? true);
        _lchPending["DisableShutdownScreen"] = LchB((r?.ShutdownMinMs ?? 0) < 0);
        _lchPending["HideMouseCursorInGame"] = LchB(r?.HideCursor ?? false);
        _lchPending["AggressiveWindowHiding"] = LchB(false);
        _lchPending["HideAllNonExclusiveFullscreenWindows"] = LchB(false);
        _lchPending["StartupScreenPostLaunchDisplayTime"] = "";   // inherit (modal shows the global default)
        _lchPending["StartupLoadDelay"] = "";                     // inherit
    }

    /// <summary>Pause counterpart of <see cref="LchSeedStartupDefaults"/> — seed the pause override's native
    /// fields with the defaults when the user first enables it (solo).</summary>
    private void LchSeedPauseDefaults()
    {
        var r = Safe(() => Gameplay.GameplaySettings.Resolve(null));
        _lchPending["UsePauseScreen"] = LchB(r?.UsePause ?? true);
        _lchPending["SuspendProcessOnPause"] = LchB(true);
        _lchPending["ForcefulPauseScreenActivation"] = LchB(false);
        foreach (var s in new[] { "PauseAutoHotkeyScript", "ResumeAutoHotkeyScript", "ResetAutoHotkeyScript",
                                  "SaveStateAutoHotkeyScript", "LoadStateAutoHotkeyScript", "SwapDiscsAutoHotkeyScript",
                                  "ExitAutoHotkeyScript" })
            _lchPending[s] = "";
    }

    // ── Dirty-tracking bridge for the main Launching fields (revert ↺ + modified colour + multi merge) ──
    // Reuses the shared core (Track / _baseline / OnField / Modified) so these fields behave exactly like the
    // Metadata / Notes fields: an overlay ↺ appears when changed, the text goes reddish, and in multi-select
    // they merge to "‹multiple values›" and write only the ones actually edited, to every selected game.

    /// <summary>Seed value for a launching field: the game's own value in solo, or the merged value
    /// ("‹multiple values›" when the selected games differ) in multi.</summary>
    private string LchVal(string field, Func<IGame, string> get) => IsMulti ? LchMerge(get) : LchGet(field);

    private string LchMerge(Func<IGame, string> get)
    {
        string? first = null;
        foreach (var g in _editGames) { var v = (Safe(() => get(g)) ?? "").Trim(); if (first == null) first = v; else if (first != v) return Multi; }
        return first ?? "";
    }

    /// <summary>Register a launching text box with the shared dirty-tracker: overlay ↺, reddish-when-changed,
    /// baseline = its current (loaded/merged) value.</summary>
    private void LchTrack(TextBox t)
    {
        t.TextChanged += (_, _) => OnField(t);
        Track(t, (Panel)t.Parent!);
        _baseline[t] = t.Text;
        RefreshFieldState(t);   // greys the placeholder; ↺ starts hidden (baseline == current)
    }

    /// <summary>Re-baseline a tracked field to its current text (after a navigate/reload), so it reads clean.</summary>
    private void LchRebase(TextBox? t)
    {
        if (t == null || !_baseline.ContainsKey(t)) return;
        _baseline[t] = t.Text; RefreshFieldState(t);
    }

    private void LchRebaseChk(CheckBox? cb)
    {
        if (cb == null || !_baseline.ContainsKey(cb)) return;
        _baseline[cb] = ValueStrOf(cb); RefreshFieldState(cb);
    }

    private void LchRebaseCbo(ComboBox? cb)
    {
        if (cb == null || !_baseline.ContainsKey(cb)) return;
        _baseline[cb] = ValueStr(cb); RefreshFieldState(cb);
    }

    /// <summary>Multi save for a Startup/Pause "Override Default …" toggle: write the bool to all edited games,
    /// and when it goes OFF clear that concern's per-game LiteBox overrides (as the solo save does), so they
    /// stop taking effect. Skipped while Indeterminate ("‹multiple values›").</summary>
    private void WriteOverrideMulti(CheckBox? cb, string field, string[] clearLiteBoxWhenOff, string[] clearNativeWhenOff)
    {
        if (cb == null || !Modified(cb) || cb.CheckState == CheckState.Indeterminate) return;
        string v = LchB(cb.Checked);
        foreach (var g in _editGames)
        {
            var lf = g as ILiteBoxFields;
            try { lf?.SetField(field, v); } catch { }
            if (!cb.Checked)
            {
                foreach (var nf in clearNativeWhenOff) { try { lf?.SetField(nf, ""); } catch { } }   // wipe the native modal fields
                string gid = Safe(() => g.Id) ?? "";
                if (gid.Length > 0)
                    foreach (var k in clearLiteBoxWhenOff) { try { Data.LiteBoxOption.SetOverride(Data.LiteBoxOption.ScopeGame, gid, k, null); } catch { } }
            }
        }
        _lchLoaded[field] = v; _baseline[cb] = ValueStrOf(cb); RefreshFieldState(cb);
    }

    /// <summary>Multi save for the emulator assignment (from the 3-state "use emulator" + the emulator list):
    /// turning it OFF clears the emulator for all; picking a specific one sets it for all; the "‹multiple
    /// values›" row / Indeterminate leaves each game's own emulator untouched.</summary>
    private void WriteEmulatorMulti()
    {
        if (_lchUseEmu is { CheckState: CheckState.Unchecked } && Modified(_lchUseEmu))
        {
            foreach (var g in _editGames) { try { (g as ILiteBoxFields)?.SetField("Emulator", ""); } catch { } }
            LchRebaseChk(_lchUseEmu); LchRebaseCbo(_lchEmuCombo);
            return;
        }
        if (_lchEmuCombo != null && Modified(_lchEmuCombo) && !IsPlaceholder(_lchEmuCombo))
        {
            string id = SelectedEmulatorId();
            if (id.Length > 0 && id != LchMultiEmuId)
            {
                foreach (var g in _editGames) { try { (g as ILiteBoxFields)?.SetField("Emulator", id); } catch { } }
                LchRebaseCbo(_lchEmuCombo); LchRebaseChk(_lchUseEmu);
            }
        }
    }

    /// <summary>DOSBox and an emulator are mutually exclusive — the solo pages enforce it interactively
    /// (ConfirmDosBoxVsEmulation on checking DOSBox; "enable emulator ⇒ DOSBox off" when picking one), but those
    /// guards are <c>!IsMulti</c>, so a multi session that sets DOSBox on one page and an emulator on another could
    /// otherwise leave a game with BOTH — a state solo never allows. Re-assert the invariant at save, DIRECTIONALLY,
    /// mirroring the solo rules: whatever the user actually changed THIS session wins — assigning an emulator turns
    /// DOSBox off (solo's automatic behaviour), checking DOSBox clears the emulator (solo's confirm-and-clear). If
    /// neither field was touched, leave pre-existing states alone; if BOTH were changed (contradictory), DOSBox wins
    /// (the solo DOSBox note's stated behaviour). Writes via SetField, like the other multi writes, for one persistence path.</summary>
    private void EnforceDosBoxEmulatorExclusivityMulti()
    {
        bool dosCheckedNow = _lchUseDos is { Checked: true } && Modified(_lchUseDos);
        bool emuAssignedNow = _lchEmuCombo != null && Modified(_lchEmuCombo) && !IsPlaceholder(_lchEmuCombo)
                              && SelectedEmulatorId() is { Length: > 0 } sid && sid != LchMultiEmuId;
        if (!dosCheckedNow && !emuAssignedNow) return;   // nothing exclusivity-relevant changed → touch nothing
        foreach (var g in _editGames)
        {
            try
            {
                if (!g.UseDosBox || string.IsNullOrEmpty(g.EmulatorId)) continue;   // only the invalid both-set state
                if (dosCheckedNow) (g as ILiteBoxFields)?.SetField("Emulator", "");            // DOSBox wins → clear the emulator
                else               (g as ILiteBoxFields)?.SetField("UseDosBox", LchB(false));  // emulator wins → DOSBox off
            }
            catch { }
        }
    }

    /// <summary>Write a tracked launching field to EVERY edited game when it was actually changed and isn't the
    /// "‹multiple values›" placeholder, then re-baseline it (a re-save becomes a no-op). Mirrors SaveCurrent.</summary>
    private void WriteLch(TextBox? t, string field)
    {
        if (t == null || !Modified(t) || IsPlaceholder(t)) return;
        string v = t.Text.Trim();
        foreach (var g in _editGames) { try { (g as ILiteBoxFields)?.SetField(field, v); } catch { } }
        _lchLoaded[field] = v; _baseline[t] = t.Text;
        RefreshFieldState(t);
    }

    /// <summary>Merged bool across the selection: the common value, or null when the games differ
    /// (→ the 3-state checkbox shows Indeterminate + a red "‹multiple values›" label).</summary>
    private bool? LchMergeBool(Func<IGame, bool> get)
    {
        bool? first = null;
        foreach (var g in _editGames) { bool v; try { v = get(g); } catch { v = false; } if (first == null) first = v; else if (first != v) return null; }
        return first;
    }

    /// <summary>A hidden "‹multiple values›" label at (x,y) — shown while its 3-state checkbox is Indeterminate.
    /// GREY (SubFg), exactly like the placeholder text of the multi text-fields: grey = "the games differ /
    /// not set", RED is reserved for an actual modification (which also gets the ↺).</summary>
    private Label LchMultiLabel(Panel p, int x, int y)
    {
        var l = new Label { Text = Multi, AutoSize = true, ForeColor = SubFg, BackColor = Bg, Location = new Point(x, y), Visible = false };
        p.Controls.Add(l);
        l.BringToFront();   // added after the checkbox → would sit BEHIND it; keep it drawn on top
        return l;
    }

    /// <summary>Set up a Use-* checkbox: 3-state in multi (Indeterminate when the games differ, with a red
    /// "‹multiple values›" label beside it), dirty-tracked (reddish text when changed). The 3rd state is
    /// DISPLAY-ONLY — a user click only toggles Checked↔Unchecked, it never cycles back INTO Indeterminate
    /// ("multiple values" isn't a choice you make); a ↺ restores the Indeterminate baseline. Returns the
    /// multi label (null in solo).</summary>
    private Label? LchTrackChk(CheckBox cb, string field, Func<IGame, bool> get, Panel p)
        => LchTrackChk3(cb, LchMergeBool(get), LchBool(LchGet(field)), p);

    /// <summary>As LchTrackChk but with the merged (multi, null = differ) and solo values passed directly —
    /// for a checkbox derived from a NON-bool field (e.g. "use an emulator" from the Emulator id).</summary>
    private Label? LchTrackChk3(CheckBox cb, bool? merged, bool solo, Panel p)
    {
        cb.ThreeState = IsMulti;
        if (IsMulti)
        {
            cb.CheckState = merged.HasValue ? (merged.Value ? CheckState.Checked : CheckState.Unchecked) : CheckState.Indeterminate;
            // AutoCheck off: the default 3-state cycle (Unchecked→Checked→Indeterminate) would let a click
            // land on Indeterminate, which makes no sense. Toggle manually between the two real choices.
            cb.AutoCheck = false;
            cb.Click += (_, _) => { if (!_readOnly) cb.CheckState = cb.CheckState == CheckState.Checked ? CheckState.Unchecked : CheckState.Checked; };
        }
        else cb.Checked = solo;

        // Position the label/↺ just PAST the checkbox text. PreferredSize can under-measure at build time
        // (before layout) and leave the label overlapping — behind — the box, so measure the text directly
        // and add the glyph width + a gap.
        int rx = cb.Left + S(22) + TextRenderer.MeasureText(cb.Text, cb.Font).Width + S(8);
        Label? multi = IsMulti ? LchMultiLabel(p, rx, cb.Top + S(1)) : null;
        cb.CheckStateChanged += (_, _) => { OnField(cb); if (multi != null) multi.Visible = cb.CheckState == CheckState.Indeterminate; };
        _fields.Add(cb);
        _baseline[cb] = ValueStrOf(cb);

        if (IsMulti)
        {
            // ↺ revert, at the SAME spot as the multi label — they're mutually exclusive: the label shows while
            // Indeterminate (== baseline, so "not modified"), the ↺ shows once the user picks a definite value.
            var rb = LchRevertButton(cb, new Point(rx, cb.Top - S(1)));
            p.Controls.Add(rb); rb.BringToFront();
            _revert[cb] = rb;
        }

        if (multi != null) multi.Visible = cb.CheckState == CheckState.Indeterminate;
        RefreshFieldState(cb);
        return multi;
    }

    // CheckState string for the baseline (ValueStr is private static in the core partial — expose a local shim).
    private static string ValueStrOf(CheckBox cb) => cb.CheckState.ToString();

    /// <summary>A hidden ↺ revert button (same style as the core Track button) that restores a control's
    /// baseline. Shown/hidden by RefreshFieldState via the _revert map.</summary>
    private Button LchRevertButton(Control target, Point loc)
    {
        var b = new Button
        {
            Text = "↺", Size = new Size(S(18), S(18)), Location = loc, Visible = false, TabStop = false, Cursor = Cursors.Hand,
            FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(92, 46, 42), ForeColor = Color.FromArgb(255, 180, 165),
            Font = new Font("Segoe UI Symbol", 9.5f), FlatAppearance = { BorderSize = 1 },
        };
        b.FlatAppearance.BorderColor = Color.FromArgb(150, 72, 64);
        b.Click += (_, _) => RevertField(target);
        _tips.SetToolTip(b, "Restore the original value");
        return b;
    }

    /// <summary>Write a tracked 3-state checkbox to EVERY edited game when changed and not Indeterminate.</summary>
    private void WriteLchBool(CheckBox? cb, string field)
    {
        if (cb == null || !Modified(cb) || cb.CheckState == CheckState.Indeterminate) return;
        string v = LchB(cb.Checked);
        foreach (var g in _editGames) { try { (g as ILiteBoxFields)?.SetField(field, v); } catch { } }
        _lchLoaded[field] = v; _baseline[cb] = ValueStrOf(cb);
        RefreshFieldState(cb);
    }

    private bool _lchSnapped;
    private bool _lchDosEmuGuard;   // re-entrancy guard for the DOSBox ↔ emulator mutual-exclusion handlers
    private bool _lchEmuDiffer;      // multi: the selected games use different emulators → the combo carries a "‹multiple values›" row
    private const string LchMultiEmuId = "multi";   // sentinel id for that row (never a real GUID)

    /// <summary>Merged emulator id across the selection: "" (all none), a shared id, or the sentinel when they differ.</summary>
    private string LchMergeEmuId()
    {
        string? first = null;
        foreach (var g in _editGames)
        {
            string e = (Safe(() => g.EmulatorId) ?? "").Trim();
            if (e == Guid.Empty.ToString()) e = "";
            if (first == null) first = e; else if (!string.Equals(first, e, StringComparison.OrdinalIgnoreCase)) return LchMultiEmuId;
        }
        return first ?? "";
    }

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
            "SwapDiscsAutoHotkeyScript", "ExitAutoHotkeyScript",
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
        _lchAppPath = LchTxt(p, LchVal("ApplicationPath", g => g.ApplicationPath), ref y, out _, browse: true, onBrowse: t => LchBrowseFile(t, "Select the application / ROM file"));
        LchTrack(_lchAppPath);

        LchCap(p, "Application Command-Line Parameters:", ref y);
        _lchCmd = LchTxt(p, LchVal("CommandLine", g => g.CommandLine), ref y, out _);
        _lchCmd.TextChanged += (_, _) => { if (_lchCustomCmd != null && !_lchCustomCmd.Focused) _lchCustomCmd.Text = _lchCmd.Text; };
        LchTrack(_lchCmd);

        LchCap(p, "Configuration Application Path:", ref y);
        _lchCfgPath = LchTxt(p, LchVal("ConfigurationPath", g => g.ConfigurationPath), ref y, out _lchCfgBrowse, browse: true, onBrowse: t => LchBrowseFile(t, "Select the configuration application"));
        LchTrack(_lchCfgPath);

        LchCap(p, "Configuration Command-Line Parameters:", ref y);
        _lchCfgCmd = LchTxt(p, LchVal("ConfigurationCommandLine", g => g.ConfigurationCommandLine), ref y, out _);
        LchTrack(_lchCfgCmd);

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
        };
        p.Controls.Add(_lchUseDos);
        LchTrackChk(_lchUseDos, "UseDosBox", g => g.UseDosBox, p);   // 3-state + grey "‹multiple values›" in multi, dirty-tracked
        _lchUseDos.Enabled = !_readOnly;   // set ONCE (never via LchEnable, which would clobber AutoCheck/colour)
        _lchUseDos.CheckStateChanged += (_, _) =>
        {
            // Solo: DOSBox and an emulator are mutually exclusive. Instead of greying the box out, let the user
            // check it and confirm turning emulation OFF (Yes) or undo the check (No).
            if (!_lchDosEmuGuard && !IsMulti && _lchUseDos.Checked && LchEmuOn) ConfirmDosBoxVsEmulation();
            UpdateLaunchingEnablement();
        };
        y += S(32);

        LchCap(p, "Custom DOSBox Configuration File (dosbox.conf):", ref y);
        _lchDosConf = LchTxt(p, LchVal("DosBoxConfigurationPath", g => g.DosBoxConfigurationPath), ref y, out var confBrowse, browse: true,
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
        LchTrack(_lchDosConf);   // after the width shrink so the ↺ lands at the field's real right edge
        LchHelp(p,
            "Leave blank for the default DOSBox configuration. Click the Browse button to browse for and select an "
            + "existing dosbox.conf file. Click the Create button to create a new dosbox.conf file and base it off of "
            + "the default DOSBox configuration.", ref y, 3);

        LchCap(p, "Custom DOSBox Version EXE Path:", ref y);
        _lchDosExe = LchTxt(p, LchVal("CustomDosBoxVersionPath", g => (g as ILiteBoxFields)?.GetField("CustomDosBoxVersionPath") ?? ""), ref y, out _, browse: true,
            onBrowse: t => LchBrowseFile(t, "Select a DOSBox executable"));
        LchTrack(_lchDosExe);
        LchHelp(p,
            "Leave blank for the default version of DOSBox that comes with LaunchBox. Click the Browse button to "
            + "browse for and select a custom version/copy of DOSBox.", ref y, 2);

        _lchDosNote = new Label
        {
            Dock = DockStyle.Bottom, Height = S(26), ForeColor = SubFg, BackColor = Bg,
            Font = new Font("Segoe UI", 9f, FontStyle.Italic),
            Text = "DOSBox and an emulator can't both be used — enabling DOSBox will turn emulation off for this game.",
        };
        p.Controls.Add(_lchDosNote);
        UpdateLaunchingEnablement();
        return p;
    }

    /// <summary>The user checked "Use DOSBox" while an emulator is assigned (solo). Ask whether to turn
    /// emulation off (Yes → clear the emulator + keep DOSBox on) or undo the check (No). Guarded so the
    /// state changes it makes don't re-trigger the handlers.</summary>
    private void ConfirmDosBoxVsEmulation()
    {
        var r = MessageBox.Show(this,
            "DOSBox can't be used at the same time as an emulator.\n\nTurn off emulation for this game so it runs through DOSBox?",
            "DOSBox vs. Emulator", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        _lchDosEmuGuard = true;
        try
        {
            if (r == DialogResult.Yes)
            {
                _lchPending["Emulator"] = "";                    // clear the emulator assignment (persists on save)
                if (_lchUseEmu != null) _lchUseEmu.Checked = false;   // reflect it if the Emulation page is built
            }
            else if (_lchUseDos != null) _lchUseDos.Checked = false;  // keep emulation; undo the DOSBox check
        }
        finally { _lchDosEmuGuard = false; }
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
        if (IsMulti)
        {
            var mm = LchMergeEmuId();          // "", a shared id, or the sentinel when they differ
            _lchEmuDiffer = mm == LchMultiEmuId;
            curEmu = mm;                        // the combo then selects the "‹multiple values›" row / shared emulator
        }
        _lchUseEmu = new CheckBox
        {
            Text = "Use an emulator to play this game (primarily for console games)", AutoSize = true,
            Location = new Point(S(14), y), ForeColor = Fg, BackColor = Bg,
        };
        p.Controls.Add(_lchUseEmu);
        // 3-state in multi (merged from "the game has an emulator"); dirty-tracked in both modes.
        bool HasEmu(IGame g0) { var e = Safe(() => g0.EmulatorId) ?? ""; return e.Length > 0 && e != Guid.Empty.ToString(); }
        LchTrackChk3(_lchUseEmu, IsMulti ? LchMergeBool(HasEmu) : (bool?)null, emuOn, p);
        _lchUseEmu.Enabled = !_readOnly;
        y += S(30);

        LchCap(p, "Choose an emulator:", ref y);
        _lchEmuCombo = new ComboBox
        {
            Location = new Point(S(14), y), Width = S(700), DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = Field, ForeColor = Fg, FlatStyle = FlatStyle.Flat,
        };
        p.Controls.Add(_lchEmuCombo);
        RefreshEmulatorCombo(curEmu);
        // Dirty-track the list: revert ↺ + colour, and the "‹multiple values›" row shows grey while a real pick goes red.
        _lchEmuCombo.SelectedIndexChanged += (_, _) => OnField(_lchEmuCombo);
        Track(_lchEmuCombo, p);
        _baseline[_lchEmuCombo] = ValueStr(_lchEmuCombo);
        RefreshFieldState(_lchEmuCombo);
        y += S(32);

        var add = DlgBtn("Add…", Color.FromArgb(60, 60, 72));
        var edit = DlgBtn("Edit…", Color.FromArgb(60, 60, 72));
        var del = DlgBtn("Delete", Color.FromArgb(60, 60, 72));
        add.Location = new Point(S(14), y);
        edit.Location = new Point(S(90), y);
        del.Location = new Point(S(166), y);
        // Add / Delete act in a single game's context — disabled across a multi-selection. Edit stays enabled
        // (it edits the emulator DEFINITION, which is global).
        add.Enabled = del.Enabled = !_readOnly && !IsMulti;
        edit.Enabled = !_readOnly;
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

        // "Use Custom Command-line Parameters" — the emulator's FORCED arguments. Same <CommandLine> field the
        // main Launching page shows as "Application Command-Line" (which only matters for a direct-exe game);
        // the two controls MIRROR each other. Shown in multi too — this is the emulator-args view.
        _lchCustomCmdChk = new CheckBox
        {
            Text = "Use Custom Command-line Parameters:", AutoSize = true,
            Location = new Point(S(14), y), ForeColor = Fg, BackColor = Bg,
        };
        p.Controls.Add(_lchCustomCmdChk);
        bool HasCmd(IGame g0) => (Safe(() => g0.CommandLine) ?? "").Trim().Length > 0;
        LchTrackChk3(_lchCustomCmdChk, IsMulti ? LchMergeBool(HasCmd) : (bool?)null, LchGet("CommandLine").Trim().Length > 0, p);
        _lchCustomCmdChk.CheckedChanged += (_, _) => UpdateLaunchingEnablement();
        y += S(26);
        // Seed from the Launching page's LIVE textbox when it is already built, not from LchVal / the game's
        // stored value — pages are built lazily and cached, so if the user already typed into _lchCmd before
        // ever opening this tab, re-reading from the game here would silently drop that pending edit (this page's
        // mirror below would then overwrite _lchCmd with the stale re-read the moment _lchCustomCmd is touched).
        _lchCustomCmd = LchTxt(p, _lchCmd?.Text ?? LchVal("CommandLine", g => g.CommandLine), ref y, out _);
        _lchCustomCmd.TextChanged += (_, _) => { if (_lchCmd != null && !_lchCmd.Focused) _lchCmd.Text = _lchCustomCmd.Text; };
        LchTrack(_lchCustomCmd);   // revert + modified colour + merge (mirrors the main Launching CommandLine)

        _lchUseEmu.CheckedChanged += (_, _) =>
        {
            // Symmetric exclusivity (solo only): turning an emulator ON silently turns DOSBox OFF (the user is
            // choosing the emulator, so no prompt needed). Guarded against the DOSBox-side handler's re-entrancy.
            if (!IsMulti && !_lchDosEmuGuard && _lchUseEmu.Checked && (_lchUseDos?.Checked ?? LchBool(LchGet("UseDosBox"))))
            {
                _lchDosEmuGuard = true;
                try { if (_lchUseDos != null) _lchUseDos.Checked = false; _lchPending["UseDosBox"] = LchB(false); }
                finally { _lchDosEmuGuard = false; }
            }
            UpdateLaunchingEnablement();
        };
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
        // Multi-select with differing emulators: a "‹multiple values›" row at the top (grey placeholder).
        if (IsMulti && _lchEmuDiffer) _lchEmus.Insert(0, (LchMultiEmuId, Multi));
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
        _lchRoot = LchTxt(p, LchVal("RootFolder", g => g.RootFolder), ref y, out _, browse: true, onBrowse: t => LchBrowseFolder(t, "Select the root folder"));
        LchTrack(_lchRoot);
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
        };
        p.Controls.Add(_lchOvrStart);
        LchTrackChk(_lchOvrStart, "OverrideDefaultStartupScreenSettings", g => { try { return g.OverrideDefaultStartupScreenSettings; } catch { return false; } }, p);
        _lchOvrStart.Enabled = !_readOnly;
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
        };
        p.Controls.Add(_lchOvrPause);
        LchTrackChk(_lchOvrPause, "OverrideDefaultPauseScreenSettings", g => LchBool((g as ILiteBoxFields)?.GetField("OverrideDefaultPauseScreenSettings") ?? ""), p);
        _lchOvrPause.Enabled = !_readOnly;
        y += S(26);
        var custPause = DlgBtn("Customize…", Color.FromArgb(60, 60, 72));
        custPause.Location = new Point(S(14), y);
        custPause.Click += (_, _) => ShowPauseCustomizeDialog();
        p.Controls.Add(custPause);

        // Customize… is reachable ONLY while its Override checkbox is checked (LB parity) — and only in SOLO:
        // the modals' content isn't multi-ready yet, so they stay disabled across a multi-selection. The base
        // override toggles still work in multi (enable/disable the override en masse).
        // Customize… is reachable while its Override box is Checked OR partially-checked (Indeterminate, i.e.
        // the games differ) — in solo and multi alike. Only "all unchecked" disables it.
        void SyncCust()
        {
            // Both Customize modals are multi-ready: reachable while their Override box is Checked OR partially
            // checked (Indeterminate = the games differ), in solo and multi.
            custStart.Enabled = !_readOnly && _lchOvrStart.CheckState != CheckState.Unchecked;
            custPause.Enabled = !_readOnly && _lchOvrPause.CheckState != CheckState.Unchecked;
        }
        // Seed defaults the moment the user ENABLES an override on a game that didn't have it (solo, off→on) —
        // the symmetric counterpart of the wipe-on-uncheck in SaveLaunching. So the Customize modal opens on a
        // clean default slate instead of showing a stale leftover field that would silently flip the screen.
        // Baseline read = the game's SAVED field (bypasses pending edits) so we only seed when the override is
        // being turned on for a game that didn't already have it — never re-seed (and clobber) an existing one.
        bool WasOverrideOn(string key) { try { return LchBool((AppsGame as ILiteBoxFields)?.GetField(key) ?? ""); } catch { return false; } }
        _lchOvrStart.CheckedChanged += (_, _) =>
        {
            SyncCust();
            if (!IsMulti && _lchOvrStart.Checked && !WasOverrideOn("OverrideDefaultStartupScreenSettings"))
                LchSeedStartupDefaults();
        };
        _lchOvrPause.CheckedChanged += (_, _) =>
        {
            SyncCust();
            if (!IsMulti && _lchOvrPause.Checked && !WasOverrideOn("OverrideDefaultPauseScreenSettings"))
                LchSeedPauseDefaults();
        };
        SyncCust();
        return p;
    }

    /// <summary>LB's "Override Default Startup Screen Settings" dialog — edits the pending values
    /// (written on the page save). Times are STORED in milliseconds, shown in seconds like LB.</summary>
    private void ShowStartupCustomizeDialog()
    {
        using var f = NewDialog("Override Default Startup Screen Settings", 700, 470);
        var (tabs, pgMain, pgLbx) = DialogTabs(f, "Startup Screen");
        var host = new Panel { Dock = DockStyle.Fill, BackColor = Bg, AutoScroll = true };
        pgMain.Controls.Add(host);

        int x = S(16), y = S(14);

        // Modal-LOCAL dirty tracking (the window's _fields/_baseline belong to the persistent pages, not this
        // transient dialog). Multi: a checkbox is 3-state (Indeterminate = the games differ); a click only
        // toggles Checked↔Unchecked; a ↺ reverts to the differ/baseline. mVals = what OK writes (null = skip).
        // The mechanics (ModalVal/ModalRefresh/ModalRevertButton/ModalChk) are shared with the Pause dialog.
        var mBase = new Dictionary<Control, string>();
        var mRev = new Dictionary<Control, Button>();
        var mVals = new List<Func<(string field, string? value)>>();
        // get(g) = the game's raw field value; `invert` shows its opposite (Enable Shutdown = !DisableShutdown).
        CheckBox Chk(string text, string field, bool invert, int cx, int cy, Func<IGame, bool> get)
            => ModalChk(host, mBase, mRev, mVals, text, field, invert, cx, cy, get);
        Chk("Enable Game Startup Screen", "UseStartupScreen", false, x, y, g => g.UseStartupScreen);
        // Bound directly to the raw field (NOT the inverted "Enable Shutdown") so a fresh game — where every
        // field is false — shows every box UNCHECKED. Unchecked = DisableShutdownScreen false = shutdown stays
        // enabled (the default), so behaviour is unchanged; only the default visual is a clean slate.
        Chk("Disable Game Shutdown Screen", "DisableShutdownScreen", false, S(360), y, g => g.DisableShutdownScreen);
        y += S(26);
        Chk("Hide Mouse Cursor During Game", "HideMouseCursorInGame", false, x, y, g => g.HideMouseCursorInGame);
        Chk("Aggressive Startup Window Hiding", "AggressiveWindowHiding", false, S(360), y, g => g.AggressiveWindowHiding);
        y += S(34);

        // Global defaults for the two duration sliders, so an un-overridden game shows the value it would
        // ACTUALLY use rather than a bare 0 (LB parity request). Returns a value-getter that keeps the field
        // EMPTY (= inherit) when the game hadn't set it and the user didn't touch the slider — so opening the
        // modal + OK never freezes the global into the game.
        int postGlobal = 1000; try { postGlobal = Gameplay.GameplaySettings.Resolve(null)?.StartupMinMs ?? 1000; } catch { }
        int delayGlobal = 5000; try { delayGlobal = Gameplay.GameplaySettings.RevealMaxMs(null); } catch { }

        // Duration slider. Multi: caption "‹multiple values›" (grey) when the games differ; the FIRST move sets
        // a definite value (red) applied to all, and a ↺ reverts to "don't touch". Solo: shows the value or the
        // global default; writing stays EMPTY (inherit) unless the game had it set or the user moved the slider.
        void SliderRow(string caption, string field, int max, int step, Func<int, string> fmt, int globalDefault, Func<IGame, string> rawGet, ref int sy)
        {
            bool differ = false; string mergedRaw;
            if (IsMulti)
            {
                string? first = null;
                foreach (var g in _editGames) { var v = (Safe(() => rawGet(g)) ?? "").Trim(); if (first == null) first = v; else if (first != v) { differ = true; break; } }
                mergedRaw = first ?? "";
            }
            else mergedRaw = (LchGet(field) ?? "").Trim();

            bool wasSet = !differ && mergedRaw.Length > 0 && int.TryParse(mergedRaw, out _);
            int val = Math.Max(0, Math.Min(max, wasSet ? int.Parse(mergedRaw, CultureInfo.InvariantCulture) : globalDefault));
            bool touched = false;

            var cap = new Label { AutoSize = true, Location = new Point(x, sy), BackColor = Bg };
            host.Controls.Add(cap);
            sy += S(22);
            var bar = new TrackBar
            {
                Location = new Point(x, sy), Width = S(620), Minimum = 0, Maximum = max, SmallChange = step,
                LargeChange = step, TickFrequency = max / 20, Value = val, Enabled = !_readOnly,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, BackColor = Bg,
            };
            host.Controls.Add(bar);
            Button? rb = null;
            void Paint()
            {
                bool placeholder = differ && !touched;
                cap.ForeColor = touched ? ModifiedColor : (placeholder ? SubFg : Fg);
                cap.Text = placeholder ? caption + Multi
                         : caption + fmt(bar.Value) + (!IsMulti && !wasSet && !touched ? "   (global default)" : "");
                if (rb != null) rb.Visible = touched && !_readOnly;
            }
            bar.ValueChanged += (_, _) => { touched = true; Paint(); };
            if (IsMulti)
            {
                rb = ModalRevertButton(() => { bar.Value = val; touched = false; Paint(); }, new Point(x + S(626), sy + S(2)));
                host.Controls.Add(rb); rb.BringToFront();
            }
            Paint();
            sy += S(48);
            mVals.Add(() => IsMulti
                ? (field, touched ? bar.Value.ToString(CultureInfo.InvariantCulture) : (string?)null)
                : (field, (wasSet || touched) ? bar.Value.ToString(CultureInfo.InvariantCulture) : ""));
        }

        SliderRow("Post-Launch Display Time: ", "StartupScreenPostLaunchDisplayTime", 10000, 250,
            ms => $"{ms / 1000.0:0.###} Second(s)", postGlobal, g => (g as ILiteBoxFields)?.GetField("StartupScreenPostLaunchDisplayTime") ?? "", ref y);

        Chk("Hide All Windows that are not in Exclusive Fullscreen Mode", "HideAllNonExclusiveFullscreenWindows", false, x, y, g => g.HideAllNonExclusiveFullscreenWindows);
        y += S(24);
        var help1 = new Label
        {
            Location = new Point(x, y), Size = new Size(S(620), S(48)), ForeColor = SubFg, BackColor = Bg,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            Text = "This option may make for a cleaner startup sequence for some emulators that use exclusive fullscreen "
                 + "mode. If you're seeing a black screen and the game never shows up after loading, you'll need to uncheck "
                 + "this box. If you're setting up a new or unknown emulator, it's worth trying to check this box to see if "
                 + "it makes the startup experience smoother without causing issues.",
        };
        host.Controls.Add(help1);
        y += S(56);

        SliderRow("Startup Load Delay: ", "StartupLoadDelay", 60000, 250,
            ms => $"{ms / 1000.0:0.000} second(s)", delayGlobal,
            g => { try { var d = g.StartupLoadDelay; return d > 0 ? d.ToString(CultureInfo.InvariantCulture) : ""; } catch { return ""; } }, ref y);
        var help2 = new Label
        {
            Location = new Point(x, y), Size = new Size(S(620), S(34)), ForeColor = SubFg, BackColor = Bg,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            Text = "The startup load delay generally defines how long the emulator takes to load before the game is playing. "
                 + "This is ultimately how long LaunchBox will wait after launching the emulator's EXE file before showing the game, if possible. "
                 + "Under LiteBox it also serves as SmartCapture's \"reveal anyway\" ceiling — the cover lifts by this time even if no render is detected (0 = default 5s).",
        };
        host.Controls.Add(help2);

        // LiteBox tab — the startup-related LiteBox-only overrides (stay-on-top, exit-early, Smart Capture).
        // Multi-aware: each option merges across the games (‹multiple values›) and writes to all on OK.
        var lbxIds = _editGames.Select(g => Safe(() => g.Id) ?? "").Where(id => id.Length > 0).ToArray();
        var (lbxPanel, lbxSave) = Gameplay.LiteBoxGameplayEditor.Build(Data.LiteBoxOption.ScopeGame,
            lbxIds, _s, Bg, Fg, SubFg, Field, _readOnly, Gameplay.GameplaySection.Startup);
        lbxPanel.Dock = DockStyle.Fill;
        pgLbx.Controls.Add(lbxPanel);

        var bottom = DialogButtons(f, out var ok, out var cancel);
        ok.Click += (_, _) =>
        {
            // Native fields: solo → pending (flushed by SaveLaunching); multi → straight to every edited game
            // (only definite + changed values; the mVals getters return null to skip).
            foreach (var getv in mVals)
            {
                var (field, value) = getv();
                if (value == null) continue;
                if (IsMulti) foreach (var g in _editGames) { try { (g as ILiteBoxFields)?.SetField(field, value); } catch { } }
                else _lchPending[field] = value;
            }
            if (!_readOnly) { try { lbxSave?.Invoke(); } catch { } }
            f.DialogResult = DialogResult.OK; f.Close();
        };
        cancel.Click += (_, _) => { f.DialogResult = DialogResult.Cancel; f.Close(); };
        f.ShowDialog(this);
    }

    // ── Shared helpers for the two Customize dialogs (outer dark tab strip + OK/Cancel footer) ──

    /// <summary>Adds a dark owner-drawn outer TabControl to the dialog with a first "<paramref
    /// name="mainTitle"/>" tab and a "LiteBox" tab; returns both pages. Fill-docked (add BEFORE the
    /// footer so the footer claims the bottom).</summary>
    private (TabControl tabs, TabPage main, TabPage lbx) DialogTabs(Form f, string mainTitle)
    {
        var tabs = new TabControl { Dock = DockStyle.Fill, ItemSize = new Size(S(120), S(24)) };
        StyleDarkTabs(tabs, f);
        var main = new TabPage(mainTitle) { BackColor = Bg, UseVisualStyleBackColor = false };
        var lbx = new TabPage("LiteBox") { BackColor = Bg, UseVisualStyleBackColor = false };
        tabs.TabPages.Add(main); tabs.TabPages.Add(lbx);
        f.Controls.Add(tabs);   // Fill added first; the Bottom footer (added later) claims its strip
        return (tabs, main, lbx);
    }

    /// <summary>The dark owner-drawn tab paint used by every TabControl in these Customize dialogs — the outer
    /// strip built by DialogTabs above, and the AutoHotkey-script sub-tabs in ShowPauseCustomizeDialog. Sets
    /// DrawMode/SizeMode too, so a caller only needs its own Size/Location/ItemSize/Anchor.</summary>
    private void StyleDarkTabs(TabControl t, Form f)
    {
        t.DrawMode = TabDrawMode.OwnerDrawFixed;
        t.SizeMode = TabSizeMode.Fixed;
        t.DrawItem += (_, e) =>
        {
            bool sel = e.Index == t.SelectedIndex;
            using var b = new SolidBrush(sel ? Field : PanelC);
            e.Graphics.FillRectangle(b, e.Bounds);
            TextRenderer.DrawText(e.Graphics, t.TabPages[e.Index].Text, f.Font, e.Bounds,
                sel ? Color.White : SubFg, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        };
    }

    // ── Shared modal-local dirty-tracking for the two Customize dialogs' fields ─────────────────
    // Both dialogs keep their OWN mBase/mRev/mVals per-invocation state (a transient dialog, not the window's
    // persistent _fields/_baseline) but the mechanics — read a control's comparable value, repaint it
    // modified/clean + toggle its revert button, build a consistently-styled revert button, and build a
    // 3-state multi-aware checkbox wired into all of that — were previously copy-pasted whole between them.

    private static string ModalVal(Control c) => c is CheckBox cb ? cb.CheckState.ToString() : "";

    private void ModalRefresh(Control c, Dictionary<Control, string> mBase, Dictionary<Control, Button> mRev)
    {
        bool mod = mBase.TryGetValue(c, out var b) && b != ModalVal(c);
        c.ForeColor = mod ? ModifiedColor : Fg;
        if (mRev.TryGetValue(c, out var rb)) rb.Visible = mod && !_readOnly;
    }

    private Button ModalRevertButton(Action onClick, Point loc)
    {
        var rb = new Button { Text = "↺", Size = new Size(S(18), S(18)), Location = loc, Visible = false, TabStop = false, Cursor = Cursors.Hand, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(92, 46, 42), ForeColor = Color.FromArgb(255, 180, 165), Font = new Font("Segoe UI Symbol", 9.5f), FlatAppearance = { BorderSize = 1 } };
        rb.FlatAppearance.BorderColor = Color.FromArgb(150, 72, 64);
        rb.Click += (_, _) => onClick();
        return rb;
    }

    /// <summary>A 3-state (multi) / plain (solo) checkbox wired into the given dialog's mBase/mRev/mVals —
    /// get(g) = the game's raw field value; invert shows its opposite (e.g. Enable Shutdown = !DisableShutdown).
    /// Multi → 3-state (Indeterminate = the games differ), with a ↺ that reverts to that baseline. On OK: solo
    /// always writes via mVals; multi writes only when definite (not Indeterminate) AND changed.</summary>
    private CheckBox ModalChk(Panel host, Dictionary<Control, string> mBase, Dictionary<Control, Button> mRev,
        List<Func<(string field, string? value)>> mVals, string text, string field, bool invert, int cx, int cy, Func<IGame, bool> get)
    {
        var cb = new CheckBox { Text = text, AutoSize = true, Location = new Point(cx, cy), ForeColor = Fg, BackColor = Bg, Enabled = !_readOnly };
        if (IsMulti)
        {
            var m = LchMergeBool(g => invert ? !get(g) : get(g));
            cb.ThreeState = true;
            cb.CheckState = m.HasValue ? (m.Value ? CheckState.Checked : CheckState.Unchecked) : CheckState.Indeterminate;
            cb.AutoCheck = false;
            cb.Click += (_, _) => { if (!_readOnly) cb.CheckState = cb.CheckState == CheckState.Checked ? CheckState.Unchecked : CheckState.Checked; };
        }
        else { bool v = LchBool(LchGet(field)); cb.Checked = invert ? !v : v; }
        host.Controls.Add(cb);
        cb.CheckStateChanged += (_, _) => ModalRefresh(cb, mBase, mRev);
        mBase[cb] = ModalVal(cb);
        if (IsMulti)
        {
            int rx = cx + S(22) + TextRenderer.MeasureText(cb.Text, cb.Font).Width + S(6);
            var rb = ModalRevertButton(() => { cb.CheckState = mBase.TryGetValue(cb, out var b) && Enum.TryParse<CheckState>(b, out var cs) ? cs : CheckState.Indeterminate; ModalRefresh(cb, mBase, mRev); }, new Point(rx, cy - S(1)));
            host.Controls.Add(rb); rb.BringToFront(); mRev[cb] = rb;
        }
        ModalRefresh(cb, mBase, mRev);
        mVals.Add(() =>
        {
            if (cb.CheckState == CheckState.Indeterminate) return (field, null);
            if (!(mBase.TryGetValue(cb, out var b) && b != ModalVal(cb))) return (field, null);   // only write what the user CHANGED
            bool stored = invert ? !cb.Checked : cb.Checked;
            return (field, LchB(stored));
        });
        return cb;
    }

    private Panel DialogButtons(Form f, out Button ok, out Button cancel)
    {
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = S(46), BackColor = Bg };
        ok = DlgBtn("✔ OK", Color.FromArgb(50, 110, 65));
        cancel = DlgBtn("✘ Cancel", Color.FromArgb(70, 70, 82));
        ok.Enabled = !_readOnly;
        cancel.DialogResult = DialogResult.Cancel;
        ok.Location = new Point(S(16), S(8));
        cancel.Location = new Point(S(100), S(8));
        bottom.Controls.AddRange(new Control[] { ok, cancel });
        f.Controls.Add(bottom);
        f.AcceptButton = ok; f.CancelButton = cancel;
        return bottom;
    }

    /// <summary>LB's "Override Default Pause Screen Settings" dialog — three switches + the six
    /// per-game AutoHotkey scripts, tabbed like LB.</summary>
    private void ShowPauseCustomizeDialog()
    {
        using var f = NewDialog("Override Default Pause Screen Settings", 700, 500);
        var (tabs, pgMain, pgLbx) = DialogTabs(f, "Pause Screen");
        var host = new Panel { Dock = DockStyle.Fill, BackColor = Bg, AutoScroll = true };
        pgMain.Controls.Add(host);

        int x = S(16), y = S(14);

        // Modal-LOCAL dirty tracking (same pattern as the Startup modal — shared via ModalVal/ModalRefresh/
        // ModalRevertButton/ModalChk). Multi: 3-state checkboxes + ↺; the AHK script boxes merge to
        // "‹multiple values›". OK writes only what changed, to every edited game.
        var mBase = new Dictionary<Control, string>();
        var mRev = new Dictionary<Control, Button>();
        var mVals = new List<Func<(string field, string? value)>>();        bool EF(IGame g, string fld) => LchBool((g as ILiteBoxFields)?.GetField(fld) ?? "");
        CheckBox Chk(string text, string field, int cx, int cy, Func<IGame, bool> get)
            => ModalChk(host, mBase, mRev, mVals, text, field, false, cx, cy, get);
        Chk("Enable Game Pause Screen", "UsePauseScreen", x, y, g => EF(g, "UsePauseScreen"));
        Chk("Suspend Emulator Process While Paused", "SuspendProcessOnPause", S(360), y, g => EF(g, "SuspendProcessOnPause"));
        y += S(26);
        Chk("Forceful Pause Screen Activation (enable this if the pause screen is not showing)", "ForcefulPauseScreenActivation", x, y, g => EF(g, "ForcefulPauseScreenActivation"));
        y += S(28);

        host.Controls.Add(new Label { Text = "AutoHotkey Scripts:", AutoSize = true, Location = new Point(x, y), ForeColor = Fg, BackColor = Bg });
        y += S(20);
        // Explicit rule for the per-script override (matches PauseManager.ScriptStr).
        host.Controls.Add(new Label
        {
            Location = new Point(x, y), Size = new Size(S(650), S(32)), ForeColor = SubFg, BackColor = Bg,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            Text = "A non-empty script REPLACES the emulator's default for that action. Leave a tab blank to inherit "
                 + "the default; put a single comment line ( ;  … ) to override with nothing = disable it entirely.",
        });
        y += S(36);

        var scriptTabs = new (string title, string field)[]
        {
            ("On Pause", "PauseAutoHotkeyScript"), ("On Resume", "ResumeAutoHotkeyScript"),
            ("Reset Game", "ResetAutoHotkeyScript"), ("Save State", "SaveStateAutoHotkeyScript"),
            ("Load State", "LoadStateAutoHotkeyScript"), ("Swap Discs", "SwapDiscsAutoHotkeyScript"),
            ("Exit Game", "ExitAutoHotkeyScript"),
        };
        var stabs = new TabControl
        {
            Location = new Point(x, y), Size = new Size(S(650), S(226)),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            ItemSize = new Size(S(92), S(24)),
        };
        StyleDarkTabs(stabs, f);
        foreach (var (title, field) in scriptTabs)
        {
            var page = new TabPage(title) { BackColor = Bg, UseVisualStyleBackColor = false };
            // Merge across the selection: the common script, or "‹multiple values›" (grey) when they differ.
            bool differ = false; string merged;
            if (IsMulti)
            {
                string? first = null;
                foreach (var g in _editGames) { var v = (Safe(() => (g as ILiteBoxFields)?.GetField(field)) ?? ""); if (first == null) first = v; else if (first != v) { differ = true; break; } }
                merged = differ ? Multi : (first ?? "");
            }
            else merged = LchGet(field);
            var tb = new TextBox
            {
                Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true,
                BackColor = PanelC, ForeColor = differ ? SubFg : Fg, BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Consolas", 9.5f), ReadOnly = _readOnly,
                Text = differ ? Multi : merged.Replace("\r\n", "\n").Replace("\n", "\r\n"),
            };
            bool touched = false;
            if (IsMulti)
            {
                string baseTxt = tb.Text;
                if (differ) tb.Enter += (_, _) => { if (tb.Text == Multi) tb.SelectAll(); };
                var rb = ModalRevertButton(() => { tb.Text = baseTxt; }, new Point(S(628), S(2)));
                rb.Anchor = AnchorStyles.Top | AnchorStyles.Right;
                tb.TextChanged += (_, _) => { touched = tb.Text != baseTxt; tb.ForeColor = tb.Text == Multi ? SubFg : (touched ? ModifiedColor : Fg); rb.Visible = touched && !_readOnly; };
                page.Controls.Add(tb); page.Controls.Add(rb); rb.BringToFront();
            }
            else page.Controls.Add(tb);
            stabs.TabPages.Add(page);
            mVals.Add(() => IsMulti
                ? (field, (touched && tb.Text != Multi) ? tb.Text.Replace("\r\n", "\n") : (string?)null)
                : (field, tb.Text.Replace("\r\n", "\n")));
        }
        host.Controls.Add(stabs);

        // LiteBox tab — multi-aware (merges each option, writes to all on OK).
        var lbxIds = _editGames.Select(g => Safe(() => g.Id) ?? "").Where(id => id.Length > 0).ToArray();
        var (lbxPanel, lbxSave) = Gameplay.LiteBoxGameplayEditor.Build(Data.LiteBoxOption.ScopeGame,
            lbxIds, _s, Bg, Fg, SubFg, Field, _readOnly, Gameplay.GameplaySection.Pause);
        lbxPanel.Dock = DockStyle.Fill;
        pgLbx.Controls.Add(lbxPanel);

        var bottom = DialogButtons(f, out var ok, out var cancel);
        ok.Click += (_, _) =>
        {
            // Native fields + scripts: solo → pending (flushed by SaveLaunching); multi → straight to every game
            // (only changed values; getters return null to skip).
            foreach (var getv in mVals)
            {
                var (field, value) = getv();
                if (value == null) continue;
                if (IsMulti) foreach (var g in _editGames) { try { (g as ILiteBoxFields)?.SetField(field, value); } catch { } }
                else _lchPending[field] = value;
            }
            if (!_readOnly) { try { lbxSave?.Invoke(); } catch { } }
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

    private void LchEnable(TextBox? t, bool on)
    {
        if (t == null) return;
        t.ReadOnly = !on;
        t.BackColor = on ? Field : PanelC;
        t.TabStop = on;
        // A dirty-tracked field owns its text colour (grey "‹multiple values›" placeholder / red modified) via
        // RefreshFieldState — don't clobber it with Fg/SubFg here; the darker BackColor still signals disabled.
        if (_baseline.ContainsKey(t)) RefreshFieldState(t);
        else t.ForeColor = on ? Fg : SubFg;
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
        bool w = !_readOnly;
        if (IsMulti)
        {
            // Multi-select shows only the main Launching + DOSBox pages (no emulator sub-page), and the games
            // may have different emulators — so don't gate on any one game's emulator: keep every field editable
            // and hide the "Emulation is active" notes.
            if (_lchAppCaption != null) _lchAppCaption.Text = "Application Path:";
            LchEnable(_lchAppPath, w); LchEnable(_lchCmd, w); LchEnable(_lchCfgPath, w);
            if (_lchCfgBrowse != null) _lchCfgBrowse.Enabled = w;
            LchEnable(_lchCfgCmd, w);
            // NOT LchEnable(_lchUseDos / _lchUseEmu): the 3-state checkboxes own their AutoCheck (manual toggle)
            // and colour (RefreshFieldState) — LchEnable would reset both. Just the text fields go through it.
            LchEnable(_lchDosConf, w); LchEnable(_lchDosExe, w);
            // Emulator list + custom-command-line field: enabled unless the relevant 3-state box is Unchecked.
            if (_lchEmuCombo != null) _lchEmuCombo.Enabled = w && (_lchUseEmu == null || _lchUseEmu.CheckState != CheckState.Unchecked);
            if (_lchCustomCmdChk != null) _lchCustomCmdChk.Enabled = w;
            if (_lchCustomCmd != null) LchEnable(_lchCustomCmd, w && (_lchCustomCmdChk == null || _lchCustomCmdChk.CheckState != CheckState.Unchecked));
            LchEnable(_lchRoot, w);   // Root Folder editable in multi (no per-game emulator gating)
            if (_lchLaunchNote != null) _lchLaunchNote.Visible = false;
            if (_lchDosNote != null) _lchDosNote.Visible = false;
            if (_lchRootNote != null) _lchRootNote.Visible = false;
            return;
        }
        bool emuOn = LchEmuOn;
        if (_lchAppCaption != null) _lchAppCaption.Text = emuOn ? "ROM File (Emulation is enabled):" : "Application Path:";
        LchEnable(_lchAppPath, w);
        LchEnable(_lchCmd, w && !emuOn);
        LchEnable(_lchCfgPath, w && !emuOn);
        if (_lchCfgBrowse != null) _lchCfgBrowse.Enabled = w && !emuOn;
        LchEnable(_lchCfgCmd, w && !emuOn);
        if (_lchLaunchNote != null) _lchLaunchNote.Visible = emuOn;

        // _lchUseDos is NOT gated here: DOSBox stays clickable even with emulation on (checking it prompts to
        // turn emulation off). Its Enabled/colour/AutoCheck are owned by the build + the dirty-tracker, so
        // LchEnable must not touch it. The note becomes a hint about the switch-off.
        if (_lchDosNote != null) _lchDosNote.Visible = emuOn;

        LchEnable(_lchRoot, w && !emuOn);
        if (_lchRootNote != null) _lchRootNote.Visible = emuOn;

        bool useEmu = _lchUseEmu?.Checked ?? emuOn;
        if (_lchEmuCombo != null) _lchEmuCombo.Enabled = w && useEmu;
        // Real .Enabled (not LchEnable): _lchCustomCmdChk is a dirty-tracked 3-state box, LchEnable would reset
        // its AutoCheck/colour. The field still goes through LchEnable (it now preserves the tracked colour).
        if (_lchCustomCmdChk != null) _lchCustomCmdChk.Enabled = w && useEmu;
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
        if (_readOnly) return;

        // Main launching fields are dirty-tracked (revert + colour + ‹multiple values›): write each to EVERY
        // edited game, only when changed & not the placeholder. CommandLine's solo dual-meaning (emulator
        // override vs plain app params) is resolved below; in multi it's a plain application command-line.
        WriteLch(_lchAppPath, "ApplicationPath");
        WriteLch(_lchCfgPath, "ConfigurationPath");
        WriteLch(_lchCfgCmd, "ConfigurationCommandLine");
        if (IsMulti)
        {
            // Multi covers the main Launching + DOSBox + Emulation pages; mounts / overrides stay single-game.
            // CommandLine is one shared field (Launching "Application Command-Line" = Emulation custom params;
            // the two controls mirror). "Use custom params" unchecked for all → clear it; else write the value.
            if (_lchCustomCmdChk is { CheckState: CheckState.Unchecked } && Modified(_lchCustomCmdChk))
            {
                foreach (var g in _editGames) { try { (g as ILiteBoxFields)?.SetField("CommandLine", ""); } catch { } }
                LchRebaseChk(_lchCustomCmdChk); LchRebase(_lchCmd); LchRebase(_lchCustomCmd);
            }
            else { WriteLch(_lchCmd, "CommandLine"); WriteLch(_lchCustomCmd, "CommandLine"); }
            WriteLchBool(_lchUseDos, "UseDosBox");
            WriteLch(_lchDosConf, "DosBoxConfigurationPath");
            WriteLch(_lchDosExe, "CustomDosBoxVersionPath");
            WriteEmulatorMulti();
            EnforceDosBoxEmulatorExclusivityMulti();   // never leave a game with both DOSBox and an emulator
            WriteLch(_lchRoot, "RootFolder");
            // Startup/Pause override toggles (base window only; the Customize modals stay solo). Clearing the
            // per-game LiteBox overrides on an unchecked concern mirrors the solo save below.
            WriteOverrideMulti(_lchOvrStart, "OverrideDefaultStartupScreenSettings",
                new[] { "StartupStayOnTop", "ExitScreenEagerMs" }.Concat(Gameplay.SmartCaptureConfig.Keys).ToArray(), LchStartupFields);
            WriteOverrideMulti(_lchOvrPause, "OverrideDefaultPauseScreenSettings", LchPauseLiteBoxKeys, LchPauseFields);
            return;
        }

        var f = AppsGame as ILiteBoxFields;
        if (f == null) return;

        // Remaining single-game fields → pending (only pages that were actually built contribute).
        if (_lchUseDos != null) _lchPending["UseDosBox"] = LchB(_lchUseDos.Checked);
        if (_lchDosConf != null) _lchPending["DosBoxConfigurationPath"] = _lchDosConf.Text.Trim();
        if (_lchDosExe != null) _lchPending["CustomDosBoxVersionPath"] = _lchDosExe.Text.Trim();
        if (_lchRoot != null) _lchPending["RootFolder"] = _lchRoot.Text.Trim();
        if (_lchOvrStart != null) _lchPending["OverrideDefaultStartupScreenSettings"] = LchB(_lchOvrStart.Checked);
        if (_lchOvrPause != null) _lchPending["OverrideDefaultPauseScreenSettings"] = LchB(_lchOvrPause.Checked);

        // Unchecking an override RESETS the whole concern: its LB-native fields (the Customize modal's values)
        // AND its per-game LiteBox-only overrides are wiped, so re-checking gives a clean slate — and nothing
        // stale lingers. (The native fields are gated at launch by StartupOverride/PauseOverride anyway.)
        string gid = Safe(() => _editGames[0].Id) ?? "";
        if (!string.IsNullOrEmpty(gid))
        {
            void ClearGame(string k) { try { Data.LiteBoxOption.SetOverride(Data.LiteBoxOption.ScopeGame, gid, k, null); } catch { } }
            if (_lchOvrStart is { Checked: false })
            {
                foreach (var fld in LchStartupFields) _lchPending[fld] = "";   // wipe the native Startup fields
                ClearGame("StartupStayOnTop"); ClearGame("ExitScreenEagerMs");
                foreach (var k in Gameplay.SmartCaptureConfig.Keys) ClearGame(k);
            }
            if (_lchOvrPause is { Checked: false })
            {
                foreach (var fld in LchPauseFields) _lchPending[fld] = "";   // wipe the native Pause fields + scripts
                foreach (var k in LchPauseLiteBoxKeys) ClearGame(k);
            }
        }

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
        LchRebase(_lchCmd);   // CommandLine written via the dual-meaning path above → re-baseline so it reads clean
        SaveMounts();
    }

    private void ReloadLaunchingIfBuilt()
    {
        if (IsMulti) return;
        LchSnapshot();
        if (_lchAppPath != null) { _lchAppPath.Text = LchGet("ApplicationPath"); LchRebase(_lchAppPath); }
        if (_lchCmd != null) { _lchCmd.Text = LchGet("CommandLine"); LchRebase(_lchCmd); }
        if (_lchCfgPath != null) { _lchCfgPath.Text = LchGet("ConfigurationPath"); LchRebase(_lchCfgPath); }
        if (_lchCfgCmd != null) { _lchCfgCmd.Text = LchGet("ConfigurationCommandLine"); LchRebase(_lchCfgCmd); }
        if (_lchUseDos != null) { _lchUseDos.Checked = LchBool(LchGet("UseDosBox")); LchRebaseChk(_lchUseDos); }
        if (_lchDosConf != null) { _lchDosConf.Text = LchGet("DosBoxConfigurationPath"); LchRebase(_lchDosConf); }
        if (_lchDosExe != null) { _lchDosExe.Text = LchGet("CustomDosBoxVersionPath"); LchRebase(_lchDosExe); }
        if (_lchRoot != null) { _lchRoot.Text = LchGet("RootFolder"); LchRebase(_lchRoot); }
        string emu = LchGet("Emulator");
        if (_lchUseEmu != null) { _lchUseEmu.Checked = emu.Length > 0 && emu != Guid.Empty.ToString(); LchRebaseChk(_lchUseEmu); }
        if (_lchEmuCombo != null) { RefreshEmulatorCombo(emu); LchRebaseCbo(_lchEmuCombo); }
        if (_lchCustomCmdChk != null) { _lchCustomCmdChk.Checked = LchGet("CommandLine").Trim().Length > 0; LchRebaseChk(_lchCustomCmdChk); }
        if (_lchCustomCmd != null) { _lchCustomCmd.Text = LchGet("CommandLine"); LchRebase(_lchCustomCmd); }
        if (_lchOvrStart != null) { _lchOvrStart.Checked = LchBool(LchGet("OverrideDefaultStartupScreenSettings")); LchRebaseChk(_lchOvrStart); }
        if (_lchOvrPause != null) { _lchOvrPause.Checked = LchBool(LchGet("OverrideDefaultPauseScreenSettings")); LchRebaseChk(_lchOvrPause); }
        LoadMounts();
        UpdateLaunchingEnablement();
    }
}
