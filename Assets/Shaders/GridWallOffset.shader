// Wall material shader for the grid renderer (URP, Forward+).
//
// Matte Lambert-style surface with a depth Polygon Offset so wall faces LOSE
// depth-buffer ties. Wall side faces end up coplanar with the floor/ceiling slab
// edges; without a bias the GPU flickers between them on that shared plane
// (z-fighting). "Offset 1, 1" pushes the wall slightly back in depth so the
// floor/ceiling slab wins the tie — no geometry moved, no epsilon gap.
//
// Hand-written HLSL because URP Lit does not expose the Offset render state and
// Shader Graph cannot emit it. Name + guid are unchanged so GridWall.mat and the
// Shader.Find call sites keep referencing it. _BaseColor is the per-tile tint
// (set via MaterialPropertyBlock). Zero specular: fluorescent-lit office look.
//
// Mapa de normales (2026-08-12): el papel pintado de Level 0 no se distingue por
// color sino por el grabado, y con smoothness cero el ÚNICO canal que puede
// mostrarlo es el N·L difuso. De ahí la base tangente en el vértice y el
// _BumpMap en el fragmento. Sigue sin haber especular ni metalicidad: el relieve
// se ve porque la luz roza la superficie, no porque brille.
Shader "Backrooms/GridWallOffset"
{
    Properties
    {
        _BaseMap("Base Map", 2D) = "white" {}
        _BaseColor("Base Color", Color) = (1, 1, 1, 1)
        // El papel pintado se distingue por RELIEVE, no por albedo: el motivo del
        // _BaseMap son 6 de 255 de luminancia y quien lo saca en luz rasante es este
        // mapa. Sin _BumpMap asignado, "bump" es la normal plana y la pared se
        // comporta exactamente como antes.
        [Normal] _BumpMap("Normal Map", 2D) = "bump" {}
        _BumpScale("Normal Scale", Float) = 1
        [Enum(UnityEngine.Rendering.CullMode)] _Cull("Cull", Float) = 2 // 0=Off,1=Front,2=Back
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" "RenderPipeline" = "UniversalPipeline" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            float4 _BumpMap_ST;
            half4 _BaseColor;
            float _BumpScale;
            float _Cull;
        CBUFFER_END
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            Cull [_Cull]
            // Polygon offset: wall loses depth ties to coplanar floor/ceiling slabs.
            Offset 1, 1

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            // Light-loop keyword set trimmed from URP SimpleLit's ForwardLit pass.
            // No lightmap/decal/cookie variants: grid geometry is runtime-generated,
            // never baked, and the project uses neither decals nor cookies.
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _FORWARD_PLUS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);
            TEXTURE2D(_BumpMap);
            SAMPLER(sampler_BumpMap);

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float4 tangentOS  : TANGENT;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS  : SV_POSITION;
                float2 uv          : TEXCOORD0;
                float3 positionWS  : TEXCOORD1;
                float3 normalWS    : TEXCOORD2;
                half   fogFactor   : TEXCOORD3;
                float3 tangentWS   : TEXCOORD4;
                float3 bitangentWS : TEXCOORD5;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                VertexPositionInputs pos = GetVertexPositionInputs(input.positionOS.xyz);
                // La base tangente sale del vértice (el Cube de los prefabs de pared la
                // trae): sin ella el mapa de normales no tiene marco en el que aplicarse.
                VertexNormalInputs nrm = GetVertexNormalInputs(input.normalOS, input.tangentOS);
                output.positionCS  = pos.positionCS;
                output.positionWS  = pos.positionWS;
                output.normalWS    = nrm.normalWS;
                output.tangentWS   = nrm.tangentWS;
                output.bitangentWS = nrm.bitangentWS;
                output.uv          = input.uv;   // sin ST: albedo y relieve tienen el suyo
                output.fogFactor   = ComputeFogFactor(pos.positionCS.z);
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float2 baseUV = input.uv * _BaseMap_ST.xy + _BaseMap_ST.zw;
                half4 albedo = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, baseUV) * _BaseColor;

                // _BumpMap lleva su propio ST para que el relieve pueda escalarse sin
                // arrastrar al albedo; el generador los deja iguales.
                float2 bumpUV = input.uv * _BumpMap_ST.xy + _BumpMap_ST.zw;
                half3 normalTS = UnpackNormalScale(
                    SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, bumpUV), _BumpScale);

                InputData inputData = (InputData)0;
                inputData.positionWS = input.positionWS;
                inputData.normalWS = normalize(TransformTangentToWorld(normalTS,
                    half3x3(input.tangentWS, input.bitangentWS, input.normalWS)));
                inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
                inputData.shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                inputData.fogCoord = input.fogFactor;
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
                inputData.bakedGI = SampleSH(inputData.normalWS); // flat ambient arrives as SH

                SurfaceData surfaceData = (SurfaceData)0;
                surfaceData.albedo = albedo.rgb;
                surfaceData.alpha = albedo.a;
                surfaceData.occlusion = 1.0h;

                half4 color = UniversalFragmentBlinnPhong(inputData, surfaceData);
                color.rgb = MixFog(color.rgb, input.fogFactor);
                return color;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ColorMask R
            Cull [_Cull]
            // Must match ForwardLit's offset or the depth prepass disagrees with the
            // color pass on the shared wall/slab plane.
            Offset 1, 1

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            float4 Vert(float4 positionOS : POSITION) : SV_POSITION
            {
                return TransformObjectToHClip(positionOS.xyz);
            }

            half Frag() : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull [_Cull]

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
            };

            float4 Vert(Attributes input) : SV_POSITION
            {
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);
            #if defined(_CASTING_PUNCTUAL_LIGHT_SHADOW)
                float3 lightDirectionWS = normalize(_LightPosition - positionWS);
            #else
                float3 lightDirectionWS = _LightDirection;
            #endif
                float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDirectionWS));
            #if UNITY_REVERSED_Z
                positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
            #else
                positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
            #endif
                return positionCS;
            }

            half4 Frag() : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }
    }
}
