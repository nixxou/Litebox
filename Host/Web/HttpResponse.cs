// Minimal HTTP/1.1 response writer for the embedded web server.
//
// Buffers status + headers + a byte body and serialises them on Write. Static helpers (Html/Json/Bytes/
// NotFound/Redirect/…) build the common shapes. Optional gzip for JSON is gated on the [Web] GzipJson flag
// (WebConfig) plus the client's Accept-Encoding. Connection handling (keep-alive vs close) is decided by the
// server's request loop, which sets the Connection header before Write.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using LbApiHost.Host.Diag;

namespace LbApiHost.Host.Web;

/// <summary>A single HTTP/1.1 response. Build with the static helpers, then <see cref="Write(Stream)"/>.</summary>
internal sealed class HttpResponse
{
    public int StatusCode { get; set; } = 200;
    public string StatusText { get; set; } = "OK";
    public Dictionary<string, string> Headers { get; } =
        new(StringComparer.OrdinalIgnoreCase);
    public byte[] Body { get; set; } = Array.Empty<byte>();

    /// <summary>Set by the server loop when the request carried <c>Accept-Encoding: gzip</c>. Write uses it,
    /// with <see cref="WebConfig.GzipJson"/> and the Content-Type, to decide whether to compress.</summary>
    public bool AcceptsGzip { get; set; }

    // ── Helpers ───────────────────────────────────────────────────────────────

    public static HttpResponse Html(string body, int status = 200)
    {
        var bytes = Encoding.UTF8.GetBytes(body ?? "");
        var r = new HttpResponse { StatusCode = status, StatusText = TextFor(status), Body = bytes };
        r.Headers["Content-Type"] = "text/html; charset=utf-8";
        return r;
    }

    public static HttpResponse Json(string body, int status = 200)
    {
        var bytes = Encoding.UTF8.GetBytes(body ?? "");
        var r = new HttpResponse { StatusCode = status, StatusText = TextFor(status), Body = bytes };
        r.Headers["Content-Type"] = "application/json; charset=utf-8";
        return r;
    }

    public static HttpResponse PlainText(string body, int status = 200)
    {
        var bytes = Encoding.UTF8.GetBytes(body ?? "");
        var r = new HttpResponse { StatusCode = status, StatusText = TextFor(status), Body = bytes };
        r.Headers["Content-Type"] = "text/plain; charset=utf-8";
        return r;
    }

    public static HttpResponse Bytes(byte[] body, string contentType, int status = 200)
    {
        var r = new HttpResponse { StatusCode = status, StatusText = TextFor(status), Body = body ?? Array.Empty<byte>() };
        r.Headers["Content-Type"] = contentType ?? "application/octet-stream";
        return r;
    }

    public static HttpResponse NotFound(string msg = "Not found") => PlainText(msg, 404);
    public static HttpResponse BadRequest(string msg = "Bad request") => PlainText(msg, 400);
    public static HttpResponse ServerError(string msg = "Internal server error") => PlainText(msg, 500);

    public static HttpResponse Redirect(string location, int status = 302)
    {
        var r = new HttpResponse { StatusCode = status, StatusText = TextFor(status), Body = Array.Empty<byte>() };
        r.Headers["Location"] = location;
        return r;
    }

    /// <summary>Adds a <c>Set-Cookie</c> header (one cookie per call).</summary>
    public HttpResponse SetCookie(string name, string value, int maxAgeSeconds, bool httpOnly = false, string path = "/")
    {
        var sb = new StringBuilder();
        sb.Append(name).Append('=').Append(value ?? "");
        sb.Append("; Path=").Append(path);
        if (maxAgeSeconds >= 0) sb.Append("; Max-Age=").Append(maxAgeSeconds);
        sb.Append("; SameSite=Lax");
        if (httpOnly) sb.Append("; HttpOnly");
        Headers["Set-Cookie"] = sb.ToString();
        return this;
    }

    /// <summary>Sets a cookie's Max-Age to 0 so the browser drops it.</summary>
    public HttpResponse ClearCookie(string name, string path = "/")
        => SetCookie(name, "", maxAgeSeconds: 0, httpOnly: false, path);

    // ── Wire format ─────────────────────────────────────────────────────────

    // Below this body size gzip's ~20 bytes of overhead is not worth the CPU.
    private const int GzipMinBytes = 1024;

    public void Write(Stream stream)
    {
        // Leave the Connection header the caller set (the request loop chooses keep-alive vs close); default
        // to "close" only when nobody set it.
        if (!Headers.ContainsKey("Connection"))
            Headers["Connection"] = "close";

        // ── Optional gzip (JSON only) ── all conditions must hold:
        //   1. [Web] GzipJson is on.  2. Client sent Accept-Encoding: gzip.  3. Not already encoded.
        //   4. Content-Type is application/json.  5. Body ≥ GzipMinBytes.
        if (AcceptsGzip
            && WebConfig.GzipJson
            && !Headers.ContainsKey("Content-Encoding")
            && Body.Length >= GzipMinBytes
            && Headers.TryGetValue("Content-Type", out var ct)
            && ct.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
        {
            int originalSize = Body.Length;
            using (var ms = new MemoryStream())
            {
                using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
                    gz.Write(Body, 0, Body.Length);
                Body = ms.ToArray();
            }
            Headers["Content-Encoding"] = "gzip";
            LbLog.Info("web", $"gzip: {originalSize} -> {Body.Length} bytes");
        }

        Headers["Content-Length"] = Body.Length.ToString();
        if (!Headers.ContainsKey("Cache-Control"))
            Headers["Cache-Control"] = "no-store";

        var head = new StringBuilder();
        head.Append("HTTP/1.1 ").Append(StatusCode).Append(' ').Append(StatusText).Append("\r\n");
        foreach (var kv in Headers)
            head.Append(kv.Key).Append(": ").Append(kv.Value).Append("\r\n");
        head.Append("\r\n");

        var headBytes = Encoding.ASCII.GetBytes(head.ToString());
        stream.Write(headBytes, 0, headBytes.Length);
        if (Body.Length > 0) stream.Write(Body, 0, Body.Length);
        stream.Flush();
    }

    private static string TextFor(int code) => code switch
    {
        200 => "OK",
        301 => "Moved Permanently",
        302 => "Found",
        304 => "Not Modified",
        400 => "Bad Request",
        403 => "Forbidden",
        404 => "Not Found",
        500 => "Internal Server Error",
        503 => "Service Unavailable",
        _ => "OK",
    };
}
