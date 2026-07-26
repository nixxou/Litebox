// Every file/folder LiteBox CREATES at runtime lives under <LB>\Core\litebox\ (config, write-back
// journal, RA/store caches + badges, logs, per-session picks) so it doesn't clutter Core. The exe itself
// stays at Core\LiteBox.exe — its path math derives the LB root from being DIRECTLY in Core, and the
// AssemblyLoadContext resolver / lbRoot derivation keep using AppContext.BaseDirectory (= Core), NOT this
// folder. Only LiteBox-owned data goes here.

#nullable enable

using System;
using System.IO;

namespace LbApiHost.Host;

internal static class LiteBoxPaths
{
    /// <summary><LB>\Core\litebox — the single home for everything LiteBox writes. Created on demand.</summary>
    public static string Data
    {
        get
        {
            string d = Path.Combine(AppContext.BaseDirectory, "litebox");
            try { Directory.CreateDirectory(d); } catch { }
            return d;
        }
    }

    /// <summary><LB>\Core\litebox\<paramref name="name"/> (a file — parent dir ensured).</summary>
    public static string File(string name) => Path.Combine(Data, name);

    /// <summary><LB>\Core\litebox\<paramref name="name"/> (a directory — created on demand).</summary>
    public static string Dir(string name)
    {
        string d = Path.Combine(Data, name);
        try { Directory.CreateDirectory(d); } catch { }
        return d;
    }

    /// <summary><LB>\Core\litebox\cache — the parent of every REBUILDABLE cache LiteBox writes (thumbs,
    /// 3D GLB, RA/store JSON + badges, romcache, WebView2 profiles, download staging). Created on demand.
    /// One home so "clear caches" and DataMaintenance target a single tree; CacheReorg relocated the dirs
    /// that historically sat loose at the litebox\ root.</summary>
    public static string Cache
    {
        get
        {
            string d = Path.Combine(Data, "cache");
            try { Directory.CreateDirectory(d); } catch { }
            return d;
        }
    }

    /// <summary><LB>\Core\litebox\cache\<paramref name="name"/> (a cache subdirectory — created on demand).
    /// Use this for anything rebuildable so it lands under the single cache\ tree, NOT <see cref="Dir"/>.</summary>
    public static string CacheDir(string name)
    {
        string d = Path.Combine(Cache, name);
        try { Directory.CreateDirectory(d); } catch { }
        return d;
    }

    /// <summary>Web root for a site served by the embedded server:
    /// <LB>\Core\litebox\web\<paramref name="site"/> — "bigbox", "litebox", "database" or "vendor".
    /// Each of the three web frontends serves from its own folder; "vendor" is the shared JS/CSS lib root.</summary>
    public static string Web(string site)
    {
        string d = Path.Combine(Data, "web", site);
        try { Directory.CreateDirectory(d); } catch { }
        return d;
    }
}
