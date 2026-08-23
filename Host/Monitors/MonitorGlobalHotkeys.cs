// System-wide hotkeys for monitor profiles — RegisterHotKey, opt-in per profile.
//
// The default binding is the app-wide message filter next door (MonitorHotkeys), which only fires while
// LiteBox has focus. That is the safe shape, and it is the wrong one for the case this file serves:
// switching to the "TV" profile from the couch while a game is in front.
//
// OPT-IN PER PROFILE, not a global switch, because the cost is not shared evenly. A registered hotkey is
// CONFISCATED from the whole system — bind Ctrl+Alt+1 and no game, no browser, nothing will ever see that
// combination again. That is a fine trade for one deliberate binding and a terrible one applied wholesale
// to every profile that happens to have a key.
//
// FAILURES ARE REPORTED. RegisterHotKey simply returns false when another process already owns the combo,
// which would otherwise produce a hotkey that quietly does nothing — the single most confusing outcome
// available here. Every refusal is logged and surfaced once.
//
// Ids are handed out from a private range and released on every refresh, so editing profiles repeatedly
// cannot leak registrations.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using LbApiHost.Host.Diag;
using LbApiHost.Host.Modules;

namespace LbApiHost.Host.Monitors;

internal static class MonitorGlobalHotkeys
{
    private const string Tag = "monitors";

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4;
    private const uint MOD_NOREPEAT = 0x4000;   // a held key fires once, not a burst of profile switches
    private const int FirstId = 0xB200;         // private range, clear of PauseManager's 0xB0B

    private static readonly object _gate = new();
    private static HotkeyWindow? _window;
    private static readonly Dictionary<int, string> _byId = new();   // hotkey id → profile id

    /// <summary>Register every profile that asked for a system-wide key, dropping whatever was registered
    /// before. Safe to call repeatedly — after a profile edit, and at boot.</summary>
    public static void Refresh()
    {
        lock (_gate)
        {
            Unregister();
            if (!LbModules.On(LbModule.Monitors)) return;

            bool wantRestore = MonitorHotkeys.RestoreHotkeyGlobal
                               && MonitorHotkeys.Parse(MonitorHotkeys.RestoreHotkey) != Keys.None;

            List<MonitorProfile> wanted;
            try
            {
                wanted = MonitorProfileStore.All()
                    .Where(p => p.HotkeyGlobal && !string.IsNullOrWhiteSpace(p.Hotkey))
                    .ToList();
            }
            catch { return; }
            if (wanted.Count == 0 && !wantRestore) return;

            _window ??= new HotkeyWindow(OnPressed);

            int id = FirstId;
            var refused = new List<string>();

            if (wantRestore)
            {
                var (rmod, rvk) = Split(MonitorHotkeys.Parse(MonitorHotkeys.RestoreHotkey));
                if (rvk != 0)
                {
                    if (RegisterHotKey(_window.Handle, id, rmod | MOD_NOREPEAT, rvk))
                    {
                        _byId[id] = RestoreToken;
                        LbLog.Info(Tag, $"global hotkey {MonitorHotkeys.RestoreHotkey} → restore original layout");
                        id++;
                    }
                    else refused.Add($"Restore Original Layout ({MonitorHotkeys.RestoreHotkey})");
                }
            }
            foreach (var p in wanted)
            {
                var (mod, vk) = Split(MonitorHotkeys.Parse(p.Hotkey));
                if (vk == 0) continue;

                if (RegisterHotKey(_window.Handle, id, mod | MOD_NOREPEAT, vk))
                {
                    _byId[id] = p.Id;
                    LbLog.Info(Tag, $"global hotkey {p.Hotkey} → \"{p.Name}\"");
                    id++;
                }
                else refused.Add($"{p.Name} ({p.Hotkey})");
            }

            if (refused.Count > 0)
            {
                string msg = "Global hotkey already taken by another application: " + string.Join(", ", refused);
                LbLog.Warn(Tag, msg);
                try { LiteBox.Notifications.NotificationCenter.Error(msg); } catch { }
            }
        }
    }

    /// <summary>Release everything (app shutdown, or module turned off).</summary>
    public static void Unregister()
    {
        lock (_gate)
        {
            if (_window != null)
                foreach (var id in _byId.Keys.ToList())
                    try { UnregisterHotKey(_window.Handle, id); } catch { }
            _byId.Clear();
        }
    }

    /// <summary>True when this profile owns a system-wide registration — the in-process filter then skips
    /// it, so a press while LiteBox has focus cannot be handled twice.</summary>
    public static bool IsGlobal(string profileId)
    {
        lock (_gate) return _byId.ContainsValue(profileId);
    }

    /// <summary>Sentinel standing in for "Restore Original Layout" in the id map — it is not a profile,
    /// but it is registered and dispatched exactly like one.</summary>
    private const string RestoreToken = "\u0000restore";

    private static void OnPressed(int id)
    {
        string profileId;
        lock (_gate) { if (!_byId.TryGetValue(id, out profileId!)) return; }

        if (profileId == RestoreToken) { MonitorHotkeys.RunRestore(); return; }

        var p = MonitorProfileStore.ById(profileId);
        if (p == null) return;

        // Off the message pump: applying a layout sleeps while the driver settles.
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                LbLog.Info(Tag, $"global hotkey → \"{p.Name}\"");
                var res = MonitorProfileApply.Apply(p);
                string text = p.Name + " — " + res.Message.ReplaceLineEndings("  ·  ");
                if (res.Ok) LiteBox.Notifications.NotificationCenter.Info(text, lifeSpanSeconds: 6);
                else LiteBox.Notifications.NotificationCenter.Error(text);
            }
            catch (Exception ex) { LbLog.Warn(Tag, "global hotkey apply failed: " + ex.Message); }
        });
    }

    /// <summary>Keys combo → the (modifiers, virtual-key) pair RegisterHotKey wants.</summary>
    private static (uint mod, uint vk) Split(Keys combo)
    {
        if (combo == Keys.None) return (0, 0);
        uint mod = 0;
        if ((combo & Keys.Control) != 0) mod |= MOD_CONTROL;
        if ((combo & Keys.Alt) != 0) mod |= MOD_ALT;
        if ((combo & Keys.Shift) != 0) mod |= MOD_SHIFT;
        return (mod, (uint)(combo & Keys.KeyCode));
    }

    /// <summary>Hidden window receiving WM_HOTKEY. Lives on the UI thread, like PauseManager's.</summary>
    private sealed class HotkeyWindow : NativeWindow
    {
        private const int WM_HOTKEY = 0x0312;
        private readonly Action<int> _cb;

        public HotkeyWindow(Action<int> cb)
        {
            _cb = cb;
            CreateHandle(new CreateParams());
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY) { try { _cb((int)m.WParam); } catch { } return; }
            base.WndProc(ref m);
        }
    }
}
