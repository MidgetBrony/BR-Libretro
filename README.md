# BR-Libretro

> **Experimental:** 0.6.36 replaces the earlier Steam/Custom Game media format. Old game-case placements are not migrated.

BR-Libretro is an in-process Libretro frontend for BOXROOM. ROMs are registered as first-class BR-MediaAPI media with their own stable identities, physical objects, shelves, held/inspect presentation, and room persistence. RetroArch is not required or launched.

The mod runs user-supplied Libretro cores inside BOXROOM, claims supported televisions and monitors through Boxroom-TV, and sends BOXROOM keyboard/controller input directly to the active core.

## User data

On first launch the mod creates:

```text
Documents\br-libretro\
  ROMs\        default platform folders (NES, SNES, Genesis, Game Boy, etc.)
  cores\       user-supplied `*_libretro.dll` files
  BIOS\        user-supplied BIOS/system files
  saves\       battery-backed saves
  states\      save states
  config\      ROM sources, stable registry, core mappings and presentation
  models\      physical media models and attribution notices
  core_assets\ core-owned assets
  logs\
  temp\
```

No cores, BIOS files, or ROMs are distributed by this project.

## ROM discovery

The Media > ROMs page has a tab for each supported console. Every tab has its own folder picker, scan status, and ROM manager. Console folders may live anywhere; they do not need to share one parent. The default folders remain under `Documents\br-libretro\ROMs` for users who prefer a single organized root.

The console ROM manager presents a searchable, paged ROM list. Selecting a ROM exposes a core dropdown containing Auto plus every installed `*_libretro.dll`. Recommended compatible cores appear first, but any installed core can be selected for experimentation. A second dropdown sets the console-wide default. Per-ROM overrides take precedence and never change media identity or cartridge/disc presentation.

`config\rom-folders.json` backs those console tabs. Paths may be relative to the default library root or absolute. Each source can be recursive and can specify an optional console-wide core override:

```json
{
  "Version": 1,
  "Sources": [
    { "Platform": "nes", "Path": "NES", "CoreOverride": "", "Recursive": true },
    { "Platform": "snes", "Path": "D:\\Games\\SNES", "CoreOverride": "snes9x", "Recursive": true }
  ]
}
```

Built-in platform IDs are `nes`, `snes`, `genesis`, `gb`, `gbc`, `gba`, `psx`, and `doom`.

Direct ROMs, CUE/M3U playlists, CHD/PBP images and platform-valid ZIP entries are discovered. ZIP files are inspected using the platform assigned to their source folder; a ZIP is never assigned a platform merely because it is an archive. Multiple ROM entries become separate media items. CUE/M3U descriptors take priority over their referenced tracks.

`config\rom-registry.json` holds permanent generated media IDs, fingerprints and optional per-ROM core overrides. Rescanning reuses identities by location first and content fingerprint second, allowing ordinary file moves and renames without changing shelf identity. Duplicate content is registered once. Do not casually delete this registry after creating rooms.

## Optional IGDB metadata

Each console tab includes **Metadata**. As in Boxroom Studio, BR-Libretro can search IGDB using a cleaned ROM title and the console's IGDB platform ID, review matches, cache the selected IGDB identity, and download cover artwork. Bulk matching applies only unique exact-title results; ambiguous games remain unchanged for manual review.

IGDB requires Twitch application credentials. BR-Libretro stores its own Client ID and masked-entry Client Secret locally in `config\igdb.json`; credentials are never copied from Studio or written to logs. ROM discovery and launching do not require IGDB or an internet connection.

Cover art may be placed beside a ROM/archive as `<name>.png`, `<name>.jpg`, `<name>.cover.png`, `cover.png`, or `boxart.jpg`.

## Physical media

NES and SNES use the included cartridge models and generated artwork labels. Genesis and handheld platforms currently use native ROM cartridge props; disc platforms use disc props. Physical format is selected from the platform, never from the chosen core.

`config\rom-presentation.json` controls the NES/SNES GLB model transforms. The SNES cartridge mesh is adapted from "Super Mario World Game Cartridge" by Laser Design under [CC BY-NC 4.0](https://creativecommons.org/licenses/by-nc/4.0/). Full attribution is in `THIRD_PARTY_NOTICES.md`; builds containing it are for noncommercial distribution only.

## Playing

1. Install a compatible core such as Mesen from **Settings > Mods > BR-Libretro > Libretro core manager**, or place its `*_libretro.dll` in `Documents\br-libretro\cores` before starting BOXROOM.
2. Put ROMs under their platform folders and refresh the ROM media library.
3. Spawn a ROM from the ROM Box or media browser, hold it, look at a supported TV, and interact.
4. Boxroom-TV owns F2: tap it to return to the room, look at the claimed screen and tap F2 to focus again, or hold F2 to stop the game.

The core resolver uses, in order: the ROM/source override, the configured extension mapping, and installed platform-preferred cores. It never parses an external emulator command line.

Keyboard player-one defaults are arrows, Z/B, X/A, A/Y, S/X, Enter/Start, and Right Shift/Select. The first gamepad is mapped as a full 16-button Libretro joypad. Input bindings and core installation, updating, and uninstallation are available in BR-Libretro's Mods settings. Installing or uninstalling a core refreshes compatible console tabs immediately. Uninstalling keeps ROMs, saves, BIOS files, mappings, and per-console/per-ROM choices so they work again if the core is reinstalled.

## Core manager how-to

Open **Settings > Mods > BR-Libretro > Libretro core manager**. Recommended Cores contains the curated defaults; Browse All Official Cores loads Libretro's Windows x64 catalogue; Installed Cores lists everything currently present in the core folder.

- Select an uninstalled core and choose **Install core**. When the first compatible core for a console becomes available, that console tab appears immediately.
- Select an installed core and choose **Update core** to replace it with the latest official build. The previous DLL is backed up.
- Select an installed core and choose **Uninstall core**, then confirm. A running game must be stopped first. Only that core DLL is removed; ROMs, saves, BIOS files, mappings and core choices are retained.
- A console tab disappears only after its final compatible installed core is removed. No BOXROOM restart is required for installer-driven tab changes.
- Use **Configure file mappings** to choose which extensions launch a core. Officially advertised file types appear under **Suggested mappings** when metadata is available.

Core DLLs added or removed manually in `Documents\br-libretro\cores` are discovered on the next BOXROOM start; use the in-game manager for immediate tab refresh.

## Content loading

Direct files and disc/playlist descriptors are passed to the Libretro wrapper by exact path. ZIP entries are extracted into a session-owned directory. Cartridge archives extract only the selected entry; archived CUE/M3U content extracts its companion files. All archive paths are traversal-checked, and session directories are removed after normal stops, failed launches, and on the next startup after a crash.

## Lifecycle diagnostics

Version 0.6.2 records ROM media state transitions without changing BR-MediaAPI behavior. Each BOXROOM session creates `config\diagnostics\rom-lifecycle-YYYYMMDD-HHMMSS.log`. The trace includes source-box demand, shelf and loose pickup, the held stack and current item, physical versus held/inspect prop instances, placement, reservations, deletion, destruction, and every change to `IsSpawned` or `IsInHand`. The same entries are mirrored into the MelonLoader log with a `[ROM-LIFECYCLE]` prefix.

## Architecture

- `src/RomLibrarySystem.cs`: folder discovery, stable IDs, duplicate prevention and persistent registry.
- `src/RomMediaLibrary.cs`: BOXROOM `IMediaLibrary` bridge.
- `src/RomPhysicalMedia.cs`: shelf, held, inspected and loose placeable lifecycle.
- `src/RomMediaVisual.cs`: platform-selected cartridge/disc presentation.
- `src/RomContentSession.cs`: exact-path and temporary archive content lifecycle.
- `src/Core.cs`: MediaAPI registration, BOXROOM interaction, camera lifecycle and Boxroom-TV display lease.
- `src/LibretroRuntime.cs` and `vendor/SK.Libretro/Scripts`: existing integrated emulator backend.

BR-MediaAPI, Boxroom-TV, and ModsPanel are required. Existing experimental Custom Game registrations are not automatically deleted, and old SteamGameData-based placements are not migrated.

## Build

```powershell
dotnet build BR-Libretro.csproj -c Release -p:GamePath="D:\SteamLibrary\steamapps\common\My Game Room"
```

Compilation is not live emulation validation. Test discovery, source-box spawning, shelf placement, room save/load, held/inspect presentation, core loading, video, audio, input and cleanup in BOXROOM.
