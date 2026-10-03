using System;
using System.IO;

namespace BR_Libretro;

internal static class DataPaths
{
    internal static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "br-libretro");
    internal static readonly string Cores = Path.Combine(Root, "cores");
    internal static readonly string Bios = Path.Combine(Root, "BIOS");
    internal static readonly string Saves = Path.Combine(Root, "saves");
    internal static readonly string States = Path.Combine(Root, "states");
    internal static readonly string Config = Path.Combine(Root, "config");
    internal static readonly string Logs = Path.Combine(Root, "logs");
    internal static readonly string Models = Path.Combine(Root, "models");

    internal static void Ensure()
    {
        MigrateLegacySystemDirectory();
        foreach (string path in new[] { Root, Cores, Bios, Saves, States, Config, Logs, Models, Path.Combine(Root, "core_assets"), Path.Combine(Root, "temp") })
            Directory.CreateDirectory(path);
    }

    private static void MigrateLegacySystemDirectory()
    {
        string legacy = Path.Combine(Root, "system");
        if (!Directory.Exists(legacy)) return;

        if (!Directory.Exists(Bios))
        {
            Directory.Move(legacy, Bios);
            return;
        }

        // Merge only files that do not already exist. Any collision remains in
        // the legacy folder for manual review, so migration never overwrites a
        // known BIOS or firmware file.
        foreach (string source in Directory.EnumerateFiles(legacy, "*", SearchOption.AllDirectories))
        {
            string relative = source.Substring(legacy.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string destination = Path.Combine(Bios, relative);
            if (File.Exists(destination)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            File.Move(source, destination);
        }
    }
}
