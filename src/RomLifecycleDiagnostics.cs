using HarmonyLib;
using MelonLoader;
using SteamShelf.Input;
using SteamShelf.Media;
using SteamShelf.Placeables;
using SteamShelf.PlayerTools;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace BR_Libretro;

internal static class RomLifecycleDiagnostics
{
    private static readonly object Sync = new();
    private static readonly FieldInfo HeldStackField = AccessTools.Field(typeof(PlayerInteractionTool), "heldMediaStack");
    private static readonly FieldInfo HeldPrefabField = AccessTools.Field(typeof(PlayerInteractionTool), "heldPrefab");
    private static readonly Dictionary<string, string> LastStates = new(StringComparer.OrdinalIgnoreCase);
    private static string logPath;
    private static int sequence;
    private static string lastToolState = string.Empty;

    internal static string LogPath { get { EnsureInitialized(); return logPath; } }

    internal static void Initialize()
    {
        if (!string.IsNullOrWhiteSpace(logPath)) return;
        string folder = Path.Combine(DataPaths.Config, "diagnostics");
        Directory.CreateDirectory(folder);
        logPath = Path.Combine(folder, $"rom-lifecycle-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        WriteRaw("BR-Libretro ROM lifecycle diagnostic session");
        WriteRaw($"Version=0.6.35 Started={DateTimeOffset.Now:O}");
        MelonLogger.Msg($"ROM lifecycle diagnostics: {logPath}");
    }

    internal static void Poll(PlayerInteractionTool tool)
    {
        EnsureInitialized();
        foreach (RomData item in RomLibrarySystem.GetKnownItems())
        {
            string state = State(item, tool);
            if (!LastStates.TryGetValue(item.Id, out string previous) || !string.Equals(previous, state, StringComparison.Ordinal))
            {
                LastStates[item.Id] = state;
                Write("STATE-CHANGE", item, tool, null, null, previous);
            }
        }

        string toolState = ToolState(tool, null);
        if (!string.Equals(toolState, lastToolState, StringComparison.Ordinal))
        {
            string previous = lastToolState;
            lastToolState = toolState;
            WriteRaw($"{Prefix()} TOOL-CHANGE {toolState} previous=[{previous}]");
        }
    }

    internal static void Record(string action, RomData item, Component source = null, RomVisualUsage? usage = null, string detail = null)
    {
        EnsureInitialized();
        Write(action, item, UnityEngine.Object.FindFirstObjectByType<PlayerInteractionTool>(), source, usage, detail);
    }

    internal static void RecordAll(string action, string detail = null)
    {
        EnsureInitialized();
        PlayerInteractionTool tool = UnityEngine.Object.FindFirstObjectByType<PlayerInteractionTool>();
        IReadOnlyList<RomData> items = RomLibrarySystem.GetKnownItems();
        if (items.Count == 0) WriteRaw($"{Prefix()} {action} no-ROM-items {ToolState(tool, null)} detail=[{Clean(detail)}]");
        foreach (RomData item in items) Write(action, item, tool, null, null, detail);
    }

    private static void Write(string action, RomData item, PlayerInteractionTool tool, Component source, RomVisualUsage? usage, string detail)
    {
        string sourceText = source == null
            ? "none"
            : $"{source.GetType().Name}#{source.GetInstanceID()} object={Clean(source.gameObject?.name)} active={source.gameObject?.activeInHierarchy}";
        string itemText = item == null
            ? "item=null"
            : $"item={Clean(item.DisplayName)} id={item.Id} platform={item.PlatformId} {State(item, tool)}";
        WriteRaw($"{Prefix()} {action} {itemText} source=[{sourceText}] usage={usage?.ToString() ?? "n/a"} detail=[{Clean(detail)}]");
    }

    private static string State(RomData item, PlayerInteractionTool tool)
    {
        if (item == null) return "null";
        bool reserved = false;
        try { reserved = PlaceableMediaContainer.IsMediaReserved(item.Ref); } catch { }
        return $"spawned={item.IsSpawned} inHand={item.IsInHand} loaded={item.IsFullyLoaded} reserved={reserved} {ToolState(tool, item)}";
    }

    private static string ToolState(PlayerInteractionTool tool, RomData subject)
    {
        if (tool == null) return "tool=null";
        var stack = HeldStackField?.GetValue(tool) as List<IMediaItem>;
        GameObject heldPrefab = HeldPrefabField?.GetValue(tool) as GameObject;
        IMediaItem current = tool.CurrentHeldMediaItem;
        bool contains = subject != null && stack?.Contains(subject) == true;
        string stackIds = stack == null ? "?" : string.Join(",", stack.Select(ItemKey));
        return $"current={ItemKey(current)} stackCount={stack?.Count ?? -1} stackHasItem={contains} stack=[{stackIds}] heldPrefab={Clean(heldPrefab?.name)}#{(heldPrefab == null ? 0 : heldPrefab.GetInstanceID())} holdingMedia={tool.IsHoldingMediaBox}";
    }

    private static string ItemKey(IMediaItem item) => item == null ? "none" : $"{(int)item.Ref.Type}:{Clean(item.Ref.Id)}";
    private static string Clean(string value) => string.IsNullOrEmpty(value) ? string.Empty : value.Replace('\r', ' ').Replace('\n', ' ').Replace('[', '(').Replace(']', ')');
    private static string Prefix() => $"{++sequence:D5} {DateTimeOffset.Now:O} frame={Time.frameCount}";

    private static void EnsureInitialized()
    {
        if (string.IsNullOrWhiteSpace(logPath)) Initialize();
    }

    private static void WriteRaw(string line)
    {
        try
        {
            lock (Sync) File.AppendAllText(logPath, line + Environment.NewLine);
            MelonLogger.Msg("[ROM-LIFECYCLE] " + line);
        }
        catch (Exception ex) { MelonLogger.Warning("ROM lifecycle diagnostic write failed: " + ex.Message); }
    }
}

[HarmonyPatch(typeof(PlayerInteractionTool), "PushMediaItem")]
internal static class RomDiagnosticPushMediaPatch
{
    private static void Prefix(IMediaItem item) { if (item is RomData rom) RomLifecycleDiagnostics.Record("PushMediaItem.BEFORE", rom); }
    private static void Postfix(IMediaItem item) { if (item is RomData rom) RomLifecycleDiagnostics.Record("PushMediaItem.AFTER", rom); }
}

[HarmonyPatch(typeof(PlayerInteractionTool), "OnMediaInHandChanged")]
internal static class RomDiagnosticHandChangedPatch
{
    private static void Prefix(IMediaItem item) { if (item is RomData rom) RomLifecycleDiagnostics.Record("OnMediaInHandChanged.BEFORE", rom); }
    private static void Postfix(IMediaItem item) { if (item is RomData rom) RomLifecycleDiagnostics.Record("OnMediaInHandChanged.AFTER", rom); }
}

[HarmonyPatch(typeof(PlayerInteractionTool), "PickupItem", new[] { typeof(PlacementTag) })]
internal static class RomDiagnosticLoosePickupPatch
{
    private static RomData Get(PlacementTag placeable) => placeable?.GetComponents<MonoBehaviour>().OfType<BR_MediaAPI.ICustomMediaProp>().FirstOrDefault()?.MediaItem as RomData;
    private static void Prefix(PlacementTag placeable, out RomData __state)
    {
        __state = Get(placeable);
        if (__state != null) RomLifecycleDiagnostics.Record("PickupItem.BEFORE", __state, placeable);
    }
    private static void Postfix(PlacementTag placeable, RomData __state)
    {
        RomData item = __state ?? Get(placeable);
        if (item != null) RomLifecycleDiagnostics.Record("PickupItem.AFTER", item, placeable);
    }
}

[HarmonyPatch(typeof(PlayerInteractionTool), "ClearMediaInHand")]
internal static class RomDiagnosticClearHandPatch
{
    private static void Prefix(PlayerInteractionTool __instance, out RomData __state)
    {
        __state = __instance.CurrentHeldMediaItem as RomData;
        if (__state != null) RomLifecycleDiagnostics.Record("ClearMediaInHand.BEFORE", __state);
    }
    private static void Postfix(RomData __state)
    {
        if (__state != null) RomLifecycleDiagnostics.Record("ClearMediaInHand.AFTER", __state);
    }
}

[HarmonyPatch(typeof(PlayerInteractionTool), "PlaceHeld")]
internal static class RomDiagnosticPlaceHeldPatch
{
    private static void Prefix(PlayerInteractionTool __instance, out RomData __state)
    {
        __state = __instance.CurrentHeldMediaItem as RomData;
        if (__state != null) RomLifecycleDiagnostics.Record("PlaceHeld.BEFORE", __state);
    }
    private static void Postfix(RomData __state)
    {
        if (__state != null) RomLifecycleDiagnostics.Record("PlaceHeld.AFTER", __state);
    }
}

[HarmonyPatch(typeof(PlayerInteractionTool), nameof(PlayerInteractionTool.OnDemandedMediaFromMenu))]
internal static class RomDiagnosticDemandPatch
{
    private static void Prefix(IMediaItem mediaItem) { if (mediaItem is RomData rom) RomLifecycleDiagnostics.Record("OnDemandedMediaFromMenu.BEFORE", rom); }
    private static void Postfix(IMediaItem mediaItem) { if (mediaItem is RomData rom) RomLifecycleDiagnostics.Record("OnDemandedMediaFromMenu.AFTER", rom); }
}

[HarmonyPatch(typeof(PlaceableMediaContainer), nameof(PlaceableMediaContainer.ReleaseAll))]
internal static class RomDiagnosticReleaseAllPatch
{
    private static void Prefix() => RomLifecycleDiagnostics.RecordAll("ReleaseAll.BEFORE");
    private static void Postfix() => RomLifecycleDiagnostics.RecordAll("ReleaseAll.AFTER");
}

[HarmonyPatch(typeof(DeletionTool), nameof(DeletionTool.OnUpdate))]
internal static class RomDiagnosticDeletePatch
{
    private static void Prefix(PlayerInputContext playerInputContext)
    {
        if (playerInputContext?.PrimaryPressedThisFrame == true) RomLifecycleDiagnostics.RecordAll("DeletionTool.BEFORE");
    }
    private static void Postfix(PlayerInputContext playerInputContext)
    {
        if (playerInputContext?.PrimaryPressedThisFrame == true) RomLifecycleDiagnostics.RecordAll("DeletionTool.AFTER");
    }
}
