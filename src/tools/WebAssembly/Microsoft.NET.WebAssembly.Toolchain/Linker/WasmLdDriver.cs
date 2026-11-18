// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.NET.WebAssembly.Binary;
using Microsoft.NET.WebAssembly.Module;

namespace Microsoft.NET.WebAssembly.Linker;

/// <summary>An input module, a display name identifying where it came from, and whether it is a lazily loaded archive member.</summary>
public readonly record struct WasmLinkInput(string DisplayName, WasmModule Module, bool IsArchiveMember = false)
{
    /// <summary>
    /// For a lazily read archive member, parses the complete module if the link extracts it; <see cref="Module"/> then
    /// holds only what decides extraction (see <see cref="LinkerInput.LoadModule"/>). Null when <see cref="Module"/>
    /// is complete.
    /// </summary>
    public Func<WasmModule>? LoadModule { get; init; }
}

/// <summary>
/// Runs a link described by a <see cref="WasmLdCommandLine"/>: loads inputs, links, validates the result (unless
/// <see cref="LinkerOptions.ValidateOutput"/> is off) and writes it.
/// </summary>
public static class WasmLdDriver
{
    /// <summary>Links and writes <see cref="WasmLdCommandLine.OutputPath"/>.</summary>
    /// <param name="commandLine">Parsed command line.</param>
    /// <param name="log">Receives informational messages; per-input details only when <see cref="WasmLdCommandLine.Verbose"/> is set.</param>
    /// <param name="warn">Receives warnings, such as accepted options that have no effect.</param>
    /// <returns>
    /// The output module as validation read it back, or <see langword="null"/> when
    /// <see cref="LinkerOptions.ValidateOutput"/> is off.
    /// </returns>
    /// <exception cref="WasmLdArgumentException">No output or no inputs were specified.</exception>
    /// <exception cref="LinkerException">Linking failed.</exception>
    public static WasmModule? Run(WasmLdCommandLine commandLine, Action<string>? log = null, Action<string>? warn = null)
    {
        if (string.IsNullOrEmpty(commandLine.OutputPath))
            throw new WasmLdArgumentException("No output file specified; use -o.");
        if (commandLine.InputFiles.Count == 0)
            throw new WasmLdArgumentException("No input files.");

        foreach (string warning in commandLine.Warnings)
            warn?.Invoke(warning);

        List<WasmLinkInput> inputs = LoadInputs(commandLine.Inputs);
        if (commandLine.Verbose)
        {
            foreach (WasmLinkInput input in inputs)
            {
                // A lazily read member's functions are not read unless it is extracted.
                log?.Invoke(input.LoadModule is null
                    ? $"Loaded {input.DisplayName}: {input.Module.Functions.Count} function(s), {input.Module.LinkingSection?.Symbols.Count ?? 0} symbol(s)."
                    : $"Loaded {input.DisplayName}: archive member, read lazily, {input.Module.LinkingSection?.Symbols.Count ?? 0} symbol(s).");
            }
        }

        // The linker validates the bytes it produces (unless told not to); that module is the result. The output file
        // is opened only once the link has succeeded, so a failed link leaves an existing output alone.
        WasmLinker.LinkedOutput output = new WasmLinker().LinkOutput(ToLinkerInputs(inputs), commandLine.Options);

        string outputPath = Path.GetFullPath(commandLine.OutputPath);
        string? outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDirectory))
            Directory.CreateDirectory(outputDirectory);
        WriteOutput(outputPath, output);

        log?.Invoke($"Linked {inputs.Count} module(s) into '{outputPath}' ({output.Length} bytes).");
        return output.Validated;
    }

    private static void WriteOutput(string outputPath, WasmLinker.LinkedOutput output) => WriteOutput(outputPath, output.WriteTo);

    /// <summary>
    /// Creates <paramref name="outputPath"/> and lets <paramref name="write"/> fill it. Unvalidated output is streamed
    /// from the linked model, so a write can fail part-way; the partial file is then deleted rather than left looking
    /// linked. A failure to open the file is reported as it is, and leaves an existing file alone.
    /// </summary>
    internal static void WriteOutput(string outputPath, Action<Stream> write)
    {
        var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        try
        {
            using (stream)
                write(stream);
        }
        catch
        {
            TryDelete(outputPath);
            throw;
        }
    }

    // Best effort: the error being reported is the write's, not the cleanup's.
    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Links already-loaded inputs and returns the encoded module.</summary>
    /// <remarks>The output is validated before it is returned, unless <see cref="LinkerOptions.ValidateOutput"/> is off.</remarks>
    public static byte[] Link(IReadOnlyList<WasmLinkInput> inputs, LinkerOptions options) =>
        new WasmLinker().LinkToBinary(ToLinkerInputs(inputs), options);

    private static List<LinkerInput> ToLinkerInputs(IReadOnlyList<WasmLinkInput> inputs)
    {
        var linkerInputs = new List<LinkerInput>(inputs.Count);
        foreach (WasmLinkInput input in inputs)
            linkerInputs.Add(new LinkerInput(input.Module, input.DisplayName, input.IsArchiveMember) { LoadModule = input.LoadModule });
        return linkerInputs;
    }

    /// <summary>
    /// Loads <c>.o</c>/<c>.wasm</c> modules and every WebAssembly member of <c>.a</c> archives. Archive members
    /// are linked lazily (only when they resolve an undefined symbol) unless they came from a
    /// <c>--whole-archive</c> region.
    /// </summary>
    public static List<WasmLinkInput> LoadInputs(IEnumerable<WasmLdInputFile> files)
    {
        var inputs = new List<WasmLinkInput>();
        foreach ((string path, bool wholeArchive) in files)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException($"Input file not found: {path}", path);

            using FileStream stream = File.OpenRead(path);
            if (Path.GetExtension(path).Equals(".a", StringComparison.OrdinalIgnoreCase))
                inputs.AddRange(LoadArchive(stream, path, wholeArchive));
            else
                inputs.Add(new WasmLinkInput(path, ModuleReader.ReadModule(stream)));
        }

        return inputs;
    }

    /// <summary>
    /// Loads every WebAssembly member of an archive. Within <c>--whole-archive</c> each member is parsed in full and
    /// always linked; otherwise each is an archive member read lazily, as a linking view with
    /// <see cref="WasmLinkInput.LoadModule"/> to parse it in full if the link extracts it.
    /// </summary>
    public static List<WasmLinkInput> LoadArchive(Stream stream, string? path, bool wholeArchive)
    {
        var inputs = new List<WasmLinkInput>();
        if (wholeArchive)
        {
            foreach ((WasmModule module, string label) in ArArchiveReader.ReadWasmModules(stream, path))
                inputs.Add(new WasmLinkInput(label, module));
            return inputs;
        }

        // Most members of a lazily linked archive are never extracted (about two thirds of the NativeAOT and
        // CoreCLR WASI links), so only the symbols that decide extraction are read up front.
        foreach ((ReadOnlyMemory<byte> bytes, string label) in ArArchiveReader.ReadWasmMembers(stream, path))
        {
            inputs.Add(new WasmLinkInput(label, ModuleReader.ReadLinkingView(bytes), IsArchiveMember: true)
            {
                LoadModule = () => ModuleReader.ParseCoreModule(bytes),
            });
        }

        return inputs;
    }
}
