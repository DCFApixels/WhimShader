using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

namespace DCFApixels.WhimShader
{
    /// <summary>
    /// Main-thread preview renderer. Borrows all input materials, property blocks and textures.
    /// Dispose when the owning window/view closes. No graph generation or reflection.
    /// </summary>
    internal sealed class WhimPreviewRenderer : IDisposable
    {
        const string ShaderPath = "Packages/com.dcfapixels.whimshader/Shaders/WhimPreview.shader";
        static readonly int ChannelsId = Shader.PropertyToID("_Channels");
        static readonly int FitScaleId = Shader.PropertyToID("_FitScale");
        static readonly int MainTexId = Shader.PropertyToID("_MainTex");
        static readonly HashSet<WhimPreviewRenderer> Live = new();

        PreviewRenderUtility preview;
        GameObject previewObject;
        MeshFilter meshFilter;
        MeshRenderer meshRenderer;
        Mesh sphere, quad;
        readonly Dictionary<RenderTexture, HeightPreviewRenderer> heightRenderers = new();
        readonly List<RenderTexture> staleHeightTargets = new();
        Material displayMaterial;
        bool disposed, rendering, disposePending;

        static WhimPreviewRenderer()
        {
            AssemblyReloadEvents.beforeAssemblyReload += DisposeAll;
            EditorApplication.quitting += DisposeAll;
        }

        internal WhimPreviewRenderer() { Live.Add(this); }

        /// <summary>
        /// Render the actual shader into an RGBA target with transparent clear, without checkerboard
        /// or BlitNoAlpha. ThreeD uses a sphere; Flat and Height use a full-frame quad.
        /// The caller supplies native preview properties (including its selected output) and chooses
        /// useSRP=true for the final shader, false for a native per-node preview shader.
        /// Height here is only the raw flat input; call Render(..., Height, ...) afterwards.
        /// Alpha is the shader's rendered framebuffer alpha, including its own blending/clip rules.
        /// </summary>
        internal void RenderMaterial(Material material, MaterialPropertyBlock properties,
            WhimShaderPreviewMode mode, RenderTexture target, bool useSRP)
        {
            CheckMode(mode);
            if (material == null) throw new ArgumentNullException(nameof(material));
            CheckTarget(target);
            if (!GraphicsFormatUtility.HasAlphaChannel(target.graphicsFormat))
                throw new ArgumentException("Raw preview target must have an alpha channel.", nameof(target));
            EnterRender();
            try
            {
                EnsureResources(true);
                if (mode == WhimShaderPreviewMode.ThreeD)
                    ConfigureSphere(target);
                else
                    ConfigureQuad(target);
                meshRenderer.sharedMaterial = material;
                meshRenderer.SetPropertyBlock(properties);
                EditorUtility.SetCameraAnimateMaterialsTime(preview.camera,
                    properties != null ? properties.GetVector("_TimeParameters").x : Time.realtimeSinceStartup);
                RenderCamera(target, useSRP);
            }
            finally { FinishRender(); }
        }

        // Compatibility for callers that still expose a single channel selector.
        internal void Render(RenderTexture source, RenderTexture target, WhimShaderPreviewMode mode,
            WhimShaderPreviewChannel channel, float heightScale)
        {
            if ((int)channel < 0 || (int)channel > 4) throw new ArgumentOutOfRangeException(nameof(channel));
            int mask = channel == WhimShaderPreviewChannel.RGBA ? 15 : 1 << ((int)channel - 1);
            Render(source, target, mode, mask, HeightPreviewRenderer.Channel.R, heightScale);
        }

        /// <summary>
        /// WhimTex display contract: independent RGBA mask and height channel. Height uses the
        /// copied horizon compute/display renderer, with no mesh, camera orbit or height saturation.
        /// canvasSize is the logical source footprint (defaults to source dimensions), never the
        /// panel size. Use square Height targets with Image.ScaleToFit, as in WhimTex.
        /// Nodes pass encodeGamma=false; final previews pass true. ThreeD receives an actual raw
        /// sphere render from RenderMaterial and only applies the display-channel mask here.
        /// </summary>
        internal void Render(RenderTexture source, RenderTexture target, WhimShaderPreviewMode mode,
            int channelMask, HeightPreviewRenderer.Channel heightChannel, float heightScale = .25f,
            bool heightTexture = true, bool highlightOutOfRange = false, bool encodeGamma = true,
            Vector2Int? canvasSize = null)
        {
            CheckMode(mode);
            CheckTexture(source, nameof(source));
            if (!source.IsCreated()) throw new ArgumentException("Source texture is not created.", nameof(source));
            if (source.antiAliasing > 1 && source.bindTextureMS)
                throw new ArgumentException("Resolve the multisampled source before previewing it.", nameof(source));
            CheckTarget(target);
            EnterRender();
            RenderTexture temporary = null;
            try
            {
                EnsureResources(false);
                PruneHeightRenderers();
                if (source == target)
                {
                    var descriptor = source.descriptor;
                    descriptor.depthBufferBits = 0;
                    descriptor.msaaSamples = 1;
                    descriptor.bindMS = false;
                    descriptor.useMipMap = false;
                    descriptor.autoGenerateMips = false;
                    temporary = RenderTexture.GetTemporary(descriptor);
                    BlitRaw(source, temporary);
                    source = temporary;
                }
                if (mode != WhimShaderPreviewMode.Height)
                {
                    ReleaseHeightRenderer(target);
                    BlitChannels(source, target, channelMask, encodeGamma);
                    return;
                }

                var footprint = canvasSize ?? new Vector2Int(source.width, source.height);
                if (footprint.x < 1 || footprint.y < 1)
                    throw new ArgumentOutOfRangeException(nameof(canvasSize));
                if ((int)heightChannel < 0 || (int)heightChannel > 4)
                    throw new ArgumentOutOfRangeException(nameof(heightChannel));
                if (!heightRenderers.TryGetValue(target, out var heightRenderer))
                {
                    heightRenderer = new HeightPreviewRenderer();
                    heightRenderers.Add(target, heightRenderer);
                }
                heightRenderer.Render(source, footprint, Mathf.Max(target.width, target.height), heightChannel, heightScale);
                var output = heightRenderer.Display(source, channelMask & 15, heightTexture,
                    encodeGamma: encodeGamma, highlightOutOfRange: highlightOutOfRange);
                // The source renderer's output is square. Preserve its fixed projection even if a
                // legacy caller supplies a rectangular target; the UI normally uses a square RT.
                BlitRaw(output, target, fitSquare: true);
            }
            finally
            {
                if (temporary != null) RenderTexture.ReleaseTemporary(temporary);
                FinishRender();
            }
        }

        void PruneHeightRenderers()
        {
            staleHeightTargets.Clear();
            foreach (var pair in heightRenderers)
                if (pair.Key == null || !pair.Key.IsCreated()) staleHeightTargets.Add(pair.Key);
            foreach (var target in staleHeightTargets) ReleaseHeightRenderer(target);
            staleHeightTargets.Clear();
        }

        void ReleaseHeightRenderer(RenderTexture target)
        {
            if (!heightRenderers.TryGetValue(target, out var renderer)) return;
            renderer.Dispose();
            heightRenderers.Remove(target);
        }

        // Call before the service destroys a display RT, including when no further render follows.
        internal void ReleaseTarget(RenderTexture target)
        {
            if (!ReferenceEquals(target, null)) ReleaseHeightRenderer(target);
        }

        void EnsureResources(bool requireCamera)
        {
            if (displayMaterial == null)
            {
                var shader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath) ?? Shader.Find("Hidden/WhimShader/Preview");
                if (shader == null) throw new InvalidOperationException("WhimPreview.shader has not been imported: " + ShaderPath);
                displayMaterial = new Material(shader) { name = "WhimShader preview display", hideFlags = HideFlags.HideAndDontSave };
            }
            if (!requireCamera || preview != null) return;
            preview = new PreviewRenderUtility();
            try
            {
                preview.ambientColor = new Color(.08f, .08f, .08f, 1);
                var camera = preview.camera;
                EditorUtility.SetCameraAnimateMaterials(camera, true);
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.clear;
                camera.allowHDR = true;
                camera.allowMSAA = false;
                camera.renderingPath = RenderingPath.Forward;
                var data = camera.GetUniversalAdditionalCameraData();
                data.renderPostProcessing = false;
                data.renderShadows = false;
                data.antialiasing = AntialiasingMode.None;
                var lights = preview.lights;
                lights[0].transform.rotation = Quaternion.Euler(35, -30, 0);
                lights[0].color = Color.white;
                lights[0].intensity = 1;
                lights[1].transform.rotation = Quaternion.Euler(340, 160, 0);
                lights[1].color = Color.white;
                lights[1].intensity = .35f;
                foreach (var light in lights) light.shadows = LightShadows.None;
                sphere = Resources.GetBuiltinResource<Mesh>("Sphere.fbx");
                quad = Resources.GetBuiltinResource<Mesh>("Quad.fbx");
                if (sphere == null || quad == null) throw new InvalidOperationException("Unity built-in preview meshes were not found.");
                previewObject = new GameObject("WhimShader preview mesh") { hideFlags = HideFlags.HideAndDontSave };
                preview.AddSingleGO(previewObject);
                meshFilter = previewObject.AddComponent<MeshFilter>();
                meshRenderer = previewObject.AddComponent<MeshRenderer>();
                meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
                meshRenderer.receiveShadows = false;
                meshRenderer.lightProbeUsage = LightProbeUsage.Off;
                meshRenderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
                meshRenderer.enabled = false;
            }
            catch
            {
                preview.Cleanup();
                preview = null;
                throw;
            }
        }

        void ConfigureQuad(RenderTexture target)
        {
            float aspect = (float)target.width / target.height;
            meshFilter.sharedMesh = quad;
            previewObject.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            previewObject.transform.localScale = new Vector3(aspect, 1, 1);
            var camera = preview.camera;
            camera.orthographic = true;
            camera.orthographicSize = .5f;
            camera.nearClipPlane = .01f;
            camera.farClipPlane = 100;
            camera.transform.SetPositionAndRotation(new Vector3(0, 0, -3), Quaternion.identity);
        }

        void ConfigureSphere(RenderTexture target)
        {
            var bounds = sphere.bounds;
            float scale = 1f / Mathf.Max(bounds.extents.x, bounds.extents.y, bounds.extents.z);
            meshFilter.sharedMesh = sphere;
            previewObject.transform.SetPositionAndRotation(-bounds.center * scale, Quaternion.identity);
            previewObject.transform.localScale = Vector3.one * scale;
            var camera = preview.camera;
            camera.orthographic = false;
            camera.fieldOfView = 30;
            float halfAngle = camera.fieldOfView * .5f * Mathf.Deg2Rad;
            float aspect = (float)target.width / target.height;
            if (aspect < 1) halfAngle = Mathf.Atan(Mathf.Tan(halfAngle) * aspect);
            float distance = 1.08f / Mathf.Sin(halfAngle);
            camera.nearClipPlane = .01f;
            camera.farClipPlane = distance + 100;
            camera.transform.SetPositionAndRotation(new Vector3(0, 0, -distance), Quaternion.identity);
        }

        void RenderCamera(RenderTexture target, bool useSRP)
        {
            var previousTarget = RenderTexture.active;
            bool previousSRGB = GL.sRGBWrite;
            bool previousSRP = Unsupported.useScriptableRenderPipeline;
            bool begun = false;
            try
            {
                // PRU owns a depth-backed HDR RGBA intermediate. The caller's target can be color-only.
                float pixelsPerPoint = Mathf.Max(1, EditorGUIUtility.pixelsPerPoint);
                preview.BeginPreview(new Rect(0, 0, target.width / pixelsPerPoint, target.height / pixelsPerPoint), GUIStyle.none);
                begun = true;
                var raw = preview.camera.targetTexture;
                preview.camera.pixelRect = new Rect(0, 0, raw.width, raw.height);
                preview.camera.aspect = (float)target.width / target.height;
                preview.camera.backgroundColor = Color.clear;
                preview.camera.clearFlags = CameraClearFlags.SolidColor;
                meshRenderer.enabled = true;
                // Persistent renderer prevents queued DrawMesh calls accumulating across previews
                // in the same Editor frame. SetPropertyBlock supplies exactly the caller's values.
                preview.Render(allowScriptableRenderPipeline: useSRP, updatefov: false);
                // No checkerboard, alpha replacement, unpremultiplication, or display-channel mapping.
                BlitRaw(raw, target);
            }
            finally
            {
                try { if (begun) preview.EndPreview(); }
                finally
                {
                    meshRenderer.enabled = false;
                    Unsupported.useScriptableRenderPipeline = previousSRP;
                    RenderTexture.active = previousTarget;
                    GL.sRGBWrite = previousSRGB;
                }
            }
        }

        void BlitChannels(RenderTexture source, RenderTexture target, int channelMask, bool encodeGamma)
        {
            var previousTarget = RenderTexture.active;
            bool previousSRGB = GL.sRGBWrite;
            try
            {
                // Literal LayerPreviewPanel.UpdateChannels settings; encodeGamma=false preserves
                // WhimTex's raw node-preview convention rather than encoding its values twice.
                displayMaterial.SetVector(ChannelsId, new Vector4((channelMask & 1) != 0 ? 1 : 0,
                    (channelMask & 2) != 0 ? 1 : 0, (channelMask & 4) != 0 ? 1 : 0, (channelMask & 8) != 0 ? 1 : 0));
                displayMaterial.SetFloat("_Exposure", 1);
                displayMaterial.SetFloat("_Debug", 0);
                displayMaterial.SetFloat("_EncodeGamma", encodeGamma ? 1 : 0);
                GL.sRGBWrite = target.sRGB;
                Graphics.Blit(source, target, displayMaterial, 0);
            }
            finally
            {
                RenderTexture.active = previousTarget;
                GL.sRGBWrite = previousSRGB;
            }
        }

        void BlitRaw(RenderTexture source, RenderTexture target, bool fitSquare = false)
        {
            var previousTarget = RenderTexture.active;
            bool previousSRGB = GL.sRGBWrite;
            try
            {
                float side = Mathf.Min(target.width, target.height);
                displayMaterial.SetVector(FitScaleId, fitSquare
                    ? new Vector4(side / target.width, side / target.height, 0, 0)
                    : new Vector4(1, 1, 0, 0));
                GL.sRGBWrite = target.sRGB;
                if (fitSquare)
                {
                    RenderTexture.active = target;
                    GL.Clear(false, true, Color.clear);
                }
                // Separate from DisplayChannels: preserve signed/HDR RGBA for actual shader data.
                Graphics.Blit(source, target, displayMaterial, 1);
            }
            finally
            {
                RenderTexture.active = previousTarget;
                GL.sRGBWrite = previousSRGB;
            }
        }

        static void CheckMode(WhimShaderPreviewMode mode)
        {
            if ((int)mode < (int)WhimShaderPreviewMode.Flat || (int)mode > (int)WhimShaderPreviewMode.ThreeD)
                throw new ArgumentOutOfRangeException(nameof(mode));
        }

        static void CheckTexture(RenderTexture texture, string name)
        {
            if (texture == null) throw new ArgumentNullException(name);
            if (texture.dimension != TextureDimension.Tex2D || texture.width < 1 || texture.height < 1)
                throw new ArgumentException("Preview requires a nonempty 2D render texture.", name);
        }

        static void CheckTarget(RenderTexture target)
        {
            CheckTexture(target, nameof(target));
            if (target.antiAliasing != 1)
                throw new ArgumentException("Preview target must be single-sampled.", nameof(target));
            if (!target.IsCreated() && !target.Create())
                throw new InvalidOperationException("Cannot create the preview target.");
        }

        void EnterRender()
        {
            if (disposed) throw new ObjectDisposedException(nameof(WhimPreviewRenderer));
            if (rendering) throw new InvalidOperationException("WhimPreviewRenderer cannot render recursively.");
            rendering = true;
        }

        void FinishRender()
        {
            if (meshRenderer != null)
            {
                meshRenderer.enabled = false;
                meshRenderer.SetPropertyBlock(null);
                meshRenderer.sharedMaterial = null;
            }
            if (displayMaterial != null) displayMaterial.SetTexture(MainTexId, null);
            rendering = false;
            if (disposePending) Dispose();
        }

        public void Dispose()
        {
            if (disposed) return;
            if (rendering) { disposePending = true; return; }
            disposed = true;
            Live.Remove(this);
            try { preview?.Cleanup(); }
            finally
            {
                foreach (var renderer in heightRenderers.Values) renderer.Dispose();
                heightRenderers.Clear();
                staleHeightTargets.Clear();
                if (displayMaterial != null) Object.DestroyImmediate(displayMaterial);
                preview = null; previewObject = null; meshFilter = null; meshRenderer = null;
                sphere = null; quad = null; displayMaterial = null;
            }
        }

        static void DisposeAll()
        {
            var copy = new WhimPreviewRenderer[Live.Count];
            Live.CopyTo(copy);
            foreach (var renderer in copy) renderer.Dispose();
        }
    }
}
