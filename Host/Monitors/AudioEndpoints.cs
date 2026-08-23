// Playback DEVICE control for monitor profiles — pick the default sound card, set its master volume.
//
// Sibling of Host\Pause\AppAudio, which does the other half of CoreAudio: per-PROCESS session mute.
// The split is deliberate — that one answers "silence this game", this one answers "send sound to the
// TV at 40%". Same raw COM style, no NAudio, no external audio library.
//
// BigBoxProfile did this through AudioDeviceCmdlets' ClassLibrary.dll (a HintPath outside the repo),
// so nothing was portable; the four interfaces below are what that dependency actually provided.
//
// SetDefaultEndpoint goes through IPolicyConfig — undocumented but the only way to change the default
// playback device from user code, and the same mechanism every "audio switcher" utility uses. It is
// COM-activated by CLSID, so an OS that ever drops it fails cleanly (device switch skipped, logged)
// rather than crashing the profile: volume and every display part still apply.
//
// Devices are addressed by FRIENDLY NAME, not by endpoint id: an id is regenerated when a device is
// re-enumerated (driver update, port change), whereas "Speakers (Realtek High Definition Audio)" is
// what the user recognises and what survives. Same reasoning as DevicePath-over-LUID next door.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using LbApiHost.Host.Diag;

namespace LbApiHost.Host.Monitors;

internal static class AudioEndpoints
{
    private const string Tag = "monitors";
    private const int DeviceStateActive = 0x1;

    /// <summary>Friendly names of every active playback endpoint, for the editor's picker.</summary>
    public static List<string> Playback()
    {
        var names = new List<string>();
        IMMDeviceEnumerator? en = null;
        IMMDeviceCollection? col = null;
        try
        {
            en = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            if (en.EnumAudioEndpoints(EDataFlow.eRender, DeviceStateActive, out col) != 0 || col == null) return names;
            col.GetCount(out int n);
            for (int i = 0; i < n; i++)
            {
                IMMDevice? dev = null;
                try
                {
                    if (col.Item(i, out dev) != 0 || dev == null) continue;
                    var name = FriendlyName(dev);
                    if (!string.IsNullOrEmpty(name)) names.Add(name!);
                }
                catch { }
                finally { Release(dev); }
            }
        }
        catch (Exception ex) { LbLog.Warn(Tag, "audio enumeration failed: " + ex.Message); }
        finally { Release(col); Release(en); }
        return names;
    }

    /// <summary>Waits for a playback endpoint to show up, polling until <paramref name="timeoutMs"/>.
    /// Returns the milliseconds waited, or -1 if it never appeared.
    ///
    /// A monitor's own audio endpoint (HDMI, DisplayPort) only exists while that monitor is active, and
    /// Windows publishes it a moment AFTER the display topology settles. A profile that turns a screen on
    /// and selects its audio in the same breath would otherwise miss it by a fraction of a second and
    /// report "device not found" about a device that is on its way in.</summary>
    public static int WaitForPlayback(string friendlyName, int timeoutMs)
    {
        if (string.IsNullOrWhiteSpace(friendlyName)) return -1;
        const int StepMs = 300;
        for (int waited = 0; waited <= timeoutMs; waited += StepMs)
        {
            if (Playback().Any(d => string.Equals(d, friendlyName, StringComparison.OrdinalIgnoreCase)))
                return waited;
            if (waited + StepMs > timeoutMs) break;
            Thread.Sleep(StepMs);
        }
        return -1;
    }

    /// <summary>Friendly name of the current default playback device, or "" when unavailable.</summary>
    public static string CurrentDefault()
    {
        IMMDeviceEnumerator? en = null;
        IMMDevice? dev = null;
        try
        {
            en = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            if (en.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out dev) != 0 || dev == null) return "";
            return FriendlyName(dev) ?? "";
        }
        catch { return ""; }
        finally { Release(dev); Release(en); }
    }

    /// <summary>Makes the named endpoint the default for all three roles (console, multimedia,
    /// communications) — a half-switched default is worse than none. False when not found.</summary>
    public static bool SetDefault(string friendlyName)
    {
        if (string.IsNullOrWhiteSpace(friendlyName)) return false;
        string? id = IdOf(friendlyName);
        if (id == null) { LbLog.Warn(Tag, $"audio device not found: {friendlyName}"); return false; }

        // The coclass MUST be a [ComImport] type: activating the CLSID through Type.GetTypeFromCLSID
        // hands back a __ComObject whose cast to a plain managed interface is not a QueryInterface, so
        // it silently fails. `new CPolicyConfigClient()` on a ComImport class is what makes the cast a
        // real QI — the same shape AppAudio uses for MMDeviceEnumerator next door. Getting this wrong is
        // invisible: every display part of a profile applies, and only the sound card quietly doesn't.
        // TWO COCLASSES, EACH WITH ITS OWN INTERFACE — and they are not interchangeable, which is the
        // trap. Measured on Windows 10 LTSC 2021 (19044), CoCreateInstance itself answers:
        //     CPolicyConfigClient      -> IPolicyConfig       E_NOINTERFACE
        //     CPolicyConfigClient      -> IPolicyConfigVista  E_NOINTERFACE
        //     CPolicyConfigVistaClient -> IPolicyConfigVista  S_OK
        // Other builds answer the modern pair instead. So both pairs are tried, modern first, and the
        // Vista pair is a genuine fallback rather than politeness. The two vtables differ by one entry
        // (the Vista one has no SetDeviceFormat), which is why the interfaces cannot simply be swapped.
        //
        // The coclass MUST be a [ComImport] type: activating through Type.GetTypeFromCLSID hands back a
        // __ComObject whose cast to a plain managed interface is not a QueryInterface, and fails silently.
        foreach (var attempt in new Func<bool>[] { () => ViaModern(id), () => ViaVista(id) })
        {
            try { if (attempt()) { LbLog.Info(Tag, "default playback device → " + friendlyName); return true; } }
            catch (Exception ex) { LbLog.Warn(Tag, "SetDefaultEndpoint attempt failed: " + ex.Message); }
        }

        LbLog.Warn(Tag, "no usable IPolicyConfig on this OS — default device left unchanged");
        return false;
    }

    private static bool ViaModern(string id)
    {
        object? cfg = null;
        try
        {
            cfg = new CPolicyConfigClient();
            if (cfg is not IPolicyConfig pc) return false;
            foreach (var role in Roles) pc.SetDefaultEndpoint(id, role);
            return true;
        }
        finally { Release(cfg); }
    }

    private static bool ViaVista(string id)
    {
        object? cfg = null;
        try
        {
            cfg = new CPolicyConfigVistaClient();
            if (cfg is not IPolicyConfigVista pv) return false;
            foreach (var role in Roles) pv.SetDefaultEndpoint(id, role);
            return true;
        }
        finally { Release(cfg); }
    }

    /// <summary>All three roles: a half-switched default (media here, communications there) is worse
    /// than none, and is exactly what users report as "it didn't work".</summary>
    private static readonly ERole[] Roles = { ERole.eConsole, ERole.eMultimedia, ERole.eCommunications };

    /// <summary>Master volume of the default playback device, 0..100; -1 when unavailable.</summary>
    public static int GetVolume()
    {
        var vol = EndpointVolume();
        if (vol == null) return -1;
        try { return vol.GetMasterVolumeLevelScalar(out float f) == 0 ? (int)Math.Round(f * 100f) : -1; }
        catch { return -1; }
        finally { Release(vol); }
    }

    /// <summary>Sets the master volume of the default playback device (clamped to 0..100).</summary>
    public static bool SetVolume(int percent)
    {
        var vol = EndpointVolume();
        if (vol == null) return false;
        try
        {
            float f = Math.Clamp(percent, 0, 100) / 100f;
            Guid g = Guid.Empty;
            if (vol.SetMasterVolumeLevelScalar(f, ref g) != 0) return false;
            LbLog.Info(Tag, $"master volume → {percent}%");
            return true;
        }
        catch (Exception ex) { LbLog.Warn(Tag, "SetVolume failed: " + ex.Message); return false; }
        finally { Release(vol); }
    }

    // ── plumbing ─────────────────────────────────────────────────────────────

    private static IAudioEndpointVolume? EndpointVolume()
    {
        IMMDeviceEnumerator? en = null;
        IMMDevice? dev = null;
        try
        {
            en = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            if (en.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out dev) != 0 || dev == null) return null;
            var iid = typeof(IAudioEndpointVolume).GUID;
            if (dev.Activate(ref iid, 1 /* CLSCTX_INPROC_SERVER */, IntPtr.Zero, out object o) != 0) return null;
            return o as IAudioEndpointVolume;
        }
        catch { return null; }
        finally { Release(dev); Release(en); }
    }

    /// <summary>Endpoint id for a friendly name (first case-insensitive match), or null.</summary>
    private static string? IdOf(string friendlyName)
    {
        IMMDeviceEnumerator? en = null;
        IMMDeviceCollection? col = null;
        try
        {
            en = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            if (en.EnumAudioEndpoints(EDataFlow.eRender, DeviceStateActive, out col) != 0 || col == null) return null;
            col.GetCount(out int n);
            for (int i = 0; i < n; i++)
            {
                IMMDevice? dev = null;
                try
                {
                    if (col.Item(i, out dev) != 0 || dev == null) continue;
                    if (!string.Equals(FriendlyName(dev), friendlyName, StringComparison.OrdinalIgnoreCase)) continue;
                    return dev.GetId(out string id) == 0 ? id : null;
                }
                finally { Release(dev); }
            }
        }
        catch { }
        finally { Release(col); Release(en); }
        return null;
    }

    private static readonly PropertyKey PkeyDeviceFriendlyName =
        new() { FormatId = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), PropertyId = 14 };

    private static string? FriendlyName(IMMDevice dev)
    {
        IPropertyStore? store = null;
        try
        {
            if (dev.OpenPropertyStore(0 /* STGM_READ */, out store) != 0 || store == null) return null;
            var key = PkeyDeviceFriendlyName;
            if (store.GetValue(ref key, out PropVariant pv) != 0) return null;
            try { return pv.Pointer == IntPtr.Zero ? null : Marshal.PtrToStringUni(pv.Pointer); }
            finally { try { PropVariantClear(ref pv); } catch { } }
        }
        catch { return null; }
        finally { Release(store); }
    }

    private static void Release(object? com)
    {
        try { if (com != null && Marshal.IsComObject(com)) Marshal.ReleaseComObject(com); } catch { }
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pvar);

    // ── COM declarations (same hand-rolled style as Host\Pause\AppAudio) ──────

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator { }

    /// <summary>CPolicyConfigClient — the undocumented coclass behind the default-device switch.</summary>
    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    private class CPolicyConfigClient { }

    /// <summary>CPolicyConfigVistaClient — the older coclass, and the ONLY one that answers on some
    /// builds (verified on Windows 10 LTSC 2021). Pairs with IPolicyConfigVista, never with IPolicyConfig.</summary>
    [ComImport, Guid("294935CE-F637-4E7C-A41B-AB255460B862")]
    private class CPolicyConfigVistaClient { }

    private enum EDataFlow { eRender, eCapture, eAll }
    private enum ERole { eConsole, eMultimedia, eCommunications }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey { public Guid FormatId; public int PropertyId; }

    /// <summary>Minimal PROPVARIANT: the 8-byte header then the union. Only VT_LPWSTR is read
    /// (the friendly name), so the union is modelled as two pointers — 24 bytes on x64, which is
    /// what the real structure occupies.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public short VarType;
        public short Reserved1, Reserved2, Reserved3;
        public IntPtr Pointer;
        public IntPtr Pointer2;
    }

    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice device);
    }

    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int Item(int index, out IMMDevice device);
    }

    [Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(int stgmAccess, out IPropertyStore properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out int props);
        [PreserveSig] int GetAt(int index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    /// <summary>IAudioEndpointVolume — only the two master-volume scalar slots are used; the
    /// earlier vtable entries are placeholders so the slot INDEXES stay correct.</summary>
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int NotImpl0();   // RegisterControlChangeNotify
        [PreserveSig] int NotImpl1();   // UnregisterControlChangeNotify
        [PreserveSig] int NotImpl2();   // GetChannelCount
        [PreserveSig] int NotImpl3();   // SetMasterVolumeLevel
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
        [PreserveSig] int NotImpl5();   // GetMasterVolumeLevel
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
    }

    /// <summary>IPolicyConfig — undocumented; SetDefaultEndpoint is vtable slot 10, so the ten
    /// preceding entries must be declared even though they are never called.</summary>
    [Guid("f8679f50-850a-45de-be8b-c4348c9a005d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int NotImpl0();   // GetMixFormat
        [PreserveSig] int NotImpl1();   // GetDeviceFormat
        [PreserveSig] int NotImpl2();   // ResetDeviceFormat
        [PreserveSig] int NotImpl3();   // SetDeviceFormat
        [PreserveSig] int NotImpl4();   // GetProcessingPeriod
        [PreserveSig] int NotImpl5();   // SetProcessingPeriod
        [PreserveSig] int NotImpl6();   // GetShareMode
        [PreserveSig] int NotImpl7();   // SetShareMode
        [PreserveSig] int NotImpl8();   // GetPropertyValue
        [PreserveSig] int NotImpl9();   // SetPropertyValue
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ERole role);
        [PreserveSig] int NotImpl11();  // SetEndpointVisibility
    }

    /// <summary>The Vista-era shape of the same thing: one method fewer before SetDefaultEndpoint (no
    /// SetDeviceFormat), so the slot lands at 9 instead of 10.</summary>
    [Guid("568b9108-44bf-40b4-9006-86afe5b5a620"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfigVista
    {
        [PreserveSig] int NotImpl0();   // GetMixFormat
        [PreserveSig] int NotImpl1();   // GetDeviceFormat
        [PreserveSig] int NotImpl2();   // SetDeviceFormat
        [PreserveSig] int NotImpl3();   // GetProcessingPeriod
        [PreserveSig] int NotImpl4();   // SetProcessingPeriod
        [PreserveSig] int NotImpl5();   // GetShareMode
        [PreserveSig] int NotImpl6();   // SetShareMode
        [PreserveSig] int NotImpl7();   // GetPropertyValue
        [PreserveSig] int NotImpl8();   // SetPropertyValue
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ERole role);
        [PreserveSig] int NotImpl10();  // SetEndpointVisibility
    }
}
