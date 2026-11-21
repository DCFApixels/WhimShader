// Copied from WhimTex. Copyright (c) 2026 Mikhail, MIT. Only model/path adapters differ.
using UnityEditor;
using UnityEngine.UIElements;

namespace DCFApixels.WhimShader
{
    internal sealed class WhimShaderIcon : VisualElement
    {
        private static StyleSheet sheet;
        private WhimShaderIcons.Definition definition;

        internal WhimShaderIcon(WhimShaderIcons.Definition definition = null)
        {
            pickingMode = PickingMode.Ignore;
            AddToClassList("whimshader-icon");
            if (sheet == null) sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>("Packages/com.dcfapixels.whimshader/Editor/UI/Catalog/WhimShaderIcon.uss");
            if (sheet != null) styleSheets.Add(sheet);
            if (definition != null) Bind(definition);
        }

        internal void Bind(WhimShaderIcons.Definition value)
        {
            if (ReferenceEquals(definition, value)) return;
            definition = value;
            Clear();
            if (value == null) return;
            VisualElement visual;
            if (value.action.HasValue) visual = new LayerActionIcon(value.action.Value);
            else if (value.symbol != null)
                visual = new Label(value.symbol) { displayTooltipWhenElided = false };
            else
            {
                visual = new VisualElement();
                visual.generateVisualContent += Draw;
            }
            visual.pickingMode = PickingMode.Ignore;
            visual.AddToClassList("whimshader-icon-mark");
            if (value.symbol?.Length > 2) visual.AddToClassList("whimshader-icon-small-symbol");
            Add(visual);
        }

        private void Draw(MeshGenerationContext context) => WhimShaderIcons.Draw(definition.kind, context, resolvedStyle.color);
    }
}
