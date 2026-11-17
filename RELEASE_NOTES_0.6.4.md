# BR-Libretro 0.6.4

- Reverts the unintended horizontal recentering of the NES artwork surface.
- Lifts the NES artwork surface outward by 0.01 model units to prevent floor-view z-fighting.
- Restores the previous placement-aware cartridge rotation for spine, face-out and face-up shelf positions.
- Restores live placement-preview rotation as the player changes shelf orientation.
- Retains the 0.6.3 bottom anchoring and safe glTF staging fixes.

Validation: release build required; shelf spine/top-label direction, face-out, face-up, floor artwork stability, held and inspection views require in-game visual acceptance.
