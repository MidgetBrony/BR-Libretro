using SteamShelf.Media;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace BR_Libretro;

internal sealed class RomMediaLibrary : IMediaLibrary
{
    public eMediaType HandledType => RomMedia.Type;
    public event Action<IMediaItem> OnItemReady;
    public event Action<IReadOnlyList<IMediaItem>> OnLibraryReady;

    internal RomMediaLibrary()
    {
        RomLibrarySystem.OnItemReady += item => OnItemReady?.Invoke(item);
        RomLibrarySystem.OnLibraryReady += items => OnLibraryReady?.Invoke(items.Cast<IMediaItem>().ToList());
    }

    public IReadOnlyList<IMediaItem> GetKnownItems() => RomLibrarySystem.GetKnownItems().Cast<IMediaItem>().ToList();
    public IMediaItem GetItemSync(MediaRef mediaRef) => mediaRef.Type == RomMedia.Type ? RomLibrarySystem.GetItem(mediaRef.Id) : null;
    public Task<IMediaItem> GetItemAsync(MediaRef mediaRef) => Task.FromResult(GetItemSync(mediaRef));
}
