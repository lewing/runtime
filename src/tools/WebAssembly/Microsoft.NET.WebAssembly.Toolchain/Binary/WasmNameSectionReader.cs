// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Microsoft.NET.WebAssembly.Module;

namespace Microsoft.NET.WebAssembly.Binary;

/// <summary>
/// Parses the wasm <c>name</c> custom section into a function-index → name dictionary.
/// Spec: https://webassembly.github.io/spec/core/appendix/custom.html#binary-namesec
/// </summary>
/// <remarks>
/// Exposed as <c>internal</c> (gated by InternalsVisibleTo) rather than public
/// because this is a low-level binary-format reader, not a stable API surface.
/// Consumers are granted access via InternalsVisibleTo in the project file.
/// </remarks>
internal static class WasmNameSectionReader
{
    /// <summary>
    /// Returns a dictionary of absolute function index → name from the module's
    /// <c>name</c> custom section (subsection id = 1, function names).
    /// Returns an empty dictionary when the section is absent or contains no
    /// function-name subsection (stripped module).
    /// </summary>
    public static Dictionary<int, string> ParseFunctionNames(WasmModule module)
    {
        var result = new Dictionary<int, string>();
        var nameSection = module.CustomSections.FirstOrDefault(cs => cs.Name == "name");
        if (nameSection is null) return result;

        var data = nameSection.Content;
        int pos = 0;
        while (pos < data.Length)
        {
            byte subId = data[pos++];
            uint subLen = ReadULEB128(data, ref pos);
            int subEnd = pos + (int)subLen;
            if (subEnd > data.Length) break;

            if (subId == 1) // function-names subsection
            {
                uint count = ReadULEB128(data, ref pos);
                for (uint i = 0; i < count && pos < subEnd; i++)
                {
                    uint funcIndex = ReadULEB128(data, ref pos);
                    uint nameLen = ReadULEB128(data, ref pos);
                    if (pos + (int)nameLen <= subEnd)
                        result[(int)funcIndex] = Encoding.UTF8.GetString(data, pos, (int)nameLen);
                    pos += (int)nameLen;
                }
            }

            pos = subEnd; // skip to next subsection regardless of id
        }
        return result;
    }

    private static uint ReadULEB128(byte[] data, ref int pos)
    {
        uint result = 0;
        int shift = 0;
        while (pos < data.Length)
        {
            byte b = data[pos++];
            result |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
        }
        return result;
    }
}
