// Launch-button group for the detail pane, modelled on ExtendDB's LaunchBox-Web theme.
//
//   ▶ Play with <emu>          [▾]     ← full width; the ▾ caret SELECTS an emulator (two-step:
//   Version: <name>                       picking updates the label, Play launches). Default first.
//   ROM: <entry>                        ← Version / ROM are shorter. ROM only when ExtendDB is loaded
//                                          AND the launch source is an archive AND the selected
//                                          emulator extracts (autoExtract) — same gating as LB-Web.
//
// Two tiers:
//   • WITHOUT ExtendDB — Play (+ emulator picker) and Version only, from the SDK (emulators for the
//     platform, additional-apps as versions). No ROM (the host can't manage in-archive selection).
//   • WITH ExtendDB — adds the ROM button. The ROM data + gating come from HostRomBridge
//     (GetLaunchInfoJson, same shape as the web detail.json); the "More" picker is opened via
//     PickRomModal (returns the chosen entry); the selection is armed via ArmSelectedRom right
//     before the host's PlayGame, exactly like the web /launch body does.

#nullable enable

using System.Text.Json;
using LbApiHost.Host.Media;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host;

internal sealed class LaunchButtons : Panel
{
    private static readonly Color Bg      = Color.FromArgb(37, 37, 38);
    private static readonly Color PlayCol = Color.FromArgb(50, 110, 65);
    private static readonly Color CaretCol= Color.FromArgb(40, 90, 55);
    private static readonly Color SubCol  = Color.FromArgb(60, 60, 70);   // duller than Play (Version/ROM)
    private static readonly Color Fg      = Color.FromArgb(230, 230, 230);

    private readonly Button _play, _caret, _version, _rom;
    private readonly Action<IGame, IAdditionalApplication?, IEmulator?> _playGame;

    // Current subject + choices.
    private IGame? _game;
    private readonly List<IEmulator> _emus = new();
    private readonly List<(string? appId, string label, IAdditionalApplication? app)> _versions = new();  // [0] = Base
    private int _selEmu;
    private string? _selVerAppId;   // null = Base
    private string? _selRom;        // null = no pick (auto)
    private bool _forcePriority;

    // ExtendDB launch-info (parsed from HostRomBridge JSON). Empty when ExtendDB absent.
    private readonly Dictionary<string, bool> _emuAutoExtract = new(StringComparer.OrdinalIgnoreCase);
    private bool _mainPathIsArchive;
    private readonly Dictionary<string, bool> _verIsArchive = new(StringComparer.Ordinal);
    private string? _lastEmuId, _lastVerAppId, _lastRomEntry;
    private bool _romFeature;   // ExtendDB present + feature on

    public LaunchButtons(Action<IGame, IAdditionalApplication?, IEmulator?> playGame)
    {
        _playGame = playGame;
        Dock = DockStyle.Bottom;
        BackColor = Bg;
        Padding = new Padding(10, 6, 10, 8);
        Height = 96;

        // Stacked, reverse Dock.Top order so the first added sits at the bottom.
        _rom = MakeBtn("ROM", SubCol, 22, FontStyle.Regular);
        _rom.Click += (_, _) => OnRomClick();
        _rom.MouseUp += (_, e) => { if (e.Button == MouseButtons.Right) OnRomClear(); };
        Controls.Add(_rom);

        _version = MakeBtn("Version", SubCol, 22, FontStyle.Regular);
        _version.Click += (_, _) => ShowVersionMenu();
        Controls.Add(_version);

        // Play row: Play (fill) + caret (right).
        var playRow = new Panel { Dock = DockStyle.Top, Height = 34, BackColor = Bg, Margin = new Padding(0) };
        _caret = MakeBtn("▾", CaretCol, 34, FontStyle.Bold);
        _caret.Dock = DockStyle.Right; _caret.Width = 32;
        _caret.Click += (_, _) => ShowEmulatorMenu();
        _play = MakeBtn("Play", PlayCol, 34, FontStyle.Bold);
        _play.Dock = DockStyle.Fill;
        _play.Click += (_, _) => OnPlay();
        playRow.Controls.Add(_play);   // Fill added first
        playRow.Controls.Add(_caret);  // Right added last → docks first
        Controls.Add(playRow);
    }

    private static Button MakeBtn(string text, Color back, int height, FontStyle style) => new()
    {
        Text = text, Dock = DockStyle.Top, Height = height,
        FlatStyle = FlatStyle.Flat, BackColor = back, ForeColor = Fg,
        FlatAppearance = { BorderSize = 0 },
        Font = new Font("Segoe UI", style == FontStyle.Bold ? 9.5f : 8.5f, style),
        Margin = new Padding(0, 0, 0, 4), TextAlign = ContentAlignment.MiddleCenter,
    };

    /// <summary>Rebuild for a game. Pass the platform's emulators and the game's
    /// additional applications (both from the SDK — the host already enumerates
    /// these for its context menu).</summary>
    /// <summary>Hide the group (no game / a tree node selected).</summary>
    public void HideGame() { _game = null; Visible = false; }

    public void ShowFor(IGame? game, IReadOnlyList<IEmulator> emulators, IReadOnlyList<IAdditionalApplication> apps)
    {
        _game = game;
        _emus.Clear(); _versions.Clear();
        _emuAutoExtract.Clear(); _verIsArchive.Clear();
        _mainPathIsArchive = false; _lastEmuId = _lastVerAppId = _lastRomEntry = null;
        _selRom = null; _forcePriority = false; _selVerAppId = null; _selEmu = 0;

        if (game == null) { Visible = false; return; }

        // Emulators (default first), via the SDK.
        var defaultId = Safe(() => game.EmulatorId);
        foreach (var e in emulators)
        {
            if (e == null) continue;
            if (!string.IsNullOrEmpty(defaultId) && string.Equals(Safe(() => e.Id), defaultId, StringComparison.Ordinal))
                _emus.Insert(0, e);
            else _emus.Add(e);
        }

        // Versions: Base + each additional app.
        _versions.Add((null, "Base", null));
        foreach (var a in apps)
        {
            if (a == null) continue;
            var name = Safe(() => a.Name);
            _versions.Add((Safe(() => a.Id), string.IsNullOrWhiteSpace(name) ? "(version)" : name!, a));
        }

        // ExtendDB ROM layer (in-process, reflection — no HTTP).
        _romFeature = RomBridge.Available;
        if (_romFeature) ParseLaunchInfo(RomBridge.GetLaunchInfoJson(game));

        // Initial selection: last launch (ExtendDB) → default → first.
        _selEmu = ResolveInitialEmu();
        _selVerAppId = (_lastVerAppId != null && _versions.Any(v => v.appId == _lastVerAppId)) ? _lastVerAppId : null;
        if (_romFeature && RomAppliesFor(_selVerAppId, CurrentEmuId())) _selRom = _lastRomEntry;

        // Always show for a game — Play is available even with no platform
        // emulator (PC game → host resolves the default). Version/ROM self-hide.
        Visible = true;
        Refresh2();
    }

    private int ResolveInitialEmu()
    {
        if (_emus.Count == 0) return 0;
        if (!string.IsNullOrEmpty(_lastEmuId))
            for (int i = 0; i < _emus.Count; i++)
                if (string.Equals(Safe(() => _emus[i].Id), _lastEmuId, StringComparison.Ordinal)) return i;
        return 0;   // default is already first
    }

    private string? CurrentEmuId() => (_selEmu >= 0 && _selEmu < _emus.Count) ? Safe(() => _emus[_selEmu].Id) : null;
    private IEmulator? CurrentEmu() => (_selEmu >= 0 && _selEmu < _emus.Count) ? _emus[_selEmu] : null;
    private IAdditionalApplication? CurrentVersionApp()
    {
        var v = _versions.FirstOrDefault(x => x.appId == _selVerAppId);
        return v.app;
    }

    /// <summary>ROM selection applies for (version, emulator): the launch source is
    /// an archive (the version's path, or the main path for Base) AND the emulator
    /// extracts (autoExtract). Same gating as the LaunchBox-Web ROM button.</summary>
    private bool RomAppliesFor(string? verAppId, string? emuId)
    {
        if (!_romFeature) return false;
        bool srcArchive = verAppId == null ? _mainPathIsArchive
                                           : (_verIsArchive.TryGetValue(verAppId, out var a) && a);
        if (!srcArchive) return false;
        return emuId != null && _emuAutoExtract.TryGetValue(emuId, out var ax) && ax;
    }

    // ── Painting the labels / visibility ──────────────────────────────
    private void Refresh2()
    {
        var emu = CurrentEmu();
        _play.Text = emu != null ? "▶  Play with " + (Safe(() => emu.Title) ?? "Emulator") : "▶  Play";
        _caret.Visible = _emus.Count > 1;

        // Version: only when there's an alternative beyond Base.
        if (_versions.Count > 1)
        {
            var cur = _versions.FirstOrDefault(v => v.appId == _selVerAppId);
            _version.Text = _selVerAppId == null ? "Version" : "Version: " + cur.label;
            _version.Visible = true;
        }
        else _version.Visible = false;

        // ROM: only when it applies for the current (version, emulator).
        bool romOk = RomAppliesFor(_selVerAppId, CurrentEmuId());
        if (romOk)
        {
            _rom.Text = string.IsNullOrEmpty(_selRom) ? "ROM" : "ROM: " + _selRom;
            _rom.Visible = true;
        }
        else { _rom.Visible = false; if (!_romFeature) _selRom = null; }

        // Size the docked panel to exactly the visible rows (no wasted space).
        int h = Padding.Vertical + 38;          // play row (34 + 4 margin)
        if (_version.Visible) h += 26;
        if (_rom.Visible) h += 26;
        Height = h;
    }

    // ── Emulator picker (two-step) ────────────────────────────────────
    private void ShowEmulatorMenu()
    {
        if (_emus.Count == 0) return;
        var menu = NewMenu();
        var defId = _game != null ? Safe(() => _game.EmulatorId) : null;
        for (int i = 0; i < _emus.Count; i++)
        {
            int idx = i;
            var title = Safe(() => _emus[i].Title) ?? "Emulator";
            bool isDef = !string.IsNullOrEmpty(defId) && string.Equals(Safe(() => _emus[i].Id), defId, StringComparison.Ordinal);
            var it = new ToolStripMenuItem(isDef ? title + "  (default)" : title) { Checked = idx == _selEmu };
            it.Click += (_, _) => { _selEmu = idx; Refresh2(); };   // select only — no launch
            menu.Items.Add(it);
        }
        menu.Show(_caret, new Point(0, 0), ToolStripDropDownDirection.AboveLeft);
    }

    // ── Version picker ────────────────────────────────────────────────
    private void ShowVersionMenu()
    {
        if (_versions.Count <= 1) return;
        var menu = NewMenu();
        foreach (var v in _versions)
        {
            var cv = v;
            var it = new ToolStripMenuItem(v.appId == null ? "Base" : v.label) { Checked = v.appId == _selVerAppId };
            it.Click += (_, _) =>
            {
                _selVerAppId = cv.appId;
                _selRom = null; _forcePriority = false;   // version changed → archive may differ
                Refresh2();
            };
            menu.Items.Add(it);
        }
        menu.Show(_version, new Point(0, 0), ToolStripDropDownDirection.AboveLeft);
    }

    // ── ROM pick / clear ──────────────────────────────────────────────
    // Quick dropdown modelled on LB-Web's buildLbRomSubmenu: ✕ Clear, the last
    // launched ROM (↻), ALL favourites (★), then up to RomQuickMax pure-priority
    // entries, then "More…" which opens the advanced picker (sortable table).
    private const int RomQuickMax = 7;

    private sealed record RomEntry(string FileName, long Size, bool IsFavorite, bool IsLastPlayed);

    private void OnRomClick()
    {
        if (_game == null || !RomAppliesFor(_selVerAppId, CurrentEmuId())) return;

        var entries = FetchEntries();
        if (entries == null || entries.Count == 0) { OpenAdvancedPicker(); return; }   // no quick data → straight to the table

        var menu = NewMenu();

        // ✕ Clear — drop the pick AND ignore last-played (pure priority), like the web.
        var clear = new ToolStripMenuItem("✕  Clear");
        clear.Click += (_, _) => { _selRom = null; _forcePriority = true; Refresh2(); };
        menu.Items.Add(clear);
        menu.Items.Add(new ToolStripSeparator());

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Push(RomEntry e, string glyph)
        {
            if (e == null || !seen.Add(e.FileName)) return;
            var it = new ToolStripMenuItem(glyph + e.FileName) { Checked = string.Equals(_selRom, e.FileName, StringComparison.OrdinalIgnoreCase) };
            it.Click += (_, _) => { _selRom = e.FileName; _forcePriority = false; Refresh2(); };
            menu.Items.Add(it);
        }

        // 1. last launched ROM (lastLaunch, else the most recently played).
        RomEntry last = null;
        if (!string.IsNullOrEmpty(_lastRomEntry))
            last = entries.FirstOrDefault(e => string.Equals(e.FileName, _lastRomEntry, StringComparison.OrdinalIgnoreCase));
        last ??= entries.FirstOrDefault(e => e.IsLastPlayed);
        if (last != null) Push(last, "↻ ");

        // 2. all favourites.
        foreach (var e in entries.Where(e => e.IsFavorite)) Push(e, "★ ");

        // 3. up to RomQuickMax in pure priority (neither last, fav, nor played).
        int prio = 0;
        foreach (var e in entries)
        {
            if (prio >= RomQuickMax) break;
            if (e.IsFavorite || e.IsLastPlayed || seen.Contains(e.FileName)) continue;
            Push(e, "");
            prio++;
        }

        // More… → the advanced picker (full sortable/filterable table).
        if (entries.Count > seen.Count)
        {
            menu.Items.Add(new ToolStripSeparator());
            var more = new ToolStripMenuItem("More…  (" + entries.Count + ")");
            more.Click += (_, _) => OpenAdvancedPicker();
            menu.Items.Add(more);
        }

        menu.Show(_rom, new Point(0, 0), ToolStripDropDownDirection.AboveLeft);
    }

    private void OpenAdvancedPicker()
    {
        if (_game == null) return;
        var chosen = RomBridge.PickRomModal(_game, _selVerAppId);   // selection mode ("Select", not Play)
        if (chosen == null) return;                                  // cancelled
        _selRom = chosen; _forcePriority = false;
        Refresh2();
    }

    private List<RomEntry>? FetchEntries()
    {
        try
        {
            var json = RomBridge.GetArchiveEntriesJson(_game!, _selVerAppId);
            if (string.IsNullOrEmpty(json)) return null;
            using var doc = JsonDocument.Parse(json!);
            if (!doc.RootElement.TryGetProperty("entries", out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
            var list = new List<RomEntry>();
            foreach (var e in arr.EnumerateArray())
            {
                var name = e.TryGetProperty("fileName", out var n) ? n.GetString() : null;
                if (string.IsNullOrEmpty(name)) continue;
                long size = e.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : 0;
                bool fav = e.TryGetProperty("isFavorite", out var f) && f.ValueKind == JsonValueKind.True;
                bool lp = e.TryGetProperty("isLastPlayed", out var l) && l.ValueKind == JsonValueKind.True;
                list.Add(new RomEntry(name!, size, fav, lp));
            }
            return list;
        }
        catch { return null; }
    }

    private void OnRomClear()
    {
        if (string.IsNullOrEmpty(_selRom) && !_forcePriority) return;
        _selRom = null; _forcePriority = true;   // clear → ignore last-played, pure priority (like LB-Web)
        Refresh2();
    }

    // ── Launch ────────────────────────────────────────────────────────
    private void OnPlay()
    {
        if (_game == null) return;
        // Arm the ROM selection in ExtendDB BEFORE launching (the hook applies it).
        if (_romFeature && RomAppliesFor(_selVerAppId, CurrentEmuId()) && (!string.IsNullOrEmpty(_selRom) || _forcePriority))
            RomBridge.ArmSelectedRom(_game, _selVerAppId, _selRom, _forcePriority);
        _playGame(_game, CurrentVersionApp(), CurrentEmu());   // CurrentEmu null → host resolves the default
    }

    // ── launch-info JSON (HostRomBridge) ──────────────────────────────
    private void ParseLaunchInfo(string? json)
    {
        if (string.IsNullOrEmpty(json)) return;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("launchOptions", out var lo) && lo.ValueKind == JsonValueKind.Object)
            {
                if (lo.TryGetProperty("mainPathIsArchive", out var mp)) _mainPathIsArchive = mp.ValueKind == JsonValueKind.True;
                if (lo.TryGetProperty("emulators", out var es) && es.ValueKind == JsonValueKind.Array)
                    foreach (var e in es.EnumerateArray())
                    {
                        var id = e.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                        bool ax = e.TryGetProperty("autoExtract", out var axEl) && axEl.ValueKind == JsonValueKind.True;
                        if (!string.IsNullOrEmpty(id)) _emuAutoExtract[id!] = ax;
                    }
                if (lo.TryGetProperty("versions", out var vs) && vs.ValueKind == JsonValueKind.Array)
                    foreach (var v in vs.EnumerateArray())
                    {
                        var appId = v.TryGetProperty("appId", out var aEl) ? aEl.GetString() : null;
                        bool arc = v.TryGetProperty("isArchive", out var arEl) && arEl.ValueKind == JsonValueKind.True;
                        if (!string.IsNullOrEmpty(appId)) _verIsArchive[appId!] = arc;
                    }
            }
            if (root.TryGetProperty("lastLaunch", out var ll) && ll.ValueKind == JsonValueKind.Object)
            {
                _lastEmuId = ll.TryGetProperty("emulatorId", out var le) ? le.GetString() : null;
                _lastVerAppId = ll.TryGetProperty("appId", out var la) ? la.GetString() : null;
                _lastRomEntry = ll.TryGetProperty("archiveEntry", out var lr) ? lr.GetString() : null;
            }
        }
        catch { }
    }

    private ContextMenuStrip NewMenu() => new()
    {
        BackColor = Color.FromArgb(45, 45, 48), ForeColor = Fg,
        Renderer = new ToolStripProfessionalRenderer(),
    };

    private static T? Safe<T>(Func<T> f) { try { return f(); } catch { return default; } }
}
