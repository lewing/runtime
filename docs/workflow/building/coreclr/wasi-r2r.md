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
image on CoreCLR/WASI**: you invoke `crossgen2` directly, deploy into a flat directory, and run under
`wasmtime`. There is no app-level `PublishReadyToRun` switch for wasm, though `src/tests` does have
composite plumbing — see
[Building a composite through the runtime test infrastructure](#building-a-composite-through-the-runtime-test-infrastructure).

> **Claims here were last verified against `origin/main` on 2026-08-19.** This area moves quickly and
> several statements below were true when written and false within weeks. Anything asserting that
> something is *impossible* or *not plumbed* deserves a fresh check before you rely on it — the
> commands and diagnostics age far better than the gap analysis.

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

### How the WASI host finds R2R images

Unlike the browser host, the WASI host serves R2R images through a
`host_runtime_contract::external_assembly_probe` callback
([`src/coreclr/hosts/corerun/wasi_r2r_probe.hpp`](../../../../src/coreclr/hosts/corerun/wasi_r2r_probe.hpp),
shared by the standalone `corerun` and the per-app `wasihost`). Four things must line up, and if any
one is wrong every assembly reports `Ready to Run header not found` — which reads exactly like "R2R
is unsupported here":

1. **`APP_ASSEMBLIES=EXTERNAL` is mandatory.** The probe is auto-enabled only for `TARGET_BROWSER`;
   on WASI it is installed *only* when this variable is set. Without it the host uses a TPA list, the
   probe never runs, and no R2R image is ever consulted.
2. **The composite is served from a baked buffer, never from disk.** The probe answers the composite
   request out of `g_wasi_r2r_image`, a buffer inside the host module that the splice step fills via
   an active data segment aimed at the exported `wasi_r2r_image_base`. A stock
   `artifacts/bin/coreclr/wasi.wasm.Release/corerun` has an empty buffer, so it can *never* load a
   composite no matter how the files are laid out. You must run the spliced host.
3. **The composite must be named `composite-r2r.wasm`.** That literal is `WASI_R2R_COMPOSITE_NAME`,
   and it is the `ownerCompositeExecutable` each per-assembly stub names. Emit it under exactly that
   name from `crossgen2`.
4. **Per-assembly stubs live in a `comp/` subdirectory**, as `<CORE_ROOT>/comp/<AssemblyName>.wasm`
   — *not* flat beside the `.dll` the way the browser host wants them. The probe searches
   `CORE_LIBS` then `CORE_ROOT`.

Prefer setting `CORE_ROOT` over passing `-c`: it avoids WASI argument-plumbing quirks, and it must
match the guest side of the `--dir` mapping either way.

### How the browser host finds R2R images

The browser side has **two** `BrowserHost_ExternalAssemblyProbe` implementations. Both can activate
R2R, but they discover the image differently:

| | corerun ([`libCorerun.js`](../../../../src/coreclr/hosts/corerun/wasm/libCorerun.js)) | product ([`assets.ts`](../../../../src/native/libs/Common/JavaScript/host/assets.ts)) |
| --- | --- | --- |
| Lookup | `FS.readFile`, mapping `<name>.dll` → `<name>.wasm` | `Map` lookup, keyed by both virtual path and base name |
| Sizes | parses data segment 0 for `payloadSize`/`tableSize` | read from boot config |
| R2R imports | always passed | passed when `tableSize > 0` |
| Table setup | `wasmTable.grow(tableSize)` then `fillWebcilTable()` | same, gated on `tableSize > 0` |

Both supply the same host ABI — `stackPointer`, `rtlRestoreContextTag`, `asyncContinuation`, `table`,
`tableBase`, `imageBase` — which is defined by crossgen's `WasmObjectWriter` and must be kept in sync
across the two hosts. The product loader gained this in
[dotnet/runtime#131658](https://github.com/dotnet/runtime/pull/131658) (merged 2026-08-07), which
records `payloadSize` and `tableSize` in the boot config so assemblies can be stream-instantiated
rather than buffered.

> **Historical note.** Before #131658 the product loader instantiated with `{ webcil: { memory } }`
> only — no table, no bases — so R2R native code had nowhere to land and browser R2R ran under
> `corerun` exclusively. Material written before August 2026 (including earlier revisions of this
> page) reflects that state. If you are reading a claim that the product path "cannot activate R2R",
> check the date.

A flat directory driven by `corerun.js` remains the layout used for hand-driven R2R work, and is what
the runtime test infrastructure drives.

Several related consequences:

- **There is no bundle scenario to test on browser.** `AssemblyProbeExtension::Probe`
  ([`assemblyprobeextension.cpp`](../../../../src/coreclr/vm/assemblyprobeextension.cpp)) has a
  `Bundle::AppBundle->Probe(...)` arm and an external-probe arm, but neither browser host calls
  `Bundle::Init`, so `Bundle::AppIsBundle()` is false and the bundle arm is unreachable there.
- **Composite path resolution is directory-relative.** `OpenR2RFromPE`
  ([`nativeimage.cpp`](../../../../src/coreclr/vm/nativeimage.cpp)) builds the composite's path as
  *the component module's directory* plus the composite file name from the R2R header. In principle a
  nested layout would exercise this differently from a flat one — in practice a nested layout has
  been measured to simply not resolve, falling back to the interpreter silently (see
  [Proving R2R is actually active](#proving-r2r-is-actually-active)). A flat directory alongside
  `corerun.js` is not merely the conventional browser R2R layout, it is the only one known to work.
- **A composite crossgen'd as `foo.dll` is probed for as `foo.wasm`.** Combined with the silent
  fallback described below, a naming mismatch does not surface as an error — it produces a **false
  pass**, with the suite running green entirely under the interpreter. Deploy the composite under both
  names if in any doubt, and confirm activation independently.
- **`corerun` has no JS interop implementation.** The `SystemInteropJS_*` entry points in
  [`pinvoke_override.cpp`](../../../../src/coreclr/hosts/corerun/wasm/pinvoke_override.cpp) are
  deliberate linker-satisfying stubs, each `_ASSERTE(!"Should not be reached")`, so that `corerun`
  need not link `libSystem.Runtime.InteropServices.JavaScript.Native`. Any suite touching JS interop
  will assert there. That is a ceiling of the host rather than a fault in the code under test — check
  whether the assert fires *after* the run's `Finished` marker before treating it as a failure.

### Building a composite through the runtime test infrastructure

`src/tests` has **browser-aware composite R2R plumbing in tree**, and it is the closest thing to a
canonical wasm composite build. Prefer it over a hand-rolled `crossgen2` rig when you can: it is the
analogue of what `tests.ioslike.targets` does for Apple mobile.

- [`src/tests/Directory.Build.props`](../../../../src/tests/Directory.Build.props) sets
  `CrossGen2OutputFormat=wasm` when `TargetOS == browser`.
- [`CLRTest.CrossGen.targets`](../../../../src/tests/Common/CLRTest.CrossGen.targets) compiles
  `IL-CG2/*.dll` into `composite-r2r.wasm` when `CompositeBuildMode` is set, passing `-f wasm` and
  automatically adding `--codegenopt:JitWasmNyiToR2RUnsupported=1` and
  `--codegenopt:JitWasmSimdNyiToR2RUnsupported=1`.
- [`CLRTest.Execute.Bash.targets`](../../../../src/tests/Common/CLRTest.Execute.Bash.targets) then runs
  the result under `node --experimental-wasm-exnref --stack-size=8192 $CORE_ROOT/corerun.js -c $CORE_ROOT ...`.

Driven with `CompositeBuildMode=1` plus `src/tests/run.sh --runcrossgen2tests` (or by setting
`AlwaysUseCrossGen2` in a test project, which sets both).

Two switches that path passes and a hand-rolled rig usually does not — `--verify-type-and-field-layout`
and `--method-layout:random`. The first is worth adopting for any ABI or layout work: it embeds
`Verify_TypeLayout` / `Verify_FieldOffset` fixups that the runtime checks at load
([`jitinterface.cpp`](../../../../src/coreclr/vm/jitinterface.cpp)), raising a fatal error on
disagreement rather than letting a layout mismatch surface later as a miscompilation. It is genuinely
enforced, not advisory. Expect the composite to grow noticeably — one measured case went from 29.1 MB
to 37.3 MB (+28%) — so use it for validation runs rather than for artifacts you intend to ship or
profile.

Note `-f wasm` is optional in general: crossgen2 promotes the container format from PE to Wasm
automatically when the target architecture is wasm32, so a hand-rolled invocation that omits it still
produces a wasm container.

Composites scale further than you might expect: a 181-input composite (full framework plus a Checked
IL CoreLib, ~180 MB, 182 component images) has been run successfully under `corerun` with layout
verification enabled. Earlier browser work reported an ~8 MB ceiling on *synchronous* wasm compilation
in V8/Node, but the `corerun` path evidently does not hit it at that size — treat the ceiling as a
property of how a module is compiled rather than a hard limit on composite size.

### What is *not* plumbed: the runtime pack's R2R CoreLib

Distinct from the test path above, the **product runtime pack** does not ship an R2R CoreLib for wasm.
`crossgen-corelib.proj` routes the wasm R2R CoreLib to `System.Private.CoreLib.NotReadyYet.wasm`, and
`NotReadyYet` appears exactly once in the tree — its own definition, with no consumer. Enabling
`UseComposite` there also runs into `CopyR2RComponentCoreLib`, which expects a rewritten managed `.dll`
under `artifacts/obj`; that is a PE/Mach-O-shaped assumption and does not hold when crossgen2 emits
`.wasm` components to `BinDir`.

This is now the *remaining* gap rather than one of two: since #131658 the product browser loader can
instantiate an R2R image, so wiring the build glue here would no longer be blocked behind boot-path
work. Verified against `origin/main` as of 2026-08-19 — `NotReadyYet` still has no consumer.

## Proving R2R is actually active

**R2R failure is silent.** If the probe cannot resolve an image — wrong layout, wrong name, missing
file — the runtime falls back to the interpreter and the run completes normally, with no diagnostic.
A passing browser or WASI R2R result is therefore *not* evidence that R2R ran. Treat every green
result as unproven until you have independent confirmation.

**Comparing program output cannot supply that confirmation.** R2R is designed to be
behaviour-transparent: a correct composite computes exactly what the interpreter computes. So
identical output is equally consistent with "R2R ran correctly" and "R2R never ran at all", and no
amount of output diffing separates them.

That includes deleting the composite and re-running. It is a cheap first pass, and a *difference* is
informative — it means the composite changed observable behaviour, which is itself a bug worth
chasing. But a null result proves nothing whenever the IL assemblies remain deployable, because the
runtime just falls back and computes the same answers. This has produced both outcomes in practice:
in a nested-layout run where R2R genuinely never activated, and in a 181-assembly composite run where
it demonstrably did.

Use one of these instead:

```bash
DOTNET_ReadyToRunLogFile=$PWD/r2r.log <run command>
grep -c "initialized successfully" r2r.log   # >0 means R2R images were really loaded
grep -c "header not found"          r2r.log   # >0 is expected for assemblies you did not crossgen
```

A live debugger tracepoint on an R2R'd method is equally conclusive.

The third option is the most useful when bisecting a fix: **run an arm that is known to fail inside
the R2R load path** — for example, a build without the fix whose absence asserts in
`NativeImage::Open` — and confirm it does fail. That assert can only fire if the composite was
genuinely being loaded, so it proves activation for the sibling arms that share the layout. A failing
arm carries more information than a passing one here, which inverts the usual intuition.

## Traps

These are the failure modes that have repeatedly produced the false conclusion that CoreCLR R2R on
WASI is broken.

**1. `Ready to Run header not found` for every assembly means the probe never ran or found nothing.**
It does **not** mean R2R is unsupported on WASI. Work through the four requirements in
[How the WASI host finds R2R images](#how-the-wasi-host-finds-r2r-images): `APP_ASSEMBLIES=EXTERNAL`
set, a spliced host (a stock `corerun` has an empty composite buffer), the composite named
`composite-r2r.wasm`, and the per-assembly stubs under `comp/`.

**2. `DOTNET_ReadyToRun=0` and `=1` producing identical output does not mean R2R is inactive.**
When the composite is baked into `corerun*.wasm` rather than loaded externally, the environment
variable is a **no-op** — it only gates external R2R image loading, and R2R is unconditionally
active in that binary. Identical output is the *expected* result. Use `DOTNET_ReadyToRunLogFile` or
a debugger tracepoint instead. A prior investigation reached and later had to reverse exactly this
conclusion.

**3. A missing framework assembly manifests as an infinite loop, not an error.**
The run directory needs the full framework IL closure; `Console.WriteLine` transitively pulls in
`System.Threading` and more. With R2R off you get a clean `FileNotFoundException`; with R2R on the
same missing assembly hangs in the binder/EH path. **Always gate a suspected trap or hang with a
`DOTNET_ReadyToRun=0` run first** — a clean `FileNotFoundException` means a deployment gap, not a
codegen bug.

**4. Stale or mixed-vintage artifacts.**
Mixing an old CoreLib IL or JIT with a freshly built crossgen2 (or the reverse) has produced
phantom `NullReferenceException`s and phantom "config X is broken" results more than once. Before
trusting any negative result, check timestamps on `IL/System.Private.CoreLib.dll`, the `crossgen2`
binary, and the JIT, and confirm with one clean rebuild of the exact configuration. When you are
handed a prebuilt `.wasm` rig, verify its provenance (`stat`, `sha256sum`) before believing anything
it tells you.

**5. `./build.sh clr.jit` does not refresh the JIT that crossgen2 loads.**
`crossgen2`'s directory holds its own *copy* of the wasm-targeting JIT
(`libclrjit_universal_wasm_<arch>.dylib`). A `clr.jit` build updates the copy under `artifacts/obj`
but not the one next to `crossgen2`, so JIT fixes are silently ignored. Either copy it over
manually or use `./build.sh clr.aot`.

**6. A bare `./build.sh clr -os wasi` link failure is a build-configuration gap, not a platform gap.**
Undefined symbols such as `PAL_ProbeMemory`, `shm_open`, or `shm_unlink` from `debughelp.cpp` and
`doublemapping.cpp` come from missing WASI PAL stubs
(`src/coreclr/pal/src/include/pal/wasi/pal_wasi_missing.h`), not from a fundamental limitation.
Build `clr+host`, which is the recipe recorded as working end to end.

**7. The branch must track upstream R2R/async thunk fixes.**
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

### Getting diagnostics out of crossgen2

Several JIT diagnostic knobs are **inert by design** under crossgen2, in ways that look like
misconfiguration. Two pipes matter, and neither is `jitstdout`:

**Fatal JIT errors are silently discarded.** A `CodeGenerationFailedException` with *no* inner
exception ([`CorInfoImpl.cs`](../../../../src/coreclr/tools/Common/JitInterface/CorInfoImpl.cs), the
`result != CORJIT_OK` tail of `CompileMethod`) means the JIT signalled a fatal error that was not
BADCODE / IMPLLIMITATION / R2R_UNSUPPORTED / OUTOFMEM. That path runs `fatal()` →
`RaiseException(FATAL_JIT_EXCEPTION)` → `__JITfilter` → `jitInfo->reportFatalError()`, and
crossgen2's managed `reportFatalError` is an empty method — the `CorJitResult` reaches managed code
and is dropped. A bare exception with no reason text is the expected outcome, not a broken flag. To
see the reason, add a line to `reportFatalError`; it is managed-only, so no JIT rebuild is needed.

**JIT assert text does not go to `jitstdout`.** `assertAbort`
([`src/coreclr/jit/error.cpp`](../../../../src/coreclr/jit/error.cpp)) formats the
`Assertion failed '...' in '...' during '...'` message and routes it through
`compHnd->doAssert(...)` — a JIT-EE callback into managed crossgen2, which forwards to its `Logger`.
So `--codegenopt:JitStdOutFile=...` never receiving assert output tells you nothing; no assert would
ever write there. `JitFuncInfoLogFile` additionally requires `FUNC_INFO_LOGGING` compiled in.

**The managed crossgen2 configuration is independent of the JIT dylib's.** `DumpReproArguments`, and
other managed diagnostics, are `#if DEBUG` on the *managed* build. A Debug native JIT paired with a
Release managed crossgen2 has them compiled out. If a failure does not print repro instructions,
check that before concluding a managed-side knob is broken.

**The JIT is `dlopen`'d, not linked.** `JitConfigProvider` installs a `SetDllImportResolver` mapping
the `DllImport` name `clrjitilc` to `clrjit_<targetspec>` (e.g. `clrjit_universal_wasm_arm64`). Use
`--jitpath` to pin exactly which JIT loads. For a debugger, set breakpoints *after* the library
loads, and take the module name from `image list` rather than guessing the file name — a pending
breakpoint that never resolves usually means the name does not match what the debugger recorded.


Symptom: under R2R, `DateTime.ToString("yyyy-MM-ddTHH:mm:ss")` returned a constant
`0001-01-01T00:07:09` regardless of input, while `.Ticks`/`.Year`/`.Month` read correctly. The
`DateTime` was correct in `DateTimeFormat.Format`'s own frame and wrong by the time
`FormatCustomized` saw it, which looks exactly like a thunk marshaling or argument-homing bug.

It was neither. It was **JIT frame layout**, and the wrong value was uninitialized memory one slot
past the end of the caller's frame.

Get a serialized JIT dump for the offending method at compile time — this is a crossgen2 flag, not a
runtime one:

```bash
crossgen2 --parallelism:1 --codegenopt JitDump=Format ...
```

`--parallelism:1` is not optional: without it the dump is interleaved across compilation threads and
unreadable. Note also that `--codegenopt JitDump=` is a **crossgen2-time** flag — these dumps come
from compilation, not from the run.

The tell is in `lvaAssignFrameOffsets`' **Assign list**. The promoted field of the `DateTime`
parameter (`V153 ... "field V01._dateData"`, marked `addr-exposed` and `P-DEP` because `Format`
passes the parameter by `ref`) never appeared in that list, so it kept stack offset 0 — which, after
the virtual-to-actual frame delta, silently aliased it to `frameSize`. Codegen then loaded the
argument from off the end of the frame.

Root cause: `Compiler::lvaAssignFrameOffsetsToPromotedStructs()` in
[`src/coreclr/jit/lclvars.cpp`](../../../../src/coreclr/jit/lclvars.cpp) gates the
`SetStackOffset(parent + lvFldOffset)` fixup for *parameters* on `mustProcessParams`, which is
hardcoded `true` only for `UNIX_AMD64_ABI`, `TARGET_ARM`, and `TARGET_X86`. The comment above it
assumes promoted parameter field offsets are assigned by `lvaAssignVirtualFrameOffsetToArg()`, which
is not true on wasm — parameters arrive as wasm locals and are homed by the prolog. The only other
propagation site requires `lvIsRegArg` and is gated to ARM/LoongArch/RISC-V, so it cannot cover wasm
either. Adding `TARGET_WASM` to that gate fixes it — see
[dotnet/runtime#131401](https://github.com/dotnet/runtime/pull/131401), which also carries the full
evidence trail and a note on testing.

Two lessons worth carrying:

- On wasm, a value that is **correct in the caller and garbage one hop later** may be a frame-offset
  bug rather than a marshaling bug. Check frame layout before dissecting thunks.
- A local **missing from the Assign list** has offset 0, and the frame delta will alias it to
  `frameSize` rather than trapping. Absence is the signal.

Only the address-exposed promoted parameter is affected, so this presents as a single argument being
wrong while every other argument homes correctly — not as a positional shift.

**Writing a regression test for this needs care: the wrong codegen reproduces easily, but the wrong
*value* does not.**

The trigger is **dominance**, not merely address exposure. If the address exposure dominates the use,
codegen rereads the parent struct local and the bad field offset is never touched — a naive
`static long Test(Wrap w) { Touch(ref w); return Consume(w, 1, 2); }` produces every correct
diagnostic signal (`V01 arg0 addr-exposed`, a `P-DEP` field local, even the `-- V04 was 0, now 16`
line in the fix pass) yet emits byte-identical output before and after the fix. Put the exposure on a
*non-dominating* path and the main path stays bound to the field local while it is still
memory-homed, which is what `Format` does via `case 'U': PrepareFormatU(ref dateTime, ...)`:

```csharp
static long Test(Wrap w, int mode)
{
    if (mode == 42) { Rare(ref w); }    // address-exposes on a rare path only
    long extra = w.Value > 0 ? 1 : 0;   // field use on the main path
    return Consume(w, (int)extra);
}
```

That diverges as expected — `i64.load 0 8` fixed versus `i64.load 0 16` unfixed.

It still will not *fail* observably, though: the wrong slot is the caller's frame base, which usually
holds the caller's own copy of the same struct, so both arms print the right answer. Poisoning an
intervening frame with a `stackalloc` does not help. The value only turns to garbage when the caller
is structurally unrelated — a thunk, in `Format`'s case.

So **assert on the generated code, not on program output**: the emitted load offset, or the fix
pass's `V## was 0, now <frameSize>` line. SuperPMI asm diffs surface this immediately as a changed
load offset and are the natural home in this repo. For an end-to-end test, use a real scenario that
does fail deterministically — `DateTime.ToString` under wasm R2R, as covered by `Utf8JsonWriter`'s
DateTime tests. And never conclude "not reproducible, must already be fixed" from a minimal test
whose output happens to be correct.


## Known-broken

- ~~**`Int128`/`UInt128` comparison or equality is miscompiled under wasm R2R.**~~ **Fixed** by
  [dotnet/runtime#131492](https://github.com/dotnet/runtime/pull/131492) (merged 2026-08-12). Kept
  here because the root cause generalises: the `S<N>` signature encoding resolves through a
  **size-keyed, first-wins struct cache**, so `Int128` and `Guid` both spell `S16` and shared a thunk
  whose frame layout only fitted whichever type the cache saw first. The fix passes `Int128`,
  `Decimal128` and wide vectors *by value* — matching clang for wasm32 — instead of as a single `i32`
  pointer. **If you see two unrelated types of the same size disagreeing about layout, suspect that
  cache.** The original symptom, `RoundtripValues<Int128>(-1)` failing with
  `Expected: -1  Actual: -1` — values rendering identically but comparing unequal — accounted for 25
  of the 39 residual `System.Text.Json` failures.
- Browser only: `Enum.ToObject`'s virtual dispatch to `GetTypeCodeImpl()` traps on a `call_indirect`
  return-type mismatch (expected `void`, actual `i32`). WASI dispatches the identical call cleanly on
  the same codegen; the suspected cause is emscripten-side R2R/interpreter thunk resolution.
- `src/tasks/WasmAppBuilder/coreclr/SignatureMapper.cs` — `TokenToSlotCount` returns 1 for any token
  not starting with `S`/`A`, so a `V` (v128) token appears to count as one 8-byte slot rather than
  two. Flagged as latent before #131492 reworked this encoding (which added the `l2`/`V2` alignment-
  factor forms); **re-verify against the current encoding before acting on it**, since the slot
  semantics may have changed underneath the original observation.

## Validating a fix at suite level

Always compare against the **interpreter (R2R off) baseline** for the same suite, not against zero,
and **characterise the residual failures** instead of declaring victory once the failures you were
chasing hit zero. These are really one technique: the baseline is what turns a residual *count* into
a *decomposition*. Without it you can report "39 failures" but you cannot say which of them you own —
and "we went from 216 to 39" invites the obvious question "so what are the 39?", which you then can't
answer.

With it, the residue splits quickly — grouping by type or by assertion shape usually separates
"distinct codegen bug", "pre-existing, fails under the interpreter too", and "legitimate test
expectation" in a few minutes. Here that was 2 pre-existing, 25 one new bug, 12 assorted. The
leftovers are also the cheapest source of the next bug: the `Int128` lead above came entirely free
from triaging the 39 that remained after a fix that took DateTime failures from 187 to 0.

A worked reference point — `System.Text.Json.Tests` on browser R2R, before and after
[dotnet/runtime#131401](https://github.com/dotnet/runtime/pull/131401):

| Run | Result |
| --- | --- |
| R2R on, before the fix | aborted partway, no result XML — 216 failures, run never completed |
| R2R on, after the fix | completed — 59,365 run / 59,284 passed / 39 failed / 42 skipped, 0 aborts |
| Interpreter baseline | 59,312 passed / 11 failed |

Report the run *outcome* before the failure count: a run that aborts without producing result XML is
easy to mistake for a run that merely failed, and the two are categorically different.

