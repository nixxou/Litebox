// How a game document opens: the system default app (shell) — or, opt-in via LiteBox.ini
// UseLbReaderForDocs=true, LaunchBox 14's built-in Reader (System\Software\LaunchBox Reader),
// the page-turn reading UI LB 14 ships for manuals/guides/magazines. The Reader takes the
// document path as its single argument (probed empirically: window title carries the document
// name once it loads).
//
// Only formats the Reader actually LOADS are routed to it; everything else keeps the shell even
// when the option is on (an empty Reader window helps nobody). Fail-soft everywhere: option off,
// pre-14 install, Reader missing, launch failure — all fall back to the shell open.
//
// ONE opener for every surface (pause screen, game context menu, Documents page), so the option
// applies uniformly.

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace LbApiHost.Host.Media;

internal static class DocOpener
{
    /// <summary>Formats LB Reader 1.0.2 actually loads, probed on the real v14 install (title bar
    /// carries the document name on success): pdf/txt/cbz/cbr + images. docx, html and md open an
    /// EMPTY Reader — those stay on the shell. epub: untested here (no sample) but a documented
    /// Reader format (14.0 changelog), so it routes too.</summary>
    private static readonly HashSet<string> ReaderExts = new(StringComparer.OrdinalIgnoreCase)
    { ".pdf", ".epub", ".txt", ".cbz", ".cbr", ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp" };

    /// <summary>Open a document (manual or additional document) per the configured mode.</summary>
    public static void Open(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            var exe = ReaderExeIfEnabled(path);
            if (exe != null)
            {
                Process.Start(new ProcessStartInfo(exe, $"\"{path}\"") { UseShellExecute = false });
                return;
            }
        }
        catch (Exception ex) { Console.WriteLine("[docs] LB Reader launch failed — shell fallback: " + ex.Message); }
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { Console.WriteLine("[docs] open failed: " + ex.Message); }
    }

    /// <summary>The Reader exe to use for <paramref name="path"/>, or null → shell (option off,
    /// unsupported format, pre-14 install / Reader not deployed).</summary>
    private static string? ReaderExeIfEnabled(string path)
    {
        if (!LiteBoxConfig.LoadForExe().GetBool("UseLbReaderForDocs", false)) return null;
        if (!ReaderExts.Contains(Path.GetExtension(path))) return null;
        string root = MediaResolver.LbRoot ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
        string exe = Path.Combine(root, "System", "Software", "LaunchBox Reader", "LaunchBox.Reader.exe");
        return File.Exists(exe) ? exe : null;
    }
}
