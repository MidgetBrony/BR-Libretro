# BR-Libretro 0.4.14

- Adds physical NES cartridges for games using Mesen, FCEUmm or Nestopia.
- Fills the cartridge's front label with the game's BOXROOM artwork and generates a centred black Segoe UI title strip on the exposed edge.
- Correctly orients NES cartridges for face-out, spine-out and face-up shelf placement modes.
- Makes green placement previews match the final cartridge orientation.
- Adds configurable per-core GLB presentation settings under `Documents\br-libretro\config\core-presentation.json`.
- Stops an active libretro core safely during BOXROOM shutdown instead of allowing the process to crash on exit.

BR-Libretro remains experimental. Restart BOXROOM after installing or updating.
