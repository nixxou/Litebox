// R5 — Select-ROM per-entry metadata overlay (BigBox theme only).
//
//   GET /bigbox/api/games/{id}/archive-metadata[?appId=…&entry=…]
//
// Renders the metadata sidecar (an HTML template + a per-archive JSON) for the highlighted archive entry
// into HTML the BigBox Select-ROM sub-menu overlays on the right-pane description (engine/app.js reads
// { ok, html }). Clean-room LiteBox rewrite of ExtendDB's Web/Theme/ArchiveMetadataApi.cs + the token
// substitution of its Utility/ArchiveMetadataResolver, retargeted to System.Text.Json (no Newtonsoft) and
// LiteBox paths (no plugin path).
//
// Resolution order (first match wins for each), plugin-path-free:
//   JSON      → <archive-dir>\<archiveBasename>.json
//               <LB>\Core\litebox\rom-metadata\metadata\<Platform>\<archiveBasename>.json
//   template  → <archive-dir>\template.html
//               <LB>\Core\litebox\rom-metadata\templates\<Platform>.html
//               <LB>\Core\litebox\rom-metadata\templates\default.html
//
// Tokens: [[JSONDATA]] (auto-narrowed to the entry when the JSON is a per-rom map), [[JSONDATA_ALL]],
// [[ENTRY]], [[ARCHIVE]], [[FIELD]], [[FIELD.NESTED]]. Unknown tokens are left untouched. Every path is
// best-effort: no template + a JSON sidecar → a default key/value table (fallback:true); nothing → 404.
//
// Response: { ok:true, html, fallback } | { ok:false, reason }. Gate: RomExtractor.Available. No secret literal.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LbApiHost.Host.Diag;
using LbApiHost.Host.Rom;
using Unbroken.LaunchBox.Plugins.Data;

namespace LbApiHost.Host.Web;

internal static class ArchiveMetadataApi
{
    private static readonly Regex TokenRegex = new(@"\[\[([A-Za-z0-9_\.]+)\]\]", RegexOptions.Compiled);

    public static HttpResponse Handle(RouteContext ctx)
    {
        if (!string.Equals(ctx.Request?.Method, "GET", StringComparison.OrdinalIgnoreCase))
            return HttpResponse.PlainText("Method not allowed", 405);
        try
        {
            if (!RomExtractor.Available) return Fail("feature disabled", 404);

            var id = ctx.GetRoute("id");
            if (string.IsNullOrEmpty(id)) return Fail("bad id");
            var game = ArchiveListingApi.ResolveGame(id);
            if (game == null) return Fail("not in library");

            var appId = ctx.Request?.GetQuery("appId");
            var entry = ctx.Request?.GetQuery("entry") ?? "";

            var absPath = RomExtractor.ResolveArchiveAbsolutePath(game, appId);
            if (string.IsNullOrEmpty(absPath)) return Fail("no archive", 404);

            var platform = Safe(() => game.Platform) ?? "";
            var meta = Resolve(absPath!, platform);
            if (!meta.HasAnything) return Fail("no metadata", 404);

            string? html = Render(meta, entry);
            bool fallback = false;
            if (string.IsNullOrEmpty(html)) { html = DefaultJsonOnlyPreview(meta, entry); fallback = !string.IsNullOrEmpty(html); }
            if (string.IsNullOrEmpty(html)) return Fail("metadata empty after render", 404);

            Log($"render id={id} appId={appId ?? "<main>"} entry=\"{entry}\" template={(meta.TemplatePath ?? "<none>")} json={(meta.JsonPath ?? "<none>")} htmlLen={html!.Length}");
            return HttpResponse.Json(JsonSerializer.Serialize(new { ok = true, html, fallback }));
        }
        catch (Exception ex) { Log("threw: " + ex.Message); return Fail("server error: " + ex.Message); }
    }

    // ── sidecar lookup ────────────────────────────────────────────────────────

    private sealed class Meta
    {
        public string ArchiveBasename = "";
        public string? JsonPath;
        public string? JsonRaw;
        public string? TemplatePath;
        public string? TemplateRaw;
        public bool HasAnything => !string.IsNullOrEmpty(JsonRaw) || !string.IsNullOrEmpty(TemplateRaw);
    }

    private static Meta Resolve(string archivePath, string platform)
    {
        var basename = string.IsNullOrEmpty(archivePath) ? "" : Path.GetFileNameWithoutExtension(archivePath);
        var safePlatform = SanitizeFolderName(platform ?? "");
        var jsonPath = FindJson(archivePath, safePlatform, basename);
        var templatePath = FindTemplate(archivePath, safePlatform);
        return new Meta
        {
            ArchiveBasename = basename,
            JsonPath = jsonPath,
            JsonRaw = TryReadAllText(jsonPath),
            TemplatePath = templatePath,
            TemplateRaw = TryReadAllText(templatePath),
        };
    }

    private static string MetaRoot => Path.Combine(LiteBoxPaths.Data, "rom-metadata");

    private static string? FindJson(string archivePath, string safePlatform, string basename)
    {
        if (string.IsNullOrEmpty(archivePath) || string.IsNullOrEmpty(basename)) return null;
        var dir = SafeDir(archivePath);
        var p1 = dir != null ? Path.Combine(dir, basename + ".json") : null;
        if (p1 != null && File.Exists(p1)) return p1;
        try { var p2 = Path.Combine(MetaRoot, "metadata", safePlatform, basename + ".json"); if (File.Exists(p2)) return p2; } catch { }
        // Compat: central metadata authored under the ExtendDB plugin (<plugin>\ArchiveMgs\metadata\<Platform>\<name>.json).
        foreach (var root in ArchiveMgsRoots())
            try { var pc = Path.Combine(root, "metadata", safePlatform, basename + ".json"); if (File.Exists(pc)) return pc; } catch { }
        return null;
    }

    private static string? FindTemplate(string archivePath, string safePlatform)
    {
        var dir = SafeDir(archivePath);
        var p1 = dir != null ? Path.Combine(dir, "template.html") : null;
        if (p1 != null && File.Exists(p1)) return p1;
        try
        {
            var p2 = Path.Combine(MetaRoot, "templates", safePlatform + ".html"); if (File.Exists(p2)) return p2;
            var p3 = Path.Combine(MetaRoot, "templates", "default.html"); if (File.Exists(p3)) return p3;
        }
        catch { }
        // Compat: central templates authored under the ExtendDB plugin.
        foreach (var root in ArchiveMgsRoots())
            try
            {
                var pc = Path.Combine(root, "templates", safePlatform + ".html"); if (File.Exists(pc)) return pc;
                var pd = Path.Combine(root, "templates", "default.html"); if (File.Exists(pd)) return pd;
            }
            catch { }
        return null;
    }

    /// <summary>Candidate ExtendDB-plugin ArchiveMgs roots (its central metadata/templates), so LiteBox
    /// finds sidecars/templates a user authored under the plugin's convention. Checked AFTER LiteBox's own
    /// rom-metadata root and the next-to-archive files.</summary>
    private static IEnumerable<string> ArchiveMgsRoots()
    {
        var lb = LbApiHost.Host.Media.MediaResolver.LbRoot;
        if (string.IsNullOrEmpty(lb)) yield break;
        yield return Path.Combine(lb!, "Plugins", "ExtendDB", "ArchiveMgs");
        yield return Path.Combine(lb!, "ExtendDB", "ArchiveMgs");
        yield return Path.Combine(lb!, "ArchiveExtend", "ExtendDB", "ArchiveMgs");
    }

    // ── render ────────────────────────────────────────────────────────────────

    private static string? Render(Meta meta, string entryFileName)
    {
        if (string.IsNullOrEmpty(meta.TemplateRaw)) return null;
        JsonDocument? doc = null;
        try { if (!string.IsNullOrEmpty(meta.JsonRaw)) doc = JsonDocument.Parse(meta.JsonRaw!); } catch { doc = null; }
        try
        {
            var root = doc?.RootElement;
            return TokenRegex.Replace(meta.TemplateRaw!, m =>
            {
                var tok = m.Groups[1].Value;
                return ResolveToken(tok, meta, root, entryFileName) ?? m.Value;
            });
        }
        finally { doc?.Dispose(); }
    }

    private static string DefaultJsonOnlyPreview(Meta meta, string entryFileName)
    {
        if (string.IsNullOrEmpty(meta.JsonRaw)) return "";
        JsonDocument doc;
        try { doc = JsonDocument.Parse(meta.JsonRaw!); } catch { return ""; }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return "";
            var sb = new StringBuilder();
            sb.Append("<html><head><style>");
            sb.Append("body { font-family: Segoe UI, sans-serif; color: #ddd; background: #23232d; margin: 8px; }");
            sb.Append("table { border-collapse: collapse; width: 100%; }");
            sb.Append("th { text-align: left; color: #aab; width: 30%; padding: 4px 6px; vertical-align: top; }");
            sb.Append("td { padding: 4px 6px; }");
            sb.Append("tr:nth-child(even) { background: #2a2a35; }");
            sb.Append("h3 { margin: 0 0 8px 0; }");
            sb.Append("</style></head><body>");
            sb.Append("<h3>").Append(HtmlEscape(entryFileName ?? "")).Append("</h3>");
            sb.Append("<table>");
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                string display = (prop.Value.ValueKind == JsonValueKind.Object || prop.Value.ValueKind == JsonValueKind.Array)
                    ? "<pre>" + HtmlEscape(prop.Value.ToString()) + "</pre>"
                    : HtmlEscape(ScalarText(prop.Value));
                sb.Append("<tr><th>").Append(HtmlEscape(prop.Name)).Append("</th><td>").Append(display).Append("</td></tr>");
            }
            sb.Append("</table></body></html>");
            return sb.ToString();
        }
    }

    // ── token resolution ────────────────────────────────────────────────────────

    private static string? ResolveToken(string token, Meta meta, JsonElement? root, string entryFileName)
    {
        if (string.Equals(token, "JSONDATA", StringComparison.OrdinalIgnoreCase))
        {
            if (root is JsonElement r && r.ValueKind == JsonValueKind.Object && TryNarrowJsonToEntry(r, entryFileName, out var narrowed))
                return narrowed.GetRawText();
            return meta.JsonRaw ?? "";
        }
        if (string.Equals(token, "JSONDATA_ALL", StringComparison.OrdinalIgnoreCase)) return meta.JsonRaw ?? "";
        if (string.Equals(token, "ENTRY", StringComparison.OrdinalIgnoreCase)) return HtmlEscape(entryFileName ?? "");
        if (string.Equals(token, "ARCHIVE", StringComparison.OrdinalIgnoreCase)) return HtmlEscape(meta.ArchiveBasename ?? "");
        if (root == null) return null;

        // Dotted-path access into nested objects.
        JsonElement cur = root.Value;
        foreach (var seg in token.Split('.'))
        {
            if (cur.ValueKind != JsonValueKind.Object || !TryGetPropertyCI(cur, seg, out cur)) return null;
        }
        if (cur.ValueKind == JsonValueKind.Object || cur.ValueKind == JsonValueKind.Array) return HtmlEscape(cur.GetRawText());
        return HtmlEscape(ScalarText(cur));
    }

    /// <summary>When <paramref name="root"/> is a per-rom map, return the property matching the entry
    /// (full name, then basename-without-extension, then key-basename == entry-basename), case-insensitive.</summary>
    private static bool TryNarrowJsonToEntry(JsonElement root, string entryFileName, out JsonElement value)
    {
        value = default;
        if (root.ValueKind != JsonValueKind.Object || string.IsNullOrEmpty(entryFileName)) return false;
        if (TryGetPropertyCI(root, entryFileName, out value)) return true;

        string entryNoExt; try { entryNoExt = Path.GetFileNameWithoutExtension(entryFileName); } catch { entryNoExt = entryFileName; }
        if (!string.IsNullOrEmpty(entryNoExt) && !string.Equals(entryNoExt, entryFileName, StringComparison.Ordinal)
            && TryGetPropertyCI(root, entryNoExt, out value)) return true;

        foreach (var p in root.EnumerateObject())
        {
            string keyNoExt; try { keyNoExt = Path.GetFileNameWithoutExtension(p.Name); } catch { continue; }
            if (!string.IsNullOrEmpty(keyNoExt) && string.Equals(keyNoExt, entryNoExt, StringComparison.OrdinalIgnoreCase))
            { value = p.Value; return true; }
        }
        return false;
    }

    private static bool TryGetPropertyCI(JsonElement obj, string name, out JsonElement value)
    {
        foreach (var p in obj.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) { value = p.Value; return true; }
        value = default;
        return false;
    }

    private static string ScalarText(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString() ?? "",
        JsonValueKind.Null => "",
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => e.GetRawText(),
    };

    // ── helpers ──────────────────────────────────────────────────────────────────

    private static HttpResponse Fail(string reason, int status = 200)
        => HttpResponse.Json(JsonSerializer.Serialize(new { ok = false, reason }), status);

    private static string? TryReadAllText(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch { return null; }
    }

    private static string? SafeDir(string path)
    {
        try { return string.IsNullOrEmpty(path) ? null : Path.GetDirectoryName(path); }
        catch { return null; }
    }

    private static string SanitizeFolderName(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(s.Length);
        foreach (var c in s) sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        return sb.ToString();
    }

    private static string HtmlEscape(string? s) => string.IsNullOrEmpty(s) ? "" : WebUtility.HtmlEncode(s);

    private static T? Safe<T>(Func<T> read) { try { return read(); } catch { return default; } }

    private static void Log(string msg) => LbLog.Info("web", "[archive] metadata: " + msg);
}
