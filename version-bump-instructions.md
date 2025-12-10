# .NET Runtime Major Version Bump Instructions (11 → 12)

## Overview
This document provides step-by-step instructions for bumping the major version of dotnet/runtime from .NET 11 to .NET 12. These instructions are based on analysis of commit `6936d807ab02592e582c999fcfa39bff39c632b6` which updated from .NET 10 to .NET 11.

**Reference PRs:**
- .NET 11 bump: https://github.com/dotnet/runtime/pull/121853
- .NET 10 bump: https://github.com/dotnet/runtime/pull/106599

---

## General Strategy

The most effective approach to finding all locations that need updating is to **use the current framework version as a sentinel value**. Search for references to `net11` (or `net11.0`, `11.0`, etc.) throughout the codebase to identify files that require changes.

**Key Search Patterns:**
- `git grep -i "net11"` - Find TFM references
- `git grep "11\.0"` - Find version number references
- `git grep "TargetsNet11"` - Find workload target conditions
- `git grep "110100"` - Find SDK band version references

**Important Notes:**
- Many files intentionally include comments with version markers (e.g., `/* net11 */` or `<!-- net11 -->`) specifically to make them easy to find during version bumps
- Not all references to "11" should be changed - some are intentionally referring to the previous version for compatibility or baseline testing
- Focus on files in these categories:
  - Build configuration (`.props`, `.targets`, `.csproj`)
  - Test infrastructure (test projects, test base classes)
  - Templates (`.template.config/template.json`)
  - Workload manifests
  - Documentation

**Validation Approach:**
After making changes, re-run the search patterns to ensure:
1. No unintended `net11` references remain in active configuration
2. Appropriate `net11` references remain for baseline/compatibility purposes
3. New `net12` references are in place

---

## Change Categories and Instructions

### 1. Core Version Properties (CRITICAL - Do First)

**File:** `eng/Versions.props`

**Changes:**
- Update `<MajorVersion>` from `11` to `12`
- Update `<ProductVersion>` from `11.0.0` to `12.0.0`
- Update `<PackageVersionNet11>` → create new entry with .NET 11's final version
- Update previous version calculations:
  - `<PackageVersionNet10>` should use .NET 10's final version (was 10.0.0)
  - `<PackageVersionNet9>` calculation may need adjustment
- Reset pre-release info:
  - `<PreReleaseVersionLabel>` to `alpha` (or appropriate initial label)
  - `<PreReleaseVersionIteration>` to `1`
  - `<StabilizePackageVersion>` to `false`
  - Clear `<DotNetFinalVersionKind>` or set appropriately
- Update workload manifest references:
  - Replace all `110100` references (net11 band) with `120100` (net12 band)
  - Example: `MicrosoftNETWorkloadEmscriptenCurrentManifest110100TransportVersion` → `MicrosoftNETWorkloadEmscriptenCurrentManifest120100TransportVersion`

**Intent:** Establish the primary version numbers used throughout the build system.

---

### 2. Target Framework Monikers (TFMs)

**File:** `Directory.Build.props`

**Changes:**
- `<NetCoreAppCurrentVersion>`: `11.0` → `12.0`
- `<NetCoreAppPrevious>`: Clear or set to `net11.0`
- `<NetCoreAppMinimum>`: `net10.0` → `net11.0`
- `<ApiCompatNetCoreAppBaselineVersion>`: `10.0.0` → `11.0.0`
- `<ApiCompatNetCoreAppBaselineTFM>`: `net10.0` → `net11.0`

**Intent:** Update the primary target framework from net11.0 to net12.0 and shift the support matrix.

---

### 3. Target Framework References

**File:** `Directory.Build.targets`

**Changes:**
- Update all hardcoded `net11.0` references to `net12.0`
- Update conditional imports for workloads (search for `TargetsNet11` → `TargetsNet12`)
- Update SDK import paths containing `net11` → `net12`
- Example patterns:
  - `Microsoft.NET.Runtime.MonoTargets.Sdk.net11` → `Microsoft.NET.Runtime.MonoTargets.Sdk.net12`
  - `Microsoft.NETCore.App.Runtime.AOT.Cross.net11.browser-wasm` → `Microsoft.NETCore.App.Runtime.AOT.Cross.net12.browser-wasm`

**Intent:** Update build target imports and workload-specific configurations.

---

### 4. Create New Workload Manifest

**New Directory:** `src/mono/nuget/Microsoft.NET.Workload.Mono.Toolchain.net12.Manifest/`

**Actions:**

1. **Create the base manifest structure:**
   - Create new directory: `Microsoft.NET.Workload.Mono.Toolchain.net12.Manifest/`
   - Copy the `.pkgproj` file from `Current.Manifest` and update package name to include `net12`
   - Copy the `localize/` directory structure from `Current.Manifest`

2. **Create `WorkloadManifest.json.in`:**
   - Use `Current.Manifest/WorkloadManifest.json.in` as the template
   - Replace `${NetVersion}` or current version placeholders with `net12`
   - Update workload IDs: `wasm-tools` → `wasm-tools-net12`, `wasm-experimental` → `wasm-experimental-net12`
   - Update descriptions to reference ".NET 12.0"
   - Ensure version tokens are appropriate for a frozen manifest (typically using `${PackageVersionNet12}`)

3. **Create `WorkloadManifest.targets.in`:**
   - Copy the common logic section from `Current.Manifest/WorkloadManifest.targets.in` (everything before the `<!-- start of TFM specific logic -->` comment)
   - Extract the TFM-specific section (after the `<!-- start of TFM specific logic -->` comment) from `Current.Manifest`
   - Transform all `TargetsCurrent` conditions to `TargetsNet12`
   - Replace `${NetVersion}` placeholders with `net12`
   - Update SDK package references to use the specific `net12` version
   - Remove or adjust any forward-compatibility workarounds that referenced the previous version
   - Ensure all `ImportGroup` and `PropertyGroup` nodes have explicit `TargetsNet12` conditions

4. **Update localized resources in `localize/*.json`:**
   - Update all localized string files (cs, de, en, es, fr, it, ja, ko, pl, pt-BR, ru, tr, zh-Hans, zh-Hant)
   - Change workload identifiers: `wasm-tools-net11` → `wasm-tools-net12`
   - Change workload identifiers: `wasm-experimental-net11` → `wasm-experimental-net12`
   - Update version references: ".NET 11.0" → ".NET 12.0" (in all respective languages)

5. **Register the new manifest:**
   - Update `src/mono/nuget/manifest-packages.proj`
   - Add `<ProjectReference Include="Microsoft.NET.Workload.Mono.Toolchain.net12.Manifest\Microsoft.NET.Workload.Mono.Toolchain.net12.Manifest.pkgproj" />`

**Intent:** Create a version-specific frozen workload manifest for .NET 12 by deriving from the Current manifest template. The Current manifest continues to represent the active development version, while the new net12 manifest captures the specific configuration for .NET 12 tooling, enabling Mono/WASM/WASI development on that target framework.

---

### 5. Test Infrastructure Updates

**Files:**
- `src/mono/wasm/Wasm.Build.Tests/BuildTestBase.cs`
- `src/mono/wasi/Wasi.Build.Tests/BuildTestBase.cs`
- `src/mono/wasm/Wasm.Build.Tests/NonWasmTemplateBuildTests.cs`

**Changes:**
- Update `TargetMajorVersion` constant: `11` → `12`
- Add new runtime pack version entries:
  - In `.csproj` files, add `<_RuntimePackVersions Include="..." EnvVarName="RUNTIME_PACK_VER12" />`
  - Keep previous versions (VER11, VER10, VER9, etc.) for compatibility testing
- Update `DefaultTargetFramework` calculations based on new constant

**Intent:** Ensure test infrastructure targets the new framework and can test against it.

---

### 6. Project Template Updates

**Files:** All template `.template.config/template.json` files:
- `src/mono/wasm/templates/templates/browser/.template.config/template.json`
- `src/mono/wasm/templates/templates/console/.template.config/template.json`
- `src/mono/wasm/templates/templates/wasi-console/.template.config/template.json`

**Changes in each:**
- `"identity"`: `"WebAssembly.Browser.11.0"` → `"WebAssembly.Browser.12.0"`
- In `symbols/Framework/choices`:
  - `"choice": "net11.0"` → `"net12.0"`
  - `"description": "Target net11.0"` → `"Target net12.0"`
  - `"displayName": ".NET 11.0"` → `".NET 12.0"`
- `"defaultValue"`: `"net11.0"` → `"net12.0"`
- `"replaces"`: Ensure `"netX.0"` is the replacement token

**File:** `src/mono/wasm/templates/Microsoft.NET.Runtime.WebAssembly.Templates.csproj`
- `<PackageId>`: `Microsoft.NET.Runtime.WebAssembly.Templates.net11` → `Microsoft.NET.Runtime.WebAssembly.Templates.net12`

**Intent:** Update `dotnet new` templates to create .NET 12 projects by default.

---

### 7. Test Asset Project Files

**Pattern:** Update all `.csproj` files in `src/mono/wasm/testassets/` and related test directories

**Files to update (search for `<TargetFramework>net11.0</TargetFramework>`):**
- BlazorBasicTestApp/App/BlazorBasicTestApp.csproj
- BlazorBasicTestApp/RazorClassLibrary/RazorClassLibrary.csproj
- LibraryMode/LibraryMode.csproj
- SatelliteAssemblyFromProjectRef/LibraryWithResources/LibraryWithResources.csproj
- WasmBasicTestApp/App/WasmBasicTestApp.csproj
- WasmBasicTestApp/Json/Json.csproj
- WasmBasicTestApp/LazyLibrary/LazyLibrary.csproj
- WasmBasicTestApp/Library/Library.csproj
- WasmBasicTestApp/ResourceLibrary/ResourceLibrary.csproj
- WasmOnAspNetCore/AspNetCoreServer/AspNetCoreServer.csproj
- WasmOnAspNetCore/BlazorClient/BlazorClient.csproj
- WasmOnAspNetCore/Shared/Shared.csproj
- WasmOnAspNetCore/WasmBrowserClient/WasmBrowserClient.csproj

**Changes:** `<TargetFramework>net11.0</TargetFramework>` → `<TargetFramework>net12.0</TargetFramework>`

**Intent:** Update test assets to target the new framework version.

---

### 8. Build Configuration Files

**File:** `src/mono/wasm/build/WasmApp.LocalBuild.props`
- `<_NetCoreAppToolCurrent>net11.0</_NetCoreAppToolCurrent>` → `<_NetCoreAppToolCurrent>net12.0</_NetCoreAppToolCurrent>`

**File:** `src/mono/msbuild/apple/build/AppleBuild.LocalBuild.props`
- Similar updates for `_NetCoreAppToolCurrent` if present

**File:** `src/native/package.json`
- `"rollup:release"`: Update `ProductVersion:11.0.0-dev` → `ProductVersion:12.0.0-dev`
- `"rollup:debug"`: Update `ProductVersion:11.0.0-dev` → `ProductVersion:12.0.0-dev`

**Intent:** Ensure local build configurations use the new version.

---

### 9. Documentation Updates

**Files to update (search for version references):**
- `docs/coding-guidelines/adding-api-guidelines.md`
- `docs/coding-guidelines/project-guidelines.md`
- `docs/project/dogfooding.md`
- `docs/workflow/building/coreclr/nativeaot.md`
- `docs/workflow/building/coreclr/wasm.md`
- `docs/workflow/building/libraries/README.md`
- `docs/workflow/building/libraries/cross-building.md`
- `docs/workflow/ci/triaging-failures.md`
- `docs/workflow/debugging/coreclr/debugging-runtime.md`
- `docs/workflow/testing/host/testing.md`
- `docs/workflow/testing/libraries/testing.md`
- `docs/workflow/testing/using-dev-shipping-packages.md`
- `docs/workflow/testing/using-your-build-with-installed-sdk.md`

**Changes:**
- Replace version numbers in examples and instructions
- Update SDK version references
- Update package version references in commands

**Intent:** Keep documentation current with new version.

---

### 10. Pipeline and Engineering Files

**Files:**
- `eng/build.ps1`, `eng/build.sh` - Check for hardcoded versions
- `eng/intellisense.targets` - Update TFM references if present
- `eng/packaging.targets` - Update version-specific packaging logic
- `eng/pipelines/coreclr/templates/crossgen2-comparison-build-job.yml` - Update version refs
- `eng/pruning.targets` - Check for TFM-specific rules
- `eng/targetingpacks.targets` - Update targeting pack references
- `eng/testing/linker/project.csproj.template` - Update TFM
- `eng/testing/tests.wasm.targets` - Update WASM-specific versions
- `eng/testing/workloads-browser.targets` - Update workload references
- `eng/testing/workloads-wasi.targets` - Update workload references
- `eng/Signing.props` - Check for version-specific signing configs
- `eng/testing/scenarios/BuildWasmAppsJobsList.txt` - Update test job references

**Intent:** Update build pipeline and engineering infrastructure to support new version.

---

### 11. Compatibility Suppressions

**Files to update:**
- `src/coreclr/System.Private.CoreLib/CompatibilitySuppressions.xml`
- `src/libraries/System.Collections.Immutable/src/CompatibilitySuppressions.xml`
- `src/libraries/System.Diagnostics.EventLog/src/CompatibilitySuppressions.xml`
- `src/libraries/System.IO.Pipes/src/CompatibilitySuppressions.xml`
- `src/libraries/System.Numerics.Tensors/src/CompatibilitySuppressions.xml`
- `src/libraries/System.Private.CoreLib/src/CompatibilitySuppressions.xml`
- `src/libraries/System.Runtime.InteropServices.JavaScript/src/CompatibilitySuppressions.WasmThreads.xml`
- `src/libraries/System.Runtime.InteropServices.JavaScript/src/CompatibilitySuppressions.xml`
- `src/libraries/System.Security.Permissions/src/CompatibilitySuppressions.xml`
- `src/tools/illink/src/linker/CompatibilitySuppressions.xml`

**Changes:**
- Remove suppressions specific to older versions (e.g., entries for `net10.0`)
- May need to add new suppressions as APIs evolve
- Update `<Left>ref/net11.0/` → `<Left>ref/net12.0/` patterns
- Update `<Right>lib/net11.0/` → `<Right>lib/net12.0/` patterns

**Intent:** Maintain API compatibility tracking across versions.

---

### 12. API Compatibility Baselines

**Files:**
- `src/libraries/apicompat/ApiCompatBaseline.NetCoreAppLatestStable.xml`
- `src/libraries/apicompat/ApiCompatBaseline.netstandard2.0.xml`
- `src/libraries/apicompat/ApiCompatBaseline.netstandard2.1.xml`

**Changes:**
- Review and update baseline suppressions
- May need to regenerate baselines after version bump

**Intent:** Establish baseline for API compatibility checks in new version.

---

### 13. Testing Infrastructure

**Files:**
- `src/libraries/Common/tests/StaticTestGenerator/README.md`
- `src/libraries/Common/tests/System/Net/Prerequisites/LocalEchoServer.helix.targets`
- `src/libraries/Common/tests/System/Net/Prerequisites/LocalEchoServer.props`
- `src/libraries/Common/tests/System/Net/Prerequisites/NetCoreServer/NetCoreServer.csproj`
- `src/libraries/Common/tests/System/Net/Prerequisites/RemoteLoopServer/RemoteLoopServer.csproj`
- `src/libraries/Fuzzing/DotnetFuzzing/run.bat`
- `src/libraries/System.Numerics.Tensors/tests/Net8Tests/System.Numerics.Tensors.Net8.Tests.csproj`
- `src/libraries/System.Resources.Extensions/tests/System.Resources.Extensions.Tests.csproj`

**Changes:**
- Update TFM references in test projects
- Update version-specific test configurations
- Review and update version-specific test filtering

**Intent:** Ensure test infrastructure is compatible with new version.

---

### 14. Library-Specific Updates

**File:** `src/libraries/System.Collections.Immutable/ref/System.Collections.Immutable.netcoreapp.cs`
**File:** `src/libraries/System.Numerics.Tensors/ref/System.Numerics.Tensors.netcore.cs`

**Changes:**
- Add new conditional compilation symbols for net12.0 if needed
- Update API availability attributes

**Intent:** Manage version-specific API surface.

---

### 15. Native Managed Code

**File:** `src/native/managed/Directory.Build.props`
- May need to add/update `UseLocalTargetingRuntimePack` configuration

**File:** `src/native/managed/cdac/Directory.Build.props`
- Ensure output path configurations are version-agnostic or updated

**Intent:** Ensure native interop projects build correctly against new TFM.

---

### 16. Tooling Updates

**File:** `src/mono/wasm/symbolicator/WasmSymbolicator.csproj`
- Update `<TargetFramework>` from `$(NetCoreAppMinimum)` (which will now be net11.0)
- This project might need to stay on an earlier TFM for compatibility with older xharness

**File:** `src/coreclr/tools/aot/ILCompiler.Reflection.ReadyToRun/ILCompiler.Reflection.ReadyToRun.csproj`
- Check and update TFM if needed

**File:** `src/tools/illink/test/ILLink.RoslynAnalyzer.Tests/TestCaseCompilation.cs`
- May need to add new compiler warning suppressions (e.g., CS1701, CS1702 for version mismatches)

**Intent:** Update tooling to work with new framework version.

---

### 17. Workload Testing Configuration

**Files:**
- `eng/testing/workloads-browser.targets`
- `eng/testing/workloads-wasi.targets`
- `src/mono/nuget/Microsoft.NET.Runtime.WebAssembly.Sdk/Sdk/AutoImport.props`
- `src/mono/nuget/Microsoft.NET.Runtime.WebAssembly.Wasi.Sdk/Sdk/AutoImport.props`
- `src/mono/nuget/Microsoft.NET.Runtime.WorkloadTesting.Internal/Sdk/WorkloadTesting.Core.targets`
- `src/mono/nuget/Microsoft.NET.Workload.Mono.Toolchain.Current.Manifest/WorkloadManifest.targets.in`

**Changes:**
- Update `TargetsNet11` → `TargetsNet12` conditions
- Update `_RuntimePackInWorkloadVersion11` → `_RuntimePackInWorkloadVersion12`
- Add imports for net12 workload SDKs
- Update runtime identifier and targeting pack configurations

**Intent:** Enable workload testing for new version.

---

### 18. Test-Specific Adjustments

**File:** `src/mono/wasm/Wasm.Build.Tests/Blazor/BlazorWasmTestBase.cs`
- Add `DefaultTargetFrameworkForBlazorTemplate` constant if template versions differ
- Update constructor to handle version selection

**File:** `src/mono/wasm/Wasm.Build.Tests/Templates/WasmTemplateTestsBase.cs`
- Update template framework selection logic
- Handle cases where Blazor templates might target a different version than other templates

**File:** `src/mono/wasm/Wasm.Build.Tests/Blazor/AppsettingsTests.cs`
- May need to add `[ActiveIssue]` attributes for tests that need updating

**Intent:** Adjust test code to properly target and test new version.

---

### 19. Installer Test Assets

**File:** `src/installer/tests/Assets/Projects/Directory.Build.targets`
- Update TFM references
- Update runtime pack version references

**Intent:** Ensure installer tests work with new version.

---

### 20. CoreCLR Scripts

**Files:**
- `src/coreclr/scripts/superpmi_aspnet2.py`
- `src/coreclr/scripts/superpmi_benchmarks.py`

**Changes:**
- Update version numbers in test configurations
- Update framework version references in benchmarking scripts

**Intent:** Keep performance and correctness testing scripts current.

---

### 21. Documentation Files (NativeAOT/Mono)

**Files:**
- `src/coreclr/nativeaot/docs/android-bionic.md`
- `src/coreclr/nativeaot/docs/compiling.md`

**Changes:**
- Update version references in examples
- Update SDK version references

**Intent:** Keep specialized documentation up to date.

---

## Systematic Search and Replace Patterns

Use these patterns to catch remaining references:

1. **Version strings:**
   - `net11.0` → `net12.0`
   - `11.0` → `12.0` (in version contexts)
   - `.NET 11` → `.NET 12`

2. **Constants and variables:**
   - `Net11` → `Net12`
   - `net11` → `net12`
   - `TargetsNet11` → `TargetsNet12`
   - `Version11` → `Version12`

3. **Package and manifest identifiers:**
   - `110100` → `120100` (SDK band versions)
   - `Manifest-11` → `Manifest-12`

4. **File and directory names:**
   - `net11.Manifest` → `net12.Manifest`
   - Any directories containing `net11` → `net12`

---

## Verification Checklist

After making changes, verify:

- [ ] All projects build successfully
- [ ] `eng/Versions.props` has correct version numbers
- [ ] `Directory.Build.props` has correct TFMs
- [ ] New workload manifest directory created and referenced
- [ ] All template `.json` files updated
- [ ] All test `.csproj` files updated
- [ ] Documentation updated
- [ ] No remaining hardcoded references to net11.0 or version 11
- [ ] Run API compatibility checks
- [ ] Test project creation with updated templates
- [ ] Run workload tests

---

## Search Commands for Verification

```bash
# Find remaining net11.0 references
git grep -i "net11\.0" | grep -v ".git" | grep -v "history" | grep -v ".md:"

# Find remaining version 11 references (excluding this file)
git grep -E "(Version|version)[^\w]*11[^\w]" | grep -v ".git" | grep -v "version-bump"

# Find TFM-related references
git grep -i "NetCoreAppCurrent" | grep -v ".git"

# Find workload manifest references
git grep -i "110100" | grep -v ".git"

# Find template identity references
git grep -i "identity.*11\.0" | grep -v ".git"
```

---

## Special Considerations

1. **Blazor Template Version:** Note that Blazor templates might initially target a different version (e.g., the SDK's default version) than the main framework. Check `DefaultTargetFrameworkForBlazorTemplate` usage.

2. **Minimum Version:** When updating `NetCoreAppMinimum`, consider projects that need to support older runtimes (like `WasmSymbolicator.csproj` which supports older xharness).

3. **Previous Version Support:** The `NetCoreAppPrevious` property might be intentionally cleared during early development and set later when the previous version is finalized.

4. **Workload Manifest Timing:** The new workload manifest might not be fully functional until SDK changes are also merged in the dotnet/sdk repository.

5. **API Compatibility:** Some compatibility suppressions can be cleaned up after the version bump, removing obsolete net10.0 references.

6. **Test Filtering:** Some tests might need to be temporarily disabled (using `[ActiveIssue]`) if they depend on external services or infrastructure that hasn't been updated yet.

---

## Order of Operations

1. **Phase 1 - Core Version Updates:**
   - Update `eng/Versions.props`
   - Update `Directory.Build.props`
   - Update `Directory.Build.targets`

2. **Phase 2 - Workload Infrastructure:**
   - Create new workload manifest directory
   - Update workload-related targets and props files
   - Update manifest package references

3. **Phase 3 - Project Files:**
   - Update all test asset `.csproj` files
   - Update template files
   - Update build configuration files

4. **Phase 4 - Documentation and Metadata:**
   - Update documentation files
   - Update compatibility suppressions
   - Update API baselines

5. **Phase 5 - Testing Infrastructure:**
   - Update test constants
   - Update test configurations
   - Add new runtime pack versions

6. **Phase 6 - Verification:**
   - Run systematic searches for remaining references
   - Build and test
   - Verify workloads and templates

---

## Notes for LLM Execution

When automating this process:

1. **Parse version systematically:** Extract current version from `eng/Versions.props` and calculate target version.

2. **Use bulk operations:** Group similar file updates (e.g., all `.csproj` files in test assets) into batch operations.

3. **Handle templates carefully:** Template files often have nested JSON structures - preserve formatting and structure.

4. **Verify file existence:** Before copying/creating new manifest directories, verify source exists.

5. **Preserve localization:** When updating localized files, maintain character encoding and translation structure.

6. **Test incrementally:** After each phase, verify the changes compile before proceeding.

7. **Generate verification report:** After completion, run the search commands and report any remaining references for manual review.

---

## End of Instructions

This document should be updated annually to reflect any new patterns or file locations discovered during the version bump process.
