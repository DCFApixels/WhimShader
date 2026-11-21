// Fragment framing adapted from URP 17.7.0 PBRForwardPass.hlsl / UnlitPass.hlsl.
// Copyright Unity Technologies ApS. Unity Companion License; see Unity-NOTICE.md.
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DistanceFog.hlsl"

PackedVaryings vert(Attributes input)
{
    return PackVaryings(BuildVaryings(input));
}

void frag(PackedVaryings packedInput, out half4 outColor : SV_Target0
#ifdef _WRITE_RENDERING_LAYERS
    , out uint outRenderingLayers : SV_Target1
#endif
)
{
    Varyings input = UnpackVaryings(packedInput);
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
    // WhimVaryings initializes the lighting context before evaluating any nodes.
    SurfaceDescription surface = BuildSurfaceDescription(input);
#if defined(_SURFACE_TYPE_TRANSPARENT)
    bool transparent = true;
#else
    bool transparent = false;
#endif
#if defined(_ALPHATEST_ON)
    half alpha = AlphaDiscard(surface.Alpha, surface.AlphaClipThreshold);
#elif defined(_SURFACE_TYPE_TRANSPARENT)
    half alpha = surface.Alpha;
#else
    half alpha = 1;
#endif
#if defined(LOD_FADE_CROSSFADE) && USE_UNITY_CROSSFADE
    LODFadeCrossFade(input.positionCS);
#endif
    float3 color = surface.WhimColor;
    // No UniversalFragmentUnlit/PBR here: nodes already supplied final radiance.
    color = ClampExposed(GetPreExposureMultiplier() * BlendDistanceFog(color, input.positionCS));
#if defined(_ALPHAPREMULTIPLY_ON)
    color *= alpha;
#elif defined(_ALPHAMODULATE_ON)
    color = AlphaModulate(color, alpha);
#endif
    outColor = half4(color, OutputAlpha(alpha, transparent));
#ifdef _WRITE_RENDERING_LAYERS
    outRenderingLayers = EncodeMeshRenderingLayer();
#endif
}
