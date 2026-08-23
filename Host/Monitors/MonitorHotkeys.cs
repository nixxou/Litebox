// In-process hotkeys that apply a monitor profile — the DEFAULT half of the binding.
//
// Bound to the app-wide message filter (Host\HostHotKeys), so the scope is the same as every other
// LiteBox hotkey: live while LiteBox has focus, silent while a game is in front. That is the safe default,
// because a key bound here costs nothing to anyone else.
//
// A profile that ticks "global hotkey" is handled by MonitorGlobalHotkeys instead (RegisterHotKey), and
// this filter skips it. The two are deliberately not interchangeable: a system-wide key is CONFISCATED
// from every other application, so it is opted into one profile at a time rather than switched on for all.
//
// A hotkey is independent of the "Show in the Tools menu" flag: the menu is one way in, a key is another,
// and someone binding a key to a profile they deliberately hid from the menu means exactly that.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using LbApiHost.Host.Diag;
using LbApiHost.Host.Modules;

namespace LbApiHost.Host.Monitors;

internal static class MonitorHotkeys
{
    private const string Tag = "monitors";
    private const int DebounceMs = 400;

    private static DateTime _lastUtc = DateTime.MinValue;

    /// <summary>Applies the profile bound to <paramref name="pressed"/>, if any. Returns true when the
    /// key was consumed — including a debounced repeat, so a held key never queues a burst of switches.</summary>
    public static bool TryHandle(Keys pressed)
    {
        if (pressed == Keys.None) return false;
        if (!LbModules.On(LbModule.Monitors)) return false;

        // Restore first: it is the way OUT, and a combination shared with a profile should undo rather
        // than apply. Skipped when it is registered system-wide — that path handles it.
        if (!RestoreHotkeyGlobal)
        {
            var rk = Parse(RestoreHotkey);
            if (rk != Keys.None && rk == pressed)
            {
                var now0 = DateTime.UtcNow;
                if ((now0 - _lastUtc).TotalMilliseconds < DebounceMs) return true;
                _lastUtc = now0;
                RunRestore();
                return true;
            }
        }

        MonitorProfile? hit;
        try
        {
            // A profile registered system-wide is handled by MonitorGlobalHotkeys; Windows delivers it as
            // WM_HOTKEY and this filter never sees the key anyway — skipping it makes that explicit rather
            // than relying on it.
            hit = MonitorProfileStore.All()
                .FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.Hotkey)
                                     && !MonitorGlobalHotkeys.IsGlobal(p.Id)
                                     && Parse(p.Hotkey) == pressed);
        }
        catch { return false; }
        if (hit == null) return false;

        // Consume regardless of the debounce: the key IS ours, and letting a swallowed repeat fall
        // through to the list view would move the selection instead of doing nothing.
        var now = DateTime.UtcNow;
        if ((now - _lastUtc).TotalMilliseconds < DebounceMs) return true;
        _lastUtc = now;

        try
        {
            LbLog.Info(Tag, $"hotkey {hit.Hotkey} → \"{hit.Name}\"");
            // Off the UI thread: applying a layout sleeps for the driver to settle, and the message
            // filter runs on the pump — blocking here would freeze the window mid-switch.
            System.Threading.Tasks.Task.Run(() =>
            {
                var res = MonitorProfileApply.Apply(hit);
                string text = hit.Name + " — " + res.Message.Replace("\n", "  ·  ");
                if (res.Ok) LiteBox.Notifications.NotificationCenter.Info(text, lifeSpanSeconds: 6);
                else LiteBox.Notifications.NotificationCenter.Error(text);
            });
        }
        catch (Exception ex) { LbLog.Warn(Tag, "hotkey apply failed: " + ex.Message); }
        return true;
    }

    public const string KeyRestore = "MonitorRestoreHotkey";
    public const string KeyRestoreGlobal = "MonitorRestoreHotkeyGlobal";

    /// <summary>The combo bound to "Restore Original Layout", or "" when unbound. It is a GLOBAL option
    /// rather than a profile field because it belongs to no profile — it undoes whichever one is in force.</summary>
    public static string RestoreHotkey
    {
        get { try { return Data.LiteBoxOptionsDb.GetGlobal(KeyRestore) ?? ""; } catch { return ""; } }
    }

    /// <summary>Whether that combo is taken from the whole system. Same trade as a profile's own key.</summary>
    public static bool RestoreHotkeyGlobal
    {
        get { try { return string.Equals(Data.LiteBoxOptionsDb.GetGlobal(KeyRestoreGlobal), "true", StringComparison.OrdinalIgnoreCase); } catch { return false; } }
    }

    /// <summary>Run the restore and report it, from whichever binding fired.</summary>
    public static void RunRestore()
    {
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var res = MonitorProfileApply.Restore();
                string text = "Original layout — " + res.Message.ReplaceLineEndings("  ·  ");
                if (res.Ok) LiteBox.Notifications.NotificationCenter.Info(text, lifeSpanSeconds: 6);
                else LiteBox.Notifications.NotificationCenter.Error(text);
            }
            catch (Exception ex) { LbLog.Warn(Tag, "restore hotkey failed: " + ex.Message); }
        });
    }

    /// <summary>"Ctrl+Alt+1" → the Keys combo. Keys.None when the string names nothing usable — same
    /// format HotkeyCaptureBox produces and PauseManager parses.</summary>
    public static Keys Parse(string? combo)
    {
        if (string.IsNullOrWhiteSpace(combo)) return Keys.None;
        Keys mods = Keys.None, key = Keys.None;
        foreach (var part in combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= Keys.Control; break;
                case "alt": mods |= Keys.Alt; break;
                case "shift": mods |= Keys.Shift; break;
                default:
                    if (Enum.TryParse<Keys>(part, true, out var k)) key = k;
                    break;
            }
        }
        return key == Keys.None ? Keys.None : key | mods;
    }

    /// <summary>Where a profile's key is live, for the editor to say so plainly.</summary>
    public static string ScopeText(MonitorProfile p)
        => string.IsNullOrWhiteSpace(p.Hotkey) ? ""
         : p.HotkeyGlobal ? "Works everywhere, including while a game runs."
         : "Works while LiteBox has focus, not while a game is in front.";

    /// <summary>Profiles whose hotkey collides with another's — the editor greys nothing, it just says
    /// so, because the fix (change one of them) is the user's call and not always urgent.</summary>
    public static List<string> Conflicts(IEnumerable<MonitorProfile> profiles)
        => profiles.Where(p => !string.IsNullOrWhiteSpace(p.Hotkey))
                   .GroupBy(p => Parse(p.Hotkey))
                   .Where(g => g.Key != Keys.None && g.Count() > 1)
                   .Select(g => string.Join(" / ", g.Select(p => p.Name)) + $" share {g.First().Hotkey}")
                   .ToList();
}
