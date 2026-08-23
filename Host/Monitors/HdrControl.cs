// HDR (Windows "Advanced Color") per monitor — read the state, set the state.
//
// Ported from the mechanism in Vaiz/HdrSwitcher, which is a small C++ CLI doing exactly three Win32
// calls. Reimplemented here as P/Invoke rather than shipped as a binary: the whole thing is a getter and
// a setter on DisplayConfigGet/SetDeviceInfo, we ALREADY hold the (adapter LUID, target id) pair every
// call needs — WindowsDisplayAPI hands it to us — and a bundled exe would have meant a fourth native
// payload to keep in step with four deploy lists, a process spawn per monitor per profile switch, and a
// C++ toolchain in the build. It also addresses monitors by index or name, where this module identifies
// them by DevicePath; that mismatch alone would have been a bug factory.
//
// TWO GENERATIONS OF THE API. GET_ADVANCED_COLOR_INFO (type 9) is the one every Windows 10/11 has.
// Windows 11 24H2 added GET_ADVANCED_COLOR_INFO_2 with a finer-grained mode enum; we deliberately stay
// on the older one, because the question this module asks — "is HDR on for this screen, and can it be" —
// is answered identically by both, and the older call exists everywhere.
//
// NAMING. Windows calls it "advanced color", not HDR: on a WCG-capable SDR panel the same flag enables
// wide colour without HDR. advancedColorSupported is what the Settings app shows as "Use HDR" being
// available at all, so that is what gates the feature here.

#nullable enable

using System;
using System.Runtime.InteropServices;
using LbApiHost.Host.Diag;
using WindowsDisplayAPI.DisplayConfig;

namespace LbApiHost.Host.Monitors;

internal static class HdrControl
{
    private const string Tag = "monitors";

    private const int GetAdvancedColorInfo = 9;
    private const int SetAdvancedColorState = 10;

    /// <summary>HDR state of one monitor. <c>Supported</c> false = the panel/link can't do it, so
    /// <c>Enabled</c> is meaningless and setting it would fail.</summary>
    internal readonly record struct HdrState(bool Supported, bool Enabled, bool ForceDisabled,
                                             string Encoding = "", int Bits = 0);

    /// <summary>Reads a monitor's advanced-colour state. Returns Supported=false on any failure — an
    /// unreadable state is indistinguishable from an unsupported one for everything we do with it.</summary>
    public static HdrState Query(PathDisplayTarget target)
    {
        try
        {
            var info = new GetAdvancedColorInfoStruct
            {
                Header = Header(target, GetAdvancedColorInfo, Marshal.SizeOf<GetAdvancedColorInfoStruct>()),
            };
            if (DisplayConfigGetDeviceInfo(ref info) != 0) return default;

            // ColorEncoding / BitsPerColorChannel ride in the same struct — they were being read and thrown
            // away. Read-only (Windows has no setter for them), captured for diagnosis.
            string enc = info.ColorEncoding switch
            {
                0 => "RGB", 1 => "YCbCr444", 2 => "YCbCr422", 3 => "YCbCr420", 4 => "Intensity", _ => "",
            };
            return new HdrState(
                Supported: (info.Value & 0x1) != 0,
                Enabled: (info.Value & 0x2) != 0,
                ForceDisabled: (info.Value & 0x8) != 0,
                Encoding: enc,
                Bits: (int)info.BitsPerColorChannel);
        }
        catch (Exception ex) { LbLog.Warn(Tag, "HDR query failed: " + ex.Message); return default; }
    }

    /// <summary>Turns advanced colour on or off for one monitor. False when the call is refused —
    /// including the case where the panel does not support it.</summary>
    public static bool Set(PathDisplayTarget target, bool enable)
    {
        try
        {
            var state = new SetAdvancedColorStateStruct
            {
                Header = Header(target, SetAdvancedColorState, Marshal.SizeOf<SetAdvancedColorStateStruct>()),
                Value = enable ? 1u : 0u,
            };
            int err = DisplayConfigSetDeviceInfo(ref state);
            if (err != 0) { LbLog.Warn(Tag, $"HDR {(enable ? "enable" : "disable")} refused (error {err})"); return false; }
            return true;
        }
        catch (Exception ex) { LbLog.Warn(Tag, "HDR set failed: " + ex.Message); return false; }
    }

    /// <summary>"on" / "off" / "n/a", for logs and for the editor's layout table.</summary>
    public static string Text(bool? state) => state == null ? "—" : state.Value ? "on" : "off";

    // ── native ───────────────────────────────────────────────────────────────

    private static DeviceInfoHeader Header(PathDisplayTarget target, int type, int size)
    {
        var luid = target.Adapter.AdapterId;
        return new DeviceInfoHeader
        {
            Type = type,
            Size = (uint)size,
            AdapterLow = luid.LowPart,
            AdapterHigh = luid.HighPart,
            Id = target.TargetId,
        };
    }

    /// <summary>DISPLAYCONFIG_DEVICE_INFO_HEADER, with the LUID inlined as its two halves — every field
    /// is 4-byte aligned, so sequential layout matches the native struct exactly (20 bytes).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfoHeader
    {
        public int Type;
        public uint Size;
        public uint AdapterLow;
        public int AdapterHigh;
        public uint Id;
    }

    /// <summary>DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO. The bitfield union is read as a raw uint:
    /// bit 0 supported, bit 1 enabled, bit 2 wideColorEnforced, bit 3 forceDisabled.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct GetAdvancedColorInfoStruct
    {
        public DeviceInfoHeader Header;
        public uint Value;
        public int ColorEncoding;
        public uint BitsPerColorChannel;
    }

    /// <summary>DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE — bit 0 is enableAdvancedColor.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct SetAdvancedColorStateStruct
    {
        public DeviceInfoHeader Header;
        public uint Value;
    }

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref GetAdvancedColorInfoStruct requestPacket);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigSetDeviceInfo(ref SetAdvancedColorStateStruct setPacket);
}
