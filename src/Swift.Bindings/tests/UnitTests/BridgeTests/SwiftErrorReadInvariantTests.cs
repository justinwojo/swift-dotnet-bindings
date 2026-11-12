// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;
using static BindingsGeneration.Tests.SwiftCallPlanTests;

namespace BindingsGeneration.Tests;

/// <summary>
/// The SwiftError-read invariant (dotnet/runtime#136523): the JIT crashes compiling a method in which
/// a <c>SwiftError</c> local passed to a CallConvSwift call is never read afterwards, and .NET 11 GA
/// still does. So every call site the generator emits that passes a SwiftError to a Swift-convention
/// import reads that error after the call. A transition routed through a bridge keeps the import's
/// name and signature in its shim, so its callers are unchanged and must still read it; the
/// invariant covers both the direct imports and the bridge shims.
/// </summary>
public sealed class SwiftErrorReadInvariantTests(Xunit.Abstractions.ITestOutputHelper log)
{
    /// <summary>One call that passes a SwiftError: the callee, whether it is a bridge shim, the error variable, and whether it is read after the call.</summary>
    public sealed record Site(string File, int Line, string Callee, bool Bridged, string? Variable, bool Read)
    {
        public override string ToString() =>
            $"{File}:{Line}: {Callee}{(Bridged ? " (bridged)" : "")} passes {(Variable is null ? "an error that is not a local" : Variable)}" +
            (Read ? "" : ", which is never read after the call");
    }

    private static bool IsSwiftError(TypeSyntax? type)
    {
        while (type is PointerTypeSyntax pointer)
            type = pointer.ElementType;
        var text = type?.ToString();
        return text is not null && (text == "SwiftError" || text.EndsWith(".SwiftError", StringComparison.Ordinal));
    }

    private static bool IsSwiftConventionImport(MethodDeclarationSyntax method) =>
        method.Body is null && method.ExpressionBody is null
        && method.AttributeLists.SelectMany(l => l.Attributes).Any(a => a.ToString().Contains("CallConvSwift", StringComparison.Ordinal));

    private static bool IsBridgeShim(MethodDeclarationSyntax method) =>
        ((SyntaxNode?)method.Body ?? method.ExpressionBody)?.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Any(i => i.Expression.ToString().Contains("__SBSR_", StringComparison.Ordinal)) == true;

    private static string? InvokedName(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        IdentifierNameSyntax id => id.Identifier.ValueText,
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        _ => null,
    };

    /// <summary>The local an argument passes the error through: <c>&amp;x</c>, <c>ref x</c>, <c>out x</c> or <c>out var x</c>.</summary>
    private static string? ErrorVariable(ArgumentSyntax argument) => argument.Expression switch
    {
        PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.AddressOfExpression, Operand: IdentifierNameSyntax id } => id.Identifier.ValueText,
        IdentifierNameSyntax id when !argument.RefKindKeyword.IsKind(SyntaxKind.None) => id.Identifier.ValueText,
        DeclarationExpressionSyntax { Designation: SingleVariableDesignationSyntax d } => d.Identifier.ValueText,
        _ => null,
    };

    private static SyntaxNode Scope(SyntaxNode node) =>
        node.Ancestors().First(a => a is BaseMethodDeclarationSyntax or AccessorDeclarationSyntax or LocalFunctionStatementSyntax
            or AnonymousFunctionExpressionSyntax or CompilationUnitSyntax);

    private static bool IsWrite(IdentifierNameSyntax id) =>
        id.Parent is AssignmentExpressionSyntax assignment && assignment.Left == id;

    /// <summary>Every call in <paramref name="source"/> that passes a SwiftError to a Swift-convention import or a bridge shim declared in it.</summary>
    public static IEnumerable<Site> Sites(string file, string source)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var callees = new Dictionary<string, (int Index, bool Bridged)>(StringComparer.Ordinal);
        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            var index = method.ParameterList.Parameters.IndexOf(p => IsSwiftError(p.Type));
            if (index < 0)
                continue;
            if (IsSwiftConventionImport(method))
                callees[method.Identifier.ValueText] = (index, false);
            else if (IsBridgeShim(method))
                callees[method.Identifier.ValueText] = (index, true);
        }
        if (callees.Count == 0)
            yield break;

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (InvokedName(invocation) is not { } name || !callees.TryGetValue(name, out var callee))
                continue;
            var line = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            var arguments = invocation.ArgumentList.Arguments;
            var variable = callee.Index < arguments.Count && arguments.All(a => a.NameColon is null) ? ErrorVariable(arguments[callee.Index]) : null;
            var read = variable is not null && Scope(invocation).DescendantNodes().OfType<IdentifierNameSyntax>()
                .Any(id => id.Identifier.ValueText == variable && id.SpanStart >= invocation.Span.End && !IsWrite(id));
            yield return new Site(file, line, name, callee.Bridged, variable, read);
        }
    }

    // ---- the corpus ----

    // Well below the real count, far above zero: a parser that drifts and finds almost nothing would
    // otherwise pass the "every site reads" assertion vacuously.
    private const int MinExpectedSites = 8;

    [SkippableFact]
    [Trait("Category", GeneratedBindingsOutputRequirement.TraitCategory)]
    public void EveryEmittedSwiftErrorCallSite_ReadsTheErrorAfterTheCall()
    {
        var output = Path.Combine(RepoRoot, "BindingTests", "output");
        var corpus = Directory.Exists(output)
            ? Directory.EnumerateFiles(output, "*.cs", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal).ToList()
            : new List<string>();
        GeneratedBindingsOutputRequirement.SkipUnlessAvailable(corpus.Count > 0, $"Generated bindings corpus not found under {output}");

        var sites = corpus.SelectMany(f => Sites(Path.GetFileName(f), File.ReadAllText(f))).ToList();
        Assert.True(sites.Count >= MinExpectedSites,
            $"only {sites.Count} SwiftError call site(s) found in the corpus (floor {MinExpectedSites}): the scan has drifted");
        log.WriteLine($"{sites.Count} SwiftError call site(s): {sites.Count(s => !s.Bridged)} direct, {sites.Count(s => s.Bridged)} through a bridge shim");
        var unread = sites.Where(s => !s.Read).ToList();
        Assert.True(unread.Count == 0,
            $"{unread.Count} of {sites.Count} SwiftError call site(s) do not read the error after the call " +
            "(dotnet/runtime#136523 crashes the JIT on these):\n" + string.Join("\n", unread));
    }

    // ---- the scan itself ----

    private static string Caller(string body, string import) => $$"""
        namespace M
        {
            public static unsafe partial class C
            {
                public static long Call(long a)
                {
        {{body}}
                }

                {{import}}
            }
        }
        """;

    private const string PointerImport = """
        [global::System.Runtime.InteropServices.UnmanagedCallConv(CallConvs = new global::System.Type[] { typeof(global::System.Runtime.CompilerServices.CallConvSwift) })]
                [global::System.Runtime.InteropServices.LibraryImport("M", EntryPoint = "$s1M1tyS2iKF")]
                private static partial long PInvoke_t(long a, global::System.Runtime.InteropServices.Swift.SwiftError* error);
        """;

    private const string RefImport = """
        [global::System.Runtime.InteropServices.UnmanagedCallConv(CallConvs = new global::System.Type[] { typeof(global::System.Runtime.CompilerServices.CallConvSwift) })]
                [global::System.Runtime.InteropServices.LibraryImport("M", EntryPoint = "$s1M1tyS2iKF")]
                private static partial long PInvoke_t(long a, ref global::System.Runtime.InteropServices.Swift.SwiftError error);
        """;

    [Theory]
    [InlineData("global::System.Runtime.InteropServices.Swift.SwiftError e = default; var r = PInvoke_t(a, &e); if (e.Value != null) throw new System.Exception(); return r;", true)]
    [InlineData("global::System.Runtime.InteropServices.Swift.SwiftError e = default; var r = PInvoke_t(a, &e); global::Swift.SwiftErrorException.ThrowIfError(e); return r;", true)]
    [InlineData("global::System.Runtime.InteropServices.Swift.SwiftError e = default; var r = PInvoke_t(a, &e); return r;", false)]
    [InlineData("global::System.Runtime.InteropServices.Swift.SwiftError e = default; if (e.Value != null) return 0; var r = PInvoke_t(a, &e); return r;", false)]
    [InlineData("global::System.Runtime.InteropServices.Swift.SwiftError e = default; var r = PInvoke_t(a, &e); e = default; return r;", false)]
    public void PointerError_IsReadOnlyWhenAUseFollowsTheCall(string body, bool read)
    {
        var site = Assert.Single(Sites("t.cs", Caller(body, PointerImport)));
        Assert.Equal("e", site.Variable);
        Assert.False(site.Bridged);
        Assert.Equal(read, site.Read);
    }

    [Theory]
    [InlineData("global::System.Runtime.InteropServices.Swift.SwiftError e = default; var r = PInvoke_t(a, ref e); if (e.Value != null) return 0; return r;", true)]
    [InlineData("global::System.Runtime.InteropServices.Swift.SwiftError e = default; return PInvoke_t(a, ref e);", false)]
    public void RefError_IsReadOnlyWhenAUseFollowsTheCall(string body, bool read)
    {
        var site = Assert.Single(Sites("t.cs", Caller(body, RefImport)));
        Assert.Equal("e", site.Variable);
        Assert.Equal(read, site.Read);
    }

    [Fact]
    public void ErrorThatIsNotALocal_IsUnread()
    {
        var site = Assert.Single(Sites("t.cs", Caller("return PInvoke_t(a, null);", PointerImport)));
        Assert.Null(site.Variable);
        Assert.False(site.Read);
    }

    /// <summary>
    /// Routing a throwing import through its bridge leaves its callers unchanged: the shim keeps the
    /// import's name and signature, so a caller that read the error still reads it, now as a bridged site.
    /// </summary>
    [Fact]
    public void RoutedThrowingImport_KeepsItsCallersReadingTheError()
    {
        var plan = Planned(Planner().PlanForward(Throwing(Free("Swift.Int", "Swift.Int")), Signature("long", "long", "SwiftError*"), "$s1M1tyS2iKF"));
        var source = Caller(
            "global::System.Runtime.InteropServices.Swift.SwiftError e = default; var r = PInvoke_t(a, &e); if (e.Value != null) throw new System.Exception(); return r;",
            PointerImport);
        var set = new SwiftCallPlanSet
        {
            Module = "M",
            Coverage = [new SwiftPlanCoverageEntry(SwiftTransitionKind.ForwardPInvoke, "M.C::PInvoke_t", "$s1M1tyS2iKF", SwiftPlanStatus.Planned)
            {
                Symbol = plan.Symbol,
                Hash = plan.Hash,
                Executable = true,
            }],
            Plans = [plan],
        };
        var routing = SwiftBridgeRouting.Plan(set, source, "MSwiftBindings");
        Assert.True(Assert.Single(routing.Routes).IsBridged, routing.Routes[0].Refusal);

        var before = Assert.Single(Sites("t.cs", source));
        Assert.False(before.Bridged);
        Assert.True(before.Read);
        var after = Assert.Single(Sites("t.cs", routing.Rewrite(source)));
        Assert.True(after.Bridged);
        Assert.True(after.Read);
        Assert.Equal(before.Variable, after.Variable);
    }
}
