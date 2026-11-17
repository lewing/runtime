// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.NET.WebAssembly.Binary;

namespace Microsoft.NET.WebAssembly.Linker;

/// <summary>
/// An <c>llvm-ar</c> compatible front end over <see cref="ArArchiveWriter"/>, for the operations builds use to create
/// static libraries: <c>r</c> (insert or replace) and <c>q</c> (append), with the <c>c</c>, <c>s</c>, <c>S</c>,
/// <c>D</c>, <c>u</c> and <c>v</c> modifiers. Archives are GNU format and deterministic, as llvm-ar writes by default.
/// </summary>
/// <remarks>Runtime-only until nesm's CLI <c>ar</c> command moves into the library.</remarks>
public static class WasmArCommandLine
{
    public const string Usage =
        "Usage: wasm-link ar [-]<r|q>[cSsDuv] [--format=gnu] <archive> <files...>";

    /// <summary>Runs the command; returns warnings, such as creating an archive without the <c>c</c> modifier.</summary>
    public static IReadOnlyList<string> Run(IEnumerable<string> args)
    {
        List<string> positional = [];
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
            throw new WasmLdArgumentException(Usage);

        char? operation = null;
        bool create = false, symbols = true;
        foreach (char c in positional[0].TrimStart('-'))
        {
            switch (c)
            {
                case 'r' or 'q':
                    if (operation is not null)
                        throw new WasmLdArgumentException("only one operation may be specified");
                    operation = c;
                    break;
                case 'c': create = true; break;
                case 's': symbols = true; break;
                case 'S': symbols = false; break;
                case 'D' or 'u' or 'v' or 'U': break;
                default: throw new WasmLdArgumentException($"unsupported ar operation or modifier '{c}'. {Usage}");
            }
        }

        if (operation is null)
            throw new WasmLdArgumentException($"an operation is required (r or q). {Usage}");

        string archive = positional[1];
        List<string> warnings = [];
        List<ArArchiveWriter.Member> members = [];
        if (File.Exists(archive))
        {
            using FileStream stream = File.OpenRead(archive);
            members = [.. ArArchiveReader.ReadMembers(stream, archive).Select(static m => new ArArchiveWriter.Member(m.Name, m.Data))];
        }
        else if (!create)
        {
            warnings.Add($"creating {archive}");
        }

        foreach (string file in positional.Skip(2))
        {
            ArArchiveWriter.Member member = ArArchiveWriter.FromFile(file);
            int index = operation == 'r' ? members.FindIndex(m => m.Name == member.Name) : -1;
            if (index >= 0)
                members[index] = member;
            else
                members.Add(member);
        }

        File.WriteAllBytes(archive, ArArchiveWriter.Write(members, symbols));
        return warnings;
    }
}
