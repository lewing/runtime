// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

extern alias crossgen2;

using System;
using System.Diagnostics;
using System.IO;

using crossgen2::ILCompiler.PortableCallHelpers.WasmObject;

using Xunit;

namespace ILCompiler.ReadyToRun.Tests;

/// <summary>
/// The call-helper object lays out tables the runtime reads through callhelpers.hpp and minipal/entrypoints.h.
/// Compiles static_asserts of <see cref="CallHelperLayout"/> against those headers with the wasi-sdk clang.
/// </summary>
public class CallHelperLayoutTests
{
    private static string? WasiSdk => Environment.GetEnvironmentVariable("WASI_SDK_PATH") is { Length: > 0 } path && Directory.Exists(path) ? path : null;

    public static bool HasWasiSdk => WasiSdk is not null;

    [ConditionalFact(typeof(CallHelperLayoutTests), nameof(HasWasiSdk))]
    public void TableLayoutsMatchTheRuntimeHeaders()
    {
        string repo = FindRepoRoot();
        string source = Path.Combine(Path.GetTempPath(), $"callhelper-layout-{Guid.NewGuid():N}.cpp");
        File.WriteAllText(source, $$"""
            #include <stddef.h>
            #include <callhelpers.hpp>
            #include <minipal/entrypoints.h>
            static_assert(sizeof(void*) == {{CallHelperLayout.PointerSize}} && sizeof(size_t) == {{CallHelperLayout.PointerSize}}, "pointer size");
            static_assert(sizeof(StringToPortableSigThunk) == {{CallHelperLayout.KeyValueSize}}, "StringToPortableSigThunk size");
            static_assert(offsetof(StringToPortableSigThunk, key) == {{CallHelperLayout.KeyOffset}}, "StringToPortableSigThunk::key");
            static_assert(offsetof(StringToPortableSigThunk, value) == {{CallHelperLayout.ValueOffset}}, "StringToPortableSigThunk::value");
            static_assert(sizeof(Entry) == {{CallHelperLayout.KeyValueSize}}, "Entry size");
            static_assert(offsetof(Entry, name) == {{CallHelperLayout.KeyOffset}}, "Entry::name");
            static_assert(offsetof(Entry, method) == {{CallHelperLayout.ValueOffset}}, "Entry::method");
            static_assert(sizeof(ReverseThunkMapEntry) == {{CallHelperLayout.ReverseThunkEntrySize}}, "ReverseThunkMapEntry size");
            static_assert(offsetof(ReverseThunkMapEntry, hashCode) == {{CallHelperLayout.ReverseThunkHashOffset}}, "ReverseThunkMapEntry::hashCode");
            static_assert(offsetof(ReverseThunkMapEntry, Source) == {{CallHelperLayout.ReverseThunkSourceOffset}}, "ReverseThunkMapEntry::Source");
            static_assert(offsetof(ReverseThunkMapEntry, value.Target) == {{CallHelperLayout.ReverseThunkTargetOffset}}, "ReverseThunkMapEntry::value.Target");
            static_assert(offsetof(ReverseThunkMapEntry, value.EntryPoint) == {{CallHelperLayout.ReverseThunkEntryPointOffset}}, "ReverseThunkMapEntry::value.EntryPoint");
            """);

        try
        {
            string sdk = WasiSdk!;
            var startInfo = new ProcessStartInfo(Path.Combine(sdk, "bin", OperatingSystem.IsWindows() ? "clang++.exe" : "clang++"))
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            foreach (string argument in new[]
            {
                "--target=wasm32-unknown-wasip2",
                $"--sysroot={Path.Combine(sdk, "share", "wasi-sysroot")}",
                "-std=gnu++17",
                "-fsyntax-only",
                "-include", Path.Combine(repo, "src", "mono", "browser", "build", "coreclr_compat.h"),
                $"-I{Path.Combine(repo, "src", "coreclr", "vm", "wasm")}",
                $"-I{Path.Combine(repo, "src", "native")}",
                source,
            })
            {
                startInfo.ArgumentList.Add(argument);
            }

            using Process clang = Process.Start(startInfo)!;
            string output = clang.StandardOutput.ReadToEnd() + clang.StandardError.ReadToEnd();
            clang.WaitForExit();
            Assert.True(clang.ExitCode == 0, output);
        }
        finally
        {
            File.Delete(source);
        }
    }

    private static string FindRepoRoot()
    {
        for (string? dir = AppContext.BaseDirectory; dir is not null; dir = Path.GetDirectoryName(dir))
        {
            if (File.Exists(Path.Combine(dir, "src", "coreclr", "vm", "wasm", "callhelpers.hpp")))
                return dir;
        }

        throw new InvalidOperationException($"No repository root with src/coreclr/vm/wasm/callhelpers.hpp above {AppContext.BaseDirectory}.");
    }
}
