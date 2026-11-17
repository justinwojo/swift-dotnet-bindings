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
        var (swift, cs) = EmitEquality(isObjCRooted, hashable: false);
        Assert.Contains("func witnessEquals<T: Swift.Equatable>(_ a: T, _ b: T) -> Swift.Bool { a == b }", swift);
        Assert.Contains("return witnessEquals(l, r) ? 1 : 0", swift);
        Assert.DoesNotContain("(l == r)", swift);
        Assert.Contains("PInvoke_eq(this.GetSwiftHandle(), other.GetSwiftHandle())", cs);
    }

    /// <summary>
    /// An NSObject-rooted class inherits Hashable from Foundation's NSObject conformance, which
    /// the module registers nothing for, so the runtime's conformance lookup misses and hashes by
    /// identity: equal objects hash apart. Its GetHashCode must hash through the witness in Swift.
    /// A Swift-rooted class keeps the runtime lookup, which finds its registered conformance.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HashableClass_HashesThroughTheWitnessOnlyWhenNSObjectRooted(bool isObjCRooted)
    {
        var (swift, cs) = EmitEquality(isObjCRooted, hashable: true);
        if (isObjCRooted)
        {
            Assert.Contains("func witnessHash<T: Swift.Hashable>(_ a: T) -> Swift.Int { a.hashValue }", swift);
            Assert.Contains("PInvoke_hash(handle)", cs);
            Assert.Contains("if (handle == IntPtr.Zero) return global::System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);", cs);
            Assert.DoesNotContain("SwiftHashable.GetHashCode", cs);
        }
        else
        {
            Assert.DoesNotContain("witnessHash", swift);
            Assert.Contains("SwiftHashable.GetHashCode(this)", cs);
        }
    }

    private static (string Swift, string CSharp) EmitEquality(bool isObjCRooted, bool hashable)
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
        if (hashable)
            classDecl.Conformances.Add(new TypeConformance(classDecl.SwiftTypeName,
                SwiftTypeName.FromModuleQualifiedName("Swift.Hashable"), ""));

        var csOutput = new StringWriter();
        var swiftOutput = new StringWriter();
        var writer = new ClassEqualityMethodsWriter(
            new CSharpWriter(csOutput), classDecl, "Keyed",
            hasExplicitEqualityOperator: true, hasExplicitInequalityOperator: false,
            new SwiftWriter(swiftOutput), new ModuleEmissionContext(), "TestModuleSwiftBindings");

        writer.WriteSwiftEquatableImplementation();
        return (swiftOutput.ToString(), csOutput.ToString());
    }
}
