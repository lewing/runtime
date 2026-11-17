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
# runtime-local.patch holds edits made in this repo (analyzer fixes, local bug fixes). Upstream them to nesm when
# possible so the patch shrinks. Files that exist only here (for example Linker/WasmLd*.cs) are never touched.
# The tests are not synchronized: they were adapted to run without nesm's interpreter.

set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
lib="$here/Microsoft.NET.WebAssembly.Toolchain"
commit_file="$here/nesm-commit.txt"
files_list="$here/nesm-files.txt"
patch_file="$here/runtime-local.patch"
subset_dirs=(Types Module Validation Binary Linker Wat)

# Writes the mechanically converted subset of nesm <checkout> at <commit> into <out>.
stage() {
    local checkout="$1" commit="$2" out="$3" raw
    raw="$(mktemp -d)"
    git -C "$checkout" archive "$commit" \
        $(printf 'src/Nesm/%s ' "${subset_dirs[@]}") src/Nesm.Toolchain/WasmFunction.Decoded.Toolchain.cs \
        | tar -x -C "$raw"

    mkdir -p "$out"
    for dir in "${subset_dirs[@]}"; do
        cp -R "$raw/src/Nesm/$dir" "$out/"
    done
    cp "$raw/src/Nesm.Toolchain/WasmFunction.Decoded.Toolchain.cs" "$out/Module/"
    rm -f "$out/Wat/WastRunner.cs"
    find "$out" -type f ! -name '*.cs' -delete
    rm -rf "$raw"

    find "$out" -name '*.cs' -print0 | while IFS= read -r -d '' f; do
        perl -pi -e 's/\bNesm\.(Binary|Linker|Module|Types|Validation|Wat)\b/Microsoft.NET.WebAssembly.$1/g; s/^namespace Nesm;/namespace Microsoft.NET.WebAssembly;/; s/^using Nesm;/using Microsoft.NET.WebAssembly;/' "$f"
        { printf '%s\n%s\n\n' '// Licensed to the .NET Foundation under one or more agreements.' '// The .NET Foundation licenses this file to you under the MIT license.'; cat "$f"; } > "$f.tmp"
        mv "$f.tmp" "$f"
    done
}

if [[ "${1:-}" == "--update-patch" ]]; then
    checkout="${2:?usage: sync-nesm.sh --update-patch <nesm-checkout>}"
    work="$(mktemp -d)"
    stage "$checkout" "$(cat "$commit_file")" "$work/a"
    mkdir -p "$work/b"
    (cd "$work/a" && find . -name '*.cs') | while read -r f; do
        if [[ -f "$lib/$f" ]]; then
            mkdir -p "$work/b/$(dirname "$f")"
            cp "$lib/$f" "$work/b/$f"
        fi
    done
    (cd "$work" && diff -ruN a b | sed -E 's/^((---|\+\+\+) [^\t]+)\t.*/\1/' > "$patch_file") || true
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
rm -rf "$work"

echo "$commit" > "$commit_file"
if [[ -s "$patch_file" ]]; then
    patch -p1 -d "$lib" --no-backup-if-mismatch < "$patch_file"
fi
echo "Imported nesm $commit. Resolve any rejected hunks, then run '$0 --update-patch $checkout'."
