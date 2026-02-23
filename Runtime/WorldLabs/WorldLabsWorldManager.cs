// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using GaussianSplatting.Runtime;
using UnityEngine;
using UnityEngine.Events;
using WorldLabs.API;

namespace WorldLabs.Runtime
{
    /// <summary>
    /// Runtime orchestrator for browsing and loading WorldLabs Gaussian Splat worlds.
    /// Handles API queries, SPZ download, GPU processing, and GaussianSplatRenderer spawning.
    /// Safe to use in builds — no Editor/AssetDatabase dependencies.
    /// </summary>
    public class WorldLabsWorldManager : MonoBehaviour
    {
        // ── Quality preset ────────────────────────────────────────────────────

        public enum SplatQuality
        {
            VeryHigh,   // Float32 pos/scale/color/SH   — maximum fidelity, highest VRAM
            High,       // Norm11 pos/scale, Float16x4 color, Float16 SH
            Medium,     // Norm11 pos/scale, Norm8x4 color, Norm6 SH   (default)
            Low,        // Norm6  pos/scale, Norm8x4 color, Cluster64k SH
            VeryLow,    // Norm6  pos/scale, BC7 color, Cluster4k SH   — lowest VRAM
        }

        public enum SplatResolution
        {
            FullRes,   // "full_res" — maximum detail
            _500k,     // "500k"    — balanced (default)
            _100k,     // "100k"    — lightest, fastest download
        }

        // ── Inspector ─────────────────────────────────────────────────────────

        // Shaders are auto-assigned at reset/awake — no manual drag-drop needed.
        [HideInInspector] public Shader splatShader;
        [HideInInspector] public Shader compositeShader;
        [HideInInspector] public Shader debugPointsShader;
        [HideInInspector] public Shader debugBoxesShader;
        [HideInInspector] public ComputeShader splatUtilitiesDeviceRadix;
        [HideInInspector] public ComputeShader splatUtilitiesFidelityFX;

        [Header("Loading")]
        public SplatQuality quality = SplatQuality.Medium;
        [Tooltip("SPZ resolution to request. Falls back to best available if the chosen resolution is absent.")]
        public SplatResolution preferredResolution = SplatResolution._500k;
        [Tooltip("Parent transform for spawned world GameObjects. Uses this transform if null.")]
        public Transform worldParent;

        [Header("Default Asset")]
        [Tooltip("Optional GaussianSplatAsset to display immediately on Start, without an API call.")]
        public GaussianSplatAsset defaultAsset;
        [Tooltip("Apply a -180° X rotation to the default asset. WorldLabs worlds typically require this.")]
        public bool defaultAssetInverted = true;

        // API key is always read from the .env file (via EnvLoader / StreamingAssets in builds).

        // ── UnityEvents (Inspector-wirable) ───────────────────────────────────

        [Serializable] public class WorldsListedEvent : UnityEvent<List<World>> { }
        [Serializable] public class WorldLoadedEvent : UnityEvent<string, GaussianSplatRenderer> { }
        [Serializable] public class WorldLoadFailedEvent : UnityEvent<string, string> { }
        [Serializable] public class WorldProgressEvent : UnityEvent<string, float> { }

        public WorldsListedEvent onWorldsListed;
        public WorldLoadedEvent  onWorldLoaded;
        public WorldLoadFailedEvent onWorldLoadFailed;
        public WorldProgressEvent onWorldLoadProgress;

        // ── C# events (code-only subscribers) ────────────────────────────────

        public event Action<List<World>>                 OnWorldsListed;
        public event Action<string>                      OnWorldLoadStarted;
        public event Action<string, float>               OnWorldLoadProgress;
        public event Action<string, GaussianSplatRenderer> OnWorldLoaded;
        public event Action<string, string>              OnWorldLoadFailed;
        public event Action<string>                      OnWorldUnloaded;

        // ── Internal state ────────────────────────────────────────────────────

        WorldLabsClient _client;
        WorldLabsClient Client => _client ??= new WorldLabsClient();

        readonly Dictionary<string, GaussianSplatRenderer> _loadedWorlds = new();
        readonly HashSet<string> _loadingWorlds = new();
        List<World> _cachedWorlds = new();

        // ── Properties ────────────────────────────────────────────────────────

        public IReadOnlyList<World> CachedWorlds      => _cachedWorlds;
        public bool IsWorldLoaded(string worldId)     => _loadedWorlds.ContainsKey(worldId);
        public bool IsWorldLoading(string worldId)    => _loadingWorlds.Contains(worldId);
        public IReadOnlyCollection<string> LoadedWorldIds => _loadedWorlds.Keys;
        /// <summary>The next-page token from the most recent <see cref="ListWorldsAsync"/> call. Null when no more pages.</summary>
        public string LastNextPageToken { get; private set; }

        // ── Lifecycle ─────────────────────────────────────────────────────────

        void Awake()
        {
            if (worldParent == null)
                worldParent = transform;
            EnsureShaders();
        }

        void Start()
        {
            if (defaultAsset != null)
                LoadDefaultAsset();
        }

        void OnDestroy()
        {
            UnloadAllWorlds();
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Fetch a page of worlds from the API.
        /// Fires <see cref="OnWorldsListed"/> / <see cref="onWorldsListed"/> on success.
        /// </summary>
        public async Task<List<World>> ListWorldsAsync(
            string pageToken = null,
            int pageSize = 20,
            WorldStatus? status = WorldStatus.SUCCEEDED,
            bool? isPublic = null)
        {
            try
            {
                var response = await Client.ListWorldsAsync(
                    pageSize:   pageSize,
                    pageToken:  pageToken,
                    status:     status,
                    isPublic:   isPublic);

                _cachedWorlds = response.worlds ?? new List<World>();
                LastNextPageToken = response.next_page_token;
                OnWorldsListed?.Invoke(_cachedWorlds);
                onWorldsListed?.Invoke(_cachedWorlds);
                return _cachedWorlds;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[WorldLabsWorldManager] ListWorldsAsync failed: {ex.Message}");
                return _cachedWorlds;
            }
        }

        /// <summary>
        /// Download, process, and render a world at runtime.
        /// Returns the spawned <see cref="GaussianSplatRenderer"/>, or null on failure.
        /// Already-loaded worlds are returned immediately without re-downloading.
        /// </summary>
        public async Task<GaussianSplatRenderer> LoadWorldAsync(World world)
        {
            if (world == null) throw new ArgumentNullException(nameof(world));
            string worldId = world.world_id;

            // Return existing renderer immediately
            if (_loadedWorlds.TryGetValue(worldId, out var existing))
                return existing;

            if (_loadingWorlds.Contains(worldId))
            {
                Debug.LogWarning($"[WorldLabsWorldManager] World {worldId} is already loading.");
                return null;
            }

            // Unload non-default worlds only; keep the default asset visible
            // as a placeholder while the new world is downloading and processing.
            foreach (var id in new List<string>(_loadedWorlds.Keys))
                if (id != "__default__")
                    UnloadWorld(id);

            _loadingWorlds.Add(worldId);
            OnWorldLoadStarted?.Invoke(worldId);

            try
            {
                // ── 1. Resolve SPZ URL ────────────────────────────────────────
                string resKey = preferredResolution switch
                {
                    SplatResolution.FullRes => "full_res",
                    SplatResolution._100k   => "100k",
                    _                       => "500k",
                };
                string spzUrl = world.assets?.splats?.GetUrl(resKey)
                             ?? world.assets?.splats?.GetBestResolutionUrl();
                if (string.IsNullOrEmpty(spzUrl))
                    throw new Exception("No SPZ URL found in world assets.");

                // ── 2. Download ───────────────────────────────────────────────
                ReportProgress(worldId, 0.05f);
                byte[] spzBytes = await WorldLabsClientExtensions.DownloadBinaryAsync(spzUrl);
                ReportProgress(worldId, 0.35f);

                // ── 3. Process on a background thread ─────────────────────────
                var (posFormat, scaleFormat, colorFormat, shFormat) = GetFormats(quality);
                RuntimeSplatData data = null;

                await Task.Run(() =>
                {
                    data = RuntimeSplatProcessing.ProcessSPZBytes(
                        spzBytes,
                        posFormat, scaleFormat, colorFormat, shFormat);
                });

                data.worldId    = worldId;
                data.worldName  = world.display_name;
                data.thumbnailUrl = world.assets?.thumbnail_url;
                ReportProgress(worldId, 0.90f);

                // ── 4. Spawn renderer on the main thread ──────────────────────
                var go = new GameObject($"World_{world.display_name ?? worldId}");
                go.transform.SetParent(worldParent, false);
                go.transform.localRotation = Quaternion.Euler(-180f, 0f, 0f);

                var renderer = go.AddComponent<GaussianSplatRenderer>();
                AssignShaders(renderer);
                renderer.LoadFromRuntimeData(data);

                _loadedWorlds[worldId] = renderer;

                // Dismiss the default placeholder now that the real world is rendering.
                if (_loadedWorlds.ContainsKey("__default__"))
                    UnloadWorld("__default__");

                ReportProgress(worldId, 1.0f);

                OnWorldLoaded?.Invoke(worldId, renderer);
                onWorldLoaded?.Invoke(worldId, renderer);
                return renderer;
            }
            catch (Exception ex)
            {
                string msg = ex.Message;
                Debug.LogError($"[WorldLabsWorldManager] LoadWorldAsync failed for '{worldId}': {msg}");
                OnWorldLoadFailed?.Invoke(worldId, msg);
                onWorldLoadFailed?.Invoke(worldId, msg);
                return null;
            }
            finally
            {
                _loadingWorlds.Remove(worldId);
            }
        }

        /// <summary>Destroy the renderer and free the slot for a world.</summary>
        public void UnloadWorld(string worldId)
        {
            if (!_loadedWorlds.TryGetValue(worldId, out var renderer)) return;
            _loadedWorlds.Remove(worldId);
            if (renderer != null)
                Destroy(renderer.gameObject);
            OnWorldUnloaded?.Invoke(worldId);
        }

        /// <summary>Destroy all loaded worlds.</summary>
        public void UnloadAllWorlds()
        {
            foreach (var id in new List<string>(_loadedWorlds.Keys))
                UnloadWorld(id);
        }

        /// <summary>
        /// Unloads all real worlds and brings back the default asset placeholder.
        /// Call this from an "Unload" button so the user returns to the default view.
        /// </summary>
        public void RestoreDefaultWorld()
        {
            foreach (var id in new List<string>(_loadedWorlds.Keys))
                if (id != "__default__")
                    UnloadWorld(id);

            if (defaultAsset != null && !_loadedWorlds.ContainsKey("__default__"))
                LoadDefaultAsset();
        }

        // ── Private helpers ───────────────────────────────────────────────────

        void LoadDefaultAsset()
        {
            const string id = "__default__";

            var go = new GameObject($"World_{defaultAsset.name}");
            go.transform.SetParent(worldParent, false);
            go.transform.localRotation = defaultAssetInverted ? Quaternion.Euler(-180f, 0f, 0f) : Quaternion.identity;

            var renderer = go.AddComponent<GaussianSplatRenderer>();
            AssignShaders(renderer);
            renderer.m_Asset = defaultAsset;

            _loadedWorlds[id] = renderer;

            OnWorldLoaded?.Invoke(id, renderer);
            onWorldLoaded?.Invoke(id, renderer);
        }

        void ReportProgress(string worldId, float progress)
        {
            OnWorldLoadProgress?.Invoke(worldId, progress);
            onWorldLoadProgress?.Invoke(worldId, progress);
        }

        void AssignShaders(GaussianSplatRenderer r)
        {
            r.m_ShaderSplats                     = splatShader;
            r.m_ShaderComposite                  = compositeShader;
            r.m_ShaderDebugPoints                = debugPointsShader;
            r.m_ShaderDebugBoxes                 = debugBoxesShader;
            r.m_CSSplatUtilities_deviceRadixSort = splatUtilitiesDeviceRadix;
            r.m_CSSplatUtilities_fidelityFX      = splatUtilitiesFidelityFX;
        }

        /// <summary>
        /// Fills any missing shader/compute references using Shader.Find() (runtime-safe).
        /// Compute shaders cannot be found at runtime — they must be serialized (set via
        /// Reset() in the Editor or manually assigned in the Inspector).
        /// </summary>
        void EnsureShaders()
        {
            if (splatShader == null)
                splatShader = Shader.Find("Gaussian Splatting/Render Splats");
            if (compositeShader == null)
                compositeShader = Shader.Find("Hidden/Gaussian Splatting/Composite");
            if (debugPointsShader == null)
                debugPointsShader = Shader.Find("Gaussian Splatting/Debug/Render Points");
            if (debugBoxesShader == null)
                debugBoxesShader = Shader.Find("Gaussian Splatting/Debug/Render Boxes");
        }

#if UNITY_EDITOR
        /// <summary>
        /// Auto-assigns all shader and compute shader fields from the package path.
        /// Called automatically when the component is first added or Reset in the Editor.
        /// </summary>
        void Reset()
        {
            const string root = "Packages/com.worldlabs.gaussian-splatting/Shaders/";
            splatShader               = UnityEditor.AssetDatabase.LoadAssetAtPath<Shader>(root + "RenderGaussianSplats.shader");
            compositeShader           = UnityEditor.AssetDatabase.LoadAssetAtPath<Shader>(root + "GaussianComposite.shader");
            debugPointsShader         = UnityEditor.AssetDatabase.LoadAssetAtPath<Shader>(root + "GaussianDebugRenderPoints.shader");
            debugBoxesShader          = UnityEditor.AssetDatabase.LoadAssetAtPath<Shader>(root + "GaussianDebugRenderBoxes.shader");
            splatUtilitiesDeviceRadix = UnityEditor.AssetDatabase.LoadAssetAtPath<ComputeShader>(root + "SplatUtilities_DeviceRadixSort.compute");
            splatUtilitiesFidelityFX  = UnityEditor.AssetDatabase.LoadAssetAtPath<ComputeShader>(root + "SplatUtilities_FidelityFX.compute");
        }
#endif

        static (GaussianSplatAsset.VectorFormat pos,
                GaussianSplatAsset.VectorFormat scale,
                GaussianSplatAsset.ColorFormat  color,
                GaussianSplatAsset.SHFormat     sh)
            GetFormats(SplatQuality q) => q switch
        {
            SplatQuality.VeryHigh => (GaussianSplatAsset.VectorFormat.Float32,
                                      GaussianSplatAsset.VectorFormat.Float32,
                                      GaussianSplatAsset.ColorFormat.Float32x4,
                                      GaussianSplatAsset.SHFormat.Float32),

            SplatQuality.High     => (GaussianSplatAsset.VectorFormat.Norm11,
                                      GaussianSplatAsset.VectorFormat.Norm11,
                                      GaussianSplatAsset.ColorFormat.Float16x4,
                                      GaussianSplatAsset.SHFormat.Float16),

            SplatQuality.Low      => (GaussianSplatAsset.VectorFormat.Norm6,
                                      GaussianSplatAsset.VectorFormat.Norm6,
                                      GaussianSplatAsset.ColorFormat.Norm8x4,
                                      GaussianSplatAsset.SHFormat.Cluster64k),

            SplatQuality.VeryLow  => (GaussianSplatAsset.VectorFormat.Norm6,
                                      GaussianSplatAsset.VectorFormat.Norm6,
                                      GaussianSplatAsset.ColorFormat.BC7,
                                      GaussianSplatAsset.SHFormat.Cluster4k),

            _ /* Medium */        => (GaussianSplatAsset.VectorFormat.Norm11,
                                      GaussianSplatAsset.VectorFormat.Norm11,
                                      GaussianSplatAsset.ColorFormat.Norm8x4,
                                      GaussianSplatAsset.SHFormat.Norm6),
        };
    }
}
