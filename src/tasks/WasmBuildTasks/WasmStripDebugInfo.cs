// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Microsoft.NET.WebAssembly.Binary;

namespace Microsoft.WebAssembly.Build.Tasks;

/// <summary>
/// Removes DWARF (<c>.debug_*</c>) and/or <c>name</c> custom sections from a WebAssembly module, as a managed
/// replacement for a post-link <c>wasm-opt --strip-dwarf</c> pass that runs no optimizations. Code is copied
/// unchanged, so unlike wasm-opt the padded relocation LEBs left by wasm-ld are not shrunk.
/// </summary>
public class WasmStripDebugInfo : Task
{
    [NotNull]
    [Required]
    public string? InputFile { get; set; }

    /// <summary>Output path; defaults to rewriting <see cref="InputFile"/> in place.</summary>
    public string? OutputFile { get; set; }

    public bool StripDwarf { get; set; } = true;

    public bool StripNames { get; set; } = true;

    public override bool Execute()
    {
        string output = string.IsNullOrEmpty(OutputFile) ? InputFile : OutputFile;
        try
        {
            long before = new FileInfo(InputFile).Length;
            int removed = WasmSections.StripDebugInfo(InputFile, output, StripDwarf, StripNames);
            Log.LogMessage(MessageImportance.Normal,
                $"Removed {removed} section(s) from '{InputFile}': {before} -> {new FileInfo(output).Length} bytes.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WasmFormatException)
        {
            Log.LogError($"Failed to strip debug information from '{InputFile}': {ex.Message}");
        }

        return !Log.HasLoggedErrors;
    }
}
