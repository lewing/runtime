// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Binary;

using Microsoft.NET.WebAssembly.Types;

/// <summary>
/// A <see cref="WasmFormatException"/> that is really nesm's "not implemented" signal.
///
/// <para>
/// Decode-path callers are typed against <see cref="WasmFormatException"/>, so a bare
/// <see cref="WasmUnimplementedException"/> would escape handlers that legitimately expect a
/// format failure. This subclass keeps the decode contract while still carrying
/// <see cref="IWasmUnimplemented"/>, so the spec runner refuses to accept it as evidence that a
/// module is malformed or invalid. See <see cref="IWasmUnimplemented"/> and #385.
/// </para>
/// </summary>
public sealed class WasmUnimplementedFormatException : WasmFormatException, IWasmUnimplemented
{
    /// <inheritdoc/>
    public string Feature { get; }

    /// <inheritdoc/>
    public string TrackingIssue { get; }

    public WasmUnimplementedFormatException(string feature, string detail, string trackingIssue = "")
        : base(WasmUnimplemented.FormatMessage(feature, detail, trackingIssue))
    {
        Feature = feature;
        TrackingIssue = trackingIssue;
    }
}
