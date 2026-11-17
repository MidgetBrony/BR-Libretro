# BR-Libretro 0.5.0

Breaking experimental media refactor:

- Registers ROMs as first-class BR-MediaAPI media rather than Steam/Custom Games.
- Adds stable ROM identities, platform folder discovery, archive inspection, duplicate prevention, and move/rename recovery.
- Adds one Media-page tab per console with independent folder selection, a searchable paged ROM list, and per-ROM/console core dropdowns populated from every installed core.
- Adds dedicated shelf, loose, held, and inspected ROM physical objects with platform-selected cartridge/disc presentation.
- Launches discovered ROM records directly through the existing integrated Libretro backend without RetroArch paths or command-line parsing.
- Passes direct files and playlists by exact path and gives ZIP extraction an owned, traversal-safe temporary lifecycle with failure and startup cleanup.
- Retains core installation, emulation, Boxroom-TV display, audio, input, NES/SNES models, and label generation.

Compatibility: old experimental SteamGameData placements are not migrated. Existing Custom Game registrations and room data are not deleted.

Validation: Release build against the local BOXROOM installation succeeded. Live discover → spawn → shelf → save/load → pickup → launch → stop acceptance is still required.
