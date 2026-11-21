# Native node coverage and porting

This is a static audit of the local Shader Graph and URP **17.7.0** sources. No Unity process, compilation, import, or runtime reflection was invoked. The JSON is the detailed source-linked inventory; its SHA-256 manifest identifies this checkout rather than an assumed upstream release.

- **223 concrete native classes**: 210 from the requested Shader Graph node root, 7 from URP (including 2D and Filter), and 6 supplemental classes. All 225 primary node-source files are accounted for.
- **217 declared Title attributes**, not 217 guaranteed menu entries. The inventory also records 5 exported particle-function variants, 8 shipped SpeedTree subgraphs, 25 Master Stack block declarations, and all 220 WhimTex kind IDs.
- **21 HDRP-only documentation entries** are listed separately. HDRP/VFX Graph implementation packages are absent; their uninstalled class catalogs cannot be exhaustively asserted from these sources.

## Preserve the native engine

Use the parent-owned Editor assembly-reference adapter into **Unity.RenderPipelines.Universal.Editor**, with its existing Shader Graph friend access. Keep native GraphData, serialization, node discovery, property types, target/subtarget validation, generated ports, node versions, precision, shader variants, dependencies, and codegen. The WhimTex mappings support migration and UI decisions; they are not a replacement-algorithm checklist. Preserve dynamic subgraphs and reflected-function providers instead of freezing the menu to this snapshot.

Mapping totals: 93 operation counterparts, 18 configurable counterparts, 31 partial counterparts, 4 compositions, 5 infrastructure analogues, 71 missing native equivalents, and 1 invalid placeholder. A counterpart does not claim identical output or port contracts.

## Names and availability that must stay exact

| Source fact | Consequence |
| --- | --- |
| Vector1Node is titled **Float**; TexelSizeNode.cs defines Texture2DPropertiesNode titled **Texture Size**. | Never derive titles from filenames or class identifiers. |
| SampleCubemapNode is **Sample Reflected Cubemap**; SampleRawCubemapNode is **Sample Cubemap**. | Keep both operations. |
| DDXY is the literal Unity title; WhimTex kind is FWidth. | Preserve native pixel derivatives, not image-graph finite differences. |
| Reflection Probe has a Title but ExposeToSearcher=false. | Keep existing-node compatibility and deprecation warning; do not expose it as a normal new node. |
| Property, Keyword, Dropdown, Sub-graph and Custom Interpolator are special dynamic entries. | Their declared titles are not their actual search-entry patterns. |
| Expression and core particle functions are providers. | Include provider discovery; a Title-only scan is incomplete. |
| VFX/Particles contains core Particle System functions usable with URP. | Category VFX does not mean HDRP-only or VFX Graph-only. |
| Custom Render Texture Self/Size/Slice use a separate native target. | Keep separate from UniversalTarget availability. |

URP availability is the union of material contexts. UI Toolkit, Terrain, Sprite Custom Lit/Unlit, and Filter nodes have explicit subtarget restrictions. Sprite Skinning also validates its sprite subtarget and 2D Animation >=10.0.0; DOTS deformation needs Entities Graphics. Baked GI needs a lit context. Scene color/depth, motion vectors, blit and 2D light buffers need their renderer resources. Canvas, Fullscreen, UI Toolkit and Filter reject different geometry/deformation nodes; the JSON records these exclusions.

Slots defaulting to All are not proof that every generated shader is legal. Rounded Polygon declares All but its raster implementation uses fwidth. Texture2D/Array LOD or Gradient modes allow both stages; standard/bias modes require fragment. Texture3D permits both stages only with LOD. Custom interpolator readers are fragment-only, mesh deformation and Vertex ID are vertex-only. Subgraph/provider stages follow their dependencies.

UI Toolkit Sample Element Texture is fragment-only in Standard mode and both-stage in LOD mode. UITK geometry has additional space/stage and optional vertex-channel restrictions. Filter permits only UV0. Light Texture outputs a resource with a white fallback, not sampled color. Master Stack BlockNode is not allowed inside subgraphs.

URP forces streaming virtual texturing off in Core.hlsl. **Sample Virtual Texture remains a native node with ordinary texture sampling fallback**; do not falsely mark it HDRP-filtered. Procedural Virtual Texture is compile-gated by PROCEDURAL_VT_IN_GRAPH.

## Important WhimTex gaps

Native geometry, camera, lighting, renderer buffers, mesh channels, texture arrays/3D/cubemaps and sampler resources need the native runtime context. WhimTex Transform only converts layer/canvas Float2 coordinates; InstanceIndex is not GPU Instance ID; LightDirection is not URP main-light data. WhimTex noise, Blend, height normals, parallax, derivatives, gradients and color operations have their own contracts even when names overlap.

## Required extensions

SoftSign and InverseSoftSign belong under **Math/Advanced**, as native AbstractMaterialNode + IGeneratesBodyCode classes in DCFApixels.WhimShader. Both use DynamicVectorMaterialSlot for float1–4 and inherit both-stage slot capability. They generate HLSL directly without reflection. The LUT sample nodes use the same direct codegen approach.

- SoftSign: x / (1 + abs(x)); maps finite real components into (-1,1).
- InverseSoftSign: x / (1 - abs(x)); the mathematical inverse domain is **(-1,1) per component**. Do not clamp: +/-1 are singular, and values outside the domain are not inverse SoftSign values. Native floating-point behavior is preserved.
- Curve is an extension, not a built-in Unity titled node. WhimTex already has SampleCurve (7), Value/Parameter Curve resources, SoftSign (221), and InverseSoftSign (222).

For editable Gradient/AnimationCurve material parameters, use serialized native Texture2D-derived graph properties plus linear floating-point LUTs and explicit-LOD sample nodes. Preserve editable keys, curve tangents and gradient mode in data; LUT evaluation is a finite-resolution approximation over normalized T in [0,1]. Expose parent importer hooks and material drawers, and runtime texture rebaking/binding without shader recompilation. Window/importer integration, grouping controls and lighting are separate work.

Static JSON integrity, unique class IDs, primary-source coverage and all WhimTex enum references were checked. Shader compilation, native menu activation and runtime behavior remain unverified because Unity execution was explicitly excluded.
