// Scene light traversal adapted from URP 17.7.0 Lighting.hlsl.
// Copyright Unity Technologies ApS. Unity Companion License; see Unity-NOTICE.md.
#ifndef WHIM_LIGHTING_INCLUDED
#define WHIM_LIGHTING_INCLUDED

struct WhimSceneLightingData
{
    float3 mainDirection;
    float3 mainColor;
    float mainDistanceAttenuation;
    float mainShadowAttenuation;
    float3 additionalDiffuse;
    float3 bakedGI;
    float3 vertexLighting;
    float4 shadowMask;
    float directAO;
    float indirectAO;
};

float3 WhimUnityPBR(float3 baseColor, float3 normalWS, float metallic,
    float smoothness, float occlusion, float3 emission)
{
#if defined(WHIM_SURFACE_CONTEXT) && !defined(SHADERGRAPH_PREVIEW)
    InputData inputData = WhimInputForNormal(normalWS);
    SurfaceData surface = (SurfaceData)0;
    surface.albedo = baseColor;
    surface.metallic = saturate(metallic);
    surface.specular = 0;
    surface.smoothness = saturate(smoothness);
    surface.occlusion = saturate(occlusion);
    surface.emission = emission;
    surface.alpha = 1; // Final Alpha owns blending; never premultiply a lighting node.
    surface.normalTS = half3(0, 0, 1);
    surface.clearCoatSmoothness = 1;
#if defined(_DBUFFER)
    ApplyDecalToSurfaceData(inputData.positionCS, surface, inputData);
    // Decals can change the normal, so evaluate directional GI for that normal.
    InputData decalGI = WhimInputForNormal(inputData.normalWS);
    inputData.bakedGI = decalGI.bakedGI;
    inputData.shadowMask = decalGI.shadowMask;
#endif
    return UniversalFragmentPBR(inputData, surface).rgb;
#else
    float3 n = normalWS * rsqrt(max(dot(normalWS, normalWS), 1e-8));
    float3 l = normalize(float3(-0.4, 0.6, 0.7));
    float3 h = normalize(l + float3(0, 0, 1));
    float spec = pow(saturate(dot(n, h)), exp2(1 + 10 * saturate(smoothness)));
    return baseColor * (1 - saturate(metallic)) * (0.2 * saturate(occlusion) + saturate(dot(n, l)))
        + lerp(float3(0.04, 0.04, 0.04), baseColor, saturate(metallic)) * spec + emission;
#endif
}

WhimSceneLightingData WhimSceneLighting(float3 normalWS)
{
    WhimSceneLightingData result = (WhimSceneLightingData)0;
#if defined(WHIM_SURFACE_CONTEXT) && !defined(SHADERGRAPH_PREVIEW)
    InputData inputData = WhimInputForNormal(normalWS);
    SurfaceData surface = (SurfaceData)0;
    surface.occlusion = 1;
    half4 shadowMask = CalculateShadowMask(inputData);
    AmbientOcclusionFactor ao = CreateAmbientOcclusionFactor(inputData, surface);
    uint meshRenderingLayers = GetMeshRenderingLayer();
    Light mainLight = GetMainLight(inputData, shadowMask, ao);
    MixRealtimeAndBakedGI(mainLight, inputData.normalWS, inputData.bakedGI);
    result.mainDirection = mainLight.direction;
    result.mainColor = mainLight.color;
    result.mainDistanceAttenuation = mainLight.distanceAttenuation;
    result.mainShadowAttenuation = mainLight.shadowAttenuation;
#ifdef _LIGHT_LAYERS
    if (!IsMatchingLightLayer(mainLight.layerMask, meshRenderingLayers)) result.mainColor = 0;
#endif
    result.bakedGI = inputData.bakedGI;
    result.shadowMask = shadowMask;
    result.directAO = ao.directAmbientOcclusion;
    result.indirectAO = ao.indirectAmbientOcclusion;
#if defined(_ADDITIONAL_LIGHTS)
    uint pixelLightCount = GetAdditionalLightsCount();
#if USE_CLUSTER_LIGHT_LOOP
    [loop] for (uint lightIndex = 0; lightIndex < min(URP_FP_DIRECTIONAL_LIGHTS_COUNT, MAX_VISIBLE_LIGHTS); lightIndex++)
    {
        CLUSTER_LIGHT_LOOP_SUBTRACTIVE_LIGHT_CHECK
        Light light = GetAdditionalLight(lightIndex, inputData, shadowMask, ao);
#ifdef _LIGHT_LAYERS
        if (IsMatchingLightLayer(light.layerMask, meshRenderingLayers))
#endif
        result.additionalDiffuse += light.color * light.distanceAttenuation * light.shadowAttenuation
            * saturate(dot(inputData.normalWS, light.direction));
    }
#endif
    LIGHT_LOOP_BEGIN(pixelLightCount)
        Light light = GetAdditionalLight(lightIndex, inputData, shadowMask, ao);
#ifdef _LIGHT_LAYERS
        if (IsMatchingLightLayer(light.layerMask, meshRenderingLayers))
#endif
        result.additionalDiffuse += light.color * light.distanceAttenuation * light.shadowAttenuation
            * saturate(dot(inputData.normalWS, light.direction));
    LIGHT_LOOP_END
#endif
#if defined(_ADDITIONAL_LIGHTS_VERTEX)
    result.vertexLighting = inputData.vertexLighting;
#endif
#else
    result.mainDirection = normalize(float3(-0.4, 0.6, 0.7));
    result.mainColor = 1;
    result.mainDistanceAttenuation = 1;
    result.mainShadowAttenuation = 1;
    result.bakedGI = 0.2;
    result.shadowMask = 1;
    result.directAO = 1;
    result.indirectAO = 1;
#endif
    return result;
}
#endif
