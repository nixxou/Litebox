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
            var (exe, why) = ResolveReader(path);
            Console.WriteLine($"[docs] open \"{Path.GetFileName(path)}\" → {(exe != null ? "LB Reader" : "shell")} ({why})");
            if (exe != null)
            {
                // -fullscreen: probed empirically (window covers the screen instead of the default
                // windowed 26,26 frame; the document still loads). LbReaderFullscreen ini key.
                bool fs = LiteBoxConfig.LoadForExe().GetBool("LbReaderFullscreen", false);
                Process.Start(new ProcessStartInfo(exe, $"{(fs ? "-fullscreen " : "")}\"{path}\"") { UseShellExecute = false });
                return;
            }
        }
        catch (Exception ex) { Console.WriteLine("[docs] LB Reader launch failed — shell fallback: " + ex.Message); }
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { Console.WriteLine("[docs] open failed: " + ex.Message); }
    }

    /// <summary>The Reader exe to use for <paramref name="path"/> (+ the routing reason for the
    /// log), exe null → shell (option off, unsupported format, pre-14 / Reader not deployed).</summary>
    private static (string? exe, string why) ResolveReader(string path)
    {
        if (!LiteBoxConfig.LoadForExe().GetBool("UseLbReaderForDocs", false)) return (null, "UseLbReaderForDocs=false");
        if (!ReaderExts.Contains(Path.GetExtension(path))) return (null, "format not Reader-supported: " + Path.GetExtension(path));
        string root = MediaResolver.LbRoot ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
        string exe = Path.Combine(root, "System", "Software", "LaunchBox Reader", "LaunchBox.Reader.exe");
        return File.Exists(exe) ? (exe, "ok") : (null, "Reader not found: " + exe);
    }
}
