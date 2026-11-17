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
        --growable-table, --strip-debug, --strip-all, --compress-relocations, -L<dir>, -l<lib>.

        Component output (wasm-component-ld compatible):
          --component                 wrap the linked core module into a component
          --component-type <file>     extra component-type (WIT world) payload
          --adapt [NAME=]<file>       adapter module for core imports from NAME
          --wasi-adapter <kind>       command|reactor|proxy|none (no built-in adapters; use --adapt)
          --skip-wit-component        emit the core module
          --wasm-ld-path <path>       accepted and ignored

        Archives (llvm-ar compatible, GNU format, deterministic):
          wasm-link ar [-]<r|q>[cSsDuv] <archive> <files...>
        """);
    return args.Length == 0 ? 1 : 0;
}

try
{
    if (args[0] == "ar")
    {
        foreach (string warning in WasmArCommandLine.Run(args[1..]))
            Console.Error.WriteLine($"wasm-link: warning: {warning}");
        return 0;
    }

    WasmComponentLdCommandLine commandLine = WasmComponentLdCommandLine.Parse(args);
    commandLine.Run(
        log: commandLine.WasmLd.Verbose ? Console.WriteLine : null,
        warn: static message => Console.Error.WriteLine($"wasm-link: warning: {message}"));
    return 0;
}
catch (Exception ex) when (ex is WasmLdArgumentException or LinkerException or WasmFormatException or WasmValidationException or IOException or InvalidDataException or NotSupportedException or FormatException)
{
    Console.Error.WriteLine($"wasm-link: error: {ex.Message}");
    return 1;
}
