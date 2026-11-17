# BR-Libretro 0.6.7

- Raises the loose-ROM placement collider by half its height so its bottom matches the cartridge's bottom-aligned root pivot.
- Prevents the invisible collider from extending 0.0125 metres into floors and shelf tops.
- Allows normal free placement to pass BOXROOM's overlap validation without holding the placement-modifier key.
- Does not move or rescale the visible cartridge model.

Validation: release build required; ordinary floor and shelf-top placement without Shift, preview validity, pickup and save/load require in-game acceptance.
