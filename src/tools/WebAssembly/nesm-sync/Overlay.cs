// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Microsoft.NET.WebAssembly.NesmSync;

/// <summary>
/// Writes a runtime-only partial file (<c>X.RuntimeLocal.cs</c>) holding members copied from an adapted test file, so
/// that they replace nesm's versions on every sync instead of being carried as a patch.
/// </summary>
internal static class Overlay
{
    public static string Extract(string source, IReadOnlyCollection<string> members)
    {
        var unit = (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(source, Renamer.ParseOptions).GetRoot();
        var wanted = new HashSet<string>(members, StringComparer.Ordinal);
        var found = new HashSet<string>(StringComparer.Ordinal);
        var types = new List<MemberDeclarationSyntax>();
        foreach (TypeDeclarationSyntax type in unit.DescendantNodes().OfType<TypeDeclarationSyntax>().Where(static t => t.Parent is not TypeDeclarationSyntax))
        {
            var selected = type.Members.Where(m => m is not BaseTypeDeclarationSyntax && TypeIndex.MemberNames(m).Any(wanted.Contains)).ToList();
            if (selected.Count == 0)
                continue;
            found.UnionWith(selected.SelectMany(TypeIndex.MemberNames));

            SyntaxTokenList modifiers = type.Modifiers.Any(SyntaxKind.PartialKeyword)
                ? type.Modifiers
                : type.Modifiers.Add(SyntaxFactory.Token(SyntaxKind.PartialKeyword).WithTrailingTrivia(SyntaxFactory.Space));
            selected[0] = selected[0].WithLeadingTrivia(selected[0].GetLeadingTrivia().SkipWhile(static t => t.IsKind(SyntaxKind.EndOfLineTrivia)));
            types.Add(type.WithModifiers(modifiers)
                .WithAttributeLists(default)
                .WithBaseList(null)
                .WithConstraintClauses(default)
                .WithMembers(SyntaxFactory.List(selected))
                .WithLeadingTrivia(type.Modifiers.Count != 0 ? type.Modifiers[0].LeadingTrivia.Where(static t => t.IsKind(SyntaxKind.WhitespaceTrivia)) : default)
                .WithTrailingTrivia(SyntaxFactory.EndOfLine("\n")));
        }

        string[] missing = wanted.Except(found).Order(StringComparer.Ordinal).ToArray();
        if (missing.Length != 0)
            throw new NesmSyncException($"members not found: {string.Join(", ", missing)}");

        var namespaceDeclaration = unit.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
        // Usings stay where the source has them: before the namespace, or inside it as nesm writes them.
        static string Block(SyntaxList<UsingDirectiveSyntax> usings) =>
            usings.Count == 0 ? "" : string.Concat(usings.Select(static u => u.WithoutTrivia().ToFullString() + "\n")) + "\n";
        string outer = Block(unit.Usings);
        string inner = namespaceDeclaration is null ? "" : Block(namespaceDeclaration.Usings);
        string body = string.Join("\n", types.Select(static t => t.ToFullString()));
        string text = namespaceDeclaration switch
        {
            FileScopedNamespaceDeclarationSyntax ns => $"{outer}namespace {ns.Name};\n\n{inner}{body}",
            NamespaceDeclarationSyntax ns => $"{outer}namespace {ns.Name}\n{{\n{inner}{body}}}\n",
            _ => $"{outer}{body}",
        };
        return Renamer.Header + "// Runtime-only members of nesm's test class, adapted to run here. nesm-sync replaces nesm's members of the\n// same name with these on every sync (see src/tools/WebAssembly/README.md).\n\n" + text;
    }
}
