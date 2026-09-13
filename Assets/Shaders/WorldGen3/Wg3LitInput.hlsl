#ifndef UNIVERSAL_LIT_INPUT_INCLUDED
#define UNIVERSAL_LIT_INPUT_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/SurfaceInput.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/ParallaxMapping.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DBuffer.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/DebugMipmapStreamingMacros.hlsl"

#if defined(_DETAIL_MULX2) || defined(_DETAIL_SCALED)
#define _DETAIL
#endif

// NOTE: Do not ifdef the properties here as SRP batcher can not handle different layouts.
CBUFFER_START(UnityPerMaterial)
float4 _BaseMap_ST;
float4 _BaseMap_TexelSize;
float4 _DetailAlbedoMap_ST;
half4 _BaseColor;
half4 _SpecColor;
half4 _EmissionColor;
half _Cutoff;
half _Smoothness;
half _Metallic;
half _BumpScale;
half _Parallax;
half _OcclusionStrength;
half _ClearCoatMask;
half _ClearCoatSmoothness;
half _DetailAlbedoMapScale;
half _DetailNormalMapScale;
half _Surface;
// WG3 (A4): al final del bloque y en TODAS las pasadas (este fichero), o el SRP Batcher se rompe.
half _Wg3StochasticCell;
half _Wg3StochasticBlend;
half _Wg3WetScale;
half _Wg3WetCoverage;
half _Wg3WetDarken;
half _Wg3WetSmoothness;
half _Wg3DryLighten;
half _Wg3WetFlatten;
UNITY_TEXTURE_STREAMING_DEBUG_VARS;
CBUFFER_END

// NOTE: Do not ifdef the properties for dots instancing, but ifdef the actual usage.
// Otherwise you might break CPU-side as property constant-buffer offsets change per variant.
// NOTE: Dots instancing is orthogonal to the constant buffer above.
#ifdef UNITY_DOTS_INSTANCING_ENABLED

UNITY_DOTS_INSTANCING_START(MaterialPropertyMetadata)
    UNITY_DOTS_INSTANCED_PROP(float4, _BaseColor)
    UNITY_DOTS_INSTANCED_PROP(float4, _SpecColor)
    UNITY_DOTS_INSTANCED_PROP(float4, _EmissionColor)
    UNITY_DOTS_INSTANCED_PROP(float , _Cutoff)
    UNITY_DOTS_INSTANCED_PROP(float , _Smoothness)
    UNITY_DOTS_INSTANCED_PROP(float , _Metallic)
    UNITY_DOTS_INSTANCED_PROP(float , _BumpScale)
    UNITY_DOTS_INSTANCED_PROP(float , _Parallax)
    UNITY_DOTS_INSTANCED_PROP(float , _OcclusionStrength)
    UNITY_DOTS_INSTANCED_PROP(float , _ClearCoatMask)
    UNITY_DOTS_INSTANCED_PROP(float , _ClearCoatSmoothness)
    UNITY_DOTS_INSTANCED_PROP(float , _DetailAlbedoMapScale)
    UNITY_DOTS_INSTANCED_PROP(float , _DetailNormalMapScale)
    UNITY_DOTS_INSTANCED_PROP(float , _Surface)
UNITY_DOTS_INSTANCING_END(MaterialPropertyMetadata)

// Here, we want to avoid overriding a property like e.g. _BaseColor with something like this:
// #define _BaseColor UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _BaseColor0)
//
// It would be simpler, but it can cause the compiler to regenerate the property loading code for each use of _BaseColor.
//
// To avoid this, the property loads are cached in some static values at the beginning of the shader.
// The properties such as _BaseColor are then overridden so that it expand directly to the static value like this:
// #define _BaseColor unity_DOTS_Sampled_BaseColor
//
// This simple fix happened to improve GPU performances by ~10% on Meta Quest 2 with URP on some scenes.
static float4 unity_DOTS_Sampled_BaseColor;
static float4 unity_DOTS_Sampled_SpecColor;
static float4 unity_DOTS_Sampled_EmissionColor;
static float  unity_DOTS_Sampled_Cutoff;
static float  unity_DOTS_Sampled_Smoothness;
static float  unity_DOTS_Sampled_Metallic;
static float  unity_DOTS_Sampled_BumpScale;
static float  unity_DOTS_Sampled_Parallax;
static float  unity_DOTS_Sampled_OcclusionStrength;
static float  unity_DOTS_Sampled_ClearCoatMask;
static float  unity_DOTS_Sampled_ClearCoatSmoothness;
static float  unity_DOTS_Sampled_DetailAlbedoMapScale;
static float  unity_DOTS_Sampled_DetailNormalMapScale;
static float  unity_DOTS_Sampled_Surface;

void SetupDOTSLitMaterialPropertyCaches()
{
    unity_DOTS_Sampled_BaseColor            = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _BaseColor);
    unity_DOTS_Sampled_SpecColor            = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _SpecColor);
    unity_DOTS_Sampled_EmissionColor        = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _EmissionColor);
    unity_DOTS_Sampled_Cutoff               = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _Cutoff);
    unity_DOTS_Sampled_Smoothness           = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _Smoothness);
    unity_DOTS_Sampled_Metallic             = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _Metallic);
    unity_DOTS_Sampled_BumpScale            = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _BumpScale);
    unity_DOTS_Sampled_Parallax             = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _Parallax);
    unity_DOTS_Sampled_OcclusionStrength    = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _OcclusionStrength);
    unity_DOTS_Sampled_ClearCoatMask        = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _ClearCoatMask);
    unity_DOTS_Sampled_ClearCoatSmoothness  = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _ClearCoatSmoothness);
    unity_DOTS_Sampled_DetailAlbedoMapScale = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _DetailAlbedoMapScale);
    unity_DOTS_Sampled_DetailNormalMapScale = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _DetailNormalMapScale);
    unity_DOTS_Sampled_Surface              = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _Surface);
}

#undef UNITY_SETUP_DOTS_MATERIAL_PROPERTY_CACHES
#define UNITY_SETUP_DOTS_MATERIAL_PROPERTY_CACHES() SetupDOTSLitMaterialPropertyCaches()

#define _BaseColor              unity_DOTS_Sampled_BaseColor
#define _SpecColor              unity_DOTS_Sampled_SpecColor
#define _EmissionColor          unity_DOTS_Sampled_EmissionColor
#define _Cutoff                 unity_DOTS_Sampled_Cutoff
#define _Smoothness             unity_DOTS_Sampled_Smoothness
#define _Metallic               unity_DOTS_Sampled_Metallic
#define _BumpScale              unity_DOTS_Sampled_BumpScale
#define _Parallax               unity_DOTS_Sampled_Parallax
#define _OcclusionStrength      unity_DOTS_Sampled_OcclusionStrength
#define _ClearCoatMask          unity_DOTS_Sampled_ClearCoatMask
#define _ClearCoatSmoothness    unity_DOTS_Sampled_ClearCoatSmoothness
#define _DetailAlbedoMapScale   unity_DOTS_Sampled_DetailAlbedoMapScale
#define _DetailNormalMapScale   unity_DOTS_Sampled_DetailNormalMapScale
#define _Surface                unity_DOTS_Sampled_Surface

#endif

TEXTURE2D(_ParallaxMap);        SAMPLER(sampler_ParallaxMap);
TEXTURE2D(_OcclusionMap);       SAMPLER(sampler_OcclusionMap);
TEXTURE2D(_DetailMask);         SAMPLER(sampler_DetailMask);
TEXTURE2D(_DetailAlbedoMap);    SAMPLER(sampler_DetailAlbedoMap);
TEXTURE2D(_DetailNormalMap);    SAMPLER(sampler_DetailNormalMap);
TEXTURE2D(_MetallicGlossMap);   SAMPLER(sampler_MetallicGlossMap);
TEXTURE2D(_SpecGlossMap);       SAMPLER(sampler_SpecGlossMap);
TEXTURE2D(_ClearCoatMap);       SAMPLER(sampler_ClearCoatMap);

#ifdef _SPECULAR_SETUP
    #define SAMPLE_METALLICSPECULAR(uv) SAMPLE_TEXTURE2D(_SpecGlossMap, sampler_SpecGlossMap, uv)
#else
    #define SAMPLE_METALLICSPECULAR(uv) SAMPLE_TEXTURE2D(_MetallicGlossMap, sampler_MetallicGlossMap, uv)
#endif

half4 SampleMetallicSpecGloss(float2 uv, half albedoAlpha)
{
    half4 specGloss;

#ifdef _METALLICSPECGLOSSMAP
    specGloss = half4(SAMPLE_METALLICSPECULAR(uv));
    #ifdef _SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A
        specGloss.a = albedoAlpha * _Smoothness;
    #else
        specGloss.a *= _Smoothness;
    #endif
#else // _METALLICSPECGLOSSMAP
    #if _SPECULAR_SETUP
        specGloss.rgb = _SpecColor.rgb;
    #else
        specGloss.rgb = _Metallic.rrr;
    #endif

    #ifdef _SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A
        specGloss.a = albedoAlpha * _Smoothness;
    #else
        specGloss.a = _Smoothness;
    #endif
#endif

    return specGloss;
}

half SampleOcclusion(float2 uv)
{
    #ifdef _OCCLUSIONMAP
        half occ = SAMPLE_TEXTURE2D(_OcclusionMap, sampler_OcclusionMap, uv).g;
        return LerpWhiteTo(occ, _OcclusionStrength);
    #else
        return half(1.0);
    #endif
}


// Returns clear coat parameters
// .x/.r == mask
// .y/.g == smoothness
half2 SampleClearCoat(float2 uv)
{
#if defined(_CLEARCOAT) || defined(_CLEARCOATMAP)
    half2 clearCoatMaskSmoothness = half2(_ClearCoatMask, _ClearCoatSmoothness);

#if defined(_CLEARCOATMAP)
    clearCoatMaskSmoothness *= SAMPLE_TEXTURE2D(_ClearCoatMap, sampler_ClearCoatMap, uv).rg;
#endif

    return clearCoatMaskSmoothness;
#else
    return half2(0.0, 1.0);
#endif  // _CLEARCOAT
}

void ApplyPerPixelDisplacement(half3 viewDirTS, inout float2 uv)
{
#if defined(_PARALLAXMAP)
    uv += ParallaxMapping(TEXTURE2D_ARGS(_ParallaxMap, sampler_ParallaxMap), viewDirTS, _Parallax, uv);
#endif
}

// Used for scaling detail albedo. Main features:
// - Depending if detailAlbedo brightens or darkens, scale magnifies effect.
// - No effect is applied if detailAlbedo is 0.5.
half3 ScaleDetailAlbedo(half3 detailAlbedo, half scale)
{
    // detailAlbedo = detailAlbedo * 2.0h - 1.0h;
    // detailAlbedo *= _DetailAlbedoMapScale;
    // detailAlbedo = detailAlbedo * 0.5h + 0.5h;
    // return detailAlbedo * 2.0f;

    // A bit more optimized
    return half(2.0) * detailAlbedo * scale - scale + half(1.0);
}

half3 ApplyDetailAlbedo(float2 detailUv, half3 albedo, half detailMask)
{
#if defined(_DETAIL)
    half3 detailAlbedo = SAMPLE_TEXTURE2D(_DetailAlbedoMap, sampler_DetailAlbedoMap, detailUv).rgb;

    // In order to have same performance as builtin, we do scaling only if scale is not 1.0 (Scaled version has 6 additional instructions)
#if defined(_DETAIL_SCALED)
    detailAlbedo = ScaleDetailAlbedo(detailAlbedo, _DetailAlbedoMapScale);
#else
    detailAlbedo = half(2.0) * detailAlbedo;
#endif

    return albedo * LerpWhiteTo(detailAlbedo, detailMask);
#else
    return albedo;
#endif
}

half3 ApplyDetailNormal(float2 detailUv, half3 normalTS, half detailMask)
{
#if defined(_DETAIL)
#if BUMP_SCALE_NOT_SUPPORTED
    half3 detailNormalTS = UnpackNormal(SAMPLE_TEXTURE2D(_DetailNormalMap, sampler_DetailNormalMap, detailUv));
#else
    half3 detailNormalTS = UnpackNormalScale(SAMPLE_TEXTURE2D(_DetailNormalMap, sampler_DetailNormalMap, detailUv), _DetailNormalMapScale);
#endif

    // With UNITY_NO_DXT5nm unpacked vector is not normalized for BlendNormalRNM
    // For visual consistancy we going to do in all cases
    detailNormalTS = normalize(detailNormalTS);

    return lerp(normalTS, BlendNormalRNM(normalTS, detailNormalTS), detailMask); // todo: detailMask should lerp the angle of the quaternion rotation, not the normals
#else
    return normalTS;
#endif
}

inline void InitializeStandardLitSurfaceData(float2 uv, out SurfaceData outSurfaceData)
{
    half4 albedoAlpha = SampleAlbedoAlpha(uv, TEXTURE2D_ARGS(_BaseMap, sampler_BaseMap));
    outSurfaceData.alpha = Alpha(albedoAlpha.a, _BaseColor, _Cutoff);

    half4 specGloss = SampleMetallicSpecGloss(uv, albedoAlpha.a);
    outSurfaceData.albedo = albedoAlpha.rgb * _BaseColor.rgb;
    outSurfaceData.albedo = AlphaModulate(outSurfaceData.albedo, outSurfaceData.alpha);

#if _SPECULAR_SETUP
    outSurfaceData.metallic = half(1.0);
    outSurfaceData.specular = specGloss.rgb;
#else
    outSurfaceData.metallic = specGloss.r;
    outSurfaceData.specular = half3(0.0, 0.0, 0.0);
#endif

    outSurfaceData.smoothness = specGloss.a;
    outSurfaceData.normalTS = SampleNormal(uv, TEXTURE2D_ARGS(_BumpMap, sampler_BumpMap), _BumpScale);
    outSurfaceData.occlusion = SampleOcclusion(uv);
    outSurfaceData.emission = SampleEmission(uv, _EmissionColor.rgb, TEXTURE2D_ARGS(_EmissionMap, sampler_EmissionMap));

#if defined(_CLEARCOAT) || defined(_CLEARCOATMAP)
    half2 clearCoat = SampleClearCoat(uv);
    outSurfaceData.clearCoatMask       = clearCoat.r;
    outSurfaceData.clearCoatSmoothness = clearCoat.g;
#else
    outSurfaceData.clearCoatMask       = half(0.0);
    outSurfaceData.clearCoatSmoothness = half(0.0);
#endif

#if defined(_DETAIL)
    half detailMask = SAMPLE_TEXTURE2D(_DetailMask, sampler_DetailMask, uv).a;
    float2 detailUv = uv * _DetailAlbedoMap_ST.xy + _DetailAlbedoMap_ST.zw;
    outSurfaceData.albedo = ApplyDetailAlbedo(detailUv, outSurfaceData.albedo, detailMask);
    outSurfaceData.normalTS = ApplyDetailNormal(detailUv, outSurfaceData.normalTS, detailMask);
#endif
}

// ════════════════════════════════════════════════════════════════════════════════════════════
// WG3 (A4, 2026-09-13) — lo único propio de esta copia. Lo llaman Wg3LitForwardPass y
// Wg3LitGBufferPass en lugar de InitializeStandardLitSurfaceData, porque necesitan la posición de
// MUNDO: la rejilla estocástica y la humedad salen de positionWS.xz y no de la UV, que está
// anclada al mundo pero envuelta a 12 m (Wg3MeshBuilder.AnchorPeriodM) y repetiría con ella.
// ════════════════════════════════════════════════════════════════════════════════════════════

#if defined(_WG3_STOCHASTIC) || defined(_WG3_WETNESS)
// Hash sin seno (Dave Hoskins): estable con coordenadas de mundo de miles de metros.
float Wg3Hash12(float2 p)
{
    float3 p3 = frac(float3(p.xyx) * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return frac((p3.x + p3.y) * p3.z);
}

float2 Wg3Hash22(float2 p)
{
    float3 p3 = frac(float3(p.xyx) * float3(0.1031, 0.1030, 0.0973));
    p3 += dot(p3, p3.yzx + 33.33);
    return frac((p3.xx + p3.yz) * p3.zy);
}
#endif

#if defined(_WG3_WETNESS)
float Wg3ValueNoise(float2 p)
{
    float2 i = floor(p);
    float2 f = frac(p);
    f = f * f * (3.0 - 2.0 * f);
    float a = Wg3Hash12(i);
    float b = Wg3Hash12(i + float2(1.0, 0.0));
    float c = Wg3Hash12(i + float2(0.0, 1.0));
    float d = Wg3Hash12(i + float2(1.0, 1.0));
    return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
}

// Cuatro octavas en [0, 1], media 0,5. El desplazamiento entre octavas evita que sus retículas
// coincidan en el origen.
float Wg3Fbm(float2 p)
{
    float sum = 0.0;
    float amp = 0.5;
    for (int o = 0; o < 4; o++)
    {
        sum += amp * Wg3ValueNoise(p);
        p = p * 2.03 + 17.1;
        amp *= 0.5;
    }
    return sum / 0.9375;
}
#endif

#if defined(_WG3_STOCHASTIC)
// Tiling-and-blending hexagonal (Deliot & Heitz 2019), rescatado de 558afb54. La rejilla vive en
// metros de MUNDO; las muestras, en la UV de la malla desplazada por un offset por vértice. Dos
// triángulos que comparten vértice piden el MISMO offset, así que no hay costura.
struct Wg3Stochastic
{
    float2 uv1;
    float2 uv2;
    float2 uv3;
    float3 w;
    float2 dx;
    float2 dy;
};

Wg3Stochastic Wg3StochasticSetup(float2 uv, float2 lattice)
{
    Wg3Stochastic s;
    float2 skewed = float2(lattice.x - lattice.y * 0.57735027, lattice.y * 1.15470054);
    float2 baseId = floor(skewed);
    float3 t = float3(frac(skewed), 0.0);
    t.z = 1.0 - t.x - t.y;

    float2 v1, v2, v3;
    if (t.z > 0.0)
    {
        s.w = float3(t.z, t.y, t.x);
        v1 = baseId;
        v2 = baseId + float2(0.0, 1.0);
        v3 = baseId + float2(1.0, 0.0);
    }
    else
    {
        s.w = float3(-t.z, 1.0 - t.y, 1.0 - t.x);
        v1 = baseId + float2(1.0, 1.0);
        v2 = baseId + float2(1.0, 0.0);
        v3 = baseId + float2(0.0, 1.0);
    }

    // 0 = corte duro, 1 = mezcla lisa.
    float sharpness = lerp(16.0, 1.0, saturate(_Wg3StochasticBlend));
    s.w = pow(max(s.w, 1e-4), sharpness);
    s.w /= (s.w.x + s.w.y + s.w.z);

    s.uv1 = uv + Wg3Hash22(v1);
    s.uv2 = uv + Wg3Hash22(v2);
    s.uv3 = uv + Wg3Hash22(v3);
    // Derivadas de la UV SIN desplazar: con la desplazada, el salto entre celdas dispara el mip
    // y cada junta sale como una línea borrosa.
    s.dx = ddx(uv);
    s.dy = ddy(uv);
    return s;
}

half4 Wg3SampleStochastic(TEXTURE2D_PARAM(tex, samp), Wg3Stochastic s)
{
    return SAMPLE_TEXTURE2D_GRAD(tex, samp, s.uv1, s.dx, s.dy) * s.w.x
         + SAMPLE_TEXTURE2D_GRAD(tex, samp, s.uv2, s.dx, s.dy) * s.w.y
         + SAMPLE_TEXTURE2D_GRAD(tex, samp, s.uv3, s.dx, s.dy) * s.w.z;
}
#endif

inline void Wg3InitializeLitSurfaceData(float2 uv, float3 positionWS, out SurfaceData outSurfaceData)
{
#if defined(_WG3_STOCHASTIC)
    outSurfaceData = (SurfaceData)0;
    Wg3Stochastic s = Wg3StochasticSetup(uv, positionWS.xz / max(_Wg3StochasticCell, 0.25));

    half4 albedoAlpha = Wg3SampleStochastic(TEXTURE2D_ARGS(_BaseMap, sampler_BaseMap), s);
    // Preservación de varianza: sin ella la franja de mezcla pierde contraste y aparece una malla
    // de manchas blandas. La media es el mip más alto, que es el color medio de la textura.
    half3 meanAlbedo = SAMPLE_TEXTURE2D_LOD(_BaseMap, sampler_BaseMap, uv, 11.0).rgb;
    albedoAlpha.rgb = saturate((albedoAlpha.rgb - meanAlbedo) * rsqrt(dot(s.w, s.w)) + meanAlbedo);

    outSurfaceData.alpha = Alpha(albedoAlpha.a, _BaseColor, _Cutoff);
    outSurfaceData.albedo = AlphaModulate(albedoAlpha.rgb * _BaseColor.rgb, outSurfaceData.alpha);

  #ifdef _METALLICSPECGLOSSMAP
    half4 specGloss = Wg3SampleStochastic(TEXTURE2D_ARGS(_MetallicGlossMap, sampler_MetallicGlossMap), s);
    specGloss.a *= _Smoothness;
  #else
    half4 specGloss = half4(_Metallic, _Metallic, _Metallic, _Smoothness);
  #endif
    outSurfaceData.metallic = specGloss.r;
    outSurfaceData.specular = half3(0.0, 0.0, 0.0);
    outSurfaceData.smoothness = specGloss.a;

  #ifdef _NORMALMAP
    outSurfaceData.normalTS = UnpackNormalScale(
        Wg3SampleStochastic(TEXTURE2D_ARGS(_BumpMap, sampler_BumpMap), s), _BumpScale);
  #else
    outSurfaceData.normalTS = half3(0.0, 0.0, 1.0);
  #endif

  #ifdef _OCCLUSIONMAP
    outSurfaceData.occlusion = LerpWhiteTo(
        Wg3SampleStochastic(TEXTURE2D_ARGS(_OcclusionMap, sampler_OcclusionMap), s).g, _OcclusionStrength);
  #else
    outSurfaceData.occlusion = half(1.0);
  #endif

    outSurfaceData.emission = SampleEmission(uv, _EmissionColor.rgb, TEXTURE2D_ARGS(_EmissionMap, sampler_EmissionMap));
    outSurfaceData.clearCoatMask = half(0.0);
    outSurfaceData.clearCoatSmoothness = half(1.0);
#else
    InitializeStandardLitSurfaceData(uv, outSurfaceData);
#endif

#if defined(_WG3_WETNESS)
    // Zonas por el MUNDO: `wet` es el borde de la mancha, `soak` su núcleo encharcado y `dry` lo
    // que queda lejos, que sube un poco para que las zonas se lean como zonas y no como sombra.
    float n = Wg3Fbm(positionWS.xz / max(_Wg3WetScale, 0.5));
    float edge = lerp(0.72, 0.38, saturate(_Wg3WetCoverage));
    float wet = smoothstep(edge - 0.05, edge + 0.05, n);
    float soak = smoothstep(edge + 0.07, edge + 0.18, n);
    float dry = 1.0 - smoothstep(edge - 0.22, edge - 0.04, n);
    outSurfaceData.albedo *= (1.0 + _Wg3DryLighten * dry) * (1.0 - _Wg3WetDarken * (0.65 * wet + 0.35 * soak));
    outSurfaceData.smoothness = lerp(outSurfaceData.smoothness,
        max(outSurfaceData.smoothness, _Wg3WetSmoothness), saturate(0.6 * wet + 0.4 * soak));
    outSurfaceData.normalTS = normalize(lerp(outSurfaceData.normalTS, half3(0.0, 0.0, 1.0), _Wg3WetFlatten * soak));
#endif
}

#endif // UNIVERSAL_INPUT_SURFACE_PBR_INCLUDED
