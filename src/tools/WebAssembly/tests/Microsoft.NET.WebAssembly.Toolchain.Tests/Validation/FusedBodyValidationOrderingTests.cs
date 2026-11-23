// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Toolchain.Tests.Validation;

using Microsoft.NET.WebAssembly.Binary;
using Microsoft.NET.WebAssembly.Validation;
using Xunit;

/// <summary>
/// A validating load checks each function body while it decodes it, but decoding comes first in the spec: a module
/// whose bytes do not decode is malformed, whatever else is wrong with it. Each case pairs a validation error with a
/// malformation that comes <em>after</em> it, so a load that reported the first error it met would say "invalid".
/// </summary>
/// <remarks>
/// The spec corpus cannot catch this ordering: none of its <c>assert_malformed</c> binaries contains a decodable but
/// invalid body ahead of its malformation (measured), so these cases are purpose-built.
/// </remarks>
public class FusedBodyValidationOrderingTests
{
    // i32.add with an empty operand stack: decodes, does not validate.
    private static readonly byte[] InvalidBody = [0x00, 0x6A, 0x0B];
    private static readonly byte[] ValidBody = [0x00, 0x0B];

    [Fact]
    public void InvalidModule_IsReportedAsInvalid()
    {
        // The control: with nothing malformed, the same body is a validation error.
        byte[] module = Module(FunctionTypes(1), Functions(1), Code(InvalidBody));

        Assert.Throws<WasmValidationException>(() => ModuleReader.ReadModule(new MemoryStream(module)));
    }

    [Fact]
    public void TypeError_ThenIllegalOpcodeInTheSameBody_IsMalformed()
    {
        // i32.add (underflow), then 0xFF, which is no opcode.
        byte[] module = Module(FunctionTypes(1), Functions(1), Code([0x00, 0x6A, 0xFF, 0x0B]));

        AssertMalformed(module);
    }

    [Fact]
    public void InvalidBody_ThenOverlongLebInTheNextBody_IsMalformed()
    {
        // local.get with a six-byte u32 index.
        byte[] module = Module(FunctionTypes(1), Functions(2), Code(InvalidBody, [0x00, 0x20, 0x80, 0x80, 0x80, 0x80, 0x80, 0x00, 0x1A, 0x0B]));

        AssertMalformed(module);
    }

    [Fact]
    public void InvalidBody_ThenMalformedDataSection_IsMalformed()
    {
        // A data segment with flag 9, which no encoding uses.
        byte[] module = Module(FunctionTypes(1), Functions(1), Memory(), Code(InvalidBody), Section(11, [0x01, 0x09]));

        AssertMalformed(module);
    }

    [Fact]
    public void InvalidBody_ThenDataCountMismatch_IsMalformed()
    {
        // The data count section declares one segment; the data section has none.
        byte[] module = Module(FunctionTypes(1), Functions(1), Memory(), Section(12, [0x01]), Code(InvalidBody), Section(11, [0x00]));

        AssertMalformed(module);
    }

    [Fact]
    public void InvalidBody_ThenTooFewCodeEntries_IsMalformed()
    {
        byte[] module = Module(FunctionTypes(1), Functions(2), Code(InvalidBody));

        AssertMalformed(module);
    }

    private static void AssertMalformed(byte[] module)
    {
        var error = Assert.ThrowsAny<Exception>(() => ModuleReader.ReadModule(new MemoryStream(module)));
        Assert.IsType<WasmFormatException>(error, exactMatch: false);
        Assert.IsNotType<WasmValidationException>(error);
    }

    private static byte[] Module(params byte[][] sections)
    {
        var bytes = new List<byte> { 0x00, 0x61, 0x73, 0x6D, 0x01, 0x00, 0x00, 0x00 };
        foreach (byte[] section in sections)
            bytes.AddRange(section);
        return [.. bytes];
    }

    private static byte[] Section(byte id, byte[] content) => [id, .. Leb((uint)content.Length), .. content];

    // One type, () -> (), shared by every function.
    private static byte[] FunctionTypes(int count) => Section(1, [(byte)count, .. Enumerable.Repeat<byte[]>([0x60, 0x00, 0x00], count).SelectMany(t => t)]);

    private static byte[] Functions(int count) => Section(3, [(byte)count, .. new byte[count]]);

    private static byte[] Memory() => Section(5, [0x01, 0x00, 0x01]);

    private static byte[] Code(params byte[][] bodies)
    {
        var content = new List<byte>(Leb((uint)bodies.Length));
        foreach (byte[] body in bodies)
        {
            content.AddRange(Leb((uint)body.Length));
            content.AddRange(body);
        }

        return Section(10, [.. content]);
    }

    private static byte[] Leb(uint value)
    {
        var bytes = new List<byte>();
        do
        {
            byte b = (byte)(value & 0x7F);
            value >>= 7;
            if (value != 0)
                b |= 0x80;
            bytes.Add(b);
        }
        while (value != 0);
        return [.. bytes];
    }
}
