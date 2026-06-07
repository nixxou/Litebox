// Deploys the bundled Everything64.dll.api (shipped next to the LiteBox exe in Core) to
// <LB>\ThirdParty\Everything\Everything64.dll if absent — exactly like ExtendDB does, and the same
// target the host GameCache's EverythingBridge P/Invokes.
//
// EverythingSdk declares its P/Invokes with a RELATIVE path ("ThirdParty\Everything\
// Everything64.dll"). Under LaunchBox (.NET Framework) that resolves against the process CWD; on
// .NET 9 the native loader does NOT resolve a relative DllImport path via the CWD, so the P/Invoke
// (and thus IsEverythingAvailable) silently failed and the host fell back to Directory enumeration.
// We register a DllImportResolver on the host assembly that maps any "Everything64" P/Invoke to the
// ABSOLUTE deployed DLL. It returns IntPtr.Zero for every other native import (user32/uxtheme/
// Magick, etc.) so default resolution is unaffected.

using System;
using System.IO;
using System.Runtime.InteropServices;

namespace LbApiHost.Host.Media;

internal static class EverythingSupport
{
    private static bool _resolverSet;
    private static string _dllPath;

    public static void Init(string lbRoot)
    {
        try
        {
            string dir = Path.Combine(lbRoot, "ThirdParty", "Everything");
            Directory.CreateDirectory(dir);
            string target = Path.Combine(dir, "Everything64.dll");
            if (!File.Exists(target))
            {
                string src = Path.Combine(AppContext.BaseDirectory, "Everything64.dll.api");
                if (File.Exists(src)) { try { File.Copy(src, target); } catch { } }
            }
            if (File.Exists(target)) _dllPath = target;

            if (!_resolverSet)
            {
                _resolverSet = true;   // SetDllImportResolver throws if called twice per assembly
                try
                {
                    NativeLibrary.SetDllImportResolver(typeof(EverythingSupport).Assembly, (name, asm, search) =>
                    {
                        if (!string.IsNullOrEmpty(_dllPath)
                            && name.IndexOf("Everything64", StringComparison.OrdinalIgnoreCase) >= 0
                            && NativeLibrary.TryLoad(_dllPath, out var h)) return h;
                        return IntPtr.Zero;   // not ours → default native resolution
                    });
                }
                catch { /* a resolver is already registered for this assembly */ }
            }
        }
        catch { }
    }
}
