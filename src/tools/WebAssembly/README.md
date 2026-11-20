# Managed WebAssembly encoding

`Microsoft.NET.WebAssembly.Toolchain` holds the WebAssembly binary encoders that the ILCompiler and crossgen2
wasm ObjectWriters use: instructions, LEB128, sections, data segments, and linking and relocation metadata.

The sources are a subset of the managed toolchain in [Blazor-Playground/nesm](https://github.com/Blazor-Playground/nesm),
which owns their tests. `nesm-commit.txt` records the nesm commit they were taken from, and `nesm-files.txt`
lists the files, relative to the project directory. The files are nesm's sources with the .NET license header added and the
namespace changed from `Nesm` to `Microsoft.NET.WebAssembly`; fix bugs upstream and re-copy rather than editing them here.
