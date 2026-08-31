// A line per request, to its own file — what a client actually asked for and what it got.
//
// Written because a plain "it downloaded the whole 7z instead of one ROM" cost a session of guesswork:
// LbLog goes to the console, which only exists under --debug, so a normally-launched LiteBox left no
// trace of the exchange at all. The interesting part of a RomM request is never the status code, it is
// WHICH branch answered — the archive as-is, one extracted entry, a multi-disc zip — and whether the
// caller was a paired token or the account password, since that decides whether a ROM binding could be
// recorded. So a handler can attach a NOTE to the current request and it lands on the same line.
//
// Off by default ([RommServer] LogRequests). It is a debugging instrument, not telemetry: it records the
// paths a client asks for, which is a log of what somebody plays.

#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using LbApiHost.Host.Diag;
using LbApiHost.Host.Web;

namespace LbApiHost.Host.Romm;

internal static class RommTrace
{
    private const long MaxBytes = 4L * 1024 * 1024;
    private static readonly object _lock = new();

    /// <summary>Notes the handlers attach for the request being served, keyed by thread — Decorate runs
    /// on the same thread that dispatched, so this needs no plumbing through every signature.</summary>
    [ThreadStatic] private static List<string>? _notes;

    public static bool Enabled => RommConfig.LogRequests || RommConfig.LogBodies;

    /// <summary>The request as it arrived, snapped BEFORE dispatch — a handler consumes the body (a
    /// multipart upload is parsed and its temp file deleted), so asking afterwards would find nothing.
    /// One per thread, like the notes.</summary>
    [ThreadStatic] private static string? _incoming;

    /// <summary>Called from the host's Intercept hook, before anything reads the request.</summary>
    public static void Capture(HttpRequest req)
    {
        _incoming = null;
        if (!RommConfig.LogBodies) return;
        try
        {
            var sb = new StringBuilder();
            sb.Append("      > ").Append(req.Method).Append(' ').Append(req.Path);
            var q = req.RawQuery ?? "";
            if (q.Length > 0) sb.Append('?').Append(q);
            sb.Append('\n');
            AppendHeaders(sb, req.Headers);
            AppendBody(sb, DescribeRequestBody(req));
            _incoming = sb.ToString();
        }
        catch { _incoming = "      > (could not be captured)\n"; }
    }

    private static void AppendHeaders(StringBuilder sb, IReadOnlyDictionary<string, string> headers)
    {
        foreach (var kv in headers)
        {
            // A bearer token in a file on disk is a credential, not a diagnostic.
            bool secret = kv.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                       || kv.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase);
            sb.Append("          ").Append(kv.Key).Append(": ")
              .Append(secret ? Redact(kv.Value) : Cut(kv.Value, 200)).Append('\n');
        }
    }

    private static string Redact(string v)
    {
        var sp = v.IndexOf(' ');
        var scheme = sp > 0 ? v.Substring(0, sp) : "";
        return (scheme.Length > 0 ? scheme + " " : "") + "\u2026 (redacted)";
    }

    private static void AppendBody(StringBuilder sb, string body)
    {
        if (body.Length == 0) return;
        foreach (var line in body.Split('\n'))
            sb.Append("          ").Append(line).Append('\n');
    }

    private const int MaxBodyChars = 4000;

    private static string DescribeRequestBody(HttpRequest req)
    {
        try
        {
            long len = req.BodyLength;
            if (len <= 0) return "";
            var ct = req.ContentType ?? "";

            if (ct.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase))
                return "body: multipart, " + Human(len) + "\n" + MultipartParts(req);

            if (IsTexty(ct))
            {
                var text = req.Body ?? "";
                if (text.Length == 0 && req.BodyBytes is { Length: > 0 })
                    text = Encoding.UTF8.GetString(req.BodyBytes);
                return "body: " + Cut(text, MaxBodyChars);
            }
            return "body: " + Human(len) + " of " + (ct.Length > 0 ? ct : "unknown type") + " (not dumped)";
        }
        catch { return "body: (unreadable)"; }
    }

    /// <summary>The parts of a multipart upload, by their headers only. Re-parsing the body would race
    /// the handler that is about to read it, and dumping a save's bytes helps nobody.</summary>
    private static string MultipartParts(HttpRequest req)
    {
        try
        {
            byte[] head;
            if (req.BodyBytes is { Length: > 0 } bb)
                head = bb.Length <= 8192 ? bb : bb[..8192];
            else if (!string.IsNullOrEmpty(req.BodyFilePath) && File.Exists(req.BodyFilePath))
            {
                using var fs = File.OpenRead(req.BodyFilePath);
                head = new byte[Math.Min(8192, fs.Length)];
                _ = fs.Read(head, 0, head.Length);
            }
            else return "";

            var sb = new StringBuilder();
            foreach (var line in Encoding.UTF8.GetString(head).Split('\n'))
                if (line.StartsWith("Content-Disposition:", StringComparison.OrdinalIgnoreCase))
                    sb.Append("  part: ").Append(Cut(line.Substring(20).Trim(), 200)).Append('\n');
            return sb.ToString().TrimEnd('\n');
        }
        catch { return ""; }
    }

    private static string DescribeResponseBody(HttpResponse resp)
    {
        try
        {
            var ct = resp.Headers.TryGetValue("Content-Type", out var t) ? t : "";
            // NEVER read BodyStream: it is what is about to be written to the socket, and reading it
            // here would consume the download.
            if (resp.BodyStream != null)
                return "body: " + Human(resp.BodyStreamLength) + " streamed"
                     + (ct.Length > 0 ? " (" + ct + ")" : "") + " (not dumped)";
            if (resp.ChunkedWriter != null) return "body: chunked (not dumped)";
            if (resp.Body.Length == 0) return "";
            if (IsTexty(ct)) return "body: " + Cut(Encoding.UTF8.GetString(resp.Body), MaxBodyChars);
            return "body: " + Human(resp.Body.Length) + " of "
                 + (ct.Length > 0 ? ct : "unknown type") + " (not dumped)";
        }
        catch { return "body: (unreadable)"; }
    }

    private static bool IsTexty(string contentType)
        => contentType.Contains("json", StringComparison.OrdinalIgnoreCase)
        || contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
        || contentType.Contains("xml", StringComparison.OrdinalIgnoreCase)
        || contentType.Contains("x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase);

    private static string Cut(string s, int max)
        => s.Length <= max ? s : s.Substring(0, max) + "\u2026 (+" + (s.Length - max) + " more)";

    private static string Path0 => LiteBoxPaths.File("romm-requests.log");

    /// <summary>Adds a detail to the line about to be written. Costs nothing when tracing is off.</summary>
    public static void Note(string note)
    {
        if (!Enabled || string.IsNullOrEmpty(note)) return;
        (_notes ??= new List<string>()).Add(note);
    }

    /// <summary>Called from the host's Decorate hook, once per request, after dispatch.</summary>
    public static void Request(HttpRequest req, HttpResponse resp)
    {
        var notes = _notes;
        _notes = null;                                  // never leak into the next request on this thread
        if (!Enabled) return;

        try
        {
            var sb = new StringBuilder();
            sb.Append(DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture));
            sb.Append("  ").Append(req.Method).Append(' ').Append(req.Path);

            var q = SafeQuery(req);
            if (q.Length > 0) sb.Append('?').Append(q);

            sb.Append("  -> ").Append(resp.StatusCode);
            if (req.ElapsedMs > 0) sb.Append(' ').Append(req.ElapsedMs).Append("ms");

            long len = resp.BodyStreamLength;
            if (len > 0) sb.Append(' ').Append(Human(len));

            sb.Append("  [").Append(Who(req)).Append(']');

            if (notes is { Count: > 0 })
                foreach (var n in notes) sb.Append("  | ").Append(n);

            if (RommConfig.LogBodies)
            {
                sb.Append('\n');
                sb.Append(_incoming ?? "      > (not captured)\n");
                sb.Append("      < ").Append(resp.StatusCode).Append(' ').Append(resp.StatusText).Append('\n');
                AppendHeaders(sb, resp.Headers);
                AppendBody(sb, DescribeResponseBody(resp));
            }
            _incoming = null;

            Append(sb.ToString());
        }
        catch { /* a broken log line must never break a download */ }
    }

    /// <summary>Who is calling, in the terms that matter here: the auth method, and the token id when
    /// there is one — a request with no token id cannot carry a ROM binding, and that is usually the
    /// answer to "why did nothing get recorded".</summary>
    private static string Who(HttpRequest req)
    {
        try
        {
            var id = RommAuth.Authenticate(req);
            if (id == null) return "unauthenticated";
            return id.TokenId is int t ? $"{id.Method} #{t}" : id.Method + " (no token id)";
        }
        catch { return "?"; }
    }

    private static string SafeQuery(HttpRequest req)
    {
        // The query can carry a device id and a pairing code; neither belongs in a file on disk.
        try
        {
            var raw = req.RawQuery ?? "";
            return raw.Length <= 300 ? raw : raw.Substring(0, 300) + "…";
        }
        catch { return ""; }
    }

    private static string Human(long n)
        => n >= 1024 * 1024 ? (n / 1024d / 1024d).ToString("0.#", CultureInfo.InvariantCulture) + " MB"
         : n >= 1024 ? (n / 1024d).ToString("0.#", CultureInfo.InvariantCulture) + " KB"
         : n + " B";

    private static void Append(string line)
    {
        lock (_lock)
        {
            try
            {
                var path = Path0;
                // One rollover, so a forgotten switch cannot fill the disk.
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > MaxBytes)
                {
                    var old = path + ".1";
                    try { if (File.Exists(old)) File.Delete(old); } catch { }
                    try { File.Move(path, old); } catch { }
                }
                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception ex) { LbLog.Once("romm", "request log unavailable: " + ex.Message); }
        }
    }
}
