// Minimal HTTP/1.1 request parser for the embedded web server.
//
// Reads the request line + headers and the body off a NetworkStream and exposes them as parsed fields.
// Hand-rolled on purpose: HttpListener needs a URL ACL for non-admin binds and ASP.NET Core is heavyweight —
// TcpListener + a small parser covers the subset the server needs. Headers are case-insensitive; query and
// cookies parse lazily; a header-size guard bounds abuse.
//
// Bodies: small ones stay in memory and keep the historical string <see cref="Body"/> (every existing JSON
// handler reads it). Anything past MaxInMemoryBody SPILLS TO A TEMP FILE instead — a save upload is 32 KB to
// 8 MB and a savestate can be 20 MB, so the old 64 KB in-memory cap made asset uploads impossible. The spill
// file belongs to the request: the server loop calls DisposeBody() once the response is written. Chunked
// request bodies are de-framed here too, since a client that streams an upload has no Content-Length to give.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using LbApiHost.Host.Diag;

namespace LbApiHost.Host.Web;

/// <summary>Parsed HTTP/1.1 request — method, path, query, headers, cookies, body.</summary>
internal sealed class HttpRequest
{
    public string Method { get; init; } = "GET";
    public string RawTarget { get; init; } = "/";
    public string Path { get; init; } = "/";
    public string RawQuery { get; init; } = "";

    /// <summary>How long the router took to answer, filled by the host after dispatch. Diagnostics only
    /// (the RomM request log prints it) — nothing behavioural reads it.</summary>
    public int ElapsedMs { get; set; }
    public Dictionary<string, string> Headers { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Request body decoded as UTF-8, for in-memory bodies only. Empty when there is none, and
    /// empty for a spilled body (see <see cref="BodyFilePath"/>) — a 20 MB upload is never JSON.</summary>
    public string Body { get; init; } = "";

    /// <summary>Raw body bytes when the body stayed in memory, else null.</summary>
    public byte[] BodyBytes { get; init; }

    /// <summary>Temp file holding the body when it exceeded <see cref="MaxInMemoryBody"/>, else null.
    /// Deleted by <see cref="DisposeBody"/>.</summary>
    public string BodyFilePath { get; init; }

    /// <summary>Body length in bytes, wherever it lives.</summary>
    public long BodyLength { get; init; }

    /// <summary>True when the client sent a body that hit the hard size ceiling and was refused.</summary>
    public bool BodyTooLarge { get; init; }

    /// <summary>Set with <see cref="BodyTooLarge"/>: the refused body was read off the socket and thrown
    /// away, so the connection is still in sync and the 413 can be delivered. False means the body was too
    /// big to even drain and the caller must close.</summary>
    public bool BodyDrained { get; init; }

    /// <summary>Content-Type header without its parameters, lowercased (e.g. "multipart/form-data").</summary>
    public string ContentType
    {
        get
        {
            var raw = GetHeader("Content-Type") ?? "";
            int semi = raw.IndexOf(';');
            return (semi >= 0 ? raw.Substring(0, semi) : raw).Trim().ToLowerInvariant();
        }
    }

    /// <summary>Opens the body for reading, wherever it lives. Returns null when there is no body. The
    /// caller disposes the stream; the spill file itself outlives it until DisposeBody().</summary>
    public Stream OpenBody()
    {
        if (BodyFilePath != null)
        {
            try { return new FileStream(BodyFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024); }
            catch { return null; }
        }
        if (BodyBytes != null && BodyBytes.Length > 0) return new MemoryStream(BodyBytes, writable: false);
        return null;
    }

    /// <summary>Drops the spill file, if any. Called by the server loop after the response is written.</summary>
    public void DisposeBody()
    {
        if (BodyFilePath == null) return;
        try { File.Delete(BodyFilePath); } catch { }
    }

    private Dictionary<string, string> _query;
    public Dictionary<string, string> Query => _query ??= ParseQuery(RawQuery);

    private Dictionary<string, string> _cookies;
    public Dictionary<string, string> Cookies => _cookies ??= ParseCookies(GetHeader("Cookie"));

    private Dictionary<string, string> _form;
    /// <summary>An <c>application/x-www-form-urlencoded</c> body, parsed like a query string. Empty for any
    /// other content type — OAuth2's token endpoint is the reason this exists.</summary>
    public Dictionary<string, string> Form => _form ??=
        ContentType == "application/x-www-form-urlencoded"
            ? ParseQuery(Body)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

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

    // Raised from the original 8 KB: a Bearer JWT plus cookies plus a long User-Agent overruns it, and a
    // client whose auth header is silently truncated fails in a way that looks like a wrong password.
    private const int MaxHeaderBytes = 16384;

    /// <summary>Bodies at or below this stay in memory and populate <see cref="Body"/>.</summary>
    public const int MaxInMemoryBody = 1024 * 1024;

    /// <summary>Hard ceiling on a request body. A client that announces more is refused (413) rather than
    /// read; settable so a surface that accepts uploads can raise or lower its own limit.</summary>
    public static long MaxBodyBytes = 512L * 1024 * 1024;

    /// <summary>How much of a refused body we are willing to read and throw away so the 413 can actually
    /// reach the client. Beyond this the socket is closed instead.</summary>
    private const long DrainLimitBytes = 32L * 1024 * 1024;

    // Reads and discards `count` bytes. False when the peer stopped early — the connection is then junk.
    private static bool Drain(Stream stream, long count)
    {
        if (count <= 0) return true;
        var sink = new byte[64 * 1024];
        try
        {
            while (count > 0)
            {
                int r = stream.Read(sink, 0, (int)Math.Min(sink.Length, count));
                if (r <= 0) return false;
                count -= r;
            }
            return true;
        }
        catch { return false; }
    }

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
        // Collapse repeated slashes BEFORE decoding, exactly as nginx's merge_slashes (on by default)
        // does. Every RomM deployment sits behind that proxy, so clients join base + path without
        // caring that both carry a slash — Argosy asks for "//api/media/…" and a real RomM never sees
        // it. We ARE the origin server here, so the normalisation has to happen on this side or those
        // requests miss every anchored route and 404. Merging before unescaping keeps an encoded %2F
        // inside a segment intact, which is the only case where the two orders differ.
        path = CollapseSlashes(path);
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

        // ── Body ──
        // Bytes already past the header terminator in our buffer are body (we over-read looking for the
        // \r\n\r\n), so they seed the read.
        int bodyStart = headerEnd + 4;
        int seeded = Math.Max(0, total - bodyStart);

        var te = headers.TryGetValue("Transfer-Encoding", out var teRaw) ? teRaw : "";
        bool chunked = te.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0;

        long contentLength = 0;
        if (!chunked && headers.TryGetValue("Content-Length", out var cl))
            long.TryParse(cl, NumberStyles.Integer, CultureInfo.InvariantCulture, out contentLength);

        var req = new HttpRequest
        {
            Method = method,
            RawTarget = rawTarget,
            Path = path,
            RawQuery = rawQuery,
            Headers = headers,
        };

        if (!chunked && contentLength <= 0)
            return req;

        // Announced-too-big: refuse before reading. The body still has to leave the socket or the next
        // request would be parsed out of its middle — so drain it, up to a bound. Past that bound the
        // connection is not worth rescuing and the caller closes it.
        if (!chunked && contentLength > MaxBodyBytes)
        {
            bool drained = contentLength <= DrainLimitBytes
                        && Drain(stream, contentLength - seeded);
            return new HttpRequest
            {
                Method = method, RawTarget = rawTarget, Path = path, RawQuery = rawQuery,
                Headers = headers, BodyTooLarge = true, BodyDrained = drained,
            };
        }

        // A client that asked permission before sending gets it here — and would have got the 413 above
        // without wasting a byte.
        var expect = headers.TryGetValue("Expect", out var exp) ? exp : "";
        if (expect.IndexOf("100-continue", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            try
            {
                var go = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");
                stream.Write(go, 0, go.Length);
                stream.Flush();
            }
            catch { return req; }
        }

        return ReadBody(req, stream, buf, bodyStart, seeded, chunked, contentLength);
    }

    // Reads the body into memory, switching to a temp file once it passes MaxInMemoryBody. Returns a fresh
    // request carrying whichever landed (records are init-only, so the body fields are set here).
    private static HttpRequest ReadBody(HttpRequest req, Stream stream, byte[] seedBuf, int seedOffset,
                                        int seedCount, bool chunked, long contentLength)
    {
        MemoryStream mem = new();
        FileStream spill = null;
        string spillPath = null;
        long written = 0;
        bool tooLarge = false;

        void Sink(byte[] data, int offset, int count)
        {
            if (count <= 0 || tooLarge) return;
            if (written + count > MaxBodyBytes) { tooLarge = true; return; }

            if (spill == null && written + count > MaxInMemoryBody)
            {
                spillPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                    "litebox-http-" + Guid.NewGuid().ToString("N") + ".tmp");
                spill = new FileStream(spillPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 64 * 1024);
                var buffered = mem.GetBuffer();
                spill.Write(buffered, 0, (int)mem.Length);
                mem.SetLength(0);
            }

            if (spill != null) spill.Write(data, offset, count);
            else mem.Write(data, offset, count);
            written += count;
        }

        try
        {
            if (chunked) ReadChunked(stream, seedBuf, seedOffset, seedCount, Sink);
            else ReadFixed(stream, seedBuf, seedOffset, seedCount, contentLength, Sink);
        }
        catch (Exception ex)
        {
            LbLog.Warn("web", "body read failed: " + ex.Message);
            try { spill?.Dispose(); } catch { }
            if (spillPath != null) { try { File.Delete(spillPath); } catch { } }
            return req;
        }
        finally { try { spill?.Dispose(); } catch { } }

        if (tooLarge)
        {
            if (spillPath != null) { try { File.Delete(spillPath); } catch { } }
            return new HttpRequest
            {
                Method = req.Method, RawTarget = req.RawTarget, Path = req.Path, RawQuery = req.RawQuery,
                Headers = req.Headers, BodyTooLarge = true,
            };
        }

        if (spillPath != null)
        {
            return new HttpRequest
            {
                Method = req.Method, RawTarget = req.RawTarget, Path = req.Path, RawQuery = req.RawQuery,
                Headers = req.Headers, BodyFilePath = spillPath, BodyLength = written,
            };
        }

        var bytes = mem.ToArray();
        string text = "";
        try { text = Encoding.UTF8.GetString(bytes); } catch { }
        return new HttpRequest
        {
            Method = req.Method, RawTarget = req.RawTarget, Path = req.Path, RawQuery = req.RawQuery,
            Headers = req.Headers, BodyBytes = bytes, BodyLength = bytes.Length, Body = text,
        };
    }

    private static void ReadFixed(Stream stream, byte[] seedBuf, int seedOffset, int seedCount,
                                  long contentLength, Action<byte[], int, int> sink)
    {
        long remaining = contentLength;

        int fromSeed = (int)Math.Min(seedCount, remaining);
        if (fromSeed > 0) { sink(seedBuf, seedOffset, fromSeed); remaining -= fromSeed; }

        var chunk = new byte[64 * 1024];
        while (remaining > 0)
        {
            int want = (int)Math.Min(chunk.Length, remaining);
            int r = stream.Read(chunk, 0, want);
            if (r <= 0) break;   // peer hung up mid-body
            sink(chunk, 0, r);
            remaining -= r;
        }
    }

    // Chunked de-framing over a seed buffer + the live stream. Trailers are read and discarded.
    private static void ReadChunked(Stream stream, byte[] seedBuf, int seedOffset, int seedCount,
                                    Action<byte[], int, int> sink)
    {
        var pending = new List<byte>(seedCount);
        for (int i = 0; i < seedCount; i++) pending.Add(seedBuf[seedOffset + i]);
        int cursor = 0;

        int ReadByte()
        {
            if (cursor < pending.Count) return pending[cursor++];
            int b = stream.ReadByte();
            return b;
        }

        while (true)
        {
            // Size line: hex digits, optional ";ext", terminated by CRLF.
            var sizeLine = new StringBuilder(16);
            while (true)
            {
                int b = ReadByte();
                if (b < 0) return;
                if (b == '\n') break;
                if (b != '\r') sizeLine.Append((char)b);
                if (sizeLine.Length > 64) return;   // nonsense
            }

            var sizeText = sizeLine.ToString();
            int semi = sizeText.IndexOf(';');
            if (semi >= 0) sizeText = sizeText.Substring(0, semi);
            if (!int.TryParse(sizeText.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int size))
                return;

            if (size == 0)
            {
                // Trailer section: read until a blank line.
                var line = new StringBuilder(64);
                while (true)
                {
                    int b = ReadByte();
                    if (b < 0) return;
                    if (b == '\n')
                    {
                        if (line.Length == 0) return;
                        line.Clear();
                        continue;
                    }
                    if (b != '\r') line.Append((char)b);
                }
            }

            var chunk = new byte[size];
            int got = 0;
            while (got < size)
            {
                if (cursor < pending.Count)
                {
                    int take = Math.Min(size - got, pending.Count - cursor);
                    for (int i = 0; i < take; i++) chunk[got + i] = pending[cursor + i];
                    cursor += take; got += take;
                    continue;
                }
                int r = stream.Read(chunk, got, size - got);
                if (r <= 0) return;
                got += r;
            }
            sink(chunk, 0, got);

            // Trailing CRLF after the chunk data.
            ReadByte();
            ReadByte();
        }
    }

    /// <summary>"//api//x" → "/api/x". Leaves a path with no repeated slash untouched (and allocates
    /// nothing for it, which is every normal request).</summary>
    private static string CollapseSlashes(string path)
    {
        if (string.IsNullOrEmpty(path) || path.IndexOf("//", StringComparison.Ordinal) < 0) return path;

        var sb = new StringBuilder(path.Length);
        bool lastSlash = false;
        foreach (var c in path)
        {
            if (c == '/')
            {
                if (lastSlash) continue;
                lastSlash = true;
            }
            else lastSlash = false;
            sb.Append(c);
        }
        return sb.ToString();
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
