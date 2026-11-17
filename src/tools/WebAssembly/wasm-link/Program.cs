// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.NET.WebAssembly.Binary;
using Microsoft.NET.WebAssembly.Linker;
using Microsoft.NET.WebAssembly.Validation;

if (args.Length == 0 || args is ["-h" or "--help"])
{
    Console.WriteLine("""
        Usage: wasm-link [options] <inputs...> -o <output>

        A managed, wasm-ld compatible static linker for WebAssembly object files (.o), modules (.wasm)
        and archives (.a). Accepts @response files and the wasm-ld options used by the .NET build,
        e.g. --export=, --export-if-defined=, --allow-undefined, --no-entry, --entry=, --gc-sections,
        --import-memory, --export-memory, --shared-memory, --initial-memory=, --max-memory=,
        --global-base=, --stack-first, -z stack-size=, --table-base=, --export-table,
        --growable-table, --strip-debug, --strip-all, -L<dir>, -l<lib>.
        """);
    return args.Length == 0 ? 1 : 0;
}

try
{
    WasmLdCommandLine commandLine = WasmLdCommandLine.Parse(args);
    WasmLdDriver.Run(
        commandLine,
        log: commandLine.Verbose ? Console.WriteLine : null,
        warn: static message => Console.Error.WriteLine($"wasm-link: warning: {message}"));
    return 0;
}
catch (Exception ex) when (ex is WasmLdArgumentException or LinkerException or WasmFormatException or WasmValidationException or IOException or InvalidDataException)
{
    Console.Error.WriteLine($"wasm-link: error: {ex.Message}");
    return 1;
}
