// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Nesm.Binary;
using Nesm.Module;
using Nesm.Types;
using Nesm.Linker;

// edit2 <mainIn> <mainOut> <imageBase> <tableBase> <tableSize>
string mainPath = args[0], outPath = args[1];
int imageBase = int.Parse(args[2]);
int tableBaseVal = int.Parse(args[3]);
int tableSize = int.Parse(args[4]);

static byte[] I32Const(int v){
    var ms=new MemoryStream(); ms.WriteByte(0x41);
    bool more=true; while(more){ byte b=(byte)(v&0x7F); v>>=7;
        if((v==0&&(b&0x40)==0)||(v==-1&&(b&0x40)!=0))more=false; else b|=0x80; ms.WriteByte(b);}
    ms.WriteByte(0x0B); return ms.ToArray();
}

using var fs = File.OpenRead(mainPath);
WasmModule m = ModuleReader.ReadModule(fs);
int impGlobal = m.Imports.Count(i => i.Descriptor is ImportDescriptor.Global);

// find the () -> () tag for rtlRestoreContextTag
int tagIdx = -1;
for (int i = 0; i < m.Tags.Count; i++){
    var ft = m.Types[m.Tags[i].TypeIndex];
    if (ft.Parameters.Length == 0 && ft.Results.Length == 0){ tagIdx = i; break; }
}
Console.WriteLine($"rtlRestoreContextTag -> tag[{tagIdx}] (() -> ())");

int gTableBase = impGlobal + m.Globals.Count;
m.Globals.Add(new WasmGlobal{ Type=new GlobalType(ValType.I32,Mutability.Const), InitExpr=I32Const(tableBaseVal) });
int gImageBase = impGlobal + m.Globals.Count;
m.Globals.Add(new WasmGlobal{ Type=new GlobalType(ValType.I32,Mutability.Const), InitExpr=I32Const(imageBase) });
// The runtime (#131167 helpers.cpp) OWNS and exports __async_continuation; the interp<->R2R naked
// accessors (RuntimeAsync_Load/Store) already global.get/set it. Alias that same global under the name
// the composite imports (webcil.asyncContinuation) so the merge wires both sides to ONE global.
// (Injecting a separate global would desync the C++ interp side from the R2R side.)
// On a no-async build (pre-#131167, runtime-async off) there is no __async_continuation export and the
// composite does not import asyncContinuation, so skip the alias entirely.
int gAsyncContinuation = m.Exports.ContainsKey("__async_continuation")
    ? ((ExportDescriptor.Global)m.Exports["__async_continuation"].Descriptor).GlobalIndex
    : -1;

var t0 = m.Tables[0];
m.Tables[0] = new TableType(t0.ElementType, new Limits((ulong)(tableBaseVal+tableSize), null, t0.Limits.Is64));

m.Exports["table"]                = new Export("table", new ExportDescriptor.Table(0));
m.Exports["stackPointer"]         = new Export("stackPointer", new ExportDescriptor.Global(0));
m.Exports["tableBase"]            = new Export("tableBase", new ExportDescriptor.Global(gTableBase));
m.Exports["imageBase"]            = new Export("imageBase", new ExportDescriptor.Global(gImageBase));
if (gAsyncContinuation >= 0)
    m.Exports["asyncContinuation"] = new Export("asyncContinuation", new ExportDescriptor.Global(gAsyncContinuation));
m.Exports["rtlRestoreContextTag"] = new Export("rtlRestoreContextTag", new ExportDescriptor.Tag(tagIdx));

byte[] bytes = new SectionMerger().EmitBinary(m);
File.WriteAllBytes(outPath, bytes);
Console.WriteLine($"Wrote {outPath} ({bytes.Length}). imageBase={imageBase} tableBase={tableBaseVal} tableSize={tableSize} table[0].min={tableBaseVal+tableSize}");
