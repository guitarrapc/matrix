# matrix CLI Specification

Status: **Implemented**

## Motivation

`matrix` is a terminal CLI that plays a Matrix-inspired digital rain animation for a configurable duration. It targets demos, shell flair, and idle ambience with low CPU and memory use.

The look should evoke the film: vertical falls, a bright leading glyph, green trails fading into black, and plenty of empty space between columns.

Ship as **standalone Native AOT binaries** from a **single `matrix.cs` file**, with no shared .NET runtime dependency.

---

## Visual Behavior

### Column rain

Each column is an independent **stream** (speed, trail length, active/inactive). Streams fall **straight down** along a fixed column **X** as one **contiguous** vertical segment — no random holes inside an active trail. Black space comes mostly from **inactive columns and gaps**, not from erasing cells mid-trail.

| Requirement | Meaning |
|---|---|
| Fixed column X | All glyphs in a stream share one terminal column. |
| Downward motion | The whole segment shifts down each frame (1–2 cells per frame). |
| Leading edge | The **Head** is at the **bottom** of the segment; older glyphs sit above. |
| Glyph mutation | Characters may change while falling; position stays aligned. |

### Display-cell grid

The grid matches **terminal display cells** (width × height), not Unicode code units.

| Mode | Anchor columns | Glyph width |
|---|---|---|
| ascii-matrix / single | Any | 1 cell |
| movie | Even columns only (`0`, `2`, `4`, …) | 2 cells (full-width) |

Wide glyphs use an **anchor** cell plus a **Continuation** cell to the right. Continuation cells emit **nothing** during render (not even a space).

### Color and brightness

Color is driven by the Matrix rain wave **and** a trail envelope:

- **Wave** — per-column pulse and raindrop truncation (reference `getRainBrightness`).
- **Trail envelope** — fade from Head (**1.0**) to trail tip (**0.0**); gamma **0.78** lifts mid-trail brightness while the tip still reaches black. Final brightness = wave × envelope.
- **Palette:** four CLI colors define a brightness→color map. LUT high end is boosted (`--bright` × 1.18, hotter peak); **`--head` is applied only on the stream Head** (white bloom), not across high wave brightness.
- **Cursor highlight:** the Head cell only (`dist == 0`); `--head` added at `--cursor-intensity` (default **2.5**).
- **Dither** on brightness before palette lookup.

True Color terminals use 24-bit ANSI; others fall back to the nearest 16-color name with a stderr warning.

### Presentation

- **`--fps`** (default **14**, range 1–60) sets frame rate; rain falls **1–2 cells per frame**, so higher FPS means faster motion in real time and a faster wave pulse. Lower FPS for a calmer effect (useful on slow integrated terminals).
- Recalculate grid on resize when the environment allows (stable size for several consecutive frames).
- Full-frame redraw uses synchronized output when the terminal accepts it; avoid `\n`-chained full-screen updates on slow integrated terminals.

### Tuning (defaults)

| Parameter | Value |
|---|---|
| FPS (`--fps`) | **14** (1–60) |
| `--density` | 0.0–1.0; default **0.55** (ascii-matrix / single), **0.7** (movie) |
| `--fps` | **1–60**; default **14**. Scales refresh rate and real-time fall speed (columns still move 1–2 cells per frame). |
| Trail length | height × **0.30** … 0.90 per stream (min **10** cells) |
| Fall speed | 1–2 cells/frame per column |
| Glyph mutation | ~35% per frame per visible stream cell |
| Movie density | effective density × **1.5** (cap 1.0) |
| Initial active columns | effective density × **90%** at start / resize (`density 1.0` → **90%** of stream columns) |
| Spawn (inactive columns) | **Adaptive** — baseline × headroom at target; **raised** when active streams fall below target (~0.2s catch-up) |
| New stream trail length | **Adaptive** — up to **+35%** when active columns are below target (offsets short / faded-looking trails) |

---

## Character Modes

| Mode | Invocation | Character set |
|---|---|---|
| **ascii-matrix** (default) | (none) | Mixed ASCII letters, digits, symbols |
| **single** | `--char X` | One user character |
| **movie** | `--mode movie` | Full-width katakana-heavy mix (see below) |

`--char` and `--mode movie` together are a fatal error. `--char` wins over other mode flags.

**movie** requires UTF-8 output; no ascii-matrix fallback. Active streams use **full-width (two-cell) glyphs on even columns** only — half-width characters are excluded because mixed display widths break vertical alignment.

**movie** glyph mix (weighted random):

| Pool | Contents | Share |
|---|---|---|
| Katakana |清音・濁音・半濁音 | **80%** |
| Digits |全角 `０`–`９` | **13.6%** (uniform) |
| Kanji |`日` `三` `二` `一` `十` | **3.4%** (uniform) |
| Symbols |`：` `・` `．` `＝` `＋` `－` `＜` `＞` `｜` `゛` `゜` | **3%** (uniform) |

Within the 17% non-katakana bucket, digits are favored over kanji (80% / 20%). `゛` `゜` and `・` are included tentatively; remove if terminal width misbehaves.

---

## Command-Line Interface

```text
matrix [duration]
matrix --duration <seconds>
matrix --char <character>
matrix --mode movie
matrix --density <0.0-1.0>
matrix --fps <1-60>
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

User-facing messages are **English only** in v1.

---

## Color Options

**Formats:** `#RGB` / `#RRGGBB`, or standard 16-color names (`black`, `green`, `white`, …).

| Option | Default |
|---|---|
| `--bg` | `#000000` |
| `--head` | `#FFFFFF` |
| `--bright` | `#30FF58` |
| `--dim` | `#00AA1C` |
| `--cursor-intensity` | **2.5** (Head-cell additive bloom using `--head`) |

Invalid colors are fatal. True Color is inferred from environment signals (`COLORTERM`, `WT_SESSION`, `TERM_PROGRAM`, etc.); `TERM=xterm-256color` alone does **not** enable 24-bit. Windows enables virtual terminal processing at startup.

---

## Exit and Terminal I/O

Playback ends on **duration elapsed** or **any keypress** (no Enter). `Ctrl+C` restores the terminal and exits cleanly.

| stdout | stdin | Result |
|---|---|---|
| TTY | TTY | Full animation + key exit |
| TTY | not TTY | Warning; duration-only |
| not TTY | any | Error (`--help` / `--version` excepted) |

Exit code **0** on normal completion; **1** on parse/validation/TTY/UTF-8/mode errors.

Non-TTY stdout streaming is **out of scope for v1**.

---

## Distribution

- Single file `matrix.cs`, `net10.0`, Native AOT, self-contained, single-file, BCL only, assembly name `matrix`.
- CI builds six RIDs: `linux-x64`, `linux-arm64`, `win-x64`, `win-arm64`, `osx-arm64`, `osx-x64` (`.tar.gz` on Unix, `.zip` on Windows).

---

## Lessons Learned

Decisions and pitfalls discovered during implementation. **Do not revert these without re-reading this section.**

### Vertical motion (column shift)

- **Tried:** random Dim→Empty inside active columns.
- **Result:** scattered flicker, not film-like rain.
- **Decision:** shift each stream down as a unit; clear cells only when they leave the segment or the stream ends. Black belongs **between** columns, not inside trails.

### movie mode layout

- **Tried:** mixed half-width and full-width glyphs in the same logical column.
- **Result:** broken vertical alignment and column collisions.
- **Decision:** even anchor columns, full-width glyphs only, Continuation cell for the second display cell.
- **Pitfall:** emitting a **space** on Continuation cells caused cursor drift and crooked lines — Continuation must write **zero bytes**.
- **Glyph mix:** katakana-only was too plain vs the film. Expanded to full-width digits, five kanji (`日三二一十`), and symbols — still **80% katakana** so the mix reads as “occasional” not “character soup.” Half-width katakana was rejected (would forfeit the two-cell grid or reintroduce width mixing). Standalone `゛` `゜` are not guaranteed two cells in Unicode; kept as a trial.
- **Spawn rate vs density:** fixed per-frame spawn could not match variable stream lifetimes (random trail length, speed, staggered deaths). Baseline spawn uses a height-based estimate with **headroom** at target; each frame, if active streams are **below** `density × 90%`, spawn **increases** to close the gap within ~0.2s. New streams also spawn with **longer trails** when shortfall is high — column count alone does not fix “thin” visuals when each trail is short or faded at the top.

### VS Code integrated terminal — periodic stutter (movie mode)

- **Symptom:** rain looks one row short; the whole screen lags behind, catches up, then repeats about every **2–3 seconds**; **all columns** glitch together. Starts fine for the first few seconds. Windows Terminal is unaffected.
- **Tried:** half-width single-cell movie fallback when True Color is off — treated as a static wide-char layout mismatch. **Did not fix it** (user reverted).
- **Actual cause:** frame-level throughput, not per-column glyph width. At startup most streams share birth time; they die together after roughly `height × 1.55 / fall speed` frames (~2–3s at 14 FPS). Adaptive spawn can recreate **cohorts** that die in sync. As the screen fills, almost every glyph emits its own SGR — frame size spikes. VS Code’s integrated terminal **drops or batches** frames; skipped frames look like vertical skips and global lag, then a catch-up burst.
- **Decision:** decorrelate stream lifetimes (stagger `HeadY` over full terminal height; cap **4 spawns per frame**); shrink and stabilize frames (deduplicate SGR, reset foreground per row, position rows with `CUP` + clear-to-EOL instead of `\n` chains, wrap output in synchronized output `?2026`); debounce resize; adaptive sleep so simulation does not outrun a slow renderer.

### Color model evolution

| Stage | Approach | Outcome |
|---|---|---|
| 1 | Discrete Head / Bright / Dim states | Too coarse |
| 2 | Fade byte = distance from Head, piecewise RGB | Better trails, still not Matrix-like |
| 3 | **256-entry palette LUT** + **wave brightness** + dither + cursor boost | Current model |

Brightness is **wave × trail envelope**, not wave alone. Wave-only brightness left some stream tips vivid when the wave phase was high; the envelope guarantees a dim tail on every column.

### Wave port — regressions that produced all-white output

These are behavioral constraints, easy to get wrong in a top-origin terminal:

1. **`raindropLength` is a small scale factor (~0.75), not trail cell count.** Using trail length as the divisor flattened brightness across each column → everything mapped to the brightest LUT entries.
2. **Terminal row 0 is the top; glyph Y is 0 at the bottom.** Without flipping Y, the wave inverts, cursor detection (`brightness` vs cell below) fires on most cells, and additive white head boost washes the screen to pure white.
3. **Cursor boost is additive and strong.** It must apply **only to the Head** (`dist == 0`). Mapping `--head` into the LUT or boosting raindrop edges washed the trail white. **LUT interpolation through HSL toward white** also produced yellow bands between white and green — keep the LUT on the green ramp; white comes from `--head` on the Head cell only.

### Runtime and build

- File-based `#:` project + top-level statements: avoid file-scoped `const` and extra `Program` types; use a static help class and `Assembly.GetExecutingAssembly()` for `--version`.
- Unix raw input: Linux and macOS need **separate** `termios` layouts (`c_cc` size and flag width differ).
- True Color gate stays **conservative** — 256-color `TERM` ≠ 24-bit.
- Hot path: pre-built ANSI prefixes, `ArrayPool` for grid/buffer, one `Stream.Write` + `Flush` per frame; palette and glyph pools built once at startup.
- Integrated terminals (notably VS Code): deduplicate SGR, row `CUP` + EL, synchronized output `?2026`, spawn cap, and adaptive frame sleep — see **Lessons Learned → VS Code stutter**.
