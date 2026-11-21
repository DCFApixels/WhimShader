using UnityEditor.Graphing;
using UnityEditor.ShaderGraph;
using UnityEngine;

namespace DCFApixels.WhimShader
{
    abstract class WhimSoftSignNode : AbstractMaterialNode, IGeneratesBodyCode
    {
        const int InputId = 0;
        const int OutputId = 1;
        protected abstract bool IsInverse { get; }

        protected WhimSoftSignNode(string title)
        {
            name = title;
            UpdateNodeAfterDeserialization();
        }

        public override bool hasPreview => true;

        public sealed override void UpdateNodeAfterDeserialization()
        {
            AddSlot(new DynamicVectorMaterialSlot(InputId, "In", "In", SlotType.Input, Vector4.zero));
            AddSlot(new DynamicVectorMaterialSlot(OutputId, "Out", "Out", SlotType.Output, Vector4.zero));
            RemoveSlotsNameNotMatching(new[] { InputId, OutputId });
        }

        public void GenerateNodeCode(ShaderStringBuilder builder, GenerationMode generationMode)
        {
            string type = FindOutputSlot<DynamicVectorMaterialSlot>(OutputId).concreteValueType.ToShaderString();
            builder.AppendLine("{0} {1} = ({2}) / (1.0 {3} abs({2}));",
                type, GetVariableNameForSlot(OutputId), GetSlotValue(InputId, generationMode), IsInverse ? "-" : "+");
        }
    }

    [Title("Math", "Advanced", "SoftSign")]
    sealed class SoftSignNode : WhimSoftSignNode
    {
        public SoftSignNode() : base("SoftSign") { }
        protected override bool IsInverse => false;
    }

    /// <summary>
    /// Inverse of SoftSign for each input component strictly between -1 and 1.
    /// The expression is intentionally unclamped and is singular at -1 and 1.
    /// </summary>
    [Title("Math", "Advanced", "InverseSoftSign")]
    sealed class InverseSoftSignNode : WhimSoftSignNode
    {
        public InverseSoftSignNode() : base("InverseSoftSign") { }
        protected override bool IsInverse => true;
    }
}
