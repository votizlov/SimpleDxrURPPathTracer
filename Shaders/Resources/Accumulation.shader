Shader "Hidden/Accumulation"
{
     HLSLINCLUDE

        #pragma target 2.0
        #pragma editor_sync_compilation
        // Core.hlsl for XR dependencies
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
        // DebuggingFullscreen.hlsl for URP debug draw
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Debug/DebuggingFullscreen.hlsl"
        // Color.hlsl for color space conversion
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"

        // Specialized blit with URP debug draw support and color space conversion support
        // Keep in sync with BlitHDROverlay.shader
        half4 FragmentURPBlit(Varyings input, SamplerState blitsampler)
        {
            half4 color = FragBlit(input, blitsampler);

            #ifdef _LINEAR_TO_SRGB_CONVERSION
            color = LinearToSRGB(color);
            #endif

            #if defined(DEBUG_DISPLAY)
            half4 debugColor = 0;
            float2 uv = input.texcoord;
            if (CanDebugOverrideOutputColor(color, uv, debugColor))
            {
                return debugColor;
            }
            #endif

            return color;
        }
    ENDHLSL

	SubShader
	{
		// No culling or depth
            ZWrite Off ZTest Always Blend Off Cull Off

		Pass
		{
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragAccumulation

            TEXTURE2D_X(_Accumulation);
			int _FrameIndex;
/*
			float4 frag (v2f i) : SV_Target
			{
				float4 currentFrame = tex2D(_CurrentFrame, i.uv);
				float4 accumulation = tex2D(_Accumulation, i.uv);

				// compute linear average of all rendered frames
				float4 color = 1;//(accumulation * _FrameIndex + currentFrame) / (_FrameIndex + 1);
				return color;
			}*/
                float4 FragAccumulation(Varyings input) : SV_Target
            {
				float4 currentFrame = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, input.texcoord.xy, _BlitMipLevel);
				float4 accumulation = SAMPLE_TEXTURE2D_X_LOD(_Accumulation, sampler_LinearClamp, input.texcoord.xy, _BlitMipLevel);
                return (accumulation * _FrameIndex + currentFrame) / (_FrameIndex + 1);
            }
            ENDHLSL
		}
	}
}
