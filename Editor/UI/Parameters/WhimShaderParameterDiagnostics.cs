using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor.ShaderGraph;
using UnityEngine;

namespace DCFApixels.WhimShader
{
    // Read-only checks for the host's Editor tests. Does not normalize, save or operate Undo.
    internal static class WhimShaderParameterDiagnostics
    {
        internal static string[] Validate(GraphData graph)
        {
            if (graph == null) throw new ArgumentNullException(nameof(graph));
            var errors = new List<string>();
            var nodes = graph.GetNodes<WhimShaderParameterLayoutNode>().ToList();
            if (nodes.Count > 1) errors.Add("More than one parameter layout node.");
            if (nodes.Count == 0) return errors.ToArray(); // Created lazily on the first layout edit.
            var layout = nodes[0].layout;
            if (layout == null || !layout.Supported || layout.items == null)
            { errors.Add("Missing or unsupported parameter layout."); return errors.ToArray(); }
            var byId = new Dictionary<string, WhimShaderParameterItem>();
            var properties = new HashSet<string>(graph.properties.Select(p => p.objectId));
            var seenProperties = new HashSet<string>();
            foreach (var item in layout.items)
            {
                if (item == null || string.IsNullOrEmpty(item.id)) { errors.Add("Null item or empty item ID."); continue; }
                if (!byId.TryAdd(item.id, item)) errors.Add("Duplicate item: " + item.id);
                if (!Enum.IsDefined(typeof(WhimShaderParameterItemKind), item.kind)) errors.Add("Unknown item kind: " + item.id);
                if (item.kind != WhimShaderParameterItemKind.Parameter) continue;
                if (string.IsNullOrEmpty(item.propertyId) || !properties.Contains(item.propertyId)) errors.Add("Missing property: " + item.id);
                else if (!seenProperties.Add(item.propertyId)) errors.Add("Duplicate property entry: " + item.propertyId);
            }
            foreach (var item in byId.Values)
            {
                byId.TryGetValue(item.parentId ?? string.Empty, out var parent);
                bool isRoot = string.IsNullOrEmpty(item.parentId);
                bool validParent = item.kind == WhimShaderParameterItemKind.Tab
                    ? parent?.kind == WhimShaderParameterItemKind.Tabs
                    : isRoot || parent?.kind is WhimShaderParameterItemKind.Group or WhimShaderParameterItemKind.Foldout or WhimShaderParameterItemKind.Tab;
                if (!validParent) errors.Add("Invalid parent: " + item.id);
                var visited = new HashSet<string> { item.id };
                for (var ancestor = parent; ancestor != null; byId.TryGetValue(ancestor.parentId ?? string.Empty, out ancestor))
                    if (!visited.Add(ancestor.id)) { errors.Add("Parent cycle: " + item.id); break; }
                if (item.kind == WhimShaderParameterItemKind.Tabs)
                {
                    var tabs = byId.Values.Where(child => child.parentId == item.id).ToList();
                    if (tabs.Count > 0 && !tabs.Any(tab => tab.id == item.defaultTabId)) errors.Add("Invalid default tab: " + item.id);
                    if (tabs.Count == 0 && !string.IsNullOrEmpty(item.defaultTabId)) errors.Add("Stale default tab: " + item.id);
                }
            }
            foreach (string property in properties)
                if (!seenProperties.Contains(property)) errors.Add("Unmapped property: " + property);
            return errors.ToArray();
        }

        [Serializable]
        sealed class State
        {
            public List<string> properties;
            public List<string> layouts;
        }

        // Capture before a UI edit; compare after the host processes native Undo/Redo or reopens the graph.
        // Includes order, IDs, defaults, exposed/reference names and the complete serialized layout.
        // Node/edge conversion is a separate graph test; this snapshot intentionally covers parameters only.
        internal static string CaptureState(GraphData graph)
        {
            if (graph == null) throw new ArgumentNullException(nameof(graph));
            return JsonUtility.ToJson(new State
            {
                properties = graph.properties.Select(property => property.Serialize()).ToList(),
                layouts = graph.GetNodes<WhimShaderParameterLayoutNode>().OrderBy(node => node.objectId).Select(node => node.Serialize()).ToList()
            });
        }

        internal static void AssertRestored(GraphData graph, string expectedState)
        {
            var errors = Validate(graph);
            if (errors.Length != 0) throw new InvalidOperationException(string.Join("\n", errors));
            if (!string.Equals(expectedState, CaptureState(graph), StringComparison.Ordinal))
                throw new InvalidOperationException("Parameter values or serialized layout differ from the captured state.");
        }
    }
}
