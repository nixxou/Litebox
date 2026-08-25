// The XInput backend — BBP's GetXINPUT, without the library: the original's XInput.Wrapper was a
// vendored source file P/Invoking xinput1_4.dll (a system DLL), so we P/Invoke it directly with the
// SAME struct layout. One line per plugged slot (1..4):
//   XINPUT{slot}<>{SubType}<>{signature}<>VendorID=0x....<>ProductID=0x....<>RevisionID=0x....[<>{mac}<>{ctype}<>{conn}<>{inputSlot}]
// where:
//   • SubType prints the wrapper's [Flags] enum NAME ("Gamepad", "Wheel"…) — enum copied verbatim;
//   • signature = Md5Short of the capabilities struct serialized EXACTLY as Newtonsoft serialized
//     the wrapper's Capability struct ({"Type":..,"SubType":..,"Flags":..,"Gamepad":{..7 fields..},
//     "Vibration":{..}} — declaration order, numeric enums, no spaces). The JSON is hand-built here
//     so signatures match ones users captured with BigBoxProfile, byte for byte;
//   • VendorID/ProductID/RevisionID come from the hidden XInputGetCapabilitiesEx export — ordinal
//     108 of xinput1_4.dll, resolved by GetProcAddress exactly as the original's XExt did;
//   • when DS4Windows is running and its log names this XInput slot, signature becomes "DS4WIN" and
//     the MAC/type/connection/input-slot tail is appended (the Ds4WinLogParser port).

#nullable enable

using System;
using System.Runtime.InteropServices;
using System.Text;

namespace LbApiHost.Host.Rules.Hid;

internal static class XInputBackend
{
    // ── the wrapper's structs, layout-identical ──
    [StructLayout(LayoutKind.Sequential)]
    private struct XCaps
    {
        public byte Type;
        public byte SubType;
        public short Flags;
        public short wButtons;
        public byte bLeftTrigger;
        public byte bRightTrigger;
        public short sThumbLX;
        public short sThumbLY;
        public short sThumbRX;
        public short sThumbRY;
        public ushort LSpeed;
        public ushort RSpeed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XCapsEx
    {
        public XCaps Capabilities;
        public ushort vendorId;
        public ushort productId;
        public ushort revisionId;
        public uint a4;
    }

    // XInput.Wrapper's DeviceSubType, verbatim — [Flags] included, because ToString() of a [Flags]
    // enum is what the original printed and combined values would spell differently without it.
    [Flags]
    private enum DeviceSubType : byte
    {
        Unknown = 0x00, Gamepad = 0x01, Wheel = 0x02, ArcadeStick = 0x03, FlightStick = 0x04,
        DancePad = 0x05, Guitar = 0x06, GuitarAlternate = 0x07, DrumKit = 0x08,
        GuitarBass = 0x0B, ArcadePad = 0x13,
    }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetCapabilities")]
    private static extern uint XInputGetCapabilities(uint userIndex, uint flags, ref XCaps caps);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LoadLibrary(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr module, uint ordinal);

    private delegate uint XInputGetCapabilitiesExFn(uint a1, uint userIndex, uint flags, ref XCapsEx caps);
    private const uint CapsExOrdinal = 108;   // hidden export, same ordinal XExt used
    private const uint GamepadFlag = 1;

    public static string Dump(string ds4WinLogPath)
    {
        var ds4Controllers = string.IsNullOrEmpty(ds4WinLogPath)
            ? new System.Collections.Generic.List<Ds4WinController>()
            : Ds4WinLogParser.Parse(ds4WinLogPath);

        IntPtr capsEx = IntPtr.Zero;
        var module = LoadLibrary("xinput1_4.dll");
        if (module != IntPtr.Zero) capsEx = GetProcAddress(module, CapsExOrdinal);

        var sb = new StringBuilder();
        for (int slot = 1; slot <= 4; slot++)
        {
            var caps = new XCaps();
            XInputGetCapabilities((uint)(slot - 1), GamepadFlag, ref caps);
            if (caps.Type == 0) continue;

            string signature = HidInfoCache.Md5Short(NewtonsoftShapeJson(caps));
            string extra = "\r\n";
            Ds4WinController? ds4 = null;
            foreach (var d in ds4Controllers) { if (d.XinputSlot == slot) { ds4 = d; break; } }
            if (ds4 != null)
            {
                signature = "DS4WIN";
                extra = $"<>{ds4.MacAddress}<>{ds4.ControllerType}<>{ds4.ConnectionType}<>{ds4.InputSlot}" + "\r\n";
            }

            ushort vid = 0, pid = 0, rev = 0;
            try
            {
                if (capsEx != IntPtr.Zero)
                {
                    var fn = Marshal.GetDelegateForFunctionPointer<XInputGetCapabilitiesExFn>(capsEx);
                    var ex = new XCapsEx();
                    if (fn(1, (uint)(slot - 1), 0, ref ex) == 0) { vid = ex.vendorId; pid = ex.productId; rev = ex.revisionId; }
                }
            }
            catch { }
            extra = $"<>VendorID=0x{vid:X04}<>ProductID=0x{pid:X04}<>RevisionID=0x{rev:X04}" + extra;

            sb.Append($"XINPUT{slot}<>{((DeviceSubType)caps.SubType).ToString().Trim()}<>{signature}").Append(extra);
        }
        return sb.ToString();
    }

    /// <summary>JsonConvert.SerializeObject(caps, Formatting.None) over the wrapper's Capability
    /// struct, reproduced by hand: fields in declaration order, enums numeric, wButtons SIGNED.</summary>
    private static string NewtonsoftShapeJson(in XCaps c)
        => $"{{\"Type\":{c.Type},\"SubType\":{c.SubType},\"Flags\":{c.Flags},"
         + $"\"Gamepad\":{{\"wButtons\":{c.wButtons},\"bLeftTrigger\":{c.bLeftTrigger},\"bRightTrigger\":{c.bRightTrigger},"
         + $"\"sThumbLX\":{c.sThumbLX},\"sThumbLY\":{c.sThumbLY},\"sThumbRX\":{c.sThumbRX},\"sThumbRY\":{c.sThumbRY}}},"
         + $"\"Vibration\":{{\"LSpeed\":{c.LSpeed},\"RSpeed\":{c.RSpeed}}}}}";
}
