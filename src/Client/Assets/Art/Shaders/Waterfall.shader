// The falling sheet of a waterfall, lit as the water surfaces are: the rock behind shows through
// clear water, bent by ripples that rush down the sheet, the sky and the sun glint in it, and
// thin streaks and clumps of foam fall with the water. The water slides glassy over the lip,
// breaks into streaks as it falls, and turns white at the foot; its sides fray. The streaks keep
// to the water that carries them, so they stretch as the water speeds up.
//
// The first texture coordinate holds meters across the sheet and the seconds the water has
// fallen, the second meters to the nearer side and how far down the fall the vertex lies, from 0
// at the lip to 1 at the foot. The tangent runs across the sheet. It reads the camera's opaque
// texture and draws after the water surfaces, both faces, without writing depth. See
// docs/ART_PIPELINE.md under Water.
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
        _StreakWidth("Streak pattern width (m)", Float) = 2.4
        _StreakLength("Streak pattern length (m)", Float) = 4
        _NormalStrength("Ripple strength", Range(0, 2)) = 0.8

        [Header(Foam)]
        _Foam("Foam on the sheet", Range(0, 1)) = 0.42
        _LipGlass("Clear water over the lip (share of the fall)", Range(0, 1)) = 0.18
        _FootFoam("Foam at the foot (share of the fall)", Range(0, 1)) = 0.3
        _EdgeSoftness("Side softness (m)", Float) = 0.3

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
            Cull Off

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vertex
            #pragma fragment Fragment
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareOpaqueTexture.hlsl"

            TEXTURE2D(_FallTexture);
            SAMPLER(sampler_FallTexture);
            TEXTURE2D(_NormalMap);
            SAMPLER(sampler_NormalMap);

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
                float _EdgeSoftness;
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
                float2 sheet : TEXCOORD0;
                float2 edges : TEXCOORD1;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float3 acrossWS : TEXCOORD2;
                float2 sheet : TEXCOORD3;
                float2 edges : TEXCOORD4;
                half fogFactor : TEXCOORD5;
            };

            Varyings Vertex(Attributes input)
            {
                Varyings output;
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionWS = positionWS;
                output.positionCS = TransformWorldToHClip(positionWS);
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.acrossWS = TransformObjectToWorldDir(input.tangentOS.xyz);
                output.sheet = input.sheet;
                output.edges = input.edges;
                output.fogFactor = ComputeFogFactor(output.positionCS.z);
                return output;
            }

            half4 Fragment(Varyings input, bool frontFace : SV_IsFrontFace) : SV_Target
            {
                float across = input.sheet.x;
                float flight = input.sheet.y;
                float side = input.edges.x;
                half fall = saturate(input.edges.y);

                // A point of the pattern keeps to the water that carries it: its coordinate down
                // the sheet is the time the water left the lip.
                float carried = (flight - _Time.y) * _PatternSpeed;
                half3 fallA = SAMPLE_TEXTURE2D(_FallTexture, sampler_FallTexture, float2(across / _StreakWidth, carried / _StreakLength)).rgb;
                half3 fallB = SAMPLE_TEXTURE2D(_FallTexture, sampler_FallTexture,
                    float2((across / (_StreakWidth * 0.63)) + 0.31, (carried * 1.21) / (_StreakLength * 0.77))).rgb;
                half streaks = saturate((fallA.r + fallB.r) * 0.55);
                half clumps = max(fallA.g, fallB.g);

                // Ripples that rush down with the water bend the sheet's normal.
                float3 normal = normalize(input.normalWS) * (frontFace ? 1 : -1);
                float3 acrossWS = normalize(input.acrossWS);
                float3 downWS = normalize(cross(normal, acrossWS));
                half3 rippleA = UnpackNormalScale(
                    SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, float2(across / 1.8, carried / 3.5)),
                    _NormalStrength);
                half3 rippleB = UnpackNormalScale(
                    SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, float2((across / 1.1) + 0.4, (carried * 1.3) / 2.2)),
                    _NormalStrength * 0.6);
                float2 slope = rippleA.xy + rippleB.xy;
                normal = normalize(normal + (acrossWS * slope.x) + (downWS * slope.y));

                Light mainLight = GetMainLight(TransformWorldToShadowCoord(input.positionWS));
                half3 ambient = SampleSH(normal);
                half3 sunlight = mainLight.color * mainLight.shadowAttenuation;

                // The rock behind shows through clear water, bent and tinted, more toward the
                // thin sides and where the water first tips over.
                float2 screenUV = GetNormalizedScreenSpaceUV(input.positionCS);
                half3 behind = SampleSceneColor(screenUV + (slope * _RefractionStrength));
                half thickness = saturate(side / 0.6) * lerp(0.55, 1, saturate(fall * 2.5));
                half3 water = lerp(_DeepColor.rgb, _Color.rgb, streaks) * (ambient + (sunlight * 0.5));
                half3 color = lerp(behind * lerp(half3(1, 1, 1), _Color.rgb * 1.2, 0.6), water, _Opacity * thickness);

                // The sky in the sheet, and the sun's glint and sparkles on its ripples.
                float3 viewDirection = normalize(_WorldSpaceCameraPos - input.positionWS);
                half fresnel = _ReflectionStrength * pow(1 - saturate(dot(normal, viewDirection)), 3);
                half3 sky = GlossyEnvironmentReflection(reflect(-viewDirection, normal), 0.1, 1);
                color = lerp(color, lerp(sky, _HorizonColor.rgb, 0.4), fresnel);
                half facing = saturate(dot(normal, normalize(mainLight.direction + viewDirection)));
                half glint = pow(facing, _SpecularPower) * _SpecularIntensity;
                half sparkle = smoothstep(0.4, 0.8, pow(facing, _SpecularPower * 0.08)) * step(0.68, fallA.b) * _SparkleIntensity;

                // Glassy over the lip, breaking into streaks as it falls, white at the foot.
                half glass = 1 - smoothstep(0, _LipGlass, fall);
                half foot = smoothstep(1 - _FootFoam, 1, fall);
                half foamAmount = saturate((_Foam * (1 - glass)) + (fall * 0.1) + (foot * 0.45));
                half coverage = smoothstep(1 - foamAmount - 0.1, 1 - foamAmount + 0.1, max(streaks, clumps));
                color += sunlight * (glint + sparkle) * (1 - (coverage * 0.7));
                color += sunlight * 0.3 * (1 - smoothstep(0, 0.04, fall));

                // Foam is brightest in its clumps and greyer between them, so white water keeps
                // its shape.
                half3 foam = _FoamColor.rgb * (ambient + (sunlight * 0.7)) * lerp(0.78, 1.05, max(clumps, streaks * 0.6));
                color = lerp(color, foam, coverage * lerp(0.75, 1, clumps));
                color = MixFog(color, input.fogFactor);

                // The sides fray where clumps of foam reach past them.
                half alpha = saturate((side - (clumps * 0.25)) / _EdgeSoftness);
                return half4(color, alpha);
            }
            ENDHLSL
        }
    }

    FallBack Off
}
