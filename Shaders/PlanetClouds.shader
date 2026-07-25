Shader "Valtiel/Planet/Clouds"
{
    // Transparent cloud shell for a planet. Attach to a child sphere slightly
    // larger than the planet body and feed it a cloud cubemap (RGB = color,
    // A = density). Shaded by the URP main directional light so the night
    // side fades to ambient, and alpha-blended on top of the planet surface.
    Properties
    {
        [Header(Clouds)]
        _CloudCube ("Cloud Cubemap", Cube) = "" {}
        [HDR] _CloudTint ("Cloud Tint", Color) = (1,1,1,1)
        _Density       ("Density Scale",   Range(0, 3))   = 1.0
        _AmbientFloor  ("Night-side Fade", Range(0, 0.5)) = 0.06
    }

    SubShader
    {
        Tags
        {
            "RenderType"     = "Transparent"
            "Queue"          = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }
        LOD 100

        Pass
        {
            Name "CloudForward"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Back

            HLSLPROGRAM
            #pragma vertex   CloudVertex
            #pragma fragment CloudFragment
            #pragma multi_compile_instancing
            #pragma multi_compile_fog
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _CloudTint;
                float  _Density;
                float  _AmbientFloor;
            CBUFFER_END

            TEXTURECUBE(_CloudCube); SAMPLER(sampler_CloudCube);

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                float3 normalOS   : TEXCOORD2;
                float  fogFactor  : TEXCOORD3;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings CloudVertex(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                VertexPositionInputs posIn = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs   nIn   = GetVertexNormalInputs(input.normalOS);

                output.positionCS = posIn.positionCS;
                output.positionWS = posIn.positionWS;
                output.normalWS   = nIn.normalWS;
                output.normalOS   = input.normalOS;
                output.fogFactor  = ComputeFogFactor(posIn.positionCS.z);
                return output;
            }

            half4 CloudFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float3 normalOS = normalize(input.normalOS);
                float3 normalWS = normalize(input.normalWS);

                half4 sample = SAMPLE_TEXTURECUBE(_CloudCube, sampler_CloudCube, normalOS);

                // Main directional light Lambert + ambient floor so the night
                // hemisphere doesn't go to pitch black (matches Earth-from-space
                // photography where high clouds still pick up scattered light).
                Light mainLight = GetMainLight(TransformWorldToShadowCoord(input.positionWS));
                float ndl = saturate(dot(normalWS, mainLight.direction));
                float light = saturate(ndl * mainLight.shadowAttenuation + _AmbientFloor);

                half3 rgb = sample.rgb * _CloudTint.rgb * mainLight.color * light;
                half  a   = saturate(sample.a * _Density);

                rgb = MixFog(rgb, input.fogFactor);
                return half4(rgb, a);
            }
            ENDHLSL
        }
    }

    FallBack "Universal Render Pipeline/Unlit"
}
