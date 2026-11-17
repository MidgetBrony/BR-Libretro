# BR-Libretro 0.6.1

- Adds a non-mutating ROM lifecycle diagnostic trace covering source-box demand, shelf and loose pickup, held-stack state, placement, inspection-created props, reservations, deletion and destruction.
- Writes a per-session trace under `UserData/BR-Libretro/config/diagnostics` and mirrors lifecycle events to the MelonLoader log.
- Keeps BR-MediaAPI 1.1.17 unchanged so the recorded behavior represents the original 0.6.0 lifecycle.

Validation: release build and Harmony target validation required; in-game reproduction pending.
