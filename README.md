# matrix

Terminal-based green code rain.

![](./image_shader.png)

| Shader | Shaderless (True-color) | ASCII |
| --- | --- | --- |
| ![](./image_shader.png) | ![](./image_shader_less.png) | ![](./image_ascii.png) |

## Color patterns

Use `--pattern <name>` to choose a color preset:

| Pattern | Description |
| --- | --- |
| `classic` | Default original trilogy-style green rain. |
| `resurrections` | Yellow-green Resurrections-style rain. |
| `operator` | Classic green without a dim trail color. |
| `twilight` | Deep-blue background with hot-pink rain and yellow heads. |
| `rain` | Blue-cyan rain on a dark rainy-night background. |
| `rainbow` | Seven vertical color bands from left to right: red, orange, yellow, green, blue, indigo, violet. |

`--bg`, `--head`, `--bright`, and `--dim` override preset colors. For `rainbow`, only `--bg` is used because the rain colors come from the seven-band palette.
