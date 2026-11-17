# BR-Libretro 0.6.5

- Applies the requested NES label lift as 0.01 BOXROOM metres after model scaling, rather than 0.01 imported-model units.
- Removes the native game-case shelf-preview offset and rotation from ROM previews so preview and final ROM roots use the same slot transform.
- Treats the outer top boundary of shelf furniture as a normal free-placement surface for held ROMs, without requiring the placement-modifier key.
- Keeps interior shelf faces on the native media-slot placement path.

Validation: release build required; label stability, top-surface free placement, interior slot preview/final alignment and shelf depth require in-game visual acceptance.
