# Managed WebAssembly toolchain (experimental)

Experimental import of the [nesm](https://github.com/Blazor-Playground/nesm) toolchain into dotnet/runtime. The
goal is to evaluate replacing native wasm tools (wasm-ld, and eventually duplicated ObjectWriter code) with a
tested managed implementation.

## Layout

| Path | Contents |
|------|----------|
| `Microsoft.NET.WebAssembly.Toolchain/` | Library: binary reader/writer (`Binary`), module model (`Module`, `Types`), validator (`Validation`), static linker (`Linker`), WAT compiler/disassembler (`Wat`). |
| `wasm-link/` | `wasm-link`, a wasm-ld compatible command-line front end. |
| `tests/Microsoft.NET.WebAssembly.Toolchain.Tests/` | Ported nesm toolchain tests, command-line/driver tests, `WasmLink` task tests and wasm-ld parity tests. |
| `../../tasks/WasmBuildTasks/WasmLink.cs` | `WasmLink` MSBuild task that links in-proc using the same library. |

The tool and the task share `WasmLdCommandLine` (wasm-ld flag parsing, `@response` files, `-L`/`-l`) and
`WasmLdDriver`, so they accept the same options and behave the same way.

Build and test with:

```bash
./build.sh tools.wasmtoolchain -test
```

The parity tests compile C with the wasi-sdk from the shared wasm tool cache (`eng/wasm/WasmToolCache.props`) and
compare the managed linker's output with wasm-ld's. They are skipped when the wasi-sdk is not present.

## Provenance and differences from nesm

* Source: nesm `src/Nesm/{Types,Module,Validation,Binary,Linker,Wat,Debugging/Symbols}` (the `Nesm.Toolchain` package
  subset) at the commit in `nesm-commit.txt`, with namespaces renamed from `Nesm.*` to `Microsoft.NET.WebAssembly.*`.
* The project suppresses a few style and performance-suggestion analyzer rules that nesm doesn't enforce yet
  (Blazor-Playground/nesm#571), so re-syncs don't need local fixes.
* `runtime-local.patch` holds the remaining library edits. Today that is only the tool name recorded in the producers
  section.
* `Linker/WasmComponentLdCommandLine.cs` (wasm-component-ld options) exists only here. `sync-nesm.sh` leaves files that aren't in `nesm-files.txt` alone.
* The tests in `nesm-test-files.txt` come from nesm's `tests/Nesm.Tests`. `runtime-local-tests.patch` adapts them to
  run without nesm's interpreter: tests that executed modules assert structure instead (exports and signatures,
  identical function bodies after a round trip, validation), and tests that need the interpreter, the spec
  testsuite, or nesm build artifacts are removed.

## Re-syncing from nesm

```bash
src/tools/WebAssembly/sync-nesm.sh <nesm-checkout> [<commit>]       # import, rename, re-apply runtime-local.patch
src/tools/WebAssembly/sync-nesm.sh --update-patch <nesm-checkout>   # after editing imported files here
```

The import replaces the library files listed in `nesm-files.txt` and the tests listed in `nesm-test-files.txt`, renames
namespaces, adds license headers, and then applies `runtime-local.patch` and `runtime-local-tests.patch`. Files that
exist only in this repo are left alone. Resolve any rejected hunks, then run `--update-patch`. Upstream local edits to
nesm when you can so the patches shrink. After a sync that fixes a gap, remove its entry from `s_knownGaps` in
`WasmLdParityTests`.

## Known gaps before the linker can replace wasm-ld

A real NativeAOT WASI link (`nativeaot/SmokeTests/UnitTests`) now produces a core module byte-identical to wasm-ld's,
through both `wasm-link` and the `WasmLink` task. That link covers the ILCompiler object, the runtime and framework
archives, wasi-libc, libc++ and compiler-rt, with `-s` and `--compress-relocations`. After wrapping with
`wasm-tools component new`, the app passes under wasmtime.

Linking clang objects now matches wasm-ld on the parity scenarios: memory, table and stack definition, data layout,
constructors, `--allow-undefined`, `--export`, the entry point, lazy archive members, COMDAT, and the relocation types
ILCompiler's `WasmObjectWriter` emits, and defining `__stack_pointer` only when it's used (as LLD 23 does). The parity
tests (`WasmLdParityTests`) record any future divergence in `s_knownGaps` with its reason. Remaining gaps:

* nesm#583 added wasm-ld 23's demangled `name` section and producers merging. The size and byte comparisons below
  predate it and haven't been re-measured.
* TLS (`__tls_base`, TLS segments and relocations) is not supported.
* Shared memory and atomics (`--shared-memory`, `0xFE` instructions) are not supported. Threaded builds can't be linked
  (Blazor-Playground/nesm#572).
* No built-in WASI preview1 adapters: modules that import `wasi_snapshot_preview1` need `--adapt`. NativeAOT and CoreCLR
  `wasm32-wasip2` builds don't need one.

## Native tools in CoreCLR and NativeAOT WebAssembly app builds

Mono is out of scope. It is being removed.

| Tool | Where | Status |
|------|-------|--------|
| `wasm-component-ld` (wasm-ld plus component wrapping), driven by `clang` | NativeAOT WASI (`src/coreclr/nativeaot/BuildIntegration/Microsoft.NETCore.Native.Wasm.targets`) | Opt in with `IlcUseManagedWasmLinker=true` (set `IlcManagedWasmLinkerPath` to `wasm-link.dll`). clang runs the managed linker through `-fuse-ld=<launcher>`, which links the core module and builds the component (`wasm-link --component`). All 14 NativeAOT WASI smoke tests pass. On the same objects the core module is identical to wasm-ld's; the component is valid, has an equivalent WIT world, and is about 5 KB smaller than wasm-component-ld's. |
| `llvm-ar` | NativeAOT WASI static libraries (`NativeLib=Static`, same targets) | Opt in with `IlcUseManagedWasmArchiver=true`, which defaults to `IlcUseManagedWasmLinker`. A launcher runs `wasm-link ar`. On a NativeAOT static library the archive, including its symbol table, is byte-identical to llvm-ar's. |
| `wasm-component-ld`, driven by `clang++` | WASI CoreCLR per-app host relink (`src/mono/wasi/build/WasiApp.CoreCLR.targets`) | Opt in with `WasiUseManagedWasmLinker=true` and `WasiManagedWasmLinkerPath`. The console sample runs under wasmtime. Its core module is identical to wasm-ld's apart from the `name` and `producers` sections, the component has an equivalent WIT world, and the host is 214 KB (0.6%) smaller than the default build, mostly from the shorter, non-demangled `name` section. |
| `wasm-tools parse`/`validate`, `wasm-merge`, `wasm-tools component unbundle`, optional `wasm-opt --simplify-globals` | WASI ReadyToRun composition (`src/tasks/WasmAppBuilder/ComposeWasiReadyToRun.cs`) | Opt in with `WasiR2RUseManagedTools=true`. Unbundling the host, compiling and validating the shim, merging and replacing the host module then run in-process; the task needs MSBuild on .NET. On the console sample the app runs under wasmtime, and the host module, the shim and the replace step are byte-identical to the native path. The merged module differs from wasm-merge's but keeps the host's DWARF (39.2 MB, against 36.2 MB for the default build). Its DWARF passes `llvm-dwarfdump --verify` and keeps 486,100 line-table rows. wasm-merge's keeps the line-table prologues but no rows, and fails verification with 10,411 range errors. The managed module also has 267 KB less code. Still native: `wasm-tools validate` of the final component, and `wasm-opt --simplify-globals` when `WasiR2ROptimizeComposite=true` (the managed replace step then takes wasm-opt's output). Without wasm-opt, a C# pass folds the constant globals. |
| `emcc` link | Browser CoreCLR relink (`src/mono/browser/build/BrowserWasmApp.CoreCLR.targets`) | Also generates the JS glue, so wasm-ld can't be swapped out here. |
| `wasm-opt -Oz`/`-O2` (opt-in) | Browser CoreCLR relink and ReadyToRun images | Optimizer; not a replacement target. |
| `emcc` / `clang -c` | Native compiles | C compiler; not a replacement target. |

## Overlap with ILCompiler's ObjectWriter

`src/coreclr/tools/Common/Compiler/ObjectWriter/Wasm*` partly duplicates this library: section and LEB encoding,
the `linking` and `reloc.*` sections, and the name section. The ILCompiler front end, which maps nodes to symbols and
relocations, and the Webcil layout are not redundant. This library's instruction encoder works from the WAT AST and
has no programmatic instruction builder.

Current state:

- `ILCompiler.ReadyToRun.Tests` validates every crossgen2 wasm output with `ModuleReader.ReadModuleAndValidate`.
- `ILCompiler.Compiler` has a `ProjectReference` to this library, not linked source. The library's types are public,
  so linking its source into shipping assemblies would collide in tests that reference both. ilc is published with
  NativeAOT, so only the used parts are kept: the reference grew ilc by about 9 KB, with no trim or AOT warnings.
  crossgen2 doesn't reference the library yet, because its Webcil writer doesn't use these types.
- `WasmRelocatableObjectWriter` uses this library's linking model (`SymbolType`, `SymbolFlags`, `RelocationType`)
  instead of its own copies. Its output is byte-identical before and after.
- The section, `linking`/`reloc.*` and name-section encoding stays in ObjectWriter. This library has no
  producer-side API that can reproduce that output byte for byte while streaming large objects
  ([nesm#581](https://github.com/Blazor-Playground/nesm/issues/581); instruction builder:
  [nesm#570](https://github.com/Blazor-Playground/nesm/issues/570)).
