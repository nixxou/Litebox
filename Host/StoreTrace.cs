// Lightweight file tracer for the store install-state machinery (boot sync, focus re-sync, the
// per-selection poll, and each field change SyncCore makes). Writes to <Core>\litebox-store.log so
// it can be read live during a GUI session (Console isn't visible in WinExe mode). Append-only,
// fail-soft. This is a diagnostic aid; it can be left in (cheap) or gated/removed later.

#nullable enable

using System;
using System.IO;

namespace LbApiHost.Host;

internal static class StoreTrace
{
    private static readonly object _lock = new();
    private static string? _path;
    private static string Path_ => _path ??= System.IO.Path.Combine(AppContext.BaseDirectory, "litebox-store.log");

    public static void Log(string msg)
    {
        try
        {
            lock (_lock)
                File.AppendAllText(Path_, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg + Environment.NewLine);
        }
        catch { }
    }
}
