// Onion skin and pose thumbnails. Drawn in immediate mode (SetPass + DrawMeshNow), so it works in any render pipeline.
Shader "Hidden/Vibrations/Ghost"
{
    Properties
    {
        _Color ("Color", Color) = (1, 1, 1, 1)
        _ViewDir ("Direction to viewer (world)", Vector) = (0, 0, 1, 0)
    }
    SubShader
    {
        Tags { "Queue" = "Transparent" }

        CGINCLUDE
        #include "UnityCG.cginc"
        float4 _Color;
        float4 _ViewDir;
        struct v2f { float4 pos : SV_POSITION; float3 normal : TEXCOORD0; };
        v2f vert(appdata_base v)
        {
            v2f o;
            o.pos = UnityObjectToClipPos(v.vertex);
            o.normal = UnityObjectToWorldNormal(v.normal);
            return o;
        }
        ENDCG

        // Depth prepass: the ghost shows only its front surface, no see-through overlap.
        Pass
        {
            ZWrite On
            ColorMask 0
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            fixed4 frag(v2f i) : SV_Target { return 0; }
            ENDCG
        }

        Pass
        {
            ZWrite Off
            ZTest LEqual
            Blend SrcAlpha OneMinusSrcAlpha
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            fixed4 frag(v2f i) : SV_Target
            {
                float3 n = normalize(i.normal);
                float facing = saturate(dot(n, normalize(_ViewDir.xyz)));
                float key = saturate(dot(n, normalize(float3(0.35, 0.85, 0.4)))) * 0.6 + 0.4;
                float rim = pow(1 - facing, 3);
                return fixed4(_Color.rgb * key + rim * 0.35, _Color.a * lerp(0.75, 1, rim));
            }
            ENDCG
        }
    }
}
