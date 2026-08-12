// SPDX-License-Identifier: MIT

using UnityEngine;

namespace GaussianSplatting.Runtime
{
    // Runtime-readable settings for GaussianSplatRenderSystem's budgeted-load scheduler
    // (see GaussianSplatRenderer.cs). Unlike GaussianSplatProjectSettings (Editor-only
    // ScriptableSingleton, used for the Commit-to-Disk backup folder), this needs to be
    // readable in actual builds — GaussianSplatRenderSystem.Tick()/CheckBudget() run on
    // Quest, not just in the Editor — so it's a plain asset loaded via Resources.Load
    // instead. Edited from the same "Gaussian Splat" Project Settings page
    // (Editor/GaussianSplatting/GaussianSplatProjectSettings.cs) for a single settings UI.
    public class GaussianSplatSchedulerSettings : ScriptableObject
    {
        public const string kResourcesPath = "GaussianSplatSchedulerSettings";
        public const string kAssetFolder = "Assets/Resources";
        public const string kAssetPath = kAssetFolder + "/" + kResourcesPath + ".asset";

        [Tooltip("Time budget per frame (ms) for the deferred/budgeted resource load, used when a mask is assigned and m_MaskT is 0 at OnEnable. IMPORTANT: this only caps the CHEAP, non-blocking work measured BETWEEN GPU/driver calls (e.g. CPU-side data copies) — it cannot interrupt a single call already in progress. A call whose own internal cost exceeds this budget (e.g. a GraphicsBuffer.SetData/GetData sync, or a driver-level ComputeShader.IsSupported() query) will still take its full real cost in one frame regardless of this setting. Lower this to reduce non-blocking overhead between calls; to reduce the blocking calls themselves, look at eliminating/caching the calls (e.g. GpuSorting's DeviceRadixSort/FidelityFxSort construction cache), not this budget.")]
        public float budgetedLoadFrameTimeMs = 2f;

        [Tooltip("Maximum number of GaussianSplatRenderers allowed to be mid-budgeted-load simultaneously. The scheduler starts at most one new renderer per frame, and only while fewer than this many are already loading.")]
        public int budgetedLoadConcurrentMax = 3;

        static GaussianSplatSchedulerSettings s_Instance;

        public static GaussianSplatSchedulerSettings instance
        {
            get
            {
                if (s_Instance != null)
                    return s_Instance;

                s_Instance = Resources.Load<GaussianSplatSchedulerSettings>(kResourcesPath);
                if (s_Instance == null)
                {
                    // No asset authored yet (fresh project/package install) — fall back to
                    // an in-memory instance with the field defaults above so the scheduler
                    // still works without requiring the settings page to be opened first.
                    s_Instance = CreateInstance<GaussianSplatSchedulerSettings>();
                }

                return s_Instance;
            }
        }
    }
}
