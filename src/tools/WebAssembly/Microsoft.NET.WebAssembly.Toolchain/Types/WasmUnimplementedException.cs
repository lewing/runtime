// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Types;

/// <summary>
/// Marks an exception as nesm's OWN "this feature is not implemented" signal, rather than a
/// failure the WebAssembly specification describes.
///
/// <para>
/// The two are categorically different and must never be confused (#385). A spec-defined
/// failure — a trap, a malformed binary, an invalid module, a link error — is a statement
/// <i>about the module</i>, and an implementation that produces it is conforming. A signal
/// carrying this interface is a statement <i>about nesm</i>: the module is fine, and nesm
/// cannot handle it. Treating the second as the first means absence of a feature gets counted
/// as conformance to it.
/// </para>
///
/// <para>
/// The concrete instance was this: every <c>0xFB</c> GC opcode threw
/// <c>WasmTrapException(WasmTrapCode.Unreachable, "GC instruction execution is not
/// implemented")</c>. That is a throw, so it satisfied <c>assert_trap</c> — and five rows of
/// <c>type-subtyping.wast</c> reported the spec's "cast failure" assertions as green in a
/// runtime that cannot perform a cast at all.
/// </para>
///
/// <para><b>If you are adding a "not implemented yet" throw:</b> use one of the types that
/// implements this interface —
/// <see cref="WasmUnimplementedException"/> for a bare signal,
/// <c>WasmUnimplementedFormatException</c> when the decode path requires a
/// <c>WasmFormatException</c>, or
/// <c>WasmUnimplementedInstantiationException</c> when instantiation requires a
/// <c>WasmInstantiationException</c>. Do not reach for
/// <c>WasmTrapException(WasmTrapCode.Unreachable, …)</c>: it collides with the real
/// <c>unreachable</c> trap and the spec runner cannot then tell the two apart.
/// </para>
///
/// <para>
/// The spec runner rejects any exception carrying this interface, anywhere in its
/// <see cref="System.Exception.InnerException"/> chain, before it evaluates whether the
/// assertion held — for <c>assert_trap</c>, <c>assert_malformed</c>, <c>assert_invalid</c>,
/// <c>assert_unlinkable</c> and <c>assert_uninstantiable</c> alike, because all five accept a
/// throw as evidence and so all five are launderable by the same mistake.
/// </para>
/// </summary>
public interface IWasmUnimplemented
{
    /// <summary>The absent feature, named the way a reader would search for it (e.g. "GC (0xFB) instruction execution").</summary>
    string Feature { get; }

    /// <summary>The issue tracking the gap, e.g. "#395". Empty when none has been filed.</summary>
    string TrackingIssue { get; }
}

/// <summary>
/// Thrown when a module requires a WebAssembly feature nesm has not implemented.
/// See <see cref="IWasmUnimplemented"/> for why this is not a trap.
/// </summary>
public sealed class WasmUnimplementedException : Exception, IWasmUnimplemented
{
    /// <inheritdoc/>
    public string Feature { get; }

    /// <inheritdoc/>
    public string TrackingIssue { get; }

    public WasmUnimplementedException(string feature, string detail, string trackingIssue = "")
        : base(WasmUnimplemented.FormatMessage(feature, detail, trackingIssue))
    {
        Feature = feature;
        TrackingIssue = trackingIssue;
    }
}

/// <summary>Shared message formatting for every <see cref="IWasmUnimplemented"/> carrier.</summary>
public static class WasmUnimplemented
{
    /// <summary>
    /// Builds the canonical message. The leading phrase is fixed so the whole class is greppable
    /// in logs, and so a reader who sees it in a test failure knows immediately that the failure
    /// is about nesm's coverage rather than about the module under test.
    /// </summary>
    public static string FormatMessage(string feature, string detail, string trackingIssue)
    {
        string message = $"nesm has not implemented {feature}";
        if (!string.IsNullOrEmpty(detail))
            message += $": {detail}";
        if (!string.IsNullOrEmpty(trackingIssue))
            message += $" (tracked by {trackingIssue})";
        return message;
    }
}
