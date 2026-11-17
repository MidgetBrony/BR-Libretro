using BR_MediaAPI;
using ModsPanel;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BR_Libretro;

internal static class RomConsoleUi
{
    private static readonly Dictionary<string, int> SelectedRom = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, int> RomPage = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> RomFilter = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<string> SectionIds = new();
    private static readonly Dictionary<string, MediaLibraryGroupDefinition> GroupDefinitions = new(StringComparer.OrdinalIgnoreCase);
    private const int PageSize = 8;

    internal static IReadOnlyList<MediaLibraryGroupDefinition> CreateGroups(CoreMap coreMap)
    {
        var groups = new List<MediaLibraryGroupDefinition>();
        HashSet<string> installed = CoreInstaller.GetInstalledCoreIds().ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (RomPlatform platform in RomPlatformCatalog.All
            .Where(value => HasInstalledCore(value, installed, coreMap))
            .OrderBy(value => value.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            if (GroupDefinitions.TryGetValue(platform.Id, out MediaLibraryGroupDefinition existing))
            {
                groups.Add(existing);
                continue;
            }
            string sectionId = RomMedia.Key + ".console." + platform.Id;
            SectionIds.Add(sectionId);
            ModSection section = ModsPanelApi.RegisterSection(sectionId, platform.DisplayName, 125);
            section.VisibleInModSettings = false;
            section.Clear()
                .AddFolder("folder", platform.DisplayName + " ROM Folder",
                    () => RomLibrarySystem.GetPlatformSourcePath(platform.Id),
                    path => RomLibrarySystem.SetPlatformSourcePath(platform.Id, path),
                    () => RomLibrarySystem.PlatformStatus(platform.Id),
                    RomLibrarySystem.Reload,
                    "Select the " + platform.DisplayName + " ROM folder")
                .AddButton("manage", "ROM list and core overrides", "MANAGE", () => Open(platform))
                .AddButton("igdb", "IGDB metadata and cover artwork", "METADATA", () => IgdbMetadata.Open(platform))
                .AddButton("core-installer", "Install and manage Libretro cores", "CORES", CoreInstaller.OpenMenu)
                .AddButton("configure-inputs", "ROM keyboard / gamepad bindings", "CONTROLS", InputBindings.OpenMenu)
                .AddButton("refresh", "Rescan this and all console folders", "REFRESH", RomLibrarySystem.Reload)
                .AddLabel("help", "Physical media follows the console. Core choices only affect emulation.");
            var definition = new MediaLibraryGroupDefinition
            {
                Key = platform.Id,
                DisplayName = ShortName(platform),
                Includes = item => item is RomData rom && string.Equals(rom.PlatformId, platform.Id, StringComparison.OrdinalIgnoreCase),
                SettingsSection = section
            };
            GroupDefinitions[platform.Id] = definition;
            groups.Add(definition);
        }
        return groups;
    }

    internal static void RefreshGroups(CoreMap coreMap)
    {
        IReadOnlyList<MediaLibraryGroupDefinition> groups = CreateGroups(coreMap);
        if (!MediaApi.UpdateLibraryGroups(RomMedia.Key, groups))
            MelonLoader.MelonLogger.Warning("Could not refresh BR-Libretro console tabs because ROM media is not registered.");
    }

    private static bool HasInstalledCore(RomPlatform platform, HashSet<string> installed, CoreMap coreMap)
    {
        if (platform.PreferredCores.Any(installed.Contains)) return true;
        if (coreMap?.Extensions == null) return false;
        return platform.Extensions.Any(extension =>
            coreMap.Extensions.TryGetValue(extension, out string mapped) && installed.Contains(mapped));
    }

    internal static void Dispose()
    {
        foreach (string id in SectionIds) ModsPanelApi.UnregisterSection(id);
        SectionIds.Clear(); GroupDefinitions.Clear(); SelectedRom.Clear(); RomPage.Clear(); RomFilter.Clear();
    }

    private static void Open(RomPlatform platform)
    {
        IReadOnlyList<RomData> roms = RomLibrarySystem.GetPlatformItems(platform.Id);
        int selected = SelectedRom.TryGetValue(platform.Id, out int saved) ? Math.Clamp(saved, 0, Math.Max(0, roms.Count - 1)) : 0;
        SelectedRom[platform.Id] = selected;
        RomData rom = roms.Count > 0 ? roms[selected] : null;
        List<string> coreIds = InstalledCores(platform);
        string platformOverride = RomLibrarySystem.GetPlatformCoreOverride(platform.Id);
        foreach (string missing in new[] { rom?.CoreOverride, platformOverride }.Where(value => !string.IsNullOrWhiteSpace(value)))
            if (!coreIds.Contains(missing, StringComparer.OrdinalIgnoreCase)) coreIds.Add(missing);
        List<string> coreLabels = new() { "Auto — recommended / console default" };
        coreLabels.AddRange(coreIds.Select(id => platform.PreferredCores.Contains(id, StringComparer.OrdinalIgnoreCase)
            ? id + " — recommended"
            : File.Exists(Path.Combine(DataPaths.Cores, id + "_libretro.dll")) ? id + " — installed" : id + " — not installed"));

        ModMenu menu = ModsUi.CreateMenu(RomMedia.Key + ".manager." + platform.Id,
            ShortName(platform) + " ROMs", RomLibrarySystem.PlatformStatus(platform.Id));
        menu.Eyebrow = "BR-LIBRETRO ROM MEDIA";
        menu.CloseText = "BACK";
        if (roms.Count == 0)
        {
            menu.AddLabel("No ROMs were discovered in this console folder.");
            menu.AddButton("Rescan folders", () => { RomLibrarySystem.Reload(); Open(platform); });
            menu.Show();
            return;
        }

        string filter = RomFilter.TryGetValue(platform.Id, out string savedFilter) ? savedFilter : string.Empty;
        List<(RomData rom, int index)> filtered = roms.Select((value, index) => (value, index))
            .Where(pair => string.IsNullOrWhiteSpace(filter) || pair.value.DisplayName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
            .Select(pair => (pair.value, pair.index)).ToList();
        int pageCount = Math.Max(1, (filtered.Count + PageSize - 1) / PageSize);
        int page = RomPage.TryGetValue(platform.Id, out int savedPage) ? Math.Clamp(savedPage, 0, pageCount - 1) : 0;
        RomPage[platform.Id] = page;
        menu.AddTextInput("Find ROM", () => RomFilter.TryGetValue(platform.Id, out string value) ? value : string.Empty,
            value => RomFilter[platform.Id] = value ?? string.Empty, "Type part of a title");
        menu.AddButton("Apply search", () => { RomPage[platform.Id] = 0; Open(platform); },
            $"{filtered.Count} matching ROM{(filtered.Count == 1 ? string.Empty : "s")}");
        foreach ((RomData listItem, int index) in filtered.Skip(page * PageSize).Take(PageSize))
        {
            string detail = index == selected ? "Selected · " : string.Empty;
            detail += string.IsNullOrWhiteSpace(listItem.CoreOverride) ? "Automatic core" : listItem.CoreOverride;
            menu.AddButton(listItem.DisplayName, () => { SelectedRom[platform.Id] = index; Open(platform); }, detail);
        }
        if (page > 0) menu.AddButton("Previous ROMs", () => { RomPage[platform.Id] = page - 1; Open(platform); });
        if (page + 1 < pageCount) menu.AddButton("Next ROMs", () => { RomPage[platform.Id] = page + 1; Open(platform); });
        menu.AddHeading("Selected ROM");
        menu.AddLabel(rom.DisplayName);
        menu.AddButton(rom.HasIgdbMetadata ? "Change IGDB match" : "Find IGDB metadata",
            () => IgdbMetadata.OpenForRom(rom, () => Open(platform)),
            rom.HasIgdbMetadata ? "Replace the cached title, details and cover" : "Search by cleaned ROM title and console");
        menu.AddDropdown("Core for selected ROM", () => coreLabels,
            () => CoreIndex(coreIds, rom.CoreOverride),
            index =>
            {
                RomLibrarySystem.SetRomCoreOverride(rom.Id, index <= 0 ? string.Empty : coreIds[index - 1]);
                ModsUi.ShowToast(index <= 0 ? $"{rom.DisplayName} now uses automatic core selection." : $"{rom.DisplayName} will use {coreIds[index - 1]}.");
            });
        menu.AddDropdown("Default core for this console", () => coreLabels,
            () => CoreIndex(coreIds, platformOverride),
            index => RomLibrarySystem.SetPlatformCoreOverride(platform.Id, index <= 0 ? string.Empty : coreIds[index - 1]));
        menu.AddButton("Rescan console folders", () => { RomLibrarySystem.Reload(); Open(platform); });
        menu.AddLabel("Auto tries the console default, configured file mapping, then the platform's recommended installed cores. Any installed core can be selected for experimentation.");
        menu.Show();
    }

    private static List<string> InstalledCores(RomPlatform platform)
    {
        IReadOnlyList<string> installed = CoreInstaller.GetInstalledCoreIds();
        return installed.OrderBy(id => Array.FindIndex(platform.PreferredCores,
                    preferred => string.Equals(preferred, id, StringComparison.OrdinalIgnoreCase)) < 0 ? 1 : 0)
            .ThenBy(id => id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static int CoreIndex(IReadOnlyList<string> ids, string selected)
    {
        if (string.IsNullOrWhiteSpace(selected)) return 0;
        for (int i = 0; i < ids.Count; i++) if (string.Equals(ids[i], selected, StringComparison.OrdinalIgnoreCase)) return i + 1;
        return 0;
    }

    private static string ShortName(RomPlatform platform) => platform.Id switch
    {
        "genesis" => "Genesis",
        "psx" => "PlayStation",
        "gbc" => "GBC",
        "gba" => "GBA",
        "gb" => "GB",
        _ => platform.Id.ToUpperInvariant()
    };
}
