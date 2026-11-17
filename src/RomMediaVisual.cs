using GLTFast;
using GLTFast.Logging;
using MelonLoader;
using SteamShelf.Placeables;
using SteamShelf.PlayerTools;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace BR_Libretro;

internal sealed class RomMediaVisual : MonoBehaviour
{
    private sealed class EmbeddedArtworkBinding
    {
        private static readonly string[] Properties = { "_MainTex", "_BaseMap", "_BaseColorTexture" };
        private readonly Material material;
        private readonly Texture artwork;
        private readonly Dictionary<string, Texture> originals = new(StringComparer.Ordinal);

        internal EmbeddedArtworkBinding(Material target, Texture replacement)
        {
            material = target;
            artwork = replacement;
            foreach (string property in Properties)
                if (material.HasProperty(property)) originals[property] = material.GetTexture(property);
        }

        internal void SetVisible(bool visible)
        {
            if (material == null) return;
            foreach (KeyValuePair<string, Texture> entry in originals)
                material.SetTexture(entry.Key, visible ? artwork : entry.Value);
        }
    }

    private static readonly Dictionary<string, Task<GltfImport>> Imports = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim InstantiationGate = new(1, 1);
    private static readonly FieldInfo CurrentMediaPlacementField = typeof(PlayerInteractionTool)
        .GetField("currentMediaPlacement", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo CurrentLookingAtContainerField = typeof(PlayerInteractionTool)
        .GetField("currentLookingAtContainer", BindingFlags.Instance | BindingFlags.NonPublic);
    private static RomPresentationMap presentations;
    [SerializeField] private GameObject content;
    [SerializeField] private Renderer layoutAnchor;
    private Texture2D artwork;
    private Texture2D generatedLabel;
    private readonly List<UnityEngine.Object> generated = new();
    private int generation;
    private bool artworkVisible = true;
    private bool visible = true;
    private RomData currentItem;
    private RomVisualUsage currentUsage;
    private RomPresentation currentDefinition;
    private Material caseMaterial;
    private readonly List<Material> caseMaterials = new();
    private readonly List<EmbeddedArtworkBinding> embeddedArtwork = new();
    private Color currentCaseColor = Color.white;
    private float appliedPlacementTurn = float.NaN;
    private CancellationTokenSource loadCancellation;

    internal void Apply(RomData item, RomVisualUsage usage)
    {
        Clear();
        currentItem = item;
        currentUsage = usage;
        if (item == null || !RomPlatformCatalog.TryGet(item.PlatformId, out RomPlatform platform)) return;
        int token = generation;
        loadCancellation = new CancellationTokenSource();
        if (platform.PhysicalFormat is RomPhysicalFormat.NesCartridge or RomPhysicalFormat.SnesCartridge)
            LoadModel(item, platform, usage, token, loadCancellation.Token);
        else CreateProcedural(item, platform, usage);
    }

    internal void SetLayoutAnchor(Renderer renderer) => layoutAnchor = renderer;

    internal void Clear()
    {
        currentItem = null;
        currentDefinition = null;
        caseMaterial = null;
        caseMaterials.Clear();
        embeddedArtwork.Clear();
        appliedPlacementTurn = float.NaN;
        generation++;
        loadCancellation?.Cancel();
        loadCancellation?.Dispose();
        loadCancellation = null;
        GameObject previousContent = content;
        if (previousContent != null) Destroy(previousContent);
        foreach (RomVisualContentMarker marker in GetComponentsInChildren<RomVisualContentMarker>(true))
            if (marker != null && marker.gameObject != previousContent) Destroy(marker.gameObject);
        content = null;
        foreach (UnityEngine.Object value in generated) if (value != null) Destroy(value);
        generated.Clear();
        if (artwork != null) Destroy(artwork);
        if (generatedLabel != null) Destroy(generatedLabel);
        artwork = null;
        generatedLabel = null;
    }

    internal static void RefreshItem(RomData item)
    {
        if (item == null) return;
        foreach (RomMediaVisual visual in FindObjectsByType<RomMediaVisual>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (visual?.currentItem == null || !string.Equals(visual.currentItem.Id, item.Id, StringComparison.OrdinalIgnoreCase)) continue;
            RomVisualUsage usage = visual.currentUsage;
            visual.Apply(item, usage);
        }
    }

    internal static void RefreshCaseColor(RomData item, Color color)
    {
        if (item == null) return;
        foreach (RomMediaVisual visual in FindObjectsByType<RomMediaVisual>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (visual?.currentItem != null && string.Equals(visual.currentItem.Id, item.Id, StringComparison.OrdinalIgnoreCase))
                visual.ApplyCaseColor(color);
    }

    internal static bool TryGetCaseColor(RomData item, out Color color)
    {
        if (item != null)
        {
            foreach (RomMediaVisual visual in FindObjectsByType<RomMediaVisual>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (visual?.currentItem != null && string.Equals(visual.currentItem.Id, item.Id, StringComparison.OrdinalIgnoreCase) && visual.caseMaterial != null)
                { color = visual.currentCaseColor; return true; }
            if (!string.IsNullOrWhiteSpace(item.CaseColor) && ColorUtility.TryParseHtmlString(item.CaseColor, out color)) return true;
        }
        color = Color.white;
        return false;
    }

    internal void SetArtworkVisible(bool visible)
    {
        artworkVisible = visible;
        if (content == null) return;
        foreach (EmbeddedArtworkBinding binding in embeddedArtwork) binding.SetVisible(visible);
        foreach (Renderer renderer in content.GetComponentsInChildren<Renderer>(true))
            renderer.enabled = visible || !renderer.name.Contains("Artwork");
    }

    internal void SetVisible(bool value)
    {
        visible = value;
        if (content != null) content.SetActive(value);
    }

    private void Start()
    {
        // ObjectPlacer can clone the loose root before its asynchronous GLB
        // child exists. Rehydrate that preview from the serialized media ID.
        if (currentItem != null || content != null) return;
        RomMediaProp prop = GetComponent<RomMediaProp>();
        if (prop?.MediaItem is RomData item) Apply(item, RomVisualUsage.Loose);
    }

    private async void LoadModel(RomData item, RomPlatform platform, RomVisualUsage usage, int token, CancellationToken cancellationToken)
    {
        GameObject candidate = null;
        bool gateHeld = false;
        try
        {
            presentations ??= RomPresentationMap.Load();
            if (!presentations.Platforms.TryGetValue(platform.Id, out RomPresentation definition))
                definition = platform.PhysicalFormat == RomPhysicalFormat.SnesCartridge ? RomPresentation.SnesCartridge() : RomPresentation.NesCartridge();
            string path = Path.IsPathRooted(definition.Model) ? definition.Model : Path.Combine(DataPaths.Models, definition.Model);
            GltfImport import = await GetImport(path);
            if (cancellationToken.IsCancellationRequested || this == null || token != generation || import == null) return;
            candidate = new GameObject(platform.DisplayName + " Cartridge");
            candidate.AddComponent<RomVisualContentMarker>();
            candidate.SetActive(false);
            await InstantiationGate.WaitAsync(cancellationToken);
            gateHeld = true;
            await import.InstantiateMainSceneAsync(candidate.transform, cancellationToken);
            InstantiationGate.Release();
            gateHeld = false;
            if (cancellationToken.IsCancellationRequested || this == null || token != generation) return;
            candidate.transform.SetParent(transform, false);
            content = candidate;
            candidate = null;
            currentDefinition = definition;
            ApplyArtwork(item, definition, platform.PhysicalFormat);
            ConfigureModel(content.transform, definition, usage);
            SetLayer(content.transform, gameObject.layer);
            content.SetActive(visible);
            RefreshPlacementPreviewMaterials();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { MelonLogger.Error($"Could not create physical media for '{item.DisplayName}': {ex}"); }
        finally
        {
            if (gateHeld) InstantiationGate.Release();
            if (candidate != null) Destroy(candidate);
        }
    }

    private void ConfigureModel(Transform model, RomPresentation definition, RomVisualUsage usage)
    {
        float[] rotation = usage switch
        {
            RomVisualUsage.Held => definition.HeldRotation,
            RomVisualUsage.Inspect => definition.InspectionRotation,
            RomVisualUsage.Loose => definition.LooseRotation,
            _ => definition.Rotation
        } ?? Array.Empty<float>();
        appliedPlacementTurn = ShelfPlacementQuarterTurn(usage, definition);
        model.localRotation = Quaternion.Euler(Value(rotation, 0), Value(rotation, 1), Value(rotation, 2))
            * Quaternion.AngleAxis(definition.FaceRotationDegrees + appliedPlacementTurn, Vector3.forward);
        model.localPosition = Vector3.zero;
        model.localScale = Vector3.one;
        Renderer[] renderers = model.GetComponentsInChildren<Renderer>(true);
        Bounds bounds = RelativeBounds(model, renderers);
        float longest = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z));
        if (longest > 0.0001f) model.localScale = Vector3.one * (definition.HeightMetres / longest);

        bounds = RelativeBounds(transform, renderers);
        if (layoutAnchor != null && usage is RomVisualUsage.Shelf or RomVisualUsage.SourceBox)
        {
            Bounds anchorBounds = RelativeBounds(transform, new[] { layoutAnchor });
            model.localPosition += anchorBounds.center - bounds.center;
            Bounds replacementWorldBounds = WorldBounds(renderers);
            model.position += Vector3.up * (layoutAnchor.bounds.min.y - replacementWorldBounds.min.y);
        }
        else
        {
            model.localPosition -= bounds.center;
            if (usage == RomVisualUsage.Loose)
            {
                Bounds worldBounds = WorldBounds(renderers);
                model.position += Vector3.up * (transform.position.y - worldBounds.min.y);
            }
        }

        float[] offset = definition.Offset ?? Array.Empty<float>();
        model.localPosition += new Vector3(Value(offset, 0), Value(offset, 1), Value(offset, 2));
    }

    private void ApplyArtwork(RomData item, RomPresentation definition, RomPhysicalFormat format)
    {
        byte[] bytes = item.CoverArtBytes;
        if (bytes == null || bytes.Length == 0) return;
        artwork = new Texture2D(2, 2, TextureFormat.RGBA32, true);
        if (!artwork.LoadImage(bytes)) return;
        Renderer labelRenderer = null;
        if (format == RomPhysicalFormat.SnesCartridge)
        {
            Renderer[] renderers = content.GetComponentsInChildren<Renderer>(true);
            Material template = FirstMaterial(renderers);
            if (template != null)
            {
                Bounds bounds = RelativeBounds(content.transform, renderers);
                labelRenderer = SnesCartridgeLabel.Create(content.transform, bounds, template, out Mesh mesh, out Material material);
                generated.Add(mesh); generated.Add(material);
                SnesCartridgeLabel.CreateEndLabel(content.transform, bounds, template, item.DisplayName, out Mesh endMesh, out Material endMaterial, out Texture2D endTexture);
                generated.Add(endMesh); generated.Add(endMaterial); generated.Add(endTexture);
                SnesCartridgeLabel.CreateWrapBridge(content.transform, bounds, template, out Mesh bridgeMesh, out Material bridgeMaterial);
                generated.Add(bridgeMesh); generated.Add(bridgeMaterial);
            }
        }
        else
        {
            foreach (Renderer renderer in content.GetComponentsInChildren<Renderer>(true))
            foreach (Material material in renderer.materials)
                if (material != null && material.name.IndexOf(definition.LabelMaterial ?? string.Empty, StringComparison.OrdinalIgnoreCase) >= 0)
                    { labelRenderer = renderer; ApplyLabelUvTransform(renderer, definition); break; }

        }
        if (labelRenderer == null) return;
        Texture label = artwork;
        if (format == RomPhysicalFormat.NesCartridge) { generatedLabel = NesCartridgeLabel.Create(item.DisplayName, artwork); label = generatedLabel; }
        foreach (Material material in labelRenderer.materials)
        {
            if (material == null || material.name.IndexOf(definition.LabelMaterial ?? string.Empty, StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (format == RomPhysicalFormat.NesCartridge) embeddedArtwork.Add(new EmbeddedArtworkBinding(material, label));
            material.mainTexture = label;
            SetTexture(material, "_MainTex", label); SetTexture(material, "_BaseMap", label); SetTexture(material, "_BaseColorTexture", label);
        }
        if (format == RomPhysicalFormat.NesCartridge)
        {
            foreach (EmbeddedArtworkBinding binding in embeddedArtwork) binding.SetVisible(artworkVisible);
        }
        else
        {
            labelRenderer.name += " Artwork";
            labelRenderer.enabled = artworkVisible;
        }
    }

    private void CreateProcedural(RomData item, RomPlatform platform, RomVisualUsage usage)
    {
        content = new GameObject(platform.DisplayName + " Media");
        content.AddComponent<RomVisualContentMarker>();
        content.transform.SetParent(transform, false);
        content.SetActive(visible);
        if (platform.PhysicalFormat == RomPhysicalFormat.HandheldCartridge)
        {
            CreateHandheldRetailBox(item, platform, usage);
            return;
        }
        PrimitiveType shape = platform.PhysicalFormat == RomPhysicalFormat.Disc ? PrimitiveType.Cylinder : PrimitiveType.Cube;
        GameObject body = GameObject.CreatePrimitive(shape);
        body.name = platform.PhysicalFormat == RomPhysicalFormat.Disc ? "Disc" : "Cartridge";
        body.transform.SetParent(content.transform, false);
        Collider childCollider = body.GetComponent<Collider>(); if (childCollider != null) Destroy(childCollider);
        body.transform.localScale = platform.PhysicalFormat switch
        {
            RomPhysicalFormat.Disc => new Vector3(0.065f, 0.002f, 0.065f),
            RomPhysicalFormat.HandheldCartridge => new Vector3(0.065f, 0.075f, 0.012f),
            _ => new Vector3(0.13f, 0.085f, 0.018f)
        };
        body.transform.localRotation = platform.PhysicalFormat == RomPhysicalFormat.Disc ? Quaternion.Euler(90f, 0f, 0f) : Quaternion.identity;
        Renderer bodyRenderer = body.GetComponent<Renderer>();
        bodyRenderer.material = new Material(Shader.Find("Standard")); generated.Add(bodyRenderer.material);
        bodyRenderer.material.color = platform.PhysicalFormat == RomPhysicalFormat.Disc ? new Color(0.75f, 0.78f, 0.82f) : new Color(0.12f, 0.12f, 0.12f);
        byte[] bytes = item.CoverArtBytes;
        if (bytes != null && bytes.Length > 0)
        {
            artwork = new Texture2D(2, 2, TextureFormat.RGBA32, true);
            if (artwork.LoadImage(bytes))
            {
                GameObject label = GameObject.CreatePrimitive(PrimitiveType.Quad);
                label.name = "ROM Artwork";
                Collider labelCollider = label.GetComponent<Collider>(); if (labelCollider != null) Destroy(labelCollider);
                label.transform.SetParent(content.transform, false);
                label.transform.localScale = platform.PhysicalFormat == RomPhysicalFormat.Disc ? new Vector3(0.085f, 0.085f, 1f) : new Vector3(0.09f, 0.06f, 1f);
                label.transform.localPosition = platform.PhysicalFormat == RomPhysicalFormat.Disc ? new Vector3(0f, 0f, -0.0021f) : new Vector3(0f, 0f, -0.0091f);
                Renderer renderer = label.GetComponent<Renderer>();
                renderer.material = new Material(Shader.Find("Unlit/Texture")); generated.Add(renderer.material);
                renderer.material.mainTexture = artwork;
                renderer.enabled = artworkVisible;
            }
        }
        content.transform.localRotation = usage == RomVisualUsage.Loose ? Quaternion.Euler(90f, 0f, 0f) : Quaternion.identity;
        SetLayer(content.transform, gameObject.layer);
    }

    private void CreateHandheldRetailBox(RomData item, RomPlatform platform, RomVisualUsage usage)
    {
        byte[] bytes = item.CoverArtBytes;
        if (bytes != null && bytes.Length > 0)
        {
            artwork = new Texture2D(2, 2, TextureFormat.RGBA32, true);
            if (!artwork.LoadImage(bytes)) { Destroy(artwork); artwork = null; }
        }

        generatedLabel = HandheldRetailBoxTexture.Create(platform.Id, artwork, out Color bodyColor);
        Color chosenColor = Color.Lerp(bodyColor, Color.black, 0.32f);
        if (!string.IsNullOrWhiteSpace(item.CaseColor) && ColorUtility.TryParseHtmlString(item.CaseColor, out Color savedColor)) chosenColor = savedColor;
        currentCaseColor = chosenColor;
        float height = 0.125f;
        float width = height * HandheldRetailBoxTexture.Aspect(platform.Id);
        float depth = 0.025f;
        Texture2D cardboard = HandheldRetailBoxTexture.LoadCardboardSurface();
        Texture2D topBottom = HandheldRetailBoxTexture.LoadTopBottomSurface();
        if (cardboard != null) generated.Add(cardboard);
        if (topBottom != null) generated.Add(topBottom);

        GameObject body = GameObject.CreatePrimitive(PrimitiveType.Cube);
        body.name = platform.DisplayName + " Retail Box";
        body.transform.SetParent(content.transform, false);
        body.transform.localScale = new Vector3(width, height, depth);
        Collider bodyCollider = body.GetComponent<Collider>(); if (bodyCollider != null) Destroy(bodyCollider);
        Renderer bodyRenderer = body.GetComponent<Renderer>();
        bodyRenderer.material = CreateSolidMaterial(chosenColor, 0.025f, cardboard);
        caseMaterial = bodyRenderer.material;
        caseMaterials.Add(bodyRenderer.material);
        generated.Add(bodyRenderer.material);

        if (topBottom != null)
        {
            CreateBoxFace("Top", new Vector3(0f, height * 0.5f + 0.0003f, 0f), Quaternion.Euler(90f, 0f, 0f),
                new Vector3(width, depth, 1f), chosenColor, topBottom);
            CreateBoxFace("Bottom", new Vector3(0f, -height * 0.5f - 0.0003f, 0f), Quaternion.Euler(-90f, 0f, 0f),
                new Vector3(width, depth, 1f), chosenColor, topBottom);
        }

        GameObject front = GameObject.CreatePrimitive(PrimitiveType.Quad);
        front.name = platform.DisplayName + " Box Artwork";
        front.transform.SetParent(content.transform, false);
        front.transform.localScale = new Vector3(width, height, 1f);
        front.transform.localPosition = new Vector3(0f, 0f, -(depth * 0.5f + 0.0006f));
        Collider frontCollider = front.GetComponent<Collider>(); if (frontCollider != null) Destroy(frontCollider);
        Renderer frontRenderer = front.GetComponent<Renderer>();
        frontRenderer.material = new Material(Shader.Find("Unlit/Texture"));
        frontRenderer.material.mainTexture = generatedLabel;
        frontRenderer.enabled = artworkVisible;
        generated.Add(frontRenderer.material);

        ConfigureProcedural(content.transform, usage);
        SetLayer(content.transform, gameObject.layer);
        RefreshPlacementPreviewMaterials();
    }

    private Material CreateSolidMaterial(Color color, float smoothness, Texture texture = null)
    {
        Shader shader = layoutAnchor?.sharedMaterial?.shader
            ?? Shader.Find("Universal Render Pipeline/Lit")
            ?? Shader.Find("Universal Render Pipeline/Simple Lit")
            ?? Shader.Find("Unlit/Texture")
            ?? Shader.Find("Standard");
        var material = new Material(shader);
        Texture surface = texture != null ? texture : Texture2D.whiteTexture;
        if (material.HasProperty("_BaseMap")) material.SetTexture("_BaseMap", surface);
        if (material.HasProperty("_MainTex")) material.SetTexture("_MainTex", surface);
        if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
        if (material.HasProperty("_Color")) material.SetColor("_Color", color);
        material.color = color;
        if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", smoothness);
        if (material.HasProperty("_Glossiness")) material.SetFloat("_Glossiness", smoothness);
        if (material.HasProperty("_Surface")) material.SetFloat("_Surface", 0f);
        material.renderQueue = -1;
        return material;
    }

    private void CreateBoxFace(string faceName, Vector3 position, Quaternion rotation, Vector3 scale, Color color, Texture texture)
    {
        GameObject face = GameObject.CreatePrimitive(PrimitiveType.Quad);
        face.name = "Handheld Box " + faceName;
        face.transform.SetParent(content.transform, false);
        face.transform.localPosition = position;
        face.transform.localRotation = rotation;
        face.transform.localScale = scale;
        Collider collider = face.GetComponent<Collider>(); if (collider != null) Destroy(collider);
        Renderer renderer = face.GetComponent<Renderer>();
        renderer.material = CreateSolidMaterial(color, 0.02f, texture);
        caseMaterials.Add(renderer.material);
        generated.Add(renderer.material);
    }

    private void ApplyCaseColor(Color color)
    {
        currentCaseColor = color;
        foreach (Material material in caseMaterials)
        {
            if (material == null) continue;
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Color")) material.SetColor("_Color", color);
            material.color = color;
        }
    }

    private void ConfigureProcedural(Transform model, RomVisualUsage usage)
    {
        model.localPosition = Vector3.zero;
        model.localRotation = usage switch
        {
            RomVisualUsage.Shelf or RomVisualUsage.SourceBox => Quaternion.Euler(-90f, 0f, 0f),
            RomVisualUsage.Loose => Quaternion.Euler(90f, 0f, 0f),
            _ => Quaternion.identity
        };
        Renderer[] renderers = model.GetComponentsInChildren<Renderer>(true);
        Bounds bounds = RelativeBounds(transform, renderers);
        if (layoutAnchor != null && usage is RomVisualUsage.Shelf or RomVisualUsage.SourceBox)
        {
            Bounds anchorBounds = RelativeBounds(transform, new[] { layoutAnchor });
            model.localPosition += anchorBounds.center - bounds.center;
            Bounds replacementWorldBounds = WorldBounds(renderers);
            model.position += Vector3.up * (layoutAnchor.bounds.min.y - replacementWorldBounds.min.y);
        }
        else
        {
            model.localPosition -= bounds.center;
            if (usage == RomVisualUsage.Loose)
            {
                Bounds worldBounds = WorldBounds(renderers);
                model.position += Vector3.up * (transform.position.y - worldBounds.min.y);
            }
        }
    }

    private static Task<GltfImport> GetImport(string path)
    {
        if (!Imports.TryGetValue(path, out Task<GltfImport> task)) { task = LoadImport(path); Imports[path] = task; }
        return task;
    }
    private static async Task<GltfImport> LoadImport(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Cartridge model not found.", path);
        var import = new GltfImport(null, null, null, new ConsoleLogger());
        string directory = Path.GetDirectoryName(path);
        if (!await import.LoadFile(Path.GetFullPath(path), new Uri(directory + Path.DirectorySeparatorChar))) throw new InvalidOperationException("glTFast could not load " + path);
        return import;
    }
    private static Material FirstMaterial(Renderer[] renderers)
    {
        foreach (Renderer renderer in renderers) foreach (Material material in renderer.sharedMaterials) if (material != null) return material;
        return null;
    }
    private void ApplyLabelUvTransform(Renderer renderer, RomPresentation definition)
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

        Mesh adjusted = Instantiate(source);
        adjusted.name = source.name + " BR-Libretro Label UV";
        Vector2[] uvs = adjusted.uv;
        for (int i = 0; i < uvs.Length; i++)
        {
            if (flipX) uvs[i].x = 1f - uvs[i].x;
            if (flipY) uvs[i].y = 1f - uvs[i].y;
        }
        adjusted.uv = uvs;
        if (filter != null) filter.sharedMesh = adjusted;
        else skinned.sharedMesh = adjusted;
        generated.Add(adjusted);
    }
    private float ShelfPlacementQuarterTurn(RomVisualUsage usage, RomPresentation definition)
    {
        if (usage == RomVisualUsage.Loose && IsLoosePlacementPreview()
            && TryGetShelfPreviewPlacement(out MediaPlacement previewPlacement))
        {
            return previewPlacement switch
            {
                MediaPlacement.Spine => definition.ShelfSpineQuarterTurnDegrees,
                MediaPlacement.FaceUp => definition.ShelfFaceUpQuarterTurnDegrees,
                _ => 0f
            };
        }

        if (usage is not RomVisualUsage.Shelf and not RomVisualUsage.SourceBox) return 0f;
        Quaternion targetRotation = transform.localRotation;
        if (Quaternion.Angle(targetRotation, Quaternion.Euler(90f, -90f, 0f)) < 2f)
            return definition.ShelfSpineQuarterTurnDegrees;
        if (Quaternion.Angle(targetRotation, Quaternion.Euler(180f, -90f, 0f)) < 2f)
            return definition.ShelfFaceUpQuarterTurnDegrees;
        return 0f;
    }
    private bool IsLoosePlacementPreview()
    {
        if (currentUsage != RomVisualUsage.Loose) return false;
        PlacementTag tag = GetComponentInParent<PlacementTag>();
        return tag?.IsPreview == true;
    }
    private static bool TryGetShelfPreviewPlacement(out MediaPlacement placement)
    {
        placement = MediaPlacement.FaceOut;
        PlayerInteractionTool tool = FindFirstObjectByType<PlayerInteractionTool>();
        if (tool == null || CurrentMediaPlacementField == null || CurrentLookingAtContainerField == null
            || CurrentLookingAtContainerField.GetValue(tool) is not PlaceableMediaContainer)
            return false;
        object value = CurrentMediaPlacementField.GetValue(tool);
        if (value is not MediaPlacement current) return false;
        placement = current;
        return true;
    }
    private void RefreshPlacementPreviewMaterials()
    {
        PlacementTag tag = GetComponent<PlacementTag>();
        if (tag?.IsPreview != true) return;
        PlaceablePainter painter = GetComponent<PlaceablePainter>();
        if (painter == null) return;
        painter.InitializeMaterials();
        painter.SetupAsForcedPlaceable();
        painter.ReCacheAllBaseMaterialsToCurrent();
    }
    private static Bounds RelativeBounds(Transform parent, Renderer[] renderers)
    {
        bool found = false; Bounds result = default;
        foreach (Renderer renderer in renderers)
        {
            if (renderer == null) continue;
            Bounds local = renderer.localBounds;
            for (int x = -1; x <= 1; x += 2) for (int y = -1; y <= 1; y += 2) for (int z = -1; z <= 1; z += 2)
            {
                Vector3 point = parent.InverseTransformPoint(renderer.transform.TransformPoint(local.center + Vector3.Scale(local.extents, new Vector3(x, y, z))));
                if (!found) { result = new Bounds(point, Vector3.zero); found = true; } else result.Encapsulate(point);
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
    private static float Value(float[] values, int index) => values != null && values.Length > index ? values[index] : 0f;
    private static void SetTexture(Material material, string property, Texture texture)
    {
        if (!material.HasProperty(property)) return;
        material.SetTexture(property, texture);
        material.SetTextureScale(property, Vector2.one);
        material.SetTextureOffset(property, Vector2.zero);
    }
    private static void SetLayer(Transform root, int layer) { root.gameObject.layer = layer; for (int i = 0; i < root.childCount; i++) SetLayer(root.GetChild(i), layer); }
    private void Update()
    {
        if (content == null || currentDefinition == null) return;
        float placementTurn = ShelfPlacementQuarterTurn(currentUsage, currentDefinition);
        if (float.IsNaN(appliedPlacementTurn) || Mathf.Abs(Mathf.DeltaAngle(appliedPlacementTurn, placementTurn)) > 0.1f)
            ConfigureModel(content.transform, currentDefinition, currentUsage);
    }
    private void OnDestroy() => Clear();
}

internal sealed class RomVisualContentMarker : MonoBehaviour { }
