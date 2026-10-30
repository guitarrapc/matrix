#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property Version=1.2.1
#:property Nullable=enable
#:property ImplicitUsings=enable
#:property PublishAot=true
#:property AssemblyName=matrix
#:property OutputType=Exe
#:property AllowUnsafeBlocks=true

using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

const string VersionString = "1.2.1";

if (TryHandleEarlyExit(args, VersionString))
    return;

var options = CliOptions.Parse(args);
if (options.ShowHelp)
{
    Console.Out.Write(AppText.Help);
    return;
}

if (options.ShowVersion)
{
    Console.Out.Write("matrix ");
    Console.Out.WriteLine(System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? VersionString);
    return;
}

if (options.ErrorMessage is not null)
{
    Console.Error.Write("Error: ");
    Console.Error.WriteLine(options.ErrorMessage);
    Environment.Exit(1);
}

if (Console.IsOutputRedirected)
{
    Console.Error.WriteLine("Error: animation requires a TTY on stdout.");
    Environment.Exit(1);
}

var colorMode = TerminalCapabilities.ResolveColorMode();
if (colorMode == TerminalColorMode.Ansi16)
{
    Console.Error.WriteLine("warning: true color not supported. falling back to 16-color palette");
}
else if (colorMode == TerminalColorMode.None)
{
    Console.Error.WriteLine("warning: ANSI color not supported. falling back to colorless ASCII output");
}

// Movie mode draws the film glyphs with a soft font when the terminal confirms support, or without the check
// when --soft-font says the terminal draws them, and otherwise falls back to full-width katakana with a warning.
// --mode kana skips the check.
var glyphMode = options.Mode;
var density = options.Density;
if (glyphMode == GlyphMode.Movie && options.ProbeSoftFont)
{
    if (TerminalCapabilities.CanUseSoftFont(colorMode, options.ForceSoftFont, out var fallbackReason))
    {
        glyphMode = GlyphMode.MovieSoftFont;
        if (!options.DensityExplicit)
            density = EngineConstants.DefaultDensity;
    }
    else
    {
        Console.Error.Write("warning: ");
        Console.Error.Write(fallbackReason);
        Console.Error.WriteLine(options.ForceSoftFont
            ? ". falling back to katakana glyphs"
            : ". falling back to katakana glyphs (use --soft-font if the terminal draws soft fonts, or --mode kana to skip this check)");
    }
}

var cursorIntensity = options.CursorIntensity;

var keyExitEnabled = !Console.IsInputRedirected;
if (!keyExitEnabled)
{
    Console.Error.WriteLine("warning: stdin is not a tty. key exit disabled, using duration only");
}

// Frames are written as UTF-8 bytes. On Windows the console reads them in its output code page (932, 437, ...
// by default), so switch it to UTF-8 for the run. The original code page comes back with the rest of the terminal state.
var utf8Output = ConsoleCodePage.TrySwitchToUtf8(out var restoreCodePage);

GlyphPool pool;
try
{
    pool = GlyphPool.Create(glyphMode, options.SingleChar, utf8Output);
}
catch (InvalidOperationException ex)
{
    restoreCodePage();
    Console.Error.Write("Error: ");
    Console.Error.WriteLine(ex.Message);
    Environment.Exit(1);
    return;
}

var palette = AnsiPalette.Create(options.Colors, colorMode, cursorIntensity);
using var engine = new MatrixEngine(pool, palette, density, options.Fps);
using var pacer = new FramePacer(options.Fps);

var restore = TerminalSession.Enter(keyExitEnabled, softFont: glyphMode == GlyphMode.MovieSoftFont, restoreCodePage);
var exitRequested = false;

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    exitRequested = true;
};

using var stdout = Console.OpenStandardOutput();
try
{
    var infinite = options.DurationSeconds <= 0;
    var deadline = infinite ? long.MaxValue : Stopwatch.GetTimestamp() + (long)(options.DurationSeconds * Stopwatch.Frequency);

    while (!exitRequested)
    {
        if (keyExitEnabled && TerminalInput.TryConsumeKey())
        {
            exitRequested = true;
            break;
        }

        if (!infinite && Stopwatch.GetTimestamp() >= deadline)
            break;

        engine.Tick();
        engine.Render(stdout);
        pacer.WaitNextFrame();
    }
}
finally
{
    restore();
}

return;

static bool TryHandleEarlyExit(string[] args, string defaultVersion)
{
    for (var i = 0; i < args.Length; i++)
    {
        if (args[i] is "--help" or "-h")
        {
            Console.Out.Write(AppText.Help);
            return true;
        }

        if (args[i] is "--version" or "-V")
        {
            Console.Out.Write("matrix ");
            Console.Out.WriteLine(System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? defaultVersion);
            return true;
        }
    }

    return false;
}

internal enum GlyphMode : byte
{
    AsciiMatrix = 0,
    Single = 1,
    Movie = 2,
    /// <summary>Movie mode drawn with the soft font, chosen at startup, not a CLI value.</summary>
    MovieSoftFont = 3,
}

internal enum CellState : byte
{
    Empty = 0,
    Glyph = 1,
    Continuation = 2,
}

internal static class EngineConstants
{
    internal const int DefaultTargetFps = 14;
    internal const int MinTargetFps = 1;
    internal const int MaxTargetFps = 60;
    internal const int GlyphMutationChance = 10;
    internal const int MinSpeed = 1;
    internal const int MaxSpeed = 2;
    internal const double DefaultDensity = 0.55;
    internal const double DefaultMovieDensity = 0.7;
    internal const double TrailMinHeightFraction = 0.30;
    internal const double TrailMaxHeightFraction = 0.90;
    internal const int MinTrailCells = 10;
    internal const double RainFallSpeed = 0.3;
    internal const double RaindropLength = 0.75;
    internal const double DitherMagnitude = 0.05;
    internal const double DefaultCursorIntensity = 2.5;
    internal const int LutTailFadeEnd = 22;
    internal const int LutDimIndex = 64;
    internal const int LutBrightIndex = 191;
    internal const double LutBrightScale = 1.18;
    /// <summary>&lt; 1 lifts mid-trail vs tip, tip still reaches 0.</summary>
    internal const double TrailEnvelopeGamma = 0.58;
    /// <summary>Protect the first trail cells from the rain wave starting at a dark phase.</summary>
    internal const int NearHeadBrightnessFloorCells = 5;
    internal const float NearHeadBrightnessFloor = 0.72f;
    /// <summary>Rain shimmer can dim a trail, but should not erase it before the envelope fades out.</summary>
    internal const float RainWaveMinimum = 0.42f;
    /// <summary>Two slots let the next stream enter at the top while the previous trail is still leaving at the bottom.</summary>
    internal const int MaxStreamsPerColumn = 2;
    /// <summary>Consecutive ticks with the same size before applying a terminal resize.</summary>
    internal const int ResizeStableFrames = 4;
}

internal readonly struct Rgb(byte r, byte g, byte b)
{
    internal readonly byte R = r;
    internal readonly byte G = g;
    internal readonly byte B = b;
}

internal enum ConsoleColor16 : byte
{
    Black = 0,
    DarkBlue = 1,
    DarkGreen = 2,
    DarkCyan = 3,
    DarkRed = 4,
    DarkMagenta = 5,
    DarkYellow = 6,
    Gray = 7,
    DarkGray = 8,
    Blue = 9,
    Green = 10,
    Cyan = 11,
    Red = 12,
    Magenta = 13,
    Yellow = 14,
    White = 15,
}

internal enum TerminalColorMode
{
    None,
    Ansi16,
    TrueColor,
}

internal readonly struct ColorValue
{
    internal readonly bool IsNamed;
    internal readonly Rgb Rgb;
    internal readonly ConsoleColor16 Named;

    internal ColorValue(Rgb rgb)
    {
        IsNamed = false;
        Rgb = rgb;
        Named = ConsoleColor16.Black;
    }

    internal ColorValue(ConsoleColor16 named, Rgb rgb)
    {
        IsNamed = true;
        Rgb = rgb;
        Named = named;
    }
}

internal enum ColorPattern
{
    Classic,
    Resurrections,
    Operator,
    Twilight,
    Rain,
    Rainbow,
    Fire,
    Cycle,
    Pulse,
    Wave,
}

internal sealed class CliOptions
{
    internal GlyphMode Mode { get; private init; } = GlyphMode.AsciiMatrix;
    /// <summary>True for --mode movie (try the soft font); false for --mode kana (katakana only).</summary>
    internal bool ProbeSoftFont { get; private init; }
    /// <summary>
    /// True for --soft-font: --mode movie uses the soft font without asking the terminal. It is for terminals
    /// whose support the check cannot see, such as a recorder behind a pseudo terminal or one that answers DA1 wrongly.
    /// </summary>
    internal bool ForceSoftFont { get; private init; }
    internal char SingleChar { get; private init; } = '0';
    internal double DurationSeconds { get; private init; } = 5;
    internal bool ShowHelp { get; private init; }
    internal bool ShowVersion { get; private init; }
    internal string? ErrorMessage { get; private init; }
    internal ColorOptions Colors { get; private init; } = ColorOptions.Default;
    internal double Density { get; private init; } = EngineConstants.DefaultDensity;
    /// <summary>True when --density was given; movie mode with the soft font replaces only the default.</summary>
    internal bool DensityExplicit { get; private init; }
    internal double CursorIntensity { get; private init; } = EngineConstants.DefaultCursorIntensity;
    internal int Fps { get; private init; } = EngineConstants.DefaultTargetFps;

    internal static CliOptions Parse(string[] args)
    {
        var mode = GlyphMode.AsciiMatrix;
        var poolMode = GlyphMode.AsciiMatrix;
        var probeSoftFont = false;
        var forceSoftFont = false;
        var modeName = "ascii";
        var hasChar = false;
        var singleChar = '0';
        var duration = 5.0;
        var density = EngineConstants.DefaultDensity;
        var hasDensityFlag = false;
        var hasDurationFlag = false;
        var hasPositionalDuration = false;
        ColorValue? bg = null, head = null, bright = null, dim = null;
        var pattern = ColorPattern.Classic;
        var cursorIntensity = EngineConstants.DefaultCursorIntensity;
        var fps = EngineConstants.DefaultTargetFps;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--help" or "-h":
                    return new CliOptions { ShowHelp = true };
                case "--version" or "-V":
                    return new CliOptions { ShowVersion = true };
                case "--mode":
                    if (++i >= args.Length)
                        return Error("missing value for --mode");
                    modeName = args[i];
                    if (args[i] == "movie")
                    {
                        poolMode = GlyphMode.Movie;
                        probeSoftFont = true;
                    }
                    else if (args[i] == "kana")
                    {
                        poolMode = GlyphMode.Movie;
                        probeSoftFont = false;
                    }
                    else if (args[i] == "ascii")
                    {
                        poolMode = GlyphMode.AsciiMatrix;
                        probeSoftFont = false;
                    }
                    else
                    {
                        return Error("unknown --mode value (expected 'ascii', 'movie', or 'kana')");
                    }
                    break;
                case "--soft-font":
                    forceSoftFont = true;
                    break;
                case "--char":
                    if (++i >= args.Length)
                        return Error("missing value for --char");
                    if (args[i].Length != 1)
                        return Error("--char requires exactly one character");
                    if (!IsDrawableChar(args[i][0]))
                        return Error("--char requires a visible character, not a control, format, or combining character");
                    hasChar = true;
                    singleChar = args[i][0];
                    break;
                case "--duration":
                    if (++i >= args.Length)
                        return Error("missing value for --duration");
                    if (!TryParseDuration(args[i], out duration))
                        return Error("invalid --duration value");
                    hasDurationFlag = true;
                    break;
                case "--bg":
                    if (++i >= args.Length)
                        return Error("missing value for --bg");
                    if (!ColorParser.TryParse(args[i], out var cBg))
                        return Error($"invalid color for --bg: {args[i]}");
                    bg = cBg;
                    break;
                case "--head":
                    if (++i >= args.Length)
                        return Error("missing value for --head");
                    if (!ColorParser.TryParse(args[i], out var cHead))
                        return Error($"invalid color for --head: {args[i]}");
                    head = cHead;
                    break;
                case "--bright":
                    if (++i >= args.Length)
                        return Error("missing value for --bright");
                    if (!ColorParser.TryParse(args[i], out var cBright))
                        return Error($"invalid color for --bright: {args[i]}");
                    bright = cBright;
                    break;
                case "--dim":
                    if (++i >= args.Length)
                        return Error("missing value for --dim");
                    if (!ColorParser.TryParse(args[i], out var cDim))
                        return Error($"invalid color for --dim: {args[i]}");
                    dim = cDim;
                    break;
                case "--pattern":
                    if (++i >= args.Length)
                        return Error("missing value for --pattern");
                    if (!TryParseColorPattern(args[i], out pattern))
                        return Error("invalid --pattern value (expected classic, resurrections, operator, twilight, rain, rainbow, fire, cycle, pulse, or wave)");
                    break;
                case "--density":
                    if (++i >= args.Length)
                        return Error("missing value for --density");
                    if (!TryParseDensity(args[i], out density))
                        return Error("invalid --density value (expected 0.0 to 1.0)");
                    hasDensityFlag = true;
                    break;
                case "--cursor-intensity":
                    if (++i >= args.Length)
                        return Error("missing value for --cursor-intensity");
                    if (!TryParseCursorIntensity(args[i], out cursorIntensity))
                        return Error("invalid --cursor-intensity value (expected 0.5 to 5.0)");
                    break;
                case "--fps":
                    if (++i >= args.Length)
                        return Error("missing value for --fps");
                    if (!TryParseFps(args[i], out fps))
                        return Error($"invalid --fps value (expected {EngineConstants.MinTargetFps} to {EngineConstants.MaxTargetFps})");
                    break;
                default:
                    if (arg.StartsWith('-'))
                        return Error($"unknown option: {arg}");
                    if (!TryParseDuration(arg, out duration))
                        return Error($"invalid duration: {arg}");
                    hasPositionalDuration = true;
                    break;
            }
        }

        if (hasChar && poolMode == GlyphMode.Movie)
            return Error($"--char and --mode {modeName} cannot be used together");

        // Only movie mode has a soft font. Ignoring the flag elsewhere would hide a mistyped mode.
        if (forceSoftFont && (hasChar || poolMode != GlyphMode.Movie || !probeSoftFont))
            return Error("--soft-font requires --mode movie");

        if (hasDurationFlag && hasPositionalDuration)
            return Error("specify duration either as a positional argument or via --duration, not both");

        if (hasChar)
            mode = GlyphMode.Single;
        else
            mode = poolMode;

        if (!hasDensityFlag && mode == GlyphMode.Movie)
            density = EngineConstants.DefaultMovieDensity;

        var colors = ColorOptions.Create(pattern, bg, head, bright, dim);

        return new CliOptions
        {
            Mode = mode,
            SingleChar = singleChar,
            DurationSeconds = duration,
            Density = density,
            DensityExplicit = hasDensityFlag,
            ProbeSoftFont = mode == GlyphMode.Movie && probeSoftFont,
            ForceSoftFont = forceSoftFont,
            CursorIntensity = cursorIntensity,
            Fps = fps,
            Colors = colors,
        };
    }

    private static CliOptions Error(string message) => new() { ErrorMessage = message };

    /// <summary>
    /// False for characters that do not draw as one glyph of their own: controls move the cursor (tab, newline),
    /// format characters are invisible (zero-width space, joiners), combining marks draw on the previous cell,
    /// line and paragraph separators break the row, and a lone surrogate is not a character at all.
    /// Any of them would shift or break the grid.
    /// </summary>
    internal static bool IsDrawableChar(char c) => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) switch
    {
        System.Globalization.UnicodeCategory.Control => false,
        System.Globalization.UnicodeCategory.Format => false,
        System.Globalization.UnicodeCategory.Surrogate => false,
        System.Globalization.UnicodeCategory.NonSpacingMark => false,
        System.Globalization.UnicodeCategory.SpacingCombiningMark => false,
        System.Globalization.UnicodeCategory.EnclosingMark => false,
        System.Globalization.UnicodeCategory.LineSeparator => false,
        System.Globalization.UnicodeCategory.ParagraphSeparator => false,
        _ => true,
    };

    private static bool TryParseDuration(ReadOnlySpan<char> text, out double seconds)
    {
        seconds = 0;
        return double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out seconds);
    }

    private static bool TryParseDensity(ReadOnlySpan<char> text, out double density)
    {
        density = 0;
        if (!double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out density))
            return false;
        return density is >= 0 and <= 1;
    }

    private static bool TryParseCursorIntensity(ReadOnlySpan<char> text, out double intensity)
    {
        intensity = 0;
        if (!double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out intensity))
            return false;
        return intensity is >= 0.5 and <= 5.0;
    }

    private static bool TryParseFps(ReadOnlySpan<char> text, out int fps)
    {
        fps = 0;
        if (!int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out fps))
            return false;
        return fps is >= EngineConstants.MinTargetFps and <= EngineConstants.MaxTargetFps;
    }

    private static bool TryParseColorPattern(ReadOnlySpan<char> text, out ColorPattern pattern)
    {
        if (text.Equals("classic", StringComparison.OrdinalIgnoreCase))
        {
            pattern = ColorPattern.Classic;
            return true;
        }

        if (text.Equals("resurrections", StringComparison.OrdinalIgnoreCase))
        {
            pattern = ColorPattern.Resurrections;
            return true;
        }

        if (text.Equals("operator", StringComparison.OrdinalIgnoreCase))
        {
            pattern = ColorPattern.Operator;
            return true;
        }

        if (text.Equals("twilight", StringComparison.OrdinalIgnoreCase))
        {
            pattern = ColorPattern.Twilight;
            return true;
        }

        if (text.Equals("rain", StringComparison.OrdinalIgnoreCase))
        {
            pattern = ColorPattern.Rain;
            return true;
        }

        if (text.Equals("rainbow", StringComparison.OrdinalIgnoreCase))
        {
            pattern = ColorPattern.Rainbow;
            return true;
        }

        if (text.Equals("fire", StringComparison.OrdinalIgnoreCase))
        {
            pattern = ColorPattern.Fire;
            return true;
        }

        if (text.Equals("cycle", StringComparison.OrdinalIgnoreCase))
        {
            pattern = ColorPattern.Cycle;
            return true;
        }

        if (text.Equals("pulse", StringComparison.OrdinalIgnoreCase))
        {
            pattern = ColorPattern.Pulse;
            return true;
        }

        if (text.Equals("wave", StringComparison.OrdinalIgnoreCase))
        {
            pattern = ColorPattern.Wave;
            return true;
        }

        pattern = default;
        return false;
    }
}

internal readonly struct ColorOptions(
    ColorPattern pattern,
    ColorValue background,
    ColorValue head,
    ColorValue bright,
    ColorValue dim,
    ColorValue? hot = null)
{
    internal static ColorOptions Default => new(
        ColorPattern.Classic,
        DefaultBackground,
        DefaultHead,
        DefaultBright,
        DefaultDim);

    internal static readonly ColorValue DefaultBackground = new(new Rgb(0, 0, 0)); // #000000
    internal static readonly ColorValue DefaultHead = new(new Rgb(255, 255, 255)); // #FFFFFF
    internal static readonly ColorValue DefaultBright = new(new Rgb(48, 255, 88)); // #30FF58
    internal static readonly ColorValue DefaultDim = new(new Rgb(5, 61, 22)); // #053D16

    internal ColorPattern Pattern { get; } = pattern;
    internal ColorValue Background { get; } = background;
    internal ColorValue Head { get; } = head;
    internal ColorValue Bright { get; } = bright;
    internal ColorValue Dim { get; } = dim;
    internal ColorValue? Hot { get; } = hot;

    internal static ColorOptions Create(ColorPattern pattern, ColorValue? bg, ColorValue? head, ColorValue? bright, ColorValue? dim)
    {
        var preset = Preset(pattern);
        // Band patterns take rain colors from their band palettes, so only the background is overridable.
        if (pattern is ColorPattern.Rainbow or ColorPattern.Fire or ColorPattern.Cycle)
        {
            return new ColorOptions(
                pattern,
                bg ?? preset.Background,
                preset.Head,
                preset.Bright,
                preset.Dim,
                preset.Hot);
        }

        return new ColorOptions(
            pattern,
            bg ?? preset.Background,
            head ?? preset.Head,
            bright ?? preset.Bright,
            dim ?? preset.Dim,
            preset.Hot);
    }

    private static ColorOptions Preset(ColorPattern pattern) =>
        pattern switch
        {
            ColorPattern.Classic or ColorPattern.Pulse or ColorPattern.Wave => Default,
            ColorPattern.Resurrections => new ColorOptions(
                pattern,
                new ColorValue(new Rgb(1, 10, 2)),       // #010A02
                new ColorValue(new Rgb(246, 255, 209)),   // #F6FFD1
                new ColorValue(new Rgb(180, 255, 61)),    // #B4FF3D
                new ColorValue(new Rgb(43, 83, 13))),     // #2B530D
            ColorPattern.Operator => new ColorOptions(
                pattern,
                DefaultBackground,
                DefaultHead,
                DefaultBright,
                DefaultBright),
            ColorPattern.Twilight => new ColorOptions(
                pattern,
                new ColorValue(new Rgb(2, 8, 32)),        // #020820
                new ColorValue(new Rgb(255, 250, 96)),    // #FFFA60
                new ColorValue(new Rgb(255, 38, 184)),    // #FF26B8
                new ColorValue(new Rgb(42, 18, 76)),      // #2A124C
                new ColorValue(new Rgb(255, 250, 96))),   // #FFFA60
            ColorPattern.Rain => new ColorOptions(
                pattern,
                new ColorValue(new Rgb(1, 11, 20)),       // #010B14
                new ColorValue(new Rgb(213, 250, 255)),   // #D5FAFF
                new ColorValue(new Rgb(53, 196, 226)),    // #35C4E2
                new ColorValue(new Rgb(7, 45, 68)),       // #072D44
                new ColorValue(new Rgb(188, 246, 255))),  // #BCF6FF
            ColorPattern.Rainbow or ColorPattern.Cycle => new ColorOptions(
                pattern,
                DefaultBackground,
                DefaultHead,
                new ColorValue(new Rgb(255, 0, 0)),
                new ColorValue(new Rgb(80, 0, 0))),
            ColorPattern.Fire => new ColorOptions(
                pattern,
                new ColorValue(new Rgb(10, 2, 0)),        // #0A0200
                new ColorValue(new Rgb(255, 138, 32)),    // #FF8A20
                new ColorValue(new Rgb(255, 106, 0)),     // #FF6A00
                new ColorValue(new Rgb(77, 32, 0))),      // #4D2000
            _ => Default,
        };
}

internal static class ColorParser
{
    internal static bool TryParse(ReadOnlySpan<char> text, out ColorValue color)
    {
        color = default;
        if (text.IsEmpty)
            return false;

        if (text[0] == '#')
            return TryParseHex(text, out color);

        return TryParseNamed(text, out color);
    }

    private static bool TryParseHex(ReadOnlySpan<char> text, out ColorValue color)
    {
        color = default;
        if (text.Length is not (4 or 7))
            return false;

        if (text.Length == 4)
        {
            if (!TryHexNibble(text[1], out var rHi) ||
                !TryHexNibble(text[2], out var gHi) ||
                !TryHexNibble(text[3], out var bHi))
                return false;

            color = new ColorValue(new Rgb((byte)(rHi * 17), (byte)(gHi * 17), (byte)(bHi * 17)));
            return true;
        }

        if (!TryHexByte(text[1], text[2], out var r) ||
            !TryHexByte(text[3], text[4], out var g) ||
            !TryHexByte(text[5], text[6], out var b))
            return false;

        color = new ColorValue(new Rgb(r, g, b));
        return true;
    }

    private static bool TryParseNamed(ReadOnlySpan<char> text, out ColorValue color)
    {
        color = default;
        foreach (var entry in NamedColors)
        {
            if (text.Equals(entry.Name.AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                color = new ColorValue(entry.Named, entry.Rgb);
                return true;
            }
        }

        return false;
    }

    private static bool TryHexNibble(char c, out int value)
    {
        if (c is >= '0' and <= '9')
        {
            value = c - '0';
            return true;
        }

        if (c is >= 'a' and <= 'f')
        {
            value = c - 'a' + 10;
            return true;
        }

        if (c is >= 'A' and <= 'F')
        {
            value = c - 'A' + 10;
            return true;
        }

        value = 0;
        return false;
    }

    private static bool TryHexByte(char hi, char lo, out byte value)
    {
        value = 0;
        if (!TryHexNibble(hi, out var h) || !TryHexNibble(lo, out var l))
            return false;
        value = (byte)((h << 4) | l);
        return true;
    }

    private readonly struct NamedColorEntry(string name, ConsoleColor16 named, Rgb rgb)
    {
        internal string Name { get; } = name;
        internal ConsoleColor16 Named { get; } = named;
        internal Rgb Rgb { get; } = rgb;
    }

    private static readonly NamedColorEntry[] NamedColors =
    [
        new("black", ConsoleColor16.Black, new Rgb(0, 0, 0)),
        new("darkblue", ConsoleColor16.DarkBlue, new Rgb(0, 0, 128)),
        new("darkgreen", ConsoleColor16.DarkGreen, new Rgb(0, 128, 0)),
        new("darkcyan", ConsoleColor16.DarkCyan, new Rgb(0, 128, 128)),
        new("darkred", ConsoleColor16.DarkRed, new Rgb(128, 0, 0)),
        new("darkmagenta", ConsoleColor16.DarkMagenta, new Rgb(128, 0, 128)),
        new("darkyellow", ConsoleColor16.DarkYellow, new Rgb(128, 128, 0)),
        new("gray", ConsoleColor16.Gray, new Rgb(192, 192, 192)),
        new("darkgray", ConsoleColor16.DarkGray, new Rgb(128, 128, 128)),
        new("blue", ConsoleColor16.Blue, new Rgb(0, 0, 255)),
        new("green", ConsoleColor16.Green, new Rgb(0, 255, 0)),
        new("cyan", ConsoleColor16.Cyan, new Rgb(0, 255, 255)),
        new("red", ConsoleColor16.Red, new Rgb(255, 0, 0)),
        new("magenta", ConsoleColor16.Magenta, new Rgb(255, 0, 255)),
        new("yellow", ConsoleColor16.Yellow, new Rgb(255, 255, 0)),
        new("white", ConsoleColor16.White, new Rgb(255, 255, 255)),
    ];

    internal static ConsoleColor16 NearestNamed(Rgb rgb)
    {
        var best = ConsoleColor16.Black;
        var bestDistance = int.MaxValue;
        foreach (var entry in NamedColors)
        {
            var dr = rgb.R - entry.Rgb.R;
            var dg = rgb.G - entry.Rgb.G;
            var db = rgb.B - entry.Rgb.B;
            var distance = dr * dr + dg * dg + db * db;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = entry.Named;
            }
        }

        return best;
    }
}

internal static partial class TerminalCapabilities
{
    internal static TerminalColorMode ResolveColorMode()
    {
        if (!EnableVirtualTerminalIfNeeded())
            return TerminalColorMode.None;

        if (SupportsTrueColor())
            return TerminalColorMode.TrueColor;

        return TerminalColorMode.Ansi16;
    }

    internal static bool EnableVirtualTerminalIfNeeded()
    {
        if (!OperatingSystem.IsWindows())
            return true;

        var handle = GetStdHandle(-11);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
            return false;

        if (!GetConsoleMode(handle, out var mode))
            return false;

        const uint enableVt = 0x0004;
        if ((mode & enableVt) != 0)
            return true;

        return SetConsoleMode(handle, mode | enableVt);
    }

    internal static bool SupportsTrueColor()
    {
        if (HasTrueColorEnv(Environment.GetEnvironmentVariable("COLORTERM")))
            return true;

        var term = Environment.GetEnvironmentVariable("TERM");
        if (term is not null &&
            (term.Contains("truecolor", StringComparison.OrdinalIgnoreCase) ||
             term.Contains("24bit", StringComparison.OrdinalIgnoreCase)))
            return true;

        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WT_SESSION")))
            return true;

        var termProgram = Environment.GetEnvironmentVariable("TERM_PROGRAM");
        if (termProgram is "vscode" or "Apple_Terminal" or "iTerm.app")
            return true;

        return false;
    }

    /// <summary>
    /// True only when the terminal confirms soft font (DECDLD) support, or when <paramref name="skipCheck"/>
    /// (--soft-font) says it draws them. Otherwise returns false with the reason: no VT output, stdin not a tty,
    /// a ConPTY host other than Windows Terminal, or no DA1 reply. The soft font is sent as VT sequences, so
    /// VT output is needed either way.
    /// </summary>
    internal static bool CanUseSoftFont(TerminalColorMode colorMode, bool skipCheck, out string reason)
    {
        reason = string.Empty;
        if (colorMode == TerminalColorMode.None)
        {
            reason = "VT output is not available";
            return false;
        }

        if (skipCheck)
            return true;

        if (Console.IsInputRedirected)
        {
            reason = "stdin is not a tty, so soft font (DECDLD) support cannot be checked";
            return false;
        }

        if (IsBehindOtherPseudoConsoleHost())
        {
            reason = "the Windows console host answers terminal queries for this terminal, so soft font (DECDLD) support cannot be checked";
            return false;
        }

        if (IsWslOutsideWindowsTerminal())
        {
            reason = "WSL outside Windows Terminal may run behind a Windows console host that answers terminal queries, so soft font (DECDLD) support cannot be checked";
            return false;
        }

        switch (QuerySoftFontSupport())
        {
            case true:
                return true;
            case false:
                reason = "terminal does not report soft font (DECDLD) support";
                return false;
            default:
                reason = "terminal did not answer the device attributes query";
                return false;
        }
    }

    /// <summary>
    /// Asks the terminal for its attributes (DA1, <c>CSI c</c>) and reports whether attribute 7
    /// (soft character sets, DECDLD) is listed. Returns null when the terminal does not answer in time.
    /// </summary>
    internal static bool? QuerySoftFontSupport(int timeoutMs = 1000)
    {
        Span<char> response = stackalloc char[128];
        var length = 0;
        Action restore;
        if (OperatingSystem.IsWindows())
        {
            if (!TryEnableWindowsVtInput(out restore))
                return null;
        }
        else if (!NativeTermios.TryEnableRawMode(out restore))
        {
            return null;
        }

        try
        {
            Console.Out.Write("\u001b[c");
            Console.Out.Flush();

            Span<byte> bytes = stackalloc byte[64];
            var deadline = Stopwatch.GetTimestamp() + timeoutMs * Stopwatch.Frequency / 1000;
            while (Stopwatch.GetTimestamp() < deadline && length < response.Length)
            {
                var gotInput = false;
                if (OperatingSystem.IsWindows())
                {
                    while (Console.KeyAvailable && length < response.Length)
                    {
                        response[length++] = Console.ReadKey(intercept: true).KeyChar;
                        gotInput = true;
                    }
                }
                else if (NativeTermios.TryReadInput(bytes, out var read))
                {
                    for (var i = 0; i < read && length < response.Length; i++)
                        response[length++] = (char)bytes[i];
                    gotInput = true;
                }

                if (gotInput && TryParseDeviceAttributes(response[..length], out var softFont))
                    return softFont;
                if (!gotInput)
                    Thread.Sleep(5);
            }

            return null;
        }
        finally
        {
            restore();
        }
    }

    /// <summary>
    /// Parses a DA1 reply such as <c>ESC [ ? 61 ; 6 ; 7 ; 22 c</c>. Attribute 7 means soft character sets;
    /// attributes are only defined for the VT200-and-later classes (first parameter 61-69).
    /// </summary>
    internal static bool TryParseDeviceAttributes(ReadOnlySpan<char> text, out bool softFont)
    {
        softFont = false;
        var start = text.IndexOf("\u001b[?".AsSpan());
        if (start < 0)
            return false;

        var body = text[(start + 3)..];
        var end = body.IndexOf('c');
        if (end < 0)
            return false;

        body = body[..end];
        var index = 0;
        var isVt200Class = false;
        foreach (var range in body.Split(';'))
        {
            if (!int.TryParse(body[range], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value))
                return false;
            if (index == 0)
                isVt200Class = value is >= 61 and <= 69;
            else if (isVt200Class && value == 7)
                softFont = true;
            index++;
        }

        return true;
    }

    /// <summary>
    /// True when a terminal other than Windows Terminal hosts this process through ConPTY (VS Code and
    /// other xterm.js hosts, for example). conhost sits in between and answers DA1 with its own attributes,
    /// which list soft fonts even when the hosting terminal cannot draw them. Under ConPTY the console
    /// window is a hidden pseudo window; a classic conhost window is visible and draws soft fonts itself.
    /// </summary>
    internal static bool IsBehindOtherPseudoConsoleHost()
    {
        if (!OperatingSystem.IsWindows())
            return false;
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WT_SESSION")))
            return false;

        var window = GetConsoleWindow();
        return window == IntPtr.Zero || !IsWindowVisible(window);
    }

    /// <summary>
    /// True for a WSL process started from Windows (wsl.exe sets WSL_DISTRO_NAME) outside Windows Terminal.
    /// wsl.exe runs behind a Windows console host, which answers DA1 with its own attributes, so soft fonts are
    /// listed even when the hosting terminal cannot draw them (the same trap as <see cref="IsBehindOtherPseudoConsoleHost"/>).
    /// Windows Terminal adds WT_SESSION to WSLENV, so WSL inside it still sees WT_SESSION and stays trusted.
    /// The console window cannot be checked from Linux, so a classic console window, which does draw soft fonts, gets katakana too.
    /// </summary>
    internal static bool IsWslOutsideWindowsTerminal() =>
        OperatingSystem.IsLinux() &&
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WSL_DISTRO_NAME")) &&
        string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WT_SESSION"));

    private static bool TryEnableWindowsVtInput(out Action restore)
    {
        restore = static () => { };
        var handle = GetStdHandle(-10);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1) || !GetConsoleMode(handle, out var original))
            return false;

        // VT input delivers the terminal's reply as input; line input and echo would hold it back or print it.
        const uint enableEchoInput = 0x0004;
        const uint enableLineInput = 0x0002;
        const uint enableVtInput = 0x0200;
        var mode = (original | enableVtInput) & ~(enableEchoInput | enableLineInput);
        if (!SetConsoleMode(handle, mode))
            return false;

        restore = () => SetConsoleMode(handle, original);
        return true;
    }

    private static bool HasTrueColorEnv(string? colorTerm)
    {
        if (string.IsNullOrEmpty(colorTerm))
            return false;
        return colorTerm.Equals("truecolor", StringComparison.OrdinalIgnoreCase) ||
               colorTerm.Equals("24bit", StringComparison.OrdinalIgnoreCase);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr GetStdHandle(int nStdHandle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetConsoleWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(IntPtr hWnd);
}

/// <summary>
/// Paces frames against fixed scheduled times so wait errors do not add up across frames.
/// On Windows, Thread.Sleep rounds up to the 15.6 ms system tick (30 FPS became about 21 FPS),
/// so waits use a high-resolution waitable timer. Other platforms already sleep precisely.
/// </summary>
internal sealed partial class FramePacer : IDisposable
{
    private const uint CreateWaitableTimerHighResolution = 0x00000002;
    private const uint TimerAllAccess = 0x001F0003;
    private const uint Infinite = 0xFFFFFFFF;

    private readonly long _frameTicks;
    private readonly IntPtr _timer;
    private long _nextFrame;

    internal FramePacer(int fps)
    {
        _frameTicks = Stopwatch.Frequency / fps;
        _nextFrame = Stopwatch.GetTimestamp() + _frameTicks;

        // Requires Windows 10 1803 or later; on failure the handle is zero and waits fall back to Thread.Sleep.
        if (OperatingSystem.IsWindows())
            _timer = CreateWaitableTimerExW(IntPtr.Zero, IntPtr.Zero, CreateWaitableTimerHighResolution, TimerAllAccess);
    }

    internal void WaitNextFrame()
    {
        var remaining = _nextFrame - Stopwatch.GetTimestamp();
        if (remaining > 0)
            Wait(remaining);

        // A frame that runs late is caught up on the next one. After falling more than a whole frame
        // behind, restart the schedule instead of rendering a burst of catch-up frames.
        _nextFrame = remaining < -_frameTicks
            ? Stopwatch.GetTimestamp() + _frameTicks
            : _nextFrame + _frameTicks;
    }

    private void Wait(long stopwatchTicks)
    {
        if (_timer != IntPtr.Zero)
        {
            // Negative due time is relative, in 100 ns units.
            var dueTime = -(long)(stopwatchTicks * (10_000_000.0 / Stopwatch.Frequency));
            if (dueTime < 0 && SetWaitableTimer(_timer, ref dueTime, 0, IntPtr.Zero, IntPtr.Zero, false))
            {
                WaitForSingleObject(_timer, Infinite);
                return;
            }
        }

        Thread.Sleep((int)(stopwatchTicks * 1000 / Stopwatch.Frequency));
    }

    public void Dispose()
    {
        if (_timer != IntPtr.Zero)
            CloseHandle(_timer);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr CreateWaitableTimerExW(IntPtr lpTimerAttributes, IntPtr lpTimerName, uint dwFlags, uint dwDesiredAccess);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWaitableTimer(IntPtr hTimer, ref long lpDueTime, int lPeriod, IntPtr pfnCompletionRoutine, IntPtr lpArgToCompletionRoutine, [MarshalAs(UnmanagedType.Bool)] bool fResume);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr hObject);
}

internal static class RainBrightness
{
    private const double Sqrt2 = 1.4142135623730951;
    private const double Sqrt5 = 2.23606797749979;

    // Rain time drops by this much for each row down.
    private const double RowStep = 0.01 / EngineConstants.RaindropLength;
    private static readonly double Sin2Step = Math.Sin(Sqrt2 * RowStep);
    private static readonly double Cos2Step = Math.Cos(Sqrt2 * RowStep);
    private static readonly double Sin5Step = Math.Sin(Sqrt5 * RowStep);
    private static readonly double Cos5Step = Math.Cos(Sqrt5 * RowStep);

    /// <summary>
    /// Rain wave brightness for the rows of one stream, top to bottom. Rain time falls by a fixed step per row,
    /// so the two wobble sines advance by rotation: one SinCos pair per stream instead of two Math.Sin per cell.
    /// </summary>
    internal struct Stepper
    {
        private double _rainTime;
        private double _sin2, _cos2, _sin5, _cos5;

        internal Stepper(int firstRowY, int gridHeight, double simTime, in ColumnState column)
        {
            // Reference shader: glyphPos.y = 0 at bottom; terminal row 0 is top.
            var glyphY = gridHeight - 1 - firstRowY;
            var columnTime = column.TimeOffset + simTime * EngineConstants.RainFallSpeed * column.SpeedOffset;
            _rainTime = (glyphY * 0.01 + columnTime) / EngineConstants.RaindropLength;
            (_sin2, _cos2) = Math.SinCos(Sqrt2 * _rainTime);
            (_sin5, _cos5) = Math.SinCos(Sqrt5 * _rainTime);
        }

        /// <summary>Brightness for the current row, then moves one row down.</summary>
        internal float Next()
        {
            // wobble(t) = t + 0.3 sin(sqrt(2) t) + 0.2 sin(sqrt(5) t)
            var wobbled = _rainTime + 0.3 * _sin2 + 0.2 * _sin5;
            _rainTime -= RowStep;
            // sin(a - d) = sin a cos d - cos a sin d, cos(a - d) = cos a cos d + sin a sin d
            (_sin2, _cos2) = (_sin2 * Cos2Step - _cos2 * Sin2Step, _cos2 * Cos2Step + _sin2 * Sin2Step);
            (_sin5, _cos5) = (_sin5 * Cos5Step - _cos5 * Sin5Step, _cos5 * Cos5Step + _sin5 * Sin5Step);
            return (float)(1.0 - Frac(wobbled));
        }
    }

    internal static float ColumnTimeOffset(int columnX)
    {
        var n = Math.Sin(columnX * 12.9898) * 43758.5453;
        return (float)(n - Math.Floor(n)) * 1000f;
    }

    internal static float SpeedOffsetFromColumnSpeed(byte speed) =>
        0.5f + (speed - EngineConstants.MinSpeed) / (float)(EngineConstants.MaxSpeed - EngineConstants.MinSpeed) * 0.5f;

    private static double Frac(double x) => x - Math.Floor(x);
}

internal sealed class BrightnessPalette
{
    private readonly Rgb[] _lut = new Rgb[256];
    private readonly Rgb _head;

    internal BrightnessPalette(Rgb head, Rgb bright, Rgb dim, Rgb bg, Rgb? hot = null)
    {
        _head = head;
        BuildLut(_lut, bright, dim, bg, hot);
    }

    internal ReadOnlySpan<Rgb> Lut => _lut;
    internal Rgb HeadColor => _head;

    internal Rgb Sample(byte brightnessIndex) => _lut[brightnessIndex];

    // Nearest 16-color name of each LUT entry, built only for 16-color output.
    private ConsoleColor16[]? _namedLut;

    /// <summary>Precomputes the nearest 16-color name of every LUT entry, so 16-color frames skip the per-cell search.</summary>
    internal void BuildNamedLut()
    {
        _namedLut = new ConsoleColor16[_lut.Length];
        for (var i = 0; i < _lut.Length; i++)
            _namedLut[i] = ColorParser.NearestNamed(_lut[i]);
    }

    internal ConsoleColor16 SampleNamed(byte brightnessIndex) => _namedLut![brightnessIndex];

    // Room for the longest true color foreground sequence, "\e[38;2;255;255;255m" (19 bytes).
    private const int FgSequenceStride = 20;
    // Pre-encoded true color foreground sequence of each LUT entry, built only for true color output.
    private byte[]? _fgSequences;
    private byte[]? _fgSequenceLengths;

    /// <summary>Encodes every LUT entry's foreground sequence once, so true color frames copy instead of formatting digits.</summary>
    internal void BuildFgSequences()
    {
        _fgSequences = new byte[_lut.Length * FgSequenceStride];
        _fgSequenceLengths = new byte[_lut.Length];
        for (var i = 0; i < _lut.Length; i++)
        {
            AnsiPalette.WriteTrueColorSequence(_fgSequences.AsSpan(i * FgSequenceStride, FgSequenceStride), isBackground: false, _lut[i], out var length);
            _fgSequenceLengths[i] = (byte)length;
        }
    }

    internal ReadOnlySpan<byte> FgSequence(byte brightnessIndex) =>
        _fgSequences.AsSpan(brightnessIndex * FgSequenceStride, _fgSequenceLengths![brightnessIndex]);

    private static void BuildLut(Span<Rgb> lut, Rgb bright, Rgb dim, Rgb bg, Rgb? hot)
    {
        var vividBright = ScaleRgb(bright, EngineConstants.LutBrightScale);
        var hotBright = hot ?? HotBright(vividBright);
        Span<(int index, Rgb rgb)> keyframes = stackalloc (int, Rgb)[4];
        keyframes[0] = (EngineConstants.LutTailFadeEnd, dim);
        keyframes[1] = (EngineConstants.LutDimIndex, dim);
        keyframes[2] = (EngineConstants.LutBrightIndex, vividBright);
        keyframes[3] = (255, hotBright);

        for (var i = 0; i < 256; i++)
        {
            if (i <= EngineConstants.LutTailFadeEnd)
            {
                var t = i / (double)EngineConstants.LutTailFadeEnd;
                lut[i] = LerpHuePreserve(bg, dim, t);
                continue;
            }

            lut[i] = SampleKeyframes(keyframes, i);
        }
    }

    private static Rgb HotBright(Rgb bright) =>
        new(
            (byte)Math.Min(255, bright.R + 45),
            (byte)Math.Min(255, bright.G + 65),
            (byte)Math.Min(255, bright.B + 25));

    private static Rgb SampleKeyframes(ReadOnlySpan<(int index, Rgb rgb)> keyframes, int i)
    {
        for (var s = 0; s < keyframes.Length - 1; s++)
        {
            if (i <= keyframes[s + 1].index)
            {
                var span = keyframes[s + 1].index - keyframes[s].index;
                var t = span > 0 ? (i - keyframes[s].index) / (double)span : 0;
                return LerpHuePreserve(keyframes[s].rgb, keyframes[s + 1].rgb, t);
            }
        }

        return keyframes[^1].rgb;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Rgb LerpHuePreserve(Rgb from, Rgb to, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return new Rgb(
            (byte)(from.R + (to.R - from.R) * t),
            (byte)(from.G + (to.G - from.G) * t),
            (byte)(from.B + (to.B - from.B) * t));
    }

    internal static Rgb AddClamped(Rgb baseColor, Rgb addend) =>
        new(
            (byte)Math.Min(255, baseColor.R + addend.R),
            (byte)Math.Min(255, baseColor.G + addend.G),
            (byte)Math.Min(255, baseColor.B + addend.B));

    internal static Rgb ScaleRgb(Rgb color, double factor) =>
        new(
            (byte)Math.Min(255, color.R * factor),
            (byte)Math.Min(255, color.G * factor),
            (byte)Math.Min(255, color.B * factor));
}

internal sealed class AnsiPalette
{
    private readonly byte[] _syncStart = "\u001b[?2026h"u8.ToArray();
    private readonly byte[] _syncEnd = "\u001b[?2026l"u8.ToArray();
    private readonly byte[] _home = "\u001b[H"u8.ToArray();
    private readonly byte[] _clearEol = "\u001b[K"u8.ToArray();
    private readonly byte[] _bgRowPrefix;
    private readonly byte[] _rowFgReset;
    private readonly BrightnessPalette _palette;
    private readonly BrightnessPalette[]? _bandPalettes;
    private readonly TerminalColorMode _colorMode;
    private readonly Rgb _cursorAddend;
    private readonly Rgb[]? _bandCursorAddends;
    private readonly BandLayout _bandLayout;
    // Brightness multiplier per frame for one heartbeat, or null when the pattern does not pulse.
    private readonly double[]? _pulseCurve;
    // Most bytes one cell can write: a foreground sequence plus a glyph. Blanks and wide-glyph pairs write less.
    private readonly int _maxCellBytes;

    // "\e[" + up to 10 digits + ";1H": the cursor move written before every row after the first.
    private const int MaxCupBytes = 15;
    // A char is at most 3 bytes in UTF-8.
    private const int MaxGlyphBytes = 3;

    // One heartbeat every 20 frames, about 1.4 seconds (a slow ~42 bpm) at the default 14 fps.
    private const int PulseFrames = 20;
    // Brightness between beats. Low enough that the beat reads clearly, high enough that rain stays visible.
    private const double PulseRest = 0.55;

    // Whether a sine-shaped band of light sweeps across the rain (wave).
    private readonly bool _wave;
    // Wave crest row per column for the frame being written, sized to the widest grid seen.
    private double[] _waveCrests = [];

    // The crest moves one wavelength to the right every 42 frames, about 3 seconds at the default 14 fps.
    private const int WaveFramesPerWavelength = 42;
    // Two crests fit across the screen, but never shorter than this, so narrow terminals keep a gentle slope.
    private const double WaveMinWavelength = 24;
    // Crest height above or below the screen middle, as a fraction of screen height.
    private const double WaveAmplitude = 0.25;
    // Light band half-thickness as a fraction of screen height, at least WaveMinThickness rows.
    private const double WaveThickness = 0.08;
    private const double WaveMinThickness = 1.5;
    // Off the crest the rain dims to WaveRest. On the crest it is pushed past normal brightness to WavePeak,
    // so the faint trail cells the band passes through light up too.
    private const double WaveRest = 0.5;
    private const double WavePeak = 1.3;
    // Squared crest distance past which the crest term is below half an ulp of WaveRest:
    // 0.8 * exp(-38) is about 2.5e-17, half an ulp of 0.5 is about 5.6e-17. Recheck if WaveRest or WavePeak change.
    private const double WaveExpCutoff = 38;

    // A cycle band moves one column-band to the right every this many frames.
    // 48 bands x 2 frames is one full hue loop in about 7 seconds at the default 14 fps.
    private const int CycleBandCount = 48;
    private const int CycleFramesPerBand = 2;

    private enum BandLayout
    {
        /// <summary>Bands split the screen into columns (rainbow).</summary>
        Columns,
        /// <summary>Bands split the screen into rows (fire).</summary>
        Rows,
        /// <summary>Column bands that shift right as frames advance (cycle).</summary>
        ScrollingColumns,
    }

    private AnsiPalette(
        byte[] bgRowPrefix,
        byte[] rowFgReset,
        BrightnessPalette palette,
        BrightnessPalette[]? bandPalettes,
        BandLayout bandLayout,
        double[]? pulseCurve,
        bool wave,
        TerminalColorMode colorMode,
        Rgb cursorAddend,
        Rgb[]? bandCursorAddends)
    {
        _bgRowPrefix = bgRowPrefix;
        _rowFgReset = rowFgReset;
        _palette = palette;
        _bandPalettes = bandPalettes;
        _bandLayout = bandLayout;
        _pulseCurve = pulseCurve;
        _wave = wave;
        _colorMode = colorMode;
        _cursorAddend = cursorAddend;
        _bandCursorAddends = bandCursorAddends;

        // Measure the longest foreground sequence with the same writers WriteFrame uses:
        // "\e[38;2;255;255;255m" for true color, "\e[97m" for 16 colors (every 16-color code has two digits).
        Span<byte> scratch = stackalloc byte[32];
        var maxFgBytes = 0;
        if (colorMode == TerminalColorMode.TrueColor)
            WriteTrueColorSequence(scratch, isBackground: false, new Rgb(255, 255, 255), out maxFgBytes);
        else if (colorMode == TerminalColorMode.Ansi16)
            WriteAnsi16Sequence(scratch, Ansi16FgCode(ConsoleColor16.White), out maxFgBytes);
        _maxCellBytes = maxFgBytes + MaxGlyphBytes;
    }

    /// <summary>Upper bound on the bytes <see cref="WriteFrame"/> writes for a width x height grid.</summary>
    internal int MaxFrameBytes(int width, int height)
    {
        var rowBytes = MaxCupBytes + _bgRowPrefix.Length + _rowFgReset.Length + _clearEol.Length + width * _maxCellBytes;
        return _syncStart.Length + _home.Length + _syncEnd.Length + height * rowBytes;
    }

    internal static AnsiPalette Create(ColorOptions colors, TerminalColorMode colorMode, double cursorIntensity)
    {
        var palette = new BrightnessPalette(
            ResolveRgb(colors.Head),
            ResolveRgb(colors.Bright),
            ResolveRgb(colors.Dim),
            ResolveRgb(colors.Background),
            colors.Hot is { } hot ? ResolveRgb(hot) : null);

        var bgRgb = ResolveRgb(colors.Background);
        var bgNamed = ResolveNamed(colors.Background);
        var bgPrefix = colorMode switch
        {
            TerminalColorMode.TrueColor => BuildTrueColorBgPrefix(bgRgb),
            TerminalColorMode.Ansi16 => BuildAnsi16BgPrefix(bgNamed),
            _ => [],
        };
        var rowFgReset = colorMode switch
        {
            TerminalColorMode.TrueColor => BuildTrueColorFgPrefix(bgRgb),
            TerminalColorMode.Ansi16 => BuildAnsi16FgPrefix(bgNamed),
            _ => [],
        };

        var bandPalettes = colors.Pattern switch
        {
            ColorPattern.Rainbow => CreateRainbowPalettes(bgRgb),
            ColorPattern.Fire => CreateFirePalettes(bgRgb),
            ColorPattern.Cycle => CreateCyclePalettes(bgRgb),
            _ => null,
        };
        Rgb[]? bandCursorAddends = null;
        if (bandPalettes is not null)
        {
            bandCursorAddends = new Rgb[bandPalettes.Length];
            for (var i = 0; i < bandPalettes.Length; i++)
                bandCursorAddends[i] = BrightnessPalette.ScaleRgb(bandPalettes[i].HeadColor, cursorIntensity);
        }

        // Per-entry output tables for the color mode, so frames skip per-cell color encoding.
        BuildOutputTables(palette, colorMode);
        if (bandPalettes is not null)
        {
            foreach (var bandPalette in bandPalettes)
                BuildOutputTables(bandPalette, colorMode);
        }

        var bandLayout = colors.Pattern switch
        {
            ColorPattern.Fire => BandLayout.Rows,
            ColorPattern.Cycle => BandLayout.ScrollingColumns,
            _ => BandLayout.Columns,
        };
        var pulseCurve = colors.Pattern == ColorPattern.Pulse ? CreatePulseCurve() : null;
        var cursorAddend = BrightnessPalette.ScaleRgb(palette.HeadColor, cursorIntensity);
        var wave = colors.Pattern == ColorPattern.Wave;
        return new AnsiPalette(bgPrefix, rowFgReset, palette, bandPalettes, bandLayout, pulseCurve, wave, colorMode, cursorAddend, bandCursorAddends);
    }

    /// <summary>
    /// Fills the crest row of each column for this frame: a sine curve that travels right.
    /// The crest depends only on the column, so Math.Sin runs once per column instead of once per cell.
    /// Returns the light band half-thickness.
    /// </summary>
    private double PrepareWaveCrests(int width, int height, ulong frameNumber)
    {
        if (_waveCrests.Length < width)
            _waveCrests = new double[width];

        var wavelength = Math.Max(WaveMinWavelength, width / 2.0);
        var travel = (frameNumber % WaveFramesPerWavelength) / (double)WaveFramesPerWavelength;
        for (var x = 0; x < width; x++)
        {
            // Subtracting travel moves the crest toward larger x as frames advance.
            var phase = 2 * Math.PI * (x / wavelength - travel);
            _waveCrests[x] = (height - 1) * 0.5 + height * WaveAmplitude * Math.Sin(phase);
        }

        return Math.Max(WaveMinThickness, height * WaveThickness);
    }

    /// <summary>Brightness multiplier at a cell for the wave pattern: high near the crest, low elsewhere.</summary>
    private static double WaveGain(int y, double crest, double thickness)
    {
        var d = (y - crest) / thickness;
        var d2 = d * d;
        // Past this distance (WavePeak - WaveRest) * exp(-d2) is under half an ulp of WaveRest,
        // so adding it changes no bit and Math.Exp can be skipped.
        return d2 > WaveExpCutoff ? WaveRest : WaveRest + (WavePeak - WaveRest) * Math.Exp(-d2);
    }

    /// <summary>
    /// A "lub-dub" heartbeat: a strong beat, a weaker second beat shortly after, then a long rest.
    /// </summary>
    private static double[] CreatePulseCurve()
    {
        var curve = new double[PulseFrames];
        for (var i = 0; i < PulseFrames; i++)
        {
            var t = i / (double)PulseFrames;
            var beat = Math.Max(Bump(t, 0.10), 0.6 * Bump(t, 0.30));
            curve[i] = PulseRest + (1 - PulseRest) * beat;
        }

        return curve;

        // Wide enough that each beat spans several frames even at low fps.
        static double Bump(double t, double center)
        {
            const double Width = 0.06;
            var d = (t - center) / Width;
            return Math.Exp(-d * d);
        }
    }

    /// <summary>
    /// Evenly spaced hues around the color wheel. One full loop spans the screen width,
    /// so every hue is on screen at once while the bands scroll.
    /// </summary>
    private static BrightnessPalette[] CreateCyclePalettes(Rgb bg)
    {
        var palettes = new BrightnessPalette[CycleBandCount];
        for (var i = 0; i < CycleBandCount; i++)
        {
            var bright = HueToRgb(i / (double)CycleBandCount);
            palettes[i] = new BrightnessPalette(
                new Rgb(255, 255, 255),
                bright,
                BrightnessPalette.ScaleRgb(bright, 0.24),
                bg);
        }

        return palettes;

        // HSV with full value and slightly reduced saturation, so blue stays bright enough to read.
        static Rgb HueToRgb(double hue)
        {
            const double Saturation = 0.85;
            var h = hue * 6;
            var f = h - Math.Floor(h);
            var low = 1 - Saturation;
            var falling = 1 - Saturation * f;
            var rising = 1 - Saturation * (1 - f);
            var (r, g, b) = (int)h switch
            {
                0 => (1.0, rising, low),
                1 => (falling, 1.0, low),
                2 => (low, 1.0, rising),
                3 => (low, falling, 1.0),
                4 => (rising, low, 1.0),
                _ => (1.0, low, falling),
            };
            return new Rgb((byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255));
        }
    }

    /// <summary>
    /// Row bands from deep red at the top to yellow-white at the bottom, so rain heats up as it falls.
    /// Many thin bands keep the gradient smooth without rebuilding palettes when the terminal resizes.
    /// </summary>
    private static BrightnessPalette[] CreateFirePalettes(Rgb bg)
    {
        const int BandCount = 32;
        ReadOnlySpan<(double t, Rgb head, Rgb bright, Rgb hot)> stops =
        [
            (0.00, new Rgb(160, 24, 0), new Rgb(122, 8, 0), new Rgb(192, 32, 0)),       // #7A0800 embers
            (0.35, new Rgb(255, 58, 0), new Rgb(216, 30, 0), new Rgb(255, 90, 16)),     // #D81E00 red flame
            (0.65, new Rgb(255, 138, 32), new Rgb(255, 106, 0), new Rgb(255, 168, 48)), // #FF6A00 orange flame
            (1.00, new Rgb(255, 232, 160), new Rgb(255, 192, 32), new Rgb(255, 242, 176)), // #FFC020 flame core
        ];

        var palettes = new BrightnessPalette[BandCount];
        for (var i = 0; i < BandCount; i++)
        {
            var t = i / (double)(BandCount - 1);
            var s = 0;
            while (s < stops.Length - 2 && t > stops[s + 1].t)
                s++;
            var (t0, head0, bright0, hot0) = stops[s];
            var (t1, head1, bright1, hot1) = stops[s + 1];
            var u = (t - t0) / (t1 - t0);
            var bright = Lerp(bright0, bright1, u);
            palettes[i] = new BrightnessPalette(
                Lerp(head0, head1, u),
                bright,
                BrightnessPalette.ScaleRgb(bright, 0.3),
                bg,
                Lerp(hot0, hot1, u));
        }

        return palettes;

        static Rgb Lerp(Rgb a, Rgb b, double u) =>
            new(
                (byte)Math.Round(a.R + (b.R - a.R) * u),
                (byte)Math.Round(a.G + (b.G - a.G) * u),
                (byte)Math.Round(a.B + (b.B - a.B) * u));
    }

    private static BrightnessPalette[] CreateRainbowPalettes(Rgb bg)
    {
        ReadOnlySpan<Rgb> brightColors =
        [
            new Rgb(255, 36, 36),
            new Rgb(255, 136, 0),
            new Rgb(255, 235, 0),
            new Rgb(38, 222, 68),
            new Rgb(35, 144, 255),
            new Rgb(70, 72, 255),
            new Rgb(168, 70, 255),
        ];

        var palettes = new BrightnessPalette[brightColors.Length];
        for (var i = 0; i < brightColors.Length; i++)
        {
            var bright = brightColors[i];
            palettes[i] = new BrightnessPalette(
                new Rgb(255, 255, 255),
                bright,
                BrightnessPalette.ScaleRgb(bright, 0.24),
                bg);
        }

        return palettes;
    }

    private static void BuildOutputTables(BrightnessPalette palette, TerminalColorMode colorMode)
    {
        if (colorMode == TerminalColorMode.TrueColor)
            palette.BuildFgSequences();
        else if (colorMode == TerminalColorMode.Ansi16)
            palette.BuildNamedLut();
    }

    private static Rgb ResolveRgb(ColorValue color) => color.Rgb;

    private static ConsoleColor16 ResolveNamed(ColorValue color)
    {
        if (color.IsNamed)
            return color.Named;
        return ColorParser.NearestNamed(color.Rgb);
    }

    private static byte[] BuildTrueColorBgPrefix(Rgb rgb) => BuildTrueColorSequence(isBackground: true, rgb);

    private static byte[] BuildTrueColorFgPrefix(Rgb rgb) => BuildTrueColorSequence(isBackground: false, rgb);

    private static byte[] BuildAnsi16FgPrefix(ConsoleColor16 color)
    {
        var code = Ansi16FgCode(color);
        return BuildAnsi16Sequence(code);
    }

    private static byte[] BuildTrueColorSequence(bool isBackground, Rgb rgb)
    {
        Span<byte> scratch = stackalloc byte[64];
        WriteTrueColorSequence(scratch, isBackground, rgb, out var length);
        return scratch[..length].ToArray();
    }

    internal static void WriteTrueColorSequence(Span<byte> scratch, bool isBackground, Rgb rgb, out int length)
    {
        scratch[0] = 0x1B;
        scratch[1] = (byte)'[';
        scratch[2] = (byte)(isBackground ? '4' : '3');
        scratch[3] = (byte)'8';
        scratch[4] = (byte)';';
        scratch[5] = (byte)'2';
        scratch[6] = (byte)';';
        var pos = 7;
        pos += AppendDecimal(scratch[pos..], rgb.R);
        scratch[pos++] = (byte)';';
        pos += AppendDecimal(scratch[pos..], rgb.G);
        scratch[pos++] = (byte)';';
        pos += AppendDecimal(scratch[pos..], rgb.B);
        scratch[pos++] = (byte)'m';
        length = pos;
    }

    private static byte[] BuildAnsi16BgPrefix(ConsoleColor16 color)
    {
        var code = Ansi16BgCode(color);
        return BuildAnsi16Sequence(code);
    }

    private static byte[] BuildAnsi16Sequence(int code)
    {
        Span<byte> scratch = stackalloc byte[16];
        WriteAnsi16Sequence(scratch, code, out var length);
        return scratch[..length].ToArray();
    }

    private static void WriteAnsi16Sequence(Span<byte> scratch, int code, out int length)
    {
        scratch[0] = 0x1B;
        scratch[1] = (byte)'[';
        var pos = 2;
        pos += AppendDecimal(scratch[pos..], code);
        scratch[pos++] = (byte)'m';
        length = pos;
    }

    private static int AppendDecimal(Span<byte> destination, int value)
    {
        // Color components (0-255) and most row numbers take a direct path, written once per lit cell.
        if ((uint)value < 10)
        {
            destination[0] = (byte)('0' + value);
            return 1;
        }

        if ((uint)value < 100)
        {
            destination[1] = (byte)('0' + value % 10);
            destination[0] = (byte)('0' + value / 10);
            return 2;
        }

        if ((uint)value < 1000)
        {
            destination[2] = (byte)('0' + value % 10);
            value /= 10;
            destination[1] = (byte)('0' + value % 10);
            destination[0] = (byte)('0' + value / 10);
            return 3;
        }

        Span<byte> digits = stackalloc byte[10];
        var count = 0;
        while (value > 0)
        {
            digits[count++] = (byte)('0' + value % 10);
            value /= 10;
        }

        for (var i = count - 1; i >= 0; i--)
            destination[count - 1 - i] = digits[i];

        return count;
    }

    /// <summary>U+3000 IDEOGRAPHIC SPACE: blank, two display cells wide.</summary>
    private static ReadOnlySpan<byte> IdeographicSpace => "　"u8;

    internal void WriteFrame(Stream stdout, ReadOnlySpan<Cell> grid, int width, int height, bool wideColumns, byte[] buffer, ulong frameNumber)
    {
        var pos = 0;
        AppendBytes(buffer, ref pos, _syncStart);
        AppendBytes(buffer, ref pos, _home);

        // Pulse dims both the rain and the head bloom together, so heads do not stay lit between beats.
        var pulse = _pulseCurve is null ? 1.0 : _pulseCurve[(int)(frameNumber % (ulong)_pulseCurve.Length)];
        var frameCursorAddend = _pulseCurve is null ? _cursorAddend : BrightnessPalette.ScaleRgb(_cursorAddend, pulse);
        var waveThickness = _wave ? PrepareWaveCrests(width, height, frameNumber) : 0;

        for (var y = 0; y < height; y++)
        {
            if (y > 0)
                AppendCupRow1(buffer, ref pos, y + 1);

            AppendBytes(buffer, ref pos, _bgRowPrefix);
            AppendBytes(buffer, ref pos, _rowFgReset);

            var row = y * width;
            // Foreground last written in this row: packed RGB for true color, the 16-color name for 16 colors, -1 for none.
            var lastFg = -1;

            for (var x = 0; x < width; x++)
            {
                ref readonly var cell = ref grid[row + x];
                if (cell.State == (byte)CellState.Continuation)
                    continue;

                if (cell.State == (byte)CellState.Empty)
                {
                    // Movie glyphs come from a fallback font. ASCII spaces between them split each row into
                    // many font runs, and terminals such as Windows Terminal re-run font fallback and shaping
                    // per run. A full-width ideographic space keeps the whole row in one run.
                    if (wideColumns && (x & 1) == 0 && x + 1 < width && grid[row + x + 1].State == (byte)CellState.Empty)
                    {
                        AppendBytes(buffer, ref pos, IdeographicSpace);
                        x++;
                        continue;
                    }

                    // Written directly: a one-byte span copy costs more than the write itself.
                    buffer[pos++] = (byte)' ';
                    continue;
                }

                var brightness = cell.Brightness / 255.0;
                brightness -= Dither(x, y, frameNumber) * (EngineConstants.DitherMagnitude / 3.0);
                var gain = _wave ? pulse * WaveGain(y, _waveCrests[x], waveThickness) : pulse;
                brightness = Math.Clamp(brightness * gain, 0, 1);
                var palette = SelectPalette(x, y, width, height, frameNumber, out var band);
                var lutIndex = (byte)(brightness * 255);
                var rgb = palette.Sample(lutIndex);
                if (cell.CursorBoost != 0)
                {
                    var cursorAddend = SelectCursorAddend(band, frameCursorAddend);
                    // Heads off the crest dim with the rain. On the crest they keep their normal bloom.
                    if (_wave)
                        cursorAddend = BrightnessPalette.ScaleRgb(cursorAddend, Math.Min(1, gain));
                    rgb = BrightnessPalette.AddClamped(rgb, cursorAddend);
                }

                if (_colorMode == TerminalColorMode.TrueColor)
                {
                    var fg = (rgb.R << 16) | (rgb.G << 8) | rgb.B;
                    if (fg != lastFg)
                    {
                        // Other cells copy the LUT entry's pre-encoded sequence. Heads add the cursor bloom, so theirs is encoded here.
                        if (cell.CursorBoost == 0)
                        {
                            AppendBytes(buffer, ref pos, palette.FgSequence(lutIndex));
                        }
                        else
                        {
                            WriteTrueColorSequence(buffer.AsSpan(pos), isBackground: false, rgb, out var fgLen);
                            pos += fgLen;
                        }

                        lastFg = fg;
                    }
                }
                else if (_colorMode == TerminalColorMode.Ansi16)
                {
                    // Many RGB values map to one 16-color name, so dedupe on the name. Only heads add the
                    // cursor bloom on top of the LUT color, so only they need the live nearest-color search.
                    var named = cell.CursorBoost != 0 ? ColorParser.NearestNamed(rgb) : palette.SampleNamed(lutIndex);
                    if ((int)named != lastFg)
                    {
                        WriteAnsi16Sequence(buffer.AsSpan(pos), Ansi16FgCode(named), out var fgLen);
                        pos += fgLen;
                        lastFg = (int)named;
                    }
                }

                AppendCharUtf8(buffer, ref pos, cell.Glyph);
            }

            AppendBytes(buffer, ref pos, _clearEol);
        }

        AppendBytes(buffer, ref pos, _syncEnd);
        stdout.Write(buffer.AsSpan(0, pos));
        stdout.Flush();
    }

    private static void AppendCupRow1(byte[] buffer, ref int pos, int row)
    {
        buffer[pos++] = 0x1B;
        buffer[pos++] = (byte)'[';
        pos += AppendDecimal(buffer.AsSpan(pos), row);
        buffer[pos++] = (byte)';';
        buffer[pos++] = (byte)'1';
        buffer[pos++] = (byte)'H';
    }

    /// <summary>White noise in [0, 1) per cell and frame, from an integer hash instead of a Math.Sin per cell.</summary>
    private static float Dither(int x, int y, ulong frame)
    {
        var h = (uint)x * 0x9E3779B1u ^ (uint)y * 0x85EBCA77u ^ (uint)frame * 0xC2B2AE3Du;
        h ^= h >> 16;
        h *= 0x7FEB352Du;
        h ^= h >> 15;
        h *= 0x846CA68Bu;
        h ^= h >> 16;
        return (h >> 8) * (1f / (1 << 24));
    }

    private static int Ansi16FgCode(ConsoleColor16 color) =>
        Ansi16IsBright(color) ? 90 + Ansi16BaseIndex(color) : 30 + Ansi16BaseIndex(color);

    private static int Ansi16BgCode(ConsoleColor16 color) =>
        Ansi16IsBright(color) ? 100 + Ansi16BaseIndex(color) : 40 + Ansi16BaseIndex(color);

    private static bool Ansi16IsBright(ConsoleColor16 color) => color >= ConsoleColor16.DarkGray;

    private static int Ansi16BaseIndex(ConsoleColor16 color) => color switch
    {
        ConsoleColor16.Black or ConsoleColor16.DarkGray => 0,
        ConsoleColor16.DarkRed or ConsoleColor16.Red => 1,
        ConsoleColor16.DarkGreen or ConsoleColor16.Green => 2,
        ConsoleColor16.DarkYellow or ConsoleColor16.Yellow => 3,
        ConsoleColor16.DarkBlue or ConsoleColor16.Blue => 4,
        ConsoleColor16.DarkMagenta or ConsoleColor16.Magenta => 5,
        ConsoleColor16.DarkCyan or ConsoleColor16.Cyan => 6,
        ConsoleColor16.Gray or ConsoleColor16.White => 7,
        _ => 7,
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private BrightnessPalette SelectPalette(int x, int y, int width, int height, ulong frameNumber, out int band)
    {
        if (_bandPalettes is null)
        {
            band = -1;
            return _palette;
        }

        var count = _bandPalettes.Length;
        var (position, extent) = _bandLayout == BandLayout.Rows ? (y, height) : (x, width);
        band = Math.Min(count - 1, position * count / Math.Max(1, extent));
        if (_bandLayout == BandLayout.ScrollingColumns)
        {
            // Subtract the shift so each color moves right over time.
            var shift = (int)(frameNumber / CycleFramesPerBand % (ulong)count);
            band = (band - shift + count) % count;
        }

        return _bandPalettes[band];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Rgb SelectCursorAddend(int band, Rgb frameCursorAddend) =>
        band >= 0 && _bandCursorAddends is not null
            ? _bandCursorAddends[band]
            : frameCursorAddend;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AppendBytes(byte[] buffer, ref int pos, ReadOnlySpan<byte> bytes)
    {
        bytes.CopyTo(buffer.AsSpan(pos));
        pos += bytes.Length;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AppendCharUtf8(byte[] buffer, ref int pos, char glyph)
    {
        if (glyph <= 0x7F)
        {
            buffer[pos++] = (byte)glyph;
            return;
        }

        var rune = new Rune(glyph);
        rune.TryEncodeToUtf8(buffer.AsSpan(pos), out var written);
        pos += written;
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct Cell
{
    internal byte State;
    internal byte Brightness;
    internal byte CursorBoost;
    internal char Glyph;
}

internal sealed class GlyphPool
{
    private readonly string _chars;
    private readonly char _singleChar;
    private readonly bool _single;
    private readonly GlyphMode _mode;
    internal readonly bool RequiresWideColumns;

    private GlyphPool(string chars, char singleChar, bool single, GlyphMode mode, bool requiresWideColumns)
    {
        _chars = chars;
        _singleChar = singleChar;
        _single = single;
        _mode = mode;
        RequiresWideColumns = requiresWideColumns;
    }

    /// <param name="utf8Output">Whether the terminal reads output as UTF-8, required for any non-ASCII glyph.</param>
    internal static GlyphPool Create(GlyphMode mode, char singleChar, bool utf8Output)
    {
        switch (mode)
        {
            case GlyphMode.Single:
                if (singleChar > 0x7F && !utf8Output)
                    throw new InvalidOperationException("--char with a non-ASCII character requires a UTF-8 capable terminal");
                // A full-width character takes two display cells, so it uses the katakana layout.
                return new GlyphPool(string.Empty, singleChar, true, GlyphMode.Single, requiresWideColumns: IsWide(singleChar));
            case GlyphMode.Movie:
                if (!utf8Output)
                    throw new InvalidOperationException("movie mode requires a UTF-8 capable terminal");
                return new GlyphPool(MovieKatakanaChars, '\0', false, GlyphMode.Movie, requiresWideColumns: true);
            case GlyphMode.MovieSoftFont:
                // Plain ASCII codes, the terminal draws them with the soft font loaded by MatrixSoftFont.
                return new GlyphPool(MatrixSoftFont.Codes, '\0', false, GlyphMode.MovieSoftFont, requiresWideColumns: false);
            default:
                return new GlyphPool(AsciiMatrixChars, '\0', false, GlyphMode.AsciiMatrix, requiresWideColumns: false);
        }
    }

    /// <summary>
    /// True for East Asian Wide and Fullwidth characters, which terminals draw two cells wide:
    /// Hangul Jamo, CJK symbols and punctuation, kana, CJK ideographs, Yi, Hangul syllables, and fullwidth forms.
    /// Ambiguous-width characters such as Greek letters count as narrow, as Windows Terminal and most terminals draw them.
    /// </summary>
    internal static bool IsWide(char c) => c switch
    {
        >= 'ᄀ' and <= 'ᅟ' => true,
        >= '⺀' and <= '〾' => true,
        >= 'ぁ' and <= '㏿' => true,
        >= '㐀' and <= '䶿' => true,
        >= '一' and <= '鿿' => true,
        >= 'ꀀ' and <= '꓏' => true,
        >= '가' and <= '힣' => true,
        >= '豈' and <= '﫿' => true,
        >= '︰' and <= '﹏' => true,
        >= '＀' and <= '｠' => true,
        >= '￠' and <= '￦' => true,
        _ => false,
    };

    internal char Pick(Random rng)
    {
        if (_single)
            return _singleChar;
        if (_mode == GlyphMode.Movie)
            return PickMovie(rng);
        return _chars[rng.Next(_chars.Length)];
    }

    private static char PickMovie(Random rng)
    {
        var roll = rng.Next(1000);
        if (roll < 800)
            return MovieKatakanaChars[rng.Next(MovieKatakanaChars.Length)];
        if (roll < 970)
            return MovieMarkChars[rng.Next(MovieMarkChars.Length)];
        return MovieSymbolChars[rng.Next(MovieSymbolChars.Length)];
    }

    private const string AsciiMatrixChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789@#$%^&*()";
    // Movie mode: full-width (two display cells) only — mixed widths break vertical alignment.
    // Every movie glyph must stay within U+3000-U+30FF. Font fallback in terminals such as Windows Terminal
    // starts a new run at each switch to another Unicode range (full-width digits, kanji, full-width symbols),
    // and each run costs a full fallback lookup, so mixed rows stutter on wide screens.
    private const string MovieKatakanaChars =
        "アイウエオカキクケコサシスセソタチツテトナニヌネノハヒフヘホマミムメモヤユヨラリルレロワヲン" +
        "ガギグゲゴザジズゼゾダヂヅデドバビブベボパピプペポヴヰヱ";
    private const string MovieMarkChars = "〇ー々〆ヶヵヿヽヾ〃";
    private const string MovieSymbolChars = "・゠〈〉゛゜〓";
}

internal sealed class MatrixEngine : IDisposable
{
    private readonly GlyphPool _pool;
    private readonly AnsiPalette _palette;
    private readonly bool _wideColumns;
    private readonly double _effectiveDensity;
    private readonly Random _rng = new();

    private Cell[] _grid = [];
    /// <summary>Last frame's grid: cells that stay lit carry their glyph over from it.</summary>
    private Cell[] _previousGrid = [];
    private ColumnState[] _columns = [];
    /// <summary>Per column: frames until the next stream enters at the top.</summary>
    private int[] _nextSpawnFrames = [];
    /// <summary>Trail envelope per trail length, indexed by distance from the Head. Filled on resize.</summary>
    private float[]?[] _envelopes = [];
    private byte[] _renderBuffer = [];
    private int _width;
    private int _height;
    private double _simTime;
    private ulong _frameNumber;
    private int _pendingResizeWidth;
    private int _pendingResizeHeight;
    private int _pendingResizeStable;
    private readonly int _targetFps;

    internal MatrixEngine(GlyphPool pool, AnsiPalette palette, double density, int targetFps)
    {
        _pool = pool;
        _palette = palette;
        _targetFps = targetFps;
        _wideColumns = pool.RequiresWideColumns;

        // Density is the fraction of stream columns showing rain at any moment (see NextGapFrames).
        // It counts columns that can hold a stream, so wide (katakana) glyphs need no extra scaling.
        _effectiveDensity = Math.Clamp(density, 0, 1);
        Resize();
    }

    internal void Tick()
    {
        var width = Console.WindowWidth;
        var height = Console.WindowHeight;
        if (width < 1)
            width = 1;
        if (height < 1)
            height = 1;

        if (width != _width || height != _height)
        {
            if (width == _pendingResizeWidth && height == _pendingResizeHeight)
                _pendingResizeStable++;
            else
            {
                _pendingResizeWidth = width;
                _pendingResizeHeight = height;
                _pendingResizeStable = 1;
            }

            if (_pendingResizeStable >= EngineConstants.ResizeStableFrames)
                Resize(width, height);
        }

        // Every frame redraws all streams onto an empty grid, keeping the last frame for glyph carry-over.
        // One contiguous clear is cheaper than clearing column by column.
        (_grid, _previousGrid) = (_previousGrid, _grid);
        Array.Clear(_grid, 0, _width * _height);
        for (var x = 0; x < _width; x++)
        {
            if (_wideColumns && (x & 1) == 1)
                continue;
            UpdateColumn(x);
        }

        _simTime += 1.0 / _targetFps;
        _frameNumber++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsStreamColumn(int x) =>
        !_wideColumns || ((x & 1) == 0 && x + 1 < _width);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int StreamIndex(int x, int slot) =>
        x * EngineConstants.MaxStreamsPerColumn + slot;

    internal void Render(Stream stdout)
    {
        var needed = _palette.MaxFrameBytes(_width, _height);
        if (_renderBuffer.Length < needed)
        {
            if (_renderBuffer.Length > 0)
                ArrayPool<byte>.Shared.Return(_renderBuffer);
            _renderBuffer = ArrayPool<byte>.Shared.Rent(needed);
        }

        _palette.WriteFrame(stdout, _grid, _width, _height, _wideColumns, _renderBuffer, _frameNumber);
    }

    private void Resize(int? widthOverride = null, int? heightOverride = null)
    {
        var newWidth = widthOverride ?? Math.Max(1, Console.WindowWidth);
        var newHeight = heightOverride ?? Math.Max(1, Console.WindowHeight);

        if (_grid.Length > 0)
        {
            ArrayPool<Cell>.Shared.Return(_grid);
            ArrayPool<Cell>.Shared.Return(_previousGrid);
            ArrayPool<ColumnState>.Shared.Return(_columns);
            ArrayPool<int>.Shared.Return(_nextSpawnFrames);
        }

        _width = newWidth;
        _height = newHeight;
        _pendingResizeWidth = newWidth;
        _pendingResizeHeight = newHeight;
        _pendingResizeStable = EngineConstants.ResizeStableFrames;
        _grid = ArrayPool<Cell>.Shared.Rent(_width * _height);
        _previousGrid = ArrayPool<Cell>.Shared.Rent(_width * _height);
        _columns = ArrayPool<ColumnState>.Shared.Rent(_width * EngineConstants.MaxStreamsPerColumn);
        _nextSpawnFrames = ArrayPool<int>.Shared.Rent(_width);
        Array.Clear(_grid, 0, _width * _height);
        Array.Clear(_previousGrid, 0, _width * _height);

        // Rain falls in from the top: each column's first stream enters within the first screen height of frames.
        // A column starts in a gap with probability (1 - density), so the visible fraction is near the target from the start.
        var (minTrail, maxTrail) = TrailLengthRange();
        EnsureEnvelopes(minTrail, maxTrail);
        var avgTrail = (minTrail + maxTrail) / 2;
        var typicalVisibleFrames = (StreamVisibleFrames(avgTrail, EngineConstants.MinSpeed) + StreamVisibleFrames(avgTrail, EngineConstants.MaxSpeed)) / 2;
        for (var x = 0; x < _width; x++)
        {
            for (var slot = 0; slot < EngineConstants.MaxStreamsPerColumn; slot++)
                _columns[StreamIndex(x, slot)] = default;
            _nextSpawnFrames[x] = int.MaxValue;
            if (!IsStreamColumn(x) || _effectiveDensity <= 0)
                continue;
            var entry = _rng.Next(1, _height + 1);
            _nextSpawnFrames[x] = _rng.NextDouble() < _effectiveDensity
                ? entry
                : entry + Math.Max(0, NextGapFrames(typicalVisibleFrames));
        }
    }

    /// <summary>Frames from the head entering row 0 until the trail tip leaves the bottom row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int StreamVisibleFrames(int trailLength, int speed) =>
        (_height + trailLength - 2) / speed + 1;

    /// <summary>
    /// Frames between one stream leaving a column and the next entering it.
    /// Density is the fraction of time a column shows a stream, so the mean gap is visible × (1 - density) / density;
    /// gaps are uniform in [0, 2 × mean] so columns drift apart instead of moving in lockstep.
    /// At density 1 the next stream enters one frame early so the column is never empty.
    /// </summary>
    private int NextGapFrames(int visibleFrames)
    {
        if (_effectiveDensity >= 1)
            return -1;
        var meanGap = visibleFrames * (1 - _effectiveDensity) / _effectiveDensity;
        return (int)Math.Min(int.MaxValue / 4, Math.Round(_rng.NextDouble() * 2 * meanGap));
    }

    private (int Min, int Max) TrailLengthRange()
    {
        var minTrail = (int)Math.Round(Math.Max(EngineConstants.MinTrailCells, _height * EngineConstants.TrailMinHeightFraction));
        var maxTrail = Math.Min(_height, (int)Math.Round(Math.Max(minTrail, _height * EngineConstants.TrailMaxHeightFraction)));
        return (Math.Min(minTrail, maxTrail), maxTrail);
    }

    private void UpdateColumn(int x)
    {
        if (IsStreamColumn(x) && --_nextSpawnFrames[x] <= 0)
        {
            var freeSlot = -1;
            for (var slot = 0; slot < EngineConstants.MaxStreamsPerColumn; slot++)
            {
                if (!_columns[StreamIndex(x, slot)].Active)
                {
                    freeSlot = slot;
                    break;
                }
            }

            // Both slots busy: try again next frame.
            if (freeSlot >= 0)
                ActivateColumn(x, freeSlot);
            else
                _nextSpawnFrames[x] = 0;
        }

        for (var slot = 0; slot < EngineConstants.MaxStreamsPerColumn; slot++)
            UpdateStream(x, slot);
    }

    private void UpdateStream(int x, int slot)
    {
        ref var column = ref _columns[StreamIndex(x, slot)];
        if (!column.Active)
            return;

        column.HeadY += column.Speed;

        var trailTop = column.HeadY - column.TrailLength + 1;
        if (trailTop > _height - 1)
        {
            column.Active = false;
            return;
        }

        var headY = column.HeadY;
        var updateStart = Math.Max(0, trailTop);
        var updateEnd = Math.Min(headY, _height - 1);
        var rain = new RainBrightness.Stepper(updateStart, _height, _simTime, column);
        var envelopes = _envelopes[column.TrailLength]!;
        for (var y = updateStart; y <= updateEnd; y++)
        {
            var dist = headY - y;
            var glyph = NextGlyph(y * _width + x);
            var wave = rain.Next();
            wave = MathF.Max(wave, NearHeadWaveFloor(dist));
            var envelope = envelopes[dist];
            wave = EngineConstants.RainWaveMinimum + wave * (1f - EngineConstants.RainWaveMinimum);
            var brightness = wave * envelope;
            var cursorBoost = (byte)(dist == 0 ? 1 : 0);
            var brightnessByte = (byte)Math.Clamp((int)(brightness * 255), 0, 255);
            SetDisplayGlyph(x, y, glyph, brightnessByte, cursorBoost);
        }
    }

    /// <summary>
    /// A cell that stays lit keeps its glyph and changes with <see cref="EngineConstants.GlyphMutationChance"/>.
    /// A cell that was dark last frame gets a fresh glyph.
    /// </summary>
    private char NextGlyph(int index)
    {
        // The column's other stream already drew this cell this frame: share its glyph.
        ref readonly var current = ref _grid[index];
        if (current.State == (byte)CellState.Glyph)
            return current.Glyph;

        ref readonly var previous = ref _previousGrid[index];
        if (previous.State == (byte)CellState.Glyph && _rng.Next(100) >= EngineConstants.GlyphMutationChance)
            return previous.Glyph;

        return _pool.Pick(_rng);
    }

    /// <summary>
    /// Builds the envelope table of every trail length a stream can get at this height, so streams read
    /// <see cref="TrailEnvelope"/> from a table instead of calling MathF.Pow per cell. A table depends only on its
    /// length, so tables from earlier sizes are kept and reused.
    /// </summary>
    private void EnsureEnvelopes(int minTrail, int maxTrail)
    {
        if (_envelopes.Length <= maxTrail)
            Array.Resize(ref _envelopes, maxTrail + 1);

        for (var length = minTrail; length <= maxTrail; length++)
        {
            if (_envelopes[length] is not null)
                continue;

            var table = new float[length];
            for (var distance = 0; distance < length; distance++)
                table[distance] = TrailEnvelope(distance, length);
            _envelopes[length] = table;
        }
    }

    /// <summary>1 at Head, 0 at trail tip. Gamma &lt; 1 keeps mid-trail brighter while the tip still fades out.</summary>
    private static float TrailEnvelope(int distanceFromHead, int trailLength)
    {
        if (trailLength <= 1)
            return 1f;
        var linear = (float)(trailLength - 1 - distanceFromHead) / (trailLength - 1);
        return MathF.Pow(linear, (float)EngineConstants.TrailEnvelopeGamma);
    }

    private static float NearHeadWaveFloor(int distanceFromHead)
    {
        if (distanceFromHead <= 0)
            return 1f;
        if (distanceFromHead >= EngineConstants.NearHeadBrightnessFloorCells)
            return 0f;

        var t = distanceFromHead / (float)EngineConstants.NearHeadBrightnessFloorCells;
        return EngineConstants.NearHeadBrightnessFloor * (1f - t * t);
    }

    private void SetDisplayGlyph(int x, int y, char glyph, byte brightness, byte cursorBoost)
    {
        ref var cell = ref _grid[y * _width + x];
        if (cell.State == (byte)CellState.Glyph && brightness < cell.Brightness && cursorBoost <= cell.CursorBoost)
            return;

        cell.State = (byte)CellState.Glyph;
        cell.Glyph = glyph;
        cell.Brightness = brightness;
        cell.CursorBoost = Math.Max(cell.CursorBoost, cursorBoost);
        if (_wideColumns && x + 1 < _width)
        {
            ref var cont = ref _grid[y * _width + x + 1];
            cont.State = (byte)CellState.Continuation;
            cont.Glyph = '\0';
            cont.Brightness = 0;
            cont.CursorBoost = 0;
        }
    }

    /// <summary>Start a stream whose head enters row 0 on this frame, and schedule the column's next stream.</summary>
    private void ActivateColumn(int x, int slot)
    {
        var (minTrail, maxTrail) = TrailLengthRange();
        var speed = (byte)_rng.Next(EngineConstants.MinSpeed, EngineConstants.MaxSpeed + 1);
        var trailLength = _rng.Next(minTrail, maxTrail + 1);

        _columns[StreamIndex(x, slot)] = new ColumnState
        {
            Active = true,
            // UpdateStream advances the head before drawing, so it lands on row 0 this frame.
            HeadY = -speed,
            Speed = speed,
            TrailLength = trailLength,
            TimeOffset = RainBrightness.ColumnTimeOffset(x),
            SpeedOffset = RainBrightness.SpeedOffsetFromColumnSpeed(speed),
        };

        var visibleFrames = StreamVisibleFrames(trailLength, speed);
        _nextSpawnFrames[x] = visibleFrames + NextGapFrames(visibleFrames);
    }

    public void Dispose()
    {
        if (_grid.Length > 0)
        {
            ArrayPool<Cell>.Shared.Return(_grid);
            _grid = [];
        }

        if (_previousGrid.Length > 0)
        {
            ArrayPool<Cell>.Shared.Return(_previousGrid);
            _previousGrid = [];
        }

        if (_columns.Length > 0)
        {
            ArrayPool<ColumnState>.Shared.Return(_columns);
            _columns = [];
        }

        if (_nextSpawnFrames.Length > 0)
        {
            ArrayPool<int>.Shared.Return(_nextSpawnFrames);
            _nextSpawnFrames = [];
        }

        if (_renderBuffer.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(_renderBuffer);
            _renderBuffer = [];
        }
    }
}

internal struct ColumnState
{
    internal bool Active;
    internal int HeadY;
    internal byte Speed;
    internal int TrailLength;
    internal float TimeOffset;
    internal float SpeedOffset;
}

internal static class TerminalSession
{
    internal static Action Enter(bool rawInput, bool softFont, Action restoreCodePage)
    {
        Console.Out.Write("\u001b[?1049h\u001b[?25l");
        if (softFont)
            Console.Out.Write(MatrixSoftFont.LoadSequence);
        Console.Out.Flush();

        Action? disableRaw = null;
        if (rawInput)
            disableRaw = TerminalInput.EnableRawMode();

        return () =>
        {
            disableRaw?.Invoke();
            if (softFont)
                Console.Out.Write(MatrixSoftFont.UnloadSequence);
            Console.Out.Write("\u001b[?25h\u001b[?1049l");
            Console.Out.Flush();
            // Last, so every byte above is still read as UTF-8.
            restoreCodePage();
        };
    }
}

/// <summary>
/// Frames are written as UTF-8 bytes, but a Windows console reads output in its code page, which defaults
/// to the OEM code page (932 on Japanese Windows, 437 on US English). There a non-ASCII glyph such as λ shows
/// as two garbage characters and shifts the row. The code page is set on the console, not the process,
/// so it stays changed after exit unless it is put back.
/// </summary>
internal static partial class ConsoleCodePage
{
    private const uint Utf8 = 65001;

    /// <summary>
    /// Switches the console output code page to UTF-8. Returns false when output is not read as UTF-8.
    /// <paramref name="restore"/> puts the original code page back. Other platforms read UTF-8 already.
    /// </summary>
    internal static bool TrySwitchToUtf8(out Action restore)
    {
        restore = static () => { };
        if (!OperatingSystem.IsWindows())
            return true;

        // Read the code page from the console itself: Console.OutputEncoding cannot always represent
        // an OEM code page, so restoring through it could leave the console on another one.
        var original = GetConsoleOutputCP();
        if (original == 0)
            return false;
        if (original == Utf8)
            return true;
        if (!SetConsoleOutputCP(Utf8))
            return false;

        restore = () => SetConsoleOutputCP(original);
        return true;
    }

    [LibraryImport("kernel32.dll")]
    private static partial uint GetConsoleOutputCP();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleOutputCP(uint wCodePageID);
}

/// <summary>
/// Movie glyphs sent to the terminal as a DECDLD soft font (DRCS), so no font has to be installed.
/// The font lives only in the terminal session: it is mapped onto G0 while the rain runs and G0 is
/// set back to ASCII on exit. It is used only after the terminal confirms DECDLD support (see TerminalCapabilities.CanUseSoftFont).
/// Glyph shapes are rasterized from Matrix-Code.ttf by Rezmason (https://github.com/Rezmason/matrix),
/// MIT License, Copyright (c) 2018 Rezmason. See THIRD_PARTY_NOTICES.md.
/// </summary>
internal static class MatrixSoftFont
{
    private const int CellWidth = 12;
    private const int CellHeight = 24;
    private const int HexPerRow = CellWidth / 4;

    /// <summary>Character set designator for the soft font (intermediate SP, final '@').</summary>
    private const string Dscs = " @";

    /// <summary>Codes 0x21.. mapped to the glyphs in <see cref="Glyphs"/> order.</summary>
    /// <remarks>A property, not a field: static fields initialize in source order and <see cref="Glyphs"/> is declared below.</remarks>
    internal static string Codes => BuildCodes();

    /// <summary>DECDLD download followed by the G0 designation of the soft font.</summary>
    internal static string LoadSequence => BuildDecdld() + "\u001b(" + Dscs;

    /// <summary>Designates US ASCII back into G0.</summary>
    internal const string UnloadSequence = "\u001b(B";

    private static string BuildCodes()
    {
        var chars = new char[Glyphs.Length];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = (char)(0x21 + i);
        return new string(chars);
    }

    private static string BuildDecdld()
    {
        // Pfn=1 font number, Pcn=1 first glyph at 0x21, Pe=0 erase this font, Pcmw=12 cell width,
        // Pss=0 80x24 page, Pt=2 full-cell glyphs, Pcmh=24 cell height, Pcss=0 94-character set.
        var sb = new StringBuilder(64 + Glyphs.Length * (CellWidth * 4 + 4));
        sb.Append("\u001bP1;1;0;").Append(CellWidth).Append(";0;2;").Append(CellHeight).Append(";0{").Append(Dscs);
        Span<int> rows = stackalloc int[CellHeight];
        for (var g = 0; g < Glyphs.Length; g++)
        {
            if (g > 0)
                sb.Append(';');

            var hex = Glyphs[g];
            for (var y = 0; y < CellHeight; y++)
                rows[y] = Convert.ToInt32(hex.Substring(y * HexPerRow, HexPerRow), 16);

            // Sixel bands: each character holds 6 vertical pixels of one column, top pixel in bit 0.
            for (var band = 0; band * 6 < CellHeight; band++)
            {
                if (band > 0)
                    sb.Append('/');
                for (var x = 0; x < CellWidth; x++)
                {
                    var bits = 0;
                    for (var k = 0; k < 6; k++)
                    {
                        var y = band * 6 + k;
                        if (y < CellHeight && (rows[y] & (1 << (CellWidth - 1 - x))) != 0)
                            bits |= 1 << k;
                    }
                    sb.Append((char)(0x3F + bits));
                }
            }
        }

        sb.Append("\u001b\\");
        return sb.ToString();
    }

    /// <summary>12x24 bitmaps, one row per 3 hex digits, leftmost pixel in the highest bit.</summary>
    private static readonly string[] Glyphs =
    [
        "0000000000000000000000000000D80D81981B81B01B0000000000000000000000000000", // " U+0022
        "0000000000000000000000000601681F80600601F8168060000000000000000000000000", // * U+002A
        "0000000000000000000000000600600601F81F8060060060000000000000000000000000", // + U+002B
        "0000000000000001F83FC39C7BE7BE7BE7FE7FE7DE7DE79E39C3FC1F8000000000000000", // 0 U+0030
        "0000000000000300F00F00F0070070070070070070070070070070070070000000000000", // 1 U+0031
        "0000000000000001F83FC3843803803801C00E007003801C01C3FC7FE000000000000000", // 2 U+0032
        "0000000000000001F83FC21C01E01E01E01C01C1F81E00700383FC7FE000000000000000", // 3 U+0033
        "00000000000001C03E03E05E0DE19E11E21E7FE3FE01E01E01E01E3FE3FC000000000000", // 4 U+0034
        "0000000000000007FE3FE01E01E01E3FE3803807807807803843FC1F8000000000000000", // 5 U+0035
        "0000000000000007FE3FE01E01C01C0380380780700700F00F00F00F0000000000000000", // 7 U+0037
        "0000000000000001F83FC39C79E39C39C1F81F839C39C79E39C3FC1F8000000000000000", // 8 U+0038
        "0000000000000001F83FC39C79E79E79E79E79C7FC3803803C01F80F8000000000000000", // 9 U+0039
        "0000000000000000000600F00F00600000000000000600F00F0060000000000000000000", // : U+003A
        "0000000000000000000000180380700E01C01C00E0070038018000000000000000000000", // < U+003C
        "0000000000000000000001801C00E00700380380700E01C0180000000000000000000000", // > U+003E
        "0000000000000007FC3FC01C03C0780700F00E01C03C03807807FC7FE000000000000000", // z U+007A
        "000000000000000060060060060060060060060060060060060060060000000000000000", // | U+007C
        "000000000000000060060060060060000000000060060060060060060000000000000000", // ¦ U+00A6
        "00000000000000000000000000000000079E79E000000000000000000000000000000000", // ╌ U+254C
        "0000000000000000000000000000000600F00F0060000000000000000000000000000000", // ▪ U+25AA
        "0000000000000003FC7FE7FE7007003703703703F01F003001800C000000000000000000", // ア U+30A2
        "0000000000007FE3FC0600607FE7FE70E70E70E70C3003003801800F0038000000000000", // ウ U+30A6
        "0000000000000000003FC3FC0600600600600600600603FC7FE7FE000000000000000000", // エ U+30A8
        "0000000000007FE3FC1C01C07FC7FC1E01E01E01E01E01E01B01B819C184000000000000", // オ U+30AA
        "0000000000000000380383FC7FC7387387387387387187187183CC3C6000000000000000", // カ U+30AB
        "0000000000000000700707FE7FE0700600600603FC7FE0E00E00E00E0000000000000000", // キ U+30AD
        "0000000000007FE3FC0380387F87F83F81DC1CC1C41C01C00C00E007003C000000000000", // ケ U+30B1
        "0000000000000003F87F87F86006006006006006006006007FE7FE3FC000000000000000", // コ U+30B3
        "0000000000000003983983983FC7FE3FC3983981981801801C00E0078000000000000000", // サ U+30B5
        "00000000000000003E03C00040043C63E23C2003003801E00FE0FE03E000000000000000", // シ U+30B7
        "0000000000000001FC1FE1FE1C01C01C01C00C01E01E03F03B831C200000000000000000", // ス U+30B9
        "0000000000007FE01C01C01C7FE7FE7FC31C31C31C19C09C01C3F87F87F8000000000000", // セ U+30BB
        "00000000000000070E70C71C71C71C31C31C3803801801C00E007803C000000000000000", // ソ U+30BD
        "0000000000000003F87F87F871870C30C3363F03E03801C00E007C01C000000000000000", // タ U+30BF
        "00000000000000032476C76C76C76C76C36C3003003801800E007C01C000000000000000", // ツ U+30C4
        "0000000000000003FC7FC3FC0003FC7FE3FC0E00E00E006007003C00C000000000000000", // テ U+30C6
        "0000000000000000C00C00C03FC7FE3FC0C00C00C00E006007003801C000000000000000", // ナ U+30CA
        "0000000000000000003FC3FC0000000000000000000003FC7FE7FE000000000000000000", // ニ U+30CB
        "0000000000000003FC7FC3FC3003803903F01F03E07C07E007003C004000000000000000", // ヌ U+30CC
        "0000000000000600603FC3FE3FC3803801C01E03F07FC67E0600603FC7FE000000000000", // ネ U+30CD
        "0000000000000001D819819819839C39C39C39C30C30C30E706706700000000000000000", // ハ U+30CF
        "00000000000000000C00C00C3FC3FC3FC00C00C00C01C01C3FC3F83F0000000000000000", // ヒ U+30D2
        "0000000000000600703FE3FE3FC07017C37C37C37C37C3747766707FC7FE000000000000", // ホ U+30DB
        "0000000000000000003FC3FC30030030030819C1980F80F8030070000000000000000000", // マ U+30DE
        "0000000000000000043FE7FE7E000001C3FC3FC30000003E3FE7FC300000000000000000", // ミ U+30DF
        "00000000000000000003003003803819819839C39C30C7EC7FE7FE000000000000000000", // ム U+30E0
        "0000000000000000001801C01D81F81F03E07C06E006007003801E000000000000000000", // メ U+30E1
        "0000000000000003FC7FC0380383F87FE3FE0380380380383F07F03F0000000000000000", // モ U+30E2
        "00000000000000001C7FE7FE7FC7183183183B81B80F80380380383FE000000000000000", // ヤ U+30E4
        "0000000000000007FE7FE6066066066067FE7FE6066066066067FE7FE000000000000000", // ヨ U+30E8
        "0000000000000000003FC3FC0003FC7FE7FC3003003801801E00FC000000000000000000", // ラ U+30E9
        "00000000000000070E70E70E70E70E70E30E30C3003803801C00F8078000000000000000", // リ U+30EA
        "0000000000000000007FE7FE70E70E70E7047003003801801E0078000000000000000000", // ワ U+30EF
        "0000000000000000000000000000000003FC3FC000000000000000000000000000000000", // ー U+30FC
        "0000000000000000000000000000F00F00000000F00F0000000000000000000000000000", // ꞊ U+A78A
    ];
}

internal static class TerminalInput
{
    private static bool _rawEnabled;

    internal static Action EnableRawMode()
    {
        if (OperatingSystem.IsWindows())
        {
            Console.TreatControlCAsInput = false;
            return static () => { };
        }

        if (!NativeTermios.TryEnableRawMode(out var restore))
            return static () => { };

        _rawEnabled = true;
        return () =>
        {
            restore();
            _rawEnabled = false;
        };
    }

    internal static bool TryConsumeKey()
    {
        if (OperatingSystem.IsWindows())
        {
            if (!Console.KeyAvailable)
                return false;
            Console.ReadKey(intercept: true);
            return true;
        }

        if (!_rawEnabled)
            return false;

        Span<byte> buffer = stackalloc byte[32];
        return NativeTermios.TryReadInput(buffer, out _);
    }
}

internal static unsafe partial class NativeTermios
{
    private const int StdinFileno = 0;

    internal static bool TryEnableRawMode(out Action restore)
    {
        restore = static () => { };
        if (OperatingSystem.IsLinux())
            return TryEnableRawModeLinux(out restore);
        if (OperatingSystem.IsMacOS())
            return TryEnableRawModeMac(out restore);
        return false;
    }

    private static bool TryEnableRawModeLinux(out Action restore)
    {
        restore = static () => { };
        if (tcgetattrLinux(StdinFileno, out var original) != 0)
            return false;

        var raw = original;
        raw.c_lflag &= ~(EchoLinux | ICanonLinux);
        raw.c_cc[VMinLinux] = 0;
        raw.c_cc[VTimeLinux] = 0;

        if (tcsetattrLinux(StdinFileno, TcsaNow, ref raw) != 0)
            return false;

        restore = () => tcsetattrLinux(StdinFileno, TcsaNow, ref original);
        return true;
    }

    private static bool TryEnableRawModeMac(out Action restore)
    {
        restore = static () => { };
        if (tcgetattrMac(StdinFileno, out var original) != 0)
            return false;

        var raw = original;
        raw.c_lflag &= ~(EchoMac | ICanonMac);
        raw.c_cc[VMinMac] = 0;
        raw.c_cc[VTimeMac] = 0;

        if (tcsetattrMac(StdinFileno, TcsaNow, ref raw) != 0)
            return false;

        restore = () => tcsetattrMac(StdinFileno, TcsaNow, ref original);
        return true;
    }

    internal static bool TryReadInput(Span<byte> buffer, out int bytesRead)
    {
        bytesRead = 0;
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return false;

        // ioctl(FIONREAD) is variadic, and Apple's arm64 ABI passes variadic arguments on the stack,
        // so a plain P/Invoke hands the kernel a garbage pointer and corrupts memory. poll is not variadic.
        var pollFd = new PollFd { fd = StdinFileno, events = PollIn };
        if (poll(ref pollFd, 1, 0) <= 0 || (pollFd.revents & PollIn) == 0)
            return false;

        bytesRead = read(StdinFileno, ref MemoryMarshal.GetReference(buffer), buffer.Length);
        return bytesRead > 0;
    }

    // Local-flag bit values differ between Linux and BSD-derived macOS (ICANON is 0x2 on Linux, 0x100 on macOS).
    private const uint EchoLinux = 0x8;
    private const uint ICanonLinux = 0x2;
    private const ulong EchoMac = 0x8;
    private const ulong ICanonMac = 0x100;
    private const int TcsaNow = 0;
    private const int VMinLinux = 6;
    private const int VTimeLinux = 5;
    private const int VMinMac = 16;
    private const int VTimeMac = 17;
    private const short PollIn = 0x0001;

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        internal int fd;
        internal short events;
        internal short revents;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TermiosLinux
    {
        internal uint c_iflag;
        internal uint c_oflag;
        internal uint c_cflag;
        internal uint c_lflag;
        internal fixed byte c_cc[32];
        internal uint c_ispeed;
        internal uint c_ospeed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TermiosMac
    {
        internal ulong c_iflag;
        internal ulong c_oflag;
        internal ulong c_cflag;
        internal ulong c_lflag;
        internal fixed byte c_cc[20];
        internal ulong c_ispeed;
        internal ulong c_ospeed;
    }

    [LibraryImport("libc", SetLastError = true)]
    private static partial int tcgetattr(int fd, out TermiosLinux termios);

    [LibraryImport("libc", SetLastError = true, EntryPoint = "tcgetattr")]
    private static partial int tcgetattrMac(int fd, out TermiosMac termios);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int tcsetattr(int fd, int optionalActions, ref TermiosLinux termios);

    [LibraryImport("libc", SetLastError = true, EntryPoint = "tcsetattr")]
    private static partial int tcsetattrMac(int fd, int optionalActions, ref TermiosMac termios);

    private static int tcgetattrLinux(int fd, out TermiosLinux termios) => tcgetattr(fd, out termios);

    private static int tcsetattrLinux(int fd, int optionalActions, ref TermiosLinux termios) =>
        tcsetattr(fd, optionalActions, ref termios);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int poll(ref PollFd fds, nuint nfds, int timeout);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int read(int fd, ref byte buffer, int count);
}

internal static class AppText
{
    internal const string Help = """
Usage:
  matrix [duration]
  matrix --duration <seconds>
  matrix --char <character>
  matrix --mode ascii
  matrix --mode movie [--soft-font]
  matrix --mode kana
  matrix --density <0.0-1.0>
  matrix --fps <1-60>
  matrix --pattern <classic|resurrections|operator|twilight|rain|rainbow|fire|cycle|pulse|wave>
  matrix --bg <color> --head <color> --bright <color> --dim <color>
  matrix --cursor-intensity <0.5-5.0>
  matrix --help
  matrix --version

Duration:
  Default 5 seconds. Use 0 or a negative value to run until a key is pressed.
  For negative values, use --duration (e.g. matrix --duration -1).

Modes:
  (default)   ascii-matrix character pool
  --mode ascii  same as default
  --char X    single-character mode
  --mode movie  film glyphs (mirrored katakana, digits, symbols) drawn with a terminal
                soft font (DECDLD) when the terminal reports support, such as Windows
                Terminal. Otherwise warns and falls back to the kana pool.
  --mode kana   full-width katakana with Japanese marks and symbols, without the soft
                font check (UTF-8 required)
  --soft-font   with --mode movie, use the soft font without asking the terminal, for
                terminals the check cannot see, such as a recorder that draws soft fonts.
                A terminal without soft fonts then shows ASCII symbols.

Density:
  --density     Rain density from 0.0 (sparse) to 1.0 (dense). Default 0.55.
                Katakana glyphs (--mode kana, or --mode movie when it falls
                back) default to 0.7.

Timing:
  --fps         Frames per second (1-60). Default 14.
                Higher values make rain fall faster in real time (1-2 cells per frame).

Colors:
  --pattern      Color preset. Default classic.
                 classic: original trilogy-style green rain.
                 resurrections: yellow-green Resurrections-style rain.
                 operator: classic green without a dim trail color.
                 twilight: classic brightness curve with deep-blue background
                           and violet/pink/magenta rain.
                 rain: blue-cyan rain on a dark rainy-night background.
                 rainbow: seven vertical color bands from left to right
                          (red, orange, yellow, green, blue, indigo, violet).
                          Only --bg overrides this pattern.
                 fire: horizontal color bands from deep red at the top to
                       yellow at the bottom, so rain heats up as it falls.
                       Only --bg overrides this pattern.
                 cycle: a full hue wheel spread across the screen width that
                        keeps flowing to the right. Only --bg overrides this pattern.
                 pulse: classic colors whose brightness beats like a slow heart.
                        All color options override this pattern, so any color can pulse.
                 wave: classic colors with a sine-shaped band of light sweeping
                       right through the rain. All color options override this pattern.
  Hex (#RGB or #RRGGBB) or 16-color names (black, green, darkgreen, ...).
  Defaults: --pattern classic --bg #000000 --head #FFFFFF --bright #30FF58 --dim #053D16
  --cursor-intensity  Head-cell bloom strength (additive --head). Default 2.5.

Examples:
  matrix
  matrix 10
  matrix --duration 0
  matrix --char '#'
  matrix --mode movie 30
  matrix --mode movie --density 0.8
  matrix --fps 24
  matrix --mode movie --fps 7
  matrix --mode kana
  matrix --mode movie --soft-font
  matrix --pattern resurrections
  matrix --pattern rain
  matrix --pattern rainbow --bg #050505
  matrix --pattern fire
  matrix --pattern cycle 0
  matrix --pattern pulse --bright #FF3040 --dim #400008
  matrix --pattern wave --density 0.8 0
  matrix --bright #0F0 --dim #080

Press any key to exit early. Ctrl+C also exits cleanly.

""";
}
