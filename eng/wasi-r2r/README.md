# WASI composite-R2R splice tooling

Tooling for building, splicing, and running a **composite ReadyToRun image on CoreCLR/WASI**.
It exists because WASI has no productised R2R path: the working flow is hand-driven — run
`crossgen2` directly, splice the result into `corerun`, and run it under `wasmtime`.

Scope note, since this is easy to over-read: **the splice is a WASI requirement, not a composite
requirement.** `WasiStaticR2RProbe` serves `composite-r2r.wasm` only from a baked-in buffer that the
splice populates, so on WASI there is no way to hand the runtime a composite from disk. Browser has
no such constraint — `crossgen2 --composite --targetos:browser` plus a flat directory driven by
`corerun.js` works without any of this tooling. Browser also has a productised **non-composite** path
since [#132339](https://github.com/dotnet/runtime/pull/132339) (`-p:PublishReadyToRun=true`); that
path declines composite, but only as an SDK opt-out.

**Read [`docs/workflow/building/coreclr/wasi-r2r.md`](../../docs/workflow/building/coreclr/wasi-r2r.md) first.**
That is the full playbook: build commands, crossgen2 invocations, run commands, and — most
importantly — the traps that repeatedly lead people to falsely conclude that CoreCLR R2R on WASI
does not work. This README only covers the tools in this directory.

## Pieces

| Path | Purpose |
| --- | --- |
| `pipeline-sym.sh` | The splice pipeline: unbundle → surgery → `wasm-merge` → activate → module-swap. |
| `surgery/` | Relocates the `corerun` core module's table/image base for the composite. |
| `activate/` | Converts the merged webcil payload and R2R element segments from passive to active. |
| `comp.rsp.template` | `crossgen2` composite response file; replace `@ROOT@` with your worktree root. |
| `Nesm.props` | Locates `Nesm.dll` for the two tools. |

## Prerequisites

- `wasm-tools`, `wasm-merge` (Binaryen), and `wasm-objdump` (WABT) on `PATH`.
  `pipeline-sym.sh` prepends `~/.cargo/bin` and `/opt/homebrew/bin` and fails fast if any are missing.
- `wasmtime` on `PATH` for running the result.
- `Nesm.dll` — a wasm binary reader/writer from <https://github.com/Blazor-Playground/nesm>, which
  lives outside this repo. `surgery/` and `activate/` reference it, so point at a built copy:

  ```bash
  export NESM_ASSEMBLY=/path/to/nesm/src/Nesm/bin/Release/net10.0/Nesm.dll
  ```

  Without it the tool builds fail with an explicit error rather than a missing-reference cascade.

## Is the splice still needed?

**Yes, for WASI.** [#131016](https://github.com/dotnet/runtime/pull/131016) added VM-side loading of a
flat webcil composite, and that code is present — `NativeImage::Open` has a `TARGET_WASM` branch that
takes the R2R header from the decoder instead of the `RTR_HEADER` export. But it does not make direct
deployment work here, because the **WASI host probe never serves the composite from disk**:
`WasiStaticR2RProbe` ([`wasi_r2r_probe.hpp`](../../src/coreclr/hosts/corerun/wasi_r2r_probe.hpp))
special-cases `composite-r2r.wasm` and returns the baked-in `g_wasi_r2r_image` buffer, which only the
splice populates. Per-assembly stubs *are* read from `comp/<name>.wasm` on disk; the composite is not.

Measured on a stock (unspliced) `corerun` with the composite deployed alongside — both in the run root
and colocated in `comp/` — this is what happens:

1. `g_wasi_r2r_image` is empty, so `WasiWebcilPayloadSize` returns `<= 0` and the probe returns `false`.
2. `OpenR2RFromPE` falls through to `PEImageLayout::LoadNative`, which reads the raw file.
3. The file begins `\0asm` — it is webcil *wrapped in wasm* — so `WebcilDecoder::DetectWebcilFormat`,
   which tests for the ASCII bytes `WbIL`, returns false.
4. `InitDecoders` therefore selects `FORMAT_PE` and runs `PEDecoder` over a wasm file.

The result is **not** a graceful fallback. It is an out-of-bounds trap during EE startup:

```
0: corerun!PEDecoder::FindReadyToRunHeader() const
1: corerun!NativeImage::Open(...)
2: corerun!AssemblyBinder::LoadNativeImage(...)
3: corerun!AcquireCompositeImage(...)
4: corerun!ReadyToRunInfo::Initialize(...)
...
memory fault at wasm address 0x6541cc8b in linear memory of size 0x8000000
wasm trap: out of bounds memory access
```

That backtrace is the signature of this deployment gap. It looks like a broken composite and reads
like "R2R does not work on wasm"; it is neither. Gate it with `DOTNET_ReadyToRun=0` — if the app then
runs clean, the composite was simply never delivered to the runtime, and you need the splice.


## Usage

`pipeline-sym.sh` derives `TOOLS` from its own location and `ROOT` from the repo root above it, so
from a worktree with a matching build already in `artifacts/` it is just:

```bash
export NESM_ASSEMBLY=/path/to/Nesm.dll
eng/wasi-r2r/pipeline-sym.sh
```

Every input is overridable by environment variable — see the header comment in the script.
It prints `VALID` and the output path (`r2rtest/ccsym/corerun-composite-sym.wasm`) on success.
