using UnityEditor;
using UnityEngine;

namespace DCFApixels.WhimShader
{
    public abstract class WhimLutMaterialDrawer : MaterialPropertyDrawer
    {
        protected abstract WhimLutKind Kind { get; }

        public override float GetPropertyHeight(MaterialProperty property, string label, MaterialEditor editor)
            => EditorGUIUtility.singleLineHeight;

        public override void OnGUI(Rect position, MaterialProperty property, string label, MaterialEditor editor)
        {
            var asset = WhimLutMaterialStore.Find(property.textureValue);
            var data = asset != null && asset.Data.kind == Kind ? asset.Data : new WhimLutData { kind = Kind };
            bool editable = property.targets.Length > 0;
            foreach (var target in property.targets)
                editable &= target is Material material && WhimLutMaterialStore.CanEdit(material);

            bool oldMixedValue = EditorGUI.showMixedValue;
            EditorGUI.showMixedValue = property.hasMixedValue;
            try
            {
                using (new EditorGUI.DisabledScope(!editable))
                {
                    EditorGUI.BeginChangeCheck();
                    if (Kind == WhimLutKind.Gradient)
                        data.gradient = EditorGUI.GradientField(position, new GUIContent(label), data.gradient, true);
                    else
                        data.curve = EditorGUI.CurveField(position, label, data.curve);
                    if (EditorGUI.EndChangeCheck())
                    {
                        editor.RegisterPropertyChangeUndo("Edit LUT Parameter");
                        foreach (var target in property.targets)
                            if (target is Material material) WhimLutMaterialStore.Apply(material, property.name, data);
                        editor.Repaint();
                    }
                }
            }
            finally { EditorGUI.showMixedValue = oldMixedValue; }
        }
    }

    // MaterialPropertyDrawer discovers these by the [WhimGradient]/[WhimCurve] shader attributes.
    public sealed class WhimGradientDrawer : WhimLutMaterialDrawer
    {
        protected override WhimLutKind Kind => WhimLutKind.Gradient;
    }

    public sealed class WhimCurveDrawer : WhimLutMaterialDrawer
    {
        protected override WhimLutKind Kind => WhimLutKind.Curve;
    }
}
