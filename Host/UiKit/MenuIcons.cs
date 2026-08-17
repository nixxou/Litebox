// The context-menu glyphs, loaded once from the embedded PNGs.
//
// They ship at 64x64 and are drawn at 16x16 (or 32 at 200% DPI), so the source is downscaled to the
// size the ToolStrip actually asks for rather than letting WinForms shrink a 64px bitmap on every
// paint — a nearest-ish runtime shrink is exactly what turns a legible glyph into mush.
//
// Every lookup is fail-soft: a missing or unreadable resource yields null, and a ToolStripMenuItem
// with a null Image simply renders without one. An icon is decoration; losing it must never cost a
// menu entry.

#nullable enable

using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;

namespace LbApiHost.Host.UiKit;

internal static class MenuIcons
{
    // Names mirror the resource files; each is one menu action.
    public const string Add = "add";                                 // create a new game
    public const string AdditionalApps = "additionalapps";           // extra applications of a game
    public const string AdditionalDocuments = "additionaldocuments"; // manuals and documents
    public const string Link = "link";                               // web links (Media ▸ Links; interlocked chain)
    public const string AdditionalVersions = "additionalversions";   // alternate versions
    public const string Combine = "combine";                         // merge the selection into one root game
    public const string Delete = "delete";
    public const string Edit = "edit";                               // bulk edit wizard
    public const string Expand = "expand";                           // split a combined game apart
    public const string Playlist = "playlist";                       // add to playlist
    public const string RefreshImages = "refreshall";
    public const string ResetCounts = "resetcounts";                 // play count and play time
    public const string ResetLastPlayed = "resetlastplayed";

    // Second batch — the LaunchBox-shaped game menu (play row, Media ▸, File Management ▸).
    public const string Play = "play";                               // launch the game
    public const string LaunchWith = "launchwith";                   // pick the emulator that runs it
    public const string Media = "media";                             // Media submenu header
    public const string FileManagement = "filemanagement";           // File Management submenu header
    public const string ViewImages = "viewimages";
    public const string View3dBox = "view3dbox";
    public const string ViewManual = "viewmanual";
    public const string PlayMusic = "playmusic";
    public const string FlipBox = "flipbox";                         // front box art ↔ back
    public const string SaveImageAs = "saveimageas";
    public const string OpenGameFolder = "opengamefolder";
    public const string OpenImagesFolder = "openimagesfolder";

    // Third batch — the LaunchBox-shaped top menu bar (Menu / Tools / View / Help). Names are wired
    // ahead of the art: until each PNG lands in menu-icons\, Get returns null and the entry shows
    // its text alone.
    public const string BigBox = "bigbox";                           // launch the fullscreen frontend
    public const string Trophy = "trophy";                           // achievements
    public const string View = "view";                               // View submenu header
    public const string Tools = "tools";                             // Tools submenu header
    public const string Help = "help";                               // Help submenu header
    public const string Exit = "exit";                               // quit the application
    public const string ListView = "listview";                       // the games as a detail table
    public const string ShowHide = "showhide";                       // which panels are visible
    public const string HideGames = "hidegames";                     // which games are filtered out
    public const string Badges = "badges";                           // the badge overlays on tiles
    public const string ImageGroup = "imagegroup";                   // which image type the tiles show
    public const string ArrangeBy = "arrangeby";                     // the sort field
    public const string Refresh = "refresh";                         // refresh the selection only
    public const string Import = "import";
    public const string Manage = "manage";
    public const string Download = "download";
    public const string ImagePacks = "imagepacks";
    public const string Audit = "audit";                             // report what a platform is missing
    public const string Scan = "scan";                               // scan the ROM folders
    public const string CleanUpMedia = "cleanupmedia";
    public const string Cloud = "cloud";
    public const string SelectRandomGame = "selectrandomgame";
    public const string ExportAndroid = "exportandroid";
    public const string Options = "options";
    public const string Welcome = "welcome";
    public const string Tutorials = "tutorials";
    public const string Forums = "forums";
    public const string Changelog = "changelog";
    public const string ReportIssue = "reportissue";
    public const string SendFeedback = "sendfeedback";
    public const string GetPremium = "getpremium";
    public const string LicenseRegistration = "licenseregistration";
    public const string CheckUpdates = "checkupdates";
    public const string About = "about";

    private static readonly ConcurrentDictionary<string, Image?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The glyph at the requested edge size, or null if it cannot be loaded.</summary>
    public static Image? Get(string name, int size = 16)
    {
        if (string.IsNullOrEmpty(name)) return null;
        return Cache.GetOrAdd($"{name}@{size}", _ => Load(name, size));
    }

    private static Image? Load(string name, int size)
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            using var s = asm.GetManifestResourceStream($"menu-icons/{name}.png");
            if (s == null) return null;
            using var src = new Bitmap(s);
            if (size <= 0 || (src.Width == size && src.Height == size)) return new Bitmap(src);

            var dst = new Bitmap(size, size);
            using (var g = Graphics.FromImage(dst))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.DrawImage(src, new Rectangle(0, 0, size, size));
            }
            return dst;
        }
        catch { return null; }
    }
}
