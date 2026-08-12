// Loads the EMBEDDED 0Harmony.dll so the whole plugin ships as a single dll. The Harmony assembly is embedded
// as a resource (see csproj) and resolved on demand from BOTH the plugin's own load context and the AppDomain,
// so wherever LaunchBox's plugin loader put us, "0Harmony" resolves to our bytes.
//
// BCL-only + no HarmonyLib reference here, so calling Ensure() is safe even from the early StartupHook phase —
// it just registers resolve handlers; the actual Harmony load happens later, the first time AdminGuard touches
// a HarmonyLib type (plugin-init, well after boot). Idempotent, never throws.

using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace LiteBoxParental
{
    internal static class HarmonyLoader
    {
        private const string ResourceName = "litebox-parental.0Harmony.dll";
        private const string AssemblyName = "0Harmony";
        private static bool _registered;
        private static readonly object _gate = new object();
        private static Assembly _cached;

        public static void Ensure()
        {
            if (_registered) return;
            lock (_gate)
            {
                if (_registered) return;
                _registered = true;
                try
                {
                    var self = typeof(HarmonyLoader).Assembly;
                    var ownCtx = AssemblyLoadContext.GetLoadContext(self) ?? AssemblyLoadContext.Default;
                    ownCtx.Resolving += (ctx, name) => name.Name == AssemblyName ? Load(ctx) : null;
                    AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
                        new AssemblyName(args.Name).Name == AssemblyName ? LoadIntoDefault() : null;
                }
                catch (Exception ex) { Log.Line("[HarmonyLoader] register failed: " + ex.Message); }
            }
        }

        private static Assembly Load(AssemblyLoadContext ctx)
        {
            try
            {
                if (_cached != null) return _cached;
                using (var s = typeof(HarmonyLoader).Assembly.GetManifestResourceStream(ResourceName))
                {
                    if (s == null) { Log.Line("[HarmonyLoader] embedded resource missing: " + ResourceName); return null; }
                    using (var ms = new MemoryStream())
                    {
                        s.CopyTo(ms);
                        ms.Position = 0;
                        _cached = ctx.LoadFromStream(ms);
                        Log.Line("[HarmonyLoader] loaded embedded 0Harmony (" + ms.Length + " bytes)");
                        return _cached;
                    }
                }
            }
            catch (Exception ex) { Log.Line("[HarmonyLoader] load failed: " + ex.Message); return null; }
        }

        private static Assembly LoadIntoDefault() => _cached ?? Load(AssemblyLoadContext.Default);
    }
}
