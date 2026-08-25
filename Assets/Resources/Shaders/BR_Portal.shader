// ADR-093 — la superficie del hueco de una puerta del Level 4: enseña lo que la cámara gemela
// ve al otro lado.
//
// Muestrea la render texture en coordenadas de PANTALLA, no por las UV del quad. Es la
// diferencia entre un portal y una televisión: la cámara gemela renderiza desde el punto de
// vista del jugador trasladado al otro lado, así que su imagen ya está en el encuadre correcto y
// lo único que hace este quad es recortarla a la forma del vano. Con UV de quad, la vista se
// estiraría con el marco y se movería con él en vez de quedarse quieta como una ventana.
//
// URP explícito ("RenderPipeline"="UniversalPipeline" + Core.hlsl). Un shader Built-in aquí sale
// magenta desde ADR-065.
Shader "Backrooms/Portal"
{
    Properties
    {
        _MainTex ("Portal view", 2D) = "black" {}
        _Tint ("Tint", Color) = (1,1,1,1)
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Geometry+1"
        }

        Pass
        {
            Name "PortalUnlit"
            // Cull Off: el hueco se mira por los dos lados, y desde el de atrás también tiene que
            // enseñar algo en vez de desaparecer.
            Cull Off
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _Tint;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float4 screenPos   : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.screenPos = ComputeScreenPos(OUT.positionHCS);
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float2 uv = IN.screenPos.xy / max(IN.screenPos.w, 1e-5);
                return SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv) * _Tint;
            }
            ENDHLSL
        }
    }

    FallBack Off
}
