// The listener + connection loop, shared by every HTTP surface LiteBox exposes.
//
// Extracted from EmbeddedWebServer so a second surface (the RomM API, which must own its own port —
// the database site already occupies /api/platforms and friends) does not fork 120 lines of socket
// handling. One instance = one TcpListener + one route table + one concurrency gate.
//
// Binding follows the same opt-in rule everywhere: an empty allow-list binds 127.0.0.1 only; any wildcard
// IP pattern flips the bind to 0.0.0.0 and the accept loop admits a connection only when its remote IP
// matches (loopback is ALWAYS allowed). One task per accepted connection, a keep-alive loop per socket,
// and a per-request concurrency cap so bursts don't spin up unbounded work.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LbApiHost.Host.Diag;

namespace LbApiHost.Host.Web;

internal sealed class HttpHost
{
    private readonly string _tag;
    private readonly Router _router;
    private readonly SemaphoreSlim _concurrencyGate;

    private TcpListener _listener;
    private CancellationTokenSource _cts;
    private Task _acceptLoop;
    private int _currentPort;
    private List<Regex> _allowedPatterns = new();

    /// <param name="tag">Log tag, e.g. "web" or "romm".</param>
    /// <param name="router">The route table; rebuilt by the owner before each Start.</param>
    /// <param name="maxConcurrentRequests">Requests processed at once (acquired per request, not per
    /// connection, so idle keep-alive sockets hold no slot).</param>
    public HttpHost(string tag, Router router, int maxConcurrentRequests = 20)
    {
        _tag = tag;
        _router = router;
        _concurrencyGate = new SemaphoreSlim(maxConcurrentRequests);
    }

    /// <summary>Comma/semicolon/whitespace-separated wildcard IP patterns. Resolved on Start.</summary>
    public Func<string> AllowedIpsProvider { get; set; }

    /// <summary>Called with every accepted request before dispatch (the kiosk selection bridge uses this).</summary>
    public Action<HttpRequest> Observe { get; set; }

    /// <summary>Answers a request before the router sees it, or returns null to let it through. A CORS
    /// preflight is the case this exists for: it must succeed on every path, including ones the table
    /// deliberately refuses.</summary>
    public Func<HttpRequest, HttpResponse> Intercept { get; set; }

    /// <summary>Sampled before and after dispatch; the result is handed to <see cref="Decorate"/>.</summary>
    public Func<bool> DegradedProbe { get; set; }

    /// <summary>Last chance to stamp headers on a response (request, response, degraded-before-dispatch).</summary>
    public Action<HttpRequest, HttpResponse, bool> Decorate { get; set; }

    /// <summary>Methods the loop admits; anything else gets a 405 without reaching the router.</summary>
    public string[] AllowedMethods { get; set; } =
        { "GET", "HEAD", "POST", "PUT", "PATCH", "DELETE", "OPTIONS" };

    public bool IsRunning => _listener != null;
    public int CurrentPort => _currentPort;

    /// <summary>True when the bind reaches beyond loopback (i.e. the allow-list was non-empty).</summary>
    public bool IsLanExposed => _allowedPatterns.Count > 0;

    public void Start(int port)
    {
        if (_listener != null) return;
        try
        {
            _cts = new CancellationTokenSource();

            _allowedPatterns = ParseAllowedIpPatterns(AllowedIpsProvider?.Invoke());
            var bindAddr = _allowedPatterns.Count > 0 ? IPAddress.Any : IPAddress.Loopback;

            _listener = new TcpListener(bindAddr, port);
            _listener.Start();
            _currentPort = ((IPEndPoint)_listener.LocalEndpoint).Port;

            LbLog.Info(_tag, _allowedPatterns.Count > 0
                ? $"listening on http://0.0.0.0:{_currentPort}/ (LAN access: {_allowedPatterns.Count} pattern(s) + loopback)"
                : $"listening on http://127.0.0.1:{_currentPort}/ (loopback only)");

            _acceptLoop = Task.Run(() => AcceptLoop(_cts.Token));
        }
        catch (Exception ex)
        {
            LbLog.Warn(_tag, "start error: " + ex);
            Stop();
            throw;
        }
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        _listener = null;
        _cts = null;
        _acceptLoop = null;
        LbLog.Info(_tag, "stopped");
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener != null)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                LbLog.Warn(_tag, "accept error: " + ex.Message);
                continue;
            }

            // Reject connections outside the allow-list before doing any work (loopback is always allowed;
            // an empty list ⇒ loopback bind, so every connection here is already loopback).
            if (!IsRemoteAllowed(client))
            {
                try { client.Close(); } catch { }
                continue;
            }

            _ = Task.Run(() => HandleClient(client));
        }
    }

    private void HandleClient(TcpClient client)
    {
        try
        {
            using (client)
            using (var stream = client.GetStream())
            {
                stream.ReadTimeout = 10000;   // idle keep-alive ceiling
                stream.WriteTimeout = 30000;  // guard against a wedged client mid-write

                while (true)
                {
                    HttpRequest req;
                    try { req = HttpRequest.TryRead(stream); }
                    catch (IOException) { return; } // peer disconnect / read timeout
                    if (req == null)
                    {
                        // On a kept-alive socket "null" is usually EOF (browser closed) — exit quietly.
                        try { HttpResponse.BadRequest("Malformed request").Write(stream); } catch { }
                        return;
                    }

                    try
                    {
                        // An over-sized body is refused. When it was drained the socket is still in sync
                        // and the connection can go on; when it was too big to drain, the next bytes on
                        // the wire are body, so answer and close.
                        if (req.BodyTooLarge)
                        {
                            var tooBig = HttpResponse.PlainText("Payload too large", 413);
                            if (!req.BodyDrained) tooBig.Headers["Connection"] = "close";
                            try { tooBig.Write(stream); } catch (IOException) { return; }
                            if (!req.BodyDrained) return;
                            continue;
                        }

                        // HTTP/1.1 default is keep-alive unless the client sent "Connection: close".
                        var connHdr = req.GetHeader("Connection") ?? "";
                        bool keepAlive = !connHdr.Equals("close", StringComparison.OrdinalIgnoreCase);

                        var _t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                        var resp = Dispatch(req, out bool degradedBefore);
                        req.ElapsedMs = (int)((System.Diagnostics.Stopwatch.GetTimestamp() - _t0) * 1000.0
                                              / System.Diagnostics.Stopwatch.Frequency);

                        Decorate?.Invoke(req, resp, degradedBefore);
                        resp.Headers["Connection"] = keepAlive ? "keep-alive" : "close";
                        if (keepAlive)
                            resp.Headers["Keep-Alive"] = "timeout=10";

                        // Forward the client's gzip preference (HEAD has no body — skip).
                        if (req.Method != "HEAD")
                        {
                            var ae = req.GetHeader("Accept-Encoding");
                            resp.AcceptsGzip = ae != null
                                && ae.IndexOf("gzip", StringComparison.OrdinalIgnoreCase) >= 0;
                        }

                        try
                        {
                            // HEAD keeps the real Content-Length and drops only the bytes.
                            if (req.Method == "HEAD") resp.SuppressBody = true;
                            resp.Write(stream);
                        }
                        catch (IOException) { return; /* client disconnected */ }

                        if (!keepAlive) return;
                    }
                    finally { req.DisposeBody(); }
                }
            }
        }
        catch (Exception ex)
        {
            LbLog.Warn(_tag, "client error: " + ex.Message);
        }
    }

    private HttpResponse Dispatch(HttpRequest req, out bool degradedBefore)
    {
        // Per-request concurrency gate (not per connection) so idle keep-alive sockets hold no slot.
        _concurrencyGate.Wait();
        // Sampled BEFORE the handler runs as well as after: a payload is built DURING dispatch, and a
        // request that spans the moment a game exits would otherwise be assembled from the thin data and
        // then labelled healthy — cached for the session, which is the one thing this exists to prevent.
        degradedBefore = DegradedProbe?.Invoke() ?? false;
        try
        {
            if (Array.IndexOf(AllowedMethods, req.Method) < 0)
                return HttpResponse.PlainText("Method not allowed", 405);

            var intercepted = Intercept?.Invoke(req);
            if (intercepted != null) return intercepted;

            Observe?.Invoke(req);
            return _router.Dispatch(req) ?? HttpResponse.NotFound($"No route for {req.Path}");
        }
        catch (Exception ex)
        {
            return HttpResponse.ServerError($"Dispatch error: {ex.Message}");
        }
        finally { _concurrencyGate.Release(); }
    }

    // ── IP allow-list ─────────────────────────────────────────────────────────

    /// <summary>Parses the config string (comma/semicolon/whitespace-separated wildcard patterns, <c>*</c> =
    /// any run) into anchored regexes. Empty / null → empty list (⇒ loopback-only bind).</summary>
    private List<Regex> ParseAllowedIpPatterns(string raw)
    {
        var list = new List<Regex>();
        if (string.IsNullOrWhiteSpace(raw)) return list;
        foreach (var tok in raw.Split(new[] { ',', ';', ' ', '\t', '\r', '\n' },
                                      StringSplitOptions.RemoveEmptyEntries))
        {
            var p = tok.Trim();
            if (p.Length == 0) continue;
            try
            {
                var rx = "^" + Regex.Escape(p).Replace("\\*", ".*") + "$";
                list.Add(new Regex(rx, RegexOptions.Compiled | RegexOptions.IgnoreCase));
            }
            catch (Exception ex) { LbLog.Warn(_tag, $"bad IP pattern '{p}': {ex.Message}"); }
        }
        return list;
    }

    /// <summary>True if the client's remote IP is loopback (always) or matches an allow-list pattern.
    /// IPv4-mapped IPv6 remotes are normalised to IPv4 first.</summary>
    private bool IsRemoteAllowed(TcpClient client)
    {
        IPAddress addr;
        try { addr = (client.Client.RemoteEndPoint as IPEndPoint)?.Address; }
        catch { return false; }
        if (addr == null) return false;
        if (addr.IsIPv4MappedToIPv6) addr = addr.MapToIPv4();
        if (IPAddress.IsLoopback(addr)) return true;          // 127.0.0.1 / ::1 always
        var s = addr.ToString();
        foreach (var rx in _allowedPatterns) if (rx.IsMatch(s)) return true;
        return false;
    }
}
