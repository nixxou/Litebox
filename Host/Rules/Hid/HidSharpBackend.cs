// The HidSharp backend — BBP's GetHIDSharpInfo. One line per HID interface the OS exposes:
//   {friendlyName}<>{VendorID}<>{ProductID}<>{DevicePath}
// (IDs decimal, path the raw \\?\hid#... interface string). Uses the SAME HidSharp 2.1.0 the
// original shipped, resolved from ThirdParty\Hid — its enumeration order and path spelling are what
// users' regexes were written against. This class is the only place HidSharp types appear, so the
// assembly loads on first call, never at boot.

#nullable enable

using System.Runtime.CompilerServices;
using System.Text;
using HidSharp;

namespace LbApiHost.Host.Rules.Hid;

internal static class HidSharpBackend
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string Dump()
    {
        HidThirdParty.EnsureResolver();
        return DumpCore();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string DumpCore()
    {
        var sb = new StringBuilder();
        foreach (var device in DeviceList.Local.GetHidDevices())
        {
            string friendlyName = "Unknown";
            try
            {
                var n = device.GetFriendlyName();
                if (n != null) friendlyName = n.Trim();
            }
            catch { }
            sb.Append($"{friendlyName}<>{device.VendorID.ToString().Trim()}<>{device.ProductID.ToString().Trim()}<>{device.DevicePath.ToString().Trim()}").Append("\r\n");
        }
        return sb.ToString();
    }
}
