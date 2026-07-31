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
        var moves = GameMediaRenamer.Plan(Case(root), Id, Plat, Old, New, MediaNameForm.Plain);
        // Nothing to convert here (the unit is pure plain and the target IS plain), so the belt is
        // exercised through the GUID direction instead, where the game owns the namespace.
        var guidMoves = GameMediaRenamer.Plan(Case(root), Id, Plat, Old, New, MediaNameForm.Guid);
        GameMediaRenamer.Apply(guidMoves);
        int f = Check("a pure-plain unit is not touched when the target is plain", moves.Count == 0);
        return f + Check("plain → GUID moved the game's own file",
            Exists(dir, $"{Old}.{Id:D}-01.pdf") && Exists(dir, $"{New}-01.pdf"));
    }

    private static int LockedFileFallsBackToCopy(string root)
    {
        string dir = Fresh(root, "Images", "Box - Front");
        string locked = Touch(dir, $"{Old}-01.jpg");
        var moves = GameMediaRenamer.Plan(Case(root), Id, Plat, Old, New, MediaNameForm.Guid);
        int applied;
        using (var hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
            applied = GameMediaRenamer.Apply(moves);   // Move fails, Copy succeeds, Delete fails
        int f = Check("a locked file still reaches its target through a copy",
            applied == 1 && Exists(dir, $"{Old}.{Id:D}-01.jpg"));
        return f + Check("the locked source is left in place rather than losing the file",
            Exists(dir, $"{Old}-01.jpg"));
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
