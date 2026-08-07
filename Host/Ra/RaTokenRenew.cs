// Automatic RetroAchievements token renewal — the fix for LaunchBox's "token silently expired" problem.
//
// LaunchBox obtains the session token once (Step 1 login) and never refreshes it; when it expires,
// achievements stop unlocking in the emulator until the user re-enters the password. If the user has
// let LiteBox keep the password (encrypted, RaPanelConfig), we re-login on a cadence and rewrite the
// token in Settings.xml — so the emulator keeps getting a valid token with no manual step.
//
// Cadence: renew when the stored token is at least RaPanelConfig.RenewEveryDays old (a missing
// timestamp is treated as due, to establish a known-fresh baseline).
//
// FAIL-SAFE (hard rule): if the re-login fails for ANY reason — offline, no internet, site down, non-2xx,
// bad response, rejected credentials — the ACTIVE token is left completely untouched and the timestamp
// is not advanced, so the next cycle retries. We never replace a working token with a failure.
//
// Read-only: writing the token edits LaunchBox's Settings.xml, so renewal is skipped in read-only
// mode. The mode is read LIVE at each firing (CanWrite delegate) — a boot-time latch went stale the
// moment the user toggled read-only in the options mid-session. Also skipped while LB/BB runs: LB
// rewrites Settings.xml wholesale at exit, so a token written under it is doomed anyway — skipping
// keeps the timestamp unstamped and the next cycle retries once the field is clear.

#nullable enable

using System;
using System.Threading.Tasks;

namespace LbApiHost.Host.Ra;

internal static class RaTokenRenew
{
    private static int _running;   // single-flight across boot + heartbeat

    /// <summary>Live "may I write Settings.xml" source, wired at boot to the data manager's
    /// read-only state. Read at each firing — never latched. Null (not wired) = no.</summary>
    public static Func<bool>? CanWrite;

    /// <summary>Renew the token if it is due and credentials are on file. Fire-and-forget (runs on a
    /// background thread); safe to call repeatedly. No-op unless a password is stored and it is due.</summary>
    public static void MaybeRenewAsync()
    {
        if (System.Threading.Interlocked.Exchange(ref _running, 1) == 1) return;
        Task.Run(() =>
        {
            try { RenewIfDue(); }
            catch (Exception ex) { Log("failed: " + ex.Message); }
            finally { System.Threading.Interlocked.Exchange(ref _running, 0); }
        });
    }

    private static void RenewIfDue()
    {
        if (CanWrite?.Invoke() != true) return;         // read-only (live) → never write Settings.xml
        if (LbApiHost.Host.Data.GameStore.IsLaunchBoxRunning()) return;   // LB owns Settings.xml right now
        if (!RaPanelConfig.HasPassword) return;         // nothing to re-login with

        string user = RaTokenStore.Username();
        string pass = RaPanelConfig.PasswordClear;
        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrEmpty(pass)) return;

        var obtained = RaPanelConfig.TokenObtainedUtc;
        if (obtained.HasValue)
        {
            double ageDays = (DateTime.UtcNow - obtained.Value).TotalDays;
            if (ageDays < RaPanelConfig.RenewEveryDays) return;   // not due yet
        }
        // no timestamp → treat as due (establish a baseline)

        var res = RaConnect.Login(user, pass);
        if (!res.Ok || string.IsNullOrEmpty(res.Token))
        {
            Log($"renewal skipped — login failed ({res.Error}); keeping the existing token.");
            return;                                     // FAIL-SAFE: leave the active token in place
        }

        if (!RaTokenStore.Write(res.Token))
        {
            Log("renewal got a fresh token but Settings.xml write failed; keeping the existing token.");
            return;
        }
        RaPanelConfig.StampTokenObtained(DateTime.UtcNow);
        Log("token renewed and written to Settings.xml.");
    }

    private static void Log(string msg) => Console.WriteLine("[ra] token-renew: " + msg);
}
