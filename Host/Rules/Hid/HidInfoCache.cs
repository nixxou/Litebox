// BigBoxProfile's HIDInfo, whole: seven detection backends, each producing "<>"-separated lines the
// matchers regex over, each cached per launch (the real channel clears at the start of a HID rule's
// Apply, exactly like the original's ClearCache at the top of Modify; the EXAMPLE channel never
// clears, so the page preview scans once and stays responsive). The seven are DELIBERATELY
// overlapping libraries — an emulator configured via SDL wants SDL's GUID, one via DirectInput wants
// the instance GUID, RetroArch's RawInput mode wants yet another spelling — so each backend calls
// the same library the emulator will and its exact output strings are the contract. Do not "fix"
// their formats (including SDL's interpolated struct-type-name field — see SdlBackend).
//
// Test mode: the selftests inject synthetic backend text and flip TestMode, which makes Clear() a
// no-op — matcher/action/pipeline logic runs deterministically with no hardware.

#nullable enable

using System;
using System.Security.Cryptography;
using System.Text;
using LbApiHost.Host.Diag;

namespace LbApiHost.Host.Rules.Hid;

internal static class HidInfoCache
{
    private const string Tag = "hid";

    private static string? _hidSharp, _ds4, _bt, _xinput, _dinput, _sdl, _sdlNoRI;
    internal static bool TestMode;

    /// <summary>Fresh scan on next access — the real launch channel's per-rule reset (BBP's
    /// ClearCache at the top of Modify). No-op in test mode so injected data survives the pipeline.</summary>
    public static void Clear()
    {
        if (TestMode) return;
        _hidSharp = _ds4 = _bt = _xinput = _dinput = _sdl = _sdlNoRI = null;
    }

    /// <summary>Selftests only: replaces every backend's output and pins it (Clear no-ops).</summary>
    public static void InjectForTest(string hidSharp = "", string ds4 = "", string bt = "",
        string xinput = "", string dinput = "", string sdl = "", string sdlNoRI = "")
    {
        TestMode = true;
        _hidSharp = hidSharp; _ds4 = ds4; _bt = bt; _xinput = xinput;
        _dinput = dinput; _sdl = sdl; _sdlNoRI = sdlNoRI;
    }

    public static string HidSharpInfo() => _hidSharp ??= Safe("hidsharp", HidSharpBackend.Dump);
    public static string Ds4Info()      => _ds4 ??= Safe("ds4", Ds4Backend.Dump);
    public static string BtInfo()       => _bt ??= Safe("bluetooth", BtBackend.Dump);
    public static string DInputInfo()   => _dinput ??= Safe("dinput", DInputBackend.Dump);
    public static string SdlInfo()      => _sdl ??= Safe("sdl", SdlBackend.Dump);
    public static string SdlNoRIInfo()  => _sdlNoRI ??= Safe("sdl-nori", SdlBackend.DumpNoRawInput);
    public static string XInputInfo(string ds4WinLogPath)
        => _xinput ??= Safe("xinput", () => XInputBackend.Dump(ds4WinLogPath));

    private static string Safe(string name, Func<string> dump)
    {
        try { return dump(); }
        catch (Exception ex) { LbLog.Warn(Tag, $"{name} backend failed ({ex.Message}) — empty"); return ""; }
    }

    /// <summary>BBP's GetMD5Short: MD5 of the UTF-8 text, uppercase hex, first 6 chars — the device
    /// signature users' regexes match on. Byte-for-byte compatible.</summary>
    public static string Md5Short(string input)
    {
        byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes(input));
        var sb = new StringBuilder(12);
        foreach (byte b in hash) sb.Append(b.ToString("X2"));
        return sb.ToString().Substring(0, 6);
    }
}
