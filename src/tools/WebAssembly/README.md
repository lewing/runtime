# Managed WebAssembly toolchain (experimental)

Experimental import of the [nesm](https://github.com/Blazor-Playground/nesm) toolchain into dotnet/runtime. The
goal is to evaluate replacing native wasm tools (wasm-ld, and eventually duplicated ObjectWriter code) with a
tested managed implementation.

## Summary

The experiment imports nesm's toolchain as `Microsoft.NET.WebAssembly.Toolchain`. It then uses that library to replace
native wasm tools in CoreCLR and NativeAOT builds, and the hand-written wasm encoding in ILCompiler and crossgen2. Mono
is out of scope. Everything below is local and opt-in; defaults are unchanged.

**What replaced what**

| Replaced | By | How to opt in |
|----------|----|---------------|
| `wasm-component-ld` (wasm-ld and component wrapping) for NativeAOT WASI apps | `wasm-link --component`, via clang `-fuse-ld` | `IlcUseManagedWasmLinker=true`, `IlcManagedWasmLinkerPath=<wasm-link or wasm-link.dll>` |
| `llvm-ar` for NativeAOT WASI `NativeLib=Static` | `wasm-link ar` | `IlcUseManagedWasmArchiver=true` (defaults to `IlcUseManagedWasmLinker`) |
| `wasm-component-ld` for the WASI CoreCLR per-app host relink | `wasm-link --component`, with WIT text passed directly | `WasiUseManagedWasmLinker=true`, `WasiManagedWasmLinkerPath=<wasm-link or wasm-link.dll>` |
| `wasm-tools component unbundle`, `wasm-tools parse`/`validate` (shim), `wasm-merge` and the host replace step in WASI ReadyToRun composition | `ComponentCoreModules`, `WatCompiler`, `ModuleMerger`, in-process in `ComposeWasiReadyToRun` | `WasiR2RUseManagedTools=true` (needs MSBuild on .NET) |
| wasm-ld for places that already run MSBuild tasks | the in-process `WasmLink` task (`src/tasks/WasmBuildTasks`) | use the task |
| Hand-written wasm encoding in ILCompiler's and crossgen2's ObjectWriter | `Binary.Writing`: `WasmModuleWriter`, linking and relocation encoders, `DataSegmentEncoder`, `NameMapWriter`, `WasmCodeWriter`, `WasmModuleEntryWriter`, `WasmLeb128` | always on (not opt-in): −1,695/+571 lines in `src/coreclr/tools` |

**Results**

- `wasm-link` output is byte-identical to wasm-ld's on the captured NativeAOT UnitTests link (3,895,291 bytes).
- All 14 NativeAOT WASI smoke tests pass with `IlcUseManagedWasmLinker` and with the default tools.
- The WASI CoreCLR console sample runs under wasmtime with `WasiUseManagedWasmLinker`, and with
  `WasiR2RUseManagedTools`, with or without wasm-opt.
- ILCompiler and crossgen2 output is byte-identical before and after the ObjectWriter move, in both Release and Debug
  builds: 23 of 24 outputs, and the 24th, `TrimmingBehaviors.o`, differs between two runs of the same ilc.
  `ILCompiler.ReadyToRun.Tests` on wasi: 421 tests, 378 passed, 43 skipped, 0 failed.
- `./build.sh tools.wasmtoolchain -test`: 668 tests, 664 passed, 4 skipped, 0 failed.

**Size and performance** (measured on an Apple M5 Max)

- ilc grew 99.5 KB (36,562,356 to 36,661,876 bytes) and crossgen2 68.7 KB (32,984,282 to 33,053,018 bytes). Both are
  NativeAOT-published with no trim or AOT warnings.
- ilc and crossgen2 wall time over the 24 objcmp compilations (single-threaded) went from 36.4 s to 37.3 s (+2.5%),
  averaged over two runs. Not profiled.
- Linking, median of five runs, wall time and peak RSS. Output is byte-identical to wasm-ld's in every case, as long as
  the output file name is the same (wasm-ld 23 records it as the module name).

  | Capture | wasm-ld (multi-threaded) | `wasm-link` NativeAOT | `wasm-link.dll` |
  |---------|--------------------------|-----------------------|-----------------|
  | NativeAOT UnitTests link (`artifacts/linkcap`, 3.9 MB output) | 0.07 s, 125 MB | 0.24 s, 259 MB | 0.81 s, 256 MB |
  | WASI CoreCLR host core link (`artifacts/hostcap`, 36 MB output) | 0.22 s, 317 MB | 0.59 s, 810 MB | 1.66 s, 794 MB |

  The NativeAOT build is about 3× faster than the dll. Turning off concurrent GC made it about 30% faster (0.34 s to
  0.24 s on linkcap) but raised its peak RSS by about 30% (195 to 259 MB). `wasm-link` is single-threaded.
- Since nesm#583 demangles the `name` section, the WASI CoreCLR host's core link is byte-identical to wasm-ld's (the
  hostcap row above). The size of the wrapped component hasn't been re-measured.
- With `WasiR2RUseManagedTools` the console sample keeps the host's DWARF (39.2 MB, against 36.2 MB by default), and
  its DWARF passes `llvm-dwarfdump --verify`; wasm-merge's output does not.

**Native tools that remain, and why**

- `clang`/`emcc` compiling C and C++: C compilers aren't replacement targets. clang also still drives the NativeAOT
  link (crt, sysroot, compiler-rt) and calls `wasm-link` through `-fuse-ld`.
- The browser `emcc` link: it also generates the JavaScript glue.
- `wasm-opt`: an optimizer, not a replacement target. This includes `--simplify-globals` in WASI ReadyToRun
  composition with `WasiR2ROptimizeComposite=true`.
- `wasm-tools validate` on the final WASI ReadyToRun component: the toolchain has no component-level validator yet.

**nesm gaps** (Blazor-Playground/nesm)

- No TLS (#569), shared memory or atomics (#572, #567), so threaded builds can't be linked.
- No component-level validation (#582).
- No built-in WASI preview1 adapters. NativeAOT and CoreCLR `wasm32-wasip2` builds don't need them.
- nesm still doesn't enforce two analyzer rules (CA1859, SA1136), so the project suppresses them (#571).
- `wasm-link` is still slower than wasm-ld and uses more memory (see above).

**Re-syncing:** run `sync-nesm.sh <fresh nesm clone> <commit>`, add any new tests to `nesm-test-files.txt`, run
`sync-nesm.sh --update-patch <clone>`, then `./build.sh tools.wasmtoolchain -test`. For changes that affect
ObjectWriter output, rerun the objcmp comparison. Details are in [Re-syncing from nesm](#re-syncing-from-nesm).

## Layout

| Path | Contents |
|------|----------|
| `Microsoft.NET.WebAssembly.Toolchain/` | Library: binary reader/writer (`Binary`), module model (`Module`, `Types`), validator (`Validation`), static linker (`Linker`), WAT compiler/disassembler (`Wat`). |
| `wasm-link/` | `wasm-link`, a wasm-ld compatible command-line front end. `wasm-link_publish.csproj` also publishes it as a NativeAOT executable for the host (`artifacts/bin/wasm-link/<config>/aot/`) when `UseNativeAotForComponents` is set. |
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
* `Linker/WasmComponentLdCommandLine.cs` (wasm-component-ld options) and the tests' `XunitV3Shims.cs` (nesm's tests use
  xunit v3's `TestContext`) exist only here. `sync-nesm.sh` leaves files that aren't in `nesm-files.txt` alone.
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
| `wasm-component-ld` (wasm-ld plus component wrapping), driven by `clang` | NativeAOT WASI (`src/coreclr/nativeaot/BuildIntegration/Microsoft.NETCore.Native.Wasm.targets`) | Opt in with `IlcUseManagedWasmLinker=true` (set `IlcManagedWasmLinkerPath` to `wasm-link` or `wasm-link.dll`; given the dll, the launcher prefers the NativeAOT build in `../aot/` and otherwise runs the dll with dotnet). clang runs the managed linker through `-fuse-ld=<launcher>`, which links the core module and builds the component (`wasm-link --component`). All 14 NativeAOT WASI smoke tests pass. On the same objects the core module is identical to wasm-ld's; the component is valid, has an equivalent WIT world, and is about 5 KB smaller than wasm-component-ld's. |
| `llvm-ar` | NativeAOT WASI static libraries (`NativeLib=Static`, same targets) | Opt in with `IlcUseManagedWasmArchiver=true`, which defaults to `IlcUseManagedWasmLinker`. A launcher runs `wasm-link ar`. On a NativeAOT static library the archive, including its symbol table, is byte-identical to llvm-ar's. |
| `wasm-component-ld`, driven by `clang++` | WASI CoreCLR per-app host relink (`src/mono/wasi/build/WasiApp.CoreCLR.targets`) | Opt in with `WasiUseManagedWasmLinker=true` and `WasiManagedWasmLinkerPath` (the same executable-or-dll resolution as above). The console sample runs under wasmtime with the NativeAOT `wasm-link`. Its core module is byte-identical to wasm-ld's, and the component has an equivalent WIT world. |
| `wasm-tools parse`/`validate`, `wasm-merge`, `wasm-tools component unbundle`, optional `wasm-opt --simplify-globals` | WASI ReadyToRun composition (`src/tasks/WasmAppBuilder/ComposeWasiReadyToRun.cs`) | Opt in with `WasiR2RUseManagedTools=true`. Unbundling the host, compiling and validating the shim, merging and replacing the host module then run in-process; the task needs MSBuild on .NET. On the console sample the app runs under wasmtime, and the host module, the shim and the replace step are byte-identical to the native path. The merged module differs from wasm-merge's but keeps the host's DWARF (39.2 MB, against 36.2 MB for the default build). Its DWARF passes `llvm-dwarfdump --verify` and keeps 486,100 line-table rows. wasm-merge's keeps the line-table prologues but no rows, and fails verification with 10,411 range errors. The managed module also has 267 KB less code. Still native: `wasm-tools validate` of the final component, and `wasm-opt --simplify-globals` when `WasiR2ROptimizeComposite=true` (the managed replace step then takes wasm-opt's output). Without wasm-opt, a C# pass folds the constant globals. |
| `emcc` link | Browser CoreCLR relink (`src/mono/browser/build/BrowserWasmApp.CoreCLR.targets`) | Also generates the JS glue, so wasm-ld can't be swapped out here. |
| `wasm-opt -Oz`/`-O2` (opt-in) | Browser CoreCLR relink and ReadyToRun images | Optimizer; not a replacement target. |
| `emcc` / `clang -c` | Native compiles | C compiler; not a replacement target. |

## ILCompiler and crossgen2 ObjectWriter

`src/coreclr/tools/Common/Compiler/ObjectWriter` (`WasmRelocatableObjectWriter` for ILCompiler `.o` files,
`WebCilObjectWriter` for crossgen2's Webcil images) serializes through this library's `Binary.Writing` API:

- `WasmModuleWriter` writes the header and frames every section, including Webcil's alignment padding section.
- `SymbolTableEncoder`, `SegmentInfoEncoder` and `LinkingSectionEncoder` write the `linking` section, and
  `RelocationSectionEncoder` writes the `reloc.*` sections.
- `DataSegmentEncoder` writes data-segment headers, and `NameMapWriter` writes the `name` section.
- `WasmCodeWriter` and `WasmOp` encode the instructions of writer-generated stubs and constant expressions
  (`WasmInstructions.cs` only maps symbol operands to relocations).
- `WasmModuleEntryWriter` and `WasmExternalKind` encode the entries of the type, import, function, global, export
  and element sections, and the data count.
- `WasmLeb128` is used for LEB128 encoding and decoding, and `WasmSectionId` for section IDs.

Runtime's own section framing, padding section, linking, relocation, data-segment-header, name-section and section-entry
encoding, its instruction encoder, `IWasmEncodable` and its duplicate enums (`WasmExprKind`, `WasmSectionType`,
`WasmExportKind`, `WasmExternalKind`, `WasmLimitType`) were deleted. No hand-written wasm encoding is left outside
Webcil's own format. Runtime still calls `WasmLeb128` directly for size prefixes and when it rewrites already-encoded
bodies. The node-to-symbol front end, relocation
resolution, data-segment layout and Webcil layout stay in runtime.

- `ILCompiler.Compiler` and `ILCompiler.ReadyToRun` have a `ProjectReference` to this library, not linked source. The
  library's types are public, so linking its source would collide in tests that reference both. ilc and crossgen2
  are NativeAOT-published with no trim or AOT warnings. Compared with builds before either referenced the library,
  ilc grew 99.5 KB (36,562,356 to 36,661,876 bytes) and crossgen2 68.7 KB (32,984,282 to 33,053,018 bytes).
- Output is byte-identical before and after. `artifacts/objcmp/run.sh` (a local script, not committed) runs ilc on
  the 14 NativeAOT WASI smoke-test response files, and crossgen2 on CoreLib, five framework libraries and a
  composite. `TrimmingBehaviors.o` also differs between two runs of the same ilc, so it's excluded.
- `ILCompiler.ReadyToRun.Tests` validates every crossgen2 wasm output with `ModuleReader.ReadModuleAndValidate`.
- Nesm's `ObjectWriterReferenceTests` and `WasmCodeWriterTests.RuntimeCodeReemitsByteIdentically` re-encode
  runtime outputs and re-emit every code body when `NESM_OBJCMP_DIR` points at an objcmp `out` directory.
