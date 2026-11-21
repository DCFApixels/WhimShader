# Changelog

## Unreleased

- Added compact agent authoring and optional Pipeline commands for node discovery, topology inspection, detailed queries and revision-checked batches, with persistent aliases, parameter defaults, native validation and live Undo/Redo.
- Adapted WhimTex's automatic arrangement to native graphs. Agent-created nodes no longer require coordinates; existing and explicit positions are preserved. Added Arrange All / Selected in the editor.
- Added on-demand node preview PNGs for agents, with output/channel selection, Image/Height/3D modes and explicit animation time. Requests use unsaved graph state and the existing preview renderer without altering graph data or presentation settings.

- Fixed stretched and blurred catalog thumbnails by preserving the atlas source dimensions and disabling texture compression and mipmaps, matching WhimTex's import settings.
- Reused existing atlas thumbnails for additional native texture, coordinate, matrix, normal, lighting and conversion nodes in both Nodes and Create Node. Parameter entries now choose thumbnails by their value/resource type; compact icons remain unchanged.
- Rebuilt Custom Function with a working default, an on-node WhimTex CodeArea, editable ports, resizable code, separate saved drafts and explicit compilation. Invalid drafts retain the last applied function.
- Added HLSL file/function selection, signature-derived ports including returns and inout, and conversion to embedded full source while retaining relative include paths. Existing native functions retain their code, slot IDs, defaults and connections when opened in the new editor.

## 0.1.0 — 2026-11-22

- Added a URP runtime shader editor with native Shader Graph node discovery, serialization, stage restrictions and ShaderLab generation.
- Added Unity PBR and Scene Lighting nodes with an unlit-style final output, shared vertex/fragment branches and native custom interpolators.
- Ported WhimTex's docking workspace, node presentation, catalog, category icons and thumbnail atlas. Added the Shader Settings panel and an original orange WhimShader icon.
- Ported connected port-label branch highlighting and selection, including downstream output branches, Ctrl/Command addition and cancellation when dragging nodes.
- Added parameter groups and material presentation, editable gradient/curve LUTs, SoftSign and Inverse SoftSign.
- Copied WhimTex's node preview hierarchy, collapse/expand controls, upward output menu, loading indicator, final-preview header and RGBA channel controls. Added 3D beside the existing Image and Height modes.
- Copied WhimTex's horizon-based Height renderer and its channel, scale, texture and range settings. GPU comparison covers 232 configurations with no pixel differences.
- Persisted node/final preview modes, output/channel selection and Height settings in the graph, including Undo/Redo and reopen validation.
- Added native Sub Graph authoring, material creation and ShaderLab/resource export.
- Added explicit Editor/GPU validation for serialization, stage diagnostics, lighting, clipping, LUT sampling and material resource lifecycle.
