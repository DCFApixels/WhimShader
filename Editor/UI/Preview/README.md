# Preview integration

All APIs are in `DCFApixels.WhimShader`. Lifecycle hooks in the window partial:

```csharp
InitializePreviews(); // after editor, workspace and native UpdateMasterPreview
UpdatePreviews();     // after HandleGraphChanges, before Graph.ClearChanges
DisposePreviews();    // first in DisposeView, before native editor/workspace/LUT cleanup
```

The service owns `WhimShaderNodePresentation`; do not attach a second presentation.
`window.PreviewService.SetEdgeConnectorListener(listener)` forwards the window's
catalog edge listener to the node presentation without exposing its private owner.
It retains the native MasterPreviewView in the hierarchy with `display: none` and
restores its display on disposal. It neither disposes nor drives PreviewManager.
The native master compilation/render lifecycle remains active. Our raw rerender
adds work after native updates; it is needed because native RTs contain checkerboard
and `BlitNoAlpha`. Static entries do not redraw in idle; dirty callbacks, native
completion notifications, size/settings changes and time-dependent branches trigger work.

Native material borrowing covers the first float1/2/3/bool output. Own shader source
is needed for another selected output, float4 alpha, subgraph outputs, and missing
master blocks whose defaults live in the native manager's private MPB. That last
case uses native Generator's public `temporaryBlocks` with its matching owned
shader, never private reflection. Existing native compilation errors are reported;
the service does not bypass them. Owned compilation warms only the forward pass
(node preview has one pass); all-pass validation is separate.

Presentation contexts are reconciled after Attach and on Update, with explicit
creation/removal subscriptions. They do not depend on a native Image existing or
remaining attached. Eligibility comes from `WhimShaderNodePresentation.SupportsPreview`.
If an eligible node has no native compilation path (for example, a nonnumeric first
output), the service uses native Generator directly instead of waiting forever for
its uncompiled PreviewShaderData. The native `hasError` flag is still respected.
The copied node UI and final panel own their controls, collapse behavior and
loading indicators. Pending work calls `SetUpdating(true)` and preserves old pixels;
`SetMessage` is reserved for actual errors. The service creates no preview chrome.

`WhimShaderPreviewSource` is pinned to SG 17.7. It requires exactly one guarded
`surface.Out` assignment; mismatch produces an explicit error. Native adaptation
and finite-magenta guard are preserved, except float4 retains its actual alpha
instead of native's forced 1. Resource/matrix outputs receive a diagnostic.

Node material rendering uses built-in rendering; final material rendering uses
URP. `ThreeD` renders the actual material on a sphere. Flat and Height first render
the actual material on a quad; Height uses the copied WhimTex horizon compute and
fullscreen display shaders. There is no terrain mesh or height saturation.
Height channel is independent of the display channel mask: defaults are R, scale
0.25, textured=true and highlightOutOfRange=false. Scale accepts -4 through 4;
height channels are R/G/B/A/Luminance. The RGBA display mask accepts 0 through 15,
using the original WhimTex DisplayChannels behavior. Final alpha means rendered
framebuffer alpha, including the material's blend/clip rules. It is not an isolated
pre-blend alpha expression. Height retains the copied renderer's alpha rules.

Raw targets are linear HDR RGBA; display targets follow native ARGB32/default color
space. Renderer `encodeGamma` is false for node previews and true for final previews,
matching WhimTex. Height display RTs are square, fitted by Image.ScaleToFit; switching
Flat/Height preserves the raw RT and source footprint (`canvasSize=raw dimensions`).
The shared renderer handles output conversion. RTs and materials are reused
until size/source/lifetime changes. Async compilations finish before a new source
is submitted for the same owned shader. No blocking waits or per-frame source scans.
All graph preview properties use native collection/MPB assignment, plus explicit
Texture2D texel size. A single time value is used throughout each service update.

Modes/channels/output IDs persist through presentation metadata, including `$final`.
The final panel uses shared floating-panel anchoring, drag and resize, and existing
workspace layout persistence. Shader graphs have one final shader output; subgraphs
expose their output ports. Preview settings are never shader/material asset changes.

## Validation

Explicit, nonblocking APIs executed in the Unity Editor:

```csharp
WhimShaderPreviewValidation.Begin();
// Allow Editor frames / native compilation to finish.
WhimShaderPreviewValidation.Snapshot();
WhimShaderPreviewValidation.Step("Flat", "RGBA", 4);
// Allow frames; Snapshot.ready must become true before comparing.
WhimShaderPreviewValidation.Snapshot();
WhimShaderPreviewValidation.Step("Height", "A", 4);
WhimShaderPreviewValidation.SetHeight("A", .25f); // independent height channel
WhimShaderPreviewValidation.SetChannels(7);     // original RGB mask, alpha disabled
WhimShaderPreviewValidation.Step("3D", "R", 5); // separate steps, warm/snapshot each
WhimShaderPreviewValidation.SetRGBA(.8f, .3f, .1f, .6f);
WhimShaderPreviewValidation.End();
```

`Step(mode, channel, outputSlotId=4, subject="all")`: modes Flat/Height/3D,
channels RGBA/R/G/B/A, Combine outputs RGBA=4/RGB=5/RG=6. Subjects are
`all`, `rgba`, `combine`, `native`, `final`. Step changes the display channel and
preserves the independent Height settings. `SetHeight(channel="R", scale=.25f,
textured=true, highlightOutOfRange=false, subject="all")` and
`SetChannels(mask, subject="all")` exercise the same persisted data as the copied UI.
Begin creates a fresh saved native URP Unlit graph with `_PreviewRGBA=(.2,.4,.6,.25)`,
expanded native Add(float4), Combine and Add(scalar) previews, and transparent final.
Property and Split are collapsed source helpers: neither has native `hasPreview`.
The scalar Add checks native material borrowing; Combine checks output selection;
float4 Add checks alpha preservation. Begin opens only its own fixture window.
End closes that window and
retains the original graph and snapshots; experimental metadata edits are discarded.

Snapshot returns JSON and saves PNGs/JSON under `Temp/WhimShader/PreviewValidation`.
It records root RGBA, graph hash, persisted settings, native state, visibility,
errors/pending status, shader/RT ownership, compile/raw/display counters, float
readback hashes, center RGBA and alpha extrema. PNGs are clipped debug images;
hashes use original float readbacks. `ready` is warm/completed status, not a test PASS.
It reports expected/presentation/service binding counts, missing bindings, image
visibility/attachment/tint and effective display/height settings. Zero node bindings
or a missing expected binding always means `ready=false`. Offscreen/collapsed entries
are included without forcing renders; Begin's three fixture previews must render.
`SnapshotWindow()` inspects the focused existing WhimShader window without creating
a fixture, for example the current PBR preview. No private reflection is used.

Check each mode/channel combination after warm frames. Flat float4 raw center should
retain alpha .25; scalar Add raw center is R=.2 replicated to RGB with A=1 and
`ownsShader=false`. Combine's RGBA output retains alpha .25; RGB/RG use native
preview adaptation with alpha 1.
Channel A on float4 should show grayscale .25. Switching only channels should not
compile or rerender the raw material. Flat ↔ Height should reuse the raw quad.
Switching to 3D should change the raw image geometry and preserve transparent
background; Height should change display geometry without changing raw samples.
Compare two idle snapshots: render/build counters and image hashes must stay stable.
Verify root values remain unchanged when changing presentation, persistence after
save/reopen, time updates only downstream, and resource release on window close.

## Reported Unity validation

Checked in Unity 6000.7.0a6 / Direct3D12 on 2026-11-22:

- The fixture has all 3 expected node preview bindings. Image, Height and 3D
  complete without preview errors. RGBA retains (.2, .4, .6, .25); transparent
  final raw RGBA is (.05, .1, .15, .25), matching material blending.
- Alpha selection does not build another shader. One extra raw render occurred
  during the transition, so strict channel-only raw-render stability is not claimed.
- Selecting Combine's RG output produces (.2, .4, 0, 1). Changing the input to
  (.8, .3, .1, .6) updates every preview and the blended final result correctly.
- The copied Height renderer matches original WhimTex shaders in 232 cases /
  696 comparisons, with maximum absolute difference 0. These cover every channel
  mask, all five height channels, height scale, texture, range highlight and gamma.
- A saved fixture reopens with 3D modes and Combine output 6 intact; all previews
  are ready. Two idle snapshots retain 3 shader builds / 4 raw / 4 display renders.
- Document validation covers node/final modes, channel masks, output selection,
  Height settings and collapsed state through Undo/Redo and serialization.
- UI validation finds 22 native ports with no replacement errors, all four final
  RGBA controls inside their panel, and no legacy preview-mode dropdown.
- The actual collapse/expand buttons respond to UI Toolkit submit events. Connected
  labels highlight the expected branches; release-click, downstream selection,
  Ctrl addition and cancellation on mouse movement were checked on the fixture.
- The existing Whim Surface fixture renders its final output, Unity PBR and UV
  previews without errors. All 32 URP pass checks pass in the current keyword state.

These checks do not certify every native node, animated graph, graphics backend,
or URP keyword combination. No player build was run.
