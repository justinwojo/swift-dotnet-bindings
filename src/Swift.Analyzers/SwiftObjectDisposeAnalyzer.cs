// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Swift.Analyzers;

/// <summary>
/// Roslyn analyzer that warns when an ISwiftObject local variable is not disposed.
/// Swift objects hold native handles that must be released deterministically.
///
/// This analyzer uses syntax-level heuristics, not control-flow graph (CFG) or dataflow
/// analysis. It recognizes <c>using</c> declarations, unconditional <c>Dispose()</c> calls
/// in the same block, <c>try/finally</c> Dispose patterns, and direct <c>return</c> of
/// the variable. Conditional disposal (e.g., inside <c>if</c>/<c>switch</c>) is intentionally
/// treated as undisposed. Complex ownership transfers (storing into a field, passing to a
/// method that takes ownership, or disposal in a called helper) are not tracked and may
/// produce false positives. This is by design — the analyzer provides lightweight guidance
/// to catch the most common leak pattern (forgetting <c>using</c>) rather than attempting
/// full lifetime analysis.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SwiftObjectDisposeAnalyzer : DiagnosticAnalyzer
{
    /// <summary>
    /// Diagnostic ID for undisposed ISwiftObject locals.
    /// </summary>
    public const string DiagnosticId = "SB1001";

    private static readonly LocalizableString Title =
        "ISwiftObject can benefit from deterministic disposal";

    private static readonly LocalizableString MessageFormat =
        "ISwiftObject '{0}' is not disposed. Consider using 'using' declaration, SwiftDisposeScope, or Dispose() for deterministic cleanup.";

    private static readonly LocalizableString Description =
        "Swift objects hold native handles that benefit from deterministic disposal. " +
        "The GC finalizer provides safe cleanup on all runtimes (Mono and NativeAOT) " +
        "via the Cdecl VWT Destroy trampoline, so disposal is never required for correctness. " +
        "Use 'using' for deterministic cleanup of scarce resources.";

    private const string Category = "Usage";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        Title,
        MessageFormat,
        Category,
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: Description);

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Rule);

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeLocalDeclaration, SyntaxKind.LocalDeclarationStatement);
    }

    private static void AnalyzeLocalDeclaration(SyntaxNodeAnalysisContext context)
    {
        var localDeclaration = (LocalDeclarationStatementSyntax)context.Node;

        // Already a using declaration — no diagnostic needed
        if (localDeclaration.UsingKeyword != default)
            return;

        // Inside a using statement — no diagnostic needed
        if (localDeclaration.Parent is UsingStatementSyntax)
            return;

        // Inside a SwiftDisposeScope using block — objects are automatically tracked
        if (IsInsideSwiftDisposeScope(localDeclaration, context))
            return;

        var declaration = localDeclaration.Declaration;

        foreach (var variable in declaration.Variables)
        {
            if (variable.Initializer == null)
                continue;

            ITypeSymbol? type = null;

            // Try to get the type from the declared symbol first
            var declaredSymbol = context.SemanticModel.GetDeclaredSymbol(variable);
            if (declaredSymbol is ILocalSymbol localSymbol)
            {
                type = localSymbol.Type;
            }

            if (type == null)
            {
                // Fall back to the type info of the initializer expression
                var typeInfo = context.SemanticModel.GetTypeInfo(variable.Initializer.Value);
                type = typeInfo.Type;
            }

            if (type == null)
                continue;

            if (!ImplementsISwiftObject(type))
                continue;

            var variableName = variable.Identifier.Text;

            // Check if Dispose() is called on this variable in the enclosing block
            if (IsDisposedInScope(variableName, localDeclaration))
                continue;

            // Check if the value is returned from the method (ownership transfer)
            if (IsReturnedFromMethod(variableName, localDeclaration))
                continue;

            var diagnostic = Diagnostic.Create(
                Rule,
                variable.GetLocation(),
                variableName);

            context.ReportDiagnostic(diagnostic);
        }
    }

    /// <summary>
    /// Checks whether a type implements Swift.Runtime.ISwiftObject via any of its interfaces.
    /// </summary>
    private static bool ImplementsISwiftObject(ITypeSymbol type)
    {
        return CheckTypeForInterface(type, "ISwiftObject") || type.AllInterfaces.Any(i => CheckTypeForInterface(i, "ISwiftObject"));
    }

    private static bool CheckTypeForInterface(ITypeSymbol type, string interfaceName)
    {
        if (type.Name != interfaceName)
            return false;

        var ns = type.ContainingNamespace;
        if (ns == null || ns.Name != "Runtime")
            return false;

        ns = ns.ContainingNamespace;
        if (ns == null || ns.Name != "Swift")
            return false;

        ns = ns.ContainingNamespace;
        return ns != null && ns.IsGlobalNamespace;
    }

    /// <summary>
    /// Scans the enclosing block for an unconditional .Dispose() call on the given variable.
    /// Only top-level statements in the same block and finally blocks are considered
    /// unconditional. Dispose calls inside if/for/while/etc. are conditional and do not
    /// suppress the diagnostic.
    /// </summary>
    private static bool IsDisposedInScope(string variableName, LocalDeclarationStatementSyntax declaration)
    {
        var block = declaration.Parent as BlockSyntax;
        if (block == null)
            return false;

        // Only look at statements after the declaration
        bool pastDeclaration = false;
        foreach (var statement in block.Statements)
        {
            if (statement == declaration)
            {
                pastDeclaration = true;
                continue;
            }

            if (!pastDeclaration)
                continue;

            // Direct x.Dispose() statement in the same block (unconditional)
            if (IsDisposeCall(statement, variableName))
                return true;

            // try { ... } finally { x.Dispose(); } — finally is guaranteed
            if (statement is TryStatementSyntax tryStatement &&
                tryStatement.Finally?.Block is BlockSyntax finallyBlock &&
                finallyBlock.Statements.Any(s => IsDisposeCall(s, variableName)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Checks whether a statement is an expression statement calling variableName.Dispose().
    /// </summary>
    private static bool IsDisposeCall(StatementSyntax statement, string variableName)
    {
        return statement is ExpressionStatementSyntax expressionStatement &&
               expressionStatement.Expression is InvocationExpressionSyntax invocation &&
               invocation.Expression is MemberAccessExpressionSyntax memberAccess &&
               memberAccess.Name.Identifier.Text == "Dispose" &&
               memberAccess.Expression is IdentifierNameSyntax identifier &&
               identifier.Identifier.Text == variableName;
    }

    /// <summary>
    /// Checks whether the declaration is inside a SwiftDisposeScope using block,
    /// either a using declaration preceding it in the same block or an enclosing using statement.
    /// </summary>
    private static bool IsInsideSwiftDisposeScope(
        LocalDeclarationStatementSyntax declaration,
        SyntaxNodeAnalysisContext context)
    {
        // Walk up the tree checking both using declarations in ancestor blocks
        // and enclosing using statements. This handles nested blocks like:
        //   using var scope = new SwiftDisposeScope();
        //   if (cond) { var x = new FooProxy(); } // x is inside the scope
        SyntaxNode? child = declaration;
        for (SyntaxNode? node = declaration.Parent; node != null; node = node.Parent)
        {
            // Check using (new SwiftDisposeScope()) { ... } enclosing this statement
            if (node is UsingStatementSyntax usingStatement)
            {
                if (usingStatement.Expression != null)
                {
                    var typeInfo = context.SemanticModel.GetTypeInfo(usingStatement.Expression);
                    if (typeInfo.Type != null && IsSwiftDisposeScopeType(typeInfo.Type))
                        return true;
                }

                if (usingStatement.Declaration != null &&
                    HasSwiftDisposeScopeInitializer(usingStatement.Declaration, context.SemanticModel))
                {
                    return true;
                }
            }

            // Check using declarations in ancestor blocks that appear before the child node
            if (node is BlockSyntax block)
            {
                foreach (var statement in block.Statements)
                {
                    // Only check statements before the child (which contains our declaration)
                    if (statement == child || statement.Span.Start >= child.Span.Start)
                        break;

                    if (statement is LocalDeclarationStatementSyntax localDecl &&
                        localDecl.UsingKeyword != default &&
                        HasSwiftDisposeScopeInitializer(localDecl.Declaration, context.SemanticModel))
                    {
                        return true;
                    }
                }
            }

            child = node;
        }

        return false;
    }

    private static bool HasSwiftDisposeScopeInitializer(VariableDeclarationSyntax declaration, SemanticModel semanticModel)
    {
        foreach (var variable in declaration.Variables)
        {
            if (variable.Initializer?.Value != null)
            {
                var typeInfo = semanticModel.GetTypeInfo(variable.Initializer.Value);
                if (typeInfo.Type != null && IsSwiftDisposeScopeType(typeInfo.Type))
                    return true;
            }
        }
        return false;
    }

    private static bool IsSwiftDisposeScopeType(ITypeSymbol type)
    {
        if (type.Name != "SwiftDisposeScope")
            return false;

        var ns = type.ContainingNamespace;
        if (ns == null || ns.Name != "Runtime")
            return false;

        ns = ns.ContainingNamespace;
        if (ns == null || ns.Name != "Swift")
            return false;

        ns = ns.ContainingNamespace;
        return ns != null && ns.IsGlobalNamespace;
    }

    /// <summary>
    /// Checks whether the variable is returned from the enclosing method (ownership transfer).
    /// </summary>
    private static bool IsReturnedFromMethod(string variableName, LocalDeclarationStatementSyntax declaration)
    {
        var block = declaration.Parent as BlockSyntax;
        if (block == null)
            return false;

        foreach (var statement in block.Statements)
        {
            if (statement is ReturnStatementSyntax returnStatement &&
                returnStatement.Expression is IdentifierNameSyntax identifier &&
                identifier.Identifier.Text == variableName)
            {
                return true;
            }
        }

        return false;
    }
}
