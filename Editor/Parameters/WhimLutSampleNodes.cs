using UnityEditor.Graphing;
using UnityEditor.ShaderGraph;
using UnityEngine;

namespace DCFApixels.WhimShader
{
    abstract class WhimLutSampleNode : AbstractMaterialNode, IGeneratesBodyCode
    {
        const int TextureId = 0;
        const int TimeId = 1;
        const int OutputId = 2;
        protected abstract bool IsScalar { get; }

        protected WhimLutSampleNode(string title)
        {
            name = title;
            UpdateNodeAfterDeserialization();
        }

        public override bool hasPreview => true;

        public sealed override void UpdateNodeAfterDeserialization()
        {
            AddSlot(new Texture2DInputMaterialSlot(TextureId, "LUT", "LUT"));
            AddSlot(new Vector1MaterialSlot(TimeId, "T", "T", SlotType.Input, 0));
            if (IsScalar) AddSlot(new Vector1MaterialSlot(OutputId, "Out", "Out", SlotType.Output, 0));
            else AddSlot(new Vector4MaterialSlot(OutputId, "Out", "Out", SlotType.Output, Vector4.zero));
            RemoveSlotsNameNotMatching(new[] { TextureId, TimeId, OutputId });
        }

        public void GenerateNodeCode(ShaderStringBuilder builder, GenerationMode generationMode)
        {
            string lut = GetSlotValue(TextureId, generationMode);
            string t = GetSlotValue(TimeId, generationMode);
            string output = GetVariableNameForSlot(OutputId);
            string uv = output + "_UV";
            builder.AppendLine("float2 {0} = float2((saturate({1}) * ({2}.texelSize.z - 1.0) + 0.5) * {2}.texelSize.x, 0.5);", uv, t, lut);
            builder.AppendLine("{0} {1} = SAMPLE_TEXTURE2D_LOD({2}.tex, {2}.samplerstate, {3}, 0){4};",
                IsScalar ? "$precision" : "$precision4", output, lut, uv, IsScalar ? ".r" : "");
        }
    }

    [Title("Input", "Gradient", "Sample Gradient LUT")]
    sealed class SampleGradientLutNode : WhimLutSampleNode
    {
        public SampleGradientLutNode() : base("Sample Gradient LUT") { }
        protected override bool IsScalar => false;
    }

    [Title("Input", "Curve", "Sample Curve LUT")]
    sealed class SampleCurveLutNode : WhimLutSampleNode
    {
        public SampleCurveLutNode() : base("Sample Curve LUT") { }
        protected override bool IsScalar => true;
    }
}
