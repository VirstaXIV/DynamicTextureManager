# Dynamic Texture Manager

A companion plugin for [Penumbra](https://github.com/xivdev/Penumbra) that manages **overlay
mods**: pick a target — gear you are wearing, an item, or an installed Penumbra mod's files —
stamp decals onto it, recolor them, tune the surface finish, and apply the result as a
plugin-managed persistent mod in Penumbra.

## Status

Early work in progress. Nothing here is stable yet.

## Installing

Add this repository to Dalamud's custom plugin repositories:

1. In-game: `/xlsettings` → **Experimental** → **Custom Plugin Repositories**
2. Add `https://raw.githubusercontent.com/VirstaXIV/DynamicTextureManager/master/repo.json`
3. Save, then install **Dynamic Texture Manager** from the plugin installer (`/xlplugins`).

Requires [Penumbra](https://github.com/xivdev/Penumbra) to be installed and enabled.

## Guide

### Concepts

- **dTexture** — one overlay definition: a source (which gear/materials to edit), its decal
  layers, and its colorset edits. Each dTexture builds into exactly one Penumbra mod. The
  main window lists your dTextures in a folder tree on the left (like Glamourer designs).
- **Decal** — an image from your decal library, stamped onto a material. Decals can be
  placed directly on the 3D model and carry per-layer settings: position, size, rotation,
  colors, opacity, and surface finish.
- **Generated mod** — pressing **Build** (hammer button) writes a real, self-contained mod
  into Penumbra (under a `DynamicTextureManager/` sort folder). Built mods bake everything
  in: they keep working with the plugin unloaded and survive game restarts.

### Quick start

1. Open the main window and create a new dTexture in the left-hand list.
2. **Source tab** — pick what to edit: a worn equipment slot, an item, or an installed
   Penumbra mod's files. This resolves the materials and textures the decals will target.
3. **Decals tab** — pick the material from the dropdown, then **Add Decal from Library…**
   (or **Import Decal…** to bring in a new image and attach it in one step). Supported
   formats: PNG, JPG, DDS, BMP, TGA — everything is normalized to PNG in the library.
4. Place the decal in the embedded 3D viewport: left-click stamps it onto the model at the
   cursor, drag moves it, mouse wheel zooms, right-drag orbits, Ctrl+wheel resizes,
   Shift+wheel rotates. The decal conforms to the surface and follows it across UV seams.
5. Adjust colors and finish (see below), then press the **hammer** to build the mod.
   With **Auto Rebuild** enabled (default), later edits rebuild the mod automatically.

### Colorset decals and colors

Most current (Dawntrail) gear has no diffuse texture — its colors come from the material's
colorset table, indexed by an ID texture. On such gear, decals work through the colorset:

- The decal image is quantized to at most **Max Colors** colors (default 6, configurable).
- Each color claims one free colorset slot; the decal's shape is written into the ID map so
  those pixels render through the claimed slots. The gear's baked cloth shading stays
  visible on the decal.
- Every color gets its own recolor swatch in the layer — recolor parts of the decal without
  touching the image. The extracted reference swatch stays next to it, and the eye icon
  highlights on your character exactly what a slot colors.
- **Dyeable** makes the claimed slots follow the gear's dye channels (the dye template is
  detected from how the rest of the gear dyes).
- If not enough free slots exist, the layer disables itself with an error — lower Max
  Colors, or free slots via **Manage Colorset** (below).

On materials with a diffuse texture, decals blend into the texture directly instead.

### Surface finish and material effects

Under **Material Effects** on each layer:

- **Normal Smoothing** flattens the cloth/skin bump detail under the decal, like a print
  sitting on top of the fabric.
- **Surface Finish** — Keep / Matte / Glossy / Custom — controls how the surface responds
  to light under the decal. Custom exposes raw **Roughness** and **Specular Scale**
  sliders. On colorset gear this is written into the claimed colorset rows (the actual
  shine driver), plus the material's mask map; a finished decal is treated as a dielectric
  print, so it stays matte even on metal armor.
- **Effect Scale** grows or shrinks the affected footprint relative to the decal.

### The Decal Library

Open it with the **Images** button on the main window's title bar. The library is shared
across all dTextures:

- Grid of thumbnails with **search**, **sort** (name/date), and free-form **tags** — click
  tag chips to filter (all selected tags must match).
- Select a decal to rename it, edit its tags, or delete it (Ctrl+Click). Deleting never
  breaks already-built mods — they bake the pixels in.
- **Presets**: on any placed layer, **Save Settings to Library** stores the layer's colors,
  surface finish, opacity, and default size/rotation on the library entry. The next time
  that decal is attached — on any gear — it starts from those settings. Everything remains
  overridable per layer, and the preset can be cleared from the library window.
- The same window doubles as the picker when adding a decal from the Decals tab, including
  importing a new image on the spot.

Decal images are stored in the plugin's config directory by default; a different folder can
be chosen in the settings (existing images are moved over safely).

### Manage Colorset

On colorset gear, the Decals tab's **Manage Colorset** section gives you slot-level control:

- A list of all 16 slots showing what is free, claimed by decals, or used by the gear —
  with a **Usable** override for slots the scanner blocks over a few stray pixels.
- **Extraction**: decals that gear authors baked into the ID map can be lifted out into
  their own layers (and into the decal library as images). An extracted decal can be moved,
  recolored, and disabled independently of the garment; removing it restores the original.

### Textures tab

A view-only inspector: every texture of the selected material as source vs. generated,
with zoom/pan, a UV-seam overlay, and a "Colorize" mode that renders ID maps through the
live colorset colors.

### Settings

The cog button opens the configuration: auto-rebuild, delete-mod-with-dTexture, default
decal colors, and the decal storage folder.

## Technical notes

- Applying a dTexture builds a real mod folder inside Penumbra's mod directory and
  registers, enables, and prioritizes it via Penumbra's IPC.
- Material colorset edits are written directly into the `.mtrl`; texture edits are
  composited in RGBA and BC-compressed through Penumbra's texture-conversion IPC.
- Rebuilds always start from the captured pristine source files, so layers never compound
  onto the plugin's own output.

## Building

Clone with submodules (`git clone --recursive`), then build `DynamicTextureManager.sln` with
the .NET SDK. The [Dalamud](https://github.com/goatcorp/Dalamud) dev environment is located
via `DALAMUD_HOME` if it is not in the default XIVLauncher location.

## Dependencies

- [OtterGui](https://github.com/Ottermandias/OtterGui)
- [Penumbra.Api](https://github.com/Ottermandias/Penumbra.Api)
- [Penumbra.String](https://github.com/Ottermandias/Penumbra.String)
- [Penumbra.GameData](https://github.com/Ottermandias/Penumbra.GameData)
- [Luna](https://github.com/Ottermandias/Luna)
- SixLabors.ImageSharp

## License

AGPL-3.0-or-later.
