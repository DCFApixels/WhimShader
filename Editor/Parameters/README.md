# Gradient and Curve LUT integration

All types are in `DCFApixels.WhimShader`. Editor sources compile in the parent's
`Unity.RenderPipelines.Universal.Editor` asmref. Runtime sources use
`WhimShader.Runtime.asmref`, targeting the installed URP Runtime GUID
`15fc0a57446b3144c949da3e2b9737a9`. No external assembly definitions are modified.

## Native graph types

- `WhimGradientShaderProperty : Texture2DShaderProperty`, blackboard **Gradient LUT**.
- `WhimCurveShaderProperty : Texture2DShaderProperty`, blackboard **Curve LUT**.
- `SampleGradientLutNode`, **Input/Gradient/Sample Gradient LUT**: Texture2D `LUT`,
  scalar `T`, float4 `Out`.
- `SampleCurveLutNode`, **Input/Curve/Sample Curve LUT**: Texture2D `LUT`, scalar
  `T`, scalar `Out`.
- `SoftSignNode` / `InverseSoftSignNode`, **Math/Advanced/SoftSign** and
  **Math/Advanced/InverseSoftSign**: dynamic float1–4 `In`/`Out`.

All four custom nodes use `AbstractMaterialNode` and `IGeneratesBodyCode` with
direct HLSL generation. Slots retain the native default `ShaderStageCapability.All`.
There is no custom reflection, including no `GetMethod`. Native discovery is unchanged.

SoftSign computes `x / (1 + abs(x))`; inverse SoftSign computes
`x / (1 - abs(x))`. The inverse domain is **(-1,1) for every component**.
There is no clamp or epsilon guard. Inputs ±1 are singular; values outside the
domain are not the mathematical inverse. Shader precision/exception behavior is native.

## Import and preview hooks

`WhimLutGraphProperties` is assembly-internal because native `GraphData` is internal.
The parent editor/importer in the same asmref can call these exact static methods:

```csharp
IReadOnlyList<WhimLutAsset> BakeForImport(GraphData graph, AssetImportContext context);
void PreparePreview(GraphData graph);
void ReleasePreview(GraphData graph);
IReadOnlyList<WhimLutAsset> ExportDefaults(GraphData graph, string assetFolder);
```

Call `BakeForImport` once per import **after validation/promotion and before creating
the native `Generator`**. It adds stable `WhimLut:<property.objectId>:Data` and
`:Texture` subassets, then sets the native Texture2D property value. Native
`generatedShader.assignedTextures` therefore contains the LUT defaults. Apply
those defaults using the parent's normal native import path; the hook does not
create or compile a shader. It returns the metadata assets if the parent needs them.

Call `PreparePreview` for an open graph before preview refresh. The overridden
native `GetPreviewMaterialProperty` also resolves textures lazily. Transient
preview textures never enter serialized property values. Call `ReleasePreview`
before discarding/replacing the graph. Domain reload and Editor shutdown also
release previews. All hooks and texture operations run on the main thread.

## Material editing and export

Native ShaderLab declarations include `[WhimGradient]` or `[WhimCurve]`, resolved
by Unity's standard `MaterialPropertyDrawer` mechanism. The parent can keep its
normal URP ShaderGUI; it must draw properties through `MaterialEditor.ShaderProperty`
to retain the drawers. A custom ShaderGUI is not installed by this extension.

```csharp
WhimLutAsset WhimLutMaterialStore.Apply(Material material, string propertyName, WhimLutData data);
WhimLutAsset WhimLutMaterialStore.Find(Texture texture);
void WhimLutMaterialStore.UpdateAsset(WhimLutAsset asset, WhimLutData data);
string[] WhimLutMaterialStore.CollectExportAssets(IEnumerable<Material> materials);
```

`Apply` requires a saved editable `.mat` under `Assets`. First edit creates a unique
adjacent `.lut.asset` containing `WhimLutAsset` metadata and a Texture2D subasset.
Later edits rebake the same owned texture. Editing a duplicated material, a shared
LUT, or a graph default creates an independent material asset rather than altering
the original. Multi-material editing creates/updates each material's own LUT.
Keys, gradient mode, curve tangents/weights and wrap modes, resolution, property
name, and owning material persist. Undo restores data and rebakes pixels. Undoing
the first material assignment can leave the created sidecar unreferenced; it is
not automatically deleted. The metadata asset also has a native-field inspector.

For standalone ShaderLab export, call `ExportDefaults(graph, existingAssetsFolder)`.
It creates unique persistent data/texture assets without changing graph properties.
Use each result's `PropertyName` and `Texture` with `ShaderImporter.SetDefaultTextures`,
then save/reimport the exported shader through the parent export flow. Include these
assets in the export. `CollectExportAssets` returns native asset dependency paths
for saved materials, including their shader and LUT sidecars; the parent chooses
the packaging format and handles package dependencies. No export is executed by
this implementation task.

## Runtime updates and sampling contract

```csharp
var data = new WhimLutData { kind = WhimLutKind.Curve, curve = curve, resolution = 256 };
Texture2D texture = WhimLutTexture.Create(data);
WhimLutTexture.Bind(material, "_CurveLUT", texture);

data.curve = changedCurve;
WhimLutTexture.Update(texture, data); // same resolution; updates pixels only
// The same string-name Bind overload accepts MaterialPropertyBlock.
// Apply the block to the renderer through the caller's existing ownership path.
```

The caller owns runtime-created textures and releases them with
`WhimLutTexture.DestroyOwned` after unbinding. Runtime update does not mutate
imported/shared assets unless the caller deliberately passes their texture.
Resolution changes require creating/rebinding a replacement runtime texture.
String-name `Bind` updates both the texture and `<name>_TexelSize`, including for
MaterialPropertyBlock. Integer-ID overloads bind only the texture; callers using
them must maintain the texel-size vector when dimensions change.

LUTs are readable, linear **RGBAFloat**, one row, no mipmaps, bilinear, clamp,
resolution 2–4096 (default 256). Target platforms must support this texture format
and filtering. Values are baked at `i/(width-1)`, without clamping HDR, alpha, or
negative curve values. `T` is clamped to [0,1] and mapped to texel centers; shader
sampling uses explicit LOD 0 for vertex and fragment use. Curve output reads red.

The LUT is a finite-resolution approximation, including weighted curves and
gradient interpolation modes. Fixed/stepped transitions may interpolate across
one texel interval. Curve wrap modes affect baking inside the normalized interval;
shader inputs outside that interval clamp to its endpoints. Remap time explicitly
when another domain is needed. Native Unity Gradient nodes remain available and
are not replaced by these material-editable LUT properties.

## Explicit validation runner

Run once on the Unity Editor main thread in idle Edit Mode:

```csharp
Debug.Log(DCFApixels.WhimShader.WhimLutValidation.Run());
```

The public `static string Run()` returns JSON with `passed`, `fixtureFolder`, and
individual `checks` (`name`, `passed`, `detail`). It also writes `report.json` in a
fresh `Assets/WhimShaderValidation/LUT_<guid>` folder. Fixtures remain for inspection.
Nothing runs automatically. The runner uses native Unlit independently of custom
lighting and exercises the parent's installed `.whimshader` importer.

Nine checks cover native graph property/node/edge serialization and independent
copies; HDR/signed pixel values and weighted curve samples; preview reuse/update/
release without serialized transient textures; SoftSign float1–4 inference;
both Gradient-vertex/Curve-fragment and reversed imported graph configurations;
material copy-on-write, resolution-changing Undo/Redo and disk reopen of material,
metadata and texture; standalone export with ShaderImporter defaults and asset
dependencies; GPU readback of actual node-generated code in both shader stages.
GPU comparisons include clamped endpoints, intermediate LUT interpolation,
SoftSign values, inverse roundtrip, and a runtime texture update with the same shader.

The caller's execution imports fixture assets and synchronously compiles passes
for the current graphics device. It temporarily disables asynchronous shader
compilation and restores the previous setting. Undo operations target a separate
fixture group; like any new edit they may discard existing Redo history. No global
Undo clear is used. Float GPU format absence is an explicit failed check, not a
skipped success. This is not an all-platform/keyword-variant compiler, UI automation,
or an Editor-restart test; reopen unloads and reloads saved fixtures in-process.

Preparation was checked against local source APIs without invoking Unity. Parent
reported successful native shader import; execution of this runner remains with
the parent. A prepared check is not a reported PASS.
