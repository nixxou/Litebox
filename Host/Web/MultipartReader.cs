// multipart/form-data parser for the embedded web server.
//
// Every RomM asset upload (saves, states, screenshots) is multipart, and so is the browser player's
// save write-back — none of which the server could read before. The parse is a streaming scan over the
// request body (which itself may already be a spill file, see HttpRequest), so a 20 MB savestate is never
// materialised twice: small parts stay in memory, large ones spill to their own temp file.
//
// Deliberately narrow: no nested multipart, no base64/quoted-printable transfer-encodings, no
// multipart/mixed. RFC 7578 form-data is what clients send and all this needs to read.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using LbApiHost.Host.Diag;

namespace LbApiHost.Host.Web;

/// <summary>One part of a multipart/form-data body. Dispose drops the spill file, if any.</summary>
internal sealed class MultipartPart : IDisposable
{
    /// <summary>The form field name (the <c>name=</c> of Content-Disposition).</summary>
    public string Name { get; set; } = "";

    /// <summary>The client's file name, or null for a plain field.</summary>
    public string FileName { get; set; }

    /// <summary>The part's own Content-Type, or null.</summary>
    public string ContentType { get; set; }

    /// <summary>Content when it stayed in memory, else null.</summary>
    public byte[] Bytes { get; set; }

    /// <summary>Temp file holding the content when it was too big for memory, else null.</summary>
    public string FilePath { get; set; }

    public long Length { get; set; }

    /// <summary>True for a part that carried a file name — i.e. an upload rather than a field.</summary>
    public bool IsFile => FileName != null;

    /// <summary>The part decoded as UTF-8 text. Empty for a spilled part (a form field is never that big).</summary>
    public string Text
    {
        get
        {
            if (Bytes == null) return "";
            try { return Encoding.UTF8.GetString(Bytes); } catch { return ""; }
        }
    }

    /// <summary>Opens the content for reading, wherever it lives. Null when the part is empty.</summary>
    public Stream Open()
    {
        if (FilePath != null)
        {
            try { return new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024); }
            catch { return null; }
        }
        if (Bytes != null) return new MemoryStream(Bytes, writable: false);
        return null;
    }

    /// <summary>Copies the content to <paramref name="destPath"/>, whichever form it is in.</summary>
    public bool SaveTo(string destPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            if (FilePath != null) { File.Copy(FilePath, destPath, overwrite: true); return true; }
            File.WriteAllBytes(destPath, Bytes ?? Array.Empty<byte>());
            return true;
        }
        catch (Exception ex) { LbLog.Warn("web", $"multipart save failed {destPath}: {ex.Message}"); return false; }
    }

    public void Dispose()
    {
        if (FilePath == null) return;
        try { File.Delete(FilePath); } catch { }
        FilePath = null;
    }
}

/// <summary>The parts of one request, disposed together.</summary>
internal sealed class MultipartForm : IDisposable
{
    public List<MultipartPart> Parts { get; } = new();

    /// <summary>The first part with this field name, or null.</summary>
    public MultipartPart Get(string name)
    {
        foreach (var p in Parts)
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p;
        return null;
    }

    /// <summary>The text value of a plain form field, or null when absent.</summary>
    public string Field(string name) => Get(name)?.Text;

    /// <summary>The first uploaded file part — by field name when given, else the first file of any name.
    /// Clients disagree on the field name (RomM sends <c>saveFile</c>, some send <c>file</c>), so falling
    /// back to "the one file in the body" is what makes them all work.</summary>
    public MultipartPart File(string name = null)
    {
        if (name != null)
        {
            var byName = Get(name);
            if (byName != null && byName.IsFile) return byName;
        }
        foreach (var p in Parts)
            if (p.IsFile) return p;
        return null;
    }

    public void Dispose()
    {
        foreach (var p in Parts) p.Dispose();
        Parts.Clear();
    }
}

internal static class MultipartReader
{
    /// <summary>Parts at or below this stay in memory; bigger ones spill to a temp file.</summary>
    private const int MaxInMemoryPart = 256 * 1024;

    private const int BufferSize = 128 * 1024;

    /// <summary>The <c>boundary=</c> value of a multipart Content-Type, or null when there is none.</summary>
    public static string BoundaryOf(string contentTypeHeader)
    {
        if (string.IsNullOrEmpty(contentTypeHeader)) return null;
        foreach (var seg in contentTypeHeader.Split(';'))
        {
            var s = seg.Trim();
            if (!s.StartsWith("boundary=", StringComparison.OrdinalIgnoreCase)) continue;
            var v = s.Substring("boundary=".Length).Trim();
            if (v.Length >= 2 && v[0] == '"' && v[v.Length - 1] == '"') v = v.Substring(1, v.Length - 2);
            return v.Length > 0 ? v : null;
        }
        return null;
    }

    /// <summary>Parses the request body as multipart/form-data. Returns null when the request is not
    /// multipart, has no boundary, or the body is unreadable — never throws.</summary>
    public static MultipartForm Parse(HttpRequest req)
    {
        if (req == null) return null;
        if (req.ContentType != "multipart/form-data") return null;

        var boundary = BoundaryOf(req.GetHeader("Content-Type"));
        if (boundary == null) return null;

        var body = req.OpenBody();
        if (body == null) return null;

        var form = new MultipartForm();
        try
        {
            Scan(body, boundary, form);
            return form;
        }
        catch (Exception ex)
        {
            LbLog.Warn("web", "multipart parse failed: " + ex.Message);
            form.Dispose();
            return null;
        }
        finally { try { body.Dispose(); } catch { } }
    }

    // ── The scan ────────────────────────────────────────────────────────────
    //
    //   --boundary CRLF  headers CRLF CRLF  content  CRLF--boundary ...  CRLF--boundary-- CRLF
    //
    // The content of a part is everything up to the next CRLF--boundary, so that is the pattern we hunt;
    // the leading CRLF belongs to the delimiter, not the data.

    private static void Scan(Stream body, string boundary, MultipartForm form)
    {
        var win = new Window(body);
        var dashBoundary = Encoding.ASCII.GetBytes("--" + boundary);
        var crlfDashBoundary = Encoding.ASCII.GetBytes("\r\n--" + boundary);

        // Preamble: skip to the first "--boundary".
        if (!win.SkipPast(dashBoundary)) return;

        while (true)
        {
            // After a delimiter: "--" ends the body, CRLF starts a part.
            var two = win.PeekTwo();
            if (two == null) return;
            if (two[0] == '-' && two[1] == '-') return;      // closing delimiter
            if (two[0] == '\r' && two[1] == '\n') win.Skip(2);
            else return;                                      // malformed

            var part = new MultipartPart();
            if (!ReadPartHeaders(win, part)) return;
            if (!ReadPartContent(win, crlfDashBoundary, part)) { part.Dispose(); return; }
            form.Parts.Add(part);
        }
    }

    private static bool ReadPartHeaders(Window win, MultipartPart part)
    {
        var headerBytes = win.ReadUntil(HeaderEnd, maxBytes: 16 * 1024);
        if (headerBytes == null) return false;

        var text = Encoding.UTF8.GetString(headerBytes);
        foreach (var line in text.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = line.Substring(0, colon).Trim();
            var val = line.Substring(colon + 1).Trim();

            if (key.Equals("Content-Disposition", StringComparison.OrdinalIgnoreCase))
            {
                part.Name = ParamOf(val, "name") ?? "";
                part.FileName = ParamOf(val, "filename");
                if (part.FileName != null) part.FileName = Path.GetFileName(part.FileName);
            }
            else if (key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                part.ContentType = val;
            }
        }
        return true;
    }

    private static bool ReadPartContent(Window win, byte[] delimiter, MultipartPart part)
    {
        MemoryStream mem = new();
        FileStream spill = null;
        string spillPath = null;
        long written = 0;

        void Sink(byte[] data, int offset, int count)
        {
            if (count <= 0) return;
            if (spill == null && written + count > MaxInMemoryPart)
            {
                spillPath = Path.Combine(Path.GetTempPath(), "litebox-part-" + Guid.NewGuid().ToString("N") + ".tmp");
                spill = new FileStream(spillPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 64 * 1024);
                spill.Write(mem.GetBuffer(), 0, (int)mem.Length);
                mem.SetLength(0);
            }
            if (spill != null) spill.Write(data, offset, count);
            else mem.Write(data, offset, count);
            written += count;
        }

        bool found;
        try { found = win.CopyUntil(delimiter, Sink); }
        finally { try { spill?.Dispose(); } catch { } }

        if (!found)
        {
            if (spillPath != null) { try { File.Delete(spillPath); } catch { } }
            return false;
        }

        part.Length = written;
        if (spillPath != null) part.FilePath = spillPath;
        else part.Bytes = mem.ToArray();
        return true;
    }

    private static readonly byte[] HeaderEnd = Encoding.ASCII.GetBytes("\r\n\r\n");

    /// <summary>Value of a <c>key="value"</c> / <c>key=value</c> parameter in a header, or null.</summary>
    private static string ParamOf(string headerValue, string key)
    {
        foreach (var seg in headerValue.Split(';'))
        {
            var s = seg.Trim();
            if (!s.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)) continue;
            var v = s.Substring(key.Length + 1).Trim();
            if (v.Length >= 2 && v[0] == '"' && v[v.Length - 1] == '"') v = v.Substring(1, v.Length - 2);
            return v;
        }
        return null;
    }

    /// <summary>A sliding read window over the body: enough buffered to match a delimiter that may straddle
    /// two reads, and never more than BufferSize resident.</summary>
    private sealed class Window
    {
        private readonly Stream _s;
        private readonly byte[] _buf = new byte[BufferSize];
        private int _start;
        private int _len;
        private bool _eof;

        public Window(Stream s) { _s = s; }

        // Compacts to the front and reads more. False when nothing more can arrive.
        private bool Fill()
        {
            if (_eof) return false;
            if (_start > 0) { Buffer.BlockCopy(_buf, _start, _buf, 0, _len); _start = 0; }
            int space = _buf.Length - _len;
            if (space == 0) return true;    // full: the caller must consume before we can read again
            int r = _s.Read(_buf, _len, space);
            if (r <= 0) { _eof = true; return false; }
            _len += r;
            return true;
        }

        private int IndexOf(byte[] pattern)
        {
            int limit = _len - pattern.Length;
            for (int i = 0; i <= limit; i++)
            {
                int j = 0;
                while (j < pattern.Length && _buf[_start + i + j] == pattern[j]) j++;
                if (j == pattern.Length) return i;
            }
            return -1;
        }

        public void Skip(int n) { _start += n; _len -= n; }

        public byte[] PeekTwo()
        {
            while (_len < 2 && Fill()) { }
            if (_len < 2) return null;
            return new[] { _buf[_start], _buf[_start + 1] };
        }

        /// <summary>Consumes everything up to and including the first occurrence of <paramref name="pattern"/>.</summary>
        public bool SkipPast(byte[] pattern)
        {
            while (true)
            {
                int at = IndexOf(pattern);
                if (at >= 0) { Skip(at + pattern.Length); return true; }
                int keep = pattern.Length - 1;
                if (_len > keep) Skip(_len - keep);
                if (!Fill()) return false;
            }
        }

        /// <summary>Returns the bytes before <paramref name="pattern"/> and consumes them plus the pattern.
        /// Null when the pattern never arrives or the span exceeds <paramref name="maxBytes"/>.</summary>
        public byte[] ReadUntil(byte[] pattern, int maxBytes)
        {
            var acc = new MemoryStream();
            while (true)
            {
                int at = IndexOf(pattern);
                if (at >= 0)
                {
                    acc.Write(_buf, _start, at);
                    Skip(at + pattern.Length);
                    return acc.ToArray();
                }
                int keep = pattern.Length - 1;
                if (_len > keep)
                {
                    int flush = _len - keep;
                    acc.Write(_buf, _start, flush);
                    Skip(flush);
                    if (acc.Length > maxBytes) return null;
                }
                if (!Fill()) return null;
            }
        }

        /// <summary>Streams everything before <paramref name="pattern"/> into <paramref name="sink"/>, then
        /// consumes the pattern. False when the pattern never arrives.</summary>
        public bool CopyUntil(byte[] pattern, Action<byte[], int, int> sink)
        {
            while (true)
            {
                int at = IndexOf(pattern);
                if (at >= 0)
                {
                    sink(_buf, _start, at);
                    Skip(at + pattern.Length);
                    return true;
                }
                int keep = pattern.Length - 1;
                if (_len > keep)
                {
                    int flush = _len - keep;
                    sink(_buf, _start, flush);
                    Skip(flush);
                }
                if (!Fill()) return false;
            }
        }
    }
}
