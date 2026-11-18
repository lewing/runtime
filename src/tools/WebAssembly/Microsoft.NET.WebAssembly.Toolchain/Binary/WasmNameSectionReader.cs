// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Microsoft.NET.WebAssembly.Binary.Writing;
using Microsoft.NET.WebAssembly.Module;

namespace Microsoft.NET.WebAssembly.Binary;

/// <summary>
/// Parses function and local names from the wasm <c>name</c> custom section.
/// Spec: https://webassembly.github.io/spec/core/appendix/custom.html#binary-namesec
/// </summary>
/// <remarks>
/// Exposed as <c>internal</c> (gated by InternalsVisibleTo) rather than public
/// because this is a low-level binary-format reader, not a stable API surface.
/// Consumers are granted access via InternalsVisibleTo in the project file.
/// </remarks>
internal static class WasmNameSectionReader
{
    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Returns a dictionary of absolute function index → name from the module's
    /// <c>name</c> custom section (subsection id = 1, function names).
    /// Returns an empty dictionary when the section is absent or contains no
    /// function-name subsection (stripped module).
    /// </summary>
    public static Dictionary<int, string> ParseFunctionNames(WasmModule module) =>
        ParseNames(module).FunctionNames;

    /// <summary>
    /// Returns an indirect name map of absolute function index → local index → name
    /// from the module's <c>name</c> custom section (subsection id = 2, local names).
    /// Parameters and locals occupy the same local-index space. Returns an empty
    /// dictionary when the section or subsection is absent.
    /// </summary>
    public static Dictionary<int, Dictionary<int, string>> ParseLocalNames(WasmModule module) =>
        ParseNames(module).LocalNames;

    /// <summary>
    /// Everything the shared symbol layer needs from one <c>name</c> section: the module name
    /// (subsection 0), function names in section order (subsection 1), local names (subsection 2)
    /// and, when the data is malformed, where parsing stopped. Parsing is strict (names must be
    /// valid UTF-8) and never throws: whatever was read before a malformation is kept.
    /// </summary>
    internal sealed record NameSectionContents(
        bool Present,
        string? ModuleName,
        bool HasFunctionNamesSubsection,
        IReadOnlyList<(int Index, string Name)> FunctionNameList,
        Dictionary<int, string> FunctionNames,
        Dictionary<int, Dictionary<int, string>> LocalNames,
        string? Malformation);

    /// <summary>Parses the module's first <c>name</c> custom section (see <see cref="NameSectionContents"/>).</summary>
    public static NameSectionContents Read(WasmModule module)
    {
        var nameSection = module.CustomSections.FirstOrDefault(cs => cs.Name == "name");
        return nameSection is null
            ? new NameSectionContents(false, null, false, [], [], [], null)
            : Read(nameSection.Content);
    }

    /// <summary>Parses raw <c>name</c> custom-section content.</summary>
    public static NameSectionContents Read(ReadOnlySpan<byte> data)
    {
        var functionNames = new Dictionary<int, string>();
        var functionNameList = new List<(int, string)>();
        var localNames = new Dictionary<int, Dictionary<int, string>>();
        string? moduleName = null;
        bool hasFunctionNames = false;
        string? malformation = null;

        int pos = 0;
        while (pos < data.Length)
        {
            int subStart = pos;
            byte subId = data[pos++];
            if (!WasmLeb128.TryReadU32(data, ref pos, out uint subLen)
                || subLen > data.Length - pos)
            {
                malformation ??= $"subsection {subId} at offset {subStart} has a truncated or oversized length";
                break;
            }

            int subEnd = pos + (int)subLen;
            ReadOnlySpan<byte> sub = data[..subEnd]; // positions stay section-relative
            if (subId == (byte)NameSubsectionId.Module)
            {
                if (TryReadName(sub, ref pos, out string? name))
                    moduleName = name;
                else
                    malformation ??= $"module-name subsection at offset {subStart} is malformed";
            }
            else if (subId == (byte)NameSubsectionId.Function)
            {
                hasFunctionNames = true;
                if (!WasmLeb128.TryReadU32(sub, ref pos, out uint count))
                {
                    malformation ??= $"function-names subsection at offset {subStart} has a malformed count";
                }
                else
                {
                    for (uint i = 0; i < count; i++)
                    {
                        int entryStart = pos;
                        if (!WasmLeb128.TryReadU32(sub, ref pos, out uint funcIndex)
                            || !TryReadName(sub, ref pos, out string? name))
                        {
                            malformation ??= $"function-names entry {i} of {count} at offset {entryStart} is malformed";
                            break;
                        }

                        if (funcIndex <= int.MaxValue)
                        {
                            functionNames[(int)funcIndex] = name;
                            functionNameList.Add(((int)funcIndex, name));
                        }
                    }
                }
            }
            else if (subId == (byte)NameSubsectionId.Local)
            {
                if (!WasmLeb128.TryReadU32(sub, ref pos, out uint functionCount))
                {
                    pos = subEnd;
                    continue;
                }

                for (uint i = 0; i < functionCount && pos < subEnd; i++)
                {
                    if (!WasmLeb128.TryReadU32(sub, ref pos, out uint funcIndex)
                        || !WasmLeb128.TryReadU32(sub, ref pos, out uint localCount))
                        break;

                    var locals = new Dictionary<int, string>();
                    bool complete = true;
                    for (uint j = 0; j < localCount; j++)
                    {
                        if (!WasmLeb128.TryReadU32(sub, ref pos, out uint localIndex)
                            || !TryReadName(sub, ref pos, out string? name))
                        {
                            complete = false;
                            break;
                        }

                        if (localIndex <= int.MaxValue)
                            locals[(int)localIndex] = name;
                    }

                    if (!complete) break;
                    if (funcIndex <= int.MaxValue)
                        localNames[(int)funcIndex] = locals;
                }
            }

            pos = subEnd; // skip to next subsection regardless of id
        }
        return new NameSectionContents(true, moduleName, hasFunctionNames, functionNameList,
            functionNames, localNames, malformation);
    }

    private static (
        Dictionary<int, string> FunctionNames,
        Dictionary<int, Dictionary<int, string>> LocalNames) ParseNames(WasmModule module)
    {
        var contents = Read(module);
        return (contents.FunctionNames, contents.LocalNames);
    }

    private static bool TryReadName(
        ReadOnlySpan<byte> data,
        ref int pos,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? name)
    {
        name = null;
        if (!WasmLeb128.TryReadU32(data, ref pos, out uint nameLen)
            || nameLen > data.Length - pos)
            return false;

        try
        {
            name = StrictUtf8.GetString(data.Slice(pos, (int)nameLen));
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
        pos += (int)nameLen;
        return true;
    }
}
