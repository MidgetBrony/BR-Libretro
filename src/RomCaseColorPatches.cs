using HarmonyLib;
using UnityEngine;

namespace BR_Libretro;

[HarmonyPatch(typeof(BoxInspector), nameof(BoxInspector.GetHeldMaterialTint))]
internal static class RomCaseColorReadPatch
{
    private static void Postfix(BoxInspector __instance, ref Color __result)
    {
        if (__instance?.heldMediaInfo is RomData item && RomMediaVisual.TryGetCaseColor(item, out Color color))
            __result = color;
    }
}

[HarmonyPatch(typeof(BoxInspector), nameof(BoxInspector.SetHeldMaterialTint))]
internal static class RomCaseColorWritePatch
{
    private static void Postfix(BoxInspector __instance, Color tint)
    {
        if (__instance?.heldMediaInfo is RomData item)
            RomLibrarySystem.SetCaseColor(item.Id, tint);
    }
}
