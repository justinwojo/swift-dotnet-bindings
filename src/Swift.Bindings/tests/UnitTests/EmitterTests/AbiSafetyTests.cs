// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for WrapperValidation.RequiresCdeclForAbiSafety — determines whether a method/property
/// NEEDS @_cdecl wrapping for ABI safety on ARM64, orthogonal to ShouldEmitWrapper validation gates.
/// </summary>
public class AbiSafetyTests
{
    #region Method RequiresCdeclForAbiSafety Tests

    [Fact]
    public void RequiresCdeclForAbiSafety_ScalarOnlyMethod_ReturnsFalse()
    {
        // Method: void -> void (no params, empty tuple return) → CallConvSwift safe
        var (moduleDecl, typeDb) = CreateTestEnvironment();
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var method = CreateMethod("doSomething", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.False(WrapperValidation.RequiresCdeclForAbiSafety(env));
    }

    [Fact]
    public void RequiresCdeclForAbiSafety_IntParam_ReturnsFalse()
    {
        // Method with Swift.Int param → primitive → CallConvSwift safe
        var (moduleDecl, typeDb) = CreateTestEnvironment();
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var method = CreateMethodWithParam("setCount", new NamedTypeSpec("Swift.Int"), "count", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.False(WrapperValidation.RequiresCdeclForAbiSafety(env));
    }

    [Fact]
    public void RequiresCdeclForAbiSafety_BoolReturn_ReturnsFalse()
    {
        // Method returning Swift.Bool → primitive → CallConvSwift safe
        var (moduleDecl, typeDb) = CreateTestEnvironment();
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var method = CreateMethodWithReturn("isValid", new NamedTypeSpec("Swift.Bool"), parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.False(WrapperValidation.RequiresCdeclForAbiSafety(env));
    }

    [Fact]
    public void RequiresCdeclForAbiSafety_StringParam_ReturnsTrue()
    {
        // Swift.String param → 16 bytes (two registers) → Mono JIT can't handle multi-register CallConvSwift
        var (moduleDecl, typeDb) = CreateTestEnvironment();
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var method = CreateMethodWithParam("setName", new NamedTypeSpec("Swift.String"), "name", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(WrapperValidation.RequiresCdeclForAbiSafety(env));
    }

    [Fact]
    public void RequiresCdeclForAbiSafety_NonFrozenStructParam_ReturnsTrue()
    {
        // Non-frozen struct → SafeHandle → non-blittable → @_cdecl required
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithType(
            "TestModule.OpaqueStruct", TypeRecordFlags.None, TypeRecordKind.Struct);
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var method = CreateMethodWithParam("setConfig",
            new NamedTypeSpec("TestModule.OpaqueStruct"), "config", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(WrapperValidation.RequiresCdeclForAbiSafety(env));
    }

    [Fact]
    public void RequiresCdeclForAbiSafety_ComplexEnumParam_ReturnsTrue()
    {
        // Complex enum (not SimpleEnum) → SafeHandle → non-blittable → @_cdecl required
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithType(
            "TestModule.ComplexEnum", TypeRecordFlags.Frozen, TypeRecordKind.Enum);
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var method = CreateMethodWithParam("setDirection",
            new NamedTypeSpec("TestModule.ComplexEnum"), "direction", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(WrapperValidation.RequiresCdeclForAbiSafety(env));
    }

    [Fact]
    public void RequiresCdeclForAbiSafety_SimpleEnumParam_ReturnsFalse()
    {
        // Simple enum → Int-based → primitive → CallConvSwift safe
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithType(
            "TestModule.SimpleDirection", TypeRecordFlags.Frozen | TypeRecordFlags.SimpleEnum, TypeRecordKind.Enum);
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var method = CreateMethodWithParam("setDirection",
            new NamedTypeSpec("TestModule.SimpleDirection"), "direction", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.False(WrapperValidation.RequiresCdeclForAbiSafety(env));
    }

    [Fact]
    public void RequiresCdeclForAbiSafety_FrozenStructWithFloatFields_ReturnsTrue()
    {
        // Custom frozen struct with float fields → NativeAOT puts floats in GPR → @_cdecl required
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithTypeRecord(
            "TestModule.Point", new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Point"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Point"),
                MetadataAccessor = "$sMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.HasFloatFields,
                Kind = TypeRecordKind.Struct,
                InlineSize = 16
            });
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var method = CreateMethodWithParam("setPosition",
            new NamedTypeSpec("TestModule.Point"), "position", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(WrapperValidation.RequiresCdeclForAbiSafety(env));
    }

    [Fact]
    public void RequiresCdeclForAbiSafety_FrozenIntegerStructSmall_ReturnsFalse()
    {
        // Custom frozen integer struct ≤ 16 bytes → CallConvSwift safe
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithTypeRecord(
            "TestModule.SmallIntStruct", new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "SmallIntStruct"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.SmallIntStruct"),
                MetadataAccessor = "$sMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct,
                InlineSize = 16
            });
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var method = CreateMethodWithParam("setData",
            new NamedTypeSpec("TestModule.SmallIntStruct"), "data", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.False(WrapperValidation.RequiresCdeclForAbiSafety(env));
    }

    [Fact]
    public void RequiresCdeclForAbiSafety_FrozenIntegerStructLarge_ReturnsTrue()
    {
        // Custom frozen integer struct > 16 bytes → NativeAOT SIGSEGV → @_cdecl required
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithTypeRecord(
            "TestModule.LargeIntStruct", new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "LargeIntStruct"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.LargeIntStruct"),
                MetadataAccessor = "$sMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct,
                InlineSize = 24
            });
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var method = CreateMethodWithParam("setPayload",
            new NamedTypeSpec("TestModule.LargeIntStruct"), "payload", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(WrapperValidation.RequiresCdeclForAbiSafety(env));
    }

    [Fact]
    public void RequiresCdeclForAbiSafety_LargeSystemFrozenStruct_CBridging_ReturnsFalse()
    {
        // C-bridging module system frozen struct (CGRect = 32 bytes from CoreGraphics)
        // → pure C struct with well-defined register layout → CallConvSwift safe at any size.
        // Evidence matrix: CGRect (32B) passes on both Mono and NativeAOT.
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithTypeRecord(
            "CoreGraphics.CGRect", new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("CoreGraphics", "CGRect"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("CoreGraphics.CGRect"),
                MetadataAccessor = "$sMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct,
                InlineSize = 32
            }, moduleName: "CoreGraphics");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var method = CreateMethodWithParam("setFrame",
            new NamedTypeSpec("CoreGraphics.CGRect"), "frame", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.False(WrapperValidation.RequiresCdeclForAbiSafety(env));
    }

    [Fact]
    public void RequiresCdeclForAbiSafety_SmallSystemFrozenStruct_ReturnsFalse()
    {
        // System frozen struct ≤ 8 bytes (Swift.Int = 8 bytes) → single register → safe
        var (moduleDecl, typeDb) = CreateTestEnvironment();
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        // Swift.Int is already registered with InlineSize=8 in CreateTestEnvironment
        var method = CreateMethodWithParam("setValue",
            new NamedTypeSpec("Swift.Int"), "value", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.False(WrapperValidation.RequiresCdeclForAbiSafety(env));
    }

    [Fact]
    public void RequiresCdeclForAbiSafety_ValueTupleParam_ReturnsTrue()
    {
        // ValueTuple → StructLayout.Auto → @_cdecl required
        var (moduleDecl, typeDb) = CreateTestEnvironment();
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var tupleSpec = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Bool")
        });
        var method = CreateMethodWithParam("setPair", tupleSpec, "pair", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(WrapperValidation.RequiresCdeclForAbiSafety(env));
    }

    [Fact]
    public void RequiresCdeclForAbiSafety_GenericContainerParam_ReturnsTrue()
    {
        // Generic container (Swift.Array) → non-blittable in CallConvSwift → @_cdecl required
        var (moduleDecl, typeDb) = CreateTestEnvironment();
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var arraySpec = new NamedTypeSpec("Swift.Array", new NamedTypeSpec("Swift.Int"));
        var method = CreateMethodWithParam("setItems", arraySpec, "items", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(WrapperValidation.RequiresCdeclForAbiSafety(env));
    }

    [Fact]
    public void RequiresCdeclForAbiSafety_ClassParam_ReturnsTrue_CC001Fix()
    {
        // CC-001 fix: Swift class param → NonFrozenSafeHandle in PInvokeEmitter → non-blittable
        // PInvokeEmitter treats class types as non-frozen (they're not frozen), so class params
        // get SafeHandle in the P/Invoke signature. SafeHandle is non-blittable with CallConvSwift.
        // Must route through @_cdecl wrapper where CallConvCdecl handles SafeHandle correctly.
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithType(
            "TestModule.MyClass", TypeRecordFlags.RequiresMemoryManagement, TypeRecordKind.Class);
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("ParentType", moduleDecl);
        var method = CreateMethodWithParam("setChild",
            new NamedTypeSpec("TestModule.MyClass"), "child", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(WrapperValidation.RequiresCdeclForAbiSafety(env));
    }

    [Fact]
    public void RequiresCdeclForAbiSafety_ClosureParam_ReturnsTrue()
    {
        // Closure params require @_cdecl for the adapter mechanism (function pointer + context conversion)
        var (moduleDecl, typeDb) = CreateTestEnvironment();
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var method = CreateMethod("handle", parentDecl, moduleDecl);

        // Add an escaping closure param
        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new List<TypeSpec> { new NamedTypeSpec("Swift.Int") }),
            TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));
        method.CSSignature.Add(new ArgumentDecl
        {
            SwiftTypeSpec = closureType,
            Name = "callback",
            PrivateName = "callback",
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = moduleDecl
        });

        var env = new MethodEnvironment(method, typeDb);

        Assert.True(WrapperValidation.RequiresCdeclForAbiSafety(env));
    }

    [Fact]
    public void RequiresCdeclForAbiSafety_FloatStructReturn_ReturnsTrue()
    {
        // Custom frozen struct with float fields returned by value → Mono SIGSEGV
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithTypeRecord(
            "TestModule.Point", new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Point"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Point"),
                MetadataAccessor = "$sMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.HasFloatFields,
                Kind = TypeRecordKind.Struct,
                InlineSize = 16
            });
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var method = CreateMethodWithReturn("getPosition",
            new NamedTypeSpec("TestModule.Point"), parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(WrapperValidation.RequiresCdeclForAbiSafety(env));
    }

    [Fact]
    public void RequiresCdeclForAbiSafety_NonFloatFrozenStructReturn_ReturnsFalse()
    {
        // Frozen integer struct return → SwiftIndirectResult or by-value → safe
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithTypeRecord(
            "TestModule.IntPair", new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "IntPair"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.IntPair"),
                MetadataAccessor = "$sMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct,
                InlineSize = 16
            });
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var method = CreateMethodWithReturn("getPair",
            new NamedTypeSpec("TestModule.IntPair"), parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.False(WrapperValidation.RequiresCdeclForAbiSafety(env));
    }

    [Fact]
    public void RequiresCdeclForAbiSafety_ValueTupleReturn_ReturnsTrue()
    {
        // ValueTuple return → StructLayout.Auto → @_cdecl required
        var (moduleDecl, typeDb) = CreateTestEnvironment();
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var tupleSpec = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Bool")
        });
        var method = CreateMethodWithReturn("getPair", tupleSpec, parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(WrapperValidation.RequiresCdeclForAbiSafety(env));
    }

    [Fact]
    public void RequiresCdeclForAbiSafety_FrozenStructWithMemoryManagementReturn_ReturnsFalse()
    {
        // Frozen struct with RequiresMemoryManagement returned → uses IndirectResult → safe
        // (e.g., Swift.String is Frozen + RequiresMemoryManagement)
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithTypeRecord(
            "TestModule.RefStruct", new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "RefStruct"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.RefStruct"),
                MetadataAccessor = "$sMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement | TypeRecordFlags.HasFloatFields,
                Kind = TypeRecordKind.Struct,
                InlineSize = 24
            });
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var method = CreateMethodWithReturn("getRefStruct",
            new NamedTypeSpec("TestModule.RefStruct"), parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        // Even though it has float fields, RequiresMemoryManagement means it uses IndirectResult → safe
        Assert.False(WrapperValidation.RequiresCdeclForAbiSafety(env));
    }

    [Fact]
    public void RequiresCdeclForAbiSafety_NonFinalClassInstanceMethod_ReturnsTrue()
    {
        // Non-final class instance method → Tj dispatch thunk → @_cdecl required
        var (moduleDecl, typeDb) = CreateTestEnvironment();
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl, isFinal: false);
        var method = CreateMethod("doSomething", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(WrapperValidation.RequiresCdeclForAbiSafety(env));
    }

    [Fact]
    public void RequiresCdeclForAbiSafety_FinalClassInstanceMethod_ReturnsFalse()
    {
        // Final class instance method → direct symbol (no Tj) → CallConvSwift safe
        var (moduleDecl, typeDb) = CreateTestEnvironment();
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl, isFinal: true);
        var method = CreateMethod("doSomething", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.False(WrapperValidation.RequiresCdeclForAbiSafety(env));
    }

    [Fact]
    public void RequiresCdeclForAbiSafety_ClassStaticMethod_ReturnsTrue()
    {
        // Static method on class → Swift's @convention(method) passes hidden @thick Self.Type
        // metatype parameter. C# P/Invoke doesn't include it → @_cdecl required.
        var (moduleDecl, typeDb) = CreateTestEnvironment();
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl, isFinal: false);
        var method = CreateMethod("doSomething", parentDecl, moduleDecl);
        method.MethodType = MethodType.Static;
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(WrapperValidation.RequiresCdeclForAbiSafety(env));
    }

    [Fact]
    public void RequiresCdeclForAbiSafety_FinalClassStaticMethod_ReturnsTrue()
    {
        // Static method on final class → still needs hidden metatype → @_cdecl required.
        var (moduleDecl, typeDb) = CreateTestEnvironment();
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl, isFinal: true);
        var method = CreateMethod("doSomething", parentDecl, moduleDecl);
        method.MethodType = MethodType.Static;
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(WrapperValidation.RequiresCdeclForAbiSafety(env));
    }

    [Fact]
    public void RequiresCdeclForAbiSafety_FinalMethodOnNonFinalClass_ReturnsFalse()
    {
        // Final method on non-final class → direct symbol (no Tj) → CallConvSwift safe
        var (moduleDecl, typeDb) = CreateTestEnvironment();
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl, isFinal: false);
        var method = CreateMethod("doSomething", parentDecl, moduleDecl);
        method.IsFinal = true;
        var env = new MethodEnvironment(method, typeDb);

        Assert.False(WrapperValidation.RequiresCdeclForAbiSafety(env));
    }

    [Fact]
    public void RequiresCdeclForAbiSafety_GenericStructConstructor_ReturnsTrue()
    {
        // Generic struct constructor → needs @_cdecl for metatype dispatch
        var (moduleDecl, typeDb) = CreateTestEnvironment();
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateStructDecl("Wrapper", moduleDecl);
        parentDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new GenericArgumentDecl("T", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };
        var method = CreateMethod("init", parentDecl, moduleDecl);
        method.IsConstructor = true;
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(WrapperValidation.RequiresCdeclForAbiSafety(env));
    }

    [Fact]
    public void RequiresCdeclForAbiSafety_GenericClassConstructor_ReturnsTrue()
    {
        // Generic class constructor → needs @_cdecl for metatype dispatch
        var (moduleDecl, typeDb) = CreateTestEnvironment();
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("Container", moduleDecl, isFinal: true);
        parentDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new GenericArgumentDecl("T", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };
        var method = CreateMethod("init", parentDecl, moduleDecl);
        method.IsConstructor = true;
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(WrapperValidation.RequiresCdeclForAbiSafety(env));
    }

    [Fact]
    public void RequiresCdeclForAbiSafety_FrozenStructConstructor_ReturnsTrue()
    {
        // Frozen struct constructor → SwiftIndirectResult + Mono JIT crash → @_cdecl required
        var (moduleDecl, typeDb) = CreateTestEnvironment();
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateStructDecl("Point", moduleDecl); // CreateStructDecl defaults IsFrozen=true
        var method = CreateMethod("init", parentDecl, moduleDecl);
        method.IsConstructor = true;
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(WrapperValidation.RequiresCdeclForAbiSafety(env));
    }

    [Fact]
    public void RequiresCdeclForAbiSafety_NonFrozenStructConstructor_ReturnsTrue()
    {
        // Non-frozen struct constructor → SwiftIndirectResult + Mono JIT crash → @_cdecl required
        // Non-frozen structs also use SwiftIndirectResult (always passed indirectly),
        // causing the same Mono JIT crash as frozen struct constructors.
        // E.g., LottieColor(r:g:b:a:denominator:) with only primitive params.
        var (moduleDecl, typeDb) = CreateTestEnvironment();
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateNonFrozenStructDecl("OpaquePoint", moduleDecl);
        var method = CreateMethod("init", parentDecl, moduleDecl);
        method.IsConstructor = true;
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(WrapperValidation.RequiresCdeclForAbiSafety(env));
    }

    [Fact]
    public void RequiresCdeclForAbiSafety_NestedGenericStructConstructor_ReturnsTrue()
    {
        // Nested frozen struct inside generic parent → frozen struct constructor check fires.
        // ShouldEmitWrapper handles the "can't wrap" decision (inherited generic context);
        // RequiresCdeclForAbiSafety correctly reports the ABI IS unsafe without wrapping.
        var (moduleDecl, typeDb) = CreateTestEnvironment();
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        // Create outer generic class
        var outerDecl = CreateClassDecl("Interceptor", moduleDecl, isFinal: false);
        outerDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new GenericArgumentDecl("T", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };

        // Create nested struct that inherits generic context
        var nestedDecl = new StructDecl
        {
            Name = "RefreshWindow",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Interceptor.RefreshWindow"),
            MangledName = "$s10TestModule11InterceptorV13RefreshWindowVN",
            MetadataAccessor = "$s10TestModule11InterceptorV13RefreshWindowVMa",
            IsFrozen = true,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>
            {
                new GenericArgumentDecl("T", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
            },
            Conformances = new List<TypeConformance>(),
            ParentDecl = outerDecl, // Nested inside generic parent
            ModuleDecl = moduleDecl
        };
        outerDecl.Types.Add(nestedDecl);

        var method = CreateMethod("init", nestedDecl, moduleDecl);
        method.IsConstructor = true;
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(WrapperValidation.RequiresCdeclForAbiSafety(env));
    }

    [Fact]
    public void RequiresCdeclForAbiSafety_NestedOwnGenericStructConstructor_ReturnsTrue()
    {
        // Nested struct with its OWN generic param (not inherited from parent) → needs @_cdecl
        var (moduleDecl, typeDb) = CreateTestEnvironment();
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        // Create non-generic outer class
        var outerDecl = CreateClassDecl("Container", moduleDecl, isFinal: false);

        // Create nested generic struct with its own T
        var nestedDecl = new StructDecl
        {
            Name = "Inner",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Container.Inner"),
            MangledName = "$s10TestModule9ContainerV5InnerVN",
            MetadataAccessor = "$s10TestModule9ContainerV5InnerVMa",
            IsFrozen = false,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>
            {
                new GenericArgumentDecl("U", "U", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
            },
            Conformances = new List<TypeConformance>(),
            ParentDecl = outerDecl,
            ModuleDecl = moduleDecl
        };
        outerDecl.Types.Add(nestedDecl);

        var method = CreateMethod("init", nestedDecl, moduleDecl);
        method.IsConstructor = true;
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(WrapperValidation.RequiresCdeclForAbiSafety(env));
    }

    [Fact]
    public void RequiresCdeclForAbiSafety_NonGenericClassConstructor_ReturnsTrue()
    {
        // Non-generic class allocating constructor → hidden metatype parameter → @_cdecl required
        // Swift's allocating init passes @thick Self.Type as hidden param (same as static methods).
        // On Mono JIT, CallConvSwift without metatype handling crashes (Keychain(), MD5()).
        var (moduleDecl, typeDb) = CreateTestEnvironment();
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("Keychain", moduleDecl, isFinal: false);
        var method = CreateMethod("init", parentDecl, moduleDecl);
        method.IsConstructor = true;
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(WrapperValidation.RequiresCdeclForAbiSafety(env));
    }

    [Fact]
    public void RequiresCdeclForAbiSafety_FinalClassConstructor_ReturnsTrue()
    {
        // Final class constructor → still needs @_cdecl (allocating init has hidden metatype)
        var (moduleDecl, typeDb) = CreateTestEnvironment();
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("Counter", moduleDecl, isFinal: true);
        var method = CreateMethod("init", parentDecl, moduleDecl);
        method.IsConstructor = true;
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(WrapperValidation.RequiresCdeclForAbiSafety(env));
    }

    [Fact]
    public void RequiresCdeclForAbiSafety_ClassConstructorWithBoolParam_ReturnsTrue()
    {
        // Class constructor with bool param → hidden metatype + MarshalAs → @_cdecl required
        // Even though bool is IsCdeclPrimitive, the class constructor pattern itself needs wrapping.
        var (moduleDecl, typeDb) = CreateTestEnvironment();
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("BooleanDisposable", moduleDecl, isFinal: false);
        var method = CreateMethodWithParam("init", new NamedTypeSpec("Swift.Bool"), "isDisposed", parentDecl, moduleDecl);
        method.IsConstructor = true;
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(WrapperValidation.RequiresCdeclForAbiSafety(env));
    }

    [Fact]
    public void RequiresCdeclForAbiSafety_ObjCBridgedClassParam_ReturnsFalse()
    {
        // Method with ObjC bridged class parameter → IntPtr → safe for CallConvSwift
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithType(
            "TestModule.NSObject", TypeRecordFlags.ObjCBridged | TypeRecordFlags.RequiresMemoryManagement, TypeRecordKind.Class);
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var method = CreateMethodWithParam("setObject",
            new NamedTypeSpec("TestModule.NSObject"), "object", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.False(WrapperValidation.RequiresCdeclForAbiSafety(env));
    }

    [Fact]
    public void RequiresCdeclForAbiSafety_TwoClassParams_ReturnsTrue()
    {
        // Method with two class parameters (like ImageTask equality: 2× SafeHandle)
        // → at least one triggers @_cdecl
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithType(
            "TestModule.ImageTask", TypeRecordFlags.RequiresMemoryManagement, TypeRecordKind.Class);
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        // Create method with two class params
        var method = new MethodDecl
        {
            Name = "areEqual",
            MangledName = "$s10TestModule_areEqual",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl { SwiftTypeSpec = TupleTypeSpec.Empty, Name = "", PrivateName = "", IsInOut = false, IsGeneric = false, ParentDecl = null, ModuleDecl = moduleDecl },
                new ArgumentDecl { SwiftTypeSpec = new NamedTypeSpec("TestModule.ImageTask"), Name = "lhs", PrivateName = "lhs", IsInOut = false, IsGeneric = false, ParentDecl = null, ModuleDecl = moduleDecl },
                new ArgumentDecl { SwiftTypeSpec = new NamedTypeSpec("TestModule.ImageTask"), Name = "rhs", PrivateName = "rhs", IsInOut = false, IsGeneric = false, ParentDecl = null, ModuleDecl = moduleDecl }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(WrapperValidation.RequiresCdeclForAbiSafety(env));
    }

    [Fact]
    public void GetResolvablePwtParameterCount_GenericClassWithConstraint_ReturnsOne()
    {
        // Generic class ConstrainedBox<T: Describable> — one resolvable PWT
        // Verifies the PWT parameter count matches what PInvokeEmitter.HandleProtocolConformance emits.
        var (moduleDecl, typeDb) = CreateTestEnvironment();
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        // Register Describable protocol in TypeDatabase
        var describableName = SwiftTypeName.FromModuleQualifiedName("TestModule.Describable");
        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(describableName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "IDescribable"),
            SwiftTypeName = describableName,
            MetadataAccessor = "$sMa",
            Flags = TypeRecordFlags.None,
            Kind = TypeRecordKind.Protocol
        });
        typeDb.AddModuleDatabase(testModule);

        var parentDecl = CreateClassDecl("ConstrainedBox", moduleDecl, isFinal: false);
        parentDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new GenericArgumentDecl("T", "T",
                new List<GenericParameterConformance>
                {
                    new GenericParameterConformance(
                        new[] { "T" }, describableName, ConformanceKind.Protocol)
                },
                new List<GenericParameterConformance>())
        };

        int pwtCount = MetatypeHelperEmitter.GetResolvablePwtParameterCount(parentDecl, typeDb);
        Assert.Equal(1, pwtCount);
    }

    [Fact]
    public void GetResolvablePwtParameterCount_GenericClassNoConstraint_ReturnsZero()
    {
        // Generic class Container<T> with no protocol conformance — zero PWT
        var (moduleDecl, typeDb) = CreateTestEnvironment();
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("Container", moduleDecl, isFinal: true);
        parentDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new GenericArgumentDecl("T", "T",
                new List<GenericParameterConformance>(),
                new List<GenericParameterConformance>())
        };

        int pwtCount = MetatypeHelperEmitter.GetResolvablePwtParameterCount(parentDecl, typeDb);
        Assert.Equal(0, pwtCount);
    }

    [Fact]
    public void GetResolvablePwtParameterCount_MultipleConstraints_ReturnsCorrectCount()
    {
        // Generic class Box<T: Describable & TestIdentifiable> — two resolvable PWTs
        // Verifies ordering stays aligned with PInvokeEmitter.HandleProtocolConformance.
        var (moduleDecl, typeDb) = CreateTestEnvironment();
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var describableName = SwiftTypeName.FromModuleQualifiedName("TestModule.Describable");
        var identifiableName = SwiftTypeName.FromModuleQualifiedName("TestModule.TestIdentifiable");
        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(describableName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "IDescribable"),
            SwiftTypeName = describableName,
            MetadataAccessor = "$sMa",
            Flags = TypeRecordFlags.None,
            Kind = TypeRecordKind.Protocol
        });
        testModule.RegisterType(identifiableName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "ITestIdentifiable"),
            SwiftTypeName = identifiableName,
            MetadataAccessor = "$sMa",
            Flags = TypeRecordFlags.None,
            Kind = TypeRecordKind.Protocol
        });
        typeDb.AddModuleDatabase(testModule);

        var parentDecl = CreateClassDecl("Box", moduleDecl, isFinal: false);
        parentDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new GenericArgumentDecl("T", "T",
                new List<GenericParameterConformance>
                {
                    new GenericParameterConformance(
                        new[] { "T" }, describableName, ConformanceKind.Protocol),
                    new GenericParameterConformance(
                        new[] { "T" }, identifiableName, ConformanceKind.Protocol)
                },
                new List<GenericParameterConformance>())
        };

        int pwtCount = MetatypeHelperEmitter.GetResolvablePwtParameterCount(parentDecl, typeDb);
        Assert.Equal(2, pwtCount);
    }

    [Fact]
    public void GetResolvablePwtParameterCount_ProtocolWithAssociatedTypes_ReturnsZero()
    {
        // Generic class with constraint on protocol that has associated types — not resolvable
        var (moduleDecl, typeDb) = CreateTestEnvironment();
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var collectionName = SwiftTypeName.FromModuleQualifiedName("TestModule.Collection");
        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(collectionName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "ICollection"),
            SwiftTypeName = collectionName,
            MetadataAccessor = "$sMa",
            Flags = TypeRecordFlags.HasAssociatedTypes,
            Kind = TypeRecordKind.Protocol
        });
        typeDb.AddModuleDatabase(testModule);

        var parentDecl = CreateClassDecl("Collector", moduleDecl, isFinal: false);
        parentDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new GenericArgumentDecl("T", "T",
                new List<GenericParameterConformance>
                {
                    new GenericParameterConformance(
                        new[] { "T" }, collectionName, ConformanceKind.Protocol)
                },
                new List<GenericParameterConformance>())
        };

        int pwtCount = MetatypeHelperEmitter.GetResolvablePwtParameterCount(parentDecl, typeDb);
        Assert.Equal(0, pwtCount);
    }

    #endregion

    #region IsSelfTypeCdeclRequired — Frozen Struct Instance Member Tests

    [Fact]
    public void RequiresCdeclForAbiSafety_FrozenStructWithFloatFields_InstanceMethod_ReturnsTrue()
    {
        // Frozen struct with float fields as PARENT (self type) for instance method
        // → IsSelfTypeCdeclRequired detects float fields → @_cdecl required
        // Real-world: Lottie LottieColor (r/g/b/a: Double) crashes Mono JIT
        var (moduleDecl, typeDb) = CreateTestEnvironment();
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        // Register the float-field struct in TypeDatabase
        var structName = SwiftTypeName.FromModuleQualifiedName("TestModule.FloatPoint");
        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(structName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "FloatPoint"),
            SwiftTypeName = structName,
            MetadataAccessor = "$sMa",
            Flags = TypeRecordFlags.Frozen | TypeRecordFlags.HasFloatFields,
            Kind = TypeRecordKind.Struct,
            InlineSize = 16
        });
        typeDb.AddModuleDatabase(testModule);

        // Create struct as parent and an instance method on it
        var parentDecl = CreateStructDecl("FloatPoint", moduleDecl);
        parentDecl.SwiftTypeName = structName;
        var method = CreateMethod("brightness", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(WrapperValidation.RequiresCdeclForAbiSafety(env));
    }

    [Fact]
    public void RequiresCdeclForAbiSafety_FrozenStructWithBoolFields_InstanceMethod_ReturnsTrue()
    {
        // Frozen struct with Bool fields as PARENT (self type) for instance method
        // → IsSelfTypeCdeclRequired detects bool fields → @_cdecl required
        // Bool fields use i1 which Mono JIT can't pass via CallConvSwift registers
        var (moduleDecl, typeDb) = CreateTestEnvironment();
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var structName = SwiftTypeName.FromModuleQualifiedName("TestModule.BoolFlags");
        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(structName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "BoolFlags"),
            SwiftTypeName = structName,
            MetadataAccessor = "$sMa",
            Flags = TypeRecordFlags.Frozen | TypeRecordFlags.HasBoolFields,
            Kind = TypeRecordKind.Struct,
            InlineSize = 3
        });
        typeDb.AddModuleDatabase(testModule);

        var parentDecl = CreateStructDecl("BoolFlags", moduleDecl);
        parentDecl.SwiftTypeName = structName;
        var method = CreateMethod("activeCount", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(WrapperValidation.RequiresCdeclForAbiSafety(env));
    }

    [Fact]
    public void RequiresCdeclForAbiSafety_FrozenStructLargerThan8Bytes_InstanceMethod_ReturnsTrue()
    {
        // Frozen struct > 8 bytes as PARENT (self type) for instance method
        // → IsSelfTypeCdeclRequired detects InlineSize > 8 → @_cdecl required
        // Multi-register SwiftSelf<T> crashes Mono JIT
        var (moduleDecl, typeDb) = CreateTestEnvironment();
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var structName = SwiftTypeName.FromModuleQualifiedName("TestModule.LargeConfig");
        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(structName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "LargeConfig"),
            SwiftTypeName = structName,
            MetadataAccessor = "$sMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct,
            InlineSize = 24
        });
        typeDb.AddModuleDatabase(testModule);

        var parentDecl = CreateStructDecl("LargeConfig", moduleDecl);
        parentDecl.SwiftTypeName = structName;
        var method = CreateMethod("volume", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(WrapperValidation.RequiresCdeclForAbiSafety(env));
    }

    [Fact]
    public void RequiresCdeclForAbiSafety_FrozenStructSmall_NoSpecialFields_InstanceMethod_ReturnsFalse()
    {
        // Frozen struct ≤ 8 bytes with no float/bool fields → CallConvSwift safe
        var (moduleDecl, typeDb) = CreateTestEnvironment();
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var structName = SwiftTypeName.FromModuleQualifiedName("TestModule.SmallStruct");
        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(structName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "SmallStruct"),
            SwiftTypeName = structName,
            MetadataAccessor = "$sMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct,
            InlineSize = 4
        });
        typeDb.AddModuleDatabase(testModule);

        var parentDecl = CreateStructDecl("SmallStruct", moduleDecl);
        parentDecl.SwiftTypeName = structName;
        var method = CreateMethod("getValue", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.False(WrapperValidation.RequiresCdeclForAbiSafety(env));
    }

    [Fact]
    public void RequiresCdeclForAbiSafety_NonFrozenStruct_InstanceMethod_ReturnsTrue()
    {
        // Non-frozen struct instance method → IsNonFrozenStructInstanceMember → @_cdecl required
        // C# projects non-frozen as SafeHandle (IntPtr self) but Swift expects SwiftSelf<T>
        var (moduleDecl, typeDb) = CreateTestEnvironment();
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var structName = SwiftTypeName.FromModuleQualifiedName("TestModule.FlexConfig");
        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(structName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "FlexConfig"),
            SwiftTypeName = structName,
            MetadataAccessor = "$sMa",
            Flags = TypeRecordFlags.None,
            Kind = TypeRecordKind.Struct
        });
        typeDb.AddModuleDatabase(testModule);

        var parentDecl = CreateNonFrozenStructDecl("FlexConfig", moduleDecl);
        parentDecl.SwiftTypeName = structName;
        var method = CreateMethod("shouldRetry", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(WrapperValidation.RequiresCdeclForAbiSafety(env));
    }

    [Fact]
    public void RequiresCdeclForAbiSafety_NonFrozenStruct_StaticMethod_ReturnsFalse()
    {
        // Non-frozen struct STATIC method → no SwiftSelf issue → safe (for struct statics)
        var (moduleDecl, typeDb) = CreateTestEnvironment();
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var structName = SwiftTypeName.FromModuleQualifiedName("TestModule.FlexConfig");
        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(structName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "FlexConfig"),
            SwiftTypeName = structName,
            MetadataAccessor = "$sMa",
            Flags = TypeRecordFlags.None,
            Kind = TypeRecordKind.Struct
        });
        typeDb.AddModuleDatabase(testModule);

        var parentDecl = CreateNonFrozenStructDecl("FlexConfig", moduleDecl);
        parentDecl.SwiftTypeName = structName;
        var method = CreateMethod("create", parentDecl, moduleDecl);
        method.MethodType = MethodType.Static;
        var env = new MethodEnvironment(method, typeDb);

        Assert.False(WrapperValidation.RequiresCdeclForAbiSafety(env));
    }

    #endregion

    #region Property RequiresCdeclForAbiSafety Tests

    [Fact]
    public void RequiresCdeclForAbiSafety_Property_StaticIntType_ReturnsFalse()
    {
        // Static property of primitive type on class → safe (no SwiftSelf involved)
        var (moduleDecl, typeDb) = CreateTestEnvironment();
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var getterMethod = CreateMethod("count_getter", parentDecl, moduleDecl);
        getterMethod.IsAccessor = true;
        var property = new PropertyDecl
        {
            Name = "count",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            HasStorage = true,
            IsStatic = true,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl { Method = getterMethod }
            },
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl
        };
        var env = new MethodEnvironment(getterMethod, typeDb);

        Assert.False(WrapperValidation.RequiresCdeclForAbiSafety(env, property));
    }

    [Fact]
    public void RequiresCdeclForAbiSafety_Property_FinalClassInstanceInt_ReturnsTrue()
    {
        // Instance property on final class → Mono JIT can't handle CallConvSwift + SwiftSelf
        // → @_cdecl required regardless of property type (ImagePrefetcher.Priority pattern)
        var (moduleDecl, typeDb) = CreateTestEnvironment();
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl, isFinal: true);
        var getterMethod = CreateMethod("count_getter", parentDecl, moduleDecl);
        getterMethod.IsAccessor = true;
        var property = new PropertyDecl
        {
            Name = "count",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            HasStorage = true,
            IsStatic = false,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl { Method = getterMethod }
            },
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl
        };
        var env = new MethodEnvironment(getterMethod, typeDb);

        Assert.True(WrapperValidation.RequiresCdeclForAbiSafety(env, property));
    }

    [Fact]
    public void RequiresCdeclForAbiSafety_Property_FloatStructGetter_ReturnsTrue()
    {
        // Read-only property returning custom frozen struct with float fields → @_cdecl required
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithTypeRecord(
            "TestModule.Point", new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Point"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Point"),
                MetadataAccessor = "$sMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.HasFloatFields,
                Kind = TypeRecordKind.Struct,
                InlineSize = 16
            });
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var getterMethod = CreateMethod("position_getter", parentDecl, moduleDecl);
        getterMethod.IsAccessor = true;
        var property = new PropertyDecl
        {
            Name = "position",
            SwiftTypeSpec = new NamedTypeSpec("TestModule.Point"),
            HasStorage = true,
            IsStatic = false,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl { Method = getterMethod }
            },
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl
        };
        var env = new MethodEnvironment(getterMethod, typeDb);

        Assert.True(WrapperValidation.RequiresCdeclForAbiSafety(env, property));
    }

    [Fact]
    public void RequiresCdeclForAbiSafety_Property_NonFrozenStructWithSetter_ReturnsTrue()
    {
        // Read-write property of non-frozen struct → SafeHandle → @_cdecl required (setter param)
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithType(
            "TestModule.OpaqueConfig", TypeRecordFlags.None, TypeRecordKind.Struct);
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var getterMethod = CreateMethod("config_getter", parentDecl, moduleDecl);
        getterMethod.IsAccessor = true;
        var setterMethod = CreateMethod("config_setter", parentDecl, moduleDecl);
        setterMethod.IsAccessor = true;
        var property = new PropertyDecl
        {
            Name = "config",
            SwiftTypeSpec = new NamedTypeSpec("TestModule.OpaqueConfig"),
            HasStorage = true,
            IsStatic = false,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl { Method = getterMethod },
                new SetAccessorDecl { Method = setterMethod }
            },
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl
        };
        var env = new MethodEnvironment(getterMethod, typeDb);

        Assert.True(WrapperValidation.RequiresCdeclForAbiSafety(env, property));
    }

    [Fact]
    public void RequiresCdeclForAbiSafety_Property_StaticSmallFrozenIntStruct_ReturnsFalse()
    {
        // Static property returning small frozen integer struct on class → safe
        // (static properties don't use SwiftSelf, and small int structs are safe)
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithTypeRecord(
            "TestModule.SmallStruct", new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "SmallStruct"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.SmallStruct"),
                MetadataAccessor = "$sMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct,
                InlineSize = 8
            });
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var getterMethod = CreateMethod("data_getter", parentDecl, moduleDecl);
        getterMethod.IsAccessor = true;
        var property = new PropertyDecl
        {
            Name = "data",
            SwiftTypeSpec = new NamedTypeSpec("TestModule.SmallStruct"),
            HasStorage = true,
            IsStatic = true,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl { Method = getterMethod }
            },
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl
        };
        var env = new MethodEnvironment(getterMethod, typeDb);

        Assert.False(WrapperValidation.RequiresCdeclForAbiSafety(env, property));
    }

    [Fact]
    public void RequiresCdeclForAbiSafety_Property_StringType_ReturnsTrue()
    {
        // String property (16 bytes) → multi-register → @_cdecl required
        // Regression test: Mono JIT SIGSEGV in Animal.name getter with CallConvSwift
        var (moduleDecl, typeDb) = CreateTestEnvironment();
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("Animal", moduleDecl);
        var getterMethod = CreateMethod("name_getter", parentDecl, moduleDecl);
        getterMethod.IsAccessor = true;
        var setterMethod = CreateMethod("name_setter", parentDecl, moduleDecl);
        setterMethod.IsAccessor = true;
        var property = new PropertyDecl
        {
            Name = "name",
            SwiftTypeSpec = new NamedTypeSpec("Swift.String"),
            HasStorage = true,
            IsStatic = false,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl { Method = getterMethod },
                new SetAccessorDecl { Method = setterMethod }
            },
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl
        };
        var env = new MethodEnvironment(getterMethod, typeDb);

        Assert.True(WrapperValidation.RequiresCdeclForAbiSafety(env, property));
    }

    [Fact]
    public void RequiresCdeclForAbiSafety_Property_OnFloatFieldStruct_ReturnsTrue()
    {
        // Property on a frozen struct with float fields → SwiftSelf<T> passes struct by value
        // → GPR/FPR mismatch when struct has float fields → @_cdecl required
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithTypeRecord(
            "TestModule.SafeDiv",
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "SafeDiv"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.SafeDiv"),
                MetadataAccessor = "$s10TestModule7SafeDivVMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.HasFloatFields,
                Kind = TypeRecordKind.Struct,
                InlineSize = 24 // int + int + double
            });
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateStructDecl("SafeDiv", moduleDecl);
        var getterMethod = CreateMethod("get_numerator", parentDecl, moduleDecl);
        var property = new PropertyDecl
        {
            Name = "numerator",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            HasStorage = true,
            IsStatic = false,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl { Method = getterMethod }
            },
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl
        };
        var env = new MethodEnvironment(getterMethod, typeDb);

        Assert.True(WrapperValidation.RequiresCdeclForAbiSafety(env, property));
    }

    [Fact]
    public void RequiresCdeclForAbiSafety_Method_OnFloatFieldStruct_ReturnsTrue()
    {
        // Instance method on frozen struct with float fields → SwiftSelf<T> by-value self
        // has float field → GPR/FPR mismatch → @_cdecl required
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithTypeRecord(
            "TestModule.SafeDiv",
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "SafeDiv"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.SafeDiv"),
                MetadataAccessor = "$s10TestModule7SafeDivVMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.HasFloatFields,
                Kind = TypeRecordKind.Struct,
                InlineSize = 24
            });
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateStructDecl("SafeDiv", moduleDecl);
        var method = CreateMethod("getResult", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(WrapperValidation.RequiresCdeclForAbiSafety(env));
    }

    [Fact]
    public void RequiresCdeclForAbiSafety_Method_OnSmallIntegerStruct_ReturnsFalse()
    {
        // Instance method on small integer-only frozen struct ≤ 8 bytes → single register → safe
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithTypeRecord(
            "TestModule.Wrapper",
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Wrapper"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Wrapper"),
                MetadataAccessor = "$s10TestModule7WrapperVMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct,
                InlineSize = 8 // single Int64
            });
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateStructDecl("Wrapper", moduleDecl);
        var method = CreateMethod("getValue", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.False(WrapperValidation.RequiresCdeclForAbiSafety(env));
    }

    [Fact]
    public void RequiresCdeclForAbiSafety_Method_OnMultiRegisterIntegerStruct_ReturnsTrue()
    {
        // Instance method on frozen struct > 8 bytes (multi-register) → @_cdecl required
        // Mono JIT can't handle multi-register SwiftSelf<T> for custom structs
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithTypeRecord(
            "TestModule.RangedInt",
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "RangedInt"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.RangedInt"),
                MetadataAccessor = "$s10TestModule9RangedIntVMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct,
                InlineSize = 12 // three Int32s
            });
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateStructDecl("RangedInt", moduleDecl);
        var method = CreateMethod("getValue", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(WrapperValidation.RequiresCdeclForAbiSafety(env));
    }

    [Fact]
    public void RequiresCdeclForAbiSafety_Method_OnNonFrozenStructParent_ReturnsTrue()
    {
        // Instance method on non-frozen struct → C# projects as ClassWithOpaquePayload (IntPtr self)
        // but Swift ABI expects SwiftSelf<T> (struct by value in registers) → ABI mismatch → @_cdecl required
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithType(
            "TestModule.IntContainer", TypeRecordFlags.None, TypeRecordKind.Struct);
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateNonFrozenStructDecl("IntContainer", moduleDecl);
        var method = CreateMethod("element", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(WrapperValidation.RequiresCdeclForAbiSafety(env));
    }

    [Fact]
    public void RequiresCdeclForAbiSafety_Method_OnNonFrozenStructParent_StaticMethod_ReturnsFalse()
    {
        // Static method on non-frozen struct → no self parameter → no ABI mismatch
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithType(
            "TestModule.IntContainer", TypeRecordFlags.None, TypeRecordKind.Struct);
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateNonFrozenStructDecl("IntContainer", moduleDecl);
        var method = CreateMethod("create", parentDecl, moduleDecl);
        method.MethodType = MethodType.Static;
        var env = new MethodEnvironment(method, typeDb);

        Assert.False(WrapperValidation.RequiresCdeclForAbiSafety(env));
    }

    [Fact]
    public void RequiresCdeclForAbiSafety_Method_OnClassParent_ReturnsFalse()
    {
        // Instance method on final class → IntPtr self (not by-value) → self type is always safe
        var (moduleDecl, typeDb) = CreateTestEnvironment();
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("Animal", moduleDecl);
        var method = CreateMethod("getName", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.False(WrapperValidation.RequiresCdeclForAbiSafety(env));
    }

    [Fact]
    public void RequiresCdeclForAbiSafety_Property_OnNonFinalClass_ReturnsTrue()
    {
        // Property on non-final class → Tj dispatch thunk for accessor → @_cdecl required
        var (moduleDecl, typeDb) = CreateTestEnvironment();
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("Animal", moduleDecl, isFinal: false);
        var getterMethod = CreateMethod("name_getter", parentDecl, moduleDecl);
        getterMethod.IsAccessor = true;
        var property = new PropertyDecl
        {
            Name = "name",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            HasStorage = true,
            IsStatic = false,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl { Method = getterMethod }
            },
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl
        };
        var env = new MethodEnvironment(getterMethod, typeDb);

        Assert.True(WrapperValidation.RequiresCdeclForAbiSafety(env, property));
    }

    [Fact]
    public void RequiresCdeclForAbiSafety_Property_OnFinalClass_ReturnsTrue()
    {
        // Instance property on final class → Mono JIT can't handle CallConvSwift + SwiftSelf
        // → @_cdecl required (ImagePrefetcher.Priority, ImageTask.Priority pattern)
        var (moduleDecl, typeDb) = CreateTestEnvironment();
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("Animal", moduleDecl, isFinal: true);
        var getterMethod = CreateMethod("name_getter", parentDecl, moduleDecl);
        getterMethod.IsAccessor = true;
        var property = new PropertyDecl
        {
            Name = "name",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            HasStorage = true,
            IsStatic = false,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl { Method = getterMethod }
            },
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl
        };
        var env = new MethodEnvironment(getterMethod, typeDb);

        Assert.True(WrapperValidation.RequiresCdeclForAbiSafety(env, property));
    }

    [Fact]
    public void RequiresCdeclForAbiSafety_Property_StaticOnFinalClass_ReturnsFalse()
    {
        // Static property on final class → no SwiftSelf → safe for CallConvSwift
        var (moduleDecl, typeDb) = CreateTestEnvironment();
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("Animal", moduleDecl, isFinal: true);
        var getterMethod = CreateMethod("count_getter", parentDecl, moduleDecl);
        getterMethod.IsAccessor = true;
        var property = new PropertyDecl
        {
            Name = "count",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            HasStorage = true,
            IsStatic = true,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl { Method = getterMethod }
            },
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl
        };
        var env = new MethodEnvironment(getterMethod, typeDb);

        Assert.False(WrapperValidation.RequiresCdeclForAbiSafety(env, property));
    }

    [Fact]
    public void RequiresCdeclForAbiSafety_Property_OnNonFrozenStruct_ReturnsTrue()
    {
        // Instance property on non-frozen struct → C# has IntPtr self (ClassWithOpaquePayload)
        // but Swift expects SwiftSelf<T> (struct by value) → ABI mismatch → @_cdecl required
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithType(
            "TestModule.IntContainer", TypeRecordFlags.None, TypeRecordKind.Struct);
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateNonFrozenStructDecl("IntContainer", moduleDecl);
        var getterMethod = CreateMethod("count_getter", parentDecl, moduleDecl);
        getterMethod.IsAccessor = true;
        var property = new PropertyDecl
        {
            Name = "count",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int32"),
            HasStorage = false,
            IsStatic = false,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl { Method = getterMethod }
            },
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl
        };
        var env = new MethodEnvironment(getterMethod, typeDb);

        Assert.True(WrapperValidation.RequiresCdeclForAbiSafety(env, property));
    }

    [Fact]
    public void RequiresCdeclForAbiSafety_Property_OnNonFrozenStruct_Static_ReturnsFalse()
    {
        // Static property on non-frozen struct → no self parameter → no ABI mismatch
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithType(
            "TestModule.IntContainer", TypeRecordFlags.None, TypeRecordKind.Struct);
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateNonFrozenStructDecl("IntContainer", moduleDecl);
        var getterMethod = CreateMethod("defaultValue_getter", parentDecl, moduleDecl);
        getterMethod.IsAccessor = true;
        getterMethod.MethodType = MethodType.Static;
        var property = new PropertyDecl
        {
            Name = "defaultValue",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int32"),
            HasStorage = false,
            IsStatic = true,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl { Method = getterMethod }
            },
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl
        };
        var env = new MethodEnvironment(getterMethod, typeDb);

        Assert.False(WrapperValidation.RequiresCdeclForAbiSafety(env, property));
    }

    [Fact]
    public void RequiresCdeclForAbiSafety_Property_ClassTypeSetter_ReturnsTrue()
    {
        // Property setter where value type is a Swift class → SafeHandle → @_cdecl required
        // Reproduces Nuke ImagePipeline.shared setter CC-001 violation
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithType(
            "TestModule.ImagePipeline", TypeRecordFlags.RequiresMemoryManagement, TypeRecordKind.Class);
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("Container", moduleDecl);
        var getterMethod = CreateMethod("shared_getter", parentDecl, moduleDecl);
        getterMethod.IsAccessor = true;
        var setterMethod = CreateMethod("shared_setter", parentDecl, moduleDecl);
        setterMethod.IsAccessor = true;
        var property = new PropertyDecl
        {
            Name = "shared",
            SwiftTypeSpec = new NamedTypeSpec("TestModule.ImagePipeline"),
            HasStorage = true,
            IsStatic = true,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl { Method = getterMethod },
                new SetAccessorDecl { Method = setterMethod }
            },
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl
        };
        var env = new MethodEnvironment(getterMethod, typeDb);

        Assert.True(WrapperValidation.RequiresCdeclForAbiSafety(env, property));
    }

    #endregion

    #region IsParamTypeCdeclRequired Tests

    [Fact]
    public void IsParamTypeCdeclRequired_Primitive_ReturnsFalse()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment();
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";
        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var method = CreateMethod("test", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.False(WrapperValidation.IsParamTypeCdeclRequired(new NamedTypeSpec("Swift.Int"), env));
        Assert.False(WrapperValidation.IsParamTypeCdeclRequired(new NamedTypeSpec("Swift.Float"), env));
        Assert.False(WrapperValidation.IsParamTypeCdeclRequired(new NamedTypeSpec("Swift.Double"), env));
        Assert.False(WrapperValidation.IsParamTypeCdeclRequired(new NamedTypeSpec("Swift.Bool"), env));
    }

    [Fact]
    public void IsParamTypeCdeclRequired_StringParam_ReturnsTrue()
    {
        // String = 16 bytes → multi-register → @_cdecl required
        var (moduleDecl, typeDb) = CreateTestEnvironment();
        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var method = CreateMethod("test", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(WrapperValidation.IsParamTypeCdeclRequired(new NamedTypeSpec("Swift.String"), env));
    }

    [Fact]
    public void IsParamTypeCdeclRequired_OptionalContainer_ReturnsTrue()
    {
        // Optional is a generic container → @_cdecl required
        var (moduleDecl, typeDb) = CreateTestEnvironment();
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";
        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var method = CreateMethod("test", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        var optionalSpec = new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("Swift.Int"));
        Assert.True(WrapperValidation.IsParamTypeCdeclRequired(optionalSpec, env));
    }

    [Fact]
    public void IsParamTypeCdeclRequired_DictionaryContainer_ReturnsTrue()
    {
        // Dictionary is a generic container → @_cdecl required
        var (moduleDecl, typeDb) = CreateTestEnvironment();
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";
        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var method = CreateMethod("test", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        var dictSpec = new NamedTypeSpec("Swift.Dictionary", new NamedTypeSpec("Swift.String"), new NamedTypeSpec("Swift.Int"));
        Assert.True(WrapperValidation.IsParamTypeCdeclRequired(dictSpec, env));
    }

    [Fact]
    public void IsParamTypeCdeclRequired_UnknownType_ReturnsFalse()
    {
        // Unknown type not in TypeDatabase → let existing gates handle
        var (moduleDecl, typeDb) = CreateTestEnvironment();
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";
        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var method = CreateMethod("test", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.False(WrapperValidation.IsParamTypeCdeclRequired(new NamedTypeSpec("Unknown.Type"), env));
    }

    [Fact]
    public void IsParamTypeCdeclRequired_ClassParam_ReturnsTrue()
    {
        // Swift class → NonFrozenSafeHandle in PInvokeEmitter → non-blittable → @_cdecl required
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithType(
            "TestModule.ImagePipeline", TypeRecordFlags.RequiresMemoryManagement, TypeRecordKind.Class);
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";
        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var method = CreateMethod("test", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(WrapperValidation.IsParamTypeCdeclRequired(new NamedTypeSpec("TestModule.ImagePipeline"), env));
    }

    [Fact]
    public void IsParamTypeCdeclRequired_ObjCBridgedClassParam_ReturnsFalse()
    {
        // ObjC bridged class → IntPtr via .Handle in PInvokeEmitter → blittable → safe
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithType(
            "TestModule.NSObject", TypeRecordFlags.ObjCBridged | TypeRecordFlags.RequiresMemoryManagement, TypeRecordKind.Class);
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";
        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var method = CreateMethod("test", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.False(WrapperValidation.IsParamTypeCdeclRequired(new NamedTypeSpec("TestModule.NSObject"), env));
    }

    [Fact]
    public void IsParamTypeCdeclRequired_ObjCRootedClassParam_ReturnsFalse()
    {
        // ObjC rooted class → IntPtr via .Handle in PInvokeEmitter → blittable → safe
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithType(
            "TestModule.MyObjCClass", TypeRecordFlags.ObjCRooted | TypeRecordFlags.RequiresMemoryManagement, TypeRecordKind.Class);
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";
        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var method = CreateMethod("test", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.False(WrapperValidation.IsParamTypeCdeclRequired(new NamedTypeSpec("TestModule.MyObjCClass"), env));
    }

    #endregion

    #region IsReturnTypeCdeclRequired Tests

    [Fact]
    public void IsReturnTypeCdeclRequired_Primitive_ReturnsFalse()
    {
        var (_, typeDb) = CreateTestEnvironment();

        Assert.False(WrapperValidation.IsReturnTypeCdeclRequired(new NamedTypeSpec("Swift.Int"), typeDb));
        Assert.False(WrapperValidation.IsReturnTypeCdeclRequired(new NamedTypeSpec("Swift.Bool"), typeDb));
        Assert.False(WrapperValidation.IsReturnTypeCdeclRequired(new NamedTypeSpec("Swift.Float"), typeDb));
    }

    [Fact]
    public void IsReturnTypeCdeclRequired_NonFrozenStructReturn_ReturnsFalse()
    {
        // Non-frozen struct returns → IndirectResult/IntPtr → safe
        var (_, typeDb) = CreateTestEnvironmentWithType(
            "TestModule.OpaqueStruct", TypeRecordFlags.None, TypeRecordKind.Struct);

        Assert.False(WrapperValidation.IsReturnTypeCdeclRequired(new NamedTypeSpec("TestModule.OpaqueStruct"), typeDb));
    }

    [Fact]
    public void IsReturnTypeCdeclRequired_ClassReturn_ReturnsFalse()
    {
        // Class returns → IntPtr → safe
        var (_, typeDb) = CreateTestEnvironmentWithType(
            "TestModule.MyClass", TypeRecordFlags.RequiresMemoryManagement, TypeRecordKind.Class);

        Assert.False(WrapperValidation.IsReturnTypeCdeclRequired(new NamedTypeSpec("TestModule.MyClass"), typeDb));
    }

    [Fact]
    public void IsReturnTypeCdeclRequired_FrozenFloatStruct_ReturnsTrue()
    {
        // Custom frozen struct with float fields returned by value → Mono SIGSEGV
        var (_, typeDb) = CreateTestEnvironmentWithTypeRecord(
            "TestModule.FloatPoint", new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "FloatPoint"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.FloatPoint"),
                MetadataAccessor = "$sMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.HasFloatFields,
                Kind = TypeRecordKind.Struct,
                InlineSize = 16
            });

        Assert.True(WrapperValidation.IsReturnTypeCdeclRequired(new NamedTypeSpec("TestModule.FloatPoint"), typeDb));
    }

    [Fact]
    public void IsReturnTypeCdeclRequired_LargeSystemFrozenStruct_CBridging_ReturnsFalse()
    {
        // C-bridging module system frozen struct (CGSize = 16 bytes from CoreGraphics)
        // → pure C struct → CallConvSwift safe at any size
        var (_, typeDb) = CreateTestEnvironmentWithTypeRecord(
            "CoreGraphics.CGSize", new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("CoreGraphics", "CGSize"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("CoreGraphics.CGSize"),
                MetadataAccessor = "$sMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct,
                InlineSize = 16
            }, moduleName: "CoreGraphics");

        Assert.False(WrapperValidation.IsReturnTypeCdeclRequired(new NamedTypeSpec("CoreGraphics.CGSize"), typeDb));
    }

    [Fact]
    public void IsReturnTypeCdeclRequired_StringReturn_ReturnsTrue()
    {
        // String = 16 bytes, system frozen struct with RequiresMemoryManagement
        // Returned by value as SwiftString.Buffer — Mono JIT SIGSEGV with CallConvSwift
        var (_, typeDb) = CreateTestEnvironment();

        Assert.True(WrapperValidation.IsReturnTypeCdeclRequired(new NamedTypeSpec("Swift.String"), typeDb));
    }

    [Fact]
    public void IsReturnTypeCdeclRequired_SmallSystemFrozenStruct_ReturnsFalse()
    {
        // System frozen struct ≤ 8 bytes (Swift.Int = 8 bytes) → single register → safe
        var (_, typeDb) = CreateTestEnvironment();

        // Swift.Int is already registered with InlineSize=8 in CreateTestEnvironment
        Assert.False(WrapperValidation.IsReturnTypeCdeclRequired(new NamedTypeSpec("Swift.Int"), typeDb));
    }

    [Fact]
    public void IsReturnTypeCdeclRequired_ValueTuple_ReturnsTrue()
    {
        var (_, typeDb) = CreateTestEnvironment();

        var tupleSpec = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Int")
        });
        Assert.True(WrapperValidation.IsReturnTypeCdeclRequired(tupleSpec, typeDb));
    }

    [Fact]
    public void IsReturnTypeCdeclRequired_ClosureReturn_ReturnsTrue()
    {
        // Closure returns (16-byte SwiftClosureData) crash Mono JIT via CallConvSwift.
        // Must route through @_cdecl with indirect result buffer.
        var (_, typeDb) = CreateTestEnvironment();

        var closureSpec = new ClosureTypeSpec(
            new NamedTypeSpec("Swift.Int32"),
            new NamedTypeSpec("Swift.Int32"));
        Assert.True(WrapperValidation.IsReturnTypeCdeclRequired(closureSpec, typeDb));
    }

    [Fact]
    public void IsCdeclSafeTuple_AllPrimitives_ReturnsTrue()
    {
        // Tuple with all blittable primitives is safe for CdeclTuple buffer marshalling.
        var tupleSpec = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int32"),
            new NamedTypeSpec("Swift.Int32")
        });
        Assert.True(tupleSpec.Elements.All(e => CdeclParamMapper.IsCdeclPrimitive(e)));
    }

    [Fact]
    public void IsCdeclSafeTuple_WithString_ReturnsFalse()
    {
        // Tuple containing String is NOT safe for Unsafe.Write — String needs
        // per-element marshalling that CdeclTuple doesn't support.
        var tupleSpec = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int32"),
            new NamedTypeSpec("Swift.String")
        });
        Assert.False(tupleSpec.Elements.All(e => CdeclParamMapper.IsCdeclPrimitive(e)));
    }

    [Fact]
    public void IsCdeclSafeTuple_WithGenericParam_ReturnsFalse()
    {
        // Tuple with generic type parameter is NOT safe — can't Unsafe.Write a projected type.
        var tupleSpec = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int32"),
            new NamedTypeSpec("τ_0_0")
        });
        Assert.False(tupleSpec.Elements.All(e => CdeclParamMapper.IsCdeclPrimitive(e)));
    }

    [Fact]
    public void IsReturnTypeCdeclRequired_LargeIntegerStruct_ReturnsFalse()
    {
        // Large integer struct return → NOT flagged by return check (only params have the > 16 byte gate)
        // Returns use SwiftIndirectResult for large structs
        var (_, typeDb) = CreateTestEnvironmentWithTypeRecord(
            "TestModule.LargeStruct", new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "LargeStruct"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.LargeStruct"),
                MetadataAccessor = "$sMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct,
                InlineSize = 32
            });

        Assert.False(WrapperValidation.IsReturnTypeCdeclRequired(new NamedTypeSpec("TestModule.LargeStruct"), typeDb));
    }

    #endregion

    #region IsCBridgingModuleType Tests (Session 7G)

    [Theory]
    [InlineData("CoreGraphics.CGRect", true)]
    [InlineData("CoreGraphics.CGSize", true)]
    [InlineData("CoreGraphics.CGPoint", true)]
    [InlineData("CoreFoundation.CFRange", true)]
    [InlineData("Darwin.timeval", true)]
    [InlineData("simd.simd_float4", true)]
    [InlineData("Swift.String", false)]
    [InlineData("Swift.Int", false)]
    [InlineData("ObjectiveC.ObjCBool", false)]
    [InlineData("_Concurrency.CheckedContinuation", false)]
    [InlineData("TestModule.CustomStruct", false)]
    public void IsCBridgingModuleType_ClassifiesCorrectly(string typeName, bool expected)
    {
        var typeSpec = new NamedTypeSpec(typeName);
        Assert.Equal(expected, WrapperValidation.IsCBridgingModuleType(typeSpec));
    }

    [Fact]
    public void IsCBridgingModuleType_NoModule_ReturnsFalse()
    {
        var typeSpec = new NamedTypeSpec("Int");
        Assert.False(WrapperValidation.IsCBridgingModuleType(typeSpec));
    }

    [Fact]
    public void IsParamTypeCdeclRequired_CBridgingStruct32Bytes_ReturnsFalse()
    {
        // CGRect (32 bytes) from CoreGraphics → C-bridging module → safe at any size
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithTypeRecord(
            "CoreGraphics.CGRect", new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("CoreGraphics", "CGRect"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("CoreGraphics.CGRect"),
                MetadataAccessor = "$sMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct,
                InlineSize = 32
            }, moduleName: "CoreGraphics");
        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var method = CreateMethod("test", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.False(WrapperValidation.IsParamTypeCdeclRequired(new NamedTypeSpec("CoreGraphics.CGRect"), env));
    }

    [Fact]
    public void IsParamTypeCdeclRequired_SimdStruct64Bytes_ReturnsFalse()
    {
        // simd_float4x4 (64 bytes) from simd module → C-bridging → safe
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithTypeRecord(
            "simd.simd_float4x4", new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("simd", "simd_float4x4"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("simd.simd_float4x4"),
                MetadataAccessor = "$sMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct,
                InlineSize = 64
            }, moduleName: "simd");
        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var method = CreateMethod("test", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.False(WrapperValidation.IsParamTypeCdeclRequired(new NamedTypeSpec("simd.simd_float4x4"), env));
    }

    [Fact]
    public void IsReturnTypeCdeclRequired_CBridgingStruct32Bytes_ReturnsFalse()
    {
        // CGRect return (32 bytes) from CoreGraphics → safe
        var (_, typeDb) = CreateTestEnvironmentWithTypeRecord(
            "CoreGraphics.CGRect", new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("CoreGraphics", "CGRect"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("CoreGraphics.CGRect"),
                MetadataAccessor = "$sMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct,
                InlineSize = 32
            }, moduleName: "CoreGraphics");

        Assert.False(WrapperValidation.IsReturnTypeCdeclRequired(new NamedTypeSpec("CoreGraphics.CGRect"), typeDb));
    }

    [Fact]
    public void IsParamTypeCdeclRequired_SwiftModuleStruct16Bytes_StillReturnsTrue()
    {
        // Swift.String (16 bytes) from Swift module → NOT C-bridging → still requires @_cdecl
        // Ensures the relaxation is targeted to C-bridging modules only
        var (moduleDecl, typeDb) = CreateTestEnvironment();
        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var method = CreateMethod("test", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(WrapperValidation.IsParamTypeCdeclRequired(new NamedTypeSpec("Swift.String"), env));
    }

    [Fact]
    public void IsReturnTypeCdeclRequired_SwiftModuleStruct16Bytes_StillReturnsTrue()
    {
        // Swift.String return (16 bytes) from Swift module → NOT C-bridging → still requires @_cdecl
        var (_, typeDb) = CreateTestEnvironment();

        Assert.True(WrapperValidation.IsReturnTypeCdeclRequired(new NamedTypeSpec("Swift.String"), typeDb));
    }

    #endregion

    #region DefaultParameterOverloadEmitter ABI Safety Gating Tests (Session 7G)

    [Fact]
    public void OverloadCdeclCheck_IndependentOverload_GatedOnAbiSafety()
    {
        // When the primary method doesn't have UsesCdeclMethodWrapper, the overload emitter
        // independently checks ShouldEmitWrapper && RequiresCdeclForAbiSafety.
        // A method on a final class with only primitive params does NOT need @_cdecl.
        // Session 7G: verify that such methods don't get unnecessary @_cdecl wrappers.
        var typeDb = new TypeDatabase();
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Int64"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                MetadataAccessor = "$sSiMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct,
                InlineSize = 8
            });
        typeDb.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Calculator"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Calculator"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Calculator"),
                MetadataAccessor = "$s10TestModule10CalculatorCMa",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Class
            });
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

        var parentDecl = new ClassDecl
        {
            Name = "Calculator",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Calculator"),
            MangledName = "$s10TestModule10CalculatorCN",
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
        moduleDecl.Types.Add(parentDecl);

        // Method with only Int params on final class — NO ABI safety issue
        // UsesCdeclMethodWrapper is NOT set (primary method uses CallConvSwift)
        var method = new MethodDecl
        {
            Name = "compute",
            MangledName = "$s10TestModule10CalculatorC7computeSi_SitF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    Name = string.Empty,
                    PrivateName = string.Empty,
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                },
                new ArgumentDecl
                {
                    Name = "x",
                    PrivateName = "x",
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                    HasDefaultArg = false,
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                },
                new ArgumentDecl
                {
                    Name = "y",
                    PrivateName = "y",
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                    HasDefaultArg = true,
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                },
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public,
            // Primary method does NOT use @_cdecl (all primitives, final class)
            UsesCdeclMethodWrapper = false,
            UsesWrapperLibrary = false,
        };
        parentDecl.Methods.Add(method);

        var emissionContext = new ModuleEmissionContext();
        var csStringWriter = new StringWriter();
        var swiftStringWriter = new StringWriter();
        var csWriter = new CSharpWriter(csStringWriter);
        var swiftWriter = new SwiftWriter(swiftStringWriter);
        var env = new MethodEnvironment(method, typeDb);
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        DefaultParameterOverloadEmitter.TryEmitOverloads(
            csWriter, swiftWriter, env, logger, emissionContext);

        var swiftOutput = swiftStringWriter.ToString();

        // Should emit @_silgen_name wrapper (for default param handling)
        Assert.Contains("@_silgen_name", swiftOutput);
        Assert.Contains("_dbw_compute_", swiftOutput);

        // Should NOT emit @_cdecl wrapper (method doesn't need it for ABI safety)
        Assert.DoesNotContain("@_cdecl", swiftOutput);
        Assert.DoesNotContain("SBW_TestModule_Calculator_compute_", swiftOutput);
    }

    #endregion

    #region Test Helpers

    private static (ModuleDecl moduleDecl, TypeDatabase typeDb) CreateTestEnvironment()
    {
        var typeDb = new TypeDatabase();

        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Int64"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                MetadataAccessor = "$sSiMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct,
                InlineSize = 8
            });
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Bool"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Boolean"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Bool"),
                MetadataAccessor = "$sSbMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct,
                InlineSize = 1
            });
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.String"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.Runtime", "SwiftString"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.String"),
                MetadataAccessor = "$sSSMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Struct,
                InlineSize = 16
            });
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

        return (moduleDecl, typeDb);
    }

    private static (ModuleDecl moduleDecl, TypeDatabase typeDb) CreateTestEnvironmentWithType(
        string qualifiedName, TypeRecordFlags flags, TypeRecordKind kind)
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment();
        var swiftTypeName = SwiftTypeName.FromModuleQualifiedName(qualifiedName);
        var moduleName = qualifiedName.Split('.')[0];
        var typeName = swiftTypeName.Name;

        var testModule = new ModuleTypeDatabase(moduleName, $"/tmp/{moduleName}.dylib");
        testModule.RegisterType(
            swiftTypeName,
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName(moduleName, typeName),
                SwiftTypeName = swiftTypeName,
                MetadataAccessor = $"$s{typeName}Ma",
                Flags = flags,
                Kind = kind
            });
        typeDb.AddModuleDatabase(testModule);

        return (moduleDecl, typeDb);
    }

    private static (ModuleDecl moduleDecl, TypeDatabase typeDb) CreateTestEnvironmentWithTypeRecord(
        string qualifiedName, TypeRecord typeRecord, string? moduleName = null)
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment();
        var swiftTypeName = SwiftTypeName.FromModuleQualifiedName(qualifiedName);
        moduleName ??= qualifiedName.Split('.')[0];

        var testModule = new ModuleTypeDatabase(moduleName, $"/tmp/{moduleName}.dylib");
        testModule.RegisterType(swiftTypeName, typeRecord);
        typeDb.AddModuleDatabase(testModule);

        return (moduleDecl, typeDb);
    }

    private static MethodDecl CreateMethod(string name, TypeDecl parentDecl, ModuleDecl moduleDecl)
    {
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule_{name}",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    SwiftTypeSpec = TupleTypeSpec.Empty,
                    Name = "",
                    PrivateName = "",
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
    }

    private static MethodDecl CreateMethodWithReturn(string name, TypeSpec returnType, TypeDecl parentDecl, ModuleDecl moduleDecl)
    {
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule_{name}",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    SwiftTypeSpec = returnType,
                    Name = "",
                    PrivateName = "",
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
    }

    private static MethodDecl CreateMethodWithParam(string name, TypeSpec paramType, string paramName, TypeDecl parentDecl, ModuleDecl moduleDecl)
    {
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule_{name}",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    SwiftTypeSpec = TupleTypeSpec.Empty,
                    Name = "",
                    PrivateName = "",
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                },
                new ArgumentDecl
                {
                    SwiftTypeSpec = paramType,
                    Name = paramName,
                    PrivateName = paramName,
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
    }

    private static ClassDecl CreateClassDecl(string name, ModuleDecl moduleDecl, bool isFinal = true)
    {
        var decl = new ClassDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}CN",
            IsFinal = isFinal,
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

    private static StructDecl CreateStructDecl(string name, ModuleDecl moduleDecl)
    {
        var decl = new StructDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}VN",
            MetadataAccessor = $"$s10TestModule{name.Length}{name}VMa",
            IsFrozen = true,
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

    private static StructDecl CreateNonFrozenStructDecl(string name, ModuleDecl moduleDecl)
    {
        var decl = new StructDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}VN",
            MetadataAccessor = $"$s10TestModule{name.Length}{name}VMa",
            IsFrozen = false,
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

    #endregion

    #region HasNonBlittablePInvokeTypes Tests

    [Fact]
    public void HasNonBlittablePInvokeTypes_MethodWithNonFrozenStructParam_ReturnsTrue()
    {
        // Method with non-frozen struct param → SafeHandle in P/Invoke → non-blittable
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithType(
            "TestModule.OpaqueStruct", TypeRecordFlags.None, TypeRecordKind.Struct);
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyClass", moduleDecl, isFinal: false);
        var method = CreateMethodWithParam("process", new NamedTypeSpec("TestModule.OpaqueStruct"), "value", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(WrapperValidation.HasNonBlittablePInvokeTypes(env));
    }

    [Fact]
    public void HasNonBlittablePInvokeTypes_BlittableParams_ReturnsFalse()
    {
        // Method with only blittable params (Int) on final class — safe for CallConvSwift
        var (moduleDecl, typeDb) = CreateTestEnvironment();
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyClass", moduleDecl, isFinal: true);
        var method = CreateMethodWithParam("setCount", new NamedTypeSpec("Swift.Int"), "count", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.False(WrapperValidation.HasNonBlittablePInvokeTypes(env));
    }

    [Fact]
    public void HasNonBlittablePInvokeTypes_NonFrozenStructInstanceMember_ReturnsTrue()
    {
        // Instance method on non-frozen struct parent: self is IntPtr (non-blittable)
        // Register the parent struct type in the type database so IsNonFrozenStructInstanceMember resolves it
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithType(
            "TestModule.MyStruct", TypeRecordFlags.None, TypeRecordKind.Struct);
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        // Create a non-frozen struct parent
        var parentStruct = new StructDecl
        {
            Name = "MyStruct",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.MyStruct"),
            MangledName = "$s10TestModule8MyStructVN",
            MetadataAccessor = "$s10TestModule8MyStructVMa",
            IsFrozen = false,
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
        moduleDecl.Types.Add(parentStruct);

        var method = CreateMethodWithParam("doSomething", new NamedTypeSpec("Swift.Int"), "count", parentStruct, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        // Non-frozen struct instance member → IntPtr self mismatch → non-blittable
        Assert.True(WrapperValidation.HasNonBlittablePInvokeTypes(env));
    }

    [Fact]
    public void HasNonBlittablePInvokeTypes_ConstructorWithNonFrozenStructParam_ReturnsTrue()
    {
        // Constructor with non-frozen struct param → SafeHandle → non-blittable
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithType(
            "TestModule.OpaqueStruct", TypeRecordFlags.None, TypeRecordKind.Struct);
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyClass", moduleDecl, isFinal: false);
        var ctorMethod = CreateMethodWithParam("init", new NamedTypeSpec("TestModule.OpaqueStruct"), "value", parentDecl, moduleDecl);
        ctorMethod.IsConstructor = true;
        var env = new MethodEnvironment(ctorMethod, typeDb);

        Assert.True(WrapperValidation.HasNonBlittablePInvokeTypes(env));
    }

    [Fact]
    public void HasNonBlittablePInvokeTypes_GenericMethodWithNonFrozenStructParam_ReturnsTrue()
    {
        // Generic method with non-frozen struct param — param type is still non-blittable
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithType(
            "TestModule.OpaqueStruct", TypeRecordFlags.None, TypeRecordKind.Struct);
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyClass", moduleDecl, isFinal: false);
        var method = CreateMethodWithParam("process", new NamedTypeSpec("TestModule.OpaqueStruct"), "value", parentDecl, moduleDecl);
        method.GenericParameters = new List<GenericArgumentDecl>
        {
            new GenericArgumentDecl("T", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(WrapperValidation.HasNonBlittablePInvokeTypes(env));
    }

    [Fact]
    public void HasNonBlittablePInvokeTypes_PropertyWithNonFrozenStructSetter_ReturnsTrue()
    {
        // Property with setter taking non-frozen struct → SafeHandle param → non-blittable
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithType(
            "TestModule.OpaqueStruct", TypeRecordFlags.None, TypeRecordKind.Struct);
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyClass", moduleDecl, isFinal: false);
        var propertyDecl = new PropertyDecl
        {
            Name = "value",
            SwiftTypeSpec = new NamedTypeSpec("TestModule.OpaqueStruct"),
            HasStorage = false,
            IsStatic = false,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl
                {
                    Method = CreateMethod("get_value", parentDecl, moduleDecl)
                },
                new SetAccessorDecl
                {
                    Method = CreateMethod("set_value", parentDecl, moduleDecl)
                }
            },
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
        };

        var checkEnv = new MethodEnvironment(propertyDecl.Accessors[0].Method, typeDb);
        Assert.True(WrapperValidation.HasNonBlittablePInvokeTypes(checkEnv, propertyDecl));
    }

    [Fact]
    public void HasNonBlittablePInvokeTypes_PropertyWithBlittableType_ReturnsFalse()
    {
        // Property with blittable type (Int) — safe for CallConvSwift
        var (moduleDecl, typeDb) = CreateTestEnvironment();
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyClass", moduleDecl, isFinal: true);
        var propertyDecl = new PropertyDecl
        {
            Name = "count",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            HasStorage = false,
            IsStatic = false,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl
                {
                    Method = CreateMethod("get_count", parentDecl, moduleDecl)
                }
            },
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
        };

        var checkEnv = new MethodEnvironment(propertyDecl.Accessors[0].Method, typeDb);
        Assert.False(WrapperValidation.HasNonBlittablePInvokeTypes(checkEnv, propertyDecl));
    }

    [Fact]
    public void ShouldSuppressNonBlittable_CannotWrapWithNonBlittable_ReturnsTrue()
    {
        // CannotWrap + non-blittable params → returns true (method would crash at runtime)
        // No AsyncLibraryName → ShouldEmitWrapper=false → CannotWrap decision
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithType(
            "TestModule.OpaqueStruct", TypeRecordFlags.None, TypeRecordKind.Struct);
        // Deliberately omit typeDb.AsyncLibraryName — forces CannotWrap

        var parentDecl = CreateClassDecl("MyClass", moduleDecl, isFinal: false);
        var method = CreateMethodWithParam("process", new NamedTypeSpec("TestModule.OpaqueStruct"), "value", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(WrapperValidation.ShouldSuppressNonBlittableCallConvSwift(env));
    }

    [Fact]
    public void ShouldSuppressNonBlittable_WrapperRequired_ReturnsFalse()
    {
        // ShouldSuppressNonBlittableCallConvSwift returns false when wrapper is available
        // (WrapperRequired decision → method will get @_cdecl wrapper)
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithType(
            "TestModule.OpaqueStruct", TypeRecordFlags.None, TypeRecordKind.Struct);
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyClass", moduleDecl, isFinal: false);
        var method = CreateMethodWithParam("process", new NamedTypeSpec("TestModule.OpaqueStruct"), "value", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        // WrapperRequired → no suppression
        Assert.False(WrapperValidation.ShouldSuppressNonBlittableCallConvSwift(env));
    }

    [Fact]
    public void ShouldSuppressNonBlittable_AlreadyHasCdeclWrapper_ReturnsFalse()
    {
        // Method that already has a @_cdecl wrapper set — should never suppress
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithType(
            "TestModule.OpaqueStruct", TypeRecordFlags.None, TypeRecordKind.Struct);
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyClass", moduleDecl, isFinal: false);
        var method = CreateMethodWithParam("process", new NamedTypeSpec("TestModule.OpaqueStruct"), "value", parentDecl, moduleDecl);
        method.UsesCdeclMethodWrapper = true; // Already has wrapper
        var env = new MethodEnvironment(method, typeDb);

        Assert.False(WrapperValidation.ShouldSuppressNonBlittableCallConvSwift(env));
    }

    [Fact]
    public void HasNonBlittablePInvokeTypes_ClassParam_ReturnsTrue()
    {
        // Method with Swift class param → SafeHandle in P/Invoke → non-blittable
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithType(
            "TestModule.ImagePipeline", TypeRecordFlags.RequiresMemoryManagement, TypeRecordKind.Class);
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyClass", moduleDecl);
        var method = CreateMethodWithParam("setPipeline",
            new NamedTypeSpec("TestModule.ImagePipeline"), "pipeline", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.True(WrapperValidation.HasNonBlittablePInvokeTypes(env));
    }

    [Fact]
    public void HasNonBlittablePInvokeTypes_ObjCBridgedClassParam_ReturnsFalse()
    {
        // Method with ObjC bridged class param → IntPtr via .Handle → blittable
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithType(
            "TestModule.NSObject", TypeRecordFlags.ObjCBridged | TypeRecordFlags.RequiresMemoryManagement, TypeRecordKind.Class);
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyClass", moduleDecl);
        var method = CreateMethodWithParam("setObject",
            new NamedTypeSpec("TestModule.NSObject"), "object", parentDecl, moduleDecl);
        var env = new MethodEnvironment(method, typeDb);

        Assert.False(WrapperValidation.HasNonBlittablePInvokeTypes(env));
    }

    [Fact]
    public void HasNonBlittablePInvokeTypes_PropertyWithClassSetter_ReturnsTrue()
    {
        // Property setter where value type is a Swift class → SafeHandle → non-blittable
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithType(
            "TestModule.ImagePipeline", TypeRecordFlags.RequiresMemoryManagement, TypeRecordKind.Class);
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyClass", moduleDecl);
        var propertyDecl = new PropertyDecl
        {
            Name = "shared",
            SwiftTypeSpec = new NamedTypeSpec("TestModule.ImagePipeline"),
            HasStorage = true,
            IsStatic = true,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl { Method = CreateMethod("get_shared", parentDecl, moduleDecl) },
                new SetAccessorDecl { Method = CreateMethod("set_shared", parentDecl, moduleDecl) }
            },
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
        };

        var checkEnv = new MethodEnvironment(propertyDecl.Accessors[0].Method, typeDb);
        Assert.True(WrapperValidation.HasNonBlittablePInvokeTypes(checkEnv, propertyDecl));
    }

    #endregion
}
