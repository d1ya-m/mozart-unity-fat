Shader "Custom/ReplaceWithCamera_URP"
{
    Properties
    {
        [IntRange] _StencilID ("Stencil ID", Range(0,255)) = 6
    }

    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Overlay" "RenderType"="Opaque" }

        // testujeme stencil: přepiš jen tam, kde stencil == _StencilID
        Stencil
        {
            Ref [_StencilID]
            Comp Equal
            Pass Keep
        }

        Pass
        {
            Name "ReplaceWithCamera"
            Tags { "LightMode"="UniversalForward" }

            Cull Off
            ZWrite Off
            // ZTest: LEqual nebo Equal. LEqual povolí přepis tam, kde je hloubka méně/rovno.
            ZTest LEqual

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            // camera color / opaque texture (URP)
            TEXTURE2D(_CameraColorTexture); 
            SAMPLER(sampler_CameraColorTexture);

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float4 screenPos : TEXCOORD0; // for sampling camera texture
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                o.positionHCS = TransformObjectToHClip(v.positionOS.xyz);
                // ComputeScreenPos gives XY = uv in 0..1, w = 1/clip.w used for proper sampling
                o.screenPos = ComputeScreenPos(o.positionHCS);
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                // screen UV (divide by w)
                float2 screenUV = i.screenPos.xy / i.screenPos.w;

                // sample camera color texture (clamp inside [0,1] to avoid artifacts)
                #if defined(_CameraColorTexture)
                    half4 camCol = SAMPLE_TEXTURE2D(_CameraColorTexture, sampler_CameraColorTexture, screenUV);
                #else
                    // fallback: black if not available
                    half4 camCol = half4(0,0,0,1);
                #endif

                return camCol;
            }
            ENDHLSL
        }
    }
}
