// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Binary;

/// <summary>
/// Resolves the on-disk location of a split-DWARF sidecar for the NATIVE load path.
/// Policy: the declared <c>external_debug_info</c> path is reduced to its basename and
/// resolved relative to the stripped module's own directory, so a hostile or stale
/// absolute/<c>..</c> path can never escape the module's folder. An explicit caller
/// override bypasses that reduction (deliberate user intent) but is still resolved
/// relative to the module directory when itself relative. Remote (URL) references are
/// rejected here — CDP/browser URL fetch is a separate follow-up slice.
/// </summary>
public static class SidecarPathResolver
{
    /// <summary>
    /// Compute the absolute path to load a sidecar from. Does not touch the
    /// filesystem — the caller checks existence and reads the bytes.
    /// </summary>
    /// <param name="mainModuleDir">Directory of the stripped <c>.wasm</c> (may be null → CWD).</param>
    /// <param name="declaredPath">The path recorded in <c>external_debug_info</c> (may be null when only an override is used).</param>
    /// <param name="overridePath">Explicit caller override (<c>debug_info_path</c>); wins when set.</param>
    /// <param name="resolvedPath">The resolved absolute path on success.</param>
    /// <param name="error">A human-readable reason on failure.</param>
    /// <returns>True when a local path was resolved; false with <paramref name="error"/> set otherwise.</returns>
    public static bool TryResolve(
        string? mainModuleDir,
        string? declaredPath,
        string? overridePath,
        out string resolvedPath,
        out string? error)
    {
        resolvedPath = string.Empty;
        error = null;

        string baseDir = string.IsNullOrEmpty(mainModuleDir)
            ? Directory.GetCurrentDirectory()
            : mainModuleDir;

        if (!string.IsNullOrEmpty(overridePath))
        {
            if (IsUrl(overridePath))
            {
                error = "debug_info_path override is a URL; native sidecar load requires a local file path.";
                return false;
            }
            resolvedPath = Path.GetFullPath(
                Path.IsPathRooted(overridePath) ? overridePath : Path.Combine(baseDir, overridePath));
            return true;
        }

        if (string.IsNullOrEmpty(declaredPath))
        {
            error = "No external_debug_info path was declared and no override was supplied.";
            return false;
        }

        if (IsUrl(declaredPath))
        {
            error = $"external_debug_info points at a URL ('{declaredPath}'); native load requires a local sidecar. " +
                    "Provide a local debug_info_path override (CDP/browser URL fetch is a follow-up).";
            return false;
        }

        // Security: reduce to basename so an absolute or ../.. path can't escape the module folder.
        string basename = Path.GetFileName(declaredPath.Replace('\\', '/'));
        if (string.IsNullOrEmpty(basename))
        {
            error = $"external_debug_info path '{declaredPath}' has no file component.";
            return false;
        }

        resolvedPath = Path.GetFullPath(Path.Combine(baseDir, basename));
        return true;
    }

    private static bool IsUrl(string p) =>
        p.Contains("://", StringComparison.Ordinal);
}
