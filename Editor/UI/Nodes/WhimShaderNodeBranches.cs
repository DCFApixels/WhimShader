// Port-label interaction adapted from WhimTex ShaderGraphWindow.Branches.cs.
// Copyright (c) 2026 Mikhail. MIT; see ../Workspace/LICENSE.WhimTex.md.
using System;
using System.Collections.Generic;
using UnityEditor.Experimental.GraphView;
using UnityEditor.Graphing;
using UnityEditor.ShaderGraph;
using UnityEditor.ShaderGraph.Drawing;
using UnityEngine;
using UnityEngine.UIElements;
using Edge = UnityEditor.Experimental.GraphView.Edge;
using Node = UnityEditor.Experimental.GraphView.Node;

namespace DCFApixels.WhimShader
{
    internal sealed class WhimShaderNodeBranches : IDisposable
    {
        internal const string BranchHighlight = "whimshader-shader-graph-branch-highlight";
        readonly MaterialGraphView graphView;
        readonly List<PortBranchLabel> labels = new();
        readonly Dictionary<SlotReference, BranchTrace> branchTraces = new();
        readonly List<VisualElement> branchHighlights = new();
        readonly List<ISelectable> branchPreviousSelection = new();
        readonly HashSet<IEdge> modelEdges = new();
        readonly HashSet<SlotReference> connectedSlots = new();
        Dictionary<AbstractMaterialNode, Node> nodeViews = new(), nextNodes = new();
        Dictionary<IEdge, Edge> edgeViews = new(), nextEdges = new();
        PortBranchLabel hoveredBranch, pressedBranch;
        Vector2 branchPressPosition;
        bool branchAdditive, disposed;

        internal WhimShaderNodeBranches(MaterialGraphView graphView)
        {
            this.graphView = graphView;
            graphView.RegisterCallback<MouseMoveEvent>(OnMouseMove, TrickleDown.TrickleDown);
            graphView.RegisterCallback<MouseUpEvent>(OnMouseUp);
            graphView.RegisterCallback<MouseCaptureOutEvent>(OnCaptureOut);
            graphView.RegisterCallback<DetachFromPanelEvent>(OnDetach);
            graphView.RegisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);
        }

        internal sealed class PortBranchLabel : IDisposable
        {
            readonly WhimShaderNodeBranches canvas;
            internal readonly ShaderPort port;
            internal readonly Label label;
            readonly VisualElement labelArea;
            readonly PickingMode originalPickingMode;
            readonly bool addedClass;
            bool connected;

            internal PortBranchLabel(WhimShaderNodeBranches canvas, ShaderPort port, Label label, VisualElement labelArea)
            {
                this.canvas = canvas; this.port = port; this.label = label; this.labelArea = labelArea;
                originalPickingMode = label.pickingMode;
                addedClass = !label.ClassListContains("whimshader-shader-graph-branch-label");
                label.AddToClassList("whimshader-shader-graph-branch-label");
                label.pickingMode = PickingMode.Ignore;
                labelArea.RegisterCallback<PointerEnterEvent>(OnEnter);
                labelArea.RegisterCallback<PointerLeaveEvent>(OnLeave);
                labelArea.RegisterCallback<DetachFromPanelEvent>(OnDetach);
                labelArea.RegisterCallback<TooltipEvent>(OnTooltip);
                labelArea.RegisterCallback<MouseDownEvent>(OnMouseDown);
                canvas.labels.Add(this);
            }

            internal void Refresh()
            {
                connected = port.slot != null && port.enabledInHierarchy && canvas.connectedSlots.Contains(port.slot.slotReference);
                labelArea.pickingMode = connected ? PickingMode.Position : PickingMode.Ignore;
                label.pickingMode = PickingMode.Ignore;
                if (!connected)
                {
                    canvas.HideBranch(this);
                    if (ReferenceEquals(canvas.pressedBranch, this)) canvas.CancelBranchClick();
                }
            }

            void OnEnter(PointerEnterEvent e) { canvas.Refresh(); if (connected) canvas.ShowBranch(this); }
            void OnLeave(PointerLeaveEvent e) => canvas.HideBranch(this);
            void OnDetach(DetachFromPanelEvent e)
            {
                canvas.HideBranch(this);
                if (ReferenceEquals(canvas.pressedBranch, this)) canvas.CancelBranchClick();
            }
            void OnTooltip(TooltipEvent e) { e.tooltip = string.Empty; e.StopPropagation(); }
            void OnMouseDown(MouseDownEvent e)
            {
                canvas.Refresh();
                if (connected && e.button == 0 && !e.altKey && !e.shiftKey) canvas.BeginBranchClick(this, e);
            }

            public void Dispose()
            {
                canvas.HideBranch(this);
                if (ReferenceEquals(canvas.pressedBranch, this)) canvas.CancelBranchClick();
                canvas.labels.Remove(this);
                labelArea.UnregisterCallback<PointerEnterEvent>(OnEnter);
                labelArea.UnregisterCallback<PointerLeaveEvent>(OnLeave);
                labelArea.UnregisterCallback<DetachFromPanelEvent>(OnDetach);
                labelArea.UnregisterCallback<TooltipEvent>(OnTooltip);
                labelArea.UnregisterCallback<MouseDownEvent>(OnMouseDown);
                label.pickingMode = originalPickingMode;
                if (addedClass) label.RemoveFromClassList("whimshader-shader-graph-branch-label");
            }
        }

        internal void Refresh()
        {
            if (disposed) return;
            nextNodes.Clear();
            foreach (var view in graphView.nodes)
                if (view is IShaderNodeView shaderView && shaderView.node != null)
                    nextNodes[shaderView.node] = view;
            modelEdges.Clear();
            foreach (var edge in graphView.graph.edges) modelEdges.Add(edge);
            nextEdges.Clear(); connectedSlots.Clear();
            foreach (var edge in graphView.edges)
            {
                if (edge.userData is not IEdge data || !modelEdges.Contains(data) ||
                    !IsEligiblePort(edge.input, data.inputSlot) || !IsEligiblePort(edge.output, data.outputSlot)) continue;
                nextEdges[data] = edge;
                connectedSlots.Add(data.inputSlot); connectedSlots.Add(data.outputSlot);
            }
            bool changed = !SameViews(nodeViews, nextNodes) || !SameViews(edgeViews, nextEdges);
            (nodeViews, nextNodes) = (nextNodes, nodeViews);
            (edgeViews, nextEdges) = (nextEdges, edgeViews);
            foreach (var label in labels) label.Refresh();
            if (changed) ResetBranchTraces(refreshHover: true);
        }

        static bool SameViews<TKey, TView>(Dictionary<TKey, TView> previous, Dictionary<TKey, TView> current) where TView : VisualElement
        {
            if (previous.Count != current.Count) return false;
            foreach (var pair in current)
                if (!previous.TryGetValue(pair.Key, out var view) || !ReferenceEquals(view, pair.Value)) return false;
            return true;
        }

        internal static bool IsEligiblePort(Port port, SlotReference slot) => port is ShaderPort shaderPort &&
            shaderPort.slot != null && shaderPort.slot.slotReference.Equals(slot) &&
            !shaderPort.slot.hidden && !shaderPort.slot.hideConnector && port.enabledInHierarchy;

        void OnMouseMove(MouseMoveEvent e)
        {
            if (pressedBranch != null && e.mousePosition != branchPressPosition) CancelBranchClick();
        }

        void OnMouseUp(MouseUpEvent e)
        {
            if (e.button != 0) return;
            Refresh();
            var branch = pressedBranch;
            pressedBranch = null;
            if (branch != null && e.mousePosition == branchPressPosition && branch.label.panel != null)
            {
                if (branchAdditive)
                {
                    graphView.ClearSelection();
                    foreach (var item in branchPreviousSelection) graphView.AddToSelection(item);
                }
                SelectBranch(branch, branchAdditive);
            }
            branchPreviousSelection.Clear();
        }

        void OnCaptureOut(MouseCaptureOutEvent e) => CancelBranchClick();
        void OnDetach(DetachFromPanelEvent e) { ResetBranchTraces(); }
        void OnKeyDown(KeyDownEvent e) { if (e.keyCode == KeyCode.Escape) CancelBranchClick(); }

        void BeginBranchClick(PortBranchLabel branch, MouseDownEvent e)
        {
            var node = branch.label.GetFirstAncestorOfType<Node>();
            if (node == null) return;
            CancelBranchClick();
            pressedBranch = branch;
            branchPressPosition = e.mousePosition;
            branchAdditive = e.ctrlKey || e.commandKey;
            if (branchAdditive) branchPreviousSelection.AddRange(graphView.selection);
            using (var down = MouseDownEvent.GetPooled(e))
            {
                down.target = node;
                node.SendEvent(down);
            }
            e.StopPropagation();
        }

        void CancelBranchClick()
        {
            pressedBranch = null;
            branchPreviousSelection.Clear();
        }

        void ShowBranch(PortBranchLabel label)
        {
            ClearBranchHighlight(); hoveredBranch = label;
            var trace = GetBranch(label);
            foreach (var model in trace.nodes) if (nodeViews.TryGetValue(model, out var node)) Highlight(node);
            foreach (var data in trace.edges) if (edgeViews.TryGetValue(data, out var edge)) Highlight(edge);
        }

        void SelectBranch(PortBranchLabel label, bool additive)
        {
            var trace = GetBranch(label);
            graphView.Focus();
            if (!additive) graphView.ClearSelection();
            foreach (var model in trace.nodes)
                if (nodeViews.TryGetValue(model, out var node) && !graphView.selection.Contains(node)) graphView.AddToSelection(node);
        }

        BranchTrace GetBranch(PortBranchLabel label)
        {
            var slot = label.port.slot.slotReference;
            if (!branchTraces.TryGetValue(slot, out var trace))
            {
                trace = Trace(graphView.graph, slot, edgeViews.Keys);
                branchTraces[slot] = trace;
            }
            return trace;
        }

        internal sealed class BranchTrace
        {
            internal readonly HashSet<AbstractMaterialNode> nodes = new();
            internal readonly HashSet<IEdge> edges = new();
        }

        internal static BranchTrace Trace(GraphData graph, SlotReference origin, IEnumerable<IEdge> visibleEdges = null)
        {
            var trace = new BranchTrace();
            if (origin.node == null || !graph.ContainsNode(origin.node) || origin.slot == null || origin.slot.hidden || origin.slot.hideConnector) return trace;
            bool upstream = origin.slot.isInputSlot;
            var connections = new Dictionary<AbstractMaterialNode, List<IEdge>>();
            foreach (var edge in visibleEdges ?? graph.edges)
            {
                var input = edge.inputSlot.slot;
                var output = edge.outputSlot.slot;
                if (input == null || output == null || input.hidden || output.hidden || input.hideConnector || output.hideConnector ||
                    !graph.ContainsNode(input.owner) || !graph.ContainsNode(output.owner)) continue;
                var from = upstream ? input.owner : output.owner;
                if (!connections.TryGetValue(from, out var list)) connections.Add(from, list = new());
                list.Add(edge);
            }
            var visited = new HashSet<AbstractMaterialNode> { origin.node };
            var pending = new Queue<AbstractMaterialNode>();
            pending.Enqueue(origin.node);
            while (pending.Count != 0)
            {
                var current = pending.Dequeue();
                if (!connections.TryGetValue(current, out var list)) continue;
                foreach (var edge in list)
                {
                    var from = upstream ? edge.inputSlot : edge.outputSlot;
                    if (current == origin.node && !from.Equals(origin)) continue;
                    trace.edges.Add(edge);
                    var next = upstream ? edge.outputSlot.node : edge.inputSlot.node;
                    if (!visited.Add(next)) continue;
                    trace.nodes.Add(next);
                    pending.Enqueue(next);
                }
            }
            return trace;
        }

        void Highlight(VisualElement element) { element.AddToClassList(BranchHighlight); branchHighlights.Add(element); }
        void HideBranch(PortBranchLabel label)
        {
            if (!ReferenceEquals(hoveredBranch, label)) return;
            ClearBranchHighlight(); hoveredBranch = null;
        }
        void ClearBranchHighlight()
        {
            foreach (var element in branchHighlights) element.RemoveFromClassList(BranchHighlight);
            branchHighlights.Clear();
        }
        void ResetBranchTraces(bool refreshHover = false)
        {
            CancelBranchClick();
            var previous = hoveredBranch;
            ClearBranchHighlight(); hoveredBranch = null; branchTraces.Clear();
            if (refreshHover && previous?.label.panel != null && previous.label.enabledInHierarchy && previous.label.resolvedStyle.display != DisplayStyle.None)
                ShowBranch(previous);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            ResetBranchTraces();
            graphView.UnregisterCallback<MouseMoveEvent>(OnMouseMove, TrickleDown.TrickleDown);
            graphView.UnregisterCallback<MouseUpEvent>(OnMouseUp);
            graphView.UnregisterCallback<MouseCaptureOutEvent>(OnCaptureOut);
            graphView.UnregisterCallback<DetachFromPanelEvent>(OnDetach);
            graphView.UnregisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);
            foreach (var label in labels.ToArray()) label.Dispose();
            nodeViews.Clear(); nextNodes.Clear(); edgeViews.Clear(); nextEdges.Clear();
            modelEdges.Clear(); connectedSlots.Clear();
        }
    }
}
