// The Bluetooth backend — BBP's GetBluetoothInfo, through the SAME 32feet.NET 3.5 assembly
// (InTheHand.Net.Personal, resolved from ThirdParty\Hid). One line per CONNECTED device:
//   {DeviceName}<>{ClassOfDevice}<>{DeviceAddress}
// The point is pairing info — a DS4 over Bluetooth is identified by its MAC here. DiscoverDevices is
// SLOW (an inquiry can take seconds); it only runs when a matcher actually checks the BT box, and
// once per launch thanks to the cache. The assembly targets .NET Framework 3.5 — it loads in compat
// on .NET 9, and if it ever fails the cache's Safe() degrades this backend to empty with a log line.

#nullable enable

using System.Runtime.CompilerServices;
using System.Text;
using InTheHand.Net.Sockets;

namespace LbApiHost.Host.Rules.Hid;

internal static class BtBackend
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
        var client = new BluetoothClient();
        foreach (var device in client.DiscoverDevices())
        {
            if (!device.Connected) continue;
            sb.Append($"{device.DeviceName}<>{device.ClassOfDevice}<>{device.DeviceAddress}").Append("\r\n");
        }
        return sb.ToString();
    }
}
