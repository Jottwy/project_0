// PROTOTIPO (paso 3 de la UI diegetica). Copia de UI/Default con UNA sola diferencia:
// el vertice se reproyecta al FOV del viewmodel antes de ir a clip space, igual que hace
// LitFieldOfView_SSS con la malla del brazo y con el cuerpo del reloj.
//
// POR QUE UN SHADER A MANO Y NO UN SHADERGRAPH: el subtarget "Canvas" de URP 17 calcula
// output.positionCS a partir de input.positionOS ANTES de llamar a ApplyVertexModification,
// y nunca vuelve a usar el positionWS que esa funcion modifica (CanvasPass.hlsl, bloque
// BuildVaryings). Es decir: en un shadergraph de Canvas, VertexDescription.Position no
// mueve nada. Para desplazar vertices de UI hay que escribir el vertex stage.
//
// EL WARP ES LITERALMENTE EL DEL VENDOR, nodo a nodo:
//   tan(_FOV * PI / 360) * UNITY_MATRIX_P[1][1]  ->  negar  ->  reciproco  =  k
//   y luego posicion_vista.xy *= k   (z intacta)
// Se replica con el mismo signo y la misma matriz a proposito: cualquier convencion de
// signo de P[1][1] (y-flip al renderizar a textura) se cancela igual aqui que alli, que es
// lo unico que garantiza que brazo, reloj y este quad coincidan.
//
// _FOV y _FOVEnabled NO se declaran en Properties a proposito: son uniforms GLOBALES que
// escribe CameraFOVHandler. Declararlos como propiedades del material dejaria que el valor
// serializado pisara al global y el quad se desincronizaria del brazo.
Shader "Backrooms/UI Warp (ViewModel FOV)"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)

        _StencilComp ("Stencil Comparison", Float) = 8
        _Stencil ("Stencil ID", Float) = 0
        _StencilOp ("Stencil Operation", Float) = 0
        _StencilWriteMask ("Stencil Write Mask", Float) = 255
        _StencilReadMask ("Stencil Read Mask", Float) = 255

        _ColorMask ("Color Mask", Float) = 15

        [Toggle(UNITY_UI_ALPHACLIP)] _UseUIAlphaClip ("Use Alpha Clip", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "IgnoreProjector" = "True"
            "RenderType" = "Transparent"
            "PreviewType" = "Plane"
            "CanUseSpriteAtlas" = "True"
        }

        Stencil
        {
            Ref [_Stencil]
            Comp [_StencilComp]
            Pass [_StencilOp]
            ReadMask [_StencilReadMask]
            WriteMask [_StencilWriteMask]
        }

        Cull Off
        Lighting Off
        ZWrite Off
        ZTest [unity_GUIZTestMode]
        Blend SrcAlpha OneMinusSrcAlpha
        ColorMask [_ColorMask]

        Pass
        {
            Name "Default"
        CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 2.0

            #include "UnityCG.cginc"
            #include "UnityUI.cginc"

            #pragma multi_compile_local _ UNITY_UI_CLIP_RECT
            #pragma multi_compile_local _ UNITY_UI_ALPHACLIP

            struct appdata_t
            {
                float4 vertex   : POSITION;
                float4 color    : COLOR;
                float2 texcoord : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 vertex        : SV_POSITION;
                fixed4 color         : COLOR;
                float2 texcoord      : TEXCOORD0;
                float4 worldPosition : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            sampler2D _MainTex;
            fixed4 _Color;
            fixed4 _TextureSampleAdd;
            float4 _ClipRect;
            float4 _MainTex_ST;

            // Globales del vendor. Nunca en Properties: ver cabecera.
            float _FOV;
            float _FOVEnabled;

            float4 WarpToViewModelFOV(float4 vertexOS)
            {
                float3 viewPos = UnityObjectToViewPos(vertexOS.xyz);
                float k = 1.0 / (-tan(_FOV * UNITY_PI / 360.0) * UNITY_MATRIX_P[1][1]);
                viewPos.xy *= k;
                return mul(UNITY_MATRIX_P, float4(viewPos, 1.0));
            }

            v2f vert(appdata_t v)
            {
                v2f OUT;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);

                // Se guarda el vertice SIN warpear: UnityGet2DClipping compara contra _ClipRect,
                // que vive en el espacio del canvas. Recortar con la posicion ya reproyectada
                // moveria el rectangulo de mascara junto con la geometria.
                OUT.worldPosition = v.vertex;

                OUT.vertex = _FOVEnabled > 0.5
                    ? WarpToViewModelFOV(v.vertex)
                    : UnityObjectToClipPos(v.vertex);

                OUT.texcoord = TRANSFORM_TEX(v.texcoord, _MainTex);
                OUT.color = v.color * _Color;
                return OUT;
            }

            fixed4 frag(v2f IN) : SV_Target
            {
                half4 color = (tex2D(_MainTex, IN.texcoord) + _TextureSampleAdd) * IN.color;

                #ifdef UNITY_UI_CLIP_RECT
                color.a *= UnityGet2DClipping(IN.worldPosition.xy, _ClipRect);
                #endif

                #ifdef UNITY_UI_ALPHACLIP
                clip (color.a - 0.001);
                #endif

                return color;
            }
        ENDCG
        }
    }
}
