// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.NET.WebAssembly.Binary;
using Microsoft.NET.WebAssembly.Linker.Components;

namespace Microsoft.NET.WebAssembly.Linker;

/// <summary>
/// A <c>wasm-component-ld</c> compatible front end: a wasm-ld command line plus the component options
/// <c>--component-type</c>, <c>--adapt</c>, <c>--wasi-adapter</c> and <c>--skip-wit-component</c>. The core module is
/// linked by <see cref="WasmLinker"/> and wrapped by <see cref="ComponentEncoder"/>.
/// </summary>
/// <remarks>
/// Used by the <c>wasm-link</c> tool (with <c>--component</c>) and the <c>WasmLink</c> task, so a NativeAOT or CoreCLR
/// WASI link driven by clang (<c>-fuse-ld=</c>) can replace wasm-component-ld. Unlike wasm-component-ld there are no
/// built-in WASI preview1 adapters: a module that imports <c>wasi_snapshot_preview1</c> needs an explicit
/// <c>--adapt wasi_snapshot_preview1=&lt;file&gt;</c>. <c>--wasm-ld-path</c> is accepted and ignored.
/// </remarks>
public sealed class WasmComponentLdCommandLine
{
    private const string Preview1 = "wasi_snapshot_preview1";

    private WasmComponentLdCommandLine(WasmLdCommandLine wasmLd, bool emitComponent, List<string> componentTypeFiles, List<(string Name, string Path)> adapters, string? wasiAdapter)
    {
        WasmLd = wasmLd;
        EmitComponent = emitComponent;
        ComponentTypeFiles = componentTypeFiles;
        Adapters = adapters;
        WasiAdapter = wasiAdapter;
    }

    /// <summary>The wasm-ld part of the command line.</summary>
    public WasmLdCommandLine WasmLd { get; }

    /// <summary>Whether the core module is wrapped into a component.</summary>
    public bool EmitComponent { get; }

    /// <summary>Files passed with <c>--component-type</c>.</summary>
    public IReadOnlyList<string> ComponentTypeFiles { get; }

    /// <summary>Adapters passed with <c>--adapt [NAME=]FILE</c>.</summary>
    public IReadOnlyList<(string Name, string Path)> Adapters { get; }

    /// <summary>Value of <c>--wasi-adapter</c> (<c>command</c>, <c>reactor</c>, <c>proxy</c> or <c>none</c>), if given.</summary>
    public string? WasiAdapter { get; }

    /// <summary>
    /// Parses <paramref name="args"/>. A component is emitted when <paramref name="emitComponentByDefault"/> is set
    /// (wasm-component-ld behavior) or <c>--component</c> is given, unless <c>--skip-wit-component</c> is given.
    /// </summary>
    public static WasmComponentLdCommandLine Parse(IEnumerable<string> args, bool emitComponentByDefault = false)
    {
        List<string> argv = WasmLdCommandLine.ExpandResponseFiles(args).ToList();
        var wasmLdArgs = new List<string>(argv.Count);
        var componentTypes = new List<string>();
        var adapters = new List<(string, string)>();
        bool emitComponent = emitComponentByDefault;
        string? wasiAdapter = null;

        for (int i = 0; i < argv.Count; i++)
        {
            string arg = argv[i];
            int eq = arg.IndexOf('=');
            string name = arg.StartsWith("--", StringComparison.Ordinal) && eq > 0 ? arg[..eq] : arg;
            string Value() => eq > 0 && name != arg ? arg[(eq + 1)..]
                : i + 1 < argv.Count ? argv[++i]
                : throw new WasmLdArgumentException($"Missing value for '{name}'.");

            switch (name)
            {
                case "--component":
                    emitComponent = true;
                    break;
                case "--skip-wit-component":
                    emitComponent = false;
                    break;
                case "--wasm-ld-path":
                    _ = Value();
                    break;
                case "--component-type":
                    componentTypes.Add(Value());
                    break;
                case "--wasi-adapter":
                    wasiAdapter = Value();
                    if (wasiAdapter is not ("command" or "reactor" or "proxy" or "none"))
                        throw new WasmLdArgumentException($"Invalid --wasi-adapter '{wasiAdapter}'.");
                    break;
                case "--adapt":
                {
                    string value = Value();
                    int sep = value.IndexOf('=');
                    string path = sep >= 0 ? value[(sep + 1)..] : value;
                    adapters.Add((sep >= 0 ? value[..sep] : Path.GetFileName(path).Split('.')[0], path));
                    break;
                }
                default:
                    wasmLdArgs.Add(arg);
                    break;
            }
        }

        return new WasmComponentLdCommandLine(WasmLdCommandLine.Parse(wasmLdArgs), emitComponent, componentTypes, adapters, wasiAdapter);
    }

    /// <summary>Links, wraps into a component when requested, validates and writes the output.</summary>
    /// <returns>The bytes written.</returns>
    public byte[] Run(Action<string>? log = null, Action<string>? warn = null)
    {
        WasmLdCommandLine commandLine = WasmLd;
        if (string.IsNullOrEmpty(commandLine.OutputPath))
            throw new WasmLdArgumentException("No output file specified; use -o.");
        if (commandLine.InputFiles.Count == 0)
            throw new WasmLdArgumentException("No input files.");

        foreach (string warning in commandLine.Warnings)
            warn?.Invoke(warning);

        List<WasmLinkInput> inputs = WasmLdDriver.LoadInputs(commandLine.Inputs);
        byte[] bytes = WasmLdDriver.Link(inputs, commandLine.Options);
        ModuleReader.ReadModuleAndValidate(new MemoryStream(bytes));
        int coreSize = bytes.Length;

        if (EmitComponent)
        {
            var adapters = new List<ComponentAdapter>();
            foreach ((string name, string path) in Adapters)
                adapters.Add(new ComponentAdapter(name, File.ReadAllBytes(path)));

            if (WasiAdapter != "none" && !adapters.Exists(static a => a.Name == Preview1) && ImportsPreview1(bytes))
            {
                throw new WasmLdArgumentException(
                    $"The module imports '{Preview1}', and no built-in WASI adapters are available; pass --adapt {Preview1}=<{Preview1}.{WasiAdapter ?? "command"}.wasm>.");
            }

            var componentTypes = new List<byte[]>();
            foreach (string file in ComponentTypeFiles)
                componentTypes.AddRange(ComponentEncoder.ReadComponentTypeFile(File.ReadAllBytes(file)));

            bytes = ComponentEncoder.Encode(bytes, new ComponentEncoderOptions { ComponentTypes = componentTypes, Adapters = adapters });
        }

        string outputPath = Path.GetFullPath(commandLine.OutputPath);
        string? outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDirectory))
            Directory.CreateDirectory(outputDirectory);
        File.WriteAllBytes(outputPath, bytes);

        log?.Invoke(EmitComponent
            ? $"Linked {inputs.Count} module(s) into component '{outputPath}' ({coreSize} byte core module, {bytes.Length} bytes)."
            : $"Linked {inputs.Count} module(s) into '{outputPath}' ({bytes.Length} bytes).");
        return bytes;
    }

    private static bool ImportsPreview1(byte[] module)
    {
        foreach (Module.Import import in ModuleReader.ReadModule(new MemoryStream(module)).Imports)
        {
            if (import.ModuleName == Preview1)
                return true;
        }
        return false;
    }
}
