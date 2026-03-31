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

            spc.AddSource("TestRegistry.g.cs", GenerateTestRegistry(validClasses));
            spc.AddSource("TestManifest.g.cs", GenerateTestManifest(validClasses));
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

            // Read method-level attributes
            var methodSkip = GetAttributeReason(method, "SkipAttribute");
            var methodSimSkip = GetAttributeReason(method, "SkipOnSimulatorAttribute");
            var methodDevSkip = GetAttributeReason(method, "SkipOnDeviceAttribute");

            methods.Add(new TestMethodInfo(
                method.Name,
                isAsync,
                methodSkip,
                methodSimSkip,
                methodDevSkip));
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
                sb.AppendLine($"                    SkipOnDevice: {QuoteOrNull(method.SkipOnDevice)}){(j < cls.Methods.Length - 1 ? "," : "")}");
            }

            sb.AppendLine($"            }}){(i < classes.Count - 1 ? "," : "")}");
        }

        sb.AppendLine("    };");

        // Emit test manifest as a constant for future use (Session 3 crash recovery)
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

        public TestMethodInfo(string name, bool isAsync, string? skip, string? skipOnSim, string? skipOnDevice)
        {
            Name = name;
            IsAsync = isAsync;
            Skip = skip;
            SkipOnSim = skipOnSim;
            SkipOnDevice = skipOnDevice;
        }

        public bool Equals(TestMethodInfo? other)
        {
            if (other is null) return false;
            return Name == other.Name && IsAsync == other.IsAsync
                && Skip == other.Skip && SkipOnSim == other.SkipOnSim
                && SkipOnDevice == other.SkipOnDevice;
        }

        public override bool Equals(object? obj) => Equals(obj as TestMethodInfo);
        public override int GetHashCode() => Name.GetHashCode();
    }
}
