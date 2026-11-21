# WhimShader URP 17.7 backend contract

Integration namespace: `DCFApixels.WhimShader`. All C# here belongs to
`Unity.RenderPipelines.Universal.Editor` through the parent Editor asmref.
No Shader Graph bridge, reflection, or WhimTex dependency is required.

## Window entry points

- `public static class WhimShaderURP`
- `internal static GraphData CreateGraph()` creates the Whim Surface target,
  its vertex/fragment blocks, a UnityPBR node connected to Color, and a hidden
  `GraphObject` owner. The window owns/disposes `graph.owner` and supplies its
  asset GUID, save/import path and preview lifecycle. `graph.messageManager` is
  initialized and non-null. Initial positions: Unity PBR `(-420, 100)`, Vertex
  context `(0, 0)`, Fragment context `(0, 300)`.
- `internal static VisualElement BuildSettings(GraphData graph, Action changed)`
  creates native UITK settings. Changes call
  `graph.owner.RegisterCompleteObjectUndo` before mutation, update active blocks,
  validate, dirty nodes and call `changed`. Rebuild this view after Undo/reload.
- `internal sealed class WhimSurfaceSubTarget : UniversalSubTarget`, display
  name **Whim Surface**, discovered by Unity's native subtarget catalogue.

Factory creation, settings changes and subtarget setup set
`UniversalTarget.customEditorGUI` to `WhimShaderMaterialGUI.CustomEditorName`.
The isolated native Lit setup context receives that override before Lit setup
and finalization, so generated SubShaders use the grouped Whim material inspector
for new and previously saved graphs. The parent importer owns `BakeMetadata`
and standalone shader metadata sidecars; this backend does not write metadata.

## Surface contract

Vertex Position/Normal/Tangent are native object-space blocks. Fragment **Color**
uses custom `WhimSurfaceBlocks.Color` / `surface.WhimColor` and
is linear, unfogged HDR radiance: it may already contain lighting. **Alpha** and
**Alpha Clip Threshold** are independent. Only these three fragment blocks and
Position/Normal/Tangent vertex blocks are created by default. The following blocks
are optional through Add Block, remain active once added, and have virtual defaults
in pass generation without being auto-added to the editor.
For Preview/PreviewForReals generation the target separately collects native slot
properties for omitted optional blocks. Missing uniforms receive hidden ShaderLab
properties with their descriptor defaults, initializing every newly created preview
Material. Existing block properties win and continue receiving native preview updates.
No additional blocks are inserted in the graph, and ForReals generation is unchanged.
**Normal (World Space)** is shared with
the depth-normal pass; connect the same normal to UnityPBR/SceneLighting and this
block when using a normal map. **Smoothness** supplies the depth-normal texture's
SSR channel. **Baked Albedo** and **Baked Emission** are independent Meta-pass
inputs; do not feed scene-lit Color into them. Receiving GI does not depend on
these two baking outputs.

Catalogue/name: `Lighting/Unity PBR` and `Lighting/Scene Lighting`; C# types stay
`UnityPBRNode` and `SceneLightingNode`.

Unity PBR is fragment-only, metallic workflow: inputs Base Color, Normal (World),
Metallic, Smoothness, Occlusion, Emission; output Color. It calls URP's native
`UniversalFragmentPBR` with the current fragment context. It does not apply fog,
exposure or framebuffer blending. Alpha belongs to the final output, so lighting
mixes in the graph remain unpremultiplied.

Scene Lighting is fragment-only: input Normal (World); outputs Main Direction,
Main Color, Main Distance Attenuation, Main Shadow Attenuation, Additional Diffuse,
Baked GI, Vertex Lighting, Shadow Mask, Direct AO, Indirect AO. Main Color already
includes cookies and URP direct AO; attenuation remains separate. Additional
Diffuse includes Lambert, shadows, cookies, rendering-layer filtering and direct
AO. Vertex Lighting is separately exposed for the per-vertex additional-light mode.
Baked GI includes mixed-light subtraction; indirect AO remains separate.

## Generation

Obtain native Lit pass descriptors from the installed engine and copy before
modification. Preserve lighting variants, instancing, APV includes, render states,
and auxiliary passes. Forward is tagged `UniversalForwardOnly`; remove GBuffer
and 2D passes. Retain ShadowCaster, DepthOnly (when enabled by native target),
DepthNormalsOnly, MotionVectors, optional XR MotionVectors, Meta, selection/picking.
Native vertex deformation and Alpha/Clip expressions remain shared.

Initialize fragment lighting context before SurfaceDescriptionFunction, including
in auxiliary passes. Nodes resample GI for their own normal without mutating the
shared context. Main/additional shadows, Forward+ clustered lights, light layers,
cookies, lightmaps, shadowmask, SH/APV, reflection probes and SSAO follow installed
URP APIs. Previews use a deterministic local-light fallback. Final color applies
exposure and distance/volumetric fog once and never calls another lighting function.

Version contract: exactly Shader Graph and URP 17.7.0. Runtime shaders have no
WhimTex dependency. This directory is staging source until the package is installed.
No Unity compilation or rendering has been run by this backend author.

## Validation handoff

Static source-structure and local-include checks passed for the four C# and six
HLSL files. The adapted Varyings file was compared to installed 17.7.0: only its
notice/include header, two GI gates and the pre-surface context hook differ.
Native constructor, override, collection and slot signatures were checked against
the installed C# sources. These checks do not establish successful compilation.

Parent owns sequential Unity import/compilation and rendering validation. Check:

- Factory owner/message manager, custom Color block, serialization roundtrip,
  importer output, node discovery, settings Undo/Redo and both node previews.
- Generated forward shader plus ShadowCaster, DepthOnly, DepthNormalsOnly, Meta,
  motion vectors and picking with shared deformation and alpha clipping.
- Forward and Forward+; main cascaded/screen shadows; additional point/spot and
  clustered directional lights; soft shadows, cookies and rendering layers.
- Static/dynamic directional lightmaps, shadowmask/subtractive mixing, SH and
  APV including probe occlusion; reflection probes, SSAO and decals.
- Opaque/transparent blend modes, exposure and fog, instancing, stereo and the
  project's graphics APIs. Compare UnityPBR against native Lit at the same inputs.
- Wire a shared world normal into both the PBR node and NormalWS output when
  comparing normal maps; wire albedo/emission to their separate baking outputs.

Auxiliary passes retain lighting variants to evaluate lighting-dependent alpha;
this increases variant count. Camera-dependent alpha (view direction, screen
textures or lighting) remains camera-dependent, including in shadow views, as in
native graphs. The independent Meta inputs avoid baking already-lit radiance.

### Explicit GPU smoke entry point

`DCFApixels.WhimShader.WhimShaderURPValidation.Run()` is public and returns an
object with checks, compiled pass names, measured pixel values and scope. Run it
on the Editor main thread in Edit Mode after import finishes, with an active URP
asset and Main Light set to Per Pixel. It does not change project settings.

It generates four temporary real shaders, calls `ShaderUtil.CompilePass(material,
pass, true)` sequentially for every pass, and renders eight images through
`PreviewRenderUtility.Render(allowScriptableRenderPipeline: true)`. Pixel assertions
check main-light response for both lighting nodes, half-quad UV alpha clipping
from both sides, and a single-sided backface-culling negative control. Non-finite
or error-magenta pixels fail. All temporary graph owners, shaders, materials,
mesh, readbacks and preview-scene objects are disposed; async compilation state
and active render target are restored. No files/assets are saved.

This smoke compiles current-keyword pass entrypoints and tests the current URP
renderer/API. It does not claim exhaustive keyword, auxiliary-pass pixel,
additional-light, shadow, GI, Forward+, XR or graphics-backend coverage. It has
not been executed by this author; parent runs Unity validation.

## Shared preview renderer

`Editor/URP/WhimPreviewRenderer.cs` defines an internal sealed `IDisposable`
renderer with a parameterless constructor, using the existing
`WhimShaderPreviewMode` / `WhimShaderPreviewChannel` enums from the UI node files.

```csharp
void RenderMaterial(Material material, MaterialPropertyBlock properties,
    WhimShaderPreviewMode mode, RenderTexture target, bool useSRP);
void Render(RenderTexture source, RenderTexture target,
    WhimShaderPreviewMode mode, WhimShaderPreviewChannel channel, float heightScale);
void Render(RenderTexture source, RenderTexture target, WhimShaderPreviewMode mode,
    int channelMask, HeightPreviewRenderer.Channel heightChannel, float heightScale = .25f,
    bool heightTexture = true, bool highlightOutOfRange = false, bool encodeGamma = true,
    Vector2Int? canvasSize = null);
```

Call on the Editor main thread. `RenderMaterial` draws the actual material on a
built-in sphere for ThreeD, or a full-frame quad for Flat/Height. Set `useSRP` true
for the final native URP shader and false for a native per-node preview shader.
Pass the native preview property block, including the selected node output.
Camera animation time follows that block's `_TimeParameters.x`; when no block is
supplied it uses `Time.realtimeSinceStartup`.
The renderer does not generate graphs, change material properties, or use reflection.
Its isolated `PreviewRenderUtility` scene uses transparent clear and a depth-backed
HDR RGBA intermediate, followed by an alpha-preserving copy into the supplied
target. No checkerboard or BlitNoAlpha is applied. Alpha is the shader's framebuffer
output, including its own blending and clip semantics; the service must preserve
Vector4 alpha in the native node-source adapter before rendering.

`Render` now uses the WhimTex display implementation, replacing the earlier mesh
terrain visualization. Flat/ThreeD use the copied `DisplayChannels.shader` math in
`Shaders/WhimPreview.shader` for an arbitrary RGBA mask (0..15). A separate raw-copy
pass preserves signed/HDR RGBA before display processing. ThreeD must receive the
actual sphere render; it never maps a flat texture onto a sphere.

Height calls the copied `Editor/URP/HeightPreviewRenderer.cs`,
`Shaders/WhimHeightPreview.compute` and `Shaders/WhimHeightPreviewDisplay.shader`.
This is WhimTex's fixed horizon projection, without mesh, camera or orbit controls:
the projection constants, occlusion scan, finite-value handling, normals, lighting,
texture toggle, source alpha, channel masking and magenta out-of-range highlighting
are preserved. Signed/HDR heights are not clamped; scale is clamped to [-4,4], with
.25 fallback for a non-finite scale. Height Channel is independent of display mask:
R/G/B/A/Luminance, default R. Other WhimTex defaults are scale .25, Texture on,
highlight off, mask 15. Nodes pass encodeGamma=false; final preview passes true.

The copied renderer's original Render/Display split and Supported/Geometry/Output
properties remain available for UI geometry caching. Only namespace, package paths
and WhimTex-specific profiling wrappers were adapted. Its geometry/output RTs are
square ARGBFloat; compute dispatches one 64-thread group per column and uses
source.height*2 horizon steps. Use a square Height display RT with Image.ScaleToFit.
Keep raw RT dimensions unchanged when switching Flat/Height. `canvasSize` is the
logical source footprint, analogous to WhimTex document dimensions; absent a canvas,
pass raw width/height. It is not the panel size. The generic renderer also fits the
square result into rectangular targets without stretching.

`HdrColor.cginc` and its `HueGradient.cginc` include are byte-for-byte local copies;
only SpriteEncode is used by display, so the unused hue helper needs no LUT binding.
Provenance and the full MIT license are in `Shaders/WhimTex-NOTICE.md`. There is no
runtime WhimTex dependency. Source comparison checks preserve the compute byte-for-byte,
the Height display except its shader name, and all Height renderer math.

Textures must be nonempty Tex2D; targets must be single-sampled, and raw material
targets must have alpha. Source==target is handled with a temporary copy. The
caller owns all materials, blocks and RTs. Dispose the renderer when its view or
window closes; resources also clean up before reload and Editor quit. No saved
scene, asset, or project setting is modified by the renderer. The initial Unity
checks passed all 32 tested URP pass compilations and 232 Height configurations
against WhimTex with maximum pixel difference 0; see the preview integration notes.
