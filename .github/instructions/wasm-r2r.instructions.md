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
- For **WASI**, and for any **composite** image on either host, there is no `PublishReadyToRun` path:
  invoke `crossgen2` directly with `--composite -O --targetarch:wasm --targetos:wasi`, then splice
  with [`eng/wasi-r2r/pipeline-sym.sh`](../../eng/wasi-r2r/README.md). The WASI host probe serves the
  composite from a baked-in buffer that only the splice populates, so deploying a composite next to a
  stock `corerun` does not work — it traps out of bounds inside `ReadyToRunInfo::Initialize`.
- For **browser**, [#132339](https://github.com/dotnet/runtime/pull/132339) (merged 2026-08-25) added
  a supported SDK path for **non-composite** images: build with `-p:PublishReadyToRun=true`. That path
  refuses composite (`PublishReadyToRunComposite` raises an error), but the check is gated on
  `PublishReadyToRun`, so it is an **SDK opt-out, not a platform limit** — hand-driven
  `crossgen2 --composite --targetos:browser` works, `corerun.js` loads it with R2R active, and that is
  what the `src/tests` `CompositeBuildMode` path has long produced. The runtime pack's prebuilt
  framework R2R images are governed by a separate build-time knob, `WasmEnableFrameworkR2R` (defaults
  `true`, in the CoreCLR `sfxproj`) — do not confuse the two.
- Take `System.Private.CoreLib.dll` from `artifacts/bin/coreclr/wasi.wasm.<config>/IL/`. The copy in
  the wasi-wasm runtime pack's `native/` directory is Mono's and silently breaks everything.
- The run directory needs the **full** framework IL closure. A missing assembly manifests as an
  infinite loop when R2R is on, and as a clean `FileNotFoundException` when it is off — always run
  with `DOTNET_ReadyToRun=0` first to tell a deployment gap apart from a codegen bug.
- On a baked composite, `DOTNET_ReadyToRun=0` and `=1` producing identical output is **expected**
  and is not evidence that R2R is inactive. Prove activity with `DOTNET_ReadyToRunLogFile` instead,
  globbing `r2r.log.*` — the runtime appends `.<pid>`. The strongest proof is a breakpoint on the
  R2R function itself (find it with nesm's `wasm_debug_search_functions`, which reports
  `r2r_managed_name`): if it is hit, R2R native code ran, and total fallback would mean no hit.
- **`Ready to Run initialized successfully` is bounded evidence.** It means a stub was found and its
  owner composite loaded — not that the assembly's code is in that composite, and not that any R2R
  code ran. Nothing validates membership, so a stub left over from an earlier composite logs success
  against a composite that does not contain it, then dereferences a NULL core header that wasm reads
  as stack rather than trapping. Measured, with byte-identical output either way; details and the
  A/B/C/D control table are in the playbook. Rebuild stubs and composite together.
- `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=true` appears in most run commands and **silently disables
  the cold path in [#130634](https://github.com/dotnet/runtime/issues/130634)**, which reaches
  `Monitor.Exit` via `ResourceManager` → `CultureInfo.GetCultureInfo` → `CultureData.GetCultureData`.
  A probe for that bug must exercise the mechanism directly (a `lock` as the first managed statement)
  rather than through globalization.
- Prefer a clean rebuild over an incremental one before trusting any negative result. Mixed-vintage
  CoreLib, JIT, and `crossgen2` artifacts have produced several phantom bugs. Note that
  `./build.sh clr.crossarchtools` does **not** refresh the managed `crossgen2` bundle; use
  `./build.sh -s clr.aot`.

wasm R2R codegen keys on the architecture, not the target OS, so browser and WASI share the code
paths and each is a useful control when isolating a bug in the other.
