// The HID detector's dependencies, resolved from <LB>\ThirdParty\Hid — NOT from Core. LiteBox.exe
// lives inside LaunchBox's OWN Core folder, so loose DLLs there are pollution at best and a clobber
// at worst (LaunchBox ships its own SharpDX 4.2.0, which is exactly why DirectInput is a
// Private=false reference to LB's copy and never a file we deploy). The four files that ARE ours —
// HidSharp.dll, SDL2-CS.dll, InTheHand.Net.Personal.dll (managed) and SDL2.dll (native) — are
// deployed by NativeInstaller to ThirdParty\Hid and found here:
//   • managed → one AssemblyLoadContext.Default.Resolving hook, installed lazily on first HID use
//     (it only ever fires when default probing already failed, so it costs the rest of the app nothing);
//   • native SDL2.dll → NativeLibrary.TryLoad by FULL PATH before the first SDL call — an
//     already-loaded module wins name resolution (the CnnEmbedder pattern; the LiteBox assembly's one
//     SetDllImportResolver is already taken by EverythingSupport, so preload is the mechanism).
//
// Same binaries as BigBoxProfile (HidSharp 2.1.0, ppy.SDL2-CS 1.0.82, 32feet 3.5, SDL2 2.0.14): the
// whole point of this module is that emulators consume these exact libraries' identifiers, so the
// strings we detect must come from the same code paths.

#nullable enable

using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using LbApiHost.Host.Diag;

namespace LbApiHost.Host.Rules.Hid;

internal static class HidThirdParty
{
    private const string Tag = "hid";
    private static bool _installed;
    private static bool _sdlPreloaded;

    /// <summary>&lt;LB&gt;\ThirdParty\Hid (lbRoot derived from Core = AppContext.BaseDirectory).</summary>
    public static string HomeDir
        => Path.Combine(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..")), "ThirdParty", "Hid");

    /// <summary>Installs the managed resolver once. Call before touching any backend that uses a
    /// bundled assembly (HidSharp / SDL2-CS / InTheHand). Cheap and idempotent.</summary>
    public static void EnsureResolver()
    {
        if (_installed) return;
        _installed = true;
        AssemblyLoadContext.Default.Resolving += (ctx, name) =>
        {
            if (name.Name is "HidSharp" or "SDL2-CS" or "InTheHand.Net.Personal")
            {
                string p = Path.Combine(HomeDir, name.Name + ".dll");
                if (File.Exists(p))
                {
                    try { return ctx.LoadFromAssemblyPath(p); }
                    catch (Exception ex) { LbLog.Warn(Tag, $"{name.Name} load failed: {ex.Message}"); }
                }
            }
            return null;
        };
    }

    /// <summary>Preloads the native SDL2.dll by full path so SDL2-CS's [DllImport("SDL2.dll")] binds to
    /// OUR copy wherever it sits. Returns false when the file is missing (backend degrades to empty).</summary>
    public static bool EnsureSdlNative()
    {
        if (_sdlPreloaded) return true;
        string p = Path.Combine(HomeDir, "SDL2.dll");
        if (!File.Exists(p)) { LbLog.Warn(Tag, $"SDL2.dll not found at {p} — SDL backends unavailable"); return false; }
        _sdlPreloaded = NativeLibrary.TryLoad(p, out _);
        if (!_sdlPreloaded) LbLog.Warn(Tag, $"SDL2.dll failed to load from {p}");
        return _sdlPreloaded;
    }
}
