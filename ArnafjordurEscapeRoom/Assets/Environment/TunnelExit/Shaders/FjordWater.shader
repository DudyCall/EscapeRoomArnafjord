// The fjord: one big flat plane, so everything happens per pixel.
// Rolling ripples (summed sine waves), sky reflection by fresnel, a sun glint, clear green
// shallows that show the seabed, and foam where the water meets the land.
// Needs the camera depth texture (URP asset: Depth Texture on).
Shader "Arnafjordur/FjordWater"
{
    Properties
    {
        _DeepColor ("Deep water", Color) = (0.035, 0.10, 0.14, 1)
        _ShallowColor ("Shallow water", Color) = (0.20, 0.43, 0.42, 1)
        _FoamColor ("Foam", Color) = (0.92, 0.95, 0.96, 1)
        _DepthFade ("Shallow depth (m)", Float) = 2.2
        _ShallowClarity ("Shallow clarity", Range(0, 1)) = 0.8
        _FoamWidth ("Foam depth (m)", Float) = 0.45
        _WaveStrength ("Wave strength", Range(0, 3)) = 1
        _WaveSpeed ("Wave speed", Range(0, 3)) = 1
        _Reflection ("Reflection", Range(0, 1.5)) = 1
        _SunGlint ("Sun glint", Range(0, 4)) = 1.4
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "FjordWater"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _DeepColor;
                half4 _ShallowColor;
                half4 _FoamColor;
                float _DepthFade;
                float _ShallowClarity;
                float _FoamWidth;
                float _WaveStrength;
                float _WaveSpeed;
                float _Reflection;
                float _SunGlint;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float eyeDepth : TEXCOORD1;
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                o.positionWS = TransformObjectToWorld(v.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                o.eyeDepth = -TransformWorldToView(o.positionWS).z;
                return o;
            }

            float Hash21(float2 p)
            {
                p = frac(p * float2(123.34, 456.21));
                p += dot(p, p + 45.32);
                return frac(p.x * p.y);
            }

            float ValueNoise(float2 p)
            {
                float2 i = floor(p);
                float2 f = frac(p);
                f = f * f * (3.0 - 2.0 * f);
                float a = Hash21(i);
                float b = Hash21(i + float2(1, 0));
                float c = Hash21(i + float2(0, 1));
                float d = Hash21(i + float2(1, 1));
                return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
            }

            // slope of one travelling sine wave
            float2 WaveSlope(float2 p, float2 dir, float wavelength, float steepness, float t)
            {
                float k = 6.2831853 / wavelength;
                float phase = dot(dir, p) * k - t * sqrt(9.81 * k);
                return dir * (steepness * cos(phase));
            }

            half4 frag(Varyings i) : SV_Target
            {
                float3 toCam = GetCameraPositionWS() - i.positionWS;
                float dist = length(toCam);
                float3 V = toCam / dist;
                float t = _Time.y * _WaveSpeed;

                // ---- surface normal
                float2 p = i.positionWS.xz;
                p += 1.3 * sin(p.yx * 0.11 + t * 0.15); // bend the wave fronts so they do not look ruled
                float swell = lerp(1.0, 0.4, saturate(dist / 900.0));
                float ripple = saturate(1.0 - dist / 170.0);
                float2 slope = 0;
                slope += WaveSlope(p, float2(0.94, 0.34), 31.0, 0.045, t) * swell;
                slope += WaveSlope(p, float2(0.71, -0.70), 19.0, 0.055, t) * swell;
                slope += WaveSlope(p, float2(0.33, 0.94), 11.0, 0.060, t) * swell;
                slope += WaveSlope(p, float2(-0.52, 0.85), 6.3, 0.065, t) * swell;
                slope += WaveSlope(p, float2(0.88, -0.47), 3.1, 0.075, t) * ripple;
                slope += WaveSlope(p, float2(-0.24, -0.97), 1.7, 0.075, t) * ripple;
                slope += WaveSlope(p, float2(0.61, 0.79), 0.9, 0.065, t) * ripple * ripple;
                slope += WaveSlope(p, float2(-0.90, 0.43), 0.55, 0.055, t) * ripple * ripple;
                float3 N = normalize(float3(-slope.x * _WaveStrength, 1.0, -slope.y * _WaveStrength));

                // ---- how deep the water is under this pixel
                float2 screenUV = GetNormalizedScreenSpaceUV(i.positionCS);
                float sceneEye = LinearEyeDepth(SampleSceneDepth(screenUV), _ZBufferParams);
                float through = max(sceneEye - i.eyeDepth, 0.0) * dist / max(i.eyeDepth, 0.001);
                float depth = through * saturate(V.y);
                float shallow = exp(-depth / max(_DepthFade, 0.01));

                // ---- colour
                Light sun = GetMainLight();
                half3 light = SampleSH(half3(0, 1, 0)) + sun.color * (0.6 * saturate(sun.direction.y));
                half3 body = lerp(_DeepColor.rgb, _ShallowColor.rgb, shallow) * light;

                float fresnel = 0.02 + 0.98 * pow(1.0 - saturate(dot(N, V)), 5.0);
                fresnel = saturate(fresnel * _Reflection);
                float3 R = reflect(-V, N);
                R.y = abs(R.y) + 0.02;
                half3 sky = GlossyEnvironmentReflection(half3(normalize(R)), 0.08, 1.0);

                float3 H = normalize(sun.direction + V);
                float glint = pow(saturate(dot(N, H)), 240.0) * _SunGlint;

                half3 col = lerp(body, sky, fresnel) + sun.color * glint;
                float alpha = max(1.0 - shallow * _ShallowClarity, fresnel);

                // ---- foam along the shore: a solid edge plus lines washing in
                float edge = saturate(1.0 - depth / max(_FoamWidth, 0.01));
                float blotch = ValueNoise(i.positionWS.xz * 1.6 + t * 0.3);
                float grain = ValueNoise(i.positionWS.xz * 6.0 - t * 0.25);
                float lines = sin(depth / max(_FoamWidth, 0.01) * 9.0 + _Time.y * 1.1 + blotch * 4.0);
                float foam = smoothstep(0.6, 0.95, edge + 0.3 * (grain - 0.5));
                foam += edge * smoothstep(0.35, 0.85, lines) * (0.35 + 0.65 * blotch);
                foam = saturate(foam);
                col = lerp(col, _FoamColor.rgb * light, foam);
                alpha = max(alpha, foam * 0.95);

                // fog per pixel: the plane is so large that a per-vertex factor comes out far too thick
                col = MixFog(col, ComputeFogFactorZ0ToFar(i.eyeDepth));
                return half4(col, alpha);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
