using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

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

    internal bool TryResolve(string launchExe, string launchArguments, out string core, out string rom)
    {
        if (!TryIdentify(launchExe, launchArguments, out core, out rom)) return false;
        return File.Exists(Path.Combine(DataPaths.Cores, core + "_libretro.dll"));
    }

    internal bool TryIdentify(string launchExe, string launchArguments, out string core, out string rom)
    {
        core = rom = null;
        var arguments = Regex.Matches(launchArguments ?? string.Empty, "\\\"([^\\\"]+)\\\"|([^\\s]+)")
            .Cast<Match>().Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)
            .ToList();
        string requestedCore = arguments.FirstOrDefault(p => p.EndsWith("_libretro.dll", StringComparison.OrdinalIgnoreCase));

        var candidates = arguments.Where(File.Exists).ToList();
        if (File.Exists(launchExe)) candidates.Add(launchExe);
        rom = candidates.LastOrDefault(p => !p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
        if (rom == null) return false;
        if (requestedCore != null)
        {
            core = Path.GetFileNameWithoutExtension(requestedCore);
            if (core.EndsWith("_libretro", StringComparison.OrdinalIgnoreCase)) core = core.Substring(0, core.Length - 9);
            return true;
        }
        string extension = Path.GetExtension(rom).TrimStart('.');
        if (!Extensions.TryGetValue(extension, out core) || string.IsNullOrWhiteSpace(core)) return false;
        return true;
    }
}
