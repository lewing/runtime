---
applyTo: "src/coreclr/vm/wasm/**, src/coreclr/tools/aot/**, src/coreclr/tools/Common/JitInterface/Wasm*, src/mono/wasi/**, src/mono/browser/**, src/tasks/WasmAppBuilder/**, eng/wasi-r2r/**"
---

# CoreCLR ReadyToRun on wasm (WASI and browser)

**CoreCLR R2R on WASI works.** Do not conclude otherwise from a failing build or a failing run
without first working through the traps in
[`docs/workflow/building/coreclr/wasi-r2r.md`](../../docs/workflow/building/coreclr/wasi-r2r.md).
That document is the playbook: build commands, `crossgen2` invocations, deployment layout, run
commands, and the specific failure modes that have repeatedly been misdiagnosed as "the platform
doesn't support this". Read it before starting wasm R2R work, and update it when you learn something
new.

Supporting tooling is vendored in [`eng/wasi-r2r/`](../../eng/wasi-r2r/README.md).

The essentials, so they are never lost again:

- Build with `./build.sh clr+host -os wasi -c Release` followed by
  `./build.sh libs+packs -os wasi -c Release`. The Mono `sfxproj` packs failure is expected and
  harmless on a CoreCLR-only WASI build.
- There is **no** `PublishReadyToRun` or WasmAppBuilder path for wasm R2R. Invoke `crossgen2`
  directly with `--composite -O --targetarch:wasm --targetos:wasi`.
- Take `System.Private.CoreLib.dll` from `artifacts/bin/coreclr/wasi.wasm.<config>/IL/`. The copy in
  the wasi-wasm runtime pack's `native/` directory is Mono's and silently breaks everything.
- The run directory needs the **full** framework IL closure. A missing assembly manifests as an
  infinite loop when R2R is on, and as a clean `FileNotFoundException` when it is off — always run
  with `DOTNET_ReadyToRun=0` first to tell a deployment gap apart from a codegen bug.
- On a baked composite, `DOTNET_ReadyToRun=0` and `=1` producing identical output is **expected**
  and is not evidence that R2R is inactive. Prove activity with `DOTNET_ReadyToRunLogFile` instead.
- Prefer a clean rebuild over an incremental one before trusting any negative result. Mixed-vintage
  CoreLib, JIT, and `crossgen2` artifacts have produced several phantom bugs.

wasm R2R codegen keys on the architecture, not the target OS, so browser and WASI share the code
paths and each is a useful control when isolating a bug in the other.
