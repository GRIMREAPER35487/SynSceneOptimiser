# Changelog

All notable changes to Synthos Scene Optimizer. Versions follow [Semantic Versioning](https://semver.org/); `-beta.N` releases are pre-releases and only appear in VCC/ALCOM with "Show pre-release packages" enabled.

## [1.2.0-beta.14] - 2026-10-13

### Added
- **The rest of the Avatar Compressor's settings in the Texture Complexity Compressor** - beta.13 copied its decisions and presets but not all of its settings. Now also:
  - **Frozen Textures** - Freeze a texture from the preview to set its divisor (1x-16x) and format by hand, or to skip it. Stored in the protection settings by asset GUID, so renames and moves keep it.
  - **Preview** - "Generate Preview" lists every texture in the open scene with its detail score, planned size, size in the world and format, the memory before and after, and the reason for every skipped texture, using the same decisions as the build.
  - **Texture Filters** - Main Textures, Normal Maps, Emission Maps and Other Textures can each be turned off (a texture is processed if any slot using it is enabled).
  - **Size Filters Use Source Size** and **Reformat Filtered Textures** - as in the Avatar Compressor.
  - **Custom preset** - every value is editable: analysis strategy and weights, thresholds, divisors, resolutions, Force Power Of Two, Min Source Size, Skip If Smaller Than and the high-quality format.
  - **Preferences** - Use Source Files, Analysis Backend and Resize Backend.
  - **Max Resolution warning** - When Max Resolution keeps a detailed texture below what is visible, the build log and the preview say so, with the size that would reach it.

### Changed
- **Keep Visible Detail now covers low-detail textures** - Copied from the Avatar Compressor, it skipped textures below the low-detail threshold, which is fine on an avatar but not for a floor or wall: a wood floor scoring 15% went from 512 to 64. Low-detail textures now keep visible detail in proportion to their score (half the target at half the threshold), so only truly flat textures shrink freely.

### Fixed
- **Normal maps scored almost no detail** (Synthos Texture Analysis 1.3.0-beta.3) - Imported normal maps are stored packed (X in alpha, Y in green), but the detail score read them as plain RGB, so normal maps scored near 0% and always got the largest divisor. They are now decoded the way Unity's shaders do. Requires Synthos Texture Analysis 1.3.0-beta.3.

## [1.2.0-beta.13] - 2026-10-13

### Changed
- **Texture Complexity Compressor makes the Avatar Compressor's decisions** - It shared the Avatar Compressor's detail score but sized and compressed textures its own way: everything was clamped between a fixed min and max resolution (so with a 512 maximum, a detailed 2048 texture lost as much as a flat one), the format was whatever the texture was imported with (uncompressed textures stayed uncompressed), and normal maps were not converted. It now uses the Avatar Compressor's pipeline: the size comes from the detail score through the same divisor curve and limits, emission keeps more detail, the format is chosen from detail and alpha (DXT1/BC7/BC5, ASTC on Quest) with the test for banding and blocky edges, normal maps go through the layout conversion, and textures are made from the original image file instead of re-compressing Unity's import. Textures that already have the right size and format are left as imported.
- **Presets** - High Quality, Quality, Balanced, Aggressive and Maximum are the Avatar Compressor's presets (same values, same names). New projects use Balanced. Projects that had changed the old min/max resolution or threshold settings get a Custom preset with those values, so builds don't change until another preset is picked.

### Added
- **Size in the world** - Like the Avatar Compressor's on-avatar size, the compressor measures how many texture pixels each texture gets per cm on the meshes that use it (per triangle, from UV area against surface area), against what is visible from a viewing distance (default 100 cm on PC, 150 cm on Quest). A texture sharper than anyone can see (a 2048 texture on a mug) is made smaller, and a detailed texture on a large surface is not shrunk below what is visible ("Keep Visible Detail"). Textures mapped any other way (triplanar, world or local mapping, other UV sets, flipbooks, matcaps) are not measured.
- **Advanced options** from the Avatar Compressor: Respect Import Max Size, Respect Import Format, Avoid Compression Artifacts and Skip Unknown Uncompressed Textures (uncompressed textures in unrecognized shader slots may be data such as lookup tables).

## [1.2.0-beta.12] - 2026-10-13

### Fixed
- **Texture Deduplicator missed duplicate TextMesh Pro fonts** - A font atlas lives inside its font asset and text components point at the font, not the atlas, so two copies of the same font (e.g. TextMesh Pro's LiberationSans SDF and ProTV's copy of it) both shipped their 1 MB atlas. Identical font assets (same glyphs, characters, face data and atlas pixels) are now merged: text components are pointed at one font and a material on its atlas (the kept font's own material when it looks the same, otherwise a copy of the text's material, so text looks unchanged). A text component whose material can't be moved is left exactly as it was. Dynamic fonts, protected text and fonts something else references are left alone.
- **Texture Deduplicator redirected materials for nothing** - When the copy being merged away was also used somewhere the optimizer can't change, both copies shipped anyway, but materials were still redirected (145 slots in a test world). Those duplicates are now left alone.

## [1.2.0-beta.11] - 2026-10-13

### Added
- **Allowed passes on protected entries** - Each entry in Protected Objects (objects, materials, meshes, textures) has an "Allowed passes" list: those passes work on it anyway, the rest still skip it. Only passes that can change that kind of entry are listed, each with a line saying what it does to it (swaps the material for a copy, replaces the mesh, changes import settings during the build, ...). Lists are empty by default, so existing protection is unchanged. A protected object only takes an optimized copy of a material or mesh when every pass that changed that copy is on its allow lists, so a material shared with unprotected objects that other passes also changed stays original on the protected object.

## [1.2.0-beta.10] - 2026-10-13

### Added
- **Texture Deduplicator** (new pass, Memory & Assets) - Finds textures that are exact copies of each other (same image file contents and the same import settings, so the build would hold the same data twice, e.g. an asset pack imported several times) and points materials at one copy. Only the build copies of materials the optimizer may change are redirected; if something else still uses one of the copies (protected or video objects, UI, sprites, scripts), that copy is the one the others point at. Runs after the Texture Complexity Compressor, so identical downscaled copies are merged too.

## [1.2.0-beta.9] - 2026-10-12

### Added
- **Unused Texture Shrinker** (new pass, Memory & Assets) - Imports textures that nothing in the world can ever draw at 32x32 for the build, then restores the import setting. Mochie Standard puts an 8192x8192 rain sheet (about 43 MB) on every material, and Unity ships it even with rain off. This works wherever the texture is referenced, including protected objects and video players, without changing any material. A texture is shrunk only when everything the scene pulls into the build (including prefabs scripts can spawn, animations, UI, sprites and Udon variables) uses it through material slots alone, and every one of those slots is skipped by the material's shader, both as authored and as swapped by the Mobile Shader Fallback, even with any keyword a script can change. It runs before the Texture Complexity Compressor, palettes and Mip Streaming, so they leave the shrunk texture alone. Not run in previews.
- **Udon scripts are read precisely** - The shrinker traces every Udon program to the exact shader keywords it can turn on or off and the material texture slots it can read, and tests each keyword by compiling the shader variant with it. Light Volumes' scripts, for example, only set their own `VRCLV_*` keywords, so they don't block anything. A keyword name built at runtime, a keyword list, or a program that can't be read makes it assume a script could turn on anything; nothing is shrunk and the log names the script.

### Changed
- **Ghost Texture Purger is back to its original job** (clearing slots the shader doesn't declare). Clearing texture slots behind disabled features, added in beta.6-beta.8, is replaced by the Unused Texture Shrinker, which reaches materials the purger can't change and leaves materials as they are. Color Palette materials still drop textures their shader can't sample.

## [1.2.0-beta.8] - 2026-10-12

### Fixed
- **Color Palette materials kept textures for features they turn off** - A palette material starts as a copy of the group's first material, so with Mochie Standard it kept the 8K rain sheet, droplet mask and SSR noise and pulled them into the build for every palettized object (about 1,000 objects in a test world). Generated palette materials now drop textures their shader variant never samples (follows the Ghost Texture Purger's "Clear Disabled Feature Textures" setting).
- **Ghost Texture Purger skipped every material an Udon behaviour referenced** - It now reads each Udon program and only skips materials for programs that can actually change shader keywords (`EnableKeyword`, `DisableKeyword`, `SetKeyword`, `shaderKeywords`, ...). Such a program protects the materials it references, renderers it references, and renderers on GameObjects it references or sits on. Programs that only set colors, toggle objects, play sounds and so on no longer block the cleanup. A program that can't be read still counts as able to change keywords.

## [1.2.0-beta.7] - 2026-10-12

### Fixed
- **Ghost Texture Purger skipped almost every material in Bakery-lit worlds** - Materials that a script could reach were left alone, but any component counted, and Bakery's lightmap storage lists every lightmapped renderer, so nearly all materials were skipped (a world with 2,100 lightmapped renderers kept its 43 MB Mochie rain sheet). Only Udon behaviours (including UdonSharp) count now, since they are the only scripts that run in a VRChat world.

## [1.2.0-beta.6] - 2026-10-12

### Added
- **Ghost Texture Purger clears textures behind disabled shader features** - A material keeps its texture in a slot even when the feature that reads it is turned off, and Unity still ships that texture. Mochie Standard, for example, fills `_RainSheet` with an 8192x8192 rain flipbook (about 43 MB of VRAM) on every material, rain on or off. The purger now compiles each material's actual shader variant, reads which textures it samples, and empties the slots it never reads on the build copy (rain, droplet and SSR textures with those features off, `_EmissionMap` on a Standard material without emission, and so on). Works with any shader. Each shader variant is checked once and cached; on the first build the check is limited to 3 minutes and the rest continues on the next build. Materials that a script or Udon behaviour references (directly or through its renderer) are left alone, since a script could turn the feature on at runtime. Can be turned off with "Clear Disabled Feature Textures" in the pass settings.

### Changed
- The Texture Complexity Compressor and Mip Streaming skip texture slots the purger emptied, so they no longer spend time on a texture that is leaving the build.

## [1.2.0-beta.5] - 2026-10-12

### Added
- **Light Volumes Atlas Auto Re-pack** (new General Fix) - VRC Light Volumes only applies new bakes and the Downscale Volumes setting when its atlas is packed, and a build ships the atlas file as it is, so an atlas packed before the latest bake or before Downscale Volumes was turned on ships old, full-size data (seen on a world shipping a 48 MB atlas from three months before its current bake with x2 downscale enabled). Before a VRChat build or entering Play Mode, an out-of-date atlas is now re-packed with Light Volumes' own "Pack Light Volumes" action; the build is stopped once with a message so it can be started again when packing finishes. The pass shows the atlas status and has a "Re-pack Now" button.
- **Lighting Texture Size Limits** (new pass, Memory & Assets) - Caps directional lightmaps (default 2048 on PC, 1024 on Quest) and reflection probe cubemaps (default 128 per face) for the build. A directional lightmap only stores which way the light comes from and probes give blurry reflections, so both lose little at these sizes: on a 4096 directional lightmap plus 25 probes at 256 this saves about 25 MB. Each cap can be changed or turned off per platform. Like Mip Streaming, the import settings are changed for the build or Play Mode only and restored afterwards (also after a crash or restart). Not run in previews.

### Fixed
- **VRAM Analyzer counted VRC Light Volumes source textures** - Each Light Volume Instance keeps its three baked volume textures, but VRC Light Volumes 3.x packs them into its atlas and removes them from the build, so only the atlas ships. They are no longer counted when the installed Light Volumes version does this (older versions that keep them are still counted).

## [1.2.0-beta.4] - 2026-10-12

### Added
- **VRAM Analyzer: what uses each environment item** - Click a lightmap, probe, skybox or light volume texture to see what references it in the trace panel: the object and the component field (e.g. `Bakery Volume.bakedTexture0`), the reflection probe, or how many renderers use a lightmap. Copy Page Details on the Environment tab includes a "Used By" column.

## [1.2.0-beta.3] - 2026-10-12

### Fixed
- **VRAM Analyzer counts what the scene actually loads** - It used to read only mesh and skinned mesh renderers. It now follows every component's references like the texture pass does: particles, trails and lines, UI images, sprites, terrain (layers, splat maps, heightmap), light cookies, camera render textures, Udon and other scripts, and materials swapped in by animations. Objects tagged EditorOnly are skipped (they are stripped from builds), mesh colliders no longer count as GPU memory, and a texture used in several places is counted once.
- **Environment is complete and limited to the scanned scene** - Adds shadowmasks, custom and realtime reflection probes, and baked lighting referenced by components (Bakery maps, light volumes). With several scenes open, only this scene's lightmaps and probes are counted. HDR textures are estimated as BC6H on PC, and render textures include their depth buffer.
- **Report no longer disappears** on entering Play Mode or after a script recompile.
- **Large scenes no longer lag the window** - Lists only draw the rows on screen.
- **Markers showed as boxes** - Emoji replaced with coloured markers and Unity's own icons.

### Changed
- **VRAM Analyzer** - Search box on the lists, sortable Environment tab with a total per type (lightmaps, probes, skybox, ...), asset path on hover, and the header shows which platform the sizes are for. Changing "Estimate Memory For" rescans right away.
- The preview's Before/After numbers use the same scan, so they now include particles, UI and the rest, and leave out EditorOnly objects.

## [1.2.0-beta.2] - 2026-10-12

### Fixed
- **Preview under-reported the VRAM saving** - The preview optimized a copy of the scene opened next to yours, and the "after" measurement counted the lightmaps and baked reflection probes of both scenes, so the total barely went down even when textures and meshes shrank. The preview now closes your scenes, measures before and after on the copy alone, and reopens your scenes when it's done. Unsaved changes are saved first (it asks).

### Added
- **Environment row in the run report** - Lightmaps, baked reflection probes and skybox textures, so Total VRAM = Textures + Meshes + Environment.

## [1.2.0-beta.1] - 2026-10-12

### Changed
- **Texture Complexity Compressor uses Synthos Texture Analysis** - The detail scoring, format choice and resizing now come from the shared `com.synthos.texture-analysis` package (the same code as Avatar Compressor), which VCC installs automatically. Textures are scored with the **Combined** method: besides edges and colour spread it measures the fine detail that would be lost by shrinking and how much of the texture is detailed. Noisy or grainy textures (concrete, dirt) shrink more readily and textures with fine features (text, line art) keep their size more often. Scoring runs on the GPU when available. All scores are recalculated once; results will differ from 1.1.0, so check your thresholds.
- **Generated textures** (textures without an import file, e.g. baked `.asset` files) are resized with area averaging instead of a plain blit, skip the alpha channel when it holds no transparency (DXT1 instead of DXT5), and step to a safer format when the cheap one would band. On Quest their normal maps use ASTC 4x4 (was 5x5). Textures with an import file keep using a re-imported copy with their own import settings.

### Fixed
- **Downscaled texture and audio copies were missing on the first run** - Textures and audio clips with an import file are copied into the cache and re-imported smaller. The copies were made inside a batched asset edit, where Unity writes the file but never imports it, so the copy could not be loaded and the original was kept. They only took effect on a later run, after Unity happened to import the files, so a build right after adding or changing textures shipped them at full size. Copies are now imported straight away, and copies left behind unimported are imported on the next run.

## [1.1.0] - 2026-10-11

Stable release of everything in the 1.1.0 betas below. Highlights:
- Texture downscaling keeps compression; normal maps, mip streaming, audio and protection fixed and made non-destructive.
- Per-project settings, presets, Preview (Dry Run), run report and cancelable progress.
- Mesh fixes (Skip/Preserve lists, colliders, LOD groups, UV3/vertex colours), lightmapped meshes simplified by default.
- Merged Mochie palette materials render with the right metallic, smoothness and emission.
- New UI tools: UI Optimizer pass, UI Audit, canvas distance culling and UI sprite atlases.
- New material tools: Material Audit and Material Deduplicator.

## [1.1.0-beta.16] - 2026-10-10

### Fixed
- **UI icons came out too big with the sprite atlas** (e.g. ProTV controls). Tight sprites only store their pixels without the transparent margin, and the atlas made that trimmed part the whole sprite, stretching it over the margin. Atlas sprites now keep the full original rect with the pixels at their original offset. Atlases are rebuilt once.

## [1.1.0-beta.15] - 2026-10-10

### Added
- **Material Deduplicator pass** (Batching & Instancing, runs first): renderers using exact duplicate materials (same shader, keywords, enabled passes, textures, tiling, values and GI flags) are pointed at one shared copy on the build copy, so they can share static batches and instanced draws. Script, UdonSharp and Udon references to the duplicates are pointed at the same copy. Materials swapped in by animations, protected materials, materials held by protected scripts, and video player / protected renderers are left alone. Material assets are never edited.

### Changed
- Material Audit treats materials with different baked-emission (GI) flags as different.

## [1.1.0-beta.14] - 2026-10-10

### Changed
- Material Audit leaves out plain-colour materials the Color Palette pass merges anyway (same rule as that pass), and says how many it left out, so its counts only show savings a new pass would add.

## [1.1.0-beta.13] - 2026-10-10

### Added
- **Material Audit** (Window > Synthos > Material Audit, or the button in the main window): groups the open scene's materials into exact duplicates and materials that differ only by the main texture's tiling/offset, shows how many materials each group could become, how many extra mesh copies baking tiling into UVs would need, and why a group can't be baked (unknown shader, triplanar, non-UV0/world UVs, rotation, scrolling, parallax, non-mesh or protected renderers). Read-only.
- Main window buttons for the Material Audit and UI Audit.

## [1.1.0-beta.12] - 2026-10-10

### Fixed
- UI sprite atlases were always square, so a set of sprites slightly too big for one size left most of the next size empty (empty space costs as much memory as used space once compressed). Atlases can now be rectangular (e.g. 1024x512), use the smallest area that fits, and full atlases are trimmed to the height they use. Sprites are packed tallest first for tighter rows. Atlases are rebuilt once.

## [1.1.0-beta.11] - 2026-10-10

### Changed
- UI sprite atlas: images on video players (e.g. ProTV controls) can be packed. Video screens are RawImages or renderers, which the atlas never touches.
- The report now lists why sprites were left alone (e.g. "12 on tiled images, 4 larger than 512 px").

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
