// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SwiftBindings.TestDiscovery;

[Generator]
public class TestDiscoveryGenerator : IIncrementalGenerator
{
    /// <summary>
    /// SBTD001: a discovered <c>Test*</c> method is declared <c>async void</c>. The discovery
    /// invoker cannot await a <c>void</c>-returning method, so it returns before the async body
    /// completes — every post-await assertion and exception is detached and the harness reports a
    /// false PASS. Author the method as <c>async Task</c> (or plain sync) instead. Error severity
    /// so the footgun fails the build rather than silently passing.
    /// </summary>
    private static readonly DiagnosticDescriptor AsyncVoidTestRule = new(
        id: "SBTD001",
        title: "Test method is 'async void'",
        messageFormat: "Test method '{0}' is 'async void'; the discovery invoker cannot await it, so post-await assertions/exceptions are detached and the test falsely passes. Declare it 'async Task' (or remove 'async' if it has no await).",
        category: "SwiftBindings.TestDiscovery",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>
    /// SBTD002: a <c>Test*</c>-named method on a <c>TestBase</c> class that discovery silently drops
    /// because it is non-public, <c>static</c>, or parameterized — none of which the registry-driven
    /// invoker can call. A method that looks like a test but never runs is a false green, so this is
    /// an Error: make it public/instance/parameterless, or rename it so it doesn't start with "Test".
    /// (<c>async void</c> is NOT covered here — it IS discovered, then flagged by SBTD001.)
    /// </summary>
    private static readonly DiagnosticDescriptor NearMissTestMethodRule = new(
        id: "SBTD002",
        title: "Test method will not be discovered",
        messageFormat: "Test method '{0}' on a 'TestBase' class is {1}, so test discovery silently skips it and it never runs (a false green). Make it public, instance, and parameterless, or rename it so it does not start with 'Test'.",
        category: "SwiftBindings.TestDiscovery",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>
    /// SBTD003: a class named <c>*Tests</c> that declares public, instance, parameterless
    /// <c>Test*</c> method(s) but does NOT derive <c>TestBase</c>, so discovery never sees it and
    /// none of its tests run. Error severity — a whole silently-undiscovered test class is the
    /// largest false-green shape. Add <c>: TestBase</c>.
    /// </summary>
    private static readonly DiagnosticDescriptor NonTestBaseTestClassRule = new(
        id: "SBTD003",
        title: "Test-named class does not derive TestBase",
        messageFormat: "Class '{0}' is named like a test class and declares public 'Test*' method(s) but does not derive from 'TestBase'. Discovery never sees it, so none of its tests run; add ': TestBase'.",
        category: "SwiftBindings.TestDiscovery",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Find all class declarations that inherit from TestBase
        var testClasses = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) => IsTestClassCandidate(node),
                transform: static (ctx, ct) => GetTestClassInfo(ctx, ct))
            .Where(static info => info is not null)
            .Collect();

        context.RegisterSourceOutput(testClasses, static (spc, classes) =>
        {
            var validClasses = classes
                .Where(c => c is not null)
                .Cast<TestClassInfo>()
                .OrderBy(c => c.Name)
                .ToList();

            // Fail the build on any `async void` test method — it would silently detach.
            foreach (var cls in validClasses)
            {
                foreach (var method in cls.Methods)
                {
                    if (method.IsAsyncVoid)
                    {
                        spc.ReportDiagnostic(Diagnostic.Create(
                            AsyncVoidTestRule, Location.None, $"{cls.Name}.{method.Name}"));
                    }
                }
            }

            spc.AddSource("TestRegistry.g.cs", GenerateTestRegistry(validClasses));
            spc.AddSource("TestManifest.g.cs", GenerateTestManifest(validClasses));
        });

        // Separate, diagnostics-only pipeline for the discovery near-misses (SBTD002/SBTD003). It is
        // intentionally NOT folded into the registry pipeline above: that one's syntactic predicate
        // requires a base list (so a `*Tests` class missing `: TestBase` entirely would never be
        // seen), and keeping the registry path untouched removes any risk of these widened candidates
        // perturbing generated output. This pipeline emits no source — only diagnostics.
        var nearMisses = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) => IsDiagnosticCandidate(node),
                transform: static (ctx, ct) => GetNearMissDiagnostics(ctx, ct))
            .Where(static d => !d.IsDefaultOrEmpty)
            .Collect();

        context.RegisterSourceOutput(nearMisses, static (spc, perClass) =>
        {
            // A partial class can surface the same near-miss from more than one declaration node
            // (the widened predicate admits base-less partials); collapse by (rule, args) so each
            // problem is reported exactly once.
            var reported = new HashSet<string>();
            foreach (var perClassDiagnostics in perClass)
            {
                foreach (var nm in perClassDiagnostics)
                {
                    if (!reported.Add(nm.RuleId + "|" + string.Join("|", nm.Args)))
                        continue;
                    var descriptor = nm.RuleId == "SBTD002" ? NearMissTestMethodRule : NonTestBaseTestClassRule;
                    spc.ReportDiagnostic(Diagnostic.Create(descriptor, Location.None, nm.Args.ToArray()));
                }
            }
        });
    }

    /// <summary>
    /// Fast syntactic filter: non-abstract class with a base type.
    /// </summary>
    private static bool IsTestClassCandidate(SyntaxNode node)
    {
        if (node is not ClassDeclarationSyntax classDecl)
            return false;

        // Must have a base type
        if (classDecl.BaseList == null || classDecl.BaseList.Types.Count == 0)
            return false;

        // Must not be abstract
        if (classDecl.Modifiers.Any(SyntaxKind.AbstractKeyword))
            return false;

        return true;
    }

    /// <summary>
    /// Semantic check: verify the class actually inherits from TestBase and collect metadata.
    /// </summary>
    private static TestClassInfo? GetTestClassInfo(GeneratorSyntaxContext context, CancellationToken ct)
    {
        var classDecl = (ClassDeclarationSyntax)context.Node;
        var semanticModel = context.SemanticModel;

        var classSymbol = semanticModel.GetDeclaredSymbol(classDecl, ct);
        if (classSymbol == null)
            return null;

        // Walk the inheritance chain to find TestBase
        if (!InheritsFromTestBase(classSymbol))
            return null;

        // Get full namespace + name for the class
        var fullName = classSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var name = classSymbol.Name;
        var namespaceName = classSymbol.ContainingNamespace?.ToDisplayString() ?? "";

        // Read class-level attributes
        var classSkip = GetAttributeReason(classSymbol, "SkipAttribute");
        var classSimSkip = GetAttributeReason(classSymbol, "SkipOnSimulatorAttribute");
        var classDevSkip = GetAttributeReason(classSymbol, "SkipOnDeviceAttribute");

        // Discover Test* methods
        var methods = new List<TestMethodInfo>();
        foreach (var member in classSymbol.GetMembers())
        {
            if (member is not IMethodSymbol method)
                continue;

            if (!method.Name.StartsWith("Test"))
                continue;

            if (method.DeclaredAccessibility != Accessibility.Public)
                continue;

            if (method.IsStatic)
                continue;

            if (method.Parameters.Length != 0)
                continue;

            // Detect async methods (returns Task or ValueTask)
            var isAsync = IsAsyncMethod(method);

            // `async void` test methods are fire-and-forget: the discovery-driven invoker
            // returns before the async body completes, detaching every post-await assertion
            // and exception so the harness reports a false PASS. They cannot be awaited, so we
            // flag them for a build-time diagnostic rather than silently mis-running them.
            var isAsyncVoid = method.IsAsync && method.ReturnsVoid;

            // Read method-level attributes
            var methodSkip = GetAttributeReason(method, "SkipAttribute");
            var methodSimSkip = GetAttributeReason(method, "SkipOnSimulatorAttribute");
            var methodDevSkip = GetAttributeReason(method, "SkipOnDeviceAttribute");
            var methodCatX64Skip = GetAttributeReason(method, "SkipOnCatalystX64Attribute");
            var methodMonoJitSkip = GetAttributeReason(method, "SkipOnMonoJitAttribute");

            methods.Add(new TestMethodInfo(
                method.Name,
                isAsync,
                methodSkip,
                methodSimSkip,
                methodDevSkip,
                methodCatX64Skip,
                methodMonoJitSkip,
                isAsyncVoid));
        }

        if (methods.Count == 0)
            return null;

        return new TestClassInfo(
            name,
            namespaceName,
            fullName,
            classSkip,
            classSimSkip,
            classDevSkip,
            methods.ToImmutableArray());
    }

    private static bool InheritsFromTestBase(INamedTypeSymbol classSymbol)
    {
        var current = classSymbol.BaseType;
        while (current != null)
        {
            if (current.Name == "TestBase")
                return true;
            current = current.BaseType;
        }
        return false;
    }

    /// <summary>
    /// Syntactic filter for the SBTD002/SBTD003 diagnostics pipeline. Admits any non-abstract class
    /// that either has a base type (a potential <c>TestBase</c> subclass with near-miss methods) or
    /// is named <c>*Tests</c> (a potential test class missing <c>: TestBase</c> — which the registry
    /// pipeline's base-list-required predicate would miss).
    /// </summary>
    private static bool IsDiagnosticCandidate(SyntaxNode node)
    {
        if (node is not ClassDeclarationSyntax classDecl)
            return false;

        if (classDecl.Modifiers.Any(SyntaxKind.AbstractKeyword))
            return false;

        var hasBaseList = classDecl.BaseList != null && classDecl.BaseList.Types.Count > 0;
        return hasBaseList || classDecl.Identifier.Text.EndsWith("Tests", StringComparison.Ordinal);
    }

    /// <summary>
    /// Semantic pass for the near-miss diagnostics. On a <c>TestBase</c> subclass, flags every
    /// <c>Test*</c> method discovery would silently drop (non-public / static / parameterized) as
    /// SBTD002. On a <c>*Tests</c>-named class that does NOT derive <c>TestBase</c> but has a
    /// would-be-discoverable <c>Test*</c> method, flags the class as SBTD003.
    /// </summary>
    private static ImmutableArray<NearMiss> GetNearMissDiagnostics(GeneratorSyntaxContext context, CancellationToken ct)
    {
        var classDecl = (ClassDeclarationSyntax)context.Node;
        var classSymbol = context.SemanticModel.GetDeclaredSymbol(classDecl, ct);
        if (classSymbol == null)
            return ImmutableArray<NearMiss>.Empty;

        var className = classSymbol.Name;
        var builder = ImmutableArray.CreateBuilder<NearMiss>();

        if (InheritsFromTestBase(classSymbol))
        {
            // SBTD002: a Test*-named method the registry-driven invoker cannot call. The reason
            // ordering mirrors the discovery filter in GetTestClassInfo (public → instance →
            // parameterless). async-void is excluded — it passes the filter and is SBTD001's job.
            foreach (var member in classSymbol.GetMembers())
            {
                if (member is not IMethodSymbol method)
                    continue;
                if (method.MethodKind != MethodKind.Ordinary)
                    continue;
                if (!method.Name.StartsWith("Test", StringComparison.Ordinal))
                    continue;

                var reason =
                    method.DeclaredAccessibility != Accessibility.Public ? "non-public" :
                    method.IsStatic ? "static" :
                    method.Parameters.Length != 0 ? "parameterized" :
                    null;
                if (reason == null)
                    continue; // a fully-discoverable method — not a near-miss

                builder.Add(new NearMiss("SBTD002",
                    ImmutableArray.Create($"{className}.{method.Name}", reason)));
            }
        }
        else if (className.EndsWith("Tests", StringComparison.Ordinal))
        {
            // SBTD003: a *Tests-named class that misses TestBase but has at least one method shaped
            // exactly like a discoverable test (public, instance, parameterless, Test*-named).
            var hasDiscoverableShape = classSymbol.GetMembers()
                .OfType<IMethodSymbol>()
                .Any(m => m.MethodKind == MethodKind.Ordinary
                    && m.Name.StartsWith("Test", StringComparison.Ordinal)
                    && m.DeclaredAccessibility == Accessibility.Public
                    && !m.IsStatic
                    && m.Parameters.Length == 0);
            if (hasDiscoverableShape)
                builder.Add(new NearMiss("SBTD003", ImmutableArray.Create(className)));
        }

        return builder.ToImmutable();
    }

    private static bool IsAsyncMethod(IMethodSymbol method)
    {
        var returnType = method.ReturnType;
        var name = returnType.Name;
        // Check for Task, ValueTask, Task<T>, ValueTask<T>
        return name == "Task" || name == "ValueTask";
    }

    private static string? GetAttributeReason(ISymbol symbol, string attributeName)
    {
        foreach (var attr in symbol.GetAttributes())
        {
            if (attr.AttributeClass?.Name == attributeName)
            {
                // The reason is the first constructor argument
                if (attr.ConstructorArguments.Length > 0 &&
                    attr.ConstructorArguments[0].Value is string reason)
                {
                    return reason;
                }
                // SlowAttribute and similar have no reason — return empty string to indicate presence
                return "";
            }
        }
        return null;
    }

    private static string GenerateTestRegistry(List<TestClassInfo> classes)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("// Generated by SwiftBindings.TestDiscovery");
        sb.AppendLine();
        sb.AppendLine("using System.CodeDom.Compiler;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using System.Threading.Tasks;");
        sb.AppendLine("using RuntimeTestsApp.Infrastructure;");
        sb.AppendLine();
        sb.AppendLine("namespace RuntimeTestsApp.Infrastructure;");
        sb.AppendLine();
        sb.AppendLine("[GeneratedCode(\"SwiftBindings.TestDiscovery\", \"1.0\")]");
        sb.AppendLine("public static class TestRegistry");
        sb.AppendLine("{");

        // Emit Classes property
        sb.AppendLine("    public static IReadOnlyList<TestClassDescriptor> Classes { get; } = new TestClassDescriptor[]");
        sb.AppendLine("    {");

        for (int i = 0; i < classes.Count; i++)
        {
            var cls = classes[i];
            var qualifiedName = string.IsNullOrEmpty(cls.Namespace)
                ? cls.Name
                : $"{cls.Namespace}.{cls.Name}";

            sb.AppendLine($"        new TestClassDescriptor(");
            sb.AppendLine($"            Name: {Quote(cls.Name)},");
            sb.AppendLine($"            Factory: (results) => new {qualifiedName}(results),");
            sb.AppendLine($"            SkipReason: {QuoteOrNull(cls.SkipReason)},");
            sb.AppendLine($"            SkipOnSimulator: {QuoteOrNull(cls.SkipOnSimulator)},");
            sb.AppendLine($"            SkipOnDevice: {QuoteOrNull(cls.SkipOnDevice)},");
            sb.AppendLine($"            Methods: new TestMethodDescriptor[]");
            sb.AppendLine($"            {{");

            for (int j = 0; j < cls.Methods.Length; j++)
            {
                var method = cls.Methods[j];
                var invokerExpr = method.IsAsync
                    ? $"async (instance) => {{ await (({qualifiedName})instance).{method.Name}(); }}"
                    : $"(instance) => {{ (({qualifiedName})instance).{method.Name}(); return default; }}";

                sb.AppendLine($"                new TestMethodDescriptor(");
                sb.AppendLine($"                    Name: {Quote(method.Name)},");
                sb.AppendLine($"                    Invoker: {invokerExpr},");
                sb.AppendLine($"                    Skip: {QuoteOrNull(method.Skip)},");
                sb.AppendLine($"                    SkipOnSim: {QuoteOrNull(method.SkipOnSim)},");
                sb.AppendLine($"                    SkipOnDevice: {QuoteOrNull(method.SkipOnDevice)},");
                sb.AppendLine($"                    SkipOnCatalystX64: {QuoteOrNull(method.SkipOnCatalystX64)},");
                sb.AppendLine($"                    SkipOnMonoJit: {QuoteOrNull(method.SkipOnMonoJit)}){(j < cls.Methods.Length - 1 ? "," : "")}");
            }

            sb.AppendLine($"            }}){(i < classes.Count - 1 ? "," : "")}");
        }

        sb.AppendLine("    };");

        // Emit test manifest as a constant for crash recovery orchestration.
        sb.AppendLine();
        sb.AppendLine("    /// <summary>");
        sb.AppendLine("    /// Full test manifest: one ClassName.MethodName per line.");
        sb.AppendLine("    /// Used by crash recovery orchestration to compute remaining tests.");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine("    public const string TestManifest =");

        var manifestLines = new List<string>();
        foreach (var cls in classes)
        {
            foreach (var method in cls.Methods)
            {
                manifestLines.Add($"{cls.Name}.{method.Name}");
            }
        }

        if (manifestLines.Count > 0)
        {
            sb.Append("        \"");
            sb.Append(string.Join("\\n", manifestLines));
            sb.AppendLine("\";");
        }
        else
        {
            sb.AppendLine("        \"\";");
        }

        sb.AppendLine("}");

        return sb.ToString();
    }

    /// <summary>
    /// Generates a C# source file containing the test manifest as extractable comment lines.
    /// Each test method is listed as "//! ClassName.MethodName" — the build system extracts
    /// these into TestClasses.g.txt for host-side crash recovery orchestration.
    /// </summary>
    private static string GenerateTestManifest(List<TestClassInfo> classes)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("// Test manifest for crash recovery orchestration.");
        sb.AppendLine("// Extract with: grep '^//! ' TestManifest.g.cs | sed 's/^\\/\\/! //'");
        sb.AppendLine("//");
        sb.AppendLine("// TestManifest:BEGIN");

        foreach (var cls in classes)
        {
            foreach (var method in cls.Methods)
            {
                sb.AppendLine($"//! {cls.Name}.{method.Name}");
            }
        }

        sb.AppendLine("// TestManifest:END");

        return sb.ToString();
    }

    private static string Quote(string value)
    {
        return $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
    }

    private static string QuoteOrNull(string? value)
    {
        return value == null ? "null" : Quote(value);
    }

    // Data models for incremental caching

    /// <summary>
    /// One near-miss diagnostic carried out of the syntax-transform stage. Value equality (with a
    /// sequence-compared <see cref="Args"/>) keeps the incremental pipeline from re-reporting on an
    /// unrelated edit, matching the hand-rolled equality on the other cached models below.
    /// </summary>
    private sealed class NearMiss : IEquatable<NearMiss>
    {
        public string RuleId { get; }
        public ImmutableArray<string> Args { get; }

        public NearMiss(string ruleId, ImmutableArray<string> args)
        {
            RuleId = ruleId;
            Args = args;
        }

        public bool Equals(NearMiss? other)
            => other is not null && RuleId == other.RuleId && Args.SequenceEqual(other.Args);

        public override bool Equals(object? obj) => Equals(obj as NearMiss);
        public override int GetHashCode() => RuleId.GetHashCode();
    }

    private sealed class TestClassInfo : IEquatable<TestClassInfo>
    {
        public string Name { get; }
        public string Namespace { get; }
        public string FullName { get; }
        public string? SkipReason { get; }
        public string? SkipOnSimulator { get; }
        public string? SkipOnDevice { get; }
        public ImmutableArray<TestMethodInfo> Methods { get; }

        public TestClassInfo(string name, string ns, string fullName,
            string? skipReason, string? skipOnSimulator, string? skipOnDevice,
            ImmutableArray<TestMethodInfo> methods)
        {
            Name = name;
            Namespace = ns;
            FullName = fullName;
            SkipReason = skipReason;
            SkipOnSimulator = skipOnSimulator;
            SkipOnDevice = skipOnDevice;
            Methods = methods;
        }

        public bool Equals(TestClassInfo? other)
        {
            if (other is null) return false;
            return Name == other.Name && Namespace == other.Namespace
                && SkipReason == other.SkipReason
                && SkipOnSimulator == other.SkipOnSimulator
                && SkipOnDevice == other.SkipOnDevice
                && Methods.SequenceEqual(other.Methods);
        }

        public override bool Equals(object? obj) => Equals(obj as TestClassInfo);
        public override int GetHashCode() => Name.GetHashCode();
    }

    private sealed class TestMethodInfo : IEquatable<TestMethodInfo>
    {
        public string Name { get; }
        public bool IsAsync { get; }
        public string? Skip { get; }
        public string? SkipOnSim { get; }
        public string? SkipOnDevice { get; }
        public string? SkipOnCatalystX64 { get; }
        public string? SkipOnMonoJit { get; }
        public bool IsAsyncVoid { get; }

        public TestMethodInfo(string name, bool isAsync, string? skip, string? skipOnSim, string? skipOnDevice,
            string? skipOnCatalystX64, string? skipOnMonoJit, bool isAsyncVoid)
        {
            Name = name;
            IsAsync = isAsync;
            Skip = skip;
            SkipOnSim = skipOnSim;
            SkipOnDevice = skipOnDevice;
            SkipOnCatalystX64 = skipOnCatalystX64;
            SkipOnMonoJit = skipOnMonoJit;
            IsAsyncVoid = isAsyncVoid;
        }

        public bool Equals(TestMethodInfo? other)
        {
            if (other is null) return false;
            return Name == other.Name && IsAsync == other.IsAsync
                && Skip == other.Skip && SkipOnSim == other.SkipOnSim
                && SkipOnDevice == other.SkipOnDevice
                && SkipOnCatalystX64 == other.SkipOnCatalystX64
                && SkipOnMonoJit == other.SkipOnMonoJit
                && IsAsyncVoid == other.IsAsyncVoid;
        }

        public override bool Equals(object? obj) => Equals(obj as TestMethodInfo);
        public override int GetHashCode() => Name.GetHashCode();
    }
}
