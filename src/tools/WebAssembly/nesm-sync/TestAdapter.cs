// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Microsoft.NET.WebAssembly.NesmSync;

/// <summary>
/// Adapts nesm's tests (already renamed) to this repository, syntax only:
/// <list type="bullet">
/// <item>Drops members that can't build or run here: they use a type that exists in nesm but not here (the interpreter,
/// <c>Nesm.Component</c>, nesm-only test helpers), a member of a shared helper class that only nesm's copy has (for
/// example <c>WasmLdParityTests.Execute</c>), an identifier in <see cref="s_unavailableIdentifiers"/>, a
/// <c>[Trait("Requires", ...)]</c> in <see cref="s_unavailableRequirements"/>, or a member dropped earlier. This repeats
/// until nothing changes, and then private members nothing uses any more are dropped too.</item>
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

    // Helpers that only work inside a nesm checkout (they locate the repository or its provisioned tools).
    private static readonly HashSet<string> s_unavailableIdentifiers = new(StringComparer.Ordinal)
    {
        "FindRepoRoot",
        "ProvisionedPaths",
    };

    // Tests that read files from a nesm checkout (samples, Blazor build output) and would only skip here. To be replaced by
    // nesm's [Trait("Requires", "NesmRepo")].
    private static readonly HashSet<string> s_nesmCheckoutTests = new(StringComparer.Ordinal)
    {
        "DwarfTypeReaderTests.RealWasm_HelloWasi_ParsesSomeStructs",
        "DwarfVariableReaderTests.RealWasm_Fibonacci_FindsSubprogram",
        "StreamingTests.StreamingDisassemble_LargeFile",
        "WasmDisassemblerTests.RoundTrip_DotnetNativeWasm",
    };

    // [Trait("Requires", X)] values whose tests need nesm's interpreter, wasi-sdk parity helpers or repository layout.
    private static readonly HashSet<string> s_unavailableRequirements = new(StringComparer.Ordinal)
    {
        "Interpreter",
        "WasiSdkParity",
        "NesmRepo",
    };

    private readonly string _stagedDirectory;
    private readonly string _nesmRoot;
    private readonly string _libraryDirectory;
    private readonly string _runtimeTestsDirectory;
    private readonly string[] _syncedFiles;
    private readonly TextWriter? _report;

    public TestAdapter(string stagedDirectory, string nesmRoot, string libraryDirectory, string runtimeTestsDirectory, string[] syncedFiles, TextWriter? report)
    {
        _stagedDirectory = stagedDirectory;
        _nesmRoot = nesmRoot;
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

        var library = SourceFiles(_libraryDirectory).Select(p => Parse(File.ReadAllText(p))).ToList();
        TypeIndex nesm = TypeIndex.Build(
            SourceFiles(Path.Combine(_nesmRoot, "src")).Concat(SourceFiles(Path.Combine(_nesmRoot, "tests")))
                .Select(p => Parse(File.ReadAllText(p))));
        var runtimeNamespaces = new HashSet<string>(
            library.Concat(runtimeOnly).Concat(files.Values).SelectMany(static u => u.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>())
                .Select(static n => n.Name.ToString()),
            StringComparer.Ordinal);

        var dropped = new Dictionary<string, List<(string Member, string Reason)>>(StringComparer.Ordinal);
        foreach ((string file, CompilationUnitSyntax unit) in files)
        {
            foreach (string error in overlays.TryGetValue(file, out CompilationUnitSyntax? overlay) ? CheckOverlay(unit, overlay) : [])
                throw new NesmSyncException($"{file}: {error}");
        }

        // Assertions that name a nesm namespace that isn't imported (Nesm.Execution.X) go; the rest of their test stays.
        // Other uses of something that isn't imported drop the whole member below, so no test is left without its assertion.
        {
            foreach (string file in files.Keys.ToArray())
            {
                var statements = files[file].DescendantNodes().OfType<ExpressionStatementSyntax>()
                    .Where(st => IsAssertion(st) && st.DescendantNodes().OfType<SimpleNameSyntax>().Any(IsUnimportedNesmNamespace))
                    .ToList();
                if (statements.Count == 0)
                    continue;
                foreach (ExpressionStatementSyntax statement in statements)
                {
                    if (!dropped.TryGetValue(file, out var list))
                        dropped[file] = list = [];
                    string owner = statement.FirstAncestorOrSelf<MemberDeclarationSyntax>() is { } m ? Describe(m) : "?";
                    list.Add(($"an assertion in {owner}", "uses something that isn't imported"));
                }

                files[file] = files[file].RemoveNodes(statements, SyntaxRemoveOptions.KeepDirectives)!;
            }
        }

        // Drop members until nothing else needs to go.
        bool changed = true;
        while (changed)
        {
            changed = false;
            TypeIndex runtime = TypeIndex.Build(library.Concat(runtimeOnly).Concat(files.Values));
            foreach (string file in files.Keys.ToArray())
            {
                overlays.TryGetValue(file, out CompilationUnitSyntax? overlay);
                var drops = new Dictionary<SyntaxNode, string>();
                foreach (TypeDeclarationSyntax type in files[file].DescendantNodes().OfType<TypeDeclarationSyntax>())
                {
                    HashSet<string> overlayMembers = overlay is null ? [] : TypeIndex.MembersOf(overlay, type.Identifier.ValueText);
                    foreach (MemberDeclarationSyntax member in type.Members)
                    {
                        if (member is BaseTypeDeclarationSyntax)
                            continue;
                        string? reason = TypeIndex.MemberNames(member).Any(overlayMembers.Contains)
                            ? "redefined in " + Path.GetFileName(file)[..^3] + RuntimeLocalSuffix
                            : WhyUnavailable(member, type, nesm, runtime);
                        if (reason is null)
                            continue;
                        if (member is ConstructorDeclarationSyntax)
                        {
                            drops[type] = $"its constructor {reason}";
                            break;
                        }
                        drops[member] = reason;
                    }

                    string? typeReason = Requirement(type.AttributeLists);
                    if (typeReason is not null)
                        drops[type] = typeReason;
                }

                foreach ((SyntaxNode node, string reason) in UnusedPrivateMembers(files[file], overlay, drops.Keys))
                    drops.TryAdd(node, reason);

                if (drops.Count == 0)
                    continue;
                foreach ((SyntaxNode node, string reason) in drops)
                {
                    if (!dropped.TryGetValue(file, out var list))
                        dropped[file] = list = [];
                    list.Add((Describe(node), reason));
                }

                files[file] = RemoveMembers(files[file], drops.Keys.Where(n => !drops.Keys.Any(o => o != n && o.Contains(n))));
                changed = true;
            }
        }

        foreach ((string file, CompilationUnitSyntax original) in files.ToArray())
        {
            CompilationUnitSyntax unit = original;
            if (overlays.TryGetValue(file, out CompilationUnitSyntax? overlay))
                unit = MakePartial(unit, overlay);
            unit = MapXunitV3(unit);
            unit = RemoveUnavailableUsings(unit, runtimeNamespaces);
            File.WriteAllText(Path.Combine(_stagedDirectory, file), unit.ToFullString());
        }

        if (_report is not null)
        {
            foreach ((string file, var list) in dropped)
            {
                foreach ((string member, string reason) in list.OrderBy(static d => d.Member, StringComparer.Ordinal))
                    _report.WriteLine($"{file}: dropped {member}: {reason}");
            }
        }
    }

    private static string? WhyUnavailable(MemberDeclarationSyntax member, TypeDeclarationSyntax type, TypeIndex nesm, TypeIndex runtime)
    {
        string? requirement = Requirement(member.AttributeLists);
        if (requirement is not null)
            return requirement;

        if (s_nesmCheckoutTests.Contains($"{type.Identifier.ValueText}.{string.Join(",", TypeIndex.MemberNames(member))}"))
            return "reads files from a nesm checkout";

        HashSet<string> ownMembers = runtime.Members(type.Identifier.ValueText);
        HashSet<string> nesmOwnMembers = nesm.Members(type.Identifier.ValueText);
        foreach (SimpleNameSyntax name in member.DescendantNodes().OfType<SimpleNameSyntax>())
        {
            string text = name.Identifier.ValueText;
            if (s_unavailableIdentifiers.Contains(text))
                return $"uses {text}, which needs a nesm checkout";

            if (name.Parent is MemberAccessExpressionSyntax access && access.Name == name)
            {
                // Type.Member where this repository's copy of a shared helper type lacks the member.
                if (access.Expression is IdentifierNameSyntax target &&
                    runtime.Contains(target.Identifier.ValueText) && nesm.Contains(target.Identifier.ValueText) &&
                    !runtime.Members(target.Identifier.ValueText).Contains(text) &&
                    nesm.Members(target.Identifier.ValueText).Contains(text))
                {
                    return $"uses {target.Identifier.ValueText}.{text}, which only nesm's copy has";
                }

                continue;
            }

            if (name.Parent is QualifiedNameSyntax qualified && qualified.Right == name ||
                name.Parent is NameColonSyntax or NameEqualsSyntax or ArgumentSyntax { NameColon: not null } ||
                name.Parent is AssignmentExpressionSyntax { Parent: InitializerExpressionSyntax } assignment && assignment.Left == name)
            {
                continue;
            }

            if (IsUnimported(name, nesm, runtime))
                return $"uses {text}, which isn't imported";

            // A member of this class that has already been dropped.
            if (nesmOwnMembers.Contains(text) && !ownMembers.Contains(text) && !IsLocal(name, text))
                return $"uses {text}, which was dropped";
        }

        return null;
    }

    // A type that nesm declares but this repository doesn't, or a name qualified by a nesm namespace that isn't imported
    // (the renamer leaves those as Nesm.*).
    private static bool IsUnimported(SimpleNameSyntax name, TypeIndex nesm, TypeIndex runtime)
    {
        if (IsUnimportedNesmNamespace(name))
            return true;
        if (name.Parent is MemberAccessExpressionSyntax access && access.Name == name ||
            name.Parent is QualifiedNameSyntax qualified && qualified.Right == name ||
            name.Parent is NameColonSyntax or NameEqualsSyntax)
        {
            return false;
        }

        return nesm.Contains(name.Identifier.ValueText) && !runtime.Contains(name.Identifier.ValueText);
    }

    private static bool IsUnimportedNesmNamespace(SimpleNameSyntax name) =>
        name.Identifier.ValueText == "Nesm" && name.Parent is QualifiedNameSyntax or MemberAccessExpressionSyntax;

    private static bool IsAssertion(ExpressionStatementSyntax statement) =>
        statement.Expression is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "Assert" } } };

    // Whether a name refers to a local, parameter or lambda parameter rather than a member.
    private static bool IsLocal(SimpleNameSyntax name, string text)
    {
        foreach (SyntaxNode ancestor in name.Ancestors())
        {
            if (ancestor is MemberDeclarationSyntax member)
            {
                return member.DescendantNodes().Any(n => n switch
                {
                    ParameterSyntax p => p.Identifier.ValueText == text,
                    VariableDeclaratorSyntax v => v.Identifier.ValueText == text,
                    SingleVariableDesignationSyntax d => d.Identifier.ValueText == text,
                    ForEachStatementSyntax f => f.Identifier.ValueText == text,
                    LocalFunctionStatementSyntax l => l.Identifier.ValueText == text,
                    _ => false,
                });
            }
        }

        return false;
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

            if (key.Token.ValueText == "Requires" && s_unavailableRequirements.Contains(value.Token.ValueText))
                return $"requires {value.Token.ValueText}";
        }

        return null;
    }

    // Private members (and private nested types) that nothing in the class, its partial file or a string names.
    private static IEnumerable<(SyntaxNode Node, string Reason)> UnusedPrivateMembers(CompilationUnitSyntax unit, CompilationUnitSyntax? overlay, IEnumerable<SyntaxNode> dropping)
    {
        var droppingSet = dropping.ToHashSet();
        bool Live(SyntaxNode node) => !node.AncestorsAndSelf().Any(droppingSet.Contains);

        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (CompilationUnitSyntax source in overlay is null ? [unit] : new[] { unit, overlay })
        {
            foreach (SyntaxToken token in source.DescendantTokens())
            {
                if (token.IsKind(SyntaxKind.IdentifierToken) && token.Parent is SimpleNameSyntax && Live(token.Parent))
                    used.Add(token.ValueText);
                else if (token.IsKind(SyntaxKind.StringLiteralToken) && Live(token.Parent!))
                    used.Add(token.ValueText);
            }
        }

        foreach (TypeDeclarationSyntax type in unit.DescendantNodes().OfType<TypeDeclarationSyntax>())
        {
            foreach (MemberDeclarationSyntax member in type.Members)
            {
                if (droppingSet.Contains(member) || !IsPrivate(member) || member is ConstructorDeclarationSyntax)
                    continue;
                if (!TypeIndex.MemberNames(member).Any(used.Contains))
                    yield return (member, "is a private helper nothing uses any more");
            }
        }
    }

    private static bool IsPrivate(MemberDeclarationSyntax member) =>
        !member.Modifiers.Any(static m => m.Kind() is SyntaxKind.PublicKeyword or SyntaxKind.InternalKeyword or SyntaxKind.ProtectedKeyword) &&
        !member.Modifiers.Any(SyntaxKind.OverrideKeyword);

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

    private static CompilationUnitSyntax MapXunitV3(CompilationUnitSyntax unit)
    {
        // Methods that can skip: those that call Assert.Skip, and those that call a method of the same file that can.
        var skipping = new HashSet<string>(StringComparer.Ordinal);
        var methods = unit.DescendantNodes().OfType<MethodDeclarationSyntax>().ToList();
        for (bool added = true; added;)
        {
            added = false;
            foreach (MethodDeclarationSyntax method in methods)
            {
                if (!skipping.Contains(method.Identifier.ValueText) &&
                    method.DescendantNodes().Any(n => IsAssertSkip(n) || n is InvocationExpressionSyntax { Expression: IdentifierNameSyntax callee } && skipping.Contains(callee.Identifier.ValueText)))
                {
                    added |= skipping.Add(method.Identifier.ValueText);
                }
            }
        }

        bool converted = skipping.Count != 0;
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

/// <summary>Type names and their member names, merged across partial declarations and files.</summary>
internal sealed class TypeIndex
{
    private readonly Dictionary<string, HashSet<string>> _members = new(StringComparer.Ordinal);

    public static TypeIndex Build(IEnumerable<CompilationUnitSyntax> units)
    {
        var index = new TypeIndex();
        foreach (CompilationUnitSyntax unit in units)
        {
            foreach (BaseTypeDeclarationSyntax type in unit.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
                index.Add(type);
            foreach (DelegateDeclarationSyntax d in unit.DescendantNodes().OfType<DelegateDeclarationSyntax>())
                index.Members(d.Identifier.ValueText, create: true);
        }

        return index;
    }

    public bool Contains(string type) => _members.ContainsKey(type);

    public HashSet<string> Members(string type) => Members(type, create: false);

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

    private void Add(BaseTypeDeclarationSyntax type)
    {
        HashSet<string> members = Members(type.Identifier.ValueText, create: true);
        IEnumerable<MemberDeclarationSyntax> declared = type switch
        {
            TypeDeclarationSyntax t => t.Members,
            EnumDeclarationSyntax e => e.Members,
            _ => [],
        };
        foreach (MemberDeclarationSyntax member in declared)
            members.UnionWith(MemberNames(member));
        if (type is RecordDeclarationSyntax { ParameterList: { } parameters })
            members.UnionWith(parameters.Parameters.Select(static p => p.Identifier.ValueText));
    }

    private HashSet<string> Members(string type, bool create)
    {
        if (_members.TryGetValue(type, out HashSet<string>? members))
            return members;
        members = new HashSet<string>(StringComparer.Ordinal);
        if (create)
            _members[type] = members;
        return members;
    }
}
