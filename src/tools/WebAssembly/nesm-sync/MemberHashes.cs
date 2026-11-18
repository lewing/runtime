// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Microsoft.NET.WebAssembly.NesmSync;

internal static class MemberHashes
{
    public static IEnumerable<string> Describe(string text)
    {
        var unit = (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(text, Renamer.ParseOptions).GetRoot();
        foreach (TypeDeclarationSyntax type in unit.DescendantNodes().OfType<TypeDeclarationSyntax>())
        {
            foreach (MemberDeclarationSyntax member in type.Members)
            {
                if (member is BaseTypeDeclarationSyntax)
                    continue;
                string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(member.WithoutLeadingTrivia().ToFullString().Trim())))[..12];
                string name = string.Join(",", TypeIndex.MemberNames(member).DefaultIfEmpty(member.Kind().ToString()));
                yield return $"{type.Identifier.ValueText}.{name}\t{hash}";
            }
        }
    }
}
