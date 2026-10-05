using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Synthos.SynSceneOptimizer
{
    /// <summary>
    /// Optimization pass that clamps ParticleSystem max capacities based on emission rates
    /// and forces their culling mode to Pause when off-screen.
    /// </summary>
    public class SynParticleSystemOptimizerPass : SynOptimizationPass
    {
        public override string Id => "synthos.particle_system_optimizer";
        public override string Name => "Particle System Safety Clamping";
        public override string Description => "Clamps ParticleSystem maximum capacities based on emission rates and forces culling to Pause when off-screen to save CPU/GPU simulation time.";

        public override int Priority => 25; // Run early-mid
        public override string Category => "Effects";

        public override void Execute(Scene scene, List<Renderer> renderers)
        {
            // Fetch settings
            bool forceCulling = SynSceneOptimizerSettings.GetBool("Particles_ForceCulling", true);
            int targetCullingModeVal = SynSceneOptimizerSettings.GetInt("Particles_CullingMode", (int)ParticleSystemCullingMode.Pause);
            var targetCullingMode = (ParticleSystemCullingMode)targetCullingModeVal;
            
            bool clampCapacity = SynSceneOptimizerSettings.GetBool("Particles_ClampCapacity", true);
            float safetyMultiplier = SynSceneOptimizerSettings.GetFloat("Particles_SafetyMultiplier", 1.5f);
            int absoluteMaxCap = SynSceneOptimizerSettings.GetInt("Particles_AbsoluteMaxCap", 200);
            bool logOptimized = SynSceneOptimizerSettings.GetBool("Particles_LogOptimized", true);
            string exclusionsInput = SynSceneOptimizerSettings.GetString("Particles_Exclusions", "");

            // Process exclusions list
            var exclusions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(exclusionsInput))
            {
                var split = exclusionsInput.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var s in split)
                {
                    exclusions.Add(s.Trim());
                }
            }

            int totalSystems = 0;
            int optimizedCullingCount = 0;
            int clampedCapacityCount = 0;

            var rootObjects = scene.GetRootGameObjects();
            var particleSystems = new List<ParticleSystem>();

            foreach (var root in rootObjects)
            {
                if (root != null)
                {
                    particleSystems.AddRange(root.GetComponentsInChildren<ParticleSystem>(true));
                }
            }

            foreach (var ps in particleSystems)
            {
                if (ps == null) continue;
                totalSystems++;

                // Skip if excluded
                if (exclusions.Contains(ps.name) || exclusions.Contains(ps.gameObject.name) || SynProtectionData.IsProtected(ps.gameObject))
                {
                    continue;
                }

                var main = ps.main;
                var emission = ps.emission;

                bool modified = false;
                string logDetails = "";

                // A. Force selected culling mode
                if (forceCulling && main.cullingMode != targetCullingMode)
                {
                    var oldMode = main.cullingMode;
                    main.cullingMode = targetCullingMode;
                    optimizedCullingCount++;
                    modified = true;
                    logDetails += $"Culling: {oldMode} -> {targetCullingMode}. ";
                }

                // B. Clamp max particles capacity based on emission settings
                if (clampCapacity)
                {
                    // Calculate max simultaneous particles under normal operation:
                    // (Emission rate over time * start lifetime) + sum of bursts
                    float rate = emission.rateOverTime.constantMax;
                    float lifetime = main.startLifetime.constantMax;

                    float burstTotal = 0f;
                    if (emission.burstCount > 0)
                    {
                        var bursts = new ParticleSystem.Burst[emission.burstCount];
                        emission.GetBursts(bursts);
                        foreach (var b in bursts)
                        {
                            burstTotal += b.maxCount;
                        }
                    }

                    // Formula: (Rate * Lifetime * Safety Multiplier) + Burst Counts
                    float calculatedNeeded = (rate * lifetime * safetyMultiplier) + burstTotal;
                    
                    // Add a baseline minimum so we don't clamp to 0 if rate is 0 (e.g. custom script-triggered particles)
                    int targetCapacity = Mathf.Max(15, Mathf.CeilToInt(calculatedNeeded));

                    // Apply the absolute maximum cap configured by the user
                    targetCapacity = Mathf.Min(targetCapacity, absoluteMaxCap);

                    // Only clamp down, never increase
                    if (main.maxParticles > targetCapacity)
                    {
                        int oldCap = main.maxParticles;
                        main.maxParticles = targetCapacity;
                        clampedCapacityCount++;
                        modified = true;
                        logDetails += $"MaxParticles: {oldCap} -> {targetCapacity} (Calculated: {calculatedNeeded:F1}). ";
                    }
                }

                if (modified && logOptimized)
                {
                    Debug.Log($"[ParticleOptimizer] Optimized <b>{ps.name}</b>. {logDetails}");
                }
            }

            if (optimizedCullingCount > 0 || clampedCapacityCount > 0)
            {
                SynPipelineCompactor.LogChange(
                    "Particle System Safety Clamping",
                    string.Format("Forced offscreen culling on {0} systems. Clamped max capacities on {1} systems.", 
                        optimizedCullingCount, clampedCapacityCount)
                );
            }
        }

        public override void DrawGUI(SynSceneOptimizerSettings settings)
        {
            bool forceCull = SynSceneOptimizerSettings.GetBool("Particles_ForceCulling", true);
            bool newForceCull = EditorGUILayout.Toggle(new GUIContent("Force Culling Mode", "If checked, forces the selected culling mode on all non-excluded particle systems in the scene."), forceCull);
            if (newForceCull != forceCull)
            {
                SynSceneOptimizerSettings.SetBool("Particles_ForceCulling", newForceCull);
            }

            if (forceCull)
            {
                EditorGUI.indentLevel++;
                int currentCullingMode = SynSceneOptimizerSettings.GetInt("Particles_CullingMode", (int)ParticleSystemCullingMode.Pause);
                var newCullingMode = (ParticleSystemCullingMode)EditorGUILayout.EnumPopup(new GUIContent("Target Culling Mode", "The culling mode to apply when the system goes off-screen. 'Pause' is highly recommended for performance."), (ParticleSystemCullingMode)currentCullingMode);
                if ((int)newCullingMode != currentCullingMode)
                {
                    SynSceneOptimizerSettings.SetInt("Particles_CullingMode", (int)newCullingMode);
                }
                EditorGUI.indentLevel--;
            }

            bool clampCap = SynSceneOptimizerSettings.GetBool("Particles_ClampCapacity", true);
            bool newClampCap = EditorGUILayout.Toggle(new GUIContent("Clamp Max Particles", "If checked, automatically lowers the 'Max Particles' field to match its real emission rate + lifetime values."), clampCap);
            if (newClampCap != clampCap)
            {
                SynSceneOptimizerSettings.SetBool("Particles_ClampCapacity", newClampCap);
            }

            if (clampCap)
            {
                EditorGUI.indentLevel++;
                float safety = SynSceneOptimizerSettings.GetFloat("Particles_SafetyMultiplier", 1.5f);
                float newSafety = EditorGUILayout.Slider(new GUIContent("Safety Multiplier", "Safety factor applied to calculated active particle count before clamping."), safety, 1.0f, 3.0f);
                if (!Mathf.Approximately(newSafety, safety))
                {
                    SynSceneOptimizerSettings.SetFloat("Particles_SafetyMultiplier", newSafety);
                }

                int absMax = SynSceneOptimizerSettings.GetInt("Particles_AbsoluteMaxCap", 200);
                int newAbsMax = EditorGUILayout.IntField(new GUIContent("Global Capacity Cap", "No particle system will be allowed to have a maximum capacity larger than this (unless excluded)."), absMax);
                if (newAbsMax != absMax)
                {
                    SynSceneOptimizerSettings.SetInt("Particles_AbsoluteMaxCap", Mathf.Max(10, newAbsMax));
                }
                EditorGUI.indentLevel--;
            }

            bool logOpt = SynSceneOptimizerSettings.GetBool("Particles_LogOptimized", true);
            bool newLogOpt = EditorGUILayout.Toggle(new GUIContent("Log Optimized Systems", "If checked, prints details about which particle systems were modified to the console."), logOpt);
            if (newLogOpt != logOpt)
            {
                SynSceneOptimizerSettings.SetBool("Particles_LogOptimized", newLogOpt);
            }

            string exclusions = SynSceneOptimizerSettings.GetString("Particles_Exclusions", "");
            string newExclusions = EditorGUILayout.TextField(new GUIContent("Exclusions (CSV)", "Comma-separated list of ParticleSystem GameObject names to ignore."), exclusions);
            if (newExclusions != exclusions)
            {
                SynSceneOptimizerSettings.SetString("Particles_Exclusions", newExclusions);
            }
        }
    }
}
