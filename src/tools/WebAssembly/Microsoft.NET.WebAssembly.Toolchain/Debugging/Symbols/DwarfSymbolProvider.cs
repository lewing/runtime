// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.NET.WebAssembly.Binary;
using Microsoft.NET.WebAssembly.Module;

namespace Microsoft.NET.WebAssembly.Debugging.Symbols;

/// <summary>
/// Function names from DWARF (#540 PR 2, #530): every out-of-line <c>DW_TAG_subprogram</c>'s
/// <c>DW_AT_name</c> (and, when different, its mangled <c>DW_AT_linkage_name</c>), bound to the
/// function whose body starts at the subprogram's <c>DW_AT_low_pc</c>.
/// <para>
/// A subprogram is bound only when its address is EXACTLY a function-body start — the code-section
/// offset of the function's locals vector, which is what LLVM emits. There is no other route: no
/// containment match and no <c>low_pc</c>-as-index guess, so DWARF whose addresses do not describe
/// this code section (zeroed by wasm-opt, or from another build) cannot name the wrong function.
/// Such DWARF is reported <see cref="ProviderState.Degenerate"/> with the evidence, so a miss is
/// explained instead of silent.
/// </para>
/// <para>
/// Only <see cref="WasmFunction.LocalsCodeSectionOffset"/> is used, which a hot-swap does not move,
/// so the binding survives body edits; <see cref="ModuleSymbols.Invalidate"/> still drops it with the
/// rest of the module's providers.
/// </para>
/// </summary>
internal sealed class DwarfSymbolProvider(WasmModule module) : IndexedSymbolProvider
{
    private const string ZeroedHint =
        "DWARF code addresses were zeroed — typically binaryen wasm-opt ran without -g (the wasi-sdk clang driver "
        + "runs it after an optimized one-step compile+link when wasm-opt is on PATH). Rebuild without wasm-opt "
        + "(compile with -c, then link with wasm-ld, or take wasm-opt off PATH) or run wasm-opt with -g.";

    private const string MismatchHint =
        "The DWARF does not describe this module's code section: it was rewritten after linking (e.g. by "
        + "wasm-opt without -g) or the DWARF belongs to a different build (check an attached sidecar).";

    public override SymbolSource Source => SymbolSource.Dwarf;

    protected override (IReadOnlyList<SymbolEntry>, ProviderStatus) Load()
    {
        DwarfSubprogramIndex index;
        try
        {
            index = DwarfSubprogramReader.Read(module);
        }
        catch (Exception ex)
        {
            return ([], StatusOf(ProviderState.Failed, $"reading .debug_info failed: {ex.Message}"));
        }

        if (!index.HasDebugInfo)
            return ([], StatusOf(ProviderState.Absent, "no DWARF .debug_info section",
                "build with -g, or attach a DWARF sidecar (wasm_dwarf_attach_sidecar)"));

        int importCount = module.Imports.Count(i => i.Descriptor is ImportDescriptor.Func);
        var bodyStarts = BodyStarts(module, importCount, out bool offsetsKnown);
        if (!offsetsKnown)
            return ([], StatusOf(ProviderState.Failed,
                "the module's code-section offsets are unknown (not read from a binary), so DWARF addresses cannot be bound to functions"));

        var entries = new List<SymbolEntry>();
        var seen = new HashSet<(int, string, NameForm)>();
        int withAddress = 0, mapped = 0, unmapped = 0, atZero = 0, tombstones = 0;
        var mappedFunctions = new HashSet<int>();
        foreach (var sp in index.Subprograms)
        {
            if (sp.IsTombstone) { tombstones++; continue; }
            withAddress++;
            if (!bodyStarts.TryGetValue(sp.LowPc, out int functionIndex))
            {
                unmapped++;
                if (sp.LowPc == 0) atZero++;
                continue;
            }
            mapped++;
            mappedFunctions.Add(functionIndex);
            if (sp.Name is { } name && seen.Add((functionIndex, name, NameForm.Raw)))
                entries.Add(new SymbolEntry(functionIndex, new SymbolName(name, SymbolSource.Dwarf, NameForm.Raw)));
            if (sp.LinkageName is { } linkage && linkage != sp.Name && seen.Add((functionIndex, linkage, NameForm.Mangled)))
                entries.Add(new SymbolEntry(functionIndex, new SymbolName(linkage, SymbolSource.Dwarf, NameForm.Mangled)));
        }

        string units = index.SkippedUnits > 0
            ? $"{index.Units} compile units read, {index.SkippedUnits} skipped"
            : $"{index.Units} compile units";
        string discarded = tombstones > 0 ? $", {tombstones} discarded by the linker" : "";

        if (withAddress == 0)
            return ([], StatusOf(ProviderState.Absent,
                $"no DW_TAG_subprogram with a code address ({units}{discarded})"));

        if (unmapped == 0)
            return (entries, StatusOf(ProviderState.Available,
                $"{mapped} subprograms bound to {mappedFunctions.Count} functions, {entries.Count} names ({units}{discarded})"));

        string evidence =
            $"{unmapped} of {withAddress} subprograms have a DW_AT_low_pc that is not a function-body start"
            + (atZero > 0 ? $" ({atZero} at address 0)" : "");
        string hint = atZero * 2 >= withAddress ? ZeroedHint : MismatchHint;

        // Partly usable: a minority of stray addresses next to a majority that land exactly on
        // body starts. Otherwise the addresses that do land are coincidences, not evidence.
        if (unmapped * 2 < withAddress)
            return (entries, StatusOf(ProviderState.Degenerate,
                $"{evidence}; names from the {mapped} that do are kept ({units}{discarded})", hint));

        return ([], StatusOf(ProviderState.Degenerate,
            $"{evidence}; no DWARF function names are used ({units}{discarded})", hint));
    }

    /// <summary>Code-section offset of each defined function's locals vector → absolute function index.</summary>
    private static Dictionary<long, int> BodyStarts(WasmModule module, int importCount, out bool offsetsKnown)
    {
        var starts = new Dictionary<long, int>(module.Functions.Count);
        offsetsKnown = true;
        for (int i = 0; i < module.Functions.Count; i++)
        {
            long start = module.Functions[i].LocalsCodeSectionOffset;
            // 0 means "not read from a binary"; a duplicate means the offsets are not a layout.
            if (start == 0 || !starts.TryAdd(start, importCount + i))
            {
                offsetsKnown = false;
                return [];
            }
        }
        return starts;
    }
}
