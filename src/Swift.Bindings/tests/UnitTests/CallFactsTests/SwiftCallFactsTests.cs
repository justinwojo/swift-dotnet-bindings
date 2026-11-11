// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using BindingsGeneration.Demangling;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Facts per kind, read from fixture ABI JSON through the real parser: target identity, self kind
/// and mutation, parameter conventions, closure escaping, results, effects, generics,
/// availability, dispatch thunks, metadata accessors, and the coverage classification.
/// </summary>
public class SwiftCallFactsTests
{
    private const string M = "TestModule";

    // ---- fixture ABI JSON ----

    private static string Nominal(string printedName, string? ownership = null, string name = "")
    {
        var n = name.Length > 0 ? name : printedName.Split('.').Last();
        var own = ownership is null ? "" : $", \"paramValueOwnership\": \"{ownership}\"";
        return $$"""{ "kind": "TypeNominal", "name": "{{n}}", "printedName": "{{printedName}}"{{own}} }""";
    }

    private const string Void = """{ "kind": "TypeNominal", "name": "Void", "printedName": "()" }""";
    private const string Int32 = """{ "kind": "TypeNominal", "name": "Int32", "printedName": "Swift.Int32", "usr": "s:s5Int32V" }""";
    private const string StringT = """{ "kind": "TypeNominal", "name": "String", "printedName": "Swift.String", "usr": "s:SS" }""";

    private static string Func(
        string name, string printedName, string mangled, IEnumerable<string> children,
        string declKind = "Func", string kind = "Function", string? selfKind = null, bool isStatic = false,
        bool throwing = false, string? genericSig = null, string[]? attrs = null, string? extra = null, string module = M)
    {
        var parts = new List<string>
        {
            $"\"kind\": \"{kind}\"",
            $"\"name\": \"{name}\"",
            $"\"printedName\": \"{printedName}\"",
            $"\"children\": [{string.Join(", ", children)}]",
            $"\"declKind\": \"{declKind}\"",
            $"\"mangledName\": \"{mangled}\"",
            $"\"moduleName\": \"{module}\"",
            $"\"declAttributes\": [{string.Join(", ", (attrs ?? new[] { "AccessControl" }).Select(a => $"\"{a}\""))}]",
        };
        if (selfKind is not null) parts.Add($"\"funcSelfKind\": \"{selfKind}\"");
        if (isStatic) parts.Add("\"static\": true");
        if (throwing) parts.Add("\"throwing\": true");
        if (genericSig is not null) parts.Add($"\"genericSig\": \"{genericSig}\"");
        if (extra is not null) parts.Add(extra);
        return "{ " + string.Join(", ", parts) + " }";
    }

    private static string Accessor(string accessorKind, string mangled, IEnumerable<string> children, string[]? attrs = null) =>
        $$"""
        { "kind": "Accessor", "name": "{{char.ToUpperInvariant(accessorKind[0]) + accessorKind[1..]}}", "printedName": "{{accessorKind}}()",
          "children": [{{string.Join(", ", children)}}], "declKind": "Accessor", "mangledName": "{{mangled}}", "moduleName": "{{M}}",
          "declAttributes": [{{string.Join(", ", (attrs ?? Array.Empty<string>()).Select(a => $"\"{a}\""))}}], "accessorKind": "{{accessorKind}}" }
        """;

    private static string Var(string name, string mangledBase, string type, bool settable, string[]? setterAttrs = null, bool isStatic = false)
    {
        var accessors = new List<string> { Accessor("get", mangledBase + (isStatic ? "vgZ" : "vg"), new[] { type }) };
        if (settable)
            accessors.Add(Accessor("set", mangledBase + (isStatic ? "vsZ" : "vs"), new[] { Void, type }, setterAttrs));
        var stat = isStatic ? ", \"static\": true" : "";
        return $$"""
        { "kind": "Var", "name": "{{name}}", "printedName": "{{name}}", "children": [{{type}}], "declKind": "Var",
          "mangledName": "{{mangledBase}}vp", "moduleName": "{{M}}", "declAttributes": ["AccessControl"]{{stat}},
          "accessors": [{{string.Join(", ", accessors)}}] }
        """;
    }

    private static string TypeDecl(string declKind, string name, string mangled, IEnumerable<string> children, string[]? attrs = null, string? genericSig = null, string? extra = null, string module = M)
    {
        var gs = genericSig is null ? "" : $", \"genericSig\": \"{genericSig}\"";
        var ex = extra is null ? "" : ", " + extra;
        return $$"""
        { "kind": "TypeDecl", "name": "{{name}}", "printedName": "{{name}}", "children": [{{string.Join(", ", children)}}],
          "declKind": "{{declKind}}", "mangledName": "{{mangled}}", "moduleName": "{{module}}",
          "declAttributes": [{{string.Join(", ", (attrs ?? new[] { "AccessControl" }).Select(a => $"\"{a}\""))}}]{{gs}}{{ex}} }
        """;
    }

    private static (ModuleDecl Module, SwiftCallFactsTable Table, TypeDatabase Db) Parse(params string[] topLevel)
    {
        var db = new TypeDatabase();
        RegisterStandardTypes(db);
        return ParseInto(db, M, topLevel);
    }

    private static (ModuleDecl Module, SwiftCallFactsTable Table, TypeDatabase Db) ParseInto(TypeDatabase db, string moduleName, params string[] topLevel)
    {
        var json = $$"""
        { "ABIRoot": { "kind": "Root", "name": "{{moduleName}}", "printedName": "{{moduleName}}", "json_format_version": 8,
          "children": [{{string.Join(",\n", topLevel)}}] } }
        """;
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, json);
            var parser = new SwiftABIParser(path, db, CreateEmptyDemanglingResults(), NullLogger.Instance, SwiftInterfaceFacts.Empty);
            var module = parser.ParseModule().ModuleDecl;
            return (module, SwiftCallFactsTable.Build(module, db), db);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void RegisterStandardTypes(TypeDatabase db)
    {
        static (SwiftTypeName, TypeRecord) Rec(string name, TypeRecordKind kind, TypeRecordFlags flags)
        {
            var swiftName = SwiftTypeName.FromModuleQualifiedName(name);
            return (swiftName, new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Ns", swiftName.Name),
                SwiftTypeName = swiftName,
                MetadataAccessor = string.Empty,
                Flags = flags,
                Kind = kind,
            });
        }
        db.AddOutOfModuleTypes(new[]
        {
            Rec("Swift.Int32", TypeRecordKind.Struct, TypeRecordFlags.Frozen),
            Rec("Swift.Int", TypeRecordKind.Struct, TypeRecordFlags.Frozen),
            Rec("Swift.Bool", TypeRecordKind.Struct, TypeRecordFlags.Frozen),
            Rec("Swift.String", TypeRecordKind.Struct, TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement),
            Rec("Swift.Optional", TypeRecordKind.Enum, TypeRecordFlags.Frozen),
            Rec("Swift.Error", TypeRecordKind.Protocol, TypeRecordFlags.None),
        });
    }

    private static DemanglingResults CreateEmptyDemanglingResults()
    {
        var ctor = typeof(DemanglingResults).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, binder: null,
            [typeof(IReduction[]), typeof(HashSet<string>)], modifiers: null)!;
        return (DemanglingResults)ctor.Invoke([Array.Empty<IReduction>(), null]);
    }

    private static SwiftCallFacts Facts(SwiftCallFactsTable table, string symbol)
    {
        Assert.True(table.TryGet(symbol, out var facts), $"no facts for {symbol}");
        return facts!;
    }

    // ---- symbols used below ----

    private const string Point = "$s10TestModule5PointV";
    private const string Box = "$s10TestModule3BoxC";

    private static string PointDecl() => TypeDecl("Struct", "Point", Point, new[]
    {
        Func("scale", "scale(by:)", Point + "5scale2byys5Int32V_tF", new[] { Void, Int32 }, selfKind: "Mutating", attrs: new[] { "Mutating", "AccessControl" }),
        Func("length", "length()", Point + "6lengths5Int32VyF", new[] { Int32 }, selfKind: "NonMutating"),
        Func("take", "take()", Point + "4takeyyF", new[] { Void }, selfKind: "Consuming"),
        Func("peek", "peek()", Point + "4peekyyF", new[] { Void }, selfKind: "Borrowing"),
        Func("origin", "origin()", Point + "6originACyFZ", new[] { Nominal("TestModule.Point") }, isStatic: true),
        Func("init", "init(x:label:tag:)", Point + "1x5label3tagACs5Int32V_SSSSntcfC",
            new[] { Nominal("TestModule.Point"), Int32, Nominal("Swift.String", name: "String"), Nominal("Swift.String", "Shared", "String") },
            declKind: "Constructor", kind: "Constructor"),
        Func("bump", "bump(_:by:)", Point + "4bump_2byys5Int32Vz_ADtF",
            new[] { Void, Nominal("Swift.Int32", "InOut"), Nominal("Swift.Int32", "Owned") }, selfKind: "NonMutating"),
        Var("x", Point + "1xs5Int32V", Int32, settable: true),
        Var("mirror", Point + "6mirrors5Int32V", Int32, settable: true, setterAttrs: new[] { "NonMutating" }),
    }, attrs: new[] { "AccessControl", "Frozen" });

    private static string BoxDecl() => TypeDecl("Class", "Box", Box, new[]
    {
        Func("open", "open()", Box + "4openyyF", new[] { Void }),
        Func("make", "make()", Box + "4makeACyFZ", new[] { Nominal("TestModule.Box") }, isStatic: true),
        Func("init", "init(seed:)", Box + "4seedACs5Int32V_tcfc", new[] { Nominal("TestModule.Box"), Int32 }, declKind: "Constructor", kind: "Constructor"),
        Func("check", "check()", Box + "5checkyyKF", new[] { Void }, throwing: true),
        Var("count", Box + "5counts5Int32V", Int32, settable: true),
    });

    // ---- closures ----

    private static ClosureTypeSpec Closure(string spelling) => Assert.IsType<ClosureTypeSpec>(TypeSpecParser.Parse(spelling));

    [Fact]
    public void Closure_TakesConventionsTypesAndEffectsFromItsFunctionType()
    {
        var (_, table, _) = Parse(PointDecl(), BoxDecl());

        var facts = table.DescribeClosure(
            Closure("(inout Swift.Int32, __owned TestModule.Box, TestModule.Point) throws -> TestModule.Point"),
            "closure argument 'f' of $sOwner", [], M);

        Assert.True(facts.IsComplete, string.Join("; ", facts.Unknowns));
        Assert.Equal(SwiftCallTargetKind.ClosureInvocation, facts.Target.Kind);
        Assert.Equal(SwiftEntryKind.FunctionPointer, facts.Target.Entry);
        Assert.Equal("closure argument 'f' of $sOwner", facts.Id);
        Assert.Equal(SwiftCallSelfKind.ClosureContext, facts.Self.Kind);
        Assert.Equal(
            new[] { SwiftParameterConvention.Inout, SwiftParameterConvention.Owned, SwiftParameterConvention.Guaranteed },
            facts.Parameters.Select(p => p.Convention));
        Assert.Equal(
            new[] { SwiftConventionSource.Explicit, SwiftConventionSource.Explicit, SwiftConventionSource.Default },
            facts.Parameters.Select(p => p.ConventionSource));
        Assert.Equal(
            new[] { SwiftTypeCategory.Struct, SwiftTypeCategory.Class, SwiftTypeCategory.Struct },
            facts.Parameters.Select(p => p.Type.Category));
        Assert.Equal(SwiftTypeCategory.Struct, facts.Result.Type.Category);
        Assert.True(facts.Effects.Throws);
        Assert.False(facts.Effects.IsAsync);
    }

    [Theory]
    [InlineData("(borrowing TestModule.Box, Swift.Int32) -> Swift.Int32", SwiftParameterConvention.Guaranteed)]
    [InlineData("(__shared TestModule.Box, Swift.Int32) -> Swift.Int32", SwiftParameterConvention.Guaranteed)]
    [InlineData("(consuming TestModule.Box, Swift.Int32) -> Swift.Int32", SwiftParameterConvention.Owned)]
    public void Closure_AnOwnershipModifierAppliesToTheParameterItPrecedes(string spelling, SwiftParameterConvention expected)
    {
        var (_, table, _) = Parse(BoxDecl());

        var facts = table.DescribeClosure(Closure(spelling), "p", [], M);

        Assert.True(facts.IsComplete, string.Join("; ", facts.Unknowns));
        Assert.Equal(2, facts.Parameters.Length);
        Assert.Equal((expected, SwiftConventionSource.Explicit), (facts.Parameters[0].Convention, facts.Parameters[0].ConventionSource));
        Assert.Equal(SwiftTypeCategory.Class, facts.Parameters[0].Type.Category);
        Assert.Equal((SwiftParameterConvention.Guaranteed, SwiftConventionSource.Default), (facts.Parameters[1].Convention, facts.Parameters[1].ConventionSource));
    }

    [Fact]
    public void Closure_AGenericParameterResolvesOnlyWhereItIsInScope()
    {
        var (_, table, _) = Parse(PointDecl());
        var closure = Closure("(T) -> Swift.Int32");

        Assert.True(table.DescribeClosure(closure, "p", ["T"], M).IsComplete);
        Assert.False(table.DescribeClosure(closure, "p", [], M).IsComplete);
    }

    [Fact]
    public void Closure_ACFunctionPointerIsNotAClosureCall()
    {
        var (_, table, _) = Parse(PointDecl());

        var facts = table.DescribeClosure(Closure("@convention[c] (Swift.Int32) -> ()"), "p", [], M);

        Assert.Contains(facts.Unknowns, u => u.Contains("@convention(c)"));
    }

    // ---- self ----

    [Fact]
    public void ValueSelf_TakesItsMutationFromTheDeclaration()
    {
        var (_, table, _) = Parse(PointDecl());

        Assert.Equal(SwiftSelfMutation.Mutating, Facts(table, Point + "5scale2byys5Int32V_tF").Self.Mutation);
        Assert.Equal(SwiftSelfMutation.Nonmutating, Facts(table, Point + "6lengths5Int32VyF").Self.Mutation);
        Assert.Equal(SwiftSelfMutation.Consuming, Facts(table, Point + "4takeyyF").Self.Mutation);
        Assert.Equal(SwiftSelfMutation.Borrowing, Facts(table, Point + "4peekyyF").Self.Mutation);

        var length = Facts(table, Point + "6lengths5Int32VyF");
        Assert.Equal(SwiftCallSelfKind.Value, length.Self.Kind);
        Assert.Equal(SwiftTypeCategory.Struct, length.Self.Type!.Category);
        Assert.True(length.Self.Type.IsFrozen);
        Assert.Equal(SwiftSelfAbstraction.Concrete, length.Self.Abstraction);
    }

    [Fact]
    public void Setter_MutatesValueSelf_UnlessDeclaredNonmutating()
    {
        var (_, table, _) = Parse(PointDecl());

        var setter = Facts(table, Point + "1xs5Int32Vvs");
        Assert.Equal(SwiftCallTargetKind.Accessor, setter.Target.Kind);
        Assert.Equal(SwiftAccessorKind.Set, setter.Target.Accessor);
        Assert.Equal(SwiftSelfMutation.Mutating, setter.Self.Mutation);

        var nonmutating = Facts(table, Point + "6mirrors5Int32Vvs");
        Assert.Equal(SwiftSelfMutation.Nonmutating, nonmutating.Self.Mutation);

        var getter = Facts(table, Point + "1xs5Int32Vvg");
        Assert.Equal(SwiftAccessorKind.Get, getter.Target.Accessor);
        Assert.Equal(SwiftSelfMutation.Nonmutating, getter.Self.Mutation);
        Assert.Equal("x", getter.Target.MemberName);
    }

    [Fact]
    public void NonmutatingSetter_IsParsedFromTheAccessorAttribute()
    {
        var (module, _, _) = Parse(PointDecl());
        var point = Assert.Single(module.Types);
        var setters = point.Properties.ToDictionary(p => p.Name, p => p.Accessors.OfType<SetAccessorDecl>().Single().Method);

        Assert.True(setters["mirror"].IsNonmutating);
        Assert.False(setters["x"].IsNonmutating);
    }

    [Fact]
    public void StaticsAndInitializers_TakeTheRightMetatype()
    {
        var (_, table, _) = Parse(PointDecl(), BoxDecl());

        // A struct's static member and initializer take a thin metatype, which occupies no register.
        Assert.Equal(SwiftCallSelfKind.ThinMetatype, Facts(table, Point + "6originACyFZ").Self.Kind);
        var pointInit = Facts(table, Point + "1x5label3tagACs5Int32V_SSSSntcfC");
        Assert.Equal(SwiftCallTargetKind.Initializer, pointInit.Target.Kind);
        Assert.Equal(SwiftCallSelfKind.ThinMetatype, pointInit.Self.Kind);

        // A class's static member and allocating initializer take the thick metatype.
        Assert.Equal(SwiftCallSelfKind.ThickMetatype, Facts(table, Box + "4makeACyFZ").Self.Kind);
        var boxInit = Facts(table, Box + "4seedACs5Int32V_tcfC");
        Assert.Equal(SwiftCallSelfKind.ThickMetatype, boxInit.Self.Kind);
        Assert.Equal(SwiftTypeCategory.Class, boxInit.Result.Type.Category);

        var open = Facts(table, Box + "4openyyF");
        Assert.Equal(SwiftCallSelfKind.Reference, open.Self.Kind);
        Assert.Equal(SwiftCallTargetKind.Method, open.Target.Kind);
        Assert.Equal("TestModule.Box", open.Target.DeclaringType);
    }

    [Fact]
    public void FreeFunction_HasNoSelf()
    {
        var (_, table, _) = Parse(Func("ping", "ping()", "$s10TestModule4pingyyF", new[] { Void }));

        var ping = Facts(table, "$s10TestModule4pingyyF");
        Assert.Equal(SwiftCallTargetKind.FreeFunction, ping.Target.Kind);
        Assert.Equal(SwiftCallSelfKind.None, ping.Self.Kind);
        Assert.True(ping.Result.IsVoid);
        Assert.Empty(ping.Parameters);
        Assert.True(ping.IsComplete);
    }

    [Fact]
    public void ProtocolMembers_HaveAGenericSelf()
    {
        var shape = TypeDecl("Protocol", "Shape", "$s10TestModule5ShapeP", new[]
        {
            Func("area", "area()", "$s10TestModule5ShapeP4areas5Int32VyF", new[] { Int32 },
                genericSig: "<τ_0_0 where τ_0_0 : TestModule.Shape>", extra: "\"protocolReq\": true"),
        });
        var classBound = TypeDecl("Protocol", "Node", "$s10TestModule4NodeP", new[]
        {
            Func("visit", "visit()", "$s10TestModule4NodeP5visityyF", new[] { Void },
                genericSig: "<τ_0_0 where τ_0_0 : TestModule.Node>", extra: "\"protocolReq\": true"),
        }, genericSig: "<τ_0_0 where τ_0_0 : AnyObject>");

        var (module, table, _) = Parse(shape, classBound);

        var area = Facts(table, "$s10TestModule5ShapeP4areas5Int32VyF");
        Assert.Equal(SwiftCallSelfKind.Value, area.Self.Kind);
        Assert.Equal(SwiftSelfAbstraction.ProtocolRequirement, area.Self.Abstraction);
        Assert.Equal(SwiftTypeCategory.GenericParameter, area.Self.Type!.Category);
        Assert.Equal(new[] { "τ_0_0" }, area.Generics.Parameters);

        // A requirement is reached through its dispatch thunk, which takes the same arguments.
        var thunk = Facts(table, "$s10TestModule5ShapeP4areas5Int32VyFTj");
        Assert.Equal(SwiftEntryKind.DispatchThunk, thunk.Target.Entry);
        Assert.Equal("$s10TestModule5ShapeP4areas5Int32VyFTj", thunk.Target.Symbol);
        Assert.Equal(area.Parameters, thunk.Parameters);

        // A class-bound protocol's self is a single reference.
        Assert.True(module.Protocols.Single(p => p.Name == "Node").IsClassBound);
        var visit = Facts(table, "$s10TestModule4NodeP5visityyF");
        Assert.Equal(SwiftCallSelfKind.Reference, visit.Self.Kind);
        Assert.True(visit.Self.Type!.IsClassBound);
    }

    // ---- parameters ----

    [Fact]
    public void ParameterConventions_FollowTheDeclarationAndItsKind()
    {
        var (_, table, _) = Parse(PointDecl());

        // An initializer's parameters default to owned; an explicit __shared is guaranteed.
        var init = Facts(table, Point + "1x5label3tagACs5Int32V_SSSSntcfC");
        Assert.Equal(
            new[] { SwiftParameterConvention.Owned, SwiftParameterConvention.Owned, SwiftParameterConvention.Guaranteed },
            init.Parameters.Select(p => p.Convention));
        Assert.Equal(
            new[] { SwiftConventionSource.Default, SwiftConventionSource.Default, SwiftConventionSource.Explicit },
            init.Parameters.Select(p => p.ConventionSource));

        // A method's parameters default to guaranteed; inout and __owned are explicit.
        var bump = Facts(table, Point + "4bump_2byys5Int32Vz_ADtF");
        Assert.Equal(
            new[] { SwiftParameterConvention.Inout, SwiftParameterConvention.Owned },
            bump.Parameters.Select(p => p.Convention));

        var scale = Facts(table, Point + "5scale2byys5Int32V_tF");
        var by = Assert.Single(scale.Parameters);
        Assert.Equal(SwiftParameterConvention.Guaranteed, by.Convention);
        Assert.Equal(SwiftConventionSource.Default, by.ConventionSource);
        Assert.Equal(SwiftTypeCategory.Struct, by.Type.Category);
        Assert.Equal("Swift.Int32", by.Type.Spelling);

        // A setter's new value is owned.
        var setter = Facts(table, Point + "1xs5Int32Vvs");
        Assert.Equal(SwiftParameterConvention.Owned, Assert.Single(setter.Parameters).Convention);
    }

    [Fact]
    public void ClosureParameters_RecordWhetherTheyEscape()
    {
        const string escaping = """
            { "kind": "TypeFunc", "name": "Function", "printedName": "@escaping (Swift.Int32) -> ()",
              "children": [ { "kind": "TypeNominal", "name": "Void", "printedName": "()" },
                            { "kind": "TypeNominal", "name": "Int32", "printedName": "Swift.Int32" } ] }
            """;
        const string nonEscaping = """
            { "kind": "TypeFunc", "name": "Function", "printedName": "(Swift.Int32) -> ()", "typeAttributes": ["noescape"],
              "children": [ { "kind": "TypeNominal", "name": "Void", "printedName": "()" },
                            { "kind": "TypeNominal", "name": "Int32", "printedName": "Swift.Int32" } ] }
            """;
        var fn = Func("run", "run(_:_:)", "$s10TestModule3runyyys5Int32Vc_yADXEtF", new[] { Void, escaping, nonEscaping });

        var (_, table, _) = Parse(fn);

        var run = Facts(table, "$s10TestModule3runyyys5Int32Vc_yADXEtF");
        Assert.Equal(new[] { SwiftClosureKind.Escaping, SwiftClosureKind.NonEscaping }, run.Parameters.Select(p => p.Closure));
        Assert.All(run.Parameters, p => Assert.Equal(SwiftTypeCategory.Closure, p.Type.Category));
    }

    // ---- results and effects ----

    [Fact]
    public void Effects_RecordThrowsAndAsync()
    {
        // fetch() async throws -> Int32, with a real mangling so the demangler sees `async`.
        var fetch = Func("fetch", "fetch()", "$s10TestModule5fetchs5Int32VyYaKF", new[] { Int32 }, throwing: true);
        var (_, table, _) = Parse(fetch, BoxDecl());

        var facts = Facts(table, "$s10TestModule5fetchs5Int32VyYaKF");
        Assert.True(facts.Effects.Throws);
        Assert.True(facts.Effects.IsAsync);
        Assert.Null(facts.Effects.TypedThrows);
        Assert.Equal(SwiftTypeCategory.Struct, facts.Result.Type.Category);

        var check = Facts(table, Box + "5checkyyKF");
        Assert.True(check.Effects.Throws);
        Assert.False(check.Effects.IsAsync);
    }

    [Fact]
    public void FailableInitializer_IsMarked()
    {
        var optionalSelf = """
            { "kind": "TypeNominal", "name": "Optional", "printedName": "TestModule.Point?",
              "children": [ { "kind": "TypeNominal", "name": "Point", "printedName": "TestModule.Point" } ] }
            """;
        var point = TypeDecl("Struct", "Point", Point, new[]
        {
            Func("init", "init(raw:)", Point + "3rawACSgs5Int32V_tcfC", new[] { optionalSelf, Int32 }, declKind: "Constructor", kind: "Constructor"),
        }, attrs: new[] { "AccessControl", "Frozen" });

        var (_, table, _) = Parse(point);

        var init = Facts(table, Point + "3rawACSgs5Int32V_tcfC");
        Assert.True(init.Result.IsFailableInitializer);
        Assert.Equal(SwiftTypeCategory.Enum, init.Result.Type.Category);
    }

    // ---- generics ----

    [Fact]
    public void GenericParent_ScopesItsSignatureIntoEveryMember()
    {
        const string bag = "$s10TestModule3BagV";
        var decl = TypeDecl("Struct", "Bag", bag, new[]
        {
            Func("first", "first()", bag + "5firstxyF", new[] { """{ "kind": "TypeNominal", "name": "GenericTypeParam", "printedName": "τ_0_0" }""" },
                genericSig: "<τ_0_0 where τ_0_0 : Swift.Hashable>"),
            Var("size", bag + "4sizes5Int32V", Int32, settable: false),
        }, genericSig: "<τ_0_0 where τ_0_0 : Swift.Hashable>");

        var (_, table, _) = Parse(decl);

        var first = Facts(table, bag + "5firstxyF");
        Assert.Equal(SwiftSelfAbstraction.GenericParent, first.Self.Abstraction);
        Assert.Equal(SwiftTypeCategory.GenericParameter, first.Result.Type.Category);
        Assert.Equal(new[] { "τ_0_0" }, first.Generics.Parameters);
        var requirement = Assert.Single(first.Generics.Requirements);
        Assert.Equal(new SwiftGenericRequirementFacts("τ_0_0", "Swift.Hashable", SwiftGenericRequirementKind.Conformance), requirement);

        // The getter carries no signature of its own; its type's is the one in scope.
        var size = Facts(table, bag + "4sizes5Int32Vvg");
        Assert.Equal(new[] { "τ_0_0" }, size.Generics.Parameters);

        // The type's metadata accessor takes the same key arguments.
        var accessor = Facts(table, bag + "Ma");
        Assert.Equal(SwiftCallTargetKind.MetadataAccessor, accessor.Target.Kind);
        Assert.Equal(new[] { "τ_0_0" }, accessor.Generics.Parameters);
        Assert.Single(accessor.Generics.Requirements);
        Assert.Equal(SwiftCallSelfKind.None, accessor.Self.Kind);
        Assert.Equal(SwiftTypeCategory.MachineWord, Assert.Single(accessor.Parameters).Type.Category);
    }

    [Fact]
    public void StandardLibraryMetadataAccessors_AreListed()
    {
        var (_, table, _) = Parse(Func("ping", "ping()", "$s10TestModule4pingyyF", new[] { Void }));

        var optional = Facts(table, "$sSqMa");
        Assert.Equal("Swift.Optional", optional.Target.DeclaringType);
        Assert.Equal(new[] { "τ_0_0" }, optional.Generics.Parameters);
        Assert.Empty(optional.Generics.Requirements);

        var dictionary = Facts(table, "$sSDMa");
        Assert.Equal(new[] { "τ_0_0", "τ_0_1" }, dictionary.Generics.Parameters);
        Assert.Single(dictionary.Generics.Requirements);
    }

    // ---- target identity ----

    [Fact]
    public void Availability_IsTheHighestDeclaredFloorPerPlatform()
    {
        var (module, _, db) = Parse(BoxDecl());
        // Availability reaches the model from the .swiftinterface; set it as that pass would.
        static AvailabilityAnnotation Intro(string platform, string version, bool runtimeFloor = false) =>
            new(platform, version, null, null, false, false, null, null) { IsRuntimeSupportFloor = runtimeFloor };
        var box = Assert.Single(module.Types);
        box.AvailabilityAnnotations = new() { Intro("iOS", "15.0"), Intro("macOS", "12.0") };
        var openDecl = box.Methods.Single(m => m.Name == "open");
        openDecl.AvailabilityAnnotations = new() { Intro("iOS", "16.4"), Intro("tvOS", "17.0", runtimeFloor: true) };
        var table = SwiftCallFactsTable.Build(module, db);

        var open = Facts(table, Box + "4openyyF");
        Assert.Equal(
            new[] { new SwiftAvailabilityFloor("iOS", "16.4"), new SwiftAvailabilityFloor("macOS", "12.0") },
            open.Target.Availability);
        Assert.Equal(new[] { new SwiftAvailabilityFloor("iOS", "15.0"), new SwiftAvailabilityFloor("macOS", "12.0") },
            Facts(table, Box + "5checkyyKF").Target.Availability);
        Assert.Equal(M, open.Target.Module);
        Assert.Equal(Box + "4openyyF", open.Id);
    }

    [Fact]
    public void DispatchThunk_IsOnlyDerivedForDispatchedMembers()
    {
        var (_, table, _) = Parse(PointDecl(), BoxDecl());

        // A class method has a dispatch thunk; a struct method does not.
        Assert.Equal(SwiftEntryKind.DispatchThunk, Facts(table, Box + "4openyyFTj").Target.Entry);
        Assert.False(table.TryGet(Point + "6lengths5Int32VyFTj", out _));
        Assert.False(table.TryGet("$s10TestModule7missingyyF", out _));
    }

    [Fact]
    public void UnknownType_IsRecordedNotGuessed()
    {
        var fn = Func("use", "use(_:)", "$s10TestModule3useyy7Elsewhere6ThingVF", new[] { Void, Nominal("Elsewhere.Thing") });

        var (_, table, _) = Parse(fn);

        var facts = Facts(table, "$s10TestModule3useyy7Elsewhere6ThingVF");
        Assert.False(facts.IsComplete);
        Assert.Equal(SwiftTypeCategory.Unresolved, Assert.Single(facts.Parameters).Type.Category);
        Assert.Contains(facts.Unknowns, u => u.Contains("Elsewhere.Thing", StringComparison.Ordinal));
    }

    [Fact]
    public void TypedPointer_IsAFrozenWord_EvenWhereTheDatabaseListsItAsResilient()
    {
        // The shipped standard-library database records UnsafePointer and UnsafeMutablePointer
        // without the frozen flag; every pointer shape is still one direct word.
        var db = new TypeDatabase();
        RegisterStandardTypes(db);
        var pointerName = SwiftTypeName.FromModuleQualifiedName("Swift.UnsafePointer");
        db.AddOutOfModuleTypes(new[]
        {
            (pointerName, new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Ns", "UnsafePointer"),
                SwiftTypeName = pointerName,
                MetadataAccessor = string.Empty,
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Struct,
            }),
        });
        const string symbol = "$s10TestModule3useyySPySiGF";
        var fn = Func("use", "use(_:)", symbol, new[] { Void, Nominal("Swift.UnsafePointer<Swift.Int>", name: "UnsafePointer") });

        var (_, table, _) = ParseInto(db, M, fn);

        var parameter = Assert.Single(Facts(table, symbol).Parameters).Type;
        Assert.Equal(SwiftTypeCategory.Struct, parameter.Category);
        Assert.True(parameter.IsFrozen);
    }

    [Fact]
    public void ExtensionOfADependencyType_DefersToTheOwningModulesDeclaration()
    {
        const string depPoint = "$s3Dep5PointV";
        var db = new TypeDatabase();
        RegisterStandardTypes(db);
        var (dep, _, _) = ParseInto(db, "Dep",
            TypeDecl("Struct", "Point", depPoint, new[]
            {
                Func("norm", "norm()", depPoint + "4norms5Int32VyF", new[] { Int32 }, module: "Dep"),
            }, attrs: new[] { "AccessControl", "Frozen" }, module: "Dep"));
        db.AddDependencyModuleDecl(dep);
        // What the dependency's own module processing registers for the type it declares.
        var depPointName = SwiftTypeName.FromModuleQualifiedName("Dep.Point");
        var depDb = new ModuleTypeDatabase("Dep", "/fake/Dep");
        depDb.RegisterType(depPointName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Dep", "Point"),
            SwiftTypeName = depPointName,
            MetadataAccessor = depPoint + "Ma",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct,
        });
        db.AddModuleDatabase(depDb);

        // The main module extends Dep.Point: the digester writes it a declaration of the type
        // that carries only the extension's member, marked external.
        var (main, table, _) = ParseInto(db, M,
            Func("ping", "ping()", "$s10TestModule4pingyyF", new[] { Void }),
            TypeDecl("Struct", "Point", depPoint, new[]
            {
                Func("shifted", "shifted()", "$s3Dep5PointV10TestModuleE7shiftedACyF", new[] { Nominal("Dep.Point") }, extra: "\"isFromExtension\": true"),
                TypeDecl("Struct", "Tag", "$s3Dep5PointV10TestModuleE3TagV", Array.Empty<string>(), attrs: new[] { "AccessControl", "Frozen" }),
            }, attrs: new[] { "AccessControl", "Frozen" }, extra: "\"isExternal\": true", module: "Dep"));

        Assert.Equal(M, main.Name);
        Assert.Empty(table.Conflicts);

        // The metadata accessor is the owning module's.
        var accessor = Facts(table, depPoint + "Ma");
        Assert.Equal("Dep", accessor.Target.Module);

        // The extension member is the main module's, and its self is the owning declaration's type.
        var shifted = Facts(table, "$s3Dep5PointV10TestModuleE7shiftedACyF");
        Assert.Equal(SwiftCallSelfKind.Value, shifted.Self.Kind);
        Assert.True(shifted.Self.Type!.IsFrozen);
        Assert.True(shifted.Result.Type.IsFrozen);
        Assert.Equal(M, shifted.Target.Module);
        Assert.True(Facts(table, depPoint + "4norms5Int32VyF").IsComplete);

        // A type the main module declares inside that extension is the main module's, though its
        // name is qualified by the foreign module.
        var tag = Facts(table, "$s3Dep5PointV10TestModuleE3TagVMa");
        Assert.Equal(SwiftCallTargetKind.MetadataAccessor, tag.Target.Kind);
        Assert.Equal(M, tag.Target.Module);
    }

    [Fact]
    public void FactsCompareByValue()
    {
        var (_, first, _) = Parse(PointDecl(), BoxDecl());
        var (_, second, _) = Parse(PointDecl(), BoxDecl());

        Assert.Equal(first.Count, second.Count);
        foreach (var facts in first.All)
        {
            Assert.True(second.TryGet(facts.Target.Symbol!, out var other));
            Assert.Equal(facts, other);
            Assert.Equal(facts.GetHashCode(), other!.GetHashCode());
        }
        Assert.Empty(first.Conflicts);
    }

    // ---- review regressions: conventions, self, effects, types, thunks ----

    private const string NonEscapingVoid = """
        { "kind": "TypeFunc", "name": "Function", "printedName": "() -> ()", "typeAttributes": ["noescape"],
          "children": [ { "kind": "TypeNominal", "name": "Void", "printedName": "()" }, { "kind": "TypeNominal", "name": "Void", "printedName": "()" } ] }
        """;

    private static string Subscript(string mangledBase, string result, string index, string[]? getterAttrs = null) => $$"""
        { "kind": "Subscript", "name": "subscript", "printedName": "subscript(_:)", "children": [{{result}}, {{index}}],
          "declKind": "Subscript", "mangledName": "{{mangledBase}}cip", "moduleName": "{{M}}", "declAttributes": ["AccessControl"],
          "accessors": [{{Accessor("get", mangledBase + "cig", new[] { result, index }, getterAttrs)}},
                        {{Accessor("set", mangledBase + "cis", new[] { Void, result, index })}}] }
        """;

    [Fact]
    public void SetterAndInitializer_ConsumeTheirParameters_ExceptANonEscapingClosure()
    {
        var point = TypeDecl("Struct", "Point", Point, new[]
        {
            Subscript(Point + "ys5Int32VSS", Int32, StringT),
            Func("init", "init(build:)", Point + "5buildACyyXE_tcfC", new[] { Nominal("TestModule.Point"), NonEscapingVoid },
                declKind: "Constructor", kind: "Constructor"),
        }, attrs: new[] { "AccessControl", "Frozen" });

        var (_, table, _) = Parse(point);

        // A subscript setter consumes its index as well as its new value; its getter borrows the index.
        var setter = Facts(table, Point + "ys5Int32VSScis");
        Assert.Equal(new[] { SwiftParameterConvention.Owned, SwiftParameterConvention.Owned }, setter.Parameters.Select(p => p.Convention));
        Assert.Equal(SwiftParameterConvention.Guaranteed, Assert.Single(Facts(table, Point + "ys5Int32VSScig").Parameters).Convention);

        // A non-escaping closure is borrowed even by an initializer.
        var build = Assert.Single(Facts(table, Point + "5buildACyyXE_tcfC").Parameters);
        Assert.Equal(SwiftClosureKind.NonEscaping, build.Closure);
        Assert.Equal(SwiftParameterConvention.Guaranteed, build.Convention);
        Assert.Equal(SwiftConventionSource.Default, build.ConventionSource);
    }

    [Fact]
    public void MutatingSubscriptGetter_MutatesSelf_AndAgreesWithTheEmissionFlag()
    {
        var point = TypeDecl("Struct", "Point", Point, new[]
        {
            Subscript(Point + "ys5Int32VSS", Int32, StringT, getterAttrs: new[] { "Mutating" }),
        }, attrs: new[] { "AccessControl", "Frozen" });

        var (module, table, _) = Parse(point);

        Assert.Equal(SwiftSelfMutation.Mutating, Facts(table, Point + "ys5Int32VSScig").Self.Mutation);
        var getter = Assert.Single(module.Types).Subscripts.Single().Accessors.OfType<GetAccessorDecl>().Single().Method;
        Assert.True(getter.IsMutating);
    }

    [Fact]
    public void ClassBoundProtocol_KeepsAnExplicitSelfConvention()
    {
        var node = TypeDecl("Protocol", "Node", "$s10TestModule4NodeP", new[]
        {
            Func("take", "take()", "$s10TestModule4NodeP4takeyyF", new[] { Void }, selfKind: "Consuming",
                genericSig: "<τ_0_0 where τ_0_0 : TestModule.Node>", extra: "\"protocolReq\": true"),
        }, genericSig: "<τ_0_0 where τ_0_0 : AnyObject>");

        var (_, table, _) = Parse(node);

        Assert.Equal(SwiftSelfMutation.Consuming, Facts(table, "$s10TestModule4NodeP4takeyyF").Self.Mutation);
        Assert.Equal(SwiftSelfMutation.Consuming, Facts(table, "$s10TestModule4NodeP4takeyyFTj").Self.Mutation);
    }

    [Fact]
    public void ActorIsolatedSyncMethod_IsNotAsync()
    {
        const string counter = "$s10TestModule7CounterC";
        var decl = TypeDecl("Class", "Counter", counter, new[]
        {
            Func("increment", "increment()", counter + "9incrementyyF", new[] { Void }),
        }, extra: """ "conformances": [ { "kind": "Conformance", "name": "Actor", "printedName": "Actor", "usr": "s:ScA", "mangledName": "$sScA" } ] """);

        var (module, table, _) = Parse(decl);

        // The parser marks it async so the binding awaits the hop; the symbol itself is synchronous.
        Assert.True(Assert.Single(module.Types).Methods.Single(m => m.Name == "increment").IsAsync);
        Assert.False(Facts(table, counter + "9incrementyyF").Effects.IsAsync);
    }

    [Fact]
    public void TypeCategories_MetatypeOfAGenericOpaqueAndNestedBelowAGenericParent()
    {
        var shape = TypeDecl("Protocol", "Shape", "$s10TestModule5ShapeP", Array.Empty<string>());
        var metatype = """
            { "kind": "TypeNominal", "name": "Metatype", "printedName": "τ_0_0.Type",
              "children": [ { "kind": "TypeNominal", "name": "GenericTypeParam", "printedName": "τ_0_0" } ] }
            """;
        var typeName = Func("typeName", "typeName(of:)", "$s10TestModule8typeName2ofSSxm_tlF", new[] { StringT, metatype }, genericSig: "<τ_0_0>");
        var opaque = """
            { "kind": "TypeNominal", "name": "OpaqueTypeArchetype", "printedName": "some TestModule.Shape",
              "children": [ { "kind": "TypeNominal", "name": "Shape", "printedName": "TestModule.Shape" } ] }
            """;
        var make = Func("make", "make()", "$s10TestModule4makeQryF", new[] { opaque });
        var host = TypeDecl("Class", "Host", "$s10TestModule4HostC", new[]
        {
            TypeDecl("Struct", "Tag", "$s10TestModule4HostC3TagV", new[]
            {
                Func("init", "init(label:)", "$s10TestModule4HostC3TagV5labelAEyx_GSS_tcfC",
                    new[] { Nominal("TestModule.Host<τ_0_0>.Tag", name: "Tag"), StringT },
                    declKind: "Constructor", kind: "Constructor", genericSig: "<τ_0_0>"),
            }, attrs: new[] { "AccessControl", "Frozen" }),
        }, genericSig: "<τ_0_0>");

        var (_, table, _) = Parse(shape, typeName, make, host);

        Assert.Equal(SwiftTypeCategory.Metatype, Assert.Single(Facts(table, "$s10TestModule8typeName2ofSSxm_tlF").Parameters).Type.Category);
        Assert.Equal(SwiftTypeCategory.Opaque, Facts(table, "$s10TestModule4makeQryF").Result.Type.Category);
        var tag = Facts(table, "$s10TestModule4HostC3TagV5labelAEyx_GSS_tcfC").Result.Type;
        Assert.Equal(SwiftTypeCategory.Struct, tag.Category);
        Assert.True(tag.IsFrozen);
    }

    [Fact]
    public void VariadicParameter_IsMarked()
    {
        var array = $$"""{ "kind": "TypeNominal", "name": "Array", "printedName": "[Swift.Int32]", "children": [{{Int32}}] }""";
        var (_, table, _) = Parse(Func("sum", "sum(_:)", "$s10TestModule3sumyys5Int32Vd_tF", new[] { Void, array }));

        Assert.True(Assert.Single(Facts(table, "$s10TestModule3sumyys5Int32Vd_tF").Parameters).IsVariadic);
    }

    [Fact]
    public void VariadicParameter_ThatCannotBePlaced_IsRecordedNotGuessed()
    {
        var strings = $$"""{ "kind": "TypeNominal", "name": "Array", "printedName": "[Swift.String]", "children": [{{StringT}}] }""";
        const string symbol = Point + "6prefix6valuesACSaySSG_SSdtcfC";
        var (_, table, _) = Parse(TypeDecl("Struct", "Point", Point, new[]
        {
            Func("init", "init(prefix:values:)", symbol, new[] { Nominal("TestModule.Point"), strings, strings },
                declKind: "Constructor", kind: "Constructor"),
        }, attrs: new[] { "AccessControl", "Frozen" }));

        // An initializer has no reduced parameter list, and both of its parameters print as
        // `[String]`, so the parser knows only that one of them is `String...`.
        var facts = Facts(table, symbol);
        Assert.All(facts.Parameters, p => Assert.False(p.IsVariadic));
        Assert.False(facts.IsComplete);
        Assert.Contains(facts.Unknowns, u => u.Contains("variadic", StringComparison.Ordinal));
    }

    [Fact]
    public void Composition_IsClassBoundByASuperclass_AndUndescribedWithAnUnknownMember()
    {
        var baseClass = TypeDecl("Class", "Base", "$s10TestModule4BaseC", Array.Empty<string>());
        var marker = TypeDecl("Protocol", "Marker", "$s10TestModule6MarkerP", Array.Empty<string>());
        static string Composition(string members) =>
            $$"""{ "kind": "TypeNominal", "name": "ProtocolComposition", "printedName": "any {{members}}" }""";
        var useBase = Func("useBase", "useBase(_:)", "$s10TestModule7useBaseyyAA0C0CAA6MarkerpF",
            new[] { Void, Composition("TestModule.Base & TestModule.Marker") });
        var useMarker = Func("useMarker", "useMarker(_:)", "$s10TestModule9useMarkeryyAA0C0_pF",
            new[] { Void, Composition("TestModule.Marker & Swift.Error") });
        var useUnknown = Func("useUnknown", "useUnknown(_:)", "$s10TestModule10useUnknownyyAA6Marker_7Elsewhere5ThingpF",
            new[] { Void, Composition("TestModule.Marker & Elsewhere.Thing") });

        var (_, table, _) = Parse(baseClass, marker, useBase, useMarker, useUnknown);

        var withBase = Assert.Single(Facts(table, "$s10TestModule7useBaseyyAA0C0CAA6MarkerpF").Parameters).Type;
        Assert.Equal(SwiftTypeCategory.Existential, withBase.Category);
        Assert.True(withBase.IsClassBound);
        var protocolsOnly = Assert.Single(Facts(table, "$s10TestModule9useMarkeryyAA0C0_pF").Parameters).Type;
        Assert.Equal(SwiftTypeCategory.Existential, protocolsOnly.Category);
        Assert.False(protocolsOnly.IsClassBound);
        var unknown = Facts(table, "$s10TestModule10useUnknownyyAA6Marker_7Elsewhere5ThingpF");
        Assert.Equal(SwiftTypeCategory.Unresolved, Assert.Single(unknown.Parameters).Type.Category);
        Assert.False(unknown.IsComplete);
    }

    [Fact]
    public void PeekingATypeRecord_NeverThrows()
    {
        var db = new TypeDatabase();

        // Stripping the Foo/FooRef suffix leaves no type name.
        Assert.False(db.TryPeekTypeRecord(SwiftTypeName.FromModuleQualifiedName("CoreFoundation.Ref"), out _));
    }

    [Fact]
    public void MetadataAccessor_ReturnsATwoWordResponse()
    {
        var (_, table, _) = Parse(PointDecl());

        var response = Facts(table, Point + "Ma").Result.Type;
        Assert.Equal(SwiftTypeCategory.Struct, response.Category);
        Assert.True(response.IsFrozen);
    }

    [Fact]
    public void DispatchThunk_IsNotDerivedForFinalExtensionStaticOrInitializerMembers()
    {
        // The ABI marks a class's `static` member Final; a `class` member is not, and has a thunk.
        const string sealedBox = "$s10TestModule9SealedBoxC";
        var sealedDecl = TypeDecl("Class", "SealedBox", sealedBox, new[]
        {
            Func("open", "open()", sealedBox + "4openyyF", new[] { Void }),
        }, attrs: new[] { "AccessControl", "Final" });
        var box = TypeDecl("Class", "Box", Box, new[]
        {
            Func("open", "open()", Box + "4openyyF", new[] { Void }),
            Func("pinned", "pinned()", Box + "6pinnedyyF", new[] { Void }, attrs: new[] { "AccessControl", "Final" }),
            Func("extra", "extra()", Box + "5extrayyF", new[] { Void }, extra: "\"isFromExtension\": true"),
            Func("make", "make()", Box + "4makeACyFZ", new[] { Nominal("TestModule.Box") }, isStatic: true,
                attrs: new[] { "Final", "AccessControl" }),
            Func("report", "report()", Box + "6reportyyFZ", new[] { Void }, isStatic: true),
            Func("init", "init(seed:)", Box + "4seedACs5Int32V_tcfc", new[] { Nominal("TestModule.Box"), Int32 }, declKind: "Constructor", kind: "Constructor"),
        });

        var (_, table, _) = Parse(sealedDecl, box);

        Assert.True(Facts(table, Box + "4openyyF").Target.HasDispatchThunk);
        Assert.True(table.TryGet(Box + "4openyyFTj", out _));
        var classMember = Facts(table, Box + "6reportyyFZTj");
        Assert.Equal(SwiftEntryKind.DispatchThunk, classMember.Target.Entry);
        Assert.Equal(SwiftCallSelfKind.ThickMetatype, classMember.Self.Kind);
        foreach (var symbol in new[] { sealedBox + "4openyyF", Box + "6pinnedyyF", Box + "5extrayyF", Box + "4makeACyFZ", Box + "4seedACs5Int32V_tcfC" })
        {
            Assert.False(Facts(table, symbol).Target.HasDispatchThunk, symbol);
            Assert.False(table.TryGet(symbol + "Tj", out _), symbol + "Tj");
        }
    }

    [Fact]
    public void BuildingFacts_RecordsNoPackageReference()
    {
        // A type a dependency's database knows, named by a member the binding may never emit.
        var db = new TypeDatabase();
        RegisterStandardTypes(db);
        var thing = SwiftTypeName.FromModuleQualifiedName("Elsewhere.Thing");
        db.AddOutOfModuleTypes(new[]
        {
            (thing, new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Elsewhere", "Thing"),
                SwiftTypeName = thing,
                MetadataAccessor = string.Empty,
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct,
            }),
        });
        var (module, _, _) = ParseInto(db, M, Func("use", "use(_:)", "$s10TestModule3useyy7Elsewhere5ThingVF", new[] { Void, Nominal("Elsewhere.Thing") }));

        CrossModuleBindingReferences.Reset();
        AppleSupplementReferences.Reset();
        var table = SwiftCallFactsTable.Build(module, db);

        Assert.True(Facts(table, "$s10TestModule3useyy7Elsewhere5ThingVF").IsComplete);
        Assert.Empty(CrossModuleBindingReferences.Current);
        Assert.False(AppleSupplementReferences.Any);
    }

    [Fact]
    public void InjectedProtocolExtensionMember_HasNoDeclaredFacts()
    {
        var (module, _, db) = Parse(PointDecl());
        var point = Assert.Single(module.Types);
        point.Methods.Add(new MethodDecl
        {
            Name = "describe",
            MangledName = "SBW_TestModule_Point_describe",
            CSSignature = new List<ArgumentDecl>
            {
                new() { SwiftTypeSpec = TupleTypeSpec.Empty, Name = "", PrivateName = "", IsInOut = false, IsGeneric = false, ParentDecl = point, ModuleDecl = module },
            },
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = false,
            IsAsync = false,
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = point,
            ModuleDecl = module,
            IsProtocolExtensionMethod = true,
        });

        var table = SwiftCallFactsTable.Build(module, db);

        Assert.False(table.TryGet("SBW_TestModule_Point_describe", out _));
    }

    // ---- coverage ----

    private static AbiCallPlan Plan(string entryPoint, string library, PInvokeCallingConvention convention = PInvokeCallingConvention.Swift) => new()
    {
        MethodName = "PInvoke_" + entryPoint.GetHashCode().ToString("x"),
        EntryPoint = entryPoint,
        Library = library,
        CallingConvention = convention,
        ReturnCarrier = "void",
        ParameterCarriers = ImmutableArray<string>.Empty,
    };

    [Fact]
    public void Coverage_ClassifiesEverySwiftConventionPlan()
    {
        var (_, table, _) = Parse(PointDecl(), BoxDecl(), Func("use", "use(_:)", "$s10TestModule3useyy7Elsewhere6ThingVF", new[] { Void, Nominal("Elsewhere.Thing") }));
        const string lib = "TestModule.framework/TestModule";
        const string wrapper = "TestModuleSwiftBindings";

        var coverage = SwiftCallFactsCoverage.Compute(M, new[]
        {
            Plan(Box + "4openyyF", lib),
            Plan(Box + "4openyyFTj", lib),
            Plan(Point + "Ma", lib),
            Plan("$sSqMa", "/usr/lib/swift/libswiftCore.dylib"),
            Plan("$s10TestModule3useyy7Elsewhere6ThingVF", lib),
            Plan("SBSW_Point_helper_1234", wrapper + ".framework/" + wrapper),
            Plan("$s10TestModule9somethingyyF", lib),
            Plan("SBW_cdecl_entry", wrapper, PInvokeCallingConvention.Cdecl),
        }, table, wrapper);

        Assert.Equal(7, coverage.Entries.Length);
        Assert.Equal(4, coverage.Count(SwiftCallFactsStatus.Complete));
        Assert.Equal(1, coverage.Count(SwiftCallFactsStatus.Incomplete));
        Assert.Equal(1, coverage.Count(SwiftCallFactsStatus.Synthetic));
        Assert.Equal(1, coverage.Count(SwiftCallFactsStatus.Unknown));
        Assert.All(coverage.Entries.Where(e => e.Status is SwiftCallFactsStatus.Unknown or SwiftCallFactsStatus.Incomplete),
            e => Assert.NotEmpty(e.Reasons));

        using var doc = JsonDocument.Parse(coverage.ToJson());
        var root = doc.RootElement;
        Assert.Equal(SwiftCallFactsCoverage.SchemaVersion, root.GetProperty("schema").GetInt32());
        Assert.Equal(SwiftCallFacts.CurrentVersion, root.GetProperty("factsVersion").GetInt32());
        Assert.Equal(7, root.GetProperty("totals").GetProperty("swiftTransitions").GetInt32());
        Assert.Equal("DispatchThunk", root.GetProperty("facts").EnumerateArray()
            .Single(f => f.GetProperty("id").GetString() == Box + "4openyyFTj")
            .GetProperty("target").GetProperty("entry").GetString());

        // Every declared symbol is indexed, including those no plan cites, but a derived dispatch thunk is not.
        var declared = root.GetProperty("declaredSymbols");
        Assert.Equal("Complete", declared.GetProperty(Point + "Ma").GetString());
        Assert.Equal("Incomplete", declared.GetProperty("$s10TestModule3useyy7Elsewhere6ThingVF").GetString());
        Assert.True(declared.TryGetProperty(Box + "5checkyyKF", out _), "a symbol no plan cites is still indexed");
        Assert.False(declared.TryGetProperty(Box + "4openyyFTj", out _));
        Assert.Equal(table.Count, root.GetProperty("totals").GetProperty("declaredSymbols").GetInt32());
        Assert.Equal(1, root.GetProperty("totals").GetProperty("declaredIncomplete").GetInt32());

        // Every incomplete declared symbol carries its reasons, even when no plan cites it.
        var uncited = SwiftCallFactsCoverage.Compute(M, Array.Empty<AbiCallPlan>(), table, wrapper);
        var use = Assert.Single(uncited.Facts);
        Assert.Equal("$s10TestModule3useyy7Elsewhere6ThingVF", use.Id);
        Assert.NotEmpty(use.Unknowns);

        // Deterministic: the same inputs serialize identically.
        Assert.Equal(coverage.ToJson(), SwiftCallFactsCoverage.Compute(M, coverage.Entries.Select(e => Plan(e.EntryPoint, e.Library)), table, wrapper).ToJson());
    }
}
