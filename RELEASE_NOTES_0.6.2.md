# BR-Libretro 0.6.2

- Preserves the ROM media ID, usage and visual reference when BOXROOM clones the held loose prop into a placed floor object.
- Writes the ROM identity into newly linked room save state so placed media restores through the stable MediaAPI ID.
- Uses state-free display objects for held and inspected ROMs; only the loose physical object owns `RomMediaProp` lifecycle state.
- Cancels asynchronous glTF model instantiation when a temporary held or inspector visual is destroyed.
- Retains the 0.6.1 lifecycle diagnostics for live acceptance testing.

Validation: release build required; floor pickup, deletion return, shelf placement and save/load acceptance pending.
