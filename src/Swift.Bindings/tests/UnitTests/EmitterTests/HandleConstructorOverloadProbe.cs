// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Asks the C# compiler which constructor <c>new T(i)</c>, with an <c>int</c> argument, binds to when
/// the call sits in a different type of the binding's own assembly. That is where an SDK-direct
/// consumer's code lives, so any constructor the generator emits that is visible there competes with
/// the public initializers in overload resolution.
/// </summary>
/// <remarks>
/// The probe compiles the emitted type's handle-adopting constructor with its emitted modifiers next to
/// one public single-parameter constructor standing in for a Swift initializer. <c>SwiftHandle</c> is
/// lifted verbatim from the runtime's source, so its conversion operators are the ones that shipped.
/// </remarks>
internal static class HandleConstructorOverloadProbe
{
    /// <summary>The binding of <c>new T(i)</c> in a sibling type; "ambiguous" on CS0121.</summary>
    internal static string BindIntArgumentFromSiblingType(string emittedCSharp, string typeName, string publicParameterType)
    {
        var handleCtors = HandleConstructors(emittedCSharp, typeName);
        Assert.True(handleCtors.Count > 0,
            $"No (SwiftHandle) constructor was emitted for {typeName}; the probe would pass vacuously.");

        var stubCtors = string.Join("\n", handleCtors.Select(c =>
            $"        {string.Join(" ", c.Modifiers.Select(m => m.Text).Where(m => m != "unsafe"))} {typeName}(global::Swift.Runtime.SwiftHandle handle) {{ }}"));

        var source = $$"""
            namespace Swift.Runtime
            {
                using System;
                using System.ComponentModel;
            {{RuntimeSwiftHandleSource()}}
            }

            namespace Probe
            {
                public class {{typeName}}
                {
                    public {{typeName}}({{publicParameterType}} value) { }
            {{stubCtors}}
                }

                public static class Consumer
                {
                    public static object Make(int i) => new {{typeName}}(i);
                }
            }
            """;

        var tree = CSharpSyntaxTree.ParseText(source);
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator).Select(p => MetadataReference.CreateFromFile(p));
        var compilation = CSharpCompilation.Create("HandleCtorProbe" + Guid.NewGuid().ToString("N"),
            [tree], references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        if (errors.Any(e => e.Id == "CS0121"))
            return "ambiguous";
        Assert.True(errors.Count == 0, "Probe failed to compile:\n" + string.Join("\n", errors));

        var creation = tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Single(c => c.Identifier.Text == "Consumer")
            .DescendantNodes().OfType<ObjectCreationExpressionSyntax>().Single();
        var bound = (IMethodSymbol?)compilation.GetSemanticModel(tree).GetSymbolInfo(creation).Symbol;
        Assert.NotNull(bound);
        return bound!.Parameters.Single().Type.ToDisplayString();
    }

    /// <summary>Every constructor of <paramref name="typeName"/> whose single parameter is a <c>SwiftHandle</c>.</summary>
    internal static List<ConstructorDeclarationSyntax> HandleConstructors(string emittedCSharp, string typeName) =>
        CSharpSyntaxTree.ParseText(emittedCSharp).GetRoot().DescendantNodes()
            .OfType<ConstructorDeclarationSyntax>()
            .Where(c => c.Identifier.Text == typeName
                && c.ParameterList.Parameters.Count == 1
                && c.ParameterList.Parameters[0].Type is { } t
                && t.ToString().Split('.').Last() == "SwiftHandle")
            .ToList();

    private static string RuntimeSwiftHandleSource()
    {
        var path = Path.Combine(FindRepoRoot(), "src", "Swift.Runtime", "src", "Swift", "Runtime", "SwiftHandle.cs");
        var declaration = CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetRoot().DescendantNodes()
            .OfType<StructDeclarationSyntax>()
            .Single(s => s.Identifier.Text == "SwiftHandle");
        return declaration.ToFullString();
    }

    private static string FindRepoRoot()
    {
        for (var dir = AppContext.BaseDirectory; dir != null; dir = Path.GetDirectoryName(dir))
        {
            if (File.Exists(Path.Combine(dir, "SwiftBindings.sln")))
                return dir;
        }
        throw new InvalidOperationException("Repository root (SwiftBindings.sln) not found above " + AppContext.BaseDirectory);
    }
}
