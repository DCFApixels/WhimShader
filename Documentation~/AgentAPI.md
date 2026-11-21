# Compact graph authoring

The Editor API uses the same native model, catalog and validation as the UI. Pipeline is optional; when installed, it exposes:

| Command | Purpose |
|---|---|
| `whimshader_sessions` | Open graph paths, revisions, unsaved status |
| `whimshader_catalog --query noise --limit 10` | Bounded node discovery |
| `whimshader_catalog --node "Gradient Noise"` | One node's ports, defaults and settings |
| `whimshader_inspect --path Assets/Example.whimshader` | Only nodes and connections; uses unsaved data when open |
| `whimshader_inspect --path ... --details true` | Include input values, settings and parameter definitions |
| `whimshader_graph --requestPath Temp/graph.json` | Apply a JSON operation |
| `whimshader_preview --path ... --node noise --output Out` | Render one node to a PNG and return its absolute path |

Append `--project-path <absolute project path> --format json` to CLI calls. Check **result.success**, not only the transport envelope. C# equivalents are `WhimShaderApi.Sessions()`, `Catalog(query, node, limit)`, `Inspect(path, details)`, `Execute(json)`, `ExecuteFile(requestPath)` and `Preview(path, node, output, size, mode, channel, time)`. They return compact JSON strings and must run on the Editor main thread.

## Create without positions

```json
{
  "op": "create",
  "path": "Assets/Example.whimshader",
  "parameters": {"Tint": {"type": "Color", "value": [0.8, 0.35, 0.1, 1]}},
  "nodes": {
    "uv": "UV",
    "noise": {"type": "Gradient Noise", "in": {"UV": "uv", "Scale": 8}},
    "tint": {"property": "Tint"},
    "colored": {"type": "Multiply", "in": {"A": "noise", "B": "tint"}}
  },
  "fragment": {"Color": "colored"}
}
```

The folder must exist. Create saves a new `.whimshader`, never overwrites, and starts with URP output contexts without the default PBR branch. Add `Unity PBR` for lighting. Shader settings accept `surface`, `blend`, `alphaClip`, `castShadows`, `receiveShadows`, `materialOverride`; surface/blend use native enum names.

Node keys are persistent aliases, retained across save/reopen and Undo/Redo. Forward references are allowed. Full catalog paths disambiguate names; exact node names precede search synonyms. `vertex` and `fragment` are reserved.

Connections use `"alias.Port"`; `"alias"` suffices for a single-output node. Inputs also accept numbers, vector arrays, booleans or `{"asset":"Assets/Texture.png"}`. Missing inputs retain native defaults, including automatic UV/position bindings. A literal disconnects the input before assigning its default; `null` disconnects and preserves its saved default. Normal Shader Graph dimension conversions and stage constraints apply.

Alternatively: `"edges":[["uv.Out","noise.UV"],["colored.Out","fragment.Color"]]`. `vertex.Position`, `vertex.Normal`, `vertex.Tangent` and active fragment blocks address output contexts. Inspection uses the same endpoint names.

## Read and patch economically

Default inspection returns `path`, `revision`, `nodes` (alias/ID → type), `edges`, `success`. It omits coordinates, hidden metadata, preview state and native serialization. Request details for a subset:

```json
{"op":"inspect","path":"Assets/Example.whimshader","ids":["noise"],"details":true}
```

Only selected node definitions and their incident wires are returned. External endpoints remain identifiable. Detailed input values are stored defaults, not GPU-evaluated pixels. Use Catalog for all available ports.

Manually created nodes without an alias use unique short IDs such as `#a012bc34`. These IDs work in patches and wire references; full native IDs remain accepted. The prefix length increases if needed to avoid collisions.

```json
{
  "op": "patch", "path": "Assets/Example.whimshader",
  "expectedRevision": "COPY_FROM_INSPECT",
  "nodes": {
    "noise": {"in": {"Scale": 12}},
    "sharper": {"type": "Power", "in": {"A": "noise", "B": 2}},
    "colored": {"in": {"A": "sharper"}}
  }
}
```

Existing nodes accept aliases or native IDs. Only supplied fields change. `remove:["alias"]` deletes nodes; `disconnect:["node.Input"]` removes individual wires. Type replacement requires explicit remove + create. Existing `.shadersubgraph` files can be inspected/patched through normal output-node ports; create currently creates full shaders.

Open graphs receive one Undo operation; patch does not save by default. Closed graphs require `save:true` or `dryRun:true`. Dry run validates and lays out without applying/writing (a supplied Custom Function may still be compiled for validation). Mutations require the current `expectedRevision`; rejected requests leave the graph unchanged. After a timeout inspect before retrying. Inspect again after manual edits, Undo/Redo or preview-presentation changes.

## Layout

- Create defaults to `layout:"all"`; patch to `layout:"new"`.
- New nodes without `position` arrange left-to-right by dependency. Existing and explicit `[x,y]` positions remain fixed.
- `layout:"none"` skips arrangement. `layout:"selected"` with `ids` arranges a subset.
- `op:"arrange"` arranges the full graph, including output contexts.
- UI **Arrange → All Nodes / Selected Nodes** uses the same planner with Undo.

The planner comes from WhimTex: port-order sweeps, bounded crossing reduction and obstacle avoidance. Groups stay together; other nodes and notes are obstacles. Open windows supply measured node/field bounds where available. New/closed graphs use conservative bounds, including external fields and CodeArea. Dense graphs can retain wire crossings. Layout does not alter shader calculations.

## View a node preview

```json
{"op":"preview","path":"Assets/Example.whimshader","node":"noise","output":"Out","size":256,"mode":"Image","channel":"RGBA","time":0}
```

Or call `whimshader_preview --path Assets/Example.whimshader --node noise`. Pass an alias, short `#ID` or full node ID from inspection. `output` accepts a port name or numeric ID. Omit output/mode/channel to use the node's saved preview selection. Modes are `Image`, `Height`, `3D`; channels are `RGBA`, `RGB`, `R`, `G`, `B`, `A`. Size is 32–1024 square pixels (default 256); time is explicit seconds (default 0).

Open the returned absolute `imagePath` with the agent's image viewer. PNGs are temporary files under `Temp/WhimShader/Previews`, not imported assets; they disappear when that folder is cleaned. A response also identifies the graph revision, node and output. RGBA PNGs preserve alpha; choose RGB for an opaque color view.

The request works for closed graphs, collapsed nodes and hidden previews. Open graphs use unsaved state. It renders on demand using the existing native preview generator and WhimShader renderer, without changing the document or saved preview settings. No background capture or per-frame polling is needed. A cold request can compile a shader; allow sufficient CLI timeout. This is an editor preview, not a scene render. Resource/matrix outputs need sampling/conversion to numeric data first. Compilation errors return `preview_compile_failed` instead of an image.

## Configuration

Parameter types: `Float`, `Float2/3/4` (also `Vector2/3/4`), `Color`, `Boolean`, `Texture2D/3D/2DArray`, `Cubemap`, `Matrix2/3/4`, `Gradient`, `Gradient LUT`, `Curve LUT`. Creation accepts `name`, `reference`, `exposed`; references default to `_` plus the alias. Updates omit `type` and accept `value`/`exposed`. Matrix arrays are row-major. Gradients use 2–8 `[time,r,g,b,a]` keys; curves use `[time,value,inTangent,outTangent]` keys. Existing keyword/dropdown nodes come from the native catalog; creating their definitions and parameter groups is outside this API's current scope.

Node `settings` accepts fields returned by Catalog, omitting the native `m_` prefix. Numeric enums retain native serialized indices. Identity, slots, object references and shared UI metadata are excluded. `expanded`, `preview`, `position` are separate optional fields. Unknown settings fail.

Custom Function uses the same compile-before-apply validation as the node UI:

```json
{
  "nodes": {
    "custom": {
      "type": "Custom Function",
      "function": {
        "mode": "Body", "code": "Out = In * In;",
        "ports": [
          {"name":"In","type":"Float","direction":"Input"},
          {"name":"Out","type":"Float","direction":"Output"}
        ]
      },
      "in": {"In": "noise"}
    }
  }
}
```

File/Source modes accept `file`, `entry`, `code`, `title`; signatures use normal discovery. Structural edits use native type/stage/cycle checks. Save runs the usual shader importer. Structural validation alone does not guarantee arbitrary external HLSL compiles on every target.

Limits: 2 MB per request, 512 graph nodes. Errors include messages and codes such as `revision_conflict`, `already_exists`, `editor_busy`, `graph_validation_failed`.
