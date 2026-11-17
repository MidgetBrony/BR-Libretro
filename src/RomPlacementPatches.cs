using HarmonyLib;
using SteamShelf.Media;
using SteamShelf.Placeables;
using SteamShelf.PlayerTools;
using System.Reflection;
using UnityEngine;

namespace BR_Libretro;

internal static class RomPlacementPatches
{
    private static readonly FieldInfo HeldMediaField = AccessTools.Field(typeof(PlayerInteractionTool), "currentHeldMediaItem");
    private static readonly FieldInfo LookingAtContainerField = AccessTools.Field(typeof(PlayerInteractionTool), "currentLookingAtContainer");
    private static readonly FieldInfo HitPointField = AccessTools.Field(typeof(PlayerInteractionTool), "currentHitPoint");
    private static readonly FieldInfo ContainerColliderField = AccessTools.Field(typeof(PlaceableMediaContainer), "boxCollider");

    private static bool IsHoldingRom(PlayerInteractionTool tool) => HeldMediaField?.GetValue(tool) is RomData;

    [HarmonyPatch(typeof(PlayerInteractionTool), "CheckLookingAtPlaceable")]
    private static class ShelfTopFreePlacementPatch
    {
        private static void Postfix(PlayerInteractionTool __instance)
        {
            if (!IsHoldingRom(__instance)
                || LookingAtContainerField?.GetValue(__instance) is not PlaceableMediaContainer container
                || HitPointField?.GetValue(__instance) is not Vector3 hitPoint)
                return;

            BoxCollider box = ContainerColliderField?.GetValue(container) as BoxCollider;
            if (box == null || Mathf.Abs(hitPoint.y - box.bounds.max.y) > 0.015f) return;

            // The outer top of shelf furniture is a normal prop surface for a
            // cartridge. Interior faces remain native media-slot targets.
            LookingAtContainerField.SetValue(__instance, null);
        }
    }
}
