// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for WitnessDispatchEmitter Swift accessor code generation.
/// </summary>
public class WitnessDispatchEmitterTests
{
    private readonly TypeDatabase _typeDatabase;
    private readonly WitnessDispatchEmitter _emitter;

    public WitnessDispatchEmitterTests()
    {
        _typeDatabase = new TypeDatabase();
        var module = new ModuleTypeDatabase("TestModule", "/fake/path");
        _typeDatabase.AddModuleDatabase(module);
        _emitter = new WitnessDispatchEmitter(_typeDatabase, NullLogger.Instance, "TestModule");
    }

    #region Swift Accessor Generation Tests

    [Fact]
    public void EmitPropertyGetter_GeneratesSilgenName()
    {
        var protocolDecl = CreateProtocolWithProperty("HasValue", "value", new NamedTypeSpec("Swift.Int32"));
        var output = EmitDispatch(protocolDecl);

        Assert.Contains("@_silgen_name(\"SBW_HasValue_get_value_0\")", output);
    }

    [Fact]
    public void EmitPropertyGetter_GeneratesFreeFunction()
    {
        var protocolDecl = CreateProtocolWithProperty("HasValue", "value", new NamedTypeSpec("Swift.Int32"));
        var output = EmitDispatch(protocolDecl);

        Assert.Contains("@_silgen_name(\"SBW_HasValue_free_get_value_0\")", output);
        Assert.Contains("ptr.assumingMemoryBound(to: Int32.self).deinitialize(count: 1)", output);
        Assert.Contains("ptr.deallocate()", output);
    }

    [Fact]
    public void EmitPropertyGetter_UsesModuleQualifiedProtocolName()
    {
        var protocolDecl = CreateProtocolWithProperty("HasValue", "value", new NamedTypeSpec("Swift.Int32"));
        var output = EmitDispatch(protocolDecl);

        Assert.Contains("(any TestModule.HasValue).self", output);
    }

    [Fact]
    public void EmitPropertyGetter_UsesContainerPtrLoad()
    {
        var protocolDecl = CreateProtocolWithProperty("HasValue", "value", new NamedTypeSpec("Swift.Int32"));
        var output = EmitDispatch(protocolDecl);

        Assert.Contains("containerPtr.load(as: (any TestModule.HasValue).self)", output);
    }

    [Fact]
    public void EmitPropertyGetter_AccessesPropertyByName()
    {
        var protocolDecl = CreateProtocolWithProperty("HasValue", "myProp", new NamedTypeSpec("Swift.Double"));
        var output = EmitDispatch(protocolDecl);

        Assert.Contains("existential.myProp", output);
    }

    [Fact]
    public void EmitPropertyGetter_AllocatesReturnType()
    {
        var protocolDecl = CreateProtocolWithProperty("HasValue", "value", new NamedTypeSpec("Swift.Int32"));
        var output = EmitDispatch(protocolDecl);

        Assert.Contains("UnsafeMutablePointer<Int32>.allocate(capacity: 1)", output);
        Assert.Contains("ptr.initialize(to: result)", output);
        Assert.Contains("return UnsafeMutableRawPointer(ptr)", output);
    }

    [Fact]
    public void EmitPropertyGetter_BoolType_GeneratesAccessor()
    {
        var protocolDecl = CreateProtocolWithProperty("HasFlag", "isActive", new NamedTypeSpec("Swift.Bool"));
        var output = EmitDispatch(protocolDecl);

        Assert.Contains("@_silgen_name(\"SBW_HasFlag_get_isActive_0\")", output);
        Assert.Contains("UnsafeMutablePointer<Bool>.allocate(capacity: 1)", output);
    }

    [Fact]
    public void EmitPropertyGetter_FloatType_GeneratesAccessor()
    {
        var protocolDecl = CreateProtocolWithProperty("HasScore", "score", new NamedTypeSpec("Swift.Float"));
        var output = EmitDispatch(protocolDecl);

        Assert.Contains("UnsafeMutablePointer<Float>.allocate(capacity: 1)", output);
    }

    #endregion

    #region Method Accessor Generation Tests

    [Fact]
    public void EmitMethod_WithReturn_GeneratesAccessorAndFree()
    {
        var protocolDecl = CreateProtocolWithMethod("HasValue", "getValue",
            returnType: new NamedTypeSpec("Swift.Int32"));
        var output = EmitDispatch(protocolDecl);

        Assert.Contains("@_silgen_name(\"SBW_HasValue_method_getValue_0\")", output);
        Assert.Contains("@_silgen_name(\"SBW_HasValue_free_method_getValue_0\")", output);
        Assert.Contains("existential.getValue()", output);
    }

    [Fact]
    public void EmitMethod_VoidReturn_NoFreeFunction()
    {
        var protocolDecl = CreateProtocolWithVoidMethod("HasValue", "reset");
        var output = EmitDispatch(protocolDecl);

        Assert.Contains("@_silgen_name(\"SBW_HasValue_method_reset_0\")", output);
        Assert.DoesNotContain("SBW_HasValue_free_method_reset_0", output);
    }

    [Fact]
    public void EmitMethod_WithBlittableParams_GeneratesLoadCalls()
    {
        var protocolDecl = CreateProtocolWithMethodAndParams("HasValue", "addValue",
            returnType: TupleTypeSpec.Empty,
            paramTypes: new[] { ("amount", new NamedTypeSpec("Swift.Int32") as TypeSpec) });
        var output = EmitDispatch(protocolDecl);

        Assert.Contains("arg0Ptr.load(as: Int32.self)", output);
    }

    [Fact]
    public void EmitMethod_WithMultipleParams_GeneratesAllLoadCalls()
    {
        var protocolDecl = CreateProtocolWithMethodAndParams("Calculator", "add",
            returnType: new NamedTypeSpec("Swift.Int32"),
            paramTypes: new[]
            {
                ("a", new NamedTypeSpec("Swift.Int32") as TypeSpec),
                ("b", new NamedTypeSpec("Swift.Int32") as TypeSpec)
            });
        var output = EmitDispatch(protocolDecl);

        Assert.Contains("arg0Ptr.load(as: Int32.self)", output);
        Assert.Contains("arg1Ptr.load(as: Int32.self)", output);
    }

    [Fact]
    public void EmitMethod_WithLabeledParams_UsesLabelsInCall()
    {
        var protocolDecl = CreateProtocolWithMethodAndParams("HasValue", "setValue",
            returnType: TupleTypeSpec.Empty,
            paramTypes: new[] { ("newValue", new NamedTypeSpec("Swift.Int32") as TypeSpec) });
        var output = EmitDispatch(protocolDecl);

        Assert.Contains("existential.setValue(newValue: arg0)", output);
    }

    [Fact]
    public void EmitMethod_VoidReturnWithParams_NoReturnStatement()
    {
        var protocolDecl = CreateProtocolWithMethodAndParams("HasValue", "setValue",
            returnType: TupleTypeSpec.Empty,
            paramTypes: new[] { ("_", new NamedTypeSpec("Swift.Int32") as TypeSpec) });
        var output = EmitDispatch(protocolDecl);

        Assert.DoesNotContain("return UnsafeMutableRawPointer", output);
        Assert.DoesNotContain("UnsafeMutablePointer<", output);
    }

    #endregion

    #region Marshalability Tests

    [Fact]
    public void IsPropertyGetterDispatchable_BlittableInt_ReturnsTrue()
    {
        var property = CreateProperty("value", new NamedTypeSpec("Swift.Int"));
        Assert.True(_emitter.IsPropertyGetterDispatchable(property));
    }

    [Fact]
    public void IsPropertyGetterDispatchable_BlittableInt32_ReturnsTrue()
    {
        var property = CreateProperty("value", new NamedTypeSpec("Swift.Int32"));
        Assert.True(_emitter.IsPropertyGetterDispatchable(property));
    }

    [Fact]
    public void IsPropertyGetterDispatchable_BlittableBool_ReturnsTrue()
    {
        var property = CreateProperty("flag", new NamedTypeSpec("Swift.Bool"));
        Assert.True(_emitter.IsPropertyGetterDispatchable(property));
    }

    [Fact]
    public void IsPropertyGetterDispatchable_BlittableFloat_ReturnsTrue()
    {
        var property = CreateProperty("score", new NamedTypeSpec("Swift.Float"));
        Assert.True(_emitter.IsPropertyGetterDispatchable(property));
    }

    [Fact]
    public void IsPropertyGetterDispatchable_BlittableDouble_ReturnsTrue()
    {
        var property = CreateProperty("value", new NamedTypeSpec("Swift.Double"));
        Assert.True(_emitter.IsPropertyGetterDispatchable(property));
    }

    [Fact]
    public void IsPropertyGetterDispatchable_String_ReturnsTrue()
    {
        var property = CreateProperty("name", new NamedTypeSpec("Swift.String"));
        Assert.True(_emitter.IsPropertyGetterDispatchable(property));
    }

    [Fact]
    public void IsPropertyGetterDispatchable_Array_ReturnsFalse()
    {
        var arrayType = new NamedTypeSpec("Swift.Array");
        arrayType.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        var property = CreateProperty("items", arrayType);
        Assert.False(_emitter.IsPropertyGetterDispatchable(property));
    }

    [Fact]
    public void IsMethodDispatchable_AllBlittable_ReturnsTrue()
    {
        var method = CreateMethod("getValue",
            returnType: new NamedTypeSpec("Swift.Int32"));
        Assert.True(_emitter.IsMethodDispatchable(method));
    }

    [Fact]
    public void IsMethodDispatchable_VoidReturnNoParams_ReturnsTrue()
    {
        var method = CreateMethod("reset", returnType: TupleTypeSpec.Empty);
        Assert.True(_emitter.IsMethodDispatchable(method));
    }

    [Fact]
    public void IsMethodDispatchable_StringReturn_ReturnsTrue()
    {
        var method = CreateMethod("getName",
            returnType: new NamedTypeSpec("Swift.String"));
        Assert.True(_emitter.IsMethodDispatchable(method));
    }

    [Fact]
    public void IsMethodDispatchable_StringParam_ReturnsTrue()
    {
        var method = CreateMethodWithParams("process",
            returnType: TupleTypeSpec.Empty,
            paramTypes: new[] { ("text", new NamedTypeSpec("Swift.String") as TypeSpec) });
        Assert.True(_emitter.IsMethodDispatchable(method));
    }

    [Fact]
    public void IsMethodDispatchable_MixedBlittableAndStringParams_ReturnsTrue()
    {
        var method = CreateMethodWithParams("process",
            returnType: new NamedTypeSpec("Swift.Int32"),
            paramTypes: new[]
            {
                ("count", new NamedTypeSpec("Swift.Int32") as TypeSpec),
                ("name", new NamedTypeSpec("Swift.String") as TypeSpec)
            });
        Assert.True(_emitter.IsMethodDispatchable(method));
    }

    [Fact]
    public void IsMethodDispatchable_ArrayParam_ReturnsFalse()
    {
        var arrayType = new NamedTypeSpec("Swift.Array");
        arrayType.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        var method = CreateMethodWithParams("process",
            returnType: TupleTypeSpec.Empty,
            paramTypes: new[] { ("items", arrayType as TypeSpec) });
        Assert.False(_emitter.IsMethodDispatchable(method));
    }

    [Fact]
    public void IsMethodDispatchable_ThrowingBlittableMethod_ReturnsTrue()
    {
        var method = CreateMethod("getValue", returnType: new NamedTypeSpec("Swift.Int32"));
        method.Throws = true;
        Assert.True(_emitter.IsMethodDispatchable(method));
    }

    [Fact]
    public void IsMethodDispatchable_AsyncMethod_ReturnsFalse()
    {
        var method = CreateMethod("getValue", returnType: new NamedTypeSpec("Swift.Int32"));
        method.IsAsync = true;
        Assert.False(_emitter.IsMethodDispatchable(method));
    }

    [Fact]
    public void IsMethodDispatchable_AsyncThrowingMethod_ReturnsFalse()
    {
        var method = CreateMethod("getValue", returnType: new NamedTypeSpec("Swift.Int32"));
        method.Throws = true;
        method.IsAsync = true;
        Assert.False(_emitter.IsMethodDispatchable(method));
    }

    [Fact]
    public void GetBlittableCSharpType_BlittableSwiftInt_ReturnsNint()
    {
        var typeSpec = new NamedTypeSpec("Swift.Int");
        var result = _emitter.GetBlittableCSharpType(typeSpec);
        Assert.Equal("nint", result);
    }

    [Fact]
    public void GetBlittableCSharpType_BlittableSwiftInt32_ReturnsInt()
    {
        var typeSpec = new NamedTypeSpec("Swift.Int32");
        var result = _emitter.GetBlittableCSharpType(typeSpec);
        Assert.Equal("int", result);
    }

    [Fact]
    public void GetBlittableCSharpType_BlittableSwiftBool_ReturnsBool()
    {
        var typeSpec = new NamedTypeSpec("Swift.Bool");
        var result = _emitter.GetBlittableCSharpType(typeSpec);
        Assert.Equal("bool", result);
    }

    [Fact]
    public void GetBlittableCSharpType_NonBlittableString_ReturnsNull()
    {
        var typeSpec = new NamedTypeSpec("Swift.String");
        var result = _emitter.GetBlittableCSharpType(typeSpec);
        Assert.Null(result);
    }

    [Fact]
    public void GetBlittableCSharpType_UnknownType_ReturnsNull()
    {
        var typeSpec = new NamedTypeSpec("MyModule.CustomType");
        var result = _emitter.GetBlittableCSharpType(typeSpec);
        Assert.Null(result);
    }

    #endregion

    #region Naming Convention Tests

    [Fact]
    public void GetAccessorSymbol_FormatsCorrectly()
    {
        var symbol = WitnessDispatchEmitter.GetAccessorSymbol("HasValue", "get", "value", 0);
        Assert.Equal("SBW_HasValue_get_value_0", symbol);
    }

    [Fact]
    public void GetFreeSymbol_FormatsCorrectly()
    {
        var symbol = WitnessDispatchEmitter.GetFreeSymbol("HasValue", "get", "value", 0);
        Assert.Equal("SBW_HasValue_free_get_value_0", symbol);
    }

    [Fact]
    public void GetAccessorSymbol_MethodWithIndex_IncludesIndex()
    {
        var symbol = WitnessDispatchEmitter.GetAccessorSymbol("Calculator", "method", "add", 2);
        Assert.Equal("SBW_Calculator_method_add_2", symbol);
    }

    [Fact]
    public void OverloadDisambiguation_DifferentMethods_GetDifferentIndices()
    {
        var protocolDecl = CreateSimpleProtocol("Calculator");
        protocolDecl.Methods.Add(CreateMethodWithParams("compute",
            returnType: new NamedTypeSpec("Swift.Int32"),
            paramTypes: new[] { ("x", new NamedTypeSpec("Swift.Int32") as TypeSpec) }));
        protocolDecl.Methods.Add(CreateMethodWithParams("compute",
            returnType: new NamedTypeSpec("Swift.Int32"),
            paramTypes: new[]
            {
                ("x", new NamedTypeSpec("Swift.Int32") as TypeSpec),
                ("y", new NamedTypeSpec("Swift.Int32") as TypeSpec)
            }));

        var output = EmitDispatch(protocolDecl);

        Assert.Contains("SBW_Calculator_method_compute_0", output);
        Assert.Contains("SBW_Calculator_method_compute_1", output);
    }

    #endregion

    #region Non-Dispatchable Members Tests

    [Fact]
    public void EmitDispatch_StringProperty_EmitsUtf8SlicePattern()
    {
        var protocolDecl = CreateProtocolWithProperty("HasName", "name", new NamedTypeSpec("Swift.String"));
        var output = EmitDispatch(protocolDecl);

        Assert.Contains("@_silgen_name(\"SBW_HasName_get_name_0\")", output);
        Assert.Contains("SBW_Utf8Slice", output);
        Assert.Contains("Array(result.utf8)", output);
        Assert.Contains("withUnsafeBufferPointer", output);
    }

    [Fact]
    public void EmitDispatch_StringMethod_EmitsUtf8SliceReturn()
    {
        var protocolDecl = CreateProtocolWithMethod("HasName", "getName",
            returnType: new NamedTypeSpec("Swift.String"));
        var output = EmitDispatch(protocolDecl);

        Assert.Contains("@_silgen_name(\"SBW_HasName_method_getName_0\")", output);
        Assert.Contains("let result: String = existential.getName()", output);
        Assert.Contains("SBW_Utf8Slice", output);
    }

    [Fact]
    public void EmitDispatch_MixedMembers_BothBlittableAndStringEmitted()
    {
        var protocolDecl = CreateSimpleProtocol("MixedProtocol");

        // Blittable property
        protocolDecl.Properties.Add(CreateProperty("count", new NamedTypeSpec("Swift.Int32")));

        // String property
        protocolDecl.Properties.Add(CreateProperty("name", new NamedTypeSpec("Swift.String")));

        var output = EmitDispatch(protocolDecl);

        Assert.Contains("SBW_MixedProtocol_get_count_0", output);
        Assert.Contains("SBW_MixedProtocol_get_name_0", output);
    }

    [Fact]
    public void EmitDispatch_NonDispatchableArrayProperty_WithUnresolvableElement_NoOutput()
    {
        // Array with an unresolvable generic element type should not be dispatchable
        var arrayType = new NamedTypeSpec("Swift.Array");
        arrayType.GenericParameters.Add(new NamedTypeSpec("SomeModule.UnknownType"));
        var protocolDecl = CreateSimpleProtocol("HasItems");
        protocolDecl.Properties.Add(CreateProperty("items", arrayType));
        var output = EmitDispatch(protocolDecl);

        Assert.DoesNotContain("@_silgen_name", output);
    }

    [Fact]
    public void EmitDispatch_SetterOnlyProperty_EmitsSetter()
    {
        // Properties with only setter should emit setter dispatch
        var protocolDecl = CreateSimpleProtocol("WriteOnly");
        var property = new PropertyDecl
        {
            Name = "value",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int32"),
            IsStatic = false,
            HasStorage = false,
            Accessors = new List<AccessorDecl>
            {
                new SetAccessorDecl { Method = CreateMethodDecl("value_set") }
            },
            ParentDecl = null,
            ModuleDecl = null
        };
        protocolDecl.Properties.Add(property);

        var output = EmitDispatch(protocolDecl);

        Assert.Contains("SBW_WriteOnly_set_value_0", output);
    }

    #endregion

    #region String Dispatch Type Tests

    [Fact]
    public void IsStringDispatchType_SwiftString_ReturnsTrue()
    {
        Assert.True(WitnessDispatchEmitter.IsStringDispatchType(new NamedTypeSpec("Swift.String")));
    }

    [Fact]
    public void IsStringDispatchType_SwiftInt_ReturnsFalse()
    {
        Assert.False(WitnessDispatchEmitter.IsStringDispatchType(new NamedTypeSpec("Swift.Int")));
    }

    [Fact]
    public void IsStringDispatchType_Null_ReturnsFalse()
    {
        Assert.False(WitnessDispatchEmitter.IsStringDispatchType(null));
    }

    [Fact]
    public void IsStringType_SwiftString_ReturnsTrue()
    {
        Assert.True(WitnessDispatchEmitter.IsStringType(new NamedTypeSpec("Swift.String")));
    }

    [Fact]
    public void IsStringType_NonString_ReturnsFalse()
    {
        Assert.False(WitnessDispatchEmitter.IsStringType(new NamedTypeSpec("Swift.Int")));
    }

    #endregion

    #region Property Setter Tests

    [Fact]
    public void IsPropertySetterDispatchable_BlittableType_ReturnsTrue()
    {
        var property = CreateProperty("value", new NamedTypeSpec("Swift.Int32"));
        Assert.True(_emitter.IsPropertySetterDispatchable(property));
    }

    [Fact]
    public void IsPropertySetterDispatchable_StringType_ReturnsTrue()
    {
        var property = CreateProperty("name", new NamedTypeSpec("Swift.String"));
        Assert.True(_emitter.IsPropertySetterDispatchable(property));
    }

    [Fact]
    public void IsPropertySetterDispatchable_ArrayType_ReturnsFalse()
    {
        var arrayType = new NamedTypeSpec("Swift.Array");
        arrayType.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        var property = CreateProperty("items", arrayType);
        Assert.False(_emitter.IsPropertySetterDispatchable(property));
    }

    [Fact]
    public void EmitDispatch_BlittableSetter_EmitsTypedPointeeAssignment()
    {
        var protocolDecl = CreateProtocolWithGetterAndSetter("HasValue", "value", new NamedTypeSpec("Swift.Int32"));
        var output = EmitDispatch(protocolDecl);

        Assert.Contains("SBW_HasValue_set_value_0", output);
        Assert.Contains("containerPtr: UnsafeMutableRawPointer", output);
        Assert.Contains("typedPtr.pointee = existential", output);
    }

    [Fact]
    public void EmitDispatch_StringSetter_EmitsUtf8SliceDecode()
    {
        var protocolDecl = CreateProtocolWithGetterAndSetter("HasName", "name", new NamedTypeSpec("Swift.String"));
        var output = EmitDispatch(protocolDecl);

        Assert.Contains("SBW_HasName_set_name_0", output);
        Assert.Contains("SBW_Utf8Slice", output);
        Assert.Contains("String(unsafeUninitializedCapacity:", output);
    }

    [Fact]
    public void EmitDispatch_SetterNoFreeFunction()
    {
        var protocolDecl = CreateProtocolWithGetterAndSetter("HasValue", "value", new NamedTypeSpec("Swift.Int32"));
        var output = EmitDispatch(protocolDecl);

        // Setters should not have free functions
        Assert.DoesNotContain("SBW_HasValue_free_set_value_0", output);
    }

    #endregion

    #region SBW_Utf8Slice Struct Tests

    [Fact]
    public void EmitDispatch_StringProperty_EmitsUtf8SliceStructOnce()
    {
        var protocolDecl = CreateSimpleProtocol("MultiString");
        protocolDecl.Properties.Add(CreateProperty("firstName", new NamedTypeSpec("Swift.String")));
        protocolDecl.Properties.Add(CreateProperty("lastName", new NamedTypeSpec("Swift.String")));

        var output = EmitDispatch(protocolDecl);

        Assert.Contains("@frozen", output);
        Assert.Contains("public struct SBW_Utf8Slice", output);
        // Should only appear once
        Assert.Equal(1, EmitterTestHelpers.CountOccurrences(output, "public struct SBW_Utf8Slice"));
    }

    [Fact]
    public void EmitDispatch_BlittableOnly_NoUtf8SliceStruct()
    {
        var protocolDecl = CreateProtocolWithProperty("HasCount", "count", new NamedTypeSpec("Swift.Int32"));
        var output = EmitDispatch(protocolDecl);

        Assert.DoesNotContain("SBW_Utf8Slice", output);
    }

    #endregion

    #region String Method Emission Tests

    [Fact]
    public void EmitMethod_StringParam_EmitsUtf8SliceDecode()
    {
        var protocolDecl = CreateProtocolWithMethodAndParams("HasName", "setName",
            returnType: TupleTypeSpec.Empty,
            paramTypes: new[] { ("name", new NamedTypeSpec("Swift.String") as TypeSpec) });
        var output = EmitDispatch(protocolDecl);

        Assert.Contains("arg0Slice = arg0Ptr.load(as: SBW_Utf8Slice.self)", output);
        Assert.Contains("String(unsafeUninitializedCapacity:", output);
    }

    [Fact]
    public void EmitMethod_MixedParams_EmitsBothPatterns()
    {
        var protocolDecl = CreateProtocolWithMethodAndParams("Calculator", "compute",
            returnType: new NamedTypeSpec("Swift.Int32"),
            paramTypes: new[]
            {
                ("count", new NamedTypeSpec("Swift.Int32") as TypeSpec),
                ("label", new NamedTypeSpec("Swift.String") as TypeSpec)
            });
        var output = EmitDispatch(protocolDecl);

        // Blittable param loads directly
        Assert.Contains("arg0Ptr.load(as: Int32.self)", output);
        // String param uses Utf8Slice
        Assert.Contains("arg1Slice = arg1Ptr.load(as: SBW_Utf8Slice.self)", output);
    }

    [Fact]
    public void EmitMethod_StringReturn_EmitsUtf8SliceFree()
    {
        var protocolDecl = CreateProtocolWithMethod("HasName", "getName",
            returnType: new NamedTypeSpec("Swift.String"));
        var output = EmitDispatch(protocolDecl);

        Assert.Contains("SBW_HasName_free_method_getName_0", output);
        Assert.Contains("slicePtr.pointee.ptr.deallocate()", output);
    }

    #endregion

    #region Static Blittability Checks

    [Fact]
    public void IsBlittablePrimitive_Int_ReturnsTrue()
    {
        Assert.True(WitnessDispatchEmitter.IsBlittablePrimitive("int"));
    }

    [Fact]
    public void IsBlittablePrimitive_Long_ReturnsTrue()
    {
        Assert.True(WitnessDispatchEmitter.IsBlittablePrimitive("long"));
    }

    [Fact]
    public void IsBlittablePrimitive_Bool_ReturnsTrue()
    {
        Assert.True(WitnessDispatchEmitter.IsBlittablePrimitive("bool"));
    }

    [Fact]
    public void IsBlittablePrimitive_String_ReturnsFalse()
    {
        Assert.False(WitnessDispatchEmitter.IsBlittablePrimitive("string"));
    }

    [Fact]
    public void IsBlittablePrimitive_SwiftString_ReturnsFalse()
    {
        Assert.False(WitnessDispatchEmitter.IsBlittablePrimitive("Swift.Runtime.SwiftString"));
    }

    #endregion

    #region Existential Return Classification Tests

    [Fact]
    public void ClassifyMethodDispatch_ThrowingExistentialReturn_ReturnsExistentialReturn()
    {
        var emitter = CreateExistentialEmitter(out _);
        var method = CreateMethod("connect", CreateExistentialReturnType("TestModule.Card"));
        method.Throws = true;
        Assert.Equal(MethodDispatchKind.ExistentialReturn, emitter.ClassifyMethodDispatch(method));
    }

    [Fact]
    public void ClassifyMethodDispatch_NonThrowingExistentialReturn_ReturnsExistentialReturn()
    {
        var emitter = CreateExistentialEmitter(out _);
        var method = CreateMethod("getBasicChannel", CreateExistentialReturnType("TestModule.CardChannel"));
        Assert.Equal(MethodDispatchKind.ExistentialReturn, emitter.ClassifyMethodDispatch(method));
    }

    [Fact]
    public void ClassifyMethodDispatch_ThrowingBlittableReturn_ReturnsThrowingBlittableOrString()
    {
        var method = CreateMethod("getValue", new NamedTypeSpec("Swift.Int32"));
        method.Throws = true;
        Assert.Equal(MethodDispatchKind.ThrowingBlittableOrString, _emitter.ClassifyMethodDispatch(method));
    }

    [Fact]
    public void ClassifyMethodDispatch_AsyncExistentialReturn_NotDispatchable()
    {
        var emitter = CreateExistentialEmitter(out _);
        var method = CreateMethod("getAsync", CreateExistentialReturnType("TestModule.Card"));
        method.IsAsync = true;
        Assert.Equal(MethodDispatchKind.NotDispatchable, emitter.ClassifyMethodDispatch(method));
    }

    [Fact]
    public void ClassifyMethodDispatch_ExistentialReturnWithNonDispatchableParam_NotDispatchable()
    {
        var emitter = CreateExistentialEmitter(out _);
        var arrayType = new NamedTypeSpec("Swift.Array");
        arrayType.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        var method = CreateMethodWithParams("process",
            CreateExistentialReturnType("TestModule.Card"),
            new[] { ("items", arrayType as TypeSpec) });
        Assert.Equal(MethodDispatchKind.NotDispatchable, emitter.ClassifyMethodDispatch(method));
    }

    [Fact]
    public void ClassifyMethodDispatch_WellKnownExistentialReturn_NotDispatchable()
    {
        // "any Error" → AnyError, well-known type, not dispatchable via existential dispatch
        var emitter = CreateExistentialEmitter(out _);
        var errorType = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("Swift.Error") });
        var method = CreateMethod("getError", errorType);
        Assert.Equal(MethodDispatchKind.NotDispatchable, emitter.ClassifyMethodDispatch(method));
    }

    [Fact]
    public void ClassifyMethodDispatch_BlittableReturn_StillBlittableOrString()
    {
        var method = CreateMethod("getValue", new NamedTypeSpec("Swift.Int32"));
        Assert.Equal(MethodDispatchKind.BlittableOrString, _emitter.ClassifyMethodDispatch(method));
    }

    [Fact]
    public void ClassifyMethodDispatch_VoidNoParams_BlittableOrString()
    {
        var method = CreateMethod("reset", TupleTypeSpec.Empty);
        Assert.Equal(MethodDispatchKind.BlittableOrString, _emitter.ClassifyMethodDispatch(method));
    }

    [Fact]
    public void ClassifyMethodDispatch_ExistentialReturnWithStringParam_ReturnsExistentialReturn()
    {
        var emitter = CreateExistentialEmitter(out _);
        var method = CreateMethodWithParams("connect",
            CreateExistentialReturnType("TestModule.Card"),
            new[] { ("protocolString", new NamedTypeSpec("Swift.String") as TypeSpec) });
        method.Throws = true;
        Assert.Equal(MethodDispatchKind.ExistentialReturn, emitter.ClassifyMethodDispatch(method));
    }

    #endregion

    #region Existential Swift Emission Tests

    [Fact]
    public void EmitExistentialMethodAccessor_NonThrowing_UsesTypedInitialize()
    {
        var emitter = CreateExistentialEmitter(out var ctx);
        var protocolDecl = CreateSimpleProtocol("Card");
        protocolDecl.Methods.Add(CreateMethod("getBasicChannel", CreateExistentialReturnType("TestModule.CardChannel")));
        var output = EmitDispatchWithEmitter(emitter, protocolDecl, ctx);

        Assert.Contains("UnsafeMutablePointer<any TestModule.CardChannel>.allocate(capacity: 1)", output);
        Assert.Contains("ptr.initialize(to: result)", output);
    }

    [Fact]
    public void EmitExistentialMethodAccessor_Throwing_HasErrorOutParam()
    {
        var emitter = CreateExistentialEmitter(out var ctx);
        var protocolDecl = CreateSimpleProtocol("CardTerminal");
        var method = CreateMethodWithParams("connect",
            CreateExistentialReturnType("TestModule.Card"),
            new[] { ("protocolString", new NamedTypeSpec("Swift.String") as TypeSpec) });
        method.Throws = true;
        protocolDecl.Methods.Add(method);
        var output = EmitDispatchWithEmitter(emitter, protocolDecl, ctx);

        Assert.Contains("errorOut: UnsafeMutablePointer<UnsafeRawPointer?>", output);
    }

    [Fact]
    public void EmitExistentialMethodAccessor_FreeFunction_UsesDeinitialize()
    {
        var emitter = CreateExistentialEmitter(out var ctx);
        var protocolDecl = CreateSimpleProtocol("Card");
        protocolDecl.Methods.Add(CreateMethod("getBasicChannel", CreateExistentialReturnType("TestModule.CardChannel")));
        var output = EmitDispatchWithEmitter(emitter, protocolDecl, ctx);

        Assert.Contains("(any TestModule.CardChannel).self).deinitialize(count: 1)", output);
        Assert.Contains("ptr.deallocate()", output);
    }

    [Fact]
    public void EmitExistentialMethodAccessor_Throwing_UsesPassRetained()
    {
        var emitter = CreateExistentialEmitter(out var ctx);
        var protocolDecl = CreateSimpleProtocol("CardTerminal");
        var method = CreateMethod("connect", CreateExistentialReturnType("TestModule.Card"));
        method.Throws = true;
        protocolDecl.Methods.Add(method);
        var output = EmitDispatchWithEmitter(emitter, protocolDecl, ctx);

        Assert.Contains("Unmanaged.passRetained(error as AnyObject).toOpaque()", output);
    }

    [Fact]
    public void EmitExistentialMethodAccessor_Throwing_ReturnTypeIsOptional()
    {
        var emitter = CreateExistentialEmitter(out var ctx);
        var protocolDecl = CreateSimpleProtocol("CardTerminal");
        var method = CreateMethod("connect", CreateExistentialReturnType("TestModule.Card"));
        method.Throws = true;
        protocolDecl.Methods.Add(method);
        var output = EmitDispatchWithEmitter(emitter, protocolDecl, ctx);

        // Throwing pattern returns optional raw pointer (nil = error)
        Assert.Contains("-> UnsafeMutableRawPointer?", output);
        Assert.Contains("return nil", output);
    }

    [Fact]
    public void EmitExistentialMethodAccessor_NonThrowing_ReturnTypeIsNonOptional()
    {
        var emitter = CreateExistentialEmitter(out var ctx);
        var protocolDecl = CreateSimpleProtocol("Card");
        protocolDecl.Methods.Add(CreateMethod("getBasicChannel", CreateExistentialReturnType("TestModule.CardChannel")));
        var output = EmitDispatchWithEmitter(emitter, protocolDecl, ctx);

        // Non-throwing: the return line should be non-optional
        Assert.Contains("-> UnsafeMutableRawPointer {", output);
        Assert.DoesNotContain("-> UnsafeMutableRawPointer? {", output);
    }

    [Fact]
    public void EmitExistentialMethodAccessor_Throwing_EmitsErrorDescriptionInfra()
    {
        var emitter = CreateExistentialEmitter(out var ctx);
        var protocolDecl = CreateSimpleProtocol("CardTerminal");
        var method = CreateMethod("connect", CreateExistentialReturnType("TestModule.Card"));
        method.Throws = true;
        protocolDecl.Methods.Add(method);
        var output = EmitDispatchWithEmitter(emitter, protocolDecl, ctx);

        // Error infrastructure should be emitted for throwing existential methods
        Assert.Contains("SBW_GetErrorDescription", output);
        Assert.Contains("SBW_ReleaseError", output);
    }

    [Fact]
    public void EmitExistentialMethodAccessor_Throwing_EmitsTryCatch()
    {
        var emitter = CreateExistentialEmitter(out var ctx);
        var protocolDecl = CreateSimpleProtocol("CardTerminal");
        var method = CreateMethod("connect", CreateExistentialReturnType("TestModule.Card"));
        method.Throws = true;
        protocolDecl.Methods.Add(method);
        var output = EmitDispatchWithEmitter(emitter, protocolDecl, ctx);

        Assert.Contains("do {", output);
        Assert.Contains("try existential.", output);
        Assert.Contains("} catch {", output);
    }

    #endregion

    #region Existential IsMethodDispatchable Backward Compat

    [Fact]
    public void IsMethodDispatchable_ExistentialReturn_ReturnsTrue()
    {
        var emitter = CreateExistentialEmitter(out _);
        var method = CreateMethod("getBasicChannel", CreateExistentialReturnType("TestModule.CardChannel"));
        Assert.True(emitter.IsMethodDispatchable(method));
    }

    [Fact]
    public void IsMethodDispatchable_ThrowingExistentialReturn_ReturnsTrue()
    {
        var emitter = CreateExistentialEmitter(out _);
        var method = CreateMethod("connect", CreateExistentialReturnType("TestModule.Card"));
        method.Throws = true;
        Assert.True(emitter.IsMethodDispatchable(method));
    }

    #endregion

    #region Throwing Blittable/String/Void Classification Tests

    [Fact]
    public void ClassifyMethodDispatch_ThrowingVoidReturn_ReturnsThrowingBlittableOrString()
    {
        var method = CreateMethod("disconnect", TupleTypeSpec.Empty);
        method.Throws = true;
        Assert.Equal(MethodDispatchKind.ThrowingBlittableOrString, _emitter.ClassifyMethodDispatch(method));
    }

    [Fact]
    public void ClassifyMethodDispatch_ThrowingStringReturn_ReturnsThrowingBlittableOrString()
    {
        var method = CreateMethod("getName", new NamedTypeSpec("Swift.String"));
        method.Throws = true;
        Assert.Equal(MethodDispatchKind.ThrowingBlittableOrString, _emitter.ClassifyMethodDispatch(method));
    }

    [Fact]
    public void ClassifyMethodDispatch_ThrowingWithNonBlittableParam_NotDispatchable()
    {
        var arrayType = new NamedTypeSpec("Swift.Array");
        arrayType.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        var method = CreateMethodWithParams("process",
            new NamedTypeSpec("Swift.Int32"),
            new[] { ("items", arrayType as TypeSpec) });
        method.Throws = true;
        Assert.Equal(MethodDispatchKind.NotDispatchable, _emitter.ClassifyMethodDispatch(method));
    }

    [Fact]
    public void ClassifyMethodDispatch_AsyncThrowingBlittable_NotDispatchable()
    {
        var method = CreateMethod("getValue", new NamedTypeSpec("Swift.Int32"));
        method.Throws = true;
        method.IsAsync = true;
        Assert.Equal(MethodDispatchKind.NotDispatchable, _emitter.ClassifyMethodDispatch(method));
    }

    #endregion

    #region Throwing Blittable/String/Void Swift Emission Tests

    [Fact]
    public void EmitThrowingMethodAccessor_BlittableReturn_EmitsDoCatch()
    {
        var protocolDecl = CreateSimpleProtocol("TestProtocol");
        var method = CreateMethod("tryGetValue", new NamedTypeSpec("Swift.Int32"));
        method.Throws = true;
        protocolDecl.Methods.Add(method);
        var output = EmitDispatch(protocolDecl);

        Assert.Contains("do {", output);
        Assert.Contains("try existential.", output);
        Assert.Contains("} catch {", output);
    }

    [Fact]
    public void EmitThrowingMethodAccessor_VoidReturn_NoReturnType()
    {
        var protocolDecl = CreateSimpleProtocol("TestProtocol");
        var method = CreateMethod("disconnect", TupleTypeSpec.Empty);
        method.Throws = true;
        protocolDecl.Methods.Add(method);
        var output = EmitDispatch(protocolDecl);

        Assert.DoesNotContain("-> UnsafeMutableRawPointer", output);
        Assert.Contains("errorOut: UnsafeMutablePointer<UnsafeRawPointer?>", output);
    }

    [Fact]
    public void EmitThrowingMethodAccessor_VoidReturn_NoFreeFunction()
    {
        var protocolDecl = CreateSimpleProtocol("TestProtocol");
        var method = CreateMethod("disconnect", TupleTypeSpec.Empty);
        method.Throws = true;
        protocolDecl.Methods.Add(method);
        var output = EmitDispatch(protocolDecl);

        Assert.DoesNotContain("SBW_TestProtocol_free_method_disconnect_0", output);
    }

    [Fact]
    public void EmitThrowingMethodAccessor_BlittableReturn_EmitsFreeFunction()
    {
        var protocolDecl = CreateSimpleProtocol("TestProtocol");
        var method = CreateMethod("tryGetValue", new NamedTypeSpec("Swift.Int32"));
        method.Throws = true;
        protocolDecl.Methods.Add(method);
        var output = EmitDispatch(protocolDecl);

        Assert.Contains("SBW_TestProtocol_free_method_tryGetValue_0", output);
    }

    [Fact]
    public void EmitThrowingMethodAccessor_StringReturn_EmitsUtf8SliceInDoCatch()
    {
        var protocolDecl = CreateSimpleProtocol("TestProtocol");
        var method = CreateMethod("tryGetName", new NamedTypeSpec("Swift.String"));
        method.Throws = true;
        protocolDecl.Methods.Add(method);
        var output = EmitDispatch(protocolDecl);

        Assert.Contains("do {", output);
        Assert.Contains("try existential.", output);
        Assert.Contains("SBW_Utf8Slice", output);
        Assert.Contains("Array(result.utf8)", output);
    }

    [Fact]
    public void EmitThrowingMethodAccessor_EmitsErrorOutParam()
    {
        var protocolDecl = CreateSimpleProtocol("TestProtocol");
        var method = CreateMethod("tryGetValue", new NamedTypeSpec("Swift.Int32"));
        method.Throws = true;
        protocolDecl.Methods.Add(method);
        var output = EmitDispatch(protocolDecl);

        Assert.Contains("errorOut: UnsafeMutablePointer<UnsafeRawPointer?>", output);
    }

    [Fact]
    public void EmitThrowingMethodAccessor_EmitsPassRetainedError()
    {
        var protocolDecl = CreateSimpleProtocol("TestProtocol");
        var method = CreateMethod("tryGetValue", new NamedTypeSpec("Swift.Int32"));
        method.Throws = true;
        protocolDecl.Methods.Add(method);
        var output = EmitDispatch(protocolDecl);

        Assert.Contains("Unmanaged.passRetained(error as AnyObject).toOpaque()", output);
    }

    [Fact]
    public void EmitThrowingMethodAccessor_ValueReturning_ReturnsOptionalPointer()
    {
        var protocolDecl = CreateSimpleProtocol("TestProtocol");
        var method = CreateMethod("tryGetValue", new NamedTypeSpec("Swift.Int32"));
        method.Throws = true;
        protocolDecl.Methods.Add(method);
        var output = EmitDispatch(protocolDecl);

        Assert.Contains("-> UnsafeMutableRawPointer?", output);
        Assert.Contains("return nil", output);
    }

    [Fact]
    public void EmitThrowingMethodAccessor_EmitsErrorDescriptionInfra()
    {
        var protocolDecl = CreateSimpleProtocol("TestProtocol");
        var method = CreateMethod("tryGetValue", new NamedTypeSpec("Swift.Int32"));
        method.Throws = true;
        protocolDecl.Methods.Add(method);
        var output = EmitDispatch(protocolDecl);

        Assert.Contains("SBW_GetErrorDescription", output);
        Assert.Contains("SBW_ReleaseError", output);
    }

    #endregion

    #region ClassReturn / StructReturn Classification Tests

    [Fact]
    public void ClassifyMethodDispatch_ClassReturn_ReturnsClassReturn()
    {
        var db = CreateTypeDatabaseWithClassesAndStructs(
            classes: new[] { "TestModule.ResponseAPDU" },
            structs: Array.Empty<string>(),
            nonFrozenStructs: Array.Empty<string>());
        var emitter = new WitnessDispatchEmitter(db, NullLogger.Instance, "TestModule");
        var method = CreateMethod("transmit", new NamedTypeSpec("TestModule.ResponseAPDU"));
        var result = emitter.ClassifyMethodDispatch(method);
        Assert.Equal(MethodDispatchKind.ClassReturn, result);
    }

    [Fact]
    public void ClassifyMethodDispatch_NonFrozenStructReturn_ReturnsStructReturn()
    {
        var db = CreateTypeDatabaseWithClassesAndStructs(
            classes: Array.Empty<string>(),
            structs: Array.Empty<string>(),
            nonFrozenStructs: new[] { "TestModule.CardStatus" });
        var emitter = new WitnessDispatchEmitter(db, NullLogger.Instance, "TestModule");
        var method = CreateMethod("getStatus", new NamedTypeSpec("TestModule.CardStatus"));
        var result = emitter.ClassifyMethodDispatch(method);
        Assert.Equal(MethodDispatchKind.StructReturn, result);
    }

    [Fact]
    public void ClassifyMethodDispatch_FrozenStructWithRefFields_ReturnsStructReturn()
    {
        var db = CreateTypeDatabaseWithClassesAndStructs(
            classes: Array.Empty<string>(),
            structs: Array.Empty<string>(),
            nonFrozenStructs: Array.Empty<string>(),
            frozenRefFieldStructs: new[] { "TestModule.BufferedData" });
        var emitter = new WitnessDispatchEmitter(db, NullLogger.Instance, "TestModule");
        var method = CreateMethod("getData", new NamedTypeSpec("TestModule.BufferedData"));
        var result = emitter.ClassifyMethodDispatch(method);
        Assert.Equal(MethodDispatchKind.StructReturn, result);
    }

    [Fact]
    public void ClassifyMethodDispatch_FrozenValueStructReturn_NotDispatchable()
    {
        var db = CreateTypeDatabaseWithClassesAndStructs(
            classes: Array.Empty<string>(),
            structs: new[] { "TestModule.Point" },
            nonFrozenStructs: Array.Empty<string>());
        var emitter = new WitnessDispatchEmitter(db, NullLogger.Instance, "TestModule");
        var method = CreateMethod("getOrigin", new NamedTypeSpec("TestModule.Point"));
        var result = emitter.ClassifyMethodDispatch(method);
        Assert.Equal(MethodDispatchKind.NotDispatchable, result);
    }

    [Fact]
    public void ClassifyMethodDispatch_ThrowingClassReturn_ReturnsClassReturn()
    {
        var db = CreateTypeDatabaseWithClassesAndStructs(
            classes: new[] { "TestModule.ResponseAPDU" },
            structs: Array.Empty<string>(),
            nonFrozenStructs: Array.Empty<string>());
        var emitter = new WitnessDispatchEmitter(db, NullLogger.Instance, "TestModule");
        var method = CreateMethod("tryTransmit", new NamedTypeSpec("TestModule.ResponseAPDU"));
        method.Throws = true;
        var result = emitter.ClassifyMethodDispatch(method);
        Assert.Equal(MethodDispatchKind.ClassReturn, result);
    }

    [Fact]
    public void ClassifyMethodDispatch_ThrowingStructReturn_ReturnsStructReturn()
    {
        var db = CreateTypeDatabaseWithClassesAndStructs(
            classes: Array.Empty<string>(),
            structs: Array.Empty<string>(),
            nonFrozenStructs: new[] { "TestModule.CardStatus" });
        var emitter = new WitnessDispatchEmitter(db, NullLogger.Instance, "TestModule");
        var method = CreateMethod("tryGetStatus", new NamedTypeSpec("TestModule.CardStatus"));
        method.Throws = true;
        var result = emitter.ClassifyMethodDispatch(method);
        Assert.Equal(MethodDispatchKind.StructReturn, result);
    }

    [Fact]
    public void ClassifyMethodDispatch_GenericTypeReturn_NotDispatchable()
    {
        var db = CreateTypeDatabaseWithClassesAndStructs(
            classes: new[] { "TestModule.Container" },
            structs: Array.Empty<string>(),
            nonFrozenStructs: Array.Empty<string>());
        var emitter = new WitnessDispatchEmitter(db, NullLogger.Instance, "TestModule");
        var genericReturn = new NamedTypeSpec("TestModule.Container");
        genericReturn.GenericParameters.Add(new NamedTypeSpec("Swift.Int32"));
        var method = CreateMethod("getContainer", genericReturn);
        var result = emitter.ClassifyMethodDispatch(method);
        Assert.Equal(MethodDispatchKind.NotDispatchable, result);
    }

    [Fact]
    public void ClassifyMethodDispatch_ConcreteReturnNonBlittableParam_NotDispatchable()
    {
        var db = CreateTypeDatabaseWithClassesAndStructs(
            classes: new[] { "TestModule.ResponseAPDU" },
            structs: Array.Empty<string>(),
            nonFrozenStructs: Array.Empty<string>());
        var emitter = new WitnessDispatchEmitter(db, NullLogger.Instance, "TestModule");
        // param is a non-blittable type not in TypeDatabase
        var method = CreateMethodWithParams("process",
            new NamedTypeSpec("TestModule.ResponseAPDU"),
            new[] { ("input", (TypeSpec)new NamedTypeSpec("TestModule.SomeStruct")) });
        var result = emitter.ClassifyMethodDispatch(method);
        Assert.Equal(MethodDispatchKind.NotDispatchable, result);
    }

    [Fact]
    public void IsPropertyClassReturn_Class_ReturnsTrue()
    {
        var db = CreateTypeDatabaseWithClassesAndStructs(
            classes: new[] { "TestModule.ResponseAPDU" },
            structs: Array.Empty<string>(),
            nonFrozenStructs: Array.Empty<string>());
        var emitter = new WitnessDispatchEmitter(db, NullLogger.Instance, "TestModule");
        var property = CreateProperty("response", new NamedTypeSpec("TestModule.ResponseAPDU"));
        Assert.True(emitter.IsPropertyClassReturn(property));
        Assert.False(emitter.IsPropertyStructReturn(property));
    }

    [Fact]
    public void IsPropertyStructReturn_NonFrozenStruct_ReturnsTrue()
    {
        var db = CreateTypeDatabaseWithClassesAndStructs(
            classes: Array.Empty<string>(),
            structs: Array.Empty<string>(),
            nonFrozenStructs: new[] { "TestModule.CardStatus" });
        var emitter = new WitnessDispatchEmitter(db, NullLogger.Instance, "TestModule");
        var property = CreateProperty("status", new NamedTypeSpec("TestModule.CardStatus"));
        Assert.False(emitter.IsPropertyClassReturn(property));
        Assert.True(emitter.IsPropertyStructReturn(property));
    }

    #endregion

    #region ClassReturn / StructReturn Swift Emission Tests

    [Fact]
    public void EmitClassReturn_UsesUnmanagedPassRetained()
    {
        var db = CreateTypeDatabaseWithClassesAndStructs(
            classes: new[] { "TestModule.ResponseAPDU" },
            structs: Array.Empty<string>(),
            nonFrozenStructs: Array.Empty<string>());
        var ctx = new ModuleEmissionContext();
        var emitter = new WitnessDispatchEmitter(db, NullLogger.Instance, "TestModule", ctx);
        var protocolDecl = CreateProtocolWithMethod("CardChannel", "transmit",
            returnType: new NamedTypeSpec("TestModule.ResponseAPDU"));
        var output = EmitDispatchWithEmitter(emitter, protocolDecl, ctx);

        Assert.Contains("Unmanaged.passRetained(result as AnyObject).toOpaque()", output);
    }

    [Fact]
    public void EmitClassReturn_HasNoFreeFunction()
    {
        var db = CreateTypeDatabaseWithClassesAndStructs(
            classes: new[] { "TestModule.ResponseAPDU" },
            structs: Array.Empty<string>(),
            nonFrozenStructs: Array.Empty<string>());
        var ctx = new ModuleEmissionContext();
        var emitter = new WitnessDispatchEmitter(db, NullLogger.Instance, "TestModule", ctx);
        var protocolDecl = CreateProtocolWithMethod("CardChannel", "transmit",
            returnType: new NamedTypeSpec("TestModule.ResponseAPDU"));
        var output = EmitDispatchWithEmitter(emitter, protocolDecl, ctx);

        Assert.Contains("SBW_CardChannel_method_transmit_0", output);
        Assert.DoesNotContain("SBW_CardChannel_free_method_transmit_0", output);
    }

    [Fact]
    public void EmitStructReturn_UsesAssumingMemoryBoundInitialize()
    {
        var db = CreateTypeDatabaseWithClassesAndStructs(
            classes: Array.Empty<string>(),
            structs: Array.Empty<string>(),
            nonFrozenStructs: new[] { "TestModule.CardStatus" });
        var ctx = new ModuleEmissionContext();
        var emitter = new WitnessDispatchEmitter(db, NullLogger.Instance, "TestModule", ctx);
        var protocolDecl = CreateProtocolWithMethod("Card", "getStatus",
            returnType: new NamedTypeSpec("TestModule.CardStatus"));
        var output = EmitDispatchWithEmitter(emitter, protocolDecl, ctx);

        Assert.Contains("resultBuf.assumingMemoryBound(to: TestModule.CardStatus.self).initialize(to: result)", output);
    }

    [Fact]
    public void EmitStructReturn_HasNoFreeFunction()
    {
        var db = CreateTypeDatabaseWithClassesAndStructs(
            classes: Array.Empty<string>(),
            structs: Array.Empty<string>(),
            nonFrozenStructs: new[] { "TestModule.CardStatus" });
        var ctx = new ModuleEmissionContext();
        var emitter = new WitnessDispatchEmitter(db, NullLogger.Instance, "TestModule", ctx);
        var protocolDecl = CreateProtocolWithMethod("Card", "getStatus",
            returnType: new NamedTypeSpec("TestModule.CardStatus"));
        var output = EmitDispatchWithEmitter(emitter, protocolDecl, ctx);

        Assert.Contains("SBW_Card_method_getStatus_0", output);
        Assert.DoesNotContain("SBW_Card_free_method_getStatus_0", output);
    }

    [Fact]
    public void EmitStructReturn_AcceptsResultBufParam()
    {
        var db = CreateTypeDatabaseWithClassesAndStructs(
            classes: Array.Empty<string>(),
            structs: Array.Empty<string>(),
            nonFrozenStructs: new[] { "TestModule.CardStatus" });
        var ctx = new ModuleEmissionContext();
        var emitter = new WitnessDispatchEmitter(db, NullLogger.Instance, "TestModule", ctx);
        var protocolDecl = CreateProtocolWithMethod("Card", "getStatus",
            returnType: new NamedTypeSpec("TestModule.CardStatus"));
        var output = EmitDispatchWithEmitter(emitter, protocolDecl, ctx);

        Assert.Contains("_ resultBuf: UnsafeMutableRawPointer", output);
    }

    [Fact]
    public void EmitThrowingClassReturn_UsesDoCatchAndErrorOut()
    {
        var db = CreateTypeDatabaseWithClassesAndStructs(
            classes: new[] { "TestModule.ResponseAPDU" },
            structs: Array.Empty<string>(),
            nonFrozenStructs: Array.Empty<string>());
        var ctx = new ModuleEmissionContext();
        var emitter = new WitnessDispatchEmitter(db, NullLogger.Instance, "TestModule", ctx);
        var protocolDecl = CreateSimpleProtocol("CardChannel");
        var method = CreateMethod("tryTransmit", new NamedTypeSpec("TestModule.ResponseAPDU"));
        method.Throws = true;
        protocolDecl.Methods.Add(method);
        var output = EmitDispatchWithEmitter(emitter, protocolDecl, ctx);

        Assert.Contains("do {", output);
        Assert.Contains("} catch {", output);
        Assert.Contains("errorOut.pointee = UnsafeRawPointer(Unmanaged.passRetained(error as AnyObject).toOpaque())", output);
        Assert.Contains("return nil", output);
        Assert.Contains("-> UnsafeMutableRawPointer?", output);
    }

    [Fact]
    public void EmitThrowingStructReturn_UsesDoCatchVoidReturn()
    {
        var db = CreateTypeDatabaseWithClassesAndStructs(
            classes: Array.Empty<string>(),
            structs: Array.Empty<string>(),
            nonFrozenStructs: new[] { "TestModule.CardStatus" });
        var ctx = new ModuleEmissionContext();
        var emitter = new WitnessDispatchEmitter(db, NullLogger.Instance, "TestModule", ctx);
        var protocolDecl = CreateSimpleProtocol("Card");
        var method = CreateMethod("tryGetStatus", new NamedTypeSpec("TestModule.CardStatus"));
        method.Throws = true;
        protocolDecl.Methods.Add(method);
        var output = EmitDispatchWithEmitter(emitter, protocolDecl, ctx);

        Assert.Contains("do {", output);
        Assert.Contains("} catch {", output);
        Assert.Contains("errorOut.pointee = UnsafeRawPointer(Unmanaged.passRetained(error as AnyObject).toOpaque())", output);
        // Struct return is always void (result written to buffer)
        Assert.DoesNotContain("-> UnsafeMutableRawPointer", output);
    }

    [Fact]
    public void EmitClassReturnPropertyGetter_UsesPassRetained()
    {
        var db = CreateTypeDatabaseWithClassesAndStructs(
            classes: new[] { "TestModule.ResponseAPDU" },
            structs: Array.Empty<string>(),
            nonFrozenStructs: Array.Empty<string>());
        var ctx = new ModuleEmissionContext();
        var emitter = new WitnessDispatchEmitter(db, NullLogger.Instance, "TestModule", ctx);
        var protocolDecl = CreateProtocolWithProperty("Card", "response",
            new NamedTypeSpec("TestModule.ResponseAPDU"));
        var output = EmitDispatchWithEmitter(emitter, protocolDecl, ctx);

        Assert.Contains("Unmanaged.passRetained(result as AnyObject).toOpaque()", output);
        Assert.DoesNotContain("free", output.ToLowerInvariant());
    }

    [Fact]
    public void EmitStructReturnPropertyGetter_UsesResultBuf()
    {
        var db = CreateTypeDatabaseWithClassesAndStructs(
            classes: Array.Empty<string>(),
            structs: Array.Empty<string>(),
            nonFrozenStructs: new[] { "TestModule.CardStatus" });
        var ctx = new ModuleEmissionContext();
        var emitter = new WitnessDispatchEmitter(db, NullLogger.Instance, "TestModule", ctx);
        var protocolDecl = CreateProtocolWithProperty("Card", "status",
            new NamedTypeSpec("TestModule.CardStatus"));
        var output = EmitDispatchWithEmitter(emitter, protocolDecl, ctx);

        Assert.Contains("_ resultBuf: UnsafeMutableRawPointer", output);
        Assert.Contains("resultBuf.assumingMemoryBound(to: TestModule.CardStatus.self).initialize(to: result)", output);
    }

    #endregion

    #region Helper Methods

    private string EmitDispatch(ProtocolDecl protocolDecl)
    {
        // Fresh context per test call ensures clean dedup state (no parallelism)
        var ctx = new ModuleEmissionContext();
        var emitter = new WitnessDispatchEmitter(_typeDatabase, NullLogger.Instance, "TestModule", ctx);

        var stringWriter = new StringWriter();
        var writer = new SwiftWriter(stringWriter);
        emitter.EmitWitnessDispatchFunctions(writer, protocolDecl);
        return stringWriter.ToString();
    }

    private string EmitDispatchWithEmitter(WitnessDispatchEmitter emitter, ProtocolDecl protocolDecl, ModuleEmissionContext ctx)
    {
        var stringWriter = new StringWriter();
        var writer = new SwiftWriter(stringWriter);
        emitter.EmitWitnessDispatchFunctions(writer, protocolDecl);
        return stringWriter.ToString();
    }

    private WitnessDispatchEmitter CreateExistentialEmitter(out ModuleEmissionContext ctx)
    {
        var db = CreateTypeDatabaseWithProtocols("TestModule.Card", "TestModule.CardChannel");
        ctx = new ModuleEmissionContext();
        return new WitnessDispatchEmitter(db, NullLogger.Instance, "TestModule", ctx);
    }

    private static TypeDatabase CreateTypeDatabaseWithProtocols(params string[] protocolNames)
    {
        var typeDatabase = new TypeDatabase();
        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        foreach (var protocolName in protocolNames)
        {
            var parts = protocolName.Split('.');
            var shortName = parts[^1];
            testModule.RegisterType(
                SwiftTypeName.FromModuleQualifiedName(protocolName),
                new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", $"I{shortName}"),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName(protocolName),
                    MetadataAccessor = "$sMa",
                    Flags = TypeRecordFlags.None,
                    Kind = TypeRecordKind.Protocol
                });
        }
        typeDatabase.AddModuleDatabase(testModule);
        return typeDatabase;
    }

    private static TypeDatabase CreateTypeDatabaseWithClassesAndStructs(
        string[] classes, string[] structs, string[] nonFrozenStructs,
        string[] frozenRefFieldStructs = null)
    {
        var typeDatabase = new TypeDatabase();
        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");

        foreach (var className in classes)
        {
            var parts = className.Split('.');
            var shortName = parts[^1];
            testModule.RegisterType(
                SwiftTypeName.FromModuleQualifiedName(className),
                new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", shortName),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName(className),
                    MetadataAccessor = $"$sMa",
                    Flags = TypeRecordFlags.None,
                    Kind = TypeRecordKind.Class
                });
        }

        foreach (var structName in structs)
        {
            var parts = structName.Split('.');
            var shortName = parts[^1];
            testModule.RegisterType(
                SwiftTypeName.FromModuleQualifiedName(structName),
                new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", shortName),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName(structName),
                    MetadataAccessor = $"$sMa",
                    Flags = TypeRecordFlags.Frozen,
                    Kind = TypeRecordKind.Struct
                });
        }

        foreach (var structName in nonFrozenStructs)
        {
            var parts = structName.Split('.');
            var shortName = parts[^1];
            testModule.RegisterType(
                SwiftTypeName.FromModuleQualifiedName(structName),
                new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", shortName),
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName(structName),
                    MetadataAccessor = $"$sMa",
                    Flags = TypeRecordFlags.None,
                    Kind = TypeRecordKind.Struct
                });
        }

        if (frozenRefFieldStructs != null)
        {
            foreach (var structName in frozenRefFieldStructs)
            {
                var parts = structName.Split('.');
                var shortName = parts[^1];
                testModule.RegisterType(
                    SwiftTypeName.FromModuleQualifiedName(structName),
                    new TypeRecord
                    {
                        CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", shortName),
                        SwiftTypeName = SwiftTypeName.FromModuleQualifiedName(structName),
                        MetadataAccessor = $"$sMa",
                        Flags = TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement,
                        Kind = TypeRecordKind.Struct
                    });
            }
        }

        typeDatabase.AddModuleDatabase(testModule);
        return typeDatabase;
    }

    private static ProtocolListTypeSpec CreateExistentialReturnType(string protocolName)
    {
        return new ProtocolListTypeSpec(new[] { new NamedTypeSpec(protocolName) });
    }

    private static ProtocolDecl CreateSimpleProtocol(string name)
    {
        return new ProtocolDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}P",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = new List<NamedTypeSpec>(),
            HasSelfRequirement = false,
            IsClassBound = false,
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    private static PropertyDecl CreateProperty(string name, TypeSpec typeSpec)
    {
        return new PropertyDecl
        {
            Name = name,
            SwiftTypeSpec = typeSpec,
            IsStatic = false,
            HasStorage = false,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl { Method = CreateMethodDecl($"{name}_get") }
            },
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    private ProtocolDecl CreateProtocolWithProperty(string protocolName, string propertyName, TypeSpec typeSpec)
    {
        var protocol = CreateSimpleProtocol(protocolName);
        protocol.Properties.Add(CreateProperty(propertyName, typeSpec));
        return protocol;
    }

    private ProtocolDecl CreateProtocolWithMethod(string protocolName, string methodName, TypeSpec returnType)
    {
        var protocol = CreateSimpleProtocol(protocolName);
        protocol.Methods.Add(CreateMethod(methodName, returnType));
        return protocol;
    }

    private ProtocolDecl CreateProtocolWithVoidMethod(string protocolName, string methodName)
    {
        var protocol = CreateSimpleProtocol(protocolName);
        protocol.Methods.Add(CreateMethod(methodName, TupleTypeSpec.Empty));
        return protocol;
    }

    private ProtocolDecl CreateProtocolWithMethodAndParams(string protocolName, string methodName, TypeSpec returnType, (string name, TypeSpec type)[] paramTypes)
    {
        var protocol = CreateSimpleProtocol(protocolName);
        protocol.Methods.Add(CreateMethodWithParams(methodName, returnType, paramTypes));
        return protocol;
    }

    private ProtocolDecl CreateProtocolWithGetterAndSetter(string protocolName, string propertyName, TypeSpec typeSpec)
    {
        var protocol = CreateSimpleProtocol(protocolName);
        protocol.Properties.Add(new PropertyDecl
        {
            Name = propertyName,
            SwiftTypeSpec = typeSpec,
            IsStatic = false,
            HasStorage = false,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl { Method = CreateMethodDecl($"{propertyName}_get") },
                new SetAccessorDecl { Method = CreateMethodDecl($"{propertyName}_set") }
            },
            ParentDecl = null,
            ModuleDecl = null
        });
        return protocol;
    }

    private static MethodDecl CreateMethod(string name, TypeSpec returnType)
    {
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s{name}",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    Name = "",
                    SwiftTypeSpec = returnType,
                    PrivateName = "",
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
    }

    private static MethodDecl CreateMethodWithParams(string name, TypeSpec returnType, (string name, TypeSpec type)[] paramTypes)
    {
        var signature = new List<ArgumentDecl>
        {
            new ArgumentDecl
            {
                Name = "",
                SwiftTypeSpec = returnType,
                PrivateName = "",
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = null,
                ModuleDecl = null
            }
        };

        foreach (var (paramName, paramType) in paramTypes)
        {
            signature.Add(new ArgumentDecl
            {
                Name = paramName,
                SwiftTypeSpec = paramType,
                PrivateName = paramName == "_" ? "" : paramName,
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = null,
                ModuleDecl = null
            });
        }

        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s{name}",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = signature,
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
    }

    private static MethodDecl CreateMethodDecl(string name)
    {
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s{name}",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    Name = "",
                    SwiftTypeSpec = TupleTypeSpec.Empty,
                    PrivateName = "",
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
    }

    #endregion

    #region IsSwiftClassType / IsIndirectStructType Tests

    [Fact]
    public void IsSwiftClassType_ReturnsTrue_ForClassInTypeDatabase()
    {
        var db = CreateTypeDatabaseWithClassesAndStructs(
            classes: new[] { "TestModule.ResponseAPDU" },
            structs: Array.Empty<string>(),
            nonFrozenStructs: Array.Empty<string>());
        var emitter = new WitnessDispatchEmitter(db, NullLogger.Instance, "TestModule");
        Assert.True(emitter.IsSwiftClassType(new NamedTypeSpec("TestModule.ResponseAPDU")));
    }

    [Fact]
    public void IsSwiftClassType_ReturnsFalse_ForObjCModule()
    {
        var db = CreateTypeDatabaseWithClassesAndStructs(
            classes: Array.Empty<string>(),
            structs: Array.Empty<string>(),
            nonFrozenStructs: Array.Empty<string>());
        var emitter = new WitnessDispatchEmitter(db, NullLogger.Instance, "TestModule");
        Assert.False(emitter.IsSwiftClassType(new NamedTypeSpec("UIKit.UIView")));
    }

    [Fact]
    public void IsSwiftClassType_ReturnsFalse_ForGenericType()
    {
        var db = CreateTypeDatabaseWithClassesAndStructs(
            classes: new[] { "TestModule.Container" },
            structs: Array.Empty<string>(),
            nonFrozenStructs: Array.Empty<string>());
        var emitter = new WitnessDispatchEmitter(db, NullLogger.Instance, "TestModule");
        var genericType = new NamedTypeSpec("TestModule.Container", new[] { new NamedTypeSpec("Swift.Int32") });
        Assert.False(emitter.IsSwiftClassType(genericType));
    }

    [Fact]
    public void IsIndirectStructType_ReturnsTrue_ForNonFrozenStruct()
    {
        var db = CreateTypeDatabaseWithClassesAndStructs(
            classes: Array.Empty<string>(),
            structs: Array.Empty<string>(),
            nonFrozenStructs: new[] { "TestModule.CardStatus" });
        var emitter = new WitnessDispatchEmitter(db, NullLogger.Instance, "TestModule");
        Assert.True(emitter.IsIndirectStructType(new NamedTypeSpec("TestModule.CardStatus")));
    }

    [Fact]
    public void IsIndirectStructType_ReturnsTrue_ForFrozenRefFieldStruct()
    {
        var db = CreateTypeDatabaseWithClassesAndStructs(
            classes: Array.Empty<string>(),
            structs: Array.Empty<string>(),
            nonFrozenStructs: Array.Empty<string>(),
            frozenRefFieldStructs: new[] { "TestModule.BufferedData" });
        var emitter = new WitnessDispatchEmitter(db, NullLogger.Instance, "TestModule");
        Assert.True(emitter.IsIndirectStructType(new NamedTypeSpec("TestModule.BufferedData")));
    }

    [Fact]
    public void IsIndirectStructType_ReturnsFalse_ForFrozenValueStruct()
    {
        var db = CreateTypeDatabaseWithClassesAndStructs(
            classes: Array.Empty<string>(),
            structs: new[] { "TestModule.Point" },
            nonFrozenStructs: Array.Empty<string>());
        var emitter = new WitnessDispatchEmitter(db, NullLogger.Instance, "TestModule");
        Assert.False(emitter.IsIndirectStructType(new NamedTypeSpec("TestModule.Point")));
    }

    [Fact]
    public void IsSwiftClassType_ReturnsFalse_ForNativeRemappedClass()
    {
        // Native-remapped classes (e.g., Foundation.URL → NSUrl) don't have .Payload
        var typeDatabase = new TypeDatabase();
        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.NativeClass"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "NativeClass"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.NativeClass"),
                MetadataAccessor = "$sMa",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Class,
                NativeTypeName = CSharpTypeName.FromNamespaceAndName("Foundation", "NSUrl")
            });
        typeDatabase.AddModuleDatabase(testModule);
        var emitter = new WitnessDispatchEmitter(typeDatabase, NullLogger.Instance, "TestModule");
        Assert.False(emitter.IsSwiftClassType(new NamedTypeSpec("TestModule.NativeClass")));
    }

    [Fact]
    public void IsIndirectStructType_ReturnsFalse_ForNativeRemappedStruct()
    {
        // Native-remapped structs (e.g., Foundation.Data → NSData) use FromX/ToX, not .Payload
        var typeDatabase = new TypeDatabase();
        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.NativeStruct"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "NativeStruct"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.NativeStruct"),
                MetadataAccessor = "$sMa",
                Flags = TypeRecordFlags.None, // non-frozen
                Kind = TypeRecordKind.Struct,
                NativeTypeName = CSharpTypeName.FromNamespaceAndName("Foundation", "NSData")
            });
        typeDatabase.AddModuleDatabase(testModule);
        var emitter = new WitnessDispatchEmitter(typeDatabase, NullLogger.Instance, "TestModule");
        Assert.False(emitter.IsIndirectStructType(new NamedTypeSpec("TestModule.NativeStruct")));
    }

    #endregion

    #region Class/Struct Param Dispatch Tests

    [Fact]
    public void ClassifyMethodDispatch_VoidWithClassParam_ReturnsBlittableOrString()
    {
        var db = CreateTypeDatabaseWithClassesAndStructs(
            classes: new[] { "TestModule.MPIMap" },
            structs: Array.Empty<string>(),
            nonFrozenStructs: Array.Empty<string>());
        var emitter = new WitnessDispatchEmitter(db, NullLogger.Instance, "TestModule");
        var method = CreateMethodWithParams("onMapChanged", TupleTypeSpec.Empty,
            new[] { ("map", (TypeSpec)new NamedTypeSpec("TestModule.MPIMap")) });
        var result = emitter.ClassifyMethodDispatch(method);
        Assert.Equal(MethodDispatchKind.BlittableOrString, result);
    }

    [Fact]
    public void ClassifyMethodDispatch_VoidWithStructParam_ReturnsBlittableOrString()
    {
        var db = CreateTypeDatabaseWithClassesAndStructs(
            classes: Array.Empty<string>(),
            structs: Array.Empty<string>(),
            nonFrozenStructs: new[] { "TestModule.CardStatus" });
        var emitter = new WitnessDispatchEmitter(db, NullLogger.Instance, "TestModule");
        var method = CreateMethodWithParams("onStatus", TupleTypeSpec.Empty,
            new[] { ("status", (TypeSpec)new NamedTypeSpec("TestModule.CardStatus")) });
        var result = emitter.ClassifyMethodDispatch(method);
        Assert.Equal(MethodDispatchKind.BlittableOrString, result);
    }

    [Fact]
    public void ClassifyMethodDispatch_ClassReturnWithStructParam_ReturnsClassReturn()
    {
        var db = CreateTypeDatabaseWithClassesAndStructs(
            classes: new[] { "TestModule.ResponseAPDU" },
            structs: Array.Empty<string>(),
            nonFrozenStructs: new[] { "TestModule.CommandAPDU" });
        var emitter = new WitnessDispatchEmitter(db, NullLogger.Instance, "TestModule");
        var method = CreateMethodWithParams("transmit", new NamedTypeSpec("TestModule.ResponseAPDU"),
            new[] { ("command", (TypeSpec)new NamedTypeSpec("TestModule.CommandAPDU")) });
        var result = emitter.ClassifyMethodDispatch(method);
        Assert.Equal(MethodDispatchKind.ClassReturn, result);
    }

    [Fact]
    public void ClassifyMethodDispatch_MixedParamTypes_StringAndClassAndBlittable()
    {
        var db = CreateTypeDatabaseWithClassesAndStructs(
            classes: new[] { "TestModule.Config" },
            structs: Array.Empty<string>(),
            nonFrozenStructs: Array.Empty<string>());
        var emitter = new WitnessDispatchEmitter(db, NullLogger.Instance, "TestModule");
        var method = CreateMethodWithParams("configure", TupleTypeSpec.Empty,
            new[] {
                ("name", (TypeSpec)new NamedTypeSpec("Swift.String")),
                ("config", (TypeSpec)new NamedTypeSpec("TestModule.Config")),
                ("count", (TypeSpec)new NamedTypeSpec("Swift.Int32"))
            });
        var result = emitter.ClassifyMethodDispatch(method);
        Assert.Equal(MethodDispatchKind.BlittableOrString, result);
    }

    [Fact]
    public void EmitParameterUnmarshal_ClassParam_EmitsUnmanagedFromOpaque()
    {
        var db = CreateTypeDatabaseWithClassesAndStructs(
            classes: new[] { "TestModule.MPIMap" },
            structs: Array.Empty<string>(),
            nonFrozenStructs: Array.Empty<string>());
        var emitter = new WitnessDispatchEmitter(db, NullLogger.Instance, "TestModule");
        var ctx = new ModuleEmissionContext();
        var emitterWithCtx = new WitnessDispatchEmitter(db, NullLogger.Instance, "TestModule", ctx);

        var protocol = CreateProtocolWithMethodAndParams("MapDelegate", "onMapChanged", TupleTypeSpec.Empty,
            new[] { ("map", (TypeSpec)new NamedTypeSpec("TestModule.MPIMap")) });
        var output = EmitDispatchWithEmitter(emitterWithCtx, protocol, ctx);

        Assert.Contains("Unmanaged<TestModule.MPIMap>.fromOpaque(rawPtr0).takeUnretainedValue()", output);
    }

    [Fact]
    public void EmitParameterUnmarshal_StructParam_EmitsAssumingMemoryBound()
    {
        var db = CreateTypeDatabaseWithClassesAndStructs(
            classes: Array.Empty<string>(),
            structs: Array.Empty<string>(),
            nonFrozenStructs: new[] { "TestModule.CardStatus" });
        var ctx = new ModuleEmissionContext();
        var emitter = new WitnessDispatchEmitter(db, NullLogger.Instance, "TestModule", ctx);

        var protocol = CreateProtocolWithMethodAndParams("StatusDelegate", "onStatus", TupleTypeSpec.Empty,
            new[] { ("status", (TypeSpec)new NamedTypeSpec("TestModule.CardStatus")) });
        var output = EmitDispatchWithEmitter(emitter, protocol, ctx);

        Assert.Contains("assumingMemoryBound(to: TestModule.CardStatus.self).pointee", output);
    }

    [Fact]
    public void EmitAccessor_VoidMethodWithClassParam_EmitsCorrectSwift()
    {
        var db = CreateTypeDatabaseWithClassesAndStructs(
            classes: new[] { "TestModule.MPIMap" },
            structs: Array.Empty<string>(),
            nonFrozenStructs: Array.Empty<string>());
        var ctx = new ModuleEmissionContext();
        var emitter = new WitnessDispatchEmitter(db, NullLogger.Instance, "TestModule", ctx);

        var protocol = CreateProtocolWithMethodAndParams("MapDelegate", "onMapChanged", TupleTypeSpec.Empty,
            new[] { ("map", (TypeSpec)new NamedTypeSpec("TestModule.MPIMap")) });
        var output = EmitDispatchWithEmitter(emitter, protocol, ctx);

        // Verify @_silgen_name generated
        Assert.Contains("@_silgen_name(\"SBW_MapDelegate_method_onMapChanged_0\")", output);
        // Verify param unmarshal uses Unmanaged pattern
        Assert.Contains("rawPtr0", output);
        Assert.Contains("takeUnretainedValue()", output);
        // Verify labeled call
        Assert.Contains("existential.onMapChanged(map: arg0)", output);
    }

    [Fact]
    public void EmitAccessor_MixedParamTypes_StringAndClassAndBlittable()
    {
        var db = CreateTypeDatabaseWithClassesAndStructs(
            classes: new[] { "TestModule.Config" },
            structs: Array.Empty<string>(),
            nonFrozenStructs: Array.Empty<string>());
        var ctx = new ModuleEmissionContext();
        var emitter = new WitnessDispatchEmitter(db, NullLogger.Instance, "TestModule", ctx);

        var protocol = CreateProtocolWithMethodAndParams("Handler", "configure", TupleTypeSpec.Empty,
            new[] {
                ("name", (TypeSpec)new NamedTypeSpec("Swift.String")),
                ("config", (TypeSpec)new NamedTypeSpec("TestModule.Config")),
                ("count", (TypeSpec)new NamedTypeSpec("Swift.Int32"))
            });
        var output = EmitDispatchWithEmitter(emitter, protocol, ctx);

        // String param: Utf8Slice decode
        Assert.Contains("arg0Slice = arg0Ptr.load(as: SBW_Utf8Slice.self)", output);
        // Class param: Unmanaged pattern
        Assert.Contains("Unmanaged<TestModule.Config>.fromOpaque(rawPtr1).takeUnretainedValue()", output);
        // Blittable param: direct load
        Assert.Contains("arg2 = arg2Ptr.load(as: Int32.self)", output);
    }

    #endregion

    #region BoundGenericReturn Classification

    [Fact]
    public void ClassifyMethodDispatch_ArrayReturn_ReturnsBoundGenericReturn()
    {
        var db = CreateTypeDatabaseWithProtocols("TestModule.MyProtocol");
        var ctx = new ModuleEmissionContext();
        var emitter = new WitnessDispatchEmitter(db, NullLogger.Instance, "TestModule", ctx);

        var arrayType = new NamedTypeSpec("Swift.Array");
        arrayType.GenericParameters.Add(new NamedTypeSpec("Swift.String"));
        var protocol = CreateProtocolWithMethod("MyProtocol", "getItems", arrayType);
        var method = protocol.Methods[0];

        var kind = emitter.ClassifyMethodDispatch(method);
        Assert.Equal(MethodDispatchKind.BoundGenericReturn, kind);
    }

    [Fact]
    public void ClassifyMethodDispatch_DictionaryReturn_ReturnsBoundGenericReturn()
    {
        var db = CreateTypeDatabaseWithProtocols("TestModule.MyProtocol");
        var ctx = new ModuleEmissionContext();
        var emitter = new WitnessDispatchEmitter(db, NullLogger.Instance, "TestModule", ctx);

        var dictType = new NamedTypeSpec("Swift.Dictionary");
        dictType.GenericParameters.Add(new NamedTypeSpec("Swift.String"));
        dictType.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        var protocol = CreateProtocolWithMethod("MyProtocol", "getMap", dictType);
        var method = protocol.Methods[0];

        var kind = emitter.ClassifyMethodDispatch(method);
        Assert.Equal(MethodDispatchKind.BoundGenericReturn, kind);
    }

    [Fact]
    public void ClassifyMethodDispatch_SetReturn_ReturnsBoundGenericReturn()
    {
        var db = CreateTypeDatabaseWithProtocols("TestModule.MyProtocol");
        var ctx = new ModuleEmissionContext();
        var emitter = new WitnessDispatchEmitter(db, NullLogger.Instance, "TestModule", ctx);

        var setType = new NamedTypeSpec("Swift.Set");
        setType.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        var protocol = CreateProtocolWithMethod("MyProtocol", "getIds", setType);
        var method = protocol.Methods[0];

        var kind = emitter.ClassifyMethodDispatch(method);
        Assert.Equal(MethodDispatchKind.BoundGenericReturn, kind);
    }

    [Fact]
    public void ClassifyMethodDispatch_CollectionReturn_WithUnsupportedParams_ReturnsNotDispatchable()
    {
        var db = CreateTypeDatabaseWithProtocols("TestModule.MyProtocol");
        var ctx = new ModuleEmissionContext();
        var emitter = new WitnessDispatchEmitter(db, NullLogger.Instance, "TestModule", ctx);

        var arrayType = new NamedTypeSpec("Swift.Array");
        arrayType.GenericParameters.Add(new NamedTypeSpec("Swift.String"));
        // Closure param is not dispatchable
        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new List<TypeSpec> { new NamedTypeSpec("Swift.Int") }),
            TupleTypeSpec.Empty);
        var protocol = CreateProtocolWithMethodAndParams("MyProtocol", "getItems", arrayType,
            new[] { ("filter", (TypeSpec)closureType) });
        var method = protocol.Methods[0];

        var kind = emitter.ClassifyMethodDispatch(method);
        Assert.Equal(MethodDispatchKind.NotDispatchable, kind);
    }

    [Fact]
    public void IsPropertyCollectionReturn_ArrayProperty_ReturnsTrue()
    {
        var db = CreateTypeDatabaseWithProtocols("TestModule.MyProtocol");
        var ctx = new ModuleEmissionContext();
        var emitter = new WitnessDispatchEmitter(db, NullLogger.Instance, "TestModule", ctx);

        var arrayType = new NamedTypeSpec("Swift.Array");
        arrayType.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        var property = CreateProperty("items", arrayType);

        Assert.True(emitter.IsPropertyCollectionReturn(property));
    }

    [Fact]
    public void GetSwiftCollectionTypeString_Array_ReturnsSquareBrackets()
    {
        var db = CreateTypeDatabaseWithProtocols("TestModule.MyProtocol");
        var ctx = new ModuleEmissionContext();
        var emitter = new WitnessDispatchEmitter(db, NullLogger.Instance, "TestModule", ctx);

        var arrayType = new NamedTypeSpec("Swift.Array");
        arrayType.GenericParameters.Add(new NamedTypeSpec("Swift.String"));

        Assert.Equal("[String]", emitter.GetSwiftCollectionTypeString(arrayType));
    }

    [Fact]
    public void GetSwiftCollectionTypeString_Dictionary_ReturnsDictLiteral()
    {
        var db = CreateTypeDatabaseWithProtocols("TestModule.MyProtocol");
        var ctx = new ModuleEmissionContext();
        var emitter = new WitnessDispatchEmitter(db, NullLogger.Instance, "TestModule", ctx);

        var dictType = new NamedTypeSpec("Swift.Dictionary");
        dictType.GenericParameters.Add(new NamedTypeSpec("Swift.String"));
        dictType.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));

        Assert.Equal("[String: Int]", emitter.GetSwiftCollectionTypeString(dictType));
    }

    [Fact]
    public void GetSwiftCollectionTypeString_Set_ReturnsSetGeneric()
    {
        var db = CreateTypeDatabaseWithProtocols("TestModule.MyProtocol");
        var ctx = new ModuleEmissionContext();
        var emitter = new WitnessDispatchEmitter(db, NullLogger.Instance, "TestModule", ctx);

        var setType = new NamedTypeSpec("Swift.Set");
        setType.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));

        Assert.Equal("Set<Int>", emitter.GetSwiftCollectionTypeString(setType));
    }

    #endregion

    #region BoundGenericReturn Swift Emission

    [Fact]
    public void EmitWitnessDispatch_CollectionPropertyGetter_EmitsAllocatePattern()
    {
        var db = CreateTypeDatabaseWithProtocols("TestModule.MyProtocol");
        var ctx = new ModuleEmissionContext();
        var emitter = new WitnessDispatchEmitter(db, NullLogger.Instance, "TestModule", ctx);

        var arrayType = new NamedTypeSpec("Swift.Array");
        arrayType.GenericParameters.Add(new NamedTypeSpec("Swift.String"));
        var protocol = CreateProtocolWithProperty("MyProtocol", "items", arrayType);
        var output = EmitDispatchWithEmitter(emitter, protocol, ctx);

        Assert.Contains("@_silgen_name(\"SBW_MyProtocol_get_items_0\")", output);
        Assert.Contains("UnsafeMutablePointer<[String]>.allocate(capacity: 1)", output);
        Assert.Contains("ptr.initialize(to: result)", output);
        Assert.Contains("return UnsafeMutableRawPointer(ptr)", output);

        // Free function
        Assert.Contains("@_silgen_name(\"SBW_MyProtocol_free_get_items_0\")", output);
        Assert.Contains("assumingMemoryBound(to: [String].self).deinitialize(count: 1)", output);
        Assert.Contains("ptr.deallocate()", output);
    }

    [Fact]
    public void EmitWitnessDispatch_CollectionMethodReturn_EmitsAccessor()
    {
        var db = CreateTypeDatabaseWithProtocols("TestModule.MyProtocol");
        var ctx = new ModuleEmissionContext();
        var emitter = new WitnessDispatchEmitter(db, NullLogger.Instance, "TestModule", ctx);

        var dictType = new NamedTypeSpec("Swift.Dictionary");
        dictType.GenericParameters.Add(new NamedTypeSpec("Swift.String"));
        dictType.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        var protocol = CreateProtocolWithMethod("MyProtocol", "getMap", dictType);
        var output = EmitDispatchWithEmitter(emitter, protocol, ctx);

        Assert.Contains("@_silgen_name(\"SBW_MyProtocol_method_getMap_0\")", output);
        Assert.Contains("UnsafeMutablePointer<[String: Int]>.allocate(capacity: 1)", output);
        Assert.Contains("@_silgen_name(\"SBW_MyProtocol_free_method_getMap_0\")", output);
        Assert.Contains("assumingMemoryBound(to: [String: Int].self).deinitialize(count: 1)", output);
    }

    [Fact]
    public void EmitWitnessDispatch_ThrowingCollectionMethod_EmitsDoTryCatch()
    {
        var db = CreateTypeDatabaseWithProtocols("TestModule.MyProtocol");
        var ctx = new ModuleEmissionContext();
        var emitter = new WitnessDispatchEmitter(db, NullLogger.Instance, "TestModule", ctx);

        var setType = new NamedTypeSpec("Swift.Set");
        setType.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        var protocol = CreateProtocolWithMethod("MyProtocol", "fetchIds", setType);
        protocol.Methods[0].Throws = true;
        var output = EmitDispatchWithEmitter(emitter, protocol, ctx);

        Assert.Contains("-> UnsafeMutableRawPointer?", output);
        Assert.Contains("do {", output);
        Assert.Contains("try existential.fetchIds()", output);
        Assert.Contains("} catch {", output);
        Assert.Contains("errorOut.pointee = UnsafeRawPointer(Unmanaged.passRetained(error as AnyObject).toOpaque())", output);
        Assert.Contains("return nil", output);
    }

    #endregion

    #region Optional Existential Return

    [Fact]
    public void ClassifyMethodDispatch_OptionalExistentialReturn_ReturnsExistentialReturn()
    {
        var emitter = CreateExistentialEmitter(out var ctx);

        var optionalExistentialType = new NamedTypeSpec("Swift.Optional");
        optionalExistentialType.GenericParameters.Add(
            new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.Card") }));
        var protocol = CreateProtocolWithMethod("MyProtocol", "findCard", optionalExistentialType);
        var method = protocol.Methods[0];

        var kind = emitter.ClassifyMethodDispatch(method);
        Assert.Equal(MethodDispatchKind.ExistentialReturn, kind);
    }

    [Fact]
    public void ClassifyMethodDispatch_ThrowingOptionalExistential_ReturnsNotDispatchable()
    {
        var emitter = CreateExistentialEmitter(out var ctx);

        var optionalExistentialType = new NamedTypeSpec("Swift.Optional");
        optionalExistentialType.GenericParameters.Add(
            new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.Card") }));
        var protocol = CreateProtocolWithMethod("MyProtocol", "findCard", optionalExistentialType);
        protocol.Methods[0].Throws = true;
        var method = protocol.Methods[0];

        var kind = emitter.ClassifyMethodDispatch(method);
        Assert.Equal(MethodDispatchKind.NotDispatchable, kind);
    }

    [Fact]
    public void EmitWitnessDispatch_OptionalExistentialReturn_EmitsIfLetPattern()
    {
        var emitter = CreateExistentialEmitter(out var ctx);

        var optionalExistentialType = new NamedTypeSpec("Swift.Optional");
        optionalExistentialType.GenericParameters.Add(
            new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.Card") }));
        var protocol = CreateProtocolWithMethod("MyProtocol", "findCard", optionalExistentialType);
        var output = EmitDispatchWithEmitter(emitter, protocol, ctx);

        Assert.Contains("-> UnsafeMutableRawPointer?", output);
        Assert.Contains("(any TestModule.Card)?", output);
        Assert.Contains("if let unwrapped = result", output);
        Assert.Contains("return nil", output);
    }

    #endregion

    #region F6: ClassifyMethodDispatchWithReason Tests

    [Fact]
    public void ClassifyMethodDispatchWithReason_Async_ReturnsReasonString()
    {
        var protocol = CreateProtocolWithVoidMethod("MyProto", "doWork");
        protocol.Methods[0].IsAsync = true;

        var classification = _emitter.ClassifyMethodDispatchWithReason(protocol.Methods[0]);
        Assert.Equal(MethodDispatchKind.NotDispatchable, classification.Kind);
        Assert.Equal("async methods require Swift concurrency runtime", classification.Reason);
    }

    [Fact]
    public void ClassifyMethodDispatchWithReason_NonBlittableReturn_IncludesTypeName()
    {
        // A method returning an unregistered type is not dispatchable
        var returnType = new NamedTypeSpec("SomeModule.WeirdType");
        var protocol = CreateProtocolWithMethod("MyProto", "getData", returnType);

        var classification = _emitter.ClassifyMethodDispatchWithReason(protocol.Methods[0]);
        Assert.Equal(MethodDispatchKind.NotDispatchable, classification.Kind);
        Assert.NotNull(classification.Reason);
        Assert.Contains("return type", classification.Reason);
        Assert.Contains("is not dispatchable", classification.Reason);
    }

    [Fact]
    public void ClassifyMethodDispatchWithReason_NonBlittableParam_IncludesParamInfo()
    {
        // A method with a non-dispatchable parameter type
        var protocol = CreateProtocolWithMethodAndParams("MyProto", "process",
            TupleTypeSpec.Empty,
            new[] { ("input", (TypeSpec)new NamedTypeSpec("SomeModule.WeirdType")) });

        var classification = _emitter.ClassifyMethodDispatchWithReason(protocol.Methods[0]);
        Assert.Equal(MethodDispatchKind.NotDispatchable, classification.Kind);
        Assert.NotNull(classification.Reason);
        Assert.Contains("parameter", classification.Reason);
        Assert.Contains("non-dispatchable type", classification.Reason);
    }

    [Fact]
    public void ClassifyMethodDispatchWithReason_ThrowingOptionalExistential_ReturnsReason()
    {
        var emitter = CreateExistentialEmitter(out var ctx);

        var optionalExistentialType = new NamedTypeSpec("Swift.Optional");
        optionalExistentialType.GenericParameters.Add(
            new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.Card") }));
        var protocol = CreateProtocolWithMethod("MyProto", "findCard", optionalExistentialType);
        protocol.Methods[0].Throws = true;

        var classification = emitter.ClassifyMethodDispatchWithReason(protocol.Methods[0]);
        Assert.Equal(MethodDispatchKind.NotDispatchable, classification.Kind);
        Assert.Equal("throwing methods with optional existential return are not supported", classification.Reason);
    }

    [Fact]
    public void ClassifyMethodDispatchWithReason_Dispatchable_HasNullReason()
    {
        // A simple void method with no params should be dispatchable with null reason
        var protocol = CreateProtocolWithVoidMethod("MyProto", "doSimple");

        var classification = _emitter.ClassifyMethodDispatchWithReason(protocol.Methods[0]);
        Assert.NotEqual(MethodDispatchKind.NotDispatchable, classification.Kind);
        Assert.Null(classification.Reason);
    }

    #endregion

    #region Actor-Isolated Property Wrappers (Issue K)

    [Fact]
    public void EmitPropertyGetter_MainActorProtocol_EmitsMainActorAttribute()
    {
        // When a protocol is @MainActor, its property getters should have @MainActor
        var protocol = CreateSimpleProtocol("CameraModel");
        protocol.IsMainActorIsolated = true;
        protocol.Properties.Add(CreateProperty("isTorchEnabled", new NamedTypeSpec("Swift.Bool")));
        var output = EmitDispatch(protocol);

        Assert.Contains("@MainActor @_silgen_name(\"SBW_CameraModel_get_isTorchEnabled_0\")", output);
    }

    [Fact]
    public void EmitPropertySetter_MainActorProtocol_EmitsMainActorAttribute()
    {
        var protocol = CreateSimpleProtocol("CameraModel");
        protocol.IsMainActorIsolated = true;
        protocol.Properties.Add(new PropertyDecl
        {
            Name = "isTorchEnabled",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Bool"),
            IsStatic = false,
            HasStorage = false,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl { Method = CreateMethodDecl("isTorchEnabled_get") },
                new SetAccessorDecl { Method = CreateMethodDecl("isTorchEnabled_set") }
            },
            ParentDecl = null,
            ModuleDecl = null
        });
        var output = EmitDispatch(protocol);

        Assert.Contains("@MainActor @_silgen_name(\"SBW_CameraModel_set_isTorchEnabled_0\")", output);
    }

    [Fact]
    public void EmitPropertyGetter_ActorIsolatedProperty_EmitsMainActorAttribute()
    {
        // Individual property with IsActorIsolated (protocol not @MainActor)
        var protocol = CreateSimpleProtocol("SomeProtocol");
        var prop = CreateProperty("isolatedProp", new NamedTypeSpec("Swift.Int32"));
        prop.IsActorIsolated = true;
        protocol.Properties.Add(prop);
        var output = EmitDispatch(protocol);

        Assert.Contains("@MainActor @_silgen_name(\"SBW_SomeProtocol_get_isolatedProp_0\")", output);
    }

    [Fact]
    public void EmitPropertyGetter_NonActorIsolated_NoMainActorAttribute()
    {
        // Non-isolated protocol should NOT have @MainActor
        var protocol = CreateProtocolWithProperty("Regular", "value", new NamedTypeSpec("Swift.Int32"));
        var output = EmitDispatch(protocol);

        Assert.DoesNotContain("@MainActor", output);
    }

    [Fact]
    public void EmitPropertyGetter_MainActorProtocol_StringType_EmitsMainActorAttribute()
    {
        // String getter path should also get @MainActor
        var protocol = CreateSimpleProtocol("CameraModel");
        protocol.IsMainActorIsolated = true;
        protocol.Properties.Add(CreateProperty("name", new NamedTypeSpec("Swift.String")));
        var output = EmitDispatch(protocol);

        Assert.Contains("@MainActor @_silgen_name(\"SBW_CameraModel_get_name_0\")", output);
    }

    #endregion

    #region F4: Optional Existential Return

    [Theory]
    [InlineData(TypeRecordFlags.HasAssociatedTypes, "PAT")]
    [InlineData(TypeRecordFlags.HasSelfRequirement, "Self requirement")]
    [InlineData(TypeRecordFlags.InheritedRequirementsOnly, "InheritedRequirementsOnly")]
    public void ClassifyMethodDispatch_OptionalExistentialWithBlockingFlags_ReturnsNotDispatchable(
        TypeRecordFlags blockingFlag, string _)
    {
        // Protocols with blocking flags (PAT, Self requirement, InheritedRequirementsOnly)
        // should NOT be dispatchable even when wrapped in Optional<any Protocol>
        var db = new TypeDatabase();
        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.BlockedProto"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "IBlockedProto"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.BlockedProto"),
                MetadataAccessor = "$sMa",
                Flags = blockingFlag,
                Kind = TypeRecordKind.Protocol
            });
        db.AddModuleDatabase(testModule);
        var emitter = new WitnessDispatchEmitter(db, NullLogger.Instance, "TestModule");

        var optionalExistentialType = new NamedTypeSpec("Swift.Optional");
        optionalExistentialType.GenericParameters.Add(
            new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.BlockedProto") }));
        var protocol = CreateProtocolWithMethod("MyProtocol", "findItem", optionalExistentialType);
        var method = protocol.Methods[0];

        var kind = emitter.ClassifyMethodDispatch(method);
        Assert.Equal(MethodDispatchKind.NotDispatchable, kind);
    }

    #endregion

    #region Generic Type Parameter Guard Tests (Issue O)

    [Fact]
    public void IsPropertyGetterDispatchable_GenericTypeParam_ReturnsFalse()
    {
        // DateResult<τ_0_0> contains an unresolved generic type parameter
        var genericType = new NamedTypeSpec("TestModule.DateResult");
        genericType.GenericParameters.Add(new NamedTypeSpec("τ_0_0"));
        var property = CreateProperty("result", genericType);
        Assert.False(_emitter.IsPropertyGetterDispatchable(property));
    }

    [Fact]
    public void IsPropertySetterDispatchable_GenericTypeParam_ReturnsFalse()
    {
        // DateResult<τ_0_0> contains an unresolved generic type parameter
        var genericType = new NamedTypeSpec("TestModule.DateResult");
        genericType.GenericParameters.Add(new NamedTypeSpec("τ_0_0"));
        var property = CreateProperty("result", genericType);
        Assert.False(_emitter.IsPropertySetterDispatchable(property));
    }

    [Fact]
    public void IsPropertyGetterDispatchable_ConcreteType_StillWorks()
    {
        // Swift.Int is blittable and concrete — should still be dispatchable
        var property = CreateProperty("count", new NamedTypeSpec("Swift.Int"));
        Assert.True(_emitter.IsPropertyGetterDispatchable(property));
    }

    [Fact]
    public void IsPropertyGetterDispatchable_AssociatedTypeRef_ReturnsFalse()
    {
        // Self.Element or τ_0_0.Element — associated type references contain unresolved generic params
        var assocType = new AssociatedTypeReferenceSpec("τ_0_0", "Element");
        var property = CreateProperty("current", assocType);
        Assert.False(_emitter.IsPropertyGetterDispatchable(property));
    }

    [Fact]
    public void IsPropertyGetterDispatchable_SelfAssociatedType_ReturnsFalse()
    {
        // Self.Index — Self-based associated type reference
        var assocType = new AssociatedTypeReferenceSpec("Self", "Index");
        var property = CreateProperty("startIndex", assocType);
        Assert.False(_emitter.IsPropertyGetterDispatchable(property));
    }

    #endregion
}
