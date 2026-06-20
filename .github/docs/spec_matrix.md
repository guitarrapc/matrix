# matrix CLI Specification

Status: **Implemented**

## Motivation

`matrix` is a terminal CLI that plays a Matrix-inspired digital rain animation for a configurable duration when launched. It is intended as a lightweight visual effect for demos, shell startup flair, or idle terminal ambience — without excessive CPU or memory use.

The visual model approximates the *Matrix* film digital rain: vertical columns of falling glyphs, a bright leading character, mixed bright and dim green tones in the trail, and frequent empty (black) cells so the screen is never fully filled.

Distribution targets **standalone executables per platform** via **Native AOT** from a **single `.cs` source file**, with no runtime dependency on a shared .NET installation.

---

## Visual Behavior

### Column rain model

The animation uses **vertical columns** of falling characters. Each column maintains an independent **stream** with its own speed, trail length, and activity state.

This model was chosen because it matches the film aesthetic, allows sparse black regions between columns, and keeps resource use bounded by updating per column rather than randomizing the entire screen each frame.

### Vertical stream motion

In the *Matrix* film, glyphs in an active column appear to fall **straight downward along a single vertical line** (fixed column **X**). The user should perceive a **contiguous vertical ribbon** moving down, not scattered characters flickering in place within the column.

Normative behavior:

| Requirement | Meaning |
|---|---|
| **Fixed column X** | Every glyph in a stream occupies the same terminal column. |
| **Downward motion** | The stream moves **down as a unit** each frame, at that column’s speed (1–3 cells per frame). |
| **Vertical contiguity** | While a stream is active on screen, its visible trail is a **single contiguous vertical segment** — no random empty holes inside the segment. |
| **Leading edge** | The **Head** is at the **bottom** of the segment (the front of the fall); older glyphs sit **above** it. |
| **Black space** | Most empty (black) area comes from **inactive columns** and **gaps between streams**, not from punching random holes through an active stream. |

Glyph **mutation** (changing a cell to another character from the pool) may still occur while the stream falls; only **position** must stay vertically aligned.

### Cell display states

Each terminal cell is in exactly one of four states:

| State | Appearance | Role |
|---|---|---|
| **Empty** | Background only, no visible glyph | Inactive column, or stream not present at this row |
| **Dim** | Dark green (configurable) | Upper trail (farther above the Head) |
| **Bright** | Highlight green (configurable) | Lower trail (near the Head) |
| **Head** | White (configurable) | Leading edge at the bottom of the active segment |

Within an active stream, state is determined by **distance from the Head** (fixed bands), not by per-cell random fading:

| Transition | Rule |
|---|---|
| **Head → Bright** | When the stream shifts down, the cell that was Head becomes Bright. |
| **Bright → Dim** | By distance above the Head (older segment of the trail). |
| **Dim → Empty** | When the cell **leaves** the stream (scrolls off the top of the screen or the stream ends) — **not** by random erasure while still inside the trail. |

This matches the film: the trail is a **continuous vertical gradient** behind the Head; irregularity comes from **between-column sparsity** and **glyph mutation**, not from breaking the column into disjoint lit cells.

### Frame rate and terminal presentation

- Target frame rate: **~18 FPS** (within the 15–20 FPS range).
- The animation runs on the **alternate screen buffer** so scrollback history is not polluted.
- The cursor is hidden during playback and restored on exit.
- On exit (timeout or user input), the terminal is restored to its pre-animation state with no trailing message.
- When the terminal is resized during playback, column and row counts are recalculated where the environment allows.

### v1 animation tuning (not exposed on CLI)

These values are fixed in v1; they define the default look and resource profile:

| Parameter | Value |
|---|---|
| Target FPS | 18 |
| Active columns | ~35% of columns at a time |
| Trail length | 8–24 characters per column (random per column) |
| Fall speed | 1–3 cells per frame (random per column); entire stream shifts by this amount |
| Head → Bright | Always on shift (former Head cell) |
| Bright vs Dim | By distance band within trail (not random per frame) |
| Dim → Empty | Only when cell exits stream or column deactivates (not random mid-trail) |
| Glyph mutation | ~8% chance per frame per visible cell in stream |
| New column spawn | ~3% chance per frame among inactive columns |

---

## Character Modes

Three glyph modes are supported:

| Mode | Invocation | Character set |
|---|---|---|
| **ascii-matrix** (default) | No mode flag | Mixed ASCII: `A–Z`, `0–9`, and symbols such as `@#$%^&*()` |
| **single** | `--char X` | Exactly one user-specified character |
| **movie** | `--mode movie` | Fixed pool dominated by katakana, plus Latin letters, digits, and a small set of symbols |

In all modes, individual cells **randomly change to another character from the active pool** at the configured mutation rate, mimicking the film’s shifting glyphs.

### movie mode encoding requirement

**movie** mode requires a **UTF-8 capable terminal**. If UTF-8 output cannot be established at startup, the process exits with an error. There is no fallback to ascii-matrix for movie mode.

The movie character pool includes katakana (清音・濁音・半濁音中心), `A–Z`, `0–9`, and symbols `:・."=*+-<>`.

### Mode conflicts

`--char` and `--mode movie` must not be used together; doing so is a fatal error.

When `--char` is present, **single** mode takes precedence regardless of other mode flags.

---

## Command-Line Interface

### Usage

```text
matrix [duration]
matrix --duration <seconds>
matrix --char <character>
matrix --mode movie
matrix --bg <color> --head <color> --bright <color> --dim <color>
matrix --help
matrix --version
```

### Duration

| Input | Behavior |
|---|---|
| Omitted | **5 seconds** |
| Positive number | Run for that many seconds (positional `[duration]` or `--duration`) |
| `0` or negative | Run until the user ends the session (see Exit behavior) |

Both a trailing positional duration and `--duration` are supported; only one effective duration should be supplied.

### Standard options

| Option | Behavior |
|---|---|
| `--help` | Print usage, modes, duration, color options, and examples (English) |
| `--version` | Print `matrix <version>` (e.g. `matrix 1.0.0`) |

### Messages

All user-facing text (`--help`, errors, warnings) is **English only** in v1.

---

## Color

### Accepted formats

Colors are specified as either:

- **Hex:** `#RGB` or `#RRGGBB` (`#` required; case-insensitive)
- **Named 16-color:** `black`, `darkblue`, `darkgreen`, `darkcyan`, `darkred`, `darkmagenta`, `darkyellow`, `gray`, `darkgray`, `blue`, `green`, `cyan`, `red`, `magenta`, `yellow`, `white`

Hex is the primary representation; named colors are accepted so users can specify palette-friendly values directly and so fallback mode has a stable vocabulary.

### Defaults

| Option | Default |
|---|---|
| `--bg` | `#000000` |
| `--head` | `#FFFFFF` |
| `--bright` | `#00FF41` |
| `--dim` | `#008F11` |

Unspecified color options use these defaults.

### True Color vs 16-color fallback

When the terminal is judged to support **True Color**, hex values are rendered as 24-bit colors. Named colors are mapped to their corresponding RGB values for output.

When True Color is **not** supported:

1. A **warning** is printed to stderr.
2. Rendering falls back to the **16-color palette**.
3. Named colors are used as-is.
4. Hex values are mapped to the **nearest 16-color** equivalent.

The canonical fallback palette for non-True-Color terminals matches the default intent: `black` / `white` / `green` / `darkgreen`.

### True Color detection

True Color support is inferred from environment signals, including:

- `COLORTERM` set to `truecolor` or `24bit`
- `TERM` containing `truecolor` or `24bit`
- `WT_SESSION` (Windows Terminal)
- `TERM_PROGRAM` indicating known True Color terminals (e.g. `vscode`, `Apple_Terminal`, `iTerm.app`)

On Windows, **virtual terminal processing** is enabled at startup in addition to the environment checks.

On Linux and macOS, detection relies on environment signals only; `TERM=xterm-256color` alone does **not** imply True Color (256-color ≠ 24-bit).

Invalid color strings are fatal errors.

---

## Exit Behavior

Playback ends when **either** condition is met first:

1. The configured duration elapses (finite runs).
2. The user presses **any key** (see Input requirements).

On exit, the alternate screen is left, the cursor and terminal state are restored, and the process exits with code **0**. No farewell message is printed.

`Ctrl+C` is handled like a normal exit path: restore the terminal, then exit.

### Input requirements

- Key exit must fire on the **first keypress** without requiring **Enter**.
- Any key (printable, space, arrows, control keys, etc.) ends the session.
- Key echo must not leave typed characters visible on the animation screen.

---

## Terminal and I/O Requirements

### stdout must be a TTY

The animation requires a **TTY on stdout**. If stdout is redirected or piped, the process exits with a non-zero code and an English error message.

`--help` and `--version` work regardless of stdout redirection.

### stdin and key exit

| stdout | stdin | Behavior |
|---|---|---|
| TTY | TTY | Full behavior: animation + key exit + duration |
| TTY | not TTY | **Warning** on stderr; animation runs on **duration only** (key exit disabled) |
| not TTY | any | **Error** (except `--help` / `--version`) |

### Future consideration (v2)

Non-TTY stdout fallback (streaming ANSI text without alternate-screen requirements) is **out of scope for v1** but may be added in v2.

---

## Distribution

### Source and build model

- **Single source file:** `matrix.cs` with file-based project directives (`#:sdk`, `#:property`, …).
- **Target framework:** `net10.0`
- **Native AOT**, **self-contained**, **single-file publish**
- **No external NuGet dependencies** (BCL only)
- Assembly / executable name: **`matrix`**

Framework-dependent deployment is explicitly **not** used; each published binary is intended to run without a preinstalled shared runtime.

### Multi-platform artifacts

Native AOT produces **one executable per runtime identifier (RID)**. A single binary does not run on all operating systems.

v1 release builds target six RIDs via CI matrix (`fail-fast: false`):

| Runner OS | RID | Artifact name |
|---|---|---|
| ubuntu-24.04 | linux-x64 | `matrix-linux-amd64` |
| ubuntu-24.04-arm | linux-arm64 | `matrix-linux-arm64` |
| windows-2025 | win-x64 | `matrix-win-amd64` |
| windows-11-arm | win-arm64 | `matrix-win-arm64` |
| macos-26 | osx-arm64 | `matrix-osx-arm64` |
| macos-26-intel | osx-x64 | `matrix-osx-amd64` |

Archives use `.tar.gz` on Unix-like platforms and `.zip` on Windows.

---

## Exit Codes

| Outcome | Code |
|---|---|
| Normal completion (timeout or key exit) | `0` |
| Parse / validation error, missing TTY, movie mode UTF-8 failure, mode conflict | non-zero (`1` unless specified otherwise) |

---

## Design Rationale Summary

| Decision | Why |
|---|---|
| Vertical stream shift + distance-based 4 states | Film-like straight-line fall; black mostly between columns |
| ascii-matrix default; movie mode optional | Broad terminal compatibility by default; authentic script when UTF-8 is available |
| True Color hex + named colors + 16-color fallback | Rich defaults on modern terminals; graceful degradation elsewhere |
| Alternate screen + restore | Preserves shell scrollback and prompt UX |
| ~18 FPS, fixed tuning in v1 | Smooth enough for the effect; avoids CLI complexity and tuning burden |
| Key exit without Enter | Immediate “press any key to dismiss” UX for demos |
| Native AOT self-contained per RID | Standalone `.exe` / binary with no shared runtime install |
| TTY required on stdout (v1) | Alternate screen, colors, and keyboard handling assume an interactive terminal |
| English-only messages | Conventional for cross-platform OSS CLI tools |

---

## Lessons Learned

- **File-based `#:` project + top-level statements:** a file-scoped `const` or extra `Program` type conflicts with the compiler-generated entry type; keep help text in a separate static class and use `Assembly.GetExecutingAssembly()` for `--version`.
- **Unix `termios` is not portable as a single blittable struct:** Linux (32-byte `c_cc`, 32-bit flags) and macOS (20-byte `c_cc`, 64-bit flags) need separate layouts and `VMIN`/`VTIME` indices.
- **True Color detection stays conservative:** `TERM=xterm-256color` alone must not enable 24-bit output; environment signals (`COLORTERM`, `WT_SESSION`, etc.) remain the gate.
- **Hot-path rendering:** pre-built ANSI byte sequences, `ArrayPool` grids/buffers, and a single `Stream.Write` + `Flush` per frame keep allocations out of the animation loop; palette and glyph pools are initialized once at startup.
- **Vertical motion must be explicit in the spec:** the film reads as straight-line falls because each stream moves down as one contiguous segment. An early design treated **Dim → Empty** as high per-frame probability anywhere in the column; that produced scattered, flickering cells and did **not** look like Matrix rain. **Black belongs mostly between columns**, not inside active trails. The visual spec was revised; animation now **shifts each column down as a unit** and assigns Bright/Dim by distance from the Head.
