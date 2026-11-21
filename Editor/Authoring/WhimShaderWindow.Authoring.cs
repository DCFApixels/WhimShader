using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Experimental.GraphView;
using UnityEditor.ShaderGraph;
using UnityEditor.ShaderGraph.Drawing;
using UnityEditor.ShaderGraph.Serialization;
using UnityEditor.Graphing.Util;
using UnityEngine;
using UnityEngine.UIElements;

namespace DCFApixels.WhimShader
{
    public sealed partial class WhimShaderWindow
    {
        internal string AuthoringPath => assetPath;
        internal void AuthoringEnsureReady()
        {
            WhimShaderApi.Require(Graph != null && !rebuilding, "Graph window is rebuilding.", "editor_busy");
            var focus = rootVisualElement.focusController?.focusedElement as VisualElement;
            WhimShaderApi.Require(GUIUtility.hotControl == 0 && !EditorGUIUtility.editingTextField &&
                rootVisualElement.panel?.GetCapturingElement(PointerId.mousePointerId) == null &&
                !(focus is TextElement { isSelectable: true }), "Finish the active graph gesture or text edit first.", "editor_busy");
            if (graphObject.wasUndoRedoPerformed) { graphObject.HandleUndoRedo(); nextUpdate = 0; }
        }
        internal Dictionary<string, WhimShaderArrangement.Geometry> AuthoringGeometry()
        {
            var result = new Dictionary<string, WhimShaderArrangement.Geometry>();
            if (editor == null) return result;
            var canvas = editor.graphView.contentViewContainer;
            foreach (var view in editor.graphView.nodes.ToList())
            {
                if (view is not IShaderNodeView shader || shader.node is BlockNode || !WhimShaderArrangement.Visible(shader.node) || view.resolvedStyle.display == DisplayStyle.None) continue;
                Rect bounds = view.GetPosition();
                if (!float.IsFinite(bounds.width) || bounds.width <= 0 || bounds.height <= 0) continue;
                Vector2 origin = shader.node.drawState.position.position;
                foreach (var field in view.Query<PortInputView>().ToList())
                    if (field.visible && field.resolvedStyle.display != DisplayStyle.None)
                        bounds = WhimShaderArrangement.Union(bounds, canvas.WorldToLocal(field.worldBound));
                var geometry = new WhimShaderArrangement.Geometry { bounds = new Rect(bounds.position - origin, bounds.size) };
                foreach (var port in view.Query<ShaderPort>().ToList())
                    if (port.slot != null) (port.slot.isOutputSlot ? geometry.outputs : geometry.inputs)[port.slot.id.ToString()] = canvas.WorldToLocal(port.worldBound.center) - origin;
                result[shader.node.objectId] = geometry;
            }
            return result;
        }
        internal void AuthoringApply(GraphData candidate, bool save, bool positionsOnly = false)
        {
            AuthoringEnsureReady();
            Undo.IncrementCurrentGroup(); int group = Undo.GetCurrentGroup();
            graphObject.RegisterCompleteObjectUndo("Author Shader Graph");
            if (positionsOnly)
            {
                foreach (var n in candidate.GetNodes<AbstractMaterialNode>().Where(n => n is not BlockNode))
                {
                    var original = Graph.GetNodeFromId(n.objectId); var draw = original.drawState;
                    draw.position = new Rect(n.drawState.position.position, draw.position.size); original.drawState = draw;
                }
                Graph.vertexContext.position = candidate.vertexContext.position;
                Graph.fragmentContext.position = candidate.fragmentContext.position;
                SyncArrangedViews();
            }
            else
            {
                // ReplaceWith transfers node references. Never transfer the disposable staging graph's nodes.
                var replacement = new GraphData { assetGuid = Graph.assetGuid, isSubGraph = Graph.isSubGraph, messageManager = new MessageManager() };
                MultiJson.Deserialize(replacement, MultiJson.Serialize(candidate));
                Graph.ReplaceWith(replacement);
                Graph.OnEnable();
                parameterPanel?.Refresh(); nodePalette?.Invalidate();
            }
            graphObject.isDirty = true; hasUnsavedChanges = true;
            nextUpdate = 0;
            Undo.CollapseUndoOperations(group);
            if (save) SaveGraph();
        }
        internal void ArrangeNodes(bool all)
        {
            if (Graph == null || editor == null) return;
            var selected = all ? Graph.GetNodes<AbstractMaterialNode>().Where(WhimShaderArrangement.Visible).Where(n => n is not BlockNode).Select(n => n.objectId) :
                editor.graphView.selection.OfType<IShaderNodeView>().Where(v => v.node is not BlockNode && WhimShaderArrangement.Visible(v.node)).Select(v => v.node.objectId);
            var moving = new HashSet<string>(selected);
            if (moving.Count == 0) return;
            graphObject.RegisterCompleteObjectUndo(all ? "Arrange Graph" : "Arrange Selected Nodes");
            WhimShaderArrangement.Arrange(Graph, moving, AuthoringGeometry(), all);
            SyncArrangedViews(); nextUpdate = 0;
            if (all) editor.schedule.Execute(FrameGraph);
        }
        void SyncArrangedViews()
        {
            if (editor == null) return;
            foreach (var view in editor.graphView.nodes.ToList())
                if (view is IShaderNodeView n && n.node is not BlockNode && WhimShaderArrangement.Visible(n.node))
                    view.SetPosition(new Rect(n.node.drawState.position.position, view.GetPosition().size));
            foreach (var context in editor.graphView.contexts)
                context.SetPosition(new Rect(context.contextData.position, Vector2.zero));
        }
    }
}
