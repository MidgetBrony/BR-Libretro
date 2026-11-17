using MelonLoader;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace BR_Libretro;

internal sealed class RomSource
{
    public string Platform { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string CoreOverride { get; set; } = string.Empty;
    public bool Recursive { get; set; } = true;
}

internal sealed class RomSourcesConfig
{
    public int Version { get; set; } = 1;
    public List<RomSource> Sources { get; set; } = new();
}

internal sealed class RomRegistryFile
{
    public int Version { get; set; } = 3;
    public List<RomRegistryRecord> Records { get; set; } = new();
}

internal sealed class RomRegistryRecord
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string ContentPath { get; set; } = string.Empty;
    public string ArchiveEntry { get; set; } = string.Empty;
    public string PlatformId { get; set; } = string.Empty;
    public string CoverArtPath { get; set; } = string.Empty;
    public string CoreOverride { get; set; } = string.Empty;
    public string CaseColor { get; set; } = string.Empty;
    public long IgdbId { get; set; }
    public string Summary { get; set; } = string.Empty;
    public string ReleaseDate { get; set; } = string.Empty;
    public string[] Developers { get; set; } = Array.Empty<string>();
    public string[] Publishers { get; set; } = Array.Empty<string>();
    public string[] Genres { get; set; } = Array.Empty<string>();
    public string Fingerprint { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public long LastWriteUtcTicks { get; set; }
}

internal static class RomLibrarySystem
{
    private static readonly string SourcesPath = Path.Combine(DataPaths.Config, "rom-folders.json");
    private static readonly string RegistryPath = Path.Combine(DataPaths.Config, "rom-registry.json");
    private static readonly Dictionary<string, RomData> Items = new(StringComparer.OrdinalIgnoreCase);
    internal static event Action<RomData> OnItemReady;
    internal static event Action<IReadOnlyList<RomData>> OnLibraryReady;

    internal static IReadOnlyList<RomData> GetKnownItems() => Items.Values
        .OrderBy(value => value.PlatformId, StringComparer.OrdinalIgnoreCase)
        .ThenBy(value => value.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();

    internal static RomData GetItem(string id) => id != null && Items.TryGetValue(id, out RomData item) ? item : null;

    internal static void ResetTracking()
    {
        foreach (RomData item in Items.Values) { item.IsSpawned = false; item.IsInHand = false; }
    }

    internal static string Status()
    {
        int count = Items.Count;
        int platforms = Items.Values.Select(value => value.PlatformId).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        return $"{count} ROM{(count == 1 ? string.Empty : "s")} across {platforms} platform{(platforms == 1 ? string.Empty : "s")}";
    }

    internal static void Reload()
    {
        try
        {
            string root = BR_MediaAPI.MediaApi.GetLibraryFolder(RomMedia.Type);
            if (string.IsNullOrWhiteSpace(root)) root = DataPaths.Roms;
            Directory.CreateDirectory(root);
            RomSourcesConfig sources = LoadSources(root);
            RomRegistryFile registry = LoadRegistry();
            Dictionary<string, RomRegistryRecord> byLocation = registry.Records
                .Where(record => !string.IsNullOrWhiteSpace(record.Id))
                .GroupBy(LocationKey, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            Dictionary<string, RomRegistryRecord> byFingerprint = registry.Records
                .Where(record => !string.IsNullOrWhiteSpace(record.Fingerprint))
                .GroupBy(record => record.Fingerprint, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var discovered = new Dictionary<string, RomData>(StringComparer.OrdinalIgnoreCase);

            foreach (RomSource source in sources.Sources ?? new())
            {
                if (!RomPlatformCatalog.TryGet(source.Platform, out RomPlatform platform))
                {
                    MelonLogger.Warning($"Ignoring ROM source with unknown platform '{source.Platform}'.");
                    continue;
                }
                string path = ResolveSourcePath(root, source.Path, platform.FolderName);
                if (!Directory.Exists(path)) { Directory.CreateDirectory(path); continue; }
                SearchOption search = source.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                foreach (string file in Directory.EnumerateFiles(path, "*", search))
                {
                    foreach (string archiveEntry in DiscoverEntries(file, platform))
                    {
                        string fullPath = Path.GetFullPath(file);
                        var probe = new RomRegistryRecord { ContentPath = fullPath, ArchiveEntry = archiveEntry, PlatformId = platform.Id };
                        string location = LocationKey(probe);
                        FileInfo info = new(fullPath);
                        RomRegistryRecord record = byLocation.TryGetValue(location, out RomRegistryRecord existing) ? existing : null;
                        string fingerprint = record != null && record.FileSize == info.Length && record.LastWriteUtcTicks == info.LastWriteTimeUtc.Ticks
                            ? record.Fingerprint
                            : Fingerprint(fullPath, platform.Id, archiveEntry);
                        if (record == null && !string.IsNullOrWhiteSpace(fingerprint)) byFingerprint.TryGetValue(fingerprint, out record);
                        record ??= new RomRegistryRecord { Id = Guid.NewGuid().ToString("N") };
                        record.ContentPath = fullPath;
                        record.ArchiveEntry = archiveEntry;
                        record.PlatformId = platform.Id;
                        record.Fingerprint = fingerprint;
                        record.FileSize = info.Length;
                        record.LastWriteUtcTicks = info.LastWriteTimeUtc.Ticks;
                        string discoveredTitle = TitleFrom(fullPath, archiveEntry);
                        if (record.IgdbId <= 0 || string.IsNullOrWhiteSpace(record.Title)) record.Title = discoveredTitle;
                        record.CoverArtPath = FindCover(fullPath, archiveEntry, record.CoverArtPath);

                        // Identical content in two locations is one media identity.
                        if (!discovered.ContainsKey(record.Id)) discovered.Add(record.Id, ToData(record));
                        byLocation[LocationKey(record)] = record;
                        if (!string.IsNullOrWhiteSpace(record.Fingerprint)) byFingerprint[record.Fingerprint] = record;
                    }
                }
            }

            Dictionary<string, (bool spawned, bool hand)> previous = Items.ToDictionary(
                pair => pair.Key, pair => (pair.Value.IsSpawned, pair.Value.IsInHand), StringComparer.OrdinalIgnoreCase);
            Items.Clear();
            foreach (KeyValuePair<string, RomData> pair in discovered)
            {
                if (previous.TryGetValue(pair.Key, out var flags)) { pair.Value.IsSpawned = flags.spawned; pair.Value.IsInHand = flags.hand; }
                Items.Add(pair.Key, pair.Value);
                OnItemReady?.Invoke(pair.Value);
            }

            // Keep missing records so a moved/remounted library can recover its old IDs later.
            foreach (RomData item in discovered.Values)
            {
                RomRegistryRecord updated = FromData(item);
                int index = registry.Records.FindIndex(value => string.Equals(value.Id, item.Id, StringComparison.OrdinalIgnoreCase));
                if (index >= 0) registry.Records[index] = updated; else registry.Records.Add(updated);
            }
            SaveAtomic(RegistryPath, registry);
            OnLibraryReady?.Invoke(GetKnownItems());
            MelonLogger.Msg($"ROM media library ready: {Status()}. Sources: {SourcesPath}");
        }
        catch (Exception ex)
        {
            MelonLogger.Error("Could not scan the ROM media library: " + ex);
            OnLibraryReady?.Invoke(GetKnownItems());
        }
    }

    private static RomSourcesConfig LoadSources(string root)
    {
        if (File.Exists(SourcesPath))
            return JsonConvert.DeserializeObject<RomSourcesConfig>(File.ReadAllText(SourcesPath)) ?? new RomSourcesConfig();
        var config = new RomSourcesConfig();
        foreach (RomPlatform platform in RomPlatformCatalog.All)
        {
            string folder = Path.Combine(root, platform.FolderName);
            Directory.CreateDirectory(folder);
            config.Sources.Add(new RomSource { Platform = platform.Id, Path = platform.FolderName, Recursive = true });
        }
        SaveAtomic(SourcesPath, config);
        return config;
    }

    internal static IReadOnlyList<RomData> GetPlatformItems(string platformId) => Items.Values
        .Where(item => string.Equals(item.PlatformId, platformId, StringComparison.OrdinalIgnoreCase))
        .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();

    internal static string GetPlatformSourcePath(string platformId)
    {
        string root = BR_MediaAPI.MediaApi.GetLibraryFolder(RomMedia.Type);
        if (string.IsNullOrWhiteSpace(root)) root = DataPaths.Roms;
        RomSourcesConfig config = LoadSources(root);
        RomSource source = config.Sources.FirstOrDefault(value => string.Equals(value.Platform, platformId, StringComparison.OrdinalIgnoreCase));
        if (!RomPlatformCatalog.TryGet(platformId, out RomPlatform platform)) return source?.Path ?? string.Empty;
        return ResolveSourcePath(root, source?.Path, platform.FolderName);
    }

    internal static void SetPlatformSourcePath(string platformId, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !RomPlatformCatalog.TryGet(platformId, out _)) return;
        string root = BR_MediaAPI.MediaApi.GetLibraryFolder(RomMedia.Type);
        if (string.IsNullOrWhiteSpace(root)) root = DataPaths.Roms;
        RomSourcesConfig config = LoadSources(root);
        RomSource source = config.Sources.FirstOrDefault(value => string.Equals(value.Platform, platformId, StringComparison.OrdinalIgnoreCase));
        if (source == null) { source = new RomSource { Platform = platformId, Recursive = true }; config.Sources.Add(source); }
        source.Path = Path.GetFullPath(path);
        SaveAtomic(SourcesPath, config);
        Reload();
    }

    internal static string GetPlatformCoreOverride(string platformId)
    {
        string root = BR_MediaAPI.MediaApi.GetLibraryFolder(RomMedia.Type);
        if (string.IsNullOrWhiteSpace(root)) root = DataPaths.Roms;
        RomSourcesConfig config = LoadSources(root);
        return config.Sources.FirstOrDefault(value => string.Equals(value.Platform, platformId, StringComparison.OrdinalIgnoreCase))?.CoreOverride ?? string.Empty;
    }

    internal static void SetPlatformCoreOverride(string platformId, string coreId)
    {
        if (!RomPlatformCatalog.TryGet(platformId, out RomPlatform platform)) return;
        string root = BR_MediaAPI.MediaApi.GetLibraryFolder(RomMedia.Type);
        if (string.IsNullOrWhiteSpace(root)) root = DataPaths.Roms;
        RomSourcesConfig config = LoadSources(root);
        RomSource source = config.Sources.FirstOrDefault(value => string.Equals(value.Platform, platformId, StringComparison.OrdinalIgnoreCase));
        if (source == null) { source = new RomSource { Platform = platformId, Path = platform.FolderName, Recursive = true }; config.Sources.Add(source); }
        source.CoreOverride = coreId ?? string.Empty;
        SaveAtomic(SourcesPath, config);
    }

    internal static void SetRomCoreOverride(string romId, string coreId)
    {
        RomData item = GetItem(romId);
        if (item == null) return;
        item.CoreOverride = coreId ?? string.Empty;
        RomRegistryFile registry = LoadRegistry();
        RomRegistryRecord record = registry.Records.FirstOrDefault(value => string.Equals(value.Id, romId, StringComparison.OrdinalIgnoreCase));
        if (record == null) registry.Records.Add(FromData(item));
        else record.CoreOverride = item.CoreOverride;
        SaveAtomic(RegistryPath, registry);
    }

    internal static void SetCaseColor(string romId, Color color)
    {
        RomData item = GetItem(romId);
        if (item == null) return;
        item.CaseColor = "#" + ColorUtility.ToHtmlStringRGB(color);
        RomRegistryFile registry = LoadRegistry();
        RomRegistryRecord record = registry.Records.FirstOrDefault(value => string.Equals(value.Id, romId, StringComparison.OrdinalIgnoreCase));
        if (record == null) registry.Records.Add(FromData(item));
        else record.CaseColor = item.CaseColor;
        SaveAtomic(RegistryPath, registry);
        RomMediaVisual.RefreshCaseColor(item, color);
    }

    internal static void ApplyIgdbMetadata(RomData item, IgdbGame details, string coverPath)
    {
        if (item == null || details == null || details.Id <= 0) return;
        item.IgdbId = details.Id;
        if (!string.IsNullOrWhiteSpace(details.Name)) item.Title = details.Name.Trim();
        item.Summary = details.Summary?.Trim() ?? string.Empty;
        item.ReleaseDate = details.FirstReleaseDate > 0
            ? DateTimeOffset.FromUnixTimeSeconds(details.FirstReleaseDate).ToString("yyyy-MM-dd")
            : string.Empty;
        item.Developers = (details.InvolvedCompanies ?? new()).Where(value => value?.Developer == true && value.Company != null)
            .Select(value => value.Company.Name).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        item.Publishers = (details.InvolvedCompanies ?? new()).Where(value => value?.Publisher == true && value.Company != null)
            .Select(value => value.Company.Name).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        item.Genres = (details.Genres ?? new()).Select(value => value?.Name).Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (!string.IsNullOrWhiteSpace(coverPath) && File.Exists(coverPath)) item.CoverArtPath = coverPath;

        RomRegistryFile registry = LoadRegistry();
        RomRegistryRecord record = registry.Records.FirstOrDefault(value => string.Equals(value.Id, item.Id, StringComparison.OrdinalIgnoreCase));
        RomRegistryRecord updated = FromData(item);
        if (record == null) registry.Records.Add(updated);
        else registry.Records[registry.Records.IndexOf(record)] = updated;
        SaveAtomic(RegistryPath, registry);
        OnItemReady?.Invoke(item);
        RomMediaVisual.RefreshItem(item);
    }

    internal static string PlatformStatus(string platformId)
    {
        string path = GetPlatformSourcePath(platformId);
        int count = GetPlatformItems(platformId).Count;
        return Directory.Exists(path) ? $"{count} ROM{(count == 1 ? string.Empty : "s")} found" : "Folder not found";
    }

    private static RomRegistryFile LoadRegistry()
    {
        if (!File.Exists(RegistryPath)) return new RomRegistryFile();
        RomRegistryFile value = JsonConvert.DeserializeObject<RomRegistryFile>(File.ReadAllText(RegistryPath)) ?? new RomRegistryFile();
        value.Version = 3;
        value.Records ??= new List<RomRegistryRecord>();
        return value;
    }

    private static IEnumerable<string> DiscoverEntries(string file, RomPlatform platform)
    {
        string extension = Path.GetExtension(file).TrimStart('.');
        if (platform.Supports(extension)) return new[] { string.Empty };
        if (!string.Equals(extension, "zip", StringComparison.OrdinalIgnoreCase)) return Array.Empty<string>();
        try
        {
            using ZipArchive archive = ZipFile.OpenRead(file);
            List<string> entries = archive.Entries.Where(entry => entry.Length > 0 && platform.Supports(Path.GetExtension(entry.FullName)))
                .Select(entry => entry.FullName).ToList();
            List<string> descriptors = entries.Where(entry =>
                string.Equals(Path.GetExtension(entry), ".cue", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetExtension(entry), ".m3u", StringComparison.OrdinalIgnoreCase)).ToList();
            return descriptors.Count > 0 ? descriptors : entries;
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"Skipping unreadable ROM archive '{file}': {ex.Message}");
            return Array.Empty<string>();
        }
    }

    private static string ResolveSourcePath(string root, string configured, string fallback)
    {
        string value = string.IsNullOrWhiteSpace(configured) ? fallback : configured;
        return Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(root, value));
    }

    private static string LocationKey(RomRegistryRecord value) =>
        $"{value.PlatformId}|{Path.GetFullPath(value.ContentPath ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant()}|{(value.ArchiveEntry ?? string.Empty).Replace('\\', '/').ToUpperInvariant()}";

    private static string Fingerprint(string path, string platform, string archiveEntry)
    {
        using SHA256 sha = SHA256.Create();
        using FileStream stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        byte[] fileHash = sha.ComputeHash(stream);
        byte[] suffix = Encoding.UTF8.GetBytes("|" + platform.ToLowerInvariant() + "|" + (archiveEntry ?? string.Empty).Replace('\\', '/').ToLowerInvariant());
        byte[] combined = new byte[fileHash.Length + suffix.Length];
        Buffer.BlockCopy(fileHash, 0, combined, 0, fileHash.Length);
        Buffer.BlockCopy(suffix, 0, combined, fileHash.Length, suffix.Length);
        return BitConverter.ToString(sha.ComputeHash(combined)).Replace("-", string.Empty).ToLowerInvariant();
    }

    private static string TitleFrom(string path, string archiveEntry)
    {
        string value = Path.GetFileNameWithoutExtension(string.IsNullOrWhiteSpace(archiveEntry) ? path : archiveEntry);
        return RomTitle.Clean(value);
    }

    private static string FindCover(string path, string archiveEntry, string configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        string directory = Path.GetDirectoryName(path);
        string baseName = Path.GetFileNameWithoutExtension(path);
        string entryName = Path.GetFileNameWithoutExtension(archiveEntry ?? string.Empty);
        string cleanEntryName = RomTitle.Clean(entryName, string.Empty);
        string cleanBaseName = RomTitle.Clean(baseName, string.Empty);
        foreach (string name in new[] { cleanEntryName, cleanBaseName, entryName, baseName, baseName + ".cover", "cover", "boxart" }
            .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase))
        foreach (string extension in new[] { ".png", ".jpg", ".jpeg" })
        {
            string candidate = Path.Combine(directory, name + extension);
            if (File.Exists(candidate)) return candidate;
        }
        return string.Empty;
    }

    private static RomData ToData(RomRegistryRecord value) => new()
    {
        Id = value.Id, Title = value.Title, ContentPath = value.ContentPath, ArchiveEntry = value.ArchiveEntry,
        PlatformId = value.PlatformId, CoverArtPath = value.CoverArtPath, CoreOverride = value.CoreOverride, CaseColor = value.CaseColor,
        IgdbId = value.IgdbId, Summary = value.Summary, ReleaseDate = value.ReleaseDate,
        Developers = value.Developers ?? Array.Empty<string>(), Publishers = value.Publishers ?? Array.Empty<string>(), Genres = value.Genres ?? Array.Empty<string>(),
        Fingerprint = value.Fingerprint, FileSize = value.FileSize, LastWriteUtcTicks = value.LastWriteUtcTicks
    };

    private static RomRegistryRecord FromData(RomData value) => new()
    {
        Id = value.Id, Title = value.Title, ContentPath = value.ContentPath, ArchiveEntry = value.ArchiveEntry,
        PlatformId = value.PlatformId, CoverArtPath = value.CoverArtPath, CoreOverride = value.CoreOverride, CaseColor = value.CaseColor,
        IgdbId = value.IgdbId, Summary = value.Summary, ReleaseDate = value.ReleaseDate,
        Developers = value.Developers ?? Array.Empty<string>(), Publishers = value.Publishers ?? Array.Empty<string>(), Genres = value.Genres ?? Array.Empty<string>(),
        Fingerprint = value.Fingerprint, FileSize = value.FileSize, LastWriteUtcTicks = value.LastWriteUtcTicks
    };

    private static void SaveAtomic(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string staging = path + "." + Guid.NewGuid().ToString("N") + ".staging";
        try
        {
            File.WriteAllText(staging, JsonConvert.SerializeObject(value, Formatting.Indented));
            if (File.Exists(path)) File.Replace(staging, path, null, true); else File.Move(staging, path);
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }
}
