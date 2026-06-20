# matrix CLI Specification

Status: **Implemented**

## Motivation

`matrix` is a terminal CLI that plays a Matrix-inspired digital rain animation for a configurable duration. It targets demos, shell flair, and idle ambience with low CPU and memory use.

The look should evoke the film: vertical falls, bright leading glyphs, colored trails fading into the background, and plenty of empty space between columns.

Ship as **standalone Native AOT binaries** from a **single `matrix.cs` file**, with no shared .NET runtime dependency.

---

## Visual Behavior

### Column rain

Each column hosts one or more independent **streams**. A stream falls **straight down** along a fixed column **X** as one **contiguous** vertical segment. Black space comes mostly from inactive columns and gaps between streams, not from random holes inside an active trail.

| Requirement | Meaning |
|---|---|
| Fixed column X | All glyphs in a stream share one terminal column. |
| Downward motion | The segment shifts down each frame by 1-2 cells. |
| Leading edge | The **Head** is at the bottom of the segment; older glyphs sit above. |
| Glyph mutation | Characters may change while falling; positions stay aligned. |
| Same-column recycling | A column may start a second stream before the prior stream fully exits when visible coverage is sparse enough. |

### Display-cell grid

The grid matches **terminal display cells** (width x height), not Unicode code units.

| Mode | Anchor columns | Glyph width |
|---|---|---|
| ascii-matrix / single | Any | 1 cell |
| movie | Even columns only (`0`, `2`, `4`, ...) | 2 cells (full-width) |

Wide glyphs use an **anchor** cell plus a **Continuation** cell to the right. Continuation cells emit **nothing** during render, not even a space.

### Color and brightness

Color is driven by a Matrix rain wave and a trail envelope:

- **Wave:** per-column pulse based on the shader-inspired rain function.
- **Near-head floor:** the Head and first few trail cells stay bright enough even when the wave phase is dark.
- **Wave minimum:** shimmer can dim a trail but should not erase it before the envelope fades out.
- **Trail envelope:** fade from Head (**1.0**) to trail tip (**0.0**); gamma **0.58** keeps mid-trail brightness visible while the tip still reaches the background.
- **Final brightness:** adjusted wave x trail envelope, dithered before palette lookup.
- **Head bloom:** `--head` is applied as an additive boost only on the stream Head, controlled by `--cursor-intensity`.

True Color terminals use 24-bit ANSI. Other terminals fall back to the nearest 16-color name with a stderr warning.

### Presentation

- `--fps` (default **14**, range 1-60) sets frame rate; rain falls 1-2 cells per frame, so higher FPS means faster real-time motion and faster wave pulse.
- Resize applies only after the terminal reports the same size for several consecutive frames.
- Full-frame redraw uses synchronized output, row positioning (`CUP`), clear-to-EOL, and foreground SGR deduplication to avoid frame drops in integrated terminals.

### Tuning defaults

| Parameter | Value |
|---|---|
| FPS (`--fps`) | **14** (1-60) |
| `--density` | 0.0-1.0; default **0.55** (ascii-matrix / single), **0.7** (movie) |
| Trail length | height x **0.30** ... **0.90** per stream, min **10** cells |
| Fall speed | 1-2 cells/frame per stream |
| Glyph mutation | ~35% per frame per visible stream cell |
| Movie density | effective density x **1.5** (cap 1.0) |
| Initial active columns | effective density x **90%** at start / resize |
| Spawn | Adaptive baseline with headroom; raised when active columns fall below target |
| New stream trail length | Adaptive, up to **+35%** when active columns are below target |
| Stream births | Capped at **4** per frame to reduce synchronized stream deaths |
| Streams per column | Up to **2** active streams |

---

## Character Modes

| Mode | Invocation | Character set |
|---|---|---|
| **ascii-matrix** (default) | none or `--mode ascii` | ASCII letters, digits, symbols |
| **single** | `--char X` | One user character |
| **movie** | `--mode movie` | Full-width katakana-heavy mix |

`--char` and `--mode movie` together are a fatal error. `--char` selects single-character mode.

**movie** requires UTF-8 output. The program attempts to set console input/output to UTF-8 and fails if the terminal cannot support it. Movie streams use **full-width glyphs on even columns only**; half-width characters are excluded because mixed display widths break vertical alignment.

**movie** glyph mix (weighted random):

| Pool | Contents | Share |
|---|---|---|
| Katakana | Clear, voiced, semi-voiced katakana | **80%** |
| Digits | Full-width `0`-`9` | **13.6%** |
| Kanji | `日` `三` `二` `一` `十` | **3.4%** |
| Symbols | `:` middle dot, period, math/comparison/line symbols, dakuten, handakuten | **3%** |

---

## Command-Line Interface

```text
matrix [duration]
matrix --duration <seconds>
matrix --char <character>
matrix --mode <ascii|movie>
matrix --density <0.0-1.0>
matrix --fps <1-60>
matrix --pattern <classic|resurrections|operator|twilight|rain|rainbow>
matrix --bg <color> --head <color> --bright <color> --dim <color>
matrix --cursor-intensity <0.5-5.0>
matrix --help
matrix --version
```

| Duration | Behavior |
|---|---|
| Omitted | **5 seconds** |
| Positive | Run that many seconds |
| `0` or negative | Run until key exit |
| Negative positional (e.g. `matrix -1`) | Rejected as an option-like token; use `--duration -1` |

User-facing messages are **English only** in v1.

---

## Color Options

**Formats:** `#RGB`, `#RRGGBB`, or standard 16-color names (`black`, `green`, `darkgreen`, `white`, etc.).

| Option | Default |
|---|---|
| `--pattern` | `classic` |
| `--bg` | `#000000` |
| `--head` | `#FFFFFF` |
| `--bright` | `#30FF58` |
| `--dim` | `#053D16` |
| `--cursor-intensity` | **2.5** |

Color patterns:

| Pattern | Behavior |
|---|---|
| `classic` | Original trilogy-style green rain. |
| `resurrections` | Yellow-green rain on a very dark green background. |
| `operator` | Classic green with no separate dim trail color. |
| `twilight` | Deep-blue background, violet/pink/magenta rain, yellow heads. |
| `rain` | Blue-cyan rain on a dark rainy-night background. |
| `rainbow` | Seven vertical color bands: red, orange, yellow, green, blue, indigo, violet. |

For non-rainbow patterns, `--bg`, `--head`, `--bright`, and `--dim` override preset colors. For `rainbow`, only `--bg` overrides the preset; rain colors come from the band palette.

Terminal pixel shaders are configured outside the CLI. `matrix` does not detect shader state or change its software head bloom for shaders; bloom strength belongs in the shader file.

True Color detection is conservative: `COLORTERM=truecolor|24bit`, `TERM` containing `truecolor` or `24bit`, Windows Terminal, VS Code, Apple Terminal, and iTerm are accepted. `TERM=xterm-256color` alone does **not** imply 24-bit color. Windows enables virtual terminal processing at startup.

Invalid colors and unknown patterns are fatal parse errors. Removed or unknown options are also fatal parse errors.

---

## Exit and Terminal I/O

Playback ends on **duration elapsed** or **any keypress** without requiring Enter. `Ctrl+C` restores the terminal and exits cleanly.

| stdout | stdin | Result |
|---|---|---|
| TTY | TTY | Full animation + key exit |
| TTY | not TTY | Warning; duration-only |
| not TTY | any | Error (`--help` / `--version` excepted) |

Normal completion exits **0**. Parse, validation, TTY, UTF-8, and mode errors exit **1**.

Non-TTY stdout streaming is **out of scope for v1**.

---

## Distribution

- Single file `matrix.cs`, `net10.0`, Native AOT, self-contained, single-file, BCL only, assembly name `matrix`.
- CI builds six RIDs: `linux-x64`, `linux-arm64`, `win-x64`, `win-arm64`, `osx-arm64`, `osx-x64` (`.tar.gz` on Unix, `.zip` on Windows).
- Windows Terminal shader examples live under `shaders/windows-terminal`.

---

## Lessons Learned

Decisions and pitfalls discovered during implementation. **Do not revert these without re-reading this section.**

### Vertical motion

- **Tried:** random Dim -> Empty inside active columns.
- **Result:** scattered flicker, not film-like rain.
- **Decision:** shift each stream down as a unit; clear cells only when they leave the segment or the stream ends. Black belongs between columns, not inside trails.

### movie mode layout

- **Tried:** mixed half-width and full-width glyphs in the same logical column.
- **Result:** broken vertical alignment and column collisions.
- **Decision:** even anchor columns, full-width glyphs only, Continuation cell for the second display cell.
- **Pitfall:** emitting a space on Continuation cells caused cursor drift and crooked lines. Continuation must write zero bytes.
- **Glyph mix:** katakana-only was too plain vs the film. Full-width digits, five kanji, and a small symbol bucket keep the mix varied while remaining mostly katakana.

### Density and stutter

- Fixed per-frame spawn could not maintain density across variable stream lifetimes.
- Adaptive spawn and trail-length boost maintain target coverage, but overly synchronized births produce cohorts that die together.
- VS Code integrated terminal showed periodic global lag when frame size spiked. The durable constraints are: stagger initial heads, cap births per frame, allow sparse same-column recycling, deduplicate SGR, use row positioning plus clear-to-EOL, wrap frames in synchronized output, and sleep based on actual render time.

### Color model

| Stage | Approach | Outcome |
|---|---|---|
| 1 | Discrete Head / Bright / Dim states | Too coarse |
| 2 | Fade byte = distance from Head, piecewise RGB | Better trails, still not Matrix-like |
| 3 | Palette LUT + wave brightness + dither + head-only additive bloom | Current model |

Brightness is wave x trail envelope, with a near-head floor and wave minimum. Wave-only brightness left some stream tips vivid when the wave phase was high; the envelope guarantees a dim tail on every column. White belongs to additive Head bloom only, not to the trail LUT.

### Wave port

These are behavioral constraints, easy to get wrong in a top-origin terminal:

1. `raindropLength` is a small scale factor, not trail cell count.
2. Terminal row 0 is the top; shader-like glyph Y must be flipped so 0 is the bottom.
3. Cursor boost is additive and strong; it must apply only to the stream Head.
4. Interpolating the trail LUT through white can create yellow bands; keep the LUT on the selected rain ramp and apply Head color separately.

### Runtime and build

- File-based `#:` project + top-level statements: avoid file-scoped `const` and extra `Program` types; use a static help class and `Assembly.GetExecutingAssembly()` for `--version`.
- Unix raw input: Linux and macOS need separate `termios` layouts.
- True Color gate stays conservative: 256-color `TERM` is not enough.
- Hot path: pre-built ANSI prefixes, pooled grid/buffers, one `Stream.Write` + `Flush` per frame, and glyph/color pools built once at startup.
