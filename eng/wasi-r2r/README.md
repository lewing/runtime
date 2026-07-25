# WASI composite-R2R splice tooling

Tooling for building, splicing, and running a **composite ReadyToRun image on CoreCLR/WASI**.
It exists because there is no `PublishReadyToRun` / WasmAppBuilder path for wasm R2R today —
the working flow is entirely hand-driven: run `crossgen2` directly, then deploy (or splice) the
result and run it under `wasmtime`.

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

Maybe not. [#131016](https://github.com/dotnet/runtime/pull/131016) added VM-side loading of a flat
webcil composite image, so deploying the `crossgen2` composite output directly — with no splice step
— may already work. **Try the direct deployment first** and fall back to `pipeline-sym.sh` only if
the VM refuses to load the image. The splice path is kept here because it is the recipe that is
recorded end-to-end verified.

## Usage

`pipeline-sym.sh` derives `TOOLS` from its own location and `ROOT` from the repo root above it, so
from a worktree with a matching build already in `artifacts/` it is just:

```bash
export NESM_ASSEMBLY=/path/to/Nesm.dll
eng/wasi-r2r/pipeline-sym.sh
```

Every input is overridable by environment variable — see the header comment in the script.
It prints `VALID` and the output path (`r2rtest/ccsym/corerun-composite-sym.wasm`) on success.
