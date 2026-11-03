// Stylized water for lakes, rivers, and the sea. Its color comes from the depth below the surface:
// clear and bright in the shallows, where the ground shows through with light patterns on it, and
// deep blue further out. Foam gathers along the shore, around everything that stands in the water,
// in lines that roll in toward the shore, and in fast water. Ripple normals scroll along the flow
// that each vertex carries in its second texture coordinate, so a river runs downstream, the wind
// carries the large and the small ripples on in two directions, so still water never stands
// still, and the sea can lift its vertices in gentle waves. The red of the vertex color marks fast
// water, the green how much the waves move a vertex, and the blue how strongly foam lines roll in
// toward the shore. One material serves every kind of water, so where water bodies meet, their
// surfaces draw alike as long as their vertices agree. The water reads the camera's depth and
// opaque textures, so the pipeline must provide both, and every animation runs on the water's
// clock of WaterCommon.hlsl. WaterRendererFeature draws the surfaces before the other transparent
// objects, the only pass that draws this shader's light mode. Overlapping water, such as a river
// that reaches into a lake, draws once per pixel through a stencil bit, so it never doubles its
// tint. See docs/ART_PIPELINE.md under Water.
Shader "Mmorpg/Water"
{
    Properties
    {
        [Header(Color)]
        _ShallowColor("Shallow color", Color) = (0.16, 0.7, 0.72, 1)
        _DeepColor("Deep color", Color) = (0.03, 0.2, 0.42, 1)
        _DepthColorDistance("Depth of the deep color (m)", Float) = 2.5
        _Clarity("Visibility through the water (m)", Float) = 2.5
        _HorizonColor("Reflected horizon color", Color) = (0.62, 0.84, 0.95, 1)

        [Header(Surface)]
        [NoScaleOffset][Normal] _NormalMap("Ripple normals", 2D) = "bump" {}
        _NormalTiling("Ripple size (m)", Float) = 7
        _NormalStrength("Ripple strength", Range(0, 2)) = 0.6
        _DetailTiling("Small ripple size (m)", Float) = 1.7
        _DetailStrength("Small ripple strength", Range(0, 2)) = 0.35
        _Drift("Drift in m/s (x, z)", Vector) = (0.06, 0.035, 0, 0)
        _FlowCycle("Flow cycle (s)", Float) = 2.5
        _RippleWind("Large ripple wind in m/s (x, z)", Vector) = (0.15, 0.09, 0, 0)
        _DetailWind("Small ripple wind in m/s (x, z)", Vector) = (-0.13, 0.26, 0, 0)
        _RefractionStrength("Refraction", Range(0, 0.2)) = 0.015
        _ReflectionStrength("Reflection", Range(0, 1)) = 0.65
        _FresnelPower("Fresnel power", Range(1, 8)) = 4
        _SpecularPower("Sun glint sharpness", Range(16, 4096)) = 900
        _SpecularIntensity("Sun glint", Range(0, 16)) = 4
        _SheenIntensity("Broad sun sheen", Range(0, 2)) = 0.35
        _SparkleIntensity("Sparkles", Range(0, 8)) = 1.5
        _RippleShading("Ripple shading", Range(0, 3)) = 1.5
        _SurfacePattern("Light lines on the surface", Range(0, 1)) = 0.12
        _SurfacePatternTiling("Light line size (m)", Float) = 4

        [Header(Foam)]
        [NoScaleOffset] _Masks("Foam (R), caustics (G), noise (B)", 2D) = "black" {}
        _FoamColor("Foam color", Color) = (0.94, 0.98, 1, 1)
        _FoamTiling("Foam size (m)", Float) = 3
        _FoamSoftness("Foam edge softness", Range(0.01, 0.5)) = 0.08
        _ShoreFoamDepth("Shore foam depth (m)", Float) = 0.3
        _IntersectionFoamDistance("Foam around objects (m)", Float) = 0.3
        _ShoreWaveFrequency("Shore wave lines per m of depth", Float) = 0
        _ShoreWaveSpeed("Shore wave lines per second", Float) = 0.2
        _ShoreWaveDepth("Shore wave reach (m)", Float) = 1.4
        _TurbulenceFoam("Foam in fast water", Range(0, 1)) = 0.7

        [Header(Caustics)]
        _CausticsTiling("Light pattern size (m)", Float) = 2.6
        _CausticsStrength("Light pattern strength", Range(0, 2)) = 0.7
        _CausticsDepth("Light pattern fade depth (m)", Float) = 2.5

        [Header(Waves)]
        _WaveAmplitude("Wave height (m)", Float) = 0
        _WaveLength("Wave length (m)", Float) = 14
        _WaveSpeed("Wave speed (m/s)", Float) = 2

        [Header(Edge)]
        _EdgeFade("Shoreline softness (m)", Float) = 0.2
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "WaterForward"
            Tags { "LightMode" = "MmorpgWaterSurface" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Back

            Stencil
            {
                Ref 128
                ReadMask 128
                WriteMask 128
                Comp NotEqual
                Pass Replace
            }

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vertex
            #pragma fragment Fragment
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareOpaqueTexture.hlsl"
            #include "WaterCommon.hlsl"

            TEXTURE2D(_NormalMap);
            SAMPLER(sampler_NormalMap);
            TEXTURE2D(_Masks);
            SAMPLER(sampler_Masks);

            CBUFFER_START(UnityPerMaterial)
                half4 _ShallowColor;
                half4 _DeepColor;
                float _DepthColorDistance;
                float _Clarity;
                half4 _HorizonColor;
                float _NormalTiling;
                half _NormalStrength;
                float _DetailTiling;
                half _DetailStrength;
                float4 _Drift;
                float _FlowCycle;
                float4 _RippleWind;
                float4 _DetailWind;
                half _RefractionStrength;
                half _ReflectionStrength;
                half _FresnelPower;
                half _SpecularPower;
                half _SpecularIntensity;
                half _SheenIntensity;
                half _SparkleIntensity;
                half _RippleShading;
                half _SurfacePattern;
                float _SurfacePatternTiling;
                half4 _FoamColor;
                float _FoamTiling;
                half _FoamSoftness;
                float _ShoreFoamDepth;
                float _IntersectionFoamDistance;
                float _ShoreWaveFrequency;
                float _ShoreWaveSpeed;
                float _ShoreWaveDepth;
                half _TurbulenceFoam;
                float _CausticsTiling;
                half _CausticsStrength;
                float _CausticsDepth;
                float _WaveAmplitude;
                float _WaveLength;
                float _WaveSpeed;
                float _EdgeFade;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 flow : TEXCOORD1;
                half4 color : COLOR;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float2 flow : TEXCOORD1;
                half4 color : TEXCOORD2;
                float3 waveNormal : TEXCOORD3;
                half fogFactor : TEXCOORD4;
            };

            Varyings Vertex(Attributes input)
            {
                Varyings output;
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float3 waveNormal = float3(0, 1, 0);
                if (_WaveAmplitude > 0)
                {
                    float3 normal;
                    float height = WaterWaveHeight(positionWS.xz, _WaveAmplitude, _WaveLength, _WaveSpeed, normal);
                    positionWS.y += height * input.color.g;
                    waveNormal = normalize(lerp(float3(0, 1, 0), normal, input.color.g));
                }

                output.positionWS = positionWS;
                output.positionCS = TransformWorldToHClip(positionWS);
                output.flow = input.flow;
                output.color = input.color;
                output.waveNormal = waveNormal;
                output.fogFactor = ComputeFogFactor(output.positionCS.z);
                return output;
            }

            // Turns a position in world X and Z against the large ripples, for the small ones.
            float2 TurnForDetail(float2 position)
            {
                return float2(position.x * 0.8 - position.y * 0.6, position.x * 0.6 + position.y * 0.8);
            }

            // Ripples at two sizes, the small ones turned against the large ones, as a slope in
            // world X and Z. The wind carries each size on in its own direction, so the surface
            // keeps changing even where the water does not flow. The wind's offset is a share of
            // the texture's size, which the water's clock keeps precise.
            float2 Ripples(float2 position)
            {
                float2 largeWind = WaterCycles(_RippleWind.xy / _NormalTiling);
                half3 large = UnpackNormalScale(
                    SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, (position / _NormalTiling) - largeWind),
                    _NormalStrength);
                float2 smallWind = WaterCycles(TurnForDetail(_DetailWind.xy) / _DetailTiling);
                half3 small = UnpackNormalScale(
                    SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, (TurnForDetail(position) / _DetailTiling) - smallWind),
                    _DetailStrength);
                return large.xy / max(large.z, 0.2) + small.xy / max(small.z, 0.2);
            }

            // Moves a texture along the flow without stretching it: two copies restart every
            // cycle, half a cycle apart, and each fades out while it restarts.
            void FlowPhases(float2 flow, out float2 offsetA, out float2 offsetB, out float weightA)
            {
                float phaseA = WaterCycles(1 / _FlowCycle);
                float phaseB = frac(phaseA + 0.5);
                offsetA = flow * (phaseA * _FlowCycle);
                offsetB = flow * (phaseB * _FlowCycle) + float2(0.37, 0.61);
                weightA = 1 - abs(1 - (2 * phaseA));
            }

            float SceneRawDepth(float2 uv)
            {
                #if UNITY_REVERSED_Z
                    return SampleSceneDepth(uv);
                #else
                    return lerp(UNITY_NEAR_CLIP_VALUE, 1, SampleSceneDepth(uv));
                #endif
            }

            half4 Fragment(Varyings input) : SV_Target
            {
                float3 positionWS = input.positionWS;
                float2 screenUV = GetNormalizedScreenSpaceUV(input.positionCS);
                float3 toCamera = _WorldSpaceCameraPos - positionWS;
                float cameraDistance = length(toCamera);
                float3 viewDirection = toCamera / max(cameraDistance, 0.0001);
                float surfaceEyeDepth = LinearEyeDepth(input.positionCS.z, _ZBufferParams);
                float rayPerEyeDepth = cameraDistance / max(surfaceEyeDepth, 0.0001);

                // The surface normal: the waves, plus ripples carried along the flow.
                float2 flow = input.flow + _Drift.xy;
                float2 offsetA;
                float2 offsetB;
                float weightA;
                FlowPhases(flow, offsetA, offsetB, weightA);
                float2 slope = lerp(Ripples(positionWS.xz - offsetB), Ripples(positionWS.xz - offsetA), weightA);
                float3 normalWS = normalize(input.waveNormal + float3(slope.x, 0, slope.y));

                // How much water lies behind this pixel, along the view and straight down.
                float sceneRaw = SceneRawDepth(screenUV);
                float pathDepth = max(0, LinearEyeDepth(SampleSceneDepth(screenUV), _ZBufferParams) - surfaceEyeDepth) * rayPerEyeDepth;
                float3 groundWS = ComputeWorldSpacePosition(screenUV, sceneRaw, UNITY_MATRIX_I_VP);
                float verticalDepth = max(0, positionWS.y - groundWS.y);

                // The ground seen through the surface, bent by the ripples, unless that would show
                // something in front of the water.
                float2 refractedUV = screenUV + (normalWS.xz * (_RefractionStrength * saturate(pathDepth) / max(1, surfaceEyeDepth * 0.08)));
                float refractedEyeDepth = LinearEyeDepth(SampleSceneDepth(refractedUV), _ZBufferParams);
                if (refractedEyeDepth < surfaceEyeDepth)
                {
                    refractedUV = screenUV;
                    refractedEyeDepth = LinearEyeDepth(SampleSceneDepth(screenUV), _ZBufferParams);
                }

                float refractedPath = max(0, refractedEyeDepth - surfaceEyeDepth) * rayPerEyeDepth;
                float3 refractedGroundWS = ComputeWorldSpacePosition(refractedUV, SceneRawDepth(refractedUV), UNITY_MATRIX_I_VP);
                float refractedDepth = max(0, positionWS.y - refractedGroundWS.y);
                half3 ground = SampleSceneColor(refractedUV);

                Light mainLight = GetMainLight(TransformWorldToShadowCoord(positionWS));
                half shadow = mainLight.shadowAttenuation;
                half3 ambient = SampleSH(float3(0, 1, 0));
                half3 sunlight = mainLight.color * shadow;

                // Light patterns on the shallow ground, where the sun reaches it.
                float2 causticsUV = refractedGroundWS.xz / _CausticsTiling;
                half causticsA = SAMPLE_TEXTURE2D(_Masks, sampler_Masks, causticsUV + WaterCycles(float2(0.031, 0.019))).g;
                half causticsB = SAMPLE_TEXTURE2D(_Masks, sampler_Masks, causticsUV * 1.37 + WaterCycles(float2(-0.023, 0.034))).g;
                half causticsFade = saturate(1 - (refractedDepth / _CausticsDepth)) * saturate(refractedDepth * 4);
                ground += min(causticsA, causticsB) * _CausticsStrength * causticsFade * sunlight;

                // The water's own color deepens with the depth, and the ground fades into it along
                // the view.
                half depthBlend = 1 - exp(-refractedDepth / _DepthColorDistance);
                half3 waterColor = lerp(_ShallowColor.rgb, _DeepColor.rgb, depthBlend);
                half3 litWater = waterColor * (ambient + (sunlight * 0.45));
                half transmittance = exp(-refractedPath / _Clarity);
                half3 tint = lerp(_ShallowColor.rgb * 1.4, half3(1, 1, 1), transmittance);
                half3 color = lerp(litWater, ground * tint, transmittance);

                // Ripples that lean toward the sun catch more of its light, and bright wavy lines
                // drift over the surface, as in painted water.
                half rippleLight = 1 + ((slope.x * mainLight.direction.x) + (slope.y * mainLight.direction.z)) * _RippleShading;
                color *= max(0.5, rippleLight);

                // Slow patches of noise decide where the lines show, so they never form a net. They
                // drift with the material's drift and move along a river's flow in the same
                // restarting phases as the ripples, so a flow that varies across a river never
                // shears them apart.
                float patchTiling = _SurfacePatternTiling * 8;
                float2 patchDrift = WaterCycles(_Drift.xy * 0.5 / patchTiling);
                float2 riverOffsetA;
                float2 riverOffsetB;
                float riverWeightA;
                FlowPhases(input.flow * 0.5, riverOffsetA, riverOffsetB, riverWeightA);
                half patches = lerp(
                    SAMPLE_TEXTURE2D(_Masks, sampler_Masks, ((positionWS.xz - riverOffsetB) / patchTiling) - patchDrift).b,
                    SAMPLE_TEXTURE2D(_Masks, sampler_Masks, ((positionWS.xz - riverOffsetA) / patchTiling) - patchDrift).b,
                    riverWeightA);
                float2 patternUV = (positionWS.xz / _SurfacePatternTiling) + (slope * 0.2);
                half surfaceA = SAMPLE_TEXTURE2D(_Masks, sampler_Masks, patternUV - (offsetA / _SurfacePatternTiling)).g;
                half surfaceB = SAMPLE_TEXTURE2D(_Masks, sampler_Masks, (patternUV * 0.83) - (offsetB / _SurfacePatternTiling)).g;
                half surfaceLines = lerp(surfaceB, surfaceA, weightA) * smoothstep(0.45, 0.75, patches)
                    * saturate(1 - (cameraDistance / 60));
                color += (ambient + sunlight * 0.5) * _SurfacePattern * surfaceLines * (1 - transmittance * 0.5);

                // The sky and the horizon in the surface, more at a glancing view.
                half viewDot = saturate(dot(normalWS, viewDirection));
                half fresnel = _ReflectionStrength * pow(1 - viewDot, _FresnelPower);
                half3 sky = GlossyEnvironmentReflection(reflect(-viewDirection, normalWS), 0.08, 1);
                color = lerp(color, lerp(sky, _HorizonColor.rgb, 0.5), fresnel);

                // The sun's glint, and sparkles where the ripples catch it. A sparkle fades in
                // over a little of the noise, so it twinkles instead of flickering on and off.
                float3 halfway = normalize(mainLight.direction + viewDirection);
                half facing = saturate(dot(normalWS, halfway));
                half glint = pow(facing, _SpecularPower) * _SpecularIntensity;
                half sheen = pow(facing, _SpecularPower * 0.05) * _SheenIntensity;
                half sparkleNoise = SAMPLE_TEXTURE2D(_Masks, sampler_Masks, (positionWS.xz * 0.9) + (slope * 0.4)).b;
                half sparkle = smoothstep(0.5, 0.9, pow(facing, _SpecularPower * 0.18)) * smoothstep(0.63, 0.69, sparkleNoise) * _SparkleIntensity;
                color += sunlight * (glint + sheen + sparkle);

                // Foam along the shore, around things in the water, in lines that roll toward the
                // shore, and in fast water. The amount decides how much of the foam pattern shows:
                // a little shows its thickest lines and bubbles, and a lot covers the water.
                float shoreFoam = pow(1 - saturate(verticalDepth / _ShoreFoamDepth), 2);
                float objectFoam = pow(1 - saturate(pathDepth / _IntersectionFoamDistance), 2);
                float lines = 0;
                if (_ShoreWaveFrequency > 0)
                {
                    float crest = 0.5 + (0.5 * sin(TWO_PI * ((verticalDepth * _ShoreWaveFrequency) + WaterCycles(_ShoreWaveSpeed))));
                    lines = pow(crest, 6) * saturate(1 - (verticalDepth / _ShoreWaveDepth)) * 0.85 * input.color.b;
                }

                // Fast water carries foam in patches that drift with it; churning water, such as
                // at the foot of a waterfall, foams all over.
                half drifting = lerp(
                    SAMPLE_TEXTURE2D(_Masks, sampler_Masks, (positionWS.xz - offsetB) / (_FoamTiling * 2.5)).b,
                    SAMPLE_TEXTURE2D(_Masks, sampler_Masks, (positionWS.xz - offsetA) / (_FoamTiling * 2.5)).b,
                    weightA);
                half foamPatches = lerp(smoothstep(0.35, 0.7, drifting), 1, smoothstep(0.6, 0.95, input.color.r));
                float turbulence = input.color.r * _TurbulenceFoam * foamPatches;
                float foamAmount = saturate(max(max(shoreFoam, objectFoam), max(lines, turbulence)));
                half foamPattern = 1 - lerp(
                    SAMPLE_TEXTURE2D(_Masks, sampler_Masks, (positionWS.xz - offsetB) / _FoamTiling).r,
                    SAMPLE_TEXTURE2D(_Masks, sampler_Masks, (positionWS.xz - offsetA) / _FoamTiling).r,
                    weightA);
                half coverage = smoothstep(1 - foamAmount - _FoamSoftness, 1 - foamAmount + _FoamSoftness, foamPattern);
                half foam = coverage * lerp(0.45, 1, foamPattern) * saturate(foamAmount * 1.5);
                color = lerp(color, _FoamColor.rgb * (ambient + (sunlight * 0.75)), foam * _FoamColor.a);

                color = MixFog(color, input.fogFactor);
                half alpha = saturate(pathDepth / _EdgeFade);
                return half4(color, alpha);
            }
            ENDHLSL
        }
    }

    FallBack Off
}
