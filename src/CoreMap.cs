using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;

namespace BR_Libretro;

internal sealed class CoreMap
{
    public Dictionary<string, string> Extensions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    internal static CoreMap Load()
    {
        string path = Path.Combine(DataPaths.Config, "core-map.json");
        if (!File.Exists(path))
        {
            CoreMap defaults = CreateDefaults();
            defaults.Save();
            return defaults;
        }
        CoreMap map = JsonConvert.DeserializeObject<CoreMap>(File.ReadAllText(path)) ?? new CoreMap();
        map.Extensions = new Dictionary<string, string>(map.Extensions ?? new(), StringComparer.OrdinalIgnoreCase);
        return map;
    }

    internal static CoreMap CreateDefaults()
    {
        var defaults = new CoreMap();
        defaults.Extensions["nes"] = "mesen";
        defaults.Extensions["fds"] = "mesen";
        defaults.Extensions["sfc"] = "snes9x";
        defaults.Extensions["smc"] = "snes9x";
        defaults.Extensions["gb"] = "gambatte";
        defaults.Extensions["gbc"] = "gambatte";
        defaults.Extensions["gba"] = "mgba";
        defaults.Extensions["md"] = "genesis_plus_gx";
        defaults.Extensions["gen"] = "genesis_plus_gx";
        defaults.Extensions["cue"] = "pcsx_rearmed";
        defaults.Extensions["wad"] = "prboom";
        return defaults;
    }

    internal void RestoreDefaults()
    {
        Extensions.Clear();
        foreach (KeyValuePair<string, string> pair in CreateDefaults().Extensions)
            Extensions[pair.Key] = pair.Value;
        Save();
    }

    internal void Save()
    {
        Directory.CreateDirectory(DataPaths.Config);
        string path = Path.Combine(DataPaths.Config, "core-map.json");
        string staging = path + "." + Guid.NewGuid().ToString("N") + ".staging";
        try
        {
            File.WriteAllText(staging, JsonConvert.SerializeObject(this, Formatting.Indented));
            if (File.Exists(path))
            {
                string backups = Path.Combine(DataPaths.Config, "backups");
                Directory.CreateDirectory(backups);
                string backup = Path.Combine(backups,
                    "core-map-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff") + ".json");
                File.Replace(staging, path, backup, true);
            }
            else File.Move(staging, path);
        }
        finally
        {
            if (File.Exists(staging)) File.Delete(staging);
        }
    }

    internal bool TryResolve(RomData rom, out string core)
    {
        core = null;
        if (rom == null || !File.Exists(rom.ContentPath)) return false;
        if (!string.IsNullOrWhiteSpace(rom.CoreOverride) && IsInstalled(rom.CoreOverride))
        {
            core = rom.CoreOverride;
            return true;
        }
        string platformOverride = RomLibrarySystem.GetPlatformCoreOverride(rom.PlatformId);
        if (!string.IsNullOrWhiteSpace(platformOverride) && IsInstalled(platformOverride))
        {
            core = platformOverride;
            return true;
        }
        if (Extensions.TryGetValue(rom.ContentExtension, out string mapped) && IsInstalled(mapped))
        {
            core = mapped;
            return true;
        }
        if (RomPlatformCatalog.TryGet(rom.PlatformId, out RomPlatform platform))
        {
            foreach (string candidate in platform.PreferredCores)
                if (IsInstalled(candidate)) { core = candidate; return true; }
        }
        return false;
    }

    private static bool IsInstalled(string core) => !string.IsNullOrWhiteSpace(core) &&
        File.Exists(Path.Combine(DataPaths.Cores, core + "_libretro.dll"));
}
