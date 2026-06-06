// A tiny in-memory dummy catalog (fake games / platforms / emulators) so the
// host stands up the LB data API with believable content. Pure placeholder —
// real data (LB XML / Extended DB) comes later.

using System;
using System.Collections.Generic;
using Unbroken.LaunchBox.Plugins.Data;
using LbApiHost.Generated;

namespace LbApiHost.Host;

internal sealed class HostCatalog
{
    public List<IGame> Games { get; } = new();
    public List<IPlatform> Platforms { get; } = new();
    public List<IEmulator> Emulators { get; } = new();

    public static HostCatalog BuildDummy()
    {
        var c = new HostCatalog();

        var duck = new DummyEmulator { Id = Guid.NewGuid().ToString(), Title = "DuckStation (dummy)", ApplicationPath = @"C:\Emu\duckstation\duckstation.exe" };
        var mgba = new DummyEmulator { Id = Guid.NewGuid().ToString(), Title = "mGBA (dummy)", ApplicationPath = @"C:\Emu\mgba\mgba.exe" };
        c.Emulators.Add(duck);
        c.Emulators.Add(mgba);

        var ps1 = NewPlatform("Sony Playstation", "Sony");
        AddGame(c, ps1, "Final Fantasy VII", "Role-Playing", duck.Id);
        AddGame(c, ps1, "Metal Gear Solid", "Action", duck.Id);
        AddGame(c, ps1, "Gran Turismo", "Racing", duck.Id);

        var gb = NewPlatform("Nintendo Game Boy", "Nintendo");
        AddGame(c, gb, "Tetris", "Puzzle", mgba.Id);
        AddGame(c, gb, "Pokemon Red", "Role-Playing", mgba.Id);

        c.Platforms.Add(ps1);
        c.Platforms.Add(gb);
        return c;
    }

    private static HostPlatform NewPlatform(string name, string manufacturer)
        => new HostPlatform { Name = name, Manufacturer = manufacturer };

    private static void AddGame(HostCatalog c, HostPlatform p, string title, string genre, string emulatorId)
    {
        var g = new DummyGame
        {
            Id = Guid.NewGuid().ToString(),
            Title = title,
            Platform = p.Name,
            GenresString = genre,
            EmulatorId = emulatorId,
            ApplicationPath = $@"C:\Games\{p.Name}\{title}.bin",
            DateAdded = new DateTime(2020, 1, 1),
            StarRatingFloat = 4.0f,
            Rating = "E",
            PlayCount = 0,
        };
        c.Games.Add(g);
        p.GamesList.Add(g);
    }
}
