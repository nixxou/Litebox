// --selftest-media-rename: the media rename/convert logic, exercised on a REAL temporary tree.
//
// It moves files on disk, so it is tested against actual files rather than a mock: the cases that
// matter are exactly the ones a mock would get wrong — a mixed unit, a suffixed GUID file, the
// global video rule, a number already taken, and a locked source.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LbApiHost.Host.Media;

namespace LbApiHost.Tools;

internal static class MediaRenameSelfTest
{
    private static readonly Guid Id = Guid.Parse("3f2a1b4c-1111-2222-3333-444455556666");
    private static readonly Guid Other = Guid.Parse("99999999-8888-7777-6666-555544443333");
    private const string Plat = "Arcade";
    private const string Old = "Street Fighter II";
    private const string New = "Street Fighter II Turbo";

    public static int Run()
    {
        int failures = 0;
        string root = Path.Combine(Path.GetTempPath(), "LiteBoxMediaTest_" + Guid.NewGuid().ToString("N"));
        try
        {
            failures += PlainToGuidKeepsNumbers(root);
            failures += MixedUnitIsLeftAlone(root);
            failures += SuffixedGuidNeverGoesPlain(root);
            failures += GuidBackToPlain(root);
            failures += VideosAreOneUnit(root);
            failures += TakenNumberIsBumped(root);
            failures += LockedFileFallsBackToCopy(root);
            failures += RegionSubfolderIsPartOfTheType(root);
            failures += MergeAppendsAfterTheDestination(root);
            failures += SharedSourceIsCopiedNotMoved(root);
            failures += FlushNotificationSurvivesBoot();
        }
        finally { Nuke(root); }

        Console.WriteLine(failures == 0 ? "[media-rename] ALL PASS" : $"[media-rename] {failures} FAILURE(S)");
        return failures == 0 ? 0 : 1;
    }

    // ── cases ────────────────────────────────────────────────────────────────────────────────
    private static int PlainToGuidKeepsNumbers(string root)
    {
        string dir = Fresh(root, "Images", "Box - Front");
        Touch(dir, $"{Old}-01.jpg");
        Touch(dir, $"{Old}-02.jpg");
        var moves = GameMediaRenamer.Plan(Case(root), Id, Plat, Old, New, MediaNameForm.Guid);
        GameMediaRenamer.Apply(moves);
        return Check("plain → GUID keeps each number",
            Exists(dir, $"{Old}.{Id:D}-01.jpg") && Exists(dir, $"{Old}.{Id:D}-02.jpg")
            && !Exists(dir, $"{Old}-01.jpg"));
    }

    private static int MixedUnitIsLeftAlone(string root)
    {
        string dir = Fresh(root, "Images", "Screenshot - Gameplay");
        Touch(dir, $"{Old}-01.png");                 // already invisible: Freeze drops it
        Touch(dir, $"{Old}.{Id:D}-01.png");
        var moves = GameMediaRenamer.Plan(Case(root), Id, Plat, Old, New, MediaNameForm.Guid);
        int f = Check("a mixed unit plans nothing", moves.Count == 0);
        GameMediaRenamer.Apply(moves);
        return f + Check("a mixed unit is left untouched on disk",
            Exists(dir, $"{Old}-01.png") && Exists(dir, $"{Old}.{Id:D}-01.png"));
    }

    private static int SuffixedGuidNeverGoesPlain(string root)
    {
        string dir = Fresh(root, "Images", "Clear Logo");
        Touch(dir, $"{Old}.{Id:D}-Europe-01.png");
        var moves = GameMediaRenamer.Plan(Case(root), Id, Plat, Old, New, MediaNameForm.Plain);
        return Check("a suffixed GUID file stays GUID", moves.Count == 0);
    }

    private static int GuidBackToPlain(string root)
    {
        string dir = Fresh(root, "Images", "Box - Front");
        Touch(dir, $"{Old}.{Id:D}-01.jpg");
        Touch(dir, $"{Old}.{Id:D}-02.jpg");
        var moves = GameMediaRenamer.Plan(Case(root), Id, Plat, Old, New, MediaNameForm.Plain);
        GameMediaRenamer.Apply(moves);
        return Check("GUID → plain uses the NEW title and keeps the numbers",
            Exists(dir, $"{New}-01.jpg") && Exists(dir, $"{New}-02.jpg")
            && !Exists(dir, $"{Old}.{Id:D}-01.jpg"));
    }

    private static int VideosAreOneUnit(string root)
    {
        string baseDir = Fresh(root, "Videos", null);
        string trailer = Path.Combine(baseDir, "Trailer");
        Directory.CreateDirectory(trailer);
        Touch(baseDir, $"{Old}-01.mp4");
        Touch(trailer, $"{Old}.{Id:D}-01.mp4");      // one GUID video anywhere → the unit is mixed
        var moves = GameMediaRenamer.Plan(Case(root), Id, Plat, Old, New, MediaNameForm.Guid);
        return Check("videos are one unit: a GUID in a subfolder freezes the whole game",
            moves.Count == 0);
    }

    private static int TakenNumberIsBumped(string root)
    {
        string dir = Fresh(root, "Manuals", null);
        Touch(dir, $"{Old}-01.pdf");
        Touch(dir, $"{New}-01.pdf");                 // the target number is already used
        // -01 under the new title is already taken, so the belt has to find the next free slot
        // rather than refuse or clobber. This namespace is the game's own: the caller never targets
        // the plain form when another game holds that title.
        var moves = GameMediaRenamer.Plan(Case(root), Id, Plat, Old, New, MediaNameForm.Plain);
        GameMediaRenamer.Apply(moves);
        int f = Check("a taken number is bumped to the next free one",
            Exists(dir, $"{New}-02.pdf") && !Exists(dir, $"{Old}-01.pdf"));
        return f + Check("the file that already held the number is untouched",
            Exists(dir, $"{New}-01.pdf"));
    }

    private static int LockedFileFallsBackToCopy(string root)
    {
        string dir = Fresh(root, "Images", "Box - Front");
        string locked = Touch(dir, $"{Old}-01.jpg");
        var moves = GameMediaRenamer.Plan(Case(root), Id, Plat, Old, New, MediaNameForm.Guid);
        GameMediaRenamer.MediaMoveResult applied;
        using (var hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
            applied = GameMediaRenamer.Apply(moves);   // Move fails, Copy succeeds, Delete fails
        int f = Check("a locked file still reaches its target through a copy",
            applied.Reached == 1 && applied.Copied == 1 && applied.Failed == 0
            && Exists(dir, $"{Old}.{Id:D}-01.jpg"));
        return f + Check("the locked source is left in place rather than losing the file",
            Exists(dir, $"{Old}-01.jpg"));
    }

    /// <summary>Reported from use: renaming Lylatwars left Lylatwars-20.png untouched under
    /// "Box - Front\World". Images live in the type folder AND in its region sub-folders, which
    /// MediaResolver walks through RegionOrder() — a type unit has to span both.</summary>
    private static int RegionSubfolderIsPartOfTheType(string root)
    {
        string type = Fresh(root, "Images", "Box - Front");
        string world = Path.Combine(type, "World");
        Directory.CreateDirectory(world);
        Touch(world, "Lylatwars-20.png");
        Touch(type, "Lylatwars-01.png");

        var moves = GameMediaRenamer.Plan(Case(root), Id, Plat, "Lylatwars", "LylatwarsAAA", MediaNameForm.Plain);
        GameMediaRenamer.Apply(moves);
        int f = Check("a file in a region sub-folder is renamed too",
            Exists(world, "LylatwarsAAA-20.png") && !Exists(world, "Lylatwars-20.png"));
        f += Check("the region file keeps its number", Exists(world, "LylatwarsAAA-20.png"));
        return f + Check("the file at the type root is renamed as well",
            Exists(type, "LylatwarsAAA-01.png"));
    }

    /// <summary>Renaming onto a title held by the SAME game (same database id) is a merge, not a
    /// clash: the files already there are the destination and must not move, and ours join them
    /// numbered AFTER the highest one present — never filling its gaps, so its order is intact.</summary>
    private static int MergeAppendsAfterTheDestination(string root)
    {
        string dir = Fresh(root, "Images", "Box - Front");
        Touch(dir, $"{New}-01.jpg");                 // destination, must not move
        Touch(dir, $"{New}-03.jpg");                 // note the gap at 02
        Touch(dir, $"{Old}-01.jpg");                 // source
        Touch(dir, $"{Old}-02.jpg");

        var moves = GameMediaRenamer.Plan(Case(root), Id, Plat, Old, New, MediaNameForm.Plain, append: true);
        GameMediaRenamer.Apply(moves);
        int f = Check("a merge leaves the destination files untouched",
            Exists(dir, $"{New}-01.jpg") && Exists(dir, $"{New}-03.jpg"));
        f += Check("merged files are appended after the highest number, not into the gap",
            Exists(dir, $"{New}-04.jpg") && Exists(dir, $"{New}-05.jpg") && !Exists(dir, $"{New}-02.jpg"));
        return f + Check("the source names are gone after a merge",
            !Exists(dir, $"{Old}-01.jpg") && !Exists(dir, $"{Old}-02.jpg"));
    }

    /// <summary>Another game still answering to the source title shares these files, whatever the
    /// media kind. Moving them would strip that game, so they are copied and the original stays.</summary>
    private static int SharedSourceIsCopiedNotMoved(string root)
    {
        string dir = Fresh(root, "Music", null);
        Touch(dir, $"{Old}-01.mp3");
        var moves = GameMediaRenamer.Plan(Case(root), Id, Plat, Old, New, MediaNameForm.Plain, sharedSource: true);
        var res = GameMediaRenamer.Apply(moves);
        int f = Check("a shared source is copied, not moved", res.Copied == 1 && res.Moved == 0);
        return f + Check("the other game keeps its file",
            Exists(dir, $"{Old}-01.mp3") && Exists(dir, $"{New}-01.mp3"));
    }

    /// <summary>The boot flush runs before the DataManager exists, so nobody is listening when it
    /// lands a rename made while LaunchBox held the XMLs — the very case the transit form is for.
    /// Those ids must be kept and handed over when a listener finally subscribes.</summary>
    private static int FlushNotificationSurvivesBoot()
    {
        var store = new LbApiHost.Host.Data.GameStore();
        var raisedBefore = new List<Guid>();
        // Raise with no listener attached (boot order), then subscribe.
        Invoke(store, new[] { Id });
        int f = Check("nothing is delivered while nobody listens", raisedBefore.Count == 0);

        var got = new List<Guid>();
        store.TitlesFlushed = ids => got.AddRange(ids);
        f += Check("subscribing later still receives the boot flush", got.Count == 1 && got[0] == Id);

        var again = new List<Guid>();
        store.TitlesFlushed = ids => again.AddRange(ids);
        return f + Check("the backlog is delivered once, not replayed", again.Count == 0);
    }

    /// <summary>Reaches the private notifier the flush calls, so the test drives the real path.</summary>
    private static void Invoke(LbApiHost.Host.Data.GameStore store, Guid[] ids)
    {
        var opType = typeof(LbApiHost.Host.Data.GameStore).Assembly.GetType("LbApiHost.Host.Data.Op");
        var listType = typeof(List<>).MakeGenericType(opType!);
        var list = (System.Collections.IList)Activator.CreateInstance(listType)!;
        foreach (var id in ids)
            list.Add(Activator.CreateInstance(opType!, 0L, "modify", "Game", id.ToString("D"), "", "Title", "x"));
        typeof(LbApiHost.Host.Data.GameStore)
            .GetMethod("NotifyTitlesFlushed", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(store, new object[] { list });
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────
    private static string _case = "";
    private static string Case(string root) => _case;

    /// <summary>A pristine LB-shaped tree per case, so one case cannot leak into the next.</summary>
    private static string Fresh(string root, string kind, string typeDir)
    {
        _case = Path.Combine(root, Guid.NewGuid().ToString("N"));
        string dir = Path.Combine(_case, kind, MediaResolver.Sanitize(Plat));
        if (typeDir != null) dir = Path.Combine(dir, typeDir);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string Touch(string dir, string name)
    {
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, "x");
        return path;
    }

    private static bool Exists(string dir, string name) => File.Exists(Path.Combine(dir, name));

    private static void Nuke(string dir) { try { Directory.Delete(dir, true); } catch { } }

    private static int Check(string name, bool ok)
    {
        Console.WriteLine($"[media-rename] {(ok ? "PASS" : "FAIL")} {name}");
        return ok ? 0 : 1;
    }
}
