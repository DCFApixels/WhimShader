using UnityEditor.Graphing;
using UnityEditor.ShaderGraph;
using UnityEditor.ShaderGraph.Internal;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace DCFApixels.WhimShader
{
    internal abstract class WhimLightingNode : AbstractMaterialNode, IGeneratesBodyCode,
        IGeneratesFunction, IMayRequireNormal, IMayRequirePosition, IMayRequireViewDirection,
        IMayRequirePixelPosition, IMayRequireMeshUV
    {
        protected const ShaderStageCapability Fragment = ShaderStageCapability.Fragment;
        protected abstract int NormalSlot { get; }
        public override bool hasPreview => true;
        public override PreviewMode previewMode => PreviewMode.Preview3D;
        public abstract void GenerateNodeCode(ShaderStringBuilder builder, GenerationMode generationMode);
        public void GenerateNodeFunction(FunctionRegistry registry, GenerationMode generationMode)
            => registry.RequiresIncludePath(WhimSurfaceSubTarget.ShaderRoot + "WhimLighting.hlsl");
        public NeededCoordinateSpace RequiresNormal(ShaderStageCapability stageCapability = ShaderStageCapability.All)
            => (stageCapability & Fragment) != 0
                ? FindSlot<NormalMaterialSlot>(NormalSlot).RequiresNormal(stageCapability) : NeededCoordinateSpace.None;
        public NeededCoordinateSpace RequiresPosition(ShaderStageCapability stageCapability = ShaderStageCapability.All)
            => (stageCapability & Fragment) != 0 ? NeededCoordinateSpace.World : NeededCoordinateSpace.None;
        public NeededCoordinateSpace RequiresViewDirection(ShaderStageCapability stageCapability = ShaderStageCapability.All)
            => (stageCapability & Fragment) != 0 ? NeededCoordinateSpace.World : NeededCoordinateSpace.None;
        public bool RequiresPixelPosition(ShaderStageCapability stageCapability = ShaderStageCapability.All)
            => (stageCapability & Fragment) != 0;
        public bool RequiresMeshUV(UVChannel channel, ShaderStageCapability stageCapability = ShaderStageCapability.All)
            => (stageCapability & Fragment) != 0 && (channel == UVChannel.UV1 || channel == UVChannel.UV2);
    }

    [Title("Lighting", "Unity PBR")]
    [SRPFilter(typeof(UniversalRenderPipeline))]
    [SubTargetFilter(typeof(WhimSurfaceSubTarget))]
    internal sealed class UnityPBRNode : WhimLightingNode
    {
        internal const int BaseColorSlot = 0, WorldNormalSlot = 1, MetallicSlot = 2,
            SmoothnessSlot = 3, OcclusionSlot = 4, EmissionSlot = 5, ColorSlot = 6;
        protected override int NormalSlot => WorldNormalSlot;
        public UnityPBRNode() { name = "Unity PBR"; UpdateNodeAfterDeserialization(); }
        public override void UpdateNodeAfterDeserialization()
        {
            AddSlot(new ColorRGBMaterialSlot(BaseColorSlot, "Base Color", "BaseColor", SlotType.Input,
                Color.grey, ColorMode.Default, Fragment));
            AddSlot(new NormalMaterialSlot(WorldNormalSlot, "Normal (World)", "NormalWS", CoordinateSpace.World, Fragment));
            AddSlot(new Vector1MaterialSlot(MetallicSlot, "Metallic", "Metallic", SlotType.Input, 0, Fragment));
            AddSlot(new Vector1MaterialSlot(SmoothnessSlot, "Smoothness", "Smoothness", SlotType.Input, .5f, Fragment));
            AddSlot(new Vector1MaterialSlot(OcclusionSlot, "Occlusion", "Occlusion", SlotType.Input, 1, Fragment));
            AddSlot(new ColorRGBMaterialSlot(EmissionSlot, "Emission", "Emission", SlotType.Input,
                Color.black, ColorMode.HDR, Fragment));
            AddSlot(new Vector3MaterialSlot(ColorSlot, "Color", "Color", SlotType.Output, Vector3.zero, Fragment));
            RemoveSlotsNameNotMatching(new[] { BaseColorSlot, WorldNormalSlot, MetallicSlot,
                SmoothnessSlot, OcclusionSlot, EmissionSlot, ColorSlot });
        }
        public override void GenerateNodeCode(ShaderStringBuilder builder, GenerationMode generationMode)
        {
            string V(int slot) => GetSlotValue(slot, generationMode);
            builder.AppendLine("$precision3 {0} = WhimUnityPBR({1}, {2}, {3}, {4}, {5}, {6});",
                GetVariableNameForSlot(ColorSlot), V(BaseColorSlot), V(WorldNormalSlot),
                V(MetallicSlot), V(SmoothnessSlot), V(OcclusionSlot), V(EmissionSlot));
        }
    }

    [Title("Lighting", "Scene Lighting")]
    [SRPFilter(typeof(UniversalRenderPipeline))]
    [SubTargetFilter(typeof(WhimSurfaceSubTarget))]
    internal sealed class SceneLightingNode : WhimLightingNode
    {
        protected override int NormalSlot => 0;
        static readonly string[] Names = { "Main Direction", "Main Color", "Main Distance Attenuation",
            "Main Shadow Attenuation", "Additional Diffuse", "Baked GI", "Vertex Lighting", "Shadow Mask",
            "Direct AO", "Indirect AO" };
        static readonly string[] Fields = { "mainDirection", "mainColor", "mainDistanceAttenuation",
            "mainShadowAttenuation", "additionalDiffuse", "bakedGI", "vertexLighting", "shadowMask",
            "directAO", "indirectAO" };
        static readonly int[] Widths = { 3, 3, 1, 1, 3, 3, 3, 4, 1, 1 };
        public SceneLightingNode() { name = "Scene Lighting"; UpdateNodeAfterDeserialization(); }
        public override void UpdateNodeAfterDeserialization()
        {
            AddSlot(new NormalMaterialSlot(0, "Normal (World)", "NormalWS", CoordinateSpace.World, Fragment));
            for (int i = 0; i < Names.Length; i++)
            {
                int id = i + 1;
                if (Widths[i] == 1)
                    AddSlot(new Vector1MaterialSlot(id, Names[i], Fields[i], SlotType.Output, 0, Fragment));
                else if (Widths[i] == 4)
                    AddSlot(new Vector4MaterialSlot(id, Names[i], Fields[i], SlotType.Output, Vector4.one, Fragment));
                else AddSlot(new Vector3MaterialSlot(id, Names[i], Fields[i], SlotType.Output, Vector3.zero, Fragment));
            }
            RemoveSlotsNameNotMatching(new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 });
        }
        public override void GenerateNodeCode(ShaderStringBuilder builder, GenerationMode generationMode)
        {
            string local = GetVariableNameForSlot(1) + "_scene";
            builder.AppendLine("WhimSceneLightingData {0} = WhimSceneLighting({1});", local, GetSlotValue(0, generationMode));
            for (int i = 0; i < Names.Length; i++)
                builder.AppendLine("$precision{0} {1} = {2}.{3};", Widths[i] == 1 ? "" : Widths[i].ToString(),
                    GetVariableNameForSlot(i + 1), local, Fields[i]);
        }
    }
}
