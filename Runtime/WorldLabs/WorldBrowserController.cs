// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using WorldLabs.API;

namespace WorldLabs.Runtime
{
    /// <summary>WorldLabs model variant to use for runtime world generation.</summary>
    public enum GenerationModel { Plus, Mini }

    /// <summary>
    /// World-space UI that lists WorldLabs worlds as clickable panorama cards.
    /// Tap a card to load the world; tap again to unload.
    /// Auto-builds a World Space Canvas hierarchy on Awake if nothing is pre-wired.
    /// </summary>
    [RequireComponent(typeof(WorldLabsWorldManager))]
    public class WorldBrowserController : MonoBehaviour
    {
        // ── Inspector ─────────────────────────────────────────────────────────

        [Header("Manager (auto-resolved if null)")]
        public WorldLabsWorldManager worldManager;

        [Header("UI — leave null to auto-create")]
        public Canvas      browserCanvas;
        public ScrollRect  worldScrollRect;
        public RectTransform worldListContent;
        public Button      prevButton;
        public Button      nextButton;
        public Text        pageLabel;
        public Text        statusText;
        [Tooltip("Optional external button that unloads the current world and restores the default asset.")]
        public Button      unloadButton;

        [Header("World Creation")]
        [Tooltip("Model to use when generating new worlds from a text prompt.\nPlus = higher quality / slower.  Mini = faster generation.")]
        public GenerationModel creationModel = GenerationModel.Plus;

        [Header("Create UI — leave null to auto-create")]
        [Tooltip("Root panel shown when in create mode (auto-created if null).")]
        public GameObject createPanel;
        [Tooltip("InputField for the generation prompt (auto-created if null).")]
        public InputField  promptInputField;
        [Tooltip("Button that triggers world generation (auto-created if null).")]
        public Button      createWorldButton;
        [Tooltip("Header button that toggles between browse and create modes (auto-created if null).")]
        public Button      createToggleButton;

        [Header("Layout")]
        [Tooltip("Canvas size in pixels (world units = pixels × 0.005).")]
        public Vector2 canvasPixelSize = new Vector2(420, 600);
        [Tooltip("Columns in the world card grid.")]
        public int columns = 2;
        [Tooltip("Total card height in pixels (image + name bar).")]
        public float cardHeight = 182f;
        [Tooltip("Height of the panorama image portion of each card.")]
        public float cardImageHeight = 148f;

        // ── State ─────────────────────────────────────────────────────────────

        readonly List<WorldCardUI> _pool    = new();
        readonly Stack<string>     _history = new();   // page-token history for Back

        string _currentToken;
        string _nextToken;
        bool   _loading;

        // ── Create-world state ────────────────────────────────────────────────
        WorldLabsClient _wlClient;
        bool            _createPanelOpen;
        bool            _isGenerating;
        bool            _generationCancelled;

        // ── Lifecycle ─────────────────────────────────────────────────────────

        void Awake()
        {
            if (worldManager == null)
                worldManager = GetComponent<WorldLabsWorldManager>();

            if (browserCanvas == null)
                BuildUI();

            prevButton.onClick.AddListener(OnPrevPage);
            nextButton.onClick.AddListener(OnNextPage);

            if (unloadButton != null)
                unloadButton.onClick.AddListener(OnUnloadCurrentWorld);

            if (createToggleButton != null)
                createToggleButton.onClick.AddListener(ToggleCreateMode);
            if (createWorldButton != null)
                createWorldButton.onClick.AddListener(StartWorldCreation);
        }

        void Start()
        {
            // If the user pre-wired a plain ScrollRect, swap it for ScrollbarOnlyScrollRect
            // so that dragging the content area no longer scrolls (only the scrollbar does).
            if (worldScrollRect != null && worldScrollRect.GetType() == typeof(ScrollRect))
                worldScrollRect = SwapToScrollbarOnly(worldScrollRect);

            EnsureContentLayout();
            _started = true;
            Refresh();   // first load always happens here, after layout is ready
        }

        /// <summary>
        /// Replaces a plain ScrollRect with ScrollbarOnlyScrollRect, preserving all bindings.
        /// </summary>
        static ScrollbarOnlyScrollRect SwapToScrollbarOnly(ScrollRect old)
        {
            // Snapshot everything we need before destroying
            var go            = old.gameObject;
            var horizontal    = old.horizontal;
            var vertical      = old.vertical;
            var content       = old.content;
            var viewport      = old.viewport;
            var hBar          = old.horizontalScrollbar;
            var vBar          = old.verticalScrollbar;
            var hVis          = old.horizontalScrollbarVisibility;
            var vVis          = old.verticalScrollbarVisibility;
            var hSpacing      = old.horizontalScrollbarSpacing;
            var vSpacing      = old.verticalScrollbarSpacing;
            var movementType  = old.movementType;
            var elasticity    = old.elasticity;
            var inertia       = old.inertia;
            var decel         = old.decelerationRate;
            var sensitivity   = old.scrollSensitivity;

            UnityEngine.Object.DestroyImmediate(old);

            var neo = go.AddComponent<ScrollbarOnlyScrollRect>();
            neo.horizontal                  = horizontal;
            neo.vertical                    = vertical;
            neo.content                     = content;
            neo.viewport                    = viewport;
            neo.horizontalScrollbar         = hBar;
            neo.verticalScrollbar           = vBar;
            neo.horizontalScrollbarVisibility = hVis;
            neo.verticalScrollbarVisibility = vVis;
            neo.horizontalScrollbarSpacing  = hSpacing;
            neo.verticalScrollbarSpacing    = vSpacing;
            neo.movementType                = movementType;
            neo.elasticity                  = elasticity;
            neo.inertia                     = inertia;
            neo.decelerationRate            = decel;
            neo.scrollSensitivity           = sensitivity;
            return neo;
        }

        /// <summary>
        /// Adds GridLayoutGroup + ContentSizeFitter to worldListContent if they are missing.
        /// Called from Start() so RectTransform sizes are already calculated by Unity.
        /// Safe to call multiple times — skips if a layout group already exists.
        /// </summary>
        void EnsureContentLayout()
        {
            if (worldListContent == null) return;
            if (worldListContent.GetComponent<LayoutGroup>() != null) return;

            // Try to read the actual viewport width for accurate cell sizing.
            Canvas.ForceUpdateCanvases();
            float width = canvasPixelSize.x;
            if (worldScrollRect?.viewport != null && worldScrollRect.viewport.rect.width > 1f)
                width = worldScrollRect.viewport.rect.width;

            const float pad = 8f, gap = 6f;
            float cellW = Mathf.Max(50f, (width - pad * 2 - gap * (columns - 1)) / columns);

            // Content must be anchored to the top and grow downward.
            worldListContent.anchorMin = new Vector2(0f, 1f);
            worldListContent.anchorMax = new Vector2(1f, 1f);
            worldListContent.pivot     = new Vector2(0.5f, 1f);
            worldListContent.offsetMin = worldListContent.offsetMax = Vector2.zero;

            var glg = worldListContent.gameObject.AddComponent<GridLayoutGroup>();
            glg.constraint      = GridLayoutGroup.Constraint.FixedColumnCount;
            glg.constraintCount = columns;
            glg.cellSize        = new Vector2(cellW, cardHeight);
            glg.spacing         = new Vector2(gap, gap);
            glg.padding         = new RectOffset((int)pad, (int)pad, (int)pad, (int)pad);
            glg.startCorner     = GridLayoutGroup.Corner.UpperLeft;
            glg.startAxis       = GridLayoutGroup.Axis.Horizontal;
            glg.childAlignment  = TextAnchor.UpperLeft;

            if (worldListContent.GetComponent<ContentSizeFitter>() == null)
                worldListContent.gameObject.AddComponent<ContentSizeFitter>().verticalFit =
                    ContentSizeFitter.FitMode.PreferredSize;

            Debug.Log($"[WorldBrowserController] EnsureContentLayout: cellW={cellW:F0}, " +
                      $"cardH={cardHeight}, cols={columns}");
        }

        bool _started;

        void OnEnable()
        {
            worldManager.OnWorldLoaded       += OnWorldLoaded;
            worldManager.OnWorldLoadFailed   += OnWorldLoadFailed;
            worldManager.OnWorldLoadProgress += OnWorldProgress;
            worldManager.OnWorldUnloaded     += OnWorldUnloadedHandler;
            // Don't Refresh() yet on the very first enable — Start() handles it after
            // EnsureContentLayout() so cards always have a GridLayoutGroup to land in.
            if (_started) Refresh();
        }

        void OnDisable()
        {
            worldManager.OnWorldLoaded       -= OnWorldLoaded;
            worldManager.OnWorldLoadFailed   -= OnWorldLoadFailed;
            worldManager.OnWorldLoadProgress -= OnWorldProgress;
            worldManager.OnWorldUnloaded     -= OnWorldUnloadedHandler;
            _generationCancelled = true;   // abort any in-progress polling
        }

        // ── Public ────────────────────────────────────────────────────────────

        /// <summary>
        /// Unloads the current world and restores the default asset placeholder.
        /// Wired to <see cref="unloadButton"/> automatically; can also be called from code.
        /// </summary>
        public void OnUnloadCurrentWorld()
        {
            worldManager.RestoreDefaultWorld();
            RefreshAllCards();
            SetStatus("World unloaded");
        }

        public async void Refresh()
        {
            if (_loading) return;
            _loading = true;
            _history.Clear();
            _currentToken = null;
            SetStatus("Loading worlds…");
            prevButton.interactable = false;
            nextButton.interactable = false;

            try
            {
                var worlds = await worldManager.ListWorldsAsync(pageToken: null, pageSize: 20);
                _nextToken = worldManager.LastNextPageToken;
                PopulateGrid(worlds);
                SetStatus($"{worlds.Count} worlds");
            }
            catch (Exception ex) { SetStatus($"Error: {ex.Message}"); }
            finally { _loading = false; UpdatePagination(); }
        }

        // ── Pagination ────────────────────────────────────────────────────────

        async void OnNextPage()
        {
            if (_loading || string.IsNullOrEmpty(_nextToken)) return;
            _loading = true;
            prevButton.interactable = false;
            nextButton.interactable = false;
            SetStatus("Loading…");

            _history.Push(_currentToken);
            _currentToken = _nextToken;
            _nextToken = null;

            try
            {
                var worlds = await worldManager.ListWorldsAsync(pageToken: _currentToken, pageSize: 20);
                _nextToken = worldManager.LastNextPageToken;
                PopulateGrid(worlds);
                SetStatus($"{worlds.Count} worlds");
            }
            catch (Exception ex) { SetStatus($"Error: {ex.Message}"); }
            finally { _loading = false; UpdatePagination(); }
        }

        async void OnPrevPage()
        {
            if (_loading || _history.Count == 0) return;
            _loading = true;
            prevButton.interactable = false;
            nextButton.interactable = false;
            SetStatus("Loading…");

            _currentToken = _history.Pop();
            _nextToken = null;

            try
            {
                var worlds = await worldManager.ListWorldsAsync(pageToken: _currentToken, pageSize: 20);
                _nextToken = worldManager.LastNextPageToken;
                PopulateGrid(worlds);
                SetStatus($"{worlds.Count} worlds");
            }
            catch (Exception ex) { SetStatus($"Error: {ex.Message}"); }
            finally { _loading = false; UpdatePagination(); }
        }

        // ── Grid ──────────────────────────────────────────────────────────────

        void PopulateGrid(IList<World> worlds)
        {
            Debug.Log($"[WorldBrowserController] PopulateGrid: {worlds.Count} worlds, " +
                      $"content={worldListContent != null}");

            foreach (var c in _pool) c.gameObject.SetActive(false);

            for (int i = 0; i < worlds.Count; i++)
            {
                WorldCardUI card;
                if (i < _pool.Count)
                {
                    card = _pool[i];
                    card.gameObject.SetActive(true);
                }
                else
                {
                    card = WorldCardUI.Create(worldListContent, cardImageHeight);
                    _pool.Add(card);
                }
                card.Bind(worlds[i], worldManager);
            }

            Debug.Log($"[WorldBrowserController] Grid populated: {_pool.Count} cards created");

            // Force layout rebuild so ContentSizeFitter sets content height immediately.
            LayoutRebuilder.ForceRebuildLayoutImmediate(worldListContent);

            if (worldScrollRect != null)
                worldScrollRect.normalizedPosition = new Vector2(0, 1);
        }

        void UpdatePagination()
        {
            prevButton.interactable = _history.Count > 0;
            nextButton.interactable = !string.IsNullOrEmpty(_nextToken);
            if (pageLabel != null)
                pageLabel.text = $"Page {_history.Count + 1}";
        }

        // ── Manager events ────────────────────────────────────────────────────

        void OnWorldLoaded(string id, GaussianSplatting.Runtime.GaussianSplatRenderer _)
        {
            RefreshCard(id);
            SetStatus("World loaded");
        }

        void OnWorldLoadFailed(string id, string error)
        {
            RefreshCard(id);
            SetStatus($"Failed: {error}");
        }

        void OnWorldProgress(string id, float progress)
        {
            SetStatus($"Loading {progress * 100:0}%…");
            foreach (var c in _pool)
                if (c.isActiveAndEnabled && c.WorldId == id)
                { c.SetProgress(progress); return; }
        }

        void OnWorldUnloadedHandler(string id)
        {
            // Refresh every visible card so "loaded" highlight resets correctly.
            RefreshAllCards();
        }

        void RefreshCard(string id)
        {
            foreach (var c in _pool)
                if (c.isActiveAndEnabled && c.WorldId == id)
                { c.RefreshState(); return; }
        }

        void RefreshAllCards()
        {
            foreach (var c in _pool)
                if (c.isActiveAndEnabled) c.RefreshState();
        }

        void SetStatus(string msg)
        {
            if (statusText != null) statusText.text = msg;
        }

        // ── World creation ────────────────────────────────────────────────────

        /// <summary>
        /// Toggles between the browse list and the create-world panel.
        /// Safe to call from a UI Button onClick event.
        /// </summary>
        public void ToggleCreateMode()
        {
            _createPanelOpen = !_createPanelOpen;

            if (createPanel != null)
                createPanel.SetActive(_createPanelOpen);

            if (worldScrollRect != null)
                worldScrollRect.gameObject.SetActive(!_createPanelOpen);

            // Update the toggle button label
            if (createToggleButton != null)
            {
                var txt = createToggleButton.GetComponentInChildren<Text>();
                if (txt != null)
                    txt.text = _createPanelOpen ? "✕ Browse" : "➕ Create";
            }

            if (_createPanelOpen && !_isGenerating)
                SetStatus("Enter a prompt and press Generate.");
            else if (!_createPanelOpen && !_isGenerating)
                SetStatus(string.Empty);
        }

        /// <summary>
        /// Starts world generation from the text in <see cref="promptInputField"/>.
        /// Loads the default asset as a placeholder while the API generates the world,
        /// then loads the finished world automatically as the active world.
        /// </summary>
        async void StartWorldCreation()
        {
            if (_isGenerating) return;

            string prompt = promptInputField != null ? (promptInputField.text ?? string.Empty).Trim() : string.Empty;
            if (string.IsNullOrEmpty(prompt))
            {
                SetStatus("Please enter a prompt.");
                return;
            }

            _isGenerating        = true;
            _generationCancelled = false;
            if (createWorldButton != null) createWorldButton.interactable = false;

            // Show the default asset as a placeholder while generation is in progress
            worldManager.RestoreDefaultWorld();
            SetStatus("Starting generation…");

            _wlClient ??= new WorldLabsClient();

            try
            {
                string modelStr = creationModel == GenerationModel.Plus
                    ? "Marble 0.1-plus"
                    : "Marble 0.1-mini";

                var request = new WorldsGenerateRequest
                {
                    world_prompt = TextPrompt.Create(prompt),
                    model        = modelStr,
                    permission   = Permission.Private
                };

                GenerateWorldResponse genResponse = await _wlClient.GenerateWorldAsync(request);
                string opId = genResponse.operation_id;

                // Poll until the operation is done AND world assets are ready
                const float pollInterval = 5f;
                const float timeout      = 600f;
                float       elapsed      = 0f;
                World       readyWorld   = null;

                while (elapsed < timeout && !_generationCancelled)
                {
                    await Task.Delay(TimeSpan.FromSeconds(pollInterval));
                    elapsed += pollInterval;

                    if (_generationCancelled) break;

                    GetOperationResponse op = await _wlClient.GetOperationAsync(opId);

                    if (op.error != null && !string.IsNullOrEmpty(op.error.message))
                        throw new Exception(op.error.message);

                    if (op.done && IsWorldReady(op.response))
                    {
                        readyWorld = op.response;
                        break;
                    }

                    SetStatus(op.done
                        ? "Finalizing assets…"
                        : $"Generating {(op.metadata?.progress ?? 0f) * 100:0}%…");
                }

                if (_generationCancelled)
                {
                    SetStatus("Generation cancelled.");
                    return;
                }

                if (readyWorld == null)
                {
                    SetStatus("Timed out waiting for world assets.");
                    return;
                }

                // Return to browse mode before loading the world
                if (_createPanelOpen) ToggleCreateMode();
                SetStatus("Loading world…");

                await worldManager.LoadWorldAsync(readyWorld);
                SetStatus("World loaded!");

                // Refresh the browse list so the new world appears in the grid
                Refresh();
            }
            catch (Exception ex)
            {
                SetStatus($"Error: {ex.Message}");
                Debug.LogError($"[WorldBrowserController] World creation failed: {ex}");
            }
            finally
            {
                _isGenerating = false;
                if (createWorldButton != null) createWorldButton.interactable = true;
            }
        }

        /// <summary>Returns true when the world has at least panorama imagery or splat assets.</summary>
        static bool IsWorldReady(World world)
        {
            if (world == null) return false;
            bool hasImagery = !string.IsNullOrEmpty(world.assets?.imagery?.pano_url);
            bool hasSplats  = world.assets?.splats?.spz_urls?.Count > 0;
            return hasImagery || hasSplats;
        }

        // ── UI auto-build ─────────────────────────────────────────────────────

        void BuildUI()
        {
            const float headerH = 40f;
            const float footerH = 46f;
            const float statusH = 20f;

            // ── World Space Canvas ────────────────────────────────────────────
            var canvasGo = new GameObject("WorldBrowserCanvas");
            canvasGo.transform.SetParent(transform, false);
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvasGo.AddComponent<GraphicRaycaster>();
            browserCanvas = canvas;

            var canvasRt = canvasGo.GetComponent<RectTransform>();
            canvasRt.sizeDelta     = canvasPixelSize;
            canvasRt.localScale    = new Vector3(0.005f, 0.005f, 0.005f);
            canvasRt.localPosition = Vector3.zero;
            canvasRt.localRotation = Quaternion.identity;

            // ── Root panel ────────────────────────────────────────────────────
            var panelGo = Div("Panel", canvasGo.transform);
            Stretch(panelGo, Vector2.zero, Vector2.one);
            panelGo.AddComponent<Image>().color = new Color(0.10f, 0.10f, 0.14f, 0.97f);
            Transform panel = panelGo.transform;

            // ── Header ────────────────────────────────────────────────────────
            var headerGo = Div("Header", panel);
            AnchorEdge(headerGo, top: true, h: headerH);
            headerGo.AddComponent<Image>().color = new Color(0.11f, 0.11f, 0.17f, 1f);

            MakeLabel(headerGo.transform, "Title", "WorldLabs Worlds", 16,
                TextAnchor.MiddleLeft,
                new Vector2(0, 0), new Vector2(0.50f, 1),
                new Vector2(12, 0), new Vector2(0, 0));

            createToggleButton = MakeButton(headerGo.transform, "➕ Create",
                new Vector2(0.50f, 0), new Vector2(0.78f, 1),
                new Vector2(2, 4), new Vector2(-2, -4));

            pageLabel = MakeLabel(headerGo.transform, "PageLabel", "Page 1", 12,
                TextAnchor.MiddleRight,
                new Vector2(0.78f, 0), new Vector2(1, 1),
                new Vector2(0, 0), new Vector2(-8, 0));
            pageLabel.color = new Color(0.65f, 0.65f, 0.65f, 1f);

            // ── Footer ────────────────────────────────────────────────────────
            var footerGo = Div("Footer", panel);
            AnchorEdge(footerGo, top: false, h: footerH);
            footerGo.AddComponent<Image>().color = new Color(0.09f, 0.09f, 0.14f, 1f);

            prevButton = MakeButton(footerGo.transform, "◀ Prev",
                new Vector2(0, 0), new Vector2(0.35f, 1), new Vector2(8, 6), new Vector2(-4, -6));
            nextButton = MakeButton(footerGo.transform, "Next ▶",
                new Vector2(0.65f, 0), new Vector2(1, 1), new Vector2(4, 6), new Vector2(-8, -6));

            // ── Status bar ────────────────────────────────────────────────────
            var statusGo = new GameObject("Status", typeof(RectTransform), typeof(Text));
            statusGo.transform.SetParent(panel, false);
            var statusRt = statusGo.GetComponent<RectTransform>();
            statusRt.anchorMin = new Vector2(0, 0);
            statusRt.anchorMax = new Vector2(1, 0);
            statusRt.offsetMin = new Vector2(10, footerH);
            statusRt.offsetMax = new Vector2(-10, footerH + statusH);
            statusText = statusGo.GetComponent<Text>();
            statusText.font      = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            statusText.fontSize  = 11;
            statusText.color     = new Color(0.55f, 0.55f, 0.55f, 1f);
            statusText.alignment = TextAnchor.MiddleLeft;
            statusText.raycastTarget = false;

            // ── Scroll view ───────────────────────────────────────────────────
            var scrollGo = Div("ScrollView", panel);
            var scrollRt = scrollGo.GetComponent<RectTransform>();
            scrollRt.anchorMin = new Vector2(0, 0);
            scrollRt.anchorMax = new Vector2(1, 1);
            scrollRt.offsetMin = new Vector2(0, footerH + statusH);
            scrollRt.offsetMax = new Vector2(0, -headerH);
            scrollGo.AddComponent<Image>().color = new Color(0.08f, 0.08f, 0.12f, 1f);

            var scroll = scrollGo.AddComponent<ScrollbarOnlyScrollRect>();
            scroll.horizontal = false;
            worldScrollRect = scroll;

            // Viewport — RectMask2D uses scissor-rect clipping (no stencil buffer).
            // Mask+Color.clear can mask out all children on Android/XR GPUs.
            var vpGo = Div("Viewport", scrollGo.transform);
            Stretch(vpGo, Vector2.zero, Vector2.one);
            vpGo.AddComponent<RectMask2D>();
            scroll.viewport = vpGo.GetComponent<RectTransform>();

            // Content — GridLayoutGroup drives layout, ContentSizeFitter drives height
            var contentGo = Div("Content", vpGo.transform);
            var contentRt = contentGo.GetComponent<RectTransform>();
            contentRt.anchorMin = new Vector2(0, 1);
            contentRt.anchorMax = new Vector2(1, 1);
            contentRt.pivot     = new Vector2(0.5f, 1);
            contentRt.offsetMin = contentRt.offsetMax = Vector2.zero;

            const float pad = 8f, gap = 6f;
            float cellW = (canvasPixelSize.x - pad * 2 - gap * (columns - 1)) / columns;

            var glg = contentGo.AddComponent<GridLayoutGroup>();
            glg.constraint      = GridLayoutGroup.Constraint.FixedColumnCount;
            glg.constraintCount = columns;
            glg.cellSize        = new Vector2(cellW, cardHeight);
            glg.spacing         = new Vector2(gap, gap);
            glg.padding         = new RectOffset((int)pad, (int)pad, (int)pad, (int)pad);
            glg.startCorner     = GridLayoutGroup.Corner.UpperLeft;
            glg.startAxis       = GridLayoutGroup.Axis.Horizontal;
            glg.childAlignment  = TextAnchor.UpperLeft;

            contentGo.AddComponent<ContentSizeFitter>().verticalFit =
                ContentSizeFitter.FitMode.PreferredSize;

            worldListContent = contentRt;
            scroll.content   = contentRt;

            // ── Create panel (overlays scroll view; hidden by default) ─────────
            var createGo = Div("CreatePanel", panel);
            var createRt = createGo.GetComponent<RectTransform>();
            createRt.anchorMin = new Vector2(0, 0);
            createRt.anchorMax = new Vector2(1, 1);
            createRt.offsetMin = new Vector2(0, footerH + statusH);
            createRt.offsetMax = new Vector2(0, -headerH);
            createGo.AddComponent<Image>().color = new Color(0.10f, 0.10f, 0.14f, 0.97f);
            createPanel = createGo;
            createGo.SetActive(false);

            MakeLabel(createGo.transform, "CreateTitle", "Create New World", 16,
                TextAnchor.MiddleCenter,
                new Vector2(0, 0.85f), new Vector2(1, 1f),
                new Vector2(10, 0), new Vector2(-10, 0));

            MakeLabel(createGo.transform, "PromptLabel", "Describe your world:", 13,
                TextAnchor.MiddleLeft,
                new Vector2(0, 0.70f), new Vector2(1, 0.83f),
                new Vector2(16, 0), new Vector2(-16, 0));

            promptInputField = MakeInputField(createGo.transform, "PromptInput",
                new Vector2(0, 0.36f), new Vector2(1, 0.70f),
                new Vector2(14, 0), new Vector2(-14, 0));

            createWorldButton = MakeButton(createGo.transform, "Generate World",
                new Vector2(0.08f, 0.20f), new Vector2(0.92f, 0.34f),
                new Vector2(0, 0), new Vector2(0, 0));
        }

        // ── Small helpers ─────────────────────────────────────────────────────

        static GameObject Div(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go;
        }

        static void Stretch(GameObject go, Vector2 anchorMin, Vector2 anchorMax)
        {
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = anchorMin; rt.anchorMax = anchorMax;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
        }

        /// <summary>Anchors a strip to the top or bottom edge of its parent.</summary>
        static void AnchorEdge(GameObject go, bool top, float h)
        {
            var rt = go.GetComponent<RectTransform>();
            if (top)
            {
                rt.anchorMin = new Vector2(0, 1); rt.anchorMax = new Vector2(1, 1);
                rt.offsetMin = new Vector2(0, -h); rt.offsetMax = Vector2.zero;
            }
            else
            {
                rt.anchorMin = new Vector2(0, 0); rt.anchorMax = new Vector2(1, 0);
                rt.offsetMin = Vector2.zero;       rt.offsetMax = new Vector2(0, h);
            }
        }

        static Text MakeLabel(Transform parent, string name, string text, int size,
            TextAnchor align,
            Vector2 anchorMin, Vector2 anchorMax,
            Vector2 offsetMin, Vector2 offsetMax)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Text));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = anchorMin; rt.anchorMax = anchorMax;
            rt.offsetMin = offsetMin; rt.offsetMax = offsetMax;
            var t = go.GetComponent<Text>();
            t.font      = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            t.fontSize  = size;
            t.color     = Color.white;
            t.alignment = align;
            t.text      = text;
            t.raycastTarget = false;
            return t;
        }

        static Button MakeButton(Transform parent, string label,
            Vector2 anchorMin, Vector2 anchorMax,
            Vector2 offsetMin, Vector2 offsetMax)
        {
            var go = new GameObject(label, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = anchorMin; rt.anchorMax = anchorMax;
            rt.offsetMin = offsetMin; rt.offsetMax = offsetMax;

            var img = go.AddComponent<Image>();
            img.color = new Color(0.20f, 0.40f, 0.70f, 1f);
            var btn = go.AddComponent<Button>();
            btn.targetGraphic = img;

            var colors = btn.colors;
            colors.highlightedColor = new Color(0.35f, 0.55f, 0.85f, 1f);
            colors.pressedColor     = new Color(0.15f, 0.30f, 0.55f, 1f);
            btn.colors = colors;

            var textGo = new GameObject("Text", typeof(RectTransform), typeof(Text));
            textGo.transform.SetParent(go.transform, false);
            var trt = textGo.GetComponent<RectTransform>();
            trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one;
            trt.offsetMin = trt.offsetMax = Vector2.zero;
            var t = textGo.GetComponent<Text>();
            t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            t.fontSize = 13; t.color = Color.white;
            t.alignment = TextAnchor.MiddleCenter; t.text = label;
            t.raycastTarget = false;
            return btn;
        }

        /// <summary>Creates a multi-line InputField sized by anchor rect.</summary>
        static InputField MakeInputField(Transform parent, string name,
            Vector2 anchorMin, Vector2 anchorMax,
            Vector2 offsetMin, Vector2 offsetMax)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = anchorMin; rt.anchorMax = anchorMax;
            rt.offsetMin = offsetMin; rt.offsetMax = offsetMax;

            var bg = go.AddComponent<Image>();
            bg.color = new Color(0.14f, 0.16f, 0.22f, 1f);

            var field = go.AddComponent<InputField>();
            field.lineType              = InputField.LineType.MultiLineNewline;
            // GameActivity on Android (Meta Quest) throws
            // "Hiding input field is not supported when using Game Activity"
            // when this is true, which then causes a keyboard-visibility timeout.
            // Setting false keeps the InputField visible while the system keyboard
            // is open — correct for a world-space VR canvas.
            field.shouldHideMobileInput = false;

            // Placeholder text
            var phGo = new GameObject("Placeholder", typeof(RectTransform), typeof(Text));
            phGo.transform.SetParent(go.transform, false);
            var phRt = phGo.GetComponent<RectTransform>();
            phRt.anchorMin = Vector2.zero; phRt.anchorMax = Vector2.one;
            phRt.offsetMin = new Vector2(8, 4); phRt.offsetMax = new Vector2(-8, -4);
            var phTxt = phGo.GetComponent<Text>();
            phTxt.font          = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            phTxt.fontSize      = 13;
            phTxt.color         = new Color(0.50f, 0.50f, 0.55f, 0.80f);
            phTxt.fontStyle     = FontStyle.Italic;
            phTxt.text          = "e.g. A serene Japanese garden at dawn…";
            phTxt.raycastTarget = false;
            field.placeholder   = phTxt;

            // Input text
            var txtGo = new GameObject("Text", typeof(RectTransform), typeof(Text));
            txtGo.transform.SetParent(go.transform, false);
            var txtRt = txtGo.GetComponent<RectTransform>();
            txtRt.anchorMin = Vector2.zero; txtRt.anchorMax = Vector2.one;
            txtRt.offsetMin = new Vector2(8, 4); txtRt.offsetMax = new Vector2(-8, -4);
            var inputTxt = txtGo.GetComponent<Text>();
            inputTxt.font          = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            inputTxt.fontSize      = 13;
            inputTxt.color         = Color.white;
            inputTxt.raycastTarget = false;
            field.textComponent    = inputTxt;

            return field;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // WorldCardUI — one clickable card per world
    // Implements pointer interfaces directly (no Button component) so that XR
    // input modules (e.g. Oculus PointableCanvasModule) always fire events on
    // the same GameObject as the raycast-target Image.
    // ──────────────────────────────────────────────────────────────────────────

    public class WorldCardUI : MonoBehaviour,
        IPointerEnterHandler,
        IPointerExitHandler,
        IInitializePotentialDragHandler,
        IBeginDragHandler,
        IDragHandler,
        IEndDragHandler
    {
        // Wired by Create()
        public RawImage   panoramaImage;
        public Text       nameText;
        public GameObject loadingOverlay;
        public Text       loadingLabel;
        public Slider     progressSlider;
        Image             _hoverOverlay;   // brightens on pointer-enter

        World _world;
        WorldLabsWorldManager _manager;

        public string WorldId => _world?.world_id;

        // ── IPointerEnterHandler / IPointerExitHandler ────────────────────────

        public void OnPointerEnter(PointerEventData _)
        {
            if (_hoverOverlay != null)
                _hoverOverlay.color = new Color(0.60f, 0.75f, 1.00f, 0.28f);
        }

        public void OnPointerExit(PointerEventData _)
        {
            if (_hoverOverlay != null)
                _hoverOverlay.color = new Color(1f, 1f, 1f, 0f);
        }

        // ── Drag interception ─────────────────────────────────────────────────
        // WorldCardUI is on the same GO as the Button, so when the event system
        // finds this as the drag handler it sets:
        //   pointerDrag  = card GO
        //   pointerPress = card GO  (Button is also on card GO)
        // Because pointerDrag == pointerPress, Unity does NOT clear eligibleForClick
        // even if the pointer drifts — so Button.onClick always fires on tap.
        // The no-op drag methods also prevent ScrollRect from ever receiving drags
        // from the card area, so dragging cards never scrolls the list.
        public void OnInitializePotentialDrag(PointerEventData e) { e.useDragThreshold = true; }
        public void OnBeginDrag(PointerEventData e) { }
        public void OnDrag(PointerEventData e) { }
        public void OnEndDrag(PointerEventData e) { }

        // ── Binding ───────────────────────────────────────────────────────────

        public void Bind(World world, WorldLabsWorldManager manager)
        {
            _world   = world;
            _manager = manager;

            Debug.Log($"[WorldCardUI] Bind '{world?.display_name}'");

            if (nameText != null)
                nameText.text = string.IsNullOrEmpty(world.display_name)
                    ? world.world_id : world.display_name;

            if (_hoverOverlay != null)
                _hoverOverlay.color = new Color(1f, 1f, 1f, 0f);

            // Wire the Button click — Button is on the same root GO.
            var btn = GetComponent<Button>();
            if (btn != null)
            {
                btn.onClick.RemoveAllListeners();
                btn.onClick.AddListener(HandleClick);
                btn.interactable = true;
            }

            RefreshState();
            DownloadPanorama();
        }

        // ── State ─────────────────────────────────────────────────────────────

        public void RefreshState()
        {
            if (_world == null) return;
            bool loaded  = _manager != null && _manager.IsWorldLoaded(_world.world_id);
            bool loading = _manager != null && _manager.IsWorldLoading(_world.world_id);

            if (loadingOverlay != null)
                loadingOverlay.SetActive(loading);

            if (progressSlider != null)
                progressSlider.gameObject.SetActive(loading);

            // Card border colour (the 2 px inset around the panorama) — green = loaded.
            var img = GetComponent<Image>();
            if (img != null)
                img.color = loaded
                    ? new Color(0.15f, 0.70f, 0.25f, 1f)   // bright green border
                    : new Color(0.22f, 0.24f, 0.32f, 1f);  // neutral dark-blue border

            if (nameText != null)
            {
                var nameBg = nameText.transform.parent?.GetComponent<Image>();
                if (nameBg != null)
                    nameBg.color = loaded
                        ? new Color(0.06f, 0.22f, 0.10f, 0.95f)
                        : new Color(0.08f, 0.08f, 0.14f, 0.95f);

                string displayName = string.IsNullOrEmpty(_world.display_name)
                    ? _world.world_id : _world.display_name;
                nameText.text = loaded ? $"✓  {displayName}" : displayName;
            }
        }

        public void SetProgress(float p)
        {
            if (progressSlider == null) return;
            progressSlider.gameObject.SetActive(true);
            progressSlider.value = p;
            if (loadingLabel != null)
                loadingLabel.text = $"Loading {p * 100:0}%";
        }

        // ── Click ─────────────────────────────────────────────────────────────

        async void HandleClick()
        {
            Debug.Log($"[WorldCardUI] Click: '{_world?.display_name}'");

            if (_world == null || _manager == null) return;
            if (_manager.IsWorldLoading(_world.world_id)) return;

            if (_manager.IsWorldLoaded(_world.world_id))
            {
                _manager.UnloadWorld(_world.world_id);
                RefreshState();
            }
            else
            {
                RefreshState();
                try
                {
                    await _manager.LoadWorldAsync(_world);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[WorldCardUI] Load failed '{_world.display_name}': {ex.Message}");
                }
                RefreshState();
            }
        }

        // ── Panorama download — async/await, no coroutine ─────────────────────

        async void DownloadPanorama()
        {
            if (panoramaImage == null) return;
            panoramaImage.texture = null;

            // Prefer the panorama URL; fall back to thumbnail (handles WebP skip)
            string panoUrl  = _world?.assets?.imagery?.pano_url;
            string thumbUrl = _world?.assets?.thumbnail_url;

            if (string.IsNullOrEmpty(panoUrl) && string.IsNullOrEmpty(thumbUrl))
            {
                Debug.Log($"[WorldCardUI] No image URL for '{_world?.display_name}'");
                return;
            }

            try
            {
                Texture2D tex = await WorldLabsClientExtensions
                    .DownloadTextureWithFallbackAsync(panoUrl, thumbUrl);

                // Guard: card might have been recycled by the time download completes
                if (panoramaImage != null && tex != null)
                    panoramaImage.texture = tex;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[WorldCardUI] Panorama download failed for " +
                                 $"'{_world?.display_name}': {ex.Message}");
            }
        }

        // ── Factory ───────────────────────────────────────────────────────────

        /// <summary>Creates a card sized by the GridLayoutGroup cell; do not set sizeDelta.</summary>
        public static WorldCardUI Create(Transform parent, float imageHeight)
        {
            const float nameBarH = 34f;

            var go = new GameObject("WorldCard", typeof(RectTransform));
            go.transform.SetParent(parent, false);

            // Root Image — the SOLE raycast target for the entire card.
            var cardImg = go.AddComponent<Image>();
            cardImg.color         = new Color(0.22f, 0.24f, 0.32f, 1f);
            cardImg.raycastTarget = true;

            // Button on the same GO as the raycast-target Image.
            // Transition.None — cardImg is hidden behind children, tinting is invisible.
            // Hover visual is handled separately by WorldCardUI + _hoverOverlay.
            var btn = go.AddComponent<Button>();
            btn.targetGraphic = cardImg;
            btn.interactable  = true;
            btn.transition    = Selectable.Transition.None;
            btn.navigation    = new Navigation { mode = Navigation.Mode.None };

            var card = go.AddComponent<WorldCardUI>();

            // ── Panorama (inset 2 px so card border colour shows) ─────────────
            var panoGo = new GameObject("Panorama", typeof(RectTransform), typeof(RawImage));
            panoGo.transform.SetParent(go.transform, false);
            var panoRt = panoGo.GetComponent<RectTransform>();
            panoRt.anchorMin = new Vector2(0f, 0f);
            panoRt.anchorMax = new Vector2(1f, 1f);
            panoRt.offsetMin = new Vector2(2f, nameBarH + 2f);
            panoRt.offsetMax = new Vector2(-2f, -2f);
            card.panoramaImage = panoGo.GetComponent<RawImage>();
            card.panoramaImage.color         = new Color(0.18f, 0.22f, 0.32f, 1f);
            card.panoramaImage.uvRect        = new Rect(0, 0, 1, 1);
            card.panoramaImage.raycastTarget = false;  // must not block root

            // ── Name bar (bottom 34 px) ───────────────────────────────────────
            var nameBgGo = new GameObject("NameBar", typeof(RectTransform), typeof(Image));
            nameBgGo.transform.SetParent(go.transform, false);
            var nameBgRt = nameBgGo.GetComponent<RectTransform>();
            nameBgRt.anchorMin = new Vector2(0f, 0f);
            nameBgRt.anchorMax = new Vector2(1f, 0f);
            nameBgRt.offsetMin = Vector2.zero;
            nameBgRt.offsetMax = new Vector2(0f, nameBarH);
            nameBgGo.GetComponent<Image>().color         = new Color(0.08f, 0.08f, 0.14f, 0.95f);
            nameBgGo.GetComponent<Image>().raycastTarget = false;

            var nameGo = new GameObject("Name", typeof(RectTransform), typeof(Text));
            nameGo.transform.SetParent(nameBgGo.transform, false);
            var nameRt = nameGo.GetComponent<RectTransform>();
            nameRt.anchorMin = Vector2.zero; nameRt.anchorMax = Vector2.one;
            nameRt.offsetMin = new Vector2(6f, 2f); nameRt.offsetMax = new Vector2(-6f, -2f);
            card.nameText = nameGo.GetComponent<Text>();
            card.nameText.font          = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            card.nameText.fontSize      = 13;
            card.nameText.color         = Color.white;
            card.nameText.alignment     = TextAnchor.MiddleLeft;
            card.nameText.raycastTarget = false;

            // ── Loading overlay (hidden by default) ───────────────────────────
            var overlayGo = new GameObject("LoadingOverlay", typeof(RectTransform), typeof(Image));
            overlayGo.transform.SetParent(go.transform, false);
            var overlayRt = overlayGo.GetComponent<RectTransform>();
            overlayRt.anchorMin = new Vector2(0f, 0f);
            overlayRt.anchorMax = new Vector2(1f, 1f);
            overlayRt.offsetMin = new Vector2(0f, nameBarH);
            overlayRt.offsetMax = Vector2.zero;
            overlayGo.GetComponent<Image>().color         = new Color(0f, 0f, 0f, 0.60f);
            overlayGo.GetComponent<Image>().raycastTarget = false;
            card.loadingOverlay = overlayGo;

            var loadLabelGo = new GameObject("LoadingLabel", typeof(RectTransform), typeof(Text));
            loadLabelGo.transform.SetParent(overlayGo.transform, false);
            var llRt = loadLabelGo.GetComponent<RectTransform>();
            llRt.anchorMin = new Vector2(0.05f, 0.45f);
            llRt.anchorMax = new Vector2(0.95f, 0.70f);
            llRt.offsetMin = llRt.offsetMax = Vector2.zero;
            card.loadingLabel = loadLabelGo.GetComponent<Text>();
            card.loadingLabel.font          = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            card.loadingLabel.fontSize      = 13;
            card.loadingLabel.color         = Color.white;
            card.loadingLabel.alignment     = TextAnchor.MiddleCenter;
            card.loadingLabel.text          = "Loading…";
            card.loadingLabel.raycastTarget = false;

            // ── Progress slider (inside overlay) ─────────────────────────────
            var sliderGo = new GameObject("Progress", typeof(RectTransform));
            sliderGo.transform.SetParent(overlayGo.transform, false);
            var sliderRt = sliderGo.GetComponent<RectTransform>();
            sliderRt.anchorMin = new Vector2(0.05f, 0.32f);
            sliderRt.anchorMax = new Vector2(0.95f, 0.42f);
            sliderRt.offsetMin = sliderRt.offsetMax = Vector2.zero;

            var slider = sliderGo.AddComponent<Slider>();
            slider.minValue = 0; slider.maxValue = 1; slider.value = 0;
            slider.interactable = false;
            slider.transition   = Selectable.Transition.None;

            var sliderBg = new GameObject("BG", typeof(RectTransform), typeof(Image));
            sliderBg.transform.SetParent(sliderGo.transform, false);
            Stretch(sliderBg, Vector2.zero, Vector2.one);
            var sliderBgImg = sliderBg.GetComponent<Image>();
            sliderBgImg.color = new Color(0.2f, 0.2f, 0.2f, 1f);
            sliderBgImg.raycastTarget = false;

            var fillArea = new GameObject("FillArea", typeof(RectTransform));
            fillArea.transform.SetParent(sliderGo.transform, false);
            Stretch(fillArea, Vector2.zero, Vector2.one);

            var fill = new GameObject("Fill", typeof(RectTransform), typeof(Image));
            fill.transform.SetParent(fillArea.transform, false);
            var fillRt = fill.GetComponent<RectTransform>();
            fillRt.anchorMin = Vector2.zero;
            fillRt.anchorMax = new Vector2(0f, 1f);
            fillRt.offsetMin = fillRt.offsetMax = Vector2.zero;
            var fillImg = fill.GetComponent<Image>();
            fillImg.color = new Color(0.25f, 0.65f, 1.0f, 1f);
            fillImg.raycastTarget = false;
            slider.fillRect = fillRt;

            card.progressSlider = slider;
            sliderGo.SetActive(false);
            overlayGo.SetActive(false);

            // ── Hover overlay — last child, drawn on top, transparent by default ─
            // WorldCardUI.OnPointerEnter/Exit sets its colour directly.
            // raycastTarget = false so all hits land on cardImg (root).
            var hlGo = new GameObject("HoverOverlay", typeof(RectTransform), typeof(Image));
            hlGo.transform.SetParent(go.transform, false);
            var hlRt = hlGo.GetComponent<RectTransform>();
            hlRt.anchorMin = Vector2.zero; hlRt.anchorMax = Vector2.one;
            hlRt.offsetMin = hlRt.offsetMax = Vector2.zero;
            var hlImg = hlGo.GetComponent<Image>();
            hlImg.color         = new Color(1f, 1f, 1f, 0f);
            hlImg.raycastTarget = false;
            card._hoverOverlay  = hlImg;

            return card;
        }

        // reuse the Stretch helper without referencing the outer class
        static void Stretch(GameObject go, Vector2 anchorMin, Vector2 anchorMax)
        {
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = anchorMin; rt.anchorMax = anchorMax;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // ScrollbarOnlyScrollRect
    // A ScrollRect that ignores pointer-drag so only the scrollbar scrolls.
    // Prevents hand-tremor in XR from triggering a scroll when clicking a card.
    // ──────────────────────────────────────────────────────────────────────────

    public class ScrollbarOnlyScrollRect : ScrollRect
    {
        public override void OnInitializePotentialDrag(PointerEventData e) { }
        public override void OnBeginDrag(PointerEventData e) { }
        public override void OnDrag(PointerEventData e) { }
        public override void OnEndDrag(PointerEventData e) { }
    }
}