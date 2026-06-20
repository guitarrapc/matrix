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

Color is driven by the Matrix rain (GLSL + palette pass), not by simple distance-from-head shading:

- **Brightness** comes from a per-column **wave** (shared pulse, raindrop truncation). It is **not** trail position.
- **Palette:** four CLI colors (`--bg`, `--dim`, `--bright`, `--head`) define a global brightness→color map.
- **Cursor highlight:** the Head and the leading edge of a bright raindrop are emphasized (cursor channel).
- **Dither** on brightness before palette lookup.

True Color terminals use 24-bit ANSI; others fall back to the nearest 16-color name with a stderr warning.

### Presentation

- ~**14 FPS**; alternate screen; hidden cursor; restore terminal on exit (no farewell message).
- Recalculate grid on resize when the environment allows.

### Tuning (defaults)

| Parameter | Value |
|---|---|
| FPS | 14 |
| `--density` | 0.0–1.0, default **0.55** |
| Trail length | height × 0.15 … 0.90 per stream (min 4 cells) |
| Fall speed | 1–2 cells/frame per column |
| Glyph mutation | ~35% per frame per visible stream cell |
| Movie density | effective density × **1.5** (cap 1.0) |

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
matrix --bg <color> --head <color> --bright <color> --dim <color>
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

### Color model evolution

| Stage | Approach | Outcome |
|---|---|---|
| 1 | Discrete Head / Bright / Dim states | Too coarse |
| 2 | Fade byte = distance from Head, piecewise RGB | Better trails, still not Matrix-like |
| 3 | **256-entry palette LUT** + **wave brightness** + dither + cursor boost | Current model |

Brightness is **wave-driven**, not trail-distance-driven. The four CLI colors define the palette; do not replace with ad-hoc per-cell RGB lerps.

### Wave port — regressions that produced all-white output

These are behavioral constraints, easy to get wrong in a top-origin terminal:

1. **`raindropLength` is a small scale factor (~0.75), not trail cell count.** Using trail length as the divisor flattened brightness across each column → everything mapped to the brightest LUT entries.
2. **Terminal row 0 is the top; glyph Y is 0 at the bottom.** Without flipping Y, the wave inverts, cursor detection (`brightness` vs cell below) fires on most cells, and additive white head boost washes the screen to pure white.
3. **Cursor boost is additive and strong.** It must stay sparse (Head + raindrop leading edge only). Broad cursor flags + white `--head` silently negate the green palette.

### Runtime and build

- File-based `#:` project + top-level statements: avoid file-scoped `const` and extra `Program` types; use a static help class and `Assembly.GetExecutingAssembly()` for `--version`.
- Unix raw input: Linux and macOS need **separate** `termios` layouts (`c_cc` size and flag width differ).
- True Color gate stays **conservative** — 256-color `TERM` ≠ 24-bit.
- Hot path: pre-built ANSI prefixes, `ArrayPool` for grid/buffer, one `Stream.Write` + `Flush` per frame; palette and glyph pools built once at startup.
