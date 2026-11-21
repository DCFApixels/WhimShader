using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor.ShaderGraph;
using UnityEditor.ShaderGraph.Internal;
using UnityEngine;

namespace DCFApixels.WhimShader
{
    internal enum WhimShaderParameterItemKind { Parameter, Group, Foldout, Tabs, Tab }

    [Serializable]
    internal sealed class WhimShaderParameterItem
    {
        public string id = Guid.NewGuid().ToString("N");
        public string parentId = string.Empty;
        public string propertyId;
        public string title = "Group";
        public WhimShaderParameterItemKind kind;
        public bool expandedByDefault = true;
        public string defaultTabId;
    }

    // A flat ordered tree avoids Unity's depth limit for inline recursive serialization.
    [Serializable]
    internal sealed class WhimShaderParameterLayout
    {
        internal const int CurrentVersion = 1;
        public int version = CurrentVersion;
        public List<WhimShaderParameterItem> items = new();
        internal bool Supported => version == CurrentVersion;
        internal WhimShaderParameterItem Find(string id) => items.Find(x => x.id == id);
        internal IEnumerable<WhimShaderParameterItem> Children(string parentId) => items.Where(x => x.parentId == (parentId ?? string.Empty));
        internal WhimShaderParameterLayout Copy() => JsonUtility.FromJson<WhimShaderParameterLayout>(JsonUtility.ToJson(this));

        internal static WhimShaderParameterLayout FromGraph(GraphData graph)
        {
            var result = new WhimShaderParameterLayout();
            var added = new HashSet<string>();
            foreach (var category in graph.categories)
            {
                string parent = string.Empty;
                if (!string.IsNullOrEmpty(category.name))
                {
                    var group = result.Add(WhimShaderParameterItemKind.Group, null, category.name);
                    parent = group.id;
                }
                foreach (var input in category.Children.OfType<AbstractShaderProperty>())
                    if (added.Add(input.objectId)) result.AddProperty(input, parent);
            }
            result.Normalize(graph);
            return result;
        }

        internal WhimShaderParameterItem Add(WhimShaderParameterItemKind kind, string parent, string title)
        {
            if (!CanParent(kind, parent)) throw new InvalidOperationException("Invalid parameter group parent.");
            var item = new WhimShaderParameterItem { kind = kind, parentId = parent ?? string.Empty, title = title };
            items.Add(item);
            return item;
        }

        internal WhimShaderParameterItem AddProperty(AbstractShaderProperty property, string parent)
        {
            var item = Add(WhimShaderParameterItemKind.Parameter, parent, property.displayName);
            item.propertyId = property.objectId;
            return item;
        }

        internal bool CanParent(WhimShaderParameterItemKind kind, string parentId)
        {
            var parent = string.IsNullOrEmpty(parentId) ? null : Find(parentId);
            if (!string.IsNullOrEmpty(parentId) && parent == null) return false;
            if (kind == WhimShaderParameterItemKind.Tab) return parent?.kind == WhimShaderParameterItemKind.Tabs;
            return parent == null || parent.kind is WhimShaderParameterItemKind.Group or WhimShaderParameterItemKind.Foldout or WhimShaderParameterItemKind.Tab;
        }

        internal bool CanMove(string id, string parentId, string beforeId = null)
        {
            var item = Find(id);
            if (item == null || !CanParent(item.kind, parentId) || id == beforeId) return false;
            if (beforeId != null && Find(beforeId)?.parentId != (parentId ?? string.Empty)) return false;
            var visited = new HashSet<string>();
            for (var parent = Find(parentId); parent != null; parent = Find(parent.parentId))
                if (parent.id == id || !visited.Add(parent.id)) return false;
            return true;
        }

        internal bool Move(string id, string parentId, string beforeId = null)
        {
            if (!CanMove(id, parentId, beforeId)) return false;
            var item = Find(id);
            items.Remove(item);
            item.parentId = parentId ?? string.Empty;
            int index = beforeId == null ? items.Count : items.FindIndex(x => x.id == beforeId);
            items.Insert(index, item);
            RepairDefaultTabs();
            return true;
        }

        // Ungroup preserves every parameter and the order of the remaining containers.
        internal void Ungroup(string id)
        {
            var item = Find(id);
            if (item == null || item.kind == WhimShaderParameterItemKind.Parameter) return;
            int index = items.IndexOf(item);
            var children = Children(id).ToList();
            if (item.kind == WhimShaderParameterItemKind.Tabs)
            {
                children = children.SelectMany(tab => Children(tab.id)).ToList();
                items.RemoveAll(x => x.parentId == id);
            }
            // Removing an individual Tab keeps its children in a neighboring tab.
            if (item.kind == WhimShaderParameterItemKind.Tab)
            {
                var sibling = Children(item.parentId).FirstOrDefault(x => x.id != id);
                if (sibling == null) { Ungroup(item.parentId); return; }
                foreach (var child in children) child.parentId = sibling.id;
            }
            else
            {
                foreach (var child in children) items.Remove(child);
                index = items.IndexOf(item);
                foreach (var child in children) { child.parentId = item.parentId; items.Insert(index++, child); }
            }
            items.Remove(item);
            RepairDefaultTabs();
        }

        internal void Normalize(GraphData graph)
        {
            if (!Supported) return;
            items ??= new();
            var ids = new HashSet<string>();
            var properties = new HashSet<string>(graph.properties.Select(x => x.objectId));
            var used = new HashSet<string>();
            items.RemoveAll(x => x == null || string.IsNullOrEmpty(x.id) || !ids.Add(x.id) ||
                !Enum.IsDefined(typeof(WhimShaderParameterItemKind), x.kind) ||
                x.kind == WhimShaderParameterItemKind.Parameter && (!properties.Contains(x.propertyId ?? string.Empty) || !used.Add(x.propertyId)));
            foreach (var item in items)
            {
                item.parentId ??= string.Empty;
                var visited = new HashSet<string> { item.id };
                for (var parent = Find(item.parentId); parent != null; parent = Find(parent.parentId))
                    if (!visited.Add(parent.id)) { item.parentId = string.Empty; break; }
                if (!CanParent(item.kind, item.parentId))
                {
                    // A detached Tab remains a useful container without introducing another root Tabs.
                    if (item.kind == WhimShaderParameterItemKind.Tab) item.kind = WhimShaderParameterItemKind.Group;
                    item.parentId = string.Empty;
                }
            }
            foreach (var property in graph.properties)
                if (used.Add(property.objectId)) AddProperty(property, null);
            RepairDefaultTabs();
        }

        internal void RepairDefaultTabs()
        {
            foreach (var tabs in items.Where(x => x.kind == WhimShaderParameterItemKind.Tabs))
                if (!Children(tabs.id).Any(x => x.id == tabs.defaultTabId)) tabs.defaultTabId = Children(tabs.id).FirstOrDefault()?.id;
        }
    }

    // No Title attribute, shader slots, preview or generated code. The host hides its native view.
    [Serializable]
    internal sealed class WhimShaderParameterLayoutNode : AbstractMaterialNode
    {
        [SerializeField] internal WhimShaderParameterLayout layout = new();
        public WhimShaderParameterLayoutNode() { name = string.Empty; }
        internal override bool ExposeToSearcher => false;
        public override bool hasPreview => false;
        public override bool canSetPrecision => false;
        public override bool canDeleteNode => false;
        public override bool canCutNode => false;
        public override bool canCopyNode => false;

        internal static WhimShaderParameterLayoutNode Find(GraphData graph) => graph.GetNodes<WhimShaderParameterLayoutNode>().FirstOrDefault();
    }
}
