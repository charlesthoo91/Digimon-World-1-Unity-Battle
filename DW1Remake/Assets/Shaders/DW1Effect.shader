// The game's technique effects: textured, lit by nothing, added to the picture (PlayStation blend mode 1) unless the material picks another mode (MmdModel.SetPsxBlend).
Shader "DW1Remake/Effect"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Color ("Colour", Color) = (1,1,1,1)
        _Rect ("UV offset and scale", Vector) = (0,0,1,1)
        [HideInInspector] _ZTest ("ZTest", Float) = 4
        [HideInInspector] _ZWrite ("ZWrite", Float) = 0
        [HideInInspector] _SrcBlend ("Src blend", Float) = 1
        [HideInInspector] _DstBlend ("Dst blend", Float) = 1
        [HideInInspector] _BlendOp ("Blend op", Float) = 0
        [HideInInspector] _VertexColor ("Use vertex colours", Float) = 0
    }
    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" }
        Pass
        {
            Name "Effect"
            Blend [_SrcBlend] [_DstBlend]
            BlendOp [_BlendOp]
            ZWrite [_ZWrite]
            ZTest [_ZTest]
            Cull Off
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
                float4 _Rect;
                float _VertexColor;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; float4 color : COLOR; };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; float4 color : COLOR; };

            Varyings vert(Attributes v)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                // Gouraud-shaded primitives (the ribbons) carry their colours in the mesh; everything else uses _Color only
                o.color = lerp(float4(1, 1, 1, 1), v.color, _VertexColor);
                float2 t = _Rect.xy + v.uv * _Rect.zw;
                o.uv = float2(t.x, 1.0 - t.y);      // picture coordinates count from the top, Unity textures from the bottom
                return o;
            }

            float4 frag(Varyings i) : SV_Target
            {
                // the PlayStation clamps texture x colour at full brightness (a colour of 128 is 1.0, 255 nearly doubles but stops at white)
                return saturate(SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv) * _Color * i.color);
            }
            ENDHLSL
        }
    }
}
