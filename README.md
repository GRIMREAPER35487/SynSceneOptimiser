<div align="center">
  <img src="https://raw.githubusercontent.com/GRIMREAPER35487/SynSceneOptimiser/main/.github/banner.png" alt="Synthos Scene Optimizer" width="100%" />
</div>

<br/>

# Synthos Scene Optimizer

> [!WARNING]
> **Experimental:** This tool is experimental. While it has been tested as thoroughly as possible, due to the nature of Unity and diverse scene configurations, some scenes might misbehave. Always make sure to **back up your project and scenes** before running optimizations just to be safe.
>
> **Non-Destructive Design:** Most of the tool is completely **non-destructive**—textures, meshes, and materials are generated as separate copies inside an isolated cache (`Assets/SynSceneOptimizer_Cache/`), leaving your original project files untouched. Any features that make permanent changes to project assets are marked with a warning triangle (⚠️) in the feature list below.

Comprehensive VRChat scene and world optimization suite for Unity. Features automatic texture VRAM reduction, mesh decimation & compression, GPU instancing, audio & particle optimization, and mirror reflection mask tuning.

## Features

- **Smart Texture VRAM Optimizer:** Analyzes scene textures using Sobel edge detection and spatial frequency algorithms (derived from Avatar Compressor) to identify low-frequency, flat, or simple textures (e.g., solid walls, plain floors). Dynamically downscales them (e.g., from 2048 to 512 or 256) while retaining full resolution for high-detail textures and normal maps. Automatically protects Bakery lightmaps and Mochie shader assets, with full support for custom drag-and-drop exclusions. Generated assets are saved into the per-platform cache.
- **Mesh Simplifier:** High-performance polygon reduction powered by Meshia (Quadric Error Metrics). Supports target scene triangle budgets, configurable decimation thresholds, and platform-specific profiles (PC vs. Mobile/Quest). Preserves UV borders, lightmap seams (UV1/UV2), normals, and blend shapes to prevent visual artifacts. Includes a dedicated Skip List to leave hero assets untouched and a Preservation List for custom decimation ratios.
- **Mesh Memory Optimizer:** Reduces vertex data overhead across all scene meshes. Converts UV3 Texture Array slice channels into compact 2-component Vector2 coordinates (cutting UV3 memory footprint in half), strips unreferenced or empty vertex channels (such as zeroed tangents, unused UVs, and default vertex colors), and purges intermediate mesh clones to save memory.
- **Mesh Deduplicator & Instancer:** Scans the scene for geometrically identical meshes across different GameObjects and unifies their references to point to a single master mesh instance. Automatically clears the `Batching Static` flag on unified objects so Unity can batch them together via hardware GPU instancing, drastically reducing unique mesh memory.
- **GPU Instancing Enabler:** Audits materials used across multiple scene renderers and automatically enables GPU Instancing where applicable. Combines draw calls for dynamic objects and unbatched statics into efficient hardware instanced batches, reducing CPU render thread overhead with zero visual penalty.
- **Audio Clip Optimizer:** Finds every clip the scene uses (AudioSources and Udon/UdonSharp references) and picks import settings from the clip's length. Optimized copies live in the cache and scene references are pointed at them, so your audio files are never modified:
  - **Long clips (30 s and up, e.g. music):** Vorbis with Streaming load type, so they never sit in memory.
  - **Medium clips (3-30 s):** Vorbis, Compressed In Memory.
  - **Short clips (under 3 s, e.g. effects):** ADPCM with Decompress On Load for instant, low-CPU playback.
  - Clips changed by versions before 1.1 can be put back to Unity's defaults with **Window** → **Synthos** → **Reset Audio Import Settings**.
- **Particle System Safety Clamping:** Audits every `ParticleSystem` in the scene, calculates realistic max particle counts based on emission rates, lifetimes, and bursts, and clamps maximum capacity to prevent accidental runaway particle counts from tanking player framerates. Forces off-screen culling to `Pause` so dormant particle systems don't waste CPU/GPU simulation cycles.
- **UI Optimizer:** Cheaper world UI without visual changes. Stops drawing fully transparent graphics (fades and clicks keep working), turns off Raycast Target on text and images nothing can click, and moves elements sitting under 0.5 mm off their canvas back onto it so they batch again. Mask graphics, custom UI shaders, animated and protected objects are left alone. Optionally (**Pack UI Sprites into Atlases**, off by default) packs the sprites of UI images into shared, compressed atlases so a panel's images draw together instead of one draw per texture, keeping 9-slice borders and pivots. Sprites swapped at runtime (scripts, Udon, button sprite swaps, animations), tiled images, custom UI shaders and sprites over 512 px are skipped.
- **UI Audit Window (Window → Synthos → UI Audit, or "Configure" on the UI Optimizer pass):** Lists every canvas in the open scene with its estimated draw calls, visible graphics, textures, masks and size, plus plain-language notes on what makes it expensive and what the UI Optimizer would fix. The audit itself is read-only.
- **Canvas Distance Culling (set up from the UI Audit):** Hides world canvases the local player is far away from and shows them again on approach, so they skip drawing, rebuilding and laser-pointer checks. One small UdonSharp manager (`SynCanvasCuller` in your scene) checks a few canvases per frame against the player's head, measured to the nearest edge of each canvas. It switches the Canvas component and its UI collider, never the GameObject, and nothing is synced. It does nothing until you add canvases: use "Add Distance Culling to Suggested Canvases" (canvases no animation or script switches on and off) or add them one by one, and adjust each distance in the audit. Its UdonSharp program asset is created in `Assets/SynSceneOptimizer/Udon/`.
- **Material Audit Window (Window → Synthos → Material Audit):** Read-only list of materials in the open scene that are exact duplicates or differ only by tiling/offset, with how many materials they could become and what blocks baking tiling into mesh UVs.
- **Mirror Layer Mask Optimizer:** Detects VRChat Mirror Reflection components and automatically sanitizes their reflection culling masks. Strips heavy, non-essential rendering layers (such as UI, UiMenu, Water, StereoLeft, StereoRight, and custom-defined layers) to eliminate redundant double-draw overhead when players look at mirrors.
- **Structural Cleanup Passes:** A suite of lightweight scene cleanups that eliminate hidden rendering leaks:
  - **Material Slot Trimmer:** Truncates excess, empty, or unassigned material array slots on renderers that exceed the mesh's actual submesh count.
  - **Material Reflection Optimizer:** Disables reflection probe usage on renderers whose materials have low smoothness/specular values, skipping pointless cubemap lookups.
  - **Light Probe Optimizer:** Turns off light probe sampling on static objects that are already lit by baked lightmaps.
  - **Ghost Texture Purger:** Removes orphaned, unused texture properties left behind in material serialization data.
- **Mipmap Streaming & Mobile Passes:**
  - **Mipmap Streaming Enabler:** Enables texture streaming only where it helps: mipmapped textures of 256px or more used by mesh, skinned and terrain renderers, plus lightmaps. UI, sprites, cookies, lookup tables, ramps, palettes and point-filtered textures are skipped. Import settings are restored after the build. Projects changed by older versions can be cleaned up with **Window** → **Synthos** → **Repair Mip Streaming Changes**.
  - **Disable Stochastic Sampling:** Automatically disables expensive stochastic texture sampling on mobile/Quest (Android & iOS) builds for maximum performance.
  - **Mobile Shader Fallback:** Automatically swaps heavy desktop PC shaders to lightweight mobile-ready alternatives during Android builds.
- **Auto Reflection Probe Baker (Bakery Fix - Optional):** Requires Bakery GPU Lightmapper (optional, only needed for this pass). Located under the **General Fixes** tab. Hooks directly into Bakery's bake completion events (`OnFinishedFullRender` / `OnFinishedProbes`). When a lightmap bake completes, it automatically triggers reflection probe baking and automatically re-enables any reflection probes Bakery left disabled, ensuring reflection probes are never left unbaked, black, or cleared in your final world. Also provides one-click manual buttons to trigger probe bakes and re-enable probes on demand.
- **VRAM Analyzer Window:** Interactive diagnostic window that provides a real-time, categorized breakdown of scene memory usage across textures, meshes, and materials. Helps identify high-consumption assets, previews projected optimization gains, and exports persistent build reports.
- **Automatic Legacy Migration:** Detects and seamlessly migrates cache files and settings from older optimizer iterations (`Assets/SynSceneOpti_v2` / `Assets/SynSceneOptimiser`) into the modern unified cache structure without breaking asset GUIDs or project references.

## How It Works

- **Automated Scene Processing Pipeline:** Hooks directly into Unity's `IProcessSceneWithReport` build lifecycle. When building or publishing a world via the VRChat SDK, the optimizer runs automatically on a temporary staging copy of the scene. Your original saved scene file (`.unity`) remains untouched.
- **Deterministic Multi-Platform Cache:** Generated assets (downscaled textures, decimated meshes, combined materials) are stored under an isolated cache directory (`Assets/SynSceneOptimizer_Cache/{Platform}/`). Assets are hashed against their content and optimization settings, ensuring blazing-fast iterative builds where only modified assets are re-processed.
- **Virtual Staging & Asset Compactor:** Uses an in-memory staging layer (`SynPipelineCompactor`) to manage material property changes, GPU instancing flags, and mesh assignments during build packaging, avoiding permanent mutation of project source files.
- **Play Mode Testing:** When entering Play Mode, the optimizer can automatically stage optimizations in-place so you can benchmark framerates and memory in real-time, cleanly reverting temporary changes upon returning to Edit Mode.
- **Smart Asset Protection:** Automatically recognizes and protects delicate lighting assets (Bakery lightmaps, directional maps, volumes) and Mochie shader lookup maps, ensuring visual fidelity while optimizing everything else around them.

## How to Use

1. **Open the Optimizer Window:**
   - In the Unity menu bar, navigate to **Window** → **Synthos** → **Syn Scene Optimizer**.

2. **Select & Configure Passes:**
   - In the **Optimizers** tab, enable or disable individual passes according to your world's needs.
   - Tune resolution caps (PC vs. Android/Quest), complexity thresholds, decimation targets, and particle clamping limits.
   - Use the **General Fixes** tab to toggle utilities like the **Bakery Auto Reflection Probe Baker** (and manual probe bakes) alongside material slot trimming.

3. **Protect Specific Assets (Optional):**
   - Open **Window** → **Synthos** → **Protected Objects** (or use the drag-and-drop boxes in the optimizer window) to protect critical GameObjects, textures, or materials from being altered.
   - For mesh decimation, use the **Skip List** or **Preservation List** within the Mesh Simplifier settings to preserve hero assets.

4. **Inspect Scene Memory (Optional):**
   - Click **Open VRAM Analyzer** in the main window (or navigate to **Window** → **Synthos** → **VRAM Analyzer**) to get an interactive, real-time diagnostic breakdown of your scene's texture, mesh, and material memory usage before and after optimizing.

5. **Build or Test Your World:**
   - Simply build and upload your world using the **VRChat SDK Control Panel** (or enter **Play Mode** in Unity).
   - Syn Scene Optimizer executes automatically in the background, logs detailed memory savings to the Unity Console, and packages the optimized build.

6. **Manage Cache:**
   - View your active platform cache statistics directly in the main window.
   - Use **Clear PC Cache**, **Clear Android Cache**, or **Clear All Platforms** anytime you want to clear cached assets for a fresh build.

## Requirements

- **Unity 2022.3** (VRChat world development environment)
- **VRChat World SDK (SDK3)**
- **Mochie Shaders:** This tool is designed primarily around [Mochie's Unity Shaders](https://github.com/Mochies-Code/Mochies-Unity-Shaders). Its material analysis, texture property mapping, packed map detection, and mobile shader fallback passes are deeply tailored for Mochie shaders (e.g., Mochie Standard and Standard Lite). While Unity Standard shaders are supported, worlds using Mochie shaders will receive the highest compatibility and optimization efficiency.
- **Bakery - GPU Lightmapper (Optional):** Only required if using the **Auto Reflection Probe Baker** pass. If Bakery is not installed in your project, all other optimizer features continue to function normally, and the pass provides a Unity Lightmapping fallback.

## Installation via VPM (VRChat Creator Companion)

Add the Synthos package repository:
```
https://grimreaper35487.github.io/Synthos-VRC-Packages/index.json
```
Then search for **Synthos Scene Optimizer** in the Creator Companion or ALCOM and click **Install** (it will automatically install **Meshia Mesh Simplification (Synthos Edition)** as a dependency).

## Credits & Acknowledgments

- **Texture Compression Algorithm:** Adapted from and inspired by [avatar-compressor](https://github.com/Limitex/avatar-compressor) by **Limitex** (MIT License).
- **Mesh Decimation Engine:** Powered by [Meshia](https://github.com/RamType0/Meshia.MeshSimplification) by **Ram.Type-0** (MIT License).

