// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Toolchain.Tests.Linker;

using System.Diagnostics;

/// <summary>Finds and runs external tools (wasm-tools, wasm-merge, llvm-ar and the like) for oracle tests.</summary>
internal static class ExternalTool
{
    internal static string? FindOnPath(string tool) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir, OperatingSystem.IsWindows() ? tool + ".exe" : tool))
            .FirstOrDefault(File.Exists);

    internal static string CreateTempDirectory()
    {
        string dir = Path.Combine(Path.GetTempPath(), "nesm-wasmld-parity", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    internal static (int ExitCode, string Stdout, string Stderr) RunTool(string fileName, IEnumerable<string> args)
    {
        var startInfo = new ProcessStartInfo(fileName) { RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string arg in args)
            startInfo.ArgumentList.Add(arg);

        using Process process = Process.Start(startInfo)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout.Result, stderr);
    }

    internal static void RunToolChecked(string fileName, IEnumerable<string> args)
    {
        string[] argv = [.. args];
        var (exitCode, _, stderr) = RunTool(fileName, argv);
        Assert.True(exitCode == 0, $"{Path.GetFileName(fileName)} {string.Join(" ", argv)} failed with exit code {exitCode}:{Environment.NewLine}{stderr}");
    }
}
