// Minimal HTTP/1.1 request parser for the embedded web server.
//
// Reads the request line + headers (and a size-capped body) off a NetworkStream and exposes them as parsed
// fields. Hand-rolled on purpose: HttpListener needs a URL ACL for non-admin binds and ASP.NET Core is
// heavyweight — TcpListener + a small parser covers the GET/HEAD/POST subset the server needs. Headers are
// case-insensitive; query and cookies parse lazily; a header-size guard bounds abuse.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace LbApiHost.Host.Web;

/// <summary>Parsed HTTP/1.1 request — method, path, query, headers, cookies, body.</summary>
internal sealed class HttpRequest
{
    public string Method { get; init; } = "GET";
    public string RawTarget { get; init; } = "/";
    public string Path { get; init; } = "/";
    public string RawQuery { get; init; } = "";
    public Dictionary<string, string> Headers { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Request body decoded as UTF-8. Empty when there is none (GET, or a zero-length body).</summary>
    public string Body { get; init; } = "";

    private Dictionary<string, string> _query;
    public Dictionary<string, string> Query => _query ??= ParseQuery(RawQuery);

    private Dictionary<string, string> _cookies;
    public Dictionary<string, string> Cookies => _cookies ??= ParseCookies(GetHeader("Cookie"));

    public string GetHeader(string name) =>
        Headers.TryGetValue(name, out var v) ? v : null;

    public string GetQuery(string name) =>
        Query.TryGetValue(name, out var v) ? v : null;

    public int GetQueryInt(string name, int fallback = 0) =>
        int.TryParse(GetQuery(name), out var v) ? v : fallback;

    public bool GetQueryBool(string name, bool fallback = false)
    {
        var v = GetQuery(name);
        if (string.IsNullOrEmpty(v)) return fallback;
        return v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Cookie value for <paramref name="name"/> (case-sensitive per RFC 6265), or null.</summary>
    public string GetCookie(string name) =>
        Cookies.TryGetValue(name, out var v) ? v : null;

    // ── Parser ──────────────────────────────────────────────────────────────

    private const int MaxHeaderBytes = 8192;

    /// <summary>Reads one request off <paramref name="stream"/>. Returns null on a malformed / oversized
    /// request line + header block (the caller treats null as EOF on a kept-alive socket).</summary>
    public static HttpRequest TryRead(Stream stream)
    {
        var buf = new byte[MaxHeaderBytes];
        int total = 0;
        int headerEnd = -1;

        while (total < buf.Length)
        {
            int read = stream.Read(buf, total, buf.Length - total);
            if (read <= 0) break;
            total += read;

            // Look for the \r\n\r\n that ends the header block.
            for (int i = Math.Max(0, total - read - 3); i < total - 3; i++)
            {
                if (buf[i] == '\r' && buf[i + 1] == '\n'
                    && buf[i + 2] == '\r' && buf[i + 3] == '\n')
                {
                    headerEnd = i;
                    break;
                }
            }
            if (headerEnd >= 0) break;
        }

        if (headerEnd < 0) return null; // malformed / too big

        var rawText = Encoding.ASCII.GetString(buf, 0, headerEnd);
        var lines = rawText.Split("\r\n");
        if (lines.Length == 0) return null;

        // ── Request line: "METHOD PATH HTTP/1.1" ──
        var firstParts = lines[0].Split(' ');
        if (firstParts.Length < 3) return null;

        var method = firstParts[0];
        var rawTarget = firstParts[1];

        string path;
        string rawQuery;
        int qIdx = rawTarget.IndexOf('?');
        if (qIdx >= 0)
        {
            path = rawTarget.Substring(0, qIdx);
            rawQuery = rawTarget.Substring(qIdx + 1);
        }
        else
        {
            path = rawTarget;
            rawQuery = "";
        }
        path = Uri.UnescapeDataString(path);

        // ── Headers ──
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrEmpty(line)) continue;
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var k = line.Substring(0, colon).Trim();
            var v = line.Substring(colon + 1).Trim();
            headers[k] = v;
        }

        // ── Body (POST/PUT) ──
        // Read up to Content-Length more bytes; any bytes already past the header terminator in our buffer
        // count as body (we may have over-read looking for \r\n\r\n). Hard-capped so a client can't make us
        // allocate without bound.
        string body = "";
        int contentLength = 0;
        if (headers.TryGetValue("Content-Length", out var cl) &&
            int.TryParse(cl, out contentLength) && contentLength > 0)
        {
            const int MaxBodyBytes = 64 * 1024;
            if (contentLength > MaxBodyBytes) contentLength = MaxBodyBytes;

            int bodyStart = headerEnd + 4; // skip "\r\n\r\n"
            int alreadyHave = Math.Max(0, total - bodyStart);

            var bodyBuf = new byte[contentLength];
            if (alreadyHave > 0)
                Array.Copy(buf, bodyStart, bodyBuf, 0, Math.Min(alreadyHave, contentLength));
            int copied = Math.Min(alreadyHave, contentLength);

            while (copied < contentLength)
            {
                int r = stream.Read(bodyBuf, copied, contentLength - copied);
                if (r <= 0) break;
                copied += r;
            }

            try { body = Encoding.UTF8.GetString(bodyBuf, 0, copied); }
            catch { body = ""; }
        }

        return new HttpRequest
        {
            Method = method,
            RawTarget = rawTarget,
            Path = path,
            RawQuery = rawQuery,
            Headers = headers,
            Body = body,
        };
    }

    private static Dictionary<string, string> ParseCookies(string raw)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(raw)) return dict;
        foreach (var pair in raw.Split(';'))
        {
            var trimmed = pair.Trim();
            if (trimmed.Length == 0) continue;
            int eq = trimmed.IndexOf('=');
            string k, v;
            if (eq < 0) { k = trimmed; v = ""; }
            else { k = trimmed.Substring(0, eq); v = trimmed.Substring(eq + 1); }
            try { v = Uri.UnescapeDataString(v); }
            catch { /* keep raw on decode failure */ }
            dict[k] = v;
        }
        return dict;
    }

    private static Dictionary<string, string> ParseQuery(string raw)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(raw)) return dict;

        foreach (var pair in raw.Split('&'))
        {
            if (string.IsNullOrEmpty(pair)) continue;
            int eq = pair.IndexOf('=');
            string k, v;
            if (eq < 0) { k = pair; v = ""; }
            else { k = pair.Substring(0, eq); v = pair.Substring(eq + 1); }
            try
            {
                k = Uri.UnescapeDataString(k.Replace('+', ' '));
                v = Uri.UnescapeDataString(v.Replace('+', ' '));
            }
            catch { /* keep raw on decode failure */ }
            dict[k] = v;
        }
        return dict;
    }
}
