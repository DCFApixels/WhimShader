// Input initialization adapted from URP 17.7.0 PBRForwardPass.hlsl.
// Copyright Unity Technologies ApS. Unity Companion License; see Unity-NOTICE.md.
#ifndef WHIM_CONTEXT_FROM_VARYINGS_INCLUDED
#define WHIM_CONTEXT_FROM_VARYINGS_INCLUDED
void WhimPrepareLightingContext(Varyings input)
{
    WhimFragmentInput = (InputData)0;
    WhimFragmentInput.positionWS = input.positionWS;
    WhimFragmentInput.positionCS = input.positionCS;
    WhimFragmentInput.normalWS = NormalizeNormalPerPixel(input.normalWS);
    WhimFragmentInput.viewDirectionWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
    float crossSign = (input.tangentWS.w > 0.0 ? 1.0 : -1.0) * GetOddNegativeScale();
    WhimFragmentInput.tangentToWorld = half3x3(input.tangentWS.xyz,
        crossSign * cross(input.normalWS, input.tangentWS.xyz), input.normalWS);
#if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
    WhimFragmentInput.shadowCoord = input.shadowCoord;
#elif defined(MAIN_LIGHT_CALCULATE_SHADOWS)
    WhimFragmentInput.shadowCoord = TransformWorldToShadowCoord(input.positionWS);
#endif
    WhimFragmentInput.vertexLighting = input.fogFactorAndVertexLight.yzw;
    WhimFragmentInput.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
    WhimFragmentInput.shadowMask = half4(1, 1, 1, 1);
    WhimFragmentInput.preExposureMultiplier = GetPreExposureMultiplier();
    WhimStaticLightmapUV = 0;
    WhimDynamicLightmapUV = 0;
    WhimVertexSH = 0;
    WhimProbeOcclusion = 1;
#if defined(LIGHTMAP_ON)
    WhimStaticLightmapUV = input.staticLightmapUV;
#else
    WhimVertexSH = input.sh;
#endif
#if defined(DYNAMICLIGHTMAP_ON)
    WhimDynamicLightmapUV = input.dynamicLightmapUV;
#endif
#if defined(USE_APV_PROBE_OCCLUSION)
    WhimProbeOcclusion = input.probeOcclusion;
#endif
#if defined(DEBUG_DISPLAY)
    #if defined(LIGHTMAP_ON)
        WhimFragmentInput.staticLightmapUV = WhimStaticLightmapUV;
    #else
        WhimFragmentInput.vertexSH = WhimVertexSH;
    #endif
    #if defined(DYNAMICLIGHTMAP_ON)
        WhimFragmentInput.dynamicLightmapUV = WhimDynamicLightmapUV;
    #endif
    #if defined(USE_APV_PROBE_OCCLUSION)
        WhimFragmentInput.probeOcclusion = WhimProbeOcclusion;
    #endif
#endif
#ifdef VARYINGS_NEED_TEXCOORD0
    SETUP_DEBUG_TEXTURE_DATA(WhimFragmentInput, input.texCoord0);
#else
    SETUP_DEBUG_TEXTURE_DATA_NO_UV(WhimFragmentInput);
#endif
}
#endif
