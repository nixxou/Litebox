// The fields LiteBox does not model, kept anyway.
//
// A game's child collections — additional applications, alternate names, mounts, custom fields —
// have no stable identity in the XML, so an edit to any one of them is journalled as a whole-
// collection "replace" and the collection is rebuilt from scratch at flush. That rebuild can only
// emit what the model knows about, which means every field LaunchBox writes and LiteBox has never
// heard of is dropped for that game the first time anything touches it.
//
// Today the loss is small and harmless: <GogAppId>, <OriginAppId> and <OriginInstallPath> lead
// every one of the 9801 additional applications in the real data, and all 9801 are empty. Nothing
// reads them. But the same mechanism would silently eat a field LaunchBox adds in a future version,
// for exactly the games the user edits — which is the failure this project is supposed to be built
// against, not one it should ship.
//
// So the unmodelled fields are captured verbatim at load, ride along in the op's JSON payload, and
// are written back untouched. Empty ones are kept as empty elements rather than dropped: that is
// what LaunchBox emits, and the point here is to give back exactly what was taken.
//
// POSITION. Extras are re-emitted BEFORE the modelled fields, because that is where LaunchBox puts
// the ones we can observe. For a field that does not exist yet the position is unknowable either
// way — but its content survives, which is what this is for.

#nullable enable

using System;
using System.Collections.Generic;
using System.Xml.Linq;

namespace LbApiHost.Host.Data;

internal static class ChildExtras
{
    /// <summary>Every child element of <paramref name="el"/> whose name is not part of the modelled
    /// field set for <paramref name="entity"/>, in document order. Null when there are none, so the
    /// common case costs no allocation.</summary>
    public static Dictionary<string, string>? Capture(XElement el, string entity)
    {
        if (el == null || !GameStore.ChildFieldOrder.TryGetValue(entity, out var known)) return null;
        Dictionary<string, string>? extra = null;
        foreach (var child in el.Elements())
        {
            string name = child.Name.LocalName;
            if (Array.IndexOf(known, name) >= 0) continue;
            (extra ??= new Dictionary<string, string>(StringComparer.Ordinal))[name] = child.Value;
        }
        return extra;
    }

    /// <summary>The unmodelled fields LaunchBox writes on every additional application it creates —
    /// present and empty on all 9801 in the real data. Seeded onto a new one so it looks like one of
    /// theirs; a captured element brings its own and never reaches this.</summary>
    public static Dictionary<string, string> NewAdditionalApplication() =>
        new(StringComparer.Ordinal) { ["GogAppId"] = "", ["OriginAppId"] = "", ["OriginInstallPath"] = "" };

    /// <summary>Pulls the extras back out of a record read from an op's JSON: whatever is not a
    /// modelled field name.</summary>
    public static Dictionary<string, string>? From(Dictionary<string, string> rec, string entity)
    {
        if (rec == null || !GameStore.ChildFieldOrder.TryGetValue(entity, out var known)) return null;
        Dictionary<string, string>? extra = null;
        foreach (var kv in rec)
            if (Array.IndexOf(known, kv.Key) < 0)
                (extra ??= new Dictionary<string, string>(StringComparer.Ordinal))[kv.Key] = kv.Value ?? "";
        return extra;
    }
}
