using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.ShaderGraph;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DCFApixels.WhimShader
{
    public static partial class WhimShaderApi
    {
        /// <summary>On-demand PNG, including collapsed/hidden nodes. Does not change saved preview settings.</summary>
        public static string Preview(string path, string node, string output = null, int size = 256,
            string mode = null, string channel = null, float time = 0)
            => Safe(() => PreviewCore(path, node, output, size, mode, channel, time));

        static JObject PreviewCore(string path, string node, string output, int size, string mode, string channel, float time)
        {
            Require(size >= 32 && size <= 1024, "Preview size must be 32..1024 pixels.");
            Require(float.IsFinite(time), "Preview time must be finite.");
            path = AssetPath(path);
            using var session = new Session(path, false);
            string revision = Revision(session.graph);
            var original = Resolve(session.graph, node);
            Require(original is not BlockNode, "Choose a node with a numeric output, not a vertex/fragment block.");
            var settings = WhimShaderNodePresentationMetadata.Read(original);
            var slot = output == null
                ? original.FindSlot<MaterialSlot>(settings.outputSlotId)
                : Port(original, output, original is not SubGraphOutputNode);
            Require(WhimShaderPreviewSource.Supports(slot), "Choose a numeric output (Float1..4 or Boolean). Textures and matrices must be sampled/converted first.");
            if (mode != null) settings.mode = Normalize(mode) switch {
                "image" or "flat" => WhimShaderPreviewMode.Flat, "height" => WhimShaderPreviewMode.Height,
                "3d" or "threed" => WhimShaderPreviewMode.ThreeD,
                _ => throw new ApiException("invalid_request", "mode must be Image, Height or 3D.") };
            if (channel != null)
            {
                settings.channelMask = channel.ToUpperInvariant() switch {
                    "RGBA" => 15, "RGB" => 7, "R" => 1, "G" => 2, "B" => 4, "A" => 8,
                    _ => throw new ApiException("invalid_request", "channel must be RGBA, RGB, R, G, B or A.") };
            }

            // Generation/LUT preparation may touch native preview caches. Keep the live model untouched.
            var graph = Clone(session.graph);
            Shader shader = null; Material material = null;
            RenderTexture raw = null, display = null;
            Texture2D pixels = null;
            var previousTarget = RenderTexture.active;
            bool previousAsync = ShaderUtil.allowAsyncCompilation;
            using var renderer = new WhimPreviewRenderer();
            try
            {
                var root = graph.GetNodeFromId(original.objectId);
                var generated = new Generator(graph, root, GenerationMode.Preview, "Hidden/WhimShader/AgentPreview/" + root.objectId, humanReadable: true);
                string source = WhimShaderPreviewSource.SelectOutput(generated.generatedShader, root, slot.id);
                ShaderUtil.allowAsyncCompilation = false;
                shader = ShaderUtil.CreateShaderAsset(source, false); shader.hideFlags = HideFlags.HideAndDontSave;
                material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                Require(material.passCount > 0, "Preview shader has no supported passes.", "preview_compile_failed");
                ShaderUtil.CompilePass(material, 0, true);
                Require(!ShaderUtil.ShaderHasError(shader), string.Join("\n", ShaderUtil.GetShaderMessages(shader).Select(m => m.message)), "preview_compile_failed");
                var properties = new WhimShaderPreviewProperties();
                properties.Rebuild(graph, generated.temporaryBlocks); properties.SetTime(time); properties.ApplyMaterialOnlyProperties(material);
                var format = SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBHalf) ? RenderTextureFormat.ARGBHalf : RenderTextureFormat.ARGBFloat;
                raw = RenderTexture.GetTemporary(size, size, 0, format, RenderTextureReadWrite.Linear);
                display = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
                raw.wrapMode = display.wrapMode = TextureWrapMode.Clamp;
                raw.filterMode = display.filterMode = FilterMode.Bilinear;
                renderer.RenderMaterial(material, properties.Block, settings.mode, raw, false);
                renderer.Render(raw, display, settings.mode, settings.EffectiveMask, settings.heightChannel,
                    settings.heightScale, settings.heightTexture, settings.highlightOutOfRange, encodeGamma: false,
                    canvasSize: new Vector2Int(size, size));
                RenderTexture.active = display;
                pixels = new Texture2D(size, size, TextureFormat.RGBA32, false, true) { hideFlags = HideFlags.HideAndDontSave };
                pixels.ReadPixels(new Rect(0, 0, size, size), 0, 0); pixels.Apply();
                string directory = Path.GetFullPath("Temp/WhimShader/Previews"); Directory.CreateDirectory(directory);
                string file = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".png");
                File.WriteAllBytes(file, pixels.EncodeToPNG());
                return new JObject { ["path"] = path, ["revision"] = revision, ["node"] = node, ["nodeId"] = original.objectId,
                    ["output"] = slot.RawDisplayName(), ["outputId"] = slot.id, ["imagePath"] = file.Replace('\\', '/'),
                    ["width"] = size, ["height"] = size, ["mode"] = settings.mode == WhimShaderPreviewMode.Flat ? "Image" : settings.mode == WhimShaderPreviewMode.Height ? "Height" : "3D",
                    ["channelMask"] = settings.EffectiveMask, ["time"] = time };
            }
            finally
            {
                ShaderUtil.allowAsyncCompilation = previousAsync; RenderTexture.active = previousTarget;
                renderer.Dispose();
                if (pixels != null) Object.DestroyImmediate(pixels);
                if (display != null) RenderTexture.ReleaseTemporary(display);
                if (raw != null) RenderTexture.ReleaseTemporary(raw);
                if (material != null) Object.DestroyImmediate(material);
                if (shader != null) Object.DestroyImmediate(shader);
                Object.DestroyImmediate(graph.owner);
            }
        }
    }
}
