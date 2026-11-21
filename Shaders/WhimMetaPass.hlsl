// Adapted from URP 17.7.0 LightingMetaPass.hlsl.
// Copyright Unity Technologies ApS. Unity Companion License; see Unity-NOTICE.md.
PackedVaryings vert(Attributes input)
{
    return PackVaryings(BuildVaryings(input));
}

half4 frag(PackedVaryings packedInput) : SV_Target
{
    Varyings input = UnpackVaryings(packedInput);
    UNITY_SETUP_INSTANCE_ID(input);
    SurfaceDescription surface = BuildSurfaceDescription(input);
#if defined(_ALPHATEST_ON)
    clip(surface.Alpha - surface.AlphaClipThreshold);
#endif
    MetaInput meta = (MetaInput)0;
    meta.Albedo = surface.WhimBakedAlbedo;
    meta.Emission = surface.WhimBakedEmission;
#ifdef EDITOR_VISUALIZATION
    meta.VizUV = input.texCoord1.xy;
    meta.LightCoord = input.texCoord2;
#endif
    return UnityMetaFragment(meta);
}
