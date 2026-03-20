// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for the generic protocol unification work:
/// - Item 7: Shared GenericProtocolEmitter
/// - Item 8: Centralized NeedsGenericDispatch with MemberKind enum
/// - Item 9: IsInheritedGenericContext applied to Method and Property emitters
/// </summary>
public class GenericProtocolUnificationTests
{
    // ═══════════════════════════════════════════════════════════════════
    // Item 7: GenericProtocolEmitter tests
    // ═══════════════════════════════════════════════════════════════════

    #region GenericProtocolEmitter.GetProtocolName

    [Fact]
    public void GetProtocolName_MethodPrefix_ProducesCorrectFormat()
    {
        var name = GenericProtocolEmitter.GetProtocolName("P", "$s_test_symbol");
        Assert.StartsWith("_SBW_P_", name);
        Assert.Equal(15, name.Length); // _SBW_P_ (7) + 8 hex chars
    }

    [Fact]
    public void GetProtocolName_PropertyGetterPrefix_ProducesCorrectFormat()
    {
        var name = GenericProtocolEmitter.GetProtocolName("PG", "$s_test_symbol");
        Assert.StartsWith("_SBW_PG_", name);
        Assert.Equal(16, name.Length); // _SBW_PG_ (8) + 8 hex chars
    }

    [Fact]
    public void GetProtocolName_ConstructorInitPrefix_ProducesCorrectFormat()
    {
        var name = GenericProtocolEmitter.GetProtocolName("CI", "$s_test_symbol");
        Assert.StartsWith("_SBW_CI_", name);
    }

    [Fact]
    public void GetProtocolName_GenericStaticMethodPrefix_ProducesCorrectFormat()
    {
        var name = GenericProtocolEmitter.GetProtocolName("GSM", "$s_test_symbol");
        Assert.StartsWith("_SBW_GSM_", name);
    }

    [Fact]
    public void GetProtocolName_GenericStaticFactoryPrefix_ProducesCorrectFormat()
    {
        var name = GenericProtocolEmitter.GetProtocolName("GSF", "$s_test_symbol");
        Assert.StartsWith("_SBW_GSF_", name);
    }

    [Fact]
    public void GetProtocolName_SameSymbol_ProducesSameHash()
    {
        var name1 = GenericProtocolEmitter.GetProtocolName("P", "$s_same_symbol");
        var name2 = GenericProtocolEmitter.GetProtocolName("P", "$s_same_symbol");
        Assert.Equal(name1, name2);
    }

    [Fact]
    public void GetProtocolName_DifferentSymbols_ProduceDifferentHashes()
    {
        var name1 = GenericProtocolEmitter.GetProtocolName("P", "$s_symbol_A");
        var name2 = GenericProtocolEmitter.GetProtocolName("P", "$s_symbol_B");
        Assert.NotEqual(name1, name2);
    }

    [Fact]
    public void GetProtocolName_DifferentPrefixes_SameSymbol_ProduceDifferentNames()
    {
        var methodName = GenericProtocolEmitter.GetProtocolName("P", "$s_test");
        var propertyName = GenericProtocolEmitter.GetProtocolName("PG", "$s_test");
        var ctorName = GenericProtocolEmitter.GetProtocolName("CI", "$s_test");
        Assert.NotEqual(methodName, propertyName);
        Assert.NotEqual(propertyName, ctorName);
        Assert.NotEqual(methodName, ctorName);
    }

    #endregion

    #region GenericProtocolEmitter.EmitProtocolAndConformance

    [Fact]
    public void EmitProtocolAndConformance_Method_EmitsCorrectSwift()
    {
        var (sw, writer) = CreateSwiftWriter();
        var protocolName = GenericProtocolEmitter.EmitProtocolAndConformance(
            writer, "P", "$s_test_symbol",
            "func doSomething() -> Int",
            "TestModule.MyClass");

        var output = sw.ToString();
        Assert.Contains($"private protocol {protocolName}", output);
        Assert.Contains("func doSomething() -> Int", output);
        Assert.Contains($"extension TestModule.MyClass: {protocolName}", output);
    }

    [Fact]
    public void EmitProtocolAndConformance_PropertyGetter_EmitsCorrectSwift()
    {
        var (sw, writer) = CreateSwiftWriter();
        var protocolName = GenericProtocolEmitter.EmitProtocolAndConformance(
            writer, "PG", "$s_test_symbol",
            "var name: String { get }",
            "TestModule.MyClass");

        var output = sw.ToString();
        Assert.Contains($"private protocol {protocolName}", output);
        Assert.Contains("var name: String { get }", output);
        Assert.Contains($"extension TestModule.MyClass: {protocolName}", output);
    }

    [Fact]
    public void EmitProtocolAndConformance_WithAnyObjectConstraint_EmitsConstraint()
    {
        var (sw, writer) = CreateSwiftWriter();
        var protocolName = GenericProtocolEmitter.EmitProtocolAndConformance(
            writer, "CI", "$s_test_symbol",
            "init(value: Int)",
            "TestModule.MyClass",
            protocolConstraint: "AnyObject");

        var output = sw.ToString();
        Assert.Contains($"private protocol {protocolName}: AnyObject", output);
        Assert.Contains("init(value: Int)", output);
        Assert.Contains($"extension TestModule.MyClass: {protocolName}", output);
    }

    [Fact]
    public void EmitProtocolAndConformance_WithoutConstraint_NoConstraintClause()
    {
        var (sw, writer) = CreateSwiftWriter();
        GenericProtocolEmitter.EmitProtocolAndConformance(
            writer, "P", "$s_test_symbol",
            "func foo()",
            "TestModule.MyClass");

        var output = sw.ToString();
        // Should NOT contain ": AnyObject" or similar constraint
        Assert.DoesNotContain(": AnyObject", output);
        // Should just be "private protocol _SBW_P_XXX {"
        Assert.Contains("private protocol _SBW_P_", output);
    }

    [Fact]
    public void EmitProtocolAndConformance_ReturnsProtocolName()
    {
        var (_, writer) = CreateSwiftWriter();
        var protocolName = GenericProtocolEmitter.EmitProtocolAndConformance(
            writer, "P", "$s_test_symbol",
            "func foo()",
            "TestModule.MyClass");

        Assert.Equal(GenericProtocolEmitter.GetProtocolName("P", "$s_test_symbol"), protocolName);
    }

    #endregion

    #region GenericProtocolEmitter.BuildPropertyGetterMemberDeclaration

    [Fact]
    public void BuildPropertyGetterMemberDeclaration_SimpleType_FormatsCorrectly()
    {
        var decl = GenericProtocolEmitter.BuildPropertyGetterMemberDeclaration(
            "name", new NamedTypeSpec("Swift.String"));
        // ExistentialBypassEmitter.RenderSwiftTypeSpec strips module prefix for known types
        Assert.Equal("var name: String { get }", decl);
    }

    [Fact]
    public void BuildPropertyGetterMemberDeclaration_IntType_FormatsCorrectly()
    {
        var decl = GenericProtocolEmitter.BuildPropertyGetterMemberDeclaration(
            "count", new NamedTypeSpec("Swift.Int"));
        // ExistentialBypassEmitter.RenderSwiftTypeSpec strips module prefix for known types
        Assert.Equal("var count: Int { get }", decl);
    }

    #endregion

    #region GenericProtocolEmitter.BuildConstructorMemberDeclaration

    [Fact]
    public void BuildConstructorMemberDeclaration_NoParams_FormatsCorrectly()
    {
        var moduleDecl = CreateModuleDecl();
        var method = CreateMethodDecl("init", isConstructor: true, moduleDecl: moduleDecl);
        var decl = GenericProtocolEmitter.BuildConstructorMemberDeclaration(
            method, moduleDecl, isFailable: false, throws: false);
        Assert.Equal("init()", decl);
    }

    [Fact]
    public void BuildConstructorMemberDeclaration_Failable_FormatsCorrectly()
    {
        var moduleDecl = CreateModuleDecl();
        var method = CreateMethodDecl("init", isConstructor: true, moduleDecl: moduleDecl);
        var decl = GenericProtocolEmitter.BuildConstructorMemberDeclaration(
            method, moduleDecl, isFailable: true, throws: false);
        Assert.Equal("init?()", decl);
    }

    [Fact]
    public void BuildConstructorMemberDeclaration_Throwing_FormatsCorrectly()
    {
        var moduleDecl = CreateModuleDecl();
        var method = CreateMethodDecl("init", isConstructor: true, moduleDecl: moduleDecl);
        var decl = GenericProtocolEmitter.BuildConstructorMemberDeclaration(
            method, moduleDecl, isFailable: false, throws: true);
        Assert.Equal("init() throws", decl);
    }

    [Fact]
    public void BuildConstructorMemberDeclaration_FailableAndThrowing_FormatsCorrectly()
    {
        var moduleDecl = CreateModuleDecl();
        var method = CreateMethodDecl("init", isConstructor: true, moduleDecl: moduleDecl);
        var decl = GenericProtocolEmitter.BuildConstructorMemberDeclaration(
            method, moduleDecl, isFailable: true, throws: true);
        Assert.Equal("init?() throws", decl);
    }

    [Fact]
    public void BuildConstructorMemberDeclaration_WithParams_IncludesLabels()
    {
        var moduleDecl = CreateModuleDecl();
        var method = CreateMethodDeclWithParams("init", isConstructor: true, moduleDecl: moduleDecl,
            ("value", new NamedTypeSpec("Swift.Int")),
            ("name", new NamedTypeSpec("Swift.String")));
        var decl = GenericProtocolEmitter.BuildConstructorMemberDeclaration(
            method, moduleDecl, isFailable: false, throws: false);
        // ExistentialBypassEmitter.RenderSwiftTypeSpec strips module prefix for known types
        Assert.Equal("init(value: Int, name: String)", decl);
    }

    [Fact]
    public void BuildConstructorMemberDeclaration_UnlabeledParam_UsesUnderscore()
    {
        var moduleDecl = CreateModuleDecl();
        var method = CreateMethodDeclWithParams("init", isConstructor: true, moduleDecl: moduleDecl,
            ("arg0", new NamedTypeSpec("Swift.Int")));
        var decl = GenericProtocolEmitter.BuildConstructorMemberDeclaration(
            method, moduleDecl, isFailable: false, throws: false);
        // ExistentialBypassEmitter.RenderSwiftTypeSpec strips module prefix for known types
        Assert.Equal("init(_: Int)", decl);
    }

    #endregion

    // ═══════════════════════════════════════════════════════════════════
    // Item 8: NeedsGenericDispatch with MemberKind tests
    // ═══════════════════════════════════════════════════════════════════

    #region MemberKind enum

    [Fact]
    public void MemberKind_HasExpectedValues()
    {
        Assert.Equal(0, (int)MemberKind.Method);
        Assert.Equal(1, (int)MemberKind.Property);
        Assert.Equal(2, (int)MemberKind.Constructor);
    }

    #endregion

    #region NeedsGenericDispatch — non-generic parent

    [Fact]
    public void NeedsGenericDispatch_NonGenericParent_ReturnsFalse_ForMethod()
    {
        var (env, _) = CreateNonGenericMethodEnv();
        Assert.False(WrapperValidation.NeedsGenericDispatch(env, MemberKind.Method));
    }

    [Fact]
    public void NeedsGenericDispatch_NonGenericParent_ReturnsFalse_ForProperty()
    {
        var (env, propertyDecl) = CreateNonGenericPropertyEnv();
        Assert.False(WrapperValidation.NeedsGenericDispatch(env, MemberKind.Property, propertyDecl));
    }

    [Fact]
    public void NeedsGenericDispatch_NonGenericParent_ReturnsFalse_ForConstructor()
    {
        var (env, _) = CreateNonGenericConstructorEnv();
        Assert.False(WrapperValidation.NeedsGenericDispatch(env, MemberKind.Constructor));
    }

    #endregion

    #region NeedsGenericDispatch — generic struct parent

    [Fact]
    public void NeedsGenericDispatch_GenericStruct_Method_WithTInSignature_ReturnsTrue()
    {
        var (env, _) = CreateGenericStructMethodEnv(hasGenericParam: true);
        Assert.True(WrapperValidation.NeedsGenericDispatch(env, MemberKind.Method));
    }

    [Fact]
    public void NeedsGenericDispatch_GenericStruct_Property_ReturnsTrue()
    {
        var (env, propertyDecl) = CreateGenericStructPropertyEnv(propertyReferencesT: false);
        Assert.True(WrapperValidation.NeedsGenericDispatch(env, MemberKind.Property, propertyDecl));
    }

    [Fact]
    public void NeedsGenericDispatch_GenericStruct_Constructor_WithTInParams_ReturnsTrue()
    {
        var (env, _) = CreateGenericStructConstructorEnv(hasGenericParam: true);
        Assert.True(WrapperValidation.NeedsGenericDispatch(env, MemberKind.Constructor));
    }

    #endregion

    #region NeedsGenericDispatch — generic class parent

    [Fact]
    public void NeedsGenericDispatch_GenericClass_Method_ConcreteSignature_ReturnsFalse()
    {
        var (env, _) = CreateGenericClassMethodEnv(hasGenericParam: false);
        Assert.False(WrapperValidation.NeedsGenericDispatch(env, MemberKind.Method));
    }

    [Fact]
    public void NeedsGenericDispatch_GenericClass_Method_WithTInSignature_ReturnsTrue()
    {
        var (env, _) = CreateGenericClassMethodEnv(hasGenericParam: true);
        Assert.True(WrapperValidation.NeedsGenericDispatch(env, MemberKind.Method));
    }

    [Fact]
    public void NeedsGenericDispatch_GenericClass_Property_ConcreteType_ReturnsFalse()
    {
        var (env, propertyDecl) = CreateGenericClassPropertyEnv(propertyReferencesT: false);
        Assert.False(WrapperValidation.NeedsGenericDispatch(env, MemberKind.Property, propertyDecl));
    }

    [Fact]
    public void NeedsGenericDispatch_GenericClass_Property_TTyped_ReturnsTrue()
    {
        var (env, propertyDecl) = CreateGenericClassPropertyEnv(propertyReferencesT: true);
        Assert.True(WrapperValidation.NeedsGenericDispatch(env, MemberKind.Property, propertyDecl));
    }

    [Fact]
    public void NeedsGenericDispatch_GenericClass_Constructor_ConcreteParams_ReturnsFalse()
    {
        var (env, parent) = CreateGenericClassConstructorEnv(hasGenericParam: false);
        // Final generic class with concrete params doesn't need generic dispatch
        ((ClassDecl)parent).IsFinal = true;
        Assert.False(WrapperValidation.NeedsGenericDispatch(env, MemberKind.Constructor));
    }

    [Fact]
    public void NeedsGenericDispatch_GenericClass_Constructor_WithTInParams_ReturnsTrue()
    {
        var (env, _) = CreateGenericClassConstructorEnv(hasGenericParam: true);
        Assert.True(WrapperValidation.NeedsGenericDispatch(env, MemberKind.Constructor));
    }

    #endregion

    #region NeedsGenericDispatch — module parent (non-type parent)

    [Fact]
    public void NeedsGenericDispatch_ModuleParent_ReturnsFalse()
    {
        // Free functions on a module (not a type) are not generic
        var moduleDecl = CreateModuleDecl();
        var method = CreateMethodDecl("test", isConstructor: false, moduleDecl: moduleDecl);
        method.ParentDecl = moduleDecl;
        var env = new MethodEnvironment(method, CreateTypeDatabase());
        Assert.False(WrapperValidation.NeedsGenericDispatch(env, MemberKind.Method));
    }

    #endregion

    // ═══════════════════════════════════════════════════════════════════
    // Item 9: IsInheritedGenericContext applicability tests
    // ═══════════════════════════════════════════════════════════════════

    #region IsInheritedGenericContext — basic behavior

    [Fact]
    public void IsInheritedGenericContext_TopLevelGenericType_ReturnsFalse()
    {
        var moduleDecl = CreateModuleDecl();
        var td = CreateGenericClassDecl("Container", moduleDecl,
            new[] { ("T", "\u03C4_0_0") }); // τ_0_0
        Assert.False(WrapperValidation.IsInheritedGenericContext(td));
    }

    [Fact]
    public void IsInheritedGenericContext_NestedInNonGenericParent_ReturnsFalse()
    {
        var moduleDecl = CreateModuleDecl();
        var outer = CreateClassDecl("Outer", moduleDecl);
        var inner = CreateGenericClassDecl("Inner", moduleDecl,
            new[] { ("T", "\u03C4_0_0") }); // τ_0_0
        inner.ParentDecl = outer;
        Assert.False(WrapperValidation.IsInheritedGenericContext(inner));
    }

    [Fact]
    public void IsInheritedGenericContext_NestedInGenericParent_SameParams_ReturnsTrue()
    {
        var moduleDecl = CreateModuleDecl();
        var outer = CreateGenericClassDecl("AuthInterceptor", moduleDecl,
            new[] { ("A", "\u03C4_0_0") });
        var inner = CreateGenericClassDecl("RefreshWindow", moduleDecl,
            new[] { ("A", "\u03C4_0_0") });
        inner.ParentDecl = outer;

        Assert.True(WrapperValidation.IsInheritedGenericContext(inner));
    }

    [Fact]
    public void IsInheritedGenericContext_NestedInGenericParent_OwnParams_ReturnsFalse()
    {
        var moduleDecl = CreateModuleDecl();
        var outer = CreateGenericClassDecl("Outer", moduleDecl,
            new[] { ("T", "\u03C4_0_0") });
        var inner = CreateGenericClassDecl("Inner", moduleDecl,
            new[] { ("U", "\u03C4_1_0") }); // Own generic param at depth 1
        inner.ParentDecl = outer;

        Assert.False(WrapperValidation.IsInheritedGenericContext(inner));
    }

    #endregion

    #region IsInheritedGenericContext — Method ShouldEmitWrapper integration

    [Fact]
    public void MethodShouldEmitWrapper_InheritedGenericContext_ReturnsFalse()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("TestType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        // Create outer generic class
        var outer = CreateGenericClassDecl("AuthInterceptor", moduleDecl,
            new[] { ("A", "\u03C4_0_0") });
        outer.ParentDecl = moduleDecl;

        // Create inner class that inherits generic context
        var inner = CreateClassDecl("RefreshWindow", moduleDecl);
        inner.GenericParameters = new List<GenericArgumentDecl>
        {
            new GenericArgumentDecl("\u03C4_0_0", "A",
                new List<GenericParameterConformance>(),
                new List<GenericParameterConformance>())
        };
        inner.ParentDecl = outer;

        // Create a method on the inner type
        var method = CreateMethodDecl("process", isConstructor: false, moduleDecl: moduleDecl);
        method.ParentDecl = inner;
        method.MethodType = MethodType.Instance;

        var env = new MethodEnvironment(method, typeDb);
        Assert.False(MethodWrapperEmitter.ShouldEmitWrapper(env),
            "Method on nested type with inherited generic context should not emit wrapper");
    }

    [Fact]
    public void MethodRejectionReason_InheritedGenericContext_ReturnsCorrectReason()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("TestType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var outer = CreateGenericClassDecl("AuthInterceptor", moduleDecl,
            new[] { ("A", "\u03C4_0_0") });
        outer.ParentDecl = moduleDecl;

        var inner = CreateClassDecl("RefreshWindow", moduleDecl);
        inner.GenericParameters = new List<GenericArgumentDecl>
        {
            new GenericArgumentDecl("\u03C4_0_0", "A",
                new List<GenericParameterConformance>(),
                new List<GenericParameterConformance>())
        };
        inner.ParentDecl = outer;

        var method = CreateMethodDecl("process", isConstructor: false, moduleDecl: moduleDecl);
        method.ParentDecl = inner;
        method.MethodType = MethodType.Instance;

        var env = new MethodEnvironment(method, typeDb);
        var reason = WrapperValidation.GetRejectionReason(env);
        Assert.Equal("inherited_generic_context", reason);
    }

    #endregion

    #region IsInheritedGenericContext — Property ShouldEmitWrapper integration

    [Fact]
    public void PropertyShouldEmitWrapper_InheritedGenericContext_ReturnsFalse()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("TestType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var outer = CreateGenericClassDecl("AuthInterceptor", moduleDecl,
            new[] { ("A", "\u03C4_0_0") });
        outer.ParentDecl = moduleDecl;

        var inner = CreateClassDecl("RefreshWindow", moduleDecl);
        inner.GenericParameters = new List<GenericArgumentDecl>
        {
            new GenericArgumentDecl("\u03C4_0_0", "A",
                new List<GenericParameterConformance>(),
                new List<GenericParameterConformance>())
        };
        inner.ParentDecl = outer;

        var getterMethod = CreateAccessorMethod("getter:name", isGetter: true, inner, moduleDecl);
        var propertyDecl = new PropertyDecl
        {
            Name = "name",
            SwiftTypeSpec = new NamedTypeSpec("Swift.String"),
            HasStorage = true,
            IsStatic = false,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl { Method = getterMethod }
            },
            ParentDecl = inner,
            ModuleDecl = moduleDecl
        };

        var env = new MethodEnvironment(getterMethod, typeDb);
        Assert.False(PropertyWrapperEmitter.ShouldEmitWrapper(propertyDecl, env),
            "Property on nested type with inherited generic context should not emit wrapper");
    }

    [Fact]
    public void PropertyRejectionReason_InheritedGenericContext_ReturnsCorrectReason()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("TestType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var outer = CreateGenericClassDecl("AuthInterceptor", moduleDecl,
            new[] { ("A", "\u03C4_0_0") });
        outer.ParentDecl = moduleDecl;

        var inner = CreateClassDecl("RefreshWindow", moduleDecl);
        inner.GenericParameters = new List<GenericArgumentDecl>
        {
            new GenericArgumentDecl("\u03C4_0_0", "A",
                new List<GenericParameterConformance>(),
                new List<GenericParameterConformance>())
        };
        inner.ParentDecl = outer;

        var getterMethod = CreateAccessorMethod("getter:name", isGetter: true, inner, moduleDecl);
        var propertyDecl = new PropertyDecl
        {
            Name = "name",
            SwiftTypeSpec = new NamedTypeSpec("Swift.String"),
            HasStorage = true,
            IsStatic = false,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl { Method = getterMethod }
            },
            ParentDecl = inner,
            ModuleDecl = moduleDecl
        };

        var env = new MethodEnvironment(getterMethod, typeDb);
        var reason = PropertyWrapperEmitter.GetRejectionReason(propertyDecl, env);
        Assert.Equal("inherited_generic_context", reason);
    }

    #endregion

    #region IsInheritedGenericContext — Constructor still protected in RequiresCdeclForAbiSafety

    [Fact]
    public void RequiresCdeclForAbiSafety_Constructor_InheritedGenericContext_ReturnsFalse()
    {
        // Verify the existing constructor protection still works
        var (moduleDecl, typeDb) = CreateTestEnvironment("TestType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var outer = CreateGenericClassDecl("AuthInterceptor", moduleDecl,
            new[] { ("A", "\u03C4_0_0") });
        outer.ParentDecl = moduleDecl;

        var inner = CreateClassDecl("RefreshWindow", moduleDecl);
        inner.GenericParameters = new List<GenericArgumentDecl>
        {
            new GenericArgumentDecl("\u03C4_0_0", "A",
                new List<GenericParameterConformance>(),
                new List<GenericParameterConformance>())
        };
        inner.ParentDecl = outer;

        // Use a regular method (not constructor) to test GetRejectionReason,
        // which rejects constructors at guard 1 before reaching inherited_generic_context.
        var method = CreateMethodDecl("doSomething", isConstructor: false, moduleDecl: moduleDecl);
        method.ParentDecl = inner;
        method.MethodType = MethodType.Instance;

        var env = new MethodEnvironment(method, typeDb);

        // IsInheritedGenericContext should detect the nested type's params come from outer
        Assert.True(WrapperValidation.IsInheritedGenericContext(inner),
            "Nested type with generic params from outer parent should be detected as inherited generic context");

        // GetRejectionReason should block wrapper emission for inherited generic contexts
        string? reason = WrapperValidation.GetRejectionReason(env);
        Assert.Equal("inherited_generic_context", reason);
    }

    [Fact]
    public void RequiresCdeclForAbiSafety_Constructor_TrulyGeneric_ReturnsTrue()
    {
        // Verify that truly generic types DO get @_cdecl required
        var (moduleDecl, typeDb) = CreateTestEnvironment("TestType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var genericClass = CreateGenericClassDecl("Container", moduleDecl,
            new[] { ("T", "\u03C4_0_0") });
        genericClass.ParentDecl = moduleDecl;

        var ctor = CreateMethodDecl("init", isConstructor: true, moduleDecl: moduleDecl);
        ctor.ParentDecl = genericClass;
        ctor.MethodType = MethodType.Instance;

        var env = new MethodEnvironment(ctor, typeDb);
        Assert.True(WrapperValidation.RequiresCdeclForAbiSafety(env),
            "Constructor on truly generic type should require @_cdecl for ABI safety");
    }

    #endregion

    #region Test Helpers

    private static (ModuleDecl moduleDecl, TypeDatabase typeDb) CreateTestEnvironment(string typeName)
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
                Kind = TypeRecordKind.Struct
            });
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.String"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "String"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.String"),
                MetadataAccessor = "$sSSMa",
                Flags = TypeRecordFlags.Frozen,
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

    private static TypeDatabase CreateTypeDatabase()
    {
        var (_, typeDb) = CreateTestEnvironment("TestType");
        return typeDb;
    }

    private static ModuleDecl CreateModuleDecl()
    {
        return new ModuleDecl
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
    }

    private static ClassDecl CreateClassDecl(string name, ModuleDecl moduleDecl)
    {
        return new ClassDecl
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
    }

    private static ClassDecl CreateGenericClassDecl(string name, ModuleDecl moduleDecl,
        (string sugaredName, string abiName)[] genericParams)
    {
        var decl = CreateClassDecl(name, moduleDecl);
        decl.GenericParameters = genericParams.Select(p =>
            new GenericArgumentDecl(p.abiName, p.sugaredName,
                new List<GenericParameterConformance>(),
                new List<GenericParameterConformance>())).ToList();
        return decl;
    }

    private static StructDecl CreateGenericStructDecl(string name, ModuleDecl moduleDecl,
        (string sugaredName, string abiName)[] genericParams)
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
            GenericParameters = genericParams.Select(p =>
                new GenericArgumentDecl(p.abiName, p.sugaredName,
                    new List<GenericParameterConformance>(),
                    new List<GenericParameterConformance>())).ToList(),
            Conformances = new List<TypeConformance>(),
            IsFrozen = true,
            MetadataAccessor = $"$s10TestModule{name.Length}{name}VMa",
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        return decl;
    }

    private static MethodDecl CreateMethodDecl(string name, bool isConstructor, ModuleDecl moduleDecl,
        MethodType methodType = MethodType.Instance)
    {
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule_{name}_{Guid.NewGuid():N}",
            MethodType = methodType,
            IsConstructor = isConstructor,
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
            ParentDecl = null,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
    }

    private static MethodDecl CreateMethodDeclWithParams(string name, bool isConstructor, ModuleDecl moduleDecl,
        params (string paramName, TypeSpec typeSpec)[] parameters)
    {
        var method = CreateMethodDecl(name, isConstructor, moduleDecl);
        foreach (var (paramName, typeSpec) in parameters)
        {
            method.CSSignature.Add(new ArgumentDecl
            {
                SwiftTypeSpec = typeSpec,
                Name = paramName,
                PrivateName = paramName,
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = null,
                ModuleDecl = moduleDecl
            });
        }
        return method;
    }

    private static MethodDecl CreateAccessorMethod(string name, bool isGetter, TypeDecl parentDecl, ModuleDecl moduleDecl)
    {
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule_accessor_{name}_{Guid.NewGuid():N}",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            IsAccessor = true,
            CSSignature = new List<ArgumentDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };
    }

    private static (StringWriter sw, SwiftWriter writer) CreateSwiftWriter()
    {
        var sw = new StringWriter();
        var writer = new SwiftWriter(sw);
        return (sw, writer);
    }

    // ─── Generic dispatch test environment helpers ────────────────────

    private static (MethodEnvironment env, TypeDecl parent) CreateNonGenericMethodEnv()
    {
        var moduleDecl = CreateModuleDecl();
        var parent = CreateClassDecl("NonGeneric", moduleDecl);
        var method = CreateMethodDecl("doWork", isConstructor: false, moduleDecl: moduleDecl);
        method.ParentDecl = parent;
        var typeDb = CreateTypeDatabase();
        return (new MethodEnvironment(method, typeDb), parent);
    }

    private static (MethodEnvironment env, PropertyDecl propertyDecl) CreateNonGenericPropertyEnv()
    {
        var moduleDecl = CreateModuleDecl();
        var parent = CreateClassDecl("NonGeneric", moduleDecl);
        var getterMethod = CreateAccessorMethod("getter:name", true, parent, moduleDecl);
        var propertyDecl = new PropertyDecl
        {
            Name = "name",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            HasStorage = true,
            IsStatic = false,
            Accessors = new List<AccessorDecl> { new GetAccessorDecl { Method = getterMethod } },
            ParentDecl = parent,
            ModuleDecl = moduleDecl
        };
        var typeDb = CreateTypeDatabase();
        return (new MethodEnvironment(getterMethod, typeDb), propertyDecl);
    }

    private static (MethodEnvironment env, TypeDecl parent) CreateNonGenericConstructorEnv()
    {
        var moduleDecl = CreateModuleDecl();
        var parent = CreateClassDecl("NonGeneric", moduleDecl);
        var ctor = CreateMethodDecl("init", isConstructor: true, moduleDecl: moduleDecl);
        ctor.ParentDecl = parent;
        var typeDb = CreateTypeDatabase();
        return (new MethodEnvironment(ctor, typeDb), parent);
    }

    private static (MethodEnvironment env, TypeDecl parent) CreateGenericStructMethodEnv(bool hasGenericParam)
    {
        var moduleDecl = CreateModuleDecl();
        var parent = CreateGenericStructDecl("Wrapper", moduleDecl,
            new[] { ("T", "\u03C4_0_0") });

        MethodDecl method;
        if (hasGenericParam)
        {
            method = CreateMethodDeclWithParams("setValue", isConstructor: false, moduleDecl: moduleDecl,
                ("value", new NamedTypeSpec("\u03C4_0_0")));
        }
        else
        {
            method = CreateMethodDeclWithParams("getCount", isConstructor: false, moduleDecl: moduleDecl,
                ("arg0", new NamedTypeSpec("Swift.Int")));
        }
        method.ParentDecl = parent;
        var typeDb = CreateTypeDatabase();
        return (new MethodEnvironment(method, typeDb), parent);
    }

    private static (MethodEnvironment env, PropertyDecl propertyDecl) CreateGenericStructPropertyEnv(bool propertyReferencesT)
    {
        var moduleDecl = CreateModuleDecl();
        var parent = CreateGenericStructDecl("Wrapper", moduleDecl,
            new[] { ("T", "\u03C4_0_0") });

        var typeSpec = propertyReferencesT
            ? (TypeSpec)new NamedTypeSpec("\u03C4_0_0")
            : new NamedTypeSpec("Swift.Int");
        var getterMethod = CreateAccessorMethod("getter:value", true, parent, moduleDecl);
        var propertyDecl = new PropertyDecl
        {
            Name = "value",
            SwiftTypeSpec = typeSpec,
            HasStorage = true,
            IsStatic = false,
            Accessors = new List<AccessorDecl> { new GetAccessorDecl { Method = getterMethod } },
            ParentDecl = parent,
            ModuleDecl = moduleDecl
        };
        var typeDb = CreateTypeDatabase();
        return (new MethodEnvironment(getterMethod, typeDb), propertyDecl);
    }

    private static (MethodEnvironment env, TypeDecl parent) CreateGenericStructConstructorEnv(bool hasGenericParam)
    {
        var moduleDecl = CreateModuleDecl();
        var parent = CreateGenericStructDecl("Wrapper", moduleDecl,
            new[] { ("T", "\u03C4_0_0") });

        MethodDecl ctor;
        if (hasGenericParam)
        {
            ctor = CreateMethodDeclWithParams("init", isConstructor: true, moduleDecl: moduleDecl,
                ("value", new NamedTypeSpec("\u03C4_0_0")));
        }
        else
        {
            ctor = CreateMethodDecl("init", isConstructor: true, moduleDecl: moduleDecl);
        }
        ctor.ParentDecl = parent;
        var typeDb = CreateTypeDatabase();
        return (new MethodEnvironment(ctor, typeDb), parent);
    }

    private static (MethodEnvironment env, TypeDecl parent) CreateGenericClassMethodEnv(bool hasGenericParam)
    {
        var moduleDecl = CreateModuleDecl();
        var parent = CreateGenericClassDecl("Container", moduleDecl,
            new[] { ("T", "\u03C4_0_0") });

        MethodDecl method;
        if (hasGenericParam)
        {
            method = CreateMethodDeclWithParams("setValue", isConstructor: false, moduleDecl: moduleDecl,
                ("value", new NamedTypeSpec("\u03C4_0_0")));
        }
        else
        {
            method = CreateMethodDeclWithParams("getCount", isConstructor: false, moduleDecl: moduleDecl,
                ("arg0", new NamedTypeSpec("Swift.Int")));
        }
        method.ParentDecl = parent;
        var typeDb = CreateTypeDatabase();
        return (new MethodEnvironment(method, typeDb), parent);
    }

    private static (MethodEnvironment env, PropertyDecl propertyDecl) CreateGenericClassPropertyEnv(bool propertyReferencesT)
    {
        var moduleDecl = CreateModuleDecl();
        var parent = CreateGenericClassDecl("Container", moduleDecl,
            new[] { ("T", "\u03C4_0_0") });

        var typeSpec = propertyReferencesT
            ? (TypeSpec)new NamedTypeSpec("\u03C4_0_0")
            : new NamedTypeSpec("Swift.Int");
        var getterMethod = CreateAccessorMethod("getter:value", true, parent, moduleDecl);
        var propertyDecl = new PropertyDecl
        {
            Name = "value",
            SwiftTypeSpec = typeSpec,
            HasStorage = true,
            IsStatic = false,
            Accessors = new List<AccessorDecl> { new GetAccessorDecl { Method = getterMethod } },
            ParentDecl = parent,
            ModuleDecl = moduleDecl
        };
        var typeDb = CreateTypeDatabase();
        return (new MethodEnvironment(getterMethod, typeDb), propertyDecl);
    }

    private static (MethodEnvironment env, TypeDecl parent) CreateGenericClassConstructorEnv(bool hasGenericParam)
    {
        var moduleDecl = CreateModuleDecl();
        var parent = CreateGenericClassDecl("Container", moduleDecl,
            new[] { ("T", "\u03C4_0_0") });

        MethodDecl ctor;
        if (hasGenericParam)
        {
            ctor = CreateMethodDeclWithParams("init", isConstructor: true, moduleDecl: moduleDecl,
                ("value", new NamedTypeSpec("\u03C4_0_0")));
        }
        else
        {
            ctor = CreateMethodDecl("init", isConstructor: true, moduleDecl: moduleDecl);
        }
        ctor.ParentDecl = parent;
        var typeDb = CreateTypeDatabase();
        return (new MethodEnvironment(ctor, typeDb), parent);
    }

    #endregion
}
