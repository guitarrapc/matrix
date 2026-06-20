// Green-tinted bloom for the matrix CLI in Windows Terminal.
//
// Setup (all required):
//   1. settings.json profile:
//        "experimental.pixelShaderPath": "D:\\github\\guitarrapc\\matrix\\shaders\\windows-terminal\\matrix-bloom.hlsl"
//   2. Open a NEW tab after saving settings.
//   3. Enable the shader: Command Palette (Ctrl+Shift+P) → "Toggle shader effects"
//      (pixel shaders are OFF by default even when a path is set).
//   4. Run: matrix --shader-bloom on

Texture2D shaderTexture;
SamplerState samplerState;

cbuffer PixelShaderSettings {
    float Time;
    float Scale;
    float2 Resolution;
    float4 Background;
};

static const float BloomRadius = 2.5;
static const float BloomThreshold = 0.06;
static const float BloomKnee = 0.12;
static const float BloomIntensity = 2.2;
static const float BloomSaturation = 1.3;
static const float3 BloomTint = float3(0.188, 1.0, 0.345); // #30FF58
static const float3 LumWeights = float3(0.299, 0.587, 0.114);

static const float3 Samples[16] = {
    float3(0.17, 0.99, 1.0),
    float3(-1.33, 0.47, 0.71),
    float3(-0.85, -1.51, 0.58),
    float3(1.55, -1.26, 0.5),
    float3(1.68, 1.47, 0.45),
    float3(-1.28, 2.09, 0.41),
    float3(-2.46, -0.98, 0.38),
    float3(0.59, -2.77, 0.35),
    float3(3.0, 0.12, 0.33),
    float3(0.41, 3.14, 0.32),
    float3(-3.17, 0.98, 0.30),
    float3(-1.57, -3.09, 0.29),
    float3(2.89, -2.16, 0.28),
    float3(2.72, 2.57, 0.27),
    float3(-2.15, 3.22, 0.26),
    float3(-3.65, -1.63, 0.25),
};

float Luminance(float3 rgb)
{
    return dot(rgb, LumWeights);
}

float4 main(float4 pos : SV_POSITION, float2 tex : TEXCOORD) : SV_TARGET
{
    float4 sample = shaderTexture.Sample(samplerState, tex);
    float3 base = lerp(Background.rgb, sample.rgb, sample.a);
    float2 sampleStep = float2(1.414 * BloomRadius, 1.414 * BloomRadius) / Resolution;

    float3 bloom = float3(0.0, 0.0, 0.0);
    [unroll]
    for (int i = 0; i < 16; i++)
    {
        float3 s = Samples[i];
        float2 sampleUv = clamp(tex + s.xy * sampleStep, 0.0, 1.0);
        float4 neighbor = shaderTexture.Sample(samplerState, sampleUv);
        float3 neighborRgb = lerp(Background.rgb, neighbor.rgb, neighbor.a);
        float l = Luminance(neighborRgb);
        float knee = max(BloomKnee, 0.0001);
        float mask = smoothstep(BloomThreshold, BloomThreshold + knee, l);
        float3 gray = l.xxx;
        float3 saturated = lerp(gray, neighborRgb, BloomSaturation);
        bloom += saturated * BloomTint * (l * mask * s.z * 0.2);
    }

    return float4(base + bloom * BloomIntensity, 1.0);
}
