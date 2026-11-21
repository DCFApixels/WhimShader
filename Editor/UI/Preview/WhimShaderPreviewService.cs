using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Experimental.GraphView;
using UnityEditor.Graphing;
using UnityEditor.ShaderGraph;
using UnityEditor.ShaderGraph.Drawing;
using UnityEditor.ShaderGraph.Drawing.Inspector;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace DCFApixels.WhimShader
{
    /// <summary>Owns presentation renders, never native PreviewManager resources.</summary>
    internal sealed class WhimShaderPreviewService : IDisposable
    {
        readonly GraphData graph;
        readonly GraphEditorView editor;
        readonly WhimShaderNodePresentation presentation;
        readonly WhimShaderFinalPreviewPanel finalPanel;
        readonly WhimPreviewRenderer renderer = new();
        readonly WhimShaderPreviewProperties properties = new();
        readonly Dictionary<AbstractMaterialNode, Entry> entries = new();
        readonly HashSet<AbstractMaterialNode> watched = new();
        readonly HashSet<AbstractMaterialNode> current = new();
        readonly List<AbstractMaterialNode> removed = new();
        readonly Stack<AbstractMaterialNode> dependencyStack = new();
        readonly List<MaterialSlot> finalSlots = new();
        readonly Entry final;
        readonly Action changed;
        readonly VisualElement viewport;
        readonly StyleSheet sheet;
        readonly MasterPreviewView nativeMaster;
        readonly StyleEnum<DisplayStyle> nativeDisplay;
        readonly bool addedSheet;
        List<BlockNode> temporaryBlocks;
        bool propertiesDirty = true, topologyDirty = true, settingsDirty = true, disposed, updating;
        bool propertiesValid;
        float frameTime;

        internal GraphData Graph => graph;
        internal GraphEditorView Editor => editor;
        internal int ShaderBuildCount { get; private set; }
        internal int RawRenderCount { get; private set; }
        internal int DisplayRenderCount { get; private set; }
        internal int OwnedTextureCount => (final.raw != null ? 1 : 0) + (final.display != null ? 1 : 0) +
            entries.Values.Sum(e => (e.raw != null ? 1 : 0) + (e.display != null ? 1 : 0));
        internal int NodePreviewCount => entries.Count;
        internal int PresentationPreviewCount => presentation.Previews.Count();
        internal string FinalError => final.error;
        internal bool IsPreviewVisible(AbstractMaterialNode node) => node == null ? final.Visible :
            entries.TryGetValue(node, out var entry) && entry.Visible;

        internal void SetNodeSettings(AbstractMaterialNode node, WhimShaderNodePreviewSettings settings)
            => presentation.SetPreviewSettings(node, settings);
        internal void SetFinalPreviewSettings(WhimShaderNodePreviewSettings settings) => SetFinalSettings(settings);
        internal void SetEdgeConnectorListener(IEdgeConnectorListener listener) => presentation.SetEdgeConnectorListener(listener);

        // Borrowed textures for parent validation/readback; callers must not destroy or retain them across Dispose.
        internal bool TryGetPreview(AbstractMaterialNode node, out RenderTexture raw, out RenderTexture display,
            out string error, out bool pending, out bool ownsShader)
        {
            Entry entry = node == null ? final : entries.TryGetValue(node, out var found) ? found : null;
            raw = entry?.raw; display = entry?.display; error = entry?.error;
            pending = entry == null || entry.codeDirty || entry.compiling || entry.rawDirty || entry.displayDirty ||
                NativeDrivesPreview(entry) && (entry.native?.shaderData == null || entry.native.shaderData.isOutOfDate || entry.native.shaderData.passesCompiling > 0);
            ownsShader = entry?.ownsSource ?? false;
            return entry != null;
        }

        internal string DescribePreview(AbstractMaterialNode node)
        {
            Entry entry = node == null ? final : entries.TryGetValue(node, out var found) ? found : null;
            if (entry == null) return "No presentation binding";
            var data = entry.native?.shaderData;
            return $"visible={entry.Visible}; bounds={entry.surface.worldBound}; nativeError={data?.hasError}; " +
                $"nativeOutOfDate={data?.isOutOfDate}; nativeCompiling={data?.passesCompiling}; " +
                $"nativeShader={data?.shader?.name}; ownedShader={entry.shader?.name}; " +
                $"rawDirty={entry.rawDirty}; displayDirty={entry.displayDirty}; timed={entry.timed}; " +
                $"nativeDrives={NativeDrivesPreview(entry)}; attached={entry.surface.panel != null}; imageVisible={entry.image.visible}; " +
                $"imageDisplay={entry.image.resolvedStyle.display}; imageTint={entry.image.tintColor}";
        }

        internal WhimShaderPreviewService(GraphData graph, GraphEditorView editor, VisualElement viewport,
            ShaderGraphPanelPlacement placement, Action savePlacement, Action changed)
        {
            this.graph = graph; this.editor = editor; this.viewport = viewport; this.changed = changed;
            try
            {
            sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(WhimShaderWindow.PackagePath + "/Editor/UI/Preview/WhimShaderPreview.uss");
            addedSheet = sheet != null && !viewport.styleSheets.Contains(sheet);
            if (addedSheet) viewport.styleSheets.Add(sheet);
            finalPanel = new WhimShaderFinalPreviewPanel(placement, graph.isSubGraph, SetFinalSettings, savePlacement);
            final = new Entry(this, graph.outputNode, true, finalPanel.Surface, finalPanel.Image);
            nativeMaster = editor.Q<MasterPreviewView>("masterPreview");
            if (nativeMaster != null) { nativeDisplay = nativeMaster.style.display; nativeMaster.style.display = DisplayStyle.None; }
            viewport.Add(finalPanel);
            presentation = WhimShaderNodePresentation.Attach(editor, PresentationChanged);
            presentation.PresentPreview = RequestNode;
            presentation.PreviewCreated += AddNode;
            presentation.PreviewRemoved += RemoveNode;
            presentation.Refresh();
            SynchronizePresentations();
            Undo.undoRedoPerformed += OnUndo;
            AssemblyReloadEvents.beforeAssemblyReload += Dispose;
            EditorApplication.quitting += Dispose;
            SynchronizeNodes();
            BindNative(final, editor.previewManager.masterRenderData);
            RefreshSettings();
            }
            catch { Dispose(); throw; }
        }

        void SetFinalSettings(WhimShaderNodePreviewSettings value)
        {
            WhimShaderNodePresentationMetadata.WriteFinal(graph, value);
            settingsDirty = true;
            changed?.Invoke();
        }

        void PresentationChanged() { settingsDirty = true; changed?.Invoke(); }
        void OnUndo()
        {
            propertiesDirty = topologyDirty = settingsDirty = true;
            InvalidateAll(true);
        }

        void SynchronizeNodes()
        {
            current.Clear();
            foreach (var node in graph.GetNodes<AbstractMaterialNode>())
            {
                current.Add(node);
                if (watched.Add(node)) { node.RegisterCallback(OnModified); topologyDirty = propertiesDirty = true; }
            }
            removed.Clear();
            foreach (var node in watched) if (!current.Contains(node)) removed.Add(node);
            foreach (var node in removed)
            {
                node.UnregisterCallback(OnModified); watched.Remove(node);
                if (entries.Remove(node, out var entry)) entry.Dispose();
                topologyDirty = propertiesDirty = true;
            }
            if (graph.addedEdges.Any() || graph.removedEdges.Any() || graph.addedInputs.Any() || graph.removedInputs.Any())
            {
                topologyDirty = propertiesDirty = settingsDirty = true;
                InvalidateAll(true);
            }
        }

        void OnModified(AbstractMaterialNode node, ModificationScope scope)
        {
            if (disposed) return;
            if (node is WhimShaderNodePresentationMetadata) { settingsDirty = true; return; }
            if (scope == ModificationScope.Layout) return;
            bool code = scope == ModificationScope.Graph || scope == ModificationScope.Topological;
            propertiesDirty = true;
            topologyDirty |= code;
            if (final.dependencies.Contains(node)) final.Invalidate(code);
            foreach (var entry in entries.Values)
                if (entry.dependencies.Contains(node)) entry.Invalidate(code);
        }

        void InvalidateAll(bool code)
        {
            final.Invalidate(code);
            foreach (var entry in entries.Values) entry.Invalidate(code);
        }

        void RequestNode(AbstractMaterialNode node, Image nativeImage, WhimShaderNodePreviewSettings settings)
        {
            if (disposed) return;
            if (!entries.TryGetValue(node, out var entry))
            {
                var context = presentation.Previews.FirstOrDefault(p => ReferenceEquals(p.Node, node));
                if (context == null) return;
                AddNode(context);
                entry = entries[node];
            }
            entry.SetSettings(settings);
            BindNative(entry, editor.previewManager.GetPreviewRenderData(node));
        }

        void AddNode(WhimShaderNodePreviewContext context)
        {
            if (disposed || context == null || context.Node?.owner != graph) return;
            if (entries.TryGetValue(context.Node, out var existing))
            {
                if (ReferenceEquals(existing.context, context)) return;
                existing.Dispose();
            }
            var entry = new Entry(this, context.Node, false, context.Surface, context.DisplayImage, context);
            entries[context.Node] = entry;
            entry.SetSettings(WhimShaderNodePresentationMetadata.Read(context.Node));
            BindNative(entry, editor.previewManager.GetPreviewRenderData(context.Node));
            topologyDirty = true;
        }

        void SynchronizePresentations()
        {
            // Attach creates existing contexts before subscribers can register; later contexts can
            // arrive from a scheduled refresh or a native view replacement. Reconcile both paths.
            foreach (var context in presentation.Previews)
            {
                AddNode(context);
                if (entries.TryGetValue(context.Node, out var entry))
                    BindNative(entry, editor.previewManager.GetPreviewRenderData(context.Node));
            }
        }

        void RemoveNode(WhimShaderNodePreviewContext context)
        {
            if (entries.TryGetValue(context.Node, out var entry) && ReferenceEquals(entry.context, context))
            { entries.Remove(context.Node); entry.Dispose(); }
        }

        void BindNative(Entry entry, PreviewRenderData native)
        {
            if (ReferenceEquals(native, entry.native)) return;
            if (entry.native != null) entry.native.onPreviewChanged -= entry.OnNativeChanged;
            entry.native = native;
            if (native != null) native.onPreviewChanged += entry.OnNativeChanged;
            entry.Invalidate(true);
        }

        void RefreshSettings()
        {
            settingsDirty = false;
            finalSlots.Clear();
            if (graph.isSubGraph && graph.outputNode != null) graph.outputNode.GetInputSlots(finalSlots);
            var state = WhimShaderNodePresentationMetadata.ReadFinal(graph);
            bool hasSavedFinal = graph.GetNodes<WhimShaderNodePresentationMetadata>().Any(m =>
                m.previews != null && m.previews.Any(e => e != null && e.nodeId == WhimShaderNodePresentationMetadata.FinalId));
            if (!hasSavedFinal && finalSlots.Count > 0) state.outputSlotId = finalSlots[0].id;
            final.SetSettings(state);
            finalPanel.SetState(final.settings, finalSlots);
            foreach (var entry in entries.Values) entry.SetSettings(WhimShaderNodePresentationMetadata.Read(entry.node));
        }

        void BuildDependencies(Entry entry)
        {
            entry.dependencies.Clear(); dependencyStack.Clear(); entry.timed = false;
            if (entry.isFinal && !graph.isSubGraph)
                foreach (var block in graph.GetNodes<BlockNode>()) dependencyStack.Push(block);
            else if (entry.node != null) dependencyStack.Push(entry.node);
            while (dependencyStack.Count > 0)
            {
                var node = dependencyStack.Pop();
                if (node == null || !entry.dependencies.Add(node)) continue;
                entry.timed |= node.RequiresTime();
                foreach (var slot in node.GetInputSlots<MaterialSlot>())
                    foreach (var edge in graph.GetEdges(slot.slotReference)) dependencyStack.Push(edge.outputSlot.node);
            }
        }

        /// <summary>Call after native HandleGraphChanges, before Graph.ClearChanges. Does not drive native rendering.</summary>
        internal void Update()
        {
            if (disposed || updating) return;
            updating = true;
            try
            {
                SynchronizeNodes();
                SynchronizePresentations();
                BindNative(final, editor.previewManager.masterRenderData);
                if (!ReferenceEquals(final.node, graph.outputNode))
                { final.node = graph.outputNode; final.Invalidate(true); topologyDirty = settingsDirty = true; }
                if (settingsDirty) RefreshSettings();
                if (topologyDirty)
                {
                    topologyDirty = false; BuildDependencies(final);
                    foreach (var entry in entries.Values) BuildDependencies(entry);
                }
                frameTime = Time.realtimeSinceStartup;
                if (!RefreshProperties()) return;
                finalPanel.style.display = editor.viewSettings.isPreviewVisible ? DisplayStyle.Flex : DisplayStyle.None;
                int buildBudget = 2; // Bound source generation/compile submissions per host update.
                UpdateEntry(final, ref buildBudget);
                foreach (var entry in entries.Values) UpdateEntry(entry, ref buildBudget);
            }
            finally { updating = false; }
        }

        bool RefreshProperties()
        {
            if (propertiesDirty)
            {
                propertiesDirty = false;
                try { properties.Rebuild(graph, temporaryBlocks); propertiesValid = true; }
                catch (Exception error)
                {
                    propertiesValid = false;
                    final.Fail(error.Message);
                    foreach (var entry in entries.Values) entry.Fail(error.Message);
                }
            }
            if (propertiesValid) properties.SetTime(frameTime);
            return propertiesValid;
        }

        void UpdateEntry(Entry entry, ref int buildBudget)
        {
            if (!entry.Visible) { entry.Message(null); return; }
            var data = entry.native?.shaderData;
            bool nativeDrives = NativeDrivesPreview(entry);
            if (data?.hasError == true) { entry.Fail("Native shader compilation failed."); return; }
            if (nativeDrives && (data == null || data.shader == null || data.mat == null))
            { entry.Pending(); return; }
            if (data != null && !ReferenceEquals(data.mat, entry.observedMaterial))
            { entry.observedMaterial = data.mat; entry.Invalidate(true); propertiesDirty = true; }
            if (nativeDrives && (data.isOutOfDate || data.passesCompiling > 0))
            { entry.Pending(); return; }

            try
            {
                if (entry.compiling && !FinishCompilation(entry)) return;
                if (entry.codeDirty)
                {
                    if (buildBudget == 0) { entry.Pending(); return; }
                    buildBudget--;
                    entry.codeDirty = false;
                    entry.error = null;
                    // Borrow the true URP material unless implicit/default Master Stack blocks need
                    // their private native MPB values. That case uses native generation + its public
                    // temporaryBlocks result, with our own matching shader/material and properties.
                    entry.ownsSource = NeedsOwnedSource(entry);
                    if (entry.ownsSource) BuildShader(entry);
                    else
                    {
                        entry.ReleaseShader();
                        if (entry.isFinal && temporaryBlocks != null) { temporaryBlocks = null; propertiesDirty = true; }
                    }
                }
                if (!string.IsNullOrEmpty(entry.error)) return;
                Material material = entry.ownsSource ? entry.material : data?.mat;
                if (material == null) { entry.Pending(); return; }
                if (entry.compiling && !FinishCompilation(entry)) return;
                if (!RefreshProperties()) return;
                if (entry.timed) entry.rawDirty = true;
                if (!entry.EnsureTextures()) { entry.Pending(); return; }
                if (entry.rawDirty)
                {
                    if (entry.ownsSource) properties.ApplyMaterialOnlyProperties(material);
                    renderer.RenderMaterial(material, properties.Block, entry.settings.mode, entry.raw, entry.isFinal && !graph.isSubGraph);
                    RawRenderCount++;
                    entry.rawDirty = false; entry.displayDirty = true;
                }
                if (entry.displayDirty)
                {
                    renderer.Render(entry.raw, entry.display, entry.settings.mode, entry.settings.EffectiveMask,
                        entry.settings.heightChannel, entry.settings.heightScale, entry.settings.heightTexture,
                        entry.settings.highlightOutOfRange, encodeGamma: entry.isFinal,
                        canvasSize: new Vector2Int(entry.raw.width, entry.raw.height));
                    DisplayRenderCount++; entry.displayDirty = false;
                    entry.image.image = entry.display; entry.image.tintColor = Color.white; entry.image.MarkDirtyRepaint();
                }
                entry.Message(null);
            }
            catch (Exception error) { entry.Fail(error.Message); }
        }

        bool FinishCompilation(Entry entry)
        {
            if (ShaderUtil.ShaderHasError(entry.shader))
            {
                entry.compiling = false;
                throw new InvalidOperationException(string.Join("\n", ShaderUtil.GetShaderMessages(entry.shader).Select(m => m.message)));
            }
            if (!ShaderUtil.IsPassCompiled(entry.material, entry.compilePass))
            { entry.Pending(); return false; }
            entry.compiling = false;
            entry.material.shader = entry.shader;
            return true;
        }

        bool NeedsOwnedSource(Entry entry)
        {
            if (entry.isFinal) return graph.isSubGraph || MissingMasterBlocks();
            if (!NativeDrivesPreview(entry)) return true;
            if (entry.node is SubGraphOutputNode) return true;
            var first = entry.node.GetOutputSlots<MaterialSlot>().FirstOrDefault();
            var selected = entry.node.FindOutputSlot<MaterialSlot>(entry.settings.outputSlotId);
            // Native source/material are already correct for the first scalar/vector2/vector3/bool.
            // Float4 always needs the alpha adapter, even when RGB is the selected display channel.
            return first == null || !WhimShaderPreviewSource.Supports(selected) || selected.id != first.id ||
                selected.concreteValueType == ConcreteSlotValueType.Vector4;
        }

        static bool NativeDrivesPreview(Entry entry)
        {
            if (entry.isFinal || entry.node != null && ReferenceEquals(entry.node, entry.node.owner?.outputNode)) return true;
            // SG 17.7 PreviewManager.KickOffShaderCompilations deliberately leaves data stale
            // for numeric nodes without native chrome (Split, Property, etc.) and bool outputs.
            // WhimTex chrome can still preview these through the same native Generator engine.
            return entry.node != null && (entry.node.hasPreview || entry.node.hasUserAuthoredCode) &&
                MaterialNodeView.IsPreviewable(entry.node);
        }

        bool MissingMasterBlocks()
        {
            var descriptors = graph.GetNodes<BlockNode>().Select(b => b.descriptor).ToList();
            foreach (var target in graph.activeTargets)
            {
                var context = new TargetActiveBlockContext(descriptors, null);
                target.GetActiveBlocks(ref context);
                foreach (var descriptor in context.activeBlocks)
                    if (!descriptors.Contains(descriptor)) return true;
            }
            return false;
        }

        void BuildShader(Entry entry)
        {
            var root = entry.isFinal ? graph.outputNode : entry.node;
            string source = entry.native?.shaderData?.shaderString;
            Generator generated = null;
            // Native 17.7 stores shaderString on master only. Node previews need a native
            // Generator on code changes, never a shader-source reconstruction or reflection.
            if (string.IsNullOrEmpty(source) || root == null)
            {
                generated = new Generator(graph, root, GenerationMode.Preview,
                    "Hidden/WhimShader/Presentation/" + (root?.objectId ?? "Final"), humanReadable: true);
                source = generated.generatedShader;
            }
            if (root != null)
            {
                MaterialSlot slot = root is SubGraphOutputNode
                    ? root.FindInputSlot<MaterialSlot>(entry.settings.outputSlotId)
                    : root.FindOutputSlot<MaterialSlot>(entry.settings.outputSlotId);
                if (!WhimShaderPreviewSource.Supports(slot))
                    throw new InvalidOperationException("Choose an existing numeric output for this preview.");
                source = WhimShaderPreviewSource.SelectOutput(source, root, slot.id);
            }
            if (entry.isFinal && root == null)
            { temporaryBlocks = generated.temporaryBlocks; propertiesDirty = true; }
            if (entry.source == source && entry.shader != null && entry.material != null && !ShaderUtil.ShaderHasError(entry.shader)) return;
            bool previousAsync = ShaderUtil.allowAsyncCompilation;
            try
            {
                ShaderUtil.allowAsyncCompilation = true;
                if (entry.shader == null)
                {
                    entry.shader = ShaderUtil.CreateShaderAsset(source, false);
                    entry.shader.hideFlags = HideFlags.HideAndDontSave;
                }
                else
                {
                    ShaderUtil.ClearCachedData(entry.shader); ShaderUtil.ClearShaderMessages(entry.shader);
                    ShaderUtil.UpdateShaderAsset(entry.shader, source, false);
                }
                if (entry.material == null) entry.material = new Material(entry.shader) { hideFlags = HideFlags.HideAndDontSave };
                else entry.material.shader = entry.shader;
                if (entry.native?.shaderData.mat != null) entry.material.CopyPropertiesFromMaterial(entry.native.shaderData.mat);
                if (entry.isFinal && !graph.isSubGraph)
                    foreach (var target in graph.activeTargets) if (target.IsActive()) target.ProcessPreviewMaterial(entry.material);
                if (entry.material.passCount == 0) throw new InvalidOperationException("Preview shader has no supported passes.");
                entry.compilePass = 0;
                if (entry.isFinal && !graph.isSubGraph)
                {
                    foreach (string name in new[] { "Whim Forward", "Universal Forward", "Universal Forward Only", "Universal2D" })
                    {
                        int pass = entry.material.FindPass(name);
                        if (pass >= 0) { entry.compilePass = pass; break; }
                    }
                }
                ShaderUtil.CompilePass(entry.material, entry.compilePass);
                entry.source = source; entry.compiling = true; ShaderBuildCount++;
            }
            finally { ShaderUtil.allowAsyncCompilation = previousAsync; }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Undo.undoRedoPerformed -= OnUndo;
            AssemblyReloadEvents.beforeAssemblyReload -= Dispose;
            EditorApplication.quitting -= Dispose;
            foreach (var node in watched) node.UnregisterCallback(OnModified);
            watched.Clear();
            if (presentation != null)
            {
                presentation.PresentPreview = null;
                presentation.PreviewCreated -= AddNode;
                presentation.PreviewRemoved -= RemoveNode;
            }
            foreach (var entry in entries.Values) entry.Dispose();
            entries.Clear(); final?.Dispose();
            presentation?.Dispose(); finalPanel?.Dispose(); renderer.Dispose();
            if (nativeMaster != null) nativeMaster.style.display = nativeDisplay;
            if (addedSheet) viewport.styleSheets.Remove(sheet);
            temporaryBlocks = null;
        }

        sealed class Entry : IDisposable
        {
            readonly WhimShaderPreviewService owner;
            internal AbstractMaterialNode node;
            internal readonly bool isFinal;
            internal readonly VisualElement surface;
            internal readonly Image image;
            internal readonly WhimShaderNodePreviewContext context;
            internal readonly HashSet<AbstractMaterialNode> dependencies = new();
            internal WhimShaderNodePreviewSettings settings;
            bool hasSettings;
            internal bool codeDirty = true, rawDirty = true, displayDirty = true, timed, compiling, ownsSource;
            internal int compilePass;
            internal PreviewRenderData native;
            internal Material observedMaterial, material;
            internal Shader shader;
            internal string source, error;
            internal RenderTexture raw, display;

            internal Entry(WhimShaderPreviewService owner, AbstractMaterialNode node, bool isFinal, VisualElement surface, Image image,
                WhimShaderNodePreviewContext context = null)
            {
                this.owner = owner; this.node = node; this.isFinal = isFinal; this.surface = surface; this.image = image;
                this.context = context;
            }

            internal bool Visible
            {
                get
                {
                    if (surface.panel == null || !surface.visible || surface.contentRect.width <= 0 || surface.contentRect.height <= 0 ||
                        !(isFinal ? owner.editor.viewSettings.isPreviewVisible : node.owner != null && node.previewExpanded)) return false;
                    for (var element = surface; element != null && element != owner.viewport; element = element.parent)
                        if (element.resolvedStyle.display == DisplayStyle.None) return false;
                    return surface.worldBound.Overlaps(owner.viewport.worldBound);
                }
            }

            internal void SetSettings(WhimShaderNodePreviewSettings value)
            {
                value.mode = (WhimShaderPreviewMode)Math.Clamp((int)value.mode, 0, 2);
                value.channel = (WhimShaderPreviewChannel)Math.Clamp((int)value.channel, 0, 4);
                value.heightChannel = (HeightPreviewRenderer.Channel)Math.Clamp((int)value.heightChannel, 0, 4);
                float scale = value.heightScale;
                value.heightScale = scale;
                if (!hasSettings || value.outputSlotId != settings.outputSlotId) Invalidate(true);
                if (!hasSettings || value.mode != settings.mode)
                {
                    rawDirty |= !hasSettings || (value.mode == WhimShaderPreviewMode.ThreeD) != (settings.mode == WhimShaderPreviewMode.ThreeD);
                    displayDirty = true;
                    if (error != null) Invalidate(true);
                }
                if (!hasSettings || value.EffectiveMask != settings.EffectiveMask || value.heightChannel != settings.heightChannel ||
                    value.heightScale != settings.heightScale || value.heightTexture != settings.heightTexture ||
                    value.highlightOutOfRange != settings.highlightOutOfRange)
                { displayDirty = true; if (error != null) Invalidate(true); }
                settings = value; hasSettings = true;
            }

            internal void Invalidate(bool code)
            { codeDirty |= code || error != null; rawDirty = displayDirty = true; error = null; }
            internal void OnNativeChanged() { rawDirty = true; }

            internal bool EnsureTextures()
            {
                float density = Mathf.Max(1, EditorGUIUtility.pixelsPerPoint);
                float width = isFinal ? surface.contentRect.width : 128;
                float height = isFinal ? surface.contentRect.height : 128;
                if (!ShaderGraphWorkspaceUI.Finite(width) || !ShaderGraphWorkspaceUI.Finite(height) || width <= 0 || height <= 0) return false;
                int w = Mathf.Clamp(Mathf.CeilToInt(width * density), 32, 1024);
                int h = Mathf.Clamp(Mathf.CeilToInt(height * density), 32, 1024);
                if (raw == null || raw.width != w || raw.height != h || !raw.IsCreated())
                {
                    ReleaseTexture(ref raw);
                    var format = SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBHalf) ? RenderTextureFormat.ARGBHalf : RenderTextureFormat.ARGBFloat;
                    raw = MakeTexture(w, h, format, RenderTextureReadWrite.Linear, "WhimShader raw preview");
                    rawDirty = displayDirty = true;
                }
                // WhimTex Height output is square and Image.ScaleToFit handles the panel aspect.
                // Switching Flat/Height must not release or rerender the raw material texture.
                if (settings.mode == WhimShaderPreviewMode.Height) w = h = Math.Max(w, h);
                if (display == null || display.width != w || display.height != h || !display.IsCreated())
                {
                    image.image = null;
                    owner.renderer.ReleaseTarget(display);
                    ReleaseTexture(ref display);
                    display = MakeTexture(w, h, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default, "WhimShader display preview");
                    displayDirty = true;
                }
                return true;
            }

            static RenderTexture MakeTexture(int width, int height, RenderTextureFormat format, RenderTextureReadWrite colorSpace, string name)
            {
                var texture = new RenderTexture(width, height, 0, format, colorSpace)
                { name = name, hideFlags = HideFlags.HideAndDontSave, useMipMap = false, autoGenerateMips = false,
                    filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, antiAliasing = 1 };
                if (texture.Create()) return texture;
                Object.DestroyImmediate(texture);
                throw new InvalidOperationException("Could not allocate an RGBA preview texture.");
            }

            internal void Pending() => Message(null, null, true);

            internal void Message(string text, string detail = null, bool pending = false)
            {
                if (isFinal) { owner.finalPanel.SetUpdating(pending); owner.finalPanel.SetMessage(text, detail); }
                else { context?.SetUpdating(pending); context?.SetMessage(text, detail); }
            }
            internal void Fail(string detail)
            {
                error = detail; image.image = null; image.MarkDirtyRepaint();
                Message("Preview unavailable", detail);
            }

            internal void ReleaseShader()
            {
                if (material != null) Object.DestroyImmediate(material);
                if (shader != null) { ShaderUtil.ClearShaderMessages(shader); Object.DestroyImmediate(shader); }
                material = null; shader = null; source = null; compiling = false;
            }
            void ReleaseTextures()
            {
                image.image = null;
                owner.renderer.ReleaseTarget(display);
                ReleaseTexture(ref raw); ReleaseTexture(ref display);
            }
            static void ReleaseTexture(ref RenderTexture texture)
            {
                if (texture != null) { texture.Release(); Object.DestroyImmediate(texture); }
                texture = null;
            }
            public void Dispose()
            {
                if (native != null) native.onPreviewChanged -= OnNativeChanged;
                ReleaseTextures(); ReleaseShader(); Message(null);
                native = null; dependencies.Clear();
            }
        }
    }
}
