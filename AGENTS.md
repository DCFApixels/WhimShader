# WhimShader agent entry point

For graph authoring use the compact JSON API instead of editing native MultiJson or writing a C# graph builder. Start with [Documentation~/AgentAPI.md](Documentation~/AgentAPI.md); do not read the complete catalog up front.

- `whimshader_sessions`: discover open graphs, including unsaved state.
- `whimshader_inspect --path ...`: only nodes, wires and revision. Use `details:true` and `ids` through `whimshader_graph` for selected-node values/settings.
- `whimshader_catalog --query ...`: bounded discovery; `--node ...` describes one node.
- `whimshader_graph --requestPath ...`: create, patch or arrange. Use aliases and named ports; omit positions. Existing graphs require `expectedRevision` from inspection.
- `whimshader_preview --path ... --node ...`: on-demand PNG of a chosen node/output, including unsaved edits. Open returned `imagePath` with an image viewer. Optional `output`, `size`, `mode`, `channel`, `time`; no need to expand the node.

Target the intended Unity project explicitly. Pipeline is optional; the same public `DCFApixels.WhimShader.WhimShaderApi` methods work from Editor C# / eval. Compile through Unity, never standalone MSBuild/dotnet. Do not access private Unity APIs through reflection.

Live patches default to unsaved edits with Undo; use `save:true` when saving is authorized. Create never overwrites. After a timeout inspect before retrying. Preserve user positions unless full arrangement is requested. Keep native stage/type validation and Custom Function compile checks.
