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
# runtime-local.patch holds edits made in this repo. Upstream them to nesm when possible so the patch shrinks.
# Files that exist only here are never touched.
#
# Tests listed in nesm-test-files.txt (paths under nesm's tests/Nesm.Tests) are synchronized the same way, with
# runtime-local-tests.patch holding the edits that let them run without nesm's interpreter.

set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
lib="$here/Microsoft.NET.WebAssembly.Toolchain"
commit_file="$here/nesm-commit.txt"
files_list="$here/nesm-files.txt"
patch_file="$here/runtime-local.patch"
tests="$here/tests/Microsoft.NET.WebAssembly.Toolchain.Tests"
test_files_list="$here/nesm-test-files.txt"
tests_patch_file="$here/runtime-local-tests.patch"
subset_dirs=(Types Module Validation Binary Linker Wat Debugging/Symbols)

convert() {
    perl -pi -e 's/\bNesm\.Tests\b/Microsoft.NET.WebAssembly.Toolchain.Tests/g; s/\bNesm\.(Binary|Debugging|Linker|Module|Types|Validation|Wat)\b/Microsoft.NET.WebAssembly.$1/g; s/^namespace Nesm;/namespace Microsoft.NET.WebAssembly;/; s/^using Nesm;/using Microsoft.NET.WebAssembly;/' "$1"
    { printf '%s\n%s\n\n' '// Licensed to the .NET Foundation under one or more agreements.' '// The .NET Foundation licenses this file to you under the MIT license.'; cat "$1"; } > "$1.tmp"
    mv "$1.tmp" "$1"
}

# Writes the mechanically converted tests listed in nesm-test-files.txt at <commit> into <out>.
stage_tests() {
    local checkout="$1" commit="$2" out="$3" f
    while read -r f; do
        mkdir -p "$out/$(dirname "$f")"
        git -C "$checkout" show "$commit:tests/Nesm.Tests/$f" > "$out/$f"
        convert "$out/$f"
    done < "$test_files_list"
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

    find "$out" -name '*.cs' -print0 | while IFS= read -r -d '' f; do
        convert "$f"
    done
}

if [[ "${1:-}" == "--update-patch" ]]; then
    checkout="${2:?usage: sync-nesm.sh --update-patch <nesm-checkout>}"
    work="$(mktemp -d)"
    stage "$checkout" "$(cat "$commit_file")" "$work/lib"
    write_patch "$work/lib" "$lib" "$patch_file"
    stage_tests "$checkout" "$(cat "$commit_file")" "$work/tests"
    write_patch "$work/tests" "$tests" "$tests_patch_file"
    rm -rf "$work"
    echo "Wrote $patch_file and $tests_patch_file"
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

stage_tests "$checkout" "$commit" "$work/tests"
cp -R "$work/tests/." "$tests/"
rm -rf "$work"

echo "$commit" > "$commit_file"
if [[ -s "$patch_file" ]]; then
    patch -p1 -d "$lib" --no-backup-if-mismatch < "$patch_file" || true
fi
if [[ -s "$tests_patch_file" ]]; then
    patch -p1 -d "$tests" --no-backup-if-mismatch < "$tests_patch_file" || true
fi
echo "Imported nesm $commit. Resolve any rejected hunks, then run '$0 --update-patch $checkout'."
