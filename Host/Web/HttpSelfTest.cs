// --selftest-http: drives the embedded HTTP core against a real socket, headless.
//
// The core grew four capabilities the theme server never needed — bodies past 64 KB (spilling to disk),
// multipart/form-data, streamed responses with Range, and chunked output — and every one of them is a wire
// format that either works byte for byte or fails in a way a browser reports as "network error". So this
// binds an ephemeral port, points a real HttpClient at it, and checks the bytes that come back.
//
// What it covers: a plain GET, a 2 MB POST (crosses the in-memory threshold into a spill file), a multipart
// upload with a field and a file, a whole-file GET, a Range GET (206 + Content-Range + the right slice),
// a suffix Range, an unsatisfiable Range (416), a chunked response, HEAD (headers with the real length and
// no body), the methods RomM needs (PUT/DELETE), and the 413 refusal.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace LbApiHost.Host.Web;

internal static class HttpSelfTest
{
    private static int _failed;
    private static int _passed;

    public static int Run()
    {
        Console.WriteLine("=== HTTP core self-test ===");

        var router = new Router();
        var host = new HttpHost("selftest", router);

        // A file with a known pattern, so a Range slice can be checked byte for byte.
        var filePath = Path.Combine(Path.GetTempPath(), "litebox-selftest-" + Guid.NewGuid().ToString("N") + ".bin");
        var fileBytes = new byte[300_000];
        for (int i = 0; i < fileBytes.Length; i++) fileBytes[i] = (byte)(i % 251);
        File.WriteAllBytes(filePath, fileBytes);

        try
        {
            RegisterTestRoutes(router, filePath);
            host.Start(0);
            var baseUrl = $"http://127.0.0.1:{host.CurrentPort}";
            Console.WriteLine("listening on " + baseUrl);

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

            PlainGet(http, baseUrl);
            LargePost(http, baseUrl);
            MultipartUpload(http, baseUrl);
            FileAndRanges(http, baseUrl, fileBytes);
            ChunkedResponse(http, baseUrl);
            HeadRequest(http, baseUrl);
            Methods(http, baseUrl);
            TooLarge(http, baseUrl);
        }
        catch (Exception ex)
        {
            Fail("harness", ex.ToString());
        }
        finally
        {
            try { host.Stop(); } catch { }
            try { File.Delete(filePath); } catch { }
        }

        Console.WriteLine($"=== {_passed} passed, {_failed} failed ===");
        return _failed == 0 ? 0 : 1;
    }

    // ── The surface under test ────────────────────────────────────────────────

    private static void RegisterTestRoutes(Router router, string filePath)
    {
        // Echoes what the server made of the body: its length and a hash, so a spilled body is proven
        // intact and not merely present.
        router.Add(@"/echo", ctx =>
        {
            var req = ctx.Request;
            using var body = req.OpenBody();
            string md5 = body == null ? "" : Md5(body);
            string where = req.BodyFilePath != null ? "file" : (req.BodyBytes != null ? "memory" : "none");
            return HttpResponse.Json($"{{\"len\":{req.BodyLength},\"md5\":\"{md5}\",\"where\":\"{where}\"}}");
        });

        router.Add(@"/upload", ctx =>
        {
            using var form = MultipartReader.Parse(ctx.Request);
            if (form == null) return HttpResponse.PlainText("not multipart", 400);

            var field = form.Field("label") ?? "";
            var file = form.File();
            string md5 = "";
            long len = 0;
            string name = "";
            if (file != null)
            {
                name = file.FileName ?? "";
                len = file.Length;
                using var s = file.Open();
                if (s != null) md5 = Md5(s);
            }
            return HttpResponse.Json(
                $"{{\"label\":\"{field}\",\"file\":\"{name}\",\"len\":{len},\"md5\":\"{md5}\",\"parts\":{form.Parts.Count}}}");
        });

        router.Add(@"/file", ctx => HttpResponse.FromFile(filePath, "application/octet-stream", ctx.Request, "probe.bin"));

        router.Add(@"/chunks", _ => HttpResponse.FromChunked(s =>
        {
            for (int i = 0; i < 5; i++)
            {
                var line = Encoding.UTF8.GetBytes($"chunk-{i};");
                s.Write(line, 0, line.Length);
            }
        }, "text/plain; charset=utf-8"));

        router.Add(@"/method", ctx => HttpResponse.PlainText(ctx.Request.Method));
    }

    // ── The checks ────────────────────────────────────────────────────────────

    private static void PlainGet(HttpClient http, string baseUrl)
    {
        var r = http.GetAsync(baseUrl + "/method").GetAwaiter().GetResult();
        Check("plain GET", r.StatusCode == HttpStatusCode.OK
            && r.Content.ReadAsStringAsync().GetAwaiter().GetResult() == "GET");

        // Clients that join a base URL and an absolute path produce "//path", and every RomM
        // deployment gets away with it because nginx merges slashes before the app ever sees the URI.
        // As the origin server we do that ourselves — without it those requests miss every anchored
        // route, which is exactly how a client ends up showing no cover art.
        var doubled = http.GetAsync(baseUrl + "//method").GetAwaiter().GetResult();
        Check("a doubled slash is merged like nginx does",
            doubled.StatusCode == HttpStatusCode.OK
            && doubled.Content.ReadAsStringAsync().GetAwaiter().GetResult() == "GET");

        var tripled = http.GetAsync(baseUrl + "///method").GetAwaiter().GetResult();
        Check("so is a longer run", tripled.StatusCode == HttpStatusCode.OK);
    }

    private static void LargePost(HttpClient http, string baseUrl)
    {
        // Past MaxInMemoryBody, so the read path must switch to a spill file mid-stream — the moment the
        // in-memory prefix is flushed to disk is exactly where a body gets silently truncated.
        var payload = new byte[2 * 1024 * 1024 + 1234];
        RandomNumberGenerator.Fill(payload);
        var expected = Md5(new MemoryStream(payload));

        var r = http.PostAsync(baseUrl + "/echo", new ByteArrayContent(payload)).GetAwaiter().GetResult();
        var text = r.Content.ReadAsStringAsync().GetAwaiter().GetResult();

        Check($"2 MB POST spills and survives ({text})",
            text.Contains($"\"len\":{payload.Length}") && text.Contains($"\"md5\":\"{expected}\"") && text.Contains("\"where\":\"file\""));

        // And a small one must still land in memory, where Body (the string) keeps working for JSON.
        var small = Encoding.UTF8.GetBytes("{\"hello\":\"world\"}");
        var r2 = http.PostAsync(baseUrl + "/echo", new ByteArrayContent(small)).GetAwaiter().GetResult();
        var t2 = r2.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        Check("small POST stays in memory", t2.Contains("\"where\":\"memory\"") && t2.Contains($"\"len\":{small.Length}"));
    }

    private static void MultipartUpload(HttpClient http, string baseUrl)
    {
        // Deliberately bigger than MaxInMemoryPart so the part spills too, and full of bytes that can
        // collide with a boundary scan.
        var file = new byte[400_000];
        RandomNumberGenerator.Fill(file);
        var expected = Md5(new MemoryStream(file));

        using var form = new MultipartFormDataContent("----litebox-selftest-boundary");
        form.Add(new StringContent("a save"), "label");
        var part = new ByteArrayContent(file);
        form.Add(part, "saveFile", "mario.srm");

        var r = http.PostAsync(baseUrl + "/upload", form).GetAwaiter().GetResult();
        var text = r.Content.ReadAsStringAsync().GetAwaiter().GetResult();

        Check($"multipart upload ({text})",
            text.Contains("\"label\":\"a save\"")
            && text.Contains("\"file\":\"mario.srm\"")
            && text.Contains($"\"len\":{file.Length}")
            && text.Contains($"\"md5\":\"{expected}\"")
            && text.Contains("\"parts\":2"));
    }

    private static void FileAndRanges(HttpClient http, string baseUrl, byte[] fileBytes)
    {
        var whole = http.GetAsync(baseUrl + "/file").GetAwaiter().GetResult();
        var wholeBytes = whole.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
        Check("whole file streams intact",
            whole.StatusCode == HttpStatusCode.OK
            && wholeBytes.Length == fileBytes.Length
            && wholeBytes.SequenceEqual(fileBytes));
        Check("whole file advertises ranges + a download name",
            whole.Headers.AcceptRanges.Contains("bytes")
            && (whole.Content.Headers.ContentDisposition?.FileName ?? "").Contains("probe.bin"));

        var mid = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/file");
        mid.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(1000, 1999);
        var midResp = http.SendAsync(mid).GetAwaiter().GetResult();
        var midBytes = midResp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
        Check("Range 1000-1999 → 206 with the right slice",
            midResp.StatusCode == HttpStatusCode.PartialContent
            && midBytes.Length == 1000
            && midBytes.SequenceEqual(fileBytes.Skip(1000).Take(1000))
            && (midResp.Content.Headers.ContentRange?.To ?? -1) == 1999
            && (midResp.Content.Headers.ContentRange?.Length ?? -1) == fileBytes.Length);

        var suffix = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/file");
        suffix.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(null, 500);
        var sufResp = http.SendAsync(suffix).GetAwaiter().GetResult();
        var sufBytes = sufResp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
        Check("suffix Range -500 → the last 500 bytes",
            sufResp.StatusCode == HttpStatusCode.PartialContent
            && sufBytes.Length == 500
            && sufBytes.SequenceEqual(fileBytes.Skip(fileBytes.Length - 500)));

        var past = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/file");
        past.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(fileBytes.Length + 10, null);
        var pastResp = http.SendAsync(past).GetAwaiter().GetResult();
        Check("Range past the end → 416", (int)pastResp.StatusCode == 416);
    }

    private static void ChunkedResponse(HttpClient http, string baseUrl)
    {
        var r = http.GetAsync(baseUrl + "/chunks").GetAwaiter().GetResult();
        var text = r.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        Check($"chunked response reassembles ({text})",
            r.StatusCode == HttpStatusCode.OK && text == "chunk-0;chunk-1;chunk-2;chunk-3;chunk-4;");
    }

    private static void HeadRequest(HttpClient http, string baseUrl)
    {
        var r = http.SendAsync(new HttpRequestMessage(HttpMethod.Head, baseUrl + "/file")).GetAwaiter().GetResult();
        var body = r.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
        Check("HEAD reports the real length and sends no body",
            r.StatusCode == HttpStatusCode.OK
            && (r.Content.Headers.ContentLength ?? -1) == 300_000
            && body.Length == 0);
    }

    private static void Methods(HttpClient http, string baseUrl)
    {
        foreach (var m in new[] { "PUT", "DELETE", "PATCH" })
        {
            var req = new HttpRequestMessage(new HttpMethod(m), baseUrl + "/method");
            if (m != "DELETE") req.Content = new StringContent("{}");
            var r = http.SendAsync(req).GetAwaiter().GetResult();
            var text = r.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            Check($"{m} reaches the router", r.StatusCode == HttpStatusCode.OK && text == m);
        }
    }

    private static void TooLarge(HttpClient http, string baseUrl)
    {
        long saved = HttpRequest.MaxBodyBytes;
        try
        {
            HttpRequest.MaxBodyBytes = 64 * 1024;
            var payload = new byte[256 * 1024];
            var r = http.PostAsync(baseUrl + "/echo", new ByteArrayContent(payload)).GetAwaiter().GetResult();
            Check("an over-sized body is refused with 413", (int)r.StatusCode == 413);
        }
        catch (Exception ex) { Fail("413 refusal", ex.Message); }
        finally { HttpRequest.MaxBodyBytes = saved; }
    }

    // ── Reporting ─────────────────────────────────────────────────────────────

    private static void Check(string what, bool ok)
    {
        if (ok) { _passed++; Console.WriteLine("  PASS  " + what); }
        else { _failed++; Console.WriteLine("  FAIL  " + what); }
    }

    private static void Fail(string what, string detail)
    {
        _failed++;
        Console.WriteLine("  FAIL  " + what + ": " + detail);
    }

    private static string Md5(Stream s)
    {
        using var md5 = MD5.Create();
        var hash = md5.ComputeHash(s);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }
}
