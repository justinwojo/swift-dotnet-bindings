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
    private const string NestedStructLeaf = "NestedValue";
    private const string NestedClassLeaf = "NestedRef";
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
        // Nested declarations under the (generic) pure-Swift class. The parser hangs
        // `PureSwiftClass<T>.NestedValue` off the OUTER segment — arguments on the outer, the leaf in
        // InnerType — so these two say whether the flavour is read from the leaf or from the segment
        // that happens to carry the arguments.
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName($"{PureClass}.{NestedStructLeaf}"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "PureSwiftClass.NestedValue"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{PureClass}.{NestedStructLeaf}"),
                MetadataAccessor = "$s10TestModule14PureSwiftClassC11NestedValueVMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName($"{PureClass}.{NestedClassLeaf}"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "PureSwiftClass.NestedRef"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{PureClass}.{NestedClassLeaf}"),
                MetadataAccessor = "$s10TestModule14PureSwiftClassC9NestedRefCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
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

    // ──────────── Borrowed callback-arg reader (the one classifier both lanes share) ────────────

    /// <summary>
    /// Each reference flavour needs its OWN reader off the borrowed object pointer Swift stores in the
    /// slot. An ObjC-bridged peer (a Microsoft.iOS binding with no Swift type-metadata record, not
    /// ISwiftObject) is the one that cannot use the Swift-payload marshal: <c>MarshalCallbackArg</c>
    /// falls through to <c>MarshalFromSwift</c>'s NSObject arm, which does <c>Marshal.ReadIntPtr</c> —
    /// treating the OBJECT pointer as the ADDRESS OF a slot holding one and wrapping its isa word.
    /// </summary>
    [Theory]
    [InlineData(PureClass, "SwiftMarshal.MarshalBorrowedClassFromSwift<Cs>(__p0)")]
    [InlineData(ObjCRootedClass, "SwiftMarshal.MarshalCallbackArg<Cs>(__p0)")]
    [InlineData(ObjCBridgedClass, "ObjCRuntime.Runtime.GetNSObject<Cs>(__p0)")]
    [InlineData(BridgeableValue, "SwiftMarshal.MarshalCallbackArg<Cs>(__p0)")]
    [InlineData(Primitive, "SwiftMarshal.MarshalCallbackArg<Cs>(__p0)")]
    public void BorrowedCallbackArgMarshal_RoutesEachReferenceFlavourToItsOwnReader(
        string typeName, string expected)
    {
        var closureHandler = new ClosureHandler(CreateTypeDatabase());

        Assert.Equal(expected,
            closureHandler.BorrowedCallbackArgMarshal(new NamedTypeSpec(typeName), "Cs", "__p0"));
    }

    /// <summary>
    /// The defect this classifier closes: an ObjC-bridged peer must NEVER be read with the Swift-payload
    /// marshal, in either the optional or the non-optional lane. Asserted as a property of the emitted
    /// expression rather than as an exact string, so a future change of bridge helper still passes.
    /// </summary>
    [Fact]
    public void BorrowedCallbackArgMarshal_ObjCBridgedPeer_NeverUsesSwiftPayloadMarshal()
    {
        var closureHandler = new ClosureHandler(CreateTypeDatabase());

        var expr = closureHandler.BorrowedCallbackArgMarshal(
            new NamedTypeSpec(ObjCBridgedClass), "TestModule.ObjCBridgedClass", "new IntPtr(arg1)");

        Assert.DoesNotContain("MarshalCallbackArg", expr);
        Assert.DoesNotContain("MarshalFromSwift", expr);
        Assert.Contains("new IntPtr(arg1)", expr);
    }

    /// <summary>
    /// The non-optional lane emits into a non-nullable delegate parameter, so its bridge call carries
    /// the null-forgiving <c>!</c>; the optional lane guards the pointer itself and must not.
    /// </summary>
    [Fact]
    public void BorrowedCallbackArgMarshal_NonNullObjCBridge_AppendsNullForgiving()
    {
        var closureHandler = new ClosureHandler(CreateTypeDatabase());
        var argType = new NamedTypeSpec(ObjCBridgedClass);

        Assert.EndsWith("!", closureHandler.BorrowedCallbackArgMarshal(
            argType, "Cs", "__p0", nonNullObjCBridge: true));
        Assert.DoesNotContain("!", closureHandler.BorrowedCallbackArgMarshal(argType, "Cs", "__p0"));
    }

    /// <summary>
    /// The closure bridges do not agree on what a REFERENCE slot holds: the direct trampoline and the
    /// method / generic closure bridges pass the instance pointer, while the protocol-extension bridge
    /// copies every non-primitive argument into a scratch buffer and passes that buffer's ADDRESS. Each
    /// reference flavour therefore needs a dereferencing reader under the slot-address convention, or the
    /// scratch buffer itself is wrapped as the instance (and Swift deallocates it on return).
    /// </summary>
    [Theory]
    [InlineData(PureClass, "SwiftMarshal.MarshalBorrowedClassFromSlot<Cs>(__p0)")]
    [InlineData(ObjCRootedClass, "SwiftMarshal.MarshalBorrowedClassFromSlot<Cs>(__p0)")]
    [InlineData(ObjCBridgedClass,
        "ObjCRuntime.Runtime.GetNSObject<Cs>(global::System.Runtime.InteropServices.Marshal.ReadIntPtr(__p0))")]
    public void BorrowedCallbackArgMarshal_SlotAddressConvention_DereferencesEveryReferenceFlavour(
        string typeName, string expected)
    {
        var closureHandler = new ClosureHandler(CreateTypeDatabase());

        Assert.Equal(expected, closureHandler.BorrowedCallbackArgMarshal(
            new NamedTypeSpec(typeName), "Cs", "__p0", ptrIsSlotAddress: true));
    }

    /// <summary>
    /// A bound generic — a shape the protocol-extension bridge explicitly admits and copies into its
    /// scratch buffer — takes the reader of its BASE declaration, since the reference flavour belongs to
    /// the declaration and not to the arguments it closes over. Deferring it to the runtime reader
    /// instead only works while every argument has Swift metadata: a generic class's metadata accessor
    /// demands metadata for each one, so a box closed over an ObjC peer resolves nothing, misses the
    /// class arm and has its slot adopted as the instance.
    /// </summary>
    [Theory]
    [InlineData(PureClass, "SwiftMarshal.MarshalBorrowedClassFromSlot<Cs>(__p0)",
        "SwiftMarshal.MarshalBorrowedClassFromSwift<Cs>(__p0)")]
    [InlineData(ObjCRootedClass, "SwiftMarshal.MarshalBorrowedClassFromSlot<Cs>(__p0)",
        "SwiftMarshal.MarshalCallbackArg<Cs>(__p0)")]
    [InlineData(ObjCBridgedClass,
        "ObjCRuntime.Runtime.GetNSObject<Cs>(global::System.Runtime.InteropServices.Marshal.ReadIntPtr(__p0))",
        "ObjCRuntime.Runtime.GetNSObject<Cs>(__p0)")]
    public void BorrowedCallbackArgMarshal_BoundGeneric_TakesItsBaseDeclarationsReader(
        string baseTypeName, string expectedFromSlot, string expectedFromObjectPointer)
    {
        var closureHandler = new ClosureHandler(CreateTypeDatabase());
        var boundGeneric = new NamedTypeSpec(baseTypeName, new NamedTypeSpec(Primitive));

        Assert.Equal(expectedFromSlot,
            closureHandler.BorrowedCallbackArgMarshal(boundGeneric, "Cs", "__p0", ptrIsSlotAddress: true));
        Assert.Equal(expectedFromObjectPointer,
            closureHandler.BorrowedCallbackArgMarshal(boundGeneric, "Cs", "__p0"));
    }

    /// <summary>
    /// A bound generic whose base is a VALUE type still reads as a value: the base-declaration lookup
    /// decides the flavour, so it must not promote every generic to a reference reader.
    /// </summary>
    [Fact]
    public void BorrowedCallbackArgMarshal_BoundGenericOverValueBase_StaysOnTheValueReader()
    {
        var closureHandler = new ClosureHandler(CreateTypeDatabase());
        var boundGeneric = new NamedTypeSpec(BridgeableValue, new NamedTypeSpec(Primitive));

        Assert.Equal("SwiftMarshal.MarshalCallbackArgFromSlot<Cs>(__p0)",
            closureHandler.BorrowedCallbackArgMarshal(boundGeneric, "Cs", "__p0", ptrIsSlotAddress: true));
        Assert.Equal("SwiftMarshal.MarshalCallbackArg<Cs>(__p0)",
            closureHandler.BorrowedCallbackArgMarshal(boundGeneric, "Cs", "__p0"));
    }

    /// <summary>
    /// A type nested inside a generic outer is classified by its LEAF declaration. The parser hangs
    /// <c>Outer&lt;T&gt;.Inner</c> off the outer segment (arguments on <c>Outer</c>, the leaf in
    /// <c>InnerType</c>) and the protocol-extension bridge admits the shape on the OUTER record's kind,
    /// so a struct nested in a generic class arrives on a slot holding its value bytes. Classifying it
    /// by the outer segment would retain the first of those words as an object.
    /// </summary>
    [Theory]
    [InlineData(NestedStructLeaf, "SwiftMarshal.MarshalCallbackArgFromSlot<Cs>(__p0)",
        "SwiftMarshal.MarshalCallbackArg<Cs>(__p0)")]
    [InlineData(NestedClassLeaf, "SwiftMarshal.MarshalBorrowedClassFromSlot<Cs>(__p0)",
        "SwiftMarshal.MarshalBorrowedClassFromSwift<Cs>(__p0)")]
    public void BorrowedCallbackArgMarshal_NestedLeafOnGenericOuter_TakesTheLeafsReader(
        string leafName, string expectedFromSlot, string expectedFromObjectPointer)
    {
        var closureHandler = new ClosureHandler(CreateTypeDatabase());
        var nested = new NamedTypeSpec(PureClass, new NamedTypeSpec(Primitive))
        {
            InnerType = new NamedTypeSpec(leafName)
        };

        Assert.Equal(expectedFromSlot,
            closureHandler.BorrowedCallbackArgMarshal(nested, "Cs", "__p0", ptrIsSlotAddress: true));
        Assert.Equal(expectedFromObjectPointer,
            closureHandler.BorrowedCallbackArgMarshal(nested, "Cs", "__p0"));
    }

    /// <summary>
    /// A nested leaf the database never registered classifies as nothing and falls through to the
    /// runtime reader — the honest answer for a declaration this generator never saw, and never a
    /// reference read inherited from the enclosing declaration.
    /// </summary>
    [Fact]
    public void BorrowedCallbackArgMarshal_UnregisteredNestedLeaf_FallsThroughToTheRuntimeReader()
    {
        var closureHandler = new ClosureHandler(CreateTypeDatabase());
        var nested = new NamedTypeSpec(PureClass, new NamedTypeSpec(Primitive))
        {
            InnerType = new NamedTypeSpec("NeverRegistered")
        };

        Assert.Equal("SwiftMarshal.MarshalCallbackArgFromSlot<Cs>(__p0)",
            closureHandler.BorrowedCallbackArgMarshal(nested, "Cs", "__p0", ptrIsSlotAddress: true));
        Assert.Equal("SwiftMarshal.MarshalCallbackArg<Cs>(__p0)",
            closureHandler.BorrowedCallbackArgMarshal(nested, "Cs", "__p0"));
    }

    /// <summary>
    /// An unbound generic parameter and a <c>Self.*</c> member type have no type record at all, so they
    /// land on the same unclassified arm and must carry the convention for the same reason.
    /// </summary>
    [Theory]
    [InlineData("τ_0_0")]
    [InlineData("Self.Element")]
    public void BorrowedCallbackArgMarshal_SlotAddressConvention_GenericParamCarriesTheConvention(
        string typeName)
    {
        var closureHandler = new ClosureHandler(CreateTypeDatabase());
        var argType = new NamedTypeSpec(typeName);

        Assert.Equal("SwiftMarshal.MarshalCallbackArgFromSlot<T>(__p0)",
            closureHandler.BorrowedCallbackArgMarshal(argType, "T", "__p0", ptrIsSlotAddress: true));
        Assert.Equal("SwiftMarshal.MarshalCallbackArg<T>(__p0)",
            closureHandler.BorrowedCallbackArgMarshal(argType, "T", "__p0"));
    }

    /// <summary>
    /// A value argument is handed over as the address of its buffer under BOTH conventions, so the two
    /// readers must agree on it — <c>MarshalCallbackArgFromSlot</c> differs from its sibling only in the
    /// true-class arm, which a value carrier never takes.
    /// </summary>
    [Theory]
    [InlineData(BridgeableValue)]
    [InlineData(Primitive)]
    public void BorrowedCallbackArgMarshal_SlotAddressConvention_ValueTypesTakeTheSharedReader(string typeName)
    {
        var closureHandler = new ClosureHandler(CreateTypeDatabase());
        var argType = new NamedTypeSpec(typeName);

        Assert.Equal("SwiftMarshal.MarshalCallbackArg<Cs>(__p0)",
            closureHandler.BorrowedCallbackArgMarshal(argType, "Cs", "__p0"));
        Assert.Equal("SwiftMarshal.MarshalCallbackArgFromSlot<Cs>(__p0)",
            closureHandler.BorrowedCallbackArgMarshal(argType, "Cs", "__p0", ptrIsSlotAddress: true));
    }
}
