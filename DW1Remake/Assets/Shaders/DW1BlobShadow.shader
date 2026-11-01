Shader "DW1Remake/BlobShadow"
{
    Properties
    {
        _Color ("Colour (alpha = darkness)", Color) = (0, 0, 0, 0.4)
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "RenderPipeline"="UniversalPipeline" "Queue"="Transparent" }
        Pass
        {
            Name "Blob"
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            Varyings vert(Attributes input)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                o.uv = input.uv;
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                float2 p = i.uv - 0.5;
                float d = saturate(1.0 - length(p) * 2.0);
                d = d * d * (3.0 - 2.0 * d);
                return half4(_Color.rgb, _Color.a * d);
            }
            ENDHLSL
        }
    }
}
