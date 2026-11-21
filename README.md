# WhimShader

URP shader authoring with the WhimTex workspace and Unity Shader Graph's graph model, node implementations, stage validation and ShaderLab generator.

## Requirements and installation

The first version targets **Unity 6000.7 and URP / Shader Graph 17.7.0**. It has been exercised in Unity 6000.7.0a6 on Direct3D 12. Other package versions are rejected explicitly rather than silently generating incompatible shaders.

Install this repository with Package Manager → Add package from Git URL:

```
https://github.com/DCFApixels/WhimShader.git
```

WhimTex is not a dependency. Its MIT-licensed workspace, visual controls, icons and catalog thumbnail atlas are included here.

## Authoring

Use **Assets → Create → WhimShader → Shader Graph**. A `.whimshader` file imports as a usable Shader asset. Open it, edit the graph, save, and create a material with **Create Material**. **Export ShaderLab** writes a readable `.shader` and its parameter resources; exported shaders still reference the installed URP, Shader Graph and WhimShader HLSL includes.

- **Parameters** defines graph inputs and Group, Foldout, Tabs and Tab organization.
- **Inspector** edits parameter defaults and selected-node settings.
- **Nodes** and **Create Node** use the WhimTex catalog, category colors, icons, compact mode and shared thumbnail atlas.
- **Shader Settings** controls surface type, blending, clipping, render faces, depth, shadows and material overrides.
- Panels can be detached, snapped, combined as tabs, or split horizontally/vertically. The layout persists per user.
- The orange graph icon distinguishes WhimShader assets and windows from Unity Shader Graph.

The node catalog comes from the installed Unity packages, including dynamic parameter, keyword, custom-interpolator and Sub Graph entries. Nodes unavailable for this URP target remain unavailable. Native node algorithms and names are preserved. Extensions include **SoftSign**, **Inverse SoftSign**, **Unity PBR**, **Scene Lighting**, **Sample Gradient LUT** and **Sample Curve LUT**.

## Lighting and stages

The Fragment output accepts **Color, Alpha and Alpha Clip Threshold**. Connect **Unity PBR** to Color for URP lighting, or supply a color directly for an unlit result. Lighting is an ordinary node, so additional color/math operations can follow it.

**Scene Lighting** exposes main-light direction/color/attenuation, additional-light diffuse contribution, baked GI, vertex lighting, shadow mask and ambient-occlusion terms. Availability still depends on the URP renderer and its enabled features. Unity PBR follows URP's light loops and BRDF.

Vertex Position, Normal and Tangent use the same graph. A shared branch is evaluated in each stage that consumes it. Fragment-only dependencies are validated transitively; use a native custom interpolator when a vertex-computed value must be passed to Fragment.

The target is forward-only, including when used in a deferred renderer. It emits shadow, depth, depth-normal, motion-vector, meta, scene-selection and picking passes where applicable. Optional **Baked Albedo / Baked Emission** inputs describe lightmapping separately from the final lit Color; **Normal (World) / Smoothness** describe the depth-normal output. A PBR result is not treated as an albedo for baking.

## Gradients and curves

Native compile-time Gradient inputs remain available. Editable **Gradient LUT** and **Curve LUT** parameters store their authored keys and bake a linear floating-point one-dimensional texture. The matching sample node accepts normalized `T`; values outside `[0,1]` clamp to the endpoints. Sampling is a finite-resolution approximation, including for fixed gradients and sharp curve transitions.

The same LUT can be sampled in Vertex and Fragment with explicit LOD. Imported graphs retain default textures as subassets. Material edits create independent LUT data when required, with Undo/Redo support. Export produces persistent LUT assets beside the shader. `WhimLutTexture` supports runtime creation, update and binding to a Material or MaterialPropertyBlock without recompiling the shader. See [parameter details](Editor/Parameters/README.md).

## Previews

Node and final previews have **Image**, **Height** and **3D** presentation and **RGBA / R / G / B / A** channel selection. Nodes with multiple outputs can select the displayed output. Presentation settings are stored in the graph. Height reuses WhimTex's horizon renderer, height-channel/scale/texture/range settings and preview controls; it does not change the generated shader. Final RGBA buttons support independent channel combinations and drag toggling. 3D evaluates the actual shader on a mesh.

## Custom Function

Create **HLSL → Custom Function** to start with `Out = In;`. Edit the code directly on the node, configure inputs/outputs under **Ports**, then press **Compile** (or Ctrl/Command+Enter). The CodeArea includes syntax coloring, line numbers, search, selection indentation, automatic indentation and Undo/Redo. Drag its lower-right handle to resize it.

**Body** contains statements inside a generated function and supports `$precision`. **File** selects a project/package HLSL file and an entry function; ports are discovered from its signature, including matrices, resource types, return values and `inout`. **Source** contains complete function definitions and helpers. **Convert to Code** embeds a file for editing and preserves its relative include locations. **Refresh** rescans source; in File mode it also recompiles the function.

Code and port edits remain a saved draft until compilation succeeds. An invalid draft leaves the applied function and its connections intact. Renaming a body port keeps its ID; removing a port or changing its direction removes that port's existing connections when applied. File discovery reads declarations without evaluating preprocessor branches; ambiguous entry declarations require an unambiguous wrapper.

## Integration boundary

WhimShader does not patch Unity package files or use reflection to access their private members. Its Editor sources compile through an assembly reference into URP.Editor, which already has Shader Graph friend access. Its small runtime LUT helpers use URP.Runtime's assembly reference so the Editor integration can reference them without altering Unity's asmdefs. This is a version-pinned internal API dependency, not a promise of compatibility with arbitrary future Unity releases.

The serialized graph uses Unity's native MultiJson format and node versions. WhimShader-only presentation and parameter-layout nodes carry no shader code. WhimShader-authored `.shadersubgraph` files remain native Sub Graph assets and reopen in WhimShader.

## Validation

Explicit Editor checks live in `Editor/Validation`, `Editor/URP/WhimShaderURPValidation.cs` and `Editor/Parameters/WhimLutValidation.cs`. They cover stage validation, graph serialization, lighting and clipping on GPU, LUT sampling in both stages, parameter copy-on-write, Undo/Redo and resource export. They do not run on import. Tests may create isolated fixtures under `Assets/WhimShaderValidation`; they do not change the user's scenes or pipeline settings.

The initial Unity validation passed 6 graph/stage checks, 6 document/presentation checks, 9 LUT checks and all 32 tested URP pass compilations. The copied Height renderer matched WhimTex in 232 configurations (696 GPU comparisons; maximum difference 0). Image/Height/3D, output selection, alpha, preview collapse, save/reopen and idle render counters were checked on isolated graphs.

No player build is part of the current validation. Shadow/GI/Forward+ variants and auxiliary-pass pixel results are not an exhaustive cross-platform certification.

See [third-party notices](ThirdPartyNotices.md) for the MIT and Unity Companion source boundaries.
