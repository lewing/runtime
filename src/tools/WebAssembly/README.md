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
* `Binary/WasmSections.cs`, `Linker/WasmLdCommandLine.cs` and `Linker/WasmLdDriver.cs` exist only here.
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

Linking clang objects now matches wasm-ld on the parity scenarios: memory, table and stack definition, data layout,
constructors, `--allow-undefined`, `--export`, the entry point, lazy archive members, COMDAT, and the relocation types
ILCompiler's `WasmObjectWriter` emits. The parity tests (`WasmLdParityTests`) skip the remaining divergence with its
reason. Remaining gaps:

* `__stack_pointer` is always defined, even when nothing references it. wasm-ld omits it, so the output has one extra
  global.
* TLS (`__tls_base`, TLS segments and relocations) is not supported.
* Shared memory and atomics (`--shared-memory`, `0xFE` instructions) are not supported. Threaded builds can't be linked
  (Blazor-Playground/nesm#572).
* `.debug_str` is not merged across inputs.
* Component output (`--component-type`, wasm-component-ld) is not implemented.

## Native tools in WebAssembly SDK app builds

| Tool | Where | Status |
|------|-------|--------|
| `llvm-size` (data section size of AOT objects for the initial heap size) | Mono, `WasmApp.Common.targets` | Replaced by `WasmGetDataSectionSizes` when `WasmUseManagedToolchain` is `true`, the default on .NET MSBuild. The output matches llvm-size. |
| `wasm-opt --strip-dwarf` post-link pass (feature flags only, no `-O`) | Mono browser and WASI, `_RunWasmOptPostLink` | `WasmStripWithManagedToolchain=true` uses `WasmStripDebugInfo`. It removes the same sections (`name`, `.debug_*`) but doesn't re-encode code, so it's opt-in until code LEB compaction exists (Blazor-Playground/nesm#570). |
| `wasm-ld` via `clang` | WASI Mono app link, WASI CoreCLR relink, NativeAOT WASI | `WasmLink` task / `wasm-link` are ready to evaluate behind a switch. Threads, TLS and component output are still missing (see the gaps above). |
| `wasm-component-ld` / `--component-type` | WASI CoreCLR host, NativeAOT `wasm32-wasip2` | Not supported (Blazor-Playground/nesm#569). |
| `llvm-ar` | NativeAOT `NativeLib=Static` | Needs an archive writer with a symbol index (Blazor-Playground/nesm#570). |
| `wasm-tools parse` / `validate`, `wasm-merge`, `wasm-tools component unbundle` | WASI ReadyToRun composition (`ComposeWasiReadyToRun`) | WAT parsing and validation exist here. Module merge and component unbundle are needed (Blazor-Playground/nesm#570). The task is in WasmAppBuilder, which also targets .NET Framework. |
| `wasm-opt -O*` | CoreCLR browser relink and ReadyToRun images | Optimizer; not a replacement target. |
| `emcc` link | Browser (Mono and CoreCLR) | Also generates the JS glue, so wasm-ld can't be swapped out here. |
| `emcc` / `clang -c` | Native compiles | C compiler; not a replacement target. |

The managed tasks live in `WasmBuildTasks`, which targets .NET only, and are declared with `Runtime="NET"`.
`WasmUseManagedToolchain` defaults to `false` on .NET Framework MSBuild until the .NET task host is validated there.

## Where native tools are used today

* **Browser (Mono and CoreCLR):** `emcc` runs wasm-ld together with Emscripten's post-link and JS glue generation
  (`dotnet.native.js`). See `src/mono/browser/build/BrowserWasmApp*.targets` and
  `src/mono/browser/runtime/CMakeLists.txt`. Replacing wasm-ld there would also require reproducing that post-link.
* **WASI NativeAOT:** clang drives wasm-ld (`src/coreclr/nativeaot/BuildIntegration/Microsoft.NETCore.Native.Wasm.targets`).
  This is the most direct replacement target.
* **WASI CoreCLR:** corerun links with wasm-component-ld (`src/coreclr/hosts/corerun/CMakeLists.txt`,
  `src/mono/wasi/build/WasiApp.CoreCLR.targets`). ReadyToRun composition uses binaryen's wasm-merge
  (`src/tasks/WasmAppBuilder/ComposeWasiReadyToRun.cs`).
* **WASI Mono:** clang link in `src/mono/wasi/build/WasiApp.targets`.

## Overlap with ILCompiler's ObjectWriter

`src/coreclr/tools/Common/Compiler/ObjectWriter/Wasm*` partly duplicates this library: section and LEB encoding,
the `linking` and `reloc.*` sections, and the name section. The ILCompiler front end, which maps nodes to symbols and
relocations, and the Webcil layout are not redundant. This library's instruction encoder works from the WAT AST and
has no programmatic instruction builder. A low-risk first step is to validate `WasmObjectWriter` output with
`ModuleReader`/`WasmModuleValidator` in ILCompiler tests. Replacing serialization would then mean building a
`WasmModule` and emitting it with `SectionMerger`, after checking memory use for large objects and deterministic
output.
