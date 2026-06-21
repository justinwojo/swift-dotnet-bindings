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
/// Roslyn analyzer that warns about a cross-heap retain cycle created when a callback handed to a
/// Swift-backed object captures that same object. Two shapes form the same cycle: handing the
/// callback to an instance method (<c>obj.SetCallback(() =&gt; obj.Method())</c>) and assigning it to a
/// stored callback property (<c>obj.Handler = () =&gt; obj.Method()</c>). The second is the dominant
/// shape for a Swift binding, because a Swift stored closure property projects to a C# property
/// setter. In both, the Swift object stores a C# delegate, the delegate holds a strong reference back
/// to the object, and the Swift-side GCHandle root plus the managed strong reference keep each other
/// alive — neither heap can collect the pair. The cycle is unbreakable from the runtime side, so the
/// only fix is at the capture: reach the object through a
/// <c>Swift.Runtime.WeakSwiftReference&lt;T&gt;</c> instead of capturing it strongly.
///
/// This analyzer uses semantic (symbol-identity) analysis, deliberately narrow: it fires only when the
/// receiver is a local, parameter, or field whose type implements <c>Swift.Runtime.ISwiftObject</c>,
/// the member is an instance method call or a property/field assignment, and a lambda /
/// anonymous-method argument or right-hand side lexically references that same receiver symbol (a
/// guaranteed capture, since the receiver is evaluated outside the lambda). It cannot know whether the
/// callee actually <i>stores</i> the delegate, so a synchronously-invoked callback (e.g. a
/// <c>ForEach</c>-style call) is a possible false positive — by design, this is lightweight guidance
/// toward <c>WeakSwiftReference&lt;T&gt;</c>, not a proof of a leak.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SwiftRetainCycleAnalyzer : DiagnosticAnalyzer
{
    /// <summary>
    /// Diagnostic ID for a self-capturing callback on a Swift-backed object.
    /// </summary>
    public const string DiagnosticId = "SB1002";

    private static readonly LocalizableString Title =
        "Callback captures the Swift object it is attached to (possible retain cycle)";

    private static readonly LocalizableString MessageFormat =
        "Callback passed to '{0}' captures '{1}', the Swift-backed object it is attached to. " +
        "If the callback is stored, this creates an unbreakable cross-heap retain cycle. " +
        "Capture a Swift.Runtime.WeakSwiftReference<T> and reach the object through its Target instead.";

    private static readonly LocalizableString Description =
        "A Swift-backed object (ISwiftObject) that stores a C# callback which strongly captures the " +
        "same object forms a retain cycle the runtime cannot break: the managed delegate roots the " +
        "object and the object's Swift-side handle roots the delegate. Break the C# leg by capturing a " +
        "WeakSwiftReference<T> and dereferencing it (weak.Target?.Method()) inside the callback. The " +
        "analyzer cannot tell a stored callback from one invoked synchronously, so it may flag a call " +
        "that does not actually leak.";

    private const string Category = "Reliability";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        Title,
        MessageFormat,
        Category,
        DiagnosticSeverity.Warning,
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
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
        context.RegisterSyntaxNodeAction(AnalyzeAssignment, SyntaxKind.SimpleAssignmentExpression);
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;

        // Must be a member-access call so there is an instance receiver: receiver.Method(...).
        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
            return;

        var receiverSymbol = GetSwiftObjectReceiver(memberAccess.Expression, context.SemanticModel);
        if (receiverSymbol == null)
            return;

        // The called member must be an instance method (a stored-callback setter is an instance call).
        if (context.SemanticModel.GetSymbolInfo(invocation).Symbol is not IMethodSymbol calledMethod ||
            calledMethod.IsStatic)
            return;

        if (invocation.ArgumentList == null)
            return;

        foreach (var argument in invocation.ArgumentList.Arguments)
        {
            if (TryReportSelfCapture(context, argument.Expression, receiverSymbol, calledMethod.Name))
            {
                // One diagnostic per call site is enough even if several arguments capture.
                return;
            }
        }
    }

    /// <summary>
    /// Flags the stored-callback-property shape: <c>receiver.Handler = () =&gt; receiver.Method()</c>.
    /// A Swift stored closure property projects to a C# property setter, so this assignment — not a
    /// method call — is the dominant form of the self-capture cycle in generated bindings.
    /// </summary>
    private static void AnalyzeAssignment(SyntaxNodeAnalysisContext context)
    {
        var assignment = (AssignmentExpressionSyntax)context.Node;

        // The assignment target must be a member access onto an instance: receiver.Member = ...
        if (assignment.Left is not MemberAccessExpressionSyntax memberAccess)
            return;

        var receiverSymbol = GetSwiftObjectReceiver(memberAccess.Expression, context.SemanticModel);
        if (receiverSymbol == null)
            return;

        // The assigned member must be an instance property or field that stores the delegate.
        var assignedSymbol = context.SemanticModel.GetSymbolInfo(assignment.Left).Symbol;
        bool isInstanceStore = assignedSymbol switch
        {
            IPropertySymbol p => !p.IsStatic,
            IFieldSymbol f => !f.IsStatic,
            IEventSymbol e => !e.IsStatic,
            _ => false
        };
        if (!isInstanceStore)
            return;

        TryReportSelfCapture(context, assignment.Right, receiverSymbol, assignedSymbol!.Name);
    }

    /// <summary>
    /// Returns the receiver symbol when <paramref name="receiverExpr"/> resolves to a local, parameter,
    /// or field whose type implements <c>Swift.Runtime.ISwiftObject</c>; otherwise null.
    /// </summary>
    private static ISymbol? GetSwiftObjectReceiver(ExpressionSyntax receiverExpr, SemanticModel semanticModel)
    {
        var receiverSymbol = semanticModel.GetSymbolInfo(receiverExpr).Symbol;
        var receiverType = receiverSymbol switch
        {
            ILocalSymbol local => local.Type,
            IParameterSymbol param => param.Type,
            IFieldSymbol field => field.Type,
            _ => null
        };
        if (receiverSymbol == null || receiverType == null)
            return null;
        return ImplementsISwiftObject(receiverType) ? receiverSymbol : null;
    }

    /// <summary>
    /// Reports SB1002 when <paramref name="candidate"/> is a non-static lambda / anonymous method that
    /// captures <paramref name="receiverSymbol"/>. Returns true when a diagnostic was reported.
    /// </summary>
    private static bool TryReportSelfCapture(
        SyntaxNodeAnalysisContext context,
        ExpressionSyntax candidate,
        ISymbol receiverSymbol,
        string memberName)
    {
        // Unwrap redundant parentheses and an explicit delegate cast so a lambda written as
        // `(Action)(() => …)` or `(() => …)` is recognized as the same cycle — the inner lambda
        // still has to capture the receiver below, so peeling these wrappers can only surface a real
        // capture, never invent one.
        while (true)
        {
            switch (candidate)
            {
                case ParenthesizedExpressionSyntax paren:
                    candidate = paren.Expression;
                    continue;
                case CastExpressionSyntax cast:
                    candidate = cast.Expression;
                    continue;
            }
            break;
        }

        if (candidate is not AnonymousFunctionExpressionSyntax lambda)
            return false;

        // A `static` lambda cannot capture anything, so it cannot form the cycle.
        if (lambda.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)))
            return false;

        var body = (SyntaxNode?)lambda.Body ?? lambda.ExpressionBody;
        if (body == null)
            return false;

        if (!CapturesSymbol(body, receiverSymbol, context.SemanticModel))
            return false;

        var diagnostic = Diagnostic.Create(
            Rule,
            lambda.GetLocation(),
            memberName,
            receiverSymbol.Name);
        context.ReportDiagnostic(diagnostic);
        return true;
    }

    /// <summary>
    /// Returns true when <paramref name="body"/> lexically references <paramref name="target"/>. Because
    /// the lambda body is nested inside the call whose receiver is <paramref name="target"/>, any such
    /// reference is necessarily a closure capture.
    /// </summary>
    private static bool CapturesSymbol(SyntaxNode body, ISymbol target, SemanticModel semanticModel)
    {
        foreach (var name in body.DescendantNodesAndSelf().OfType<SimpleNameSyntax>())
        {
            var symbol = semanticModel.GetSymbolInfo(name).Symbol;
            if (symbol != null && SymbolEqualityComparer.Default.Equals(symbol, target))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Checks whether a type implements Swift.Runtime.ISwiftObject (directly or via an interface).
    /// </summary>
    private static bool ImplementsISwiftObject(ITypeSymbol type)
    {
        return CheckTypeForInterface(type, "ISwiftObject")
            || type.AllInterfaces.Any(i => CheckTypeForInterface(i, "ISwiftObject"));
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
}
