using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Graphing;
using UnityEditor.Graphing.Util;
using UnityEditor.ShaderGraph;
using UnityEditor.ShaderGraph.Internal;
using UnityEditor.ShaderGraph.Serialization;
using UnityEngine;

namespace DCFApixels.WhimShader
{
    public static class WhimShaderDocumentValidation
    {
        public static object Run()
        {
            var graph = WhimShaderURP.CreateGraph();
            var owner = graph.owner;
            var checks = new List<string>();
            try
            {
                var number = new Vector1ShaderProperty { displayName = "Amount", value = .37f };
                var color = new ColorShaderProperty { displayName = "Tint", value = Color.cyan };
                graph.AddGraphInput(number); graph.AddGraphInput(color);
                var metadata = new WhimShaderParameterLayoutNode(); graph.AddNode(metadata);
                var layout = metadata.layout; layout.Normalize(graph);
                var group = layout.Add(WhimShaderParameterItemKind.Group, null, "Surface");
                var foldout = layout.Add(WhimShaderParameterItemKind.Foldout, group.id, "Details");
                var tabs = layout.Add(WhimShaderParameterItemKind.Tabs, null, "Modes");
                var tab = layout.Add(WhimShaderParameterItemKind.Tab, tabs.id, "Tint");
                tabs.defaultTabId = tab.id;
                var numberItem = layout.items.Single(item => item.propertyId == number.objectId);
                var colorItem = layout.items.Single(item => item.propertyId == color.objectId);
                Require(layout.Move(numberItem.id, foldout.id), "Move into Foldout failed.");
                Require(layout.Move(colorItem.id, tab.id), "Move into Tab failed.");
                Require(!layout.CanMove(group.id, foldout.id), "Cyclic move accepted.");
                Require(!layout.CanMove(numberItem.id, tabs.id), "Parameter directly under Tabs accepted.");
                graph.ValidateGraph();
                Require(WhimShaderParameterDiagnostics.Validate(graph).Length == 0, "Invalid initial grouped layout.");
                checks.Add("Group/Foldout/Tabs/Tab moves and invalid-parent/cycle rejection");

                string before = WhimShaderParameterDiagnostics.CaptureState(graph);
                Undo.FlushUndoRecordObjects(); Undo.IncrementCurrentGroup();
                owner.RegisterCompleteObjectUndo("WhimShader fixture move");
                Require(layout.Move(numberItem.id, tab.id, colorItem.id), "Cross-group insertion failed.");
                string after = WhimShaderParameterDiagnostics.CaptureState(graph);
                Undo.FlushUndoRecordObjects(); Undo.IncrementCurrentGroup();
                Undo.PerformUndo();
                Require(owner.wasUndoRedoPerformed, "Native graph Undo not recorded.");
                owner.HandleUndoRedo();
                WhimShaderParameterDiagnostics.AssertRestored(graph, before);
                Undo.PerformRedo();
                Require(owner.wasUndoRedoPerformed, "Native graph Redo not recorded.");
                owner.HandleUndoRedo();
                WhimShaderParameterDiagnostics.AssertRestored(graph, after);
                checks.Add("Native GraphObject Undo/Redo restores order, parents and property data");

                var reopened = new GraphData { messageManager = new MessageManager() };
                MultiJson.Deserialize(reopened, MultiJson.Serialize(graph)); reopened.OnEnable(); reopened.ValidateGraph();
                try { WhimShaderParameterDiagnostics.AssertRestored(reopened, after); }
                finally { reopened.OnDisable(); }
                checks.Add("Grouped parameters survive native graph roundtrip");

                using var materialMetadata = new OwnedMetadata(WhimShaderMaterialMetadata.CreateMetadata(graph));
                Require(JsonUtility.ToJson(materialMetadata.Value.layout) == JsonUtility.ToJson(WhimShaderParameterLayoutNode.Find(graph).layout),
                    "Material grouping differs from graph grouping.");
                Require(materialMetadata.Value.parameters.Count == graph.properties.Count(), "Material property mapping differs.");
                checks.Add("Material metadata uses the same group tree and parameter addresses");
                ValidatePreviewPresentation(graph, checks);
                ValidateSubGraph(checks);
                return new { passed = checks.Count, checks };
            }
            finally { Undo.ClearUndo(owner); UnityEngine.Object.DestroyImmediate(owner); }
        }

        static void ValidatePreviewPresentation(GraphData graph, List<string> checks)
        {
            var node = new CombineNode(); graph.AddNode(node); node.previewExpanded = false;
            string id = node.objectId;
            var defaults = WhimShaderNodePresentationMetadata.Read(node);
            var settings = new WhimShaderNodePreviewSettings
            {
                mode = WhimShaderPreviewMode.Height, outputSlotId = 6, channelMask = 5,
                heightChannel = HeightPreviewRenderer.Channel.Luminance, heightScale = -.8f,
                heightTexture = false, highlightOutOfRange = true
            };
            Undo.FlushUndoRecordObjects(); Undo.IncrementCurrentGroup();
            WhimShaderNodePresentationMetadata.Write(node, settings);
            Undo.FlushUndoRecordObjects(); Undo.IncrementCurrentGroup();
            Undo.PerformUndo(); graph.owner.HandleUndoRedo();
            Require(JsonUtility.ToJson(WhimShaderNodePresentationMetadata.Read(graph.GetNodeFromId(id))) == JsonUtility.ToJson(defaults),
                "Preview settings Undo failed.");
            Undo.PerformRedo(); graph.owner.HandleUndoRedo();
            Require(JsonUtility.ToJson(WhimShaderNodePresentationMetadata.Read(graph.GetNodeFromId(id))) == JsonUtility.ToJson(settings),
                "Preview settings Redo failed.");
            var final = settings; final.mode = WhimShaderPreviewMode.ThreeD; final.channelMask = 8;
            WhimShaderNodePresentationMetadata.WriteFinal(graph, final);
            var reopened = new GraphData { messageManager = new MessageManager() };
            MultiJson.Deserialize(reopened, MultiJson.Serialize(graph)); reopened.OnEnable(); reopened.ValidateGraph();
            try
            {
                Require(!reopened.GetNodeFromId(id).previewExpanded, "Collapsed preview was not persisted.");
                Require(JsonUtility.ToJson(WhimShaderNodePresentationMetadata.Read(reopened.GetNodeFromId(id))) == JsonUtility.ToJson(settings),
                    "Node preview mode/output/height settings were not persisted.");
                Require(JsonUtility.ToJson(WhimShaderNodePresentationMetadata.ReadFinal(reopened)) == JsonUtility.ToJson(final),
                    "Final preview settings were not persisted.");
            }
            finally { reopened.OnDisable(); }
            checks.Add("Preview modes, channels, output, height settings and collapse survive Undo/Redo and graph roundtrip");
        }

        static void ValidateSubGraph(List<string> checks)
        {
            const string folder = "Assets/WhimShaderValidation";
            if (!AssetDatabase.IsValidFolder(folder)) AssetDatabase.CreateFolder("Assets", "WhimShaderValidation");
            string path = AssetDatabase.GenerateUniqueAssetPath(folder + "/SharedBranch.shadersubgraph");
            var sub = new GraphData { isSubGraph = true, messageManager = new MessageManager() };
            var owner = ScriptableObject.CreateInstance<GraphObject>(); owner.hideFlags = HideFlags.HideAndDontSave; owner.graph = sub;
            var parent = WhimShaderURP.CreateGraph();
            try
            {
                var property = new Vector1ShaderProperty { displayName = "Amount", value = .37f };
                sub.AddGraphInput(property);
                var input = new PropertyNode(); sub.AddNode(input); input.property = property;
                var output = new SubGraphOutputNode();
                sub.AddNode(output);
                sub.outputNode = output;
                output.AddSlot(new Vector1MaterialSlot(0, "Out", "Out", SlotType.Input, 0));
                sub.AddNode(new WhimShaderParameterLayoutNode());
                sub.Connect(input.GetSlotReference(0), output.GetSlotReference(0)); sub.ValidateGraph();
                WhimShaderWindow.WriteAtomic(path, MultiJson.Serialize(sub));
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                var asset = AssetDatabase.LoadAssetAtPath<SubGraphAsset>(path);
                Require(asset != null && asset.isValid, "Sub Graph import failed.");
                var node = new SubGraphNode(); parent.AddNode(node); node.asset = asset;
                int outputId = node.GetOutputSlots<MaterialSlot>().First().id;
                var vertex = parent.GetNodes<BlockNode>().Single(n => n.descriptor == BlockFields.VertexDescription.Position);
                var fragment = parent.GetNodes<BlockNode>().Single(n => n.descriptor == WhimSurfaceBlocks.Color);
                parent.Connect(node.GetSlotReference(outputId), vertex.GetSlotReference(0));
                parent.Connect(node.GetSlotReference(outputId), fragment.GetSlotReference(0)); parent.ValidateGraph();
                Require(!parent.messageManager.HasSeverity(_ => true, UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error), "Shared Sub Graph stage validation failed.");
                var shader = ShaderUtil.CreateShaderAsset(new Generator(parent, parent.outputNode, GenerationMode.ForReals,
                    "Hidden/WhimShader/SubGraphValidation").generatedShader, false);
                var material = new Material(shader);
                bool previous = ShaderUtil.allowAsyncCompilation;
                try
                {
                    ShaderUtil.allowAsyncCompilation = false;
                    ShaderUtil.CompilePass(material, 0, true);
                    Require(!ShaderUtil.ShaderHasError(shader), string.Join("\n", ShaderUtil.GetShaderMessages(shader).Select(m => m.message)));
                }
                finally { ShaderUtil.allowAsyncCompilation = previous; UnityEngine.Object.DestroyImmediate(material); UnityEngine.Object.DestroyImmediate(shader); }
                checks.Add("Native Sub Graph import and a shared vertex/fragment consumer compile");
            }
            finally { UnityEngine.Object.DestroyImmediate(owner); UnityEngine.Object.DestroyImmediate(parent.owner); }
        }

        static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        sealed class OwnedMetadata : IDisposable
        {
            internal WhimShaderMaterialMetadata Value;
            internal OwnedMetadata(WhimShaderMaterialMetadata value) { Value = value; }
            public void Dispose() => UnityEngine.Object.DestroyImmediate(Value);
        }
    }
}
