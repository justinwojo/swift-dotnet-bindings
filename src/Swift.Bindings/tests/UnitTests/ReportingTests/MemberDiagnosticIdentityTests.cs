// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Immutable;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Identity-construction and equality tests for <see cref="MemberDiagnosticIdentity"/>.
/// Drives the overload-stable diagnostic identity contract:
/// <c>(Module, DeclPath, Kind, BaseName, ParameterLabels[i] + ParameterTypes[i],
/// Accessor, MangledSymbol)</c> jointly determine equality, and field-level
/// differences in any of those components produce distinct identities.
/// </summary>
public class MemberDiagnosticIdentityTests
{
    [Fact]
    public void FromMember_LegacyTriple_PopulatesModuleAndDeclPath()
    {
        var moduleDecl = TestModelFactory.CreateModuleDecl();
        var classDecl = (ClassDecl)moduleDecl.Types[0];

        var id = MemberDiagnosticIdentity.FromMember(BindingItemKind.Method, "fetch", classDecl);

        Assert.Equal("TestModule", id.Module);
        Assert.Equal("Loader", id.DeclPath);
        Assert.Equal(BindingItemKind.Method, id.Kind);
        Assert.Equal("fetch", id.BaseName);
        Assert.True(id.ParameterLabels.IsEmpty);
        Assert.True(id.ParameterTypes.IsEmpty);
        Assert.Equal(AccessorKind.None, id.Accessor);
        Assert.Null(id.MangledSymbol);
    }

    [Fact]
    public void FromMember_NestedType_SplitsModuleFromDeclChain()
    {
        var moduleDecl = TestModelFactory.CreateModuleDecl();
        var classDecl = (ClassDecl)moduleDecl.Types[0];
        var nested = (StructDecl)classDecl.Types[0];

        var id = MemberDiagnosticIdentity.FromMember(BindingItemKind.Method, "read", nested);

        Assert.Equal("TestModule", id.Module);
        Assert.Equal("Loader.Payload", id.DeclPath);
    }

    [Fact]
    public void FromMember_ModuleScopeContainer_HasEmptyDeclPath()
    {
        var moduleDecl = TestModelFactory.CreateModuleDecl();

        var id = MemberDiagnosticIdentity.FromMember(BindingItemKind.Method, "topLevel", moduleDecl);

        Assert.Equal("TestModule", id.Module);
        Assert.Equal(string.Empty, id.DeclPath);
    }

    [Fact]
    public void FromMember_NullContainer_HasEmptyModuleAndDeclPath()
    {
        var id = MemberDiagnosticIdentity.FromMember(BindingItemKind.Method, "free", null);

        Assert.Equal(string.Empty, id.Module);
        Assert.Equal(string.Empty, id.DeclPath);
        Assert.Equal("free", id.BaseName);
    }

    [Fact]
    public void FromMethod_InstanceMethod_CapturesParameterSignature()
    {
        var moduleDecl = TestModelFactory.CreateModuleDecl();
        var classDecl = (ClassDecl)moduleDecl.Types[0];
        var method = TestModelFactory.CreateMethod(
            "fetch",
            classDecl,
            args: new[] { ("x", "Swift.Int"), ("by", "Swift.String") },
            mangledName: "$s10TestModule6LoaderC5fetch1x2bySi_SStF",
            methodType: MethodType.Instance);

        var id = MemberDiagnosticIdentity.FromMethod(method);

        Assert.Equal(BindingItemKind.Method, id.Kind);
        Assert.Equal("TestModule", id.Module);
        Assert.Equal("Loader", id.DeclPath);
        Assert.Equal("fetch", id.BaseName);
        Assert.Equal(new[] { "x", "by" }, id.ParameterLabels.ToArray());
        Assert.Equal(new[] { "Swift.Int", "Swift.String" }, id.ParameterTypes.ToArray());
        Assert.Equal(AccessorKind.None, id.Accessor);
        Assert.Equal("$s10TestModule6LoaderC5fetch1x2bySi_SStF", id.MangledSymbol);
    }

    [Fact]
    public void FromMethod_StaticMethod_StillKeyedAsMethodKind()
    {
        var moduleDecl = TestModelFactory.CreateModuleDecl();
        var classDecl = (ClassDecl)moduleDecl.Types[0];
        var method = TestModelFactory.CreateMethod(
            "make",
            classDecl,
            args: new[] { ("from", "Swift.String") },
            mangledName: "$s10TestModule6LoaderC4make4fromAcA0M0VAA0F0V_tFZ",
            methodType: MethodType.Static);

        var id = MemberDiagnosticIdentity.FromMethod(method);

        Assert.Equal(BindingItemKind.Method, id.Kind);
        Assert.Equal("make", id.BaseName);
        Assert.Single(id.ParameterLabels);
        Assert.Equal("from", id.ParameterLabels[0]);
    }

    [Fact]
    public void FromMethod_Initializer_BaseNameIsInit()
    {
        var moduleDecl = TestModelFactory.CreateModuleDecl();
        var classDecl = (ClassDecl)moduleDecl.Types[0];
        var ctor = TestModelFactory.CreateMethod(
            "init",
            classDecl,
            args: new[] { ("name", "Swift.String") },
            mangledName: "$s10TestModule6LoaderC4nameACSS_tcfc",
            isConstructor: true);

        var id = MemberDiagnosticIdentity.FromMethod(ctor);

        Assert.Equal("init", id.BaseName);
        Assert.Equal(BindingItemKind.Method, id.Kind);
        Assert.Equal("$s10TestModule6LoaderC4nameACSS_tcfc", id.MangledSymbol);
    }

    [Fact]
    public void FromProperty_CarriesAccessorKind()
    {
        var moduleDecl = TestModelFactory.CreateModuleDecl();
        var classDecl = (ClassDecl)moduleDecl.Types[0];
        var prop = TestModelFactory.CreateProperty("State", classDecl);

        var getterId = MemberDiagnosticIdentity.FromProperty(prop, AccessorKind.Getter);
        var setterId = MemberDiagnosticIdentity.FromProperty(prop, AccessorKind.Setter);
        var propLevelId = MemberDiagnosticIdentity.FromProperty(prop);

        Assert.Equal(BindingItemKind.Property, getterId.Kind);
        Assert.Equal("State", getterId.BaseName);
        Assert.True(getterId.ParameterLabels.IsEmpty);
        Assert.Equal(AccessorKind.Getter, getterId.Accessor);
        Assert.Equal(AccessorKind.Setter, setterId.Accessor);
        Assert.Equal(AccessorKind.None, propLevelId.Accessor);
        Assert.NotEqual(getterId, setterId);
        Assert.NotEqual(getterId, propLevelId);
    }

    [Fact]
    public void FromSubscript_GetterAndSetterAreDistinct()
    {
        var moduleDecl = TestModelFactory.CreateModuleDecl();
        var classDecl = (ClassDecl)moduleDecl.Types[0];
        var sub = TestModelFactory.CreateSubscript(
            classDecl,
            indexParams: new[] { ("index", "Swift.Int") });

        var getId = MemberDiagnosticIdentity.FromSubscript(sub, AccessorKind.SubscriptGetter);
        var setId = MemberDiagnosticIdentity.FromSubscript(sub, AccessorKind.SubscriptSetter);

        Assert.Equal(BindingItemKind.Subscript, getId.Kind);
        Assert.Equal("subscript", getId.BaseName);
        Assert.Equal(new[] { "index" }, getId.ParameterLabels.ToArray());
        Assert.Equal(new[] { "Swift.Int" }, getId.ParameterTypes.ToArray());
        Assert.NotEqual(getId, setId);
        Assert.Equal(getId.MangledSymbol, setId.MangledSymbol);
    }

    [Fact]
    public void FromOperator_PullsSignatureFromUnderlyingMethod()
    {
        var moduleDecl = TestModelFactory.CreateModuleDecl();
        var classDecl = (ClassDecl)moduleDecl.Types[0];
        var op = TestModelFactory.CreateOperator(
            "+",
            classDecl,
            args: new[] { ("lhs", "TestModule.Loader"), ("rhs", "TestModule.Loader") });

        var id = MemberDiagnosticIdentity.FromOperator(op);

        Assert.Equal(BindingItemKind.Operator, id.Kind);
        Assert.Equal("+", id.BaseName);
        Assert.Equal(new[] { "lhs", "rhs" }, id.ParameterLabels.ToArray());
        Assert.Equal(new[] { "TestModule.Loader", "TestModule.Loader" }, id.ParameterTypes.ToArray());
        Assert.NotNull(id.MangledSymbol);
    }

    [Fact]
    public void FromType_HasEmptyParameters()
    {
        var moduleDecl = TestModelFactory.CreateModuleDecl();
        var classDecl = (ClassDecl)moduleDecl.Types[0];

        var id = MemberDiagnosticIdentity.FromType(classDecl);

        Assert.Equal(BindingItemKind.Type, id.Kind);
        Assert.Equal("Loader", id.BaseName);
        Assert.True(id.ParameterLabels.IsEmpty);
        Assert.True(id.ParameterTypes.IsEmpty);
        Assert.Equal(AccessorKind.None, id.Accessor);
        Assert.Equal("TestModule", id.Module);
    }

    [Fact]
    public void FreeFunction_AtModuleScope_HasEmptyDeclPath()
    {
        var moduleDecl = TestModelFactory.CreateModuleDecl();
        var freeMethod = TestModelFactory.CreateMethod(
            "freeFunc",
            moduleDecl,
            args: new[] { ("_", "Swift.Int") },
            mangledName: "$s10TestModule8freeFuncyySiF");

        var id = MemberDiagnosticIdentity.FromMethod(freeMethod);

        Assert.Equal("TestModule", id.Module);
        Assert.Equal(string.Empty, id.DeclPath);
        Assert.Equal("freeFunc", id.BaseName);
    }

    // ---- Equality / inequality matrix ---------------------------------------

    [Fact]
    public void Equality_IdenticalFields_AreEqual()
    {
        var a = MemberDiagnosticIdentity.Create(
            module: "TestModule",
            declPath: "Loader",
            kind: BindingItemKind.Method,
            baseName: "fetch",
            parameterLabels: ImmutableArray.Create("x"),
            parameterTypes: ImmutableArray.Create("Swift.Int"),
            accessor: AccessorKind.None,
            mangledSymbol: "$s10TestModule6LoaderC5fetch1xySiF");
        var b = MemberDiagnosticIdentity.Create(
            module: "TestModule",
            declPath: "Loader",
            kind: BindingItemKind.Method,
            baseName: "fetch",
            parameterLabels: ImmutableArray.Create("x"),
            parameterTypes: ImmutableArray.Create("Swift.Int"),
            accessor: AccessorKind.None,
            mangledSymbol: "$s10TestModule6LoaderC5fetch1xySiF");

        Assert.Equal(a, b);
        Assert.True(a.Equals(b));
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.Equal(a.ToStableString(), b.ToStableString());
    }

    [Theory]
    [InlineData("module")]
    [InlineData("declPath")]
    [InlineData("kind")]
    [InlineData("baseName")]
    [InlineData("paramLabel")]
    [InlineData("paramType")]
    [InlineData("paramCount")]
    [InlineData("accessor")]
    [InlineData("mangled")]
    public void Equality_AnySingleFieldDiffers_AreNotEqual(string variant)
    {
        var baseId = MemberDiagnosticIdentity.Create(
            module: "TestModule",
            declPath: "Loader",
            kind: BindingItemKind.Method,
            baseName: "fetch",
            parameterLabels: ImmutableArray.Create("x"),
            parameterTypes: ImmutableArray.Create("Swift.Int"),
            accessor: AccessorKind.None,
            mangledSymbol: "$s10TestModule6LoaderC5fetch1xySiF");

        var other = variant switch
        {
            "module" => baseId with { Module = "OtherModule" },
            "declPath" => baseId with { DeclPath = "OtherType" },
            "kind" => baseId with { Kind = BindingItemKind.Property },
            "baseName" => baseId with { BaseName = "load" },
            "paramLabel" => baseId with { ParameterLabels = ImmutableArray.Create("y") },
            "paramType" => baseId with { ParameterTypes = ImmutableArray.Create("Swift.String") },
            "paramCount" => baseId with
            {
                ParameterLabels = ImmutableArray.Create("x", "extra"),
                ParameterTypes = ImmutableArray.Create("Swift.Int", "Swift.String"),
            },
            "accessor" => baseId with { Accessor = AccessorKind.Getter },
            "mangled" => baseId with { MangledSymbol = "$s10TestModule6LoaderC5fetch1xySiG" },
            _ => throw new ArgumentOutOfRangeException(nameof(variant)),
        };

        Assert.NotEqual(baseId, other);
        Assert.False(baseId.Equals(other));
        Assert.NotEqual(baseId.ToStableString(), other.ToStableString());
    }

    [Fact]
    public void Equality_TrailingClosureVsNonTrailingOverloads_AreNotEqual()
    {
        // The gameplan callout: parameter labels alone don't disambiguate two
        // overloads that differ only in trailing-closure-vs-non shape (both
        // can have label "_" or no label). Parameter types must also be in
        // the identity. Two overloads with the same labels but different
        // types must record distinctly.
        var withInt = MemberDiagnosticIdentity.Create(
            module: "TestModule",
            declPath: "Loader",
            kind: BindingItemKind.Method,
            baseName: "foo",
            parameterLabels: ImmutableArray.Create("_"),
            parameterTypes: ImmutableArray.Create("Swift.Int"));
        var withClosure = MemberDiagnosticIdentity.Create(
            module: "TestModule",
            declPath: "Loader",
            kind: BindingItemKind.Method,
            baseName: "foo",
            parameterLabels: ImmutableArray.Create("_"),
            parameterTypes: ImmutableArray.Create("(Swift.Int) -> Swift.Void"));

        Assert.NotEqual(withInt, withClosure);
        Assert.NotEqual(withInt.GetHashCode(), withClosure.GetHashCode());
    }

    [Fact]
    public void Equality_DefaultStruct_HasEmptyArraysAndDoesNotThrow()
    {
        // record struct default(T) leaves ImmutableArray fields uninitialized
        // (IsDefault=true). Equals/GetHashCode must treat default arrays as
        // empty and not throw.
        var defaultId = default(MemberDiagnosticIdentity);
        var emptyId = MemberDiagnosticIdentity.Create(
            module: string.Empty,
            declPath: string.Empty,
            kind: BindingItemKind.Type,
            baseName: string.Empty);

        Assert.Equal(defaultId, emptyId);
        Assert.Equal(defaultId.GetHashCode(), emptyId.GetHashCode());
        Assert.Equal(defaultId.ToStableString(), emptyId.ToStableString());
    }

    [Fact]
    public void Create_ParameterLabelTypeLengthMismatch_Throws()
    {
        Assert.Throws<ArgumentException>(() => MemberDiagnosticIdentity.Create(
            module: "M",
            declPath: "T",
            kind: BindingItemKind.Method,
            baseName: "f",
            parameterLabels: ImmutableArray.Create("a", "b"),
            parameterTypes: ImmutableArray.Create("Swift.Int")));
    }

    [Fact]
    public void ToStableString_HasExpectedShape()
    {
        var id = MemberDiagnosticIdentity.Create(
            module: "TestModule",
            declPath: "Loader",
            kind: BindingItemKind.Method,
            baseName: "fetch",
            parameterLabels: ImmutableArray.Create("x", "by"),
            parameterTypes: ImmutableArray.Create("Swift.Int", "Swift.String"),
            accessor: AccessorKind.None,
            mangledSymbol: "$s_mangled");

        var s = id.ToStableString();
        Assert.Contains("TestModule|Loader|Method|fetch(", s);
        Assert.Contains("x:Swift.Int", s);
        Assert.Contains("by:Swift.String", s);
        Assert.Contains("|None|", s);
        Assert.EndsWith("$s_mangled", s);
    }

    [Fact]
    public void HashSet_DistinctOverloads_NotCollapsed()
    {
        // Direct demonstration of the M1 dedup contract: two MemberDiagnosticIdentity
        // values that differ only in parameter types are distinct hash-set entries.
        var fooInt = MemberDiagnosticIdentity.Create(
            module: "TestModule", declPath: "Loader",
            kind: BindingItemKind.Method, baseName: "foo",
            parameterLabels: ImmutableArray.Create("_"),
            parameterTypes: ImmutableArray.Create("Swift.Int"));
        var fooString = MemberDiagnosticIdentity.Create(
            module: "TestModule", declPath: "Loader",
            kind: BindingItemKind.Method, baseName: "foo",
            parameterLabels: ImmutableArray.Create("_"),
            parameterTypes: ImmutableArray.Create("Swift.String"));

        var set = new HashSet<MemberDiagnosticIdentity>();
        Assert.True(set.Add(fooInt));
        Assert.True(set.Add(fooString));
        Assert.Equal(2, set.Count);
        Assert.False(set.Add(fooInt)); // dedup hits on second add.
    }
}
