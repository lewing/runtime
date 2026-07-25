// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Nesm.Binary;
using Nesm.Module;
using Nesm.Types;
using Nesm.Linker;

// activate <mergedIn> <out>  — convert webcil passive payload + R2R passive elem to ACTIVE segments,
// pre-patch tableBase into the webcil header at payload+28. No start function needed.
string inPath = args[0], outPath = args[1];

static byte[] I32ConstExpr(int v){
    var ms=new MemoryStream(); ms.WriteByte(0x41);
    bool more=true; while(more){ byte b=(byte)(v&0x7F); v>>=7;
        if((v==0&&(b&0x40)==0)||(v==-1&&(b&0x40)!=0))more=false; else b|=0x80; ms.WriteByte(b);}
    ms.WriteByte(0x0B); return ms.ToArray();
}
static int DecodeI32Const(byte[] init){
    int i=1,shift=0,result=0; byte b;
    do{ b=init[i++]; result|=(b&0x7F)<<shift; shift+=7; }while((b&0x80)!=0);
    if(shift<32&&(b&0x40)!=0) result|=-(1<<shift);
    return result;
}

using var fs = File.OpenRead(inPath);
WasmModule m = ModuleReader.ReadModule(fs);
int impGlobal = m.Imports.Count(i => i.Descriptor is ImportDescriptor.Global);

int imageBaseGlobal = ((ExportDescriptor.Global)m.Exports["imageBase"].Descriptor).GlobalIndex;
int tableBaseGlobal = ((ExportDescriptor.Global)m.Exports["tableBase"].Descriptor).GlobalIndex;
int imageBase = DecodeI32Const(m.Globals[imageBaseGlobal - impGlobal].InitExpr);
int tableBase = DecodeI32Const(m.Globals[tableBaseGlobal - impGlobal].InitExpr);
Console.WriteLine($"imageBase={imageBase} tableBase={tableBase}");

// 1. Webcil payload = the LARGEST passive data segment. Convert to active at imageBase, patch [28..32]=tableBase.
Data? payload = null; int payloadIdx = -1;
for (int i=0;i<m.DataSegments.Count;i++){
    var d=m.DataSegments[i];
    if (d.IsPassive && (payload is null || d.InitData.Length > payload.InitData.Length)){ payload=d; payloadIdx=i; }
}
if (payload is null) throw new Exception("no passive data segment (webcil payload) found");
byte[] data = (byte[])payload.InitData.Clone();
if (data.Length >= 32){ // patch tableBase into webcil header at offset 28 (what getWebcilPayload does)
    data[28]=(byte)(tableBase&0xFF); data[29]=(byte)((tableBase>>8)&0xFF);
    data[30]=(byte)((tableBase>>16)&0xFF); data[31]=(byte)((tableBase>>24)&0xFF);
}
m.DataSegments[payloadIdx] = new Data{ IsPassive=false, MemoryIndex=0, OffsetExpr=I32ConstExpr(imageBase), InitData=data };
Console.WriteLine($"payload seg #{payloadIdx}: {data.Length} bytes -> ACTIVE @ {imageBase}, patched [28..32]=tableBase");

// 2. R2R funcs passive element (TableIndex=-1, has FunctionIndices) -> ACTIVE at tableBase.
Element? elem=null; int elemIdx=-1;
for (int i=0;i<m.Elements.Count;i++){
    var e=m.Elements[i];
    if (e.TableIndex==-1 && e.FunctionIndices.Count>0){ elem=e; elemIdx=i; break; }
}
if (elem is null) throw new Exception("no passive element (R2R funcs) found");
m.Elements[elemIdx] = new Element{ TableIndex=0, OffsetExpr=I32ConstExpr(tableBase), FunctionIndices=elem.FunctionIndices };
Console.WriteLine($"elem seg #{elemIdx}: {elem.FunctionIndices.Count} funcs -> ACTIVE @ table[{tableBase}]");

byte[] bytes = new SectionMerger().EmitBinary(m);
File.WriteAllBytes(outPath, bytes);
Console.WriteLine($"wrote {outPath} ({bytes.Length} bytes) — no start function");
