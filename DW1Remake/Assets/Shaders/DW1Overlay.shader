Shader "DW1Remake/Overlay"
{
    Properties
    {
        _BaseMap ("Texture", 2D) = "white" {}
        _Texel ("Texel size (1/w, 1/h)", Vector) = (0.01, 0.01, 0, 0)
        _BlurMax ("Tilt-shift blur at the screen edge (texels)", Float) = 3
        _FocusCenter ("Focus band centre (0 bottom, 1 top)", Float) = 0.5
        _FocusHalf ("Focus band half height", Float) = 0.22
        _Sharpen ("Sharpen amount", Float) = 0.5
    }
    SubShader
    {
        Tags { "RenderType"="TransparentCutout" "RenderPipeline"="UniversalPipeline" "Queue"="AlphaTest" }
        Pass
        {
            Name "Overlay"
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

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; float4 screenPos : TEXCOORD1; };

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

                // transparent texels are stored as (0,0,0,0), so colour is divided by alpha after filtering
                half4 s0 = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv);
                half3 sumC = s0.rgb;
                half sumA = s0.a;
                half n = 1;

                if (radius > 0.05)
                {
                    for (int k = 0; k < 12; k++)
                    {
                        float a = k * 2.39996323;
                        float rad = sqrt((k + 0.5) / 12.0);
                        float2 o = float2(cos(a), sin(a)) * rad * radius * _Texel.xy;
                        half4 s = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv + o);
                        sumC += s.rgb; sumA += s.a;
                    }
                    n = 13;
                }
                half alpha = sumA / n;
                clip(alpha - 0.5);
                half3 c = sumC / max(sumA, 0.0001);

                if (radius <= 0.05 && _Sharpen > 0.001)
                {
                    half4 n1 = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv + float2(_Texel.x, 0));
                    half4 n2 = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv - float2(_Texel.x, 0));
                    half4 n3 = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv + float2(0, _Texel.y));
                    half4 n4 = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv - float2(0, _Texel.y));
                    half na = n1.a + n2.a + n3.a + n4.a;
                    if (na > 3.5)
                    {
                        half3 nb = (n1.rgb + n2.rgb + n3.rgb + n4.rgb) * 0.25;
                        c = saturate(c + (c - nb) * _Sharpen);
                    }
                }
                return half4(c, 1);
            }
            ENDHLSL
        }
    }
}
