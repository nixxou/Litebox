// Deploys the bundled Everything64.dll.api (shipped next to the LiteBox exe in Core) to
// <LB>\ThirdParty\Everything\Everything64.dll if absent — exactly like ExtendDB does, and the same
// target the host GameCache's EverythingBridge P/Invokes (a RELATIVE path "ThirdParty\Everything\
// Everything64.dll", resolved against the process CWD which the host sets to the LB root). So the
// host GameCache gets Everything's instant file enumeration with or without ExtendDB installed.

using System;
using System.IO;

namespace LbApiHost.Host.Media;

internal static class EverythingSupport
{
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
        }
        catch { }
    }
}
