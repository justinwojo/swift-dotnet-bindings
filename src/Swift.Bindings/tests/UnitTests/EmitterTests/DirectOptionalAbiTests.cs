// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for the direct-CallConvSwift Optional width classifier and the ABI floor built on it.
///
/// <para>The defect these pin: the emitter's preferred route for a wide <c>Optional&lt;T&gt;</c> is
/// a Swift wrapper with an out-buffer, but that route is conditional on the member being
/// wrapper-eligible. When it is not, the emitter falls back to a direct P/Invoke that declares the
/// Optional as a single <c>IntPtr</c> and then copies the type metadata's full size out of that
/// pointer-sized local. For a 16-byte <c>Optional&lt;String&gt;</c> that transfers 8 real bytes and
/// 8 bytes of adjacent stack memory — and because such an Optional carries no separate tag byte,
/// the bytes that were never transferred are exactly the ones deciding nil-ness.</para>
///
/// <para>The classifier is the soundness half of the fix, and its hardest requirement is not
/// catching the wide shapes but <b>leaving the narrow ones alone</b>: the routing predicate that
/// selects the wrapper calls several genuinely one-word Optionals "large", and refusing those would
/// replace working members with throws. Both directions are asserted here.</para>
/// </summary>
public class DirectOptionalAbiTests
{
    #region Classifier — shapes that do NOT fit the single direct slot

    [Fact]
    public void Classify_OptionalString_IsTwoIntegerWords()
    {
        // The reported defect's exact shape. String is two words and has spare bits, so the
        // Optional needs no tag byte and stays at exactly 16 bytes — twice the direct slot.
        var typeDb = CreateTypeDatabase();

        var result = DirectOptionalAbi.Classify(Optional(new NamedTypeSpec("Swift.String")), typeDb);

        Assert.Equal(DirectOptionalAbiWidth.TwoIntegerWords, result);
        Assert.True(DirectOptionalAbi.ExceedsDirectSlot(Optional(new NamedTypeSpec("Swift.String")), typeDb));
    }

    [Theory]
    [InlineData("Swift.Double")]
    [InlineData("Swift.Int")]
    [InlineData("Swift.Int64")]
    [InlineData("Swift.CGFloat")]
    public void Classify_OptionalWordSizedPayload_IsUnprovable(string innerName)
    {
        // A payload that already fills a word has no spare bits, so the Optional appends a tag
        // byte and spills past the slot. Deliberately Unprovable rather than "two words": the
        // payload's register class differs between these (Double lands in a floating-point
        // register, Int in an integer one), so a single two-integer-word carrier would not be
        // correct for all of them and the classifier must not imply that it would.
        var typeDb = CreateTypeDatabase();

        var result = DirectOptionalAbi.Classify(Optional(new NamedTypeSpec(innerName)), typeDb);

        Assert.Equal(DirectOptionalAbiWidth.Unprovable, result);
    }

    [Fact]
    public void Classify_OptionalUnknownStruct_IsUnprovable()
    {
        // A struct the database carries no layout knowledge of could be resilient (address-only
        // across its module boundary) or arbitrarily wide. Nothing here establishes it fits, so
        // the classifier refuses rather than assuming.
        var typeDb = CreateTypeDatabase();

        var result = DirectOptionalAbi.Classify(Optional(new NamedTypeSpec("Other.MysteryStruct")), typeDb);

        Assert.Equal(DirectOptionalAbiWidth.Unprovable, result);
    }

    [Fact]
    public void Classify_OptionalBareContainer_IsUnprovable()
    {
        // The single-pointer container proof is about a fully applied Array/Dictionary/Set. A
        // bare container name with no element type is not a shape whose layout has been
        // established, so it must not borrow the applied form's answer.
        var typeDb = CreateTypeDatabase();

        var result = DirectOptionalAbi.Classify(Optional(new NamedTypeSpec("Swift.Array")), typeDb);

        Assert.Equal(DirectOptionalAbiWidth.Unprovable, result);
    }

    [Fact]
    public void Classify_OptionalObjCBridgedValueType_IsUnprovable()
    {
        // The reference predicate this classifier consults answers a *bridging* question — does
        // this arrive as a nullable object pointer at a @_cdecl boundary — and so also accepts
        // Swift value types that bridge to an ObjC class (Foundation.URL, Date, IndexPath). There
        // is no bridging on the direct CallConvSwift path: such a payload keeps its native Swift
        // layout, which is wider than a pointer. Answering SingleWord here would be a licence to
        // truncate, so the classifier must decline to prove it.
        var typeDb = CreateTypeDatabase();

        var result = DirectOptionalAbi.Classify(Optional(new NamedTypeSpec("TestModule.BridgedValue")), typeDb);

        Assert.Equal(DirectOptionalAbiWidth.Unprovable, result);
    }

    #endregion

    #region Classifier — shapes that DO fit (the over-broad-gate canaries)

    [Theory]
    [InlineData("Swift.Array")]
    [InlineData("Swift.Dictionary")]
    [InlineData("Swift.Set")]
    public void Classify_OptionalSinglePointerContainer_IsSingleWord(string containerName)
    {
        // THE load-bearing negative control. These are classified "large" by the wrapper-routing
        // predicate, but each is physically one refcounted storage pointer using null as its extra
        // inhabitant, so the existing single-slot direct call is already correct for them. A gate
        // keyed on the routing predicate instead of on real width would tombstone working members
        // here — this test is what catches that.
        var typeDb = CreateTypeDatabase();
        var spec = Optional(Generic(containerName, new NamedTypeSpec("Swift.String")));

        Assert.Equal(DirectOptionalAbiWidth.SingleWord, DirectOptionalAbi.Classify(spec, typeDb));
        Assert.False(DirectOptionalAbi.ExceedsDirectSlot(spec, typeDb));
    }

    [Fact]
    public void Classify_OptionalErrorExistential_IsSingleWord()
    {
        // `any Error` is the one existential Swift represents as a single refcounted box rather
        // than a multi-word container: measured at 8 bytes, where an ordinary `(any P)?` is 40.
        // Every `throws`-shaped Optional-error return depends on this staying live, so it is the
        // load-bearing control against the existential arm refusing a pointer-sized shape.
        var typeDb = CreateTypeDatabase();

        var result = DirectOptionalAbi.Classify(Optional(new NamedTypeSpec("Swift.Error")), typeDb);

        Assert.Equal(DirectOptionalAbiWidth.SingleWord, result);
    }

    [Fact]
    public void Floor_OptionalObjCBridgedValueTypeReturn_Fires()
    {
        // The floor must reach this shape even though the large-Optional routing predicates
        // answer "not large" for it — they early-out on a *bridging* question, and there is no
        // bridging on the direct path. Foundation.URL? measures 16 bytes, so the single-slot
        // call reads one word of a struct and hands it to GetINativeObject as an object pointer,
        // releasing a value that was never a reference.
        var (moduleDecl, typeDb) = CreateEnvironment();
        var parent = CreateClass("Host", moduleDecl);
        var method = MethodReturning(
            "boxedUrl",
            Optional(new NamedTypeSpec("TestModule.BridgedValue")),
            parent,
            moduleDecl);

        Assert.True(WrapperValidation.HasTruncatedLargeOptionalDirectDispatch(
            new MethodEnvironment(method, typeDb)));
    }

    [Fact]
    public void Classify_OptionalClassReference_IsSingleWord()
    {
        // One object pointer, nil as its null extra inhabitant — the case the existing direct
        // emission was designed around and must keep serving.
        var typeDb = CreateTypeDatabase();
        var spec = Optional(new NamedTypeSpec("TestModule.MyClass"));

        Assert.Equal(DirectOptionalAbiWidth.SingleWord, DirectOptionalAbi.Classify(spec, typeDb));
    }

    [Theory]
    [InlineData("Swift.Bool")]
    [InlineData("Swift.Int8")]
    [InlineData("Swift.Int16")]
    [InlineData("Swift.Int32")]
    [InlineData("Swift.UInt32")]
    [InlineData("Swift.Float")]
    public void Classify_OptionalSubWordPrimitive_IsSingleWord(string innerName)
    {
        // Payload plus its appended tag byte still fits inside one word.
        var typeDb = CreateTypeDatabase();

        var result = DirectOptionalAbi.Classify(Optional(new NamedTypeSpec(innerName)), typeDb);

        Assert.Equal(DirectOptionalAbiWidth.SingleWord, result);
    }

    [Fact]
    public void Classify_NonOptional_IsNotOptional()
    {
        var typeDb = CreateTypeDatabase();

        Assert.Equal(
            DirectOptionalAbiWidth.NotOptional,
            DirectOptionalAbi.Classify(new NamedTypeSpec("Swift.String"), typeDb));
        Assert.False(DirectOptionalAbi.ExceedsDirectSlot(new NamedTypeSpec("Swift.String"), typeDb));
    }

    #endregion

    #region ABI floor — return side

    [Fact]
    public void Floor_UnwrappedOptionalStringReturn_Fires()
    {
        // The defect as the generator sees it: no wrapper of any kind assigned, so the emitted
        // call is the truncating one.
        var (moduleDecl, typeDb) = CreateEnvironment();
        var parent = CreateClass("Host", moduleDecl);
        var method = MethodReturning("label", Optional(new NamedTypeSpec("Swift.String")), parent, moduleDecl);

        Assert.True(WrapperValidation.HasTruncatedLargeOptionalDirectDispatch(
            new MethodEnvironment(method, typeDb)));
    }

    [Fact]
    public void Floor_OptionalArrayReturn_DoesNotFire()
    {
        // Same wrapper-less direct path, but the value genuinely fits the slot. Must stay live —
        // this is the assertion that fails if the floor is widened to the routing predicate.
        var (moduleDecl, typeDb) = CreateEnvironment();
        var parent = CreateClass("Host", moduleDecl);
        var method = MethodReturning(
            "names",
            Optional(Generic("Swift.Array", new NamedTypeSpec("Swift.String"))),
            parent,
            moduleDecl);

        Assert.False(WrapperValidation.HasTruncatedLargeOptionalDirectDispatch(
            new MethodEnvironment(method, typeDb)));
    }

    [Fact]
    public void Floor_OptionalStringReturnWithOptionalPointerWrapper_DoesNotFire()
    {
        // The out-buffer wrapper exists precisely to carry these through memory. When it is
        // assigned, width stops mattering and the member must keep its real body.
        var (moduleDecl, typeDb) = CreateEnvironment();
        var parent = CreateClass("Host", moduleDecl);
        var method = MethodReturning("label", Optional(new NamedTypeSpec("Swift.String")), parent, moduleDecl);
        method.HasOptionalPointerWrapper = true;
        method.UsesWrapperLibrary = true;

        Assert.False(WrapperValidation.HasTruncatedLargeOptionalDirectDispatch(
            new MethodEnvironment(method, typeDb)));
    }

    [Fact]
    public void Floor_OptionalStringReturnWithCdeclWrapper_DoesNotFire()
    {
        var (moduleDecl, typeDb) = CreateEnvironment();
        var parent = CreateClass("Host", moduleDecl);
        var method = MethodReturning("label", Optional(new NamedTypeSpec("Swift.String")), parent, moduleDecl);
        method.UsesCdeclMethodWrapper = true;

        Assert.False(WrapperValidation.HasTruncatedLargeOptionalDirectDispatch(
            new MethodEnvironment(method, typeDb)));
    }

    #endregion

    #region ABI floor — parameter side

    [Fact]
    public void Floor_UnwrappedOptionalStringParam_Fires()
    {
        // Swift reads a two-word Optional argument out of two integer registers; supplying only
        // the first leaves the callee's own nil check reading whatever the second held. The
        // parameter side is a distinct emission path from the return side and needs its own gate.
        var (moduleDecl, typeDb) = CreateEnvironment();
        var parent = CreateClass("Host", moduleDecl);
        var method = MethodTaking("width", Optional(new NamedTypeSpec("Swift.String")), parent, moduleDecl);

        Assert.True(WrapperValidation.HasTruncatedLargeOptionalDirectDispatch(
            new MethodEnvironment(method, typeDb)));
    }

    [Fact]
    public void Floor_OptionalArrayParam_DoesNotFire()
    {
        // Parameter-side counterpart of the Array return control.
        var (moduleDecl, typeDb) = CreateEnvironment();
        var parent = CreateClass("Host", moduleDecl);
        var method = MethodTaking(
            "count",
            Optional(Generic("Swift.Array", new NamedTypeSpec("Swift.String"))),
            parent,
            moduleDecl);

        Assert.False(WrapperValidation.HasTruncatedLargeOptionalDirectDispatch(
            new MethodEnvironment(method, typeDb)));
    }

    [Fact]
    public void Floor_OptionalClosureParam_DoesNotFire()
    {
        // Function-valued Optionals are outside this floor. Width alone would not decide them:
        // an Optional @convention(c) function is one word (8 bytes), a Swift closure's is two
        // (16), and the convention is not decidable from the spec alone — closures parsed from
        // ABI JSON carry no convention attribute. Firing here would tombstone working
        // @convention(c) members over a missing attribute, and Optional closures have their own
        // marshalling path that never reads the value out of a single slot.
        var (moduleDecl, typeDb) = CreateEnvironment();
        var parent = CreateClass("Host", moduleDecl);
        var closure = new ClosureTypeSpec(new NamedTypeSpec("Swift.Int"), TupleTypeSpec.Empty);
        var method = MethodTaking("observe", Optional(closure), parent, moduleDecl);

        Assert.False(WrapperValidation.HasTruncatedLargeOptionalDirectDispatch(
            new MethodEnvironment(method, typeDb)));
    }

    [Fact]
    public void Floor_OptionalClosureReturn_DoesNotFire()
    {
        // The return-side twin of Floor_OptionalClosureParam_DoesNotFire. The exclusion is applied
        // on both arms, and Classify answers Unprovable for a closure payload (the inner spec is
        // not a NamedTypeSpec), so without the return-arm guard every Optional-closure return on a
        // wrapper-ineligible parent would be tombstoned. One arm being right does not imply the
        // other, so each is pinned separately.
        var (moduleDecl, typeDb) = CreateEnvironment();
        var parent = CreateClass("Host", moduleDecl);
        var closure = new ClosureTypeSpec(new NamedTypeSpec("Swift.Int"), TupleTypeSpec.Empty);
        var method = MethodReturning("makeHandler", Optional(closure), parent, moduleDecl);

        Assert.False(WrapperValidation.HasTruncatedLargeOptionalDirectDispatch(
            new MethodEnvironment(method, typeDb)));
    }

    [Fact]
    public void Floor_OptionalErrorExistentialReturn_DoesNotFire()
    {
        // Classify_OptionalErrorExistential_IsSingleWord pins the classifier; this pins the floor
        // that consumes it. `any Error` is the one existential Swift represents as a single
        // refcounted box (measured 8 bytes, against 40 for an ordinary `(any P)?`), so it must
        // stay live even though the general existential arm right after it refuses. A future
        // "existentials always exceed the slot" shortcut in the floor would pass the classifier
        // test and fail this one.
        var (moduleDecl, typeDb) = CreateEnvironment();
        var parent = CreateClass("Host", moduleDecl);
        var method = MethodReturning("failure", Optional(new NamedTypeSpec("Swift.Error")), parent, moduleDecl);

        Assert.False(WrapperValidation.HasTruncatedLargeOptionalDirectDispatch(
            new MethodEnvironment(method, typeDb)));
    }

    [Fact]
    public void Floor_OptionalGenericPayloadParam_DoesNotFire()
    {
        // A generic payload has no static size at the call site, so Swift takes the argument
        // indirectly and the emitter passes the buffer address rather than a value word. A pointer
        // carries the whole value however wide it is, so there is nothing to truncate. Confirmed
        // on the Simulator: a `T?` argument answers correctly for both nil and non-nil, so firing
        // here would destroy working surface.
        var (moduleDecl, typeDb) = CreateEnvironment();
        var parent = CreateClass("Host", moduleDecl);
        var method = MethodTaking("accept", Optional(new NamedTypeSpec("T")), parent, moduleDecl);

        Assert.False(WrapperValidation.HasTruncatedLargeOptionalDirectDispatch(
            new MethodEnvironment(method, typeDb)));
    }

    [Fact]
    public void Floor_OptionalPointerParam_Fires()
    {
        // The counter-example to "one word means it fits". OpaquePointer? measures 8 bytes — the
        // same as the [String]? argument that round-trips correctly — yet calling it with the
        // parameter arm lifted SIGSEGVs the simulator on the first call. Width is necessary but
        // not sufficient for the direct argument slot, so nullable pointers must stay Unprovable.
        // This test is the guard against "re-classify them SingleWord, the measurement says 8".
        var (moduleDecl, typeDb) = CreateEnvironment();
        var parent = CreateClass("Host", moduleDecl);
        var method = MethodTaking("accept", Optional(new NamedTypeSpec("Swift.OpaquePointer")), parent, moduleDecl);

        Assert.True(WrapperValidation.HasTruncatedLargeOptionalDirectDispatch(
            new MethodEnvironment(method, typeDb)));
    }

    [Fact]
    public void Floor_OptionalProtocolExistentialParam_Fires()
    {
        // The wrapper route selects on IsLargeOptionalParam OR IsLargeOptionalProtocolParam,
        // because the first deliberately returns false for protocol existentials and hands them
        // to the second. A floor consulting only the first would let every `(any P)?` parameter
        // on a wrapper-ineligible member walk past into the truncating call — and an existential
        // container is five words wide, so one slot is nowhere near enough.
        var (moduleDecl, typeDb) = CreateEnvironment();
        var parent = CreateClass("Host", moduleDecl);
        var method = MethodTaking(
            "accept",
            Optional(new NamedTypeSpec("TestModule.MyProtocol")),
            parent,
            moduleDecl);

        Assert.True(WrapperValidation.HasTruncatedLargeOptionalDirectDispatch(
            new MethodEnvironment(method, typeDb)));
    }

    #endregion

    #region ABI floor — accessors are in scope

    [Fact]
    public void Floor_AccessorWithOptionalStringReturn_Fires()
    {
        // The sibling internal-visibility floor excludes accessors, because the advisory marker it
        // drives is never rendered on that path. That exclusion must NOT be inherited here: a
        // `public var name: String?` getter truncates exactly like the equivalent method, and it is
        // the most ordinary shape a Swift API has. Inheriting the exclusion would leave the defect
        // reachable through properties while the method form was fixed.
        var (moduleDecl, typeDb) = CreateEnvironment();
        var parent = CreateClass("Host", moduleDecl);
        var method = MethodReturning("get_label", Optional(new NamedTypeSpec("Swift.String")), parent, moduleDecl);
        method.IsAccessor = true;

        Assert.True(WrapperValidation.HasTruncatedLargeOptionalDirectDispatch(
            new MethodEnvironment(method, typeDb)));
    }

    #endregion

    #region Helpers

    private static NamedTypeSpec Optional(TypeSpec inner)
    {
        var spec = new NamedTypeSpec("Swift.Optional");
        spec.GenericParameters.Add(inner);
        return spec;
    }

    private static NamedTypeSpec Generic(string name, TypeSpec arg)
    {
        var spec = new NamedTypeSpec(name);
        spec.GenericParameters.Add(arg);
        return spec;
    }

    private static TypeDatabase CreateTypeDatabase() => CreateEnvironment().typeDb;

    private static (ModuleDecl moduleDecl, TypeDatabase typeDb) CreateEnvironment()
    {
        var typeDb = new TypeDatabase();

        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        RegisterStruct(swiftModule, "Swift.String", "Swift", "SwiftString", inlineSize: 16);
        RegisterStruct(swiftModule, "Swift.Double", "System", "Double", inlineSize: 8);
        RegisterStruct(swiftModule, "Swift.Int", "System", "IntPtr", inlineSize: 8);
        RegisterStruct(swiftModule, "Swift.Int32", "System", "Int32", inlineSize: 4);
        RegisterStruct(swiftModule, "Swift.Bool", "System", "Boolean", inlineSize: 1);
        typeDb.AddModuleDatabase(swiftModule);

        var moduleDecl = new ModuleDecl
        {
            Name = "TestModule",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.MyClass"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "MyClass"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.MyClass"),
                MetadataAccessor = "$s10TestModule7MyClassCMa",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Class
            });
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.MyProtocol"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "IMyProtocol"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.MyProtocol"),
                MetadataAccessor = "$s10TestModule10MyProtocolMp",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Protocol
            });

        // A Swift value type that bridges to an ObjC class, in the shape of Foundation.URL:
        // a frozen struct carrying the ObjCBridged flag.
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.BridgedValue"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "BridgedValue"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.BridgedValue"),
                MetadataAccessor = "$s10TestModule12BridgedValueVMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.ObjCBridged,
                Kind = TypeRecordKind.Struct,
                InlineSize = 16
            });
        typeDb.AddModuleDatabase(testModule);

        return (moduleDecl, typeDb);
    }

    private static void RegisterStruct(
        ModuleTypeDatabase db, string qualifiedName, string ns, string csName, int inlineSize)
    {
        var swiftTypeName = SwiftTypeName.FromModuleQualifiedName(qualifiedName);
        db.RegisterType(
            swiftTypeName,
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName(ns, csName),
                SwiftTypeName = swiftTypeName,
                MetadataAccessor = $"$s{swiftTypeName.Name}Ma",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct,
                InlineSize = inlineSize
            });
    }

    private static ClassDecl CreateClass(string name, ModuleDecl moduleDecl)
    {
        var decl = new ClassDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}CN",
            IsFinal = true,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        moduleDecl.Types.Add(decl);
        return decl;
    }

    private static ArgumentDecl Arg(TypeSpec spec, string name, ModuleDecl moduleDecl) =>
        new ArgumentDecl
        {
            SwiftTypeSpec = spec,
            Name = name,
            PrivateName = name,
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = moduleDecl
        };

    private static MethodDecl MethodReturning(
        string name, TypeSpec returnType, TypeDecl parent, ModuleDecl moduleDecl) =>
        BuildMethod(name, parent, moduleDecl, Arg(returnType, "", moduleDecl));

    private static MethodDecl MethodTaking(
        string name, TypeSpec paramType, TypeDecl parent, ModuleDecl moduleDecl) =>
        BuildMethod(
            name,
            parent,
            moduleDecl,
            Arg(new NamedTypeSpec("Swift.Int32"), "", moduleDecl),
            Arg(paramType, "value", moduleDecl));

    private static MethodDecl BuildMethod(
        string name, TypeDecl parent, ModuleDecl moduleDecl, params ArgumentDecl[] signature) =>
        new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule_{name}",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>(signature),
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parent,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };

    #endregion
}
