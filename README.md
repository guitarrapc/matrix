[![Build](https://github.com/guitarrapc/matrix/actions/workflows/build.yaml/badge.svg)](https://github.com/guitarrapc/matrix/actions/workflows/build.yaml)

# matrix

English | [日本語](README-ja.md)

Terminal-based green code rain. [Matrix](https://en.wikipedia.org/wiki/Digital_rain) is something I always wanted to have on my terminal, so I made it. It supports true-color shaders and custom color patterns, and it runs on Windows, Linux, and macOS.

![](./images/classic_shader.png)

| Shader | Shaderless (True-color) | ASCII |
| --- | --- | --- |
| ![](./images/classic_shader.png) | ![](./images/classic_shaderless.png) | ![](./images/classic_ascii.png) |

## Quick start

Download the asset for your OS from GitHub Releases, then place `matrix` (or `matrix.exe` on Windows) where you want.

```sh
# Windows (Scoop)
scoop bucket add guitarrapc https://github.com/guitarrapc/scoop-bucket
scoop install matrix
```

```bash
# On macOS/Linux, add execute permission if needed.
chmod +x ./matrix

# Run the classic matrix for 5sec
matrix

# Run infinitely. Press any key to stop. (default: 5sec)
matrix 0
matrix --duration 0

# Specify letters to use. ascii use ASCII letters, movie uses Japanese characters. (default: ascii)
matrix --mode <ascii|movie>

# There are some patterns avaiable
matrix --pattern <classic|resurrections|operator|twilight|rain|rainbow>

# Custom colors (hex or ASCII color names) Let's run a orange theme with a indigo background. If your terminal does not support true-color, it will fallback to the closest color in the 256-color palette.
matrix --bg "#080300" --head "#FFF3D0" --bright "#FF9F1A" --dim "#5A2100"

# When shader is available, reduce intensity for bloom shader. (default: auto)
matrix --shader-bloom <auto|off|on>

# Change rain drop speed. (default: 14)
matrix --fps 10

# Specify character to use.
matrix --char "λ"
```

**Usage**

```bash
matrix [--duration <seconds>] [--mode <ascii|movie>] [--pattern <classic|resurrections|operator|twilight|rain|rainbow>] [--bg <color>] [--head <color>] [--bright <color>] [--dim <color>] [--shader-bloom <auto|off|on>] [--fps <number>] [--char <character>]
```

### Shaders

If your terminal supports shaders, you can use `--shader-bloom` to add bloom effects to the rain.
Windows Terminal `Pixel shader` examples live in `shaders/windows-terminal`:

| Shader | Effect |
| --- | --- |
| `matrix-bloom.hlsl` | Green bloom tuned for the Matrix rain head cells. |
| `matrix-bloom-soft.hlsl` | Softer, wider bloom. |
| `matrix-ripple.hlsl` | A large timed ripple that expands across the whole screen with highlighted wave crests. |
| `verify-shader.hlsl` | Color inversion sanity check. |

Set `experimental.pixelShaderPath` in a Windows Terminal profile, open a new tab, then run "Toggle shader effects" from the command palette. Use `shaders/windows-terminal/config.example.json` as a minimal settings example.

> ![INFO]
> [Windows Terminal's shader](https://github.com/microsoft/terminal/tree/main/samples/PixelShaders) inputs include time, resolution, background color, and the rendered terminal texture. They do not include mouse click coordinates, so `matrix-ripple.hlsl` uses fixed timed ripple origins to trigger the effect. We cannot use clicks to trigger ripples.

## Development

Use `dotnet` for local development, debugging, or publishing.

### Requirements

- .NET 10 SDK (file-based C# app)

```bash
# Local run
dotnet run matrix.cs -- [args]
```
