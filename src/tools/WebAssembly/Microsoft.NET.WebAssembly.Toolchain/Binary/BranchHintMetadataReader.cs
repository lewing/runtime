// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Binary;

using Microsoft.NET.WebAssembly.Module;

internal static class BranchHintMetadataReader
{
    internal const string SectionName = BranchHint.CustomSectionName;

    private sealed record EncodedHint(uint FunctionIndex, uint Offset, BranchHintKind Kind);
    private sealed record EncodedFunctionEntry(
        uint FunctionIndex,
        IReadOnlyList<EncodedHint> Hints);

    private sealed class MetadataReadException(
        WasmMetadataErrorKind kind,
        string message,
        uint? functionIndex = null,
        uint? instructionOffset = null) : Exception(message)
    {
        internal WasmMetadataErrorKind Kind { get; } = kind;
        internal uint? FunctionIndex { get; } = functionIndex;
        internal uint? InstructionOffset { get; } = instructionOffset;
    }

    internal static void Apply(WasmModule module, WasmMetadataValidationMode mode)
    {
        var sections = module.CustomSections
            .Where(static section =>
                string.Equals(section.Name, SectionName, StringComparison.Ordinal))
            .ToArray();

        if (sections.Length == 0)
        {
            module.MetadataDiagnostics = Array.Empty<WasmMetadataDiagnostic>();
            return;
        }

        var diagnostics = new List<WasmMetadataDiagnostic>();
        if (sections.Length > 1)
        {
            Report(
                diagnostics,
                mode,
                WasmMetadataErrorKind.Malformed,
                "at most one branch-hint metadata section may be present");
        }

        if (sections.Any(static section => section.AppearedAfterCode))
        {
            Report(
                diagnostics,
                mode,
                WasmMetadataErrorKind.Malformed,
                "branch-hint metadata section must appear before the code section");
        }

        if (diagnostics.Count != 0)
        {
            module.MetadataDiagnostics = diagnostics;
            return;
        }

        try
        {
            IReadOnlyList<EncodedFunctionEntry> encodedEntries = ParsePayload(sections[0].Content);
            var resolved = Resolve(module, encodedEntries);
            foreach ((WasmFunction function, List<BranchHint> hints) in resolved)
                function.ReplaceBranchHints(hints);
        }
        catch (MetadataReadException ex)
        {
            Report(
                diagnostics,
                mode,
                ex.Kind,
                ex.Message,
                ex.FunctionIndex,
                ex.InstructionOffset);
        }
        catch (WasmFormatException ex)
        {
            Report(
                diagnostics,
                mode,
                WasmMetadataErrorKind.Malformed,
                $"malformed branch-hint payload: {ex.Message}");
        }

        module.MetadataDiagnostics = diagnostics;
    }

    private static IReadOnlyList<EncodedFunctionEntry> ParsePayload(byte[] content)
    {
        using var stream = new MemoryStream(content, writable: false);
        var reader = new WasmBinaryReader(stream);
        uint functionCount = reader.ReadU32Leb128();
        var entries = new List<EncodedFunctionEntry>();
        uint? previousFunctionIndex = null;

        for (uint functionEntry = 0; functionEntry < functionCount; functionEntry++)
        {
            uint functionIndex = reader.ReadU32Leb128();
            if (previousFunctionIndex.HasValue && functionIndex <= previousFunctionIndex.Value)
            {
                throw new MetadataReadException(
                    WasmMetadataErrorKind.Malformed,
                    $"function index {functionIndex} is not strictly greater than preceding index {previousFunctionIndex.Value}",
                    functionIndex);
            }
            previousFunctionIndex = functionIndex;

            uint hintCount = reader.ReadU32Leb128();
            var hints = new List<EncodedHint>();
            uint? previousOffset = null;
            for (uint hintEntry = 0; hintEntry < hintCount; hintEntry++)
            {
                uint offset = reader.ReadU32Leb128();
                if (previousOffset.HasValue && offset <= previousOffset.Value)
                {
                    throw new MetadataReadException(
                        WasmMetadataErrorKind.Malformed,
                        $"instruction offset {offset} is not strictly greater than preceding offset {previousOffset.Value}",
                        functionIndex,
                        offset);
                }
                previousOffset = offset;

                uint payloadSize = reader.ReadU32Leb128();
                if (payloadSize != 1)
                {
                    throw new MetadataReadException(
                        WasmMetadataErrorKind.Malformed,
                        $"branch-hint payload size must be 1, not {payloadSize}",
                        functionIndex,
                        offset);
                }

                byte value = reader.ReadByte();
                if (value > 1)
                {
                    throw new MetadataReadException(
                        WasmMetadataErrorKind.Malformed,
                        $"branch-hint value must be 0 or 1, not {value}",
                        functionIndex,
                        offset);
                }

                hints.Add(new EncodedHint(
                    functionIndex,
                    offset,
                    (BranchHintKind)value));
            }
            entries.Add(new EncodedFunctionEntry(functionIndex, hints));
        }

        if (reader.HasMore)
        {
            throw new MetadataReadException(
                WasmMetadataErrorKind.Malformed,
                "unexpected trailing bytes in branch-hint payload");
        }

        return entries;
    }

    private static List<(WasmFunction Function, List<BranchHint> Hints)> Resolve(
        WasmModule module,
        IReadOnlyList<EncodedFunctionEntry> encodedEntries)
    {
        uint importedFunctionCount = 0;
        foreach (var import in module.Imports)
        {
            if (import.Descriptor is ImportDescriptor.Func)
                importedFunctionCount++;
        }

        var byFunction = new Dictionary<int, List<BranchHint>>();
        foreach (EncodedFunctionEntry entry in encodedEntries)
        {
            uint absoluteFunctionIndex = entry.FunctionIndex;
            if (absoluteFunctionIndex < importedFunctionCount)
            {
                throw new MetadataReadException(
                    WasmMetadataErrorKind.Invalid,
                    $"function index {absoluteFunctionIndex} refers to an imported function",
                    absoluteFunctionIndex);
            }

            uint localFunctionIndex = absoluteFunctionIndex - importedFunctionCount;
            if (localFunctionIndex >= (uint)module.Functions.Count)
            {
                throw new MetadataReadException(
                    WasmMetadataErrorKind.Invalid,
                    $"function index {absoluteFunctionIndex} is outside the module function index space",
                    absoluteFunctionIndex);
            }

            WasmFunction function = module.Functions[(int)localFunctionIndex];
            var instructionsByOffset = DecodeInstructionLocations(function);
            var resolvedHints = new List<BranchHint>();
            foreach (EncodedHint encodedHint in entry.Hints)
            {
                if (!instructionsByOffset.TryGetValue(
                        encodedHint.Offset,
                        out (int Ordinal, byte Opcode) target))
                {
                    throw new MetadataReadException(
                        WasmMetadataErrorKind.Invalid,
                        $"offset {encodedHint.Offset} is not an instruction boundary",
                        absoluteFunctionIndex,
                        encodedHint.Offset);
                }

                if (target.Opcode is not 0x04 and not 0x0D)
                {
                    throw new MetadataReadException(
                        WasmMetadataErrorKind.Invalid,
                        $"offset {encodedHint.Offset} targets opcode 0x{target.Opcode:X2}, not if or br_if",
                        absoluteFunctionIndex,
                        encodedHint.Offset);
                }

                resolvedHints.Add(new BranchHint(
                    target.Ordinal,
                    encodedHint.Kind,
                    encodedHint.Offset));
            }

            byFunction.Add((int)localFunctionIndex, resolvedHints);
        }

        return byFunction
            .OrderBy(static pair => pair.Key)
            .Select(pair => (module.Functions[pair.Key], pair.Value))
            .ToList();
    }

    private static Dictionary<uint, (int Ordinal, byte Opcode)> DecodeInstructionLocations(
        WasmFunction function)
    {
        var result = new Dictionary<uint, (int Ordinal, byte Opcode)>();
        var cursor = new InstructionCursor(function.Body, WasmFeatures.All);
        int ordinal = 0;
        while (cursor.MoveNext(out var instruction))
        {
            uint offset = checked(function.OriginalLocalsByteLength + (uint)instruction.ByteOffset);
            result.Add(offset, (ordinal++, instruction.OpcodeId.Opcode));
        }
        return result;
    }

    private static void Report(
        List<WasmMetadataDiagnostic> diagnostics,
        WasmMetadataValidationMode mode,
        WasmMetadataErrorKind kind,
        string message,
        uint? functionIndex = null,
        uint? instructionOffset = null)
    {
        var diagnostic = new WasmMetadataDiagnostic(
            SectionName,
            kind,
            message,
            functionIndex,
            instructionOffset);
        if (mode == WasmMetadataValidationMode.Strict)
            throw new WasmMetadataException(diagnostic);
        diagnostics.Add(diagnostic);
    }
}
