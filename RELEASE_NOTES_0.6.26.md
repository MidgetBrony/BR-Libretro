# BR-Libretro 0.6.26

- Adds core uninstallation to every installed core's details page.
- Requires confirmation and refuses to remove a core while a Libretro game is running.
- Removes only the selected core DLL; ROMs, saves, BIOS files, file mappings, and console/ROM core choices are retained.
- Reports locked-file and other removal failures without claiming the core was uninstalled.
- Reminds the user to restart BOXROOM so console tabs are rebuilt after removal.
