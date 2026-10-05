# Synthos Scene Optimizer

Comprehensive VRChat scene and world optimization suite for Unity. Features automatic texture VRAM reduction, mesh decimation & compression, GPU instancing, audio & particle optimization, and mirror reflection mask tuning.

## Features

- **Smart Texture VRAM Optimizer:** Analyzes texture entropy, visual detail, and screen footprint to reduce unnecessary VRAM usage without perceptible loss in quality.
- **Mesh Simplifier:** High-performance mesh decimation powered by Meshia with configurable thresholds, scene triangle caps, preservation lists, and skip lists.
- **Mesh VRAM Optimizer:** Reduces vertex data overhead and strips unnecessary vertex channels.
- **Mesh Deduplicator:** Detects identical meshes and unifies them to save memory.
- **GPU Instancing Enabler:** Automatically flags compatible materials for hardware GPU instancing.
- **Audio Optimizer:** Audits AudioSources and clips for optimal compression, mono channels, and load types.
- **Particle System Optimizer:** Caps max particle emissions and tunes prewarm settings.
- **Mirror Layer Mask Optimizer:** Ensures VRChat mirrors don't redundantly draw heavy layers.
- **VRAM Analyzer Window:** Live diagnostics tracking texture, mesh, and material memory footprint.
- **Automatic Legacy Migration:** Seamlessly moves legacy cache and settings from earlier versions (`Assets/SynSceneOpti_v2` / `Assets/SynSceneOptimiser`) into modern project locations without breaking asset GUIDs.

## Installation via VPM (VRChat Creator Companion)

Add the Synthos package repository:
```
https://grimreaper35487.github.io/Synthos-VRC-Packages/index.json
```
Then search for **Synthos Scene Optimizer** in the Creator Companion or ALCOM and click **Install**.
