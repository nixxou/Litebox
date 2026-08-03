// ─────────────────────────────────────────────────────────────────────────────
// ROM extractor (ArchiveMGS) — in-process armed-entry carrier. Slice R3.
// ─────────────────────────────────────────────────────────────────────────────
//
// The plugin armed a 60-second cross-process ArchiveLaunchContextRegistry because
// the Play-click (LaunchBox UI / web) and the emulator spawn (LaunchBox.exe) live in
// different processes. LiteBox OWNS the launch: the Play button and ResolveLaunch are
// the same in-process call stack, so the picked entry is passed as a single-shot
// carrier — NO timed registry, NO cross-process handoff.
//
// Arm() is called on the UI thread immediately before HostLaunch.Launch; Consume() is
// read ONCE on the launch WORKER thread inside RomExtractor.ResolveLaunch (Launch runs
// RunAndWait on a background thread — so this is a plain lock-guarded static, NOT a
// [ThreadStatic]). Single-shot: Consume clears the arm so a later un-armed launch of
// the same game falls back to auto-pick.
//
//   • entry != null            → launch THIS entry (PathInArchive identity; basename
//                                fallback). Auto-pick when it doesn't match the archive.
//   • entry == null, force=true → the "Clear → pure priority" path: auto-pick with
//                                last-played IGNORED (region/tag priority only).
//   • not armed                → Consume returns HasPick=false → normal auto-pick
//                                (last-played honoured).

#nullable enable

using System;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Rom;

/// <summary>The single-shot result of consuming an armed pick.</summary>
internal readonly struct RomPick
{
    public bool HasPick { get; init; }         // the Play button armed a selection for this game
    public string? Entry { get; init; }        // chosen entry (PathInArchive / basename), null = no explicit entry
    public bool ForcePriority { get; init; }   // ignore last-played (the "Clear" path)
}

internal static class RomLaunchPick
{
    private static readonly object _lock = new();
    private static string? _gameId;
    private static string? _appId;
    private static string? _entry;
    private static bool _force;
    private static bool _armed;

    /// <summary>Arm the pick for the imminent launch of <paramref name="game"/> (call right BEFORE
    /// HostLaunch.Launch). <paramref name="entry"/> null + <paramref name="forcePriority"/> true = the
    /// "Clear → pure priority" path. Overwrites any previous un-consumed arm.</summary>
    public static void Arm(IGame game, string? appId, string? entry, bool forcePriority)
    {
        string? gid = null; try { gid = game?.Id; } catch { }
        lock (_lock)
        {
            _gameId = gid; _appId = appId; _entry = entry; _force = forcePriority; _armed = true;
        }
    }

    /// <summary>Read the armed pick for <paramref name="game"/> exactly once, then clear it. Returns
    /// HasPick=false when nothing was armed for this game (→ caller auto-picks). Matching is by game id
    /// only (one launch at a time in this UI); the version appId is informational.</summary>
    public static RomPick Consume(IGame game)
    {
        string? gid = null; try { gid = game?.Id; } catch { }
        lock (_lock)
        {
            if (!_armed || string.IsNullOrEmpty(gid) || !string.Equals(_gameId, gid, StringComparison.Ordinal))
                return default;   // HasPick = false
            var pick = new RomPick { HasPick = true, Entry = _entry, ForcePriority = _force };
            _armed = false; _gameId = _appId = _entry = null; _force = false;   // single-shot
            return pick;
        }
    }

    /// <summary>Non-consuming check used before automatic M3U generation. An explicit archive entry means
    /// the user requested that one ROM, so a disc/side playlist must not replace it.</summary>
    public static bool HasExplicitEntry(IGame game)
    {
        string? gid = null; try { gid = game?.Id; } catch { }
        lock (_lock)
            return _armed && !string.IsNullOrEmpty(gid)
                && string.Equals(_gameId, gid, StringComparison.Ordinal)
                && !string.IsNullOrEmpty(_entry);
    }

    /// <summary>Drop any armed pick without consuming it (e.g. a launch that never reaches ResolveLaunch).</summary>
    public static void Clear()
    {
        lock (_lock) { _armed = false; _gameId = _appId = _entry = null; _force = false; }
    }
}
