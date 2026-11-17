// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.NET.WebAssembly.Module;

namespace Microsoft.NET.WebAssembly.Binary;

/// <summary>Outcome of overlaying a split-DWARF sidecar onto a stripped module.</summary>
/// <param name="MergedSections">The <c>.debug_*</c> section names lifted from the sidecar.</param>
/// <param name="DebugInfoRefreshed">
/// True when the sidecar carried a <c>.debug_line</c> section, so the module's
/// pre-parsed <see cref="WasmModule.DebugInfo"/> line-program cache was rebuilt.
/// </param>
/// <param name="BuildIdMatch">
/// Tri-state build-id comparison: null when either side lacks a <c>build_id</c>
/// (nothing to compare); true/false when both are present and (mis)match.
/// </param>
public sealed record SidecarMergeResult(
    IReadOnlyList<string> MergedSections,
    bool DebugInfoRefreshed,
    bool? BuildIdMatch);

/// <summary>
/// Overlays the <c>.debug_*</c> custom sections of an external split-DWARF
/// sidecar (a companion <c>.debug.wasm</c>) onto a stripped module so that every
/// existing nesm DWARF reader — which keys off
/// <c>module.CustomSections.FirstOrDefault(cs =&gt; cs.Name == ".debug_*")</c> —
/// works unchanged. The sidecar is itself a valid wasm binary containing only
/// custom sections, so it is parsed through the same <see cref="ModuleReader"/>.
/// </summary>
public static class DwarfSidecarMerger
{
    /// <summary>
    /// Parse <paramref name="sidecarBytes"/> as a wasm module and overlay its
    /// <c>.debug_*</c> custom sections onto <paramref name="target"/>. Sidecar
    /// sections win on a name clash (the stripped module should have none, but a
    /// partial strip is tolerated). After overlay, the module's eagerly-parsed
    /// <see cref="WasmModule.DebugInfo"/> line-program cache is rebuilt from the
    /// merged <c>.debug_line</c> section.
    /// </summary>
    /// <param name="target">The stripped module to enrich (mutated in place).</param>
    /// <param name="sidecarBytes">Raw bytes of the sidecar <c>.debug.wasm</c>.</param>
    /// <param name="expectedBuildId">
    /// Optional build id from the stripped module's <c>external_debug_info</c>
    /// discovery, compared against the sidecar's own <c>build_id</c> section.
    /// </param>
    /// <returns>A summary of what was merged.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="target"/> is null.</exception>
    /// <exception cref="WasmFormatException">The sidecar bytes are not a parseable core module.</exception>
    /// <exception cref="InvalidOperationException">The sidecar carries no <c>.debug_*</c> sections.</exception>
    public static SidecarMergeResult Merge(
        WasmModule target,
        ReadOnlySpan<byte> sidecarBytes,
        byte[]? expectedBuildId = null)
    {
        ArgumentNullException.ThrowIfNull(target);

        var sidecar = ModuleReader.ParseCoreModule(sidecarBytes.ToArray());

        var debugSections = sidecar.CustomSections
            .Where(cs => cs.Name.StartsWith(".debug", StringComparison.Ordinal))
            .ToArray();

        if (debugSections.Length == 0)
            throw new InvalidOperationException(
                "The sidecar contains no .debug_* custom sections; it is not a DWARF debug companion.");

        // Build the merged section list OFF TO THE SIDE (sidecar wins on name clash),
        // then parse/validate the new line-program cache — all before touching the
        // target. This keeps the commit exception-free and atomic: a malformed sidecar
        // throws here, leaving the (possibly shared, already-loaded) target untouched.
        var overlaidNames = new HashSet<string>(debugSections.Select(s => s.Name), StringComparer.Ordinal);
        var newSections = new List<CustomSection>(target.CustomSections.Count + debugSections.Length);
        foreach (var cs in target.CustomSections)
            if (!overlaidNames.Contains(cs.Name))
                newSections.Add(cs);
        newSections.AddRange(debugSections);

        var newDebugInfo = BuildDebugLineInfo(newSections);
        bool refreshed = newDebugInfo is not null;

        bool? buildIdMatch = null;
        if (expectedBuildId is { Length: > 0 })
        {
            var sidecarBuildId = ExternalDebugInfo.TryReadBuildId(sidecar);
            if (sidecarBuildId is { Length: > 0 })
                buildIdMatch = expectedBuildId.AsSpan().SequenceEqual(sidecarBuildId);
        }

        // Atomic commit: reference writes only, so a concurrent reader sees either the
        // pre-merge or fully-merged module, never a torn in-place mutation. Publish the
        // line-program cache FIRST and the section list LAST: DWARF consumers gate on
        // `.debug_info` being present in CustomSections, so once they observe the merged
        // sections the refreshed DebugInfo is already in place. (Single-writer expected —
        // per repo convention, module structures require external locking for concurrent
        // mutation; only one attach/load writes a given module.)
        if (refreshed)
            target.DebugInfo = newDebugInfo;
        target.CustomSections = newSections;

        return new SidecarMergeResult([.. debugSections.Select(s => s.Name)], refreshed, buildIdMatch);
    }

    /// <summary>
    /// Rebuild <see cref="WasmModule.DebugInfo"/> from the module's current
    /// <c>.debug_line</c> section, in place. Mirrors <c>ModuleReader.ParseDebugSections</c>,
    /// which runs eagerly at read time. Prefer <see cref="Merge"/> for the split-DWARF
    /// path — it computes and commits atomically; this in-place variant is for callers
    /// that have already edited a module's sections directly.
    /// </summary>
    /// <returns>True if a <c>.debug_line</c> section was present and parsed.</returns>
    public static bool RefreshDebugLineInfo(WasmModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        var parsed = BuildDebugLineInfo(module.CustomSections);
        if (parsed is null)
            return false;
        module.DebugInfo = parsed;
        return true;
    }

    /// <summary>
    /// Parse DWARF line programs from a section list without mutating any module.
    /// Returns null when no <c>.debug_line</c> section is present.
    /// </summary>
    private static IReadOnlyList<DwarfLineProgram>? BuildDebugLineInfo(IReadOnlyList<CustomSection> sections)
    {
        var debugLine = sections.FirstOrDefault(s => s.Name == ".debug_line");
        if (debugLine is null)
            return null;

        var debugLineStr = sections.FirstOrDefault(s => s.Name == ".debug_line_str");
        var debugStr = sections.FirstOrDefault(s => s.Name == ".debug_str");

        return DwarfLineReader.Parse(
            debugLine.Content,
            debugLineStr?.Content,
            debugStr?.Content);
    }
}
