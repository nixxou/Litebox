// Bridge to ExtendDB's DEDICATED image download primitive, so that when the Extended Database module is on
// LiteBox downloads web images EXACTLY the way ExtendDB's metadata-download wizard does — instead of a naive
// CDN GET that only works for launchbox-origin rows.
//
// The primitive is ExtendDB.Web.Api.MediaApi.FetchForWizard(ImageMetadata, CancellationToken): given a row's
// metadata it walks the per-origin URL chain (Launchbox CDN, Screenscraper with credentials, Steam CDN,
// the extenddb.com mirror cache, …) and returns the image bytes. It is the same code path the wizard drives
// through LaunchBox's "extenddb-…-specialext" URL interception, so origin handling, credential injection and
// mirror fallback all match.
//
// The gate is NOT "ExtendDB loaded". It is the real one ExtendDB itself uses for its extended-database feature:
//   • Modules.Active(Module.Base) — the "Extended database" module is enabled AND the welcome-screen
//     activation barrier has been passed (Active, not On), and
//   • ExtendDBPlugin.CustomDatabaseExist — the extended DB (LaunchBox.Extended.Metadata.db) is actually
//     DOWNLOADED. Without it no non-launchbox rows were ever seeded, so there is nothing that needs the
//     per-origin path.

#nullable enable

using System;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;

namespace LbApiHost.Host.Media;

internal static class MediaApiBridge
{
    private static bool _probed;
    private static Assembly? _asm;
    private static Type? _metaType;
    private static MethodInfo? _fetch;                 // MediaApi.FetchForWizard(ImageMetadata, CancellationToken)
    private static MethodInfo? _moduleActive;          // Modules.Active(Module)
    private static object? _baseModule;                // Module.Base enum value
    private static FieldInfo? _customDbExist;          // ExtendDBPlugin.CustomDatabaseExist (static bool)
    private static PropertyInfo? _pDb, _pType, _pRegion, _pCrc, _pOrigin, _pDup, _pFt, _pPlat, _pFile;
    private static MethodInfo? _listUrls;              // MediaApi.ListDirectUrls(MediaContext, string, string, long, MediaSourcePolicyTable)
    private static MethodInfo? _ctxFromType;           // MediaSourcePolicy.ContextFromType(string) → MediaContext

    private static void Probe()
    {
        if (_probed) return;
        _probed = true;
        try
        {
            _asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "ExtendDB");
            if (_asm == null) return;

            _metaType = _asm.GetType("ImageMetadata");                       // global namespace by design
            var mediaApi = _asm.GetType("ExtendDB.Web.Api.MediaApi");        // internal static — GetType still finds it
            if (_metaType != null && mediaApi != null)
                _fetch = mediaApi.GetMethod("FetchForWizard", BindingFlags.Public | BindingFlags.Static,
                    null, new[] { _metaType, typeof(CancellationToken) }, null);

            // Streaming path: the ordered per-origin URL chain, WITHOUT downloading. This is what turns a Steam
            // row whose FileName ends in ".m3u8.mp4" (a fake mp4 — 5.9k of them) into the real ".m3u8" manifest.
            var policyType = _asm.GetType("ExtendDB.Web.Backend.MediaSourcePolicy");
            _ctxFromType = policyType?.GetMethod("ContextFromType", BindingFlags.Public | BindingFlags.Static);
            _listUrls = mediaApi?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                                 .FirstOrDefault(m => m.Name == "ListDirectUrls" && m.GetParameters().Length == 5);

            if (_metaType != null)
            {
                _pDb = _metaType.GetProperty("DatabaseId"); _pType = _metaType.GetProperty("Type");
                _pRegion = _metaType.GetProperty("Region"); _pCrc = _metaType.GetProperty("CRC32");
                _pOrigin = _metaType.GetProperty("Origin"); _pDup = _metaType.GetProperty("Duplicate");
                _pFt = _metaType.GetProperty("FileType"); _pPlat = _metaType.GetProperty("Platform");
                _pFile = _metaType.GetProperty("FileName");
            }

            // Real "extended-database module active" gate: Modules.Active(Module.Base).
            var moduleEnum = _asm.GetType("ExtendDB.Module");
            var modulesType = _asm.GetType("ExtendDB.Modules");
            if (moduleEnum != null && modulesType != null)
            {
                _baseModule = Enum.Parse(moduleEnum, "Base");
                _moduleActive = modulesType.GetMethod("Active", BindingFlags.Public | BindingFlags.Static, null, new[] { moduleEnum }, null);
            }
            // "Extended DB downloaded" gate: ExtendDBPlugin.CustomDatabaseExist.
            _customDbExist = _asm.GetType("ExtendDB.ExtendDBPlugin")?.GetField("CustomDatabaseExist", BindingFlags.Public | BindingFlags.Static);
        }
        catch { }
    }

    /// <summary>ExtendDB's per-origin wizard downloader is reachable by reflection.</summary>
    public static bool Available { get { Probe(); return _fetch != null && _metaType != null; } }

    /// <summary>
    /// The Extended Database module is genuinely in play: its module is Active (enabled + past the welcome
    /// barrier) AND the extended DB has been downloaded. This is the condition under which the merged
    /// non-launchbox rows exist and the per-origin download path is required.
    /// </summary>
    public static bool ModuleActive
    {
        get
        {
            Probe();
            try
            {
                bool baseActive = _moduleActive != null && _baseModule != null
                                  && _moduleActive.Invoke(null, new[] { _baseModule }) is bool a && a;
                bool downloaded = _customDbExist != null && _customDbExist.GetValue(null) is bool d && d;
                return baseActive && downloaded;
            }
            catch { return false; }
        }
    }

    /// <summary>True when downloads/previews MUST go through ExtendDB's dedicated path (same as the DL wizard).</summary>
    public static bool UseWizardPath => Available && ModuleActive;

    /// <summary>
    /// Fetches one image's bytes THE SAME WAY the metadata-download wizard does (per-origin URL chain), via
    /// MediaApi.FetchForWizard. Returns null on any failure. Synchronous/blocking (the wizard fetch gate is);
    /// call it off the UI thread. Only meaningful when <see cref="Available"/>.
    /// </summary>
    public static byte[]? FetchBytes(MetadataDb.WebImage w, string platform)
    {
        Probe();
        if (_fetch == null || _metaType == null) return null;
        try
        {
            object meta = Activator.CreateInstance(_metaType)!;
            _pDb?.SetValue(meta, w.DatabaseId);
            _pType?.SetValue(meta, w.Type ?? "");
            _pRegion?.SetValue(meta, string.IsNullOrEmpty(w.Region) ? "none" : w.Region);
            _pCrc?.SetValue(meta, w.Crc32);
            _pOrigin?.SetValue(meta, string.IsNullOrEmpty(w.Origin) ? "launchbox" : w.Origin);
            _pDup?.SetValue(meta, w.Duplicate);
            _pFt?.SetValue(meta, string.IsNullOrEmpty(w.FileType) ? System.IO.Path.GetExtension(w.FileName) : w.FileType);
            _pPlat?.SetValue(meta, platform ?? "");
            _pFile?.SetValue(meta, w.FileName ?? "");

            using var resp = _fetch.Invoke(null, new object[] { meta, CancellationToken.None }) as HttpResponseMessage;
            if (resp == null || !resp.IsSuccessStatusCode) return null;
            return resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
        }
        catch { return null; }
    }

    /// <summary>One playable upstream: the URL to open and the Referer the CDN gates on (null when it doesn't).</summary>
    public readonly record struct UrlCandidate(string Kind, string Url, string? Referer);

    /// <summary>
    /// The ordered upstream URLs ExtendDB would try for this row, WITHOUT fetching anything — the streaming
    /// counterpart of <see cref="FetchBytes"/>, used to PLAY a web video instead of downloading it.
    /// <para>
    /// This is the only correct way to get a Steam trailer's URL: the DB stores some of them as
    /// "…/movie480.m3u8.mp4" — a fake mp4. ExtendDB's SteamCdn builder strips the trailing ".mp4" and the real
    /// resource is the HLS manifest underneath. libvlc plays HLS natively, so we can stream what LaunchBox's
    /// downloader has to skip (FetchForWizard drops HLS URLs — raw manifest bytes are useless on disk).
    /// </para>
    /// Empty when ExtendDB isn't loaded.
    /// </summary>
    public static List<UrlCandidate> ListUrls(MetadataDb.WebImage w)
    {
        Probe();
        var result = new List<UrlCandidate>();
        if (_listUrls == null || _ctxFromType == null) return result;
        try
        {
            object? ctx = _ctxFromType.Invoke(null, new object?[] { w.Type ?? "" });
            if (ctx == null) return result;

            var raw = _listUrls.Invoke(null, new object?[]
            {
                ctx,
                w.FileName ?? "",
                string.IsNullOrEmpty(w.Origin) ? "launchbox" : w.Origin,
                w.Crc32,
                null,          // policy: null → ExtendDB's live (remote-backed) table
            }) as System.Collections.IEnumerable;
            if (raw == null) return result;

            foreach (var item in raw)
            {
                var t = item.GetType();
                string url = t.GetProperty("Url")?.GetValue(item) as string ?? "";
                if (url.Length == 0) continue;
                result.Add(new UrlCandidate(
                    t.GetProperty("Kind")?.GetValue(item)?.ToString() ?? "?",
                    url,
                    t.GetProperty("Referer")?.GetValue(item) as string));
            }
        }
        catch { }
        return result;
    }
}
