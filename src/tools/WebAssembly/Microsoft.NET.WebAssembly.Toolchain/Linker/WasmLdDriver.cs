// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.NET.WebAssembly.Binary;
using Microsoft.NET.WebAssembly.Module;

namespace Microsoft.NET.WebAssembly.Linker;

/// <summary>An input module, a display name identifying where it came from, and whether it is a lazily loaded archive member.</summary>
public readonly record struct WasmLinkInput(string DisplayName, WasmModule Module, bool IsArchiveMember = false);

/// <summary>
/// Runs a link described by a <see cref="WasmLdCommandLine"/>: loads inputs, links, validates the result and writes it.
/// </summary>
public static class WasmLdDriver
{
    /// <summary>Links and writes <see cref="WasmLdCommandLine.OutputPath"/>.</summary>
    /// <param name="commandLine">Parsed command line.</param>
    /// <param name="log">Receives informational messages; per-input details only when <see cref="WasmLdCommandLine.Verbose"/> is set.</param>
    /// <param name="warn">Receives warnings, such as accepted options that have no effect.</param>
    /// <returns>The validated output module.</returns>
    /// <exception cref="WasmLdArgumentException">No output or no inputs were specified.</exception>
    /// <exception cref="LinkerException">Linking failed.</exception>
    public static WasmModule Run(WasmLdCommandLine commandLine, Action<string>? log = null, Action<string>? warn = null)
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
                log?.Invoke($"Loaded {input.DisplayName}: {input.Module.Functions.Count} function(s), {input.Module.LinkingSection?.Symbols.Count ?? 0} symbol(s).");
        }

        byte[] bytes = Link(inputs, commandLine.Options);
        WasmModule result = ModuleReader.ReadModuleAndValidate(new MemoryStream(bytes));

        string outputPath = Path.GetFullPath(commandLine.OutputPath);
        string? outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDirectory))
            Directory.CreateDirectory(outputDirectory);
        File.WriteAllBytes(outputPath, bytes);

        log?.Invoke($"Linked {inputs.Count} module(s) into '{outputPath}' ({bytes.Length} bytes).");
        return result;
    }

    /// <summary>Links already-loaded inputs and returns the encoded module.</summary>
    public static byte[] Link(IReadOnlyList<WasmLinkInput> inputs, LinkerOptions options)
    {
        var linkerInputs = new List<LinkerInput>(inputs.Count);
        foreach (WasmLinkInput input in inputs)
            linkerInputs.Add(new LinkerInput(input.Module, input.DisplayName, input.IsArchiveMember));
        return new WasmLinker().LinkToBinary(linkerInputs, options);
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
            {
                foreach ((WasmModule module, string label) in ArArchiveReader.ReadWasmModules(stream, path))
                    inputs.Add(new WasmLinkInput(label, module, IsArchiveMember: !wholeArchive));
            }
            else
            {
                inputs.Add(new WasmLinkInput(path, ModuleReader.ReadModule(stream)));
            }
        }

        return inputs;
    }
}
