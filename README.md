# BR-Libretro

> **Experimental:** back up important BOXROOM room saves before testing. Core and game compatibility varies.

An in-process libretro frontend for BOXROOM. It runs user-supplied libretro cores inside BOXROOM, claims supported televisions/monitors through Boxroom-TV's shared display API, and sends BOXROOM keyboard/controller input directly to the core. It does not launch or capture RetroArch.

## User data

On first launch the mod creates:

```text
Documents\br-libretro\
  cores\       user-supplied `*_libretro.dll` files
  BIOS\        user-supplied BIOS/system files
  saves\       battery-backed saves
  states\      save states
  config\      core-map.json and core-presentation.json
  models\      physical core models and their attribution notices
  core_assets\ core-owned assets
  logs\
  temp\
```

No cores, BIOS files, or ROMs are distributed by this project.

## Physical core models

Games resolved to the `mesen`, `fceumm`, or `nestopia` cores use an NES cartridge instead of the standard BOXROOM game case. Games resolved to `snes9x` use a neutral SNES cartridge with BOXROOM cover art applied to a separate front label. The original game object, collider, pickup behavior, launch data, and saved placement remain unchanged; only its visible shell is replaced. NES cover art fills the cartridge's broad front label, while a black Segoe UI title strip wraps onto the exposed edge. Face-out, spine-out and face-up shelf placements—and their green placement previews—each orient the cartridge independently.

The SNES cartridge mesh is adapted from "Super Mario World Game Cartridge" by Laser Design under [CC BY-NC 4.0](https://creativecommons.org/licenses/by-nc/4.0/). Its supplied texture and branding are not distributed. Full attribution and modification details are in `THIRD_PARTY_NOTICES.md` and are extracted beside the model. Builds containing this asset are for noncommercial distribution only.

For a dedicated SNES cartridge label, place a 92:43 PNG at `steam_cache_v2\<AppId>\label.png`. BR-Libretro uses it only for the cartridge label and leaves BOXROOM's ordinary 600x900 case artwork unchanged. If `label.png` is absent, the normal box art remains the fallback. Restart or reload the room after adding or replacing a label.

`Documents\br-libretro\config\core-presentation.json` maps core names to GLB models. Add another entry and place its GLB in `Documents\br-libretro\models` to give another core its own physical format. Each entry supports model filename, height in metres, placed/shelf rotation, separate spine and face-up shelf corrections, inspection rotation, face rotation, XYZ offset, label material name, artwork on/off, and an enable switch. Restart BOXROOM after editing this file.

## First test

1. Put `mesen_libretro.dll` in `Documents\br-libretro\cores`.
2. Keep the existing BOXROOM custom-game arguments containing the ROM/archive path. Existing RetroArch `-L` arguments are understood but RetroArch is not launched.
3. Hold the game box, look at a supported TV, and interact.
4. F2 toggles between emulator controls and room controls.

Keyboard player-one defaults: arrows, Z/B, X/A, A/Y, S/X, Enter/Start, Right Shift/Select. The first gamepad is mapped as a full 16-button libretro joypad, including shoulders, triggers and stick clicks.

Open **Settings > Mods > BR-Libretro > Keyboard / gamepad bindings** to configure controls. Choose Keyboard or Gamepad bindings, select an action, release the old input, and press the replacement. Keyboard bindings can be cleared with Backspace during capture. The menu also restores all defaults. Bindings persist in `UserData/MelonPreferences.cfg`.

Open **Settings > Mods > BR-Libretro > Libretro core installer** to install or update cores from Libretro's official Windows x64 buildbot. The popup includes recommended cores, a searchable and paged live catalogue, installed-core management, and an editor for mapping game-file extensions to cores. Downloads are validated as ZIP archives containing the expected x64 DLL before installation. Updating an existing core keeps its previous DLL in `Documents\br-libretro\cores\backups`; mapping changes keep backups under `config\backups`. The installer does not download games, BIOS or firmware.

## Architecture

- `vendor/SK.Libretro/Scripts`: pinned upstream engine-independent runtime (MIT).
- `src/Processors.cs`: BOXROOM video and input bridge.
- `src/AudioBridge.cs`: Unity spatial-audio bridge attached to the TV.
- `src/Core.cs`: BOXROOM interaction, camera lifecycle, and exclusive Boxroom-TV display lease.
- `src/LibretroMediaPresentation.cs`: per-core physical game model replacement while retaining BOXROOM gameplay identity.
- `UnityAssets`: editable bundle handoff for future native-style screens and controls. Emulation logic stays in the DLL.

Boxroom-TV and ModsPanel are required. Boxroom-TV owns screen materials and arbitration, ensuring videos and emulators cannot overwrite each other. Existing TV playback is paused and restored after an emulator releases its display. BOXROOM already ships the required Newtonsoft.Json and Unity runtime assemblies; releases must not bundle duplicates.

## Build

```powershell
Copy-Item Directory.Build.user.props.example Directory.Build.user.props
# Edit GamePath in Directory.Build.user.props, then:
dotnet build BR-Libretro.csproj -c Release -p:DeployToGame=true
```

Compilation is not live emulation validation. Test core loading, video, audio, input, saves, cleanup and coexistence with Boxroom-TV in-game.
