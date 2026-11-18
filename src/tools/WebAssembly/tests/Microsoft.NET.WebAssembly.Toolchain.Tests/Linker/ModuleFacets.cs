// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Toolchain.Tests.Linker;

using Microsoft.NET.WebAssembly.Module;
using Microsoft.NET.WebAssembly.Types;

/// <summary>Structural descriptions of a linked module (exports, imports, memory, stack pointer, memory image) for comparing linkers' products.</summary>
internal static class ModuleFacets
{
    internal static List<string> DescribeExports(WasmModule module) =>
        module.Exports.Values.Select(e => e.Descriptor switch
        {
            ExportDescriptor.Func f => $"func {e.Name}{Signature(FunctionType(module, f.FunctionIndex))}",
            ExportDescriptor.Memory => $"memory {e.Name} ({(module.Imports.Any(i => i.Descriptor is ImportDescriptor.Memory) ? "imported" : "defined")})",
            ExportDescriptor.Table => $"table {e.Name}",
            ExportDescriptor.Global => $"global {e.Name}",
            _ => $"other {e.Name}",
        }).Order(StringComparer.Ordinal).ToList();

    internal static List<string> DescribeImports(WasmModule module) =>
        module.Imports.Select(i => i.Descriptor switch
        {
            ImportDescriptor.Func f => $"func {i.ModuleName}.{i.Name}{Signature(module.Types[f.TypeIndex])}",
            ImportDescriptor.Global g => $"global {i.ModuleName}.{i.Name} {g.Type.ContentType} {g.Type.Mutability}",
            ImportDescriptor.Memory m => $"memory {i.ModuleName}.{i.Name} {m.Type.Limits.Min}..{m.Type.Limits.Max}",
            ImportDescriptor.Table t => $"table {i.ModuleName}.{i.Name} {t.Type.Limits.Min}..{t.Type.Limits.Max}",
            _ => $"other {i.ModuleName}.{i.Name}",
        }).Order(StringComparer.Ordinal).ToList();

    internal static List<string> DescribeMemoryAndTable(WasmModule module)
    {
        var result = new List<string>();
        foreach (MemoryType m in module.Memories)
            result.Add($"memory {m.Limits.Min}..{m.Limits.Max}");
        foreach (TableType t in module.Tables)
            result.Add($"table {t.ElementType} {t.Limits.Min}..{t.Limits.Max}");
        foreach (Element e in module.Elements)
            result.Add($"elem table {e.TableIndex} at {ConstI32(e.OffsetExpr)}: {string.Join(",", e.FunctionIndices.Select(f => Signature(FunctionType(module, f))))}");
        return result;
    }

    internal static List<string> DescribeDefinedFunctions(WasmModule module) =>
        module.Functions.Select(f => Signature(module.Types[f.TypeIndex])).Order(StringComparer.Ordinal).ToList();

    internal static string DescribeStackPointer(WasmModule module)
    {
        if (module.Imports.Any(i => i.Name == "__stack_pointer"))
            return "imported __stack_pointer";

        // wasm-ld defines __stack_pointer as the first defined global.
        if (module.Globals.Count == 0 || module.Globals[0].Type is not { ContentType: ValType.I32, Mutability: Mutability.Var })
            return "none";
        return $"defined mutable i32 = {ConstI32(module.Globals[0].InitExpr)}";
    }

    internal static List<string> DescribeMemoryImage(WasmModule module)
    {
        var image = new SortedDictionary<long, byte>();
        foreach (Data segment in module.DataSegments)
        {
            Assert.False(segment.IsPassive, "passive data segments are not expected for a non-shared-memory link");
            long start = ConstI32(segment.OffsetExpr);
            for (int i = 0; i < segment.InitData.Length; i++)
            {
                if (segment.InitData[i] != 0)
                    image[start + i] = segment.InitData[i];
            }
        }

        // Group runs so a mismatch reads as addresses and bytes rather than one entry per byte.
        var runs = new List<string>();
        long runStart = -1, previous = -2;
        var bytes = new List<byte>();
        foreach ((long address, byte value) in image)
        {
            if (address != previous + 1 && bytes.Count > 0)
            {
                runs.Add($"{runStart}: {Convert.ToHexString(bytes.ToArray())}");
                bytes.Clear();
            }
            if (bytes.Count == 0)
                runStart = address;
            bytes.Add(value);
            previous = address;
        }
        if (bytes.Count > 0)
            runs.Add($"{runStart}: {Convert.ToHexString(bytes.ToArray())}");
        return runs;
    }

    internal static FuncType FunctionType(WasmModule module, int functionIndex)
    {
        int importCount = module.Imports.Count(i => i.Descriptor is ImportDescriptor.Func);
        if (functionIndex < importCount)
            return module.Types[((ImportDescriptor.Func)module.Imports.Where(i => i.Descriptor is ImportDescriptor.Func).ElementAt(functionIndex).Descriptor).TypeIndex];
        return module.Types[module.Functions[functionIndex - importCount].TypeIndex];
    }

    internal static long ConstI32(byte[] expr)
    {
        Assert.True(expr.Length >= 2 && expr[0] == 0x41, $"expected an i32.const expression, got {Convert.ToHexString(expr)}");
        long result = 0;
        int shift = 0;
        int i = 1;
        byte b;
        do
        {
            b = expr[i++];
            result |= (long)(b & 0x7F) << shift;
            shift += 7;
        } while ((b & 0x80) != 0);
        if (shift < 64 && (b & 0x40) != 0)
            result |= -1L << shift;
        return result;
    }

    internal static string Signature(FuncType type) =>
        $"({string.Join(",", type.Parameters)})->({string.Join(",", type.Results)})";
}
