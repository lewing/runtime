#!/usr/bin/env bash
# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.
#
# Re-imports the nesm toolchain subset into Microsoft.NET.WebAssembly.Toolchain.
#
#   sync-nesm.sh <nesm-checkout> [<commit>]   Import <commit> (default: HEAD of the checkout), rename namespaces,
#                                             add license headers, then re-apply runtime-local.patch.
#   sync-nesm.sh --update-patch <nesm-checkout>
#                                             Regenerate runtime-local.patch: the difference between the mechanical
#                                             import of the commit in nesm-commit.txt and the current sources.
#
# runtime-local.patch holds edits made in this repo to the library. Upstream them to nesm when possible so the
# patch shrinks. Files that exist only here are never touched.
#
# Tests listed in nesm-test-files.txt (paths under nesm's tests/Nesm.Tests) are adapted by nesm-sync, a syntax-only
# Roslyn rewriter (see nesm-sync/TestAdapter.cs) that applies nesm's import rule: tests marked
# [Trait("Requires", ...)] need nesm's interpreter, wasi-sdk parity harness or repository, and are deleted.
# Members that need hand adaptation live in runtime-only <File>.RuntimeLocal.cs partial files, which replace nesm's
# members of the same name on every sync. Create one with:
#   dotnet run --project nesm-sync -- extract <synced test file> <member>...

set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$here/../../.." && pwd)"
lib="$here/Microsoft.NET.WebAssembly.Toolchain"
commit_file="$here/nesm-commit.txt"
files_list="$here/nesm-files.txt"
patch_file="$here/runtime-local.patch"
tests="$here/tests/Microsoft.NET.WebAssembly.Toolchain.Tests"
test_files_list="$here/nesm-test-files.txt"
subset_dirs=(Types Module Validation Binary Linker Wat Debugging/Symbols)

nesm_sync() {
    "$repo_root/dotnet.sh" run --project "$here/nesm-sync/nesm-sync.csproj" -c Release -v q --no-launch-profile -- "$@"
}

# Writes nesm's tests listed in nesm-test-files.txt at <commit>, adapted against the library in <library>, into <out>.
stage_tests() {
    local checkout="$1" commit="$2" library="$3" out="$4" f
    while read -r f; do
        mkdir -p "$out/$(dirname "$f")"
        git -C "$checkout" show "$commit:tests/Nesm.Tests/$f" > "$out/$f"
    done < "$test_files_list"
    nesm_sync tests "$out" --library "$library" --runtime-tests "$tests" --synced "$test_files_list"
}

# Writes a patch from the files under <a> to the matching files under <b>.
write_patch() {
    local a="$1" b="$2" file="$3" work
    work="$(mktemp -d)"
    cp -R "$a" "$work/a"
    mkdir -p "$work/b"
    (cd "$work/a" && find . -name '*.cs') | while read -r f; do
        if [[ -f "$b/$f" ]]; then
            mkdir -p "$work/b/$(dirname "$f")"
            cp "$b/$f" "$work/b/$f"
        fi
    done
    (cd "$work" && diff -ruN a b | perl -pe 's/^((?:---|\+\+\+) \S+)\t.*$/$1/' > "$file") || true
    rm -rf "$work"
}

# Writes the mechanically converted subset of nesm <checkout> at <commit> into <out>.
stage() {
    local checkout="$1" commit="$2" out="$3" raw
    raw="$(mktemp -d)"
    git -C "$checkout" archive "$commit" \
        $(printf 'src/Nesm/%s ' "${subset_dirs[@]}") src/Nesm.Toolchain/WasmFunction.Decoded.Toolchain.cs \
        | tar -x -C "$raw"

    mkdir -p "$out"
    for dir in "${subset_dirs[@]}"; do
        mkdir -p "$out/$(dirname "$dir")"
        cp -R "$raw/src/Nesm/$dir" "$out/$dir"
    done
    cp "$raw/src/Nesm.Toolchain/WasmFunction.Decoded.Toolchain.cs" "$out/Module/"
    # Mirrors the exclusions in nesm's src/Nesm.Toolchain/Nesm.Toolchain.csproj.
    rm -f "$out/Wat/WastRunner.cs"
    find "$out/Debugging" -name '*.Runtime.cs' -delete
    find "$out" -type f ! -name '*.cs' -delete
    rm -rf "$raw"

    nesm_sync library "$out"
}

if [[ "${1:-}" == "--update-patch" ]]; then
    checkout="${2:?usage: sync-nesm.sh --update-patch <nesm-checkout>}"
    work="$(mktemp -d)"
    stage "$checkout" "$(cat "$commit_file")" "$work/lib"
    write_patch "$work/lib" "$lib" "$patch_file"
    rm -rf "$work"
    echo "Wrote $patch_file"
    exit 0
fi

checkout="${1:?usage: sync-nesm.sh <nesm-checkout> [<commit>]}"
commit="$(git -C "$checkout" rev-parse "${2:-HEAD}")"
work="$(mktemp -d)"
stage "$checkout" "$commit" "$work/staged"

if [[ -f "$files_list" ]]; then
    while read -r f; do rm -f "$lib/$f"; done < "$files_list"
fi
cp -R "$work/staged/." "$lib/"
(cd "$work/staged" && find . -name '*.cs' | sed 's|^\./||' | LC_ALL=C sort) > "$files_list"

echo "$commit" > "$commit_file"
if [[ -s "$patch_file" ]]; then
    patch -p1 -d "$lib" --no-backup-if-mismatch < "$patch_file" || true
fi

# The tests are adapted against the patched library, so a member runtime's copy lacks drops the tests that use it.
stage_tests "$checkout" "$commit" "$lib" "$work/tests"
cp -R "$work/tests/." "$tests/"
rm -rf "$work"
echo "Imported nesm $commit. Resolve any rejected hunks, then run '$0 --update-patch $checkout'."
