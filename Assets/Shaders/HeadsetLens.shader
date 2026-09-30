// Headset view, one eye: shows the central crop of the full-screen AR view,
// pre-distorted (barrel) so a phone VR viewer's magnifying lenses straighten
// it out again. Drawn by a UI RawImage per eye (HeadsetView).
Shader "MARIO/HeadsetLens"
{
    Properties
    {
        [PerRendererData] _MainTex ("View", 2D) = "black" {}
        _CropX ("Crop start (u)", Float) = 0.25
        _CropW ("Crop width (u)", Float) = 0.5
        _K1 ("Distortion k1", Float) = 0.22
        _K2 ("Distortion k2", Float) = 0.12
        _Zoom ("Zoom", Float) = 1.0
        _EyeAspect ("Eye aspect (w/h)", Float) = 1.0
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" "PreviewType"="Plane" }
        Cull Off ZWrite Off ZTest Always Blend Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float _CropX, _CropW, _K1, _K2, _Zoom, _EyeAspect;

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                // Eye-centred coordinates, x scaled so distortion is circular.
                float2 p = (i.uv - 0.5) * 2.0;
                p.x *= _EyeAspect;
                float r2 = dot(p, p);
                p *= (1.0 + _K1 * r2 + _K2 * r2 * r2) / _Zoom;
                p.x /= _EyeAspect;
                float2 uv = p * 0.5 + 0.5;
                if (any(uv < 0.0) || any(uv > 1.0)) return fixed4(0, 0, 0, 1);
                uv.x = _CropX + uv.x * _CropW; // the central part of the full-screen view
                return fixed4(tex2D(_MainTex, uv).rgb, 1);
            }
            ENDCG
        }
    }
}
