using System;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace BR_Libretro;

internal sealed class RomContentSession : IDisposable
{
    private static readonly string SessionsRoot = Path.Combine(DataPaths.Temp, "content-sessions");
    internal string LaunchPath { get; private set; }
    private string temporaryDirectory;

    private RomContentSession() { }

    internal static RomContentSession Prepare(RomData rom)
    {
        if (rom == null) throw new ArgumentNullException(nameof(rom));
        if (!File.Exists(rom.ContentPath)) throw new FileNotFoundException("The ROM file is missing.", rom.ContentPath);
        var session = new RomContentSession();
        try
        {
            if (!string.Equals(Path.GetExtension(rom.ContentPath), ".zip", StringComparison.OrdinalIgnoreCase))
            {
                session.LaunchPath = Path.GetFullPath(rom.ContentPath);
                return session;
            }
            if (string.IsNullOrWhiteSpace(rom.ArchiveEntry))
                throw new InvalidDataException("The ROM archive does not identify which entry should be launched.");

            Directory.CreateDirectory(SessionsRoot);
            session.temporaryDirectory = Path.Combine(SessionsRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(session.temporaryDirectory);
            using ZipArchive archive = ZipFile.OpenRead(rom.ContentPath);
            ZipArchiveEntry selected = archive.Entries.FirstOrDefault(entry =>
                string.Equals(Normalize(entry.FullName), Normalize(rom.ArchiveEntry), StringComparison.OrdinalIgnoreCase));
            if (selected == null) throw new InvalidDataException($"Archive entry '{rom.ArchiveEntry}' is missing.");

            string extension = Path.GetExtension(selected.FullName);
            bool descriptor = string.Equals(extension, ".cue", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(extension, ".m3u", StringComparison.OrdinalIgnoreCase);
            if (descriptor)
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                    if (!string.IsNullOrEmpty(entry.Name)) ExtractSafe(entry, session.temporaryDirectory);
            }
            else ExtractSafe(selected, session.temporaryDirectory);

            session.LaunchPath = SafeDestination(session.temporaryDirectory, selected.FullName);
            if (!File.Exists(session.LaunchPath)) throw new InvalidDataException("The selected ROM could not be extracted.");
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    internal static void CleanupAbandonedSessions()
    {
        if (!Directory.Exists(SessionsRoot)) return;
        foreach (string directory in Directory.EnumerateDirectories(SessionsRoot))
        {
            try { Directory.Delete(directory, true); }
            catch { }
        }
    }

    private static void ExtractSafe(ZipArchiveEntry entry, string root)
    {
        string destination = SafeDestination(root, entry.FullName);
        Directory.CreateDirectory(Path.GetDirectoryName(destination));
        entry.ExtractToFile(destination, true);
    }

    private static string SafeDestination(string root, string entry)
    {
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string destination = Path.GetFullPath(Path.Combine(fullRoot, Normalize(entry).Replace('/', Path.DirectorySeparatorChar)));
        if (!destination.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The archive contains an unsafe path: " + entry);
        return destination;
    }

    private static string Normalize(string value) => (value ?? string.Empty).Replace('\\', '/').TrimStart('/');

    public void Dispose()
    {
        string directory = temporaryDirectory;
        temporaryDirectory = null;
        LaunchPath = null;
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;
        try { Directory.Delete(directory, true); }
        catch (Exception ex) { MelonLoader.MelonLogger.Warning($"Could not remove temporary ROM content '{directory}': {ex.Message}"); }
    }
}
