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

* Source: nesm `src/Nesm/{Types,Module,Validation,Binary,Linker,Wat}` (the `Nesm.Toolchain` package subset, excluding
  `WastRunner`) at the commit in `nesm-commit.txt`, with namespaces renamed from `Nesm.*` to
  `Microsoft.NET.WebAssembly.*`.
* `runtime-local.patch` holds the edits made here: fixes for the repo's analyzer rules (removed unused parameters,
  members made static, and so on), the tool name recorded in the producers section, and a fix to memarg skipping in the body validator so memory64 offsets and
  multi-memory SIMD memargs parse.
* `Linker/WasmLdCommandLine.cs` and `Linker/WasmLdDriver.cs` exist only here.
* Tests that executed modules through nesm's interpreter now assert structure instead: exports and signatures,
  identical function bodies after a round trip, and output validation. Tests that need the interpreter, the
  WebAssembly spec-testsuite submodule, or nesm build artifacts were not ported.

## Re-syncing from nesm

```bash
src/tools/WebAssembly/sync-nesm.sh <nesm-checkout> [<commit>]       # import, rename, re-apply runtime-local.patch
src/tools/WebAssembly/sync-nesm.sh --update-patch <nesm-checkout>   # after editing imported files here
```

The import replaces the files listed in `nesm-files.txt`, renames namespaces, adds license headers, and then applies
`runtime-local.patch`. Files that exist only in this repo are left alone. Upstream the local edits to nesm when you
can so the patch shrinks. Tests are not synchronized. After a sync that fixes a gap, remove its entry from
`s_knownGaps` in `WasmLdParityTests`.

## Known gaps before the linker can replace wasm-ld

The parity tests (`WasmLdParityTests`) skip each known divergence with its reason. Summary:

* The `env.__linear_memory` and `env.__indirect_function_table` placeholder imports in relocatable objects are passed
  through instead of being replaced by a defined memory and table, and the memory is not exported as `memory`.
  `--import-memory` produces a second memory.
* `__stack_pointer` is not defined, and no stack is allocated.
* Data is laid out from address 0, and zero-initialized (`.bss`) data overlaps `.data`. A C function that updates a
  global array returns wrong results.
* `__wasm_call_ctors` is not synthesized from `WASM_INIT_FUNCS`.
* The entry symbol (`--entry`, default `_start`) becomes the wasm start section instead of being exported and kept
  alive, so a WASI command loses `_start`. A missing entry symbol is not reported as an error.
* `--allow-undefined` drops undefined functions instead of importing them.
* `--export` has no effect on defined functions that are not marked exported.
* The relocated code for a stack-using function fails validation.
* The toolchain-only body validator does not type-check instructions, so it does not catch invalid output. It also
  rejects all atomics (`0xFE`), which threaded builds need.
* Archives are always loaded whole (`--whole-archive` semantics) instead of being extracted lazily. COMDAT, TLS, and
  component wrapping (`--component-type`, wasm-component-ld) are not implemented.
* Relocation types that ILCompiler's `WasmObjectWriter` can emit but `RelocationEngine` does not patch:
  `R_WASM_MEMORY_ADDR_REL_SLEB`, `R_WASM_MEMORY_ADDR_I64`, `R_WASM_TABLE_INDEX_I64`, `R_WASM_MEMORY_ADDR_LOCREL_I32`.

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
