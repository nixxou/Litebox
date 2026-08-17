// How a game document opens: the system default app (shell) — or, opt-in via LiteBox.ini
// UseLbReaderForDocs=true, LaunchBox 14's built-in Reader (System\Software\LaunchBox Reader),
// the page-turn reading UI LB 14 ships for manuals/guides/magazines.
//
// The Reader's REAL command-line contract, captured from a live LB 14 launch (Win32_Process
// CommandLine while LB opened a manual):
//
//   --path "<file>" --document-title Manual --game-title "Age of Wonders" --platform Windows
//   --launch-window-handle 0x140C22 --fullscreen
//
// --launch-window-handle is how the Reader lands on the SAME MONITOR as its launcher (and what
// its lifetime is tied to); --fullscreen is what LB itself passes (windowed without it — probed
// by window-rect measurement). A bare path also works, but we speak the full contract.
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

    /// <summary>Open a document (manual or additional document) per the configured mode.
    /// The optional metadata mirrors LB 14's own Reader invocation; <paramref name="launchWindow"/>
    /// puts the Reader on that window's monitor (the game's, in pause; LiteBox's elsewhere).</summary>
    public static void Open(string path, string? docTitle = null, string? gameTitle = null,
                            string? platform = null, IntPtr launchWindow = default)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            var (exe, why) = ResolveReader(path);
            Console.WriteLine($"[docs] open \"{Path.GetFileName(path)}\" → {(exe != null ? "LB Reader" : "shell")} ({why})");
            if (exe == ExternalReaderMarker)
            {
                // An external viewer wins over the shell: LB's configured one on 14+, LiteBox's own
                // ini path when this LaunchBox has no Reader at all (see ReaderOptions).
                var ext = ReaderSettingsDb.LoadGlobal()?.ExternalReaderExecutablePath ?? "";
                if (ext.Length == 0) ext = LiteBoxConfig.LoadForExe().Get(Options.ReaderOptions.ExternalReaderKey, "") ?? "";
                if (ext.Length > 0 && File.Exists(ext))
                {
                    Process.Start(new ProcessStartInfo(ext, $"\"{path}\"") { UseShellExecute = false });
                    return;
                }
            }
            else if (exe != null)
            {
                var args = new System.Text.StringBuilder();
                args.Append($"--path \"{path}\"");
                args.Append($" --document-title \"{Clean(docTitle ?? Path.GetFileNameWithoutExtension(path))}\"");
                if (!string.IsNullOrEmpty(gameTitle)) args.Append($" --game-title \"{Clean(gameTitle!)}\"");
                if (!string.IsNullOrEmpty(platform)) args.Append($" --platform \"{Clean(platform!)}\"");
                if (launchWindow != IntPtr.Zero) args.Append($" --launch-window-handle 0x{launchWindow.ToInt64():X}");
                // Fullscreen follows LB'S OWN setting (Options → Reader → "Open in fullscreen",
                // GlobalSettings.FullscreenByDefault) so both apps behave identically; the ini key is
                // only the fallback when the Reader has no settings DB yet.
                bool fs = ReaderSettingsDb.LoadGlobal()?.FullscreenByDefault
                          ?? LiteBoxConfig.LoadForExe().GetBool("LbReaderFullscreen", true);
                if (fs) args.Append(" --fullscreen");
                Process.Start(new ProcessStartInfo(exe, args.ToString()) { UseShellExecute = false });
                return;
            }
        }
        catch (Exception ex) { Console.WriteLine("[docs] LB Reader launch failed — shell fallback: " + ex.Message); }
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { Console.WriteLine("[docs] open failed: " + ex.Message); }
    }

    /// <summary>Argument-safe: a stray quote in a title must not break the command line.</summary>
    private static string Clean(string s) => s.Replace('"', '\'');

    /// <summary>Sentinel: LB's provider says "an external viewer", handled by the caller.</summary>
    private const string ExternalReaderMarker = "\0external";

    /// <summary>How <paramref name="path"/> must open (+ the routing reason for the log): the Reader
    /// exe, <see cref="ExternalReaderMarker"/>, or null → shell (LiteBox's opt-in off, LB's provider
    /// set to the default application, unsupported format, pre-14 / Reader not deployed).</summary>
    private static (string? exe, string why) ResolveReader(string path)
    {
        // No LaunchBox Reader on this install (pre-14, or not deployed): the only alternative to the
        // shell is the external viewer LiteBox keeps in its own ini — LB's settings stay untouched.
        if (!ReaderSettingsDb.Available)
        {
            var own = LiteBoxConfig.LoadForExe().Get(Options.ReaderOptions.ExternalReaderKey, "") ?? "";
            return own.Length > 0 && File.Exists(own)
                ? (ExternalReaderMarker, "external reader (LiteBox.ini)")
                : (null, "no LaunchBox Reader on this install");
        }
        if (!LiteBoxConfig.LoadForExe().GetBool("UseLbReaderForDocs", false)) return (null, "UseLbReaderForDocs=false");
        // LB's own provider choice (Options → Reader → Reader Provider) is honoured when the Reader
        // settings DB exists — one setting for both apps.
        var g = ReaderSettingsDb.LoadGlobal();
        if (g != null)
        {
            if (string.Equals(g.ReaderProvider, "DefaultApplication", StringComparison.OrdinalIgnoreCase))
                return (null, "LB ReaderProvider=DefaultApplication");
            if (string.Equals(g.ReaderProvider, "ExternalReader", StringComparison.OrdinalIgnoreCase))
                return (ExternalReaderMarker, "LB ReaderProvider=ExternalReader");
        }
        if (!ReaderExts.Contains(Path.GetExtension(path))) return (null, "format not Reader-supported: " + Path.GetExtension(path));
        string root = MediaResolver.LbRoot ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
        string exe = Path.Combine(root, "System", "Software", "LaunchBox Reader", "LaunchBox.Reader.exe");
        return File.Exists(exe) ? (exe, "ok") : (null, "Reader not found: " + exe);
    }
}
