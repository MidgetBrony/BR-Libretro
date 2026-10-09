using BR_MediaAPI;
using GLTFast;
using GLTFast.Logging;
using MelonLoader;
using SteamShelf;
using SteamShelf.Media;
using SteamShelf.Placeables;
using SteamShelf.PlayerTools;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using UnityEngine;

namespace BR_Libretro;

internal sealed class LibretroMediaPresentation : IDisposable
{
    internal const string OverrideKey = "com.midgetbrony.br-libretro.core-models";
    private readonly CoreMap coreMap;
    private readonly CorePresentationMap presentationMap;

    internal LibretroMediaPresentation(CoreMap coreMap)
    {
        this.coreMap = coreMap;
        presentationMap = CorePresentationMap.Load();
        MediaApi.RegisterVisualOverride(new MediaVisualOverrideDefinition
        {
            Key = OverrideKey,
            Priority = 100,
            Matches = Matches,
            CreateVisual = CreateVisual
        });
        MelonLogger.Msg($"Core media presentations registered through BR-MediaAPI: {Path.Combine(DataPaths.Config, "core-presentation.json")}");
    }

    private bool Matches(IMediaItem item)
    {
        if (item is not SteamGameData game) return false;
        return TryGetPresentation(game, out _, out _);
    }

    private GameObject CreateVisual(MediaVisualOverrideContext context)
    {
        if (context.Item is not SteamGameData game || !TryGetPresentation(game, out string core, out CorePresentation definition))
            return null;

        var visual = new GameObject("BR-Libretro Media Visual");
        visual.transform.SetParent(context.Target.transform, false);
        visual.AddComponent<LibretroMediaVisual>().Initialize(context, game, core, definition);
        return visual;
    }

    private bool TryGetPresentation(SteamGameData game, out string core, out CorePresentation definition)
    {
        core = null;
        definition = null;
        if (game == null || !coreMap.TryIdentify(game.LaunchExePath, game.LaunchArguments, out core, out _)) return false;
        return presentationMap.Cores.TryGetValue(core, out definition) && definition?.Enabled == true;
    }

    public void Dispose() => MediaApi.UnregisterVisualOverride(OverrideKey);
}

internal sealed class LibretroMediaVisual : MonoBehaviour
{
    private static readonly Dictionary<string, Task<GltfImport>> Imports = new(StringComparer.OrdinalIgnoreCase);
    private static readonly FieldInfo CurrentMediaPlacementField = typeof(PlayerInteractionTool)
        .GetField("currentMediaPlacement", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo CurrentLookingAtContainerField = typeof(PlayerInteractionTool)
        .GetField("currentLookingAtContainer", BindingFlags.Instance | BindingFlags.NonPublic);
    private SteamGameData game;
    private CorePresentation definition;
    private MediaVisualOverrideContext context;
    private Renderer labelRenderer;
    private Mesh labelMesh;
    private Texture2D generatedLabelTexture;
    private Texture appliedArtwork;
    private string core;
    private float appliedPlacementTurn = float.NaN;

    internal void Initialize(MediaVisualOverrideContext newContext, SteamGameData newGame, string newCore, CorePresentation newDefinition)
    {
        context = newContext;
        game = newGame;
        core = newCore;
        definition = newDefinition;
        Load();
    }

    private async void Load()
    {
        try
        {
            string path = ResolveModelPath(definition.Model);
            if (!File.Exists(path)) throw new FileNotFoundException($"No cartridge model found for core '{core}'.", path);
            GltfImport import = await GetImport(path);
            if (this == null || import == null) return;
            await import.InstantiateMainSceneAsync(transform);
            if (this == null) return;
            ConfigureTransform();
            FindLabelRenderer();
            ApplyArtwork();
            RefreshPlacementPreviewMaterials();
            MelonLogger.Msg($"BR-MediaAPI applied '{Path.GetFileName(path)}' to {context.Usage} for '{game?.Name}'.");
        }
        catch (Exception ex)
        {
            MelonLogger.Error($"Could not create the {core} media visual: {ex}");
        }
    }

    private void Update()
    {
        float placementTurn = ShelfPlacementQuarterTurn();
        if (float.IsNaN(appliedPlacementTurn) || Mathf.Abs(Mathf.DeltaAngle(appliedPlacementTurn, placementTurn)) > 0.1f)
            ConfigureTransform();
        ApplyArtwork();
    }

    private bool IsLoosePlacementPreview()
    {
        if (context?.Usage != MediaVisualUsage.Loose || context.Target == null) return false;
        PlacementTag tag = context.Target.GetComponent<PlacementTag>();
        return tag?.IsPreview == true;
    }

    private static Task<GltfImport> GetImport(string path)
    {
        if (!Imports.TryGetValue(path, out Task<GltfImport> task))
        {
            task = LoadImport(path);
            Imports[path] = task;
        }
        return task;
    }

    private static async Task<GltfImport> LoadImport(string path)
    {
        var import = new GltfImport(null, null, null, new ConsoleLogger());
        string directory = Path.GetDirectoryName(path);
        Uri baseUri = string.IsNullOrEmpty(directory) ? null : new Uri(directory + Path.DirectorySeparatorChar);
        if (!await import.LoadFile(Path.GetFullPath(path), baseUri))
            throw new InvalidOperationException("glTFast could not load " + path);
        return import;
    }

    private static string ResolveModelPath(string configured)
    {
        if (string.IsNullOrWhiteSpace(configured)) return string.Empty;
        return Path.IsPathRooted(configured) ? configured : Path.Combine(DataPaths.Models, configured);
    }

    private void ConfigureTransform()
    {
        float[] rotation = context.Usage switch
        {
            MediaVisualUsage.Held => definition.HeldRotation ?? Array.Empty<float>(),
            MediaVisualUsage.Inspect => definition.InspectionRotation ?? Array.Empty<float>(),
            MediaVisualUsage.Loose when IsLoosePlacementPreview() => definition.PlacementPreviewRotation ?? definition.LooseRotation ?? Array.Empty<float>(),
            MediaVisualUsage.Loose => definition.LooseRotation ?? Array.Empty<float>(),
            _ => definition.Rotation ?? Array.Empty<float>()
        };
        transform.localPosition = Vector3.zero;
        transform.localScale = Vector3.one;
        appliedPlacementTurn = ShelfPlacementQuarterTurn();
        float faceRotation = definition.FaceRotationDegrees + appliedPlacementTurn;
        transform.localRotation = Quaternion.Euler(Value(rotation, 0), Value(rotation, 1), Value(rotation, 2))
            * Quaternion.AngleAxis(faceRotation, Vector3.forward);

        Renderer[] renderers = GetComponentsInChildren<Renderer>(true);
        // Measure in the imported model's own coordinate space. World AABBs
        // grow at diagonal angles, which previously made a 45-degree preview
        // recalculate smaller even though the player had not resized it.
        Bounds modelBounds = BoundsRelativeTo(transform, renderers);
        float longestSide = Mathf.Max(modelBounds.size.x, Mathf.Max(modelBounds.size.y, modelBounds.size.z));
        if (longestSide > 0.0001f)
            transform.localScale = Vector3.one * (Mathf.Max(0.01f, definition.HeightMetres) / longestSide);

        Bounds bounds = BoundsRelativeTo(context.Target.transform, renderers);
        float[] offset = definition.Offset ?? Array.Empty<float>();
        Vector3 configuredOffset = new(Value(offset, 0), Value(offset, 1), Value(offset, 2));
        Vector3 position = context.AnchorCenter + configuredOffset - bounds.center;
        transform.localPosition = position;
        if (context.Usage == MediaVisualUsage.Shelf && context.AnchorWorldBounds.size.sqrMagnitude > 0.0000001f)
        {
            // ShelfBox roots are authored with a 90-degree rotation, so their
            // local Y axis is not world-up. Match the actual visible case's
            // world-space bottom instead of moving along the wrong local axis.
            Bounds replacementWorldBounds = WorldBounds(renderers);
            transform.position += Vector3.up * (context.AnchorWorldBounds.min.y - replacementWorldBounds.min.y);
        }
    }

    private static Bounds BoundsRelativeTo(Transform parent, Renderer[] renderers)
    {
        bool found = false;
        Bounds result = default;
        foreach (Renderer renderer in renderers)
        {
            if (renderer == null) continue;
            Bounds local = renderer.localBounds;
            for (int x = -1; x <= 1; x += 2)
            for (int y = -1; y <= 1; y += 2)
            for (int z = -1; z <= 1; z += 2)
            {
                Vector3 rendererPoint = local.center + Vector3.Scale(local.extents, new Vector3(x, y, z));
                Vector3 point = parent.InverseTransformPoint(renderer.transform.TransformPoint(rendererPoint));
                if (!found) { result = new Bounds(point, Vector3.zero); found = true; }
                else result.Encapsulate(point);
            }
        }
        return result;
    }

    private static Bounds WorldBounds(Renderer[] renderers)
    {
        bool found = false;
        Bounds result = default;
        foreach (Renderer renderer in renderers)
        {
            if (renderer == null) continue;
            if (!found) { result = renderer.bounds; found = true; }
            else result.Encapsulate(renderer.bounds);
        }
        return result;
    }

    private static float Value(float[] values, int index) => values.Length > index ? values[index] : 0f;

    private float ShelfPlacementQuarterTurn()
    {
        if (context?.Target == null) return 0f;

        if (context.Usage == MediaVisualUsage.Loose && IsLoosePlacementPreview()
            && TryGetShelfPreviewPlacement(out MediaPlacement previewPlacement))
        {
            return previewPlacement switch
            {
                MediaPlacement.Spine => definition.ShelfSpineQuarterTurnDegrees,
                MediaPlacement.FaceUp => definition.ShelfFaceUpQuarterTurnDegrees,
                _ => 0f
            };
        }

        if (context.Usage != MediaVisualUsage.Shelf) return 0f;

        Quaternion targetRotation = context.Target.transform.localRotation;
        if (Quaternion.Angle(targetRotation, Quaternion.Euler(90f, -90f, 0f)) < 2f)
            return definition.ShelfSpineQuarterTurnDegrees;
        if (Quaternion.Angle(targetRotation, Quaternion.Euler(180f, -90f, 0f)) < 2f)
            return definition.ShelfFaceUpQuarterTurnDegrees;
        return 0f;
    }

    private static bool TryGetShelfPreviewPlacement(out MediaPlacement placement)
    {
        placement = MediaPlacement.FaceOut;
        PlayerInteractionTool tool = UnityEngine.Object.FindFirstObjectByType<PlayerInteractionTool>();
        if (tool == null || CurrentMediaPlacementField == null || CurrentLookingAtContainerField == null
            || CurrentLookingAtContainerField.GetValue(tool) is not PlaceableMediaContainer)
            return false;

        object value = CurrentMediaPlacementField.GetValue(tool);
        if (value is not MediaPlacement current) return false;
        placement = current;
        return true;
    }

    private void FindLabelRenderer()
    {
        string wanted = definition.LabelMaterial ?? string.Empty;
        foreach (Renderer renderer in GetComponentsInChildren<Renderer>(true))
        foreach (Material material in renderer.sharedMaterials)
            if (material != null && material.name.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                labelRenderer = renderer;
                ApplyLabelUvTransform(renderer);
                return;
            }
    }

    private void ApplyLabelUvTransform(Renderer renderer)
    {
        bool rotate180 = Mathf.Abs(Mathf.DeltaAngle(definition.LabelRotationDegrees, 180f)) < 1f;
        bool flipX = definition.LabelMirrorHorizontal != rotate180;
        bool flipY = definition.LabelMirrorVertical != rotate180;
        if (!flipX && !flipY) return;

        Mesh source = null;
        MeshFilter filter = renderer.GetComponent<MeshFilter>();
        SkinnedMeshRenderer skinned = renderer as SkinnedMeshRenderer;
        if (filter != null) source = filter.sharedMesh;
        else if (skinned != null) source = skinned.sharedMesh;
        if (source == null) return;

        labelMesh = Instantiate(source);
        labelMesh.name = source.name + " BR-Libretro Label UV";
        Vector2[] uvs = labelMesh.uv;
        for (int i = 0; i < uvs.Length; i++)
        {
            if (flipX) uvs[i].x = 1f - uvs[i].x;
            if (flipY) uvs[i].y = 1f - uvs[i].y;
        }
        labelMesh.uv = uvs;
        if (filter != null) filter.sharedMesh = labelMesh;
        else skinned.sharedMesh = labelMesh;
    }

    private void ApplyArtwork()
    {
        if (!definition.UseGameArtwork || labelRenderer == null || game == null) return;
        Texture texture = SteamTextureCache.GetBoxArt(game);
        if (texture == null || texture == appliedArtwork) return;
        Texture labelTexture = texture;
        if (string.Equals(Path.GetFileName(definition.Model), "nes_cartridge.glb", StringComparison.OrdinalIgnoreCase))
        {
            if (generatedLabelTexture != null) Destroy(generatedLabelTexture);
            generatedLabelTexture = NesCartridgeLabel.Create(game.Name, texture);
            labelTexture = generatedLabelTexture;
        }
        foreach (Material material in labelRenderer.materials)
        {
            if (material == null || material.name.IndexOf(definition.LabelMaterial ?? string.Empty, StringComparison.OrdinalIgnoreCase) < 0) continue;
            material.mainTexture = labelTexture;
            ApplyTexture(material, "_MainTex", labelTexture);
            ApplyTexture(material, "_BaseMap", labelTexture);
            ApplyTexture(material, "_BaseColorTexture", labelTexture);
        }
        appliedArtwork = texture;
    }

    private void RefreshPlacementPreviewMaterials()
    {
        PlacementTag tag = GetComponentInParent<PlacementTag>();
        if (tag?.IsPreview != true) return;
        PlaceablePainter painter = tag.GetComponent<PlaceablePainter>();
        if (painter == null) return;
        painter.InitializeMaterials();
        painter.SetupAsForcedPlaceable();
        painter.ReCacheAllBaseMaterialsToCurrent();
    }

    private static void ApplyTexture(Material material, string property, Texture texture)
    {
        if (!material.HasProperty(property)) return;
        material.SetTexture(property, texture);
        material.SetTextureScale(property, Vector2.one);
        material.SetTextureOffset(property, Vector2.zero);
    }

    private void OnDestroy()
    {
        if (labelMesh != null) Destroy(labelMesh);
        if (generatedLabelTexture != null) Destroy(generatedLabelTexture);
    }
}
