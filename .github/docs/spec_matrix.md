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
| Leading edge | The **Head** is at the bottom of the segment, older glyphs sit above. |
| Glyph mutation | Characters may change while falling, positions stay aligned. |
| Same-column recycling | A column may start a second stream before the prior stream fully exits when visible coverage is sparse enough. |

### Display-cell grid

The grid matches **terminal display cells** (width x height), not Unicode code units.

| Mode | Anchor columns | Glyph width |
|---|---|---|
| ascii-matrix / single / movie (soft font) | Any | 1 cell |
| movie (katakana) / kana | Even columns only (`0`, `2`, `4`, ...) | 2 cells (full-width) |

Wide glyphs use an **anchor** cell plus a **Continuation** cell to the right. Continuation cells emit **nothing** during render, not even a space.

In movie mode with katakana, an empty anchor + Continuation pair renders as one **U+3000 IDEOGRAPHIC SPACE** instead of two ASCII spaces. A trailing odd column that cannot hold a wide glyph still renders as an ASCII space.

### Color and brightness

Color is driven by a Matrix rain wave and a trail envelope:

- **Wave:** per-column pulse based on the shader-inspired rain function.
- **Near-head floor:** the Head and first few trail cells stay bright enough even when the wave phase is dark.
- **Wave minimum:** shimmer can dim a trail but should not erase it before the envelope fades out.
- **Trail envelope:** fade from Head (**1.0**) to trail tip (**0.0**), gamma **0.58** keeps mid-trail brightness visible while the tip still reaches the background.
- **Final brightness:** adjusted wave x trail envelope, dithered before palette lookup.
- **Head bloom:** `--head` is applied as an additive boost only on the stream Head, controlled by `--cursor-intensity`.

True Color terminals use 24-bit ANSI. Other terminals fall back to the nearest 16-color name with a stderr warning.

### Presentation

- `--fps` (default **14**, range 1-60) sets frame rate, rain falls 1-2 cells per frame, so higher FPS means faster real-time motion and faster wave pulse.
- Resize applies only after the terminal reports the same size for several consecutive frames.
- Full-frame redraw uses synchronized output, row positioning (`CUP`), clear-to-EOL, and foreground SGR deduplication to avoid frame drops in integrated terminals.

### Tuning defaults

| Parameter | Value |
|---|---|
| FPS (`--fps`) | **14** (1-60) |
| `--density` | 0.0-1.0, default **0.55** (ascii-matrix / single / movie with soft font), **0.7** (movie with katakana, kana) |
| Trail length | height x **0.30** ... **0.90** per stream, min **10** cells |
| Fall speed | 1-2 cells/frame per stream |
| Glyph mutation | ~35% per frame per visible stream cell |
| Active columns | density x **90%** of the columns that can hold a stream (even columns only for katakana), at start / resize and as the adaptive spawn target |
| Spawn | Adaptive baseline with headroom, raised when active columns fall below target |
| New stream trail length | Adaptive up to **+35%** when active columns are below target |
| Stream births | Capped at **4** per frame to reduce synchronized stream deaths |
| Streams per column | Up to **2** active streams |

---

## Character Modes

| Mode | Invocation | Character set |
|---|---|---|
| **ascii-matrix** (default) | none or `--mode ascii` | ASCII letters, digits, symbols |
| **single** | `--char X` | One user character |
| **movie** | `--mode movie` | Film glyphs from a soft font, or a full-width katakana-heavy mix |
| **kana** | `--mode kana` | Full-width katakana-heavy mix, without the soft font check |

`--char` together with `--mode movie` or `--mode kana` is a fatal error. `--char` selects single-character mode.

### movie mode

**movie** aims at the glyphs seen in the films, which Unicode cannot express (many are mirrored katakana and digits), **without installing or shipping a font**. At startup it picks one of two renderings:

| Rendering | When | Glyphs |
|---|---|---|
| Soft font | The terminal confirms soft font (DECDLD) support | 55 film glyphs, 1 cell wide |
| Katakana | Otherwise, with a stderr warning that names the reason | Full-width katakana, marks, symbols |

`--mode kana` always uses the katakana rendering, skips the check, and prints no warning. It is for terminals where the check is wrong or slow, and for people who prefer the katakana look. There is no option to force the soft font: on a terminal without support it shows ASCII symbols, which is worse than katakana.

**Soft font rendering**

- The glyphs are sent to the terminal as a **DECDLD soft font** (DRCS) when the rain starts, mapped onto G0, and G0 is set back to ASCII on exit. The font exists only in that terminal session.
- Streams emit ASCII codes `0x21` and up, the terminal draws each code with its soft-font glyph. SGR colors apply as usual, so every color pattern works.
- 55 glyphs, each a 12 x 24 one-bit bitmap embedded in the binary. They are rasterized from `Matrix-Code.ttf` by Rezmason (MIT License, see `THIRD_PARTY_NOTICES.md`).
- Glyphs are 1 cell wide, so streams use every column like ascii-matrix, and UTF-8 is not needed.
- Terminals with soft font support include Windows Terminal, xterm with a VT340 terminal ID, mlterm, and RLogin. iTerm2, kitty, Alacritty, GNOME Terminal, and macOS Terminal lack it.

**Detection**

- The terminal is asked for its attributes (DA1). Soft fonts are used only when the reply lists attribute 7 (soft character sets) in a VT200-or-later class (first parameter 61-69).
- Katakana is used when there is no VT output, stdin is not a tty, the reply lacks attribute 7, or no reply comes within 1 second. Older Windows Terminal draws soft fonts but replies `?1;0c`, so it gets katakana.
- On Windows, DA1 is trusted only in Windows Terminal (`WT_SESSION`) and in a classic console window. Other terminals that host the program through ConPTY get katakana and a warning, without a query (see Lessons Learned: movie mode terminal detection).

**Katakana rendering** (movie fallback and `--mode kana`)

Requires UTF-8 output. The program attempts to set console input/output to UTF-8 and fails if the terminal cannot support it. Streams use **full-width glyphs on even columns only**, half-width characters are excluded because mixed display widths break vertical alignment.

Glyph mix (weighted random):

| Pool | Contents | Share |
|---|---|---|
| Katakana | Clear, voiced, semi-voiced katakana | **80%** |
| Marks | `〇` `ー` `々` `〆` `ヶ` `ヵ` `ヿ` `ヽ` `ヾ` `〃` | **17%** |
| Symbols | `・` `゠` `〈` `〉` `゛` `゜` `〓` | **3%** |

Every movie glyph stays within **U+3000-U+30FF** (see Lessons Learned: movie mode font fallback stutter).

---

## Command-Line Interface

```text
matrix [duration]
matrix --duration <seconds>
matrix --char <character>
matrix --mode <ascii|movie|kana>
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
| Negative positional (e.g. `matrix -1`) | Rejected as an option-like token, use `--duration -1` |

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

For non-rainbow patterns, `--bg`, `--head`, `--bright`, and `--dim` override preset colors. For `rainbow`, only `--bg` overrides the preset, rain colors come from the band palette.

Terminal pixel shaders are configured outside the CLI. `matrix` does not detect shader state or change its software head bloom for shaders, bloom strength belongs in the shader file.

True Color detection is conservative: `COLORTERM=truecolor|24bit`, `TERM` containing `truecolor` or `24bit`, Windows Terminal, VS Code, Apple Terminal, and iTerm are accepted. `TERM=xterm-256color` alone does **not** imply 24-bit color. Windows enables virtual terminal processing at startup.

Invalid colors and unknown patterns are fatal parse errors. Removed or unknown options are also fatal parse errors.

---

## Exit and Terminal I/O

Playback ends on **duration elapsed** or **any keypress** without requiring Enter. `Ctrl+C` restores the terminal and exits cleanly.

| stdout | stdin | Result |
|---|---|---|
| TTY | TTY | Full animation + key exit |
| TTY | not TTY | Warning, duration-only |
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
- **Decision:** shift each stream down as a unit, clear cells only when they leave the segment or the stream ends. Black belongs between columns, not inside trails.

### movie mode layout

- **Tried:** mixed half-width and full-width glyphs in the same logical column.
- **Result:** broken vertical alignment and column collisions.
- **Decision:** even anchor columns, full-width glyphs only, Continuation cell for the second display cell.
- **Pitfall:** emitting a space on Continuation cells caused cursor drift and crooked lines. Continuation must write zero bytes.
- **Glyph mix:** katakana-only was too plain vs the film. Full-width digits, five kanji, and full-width symbols were added first, but they made wide screens stutter (see below). Marks and symbols from U+3000-U+30FF now keep the mix varied: `〇` stands in for zero, `ー` for `一`, `゠` for `=`, and `〈` `〉` for `<` `>`.

### movie mode font fallback stutter

- **Symptom:** a full-screen Windows Terminal on a portrait 1440 x 2560 display (156 x 133 cells, default Cascadia Mono) stuttered in movie mode while ascii and rainbow stayed smooth. The program side was not the bottleneck: each frame was built and written in about 1 ms.
- **Cause:** typical terminal fonts lack katakana, so movie glyphs come from a fallback font. Windows Terminal maps fonts and shapes text for each run of characters that share a font. ASCII spaces between glyphs split every row into dozens of short runs, alternating between the primary font and the fallback font. The terminal render thread used about 14x the CPU of ascii mode. At 30 FPS it presented only 208-232 of about 253 frames.
- **Decision:** render empty movie pairs as U+3000. Japanese fallback fonts cover it, so a row stays in one fallback run. The render thread used about 30% less CPU, and at 30 FPS the present rate matched ascii (252 frames, p95 35 ms).
- **Tried:** switching the terminal font to one that contains katakana (BIZ UDGothic, Meiryo). In an isolated DirectWrite benchmark this cut shaping cost, but in Windows Terminal the render thread CPU did not change, with or without U+3000. Changing the font is not a fix.
- **Wide screens:** on a full-screen 5120 x 2160 display (565 x 112 cells), the render thread stays saturated in movie mode with or without U+3000, and presents only about 8-12 FPS at both 14 and 30 FPS. ascii at the same size uses about 12%. A sampling profile of the render thread shows about 80% of its time in `IDWriteFontFallback::MapCharacters`, and only about 8% in shaping and under 1% in drawing. System font fallback ends a mapped run at every switch between Unicode ranges, even when both sides map to the same font. Only U+3000-U+30FF (CJK symbols, hiragana, katakana) stays in one run with katakana, full-width digits, kanji, and full-width symbols each start a new run. With U+3000 and a katakana-only pool, the same screen presents 28.8 FPS at about 50% CPU.
- **Decision:** limit the movie pool to U+3000-U+30FF (katakana, marks, symbols). On the 5120 x 2160 screen at 30 FPS the render thread dropped from 91-94% to 30-36% CPU, and the present rate rose from about 11 FPS to the same rate as ascii.
- **Pitfall:** do not reintroduce ASCII spaces inside movie rows, or glyphs outside U+3000-U+30FF. Look-alikes in other blocks (Suzhou numerals `〡`-`〩`, Kangxi radicals, enclosed CJK) also split runs or fall back to other fonts. Each fallback glyph still costs more to shape than an ASCII glyph, so movie mode has less headroom than ascii.

### movie mode soft font

- **Rejected:** switching the terminal font only while the program runs. Windows Terminal has no escape sequence for the font face, conhost (`SetCurrentConsoleFontEx`) accepts only installed fonts, and `AddFontResourceEx(FR_PRIVATE)` registers a font only for the calling process, not for the terminal process that draws the text. xterm (OSC 50) and iTerm2 (`SetProfile`) have their own sequences, but no sequence works across terminals.
- **Rejected:** Unicode look-alikes. Mirrored katakana do not exist in Unicode, and non-ASCII glyphs bring back font fallback costs.
- **Rejected:** drawing the screen as images (Sixel, kitty graphics) or as braille or block pixel art. Images are heavy at 14-60 FPS, and pixel art needs 2-3 cells per glyph.
- **Decision:** DECDLD soft font. It is a VT220-era standard, supported by Windows Terminal, and the output stays ASCII.
- **Tried:** a separate `--mode real` that always used the soft font and warned when support was not confirmed. Unsupported terminals then showed ASCII symbols, which is worse than katakana. **Decision:** `--mode movie` picks the soft font only on a confirmed DA1 reply and falls back to katakana with a warning, `--mode real` was removed. `--mode kana` was added so the fallback can be chosen without the check and its warning.
- **Glyph scaling:** `Matrix-Code.ttf` glyphs are about as wide as they are tall. Scaling them uniformly into a 12 x 24 cell left them 8 pixels tall, so width and height are scaled separately to a 10 x 16 box. The box size comes from the katakana only, the vertical bar and `¦` are taller and would shrink every glyph.

### movie mode terminal detection

- **Symptom:** in the Claude desktop terminal panel (xterm.js, `TERM_PROGRAM=claude-desktop`), DA1 replied `?61;6;7;21;22;23;24;28;32;42c`, listing soft fonts, but the panel drew the plain ASCII codes.
- **Cause:** on Windows, terminals other than a classic console window host programs through ConPTY. conhost sits in between and answers DA1 with its own attributes, not the hosting terminal's.
- **Decision:** trust DA1 only when `WT_SESSION` is set or the console window is visible (a classic conhost window, which draws soft fonts itself). Under ConPTY the console window is a hidden pseudo window, so any other host gets katakana with a warning.

### Density and stutter

- Fixed per-frame spawn could not maintain density across variable stream lifetimes.
- Adaptive spawn and trail-length boost maintain target coverage, but overly synchronized births produce cohorts that die together.
- **Removed:** a 1.5x density multiplier for katakana. It was added to offset even-column spacing, but the active-column target is already a fraction of the columns that can hold a stream, so the spacing was counted twice. Katakana reached the 90% cap from `--density` 0.67, so 0.67-1.0 all looked the same and the 0.7 default was already the densest setting. Now the same `--density` gives the same active fraction in every mode.
- VS Code integrated terminal showed periodic global lag when frame size spiked. The durable constraints are: stagger initial heads, cap births per frame, allow sparse same-column recycling, deduplicate SGR, use row positioning plus clear-to-EOL, wrap frames in synchronized output, and wait until each frame's scheduled time.

### Frame pacing

- **Symptom:** on Windows, `--fps 30` presented about 21 FPS in every mode, and the default 14 FPS ran at about 12.5 FPS. Each frame took about 3 ms to build, but the app's frame interval averaged 46.8 ms.
- **Cause:** `Thread.Sleep` rounds up to the 15.6 ms system timer tick, so a 30 ms sleep became 46.8 ms. A system-wide 1 ms timer requested by another process no longer shortens this process's sleeps (Windows 10 2004 and later). Sleeping "frame time minus render time" also let rounding errors add up across frames.
- **Decision:** wait until a fixed schedule of frame times, and on Windows wait on a high-resolution waitable timer (`CREATE_WAITABLE_TIMER_HIGH_RESOLUTION`, Windows 10 1803 and later). This is an OS wait, not a spin, and it does not raise the system-wide timer rate. Falling more than one frame behind restarts the schedule instead of rendering catch-up bursts. If the timer cannot be created, waits fall back to `Thread.Sleep`. Linux and macOS sleeps are already precise.
- **Result:** on a full-screen 5120 x 2160 Windows Terminal, ascii and movie present exactly 30.0 FPS at `--fps 30` (p95 34 ms) and 14.0 FPS at the default. ascii reaches 60.0 FPS at `--fps 60`, movie reaches about 56 FPS there, limited by the terminal. Per-frame app CPU did not change, so the wait does not spin.

### Color model

| Stage | Approach | Outcome |
|---|---|---|
| 1 | Discrete Head / Bright / Dim states | Too coarse |
| 2 | Fade byte = distance from Head, piecewise RGB | Better trails, still not Matrix-like |
| 3 | Palette LUT + wave brightness + dither + head-only additive bloom | Current model |

Brightness is wave x trail envelope, with a near-head floor and wave minimum. Wave-only brightness left some stream tips vivid when the wave phase was high, the envelope guarantees a dim tail on every column. White belongs to additive Head bloom only, not to the trail LUT.

### Wave port

These are behavioral constraints, easy to get wrong in a top-origin terminal:

1. `raindropLength` is a small scale factor, not trail cell count.
2. Terminal row 0 is the top, shader-like glyph Y must be flipped so 0 is the bottom.
3. Cursor boost is additive and strong, it must apply only to the stream Head.
4. Interpolating the trail LUT through white can create yellow bands, keep the LUT on the selected rain ramp and apply Head color separately.

### Runtime and build

- File-based `#:` project + top-level statements: avoid file-scoped `const` and extra `Program` types, use a static help class and `Assembly.GetExecutingAssembly()` for `--version`.
- Unix raw input: Linux and macOS need separate `termios` layouts.
- True Color gate stays conservative: 256-color `TERM` is not enough.
- Hot path: pre-built ANSI prefixes, pooled grid/buffers, one `Stream.Write` + `Flush` per frame, and glyph/color pools built once at startup.
