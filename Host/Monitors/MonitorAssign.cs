// Which monitor profile a launch should use — the assignment store and its resolution order.
//
// THREE PLACES can name a profile, and the most specific wins:
//
//     version  (an additional version / add-app)   ← most specific
//     game
//     emulator                                     ← most general
//
// Each level holds one of four answers, and the distinction between the last two is the whole point:
//
//     (absent)   no opinion — ask the level below
//     none       explicitly NO profile, even if a level below has one
//     <id>       this saved profile
//     custom     the inline configuration stored beside the assignment (emulators only)
//
// "none" is what makes the chain usable. An emulator can carry a profile for all its games while one
// particular game opts out — without it, the only way to exempt a game would be to strip the emulator's
// assignment and re-add it everywhere else.
//
// "custom" exists because an emulator is where a one-off arrangement is most often needed and least
// worth cluttering the global profile list with: a display mode and a sound card for THIS emulator, not
// a named thing to pick from a menu.
//
// Nothing here applies anything — it answers "which profile", and the launch path (HostServices) does
// the rest through MonitorProfileApply's game scope.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using LbApiHost.Host.Data;
using LbApiHost.Host.Diag;
using LbApiHost.Host.Modules;

namespace LbApiHost.Host.Monitors;

/// <summary>What one entity says about monitor profiles.</summary>
internal enum AssignKind { None, Profile, Custom }

internal readonly record struct Assignment(AssignKind Kind, string ProfileId)
{
    public static readonly Assignment Unset = new((AssignKind)(-1), "");
    public bool IsSet => (int)Kind >= 0;
}

internal static class MonitorAssign
{
    private const string Tag = "monitors";

    public const string KeyAssign = "MonitorProfileAssign";
    public const string KeyCustom = "MonitorProfileCustom";

    private const string ValueNone = "none";
    private const string ValueCustom = "custom";

    // ── read / write one entity ──────────────────────────────────────────────

    /// <summary>The assignment stored on one entity, or <see cref="Assignment.Unset"/> when it has none.</summary>
    public static Assignment Get(string scope, string entityId)
    {
        if (string.IsNullOrEmpty(entityId)) return Assignment.Unset;
        string raw;
        try { raw = LiteBoxOptionsDb.Get(scope, entityId, KeyAssign) ?? ""; }
        catch { return Assignment.Unset; }

        if (raw.Length == 0) return Assignment.Unset;
        if (string.Equals(raw, ValueNone, StringComparison.OrdinalIgnoreCase)) return new Assignment(AssignKind.None, "");
        if (string.Equals(raw, ValueCustom, StringComparison.OrdinalIgnoreCase)) return new Assignment(AssignKind.Custom, "");
        return new Assignment(AssignKind.Profile, raw);
    }

    /// <summary>Store (or clear, with <see cref="Assignment.Unset"/>) one entity's assignment.</summary>
    public static void Set(string scope, string entityId, Assignment a)
    {
        if (string.IsNullOrEmpty(entityId)) return;
        try
        {
            string? v = !a.IsSet ? null
                      : a.Kind == AssignKind.None ? ValueNone
                      : a.Kind == AssignKind.Custom ? ValueCustom
                      : (a.ProfileId.Length > 0 ? a.ProfileId : null);
            LiteBoxOptionsDb.Set(scope, entityId, KeyAssign, v);
        }
        catch (Exception ex) { LbLog.Warn(Tag, $"assign write failed ({scope}/{entityId}): {ex.Message}"); }
    }

    /// <summary>The inline profile of an entity using "custom" (emulators). Null when none is stored.</summary>
    public static MonitorProfile? GetCustom(string scope, string entityId)
    {
        if (string.IsNullOrEmpty(entityId)) return null;
        try { return LiteBoxOptionsDb.GetJson<MonitorProfile>(scope, entityId, KeyCustom); }
        catch { return null; }
    }

    public static void SetCustom(string scope, string entityId, MonitorProfile? p)
    {
        if (string.IsNullOrEmpty(entityId)) return;
        try { LiteBoxOptionsDb.SetJson(scope, entityId, KeyCustom, p); }
        catch (Exception ex) { LbLog.Warn(Tag, $"custom write failed ({scope}/{entityId}): {ex.Message}"); }
    }

    // ── the one-shot override ────────────────────────────────────────────────
    //
    // "Run the NEXT game as…" — set from the Tools menu, consumed by the first launch that follows, and
    // gone. It sits ABOVE version / game / emulator because it is the most deliberate statement there is:
    // someone standing at the machine, right now, saying "not the usual arrangement, this once".
    //
    // In memory only, deliberately. A one-shot that survived a restart would fire on some launch hours
    // later, long after the intent behind it had expired — the failure mode of every sticky "just this
    // once" setting.

    private static readonly object _nextGate = new();
    private static Assignment _nextLaunch = Assignment.Unset;

    /// <summary>What the next launch will use, or <see cref="Assignment.Unset"/> when nothing is armed.</summary>
    public static Assignment NextLaunch { get { lock (_nextGate) return _nextLaunch; } }

    /// <summary>Arm (or, with <see cref="Assignment.Unset"/>, disarm) the one-shot.</summary>
    public static void SetNextLaunch(Assignment a)
    {
        lock (_nextGate) _nextLaunch = a;
        LbLog.Info(Tag, !a.IsSet ? "next-launch override cancelled"
                      : a.Kind == AssignKind.None ? "next launch: no monitor profile"
                      : $"next launch: profile {MonitorProfileStore.ById(a.ProfileId)?.Name ?? a.ProfileId}");
    }

    /// <summary>Read AND clear the one-shot — called by the launch resolver, once.</summary>
    private static Assignment TakeNextLaunch()
    {
        lock (_nextGate) { var a = _nextLaunch; _nextLaunch = Assignment.Unset; return a; }
    }

    // ── resolution ───────────────────────────────────────────────────────────

    /// <summary>The profile a launch should apply, or null for "leave the desktop alone".
    ///
    /// Walks version → game → emulator and stops at the FIRST level that has an opinion, including an
    /// explicit "none" — that is the whole reason the chain has three levels rather than one.</summary>
    public static MonitorProfile? Resolve(string? gameId, string? versionId, string? emulatorId)
    {
        if (!LbModules.On(LbModule.Monitors)) return null;

        // The one-shot first, and consumed whether or not it names a profile — arming "no profile for the
        // next game" has to be spendable too, or it would silently stay armed for every launch after.
        var once = TakeNextLaunch();
        if (once.IsSet)
        {
            if (once.Kind == AssignKind.None) { LbLog.Info(Tag, "launch: one-shot says no monitor profile"); return null; }
            var chosen = MonitorProfileStore.ById(once.ProfileId);
            if (chosen != null) { LbLog.Info(Tag, $"launch: one-shot profile \"{chosen.Name}\""); return chosen; }
            LbLog.Warn(Tag, "launch: the one-shot named a profile that no longer exists — falling through");
        }

        foreach (var (scope, id) in new[]
                 {
                     (LiteBoxOption.ScopeVersion,  versionId ?? ""),
                     (LiteBoxOption.ScopeGame,     gameId ?? ""),
                     (LiteBoxOption.ScopeEmulator, emulatorId ?? ""),
                 })
        {
            var a = Get(scope, id);
            if (!a.IsSet) continue;

            switch (a.Kind)
            {
                case AssignKind.None:
                    LbLog.Info(Tag, $"launch: {scope} says no monitor profile");
                    return null;

                case AssignKind.Custom:
                    var custom = GetCustom(scope, id);
                    if (custom != null) { custom.Name = custom.Name.Length > 0 ? custom.Name : $"{scope} settings"; return custom; }
                    LbLog.Warn(Tag, $"launch: {scope}/{id} is set to custom but has no configuration — ignored");
                    continue;

                default:
                    var p = MonitorProfileStore.ById(a.ProfileId);
                    if (p != null) return p;
                    LbLog.Warn(Tag, $"launch: {scope}/{id} names a profile that no longer exists — ignored");
                    continue;
            }
        }
        return null;
    }

    // ── the Assignments page ─────────────────────────────────────────────────

    /// <summary>One row of the Assignments list.</summary>
    internal sealed record Row(string Scope, string EntityId, string EntityName, string What);

    /// <summary>Every assignment in one scope, resolved to display names through <paramref name="nameOf"/>.
    /// An entity whose name cannot be resolved any more is still listed — as an id — because a dangling
    /// assignment is exactly the thing this page exists to clean up.</summary>
    public static List<Row> All(string scope, Func<string, string?> nameOf)
    {
        var rows = new List<Row>();
        Dictionary<string, string> raw;
        try { raw = LiteBoxOptionsDb.AllOf(scope, KeyAssign); }
        catch { return rows; }

        foreach (var kv in raw)
        {
            var a = Get(scope, kv.Key);
            if (!a.IsSet) continue;
            string what = a.Kind switch
            {
                AssignKind.None => "(no monitor profile)",
                AssignKind.Custom => "custom settings",
                _ => MonitorProfileStore.ById(a.ProfileId)?.Name ?? $"<deleted profile {a.ProfileId}>",
            };
            rows.Add(new Row(scope, kv.Key, nameOf(kv.Key) ?? $"<unknown {kv.Key}>", what));
        }
        return rows.OrderBy(r => r.EntityName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Drop an entity's assignment AND its inline configuration.</summary>
    public static void Clear(string scope, string entityId)
    {
        Set(scope, entityId, Assignment.Unset);
        SetCustom(scope, entityId, null);
    }
}
