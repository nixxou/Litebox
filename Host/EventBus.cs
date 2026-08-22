// Fires LaunchBox system events to ISystemEventsPlugin instances. The event
// vocabulary is read straight off the SDK's SystemEventTypes static class (its
// const string fields), so we use the exact strings LaunchBox would.

using System;
using System.Collections.Generic;
using System.Reflection;
using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host;

internal static class EventBus
{
    /// <summary>Field-name -> event-string for every public const/static string on SystemEventTypes.</summary>
    public static Dictionary<string, string> Vocabulary()
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in typeof(SystemEventTypes).GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy))
        {
            if (f.FieldType != typeof(string)) continue;
            string val = null;
            try { val = (string)(f.IsLiteral ? f.GetRawConstantValue() : f.GetValue(null)); } catch { }
            if (val != null) d[f.Name] = val;
        }
        return d;
    }

    /// <summary>Deliver an event to every ISystemEventsPlugin — on the PLUGIN loop, never on ours. A handler
    /// is free to take its time (ThirdScreen builds a window and starts a video surface inside one), and it
    /// used to take that time on the UI thread, where it reads as a freeze. Fire-and-forget by design: no
    /// caller of this needs an answer, and waiting for one would put the stall straight back.</summary>
    public static void Fire(PluginRegistry reg, string evt)
    {
        var targets = reg.SystemEvents;
        if (targets.Count == 0) return;
        PluginUiThread.Post(() =>
        {
            foreach (var p in targets)
            {
                try { p.OnEventRaised(evt); }
                catch (Exception ex)
                {
                    Console.WriteLine($"[event] {p.GetType().Name}.OnEventRaised(\"{evt}\") threw: {ex.GetType().Name}: {ex.Message}");
                }
            }
        });
    }

    /// <summary>The loaded plugins, set once at boot. Kept here so anything that learns something worth
    /// telling them — the launch lifecycle, the web server — can say so without being handed a registry.</summary>
    public static PluginRegistry Registry;

    /// <summary>Fire by SystemEventTypes field name to whatever is loaded. No-op before boot finishes.</summary>
    public static void FireNamed(string fieldName) => FireNamed(Registry, fieldName);

    private static Dictionary<string, string> _vocab;

    /// <summary>Fire the event named by a SystemEventTypes FIELD ("SelectionChanged"), resolved to the
    /// string LaunchBox actually sends. Silent and free when no plugin listens, which is the common case:
    /// this is called on every settled selection.</summary>
    public static void FireNamed(PluginRegistry reg, string fieldName)
    {
        if (reg == null || reg.SystemEvents.Count == 0) return;
        _vocab ??= Vocabulary();
        Fire(reg, _vocab.TryGetValue(fieldName, out var v) && !string.IsNullOrEmpty(v) ? v : fieldName);
    }

    public static void FirePluginInitialized(PluginRegistry reg)
    {
        var vocab = Vocabulary();
        string evt = vocab.TryGetValue("PluginInitialized", out var v) && !string.IsNullOrEmpty(v) ? v : "PluginInitialized";
        Console.WriteLine($"[event] firing PluginInitialized (\"{evt}\") -> {reg.SystemEvents.Count} plugin(s)");
        Fire(reg, evt);
    }
}
