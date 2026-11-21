// GI initialization adapted from URP 17.7.0 PBRForwardPass.hlsl.
// Copyright Unity Technologies ApS. Unity Companion License; see Unity-NOTICE.md.
#ifndef WHIM_LIGHTING_CONTEXT_INCLUDED
#define WHIM_LIGHTING_CONTEXT_INCLUDED
#define WHIM_SURFACE_CONTEXT 1

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

static InputData WhimFragmentInput;
static float2 WhimStaticLightmapUV;
static float2 WhimDynamicLightmapUV;
static half3 WhimVertexSH;
static float4 WhimProbeOcclusion;

InputData WhimInputForNormal(float3 normalWS)
{
    InputData data = WhimFragmentInput;
    data.normalWS = NormalizeNormalPerPixel(normalWS);
    // These are per-node samples: two differently oriented PBR nodes may share a graph.
#if defined(_SCREEN_SPACE_IRRADIANCE)
    data.bakedGI = SAMPLE_GI(_ScreenSpaceIrradiance, data.positionCS.xy, data.normalWS, GetInvPreExposureMultiplier());
#elif defined(DYNAMICLIGHTMAP_ON)
    data.bakedGI = SAMPLE_GI(WhimStaticLightmapUV, WhimDynamicLightmapUV, WhimVertexSH, data.normalWS);
    data.shadowMask = SAMPLE_SHADOWMASK(WhimStaticLightmapUV);
#elif !defined(LIGHTMAP_ON) && (defined(PROBE_VOLUMES_L1) || defined(PROBE_VOLUMES_L2))
    data.bakedGI = SAMPLE_GI(WhimVertexSH, GetAbsolutePositionWS(data.positionWS),
        data.normalWS, data.viewDirectionWS, data.positionCS.xy, WhimProbeOcclusion, data.shadowMask);
#else
    data.bakedGI = SAMPLE_GI(WhimStaticLightmapUV, WhimVertexSH, data.normalWS);
    data.shadowMask = SAMPLE_SHADOWMASK(WhimStaticLightmapUV);
#endif
    return data;
}
#endif
