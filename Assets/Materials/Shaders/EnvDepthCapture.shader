// Metric-depth capture blit for Stage-1 keyframe capture.
//
// Blits the SDK's global _EnvironmentDepthTexture (a Tex2DArray, one slice per eye)
// into an RFloat RenderTexture, outputting LINEAR METRIC eye-depth in metres.
//
// Root cause of the original 0.13 m constant bug: the SDK's SAMPLE_TEXTURE2D_X
// macro only treats the texture as an array when a stereo shader keyword is active.
// A plain Graphics.Blit has no stereo keyword, so the macro fell back to sampling a
// plain Texture2D handle against a Tex2DArray resource -> constant garbage value.
//
// Fix: C# binds the SDK's global depth array to our own material property
// _EnvDepthArray (distinct name, no redefinition clash) and we sample slice 0
// explicitly, bypassing the stereo macro entirely.
Shader "Custom/EnvDepthCapture"
{
    Properties { }
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.meta.xr.sdk.core/Shaders/EnvironmentDepth/URP/EnvironmentOcclusionURP.hlsl"

            // Explicit Tex2DArray view — C# binds _EnvironmentDepthTexture to this
            // property so we can sample slice 0 (left eye) directly without the
            // stereo macro. Distinct name avoids redefinition with the SDK include.
            Texture2DArray<float> _EnvDepthArray;
            SamplerState sampler_EnvDepthArray;

            struct Attributes { uint vertexID : SV_VertexID; };
            struct Varyings   { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };

            Varyings Vert(Attributes IN)
            {
                Varyings OUT;
                float2 uv = float2((IN.vertexID << 1) & 2, IN.vertexID & 2);
                OUT.positionCS = float4(uv * 2.0 - 1.0, 0.0, 1.0);
                OUT.uv = float2(uv.x, 1.0 - uv.y); // flip V to match texture orientation
                return OUT;
            }

            float Frag(Varyings IN) : SV_Target
            {
                // Sample left-eye slice explicitly (no stereo macro).
                float raw = _EnvDepthArray.SampleLevel(sampler_EnvDepthArray, float3(IN.uv, 0), 0);

                // Linearise using the SDK's own ZBufferParams (set globally each frame).
                float ndc = raw * 2.0 - 1.0;
                if (ndc >= 1.0)
                    return 0.0; // no-data sentinel -> zero so PNG encodes as 0 mm
                float metres = (1.0 / (ndc + _EnvironmentDepthZBufferParams.y))
                               * _EnvironmentDepthZBufferParams.x;
                return (metres >= 100.0) ? 0.0 : metres;
            }
            ENDHLSL
        }
    }
}
