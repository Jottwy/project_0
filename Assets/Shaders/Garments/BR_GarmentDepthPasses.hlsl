#ifndef BR_GARMENT_DEPTH_PASSES_INCLUDED
#define BR_GARMENT_DEPTH_PASSES_INCLUDED

// Sombra, profundidad y normales de la ropa por zonas: los mismos agujeros que el pase de color, o la profundidad y el
// SSAO los taparían. Una pasada define BR_GARMENT_SHADOW, BR_GARMENT_DEPTH_ONLY o BR_GARMENT_DEPTH_NORMALS antes de incluir.

#if defined(BR_GARMENT_DEPTH_NORMALS)
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
#else
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#endif
#if defined(BR_GARMENT_SHADOW)
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
float3 _LightDirection;
float3 _LightPosition;
#endif
#if defined(LOD_FADE_CROSSFADE)
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/LODCrossFade.hlsl"
#endif
#include "BR_GarmentDamage.hlsl"

struct GarmentDepthAttributes
{
    float4 positionOS : POSITION;
    float3 normalOS   : NORMAL;
    float2 texcoord   : TEXCOORD0;
    float2 zoneUV     : TEXCOORD3;
    float2 zoneCoords : TEXCOORD4;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct GarmentDepthVaryings
{
    float4 positionCS : SV_POSITION;
    float2 uv         : TEXCOORD0;
    float4 garment    : TEXCOORD1;
    half3  normalWS   : TEXCOORD2;
    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};

GarmentDepthVaryings GarmentDepthVertex(GarmentDepthAttributes input)
{
    GarmentDepthVaryings output = (GarmentDepthVaryings)0;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

    input.positionOS.xyz += input.normalOS * _GarmentInflate;
#if defined(BR_GARMENT_FP)
    input.positionOS.xyz = GarmentWarpToViewModel(input.positionOS.xyz);
    output.uv = input.zoneCoords * _GarmentFabricTiling + _BaseMap_ST.zw;
#else
    output.uv = TRANSFORM_TEX(input.texcoord, _BaseMap);
#endif
    output.garment = float4(input.zoneUV, input.zoneCoords);
    output.normalWS = TransformObjectToWorldNormal(input.normalOS);

#if defined(BR_GARMENT_SHADOW)
    float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
    #if _CASTING_PUNCTUAL_LIGHT_SHADOW
        float3 lightDirectionWS = normalize(_LightPosition - positionWS);
    #else
        float3 lightDirectionWS = _LightDirection;
    #endif
    float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, output.normalWS, lightDirectionWS));
    output.positionCS = ApplyShadowClamping(positionCS);
#else
    float3 positionVS = TransformWorldToView(TransformObjectToWorld(input.positionOS.xyz));
    positionVS.z += _GarmentViewBias;
    output.positionCS = TransformWViewToHClip(positionVS);
#endif
    return output;
}

void GarmentDepthClip(GarmentDepthVaryings input)
{
    GarmentDamage damage = EvaluateGarmentDamage(input.garment.x, input.garment.y, input.garment.zw);
    clip(0.5 - damage.clipMask);
    #if defined(LOD_FADE_CROSSFADE)
        LODFadeCrossFade(input.positionCS);
    #endif
}

#if defined(BR_GARMENT_SHADOW)
half4 GarmentShadowFragment(GarmentDepthVaryings input) : SV_TARGET
{
    UNITY_SETUP_INSTANCE_ID(input);
    GarmentDepthClip(input);
    return 0;
}
#endif

#if defined(BR_GARMENT_DEPTH_ONLY)
half GarmentDepthOnlyFragment(GarmentDepthVaryings input) : SV_TARGET
{
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
    GarmentDepthClip(input);
    return input.positionCS.z;
}
#endif

#if defined(BR_GARMENT_DEPTH_NORMALS)
void GarmentDepthNormalsFragment(
    GarmentDepthVaryings input
    , FRONT_FACE_TYPE isFrontFace : FRONT_FACE_SEMANTIC
    , out half4 outNormalWS : SV_Target0
#ifdef _WRITE_RENDERING_LAYERS
    , out float4 outRenderingLayers : SV_Target1
#endif
)
{
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
    GarmentDepthClip(input);

    float3 normalWS = NormalizeNormalPerPixel(input.normalWS);
    if (!IS_FRONT_VFACE(isFrontFace, true, false))
        normalWS = -normalWS;
    outNormalWS = half4(normalWS, 0.0);

    #ifdef _WRITE_RENDERING_LAYERS
        uint renderingLayers = GetMeshRenderingLayer();
        outRenderingLayers = float4(EncodeMeshRenderingLayer(renderingLayers), 0, 0, 0);
    #endif
}
#endif

#endif
