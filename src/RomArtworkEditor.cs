using HarmonyLib;
using MelonLoader;
using SFB;
using SteamShelf.Media;
using SteamShelf.PlayerTools;
using SteamShelf.UI;
using SteamGridBox;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace BR_Libretro;

internal static class RomArtworkEditor
{
    private const int MaximumImageBytes = 20 * 1024 * 1024;
    private static readonly Dictionary<TMP_Text, string> OriginalLabels = new();
    private static readonly HashSet<GameObject> HiddenButtons = new();
    private static readonly FieldInfo OptionsPanelField = AccessTools.Field(typeof(Menu_Inspect), "optionsPanel");
    private static readonly FieldInfo OpenButtonTrackerField = AccessTools.Field(typeof(Menu_Inspect), "m_OpenButtonTracker");
    private static string activeRomId;
    private static RomData inspectedRom;
    private static Menu_Inspect inspectorMenu;

    internal static RomData CurrentRom()
    {
        if (inspectedRom != null) return inspectedRom;
        PlayerInteractionTool tool = UnityEngine.Object.FindFirstObjectByType<PlayerInteractionTool>();
        if (tool?.CurrentHeldMediaItem is RomData held) return held;
        BoxInspector inspector = UnityEngine.Object.FindFirstObjectByType<BoxInspector>();
        return inspector?.heldMediaInfo as RomData;
    }

    internal static void Tick()
    {
        inspectorMenu ??= UnityEngine.Object.FindFirstObjectByType<Menu_Inspect>();
        if (inspectorMenu != null && inspectorMenu.isActiveAndEnabled) ApplyInspectorMenu(inspectorMenu);
        else if (activeRomId != null) RestoreInspectorMenu();
    }

    internal static void SetInspectedItem(IMediaItem item)
    {
        inspectedRom = item as RomData;
        if (inspectedRom == null && activeRomId != null) RestoreInspectorMenu();
    }

    internal static void ClearInspectedItem()
    {
        inspectedRom = null;
        RestoreInspectorMenu();
    }

    internal static void ApplyInspectorMenu(Menu_Inspect menu)
    {
        RomData item = CurrentRom();
        if (menu == null || item == null)
        {
            if (activeRomId != null) RestoreInspectorMenu();
            return;
        }
        if (!string.Equals(activeRomId, item.Id, StringComparison.OrdinalIgnoreCase))
        {
            RestoreInspectorMenu();
            activeRomId = item.Id;
        }
        bool supportsBoxLogo = RomPlatformCatalog.TryGet(item.PlatformId, out RomPlatform platform) &&
            platform.PhysicalFormat == RomPhysicalFormat.HandheldCartridge;

        // Main.unity owns these as direct optionsPanel children. Targeting the
        // serialized controls avoids touching unrelated localized UI text.
        RectTransform options = OptionsPanelField?.GetValue(menu) as RectTransform;
        SetButtonLabel(options, "AddScreenshots", "SteamGridDB Art");
        if (supportsBoxLogo) SetButtonLabel(options, "ChangeDiskArt", "Box Logo");
        else HideButton(options, "ChangeDiskArt");

        // m_OpenButtonTracker is attached to the actual world-space OpenButton.
        Component openTracker = OpenButtonTrackerField?.GetValue(menu) as Component;
        if (openTracker != null)
            foreach (TMP_Text label in openTracker.GetComponentsInChildren<TMP_Text>(true))
                SetLabel(label, "Play");
    }

    internal static void RestoreInspectorMenu()
    {
        activeRomId = null;
        foreach (KeyValuePair<TMP_Text, string> pair in OriginalLabels)
            if (pair.Key != null) pair.Key.text = pair.Value;
        OriginalLabels.Clear();
        foreach (GameObject button in HiddenButtons)
            if (button != null) button.SetActive(true);
        HiddenButtons.Clear();
    }

    internal static bool ChooseLocalImage(RomData item, bool boxLogo)
    {
        if (item == null) return false;
        string[] selected = StandaloneFileBrowser.OpenFilePanel(
            boxLogo ? "Choose Box Logo" : "Choose Cartridge / Box Art", string.Empty,
            new[] { new ExtensionFilter("Images", "png", "jpg", "jpeg") }, false);
        if (selected == null || selected.Length == 0 || string.IsNullOrWhiteSpace(selected[0])) return false;
        try
        {
            byte[] bytes = File.ReadAllBytes(selected[0]);
            return SaveBytes(item, bytes, boxLogo, boxLogo ? "local box logo" : "local cover art");
        }
        catch (Exception ex)
        {
            MelonLogger.Warning("Could not apply ROM artwork: " + ex.Message);
            ModsPanel.ModsUi.ShowToast("Could not apply artwork: " + ex.Message, 8f);
            return false;
        }
    }

    internal static bool SaveBytes(RomData item, byte[] bytes, bool boxLogo, string description)
    {
        if (item == null || bytes == null || bytes.Length < 128 || bytes.Length > MaximumImageBytes)
        {
            ModsPanel.ModsUi.ShowToast("The selected artwork is empty or too large (20 MB maximum).", 8f);
            return false;
        }

        var probe = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        bool valid = probe.LoadImage(bytes);
        UnityEngine.Object.Destroy(probe);
        if (!valid)
        {
            ModsPanel.ModsUi.ShowToast("That file is not a supported PNG or JPEG image.", 8f);
            return false;
        }

        Directory.CreateDirectory(DataPaths.Artwork);
        string path = Path.Combine(DataPaths.Artwork, item.Id + (boxLogo ? ".logo" : ".cover"));
        string staging = path + "." + Guid.NewGuid().ToString("N") + ".staging";
        try
        {
            File.WriteAllBytes(staging, bytes);
            if (File.Exists(path)) File.Replace(staging, path, null, true); else File.Move(staging, path);
            RomLibrarySystem.ApplyArtwork(item, path, boxLogo);
            ModsPanel.ModsUi.ShowToast(item.DisplayName + " " + description + " saved.", 6f);
            return true;
        }
        catch
        {
            if (File.Exists(staging)) File.Delete(staging);
            throw;
        }
    }

    internal static void OpenSteamGridArtwork(RomData item, Action back)
    {
        if (item == null) return;
        bool supportsLogo = RomPlatformCatalog.TryGet(item.PlatformId, out RomPlatform platform) &&
            platform.PhysicalFormat == RomPhysicalFormat.HandheldCartridge;
        SteamGridArtworkBrowser.Open(new SteamGridArtworkRequest
        {
            Id = "rom-" + item.Id,
            Title = item.DisplayName,
            SearchQuery = RomTitle.Clean(item.DisplayName),
            IncludeLogos = supportsLogo,
            Back = back ?? ModsPanel.ModsUi.CloseMenu,
            Apply = (bytes, kind) =>
            {
                bool logo = kind == SteamGridArtworkKind.Logo;
                SaveBytes(item, bytes, logo, logo ? "SteamGridDB box logo" : "SteamGridDB artwork");
            }
        });
    }

    private static void SetLabel(TMP_Text label, string value)
    {
        if (!OriginalLabels.ContainsKey(label)) OriginalLabels[label] = label.text;
        label.text = value;
    }

    private static void SetButtonLabel(RectTransform options, string childName, string value)
    {
        Transform child = options?.Find(childName);
        TMP_Text label = child?.GetComponentInChildren<TMP_Text>(true);
        if (label != null) SetLabel(label, value);
    }

    private static void HideButton(RectTransform options, string childName)
    {
        Button button = options?.Find(childName)?.GetComponent<Button>();
        if (button == null) return;
        HiddenButtons.Add(button.gameObject);
        button.gameObject.SetActive(false);
    }
}

[HarmonyPatch(typeof(Menu_Inspect), "OnPreShow")]
internal static class RomInspectMenuPatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(Menu_Inspect __instance) => RomArtworkEditor.ApplyInspectorMenu(__instance);
}

[HarmonyPatch(typeof(Menu_Inspect), nameof(Menu_Inspect.OnClick_ChangeArt))]
internal static class RomChangeArtPatch
{
    private static bool Prefix()
    {
        RomData item = RomArtworkEditor.CurrentRom();
        if (item == null) return true;
        RomArtworkEditor.ChooseLocalImage(item, false);
        return false;
    }
}

[HarmonyPatch(typeof(Menu_Inspect), nameof(Menu_Inspect.OnClick_ChangeDiskArt))]
internal static class RomChangeBoxLogoPatch
{
    private static bool Prefix()
    {
        RomData item = RomArtworkEditor.CurrentRom();
        if (item == null) return true;
        RomArtworkEditor.ChooseLocalImage(item, true);
        return false;
    }
}

[HarmonyPatch(typeof(Menu_Inspect), nameof(Menu_Inspect.OnClick_AddScreenShot))]
internal static class RomSteamGridDbArtPatch
{
    private static bool Prefix()
    {
        RomData item = RomArtworkEditor.CurrentRom();
        if (item == null) return true;
        RomArtworkEditor.OpenSteamGridArtwork(item, ModsPanel.ModsUi.CloseMenu);
        return false;
    }
}

[HarmonyPatch(typeof(BoxInspector), nameof(BoxInspector.SetHeldMedia))]
internal static class RomInspectorItemPatch
{
    private static void Postfix(IMediaItem item) => RomArtworkEditor.SetInspectedItem(item);
}

[HarmonyPatch(typeof(BoxInspector), nameof(BoxInspector.OnToolDeactivated))]
internal static class RomInspectorExitPatch
{
    private static void Postfix() => RomArtworkEditor.ClearInspectedItem();
}

[HarmonyPatch(typeof(BoxInspector), nameof(BoxInspector.GetHeldMaterialTint))]
internal static class RomInspectorGetColourPatch
{
    private static bool Prefix(ref Color __result)
    {
        RomData item = RomArtworkEditor.CurrentRom();
        if (item == null) return true;
        __result = RomMediaVisual.TryGetCaseColor(item, out Color color) ? color : Color.white;
        return false;
    }
}

[HarmonyPatch(typeof(BoxInspector), nameof(BoxInspector.SetHeldMaterialTint))]
internal static class RomInspectorSetColourPatch
{
    private static bool Prefix(Color tint)
    {
        RomData item = RomArtworkEditor.CurrentRom();
        if (item == null) return true;
        RomLibrarySystem.SetCaseColor(item.Id, tint);
        return false;
    }
}
