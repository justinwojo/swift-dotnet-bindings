// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// F34 (deliverable A): a non-<c>Equatable</c>, heap-backed root class wrapper must project
/// object identity into C# as handle-identity <c>Equals</c>/<c>GetHashCode</c> — two C# wrappers
/// over the SAME Swift instance compare equal and hash alike — guarded so a disposed/zero-handle
/// wrapper falls back to reference identity instead of colliding on <c>IntPtr.Zero</c>. The
/// dispatcher (<see cref="ClassEqualityMethodsWriter.WriteSwiftEquatableImplementation"/>)
/// must route an <c>Equatable</c> class to the Swift-equals variant (value equality, typed
/// <c>IEquatable&lt;T&gt;</c>, <c>operator ==</c>) and a derived class to no override at all
/// (it inherits the root's). These pin the emitted SHAPE; the ARC/round-trip BEHAVIOR is pinned
/// at the runtime layer by the BindingTests Lifetime/ identity tests.
/// </summary>
public class ClassIdentityEmitterTests
{
    private static string Emit(ClassDecl classDecl)
    {
        var stringWriter = new StringWriter();
        var csWriter = new CSharpWriter(stringWriter);
        // Non-generic class → typeNameWithGenerics is just the bare name. No explicit ==/!= operators.
        var writer = new ClassEqualityMethodsWriter(
            csWriter, classDecl, classDecl.Name,
            hasExplicitEqualityOperator: false, hasExplicitInequalityOperator: false,
            typeDatabase: null);
        writer.WriteSwiftEquatableImplementation();
        return stringWriter.ToString();
    }

    private static ClassDecl MakeClass(string name, bool equatable = false, ClassDecl? resolvedSuperclass = null)
    {
        var cls = new ClassDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}CN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = null,
            ResolvedSuperclass = resolvedSuperclass,
        };
        if (equatable)
        {
            cls.Conformances.Add(new TypeConformance(
                cls.SwiftTypeName,
                SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"),
                "$sEquatableConformance"));
        }
        return cls;
    }

    [Fact]
    public void NonEquatableRootClass_EmitsGuardedHandleIdentityEquals()
    {
        var output = Emit(MakeClass("ImageBuffer"));

        // Identity Equals: compares the live Swift handle, never a value witness.
        Assert.Contains("public override bool Equals(object? obj)", output);
        Assert.Contains("var thisHandle = GetSwiftHandle();", output);
        Assert.Contains("var otherHandle = other.GetSwiftHandle();", output);
        // Disposed/zero-handle guard: only compare handles when BOTH are live.
        Assert.Contains("thisHandle != IntPtr.Zero && otherHandle != IntPtr.Zero", output);
        Assert.Contains("return thisHandle == otherHandle;", output);
        // Fall back to reference identity when either operand has no live handle.
        Assert.Contains("return ReferenceEquals(this, other);", output);
    }

    [Fact]
    public void NonEquatableRootClass_EmitsGuardedHandleIdentityHashCode()
    {
        var output = Emit(MakeClass("ImageBuffer"));

        Assert.Contains("public override int GetHashCode()", output);
        Assert.Contains("var handle = GetSwiftHandle();", output);
        // Live handle hashes by pointer; dead handle defers to object identity hash.
        Assert.Contains("handle != IntPtr.Zero ? handle.GetHashCode() : base.GetHashCode();", output);
        // Pin the first-computed cache: the hash must be memoized so GetHashCode stays stable across
        // Dispose (hash-key immutability). A future edit that drops caching would re-derive from a
        // zeroed handle after Dispose and silently break HashSet/Dictionary membership.
        Assert.Contains("private int? _swiftIdentityHashCode;", output);
        Assert.Contains("if (_swiftIdentityHashCode is int cached)", output);
        Assert.Contains("return cached;", output);
        Assert.Contains("_swiftIdentityHashCode = hash;", output);
    }

    [Fact]
    public void NonEquatableRootClass_DoesNotEmitEqualityOperatorOrTypedEquatable()
    {
        var output = Emit(MakeClass("ImageBuffer"));

        // Object-identity only — no operator == (a deliberate F34 decision) and no typed
        // IEquatable<T>.Equals(T?) (that is the value-equality surface).
        Assert.DoesNotContain("operator ==", output);
        Assert.DoesNotContain("public bool Equals(ImageBuffer? other)", output);
        Assert.DoesNotContain("SwiftEquatable.Equals", output);
    }

    [Fact]
    public void EquatableRootClass_RoutesToValueEqualityNotIdentity()
    {
        var output = Emit(MakeClass("Money", equatable: true));

        // Value-equality variant: typed IEquatable<T> surface + operator ==, routed through the
        // Swift equality witness — NOT the handle-identity fallback.
        Assert.Contains("public bool Equals(Money? other)", output);
        Assert.Contains("operator ==", output);
        Assert.Contains("SwiftEquatable.Equals", output);
        // The identity-specific guard must be absent — an Equatable class never reaches it.
        Assert.DoesNotContain("return ReferenceEquals(this, other);", output);
        Assert.DoesNotContain("var thisHandle = GetSwiftHandle();", output);
    }

    [Fact]
    public void DerivedNonEquatableClass_EmitsNoOverride_InheritsRoot()
    {
        var root = MakeClass("BaseBuffer");
        var derived = MakeClass("TileBuffer", resolvedSuperclass: root);

        var output = Emit(derived);

        // A derived class has no _handle of its own; it inherits the root's identity Equals.
        Assert.DoesNotContain("Equals", output);
        Assert.DoesNotContain("GetHashCode", output);
        Assert.True(string.IsNullOrWhiteSpace(output), $"Expected no emission for a derived class, got:\n{output}");
    }
}
