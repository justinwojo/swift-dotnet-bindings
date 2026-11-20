// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Text.RegularExpressions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// A type that declares both the binary and the prefix form of <c>+</c> or <c>-</c>. Both forms once
/// bound a P/Invoke of the same name; with the same argument count the report's call reader, which
/// resolves a call by name and arity, could not tell which Swift function each operator reaches, and
/// the generator failed the module ("ambiguous call PInvoke_op_Subtraction/2").
/// </summary>
public class OperatorFixityTests
{
    private static string PInvokeName(string cs)
    {
        var body = cs[cs.IndexOf(" operator ", StringComparison.Ordinal)..];
        return Regex.Match(body, @"\b(PInvoke_op_\w+)\(").Groups[1].Value;
    }

    [Theory]
    [InlineData(true, "-", "PInvoke_op_Subtraction", "PInvoke_op_UnaryNegation")]
    [InlineData(false, "-", "PInvoke_op_Subtraction", "PInvoke_op_UnaryNegation")]
    [InlineData(true, "+", "PInvoke_op_Addition", "PInvoke_op_UnaryPlus")]
    [InlineData(false, "+", "PInvoke_op_Addition", "PInvoke_op_UnaryPlus")]
    public async Task BinaryAndPrefixForms_BindDistinctEntryPoints(bool classParent, string symbol, string binaryName, string prefixName)
    {
        var database = await OperatorOptionalObjectTests.CreateDatabaseAsync();
        var parent = OperatorOptionalObjectTests.Parent(classParent);
        var self = new NamedTypeSpec(parent.SwiftTypeName.ToString());

        var (binaryEmitted, binary) = OperatorOptionalObjectTests.Emit(OperatorOptionalObjectTests.Operator(symbol, parent, self, self), database);
        var (prefixEmitted, prefix) = OperatorOptionalObjectTests.Emit(OperatorOptionalObjectTests.Operator(symbol, parent, self), database);

        Assert.True(binaryEmitted, binary);
        Assert.True(prefixEmitted, prefix);
        Assert.Equal(binaryName, PInvokeName(binary));
        Assert.Equal(prefixName, PInvokeName(prefix));
    }

    /// <summary>
    /// The two forms emitted into one class, where a static operator passes the class metatype as an
    /// untyped <c>SwiftSelf</c> (a direct exposure the report's call reader follows to its import), are
    /// read back with every call resolved to exactly one P/Invoke. A throwing prefix form takes the error
    /// slot, which gives it the binary form's argument count.
    /// </summary>
    [Theory]
    [InlineData("-")]
    [InlineData("+")]
    public async Task BinaryAndPrefixForms_ResolveInTheCallReader(string symbol)
    {
        var database = await OperatorOptionalObjectTests.CreateDatabaseAsync();
        var parent = OperatorOptionalObjectTests.Parent(isClass: true);
        var self = new NamedTypeSpec(parent.SwiftTypeName.ToString());
        var (_, binary) = OperatorOptionalObjectTests.Emit(OperatorOptionalObjectTests.Operator(symbol, parent, self, self), database);
        var (_, prefix) = OperatorOptionalObjectTests.Emit(OperatorOptionalObjectTests.Operator(symbol, parent, self, throws: true), database);

        static string Code(string emitted) => string.Join('\n', emitted.Split('\n').TakeWhile(line => !line.StartsWith("Operator '", StringComparison.Ordinal)));
        var source = $$"""
            using System;
            using System.Runtime.CompilerServices;
            using System.Runtime.InteropServices;
            using System.Runtime.InteropServices.Swift;
            using Swift.Runtime;
            namespace TestModule;
            public partial class Box
            {
            {{Code(binary)}}
            {{Code(prefix)}}
            }
            """;
        var directory = Directory.CreateTempSubdirectory("operator-fixity-");
        try
        {
            var path = Path.Combine(directory.FullName, "Fixture.cs");
            File.WriteAllText(path, source);
            var scan = BindingsGeneration.GeneratedSwiftCallReader.Scan([path], directory.FullName);
            Assert.True(scan.UnresolvedSpecimens.Count == 0, string.Join("\n", scan.UnresolvedSpecimens) + "\n" + source);
            // Not vacuous: both operators reach their imports through the untyped SwiftSelf.
            Assert.True(scan.Observations.Count == 2, $"{scan.Observations.Count} exposures\n{source}");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
