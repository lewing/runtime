// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Microsoft.NET.WebAssembly.Binary;
using Microsoft.NET.WebAssembly.Linker;
using Microsoft.NET.WebAssembly.Validation;

namespace Microsoft.WebAssembly.Build.Tasks;

/// <summary>
/// Links WebAssembly object files, modules and archives in-process with the managed linker, as a replacement
/// for invoking wasm-ld. Properties are translated to wasm-ld options and parsed by the same
/// <see cref="WasmLdCommandLine"/> used by the <c>wasm-link</c> tool, so both behave identically.
/// </summary>
public class WasmLink : Task
{
    /// <summary>Input <c>.o</c>, <c>.wasm</c> and <c>.a</c> files.</summary>
    [NotNull]
    [Required]
    public ITaskItem[]? InputFiles { get; set; }

    /// <summary>Output module path.</summary>
    [NotNull]
    [Required]
    public string? OutputFile { get; set; }

    /// <summary>Entry point symbol (<c>--entry</c>). When empty, no entry point is set (<c>--no-entry</c>).</summary>
    public string? EntryPoint { get; set; }

    /// <summary>Symbols to export (<c>--export</c>).</summary>
    public string[] ExportedSymbols { get; set; } = [];

    /// <summary>Symbols to export only if defined (<c>--export-if-defined</c>).</summary>
    public string[] ExportIfDefinedSymbols { get; set; } = [];

    public bool AllowUndefined { get; set; }
    public bool GcSections { get; set; } = true;
    public bool ImportMemory { get; set; }
    public bool ExportMemory { get; set; }
    public bool SharedMemory { get; set; }
    public bool StackFirst { get; set; }
    public bool ExportTable { get; set; }
    public bool GrowableTable { get; set; }
    public bool StripDebug { get; set; }
    public bool StripAll { get; set; }

    /// <summary>Initial memory in bytes (<c>--initial-memory</c>).</summary>
    public string? InitialMemory { get; set; }

    /// <summary>Maximum memory in bytes (<c>--max-memory</c>).</summary>
    public string? MaxMemory { get; set; }

    /// <summary>Address where data starts (<c>--global-base</c>).</summary>
    public string? GlobalBase { get; set; }

    /// <summary>Stack size in bytes (<c>-z stack-size</c>).</summary>
    public string? StackSize { get; set; }

    /// <summary>First table index used for function pointers (<c>--table-base</c>).</summary>
    public string? TableBase { get; set; }

    /// <summary>Additional raw wasm-ld arguments, including <c>@response</c> files.</summary>
    public string[] ExtraArguments { get; set; } = [];

    public override bool Execute()
    {
        WasmLdCommandLine commandLine;
        try
        {
            commandLine = WasmLdCommandLine.Parse(BuildArguments());
        }
        catch (WasmLdArgumentException ex)
        {
            Log.LogError(ex.Message);
            return false;
        }

        try
        {
            WasmLdDriver.Run(
                commandLine,
                log: message => Log.LogMessage(MessageImportance.Normal, message),
                warn: message => Log.LogWarning(message));
        }
        catch (Exception ex) when (ex is WasmLdArgumentException or LinkerException or WasmFormatException or WasmValidationException or IOException or InvalidDataException)
        {
            Log.LogError($"Linking '{OutputFile}' failed: {ex.Message}");
        }

        return !Log.HasLoggedErrors;
    }

    internal List<string> BuildArguments()
    {
        var args = new List<string> { "-o", OutputFile };

        if (string.IsNullOrEmpty(EntryPoint))
            args.Add("--no-entry");
        else
            args.Add($"--entry={EntryPoint}");

        foreach (string symbol in ExportedSymbols)
            args.Add($"--export={symbol}");
        foreach (string symbol in ExportIfDefinedSymbols)
            args.Add($"--export-if-defined={symbol}");

        AddFlag(args, AllowUndefined, "--allow-undefined");
        args.Add(GcSections ? "--gc-sections" : "--no-gc-sections");
        AddFlag(args, ImportMemory, "--import-memory");
        AddFlag(args, ExportMemory, "--export-memory");
        AddFlag(args, SharedMemory, "--shared-memory");
        AddFlag(args, StackFirst, "--stack-first");
        AddFlag(args, ExportTable, "--export-table");
        AddFlag(args, GrowableTable, "--growable-table");
        AddFlag(args, StripDebug, "--strip-debug");
        AddFlag(args, StripAll, "--strip-all");

        AddValue(args, InitialMemory, "--initial-memory");
        AddValue(args, MaxMemory, "--max-memory");
        AddValue(args, GlobalBase, "--global-base");
        AddValue(args, TableBase, "--table-base");
        if (!string.IsNullOrEmpty(StackSize))
            args.Add($"-zstack-size={StackSize}");

        args.AddRange(ExtraArguments);

        foreach (ITaskItem item in InputFiles)
            args.Add(item.ItemSpec);

        return args;
    }

    private static void AddFlag(List<string> args, bool value, string flag)
    {
        if (value)
            args.Add(flag);
    }

    private static void AddValue(List<string> args, string? value, string option)
    {
        if (!string.IsNullOrEmpty(value))
            args.Add($"{option}={value}");
    }
}
