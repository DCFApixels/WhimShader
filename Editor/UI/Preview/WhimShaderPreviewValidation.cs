using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.Graphing;
using UnityEditor.Graphing.Util;
using UnityEditor.Rendering.Universal.ShaderGraph;
using UnityEditor.ShaderGraph;
using UnityEditor.ShaderGraph.Internal;
using UnityEditor.ShaderGraph.Serialization;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace DCFApixels.WhimShader
{
    /// <summary>Explicit main-thread, nonblocking integration probe; never runs automatically.</summary>
    public static class WhimShaderPreviewValidation
    {
        [Serializable] sealed class ImageResult
        {
            public int width, height;
            public string hash, png, error;
            public Color center;
            public float alphaMin, alphaMax;
        }
        [Serializable] sealed class PreviewResult
        {
            public string subject, nodeId, mode, channel, heightChannel, error, details;
            public int outputSlotId, channelMask;
            public float heightScale;
            public bool pending, ownsShader, binding, visible, renderRequired, heightTexture, highlightOutOfRange;
            public ImageResult raw, display;
        }
        [Serializable] sealed class Report
        {
            public bool ready;
            public string fixturePath, snapshotFolder, graphHash, error;
            public int framesSinceStep, shaderBuilds, rawRenders, displayRenders, ownedTextures,
                nodeBindings, presentationBindings, expectedNodeBindings, missingNodeBindings;
            public Vector4 rootRGBA;
            public List<PreviewResult> previews = new();
        }

        static WhimShaderWindow fixtureWindow;
        static string fixturePath, snapshotFolder, rgbaId, combineId, nativeId;
        static int framesSinceStep, snapshotNumber;
        static readonly Vector4 InitialRGBA = new(.2f, .4f, .6f, .25f);

        /// <summary>Creates native Add float4/scalar and multi-output Combine previews plus transparent final.</summary>
        public static string Begin()
        {
            if (fixtureWindow != null) throw new InvalidOperationException("End the existing preview validation session first.");
            if (EditorApplication.isCompiling || EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Begin in idle Edit Mode.");
            const string parent = "Assets/WhimShaderValidation";
            if (!AssetDatabase.IsValidFolder(parent)) AssetDatabase.CreateFolder("Assets", "WhimShaderValidation");
            string id = Guid.NewGuid().ToString("N");
            fixturePath = parent + "/Preview_" + id + ".whimshader";
            snapshotFolder = "Temp/WhimShader/PreviewValidation/" + id;
            Directory.CreateDirectory(snapshotFolder);
            var graph = new GraphData { assetGuid = id, messageManager = new MessageManager() };
            var owner = ScriptableObject.CreateInstance<GraphObject>(); owner.hideFlags = HideFlags.HideAndDontSave; owner.graph = graph;
            try
            {
                graph.AddContexts();
                var target = new UniversalTarget { surfaceType = SurfaceType.Transparent, alphaMode = AlphaMode.Alpha };
                if (!target.TrySetActiveSubTarget(typeof(UniversalUnlitSubTarget))) throw new InvalidOperationException("Native URP Unlit unavailable.");
                graph.InitializeOutputs(new Target[] { target }, new[] { BlockFields.VertexDescription.Position,
                    BlockFields.SurfaceDescription.BaseColor, BlockFields.SurfaceDescription.Alpha });
                graph.OnEnable();
                var property = new Vector4ShaderProperty { displayName = "Preview RGBA", overrideReferenceName = "_PreviewRGBA", value = InitialRGBA };
                graph.AddGraphInput(property);
                var input = new PropertyNode { property = property, previewExpanded = false };
                var rgba = new AddNode { previewExpanded = true };
                var split = new SplitNode { previewExpanded = false };
                var combine = new CombineNode { previewExpanded = true };
                var native = new AddNode { previewExpanded = true };
                graph.AddNode(input); graph.AddNode(rgba); graph.AddNode(split); graph.AddNode(combine); graph.AddNode(native);
                var draw = input.drawState; draw.position = new Rect(-550, 30, 210, 80); input.drawState = draw;
                draw = rgba.drawState; draw.position = new Rect(-550, 120, 210, 240); rgba.drawState = draw;
                draw = split.drawState; draw.position = new Rect(-300, -80, 210, 80); split.drawState = draw;
                draw = combine.drawState; draw.position = new Rect(-300, 120, 210, 240); combine.drawState = draw;
                draw = native.drawState; draw.position = new Rect(-50, 120, 210, 240); native.drawState = draw;
                graph.Connect(input.GetSlotReference(0), split.GetSlotReference(SplitNode.InputSlotId));
                for (int component = 0; component < 4; component++)
                    graph.Connect(split.GetSlotReference(component + 1), combine.GetSlotReference(component));
                graph.Connect(combine.GetSlotReference(4), rgba.GetSlotReference(0));
                graph.Connect(split.GetSlotReference(SplitNode.OutputSlotRId), native.GetSlotReference(0));
                var color = graph.GetNodes<BlockNode>().First(b => b.descriptor == BlockFields.SurfaceDescription.BaseColor);
                var alpha = graph.GetNodes<BlockNode>().First(b => b.descriptor == BlockFields.SurfaceDescription.Alpha);
                graph.Connect(rgba.GetSlotReference(2), color.GetSlotReference(0));
                graph.Connect(split.GetSlotReference(SplitNode.OutputSlotAId), alpha.GetSlotReference(0));
                graph.ValidateGraph();
                if (graph.messageManager.HasSeverity(_ => true, UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error))
                    throw new InvalidOperationException("Fixture graph failed native validation.");
                if (!rgba.hasPreview || !native.hasPreview || !combine.hasPreview)
                    throw new InvalidOperationException("Native preview capability changed; update the pinned fixture.");
                rgbaId = rgba.objectId; combineId = combine.objectId; nativeId = native.objectId;
                File.WriteAllText(fixturePath, MultiJson.Serialize(graph));
            }
            finally { graph.OnDisable(); Object.DestroyImmediate(owner); }
            AssetDatabase.ImportAsset(fixturePath, ImportAssetOptions.ForceSynchronousImport);
            fixtureWindow = WhimShaderWindow.Open(fixturePath);
            var position = fixtureWindow.position; position.size = new Vector2(1200, 800); fixtureWindow.position = position;
            fixtureWindow.Show(); fixtureWindow.Focus();
            var framedWindow = fixtureWindow;
            framedWindow.rootVisualElement.schedule.Execute(() =>
            {
                if (framedWindow != null) framedWindow.PreviewService?.Editor.graphView.FrameAll();
            }).StartingIn(150);
            framesSinceStep = snapshotNumber = 0;
            EditorApplication.update -= CountFrame;
            EditorApplication.update += CountFrame;
            return JsonUtility.ToJson(new Report { fixturePath = fixturePath, snapshotFolder = snapshotFolder }, true);
        }

        /// <summary>Sets persisted presentation metadata. Allow host Editor updates before Snapshot.</summary>
        public static string Step(string mode, string channel, int outputSlotId = 4, string subject = "all")
        {
            var window = RequireFixture();
            if (!Enum.TryParse(mode == "3D" ? "ThreeD" : mode, true, out WhimShaderPreviewMode parsedMode) || (int)parsedMode < 0 || (int)parsedMode > 2)
                throw new ArgumentException("Mode must be Flat, Height or 3D.", nameof(mode));
            if (!Enum.TryParse(channel, true, out WhimShaderPreviewChannel parsedChannel) || (int)parsedChannel < 0 || (int)parsedChannel > 4)
                throw new ArgumentException("Channel must be RGBA, R, G, B or A.", nameof(channel));
            subject = subject.ToLowerInvariant();
            ValidateSubject(subject);
            if (subject is "all" or "combine")
            {
                if (outputSlotId < 4 || outputSlotId > 6) throw new ArgumentOutOfRangeException(nameof(outputSlotId), "Combine output IDs: RGBA=4, RGB=5, RG=6.");
                Set(window.Graph.GetNodeFromId(combineId), outputSlotId);
            }
            if (subject is "all" or "rgba") Set(window.Graph.GetNodeFromId(rgbaId), 2);
            if (subject is "all" or "native") Set(window.Graph.GetNodeFromId(nativeId), 2);
            if (subject is "all" or "final") Set(null, 0);
            framesSinceStep = 0;
            return JsonUtility.ToJson(new Report { fixturePath = fixturePath, snapshotFolder = snapshotFolder }, true);

            void Set(AbstractMaterialNode node, int slot)
            {
                var state = node == null ? WhimShaderNodePresentationMetadata.ReadFinal(window.Graph) : WhimShaderNodePresentationMetadata.Read(node);
                state.mode = parsedMode; state.channel = parsedChannel; state.channelMask = -1; state.outputSlotId = slot;
                if (node == null) window.PreviewService.SetFinalPreviewSettings(state);
                else window.PreviewService.SetNodeSettings(node, state);
            }
        }

        public static void SetHeight(string channel = "R", float scale = .25f, bool textured = true,
            bool highlightOutOfRange = false, string subject = "all")
        {
            if (!Enum.TryParse(channel, true, out HeightPreviewRenderer.Channel parsed) || (int)parsed < 0 || (int)parsed > 4)
                throw new ArgumentException("Height channel must be R, G, B, A or Luminance.", nameof(channel));
            ChangeSettings(subject, state =>
            { state.heightChannel = parsed; state.heightScale = scale; state.heightTexture = textured; state.highlightOutOfRange = highlightOutOfRange; return state; });
        }

        public static void SetChannels(int mask, string subject = "all")
        {
            if (mask < 0 || mask > 15) throw new ArgumentOutOfRangeException(nameof(mask));
            ChangeSettings(subject, state => { state.channelMask = mask; return state; });
        }

        static void ChangeSettings(string subject, Func<WhimShaderNodePreviewSettings, WhimShaderNodePreviewSettings> change)
        {
            var window = RequireFixture(); subject = subject.ToLowerInvariant(); ValidateSubject(subject);
            if (subject is "all" or "rgba") Set(rgbaId);
            if (subject is "all" or "combine") Set(combineId);
            if (subject is "all" or "native") Set(nativeId);
            if (subject is "all" or "final") window.PreviewService.SetFinalPreviewSettings(change(WhimShaderNodePresentationMetadata.ReadFinal(window.Graph)));
            framesSinceStep = 0;
            void Set(string id)
            {
                var node = window.Graph.GetNodeFromId(id);
                window.PreviewService.SetNodeSettings(node, change(WhimShaderNodePresentationMetadata.Read(node)));
            }
        }

        static void ValidateSubject(string subject)
        {
            if (subject is not ("all" or "rgba" or "combine" or "native" or "final"))
                throw new ArgumentException("Subject must be all, rgba, combine, native or final.", nameof(subject));
        }

        public static void SetRGBA(float r, float g, float b, float a)
        {
            var window = RequireFixture();
            window.Graph.owner.RegisterCompleteObjectUndo("Change Preview Fixture RGBA");
            var property = window.Graph.properties.OfType<Vector4ShaderProperty>().Single(p => p.referenceName == "_PreviewRGBA");
            property.value = new Vector4(r, g, b, a);
            foreach (var node in window.Graph.GetNodes<PropertyNode>())
                if (ReferenceEquals(node.property, property)) node.Dirty(ModificationScope.Node);
            window.Graph.owner.isDirty = true;
            framesSinceStep = 0;
        }

        /// <summary>JSON plus retained PNG snapshots; ready requires warm Editor updates and completed rendering.</summary>
        public static string Snapshot() => CaptureWindow(RequireFixture(), snapshotFolder, fixturePath, framesSinceStep);

        /// <summary>Readback of an already open window, useful for diagnosing production node previews.</summary>
        public static string SnapshotWindow(WhimShaderWindow window = null)
        {
            window ??= EditorWindow.focusedWindow as WhimShaderWindow;
            window ??= Resources.FindObjectsOfTypeAll<WhimShaderWindow>().FirstOrDefault(w => w.PreviewService != null);
            if (window == null) throw new InvalidOperationException("No initialized WhimShader window.");
            string folder = "Temp/WhimShader/PreviewValidation/Inspect_" + Guid.NewGuid().ToString("N");
            return CaptureWindow(window, folder, null, -1);
        }

        static string CaptureWindow(WhimShaderWindow window, string folder, string path, int warmFrames)
        {
            var service = window.PreviewService;
            var report = new Report { fixturePath = path, snapshotFolder = folder, framesSinceStep = warmFrames };
            if (service == null) { report.error = "Preview service is not initialized."; return JsonUtility.ToJson(report, true); }
            Directory.CreateDirectory(folder);
            string prefix = (++snapshotNumber).ToString("D4");
            report.shaderBuilds = service.ShaderBuildCount; report.rawRenders = service.RawRenderCount;
            report.displayRenders = service.DisplayRenderCount; report.ownedTextures = service.OwnedTextureCount;
            report.nodeBindings = service.NodePreviewCount; report.presentationBindings = service.PresentationPreviewCount;
            report.graphHash = Hash(System.Text.Encoding.UTF8.GetBytes(MultiJson.Serialize(window.Graph)));
            var property = window.Graph.properties.OfType<Vector4ShaderProperty>().FirstOrDefault(p => p.referenceName == "_PreviewRGBA");
            report.rootRGBA = property?.value ?? Vector4.zero;
            Add(null, "final", WhimShaderNodePresentationMetadata.ReadFinal(window.Graph));
            foreach (var node in window.Graph.GetNodes<AbstractMaterialNode>())
            {
                bool expected = node.previewExpanded && WhimShaderNodePresentation.SupportsPreview(node);
                bool binding = service.TryGetPreview(node, out _, out _, out _, out _, out _);
                if (expected) { report.expectedNodeBindings++; if (!binding) report.missingNodeBindings++; }
                if (!binding && !expected) continue;
                Add(node, node.objectId == rgbaId ? "rgba" : node.objectId == combineId ? "combine" : node.objectId == nativeId ? "native" : node.name,
                    WhimShaderNodePresentationMetadata.Read(node));
            }
            if (report.missingNodeBindings > 0) report.error = $"Missing {report.missingNodeBindings} expected node preview binding(s).";
            else if (report.nodeBindings == 0) report.error = "No node preview bindings; a final-only capture does not validate node previews.";
            report.ready = (warmFrames < 0 || warmFrames >= 3) && report.missingNodeBindings == 0 && report.nodeBindings > 0 &&
                report.previews.All(p => p.binding && (!p.renderRequired ||
                    !p.pending && string.IsNullOrEmpty(p.error) && p.raw != null && p.display != null && p.raw.error == null && p.display.error == null));
            string json = JsonUtility.ToJson(report, true);
            File.WriteAllText(folder + "/" + prefix + ".json", json);
            return json;

            void Add(AbstractMaterialNode node, string subject, WhimShaderNodePreviewSettings state)
            {
                bool bound = service.TryGetPreview(node, out var raw, out var display, out string error, out bool pending, out bool owns);
                bool visible = service.IsPreviewVisible(node);
                // Fixture nodes must actually render; other collapsed/offscreen nodes remain in
                // diagnostics without forcing work that the production visibility policy skips.
                bool required = node == null || visible || warmFrames >= 0 &&
                    (node.objectId == rgbaId || node.objectId == combineId || node.objectId == nativeId);
                string key = node?.objectId ?? "final";
                report.previews.Add(new PreviewResult { subject = subject, nodeId = key, mode = state.mode.ToString(), channel = state.channel.ToString(),
                    channelMask = state.EffectiveMask, heightChannel = state.heightChannel.ToString(), heightScale = state.heightScale,
                    heightTexture = state.heightTexture, highlightOutOfRange = state.highlightOutOfRange,
                    outputSlotId = state.outputSlotId, pending = pending, ownsShader = owns, binding = bound, visible = visible,
                    renderRequired = required, error = bound ? error : "No presentation binding", details = service.DescribePreview(node),
                    raw = ReadImage(raw, folder + "/" + prefix + "-" + key + "-raw.png"),
                    display = ReadImage(display, folder + "/" + prefix + "-" + key + "-display.png") });
            }
        }

        static ImageResult ReadImage(RenderTexture texture, string path)
        {
            if (texture == null || !texture.IsCreated()) return null;
            var result = new ImageResult { width = texture.width, height = texture.height, png = path };
            var previous = RenderTexture.active;
            Texture2D readback = null, png = null;
            try
            {
                readback = new Texture2D(texture.width, texture.height, TextureFormat.RGBAFloat, false, true);
                RenderTexture.active = texture;
                readback.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0); readback.Apply(false, false);
                result.hash = Hash(readback.GetRawTextureData<byte>().ToArray());
                result.center = readback.GetPixel(texture.width / 2, texture.height / 2);
                var pixels = readback.GetPixels();
                result.alphaMin = float.PositiveInfinity; result.alphaMax = float.NegativeInfinity;
                foreach (var pixel in pixels) { result.alphaMin = Mathf.Min(result.alphaMin, pixel.a); result.alphaMax = Mathf.Max(result.alphaMax, pixel.a); }
                // PNG is a display/debug artifact (HDR is clipped); the hash uses original float readback bytes.
                png = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, false, true);
                png.SetPixels(pixels); png.Apply(false, false); File.WriteAllBytes(path, png.EncodeToPNG());
            }
            catch (Exception error) { result.error = error.Message; }
            finally
            {
                RenderTexture.active = previous;
                if (readback != null) Object.DestroyImmediate(readback);
                if (png != null) Object.DestroyImmediate(png);
            }
            return result;
        }

        static string Hash(byte[] bytes)
        {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }
        static void CountFrame() { if (fixtureWindow != null) framesSinceStep++; }
        static WhimShaderWindow RequireFixture()
        {
            if (fixtureWindow == null || fixtureWindow.Graph == null || fixtureWindow.PreviewService == null)
                throw new InvalidOperationException("Call Begin and wait for the fixture window to initialize.");
            return fixtureWindow;
        }

        /// <summary>Closes only this test window; keeps the saved fixture and snapshots for inspection.</summary>
        public static void End()
        {
            EditorApplication.update -= CountFrame;
            if (fixtureWindow != null)
            {
                // Presentation experiments are intentionally not saved over the initial fixture.
                fixtureWindow.ClosePreviewValidationFixture();
            }
            fixtureWindow = null;
        }
    }
}
