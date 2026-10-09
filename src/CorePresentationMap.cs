using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace BR_Libretro;

internal sealed class CorePresentationMap
{
    public Dictionary<string, CorePresentation> Cores { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    internal static CorePresentationMap Load()
    {
        ExtractBuiltInModel();
        string path = Path.Combine(DataPaths.Config, "core-presentation.json");
        if (!File.Exists(path))
        {
            var defaults = new CorePresentationMap();
            foreach (string core in new[] { "mesen", "fceumm", "nestopia" })
                defaults.Cores[core] = CorePresentation.NesCartridge();
            File.WriteAllText(path, JsonConvert.SerializeObject(defaults, Formatting.Indented));
            return defaults;
        }

        CorePresentationMap map = JsonConvert.DeserializeObject<CorePresentationMap>(File.ReadAllText(path))
            ?? new CorePresentationMap();
        map.Cores = new Dictionary<string, CorePresentation>(map.Cores ?? new(), StringComparer.OrdinalIgnoreCase);

        bool updated = false;
        foreach (CorePresentation presentation in map.Cores.Values)
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
        }

        if (updated)
            File.WriteAllText(path, JsonConvert.SerializeObject(map, Formatting.Indented));

        return map;
    }

    private static void ExtractBuiltInModel()
    {
        string destination = Path.Combine(DataPaths.Models, "nes_cartridge.glb");
        if (File.Exists(destination)) return;

        using Stream source = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("BR_Libretro.Assets.nes_cartridge.glb");
        if (source == null) throw new InvalidOperationException("The built-in NES cartridge model is missing.");
        using FileStream target = File.Create(destination);
        source.CopyTo(target);
    }
}

internal sealed class CorePresentation
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
    public float[] Offset { get; set; } = { 0f, 0f, 0f };
    public string LabelMaterial { get; set; } = "Material.001";
    public float LabelRotationDegrees { get; set; } = 180f;
    public bool LabelMirrorHorizontal { get; set; } = true;
    public bool LabelMirrorVertical { get; set; } = true;
    public bool UseGameArtwork { get; set; } = true;

    internal static CorePresentation NesCartridge() => new();
}
