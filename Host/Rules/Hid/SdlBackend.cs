// The SDL backends — BBP's GetSDLInfo / GetSDLNoRIInfo, through the SAME managed binding
// (ppy.SDL2-CS 1.0.82) over the SAME native SDL2.dll (2.0.14) the original shipped. Lines:
//   SDL{i}<>{NameForIndex}<>{capsSig}<>{struct}<>{serial}<>{guidString}<>VendorID=0x....<>ProductID=0x....
//   SDLNORI{i}<>{NameForIndex}<>{capsSig}<>{struct}<>{serial}<>{guidString}
// where capsSig = Md5Short("axes balls buttons hats"), guidString = SDL's 32-hex device GUID (what
// emulators store), and {struct} is — faithfully — the literal "SDL2.SDL+SDL_JoystickGUID": the
// original interpolated the raw struct return, which prints its type name. Users' regexes were
// written against these exact lines, quirk included, so the quirk ships.
//
// The NoRI variant re-inits SDL with the RawInput hint off — some emulators run SDL that way and
// see a different device list/order. SDL_Quit before each init mirrors the original (SDL state is
// process-global; nothing else in LiteBox uses SDL).

#nullable enable

using System.Runtime.CompilerServices;
using System.Text;
using SDL2;

namespace LbApiHost.Host.Rules.Hid;

internal static class SdlBackend
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string Dump()
    {
        HidThirdParty.EnsureResolver();
        if (!HidThirdParty.EnsureSdlNative()) return "";
        return DumpCore(rawInputOff: false, prefix: "SDL", withIds: true);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string DumpNoRawInput()
    {
        HidThirdParty.EnsureResolver();
        if (!HidThirdParty.EnsureSdlNative()) return "";
        return DumpCore(rawInputOff: true, prefix: "SDLNORI", withIds: false);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string DumpCore(bool rawInputOff, string prefix, bool withIds)
    {
        var sb = new StringBuilder();
        SDL.SDL_Quit();
        if (rawInputOff) SDL.SDL_SetHint(SDL.SDL_HINT_JOYSTICK_RAWINPUT, "0");
        SDL.SDL_Init(SDL.SDL_INIT_JOYSTICK | SDL.SDL_INIT_GAMECONTROLLER);
        SDL.SDL_JoystickUpdate();

        for (int i = 0; i < SDL.SDL_NumJoysticks(); i++)
        {
            var currentJoy = SDL.SDL_JoystickOpen(i);
            string caps = $"{SDL.SDL_JoystickNumAxes(currentJoy)} {SDL.SDL_JoystickNumBalls(currentJoy)} {SDL.SDL_JoystickNumButtons(currentJoy)} {SDL.SDL_JoystickNumHats(currentJoy)}";
            string signature = HidInfoCache.Md5Short(caps);
            const int bufferSize = 256;
            byte[] guidBuffer = new byte[bufferSize];
            SDL.SDL_JoystickGetGUIDString(SDL.SDL_JoystickGetGUID(currentJoy), guidBuffer, bufferSize);
            string guidString = Encoding.UTF8.GetString(guidBuffer).Trim('\0');

            if (withIds)
            {
                ushort vendorId = SDL.SDL_JoystickGetVendor(currentJoy);
                ushort productId = SDL.SDL_JoystickGetProduct(currentJoy);
                sb.Append($"{prefix}{i}<>{SDL.SDL_JoystickNameForIndex(i)}<>{signature}<>{SDL.SDL_JoystickGetDeviceGUID(i)}<>{SDL.SDL_JoystickGetSerial(currentJoy)}<>{guidString}<>VendorID=0x{vendorId:X04}<>ProductID=0x{productId:X04}").Append("\r\n");
            }
            else
            {
                sb.Append($"{prefix}{i}<>{SDL.SDL_JoystickNameForIndex(i)}<>{signature}<>{SDL.SDL_JoystickGetDeviceGUID(i)}<>{SDL.SDL_JoystickGetSerial(currentJoy)}<>{guidString}").Append("\r\n");
            }
            SDL.SDL_JoystickClose(currentJoy);
        }
        return sb.ToString();
    }
}
