// Assets/Shaders/SegMaskPreview.shader
// 2026-11-03：SelfieSeg 原始掩码小窗（眼相机右上角）。
//   与主高亮层相互独立：主层若因单应/门控不可见，这个小窗仍能直接看到分割结果，
//   用于区分“分割没出来”和“屏幕映射没对上”。
Shader "ARAttention/SegMaskPreview"
{
    Properties
    {
        _MaskTex ("Mask (selfie seg)", 2D) = "black" {}
        _Color ("Mask color", Color) = (0.10, 0.90, 1.00, 0.95)
        _Bg ("Background", Color) = (0.00, 0.00, 0.00, 0.30)
        _Border ("Border", Color) = (1.00, 1.00, 1.00, 0.90)
    }
    SubShader
    {
        Tags { "Queue"="Transparent+100" "RenderType"="Transparent" "IgnoreProjector"="True" }
        Cull Off
        ZWrite Off
        ZTest Always
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MaskTex;
            fixed4 _Color;
            fixed4 _Bg;
            fixed4 _Border;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv  : TEXCOORD0;
            };

            v2f vert(appdata_base v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.texcoord.xy;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                // 掩码数据第 0 行 = 图像顶部；Unity 纹理 v=0 在底部 → 小窗里翻一次 y
                float2 uv = float2(i.uv.x, 1.0 - i.uv.y);
                float m = tex2D(_MaskTex, uv).r;
                float body = smoothstep(0.15, 0.70, m);

                // 2% 白边，保证小窗在明亮环境里也找得到
                float2 d = min(i.uv, 1.0 - i.uv);
                float border = 1.0 - smoothstep(0.0, 0.025, min(d.x, d.y));

                float3 col = lerp(_Bg.rgb, _Color.rgb, body);
                float a = lerp(_Bg.a, _Color.a, body);
                col = lerp(col, _Border.rgb, border);
                a = max(a, border * _Border.a);
                return fixed4(col, a);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
