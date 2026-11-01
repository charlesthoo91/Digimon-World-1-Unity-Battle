// The game's technique effects: textured, lit by nothing, added to the picture (PlayStation blend mode 1).
Shader "DW1Remake/Effect"
{
    Properties { _MainTex ("Texture", 2D) = "white" {} _Color ("Colour", Color) = (1,1,1,1) _Rect ("UV offset and scale", Vector) = (0,0,1,1) }
    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" }
        Pass
        {
            Blend One One
            ZWrite Off
            Cull Off
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float4 _Color, _Rect;
            struct V { float4 pos : POSITION; float2 uv : TEXCOORD0; };
            struct F { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
            F vert(V v) { F o; o.pos = UnityObjectToClipPos(v.pos); o.uv = _Rect.xy + v.uv * _Rect.zw; return o; }
            float4 frag(F i) : SV_Target { return tex2D(_MainTex, i.uv) * _Color; }
            ENDHLSL
        }
    }
}
