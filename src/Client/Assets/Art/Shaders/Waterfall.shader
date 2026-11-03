// The falling water of a waterfall, a column with a body of its own that is lit as the water
// surfaces are: the rock behind shows through clear water, bent by ripples that rush down it, the
// sky and the sun glint in it, and thin streaks and clumps of foam fall with the water. The water
// fades in over the stream above as it nears the lip, slides glassy over the lip, breaks into
// streaks as it falls, and turns white at the foot. Its outline frays and fades where it turns
// away around the column, so it has no hard edge from any side, and clear water looks denser
// where the view passes through it at a slant. The streaks keep to the water that carries them,
// so they stretch as the water speeds up.
//
// The texture coordinate holds the meters around the column from its front, the seconds the
// water has fallen, negative before it falls, how far down the fall the vertex lies, from 0 at the
// lip to 1 at the foot, and how far the column has faded in over the water above. The normals
// face out of the column and the tangents run around it. It draws the column's outside after the
// water surfaces, without writing depth, and sees through itself the scene with those surfaces,
// which WaterRendererFeature copies after drawing them, so the pool shows through the fall as
// water. Its streaks run on the water's clock of WaterCommon.hlsl. See docs/ART_PIPELINE.md under
// Water.
Shader "Mmorpg/Waterfall"
{
    Properties
    {
        [Header(Color)]
        _Color("Clear water color", Color) = (0.36, 0.82, 0.86, 1)
        _DeepColor("Shaded water color", Color) = (0.06, 0.36, 0.52, 1)
        _FoamColor("Foam color", Color) = (0.92, 0.97, 1, 1)
        _HorizonColor("Reflected horizon color", Color) = (0.62, 0.84, 0.95, 1)
        _Opacity("Opacity of clear water", Range(0, 1)) = 0.55

        [Header(Pattern)]
        [NoScaleOffset] _FallTexture("Streaks (R), foam clumps (G), sparkle noise (B)", 2D) = "black" {}
        [NoScaleOffset][Normal] _NormalMap("Ripple normals", 2D) = "bump" {}
        _PatternSpeed("Pattern speed (m/s)", Float) = 3.2
        _StreakWidth("Streak pattern width (m)", Float) = 0.7
        _StreakLength("Streak pattern length (m)", Float) = 8
        _NormalStrength("Ripple strength", Range(0, 2)) = 0.8

        [Header(Foam)]
        _Foam("Foam on the falling water", Range(0, 1)) = 0.42
        _LipGlass("Clear water over the lip (share of the fall)", Range(0, 1)) = 0.18
        _FootFoam("Foam at the foot (share of the fall)", Range(0, 1)) = 0.3
        _OutlineSoftness("Outline softness", Range(0.01, 1)) = 0.3

        [Header(Light)]
        _RefractionStrength("Refraction", Range(0, 0.2)) = 0.035
        _ReflectionStrength("Reflection", Range(0, 1)) = 0.55
        _SpecularPower("Sun glint sharpness", Range(16, 2048)) = 300
        _SpecularIntensity("Sun glint", Range(0, 16)) = 3
        _SparkleIntensity("Sparkles", Range(0, 8)) = 2
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent+1"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "WaterfallForward"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Back

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
            #include "WaterCommon.hlsl"

            TEXTURE2D(_FallTexture);
            SAMPLER(sampler_FallTexture);
            TEXTURE2D(_NormalMap);
            SAMPLER(sampler_NormalMap);

            // The scene behind the column with the water surfaces drawn into it.
            TEXTURE2D_X(_WaterSceneColorTexture);
            SAMPLER(sampler_WaterSceneColorTexture);

            half3 SampleWaterSceneColor(float2 uv)
            {
                return SAMPLE_TEXTURE2D_X(_WaterSceneColorTexture, sampler_WaterSceneColorTexture, UnityStereoTransformScreenSpaceTex(uv)).rgb;
            }

            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
                half4 _DeepColor;
                half4 _FoamColor;
                half4 _HorizonColor;
                half _Opacity;
                float _PatternSpeed;
                float _StreakWidth;
                float _StreakLength;
                half _NormalStrength;
                half _Foam;
                half _LipGlass;
                half _FootFoam;
                half _OutlineSoftness;
                half _RefractionStrength;
                half _ReflectionStrength;
                half _SpecularPower;
                half _SpecularIntensity;
                half _SparkleIntensity;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float4 tangentOS : TANGENT;
                float4 column : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float3 aroundWS : TEXCOORD2;
                float4 column : TEXCOORD3;
                half fogFactor : TEXCOORD4;
            };

            Varyings Vertex(Attributes input)
            {
                Varyings output;
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionWS = positionWS;
                output.positionCS = TransformWorldToHClip(positionWS);
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.aroundWS = TransformObjectToWorldDir(input.tangentOS.xyz);
                output.column = input.column;
                output.fogFactor = ComputeFogFactor(output.positionCS.z);
                return output;
            }

            half4 Fragment(Varyings input) : SV_Target
            {
                float around = input.column.x;
                float flight = input.column.y;
                half fall = saturate(input.column.z);
                half fadeIn = smoothstep(0, 1, saturate(input.column.w));

                // A point of the pattern keeps to the water that carries it: its coordinate down
                // the column is how far the water has fallen from the lip, less how far the
                // pattern has moved on, which the water's clock gives for each sample's scale.
                float fallen = flight * _PatternSpeed;
                float streakA = (fallen / _StreakLength) - WaterCycles(_PatternSpeed / _StreakLength);
                float streakScaleB = 1.21 / (_StreakLength * 0.77);
                float streakB = (fallen * streakScaleB) - WaterCycles(_PatternSpeed * streakScaleB);
                half3 fallA = SAMPLE_TEXTURE2D(_FallTexture, sampler_FallTexture, float2(around / _StreakWidth, streakA)).rgb;
                half3 fallB = SAMPLE_TEXTURE2D(_FallTexture, sampler_FallTexture,
                    float2((around / (_StreakWidth * 0.63)) + 0.31, streakB)).rgb;
                half streaks = saturate((fallA.r + fallB.r) * 0.55);
                half clumps = max(fallA.g, fallB.g);

                // Ripples that rush down with the water bend the column's normal.
                float3 surfaceNormal = normalize(input.normalWS);
                float3 aroundWS = normalize(input.aroundWS);
                float3 downWS = normalize(cross(surfaceNormal, aroundWS));
                float rippleDownA = (fallen / 3.5) - WaterCycles(_PatternSpeed / 3.5);
                float rippleDownB = (fallen * (1.3 / 2.2)) - WaterCycles(_PatternSpeed * (1.3 / 2.2));
                half3 rippleA = UnpackNormalScale(
                    SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, float2(around / 1.8, rippleDownA)),
                    _NormalStrength);
                half3 rippleB = UnpackNormalScale(
                    SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, float2((around / 1.1) + 0.4, rippleDownB)),
                    _NormalStrength * 0.6);
                float2 slope = rippleA.xy + rippleB.xy;
                float3 normal = normalize(surfaceNormal + (aroundWS * slope.x) + (downWS * slope.y));

                Light mainLight = GetMainLight(TransformWorldToShadowCoord(input.positionWS));
                half3 ambient = SampleSH(normal);
                half3 sunlight = mainLight.color * mainLight.shadowAttenuation;

                // How squarely the column faces the view: the view passes through more water at a
                // slant. The outline fades where the column turns away around its section, which
                // the view's part across the water's path measures, so the top of the water over
                // the lip stays a surface even at a glancing view along the stream.
                float3 viewDirection = normalize(_WorldSpaceCameraPos - input.positionWS);
                half facing = saturate(dot(surfaceNormal, viewDirection));
                float3 viewAcrossPath = viewDirection - (downWS * dot(viewDirection, downWS));
                half outlineFacing = saturate(dot(surfaceNormal, viewAcrossPath) * rsqrt(max(dot(viewAcrossPath, viewAcrossPath), 1e-4)));

                // The rock and the pool behind show through clear water, bent and tinted, less
                // where the view passes through more water and where the water has broken up. A
                // bend that would show something in front of the water, such as a character
                // wading at its foot, looks straight through instead.
                float2 screenUV = GetNormalizedScreenSpaceUV(input.positionCS);
                float2 refractedUV = screenUV + (slope * _RefractionStrength);
                float waterEyeDepth = LinearEyeDepth(input.positionCS.z, _ZBufferParams);
                if (LinearEyeDepth(SampleSceneDepth(refractedUV), _ZBufferParams) < waterEyeDepth)
                {
                    refractedUV = screenUV;
                }

                half3 behind = SampleWaterSceneColor(refractedUV);
                half thickness = lerp(1, 0.6, facing) * lerp(0.6, 1, saturate(fall * 2.5));
                half3 water = lerp(_DeepColor.rgb, _Color.rgb, streaks) * (ambient + (sunlight * 0.5));
                half3 color = lerp(behind * lerp(half3(1, 1, 1), _Color.rgb * 1.2, 0.6), water, saturate(_Opacity * thickness * 1.3));

                // The sky in the water, and the sun's glint and sparkles on its ripples.
                half fresnel = _ReflectionStrength * pow(1 - saturate(dot(normal, viewDirection)), 3);
                half3 sky = GlossyEnvironmentReflection(reflect(-viewDirection, normal), 0.1, 1);
                color = lerp(color, lerp(sky, _HorizonColor.rgb, 0.4), fresnel);
                half sun = saturate(dot(normal, normalize(mainLight.direction + viewDirection)));
                half glint = pow(sun, _SpecularPower) * _SpecularIntensity;
                half sparkle = smoothstep(0.4, 0.8, pow(sun, _SpecularPower * 0.08)) * smoothstep(0.65, 0.71, fallA.b) * _SparkleIntensity;

                // Glassy over the lip, breaking into streaks as it falls, white at the foot.
                half glass = 1 - smoothstep(0, _LipGlass, fall);
                half foot = smoothstep(1 - _FootFoam, 1, fall);
                half foamAmount = saturate((_Foam * (1 - glass)) + (fall * 0.1) + (foot * 0.45));
                half coverage = smoothstep(1 - foamAmount - 0.1, 1 - foamAmount + 0.1, max(streaks, clumps));
                color += sunlight * (glint + sparkle) * (1 - (coverage * 0.7));

                // Foam is brightest in its clumps and greyer between them, so white water keeps
                // its shape.
                half3 foam = _FoamColor.rgb * (ambient + (sunlight * 0.7)) * lerp(0.78, 1.05, max(clumps, streaks * 0.6));
                color = lerp(color, foam, coverage * lerp(0.75, 1, clumps));
                color = MixFog(color, input.fogFactor);

                // The outline frays where clumps of foam reach past it.
                half alpha = saturate((outlineFacing - (clumps * 0.2)) / _OutlineSoftness) * fadeIn;
                return half4(color, alpha);
            }
            ENDHLSL
        }
    }

    FallBack Off
}
