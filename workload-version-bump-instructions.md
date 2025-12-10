# Workload Version Bump Instructions

This document provides specific instructions for updating workload-related files when bumping the .NET major version. These instructions complement the general version bump instructions.

## Overview

When bumping from .NET N to .NET N+1, workload manifests and associated infrastructure need to be updated to support the new target framework while maintaining compatibility with previous versions.

## General Pattern

**Key Principle**: When you encounter lists with "Current + previous versions", update them to follow the pattern:
- **Current (N+1)** + **N** + previous versions (N-1, N-2, etc.)

This pattern applies to:
- Workload manifest references in testing targets
- WorkloadIdForTesting item groups
- Template framework choices
- Test project version lists

**Example**: When bumping to net12:
- Before: Current, net10, net9, net8
- After: Current (now net12), net11, net10, net9, net8

The previous "Current" (net11) becomes an explicit version entry, and Current now represents net12.

## Prerequisites

1. Core version properties must be updated first (eng/Versions.props, Directory.Build.props)
2. The new frozen workload manifest directory will be **created as part of this process**

## Creating the Frozen Manifest (netN.Manifest)

**Important**: When bumping from .NET N to .NET N+1, you create a frozen manifest for **netN** (the previous Current version), NOT netN+1.

**Example**: When bumping to .NET 12 (where Current becomes net12), you create the **net11** frozen manifest to preserve the previous version's workload configuration.

The new frozen workload manifest must be created from Current.Manifest with specific transformations. Follow these steps:

### Step 1: Create Directory Structure

**New Directory:** `src/mono/nuget/Microsoft.NET.Workload.Mono.Toolchain.netN.Manifest/`

**Example**: When bumping to net12, create `Microsoft.NET.Workload.Mono.Toolchain.net11.Manifest/`

**Actions:**
1. Create the new directory: `Microsoft.NET.Workload.Mono.Toolchain.netN.Manifest/`
2. Copy the `.pkgproj` file from `netN-1.Manifest` and update:
   - Package name to include `netN` (e.g., when bumping to net12, copy from net10.Manifest and use `Microsoft.NET.Workload.Mono.ToolChain.net11.Manifest`)
   - Update version property references (e.g., `PackageVersionNet10` → `PackageVersionNet11`)
3. Copy the `localize/` directory structure from `netN-1.Manifest`

### Step 2: Create WorkloadManifest.json.in

**Source:** Copy from `Current.Manifest/WorkloadManifest.json.in` (the current state)

**Pattern Reference:** Use `netN-1.Manifest/WorkloadManifest.json.in` to understand the naming pattern

**Transformations:**
1. Copy the current `Current.Manifest/WorkloadManifest.json.in`
2. Look at `netN-1.Manifest/WorkloadManifest.json.in` to see the pattern for frozen manifests
3. Update workload IDs following the pattern from netN-1:
   - `wasm-tools` → `wasm-tools-netN`
   - `wasm-experimental` → `wasm-experimental-netN`
   - `wasi-experimental` → `wasi-experimental-netN`
4. Update all other workload IDs to include `-netN` suffix (e.g., `mobile-librarybuilder-netN`, `microsoft-net-runtime-android-netN`, etc.)
5. Update descriptions to reference ".NET N.0"
6. Replace `${NetVersion}` with `netN` throughout
7. Replace `${PackageVersion}` with `${PackageVersionNetN}` throughout

**Example**: When bumping to net12, copy Current.Manifest's json.in, reference net10.Manifest's json.in for the pattern, then use `wasm-tools-net11`, `.NET 11.0`, and `${PackageVersionNet11}`

### Step 3: Create WorkloadManifest.targets.in

**Important:** This is the most critical transformation.

**Conceptual approach:**
1. **Copy the TFM-specific section from Current.Manifest** (WorkloadManifest.targets.in) - the section that starts with "start of TFM specific logic"
2. **Use the previous frozen manifest** (netN-1.Manifest/WorkloadManifest.targets.in) as a reference for the naming pattern

**What to include:**
- **Only the TFM-specific conditional logic** - the section that conditionally imports SDK packages and sets properties for a specific target framework
- Look for patterns like:
  - `ImportGroup` with conditions like `TargetsCurrent`
  - SDK package imports for runtime packs, AOT cross-compilers, WebAssembly SDK, etc.
  - Version-specific property settings

**Transformations to apply:**
1. From Current.Manifest/WorkloadManifest.targets.in, copy everything from the comment "<!-- start of TFM specific logic -->" to the end
2. Look at netN-1.Manifest/WorkloadManifest.targets.in to understand the frozen manifest pattern
3. Change all `TargetsCurrent` conditions to `TargetsNetN`
4. Replace `${NetVersion}` with `netN` throughout
5. Update SDK package references from `.${NetVersion}` pattern to `.netN` pattern
6. Replace `$(_RuntimePackInWorkloadVersionCurrent)` with `$(_RuntimePackInWorkloadVersionN)`
7. Update all TFM references from `${NetVersion}.0` to `netN.0`
8. Ensure all `ImportGroup` and `PropertyGroup` nodes have explicit `TargetsNetN` conditions

**Example**: When bumping to net12, copy the TFM-specific section from Current.Manifest, reference net10.Manifest for patterns, then change `TargetsCurrent` to `TargetsNet11`, `${NetVersion}` to `net11`, and use `_RuntimePackInWorkloadVersion11`

**What NOT to include:**
- **Do not copy** the general/shared section that appears at the top of Current.Manifest's .targets.in (before the TFM-specific logic)
- **Do not copy** telemetry files (`Microsoft.NET.Sdk.WebAssembly.Pack.Telemetry.*.targets.in`)
- **Do not copy** settings files (`Microsoft.NET.Sdk.WebAssembly.Pack.Settings.*.targets.in`)
- **Do not include** multi-version selection logic or conditional logic that spans multiple TFMs

**Rationale:** All workload manifests (Current, netN+1, netN, etc.) are loaded simultaneously by the SDK. Non-TFM-specific logic (multi-version selection, telemetry, settings) should only exist in Current.Manifest to avoid duplication. Each frozen manifest should contain only the TFM-specific conditional logic for its respective target framework. By starting with the previous frozen manifest and updating based on Current.Manifest, you ensure structural consistency while capturing any evolution in the workload imports.

### Step 4: Update Localized Resources

**Source:** Copy from `netN-1.Manifest/localize/` directory

**Directory:** `localize/*.json`

After copying all localized string files from netN-1.Manifest (cs, de, en, es, fr, it, ja, ko, pl, pt-BR, ru, tr, zh-Hans, zh-Hant), update them:

**Changes:**
- Workload identifiers: `wasm-tools-netN-1` → `wasm-tools-netN`
- Workload identifiers: `wasm-experimental-netN-1` → `wasm-experimental-netN`
- Workload identifiers: `wasi-experimental-netN-1` → `wasi-experimental-netN` (if present)
- Version references in descriptions: Update from ".NET N-1.0" to ".NET N.0" (in all respective languages)

**Example**: When bumping to net12, copy from net10.Manifest/localize, then update `wasm-tools-net10` to `wasm-tools-net11` and ".NET 10.0" to ".NET 11.0" in all languages

### Step 5: Create AutoImport.props

Create `AutoImport.props` in the new manifest directory:

```xml
<Import Project="Sdk.props" Sdk="Microsoft.NET.Workload.Mono.ToolChain.netN" />
```

**Example**: When bumping to net12, the new net11 manifest's AutoImport.props should reference `Microsoft.NET.Workload.Mono.ToolChain.net11`

### Step 6: Register the New Manifest

**File:** `src/mono/nuget/manifest-packages.proj`

Add the new manifest as a project reference:

```xml
<!-- Add after Current.Manifest: -->
<ProjectReference Include="Microsoft.NET.Workload.Mono.Toolchain.netN.Manifest\WorkloadManifest.proj" />
```

**Example**: When bumping to net12, add `Microsoft.NET.Workload.Mono.Toolchain.net11.Manifest\WorkloadManifest.proj`

**Note:** Keep all previous version manifests (netN-1, netN-2, etc.) to maintain backwards compatibility.

## Workload Manifest Updates

### 1. Workload Manifest Package Reference (eng/Versions.props)

Update the SDK band version for workload manifests:

```xml
<!-- Change from: -->
<SdkBandVersionForWorkload_FromRuntimeVersions>N.0.100</SdkBandVersionForWorkload_FromRuntimeVersions>

<!-- To: -->
<SdkBandVersionForWorkload_FromRuntimeVersions>N+1.0.100</SdkBandVersionForWorkload_FromRuntimeVersions>
```

**Pattern**: The band version is always in the format `N.0.100` where N is the major version.

**Example**: 11.0.100 → 12.0.100

### 2. Manifest Packages Project (src/mono/nuget/manifest-packages.proj)

Add the new frozen manifest as a project reference:

```xml
<!-- Add after the Current manifest: -->
<ProjectReference Include="Microsoft.NET.Workload.Mono.Toolchain.netN.Manifest\WorkloadManifest.proj" />
```

**Example**: When bumping to net12, add the net11 manifest

**Note**: Keep all previous version manifests (netN-1, netN-2, etc.) to maintain backwards compatibility.

### 3. AutoImport Files

Update **both** AutoImport.props files to reference the new Current version:

**Files to update:**
- `src/mono/nuget/Microsoft.NET.Workload.Mono.Toolchain.netN.Manifest/AutoImport.props` (the newly created frozen manifest)
- `src/mono/nuget/Microsoft.NET.Workload.Mono.Toolchain.Current.Manifest/AutoImport.props`

```xml
<!-- Change the Import statement from: -->
<Import Project="Sdk.props" Sdk="Microsoft.NET.Workload.Mono.ToolChain.netN-1" />

<!-- To: -->
<Import Project="Sdk.props" Sdk="Microsoft.NET.Workload.Mono.ToolChain.netN" />
```

**Example**: When bumping to net12, both net11.Manifest and Current.Manifest should reference `Microsoft.NET.Workload.Mono.ToolChain.net11` (the previous stable SDK)

## Testing Infrastructure Updates

### 1. Workload Testing Configuration (eng/testing/workloads-testing.targets)

Update baseline SDK channel and shared framework channel:

```xml
<!-- Update comments and channel versions -->
<!-- install baseline builds from netN channel -->
--channel N.0

<!-- To: -->
<!-- install baseline builds from netN+1 channel -->
--channel N+1.0
```

```xml
<!-- Update shared framework channel -->
<!-- Required for running apps built with netN.0 sdk -->
-Channel N-1.0

<!-- To: -->
<!-- Required for running apps built with netN+1.0 sdk -->
-Channel N.0
```

**Pattern**: 
- Baseline channel moves forward: N.0 → N+1.0
- Shared framework channel moves forward: N-1.0 → N.0

### 2. Workload Browser/WASI Testing Targets

**Files**: `eng/testing/workloads-browser.targets`, `eng/testing/workloads-wasi.targets`

**Add the previous version (netN) to the WorkloadIdForTesting item group** when bumping to netN+1:

**For workloads-browser.targets**, add after the netN-1 entry:

```xml
<WorkloadIdForTesting Include="wasm-tools-netN;wasm-experimental-netN"
                      ManifestName="Microsoft.NET.Workload.Mono.ToolChain.netN"
                      Variant="netN"
                      Version="$(PackageVersionForWorkloadManifests)" />

<WorkloadCombinationsToInstall Include="netN" Variants="netN" Condition="'$(WorkloadsTestPreviousVersions)' == 'true'" />
```

**For workloads-wasi.targets**, add after the netN-1 entry:

```xml
<WorkloadIdForTesting Include="wasi-experimental-netN;wasi-experimental"
                      ManifestName="Microsoft.NET.Workload.Mono.ToolChain.netN"
                      Variant="netN"
                      Version="$(PackageVersionForWorkloadManifests)"
                      Condition="'$(WorkloadsTestPreviousVersions)' == 'true'" />

<WorkloadCombinationsToInstall Include="netN" Variants="netN" Condition="'$(WorkloadsTestPreviousVersions)' == 'true'" />
```

**Note**: workloads-wasm.targets doesn't need changes as it doesn't define WorkloadIdForTesting items.

## Template Updates

### 1. Template Configuration Files

Update **all** template.json files in the WebAssembly templates:

**Files**:
- `src/mono/wasm/templates/templates/browser/.template.config/template.json`
- `src/mono/wasm/templates/templates/console/.template.config/template.json`
- `src/mono/wasm/templates/templates/wasi-console/.template.config/template.json`

**Changes needed**:

```json
// Update identity
"identity": "Microsoft.NET.Runtime.MonoTargetsNet.Templates.Browser.N"
// To:
"identity": "Microsoft.NET.Runtime.MonoTargetsNet.Templates.Browser.N+1"

// Update FrameworkChoice display name
"displayName": "Current (.NET N)"
// To:
"displayName": "Current (.NET N+1)"

// Update FrameworkChoice default value
"defaultValue": "netN.0"
// To:
"defaultValue": "netN+1.0"
```

### 2. Template Project File

Update the templates project reference:

**File**: `src/mono/wasm/templates/Microsoft.NET.Runtime.WebAssembly.Templates.csproj`

```xml
<!-- Update NuGet PackageId -->
<PackageId>Microsoft.NET.Runtime.MonoTargetsNet.Templates.N</PackageId>
<!-- To: -->
<PackageId>Microsoft.NET.Runtime.MonoTargetsNet.Templates.N+1</PackageId>
```

## Test Projects

### 1. Test Base Classes

Update `TargetMajorVersion` constant in test infrastructure base classes:

**Files**:
- `src/mono/wasm/build/WasmApp.InTree.targets` → search for files importing this that define TargetMajorVersion
- `src/libraries/Common/tests/BuildTestBase.cs` (if exists in multiple locations, update all)
- Test-specific base classes that reference version numbers

```csharp
// Change from:
public const int TargetMajorVersion = N;

// To:
public const int TargetMajorVersion = N+1;
```

### 2. Test Project Files (.csproj)

Update test projects that define version-specific environment variables:

**Files**: Search for `.csproj` files in `src/mono/wasm/` test directories

```xml
<!-- Add new environment variable -->
<EnvironmentVariables Include="RUNTIME_PACK_VERN+1=$(MicrosoftNETCoreAppRuntimewinx64PackageVersion)" />
```

**Keep existing** version environment variables (RUNTIME_PACK_VERN, RUNTIME_PACK_VERN-1, etc.) for compatibility.

### 3. Test Assets

Update all test asset projects that target the current framework:

**Location**: `src/mono/wasm/testassets/`

**Files**: All `.csproj` files in subdirectories

```xml
<!-- Change from: -->
<TargetFramework>netN.0</TargetFramework>

<!-- To: -->
<TargetFramework>netN+1.0</TargetFramework>
```

**Typical test assets**:
- BlazorBasicTestApp
- WasmBasicTestApp
- WasmOnAspNetCore
- Various Library Mode tests
- Workload test applications

## Build Configuration

### 1. Local Build Property Files

Update framework references in local build configuration:

**Files**:
- `src/tests/BuildWasmApps/Wasm.Build.Tests/data/AppleBuild.LocalBuild.props`
- `src/tests/BuildWasmApps/Wasm.Build.Tests/data/WasmApp.LocalBuild.props`

```xml
<!-- Change from: -->
<KnownAppHostPack Include="Microsoft.NETCore.App" TargetFramework="netN.0" ... />
<KnownRuntimePack Include="Microsoft.NETCore.App" TargetFramework="netN.0" ... />

<!-- To: -->
<KnownAppHostPack Include="Microsoft.NETCore.App" TargetFramework="netN+1.0" ... />
<KnownRuntimePack Include="Microsoft.NETCore.App" TargetFramework="netN+1.0" ... />
```

### 2. Package Configuration (package.json)

Update Node.js package references if they include version-specific identifiers:

**File**: `src/mono/wasm/templates/templates/package.json`

```json
// Update version-specific references
"name": "@microsoft/dotnet-runtime-N"
// To:
"name": "@microsoft/dotnet-runtime-N+1"
```

## Files That Should NOT Be Changed

### Dependency Package Versions

**File**: `eng/Version.Details.props`

This file contains package dependency versions (all the `11.0.0-alpha.1.XXXXX` entries). These are **automatically updated** by the Arcade build system (Darc/Maestro) and should **NOT** be manually modified.

### Workload Testing Matrix Files

**File**: `eng/testing/workloads-wasm.targets`

This file provides shared functionality for workload testing and doesn't define WorkloadIdForTesting items. No changes needed.

**Files**: `eng/testing/workloads-browser.targets`, `eng/testing/workloads-wasi.targets`

These files **do require updates** - see section "3. Workload Browser/WASI Testing Targets" above to add the previous version (netN) to the testing matrix.

## Verification Checklist

After making all changes:

- [ ] Created netN frozen manifest directory (e.g., net11 when bumping to net12)
- [ ] All template.json files updated (3 files)
- [ ] All test assets target netN+1.0
- [ ] Test base classes have TargetMajorVersion = N+1
- [ ] Workload manifest references use N+1.0.100 band
- [ ] AutoImport.props files reference netN SDK (e.g., net11 when bumping to net12)
- [ ] manifest-packages.proj includes netN manifest
- [ ] workloads-testing.targets uses N+1.0 and N.0 channels
- [ ] workloads-browser.targets includes netN WorkloadIdForTesting entry
- [ ] workloads-wasi.targets includes netN WorkloadIdForTesting entry
- [ ] Test .csproj files have RUNTIME_PACK_VERN+1 environment variable
- [ ] Local build props files reference netN+1.0
- [ ] Template project uses correct PackageId

## Common Patterns

### Version Number Formats

- **TFM Format**: `netN.0` (e.g., net12.0)
- **Package Version**: `N.0.0` (e.g., 12.0.0)
- **SDK Band**: `N.0.100` (e.g., 12.0.100)
- **Channel**: `N.0` (e.g., 12.0)

### Workload Manifest Naming

- **Current/Latest**: `Microsoft.NET.Workload.Mono.ToolChain.Current`
- **Frozen Version**: `Microsoft.NET.Workload.Mono.ToolChain.netN` (e.g., `Microsoft.NET.Workload.Mono.ToolChain.net12`)

### Test Asset Locations

All WebAssembly test assets are under `src/mono/wasm/testassets/` and should have their TargetFramework updated.

## Search Commands for Finding Files

Use these commands to locate files that may need updating:

```bash
# Find all template.json files
git ls-files "**/.template.config/template.json"

# Find test assets with TargetFramework
git grep -l "TargetFramework.*net11.0" -- "src/mono/wasm/testassets/**/*.csproj"

# Find TargetMajorVersion in test code
git grep -l "TargetMajorVersion.*11" -- "src/mono/wasm/**/*.cs"

# Find workload testing configuration
git ls-files "eng/testing/workloads-*.targets"

# Find version references in engineering files
git grep -l "11\.0" -- "eng/*.targets" "eng/*.props" "eng/testing/*.targets"
```

## Notes

- Always update workload manifests **after** core version properties are set
- Test assets must be updated to prevent test failures
- Workload testing infrastructure changes are critical for CI/CD pipelines
- Template updates ensure `dotnet new` creates projects with the correct TFM
- Previous version manifests (netN, netN-1) should be kept for backwards compatibility
