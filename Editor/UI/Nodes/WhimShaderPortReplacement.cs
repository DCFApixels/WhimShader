using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using UnityEditor.Experimental.GraphView;
using UnityEditor.ShaderGraph;
using UnityEditor.ShaderGraph.Drawing;
using UnityEngine.UIElements;

namespace DCFApixels.WhimShader
{
    // Replaces only native ShaderPort visuals, never graph slots/edges or their serialized identity.
    // TokenNode.output delegates to Pill.right, so use that public setter instead of replacing its child.
    // The installed ports belong to the native views; dispose the host editor after the presenter.
    internal sealed class WhimShaderPortReplacement
    {
        sealed class Installed { }
        readonly ConditionalWeakTable<ShaderPort, Installed> installed = new();
        internal IEdgeConnectorListener Listener { get; }
        internal string LastFailure { get; private set; }
        internal Action<ShaderPort, ShaderPort> PortReplaced { get; set; }
        bool dragInProgress;

        internal WhimShaderPortReplacement(IEdgeConnectorListener listener)
        { Listener = listener ?? throw new ArgumentNullException(nameof(listener)); }

        internal void BeginRefresh(MaterialGraphView graphView)
        {
            // This also covers dragging an existing Edge: EdgeManipulator uses the endpoint's helper.
            dragInProgress = graphView.Query<Port>().ToList().Any(port => port.edgeConnector?.edgeDragHelper?.edgeCandidate != null);
        }

        internal int Apply(IShaderNodeView shaderView, Action<ShaderPort> releasePresentation)
        {
            if (dragInProgress || shaderView.node == null) return 0;
            var view = shaderView.gvNode;
            if (view is not (MaterialNodeView or PropertyNodeView or RedirectNodeView)) return 0;
            int count = 0;
            foreach (var port in Ports(view).ToArray())
            {
                if (port.slot == null || installed.TryGetValue(port, out _)) continue;
                if (!shaderView.FindPort(port.slot.slotReference, out var found) || !ReferenceEquals(found, port))
                { LastFailure = "Native FindPort does not resolve the visible port on " + shaderView.node.name; continue; }
                var pill = view is TokenNode ? view.Q<Pill>("pill") : null;
                if (view is TokenNode token && (pill == null ||
                    port.direction == Direction.Output && (!ReferenceEquals(token.output, port) || !ReferenceEquals(pill.right, port)) ||
                    port.direction == Direction.Input && (!ReferenceEquals(token.input, port) || !ReferenceEquals(pill.left, port))))
                { LastFailure = "TokenNode/Pill port identity mismatch; replacement was skipped."; continue; }
                releasePresentation?.Invoke(port);
                if (Replace(shaderView, port, pill)) count++;
            }
            return count;
        }

        bool Replace(IShaderNodeView shaderView, ShaderPort original, Pill pill)
        {
            var parent = original.parent;
            if (parent == null) return false;
            int index = parent.IndexOf(original);
            var slot = original.slot;
            var edges = original.connections.ToArray();
            var onDisconnect = original.OnDisconnect;
            var replacement = ShaderPort.Create(slot, Listener);
            replacement.name = original.name; replacement.viewDataKey = original.viewDataKey;
            replacement.userData = original.userData; replacement.tooltip = original.tooltip;
            replacement.pickingMode = original.pickingMode; replacement.capabilities = original.capabilities;
            replacement.allowMultiDrag = original.allowMultiDrag; replacement.focusable = original.focusable;
            replacement.portName = original.portName; replacement.portColor = original.portColor;
            replacement.SetEnabled(original.enabledSelf);
            replacement.style.visibility = original.style.visibility;
            replacement.style.display = original.style.display;
            foreach (string className in original.GetClasses()) replacement.AddToClassList(className);

            void Install(ShaderPort port)
            {
                if (pill != null)
                {
                    if (slot.isOutputSlot) pill.right = port;
                    else pill.left = port;
                }
                else
                {
                    var removed = ReferenceEquals(port, original) ? replacement : original;
                    removed.RemoveFromHierarchy(); parent.Insert(Math.Min(index, parent.childCount), port);
                }
            }

            try
            {
                Install(replacement);
                if (!shaderView.FindPort(slot.slotReference, out var resolved) || !ReferenceEquals(resolved, replacement))
                    throw new InvalidOperationException("Native FindPort retained the old port reference.");
                // RefreshPorts registers Node's internal connect/disconnect UI callbacks on the new port.
                shaderView.gvNode.RefreshPorts();
                original.OnDisconnect = null;
                foreach (var edge in edges)
                {
                    original.Disconnect(edge);
                    if (ReferenceEquals(edge.input, original)) edge.input = replacement;
                    if (ReferenceEquals(edge.output, original)) edge.output = replacement;
                    replacement.Connect(edge); edge.UpdateEdgeControl();
                }
                replacement.OnDisconnect = onDisconnect;
                shaderView.gvNode.RefreshExpandedState();
            }
            catch (Exception exception)
            {
                // Keep the original alive until lookup and edge migration have succeeded.
                Install(original);
                replacement.OnDisconnect = null;
                foreach (var edge in edges)
                {
                    replacement.Disconnect(edge);
                    if (ReferenceEquals(edge.input, replacement)) edge.input = original;
                    if (ReferenceEquals(edge.output, replacement)) edge.output = original;
                    if (!original.connections.Contains(edge)) original.Connect(edge);
                    edge.UpdateEdgeControl();
                }
                original.OnDisconnect = onDisconnect;
                replacement.Dispose(); shaderView.gvNode.RefreshExpandedState();
                LastFailure = "Port replacement rolled back: " + exception.Message;
                return false;
            }

            // Commit only after all native lookup/connection/layout operations succeeded. Never roll
            // back to a disposed port, and let the host retarget its popup cache before disposal.
            installed.Add(replacement, new Installed());
            try { PortReplaced?.Invoke(original, replacement); }
            catch (Exception exception) { LastFailure = "Port installed, but the host cache callback failed: " + exception.Message; }
            finally { original.Dispose(); }
            return true;
        }

        static IEnumerable<ShaderPort> Ports(Node view)
        {
            // Native Node may detach an empty input/output container while collapsed.
            var ports = view.Query<ShaderPort>().ToList();
            if (view.inputContainer != null) ports.AddRange(view.inputContainer.Query<ShaderPort>().ToList());
            if (view.outputContainer != null) ports.AddRange(view.outputContainer.Query<ShaderPort>().ToList());
            if (view is TokenNode token)
            {
                if (token.input is ShaderPort input) ports.Add(input);
                if (token.output is ShaderPort output) ports.Add(output);
            }
            return ports.Distinct();
        }

        // Read-only host test: run after replacements and again after native Undo/topology changes.
        internal static string[] Validate(MaterialGraphView graphView)
        {
            var failures = new List<string>();
            foreach (var node in graphView.Query<Node>().ToList())
            {
                if (node is not IShaderNodeView shaderView || shaderView.node == null) continue;
                foreach (var port in Ports(node))
                {
                    if (port.slot == null || !shaderView.FindPort(port.slot.slotReference, out var resolved) || !ReferenceEquals(resolved, port))
                        failures.Add("FindPort identity mismatch: " + shaderView.node.objectId);
                    foreach (var edge in port.connections)
                        if (!ReferenceEquals(port.direction == Direction.Input ? edge.input : edge.output, port))
                            failures.Add("Edge endpoint identity mismatch: " + shaderView.node.objectId);
                }
            }
            return failures.ToArray();
        }
    }
}
