// ADR-149 R4b tanda 2: la máscara de piel del cuerpo (Skin.shadergraph del vendor) con las zonas rotas destapadas.
// Las máscaras del vendor guardan en R lo que la ropa OCULTA (blanco = no se pinta la piel). Aquí se juntan dos capas (lo
// del torso y lo de encima: se oculta lo que oculte cualquiera) y se destapa la piel de las zonas marcadas en _Reveal*,
// usando el mapa de zonas horneado en el espacio UV del cuerpo (R = zona + 1). Lo usa Graphics.Blit.
Shader "Hidden/Backrooms/Skin Mask Compose"
{
    Properties
    {
        _MainTex("Máscara de la capa de dentro", 2D) = "black" {}
        _MaskB("Máscara de la capa de encima", 2D) = "black" {}
        _ZoneMap("Mapa de zonas del cuerpo", 2D) = "black" {}
    }

    SubShader
    {
        ZTest Always
        Cull Off
        ZWrite Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            sampler2D _MaskB;
            sampler2D _ZoneMap;
            float4 _RevealA;
            float4 _RevealB;
            float4 _RevealC;
            float4 _RevealD;

            fixed4 frag(v2f_img i) : SV_Target
            {
                float hide = max(tex2D(_MainTex, i.uv).r, tex2D(_MaskB, i.uv).r);
                int zone = (int)floor(tex2D(_ZoneMap, i.uv).r * 255.0 + 0.5) - 1;
                if (zone >= 0)
                {
                    float4 reveal = zone < 4 ? _RevealA : (zone < 8 ? _RevealB : (zone < 12 ? _RevealC : _RevealD));
                    if (reveal[zone % 4] > 0.5)
                        hide = 0.0;
                }
                return fixed4(hide, hide, hide, 1.0);
            }
            ENDCG
        }
    }
}
