using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor.ShaderGraph;
using UnityEditor.ShaderGraph.Drawing;
using UnityEditor.ShaderGraph.Drawing.Inspector;
using UnityEditor.ShaderGraph.Internal;
using UnityEngine.UIElements;

namespace DCFApixels.WhimShader
{
    internal sealed partial class WhimShaderParametersPanel
    {
        readonly Dictionary<PropertyNodeView, NodeHover> nodeHover = new();
        string hoveredProperty;

        internal void SetSelectedNodeInspector(VisualElement nativeInspector)
        {
            selectedNodeInspector.Clear();
            if (nativeInspector == null) { selectedNodeInspector.RemoveFromHierarchy(); return; }
            nativeInspector.RemoveFromHierarchy();
            // Native floating inspectors store position inline; the outer workspace owns placement here.
            nativeInspector.style.position = Position.Relative;
            nativeInspector.style.left = nativeInspector.style.top = nativeInspector.style.right = nativeInspector.style.bottom = StyleKeyword.Auto;
            nativeInspector.style.width = nativeInspector.style.height = StyleKeyword.Auto;
            nativeInspector.style.minWidth = nativeInspector.style.minHeight = 0;
            nativeInspector.style.flexGrow = 1;
            selectedNodeInspector.Add(nativeInspector);
            selectedNodeInspector.AddToClassList("whimshader-selected-node-inspector");
            if (selectedNodeInspector.parent != inspectorRoot) inspectorRoot.Add(selectedNodeInspector);
        }

        internal static void ProtectGeneratedFunctionSettings(InspectorView inspector)
        {
            var selection = inspector.selection;
            if (!selection.Any(item => item is MaterialNodeView { node: WhimShaderCustomFunctionNode }))
                return;

            var fields = inspector.Q("NodeSettingsContainer").Query<HlslFunctionView>().ToList();
            var functions = selection.OfType<MaterialNodeView>().Select(view => view.node).OfType<CustomFunctionNode>();
            foreach (var (field, function) in fields.Zip(functions, (field, function) => (field, function)))
            {
                if (function is not WhimShaderCustomFunctionNode || field.style.display == DisplayStyle.None)
                    continue;

                // The inline editor applies HLSL and port changes together, leaving the compiled wrapper intact on failure.
                field.SetEnabled(false);
                field.style.display = DisplayStyle.None;
                foreach (var ports in field.parent.Children().OfType<ReorderableSlotListView>())
                {
                    ports.SetEnabled(false);
                    ports.style.display = DisplayStyle.None;
                }
                field.parent.Add(new Label("Edit code and ports on the node.") { style = { whiteSpace = WhiteSpace.Normal } });
            }
        }

        // A standalone MaterialGraphView can be used without a GraphEditorView catalog.
        // Normal embedding uses native catalog actions, including custom property drawers.
        void AddFallbackPropertyMenu(DropdownMenu menu)
        {
            void Add(string title, Func<AbstractShaderProperty> create) => menu.AppendAction("Parameter/" + title,
                _ => AddProperty(new AddShaderInputAction { shaderInputReference = create() }),
                _ => CanEdit ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
            Add("Float", () => new Vector1ShaderProperty());
            Add("Vector 2", () => new Vector2ShaderProperty()); Add("Vector 3", () => new Vector3ShaderProperty());
            Add("Vector 4", () => new Vector4ShaderProperty()); Add("Boolean", () => new BooleanShaderProperty());
            Add("Color", () => new ColorShaderProperty()); Add("Gradient", () => new GradientShaderProperty());
            Add("Texture 2D", () => new Texture2DShaderProperty()); Add("Texture 2D Array", () => new Texture2DArrayShaderProperty());
            Add("Texture 3D", () => new Texture3DShaderProperty()); Add("Cubemap", () => new CubemapShaderProperty());
            Add("Matrix 2", () => new Matrix2ShaderProperty()); Add("Matrix 3", () => new Matrix3ShaderProperty());
            Add("Matrix 4", () => new Matrix4ShaderProperty()); Add("Sampler State", () => new SamplerStateShaderProperty());
            Add("Virtual Texture", () => new VirtualTextureShaderProperty());
            menu.AppendAction("Keyword/Boolean", _ => AddProperty(new AddShaderInputAction { shaderInputReference = new ShaderKeyword(KeywordType.Boolean) }),
                _ => CanEdit ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
            menu.AppendAction("Keyword/Enum", _ => AddProperty(new AddShaderInputAction { shaderInputReference = new ShaderKeyword(KeywordType.Enum) }),
                _ => CanEdit ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
            menu.AppendAction("Dropdown (Sub Graph)", _ => AddProperty(new AddShaderInputAction { shaderInputReference = new ShaderDropdown() }),
                _ => CanEdit && graph.isSubGraph ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
        }

        void HoverProperty(string id, bool hovered)
        {
            if (hovered) hoveredProperty = id;
            else if (hoveredProperty == id) hoveredProperty = null;
            foreach (var pair in nodeHover)
                pair.Key.EnableInClassList("whimshader-parameter-node-hover", (pair.Key.node as PropertyNode)?.property?.objectId == hoveredProperty && hoveredProperty != null);
            foreach (var element in definitions.Query<Foldout>(className: "whimshader-parameter-block").ToList())
            {
                var item = layout?.Find(element.name.Substring("parameter-item-".Length));
                element.EnableInClassList("whimshader-parameter-block--hover", hoveredProperty != null && item?.propertyId == hoveredProperty);
            }
        }

        void RefreshNodeHoverBindings()
        {
            var current = graphView.Query<PropertyNodeView>().ToList();
            foreach (var view in nodeHover.Keys.Where(x => !current.Contains(x)).ToList())
            { nodeHover[view].Dispose(); nodeHover.Remove(view); }
            foreach (var view in current)
                if (!nodeHover.ContainsKey(view)) nodeHover.Add(view, new NodeHover(this, view));
        }

        void DisposeNodeHoverBindings()
        {
            foreach (var binding in nodeHover.Values) binding.Dispose();
            nodeHover.Clear(); hoveredProperty = null;
        }

        sealed class NodeHover : IDisposable
        {
            readonly WhimShaderParametersPanel owner;
            readonly PropertyNodeView view;
            internal NodeHover(WhimShaderParametersPanel owner, PropertyNodeView view)
            {
                this.owner = owner; this.view = view;
                view.RegisterCallback<MouseEnterEvent>(Enter); view.RegisterCallback<MouseLeaveEvent>(Leave);
            }
            void Enter(MouseEnterEvent evt) { owner.HoverProperty((view.node as PropertyNode)?.property?.objectId, true); }
            void Leave(MouseLeaveEvent evt) { owner.HoverProperty((view.node as PropertyNode)?.property?.objectId, false); }
            public void Dispose()
            {
                view.UnregisterCallback<MouseEnterEvent>(Enter); view.UnregisterCallback<MouseLeaveEvent>(Leave);
                view.RemoveFromClassList("whimshader-parameter-node-hover");
            }
        }
    }
}
