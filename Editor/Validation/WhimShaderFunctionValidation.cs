using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Graphing;
using UnityEditor.Graphing.Util;
using UnityEditor.ShaderGraph;
using UnityEditor.ShaderGraph.Drawing;
using UnityEditor.ShaderGraph.Internal;
using UnityEditor.ShaderGraph.Serialization;
using UnityEngine;
using UnityEngine.UIElements;

namespace DCFApixels.WhimShader
{
    public static class WhimShaderFunctionValidation
    {
        static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        public static object Run()
        {
            var checks = new List<string>();
            var graph = WhimShaderURP.CreateGraph(); var owner = graph.owner;
            string folder = null;
            try
            {
                var node = new WhimShaderCustomFunctionNode(); graph.AddNode(node);
                var root = graph.GetNodes<BlockNode>().First(n => n.descriptor == WhimSurfaceBlocks.Color);
                graph.Connect(node.GetSlotReference(1), root.GetSlotReference(0));
                WhimShaderFunctions.CompileAndApply(node);
                float pixel = Render(graph, node);
                Require(Mathf.Abs(pixel - .5f) < .003f, "Default output differs: " + pixel);
                checks.Add("Default body compiles and renders 0.5");

                var input = new Vector1Node(); graph.AddNode(input);
                input.FindInputSlot<Vector1MaterialSlot>(Vector1Node.InputSlotXId).value = .4f;
                graph.Connect(input.GetSlotReference(Vector1Node.OutputSlotId), node.GetSlotReference(0));
                node.draft.ports[0].name = "Value"; node.draft.code = "Out = Value * 0.5;";
                WhimShaderFunctions.CompileAndApply(node);
                Require(node.FindSlot<MaterialSlot>(0).shaderOutputName == "Value" && graph.GetEdges(node.GetSlotReference(0)).Any(), "Rename lost name/connection.");
                Require(Mathf.Abs(Render(graph, node) - .2f) < .003f, "Renamed input produced the wrong result.");
                checks.Add("Port rename retains both connections and input value");

                string valid = node.functionBody; node.draft.code = "Out = missing_function(Value);";
                bool rejected = false;
                try { WhimShaderFunctions.CompileAndApply(node); } catch (FormatException) { rejected = true; }
                Require(rejected && node.functionBody == valid && graph.GetEdges(node.GetSlotReference(0)).Any(), "Invalid draft changed applied graph.");
                checks.Add("Invalid HLSL is rejected without changing the applied graph");
                string json = MultiJson.Serialize(graph);
                var restored = new GraphData { messageManager = new MessageManager() };
                MultiJson.Deserialize(restored, json); restored.OnEnable();
                var restoredNode = (WhimShaderCustomFunctionNode)restored.GetNodeFromId(node.objectId);
                Require(restoredNode.draft.code.Contains("missing_function") && restoredNode.functionBody == valid, "Draft/applied separation was lost on reopen.");
                restored.OnDisable(); checks.Add("Serialization preserves invalid draft and valid applied code separately");

                node.draft.code = "Out = Value * 0.25;";
                Undo.IncrementCurrentGroup(); WhimShaderFunctions.CompileAndApply(node); Undo.FlushUndoRecordObjects(); Undo.IncrementCurrentGroup();
                string id = node.objectId; Undo.PerformUndo(); owner.HandleUndoRedo(); node = (WhimShaderCustomFunctionNode)graph.GetNodeFromId(id);
                Require(node.functionBody == valid, "Undo did not restore the applied function.");
                Undo.PerformRedo(); owner.HandleUndoRedo(); node = (WhimShaderCustomFunctionNode)graph.GetNodeFromId(id);
                Require(node.functionBody.Contains("0.25"), "Redo did not restore the compiled function.");
                checks.Add("Compile Undo/Redo restores applied code and ports");

                node.draft = new WhimFunctionDraft { mode = WhimFunctionSource.Source, code =
                    "float Helper(float x) { return x * 2; }\nfloat Branch(inout float x, out float xOut) { x = Helper(x); xOut = x + 1; return x + 2; }" };
                var definitions = WhimShaderFunctionDiscovery.Parse(node.draft.code);
                node.draft.entry = WhimShaderFunctions.Key(definitions.Single(d => d.Name == "Branch"));
                WhimShaderFunctions.DiscoverPorts(node.draft);
                Require(node.draft.ports.Select(p => p.name).Distinct().Count() == 4, "Inout generated colliding names.");
                var identities = node.draft.ports.Select(p => p.id).ToArray();
                node.draft.entry = WhimShaderFunctions.Key(definitions.Single(d => d.Name == "Helper")); WhimShaderFunctions.DiscoverPorts(node.draft);
                node.draft.entry = WhimShaderFunctions.Key(definitions.Single(d => d.Name == "Branch")); WhimShaderFunctions.DiscoverPorts(node.draft);
                Require(identities.SequenceEqual(node.draft.ports.Select(p => p.id)), "Browsing entries changed stable port IDs.");
                WhimShaderFunctions.CompileAndApply(node);
                Require(node.functionBody.Contains("Branch(") && node.appliedSource.Contains("Helper"), "Embedded source was not applied.");
                checks.Add("Full source, helpers, return values, inout and collision-safe ports compile");
                checks.Add("Browsing entry functions preserves port identities");
                var renamed = new WhimFunctionDraft { mode = WhimFunctionSource.Source, code = "float Renamed(float Before) { return Before; }" };
                renamed.entry = WhimShaderFunctions.Key(WhimShaderFunctionDiscovery.Parse(renamed.code).Single());
                renamed.code = "float Renamed(float After) { return After; }"; WhimShaderFunctions.DiscoverPorts(renamed);
                Require(renamed.ports.Any(p => p.name == "After"), "Source signature edits did not update discovered ports.");
                checks.Add("Editing an unambiguous source signature refreshes its ports");

                folder = "Assets/WhimShaderValidation/Function_" + Guid.NewGuid().ToString("N");
                AssetDatabase.CreateFolder("Assets/WhimShaderValidation", Path.GetFileName(folder));
                File.WriteAllText(folder + "/Helper.hlsl", "float TestHelper(float x) { return x * 0.75; }");
                File.WriteAllText(folder + "/Function.hlsl", "#include \"Helper.hlsl\"\nvoid FileFunction_float(float Value, out float Out) { Out = TestHelper(Value); }");
                AssetDatabase.ImportAsset(folder + "/Helper.hlsl", ImportAssetOptions.ForceSynchronousImport);
                AssetDatabase.ImportAsset(folder + "/Function.hlsl", ImportAssetOptions.ForceSynchronousImport);
                node.draft = new WhimFunctionDraft { mode = WhimFunctionSource.File, fileGuid = AssetDatabase.AssetPathToGUID(folder + "/Function.hlsl") };
                node.draft.entry = WhimShaderFunctions.Key(WhimShaderFunctionDiscovery.Parse(WhimShaderFunctions.Source(node.draft)).Single());
                WhimShaderFunctions.CompileAndApply(node);
                node.draft.code = WhimShaderFunctions.EmbedFile(node.draft); node.draft.mode = WhimFunctionSource.Source;
                Require(node.draft.code.Contains(folder + "/Helper.hlsl"), "Embedding lost relative include location.");
                WhimShaderFunctions.CompileAndApply(node); checks.Add("File mode and Convert to Code retain relative includes");

                var legacy = new CustomFunctionNode { sourceType = HlslSourceType.String, functionName = "Legacy", functionBody = "Out = In;" };
                legacy.AddSlot(new Vector1MaterialSlot(7, "In", "In", SlotType.Input, .7f));
                legacy.AddSlot(new Vector1MaterialSlot(9, "Out", "Out", SlotType.Output, 0)); graph.AddNode(legacy);
                root = graph.GetNodes<BlockNode>().First(n => n.descriptor == WhimSurfaceBlocks.Color);
                graph.Connect(legacy.GetSlotReference(9), root.GetSlotReference(0));
                var upgraded = WhimShaderFunctions.UpgradeLegacy(graph);
                var legacyNew = upgraded.GetNodeFromId(legacy.objectId) as WhimShaderCustomFunctionNode;
                Require(legacyNew != null && legacyNew.functionBody == legacy.functionBody && upgraded.GetEdges(legacyNew.GetSlotReference(9)).Any(),
                    "Native upgrade lost code or wires: type=" + upgraded.GetNodeFromId(legacy.objectId)?.GetType().Name + "; body=" + legacyNew?.functionBody + "; edges=" + upgraded.edges.Count());
                Require(Mathf.Abs(legacyNew.FindSlot<Vector1MaterialSlot>(7).value - .7f) < .0001f, "Native upgrade lost input default.");
                upgraded.OnDisable(); checks.Add("Existing native functions retain IDs, slots, defaults and wires");
                return new { passed = checks.Count, checks };
            }
            finally
            {
                if (folder != null) AssetDatabase.DeleteAsset(folder);
                Undo.ClearUndo(owner); graph.OnDisable(); UnityEngine.Object.DestroyImmediate(owner);
            }
        }

        static float Render(GraphData graph, AbstractMaterialNode node)
        {
            var shader = ShaderUtil.CreateShaderAsset(new Generator(graph, node, GenerationMode.Preview, "Hidden/WhimShader/FunctionPixel").generatedShader, false);
            var material = new Material(shader); var target = RenderTexture.GetTemporary(4, 4, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            var previous = RenderTexture.active; var pixel = new Texture2D(4, 4, TextureFormat.RGBAFloat, false, true);
            try
            {
                var properties = new List<PreviewProperty>();
                foreach (var source in graph.GetNodes<AbstractMaterialNode>()) source.CollectPreviewMaterialProperties(properties);
                foreach (var property in properties)
                    if (property.propType == PropertyType.Float) material.SetFloat(property.name, property.floatValue);
                ShaderUtil.CompilePass(material, 0, true); Graphics.Blit(Texture2D.whiteTexture, target, material, 0);
                RenderTexture.active = target; pixel.ReadPixels(new Rect(0, 0, 4, 4), 0, 0); pixel.Apply(); return pixel.GetPixel(2, 2).r;
            }
            finally { RenderTexture.active = previous; RenderTexture.ReleaseTemporary(target); UnityEngine.Object.DestroyImmediate(pixel); UnityEngine.Object.DestroyImmediate(material); UnityEngine.Object.DestroyImmediate(shader); }
        }

        public static string CreateFixture()
        {
            var graph = WhimShaderURP.CreateGraph(); var owner = graph.owner;
            try
            {
                var node = new WhimShaderCustomFunctionNode(); graph.AddNode(node);
                node.draft.code = "// Edit and press Compile\n$precision2 uv = In.xx;\nOut = sin(uv.x * 6.283185) * 0.5 + 0.5;";
                WhimShaderFunctions.CompileAndApply(node);
                graph.Connect(node.GetSlotReference(1), graph.GetNodes<BlockNode>().First(n => n.descriptor == WhimSurfaceBlocks.Color).GetSlotReference(0));
                var draw = node.drawState; draw.position = new Rect(-450, 300, 0, 0); node.drawState = draw;
                string path = AssetDatabase.GenerateUniqueAssetPath("Assets/WhimShaderValidation/Custom Function.whimshader");
                WhimShaderWindow.WriteAtomic(path, MultiJson.Serialize(graph)); AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                WhimShaderWindow.Open(path); return path;
            }
            finally { graph.OnDisable(); UnityEngine.Object.DestroyImmediate(owner); }
        }

        public static object CheckEditor()
        {
            var window = WhimShaderWindow.Open("Assets/WhimShaderValidation/Custom Function.whimshader");
            var node = window.Graph.GetNodes<WhimShaderCustomFunctionNode>().Single();
            var editor = window.rootVisualElement.Q<WhimShaderFunctionEditor>();
            var field = editor.Q<ShaderCodeField>();
            Require(field != null && field.contentRect.width > 0 && field.contentRect.height > 0, "CodeArea is not visible.");
            string applied = node.functionBody;
            field.Focus(); field.textSelection.SelectRange(0, field.value.Length);
            Require(field.UseReference("// UI validation\nOut = In * 0.5;"), "CodeArea did not accept editing.");
            Require(node.draft.code == field.value && node.functionBody == applied, "Typing changed applied code or lost the draft.");
            field.textSelection.SelectRange(0, field.value.Length);
            using (var tab = KeyDownEvent.GetPooled('\t', KeyCode.Tab, EventModifiers.None)) { tab.target = field; field.SendEvent(tab); }
            Require(field.value.StartsWith("    ") || field.value.StartsWith("\t"), "Tab did not indent the selection.");
            using (var search = KeyDownEvent.GetPooled('f', KeyCode.F, EventModifiers.Control)) { search.target = field; field.SendEvent(search); }
            Require(field.SearchHasFocus || field.Q(className: "whimshader-code-search") != null, "Search UI was not created.");
            editor.Compile(); Require(node.functionBody.Contains("In * 0.5"), "Compile button did not apply CodeArea text.");
            var view = window.PreviewService.Editor.graphView.nodes.OfType<MaterialNodeView>().Single(v => v.node == node);
            window.PreviewService.Editor.graphView.ClearSelection(); window.PreviewService.Editor.graphView.AddToSelection(view);
            window.PreviewService.Editor.inspectorView.RefreshInspectables();
            Require(window.rootVisualElement.Query<VisualElement>().ToList().All(e => e.GetType().Name != "HlslFunctionView"), "Native inspector exposes the generated wrapper for editing.");
            window.SaveChanges();
            string path = "Assets/WhimShaderValidation/Custom Function.whimshader";
            var restored = new GraphData { messageManager = new MessageManager() };
            MultiJson.Deserialize(restored, File.ReadAllText(path)); restored.OnEnable();
            var reopened = restored.GetNodes<WhimShaderCustomFunctionNode>().Single();
            Require(reopened.draft.code == node.draft.code && reopened.functionBody == node.functionBody, "Saved CodeArea state did not reopen correctly.");
            restored.OnDisable();
            return new { passed = 6, checks = new[] { "Visible CodeArea", "Typing preserves applied code", "Tab indentation", "Search", "Compile and protected Inspector", "Save/reopen" } };
        }

        public static object CheckPortTypes()
        {
            var graph = WhimShaderURP.CreateGraph(); var owner = graph.owner;
            var passed = new List<string>();
            try
            {
                foreach (string type in WhimShaderFunctions.PortTypes)
                {
                    var node = new WhimShaderCustomFunctionNode(); graph.AddNode(node);
                    node.draft.ports[0].type = node.draft.ports[1].type = type;
                    WhimShaderFunctions.CompileAndApply(node); passed.Add(type); graph.RemoveNode(node);
                }
                return new { passed = passed.Count, types = passed };
            }
            catch (Exception error) { throw new InvalidOperationException("Port type after " + string.Join(", ", passed) + ": " + error.Message, error); }
            finally { Undo.ClearUndo(owner); graph.OnDisable(); UnityEngine.Object.DestroyImmediate(owner); }
        }
    }
}
