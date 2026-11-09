// Ours (mod "Remastered maps", not in the original game): makes a bigger copy of a map picture or map piece on the player's PC.
// Pass 0: Catmull-Rom bicubic enlargement. Colours are weighted by their alpha, so see-through pixels (colour 0) do not darken
// the edges of a piece, and each result is held between the darkest and brightest of its four nearest source pixels (no halos).
// Pass 1: contrast-adaptive sharpening of the enlarged copy (flat areas are sharpened less than detailed ones).
Shader "DW1Remake/Upscale"
{
    Properties
    {
        _MainTex ("Source", 2D) = "white" {}
        _SrcSize ("Source size (w, h, 1/w, 1/h)", Vector) = (256, 256, 0.00390625, 0.00390625)
        _Sharp ("Sharpening (0 - 1)", Float) = 0.6
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        ZTest Always
        ZWrite Off
        Cull Off

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        TEXTURE2D(_MainTex);
        CBUFFER_START(UnityPerMaterial)
            float4 _SrcSize;
            float _Sharp;
        CBUFFER_END

        struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
        struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };

        Varyings vert(Attributes v)
        {
            Varyings o;
            o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
            o.uv = v.uv;
            return o;
        }

        float4 Texel(int2 p)
        {
            p = clamp(p, int2(0, 0), int2(_SrcSize.xy) - 1);
            return LOAD_TEXTURE2D(_MainTex, p);
        }

        float4 CatmullRom(float t)
        {
            float t2 = t * t, t3 = t2 * t;
            return float4(-0.5 * t3 + t2 - 0.5 * t,
                          1.5 * t3 - 2.5 * t2 + 1.0,
                          -1.5 * t3 + 2.0 * t2 + 0.5 * t,
                          0.5 * t3 - 0.5 * t2);
        }
        ENDHLSL

        Pass
        {
            Name "Enlarge"
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            float4 frag(Varyings i) : SV_Target
            {
                float2 p = i.uv * _SrcSize.xy - 0.5;
                int2 b = (int2)floor(p);
                float2 f = p - floor(p);
                float4 wx = CatmullRom(f.x), wy = CatmullRom(f.y);
                float3 rgb = 0; float a = 0;
                [unroll] for (int y = 0; y < 4; y++)
                {
                    [unroll] for (int x = 0; x < 4; x++)
                    {
                        float w = wx[x] * wy[y];
                        float4 c = Texel(b + int2(x - 1, y - 1));
                        rgb += c.rgb * c.a * w;
                        a += c.a * w;
                    }
                }
                // the four nearest source pixels bound the result (the see-through ones do not count)
                float3 lo = 1, hi = 0; float any = 0;
                [unroll] for (int k = 0; k < 4; k++)
                {
                    float4 c = Texel(b + int2(k & 1, k >> 1));
                    if (c.a > 0.5) { lo = min(lo, c.rgb); hi = max(hi, c.rgb); any = 1; }
                }
                rgb = a > 0.0001 ? rgb / a : 0;
                if (any > 0.5) rgb = clamp(rgb, lo, hi);
                return float4(saturate(rgb), saturate(a));
            }
            ENDHLSL
        }

        Pass
        {
            Name "Sharpen"
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            float4 frag(Varyings i) : SV_Target
            {
                int2 p = (int2)(i.uv * _SrcSize.xy);
                float4 c = Texel(p);
                float3 n = Texel(p + int2(0, 1)).rgb, s = Texel(p - int2(0, 1)).rgb;
                float3 e = Texel(p + int2(1, 0)).rgb, w = Texel(p - int2(1, 0)).rgb;
                float3 mn = min(c.rgb, min(min(n, s), min(e, w)));
                float3 mx = max(c.rgb, max(max(n, s), max(e, w)));
                // how much room there is before clipping, against the local contrast: little room or flat colour = little sharpening
                float3 amount = sqrt(saturate(min(mn, 1.0 - mx) / max(mx, 0.0001)));
                float3 k = -amount * lerp(0.125, 0.2, saturate(_Sharp));
                float3 rgb = (c.rgb + k * (n + s + e + w)) / (1.0 + 4.0 * k);
                return float4(saturate(rgb), c.a);
            }
            ENDHLSL
        }
    }
}
