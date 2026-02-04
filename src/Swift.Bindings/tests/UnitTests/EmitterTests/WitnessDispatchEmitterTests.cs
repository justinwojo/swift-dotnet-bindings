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
    public void IsPropertyGetterDispatchable_String_ReturnsFalse()
    {
        var property = CreateProperty("name", new NamedTypeSpec("Swift.String"));
        Assert.False(_emitter.IsPropertyGetterDispatchable(property));
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
    public void IsMethodDispatchable_StringReturn_ReturnsFalse()
    {
        var method = CreateMethod("getName",
            returnType: new NamedTypeSpec("Swift.String"));
        Assert.False(_emitter.IsMethodDispatchable(method));
    }

    [Fact]
    public void IsMethodDispatchable_NonBlittableParam_ReturnsFalse()
    {
        var method = CreateMethodWithParams("process",
            returnType: TupleTypeSpec.Empty,
            paramTypes: new[] { ("text", new NamedTypeSpec("Swift.String") as TypeSpec) });
        Assert.False(_emitter.IsMethodDispatchable(method));
    }

    [Fact]
    public void IsMethodDispatchable_MixedParams_ReturnsFalse()
    {
        var method = CreateMethodWithParams("process",
            returnType: new NamedTypeSpec("Swift.Int32"),
            paramTypes: new[]
            {
                ("count", new NamedTypeSpec("Swift.Int32") as TypeSpec),
                ("name", new NamedTypeSpec("Swift.String") as TypeSpec)
            });
        Assert.False(_emitter.IsMethodDispatchable(method));
    }

    [Fact]
    public void IsMethodDispatchable_ThrowingMethod_ReturnsFalse()
    {
        var method = CreateMethod("getValue", returnType: new NamedTypeSpec("Swift.Int32"));
        method.Throws = true;
        Assert.False(_emitter.IsMethodDispatchable(method));
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
    public void EmitDispatch_NonBlittableProperty_NoOutput()
    {
        var protocolDecl = CreateProtocolWithProperty("HasName", "name", new NamedTypeSpec("Swift.String"));
        var output = EmitDispatch(protocolDecl);

        Assert.DoesNotContain("@_silgen_name", output);
    }

    [Fact]
    public void EmitDispatch_NonBlittableMethod_NoOutput()
    {
        var protocolDecl = CreateProtocolWithMethod("HasName", "getName",
            returnType: new NamedTypeSpec("Swift.String"));
        var output = EmitDispatch(protocolDecl);

        Assert.DoesNotContain("@_silgen_name", output);
    }

    [Fact]
    public void EmitDispatch_MixedMembers_OnlyBlittableEmitted()
    {
        var protocolDecl = CreateSimpleProtocol("MixedProtocol");

        // Blittable property
        protocolDecl.Properties.Add(CreateProperty("count", new NamedTypeSpec("Swift.Int32")));

        // Non-blittable property
        protocolDecl.Properties.Add(CreateProperty("name", new NamedTypeSpec("Swift.String")));

        var output = EmitDispatch(protocolDecl);

        Assert.Contains("SBW_MixedProtocol_get_count_0", output);
        Assert.DoesNotContain("SBW_MixedProtocol_get_name_0", output);
    }

    [Fact]
    public void EmitDispatch_SetterOnlyProperty_NoOutput()
    {
        // Properties with only setter (no getter) should not emit dispatch
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

        Assert.DoesNotContain("@_silgen_name", output);
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

    #region Helper Methods

    private string EmitDispatch(ProtocolDecl protocolDecl)
    {
        var stringWriter = new StringWriter();
        var writer = new SwiftWriter(stringWriter);
        _emitter.EmitWitnessDispatchFunctions(writer, protocolDecl);
        return stringWriter.ToString();
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
}
