// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using Microsoft.NET.WebAssembly.Binary;

namespace Microsoft.NET.WebAssembly.Linker;

/// <summary>
/// An <c>llvm-ar</c> compatible front end over <see cref="ArArchiveWriter"/>: the operations <c>r</c> (insert or
/// replace), <c>q</c> (append), <c>d</c> (delete), <c>t</c> (list), <c>x</c> (extract) and <c>s</c> (index only),
/// with the <c>c</c>, <c>s</c>, <c>S</c>, <c>D</c>, <c>u</c>, <c>U</c> and <c>v</c> modifiers. Archives are GNU
/// format and deterministic, as llvm-ar writes by default, and byte-identical to its output.
/// </summary>
public static class WasmArCommandLine
{
    /// <summary>The usage text, for a command invoked as <paramref name="command"/> (e.g. <c>nesm ar</c>).</summary>
    public static string GetUsage(string command) =>
        $"Usage: {command} [-]<operation>[modifiers] [--format=gnu] <archive> [files...]\n" +
        "  Like llvm-ar (GNU format, deterministic). Operations: r (insert/replace), q (append), d (delete),\n" +
        "  t (list), x (extract), s (index only). Modifiers: c (no create warning), s (symbol index, default),\n" +
        "  S (no symbol index), D (deterministic, default), u, U and v (accepted).";

    /// <summary>
    /// Runs the command (response files are expanded) and returns its warnings, such as creating an archive without
    /// the <c>c</c> modifier.
    /// </summary>
    /// <param name="args">The arguments after <c>ar</c>.</param>
    /// <param name="output">Where <c>t</c> lists members; the console when null.</param>
    /// <param name="workingDirectory">
    /// Where <c>x</c> extracts members; the current directory when null. The archive and input paths are not
    /// resolved against it (pass them as the caller resolves them).
    /// </param>
    /// <param name="command">How the caller invokes the command, for the usage text (e.g. <c>nesm ar</c>).</param>
    /// <exception cref="WasmLdArgumentException">The arguments are invalid.</exception>
    /// <exception cref="IOException">The archive or an input cannot be read or written.</exception>
    /// <exception cref="InvalidDataException">The archive is malformed.</exception>
    public static IReadOnlyList<string> Run(IEnumerable<string> args, TextWriter? output = null, string? workingDirectory = null, string command = "ar")
    {
        ArgumentNullException.ThrowIfNull(args);
        var positional = new List<string>();
        foreach (string arg in WasmLdCommandLine.ExpandResponseFiles(args))
        {
            if (arg.StartsWith("--format=", StringComparison.Ordinal))
            {
                if (arg is not ("--format=gnu" or "--format=default"))
                    throw new WasmLdArgumentException($"unsupported archive format '{arg["--format=".Length..]}'; only gnu is supported.");
                continue;
            }

            positional.Add(arg);
        }

        if (positional.Count < 2)
            throw new WasmLdArgumentException(GetUsage(command));

        char? operation = null;
        bool create = false, symbols = true, explicitIndex = false;
        foreach (char c in positional[0].TrimStart('-'))
        {
            switch (c)
            {
                case 'r' or 'q' or 'd' or 't' or 'x':
                    if (operation is not null)
                        throw new WasmLdArgumentException("only one operation may be specified");
                    operation = c;
                    break;
                case 'c': create = true; break;
                case 's': explicitIndex = true; symbols = true; break;
                case 'S': symbols = false; break;
                case 'D' or 'u' or 'v' or 'U': break;
                default: throw new WasmLdArgumentException($"unknown option {c}");
            }
        }

        operation ??= explicitIndex ? 's' : null;
        if (operation is null)
            throw new WasmLdArgumentException("an operation is required (r, q, d, t, x or s)");

        string archive = positional[1];
        List<string> files = positional[2..];
        var warnings = new List<string>();
        bool exists = File.Exists(archive);
        if (operation is 't' or 'x')
        {
            if (exists)
                ListOrExtract(archive, operation == 'x', files, output, workingDirectory);
            return warnings;
        }

        List<ArArchiveWriter.Member> members = [];
        if (exists)
        {
            // Closed before the archive is rewritten (Windows refuses to replace a file that is open).
            using FileStream stream = File.OpenRead(archive);
            foreach (var member in ArArchiveReader.ReadMembers(stream, archive))
                members.Add(new ArArchiveWriter.Member(member.Name, member.Data));
        }

        switch (operation)
        {
            case 'r' or 'q':
                if (!exists && !create)
                    warnings.Add($"creating {archive}");
                foreach (string file in files)
                {
                    var member = ArArchiveWriter.FromFile(file);
                    int index = operation == 'r' ? members.FindIndex(m => m.Name == member.Name) : -1;
                    if (index >= 0)
                        members[index] = member;
                    else
                        members.Add(member);
                }

                break;
            case 'd':
                members.RemoveAll(m => files.Contains(m.Name));
                break;
            case 's':
                if (!exists)
                    throw new FileNotFoundException($"unable to open '{archive}'", archive);
                break;
        }

        // The symbol index precedes the members and names every member's symbols, so the members are held in
        // memory; the archive itself is streamed to the file once it has been laid out.
        ArArchiveWriter.WriteFile(archive, members, symbols);
        return warnings;
    }

    // t and x read only the member headers, and x copies each member's contents straight to its file.
    private static void ListOrExtract(string archive, bool extract, List<string> files, TextWriter? output, string? workingDirectory)
    {
        using FileStream stream = File.OpenRead(archive);
        byte[]? buffer = null;
        try
        {
            foreach (var member in ArArchiveReader.ReadMemberLayouts(stream, archive))
            {
                if (!extract)
                {
                    (output ?? Console.Out).WriteLine(member.Name);
                    continue;
                }

                if (files.Count != 0 && !files.Contains(member.Name))
                    continue;

                // Like llvm-ar, only the base name: a member name must not write outside the working directory.
                string fileName = Path.GetFileName(member.Name);
                if (fileName is "" or "." or "..")
                    throw new InvalidDataException($"archive member name '{member.Name}' is not a file name.");

                buffer ??= ArrayPool<byte>.Shared.Rent(81920);
                stream.Position = member.Offset;
                using var target = new FileStream(
                    workingDirectory is null ? fileName : Path.Combine(workingDirectory, fileName),
                    FileMode.Create, FileAccess.Write, FileShare.Read);
                for (int remaining = member.Length; remaining > 0;)
                {
                    int chunk = Math.Min(remaining, buffer.Length);
                    stream.ReadExactly(buffer, 0, chunk);
                    target.Write(buffer, 0, chunk);
                    remaining -= chunk;
                }
            }
        }
        finally
        {
            if (buffer is not null)
                ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
