// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.NET.WebAssembly.NesmSync;

// nesm-sync library <dir>
//     Renames Nesm.* namespaces to Microsoft.NET.WebAssembly.* and adds license headers, in place.
// nesm-sync extract <file> <member>...
//     Writes <file without .cs>.RuntimeLocal.cs with the named members, which then replace nesm's on every sync.
// nesm-sync members <file>...
//     Lists each member of each file with a hash of its text.
// nesm-sync tests <dir> --nesm <checkout> --library <dir> --runtime-tests <dir> --synced <list> [--report]
//     Also adapts nesm's tests to this repository: see TestAdapter.
try
{
    switch (args)
    {
        case ["library", string dir]:
            foreach (string path in SourceFiles(dir))
                File.WriteAllText(path, Renamer.Rename(File.ReadAllText(path)));
            return 0;
        case ["tests", string dir, .. string[] options]:
            var adapter = new TestAdapter(
                stagedDirectory: dir,
                nesmRoot: Option(options, "--nesm"),
                libraryDirectory: Option(options, "--library"),
                runtimeTestsDirectory: Option(options, "--runtime-tests"),
                syncedFiles: File.ReadAllLines(Option(options, "--synced")).Where(static l => l.Length != 0).ToArray(),
                report: options.Contains("--report") ? Console.Out : null);
            adapter.Run();
            return 0;
        case ["extract", string file, .. string[] members] when members.Length != 0:
            // Moves hand-adapted members of a synced test file into its runtime-only partial file.
            File.WriteAllText(Path.ChangeExtension(file, null) + TestAdapter.RuntimeLocalSuffix, Overlay.Extract(File.ReadAllText(file), members));
            return 0;
        case ["members", .. string[] paths]:
            // Each member of each file, with a hash of its text: for comparing a rewrite with hand-adapted sources.
            foreach (string path in paths)
            {
                foreach (string line in MemberHashes.Describe(File.ReadAllText(path)))
                    Console.WriteLine($"{path}\t{line}");
            }

            return 0;
        default:
            Console.Error.WriteLine("""
                Usage: nesm-sync library <dir>
                       nesm-sync tests <dir> --nesm <checkout> --library <dir> --runtime-tests <dir> --synced <list> [--report]
                """);
            return 1;
    }
}
catch (NesmSyncException ex)
{
    Console.Error.WriteLine($"nesm-sync: error: {ex.Message}");
    return 1;
}

static string Option(string[] options, string name)
{
    int index = Array.IndexOf(options, name);
    return index >= 0 && index + 1 < options.Length ? options[index + 1] : throw new NesmSyncException($"{name} is required.");
}

static IEnumerable<string> SourceFiles(string dir) =>
    Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories).Order(StringComparer.Ordinal);
