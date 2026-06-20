#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property Version=1.0.0
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

if (TryHandleEarlyExit(args))
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
    Console.Out.WriteLine(System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0");
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

var useTrueColor = TerminalCapabilities.EnableVirtualTerminalIfNeeded() && TerminalCapabilities.SupportsTrueColor();
if (!useTrueColor)
{
    Console.Error.WriteLine("warning: true color not supported; falling back to 16-color palette");
}

var keyExitEnabled = !Console.IsInputRedirected;
if (!keyExitEnabled)
{
    Console.Error.WriteLine("warning: stdin is not a tty; key exit disabled, using duration only");
}

GlyphPool pool;
try
{
    pool = GlyphPool.Create(options.Mode, options.SingleChar);
}
catch (InvalidOperationException ex)
{
    Console.Error.Write("Error: ");
    Console.Error.WriteLine(ex.Message);
    Environment.Exit(1);
    return;
}

var palette = AnsiPalette.Create(options.Colors, useTrueColor, options.CursorIntensity);
using var engine = new MatrixEngine(pool, palette, options.Density, options.Fps);
var frameDelayMs = 1000.0 / options.Fps;

var restore = TerminalSession.Enter(keyExitEnabled);
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
        var frameStart = Stopwatch.GetTimestamp();
        engine.Render(stdout);
        var frameMs = (Stopwatch.GetTimestamp() - frameStart) * 1000.0 / Stopwatch.Frequency;
        var sleepMs = Math.Max(0, (int)Math.Round(frameDelayMs - frameMs));
        Thread.Sleep(sleepMs);
    }
}
finally
{
    restore();
}

return;

static bool TryHandleEarlyExit(string[] args)
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
            Console.Out.WriteLine(System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0");
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
    internal const double StreamLifetimeHeightFactor = 1.55;
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
    internal const int LutTailFadeEnd = 38;
    internal const int LutDimIndex = 64;
    internal const int LutBrightIndex = 191;
    internal const double LutBrightScale = 1.18;
    /// <summary>&lt; 1 lifts mid-trail vs tip; tip still reaches 0.</summary>
    internal const double TrailEnvelopeGamma = 0.78;
    /// <summary>Cap simultaneous stream births so cohorts do not die in sync (~2–3s).</summary>
    internal const int MaxSpawnsPerFrame = 4;
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

internal sealed class CliOptions
{
    internal GlyphMode Mode { get; private init; } = GlyphMode.AsciiMatrix;
    internal char SingleChar { get; private init; } = '0';
    internal double DurationSeconds { get; private init; } = 5;
    internal bool ShowHelp { get; private init; }
    internal bool ShowVersion { get; private init; }
    internal string? ErrorMessage { get; private init; }
    internal ColorOptions Colors { get; private init; } = ColorOptions.Default;
    internal double Density { get; private init; } = EngineConstants.DefaultDensity;
    internal double CursorIntensity { get; private init; } = EngineConstants.DefaultCursorIntensity;
    internal int Fps { get; private init; } = EngineConstants.DefaultTargetFps;

    internal static CliOptions Parse(string[] args)
    {
        var mode = GlyphMode.AsciiMatrix;
        var hasMovie = false;
        var hasChar = false;
        var singleChar = '0';
        var duration = 5.0;
        var density = EngineConstants.DefaultDensity;
        var hasDensityFlag = false;
        var hasDurationFlag = false;
        var hasPositionalDuration = false;
        ColorValue? bg = null, head = null, bright = null, dim = null;
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
                    if (args[i] == "movie")
                        hasMovie = true;
                    else if (args[i] == "ascii")
                        hasMovie = false;
                    else
                        return Error("unknown --mode value (expected 'ascii' or 'movie')");
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

        if (hasChar && hasMovie)
            return Error("--char and --mode movie cannot be used together");

        if (hasDurationFlag && hasPositionalDuration)
            return Error("specify duration either as a positional argument or via --duration, not both");

        if (hasChar)
            mode = GlyphMode.Single;
        else if (hasMovie)
            mode = GlyphMode.Movie;

        if (!hasDensityFlag && mode == GlyphMode.Movie)
            density = EngineConstants.DefaultMovieDensity;

        return new CliOptions
        {
            Mode = mode,
            SingleChar = singleChar,
            DurationSeconds = duration,
            Density = density,
            CursorIntensity = cursorIntensity,
            Fps = fps,
            Colors = new ColorOptions(
                bg ?? ColorOptions.DefaultBackground,
                head ?? ColorOptions.DefaultHead,
                bright ?? ColorOptions.DefaultBright,
                dim ?? ColorOptions.DefaultDim),
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
}

internal readonly struct ColorOptions(
    ColorValue background,
    ColorValue head,
    ColorValue bright,
    ColorValue dim)
{
    internal static ColorOptions Default => new(
        DefaultBackground,
        DefaultHead,
        DefaultBright,
        DefaultDim);

    internal static readonly ColorValue DefaultBackground = new(new Rgb(0, 0, 0));
    internal static readonly ColorValue DefaultHead = new(new Rgb(255, 255, 255));
    internal static readonly ColorValue DefaultBright = new(new Rgb(48, 255, 88));
    internal static readonly ColorValue DefaultDim = new(new Rgb(0, 170, 28));

    internal ColorValue Background { get; } = background;
    internal ColorValue Head { get; } = head;
    internal ColorValue Bright { get; } = bright;
    internal ColorValue Dim { get; } = dim;
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

    internal BrightnessPalette(Rgb head, Rgb bright, Rgb dim, Rgb bg)
    {
        _head = head;
        BuildLut(_lut, bright, dim, bg);
    }

    internal ReadOnlySpan<Rgb> Lut => _lut;
    internal Rgb HeadColor => _head;

    internal Rgb Sample(byte brightnessIndex) => _lut[brightnessIndex];

    private static void BuildLut(Span<Rgb> lut, Rgb bright, Rgb dim, Rgb bg)
    {
        var vividBright = ScaleRgb(bright, EngineConstants.LutBrightScale);
        var hotBright = HotBright(vividBright);
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
    private readonly bool _trueColor;
    private readonly Rgb _cursorAddend;

    private AnsiPalette(byte[] bgRowPrefix, byte[] rowFgReset, BrightnessPalette palette, bool trueColor, Rgb cursorAddend)
    {
        _bgRowPrefix = bgRowPrefix;
        _rowFgReset = rowFgReset;
        _palette = palette;
        _trueColor = trueColor;
        _cursorAddend = cursorAddend;
    }

    internal static AnsiPalette Create(ColorOptions colors, bool trueColor, double cursorIntensity)
    {
        var palette = new BrightnessPalette(
            ResolveRgb(colors.Head),
            ResolveRgb(colors.Bright),
            ResolveRgb(colors.Dim),
            ResolveRgb(colors.Background));

        var bgRgb = ResolveRgb(colors.Background);
        var bgNamed = ResolveNamed(colors.Background);
        var bgPrefix = trueColor
            ? BuildTrueColorBgPrefix(bgRgb)
            : BuildAnsi16BgPrefix(bgNamed);
        var rowFgReset = trueColor
            ? BuildTrueColorFgPrefix(bgRgb)
            : BuildAnsi16FgPrefix(bgNamed);

        var cursorAddend = BrightnessPalette.ScaleRgb(palette.HeadColor, cursorIntensity);
        return new AnsiPalette(bgPrefix, rowFgReset, palette, trueColor, cursorAddend);
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
        var code = color <= ConsoleColor16.White ? 40 + (int)color : 100 + ((int)color - 8);
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

    internal void WriteFrame(Stream stdout, ReadOnlySpan<Cell> grid, int width, int height, byte[] buffer, ulong frameNumber)
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
                    AppendBytes(buffer, ref pos, _space);
                    continue;
                }

                var brightness = cell.Brightness / 255.0;
                brightness -= Dither(x, y, frameNumber) * (EngineConstants.DitherMagnitude / 3.0);
                brightness = Math.Clamp(brightness, 0, 1);
                var rgb = _palette.Sample((byte)(brightness * 255));
                if (cell.CursorBoost != 0)
                    rgb = BrightnessPalette.AddClamped(rgb, _cursorAddend);

                if (_trueColor)
                {
                    if (!hasLastFgRgb || rgb.R != lastFgRgb.R || rgb.G != lastFgRgb.G || rgb.B != lastFgRgb.B)
                    {
                        WriteTrueColorSequence(fgScratch, isBackground: false, rgb, out var fgLen);
                        AppendBytes(buffer, ref pos, fgScratch[..fgLen]);
                        lastFgRgb = rgb;
                        hasLastFgRgb = true;
                    }
                }
                else
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
        color <= ConsoleColor16.White ? 30 + (int)color : 90 + ((int)color - 8);

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
            return rng.Next(100) < 80
                ? MovieDigitChars[rng.Next(MovieDigitChars.Length)]
                : MovieKanjiChars[rng.Next(MovieKanjiChars.Length)];
        return MovieSymbolChars[rng.Next(MovieSymbolChars.Length)];
    }

    private const string AsciiMatrixChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789@#$%^&*()";
    // Movie mode: full-width (two display cells) only — mixed widths break vertical alignment.
    private const string MovieKatakanaChars =
        "アイウエオカキクケコサシスセソタチツテトナニヌネノハヒフヘホマミムメモヤユヨラリルレロワヲン" +
        "ガギグゲゴザジズゼゾダヂヅデドバビブベボパピプペポヴヰヱ";
    private const string MovieDigitChars = "０１２３４５６７８９";
    private const string MovieKanjiChars = "日三二一十";
    private const string MovieSymbolChars = "：・．＝＋－＜＞｜゛゜";
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
            if (_columns[x].Active)
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

        if (deficit <= 0)
        {
            _spawnChancePercent = headroomSpawn;
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

    internal void Render(Stream stdout)
    {
        var needed = _width * _height * 72 + _height * 24 + 96;
        if (_renderBuffer.Length < needed)
        {
            if (_renderBuffer.Length > 0)
                ArrayPool<byte>.Shared.Return(_renderBuffer);
            _renderBuffer = ArrayPool<byte>.Shared.Rent(needed);
        }

        _palette.WriteFrame(stdout, _grid, _width, _height, _renderBuffer, _frameNumber);
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
        _columns = ArrayPool<ColumnState>.Shared.Rent(_width);
        Array.Clear(_grid, 0, _width * _height);

        for (var x = 0; x < _width; x++)
        {
            _columns[x] = default;
            if (IsStreamColumn(x) && RollPercent(_activeChancePercent))
                ActivateColumn(x);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool RollPercent(int chance) =>
        chance >= 100 || (chance > 0 && _rng.Next(100) < chance);

    private void UpdateColumn(int x)
    {
        ref var column = ref _columns[x];
        if (!column.Active)
        {
            if (_spawnsThisFrame < EngineConstants.MaxSpawnsPerFrame &&
                RollPercent(_spawnChancePercent))
            {
                ActivateColumn(x);
                _spawnsThisFrame++;
            }
            return;
        }

        var speed = column.Speed;
        ShiftColumnDown(x, speed);
        column.HeadY += speed;

        var trailTop = column.HeadY - column.TrailLength + 1;
        if (trailTop > _height - 1)
        {
            ClearColumn(x);
            column.Active = false;
            return;
        }

        var headY = column.HeadY;
        var clearAboveEnd = Math.Min(trailTop, _height);
        for (var y = 0; y < clearAboveEnd; y++)
            ClearDisplayCells(x, y);

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
            var envelope = TrailEnvelope(dist, column.TrailLength);
            var brightness = wave * envelope;
            var cursorBoost = (byte)(dist == 0 ? 1 : 0);
            var brightnessByte = (byte)Math.Clamp((int)(brightness * 255), 0, 255);
            SetDisplayGlyph(x, y, cell.Glyph, brightnessByte, cursorBoost);
        }

        for (var y = Math.Max(headY + 1, 0); y < _height; y++)
            ClearDisplayCells(x, y);
    }

    /// <summary>1 at Head, 0 at trail tip. Gamma &lt; 1 keeps mid-trail brighter while the tip still fades out.</summary>
    private static float TrailEnvelope(int distanceFromHead, int trailLength)
    {
        if (trailLength <= 1)
            return 1f;
        var linear = (float)(trailLength - 1 - distanceFromHead) / (trailLength - 1);
        return MathF.Pow(linear, (float)EngineConstants.TrailEnvelopeGamma);
    }

    private void SetDisplayGlyph(int x, int y, char glyph, byte brightness, byte cursorBoost)
    {
        ref var cell = ref _grid[y * _width + x];
        cell.State = (byte)CellState.Glyph;
        cell.Glyph = glyph;
        cell.Brightness = brightness;
        cell.CursorBoost = cursorBoost;
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

    private void ShiftColumnDown(int x, int speed)
    {
        if (speed <= 0)
            return;

        var span = _wideColumns ? 2 : 1;
        var limit = Math.Min(speed, _height);
        for (var y = _height - 1; y >= limit; y--)
        {
            for (var dx = 0; dx < span && x + dx < _width; dx++)
            {
                ref var dest = ref _grid[y * _width + x + dx];
                ref var src = ref _grid[(y - limit) * _width + x + dx];
                dest = src;
            }
        }

        for (var y = 0; y < limit; y++)
            ClearDisplayCells(x, y);
    }

    private void ActivateColumn(int x)
    {
        var minTrail = (int)Math.Round(
            Math.Max(EngineConstants.MinTrailCells, _height * EngineConstants.TrailMinHeightFraction) * _trailLengthBoost);
        var maxTrail = (int)Math.Round(
            Math.Max(minTrail, _height * EngineConstants.TrailMaxHeightFraction) * _trailLengthBoost);
        maxTrail = Math.Min(_height, maxTrail);
        minTrail = Math.Min(minTrail, maxTrail);
        var speed = (byte)_rng.Next(EngineConstants.MinSpeed, EngineConstants.MaxSpeed + 1);

        _columns[x] = new ColumnState
        {
            Active = true,
            HeadY = -_rng.Next(0, Math.Max(1, _height)),
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
    internal static Action Enter(bool rawInput)
    {
        Console.Out.Write("\u001b[?1049h\u001b[?25l");
        Console.Out.Flush();

        Action? disableRaw = null;
        if (rawInput)
            disableRaw = TerminalInput.EnableRawMode();

        return () =>
        {
            disableRaw?.Invoke();
            Console.Out.Write("\u001b[?25h\u001b[?1049l");
            Console.Out.Flush();
        };
    }
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
  matrix --density <0.0-1.0>
  matrix --fps <1-60>
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
  --mode movie  full-width katakana-heavy pool with digits, kanji, symbols (UTF-8 required)

Density:
  --density     Rain density from 0.0 (sparse) to 1.0 (dense).
                Default 0.55 (ascii-matrix / single). Default 0.7 (movie).
                Movie mode applies a 1.5x effective boost (capped at 1.0).

Timing:
  --fps         Frames per second (1-60). Default 14.
                Higher values make rain fall faster in real time (1-2 cells per frame).

Colors:
  Hex (#RGB or #RRGGBB) or 16-color names (black, green, darkgreen, ...).
  Defaults: --bg #000000 --head #FFFFFF --bright #30FF58 --dim #00AA1C
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
  matrix --bright #0F0 --dim #080

Press any key to exit early. Ctrl+C also exits cleanly.

""";
}
