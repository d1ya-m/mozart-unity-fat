Shader "Custom/PortalContentUnlit"
{
    Properties
    {
        _BaseMap ("Base Map", 2D) = "white" {}
        _BaseColor ("Base Color", Color) = (1,1,1,1)
        _EnvDepthBias ("Env Depth Bias", Float) = 0.015
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry+500" "RenderPipeline"="UniversalPipeline" }

        Stencil
        {
            Ref 6
            Comp Equal
            Pass Keep
        }

        Pass
        {
            Name "UniversalForward"
            Tags { "LightMode"="UniversalForward" }

            // Alpha blend so occluded pixels can fade/clip out.
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest Always
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            // Toggling these keywords on the material enables environment-depth
            // occlusion. With neither defined, the shader renders plain (no
            // occlusion) - safe fallback if the depth texture isn't available.
            #pragma multi_compile _ HARD_OCCLUSION SOFT_OCCLUSION

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            // Gives us _CameraDepthTexture + SampleSceneDepth(). The portal MASK
            // mesh (Custom/StencilMask, Queue Geometry-1, ZWrite On) renders into
            // this depth texture BEFORE the portal content, so at every portal
            // pixel the camera depth holds the OPENING's front-most face depth -
            // for ANY shape (cube, sphere, arbitrary object.obj), no shape math.
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.meta.xr.sdk.core/Shaders/EnvironmentDepth/URP/EnvironmentOcclusionURP.hlsl"

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                // Carries the data the Meta depth macros need (world pos etc).
                META_DEPTH_VERTEX_OUTPUT(3)
                UNITY_VERTEX_OUTPUT_STEREO
            };

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float4 _BaseColor;
                float _EnvDepthBias;
            CBUFFER_END

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                META_DEPTH_INITIALIZE_VERTEX_OUTPUT(output, input.positionOS);
                output.positionHCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                half4 col = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv) * _BaseColor;

                // OCCLUSION REFERENCE = THE PORTAL OPENING, NOT THE VIRTUAL ROOM.
                //
                // The portal content material is painted on the VIRTUAL ROOM mesh,
                // so input.posWorld is a point deep inside the virtual scene. Using
                // it as the reference made the occlusion ask "is the real object in
                // front of the virtual wall?" - so a real cupboard with a distant
                // virtual wall behind it punched holes in the portal (the cupboard
                // bug).
                //
                // Instead we read the depth the portal MASK mesh already wrote into
                // _CameraDepthTexture and reconstruct that opening point's world
                // position. Feeding THAT to the occlusion macro makes the question
                // "is the real object in front of the portal OPENING?":
                //   real in front of opening  -> occ=0 -> show real world (hands)
                //   real behind/inside opening -> occ=1 -> portal stays solid
                //     (cupboards behind it AND people stepping into the volume)
                // This reads rasterized geometry, so it works for any opening shape.
                #if defined(HARD_OCCLUSION) || defined(SOFT_OCCLUSION)
                    // Screen UV of THIS pixel. GetNormalizedScreenSpaceUV handles the
                    // render-target scale AND the per-eye stereo viewport correctly -
                    // doing this by hand (positionHCS.xy / _ScaledScreenParams.xy)
                    // sampled the depth texture at a shifted pixel, which displaced
                    // the reconstructed opening (hole misaligned, worse with distance
                    // and when the viewport changed in add/delete mode).
                    float2 screenUV = GetNormalizedScreenSpaceUV(input.positionHCS);
                    float openingRawDepth = SampleSceneDepth(screenUV);
                    // Reconstruct the opening's world position. unity_MatrixInvVP is
                    // already the per-eye inverse view-projection in single-pass stereo.
                    float3 openingWorld = ComputeWorldSpacePosition(screenUV, openingRawDepth, UNITY_MATRIX_I_VP);

                    float occ = META_DEPTH_GET_OCCLUSION_VALUE_WORLDPOS(openingWorld, _EnvDepthBias);
                    col.a *= saturate(occ);
                    clip(col.a - 0.001);
                #endif

                return col;
            }
            ENDHLSL
        }
    }
}
