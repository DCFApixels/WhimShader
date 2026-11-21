using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Rendering.Universal.ShaderGraph;
using UnityEditor.ShaderGraph;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

namespace DCFApixels.WhimShader
{
    /// <summary>
    /// Explicit, synchronous GPU smoke test. No assets are saved, no user scene or
    /// pipeline settings are edited. Invoke on the Editor main thread outside import.
    /// </summary>
    public static class WhimShaderURPValidation
    {
        static bool running;

        public static object Run()
        {
            if (running) throw new InvalidOperationException("WhimShader GPU validation is already running.");
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Run GPU validation in Edit Mode after compilation and import finish.");
            if (GraphicsSettings.currentRenderPipeline is not UniversalRenderPipelineAsset pipeline)
                throw new InvalidOperationException("An active URP pipeline is required; validation does not change pipeline settings.");
            if (pipeline.mainLightRenderingMode != LightRenderingMode.PerPixel)
                throw new InvalidOperationException("This smoke test requires URP Main Light rendering enabled (Per Pixel).");
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null || !SystemInfo.supportsRenderTextures ||
                !SystemInfo.SupportsTextureFormat(TextureFormat.RGBAFloat))
                throw new InvalidOperationException("A graphics device with render textures and RGBAFloat readback is required.");

            running = true;
            bool previousAsync = ShaderUtil.allowAsyncCompilation;
            var fixtures = new List<Fixture>();
            var checks = new List<string>();
            var samples = new Dictionary<string, Pixels>();
            var compiledPasses = new List<string>();
            try
            {
                ShaderUtil.allowAsyncCompilation = false;
                var pbr = Add("PBR", graph =>
                {
                    // Remove indirect lighting from the light-response comparison.
                    graph.GetNodes<UnityPBRNode>().Single()
                        .FindInputSlot<Vector1MaterialSlot>(UnityPBRNode.OcclusionSlot).value = 0;
                });
                var scene = Add("SceneLighting", graph =>
                {
                    var node = new SceneLightingNode();
                    graph.AddNode(node);
                    graph.Connect(node.GetSlotReference(2), Block(graph, WhimSurfaceBlocks.Color).GetSlotReference(0));
                });
                var clipBoth = Add("ClipBoth", graph => ConfigureClip(graph, RenderFace.Both));
                var clipFront = Add("ClipFront", graph => ConfigureClip(graph, RenderFace.Front));
                checks.Add("Factory layout and exactly three initial vertex / three fragment blocks");
                checks.Add("Required pass tags present; no GBuffer or Universal2D pass");
                checks.Add("All material passes compiled synchronously for the current keyword state");

                using (var rig = new PreviewRig())
                {
                    samples["PBR.dark"] = rig.Render(pbr.material, false, false);
                    samples["PBR.lit"] = rig.Render(pbr.material, true, false);
                    RespondsToLight(samples["PBR.dark"], samples["PBR.lit"], "Unity PBR");
                    checks.Add("Unity PBR responds to a real preview-scene directional light");

                    samples["Scene.dark"] = rig.Render(scene.material, false, false);
                    samples["Scene.lit"] = rig.Render(scene.material, true, false);
                    RespondsToLight(samples["Scene.dark"], samples["Scene.lit"], "Scene Lighting");
                    checks.Add("Scene Lighting responds to the scene light, not its fallback preview constant");

                    samples["Clip.front"] = rig.Render(clipBoth.material, true, false);
                    samples["Clip.back"] = rig.Render(clipBoth.material, true, true);
                    AssertHalfClipped(samples["Clip.front"], "Two-sided front");
                    AssertHalfClipped(samples["Clip.back"], "Two-sided back");
                    checks.Add("UV alpha clipping removes half the quad on both sides");

                    samples["Cull.front"] = rig.Render(clipFront.material, true, false);
                    samples["Cull.back"] = rig.Render(clipFront.material, true, true);
                    AssertHalfClipped(samples["Cull.front"], "Front-only front");
                    if (samples["Cull.back"].maximum > .02f)
                        throw new InvalidOperationException("Front-only material rendered its back face: " + samples["Cull.back"]);
                    checks.Add("Backface culling control distinguishes Front from Both");
                }
                foreach (var fixture in fixtures) AssertShader(fixture.shader, fixture.name + " after rendering");
                return new
                {
                    passed = true, checks = checks.ToArray(), compiledPasses = compiledPasses.ToArray(), samples,
                    pipeline = pipeline.name, graphicsAPI = SystemInfo.graphicsDeviceType.ToString(),
                    scope = "Current URP renderer/API; all pass entrypoints in current keyword state. " +
                        "Pixel tests cover main-light PBR/Scene Lighting, UV alpha clip and culling. " +
                        "Not an exhaustive shadow/GI/Forward+ or auxiliary-pass pixel validation."
                };

                Fixture Add(string name, Action<GraphData> configure)
                {
                    var fixture = new Fixture(name, configure);
                    fixtures.Add(fixture);
                    for (int pass = 0; pass < fixture.material.passCount; pass++)
                    {
                        ShaderUtil.CompilePass(fixture.material, pass, true);
                        AssertShader(fixture.shader, name + " / " + fixture.material.GetPassName(pass));
                        if (!ShaderUtil.IsPassCompiled(fixture.material, pass))
                            throw new InvalidOperationException(name + " pass remained uncompiled: " + fixture.material.GetPassName(pass));
                        compiledPasses.Add(name + ": " + fixture.material.GetPassName(pass));
                    }
                    return fixture;
                }
            }
            finally
            {
                try { foreach (var fixture in fixtures) fixture.Dispose(); }
                finally { ShaderUtil.allowAsyncCompilation = previousAsync; running = false; }
            }
        }

        static BlockNode Block(GraphData graph, BlockFieldDescriptor descriptor)
            => graph.GetNodes<BlockNode>().Single(n => n.descriptor == descriptor);

        static void ConfigureClip(GraphData graph, RenderFace face)
        {
            var target = graph.activeTargets.OfType<UniversalTarget>().Single();
            target.alphaClip = true;
            target.renderFace = face;
            var white = new Vector1Node();
            white.FindInputSlot<Vector1MaterialSlot>(Vector1Node.InputSlotXId).value = 1;
            var uv = new UVNode();
            graph.AddNode(white); graph.AddNode(uv);
            graph.Connect(white.GetSlotReference(Vector1Node.OutputSlotId), Block(graph, WhimSurfaceBlocks.Color).GetSlotReference(0));
            // Native vector-to-scalar conversion takes UV.x.
            graph.Connect(uv.GetSlotReference(UVNode.OutputSlotId), Block(graph, BlockFields.SurfaceDescription.Alpha).GetSlotReference(0));
            Block(graph, BlockFields.SurfaceDescription.AlphaClipThreshold).FindInputSlot<Vector1MaterialSlot>(0).value = .5f;
        }

        static void RespondsToLight(Pixels dark, Pixels lit, string label)
        {
            if (lit.center < dark.center + .04f)
                throw new InvalidOperationException(label + " did not respond to the main light. Dark=" + dark + "; lit=" + lit);
        }

        static void AssertHalfClipped(Pixels pixels, string label)
        {
            float dark = Mathf.Min(pixels.left, pixels.right), bright = Mathf.Max(pixels.left, pixels.right);
            if (dark > .02f || bright < .1f)
                throw new InvalidOperationException(label + " did not show one clipped and one visible half: " + pixels);
        }

        static void AssertShader(Shader shader, string label)
        {
            var errors = ShaderUtil.GetShaderMessages(shader)
                .Where(m => m.severity == UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error)
                .Select(m => m.message + " (" + m.file + ":" + m.line + ")").ToArray();
            if (errors.Length != 0 || ShaderUtil.ShaderHasError(shader))
                throw new InvalidOperationException(label + ": " + string.Join("\n", errors));
        }

        sealed class Fixture : IDisposable
        {
            internal readonly string name;
            internal readonly GraphData graph;
            internal Shader shader;
            internal Material material;
            internal Fixture(string name, Action<GraphData> configure)
            {
                this.name = name;
                graph = WhimShaderURP.CreateGraph();
                try
                {
                    if (graph.vertexContext.blocks.Count != 3 || graph.fragmentContext.blocks.Count != 3 ||
                        graph.vertexContext.position != Vector2.zero || graph.fragmentContext.position != new Vector2(0, 300) ||
                        graph.GetNodes<UnityPBRNode>().Single().drawState.position.position != new Vector2(-420, 100))
                        throw new InvalidOperationException("Unexpected initial graph layout/blocks.");
                    configure(graph);
                    graph.UpdateActiveBlocks(graph.GetActiveBlocksForAllActiveTargets());
                    graph.ValidateGraph();
                    var generator = new Generator(graph, graph.outputNode, GenerationMode.ForReals,
                        "Hidden/WhimShader/GpuSmoke/" + name);
                    string source = generator.generatedShader;
                    if (string.IsNullOrEmpty(source) || graph.messageManager.HasSeverity(_ => true,
                        UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error))
                        throw new InvalidOperationException(name + " has graph/generation errors.");
                    foreach (string mode in new[] { "UniversalForwardOnly", "ShadowCaster", "DepthOnly", "DepthNormalsOnly", "Meta", "MotionVectors" })
                        if (!source.Contains("\"LightMode\" = \"" + mode + "\""))
                            throw new InvalidOperationException(name + " is missing pass " + mode);
                    if (source.Contains("\"UniversalGBuffer\"") || source.Contains("\"Universal2D\""))
                        throw new InvalidOperationException("Unexpected deferred/2D pass in " + name);
                    shader = ShaderUtil.CreateShaderAsset(source, false);
                    if (shader == null) throw new InvalidOperationException(name + " shader creation returned null.");
                    shader.hideFlags = HideFlags.HideAndDontSave;
                    AssertShader(shader, name);
                    material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                    graph.activeTargets.OfType<UniversalTarget>().Single().ProcessPreviewMaterial(material);
                    if (material.passCount == 0) throw new InvalidOperationException(name + " has no material passes.");
                }
                catch { Dispose(); throw; }
            }
            public void Dispose()
            {
                if (material != null) Object.DestroyImmediate(material);
                if (shader != null) Object.DestroyImmediate(shader);
                if (graph?.owner != null) Object.DestroyImmediate(graph.owner);
            }
        }

        [Serializable]
        public struct Pixels
        {
            public float center, left, right, maximum;
            public override string ToString() => $"center={center:F4}, left={left:F4}, right={right:F4}, max={maximum:F4}";
        }

        sealed class PreviewRig : IDisposable
        {
            readonly PreviewRenderUtility preview;
            readonly Mesh mesh;
            readonly GameObject quad;
            readonly MeshRenderer renderer;
            internal PreviewRig()
            {
                preview = new PreviewRenderUtility();
                try
                {
                    preview.ambientColor = Color.black;
                    var camera = preview.camera;
                    camera.clearFlags = CameraClearFlags.SolidColor;
                    camera.backgroundColor = Color.black;
                    camera.orthographic = true; camera.orthographicSize = 1.25f;
                    camera.nearClipPlane = .1f; camera.farClipPlane = 10;
                    camera.allowHDR = true; camera.allowMSAA = false;
                    camera.transform.SetPositionAndRotation(new Vector3(0, 0, -3), Quaternion.identity);
                    var cameraData = camera.GetUniversalAdditionalCameraData();
                    cameraData.renderPostProcessing = false;
                    cameraData.renderShadows = false;
                    cameraData.antialiasing = AntialiasingMode.None;

                    mesh = new Mesh { name = "WhimShader smoke quad", hideFlags = HideFlags.HideAndDontSave };
                    mesh.vertices = new[] { new Vector3(-1, -1, 0), new Vector3(1, -1, 0), new Vector3(1, 1, 0), new Vector3(-1, 1, 0) };
                    mesh.normals = Enumerable.Repeat(Vector3.back, 4).ToArray();
                    mesh.tangents = Enumerable.Repeat(new Vector4(1, 0, 0, -1), 4).ToArray();
                    mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up };
                    mesh.uv2 = mesh.uv; mesh.uv3 = mesh.uv;
                    mesh.triangles = new[] { 0, 2, 1, 0, 3, 2 };
                    mesh.RecalculateBounds();
                    quad = new GameObject("WhimShader smoke quad") { hideFlags = HideFlags.HideAndDontSave };
                    preview.AddSingleGO(quad);
                    quad.AddComponent<MeshFilter>().sharedMesh = mesh;
                    renderer = quad.AddComponent<MeshRenderer>();
                    renderer.shadowCastingMode = ShadowCastingMode.Off;
                    renderer.receiveShadows = false;
                    renderer.lightProbeUsage = LightProbeUsage.Off;
                    renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
                }
                catch { Dispose(); throw; }
            }

            internal Pixels Render(Material material, bool lit, bool back)
            {
                renderer.sharedMaterial = material;
                quad.transform.rotation = back ? Quaternion.Euler(0, 180, 0) : Quaternion.identity;
                var previousTarget = RenderTexture.active;
                Texture2D readback = null;
                bool begun = false;
                try
                {
                    preview.BeginPreview(new Rect(0, 0, 96, 96), GUIStyle.none);
                    begun = true;
                    var target = preview.camera.targetTexture;
                    preview.camera.pixelRect = new Rect(0, 0, target.width, target.height);
                    preview.camera.aspect = 1;
                    var lights = preview.lights;
                    lights[0].type = LightType.Directional;
                    lights[0].transform.rotation = Quaternion.identity;
                    lights[0].color = Color.white;
                    lights[0].intensity = lit ? 1 : 0;
                    lights[0].shadows = LightShadows.None;
                    lights[1].intensity = 0;
                    preview.Render(allowScriptableRenderPipeline: true, updatefov: false);
                    RenderTexture.active = target;
                    readback = new Texture2D(target.width, target.height, TextureFormat.RGBAFloat, false, true);
                    readback.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
                    readback.Apply(false, false);
                    return Measure(readback.GetPixels(), target.width, target.height);
                }
                finally
                {
                    try { if (begun) preview.EndPreview(); }
                    finally
                    {
                        RenderTexture.active = previousTarget;
                        if (readback != null) Object.DestroyImmediate(readback);
                    }
                }
            }

            static Pixels Measure(Color[] pixels, int width, int height)
            {
                var result = new Pixels();
                foreach (var c in pixels)
                {
                    if (!Finite(c.r) || !Finite(c.g) || !Finite(c.b) || !Finite(c.a))
                        throw new InvalidOperationException("Non-finite pixels in WhimShader GPU smoke output.");
                    if (c.r > .8f && c.b > .8f && c.g < .1f)
                        throw new InvalidOperationException("Error-magenta pixels in WhimShader GPU smoke output.");
                    result.maximum = Mathf.Max(result.maximum, Luminance(c));
                }
                result.center = Region(.4f, .6f);
                result.left = Region(.22f, .35f);
                result.right = Region(.65f, .78f);
                return result;

                float Region(float minX, float maxX)
                {
                    float sum = 0; int count = 0;
                    for (int y = height * 2 / 5; y < height * 3 / 5; y++)
                        for (int x = (int)(width * minX); x < (int)(width * maxX); x++)
                        { sum += Luminance(pixels[y * width + x]); count++; }
                    return sum / Mathf.Max(count, 1);
                }
            }
            static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
            static float Luminance(Color c) => c.r * .2126f + c.g * .7152f + c.b * .0722f;
            public void Dispose()
            {
                try { preview?.Cleanup(); }
                finally
                {
                    if (quad != null) Object.DestroyImmediate(quad);
                    if (mesh != null) Object.DestroyImmediate(mesh);
                }
            }
        }
    }
}
