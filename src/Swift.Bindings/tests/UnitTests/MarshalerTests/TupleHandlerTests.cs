// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for tuple type detection and handling.
/// These tests focus on the TupleTypeSpec parsing and translation to C# types.
/// </summary>
public class TupleHandlerTests
{
    private readonly MockTypeDatabase _typeDatabase;
    private readonly TupleHandler _tupleHandler;

    public TupleHandlerTests()
    {
        _typeDatabase = new MockTypeDatabase();
        _tupleHandler = new TupleHandler(_typeDatabase);
    }

    #region IsTuple Detection Tests

    [Fact]
    public void IsTuple_WithNonEmptyTuple_ReturnsTrue()
    {
        var tuple = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.String")
        });

        Assert.True(_tupleHandler.IsTuple(tuple));
    }

    [Fact]
    public void IsTuple_WithEmptyTuple_ReturnsFalse()
    {
        var tuple = TupleTypeSpec.Empty;

        Assert.False(_tupleHandler.IsTuple(tuple));
    }

    [Fact]
    public void IsTuple_WithNamedTypeSpec_ReturnsFalse()
    {
        var namedType = new NamedTypeSpec("Swift.Int");

        Assert.False(_tupleHandler.IsTuple(namedType));
    }

    [Fact]
    public void IsTuple_WithClosureTypeSpec_ReturnsFalse()
    {
        var closure = new ClosureTypeSpec(TupleTypeSpec.Empty, TupleTypeSpec.Empty);

        Assert.False(_tupleHandler.IsTuple(closure));
    }

    [Fact]
    public void IsTuple_WithArgumentDecl_DetectsTuple()
    {
        var tuple = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Bool")
        });
        var argument = CreateArgumentDecl("point", tuple);

        Assert.True(_tupleHandler.IsTuple(argument));
    }

    [Fact]
    public void IsTuple_WithArgumentDeclNonTuple_ReturnsFalse()
    {
        var namedType = new NamedTypeSpec("Swift.Int");
        var argument = CreateArgumentDecl("value", namedType);

        Assert.False(_tupleHandler.IsTuple(argument));
    }

    #endregion

    #region GetTupleTypeSpec Tests

    [Fact]
    public void GetTupleTypeSpec_WithTupleArgument_ReturnsTupleTypeSpec()
    {
        var tuple = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.String")
        });
        var argument = CreateArgumentDecl("data", tuple);

        var result = _tupleHandler.GetTupleTypeSpec(argument);

        Assert.NotNull(result);
        Assert.Equal(2, result.Elements.Count);
    }

    [Fact]
    public void GetTupleTypeSpec_WithNonTupleArgument_ReturnsNull()
    {
        var namedType = new NamedTypeSpec("Swift.Int");
        var argument = CreateArgumentDecl("value", namedType);

        var result = _tupleHandler.GetTupleTypeSpec(argument);

        Assert.Null(result);
    }

    [Fact]
    public void GetTupleTypeSpec_WithEmptyTupleArgument_ReturnsNull()
    {
        var argument = CreateArgumentDecl("void", TupleTypeSpec.Empty);

        var result = _tupleHandler.GetTupleTypeSpec(argument);

        Assert.Null(result);
    }

    #endregion

    #region IsSupportedTuple Tests

    [Fact]
    public void IsSupportedTuple_WithFrozenPrimitives_ReturnsTrue()
    {
        var tuple = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Bool")
        });

        Assert.True(_tupleHandler.IsSupportedTuple(tuple));
    }

    [Fact]
    public void IsSupportedTuple_WithEmptyTuple_ReturnsFalse()
    {
        Assert.False(_tupleHandler.IsSupportedTuple(TupleTypeSpec.Empty));
    }

    [Fact]
    public void IsSupportedTuple_WithNestedTuple_ReturnsFalse()
    {
        var innerTuple = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Int")
        });
        var outerTuple = new TupleTypeSpec(new List<TypeSpec>
        {
            innerTuple,
            new NamedTypeSpec("Swift.Bool")
        });

        Assert.False(_tupleHandler.IsSupportedTuple(outerTuple));
    }

    [Fact]
    public void IsSupportedTuple_WithClosure_ReturnsFalse()
    {
        var closure = new ClosureTypeSpec(TupleTypeSpec.Empty, TupleTypeSpec.Empty);
        var tuple = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int"),
            closure
        });

        Assert.False(_tupleHandler.IsSupportedTuple(tuple));
    }

    [Fact]
    public void IsSupportedTuple_With8Elements_ReturnsFalse()
    {
        var elements = new List<TypeSpec>();
        for (int i = 0; i < 8; i++)
        {
            elements.Add(new NamedTypeSpec("Swift.Int"));
        }
        var tuple = new TupleTypeSpec(elements);

        Assert.False(_tupleHandler.IsSupportedTuple(tuple));
    }

    [Fact]
    public void IsSupportedTuple_With7Elements_ReturnsTrue()
    {
        var elements = new List<TypeSpec>();
        for (int i = 0; i < 7; i++)
        {
            elements.Add(new NamedTypeSpec("Swift.Int"));
        }
        var tuple = new TupleTypeSpec(elements);

        Assert.True(_tupleHandler.IsSupportedTuple(tuple));
    }

    [Fact]
    public void IsSupportedTuple_WithNonFrozenType_ReturnsFalse()
    {
        var tuple = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Test.NonFrozenType") // Not registered as frozen
        });

        Assert.False(_tupleHandler.IsSupportedTuple(tuple));
    }

    [Fact]
    public void IsSupportedTuple_WithGenericParameters_ReturnsFalse()
    {
        var genericType = new NamedTypeSpec("Swift.Array");
        genericType.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        var tuple = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int"),
            genericType
        });

        Assert.False(_tupleHandler.IsSupportedTuple(tuple));
    }

    #endregion

    #region GetCSharpTupleType Tests

    [Fact]
    public void GetCSharpTupleType_IntString_ReturnsCorrectType()
    {
        var tuple = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.String")
        });

        var result = _tupleHandler.GetCSharpTupleType(tuple);

        Assert.Equal("(nint, Swift.SwiftString)", result);
    }

    [Fact]
    public void GetCSharpTupleType_WithLabels_PreservesLabels()
    {
        var intType = new NamedTypeSpec("Swift.Int") { TypeLabel = "x" };
        var boolType = new NamedTypeSpec("Swift.Bool") { TypeLabel = "y" };
        var tuple = new TupleTypeSpec(new List<TypeSpec> { intType, boolType });

        var result = _tupleHandler.GetCSharpTupleType(tuple);

        Assert.Equal("(nint x, bool y)", result);
    }

    [Fact]
    public void GetCSharpTupleType_SingleElement_ReturnsCorrectType()
    {
        var tuple = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Double")
        });

        var result = _tupleHandler.GetCSharpTupleType(tuple);

        Assert.Equal("(double)", result);
    }

    [Fact]
    public void GetCSharpTupleType_ThreeElements_ReturnsCorrectType()
    {
        var tuple = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Bool"),
            new NamedTypeSpec("Swift.Double")
        });

        var result = _tupleHandler.GetCSharpTupleType(tuple);

        Assert.Equal("(nint, bool, double)", result);
    }

    [Fact]
    public void GetCSharpTupleType_WithSimdAliasElement_CollapsesElementViaSharedService()
    {
        // A SIMD bound-generic tuple element routes through the shared
        // BoundGenericTranslation.TryResolveSimdAliasCSharp short-circuit and collapses to the
        // non-generic alias record — never the invalid `simd_float3<float>`.
        var simd3 = new NamedTypeSpec("Swift.SIMD3");
        simd3.GenericParameters.Add(new NamedTypeSpec("Swift.Float"));
        var tuple = new TupleTypeSpec(new List<TypeSpec> { simd3, new NamedTypeSpec("Swift.Int") });

        var result = _tupleHandler.GetCSharpTupleType(tuple);

        Assert.Equal("(System.Numerics.Vector3, nint)", result);
        Assert.DoesNotContain("Vector3<", result);
    }

    [Fact]
    public void GetCSharpTupleType_EmptyTupleBoundGenericElement_FlowsThroughDelegateNotSwiftVoid()
    {
        // The tuple path hands the shared BoundGenericTranslation service
        // mapEmptyTupleArgumentToSwiftVoid: false, so an empty-tuple generic argument flows through
        // this handler's own element translator (which yields AnyType for a non-named element) rather
        // than collapsing to Swift.SwiftVoid the way the closure path does. This guards the handler's
        // empty-tuple policy at the delegation site: a flag flip there would re-couple the tuple
        // caller to the closure caller's divergent behavior and produce Swift.SwiftVoid here instead.
        var optionalOfVoid = new NamedTypeSpec("Swift.Optional");
        optionalOfVoid.GenericParameters.Add(TupleTypeSpec.Empty);
        var tuple = new TupleTypeSpec(new List<TypeSpec> { optionalOfVoid, new NamedTypeSpec("Swift.Int") });

        var result = _tupleHandler.GetCSharpTupleType(tuple);

        Assert.DoesNotContain("Swift.SwiftVoid", result);
        Assert.Contains(TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName, result);
    }

    #endregion

    #region GetPInvokeTupleType Tests

    [Fact]
    public void GetPInvokeTupleType_IntString_ReturnsValueTuple()
    {
        var tuple = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.String")
        });

        var result = _tupleHandler.GetPInvokeTupleType(tuple);

        Assert.Equal("ValueTuple<nint, Swift.SwiftString>", result);
    }

    [Fact]
    public void GetPInvokeTupleType_ThreeElements_ReturnsValueTuple()
    {
        var tuple = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Bool"),
            new NamedTypeSpec("Swift.Double")
        });

        var result = _tupleHandler.GetPInvokeTupleType(tuple);

        Assert.Equal("ValueTuple<nint, bool, double>", result);
    }

    [Fact]
    public void GetPInvokeTupleType_BoundGenericElement_UsesIntPtrNotVoidStar()
    {
        var arrayType = new NamedTypeSpec("Swift.Array");
        arrayType.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        var tuple = new TupleTypeSpec(new List<TypeSpec>
        {
            arrayType,
            new NamedTypeSpec("Swift.Bool")
        });

        var result = _tupleHandler.GetPInvokeTupleType(tuple);

        Assert.Equal("ValueTuple<IntPtr, bool>", result);
        Assert.DoesNotContain("void*", result);
    }

    [Fact]
    public void GetPInvokeTupleType_OptionalNonObjCElement_UsesIntPtr()
    {
        var optionalInt = new NamedTypeSpec("Swift.Optional");
        optionalInt.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        var tuple = new TupleTypeSpec(new List<TypeSpec>
        {
            optionalInt,
            new NamedTypeSpec("Swift.Bool")
        });

        var result = _tupleHandler.GetPInvokeTupleType(tuple);

        Assert.Equal("ValueTuple<IntPtr, bool>", result);
        Assert.DoesNotContain("void*", result);
    }

    [Fact]
    public void GetPInvokeTupleType_MultipleBoundGenerics_UsesIntPtrForAll()
    {
        var arrayType1 = new NamedTypeSpec("Swift.Array");
        arrayType1.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        var arrayType2 = new NamedTypeSpec("Swift.Array");
        arrayType2.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        var tuple = new TupleTypeSpec(new List<TypeSpec>
        {
            arrayType1,
            arrayType2
        });

        var result = _tupleHandler.GetPInvokeTupleType(tuple);

        Assert.Equal("ValueTuple<IntPtr, IntPtr>", result);
    }

    [Fact]
    public void GetPInvokeTupleType_NonFrozenStructElement_ReturnsIntPtr()
    {
        var tuple = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("ImagePipeline.ImageResponse"),
            new NamedTypeSpec("Swift.Int")
        });

        var result = _tupleHandler.GetPInvokeTupleType(tuple);

        Assert.Equal("ValueTuple<IntPtr, nint>", result);
    }

    [Fact]
    public void GetPInvokeTupleType_ClassElement_ReturnsIntPtr()
    {
        var tuple = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("ImagePipeline.ImageTask"),
            new NamedTypeSpec("Swift.Bool")
        });

        var result = _tupleHandler.GetPInvokeTupleType(tuple);

        Assert.Equal("ValueTuple<IntPtr, bool>", result);
    }

    [Fact]
    public void GetPInvokeTupleType_AnyTypeElement_ReturnsIntPtr()
    {
        // An unknown type that resolves to AnyType should use IntPtr fallback
        var tuple = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("SomeModule.UnknownType"),
            new NamedTypeSpec("Swift.Int")
        });

        var result = _tupleHandler.GetPInvokeTupleType(tuple);

        Assert.Equal("ValueTuple<IntPtr, nint>", result);
    }

    [Fact]
    public void HasClosureUnsafeTupleElements_WithOptionalNonFrozen_ReturnsTrue()
    {
        // Optional<NonFrozenStruct> → P/Invoke IntPtr vs C# SwiftOptional<T> → mismatch
        var optionalResponse = new NamedTypeSpec("Swift.Optional");
        optionalResponse.GenericParameters.Add(new NamedTypeSpec("ImagePipeline.ImageResponse"));
        var tuple = new TupleTypeSpec(new List<TypeSpec>
        {
            optionalResponse,
            new NamedTypeSpec("Swift.Int")
        });

        Assert.True(_tupleHandler.HasClosureUnsafeTupleElements(tuple));
    }

    [Fact]
    public void HasClosureUnsafeTupleElements_WithPointerType_ReturnsFalse()
    {
        // UnsafeMutablePointer<T> → IntPtr in BOTH contexts → no mismatch
        var pointerType = new NamedTypeSpec("Swift.UnsafeMutablePointer");
        pointerType.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        var tuple = new TupleTypeSpec(new List<TypeSpec>
        {
            pointerType,
            new NamedTypeSpec("Swift.Int")
        });

        Assert.False(_tupleHandler.HasClosureUnsafeTupleElements(tuple));
    }

    [Fact]
    public void HasClosureUnsafeTupleElements_WithOnlyPrimitives_ReturnsFalse()
    {
        var tuple = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Int")
        });

        Assert.False(_tupleHandler.HasClosureUnsafeTupleElements(tuple));
    }

    [Fact]
    public void HasClosureUnsafeTupleElements_WithExistentialElement_ReturnsFalse()
    {
        // Existential elements no longer trigger the gate — the emitter handles
        // per-element conversion between ExistentialContainer and object/interface.
        var existentialElement = new NamedTypeSpec("TestModule.SomeProtocol") { IsAny = true };
        var intElement = new NamedTypeSpec("Swift.Int");
        var tuple = new TupleTypeSpec(new List<TypeSpec> { existentialElement, intElement });

        Assert.False(_tupleHandler.HasClosureUnsafeTupleElements(tuple));
    }

    #endregion

    #region HasUnmarshalledTupleElements (fail-closed tuple-parameter gate)

    [Fact]
    public void HasUnmarshalledTupleElements_WithExistentialElement_ReturnsTrue()
    {
        // The SAME existential element that HasClosureUnsafeTupleElements treats as safe (it only
        // catches the IntPtr subset for the closure-callback delegate-shape contract) is UNmarshalled
        // on the tuple-PARAMETER path: its P/Invoke ExistentialContainerN differs from its C# interface,
        // and the converted-tuple call-argument path does not yet thread per-element conversion. Gate 5b
        // skips such a member fail-closed (UnsupportedSignature) instead of emitting non-compiling code.
        var existentialElement = new NamedTypeSpec("TestModule.SomeProtocol") { IsAny = true };
        var intElement = new NamedTypeSpec("Swift.Int");
        var tuple = new TupleTypeSpec(new List<TypeSpec> { existentialElement, intElement });

        // Strict superset: the IntPtr-subset gate misses this element, the unmarshalled gate catches it.
        Assert.False(_tupleHandler.HasClosureUnsafeTupleElements(tuple));
        Assert.True(_tupleHandler.HasUnmarshalledTupleElements(tuple));
    }

    [Fact]
    public void HasUnmarshalledTupleElements_WithNonFrozenStructElement_ReturnsTrue()
    {
        // Non-frozen struct → P/Invoke IntPtr vs C# class type → mismatch → fail-closed.
        var tuple = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("ImagePipeline.ImageResponse"),
            new NamedTypeSpec("Swift.Int")
        });

        Assert.True(_tupleHandler.HasUnmarshalledTupleElements(tuple));
    }

    [Fact]
    public void HasUnmarshalledTupleElements_WithClassElement_ReturnsTrue()
    {
        // Swift class → P/Invoke IntPtr vs C# class type → mismatch → fail-closed.
        var tuple = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("ImagePipeline.ImageTask"),
            new NamedTypeSpec("Swift.Bool")
        });

        Assert.True(_tupleHandler.HasUnmarshalledTupleElements(tuple));
    }

    [Fact]
    public void HasUnmarshalledTupleElements_WithOnlyPrimitives_ReturnsFalse()
    {
        // All-frozen-primitive tuple — P/Invoke type equals C# type for every element, so the raw
        // ValueTuple path marshals correctly. Must NOT be falsely rejected.
        var tuple = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Bool"),
            new NamedTypeSpec("Swift.Double")
        });

        Assert.False(_tupleHandler.HasUnmarshalledTupleElements(tuple));
    }

    [Fact]
    public void HasUnmarshalledTupleElements_WithPointerType_ReturnsFalse()
    {
        // UnsafeMutablePointer<T> is IntPtr in BOTH contexts (modulo the System. prefix). The Norm()
        // helper folds "System.IntPtr" == "IntPtr" so the prefix difference is not a false mismatch.
        var pointerType = new NamedTypeSpec("Swift.UnsafeMutablePointer");
        pointerType.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        var tuple = new TupleTypeSpec(new List<TypeSpec>
        {
            pointerType,
            new NamedTypeSpec("Swift.Int")
        });

        Assert.False(_tupleHandler.HasUnmarshalledTupleElements(tuple));
    }

    #endregion

    #region IsCdeclBufferMarshallableTuple (class-element @_cdecl buffer path)

    [Fact]
    public void IsCdeclBufferMarshallableTuple_WithAllPrimitives_ReturnsTrue()
    {
        // The original all-primitive buffer path — every element is a cdecl scalar.
        var tuple = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Bool"),
            new NamedTypeSpec("Swift.Double")
        });

        Assert.True(_tupleHandler.IsCdeclBufferMarshallableTuple(tuple));
    }

    [Fact]
    public void IsCdeclBufferMarshallableTuple_WithClassAndPrimitive_ReturnsTrue()
    {
        // (Class, primitive) — the v1 extension. The class element occupies a single pointer-width
        // slot written as its object handle; the primitive is written by value.
        var tuple = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("ImagePipeline.ImageTask"),
            new NamedTypeSpec("Swift.Bool")
        });

        Assert.True(_tupleHandler.IsCdeclBufferMarshallableTuple(tuple));
    }

    [Fact]
    public void IsCdeclBufferMarshallableTuple_ClassTuple_IsAlsoUnmarshalledButNowBufferable()
    {
        // The combined validator gate is (HasUnmarshalledTupleElements && !IsCdeclBufferMarshallableTuple).
        // A (Class, primitive) tuple trips the unmarshalled gate (class P/Invoke IntPtr != C# wrapper),
        // but is now buffer-marshallable — so the AND is false and the member emits instead of skipping.
        var tuple = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("ImagePipeline.ImageTask"),
            new NamedTypeSpec("Swift.Bool")
        });

        Assert.True(_tupleHandler.HasUnmarshalledTupleElements(tuple));
        Assert.True(_tupleHandler.IsCdeclBufferMarshallableTuple(tuple));
    }

    [Fact]
    public void IsCdeclBufferMarshallableTuple_WithNonFrozenStructElement_ReturnsFalse()
    {
        // Non-frozen struct (ClassWithOpaquePayload) is projected as a C# class but Swift stores it
        // INLINE at resilient value size — a handle/IntPtr write would corrupt the slot. Stays fail-closed.
        var tuple = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("ImagePipeline.ImageResponse"),
            new NamedTypeSpec("Swift.Int")
        });

        Assert.False(_tupleHandler.IsCdeclBufferMarshallableTuple(tuple));
    }

    [Fact]
    public void IsCdeclBufferMarshallableTuple_WithSingleProtocolExistentialElement_ReturnsFalse()
    {
        // Single-protocol (EC1) existential `any P` may BOX a value-type conformer at +1 and so needs
        // per-element owned-payload teardown — out of scope for the borrowed buffer path. Bare-Any (EC0)
        // is excluded for the same reason. Only composition (EC2+) existentials, which are always +0
        // borrowed via GetExistentialContainer, are bufferable (see the composition test below).
        var existentialElement = new NamedTypeSpec("TestModule.SomeProtocol") { IsAny = true };
        var tuple = new TupleTypeSpec(new List<TypeSpec>
        {
            existentialElement,
            new NamedTypeSpec("Swift.Int")
        });

        Assert.False(_tupleHandler.IsCdeclBufferMarshallableTuple(tuple));
    }

    [Fact]
    public void IsCdeclBufferMarshallableTuple_WithCompositionExistentialElement_ReturnsTrue()
    {
        // Composition (EC2+) existential `any P & Q` is sized by a fixed-stride container
        // (GetExistentialTypeMetadata(count)) and projected through the ALWAYS-borrowed
        // GetExistentialContainer() path — no boxing, no per-element teardown — so its container is
        // bit-copied into the slot and kept valid by the source tuple's keep-alive.
        var composition = new ProtocolListTypeSpec(new[]
        {
            new NamedTypeSpec("TestModule.Nameable"),
            new NamedTypeSpec("TestModule.Ageable")
        });
        var tuple = new TupleTypeSpec(new List<TypeSpec>
        {
            composition,
            new NamedTypeSpec("Swift.Int")
        });

        Assert.True(_tupleHandler.IsCdeclBufferMarshallableTuple(tuple));
    }

    [Fact]
    public void IsCdeclBufferMarshallableTuple_WithStringElement_ReturnsTrue()
    {
        // Swift.String is a 16-byte frozen value projected as a Swift.SwiftString that owns its storage;
        // its borrowed 16-byte value is bit-copied into the slot and the source tuple is kept alive
        // across the call (the v2 extension — no fresh mint, no Dispose, same borrow model as a class).
        var tuple = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.String"),
            new NamedTypeSpec("Swift.Int")
        });

        Assert.True(_tupleHandler.IsCdeclBufferMarshallableTuple(tuple));
    }

    [Fact]
    public void IsCdeclBufferMarshallableTuple_CompositionExistentialTuple_IsAlsoUnmarshalledButNowBufferable()
    {
        // Same combined gate for a composition-existential element: unmarshalled (ExistentialContainerN
        // P/Invoke form != C# composition interface) yet buffer-marshallable, so the member emits.
        var composition = new ProtocolListTypeSpec(new[]
        {
            new NamedTypeSpec("TestModule.Nameable"),
            new NamedTypeSpec("TestModule.Ageable")
        });
        var tuple = new TupleTypeSpec(new List<TypeSpec>
        {
            composition,
            new NamedTypeSpec("Swift.Int")
        });

        Assert.True(_tupleHandler.HasUnmarshalledTupleElements(tuple));
        Assert.True(_tupleHandler.IsCdeclBufferMarshallableTuple(tuple));
    }

    [Fact]
    public void IsCdeclBufferMarshallableTuple_OverArityWithBufferableElements_ReturnsFalse()
    {
        // Eight String elements are each individually buffer-marshallable, but
        // TypeMetadata.GetTupleTypeMetadataFromElements throws above 7 — so an over-arity tuple must
        // stay fail-closed at generation (parity with IsSupportedTuple's MaxSupportedTupleElements),
        // not slip through to throw at runtime.
        var tuple = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.String"),
            new NamedTypeSpec("Swift.String"),
            new NamedTypeSpec("Swift.String"),
            new NamedTypeSpec("Swift.String"),
            new NamedTypeSpec("Swift.String"),
            new NamedTypeSpec("Swift.String"),
            new NamedTypeSpec("Swift.String"),
            new NamedTypeSpec("Swift.String")
        });

        Assert.Equal(8, tuple.Elements.Count);
        Assert.False(_tupleHandler.IsCdeclBufferMarshallableTuple(tuple));
    }

    [Fact]
    public void IsCdeclBufferMarshallableTuple_WithOptionalElement_ReturnsFalse()
    {
        // Optional<Int> projects to SwiftOptional<T> (a multi-field carrier), not a pointer slot.
        var optionalInt = new NamedTypeSpec("Swift.Optional");
        optionalInt.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        var tuple = new TupleTypeSpec(new List<TypeSpec>
        {
            optionalInt,
            new NamedTypeSpec("Swift.Bool")
        });

        Assert.False(_tupleHandler.IsCdeclBufferMarshallableTuple(tuple));
    }

    [Fact]
    public void IsCdeclBufferMarshallableTuple_WithEmptyTuple_ReturnsFalse()
    {
        Assert.False(_tupleHandler.IsCdeclBufferMarshallableTuple(TupleTypeSpec.Empty));
    }

    [Fact]
    public void IsCdeclBufferMarshallableElement_PrimitiveScalar_ReturnsTrue()
    {
        Assert.True(_tupleHandler.IsCdeclBufferMarshallableElement(new NamedTypeSpec("Swift.Int")));
    }

    [Fact]
    public void IsCdeclBufferMarshallableElement_PureSwiftClass_ReturnsTrue()
    {
        Assert.True(_tupleHandler.IsCdeclBufferMarshallableElement(new NamedTypeSpec("ImagePipeline.ImageTask")));
    }

    [Fact]
    public void IsCdeclBufferMarshallableElement_NonFrozenStruct_ReturnsFalse()
    {
        // Kind=Struct (not Class) — even though projected as a C# class, stored inline by Swift.
        Assert.False(_tupleHandler.IsCdeclBufferMarshallableElement(new NamedTypeSpec("ImagePipeline.ImageResponse")));
    }

    [Fact]
    public void IsCdeclBufferMarshallableElement_UnknownType_ReturnsFalse()
    {
        // Not in the database → no TypeRecord → not bufferable.
        Assert.False(_tupleHandler.IsCdeclBufferMarshallableElement(new NamedTypeSpec("SomeModule.Unknown")));
    }

    [Fact]
    public void IsCdeclBufferMarshallableElement_SwiftString_ReturnsTrue()
    {
        // Swift.String — 16-byte borrowed value slot (v2).
        Assert.True(_tupleHandler.IsCdeclBufferMarshallableElement(new NamedTypeSpec("Swift.String")));
    }

    [Fact]
    public void IsCdeclBufferMarshallableElement_CompositionExistential_ReturnsTrue()
    {
        // EC2 composition — always-borrowed fixed-stride container (v3).
        var composition = new ProtocolListTypeSpec(new[]
        {
            new NamedTypeSpec("TestModule.Nameable"),
            new NamedTypeSpec("TestModule.Ageable")
        });
        Assert.True(_tupleHandler.IsCdeclBufferMarshallableElement(composition));
    }

    [Fact]
    public void IsCdeclBufferMarshallableElement_SingleProtocolExistential_ReturnsFalse()
    {
        // EC1 single-protocol existential may box a value-type conformer — stays fail-closed.
        var existential = new NamedTypeSpec("TestModule.SomeProtocol") { IsAny = true };
        Assert.False(_tupleHandler.IsCdeclBufferMarshallableElement(existential));
    }

    #endregion

    #region IsCompositionExistentialElement / TupleElementNeedsBorrowKeepAlive

    [Fact]
    public void IsCompositionExistentialElement_TwoProtocols_ReturnsTrue()
    {
        var composition = new ProtocolListTypeSpec(new[]
        {
            new NamedTypeSpec("TestModule.Nameable"),
            new NamedTypeSpec("TestModule.Ageable")
        });
        Assert.True(_tupleHandler.IsCompositionExistentialElement(composition));
        Assert.Equal(2, _tupleHandler.GetCompositionExistentialElementProtocolCount(composition));
    }

    [Fact]
    public void IsCompositionExistentialElement_SingleProtocol_ReturnsFalse()
    {
        // One non-marker protocol → EC1, not a composition.
        var single = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.Nameable") });
        Assert.False(_tupleHandler.IsCompositionExistentialElement(single));
    }

    [Fact]
    public void IsCompositionExistentialElement_NonExistential_ReturnsFalse()
    {
        Assert.False(_tupleHandler.IsCompositionExistentialElement(new NamedTypeSpec("Swift.Int")));
    }

    [Fact]
    public void IsCompositionExistentialElement_MixedObjCSwiftComposition_ReturnsFalse()
    {
        // any Foundation.NSCopying & TestModule.Nameable: IsSupportedExistential admits it (both are
        // protocols, neither an ObjC root class), and GetNonMarkerProtocols keeps both (count 2). But the
        // PUBLIC element type filters the ObjC protocol out (GetEffectiveProtocols), collapsing to a
        // single INameable whose proxy is ISwiftExistentialConvertible<ExistentialContainer1> — while the
        // ABI slot/cast would use EC2. That filtered-count mismatch must stay fail-closed.
        var mixed = new ProtocolListTypeSpec(new[]
        {
            new NamedTypeSpec("Foundation.NSCopying"),
            new NamedTypeSpec("TestModule.Nameable")
        });
        Assert.False(_tupleHandler.IsCompositionExistentialElement(mixed));
        Assert.False(_tupleHandler.IsCdeclBufferMarshallableElement(mixed));
    }

    [Fact]
    public void TupleElementNeedsBorrowKeepAlive_BorrowedKinds_ReturnTrue()
    {
        // Class, String, and composition existential all alias the source's ARC root.
        Assert.True(_tupleHandler.TupleElementNeedsBorrowKeepAlive(new NamedTypeSpec("ImagePipeline.ImageTask")));
        Assert.True(_tupleHandler.TupleElementNeedsBorrowKeepAlive(new NamedTypeSpec("Swift.String")));
        var composition = new ProtocolListTypeSpec(new[]
        {
            new NamedTypeSpec("TestModule.Nameable"),
            new NamedTypeSpec("TestModule.Ageable")
        });
        Assert.True(_tupleHandler.TupleElementNeedsBorrowKeepAlive(composition));
    }

    [Fact]
    public void TupleElementNeedsBorrowKeepAlive_Primitive_ReturnsFalse()
    {
        // Written by value — no borrowed alias, no keep-alive needed.
        Assert.False(_tupleHandler.TupleElementNeedsBorrowKeepAlive(new NamedTypeSpec("Swift.Int")));
    }

    #endregion

    #region TupleTypeSpec Kind Tests

    [Fact]
    public void TupleTypeSpec_HasCorrectKind()
    {
        var tuple = new TupleTypeSpec();

        Assert.Equal(TypeSpecKind.Tuple, tuple.Kind);
    }

    #endregion

    #region Bound Generic Tuple Element Support (T1)

    [Fact]
    public void IsSupportedTuple_WithBoundGenericElement_ReturnsTrue()
    {
        // Tuple (Optional<Int>, Bool) — Optional<Int> is a bound generic element
        // where Optional is in the database and Int is a supported element type.
        var optionalInt = new NamedTypeSpec("Swift.Optional");
        optionalInt.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));

        var tuple = new TupleTypeSpec(new List<TypeSpec>
        {
            optionalInt,
            new NamedTypeSpec("Swift.Bool")
        });

        Assert.True(_tupleHandler.IsSupportedTuple(tuple));
    }

    [Fact]
    public void IsSupportedTuple_WithBoundGenericElementBaseNotInDb_ReturnsFalse()
    {
        // Tuple (Unknown<Int>, Bool) — Unknown is not in the database
        var unknownGeneric = new NamedTypeSpec("SomeModule.Unknown");
        unknownGeneric.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));

        var tuple = new TupleTypeSpec(new List<TypeSpec>
        {
            unknownGeneric,
            new NamedTypeSpec("Swift.Bool")
        });

        Assert.False(_tupleHandler.IsSupportedTuple(tuple));
    }

    #endregion

    #region Helper Methods

    private static ArgumentDecl CreateArgumentDecl(string name, TypeSpec typeSpec)
    {
        return new ArgumentDecl
        {
            Name = name,
            PrivateName = name,
            SwiftTypeSpec = typeSpec,
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    #endregion

    #region Mock Type Database

    private class MockTypeDatabase : ITypeDatabase
    {
        private readonly Dictionary<string, TypeRecord> _types;

        public string AsyncLibraryName => null!;

        public MockTypeDatabase()
        {
            _types = new Dictionary<string, TypeRecord>
            {
                ["Swift.Int"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.NIntType,
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.Frozen,
                    Kind = TypeRecordKind.Struct
                },
                ["Swift.Bool"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Boolean"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Bool"),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.Frozen,
                    Kind = TypeRecordKind.Struct
                },
                ["Swift.Double"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Double"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Double"),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.Frozen,
                    Kind = TypeRecordKind.Struct
                },
                ["Swift.String"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftString"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.String"),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.Frozen,
                    Kind = TypeRecordKind.Struct
                },
                ["Swift.Optional"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftOptional"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Optional"),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement,
                    Kind = TypeRecordKind.Struct
                },
                // Non-frozen struct (ClassWithOpaquePayload)
                ["ImagePipeline.ImageResponse"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("ImagePipeline", "ImageResponse"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("ImagePipeline.ImageResponse"),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.None, // NOT frozen
                    Kind = TypeRecordKind.Struct
                },
                // Swift class
                ["ImagePipeline.ImageTask"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("ImagePipeline", "ImageTask"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("ImagePipeline.ImageTask"),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.RequiresMemoryManagement,
                    Kind = TypeRecordKind.Class
                },
                // Pointer type — must return the exact TypeDatabaseExtensions.IntPtrType instance
                // so TranslateBoundGenericToCSharp recognizes it as a pointer (reference equality check)
                ["Swift.UnsafeMutablePointer"] = TypeDatabaseExtensions.IntPtrType,
                // SIMD alias target — Swift.SIMD3<Swift.Float> collapses to this non-generic record
                // via the shared BoundGenericTranslation.TryResolveSimdAliasCSharp short-circuit.
                ["simd.simd_float3"] = new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System.Numerics", "Vector3"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("simd.simd_float3"),
                    MetadataAccessor = "",
                    Flags = TypeRecordFlags.Frozen,
                    Kind = TypeRecordKind.Struct
                }
            };
        }

        public bool IsTypeProcessed(SwiftTypeName swiftTypeName) => _types.ContainsKey(swiftTypeName.ModuleQualifiedName);

        public bool TryGetTypeRecord(SwiftTypeName swiftTypeName, out TypeRecord record)
        {
            return _types.TryGetValue(swiftTypeName.ModuleQualifiedName, out record);
        }

        public string GetLibraryPath(string moduleName) => "";

        public void UpdateTypeRecord(SwiftTypeName name, TypeRecord record) { }
    }

    #endregion
}
