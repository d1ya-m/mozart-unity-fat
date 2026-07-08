Shader "Custom/PortalContentUnlit"
{
    Properties
    {
        _BaseMap ("Base Map", 2D) = "white" {}
        _BaseColor ("Base Color", Color) = (1,1,1,1)
        _EnvDepthBias ("Env Depth Bias", Float) = 0.015
        [Enum(Off,0,Occ,1,VirtualDepth,2,EnvDepth,3,Compare,4)] _DebugMode ("Debug Mode", Float) = 0
        _DebugRange ("Debug Range (m)", Float) = 5.0

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

            Blend SrcAlpha OneMinusSrcAlpha

            ZWrite Off
            ZTest Always
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile _ HARD_OCCLUSION SOFT_OCCLUSION

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
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
                META_DEPTH_VERTEX_OUTPUT(3)
                UNITY_VERTEX_OUTPUT_STEREO
            };

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float4 _BaseColor;
                float _EnvDepthBias;
                float _DebugMode;
                float _DebugRange;
            CBUFFER_END

            // ----- Portal box (set from C# each frame via Shader.SetGlobal*) -----
            float4x4 _PortalWorldToLocal;
            float3   _PortalBoxCenter;
            float3   _PortalBoxExtents;

            // Returns the world position where the eye->fragment ray first enters
            // the portal cube. Falls back to fragWorld if the ray misses the box.
            float3 PortalFrontFaceWorld(float3 fragWorld)
            {
                float3 camWorld = GetCurrentViewPosition();


                float3 dirWorld = fragWorld - camWorld;          // along the view ray

                // Transform the ray into the box's local space. The ray parameter t
                // is preserved because origin and direction use the same matrix.
                float3 oL = mul(_PortalWorldToLocal, float4(camWorld, 1.0)).xyz - _PortalBoxCenter;
                float3 dL = mul(_PortalWorldToLocal, float4(dirWorld, 0.0)).xyz;

                float3 invD = 1.0 / dL;
                float3 t0 = (-_PortalBoxExtents - oL) * invD;
                float3 t1 = ( _PortalBoxExtents - oL) * invD;
                float3 ts = min(t0, t1);
                float3 tb = max(t0, t1);
                float tNear = max(max(ts.x, ts.y), ts.z);
                float tFar  = min(min(tb.x, tb.y), tb.z);

                if (tFar < tNear || tFar < 0.0)
                    return camWorld + normalize(dirWorld) * 0.01; // ray misses → keep portal solid
          

                float tEnter = max(tNear, 0.0);  // camera inside box -> front at camera
                return camWorld + tEnter * dirWorld;
            }


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
                half4 texColor = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv);
                float occ = META_DEPTH_GET_OCCLUSION_VALUE_WORLDPOS(PortalFrontFaceWorld(input.posWorld), _EnvDepthBias);

                #if defined(HARD_OCCLUSION) || defined(SOFT_OCCLUSION)
                if (_DebugMode > 0.5)
                {
                    float4 depthSpace = mul(_EnvironmentDepthReprojectionMatrices[unity_StereoEyeIndex],
                                            float4(input.posWorld, 1.0));
                    float2 envUV      = (depthSpace.xy / depthSpace.w + 1.0) * 0.5;
                    float virtualLinear = (1.0 / ((depthSpace.z / depthSpace.w)
                                          + _EnvironmentDepthZBufferParams.y)) * _EnvironmentDepthZBufferParams.x;
                    float envLinear = SampleEnvironmentDepthLinear(envUV);

                    if (_DebugMode < 1.5)
                        return half4(occ, occ, occ, 1);

                    if (_DebugMode < 2.5)
                    {
                        float t = saturate(virtualLinear / _DebugRange);
                        return half4(t, 1.0 - t, 0, 1);
                    }

                    if (_DebugMode < 3.5)
                    {
                        if (envLinear > 50.0) return half4(0, 0, 1, 1);
                        float t = saturate(envLinear / _DebugRange);
                        return half4(t, 1.0 - t, 0, 1);
                    }

                    if (envLinear > 50.0) return half4(0, 0, 1, 1);
                    return (envLinear < virtualLinear) ? half4(1,0,0,1) : half4(0,1,0,1);
                }
                #endif
                
                half4 col = texColor * _BaseColor;
                col.a *= saturate(occ);
                clip(col.a - 0.001);
                return col;
            }





            ENDHLSL
        }

    }
}
