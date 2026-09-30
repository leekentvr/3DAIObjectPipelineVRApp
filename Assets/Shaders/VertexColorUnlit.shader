// Unlit URP shader that renders a mesh's per-vertex colors (glTF COLOR_0).
//
// Why this exists: SAM3D reconstructions carry their appearance as vertex colors and
// ship with NO material/texture. URP's stock Unlit/Lit shaders ignore vertex color, so
// such a mesh comes out flat grey. This reads the COLOR semantic and outputs it, so the
// baked colors show. It's a plain hand-written shader (not a ShaderGraph), so it's small
// and reliable to include in a Quest/Android build -- reference it from a Material asset
// (assigned to MeshInstantiator._fallbackMaterial) so it isn't stripped.
Shader "VR23D/VertexColorUnlit"
{
    Properties
    {
        _BaseColor ("Tint", Color) = (1,1,1,1)
        // SAM3D bakes COLOR_0 from image/Gaussian-splat RGB, which are sRGB display
        // values, but glTF declares COLOR_0 as LINEAR, so glTFast uploads the bytes
        // unconverted. In a Linear-color-space project (this one: ProjectSettings
        // m_ActiveColorSpace = 1) the camera then applies linear->sRGB on output, so
        // an untouched sRGB value gets brightened once too often -> the "washed out",
        // low-contrast look. With this toggle ON we undo that by converting the vertex
        // color sRGB->linear here, so the final output encode cancels back to the true
        // color. Set to 0 if your glTF importer already delivers linear vertex colors
        // (then leaving it on would make everything too dark instead).
        [Toggle] _VertexColorIsSRGB ("Vertex color is sRGB (convert to linear)", Float) = 1
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        Pass
        {
            Name "Unlit"
            Cull Off // reconstructions can have inconsistent winding; draw both sides

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                half4  color      : COLOR;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                half4  color       : COLOR;
            };

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                float _VertexColorIsSRGB;
            CBUFFER_END

            Varyings vert (Attributes IN)
            {
                Varyings OUT;
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.color = IN.color;
                return OUT;
            }

            half4 frag (Varyings IN) : SV_Target
            {
                half3 rgb = IN.color.rgb;
                // See _VertexColorIsSRGB note above: undo the extra sRGB->linear the
                // Linear-space pipeline would otherwise skip on these vertex colors.
                if (_VertexColorIsSRGB > 0.5)
                    rgb = SRGBToLinear(rgb);
                return half4(rgb, IN.color.a) * _BaseColor;
            }
            ENDHLSL
        }
    }

    // If the URP pass is unavailable for some reason, still draw something visible.
    Fallback "Universal Render Pipeline/Unlit"
}
