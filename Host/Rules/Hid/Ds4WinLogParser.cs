// The DS4Windows log parser — BBP's HIDDS4WinParser, whole. When DS4Windows remaps a DualShock to a
// virtual X360 pad, XInput sees the FAKE pad; the only source of truth linking an XInput slot back
// to the real controller (its MAC, its connection type) is DS4Windows' own log. This walks the most
// recent .txt in the log folder BACKWARDS to the last "Starting..." marker (a "Stopped DS4Windows"
// after it means the tool shut down — empty result), then replays the association events in order:
// found controllers take the first free input slot, output slots map to XInput slots as virtual pads
// plug/unplug, removals free their entry. Only runs when a DS4Windows process exists.

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace LbApiHost.Host.Rules.Hid;

internal sealed class Ds4WinController
{
    public int InputSlot;
    public int OutputSlot;
    public int XinputSlot;
    public string MacAddress = "";
    public string Profile = "";
    public string ConnectionType = "";
    public string ControllerType = "";
}

internal static class Ds4WinLogParser
{
    public static List<Ds4WinController> Parse(string logDir)
    {
        var list = new List<Ds4WinController>();
        if (!Directory.Exists(logDir)) return list;
        if (Process.GetProcessesByName("DS4Windows").Length <= 0) return list;

        string? logFile = Directory.GetFiles(logDir, "*.txt")
            .Select(p => new FileInfo(p))
            .OrderByDescending(f => f.LastWriteTime)
            .FirstOrDefault()?.FullName;
        if (logFile == null) return list;

        var xinputSlot = new Dictionary<int, int>();   // output slot → XInput slot
        using var reader = new StringReader(RelevantTail(logFile));
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var m = Regex.Match(line, @"Found Controller: ([0-9A-F:]*) \(([^\)]*)\) \(([^\)]*)\)");
            if (m.Success)
            {
                var c = new Ds4WinController
                {
                    InputSlot = FirstFreeSlot(list),
                    MacAddress = m.Groups[1].Value,
                    ConnectionType = m.Groups[2].Value,
                    ControllerType = m.Groups[3].Value,
                };
                list.Add(c);
                continue;
            }

            m = Regex.Match(line, @"Associated input controller #([0-9]*) \(([^\)]*)\) to virtual X360 Controller in output slot #([0-9]*)");
            if (m.Success)
            {
                var c = ByInputSlot(list, int.Parse(m.Groups[1].Value));
                if (c == null) continue;
                if (c.ControllerType == m.Groups[2].Value)
                {
                    c.OutputSlot = int.Parse(m.Groups[3].Value);
                    if (xinputSlot.TryGetValue(c.OutputSlot, out int xs)) c.XinputSlot = xs;
                }
                continue;
            }

            m = Regex.Match(line, @"Controller ([0-9]*) is using Profile ""([^""]*)""");
            if (m.Success)
            {
                var c = ByInputSlot(list, int.Parse(m.Groups[1].Value));
                if (c != null) c.Profile = m.Groups[2].Value;
                continue;
            }

            m = Regex.Match(line, @"Disassociated virtual X360 Controller in output slot #([0-9]*) from input controller #([0-9]*)");
            if (m.Success)
            {
                // The original read group 1 as the CONTROLLER index — kept as-is (semantics pinned).
                var c = ByInputSlot(list, int.Parse(m.Groups[1].Value));
                if (c != null) c.OutputSlot = 0;
                continue;
            }

            m = Regex.Match(line, @"Controller ([0-9]*) was removed or lost connection");
            if (m.Success)
            {
                var c = ByInputSlot(list, int.Parse(m.Groups[1].Value));
                if (c != null) list.Remove(c);
                continue;
            }

            m = Regex.Match(line, @"Plugging in virtual X360 controller \(XInput slot #([0-9]*)\) in output slot #([0-9]*)");
            if (m.Success)
            {
                int xs = int.Parse(m.Groups[1].Value);
                int os = int.Parse(m.Groups[2].Value);
                xinputSlot[os] = xs;
                foreach (var c in list) if (c.OutputSlot == os) c.XinputSlot = xs;
            }

            m = Regex.Match(line, @"Unplugging virtual X360 Controller from output slot #([0-9]*)");
            if (m.Success) xinputSlot.Remove(int.Parse(m.Groups[1].Value));
        }
        return list;
    }

    private static Ds4WinController? ByInputSlot(List<Ds4WinController> list, int slot)
        => list.FirstOrDefault(c => c.InputSlot == slot);

    private static int FirstFreeSlot(List<Ds4WinController> list)
    {
        int slot = 1;
        while (list.Any(c => c.InputSlot == slot)) slot++;
        return slot;
    }

    /// <summary>Reads the file backwards in 4 KB chunks until the last "Starting..." (session start)
    /// or "Stopped DS4Windows" (tool stopped after it → ""). The original's GetRelevantLog.</summary>
    private static string RelevantTail(string filePath)
    {
        const string startText = "Starting...";
        const string stopText = "Stopped DS4Windows";
        string result = "";
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        byte[] buffer = new byte[4096];
        long position = fs.Length;
        while (position > 0)
        {
            position -= buffer.Length;
            if (position < 0) { buffer = new byte[buffer.Length + (int)position]; position = 0; }
            fs.Seek(position, SeekOrigin.Begin);
            int read = fs.Read(buffer, 0, buffer.Length);
            string text = Encoding.UTF8.GetString(buffer, 0, read);
            int startIdx = text.LastIndexOf(startText, StringComparison.Ordinal);
            int stopIdx = text.LastIndexOf(stopText, StringComparison.Ordinal);
            if (startIdx >= 0 || stopIdx >= 0)
            {
                int idx = Math.Max(startIdx, stopIdx);
                result = text.Substring(idx) + result;
                if (stopIdx > startIdx) return "";
                break;
            }
            result = text + result;
        }
        return result;
    }
}
