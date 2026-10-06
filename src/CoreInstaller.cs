using MelonLoader;
using ModsPanel;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using UnityEngine.Networking;

namespace BR_Libretro;

internal static class CoreInstaller
{
    private const string OwnerId = "com.midgetbrony.br-libretro.core-installer";
    private const string BuildbotRoot = "https://buildbot.libretro.com/nightly/windows/x86_64/latest/";
    private const string CoreInfoUrl = "https://buildbot.libretro.com/assets/frontend/info.zip";
    private const long MaximumArchiveBytes = 512L * 1024 * 1024;
    private const long MaximumCoreBytes = 1024L * 1024 * 1024;
    private const int CataloguePageSize = 10;
    private static readonly Regex CatalogueLink = new(
        "href=[\\\"']([^\\\"']*/)?(?<file>[^/\\\"']+_libretro\\.dll\\.zip)[\\\"']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SafeExtension = new("^[a-z0-9][a-z0-9_+-]{0,31}$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SafeCoreId = new("^[a-z0-9][a-z0-9_-]{0,95}$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private sealed class CorePackage
    {
        internal CorePackage(string id, string name = null, string systems = null,
            bool needsWarning = false, string archiveName = null)
        {
            Id = id;
            Name = string.IsNullOrWhiteSpace(name) ? FriendlyName(id) : name;
            Systems = systems ?? string.Empty;
            NeedsWarning = needsWarning;
            ArchiveName = archiveName ?? id + "_libretro.dll.zip";
        }

        internal string Id { get; }
        internal string Name { get; private set; }
        internal string Systems { get; private set; }
        internal bool NeedsWarning { get; }
        internal string ArchiveName { get; }
        internal string[] SupportedExtensions { get; private set; } = Array.Empty<string>();
        internal bool IsExperimental { get; private set; }
        internal string FileName => ArchiveName.Substring(0, ArchiveName.Length - 4);
        internal string DownloadUrl => BuildbotRoot + ArchiveName;
        internal string Destination => Path.Combine(DataPaths.Cores, FileName);
        internal bool IsInstalled => File.Exists(Destination);

        internal void ApplyInfo(Dictionary<string, string> info)
        {
            if (info.TryGetValue("display_name", out string displayName) && !string.IsNullOrWhiteSpace(displayName))
                Name = displayName;
            if (info.TryGetValue("systemname", out string systemName) && !string.IsNullOrWhiteSpace(systemName))
                Systems = systemName;
            if (info.TryGetValue("supported_extensions", out string extensions))
                SupportedExtensions = extensions.Split('|').Select(value => value.Trim().TrimStart('.'))
                    .Where(value => SafeExtension.IsMatch(value)).Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
            if (info.TryGetValue("is_experimental", out string experimental))
                IsExperimental = string.Equals(experimental, "true", StringComparison.OrdinalIgnoreCase);
        }
    }

    private static readonly CorePackage[] Recommended =
    {
        new("mesen", "Mesen", "NES / Famicom Disk System"),
        new("snes9x", "Snes9x", "Super Nintendo"),
        new("gambatte", "Gambatte", "Game Boy / Game Boy Color"),
        new("mgba", "mGBA", "Game Boy Advance"),
        new("genesis_plus_gx", "Genesis Plus GX", "Mega Drive / Genesis"),
        new("pcsx_rearmed", "PCSX-ReARMed", "PlayStation"),
        new("prboom", "PrBoom", "Doom WAD", true)
    };

    private static CoreMap coreMap;
    private static List<CorePackage> catalogue;
    private static bool installing, catalogueLoading;
    private static string catalogueFilter = string.Empty;
    private static int cataloguePage;
    private static string editingExtension, editingCore, originalExtension;
    private static Action mappingReturn;

    internal static void Initialize(CoreMap map) => coreMap = map;

    internal static void OpenMenu()
    {
        ModMenu menu = CreateMenu("", "Libretro Core Manager",
            "Install cores and choose which game files use them");
        int installed = EnumerateInstalled().Count;
        menu.AddButton("Recommended cores", OpenRecommendedMenu,
            "Curated defaults for BR-Libretro");
        menu.AddButton("Browse all official cores", OpenCatalogue,
            catalogue == null ? "Load the live Libretro catalogue" : $"{catalogue.Count} cores cached this session");
        menu.AddButton("Installed cores", OpenInstalledMenu,
            installed == 1 ? "1 core installed" : $"{installed} cores installed");
        menu.AddButton("File type mappings", () => OpenMappingsMenu(OpenMenu),
            $"{coreMap?.Extensions.Count ?? 0} extension mappings");
        menu.AddSpacer(12f);
        menu.AddLabel("Cores come from Libretro's official nightly Windows x64 buildbot. Games, BIOS and firmware are not included.");
        menu.Show();
    }

    private static ModMenu CreateMenu(string suffix, string title, string subtitle)
    {
        ModMenu menu = ModsUi.CreateMenu(OwnerId + suffix, title, subtitle);
        menu.Eyebrow = "BR-LIBRETRO";
        menu.CloseText = "BACK";
        return menu;
    }

    private static void OpenRecommendedMenu()
    {
        ModMenu menu = CreateMenu(".recommended", "Recommended Cores",
            "Defaults already recognized by BR-Libretro");
        foreach (CorePackage item in Recommended)
        {
            CorePackage package = item;
            menu.AddButton(package.Name, () => OpenCoreDetails(package, OpenRecommendedMenu),
                PackageDetail(package));
        }
        menu.AddButton("Back", OpenMenu);
        menu.Show();
    }

    private static void OpenCatalogue()
    {
        if (catalogue != null) { ShowCataloguePage(); return; }
        RefreshCatalogue();
    }

    private static void RefreshCatalogue()
    {
        if (catalogueLoading || installing)
        {
            ModsUi.ShowToast("Another core-manager operation is already in progress.");
            return;
        }
        catalogueLoading = true;
        ModsUi.CloseMenu();
        ModsUi.ShowToast("Loading the official Libretro core catalogue...", 8f);
        MelonCoroutines.Start(DownloadCatalogue());
    }

    private static IEnumerator DownloadCatalogue()
    {
        using UnityWebRequest request = UnityWebRequest.Get(BuildbotRoot);
        request.timeout = 60;
        request.SetRequestHeader("User-Agent", "BR-Libretro/0.4.12");
        UnityWebRequestAsyncOperation operation = null;
        string failure = null;
        try { operation = request.SendWebRequest(); }
        catch (Exception ex) { failure = ex.Message; }
        if (operation != null) yield return operation;
        if (failure == null && request.result != UnityWebRequest.Result.Success)
            failure = string.IsNullOrWhiteSpace(request.error) ? $"HTTP {request.responseCode}" : request.error;

        if (failure == null)
        {
            try
            {
                var found = new Dictionary<string, CorePackage>(StringComparer.OrdinalIgnoreCase);
                foreach (Match match in CatalogueLink.Matches(request.downloadHandler.text ?? string.Empty))
                {
                    string archiveName = Uri.UnescapeDataString(match.Groups["file"].Value);
                    const string suffix = "_libretro.dll.zip";
                    if (!archiveName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
                    string id = archiveName.Substring(0, archiveName.Length - suffix.Length);
                    if (!SafeCoreId.IsMatch(id) || found.ContainsKey(id)) continue;
                    CorePackage recommended = Recommended.FirstOrDefault(p =>
                        string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
                    found[id] = recommended ?? new CorePackage(id, archiveName: archiveName);
                }
                catalogue = found.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
                if (catalogue.Count == 0) throw new InvalidDataException("No core packages were found in the catalogue.");
                cataloguePage = 0;
                MelonLogger.Msg($"Loaded {catalogue.Count} Windows x64 cores from the Libretro buildbot.");
            }
            catch (Exception ex) { failure = ex.Message; }
        }

        if (failure == null)
        {
            using UnityWebRequest infoRequest = UnityWebRequest.Get(CoreInfoUrl);
            infoRequest.timeout = 60;
            infoRequest.SetRequestHeader("User-Agent", "BR-Libretro/0.4.12");
            UnityWebRequestAsyncOperation infoOperation = null;
            try { infoOperation = infoRequest.SendWebRequest(); }
            catch (Exception ex) { MelonLogger.Warning("Could not request Libretro core metadata: " + ex.Message); }
            if (infoOperation != null) yield return infoOperation;
            if (infoRequest.result == UnityWebRequest.Result.Success)
            {
                try { ApplyCoreInfo(infoRequest.downloadHandler.data); }
                catch (Exception ex) { MelonLogger.Warning("Could not read Libretro core metadata: " + ex.Message); }
            }
            else MelonLogger.Warning("Could not download Libretro core metadata: " + infoRequest.error);

            catalogue = catalogue.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        catalogueLoading = false;
        if (failure != null)
        {
            MelonLogger.Error("Could not load the Libretro core catalogue: " + failure);
            ModsUi.ShowToast("Could not load the core catalogue: " + failure, 10f);
            OpenMenu();
        }
        else ShowCataloguePage();
    }

    private static void ShowCataloguePage()
    {
        List<CorePackage> filtered = FilteredCatalogue();
        int pages = Math.Max(1, (filtered.Count + CataloguePageSize - 1) / CataloguePageSize);
        cataloguePage = Math.Max(0, Math.Min(cataloguePage, pages - 1));
        ModMenu menu = CreateMenu(".catalogue", "All Official Cores",
            $"Page {cataloguePage + 1} of {pages} - {filtered.Count} matches");
        menu.AddTextInput("Search by core or system", () => catalogueFilter,
            value => catalogueFilter = value ?? string.Empty, "Example: Nintendo, MAME, mesen");
        menu.AddButton("Apply search", () => { cataloguePage = 0; ShowCataloguePage(); },
            string.IsNullOrWhiteSpace(catalogueFilter) ? "Showing every available core" : catalogueFilter);

        foreach (CorePackage item in filtered.Skip(cataloguePage * CataloguePageSize).Take(CataloguePageSize))
        {
            CorePackage package = item;
            menu.AddButton(package.Name, () => OpenCoreDetails(package, ShowCataloguePage),
                PackageDetail(package));
        }
        if (cataloguePage > 0)
            menu.AddButton("Previous page", () => { cataloguePage--; ShowCataloguePage(); });
        if (cataloguePage + 1 < pages)
            menu.AddButton("Next page", () => { cataloguePage++; ShowCataloguePage(); });
        menu.AddButton("Refresh official catalogue", RefreshCatalogue);
        menu.AddButton("Back", OpenMenu);
        menu.Show();
    }

    private static List<CorePackage> FilteredCatalogue()
    {
        if (catalogue == null) return new List<CorePackage>();
        string filter = (catalogueFilter ?? string.Empty).Trim();
        if (filter.Length == 0) return catalogue;
        return catalogue.Where(p => p.Id.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
            p.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
            p.Systems.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
            p.SupportedExtensions.Any(value => value.IndexOf(filter.TrimStart('.'), StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
    }

    private static void ApplyCoreInfo(byte[] zipBytes)
    {
        if (zipBytes == null || zipBytes.Length == 0) return;
        var packages = catalogue.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
        int enriched = 0;
        using var memory = new MemoryStream(zipBytes, false);
        using var archive = new ZipArchive(memory, ZipArchiveMode.Read, false);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string fileName = Path.GetFileName(entry.FullName);
            const string suffix = "_libretro.info";
            if (!fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
            string id = fileName.Substring(0, fileName.Length - suffix.Length);
            if (!packages.TryGetValue(id, out CorePackage package)) continue;

            var info = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using Stream stream = entry.Open();
            using var reader = new StreamReader(stream);
            while (!reader.EndOfStream)
            {
                string line = reader.ReadLine();
                Match match = Regex.Match(line ?? string.Empty,
                    "^\\s*(?<key>[a-z0-9_]+)\\s*=\\s*\\\"(?<value>.*)\\\"\\s*$",
                    RegexOptions.IgnoreCase);
                if (match.Success) info[match.Groups["key"].Value] = match.Groups["value"].Value;
            }
            package.ApplyInfo(info);
            enriched++;
        }
        MelonLogger.Msg($"Applied official metadata to {enriched} Libretro cores.");
    }

    private static void OpenInstalledMenu()
    {
        List<CorePackage> installed = EnumerateInstalled();
        ModMenu menu = CreateMenu(".installed", "Installed Cores",
            installed.Count == 1 ? "1 core available" : $"{installed.Count} cores available");
        if (installed.Count == 0) menu.AddLabel("No libretro cores are installed yet.");
        foreach (CorePackage item in installed)
        {
            CorePackage package = item;
            menu.AddButton(package.Name, () => OpenCoreDetails(package, OpenInstalledMenu),
                package.Id + " | Installed");
        }
        menu.AddButton("Back", OpenMenu);
        menu.Show();
    }

    private static List<CorePackage> EnumerateInstalled()
    {
        if (!Directory.Exists(DataPaths.Cores)) return new List<CorePackage>();
        var result = new List<CorePackage>();
        foreach (string path in Directory.EnumerateFiles(DataPaths.Cores, "*_libretro.dll", SearchOption.TopDirectoryOnly))
        {
            string fileName = Path.GetFileName(path);
            const string suffix = "_libretro.dll";
            string id = fileName.Substring(0, fileName.Length - suffix.Length);
            CorePackage known = Recommended.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))
                ?? catalogue?.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
            result.Add(known ?? new CorePackage(id, archiveName: fileName + ".zip"));
        }
        return result.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void OpenCoreDetails(CorePackage package, Action back)
    {
        string mapped = coreMap == null ? string.Empty : string.Join(", ", coreMap.Extensions
            .Where(pair => string.Equals(pair.Value, package.Id, StringComparison.OrdinalIgnoreCase))
            .Select(pair => "." + pair.Key).OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
        ModMenu menu = CreateMenu(".core." + package.Id, package.Name, package.Id + "_libretro.dll");
        menu.AddLabel(string.IsNullOrWhiteSpace(package.Systems) ? "Official Libretro Windows x64 core." : package.Systems);
        menu.AddLabel(package.IsInstalled ? "Status: Installed" : "Status: Not installed");
        if (package.IsExperimental) menu.AddLabel("Libretro marks this core as experimental.");
        if (package.SupportedExtensions.Length > 0)
            menu.AddLabel("Supported files: ." + string.Join(", .", package.SupportedExtensions));
        menu.AddLabel(mapped.Length == 0 ? "Mapped file types: None" : "Mapped file types: " + mapped);
        menu.AddButton(package.IsInstalled ? "Update core" : "Install core",
            () => RequestInstall(package, back), "Download and validate the latest official nightly build");
        menu.AddButton("Configure file mappings", () => OpenCoreMappingsMenu(package, back),
            "Choose which file extensions launch this core");
        if (package.SupportedExtensions.Length > 0)
            menu.AddButton("Suggested mappings", () => OpenSuggestedMappingsMenu(package, back),
                "Review the file types advertised by this core");
        menu.AddButton("Back", back);
        menu.Show();
    }

    private static void OpenSuggestedMappingsMenu(CorePackage package, Action detailsBack)
    {
        Action reopen = () => OpenSuggestedMappingsMenu(package, detailsBack);
        ModMenu menu = CreateMenu(".suggested." + package.Id, package.Name + " File Types",
            "Select one extension at a time; existing assignments are never silently replaced");
        foreach (string item in package.SupportedExtensions)
        {
            string extension = item;
            coreMap.Extensions.TryGetValue(extension, out string current);
            string detail = string.IsNullOrWhiteSpace(current) ? "Not mapped" :
                string.Equals(current, package.Id, StringComparison.OrdinalIgnoreCase) ? "Already uses " + package.Id : "Currently uses " + current;
            menu.AddButton("." + extension, () => RequestSuggestedMapping(package, extension, reopen), detail);
        }
        menu.AddButton("Back", () => OpenCoreDetails(package, detailsBack));
        menu.Show();
    }

    private static void RequestSuggestedMapping(CorePackage package, string extension, Action back)
    {
        coreMap.Extensions.TryGetValue(extension, out string current);
        if (string.Equals(current, package.Id, StringComparison.OrdinalIgnoreCase))
        {
            ModsUi.ShowToast("." + extension + " already uses " + package.Id + ".");
            return;
        }
        if (string.IsNullOrWhiteSpace(current))
        {
            AssignSuggestedMapping(package, extension, back);
            return;
        }

        ModMenu menu = CreateMenu(".replace-suggestion", "Replace File Mapping?", "." + extension);
        menu.AddLabel("." + extension + " currently launches " + current + ". Replace it with " + package.Id + "?");
        menu.AddButton("Use " + package.Id, () => AssignSuggestedMapping(package, extension, back),
            "The current config will be backed up");
        menu.AddButton("Cancel", back);
        menu.Show();
    }

    private static void AssignSuggestedMapping(CorePackage package, string extension, Action back)
    {
        try
        {
            coreMap.Extensions[extension] = package.Id;
            coreMap.Save();
            ModsUi.ShowToast("." + extension + " now uses " + package.Id + ".");
            back();
        }
        catch (Exception ex)
        {
            MelonLogger.Error("Could not save suggested core mapping: " + ex);
            ModsUi.ShowToast("Could not save the mapping: " + ex.Message, 8f);
        }
    }

    private static void RequestInstall(CorePackage package, Action back)
    {
        if (installing || catalogueLoading)
        {
            ModsUi.ShowToast("Another core-manager operation is already in progress.");
            return;
        }
        if (LibretroRuntime.Current != null)
        {
            ModsUi.ShowToast("Stop the running game before installing or updating a core.", 6f);
            return;
        }
        if (package.NeedsWarning) { ShowCompatibilityWarning(package, back); return; }
        BeginInstall(package, back);
    }

    private static void ShowCompatibilityWarning(CorePackage package, Action back)
    {
        ModMenu menu = CreateMenu(".warning", "PrBoom Compatibility Warning", "Nightly core build");
        menu.AddLabel("A previous PrBoom nightly build crashed with Doom on this setup. The currently installed stable core may be safer.");
        menu.AddButton(package.IsInstalled ? "Replace with nightly PrBoom" : "Install nightly PrBoom",
            () => BeginInstall(package, back), "Experimental - the previous DLL will be backed up");
        menu.AddButton("Back", () => OpenCoreDetails(package, back));
        menu.Show();
    }

    private static void BeginInstall(CorePackage package, Action back)
    {
        if (installing || catalogueLoading) return;
        installing = true;
        ModsUi.CloseMenu();
        ModsUi.ShowToast($"Downloading {package.Name} from Libretro...", 8f);
        MelonCoroutines.Start(DownloadAndInstall(package, back));
    }

    private static IEnumerator DownloadAndInstall(CorePackage package, Action back)
    {
        UnityWebRequest request = null;
        string failure = null, installedHash = null, backupPath = null;
        Directory.CreateDirectory(DataPaths.Temp);
        string archivePath = Path.Combine(DataPaths.Temp,
            package.ArchiveName + "." + Guid.NewGuid().ToString("N") + ".download");
        try
        {
            request = new UnityWebRequest(package.DownloadUrl, "GET",
                new DownloadHandlerFile(archivePath), null);
            request.timeout = 120;
            request.SetRequestHeader("User-Agent", "BR-Libretro/0.4.12");
        }
        catch (Exception ex) { failure = ex.Message; }

        if (request != null)
        {
            UnityWebRequestAsyncOperation operation = null;
            try { operation = request.SendWebRequest(); }
            catch (Exception ex) { failure = ex.Message; }
            if (operation != null) yield return operation;
            if (failure == null && request.result != UnityWebRequest.Result.Success)
                failure = string.IsNullOrWhiteSpace(request.error) ? $"HTTP {request.responseCode}" : request.error;
            if (failure == null)
            {
                try
                {
                    var archive = new FileInfo(archivePath);
                    if (!archive.Exists || archive.Length == 0) throw new InvalidDataException("Libretro returned an empty download.");
                    if (archive.Length > MaximumArchiveBytes) throw new InvalidDataException("The downloaded archive is unexpectedly large.");
                    InstallArchive(package, archivePath, out installedHash, out backupPath);
                }
                catch (Exception ex) { failure = ex.Message; }
            }
        }

        request?.Dispose();
        try { if (File.Exists(archivePath)) File.Delete(archivePath); }
        catch (Exception ex) { MelonLogger.Warning("Could not remove the temporary core archive: " + ex.Message); }
        installing = false;
        if (failure != null)
        {
            MelonLogger.Error($"Core installer failed for {package.Name}: {failure}");
            ModsUi.ShowToast($"Could not install {package.Name}: {failure}", 10f);
        }
        else
        {
            string backupMessage = backupPath == null ? string.Empty : $" Previous core: {backupPath}";
            MelonLogger.Msg($"Installed {package.Name} from {package.DownloadUrl}. SHA-256: {installedHash}.{backupMessage}");
            ModsUi.ShowToast($"{package.Name} installed successfully.", 7f);
        }
        OpenCoreDetails(package, back);
    }

    private static void OpenMappingsMenu(Action back)
    {
        ModMenu menu = CreateMenu(".mappings", "File Type Mappings",
            "Choose which core opens each game-file extension");
        if (coreMap == null || coreMap.Extensions.Count == 0) menu.AddLabel("No file mappings are configured.");
        else foreach (KeyValuePair<string, string> item in coreMap.Extensions.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            string extension = item.Key;
            menu.AddButton("." + extension, () => OpenMappingEditor(extension, null, () => OpenMappingsMenu(back)),
                item.Value + (IsCoreInstalled(item.Value) ? " | Installed" : " | Core not installed"));
        }
        menu.AddButton("Add file mapping", () => OpenMappingEditor(null, null, () => OpenMappingsMenu(back)),
            "Associate another extension with a core ID");
        menu.AddButton("Restore default mappings", () => ConfirmRestoreMappings(back),
            "NES, SNES, GB, GBA, Mega Drive, PlayStation and WAD defaults");
        menu.AddButton("Back", back);
        menu.Show();
    }

    private static void OpenCoreMappingsMenu(CorePackage package, Action detailsBack)
    {
        Action reopen = () => OpenCoreMappingsMenu(package, detailsBack);
        List<string> extensions = coreMap.Extensions
            .Where(pair => string.Equals(pair.Value, package.Id, StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Key).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList();
        ModMenu menu = CreateMenu(".core-mappings." + package.Id, package.Name + " Mappings",
            "File extensions assigned to " + package.Id);
        if (extensions.Count == 0) menu.AddLabel("No file types currently launch this core.");
        foreach (string item in extensions)
        {
            string extension = item;
            menu.AddButton("." + extension, () => OpenMappingEditor(extension, package.Id, reopen), "Edit or remove mapping");
        }
        menu.AddButton("Add file extension", () => OpenMappingEditor(null, package.Id, reopen),
            "Example: nes, chd, iso or zip");
        menu.AddButton("Back", () => OpenCoreDetails(package, detailsBack));
        menu.Show();
    }

    private static void OpenMappingEditor(string extension, string preferredCore, Action back)
    {
        originalExtension = extension;
        editingExtension = extension ?? string.Empty;
        editingCore = preferredCore ?? (extension != null && coreMap.Extensions.TryGetValue(extension, out string value)
            ? value : string.Empty);
        mappingReturn = back;
        ModMenu menu = CreateMenu(".mapping-editor", extension == null ? "Add File Mapping" : "Edit File Mapping",
            "Core ID is the DLL name before _libretro.dll");
        menu.AddTextInput("File extension", () => editingExtension, value => editingExtension = value ?? string.Empty, "Example: nes");
        menu.AddTextInput("Core ID", () => editingCore, value => editingCore = value ?? string.Empty, "Example: mesen");
        menu.AddButton("Save mapping", SaveMapping, "The mapping is used immediately and saved to core-map.json");
        if (extension != null)
            menu.AddButton("Remove mapping", ConfirmRemoveMapping, "." + extension + " will no longer launch a core");
        menu.AddButton("Back", back);
        menu.Show();
    }

    private static void SaveMapping()
    {
        string extension = (editingExtension ?? string.Empty).Trim().TrimStart('.');
        string id = (editingCore ?? string.Empty).Trim();
        if (!SafeExtension.IsMatch(extension))
        {
            ModsUi.ShowToast("Enter a valid file extension without a path or wildcard.", 7f);
            return;
        }
        if (!SafeCoreId.IsMatch(id))
        {
            ModsUi.ShowToast("Enter the core DLL name without _libretro.dll.", 7f);
            return;
        }
        try
        {
            if (originalExtension != null && !string.Equals(originalExtension, extension, StringComparison.OrdinalIgnoreCase))
                coreMap.Extensions.Remove(originalExtension);
            coreMap.Extensions[extension] = id;
            coreMap.Save();
            ModsUi.ShowToast($".{extension} now uses {id}." + (IsCoreInstalled(id) ? string.Empty : " Install that core before playing."), 7f);
            mappingReturn?.Invoke();
        }
        catch (Exception ex)
        {
            MelonLogger.Error("Could not save core mapping: " + ex);
            ModsUi.ShowToast("Could not save the mapping: " + ex.Message, 8f);
        }
    }

    private static void ConfirmRemoveMapping()
    {
        string extension = originalExtension;
        ModMenu menu = CreateMenu(".remove-mapping", "Remove Mapping?", "." + extension);
        menu.AddLabel("Games with this file extension will no longer resolve to a default core.");
        menu.AddButton("Remove ." + extension, () => RemoveMapping(extension), "The current config will be backed up");
        menu.AddButton("Cancel", mappingReturn);
        menu.Show();
    }

    private static void RemoveMapping(string extension)
    {
        try
        {
            coreMap.Extensions.Remove(extension);
            coreMap.Save();
            ModsUi.ShowToast("Removed the ." + extension + " mapping.");
            mappingReturn?.Invoke();
        }
        catch (Exception ex)
        {
            MelonLogger.Error("Could not remove core mapping: " + ex);
            ModsUi.ShowToast("Could not remove the mapping: " + ex.Message, 8f);
        }
    }

    private static void ConfirmRestoreMappings(Action back)
    {
        ModMenu menu = CreateMenu(".restore-mappings", "Restore Default Mappings?", "Current mappings will be replaced");
        menu.AddLabel("The current core-map.json will be backed up before BR-Libretro restores its original mappings.");
        menu.AddButton("Restore defaults", () => RestoreMappings(back), "Replace all current file mappings");
        menu.AddButton("Cancel", () => OpenMappingsMenu(back));
        menu.Show();
    }

    private static void RestoreMappings(Action back)
    {
        try
        {
            coreMap.RestoreDefaults();
            ModsUi.ShowToast("Default core mappings restored.");
            OpenMappingsMenu(back);
        }
        catch (Exception ex)
        {
            MelonLogger.Error("Could not restore core mappings: " + ex);
            ModsUi.ShowToast("Could not restore mappings: " + ex.Message, 8f);
        }
    }

    private static bool IsCoreInstalled(string id) =>
        File.Exists(Path.Combine(DataPaths.Cores, id + "_libretro.dll"));

    private static string PackageDetail(CorePackage package)
    {
        string subject = string.IsNullOrWhiteSpace(package.Systems) ? package.Id : package.Systems;
        return subject + " | " + (package.IsInstalled ? "Installed" : "Not installed");
    }

    private static string FriendlyName(string id)
    {
        string words = (id ?? string.Empty).Replace('_', ' ').Replace('-', ' ');
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(words);
    }

    private static void InstallArchive(CorePackage package, string archivePath,
        out string installedHash, out string backupPath)
    {
        Directory.CreateDirectory(DataPaths.Cores);
        Directory.CreateDirectory(DataPaths.Temp);
        string stagingPath = Path.Combine(DataPaths.Temp,
            package.FileName + "." + Guid.NewGuid().ToString("N") + ".staging");
        backupPath = null;
        try
        {
            using (var archive = ZipFile.OpenRead(archivePath))
            {
                ZipArchiveEntry selected = null;
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    if (!string.Equals(entry.FullName, package.FileName, StringComparison.OrdinalIgnoreCase)) continue;
                    if (selected != null) throw new InvalidDataException("The archive contains the core DLL more than once.");
                    selected = entry;
                }
                if (selected == null) throw new InvalidDataException($"The archive does not contain {package.FileName}.");
                if (selected.Length < 65536 || selected.Length > MaximumCoreBytes)
                    throw new InvalidDataException("The core DLL has an unexpected size.");
                using FileStream output = new(stagingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using Stream input = selected.Open();
                input.CopyTo(output);
                output.Flush(true);
            }

            ValidateWindowsX64Dll(stagingPath);
            installedHash = ComputeSha256(stagingPath);
            if (File.Exists(package.Destination))
            {
                string backupDirectory = Path.Combine(DataPaths.Cores, "backups");
                Directory.CreateDirectory(backupDirectory);
                backupPath = Path.Combine(backupDirectory,
                    Path.GetFileNameWithoutExtension(package.FileName) + "-" +
                    DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff") + ".dll");
                File.Replace(stagingPath, package.Destination, backupPath, true);
            }
            else File.Move(stagingPath, package.Destination);
        }
        finally
        {
            if (File.Exists(stagingPath)) File.Delete(stagingPath);
        }
    }

    private static void ValidateWindowsX64Dll(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length < 65536) throw new InvalidDataException("The downloaded core is too small.");
        byte[] dosHeader = new byte[64];
        if (stream.Read(dosHeader, 0, dosHeader.Length) != dosHeader.Length || dosHeader[0] != (byte)'M' || dosHeader[1] != (byte)'Z')
            throw new InvalidDataException("The downloaded core is not a Windows DLL.");
        int peOffset = BitConverter.ToInt32(dosHeader, 0x3c);
        if (peOffset < 64 || peOffset > stream.Length - 6)
            throw new InvalidDataException("The downloaded core has an invalid PE header.");
        stream.Position = peOffset;
        byte[] peHeader = new byte[6];
        if (stream.Read(peHeader, 0, peHeader.Length) != peHeader.Length || peHeader[0] != (byte)'P' || peHeader[1] != (byte)'E' || peHeader[2] != 0 || peHeader[3] != 0)
            throw new InvalidDataException("The downloaded core has an invalid PE signature.");
        if (BitConverter.ToUInt16(peHeader, 4) != 0x8664)
            throw new InvalidDataException("The downloaded core is not a Windows x64 DLL.");
    }

    private static string ComputeSha256(string path)
    {
        using SHA256 sha = SHA256.Create();
        using FileStream stream = File.OpenRead(path);
        return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty);
    }
}
