using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace BR_Libretro;

internal sealed class RomPresentationMap
{
    public Dictionary<string, RomPresentation> Platforms { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    internal static RomPresentationMap Load()
    {
        ExtractBuiltInAsset("nes_cartridge.glb", "BR_Libretro.Assets.nes_cartridge.glb", DataPaths.Models);
        ExtractBuiltInAsset("snes_cartridge.glb", "BR_Libretro.Assets.snes_cartridge.glb", DataPaths.Models);
        ExtractBuiltInAsset("snes_cartridge_ATTRIBUTION.txt", "BR_Libretro.Assets.snes_cartridge_ATTRIBUTION.txt", DataPaths.Models);
        string path = Path.Combine(DataPaths.Config, "rom-presentation.json");
        if (!File.Exists(path))
        {
            var defaults = new RomPresentationMap();
            defaults.Platforms["nes"] = RomPresentation.NesCartridge();
            defaults.Platforms["snes"] = RomPresentation.SnesCartridge();
            File.WriteAllText(path, JsonConvert.SerializeObject(defaults, Formatting.Indented));
            return defaults;
        }

        RomPresentationMap map = JsonConvert.DeserializeObject<RomPresentationMap>(File.ReadAllText(path))
            ?? new RomPresentationMap();
        map.Platforms = new Dictionary<string, RomPresentation>(map.Platforms ?? new(), StringComparer.OrdinalIgnoreCase);

        bool updated = false;
        foreach (RomPresentation presentation in map.Platforms.Values)
        {
            if (string.Equals(Path.GetFileName(presentation.Model), "nes_cartridge.glb", StringComparison.OrdinalIgnoreCase)
                && !presentation.LabelMirrorHorizontal
                && presentation.LabelMirrorVertical
                && (Math.Abs(presentation.LabelRotationDegrees) < 0.01f
                    || Math.Abs(presentation.LabelRotationDegrees - 180f) < 0.01f))
            {
                presentation.LabelRotationDegrees = 180f;
                presentation.LabelMirrorHorizontal = true;
                updated = true;
            }

            if (string.Equals(Path.GetFileName(presentation.Model), "snes_cartridge.glb", StringComparison.OrdinalIgnoreCase)
                && (string.Equals(presentation.LabelRenderer, "Label_MANA", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(presentation.LabelMaterial, "Labels_Combined", StringComparison.OrdinalIgnoreCase)))
            {
                ApplySnesDefaults(presentation);
                updated = true;
            }
        }

        if (!map.Platforms.ContainsKey("nes"))
        {
            map.Platforms["nes"] = RomPresentation.NesCartridge();
            updated = true;
        }
        if (!map.Platforms.ContainsKey("snes"))
        {
            map.Platforms["snes"] = RomPresentation.SnesCartridge();
            updated = true;
        }

        if (updated)
            File.WriteAllText(path, JsonConvert.SerializeObject(map, Formatting.Indented));

        return map;
    }

    private static void ExtractBuiltInAsset(string fileName, string resourceName, string directory)
    {
        string destination = Path.Combine(directory, fileName);
        using Stream source = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream(resourceName);
        if (source == null) throw new InvalidOperationException($"The built-in asset '{fileName}' is missing.");
        if (File.Exists(destination) && StreamsEqual(source, destination)) return;
        source.Position = 0;
        using FileStream target = File.Create(destination);
        source.CopyTo(target);
    }

    private static bool StreamsEqual(Stream source, string destination)
    {
        var info = new FileInfo(destination);
        if (info.Length != source.Length) return false;
        using FileStream existing = File.OpenRead(destination);
        var left = new byte[81920];
        var right = new byte[81920];
        while (true)
        {
            int leftCount = source.Read(left, 0, left.Length);
            int rightCount = existing.Read(right, 0, right.Length);
            if (leftCount != rightCount) return false;
            if (leftCount == 0) return true;
            for (int i = 0; i < leftCount; i++)
                if (left[i] != right[i]) return false;
        }
    }

    private static void ApplySnesDefaults(RomPresentation presentation)
    {
        RomPresentation defaults = RomPresentation.SnesCartridge();
        presentation.HeightMetres = defaults.HeightMetres;
        presentation.LabelRenderer = defaults.LabelRenderer;
        presentation.LabelMaterial = defaults.LabelMaterial;
        presentation.LabelRotationDegrees = defaults.LabelRotationDegrees;
        presentation.LabelMirrorHorizontal = defaults.LabelMirrorHorizontal;
        presentation.LabelMirrorVertical = defaults.LabelMirrorVertical;
    }
}

internal sealed class RomPresentation
{
    public bool Enabled { get; set; } = true;
    public string Model { get; set; } = "nes_cartridge.glb";
    public float HeightMetres { get; set; } = 0.135f;
    public float[] Rotation { get; set; } = { 90f, 0f, 0f };
    public float[] LooseRotation { get; set; } = { -90f, 0f, 0f };
    public float[] PlacementPreviewRotation { get; set; } = { -90f, 0f, 0f };
    public float[] HeldRotation { get; set; } = { 0f, 0f, 0f };
    public float[] InspectionRotation { get; set; } = { 180f, 0f, 0f };
    public float FaceRotationDegrees { get; set; } = 180f;
    public float ShelfSpineQuarterTurnDegrees { get; set; } = 270f;
    public float ShelfFaceUpQuarterTurnDegrees { get; set; } = 270f;
    public float[] Offset { get; set; } = { 0f, 0f, 0f };
    public string LabelRenderer { get; set; } = string.Empty;
    public string LabelMaterial { get; set; } = "Material.001";
    public float LabelRotationDegrees { get; set; } = 180f;
    public bool LabelMirrorHorizontal { get; set; } = true;
    public bool LabelMirrorVertical { get; set; } = true;
    public bool UseGameArtwork { get; set; } = true;

    internal static RomPresentation NesCartridge() => new();

    internal static RomPresentation SnesCartridge() => new()
    {
        Model = "snes_cartridge.glb",
        HeightMetres = 0.14f,
        LabelRenderer = "BR-Libretro SNES Artwork Label",
        LabelMaterial = "BR_Libretro_SNESCoverArt",
        LabelRotationDegrees = 0f,
        LabelMirrorHorizontal = false,
        LabelMirrorVertical = false
    };
}
