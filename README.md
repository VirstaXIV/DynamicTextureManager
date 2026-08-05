# Dynamic Texture Manager

A companion plugin for [Penumbra](https://github.com/xivdev/Penumbra) that manages **overlay
mods**: pick a target — gear you are wearing, your body, or your hair — stamp decals onto it,
recolor them, tune the surface finish, convert hair and tails to an animated glowing effect,
and apply the result as a plugin-managed persistent mod in Penumbra.

## Status

In active development. Stable releases go out on the default channel; a rolling beta is
available by enabling testing builds for the plugin in the installer. Expect rough edges —
feedback and bug reports are welcome on the issue tracker.

## AI usage disclosure

This plugin is developed as a human-directed AI collaboration, at what the
[Dalamud AI usage policy](https://dalamud.dev/plugin-publishing/ai-policy) calls the
**Copilot** level: a large share of the implementation is written by an AI assistant
(Claude), while a human plans the features, directs the work, reviews the changes, and
tests the results in game with each release. The commit history reflects this openly
(AI-assisted commits carry a `Co-Authored-By` trailer).

The plugin icon is AI-generated.

## Installing

Add this repository to Dalamud's custom plugin repositories:

1. In-game: `/xlsettings` → **Experimental** → **Custom Plugin Repositories**
2. Add `https://raw.githubusercontent.com/VirstaXIV/DynamicTextureManager/master/repo.json`
3. Save, then install **Dynamic Texture Manager** from the plugin installer (`/xlplugins`).

Requires [Penumbra](https://github.com/xivdev/Penumbra) to be installed and enabled.

## Concepts

- **dTexture** — one overlay project: a source (which pieces to edit), its decal layers, and
  its colorset edits. Each dTexture builds into exactly one Penumbra mod.
- **Decal** — an image from your library, stamped onto a material. Placed directly on the 3D
  model, it conforms to the surface and carries per-layer colors, finish, and size.
- **Generated mod** — pressing **Build** writes a real, self-contained mod into Penumbra.
  Built mods bake everything in: they keep working with the plugin unloaded.

## Documentation

Full documentation lives in the
[wiki](https://github.com/VirstaXIV/DynamicTextureManager/wiki).

User guide:

- [Getting Started](https://github.com/VirstaXIV/DynamicTextureManager/wiki/Getting-Started) —
  install, first decal, first build
- [Sources](https://github.com/VirstaXIV/DynamicTextureManager/wiki/Sources) —
  picking gear, body and hair pieces
- [Decals](https://github.com/VirstaXIV/DynamicTextureManager/wiki/Decals) —
  placement, recoloring, surface finish, extraction, Manage Colorset
- [Hair and the Animated Effect](https://github.com/VirstaXIV/DynamicTextureManager/wiki/Hair-and-the-Animated-Effect) —
  Shine and the animated glow for hair and tails
- [Library, Settings and Troubleshooting](https://github.com/VirstaXIV/DynamicTextureManager/wiki/Library-Settings-and-Troubleshooting)

Technical documentation — full disclosure of how each mechanism works, including the
formulas and reverse-engineered shader facts the implementation is built on:

- [Colorset Decals and the ID Map](https://github.com/VirstaXIV/DynamicTextureManager/wiki/Colorset-Decals-and-the-ID-Map)
- [Surface Projection and Decal Baking](https://github.com/VirstaXIV/DynamicTextureManager/wiki/Surface-Projection-and-Decal-Baking)
- [The Composite and Build Pipeline](https://github.com/VirstaXIV/DynamicTextureManager/wiki/The-Composite-and-Build-Pipeline)
- [The Animated Effect](https://github.com/VirstaXIV/DynamicTextureManager/wiki/The-Animated-Effect)
- [The 3D Preview System](https://github.com/VirstaXIV/DynamicTextureManager/wiki/The-3D-Preview-System)

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
