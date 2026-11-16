// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.IO;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// The class equality wrapper behind a bound class's <c>Equals</c>. It must compare through the
/// class's <c>Equatable</c> witness: a subclass that declares its own <c>==</c> while inheriting
/// the conformance (from NSObject or a Swift base) would otherwise resolve <c>l == r</c> statically
/// to that operator, so <c>Equals</c> would disagree with <c>GetHashCode</c>, which hashes through
/// the inherited <c>Hashable</c> witness, and with Swift's own <c>Set</c>.
/// </summary>
public class ClassEqualityWrapperTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EqualityWrapper_ComparesThroughTheEquatableWitness(bool isObjCRooted)
    {
        var moduleDecl = new ModuleDecl
        {
            Name = "TestModule",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
        };
        var classDecl = new ClassDecl
        {
            Name = "Keyed",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Keyed"),
            MangledName = "$s10TestModule5KeyedCN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl,
            IsObjCRooted = isObjCRooted,
        };
        classDecl.Conformances.Add(new TypeConformance(classDecl.SwiftTypeName,
            SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"), ""));

        var csOutput = new StringWriter();
        var swiftOutput = new StringWriter();
        var writer = new ClassEqualityMethodsWriter(
            new CSharpWriter(csOutput), classDecl, "Keyed",
            hasExplicitEqualityOperator: true, hasExplicitInequalityOperator: false,
            new SwiftWriter(swiftOutput), new ModuleEmissionContext(), "TestModuleSwiftBindings");

        writer.WriteSwiftEquatableImplementation();

        var swift = swiftOutput.ToString();
        Assert.Contains("func witnessEquals<T: Swift.Equatable>(_ a: T, _ b: T) -> Swift.Bool { a == b }", swift);
        Assert.Contains("return witnessEquals(l, r) ? 1 : 0", swift);
        Assert.DoesNotContain("(l == r)", swift);
        Assert.Contains("PInvoke_eq(this.GetSwiftHandle(), other.GetSwiftHandle())", csOutput.ToString());
    }
}
