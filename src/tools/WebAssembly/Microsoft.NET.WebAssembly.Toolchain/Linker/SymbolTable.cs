// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Linker;

using Microsoft.NET.WebAssembly.Binary.Linking;
using Microsoft.NET.WebAssembly.Module;

/// <summary>Symbol binding strength, mirroring the wasm linking-convention flags.</summary>
public enum SymbolBinding
{
    /// <summary>Normal definition; wins over any weak definition.</summary>
    Strong,
    /// <summary>Weak definition; discarded if a strong definition exists in another module.</summary>
    Weak,
}

/// <summary>
/// A symbol that has been resolved across all input modules.
/// Tracks which module provides the definition and where the symbol will land in the output.
/// </summary>
public sealed class ResolvedSymbol
{
    public required string Name { get; init; }
    public required SymbolType Kind { get; init; }
    public required SymbolBinding Binding { get; set; }
    public SymbolFlags Flags { get; init; }

    /// <summary>Index of the input module that defines this symbol. Null means the symbol is still undefined.</summary>
    public int? DefiningModuleIndex { get; set; }

    /// <summary>Module-local index within the defining module's index space (type/function/global/…).</summary>
    public uint? LocalIndex { get; set; }

    /// <summary>Final merged index in the output module. Populated after index remapping is complete.</summary>
    public uint? MergedIndex { get; set; }

    public bool IsUndefined => !DefiningModuleIndex.HasValue;
}

/// <summary>
/// Merged symbol table built from all input modules' <c>WASM_SYMBOL_TABLE</c> linking subsections.
/// Implements strong/weak/undefined resolution rules from the wasm tool-conventions spec.
/// </summary>
public sealed class SymbolTable
{
    private readonly IReadOnlyList<WasmSymbol>? _indexedSymbols;

    /// <summary>All non-local symbols seen across all input modules, keyed by name.</summary>
    public Dictionary<string, ResolvedSymbol> Symbols { get; } =
        new(StringComparer.Ordinal);

    public SymbolTable()
    {
    }

    public SymbolTable(IReadOnlyList<WasmSymbol> symbols)
    {
        ArgumentNullException.ThrowIfNull(symbols);
        _indexedSymbols = symbols;
    }

    public SymbolTable(LinkingSection linkingSection)
        : this(linkingSection?.Symbols ?? throw new ArgumentNullException(nameof(linkingSection)))
    {
    }

    /// <summary>
    /// Symbol names that remain undefined after all modules have been processed.
    /// Populated by <see cref="ResolveUndefined"/>.
    /// </summary>
    public List<string> UnresolvedSymbols { get; } = [];

    /// <summary>Number of relocation-addressable symbols available through the indexed view.</summary>
    public int Count => _indexedSymbols?.Count ?? 0;

    /// <summary>The raw per-module symbol list used for relocation lookups by symbol index.</summary>
    public IReadOnlyList<WasmSymbol> IndexedSymbols => _indexedSymbols ?? Array.Empty<WasmSymbol>();

    public WasmSymbol this[uint index]
    {
        get
        {
            if (_indexedSymbols is null)
                throw new InvalidOperationException("This symbol table instance was created for merged-name resolution only and does not expose indexed relocation symbols.");

            if (index >= (uint)_indexedSymbols.Count)
                throw new KeyNotFoundException($"Symbol index {index} is out of range for symbol table size {_indexedSymbols.Count}.");

            return _indexedSymbols[(int)index];
        }
    }

    /// <summary>
    /// Merges the linking-section symbols from one input module into this table.
    /// Local symbols (<c>WASM_SYM_BINDING_LOCAL</c>) are skipped — they are module-private.
    /// </summary>
    /// <param name="moduleIndex">Index of the input module within the linker's input list.</param>
    /// <param name="module">The parsed input module.</param>
    public void AddModule(int moduleIndex, WasmModule module)
    {
        var linking = module.LinkingSection;
        if (linking is null)
            return;

        foreach (var sym in linking.Symbols)
        {
            if (sym.Name is null)
                continue;

            // Local symbols are never merged across modules.
            if ((sym.Flags & SymbolFlags.BindingLocal) != 0)
                continue;

            bool isUndefined = (sym.Flags & SymbolFlags.Undefined) != 0;
            bool isWeak = (sym.Flags & SymbolFlags.BindingWeak) != 0;
            var binding = isWeak ? SymbolBinding.Weak : SymbolBinding.Strong;

            if (isUndefined)
            {
                // Register an undefined placeholder only if no entry exists yet.
                if (!Symbols.ContainsKey(sym.Name))
                {
                    Symbols[sym.Name] = new ResolvedSymbol
                    {
                        Name = sym.Name,
                        Kind = sym.Type,
                        Binding = binding,
                        Flags = sym.Flags,
                    };
                }
                continue;
            }

            if (Symbols.TryGetValue(sym.Name, out var existing))
            {
                if (!existing.DefiningModuleIndex.HasValue)
                {
                    // Existing was undefined — this module provides the definition.
                    existing.DefiningModuleIndex = moduleIndex;
                    existing.LocalIndex = (uint?)sym.Index;
                    existing.Binding = binding;
                }
                else
                {
                    // Both are defined.  Strong+strong is a duplicate error.
                    if (existing.Binding == SymbolBinding.Strong && binding == SymbolBinding.Strong)
                        throw new LinkerException($"Duplicate strong symbol definition: '{sym.Name}'");

                    // New strong overrides existing weak.
                    if (binding == SymbolBinding.Strong)
                    {
                        existing.DefiningModuleIndex = moduleIndex;
                        existing.LocalIndex = (uint?)sym.Index;
                        existing.Binding = SymbolBinding.Strong;
                    }
                    // else: existing strong/weak keeps priority; new weak is discarded.
                }
            }
            else
            {
                Symbols[sym.Name] = new ResolvedSymbol
                {
                    Name = sym.Name,
                    Kind = sym.Type,
                    Binding = binding,
                    Flags = sym.Flags,
                    DefiningModuleIndex = moduleIndex,
                    LocalIndex = (uint?)sym.Index,
                };
            }
        }
    }

    /// <summary>
    /// Identifies all symbols that remain undefined after all modules have been processed.
    /// Populates <see cref="UnresolvedSymbols"/>.
    /// </summary>
    public void ResolveUndefined()
    {
        UnresolvedSymbols.Clear();
        foreach (var (name, sym) in Symbols)
        {
            if (sym.IsUndefined)
                UnresolvedSymbols.Add(name);
        }
    }
}
