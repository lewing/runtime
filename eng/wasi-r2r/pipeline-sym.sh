#!/bin/bash
# Portable WASI composite-R2R splice pipeline.
# Splices the crossgen composite R2R code section into the corerun component's core module.
#
# Optional env (all have sensible defaults derived from this script's location):
#   TOOLS  = dir containing surgery/ and activate/                 default: this script's directory
#   ROOT   = repo/worktree root (has artifacts/)                   default: $TOOLS/../..
#   COMP   = crossgen composite output wasm                        default $ROOT/r2rtest/out/composite-r2r.wasm
#   CORERUN= built corerun component                               default $ROOT/artifacts/bin/coreclr/wasi.wasm.Release/corerun
#   OUTDIR = work/output dir                                        default $ROOT/r2rtest/ccsym
#   NESM_ASSEMBLY = path to Nesm.dll required by surgery/activate  (see README.md)
# Produces $OUTDIR/corerun-composite-sym.wasm (the runnable component).
set -e
TOOLS=${TOOLS:-$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)}
ROOT=${ROOT:-$(cd "$TOOLS/../.." && pwd)}
export PATH="$HOME/.cargo/bin:/opt/homebrew/bin:$PATH"

for tool in wasm-tools wasm-merge wasm-objdump; do
    command -v "$tool" >/dev/null || { echo "error: '$tool' not found on PATH (see eng/wasi-r2r/README.md)" >&2; exit 1; }
done

DOTNET=$ROOT/.dotnet/dotnet
CORERUN=${CORERUN:-$ROOT/artifacts/bin/coreclr/wasi.wasm.Release/corerun}
COMP=${COMP:-$ROOT/r2rtest/out/composite-r2r.wasm}

[ -f "$CORERUN" ] || { echo "error: corerun not found at '$CORERUN' — build it with './build.sh clr+host -os wasi -c Release'" >&2; exit 1; }
[ -f "$COMP" ] || { echo "error: composite not found at '$COMP' — run crossgen2 first (see docs/workflow/building/coreclr/wasi-r2r.md)" >&2; exit 1; }

D=${OUTDIR:-$ROOT/r2rtest/ccsym}; rm -rf "$D"; mkdir -p "$D"

# 1. unbundle the corerun component -> core module 0
wasm-tools component unbundle "$CORERUN" --module-dir "$D" -o /dev/null 2>&1 | head -1
MAIN=$(ls "$D"/*module0*.wasm | head -1)

# 2. read table base / image base / table size from the corerun core module
TB=$(wasm-objdump -x "$MAIN" 2>/dev/null | grep -iE "^ - table\[0\]" | grep -oE "initial=[0-9]+" | grep -oE "[0-9]+")
IDX=$(wasm-objdump -j Export -x "$MAIN" 2>/dev/null | grep -i "wasi_r2r_image_base" | grep -oE "func\[[0-9]+\]" | grep -oE "[0-9]+")
ADDR=$(wasm-objdump -d "$MAIN" 2>/dev/null | awk -v f="func\\\[$IDX\\\]" '$0 ~ f {p=1} p&&/i32.const/{print $NF; exit}')
TS=$(wasm-objdump -h "$COMP" 2>/dev/null | grep -iE "^ Function " | grep -oE "count: [0-9]+" | grep -oE "[0-9]+")
echo "SYM: tableBase=$TB imageBase=$ADDR tableSize=$TS main=$(basename "$MAIN")"

# 3. surgery: rewrite the corerun core module for the composite (relocate table/image base)
( cd "$TOOLS/surgery" && $DOTNET run -c Release -- "$MAIN" "$D/edited-sym.wasm" "$ADDR" "$TB" "$TS" 2>&1 | tail -1 )

# 4. merge the composite R2R module in as section `comp`
wasm-merge -g --all-features --skip-export-conflicts "$D/edited-sym.wasm" webcil "$COMP" comp -o "$D/merged-sym.wasm" 2>&1 | tail -1

# 5. activate: wire up the merged R2R (virtual-IP table etc.)
( cd "$TOOLS/activate" && $DOTNET run -c Release -- "$D/merged-sym.wasm" "$D/active-sym.wasm" 2>&1 | tail -1 )

# 6. swap the activated core module back into the corerun COMPONENT (replace first core module section)
python3 - "$CORERUN" "$D/active-sym.wasm" "$D/corerun-composite-sym.wasm" <<'PY'
import sys
cp,mp,op=sys.argv[1:4]
merged=open(mp,'rb').read();data=open(cp,'rb').read()
def wl(v):
    o=bytearray()
    while True:
        b=v&0x7f;v>>=7
        if v:o.append(b|0x80)
        else:o.append(b);break
    return bytes(o)
def rl(d,p):
    r=s=0
    while True:
        b=d[p];p+=1;r|=(b&0x7f)<<s;s+=7
        if not(b&0x80):break
    return r,p
out=bytearray(data[:8]);pos=8;sw=False
while pos<len(data):
    sid=data[pos];ss=pos;pos+=1
    size,pos=rl(data,pos)
    if sid==1 and not sw: out.append(1);out+=wl(len(merged));out+=merged;sw=True
    else: out+=data[ss:pos+size]
    pos+=size
open(op,'wb').write(out)
PY
wasm-tools validate --features all "$D/corerun-composite-sym.wasm" >/dev/null 2>&1 && echo "VALID" || echo "INVALID"
echo "OUT: $D/corerun-composite-sym.wasm"
