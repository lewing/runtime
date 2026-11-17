// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Types;

/// <summary>
/// Central rejection point for wasm64 (memory64/table64) modules on paths that cannot yet
/// honour 64-bit limits.
/// <para>
/// nesm supports <b>parsing and inspecting</b> wasm64 modules only (see #88). Instantiation and
/// linking are not yet supported. Every site that consumes memory/table limits on an execution or
/// linking path — casting <see cref="Limits.Min"/>/<see cref="Limits.Max"/> from <c>ulong</c> to
/// <c>uint</c>/<c>int</c>, or reconstructing a <see cref="Limits"/> (which drops
/// <see cref="Limits.Is64"/> unless it is explicitly threaded through) — MUST reject an
/// <see cref="Limits.Is64"/> memory/table rather than silently truncate the value or erase the
/// flag. Route those rejections through <see cref="NotSupportedMessage"/> so the whole class of
/// "silent wasm64 truncation" bugs has a single, greppable choke point.
/// </para>
/// </summary>
public static class Wasm64Guard
{
    /// <summary>
    /// Builds the canonical "wasm64 not yet supported (#88)" message.
    /// </summary>
    /// <param name="kind">The concrete kind being rejected (e.g. <c>"memory64"</c> or <c>"table64"</c>).</param>
    /// <param name="phase">The unsupported phase (e.g. <c>"execution"</c> or <c>"linking"</c>).</param>
    public static string NotSupportedMessage(string kind, string phase) =>
        $"{kind} (wasm64) {phase} is not yet supported; nesm currently supports " +
        "parsing/inspecting wasm64 modules only (see #88)";
}
