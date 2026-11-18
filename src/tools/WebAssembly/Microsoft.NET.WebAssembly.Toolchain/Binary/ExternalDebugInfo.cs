// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Microsoft.NET.WebAssembly.Binary.Writing;
using Microsoft.NET.WebAssembly.Module;

namespace Microsoft.NET.WebAssembly.Binary;

/// <summary>
/// The split-DWARF discovery record parsed from a stripped wasm module's
/// <c>external_debug_info</c> custom section (WebAssembly/tool-conventions
/// <c>Debugging.md</c>). The section content is a wasm <c>name</c>
/// (<c>u32 LEB length</c> + UTF-8 bytes) giving the path/URL of a companion
/// <c>.debug.wasm</c> sidecar that carries the real <c>.debug_*</c> sections.
/// An optional <c>build_id</c> custom section (BuildId.md) lets a consumer
/// confirm the sidecar matches the stripped module.
/// </summary>
public sealed record ExternalDebugInfo(string Path, byte[]? BuildId)
{
    /// <summary>The <c>external_debug_info</c> custom section name.</summary>
    public const string SectionName = "external_debug_info";

    /// <summary>The <c>build_id</c> custom section name.</summary>
    public const string BuildIdSectionName = "build_id";

    /// <summary>
    /// Parse the <c>external_debug_info</c> custom section of <paramref name="module"/>,
    /// if present. Returns null when the module carries no such section (i.e. it is
    /// not a split-DWARF module). The optional <c>build_id</c> section, when present,
    /// is attached for later matching.
    /// </summary>
    public static ExternalDebugInfo? TryRead(WasmModule module)
    {
        ArgumentNullException.ThrowIfNull(module);

        var section = module.CustomSections.FirstOrDefault(cs => cs.Name == SectionName);
        if (section is null)
            return null;

        var path = ReadNamePath(section.Content);
        if (string.IsNullOrEmpty(path))
            return null;

        return new ExternalDebugInfo(path, TryReadBuildId(module));
    }

    /// <summary>
    /// Read and decode the module's <c>build_id</c> custom section, if present.
    /// Applies the same LEB-length-aware decode used when discovering a split
    /// module, so producers and consumers agree on the raw id bytes.
    /// </summary>
    public static byte[]? TryReadBuildId(WasmModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        var buildIdSection = module.CustomSections.FirstOrDefault(cs => cs.Name == BuildIdSectionName);
        return buildIdSection is null ? null : ReadBuildId(buildIdSection.Content);
    }

    /// <summary>
    /// Decode the sidecar path. The canonical encoding is a wasm <c>name</c>
    /// (<c>u32 LEB length</c> + UTF-8). Producers occasionally emit the raw string
    /// with no length prefix or a trailing NUL, so this falls back to interpreting
    /// the whole payload as UTF-8 when the length prefix does not describe the
    /// remaining bytes.
    /// </summary>
    private static string ReadNamePath(byte[] content)
    {
        if (content.Length == 0)
            return string.Empty;

        if (TryReadU32Leb(content, out int len, out int headerLen)
            && (long)headerLen + len <= content.Length
            // Guard against a coincidental first byte: require the declared length to
            // reach the end (canonical) or leave only trailing padding NULs.
            && AllZero(content, headerLen + len))
        {
            return Encoding.UTF8.GetString(content, headerLen, len);
        }

        // Fallback: raw UTF-8, trimming any trailing NUL padding.
        int end = content.Length;
        while (end > 0 && content[end - 1] == 0)
            end--;
        return Encoding.UTF8.GetString(content, 0, end);
    }

    /// <summary>
    /// Read the build-id blob. The <c>build_id</c> section content is a
    /// <c>u32 LEB length</c> + raw bytes; falls back to the whole payload.
    /// </summary>
    private static byte[] ReadBuildId(byte[] content)
    {
        if (TryReadU32Leb(content, out int len, out int headerLen)
            && (long)headerLen + len <= content.Length)
        {
            return content[headerLen..(headerLen + len)];
        }
        return content;
    }

    private static bool AllZero(byte[] buf, int from)
    {
        for (int i = from; i < buf.Length; i++)
            if (buf[i] != 0) return false;
        return true;
    }

    private static bool TryReadU32Leb(ReadOnlySpan<byte> buf, out int value, out int bytesRead)
    {
        // A length we can index with: a valid u32 that also fits an int.
        if (WasmLeb128.DecodeU32(buf, out uint result, out bytesRead) == Leb128Status.Ok && result <= int.MaxValue)
        {
            value = (int)result;
            return true;
        }

        value = 0;
        bytesRead = 0;
        return false;
    }
}
