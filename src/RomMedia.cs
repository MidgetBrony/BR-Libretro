using SteamShelf.Media;

namespace BR_Libretro;

internal static class RomMedia
{
    // Media IDs are permanent save contracts. 1002 belongs to BR-BookSystem.
    internal const int TypeId = 1003;
    internal const string Key = "com.midgetbrony.br-libretro.roms";
    internal const string PlaceableId = "BR_Libretro_ROM";
    internal static readonly eMediaType Type = (eMediaType)TypeId;
}
