// Visual hierarchy/port badges/preview chrome adapted from WhimTex ShaderGraphWindow.Nodes.cs,
// Branches.cs, Connections.cs and Preview.cs. Copyright (c) 2026 Mikhail.
// MIT; see ../Workspace/LICENSE.WhimTex.md. Native model/views remain owned by GraphEditorView.
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Experimental.GraphView;
using UnityEditor.Graphing;
using UnityEditor.ShaderGraph;
using UnityEditor.ShaderGraph.Drawing;
using UnityEditor.ShaderGraph.Internal;
using UnityEngine;
using UnityEngine.UIElements;
using Node = UnityEditor.Experimental.GraphView.Node;

namespace DCFApixels.WhimShader
{
    internal sealed class WhimShaderNodePreviewContext
    {
        internal AbstractMaterialNode Node { get; set; }
        internal Image NativeImage { get; set; }
        internal Image DisplayImage { get; set; }
        internal VisualElement Surface { get; set; }
        internal WhimShaderNodePreviewSettings Settings { get; set; }
        internal WhimShaderNodePreview View { get; set; }
        internal Action<string, string> Message { get; set; }
        internal void SetUpdating(bool value) => View?.SetUpdating(value);
        internal void SetMessage(string text, string detail = null) => Message?.Invoke(text, detail);
    }

    internal sealed class WhimShaderNodePresentation : IDisposable
    {
        const string Root = "Packages/com.dcfapixels.whimshader/Editor/UI/";
        readonly GraphEditorView editor;
        readonly MaterialGraphView graphView;
        readonly WhimShaderNodeBranches branches;
        readonly Action changed;
        readonly Dictionary<Node, Binding> bindings = new();
        readonly List<StyleSheet> addedSheets = new();
        readonly IVisualElementScheduledItem refreshTask;
        readonly bool addedGraphClass;
        bool disposed, queued, refreshing;
        WhimShaderPortReplacement portReplacement;

        internal string PortReplacementFailure => portReplacement?.LastFailure;
        // Subscribe before SetEdgeConnectorListener if a custom popup retains a ShaderPort reference.
        internal event Action<ShaderPort, ShaderPort> PortReplaced;
        // Configure during CreateGUI before graph interaction, while no native search popup is open.
        internal void SetEdgeConnectorListener(IEdgeConnectorListener listener)
        {
            if (ReferenceEquals(portReplacement?.Listener, listener) && listener != null) return;
            portReplacement = new WhimShaderPortReplacement(listener)
            { PortReplaced = (previous, current) => PortReplaced?.Invoke(previous, current) };
            Refresh();
        }

        // Receives the PreviewManager-owned source Image. Render and call SetPreview(node, texture).
        // Called on native preview changes, settings changes and Refresh.
        // Without a renderer the existing native texture is displayed; controls still persist their state.
        internal Action<AbstractMaterialNode, Image, WhimShaderNodePreviewSettings> PresentPreview { get; set; }
        internal event Action<WhimShaderNodePreviewContext> PreviewCreated;
        internal event Action<WhimShaderNodePreviewContext> PreviewRemoved;

        internal static WhimShaderNodePresentation Attach(GraphEditorView editor, Action changed = null) => new(editor, changed);
        internal static WhimShaderNodePresentation Apply(GraphEditorView editor, Action changed = null) => Attach(editor, changed);

        WhimShaderNodePresentation(GraphEditorView editor, Action changed)
        {
            this.editor = editor ?? throw new ArgumentNullException(nameof(editor));
            graphView = editor.graphView ?? throw new ArgumentException("Initialize GraphEditorView before attaching presentation.", nameof(editor));
            branches = new WhimShaderNodeBranches(graphView);
            this.changed = changed;
            addedGraphClass = !graphView.ClassListContains("whimshader-shader-graph");
            graphView.AddToClassList("whimshader-shader-graph");
            graphView.AddToClassList("whimshader-node-presentation");
            foreach (string path in new[] { Root + "WhimShaderWindow.uss", Root + "Preview/WhimShaderPreview.uss", Root + "Nodes/WhimShaderNodePresentation.uss" })
            {
                var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(path);
                if (sheet != null && !graphView.styleSheets.Contains(sheet)) { graphView.styleSheets.Add(sheet); addedSheets.Add(sheet); }
            }
            // Covers newly created/replaced views, including Undo and dynamic provider slots.
            refreshTask = graphView.schedule.Execute(Refresh).Every(100);
            Refresh();
        }

        internal void Refresh()
        {
            if (disposed || refreshing) return;
            refreshing = true;
            try
            {
                portReplacement?.BeginRefresh(graphView);
                var current = graphView.nodes.ToList().Where(view => view is IShaderNodeView shaderView && shaderView.node != null).ToList();
                foreach (var view in bindings.Keys.Where(view => !current.Contains(view) || !ReferenceEquals(bindings[view].Model, ((IShaderNodeView)view).node)).ToList())
                { bindings[view].Dispose(); bindings.Remove(view); }
                foreach (var view in current)
                {
                    if (!bindings.TryGetValue(view, out var binding)) bindings.Add(view, binding = new Binding(this, view));
                    binding.Refresh();
                }
                branches.Refresh();
            }
            finally { refreshing = false; }
        }

        void ScheduleRefresh()
        {
            if (disposed || queued) return;
            queued = true;
            graphView.schedule.Execute(() => { queued = false; Refresh(); });
        }

        internal WhimShaderNodePreviewSettings GetPreviewSettings(AbstractMaterialNode node) => WhimShaderNodePresentationMetadata.Read(node);
        internal void SetPreviewSettings(AbstractMaterialNode node, WhimShaderNodePreviewSettings settings)
        {
            WhimShaderNodePresentationMetadata.Write(node, settings);
            changed?.Invoke(); Refresh();
        }

        internal IEnumerable<WhimShaderNodePreviewContext> Previews => bindings.Values.Select(binding => binding.Preview).Where(context => context != null);

        // WhimTex ShaderGraphNodes.CanHaveNodePreview excludes Parameter/Split/Reroute and constants.
        // Native hasPreview supplies the corresponding eligibility for its larger node catalogue.
        internal static bool SupportsPreview(AbstractMaterialNode node) => node != null && node.hasPreview &&
            node is not (BlockNode or PropertyNode or SplitNode or WhimShaderParameterLayoutNode or WhimShaderNodePresentationMetadata) && PreviewSlots(node).Any();
        static IEnumerable<MaterialSlot> PreviewSlots(AbstractMaterialNode node) => node.GetOutputSlots<MaterialSlot>().Where(slot =>
            !slot.hidden && slot.concreteValueType is ConcreteSlotValueType.Vector1 or ConcreteSlotValueType.Vector2 or ConcreteSlotValueType.Vector3 or ConcreteSlotValueType.Vector4);

        internal bool TryGetPreviewImages(AbstractMaterialNode node, out Image nativeImage, out Image displayImage)
        {
            var preview = bindings.Values.FirstOrDefault(binding => ReferenceEquals(binding.Model, node))?.Preview;
            nativeImage = preview?.NativeImage; displayImage = preview?.DisplayImage;
            return preview != null;
        }

        internal void SetPreview(AbstractMaterialNode node, Texture texture)
        {
            if (!TryGetPreviewImages(node, out _, out var target)) return;
            target.image = texture; target.tintColor = texture == null ? Color.clear : Color.white;
            bindings.Values.FirstOrDefault(binding => ReferenceEquals(binding.Model, node))?.Preview?.View?.RefreshTint();
            target.MarkDirtyRepaint();
        }

        void OnPreviewCreated(WhimShaderNodePreviewContext preview) => PreviewCreated?.Invoke(preview);
        void OnPreviewRemoved(WhimShaderNodePreviewContext preview) => PreviewRemoved?.Invoke(preview);

        public void Dispose()
        {
            if (disposed) return;
            disposed = true; refreshTask.Pause();
            foreach (var binding in bindings.Values) binding.Dispose();
            bindings.Clear();
            branches.Dispose();
            foreach (var sheet in addedSheets) graphView.styleSheets.Remove(sheet);
            graphView.RemoveFromClassList("whimshader-node-presentation");
            if (addedGraphClass) graphView.RemoveFromClassList("whimshader-shader-graph");
        }

        // Records only presentation changes, so disposal never destroys native views or controls.
        sealed class Changes
        {
            readonly List<Action> undo = new();
            readonly HashSet<VisualElement> moved = new();
            internal void Record(Action action) => undo.Add(action);
            internal void Class(VisualElement element, string value)
            {
                if (element == null || element.ClassListContains(value)) return;
                element.AddToClassList(value); Record(() => element.RemoveFromClassList(value));
            }
            internal void Name(VisualElement element, string value)
            {
                string old = element.name; element.name = value; Record(() => element.name = old);
            }
            internal void Move(VisualElement element, VisualElement parent, int index = -1)
            {
                if (element == null || element.parent == parent) return;
                if (moved.Add(element))
                {
                    var original = element.parent; int position = original?.IndexOf(element) ?? 0;
                    Record(() => { if (original == null) element.RemoveFromHierarchy(); else original.Insert(Math.Min(position, original.childCount), element); });
                }
                if (index < 0) parent.Add(element); else parent.Insert(Math.Min(index, parent.childCount), element);
            }
            internal void Restore() { for (int i = undo.Count - 1; i >= 0; --i) undo[i](); undo.Clear(); }
        }

        sealed class Binding : IDisposable
        {
            readonly WhimShaderNodePresentation owner;
            readonly Node view;
            readonly MaterialNodeView material;
            readonly Changes changes = new();
            readonly Dictionary<ShaderPort, PortBinding> ports = new();
            readonly bool metadata, block, redirect;
            VisualElement nativeSurface;
            Image nativeImage, displayImage;
            WhimShaderNodePreview previewView;
            WhimShaderNodeMessageBadge messageBadge;
            WhimShaderFunctionEditor functionEditor;
            PreviewRenderData renderData;
            internal AbstractMaterialNode Model { get; }
            internal WhimShaderNodePreviewContext Preview { get; private set; }

            internal Binding(WhimShaderNodePresentation owner, Node view)
            {
                this.owner = owner; this.view = view; Model = ((IShaderNodeView)view).node;
                material = view as MaterialNodeView;
                metadata = Model is WhimShaderParameterLayoutNode or WhimShaderNodePresentationMetadata;
                block = Model is BlockNode; redirect = view is RedirectNodeView;
                if (metadata) { changes.Class(view, "whimshader-presentation-hidden"); return; }
                if (!block) changes.Class(view, "whimshader-shader-graph-node");
                if (redirect) changes.Class(view, "whimshader-shader-graph-reroute");
                if (view is PropertyNodeView)
                {
                    changes.Class(view, "whimshader-shader-graph-parameter-node");
                    changes.Class(view.Q("icon"), "whimshader-presentation-hidden");
                }
                var overflow = view.mainContainer.style.overflow;
                view.mainContainer.style.overflow = StyleKeyword.Null;
                changes.Record(() => view.mainContainer.style.overflow = overflow);
                var border = view.Q("selection-border");
                if (border != null)
                {
                    var parent = border.parent; int index = parent.IndexOf(border);
                    border.SendToBack(); changes.Record(() => parent.Insert(Math.Min(index, parent.childCount), border));
                }
                if (material != null && !block)
                {
                    // Inherit the same GraphView Node styling used by WhimTex; discard only the native
                    // MaterialNodeView skin (200px floating preview, thick title strip, zero-size extension).
                    var skin = Resources.Load<StyleSheet>("Styles/MaterialNodeView");
                    if (skin != null && view.styleSheets.Contains(skin))
                    { view.styleSheets.Remove(skin); changes.Record(() => view.styleSheets.Add(skin)); }
                    foreach (string name in new[] { "controls", "dropdowns" })
                    {
                        var container = view.Q(name);
                        if (container == null) continue;
                        changes.Move(container, view.extensionContainer);
                        changes.Class(container, "whimshader-node-options");
                        changes.Class(container.Q("divider"), "whimshader-presentation-hidden");
                        foreach (var field in container.Query<VisualElement>(className: BaseField<float>.ussClassName).ToList())
                            changes.Class(field, "whimshader-shader-graph-node-field");
                    }
                    changes.Class(view.Q("previewFiller"), "whimshader-presentation-hidden");
                    view.RefreshExpandedState();
                }
                if (Model is WhimShaderCustomFunctionNode function)
                {
                    functionEditor = new WhimShaderFunctionEditor(function, view, owner.changed);
                    view.extensionContainer.Add(functionEditor);
                    view.RefreshExpandedState();
                }
                Model.RegisterCallback(OnModified);
            }

            void OnModified(AbstractMaterialNode _, ModificationScope scope) => owner.ScheduleRefresh();

            internal void Refresh()
            {
                functionEditor?.Refresh();
                if (metadata) return;
                owner.portReplacement?.Apply((IShaderNodeView)view, old =>
                {
                    if (!ports.TryGetValue(old, out var previous)) return;
                    previous.Dispose(); ports.Remove(old);
                });
                var current = view.Query<ShaderPort>().ToList();
                foreach (var port in ports.Keys.Where(port => !current.Contains(port)).ToList())
                { ports[port].Dispose(); ports.Remove(port); }
                foreach (var port in current)
                {
                    if (!ports.TryGetValue(port, out var binding)) ports.Add(port, binding = new PortBinding(owner.branches, port, redirect));
                    binding.Refresh();
                }
                // Native PreviewManager remains the texture source. Its container may be absent on
                // initial collapse; the copied WhimTex UI must not depend on that hierarchy existing.
                var currentSurface = view.Q("previewContainer");
                if (currentSurface != null && currentSurface != nativeSurface)
                {
                    nativeSurface = currentSurface; nativeImage = nativeSurface.Q<Image>("preview");
                    changes.Class(nativeSurface, "whimshader-presentation-hidden");
                    if (Preview != null) Preview.NativeImage = nativeImage;
                }
                bool supported = SupportsPreview(Model) && !redirect;
                if (supported && previewView == null) BuildPreview();
                if (previewView == null) return;
                previewView.style.display = supported ? DisplayStyle.Flex : DisplayStyle.None;
                bool expanded = supported && Model.previewExpanded;
                view.EnableInClassList("whimshader-shader-graph-node--preview", expanded);
                previewView.SetExpanded(expanded);
                previewView.SetState(owner.GetPreviewSettings(Model), PreviewSlots(Model));
                Render();
            }

            void BuildPreview()
            {
                previewView = new WhimShaderNodePreview(ChangeExpanded, value => owner.SetPreviewSettings(Model, value)) { userData = Model.objectId };
                view.extensionContainer.Add(previewView); changes.Record(previewView.RemoveFromHierarchy);
                view.RefreshExpandedState();
                displayImage = previewView.Image;
                Preview = new WhimShaderNodePreviewContext
                {
                    Node = Model, NativeImage = nativeImage, DisplayImage = displayImage, Surface = previewView.Surface,
                    Settings = owner.GetPreviewSettings(Model), View = previewView, Message = SetMessage
                };
                renderData = owner.editor.previewManager.GetPreviewRenderData(Model);
                if (renderData != null) renderData.onPreviewChanged += Render;
                owner.OnPreviewCreated(Preview);
            }

            void ChangeExpanded(bool value)
            {
                if (Model.owner?.owner == null || Model.previewExpanded == value) return;
                Model.owner.owner.RegisterCompleteObjectUndo("Toggle Node Preview");
                Model.previewExpanded = value; Model.owner.owner.isDirty = true;
                owner.changed?.Invoke(); owner.Refresh();
            }

            void SetMessage(string text, string detail)
            {
                bool error = !string.IsNullOrEmpty(text);
                view.EnableInClassList("whimshader-shader-graph-node--error", error);
                if (!error) { messageBadge?.RemoveFromHierarchy(); messageBadge = null; return; }
                if (messageBadge == null) { messageBadge = new WhimShaderNodeMessageBadge(); view.hierarchy.Add(messageBadge); }
                messageBadge.SetMessage(string.IsNullOrEmpty(detail) ? text : text + "\n\n" + detail, HelpBoxMessageType.Error);
            }

            void Render()
            {
                if (Preview == null || Model.owner == null) return;
                Preview.Settings = owner.GetPreviewSettings(Model);
                if (owner.PresentPreview != null) owner.PresentPreview(Model, nativeImage, Preview.Settings);
                else
                {
                    displayImage.image = nativeImage != null ? nativeImage.image : renderData?.texture;
                    displayImage.tintColor = nativeImage != null ? nativeImage.tintColor : Color.white;
                    displayImage.MarkDirtyRepaint();
                }
            }

            public void Dispose()
            {
                if (!metadata) Model.UnregisterCallback(OnModified);
                if (renderData != null) renderData.onPreviewChanged -= Render;
                if (Preview != null) owner.OnPreviewRemoved(Preview);
                previewView?.Dispose(); messageBadge?.RemoveFromHierarchy();
                functionEditor?.Dispose();
                if (Preview != null) { Preview.View = null; Preview.Message = null; }
                view.RemoveFromClassList("whimshader-shader-graph-node--error");
                foreach (var port in ports.Values) port.Dispose(); ports.Clear();
                view.RemoveFromClassList("whimshader-shader-graph-node--preview");
                // Removed native views have already been disposed; don't reinsert their old controls.
                if (((IShaderNodeView)view).node != null) changes.Restore();
            }
        }

        sealed class PortBinding : IDisposable
        {
            readonly ShaderPort port;
            readonly Changes changes = new();
            readonly VisualElement connector, labelArea;
            readonly Label label, badge;
            readonly WhimShaderNodeBranches.PortBranchLabel branch;
            readonly bool redirect;
            PortInputView input;
            VisualElement stub;
            bool hovered;
            readonly Color originalColor;

            internal PortBinding(WhimShaderNodeBranches branches, ShaderPort port, bool redirect)
            {
                this.port = port; this.redirect = redirect; originalColor = port.portColor;
                changes.Class(port, "whimshader-shader-graph-port");
                label = port.Q<Label>("type"); connector = port.Q("connector");
                if (label != null && !redirect)
                {
                    labelArea = new VisualElement { name = "port-label", pickingMode = PickingMode.Ignore };
                    labelArea.AddToClassList("whimshader-shader-graph-port-label");
                    port.Insert(port.IndexOf(label), labelArea); changes.Record(labelArea.RemoveFromHierarchy);
                    changes.Move(label, labelArea);
                    badge = new Label { name = "port-metadata", pickingMode = PickingMode.Ignore, enableRichText = false };
                    badge.AddToClassList("whimshader-shader-graph-port-metadata"); labelArea.Add(badge);
                    branch = new WhimShaderNodeBranches.PortBranchLabel(branches, port, label, labelArea);
                }
                else if (label != null) changes.Class(label, "whimshader-presentation-hidden");
                port.RegisterCallback<PointerEnterEvent>(Enter); port.RegisterCallback<PointerMoveEvent>(Move);
                port.RegisterCallback<PointerLeaveEvent>(Leave);
                if (connector != null) connector.generateVisualContent += DrawHover;
            }

            internal void Refresh()
            {
                if (port.slot == null) return;
                var type = port.slot.concreteValueType;
                bool boolean = type is ConcreteSlotValueType.Boolean or ConcreteSlotValueType.PropertyConnectionState;
                bool numeric = type is ConcreteSlotValueType.Vector1 or ConcreteSlotValueType.Vector2 or ConcreteSlotValueType.Vector3 or ConcreteSlotValueType.Vector4;
                port.EnableInClassList("whimshader-shader-graph-port--boolean", boolean);
                port.EnableInClassList("whimshader-shader-graph-port--resource", !boolean && !numeric);
                port.portColor = WhimShaderNodeDrawing.PortColor(type);
                if (label != null && !redirect)
                {
                    label.text = port.slot.RawDisplayName();
                    badge.text = port.slot is ColorRGBMaterialSlot or ColorRGBAMaterialSlot || port.slot.owner is ColorNode ||
                        port.slot.owner is PropertyNode property && property.property is ColorShaderProperty ? "col" : WhimShaderNodeDrawing.PortTypeLabel(type);
                }
                connector?.MarkDirtyRepaint();
                if (port.direction != Direction.Input || redirect) return;
                // Keep native parent -> PortInputView + ShaderPort contract exactly intact.
                changes.Class(port.parent, "whimshader-shader-graph-port-row");
                var current = port.parent.Q<PortInputView>();
                if (current != input)
                {
                    input = current;
                    if (input != null)
                    {
                        changes.Class(input, "whimshader-shader-graph-port-value");
                        changes.Class(input.Q("container"), "whimshader-shader-graph-value-fields");
                        changes.Class(input.Q("slot"), "whimshader-presentation-hidden");
                        foreach (var edge in input.Query<EdgeControl>().ToList()) changes.Class(edge, "whimshader-presentation-hidden");
                        stub = new VisualElement { pickingMode = PickingMode.Ignore };
                        stub.AddToClassList("whimshader-shader-graph-value-stub"); input.Insert(0, stub);
                        var retained = stub; changes.Record(retained.RemoveFromHierarchy);
                    }
                }
                if (stub != null) { var tint = port.portColor; tint.a = .45f; stub.style.backgroundColor = tint; }
                if (input != null)
                {
                    var components = input.Query<VisualElement>("dummy").ToList();
                    for (int i = 1; i < components.Count; ++i) changes.Class(components[i], "whimshader-value-component-following");
                }
            }

            void Enter(PointerEnterEvent evt) { hovered = port.ContainsPoint(port.WorldToLocal(evt.position)); connector?.MarkDirtyRepaint(); }
            void Move(PointerMoveEvent evt) { hovered = port.ContainsPoint(port.WorldToLocal(evt.position)); connector?.MarkDirtyRepaint(); }
            void Leave(PointerLeaveEvent evt) { hovered = false; connector?.MarkDirtyRepaint(); }
            void DrawHover(MeshGenerationContext context) { if (hovered && port.connected) WhimShaderNodeDrawing.DrawConnectorHover(context); }
            public void Dispose()
            {
                branch?.Dispose();
                port.UnregisterCallback<PointerEnterEvent>(Enter); port.UnregisterCallback<PointerMoveEvent>(Move); port.UnregisterCallback<PointerLeaveEvent>(Leave);
                if (connector != null) connector.generateVisualContent -= DrawHover;
                port.RemoveFromClassList("whimshader-shader-graph-port--boolean"); port.RemoveFromClassList("whimshader-shader-graph-port--resource");
                if (port.slot != null) { port.portColor = originalColor; if (label != null) label.text = port.slot.displayName; }
                changes.Restore();
            }
        }
    }
}
