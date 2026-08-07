# Changelog

Stable-release history. Betas ship incrementally on the rolling testing channel; this file
tracks what actually reaches the default channel. Earlier tagged releases (back to v0.1.0)
are on the [Releases page](https://github.com/VirstaXIV/DynamicTextureManager/releases) but
predate this file.

## v0.9.9

### Fur, Scales and Skin Patterns (new)

A generated coat — fur, scales, or skin patterns — covering the whole body and face as one
continuous surface, combed along the body's natural flow:

- Colors follow your hair by default (Glamourer changes included), or pick your own.
- Markings — tabby stripes, spots, marbling, your own painted design, or a custom tileable
  image via the Resource Library's new **Marking Patterns** tab (with ready-made examples:
  Rosettes, Paw Prints, Stars).
- Paint tools in the 3D preview: brush and line, with undo/redo, for erasing coverage or
  drawing markings by hand.
- Relief bakes into the normal map and shades live in the preview without building; an
  auto-rebuild toggle keeps the preview current.
- Brush **Strength** reworked to read as hardness — max cuts off sharply at the brush edge,
  low values blend gently.

### Canvas Groups

Renamed "dTexture" to **Canvas Group** throughout the interface, and added per-group mod
priority for resolving conflicts between overlapping projects.

### Gear and body canvases

- The Body canvas now derives from the body models your character actually renders with,
  fixing feet/hands replacement mods being mistaken for the whole body mod.
- Skin exposed by worn gear (false-nail gloves and similar pieces) now matches the body's
  coat and markings automatically.

### Decals

- **Fixed**: a decal reused from the Resource Library could lose its ability to recolor
  entirely if that same decal was ever placed on non-colorset gear before — its colorset
  eligibility now always follows the gear it's actually placed on, not a stale setting
  carried over from a previous placement.
- **Manage Colorset** — both the per-decal row editor and the baked-decal extraction list
  now show colors as compact palette swatches instead of long inline editors; the extraction
  list is a proper table ordered by slot (1-16) instead of a size-sorted list.

### Under the hood

- Migrated the plugin's UI/service framework from OtterGui to
  [Luna](https://github.com/Ottermandias/Luna) — no user-facing change, groundwork for
  future development.
- Removed leftover disabled code from earlier development: an old fur/scales flow-anchor
  placement system and an unused skin-tint option that never got a control.

## v0.9.0

Baseline for this changelog. Includes hair and tail conversion to an animated glowing
effect (Shine, three-color model, live character colors), colorset decal placement and
recoloring with anti-aliased baking and automatic row allocation, skin tattoos, and the
embedded 3D preview.
