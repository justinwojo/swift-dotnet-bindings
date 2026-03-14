// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Immutable;
using System.Composition;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Swift.Analyzers;

/// <summary>
/// Code fix provider that adds a 'using' modifier to undisposed ISwiftObject local declarations.
/// Transforms <c>var x = new Foo()</c> into <c>using var x = new Foo()</c>.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(SwiftObjectDisposeCodeFixProvider))]
[Shared]
public sealed class SwiftObjectDisposeCodeFixProvider : CodeFixProvider
{
    private const string Title = "Add 'using' declaration";
    private const string DisposeScopeTitle = "Wrap in SwiftDisposeScope";

    /// <inheritdoc/>
    public sealed override ImmutableArray<string> FixableDiagnosticIds =>
        ImmutableArray.Create(SwiftObjectDisposeAnalyzer.DiagnosticId);

    /// <inheritdoc/>
    public sealed override FixAllProvider GetFixAllProvider() =>
        WellKnownFixAllProviders.BatchFixer;

    /// <inheritdoc/>
    public sealed override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root == null)
            return;

        var diagnostic = context.Diagnostics[0];
        var diagnosticSpan = diagnostic.Location.SourceSpan;

        var node = root.FindNode(diagnosticSpan);

        // Walk up to find the LocalDeclarationStatementSyntax
        var localDeclaration = node.FirstAncestorOrSelf<LocalDeclarationStatementSyntax>();
        if (localDeclaration == null)
            return;

        context.RegisterCodeFix(
            CodeAction.Create(
                title: Title,
                createChangedDocument: ct => AddUsingModifierAsync(context.Document, localDeclaration, ct),
                equivalenceKey: "AddUsingDeclaration"),
            diagnostic);

        context.RegisterCodeFix(
            CodeAction.Create(
                title: DisposeScopeTitle,
                createChangedDocument: ct => WrapInDisposeScopeAsync(context.Document, localDeclaration, ct),
                equivalenceKey: "WrapInSwiftDisposeScope"),
            diagnostic);
    }

    private static async Task<Document> WrapInDisposeScopeAsync(
        Document document,
        LocalDeclarationStatementSyntax localDeclaration,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root == null)
            return document;

        var block = localDeclaration.Parent as BlockSyntax;
        if (block == null)
            return document;

        // Get indentation from the diagnosed statement
        var leadingTrivia = localDeclaration.GetLeadingTrivia();

        var scopeStatement = SyntaxFactory.ParseStatement("using var _ = new Swift.Runtime.SwiftDisposeScope();")
            .WithLeadingTrivia(leadingTrivia)
            .WithTrailingTrivia(SyntaxFactory.EndOfLine("\n"));

        var idx = block.Statements.IndexOf(localDeclaration);
        var newStatements = block.Statements.Insert(idx, scopeStatement);
        var newBlock = block.WithStatements(newStatements);

        var newRoot = root.ReplaceNode(block, newBlock);
        return document.WithSyntaxRoot(newRoot);
    }

    private static async Task<Document> AddUsingModifierAsync(
        Document document,
        LocalDeclarationStatementSyntax localDeclaration,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root == null)
            return document;

        // Move leading trivia (indentation) from the declaration to the 'using' keyword,
        // then strip it from the original first token so it doesn't appear twice.
        var leadingTrivia = localDeclaration.GetLeadingTrivia();

        var usingKeyword = SyntaxFactory.Token(SyntaxKind.UsingKeyword)
            .WithLeadingTrivia(leadingTrivia)
            .WithTrailingTrivia(SyntaxFactory.Space);

        // Strip the original leading trivia from the declaration so it won't duplicate
        var strippedDeclaration = localDeclaration.WithLeadingTrivia(SyntaxTriviaList.Empty);

        var newDeclaration = strippedDeclaration.WithUsingKeyword(usingKeyword);

        var newRoot = root.ReplaceNode(localDeclaration, newDeclaration);
        return document.WithSyntaxRoot(newRoot);
    }
}
