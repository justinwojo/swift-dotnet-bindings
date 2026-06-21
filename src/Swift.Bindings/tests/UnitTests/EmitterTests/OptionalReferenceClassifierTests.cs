// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Pins the load-bearing asymmetry between two distinct optional-reference ABI questions.
///
/// <para>
/// <see cref="OptionalReferenceClassifier.UsesNullablePointerAbi"/> is the PRODUCER-position oracle
/// (<see cref="WrapperValidation.IsOptionalWithReferenceInner"/>): "can wrapper code that explicitly
/// bridges (a witness getter's <c>passRetained(... as AnyObject)</c>, an <c>@_cdecl</c> return) present
/// this optional as a nullable reference?" It is intentionally WIDER than the closure-position predicate
/// — it also classifies an <c>Optional&lt;ObjC-bridgeable value&gt;</c> and the no-<c>TypeRecord</c>
/// Apple/concrete-class fallbacks as nullable-pointer ABI, because the wrapper materialises the pointer.
/// </para>
///
/// <para>
/// <see cref="ClosureHandler.IsOptionalReferenceArg"/> is the CONSUMER-position gate for a closure's
/// native argument/return slot, where there is no Swift-side bridge. It is narrower (true reference
/// inners only). Feeding the wider oracle into a closure slot reads an <c>Optional&lt;value-type&gt;</c>
/// (e.g. <c>URL?</c>) as an object pointer and SIGABRTs at runtime — which is why these two positions
/// must use different predicates.
/// </para>
/// </summary>
public class OptionalReferenceClassifierTests
{
    private const string PureClass = "TestModule.PureSwiftClass";
    private const string ObjCRootedClass = "TestModule.ObjCRootedClass";
    private const string ObjCBridgedClass = "TestModule.ObjCBridgedClass";
    private const string BridgeableValue = "TestModule.BridgeableValue";
    private const string Primitive = "Swift.Int32";
    private const string SimpleEnum = "TestModule.SimpleColor";
    private const string Bool = "Swift.Bool";

    private static TypeDatabase CreateTypeDatabase()
    {
        var typeDatabase = new TypeDatabase();

        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int32"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Int32"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int32"),
                MetadataAccessor = "$ss5Int32VMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Bool"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Boolean"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Bool"),
                MetadataAccessor = "$sSbMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        // Pure-Swift class — IsClassType true, owning +1 via MarshalBorrowedClassFromSwift.
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName(PureClass),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "PureSwiftClass"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName(PureClass),
                MetadataAccessor = "$s10TestModule14PureSwiftClassCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        // @objc:NSObject-rooted generator-bound class — no native remap (NativeTypeName null),
        // owning +1 via MarshalCallbackArg's Kind==Class upgrade.
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName(ObjCRootedClass),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "ObjCRootedClass"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName(ObjCRootedClass),
                MetadataAccessor = "$s10TestModule15ObjCRootedClassCMa",
                Flags = TypeRecordFlags.ObjCRooted | TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        // ObjC-bridged class (Microsoft.iOS-remapped peer) — borrowed via FormatObjCBridgeCall.
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName(ObjCBridgedClass),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "ObjCBridgedClass"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName(ObjCBridgedClass),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.ObjCBridged | TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        // ObjC-bridgeable VALUE type (e.g., Foundation.URL shape): ObjCBridgeable flag, NOT
        // ObjCBridged — crosses @_cdecl as an object pointer (nullable-pointer ABI) but is a value.
        // This is the intended correction: the oracle says true, old IsReferenceType says false.
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName(BridgeableValue),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "BridgeableValue"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName(BridgeableValue),
                MetadataAccessor = "$s10TestModule15BridgeableValueVMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.ObjCBridgeable,
                Kind = TypeRecordKind.Struct
            });
        // No-payload (simple) enum.
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName(SimpleEnum),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "SimpleColor"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName(SimpleEnum),
                MetadataAccessor = "$s10TestModule11SimpleColorOMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.SimpleEnum,
                Kind = TypeRecordKind.Enum
            });
        typeDatabase.AddModuleDatabase(testModule);

        return typeDatabase;
    }

    private static NamedTypeSpec Optional(string innerName)
    {
        var opt = new NamedTypeSpec("Swift.Optional");
        opt.GenericParameters.Add(new NamedTypeSpec(innerName));
        return opt;
    }

    // ───────────────────────── ABI axis ─────────────────────────

    [Theory]
    [InlineData(PureClass, true)]
    [InlineData(ObjCRootedClass, true)]
    [InlineData(ObjCBridgedClass, true)]
    [InlineData(BridgeableValue, true)]   // intended correction (oracle is wider)
    [InlineData(Primitive, false)]
    [InlineData(SimpleEnum, false)]
    [InlineData(Bool, false)]
    public void UsesNullablePointerAbi_MatchesCanonicalOracle(string innerName, bool expected)
    {
        var typeDb = CreateTypeDatabase();
        var optional = Optional(innerName);

        Assert.Equal(expected, OptionalReferenceClassifier.UsesNullablePointerAbi(optional, typeDb));
        // It must BE the oracle, not a parallel re-implementation.
        Assert.Equal(
            WrapperValidation.IsOptionalWithReferenceInner(optional, typeDb),
            OptionalReferenceClassifier.UsesNullablePointerAbi(optional, typeDb));
    }

    [Fact]
    public void UsesNullablePointerAbi_BridgeableValue_WiderThanClosureReferencePredicate()
    {
        // The load-bearing divergence: a bridgeable VALUE type lowers to a nullable pointer at a
        // producer position (oracle = true, the wrapper bridges via `as AnyObject`) but is NOT a
        // reference in a closure slot (IsReferenceType = false). The two positions read it differently.
        var typeDb = CreateTypeDatabase();
        var closureHandler = new ClosureHandler(typeDb);
        var inner = new NamedTypeSpec(BridgeableValue);

        Assert.True(OptionalReferenceClassifier.UsesNullablePointerAbi(Optional(BridgeableValue), typeDb));
        Assert.False(closureHandler.IsReferenceType(inner));
    }

    [Fact]
    public void UsesNullablePointerAbi_NoTypeRecordConcreteClassFallback_DiffersFromOldReferenceTypePredicate()
    {
        // No-TypeRecord Apple concrete-class fallback (RealityFoundation.Entity): oracle Path 3
        // catches it, the closure-local IsClassType (which requires a TypeRecord) does not.
        var typeDb = CreateTypeDatabase();
        var closureHandler = new ClosureHandler(typeDb);
        var inner = new NamedTypeSpec("RealityFoundation.Entity");

        Assert.True(OptionalReferenceClassifier.UsesNullablePointerAbi(Optional("RealityFoundation.Entity"), typeDb));
        Assert.False(closureHandler.IsClassType(inner));
    }

    // ──────────────── Closure-arg gate (the narrow consumer-position predicate) ────────────────

    [Theory]
    [InlineData(PureClass, true)]
    [InlineData(ObjCRootedClass, true)]
    [InlineData(ObjCBridgedClass, true)]
    [InlineData(BridgeableValue, false)]   // value type: a closure slot carries its value, not a pointer
    [InlineData(Primitive, false)]
    [InlineData(SimpleEnum, false)]
    [InlineData(Bool, false)]
    public void IsOptionalReferenceArg_TrueForReferenceInnersOnly(string innerName, bool expected)
    {
        var typeDb = CreateTypeDatabase();
        var closureHandler = new ClosureHandler(typeDb);

        Assert.Equal(expected, closureHandler.IsOptionalReferenceArg(Optional(innerName)));
    }

    [Fact]
    public void IsOptionalReferenceArg_ExcludesBridgeableValueThatProducerOracleIncludes()
    {
        // The fix in one assertion: the bridgeable VALUE type is nullable-pointer ABI at a producer
        // position (oracle = true) but the closure-arg gate excludes it (false). Including it here
        // would emit GetNSObject<T> over a Swift value buffer → _objc_fatal at runtime.
        var typeDb = CreateTypeDatabase();
        var closureHandler = new ClosureHandler(typeDb);

        Assert.True(OptionalReferenceClassifier.UsesNullablePointerAbi(Optional(BridgeableValue), typeDb));
        Assert.False(closureHandler.IsOptionalReferenceArg(Optional(BridgeableValue)));
    }

    [Fact]
    public void IsObjCRootedClass_DistinguishesRootedFromBridgedAndPure()
    {
        var typeDb = CreateTypeDatabase();
        var closureHandler = new ClosureHandler(typeDb);

        Assert.True(closureHandler.IsObjCRootedClass(new NamedTypeSpec(ObjCRootedClass)));
        Assert.False(closureHandler.IsObjCRootedClass(new NamedTypeSpec(PureClass)));
        Assert.False(closureHandler.IsObjCRootedClass(new NamedTypeSpec(ObjCBridgedClass)));
        Assert.False(closureHandler.IsObjCRootedClass(new NamedTypeSpec(BridgeableValue)));
    }
}
