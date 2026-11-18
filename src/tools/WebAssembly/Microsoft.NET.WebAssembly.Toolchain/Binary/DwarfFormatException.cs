// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Binary;

/// <summary>
/// Thrown when a DWARF section is malformed: a length or count runs past its section, unit or
/// header, a reserved <c>unit_length</c> is used, or a value cannot be decoded. Readers that
/// cannot make progress throw this rather than looping, stopping silently, or reading garbage.
/// </summary>
/// <remarks>
/// DWARF lives in custom sections, so a malformed DWARF section never invalidates the module.
/// The line, type and compile-unit readers recover per unit: a malformed unit is reported and skipped, and only
/// a bad <c>unit_length</c> (which leaves no next unit to resume at) ends the parse.
/// <see cref="ModuleReader"/> keeps the good units and records one
/// <see cref="Module.WasmModule.MetadataDiagnostics"/> entry per bad unit (strict mode throws
/// <see cref="WasmMetadataException"/> with this exception as its inner exception).
/// </remarks>
public sealed class DwarfFormatException : WasmFormatException
{
    /// <summary>Creates the exception; the message is <c>"{section}: {detail}"</c>.</summary>
    public DwarfFormatException(string section, long offset, string detail)
        : base($"{section}: {detail}")
    {
        Section = section;
        Offset = offset;
        Detail = detail;
    }

    /// <summary>Creates the exception, wrapping the runtime exception that exposed the malformation.</summary>
    public DwarfFormatException(string section, long offset, string detail, Exception inner)
        : base($"{section}: {detail}", inner)
    {
        Section = section;
        Offset = offset;
        Detail = detail;
    }

    /// <summary>The DWARF section that is malformed, e.g. <c>.debug_line</c>.</summary>
    public string Section { get; }

    /// <summary>The section offset of the malformed field (or, for a unit-level failure, of its unit).</summary>
    public long Offset { get; }

    /// <summary>The message without the section prefix.</summary>
    public string Detail { get; }
}
