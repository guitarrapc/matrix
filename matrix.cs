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

var palette = AnsiPalette.Create(options.Colors, useTrueColor);
var engine = new MatrixEngine(pool, palette);

var restore = TerminalSession.Enter(keyExitEnabled);
var exitRequested = false;

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    exitRequested = true;
};

var stdout = Console.OpenStandardOutput();
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

        Thread.Sleep(EngineConstants.FrameDelayMs);
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
    Dim = 1,
    Bright = 2,
    Head = 3,
    /// <summary>Right display cell of a wide glyph; nothing is written when rendering.</summary>
    Continuation = 4,
}

internal static class EngineConstants
{
    internal const int TargetFps = 18;
    internal const int FrameDelayMs = 1000 / TargetFps;
    internal const int SpawnChancePercent = 3;
    internal const int InitialActivePercent = 35;
    internal const int GlyphMutationChance = 8;
    internal const int MinTrailLength = 8;
    internal const int MaxTrailLength = 24;
    internal const int MinSpeed = 1;
    internal const int MaxSpeed = 3;
    internal const int BrightDistance = 2;
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

    internal static CliOptions Parse(string[] args)
    {
        var mode = GlyphMode.AsciiMatrix;
        var hasMovie = false;
        var hasChar = false;
        var singleChar = '0';
        var duration = 5.0;
        var hasDurationFlag = false;
        var hasPositionalDuration = false;
        ColorValue? bg = null, head = null, bright = null, dim = null;

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

        return new CliOptions
        {
            Mode = mode,
            SingleChar = singleChar,
            DurationSeconds = duration,
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
    internal static readonly ColorValue DefaultBright = new(new Rgb(0, 255, 65));
    internal static readonly ColorValue DefaultDim = new(new Rgb(0, 143, 17));

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

internal sealed class AnsiPalette
{
    private readonly byte[] _home = "\u001b[H"u8.ToArray();
    private readonly byte[] _bgRowPrefix;
    private readonly byte[][] _fgByState;
    private readonly byte[] _space = " "u8.ToArray();
    private readonly byte[] _newline = "\n"u8.ToArray();

    private AnsiPalette(byte[] bgRowPrefix, byte[][] fgByState)
    {
        _bgRowPrefix = bgRowPrefix;
        _fgByState = fgByState;
    }

    internal static AnsiPalette Create(ColorOptions colors, bool trueColor)
    {
        if (trueColor)
        {
            return new AnsiPalette(
                BuildTrueColorBgPrefix(ResolveRgb(colors.Background)),
                [
                    Array.Empty<byte>(),
                    BuildTrueColorFgPrefix(ResolveRgb(colors.Dim)),
                    BuildTrueColorFgPrefix(ResolveRgb(colors.Bright)),
                    BuildTrueColorFgPrefix(ResolveRgb(colors.Head)),
                ]);
        }

        return new AnsiPalette(
            BuildAnsi16BgPrefix(ResolveNamed(colors.Background)),
            [
                Array.Empty<byte>(),
                BuildAnsi16FgPrefix(ResolveNamed(colors.Dim)),
                BuildAnsi16FgPrefix(ResolveNamed(colors.Bright)),
                BuildAnsi16FgPrefix(ResolveNamed(colors.Head)),
            ]);
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

    private static byte[] BuildTrueColorSequence(bool isBackground, Rgb rgb)
    {
        Span<byte> scratch = stackalloc byte[64];
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
        return scratch[..pos].ToArray();
    }

    private static byte[] BuildAnsi16BgPrefix(ConsoleColor16 color)
    {
        var code = color <= ConsoleColor16.White ? 40 + (int)color : 100 + ((int)color - 8);
        return BuildAnsi16Sequence(code);
    }

    private static byte[] BuildAnsi16FgPrefix(ConsoleColor16 color)
    {
        var code = color <= ConsoleColor16.White ? 30 + (int)color : 90 + ((int)color - 8);
        return BuildAnsi16Sequence(code);
    }

    private static byte[] BuildAnsi16Sequence(int code)
    {
        Span<byte> scratch = stackalloc byte[16];
        scratch[0] = 0x1B;
        scratch[1] = (byte)'[';
        var pos = 2;
        pos += AppendDecimal(scratch[pos..], code);
        scratch[pos++] = (byte)'m';
        return scratch[..pos].ToArray();
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

    internal void WriteFrame(Stream stdout, ReadOnlySpan<Cell> grid, int width, int height, byte[] buffer)
    {
        var pos = 0;
        AppendBytes(buffer, ref pos, _home);

        for (var y = 0; y < height; y++)
        {
            AppendBytes(buffer, ref pos, _bgRowPrefix);
            var row = y * width;
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

                AppendBytes(buffer, ref pos, _fgByState[cell.State]);
                AppendCharUtf8(buffer, ref pos, cell.Glyph);
            }

            AppendBytes(buffer, ref pos, _newline);
        }

        stdout.Write(buffer.AsSpan(0, pos));
        stdout.Flush();
    }

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

        Span<char> chars = stackalloc char[1];
        chars[0] = glyph;
        pos += Encoding.UTF8.GetBytes(chars, buffer.AsSpan(pos));
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct Cell
{
    internal byte State;
    internal char Glyph;
}

internal sealed class GlyphPool
{
    private readonly string _chars;
    private readonly char _singleChar;
    private readonly bool _single;
    internal readonly bool RequiresWideColumns;

    private GlyphPool(string chars, char singleChar, bool single, bool requiresWideColumns)
    {
        _chars = chars;
        _singleChar = singleChar;
        _single = single;
        RequiresWideColumns = requiresWideColumns;
    }

    internal static GlyphPool Create(GlyphMode mode, char singleChar)
    {
        switch (mode)
        {
            case GlyphMode.Single:
                return new GlyphPool(string.Empty, singleChar, true, requiresWideColumns: false);
            case GlyphMode.Movie:
                EnsureUtf8();
                return new GlyphPool(MovieStreamChars, '\0', false, requiresWideColumns: true);
            default:
                return new GlyphPool(AsciiMatrixChars, '\0', false, requiresWideColumns: false);
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

    internal char Pick(Random rng) =>
        _single ? _singleChar : _chars[rng.Next(_chars.Length)];

    private const string AsciiMatrixChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789@#$%^&*()";
    // Full-width katakana only — half-width Latin/digits break vertical alignment in terminals.
    private const string MovieStreamChars =
        "アイウエオカキクケコサシスセソタチツテトナニヌネノハヒフヘホマミムメモヤユヨラリルレロワヲン" +
        "ガギグゲゴザジズゼゾダヂヅデドバビブベボパピプペポヴヰヱ";
}

internal sealed class MatrixEngine
{
    private readonly GlyphPool _pool;
    private readonly AnsiPalette _palette;
    private readonly bool _wideColumns;
    private readonly Random _rng = new();

    private Cell[] _grid = [];
    private ColumnState[] _columns = [];
    private byte[] _renderBuffer = [];
    private int _width;
    private int _height;

    internal MatrixEngine(GlyphPool pool, AnsiPalette palette)
    {
        _pool = pool;
        _palette = palette;
        _wideColumns = pool.RequiresWideColumns;
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
            Resize(width, height);

        for (var x = 0; x < _width; x++)
        {
            if (_wideColumns && (x & 1) == 1)
                continue;
            UpdateColumn(x);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsStreamColumn(int x) =>
        !_wideColumns || ((x & 1) == 0 && x + 1 < _width);

    internal void Render(Stream stdout)
    {
        var needed = _width * _height * 48 + 64;
        if (_renderBuffer.Length < needed)
        {
            if (_renderBuffer.Length > 0)
                ArrayPool<byte>.Shared.Return(_renderBuffer);
            _renderBuffer = ArrayPool<byte>.Shared.Rent(needed);
        }

        _palette.WriteFrame(stdout, _grid, _width, _height, _renderBuffer);
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
        _grid = ArrayPool<Cell>.Shared.Rent(_width * _height);
        _columns = ArrayPool<ColumnState>.Shared.Rent(_width);
        Array.Clear(_grid, 0, _width * _height);

        for (var x = 0; x < _width; x++)
        {
            _columns[x] = default;
            if (IsStreamColumn(x) && _rng.Next(100) < EngineConstants.InitialActivePercent)
                ActivateColumn(x);
        }
    }

    private void UpdateColumn(int x)
    {
        ref var column = ref _columns[x];
        if (!column.Active)
        {
            if (_rng.Next(100) < EngineConstants.SpawnChancePercent)
                ActivateColumn(x);
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
        for (var y = 0; y < _height; y++)
        {
            if (y < trailTop || y > headY)
            {
                ClearDisplayCells(x, y);
                continue;
            }

            var dist = headY - y;
            ref var cell = ref _grid[y * _width + x];
            if (cell.State is (byte)CellState.Empty or (byte)CellState.Continuation)
                cell.Glyph = _pool.Pick(_rng);

            var state = dist == 0
                ? (byte)CellState.Head
                : dist <= EngineConstants.BrightDistance
                    ? (byte)CellState.Bright
                    : (byte)CellState.Dim;

            if (_rng.Next(100) < EngineConstants.GlyphMutationChance)
                cell.Glyph = _pool.Pick(_rng);

            SetDisplayGlyph(x, y, state, cell.Glyph);
        }
    }

    private void SetDisplayGlyph(int x, int y, byte state, char glyph)
    {
        ref var cell = ref _grid[y * _width + x];
        cell.State = state;
        cell.Glyph = glyph;
        if (_wideColumns && x + 1 < _width)
        {
            ref var cont = ref _grid[y * _width + x + 1];
            cont.State = (byte)CellState.Continuation;
            cont.Glyph = '\0';
        }
    }

    private void ClearDisplayCells(int x, int y)
    {
        ref var cell = ref _grid[y * _width + x];
        cell.State = (byte)CellState.Empty;
        cell.Glyph = '\0';
        if (_wideColumns && x + 1 < _width)
        {
            ref var cont = ref _grid[y * _width + x + 1];
            cont.State = (byte)CellState.Empty;
            cont.Glyph = '\0';
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
        _columns[x] = new ColumnState
        {
            Active = true,
            HeadY = -_rng.Next(0, Math.Max(1, _height / 2)),
            Speed = (byte)_rng.Next(EngineConstants.MinSpeed, EngineConstants.MaxSpeed + 1),
            TrailLength = (byte)_rng.Next(EngineConstants.MinTrailLength, EngineConstants.MaxTrailLength + 1),
        };
    }

    private void ClearColumn(int x)
    {
        for (var y = 0; y < _height; y++)
            ClearDisplayCells(x, y);
    }
}

internal struct ColumnState
{
    internal bool Active;
    internal int HeadY;
    internal byte Speed;
    internal byte TrailLength;
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
  matrix --mode movie
  matrix --bg <color> --head <color> --bright <color> --dim <color>
  matrix --help
  matrix --version

Duration:
  Default 5 seconds. Use 0 or a negative value to run until a key is pressed.

Modes:
  (default)   ascii-matrix character pool
  --char X    single-character mode
  --mode movie  katakana-heavy movie pool (UTF-8 terminal required)

Colors:
  Hex (#RGB or #RRGGBB) or 16-color names (black, green, darkgreen, ...).
  Defaults: --bg #000000 --head #FFFFFF --bright #00FF41 --dim #008F11

Examples:
  matrix
  matrix 10
  matrix --duration 0
  matrix --char '#'
  matrix --mode movie 30
  matrix --bright #0F0 --dim #080

Press any key to exit early. Ctrl+C also exits cleanly.

""";
}
