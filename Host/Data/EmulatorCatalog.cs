// Loads LB\Data\Emulators.xml: <Emulator> definitions + <EmulatorPlatform>
// mappings. Backs IEmulator/IEmulatorPlatform so GetAllEmulators/GetEmulatorById
// work and real launch can build a command line.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Unbroken.LaunchBox.Plugins.Data;
using LbApiHost.Generated;

namespace LbApiHost.Host.Data;

internal sealed class HostEmulatorPlatform : DummyEmulatorPlatform
{
    public string EmulatorIdValue, PlatformValue, CommandLineValue;
    public bool IsDefaultValue, M3uValue;
    public bool? AutoExtractValue;

    public override string EmulatorId { get => EmulatorIdValue ?? ""; set { } }
    public override string Platform { get => PlatformValue ?? ""; set { } }
    public override string CommandLine { get => CommandLineValue ?? ""; set { } }
    public override bool IsDefault { get => IsDefaultValue; set { } }
    public override bool M3uDiscLoadEnabled { get => M3uValue; set { } }
    public override Nullable<bool> AutoExtract { get => AutoExtractValue; set { } }
}

internal sealed class HostEmulator : DummyEmulator
{
    private readonly string _id;
    private readonly IEmulatorPlatform[] _platforms;
    public HostEmulator(string id, IEmulatorPlatform[] platforms) { _id = id; _platforms = platforms; }

    public string TitleValue, ApplicationPathValue, CommandLineValue, DefaultPlatformValue;
    public bool NoSpaceValue, NoQuotesValue;

    public override string Id { get => _id; set { } }
    public override string Title { get => TitleValue ?? ""; set { } }
    public override string ApplicationPath { get => ApplicationPathValue ?? ""; set { } }
    public override string CommandLine { get => CommandLineValue ?? ""; set { } }
    public override string DefaultPlatform { get => DefaultPlatformValue ?? ""; set { } }
    public override bool NoSpace { get => NoSpaceValue; set { } }
    public override bool NoQuotes { get => NoQuotesValue; set { } }

    public override IEmulatorPlatform[] GetAllEmulatorPlatforms() => _platforms;
}

internal static class EmulatorCatalog
{
    public static List<HostEmulator> Load(string dataDir)
    {
        var result = new List<HostEmulator>();
        string file = Path.Combine(dataDir, "Emulators.xml");
        if (!File.Exists(file)) return result;

        XDocument doc;
        try { doc = XDocument.Load(file); } catch { return result; }
        var root = doc.Root;
        if (root == null) return result;

        // EmulatorPlatform mappings grouped by emulator id.
        var byEmu = new Dictionary<string, List<IEmulatorPlatform>>(StringComparer.OrdinalIgnoreCase);
        foreach (var ep in root.Elements("EmulatorPlatform"))
        {
            string emuId = (string)ep.Element("Emulator");
            if (string.IsNullOrWhiteSpace(emuId)) continue;
            var hep = new HostEmulatorPlatform
            {
                EmulatorIdValue = emuId,
                PlatformValue = (string)ep.Element("Platform"),
                CommandLineValue = (string)ep.Element("CommandLine"),
                IsDefaultValue = ((string)ep.Element("Default") ?? "").Equals("true", StringComparison.OrdinalIgnoreCase),
                M3uValue = ((string)ep.Element("M3uDiscLoadEnabled") ?? "").Equals("true", StringComparison.OrdinalIgnoreCase),
                AutoExtractValue = ParseBoolN((string)ep.Element("AutoExtract")),
            };
            if (!byEmu.TryGetValue(emuId, out var list)) byEmu[emuId] = list = new List<IEmulatorPlatform>();
            list.Add(hep);
        }

        foreach (var ee in root.Elements("Emulator"))
        {
            string id = (string)ee.Element("ID");
            if (string.IsNullOrWhiteSpace(id)) continue;
            byEmu.TryGetValue(id, out var platforms);
            result.Add(new HostEmulator(id, (platforms ?? new List<IEmulatorPlatform>()).ToArray())
            {
                TitleValue = (string)ee.Element("Title"),
                ApplicationPathValue = (string)ee.Element("ApplicationPath"),
                CommandLineValue = (string)ee.Element("CommandLine"),
                DefaultPlatformValue = (string)ee.Element("DefaultPlatform"),
                NoSpaceValue = ((string)ee.Element("NoSpace") ?? "").Equals("true", StringComparison.OrdinalIgnoreCase),
                NoQuotesValue = ((string)ee.Element("NoQuotes") ?? "").Equals("true", StringComparison.OrdinalIgnoreCase),
            });
        }

        Console.WriteLine($"[emucat] file={file} exists={File.Exists(file)} emulators={result.Count} emPlatformGroups={byEmu.Count}");
        return result;
    }

    private static bool? ParseBoolN(string s)
        => string.IsNullOrWhiteSpace(s) ? (bool?)null : s.Equals("true", StringComparison.OrdinalIgnoreCase);
}
