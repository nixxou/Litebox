// The DS4 backend — BigBoxProfile's DualShock4Lib (its own source project, pure P/Invoke), vendored
// compact: enumerate HID interfaces via setupapi, keep Sony's (VID 1356, PID 1476 DS4 v1 / 2508
// DS4 v2), one line per pad:
//   DS4Controller<>{VendorId}<>{ProductId}<>{DevicePath}<>USB:YES/NO
// (IDs decimal; the connection heuristic is the lib's — a 64-byte input report means USB.) One
// faithful side effect kept on purpose: on a Bluetooth pad the lib's Controller constructor read
// feature report 0x02, which switches the DS4 from its minimal BT report to the full 0x11 report —
// emulators expecting a "woken" pad rely on it, so we read it too (failure ignored, as there).
// Same Windows APIs in the same order as the lib → same DevicePath spelling, same enumeration order.

#nullable enable

using System;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace LbApiHost.Host.Rules.Hid;

internal static class Ds4Backend
{
    private const int SonyVendorId = 1356;                       // 0x054C
    private static readonly int[] Ds4ProductIds = { 1476, 2508 };  // 0x05C4, 0x09CC

    public static string Dump()
    {
        var sb = new StringBuilder();
        NativeMethods.HidD_GetHidGuid(out Guid hidGuid);
        IntPtr infoSet = NativeMethods.SetupDiGetClassDevs(ref hidGuid, null, IntPtr.Zero,
            NativeMethods.DIGCF_PRESENT | NativeMethods.DIGCF_DEVICEINTERFACE);
        try
        {
            var infoData = default(NativeMethods.SP_DEVINFO_DATA);
            infoData.cbSize = (uint)Marshal.SizeOf(infoData);
            uint deviceIndex = 0;
            while (NativeMethods.SetupDiEnumDeviceInfo(infoSet, deviceIndex++, ref infoData))
            {
                var ifaceData = default(NativeMethods.SP_DEVICE_INTERFACE_DATA);
                ifaceData.cbSize = Marshal.SizeOf(ifaceData);
                uint memberIndex = 0;
                while (NativeMethods.SetupDiEnumDeviceInterfaces(infoSet, ref infoData, ref hidGuid, memberIndex++, ref ifaceData))
                {
                    string? path = GetDevicePath(infoSet, ref ifaceData);
                    if (path == null) continue;
                    try
                    {
                        using var handle = OpenDevice(path, 0);
                        var attribs = default(NativeMethods.HIDD_ATTRIBUTES);
                        attribs.Size = (uint)Marshal.SizeOf(attribs);
                        if (!NativeMethods.HidD_GetAttributes(handle, ref attribs)) continue;
                        if (attribs.VendorID != SonyVendorId || Array.IndexOf(Ds4ProductIds, (int)attribs.ProductID) < 0) continue;

                        ushort inputLen = 0, featureLen = 0;
                        IntPtr pp = IntPtr.Zero;
                        if (NativeMethods.HidD_GetPreparsedData(handle, ref pp))
                        {
                            var caps = default(NativeMethods.HIDP_CAPS);
                            NativeMethods.HidP_GetCaps(pp, ref caps);
                            NativeMethods.HidD_FreePreparsedData(pp);
                            inputLen = caps.InputReportByteLength;
                            featureLen = caps.FeatureReportByteLength;
                        }
                        bool usb = inputLen == 64;
                        if (!usb && featureLen > 0) TryWakeBluetooth(path, featureLen);   // the lib's ctor side effect

                        string usbStatus = usb ? "USB:YES" : "USB:NO";
                        sb.Append($"DS4Controller<>{attribs.VendorID.ToString().Trim()}<>{attribs.ProductID.ToString().Trim()}<>{path.Trim()}<>{usbStatus}").Append("\r\n");
                    }
                    catch { }
                }
            }
        }
        finally { NativeMethods.SetupDiDestroyDeviceInfoList(infoSet); }
        return sb.ToString();
    }

    private static void TryWakeBluetooth(string path, ushort featureLen)
    {
        try
        {
            byte[] buffer = new byte[featureLen];
            buffer[0] = 0x02;
            using var handle = OpenDevice(path, NativeMethods.GENERIC_READ);
            NativeMethods.HidD_GetFeature(handle, buffer, buffer.Length);
        }
        catch { }
    }

    private static SafeFileHandle OpenDevice(string path, uint access)
    {
        var security = new NativeMethods.SECURITY_ATTRIBUTES
        {
            lpSecurityDescriptor = IntPtr.Zero,
            bInheritHandle = true,
            nLength = Marshal.SizeOf(typeof(NativeMethods.SECURITY_ATTRIBUTES)),
        };
        var handle = NativeMethods.CreateFile(path, access,
            NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
            ref security, NativeMethods.OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle.IsInvalid) Marshal.ThrowExceptionForHR(Marshal.GetHRForLastWin32Error());
        return handle;
    }

    private static string? GetDevicePath(IntPtr infoSet, ref NativeMethods.SP_DEVICE_INTERFACE_DATA ifaceData)
    {
        var detail = new NativeMethods.SP_DEVICE_INTERFACE_DETAIL_DATA
        {
            cbSize = IntPtr.Size == 8 ? 8 : 4 + Marshal.SystemDefaultCharSize,
        };
        uint requiredSize = 0;
        return NativeMethods.SetupDiGetDeviceInterfaceDetail(infoSet, ref ifaceData, ref detail, 1024, ref requiredSize, IntPtr.Zero)
            ? detail.DevicePath : null;
    }

    private static class NativeMethods
    {
        public const int DIGCF_PRESENT = 0x02;
        public const int DIGCF_DEVICEINTERFACE = 0x10;
        public const uint GENERIC_READ = 0x80000000;
        public const uint FILE_SHARE_READ = 0x1;
        public const uint FILE_SHARE_WRITE = 0x2;
        public const uint OPEN_EXISTING = 3;

        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVINFO_DATA
        {
            public uint cbSize;
            public Guid ClassGuid;
            public uint DevInst;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public struct SP_DEVICE_INTERFACE_DETAIL_DATA
        {
            public int cbSize;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 512)]
            public string DevicePath;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HIDD_ATTRIBUTES
        {
            public uint Size;
            public ushort VendorID;
            public ushort ProductID;
            public ushort VersionNumber;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HIDP_CAPS
        {
            public ushort Usage;
            public ushort UsagePage;
            public ushort InputReportByteLength;
            public ushort OutputReportByteLength;
            public ushort FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
            public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes;
            public ushort NumberInputButtonCaps;
            public ushort NumberInputValueCaps;
            public ushort NumberInputDataIndices;
            public ushort NumberOutputButtonCaps;
            public ushort NumberOutputValueCaps;
            public ushort NumberOutputDataIndices;
            public ushort NumberFeatureButtonCaps;
            public ushort NumberFeatureValueCaps;
            public ushort NumberFeatureDataIndices;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SECURITY_ATTRIBUTES
        {
            public int nLength;
            public IntPtr lpSecurityDescriptor;
            public bool bInheritHandle;
        }

        [DllImport("hid.dll", SetLastError = true)]
        public static extern void HidD_GetHidGuid(out Guid guid);

        [DllImport("setupapi.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, IntPtr hwndParent, int flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint memberIndex, ref SP_DEVINFO_DATA deviceInfoData);

        [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, ref Guid interfaceClassGuid, uint memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

        [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, ref SP_DEVICE_INTERFACE_DETAIL_DATA deviceInterfaceDetailData, uint deviceInterfaceDetailDataSize, ref uint requiredSize, IntPtr deviceInfoData);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, ref SECURITY_ATTRIBUTES securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_GetAttributes(SafeFileHandle hidDeviceObject, ref HIDD_ATTRIBUTES attributes);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_GetPreparsedData(SafeFileHandle hidDeviceObject, ref IntPtr preparsedData);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern uint HidP_GetCaps(IntPtr preparsedData, ref HIDP_CAPS capabilities);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_GetFeature(SafeFileHandle hidDeviceObject, byte[] reportBuffer, int reportBufferLength);
    }
}
