#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property Version=1.1.0
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

const string VersionString = "1.1.0";

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

// Movie mode draws the film glyphs with a soft font when the terminal confirms support,
// and otherwise falls back to full-width katakana with a warning. --mode kana skips the check.
var glyphMode = options.Mode;
var density = options.Density;
if (glyphMode == GlyphMode.Movie && options.ProbeSoftFont)
{
    if (TerminalCapabilities.CanUseSoftFont(colorMode, out var fallbackReason))
    {
        glyphMode = GlyphMode.MovieSoftFont;
        if (!options.DensityExplicit)
            density = EngineConstants.DefaultDensity;
    }
    else
    {
        Console.Error.Write("warning: ");
        Console.Error.Write(fallbackReason);
        Console.Error.WriteLine(". falling back to katakana glyphs (use --mode kana to skip this check)");
    }
}

var cursorIntensity = options.CursorIntensity;

var keyExitEnabled = !Console.IsInputRedirected;
if (!keyExitEnabled)
{
    Console.Error.WriteLine("warning: stdin is not a tty. key exit disabled, using duration only");
}

GlyphPool pool;
try
{
    pool = GlyphPool.Create(glyphMode, options.SingleChar);
}
catch (InvalidOperationException ex)
{
    Console.Error.Write("Error: ");
    Console.Error.WriteLine(ex.Message);
    Environment.Exit(1);
    return;
}

var palette = AnsiPalette.Create(options.Colors, colorMode, cursorIntensity);
using var engine = new MatrixEngine(pool, palette, density, options.Fps);
using var pacer = new FramePacer(options.Fps);

var restore = TerminalSession.Enter(keyExitEnabled, softFont: glyphMode == GlyphMode.MovieSoftFont);
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
    internal const int GlyphMutationChance = 35;
    internal const int MinSpeed = 1;
    internal const int MaxSpeed = 2;
    internal const double DefaultDensity = 0.55;
    internal const double DefaultMovieDensity = 0.7;
    internal const int DensityActiveBasePercent = 90;
    internal const double MovieDensityBoost = 1.5;
    /// <summary>Average cells a stream travels before deactivating ≈ factor × height (see spawn equilibrium).</summary>
    internal const double StreamLifetimeHeightFactor = 1.15;
    internal const double AvgFallSpeedCells = (MinSpeed + MaxSpeed) / 2.0;
    /// <summary>Frames to close an active-column deficit when below density target (~0.2s at 14 FPS).</summary>
    internal const int DensityRecoveryFrames = 3;
    /// <summary>Baseline spawn multiplier at/above target — compensates lifetime model error and ongoing deaths.</summary>
    internal const double DensitySpawnHeadroom = 1.45;
    /// <summary>Max trail-length scale when active columns are far below the density target (e.g. 0.35 → up to 35% longer).</summary>
    internal const double TrailLengthDeficitMaxBoost = 0.35;
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
    /// <summary>Cap simultaneous stream births so cohorts do not die in sync (~2–3s).</summary>
    internal const int MaxSpawnsPerFrame = 4;
    internal const int MaxStreamsPerColumn = 2;
    /// <summary>Begin considering same-column respawn once the head has crossed this screen fraction.</summary>
    internal const double RecycleStartHeadFraction = 0.50;
    /// <summary>Screen coverage below this fraction strongly favors immediate same-column respawn.</summary>
    internal const double RecycleSparseVisibleFraction = 0.28;
    /// <summary>Screen coverage above this fraction delays respawn until the head is off-screen.</summary>
    internal const double RecycleDenseVisibleFraction = 0.72;
    /// <summary>Maximum top-entry delay for recycled streams; initial streams keep the wider stagger.</summary>
    internal const double RecycleInitialDelayHeightFraction = 0.22;
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
}

internal sealed class CliOptions
{
    internal GlyphMode Mode { get; private init; } = GlyphMode.AsciiMatrix;
    /// <summary>True for --mode movie (try the soft font); false for --mode kana (katakana only).</summary>
    internal bool ProbeSoftFont { get; private init; }
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
                case "--char":
                    if (++i >= args.Length)
                        return Error("missing value for --char");
                    if (args[i].Length != 1)
                        return Error("--char requires exactly one character");
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
                        return Error("invalid --pattern value (expected classic, resurrections, operator, twilight, rain, or rainbow)");
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
            CursorIntensity = cursorIntensity,
            Fps = fps,
            Colors = colors,
        };
    }

    private static CliOptions Error(string message) => new() { ErrorMessage = message };

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
        if (pattern == ColorPattern.Rainbow)
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
            ColorPattern.Classic => Default,
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
            ColorPattern.Rainbow => new ColorOptions(
                pattern,
                DefaultBackground,
                DefaultHead,
                new ColorValue(new Rgb(255, 0, 0)),
                new ColorValue(new Rgb(80, 0, 0))),
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

internal static class TerminalCapabilities
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
    /// True only when the terminal confirms soft font (DECDLD) support. Otherwise returns false with
    /// the reason: no VT output, stdin not a tty, a ConPTY host other than Windows Terminal, or no DA1 reply.
    /// </summary>
    internal static bool CanUseSoftFont(TerminalColorMode colorMode, out string reason)
    {
        reason = string.Empty;
        if (colorMode == TerminalColorMode.None)
        {
            reason = "VT output is not available";
            return false;
        }

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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);
}

/// <summary>
/// Paces frames against fixed scheduled times so wait errors do not add up across frames.
/// On Windows, Thread.Sleep rounds up to the 15.6 ms system tick (30 FPS became about 21 FPS),
/// so waits use a high-resolution waitable timer. Other platforms already sleep precisely.
/// </summary>
internal sealed class FramePacer : IDisposable
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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateWaitableTimerExW(IntPtr lpTimerAttributes, IntPtr lpTimerName, uint dwFlags, uint dwDesiredAccess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetWaitableTimer(IntPtr hTimer, ref long lpDueTime, int lPeriod, IntPtr pfnCompletionRoutine, IntPtr lpArgToCompletionRoutine, bool fResume);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}

internal static class RainBrightness
{
    private const double Sqrt2 = 1.4142135623730951;
    private const double Sqrt5 = 2.23606797749979;

    internal static float Compute(int rowY, int gridHeight, double simTime, in ColumnState column)
    {
        // Reference shader: glyphPos.y = 0 at bottom; terminal row 0 is top.
        var glyphY = gridHeight - 1 - rowY;
        var columnTime = column.TimeOffset + simTime * EngineConstants.RainFallSpeed * column.SpeedOffset;
        var rainTime = (glyphY * 0.01 + columnTime) / EngineConstants.RaindropLength;
        rainTime = Wobble(rainTime);
        return (float)(1.0 - Frac(rainTime));
    }

    internal static float ColumnTimeOffset(int columnX)
    {
        var n = Math.Sin(columnX * 12.9898) * 43758.5453;
        return (float)(n - Math.Floor(n)) * 1000f;
    }

    internal static float SpeedOffsetFromColumnSpeed(byte speed) =>
        0.5f + (speed - EngineConstants.MinSpeed) / (float)(EngineConstants.MaxSpeed - EngineConstants.MinSpeed) * 0.5f;

    private static double Wobble(double x) =>
        x + 0.3 * Math.Sin(Sqrt2 * x) + 0.2 * Math.Sin(Sqrt5 * x);

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
    private readonly byte[] _space = " "u8.ToArray();
    private readonly BrightnessPalette _palette;
    private readonly BrightnessPalette[]? _columnBandPalettes;
    private readonly TerminalColorMode _colorMode;
    private readonly Rgb _cursorAddend;
    private readonly Rgb[]? _columnBandCursorAddends;

    private AnsiPalette(
        byte[] bgRowPrefix,
        byte[] rowFgReset,
        BrightnessPalette palette,
        BrightnessPalette[]? columnBandPalettes,
        TerminalColorMode colorMode,
        Rgb cursorAddend,
        Rgb[]? columnBandCursorAddends)
    {
        _bgRowPrefix = bgRowPrefix;
        _rowFgReset = rowFgReset;
        _palette = palette;
        _columnBandPalettes = columnBandPalettes;
        _colorMode = colorMode;
        _cursorAddend = cursorAddend;
        _columnBandCursorAddends = columnBandCursorAddends;
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

        BrightnessPalette[]? columnBandPalettes = null;
        Rgb[]? columnBandCursorAddends = null;
        if (colors.Pattern == ColorPattern.Rainbow)
        {
            columnBandPalettes = CreateRainbowPalettes(bgRgb);
            columnBandCursorAddends = new Rgb[columnBandPalettes.Length];
            for (var i = 0; i < columnBandPalettes.Length; i++)
                columnBandCursorAddends[i] = BrightnessPalette.ScaleRgb(columnBandPalettes[i].HeadColor, cursorIntensity);
        }

        var cursorAddend = BrightnessPalette.ScaleRgb(palette.HeadColor, cursorIntensity);
        return new AnsiPalette(bgPrefix, rowFgReset, palette, columnBandPalettes, colorMode, cursorAddend, columnBandCursorAddends);
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

    private static void WriteTrueColorSequence(Span<byte> scratch, bool isBackground, Rgb rgb, out int length)
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
        if (value == 0)
        {
            destination[0] = (byte)'0';
            return 1;
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
        Span<byte> fgScratch = stackalloc byte[32];

        for (var y = 0; y < height; y++)
        {
            if (y > 0)
                AppendCupRow1(buffer, ref pos, y + 1);

            AppendBytes(buffer, ref pos, _bgRowPrefix);
            AppendBytes(buffer, ref pos, _rowFgReset);

            var row = y * width;
            Rgb lastFgRgb = default;
            var hasLastFgRgb = false;

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

                    AppendBytes(buffer, ref pos, _space);
                    continue;
                }

                var brightness = cell.Brightness / 255.0;
                brightness -= Dither(x, y, frameNumber) * (EngineConstants.DitherMagnitude / 3.0);
                brightness = Math.Clamp(brightness, 0, 1);
                var palette = SelectPalette(x, width, out var band);
                var rgb = palette.Sample((byte)(brightness * 255));
                if (cell.CursorBoost != 0)
                    rgb = BrightnessPalette.AddClamped(rgb, SelectCursorAddend(band));

                if (_colorMode == TerminalColorMode.TrueColor)
                {
                    if (!hasLastFgRgb || rgb.R != lastFgRgb.R || rgb.G != lastFgRgb.G || rgb.B != lastFgRgb.B)
                    {
                        WriteTrueColorSequence(fgScratch, isBackground: false, rgb, out var fgLen);
                        AppendBytes(buffer, ref pos, fgScratch[..fgLen]);
                        lastFgRgb = rgb;
                        hasLastFgRgb = true;
                    }
                }
                else if (_colorMode == TerminalColorMode.Ansi16)
                {
                    if (!hasLastFgRgb || rgb.R != lastFgRgb.R || rgb.G != lastFgRgb.G || rgb.B != lastFgRgb.B)
                    {
                        var fgCode = Ansi16FgCode(ColorParser.NearestNamed(rgb));
                        WriteAnsi16Sequence(fgScratch, fgCode, out var fgLen);
                        AppendBytes(buffer, ref pos, fgScratch[..fgLen]);
                        lastFgRgb = rgb;
                        hasLastFgRgb = true;
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

    private static float Dither(int x, int y, ulong frame)
    {
        var n = Math.Sin(x * 12.9898 + y * 78.233 + frame * 0.1) * 43758.5453;
        return (float)(n - Math.Floor(n));
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
    private BrightnessPalette SelectPalette(int x, int width, out int band)
    {
        if (_columnBandPalettes is null)
        {
            band = -1;
            return _palette;
        }

        band = Math.Min(_columnBandPalettes.Length - 1, x * _columnBandPalettes.Length / Math.Max(1, width));
        return _columnBandPalettes[band];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Rgb SelectCursorAddend(int band) =>
        band >= 0 && _columnBandCursorAddends is not null
            ? _columnBandCursorAddends[band]
            : _cursorAddend;

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

    internal static GlyphPool Create(GlyphMode mode, char singleChar)
    {
        switch (mode)
        {
            case GlyphMode.Single:
                return new GlyphPool(string.Empty, singleChar, true, GlyphMode.Single, requiresWideColumns: false);
            case GlyphMode.Movie:
                EnsureUtf8();
                return new GlyphPool(MovieKatakanaChars, '\0', false, GlyphMode.Movie, requiresWideColumns: true);
            case GlyphMode.MovieSoftFont:
                // Plain ASCII codes, the terminal draws them with the soft font loaded by MatrixSoftFont.
                return new GlyphPool(MatrixSoftFont.Codes, '\0', false, GlyphMode.MovieSoftFont, requiresWideColumns: false);
            default:
                return new GlyphPool(AsciiMatrixChars, '\0', false, GlyphMode.AsciiMatrix, requiresWideColumns: false);
        }
    }

    private static void EnsureUtf8()
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.InputEncoding = Encoding.UTF8;
        }
        catch (Exception)
        {
            throw new InvalidOperationException("movie mode requires a UTF-8 capable terminal");
        }

        if (!Encoding.UTF8.Equals(Console.OutputEncoding))
            throw new InvalidOperationException("movie mode requires a UTF-8 capable terminal");
    }

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
    private int _activeChancePercent;
    private int _baseSpawnChancePercent;
    private int _spawnChancePercent;
    private double _trailLengthBoost = 1.0;
    private readonly Random _rng = new();

    private Cell[] _grid = [];
    private ColumnState[] _columns = [];
    private byte[] _renderBuffer = [];
    private int _width;
    private int _height;
    private double _simTime;
    private ulong _frameNumber;
    private int _pendingResizeWidth;
    private int _pendingResizeHeight;
    private int _pendingResizeStable;
    private int _spawnsThisFrame;
    private readonly int _targetFps;

    internal MatrixEngine(GlyphPool pool, AnsiPalette palette, double density, int targetFps)
    {
        _pool = pool;
        _palette = palette;
        _targetFps = targetFps;
        _wideColumns = pool.RequiresWideColumns;

        var effectiveDensity = Math.Clamp(density, 0, 1);
        if (_wideColumns)
            effectiveDensity = Math.Min(1.0, effectiveDensity * EngineConstants.MovieDensityBoost);

        _effectiveDensity = effectiveDensity;
        Resize();
    }

    private void UpdateDensityChances()
    {
        _activeChancePercent = Math.Min(100, (int)(_effectiveDensity * EngineConstants.DensityActiveBasePercent));
        _baseSpawnChancePercent = ComputeEquilibriumSpawnPercent(_activeChancePercent, _height);
        _spawnChancePercent = _baseSpawnChancePercent;
    }

    /// <summary>Adjust spawn rate and new-stream trail length from active-column shortfall vs density target.</summary>
    private void RefreshAdaptiveSpawnChance()
    {
        var streamCount = 0;
        var activeCount = 0;
        for (var x = 0; x < _width; x++)
        {
            if (!IsStreamColumn(x))
                continue;
            streamCount++;
            if (IsColumnActive(x))
                activeCount++;
        }

        if (streamCount == 0 || _activeChancePercent <= 0)
        {
            _spawnChancePercent = 0;
            _trailLengthBoost = 1.0;
            return;
        }

        var targetFraction = _activeChancePercent / 100.0;
        var currentFraction = activeCount / (double)streamCount;
        var shortfall = Math.Max(0, targetFraction - currentFraction);
        _trailLengthBoost = 1.0 + shortfall / Math.Max(0.01, targetFraction) * EngineConstants.TrailLengthDeficitMaxBoost;

        if (_activeChancePercent >= 100)
        {
            _spawnChancePercent = 100;
            return;
        }

        var targetActive = (int)Math.Ceiling(targetFraction * streamCount);
        var deficit = targetActive - activeCount;
        var headroomSpawn = Math.Min(100, (int)Math.Round(_baseSpawnChancePercent * EngineConstants.DensitySpawnHeadroom));

        if (deficit < 0)
        {
            _spawnChancePercent = 0;
            return;
        }

        if (deficit == 0)
        {
            _spawnChancePercent = _baseSpawnChancePercent;
            return;
        }

        var inactive = streamCount - activeCount;
        if (inactive <= 0)
        {
            _spawnChancePercent = 100;
            return;
        }

        var neededPerFrame = deficit / (double)EngineConstants.DensityRecoveryFrames;
        var adaptivePercent = (int)Math.Ceiling(neededPerFrame / inactive * 100);
        _spawnChancePercent = Math.Min(100, Math.Max(headroomSpawn, adaptivePercent));
    }

    /// <summary>Per-frame spawn % so steady active fraction ≈ initial active chance (births = deaths).</summary>
    private static int ComputeEquilibriumSpawnPercent(int activeChancePercent, int height)
    {
        if (activeChancePercent <= 0)
            return 0;
        if (activeChancePercent >= 100)
            return 100;

        var targetFraction = activeChancePercent / 100.0;
        var avgLifetimeFrames = Math.Max(
            1.0,
            (EngineConstants.StreamLifetimeHeightFactor * height - 2) / EngineConstants.AvgFallSpeedCells);
        var deathRate = 1.0 / avgLifetimeFrames;
        var spawnFraction = targetFraction * deathRate / (1.0 - targetFraction);
        return Math.Min(100, (int)Math.Round(spawnFraction * 100));
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

        _spawnsThisFrame = 0;
        RefreshAdaptiveSpawnChance();

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

    private bool IsColumnActive(int x)
    {
        for (var slot = 0; slot < EngineConstants.MaxStreamsPerColumn; slot++)
        {
            if (_columns[StreamIndex(x, slot)].Active)
                return true;
        }

        return false;
    }

    internal void Render(Stream stdout)
    {
        var needed = _width * _height * 72 + _height * 24 + 96;
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
            ArrayPool<ColumnState>.Shared.Return(_columns);
        }

        _width = newWidth;
        _height = newHeight;
        _pendingResizeWidth = newWidth;
        _pendingResizeHeight = newHeight;
        _pendingResizeStable = EngineConstants.ResizeStableFrames;
        UpdateDensityChances();
        _grid = ArrayPool<Cell>.Shared.Rent(_width * _height);
        _columns = ArrayPool<ColumnState>.Shared.Rent(_width * EngineConstants.MaxStreamsPerColumn);
        Array.Clear(_grid, 0, _width * _height);

        for (var x = 0; x < _width; x++)
        {
            for (var slot = 0; slot < EngineConstants.MaxStreamsPerColumn; slot++)
                _columns[StreamIndex(x, slot)] = default;
            if (IsStreamColumn(x) && RollPercent(_activeChancePercent))
                ActivateColumn(x);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool RollPercent(int chance) =>
        chance >= 100 || (chance > 0 && _rng.Next(100) < chance);

    private void UpdateColumn(int x)
    {
        ClearColumn(x);

        var activeCount = 0;
        var freeSlot = -1;
        for (var slot = 0; slot < EngineConstants.MaxStreamsPerColumn; slot++)
        {
            ref var stream = ref _columns[StreamIndex(x, slot)];
            if (stream.Active)
                activeCount++;
            else if (freeSlot < 0)
                freeSlot = slot;
        }

        if (freeSlot >= 0 && _spawnsThisFrame < EngineConstants.MaxSpawnsPerFrame)
        {
            if (activeCount == 0 && RollPercent(_spawnChancePercent))
            {
                ActivateColumn(x, freeSlot);
                _spawnsThisFrame++;
            }
            else if (activeCount > 0 && ShouldRecycleColumn(x) && RollPercent(_spawnChancePercent))
            {
                ActivateColumn(x, freeSlot, recycled: true);
                _spawnsThisFrame++;
            }
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
        for (var y = updateStart; y <= updateEnd; y++)
        {
            var dist = headY - y;
            ref var cell = ref _grid[y * _width + x];
            if (cell.State is (byte)CellState.Empty or (byte)CellState.Continuation)
                cell.Glyph = _pool.Pick(_rng);

            if (_rng.Next(100) < EngineConstants.GlyphMutationChance)
                cell.Glyph = _pool.Pick(_rng);

            var wave = RainBrightness.Compute(y, _height, _simTime, column);
            wave = MathF.Max(wave, NearHeadWaveFloor(dist));
            var envelope = TrailEnvelope(dist, column.TrailLength);
            wave = EngineConstants.RainWaveMinimum + wave * (1f - EngineConstants.RainWaveMinimum);
            var brightness = wave * envelope;
            var cursorBoost = (byte)(dist == 0 ? 1 : 0);
            var brightnessByte = (byte)Math.Clamp((int)(brightness * 255), 0, 255);
            SetDisplayGlyph(x, y, cell.Glyph, brightnessByte, cursorBoost);
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

    private void ClearDisplayCells(int x, int y)
    {
        ref var cell = ref _grid[y * _width + x];
        cell.State = (byte)CellState.Empty;
        cell.Glyph = '\0';
        cell.Brightness = 0;
        cell.CursorBoost = 0;
        if (_wideColumns && x + 1 < _width)
        {
            ref var cont = ref _grid[y * _width + x + 1];
            cont.State = (byte)CellState.Empty;
            cont.Glyph = '\0';
            cont.Brightness = 0;
            cont.CursorBoost = 0;
        }
    }

    private bool ShouldRecycleColumn(int x)
    {
        if (_height <= 0)
            return false;

        var visibleCells = 0;
        var headY = int.MinValue;
        for (var slot = 0; slot < EngineConstants.MaxStreamsPerColumn; slot++)
        {
            ref var column = ref _columns[StreamIndex(x, slot)];
            if (!column.Active)
                continue;

            headY = Math.Max(headY, column.HeadY);
            var trailTop = column.HeadY - column.TrailLength + 1;
            var visibleStart = Math.Max(0, trailTop);
            var visibleEnd = Math.Min(column.HeadY, _height - 1);
            if (visibleEnd >= visibleStart)
                visibleCells += visibleEnd - visibleStart + 1;
        }

        var recycleStart = (int)Math.Round(_height * EngineConstants.RecycleStartHeadFraction);
        if (headY < recycleStart)
            return false;

        var visibleFraction = Math.Min(1.0, visibleCells / (double)_height);
        var progress = Math.Clamp((headY - recycleStart) / (double)Math.Max(1, _height - recycleStart), 0.0, 1.0);

        if (visibleFraction <= EngineConstants.RecycleSparseVisibleFraction)
            return RollPercent((int)Math.Round(35 + progress * 50));

        if (visibleFraction >= EngineConstants.RecycleDenseVisibleFraction && headY <= _height - 1)
            return false;

        var coverageDelay = Math.Clamp(
            (visibleFraction - EngineConstants.RecycleSparseVisibleFraction) /
            (EngineConstants.RecycleDenseVisibleFraction - EngineConstants.RecycleSparseVisibleFraction),
            0.0,
            1.0);
        var chance = Math.Clamp((progress - coverageDelay * 0.45) * 60, 0, 65);
        return RollPercent((int)Math.Round(chance));
    }

    private void ActivateColumn(int x, int slot = 0, bool recycled = false)
    {
        var minTrail = (int)Math.Round(
            Math.Max(EngineConstants.MinTrailCells, _height * EngineConstants.TrailMinHeightFraction) * _trailLengthBoost);
        var maxTrail = (int)Math.Round(
            Math.Max(minTrail, _height * EngineConstants.TrailMaxHeightFraction) * _trailLengthBoost);
        maxTrail = Math.Min(_height, maxTrail);
        minTrail = Math.Min(minTrail, maxTrail);
        var speed = (byte)_rng.Next(EngineConstants.MinSpeed, EngineConstants.MaxSpeed + 1);

        _columns[StreamIndex(x, slot)] = new ColumnState
        {
            Active = true,
            HeadY = -_rng.Next(0, Math.Max(1, recycled ? (int)Math.Round(_height * EngineConstants.RecycleInitialDelayHeightFraction) : _height)),
            Speed = speed,
            TrailLength = _rng.Next(minTrail, maxTrail + 1),
            TimeOffset = RainBrightness.ColumnTimeOffset(x),
            SpeedOffset = RainBrightness.SpeedOffsetFromColumnSpeed(speed),
        };
    }

    private void ClearColumn(int x)
    {
        for (var y = 0; y < _height; y++)
            ClearDisplayCells(x, y);
    }

    public void Dispose()
    {
        if (_grid.Length > 0)
        {
            ArrayPool<Cell>.Shared.Return(_grid);
            _grid = [];
        }

        if (_columns.Length > 0)
        {
            ArrayPool<ColumnState>.Shared.Return(_columns);
            _columns = [];
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
    internal static Action Enter(bool rawInput, bool softFont)
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
        };
    }
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

internal static unsafe class NativeTermios
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
        raw.c_lflag &= ~(uint)(Echo | ICanon);
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
        raw.c_lflag &= ~(uint)(Echo | ICanon);
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

        var available = 0;
        var ioctlRequest = OperatingSystem.IsLinux() ? FionReadLinux : FionReadMacOS;
        if (ioctl(StdinFileno, ioctlRequest, ref available) != 0 || available <= 0)
            return false;

        bytesRead = read(StdinFileno, ref MemoryMarshal.GetReference(buffer), buffer.Length);
        return bytesRead > 0;
    }

    private const uint Echo = 8;
    private const uint ICanon = 2;
    private const int TcsaNow = 0;
    private const int VMinLinux = 6;
    private const int VTimeLinux = 5;
    private const int VMinMac = 16;
    private const int VTimeMac = 17;
    private const uint FionReadLinux = 0x541B;
    private const uint FionReadMacOS = 0x4004667F;

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

    [DllImport("libc", SetLastError = true)]
    private static extern int tcgetattr(int fd, out TermiosLinux termios);

    [DllImport("libc", SetLastError = true, EntryPoint = "tcgetattr")]
    private static extern int tcgetattrMac(int fd, out TermiosMac termios);

    [DllImport("libc", SetLastError = true)]
    private static extern int tcsetattr(int fd, int optionalActions, ref TermiosLinux termios);

    [DllImport("libc", SetLastError = true, EntryPoint = "tcsetattr")]
    private static extern int tcsetattrMac(int fd, int optionalActions, ref TermiosMac termios);

    private static int tcgetattrLinux(int fd, out TermiosLinux termios) => tcgetattr(fd, out termios);

    private static int tcsetattrLinux(int fd, int optionalActions, ref TermiosLinux termios) =>
        tcsetattr(fd, optionalActions, ref termios);

    [DllImport("libc", SetLastError = true)]
    private static extern int ioctl(int fd, uint request, ref int argp);

    [DllImport("libc", SetLastError = true)]
    private static extern int read(int fd, ref byte buffer, int count);
}

internal static class AppText
{
    internal const string Help = """
Usage:
  matrix [duration]
  matrix --duration <seconds>
  matrix --char <character>
  matrix --mode ascii
  matrix --mode movie
  matrix --mode kana
  matrix --density <0.0-1.0>
  matrix --fps <1-60>
  matrix --pattern <classic|resurrections|operator|twilight|rain|rainbow>
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

Density:
  --density     Rain density from 0.0 (sparse) to 1.0 (dense). Default 0.55.
                Katakana glyphs (--mode kana, or --mode movie when it falls
                back) fill only every other column, so their density is
                multiplied by 1.5 (capped at 1.0) and the default is 0.7.

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
  matrix --pattern resurrections
  matrix --pattern rain
  matrix --pattern rainbow --bg #050505
  matrix --bright #0F0 --dim #080

Press any key to exit early. Ctrl+C also exits cleanly.

""";
}
