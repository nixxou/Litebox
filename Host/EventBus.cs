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

    public static void Fire(PluginRegistry reg, string evt)
    {
        foreach (var p in reg.SystemEvents)
        {
            try { p.OnEventRaised(evt); }
            catch (Exception ex)
            {
                Console.WriteLine($"[event] {p.GetType().Name}.OnEventRaised(\"{evt}\") threw: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    public static void FirePluginInitialized(PluginRegistry reg)
    {
        var vocab = Vocabulary();
        string evt = vocab.TryGetValue("PluginInitialized", out var v) && !string.IsNullOrEmpty(v) ? v : "PluginInitialized";
        Console.WriteLine($"[event] firing PluginInitialized (\"{evt}\") -> {reg.SystemEvents.Count} plugin(s)");
        Fire(reg, evt);
    }
}
