using BR_MediaAPI;
using HarmonyLib;
using SteamShelf;
using SteamShelf.Media;
using SteamShelf.Placeables;
using SteamShelf.PlayerTools;
using SteamShelf.Save;
using System;
using UnityEngine;

namespace BR_Libretro;

internal enum RomVisualUsage { Shelf, SourceBox, Loose, Held, Inspect }

internal static class RomPhysicalMedia
{
    private static PlaceableData placeableData;

    internal static PlaceableData GetPlaceableData()
    {
        if (placeableData != null) return placeableData;
        placeableData = ScriptableObject.CreateInstance<PlaceableData>();
        placeableData.ID = RomMedia.PlaceableId;
        placeableData.DisplayName = "ROM";
        placeableData.PlacementType = PlacementType.Prop;
        placeableData.SetToolType(EToolType.Placeable);
        placeableData.IsLoadedFromMod = false;
        return placeableData;
    }

    internal static GameObject CreateLoose() => CreateProp("ROM Cartridge", RomVisualUsage.Loose);
    internal static GameObject CreateHeld() => CreateDisplay("Held ROM");
    internal static GameObject CreateInspect(RomData item) => CreateDisplay("Inspected ROM");
    internal static void ApplyVisual(GameObject root, RomData item, RomVisualUsage usage) =>
        root?.GetComponent<RomMediaVisual>()?.Apply(item, usage);

    internal static IShelfItem CreateShelf(Transform parent)
    {
        IShelfItem native = ShelfItemFactory.Create(eMediaType.SteamGame, parent);
        Component nativeComponent = native as Component;
        GameObject root = nativeComponent != null ? nativeComponent.gameObject : NewRoot("ROM Shelf Item", parent);
        Renderer anchor = nativeComponent == null ? null
            : AccessTools.Field(nativeComponent.GetType(), "rend")?.GetValue(nativeComponent) as Renderer;
        anchor ??= root.GetComponentInChildren<Renderer>(true);

        foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
            if (renderer != null) renderer.enabled = false;

        RomMediaVisual visual = root.AddComponent<RomMediaVisual>();
        visual.SetLayoutAnchor(anchor);
        RomShelfItem shelf = root.AddComponent<RomShelfItem>();
        RomVisualUsage usage = parent != null && parent.GetComponentInParent<GenericUnplacedMediaBox>(true) != null
            ? RomVisualUsage.SourceBox
            : RomVisualUsage.Shelf;
        shelf.Initialize(visual, usage);
        if (nativeComponent != null) UnityEngine.Object.DestroyImmediate(nativeComponent);
        return shelf;
    }

    private static GameObject CreateProp(string name, RomVisualUsage usage)
    {
        GameObject root = NewRoot(name, null);
        var collider = root.AddComponent<BoxCollider>();
        collider.size = new Vector3(0.12479591f, 0.03278351f, 0.17819786f);
        collider.center = new Vector3(0.0018644333f, 0.01818639f, -0.0013141632f);
        RomMediaVisual visual = root.AddComponent<RomMediaVisual>();
        RomMediaProp prop = root.AddComponent<RomMediaProp>();
        prop.Initialize(visual, usage);
        root.SetActive(true);
        return root;
    }

    private static GameObject CreateDisplay(string name)
    {
        GameObject root = NewRoot(name, null);
        root.AddComponent<RomMediaVisual>();
        root.SetActive(true);
        return root;
    }

    private static GameObject NewRoot(string name, Transform parent)
    {
        var root = new GameObject(name);
        if (parent != null) root.transform.SetParent(parent, false);
        root.transform.localPosition = Vector3.zero;
        root.transform.localRotation = Quaternion.identity;
        root.transform.localScale = Vector3.one;
        return root;
    }
}

internal sealed class RomShelfItem : MonoBehaviour, IShelfItem
{
    private RomMediaVisual visual;
    private RomData item;
    private RomVisualUsage usage;
    private bool fullDisplay = true;
    public bool HasItemAndIsActive => item != null && fullDisplay && gameObject.activeInHierarchy;
    public IMediaItem ItemInfo => item;
    internal void Initialize(RomMediaVisual value, RomVisualUsage visualUsage) { visual = value; usage = visualUsage; Clear(); RomLifecycleDiagnostics.Record("Shelf.Initialize", item, this, usage); }
    public void SetItem(IMediaItem value, bool playTween = false)
    {
        if (value is not RomData rom) { Clear(); return; }
        item = rom;
        rom.IsSpawned = true;
        visual.Apply(rom, usage);
        RomLifecycleDiagnostics.Record("Shelf.SetItem", rom, this, usage, $"playTween={playTween}");
    }
    public void SetItem(MediaRef mediaRef) => SetItem(mediaRef.Type == RomMedia.Type ? RomLibrarySystem.GetItem(mediaRef.Id) : null);
    public void Clear() { RomData previous = item; item = null; visual?.Clear(); RomLifecycleDiagnostics.Record("Shelf.Clear", previous, this, usage); }
    private void OnDestroy() => RomLifecycleDiagnostics.Record("Shelf.OnDestroy", item, this, usage);
    public void HideCoverArt() => visual?.SetArtworkVisible(false);
    public void ShowCoverArt() => visual?.SetArtworkVisible(true);
    public void SetFullDisplayActive(bool active) { fullDisplay = active; visual?.SetVisible(active); }
    public void SetHoveredDisplayActive(bool active) { }
}

internal sealed class RomMediaProp : MonoBehaviour, ICustomMediaProp
{
    [Serializable] private sealed class SaveData { public int MediaType; public string MediaId; }
    [SerializeField] private RomMediaVisual visual;
    [SerializeField] private RomVisualUsage usage;
    [SerializeField] private string mediaId;
    private RomData item;
    private PlaceableSaveState saveState;
    private MediaRef reservedRef;
    public IMediaItem MediaItem { get { Restore(); return item; } }
    public PlaceableSaveState SaveState => saveState;

    internal void Initialize(RomMediaVisual value, RomVisualUsage visualUsage) { visual = value; usage = visualUsage; RomLifecycleDiagnostics.Record("Prop.Initialize", item, this, usage); }
    private void Awake()
    {
        PlayerInteractionTool.SpawnedMediaDemanded += OnSpawnedMediaDemanded;
        RomLifecycleDiagnostics.Record("Prop.Awake", item, this, usage);
    }
    private void OnDestroy()
    {
        Restore();
        RomLifecycleDiagnostics.Record("Prop.OnDestroy.BEFORE", item, this, usage, $"mediaId={mediaId}");
        PlayerInteractionTool.SpawnedMediaDemanded -= OnSpawnedMediaDemanded;
        ClearReservation();
        RomLifecycleDiagnostics.Record("Prop.OnDestroy.AFTER", item, this, usage, $"mediaId={mediaId}");
    }

    public void ApplyMedia(IMediaItem value)
    {
        if (value is not RomData rom) return;
        RomLifecycleDiagnostics.Record("Prop.ApplyMedia.BEFORE", rom, this, usage);
        item = rom;
        mediaId = rom.Id;
        rom.IsSpawned = true;
        rom.IsInHand = usage != RomVisualUsage.Loose;
        saveState ??= new PlaceableSaveState(string.Empty, transform.position, transform.rotation);
        saveState.CustomData = JsonUtility.ToJson(new SaveData { MediaType = RomMedia.TypeId, MediaId = rom.Id });
        visual?.Apply(rom, usage);
        RomLifecycleDiagnostics.Record("Prop.ApplyMedia.AFTER", rom, this, usage);
    }

    public void PopulateFromLoad(PlaceableSaveState state)
    {
        RomLifecycleDiagnostics.Record("Prop.PopulateFromLoad.BEFORE", item, this, usage, state?.CustomData);
        saveState = state;
        SaveData data = JsonUtility.FromJson<SaveData>(state?.CustomData ?? string.Empty);
        if (data == null || data.MediaType != RomMedia.TypeId || string.IsNullOrWhiteSpace(data.MediaId)) return;
        mediaId = data.MediaId;
        reservedRef = new MediaRef(RomMedia.Type, mediaId);
        PlaceableMediaContainer.ReserveMedia(reservedRef);
        Restore();
        if (item != null) { item.IsSpawned = true; item.IsInHand = false; visual?.Apply(item, RomVisualUsage.Loose); ClearReservation(); }
        RomLifecycleDiagnostics.Record("Prop.PopulateFromLoad.AFTER", item, this, usage, $"mediaId={mediaId}");
    }

    public void LinkSaveState(PlaceableSaveState state) { saveState = state; WriteSaveIdentity(); }
    public void RefreshSaveStateReference(PlaceableSaveState state) { saveState = state; WriteSaveIdentity(); }
    public void OnPlaced() { Restore(); RomLifecycleDiagnostics.Record("Prop.OnPlaced.BEFORE", item, this, usage); if (item != null) { item.IsSpawned = true; item.IsInHand = false; visual?.Apply(item, RomVisualUsage.Loose); } RomLifecycleDiagnostics.Record("Prop.OnPlaced.AFTER", item, this, usage); }
    public void OnPickedUp() { Restore(); RomLifecycleDiagnostics.Record("Prop.OnPickedUp.BEFORE", item, this, usage); if (item != null) item.IsInHand = true; RomLifecycleDiagnostics.Record("Prop.OnPickedUp.AFTER", item, this, usage); }
    public void OnDeleted()
    {
        Restore();
        RomLifecycleDiagnostics.Record("Prop.OnDeleted.BEFORE", item, this, usage);
        if (item != null) { item.IsSpawned = false; item.IsInHand = false; }
        RomLifecycleDiagnostics.Record("Prop.OnDeleted.AFTER", item, this, usage);
    }

    private void Restore()
    {
        if (item == null && !string.IsNullOrWhiteSpace(mediaId)) item = RomLibrarySystem.GetItem(mediaId);
    }
    private void WriteSaveIdentity()
    {
        if (saveState == null || string.IsNullOrWhiteSpace(mediaId)) return;
        saveState.CustomData = JsonUtility.ToJson(new SaveData { MediaType = RomMedia.TypeId, MediaId = mediaId });
    }
    private void ClearReservation()
    {
        if (!reservedRef.IsValid()) return;
        RomLifecycleDiagnostics.Record("Prop.ClearReservation.BEFORE", item, this, usage, reservedRef.Id);
        PlaceableMediaContainer.UnreserveMedia(reservedRef);
        reservedRef = default;
        RomLifecycleDiagnostics.Record("Prop.ClearReservation.AFTER", item, this, usage);
    }
    private void OnSpawnedMediaDemanded(MediaRef value)
    {
        Restore();
        RomLifecycleDiagnostics.Record("Prop.SpawnedMediaDemanded.SEEN", item, this, usage, $"demand={(int)value.Type}:{value.Id}");
        if (item != null && value.Type == RomMedia.Type && string.Equals(value.Id, item.Id, StringComparison.OrdinalIgnoreCase))
        {
            RomLifecycleDiagnostics.Record("Prop.SpawnedMediaDemanded.MATCH", item, this, usage);
            Destroy(gameObject);
        }
    }
}
