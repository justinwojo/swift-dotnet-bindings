// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for SubscriptWrapperEmitter: per-subscript @_cdecl wrappers that route
/// subscript accessor P/Invokes through C calling convention to avoid CallConvSwift crashes.
/// </summary>
public class SubscriptWrapperEmitterTests
{
    #region ShouldEmitSubscriptWrapper Guard Tests

    [Fact]
    public void ShouldEmit_ValidClassSubscript_ReturnsTrue()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var accessor = new GetAccessorDecl { Method = CreateAccessorMethod("getter:subscript", true, parentDecl, moduleDecl) };
        var subscriptDecl = CreateSubscriptDecl(
            new NamedTypeSpec("Swift.Int"),
            new[] { CreateIndexParam("key", new NamedTypeSpec("Swift.Int"), moduleDecl) },
            new AccessorDecl[] { accessor },
            parentDecl, moduleDecl);

        var env = new MethodEnvironment(accessor.Method, typeDb);
        Assert.True(SubscriptWrapperEmitter.ShouldEmitSubscriptWrapper(subscriptDecl, accessor, env));
    }

    [Fact]
    public void ShouldEmit_NoAsyncLibraryName_ReturnsFalse()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        // AsyncLibraryName is null — not in xcframework mode

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var accessor = new GetAccessorDecl { Method = CreateAccessorMethod("getter:subscript", true, parentDecl, moduleDecl) };
        var subscriptDecl = CreateSubscriptDecl(
            new NamedTypeSpec("Swift.Int"),
            new[] { CreateIndexParam("key", new NamedTypeSpec("Swift.Int"), moduleDecl) },
            new AccessorDecl[] { accessor },
            parentDecl, moduleDecl);

        var env = new MethodEnvironment(accessor.Method, typeDb);
        Assert.False(SubscriptWrapperEmitter.ShouldEmitSubscriptWrapper(subscriptDecl, accessor, env));
    }

    [Fact]
    public void ShouldEmit_GenericClassParent_ConcreteSubscript_ReturnsTrue()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("GenericBox");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("GenericBox", moduleDecl);
        parentDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new("τ_0_0", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };
        var accessor = new GetAccessorDecl { Method = CreateAccessorMethod("getter:subscript", true, parentDecl, moduleDecl) };
        var subscriptDecl = CreateSubscriptDecl(
            new NamedTypeSpec("Swift.Int"),
            new[] { CreateIndexParam("key", new NamedTypeSpec("Swift.Int"), moduleDecl) },
            new AccessorDecl[] { accessor },
            parentDecl, moduleDecl);

        var env = new MethodEnvironment(accessor.Method, typeDb);
        Assert.True(SubscriptWrapperEmitter.ShouldEmitSubscriptWrapper(subscriptDecl, accessor, env));
    }

    [Fact]
    public void ShouldEmit_GenericStructParent_ReturnsFalse()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("GenericBox");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateStructDecl("GenericBox", moduleDecl);
        parentDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new("τ_0_0", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };
        var accessor = new GetAccessorDecl { Method = CreateAccessorMethod("getter:subscript", true, parentDecl, moduleDecl) };
        var subscriptDecl = CreateSubscriptDecl(
            new NamedTypeSpec("Swift.Int"),
            new[] { CreateIndexParam("key", new NamedTypeSpec("Swift.Int"), moduleDecl) },
            new AccessorDecl[] { accessor },
            parentDecl, moduleDecl);

        var env = new MethodEnvironment(accessor.Method, typeDb);
        Assert.False(SubscriptWrapperEmitter.ShouldEmitSubscriptWrapper(subscriptDecl, accessor, env));
    }

    [Fact]
    public void ShouldEmit_GenericClassParent_GenericReturnType_ReturnsFalse()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("GenericBox");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("GenericBox", moduleDecl);
        parentDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new("τ_0_0", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };
        var accessor = new GetAccessorDecl { Method = CreateAccessorMethod("getter:subscript", true, parentDecl, moduleDecl) };
        // Return type references τ_0_0
        var subscriptDecl = CreateSubscriptDecl(
            new NamedTypeSpec("τ_0_0"),
            new[] { CreateIndexParam("key", new NamedTypeSpec("Swift.Int"), moduleDecl) },
            new AccessorDecl[] { accessor },
            parentDecl, moduleDecl);

        var env = new MethodEnvironment(accessor.Method, typeDb);
        Assert.False(SubscriptWrapperEmitter.ShouldEmitSubscriptWrapper(subscriptDecl, accessor, env));
    }

    [Fact]
    public void ShouldEmit_StaticSubscript_ReturnsFalse()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var accessor = new GetAccessorDecl { Method = CreateAccessorMethod("getter:subscript", true, parentDecl, moduleDecl) };
        var subscriptDecl = CreateSubscriptDecl(
            new NamedTypeSpec("Swift.Int"),
            new[] { CreateIndexParam("key", new NamedTypeSpec("Swift.Int"), moduleDecl) },
            new AccessorDecl[] { accessor },
            parentDecl, moduleDecl,
            isStatic: true);

        var env = new MethodEnvironment(accessor.Method, typeDb);
        Assert.False(SubscriptWrapperEmitter.ShouldEmitSubscriptWrapper(subscriptDecl, accessor, env));
    }

    [Fact]
    public void ShouldEmit_AsyncAccessor_ReturnsFalse()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var method = CreateAccessorMethod("getter:subscript", true, parentDecl, moduleDecl);
        method.IsAsync = true;
        var accessor = new GetAccessorDecl { Method = method };
        var subscriptDecl = CreateSubscriptDecl(
            new NamedTypeSpec("Swift.Int"),
            new[] { CreateIndexParam("key", new NamedTypeSpec("Swift.Int"), moduleDecl) },
            new AccessorDecl[] { accessor },
            parentDecl, moduleDecl);

        var env = new MethodEnvironment(accessor.Method, typeDb);
        Assert.False(SubscriptWrapperEmitter.ShouldEmitSubscriptWrapper(subscriptDecl, accessor, env));
    }

    [Fact]
    public void ShouldEmit_NonCopyableStructParent_ReturnsTrue()
    {
        // Noncopyable types now get @_cdecl wrappers with borrowing pointer semantics
        var (moduleDecl, typeDb) = CreateTestEnvironment("MoveOnly");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateStructDecl("MoveOnly", moduleDecl);
        // Non-copyable: has Escapable but NOT Copyable
        parentDecl.Conformances = new List<TypeConformance>
        {
            new(SwiftTypeName.FromModuleQualifiedName("TestModule.MoveOnly"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Escapable"),
                "$s10TestModule8MoveOnlyVACSWAAMc")
        };
        var accessor = new GetAccessorDecl { Method = CreateAccessorMethod("getter:subscript", true, parentDecl, moduleDecl) };
        var subscriptDecl = CreateSubscriptDecl(
            new NamedTypeSpec("Swift.Int"),
            new[] { CreateIndexParam("key", new NamedTypeSpec("Swift.Int"), moduleDecl) },
            new AccessorDecl[] { accessor },
            parentDecl, moduleDecl);

        var env = new MethodEnvironment(accessor.Method, typeDb);
        Assert.True(SubscriptWrapperEmitter.ShouldEmitSubscriptWrapper(subscriptDecl, accessor, env));
    }

    [Fact]
    public void ShouldEmit_OpaqueReturnType_ReturnsFalse()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var accessor = new GetAccessorDecl { Method = CreateAccessorMethod("getter:subscript", true, parentDecl, moduleDecl) };
        var opaqueReturn = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.SomeProto") }) { IsOpaque = true };
        var subscriptDecl = CreateSubscriptDecl(
            opaqueReturn,
            new[] { CreateIndexParam("key", new NamedTypeSpec("Swift.Int"), moduleDecl) },
            new AccessorDecl[] { accessor },
            parentDecl, moduleDecl);

        var env = new MethodEnvironment(accessor.Method, typeDb);
        Assert.False(SubscriptWrapperEmitter.ShouldEmitSubscriptWrapper(subscriptDecl, accessor, env));
    }

    [Fact]
    public void ShouldEmit_ClosureReturnType_ReturnsFalse()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var accessor = new GetAccessorDecl { Method = CreateAccessorMethod("getter:subscript", true, parentDecl, moduleDecl) };
        var closureReturn = new ClosureTypeSpec(
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Int"));
        var subscriptDecl = CreateSubscriptDecl(
            closureReturn,
            new[] { CreateIndexParam("key", new NamedTypeSpec("Swift.Int"), moduleDecl) },
            new AccessorDecl[] { accessor },
            parentDecl, moduleDecl);

        var env = new MethodEnvironment(accessor.Method, typeDb);
        Assert.False(SubscriptWrapperEmitter.ShouldEmitSubscriptWrapper(subscriptDecl, accessor, env));
    }

    [Fact]
    public void ShouldEmit_NonEmptyTupleReturn_ReturnsTrue()
    {
        // Tuple returns are now routed through IndirectResult (resultPtr buffer)
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var accessor = new GetAccessorDecl { Method = CreateAccessorMethod("getter:subscript", true, parentDecl, moduleDecl) };
        var tupleReturn = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Int")
        });
        var subscriptDecl = CreateSubscriptDecl(
            tupleReturn,
            new[] { CreateIndexParam("key", new NamedTypeSpec("Swift.Int"), moduleDecl) },
            new AccessorDecl[] { accessor },
            parentDecl, moduleDecl);

        var env = new MethodEnvironment(accessor.Method, typeDb);
        Assert.True(SubscriptWrapperEmitter.ShouldEmitSubscriptWrapper(subscriptDecl, accessor, env));
    }

    [Fact]
    public void ShouldEmit_OptionalValueTypeReturn_ReturnsTrue()
    {
        // Optional<value-type> returns now handled via @_cdecl IndirectResult
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var accessor = new GetAccessorDecl { Method = CreateAccessorMethod("getter:subscript", true, parentDecl, moduleDecl) };
        var optionalSpec = new NamedTypeSpec("Swift.Optional");
        optionalSpec.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        var subscriptDecl = CreateSubscriptDecl(
            optionalSpec,
            new[] { CreateIndexParam("key", new NamedTypeSpec("Swift.Int"), moduleDecl) },
            new AccessorDecl[] { accessor },
            parentDecl, moduleDecl);

        var env = new MethodEnvironment(accessor.Method, typeDb);
        Assert.True(SubscriptWrapperEmitter.ShouldEmitSubscriptWrapper(subscriptDecl, accessor, env));
    }

    [Fact]
    public void ShouldEmit_OptionalClassReturn_ReturnsTrue()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithExtraTypes("MyType",
            ("TestModule.Child", TypeRecordFlags.None, TypeRecordKind.Class));
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var accessor = new GetAccessorDecl { Method = CreateAccessorMethod("getter:subscript", true, parentDecl, moduleDecl) };
        var optionalSpec = new NamedTypeSpec("Swift.Optional");
        optionalSpec.GenericParameters.Add(new NamedTypeSpec("TestModule.Child"));
        var subscriptDecl = CreateSubscriptDecl(
            optionalSpec,
            new[] { CreateIndexParam("key", new NamedTypeSpec("Swift.Int"), moduleDecl) },
            new AccessorDecl[] { accessor },
            parentDecl, moduleDecl);

        var env = new MethodEnvironment(accessor.Method, typeDb);
        Assert.True(SubscriptWrapperEmitter.ShouldEmitSubscriptWrapper(subscriptDecl, accessor, env));
    }

    [Fact]
    public void ShouldEmit_StructSubscript_ReturnsTrue()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateStructDecl("MyType", moduleDecl);
        var accessor = new GetAccessorDecl { Method = CreateAccessorMethod("getter:subscript", true, parentDecl, moduleDecl) };
        var subscriptDecl = CreateSubscriptDecl(
            new NamedTypeSpec("Swift.Int"),
            new[] { CreateIndexParam("index", new NamedTypeSpec("Swift.Int"), moduleDecl) },
            new AccessorDecl[] { accessor },
            parentDecl, moduleDecl);

        var env = new MethodEnvironment(accessor.Method, typeDb);
        Assert.True(SubscriptWrapperEmitter.ShouldEmitSubscriptWrapper(subscriptDecl, accessor, env));
    }

    #endregion

    #region Collection Container Guard Tests

    [Fact]
    public void ShouldEmit_ArrayReturn_ReturnsTrue()
    {
        // Array returns now handled via @_cdecl IndirectResult
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var accessor = new GetAccessorDecl { Method = CreateAccessorMethod("getter:subscript", true, parentDecl, moduleDecl) };
        var arraySpec = new NamedTypeSpec("Swift.Array");
        arraySpec.GenericParameters.Add(new NamedTypeSpec("Swift.String"));
        var subscriptDecl = CreateSubscriptDecl(
            arraySpec,
            new[] { CreateIndexParam("key", new NamedTypeSpec("Swift.Int"), moduleDecl) },
            new AccessorDecl[] { accessor },
            parentDecl, moduleDecl);

        var env = new MethodEnvironment(accessor.Method, typeDb);
        Assert.True(SubscriptWrapperEmitter.ShouldEmitSubscriptWrapper(subscriptDecl, accessor, env));
    }

    [Fact]
    public void ShouldEmit_DictionaryIndexParam_ReturnsTrue()
    {
        // Dictionary index params now handled via @_cdecl UnsafeRawPointer
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var accessor = new GetAccessorDecl { Method = CreateAccessorMethod("getter:subscript", true, parentDecl, moduleDecl) };
        var dictSpec = new NamedTypeSpec("Swift.Dictionary");
        dictSpec.GenericParameters.Add(new NamedTypeSpec("Swift.String"));
        dictSpec.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        var subscriptDecl = CreateSubscriptDecl(
            new NamedTypeSpec("Swift.Int"),
            new[] { CreateIndexParam("mapping", dictSpec, moduleDecl) },
            new AccessorDecl[] { accessor },
            parentDecl, moduleDecl);

        var env = new MethodEnvironment(accessor.Method, typeDb);
        Assert.True(SubscriptWrapperEmitter.ShouldEmitSubscriptWrapper(subscriptDecl, accessor, env));
    }

    #endregion

    #region Symbol Name Tests

    [Fact]
    public void GetSubscriptAccessorSymbolName_Getter_CorrectFormat()
    {
        var symbol = SubscriptWrapperEmitter.GetSubscriptAccessorSymbolName(
            "TestModule", "MyType", "$s10TestModule6MyTypeVSubscriptMangled", isGetter: true);

        Assert.StartsWith("SBW_SubGet_TestModule_MyType_", symbol);
        Assert.Equal(8, symbol.Split('_').Last().Length); // 8-char hash
    }

    [Fact]
    public void GetSubscriptAccessorSymbolName_Setter_CorrectFormat()
    {
        var symbol = SubscriptWrapperEmitter.GetSubscriptAccessorSymbolName(
            "TestModule", "MyType", "$s10TestModule6MyTypeVSubscriptMangled", isGetter: false);

        Assert.StartsWith("SBW_SubSet_TestModule_MyType_", symbol);
    }

    [Fact]
    public void GetSubscriptAccessorSymbolName_NestedType_DotReplacedWithUnderscore()
    {
        var symbol = SubscriptWrapperEmitter.GetSubscriptAccessorSymbolName(
            "TestModule", "Outer.Inner", "$sMangled", isGetter: true);

        Assert.Contains("Outer_Inner", symbol);
        Assert.DoesNotContain("Outer.Inner", symbol);
    }

    [Fact]
    public void GetSubscriptAccessorSymbolName_SameMangled_DeterministicHash()
    {
        var symbol1 = SubscriptWrapperEmitter.GetSubscriptAccessorSymbolName(
            "M", "T", "$sMangled", isGetter: true);
        var symbol2 = SubscriptWrapperEmitter.GetSubscriptAccessorSymbolName(
            "M", "T", "$sMangled", isGetter: true);

        Assert.Equal(symbol1, symbol2);
    }

    [Fact]
    public void GetSubscriptAccessorSymbolName_DifferentMangled_DifferentHash()
    {
        var symbol1 = SubscriptWrapperEmitter.GetSubscriptAccessorSymbolName(
            "M", "T", "$sMangled1", isGetter: true);
        var symbol2 = SubscriptWrapperEmitter.GetSubscriptAccessorSymbolName(
            "M", "T", "$sMangled2", isGetter: true);

        Assert.NotEqual(symbol1, symbol2);
    }

    #endregion

    #region Getter Emission Tests

    [Fact]
    public void EmitGetterWrapper_ClassSelf_UsesUnmanaged()
    {
        var (swiftWriter, sw, subscriptDecl, env, ctx) = CreateGetterTestSetup(
            new NamedTypeSpec("Swift.Int"),
            new[] { CreateIndexParam("key", new NamedTypeSpec("Swift.Int"), env: null) },
            isClass: true);

        var symbol = "SBW_SubGet_TestModule_MyType_abc12345";
        SubscriptWrapperEmitter.EmitSwiftSubscriptGetterWrapper(swiftWriter, subscriptDecl, symbol, env, ctx);

        var output = sw.ToString();
        Assert.Contains("@_cdecl(\"SBW_SubGet_TestModule_MyType_abc12345\")", output);
        Assert.Contains("Unmanaged<TestModule.MyType>.fromOpaque(self_).takeUnretainedValue()", output);
        Assert.Contains("obj[", output);
    }

    [Fact]
    public void EmitGetterWrapper_StructSelf_UsesAssuming()
    {
        var (swiftWriter, sw, subscriptDecl, env, ctx) = CreateGetterTestSetup(
            new NamedTypeSpec("Swift.Int"),
            new[] { CreateIndexParam("key", new NamedTypeSpec("Swift.Int"), env: null) },
            isClass: false);

        var symbol = "SBW_SubGet_TestModule_MyType_def67890";
        SubscriptWrapperEmitter.EmitSwiftSubscriptGetterWrapper(swiftWriter, subscriptDecl, symbol, env, ctx);

        var output = sw.ToString();
        Assert.Contains("self_.assumingMemoryBound(to: TestModule.MyType.self).pointee", output);
        Assert.Contains("UnsafeRawPointer", output); // struct self is read-only
    }

    [Fact]
    public void EmitGetterWrapper_StringReturn_EmitsUtf8Slice()
    {
        var (swiftWriter, sw, subscriptDecl, env, ctx) = CreateGetterTestSetup(
            new NamedTypeSpec("Swift.String"),
            new[] { CreateIndexParam("key", new NamedTypeSpec("Swift.Int"), env: null) },
            isClass: true);

        var symbol = "SBW_SubGet_TestModule_MyType_str12345";
        SubscriptWrapperEmitter.EmitSwiftSubscriptGetterWrapper(swiftWriter, subscriptDecl, symbol, env, ctx);

        var output = sw.ToString();
        Assert.Contains("SBW_Utf8Slice", output);
        Assert.Contains("result.utf8", output);
        Assert.Contains("resultPtr", output);
    }

    [Fact]
    public void EmitGetterWrapper_IndirectResult_UsesResultPtr()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithExtraTypes("MyType",
            ("TestModule.Config", TypeRecordFlags.None, TypeRecordKind.Struct));
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var accessor = new GetAccessorDecl { Method = CreateAccessorMethod("getter:subscript", true, parentDecl, moduleDecl) };
        var subscriptDecl = CreateSubscriptDecl(
            new NamedTypeSpec("TestModule.Config"),
            new[] { CreateIndexParam("key", new NamedTypeSpec("Swift.Int"), moduleDecl) },
            new AccessorDecl[] { accessor },
            parentDecl, moduleDecl);

        var env = new MethodEnvironment(accessor.Method, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);

        SubscriptWrapperEmitter.EmitSwiftSubscriptGetterWrapper(swiftWriter, subscriptDecl, "SBW_SubGet_test", env, ctx);

        var output = sw.ToString();
        Assert.Contains("resultPtr", output);
        Assert.Contains("initializeMemory", output);
    }

    [Fact]
    public void EmitGetterWrapper_DuplicateSymbol_OnlyEmitsOnce()
    {
        var (swiftWriter, sw, subscriptDecl, env, ctx) = CreateGetterTestSetup(
            new NamedTypeSpec("Swift.Int"),
            new[] { CreateIndexParam("key", new NamedTypeSpec("Swift.Int"), env: null) },
            isClass: true);

        var symbol = "SBW_SubGet_TestModule_MyType_dedup123";
        SubscriptWrapperEmitter.EmitSwiftSubscriptGetterWrapper(swiftWriter, subscriptDecl, symbol, env, ctx);
        SubscriptWrapperEmitter.EmitSwiftSubscriptGetterWrapper(swiftWriter, subscriptDecl, symbol, env, ctx);

        var output = sw.ToString();
        var cdeclCount = output.Split("@_cdecl(\"").Length - 1;
        Assert.Equal(1, cdeclCount);
    }

    [Fact]
    public void EmitGetterWrapper_MainActorIsolated_HasAnnotationOnCdecl()
    {
        // @MainActor IS propagated to @_cdecl wrappers — Swift 6 requires the caller
        // to share isolation context. @MainActor on @_cdecl is compile-time only.
        var (swiftWriter, sw, subscriptDecl, env, ctx) = CreateGetterTestSetup(
            new NamedTypeSpec("Swift.Int"),
            new[] { CreateIndexParam("key", new NamedTypeSpec("Swift.Int"), env: null) },
            isClass: true,
            isMainActorIsolated: true);

        SubscriptWrapperEmitter.EmitSwiftSubscriptGetterWrapper(swiftWriter, subscriptDecl, "SBW_SubGet_test", env, ctx);

        var output = sw.ToString();
        Assert.Contains("@MainActor", output);
        Assert.Contains("@_cdecl", output);
    }

    [Fact]
    public void EmitGetterWrapper_TupleReturn_UsesResultPtr()
    {
        // Tuple returns use resultPtr.initializeMemory(as: (T1, T2).self)
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var accessor = new GetAccessorDecl { Method = CreateAccessorMethod("getter:subscript", true, parentDecl, moduleDecl) };
        var tupleReturn = new TupleTypeSpec(new List<TypeSpec>
        {
            new NamedTypeSpec("Swift.Int"),
            new NamedTypeSpec("Swift.Int")
        });
        var subscriptDecl = CreateSubscriptDecl(
            tupleReturn,
            new[] { CreateIndexParam("key", new NamedTypeSpec("Swift.Int"), moduleDecl) },
            new AccessorDecl[] { accessor },
            parentDecl, moduleDecl);

        var env = new MethodEnvironment(accessor.Method, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);

        SubscriptWrapperEmitter.EmitSwiftSubscriptGetterWrapper(swiftWriter, subscriptDecl, "SBW_SubGet_tuple", env, ctx);

        var output = sw.ToString();
        Assert.Contains("_ resultPtr: UnsafeMutableRawPointer", output);
        Assert.Contains("initializeMemory(as: (Swift.Int, Swift.Int).self", output);
    }

    #endregion

    #region Setter Emission Tests

    [Fact]
    public void EmitSetterWrapper_ClassSelf_AlwaysMutable()
    {
        var (swiftWriter, sw, subscriptDecl, env, ctx) = CreateSetterTestSetup(
            new NamedTypeSpec("Swift.Int"),
            new[] { CreateIndexParam("key", new NamedTypeSpec("Swift.Int"), env: null) },
            isClass: true);

        SubscriptWrapperEmitter.EmitSwiftSubscriptSetterWrapper(swiftWriter, subscriptDecl, "SBW_SubSet_test", env, ctx);

        var output = sw.ToString();
        Assert.Contains("UnsafeMutableRawPointer", output); // setter self is always mutable
        Assert.Contains("Unmanaged<TestModule.MyType>.fromOpaque(self_).takeUnretainedValue()", output);
    }

    [Fact]
    public void EmitSetterWrapper_StructSelf_MutablePointer()
    {
        var (swiftWriter, sw, subscriptDecl, env, ctx) = CreateSetterTestSetup(
            new NamedTypeSpec("Swift.Int"),
            new[] { CreateIndexParam("key", new NamedTypeSpec("Swift.Int"), env: null) },
            isClass: false);

        SubscriptWrapperEmitter.EmitSwiftSubscriptSetterWrapper(swiftWriter, subscriptDecl, "SBW_SubSet_test", env, ctx);

        var output = sw.ToString();
        Assert.Contains("UnsafeMutableRawPointer", output);
        Assert.Contains("assumingMemoryBound", output);
        Assert.Contains("pointee[", output);
    }

    [Fact]
    public void EmitSetterWrapper_StringValue_EncodesUtf8()
    {
        var (swiftWriter, sw, subscriptDecl, env, ctx) = CreateSetterTestSetup(
            new NamedTypeSpec("Swift.String"),
            new[] { CreateIndexParam("key", new NamedTypeSpec("Swift.Int"), env: null) },
            isClass: true);

        SubscriptWrapperEmitter.EmitSwiftSubscriptSetterWrapper(swiftWriter, subscriptDecl, "SBW_SubSet_str_test", env, ctx);

        var output = sw.ToString();
        Assert.Contains("utf8Ptr", output);
        Assert.Contains("utf8Len", output);
        Assert.Contains("String(bytes:", output);
    }

    [Fact]
    public void EmitSetterWrapper_DuplicateSymbol_OnlyEmitsOnce()
    {
        var (swiftWriter, sw, subscriptDecl, env, ctx) = CreateSetterTestSetup(
            new NamedTypeSpec("Swift.Int"),
            new[] { CreateIndexParam("key", new NamedTypeSpec("Swift.Int"), env: null) },
            isClass: true);

        var symbol = "SBW_SubSet_TestModule_MyType_dedup123";
        SubscriptWrapperEmitter.EmitSwiftSubscriptSetterWrapper(swiftWriter, subscriptDecl, symbol, env, ctx);
        SubscriptWrapperEmitter.EmitSwiftSubscriptSetterWrapper(swiftWriter, subscriptDecl, symbol, env, ctx);

        var output = sw.ToString();
        var cdeclCount = output.Split("@_cdecl(\"").Length - 1;
        Assert.Equal(1, cdeclCount);
    }

    [Fact]
    public void EmitGetterWrapper_ConstrainedGenericClassParent_ConcreteReturn_EmitsPwtAfterMetadata()
    {
        // Subscript counterpart of the property-getter SIGSEGV on constrained-generic
        // classes: when the parent class carries a resolvable protocol conformance
        // (e.g., `class Box<T: Marker>`), the C# P/Invoke side passes both _metadata0
        // AND _pwt0 in the Metadata phase. The Swift wrapper signature must absorb
        // both, otherwise the PWT pointer slides into the self_ slot and the wrapper's
        // `Unmanaged.fromOpaque(self_)` cast walks garbage.
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithExtraTypes(
            "ConstrainedBox",
            ("TestModule.Marker", TypeRecordFlags.None, TypeRecordKind.Protocol));
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("ConstrainedBox", moduleDecl);
        parentDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new("τ_0_0", "T",
                new List<GenericParameterConformance>
                {
                    new(new[] { "τ_0_0" },
                        SwiftTypeName.FromModuleQualifiedName("TestModule.Marker"),
                        ConformanceKind.Protocol)
                },
                new List<GenericParameterConformance>())
        };
        var accessor = new GetAccessorDecl { Method = CreateAccessorMethod("getter:subscript", true, parentDecl, moduleDecl) };
        var subscriptDecl = CreateSubscriptDecl(
            new NamedTypeSpec("Swift.Int"),
            new[] { CreateIndexParam("index", new NamedTypeSpec("Swift.Int"), moduleDecl) },
            new AccessorDecl[] { accessor },
            parentDecl, moduleDecl);

        var env = new MethodEnvironment(accessor.Method, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);
        var symbol = "SBW_SubGet_TestModule_ConstrainedBox_cgcp1234";

        SubscriptWrapperEmitter.EmitSwiftSubscriptGetterWrapper(swiftWriter, subscriptDecl, symbol, env, ctx);

        var output = sw.ToString();
        Assert.Contains("_ _metadata0: UnsafeRawPointer", output);
        Assert.Contains("_ _pwt0: UnsafeRawPointer", output);
        var cdeclLine = output.Split('\n').First(l => l.Contains("public func _sbw_subget_"));
        var metaIdx = cdeclLine.IndexOf("_metadata0");
        var pwtIdx = cdeclLine.IndexOf("_pwt0");
        var selfIdx = cdeclLine.IndexOf("self_");
        Assert.True(metaIdx >= 0 && pwtIdx >= 0 && selfIdx >= 0, $"Expected all three params on the wrapper signature; got: {cdeclLine}");
        Assert.True(metaIdx < pwtIdx, "Metadata must come before PWT");
        Assert.True(pwtIdx < selfIdx, "PWT must come before self_");
    }

    [Fact]
    public void EmitSetterWrapper_ConstrainedGenericClassParent_ConcreteValue_EmitsPwtAfterMetadata()
    {
        // Setter counterpart of the constrained-generic subscript regression — the
        // wrapper must absorb both _metadata0 and _pwt0 in the Metadata phase so the
        // PWT pointer does not slide into the self_ slot.
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithExtraTypes(
            "ConstrainedBox",
            ("TestModule.Marker", TypeRecordFlags.None, TypeRecordKind.Protocol));
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("ConstrainedBox", moduleDecl);
        parentDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new("τ_0_0", "T",
                new List<GenericParameterConformance>
                {
                    new(new[] { "τ_0_0" },
                        SwiftTypeName.FromModuleQualifiedName("TestModule.Marker"),
                        ConformanceKind.Protocol)
                },
                new List<GenericParameterConformance>())
        };
        var accessor = new SetAccessorDecl { Method = CreateAccessorMethod("setter:subscript", false, parentDecl, moduleDecl) };
        var subscriptDecl = CreateSubscriptDecl(
            new NamedTypeSpec("Swift.Int"),
            new[] { CreateIndexParam("index", new NamedTypeSpec("Swift.Int"), moduleDecl) },
            new AccessorDecl[] { accessor },
            parentDecl, moduleDecl);

        var env = new MethodEnvironment(accessor.Method, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);
        var symbol = "SBW_SubSet_TestModule_ConstrainedBox_cgcp5678";

        SubscriptWrapperEmitter.EmitSwiftSubscriptSetterWrapper(swiftWriter, subscriptDecl, symbol, env, ctx);

        var output = sw.ToString();
        Assert.Contains("_ _metadata0: UnsafeRawPointer", output);
        Assert.Contains("_ _pwt0: UnsafeRawPointer", output);
        var cdeclLine = output.Split('\n').First(l => l.Contains("public func _sbw_subset_"));
        var metaIdx = cdeclLine.IndexOf("_metadata0");
        var pwtIdx = cdeclLine.IndexOf("_pwt0");
        var selfIdx = cdeclLine.IndexOf("self_");
        Assert.True(metaIdx >= 0 && pwtIdx >= 0 && selfIdx >= 0, $"Expected all three params on the wrapper signature; got: {cdeclLine}");
        Assert.True(metaIdx < pwtIdx, "Metadata must come before PWT");
        Assert.True(pwtIdx < selfIdx, "PWT must come before self_");
    }

    #endregion

    #region Cdecl Subscript C# Emission Regression Tests

    [Fact]
    public void CdeclGetterWithStringIndexParam_AppliesReturnProjection()
    {
        // Regression: Finding 1 — hasStringIndexParam early return bypassed return projection.
        // A subscript[key: String] -> Data should emit byte[] conversion, not raw Data return.
        var sw = new StringWriter();
        var csWriter = new CSharpWriter(sw);
        var paramInfos = new List<(string typeName, string paramName, ITypeProjection? projection)>
        {
            ("string", "key", new StringProjection())
        };
        var returnProjection = new DataProjection();

        SubscriptHandler.EmitCdeclGetterWithFixedBlock(
            csWriter, "GetSubscript", "(IntPtr)__keyPtr, __keyUtf8.Length",
            setupLines: new List<string> { "var __keyUtf8 = System.Text.Encoding.UTF8.GetBytes(key);" },
            usingLines: new List<string>(),
            paramInfos, hasStringIndexParam: true,
            returnProjection: returnProjection, isStringReturn: false);

        var output = sw.ToString();
        // Must apply Data→byte[] conversion via projection
        Assert.Contains(".ToByteArray()", output);
        // Should NOT contain a plain "return GetSubscript(...)" without conversion
        Assert.DoesNotContain("return GetSubscript((IntPtr)__keyPtr, __keyUtf8.Length);", output);
    }

    [Fact]
    public void CdeclGetterWithStringIndexParam_WithDisposableProjection_EmitsUsing()
    {
        // Regression: Finding 1 — return projection with disposal (e.g. SwiftString→string)
        // must emit using+conversion even inside fixed blocks.
        var sw = new StringWriter();
        var csWriter = new CSharpWriter(sw);
        var paramInfos = new List<(string typeName, string paramName, ITypeProjection? projection)>
        {
            ("string", "key", new StringProjection())
        };
        var returnProjection = new StringProjection();

        SubscriptHandler.EmitCdeclGetterWithFixedBlock(
            csWriter, "GetSubscript", "(IntPtr)__keyPtr, __keyUtf8.Length",
            setupLines: new List<string> { "var __keyUtf8 = System.Text.Encoding.UTF8.GetBytes(key);" },
            usingLines: new List<string>(),
            paramInfos, hasStringIndexParam: true,
            returnProjection: returnProjection, isStringReturn: false);

        var output = sw.ToString();
        Assert.Contains("using var __ret", output);
        Assert.Contains(".ToString()", output);
    }

    [Fact]
    public void CdeclGetterStringReturn_EmptyString_DoesNotFreeSbwBuffer()
    {
        // Regression: Finding 2 — empty-string getter freed shared _sbw_emptyBuffer.
        // When Len==0, must return string.Empty WITHOUT calling SBW_Free.
        var sw = new StringWriter();
        var csWriter = new CSharpWriter(sw);
        var paramInfos = new List<(string typeName, string paramName, ITypeProjection? projection)>
        {
            ("nint", "index", null)
        };

        SubscriptHandler.EmitCdeclGetterWithFixedBlock(
            csWriter, "GetSubscript", "index",
            setupLines: new List<string>(),
            usingLines: new List<string>(),
            paramInfos, hasStringIndexParam: false,
            returnProjection: null, isStringReturn: true);

        var output = sw.ToString();
        // String return now delegates to SwiftMarshal.ReadUtf8Slice (handles empty-string + free internally)
        Assert.Contains("SwiftMarshal.ReadUtf8Slice", output);
    }

    [Fact]
    public void CdeclSetterWithStringIndexParam_AppliesValueProjection()
    {
        // Regression: Finding 1 — hasStringIndexParam early return bypassed value projection.
        // A subscript setter with string index param and Data value should emit FromByteArray conversion.
        var sw = new StringWriter();
        var csWriter = new CSharpWriter(sw);
        var paramInfos = new List<(string typeName, string paramName, ITypeProjection? projection)>
        {
            ("string", "key", new StringProjection())
        };
        var valueProjection = new DataProjection();

        SubscriptHandler.EmitCdeclSetterWithFixedBlock(
            csWriter, "SetSubscript", "(IntPtr)__keyPtr, __keyUtf8.Length",
            setupLines: new List<string> { "var __keyUtf8 = System.Text.Encoding.UTF8.GetBytes(key);" },
            usingLines: new List<string>(),
            paramInfos, hasStringIndexParam: true,
            valueProjection: valueProjection, isStringValue: false);

        var output = sw.ToString();
        // Must apply byte[]→Data conversion, not raw value
        Assert.Contains("FromByteArray(value)", output);
    }

    [Fact]
    public void CdeclSetterWithStringIndexParam_WithDisposableProjection_EmitsUsing()
    {
        // Regression: Finding 1 — setter with disposable value projection (e.g. string→SwiftString)
        var sw = new StringWriter();
        var csWriter = new CSharpWriter(sw);
        var paramInfos = new List<(string typeName, string paramName, ITypeProjection? projection)>
        {
            ("string", "key", new StringProjection())
        };
        var valueProjection = new StringProjection();

        SubscriptHandler.EmitCdeclSetterWithFixedBlock(
            csWriter, "SetSubscript", "(IntPtr)__keyPtr, __keyUtf8.Length",
            setupLines: new List<string> { "var __keyUtf8 = System.Text.Encoding.UTF8.GetBytes(key);" },
            usingLines: new List<string>(),
            paramInfos, hasStringIndexParam: true,
            valueProjection: valueProjection, isStringValue: false);

        var output = sw.ToString();
        Assert.Contains("using var __val", output);
        Assert.Contains("new SwiftString(value)", output);
    }

    [Fact]
    public void EmitProjectedReturn_NullProjection_EmitsRawReturn()
    {
        var sw = new StringWriter();
        var csWriter = new CSharpWriter(sw);

        SubscriptHandler.EmitProjectedReturn(csWriter, "GetMethod", "arg1, arg2", projection: null);

        var output = sw.ToString();
        Assert.Contains("return GetMethod(arg1, arg2);", output);
    }

    [Fact]
    public void EmitProjectedReturn_WithProjection_EmitsConversion()
    {
        var sw = new StringWriter();
        var csWriter = new CSharpWriter(sw);

        SubscriptHandler.EmitProjectedReturn(csWriter, "GetMethod", "arg1", projection: new DataProjection());

        var output = sw.ToString();
        Assert.Contains(".ToByteArray()", output);
    }

    [Fact]
    public void EmitProjectedSetterCall_NullProjection_EmitsRawCall()
    {
        var sw = new StringWriter();
        var csWriter = new CSharpWriter(sw);

        SubscriptHandler.EmitProjectedSetterCall(csWriter, "SetMethod", "arg1, arg2", valueProjection: null);

        var output = sw.ToString();
        Assert.Contains("SetMethod(value, arg1, arg2);", output);
    }

    [Fact]
    public void EmitProjectedSetterCall_WithProjection_EmitsConversion()
    {
        var sw = new StringWriter();
        var csWriter = new CSharpWriter(sw);

        SubscriptHandler.EmitProjectedSetterCall(csWriter, "SetMethod", "arg1", valueProjection: new DataProjection());

        var output = sw.ToString();
        Assert.Contains("FromByteArray(value)", output);
    }

    [Fact]
    public void EmitProjectedSetterCall_DisposableProjection_EmitsUsing()
    {
        var sw = new StringWriter();
        var csWriter = new CSharpWriter(sw);

        SubscriptHandler.EmitProjectedSetterCall(csWriter, "SetMethod", "arg1", valueProjection: new StringProjection());

        var output = sw.ToString();
        Assert.Contains("using var __val", output);
        Assert.Contains("new SwiftString(value)", output);
    }

    #endregion

    #region Helper Methods

    private static (SwiftWriter swiftWriter, StringWriter sw, SubscriptDecl subscriptDecl, MethodEnvironment env, ModuleEmissionContext ctx) CreateGetterTestSetup(
        TypeSpec returnType, ArgumentDecl[] indexParams, bool isClass, bool isMainActorIsolated = false)
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        TypeDecl parentDecl = isClass
            ? CreateClassDecl("MyType", moduleDecl)
            : CreateStructDecl("MyType", moduleDecl);
        if (isMainActorIsolated)
            parentDecl.IsMainActorIsolated = true;

        // Fix up index params with module decl
        foreach (var p in indexParams)
            p.ModuleDecl ??= moduleDecl;

        var accessor = new GetAccessorDecl { Method = CreateAccessorMethod("getter:subscript", true, parentDecl, moduleDecl) };
        var subscriptDecl = CreateSubscriptDecl(returnType, indexParams, new AccessorDecl[] { accessor }, parentDecl, moduleDecl);

        var env = new MethodEnvironment(accessor.Method, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);

        return (swiftWriter, sw, subscriptDecl, env, ctx);
    }

    private static (SwiftWriter swiftWriter, StringWriter sw, SubscriptDecl subscriptDecl, MethodEnvironment env, ModuleEmissionContext ctx) CreateSetterTestSetup(
        TypeSpec returnType, ArgumentDecl[] indexParams, bool isClass, bool isMainActorIsolated = false)
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        TypeDecl parentDecl = isClass
            ? CreateClassDecl("MyType", moduleDecl)
            : CreateStructDecl("MyType", moduleDecl);
        if (isMainActorIsolated)
            parentDecl.IsMainActorIsolated = true;

        // Fix up index params with module decl
        foreach (var p in indexParams)
            p.ModuleDecl ??= moduleDecl;

        var accessor = new SetAccessorDecl { Method = CreateAccessorMethod("setter:subscript", false, parentDecl, moduleDecl) };
        var subscriptDecl = CreateSubscriptDecl(returnType, indexParams, new AccessorDecl[] { accessor }, parentDecl, moduleDecl);

        var env = new MethodEnvironment(accessor.Method, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);

        return (swiftWriter, sw, subscriptDecl, env, ctx);
    }

    private static SubscriptDecl CreateSubscriptDecl(
        TypeSpec returnType, ArgumentDecl[] indexParams, AccessorDecl[] accessors,
        TypeDecl parentDecl, ModuleDecl moduleDecl, bool isStatic = false)
    {
        return new SubscriptDecl
        {
            Name = "subscript",
            ReturnTypeSpec = returnType,
            IndexParameters = indexParams.ToList(),
            IsStatic = isStatic,
            Accessors = accessors.ToList(),
            MangledName = "$s10TestModule_subscript",
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl
        };
    }

    private static ArgumentDecl CreateIndexParam(string name, TypeSpec typeSpec, ModuleDecl? env)
    {
        return new ArgumentDecl
        {
            Name = name,
            PrivateName = name,
            SwiftTypeSpec = typeSpec,
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = env
        };
    }

    private static MethodDecl CreateAccessorMethod(string name, bool isGetter, TypeDecl parentDecl, ModuleDecl moduleDecl)
    {
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule_accessor_{name}",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            IsAccessor = true,
            CSSignature = new List<ArgumentDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };
    }

    private static StructDecl CreateStructDecl(string name, ModuleDecl moduleDecl)
    {
        var decl = new StructDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}VN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            IsFrozen = true,
            MetadataAccessor = "$sMa"
        };
        moduleDecl.Types.Add(decl);
        return decl;
    }

    private static ClassDecl CreateClassDecl(string name, ModuleDecl moduleDecl)
    {
        var decl = new ClassDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}CN",
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

    private static (ModuleDecl moduleDecl, TypeDatabase typeDb) CreateTestEnvironment(string typeName)
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
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName($"TestModule.{typeName}"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", typeName),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{typeName}"),
                MetadataAccessor = $"$s10TestModule{typeName.Length}{typeName}VMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
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

        return (moduleDecl, typeDb);
    }

    private static (ModuleDecl moduleDecl, TypeDatabase typeDb) CreateTestEnvironmentWithExtraTypes(
        string typeName,
        params (string qualifiedName, TypeRecordFlags flags, TypeRecordKind kind)[] extraTypes)
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
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName($"TestModule.{typeName}"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", typeName),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{typeName}"),
                MetadataAccessor = $"$s10TestModule{typeName.Length}{typeName}VMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });

        foreach (var (qualifiedName, flags, kind) in extraTypes)
        {
            var swiftTypeName = SwiftTypeName.FromModuleQualifiedName(qualifiedName);
            testModule.RegisterType(
                swiftTypeName,
                new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", swiftTypeName.Name),
                    SwiftTypeName = swiftTypeName,
                    MetadataAccessor = $"$s{swiftTypeName.Name}Ma",
                    Flags = flags,
                    Kind = kind
                });
        }

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

    #endregion
}
