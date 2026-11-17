using SteamShelf.Media;
using System;
using System.IO;

namespace BR_Libretro;

public sealed class RomData : IMediaItem
{
    public string Id { get; internal set; } = string.Empty;
    public string Title { get; internal set; } = string.Empty;
    public string ContentPath { get; internal set; } = string.Empty;
    public string ArchiveEntry { get; internal set; } = string.Empty;
    public string PlatformId { get; internal set; } = string.Empty;
    public string CoverArtPath { get; internal set; } = string.Empty;
    public string CoreOverride { get; set; } = string.Empty;
    public string CaseColor { get; internal set; } = string.Empty;
    public long IgdbId { get; internal set; }
    public string Summary { get; internal set; } = string.Empty;
    public string ReleaseDate { get; internal set; } = string.Empty;
    public string[] Developers { get; internal set; } = Array.Empty<string>();
    public string[] Publishers { get; internal set; } = Array.Empty<string>();
    public string[] Genres { get; internal set; } = Array.Empty<string>();
    public string Fingerprint { get; internal set; } = string.Empty;
    public long FileSize { get; internal set; }
    public long LastWriteUtcTicks { get; internal set; }
    public bool IsSpawned { get; set; }
    public bool IsInHand { get; set; }
    public bool IsFullyLoaded => true;
    public bool CoverArtLoaded => !string.IsNullOrWhiteSpace(CoverArtPath) && File.Exists(CoverArtPath);
    public byte[] CoverArtBytes
    {
        get
        {
            if (!CoverArtLoaded) return null;
            try { return File.ReadAllBytes(CoverArtPath); }
            catch { return null; }
        }
    }
    public MediaRef Ref => new(RomMedia.Type, Id);
    public string DisplayName => string.IsNullOrWhiteSpace(Title) ? Path.GetFileNameWithoutExtension(ContentPath) : Title;
    public bool HasIgdbMetadata => IgdbId > 0;

    internal string ContentExtension
    {
        get
        {
            string subject = string.IsNullOrWhiteSpace(ArchiveEntry) ? ContentPath : ArchiveEntry;
            return Path.GetExtension(subject).TrimStart('.').ToLowerInvariant();
        }
    }
}
