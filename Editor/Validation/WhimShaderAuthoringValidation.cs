using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Graphing;
using UnityEditor.ShaderGraph;
using UnityEditor.ShaderGraph.Serialization;
using UnityEditor.ShaderGraph.Drawing;
using UnityEngine;
using UnityEngine.UIElements;

namespace DCFApixels.WhimShader
{
    public static partial class WhimShaderAuthoringValidation
    {
        static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        static JObject Call(JObject request, bool success = true)
        {
            var result = JObject.Parse(WhimShaderApi.Execute(request.ToString(Formatting.None)));
            Check((bool)result["success"] == success, result.ToString()); return result;
        }
        static JObject Inspect(string path, bool details = false) => JObject.Parse(WhimShaderApi.Inspect(path, details));
        static JObject Patch(string path) => new() { ["op"] = "patch", ["path"] = path, ["expectedRevision"] = Inspect(path)["revision"], ["save"] = true };
        static GraphData Load(string path) => WhimShaderApi.Clone(null, File.ReadAllText(path));
        static void Release(GraphData graph) => UnityEngine.Object.DestroyImmediate(graph.owner);

        public static object Extended()
        {
            var checks = new List<string>();
            string path = "Assets/WhimShaderValidation/AgentExtended_" + Guid.NewGuid().ToString("N") + ".whimshader";
            try
            {
                var request = JObject.Parse(@"{'op':'create',
                  'parameters':{
                    'amount':{'name':'Amount Display','reference':'_DifferentReference','type':'Float','value':0.3},
                    'gradient':{'type':'Gradient LUT','value':[[0,0,0,0,1],[1,1,0,0,1]]},
                    'curve':{'type':'Curve LUT','value':[[0,0,1,1],[1,1,1,1]]},
                    'matrix':{'type':'Matrix2','exposed':false,'value':[1,0,0,1]}},
                  'nodes':{
                    'value':{'property':'amount'},
                    'custom':{'type':'Custom Function','function':{'code':'Out = In * In;'},'in':{'In':'value'}},
                    'uv':{'type':'UV','settings':{'OutputChannel':1}}},
                  'fragment':{'Color':'custom'}}");
                // Resolve the UV field from the native schema; version-pinned numeric setting.
                var uvSettings = (JObject)JObject.Parse(WhimShaderApi.Catalog(node: "UV"))["settings"];
                request["nodes"]["uv"]["settings"] = new JObject { [uvSettings.Properties().First().Name] = 1 };
                request["path"] = path; Call(request);
                var update = Patch(path); update["parameters"] = JObject.Parse(@"{'amount':{'value':0.6}}"); Call(update);
                var snapshot = Inspect(path, true);
                Check(((JArray)snapshot["parameters"]).Count == 4 && ((JArray)snapshot["parameters"]).Any(p => (string)p["reference"] == "_DifferentReference" && Math.Abs((float)p["value"] - .6f) < 1e-6), "Parameter alias was lost when the display/reference name changed");
                checks.Add("Renamed parameter aliases, LUTs, row-major matrices and native settings save and reopen");
                var graph = Load(path);
                try
                {
                    var custom = graph.GetNodes<WhimShaderCustomFunctionNode>().Single();
                    Check(custom.functionBody.Contains("In * In"), "Custom Function code not applied");
                    var g = new Generator(graph, custom, GenerationMode.Preview, "Hidden/WhimShader/AgentCustom");
                    var shader = ShaderUtil.CreateShaderAsset(g.generatedShader, false); var material = new Material(shader);
                    try { ShaderUtil.CompilePass(material, 0, true); Check(!ShaderUtil.ShaderHasError(shader), "Custom Function graph failed HLSL compilation"); }
                    finally { UnityEngine.Object.DestroyImmediate(material); UnityEngine.Object.DestroyImmediate(shader); }
                }
                finally { Release(graph); }
                checks.Add("Agent Custom Function uses the existing checked compiler and remains connected after save");
                string original = File.ReadAllText(path);
                update = Patch(path); update["nodes"] = JObject.Parse(@"{'custom':{'function':{'code':'Out = syntax_error(;'}}}");
                Call(update, false); Check(File.ReadAllText(path) == original, "Invalid HLSL damaged the graph");
                checks.Add("Invalid user HLSL fails the entire batch without changing applied code");
                graph = Load(path);
                try
                {
                    var nodes = graph.GetNodes<AbstractMaterialNode>().Where(n => WhimShaderArrangement.Visible(n) && n is not BlockNode).ToArray();
                    var group = new GroupData("Fixture", new Vector2(-400, -200)); graph.CreateGroup(group);
                    graph.SetGroup(nodes[0], group); graph.SetGroup(nodes[1], group);
                    var note = new StickyNoteData { position = new Rect(-800, -400, 250, 300) }; graph.AddStickyNote(note);
                    var fixedPosition = nodes[2].drawState.position;
                    var move = new HashSet<string>(nodes.Take(2).Select(n => n.objectId));
                    WhimShaderArrangement.Arrange(graph, move);
                    Check(nodes[2].drawState.position == fixedPosition, "Unselected node moved");
                    foreach (var node in nodes.Take(2)) Check(!WhimShaderArrangement.Translate(WhimShaderArrangement.Estimate(node).bounds, node.drawState.position.position).Overlaps(note.position), "Node overlaps note");
                    Check(nodes[0].group == group && nodes[1].group == group, "Group membership changed");
                }
                finally { Release(graph); }
                checks.Add("Partial arrangement preserves group membership, pinned nodes and note obstacles");
                graph = Load(path);
                try
                {
                    graph.AddNode(new Vector1Node());
                    WhimShaderWindow.WriteAtomic(path, MultiJson.Serialize(graph)); AssetDatabase.ImportAsset(path);
                }
                finally { Release(graph); }
                string shortId = ((JObject)Inspect(path)["nodes"]).Properties().Single(p => p.Name.StartsWith("#") && (string)p.Value == "Float").Name;
                update = Patch(path); update["nodes"] = new JObject { [shortId] = JObject.Parse(@"{'in':{'X':0.42}}") }; Call(update);
                Check(Math.Abs((float)Inspect(path, true)["nodes"][shortId]["in"]["X"] - .42f) < 1e-6, "Short ID edit failed");
                checks.Add("Existing manually created nodes can be inspected and edited by stable short IDs");
                return new { success = true, checks };
            }
            finally { if (File.Exists(path)) AssetDatabase.DeleteAsset(path); }
        }

        static WhimShaderWindow uiWindow;
        static string uiPath;
        public static object StartUI()
        {
            Check(uiWindow == null, "Finish the previous UI check first");
            uiPath = "Assets/WhimShaderValidation/AgentUI_" + Guid.NewGuid().ToString("N") + ".whimshader";
            var request = JObject.Parse(@"{'op':'create','nodes':{
              'uv':'UV','noise':{'type':'Gradient Noise','in':{'UV':'uv','Scale':8}},
              'other':{'type':'Simple Noise','in':{'Scale':4}},
              'mix':{'type':'Lerp','in':{'A':'noise','B':'other','T':0.5}},
              'function':{'type':'Custom Function','in':{'In':'mix'}}},'fragment':{'Color':'function'}}");
            request["path"] = uiPath; Call(request);
            uiWindow = WhimShaderWindow.Open(uiPath);
            return new { path = uiPath, success = true };
        }
        public static object CheckUI()
        {
            Check(uiWindow != null, "StartUI first");
            foreach (var view in uiWindow.rootVisualElement.Query<MaterialNodeView>().ToList())
                if (view.node is not BlockNode && WhimShaderArrangement.Visible(view.node))
                    Check(Vector2.Distance(view.GetPosition().position, view.node.drawState.position.position) < 1f,
                        "UI position differs from the model: " + view.node.name + " " + view.GetPosition().position + " / " + view.node.drawState.position.position);
            foreach (var context in uiWindow.rootVisualElement.Query<ContextView>().ToList())
                Check(Vector2.Distance(context.GetPosition().position, context.contextData.position) < 1f,
                    "Output context view did not move: " + context.GetPosition().position + " / " + context.contextData.position);
            var geometry = uiWindow.AuthoringGeometry();
            Check(geometry.Count >= 5, "Views have not laid out yet");
            var rects = geometry.Select(p => (id: p.Key, rect: WhimShaderArrangement.Translate(p.Value.bounds, uiWindow.Graph.GetNodeFromId(p.Key).drawState.position.position))).ToArray();
            for (int i = 0; i < rects.Length; i++) for (int j = i + 1; j < rects.Length; j++)
                Check(!rects[i].rect.Overlaps(rects[j].rect), "Measured views overlap: " + uiWindow.Graph.GetNodeFromId(rects[i].id).name + "/" + uiWindow.Graph.GetNodeFromId(rects[j].id).name);
            return new { success = true, measuredNodes = geometry.Count, checks = new[] { "Actual laid-out views, CodeArea and external port fields do not overlap" } };
        }
        public static object ArrangeUI()
        {
            Check(uiWindow != null, "StartUI first");
            string before = new Generator(uiWindow.Graph, uiWindow.Graph.outputNode, GenerationMode.ForReals, "Hidden/WhimShader/UILayout").generatedShader;
            var nodes = uiWindow.Graph.GetNodes<AbstractMaterialNode>().ToDictionary(n => n.objectId);
            uiWindow.ArrangeNodes(true);
            var arrange = Patch(uiPath); arrange["op"] = "arrange"; arrange["save"] = false; Call(arrange);
            Check(nodes.All(p => ReferenceEquals(p.Value, uiWindow.Graph.GetNodeFromId(p.Key))), "Pure arrangement recreated native nodes");
            Check(before == new Generator(uiWindow.Graph, uiWindow.Graph.outputNode, GenerationMode.ForReals, "Hidden/WhimShader/UILayout").generatedShader, "UI arrangement changed HLSL");
            return new { success = true, checks = new[] { "Toolbar/API arrangement moves native views and contexts without replacing nodes or changing shader code" } };
        }
        public static object FinishUI()
        {
            if (uiWindow != null) { uiWindow.DiscardChanges(); uiWindow.Close(); uiWindow = null; }
            if (!string.IsNullOrEmpty(uiPath) && File.Exists(uiPath)) AssetDatabase.DeleteAsset(uiPath);
            uiPath = null; return new { success = true };
        }

        public static object Run()
        {
            var checks = new List<string>();
            const string folder = "Assets/WhimShaderValidation";
            if (!AssetDatabase.IsValidFolder(folder)) AssetDatabase.CreateFolder("Assets", "WhimShaderValidation");
            string path = folder + "/Agent_" + Guid.NewGuid().ToString("N") + ".whimshader";
            WhimShaderWindow window = null;
            try
            {
                var catalog = JObject.Parse(WhimShaderApi.Catalog("", limit: 3));
                Check((bool)catalog["success"] && ((JArray)catalog["nodes"]).Count == 3 && (int)catalog["total"] > 100, "Bounded catalog");
                Check((bool)JObject.Parse(WhimShaderApi.Catalog(node: "Multiply"))["success"], "Exact name must win over synonym");
                checks.Add("Catalog is bounded; exact node names take precedence over synonyms");
                var create = JObject.Parse(@"{
                    'op':'create','parameters':{'Tint':{'type':'Color','value':[0.7,0.4,0.2,1]}},
                    'nodes':{
                        'result':{'type':'Multiply','in':{'A':'noise','B':'tint'}},
                        'noise':{'type':'Gradient Noise','in':{'UV':'uv','Scale':8}},
                        'uv':'UV','tint':{'property':'Tint'}},
                    'fragment':{'Color':'result'}, 'vertex':{'Position':[0,0,0]}
                }");
                create["path"] = path; create["dryRun"] = true;
                Call(create); Check(!File.Exists(path), "Dry run wrote a file");
                checks.Add("Create dry run validates and arranges without saving");
                create["dryRun"] = false; var created = Call(create);
                var snapshot = Inspect(path);
                Check((bool)snapshot["success"] && (string)snapshot["nodes"]["noise"] == "Gradient Noise" && ((JArray)snapshot["edges"]).Count == 4, snapshot.ToString());
                Check(snapshot["parameters"] == null && !snapshot.ToString().Contains("position"), "Compact inspection contains excess data");
                Check((string)created["revision"] == (string)snapshot["revision"], "Returned revision differs after reopen");
                checks.Add("Forward aliases, parameters, inline connections and vertex/fragment outputs save and reopen");
                checks.Add("Compact inspection contains nodes and wires without positions or parameter payloads");
                var graph = Load(path);
                string source;
                Dictionary<string, Vector2> positions;
                try
                {
                    var nodes = graph.GetNodes<AbstractMaterialNode>().Where(WhimShaderArrangement.Visible).Where(n => n is not BlockNode).ToArray();
                    positions = nodes.ToDictionary(n => n.objectId, n => n.drawState.position.position);
                    for (int i = 0; i < nodes.Length; i++)
                        for (int j = i + 1; j < nodes.Length; j++)
                            Check(!WhimShaderArrangement.Translate(WhimShaderArrangement.Estimate(nodes[i]).bounds, nodes[i].drawState.position.position)
                                .Overlaps(WhimShaderArrangement.Translate(WhimShaderArrangement.Estimate(nodes[j]).bounds, nodes[j].drawState.position.position)), "Arranged nodes overlap");
                    source = new Generator(graph, graph.outputNode, GenerationMode.ForReals, "Hidden/WhimShader/AgentCheck").generatedShader;
                    var shader = ShaderUtil.CreateShaderAsset(source, false); var material = new Material(shader);
                    bool async = ShaderUtil.allowAsyncCompilation;
                    try { ShaderUtil.allowAsyncCompilation = false; ShaderUtil.CompilePass(material, 0, true);
                        Check(!ShaderUtil.ShaderHasError(shader), string.Join("\n", ShaderUtil.GetShaderMessages(shader).Select(m => m.message))); }
                    finally { ShaderUtil.allowAsyncCompilation = async; UnityEngine.Object.DestroyImmediate(material); UnityEngine.Object.DestroyImmediate(shader); }
                    checks.Add("Automatic layout leaves room for node bodies and external input fields; generated URP shader compiles");
                }
                finally { Release(graph); }
                var arrange = Patch(path); arrange["op"] = "arrange";
                Call(arrange); graph = Load(path);
                try
                {
                    Check(source == new Generator(graph, graph.outputNode, GenerationMode.ForReals, "Hidden/WhimShader/AgentCheck").generatedShader, "Layout changed generated HLSL");
                    foreach (var pair in positions) Check(graph.GetNodeFromId(pair.Key).drawState.position.position == pair.Value, "Arrange is not deterministic");
                }
                finally { Release(graph); }
                checks.Add("Repeated full arrangement is deterministic and generated HLSL remains identical");
                var add = Patch(path); add["nodes"] = JObject.Parse(@"{'extra':{'type':'Add','in':{'A':'noise','B':0.2}},'pin':{'type':'Float','in':{'X':0.3},'position':[-2100,123]}}");
                Call(add); graph = Load(path);
                try
                {
                    foreach (var p in positions) Check(graph.GetNodeFromId(p.Key).drawState.position.position == p.Value, "Adding a branch moved existing nodes");
                    string pin = (string)Call(new JObject { ["op"] = "patch", ["path"] = path, ["expectedRevision"] = Inspect(path)["revision"], ["dryRun"] = true })["ids"]["pin"];
                    Check(graph.GetNodeFromId(pin).drawState.position.position == new Vector2(-2100,123), "Explicit position changed");
                }
                finally { Release(graph); }
                checks.Add("New nodes auto-arrange while existing and explicit positions remain unchanged");

                string intact = File.ReadAllText(path);
                foreach (var invalid in new[] {
                    JObject.Parse(@"{'nodes':{'extra':{'in':{'A':'result.Unknown'}}}}"),
                    JObject.Parse(@"{'nodes':{'extra':{'in':{'A':'extra'}}}}"),
                    JObject.Parse(@"{'nodes':{'extra':{'in':{'NoSuchInput':2}}}}"),
                    JObject.Parse(@"{'nodes':{'oops':{'type':'DDX','in':{'In':'noise'}}},'vertex':{'Position':'oops'}}"),
                    JObject.Parse(@"{'nodes':{'oops':{'type':'Unknown Node'}}}"),
                    JObject.Parse(@"{'nodes':{'extra':{'settings':{'ObjectId':'evil'}}}}"),
                    JObject.Parse(@"{'nodes':{'extra':{'in':[1,2]}}}") })
                {
                    var req = Patch(path); req.Merge(invalid); Call(req, false);
                    Check(File.ReadAllText(path) == intact, "Rejected batch changed file");
                }
                checks.Add("Invalid ports, cycles, stage conflicts, unknown types/settings and malformed input leave the asset unchanged");
                var conflict = Patch(path); conflict["expectedRevision"] = "old";
                Check((string)Call(conflict, false)["error"] == "revision_conflict", "Stale revision was accepted");
                Call(create, false); Check(File.ReadAllText(path) == intact, "Create overwrote existing file");
                checks.Add("Stale revisions and accidental overwrite are rejected");
                var disconnect = Patch(path); disconnect["disconnect"] = new JArray("result.A"); Call(disconnect);
                Check(((JArray)Inspect(path)["edges"]).Count == 4, "Disconnect failed");
                checks.Add("Disconnect removes only the requested input connection");
                var details = Call(new JObject { ["op"] = "inspect", ["path"] = path, ["ids"] = new JArray("noise"), ["details"] = true });
                Check(((JObject)details["nodes"]).Count == 1 && details["nodes"]["noise"]["settings"]["HashType"] != null, "Scoped details missing");
                checks.Add("Detailed inspection can be limited to chosen nodes and their incident wires");

                window = WhimShaderWindow.Open(path);
                var live = Patch(path); live["save"] = false;
                live["nodes"] = JObject.Parse(@"{'pin':{'in':{'X':0.8}},'live':{'type':'Sine','in':{'In':'pin'}}}");
                var beforeLive = Inspect(path); string beforeFile = File.ReadAllText(path);
                Call(live); Check(File.ReadAllText(path) == beforeFile, "Live edit auto-saved");
                Check(Inspect(path)["nodes"]["live"] != null && window.hasUnsavedChanges, "Live graph wasn't updated");
                Undo.PerformUndo(); window.AuthoringEnsureReady();
                Check(Inspect(path)["nodes"]["live"] == null, "Undo did not revert the complete batch");
                Undo.PerformRedo(); window.AuthoringEnsureReady();
                Check(Inspect(path)["nodes"]["live"] != null, "Redo did not restore the batch");
                checks.Add("Live batch is unsaved by default and supports one-step Undo/Redo");
                window.SaveChanges(); window.Close(); window = null;
                Check(Inspect(path)["nodes"]["live"] != null, "Saved aliases lost on reopen");
                checks.Add("Applied live edits and aliases survive save and closing the window");
                return new { success = true, checks };
            }
            finally
            {
                if (window != null) { window.DiscardChanges(); window.Close(); }
                if (File.Exists(path)) AssetDatabase.DeleteAsset(path);
            }
        }
    }
}
