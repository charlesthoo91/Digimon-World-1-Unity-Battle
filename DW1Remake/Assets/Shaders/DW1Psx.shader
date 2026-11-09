// A PlayStation TMD packet: the picture times the packet's colour (128 = as the picture is; up to nearly double, clamped at white),
// or the colour alone for a packet with no picture. Colour 0x0000 of a picture is see-through. Blend, depth write and depth test come
// from the material (the semi-transparent STP colours are drawn by a second material with the packet's blend mode).
Shader "DW1Remake/Psx"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Textured ("Textured", Float) = 1
        _Opacity ("Opacity", Float) = 1
        [HideInInspector] _SrcBlend ("Src blend", Float) = 1
        [HideInInspector] _DstBlend ("Dst blend", Float) = 0
        [HideInInspector] _BlendOp ("Blend op", Float) = 0
        [HideInInspector] _ZWrite ("ZWrite", Float) = 1
        [HideInInspector] _ZTest ("ZTest", Float) = 4
        [HideInInspector] _Cull ("Cull", Float) = 0
    }
    SubShader
    {
        Tags { "Queue" = "Geometry" "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        Pass
        {
            Name "Psx"
            Blend [_SrcBlend] [_DstBlend]
            BlendOp [_BlendOp]
            ZWrite [_ZWrite]
            ZTest [_ZTest]
            Cull [_Cull]
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            CBUFFER_START(UnityPerMaterial)
                float _Textured;
                float _Opacity;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; float4 color : COLOR; };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; float4 color : COLOR; };

            Varyings vert(Attributes v)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.uv = v.uv;
                o.color = v.color;
                return o;
            }

            float4 frag(Varyings i) : SV_Target
            {
                float3 c = i.color.rgb;
                if (_Textured > 0.5)
                {
                    float4 t = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv);
                    clip(t.a - 0.5);
                    c = saturate(t.rgb * c * 1.9921875);
                }
                return float4(c, _Opacity);
            }
            ENDHLSL
        }
    }
}
