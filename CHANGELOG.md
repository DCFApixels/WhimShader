# Changelog

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
