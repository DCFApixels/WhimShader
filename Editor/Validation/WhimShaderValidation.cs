using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Graphing;
using UnityEditor.Graphing.Util;
using UnityEditor.ShaderGraph;
using UnityEditor.ShaderGraph.Serialization;
using UnityEngine;

namespace DCFApixels.WhimShader
{
    public static class WhimShaderValidation
    {
        public static object Run()
        {
            var checks = new List<string>();
            var graph = WhimShaderURP.CreateGraph();
            var owner = graph.owner;
            try
            {
                var root = graph.GetNodes<BlockNode>().First(n => n.descriptor == WhimSurfaceBlocks.Color);
                var vertex = graph.GetNodes<BlockNode>().First(n => n.descriptor == BlockFields.VertexDescription.Position);
                var value = new Vector1Node();
                value.FindInputSlot<Vector1MaterialSlot>(Vector1Node.InputSlotXId).value = .25f;
                graph.AddNode(value);
                graph.Connect(value.GetSlotReference(Vector1Node.OutputSlotId), root.GetSlotReference(0));
                graph.Connect(value.GetSlotReference(Vector1Node.OutputSlotId), vertex.GetSlotReference(0));
                graph.ValidateGraph();
                AssertNoGraphErrors(graph, "Shared vertex/fragment branch"); checks.Add("Shared vertex/fragment branch");
                string source = new Generator(graph, graph.outputNode, GenerationMode.ForReals, "WhimShader/Validation", humanReadable: true).generatedShader;
                if (!source.Contains("VertexDescriptionFunction") || !source.Contains("SurfaceDescriptionFunction") || !source.Contains("ShadowCaster"))
                    throw new Exception("Generated shader is missing stage/pass functions.");
                checks.Add("ShaderLab contains vertex, fragment and shadow passes");
                string json = MultiJson.Serialize(graph);
                var reopened = new GraphData { messageManager = new MessageManager() };
                MultiJson.Deserialize(reopened, json); reopened.OnEnable(); reopened.ValidateGraph();
                if (graph.edges.Count() != reopened.edges.Count() || graph.GetNodes<AbstractMaterialNode>().Count() != reopened.GetNodes<AbstractMaterialNode>().Count())
                    throw new Exception("Graph roundtrip changed nodes or edges.");
                checks.Add("Native graph serialization roundtrip");
                var derivative = new DDXNode(); graph.AddNode(derivative);
                graph.Connect(value.GetSlotReference(0), derivative.GetSlotReference(0));
                var invalid = graph.Connect(derivative.GetSlotReference(1), vertex.GetSlotReference(0));
                graph.ValidateGraph();
                if (!HasErrors(graph)) throw new Exception("DDX in a vertex branch was not rejected.");
                checks.Add("Fragment-only dependency rejected in Vertex");
                graph.RemoveEdge(invalid); graph.ValidateGraph();
                AssertNoGraphErrors(graph, "Reconnect diagnostics");
                checks.Add("Old stage diagnostics clear after disconnection");
                var layout = new ShaderGraphWorkspaceLayout(); layout.Reset(); layout.Validate();
                var kinds = layout.treeNodes.SelectMany(n => n.tabs).ToArray();
                if (kinds.Length != 4 || kinds.Distinct().Count() != 4 || !kinds.Contains(ShaderGraphPanelKind.ShaderSettings))
                    throw new Exception("Workspace defaults do not contain all four panels exactly once.");
                checks.Add("Docking defaults and Shader Settings tab");
                return new { passed = checks.Count, checks, shaderCharacters = source.Length };
            }
            finally { UnityEngine.Object.DestroyImmediate(owner); }
        }

        public static object CreateSmokeGraph()
        {
            const string folder = "Assets/WhimShaderValidation";
            if (!AssetDatabase.IsValidFolder(folder)) AssetDatabase.CreateFolder("Assets", "WhimShaderValidation");
            string path = AssetDatabase.GenerateUniqueAssetPath(folder + "/Smoke.whimshader");
            var graph = WhimShaderURP.CreateGraph();
            var root = graph.GetNodes<BlockNode>().First(n => n.descriptor == WhimSurfaceBlocks.Color);
            var uv = new UVNode(); graph.AddNode(uv);
            var draw = uv.drawState; draw.position = new Rect(-400, 550, 0, 0); uv.drawState = draw;
            graph.Connect(uv.GetSlotReference(0), root.GetSlotReference(0));
            WhimShaderWindow.WriteAtomic(path, MultiJson.Serialize(graph));
            UnityEngine.Object.DestroyImmediate(graph.owner);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
            if (shader == null) throw new Exception("Graph importer did not produce a Shader asset.");
            var errors = ShaderUtil.GetShaderMessages(shader).Where(m => m.severity == UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error).Select(m => m.message).ToArray();
            if (errors.Length > 0) throw new Exception(string.Join("\n", errors));
            WhimShaderWindow.Open(path);
            return new { path, shader = shader.name, messages = ShaderUtil.GetShaderMessages(shader).Select(m => m.message).ToArray() };
        }

        static bool HasErrors(GraphData graph) => graph.messageManager.HasSeverity(_ => true, UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error);
        static void AssertNoGraphErrors(GraphData graph, string context)
        {
            if (HasErrors(graph)) throw new Exception(context + " produced graph errors.");
        }
    }
}
