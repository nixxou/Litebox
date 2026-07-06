// Per-process audio session control (WASAPI CoreAudio, raw COM interop — no NAudio).
// Ported from Mehdi's BigBoxProfile VolumeMixer (github.com/nixxou/BigBoxProfile), the
// field-proven "mute one precise process" building block, trimmed to what the pause
// screen needs: find the process's audio session(s) on the default render device and
// flip their mute. Differences from the original: every session matching the pid is
// touched (an emulator can open several), and we use SetMute rather than volume-0 so
// the user's per-app volume is never clobbered.
//
// Drives LB's "Mute Audio During Transitions" (Settings.xml PauseScreenMuting):
// PauseManager mutes right when the pause kicks in and restores on resume/teardown.
// A process with no audio session yet (no sound played) simply yields no-op — callers
// treat false as "nothing muted".

#nullable enable

using System.Runtime.InteropServices;

namespace LbApiHost.Host.Pause;

internal static class AppAudio
{
    /// <summary>Sets the mute flag on EVERY audio session of <paramref name="pid"/> on the
    /// default render device. Returns true when at least one session was touched.</summary>
    public static bool SetMute(int pid, bool mute)
    {
        bool touched = false;
        ForEachSession(pid, vol => { Guid g = Guid.Empty; vol.SetMute(mute, ref g); touched = true; });
        return touched;
    }

    /// <summary>True when the process has at least one audio session and ALL of them are
    /// muted; false when any plays unmuted; null when no session exists (no audio yet).</summary>
    public static bool? GetMute(int pid)
    {
        bool any = false, all = true;
        ForEachSession(pid, vol => { vol.GetMute(out bool m); any = true; if (!m) all = false; });
        return any ? all : null;
    }

    private static void ForEachSession(int pid, Action<ISimpleAudioVolume> action)
    {
        IMMDeviceEnumerator? deviceEnumerator = null;
        IMMDevice? speakers = null;
        IAudioSessionManager2? mgr = null;
        IAudioSessionEnumerator? sessionEnumerator = null;
        try
        {
            deviceEnumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            deviceEnumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out speakers);

            Guid iid = typeof(IAudioSessionManager2).GUID;
            speakers.Activate(ref iid, 0, IntPtr.Zero, out object o);
            mgr = (IAudioSessionManager2)o;

            mgr.GetSessionEnumerator(out sessionEnumerator);
            sessionEnumerator.GetCount(out int count);

            for (int i = 0; i < count; i++)
            {
                IAudioSessionControl2? ctl = null;
                try
                {
                    sessionEnumerator.GetSession(i, out ctl);
                    ctl.GetProcessId(out int cpid);
                    if (cpid == pid && ctl is ISimpleAudioVolume vol) action(vol);
                }
                catch { }
                finally { if (ctl != null) Marshal.ReleaseComObject(ctl); }
            }
        }
        catch { }
        finally
        {
            if (sessionEnumerator != null) Marshal.ReleaseComObject(sessionEnumerator);
            if (mgr != null) Marshal.ReleaseComObject(mgr);
            if (speakers != null) Marshal.ReleaseComObject(speakers);
            if (deviceEnumerator != null) Marshal.ReleaseComObject(deviceEnumerator);
        }
    }

    // ── COM plumbing (verbatim shapes from the BigBoxProfile original) ──────────

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator { }

    private enum EDataFlow { eRender, eCapture, eAll }
    private enum ERole { eConsole, eMultimedia, eCommunications }

    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int NotImpl1();
        [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice ppDevice);
    }

    [Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
    }

    [Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        int NotImpl1();
        int NotImpl2();
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);
    }

    [Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int sessionCount);
        [PreserveSig] int GetSession(int index, out IAudioSessionControl2 session);
    }

    [Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float fLevel, ref Guid eventContext);
        [PreserveSig] int GetMasterVolume(out float pfLevel);
        [PreserveSig] int SetMute(bool bMute, ref Guid eventContext);
        [PreserveSig] int GetMute(out bool pbMute);
    }

    [Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        // IAudioSessionControl
        [PreserveSig] int NotImpl0();
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string value, [MarshalAs(UnmanagedType.LPStruct)] Guid eventContext);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string value, [MarshalAs(UnmanagedType.LPStruct)] Guid eventContext);
        [PreserveSig] int GetGroupingParam(out Guid pRetVal);
        [PreserveSig] int SetGroupingParam([MarshalAs(UnmanagedType.LPStruct)] Guid Override, [MarshalAs(UnmanagedType.LPStruct)] Guid eventContext);
        [PreserveSig] int NotImpl1();
        [PreserveSig] int NotImpl2();
        // IAudioSessionControl2
        [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
        [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
        [PreserveSig] int GetProcessId(out int pRetVal);
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference(bool optOut);
    }
}
