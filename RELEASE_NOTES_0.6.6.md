# BR-Libretro 0.6.6

- Fixes free-placed ROMs retaining the dynamically cloned cartridge model and then loading a second model over it.
- Serializes the runtime content reference so Unity remaps it to the matching child when cloning a loose ROM prefab.
- Defensively clears orphaned visual children before applying or restoring a ROM visual.
- Removes the 0.01-metre label lift; the apparent label z-fighting came from two complete overlapping cartridge visuals, not the model's intended wrap-label mesh.
- Retains the 0.6.5 shelf-preview and shelf-top free-placement corrections.

Validation: release build required; free-place preview, committed floor/top placement, single-model count, label stability, pickup and save/load require in-game acceptance.
