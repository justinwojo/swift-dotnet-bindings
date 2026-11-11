// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BindingsGeneration.Tests;

/// <summary>
/// Finds the P/Invoke declarations in C# source that import a Swift type metadata accessor
/// (an entry point <c>$s…Ma</c>). The accessor is a Swift-convention function that takes the
/// metadata request in its first argument register, so an import that declares no request
/// parameter calls it with whatever that register happens to hold.
/// </summary>
public static class MetadataAccessorImportAudit
{
    /// <summary>One accessor import: the declaring method, its entry point, and its first parameter's type.</summary>
    public readonly record struct AccessorImport(string Method, string EntryPoint, string? FirstParameterType)
    {
        public bool PassesRequest =>
            FirstParameterType is { } type &&
            (type == "TypeMetadataRequest" || type.EndsWith(".TypeMetadataRequest", System.StringComparison.Ordinal));

        public override string ToString() =>
            $"{Method} -> {EntryPoint} (first parameter: {FirstParameterType ?? "none"})";
    }

    private static readonly Regex AccessorSymbol = new(@"^\$s.+Ma$", RegexOptions.CultureInvariant);

    public static IReadOnlyList<AccessorImport> Find(string source)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var imports = new List<AccessorImport>();
        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            var entryPoint = ImportEntryPoint(method);
            if (entryPoint is null || !AccessorSymbol.IsMatch(entryPoint))
                continue;
            var first = method.ParameterList.Parameters.FirstOrDefault()?.Type?.ToString();
            imports.Add(new AccessorImport(method.Identifier.Text, entryPoint, first));
        }
        return imports;
    }

    // The entry point a [DllImport]/[LibraryImport] method binds, or null when the method imports nothing.
    private static string? ImportEntryPoint(MethodDeclarationSyntax method)
    {
        foreach (var attribute in method.AttributeLists.SelectMany(list => list.Attributes))
        {
            var name = attribute.Name.ToString();
            var leaf = name[(name.LastIndexOf('.') + 1)..];
            if (leaf is not ("DllImport" or "DllImportAttribute" or "LibraryImport" or "LibraryImportAttribute"))
                continue;

            var entryPoint = attribute.ArgumentList?.Arguments
                .FirstOrDefault(argument => argument.NameEquals?.Name.Identifier.Text == "EntryPoint")?
                .Expression as LiteralExpressionSyntax;
            return entryPoint?.Token.ValueText ?? method.Identifier.Text;
        }
        return null;
    }
}
