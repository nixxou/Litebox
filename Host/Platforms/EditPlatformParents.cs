// The "Parents" tab of the Edit Platform window — which Platform Categories contain this platform.
// Thin wrapper over the shared ParentsPicker (LB rules: a platform's parents are Root or categories only;
// zero checks = explicit Root row). See ParentsPicker.cs for the full decoded semantics + Parents.xml format.

#nullable enable

using System;
using System.Windows.Forms;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Platforms;

internal static class EditPlatformParents
{
    public static (Control panel, Action apply) Build(IPlatform plat, bool readOnly, float s)
    {
        string name = Safe(() => plat.Name) ?? "";
        // Deferred key: the Details tab may rename the platform before the appliers run.
        return ParentsPicker.Build(ParentChildKind.Platform, name, readOnly, s, () => Safe(() => plat.Name) ?? name);
    }

    private static T? Safe<T>(Func<T> f) { try { return f(); } catch { return default; } }
}
