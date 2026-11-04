// Assets/Shaders/SegMaskHighlight.shader
// 2026-11-03：SelfieSeg 调试高亮——把人体掩码按“眼相机屏幕 → PCA 摄像头像素”的单应
// 映射回真实世界，叠一层青色轮廓，用来人工检查分割是否贴合老师。
// 仅调试用；遮挡交互后续用 MaskErase。
Shader "ARAttention/SegMaskHighlight"
{
    Properties
    {
        _MaskTex ("Mask (selfie seg)", 2D) = "black" {}
        _Color ("Highlight Color", Color) = (0.1, 0.9, 1, 0.45)
        _EdgeLow ("Edge low", Range(0,1)) = 0.5
        _EdgeHigh ("Edge high", Range(0,1)) = 0.75
        _TargetRect ("Target rect (mask UV)", Vector) = (0,0,1,1)
        _Fill ("Fill alpha scale", Range(0,1)) = 0.55
        _Rim ("Rim strength", Range(0,2)) = 1.2
    }
    SubShader
    {
        Tags { "Queue"="Transparent+90" "RenderType"="Transparent" "IgnoreProjector"="True" }
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
            float3 _Row0, _Row1, _Row2;
            fixed4 _Color;
            float _EdgeLow, _EdgeHigh, _Fill, _Rim;
            float4 _TargetRect; // x,y,w,h in mask UV (top-left)

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv  : TEXCOORD0;
            };

            v2f vert(appdata_base v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = ComputeScreenPos(o.pos).xy / max(1e-5, o.pos.w);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                // 屏幕UV(左下原点) → 掩码UV(左上原点)
                float2 s = float2(i.uv.x, 1.0 - i.uv.y);
                float3 sw = float3(s, 1.0);
                float3 h = float3(dot(_Row0, sw), dot(_Row1, sw), dot(_Row2, sw));
                float2 muv = h.xy / max(1e-5, abs(h.z));

                float inside = step(0.0, muv.x) * step(muv.x, 1.0)
                             * step(0.0, muv.y) * step(muv.y, 1.0);
                // 只保留目标人框内的掩码：去掉旁边的反光/海报/其他人
                float inRect = step(_TargetRect.x, muv.x) * step(muv.x, _TargetRect.x + _TargetRect.z)
                             * step(_TargetRect.y, muv.y) * step(muv.y, _TargetRect.y + _TargetRect.w);
                float m = tex2D(_MaskTex, saturate(muv)).r;
                float body = smoothstep(_EdgeLow, _EdgeHigh, m) * inside;
                float bodyTarget = body * inRect;

                // 简单梯度当轮廓：边缘更亮，便于看分割贴合度
                float mx = tex2D(_MaskTex, saturate(muv + float2(1.0/256.0, 0.0))).r;
                float my = tex2D(_MaskTex, saturate(muv + float2(0.0, 1.0/144.0))).r;
                float edge = saturate((abs(mx - m) + abs(my - m)) * 8.0) * inside;
                float edgeTarget = edge * inRect;

                // 2026-11-03：全画面人体掩码给一层弱高亮，目标人框内再加强。
                //   这样即使目标框偏了/没匹配上，用户仍能看到分割轮廓，便于判断是“分割错”还是“框错”。
                float a = saturate(body * _Fill * 0.35f + bodyTarget * _Fill * 0.75f
                                   + edge * _Rim * 0.25f + edgeTarget * _Rim) * _Color.a;
                return fixed4(_Color.rgb, a);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
