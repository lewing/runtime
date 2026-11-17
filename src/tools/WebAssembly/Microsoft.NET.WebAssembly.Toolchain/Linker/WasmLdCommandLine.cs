// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text;

namespace Microsoft.NET.WebAssembly.Linker;

/// <summary>
/// Parses a wasm-ld compatible command line into <see cref="LinkerOptions"/> plus the input and output paths.
/// Used by dotnet/runtime's <c>wasm-link</c> tool and <c>WasmLink</c> task; <c>nesm link</c> uses its response-file
/// expansion (<see cref="ExpandResponseFiles"/>).
/// </summary>
/// <remarks>
/// Options accept both the <c>--name=value</c> and <c>--name value</c> spellings. Options that wasm-ld accepts but that
/// have no effect on this linker's output are recorded in <see cref="Warnings"/>; options that would change the output
/// in a way this linker cannot honor are rejected with <see cref="WasmLdArgumentException"/>.
/// </remarks>
public sealed class WasmLdCommandLine
{
    private WasmLdCommandLine(LinkerOptions options, string? outputPath, List<WasmLdInputFile> inputs, List<string> warnings, bool verbose)
    {
        Options = options;
        OutputPath = outputPath;
        Inputs = inputs;
        InputFiles = inputs.ConvertAll(static i => i.Path);
        Warnings = warnings;
        Verbose = verbose;
    }

    /// <summary>Linker options derived from the command line.</summary>
    public LinkerOptions Options { get; }

    /// <summary>Output path from <c>-o</c>, or <see langword="null"/> if none was given.</summary>
    public string? OutputPath { get; }

    /// <summary>Resolved input files (<c>.o</c>, <c>.wasm</c>, <c>.a</c>) in command-line order, including <c>-l</c> libraries.</summary>
    public IReadOnlyList<string> InputFiles { get; }

    /// <summary><see cref="InputFiles"/> with whether each appeared inside a <c>--whole-archive</c> region.</summary>
    public IReadOnlyList<WasmLdInputFile> Inputs { get; }

    /// <summary>Accepted options that were ignored or only approximated.</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>Whether <c>--verbose</c> was specified.</summary>
    public bool Verbose { get; }

    /// <summary>Parses <paramref name="args"/>, expanding <c>@response</c> files.</summary>
    /// <exception cref="WasmLdArgumentException">An option is unknown, unsupported, or malformed.</exception>
    public static WasmLdCommandLine Parse(IEnumerable<string> args)
    {
        string[] argv = ExpandResponseFiles(args).ToArray();

        string? outputPath = null;

        // As in wasm-ld, the last of --entry/--no-entry wins and a final link defaults to _start.
        string? entryPoint = "_start";
        bool allowUndefined = false;
        bool verbose = false;
        bool gcSections = true;
        bool printGcSections = false;
        bool stripDebug = false;
        bool noNameSection = false;
        bool stripAll = false;
        bool compressRelocations = false;
        bool coalesceSegments = true;
        bool exportAll = false;
        bool importMemory = false;
        bool exportMemory = false;
        bool sharedMemory = false;
        long? initialMemory = null;
        long? maxMemory = null;
        uint? globalBase = null;
        // null selects the linker's default (stack first for object files, as in LLD 22).
        bool? stackFirst = null;
        bool wholeArchive = false;
        uint stackSize = 65536;
        bool exportTable = false;
        bool growableTable = false;
        bool importTable = false;
        uint? tableBase = null;
        var exports = new List<string>();
        var exportIfDefined = new List<string>();
        var inputs = new List<WasmLdInputFile>();
        var libraryPaths = new List<string>();
        var warnings = new List<string>();

        for (int i = 0; i < argv.Length; i++)
        {
            string arg = argv[i];

            if (arg.Length == 0)
                continue;

            if (arg[0] != '-' || arg == "-")
            {
                inputs.Add(new WasmLdInputFile(arg, wholeArchive));
                continue;
            }

            // Short options that take a value either attached (-ofoo, -Ldir, -lc) or as the next argument.
            if (arg.Length >= 2 && arg[1] is 'o' or 'L' or 'l' or 'z' or 'm' && !arg.StartsWith("--", StringComparison.Ordinal))
            {
                char flag = arg[1];
                string value = arg.Length > 2 ? arg[2..] : NextValue(argv, ref i, arg);
                switch (flag)
                {
                    case 'o':
                        outputPath = value;
                        break;
                    case 'L':
                        libraryPaths.Add(value);
                        break;
                    case 'l':
                        inputs.Add(new WasmLdInputFile(ResolveLibrary(value, libraryPaths), wholeArchive));
                        break;
                    case 'm':
                        if (value != "wasm32")
                            throw new WasmLdArgumentException($"Unsupported emulation '-m {value}'; only wasm32 is supported.");
                        break;
                    case 'z':
                        ParseZOption(value, ref stackSize);
                        break;
                }
                continue;
            }

            // wasm-ld accepts single-dash spellings of long options.
            string name = !arg.StartsWith("--", StringComparison.Ordinal) && arg.Length > 2 ? "-" + arg : arg;
            string? inlineValue = null;
            int eq = name.IndexOf('=');
            if (eq > 0)
            {
                inlineValue = name[(eq + 1)..];
                name = name[..eq];
            }

            string Value() => inlineValue ?? NextValue(argv, ref i, name);

            switch (name)
            {
                case "--output":
                    outputPath = Value();
                    break;
                case "--entry":
                    entryPoint = Value();
                    break;
                case "--no-entry":
                    entryPoint = null;
                    break;
                case "--allow-undefined":
                    allowUndefined = true;
                    break;
                case "--export":
                    exports.Add(Value());
                    break;
                case "--export-all":
                    exportAll = true;
                    break;
                case "--export-if-defined":
                    exportIfDefined.Add(Value());
                    break;
                case "--export-table":
                    exportTable = true;
                    break;
                case "--growable-table":
                    growableTable = true;
                    break;
                case "--import-table":
                    importTable = true;
                    break;
                case "--table-base":
                    tableBase = ParseUInt32(name, Value());
                    break;
                case "--verbose":
                    verbose = true;
                    break;
                case "--gc-sections":
                    gcSections = true;
                    break;
                case "--no-gc-sections":
                    gcSections = false;
                    break;
                case "--print-gc-sections":
                    printGcSections = true;
                    break;
                case "--strip-debug":
                case "-S":
                    stripDebug = true;
                    break;
                case "--strip-all":
                case "-s":
                    stripAll = true;
                    break;
                case "--no-name-section":
                    noNameSection = true;
                    break;
                case "--coalesce-segments":
                    coalesceSegments = true;
                    break;
                case "--no-coalesce-segments":
                    coalesceSegments = false;
                    break;
                case "--import-memory":
                    importMemory = true;
                    break;
                case "--export-memory":
                    exportMemory = true;
                    break;
                case "--shared-memory":
                    sharedMemory = true;
                    break;
                case "--initial-memory":
                    initialMemory = ParseInt64(name, Value());
                    break;
                case "--max-memory":
                    maxMemory = ParseInt64(name, Value());
                    break;
                case "--global-base":
                    globalBase = ParseUInt32(name, Value());
                    break;
                case "--stack-first":
                    stackFirst = true;
                    break;
                case "--no-stack-first":
                    stackFirst = false;
                    break;
                case "--stack-size":
                    stackSize = ParseUInt32(name, Value());
                    break;

                // Already applied by ExpandResponseFiles.
                case "--rsp-quoting":
                    _ = Value();
                    break;

                case "--whole-archive":
                    wholeArchive = true;
                    break;
                case "--no-whole-archive":
                    wholeArchive = false;
                    break;

                case "--compress-relocations":
                    compressRelocations = true;
                    break;

                // Accepted but without effect on a final (non-relocatable) link.
                case "--error-limit":
                    _ = Value();
                    break;
                case "--fatal-warnings":
                case "--no-fatal-warnings":
                case "--color-diagnostics":
                case "--no-color-diagnostics":
                    warnings.Add($"Ignoring '{name}'.");
                    break;

                default:
                    throw new WasmLdArgumentException($"Unsupported option '{arg}'.");
            }
        }

        var options = new LinkerOptions
        {
            EntryPoint = entryPoint,
            AllowUndefined = allowUndefined,
            Exports = exports,
            ExportAll = exportAll,
            GcSections = gcSections,
            PrintGcSections = printGcSections,
            StripDebug = stripDebug,
            NoNameSection = noNameSection,
            StripAll = stripAll,
            CompressRelocations = compressRelocations,
            CoalesceSegments = coalesceSegments,
            ImportMemory = importMemory,
            ExportMemory = exportMemory,
            InitialMemory = initialMemory,
            MaxMemory = maxMemory,
            SharedMemory = sharedMemory,
            GlobalBase = globalBase,
            StackFirst = stackFirst,
            StackSize = stackSize,
            ExportTable = exportTable,
            GrowableTable = growableTable,
            ImportTable = importTable,
            TableBase = tableBase,
            ExportIfDefined = exportIfDefined,
        };

        return new WasmLdCommandLine(options, outputPath, inputs, warnings, verbose);
    }

    /// <summary>
    /// Expands <c>@file</c> arguments. As in wasm-ld, response files use Windows command-line quoting on Windows
    /// hosts and GNU quoting elsewhere, unless <c>--rsp-quoting=windows|posix</c> is given.
    /// </summary>
    public static IEnumerable<string> ExpandResponseFiles(IEnumerable<string> args)
    {
        List<string> argList = args.ToList();
        bool windowsQuoting = OperatingSystem.IsWindows();
        for (int i = 0; i < argList.Count; i++)
        {
            string arg = argList[i];
            string? style = arg.StartsWith("--rsp-quoting=", StringComparison.Ordinal) || arg.StartsWith("-rsp-quoting=", StringComparison.Ordinal)
                ? arg[(arg.IndexOf('=') + 1)..]
                : arg is "--rsp-quoting" or "-rsp-quoting" && i + 1 < argList.Count ? argList[++i] : null;
            if (style is not null)
            {
                windowsQuoting = style switch
                {
                    "windows" => true,
                    "posix" => false,
                    _ => throw new WasmLdArgumentException($"Invalid response file quoting style '{style}'."),
                };
            }
        }

        var expanded = new List<string>(argList.Count);
        Expand(argList, windowsQuoting, [], expanded);
        return expanded;

        // `active` holds the response files being expanded, so a file that includes itself, directly or indirectly,
        // is reported (as LLVM's "recursive expansion") instead of overflowing the stack. A file may still appear
        // more than once side by side.
        static void Expand(IEnumerable<string> args, bool windowsQuoting, HashSet<string> active, List<string> expanded)
        {
            foreach (string arg in args)
            {
                if (arg.Length > 1 && arg[0] == '@')
                {
                    string path = arg[1..];
                    if (!File.Exists(path))
                        throw new WasmLdArgumentException($"Response file not found: {path}");
                    string fullPath = Path.GetFullPath(path);
                    if (!active.Add(fullPath))
                        throw new WasmLdArgumentException($"recursive expansion of response file '{path}'.");

                    string text = File.ReadAllText(path);
                    List<string> tokens = windowsQuoting ? TokenizeWindowsResponseFile(text) : TokenizeResponseFile(text);
                    Expand(tokens, windowsQuoting, active, expanded);
                    active.Remove(fullPath);
                }
                else
                {
                    expanded.Add(arg);
                }
            }
        }
    }

    /// <summary>
    /// Tokenizes like <c>CommandLineToArgvW</c>: backslashes are literal except before a double quote, where 2n
    /// backslashes produce n and a quote toggles quoting, and 2n+1 backslashes produce n and a literal quote.
    /// </summary>
    internal static List<string> TokenizeWindowsResponseFile(string text)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        bool inToken = false;
        bool inQuotes = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\\')
            {
                int backslashes = 0;
                while (i < text.Length && text[i] == '\\')
                {
                    backslashes++;
                    i++;
                }

                if (i < text.Length && text[i] == '"')
                {
                    current.Append('\\', backslashes / 2);
                    if (backslashes % 2 == 1)
                        current.Append('"');
                    else
                        inQuotes = !inQuotes;
                }
                else
                {
                    current.Append('\\', backslashes);
                    i--;
                }
                inToken = true;
            }
            else if (c == '"')
            {
                if (inQuotes && i + 1 < text.Length && text[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
                inToken = true;
            }
            else if (!inQuotes && char.IsWhiteSpace(c))
            {
                if (inToken)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    inToken = false;
                }
            }
            else
            {
                current.Append(c);
                inToken = true;
            }
        }

        if (inToken)
            tokens.Add(current.ToString());

        return tokens;
    }

    internal static List<string> TokenizeResponseFile(string text)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        bool inToken = false;
        char quote = '\0';

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\\' && i + 1 < text.Length)
            {
                current.Append(text[++i]);
                inToken = true;
            }
            else if (quote != '\0')
            {
                if (c == quote)
                    quote = '\0';
                else
                    current.Append(c);
            }
            else if (c is '"' or '\'')
            {
                quote = c;
                inToken = true;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (inToken)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    inToken = false;
                }
            }
            else
            {
                current.Append(c);
                inToken = true;
            }
        }

        if (inToken)
            tokens.Add(current.ToString());

        return tokens;
    }

    private static void ParseZOption(string value, ref uint stackSize)
    {
        const string StackSizePrefix = "stack-size=";
        if (value.StartsWith(StackSizePrefix, StringComparison.Ordinal))
        {
            stackSize = ParseUInt32("-z stack-size", value[StackSizePrefix.Length..]);
            return;
        }

        throw new WasmLdArgumentException($"Unsupported option '-z {value}'.");
    }

    private static string ResolveLibrary(string name, List<string> libraryPaths)
    {
        foreach (string dir in libraryPaths)
        {
            string candidate = Path.Combine(dir, $"lib{name}.a");
            if (File.Exists(candidate))
                return candidate;
        }

        throw new WasmLdArgumentException($"Unable to find library -l{name}.");
    }

    private static string NextValue(string[] argv, ref int i, string option)
    {
        if (i + 1 >= argv.Length)
            throw new WasmLdArgumentException($"Missing value for '{option}'.");
        return argv[++i];
    }

    private static uint ParseUInt32(string option, string value)
    {
        long result = ParseInt64(option, value);
        if (result > uint.MaxValue)
            throw new WasmLdArgumentException($"Invalid value '{value}' for '{option}'.");
        return (uint)result;
    }

    // Memory sizes are 64-bit so the 4 GiB wasm32 maximum (4294967296) is accepted; the linker validates the range.
    private static long ParseInt64(string option, string value)
    {
        bool ok = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? long.TryParse(value.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out long result)
            : long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result);

        if (!ok || result < 0)
            throw new WasmLdArgumentException($"Invalid value '{value}' for '{option}'.");
        return result;
    }
}

/// <summary>An input file and whether it appeared inside a <c>--whole-archive</c> region.</summary>
public readonly record struct WasmLdInputFile(string Path, bool WholeArchive);

/// <summary>A wasm-ld command line could not be parsed.</summary>
public sealed class WasmLdArgumentException : Exception
{
    public WasmLdArgumentException(string message) : base(message) { }
}
