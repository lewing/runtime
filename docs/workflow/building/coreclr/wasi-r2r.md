# CoreCLR ReadyToRun (R2R) on WASI

> **Status: this works.** On the `lewing-wasi-r2r-rebuild-delta` branch a clean WASI build
> genuinely activates R2R and runs real xUnit suites (12166/12166 tests discovered on
> `System.Text.Json.Tests`) with zero R2R signature-mismatch traps and zero GC-related NREs.
>
> If you reproduce a "WASI R2R doesn't work" result, suspect — in this order — stale or
> mis-provenanced build artifacts, a misread of `DOTNET_ReadyToRun` on a baked composite, or a
> missing upstream cherry-pick. See [Traps](#traps) before concluding there is a platform
> limitation. Several sessions have burned days re-deriving this.

This page documents the hand-driven flow for building, compiling, and running a **composite R2R
image on CoreCLR/WASI**. There is deliberately no `PublishReadyToRun` or WasmAppBuilder switch that
does wasm R2R today: you invoke `crossgen2` directly, deploy into a flat directory, and run under
`wasmtime`.

The same `crossgen2` and codegen paths serve `browser` as well — wasm R2R codegen keys on the
architecture (`IsWasm`), not on the target OS, so browser is a useful control when isolating a bug.
Only the `--targetos` value and the host layer differ.

- Supporting tooling lives in [`eng/wasi-r2r/`](../../../../eng/wasi-r2r/README.md).
- General wasm CoreCLR build/debug instructions are in [`wasm.md`](wasm.md).

## Prerequisites

| Tool | Notes |
| --- | --- |
| wasi-sdk | Auto-provisioned by `./build.sh ... -os wasi`. Do **not** set `WASI_SDK_PATH` manually. |
| `wasmtime` | Used directly, not vendored by the repo. |
| `wasm-tools`, `wasm-merge`, `wasm-objdump` | Only needed for the splice pipeline and for inspecting images. |
| `Nesm.dll` | Only needed for the splice pipeline. See [`eng/wasi-r2r/README.md`](../../../../eng/wasi-r2r/README.md). |
| emscripten | Browser target only, also auto-provisioned. On macOS you may need `export EMSDK_PYTHON=/opt/homebrew/bin/python3.12` if the system Python is too old. |

## Build

From the repo root, with the WASI R2R branch checked out:

```bash
./build.sh clr+host -os wasi -c Release
./build.sh libs+packs -os wasi -c Release
```

The Mono-targeted packs sub-step (the `sfxproj` that produces `dotnet.wasm`) is **expected to fail**
on a CoreCLR-only WASI build. Ignore that specific failure — the libraries and the runtime layout
still succeed, and that is all R2R needs.

Faster iteration once a full build exists:

```bash
./build.sh clr.aot -os wasi -c Release      # crossgen2 + the wasm-targeting JIT only
./build.sh clr.corelib -os wasi -c Release  # System.Private.CoreLib IL only
```

A `clr`-only build is **not** sufficient for an R2R run. You also need the wasm-targeting crossgen
JIT (from `clr.aot`) and the runtime pack implementation assemblies (from `libs+packs`), which serve
as both `crossgen2` references and deployment inputs. Never use reference assemblies for either.

When you suspect stale artifacts, rebuild from clean rather than incrementally:

```bash
rm -rf artifacts/obj/coreclr/wasi.wasm.Release artifacts/bin/coreclr/wasi.wasm.Release
./build.sh clr+host -os wasi -c Release
```

### Where things land

| Artifact | Path |
| --- | --- |
| `crossgen2` | `artifacts/bin/coreclr/wasi.wasm.Release/<host-arch>/crossgen2/crossgen2` |
| CoreCLR CoreLib IL | `artifacts/bin/coreclr/wasi.wasm.Release/IL/System.Private.CoreLib.dll` |
| Framework IL | `artifacts/bin/microsoft.netcore.app.runtime.wasi-wasm/Release/runtimes/wasi-wasm/lib/net11.0/` |
| `corerun` component | `artifacts/bin/coreclr/wasi.wasm.Release/corerun` |
| `R2RDump` | `artifacts/bin/coreclr/wasi.wasm.Release/R2RDump/R2RDump.dll` |

> **Always take `System.Private.CoreLib.dll` from CoreCLR's own `IL/` directory.** The copy under the
> wasi-wasm runtime pack's `native/` directory is **Mono's** CoreLib even when you are building
> CoreCLR, and using it silently corrupts the whole pipeline. A quick check:
> `strings <corelib> | grep -c mono_` returns 0 for the correct one and >100 for Mono's. Every other
> framework assembly in the pack is runtime-neutral IL and is fine to use as-is.

## Compile the composite

Everything you want R2R-compiled goes in the positional input list; everything it merely references
goes in `--reference:`. Response files (`@file.rsp`, one argument per line) are the practical form
once the input set grows — [`eng/wasi-r2r/comp.rsp.template`](../../../../eng/wasi-r2r/comp.rsp.template)
is a ready-made starting point.

```bash
ROOT=$PWD
XGEN=$ROOT/artifacts/bin/coreclr/wasi.wasm.Release/arm64/crossgen2/crossgen2   # arm64 -> your host arch
CORELIB=$ROOT/artifacts/bin/coreclr/wasi.wasm.Release/IL/System.Private.CoreLib.dll
PACK=$ROOT/artifacts/bin/microsoft.netcore.app.runtime.wasi-wasm/Release/runtimes/wasi-wasm/lib/net11.0

REFS=""
for f in "$PACK"/*.dll; do
    [ "$(basename "$f")" = "System.Private.CoreLib.dll" ] && continue
    REFS="$REFS --reference:$f"
done

DOTNET_ROOT=$ROOT/.dotnet "$XGEN" \
    --composite -O --targetarch:wasm --targetos:wasi \
    --instruction-set base,simd128 \
    --codegenopt:JitWasmNyiToR2RUnsupported=1 \
    --codegenopt:JitWasmSimdNyiToR2RUnsupported=1 \
    $REFS \
    --out "$ROOT/r2rtest/out/composite-r2r.wasm" \
    "$CORELIB" "$PACK/System.Runtime.dll" "$PACK/System.Console.dll" "$ROOT/r2rtest/in/Hello.dll"
```

Notes:

- `JitWasmNyiToR2RUnsupported=1` makes JIT-NYI paths fall back to interpretation instead of hard
  failing the compile.
- `JitWasmSimdNyiToR2RUnsupported` has been verified to produce byte-identical output for `0` and
  `1` on current inputs, so either value is fine.
- The output extension is `.wasm`, but the file is a webcil-wrapped R2R PE image.
- Sanity check every composite before you try to run it:
  `dotnet artifacts/bin/coreclr/wasi.wasm.Release/R2RDump/R2RDump.dll <image.wasm>`. Machine type
  `65534` is the wasm placeholder and is expected.
- To debug codegen for one method, set `DOTNET_JitDisasm=<Type:Method>`, `DOTNET_JitGCDump=...`, or
  `DOTNET_JitDisasmSummary=1` on the **crossgen2** invocation — these are compile-time, not run-time.
- A `Debug`-configuration crossgen2 (`./build.sh clr.aot -os wasi -c Debug`) has assertions live and
  is the cheap way to confirm or rule out an invariant violation that a Release build would silently
  miscompile.

## Deploy and run

The run directory is flat and must contain the **full framework IL closure** (~182 assemblies), the
composite-bearing `corerun`, and the entry assembly:

```bash
DEST=$ROOT/r2rtest/run; rm -rf "$DEST"; mkdir -p "$DEST/comp"
cp "$ROOT/r2rtest/ccsym/corerun-composite-sym.wasm" "$DEST/"
cp "$ROOT/r2rtest/out"/System.*.wasm "$DEST/comp/"
cp "$ROOT/r2rtest/in/Hello.dll" "$DEST/"
PACK=$ROOT/artifacts/bin/microsoft.netcore.app.runtime.wasi-wasm/Release/runtimes/wasi-wasm
cp "$PACK"/lib/net11.0/*.dll "$DEST/"
cp "$CORELIB" "$DEST/"   # CoreCLR's CoreLib must win over the pack's Mono one
```

See [`eng/wasi-r2r/README.md`](../../../../eng/wasi-r2r/README.md) for how
`corerun-composite-sym.wasm` is produced, and for whether the splice step is still required at all.

```bash
cd "$DEST"
wasmtime run \
    -S http=y \
    -W exceptions=y,gc=y,function-references=y,tail-call=y,threads=y,simd=y,relaxed-simd=y \
    --env DOTNET_ReadyToRun=1 \
    --env CORE_ROOT=/core \
    --env APP_ASSEMBLIES=EXTERNAL \
    --env DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=true \
    --env DOTNET_WASI_PRINT_EXIT_CODE=1 \
    --dir "$PWD::/core" \
    corerun-composite-sym.wasm Hello.dll
```

- `CORE_ROOT` must exactly match the guest side of the `--dir` mapping.
- `-S http=y` is required even for tests that do no HTTP, because this `corerun` links
  `WasiHttpWorld` and instantiation fails without it.
- The reference `Hello` app exits 42, printed as `WASM EXIT 42`.

Running an xUnit suite is the same shape with a different entry assembly:

```bash
wasmtime run -S http=y -W exceptions=y,gc=y,function-references=y,tail-call=y,threads=y,simd=y,relaxed-simd=y \
    --env DOTNET_ReadyToRun=1 --env CORE_ROOT=/core --env APP_ASSEMBLIES=EXTERNAL \
    --env DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 \
    --dir "$PWD::/core" \
    corerun-composite-sym.wasm WasmTestRunner.dll System.Text.Json.Tests.dll
```

To produce a realistic deployment layout to compile against, build a library test for WASI first —
it lays out `AppBundle/managed/` with `corerun`, the framework, and `WasmTestRunner.dll`:

```bash
./dotnet.sh build /t:Test \
    src/libraries/System.Runtime/tests/System.Runtime.CompilerServices.Unsafe.Tests/System.Runtime.CompilerServices.Unsafe.Tests.csproj \
    /p:TargetOS=wasi /p:TargetArchitecture=wasm /p:RuntimeFlavor=coreclr /p:Configuration=Release
```

## Proving R2R is actually active

Do not infer this from output alone:

```bash
DOTNET_ReadyToRunLogFile=$PWD/r2r.log wasmtime run ... corerun-composite-sym.wasm Hello.dll
grep -c "initialized successfully" r2r.log   # >0 means R2R images were really loaded
grep -c "header not found"          r2r.log   # >0 is expected for assemblies you did not crossgen
```

A live debugger tracepoint on an R2R'd method is the other reliable test.

## Traps

These are the failure modes that have repeatedly produced the false conclusion that CoreCLR R2R on
WASI is broken.

**1. `DOTNET_ReadyToRun=0` and `=1` producing identical output does not mean R2R is inactive.**
When the composite is baked into `corerun*.wasm` rather than loaded externally, the environment
variable is a **no-op** — it only gates external R2R image loading, and R2R is unconditionally
active in that binary. Identical output is the *expected* result. Use `DOTNET_ReadyToRunLogFile` or
a debugger tracepoint instead. A prior investigation reached and later had to reverse exactly this
conclusion.

**2. A missing framework assembly manifests as an infinite loop, not an error.**
The run directory needs the full framework IL closure; `Console.WriteLine` transitively pulls in
`System.Threading` and more. With R2R off you get a clean `FileNotFoundException`; with R2R on the
same missing assembly hangs in the binder/EH path. **Always gate a suspected trap or hang with a
`DOTNET_ReadyToRun=0` run first** — a clean `FileNotFoundException` means a deployment gap, not a
codegen bug.

**3. Stale or mixed-vintage artifacts.**
Mixing an old CoreLib IL or JIT with a freshly built crossgen2 (or the reverse) has produced
phantom `NullReferenceException`s and phantom "config X is broken" results more than once. Before
trusting any negative result, check timestamps on `IL/System.Private.CoreLib.dll`, the `crossgen2`
binary, and the JIT, and confirm with one clean rebuild of the exact configuration. When you are
handed a prebuilt `.wasm` rig, verify its provenance (`stat`, `sha256sum`) before believing anything
it tells you.

**4. `./build.sh clr.jit` does not refresh the JIT that crossgen2 loads.**
`crossgen2`'s directory holds its own *copy* of the wasm-targeting JIT
(`libclrjit_universal_wasm_<arch>.dylib`). A `clr.jit` build updates the copy under `artifacts/obj`
but not the one next to `crossgen2`, so JIT fixes are silently ignored. Either copy it over
manually or use `./build.sh clr.aot`.

**5. A bare `./build.sh clr -os wasi` link failure is a build-configuration gap, not a platform gap.**
Undefined symbols such as `PAL_ProbeMemory`, `shm_open`, or `shm_unlink` from `debughelp.cpp` and
`doublemapping.cpp` come from missing WASI PAL stubs
(`src/coreclr/pal/src/include/pal/wasi/pal_wasi_missing.h`), not from a fundamental limitation.
Build `clr+host`, which is the recipe recorded as working end to end.

**6. The branch must track upstream R2R/async thunk fixes.**
Dropping [#131167](https://github.com/dotnet/runtime/pull/131167)-family fixes regresses a fixed
async/R2R thunk-signature bug and reintroduces `call_indirect` signature-mismatch traps. After any
rebase, do one full rebuild before re-testing.

## Inspecting images

```bash
dotnet artifacts/bin/coreclr/wasi.wasm.Release/R2RDump/R2RDump.dll <image.wasm>
wasm-tools validate --features all <image.wasm>
wasm-tools print <image.wasm> | grep -c "(export"
wasm-objdump -d <image.wasm> | less
```

For live debugging of a trapping `call_indirect`, arm a pre-trap breakpoint at the known byte offset
rather than relying on an exception pause — the host wrapper catches and rethrows, so the trap never
surfaces as uncaught. When handing a `.wasm` fixture to another session for debugging, tag it with
its `sha256sum`; ambiguity between similarly named rigs has cost real debugging time.

## Known-broken

- Browser only: `Enum.ToObject`'s virtual dispatch to `GetTypeCodeImpl()` traps on a `call_indirect`
  return-type mismatch (expected `void`, actual `i32`). WASI dispatches the identical call cleanly on
  the same codegen; the suspected cause is emscripten-side R2R/interpreter thunk resolution.
- `src/tasks/WasmAppBuilder/coreclr/SignatureMapper.cs` has no `'V'` (v128) case in
  `TokenToSlotCount`, which should be 2 slots. Latent, in the same v128-thunk family.
