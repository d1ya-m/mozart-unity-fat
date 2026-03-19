Shader "Custom/UnlitMaskObjects_URP"
{
    Properties
    {
        _Color ("Color", Color) = (1,1,1,1)
        _MainTex ("Albedo (RGB)", 2D) = "white" {}
        [IntRange] _StencilID ("Stencil ID", Range(0, 255)) = 0
    }

    SubShader
    {
        Tags 
        { 
            "RenderType"="Opaque"
            "Queue"="Geometry"
            "RenderPipeline"="UniversalPipeline"
        }

        // 🔥 STENCIL CLIP PRO MODEL – KRESLI POUZE TAM, KDE st != StencilID
        Stencil
        {
            Ref [_StencilID]
            Comp NotEqual   // pokud stencil == ref, nekreslit → vyříznutí
            Pass Keep
        }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            Blend One Zero
            ZWrite On

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            
            // URP includes
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            float4 _MainTex_ST;
            float4 _Color;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv          : TEXCOORD0;
            };

            Varyings vert (Attributes v)
            {
                Varyings o;
                o.positionHCS = TransformObjectToHClip(v.positionOS.xyz);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                return o;
            }

            half4 frag (Varyings i) : SV_Target
            {
                float4 col = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv);
                return col * _Color;
            }

            ENDHLSL
        }
    }
}
