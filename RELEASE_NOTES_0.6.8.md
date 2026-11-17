# BR-Libretro 0.6.8

- Rehydrates a loose placement preview when BOXROOM clones it before the asynchronous cartridge model has loaded.
- Re-registers late-loaded preview renderers with BOXROOM's placement painter.
- Gives the ROM Box its own presentation context and flips only that context to show the cartridge label rather than its back.
- Includes the 0.6.7 collider-bottom correction so normal floor and shelf-top placement no longer requires Shift to bypass a false overlap.
- Retains the 0.6.6 single-model clone fix and all normal shelf rotation behavior.

Validation: release build required; ROM Box display, removal preview, interior shelf preview, committed shelf placement, shelf-top/floor placement without Shift, pickup and save/load require in-game acceptance.
