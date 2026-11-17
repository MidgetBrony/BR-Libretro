# BR-Libretro 0.6.15

- Renders procedural handheld retail-box solids with BOXROOM's known-working native material shader.
- Falls back through URP Lit, URP Simple Lit, and Unlit shaders instead of relying on Unity's stripped Standard shader.
- Initializes solid materials with white base textures, opaque surface state, and explicit color properties.
- Fixes the GB/GBC/GBA box body and cardboard face being invisible while only the artwork quad rendered.
