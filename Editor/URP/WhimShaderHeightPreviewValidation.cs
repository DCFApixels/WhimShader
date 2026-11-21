using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace DCFApixels.WhimShader
{
    /// <summary>
    /// Explicit GPU comparison against the installed ORIGINAL WhimTex compute/display assets.
    /// Run on the Editor main thread after import. No scene, saved asset or setting is changed.
    /// Reference dispatch/display bindings follow WhimTex HeightPreviewRenderer.cs (MIT;
    /// copyright (c) 2026 Mikhail; see Shaders/WhimTex-NOTICE.md).
    /// </summary>
    public static class WhimShaderHeightPreviewValidation
    {
        const string OriginalRoot = "Packages/com.dcfapixels.whimtex/src/Shaders/";
        const string CopiedRoot = "Packages/com.dcfapixels.whimshader/Shaders/";
        static bool running;

        [Serializable]
        public sealed class Difference
        {
            public float maximumAbsoluteError;
            public int mismatchedPixels, nonFinitePixels;
            public string firstMismatch;
        }

        [Serializable]
        public sealed class CaseResult
        {
            public string heightChannel, scale;
            public int size, channelMask;
            public Vector2Int canvasSize;
            public bool textured, highlightOutOfRange, encodeGamma, passed;
            public Difference geometry, copiedDisplay, integratedRenderer;
        }

        [Serializable]
        public sealed class Report
        {
            public bool passed;
            public string graphicsAPI, colorSpace, originalCompute, originalDisplay;
            public float tolerance, maximumAbsoluteError;
            public double elapsedSeconds;
            public int comparisons, failedCases, testedMaskBits, testedHeightChannels;
            public int referenceGeometryPixels, referenceHighlightPixels;
            public List<CaseResult> cases = new();
        }

        /// <summary>
        /// 232 cases: all five height channels x {-1,0,.25,1} x Texture x Highlight x
        /// encodeGamma; every mask 0..15 x Texture x encodeGamma; scale clamp/fallback cases.
        /// Alternates two output sizes/canvas aspects through ONE integrated renderer.
        /// Returns serializable differences (including failures), suitable for JsonUtility.ToJson.
        /// Unsupported hardware, source assets or shader errors throw instead of producing a PASS.
        /// </summary>
        public static Report Run(int size = 48, float tolerance = .00001f)
        {
            if (running) throw new InvalidOperationException("Height preview validation is already running.");
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Run Height preview validation in Edit Mode after import completes.");
            if (size < 16 || size > 128) throw new ArgumentOutOfRangeException(nameof(size));
            if (!float.IsFinite(tolerance) || tolerance <= 0) throw new ArgumentOutOfRangeException(nameof(tolerance));
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null || !HeightPreviewRenderer.Supported ||
                !SystemInfo.SupportsTextureFormat(TextureFormat.RGBAFloat))
                throw new NotSupportedException("Height comparison requires compute, ARGBFloat render targets and RGBAFloat readback.");

            running = true;
            var previousTarget = RenderTexture.active;
            bool previousSRGB = GL.sRGBWrite, previousAsync = ShaderUtil.allowAsyncCompilation;
            var timer = Stopwatch.StartNew();
            var contexts = new List<ComparisonContext>();
            var materials = new List<Material>();
            Texture2D fixture = null;
            RenderTexture source = null;
            WhimPreviewRenderer renderer = null;
            var report = new Report
            {
                graphicsAPI = SystemInfo.graphicsDeviceType.ToString(),
                colorSpace = QualitySettings.activeColorSpace.ToString(),
                originalCompute = OriginalRoot + "HeightPreview.compute",
                originalDisplay = OriginalRoot + "HeightPreviewDisplay.shader",
                tolerance = tolerance,
            };
            try
            {
                ShaderUtil.allowAsyncCompilation = false;
                var originalCompute = RequireAsset<ComputeShader>(report.originalCompute);
                int kernel = originalCompute.FindKernel("Horizon");
                if (!originalCompute.IsSupported(kernel)) throw new NotSupportedException("Original WhimTex Horizon kernel is unsupported.");
                var originalDisplay = Compile(RequireAsset<Shader>(report.originalDisplay));
                Compile(RequireAsset<Shader>(CopiedRoot + "WhimHeightPreviewDisplay.shader"));
                Compile(RequireAsset<Shader>(CopiedRoot + "WhimPreview.shader"));

                // Signed/HDR RGB, varying alpha, slopes and local extrema exercise horizons,
                // channel selection, neutral/textured lighting and magenta range highlighting.
                const int width = 64, height = 40;
                fixture = new Texture2D(width, height, TextureFormat.RGBAFloat, false, true)
                { name = "Whim Height comparison source", hideFlags = HideFlags.HideAndDontSave,
                    filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
                var pixels = new Color[width * height];
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                    {
                        float u = (x + .5f) / width, v = (y + .5f) / height;
                        pixels[y * width + x] = new Color(-.3f + 1.6f * u + .2f * Mathf.Sin(v * 17),
                            -.2f + 1.4f * v, .5f + .75f * Mathf.Sin(u * 15) * Mathf.Cos(v * 11), .1f + .85f * u * v);
                    }
                fixture.SetPixels(pixels); fixture.Apply(false, false);
                source = MakeTarget(width, height, false, "Whim Height comparison input");
                GL.sRGBWrite = false;
                Graphics.Blit(fixture, source);

                contexts.Add(new ComparisonContext(size));
                contexts.Add(new ComparisonContext(size * 2));
                renderer = new WhimPreviewRenderer();
                foreach (HeightPreviewRenderer.Channel channel in Enum.GetValues(typeof(HeightPreviewRenderer.Channel)))
                    foreach (float scale in new[] { -1f, 0f, .25f, 1f })
                        for (int texture = 0; texture < 2; texture++)
                            for (int highlight = 0; highlight < 2; highlight++)
                                for (int gamma = 0; gamma < 2; gamma++)
                                    Check(channel, scale, 15, texture != 0, highlight != 0, gamma != 0);
                for (int mask = 0; mask < 16; mask++)
                    for (int texture = 0; texture < 2; texture++)
                        for (int gamma = 0; gamma < 2; gamma++)
                            Check(HeightPreviewRenderer.Channel.Luminance, .25f, mask, texture != 0, true, gamma != 0);
                foreach (float scale in new[] { -8f, 8f, float.NaN, float.PositiveInfinity })
                    for (int gamma = 0; gamma < 2; gamma++)
                        Check(HeightPreviewRenderer.Channel.R, scale, 15, true, true, gamma != 0);

                if (report.referenceGeometryPixels == 0 || report.referenceHighlightPixels == 0)
                    throw new InvalidOperationException("Comparison fixture failed to exercise visible geometry and range highlighting.");
                report.passed = report.failedCases == 0 && report.testedMaskBits == 0xffff && report.testedHeightChannels == 0x1f;
                report.elapsedSeconds = timer.Elapsed.TotalSeconds;
                return report;

                Material Compile(Shader shader)
                {
                    var material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                    materials.Add(material);
                    for (int pass = 0; pass < material.passCount; pass++) ShaderUtil.CompilePass(material, pass, true);
                    if (ShaderUtil.ShaderHasError(shader))
                        throw new InvalidOperationException(shader.name + ": " + string.Join("\n", ShaderUtil.GetShaderMessages(shader).Select(m => m.message)));
                    if (material.passCount == 0) throw new InvalidOperationException(shader.name + " has no passes.");
                    return material;
                }

                void Check(HeightPreviewRenderer.Channel channel, float scale, int mask, bool textured, bool highlight, bool gamma)
                {
                    var context = contexts[report.cases.Count % contexts.Count];
                    // Logical canvas aspect differs from texture resolution, just as document
                    // dimensions can differ from a downsampled WhimTex source preview.
                    var canvas = context.size == size ? new Vector2Int(640, 320) : new Vector2Int(320, 640);
                    context.RenderOriginal(originalCompute, kernel, originalDisplay, source, canvas, channel, scale, mask, textured, highlight, gamma);
                    context.copied.Render(source, canvas, context.size, channel, scale);
                    var copiedOutput = context.copied.Display(source, mask, textured, gamma, highlight);
                    renderer.Render(source, context.actual, WhimShaderPreviewMode.Height, mask, channel,
                        scale, textured, highlight, gamma, canvas);

                    var result = new CaseResult
                    {
                        size = context.size, canvasSize = canvas, heightChannel = channel.ToString(),
                        scale = scale.ToString("R", CultureInfo.InvariantCulture), channelMask = mask,
                        textured = textured, highlightOutOfRange = highlight, encodeGamma = gamma,
                        geometry = context.Compare(context.geometry, context.copied.Geometry, tolerance),
                        copiedDisplay = context.Compare(context.output, copiedOutput, tolerance),
                        integratedRenderer = context.Compare(context.output, context.actual, tolerance),
                    };
                    result.passed = Good(result.geometry) && Good(result.copiedDisplay) && Good(result.integratedRenderer);
                    report.cases.Add(result);
                    report.comparisons += 3;
                    if (!result.passed) report.failedCases++;
                    report.testedMaskBits |= 1 << mask;
                    report.testedHeightChannels |= 1 << (int)channel;
                    report.maximumAbsoluteError = Mathf.Max(report.maximumAbsoluteError, result.geometry.maximumAbsoluteError,
                        result.copiedDisplay.maximumAbsoluteError, result.integratedRenderer.maximumAbsoluteError);
                    context.CountReferencePixels(out int geometryPixels, out int highlightPixels);
                    report.referenceGeometryPixels += geometryPixels;
                    if (highlight) report.referenceHighlightPixels += highlightPixels;
                }
            }
            finally
            {
                RenderTexture.active = previousTarget;
                GL.sRGBWrite = previousSRGB;
                ShaderUtil.allowAsyncCompilation = previousAsync;
                try
                {
                    renderer?.Dispose();
                    foreach (var context in contexts) context.Dispose();
                    foreach (var material in materials) Object.DestroyImmediate(material);
                    if (source != null) Object.DestroyImmediate(source);
                    if (fixture != null) Object.DestroyImmediate(fixture);
                }
                finally { running = false; }
            }
        }

        static bool Good(Difference difference) => difference.mismatchedPixels == 0 && difference.nonFinitePixels == 0;

        static T RequireAsset<T>(string path) where T : Object
        {
            var asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset == null) throw new InvalidOperationException("Required comparison asset is not imported: " + path);
            return asset;
        }

        static RenderTexture MakeTarget(int width, int height, bool randomWrite, string name)
        {
            var texture = new RenderTexture(new RenderTextureDescriptor(width, height, RenderTextureFormat.ARGBFloat, 0)
            { enableRandomWrite = randomWrite, sRGB = false, msaaSamples = 1 })
            { name = name, hideFlags = HideFlags.HideAndDontSave,
                filterMode = randomWrite ? FilterMode.Point : FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            if (!texture.Create()) { Object.DestroyImmediate(texture); throw new InvalidOperationException("Cannot create " + name); }
            return texture;
        }

        sealed class ComparisonContext : IDisposable
        {
            internal readonly int size;
            internal readonly HeightPreviewRenderer copied = new();
            internal RenderTexture geometry, output, actual;
            Texture2D expectedReadback, actualReadback;

            internal ComparisonContext(int size)
            {
                this.size = size;
                try
                {
                    geometry = MakeTarget(size, size, true, "Original WhimTex Horizon geometry");
                    output = MakeTarget(size, size, false, "Original WhimTex Height output");
                    actual = MakeTarget(size, size, false, "WhimShader integrated Height output");
                    expectedReadback = new Texture2D(size, size, TextureFormat.RGBAFloat, false, true) { hideFlags = HideFlags.HideAndDontSave };
                    actualReadback = new Texture2D(size, size, TextureFormat.RGBAFloat, false, true) { hideFlags = HideFlags.HideAndDontSave };
                }
                catch { Dispose(); throw; }
            }

            internal void RenderOriginal(ComputeShader compute, int kernel, Material display, Texture source,
                Vector2Int canvas, HeightPreviewRenderer.Channel channel, float scale, int mask, bool textured, bool highlight, bool gamma)
            {
                var weights = channel == HeightPreviewRenderer.Channel.Luminance ? new Vector4(.2126f, .7152f, .0722f, 0) : Vector4.zero;
                if (channel != HeightPreviewRenderer.Channel.Luminance) weights[Mathf.Clamp((int)channel, 0, 3)] = 1;
                float longest = Mathf.Max(1, Mathf.Max(canvas.x, canvas.y));
                compute.SetTexture(kernel, "_Source", source); compute.SetTexture(kernel, "_Geometry", geometry);
                compute.SetVector("_SourceSize", new Vector4(1f / source.width, 1f / source.height, source.width, source.height));
                compute.SetVector("_Footprint", new Vector4(canvas.x / longest, canvas.y / longest, 0, 0));
                compute.SetVector("_HeightChannel", weights);
                compute.SetFloat("_HeightScale", float.IsFinite(scale) ? Mathf.Clamp(scale, -4, 4) : .25f);
                compute.SetInt("_Size", size); compute.SetInt("_Steps", source.height * 2);
                compute.Dispatch(kernel, size, 1, 1);
                GL.sRGBWrite = false;
                display.SetTexture("_Source", source);
                display.SetVector("_Channels", new Vector4((mask & 1) != 0 ? 1 : 0, (mask & 2) != 0 ? 1 : 0,
                    (mask & 4) != 0 ? 1 : 0, (mask & 8) != 0 ? 1 : 0));
                display.SetFloat("_Textured", textured ? 1 : 0);
                display.SetFloat("_EncodeGamma", gamma ? 1 : 0);
                display.SetVector("_HeightChannel", weights);
                display.SetFloat("_HighlightOutOfRange", highlight ? 1 : 0);
                Graphics.Blit(geometry, output, display);
            }

            internal Difference Compare(RenderTexture expected, RenderTexture rendered, float tolerance)
            {
                Read(expected, expectedReadback); Read(rendered, actualReadback);
                var a = expectedReadback.GetRawTextureData<Color>();
                var b = actualReadback.GetRawTextureData<Color>();
                var result = new Difference();
                for (int i = 0; i < a.Length; i++)
                {
                    float error = 0;
                    bool finite = true;
                    for (int component = 0; component < 4; component++)
                    {
                        finite &= float.IsFinite(a[i][component]) && float.IsFinite(b[i][component]);
                        float delta = Mathf.Abs(a[i][component] - b[i][component]);
                        if (float.IsFinite(delta)) error = Mathf.Max(error, delta);
                    }
                    result.maximumAbsoluteError = Mathf.Max(result.maximumAbsoluteError, error);
                    if (!finite) result.nonFinitePixels++;
                    if (!finite || error > tolerance)
                    {
                        result.mismatchedPixels++;
                        result.firstMismatch ??= $"({i % size},{i / size}): expected {a[i]}, actual {b[i]}";
                    }
                }
                return result;
            }

            internal void CountReferencePixels(out int geometryPixels, out int highlightPixels)
            {
                Read(geometry, expectedReadback); Read(output, actualReadback);
                var geometryData = expectedReadback.GetRawTextureData<Color>();
                var outputData = actualReadback.GetRawTextureData<Color>();
                geometryPixels = highlightPixels = 0;
                for (int i = 0; i < geometryData.Length; i++)
                {
                    if (geometryData[i].a > .5f) geometryPixels++;
                    var c = outputData[i];
                    if (Mathf.Abs(c.r - 1) < .00001f && Mathf.Abs(c.g) < .00001f && Mathf.Abs(c.b - 1) < .00001f && Mathf.Abs(c.a - 1) < .00001f)
                        highlightPixels++;
                }
            }

            void Read(RenderTexture texture, Texture2D readback)
            {
                RenderTexture.active = texture;
                readback.ReadPixels(new Rect(0, 0, size, size), 0, 0, false);
            }

            public void Dispose()
            {
                copied.Dispose();
                if (geometry != null) Object.DestroyImmediate(geometry);
                if (output != null) Object.DestroyImmediate(output);
                if (actual != null) Object.DestroyImmediate(actual);
                if (expectedReadback != null) Object.DestroyImmediate(expectedReadback);
                if (actualReadback != null) Object.DestroyImmediate(actualReadback);
            }
        }
    }
}
