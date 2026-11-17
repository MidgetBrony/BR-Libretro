# BR-Libretro 0.6.9

- Uses BOXROOM's native Steam GameBox shelf prefab as the hidden layout anchor for ROM shelf items and ROM Box items.
- Fits the cartridge GLB to the authored native renderer center and world-space bottom instead of guessing shelf offsets.
- Restores BOXROOM's native GameBox shelf-preview offset `(0.06, 0.017, -0.09)` and rotation `(180, 0, 0)`.
- Copies the native placed GameBox collider size and center for loose ROMs.
- Removes the special ROM Box face-turn workaround; the native GameBox anchor now owns that orientation.
- Preserves the 0.6.8 asynchronous preview rehydration and the 0.6.6 single-model clone fix.

Validation: release build required; all three shelf placements, their previews, ROM Box display/removal, free placement, pickup and save/load require in-game acceptance.
