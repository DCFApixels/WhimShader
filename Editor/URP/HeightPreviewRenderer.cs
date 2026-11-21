// Copied/adapted from WhimTex. Copyright (c) 2026 Mikhail. MIT; see Shaders/WhimTex-NOTICE.md.
using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace DCFApixels.WhimShader
{
    // Owns only preview-sized outputs; callers retain ownership of the source image.
    internal sealed class HeightPreviewRenderer : IDisposable
    {
        internal enum Channel { R, G, B, A, Luminance }
        private static ComputeShader compute;
        private static int kernel;
        private Material display;
        private RenderTexture geometry, output;
        private Vector4 heightChannelWeights;
        internal RenderTexture Geometry => geometry;
        internal RenderTexture Output => output;
        internal static bool Supported
        {
            get
            {
                if (!SystemInfo.supportsComputeShaders ||
                    !SystemInfo.IsFormatSupported(GraphicsFormat.R32G32B32A32_SFloat, GraphicsFormatUsage.LoadStore)) return false;
                if (compute == null)
                {
                    compute = AssetDatabase.LoadAssetAtPath<ComputeShader>("Packages/com.dcfapixels.whimshader/Shaders/WhimHeightPreview.compute");
                    if (compute == null) return false;
                    kernel = compute.FindKernel("Horizon");
                }
                return compute.IsSupported(kernel);
            }
        }

        internal void Render(Texture source, Vector2Int canvasSize, int size, Channel channel, float scale)
        {
            if (!Supported) throw new NotSupportedException("Height preview requires compute shaders and a floating-point render target.");
            Ensure(ref geometry, size, true);
            var weights = channel == Channel.Luminance ? new Vector4(.2126f, .7152f, .0722f, 0) : Vector4.zero;
            if (channel != Channel.Luminance) weights[Mathf.Clamp((int)channel, 0, 3)] = 1;
            heightChannelWeights = weights;
            float longest = Mathf.Max(1, Mathf.Max(canvasSize.x, canvasSize.y));
            compute.SetTexture(kernel, "_Source", source); compute.SetTexture(kernel, "_Geometry", geometry);
            compute.SetVector("_SourceSize", new Vector4(1f / source.width, 1f / source.height, source.width, source.height));
            compute.SetVector("_Footprint", new Vector4(canvasSize.x / longest, canvasSize.y / longest, 0, 0));
            compute.SetVector("_HeightChannel", weights);
            compute.SetFloat("_HeightScale", float.IsFinite(scale) ? Mathf.Clamp(scale, -4, 4) : .25f);
            compute.SetInt("_Size", size); compute.SetInt("_Steps", source.height * 2);
            compute.Dispatch(kernel, size, 1, 1);
        }

        internal RenderTexture Display(Texture source, int channelMask, bool textured, bool encodeGamma = true, bool highlightOutOfRange = false)
        {
            if (geometry == null) return null;
            Ensure(ref output, geometry.width, false);
            if (display == null)
            {
                var shader = AssetDatabase.LoadAssetAtPath<Shader>("Packages/com.dcfapixels.whimshader/Shaders/WhimHeightPreviewDisplay.shader");
                if (shader == null || !shader.isSupported) throw new NotSupportedException("Height preview display shader is unavailable.");
                display = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            }
            var previous = RenderTexture.active; bool srgb = GL.sRGBWrite;
            try
            {
                GL.sRGBWrite = false;
                display.SetTexture("_Source", source);
                display.SetVector("_Channels", new Vector4((channelMask & 1) != 0 ? 1 : 0, (channelMask & 2) != 0 ? 1 : 0,
                    (channelMask & 4) != 0 ? 1 : 0, (channelMask & 8) != 0 ? 1 : 0));
                display.SetFloat("_Textured", textured ? 1 : 0);
                display.SetFloat("_EncodeGamma", encodeGamma ? 1 : 0);
                display.SetVector("_HeightChannel", heightChannelWeights);
                display.SetFloat("_HighlightOutOfRange", highlightOutOfRange ? 1 : 0);
                Graphics.Blit(geometry, output, display);
                return output;
            }
            finally { RenderTexture.active = previous; GL.sRGBWrite = srgb; }
        }

        private static void Ensure(ref RenderTexture texture, int size, bool randomWrite)
        {
            if (texture != null && texture.IsCreated() && texture.width == size) return;
            if (texture != null) RenderTexture.ReleaseTemporary(texture);
            var descriptor = new RenderTextureDescriptor(size, size, RenderTextureFormat.ARGBFloat, 0)
            { enableRandomWrite = randomWrite, sRGB = false, msaaSamples = 1 };
            texture = RenderTexture.GetTemporary(descriptor);
            texture.filterMode = randomWrite ? FilterMode.Point : FilterMode.Bilinear;
            texture.wrapMode = TextureWrapMode.Clamp;
        }

        public void Dispose()
        {
            if (geometry != null) RenderTexture.ReleaseTemporary(geometry);
            if (output != null) RenderTexture.ReleaseTemporary(output);
            geometry = output = null;
            if (display != null) UnityEngine.Object.DestroyImmediate(display);
            display = null;
        }
    }
}
