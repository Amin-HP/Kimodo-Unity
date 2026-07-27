// SPDX-License-Identifier: Apache-2.0
// Simple transparent "ghost" shader (URP). Unlit, alpha-blended, with a fresnel rim so the
// silhouette reads clearly. Used by the pose-constraint editor to show the character mesh at an
// authored pose without disturbing the live preview. Colour is driven from the component.
Shader "Kimodo/GhostMesh"
{
    Properties
    {
        _BaseColor ("Base Color", Color) = (1.0, 1.0, 1.0, 0.51)
        _RimColor  ("Rim Color",  Color) = (1.0, 1.0, 1.0, 0.4)
        _RimPower  ("Rim Power",  Range(0.5, 8)) = 2.5
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "GhostForward"
            Tags { "LightMode" = "UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor;
                float4 _RimColor;
                float  _RimPower;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float4 color      : COLOR;   // per-vertex activation mask in .a (1 = show, 0 = hide)
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float3 normalWS    : TEXCOORD0;
                float3 positionWS  : TEXCOORD1;
                float  mask        : TEXCOORD2;
            };

            Varyings vert (Attributes IN)
            {
                Varyings OUT;
                VertexPositionInputs p = GetVertexPositionInputs(IN.positionOS.xyz);
                OUT.positionHCS = p.positionCS;
                OUT.positionWS  = p.positionWS;
                OUT.normalWS    = TransformObjectToWorldNormal(IN.normalOS);
                OUT.mask        = IN.color.a;
                return OUT;
            }

            half4 frag (Varyings IN) : SV_Target
            {
                float3 n = normalize(IN.normalWS);
                float3 v = GetWorldSpaceNormalizeViewDir(IN.positionWS);
                float fres = pow(saturate(1.0 - saturate(dot(n, v))), _RimPower);
                float3 col = lerp(_BaseColor.rgb, _RimColor.rgb, fres);
                // Per-vertex activation mask fades out deactivated regions (smooth via skin weights).
                float  a   = saturate(_BaseColor.a + fres * _RimColor.a) * saturate(IN.mask);
                return half4(col, a);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
