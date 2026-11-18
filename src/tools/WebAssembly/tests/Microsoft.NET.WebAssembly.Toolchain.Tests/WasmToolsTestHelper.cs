// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Toolchain.Tests;

using Microsoft.DotNet.XUnitExtensions;
using System.Diagnostics;

internal static class WasmToolsTestHelper
{
    internal const string OracleVersion = "1.259.0";

    internal static string? Find()
    {
        string? configured = Environment.GetEnvironmentVariable("WASM_TOOLS");
        string path = Environment.GetEnvironmentVariable("PATH") ?? "";
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Find(configured, path, userProfile, IsRunnable);
    }

    internal static string? Find(
        string? configured,
        string path,
        string userProfile,
        Func<string, bool> isRunnable)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            return isRunnable(configured) ? configured : null;

        string executableName = OperatingSystem.IsWindows() ? "wasm-tools.exe" : "wasm-tools";
        foreach (string directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(directory, executableName);
            if (File.Exists(candidate) && isRunnable(candidate))
                return candidate;
        }

        string cargoTool = Path.Combine(userProfile, ".cargo", "bin", executableName);
        return isRunnable(cargoTool) ? cargoTool : null;
    }

    internal static string RequireOracle(string purpose)
    {
        string? tool = Find();
        if (tool is null)
            throw new SkipTestException($"{purpose} requires wasm-tools {OracleVersion}.");

        string version = GetVersion(tool);
        string[] versionParts = version.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(
            versionParts is ["wasm-tools", OracleVersion, ..],
            $"Selected '{tool}' reporting '{version}', but {purpose} requires wasm-tools {OracleVersion}.");
        return tool;
    }

    internal static string GetVersion(string tool)
    {
        using var process = Process.Start(new ProcessStartInfo(tool, "--version")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException($"Could not start {tool}.");
        string version = process.StandardOutput.ReadToEnd().Trim();
        string standardError = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(5_000), $"'{tool} --version' timed out.");
        Assert.True(
            process.ExitCode == 0,
            $"'{tool} --version' exited with {process.ExitCode}: {standardError}");
        return version;
    }

    private static bool IsRunnable(string candidate)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(candidate, "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            return process is not null
                && process.WaitForExit(5_000)
                && process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
