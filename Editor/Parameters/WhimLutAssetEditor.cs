using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace DCFApixels.WhimShader
{
    [CustomEditor(typeof(WhimLutAsset))]
    sealed class WhimLutAssetEditor : UnityEditor.Editor
    {
        public override VisualElement CreateInspectorGUI()
        {
            var asset = (WhimLutAsset)target;
            var root = new VisualElement();
            if (!AssetDatabase.IsMainAsset(asset))
            {
                root.Add(new Label("Edit this default in the graph's LUT property."));
                return root;
            }

            var data = asset.Data;
            GradientField gradientField = null;
            CurveField curveField = null;
            if (data.kind == WhimLutKind.Gradient)
            {
                var gradient = new GradientField("Gradient") { value = data.gradient, hdr = true, colorSpace = ColorSpace.Linear };
                gradientField = gradient;
                gradient.AddToClassList(GradientField.alignedFieldUssClassName);
                gradient.RegisterValueChangedCallback(evt =>
                {
                    var changed = asset.Data;
                    changed.gradient = evt.newValue;
                    WhimLutMaterialStore.UpdateAsset(asset, changed);
                });
                root.Add(gradient);
            }
            else
            {
                var curve = new CurveField("Curve") { value = data.curve };
                curveField = curve;
                curve.AddToClassList(CurveField.alignedFieldUssClassName);
                curve.RegisterValueChangedCallback(evt =>
                {
                    var changed = asset.Data;
                    changed.curve = evt.newValue;
                    WhimLutMaterialStore.UpdateAsset(asset, changed);
                });
                root.Add(curve);
            }

            var resolution = new IntegerField("Resolution") { value = data.resolution, isDelayed = true };
            resolution.AddToClassList(IntegerField.alignedFieldUssClassName);
            resolution.RegisterValueChangedCallback(evt =>
            {
                int width = Mathf.Clamp(evt.newValue, WhimLutData.MinResolution, WhimLutData.MaxResolution);
                resolution.SetValueWithoutNotify(width);
                if (width == asset.Data.resolution) return;
                var changed = asset.Data;
                changed.resolution = width;
                WhimLutMaterialStore.UpdateAsset(asset, changed);
            });
            root.Add(resolution);
            root.SetEnabled(AssetDatabase.IsOpenForEdit(asset));
            void RefreshAfterUndo()
            {
                if (asset == null) return;
                var restored = asset.Data;
                gradientField?.SetValueWithoutNotify(restored.gradient);
                curveField?.SetValueWithoutNotify(restored.curve);
                resolution.SetValueWithoutNotify(restored.resolution);
            }
            root.RegisterCallback<AttachToPanelEvent>(_ => Undo.undoRedoPerformed += RefreshAfterUndo);
            root.RegisterCallback<DetachFromPanelEvent>(_ => Undo.undoRedoPerformed -= RefreshAfterUndo);
            return root;
        }
    }
}
