# BR-Libretro 0.6.13

- Keeps the active ModsPanel menu open while IGDB searches, bulk matching, metadata, and cover downloads run.
- Replaces the current screen only after an asynchronous IGDB operation completes.
- Prevents IGDB result selection from releasing UI input control with no recovery menu available.
- Leaves the current screen usable when an IGDB request fails.
