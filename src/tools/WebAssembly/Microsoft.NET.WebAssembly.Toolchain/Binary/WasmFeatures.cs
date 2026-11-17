// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly;

/// <summary>
/// Opt-in/opt-out flags for Wasm proposal-level features.
/// Pass via <see cref="Binary.ModuleReaderOptions.Features"/>.
/// </summary>
[Flags]
public enum WasmFeatures : ulong
{
    // ── Core (always on) ───────────────────────────────────────────────────
    None              = 0,

    // ── Post-MVP proposals ─────────────────────────────────────────────────
    /// <summary>Multi-value returns / blocks (widely supported, near-MVP).</summary>
    MultiValue        = 1UL << 0,

    /// <summary>Reference types (funcref/externref, table instructions).</summary>
    ReferenceTypes    = 1UL << 1,

    /// <summary>Bulk memory operations (0xFC sub-ops: memory.copy, memory.fill, etc.).</summary>
    BulkMemory        = 1UL << 2,

    /// <summary>SIMD instructions (0xFD prefix).</summary>
    Simd              = 1UL << 3,

    /// <summary>GC proposal — struct/array heap types (0xFB prefix).</summary>
    Gc                = 1UL << 4,

    /// <summary>Exception handling — try/catch/throw/rethrow and try_table.</summary>
    ExceptionHandling = 1UL << 5,

    /// <summary>Threads &amp; atomics — shared memory, 0xFE prefix.</summary>
    Threads           = 1UL << 6,

    /// <summary>Tail calls (return_call / return_call_indirect).</summary>
    TailCalls         = 1UL << 7,

    /// <summary>Stack-switching proposal (resume/suspend/cont opcodes, 0xe0–0xe4 range).</summary>
    StackSwitching    = 1UL << 8,

    /// <summary>Relaxed SIMD proposal — non-deterministic SIMD ops with native-mapping semantics (0xFD 0x100–0x113).</summary>
    RelaxedSimd       = 1UL << 9,

    // ── Debug / tooling ────────────────────────────────────────────────────
    /// <summary>
    /// Parse DWARF .debug_line sections into <see cref="Module.WasmModule.DebugInfo"/>.
    /// Off by default — zero-cost when not requested.
    /// </summary>
    DebugInfo         = 1UL << 32,

    // ── Composite presets ─────────────────────────────────────────────────
    /// <summary>MVP only — no post-MVP proposals accepted.</summary>
    Mvp               = None,

    /// <summary>
    /// All post-MVP runtime proposals enabled (current implicit behavior).
    /// Does NOT include DebugInfo (opt-in only).
    /// </summary>
    Default           = MultiValue | ReferenceTypes | BulkMemory | Simd
                      | Gc | ExceptionHandling | Threads | TailCalls | StackSwitching | RelaxedSimd,

    /// <summary>Everything including debug info parsing.</summary>
    All               = Default | DebugInfo,
}
