using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Graphing;
using UnityEditor.ShaderGraph;
using UnityEditor.ShaderGraph.Drawing;
using UnityEditor.ShaderGraph.Drawing.Inspector.PropertyDrawers;
using UnityEditor.ShaderGraph.Internal;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace DCFApixels.WhimShader
{
    internal sealed partial class WhimShaderParametersPanel : VisualElement, IDisposable
    {
        readonly GraphData graph;
        readonly MaterialGraphView graphView;
        readonly Action changed;
        readonly ScrollView definitions = new(ScrollViewMode.Vertical);
        readonly ScrollView inspectorRoot = new(ScrollViewMode.Vertical);
        readonly VisualElement inspector = new();
        readonly Foldout selectedNodeInspector = new() { text = "Selected Node", value = true };
        readonly List<Action> refreshValues = new();
        readonly Dictionary<string, bool> definitionExpanded = new();
        readonly Dictionary<string, bool> foldoutExpanded = new();
        readonly Dictionary<string, string> activeTabs = new();
        readonly List<IPropertyDrawer> nativeDrawers = new();
        readonly List<ShaderInput> renderedInputs = new();
        // Extensions opt in only after their ToConcreteNode implementation preserves their authored default.
        internal static Func<AbstractShaderProperty, bool> CanConvertExtensionToConcrete { get; set; }
        WhimShaderParameterLayout layout;
        string signature, selectedId;
        bool disposed, queued, rebuilding;
        IVisualElementScheduledItem refreshTask;
        internal VisualElement Inspector => inspectorRoot;

        internal WhimShaderParametersPanel(GraphData graph, MaterialGraphView graphView, Action changed)
        {
            this.graph = graph ?? throw new ArgumentNullException(nameof(graph));
            this.graphView = graphView ?? throw new ArgumentNullException(nameof(graphView));
            this.changed = changed ?? (() => { });
            name = "whimshader-parameters";
            AddToClassList("whimshader-parameters-panel");
            inspectorRoot.AddToClassList("whimshader-parameters-panel"); inspectorRoot.name = "whimshader-parameter-inspector";
            inspectorRoot.Add(inspector);
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>("Packages/com.dcfapixels.whimshader/Editor/UI/Parameters/WhimShaderParameters.uss");
            if (sheet != null) { styleSheets.Add(sheet); inspectorRoot.styleSheets.Add(sheet); graphView.styleSheets.Add(sheet); }
            var toolbar = new UnityEditor.UIElements.Toolbar();
            toolbar.AddToClassList("whimshader-shader-graph-parameter-toolbar");
            var add = new ToolbarMenu { name = "graph-add-parameter" };
            add.AddToClassList("whimshader-layer-menu-button");
            add.AddToClassList("whimshader-shader-graph-parameter-add");
            add.EnableInClassList("whimshader-layer-menu-button--light", !EditorGUIUtility.isProSkin);
            add.Add(new LayerActionIcon(LayerActionIcon.Kind.Add));
            BuildAddMenu(add.menu); toolbar.Add(add);
            Add(toolbar); Add(definitions);
            definitions.AddToClassList("whimshader-parameter-scroll");
            graphView.RegisterCallback<DragUpdatedEvent>(GraphDragUpdated, TrickleDown.TrickleDown);
            graphView.RegisterCallback<DragPerformEvent>(GraphDragPerform, TrickleDown.TrickleDown);
            refreshTask = schedule.Execute(Refresh).Every(200);
            Refresh();
        }

        string Fingerprint()
        {
            var result = new StringBuilder();
            foreach (var input in AllInputs()) result.Append(input.Serialize());
            var metadata = WhimShaderParameterLayoutNode.Find(graph);
            result.Append(metadata == null ? string.Empty : JsonUtility.ToJson(metadata.layout));
            if (metadata == null) foreach (var category in graph.categories)
            {
                result.Append(category.objectId).Append(category.name);
                foreach (var input in category.Children) result.Append(input.objectId);
            }
            return result.ToString();
        }

        bool HasEditingFocus()
        {
            var focused = panel?.focusController?.focusedElement as VisualElement;
            // Text/number drags retain focus; avoid replacing their element during an edit.
            return focused != null && (Contains(focused) || inspector.Contains(focused));
        }

        internal void Refresh()
        {
            if (disposed || rebuilding) return;
            RefreshNodeHoverBindings();
            var current = AllInputs().ToList();
            bool replaced = current.Count != renderedInputs.Count;
            if (!replaced) for (int i = 0; i < current.Count; i++) if (!ReferenceEquals(current[i], renderedInputs[i])) { replaced = true; break; }
            string next = Fingerprint();
            if (!replaced && signature == next) { RefreshValues(); return; }
            if (!replaced && HasEditingFocus()) { RefreshValues(); return; }
            Rebuild(current, next);
        }

        void Rebuild(List<ShaderInput> inputs, string next)
        {
            rebuilding = true;
            try
            {
                DisposeDrawers(); refreshValues.Clear();
                definitions.Clear(); inspector.Clear();
                layout = WhimShaderParameterLayoutNode.Find(graph)?.layout?.Copy() ?? WhimShaderParameterLayout.FromGraph(graph);
                layout.Normalize(graph);
                renderedInputs.Clear(); renderedInputs.AddRange(inputs);
                signature = next;
                if (!layout.Supported)
                {
                    var message = "This parameter layout uses an unsupported format version. Editing is disabled.";
                    definitions.Add(new HelpBox(message, HelpBoxMessageType.Error));
                    inspector.Add(new HelpBox(message, HelpBoxMessageType.Error));
                    return;
                }
                BuildDefinitions(); BuildNativeInputDefinitions(); BuildInspector();
                definitions.SetEnabled(graph.owner != null); inspector.SetEnabled(graph.owner != null);
            }
            finally { rebuilding = false; }
        }

        void RefreshValues() { foreach (var refresh in refreshValues) refresh(); }
        void QueueRebuild()
        {
            if (disposed || queued) return;
            queued = true;
            schedule.Execute(() =>
            {
                queued = false;
                if (!disposed) Rebuild(AllInputs().ToList(), Fingerprint());
            });
        }

        AbstractShaderProperty Property(string id) => graph.properties.FirstOrDefault(p => p.objectId == id);

        WhimShaderParameterLayout WritableLayout()
        {
            var node = WhimShaderParameterLayoutNode.Find(graph);
            if (node == null)
            {
                // Retain the IDs shown before the first edit, so the first move/rename resolves its target.
                node = new WhimShaderParameterLayoutNode { layout = layout?.Copy() ?? WhimShaderParameterLayout.FromGraph(graph) };
                graph.AddNode(node);
            }
            node.layout ??= layout?.Copy() ?? WhimShaderParameterLayout.FromGraph(graph);
            if (!node.layout.Supported) throw new InvalidOperationException("Unsupported parameter layout version.");
            node.layout.Normalize(graph);
            return node.layout;
        }

        void EditLayout(string label, Action<WhimShaderParameterLayout> edit)
        {
            if (disposed || graph.owner == null || layout?.Supported == false) return;
            graph.owner.RegisterCompleteObjectUndo(label);
            var model = WritableLayout(); edit(model); model.Normalize(graph);
            WhimShaderParameterLayoutNode.Find(graph).Dirty(ModificationScope.Layout);
            changed(); QueueRebuild();
        }

        void EditValue(AbstractShaderProperty property, string label, Action edit)
        {
            if (disposed || graph.owner == null || !ReferenceEquals(Property(property.objectId), property)) return;
            graph.owner.RegisterCompleteObjectUndo(label); edit();
            NotifyProperty(property, ModificationScope.Graph);
            signature = Fingerprint(); RefreshValues(); changed();
        }

        void NotifyProperty(AbstractShaderProperty property, ModificationScope scope)
        {
            foreach (var observer in property.InputObservers.ToArray()) observer.OnShaderInputUpdated(scope);
            foreach (var node in graph.GetNodes<PropertyNode>().Where(n => n.property == property))
            { node.UpdateNodeDisplayName(property.displayName); node.Dirty(scope); }
            foreach (var node in graph.GetNodes<SubGraphNode>()) { node.ValidateNode(); node.Dirty(scope); }
            graph.ValidateGraph();
            if (graph.owner != null) graph.owner.isDirty = true;
        }

        VisualElement NativeSettings(ShaderInput property)
        {
            var drawer = new ShaderInputPropertyDrawer(); nativeDrawers.Add(drawer);
            drawer.inspectorUpdateDelegate = QueueRebuild;
            var model = new ShaderInputViewModel
            {
                model = property, parentView = this, isSubGraph = graph.isSubGraph,
                isInputExposed = graph.isSubGraph || property.isExposed,
                inputName = property.displayName, inputTypeName = InputTypeName(property),
                requestModelChangeAction = action => graph.owner.graphDataStore.Dispatch(action)
            };
            drawer.GetViewModel(model, graph, (refresh, scope) =>
            {
                NotifyInput(property, scope); changed();
                if (refresh) QueueRebuild();
                else { signature = Fingerprint(); RefreshValues(); }
            });
            var content = drawer.DrawProperty(null, property, null);
            content.AddToClassList("whimshader-parameter-native-settings");
            return content;
        }

        VisualElement ValueField(AbstractShaderProperty property, string label)
        {
            if (property is IShaderPropertyDrawer extension)
            {
                // Native extensions (including LUT properties) take priority over their Texture2D base class.
                var root = new VisualElement();
                if (!string.IsNullOrEmpty(label) && label != "Default") root.Add(new Label(label));
                var content = new VisualElement(); root.Add(content);
                string previous = null;
                void Sync()
                {
                    string next = property.Serialize();
                    var focused = content.panel?.focusController?.focusedElement as VisualElement;
                    if (previous == next || focused != null && content.Contains(focused)) return;
                    previous = next; content.Clear(); var sheet = new PropertySheet(); content.Add(sheet);
                    extension.HandlePropertyField(sheet, undo => graph.owner.RegisterCompleteObjectUndo(undo), (rebuild, scope) =>
                    {
                        NotifyProperty(property, scope); changed();
                        if (rebuild) QueueRebuild();
                        else { previous = property.Serialize(); signature = Fingerprint(); RefreshValues(); }
                    });
                }
                Sync(); refreshValues.Add(Sync); return root;
            }
            return WhimShaderPropertyFields.Create(property, label, (undo, edit) => EditValue(property, undo, edit), refreshValues) ?? NativeSettings(property);
        }

        void BuildAddMenu(DropdownMenu menu)
        {
            var catalog = graphView.GetFirstAncestorOfType<GraphEditorView>()?.blackboardController?.ViewModel.propertyNameToAddActionMap;
            if (catalog != null) foreach (var entry in catalog)
            {
                if (entry.Value is not AddShaderInputAction) continue;
                string key = entry.Key;
                menu.AppendAction("Parameter/" + key, _ =>
                {
                    var current = graphView.GetFirstAncestorOfType<GraphEditorView>()?.blackboardController?.ViewModel.propertyNameToAddActionMap;
                    if (current != null && current.TryGetValue(key, out var action) && action is AddShaderInputAction add) AddProperty(add);
                }, _ => CanEdit ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
            }
            else AddFallbackPropertyMenu(menu);
            if (catalog != null) AddNativeInputMenu(menu);
            foreach (var kind in new[] { WhimShaderParameterItemKind.Group, WhimShaderParameterItemKind.Foldout, WhimShaderParameterItemKind.Tabs, WhimShaderParameterItemKind.Tab })
            {
                var retained = kind;
                menu.AppendAction(kind.ToString(), _ => AddGroup(retained), _ => CanEdit &&
                    (retained != WhimShaderParameterItemKind.Tab || layout?.Find(selectedId)?.kind == WhimShaderParameterItemKind.Tabs)
                    ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
            }
        }

        bool CanEdit => !disposed && graph.owner != null && layout?.Supported != false;
        string InsertionParent(WhimShaderParameterItemKind kind)
        {
            var selected = layout?.Find(selectedId);
            string parent = selected != null && layout.CanParent(kind, selected.id) ? selected.id : selected?.parentId;
            return layout != null && layout.CanParent(kind, parent) ? parent : string.Empty;
        }

        void AddProperty(AddShaderInputAction action)
        {
            if (!CanEdit) return;
            string parent = InsertionParent(WhimShaderParameterItemKind.Parameter);
            // Native creation dispatch keeps the native blackboard's observers in sync.
            Undo.IncrementCurrentGroup(); int undo = Undo.GetCurrentGroup();
            graph.owner.RegisterCompleteObjectUndo("Add Shader Parameter");
            if (!graph.categories.Any()) graph.AddCategory(CategoryData.DefaultCategory());
            graph.owner.graphDataStore.Dispatch(action);
            if (action.shaderInputReference is AbstractShaderProperty property)
            {
                var model = WritableLayout();
                var item = model.items.First(x => x.propertyId == property.objectId);
                if (model.CanParent(item.kind, parent)) model.Move(item.id, parent);
                selectedId = item.id; definitionExpanded[item.id] = true;
                NotifyProperty(property, ModificationScope.Graph);
            }
            else if (action.shaderInputReference is ShaderInput input)
            {
                selectedId = NativeInputId(input); definitionExpanded[selectedId] = true;
                NotifyInput(input, ModificationScope.Topological);
            }
            Undo.CollapseUndoOperations(undo); changed(); QueueRebuild();
        }

        void AddGroup(WhimShaderParameterItemKind kind)
        {
            string parent = InsertionParent(kind);
            if (!CanEdit || !layout.CanParent(kind, parent)) return;
            EditLayout("Add Parameter Group", model =>
            {
                var group = model.Add(kind, parent, kind.ToString()); selectedId = group.id;
                if (kind == WhimShaderParameterItemKind.Tabs)
                { var tab = model.Add(WhimShaderParameterItemKind.Tab, group.id, "Tab 1"); group.defaultTabId = tab.id; }
            });
        }

        void DeleteProperty(string id)
        {
            var property = Property(id); if (!CanEdit || property == null) return;
            var usages = graph.GetNodes<PropertyNode>().Where(n => n.property == property).ToList();
            if (usages.Count > 0 && property is IShaderPropertyDrawer && CanConvertExtensionToConcrete?.Invoke(property) != true)
            {
                EditorUtility.DisplayDialog("Cannot Delete Parameter", "This custom parameter has not registered a lossless concrete-node conversion. Disconnect or replace its usages before deleting it.", "OK");
                return;
            }
            foreach (var usage in usages)
            {
                var concrete = property.ToConcreteNode();
                bool supported = concrete != null && concrete.GetOutputSlots<MaterialSlot>().Any(s => s.valueType == usage.FindOutputSlot<MaterialSlot>(PropertyNode.OutputSlotId)?.valueType);
                concrete?.Dispose();
                if (!supported)
                {
                    EditorUtility.DisplayDialog("Cannot Delete Parameter", "This parameter is in use and has no compatible concrete node. Disconnect or replace its usages before deleting it.", "OK");
                    return;
                }
            }
            if (usages.Count > 0 && !EditorUtility.DisplayDialog("Delete Used Parameter",
                $"Delete '{property.displayName}'? Its {usages.Count} property node(s) will become concrete nodes using the current default value. Connections will be preserved.", "Convert and Delete", "Cancel")) return;
            graph.owner.RegisterCompleteObjectUndo("Delete Shader Parameter");
            graph.owner.graphDataStore.Dispatch(new DeleteShaderInputAction { shaderInputsToDelete = new List<ShaderInput> { property } });
            WritableLayout().Normalize(graph);
            changed(); QueueRebuild();
        }

        void DisposeDrawers() { foreach (var drawer in nativeDrawers) drawer.DisposePropertyDrawer(); nativeDrawers.Clear(); }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true; refreshTask?.Pause(); DisposeDrawers(); DisposeNodeHoverBindings();
            graphView.UnregisterCallback<DragUpdatedEvent>(GraphDragUpdated, TrickleDown.TrickleDown);
            graphView.UnregisterCallback<DragPerformEvent>(GraphDragPerform, TrickleDown.TrickleDown);
        }
    }
}
