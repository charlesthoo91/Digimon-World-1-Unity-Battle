Shader "DW1Remake/Backdrop"
{
    Properties
    {
        _BaseMap ("Texture", 2D) = "white" {}
        _Texel ("Texel size (1/w, 1/h)", Vector) = (0.001, 0.001, 0, 0)
        _BlurMax ("Tilt-shift blur at the screen edge (texels)", Float) = 3
        _FocusCenter ("Focus band centre (0 bottom, 1 top)", Float) = 0.5
        _FocusHalf ("Focus band half height", Float) = 0.22
        _Sharpen ("Sharpen amount", Float) = 0.5
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" "Queue"="Geometry" }
        Pass
        {
            Name "Backdrop"
            Cull Off
            ZWrite On

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float4 _Texel;
                float _BlurMax;
                float _FocusCenter;
                float _FocusHalf;
                float _Sharpen;
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
                float4 screenPos : TEXCOORD1;
            };

            Varyings vert(Attributes input)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                o.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                o.screenPos = ComputeScreenPos(o.positionCS);
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                float sy = i.screenPos.y / i.screenPos.w;
                float d = saturate((abs(sy - _FocusCenter) - _FocusHalf) / max(0.0001, 0.5 - _FocusHalf));
                float radius = d * d * _BlurMax;

                half3 c = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv).rgb;

                if (radius > 0.05)
                {
                    // soft disc blur, stronger towards the top and bottom of the screen
                    half3 sum = c;
                    for (int k = 0; k < 12; k++)
                    {
                        float a = k * 2.39996323;
                        float rad = sqrt((k + 0.5) / 12.0);
                        float2 o = float2(cos(a), sin(a)) * rad * radius * _Texel.xy;
                        sum += SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv + o).rgb;
                    }
                    c = sum / 13.0;
                }
                else if (_Sharpen > 0.001)
                {
                    // gentle sharpening inside the focus band
                    half3 n = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv + float2(_Texel.x, 0)).rgb
                            + SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv - float2(_Texel.x, 0)).rgb
                            + SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv + float2(0, _Texel.y)).rgb
                            + SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv - float2(0, _Texel.y)).rgb;
                    c = saturate(c + (c - n * 0.25) * _Sharpen);
                }
                return half4(c, 1);
            }
            ENDHLSL
        }
    }
}
