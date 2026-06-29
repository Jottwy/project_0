// Wall material shader for the grid renderer (Built-in render pipeline).
//
// Matte Lambert surface with a depth Polygon Offset so wall faces LOSE depth-buffer
// ties. Wall side faces end up coplanar with the floor/ceiling slab edges; without a
// bias the GPU flickers between them on that shared plane (z-fighting). "Offset 1, 1"
// pushes the wall slightly back in depth so the floor/ceiling slab wins the tie — no
// geometry moved, no epsilon gap.
//
// The project runs the Built-in pipeline (the STP vendor is Built-in), so this is a
// Built-in surface shader using _MainTex/_Color. Name + guid are unchanged so
// GridWall.mat keeps referencing it.
Shader "Backrooms/GridWallOffset"
{
    Properties
    {
        _MainTex("Base Map", 2D) = "white" {}
        _Color("Base Color", Color) = (1, 1, 1, 1)
        [Enum(UnityEngine.Rendering.CullMode)] _Cull("Cull", Float) = 2 // 0=Off,1=Front,2=Back
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" }

        Cull [_Cull]
        // Polygon offset: wall loses depth ties to coplanar floor/ceiling slabs.
        Offset 1, 1

        CGPROGRAM
        // Lambert: matte diffuse, no specular — fluorescent-lit office look.
        #pragma surface surf Lambert

        sampler2D _MainTex;
        fixed4 _Color;

        struct Input
        {
            float2 uv_MainTex;
        };

        void surf(Input IN, inout SurfaceOutput o)
        {
            // _Color is the per-tile tint (set via MaterialPropertyBlock).
            fixed4 c = tex2D(_MainTex, IN.uv_MainTex) * _Color;
            o.Albedo = c.rgb;
            o.Alpha = c.a;
        }
        ENDCG
    }

    Fallback "Diffuse"
}
