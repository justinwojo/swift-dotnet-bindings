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
/// Roslyn analyzer that warns when a mutation is applied through a Swift-struct-typed property of a
/// Swift-backed object — <c>owner.StructProperty.Member = value</c>, or a call to a Swift
/// <c>mutating func</c> as <c>owner.StructProperty.Method(…)</c>. A Swift struct has value
/// semantics, so the generated getter hands back a fresh wrapper over a freshly copied native
/// buffer on every read. Mutating that wrapper compiles, runs, reports no error, and changes
/// nothing on the owner: the copy is discarded at the end of the statement. Because non-frozen
/// Swift structs project as C# <i>classes</i>, nothing in the C# type system hints that the
/// receiver is a temporary, which is what makes the no-op silent.
///
/// The fix is the copy-modify-write-back idiom, which the generated setter already supports:
/// read the property into a local, mutate the local, then assign the local back to the property.
/// Disposing the local (<c>using var</c>) returns its native buffer deterministically instead of
/// leaving it to the critical finalizer.
///
/// The analyzer is deliberately narrow to avoid false positives. It fires only when the receiver
/// of the mutation resolves to a <b>property</b> (not a local, parameter, or field — those are the
/// legitimate write-back idiom itself) that is not <c>ref</c>-returning, whose type implements
/// <c>Swift.Runtime.ISwiftStruct</c>, <b>and</b> whose containing type implements
/// <c>Swift.Runtime.ISwiftObject</c>.
///
/// Consequences of those tests worth stating plainly:
/// <list type="bullet">
/// <item>The containing-type test is a heuristic, not a proof of "generated". A consumer-authored
/// member on the same (partial) generated class that stores and returns a wrapper instance would be
/// flagged even though mutating through it is correct. Tightening it further — e.g. requiring the
/// declaring type to come from metadata — would go silent for SDK-direct consumption, where the
/// generated source is compiled into the consuming project itself. The heuristic is kept because
/// the shape it would misjudge is rare and the shape it catches is the common one.</item>
/// <item>A property reached through a generated <i>protocol interface</i> is not flagged: those
/// interfaces do not implement <c>ISwiftObject</c>, and they are explicitly consumer-implementable,
/// so a consumer's own implementation may legitimately return a stored wrapper. Accepting any
/// interface receiver would trade this false negative for a false positive on consumer code.</item>
/// <item>A <c>ref</c>- or <c>ref readonly</c>-returning property is never flagged: it hands back
/// the storage itself, so a write through it lands. The generator emits no such property, so the
/// shape is always consumer-authored — and for a frozen struct projection (a real C# struct) it is
/// the only spelling through which a member write even compiles.</item>
/// </list>
///
/// <para><b>Where the method-call arm stops.</b> A Swift <c>mutating func</c> and a plain one
/// project to the same C# method — no attribute, no naming difference, nothing in the public
/// surface distinguishes them — so "is this call a mutation?" cannot be answered from the
/// consumer's side. Rather than guess, the call arm fires only where a mutation, if the method
/// performs one, is certainly lost <i>and</i> the call cannot have been a read: the method's result
/// is unusable, meaning it returns <c>void</c>, or its value is discarded in statement position.
/// Two boundaries follow, and both are deliberate:</para>
/// <list type="bullet">
/// <item>False negative: a <c>mutating func</c> whose return value the caller consumes
/// (<c>var n = owner.Settings.Bump(1);</c>) is not flagged. Reporting it would equally hit every
/// ordinary read of a struct projection, which is the far commoner shape.</item>
/// <item>Residual false positive: a projected method that mutates nothing and is called for an
/// external effect only — firing a stored closure, say — is flagged if its result is unused,
/// because from C# it is indistinguishable from a lost mutation. Object overrides, <c>Dispose</c>,
/// and static methods are excluded, since none of them can be the lost-mutation shape.</item>
/// </list>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SwiftStructWriteBackAnalyzer : DiagnosticAnalyzer
{
    /// <summary>
    /// Diagnostic ID for a write through a Swift-struct-typed property that mutates a copy.
    /// </summary>
    public const string DiagnosticId = "SB1003";

    private static readonly LocalizableString Title =
        "Write through a Swift struct property mutates a temporary copy";

    private static readonly LocalizableString CallTitle =
        "Call through a Swift struct property mutates a temporary copy";

    private static readonly LocalizableString MessageFormat =
        "'{0}' is written on a copy: {1} returns a new copy on every read, so this write is " +
        "discarded. {2}";

    private static readonly LocalizableString CallMessageFormat =
        "'{0}' is called on a copy: {1} returns a new copy on every read, so any mutation the " +
        "call makes is discarded. {2}";

    private static readonly LocalizableString Description =
        "Swift structs have value semantics. A generated getter for a struct-typed property or " +
        "subscript returns a fresh wrapper over a fresh copy of the native buffer, so mutating a " +
        "member through it changes only that copy and is silently lost. Non-frozen Swift structs " +
        "project as C# classes, so the C# type system gives no hint that the receiver is a temporary. " +
        "Where a setter exists, use the copy-modify-write-back idiom and dispose the intermediate " +
        "copy so its native buffer is released deterministically instead of at finalization; where " +
        "none does, the owner cannot be updated through that member at all.";

    private const string Category = "Reliability";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        Title,
        MessageFormat,
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: Description);

    /// <summary>
    /// Same rule, same severity — a separate descriptor only because a lost call reads nothing like
    /// a lost assignment, and the message has to say which one the consumer is looking at.
    /// </summary>
    private static readonly DiagnosticDescriptor CallRule = new(
        DiagnosticId,
        CallTitle,
        CallMessageFormat,
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: Description);

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Rule, CallRule);

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        // Compound assignments (`owner.Settings.Count += 1`) lose the write exactly as a simple
        // assignment does — same node type, same discarded copy — so they register together.
        context.RegisterSyntaxNodeAction(
            AnalyzeAssignment,
            SyntaxKind.SimpleAssignmentExpression,
            SyntaxKind.AddAssignmentExpression,
            SyntaxKind.SubtractAssignmentExpression,
            SyntaxKind.MultiplyAssignmentExpression,
            SyntaxKind.DivideAssignmentExpression,
            SyntaxKind.ModuloAssignmentExpression,
            SyntaxKind.AndAssignmentExpression,
            SyntaxKind.OrAssignmentExpression,
            SyntaxKind.ExclusiveOrAssignmentExpression,
            SyntaxKind.LeftShiftAssignmentExpression,
            SyntaxKind.RightShiftAssignmentExpression,
            SyntaxKind.UnsignedRightShiftAssignmentExpression,
            SyntaxKind.CoalesceAssignmentExpression);

        // `owner.Settings.Count++` is the same lost write with no assignment node to hang off.
        context.RegisterSyntaxNodeAction(
            AnalyzeIncrementOrDecrement,
            SyntaxKind.PostIncrementExpression,
            SyntaxKind.PostDecrementExpression,
            SyntaxKind.PreIncrementExpression,
            SyntaxKind.PreDecrementExpression);

        // `owner.Settings.Bump(1)` — a Swift `mutating func` reached through the same copying
        // getter. There is no write node at all here; the mutation happens inside the callee.
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
    }

    private static void AnalyzeAssignment(SyntaxNodeAnalysisContext context)
    {
        var assignment = (AssignmentExpressionSyntax)context.Node;
        AnalyzeMutationTarget(context, assignment.Left);
    }

    private static void AnalyzeIncrementOrDecrement(SyntaxNodeAnalysisContext context)
    {
        var operand = context.Node switch
        {
            PostfixUnaryExpressionSyntax postfix => postfix.Operand,
            PrefixUnaryExpressionSyntax prefix => prefix.Operand,
            _ => null,
        };

        if (operand != null)
            AnalyzeMutationTarget(context, operand);
    }

    /// <summary>
    /// Reports when <paramref name="target"/> — the thing being written — reaches its storage
    /// through a copying Swift struct property. The diagnostic is placed on the whole statement's
    /// expression so the report covers the mutation, not just the member name.
    /// </summary>
    private static void AnalyzeMutationTarget(
        SyntaxNodeAnalysisContext context, ExpressionSyntax target)
    {
        var semanticModel = context.SemanticModel;

        // The target must reach through a receiver — a bare local/parameter/field write has no
        // intermediate to lose. Both `receiver.Member` and `receiver[index]` qualify: the indexer
        // setter runs on the copy exactly as a property setter does.
        target = Unwrap(target);
        var receiverExpr = GetReceiverOf(target);
        if (receiverExpr == null)
            return;

        // The written member must be state on the receiver. An event or a method group is not a
        // value write-back, and flagging one would only add noise.
        var writtenSymbol = semanticModel.GetSymbolInfo(target).Symbol;
        if (writtenSymbol is not IPropertySymbol && writtenSymbol is not IFieldSymbol)
            return;

        receiverExpr = Unwrap(receiverExpr);
        var receiverProperty = GetCopyingStructProperty(receiverExpr, semanticModel);
        if (receiverProperty == null)
            return;

        var mutation = target is ElementAccessExpressionSyntax
            ? "copy[…] = …"
            : $"copy.{writtenSymbol.Name} = …";

        context.ReportDiagnostic(Diagnostic.Create(
            Rule,
            context.Node.GetLocation(),
            writtenSymbol.Name,
            Describe(receiverProperty),
            BuildGuidance(receiverProperty, receiverExpr, mutation, semanticModel)));
    }

    /// <summary>
    /// Reports a call that reaches a Swift struct's own method through a copying getter. Nothing in
    /// the projected C# says whether the callee is a Swift <c>mutating func</c>, so the report is
    /// confined to calls that cannot have been reads: the result is <c>void</c>, or it is thrown
    /// away in statement position. A call whose value is consumed is left alone.
    /// </summary>
    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        var semanticModel = context.SemanticModel;

        if (semanticModel.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method)
            return;

        if (!IsProjectedStructMethod(method))
            return;

        if (!method.ReturnsVoid && !IsResultDiscarded(invocation))
            return;

        var receiverExpr = GetInvocationReceiver(invocation);
        if (receiverExpr == null)
            return;

        receiverExpr = Unwrap(receiverExpr);
        var receiverProperty = GetCopyingStructProperty(receiverExpr, semanticModel);
        if (receiverProperty == null)
            return;

        context.ReportDiagnostic(Diagnostic.Create(
            CallRule,
            (GetOwningConditionalAccess(invocation) ?? (SyntaxNode)invocation).GetLocation(),
            method.Name,
            Describe(receiverProperty),
            BuildGuidance(
                receiverProperty, receiverExpr, $"copy.{method.Name}(…)", semanticModel)));
    }

    /// <summary>
    /// Whether <paramref name="method"/> could be a Swift <c>mutating func</c> projected onto a
    /// struct wrapper. Statics have no receiver to lose, and the members every generated struct
    /// carries regardless of its Swift source — the <c>System.Object</c> overrides and
    /// <c>Dispose</c> — mutate nothing; disposing the copy is in fact the right thing to do with
    /// it. What survives is an ordinary instance method declared on a struct projection, which is
    /// the same containing-type heuristic the property side uses.
    /// </summary>
    private static bool IsProjectedStructMethod(IMethodSymbol method)
    {
        if (method.IsStatic || method.IsOverride || method.MethodKind != MethodKind.Ordinary)
            return false;

        if (method.Name == "Dispose" && method.Parameters.Length == 0 && method.ReturnsVoid)
            return false;

        return ImplementsSwiftInterface(method.ContainingType, "ISwiftStruct");
    }

    /// <summary>
    /// Returns the expression the call runs on. Beyond the member and element accesses a write can
    /// also reach through, a call has one spelling of its own: <c>owner.Settings?.Bump(1)</c> hangs
    /// the call off a member binding, and the receiver is the conditional access's own left side.
    /// An optional-typed struct property is exactly where a consumer reaches for <c>?.</c>, and the
    /// copy is lost there just the same.
    /// </summary>
    private static ExpressionSyntax? GetInvocationReceiver(InvocationExpressionSyntax invocation)
    {
        var callee = Unwrap(invocation.Expression);

        return callee is MemberBindingExpressionSyntax
            ? GetOwningConditionalAccess(invocation)?.Expression
            : GetReceiverOf(callee);
    }

    /// <summary>
    /// The conditional access an invocation is the tested right-hand side of, if any — the node that
    /// spells the whole <c>owner.Settings?.Bump(1)</c> the consumer wrote, rather than the bare
    /// <c>?.Bump(1)</c> the invocation node covers.
    /// </summary>
    private static ConditionalAccessExpressionSyntax? GetOwningConditionalAccess(
        InvocationExpressionSyntax invocation) =>
        invocation.Parent is ConditionalAccessExpressionSyntax conditional
            && conditional.WhenNotNull == invocation
            ? conditional
            : null;

    /// <summary>
    /// Whether the invocation's value goes nowhere — it sits in statement position, possibly behind
    /// the same wrappers a receiver can hide behind, behind an <c>await</c>, or behind the
    /// conditional access it is the right-hand side of.
    /// </summary>
    private static bool IsResultDiscarded(ExpressionSyntax invocation)
    {
        SyntaxNode node = invocation;
        while (node.Parent is ParenthesizedExpressionSyntax
            || node.Parent is AwaitExpressionSyntax
            || (node.Parent is PostfixUnaryExpressionSyntax suppression
                && suppression.IsKind(SyntaxKind.SuppressNullableWarningExpression))
            || (node.Parent is ConditionalAccessExpressionSyntax conditional
                && conditional.WhenNotNull == node))
        {
            node = node.Parent;
        }

        return node.Parent is ExpressionStatementSyntax;
    }

    /// <summary>
    /// Returns the expression whose value <paramref name="expression"/> reaches into — the receiver
    /// of a member access or of an element access — or null when it names storage directly.
    /// </summary>
    private static ExpressionSyntax? GetReceiverOf(ExpressionSyntax expression) => expression switch
    {
        MemberAccessExpressionSyntax memberAccess
            when memberAccess.IsKind(SyntaxKind.SimpleMemberAccessExpression) => memberAccess.Expression,
        ElementAccessExpressionSyntax elementAccess => elementAccess.Expression,
        _ => null,
    };

    /// <summary>
    /// Names the copying member for the diagnostic message. An indexer's symbol name is
    /// <c>this[]</c>, which reads as nonsense in prose, so a subscript is described by its
    /// declaring type instead.
    /// </summary>
    private static string Describe(IPropertySymbol receiverProperty) =>
        receiverProperty.IsIndexer
            ? $"the subscript on the Swift struct '{receiverProperty.ContainingType.Name}'"
            : $"the Swift struct property '{receiverProperty.Name}'";

    /// <summary>
    /// Builds the remedy sentence. The plain write-back recipe is only correct for a single-level
    /// copy through a settable property, so the three shapes where it would mislead get their own
    /// wording: a member with no setter (the suggested write-back would not compile), a subscript
    /// (there is no <c>….Name</c> spelling, and the value it was read from is a copy too), and a
    /// chain of copying properties (writing back one link still mutates a copy of the link above).
    /// </summary>
    private static string BuildGuidance(
        IPropertySymbol receiverProperty,
        ExpressionSyntax receiverExpr,
        string mutation,
        SemanticModel semanticModel)
    {
        if (receiverProperty.SetMethod == null)
        {
            return "It has no setter, so there is no way to write the modified copy back; the " +
                "owner cannot be updated through it at all.";
        }

        if (receiverProperty.IsIndexer)
        {
            return "The element it hands back is a copy, and so is the value the subscript was " +
                "read from: assign the mutated element back through the same subscript, then " +
                "assign that value back to its own owner.";
        }

        var outerReceiver = GetReceiverOf(receiverExpr);
        if (outerReceiver != null &&
            GetCopyingStructProperty(Unwrap(outerReceiver), semanticModel) != null)
        {
            return "It is itself read from a copying struct member, so every link in the chain " +
                "copies: read each link into its own local, mutate the innermost one, then assign " +
                "the locals back outward one level at a time — and if any outer link has no " +
                "setter, the write cannot reach the owner at all.";
        }

        return "Read it into a local, mutate the local, then assign the local back: " +
            $"'using var copy = ….{receiverProperty.Name}; {mutation}; " +
            $"….{receiverProperty.Name} = copy;'.";
    }

    /// <summary>
    /// Returns the receiver's property symbol when <paramref name="receiverExpr"/> resolves to a
    /// property of a Swift-backed type whose own type is a Swift-struct projection — i.e. a getter
    /// that hands back a copy. Returns null for locals, parameters, fields, method calls, and
    /// properties declared on ordinary C# types, none of which are known to copy.
    /// </summary>
    private static IPropertySymbol? GetCopyingStructProperty(
        ExpressionSyntax receiverExpr, SemanticModel semanticModel)
    {
        if (semanticModel.GetSymbolInfo(receiverExpr).Symbol is not IPropertySymbol property)
            return null;

        // A ref return hands back the storage, not a copy of it, so the mutation lands. No
        // generated property returns by ref, so this can only be consumer-authored code.
        if (property.ReturnsByRef || property.ReturnsByRefReadonly)
            return null;

        if (!ImplementsSwiftInterface(property.Type, "ISwiftStruct"))
            return null;

        // Only a generated binding property is known to copy on read. A hand-written C# property of
        // the same struct-projected type may simply return a stored wrapper instance, where mutating
        // through it is correct — so require the declaring type to be Swift-backed too.
        var containingType = property.ContainingType;
        return containingType != null && ImplementsSwiftInterface(containingType, "ISwiftObject")
            ? property
            : null;
    }

    /// <summary>
    /// Strips the syntax that wraps an expression without changing which storage it names —
    /// parentheses, a cast, and the null-forgiving <c>!</c>. All three are common around a property
    /// receiver and none of them stop the getter from copying.
    /// </summary>
    private static ExpressionSyntax Unwrap(ExpressionSyntax expression)
    {
        while (true)
        {
            switch (expression)
            {
                case ParenthesizedExpressionSyntax parenthesized:
                    expression = parenthesized.Expression;
                    continue;
                case CastExpressionSyntax cast:
                    expression = cast.Expression;
                    continue;
                case PostfixUnaryExpressionSyntax suppression
                    when suppression.IsKind(SyntaxKind.SuppressNullableWarningExpression):
                    expression = suppression.Operand;
                    continue;
                default:
                    return expression;
            }
        }
    }

    /// <summary>
    /// Checks whether a type is, or implements, the named marker interface in
    /// <c>Swift.Runtime</c>.
    /// </summary>
    private static bool ImplementsSwiftInterface(ITypeSymbol type, string interfaceName)
    {
        return CheckTypeForInterface(type, interfaceName)
            || type.AllInterfaces.Any(i => CheckTypeForInterface(i, interfaceName));
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
