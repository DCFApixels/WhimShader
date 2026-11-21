// WhimTex catalog presentation; MIT, copyright 2026 Mikhail.
using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
namespace DCFApixels.WhimShader {
    internal sealed class ShaderGraphCatalogIcon : VisualElement {
        internal readonly struct Appearance {
            internal readonly Color color;
            internal readonly WhimShaderIcons.Definition icon;
            internal readonly bool group;
            internal readonly string kind;
            internal Appearance(string path, WhimShaderIcons.Definition icon, bool group, string kind) {
                color = CategoryColor(path); this.icon = icon; this.group = group; this.kind = kind;
            }
        }
        internal static Appearance ForCategory(string path) => new(path, WhimShaderIcons.Get(CategoryIcon(path)), true, null);
        internal static Appearance ForNode(string path, string kind) {
            var symbol = kind switch {
                "Add" => WhimShaderIcons.Kind.Add, "Subtract" or "Negate" => WhimShaderIcons.Kind.Subtract,
                "Multiply" => WhimShaderIcons.Kind.Multiply, "Divide" => WhimShaderIcons.Kind.Divide,
                "Power" => WhimShaderIcons.Kind.Power, "SquareRoot" => WhimShaderIcons.Kind.SquareRoot,
                "Absolute" => WhimShaderIcons.Kind.Absolute, "OneMinus" => WhimShaderIcons.Kind.OneMinus,
                "Minimum" => WhimShaderIcons.Kind.Minimum, "Maximum" => WhimShaderIcons.Kind.Maximum,
                "Exponential" => WhimShaderIcons.Kind.Exponential, "Log" => WhimShaderIcons.Kind.Logarithm,
                "And" => WhimShaderIcons.Kind.And, "Or" => WhimShaderIcons.Kind.Or, "Not" => WhimShaderIcons.Kind.Not,
                "Compare" => WhimShaderIcons.Kind.Compare, "Reciprocal" => WhimShaderIcons.Kind.Reciprocal,
                "Time" => WhimShaderIcons.Kind.Time, "Split" => WhimShaderIcons.Kind.Split,
                "Combine" or "Append" => WhimShaderIcons.Kind.Combine, "Expression" or "CustomFunction" => WhimShaderIcons.Kind.Code,
                _ => CategoryIcon(path)
            };
            return new(path, WhimShaderIcons.Get(symbol), false, kind);
        }
        internal static Color32 CategoryColor(string path) {
            string root = path.Split('/')[0];
            return root switch {
                "Color" or "Artistic" or "Filter" or "Normal" => new Color32(219,119,59,255),
                "Channel" => new Color32(151,209,61,255),
                "Input" or "Sampling" or "Parameters" or "Properties" => new Color32(203,48,34,255),
                "Math" => new Color32(75,146,243,255), "Procedural" => new Color32(156,79,255,255),
                "UV" => new Color32(8,215,139,255), _ => new Color32(174,174,174,255)
            };
        }
        private static WhimShaderIcons.Kind CategoryIcon(string path)
        {
            if (path.StartsWith("HLSL", StringComparison.Ordinal)) return WhimShaderIcons.Kind.Code;
            if (path.StartsWith("Sub Graph", StringComparison.Ordinal)) return WhimShaderIcons.Kind.Graph;
            int separator = path.LastIndexOf('/');
            string name = separator < 0 ? path : path.Substring(separator + 1);
            return name switch
            {
                "Math" or "Basic" or "Advanced" or "Derivative" => WhimShaderIcons.Kind.Function,
                "Trigonometry" or "Wave" => WhimShaderIcons.Kind.Wave, "Round" => WhimShaderIcons.Kind.Step,
                "Range" => WhimShaderIcons.Kind.Range, "Interpolation" => WhimShaderIcons.Kind.Curve,
                "Matrix" => WhimShaderIcons.Kind.Matrix, "Vector" or "Quaternion" or "UV" or "Distortion" => WhimShaderIcons.Kind.Vector,
                "Noise" or "Grain" or "Warp" or "Random" => WhimShaderIcons.Kind.Noise,
                "Procedural" or "Pattern" or "Scatter" => WhimShaderIcons.Kind.Pattern, "Shape" => WhimShaderIcons.Kind.Shape,
                "Channel" => WhimShaderIcons.Kind.Split, "Logic" => WhimShaderIcons.Kind.Logic,
                "Color" or "Adjustment" or "Adjustments" or "Conversion" or "Mapping" or "Selection" or "Mixing" or "Alpha" or "Generation" => WhimShaderIcons.Kind.Color,
                "Filter" => WhimShaderIcons.Kind.Filter, "Normal" => WhimShaderIcons.Kind.Normal, "Lighting" => WhimShaderIcons.Kind.Light,
                "Input" or "Parameters" => WhimShaderIcons.Kind.Input, "Sampling" => WhimShaderIcons.Kind.Texture,
                "Loop" => WhimShaderIcons.Kind.Loop, _ => WhimShaderIcons.Kind.Graph
            };
        }

        private static StyleSheet sheet;
        private readonly WhimShaderIcon icon;
        private Appearance appearance;
        private bool bound;

        internal ShaderGraphCatalogIcon()
        {
            pickingMode = PickingMode.Ignore;
            AddToClassList("whimshader-graph-catalog-icon");
            if (sheet == null) sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>("Packages/com.dcfapixels.whimshader/Editor/UI/Catalog/ShaderGraphCatalogIcon.uss");
            if (sheet != null) styleSheets.Add(sheet);
            icon = new WhimShaderIcon(); Add(icon);
        }

        internal void Bind(Appearance value)
        {
            if (bound && appearance.color == value.color && appearance.icon == value.icon && appearance.group == value.group) return;
            appearance = value; bound = true;
            icon.Bind(value.icon);
        }
    }
}
