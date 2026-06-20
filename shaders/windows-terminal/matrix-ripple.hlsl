// Time-driven ripple distortion for Windows Terminal.
//
// Windows Terminal pixel shaders receive Time, Scale, Resolution, Background,
// and the terminal render texture. They do not receive mouse click positions,
// so this shader emits repeating ripples from fixed points instead of true
// click-origin ripples.
//
// Setup (all required):
//   1. settings.json profile:
//        "experimental.pixelShaderPath": "D:\\github\\guitarrapc\\matrix\\shaders\\windows-terminal\\matrix-ripple.hlsl"
//   2. Open a NEW tab after saving settings.
//   3. Enable the shader: Command Palette (Ctrl+Shift+P) -> "Toggle shader effects"
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

static const float RippleInterval = 6.2;
static const float RippleActiveSeconds = 5.0;
static const float RippleSpeed = 0.24;
static const float RippleFrequency = 44.0;
static const float RippleWidth = 0.18;
static const float RippleDistortion = 0.010;
static const float RippleBrightness = 0.62;
static const float2 RippleOriginMin = float2(0.18, 0.18);
static const float2 RippleOriginMax = float2(0.82, 0.72);
static const float3 LumWeights = float3(0.299, 0.587, 0.114);

float3 CompositeTerminal(float2 tex)
{
    float4 sample = shaderTexture.Sample(samplerState, tex);
    return lerp(Background.rgb, sample.rgb, sample.a);
}

float Luminance(float3 rgb)
{
    return dot(rgb, LumWeights);
}

struct RippleResult
{
    float2 Uv;
    float Brightness;
};

float Hash(float seed)
{
    return frac(sin(seed) * 43758.5453);
}

float2 RippleOriginForCycle(float cycle)
{
    float2 random = float2(Hash(cycle * 12.9898 + 78.233), Hash(cycle * 39.3468 + 11.135));
    return lerp(RippleOriginMin, RippleOriginMax, random);
}

RippleResult RippledUv(float2 tex)
{
    float2 safeResolution = max(Resolution, float2(1.0, 1.0));
    float aspect = safeResolution.x / safeResolution.y;
    float elapsed = fmod(Time, RippleInterval);
    float cycle = floor(Time / RippleInterval);
    float2 origin = RippleOriginForCycle(cycle);
    float active = step(elapsed, RippleActiveSeconds);
    float progress = saturate(elapsed / RippleActiveSeconds);
    float2 delta = tex - origin;
    delta.x *= aspect;
    float dist = length(delta);
    float t = dist - elapsed * RippleSpeed;
    float safeT = max(abs(t), 0.028);
    float wave = sin(t * RippleFrequency) / safeT;
    float envelope = exp(-pow(t / RippleWidth, 2.0));
    float grow = smoothstep(0.0, 0.08, progress);
    float fade = 1.0 - smoothstep(0.78, 1.0, progress);
    float ripple = wave * envelope * grow * fade * active;
    float brightness = saturate(ripple * 0.11);
    float2 dir = normalize(delta + float2(0.0001, 0.0001));
    dir.x /= aspect;

    RippleResult result;
    result.Uv = clamp(tex + dir * ripple * RippleDistortion, 0.0, 1.0);
    result.Brightness = brightness;
    return result;
}

float4 main(float4 pos : SV_POSITION, float2 tex : TEXCOORD) : SV_TARGET
{
    RippleResult ripple = RippledUv(tex);
    float3 base = CompositeTerminal(ripple.Uv);
    float glyphMask = smoothstep(0.025, 0.22, Luminance(base));
    float3 color = base * (1.0 + ripple.Brightness * RippleBrightness * glyphMask);

    return float4(saturate(color), 1.0);
}
