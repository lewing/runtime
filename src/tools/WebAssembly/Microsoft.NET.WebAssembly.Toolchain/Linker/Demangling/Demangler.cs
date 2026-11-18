// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// The dispatch follows LLVM's llvm/lib/Demangle/Demangle.cpp (llvm-project 895aa2c896ada719451be2e3673c83da8ddf1141,
// LLVM 23), which is part of the LLVM Project, under the Apache License v2.0 with LLVM Exceptions.
// See https://llvm.org/LICENSE.txt for license information.
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception

using System.Text;

namespace Microsoft.NET.WebAssembly.Linker.Demangling;

/// <summary>
/// LLVM's <c>llvm::demangle</c>, which wasm-ld uses for the <c>name</c> section: Itanium C++ names are demangled like
/// LLVM 23's <c>ItaniumDemangle</c>, Rust v0 names (<c>_R</c>) like its <c>RustDemangle</c>.
/// </summary>
/// <remarks>
/// D (<c>_D</c>) and Microsoft (<c>?</c>) manglings are returned unchanged; LLVM would demangle the valid ones. A name
/// that fails to parse is returned unchanged, as in LLVM.
/// <para>
/// LLVM demangles bytes and returns a C string. The demanglers here run on a byte string, one char per UTF-8 byte of the
/// name (an ASCII name is its own byte string, so it is not converted), so the bytes a string-literal template argument
/// or a punycode identifier spells out come back as the same bytes, and anything after an embedded NUL is lost.
/// <see cref="DemangleToUtf8"/> returns those bytes as they are, as wasm-ld writes them; the <see cref="string"/> APIs
/// decode them, so bytes that are not valid UTF-8 become U+FFFD there.
/// </para>
/// </remarks>
public static class Demangler
{
    /// <summary>LLVM's <c>llvm::demangle</c>.</summary>
    public static string Demangle(string mangledName)
    {
        ArgumentNullException.ThrowIfNull(mangledName);
        return DemangleByteString(ToByteString(mangledName)) is { } demangled ? FromByteString(demangled) : mangledName;
    }

    /// <summary>
    /// LLVM's <c>llvm::demangle</c> as UTF-8, byte for byte what wasm-ld writes to the name section (including bytes
    /// that are not valid UTF-8, which a string-literal template argument can spell out).
    /// </summary>
    public static byte[] DemangleToUtf8(string mangledName)
    {
        ArgumentNullException.ThrowIfNull(mangledName);
        return DemangleByteString(ToByteString(mangledName)) is { } demangled
            ? Encoding.Latin1.GetBytes(demangled)
            : Encoding.UTF8.GetBytes(mangledName);
    }

    /// <summary>LLVM's <c>itaniumDemangle</c>: the demangled name, or null when <paramref name="mangledName"/> does not parse.</summary>
    public static string? TryDemangleItanium(string mangledName)
    {
        ArgumentNullException.ThrowIfNull(mangledName);
        return mangledName.Length == 0 ? null : FromByteString(CString(DemangleItaniumBytes(ToByteString(mangledName))));
    }

    /// <summary>LLVM's <c>rustDemangle</c> (Rust v0): the demangled name, or null when <paramref name="mangledName"/> does not parse.</summary>
    public static string? TryDemangleRust(string mangledName)
    {
        ArgumentNullException.ThrowIfNull(mangledName);
        return FromByteString(CString(RustDemangler.Demangle(ToByteString(mangledName))));
    }

    // llvm::demangle on a byte string: the demangled byte string, or null to keep the name.
    private static string? DemangleByteString(string name)
    {
        if (NonMicrosoftDemangle(name, 0, canHaveLeadingDot: true) is { } result)
            return result;
        if (name.StartsWith('_') && NonMicrosoftDemangle(name, 1, canHaveLeadingDot: false) is { } stripped)
            return stripped;
        return null;
    }

    private static string ToByteString(string name) =>
        Ascii.IsValid(name) ? name : Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(name));

    [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(bytes))]
    private static string? FromByteString(string? bytes) =>
        bytes is null || Ascii.IsValid(bytes) ? bytes : Encoding.UTF8.GetString(Encoding.Latin1.GetBytes(bytes));

    // The demanglers return C strings: anything after an embedded NUL is lost.
    [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(demangled))]
    private static string? CString(string? demangled)
    {
        int nul = demangled is null ? -1 : demangled.IndexOf('\0', StringComparison.Ordinal);
        return nul >= 0 ? demangled![..nul] : demangled;
    }

    // LLVM has no depth limit: a name nested deeper than its stack crashes it. Here the depth is bounded by
    // DemangleDepth.Max, which is deterministic; a name within it that the caller's stack cannot hold (a 1 MB thread
    // pool thread, a deep caller) is demangled again on a thread with this fixed stack, which always holds it. So
    // whether a name demangles depends on neither the thread nor the JIT tier, except where threads are unavailable
    // (browser and WASI), where such a name stays mangled.
    private const int DeepNameStackSize = 16 * 1024 * 1024;

    private static string? DemangleItaniumBytes(string mangledName)
    {
        try
        {
            return DemangleItaniumOnThisThread(mangledName);
        }
        catch (InsufficientExecutionStackException) when (!OperatingSystem.IsBrowser() && !OperatingSystem.IsWasi())
        {
            string? result = null;
            var thread = new Thread(() =>
            {
                try
                {
                    result = DemangleItaniumOnThisThread(mangledName);
                }
                catch (InsufficientExecutionStackException)
                {
                }
            }, DeepNameStackSize) { IsBackground = true };
            thread.Start();
            thread.Join();
            return result;
        }
        catch (InsufficientExecutionStackException)
        {
            return null;
        }
    }

    private static string? DemangleItaniumOnThisThread(string mangledName)
    {
        DemangleDepth.Reset();
        try
        {
            Node? ast = new ItaniumParser(mangledName, 0, mangledName.Length).Parse();
            if (ast is null)
                return null;
            var ob = new DemangleOutput();
            ast.Print(ob);
            return ob.ToString();
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or DemangleDepth.TooDeepException)
        {
            return null;
        }
    }

    // The name from start on; only a name that is a candidate for a demangler is copied.
    private static string? NonMicrosoftDemangle(string name, int start, bool canHaveLeadingDot)
    {
        bool dot = canHaveLeadingDot && start < name.Length && name[start] == '.';
        if (dot)
            start++;

        ReadOnlySpan<char> span = name.AsSpan(start);
        string? demangled;
        if (IsItaniumEncoding(span))
            demangled = DemangleItaniumBytes(start == 0 ? name : name[start..]);
        else if (span.StartsWith("_R", StringComparison.Ordinal))
            demangled = RustDemangler.Demangle(start == 0 ? name : name[start..]);
        else
            return null;

        demangled = CString(demangled);
        return demangled is null ? null : dot ? "." + demangled : demangled;
    }

    private static bool IsItaniumEncoding(ReadOnlySpan<char> span)
    {
        if (span.StartsWith("__alloc_token_", StringComparison.Ordinal))
        {
            span = span["__alloc_token_".Length..];
            if (span.Length > 0 && char.IsAsciiDigit(span[0]))
            {
                while (span.Length > 0 && char.IsAsciiDigit(span[0]))
                    span = span[1..];
                if (span.StartsWith("_", StringComparison.Ordinal))
                    span = span[1..];
            }
        }

        // The Itanium demangler accepts one to four underscores before the Z.
        int pos = span.IndexOfAnyExcept('_');
        return pos > 0 && pos <= 4 && span[pos] == 'Z';
    }
}

/// <summary>
/// The nesting depth of the Itanium demangler: every recursion in the parser and through the node graph passes
/// <see cref="Enter"/>, so a name nested deeper than <see cref="Max"/> fails to demangle (and stays mangled) instead of
/// overflowing the stack, as in LLVM's Rust demangler (which stops at 500).
/// </summary>
internal static class DemangleDepth
{
    /// <summary>
    /// Far beyond any real name, and beyond what LLVM itself demangles on an 8 MB stack before it crashes for the
    /// nestings measured (DemanglerTests.DeepNamesDemangleTheSameOnAnyThread); small enough for the fixed 16 MB stack.
    /// </summary>
    public const int Max = 10_000;

    [ThreadStatic]
    private static int t_depth;

    public static void Reset() => t_depth = 0;

    public static void Enter()
    {
        if (++t_depth > Max)
            throw new TooDeepException();
        System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
    }

    public static void Exit() => t_depth--;

    internal sealed class TooDeepException : Exception;
}
