// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Types;

/// <summary>
/// Limits for memories and tables (§2.3.5).
/// <para>
/// <see cref="Min"/> / <see cref="Max"/> are stored as <see cref="ulong"/> so that
/// memory64 / table64 (wasm64) index types are represented without truncation.
/// For 32-bit memories/tables the values always fit in a <see cref="uint"/>.
/// </para>
/// <para>
/// <see cref="Is64"/> is set when the limits were encoded with the 64-bit flag bit
/// (0x04) — i.e. a memory64 memory or a table64 table. Downstream decoders use this
/// to know that memarg offsets are u64-encoded and that index operands are i64.
/// </para>
/// </summary>
public sealed record Limits(ulong Min, ulong? Max = null, bool Is64 = false);
