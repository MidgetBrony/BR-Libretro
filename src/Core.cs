using Boxroom_TV;
using BR_MediaAPI;
using MelonLoader;
using ModsPanel;
using SteamShelf;
using SteamShelf.Input;
using SteamShelf.Placeables;
using SteamShelf.PlayerTools;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.InputSystem;

[assembly: MelonInfo(typeof(BR_Libretro.Core), "BR-Libretro", "0.4.15", "MidgetBrony")]
[assembly: MelonGame("NestedLoop", "BOXROOM")]
[assembly: MelonAdditionalDependencies("Boxroom_TV", "ModsPanel", "BR_MediaAPI")]

namespace BR_Libretro;

public sealed class Core : MelonMod
{
    private PlayerInteractionTool interaction;
    private CoreMap coreMap;
    private LibretroMediaPresentation presentations;
    private LibretroRuntime runtime;
    private BoxroomTvDisplayLease displayLease;
    private AudioBridge audio;
    private Camera camera;
    private Vector3 roomCameraPosition;
    private Quaternion roomCameraRotation;
    private FirstPersonController controller;
    private bool previousPlayerCanMove, previousCameraCanMove, primaryWasPressed, powerKeyWasDown, tvMode, holdTriggered;
    private bool firstFrameAttached;
    private float powerKeyDownAt, unfocusedSince = -1f;
    private MelonPreferences_Entry<string> powerKey;
    private MelonPreferences_Entry<float> holdToStopSeconds, autoOffMinutes, tvVolume, audioRange;
    private readonly List<InputActionMap> lockedActionMaps = new();
    private readonly List<PlayerInput> lockedPlayerInputs = new();
    private bool applicationQuitting;

    public override void OnInitializeMelon()
    {
        DataPaths.Ensure(); coreMap = CoreMap.Load();
        CoreInstaller.Initialize(coreMap);
        presentations = new LibretroMediaPresentation(coreMap);
        MelonPreferences_Category prefs = MelonPreferences.CreateCategory("BR-Libretro");
        powerKey = prefs.CreateEntry("PowerKey", "F2", "TV focus / power key");
        holdToStopSeconds = prefs.CreateEntry("HoldToStopSeconds", 1.25f, "Hold power key to stop game");
        autoOffMinutes = prefs.CreateEntry("AutoOffMinutes", 15f, "Stop after this many minutes outside TV mode; 0 disables");
        tvVolume = prefs.CreateEntry("Volume", 0.65f, "TV game volume");
        audioRange = prefs.CreateEntry("AudioRange", 3f, "Maximum audible distance from TV");
        InputBindings.Initialize(prefs, powerKey);
        RegisterSettings();
        LoggerInstance.Msg($"Ready. Cores: {DataPaths.Cores} | BIOS/Firmware: {DataPaths.Bios}. Hold a configured game and interact with a TV.");
    }

    public override void OnUpdate()
    {
        InputBindings.UpdateCapture();
        interaction ??= UnityEngine.Object.FindFirstObjectByType<PlayerInteractionTool>();
        PlayerInputContext context = Singleton<InputManager>.Instance?.CurrentPlayerInputContext;
        bool primary = (context != null && context.PrimaryPressedThisFrame) || Input.GetKeyDown(KeyCode.E);
        if (primary && !primaryWasPressed && runtime == null) TryStart();
        primaryWasPressed = primary;

        bool powerDown = IsPowerKeyDown();
        if (runtime != null)
        {
            if (powerDown && !powerKeyWasDown) { powerKeyDownAt = Time.unscaledTime; holdTriggered = false; }
            if (powerDown && !holdTriggered && Time.unscaledTime - powerKeyDownAt >= Mathf.Max(0.5f, holdToStopSeconds.Value))
            {
                holdTriggered = true;
                LoggerInstance.Msg("Power key held: stopping libretro game and restoring TV.");
                StopRuntime();
            }
            else if (!powerDown && powerKeyWasDown && !holdTriggered) SetTvMode(!tvMode);
        }
        powerKeyWasDown = powerDown;

        if (runtime == null) return;
        if (displayLease?.IsActive != true)
        {
            LoggerInstance.Warning("BR-Libretro lost its Boxroom-TV display claim; stopping the game.");
            StopRuntime();
            return;
        }
        audio?.Update(displayLease.ScreenCenter, tvVolume.Value, audioRange.Value);
        if (!tvMode && autoOffMinutes.Value > 0f && unfocusedSince >= 0f && Time.unscaledTime - unfocusedSince >= autoOffMinutes.Value * 60f)
        {
            LoggerInstance.Msg("Libretro unattended timer expired; stopping game and restoring TV.");
            StopRuntime(); return;
        }
        if (tvMode) KeepBoxroomInputLocked();
        if (runtime.HasExited) { LoggerInstance.Warning("The libretro session ended; restoring the TV."); StopRuntime(); return; }
        runtime.UpdateInput();
        if (runtime.PumpVideo(out Texture2D texture) && displayLease?.IsActive == true)
        {
            BindScreenTexture(texture);
            if (!firstFrameAttached)
            {
                firstFrameAttached = true;
                LoggerInstance.Msg($"First libretro frame attached to TV ({texture.width}x{texture.height}).");
            }
        }
    }

    private void TryStart()
    {
        SteamGameData game = interaction?.CurrentHeldSteamGameData;
        PlacementTag tag = interaction?.LookingAtPlaceableTag;
        if (game == null) return;

        GameImagePainter painter = tag?.GetComponent<GameImagePainter>();
        if (painter == null)
        {
            Camera view = Camera.main;
            if (view == null || !Physics.Raycast(view.transform.position, view.transform.forward, out RaycastHit hit, 4f))
            {
                LoggerInstance.Warning($"Cannot start '{game.Name}': no TV screen is under the crosshair.");
                return;
            }

            painter = hit.collider.GetComponentInParent<GameImagePainter>();
            tag = painter?.GetComponent<PlacementTag>() ?? hit.collider.GetComponentInParent<PlacementTag>();
            if (painter == null || tag == null)
            {
                LoggerInstance.Warning($"Cannot start '{game.Name}': the looked-at object is not a supported TV screen.");
                return;
            }
        }
        if (!coreMap.TryResolve(game.LaunchExePath, game.LaunchArguments, out string core, out string rom))
        {
            LoggerInstance.Warning($"No installed libretro core mapping could resolve '{game.Name}'. Check {DataPaths.Config}\\core-map.json and {DataPaths.Cores}.");
            return;
        }

        if (!BoxroomTvApi.TryClaimDisplay(painter, tag,
                "com.midgetbrony.br-libretro", "BR-Libretro", out displayLease))
        {
            LoggerInstance.Warning($"Cannot start '{game.Name}': Boxroom-TV rejected this display because it is unsupported or already in use.");
            ModsUi.ShowToast("That screen is unsupported or currently in use.");
            return;
        }

        audio = new AudioBridge();
        try { runtime = LibretroRuntime.Start(core, rom, audio); }
        catch
        {
            audio.Dispose();
            audio = null;
            displayLease.Dispose();
            displayLease = null;
            throw;
        }
        firstFrameAttached = false;
        SetTvMode(true);
        LoggerInstance.Msg($"Starting '{game.Name}' in-process with {core}_libretro.dll. F2 toggles game/room controls.");
    }

    private void SetTvMode(bool active)
    {
        if (runtime == null) return;
        if (active)
        {
            camera = Camera.main;
            if (camera == null) return;
            roomCameraPosition = camera.transform.position; roomCameraRotation = camera.transform.rotation;
            controller = UnityEngine.Object.FindFirstObjectByType<FirstPersonController>();
            if (controller != null) { previousPlayerCanMove = controller.playerCanMove; previousCameraCanMove = controller.cameraCanMove; controller.playerCanMove = false; controller.cameraCanMove = false; }
            LockBoxroomInput();
            Bounds bounds = displayLease.ScreenBounds; Vector3 normal = displayLease.ScreenForward;
            float halfFov = camera.fieldOfView * 0.5f * Mathf.Deg2Rad;
            float verticalDistance = bounds.extents.y / Mathf.Tan(halfFov);
            float horizontalDistance = bounds.extents.x / (Mathf.Tan(halfFov) * Mathf.Max(0.1f, camera.aspect));
            float distance = Mathf.Max(0.25f, Mathf.Max(verticalDistance, horizontalDistance) * 1.12f);
            Vector3 a = bounds.center - normal * distance, b = bounds.center + normal * distance;
            Vector3 position = Vector3.Distance(a, roomCameraPosition) <= Vector3.Distance(b, roomCameraPosition) ? a : b;
            camera.transform.position = position; camera.transform.rotation = Quaternion.LookRotation(bounds.center - position, Vector3.up);
        }
        else
        {
            if (camera != null) { camera.transform.position = roomCameraPosition; camera.transform.rotation = roomCameraRotation; }
            if (controller != null) { controller.playerCanMove = previousPlayerCanMove; controller.cameraCanMove = previousCameraCanMove; }
            UnlockBoxroomInput();
            Input.ResetInputAxes();
        }
        tvMode = active; unfocusedSince = active ? -1f : Time.unscaledTime; runtime.SetInputEnabled(active);
    }

    private void StopRuntime(bool restorePlayerState = true)
    {
        if (restorePlayerState && tvMode) SetTvMode(false);
        else if (!restorePlayerState)
        {
            tvMode = false;
            lockedActionMaps.Clear();
            lockedPlayerInputs.Clear();
            camera = null;
            controller = null;
        }

        LibretroRuntime stoppingRuntime = runtime;
        runtime = null;
        stoppingRuntime?.Dispose();
        audio?.Dispose();
        audio = null;
        if (restorePlayerState) displayLease?.Dispose();
        displayLease = null;
        unfocusedSince = -1f;
        firstFrameAttached = false;
    }

    private void BindScreenTexture(Texture texture)
    {
        if (displayLease?.SetTexture(texture) != true)
            LoggerInstance.Warning("BR-Libretro lost its Boxroom-TV display claim.");
    }

    private void LockBoxroomInput()
    {
        lockedActionMaps.Clear();
        lockedPlayerInputs.Clear();

        foreach (PlayerInput playerInput in UnityEngine.Object.FindObjectsByType<PlayerInput>(FindObjectsSortMode.None))
        {
            if (playerInput == null || !playerInput.inputIsActive) continue;
            lockedPlayerInputs.Add(playerInput);
            playerInput.DeactivateInput();
        }

        object manager = Singleton<InputManager>.Instance;
        if (manager == null) return;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (FieldInfo field in manager.GetType().GetFields(flags))
            DisableMapsFrom(field.GetValue(manager));
        foreach (PropertyInfo property in manager.GetType().GetProperties(flags))
        {
            if (!property.CanRead || property.GetIndexParameters().Length != 0) continue;
            try { DisableMapsFrom(property.GetValue(manager)); } catch { }
        }
    }

    private void DisableMapsFrom(object value)
    {
        if (value is InputActionMap map)
        {
            if (map.enabled && !lockedActionMaps.Contains(map)) { lockedActionMaps.Add(map); map.Disable(); }
        }
        else if (value is InputActionAsset asset)
        {
            foreach (InputActionMap candidate in asset.actionMaps)
                if (candidate.enabled && !lockedActionMaps.Contains(candidate)) { lockedActionMaps.Add(candidate); candidate.Disable(); }
        }
    }

    private void KeepBoxroomInputLocked()
    {
        foreach (InputActionMap map in lockedActionMaps)
            if (map != null && map.enabled) map.Disable();
        if (controller != null) { controller.playerCanMove = false; controller.cameraCanMove = false; }
    }

    private void UnlockBoxroomInput()
    {
        foreach (InputActionMap map in lockedActionMaps)
            if (map != null) map.Enable();
        lockedActionMaps.Clear();
        foreach (PlayerInput playerInput in lockedPlayerInputs)
            if (playerInput != null) playerInput.ActivateInput();
        lockedPlayerInputs.Clear();
    }

    private bool IsPowerKeyDown()
    {
        return InputBindings.IsPowerPressed();
    }

    private void RegisterSettings()
    {
        ModsPanelApi.RegisterSection("com.midgetbrony.br-libretro", "BR-Libretro", 125).Clear()
            .AddSlider("hold-stop", "Hold key to stop", () => holdToStopSeconds.Value,
                value => { holdToStopSeconds.Value = Mathf.Clamp(value, 0.5f, 3f); MelonPreferences.Save(); }, 0.5f, 3f, false, "0.00 sec")
            .AddSlider("auto-off", "Unfocused auto-off timer", () => autoOffMinutes.Value,
                value => { autoOffMinutes.Value = Mathf.Clamp(value, 0f, 60f); MelonPreferences.Save(); }, 0f, 60f, true, "0 min")
            .AddSlider("volume", "Game volume", () => tvVolume.Value,
                value => { tvVolume.Value = Mathf.Clamp01(value); MelonPreferences.Save(); }, 0f, 1f, false, "0%")
            .AddSlider("range", "TV audio range", () => audioRange.Value,
                value => { audioRange.Value = Mathf.Clamp(value, 1f, 8f); MelonPreferences.Save(); }, 1f, 8f, false, "0.0 m")
            .AddButton("core-installer", "Libretro core installer", "OPEN", CoreInstaller.OpenMenu)
            .AddButton("configure-inputs", "Keyboard / gamepad bindings", "CONFIGURE", InputBindings.OpenMenu)
            .AddLabel("controls", "Tap the power key to enter or leave TV controls. Hold it to stop the game. Configure player-one keyboard and gamepad bindings above. An auto-off value of 0 disables the timer.");
    }

    public override void OnApplicationQuit()
    {
        applicationQuitting = true;
        LoggerInstance.Msg("BOXROOM quit requested; stopping the active libretro session before engine shutdown.");
        StopRuntime(false);
    }

    public override void OnDeinitializeMelon()
    {
        InputBindings.CancelCapture();
        StopRuntime(!applicationQuitting);
        presentations?.Dispose();
        presentations = null;
    }
}
