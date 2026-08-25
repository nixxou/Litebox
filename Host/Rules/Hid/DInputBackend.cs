// The DirectInput backend — BBP's GetDInputInfo. One line per stick-like device:
//   DINPUT{index}<>{ProductName}<>{Type}<>{InstanceGuid}<>{InstanceName}<>{InterfacePath}
// SharpDX.DirectInput 4.2.0 — the SAME assembly LaunchBox itself ships in Core (Private=false
// reference, nothing deployed): identical version to what BigBoxProfile bundled, so instance GUIDs
// and type names print identically. Only this class touches SharpDX, so it loads on first call.

#nullable enable

using System.Runtime.CompilerServices;
using System.Text;
using SharpDX.DirectInput;

namespace LbApiHost.Host.Rules.Hid;

internal static class DInputBackend
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string Dump()
    {
        var sb = new StringBuilder();
        int index = 0;
        var directInput = new DirectInput();
        foreach (var deviceInstance in directInput.GetDevices())
        {
            if (!IsStickType(deviceInstance)) continue;
            var joystick = new Joystick(directInput, deviceInstance.InstanceGuid);
            sb.Append($"DINPUT{index}<>{deviceInstance.ProductName}<>{deviceInstance.Type}<>{deviceInstance.InstanceGuid}<>{deviceInstance.InstanceName}<>{joystick.Properties.InterfacePath}").Append("\r\n");
            index++;
        }
        return sb.ToString();
    }

    private static bool IsStickType(DeviceInstance d)
        => d.Type is DeviceType.Joystick or DeviceType.Gamepad or DeviceType.FirstPerson
            or DeviceType.Flight or DeviceType.Driving or DeviceType.Supplemental;
}
