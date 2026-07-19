// POST endpoints that write to LiteBox's library / drive a launch on behalf of the theme surfaces:
//
//   POST /bigbox|launchbox/api/games/{id}/rating   body { "value": 0..5 }
//   POST …/api/games/{id}/favorite                 body { "value": true|false }
//   POST …/api/games/{id}/hide                     body { "value": true|false }
//   POST …/api/games/{id}/broken                   body { "value": true|false }
//   POST …/api/games/{id}/play                     (no body) → launch the game
//   POST …/api/games/{id}/resethistory             clear last-launch memory
//
// {id} is the LaunchBox GUID string (or the metadata DatabaseID, matched against IGame.LaunchBoxDbId).
//
// Clean-room LiteBox rewrite of ExtendDB's Web/Theme/BigBoxMutationApi.cs, with the two seams cut:
//   • PLAY — instead of WPF PluginHelper.*MainViewModel.PlayGame on Application.Current.Dispatcher, resolve the
//     IGame and call LiteBox's native HostLaunch.Launch("web", game, null, null, null) on the WinForms UI
//     thread (UiThread.Invoke). HostLaunch already owns emulator resolution, launch history (recorded in the
//     launch pipeline) and extraction deferral — it is the native equivalent of the plugin's PlayGame.
//   • favorite/hide/broken/rating — the game's setter (routes to the op-log) + DataManager.Save(false).
//
// Archive / Select-ROM mutations are served by the dedicated handlers (ArchiveListingApi / ArchiveMetadataApi).

using System;
using System.Text.Json;
using System.Threading.Tasks;
using LbApiHost.Host;
using LbApiHost.Host.Data;
using LbApiHost.Host.Diag;
using LbApiHost.Host.Parental;
using LbApiHost.Host.Rom;
using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Web;

internal static class BigBoxMutationApi
{
    public static HttpResponse Handle(RouteContext ctx)
    {
        if (!string.Equals(ctx.Request?.Method, "POST", StringComparison.OrdinalIgnoreCase))
            return HttpResponse.PlainText("Method not allowed", 405);

        var kind = (ctx.GetRoute("kind") ?? "").ToLowerInvariant();
        var id = ctx.GetRoute("id");
        if (string.IsNullOrEmpty(id)) return Fail("bad id");

        // Archive verbs (archive-entries / archive-favorite / archive-metadata) are DASHED, so they don't
        // match the [a-z]+ {kind} capture — they route to ArchiveListingApi/ArchiveMetadataApi directly. If
        // one still reaches here (mis-registration), fail closed rather than silently 200.
        if (kind.StartsWith("archive", StringComparison.OrdinalIgnoreCase))
            return NotAvailable("archive routes are served by the dedicated handlers");

        try
        {
            return kind switch
            {
                "rating"       => SetRating(id, ctx.Request.Body, ctx),
                "favorite"     => SetFlag(id, ctx, (g, v) => g.Favorite = v, "favorite"),
                "hide"         => SetFlag(id, ctx, (g, v) => g.Hide = v, "hide"),
                "broken"       => SetFlag(id, ctx, (g, v) => g.Broken = v, "broken"),
                "play"         => Play(id, ctx),
                "launch"       => Play(id, ctx),   // alias
                "resethistory" => ResetHistory(id),
                "install"      => Install(id, ctx),
                _              => Fail("unknown action"),
            };
        }
        catch (Exception ex) { Log($"{kind}({id}) threw: {ex.Message}"); return Fail("server error"); }
    }

    // ── play (native launch — fire-and-forget on the WinForms UI thread) ────────

    private static HttpResponse Play(string id, RouteContext ctx)
    {
        // Server-authoritative anti-double-launch: refuse while one is already in flight.
        if (RecentState.IsGameRunning) { Log($"play id={id} refused: a game is already running"); return Fail("game already running"); }
        if (RecentState.IsExtractionInProgress) { Log($"play id={id} refused: an archive extraction is in progress"); return Fail("extraction in progress"); }

        var game = ResolveGame(id);
        if (game == null) return Fail("not in library");

        // Optional launch selection carried by the Select-ROM / Select-Version sub-menus (all optional):
        //   emulatorId / additionalAppId  → the alt emulator / disc the archive is resolved from,
        //   archiveEntryFileName          → the explicitly picked in-archive entry (PathInArchive identity),
        //   forcePriority                 → the ROM "Clear" path (auto-pick, last-played ignored).
        var body = ctx?.Request?.Body;
        var emulatorId = TryGetString(body, "emulatorId");
        var additionalAppId = TryGetString(body, "additionalAppId");
        var archiveEntryFileName = TryGetString(body, "archiveEntryFileName");
        TryGetBool(body, "forcePriority", out var forcePriority);

        var emulator = FindEmulator(emulatorId);
        var app = FindAdditionalApp(game, additionalAppId);

        // Marshal onto the WinForms UI thread; run in the background so the HTTP ack returns immediately.
        // Arm the ROM pick in-process IMMEDIATELY before HostLaunch.Launch — RomExtractor.ResolveLaunch
        // consumes it once on the launch worker thread (single-shot; no cross-process registry — same
        // pattern as the GUI's LaunchButtons.OnPlay). HostLaunch owns emulator resolution, launch history
        // and extraction deferral — the native equivalent of the plugin's PlayGame.
        bool armPick = RomExtractor.Available && (!string.IsNullOrEmpty(archiveEntryFileName) || forcePriority);
        Task.Run(() =>
        {
            try
            {
                UiThread.Invoke(() =>
                {
                    if (armPick) RomLaunchPick.Arm(game, additionalAppId, archiveEntryFileName, forcePriority);
                    HostLaunch.Launch("web", game, app, emulator, null);
                });
            }
            catch (Exception ex) { Log($"play id={id} launch failed: {ex.Message}"); }
        });

        Log($"play id={id} emu={emulatorId ?? "default"} app={additionalAppId ?? "default"} entry={archiveEntryFileName ?? "<none>"} forcePriority={forcePriority} → HostLaunch.Launch(\"web\")");
        return Ok(new { ok = true });
    }

    // ── install (delegate the download to the store client via its URI) ─────────
    // Uninstalled GOG/Steam/Epic/Ubisoft/EA game → fire the client's install URI (goggalaxy:// /
    // steam://install / com.epicgames.launcher://…?action=install / …), the same one the desktop
    // Install button uses. The store client owns the download; LiteBox re-detects state on next refresh.

    private static HttpResponse Install(string id, RouteContext ctx)
    {
        var game = ResolveGame(id);
        if (game == null) return Fail("not in library");

        var kind = StoreSupport.KindOf(game);
        if (kind == StoreKind.None) return Fail("not a store game");

        // Parental: when the install is PIN-blocked for THIS client (active + locked + BlockInstallWhenLocked),
        // require and verify a ONE-SHOT PIN — same lockout/reason shape as /api/parental/unlock. A correct PIN
        // authorizes THIS install only (no global unlock; the client stays locked). Uses the per-client web
        // lock (cookie for a browser, shared desktop lock for the kiosk) so it matches parental.installNeedsUnlock
        // the frontend received from /api/parental/state. The frontend opens the PIN pad and POSTs {pin} here.
        if (WebParentalState.From(ctx?.Request).InstallNeedsUnlock)
        {
            if (ParentalFilter.PinLockedOut)
                return HttpResponse.Json(JsonSerializer.Serialize(new { ok = false, reason = "locked-out" }));
            var pin = TryGetString(ctx?.Request?.Body, "pin");
            if (string.IsNullOrEmpty(pin))
                return HttpResponse.Json(JsonSerializer.Serialize(new { ok = false, reason = "no-pin" }));
            if (!ParentalFilter.VerifyPin(pin!))
            {
                int remaining = ParentalFilter.RegisterFailedPinAttempt();
                return HttpResponse.Json(JsonSerializer.Serialize(new { ok = false, reason = remaining == 0 ? "locked-out" : "wrong-pin", attemptsRemaining = remaining }));
            }
        }

        string appPath = ""; try { appPath = game.ApplicationPath ?? ""; } catch { }
        string? gogAppId = null; try { gogAppId = (game as ILiteBoxGame)?.GetField("GogAppId"); } catch { }

        var uri = StoreSupport.InstallUri(kind, gogAppId, StoreSupport.SteamAppId(appPath),
                                          StoreSupport.EpicAppName(appPath), StoreSupport.UplayId(appPath), StoreSupport.EaId(appPath));
        if (string.IsNullOrEmpty(uri) || !StoreSupport.ShellOpen(uri))
        {
            Log($"install id={id} kind={kind}: no URI / shell-open failed (uri={uri ?? "<none>"})");
            return Fail("install failed — is the store client installed?");
        }
        // From the fullscreen TopMost kiosk, the store client (Galaxy/Steam/Epic…) would open hidden behind
        // it — let it surface so the user can drive the install. Kiosk requests only; a normal browser is fine.
        if (WebParentalState.IsKioskRequest(ctx?.Request))
            Web.Kiosk.WebKioskWindow.YieldForExternalLaunch();
        Log($"install id={id} kind={kind} → {uri}");
        return Ok(new { ok = true });
    }

    // ── launch-target resolution (emulator / additional app by id) ──────────────

    private static IEmulator FindEmulator(string emuId)
    {
        if (string.IsNullOrEmpty(emuId)) return null;
        try { return PluginHelper.DataManager.GetEmulatorById(emuId); } catch { return null; }
    }

    private static IAdditionalApplication FindAdditionalApp(IGame game, string appId)
    {
        if (game == null || string.IsNullOrEmpty(appId)) return null;
        try
        {
            foreach (var a in game.GetAllAdditionalApplications() ?? Array.Empty<IAdditionalApplication>())
                try { if (a != null && string.Equals(a.Id, appId, StringComparison.Ordinal)) return a; } catch { }
        }
        catch { }
        return null;
    }

    // ── rating (real write) ─────────────────────────────────────────────────────

    private static HttpResponse SetRating(string id, string body, RouteContext ctx)
    {
        var deny = ParentalWebWriteGuard.DenyReason(IsLocked(ctx), "rating");
        if (deny != null) return Fail(deny);
        if (!TryGetDouble(body, "value", out var value)) return Fail("missing value");
        value = Math.Clamp(value, 0, 5);

        var game = ResolveGame(id);
        if (game == null) return Fail("not in library");

        try { game.StarRatingFloat = (float)value; PluginHelper.DataManager.Save(false); }
        catch (Exception ex) { Log($"SetRating save failed: {ex.Message}"); return Fail("save failed"); }

        Log($"rating id={id} → {value:0.0}");
        return Ok(new { ok = true, value });
    }

    // ── favorite / hide / broken (real write; body { "value": true|false }) ─────

    private static HttpResponse SetFlag(string id, RouteContext ctx, Action<IGame, bool> setter, string label)
    {
        var deny = ParentalWebWriteGuard.DenyReason(IsLocked(ctx), label);
        if (deny != null) return Fail(deny);
        if (!TryGetBool(ctx.Request.Body, "value", out var value)) return Fail("missing value");

        var game = ResolveGame(id);
        if (game == null) return Fail("not in library");

        try { setter(game, value); PluginHelper.DataManager.Save(false); }
        catch (Exception ex) { Log($"Set{label} save failed: {ex.Message}"); return Fail("save failed"); }

        Log($"{label} id={id} → {(value ? "on" : "off")}");
        return Ok(new { ok = true, value });
    }

    // ── resethistory ────────────────────────────────────────────────────────────

    private static HttpResponse ResetHistory(string id)
    {
        var game = ResolveGame(id);
        if (game == null) return Fail("not in library");
        try { if (PluginHelper.DataManager is HostDataManagerXml hdm) hdm.ClearLastLaunch(game.Id); } catch { }
        Log($"resethistory id={id}");
        return Ok(new { ok = true });
    }

    // ── parental gating ─────────────────────────────────────────────────────────
    // IsLocked returns this client's per-request web lock state (WebParentalState cookie); the per-action
    // decision (allow-flags + BigBoxWriteMode Block/Merge) lives in ParentalWebWriteGuard.DenyReason.
    // Fail-safe: deny on evaluation error.
    private static bool IsLocked(RouteContext ctx)
    {
        try { var st = WebParentalState.From(ctx?.Request); return st != null && st.IsLocked; }
        catch { return true; }   // fail safe: deny on evaluation error
    }

    // ── game resolution (opaque id → IGame) ─────────────────────────────────────

    private static IGame ResolveGame(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        if (Guid.TryParse(id, out _))
        {
            try { var g = PluginHelper.DataManager.GetGameById(id); if (g != null) return g; }
            catch (Exception ex) { Log($"GetGameById({id}): {ex.Message}"); }
        }
        if (int.TryParse(id, out var dbId) && dbId > 0) return FindOwnedGame(dbId);
        return null;
    }

    private static IGame FindOwnedGame(int dbId)
    {
        IGame[] games;
        try { games = PluginHelper.DataManager.GetAllGames(); }
        catch (Exception ex) { Log($"GetAllGames: {ex.Message}"); return null; }
        if (games == null) return null;
        foreach (var g in games)
        {
            try { if (g?.LaunchBoxDbId is int lid && lid == dbId) return g; }
            catch { }
        }
        return null;
    }

    // ── body helpers ────────────────────────────────────────────────────────────

    private static string TryGetString(string body, string key)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty(key, out var el)) return null;
            var s = el.ValueKind == JsonValueKind.String ? el.GetString() : null;
            return string.IsNullOrEmpty(s) ? null : s;
        }
        catch { return null; }
    }

    private static bool TryGetDouble(string body, string key, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(body)) return false;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty(key, out var el)) return false;
            if (el.ValueKind == JsonValueKind.Number) { value = el.GetDouble(); return true; }
            if (el.ValueKind == JsonValueKind.String && double.TryParse(el.GetString(),
                    System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v))
            { value = v; return true; }
            return false;
        }
        catch { return false; }
    }

    private static bool TryGetBool(string body, string key, out bool value)
    {
        value = false;
        if (string.IsNullOrWhiteSpace(body)) return false;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty(key, out var el)) return false;
            if (el.ValueKind == JsonValueKind.True) { value = true; return true; }
            if (el.ValueKind == JsonValueKind.False) { value = false; return true; }
            if (el.ValueKind == JsonValueKind.Number) { value = el.GetDouble() != 0; return true; }
            if (el.ValueKind == JsonValueKind.String)
            {
                var s = el.GetString();
                if (bool.TryParse(s, out var bv)) { value = bv; return true; }
                if (s == "1") { value = true; return true; }
                if (s == "0") { value = false; return true; }
            }
            return false;
        }
        catch { return false; }
    }

    private static HttpResponse NotAvailable(string reason)
        => HttpResponse.Json(JsonSerializer.Serialize(new { ok = false, reason }), 501);

    private static HttpResponse Ok(object obj) => HttpResponse.Json(JsonSerializer.Serialize(obj));
    private static HttpResponse Fail(string reason) => HttpResponse.Json(JsonSerializer.Serialize(new { ok = false, reason }));
    private static void Log(string msg) => LbLog.Info("web", "[theme] " + msg);
}
