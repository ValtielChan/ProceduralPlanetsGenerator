Shader "Valtiel/Planet/Star"
{
    // Unlit self-illuminating shader for a star surface. The Planet Generator's
    // Star mode exports two cubemaps: a colored "photosphere" map and a
    // greyscale emission map. The final color is
    //
    //     output = baseSample * tint * (emissionFloor + emissionBoost * emissionSample)
    //
    // The tint is HDR (typically 2.5+ per channel) and emissionBoost can push
    // the brightest cells well above 1.0 so URP's bloom post-process picks
    // them up. The shader is pure unlit — scene lights have no effect on the
    // star itself, since stars emit their own light.
    Properties
    {
        [Header(Star Surface)]
        _BaseMap    ("Base Map (2D)",    2D)   = "white" {}
        _BaseCube   ("Base Cubemap",     Cube) = "" {}
        [Toggle(_USE_STAR_CUBE)] _UseStarCube ("Use Base Cubemap", Float) = 0
        [HDR] _BaseColor ("Base Tint (HDR)", Color) = (2.5, 2.35, 1.8, 1)

        [Header(Emission)]
        _EmissionCube ("Emission Cubemap", Cube) = "" {}
        [Toggle(_USE_EMISSION_CUBE)] _UseEmissionCube ("Use Emission Cubemap", Float) = 0
        _EmissionFloor ("Emission Floor", Range(0, 3)) = 1.0
        _EmissionBoost ("Emission Boost", Range(0, 8)) = 1.5
    }

    SubShader
    {
        Tags
        {
            "RenderType"     = "Opaque"
            "Queue"          = "Geometry"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }
        LOD 100

        Pass
        {
            Name "StarUnlit"
            Tags { "LightMode" = "UniversalForward" }

            Cull Back
            ZWrite On

            HLSLPROGRAM
            #pragma vertex   StarVert
            #pragma fragment StarFrag
            #pragma multi_compile_instancing
            #pragma multi_compile_fog
            #pragma shader_feature_local _USE_STAR_CUBE
            #pragma shader_feature_local _USE_EMISSION_CUBE

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float4 _BaseColor;
                float  _EmissionFloor;
                float  _EmissionBoost;
            CBUFFER_END

            TEXTURE2D(_BaseMap);        SAMPLER(sampler_BaseMap);
            TEXTURECUBE(_BaseCube);     SAMPLER(sampler_BaseCube);
            TEXTURECUBE(_EmissionCube); SAMPLER(sampler_EmissionCube);

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                float3 normalOS   : TEXCOORD1;
                float  fogFactor  : TEXCOORD2;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings StarVert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                VertexPositionInputs posIn = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = posIn.positionCS;
                output.normalOS   = input.normalOS;
                output.uv         = TRANSFORM_TEX(input.uv, _BaseMap);
                output.fogFactor  = ComputeFogFactor(posIn.positionCS.z);
                return output;
            }

            half4 StarFrag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float3 dir = normalize(input.normalOS);

                // Base photosphere color: cubemap by direction, or 2D fallback.
                #if defined(_USE_STAR_CUBE)
                    half3 base = SAMPLE_TEXTURECUBE(_BaseCube, sampler_BaseCube, dir).rgb;
                #else
                    half3 base = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).rgb;
                #endif

                // Emission term: floor + scale × cubemap sample (greyscale).
                // Without the cube, emission is just the floor — still bright
                // enough to look like a star with the default HDR tint.
                half emission = 1.0;
                #if defined(_USE_EMISSION_CUBE)
                    emission = SAMPLE_TEXTURECUBE(_EmissionCube, sampler_EmissionCube, dir).r;
                #endif

                half3 color = base * _BaseColor.rgb * (_EmissionFloor + _EmissionBoost * emission);

                // Fog is applied at the end so distant stars don't show through
                // atmospheric fog. Comment this out for "always-bright" stars.
                color = MixFog(color, input.fogFactor);
                return half4(color, 1.0);
            }
            ENDHLSL
        }

        // Shadow caster: lets the star cast shadows on nearby planets. Inline
        // (rather than UsePass) because UsePass against URP/Unlit's ShadowCaster
        // is fragile across URP versions and can silently fail the whole shader.
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull Back

            HLSLPROGRAM
            #pragma target 2.0
            #pragma multi_compile_instancing
            #pragma vertex   ShadowVert
            #pragma fragment ShadowFrag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            struct ShadowA
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct ShadowV
            {
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            float4 GetShadowPositionHClip(float3 positionWS, float3 normalWS)
            {
                #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                    float3 lightDirectionWS = normalize(_LightPosition - positionWS);
                #else
                    float3 lightDirectionWS = _LightDirection;
                #endif
                float4 positionCS = TransformWorldToHClip(
                    ApplyShadowBias(positionWS, normalWS, lightDirectionWS));
                #if UNITY_REVERSED_Z
                    positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #else
                    positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #endif
                return positionCS;
            }

            ShadowV ShadowVert(ShadowA input)
            {
                ShadowV o;
                UNITY_SETUP_INSTANCE_ID(input);
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float3 normalWS   = TransformObjectToWorldNormal(input.normalOS);
                o.positionCS = GetShadowPositionHClip(positionWS, normalWS);
                return o;
            }

            half4 ShadowFrag(ShadowV i) : SV_Target { return 0; }
            ENDHLSL
        }

        // Depth-only: needed so the star participates correctly in URP's
        // depth prepass / depth-of-field / opaque depth buffer.
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ColorMask 0
            Cull Back

            HLSLPROGRAM
            #pragma target 2.0
            #pragma multi_compile_instancing
            #pragma vertex   DepthVert
            #pragma fragment DepthFrag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct DepthA { float4 positionOS : POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct DepthV { float4 positionCS : SV_POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };

            DepthV DepthVert(DepthA i)
            {
                DepthV o;
                UNITY_SETUP_INSTANCE_ID(i);
                o.positionCS = TransformObjectToHClip(i.positionOS.xyz);
                return o;
            }

            half4 DepthFrag(DepthV i) : SV_Target { return 0; }
            ENDHLSL
        }
    }

    FallBack Off
}
