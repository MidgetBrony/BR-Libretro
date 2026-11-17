# BR-Libretro 0.6.3

- Anchors shelf and loose cartridge visuals by their world-space bottom instead of centering them through the placement point.
- Keeps held and inspected cartridge visuals centered independently from physical placement.
- Restores the NES label UV correction and resets material texture scale and offset when applying cover artwork.
- Centers the NES label surface horizontally on the cartridge body while retaining its intended upper-face position.
- Applies cover artwork only to the configured label material.
- Stages and serializes glTF scene instantiation so destroyed temporary visuals cannot invalidate an in-progress import.

Validation: release build required; shelf, floor, held, inspection and label alignment require in-game visual acceptance.
