# Synthos Scene Optimizer

> [!WARNING]
> **Experimental:** This tool is experimental. While it has been tested as thoroughly as possible, due to the nature of Unity and diverse scene configurations, some scenes might misbehave. Always make sure to **back up your project and scenes** before running optimizations just to be safe.
>
> **Note on Non-Destructive Design:** Most of the tool is **non-destructive**—heavy operations like texture downscaling, mesh simplification, and material optimization generate isolated assets inside a cache folder (`Assets/SynSceneOptimizer_Cache/`) rather than modifying your original project files. However, certain passes directly modify scene components or project asset import settings on disk (highlighted with ⚠️ below).

Comprehensive VRChat scene and world optimization suite for Unity. Features automatic texture VRAM reduction, mesh decimation & compression, GPU instancing, audio & particle optimization, and mirror reflection mask tuning.

## Features

- **Smart Texture VRAM Optimizer** *(Non-Destructive)*: Analyzes texture entropy, visual detail, and screen footprint to reduce unnecessary VRAM usage without perceptible loss in quality. Generated downscaled textures are stored in an isolated cache (`Assets/SynSceneOptimizer_Cache/`); original texture assets are never overwritten.
- **Mesh Simplifier** *(Non-Destructive)*: High-performance mesh decimation powered by Meshia with configurable thresholds, scene triangle caps, preservation lists, and skip lists. Generates simplified mesh copies in the cache without altering original model files.
- **Mesh VRAM Optimizer** *(Non-Destructive)*: Reduces vertex data overhead, compacts UV3 channels, and strips unused vertex attributes into cached mesh copies.
- **Mesh Deduplicator** *(Non-Destructive to Assets)*: Detects identical meshes and unifies scene renderer references to save memory; does not delete or alter original mesh asset files on disk.
- **GPU Instancing Enabler** *(Non-Destructive)*: Automatically flags compatible materials for hardware GPU instancing via virtual staging and cached material duplicates.
- **Audio Optimizer** *(⚠️ Destructive — Modifies Project Asset Importers)*: Audits AudioSources and clips for optimal compression, mono channels, and load types. **Directly modifies and re-imports AudioClip asset import settings on disk** (e.g., Vorbis, ADPCM, load type).
- **Particle System Optimizer** *(⚠️ Destructive — Modifies Scene Components)*: Directly modifies `ParticleSystem` component properties (culling modes and max particle capacity clamps) on GameObjects within your scene.
- **Mirror Layer Mask Optimizer** *(⚠️ Destructive — Modifies Scene Components)*: Directly modifies reflection layer culling masks on VRChat Mirror components in your scene.
- **Structural Cleanup Passes** *(⚠️ Destructive — Modifies Scene Components)*: Optional passes (Material Slot Trimmer, Reflection & Light Probe Optimizers) that trim empty material slots on renderers and turn off probes on matte/baked objects in the scene.
- **VRAM Analyzer Window** *(Read-Only)*: Live diagnostics tracking texture, mesh, and material memory footprint without modifying any assets or scenes.
- **Automatic Legacy Migration**: Seamlessly moves legacy cache and settings from earlier versions (`Assets/SynSceneOpti_v2` / `Assets/SynSceneOptimiser`) into modern project locations without breaking asset GUIDs.

## Installation via VPM (VRChat Creator Companion)

Add the Synthos package repository:
```
https://grimreaper35487.github.io/Synthos-VRC-Packages/index.json
```
Then search for **Synthos Scene Optimizer** in the Creator Companion or ALCOM and click **Install** (it will automatically install **Meshia Mesh Simplification (Synthos Edition)** as a dependency).

## Credits & Acknowledgments

- **Texture Compression Algorithm:** Adapted from and inspired by [avatar-compressor](https://github.com/Limitex/avatar-compressor) by **Limitex** (MIT License).
- **Mesh Decimation Engine:** Powered by [Meshia](https://github.com/RamType0/Meshia.MeshSimplification) by **Ram.Type-0** (MIT License).

