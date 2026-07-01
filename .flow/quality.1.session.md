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

[^] Continue quality. Last: activity complete, HUD confirmed on. Next: drift or wrap up. Confirm: none.
