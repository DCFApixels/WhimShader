using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Graphing;
using UnityEditor.Graphing.Util;
using UnityEditor.Rendering.Universal.ShaderGraph;
using UnityEditor.ShaderGraph;
using UnityEditor.ShaderGraph.Internal;
using UnityEditor.ShaderGraph.Serialization;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace DCFApixels.WhimShader
{
    /// <summary>Explicit, main-thread validation. Never runs on import or automatically.</summary>
    public static class WhimLutValidation
    {
        [Serializable] sealed class Check { public string name; public bool passed; public string detail; }
        [Serializable] sealed class Report
        {
            public bool passed;
            public string fixtureFolder;
            public List<Check> checks = new List<Check>();
        }
        static bool running;

        /// <summary>Returns JSON. Retains fresh fixtures for inspection; never touches existing assets/scenes.</summary>
        public static string Run()
        {
            if (running || EditorApplication.isCompiling || EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Run LUT validation once, in idle Edit Mode.");
            running = true;
            var report = new Report();
            Fixture fixture = null;
            bool oldAsync = ShaderUtil.allowAsyncCompilation;
            try
            {
                ShaderUtil.allowAsyncCompilation = false;
                Undo.FlushUndoRecordObjects();
                Undo.IncrementCurrentGroup(); // Keep fixture Undo separate from the user's last edit.
                const string parent = "Assets/WhimShaderValidation";
                if (!AssetDatabase.IsValidFolder(parent)) AssetDatabase.CreateFolder("Assets", "WhimShaderValidation");
                string child = "LUT_" + Guid.NewGuid().ToString("N");
                AssetDatabase.CreateFolder(parent, child);
                report.fixtureFolder = parent + "/" + child;
                fixture = new Fixture();
                RunCheck(report, "Native graph property/node/edge roundtrip and deep copy", () => Roundtrip(fixture));
                RunCheck(report, "Linear float LUT endpoints, signed/HDR values and weighted curve samples", () => Pixels(fixture));
                RunCheck(report, "Preview reuse, updates, release and no transient serialization", () => Preview(fixture));
                RunCheck(report, "SoftSign/InverseSoftSign native float1-4 inference", () => DynamicWidths(fixture));
                Shader imported = null;
                RunCheck(report, "Native import: Gradient vertex, Curve fragment, persistent defaults", () =>
                {
                    imported = ImportStages(fixture, report.fixtureFolder, false);
                });
                RunCheck(report, "Native import: Curve vertex, Gradient fragment", () => ImportStages(fixture, report.fixtureFolder, true));
                RunCheck(report, "Material copy-on-write, Undo/Redo and disk reopen", () => Materials(fixture, imported, report.fixtureFolder));
                RunCheck(report, "Standalone export/default textures and dependency collection", () => Export(fixture, report.fixtureFolder));
                RunCheck(report, "GPU actual node code: Gradient/Curve in both stages and SoftSign inverse", () => Gpu(fixture, report.fixtureFolder));
            }
            catch (Exception error)
            {
                report.checks.Add(new Check { name = "Runner setup", passed = false, detail = error.ToString() });
            }
            finally
            {
                try { fixture?.Dispose(); }
                finally { ShaderUtil.allowAsyncCompilation = oldAsync; running = false; Undo.IncrementCurrentGroup(); }
            }
            report.passed = report.checks.Count == 9 && report.checks.All(c => c.passed);
            string json = JsonUtility.ToJson(report, true);
            if (!string.IsNullOrEmpty(report.fixtureFolder)) File.WriteAllText(report.fixtureFolder + "/report.json", json);
            return json;
        }

        static void RunCheck(Report report, string name, Action action)
        {
            var result = new Check { name = name };
            try { action(); result.passed = true; result.detail = "PASS"; }
            catch (Exception error) { result.detail = error.ToString(); }
            report.checks.Add(result);
        }

        sealed class Fixture : IDisposable
        {
            public readonly GraphData graph;
            public readonly WhimGradientShaderProperty gradient = new WhimGradientShaderProperty { displayName = "Gradient", overrideReferenceName = "_GradientLUT" };
            public readonly WhimCurveShaderProperty curve = new WhimCurveShaderProperty { displayName = "Curve", overrideReferenceName = "_CurveLUT" };
            public readonly Vector1ShaderProperty time = new Vector1ShaderProperty { displayName = "Time", overrideReferenceName = "_TestT", value = .37f };
            public readonly PropertyNode gradientInput, curveInput, timeInput;
            public readonly SampleGradientLutNode gradientSample = new SampleGradientLutNode();
            public readonly SampleCurveLutNode curveSample = new SampleCurveLutNode();
            public Fixture()
            {
                graph = NewGraph();
                var g = new WhimLutData { kind = WhimLutKind.Gradient, resolution = 17 };
                g.gradient.SetKeys(new[] {
                    new GradientColorKey(new Color(.1f, 2, .3f), 0),
                    new GradientColorKey(new Color(3, .2f, .8f), .4f),
                    new GradientColorKey(new Color(.7f, .4f, 2), 1) },
                    new[] { new GradientAlphaKey(.2f, 0), new GradientAlphaKey(.85f, 1) });
                gradient.SetData(g);
                var c = new WhimLutData { kind = WhimLutKind.Curve, resolution = 33 };
                c.curve = new AnimationCurve(
                    new Keyframe(0, -2, 1, 4) { weightedMode = WeightedMode.Both, inWeight = .2f, outWeight = .25f },
                    new Keyframe(.4f, 3, -.5f, -1) { weightedMode = WeightedMode.Both, inWeight = .3f, outWeight = .2f },
                    new Keyframe(1, -1, -3, 2) { weightedMode = WeightedMode.Both, inWeight = .2f, outWeight = .4f });
                c.curve.preWrapMode = WrapMode.Loop; c.curve.postWrapMode = WrapMode.PingPong;
                curve.SetData(c);
                gradientInput = AddProperty(graph, gradient); curveInput = AddProperty(graph, curve); timeInput = AddProperty(graph, time);
                graph.AddNode(gradientSample); graph.AddNode(curveSample);
                graph.Connect(gradientInput.GetSlotReference(0), gradientSample.GetSlotReference(0));
                graph.Connect(curveInput.GetSlotReference(0), curveSample.GetSlotReference(0));
                graph.Connect(timeInput.GetSlotReference(0), gradientSample.GetSlotReference(1));
                graph.Connect(timeInput.GetSlotReference(0), curveSample.GetSlotReference(1));
                Stages(false);
            }
            public void Stages(bool swap)
            {
                var vertex = graph.GetNodes<BlockNode>().First(n => n.descriptor == BlockFields.VertexDescription.Position);
                var fragment = graph.GetNodes<BlockNode>().First(n => n.descriptor == BlockFields.SurfaceDescription.BaseColor);
                graph.Connect((swap ? (AbstractMaterialNode)curveSample : gradientSample).GetSlotReference(2), vertex.GetSlotReference(0));
                graph.Connect((swap ? (AbstractMaterialNode)gradientSample : curveSample).GetSlotReference(2), fragment.GetSlotReference(0));
                graph.ValidateGraph();
                Require(!graph.messageManager.HasSeverity(_ => true, UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error), "Native graph stage validation failed.");
            }
            public void Dispose() { WhimLutGraphProperties.ReleasePreview(graph); graph.OnDisable(); Object.DestroyImmediate(graph.owner); }
        }

        static GraphData NewGraph()
        {
            var graph = new GraphData { messageManager = new MessageManager(), assetGuid = Guid.NewGuid().ToString("N") };
            var owner = ScriptableObject.CreateInstance<GraphObject>(); owner.hideFlags = HideFlags.HideAndDontSave; owner.graph = graph;
            graph.AddContexts();
            var target = new UniversalTarget();
            Require(target.TrySetActiveSubTarget(typeof(UniversalUnlitSubTarget)), "Native Unlit target unavailable.");
            graph.InitializeOutputs(new Target[] { target }, new[] { BlockFields.VertexDescription.Position, BlockFields.SurfaceDescription.BaseColor, BlockFields.SurfaceDescription.Alpha });
            graph.OnEnable();
            return graph;
        }

        static PropertyNode AddProperty(GraphData graph, AbstractShaderProperty property)
        {
            graph.AddGraphInput(property);
            var node = new PropertyNode { property = property }; graph.AddNode(node); return node;
        }

        static void Roundtrip(Fixture f)
        {
            string json = MultiJson.Serialize(f.graph);
            var restored = new GraphData { messageManager = new MessageManager() };
            var owner = ScriptableObject.CreateInstance<GraphObject>(); owner.hideFlags = HideFlags.HideAndDontSave; owner.graph = restored;
            try
            {
                MultiJson.Deserialize(restored, json); restored.OnEnable(); restored.ValidateGraph();
                var g = restored.properties.OfType<WhimGradientShaderProperty>().Single();
                var c = restored.properties.OfType<WhimCurveShaderProperty>().Single();
                Require(JsonUtility.ToJson(g.Data) == JsonUtility.ToJson(f.gradient.Data), "Gradient keys/mode/resolution changed.");
                Require(JsonUtility.ToJson(c.Data) == JsonUtility.ToJson(f.curve.Data), "Curve keys/tangents/weights/wrap/resolution changed.");
                Require(g.referenceName == f.gradient.referenceName && c.referenceName == f.curve.referenceName, "Reference names changed.");
                Require(restored.edges.Count() == f.graph.edges.Count(), "Edges lost.");
                Require(restored.edges.Select(EdgeKey).OrderBy(s => s).SequenceEqual(f.graph.edges.Select(EdgeKey).OrderBy(s => s)), "Edge endpoints changed.");
                Require(restored.GetNodes<SampleGradientLutNode>().Count() == 1 && restored.GetNodes<SampleCurveLutNode>().Count() == 1, "Sample node types lost.");
                Require(!restored.messageManager.HasSeverity(_ => true, UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error), "Deserialized graph failed validation.");
                var copy = (WhimCurveShaderProperty)c.Copy();
                var changed = copy.Data; changed.curve.MoveKey(0, new Keyframe(0, 20)); copy.SetData(changed);
                Near(c.Data.curve.Evaluate(0), -2, 1e-6f, "Copy aliases original curve");
                var gradientCopy = (WhimGradientShaderProperty)g.Copy();
                var gradientChanged = gradientCopy.Data;
                gradientChanged.gradient.SetKeys(new[] { new GradientColorKey(Color.black, 0), new GradientColorKey(Color.white, 1) }, gradientChanged.gradient.alphaKeys);
                gradientCopy.SetData(gradientChanged);
                Near(g.Data.gradient.Evaluate(0), f.gradient.Data.gradient.Evaluate(0), 1e-6f, "Copy aliases original gradient");
            }
            finally { restored.OnDisable(); Object.DestroyImmediate(owner); }
        }

        static string EdgeKey(IEdge edge) => edge.outputSlot.node.objectId + ":" + edge.outputSlot.slotId + ">" + edge.inputSlot.node.objectId + ":" + edge.inputSlot.slotId;

        static void Pixels(Fixture f)
        {
            foreach (var property in new WhimLutShaderProperty[] { f.gradient, f.curve })
            {
                var data = property.Data; var texture = WhimLutTexture.Create(data);
                try
                {
                    Require(texture.width == data.resolution && texture.height == 1 && texture.mipmapCount == 1 && texture.format == TextureFormat.RGBAFloat, "LUT storage mismatch.");
                    Require(texture.wrapMode == TextureWrapMode.Clamp && texture.filterMode == FilterMode.Bilinear, "LUT sampling state mismatch.");
                    for (int i = 0; i < data.resolution; ++i) Near(texture.GetPixel(i, 0), Evaluate(data, (float)i / (data.resolution - 1)), 3e-6f, "Baked pixel " + i);
                    var block = new MaterialPropertyBlock(); WhimLutTexture.Bind(block, "_LUT", texture);
                    Require(block.GetTexture("_LUT") == texture, "PropertyBlock texture not bound.");
                    Near(block.GetVector("_LUT_TexelSize").z, data.resolution, 0, "PropertyBlock texel size");
                    if (data.kind == WhimLutKind.Curve)
                    {
                        data.curve = AnimationCurve.Linear(0, -4, 1, 5);
                        WhimLutTexture.Update(texture, data);
                        Near(texture.GetPixel(0, 0).r, -4, 1e-6f, "Runtime update start"); Near(texture.GetPixel(texture.width - 1, 0).r, 5, 1e-6f, "Runtime update end");
                    }
                }
                finally { WhimLutTexture.DestroyOwned(texture); }
            }
        }

        static void Preview(Fixture f)
        {
            string before = MultiJson.Serialize(f.graph);
            WhimLutGraphProperties.PreparePreview(f.graph);
            var first = WhimLutGraphProperties.GetPreviewTexture(f.curve);
            WhimLutGraphProperties.PreparePreview(f.graph);
            Require(first == WhimLutGraphProperties.GetPreviewTexture(f.curve), "Unchanged preview was reallocated.");
            Require(before == MultiJson.Serialize(f.graph), "Preview changed graph serialization.");
            var saved = f.curve.Data; var changed = saved.Clone(); changed.curve = AnimationCurve.Linear(0, -7, 1, 6);
            try
            {
                f.curve.SetData(changed); WhimLutGraphProperties.PreparePreview(f.graph);
                Require(first == WhimLutGraphProperties.GetPreviewTexture(f.curve), "Same-size preview update replaced its texture.");
                Near(first.GetPixel(0, 0).r, -7, 1e-6f, "Stale preview pixels");
            }
            finally { f.curve.SetData(saved); WhimLutGraphProperties.ReleasePreview(f.graph); }
            Require(first == null, "ReleasePreview did not destroy the transient texture.");
        }

        static void DynamicWidths(Fixture f)
        {
            for (int width = 1; width <= 4; ++width)
            {
                AbstractShaderProperty property = width switch { 1 => new Vector1ShaderProperty(), 2 => new Vector2ShaderProperty(), 3 => new Vector3ShaderProperty(), _ => new Vector4ShaderProperty() };
                property.displayName = "Width" + width;
                var input = AddProperty(f.graph, property); var soft = new SoftSignNode(); var inverse = new InverseSoftSignNode();
                f.graph.AddNode(soft); f.graph.AddNode(inverse);
                f.graph.Connect(input.GetSlotReference(0), soft.GetSlotReference(0)); f.graph.Connect(soft.GetSlotReference(1), inverse.GetSlotReference(0));
                f.graph.ValidateGraph();
                var expected = width switch { 1 => ConcreteSlotValueType.Vector1, 2 => ConcreteSlotValueType.Vector2, 3 => ConcreteSlotValueType.Vector3, _ => ConcreteSlotValueType.Vector4 };
                Require(soft.FindOutputSlot<MaterialSlot>(1).concreteValueType == expected && inverse.FindOutputSlot<MaterialSlot>(1).concreteValueType == expected, "Incorrect dynamic width " + width);
                Require(soft.FindOutputSlot<MaterialSlot>(1).stageCapability == ShaderStageCapability.All && inverse.FindOutputSlot<MaterialSlot>(1).stageCapability == ShaderStageCapability.All, "SoftSign stage restriction.");
            }
        }

        static Shader ImportStages(Fixture f, string folder, bool swap)
        {
            f.Stages(swap);
            string path = folder + (swap ? "/CurveVertex.whimshader" : "/GradientVertex.whimshader");
            File.WriteAllText(path, MultiJson.Serialize(f.graph));
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
            Require(shader != null && !shader.name.Contains("Error"), "Importer returned no valid shader.");
            Compile(shader);
            var assets = AssetDatabase.LoadAllAssetsAtPath(path).OfType<WhimLutAsset>().ToArray();
            Require(assets.Length == 2 && assets.All(a => a.Texture != null), "Importer did not persist both LUT metadata/texture pairs.");
            var material = new Material(shader);
            try { foreach (var asset in assets) Require(material.GetTexture(asset.PropertyName) == asset.Texture, "Default texture missing for " + asset.PropertyName); }
            finally { Object.DestroyImmediate(material); }
            return shader;
        }

        static void Materials(Fixture f, Shader shader, string folder)
        {
            Require(shader != null, "Native import prerequisite failed.");
            var a = new Material(shader); AssetDatabase.CreateAsset(a, folder + "/A.mat");
            var originalDefault = a.GetTexture(f.curve.referenceName);
            var ownedA = WhimLutMaterialStore.Apply(a, f.curve.referenceName, f.curve.Data);
            Require(ownedA.OwnerMaterial == a && ownedA.Texture != originalDefault, "First edit changed a graph default.");
            var b = new Material(a); AssetDatabase.CreateAsset(b, folder + "/B.mat");
            var changed = f.curve.Data; changed.curve = AnimationCurve.Linear(0, -9, 1, 8);
            var ownedB = WhimLutMaterialStore.Apply(b, f.curve.referenceName, changed);
            Require(ownedA != ownedB && ownedA.Texture != ownedB.Texture && ownedB.OwnerMaterial == b, "Duplicated material did not copy on write.");
            Near(ownedA.Texture.GetPixel(0, 0).r, -2, 1e-6f, "Other material changed");
            Undo.IncrementCurrentGroup(); int group = Undo.GetCurrentGroup();
            var edited = ownedB.Data; edited.curve = AnimationCurve.Linear(0, -5, 1, 4);
            edited.resolution = 65;
            WhimLutMaterialStore.UpdateAsset(ownedB, edited);
            Undo.FlushUndoRecordObjects(); Undo.CollapseUndoOperations(group); Undo.PerformUndo();
            Near(ownedB.Data.curve.Evaluate(0), -9, 1e-6f, "Undo metadata"); Near(ownedB.Texture.GetPixel(0, 0).r, -9, 1e-6f, "Undo baked pixels");
            Require(ownedB.Texture.width == changed.resolution && ownedB.Data.resolution == changed.resolution, "Undo resolution mismatch.");
            Undo.PerformRedo(); Near(ownedB.Texture.GetPixel(0, 0).r, -5, 1e-6f, "Redo baked pixels");
            Require(ownedB.Texture.width == 65 && ownedB.Data.resolution == 65, "Redo resolution mismatch.");
            Require(a.shader == shader && b.shader == shader, "LUT editing replaced the shader.");
            string assetPath = AssetDatabase.GetAssetPath(ownedB);
            EditorUtility.SetDirty(ownedB); AssetDatabase.SaveAssetIfDirty(ownedB);
            Undo.ClearUndo(ownedB); Undo.ClearUndo(ownedB.Texture); Undo.ClearUndo(a); Undo.ClearUndo(b);
            string materialPath = AssetDatabase.GetAssetPath(b);
            AssetDatabase.SaveAssetIfDirty(b);
            Resources.UnloadAsset(ownedB.Texture);
            Resources.UnloadAsset(ownedB);
            Resources.UnloadAsset(b);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            AssetDatabase.ImportAsset(materialPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            var reopened = AssetDatabase.LoadAssetAtPath<WhimLutAsset>(assetPath);
            var reopenedMaterial = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            Near(reopened.Data.curve.Evaluate(0), -5, 1e-6f, "Reopened metadata"); Near(reopened.Texture.GetPixel(0, 0).r, -5, 1e-6f, "Reopened pixels");
            Require(reopened.Texture.width == 65 && reopened.Data.resolution == 65, "Reopened resolution lost.");
            Require(reopened.OwnerMaterial == reopenedMaterial && reopened.PropertyName == f.curve.referenceName, "Reopened material ownership lost.");
            Require(reopenedMaterial.GetTexture(f.curve.referenceName) == reopened.Texture, "Reopened material texture binding lost.");
        }

        static void Export(Fixture f, string folder)
        {
            string graphBefore = MultiJson.Serialize(f.graph);
            var assets = WhimLutGraphProperties.ExportDefaults(f.graph, folder);
            Require(assets.Count == 2 && assets.All(a => AssetDatabase.IsMainAsset(a)), "Export did not save both independent metadata assets.");
            Require(graphBefore == MultiJson.Serialize(f.graph), "Export mutated the graph.");
            string source = new Generator(f.graph, f.graph.outputNode, GenerationMode.ForReals, "WhimShader/ValidationExport").generatedShader;
            string path = folder + "/Export.shader"; File.WriteAllText(path, source);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var importer = (ShaderImporter)AssetImporter.GetAtPath(path);
            importer.SetDefaultTextures(assets.Select(a => a.PropertyName).ToArray(), assets.Select(a => (Texture)a.Texture).ToArray()); importer.SaveAndReimport();
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(path); Compile(shader);
            var material = new Material(shader); AssetDatabase.CreateAsset(material, folder + "/Export.mat");
            foreach (var asset in assets) Require(material.GetTexture(asset.PropertyName) == asset.Texture, "Exported shader default missing.");
            string[] dependencies = WhimLutMaterialStore.CollectExportAssets(new[] { material });
            foreach (var asset in assets) Require(dependencies.Contains(AssetDatabase.GetAssetPath(asset)), "Export dependency missing for " + asset.PropertyName);
        }

        static void Compile(Shader shader)
        {
            Require(shader != null && shader.isSupported, "Shader is missing or unsupported on this graphics device.");
            var material = new Material(shader);
            try
            {
                Require(material.passCount > 0, "Shader has no passes.");
                for (int pass = 0; pass < material.passCount; ++pass)
                {
                    ShaderUtil.CompilePass(material, pass);
                    Require(ShaderUtil.IsPassCompiled(material, pass), "Pass still pending/uncompiled: " + pass);
                }
                var errors = ShaderUtil.GetShaderMessages(shader).Where(m => m.severity == UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error).Select(m => m.message);
                Require(!errors.Any(), string.Join("\n", errors));
            }
            finally { Object.DestroyImmediate(material); }
        }

        static Color Evaluate(WhimLutData data, float t)
        {
            if (data.kind == WhimLutKind.Gradient) return data.gradient.Evaluate(t);
            float value = data.curve.Evaluate(t); return new Color(value, value, value, 1);
        }
        static Color ExpectedLut(WhimLutData data, float t)
        {
            float sample = Mathf.Clamp01(t) * (data.resolution - 1); int low = Mathf.FloorToInt(sample); int high = Mathf.Min(low + 1, data.resolution - 1);
            return Color.LerpUnclamped(Evaluate(data, (float)low / (data.resolution - 1)), Evaluate(data, (float)high / (data.resolution - 1)), sample - low);
        }
        static void NearGpu(Color actual, Color expected, WhimLutData data, float t, string message)
        {
            float sample = Mathf.Clamp01(t) * (data.resolution - 1);
            int low = Mathf.FloorToInt(sample), high = Mathf.Min(low + 1, data.resolution - 1);
            var a = Evaluate(data, (float)low / (data.resolution - 1));
            var b = Evaluate(data, (float)high / (data.resolution - 1));
            // Hardware bilinear weights need only 8 fractional bits. Account for one
            // weight quantum using the LOCAL texel slope, not a broad HDR-value tolerance.
            // https://microsoft.github.io/DirectX-Specs/d3d/archive/D3D11_3_FunctionalSpec.htm
            for (int channel = 0; channel < 4; channel++)
                Near(actual[channel], expected[channel], 3e-4f + Mathf.Abs(b[channel] - a[channel]) / 256f,
                    message + " channel " + channel);
        }
        static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        static void Near(float actual, float expected, float tolerance, string message)
        {
            Require(!float.IsNaN(actual) && !float.IsInfinity(actual) && Mathf.Abs(actual - expected) <= tolerance, message + $": expected {expected}, got {actual}");
        }
        static void Near(Color actual, Color expected, float tolerance, string message)
        {
            for (int channel = 0; channel < 4; ++channel) Near(actual[channel], expected[channel], tolerance, message + " channel " + channel);
        }

        static void Gpu(Fixture f, string folder)
        {
            Require(SystemInfo.SupportsTextureFormat(TextureFormat.RGBAFloat) && SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBFloat), "Float texture/readback unsupported; GPU verification was not performed.");
            GpuNode(f, folder, f.gradient, f.gradientInput, f.gradientSample, false);
            GpuNode(f, folder, f.curve, f.curveInput, f.curveSample, true);
        }

        static void GpuNode(Fixture f, string folder, WhimLutShaderProperty property, PropertyNode input, WhimLutSampleNode node, bool scalar)
        {
            var soft = new SoftSignNode(); var inverse = new InverseSoftSignNode();
            f.graph.AddNode(soft); f.graph.AddNode(inverse);
            f.graph.Connect(node.GetSlotReference(2), soft.GetSlotReference(0)); f.graph.Connect(soft.GetSlotReference(1), inverse.GetSlotReference(0)); f.graph.ValidateGraph();
            using var builder = new ShaderStringBuilder();
            input.GenerateNodeCode(builder, GenerationMode.ForReals); f.timeInput.GenerateNodeCode(builder, GenerationMode.ForReals);
            node.GenerateNodeCode(builder, GenerationMode.ForReals); soft.GenerateNodeCode(builder, GenerationMode.ForReals); inverse.GenerateNodeCode(builder, GenerationMode.ForReals);
            string output = inverse.GetVariableNameForSlot(1);
            string sampleOutput = node.GetVariableNameForSlot(2);
            string expression = scalar ? $"float4({output}, {output}, {output}, 1)" : output;
            string direct = scalar ? $"float4({sampleOutput}, {sampleOutput}, {sampleOutput}, 1)" : sampleOutput;
            string softOutput = soft.GetVariableNameForSlot(1);
            string softExpression = scalar ? $"float4({softOutput}, {softOutput}, {softOutput}, 1)" : softOutput;
            string body = builder.ToCodeBlock().Replace("$precision", "float");
            string textureName = property.referenceName;
            string shaderText = "Shader \"Hidden/WhimShader/LutValidation" + (scalar ? "Curve" : "Gradient") + "\" { Properties { " + textureName + " (\"LUT\", 2D) = \"white\" {} _TestT (\"T\", Float) = 0 _Stage (\"Stage\", Float) = 0 _Roundtrip (\"Roundtrip\", Float) = 0 } SubShader { Pass { ZTest Always ZWrite Off Cull Off HLSLPROGRAM\n" +
                "#pragma target 4.5\n#pragma vertex Vert\n#pragma fragment Frag\n#include \"Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl\"\n#include \"Packages/com.unity.render-pipelines.core/ShaderLibrary/Texture.hlsl\"\n" +
                $"TEXTURE2D({textureName}); SAMPLER(sampler{textureName}); float4 {textureName}_TexelSize; float _TestT, _Stage, _Roundtrip;\nfloat4 EvaluateNode() {{\n{body}\nreturn _Roundtrip > 1.5 ? {softExpression} : (_Roundtrip > 0.5 ? {expression} : {direct});\n}}\n" +
                "struct Varyings { float4 positionCS : SV_POSITION; float4 lutValue : TEXCOORD0; };\nVaryings Vert(uint id : SV_VertexID) { Varyings o; float2 uv = float2((id << 1) & 2, id & 2); o.positionCS = float4(uv * 2 - 1, 0, 1); o.lutValue = EvaluateNode(); return o; }\nfloat4 Frag(Varyings i) : SV_Target { return _Stage > 0.5 ? i.lutValue : EvaluateNode(); }\nENDHLSL } } }";
            string path = folder + (scalar ? "/CurveGPU.shader" : "/GradientGPU.shader"); File.WriteAllText(path, shaderText); AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(path); Compile(shader);
            var material = new Material(shader); var data = property.Data; var texture = WhimLutTexture.Create(data);
            try
            {
                WhimLutTexture.Bind(material, textureName, texture);
                for (int stage = 0; stage < 2; ++stage)
                    for (int roundtrip = 0; roundtrip < 3; ++roundtrip)
                        foreach (float t in new[] { -1f, 0f, .125f, .37f, .5f, 1f, 2f })
                        {
                            material.SetFloat("_TestT", t); material.SetFloat("_Stage", stage); material.SetFloat("_Roundtrip", roundtrip);
                            var expected = ExpectedLut(data, t);
                            if (roundtrip == 2)
                                for (int channel = 0; channel < (scalar ? 3 : 4); ++channel)
                                    expected[channel] /= 1 + Mathf.Abs(expected[channel]);
                            NearGpu(ReadPixel(material), expected, data, t, $"GPU {property.LutKind} stage={stage} mode={roundtrip} T={t}");
                        }
                if (scalar)
                {
                    data.curve = AnimationCurve.Linear(0, -4, 1, 5); WhimLutTexture.Update(texture, data);
                    material.SetFloat("_TestT", .37f); material.SetFloat("_Roundtrip", 0);
                    NearGpu(ReadPixel(material), ExpectedLut(data, .37f), data, .37f, "GPU runtime texture update without replacing shader/material");
                }
            }
            finally { Object.DestroyImmediate(material); WhimLutTexture.DestroyOwned(texture); }
        }

        static Color ReadPixel(Material material)
        {
            var target = RenderTexture.GetTemporary(1, 1, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            var readback = new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true);
            var previous = RenderTexture.active;
            var commands = new CommandBuffer { name = "WhimShader LUT Validation" };
            try
            {
                commands.SetRenderTarget(target); commands.SetViewport(new Rect(0, 0, 1, 1)); commands.ClearRenderTarget(false, true, new Color(123, 123, 123, 123));
                commands.DrawProcedural(Matrix4x4.identity, material, 0, MeshTopology.Triangles, 3);
                Graphics.ExecuteCommandBuffer(commands);
                RenderTexture.active = target; readback.ReadPixels(new Rect(0, 0, 1, 1), 0, 0); readback.Apply(false, false);
                return readback.GetPixel(0, 0);
            }
            finally { RenderTexture.active = previous; commands.Release(); Object.DestroyImmediate(readback); RenderTexture.ReleaseTemporary(target); }
        }
    }
}
