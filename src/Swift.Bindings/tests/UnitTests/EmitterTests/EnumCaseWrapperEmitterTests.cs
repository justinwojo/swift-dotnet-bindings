// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for EnumCaseWrapperEmitter: per-enum-case @_cdecl wrappers that route
/// complex enum case constructors through C calling convention.
/// </summary>
public class EnumCaseWrapperEmitterTests
{
    #region ShouldEmitCaseFactoryWrapper Guard Tests

    [Fact]
    public void ShouldEmit_SimpleCase_ReturnsTrue()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment();
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var enumDecl = CreateEnumDecl("Status", moduleDecl);
        var caseDecl = CreateCaseDecl("active", new List<TypeSpec> { new NamedTypeSpec("Swift.Int") }, moduleDecl);

        Assert.True(EnumCaseWrapperEmitter.ShouldEmitCaseFactoryWrapper(enumDecl, caseDecl, typeDb));
    }

    [Fact]
    public void ShouldEmit_NoAsyncLibraryName_ReturnsFalse()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment();
        // AsyncLibraryName is null — not in xcframework mode

        var enumDecl = CreateEnumDecl("Status", moduleDecl);
        var caseDecl = CreateCaseDecl("active", new List<TypeSpec> { new NamedTypeSpec("Swift.Int") }, moduleDecl);

        Assert.False(EnumCaseWrapperEmitter.ShouldEmitCaseFactoryWrapper(enumDecl, caseDecl, typeDb));
    }

    [Fact]
    public void ShouldEmit_GenericEnum_ReturnsFalse()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment();
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var enumDecl = CreateEnumDecl("Result", moduleDecl);
        enumDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new("τ_0_0", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };
        var caseDecl = CreateCaseDecl("success", new List<TypeSpec> { new NamedTypeSpec("τ_0_0") }, moduleDecl);

        Assert.False(EnumCaseWrapperEmitter.ShouldEmitCaseFactoryWrapper(enumDecl, caseDecl, typeDb));
    }

    [Fact]
    public void ShouldEmit_EnumNestedInGenericParent_StampedParams_ReturnsFalse()
    {
        // A non-generic-looking enum nested in a generic parent
        // (struct Outer<T> { enum E { case n(Int32) } }). The Swift ABI digester stamps
        // the outer generic signature onto the nested decl, so the parser populates the
        // nested enum's GenericParameters with the inherited T — IsGeneric is therefore
        // already true and the case factory is correctly declined. This pins that the
        // reachable shape never emits a case-factory wrapper (constructing Outer<T>.E
        // needs T's metadata, which the @_cdecl wrapper does not route).
        var (moduleDecl, typeDb) = CreateTestEnvironment();
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var outer = CreateEnumDecl("Outer", moduleDecl);
        outer.GenericParameters = new List<GenericArgumentDecl>
        {
            new("τ_0_0", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };
        var nested = CreateEnumDecl("E", moduleDecl);
        nested.ParentDecl = outer;
        nested.GenericParameters = new List<GenericArgumentDecl>
        {
            new("τ_0_0", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };
        var caseDecl = CreateCaseDecl("number", new List<TypeSpec> { new NamedTypeSpec("Swift.Int") }, moduleDecl);

        Assert.False(EnumCaseWrapperEmitter.ShouldEmitCaseFactoryWrapper(nested, caseDecl, typeDb));
    }

    [Fact]
    public void ShouldEmit_EnumNestedInGenericParent_OwnSignatureAbsent_ReturnsFalse()
    {
        // Fail-closed edge: a nested enum whose OWN generic signature is absent (empty
        // GenericParameters → IsGeneric false) but whose parent is generic. Without the
        // IsInheritedGenericContext check this slips past the IsGeneric guard and the
        // @_cdecl wrapper is emitted with no metadata-routing, producing a C#/Swift
        // signature mismatch (the C# side injects the inherited PInvokeHelperContext's
        // metadata params; the wrapper omits them). The gate must decline on the
        // inherited generic context too, matching the constructor/method/property
        // wrapper emitters.
        var (moduleDecl, typeDb) = CreateTestEnvironment();
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var outer = CreateEnumDecl("Outer", moduleDecl);
        outer.GenericParameters = new List<GenericArgumentDecl>
        {
            new("τ_0_0", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };
        var nested = CreateEnumDecl("E", moduleDecl);
        nested.ParentDecl = outer; // GenericParameters intentionally left empty
        var caseDecl = CreateCaseDecl("number", new List<TypeSpec> { new NamedTypeSpec("Swift.Int") }, moduleDecl);

        Assert.False(EnumCaseWrapperEmitter.ShouldEmitCaseFactoryWrapper(nested, caseDecl, typeDb));
    }

    [Fact]
    public void ShouldEmit_ClosureAssociatedValue_ReturnsFalse()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment();
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var enumDecl = CreateEnumDecl("Action", moduleDecl);
        var closureSpec = new ClosureTypeSpec(new NamedTypeSpec("Swift.Int"), TupleTypeSpec.Empty);
        var caseDecl = CreateCaseDecl("perform", new List<TypeSpec> { closureSpec }, moduleDecl);

        Assert.False(EnumCaseWrapperEmitter.ShouldEmitCaseFactoryWrapper(enumDecl, caseDecl, typeDb));
    }

    [Fact]
    public void ShouldEmit_StringAssociatedValue_ReturnsTrue()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment();
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var enumDecl = CreateEnumDecl("Message", moduleDecl);
        var caseDecl = CreateCaseDecl("text", new List<TypeSpec> { new NamedTypeSpec("Swift.String") }, moduleDecl);

        Assert.True(EnumCaseWrapperEmitter.ShouldEmitCaseFactoryWrapper(enumDecl, caseDecl, typeDb));
    }

    [Fact]
    public void ShouldEmit_MultipleAssociatedValues_ReturnsTrue()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment();
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var enumDecl = CreateEnumDecl("Event", moduleDecl);
        var caseDecl = CreateCaseDecl("userAction", new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.String")
        }, moduleDecl);

        Assert.True(EnumCaseWrapperEmitter.ShouldEmitCaseFactoryWrapper(enumDecl, caseDecl, typeDb));
    }

    [Fact]
    public void ShouldEmit_OptionalAssociatedValue_ReturnsTrue()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment();
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var enumDecl = CreateEnumDecl("Config", moduleDecl);
        var optionalSpec = new NamedTypeSpec("Swift.Optional");
        optionalSpec.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        var caseDecl = CreateCaseDecl("limit", new List<TypeSpec> { optionalSpec }, moduleDecl);

        Assert.True(EnumCaseWrapperEmitter.ShouldEmitCaseFactoryWrapper(enumDecl, caseDecl, typeDb));
    }

    [Fact]
    public void ShouldEmit_OptionalMetatypeAssociatedValue_ReturnsFalse()
    {
        // Bug-2 pin: Optional<AnyClass.Type> collapses to AnyType in MetatypeStrategy
        // and has no @_cdecl-compatible representation. The associated value must be
        // rejected here, otherwise CdeclParamMapper.Map renders an invalid Swift
        // wrapper (bare "Type" / "(any AnyClass.Type).self") and a bare "Type"
        // C# parameter that fails to compile.
        var (moduleDecl, typeDb) = CreateTestEnvironment();
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var enumDecl = CreateEnumDecl("Registration", moduleDecl);
        var optionalMetatype = new NamedTypeSpec("Swift.Optional");
        optionalMetatype.GenericParameters.Add(new NamedTypeSpec("AnyClass.Type"));
        var caseDecl = CreateCaseDecl("registered", new List<TypeSpec> { optionalMetatype }, moduleDecl);

        Assert.False(EnumCaseWrapperEmitter.ShouldEmitCaseFactoryWrapper(enumDecl, caseDecl, typeDb));
    }

    [Fact]
    public void ShouldEmit_BareMetatypeAssociatedValue_ReturnsFalse()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment();
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var enumDecl = CreateEnumDecl("Registration", moduleDecl);
        var caseDecl = CreateCaseDecl("registered",
            new List<TypeSpec> { new NamedTypeSpec("AnyClass.Type") }, moduleDecl);

        Assert.False(EnumCaseWrapperEmitter.ShouldEmitCaseFactoryWrapper(enumDecl, caseDecl, typeDb));
    }

    [Fact]
    public void ShouldEmit_PrimitiveTuple_ReturnsTrue()
    {
        // Tuple of primitives: C# ValueTuple<long, bool> has same memory layout as Swift (Int, Bool)
        var (moduleDecl, typeDb) = CreateTestEnvironment();
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var enumDecl = CreateEnumDecl("Coord", moduleDecl);
        var tupleSpec = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Bool")
        });
        var caseDecl = CreateCaseDecl("point", new List<TypeSpec> { tupleSpec }, moduleDecl);

        Assert.True(EnumCaseWrapperEmitter.ShouldEmitCaseFactoryWrapper(enumDecl, caseDecl, typeDb));
    }

    [Fact]
    public void ShouldEmit_TupleWithString_ReturnsFalse()
    {
        // Tuple with string: C# IntPtr (8 bytes) vs Swift String (16 bytes) — layout mismatch
        var (moduleDecl, typeDb) = CreateTestEnvironment();
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var enumDecl = CreateEnumDecl("Message", moduleDecl);
        var tupleSpec = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.String"),
            new NamedTypeSpec("Swift.Int")
        });
        var caseDecl = CreateCaseDecl("tagged", new List<TypeSpec> { tupleSpec }, moduleDecl);

        Assert.False(EnumCaseWrapperEmitter.ShouldEmitCaseFactoryWrapper(enumDecl, caseDecl, typeDb));
    }

    [Fact]
    public void ShouldEmit_TupleWithExistential_ReturnsFalse()
    {
        // Tuple with protocol existential: ExistentialContainer layout may not match tuple element
        var (moduleDecl, typeDb) = CreateTestEnvironment();
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var enumDecl = CreateEnumDecl("Event", moduleDecl);
        var tupleSpec = new TupleTypeSpec(new List<TypeSpec>
        {
            new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.SomeProtocol") }),
            new NamedTypeSpec("Swift.Int")
        });
        var caseDecl = CreateCaseDecl("failed", new List<TypeSpec> { tupleSpec }, moduleDecl);

        Assert.False(EnumCaseWrapperEmitter.ShouldEmitCaseFactoryWrapper(enumDecl, caseDecl, typeDb));
    }

    [Fact]
    public void ShouldEmit_TupleWithGenericContainer_ReturnsFalse()
    {
        // Tuple with Optional<T>: C# uses lowered IntPtr, layout mismatch
        var (moduleDecl, typeDb) = CreateTestEnvironment();
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var enumDecl = CreateEnumDecl("Config", moduleDecl);
        var optionalSpec = new NamedTypeSpec("Swift.Optional");
        optionalSpec.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        var tupleSpec = new TupleTypeSpec(new List<TypeSpec>
        {
            optionalSpec,
            new NamedTypeSpec("Swift.Bool")
        });
        var caseDecl = CreateCaseDecl("setting", new List<TypeSpec> { tupleSpec }, moduleDecl);

        Assert.False(EnumCaseWrapperEmitter.ShouldEmitCaseFactoryWrapper(enumDecl, caseDecl, typeDb));
    }

    [Fact]
    public void ShouldEmit_TupleWithClass_ReturnsFalse()
    {
        // Tuple with class: C# tuple stores managed object reference, but Swift expects
        // a native pointer (Unmanaged.fromOpaque). Taking &valueTuple doesn't rewrite
        // managed references into native handles.
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithClassAndEnum();
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var enumDecl = CreateEnumDecl("Wrapper", moduleDecl);
        var tupleSpec = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("TestModule.MyClass"),
            new NamedTypeSpec("Swift.Int")
        });
        var caseDecl = CreateCaseDecl("tagged", new List<TypeSpec> { tupleSpec }, moduleDecl);

        Assert.False(EnumCaseWrapperEmitter.ShouldEmitCaseFactoryWrapper(enumDecl, caseDecl, typeDb));
    }

    [Fact]
    public void ShouldEmit_TupleWithEnum_ReturnsFalse()
    {
        // Tuple with enum: the non-tuple @_cdecl path reconstructs enums via init(rawValue:)
        // or unsafe memory load. Tuple pointer transport skips that per-element reconstruction,
        // so widened C# representation vs compact Swift storage would mismatch.
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithClassAndEnum();
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var enumDecl = CreateEnumDecl("Container", moduleDecl);
        var tupleSpec = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("TestModule.SimpleStatus"),
            new NamedTypeSpec("Swift.Bool")
        });
        var caseDecl = CreateCaseDecl("item", new List<TypeSpec> { tupleSpec }, moduleDecl);

        Assert.False(EnumCaseWrapperEmitter.ShouldEmitCaseFactoryWrapper(enumDecl, caseDecl, typeDb));
    }

    #endregion

    #region Symbol Name Tests

    [Fact]
    public void GetCaseFactorySymbolName_CorrectFormat()
    {
        var symbol = EnumCaseWrapperEmitter.GetCaseFactorySymbolName(
            "TestModule", "Status", "active", "$s10TestModule6StatusO6activeyACSi_tcACmF");

        Assert.StartsWith("SBW_TestModule_Status_active_", symbol);
        Assert.Equal(8, symbol.Split('_').Last().Length); // 8-char hash
    }

    #endregion

    #region Emission Tests

    [Fact]
    public void EmitWrapper_PrimitiveAssociatedValue_EmitsCorrectSwift()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment();
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var enumDecl = CreateEnumDecl("Status", moduleDecl);
        var caseDecl = CreateCaseDecl("active", new List<TypeSpec> { new NamedTypeSpec("Swift.Int") }, moduleDecl);

        var dummyMethod = CreateDummyMethod("active", enumDecl, moduleDecl);
        var env = new MethodEnvironment(dummyMethod, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);

        EnumCaseWrapperEmitter.EmitSwiftCaseFactoryWrapper(
            swiftWriter, enumDecl, caseDecl, "SBW_TestModule_Status_active_12345678", env, ctx);

        var output = sw.ToString();
        Assert.Contains("@_cdecl(\"SBW_TestModule_Status_active_12345678\")", output);
        Assert.Contains("_ resultPtr: UnsafeMutableRawPointer", output);
        Assert.Contains("TestModule.Status.active(", output);
        Assert.Contains("resultPtr.initializeMemory(as: TestModule.Status.self", output);
    }

    [Fact]
    public void EmitWrapper_StringAssociatedValue_EmitsUtf8Reconstruction()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment();
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var enumDecl = CreateEnumDecl("Message", moduleDecl);
        var stringSpec = new NamedTypeSpec("Swift.String") { TypeLabel = "text" };
        var caseDecl = CreateCaseDecl("text", new List<TypeSpec> { stringSpec }, moduleDecl);

        var dummyMethod = CreateDummyMethod("text", enumDecl, moduleDecl);
        var env = new MethodEnvironment(dummyMethod, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);

        EnumCaseWrapperEmitter.EmitSwiftCaseFactoryWrapper(
            swiftWriter, enumDecl, caseDecl, "SBW_TestModule_Message_text_12345678", env, ctx);

        var output = sw.ToString();
        // String params use UTF-8 pointer + length reconstruction (NativeAOT-safe)
        Assert.Contains("Utf8Ptr: UnsafePointer<UInt8>", output);
        Assert.Contains("Utf8Len: Int", output);
        Assert.Contains("UnsafeBufferPointer", output);
    }

    [Fact]
    public void EmitWrapper_DuplicateSymbol_OnlyEmitsOnce()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment();
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var enumDecl = CreateEnumDecl("Status", moduleDecl);
        var caseDecl = CreateCaseDecl("active", new List<TypeSpec> { new NamedTypeSpec("Swift.Int") }, moduleDecl);

        var dummyMethod = CreateDummyMethod("active", enumDecl, moduleDecl);
        var env = new MethodEnvironment(dummyMethod, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);

        var symbol = "SBW_TestModule_Status_active_dedup123";
        EnumCaseWrapperEmitter.EmitSwiftCaseFactoryWrapper(swiftWriter, enumDecl, caseDecl, symbol, env, ctx);
        EnumCaseWrapperEmitter.EmitSwiftCaseFactoryWrapper(swiftWriter, enumDecl, caseDecl, symbol, env, ctx);

        var output = sw.ToString();
        Assert.Equal(1, CountOccurrences(output, $"@_cdecl(\"{symbol}\")"));
    }

    [Fact]
    public void EmitWrapper_UnlabeledAssociatedValue_NoColonInCallArg()
    {
        // Regression test: unlabeled associated values (TypeLabel=null) set Name="_".
        // GetCdeclParamMapping must produce no argument label, not ": ".
        var (moduleDecl, typeDb) = CreateTestEnvironment();
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var enumDecl = CreateEnumDecl("Error", moduleDecl);
        // Unlabeled associated value: TypeLabel is null for enum cases that carry a bare value without an argument label.
        var intSpec = new NamedTypeSpec("Swift.Int") { TypeLabel = null };
        var caseDecl = CreateCaseDecl("statusCodeUnacceptable", new List<TypeSpec> { intSpec }, moduleDecl);

        var dummyMethod = CreateDummyMethod("statusCodeUnacceptable", enumDecl, moduleDecl);
        var env = new MethodEnvironment(dummyMethod, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);

        EnumCaseWrapperEmitter.EmitSwiftCaseFactoryWrapper(
            swiftWriter, enumDecl, caseDecl, "SBW_TestModule_Error_statusCodeUnacceptable_12345678", env, ctx);

        var output = sw.ToString();
        // Must NOT contain "(: value0)" — that's the bug pattern
        Assert.DoesNotContain("(: ", output);
        // Must contain the enum case construction without label
        Assert.Contains("TestModule.Error.statusCodeUnacceptable(value0)", output);
    }

    [Fact]
    public void EmitWrapper_LabeledAssociatedValue_HasLabelInCallArg()
    {
        // Verify labeled associated values still emit "label: val" correctly
        var (moduleDecl, typeDb) = CreateTestEnvironment();
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var enumDecl = CreateEnumDecl("Event", moduleDecl);
        var intSpec = new NamedTypeSpec("Swift.Int") { TypeLabel = "code" };
        var caseDecl = CreateCaseDecl("error", new List<TypeSpec> { intSpec }, moduleDecl);

        var dummyMethod = CreateDummyMethod("error", enumDecl, moduleDecl);
        var env = new MethodEnvironment(dummyMethod, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);

        EnumCaseWrapperEmitter.EmitSwiftCaseFactoryWrapper(
            swiftWriter, enumDecl, caseDecl, "SBW_TestModule_Event_error_12345678", env, ctx);

        var output = sw.ToString();
        Assert.Contains("code: ", output);
    }

    [Fact]
    public void EmitWrapper_TupleAssociatedValue_DestructuresByFieldAccess()
    {
        // S8: When an enum case has a single associated value that is a tuple
        // (e.g., case fixed(width: CGFloat, height: CGFloat)), the ABI stores it as
        // one tuple parameter. The wrapper must destructure it into individual
        // constructor arguments: .fixed(width: val.width, height: val.height)
        var (moduleDecl, typeDb) = CreateTestEnvironment();
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var enumDecl = CreateEnumDecl("Layout", moduleDecl);

        // Create a tuple type with labeled elements (simulating ABI representation)
        var widthElem = new NamedTypeSpec("CoreFoundation.CGFloat") { TypeLabel = "width" };
        var heightElem = new NamedTypeSpec("CoreFoundation.CGFloat") { TypeLabel = "height" };
        var tupleSpec = new TupleTypeSpec(new List<TypeSpec> { widthElem, heightElem });

        // The ABI represents this as a single associated value that is a tuple
        var caseDecl = CreateCaseDecl("fixed", new List<TypeSpec> { tupleSpec }, moduleDecl);

        var dummyMethod = CreateDummyMethod("fixed", enumDecl, moduleDecl);
        var env = new MethodEnvironment(dummyMethod, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);

        EnumCaseWrapperEmitter.EmitSwiftCaseFactoryWrapper(
            swiftWriter, enumDecl, caseDecl, "SBW_TestModule_Layout_fixed_12345678", env, ctx);

        var output = sw.ToString();

        // Must destructure by field access, not pass tuple directly
        Assert.Contains(".width", output);
        Assert.Contains(".height", output);
        // Must use labeled constructor arguments
        Assert.Contains("width:", output);
        Assert.Contains("height:", output);
        // Must reference the enum case constructor
        Assert.Contains("TestModule.Layout.fixed(", output);
    }

    [Fact]
    public void EmitWrapper_UnlabeledTupleAssociatedValue_DestructuresByIndex()
    {
        // Unlabeled tuple elements use positional index access (.0, .1)
        var (moduleDecl, typeDb) = CreateTestEnvironment();
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var enumDecl = CreateEnumDecl("Pair", moduleDecl);

        var elem0 = new NamedTypeSpec("Swift.Int");
        var elem1 = new NamedTypeSpec("Swift.Bool");
        var tupleSpec = new TupleTypeSpec(new List<TypeSpec> { elem0, elem1 });

        var caseDecl = CreateCaseDecl("values", new List<TypeSpec> { tupleSpec }, moduleDecl);

        var dummyMethod = CreateDummyMethod("values", enumDecl, moduleDecl);
        var env = new MethodEnvironment(dummyMethod, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);

        EnumCaseWrapperEmitter.EmitSwiftCaseFactoryWrapper(
            swiftWriter, enumDecl, caseDecl, "SBW_TestModule_Pair_values_12345678", env, ctx);

        var output = sw.ToString();

        // Must use positional index access for unlabeled elements
        Assert.Contains(".0", output);
        Assert.Contains(".1", output);
    }

    [Fact]
    public void EmitWrapper_SingleUnlabeledTuplePayload_RewrapsArgsIntoTuple()
    {
        // case shipped((Int32, BoxedCounter)) — ONE unlabeled tuple-typed associated value.
        // The ABI flattens it into N AssociatedValues, identical in shape to `case foo(A, B)`
        // (two separate values). The parser sets IsSingleTuplePayload from the enum-case
        // function-type's paren nesting; the wrapper must re-wrap the flattened args into a
        // single tuple — .shipped((value0, value1)) — or Swift rejects the malformed
        // .shipped(value0, value1) ("enum case 'shipped' expects a single parameter of
        // type '(A, B)'") and the @_cdecl wrapper is stripped, dangling its C# P/Invoke.
        var (moduleDecl, typeDb) = CreateTestEnvironment();
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var enumDecl = CreateEnumDecl("TaggedDelivery", moduleDecl);
        var caseDecl = CreateCaseDecl("shipped", new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Bool")
        }, moduleDecl);
        caseDecl.IsSingleTuplePayload = true;

        var dummyMethod = CreateDummyMethod("shipped", enumDecl, moduleDecl);
        var env = new MethodEnvironment(dummyMethod, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);

        EnumCaseWrapperEmitter.EmitSwiftCaseFactoryWrapper(
            swiftWriter, enumDecl, caseDecl, "SBW_TestModule_TaggedDelivery_shipped_12345678", env, ctx);

        var output = sw.ToString();
        // Re-wrapped into a single tuple argument (double opening paren).
        Assert.Contains("TestModule.TaggedDelivery.shipped((", output);
        // NOT the broken flattened form .shipped(value0, value1) (single paren before the value).
        Assert.DoesNotContain("TestModule.TaggedDelivery.shipped(value", output);
    }

    [Fact]
    public void EmitWrapper_TwoSeparateValues_NotRewrappedWhenFlagUnset()
    {
        // Control: the same flattened AssociatedValues shape WITHOUT the flag (a genuine
        // `case foo(A, B)`) must keep the standard flat construction, never gaining a tuple.
        var (moduleDecl, typeDb) = CreateTestEnvironment();
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var enumDecl = CreateEnumDecl("Outcome", moduleDecl);
        var caseDecl = CreateCaseDecl("success", new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Bool")
        }, moduleDecl);
        // IsSingleTuplePayload deliberately left false.

        var dummyMethod = CreateDummyMethod("success", enumDecl, moduleDecl);
        var env = new MethodEnvironment(dummyMethod, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);

        EnumCaseWrapperEmitter.EmitSwiftCaseFactoryWrapper(
            swiftWriter, enumDecl, caseDecl, "SBW_TestModule_Outcome_success_12345678", env, ctx);

        var output = sw.ToString();
        // Flat construction (single opening paren), no spurious tuple wrap.
        Assert.Contains("TestModule.Outcome.success(value", output);
        Assert.DoesNotContain("TestModule.Outcome.success((", output);
    }

    [Fact]
    public void EmitWrapper_NonTupleAssociatedValues_NoDestructuring()
    {
        // Multiple separate associated values (not a single tuple) don't need destructuring
        var (moduleDecl, typeDb) = CreateTestEnvironment();
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var enumDecl = CreateEnumDecl("Event", moduleDecl);
        var caseDecl = CreateCaseDecl("userAction", new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int") { TypeLabel = "code" },
            new NamedTypeSpec("Swift.Bool") { TypeLabel = "flag" }
        }, moduleDecl);

        var dummyMethod = CreateDummyMethod("userAction", enumDecl, moduleDecl);
        var env = new MethodEnvironment(dummyMethod, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);

        EnumCaseWrapperEmitter.EmitSwiftCaseFactoryWrapper(
            swiftWriter, enumDecl, caseDecl, "SBW_TestModule_Event_userAction_12345678", env, ctx);

        var output = sw.ToString();

        // Standard path: no destructuring needed
        Assert.Contains("TestModule.Event.userAction(", output);
        // Should NOT contain tuple field access patterns
        Assert.DoesNotContain(".width", output);
        Assert.DoesNotContain(".height", output);
    }

    #endregion

    #region Helpers

    private static int CountOccurrences(string source, string search)
    {
        int count = 0;
        int idx = 0;
        while ((idx = source.IndexOf(search, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += search.Length;
        }
        return count;
    }

    private static EnumCaseDecl CreateCaseDecl(string name, List<TypeSpec> assocValues, ModuleDecl moduleDecl)
    {
        return new EnumCaseDecl
        {
            Name = name,
            MangledName = $"$s10TestModule_{name}_mangled",
            AssociatedValues = assocValues,
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
    }

    private static MethodDecl CreateDummyMethod(string name, EnumDecl enumDecl, ModuleDecl moduleDecl)
    {
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule_{name}_mangled",
            MethodType = MethodType.Static,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = enumDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };
    }

    private static EnumDecl CreateEnumDecl(string name, ModuleDecl moduleDecl)
    {
        var decl = new EnumDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}ON",
            MetadataAccessor = $"$s10TestModule{name.Length}{name}OMa",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            Cases = new List<EnumCaseDecl>(),
            IsFrozen = false,
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        moduleDecl.Types.Add(decl);
        return decl;
    }

    private static (ModuleDecl moduleDecl, TypeDatabase typeDb) CreateTestEnvironment()
    {
        var typeDb = new TypeDatabase();

        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.NIntType,
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                MetadataAccessor = "$sSiMa",
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
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.String"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.Runtime", "SwiftString"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.String"),
                MetadataAccessor = "$sSSMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Struct
            });
        typeDb.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        typeDb.AddModuleDatabase(testModule);

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

        return (moduleDecl, typeDb);
    }

    private static (ModuleDecl moduleDecl, TypeDatabase typeDb) CreateTestEnvironmentWithClassAndEnum()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment();

        var testModule = new ModuleTypeDatabase("TestModule_extra", "/tmp/TestModule.dylib");
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
            SwiftTypeName.FromModuleQualifiedName("TestModule.SimpleStatus"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "SimpleStatus"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.SimpleStatus"),
                MetadataAccessor = "$s10TestModule12SimpleStatusOMa",
                RawValueTypeName = "Int",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.SimpleEnum,
                Kind = TypeRecordKind.Enum
            });
        typeDb.AddModuleDatabase(testModule);

        return (moduleDecl, typeDb);
    }

    #endregion
}
