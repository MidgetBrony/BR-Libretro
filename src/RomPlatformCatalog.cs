using System;
using System.Collections.Generic;
using System.Linq;

namespace BR_Libretro;

internal enum RomPhysicalFormat { NesCartridge, SnesCartridge, Cartridge, HandheldCartridge, Disc }

internal sealed class RomPlatform
{
    internal string Id { get; init; }
    internal string DisplayName { get; init; }
    internal string FolderName { get; init; }
    internal RomPhysicalFormat PhysicalFormat { get; init; }
    internal string[] Extensions { get; init; } = Array.Empty<string>();
    internal string[] PreferredCores { get; init; } = Array.Empty<string>();
    internal int IgdbPlatformId { get; init; }

    internal bool Supports(string extension) => Extensions.Contains(extension.TrimStart('.'), StringComparer.OrdinalIgnoreCase);
}

internal static class RomPlatformCatalog
{
    private static readonly Dictionary<string, RomPlatform> Platforms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["nes"] = New("nes", "Nintendo Entertainment System", "NES", RomPhysicalFormat.NesCartridge, 18,
            new[] { "nes", "fds", "unf", "unif" }, "mesen", "fceumm", "nestopia"),
        ["snes"] = New("snes", "Super Nintendo", "SNES", RomPhysicalFormat.SnesCartridge, 19,
            new[] { "sfc", "smc" }, "snes9x"),
        ["genesis"] = New("genesis", "Sega Genesis / Mega Drive", "Genesis", RomPhysicalFormat.Cartridge, 29,
            new[] { "md", "gen", "smd" }, "genesis_plus_gx", "picodrive"),
        ["gb"] = New("gb", "Game Boy", "Game Boy", RomPhysicalFormat.HandheldCartridge, 33,
            new[] { "gb" }, "gambatte", "mgba"),
        ["gbc"] = New("gbc", "Game Boy Color", "Game Boy Color", RomPhysicalFormat.HandheldCartridge, 22,
            new[] { "gbc" }, "gambatte", "mgba"),
        ["gba"] = New("gba", "Game Boy Advance", "Game Boy Advance", RomPhysicalFormat.HandheldCartridge, 24,
            new[] { "gba" }, "mgba"),
        ["psx"] = New("psx", "PlayStation", "PlayStation", RomPhysicalFormat.Disc, 7,
            new[] { "cue", "chd", "pbp", "m3u" }, "pcsx_rearmed", "swanstation"),
        ["doom"] = New("doom", "Doom", "Doom", RomPhysicalFormat.Disc, 6,
            new[] { "wad" }, "prboom")
    };

    internal static IEnumerable<RomPlatform> All => Platforms.Values;
    internal static bool TryGet(string id, out RomPlatform platform) => Platforms.TryGetValue(id ?? string.Empty, out platform);

    private static RomPlatform New(string id, string name, string folder, RomPhysicalFormat format, int igdbPlatformId, string[] extensions, params string[] cores) => new()
    {
        Id = id, DisplayName = name, FolderName = folder, PhysicalFormat = format,
        Extensions = extensions, PreferredCores = cores, IgdbPlatformId = igdbPlatformId
    };
}
