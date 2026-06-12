// Host-side facade over the LaunchBox EMULATOR-INTEGRATION plugins (the
// "<Emulator> LaunchBox Integration" DLLs: RetroArch, Dolphin, MAME, …).
//
// Those DLLs subclass the PUBLIC SDK abstract class
// Unbroken.LaunchBox.Plugins.EmulatorPlugin; every arg/response type has a
// public constructor (verified via --dump-ctors), so the host can drive them
// EXACTLY like LaunchBox does — their code runs untouched, LiteBox only
// implements the calling side of the contract:
//
//   • ForEmulator(emu)          → which plugin claims this emulator
//                                  (GetApplicableEmulators), cached per emulator id.
//   • PrepareForLaunch(...)     → PrepareEmulatorForLaunch right before the spawn;
//                                  the plugin may rewrite the command line
//                                  (NewCommandLine). RA credentials: null for now
//                                  (wired with the RetroAchievements work).
//   • NormalizeCommandLine(...) → NormalizeCommandLineForExecutable on the
//                                  emulator's default command line.
//   • CurrentVersion / UpdateAvailable / BiosFiles / Cores → consumed by the
//     Edit Emulator window (V2/V3).
//
// Every call is defensive: a plugin that throws must never break a launch.

#nullable enable

using System.IO;
using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host;

internal static class EmuPlugins
{
    private static PluginRegistry? _reg;
    private static readonly Dictionary<string, EmulatorPlugin?> _byEmulatorId = new(StringComparer.OrdinalIgnoreCase);

    public static void Configure(PluginRegistry reg) { _reg = reg; _byEmulatorId.Clear(); }

    public static IReadOnlyList<EmulatorPlugin> All => _reg?.EmulatorPlugins ?? (IReadOnlyList<EmulatorPlugin>)Array.Empty<EmulatorPlugin>();

    /// <summary>The integration plugin claiming <paramref name="emu"/> (via
    /// GetApplicableEmulators), or null. Cached per emulator id.</summary>
    public static EmulatorPlugin? ForEmulator(IEmulator? emu)
    {
        if (emu == null || _reg == null || _reg.EmulatorPlugins.Count == 0) return null;
        string id;
        try { id = emu.Id ?? ""; } catch { id = ""; }
        if (id.Length > 0 && _byEmulatorId.TryGetValue(id, out var cached)) return cached;

        EmulatorPlugin? match = null;
        foreach (var p in _reg.EmulatorPlugins)
        {
            try
            {
                var applicable = p.GetApplicableEmulators(new[] { emu });
                if (applicable != null && applicable.Contains(emu)) { match = p; break; }
            }
            catch { }
        }
        if (id.Length > 0) _byEmulatorId[id] = match;   // cache misses too (most emulators have no plugin)
        string title = ""; try { title = emu.Title ?? ""; } catch { }
        Console.WriteLine(match != null
            ? $"[emuplugin] \"{title}\" handled by {match.GetType().Name}"
            : $"[emuplugin] \"{title}\" has no integration plugin");
        return match;
    }

    /// <summary>PrepareEmulatorForLaunch hook: lets the integration plugin adjust
    /// the final command line right before the spawn (what LB does silently).
    /// Returns the (possibly rewritten) command line; never throws.</summary>
    public static string PrepareForLaunch(IEmulator emu, IGame game, IAdditionalApplication? app, string commandLine)
    {
        var p = ForEmulator(emu);
        if (p == null) return commandLine;
        try
        {
            var args = new PrepareForLaunchArgs(emu, game, commandLine, app, null /* RA credentials: later */);
            var resp = p.PrepareEmulatorForLaunch(args);
            if (resp is { WasSuccess: true } && !string.IsNullOrEmpty(resp.NewCommandLine)
                && !string.Equals(resp.NewCommandLine, commandLine, StringComparison.Ordinal))
            {
                Console.WriteLine($"[emuplugin] {p.GetType().Name}.PrepareEmulatorForLaunch rewrote the command line:\n  before: {commandLine}\n  after:  {resp.NewCommandLine}");
                return resp.NewCommandLine!;
            }
            if (resp is { WasSuccess: false })
                Console.WriteLine($"[emuplugin] {p.GetType().Name}.PrepareEmulatorForLaunch: not successful ({resp.Message ?? "no message"}) — command line kept.");
        }
        catch (Exception ex) { Console.WriteLine("[emuplugin] PrepareEmulatorForLaunch threw: " + ex.Message); }
        return commandLine;
    }

    /// <summary>NormalizeCommandLineForExecutable on the emulator's command line
    /// (per-executable fixups the plugin knows about). Never throws.</summary>
    public static string NormalizeCommandLine(IEmulator emu, string commandLine, string applicationPath)
    {
        var p = ForEmulator(emu);
        if (p == null) return commandLine;
        try
        {
            var n = p.NormalizeCommandLineForExecutable(commandLine, applicationPath);
            if (!string.IsNullOrEmpty(n) && !string.Equals(n, commandLine, StringComparison.Ordinal))
            {
                Console.WriteLine($"[emuplugin] {p.GetType().Name}.NormalizeCommandLine: \"{commandLine}\" → \"{n}\"");
                return n!;
            }
        }
        catch (Exception ex) { Console.WriteLine("[emuplugin] NormalizeCommandLine threw: " + ex.Message); }
        return commandLine;
    }

    // ── Edit-Emulator surface (V2/V3) — defensive pass-throughs ──────────

    public static string? CurrentVersion(IEmulator emu)
    {
        var p = ForEmulator(emu);
        if (p == null) return null;
        try { return p.GetCurrentVersion(ResolveAppPath(emu)); } catch { return null; }
    }

    public static IEnumerable<EmulatorBiosFile> BiosFiles(IEmulator emu, string platform)
    {
        var p = ForEmulator(emu);
        if (p == null) return Array.Empty<EmulatorBiosFile>();
        try
        {
            string? cmd = null; try { cmd = emu.CommandLine; } catch { }
            return p.GetBiosFilesForPlatform(ResolveAppPath(emu), platform, cmd) ?? Array.Empty<EmulatorBiosFile>();
        }
        catch { return Array.Empty<EmulatorBiosFile>(); }
    }

    public static string[] Cores(IEmulator emu)
    {
        if (ForEmulator(emu) is IEmulatorWithCores wc)
        {
            try { return wc.GetAllAvailableCores(ResolveAppPath(emu)) ?? Array.Empty<string>(); }
            catch { }
        }
        return Array.Empty<string>();
    }

    public static string[] SuggestedCores(IEmulator emu, string platform)
    {
        if (ForEmulator(emu) is IEmulatorWithCores wc)
        {
            try { return wc.GetSuggestedCores(ResolveAppPath(emu), platform) ?? Array.Empty<string>(); }
            catch { }
        }
        return Array.Empty<string>();
    }

    private static string ResolveAppPath(IEmulator emu)
    {
        string p = "";
        try { p = emu.ApplicationPath ?? ""; } catch { }
        try
        {
            if (!Path.IsPathRooted(p))
            {
                var root = Media.MediaResolver.LbRoot;
                if (!string.IsNullOrEmpty(root)) p = Path.GetFullPath(Path.Combine(root, p));
            }
        }
        catch { }
        return p;
    }
}
