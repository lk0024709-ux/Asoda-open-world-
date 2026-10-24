Shader "Asoda/StylizedIndianSunsetSkybox"
{
    Properties
    {
        _ZenithColor ("Zenith Sky Color", Color) = (0.22, 0.25, 0.48, 1.0)
        _MidSkyColor ("Mid Sunset Color", Color) = (0.86, 0.47, 0.34, 1.0)
        _HorizonColor ("Horizon Dust / Fog Color", Color) = (0.92, 0.64, 0.43, 1.0)
        _SunColor ("Sun Disc & Glow Color", Color) = (1.0, 0.88, 0.58, 1.0)
        _SunDirection ("Sun Direction (World)", Vector) = (0.35, 0.28, 0.89, 0.0)
        _HorizonHeight ("Horizon Haze Height", Range(0.02, 0.45)) = 0.16
        _SunDiscSize ("Sun Disc Radius", Range(0.005, 0.15)) = 0.035
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Background"
            "RenderType" = "Background"
            "PreviewType" = "Skybox"
        }

        Cull Off
        ZWrite Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 2.0

            #include "UnityCG.cginc"

            half4 _ZenithColor;
            half4 _MidSkyColor;
            half4 _HorizonColor;
            half4 _SunColor;
            float4 _SunDirection;
            half _HorizonHeight;
            half _SunDiscSize;

            struct appdata
            {
                float4 vertex : POSITION;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 viewDir : TEXCOORD0;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.viewDir = v.vertex.xyz;
                return o;
            }

            half4 frag(v2f i) : SV_Target
            {
                float3 dir = normalize(i.viewDir);
                half y = (half)dir.y;

                // Below and directly along the horizon matches RenderSettings.fogColor 1:1
                // so distant fogged chunks melt seamlessly into the sky without any visible seam.
                half lowerBlend = saturate(y / max((half)0.02, _HorizonHeight));
                lowerBlend = lowerBlend * lowerBlend * ((half)3.0 - (half)2.0 * lowerBlend);

                half upperBlend = saturate((y - _HorizonHeight) / max((half)0.05, (half)1.0 - _HorizonHeight));
                upperBlend = pow(upperBlend, (half)0.75);

                half3 skyColor = lerp(_HorizonColor.rgb, _MidSkyColor.rgb, lowerBlend);
                skyColor = lerp(skyColor, _ZenithColor.rgb, upperBlend);

                // Stylized golden-hour sun glow & soft disc above the horizon
                float3 sunDir = normalize(_SunDirection.xyz);
                half sunDot = (half)saturate(dot(dir, sunDir));
                half horizonMask = saturate((y + (half)0.04) * (half)8.0);

                half sunHalo = pow(sunDot, (half)12.0) * (half)0.32 * horizonMask;
                half discThreshold = (half)1.0 - _SunDiscSize;
                half sunDisc = smoothstep(discThreshold - (half)0.008, discThreshold + (half)0.004, sunDot) * horizonMask;

                skyColor += _SunColor.rgb * (sunHalo + sunDisc * (half)0.65);

                // Re-anchor the immediate horizon line strictly to _HorizonColor to hide chunk edges
                half horizonDustLock = (half)1.0 - saturate(abs(y) * (half)14.0);
                skyColor = lerp(skyColor, _HorizonColor.rgb, horizonDustLock * (half)0.45);

                return half4(skyColor, 1.0);
            }
            ENDCG
        }
    }

    Fallback Off
}
