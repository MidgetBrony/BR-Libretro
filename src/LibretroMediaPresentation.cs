using BR_MediaAPI;
using GLTFast;
using GLTFast.Logging;
using MelonLoader;
using SteamShelf;
using SteamShelf.Media;
using SteamShelf.Placeables;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using TMPro;
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
    private static TMP_FontAsset topLabelFont;
    private static Font topLabelSourceFont;
    private SteamGameData game;
    private CorePresentation definition;
    private MediaVisualOverrideContext context;
    private Renderer labelRenderer;
    private Mesh labelMesh;
    private Material topLabelMaterial;
    private Texture appliedArtwork;
    private string core;

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
            CreateNesTopLabel();
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
        transform.localRotation = Quaternion.Euler(Value(rotation, 0), Value(rotation, 1), Value(rotation, 2))
            * Quaternion.AngleAxis(definition.FaceRotationDegrees, Vector3.forward);

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
        foreach (Material material in labelRenderer.materials)
        {
            if (material == null || material.name.IndexOf(definition.LabelMaterial ?? string.Empty, StringComparison.OrdinalIgnoreCase) < 0) continue;
            material.mainTexture = texture;
            ApplyTexture(material, "_MainTex", texture);
            ApplyTexture(material, "_BaseMap", texture);
            ApplyTexture(material, "_BaseColorTexture", texture);
        }
        appliedArtwork = texture;
    }

    private void CreateNesTopLabel()
    {
        if (!string.Equals(Path.GetFileName(definition.Model), "nes_cartridge.glb", StringComparison.OrdinalIgnoreCase)) return;

        Renderer[] renderers = GetComponentsInChildren<Renderer>(true);
        Bounds bounds = BoundsRelativeTo(transform, renderers);
        if (bounds.size.x <= 0.001f || bounds.size.y <= 0.001f || bounds.size.z <= 0.001f) return;

        float width = bounds.size.x * 0.64f;
        float height = bounds.size.y * 0.62f;
        float depth = Mathf.Max(0.008f, bounds.size.z * 0.002f);
        Vector3 center = new(bounds.center.x, bounds.center.y, bounds.max.z + depth * 0.5f);

        GameObject panel = GameObject.CreatePrimitive(PrimitiveType.Cube);
        panel.name = "NES Top Title Label";
        panel.transform.SetParent(transform, false);
        panel.transform.localPosition = center;
        panel.transform.localRotation = Quaternion.identity;
        panel.transform.localScale = new Vector3(width, height, depth);
        Collider collider = panel.GetComponent<Collider>();
        if (collider != null) Destroy(collider);

        Shader shader = Shader.Find("Universal Render Pipeline/Unlit")
            ?? Shader.Find("Unlit/Color")
            ?? Shader.Find("Standard");
        topLabelMaterial = new Material(shader) { name = "BR-Libretro NES Top Label", color = Color.black };
        if (topLabelMaterial.HasProperty("_BaseColor")) topLabelMaterial.SetColor("_BaseColor", Color.black);
        panel.GetComponent<Renderer>().sharedMaterial = topLabelMaterial;

        GameObject titleObject = new("NES Top Title");
        titleObject.transform.SetParent(transform, false);
        titleObject.transform.localPosition = new Vector3(center.x, center.y, bounds.max.z + depth * 1.1f);
        titleObject.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);

        TextMeshPro title = titleObject.AddComponent<TextMeshPro>();
        title.text = game?.Name ?? string.Empty;
        title.font = GetTopLabelFont();
        title.color = Color.white;
        title.alignment = TextAlignmentOptions.Center;
        title.enableAutoSizing = true;
        title.fontSizeMin = Mathf.Max(0.035f, height * 0.12f);
        title.fontSizeMax = Mathf.Max(0.08f, height * 0.58f);
        title.textWrappingMode = TextWrappingModes.NoWrap;
        title.overflowMode = TextOverflowModes.Ellipsis;
        title.rectTransform.sizeDelta = new Vector2(width * 0.9f, height * 0.78f);
    }

    private static TMP_FontAsset GetTopLabelFont()
    {
        if (topLabelFont != null) return topLabelFont;
        topLabelSourceFont = Font.CreateDynamicFontFromOSFont("Segoe UI", 64)
            ?? Font.CreateDynamicFontFromOSFont("Arial", 64);
        topLabelFont = TMP_FontAsset.CreateFontAsset(topLabelSourceFont);
        topLabelFont.name = "BR-Libretro Segoe UI";
        return topLabelFont;
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
        if (topLabelMaterial != null) Destroy(topLabelMaterial);
    }
}
