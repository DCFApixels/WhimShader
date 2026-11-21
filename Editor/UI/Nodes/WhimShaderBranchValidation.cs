using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Experimental.GraphView;
using UnityEditor.Graphing;
using UnityEditor.ShaderGraph;
using UnityEditor.ShaderGraph.Drawing;
using UnityEngine;
using UnityEngine.UIElements;

namespace DCFApixels.WhimShader
{
    public static class WhimShaderBranchValidation
    {
        public static object Snapshot(WhimShaderWindow window = null)
        {
            var view = View(window);
            var edges = VisibleEdges(view);
            var connected = new HashSet<SlotReference>();
            foreach (var edge in edges) { connected.Add(edge.inputSlot); connected.Add(edge.outputSlot); }
            var failures = new List<string>();
            var ports = view.Query<ShaderPort>().ToList().Where(port => port.slot != null).Select(port =>
            {
                var area = port.Q("port-label");
                var label = area?.Q<Label>("type");
                bool expectedConnected = connected.Contains(port.slot.slotReference);
                bool branchLabel = label?.ClassListContains("whimshader-shader-graph-branch-label") == true;
                if (area != null && (!branchLabel || area.pickingMode != (expectedConnected ? PickingMode.Position : PickingMode.Ignore) || label.pickingMode != PickingMode.Ignore))
                    failures.Add(port.slot.owner.objectId + ":" + port.slot.id + " label interaction state mismatch");
                return new
                {
                    nodeId = port.slot.owner.objectId, nodeName = port.slot.owner.name, slotId = port.slot.id,
                    direction = port.direction.ToString(), port.connected, expectedConnected, branchLabel,
                    labelPicking = area?.pickingMode.ToString(), textPicking = label?.pickingMode.ToString(),
                    labelBounds = area?.worldBound.ToString()
                };
            }).ToArray();
            return new
            {
                errors = failures.ToArray(), ports,
                highlightedNodes = view.nodes.ToList().OfType<IShaderNodeView>()
                    .Where(node => node.gvNode.ClassListContains(WhimShaderNodeBranches.BranchHighlight)).Select(node => node.node.objectId).ToArray(),
                highlightedEdges = view.edges.ToList().Where(edge => edge.ClassListContains(WhimShaderNodeBranches.BranchHighlight))
                    .Select(edge => edge.userData).OfType<IEdge>().Select(DescribeEdge).ToArray(),
                selectedNodes = view.selection.OfType<IShaderNodeView>().Select(node => node.node.objectId).ToArray()
            };
        }

        public static object Trace(string nodeId, int slotId, WhimShaderWindow window = null)
        {
            var view = View(window);
            var node = view.graph.GetNodeFromId(nodeId);
            var slot = node?.FindSlot<MaterialSlot>(slotId);
            if (slot == null) throw new ArgumentException("No native slot " + nodeId + ":" + slotId);
            var trace = WhimShaderNodeBranches.Trace(view.graph, slot.slotReference, VisibleEdges(view));
            return new
            {
                nodeId, slotId, direction = slot.isInputSlot ? "Input" : "Output",
                nodes = trace.nodes.Select(model => model.objectId).OrderBy(id => id).ToArray(),
                edges = trace.edges.Select(DescribeEdge).ToArray()
            };
        }

        static object DescribeEdge(IEdge edge) => new
        {
            outputNode = edge.outputSlot.node.objectId, outputSlot = edge.outputSlot.slotId,
            inputNode = edge.inputSlot.node.objectId, inputSlot = edge.inputSlot.slotId
        };

        static List<IEdge> VisibleEdges(MaterialGraphView view)
        {
            var model = new HashSet<IEdge>(view.graph.edges);
            return view.edges.ToList().Where(edge => edge.userData is IEdge data && model.Contains(data) &&
                WhimShaderNodeBranches.IsEligiblePort(edge.input, data.inputSlot) &&
                WhimShaderNodeBranches.IsEligiblePort(edge.output, data.outputSlot)).Select(edge => (IEdge)edge.userData).ToList();
        }

        static MaterialGraphView View(WhimShaderWindow window)
        {
            window ??= EditorWindow.focusedWindow as WhimShaderWindow;
            window ??= Resources.FindObjectsOfTypeAll<WhimShaderWindow>().FirstOrDefault(candidate => candidate.PreviewService != null);
            if (window?.PreviewService == null) throw new InvalidOperationException("No initialized WhimShader window.");
            return window.PreviewService.Editor.graphView;
        }
    }
}
