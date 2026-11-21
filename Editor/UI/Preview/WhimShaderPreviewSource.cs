using System;
using System.Text.RegularExpressions;
using UnityEditor.ShaderGraph;

namespace DCFApixels.WhimShader
{
    /// <summary>Source adapter pinned to Shader Graph 17.7 GenerateSurfaceDescriptionRemap.</summary>
    internal static class WhimShaderPreviewSource
    {
        static readonly Regex Assignment = new(@"(?m)^[ \t]*surface\.Out[ \t]*=[^;\r\n]*;[ \t]*\r?$");
        const string ErrorColor = "float4(1.0f, 0.0f, 1.0f, 1.0f)";

        internal static bool Supports(MaterialSlot slot) => slot != null && slot.concreteValueType is
            ConcreteSlotValueType.Vector1 or ConcreteSlotValueType.Vector2 or ConcreteSlotValueType.Vector3 or
            ConcreteSlotValueType.Vector4 or ConcreteSlotValueType.Boolean;

        internal static string SelectOutput(string source, AbstractMaterialNode node, int slotId)
        {
            if (string.IsNullOrEmpty(source)) throw new InvalidOperationException("Native preview source is empty.");
            var slot = node.FindSlot<MaterialSlot>(slotId);
            if (!Supports(slot)) throw new InvalidOperationException("This output is not a numeric preview output.");
            var matches = Assignment.Matches(source);
            if (matches.Count != 1 || !matches[0].Value.Contains("all(isfinite(") || !matches[0].Value.Contains(ErrorColor))
                throw new InvalidOperationException("Shader Graph preview source no longer matches the pinned 17.7 output adapter.");

            // SubGraphOutput's ports are inputs. Its upstream expressions are emitted by native
            // generation even though the preview struct declares only the first output field.
            string raw = node.GetSlotValue(slotId, GenerationMode.Preview, node.concretePrecision);
            string adapted = node is SubGraphOutputNode
                ? GenerationUtils.AdaptNodeOutputForPreview(node, slotId, "(" + raw + ")")
                : GenerationUtils.AdaptNodeOutputForPreview(node, slotId);
            // Native 17.7 intentionally sets vector4 preview alpha to 1. Preserve its fourth
            // component here; the camera rerender alone cannot recover the discarded channel.
            if (slot.concreteValueType == ConcreteSlotValueType.Vector4) adapted = "float4(" + raw + ")";
            if (!node.isActive) adapted = "float4(0.0f, 0.0f, 0.0f, 0.0f)";
            string replacement = $"surface.Out = all(isfinite({raw})) ? {adapted} : {ErrorColor};";
            var match = matches[0];
            return source.Substring(0, match.Index) + replacement + source.Substring(match.Index + match.Length);
        }
    }
}
