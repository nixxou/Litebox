// Minimal HTTP/1.1 response writer for the embedded web server.
//
// Three body shapes, picked by the helper you build with:
//   • byte[]        — the historical one: JSON, HTML, small images. Buffered, gzip-eligible.
//   • Stream        — known length, streamed to the socket in 64 KB slices. FromFile() builds these and
//                     handles Range/206, so a 4 GB ISO is served without ever being in RAM.
//   • ChunkedWriter — unknown length (a zip built on the fly): Transfer-Encoding: chunked, the callback
//                     writes into a stream that frames each Write as a chunk.
//
// Optional gzip applies to buffered JSON only ([Web] GzipJson + the client's Accept-Encoding); a streamed or
// chunked body is passed through untouched. Connection handling (keep-alive vs close) is decided by the
// server's request loop, which sets the Connection header before Write.

using System;
using System.Collections.Generic;
using System.Globalization;
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

    /// <summary>Known-length body streamed straight to the socket. Disposed by Write.</summary>
    public Stream BodyStream { get; set; }

    /// <summary>Byte count <see cref="BodyStream"/> will produce (becomes Content-Length).</summary>
    public long BodyStreamLength { get; set; }

    /// <summary>Unknown-length body: Write sends Transfer-Encoding: chunked and hands this callback a
    /// stream that frames every write as a chunk.</summary>
    public Action<Stream> ChunkedWriter { get; set; }

    /// <summary>HEAD: emit the headers, including the real Content-Length, but no body.</summary>
    public bool SuppressBody { get; set; }

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

    /// <summary>Streams <paramref name="stream"/> as the body. Length must be known (it becomes
    /// Content-Length); the stream is disposed after the write.</summary>
    public static HttpResponse FromStream(Stream stream, long length, string contentType, int status = 200)
    {
        var r = new HttpResponse
        {
            StatusCode = status, StatusText = TextFor(status),
            BodyStream = stream, BodyStreamLength = Math.Max(0, length),
        };
        r.Headers["Content-Type"] = contentType ?? "application/octet-stream";
        return r;
    }

    /// <summary>Streams a body of unknown length with chunked transfer-encoding. The callback writes into
    /// the supplied stream; each write becomes one chunk.</summary>
    public static HttpResponse FromChunked(Action<Stream> writer, string contentType, int status = 200)
    {
        var r = new HttpResponse { StatusCode = status, StatusText = TextFor(status), ChunkedWriter = writer };
        r.Headers["Content-Type"] = contentType ?? "application/octet-stream";
        return r;
    }

    /// <summary>Serves a file from disk, streamed, honouring a single <c>Range</c> header on
    /// <paramref name="req"/> (206 + Content-Range; 416 when unsatisfiable). Missing file → 404.</summary>
    public static HttpResponse FromFile(string path, string contentType, HttpRequest req,
                                        string downloadName = null)
    {
        FileInfo info;
        try { info = new FileInfo(path); if (!info.Exists) return NotFound(); }
        catch { return NotFound(); }

        long total = info.Length;
        long start = 0, end = total - 1;
        bool partial = false;

        var range = req?.GetHeader("Range");
        if (!string.IsNullOrEmpty(range) && total > 0)
        {
            switch (ParseRange(range, total, ref start, ref end))
            {
                case RangeResult.Partial: partial = true; break;
                case RangeResult.Unsatisfiable:
                    var bad = PlainText("Requested range not satisfiable", 416);
                    bad.Headers["Content-Range"] = "bytes */" + total.ToString(CultureInfo.InvariantCulture);
                    bad.Headers["Accept-Ranges"] = "bytes";
                    return bad;
                default: break;   // ignore a malformed header, serve the whole file
            }
        }

        FileStream fs;
        try
        {
            fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024);
            if (start > 0) fs.Seek(start, SeekOrigin.Begin);
        }
        catch (Exception ex)
        {
            LbLog.Warn("web", $"file open failed {path}: {ex.Message}");
            return ServerError("read error");
        }

        long length = total == 0 ? 0 : end - start + 1;
        var r = FromStream(fs, length, contentType, partial ? 206 : 200);
        r.Headers["Accept-Ranges"] = "bytes";
        r.Headers["Last-Modified"] = info.LastWriteTimeUtc.ToString("R", CultureInfo.InvariantCulture);
        if (partial)
            r.Headers["Content-Range"] =
                $"bytes {start.ToString(CultureInfo.InvariantCulture)}-{end.ToString(CultureInfo.InvariantCulture)}/{total.ToString(CultureInfo.InvariantCulture)}";
        if (!string.IsNullOrEmpty(downloadName))
            r.Headers["Content-Disposition"] = BuildDisposition(downloadName);
        return r;
    }

    /// <summary>RFC 6266 attachment header with both the plain and the UTF-8 encoded name, the way every
    /// browser and the RomM clients expect it.</summary>
    public static string BuildDisposition(string fileName)
    {
        var safe = (fileName ?? "download").Replace("\"", "");
        string encoded;
        try { encoded = Uri.EscapeDataString(safe); } catch { encoded = safe; }
        return $"attachment; filename=\"{safe}\"; filename*=UTF-8''{encoded}";
    }

    private enum RangeResult { None, Partial, Unsatisfiable }

    // "bytes=start-end" | "bytes=start-" | "bytes=-suffix". Multi-range is answered with the whole file
    // (legal per RFC 7233 and far simpler than multipart/byteranges); nothing we serve asks for it.
    private static RangeResult ParseRange(string header, long total, ref long start, ref long end)
    {
        const string prefix = "bytes=";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return RangeResult.None;
        var spec = header.Substring(prefix.Length).Trim();
        if (spec.IndexOf(',') >= 0) return RangeResult.None;

        int dash = spec.IndexOf('-');
        if (dash < 0) return RangeResult.None;

        var left = spec.Substring(0, dash).Trim();
        var right = spec.Substring(dash + 1).Trim();

        if (left.Length == 0)
        {
            // Suffix form: the last N bytes.
            if (!long.TryParse(right, out long suffix) || suffix <= 0) return RangeResult.None;
            if (suffix > total) suffix = total;
            start = total - suffix;
            end = total - 1;
            return RangeResult.Partial;
        }

        if (!long.TryParse(left, out long from) || from < 0) return RangeResult.None;
        if (from >= total) return RangeResult.Unsatisfiable;

        long to = total - 1;
        if (right.Length > 0 && (!long.TryParse(right, out to) || to < from)) return RangeResult.None;
        if (to >= total) to = total - 1;

        start = from;
        end = to;
        return RangeResult.Partial;
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

        bool streamed = BodyStream != null;
        bool chunked = ChunkedWriter != null;

        // ── Optional gzip (buffered JSON only) ── all conditions must hold:
        //   1. [Web] GzipJson is on.  2. Client sent Accept-Encoding: gzip.  3. Not already encoded.
        //   4. Content-Type is application/json.  5. Body ≥ GzipMinBytes.
        if (!streamed && !chunked
            && AcceptsGzip
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

        // A HEAD reply carries no body, so it must not announce chunked framing either — a client that
        // read that header would sit waiting for chunks that never come.
        if (chunked && !SuppressBody) Headers["Transfer-Encoding"] = "chunked";
        else if (!chunked) Headers["Content-Length"] = (streamed ? BodyStreamLength : Body.Length)
                                                       .ToString(CultureInfo.InvariantCulture);

        if (!Headers.ContainsKey("Cache-Control"))
            Headers["Cache-Control"] = "no-store";

        var head = new StringBuilder();
        head.Append("HTTP/1.1 ").Append(StatusCode).Append(' ').Append(StatusText).Append("\r\n");
        foreach (var kv in Headers)
            head.Append(kv.Key).Append(": ").Append(kv.Value).Append("\r\n");
        head.Append("\r\n");

        var headBytes = Encoding.ASCII.GetBytes(head.ToString());
        stream.Write(headBytes, 0, headBytes.Length);

        if (SuppressBody)
        {
            try { BodyStream?.Dispose(); } catch { }
            stream.Flush();
            return;
        }

        if (chunked)
        {
            using (var chunkStream = new ChunkedStream(stream))
            {
                ChunkedWriter(chunkStream);
                chunkStream.Complete();
            }
        }
        else if (streamed)
        {
            try { Pump(BodyStream, stream, BodyStreamLength); }
            finally { try { BodyStream.Dispose(); } catch { } }
        }
        else if (Body.Length > 0)
        {
            stream.Write(Body, 0, Body.Length);
        }

        stream.Flush();
    }

    private static void Pump(Stream from, Stream to, long count)
    {
        var buf = new byte[64 * 1024];
        long left = count;
        while (left > 0)
        {
            int want = (int)Math.Min(buf.Length, left);
            int r = from.Read(buf, 0, want);
            if (r <= 0) break;
            to.Write(buf, 0, r);
            left -= r;
        }
    }

    /// <summary>Frames every write as one HTTP chunk. Complete() emits the terminating zero chunk.</summary>
    private sealed class ChunkedStream : Stream
    {
        private readonly Stream _inner;
        private bool _completed;

        public ChunkedStream(Stream inner) { _inner = inner; }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (count <= 0 || _completed) return;
            var size = Encoding.ASCII.GetBytes(count.ToString("x", CultureInfo.InvariantCulture) + "\r\n");
            _inner.Write(size, 0, size.Length);
            _inner.Write(buffer, offset, count);
            _inner.Write(Crlf, 0, Crlf.Length);
        }

        public void Complete()
        {
            if (_completed) return;
            _completed = true;
            var tail = Encoding.ASCII.GetBytes("0\r\n\r\n");
            _inner.Write(tail, 0, tail.Length);
            _inner.Flush();
        }

        private static readonly byte[] Crlf = { (byte)'\r', (byte)'\n' };

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) Complete(); base.Dispose(disposing); }
    }

    private static string TextFor(int code) => code switch
    {
        200 => "OK",
        201 => "Created",
        204 => "No Content",
        206 => "Partial Content",
        301 => "Moved Permanently",
        302 => "Found",
        304 => "Not Modified",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        409 => "Conflict",
        413 => "Payload Too Large",
        416 => "Range Not Satisfiable",
        422 => "Unprocessable Entity",
        500 => "Internal Server Error",
        501 => "Not Implemented",
        503 => "Service Unavailable",
        _ => "OK",
    };
}
