// ─────────────────────────────────────────────────────────────────────────────
// AI / agent context — read this before touching the file
// ─────────────────────────────────────────────────────────────────────────────
//
// Purpose
//   Fast file enumeration helpers for the plugin. Wraps voidtools'
//   <c>Everything64.dll</c> (NTFS USN-based search) for instant
//   directory walks, with a pure-.NET fallback for when Everything
//   isn't available.
//
//   Why bother? GameCache walks the entire image folder hierarchy
//   (often 10s of thousands of files) on every rebuild. Doing this
//   with Directory.EnumerateFiles takes minutes on cold disk;
//   Everything returns the same listing in milliseconds because it
//   reads from a pre-indexed, RAM-resident database maintained by
//   the Everything service.
//
// File contents (5 types)
//   • FileInfoResult           — DTO: full path + size + dir.
//   • FileInfoResultExtended   — DTO: + date modified.
//   • ImageFileInfo            — DTO with CRC field. CURRENTLY UNUSED
//                                in the project; kept for symmetry
//                                with future image-related batch APIs.
//   • EverythingSdk            — P/Invoke layer over Everything64.dll.
//                                Internal — only EverythingBridge
//                                consumes it.
//   • EverythingBridge         — high-level API: availability check
//                                (memoized via Volatile read), three
//                                "GetFiles*" entry points with
//                                progressively richer info.
//   • FileSystemFallback       — pure-.NET fallback. CURRENTLY UNUSED
//                                — GameCache rolls its own fallback
//                                inline. Kept here for future code
//                                paths that want a turnkey fallback.
//
// Everything availability detection
//   <see cref="EverythingBridge.IsEverythingAvailable"/> performs
//   exactly ONE check at first call (atomic via Interlocked-style
//   Volatile read/write on _availabilityChecked) and memoizes the
//   result. Two failure modes both return false:
//     1. The DLL file isn't present at <c>ThirdParty\Everything\Everything64.dll</c>.
//     2. The DLL is loadable but the Everything DB isn't loaded
//        (Everything service not running, or still indexing).
//   Callers should treat the result as a one-shot decision: if
//   Everything is available at boot, use it for the rest of the
//   process; if not, fall back permanently. Re-checking would just
//   add overhead.
//
// SDK request flags (raw constants used in this file)
//   • 0x00000001 EVERYTHING_REQUEST_FILE_NAME
//   • 0x00000002 EVERYTHING_REQUEST_PATH
//   • 0x00000003 = file name + path (the constant defined here)
//   • 0x00000010 EVERYTHING_REQUEST_SIZE — used inline in
//     GetFilesWithInfo and GetFilesWithInfoExtended.
//   • 0x00000040 EVERYTHING_REQUEST_DATE_MODIFIED — used inline in
//     GetFilesWithInfoExtended.
//   The hex values are hardcoded at call sites rather than named
//   constants — minor consistency nit, not worth changing.
//
// Query format
//   <see cref="EverythingBridge.BuildQuery"/> produces strings of the form:
//     file: path:"&lt;dir&gt;\" &lt;pattern&gt;
//   The trailing backslash on the path is required — otherwise
//   Everything matches paths starting with the prefix (e.g.
//   "C:\Games" would also match "C:\GamesArchive\..."). The
//   "file:" prefix tells Everything to skip directory results.
//
// Critical invariants for editors
//   • Lives in `namespace ExtendDB.Utility`. Was at the global scope
//     historically; aligned now with the rest of the utility folder.
//     The single consumer (GameCache.cs) already has
//     `using ExtendDB.Utility;` so no caller change was needed.
//   • The DLL path is RELATIVE: <c>ThirdParty\Everything\Everything64.dll</c>.
//     Resolved against the process's working directory. LB sets that
//     to its install folder, so the relative path resolves to
//     <c>&lt;LB&gt;\ThirdParty\Everything\Everything64.dll</c>. If you
//     ever change the deployment layout, update the const.
//   • The P/Invoke signatures must match Everything64.dll's exports
//     EXACTLY. Charset = Unicode for the string-handling functions.
//     Don't add overloads or change return types without consulting
//     the Everything SDK headers.
//   • <see cref="EverythingBridge.GetFiles"/> uses a single
//     1024-character StringBuilder reused across iterations; this is
//     intentional. Don't allocate per-iteration without a benchmark.
//   • The fallback path (FileSystemFallback) is currently dead code.
//     It also contains a known double-enumeration bug in
//     <see cref="FileSystemFallback.EnumerateFilesParallel"/> — the
//     while loop and the Parallel.ForEach process the same dirs.
//     Don't wire it up in production without fixing that bug first.
//
// Files that depend on this one
//   • Cache/GameCache — only consumer. Calls IsEverythingAvailable
//     once at the start of each rebuild phase and routes through
//     either GetFilesWithInfo or its own inline fallback.
//
// External dependencies in this file
//   • Everything64.dll (voidtools, ships with the plugin under
//     ThirdParty\Everything\).
//
// ─────────────────────────────────────────────────────────────────────────────
// NOTE (for humans, short)
// ─────────────────────────────────────────────────────────────────────────────
//
// Fast file enumeration via Everything64.dll P/Invoke. EverythingBridge
// is the high-level entry point with three GetFiles* flavors (basic,
// + size, + size + date). FileSystemFallback is a pure-.NET fallback
// kept for future use but currently dead code (and contains a known
// double-enumeration bug). Used exclusively by GameCache. ImageFileInfo
// is also kept-but-unused.
//
// ─────────────────────────────────────────────────────────────────────────────

using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace LbApiHost.Host.Gc
{
    /// <summary>
    /// Lightweight file metadata DTO: full path, size, and parent
    /// directory. Returned by <see cref="EverythingBridge.GetFilesWithInfo"/>
    /// and <see cref="FileSystemFallback.EnumerateFilesWithInfo"/>.
    /// </summary>
    public class FileInfoResult
    {
        /// <summary>Absolute path of the file.</summary>
        public string FullPath { get; set; }

        /// <summary>File size in bytes.</summary>
        public long FileSize { get; set; }

        /// <summary>Parent directory path (Path.GetDirectoryName(FullPath)).</summary>
        public string DirectoryPath { get; set; }
    }

    /// <summary>
    /// Like <see cref="FileInfoResult"/> with the addition of the file's
    /// last-write timestamp. Returned by
    /// <see cref="EverythingBridge.GetFilesWithInfoExtended"/>.
    /// </summary>
    public class FileInfoResultExtended
    {
        /// <summary>Absolute path of the file.</summary>
        public string FullPath { get; set; }

        /// <summary>File size in bytes.</summary>
        public long FileSize { get; set; }

        /// <summary>Parent directory path.</summary>
        public string DirectoryPath { get; set; }

        /// <summary>Last-write timestamp (UTC). MinValue when missing.</summary>
        public DateTime DateModified { get; set; } = DateTime.MinValue;
    }

    /// <summary>
    /// Image-oriented file DTO with a CRC slot. CURRENTLY UNUSED in
    /// the project — kept on purpose for future image-batch APIs that
    /// want to combine enumeration with CRC checking.
    /// </summary>
    public class ImageFileInfo
    {
        /// <summary>Absolute path of the image file.</summary>
        public string FullPath { get; set; }

        /// <summary>Bare filename (no directory).</summary>
        public string FileName { get; set; }

        /// <summary>File size in bytes.</summary>
        public long FileSize { get; set; }

        /// <summary>CRC32, or -1 if not yet computed.</summary>
        public long Crc { get; set; } = -1;
    }


    /// <summary>
    /// P/Invoke wrappers for the Everything search engine
    /// (voidtools). Internal helper; consumers should use
    /// <see cref="EverythingBridge"/> rather than calling this
    /// directly.
    /// </summary>
    internal static class EverythingSdk
    {
        public const string DllName = @"ThirdParty\Everything\Everything64.dll";

        private const uint EVERYTHING_REQUEST_FILE_NAME = 0x00000001;
        private const uint EVERYTHING_REQUEST_PATH = 0x00000002;
        private const uint EVERYTHING_REQUEST_FULL_PATH_AND_FILE_NAME =
            EVERYTHING_REQUEST_FILE_NAME | EVERYTHING_REQUEST_PATH;

        [DllImport(DllName, CharSet = CharSet.Unicode)]
        public static extern void Everything_SetSearch(string lpSearchString);

        [DllImport(DllName)]
        public static extern void Everything_SetRequestFlags(uint dwRequestFlags);

        [DllImport(DllName)]
        public static extern bool Everything_Query(bool bWait);

        [DllImport(DllName)]
        public static extern uint Everything_GetNumResults();

        [DllImport(DllName, CharSet = CharSet.Unicode)]
        public static extern void Everything_GetResultFullPathName(
            uint nIndex,
            StringBuilder lpString,
            uint nMaxCount
        );

        [DllImport(DllName, CharSet = CharSet.Unicode)]
        public static extern IntPtr Everything_GetResultPath(uint nIndex);

        [DllImport(DllName, CharSet = CharSet.Unicode)]
        public static extern IntPtr Everything_GetResultFileName(uint nIndex);

        [DllImport(EverythingSdk.DllName)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool Everything_IsDBLoaded();

        [DllImport(DllName)]
        public static extern bool Everything_GetResultSize(uint nIndex, out long lpFileSize);

        [DllImport(DllName, CharSet = CharSet.Unicode)]
        public static extern bool Everything_GetResultDateModified(
        uint index,
        out long dateModified);

    }
    /// <summary>
    /// High-level entry point for fast file enumeration via Everything.
    /// Use <see cref="IsEverythingAvailable"/> to detect whether
    /// Everything can serve the request, then call one of the
    /// GetFiles* methods. See file header for the complete contract.
    /// </summary>
    public static class EverythingBridge
    {
        /// <summary>
        /// Memoization flag for <see cref="IsEverythingAvailable"/>.
        /// 0 = not checked yet, 1 = checked. Read/written via Volatile.
        /// </summary>
        private static int _availabilityChecked = 0;

        /// <summary>The cached result of the availability check.</summary>
        private static bool _isEverythingAvailable;

        /// <summary>
        /// Returns true if the Everything DLL is loadable AND the
        /// Everything DB is loaded (service running, indexing
        /// complete). Result is computed exactly ONCE on first call
        /// and cached for the rest of the process lifetime.
        /// </summary>
        public static bool IsEverythingAvailable()
        {
            if (Volatile.Read(ref _availabilityChecked) == 1)
                return _isEverythingAvailable;

            bool available = CheckEverythingAvailability();

            _isEverythingAvailable = available;
            Volatile.Write(ref _availabilityChecked, 1);

            return available;
        }

        /// <summary>
        /// One-shot availability check. Two failure modes both yield
        /// false: missing DLL on disk, or DLL loadable but DB not yet
        /// loaded by the Everything service.
        /// </summary>
        private static bool CheckEverythingAvailability()
        {
            if (!File.Exists(EverythingSdk.DllName))
                return false;

            try
            {
                return EverythingSdk.Everything_IsDBLoaded();
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Returns the absolute paths of all files matching
        /// <paramref name="searchPattern"/> within
        /// <paramref name="path"/>. Recursive (Everything searches
        /// the full sub-tree under the path). Returns an empty array
        /// on query failure or when no files match.
        /// </summary>
        public static string[] GetFiles(
            string path,
            string searchPattern
        )
        {
            var query = BuildQuery(path, searchPattern);

            EverythingSdk.Everything_SetSearch(query);
            EverythingSdk.Everything_SetRequestFlags(
                0x00000003
            );


            if (!EverythingSdk.Everything_Query(true))
                return Array.Empty<string>();

            uint count = EverythingSdk.Everything_GetNumResults();
            var results = new string[count];

            var sb = new StringBuilder(1024);

            for (uint i = 0; i < count; i++)
            {
                sb.Clear();
                EverythingSdk.Everything_GetResultFullPathName(
                    i,
                    sb,
                    (uint)sb.Capacity
                );

                results[i] = sb.ToString();
            }

            return results;

        }

        /// <summary>
        /// Builds an Everything query string of the form
        /// <c>file: path:"&lt;dir&gt;\" &lt;pattern&gt;</c>. The
        /// trailing backslash is required to anchor the path match
        /// (otherwise Everything also matches sibling paths starting
        /// with the same prefix). The "file:" prefix excludes
        /// directory results.
        /// </summary>
        private static string BuildQuery(string path, string pattern)
        {
            // Make sure the path ends with a backslash.
            if (!path.EndsWith("\\"))
                path += "\\";

            var query = $"file: path:\"{path}\"";

            if (pattern != "*" && !string.IsNullOrEmpty(pattern))
            {
                query += $" {pattern}";
            }

            return query;
        }

        /// <summary>
        /// Returns <see cref="FileInfoResult"/> entries (path + size +
        /// directory) for every file matching <paramref name="searchPattern"/>
        /// under <paramref name="path"/>. Recursive.
        /// </summary>
        public static FileInfoResult[] GetFilesWithInfo(string path, string searchPattern = "*.*")
        {
            var query = BuildQuery(path, searchPattern);

            EverythingSdk.Everything_SetSearch(query);
            EverythingSdk.Everything_SetRequestFlags(0x00000003 | 0x00000010); // + SIZE

            if (!EverythingSdk.Everything_Query(true))
                return Array.Empty<FileInfoResult>();

            uint count = EverythingSdk.Everything_GetNumResults();
            var results = new FileInfoResult[count];

            var sb = new StringBuilder(1024);

            for (uint i = 0; i < count; i++)
            {
                sb.Clear();
                EverythingSdk.Everything_GetResultFullPathName(i, sb, (uint)sb.Capacity);

                EverythingSdk.Everything_GetResultSize(i, out long fileSize);

                string fullPath = sb.ToString();
                results[i] = new FileInfoResult
                {
                    FullPath = fullPath,
                    FileSize = fileSize,
                    DirectoryPath = Path.GetDirectoryName(fullPath)
                };
            }

            return results;
        }


        /// <summary>
        /// Like <see cref="GetFilesWithInfo"/> but also returns each
        /// file's last-modified timestamp. Slightly more expensive on
        /// the SDK side because it requires the DATE_MODIFIED request
        /// flag.
        /// </summary>
        public static FileInfoResultExtended[] GetFilesWithInfoExtended(
            string path,
            string searchPattern = "*.*")
        {
            var query = BuildQuery(path, searchPattern);

            EverythingSdk.Everything_SetSearch(query);

            // FULL_PATH + FILE_SIZE + DATE_MODIFIED
            EverythingSdk.Everything_SetRequestFlags(
                0x00000003 | 0x00000010 | 0x00000040);

            if (!EverythingSdk.Everything_Query(true))
                return Array.Empty<FileInfoResultExtended>();

            uint count = EverythingSdk.Everything_GetNumResults();
            var results = new FileInfoResultExtended[count];

            var sb = new StringBuilder(1024);

            for (uint i = 0; i < count; i++)
            {
                sb.Clear();
                EverythingSdk.Everything_GetResultFullPathName(
                    i, sb, (uint)sb.Capacity);

                EverythingSdk.Everything_GetResultSize(i, out long fileSize);

                // Last-modified timestamp.
                EverythingSdk.Everything_GetResultDateModified(
                    i, out long fileTime);

                string fullPath = sb.ToString();

                results[i] = new FileInfoResultExtended
                {
                    FullPath = fullPath,
                    DirectoryPath = Path.GetDirectoryName(fullPath),
                    FileSize = fileSize,
                    DateModified = DateTime.FromFileTimeUtc(fileTime)
                };
            }

            return results;
        }
    }

    /// <summary>
    /// Pure-.NET file enumeration helpers. Designed as a fallback for
    /// when Everything is unavailable, but CURRENTLY UNUSED in the
    /// project — GameCache rolls its own inline fallback instead.
    /// Kept here for future code paths that want a turnkey fallback.
    ///
    /// Note: <see cref="EnumerateFilesParallel"/> contains a known
    /// double-enumeration bug (the outer while-loop and the inner
    /// Parallel.ForEach process the same dirs). Fix before wiring up
    /// — see file header.
    /// </summary>
    internal static class FileSystemFallback
    {
        /// <summary>
        /// Walks <paramref name="rootPath"/> recursively in parallel
        /// and returns every file matching <paramref name="pattern"/>.
        /// Long-path normalized via <see cref="NormalizeLongPath"/>.
        ///
        /// ⚠ DEAD CODE WITH A BUG. The implementation iterates dirs
        /// in BOTH the outer while-loop AND a Parallel.ForEach over a
        /// snapshot of <c>dirs</c>, which means each dir is processed
        /// twice and the result list contains duplicates. Fix this
        /// before reactivating.
        /// </summary>
        public static string[] EnumerateFilesParallel(string rootPath, string pattern = "*.*")
        {
            if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
                return Array.Empty<string>();

            string normalizedPath = NormalizeLongPath(rootPath);

            var result = new ConcurrentBag<string>();
            var dirs = new ConcurrentQueue<string>();
            dirs.Enqueue(normalizedPath);

            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = 3
            };

            while (dirs.TryDequeue(out string currentDir))
            {
                try
                {
                    // Add the sub-folders.
                    var subDirs = Directory.GetDirectories(currentDir);
                    foreach (var dir in subDirs)
                        dirs.Enqueue(dir);

                    // Add the files.
                    var files = Directory.GetFiles(currentDir, pattern);
                    foreach (var file in files)
                        result.Add(file);
                }
                catch
                {
                    // Access denied or folder deleted → ignore.
                }

                // Parallel processing of the currently-known sub-folders.
                Parallel.ForEach(dirs.ToArray(), parallelOptions, dir =>
                {
                    try
                    {
                        var subDirs = Directory.GetDirectories(dir);
                        foreach (var subDir in subDirs)
                            dirs.Enqueue(subDir);

                        var files = Directory.GetFiles(dir, pattern);
                        foreach (var file in files)
                            result.Add(file);
                    }
                    catch { }
                });
            }

            return result.ToArray();
        }

        /// <summary>
        /// Prepends <c>\\?\</c> (or <c>\\?\UNC\</c> for UNC paths) to
        /// allow paths longer than MAX_PATH on Windows. No-op if the
        /// path already starts with <c>\\?\</c>.
        /// </summary>
        private static string NormalizeLongPath(string path)
        {
            if (path.StartsWith(@"\\?\"))
                return path;

            if (path.StartsWith(@"\\"))
                return @"\\?\UNC\" + path.Substring(2);

            return @"\\?\" + path;
        }

        /// <summary>
        /// Sequential pure-.NET enumeration returning
        /// <see cref="FileInfoResult"/> entries. Recursive
        /// (AllDirectories). Single-file failures are swallowed —
        /// inaccessible files are simply omitted. CURRENTLY UNUSED.
        /// </summary>
        public static FileInfoResult[] EnumerateFilesWithInfo(
            string rootPath,
            string pattern = "*.*"
        )
        {
            if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
                return Array.Empty<FileInfoResult>();

            var result = new List<FileInfoResult>();

            try
            {
                var files = Directory.EnumerateFiles(rootPath, pattern, SearchOption.AllDirectories);

                foreach (var filePath in files)
                {
                    try
                    {
                        var fileInfo = new FileInfo(filePath);
                        result.Add(new FileInfoResult
                        {
                            FullPath = filePath,
                            FileSize = fileInfo.Length,
                            DirectoryPath = fileInfo.DirectoryName
                        });
                    }
                    catch { }
                }
            }
            catch { }

            return result.ToArray();
        }
    }

}