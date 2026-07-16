// Native (plugin-free) downloader for the community Extended metadata database
// (LaunchBox.Extended.Metadata.db), so LiteBox can obtain and refresh it on its own.
//
// The database is published as GitHub release assets in a small, fixed convention:
//   <version>.main.dbz.zst    full snapshot, zstd-compressed raw SQLite file ("binary major")
//   <version>.main.sqlb.zst   full snapshot as a zstd-compressed SQL dump ("SQL major")
//   <version>.patch.sqlb.zst  incremental patch as a zstd-compressed SQL script ("minor")
//   <version>.{main|patch}.manifest.json   sidecar with archive_size + archive_sha256
// <version> is a compact UTC timestamp (yyyyMMddHHmmss). The restored database carries a
// `__version` table whose highest `version` row is the installed version — that is what the
// update check compares against the release (rule: up to date iff local >= newest minor,
// or newest major when no minor exists). Archives are compressed with zstd long-distance
// matching (large window), so decompression must raise ZSTD_d_windowLogMax.
//
// Install flow: download the needed archives into Core\litebox\cache\ (SHA-256 verified
// against the manifests, reused across runs), rebuild a fresh .db from the archives (never
// by mutating the live file), then swap it into place via TargetPath + ".new" → File.Move.
// If the live DB is held open by a reader the staged file is parked as TargetPath + ".todo"
// and ApplyPendingTodoIfAny() finishes the swap on the next boot, before anything opens it.
//
// Scope note vs the ExtendDB plugin's updater: we deliberately do NOT run the plugin's
// post-restore preparation (copying its ExtendDBState table, LB's __EFMigrationsHistory,
// adding `fromlb` marker columns…) — that machinery only serves the plugin's live LB↔DB
// merge, which has no LiteBox equivalent. The restored file is used read-only here.

#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LbApiHost.Host.Diag;
using LbApiHost.Host.Media;
using Microsoft.Data.Sqlite;
using ZstdSharp;
using ZstdSharp.Unsafe;

namespace LbApiHost.Host.Data;

internal static class ExtDbDownloader
{
    /// <summary>Default "owner/repo" hosting the Extended-DB release artifacts. Overridable via
    /// LiteBox.ini → [Base] UpdateRepo=owner/repo (the extended DB belongs to the Base module).</summary>
    private const string DefaultRepo = "nixxou/ExtendDB_Database";

    private const string DbFileName = "LaunchBox.Extended.Metadata.db";
    private const string TodoSuffix = ".todo";
    private const string TmpDbName = "extended.tmp.db";

    private static readonly byte[] SqliteMagic = Encoding.ASCII.GetBytes("SQLite format 3\0");

    // ── Public surface ────────────────────────────────────────────────────

    /// <summary>LiteBox's own copy of the extended DB, under Core\litebox\. This is the ONLY
    /// path this class ever writes — the legacy plugin copy is read-only fallback territory.</summary>
    public static string TargetPath => LiteBoxPaths.File(DbFileName);

    /// <summary>True when an extended DB is usable: our own copy, or the legacy plugin copy
    /// (via <see cref="MetadataDb.ExtendedDbPath"/>'s own-first/legacy-fallback probe).</summary>
    public static bool Installed => File.Exists(TargetPath) || MetadataDb.ExtendedDbPath != null;

    /// <summary>
    /// Silent update check against the GitHub release. Reads the local version from the
    /// `__version` table of the DB currently in use (own copy first, legacy plugin copy as
    /// fallback; 0 when absent) and compares it to the release's newest version (latest minor
    /// patch when present, else the major). <c>UpdateAvailable</c> follows the plugin's exact
    /// rule: an update is pending iff local &lt; target. <c>AssetBytes</c> is the total size of
    /// the archives an install would fetch (0 when up to date). Throws on network errors.
    /// </summary>
    public static async Task<(bool UpdateAvailable, string? RemoteVersion, string? LocalVersion, long AssetBytes)>
        CheckAsync(CancellationToken ct)
    {
        string cacheDir = LiteBoxPaths.Dir("cache");
        using var http = NewHttp();
        var plan = await BuildPlanAsync(http, cacheDir, ct).ConfigureAwait(false);

        string? local = plan.LocalVersion > 0
            ? plan.LocalVersion.ToString(CultureInfo.InvariantCulture) : null;
        string? remote = plan.ReleaseFound
            ? plan.TargetVersion.ToString(CultureInfo.InvariantCulture) : null;
        long bytes = (plan.Major?.ExpectedSize ?? 0) + (plan.Minor?.ExpectedSize ?? 0);
        return (!plan.UpToDate, remote, local, bytes);
    }

    /// <summary>
    /// Full update: check → download (with SHA-256 verification, cache reuse) → rebuild a fresh
    /// .db from the archives → swap into <see cref="TargetPath"/>. Returns true when the DB is
    /// up to date afterwards — including the "staged as .todo because the live file was locked"
    /// case (the swap then completes on next boot). Returns false on any failure or cancel.
    /// </summary>
    // ── Shared (single-flight) operation ──────────────────────────────────────
    // The boot auto-update and the options-panel button may both want to run the update; racing two instances
    // corrupts the .part download ("file in use"). One shared operation runs at a time; later callers JOIN it
    // (their progress sink is fanned in, they await the same task). Cancel cancels for everyone.

    private static readonly object _opLock = new();
    private static Task<bool>? _op;
    private static CancellationTokenSource? _opCts;
    private static event Action<string>? _opProgress;
    private static string _opLast = "";

    /// <summary>An update/adopt operation is currently running.</summary>
    public static bool OperationRunning { get { lock (_opLock) return _op is { IsCompleted: false }; } }

    /// <summary>The last progress line of the running (or finished) shared operation.</summary>
    public static string LastProgress { get { lock (_opLock) return _opLast; } }

    /// <summary>Starts the update (download / adopt / no-op when up to date) — or JOINS the one already
    /// running. <paramref name="onProgress"/> receives every subsequent progress line (marshal in the caller).</summary>
    public static Task<bool> RunSharedAsync(Action<string>? onProgress = null)
    {
        lock (_opLock)
        {
            if (onProgress != null) _opProgress += onProgress;
            if (_op is { IsCompleted: false }) return _op;

            _opCts = new CancellationTokenSource();
            var ct = _opCts.Token;
            var fan = new Progress<string>(m =>
            {
                lock (_opLock) _opLast = m;
                try { _opProgress?.Invoke(m); } catch { }
            });
            _op = Task.Run(async () =>
            {
                try { return await DownloadAndInstallAsync(fan, ct).ConfigureAwait(false); }
                finally { lock (_opLock) { _opProgress = null; } }   // drop sinks — next run re-subscribes
            });
            return _op;
        }
    }

    /// <summary>Cancels the running shared operation (no-op when idle).</summary>
    public static void CancelShared() { lock (_opLock) { try { _opCts?.Cancel(); } catch { } } }

    /// <summary>Copies an existing legacy (plugin) Extended-DB into LiteBox's OWN Core\litebox copy so LiteBox
    /// owns/manages it (merge, atomic swaps) without a multi-GB re-download. Reports progress; atomic swap into place.</summary>
    private static async Task<bool> AdoptLegacyAsync(string legacy, string cacheDir, IProgress<string>? progress, CancellationToken ct)
    {
        string tmp = Path.Combine(cacheDir, "adopt.tmp.db");
        try
        {
            long total = 0; try { total = new FileInfo(legacy).Length; } catch { }
            Report(progress, $"Adopting the existing database into LiteBox ({(total > 0 ? total / (1 << 20) + " MB" : "…")})...");
            TryDelete(tmp);
            await using (var src = File.OpenRead(legacy))
            await using (var dst = File.Create(tmp))
            {
                var buf = new byte[1 << 20];
                long done = 0; double lastPct = 0; int r;
                while ((r = await src.ReadAsync(buf.AsMemory(0, buf.Length), ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, r), ct).ConfigureAwait(false);
                    done += r;
                    if (total > 0) { double pct = 100.0 * done / total; if (pct - lastPct >= 5) { lastPct = pct; Report(progress, $"  adopting: {pct:F0}%"); } }
                }
            }
            bool swapped = await SwapIntoPlaceAsync(tmp, ct).ConfigureAwait(false);
            if (swapped) MetadataDb.InvalidateExtendedDbProbe();
            Report(progress, swapped ? "Database is now managed by LiteBox." : "Adopted — applies on next start (the DB is in use).");
            return true;
        }
        catch (OperationCanceledException) { TryDelete(tmp); Report(progress, "Adopt cancelled."); return false; }
        catch (Exception ex) { TryDelete(tmp); LbLog.Warn("extdb", "adopt failed: " + ex.Message); Report(progress, "Adopt failed: " + ex.Message); return false; }
    }

    public static async Task<bool> DownloadAndInstallAsync(IProgress<string>? progress, CancellationToken ct)
    {
        try
        {
            // Finish a swap deferred by an earlier run before stacking a new one on top.
            ApplyPendingTodoIfAny();

            string cacheDir = LiteBoxPaths.Dir("cache");
            using var http = NewHttp();

            Report(progress, $"Checking GitHub ({Repo})...");
            var plan = await BuildPlanAsync(http, cacheDir, ct).ConfigureAwait(false);

            if (!plan.ReleaseFound)
            {
                Report(progress, "No release found on GitHub.");
                return false;
            }
            if (plan.UpToDate)
            {
                // Up to date — but if LiteBox has no OWN copy yet and only a legacy (plugin) copy exists, ADOPT
                // it (copy into Core\litebox) so LiteBox owns and manages the DB (merge, atomic swaps) without a
                // multi-GB re-download.
                if (!File.Exists(TargetPath))
                {
                    var legacy = MetadataDb.ExtendedDbPath;   // own is absent → this resolves to the legacy copy (or null)
                    if (!string.IsNullOrEmpty(legacy) && File.Exists(legacy)
                        && !string.Equals(Path.GetFullPath(legacy!), Path.GetFullPath(TargetPath), StringComparison.OrdinalIgnoreCase))
                        return await AdoptLegacyAsync(legacy!, cacheDir, progress, ct).ConfigureAwait(false);
                }
                Report(progress, "Already up to date.");
                return true;
            }

            Report(progress, $"Update available: {plan.TargetVersion} (local: " +
                             $"{(plan.LocalVersion > 0 ? plan.LocalVersion.ToString(CultureInfo.InvariantCulture) : "none")})");

            if (plan.Major != null)
                await EnsureArchiveAsync(http, plan.Major, cacheDir, progress, ct).ConfigureAwait(false);
            if (plan.Minor != null)
                await EnsureArchiveAsync(http, plan.Minor, cacheDir, progress, ct).ConfigureAwait(false);

            // The rebuild always starts from a major archive. When only a minor was needed
            // (local already past the major), the baseline major must sit in the cache.
            string? majorPath = plan.Major != null
                ? Path.Combine(cacheDir, plan.Major.Asset.Name)
                : FindNewestCachedMajor(cacheDir);
            if (majorPath == null || !File.Exists(majorPath))
                throw new InvalidOperationException(
                    "No major archive available in the cache. Delete Core\\litebox\\cache and retry for a full re-download.");
            string? minorPath = plan.Minor != null ? Path.Combine(cacheDir, plan.Minor.Asset.Name) : null;

            string tmpDb = Path.Combine(cacheDir, TmpDbName);
            try
            {
                await RestoreArchivesAsync(majorPath, minorPath, tmpDb, progress, ct).ConfigureAwait(false);
                bool swapped = await SwapIntoPlaceAsync(tmpDb, ct).ConfigureAwait(false);
                if (swapped) MetadataDb.InvalidateExtendedDbProbe();   // make the fresh file visible without a restart
                Report(progress, swapped
                    ? "Extended database installed."
                    : "Database is in use - update staged, it will be applied on next start.");
            }
            catch
            {
                TryDelete(tmpDb);
                throw;
            }

            CleanupCache(cacheDir, Path.GetFileName(majorPath),
                minorPath != null ? Path.GetFileName(minorPath) : null);
            return true;
        }
        catch (OperationCanceledException)
        {
            Report(progress, "Update cancelled.");
            return false;
        }
        catch (Exception ex)
        {
            LbLog.Warn("extdb", "update failed: " + ex.Message);
            progress?.Report("Update failed: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Completes a deferred swap: when a <c>TargetPath + ".todo"</c> from a previous run exists,
    /// moves it over <see cref="TargetPath"/>. Must be called at boot BEFORE anything opens a
    /// SQLite connection on the extended DB. Safe no-op otherwise. A suspiciously tiny .todo
    /// (interrupted copy) is discarded instead of installed.
    /// </summary>
    public static void ApplyPendingTodoIfAny()
    {
        try
        {
            string todo = TargetPath + TodoSuffix;
            if (!File.Exists(todo)) return;

            long size = 0;
            try { size = new FileInfo(todo).Length; } catch { }
            if (size < 1024)
            {
                LbLog.Info("extdb", $"discarding suspicious {Path.GetFileName(todo)} ({size} bytes)");
                TryDelete(todo);
                return;
            }

            LbLog.Info("extdb", $"pending update detected ({size} bytes), applying...");
            for (int attempt = 1; attempt <= 5; attempt++)
            {
                try
                {
                    File.Move(todo, TargetPath, overwrite: true);
                    MetadataDb.InvalidateExtendedDbProbe();
                    LbLog.Info("extdb", "pending update applied.");
                    return;
                }
                catch (IOException)
                {
                    if (attempt < 5) Thread.Sleep(1000);
                }
            }
            LbLog.Warn("extdb", "pending update could not be applied (target busy); kept for next boot.");
        }
        catch (Exception ex)
        {
            LbLog.Warn("extdb", "ApplyPendingTodoIfAny error: " + ex.Message);
        }
    }

    // ── Release plan (what to fetch, what to apply) ───────────────────────

    private enum ArchiveKind { Major, Minor }

    private sealed record ParsedName(long Version, ArchiveKind Kind, bool Binary);

    private sealed record Asset(string Name, long Size, string Url);

    private sealed record ArchiveStep(Asset Asset, ParsedName Parsed, long ExpectedSize, string Sha256);

    private sealed class Plan
    {
        public bool ReleaseFound;
        public bool UpToDate;
        public long LocalVersion;
        public long TargetVersion;
        public ArchiveStep? Major;   // null = not needed (baseline already cached)
        public ArchiveStep? Minor;   // null = release has no applicable patch
    }

    private static string Repo
    {
        get
        {
            try
            {
                var cfg = LiteBoxConfig.LoadForExe();
                string r = (cfg.GetSec("Base", "UpdateRepo", DefaultRepo) ?? DefaultRepo).Trim();
                if (r.Contains('/')) return r;
            }
            catch { }
            return DefaultRepo;
        }
    }

    private static async Task<Plan> BuildPlanAsync(HttpClient http, string cacheDir, CancellationToken ct)
    {
        var plan = new Plan { LocalVersion = ReadLocalVersion() };

        var (found, assets) = await FetchLatestReleaseAssetsAsync(http, Repo, ct).ConfigureAwait(false);
        if (!found)
        {
            // Mirror the reference behavior: with no release to compare against, "up to date"
            // means "we at least have a local DB".
            plan.UpToDate = plan.LocalVersion > 0;
            return plan;
        }
        plan.ReleaseFound = true;

        var majorAsset = assets.FirstOrDefault(a =>
                a.Name.Contains(".main.", StringComparison.OrdinalIgnoreCase) &&
                a.Name.EndsWith(".zst", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Release contains no major archive (*.main.*.zst).");
        var major = ParseName(majorAsset.Name)
            ?? throw new InvalidOperationException($"Unrecognized archive name: {majorAsset.Name}");

        var minorPick = assets
            .Where(a => a.Name.EndsWith(".patch.sqlb.zst", StringComparison.OrdinalIgnoreCase))
            .Select(a => (Asset: a, Parsed: ParseName(a.Name)))
            .Where(x => x.Parsed != null)
            .OrderByDescending(x => x.Parsed!.Version)
            .FirstOrDefault();

        plan.TargetVersion = minorPick.Parsed?.Version ?? major.Version;
        if (plan.LocalVersion >= plan.TargetVersion)
        {
            plan.UpToDate = true;
            return plan;
        }

        // The major is needed when the local DB predates it, or when it is absent from the
        // cache (a minor alone cannot be applied without its baseline).
        bool needMajor = plan.LocalVersion < major.Version
                      || !File.Exists(Path.Combine(cacheDir, majorAsset.Name));
        if (needMajor)
        {
            var (size, sha) = await FetchManifestAsync(http, assets, major.Version, ArchiveKind.Major, ct)
                .ConfigureAwait(false);
            plan.Major = new ArchiveStep(majorAsset, major, size > 0 ? size : majorAsset.Size, sha);
        }

        if (minorPick.Parsed != null
            && minorPick.Parsed.Version > plan.LocalVersion
            && minorPick.Parsed.Version > major.Version)
        {
            var (size, sha) = await FetchManifestAsync(http, assets, minorPick.Parsed.Version, ArchiveKind.Minor, ct)
                .ConfigureAwait(false);
            plan.Minor = new ArchiveStep(minorPick.Asset, minorPick.Parsed, size > 0 ? size : minorPick.Asset.Size, sha);
        }

        return plan;
    }

    /// <summary>Parses the release-asset naming convention; null for anything unrecognized.</summary>
    private static ParsedName? ParseName(string filename)
    {
        string n = Path.GetFileName(filename);
        string[] p = n.Split('.');
        if (p.Length != 4 || !string.Equals(p[3], "zst", StringComparison.OrdinalIgnoreCase)) return null;
        if (!long.TryParse(p[0], NumberStyles.None, CultureInfo.InvariantCulture, out long v)) return null;
        if (p[1] == "main" && p[2] == "dbz") return new ParsedName(v, ArchiveKind.Major, Binary: true);
        if (p[1] == "main" && p[2] == "sqlb") return new ParsedName(v, ArchiveKind.Major, Binary: false);
        if (p[1] == "patch" && p[2] == "sqlb") return new ParsedName(v, ArchiveKind.Minor, Binary: false);
        return null;
    }

    // ── Local version ─────────────────────────────────────────────────────

    /// <summary>
    /// Highest `version` row of the `__version` table in the extended DB currently in use
    /// (own copy preferred, legacy plugin copy otherwise), or 0 when there is no DB / no table.
    /// </summary>
    private static long ReadLocalVersion()
    {
        string? path = File.Exists(TargetPath) ? TargetPath : MetadataDb.ExtendedDbPath;
        if (path == null || !File.Exists(path)) return 0;
        try
        {
            using var conn = OpenSqlite(path, readOnly: true);
            using (var check = conn.CreateCommand())
            {
                check.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='__version' LIMIT 1";
                if (check.ExecuteScalar() == null) return 0;
            }
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT version FROM \"__version\" ORDER BY version DESC LIMIT 1";
            object? v = cmd.ExecuteScalar();
            return v is long l ? l : (v == null ? 0 : Convert.ToInt64(v, CultureInfo.InvariantCulture));
        }
        catch (Exception ex)
        {
            LbLog.Warn("extdb", "local version read failed: " + ex.Message);
            return 0;
        }
    }

    private static SqliteConnection OpenSqlite(string path, bool readOnly)
    {
        try { SQLitePCL.Batteries_V2.Init(); } catch { /* may already be initialised */ }
        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false, // no pool → no lingering handle to block the file swap
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
        };
        var conn = new SqliteConnection(csb.ToString());
        conn.Open();
        return conn;
    }

    // ── GitHub API ────────────────────────────────────────────────────────

    private static HttpClient NewHttp()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("LiteBox", "1.0"));
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        c.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return c;
    }

    private static async Task<(bool Found, List<Asset> Assets)> FetchLatestReleaseAssetsAsync(
        HttpClient http, string repo, CancellationToken ct)
    {
        using var resp = await http.GetAsync(
            $"https://api.github.com/repos/{repo}/releases/latest", ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound) return (false, new List<Asset>());
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"GitHub API returned {(int)resp.StatusCode} for {repo}/releases/latest.");

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var assets = new List<Asset>();
        if (doc.RootElement.TryGetProperty("assets", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in arr.EnumerateArray())
            {
                string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                long size = a.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
                string? url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(url))
                    assets.Add(new Asset(name, size, url));
            }
        }
        return (true, assets);
    }

    private static async Task<(long Size, string Sha256)> FetchManifestAsync(
        HttpClient http, List<Asset> assets, long version, ArchiveKind kind, CancellationToken ct)
    {
        string manifestName = kind == ArchiveKind.Major
            ? $"{version}.main.manifest.json"
            : $"{version}.patch.manifest.json";
        var asset = assets.FirstOrDefault(a =>
                string.Equals(a.Name, manifestName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Release is missing {manifestName}.");

        using var doc = JsonDocument.Parse(await http.GetStringAsync(asset.Url, ct).ConfigureAwait(false));
        long size = doc.RootElement.TryGetProperty("archive_size", out var s) && s.TryGetInt64(out long sz) ? sz : 0;
        string sha = doc.RootElement.TryGetProperty("archive_sha256", out var h) ? h.GetString() ?? "" : "";
        return (size, sha);
    }

    // ── Download (cache + SHA-256 verification) ───────────────────────────

    private static async Task EnsureArchiveAsync(
        HttpClient http, ArchiveStep step, string cacheDir, IProgress<string>? progress, CancellationToken ct)
    {
        string path = Path.Combine(cacheDir, step.Asset.Name);

        if (File.Exists(path))
        {
            if (new FileInfo(path).Length == step.ExpectedSize
                && HashMatches(await Sha256OfAsync(path, ct).ConfigureAwait(false), step.Sha256))
            {
                Report(progress, $"Cached: {step.Asset.Name}");
                return;
            }
            TryDelete(path); // stale or corrupt — re-download
        }

        Report(progress, $"Downloading {step.Asset.Name} ({FormatSize(step.ExpectedSize)})...");
        string part = path + ".part";
        TryDelete(part);

        using (var req = new HttpRequestMessage(HttpMethod.Get, step.Asset.Url))
        using (var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException($"Download of {step.Asset.Name} failed: {(int)resp.StatusCode}.");

            long total = resp.Content.Headers.ContentLength ?? step.ExpectedSize;
            double lastPct = 0;
            var buffer = new byte[1 << 20];
            long done = 0;

            await using var input = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var output = File.Create(part);
            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                done += read;
                if (total > 0)
                {
                    double pct = 100.0 * done / total;
                    if (pct - lastPct >= 5.0) // throttle: one report per 5%
                    {
                        lastPct = pct;
                        Report(progress, $"  {step.Asset.Name}: {pct:F0}%");
                    }
                }
            }
        }

        if (step.ExpectedSize > 0 && new FileInfo(part).Length != step.ExpectedSize)
        {
            TryDelete(part);
            throw new InvalidDataException($"Size mismatch for {step.Asset.Name} - download discarded.");
        }
        string actualSha = await Sha256OfAsync(part, ct).ConfigureAwait(false);
        if (!HashMatches(actualSha, step.Sha256))
        {
            TryDelete(part);
            throw new InvalidDataException($"Checksum mismatch for {step.Asset.Name} - download discarded.");
        }

        File.Move(part, path, overwrite: true);
        Report(progress, $"  {step.Asset.Name}: verified.");
    }

    private static bool HashMatches(string actual, string expected) =>
        string.IsNullOrEmpty(expected) // manifests always carry a hash; empty = nothing to check against
        || string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

    private static async Task<string> Sha256OfAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        byte[] hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    // ── Restore (zstd → SQLite) ───────────────────────────────────────────

    private static async Task RestoreArchivesAsync(
        string majorPath, string? minorPath, string tmpDb, IProgress<string>? progress, CancellationToken ct)
    {
        var parsed = ParseName(majorPath)
            ?? throw new InvalidOperationException($"Unrecognized major archive: {Path.GetFileName(majorPath)}");

        Report(progress, "Rebuilding database from archive (this can take a while)...");
        if (parsed.Binary)
            await DecompressBinaryDbAsync(majorPath, tmpDb, ct).ConfigureAwait(false);
        else
            await ApplySqlArchiveAsync(majorPath, tmpDb, freshTarget: true, ct).ConfigureAwait(false);

        if (minorPath != null)
        {
            Report(progress, "Applying incremental patch...");
            await ApplySqlArchiveAsync(minorPath, tmpDb, freshTarget: false, ct).ConfigureAwait(false);
        }
    }

    /// <summary>zstd decompressor tuned for the release archives: they are produced with
    /// long-distance matching and a large window, which a default decoder refuses.</summary>
    private static DecompressionStream OpenDecompressor(Stream input)
    {
        var s = new DecompressionStream(input);
        s.SetParameter(ZSTD_dParameter.ZSTD_d_windowLogMax, 31);
        return s;
    }

    /// <summary>Streams a binary major (`.main.dbz.zst`) out to a raw .db file, validating the
    /// SQLite file magic on the first bytes.</summary>
    private static async Task DecompressBinaryDbAsync(string archivePath, string outPath, CancellationToken ct)
    {
        if (File.Exists(outPath)) File.Delete(outPath);

        await using var fileIn = File.OpenRead(archivePath);
        await using var zstd = OpenDecompressor(fileIn);
        await using var fileOut = File.Create(outPath);

        var header = new byte[16];
        int got = 0;
        while (got < header.Length)
        {
            int n = await zstd.ReadAsync(header.AsMemory(got, header.Length - got), ct).ConfigureAwait(false);
            if (n == 0) break;
            got += n;
        }
        if (got != header.Length || !header.AsSpan().SequenceEqual(SqliteMagic))
            throw new InvalidDataException("Decompressed archive is not a SQLite database (bad header).");

        await fileOut.WriteAsync(header.AsMemory(0, got), ct).ConfigureAwait(false);
        var buffer = new byte[1 << 20];
        int read;
        while ((read = await zstd.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
            await fileOut.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Executes a zstd-compressed SQL script (`.sqlb.zst`) against <paramref name="dbPath"/>:
    /// a fresh database for a SQL major, the just-restored baseline for a patch. Uses the same
    /// bulk-load pragmas as the reference restore so the resulting file behaves identically.
    /// </summary>
    private static async Task ApplySqlArchiveAsync(
        string archivePath, string dbPath, bool freshTarget, CancellationToken ct)
    {
        if (freshTarget)
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
        else if (!File.Exists(dbPath))
        {
            throw new FileNotFoundException("Patch baseline database is missing.", dbPath);
        }

        await using var fileIn = File.OpenRead(archivePath);
        await using var zstd = OpenDecompressor(fileIn);
        using var reader = new StreamReader(zstd, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false,
            bufferSize: 1 << 20);

        using var conn = OpenSqlite(dbPath, readOnly: false);

        foreach (var pragma in new[]
        {
            "PRAGMA journal_mode = OFF", "PRAGMA synchronous = OFF", "PRAGMA temp_store = MEMORY",
            "PRAGMA cache_size = -131072", "PRAGMA locking_mode = EXCLUSIVE",
        })
            await ExecAsync(conn, pragma, ct).ConfigureAwait(false);

        using (var cmd = conn.CreateCommand())
        {
            foreach (string raw in SplitSqlStatements(reader))
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(raw)) continue;
                // Defensive: a raw NUL inside a statement would truncate it at the SQLite
                // layer; re-express it as a char(0) concatenation (dumps normally pre-encode
                // NULs this way already, so this almost never fires).
                cmd.CommandText = raw.IndexOf('\0') >= 0 ? raw.Replace("\0", "' || char(0) || '") : raw;
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }

        foreach (var pragma in new[]
        {
            "PRAGMA locking_mode = NORMAL", "PRAGMA journal_mode = DELETE",
            "PRAGMA synchronous = NORMAL", "ANALYZE",
        })
            await ExecAsync(conn, pragma, ct).ConfigureAwait(false);
    }

    private static async Task ExecAsync(SqliteConnection conn, string sql, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Splits a SQL script into executable statements. Semicolons are statement terminators
    /// only when outside of: 'strings' (with '' escapes), "quoted"/`backtick`/[bracket]
    /// identifiers, -- line and /* block */ comments, CREATE TRIGGER … BEGIN…END bodies, and
    /// CASE…END expressions.
    /// </summary>
    private static IEnumerable<string> SplitSqlStatements(TextReader reader)
    {
        var stmt = new StringBuilder(8192);
        var word = new StringBuilder(16);

        bool sawCreate = false, isTrigger = false, inTriggerBody = false;
        int caseDepth = 0;

        void FlushWord()
        {
            if (word.Length == 0) return;
            string w = word.ToString();
            word.Clear();
            if (w.Equals("CREATE", StringComparison.OrdinalIgnoreCase)) sawCreate = true;
            else if (w.Equals("TRIGGER", StringComparison.OrdinalIgnoreCase)) { if (sawCreate) isTrigger = true; }
            else if (w.Equals("BEGIN", StringComparison.OrdinalIgnoreCase)) { if (isTrigger) inTriggerBody = true; }
            else if (w.Equals("CASE", StringComparison.OrdinalIgnoreCase)) caseDepth++;
            else if (w.Equals("END", StringComparison.OrdinalIgnoreCase))
            {
                if (caseDepth > 0) caseDepth--;
                else if (inTriggerBody) inTriggerBody = false;
            }
        }

        const int ModeCode = 0, ModeString = 1, ModeQuotedId = 2, ModeBacktickId = 3,
                  ModeBracketId = 4, ModeLineComment = 5, ModeBlockComment = 6;
        int mode = ModeCode;

        int ci;
        while ((ci = reader.Read()) >= 0)
        {
            char c = (char)ci;
            switch (mode)
            {
                case ModeLineComment:
                    stmt.Append(c);
                    if (c == '\n') mode = ModeCode;
                    break;

                case ModeBlockComment:
                    stmt.Append(c);
                    if (c == '*' && reader.Peek() == '/')
                    {
                        stmt.Append((char)reader.Read());
                        mode = ModeCode;
                    }
                    break;

                case ModeString:
                    stmt.Append(c);
                    if (c == '\'')
                    {
                        if (reader.Peek() == '\'') stmt.Append((char)reader.Read()); // '' escape
                        else mode = ModeCode;
                    }
                    break;

                case ModeQuotedId:
                    stmt.Append(c);
                    if (c == '"')
                    {
                        if (reader.Peek() == '"') stmt.Append((char)reader.Read()); // "" escape
                        else mode = ModeCode;
                    }
                    break;

                case ModeBacktickId:
                    stmt.Append(c);
                    if (c == '`') mode = ModeCode;
                    break;

                case ModeBracketId:
                    stmt.Append(c);
                    if (c == ']') mode = ModeCode;
                    break;

                default: // ModeCode
                    bool isWordChar = c == '_' || char.IsAsciiLetterOrDigit(c);
                    if (!isWordChar) FlushWord();
                    if (isWordChar)
                    {
                        word.Append(c);
                        stmt.Append(c);
                        break;
                    }
                    switch (c)
                    {
                        case '\'': stmt.Append(c); mode = ModeString; break;
                        case '"': stmt.Append(c); mode = ModeQuotedId; break;
                        case '`': stmt.Append(c); mode = ModeBacktickId; break;
                        case '[': stmt.Append(c); mode = ModeBracketId; break;
                        case '-':
                            stmt.Append(c);
                            if (reader.Peek() == '-')
                            {
                                stmt.Append((char)reader.Read());
                                mode = ModeLineComment;
                            }
                            break;
                        case '/':
                            stmt.Append(c);
                            if (reader.Peek() == '*')
                            {
                                stmt.Append((char)reader.Read());
                                mode = ModeBlockComment;
                            }
                            break;
                        case ';':
                            stmt.Append(c);
                            if (!inTriggerBody && caseDepth == 0)
                            {
                                string s = stmt.ToString();
                                stmt.Clear();
                                sawCreate = false;
                                isTrigger = false;
                                yield return s;
                            }
                            break;
                        default:
                            stmt.Append(c);
                            break;
                    }
                    break;
            }
        }

        if (mode == ModeCode) FlushWord();
        if (stmt.Length > 0)
        {
            string tail = stmt.ToString();
            if (!string.IsNullOrWhiteSpace(tail)) yield return tail;
        }
    }

    // ── Swap into place ───────────────────────────────────────────────────

    /// <summary>
    /// Moves the freshly rebuilt DB into <see cref="TargetPath"/>: stage as ".new" (same
    /// volume → the final rename is atomic), then a few overwrite-move attempts. When the
    /// live file stays locked, the staged copy is parked as ".todo" for the next boot.
    /// Returns true when the live file was replaced now.
    /// </summary>
    private static async Task<bool> SwapIntoPlaceAsync(string tmpDb, CancellationToken ct)
    {
        string staged = TargetPath + ".new";
        TryDelete(staged);
        File.Move(tmpDb, staged);

        for (int attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                File.Move(staged, TargetPath, overwrite: true);
                return true;
            }
            catch (IOException ex)
            {
                LbLog.Info("extdb", $"swap attempt {attempt}/5 failed: {ex.Message}");
                if (attempt < 5) await Task.Delay(1000, ct).ConfigureAwait(false);
            }
        }

        string todo = TargetPath + TodoSuffix;
        TryDelete(todo);
        File.Move(staged, todo);
        LbLog.Info("extdb", "target locked; update parked as .todo for next boot.");
        return false;
    }

    // ── Cache helpers ─────────────────────────────────────────────────────

    /// <summary>Newest major archive present in the cache (by the version encoded in its
    /// filename), or null. Used as the patch baseline when only a minor had to be fetched.</summary>
    private static string? FindNewestCachedMajor(string cacheDir)
    {
        string? best = null;
        long bestVersion = -1;
        try
        {
            foreach (var f in Directory.EnumerateFiles(cacheDir, "*.zst", SearchOption.TopDirectoryOnly))
            {
                var p = ParseName(f);
                if (p == null || p.Kind != ArchiveKind.Major) continue;
                if (p.Version > bestVersion) { bestVersion = p.Version; best = f; }
            }
        }
        catch { }
        return best;
    }

    /// <summary>Drops cached archives (and stray .part files) other than the ones just applied,
    /// so the cache holds at most one baseline + one patch.</summary>
    private static void CleanupCache(string cacheDir, params string?[] keepNames)
    {
        var keep = new HashSet<string>(keepNames.Where(n => n != null)!, StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var f in Directory.EnumerateFiles(cacheDir, "*.zst", SearchOption.TopDirectoryOnly))
                if (!keep.Contains(Path.GetFileName(f)))
                    TryDelete(f);
            foreach (var f in Directory.EnumerateFiles(cacheDir, "*.part", SearchOption.TopDirectoryOnly))
                TryDelete(f);
        }
        catch { }
    }

    // ── Misc ──────────────────────────────────────────────────────────────

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static string FormatSize(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):F1} GB"
        : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):F0} MB"
        : $"{Math.Max(1, bytes / 1024)} KB";

    private static void Report(IProgress<string>? progress, string message)
    {
        LbLog.Info("extdb", message);
        progress?.Report(message);
    }
}
