using System;
using UnityEditor.Graphing;
using UnityEditor.ShaderGraph;
using UnityEditor.ShaderGraph.Drawing;
using UnityEditor.ShaderGraph.Drawing.Inspector;
using UnityEditor.ShaderGraph.Drawing.Inspector.PropertyDrawers;
using UnityEditor.ShaderGraph.Internal;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using static UnityEditor.ShaderGraph.Drawing.Inspector.PropertyDrawers.ShaderInputPropertyDrawer;

namespace DCFApixels.WhimShader
{
    [Serializable]
    abstract class WhimLutShaderProperty : Texture2DShaderProperty, IShaderPropertyDrawer
    {
        [SerializeField] WhimLutData m_LutData = new WhimLutData();

        public abstract WhimLutKind LutKind { get; }
        protected abstract string DrawerAttribute { get; }
        public WhimLutData Data => m_LutData.Clone();

        protected WhimLutShaderProperty(WhimLutKind kind)
        {
            m_LutData.kind = kind;
            useTilingAndOffset = false;
            useTexelSize = true;
            generatePropertyBlock = true;
            defaultType = DefaultType.White;
        }

        public void SetData(WhimLutData data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            data.Validate();
            if (data.kind != LutKind) throw new ArgumentException("LUT kind does not match this property.", nameof(data));
            m_LutData = data.Clone();
        }

        internal override string GetPropertyBlockString() => DrawerAttribute + base.GetPropertyBlockString();

        internal override PreviewProperty GetPreviewMaterialProperty()
        {
            return new PreviewProperty(PropertyType.Texture2D)
            {
                name = referenceName,
                textureValue = WhimLutGraphProperties.GetPreviewTexture(this),
                texture2DDefaultType = defaultType
            };
        }

        protected T CopyTo<T>(T copy) where T : WhimLutShaderProperty
        {
            copy.displayName = displayName;
            copy.overrideReferenceName = overrideReferenceName;
            copy.generatePropertyBlock = generatePropertyBlock;
            copy.hidden = hidden;
            copy.PerRendererData = PerRendererData;
            copy.precision = precision;
            copy.overrideHLSLDeclaration = overrideHLSLDeclaration;
            copy.hlslDeclarationOverride = hlslDeclarationOverride;
            copy.SetData(Data);
            // Do not copy a transient preview or an importer-owned texture reference.
            return copy;
        }

        void IShaderPropertyDrawer.HandlePropertyField(PropertySheet sheet,
            PreChangeValueCallback preChange, PostChangeValueCallback postChange)
        {
            if (LutKind == WhimLutKind.Gradient)
            {
                var field = new GradientField { value = Data.gradient, hdr = true, colorSpace = ColorSpace.Linear };
                field.RegisterValueChangedCallback(evt =>
                {
                    preChange("Change Gradient LUT");
                    var data = Data;
                    data.gradient = evt.newValue;
                    SetData(data);
                    postChange(false, ModificationScope.Node);
                });
                AddNativeRow(sheet, "Default", field);
            }
            else
            {
                var field = new CurveField { value = Data.curve };
                field.RegisterValueChangedCallback(evt =>
                {
                    preChange("Change Curve LUT");
                    var data = Data;
                    data.curve = evt.newValue;
                    SetData(data);
                    postChange(false, ModificationScope.Node);
                });
                AddNativeRow(sheet, "Default", field);
            }

            var resolution = new IntegerField { value = Data.resolution, isDelayed = true };
            resolution.RegisterValueChangedCallback(evt =>
            {
                int width = Mathf.Clamp(evt.newValue, WhimLutData.MinResolution, WhimLutData.MaxResolution);
                resolution.SetValueWithoutNotify(width);
                if (width == Data.resolution) return;
                preChange("Change LUT Resolution");
                var data = Data;
                data.resolution = width;
                SetData(data);
                postChange(false, ModificationScope.Node);
            });
            AddNativeRow(sheet, "Resolution", resolution);
        }

        static void AddNativeRow(PropertySheet sheet, string label, VisualElement field)
        {
            // Same row and stylesheet used by native SG Texture2D/GradientPropertyDrawer.
            var row = new PropertyRow(PropertyDrawerUtils.CreateLabel(label, 0, FontStyle.Normal));
            row.Add(field);
            row.styleSheets.Add(Resources.Load<StyleSheet>("Styles/PropertyRow"));
            sheet.Add(row);
        }
    }

    [Serializable]
    [BlackboardInputInfo(31, "Gradient LUT")]
    sealed class WhimGradientShaderProperty : WhimLutShaderProperty
    {
        public WhimGradientShaderProperty() : base(WhimLutKind.Gradient) { displayName = "Gradient LUT"; }
        public override WhimLutKind LutKind => WhimLutKind.Gradient;
        protected override string DrawerAttribute => "[WhimGradient]";
        internal override ShaderInput Copy() => CopyTo(new WhimGradientShaderProperty());
    }

    [Serializable]
    [BlackboardInputInfo(32, "Curve LUT")]
    sealed class WhimCurveShaderProperty : WhimLutShaderProperty
    {
        public WhimCurveShaderProperty() : base(WhimLutKind.Curve) { displayName = "Curve LUT"; }
        public override WhimLutKind LutKind => WhimLutKind.Curve;
        protected override string DrawerAttribute => "[WhimCurve]";
        internal override ShaderInput Copy() => CopyTo(new WhimCurveShaderProperty());
    }
}
