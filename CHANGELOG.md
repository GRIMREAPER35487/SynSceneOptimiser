# Changelog

All notable changes to Synthos Scene Optimizer. Versions follow [Semantic Versioning](https://semver.org/); `-beta.N` releases are pre-releases and only appear in VCC/ALCOM with "Show pre-release packages" enabled.

## [1.1.0-beta.10] - 2026-10-10

### Added
- **Include Sprites Scripts Use** (under Pack UI Sprites into Atlases, on by default): sprites held by scripts, Udon/UdonSharp variables and button sprite swaps are packed too, and those references are pointed at the atlas copies on the build copy (UdonSharp fields and the UdonBehaviour reference list both), so swaps keep batching, sprite comparisons in scripts still match and the original sprite doesn't ship. Sprites changed by animations, used by SpriteRenderers or held by protected objects are still left alone.

## [1.1.0-beta.9] - 2026-10-10

### Added
- **Pack UI Sprites into Atlases** (UI Optimizer, opt-in): packs UI Image sprites into shared atlases (up to 2048 on PC, 1024 on Quest) so images on a canvas can batch together. Atlases are PNGs in the per-platform cache, imported as multi-sprite textures with the source's filter mode, colour space, pixels-per-unit and compression, with 9-slice borders, pivots and edge padding kept. Sprites anything else uses or swaps at runtime are never packed, so both versions don't ship.

## [1.1.0-beta.8] - 2026-10-10

### Fixed
- Beta.7 failed to compile (the editor assembly was missing its reference to `VRC.Udon.Editor`), so Unity kept running beta.6 and the distance culling controls never appeared.

## [1.1.0-beta.7] - 2026-10-10

### Added
- **Canvas Distance Culling:** hides world canvases when the local player is far away and shows them again on approach (Canvas component and UI collider only; scripts keep running; nothing synced). Set up from the UI Audit with "Add Distance Culling to Suggested Canvases" or per canvas, with an editable distance per canvas. Canvases switched by an animation or by a script holding the Canvas component are not suggested, and adding one shows a warning.
- The UI Optimizer pass says when distance culling isn't set up yet, and how many canvases it manages once it is.
- UI Audit "Hide at" column, and notes on whether a script holds the Canvas component itself or only its GameObject.

### Changed
- The package has a Runtime assembly again, holding the UdonSharp culling script.

### Fixed
- UI Audit counted every graphic without "skip when transparent" as a transparent graphic being drawn. It now reports only graphics that are fully transparent right now, and lists the rest separately.
- UI Audit offered fixes on video player canvases, which the UI Optimizer pass skips. They are now marked "video player, not changed".
- UI Audit note counts read like part of the canvas name (e.g. "Menu (3)"); they now show as "- 3 notes".

## [1.1.0-beta.6] - 2026-10-10

### Added
- **UI Optimizer pass:** stops drawing fully transparent UI graphics, turns off Raycast Target on graphics nothing can click, and flattens elements sitting less than 0.5 mm off their canvas so they batch again. Each fix has its own toggle; build, Play Mode and preview copies only.
- **UI Audit window** (Window → Synthos → UI Audit, or "Configure / Open UI Audit..." on the UI Optimizer pass): every canvas in the open scene with estimated draw calls, graphics, textures, masks, shaders and size, and what makes it expensive. Read-only.

### Fixed
- GPU Instancing Enabler's "Preview Eligible Materials" and "Apply to Material Assets on Disk" buttons now use the same rule as the build (renderers sharing both mesh and material), so they no longer list or change materials the build would skip.

## [1.1.0-beta.5] - 2026-10-10

### Changed
- **Skip Lightmapped Meshes is off by default again.** Turning it on in beta.1 stopped most meshes in fully baked worlds from being simplified, which raised triangle count and GPU cost. Turn it on if you see smeared shadows or dark seams after optimizing.

## [1.1.0-beta.4] - 2026-10-09

### Fixed
- **Preview (Dry Run) changed the open scene.** The renderer search also collected renderers from every other loaded scene, so passes and the final apply step modified the user's scene alongside the temporary preview copy. It now only returns renderers from the scene being optimized, its cache is tied to that scene, and the risky fallback that could reach prefab/preview scenes was removed.
- Mesh auto-tune measures the scene being optimized instead of whichever scene is active.

## [1.1.0-beta.3] - 2026-10-09

### Fixed
- **Merged Mochie materials came out fully metallic and fully smooth (regression in beta.2).** Mochie shaders were matched to the Unity Standard layout because their name contains "standard", so the palette texture never reached Mochie's packed map and the empty map sampled as white. The most specific shader layout now wins, and the Mochie palette setup assigns its packed and emission maps itself. Palette caches are rebuilt once.

## [1.1.0-beta.2] - 2026-10-09

### Added
- **Preview (Dry Run):** runs every enabled pass on a temporary copy of the open scene and shows what would change, with VRAM before and after. Your scene is not modified.
- **Optimization report** (Window → Synthos → Last Optimization Report): status, time and notes for every pass of the last build, Play Mode run or preview. Optional "Show Report After Each Run".
- **Cancel** button on the progress bar. Cancelling stops a build; in Play Mode the remaining passes are skipped.
- **Presets:** Balanced (Default), Quality and Quest Aggressive, plus Reset All.
- **Reset Audio Import Settings** window to put clips back to Unity's defaults (undoes changes made by versions before 1.1).
- **VRAM Analyzer "Estimate Memory For"**: exact numbers for the active build target, or PC/Quest estimates from each texture's import settings.

### Changed
- **Settings are now per project** (`ProjectSettings/SynSceneOptimizer.json`). Values previously set in the machine-wide editor preferences are copied in automatically the first time a project opens.
- **Audio Clip Optimizer is non-destructive:** it optimizes the clips the scene uses (AudioSources and Udon/UdonSharp references) as cached copies, chooses settings by clip length (streamed Vorbis / compressed Vorbis / ADPCM), and points scene references at the copies. The "Entire Project" scan mode was removed.
- Mesh colliders that shared a renderer's mesh now follow it to the deduplicated, memory-optimized or simplified mesh.
- The mesh simplifier skips meshes inside LOD groups and non-triangle meshes.
- GPU Instancing Enabler only enables instancing where renderers share both mesh and material, and never edits material assets during builds.
- Texture analysis reads textures back at up to 1024 px in small batches and caches scores, so repeat builds skip unchanged textures.
- Textures still used outside optimized renderers (UI, particles, protected objects, Udon, skybox, animated material swaps) are no longer downscaled, so both versions never ship.
- Baked-lighting detection uses import type, the scene's lightmap list and lighting output folders instead of broad name matching.
- All menu items live under Window → Synthos.
- `package.json`: removed the Unity Package Manager `dependencies` block (VPM uses `vpmDependencies`), added license, documentation and changelog links.

### Fixed
- **Merged Mochie Standard materials rendered fully metallic.** The palette pass now enables Mochie's packed workflow, uses matching texture channels, reads metallic/roughness/emission the way Mochie computes them, resets triplanar/stochastic/UV-set sampling, and only merges materials with the same shader features.
- Protected objects could still receive optimized materials and meshes when changes were applied.
- The mesh memory pass no longer runs a slow asset unload on every run when it created nothing.
- Faster video-player detection and palette building in large scenes.

### Removed
- `SynDynamicPropertyBlock` runtime component (never used; VRChat strips custom scripts from worlds) and the empty Runtime assembly.

## [1.1.0-beta.1] - 2026-10-09

### Changed
- **Texture downscaling keeps compression:** downscaled textures are importer-backed copies re-imported by Unity at a lower max size, keeping the original format, color space, normal-map encoding, mips and sampler state. Previously they were saved uncompressed and often increased VRAM.
- **Mip streaming** only applies to textures that benefit, never forces mipmaps on, and journals every import change to `Library/` so it is reverted after builds, Play Mode and editor restarts. New Repair Mip Streaming Changes window for projects changed by older versions.
- Audio Clip Optimizer became opt-in and is flagged as modifying project assets.
- Protection settings moved out of the package into `Assets/SynSceneOptimizer/`.
- Unused cache entries are removed after builds (default 30 days).
- Builds stop if a pass fails partway (can be turned off).
- Only the optimizer's own cache assets are saved during runs.
- Deduplication only removes static batching from same-mesh/same-material groups large enough to instance.
- Mirror pass works with either reflect-layers field name and never strips Player/PlayerLocal/MirrorReflection.
- Bakery pass only re-enables probes Bakery itself disabled.
- EditorOnly pruner keeps renderers that scripts, Udon or animators may fill at runtime.

### Fixed
- Normal maps corrupted when downscaled; alpha treated as transparency on smoothness/mask maps.
- Mesh Skip/Preserve lists stopped matching after other passes swapped meshes; simplifier smeared baked lightmaps; UV3 z/w and black vertex-color masks were wiped; deduplication ignored blendshapes and UV4-7.
- Stale cache results after changing settings; auto-tune overwrote tier sliders.
- Palette materials never received later staged changes (e.g. Quest shader fallback); palette metallic texture was sRGB.
- Probe-lit statics lost light probes; Ghost Texture Purger removed nothing.
- Protection did not apply to the build copy of the scene.
- Pass order was not deterministic.

## [1.0.9] - 2026-10-09
### Fixed
- Adopted VRCFury build hook order and non-destructive asset persistence.

## [1.0.8] - 2026-10-09
### Fixed
- Restored native `.asset` texture storage and raw linear colors for the palette pass.

## [1.0.7] - 2026-10-08
### Fixed
- Color palette sRGB/linear color fidelity, UV tiling offsets and texture import settings.

## [1.0.6] - 2026-10-08
### Fixed
- First fix for material scrambling and cache collisions.
### Changed
- Release workflow triggers the VPM listing rebuild; README expanded.

## [1.0.5] - 2026-10-05
- Removed the Frame Exporter button from the optimizer window.

## [1.0.4] - 2026-10-05
- Enabled the Open Frame Exporter button; added credits for avatar-compressor and Meshia.

## [1.0.3] - 2026-10-05
- Fixed a missing `using System.IO` in SynProtectionData.

## [1.0.2] - 2026-10-05
- Added `legacyPackages` entry for `com.ramtype0.meshia.mesh-simplification`.

## [1.0.1] - 2026-10-05
- Package type set to world with a `com.vrchat.worlds` dependency.

## [1.0.0] - 2026-10-05
- Initial release.
