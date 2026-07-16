// Generic tagged logging for LiteBox — one funnel for every subsystem's trace.
//
// LiteBox already tags its Console output by hand ("[smartcapture] …", "[pause] …", "[store] …"); this
// centralises that convention so callers write LbLog.Info("smartcapture", …) instead of hand-formatting the
// prefix, and so a future change (levels, timestamps, per-tag filtering) happens in ONE place. Output still
// goes through Console.Out, i.e. into litebox-debug.log only when DebugLog / --debug is on (see Program.cs) —
// a normal launch writes nothing.
//
// Deliberately NOT part of the module system: logging is a cross-cutting role, available to every feature
// whether or not any module is enabled. (In ExtendDB the dedupe log lived inside Modules; here it is generic.)

#nullable enable

using System;
using System.Collections.Generic;

namespace LbApiHost.Host.Diag;

internal static class LbLog
{
    /// <summary>One tagged line: "[tag] message".</summary>
    public static void Info(string tag, string message) => Console.WriteLine($"[{tag}] {message}");

    /// <summary>A warning line: "[tag] WARN: message".</summary>
    public static void Warn(string tag, string message) => Console.WriteLine($"[{tag}] WARN: {message}");

    private static readonly HashSet<string> _once = new(StringComparer.Ordinal);

    /// <summary>Log at most ONCE per session for a given (tag, message) — for hot paths that would otherwise
    /// flood the log (the "fallback / skipped" pattern: a feature degrading because a dependency is off).</summary>
    public static void Once(string tag, string message)
    {
        var key = tag + "" + message;
        lock (_once) { if (!_once.Add(key)) return; }
        Console.WriteLine($"[{tag}] {message}");
    }
}
