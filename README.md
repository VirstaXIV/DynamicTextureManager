# Dynamic Texture Manager

A companion plugin for [Penumbra](https://github.com/xivdev/Penumbra) that manages **overlay
mods**: pick a target — gear you are wearing, an item, or an installed Penumbra mod's files —
edit its materials (colorset rows) and textures (decal stamping), and apply the result as a
plugin-managed persistent mod in Penumbra.

## Status

Early work in progress. Nothing here is stable yet.

## Installing

Add this repository to Dalamud's custom plugin repositories:

1. In-game: `/xlsettings` → **Experimental** → **Custom Plugin Repositories**
2. Add `https://raw.githubusercontent.com/VirstaXIV/DynamicTextureManager/master/repo.json`
3. Save, then install **Dynamic Texture Manager** from the plugin installer (`/xlplugins`).

Requires [Penumbra](https://github.com/xivdev/Penumbra) to be installed and enabled.

## How it works

- Overlay definitions ("dTextures") are managed in a Glamourer-style file-system selector.
- Applying a dTexture builds a real mod folder inside Penumbra's mod directory (grouped under
  a `DynamicTextureManager/` sort folder) and registers, enables, and prioritizes it via
  Penumbra's IPC. Generated mods are self-contained: they keep working with the plugin
  unloaded and survive restarts.
- Material colorset edits are written directly into the `.mtrl`; texture edits are composited
  in RGBA and BC-compressed through Penumbra's texture-conversion IPC.

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
