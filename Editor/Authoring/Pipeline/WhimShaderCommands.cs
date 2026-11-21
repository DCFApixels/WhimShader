#if WHIMSHADER_PIPELINE
using Newtonsoft.Json.Linq;
using Unity.Pipeline.Commands;

namespace DCFApixels.WhimShader
{
    public static class WhimShaderCommands
    {
        [CliCommand("whimshader_sessions", "List open WhimShader graphs and their revisions, including unsaved changes.", MainThreadRequired = true)]
        public static JObject Sessions() => JObject.Parse(WhimShaderApi.Sessions());

        [CliCommand("whimshader_catalog", "Find nodes with query, or describe one exact node's ports/defaults/settings with node. Compact, bounded results.", MainThreadRequired = true)]
        public static JObject Catalog([CliArg("query", "Name/category substring")] string query = null,
            [CliArg("node", "Exact catalog path or unambiguous node name; returns its schema")] string node = null,
            [CliArg("limit", "Maximum search results, 1..100")] int limit = 25) => JObject.Parse(WhimShaderApi.Catalog(query, node, limit));

        [CliCommand("whimshader_inspect", "Read graph nodes, connections, parameters and revision. Uses unsaved state when open.", MainThreadRequired = true)]
        public static JObject Inspect([CliArg("path", "Assets/... .whimshader or .shadersubgraph", Required = true)] string path,
            [CliArg("details", "Include input values/settings; default is only nodes and edges")] bool details = false) => JObject.Parse(WhimShaderApi.Inspect(path, details));

        [CliCommand("whimshader_preview", "Render one node output to PNG using unsaved graph data when open. Returns absolute imagePath for an image viewer. Does not change the graph or preview settings.", MainThreadRequired = true)]
        public static JObject Preview([CliArg("path", "Graph asset path", Required = true)] string path,
            [CliArg("node", "Alias, short #ID or full node ID from inspect", Required = true)] string node,
            [CliArg("output", "Port name or ID; defaults to the selected preview output")] string output = null,
            [CliArg("size", "Square size 32..1024")] int size = 256,
            [CliArg("mode", "Image, Height or 3D; defaults to node setting")] string mode = null,
            [CliArg("channel", "RGBA, RGB, R, G, B, A; defaults to node setting")] string channel = null,
            [CliArg("time", "Animation time in seconds")] float time = 0)
            => JObject.Parse(WhimShaderApi.Preview(path, node, output, size, mode, channel, time));

        [CliCommand("whimshader_graph", "Create, patch or arrange a graph from compact JSON. Nodes use aliases and named ports; positions are optional. Patch requires expectedRevision; live edits use one Undo. See Documentation~/AgentAPI.md.", MainThreadRequired = true)]
        public static JObject Graph([CliArg("requestPath", "JSON file path; op:create|patch|arrange, path, nodes, edges, vertex/fragment", Required = true)] string requestPath)
            => JObject.Parse(WhimShaderApi.ExecuteFile(requestPath));
    }
}
#endif
