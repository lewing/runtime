// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Microsoft.NET.WebAssembly.NesmSync;

/// <summary>
/// Renames nesm's namespaces (<c>Nesm.Binary</c> → <c>Microsoft.NET.WebAssembly.Binary</c>, <c>Nesm.Tests</c> →
/// <c>Microsoft.NET.WebAssembly.Toolchain.Tests</c>, the root <c>Nesm</c> → <c>Microsoft.NET.WebAssembly</c>) and adds
/// the license header. Only the renamed text changes, so the rest of each file keeps nesm's bytes.
/// </summary>
internal static partial class Renamer
{
    public const string Header =
        "// Licensed to the .NET Foundation under one or more agreements.\n" +
        "// The .NET Foundation licenses this file to you under the MIT license.\n\n";

    private const string Root = "Microsoft.NET.WebAssembly";
    private const string TestsRoot = "Microsoft.NET.WebAssembly.Toolchain";

    public static readonly CSharpParseOptions ParseOptions =
        new(LanguageVersion.Preview, DocumentationMode.Parse);

    // The subset's top-level namespaces; anything else (Nesm.Execution, Nesm.Component, ...) isn't imported.
    public static readonly string[] SubsetNamespaces = ["Binary", "Debugging", "Linker", "Module", "Types", "Validation", "Wat"];

    [GeneratedRegex(@"\bNesm\.Tests\b")]
    private static partial Regex TestsInText();

    [GeneratedRegex(@"\bNesm\.(Binary|Debugging|Linker|Module|Types|Validation|Wat)\b")]
    private static partial Regex SubsetInText();

    public static string Rename(string text)
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText(text, ParseOptions);
        SourceText source = tree.GetText();
        var changes = new List<TextChange>();

        foreach (SyntaxToken token in tree.GetRoot().DescendantTokens(descendIntoTrivia: true))
        {
            if (token.IsKind(SyntaxKind.IdentifierToken) && token.ValueText == "Nesm")
            {
                string? replacement = RenameNesmIdentifier(token);
                if (replacement is not null)
                    changes.Add(new TextChange(token.Span, replacement));
            }
            else if (token.Kind() is SyntaxKind.StringLiteralToken or SyntaxKind.InterpolatedStringTextToken
                     or SyntaxKind.XmlTextLiteralToken or SyntaxKind.SingleLineRawStringLiteralToken
                     or SyntaxKind.MultiLineRawStringLiteralToken or SyntaxKind.Utf8StringLiteralToken)
            {
                AddTextRenames(changes, token.Span, token.Text);
            }
        }

        foreach (SyntaxTrivia trivia in tree.GetRoot().DescendantTrivia(descendIntoTrivia: true))
        {
            if (trivia.Kind() is SyntaxKind.SingleLineCommentTrivia or SyntaxKind.MultiLineCommentTrivia)
                AddTextRenames(changes, trivia.Span, trivia.ToFullString());
        }

        string renamed = source.WithChanges(changes).ToString();
        return renamed.StartsWith("// Licensed to the .NET Foundation", StringComparison.Ordinal) ? renamed : Header + renamed;
    }

    // `Nesm.<subset>...` and `Nesm.Tests...` anywhere, and the bare root in `namespace Nesm;` / `using Nesm;`.
    private static string? RenameNesmIdentifier(SyntaxToken token)
    {
        SyntaxToken dot = token.GetNextToken(includeSkipped: false, includeDirectives: false, includeDocumentationComments: true);
        if (dot.IsKind(SyntaxKind.DotToken))
        {
            string next = dot.GetNextToken(includeDocumentationComments: true).ValueText;
            if (next == "Tests")
                return TestsRoot;
            if (Array.IndexOf(SubsetNamespaces, next) >= 0)
                return Root;
            return null;
        }

        return token.Parent is IdentifierNameSyntax { Parent: BaseNamespaceDeclarationSyntax or UsingDirectiveSyntax } ? Root : null;
    }

    private static void AddTextRenames(List<TextChange> changes, TextSpan span, string text)
    {
        string renamed = SubsetInText().Replace(TestsInText().Replace(text, TestsRoot + ".Tests"), Root + ".$1");
        if (!string.Equals(renamed, text, StringComparison.Ordinal))
            changes.Add(new TextChange(span, renamed));
    }
}

internal sealed class NesmSyncException(string message) : Exception(message);
