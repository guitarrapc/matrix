// Sanity check: inverts terminal colors. If this works, matrix-bloom.hlsl path is valid.
//
//   "experimental.pixelShaderPath": "...\\verify-shader.hlsl"
//   Ctrl+Shift+P → Toggle shader effects

Texture2D shaderTexture;
SamplerState samplerState;

cbuffer PixelShaderSettings {
    float Time;
    float Scale;
    float2 Resolution;
    float4 Background;
};

float4 main(float4 pos : SV_POSITION, float2 tex : TEXCOORD) : SV_TARGET
{
    float4 color = shaderTexture.Sample(samplerState, tex);
    color.rgb = lerp(Background.rgb, 1.0 - color.rgb, color.a);
    return float4(color.rgb, 1.0);
}
