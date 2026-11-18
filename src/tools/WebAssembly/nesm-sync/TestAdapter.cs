// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Microsoft.NET.WebAssembly.NesmSync;

/// <summary>
/// Adapts nesm's tests (already renamed) to this repository, syntax only, following the import rule in nesm's
/// tests/Nesm.Tests/README.md (enforced there by ToolchainTestRequirementsTests):
/// <list type="bullet">
/// <item>Deletes every type and member carrying a <c>[Trait("Requires", ...)]</c> in <see cref="s_requirements"/>.</item>
/// <item>Replaces members that a runtime-only partial file (<c>X.RuntimeLocal.cs</c> next to <c>X.cs</c>) redefines,
/// and makes those classes partial.</item>
/// <item>Maps xunit v3 to v2: <c>Assert.Skip(...)</c> becomes <c>throw new SkipTestException(...)</c>, with
/// <c>[ConditionalFact]</c>/<c>[ConditionalTheory]</c> on the tests that can skip, and
/// <c>TestContext.Current.CancellationToken</c> becomes <c>CancellationToken.None</c>.</item>
/// <item>Removes <c>using</c>s of namespaces that don't exist here.</item>
/// </list>
/// </summary>
internal sealed class TestAdapter
{
    public const string RuntimeLocalSuffix = ".RuntimeLocal.cs";

    // nesm's ToolchainTestRequirementsTests.Requirements: what the tests need beyond the toolchain.
    private static readonly HashSet<string> s_requirements = new(StringComparer.Ordinal)
    {
        "Interpreter",
        "WasiSdkParity",
        "NesmRepo",
    };

    private readonly string _stagedDirectory;
    private readonly string _libraryDirectory;
    private readonly string _runtimeTestsDirectory;
    private readonly string[] _syncedFiles;
    private readonly TextWriter? _report;

    public TestAdapter(string stagedDirectory, string libraryDirectory, string runtimeTestsDirectory, string[] syncedFiles, TextWriter? report)
    {
        _stagedDirectory = stagedDirectory;
        _libraryDirectory = libraryDirectory;
        _runtimeTestsDirectory = runtimeTestsDirectory;
        _syncedFiles = syncedFiles;
        _report = report;
    }

    public void Run()
    {
        var files = new SortedDictionary<string, CompilationUnitSyntax>(StringComparer.Ordinal);
        foreach (string file in _syncedFiles)
            files[file] = Parse(Renamer.Rename(File.ReadAllText(Path.Combine(_stagedDirectory, file))));

        // Runtime-only test sources: everything in the test project that isn't synced, including the partial files.
        var runtimeOnly = new List<CompilationUnitSyntax>();
        var overlays = new Dictionary<string, CompilationUnitSyntax>(StringComparer.Ordinal);
        var synced = new HashSet<string>(_syncedFiles.Select(Normalize), StringComparer.Ordinal);
        foreach (string path in SourceFiles(_runtimeTestsDirectory))
        {
            string relative = Normalize(Path.GetRelativePath(_runtimeTestsDirectory, path));
            if (synced.Contains(relative) || relative.StartsWith("obj/", StringComparison.Ordinal) || relative.StartsWith("bin/", StringComparison.Ordinal))
                continue;
            CompilationUnitSyntax unit = Parse(File.ReadAllText(path));
            runtimeOnly.Add(unit);
            if (relative.EndsWith(RuntimeLocalSuffix, StringComparison.Ordinal))
                overlays[relative[..^RuntimeLocalSuffix.Length] + ".cs"] = unit;
        }

        var namespaces = new HashSet<string>(
            SourceFiles(_libraryDirectory).Select(p => Parse(File.ReadAllText(p))).Concat(runtimeOnly).Concat(files.Values)
                .SelectMany(static u => u.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>())
                .Select(static n => n.Name.ToString()),
            StringComparer.Ordinal);

        foreach ((string file, CompilationUnitSyntax original) in files.ToArray())
        {
            overlays.TryGetValue(file, out CompilationUnitSyntax? overlay);
            if (overlay is not null)
            {
                foreach (string error in CheckOverlay(original, overlay))
                    throw new NesmSyncException($"{file}: {error}");
            }

            // Tagged members aren't searched further: what they contain goes with them.
            var drops = new List<(SyntaxNode Node, string Reason)>();
            foreach (MemberDeclarationSyntax member in original.DescendantNodes(static n => n is not MemberDeclarationSyntax m || Requirement(m.AttributeLists) is null)
                .OfType<MemberDeclarationSyntax>())
            {
                string? reason = Requirement(member.AttributeLists);
                if (reason is null && overlay is not null && member.Parent is TypeDeclarationSyntax type && member is not BaseTypeDeclarationSyntax &&
                    TypeIndex.MemberNames(member).Any(TypeIndex.MembersOf(overlay, type.Identifier.ValueText).Contains))
                {
                    reason = "redefined in " + Path.GetFileName(file)[..^3] + RuntimeLocalSuffix;
                }

                if (reason is not null)
                    drops.Add((member, reason));
            }

            foreach ((SyntaxNode node, string reason) in drops.OrderBy(static d => Describe(d.Node), StringComparer.Ordinal))
                _report?.WriteLine($"{file}: dropped {Describe(node)}: {reason}");

            CompilationUnitSyntax unit = drops.Count == 0 ? original : RemoveMembers(original, drops.Select(static d => d.Node));
            files[file] = overlay is null ? unit : MakePartial(unit, overlay);
        }

        HashSet<string> skipping = SkippingMethods(files.Values);
        foreach ((string file, CompilationUnitSyntax unit) in files)
            File.WriteAllText(Path.Combine(_stagedDirectory, file), RemoveUnavailableUsings(MapXunitV3(unit, skipping), namespaces).ToFullString());
    }

    private static string? Requirement(SyntaxList<AttributeListSyntax> attributeLists)
    {
        foreach (AttributeSyntax attribute in attributeLists.SelectMany(static l => l.Attributes))
        {
            if (attribute.Name.ToString() is not ("Trait" or "TraitAttribute" or "Xunit.Trait") ||
                attribute.ArgumentList?.Arguments is not [{ Expression: LiteralExpressionSyntax key }, { Expression: LiteralExpressionSyntax value }])
            {
                continue;
            }

            if (key.Token.ValueText == "Requires" && s_requirements.Contains(value.Token.ValueText))
                return $"requires {value.Token.ValueText}";
        }

        return null;
    }

    // Removes members with their comments; keeps preprocessor directives, and doesn't leave a blank line after '{'.
    private static CompilationUnitSyntax RemoveMembers(CompilationUnitSyntax unit, IEnumerable<SyntaxNode> nodes)
    {
        unit = unit.RemoveNodes(nodes, SyntaxRemoveOptions.KeepDirectives)!;
        return unit.ReplaceNodes(
            unit.DescendantNodes().OfType<TypeDeclarationSyntax>().Where(static t => t.Members.Count != 0),
            static (_, type) =>
            {
                MemberDeclarationSyntax first = type.Members[0];
                SyntaxTriviaList leading = first.GetLeadingTrivia();
                int blank = 0;
                while (blank < leading.Count && leading[blank].IsKind(SyntaxKind.EndOfLineTrivia))
                    blank++;
                if (blank == 0 && leading.Count > 1 && leading[0].IsKind(SyntaxKind.WhitespaceTrivia) && leading[1].IsKind(SyntaxKind.EndOfLineTrivia))
                    blank = 2;
                return blank == 0 ? type : type.ReplaceNode(first, first.WithLeadingTrivia(leading.Skip(blank)));
            });
    }

    private static IEnumerable<string> CheckOverlay(CompilationUnitSyntax unit, CompilationUnitSyntax overlay)
    {
        var types = unit.DescendantNodes().OfType<TypeDeclarationSyntax>().Select(static t => t.Identifier.ValueText).ToHashSet();
        foreach (TypeDeclarationSyntax type in overlay.DescendantNodes().OfType<TypeDeclarationSyntax>())
        {
            if (!types.Contains(type.Identifier.ValueText))
                yield return $"{RuntimeLocalSuffix} extends {type.Identifier.ValueText}, which nesm's file doesn't declare";
        }
    }

    private static CompilationUnitSyntax MakePartial(CompilationUnitSyntax unit, CompilationUnitSyntax overlay)
    {
        var extended = overlay.DescendantNodes().OfType<TypeDeclarationSyntax>().Select(static t => t.Identifier.ValueText).ToHashSet();
        return unit.ReplaceNodes(
            unit.DescendantNodes().OfType<TypeDeclarationSyntax>().Where(t => extended.Contains(t.Identifier.ValueText) && !t.Modifiers.Any(SyntaxKind.PartialKeyword)),
            static (_, type) => type.WithModifiers(type.Modifiers.Add(SyntaxFactory.Token(SyntaxKind.PartialKeyword).WithTrailingTrivia(SyntaxFactory.Space))));
    }

    // Methods that can skip: those that call Assert.Skip, and those that call one that can, in any synced file. Matching
    // by name can only add [ConditionalFact] to a test that never skips, which is harmless.
    private static HashSet<string> SkippingMethods(IEnumerable<CompilationUnitSyntax> units)
    {
        var skipping = new HashSet<string>(StringComparer.Ordinal);
        var methods = units.SelectMany(static u => u.DescendantNodes().OfType<MethodDeclarationSyntax>()).ToList();
        for (bool added = true; added;)
        {
            added = false;
            foreach (MethodDeclarationSyntax method in methods)
            {
                if (!skipping.Contains(method.Identifier.ValueText) &&
                    method.DescendantNodes().Any(n => IsAssertSkip(n) || n is InvocationExpressionSyntax { Expression: var callee } &&
                        callee is IdentifierNameSyntax or MemberAccessExpressionSyntax &&
                        skipping.Contains(callee is MemberAccessExpressionSyntax access ? access.Name.Identifier.ValueText : ((IdentifierNameSyntax)callee).Identifier.ValueText)))
                {
                    added |= skipping.Add(method.Identifier.ValueText);
                }
            }
        }

        return skipping;
    }

    private static CompilationUnitSyntax MapXunitV3(CompilationUnitSyntax unit, HashSet<string> skipping)
    {
        bool converted = unit.DescendantNodes().Any(n => IsAssertSkip(n) ||
            n is MethodDeclarationSyntax m && skipping.Contains(m.Identifier.ValueText) && m.AttributeLists.SelectMany(static l => l.Attributes)
                .Any(static a => a.Name is IdentifierNameSyntax { Identifier.ValueText: "Fact" or "Theory" }));
        unit = unit.ReplaceNodes(
            unit.DescendantNodes().Where(n => n is ExpressionStatementSyntax { Expression: var e } && IsAssertSkip(e) || IsCancellationToken(n) ||
                n is AttributeSyntax { Name: IdentifierNameSyntax { Identifier.ValueText: "Fact" or "Theory" } } a &&
                a.FirstAncestorOrSelf<MethodDeclarationSyntax>() is { } m && skipping.Contains(m.Identifier.ValueText)),
            static (_, node) => node switch
            {
                ExpressionStatementSyntax { Expression: InvocationExpressionSyntax skip } statement =>
                    SyntaxFactory.ParseStatement($"throw new SkipTestException{skip.ArgumentList.ToFullString()};").WithTriviaFrom(statement),
                AttributeSyntax attribute => attribute.WithName(SyntaxFactory.IdentifierName(
                    "Conditional" + ((IdentifierNameSyntax)attribute.Name).Identifier.ValueText).WithTriviaFrom(attribute.Name)),
                _ => SyntaxFactory.ParseExpression("CancellationToken.None").WithTriviaFrom(node),
            });

        if (converted && !unit.DescendantNodes().OfType<UsingDirectiveSyntax>().Any(static u => u.Name?.ToString() == "Microsoft.DotNet.XUnitExtensions"))
        {
            // The new directive goes first in whichever using list the file has (nesm writes them inside the namespace),
            // and takes over the first directive's leading trivia.
            UsingDirectiveSyntax directive = SyntaxFactory.ParseCompilationUnit("using Microsoft.DotNet.XUnitExtensions;\n").Usings[0];
            static SyntaxList<UsingDirectiveSyntax> Prepend(SyntaxList<UsingDirectiveSyntax> usings, UsingDirectiveSyntax directive) =>
                SyntaxFactory.List(new[] { directive.WithLeadingTrivia(usings[0].GetLeadingTrivia()), usings[0].WithLeadingTrivia() }.Concat(usings.Skip(1)));

            BaseNamespaceDeclarationSyntax? ns = unit.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
            if (unit.Usings.Count != 0)
                unit = unit.WithUsings(Prepend(unit.Usings, directive));
            else if (ns is { Usings.Count: > 0 })
                unit = unit.ReplaceNode(ns, ns.WithUsings(Prepend(ns.Usings, directive)));
            else
                unit = unit.AddUsings(directive);
        }

        return unit;
    }

    private static bool IsAssertSkip(SyntaxNode node) =>
        node is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "Assert" }, Name.Identifier.ValueText: "Skip" } };

    private static bool IsCancellationToken(SyntaxNode node) =>
        node is MemberAccessExpressionSyntax
        {
            Name.Identifier.ValueText: "CancellationToken",
            Expression: MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "TestContext" }, Name.Identifier.ValueText: "Current" },
        };

    private static CompilationUnitSyntax RemoveUnavailableUsings(CompilationUnitSyntax unit, HashSet<string> runtimeNamespaces)
    {
        var unavailable = unit.DescendantNodes().OfType<UsingDirectiveSyntax>().Where(u => u.Name?.ToString() is string name &&
            (name.StartsWith("Microsoft.NET.WebAssembly", StringComparison.Ordinal) || name.StartsWith("Nesm", StringComparison.Ordinal)) &&
            !runtimeNamespaces.Contains(name)).ToList();
        return unavailable.Count == 0 ? unit : unit.RemoveNodes(unavailable, SyntaxRemoveOptions.KeepDirectives | SyntaxRemoveOptions.KeepLeadingTrivia)!;
    }

    private static string Describe(SyntaxNode node) => node switch
    {
        BaseTypeDeclarationSyntax t => t.Identifier.ValueText,
        MemberDeclarationSyntax m => string.Join(", ", TypeIndex.MemberNames(m)),
        _ => node.Kind().ToString(),
    };

    private static CompilationUnitSyntax Parse(string text) =>
        (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(text, Renamer.ParseOptions).GetRoot();

    private static string Normalize(string path) => path.Replace('\\', '/');

    private static IEnumerable<string> SourceFiles(string dir) =>
        Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories).Order(StringComparer.Ordinal) : [];
}

/// <summary>Member names of type declarations.</summary>
internal static class TypeIndex
{
    public static HashSet<string> MembersOf(CompilationUnitSyntax unit, string type) =>
        unit.DescendantNodes().OfType<TypeDeclarationSyntax>().Where(t => t.Identifier.ValueText == type)
            .SelectMany(static t => t.Members).SelectMany(MemberNames).ToHashSet(StringComparer.Ordinal);

    public static IEnumerable<string> MemberNames(MemberDeclarationSyntax member) => member switch
    {
        MethodDeclarationSyntax m => [m.Identifier.ValueText],
        PropertyDeclarationSyntax p => [p.Identifier.ValueText],
        EventDeclarationSyntax e => [e.Identifier.ValueText],
        BaseFieldDeclarationSyntax f => f.Declaration.Variables.Select(static v => v.Identifier.ValueText),
        BaseTypeDeclarationSyntax t => [t.Identifier.ValueText],
        DelegateDeclarationSyntax d => [d.Identifier.ValueText],
        EnumMemberDeclarationSyntax e => [e.Identifier.ValueText],
        _ => [],
    };
}
