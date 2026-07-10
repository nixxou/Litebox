// "Hide Mouse Cursor During Game" (LB emulator/game field HideMouseCursorInGame). Unlike the startup
// OVERLAY cursor hide (Cursor.Hide, which only affects our own windows), this must hide the cursor over
// ANOTHER process's window for the whole game. The reliable cross-process way is to swap every standard
// SYSTEM cursor for a transparent one; restore reloads the user's real cursors from the registry via
// SystemParametersInfo(SPI_SETCURSORS) — robust even if we die mid-game (a ProcessExit hook + the next
// launch's Show both re-assert). Armed at launch when the flag resolves true, disarmed in the finally.

#nullable enable

using System;
using System.Runtime.InteropServices;

namespace LbApiHost.Host.Gameplay;

internal static class GameCursor
{
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetSystemCursor(IntPtr hcur, uint id);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr CreateCursor(IntPtr hInst, int xHot, int yHot, int nWidth, int nHeight, byte[] andPlane, byte[] xorPlane);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);
    private const uint SPI_SETCURSORS = 0x0057;

    // Every standard OCR_* system cursor id (arrow, ibeam, wait, cross, hand, sizers, …). SetSystemCursor
    // replaces ONE id and takes ownership of the handle, so each id gets its own fresh blank cursor.
    private static readonly uint[] OcrIds =
        { 32512, 32513, 32514, 32515, 32516, 32631, 32640, 32641, 32642, 32643, 32644, 32645, 32646, 32648, 32649, 32650, 32651 };

    private static readonly object _lock = new();
    private static bool _hidden;
    private static bool _exitHooked;

    /// <summary>Hide the system cursor for the whole desktop (idempotent). Safe to call for any launch type.</summary>
    public static void Hide()
    {
        lock (_lock)
        {
            if (_hidden) return;
            try
            {
                const int w = 32, h = 32;
                var and = new byte[w * h / 8]; var xor = new byte[w * h / 8];
                for (int i = 0; i < and.Length; i++) { and[i] = 0xFF; xor[i] = 0x00; }   // AND=1,XOR=0 ⇒ fully transparent
                int n = 0;
                foreach (var id in OcrIds)
                {
                    var blank = CreateCursor(IntPtr.Zero, 0, 0, w, h, and, xor);   // one per id — SetSystemCursor consumes it
                    if (blank != IntPtr.Zero && SetSystemCursor(blank, id)) n++;
                }
                _hidden = true;
                if (!_exitHooked) { _exitHooked = true; AppDomain.CurrentDomain.ProcessExit += (_, _) => Show(); }
                Console.WriteLine($"[gamecursor] hidden ({n} system cursors swapped)");
            }
            catch (Exception ex) { Console.WriteLine("[gamecursor] hide failed: " + ex.Message); }
        }
    }

    /// <summary>Restore the user's real system cursors (idempotent). Reloads them from the registry, so it
    /// fixes the cursor even if a prior run crashed without restoring.</summary>
    public static void Show()
    {
        lock (_lock)
        {
            if (!_hidden) return;
            _hidden = false;
            try { SystemParametersInfo(SPI_SETCURSORS, 0, IntPtr.Zero, 0); Console.WriteLine("[gamecursor] restored"); }
            catch (Exception ex) { Console.WriteLine("[gamecursor] restore failed: " + ex.Message); }
        }
    }
}
