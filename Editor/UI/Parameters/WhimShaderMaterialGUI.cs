using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace DCFApixels.WhimShader
{
    // ShaderLab CustomEditor: "DCFApixels.WhimShader.WhimShaderMaterialGUI".
    public sealed class WhimShaderMaterialGUI : ShaderGUI
    {
        // UniversalTarget.customEditorGUI (URP 17.7), also emitted as the ShaderLab CustomEditor name.
        internal const string CustomEditorName = "DCFApixels.WhimShader.WhimShaderMaterialGUI";
        readonly Dictionary<string, bool> foldouts = new();
        readonly Dictionary<string, string> activeTabs = new();
        readonly Dictionary<string, MaterialProperty> propertiesByName = new();
        readonly Dictionary<string, WhimShaderMaterialParameter> parametersById = new();
        readonly HashSet<string> drawn = new();
        Shader shader;
        ShaderGUI native;
        WhimShaderMaterialMetadata metadata;
        bool nativeUnlit;

        public override void OnGUI(MaterialEditor materialEditor, MaterialProperty[] properties)
        {
            if (materialEditor.target is not Material material) return;
            Resolve(material);
            native.OnGUI(materialEditor, properties);
        }

        void Resolve(Material material)
        {
            bool switched = shader != material.shader;
            // Import replaces the artifact object. Reload at Layout, not between Layout and Repaint.
            if (switched || metadata == null || Event.current?.type == EventType.Layout)
                metadata = WhimShaderMaterialMetadata.Find(material.shader);
            bool unlit = metadata != null ? metadata.unlit : material.GetTag("UniversalMaterialType", false, "Lit") == "Unlit";
            if (native == null || switched || nativeUnlit != unlit)
            {
                shader = material.shader; nativeUnlit = unlit;
                native = unlit ? new GroupedUnlitGUI(this) : new GroupedLitGUI(this);
                foldouts.Clear(); activeTabs.Clear();
            }
        }

        public override void ValidateMaterial(Material material)
        {
            if (material == null) return;
            // Validation can be called for another material during multi-object editing.
            var data = WhimShaderMaterialMetadata.Find(material.shader);
            bool unlit = data != null ? data.unlit : material.GetTag("UniversalMaterialType", false, "Lit") == "Unlit";
            if (unlit) new ShaderGraphUnlitGUI().ValidateMaterial(material);
            else new ShaderGraphLitGUI().ValidateMaterial(material);
        }

        public override void AssignNewShaderToMaterial(Material material, Shader oldShader, Shader newShader)
        {
            base.AssignNewShaderToMaterial(material, oldShader, newShader);
            ValidateMaterial(material);
        }

        bool DrawParameters(MaterialEditor editor, MaterialProperty[] properties)
        {
            if (metadata == null) return false;
            if (!metadata.Supported)
            {
                EditorGUILayout.HelpBox("Unsupported WhimShader material layout version. Showing native shader properties.", MessageType.Warning);
                return false;
            }
            propertiesByName.Clear(); parametersById.Clear(); drawn.Clear();
            foreach (var property in properties) propertiesByName[property.name] = property;
            foreach (var parameter in metadata.parameters) parametersById[parameter.propertyId] = parameter;
            var path = new HashSet<string>();
            foreach (var item in metadata.layout.Children(null)) DrawItem(editor, item, path);
            // These are graph-authored inputs only; URP surface options remain in the native sections.
            foreach (string reference in metadata.otherInputReferences) DrawProperty(editor, reference);
            return true;
        }

        bool HasVisibleContent(WhimShaderParameterItem item, HashSet<string> visited)
        {
            if (!visited.Add(item.id)) return false;
            try
            {
                if (item.kind == WhimShaderParameterItemKind.Parameter)
                    return parametersById.TryGetValue(item.propertyId, out var parameter) &&
                        parameter.referenceNames.Any(name => propertiesByName.TryGetValue(name, out var property) && Visible(property));
                return metadata.layout.Children(item.id).Any(child => HasVisibleContent(child, visited));
            }
            finally { visited.Remove(item.id); }
        }

        void DrawItem(MaterialEditor editor, WhimShaderParameterItem item, HashSet<string> path)
        {
            if (!HasVisibleContent(item, new HashSet<string>()) || !path.Add(item.id)) return;
            try
            {
                if (item.kind == WhimShaderParameterItemKind.Parameter)
                {
                    if (parametersById.TryGetValue(item.propertyId, out var parameter))
                        foreach (string reference in parameter.referenceNames) DrawProperty(editor, reference);
                    return;
                }
                if (item.kind == WhimShaderParameterItemKind.Tabs)
                {
                    var tabs = metadata.layout.Children(item.id).Where(tab => HasVisibleContent(tab, new HashSet<string>())).ToList();
                    if (tabs.Count == 0) return;
                    EditorGUILayout.LabelField(item.title, EditorStyles.boldLabel);
                    string active = activeTabs.TryGetValue(item.id, out var stored) ? stored : item.defaultTabId;
                    int selected = Math.Max(0, tabs.FindIndex(tab => tab.id == active));
                    var labels = tabs.Select(tab => new GUIContent(tab.title)).ToArray();
                    // Measure native button labels against the current indented panel width.
                    Rect available = EditorGUI.IndentedRect(EditorGUILayout.GetControlRect(false, 0));
                    float widest = labels.Max(label => EditorStyles.toolbarButton.CalcSize(label).x);
                    int columns = Mathf.Clamp(Mathf.FloorToInt(available.width / Mathf.Max(1, widest)), 1, tabs.Count);
                    selected = GUILayout.SelectionGrid(selected, labels, columns, EditorStyles.toolbarButton);
                    activeTabs[item.id] = tabs[selected].id;
                    foreach (var child in metadata.layout.Children(tabs[selected].id)) DrawItem(editor, child, path);
                    return;
                }
                bool expanded = true;
                if (item.kind == WhimShaderParameterItemKind.Foldout)
                {
                    expanded = foldouts.TryGetValue(item.id, out var current) ? current : item.expandedByDefault;
                    expanded = EditorGUILayout.Foldout(expanded, item.title, true);
                    foldouts[item.id] = expanded;
                }
                else EditorGUILayout.LabelField(item.title, EditorStyles.boldLabel);
                if (!expanded) return;
                EditorGUI.indentLevel++;
                try { foreach (var child in metadata.layout.Children(item.id)) DrawItem(editor, child, path); }
                finally { EditorGUI.indentLevel--; }
            }
            finally { path.Remove(item.id); }
        }

        static bool Visible(MaterialProperty property) => WhimShaderPropertyFields.IsMaterialPropertyVisible(property);

        void DrawProperty(MaterialEditor editor, string reference)
        {
            if (!propertiesByName.TryGetValue(reference, out var property) || !Visible(property) || !drawn.Add(reference)) return;
            WhimShaderPropertyFields.DrawMaterial(editor, property);
        }

        sealed class GroupedLitGUI : ShaderGraphLitGUI
        {
            readonly WhimShaderMaterialGUI owner;
            MaterialProperty[] properties;
            internal GroupedLitGUI(WhimShaderMaterialGUI owner) { this.owner = owner; }
            public override void FindProperties(MaterialProperty[] values) { properties = values; base.FindProperties(values); }
            public override void DrawSurfaceInputs(Material material)
            { if (!owner.DrawParameters(materialEditor, properties)) base.DrawSurfaceInputs(material); }
        }

        sealed class GroupedUnlitGUI : ShaderGraphUnlitGUI
        {
            readonly WhimShaderMaterialGUI owner;
            MaterialProperty[] properties;
            internal GroupedUnlitGUI(WhimShaderMaterialGUI owner) { this.owner = owner; }
            public override void FindProperties(MaterialProperty[] values) { properties = values; base.FindProperties(values); }
            public override void DrawSurfaceInputs(Material material)
            { if (!owner.DrawParameters(materialEditor, properties)) base.DrawSurfaceInputs(material); }
        }
    }
}
