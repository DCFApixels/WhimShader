using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.ShaderGraph;
using UnityEditor.ShaderGraph.Internal;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace DCFApixels.WhimShader
{
    // Shared by parameter definitions and the value inspector. Transactions are supplied by
    // the caller; field drawing has no dependency on GraphData, a window or layout metadata.
    // Material inspectors use the same entry point below with Unity's own IMGUI property drawers.
    internal static class WhimShaderPropertyFields
    {
        internal delegate VisualElement DefaultDrawer(AbstractShaderProperty property, string label,
            Action<string, Action> edit, List<Action> refresh);
        static readonly List<DefaultDrawer> drawers = new();
        internal static void RegisterDefaultDrawer(DefaultDrawer drawer) { if (!drawers.Contains(drawer)) drawers.Add(drawer); }
        internal static void UnregisterDefaultDrawer(DefaultDrawer drawer) => drawers.Remove(drawer);

        internal static bool IsMaterialPropertyVisible(MaterialProperty property) => property != null &&
            (property.propertyFlags & ShaderPropertyFlags.HideInInspector) == 0;

        internal static void DrawMaterial(MaterialEditor editor, MaterialProperty property)
        {
            if (!IsMaterialPropertyVisible(property)) return;
            using (new EditorGUI.DisabledScope((property.propertyFlags & ShaderPropertyFlags.PerRendererData) != 0))
            {
                // Native MaterialPropertyDrawer dispatch preserves LUT drawers, mixed values,
                // animation and Undo without inspecting Unity internals or reimplementing fields.
                editor.ShaderProperty(property, property.displayName);
            }
        }

        internal static VisualElement Create(AbstractShaderProperty property, string label, Action<string, Action> edit, List<Action> refresh)
        {
            foreach (var drawer in drawers)
            {
                var custom = drawer(property, label, edit, refresh);
                if (custom != null) return custom;
            }
            switch (property)
            {
                case Vector1ShaderProperty p: return Float(p, label, edit, refresh);
                case Vector2ShaderProperty p:
                    return Bind(new Vector2Field(label), () => (Vector2)p.value, v => p.value = new Vector4(v.x, v.y, p.value.z, p.value.w), edit, refresh);
                case Vector3ShaderProperty p:
                    return Bind(new Vector3Field(label), () => (Vector3)p.value, v => p.value = new Vector4(v.x, v.y, v.z, p.value.w), edit, refresh);
                case Vector4ShaderProperty p: return Bind(new Vector4Field(label), () => p.value, v => p.value = v, edit, refresh);
                case BooleanShaderProperty p: return Bind(new Toggle(label), () => p.value, v => p.value = v, edit, refresh);
                case ColorShaderProperty p:
                    return Bind(new ColorField(label) { hdr = p.colorMode == ColorMode.HDR, showAlpha = true }, () => p.value, v => p.value = v, edit, refresh);
                case Texture2DShaderProperty p: return Texture(label, typeof(Texture), () => p.value.texture, v => p.value.texture = (Texture)v, edit, refresh);
                case Texture2DArrayShaderProperty p: return Texture(label, typeof(Texture2DArray), () => p.value.textureArray, v => p.value.textureArray = (Texture2DArray)v, edit, refresh);
                case Texture3DShaderProperty p: return Texture(label, typeof(Texture3D), () => p.value.texture, v => p.value.texture = (Texture3D)v, edit, refresh);
                case CubemapShaderProperty p: return Texture(label, typeof(Cubemap), () => p.value.cubemap, v => p.value.cubemap = (Cubemap)v, edit, refresh);
                case GradientShaderProperty p: return Bind(new GradientField(label), () => p.value, v => p.value = v, edit, refresh);
                case Matrix2ShaderProperty p: return Matrix(label, 2, () => p.value, v => p.value = v, edit, refresh);
                case Matrix3ShaderProperty p: return Matrix(label, 3, () => p.value, v => p.value = v, edit, refresh);
                case Matrix4ShaderProperty p: return Matrix(label, 4, () => p.value, v => p.value = v, edit, refresh);
                case SamplerStateShaderProperty p:
                {
                    var root = Group(label);
                    root.Add(Bind(new EnumField("Filter", p.value.filter), () => (Enum)p.value.filter, v => p.value.filter = (TextureSamplerState.FilterMode)v, edit, refresh));
                    root.Add(Bind(new EnumField("Wrap", p.value.wrap), () => (Enum)p.value.wrap, v => p.value.wrap = (TextureSamplerState.WrapMode)v, edit, refresh));
                    root.Add(Bind(new EnumField("Anisotropic", p.value.anisotropic), () => (Enum)p.value.anisotropic, v => p.value.anisotropic = (TextureSamplerState.Anisotropic)v, edit, refresh));
                    return root;
                }
                case VirtualTextureShaderProperty p:
                {
                    var root = Group(label);
                    foreach (var layer in p.value.layers)
                    {
                        var retained = layer;
                        root.Add(Texture(retained.layerName, typeof(Texture), () => retained.layerTexture.texture,
                            v => retained.layerTexture.texture = (Texture)v, edit, refresh));
                    }
                    return root;
                }
                default: return null; // The panel falls back to the native extensible property drawer.
            }
        }

        internal static BaseField<T> Bind<T>(BaseField<T> field, Func<T> read, Action<T> write,
            Action<string, Action> edit, List<Action> refresh)
        {
            field.AddToClassList(BaseField<T>.alignedFieldUssClassName);
            field.AddToClassList("whimshader-parameter-field");
            field.SetValueWithoutNotify(read());
            field.RegisterValueChangedCallback(evt => edit("Change Shader Parameter", () => write(evt.newValue)));
            refresh.Add(() =>
            {
                var focused = field.panel?.focusController?.focusedElement as VisualElement;
                if (focused == field || focused != null && field.Contains(focused)) return;
                field.SetValueWithoutNotify(read());
            });
            return field;
        }

        static VisualElement Texture(string label, Type type, Func<Object> read, Action<Object> write, Action<string, Action> edit, List<Action> refresh)
            => Bind(new ObjectField(label) { objectType = type, allowSceneObjects = false }, read, write, edit, refresh);

        static VisualElement Group(string label)
        {
            var root = new VisualElement(); root.AddToClassList("whimshader-parameter-compound");
            if (!string.IsNullOrEmpty(label)) { var title = new Label(label); title.AddToClassList("whimshader-parameter-heading"); root.Add(title); }
            return root;
        }

        static VisualElement Float(Vector1ShaderProperty p, string label, Action<string, Action> edit, List<Action> refresh)
        {
            if (p.floatType == FloatType.Integer)
                return Bind(new IntegerField(label), () => (int)p.value, v => p.value = v, edit, refresh);
            if (p.floatType == FloatType.Slider && p.rangeValues.x < p.rangeValues.y)
            {
                if (p.sliderType == SliderType.Integer)
                    return Bind(new SliderInt(label, Mathf.CeilToInt(p.rangeValues.x), Mathf.FloorToInt(p.rangeValues.y)) { showInputField = true },
                        () => Mathf.RoundToInt(p.value), v => p.value = v, edit, refresh);
                // Power range defaults are edited numerically; native settings retains its power metadata.
                if (p.sliderType != SliderType.Power)
                    return Bind(new Slider(label, p.rangeValues.x, p.rangeValues.y) { showInputField = true }, () => p.value, v => p.value = v, edit, refresh);
            }
            if (p.floatType == FloatType.Enum && p.enumNames.Count > 0)
            {
                var names = new List<string>(p.enumNames);
                var values = new List<int>(p.enumValues);
                var field = new PopupField<string>(label, names, 0);
                void Sync()
                {
                    int index = values.IndexOf((int)p.value);
                    // Preserve an out-of-list serialized value until the user selects an entry.
                    field.SetValueWithoutNotify(index >= 0 && index < names.Count ? names[index] : p.value.ToString());
                }
                Sync(); refresh.Add(Sync);
                field.AddToClassList(BaseField<string>.alignedFieldUssClassName);
                field.RegisterValueChangedCallback(evt =>
                {
                    int index = names.IndexOf(evt.newValue);
                    if (index >= 0) edit("Change Shader Parameter", () => p.value = index < values.Count ? values[index] : index);
                });
                return field;
            }
            return Bind(new UnityEngine.UIElements.FloatField(label), () => p.value, v => p.value = v, edit, refresh);
        }

        static VisualElement Matrix(string label, int dimension, Func<Matrix4x4> read, Action<Matrix4x4> write,
            Action<string, Action> edit, List<Action> refresh)
        {
            var root = Group(label);
            for (int row = 0; row < dimension; row++)
            {
                int r = row;
                void Set(Vector4 value) { var matrix = read(); matrix.SetRow(r, value); write(matrix); }
                string title = "Row " + (row + 1);
                if (dimension == 2) root.Add(Bind(new Vector2Field(title), () => (Vector2)read().GetRow(r), v =>
                { var current = read().GetRow(r); Set(new Vector4(v.x, v.y, current.z, current.w)); }, edit, refresh));
                else if (dimension == 3) root.Add(Bind(new Vector3Field(title), () => (Vector3)read().GetRow(r), v =>
                { var current = read().GetRow(r); Set(new Vector4(v.x, v.y, v.z, current.w)); }, edit, refresh));
                else root.Add(Bind(new Vector4Field(title), () => read().GetRow(r), Set, edit, refresh));
            }
            return root;
        }
    }
}
