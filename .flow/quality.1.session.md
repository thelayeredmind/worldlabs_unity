# quality — Session 1

- session opened
- target: runtime quality label HUD — read each GaussianSplatRenderer's format fields at runtime, display a per-splat-asset screen-space label showing quality (pos/scale/color/SH formats) on standalone Quest 3

[F] Quality flows from DataQuality enum (VeryHigh→VeryLow+Custom) in GaussianSplatAssetCreator → four format fields on GaussianSplatAsset: posFormat (VectorFormat), scaleFormat (VectorFormat), colorFormat (ColorFormat), shFormat (SHFormat). Public accessors exist on the asset.
[F] At runtime (WorldLabs path), quality is selected via SplatQuality enum on WorldLabsWorldManager, resolved to the same four formats, stored on RuntimeSplatData. GaussianSplatRenderer exposes `asset` (for file-loaded splats) and `HasValidRuntimeData` (for runtime-loaded splats); there is no single public "quality label" string — it must be inferred from the four format fields.
[F] Chunking is a derived property: present whenever any format is not Float32. Not a quality level itself — a consequence of compression. kChunkSize = 256 splats/chunk, ChunkInfo = 64 bytes.
[F] GaussianSplatRenderer.asset is public — so posFormat/scaleFormat/colorFormat/shFormat are accessible from outside at runtime via renderer.asset.posFormat etc. For runtime-loaded splats, RuntimeSplatData holds the same four fields but is internal (m_RuntimeData is private). Need to verify if there's a public path to runtime format data, or if we need to add one.

[D] Runtime splats parked — scope is file-loaded assets only via renderer.asset public API.
[D] Label content: quality tier name (reverse-mapped from four format fields) as headline + raw format fields beneath. OnGUI for rendering — no Canvas/prefab dependencies, works in Quest 3 standalone builds.

[Activity] A single MonoBehaviour component, `GaussianQualityHUD`, that finds all active GaussianSplatRenderer instances each frame, projects their world-space centre to screen space, and draws an OnGUI label at that position showing the reverse-mapped quality tier name (VeryHigh/High/Medium/Low/VeryLow/Custom) plus the four raw format fields beneath — reading directly from renderer.asset.posFormat, scaleFormat, colorFormat, shFormat. The component requires no scene dependencies beyond being added to any GameObject; it uses renderer.asset's existing public surface and works in standalone Quest 3 builds.

Activity: GaussianQualityHUD — per-splat screen-space quality label via OnGUI, no scene dependencies.

Ready to wind up.
[S] GaussianQualityHUD.cs — MonoBehaviour with OnGUI, world-to-screen projection, tier reverse-mapping, format detail lines, background box
- reviewable surface: compile check via Unity MCP
Attempt 1
Written GaussianQualityHUD.cs in Runtime/GaussianSplatting/. Finds all GaussianSplatRenderer instances each frame via FindObjectsByType, projects asset bounds centre to screen, draws OnGUI box with tier name headline + four raw format fields. Tier reverse-mapped from the four known quality presets; falls back to "Custom".
Compile check: 0 errors, isCompiling false.
✓ Clean
[/S]
[/Activity] — GaussianQualityHUD shipped and confirmed working in Play mode by user.

[D] Next activity: label boxes stack in a fixed top-right legend column; a GL line connects each box to its splat's projected screen centre.

[Activity] Extend GaussianQualityHUD so label boxes stack vertically in a fixed top-right column (one per active GaussianSplatRenderer), and each box is connected to its splat's projected world-centre by a GL line drawn via GL.Begin(GL.LINES) in OnPostRender (or a helper Camera component). The line runs from the right edge of the label box to the splat's screen-space point. Splat-to-label association is stable across frames (same order as FindObjectsByType). The existing OnGUI label content (tier name + format fields) is unchanged.

Activity: Legend column + GL connector lines from each label to its splat's screen centre.

Ready to wind up.
[S] Legend column + GL connector lines
- reviewable surface: compile check + visual in Play mode
Attempt 1
Rewrote GaussianQualityHUD.cs. OnGUI now stacks label boxes in a fixed top-right column, one slot per in-frustum renderer (sorted by instance ID, compacted when renderers leave the frustum). OnPostRender draws a GL line (yellow, alpha 0.85) from the left-centre of each box to the splat's projected screen position using Hidden/Internal-Colored shader + LoadPixelMatrix. Y-coordinate conversion handled explicitly between GUI space and GL screen space.
Compile check: 0 errors, isCompiling false.
✓ Clean (compile)
[/S]
[R1] Slot assignment was instance-field slot++, not static — rebuilt every frame with no memory. >> Fixed: static s_Registered list, renderers self-register on OnEnable/OnDisable.
[R2] DrawLine GUIUtility.RotateAroundPivot corrupting GUI matrix for subsequent draws. >> Fixed: save/restore GUI.matrix around rotation.
[>] Occlusion: hide dot/label when splat centre is occluded by scene geometry — deferred, needs colliders or depth buffer approach.

--- CLOSED 2026-07-01 — Quality system oriented (format/chunk pipeline survey). GaussianQualityHUD shipped: tier name + format fields, color-coded dot, connector line to legend column, renderers self-register via static list. Confirmed working in editor at 1x Game view scale. ---

[^] Continue quality. Last: session closed. Next: new session or done.
