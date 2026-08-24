// S6 — builds the detail.json `launchOptions` DTO for the web themes (lb-web / bb-web): the
// alt-emulator / multi-disc / Select-ROM launch menu. Byte-compatible with the shape the shipped theme
// JS parses (litebox/app.js + bigbox/engine/app.js) AND with ExtendDB's HostRomBridge, so the existing
// JS drives it unchanged:
//
//   launchOptions = { emulators[{id,title,isDefault,autoExtract}],
//                     versions[{appId,disc,label,isDefault,isArchive,emulatorId,useEmulator,useDosBox,exeName}],
//                     mainPathIsArchive, mainExeName, mainUseDosBox }
//
// Mirrors the desktop LaunchButtons enumeration exactly (platform emulators default-first with the
// effective per-platform AutoExtract; Base + non-document additional-app versions, each with its own
// isArchive; the main-path archive flag). The Select-ROM button in the theme only appears when a
// version isArchive AND the selected emulator autoExtracts — the same gating as the desktop.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LbApiHost.Host.Data;
using LbApiHost.Host.Modules;
using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Web;

internal static class WebLaunchOptions
{
    /// <summary>The launchOptions DTO for a game, or null on failure / no game.</summary>
    public static object? Build(IGame game)
    {
        if (game == null) return null;
        try
        {
            string platform = S(() => game.Platform) ?? "";
            string? gameEmuId = S(() => game.EmulatorId);

            // Emulators for the platform (default first), each with its effective AutoExtract.
            var emuDtos = new List<object>();
            foreach (var e in EmulatorsForPlatform(platform, game))
            {
                string? id = S(() => e.Id);
                if (string.IsNullOrEmpty(id)) continue;
                emuDtos.Add(new
                {
                    id,
                    title = S(() => e.Title) ?? "",
                    isDefault = !string.IsNullOrEmpty(gameEmuId) && string.Equals(id, gameEmuId, StringComparison.Ordinal),
                    autoExtract = EffectiveAutoExtract(e, platform),
                });
            }

            // Versions: Base (main path) + each non-document additional app.
            bool mainIsArc = SafeIsArchive(S(() => game.ApplicationPath));
            string? mainExe = ExeNameIfDirect(S(() => game.ApplicationPath), mainIsArc);
            bool mainDos = SafeBool(() => game.UseDosBox);

            var verDtos = new List<object>
            {
                new
                {
                    appId = (string?)null,
                    disc = (string?)null,
                    label = "Base",
                    isDefault = true,
                    isArchive = mainIsArc,
                    emulatorId = gameEmuId,
                    useEmulator = !string.IsNullOrEmpty(gameEmuId),
                    useDosBox = mainDos,
                    exeName = mainExe,
                }
            };
            foreach (var a in AddApps(game))
            {
                string? path = S(() => a.ApplicationPath);
                bool arc = SafeIsArchive(path);
                verDtos.Add(new
                {
                    appId = S(() => a.Id),
                    disc = S(() => a.Disc),
                    label = FirstNonEmpty(S(() => a.Name), "(version)"),
                    isDefault = false,
                    isArchive = arc,
                    emulatorId = S(() => a.UseEmulator ? a.EmulatorId : null),
                    useEmulator = SafeBool(() => a.UseEmulator),
                    useDosBox = SafeBool(() => a.UseDosBox),
                    exeName = ExeNameIfDirect(path, arc),
                });
            }

            // Public monitor profiles, for the theme's per-launch "Monitor profile" picker. Null (absent
            // in the JSON) when the Monitors module is off or nothing is public — which is also what hides
            // the menu client-side, so the gate lives in exactly one place.
            object? monDtos = null;
            try
            {
                if (LbModules.On(LbModule.Monitors))
                {
                    var pubs = Monitors.MonitorProfileStore.All()
                        .Where(mp => mp.Public && !mp.IsEmpty)
                        .Select(mp => (object)new { id = mp.Id, name = mp.Name })
                        .ToList();
                    if (pubs.Count > 0) monDtos = pubs;
                }
            }
            catch { }

            return new
            {
                emulators = emuDtos,
                versions = verDtos,
                mainPathIsArchive = mainIsArc,
                mainExeName = mainExe,
                mainUseDosBox = mainDos,
                monitorProfiles = monDtos,
            };
        }
        catch { return null; }
    }

    // ── Emulators for a platform (default first) — mirrors MainWindow.SafeEmulatorsForPlatform. ──
    private static List<IEmulator> EmulatorsForPlatform(string platform, IGame game)
    {
        var result = new List<IEmulator>();
        try
        {
            var all = (PluginHelper.DataManager?.GetAllEmulators() ?? Array.Empty<IEmulator>())
                .Where(e => { try { var id = e?.Id; return !string.IsNullOrEmpty(id) && id != Guid.Empty.ToString(); } catch { return false; } })
                .ToList();

            var match = all.Where(e =>
            {
                try { return e.GetAllEmulatorPlatforms()?.Any(ep => string.Equals(ep.Platform, platform, StringComparison.OrdinalIgnoreCase)) == true; }
                catch { return false; }
            }).ToList();

            List<IEmulator> chosen;
            if (match.Count > 0) chosen = match;
            else
            {
                // No emulator maps to the platform: keep only what the game / its versions reference.
                var wanted = new HashSet<string>(StringComparer.Ordinal);
                var gid = S(() => game.EmulatorId);
                if (!string.IsNullOrEmpty(gid)) wanted.Add(gid!);
                foreach (var a in AddApps(game))
                {
                    var vid = S(() => a.UseEmulator ? a.EmulatorId : null);
                    if (!string.IsNullOrEmpty(vid)) wanted.Add(vid!);
                }
                chosen = all.Where(e => { var id = S(() => e.Id); return id != null && wanted.Contains(id); }).ToList();
            }

            // Default first.
            string? defId = S(() => game.EmulatorId);
            foreach (var e in chosen)
            {
                if (!string.IsNullOrEmpty(defId) && string.Equals(S(() => e.Id), defId, StringComparison.Ordinal)) result.Insert(0, e);
                else result.Add(e);
            }
        }
        catch { }
        return result;
    }

    // Effective per-platform AutoExtract — mirrors LaunchButtons.ResolveEffectiveAutoExtract.
    private static bool EffectiveAutoExtract(IEmulator emulator, string platform)
    {
        try
        {
            var eps = S(() => emulator.GetAllEmulatorPlatforms());
            var ep = eps?.FirstOrDefault(x => string.Equals(S(() => x.Platform), platform, StringComparison.OrdinalIgnoreCase))
                  ?? eps?.FirstOrDefault(x => SafeBool(() => x.IsDefault));
            bool? pv = null;
            try { pv = ep?.AutoExtract; } catch { }
            if (pv.HasValue) return pv.Value;
        }
        catch { }
        return SafeBool(() => emulator.AutoExtract);
    }

    // Launchable additional applications only (documents are Section=="Document", LB 14 links are
    // Section=="Link" — neither is a launchable version).
    private static IEnumerable<IAdditionalApplication> AddApps(IGame game)
    {
        IAdditionalApplication[]? apps = null;
        try { apps = game.GetAllAdditionalApplications(); } catch { }
        if (apps == null) yield break;
        foreach (var a in apps)
        {
            if (a == null) continue;
            if (a is HostAdditionalApplication { IsNonLaunchable: true }) continue;
            yield return a;
        }
    }

    private static string? ExeNameIfDirect(string? path, bool isArchive)
    {
        if (isArchive || string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var ext = (Path.GetExtension(path) ?? "").ToLowerInvariant();
            return (ext == ".exe" || ext == ".bat" || ext == ".lnk") ? Path.GetFileName(path) : null;
        }
        catch { return null; }
    }

    private static bool SafeIsArchive(string? path)
    {
        try { return !string.IsNullOrWhiteSpace(path) && Rom.RomExtractor.IsArchive(path!); } catch { return false; }
    }

    private static string FirstNonEmpty(string? a, string fallback) => string.IsNullOrWhiteSpace(a) ? fallback : a!;
    private static T? S<T>(Func<T> f) { try { return f(); } catch { return default; } }
    private static bool SafeBool(Func<bool> f) { try { return f(); } catch { return false; } }
}
