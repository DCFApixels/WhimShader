using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Graphing;
using UnityEditor.Graphing.Util;
using UnityEditor.ShaderGraph;
using UnityEditor.ShaderGraph.Drawing;
using UnityEditor.ShaderGraph.Internal;
using UnityEditor.ShaderGraph.Serialization;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DCFApixels.WhimShader
{
    /// <summary>Compact, main-thread authoring API. Native graph validation and serialization remain authoritative.</summary>
    public static partial class WhimShaderApi
    {
        const int MaxNodes = 512;
        public static string ExecuteFile(string requestPath) => Safe(() => ExecuteCore(Parse(File.ReadAllText(requestPath))));
        public static string Execute(string json) => Safe(() => ExecuteCore(Parse(json)));
        public static string Sessions() => Safe(() => new JObject { ["sessions"] = new JArray(
            Resources.FindObjectsOfTypeAll<WhimShaderWindow>().Where(w => w.Graph != null).Select(w => new JObject {
                ["path"] = w.AuthoringPath, ["revision"] = Revision(w.Graph), ["unsaved"] = w.hasUnsavedChanges || w.Graph.owner.isDirty })) });
        public static string Inspect(string path, bool details = false) => Execute(new JObject { ["op"] = "inspect", ["path"] = path, ["details"] = details }.ToString(Formatting.None));
        public static string Catalog(string query = null, string node = null, int limit = 25) => Safe(() =>
        {
            using var session = new Session(null, true);
            using var catalog = new CatalogScope(session.graph);
            if (!string.IsNullOrEmpty(node)) return Describe(catalog.Find(node).node);
            var matches = catalog.entries.Where(e => string.IsNullOrWhiteSpace(query) ||
                (PathOf(e) + " " + string.Join(" ", e.node.synonyms ?? Array.Empty<string>())).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
            return new JObject { ["total"] = matches.Length, ["nodes"] = new JArray(matches.Take(Math.Clamp(limit, 1, 100)).Select(PathOf)) };
        });

        static string Safe(Func<JObject> action)
        {
            try
            {
                Require(!EditorApplication.isCompiling && !EditorApplication.isUpdating && !EditorApplication.isPlayingOrWillChangePlaymode,
                    "Editor must be ready and outside Play Mode.", "editor_busy");
                var result = action(); result["success"] = true; return result.ToString(Formatting.None);
            }
            catch (Exception e)
            {
                return new JObject { ["success"] = false, ["error"] = e is ApiException a ? a.code : "invalid_request", ["message"] = e.Message }.ToString(Formatting.None);
            }
        }
        internal static JObject Parse(string json)
        {
            Require(json != null && json.Length <= 2_000_000, "Request exceeds 2 MB.");
            using var reader = new JsonTextReader(new StringReader(json)) { MaxDepth = 64, DateParseHandling = DateParseHandling.None };
            var value = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            Require(!reader.Read(), "Unexpected text after request."); return value;
        }
        internal static void Require(bool value, string message, string code = "invalid_request") { if (!value) throw new ApiException(code, message); }
        sealed class ApiException : Exception { internal readonly string code; internal ApiException(string code, string text) : base(text) { this.code = code; } }
        internal static void Keys(JObject value, params string[] names)
        {
            foreach (var p in value.Properties()) Require(names.Contains(p.Name), "Unknown field: " + p.Name);
        }
        internal static string Revision(GraphData graph) => Hash(MultiJson.Serialize(graph));
        static string Hash(string text) { using var sha = SHA256.Create(); return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant(); }
        static string Normalize(string name) => new string((name ?? "").Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        static string PathOf(NodeEntry e) => string.Join("/", WhimShaderWindow.CatalogPath(e));

        sealed class CatalogScope : IDisposable
        {
            internal readonly SearcherProvider provider = new();
            internal readonly NodeEntry[] entries;
            internal CatalogScope(GraphData graph)
            {
                provider.Initialize(null, graph, null);
                entries = provider.currentNodeEntries.Where(e => e.node.GetType() != typeof(CustomFunctionNode) &&
                    WhimShaderArrangement.Visible(e.node) && e.node is not BlockNode &&
                    (graph.isSubGraph ? e.node.allowedInSubGraph : graph.activeTargets.Any(t => t.IsNodeAllowedByTarget(e.node.GetType()))))
                    .OrderBy(PathOf, StringComparer.OrdinalIgnoreCase).ToArray();
            }
            internal NodeEntry Find(string type)
            {
                var exact = entries.Where(e => string.Equals(PathOf(e), type, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (exact.Length == 1) return exact[0];
                var found = entries.Where(e => Normalize(e.title.Last()) == Normalize(type)).ToArray();
                if (found.Length == 0) found = entries.Where(e => e.node.synonyms?.Any(s => Normalize(s) == Normalize(type)) == true).ToArray();
                Require(found.Length == 1, found.Length == 0 ? "Unknown node '" + type + "'. Search Catalog first." : "Ambiguous node '" + type + "': " + string.Join(", ", found.Select(PathOf)));
                return found[0];
            }
            public void Dispose() => provider.Dispose();
        }

        sealed class Session : IDisposable
        {
            internal readonly GraphData graph;
            internal readonly WhimShaderWindow window;
            internal readonly string path;
            readonly bool owns;
            internal Session(string path, bool create)
            {
                this.path = path;
                if (create)
                {
                    graph = WhimShaderURP.CreateGraph(); owns = true;
                    foreach (var n in graph.GetNodes<UnityPBRNode>().ToArray()) graph.RemoveNode(n);
                    return;
                }
                window = Resources.FindObjectsOfTypeAll<WhimShaderWindow>().SingleOrDefault(w => string.Equals(w.AuthoringPath, path, StringComparison.OrdinalIgnoreCase));
                if (window != null) { window.AuthoringEnsureReady(); graph = window.Graph; return; }
                Require(File.Exists(path), "Graph does not exist: " + path);
                graph = Clone(null, File.ReadAllText(path), path.EndsWith(".shadersubgraph", StringComparison.OrdinalIgnoreCase));
                graph.assetGuid = AssetDatabase.AssetPathToGUID(path); owns = true;
            }
            public void Dispose() { if (owns) { graph.OnDisable(); Object.DestroyImmediate(graph.owner); } }
        }
        internal static GraphData Clone(GraphData original, string json = null, bool subgraph = false)
        {
            var graph = new GraphData { assetGuid = original?.assetGuid, isSubGraph = original?.isSubGraph ?? subgraph, messageManager = new MessageManager() };
            var owner = ScriptableObject.CreateInstance<GraphObject>(); owner.hideFlags = HideFlags.HideAndDontSave; owner.graph = graph;
            try { MultiJson.Deserialize(graph, json ?? MultiJson.Serialize(original)); graph.OnEnable(); graph.ValidateGraph(); return graph; }
            catch { Object.DestroyImmediate(owner); throw; }
        }
        static string AssetPath(string path)
        {
            Require(!string.IsNullOrEmpty(path), "path is required. Use Sessions to find open graphs.");
            string full = Path.GetFullPath(path), assets = Path.GetFullPath(Application.dataPath) + Path.DirectorySeparatorChar;
            Require(full.StartsWith(assets, StringComparison.OrdinalIgnoreCase), "Graph path must be inside Assets/.");
            string result = "Assets/" + full.Substring(assets.Length).Replace('\\', '/');
            Require(result.EndsWith(".whimshader", StringComparison.OrdinalIgnoreCase) || result.EndsWith(".shadersubgraph", StringComparison.OrdinalIgnoreCase), "Expected .whimshader or .shadersubgraph.");
            return result;
        }

        static JObject ExecuteCore(JObject request)
        {
            if ((string)request["op"] == "preview")
            {
                Keys(request, "op", "path", "node", "output", "size", "mode", "channel", "time");
                return PreviewCore((string)request["path"], (string)request["node"], (string)request["output"],
                    (int?)request["size"] ?? 256, (string)request["mode"], (string)request["channel"], (float?)request["time"] ?? 0);
            }
            Keys(request, "op", "path", "expectedRevision", "nodes", "parameters", "edges", "fragment", "vertex", "remove", "disconnect", "layout", "save", "dryRun", "shaderSettings", "ids", "details");
            string op = (string)request["op"] ?? "inspect", path = AssetPath((string)request["path"]);
            Require(new[] { "inspect", "create", "patch", "arrange" }.Contains(op), "op must be inspect, create, patch, arrange or preview.");
            if (op == "arrange") Require(!new[] { "nodes", "edges", "parameters", "remove", "disconnect", "vertex", "fragment", "shaderSettings" }.Any(k => request[k] != null), "arrange only changes positions; use patch for graph edits.");
            bool create = op == "create", dryRun = (bool?)request["dryRun"] ?? false;
            if (create)
            {
                Require(path.EndsWith(".whimshader", StringComparison.OrdinalIgnoreCase), "create currently writes .whimshader; existing Sub Graphs can be inspected and patched.");
                Require(!File.Exists(path) && !File.Exists(path + ".meta"), "Create never overwrites an asset.", "already_exists");
                Require(AssetDatabase.IsValidFolder(Path.GetDirectoryName(path).Replace('\\', '/')), "Destination folder must already exist.");
            }
            using var session = new Session(path, create);
            if (op == "inspect") return Snapshot(session.graph, path, (bool?)request["details"] ?? false, request["ids"] as JArray);
            string revision = Revision(session.graph);
            if (!create) Require((string)request["expectedRevision"] == revision, "Graph changed or expectedRevision is missing. Inspect it again before editing.", "revision_conflict");
            bool save = (bool?)request["save"] ?? create;
            Require(create || session.window != null || save || dryRun, "A closed graph requires save:true (or dryRun:true).");
            var candidate = Clone(session.graph);
            try
            {
                var moving = new HashSet<string>(StringComparer.Ordinal);
                Apply(candidate, request, moving);
                Require(candidate.GetNodes<AbstractMaterialNode>().Count() <= MaxNodes, "Graph exceeds the 512 node authoring limit.");
                string layout = (string)request["layout"] ?? (op == "arrange" || create ? "all" : "new");
                Require(new[] { "all", "new", "none", "selected" }.Contains(layout), "layout must be all, new, selected or none.");
                if (layout == "all") moving.UnionWith(candidate.GetNodes<AbstractMaterialNode>().Where(WhimShaderArrangement.Visible).Where(n => n is not BlockNode).Select(n => n.objectId));
                if (layout == "selected")
                {
                    Require(request["ids"] is JArray, "layout:selected requires ids."); moving.Clear();
                    foreach (string id in (JArray)request["ids"]) moving.Add(Resolve(candidate, id).objectId);
                }
                // Explicit positions always win, even in an otherwise fully automatic draft.
                if (request["nodes"] is JObject nodes)
                    foreach (var p in nodes.Properties()) if (p.Value is JObject n && n["position"] != null) moving.Remove(Resolve(candidate, p.Name).objectId);
                int arranged = layout == "none" ? 0 : WhimShaderArrangement.Arrange(candidate, moving, session.window?.AuthoringGeometry(), layout == "all").Count;
                candidate.ValidateGraph();
                var errors = candidate.messageManager.ErrorStrings(_ => true).ToArray();
                Require(!candidate.messageManager.HasSeverity(_ => true, UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error), string.Join("\n", errors), "graph_validation_failed");
                string next = Revision(candidate);
                if (!dryRun)
                {
                    if (session.window != null) { session.window.AuthoringApply(candidate, save, op == "arrange"); next = Revision(session.window.Graph); }
                    else { WhimShaderWindow.WriteAtomic(path, MultiJson.Serialize(candidate)); AssetDatabase.ImportAsset(path); }
                }
                return new JObject { ["path"] = path, ["revision"] = dryRun ? revision : next, ["dryRun"] = dryRun,
                    ["saved"] = !dryRun && save, ["nodes"] = candidate.GetNodes<AbstractMaterialNode>().Count(WhimShaderArrangement.Visible),
                    ["edges"] = candidate.edges.Count(), ["arranged"] = arranged,
                    ["ids"] = new JObject(Names(candidate).Select(p => new JProperty(p.Key, p.Value))), ["diagnostics"] = new JArray(errors) };
            }
            finally { candidate.OnDisable(); Object.DestroyImmediate(candidate.owner); }
        }

        static Dictionary<string, string> Names(GraphData graph) => graph.GetNodes<WhimShaderNodePresentationMetadata>().FirstOrDefault()?.authoringNames?
            .Where(n => graph.GetNodeFromId(n.nodeId) != null).GroupBy(n => n.name).ToDictionary(g => g.Key, g => g.First().nodeId, StringComparer.Ordinal) ?? new(StringComparer.Ordinal);
        static void Name(GraphData graph, string alias, AbstractMaterialNode node)
        {
            Require(alias != "vertex" && alias != "fragment", "vertex and fragment are reserved output names.");
            Require(System.Text.RegularExpressions.Regex.IsMatch(alias, @"\A[A-Za-z][A-Za-z0-9_-]{0,63}\z"), "Node aliases use 1..64 letters, digits, '_' or '-', beginning with a letter.");
            var metadata = graph.GetNodes<WhimShaderNodePresentationMetadata>().FirstOrDefault();
            if (metadata == null) { metadata = new WhimShaderNodePresentationMetadata(); graph.AddNode(metadata); }
            metadata.authoringNames.RemoveAll(n => n.name == alias || graph.GetNodeFromId(n.nodeId) == null);
            metadata.authoringNames.Add(new() { name = alias, nodeId = node.objectId });
        }
        static AbstractMaterialNode Resolve(GraphData graph, string id)
        {
            Require(!string.IsNullOrEmpty(id), "Missing node ID.");
            string actual = Names(graph).TryGetValue(id, out var named) ? named : id;
            var node = NativeReference(graph, actual);
            Require(node != null && WhimShaderArrangement.Visible(node), "Unknown node: " + id); return node;
        }
        static AbstractMaterialNode NativeReference(GraphData graph, string id)
        {
            if (id?.StartsWith("#", StringComparison.Ordinal) != true) return graph.GetNodeFromId(id);
            string prefix = id.Substring(1);
            Require(prefix.Length >= 8, "Short node IDs need at least 8 hexadecimal characters.");
            var found = graph.GetNodes<AbstractMaterialNode>().Where(n => n.objectId.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
            Require(found.Length <= 1, "Ambiguous node ID: " + id); return found.FirstOrDefault();
        }
        static string ShortId(GraphData graph, AbstractMaterialNode node)
        {
            for (int length = 8; length < node.objectId.Length; length += 4)
            {
                string prefix = node.objectId.Substring(0, length);
                if (!graph.GetNodes<AbstractMaterialNode>().Any(n => n != node && n.objectId.StartsWith(prefix, StringComparison.Ordinal))) return "#" + prefix;
            }
            return node.objectId;
        }
        static MaterialSlot Port(AbstractMaterialNode node, string name, bool output)
        {
            var slots = (output ? node.GetOutputSlots<MaterialSlot>() : node.GetInputSlots<MaterialSlot>()).Where(s => !s.hidden).ToArray();
            if (string.IsNullOrEmpty(name)) { Require(slots.Length == 1, node.name + " requires an explicit port: " + string.Join(", ", slots.Select(s => s.RawDisplayName()))); return slots[0]; }
            var found = slots.Where(s => s.id.ToString() == name || Normalize(s.RawDisplayName()) == Normalize(name) || Normalize(s.shaderOutputName) == Normalize(name)).ToArray();
            Require(found.Length == 1, "Unknown or ambiguous port '" + name + "' on " + node.name + ". Available: " + string.Join(", ", slots.Select(s => s.RawDisplayName()))); return found[0];
        }
        static MaterialSlot Endpoint(GraphData graph, string value, bool output)
        {
            int dot = value?.LastIndexOf('.') ?? -1;
            if (!output && dot > 0 && value.Substring(0, dot) is "vertex" or "fragment") return Output(graph, value.Substring(0, dot), value.Substring(dot + 1));
            return Port(Resolve(graph, dot < 0 ? value : value.Substring(0, dot)), dot < 0 ? null : value.Substring(dot + 1), output);
        }
        static void Connect(GraphData graph, MaterialSlot source, MaterialSlot destination)
        {
            Require(source.IsCompatibleWith(destination), "Incompatible ports: " + source.owner.name + "." + source.RawDisplayName() + " -> " + destination.owner.name + "." + destination.RawDisplayName());
            Require(source.IsCompatibleStageWith(destination) && destination.IsCompatibleStageWith(source), "Connection crosses incompatible vertex/fragment stages.");
            Require(graph.Connect(source.slotReference, destination.slotReference) != null, "Connection would create a cycle or is invalid.");
        }
        static void Input(GraphData graph, MaterialSlot port, JToken value)
        {
            if (value.Type == JTokenType.String) { Connect(graph, Endpoint(graph, (string)value, true), port); return; }
            graph.RemoveEdges(graph.edges.Where(e => e.inputSlot.Equals(port.slotReference)).Cast<IEdge>().ToArray());
            if (value.Type != JTokenType.Null) SetValue(port, value);
        }

        static void Apply(GraphData graph, JObject request, HashSet<string> moving)
        {
            foreach (string key in new[] { "nodes", "parameters", "vertex", "fragment", "shaderSettings" })
                if (request[key] != null) Require(request[key] is JObject, key + " must be an object.");
            foreach (string key in new[] { "edges", "remove", "disconnect", "ids" })
                if (request[key] != null) Require(request[key] is JArray, key + " must be an array.");
            if (request["shaderSettings"] is JObject settings) Settings(graph, settings);
            if (request["parameters"] is JObject parameters) Parameters(graph, parameters);
            if (request["remove"] is JArray removed)
                foreach (string id in removed) { var n = Resolve(graph, id); Require(n.canDeleteNode && n is not BlockNode, "Cannot remove protected node: " + id); graph.RemoveNode(n); }
            if (request["nodes"] != null) Require(request["nodes"] is JObject, "nodes must be an object keyed by short aliases.");
            if (request["nodes"] is JObject nodes)
            {
                Require(nodes.Count <= MaxNodes, "Too many nodes.");
                using var catalog = new CatalogScope(graph);
                foreach (var item in nodes.Properties())
                {
                    var spec = item.Value is JValue { Type: JTokenType.String } ? new JObject { ["type"] = item.Value } : item.Value as JObject;
                    Require(spec != null, "Node '" + item.Name + "' must be a type name or object.");
                    Keys(spec, "type", "property", "in", "settings", "position", "expanded", "preview", "function");
                    foreach (string key in new[] { "in", "settings", "function" })
                        if (spec[key] != null) Require(spec[key] is JObject, item.Name + "." + key + " must be an object.");
                    var n = Names(graph).TryGetValue(item.Name, out var existing) ? graph.GetNodeFromId(existing) : NativeReference(graph, item.Name);
                    if (n == null)
                    {
                        if (spec["property"] != null)
                        {
                            var prop = FindProperty(graph, (string)spec["property"]);
                            var pn = new PropertyNode(); graph.AddNode(pn); pn.property = prop; n = pn;
                        }
                        else { n = catalog.provider.CopyNodeForGraph(catalog.Find((string)spec["type"]).node); graph.AddNode(n); }
                        Name(graph, item.Name, n); moving.Add(n.objectId);
                    }
                    else
                    {
                        if (spec["type"] != null) Require(catalog.Find((string)spec["type"]).node.GetType() == n.GetType(), "Changing an existing node's type requires remove + create.");
                        if (spec["property"] != null) { Require(n is PropertyNode, "property requires a parameter node."); ((PropertyNode)n).property = FindProperty(graph, (string)spec["property"]); }
                    }
                    Require(WhimShaderArrangement.Visible(n) && n is not BlockNode, "Protected node.");
                    if (spec["settings"] is JObject config) Configure(n, config);
                    if (spec["function"] is JObject function) Function(n, function);
                    var draw = n.drawState;
                    if (spec["expanded"] != null) draw.expanded = (bool)spec["expanded"];
                    if (spec["position"] != null) { var v = Numbers(spec["position"], 2); draw.position = new Rect(new Vector2(v[0], v[1]), draw.position.size); }
                    n.drawState = draw;
                    if (spec["preview"] != null) n.previewExpanded = (bool)spec["preview"];
                }
                // Resolve wires after every alias exists, allowing forward references.
                foreach (var item in nodes.Properties())
                    if (item.Value is JObject spec && spec["in"] is JObject inputs)
                        foreach (var input in inputs.Properties()) Input(graph, Port(Resolve(graph, item.Name), input.Name, false), input.Value);
            }
            if (request["disconnect"] is JArray disconnected) foreach (string endpoint in disconnected) Input(graph, Endpoint(graph, endpoint, false), JValue.CreateNull());
            if (request["edges"] is JArray edges)
                foreach (var edge in edges)
                {
                    Require(edge is JArray pair && pair.Count == 2, "Each edge is [source.port, destination.port].");
                    Connect(graph, Endpoint(graph, (string)edge[0], true), Endpoint(graph, (string)edge[1], false));
                }
            foreach (string stage in new[] { "vertex", "fragment" })
                if (request[stage] is JObject outputs)
                    foreach (var output in outputs.Properties()) Input(graph, Output(graph, stage, output.Name), output.Value);
        }

        static MaterialSlot Output(GraphData graph, string stage, string name)
        {
            Require(!graph.isSubGraph, "Sub Graph outputs are addressed through their output node.");
            var context = stage == "vertex" ? graph.vertexContext : graph.fragmentContext;
            var block = context.blocks.Select(b => b.value).FirstOrDefault(b => Normalize(b.descriptor.displayName) == Normalize(name) || Normalize(b.descriptor.name) == Normalize(name));
            Require(block != null, "Unknown " + stage + " output '" + name + "'. Available: " + string.Join(", ", context.blocks.Select(b => b.value.descriptor.displayName)));
            return block.FindInputSlot<MaterialSlot>(0);
        }
    }
}
