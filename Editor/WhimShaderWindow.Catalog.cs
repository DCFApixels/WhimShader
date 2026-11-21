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

namespace DCFApixels.WhimShader
{
    public sealed partial class WhimShaderWindow
    {
        SearcherProvider catalogProvider;
        bool catalogCompatibilityFlag => false;
        bool Editable => Graph != null && graphObject != null;
        sealed class CatalogChoice
        {
            internal NodeEntry entry;
            internal string aliases => string.Join(" ", entry.node.synonyms ?? Array.Empty<string>());
        }

        VisualElement BuildCatalog()
        {
            catalogProvider?.Dispose();
            catalogProvider = new SearcherProvider();
            catalogProvider.Initialize(this, Graph, editor.graphView);
            nodePalette = new NodePalette(this); nodePalette.RefreshIfNeeded();
            editor.graphView.nodeCreationRequest = request => OpenCreateNode(request);
            editor.graphView.RegisterCallback<DragUpdatedEvent>(evt =>
            {
                if (!ReadNodePaletteDrag(out _)) return;
                DragAndDrop.visualMode = DragAndDropVisualMode.Copy; evt.StopPropagation();
            });
            editor.graphView.RegisterCallback<DragPerformEvent>(evt =>
            {
                if (!ReadNodePaletteDrag(out var choice)) return;
                CreateNodeChoice(choice, editor.graphView.contentViewContainer.WorldToLocal(evt.mousePosition));
                DragAndDrop.AcceptDrag(); DragAndDrop.SetGenericData(NodePaletteDragKey, null); evt.StopPropagation();
            });
            return nodePalette;
        }

        List<SearchTreeEntry> CreateNodeSearchTree()
        {
            catalogProvider.connectedPort = null; catalogProvider.target = null;
            catalogProvider.GenerateNodeEntries();
            return ToSearchTree(catalogProvider.currentNodeEntries);
        }

        List<SearchTreeEntry> ToSearchTree(IEnumerable<NodeEntry> sourceEntries)
        {
            var leaves = sourceEntries.Where(e => e.node.GetType() != typeof(CustomFunctionNode) && (!Graph.activeTargets.Any() ||
                Graph.activeTargets.Any(t => t.IsNodeAllowedByTarget(e.node.GetType()))))
                .Select(e => (entry: e, path: CatalogPath(e))).OrderBy(e => RootOrder(e.path[0]))
                .ThenBy(e => string.Join("/", e.path), StringComparer.OrdinalIgnoreCase).ToArray();
            var result = new List<SearchTreeEntry> { new SearchTreeGroupEntry(new GUIContent("Create Node"), 0) };
            var previous = Array.Empty<string>();
            foreach (var leaf in leaves)
            {
                int common = 0;
                while (common < previous.Length && common < leaf.path.Length - 1 && previous[common] == leaf.path[common]) common++;
                for (int i = common; i < leaf.path.Length - 1; i++)
                    result.Add(new SearchTreeGroupEntry(new GUIContent(leaf.path[i]), i + 1));
                result.Add(new SearchTreeEntry(new GUIContent(leaf.path[^1])) {
                    level = leaf.path.Length, userData = new CatalogChoice { entry = leaf.entry } });
                previous = leaf.path.Take(leaf.path.Length - 1).ToArray();
            }
            return result;
        }

        internal static string[] CatalogPath(NodeEntry entry)
        {
            var path = entry.title.ToArray();
            if (entry.node is PropertyNode property) path = new[] { "Parameters", property.property.displayName };
            else if (entry.node is SubGraphNode && path[0] != "Sub Graphs") path = new[] { "Sub Graphs" }.Concat(path).ToArray();
            else if (entry.node is CustomFunctionNode) path = new[] { "HLSL", "Custom Function" };
            if (entry.compatibleSlotId >= 0 && entry.node.FindSlot<MaterialSlot>(entry.compatibleSlotId) is { } slot)
                path[^1] += " : " + slot.displayName;
            return path;
        }

        static float RootOrder(string root)
        {
            Color.RGBToHSV(ShaderGraphCatalogIcon.CategoryColor(root), out float hue, out float saturation, out _);
            return root switch { "Parameters" => -3, "Input" or "Sampling" => -2, "UV" => -1.5f,
                "Procedural" => -1, "Sub Graphs" => 2, "HLSL" => 3, _ => saturation == 0 ? 1 : hue };
        }

        static ShaderGraphCatalogIcon.Appearance CatalogAppearance(SearchTreeEntry entry, string path)
        {
            if (entry is SearchTreeGroupEntry) return ShaderGraphCatalogIcon.ForCategory(path);
            var choice = entry.userData as CatalogChoice;
            string name = choice?.entry.title.LastOrDefault() ?? entry.content.text;
            string kind = name.Replace(" ", "");
            kind = kind switch { "Float" or "Vector2" or "Vector3" or "Vector4" => "Value",
                "DDXY" => "FWidth", "Logarithm" => "Log", "SimpleNoise" => "ValueNoise", "GradientNoise" => "PerlinNoise",
                "Voronoi" => "CellularNoise", "UnityPBR" or "SceneLighting" => "NormalLighting",
                "SampleTexture2D" => "SampleTexture", "SampleGradientLUT" => "SampleGradient", "SampleCurveLUT" => "SampleCurve",
                "Branch" => "Select", "Comparison" => "Compare", "Modulo" => "Modulo", _ => kind };
            if (choice?.entry.node is PropertyNode property) kind = property.property switch
            {
                WhimGradientShaderProperty => "SampleGradient",
                WhimCurveShaderProperty => "SampleCurve",
                { propertyType: PropertyType.Float or PropertyType.Vector2 or PropertyType.Vector3 or PropertyType.Vector4 } => "Value",
                { propertyType: PropertyType.Color } => "Color",
                { propertyType: PropertyType.Matrix2 or PropertyType.Matrix3 or PropertyType.Matrix4 } => "MatrixConstruction",
                { propertyType: PropertyType.Texture2D or PropertyType.Texture2DArray or PropertyType.Texture3D or
                    PropertyType.Cubemap or PropertyType.VirtualTexture } => "SampleTexture",
                { propertyType: PropertyType.Gradient } => "SampleGradient",
                _ => "Parameter"
            };
            if (choice?.entry.node is SubGraphNode) kind = "SubGraph";
            return ShaderGraphCatalogIcon.ForNode(path, kind);
        }

        internal string CatalogThumbnailReport()
        {
            var lines = new List<string>();
            int total = 0, covered = 0, reused = 0;
            foreach (var entry in CreateNodeSearchTree())
            {
                if (entry is SearchTreeGroupEntry) continue;
                var choice = (CatalogChoice)entry.userData;
                string path = string.Join("/", CatalogPath(choice.entry));
                string kind = CatalogAppearance(entry, path).kind;
                string resolved = ShaderGraphCatalogThumbnails.ResolveKind(kind);
                bool available = ShaderGraphCatalogThumbnails.TryGet(kind, out _, out _);
                total++; if (available) covered++;
                if (available && resolved != kind) reused++;
                lines.Add($"{(available ? "thumbnail" : "icon")}\t{path}\t{resolved}");
            }
            lines.Insert(0, $"{covered}/{total} entries with thumbnails; {reused} use shared atlas aliases.");
            return string.Join("\n", lines);
        }

        bool CreateNodeChoice(object choice, Vector2 position, ContextView context = null, ShaderPort connectedPort = null)
        {
            if (!Editable || choice is not CatalogChoice candidate) return false;
            graphObject.RegisterCompleteObjectUndo("Create " + candidate.entry.node.name);
            var node = catalogProvider.CopyNodeForGraph(candidate.entry.node);
            if (node is BlockNode block)
            {
                if (context == null) return false;
                if (block.isCustomBlock)
                    block.customName = GraphUtil.SanitizeName(context.contextData.blocks.Select(b => b.value.descriptor.displayName), "{0}_{1}", block.descriptor.displayName);
                else if (context.contextData.blocks.Any(b => b.value.name == block.name)) return true;
                block.owner = Graph;
                Graph.AddBlock(block, context.contextData, context.contextData.blocks.Count);
                return true;
            }
            var draw = node.drawState; draw.position = new Rect(position, Vector2.zero); node.drawState = draw;
            Graph.AddNode(node);
            if (connectedPort?.slot != null && candidate.entry.compatibleSlotId >= 0)
            {
                var source = connectedPort.slot.slotReference;
                var created = node.GetSlotReference(candidate.entry.compatibleSlotId);
                Graph.Connect(connectedPort.slot.isOutputSlot ? source : created, connectedPort.slot.isOutputSlot ? created : source);
            }
            Graph.ValidateGraph(); nextUpdate = 0;
            return true;
        }

        void OpenCreateNode(NodeCreationContext request, ShaderPort connectedPort = null)
        {
            if (!Editable) return;
            catalogProvider.connectedPort = connectedPort; catalogProvider.target = request.target;
            catalogProvider.GenerateNodeEntries();
            var tree = ToSearchTree(catalogProvider.currentNodeEntries);
            var root = new WhimShaderSearchDropdown.Item("Create Node", true);
            var parents = new List<WhimShaderSearchDropdown.Item> { root };
            foreach (var entry in tree.Skip(1))
            {
                while (parents.Count > entry.level) parents.RemoveAt(parents.Count - 1);
                string path = string.Join("/", parents.Skip(1).Select(p => p.text).Append(entry.content.text));
                var appearance = CatalogAppearance(entry, path);
                var item = new WhimShaderSearchDropdown.Item(entry.content.text, entry is SearchTreeGroupEntry, entry) {
                    makeIcon = () => { var preview = new ShaderGraphCatalogPreview(); preview.Bind(appearance); return preview; },
                    accentColor = appearance.color, searchAlias = (entry.userData as CatalogChoice)?.aliases
                };
                parents[^1].children.Add(item); if (item.group) parents.Add(item);
            }
            var graph = Graph;
            Vector2 panelPosition = request.screenMousePosition - position.position;
            Vector2 nodePosition = editor.graphView.contentViewContainer.WorldToLocal(panelPosition);
            WhimShaderSearchDropdown.Show(request.screenMousePosition, root, item => {
                var entry = (SearchTreeEntry)item.data;
                if (entry.userData is not CatalogChoice choice) return false;
                return CreateNodeChoice(choice, nodePosition, request.target as ContextView, connectedPort);
            }, owner: this, valid: () => editor != null && ReferenceEquals(Graph, graph) &&
                (connectedPort == null || connectedPort.slot?.owner?.owner == graph),
                makeOptions: ShaderGraphCatalogThumbnails.CreateSelector,
                rowHeight: item => ShaderGraphCatalogThumbnails.Height(CatalogAppearance((SearchTreeEntry)item.data, item.text).kind, 20));
        }

        sealed class CatalogEdgeListener : IEdgeConnectorListener
        {
            readonly WhimShaderWindow window;
            internal CatalogEdgeListener(WhimShaderWindow window) { this.window = window; }
            public void OnDropOutsidePort(UnityEditor.Experimental.GraphView.Edge edge, Vector2 position)
            {
                var dragged = edge.output?.edgeConnector?.edgeDragHelper.draggedPort ?? edge.input?.edgeConnector?.edgeDragHelper.draggedPort;
                if (dragged is not ShaderPort port || port.slot == null || window.editor == null) return;
                window.OpenCreateNode(new NodeCreationContext { screenMousePosition = position + window.position.position }, port);
            }
            public void OnDrop(GraphView view, UnityEditor.Experimental.GraphView.Edge edge)
            {
                var output = edge.output.GetSlot(); var input = edge.input.GetSlot();
                if (output == null || input == null || window.Graph == null) return;
                window.graphObject.RegisterCompleteObjectUndo("Connect Edge");
                window.Graph.Connect(output.slotReference, input.slotReference);
                window.nextUpdate = 0;
            }
        }
    }
}
