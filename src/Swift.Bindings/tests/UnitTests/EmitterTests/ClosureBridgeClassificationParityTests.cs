// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Pins the single-source closure-argument classification that <see cref="MethodClosureBridge"/> and
/// <see cref="NestedClosureBridge"/> SHARE, so the two bridges can never drift apart on whether a
/// closure argument is an ObjC-bridged reference, a pure-Swift class, or an unresolvable
/// (no-<c>TypeRecord</c>) fallback.
///
/// <para>
/// The convergence has two load-bearing sources, and both bridges read both:
/// <list type="bullet">
/// <item><b>Non-closure params</b> route through <see cref="MethodClosureBridge.ClassifyParam"/> —
/// <see cref="NestedClosureBridge"/> calls it directly (its <c>GetNonClosureParamCSharpType</c> and
/// outer-arg classification delegate to the same static method), so the ObjC-bridged → <c>ObjCHandle</c>,
/// pure-class → <c>PayloadHandle</c>, no-record → <c>Unsupported</c> decisions are identical by
/// construction.</item>
/// <item><b>Closure args</b> route through the shared <see cref="ClosureHandler"/> predicates
/// (<see cref="ClosureHandler.IsObjCBridgedClass"/>, <see cref="ClosureHandler.IsClassType"/>,
/// <see cref="ClosureHandler.IsOptionalReferenceArg"/>): each bridge's marshalling helper branches on
/// the same predicate to pick the <c>.Handle</c> (ObjC-bridged) vs <c>.Payload</c> (pure-Swift) vs
/// identity-fallback arm.</item>
/// </list>
/// </para>
///
/// <para>
/// These tests fix the EXPECTED classification of an <c>Optional&lt;ObjC-bridged class&gt;</c>, an
/// <c>Optional&lt;pure-Swift class&gt;</c>, and a no-<c>TypeRecord</c> type at that shared decision
/// point. They are the durable guard against reintroducing a bridge-local predicate (the divergent
/// <c>GetCSharpPrimitiveType</c>/<c>GetCallbackParamType</c> tables that the closure two-axis
/// convergence deleted): a fresh divergent classifier in one bridge would re-answer one of these inputs
/// and break the shared contract these assertions lock.
/// </para>
/// </summary>
public class ClosureBridgeClassificationParityTests
{
    private const string ObjCBridgedClass = "Foundation.NSError";
    private const string PureSwiftClass = "TestModule.PureSwiftClass";
    // Deliberately never registered: exercises the no-TypeRecord fallback both bridges must agree on.
    private const string NoRecordType = "UnknownModule.Unresolvable";

    private static TypeDatabase CreateTypeDatabase()
    {
        var typeDatabase = new TypeDatabase();

        var foundationModule = new ModuleTypeDatabase("Foundation", "/usr/lib/swift/libswiftFoundation.dylib");
        // ObjC-bridged class: IsObjCBridgedClass true, IsClassType false (the predicate excludes
        // ObjC-bridged/rooted), ClassifyParam → ObjCHandle. Closure arg → .Handle arm.
        foundationModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName(ObjCBridgedClass),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Foundation", "NSError"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName(ObjCBridgedClass),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.ObjCBridged | TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        typeDatabase.AddModuleDatabase(foundationModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        // Pure-Swift class: IsClassType true, IsObjCBridgedClass false, ClassifyParam → PayloadHandle.
        // Closure arg → .Payload arm. The contrast that keeps the ObjC-bridged assertions non-vacuous.
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName(PureSwiftClass),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "PureSwiftClass"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName(PureSwiftClass),
                MetadataAccessor = "$s10TestModule14PureSwiftClassCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        typeDatabase.AddModuleDatabase(testModule);

        return typeDatabase;
    }

    private static ModuleDecl CreateModuleDecl(string name) => new ModuleDecl
    {
        Name = name,
        Properties = new List<PropertyDecl>(),
        Methods = new List<MethodDecl>(),
        Types = new List<TypeDecl>(),
        Dependencies = new List<string>(),
        Protocols = new List<ProtocolDecl>(),
        ParentDecl = null,
        ModuleDecl = null
    };

    private static ArgumentDecl CreateArgument(string name, TypeSpec typeSpec, ModuleDecl moduleDecl) => new ArgumentDecl
    {
        SwiftTypeSpec = typeSpec,
        Name = name,
        PrivateName = string.Empty,
        IsInOut = false,
        IsGeneric = false,
        ParentDecl = null,
        ModuleDecl = moduleDecl
    };

    private static NamedTypeSpec Optional(string innerName) =>
        new NamedTypeSpec("Swift.Optional", new NamedTypeSpec(innerName));

    // ─── Non-closure param classifier (MethodClosureBridge.ClassifyParam) ───
    // NestedClosureBridge delegates to this exact static method, so a single assertion fixes both
    // bridges' decision for a non-closure parameter of each shape.

    // ParamAbiCategory is internal, so it cannot appear in a [Theory]'s public signature (CS0051) —
    // the three shapes are asserted inline where the enum stays in the method body.
    [Fact]
    public void ClassifyParam_SharedByBothBridges_ProducesExpectedCategory()
    {
        var typeDb = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");

        Assert.Equal(
            MethodClosureBridge.ParamAbiCategory.ObjCHandle,
            MethodClosureBridge.ClassifyParam(CreateArgument("p", new NamedTypeSpec(ObjCBridgedClass), moduleDecl), typeDb));
        Assert.Equal(
            MethodClosureBridge.ParamAbiCategory.PayloadHandle,
            MethodClosureBridge.ClassifyParam(CreateArgument("p", new NamedTypeSpec(PureSwiftClass), moduleDecl), typeDb));
        Assert.Equal(
            MethodClosureBridge.ParamAbiCategory.Unsupported,
            MethodClosureBridge.ClassifyParam(CreateArgument("p", new NamedTypeSpec(NoRecordType), moduleDecl), typeDb));
    }

    // ─── Closure-arg predicates (shared ClosureHandler) ───
    // Both bridges branch their closure-arg marshalling on these three predicates: an ObjC-bridged
    // inner takes the .Handle arm, a pure-Swift class takes the .Payload arm, and a no-TypeRecord type
    // takes neither (identity fallback). Pinning the predicate outcomes pins which arm each bridge picks.

    [Fact]
    public void OptionalObjCBridgedReference_BothBridgesSeeNullableObjCHandleArm()
    {
        var closureHandler = new ClosureHandler(CreateTypeDatabase());
        var inner = new NamedTypeSpec(ObjCBridgedClass);

        // Optional<ObjC-bridged class> IS a nullable reference arg (drives the nil-check + handle arm)…
        Assert.True(closureHandler.IsOptionalReferenceArg(Optional(ObjCBridgedClass)));
        // …and the inner resolves on the ObjC-bridged predicate (the .Handle arm), NOT the pure-Swift
        // IsClassType arm (the .Payload arm) — the two arms are mutually exclusive for a bridged class.
        Assert.True(closureHandler.IsObjCBridgedClass(inner));
        Assert.False(closureHandler.IsClassType(inner));
    }

    [Fact]
    public void OptionalPureSwiftClass_BothBridgesSeeNullablePayloadArm()
    {
        var closureHandler = new ClosureHandler(CreateTypeDatabase());
        var inner = new NamedTypeSpec(PureSwiftClass);

        // Optional<pure-Swift class> is also a nullable reference arg, but routes to the .Payload arm:
        // IsObjCBridgedClass false, IsClassType true. Keeps the ObjC-bridged assertions above honest.
        Assert.True(closureHandler.IsOptionalReferenceArg(Optional(PureSwiftClass)));
        Assert.False(closureHandler.IsObjCBridgedClass(inner));
        Assert.True(closureHandler.IsClassType(inner));
    }

    [Fact]
    public void NoTypeRecordType_BothBridgesAgreeNonReferenceFallback()
    {
        var closureHandler = new ClosureHandler(CreateTypeDatabase());
        var inner = new NamedTypeSpec(NoRecordType);

        // A type the database can't resolve is neither ObjC-bridged nor a class, so an Optional over it
        // is NOT a reference arg — both bridges fall through to the identity marshal rather than reading
        // a void* slot as an object pointer (the no-TypeRecord fallback the two must answer identically).
        Assert.False(closureHandler.IsObjCBridgedClass(inner));
        Assert.False(closureHandler.IsClassType(inner));
        Assert.False(closureHandler.IsOptionalReferenceArg(Optional(NoRecordType)));
    }
}
