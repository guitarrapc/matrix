[![Build](https://github.com/guitarrapc/matrix/actions/workflows/build.yaml/badge.svg)](https://github.com/guitarrapc/matrix/actions/workflows/build.yaml)

# matrix

English | [日本語](README-ja.md)

Terminal-based green code rain. [Matrix](https://en.wikipedia.org/wiki/Digital_rain) is something I always wanted to have on my terminal, so I made it.

It runs on Windows, Linux, and macOS as a standalone binary. It supports ASCII rain, full-width movie-style glyphs, true-color themes, and optional Windows Terminal pixel shaders.

![](./images/classic_shader.png)

| Shader | Shaderless (True-color) | ASCII |
| --- | --- | --- |
| ![](./images/classic_shader.png) | ![](./images/classic_shaderless.png) | ![](./images/classic_ascii.png) |

## Quick start

Download the asset for your OS from [GitHub Releases](https://github.com/guitarrapc/matrix/releases), then place `matrix` (or `matrix.exe` on Windows) somewhere on your `PATH`.

```sh
# Windows (Scoop)
scoop bucket add guitarrapc https://github.com/guitarrapc/scoop-bucket
scoop install matrix
```

```bash
# On macOS/Linux, add execute permission if needed.
chmod +x ./matrix

# Run the classic Matrix rain for 5 seconds.
matrix

# Run until a key is pressed. The default duration is 5 seconds.
matrix 0
matrix --duration 0

# Use ASCII glyphs or full-width movie-style glyphs. Default is ascii.
matrix --mode <ascii|movie>

# Pick a built-in color pattern.
matrix --pattern <classic|resurrections|operator|twilight|rain|rainbow>

# Custom colors accept #RGB, #RRGGBB, or 16-color names.
# Fallback order is hex true color, nearest 16-color palette, then colorless ASCII.
matrix --bg "#080300" --head "#FFF3D0" --bright "#FF9F1A" --dim "#5A2100"

# Change frame rate. Higher FPS also makes rain move faster in real time. Default is 14.
matrix --fps 10

# Use one repeated character.
matrix --char "λ"
```

**Usage**

```bash
matrix [duration]
matrix [--duration <seconds>] [--mode <ascii|movie>] [--char <character>]
       [--density <0.0-1.0>] [--fps <1-60>]
       [--pattern <classic|resurrections|operator|twilight|rain|rainbow>]
       [--bg <color>] [--head <color>] [--bright <color>] [--dim <color>]
       [--cursor-intensity <0.5-5.0>]
       [--help] [--version]
```

For exact option behavior, defaults, validation ranges, terminal I/O behavior, and implementation notes, see [the CLI specification](./.github/docs/spec_matrix.md).

### Shaders

If your terminal supports pixel shaders, a terminal GPU shader can add glow or post-processing to the rendered rain. Bloom strength is controlled in the shader file.

Windows Terminal pixel shader examples live in `shaders/windows-terminal`:

| Shader | Effect |
| --- | --- |
| `matrix-bloom.hlsl` | Green bloom tuned for the Matrix rain head cells. |
| `matrix-bloom-soft.hlsl` | Softer, wider bloom. |
| `matrix-ripple.hlsl` | A large timed ripple that expands across the whole screen with highlighted wave crests. |
| `verify-shader.hlsl` | Color inversion sanity check. |

Set `experimental.pixelShaderPath` in a Windows Terminal profile, open a new tab, then run "Toggle shader effects" from the command palette. Use `shaders/windows-terminal/config.example.json` as a minimal settings example.

> [!TIP]
> [Windows Terminal's shader](https://github.com/microsoft/terminal/tree/main/samples/PixelShaders) inputs include time, resolution, background color, and the rendered terminal texture. They do not include mouse click coordinates, so `matrix-ripple.hlsl` uses fixed timed ripple origins.

## Development

Use `dotnet` for local development, debugging, or publishing.

### Requirements

- .NET 10 SDK (file-based C# app)

```bash
# Local run
dotnet run matrix.cs -- [args]
```
