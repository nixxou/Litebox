// Steam achievements helper plumbing. Because Steamworks binds ONE appid per process, LiteBox queries a
// game's unlock state by re-launching ITSELF as a short-lived helper: "LiteBox.exe --steam-ach <appid>"
// writes the result as one JSON line to stdout, then exits. Mirrors SAM's one-process-per-appid model.
//
//   RunHelperMode(appid)  — the --steam-ach entry point (Program.cs): set up steam_appid.txt + env + CWD,
//                           read via SteamWorksNative, print JSON, exit.
//   Query(appid)          — the in-app caller (SteamAchievements provider): spawn the helper, parse JSON.
//   EnsureDll()           — deploy the bundled genuine Valve steam_api64.dll to LB\ThirdParty\Steam\.

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace LbApiHost.Host.Store;

internal static class SteamHelper
{
    /// <summary>JSON shape exchanged between the helper process and the provider.</summary>
    internal sealed class Result
    {
        public bool ok { get; set; }
        public string? error { get; set; }
        public string? appId { get; set; }
        public int total { get; set; }
        public List<SteamWorksNative.RawAch> achievements { get; set; } = new();
    }

    // LB root from the exe location (Core\.. = LB), independent of MediaResolver (which isn't
    // initialised on the early --steam-ach path).
    private static string LbRoot
    {
        get
        {
            var core = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return Path.GetDirectoryName(core) ?? core;
        }
    }

    /// <summary>Returns the steam_api64.dll path in LB\ThirdParty\Steam\. The DLL is deployed by
    /// NativeInstaller (embedded → ThirdParty); this triggers that deploy if it hasn't run yet.
    /// Null when it can't be made available.</summary>
    public static string? EnsureDll()
    {
        try
        {
            string dst = Path.Combine(LbRoot, "ThirdParty", "Steam", "steam_api64.dll");
            if (!File.Exists(dst)) LbApiHost.Host.Install.NativeInstaller.EnsureDeployed(LbRoot);
            return File.Exists(dst) ? dst : null;
        }
        catch { return null; }
    }

    /// <summary>--steam-ach &lt;appid&gt; entry point. Prints exactly one JSON line and returns an exit code.</summary>
    public static int RunHelperMode(string? appId)
    {
        var res = new Result { appId = appId };
        if (string.IsNullOrWhiteSpace(appId)) { res.error = "missing appid"; Emit(res); return 1; }

        string? dll = EnsureDll();
        if (dll == null) { res.error = "steam_api64.dll not available"; Emit(res); return 1; }

        string work = Path.Combine(Path.GetTempPath(), "litebox-steamach-" + Environment.ProcessId);
        try
        {
            Directory.CreateDirectory(work);
            File.WriteAllText(Path.Combine(work, "steam_appid.txt"), appId);
            Environment.SetEnvironmentVariable("SteamAppId", appId);
            Environment.SetEnvironmentVariable("SteamGameId", appId);
            Environment.CurrentDirectory = work;   // SteamAPI_Init reads steam_appid.txt from CWD

            var list = SteamWorksNative.Read(appId!, dll, out string err);
            if (list == null) { res.error = err; Emit(res); return 1; }
            res.ok = true; res.total = list.Count; res.achievements = list;
            Emit(res);
            return 0;
        }
        catch (Exception ex) { res.error = ex.Message; Emit(res); return 1; }
        finally { try { Directory.Delete(work, true); } catch { } }
    }

    private static void Emit(Result r)
    { try { Console.Out.Write(JsonSerializer.Serialize(r)); Console.Out.Flush(); } catch { } }

    /// <summary>Spawns the helper for <paramref name="appId"/> and returns its parsed result (null on
    /// spawn/timeout failure). BLOCKING — call off the UI thread.</summary>
    public static Result? Query(string appId, int timeoutMs = 12000)
    {
        try
        {
            string self = Process.GetCurrentProcess().MainModule?.FileName ?? "";
            if (string.IsNullOrEmpty(self)) return null;
            var psi = new ProcessStartInfo
            {
                FileName = self,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("--steam-ach");
            psi.ArgumentList.Add(appId);
            using var p = Process.Start(psi);
            if (p == null) return null;
            var outTask = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return null; }
            string stdout = outTask.GetAwaiter().GetResult();
            int i = stdout.IndexOf('{');   // tolerate any stray leading output
            if (i < 0) return null;
            return JsonSerializer.Deserialize<Result>(stdout.Substring(i));
        }
        catch { return null; }
    }
}
