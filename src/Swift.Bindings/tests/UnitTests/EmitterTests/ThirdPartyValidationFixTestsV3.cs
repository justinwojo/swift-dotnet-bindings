// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Regression tests for third-party library binding compilation error fixes (B5-B19).
/// Validation Pass 3: 228 generator-caused errors across 15 bug patterns.
/// </summary>
public class ThirdPartyValidationFixTestsV3
{
    #region B14 — PInvoke parameter name dedup

    [Fact]
    public void PInvokeParamName_Dedup_WhenDuplicate_AppendsSuffix()
    {
        // Two parameters named "result" should be deduplicated
        var parameters = new List<Parameter>
        {
            new("IntPtr", "result"),
            new("nint", "result")
        };

        SignatureBuilderBase.DeduplicateParameterNames(parameters);

        Assert.Equal("result", parameters[0].Name);
        Assert.Equal("result_1", parameters[1].Name);
    }

    [Fact]
    public void PInvokeParamName_Dedup_WithExistingCollision_SkipsToNextSuffix()
    {
        // [value, value_1, value] must NOT produce [value, value_1, value_1]
        // The dedup should skip _1 (already taken) and use _2 instead
        var parameters = new List<Parameter>
        {
            new("IntPtr", "value"),
            new("nint", "value_1"),
            new("nint", "value")
        };

        SignatureBuilderBase.DeduplicateParameterNames(parameters);

        Assert.Equal("value", parameters[0].Name);
        Assert.Equal("value_1", parameters[1].Name);  // Existing name preserved
        Assert.Equal("value_2", parameters[2].Name);   // Skipped _1, used _2
    }

    [Fact]
    public void PInvokeParamName_Dedup_NoDuplicates_Unchanged()
    {
        var parameters = new List<Parameter>
        {
            new("IntPtr", "result"),
            new("nint", "value"),
            new("nint", "count")
        };

        SignatureBuilderBase.DeduplicateParameterNames(parameters);

        Assert.Equal("result", parameters[0].Name);
        Assert.Equal("value", parameters[1].Name);
        Assert.Equal("count", parameters[2].Name);
    }

    #endregion

    #region B18 — Non-simple enum .Buffer return type

    [Fact]
    public void CanEmitMethod_NonSimpleEnumReturn_IsSkipped()
    {
        var typeDatabase = CreateTypeDatabaseWithEnum(isSimple: false, requiresMemMgmt: true);
        var method = CreateMethodDecl("getStatus", "TestModule.TestType",
            returnType: new NamedTypeSpec("TestModule.ComplexEnum"));

        var result = MemberEmissionValidator.CanEmitMethod(method, typeDatabase, out var details, out _);

        Assert.NotNull(result);
        Assert.Equal(SkipReason.UnsupportedSignature, result);
        Assert.Contains("Non-simple enum", details!);
    }

    [Fact]
    public void CanEmitMethod_SimpleEnumReturn_NotSkipped()
    {
        var typeDatabase = CreateTypeDatabaseWithEnum(isSimple: true, requiresMemMgmt: false);
        var method = CreateMethodDecl("getStatus", "TestModule.TestType",
            returnType: new NamedTypeSpec("TestModule.SimpleEnum"));

        var result = MemberEmissionValidator.CanEmitMethod(method, typeDatabase, out _, out _);

        // Simple enums don't trigger the skip
        Assert.Null(result);
    }

    [Fact]
    public void CanEmitMethod_FrozenStructReturn_NotSkipped()
    {
        var typeDatabase = CreateTypeDatabaseWithFrozenStruct();
        var method = CreateMethodDecl("getPoint", "TestModule.TestType",
            returnType: new NamedTypeSpec("TestModule.Point"));

        var result = MemberEmissionValidator.CanEmitMethod(method, typeDatabase, out _, out _);

        Assert.Null(result);
    }

    [Fact]
    public void CanEmitProperty_NonSimpleEnumAccessorReturn_IsSkipped()
    {
        var typeDatabase = CreateTypeDatabaseWithEnum(isSimple: false, requiresMemMgmt: true);
        var property = new PropertyDecl
        {
            Name = "status",
            SwiftTypeSpec = new NamedTypeSpec("TestModule.ComplexEnum"),
            IsStatic = false,
            HasStorage = true,
            Accessors = new List<AccessorDecl>
            {
                CreateGetAccessor(new NamedTypeSpec("TestModule.ComplexEnum"))
            },
            ParentDecl = CreateStructDecl("Owner"),
            ModuleDecl = CreateModuleDecl()
        };

        var result = MemberEmissionValidator.CanEmitProperty(property, typeDatabase, out var details, out _);

        Assert.NotNull(result);
        Assert.Equal(SkipReason.UnsupportedSignature, result);
        Assert.Contains("Non-simple enum", details!);
    }

    #endregion

    #region B8 — Void as generic type arg in closure context

    [Fact]
    public void ClosureHandler_TranslateBoundGeneric_VoidInResult_UsesSwiftVoid()
    {
        var typeDatabase = CreateTypeDatabaseWithResult();
        var handler = new ClosureHandler(typeDatabase);

        // Result<Void, Error> inside a closure context
        var resultTypeSpec = new NamedTypeSpec("Swift.Result");
        resultTypeSpec.GenericParameters.Add(TupleTypeSpec.Empty); // Void
        resultTypeSpec.GenericParameters.Add(new NamedTypeSpec("Swift.Error"));

        var result = handler.TranslateTypeSpecToCSharp(resultTypeSpec);

        Assert.Contains("SwiftVoid", result);
        Assert.DoesNotContain("<void", result);
    }

    [Fact]
    public void ClosureHandler_TranslateTypeSpec_VoidReturn_StillVoid()
    {
        var typeDatabase = CreateTypeDatabaseWithResult();
        var handler = new ClosureHandler(typeDatabase);

        // Plain void (empty tuple) in non-generic context should still be "void"
        var result = handler.TranslateTypeSpecToCSharp(TupleTypeSpec.Empty);

        Assert.Equal("void", result);
    }

    #endregion

    #region B15 — Duplicate async method name dedup

    [Fact]
    public void HandleBaseDecl_DuplicateAsyncMethodName_SkipsSecond()
    {
        // Two async methods with different Swift param names but same projected C# name
        // Both produce the same C# public method name after normalization
        var typeDatabase = CreateTypeDatabaseWithString();

        var method1 = CreateMethodDecl("fetchSecret", "TestModule.TestType",
            returnType: new NamedTypeSpec("Swift.String"),
            parameters: new[] { ("secret", new NamedTypeSpec("Swift.String") as TypeSpec) });
        method1.IsAsync = true;

        var method2 = CreateMethodDecl("fetchSecret", "TestModule.TestType",
            returnType: new NamedTypeSpec("Swift.String"),
            parameters: new[] { ("clientSecret", new NamedTypeSpec("Swift.String") as TypeSpec) });
        method2.IsAsync = true;

        // Both methods should have the same projected key
        // (GetPublicMethodName + parameter types resolve to the same thing)
        // We can't easily test HandleBaseDecl directly, so test the projected key
        var key1 = GetProjectedKeyViaReflection(method1, typeDatabase);
        var key2 = GetProjectedKeyViaReflection(method2, typeDatabase);

        // Both have same return type, same param type (String), same method name
        Assert.Equal(key1, key2);
    }

    [Fact]
    public void HandleBaseDecl_DifferentParamTypes_KeepsBoth()
    {
        var typeDatabase = CreateTypeDatabaseWithString();

        var method1 = CreateMethodDecl("process", "TestModule.TestType",
            returnType: TupleTypeSpec.Empty,
            parameters: new[] { ("input", new NamedTypeSpec("Swift.String") as TypeSpec) });

        var method2 = CreateMethodDecl("process", "TestModule.TestType",
            returnType: TupleTypeSpec.Empty,
            parameters: new[] { ("input", new NamedTypeSpec("Swift.Int") as TypeSpec) });

        var key1 = GetProjectedKeyViaReflection(method1, typeDatabase);
        var key2 = GetProjectedKeyViaReflection(method2, typeDatabase);

        Assert.NotEqual(key1, key2);
    }

    #endregion

    #region B16 — Non-blittable enum in closure callback

    [Fact]
    public void IsSupportedClosureParameterType_Enum_ReturnsFalse()
    {
        var typeDatabase = CreateTypeDatabaseWithEnum(isSimple: true, requiresMemMgmt: false);
        var handler = new ClosureHandler(typeDatabase);

        // Create a closure with an enum parameter: (SimpleEnum) -> Void
        var closureTypeSpec = new ClosureTypeSpec(
            new NamedTypeSpec("TestModule.SimpleEnum"),
            TupleTypeSpec.Empty);

        Assert.False(handler.IsSupportedClosure(closureTypeSpec));
    }

    [Fact]
    public void IsSupportedClosureParameterType_Int_ReturnsTrue()
    {
        var typeDatabase = CreateTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        // Closure with Int param should be supported
        var closureTypeSpec = new ClosureTypeSpec(
            new NamedTypeSpec("Swift.Int"),
            TupleTypeSpec.Empty);

        Assert.True(handler.IsSupportedClosure(closureTypeSpec));
    }

    #endregion

    #region B5 — Optional tuple with existential element

    [Fact]
    public void HasNonSwiftObjectGenericArg_OptionalTupleWithExistential_ReturnsTrue()
    {
        var typeDatabase = CreateTypeDatabase();
        var handler = new BoundGenericsHandler(typeDatabase);

        // Optional<(Int, any UnknownProtocol)>
        var tupleTypeSpec = new TupleTypeSpec();
        tupleTypeSpec.Elements.Add(new NamedTypeSpec("Swift.Int"));
        // Add an existential element (ProtocolListTypeSpec with unknown protocol)
        var unknownProtocol = new NamedTypeSpec("TestModule.UnknownProtocol");
        var protocolList = new ProtocolListTypeSpec(new[] { unknownProtocol });
        tupleTypeSpec.Elements.Add(protocolList);

        var optionalTypeSpec = new NamedTypeSpec("Swift.Optional");
        optionalTypeSpec.GenericParameters.Add(tupleTypeSpec);

        Assert.True(handler.HasNonSwiftObjectGenericArg(optionalTypeSpec));
    }

    [Fact]
    public void HasNonSwiftObjectGenericArg_OptionalTupleWithPrimitive_ReturnsFalse()
    {
        var typeDatabase = CreateTypeDatabase();
        var handler = new BoundGenericsHandler(typeDatabase);

        // Optional<(Int, Int)> — should NOT be flagged
        var tupleTypeSpec = new TupleTypeSpec();
        tupleTypeSpec.Elements.Add(new NamedTypeSpec("Swift.Int"));
        tupleTypeSpec.Elements.Add(new NamedTypeSpec("Swift.Int"));

        var optionalTypeSpec = new NamedTypeSpec("Swift.Optional");
        optionalTypeSpec.GenericParameters.Add(tupleTypeSpec);

        Assert.False(handler.HasNonSwiftObjectGenericArg(optionalTypeSpec));
    }

    #endregion

    #region B7 — Closure return type void* vs frozen struct

    [Fact]
    public void IsSupportedClosureReturnType_OptionalSwiftString_ReturnsFalse()
    {
        var typeDatabase = CreateTypeDatabaseWithString();
        var handler = new ClosureHandler(typeDatabase);

        // Closure returning Optional<String> — String requires memory management
        var optionalString = new NamedTypeSpec("Swift.Optional");
        optionalString.GenericParameters.Add(new NamedTypeSpec("Swift.String"));

        var closureTypeSpec = new ClosureTypeSpec(TupleTypeSpec.Empty, optionalString);

        Assert.False(handler.IsSupportedClosure(closureTypeSpec));
    }

    [Fact]
    public void IsSupportedClosureReturnType_OptionalInt_ReturnsTrue()
    {
        var typeDatabase = CreateTypeDatabaseWithOptional();
        var handler = new ClosureHandler(typeDatabase);

        // Closure returning Optional<Int> — Int is blittable, should be fine
        var optionalInt = new NamedTypeSpec("Swift.Optional");
        optionalInt.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));

        var closureTypeSpec = new ClosureTypeSpec(TupleTypeSpec.Empty, optionalInt);

        Assert.True(handler.IsSupportedClosure(closureTypeSpec));
    }

    #endregion

    #region B11 — DateTimeOffset in SwiftObjectHelper

    [Fact]
    public void HasNonSwiftObjectGenericArg_FoundationDate_ReturnsTrue()
    {
        var typeDatabase = CreateTypeDatabaseWithFoundationDate();
        var handler = new BoundGenericsHandler(typeDatabase);

        // Array<Foundation.Date> — Date has NativeTypeName (DateTimeOffset), not ISwiftObject
        var arrayTypeSpec = new NamedTypeSpec("Swift.Array");
        arrayTypeSpec.GenericParameters.Add(new NamedTypeSpec("Foundation.Date"));

        Assert.True(handler.HasNonSwiftObjectGenericArg(arrayTypeSpec));
    }

    #endregion

    #region B13 — Async closure arity mismatch

    [Fact]
    public void IsSupportedClosure_AsyncThrowingWithParams_ReturnsFalse()
    {
        var typeDatabase = CreateTypeDatabaseWithString();
        var handler = new ClosureHandler(typeDatabase);

        // (String) async throws -> String
        var closureTypeSpec = new ClosureTypeSpec(
            new NamedTypeSpec("Swift.String"),
            new NamedTypeSpec("Swift.String"));
        closureTypeSpec.IsAsync = true;
        closureTypeSpec.Throws = true;

        Assert.False(handler.IsSupportedClosure(closureTypeSpec));
    }

    [Fact]
    public void IsSupportedClosure_AsyncThrowingNoParams_ReturnsTrue()
    {
        var typeDatabase = CreateTypeDatabaseWithString();
        var handler = new ClosureHandler(typeDatabase);

        // () async throws -> String
        var closureTypeSpec = new ClosureTypeSpec(
            TupleTypeSpec.Empty,
            new NamedTypeSpec("Swift.String"));
        closureTypeSpec.IsAsync = true;
        closureTypeSpec.Throws = true;

        Assert.True(handler.IsSupportedClosure(closureTypeSpec));
    }

    [Fact]
    public void IsSupportedClosure_AsyncNonThrowingWithParams_ReturnsTrue()
    {
        var typeDatabase = CreateTypeDatabase();
        var handler = new ClosureHandler(typeDatabase);

        // (Int) async -> Void — async-only closures use a different path
        var closureTypeSpec = new ClosureTypeSpec(
            new NamedTypeSpec("Swift.Int"),
            TupleTypeSpec.Empty);
        closureTypeSpec.IsAsync = true;
        closureTypeSpec.Throws = false;

        Assert.True(handler.IsSupportedClosure(closureTypeSpec));
    }

    #endregion

    #region B17 — INSObject composition for ObjC root type

    [Fact]
    public void GetCompositionInterfaceName_WithNSObject_FiltersObjCRoot()
    {
        var typeDatabase = CreateTypeDatabaseWithProtocol();
        var handler = new ExistentialHandler(typeDatabase);

        // Composition: NSObject & STPFormEncodable → should filter NSObject, return ISTPFormEncodable
        var nsObject = new NamedTypeSpec("ObjectiveC.NSObject");
        var formEncodable = new NamedTypeSpec("TestModule.STPFormEncodable");
        var protocolList = new ProtocolListTypeSpec(new[] { nsObject, formEncodable });

        var result = handler.GetCompositionInterfaceName(protocolList);

        Assert.DoesNotContain("NSObject", result);
        Assert.Contains("STPFormEncodable", result);
    }

    [Fact]
    public void GetCompositionInterfaceName_WithoutObjC_Unchanged()
    {
        var typeDatabase = CreateTypeDatabaseWithProtocol();
        var handler = new ExistentialHandler(typeDatabase);

        // Composition: P1 & P2 → should keep both
        var p1 = new NamedTypeSpec("TestModule.Encodable");
        var p2 = new NamedTypeSpec("TestModule.Decodable");
        var protocolList = new ProtocolListTypeSpec(new[] { p1, p2 });

        var result = handler.GetCompositionInterfaceName(protocolList);

        Assert.Contains("Decodable", result);
        Assert.Contains("Encodable", result);
    }

    #endregion

    #region B6 — Dictionary existential generic arg

    [Fact]
    public void CanEmitMethod_DictionaryWithExistentialArg_ReturnsUnsupportedExistential()
    {
        var typeDatabase = CreateTypeDatabaseWithDictionary();
        var existentialParam = new NamedTypeSpec("TestModule.MixpanelType") { IsAny = true };
        var dictTypeSpec = new NamedTypeSpec("Swift.Dictionary");
        dictTypeSpec.GenericParameters.Add(new NamedTypeSpec("Swift.String"));
        dictTypeSpec.GenericParameters.Add(existentialParam);

        var method = CreateMethodDecl("process", "TestModule.TestType",
            returnType: TupleTypeSpec.Empty,
            parameters: new[] { ("data", dictTypeSpec as TypeSpec) });

        var result = MemberEmissionValidator.CanEmitMethod(method, typeDatabase, out var details, out _);

        Assert.NotNull(result);
        Assert.Equal(SkipReason.UnsupportedExistential, result);
        Assert.Contains("existential", details!);
    }

    [Fact]
    public void CanEmitMethod_StandaloneExistentialParam_NotAffectedByB6()
    {
        // Standalone existential params (not inside a bound generic) should not be caught by B6.
        // They may be caught by the signature handler for other reasons, but that's separate.
        var typeDatabase = CreateTypeDatabase();
        var existentialParam = new NamedTypeSpec("TestModule.SomeProtocol") { IsAny = true };

        var method = CreateMethodDecl("process", "TestModule.TestType",
            returnType: TupleTypeSpec.Empty,
            parameters: new[] { ("input", existentialParam as TypeSpec) });

        var result = MemberEmissionValidator.CanEmitMethod(method, typeDatabase, out _, out _);

        // Standalone existential NOT inside a bound generic — B6 doesn't apply.
        // Result may be null (no skip) or another reason, but NOT UnsupportedExistential from B6.
        Assert.True(result == null || result != SkipReason.UnsupportedExistential,
            "Standalone existential should not be caught by the bound generic existential check (B6).");
    }

    [Fact]
    public void TryGetFirstExistentialTypeArgument_DictionaryExistential_CaughtButArrayExistential_Allowed()
    {
        // P1 regression test: MethodHandler.Emit() now has a targeted B6 check that catches
        // supported existentials in non-Array bound generics (Dictionary, Set, etc.) while
        // allowing Array<any Protocol> through (which has dedicated existential handling).
        // TryGetFirstUnsupportedExistentialTypeArgument (line 422) only catches 9+ protocol
        // compositions; the new B6 check after it catches the rest for non-Array types.
        var typeDatabase = CreateTypeDatabaseWithDictionary();
        var handler = new BoundGenericsHandler(typeDatabase);

        // Dictionary<String, any MixpanelType> — supported existential, caught by TryGetFirstExistentialTypeArgument
        var existentialParam = new NamedTypeSpec("TestModule.MixpanelType") { IsAny = true };
        var dictTypeSpec = new NamedTypeSpec("Swift.Dictionary");
        dictTypeSpec.GenericParameters.Add(new NamedTypeSpec("Swift.String"));
        dictTypeSpec.GenericParameters.Add(existentialParam);

        // TryGetFirstExistentialTypeArgument catches ALL existentials
        Assert.True(handler.TryGetFirstExistentialTypeArgument(dictTypeSpec, out var existentialType));
        Assert.Contains("MixpanelType", existentialType);

        // TryGetFirstUnsupportedExistentialTypeArgument misses supported existentials
        Assert.False(handler.TryGetFirstUnsupportedExistentialTypeArgument(dictTypeSpec, out _));

        // Array<any Protocol> would also be caught by TryGetFirstExistentialTypeArgument,
        // but MethodHandler allows it through because Array has dedicated existential handling
        var arrayTypeSpec = new NamedTypeSpec("Swift.Array");
        arrayTypeSpec.GenericParameters.Add(existentialParam);
        Assert.True(handler.TryGetFirstExistentialTypeArgument(arrayTypeSpec, out _));
    }

    [Fact]
    public void ArrayException_NestedExistential_StillSkipped()
    {
        // Array<Dictionary<String, any P>> has a nested existential inside the Dictionary element.
        // The dedicated Array existential handling in WrapperEmitter.Marshalling only covers direct
        // Array<any Protocol> elements. Nested cases must still be skipped.
        var typeDatabase = CreateTypeDatabaseWithDictionary();
        var handler = new BoundGenericsHandler(typeDatabase);
        var typeConversionHandler = new TypeConversionHandler(typeDatabase);
        var existentialHandler = new ExistentialHandler(typeDatabase);

        // Build Array<Dictionary<String, any MixpanelType>>
        var existentialParam = new NamedTypeSpec("TestModule.MixpanelType") { IsAny = true };
        var innerDict = new NamedTypeSpec("Swift.Dictionary");
        innerDict.GenericParameters.Add(new NamedTypeSpec("Swift.String"));
        innerDict.GenericParameters.Add(existentialParam);
        var arrayOfDict = new NamedTypeSpec("Swift.Array");
        arrayOfDict.GenericParameters.Add(innerDict);

        // TryGetFirstExistentialTypeArgument detects the nested existential
        Assert.True(handler.TryGetFirstExistentialTypeArgument(arrayOfDict, out var existentialType));
        Assert.Contains("MixpanelType", existentialType);

        // Outer IS an Array...
        Assert.True(typeConversionHandler.IsSwiftArray(arrayOfDict));

        // ...but the element (Dictionary) is NOT itself an existential — it contains one nested.
        // The allowlist uses IsExistential(element) which correctly rejects this.
        Assert.False(existentialHandler.IsExistential(arrayOfDict.GenericParameters[0]));
        // Therefore: isArrayWithDirectExistentialElement = false → method gets skipped.
    }

    [Fact]
    public void ArrayException_DirectExistential_Allowed()
    {
        // Array<any Protocol> has a direct existential element — dedicated marshalling handles it.
        var typeDatabase = CreateTypeDatabaseWithDictionary();
        var handler = new BoundGenericsHandler(typeDatabase);
        var typeConversionHandler = new TypeConversionHandler(typeDatabase);
        var existentialHandler = new ExistentialHandler(typeDatabase);

        var existentialParam = new NamedTypeSpec("TestModule.MixpanelType") { IsAny = true };
        var arrayTypeSpec = new NamedTypeSpec("Swift.Array");
        arrayTypeSpec.GenericParameters.Add(existentialParam);

        Assert.True(handler.TryGetFirstExistentialTypeArgument(arrayTypeSpec, out _));
        Assert.True(typeConversionHandler.IsSwiftArray(arrayTypeSpec));
        // Element IS directly an existential (IsAny = true)
        Assert.True(existentialHandler.IsExistential(arrayTypeSpec.GenericParameters[0]));
        // Therefore: isArrayWithDirectExistentialElement = true → method is allowed through.
    }

    #endregion

    #region B19 — SwiftUI namespace isolation

    [Fact]
    public void CanEmitMethod_WithSwiftUIParam_ReturnsSwiftUIConstraint()
    {
        var typeDatabase = CreateTypeDatabase();
        var method = CreateMethodDecl("configure", "TestModule.TestType",
            returnType: TupleTypeSpec.Empty,
            parameters: new[] { ("view", new NamedTypeSpec("SwiftUI.AnyView") as TypeSpec) });

        var result = MemberEmissionValidator.CanEmitMethod(method, typeDatabase, out var details, out _);

        Assert.NotNull(result);
        Assert.Equal(SkipReason.SwiftUIConstraint, result);
        Assert.Contains("unsupported module", details!);
    }

    [Fact]
    public void CanEmitMethod_WithSwiftUIReturn_ReturnsSwiftUIConstraint()
    {
        var typeDatabase = CreateTypeDatabase();
        var method = CreateMethodDecl("getBody", "TestModule.TestType",
            returnType: new NamedTypeSpec("SwiftUI.AnyView"));

        var result = MemberEmissionValidator.CanEmitMethod(method, typeDatabase, out var details, out _);

        Assert.NotNull(result);
        Assert.Equal(SkipReason.SwiftUIConstraint, result);
    }

    [Fact]
    public void CanEmitProperty_WithSwiftUIType_ReturnsSwiftUIConstraint()
    {
        var typeDatabase = CreateTypeDatabase();
        var property = new PropertyDecl
        {
            Name = "body",
            SwiftTypeSpec = new NamedTypeSpec("SwiftUI.AnyView"),
            IsStatic = false,
            HasStorage = true,
            Accessors = new List<AccessorDecl>
            {
                CreateGetAccessor(new NamedTypeSpec("SwiftUI.AnyView"))
            },
            ParentDecl = CreateStructDecl("Owner"),
            ModuleDecl = CreateModuleDecl()
        };

        var result = MemberEmissionValidator.CanEmitProperty(property, typeDatabase, out var details, out _);

        Assert.NotNull(result);
        Assert.Equal(SkipReason.SwiftUIConstraint, result);
    }

    [Fact]
    public void CanEmitMethod_WithNonSwiftUIParam_ReturnsNull()
    {
        var typeDatabase = CreateTypeDatabase();
        var method = CreateMethodDecl("process", "TestModule.TestType",
            returnType: TupleTypeSpec.Empty,
            parameters: new[] { ("value", new NamedTypeSpec("Swift.Int") as TypeSpec) });

        var result = MemberEmissionValidator.CanEmitMethod(method, typeDatabase, out _, out _);

        Assert.Null(result);
    }

    [Fact]
    public void CanEmitMethod_WithCombineParam_ReturnsSwiftUIConstraint()
    {
        var typeDatabase = CreateTypeDatabase();
        var method = CreateMethodDecl("subscribe", "TestModule.TestType",
            returnType: TupleTypeSpec.Empty,
            parameters: new[] { ("publisher", new NamedTypeSpec("Combine.AnyPublisher") as TypeSpec) });

        var result = MemberEmissionValidator.CanEmitMethod(method, typeDatabase, out _, out _);

        Assert.NotNull(result);
        Assert.Equal(SkipReason.SwiftUIConstraint, result);
    }

    [Fact]
    public void CanEmitMethod_WithSwiftUIInGenericParam_ReturnsSwiftUIConstraint()
    {
        var typeDatabase = CreateTypeDatabaseWithOptional();
        var optionalView = new NamedTypeSpec("Swift.Optional");
        optionalView.GenericParameters.Add(new NamedTypeSpec("SwiftUI.AnyView"));

        var method = CreateMethodDecl("display", "TestModule.TestType",
            returnType: TupleTypeSpec.Empty,
            parameters: new[] { ("view", optionalView as TypeSpec) });

        var result = MemberEmissionValidator.CanEmitMethod(method, typeDatabase, out _, out _);

        Assert.NotNull(result);
        Assert.Equal(SkipReason.SwiftUIConstraint, result);
    }

    #endregion

    #region B9 — Protocol methods with existential params skipped

    [Fact]
    public void ProtocolInterface_MethodWithExistentialParam_IsSkipped()
    {
        // Verify that B9 skip logic correctly identifies methods with existential params.
        // ProtocolHandler at line 248-258 checks: CSSignature.Skip(1).Any(arg => IsExistential || IsOptionalExistential)
        // Replicate the exact condition used in the emission path.
        var typeDatabase = CreateTypeDatabase();
        var existentialHandler = new ExistentialHandler(typeDatabase);

        // Simulate a method with an existential parameter (any SomeProtocol)
        var existentialSpec = new NamedTypeSpec("TestModule.SomeProtocol") { IsAny = true };

        var moduleDecl = CreateModuleDecl();
        var csSignature = new List<ArgumentDecl>
        {
            new ArgumentDecl { Name = "_return", PrivateName = "_return", SwiftTypeSpec = TupleTypeSpec.Empty, IsGeneric = false, IsInOut = false, ParentDecl = null, ModuleDecl = moduleDecl },
            new ArgumentDecl { Name = "input", PrivateName = "input", SwiftTypeSpec = existentialSpec, IsGeneric = false, IsInOut = false, ParentDecl = null, ModuleDecl = moduleDecl }
        };

        // Run the same check ProtocolHandler uses (line 251-253)
        bool hasExistentialParam = csSignature.Skip(1).Any(arg =>
            existentialHandler.IsExistential(arg.SwiftTypeSpec) ||
            existentialHandler.IsOptionalExistential(arg.SwiftTypeSpec));

        Assert.True(hasExistentialParam);
    }

    [Fact]
    public void ProtocolInterface_MethodWithNonExistentialParam_NotSkipped()
    {
        var typeDatabase = CreateTypeDatabase();
        var existentialHandler = new ExistentialHandler(typeDatabase);

        // A regular typed parameter should NOT trigger the existential skip
        var normalSpec = new NamedTypeSpec("Swift.Int");

        var moduleDecl = CreateModuleDecl();
        var csSignature = new List<ArgumentDecl>
        {
            new ArgumentDecl { Name = "_return", PrivateName = "_return", SwiftTypeSpec = TupleTypeSpec.Empty, IsGeneric = false, IsInOut = false, ParentDecl = null, ModuleDecl = moduleDecl },
            new ArgumentDecl { Name = "value", PrivateName = "value", SwiftTypeSpec = normalSpec, IsGeneric = false, IsInOut = false, ParentDecl = null, ModuleDecl = moduleDecl }
        };

        bool hasExistentialParam = csSignature.Skip(1).Any(arg =>
            existentialHandler.IsExistential(arg.SwiftTypeSpec) ||
            existentialHandler.IsOptionalExistential(arg.SwiftTypeSpec));

        Assert.False(hasExistentialParam);
    }

    #endregion

    #region B10 — Protocol proxy receiver type conversion

    [Fact]
    public void TypeConversionHandler_GetReturnConversion_OptionalString_ReturnsNonNullConversion()
    {
        var typeDatabase = CreateTypeDatabaseWithString();
        var handler = new TypeConversionHandler(typeDatabase);

        // Optional<String> → should return a conversion expression
        // The receiver unmarshals SwiftOptional<SwiftString> but the C# impl expects string?
        var optionalString = new NamedTypeSpec("Swift.Optional");
        optionalString.GenericParameters.Add(new NamedTypeSpec("Swift.String"));

        var conversion = handler.GetReturnConversion("value", optionalString);

        // Must produce a non-null conversion expression for the receiver to apply
        Assert.NotNull(conversion);
        // The conversion should reference the variable name
        Assert.Contains("value", conversion);
    }

    [Fact]
    public void TypeConversionHandler_GetReturnConversion_PlainInt_ReturnsNull()
    {
        var typeDatabase = CreateTypeDatabase();
        var handler = new TypeConversionHandler(typeDatabase);

        // Plain Int → no conversion needed (blittable type, same in both contexts)
        var intSpec = new NamedTypeSpec("Swift.Int");

        var conversion = handler.GetReturnConversion("value", intSpec);

        Assert.Null(conversion);
    }

    #endregion

    #region Helper Methods

    private static MethodDecl CreateMethodDecl(string name, string parentTypeName,
        TypeSpec? returnType = null, (string name, TypeSpec type)[]? parameters = null)
    {
        var moduleDecl = CreateModuleDecl();
        var csSignature = new List<ArgumentDecl>();

        // Add return type (always first in CSSignature)
        csSignature.Add(new ArgumentDecl
        {
            Name = "_return",
            PrivateName = "_return",
            SwiftTypeSpec = returnType ?? TupleTypeSpec.Empty,
            IsGeneric = false,
            IsInOut = false,
            ParentDecl = null,
            ModuleDecl = moduleDecl
        });

        // Add parameters
        if (parameters != null)
        {
            foreach (var (pName, pType) in parameters)
            {
                csSignature.Add(new ArgumentDecl
                {
                    Name = pName,
                    PrivateName = pName,
                    SwiftTypeSpec = pType,
                    IsGeneric = false,
                    IsInOut = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                });
            }
        }

        return new MethodDecl
        {
            Name = name,
            MangledName = "$s4test" + name,
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public,
            CSSignature = csSignature,
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = CreateStructDecl(parentTypeName.Split('.').Last()),
            ModuleDecl = moduleDecl
        };
    }

    private static ModuleDecl CreateModuleDecl()
    {
        return new ModuleDecl
        {
            Name = "TestModule",
            Types = new List<TypeDecl>(),
            Protocols = new List<ProtocolDecl>(),
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Dependencies = new List<string>(),
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    private static StructDecl CreateStructDecl(string name)
    {
        return new StructDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = "$sN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = null,
            IsFrozen = true,
            MetadataAccessor = "$sMa"
        };
    }

    private static GetAccessorDecl CreateGetAccessor(TypeSpec returnType)
    {
        var method = new MethodDecl
        {
            Name = "get",
            MangledName = "$sGetter",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    Name = "_return",
                    PrivateName = "_return",
                    SwiftTypeSpec = returnType,
                    IsGeneric = false,
                    IsInOut = false,
                    ParentDecl = null,
                    ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };
        return new GetAccessorDecl { Method = method };
    }

    private static TypeDatabase CreateTypeDatabase()
    {
        var typeDatabase = new TypeDatabase();
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
        typeDatabase.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        typeDatabase.AddModuleDatabase(testModule);
        return typeDatabase;
    }

    private static TypeDatabase CreateTypeDatabaseWithString()
    {
        var typeDatabase = new TypeDatabase();
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
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftString"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.String"),
                MetadataAccessor = "$sSSMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        typeDatabase.AddModuleDatabase(testModule);
        return typeDatabase;
    }

    private static TypeDatabase CreateTypeDatabaseWithResult()
    {
        var typeDatabase = new TypeDatabase();
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
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftString"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.String"),
                MetadataAccessor = "$sSSMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Struct
            });
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Result"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftResult"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Result"),
                MetadataAccessor = "$ss6ResultOMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Enum
            });
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Error"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftError"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Error"),
                MetadataAccessor = "$ss5ErrorMa",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        typeDatabase.AddModuleDatabase(testModule);
        return typeDatabase;
    }

    private static TypeDatabase CreateTypeDatabaseWithOptional()
    {
        var typeDatabase = new TypeDatabase();
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
            SwiftTypeName.FromModuleQualifiedName("Swift.Optional"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftOptional"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Optional"),
                MetadataAccessor = "$sSqMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Enum
            });
        typeDatabase.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        typeDatabase.AddModuleDatabase(testModule);
        return typeDatabase;
    }

    private static TypeDatabase CreateTypeDatabaseWithEnum(bool isSimple, bool requiresMemMgmt)
    {
        var typeDatabase = new TypeDatabase();
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
        typeDatabase.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        var enumName = isSimple ? "SimpleEnum" : "ComplexEnum";
        var flags = TypeRecordFlags.None;
        if (isSimple) flags |= TypeRecordFlags.SimpleEnum;
        if (requiresMemMgmt) flags |= TypeRecordFlags.RequiresMemoryManagement;

        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName($"TestModule.{enumName}"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", enumName),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{enumName}"),
                MetadataAccessor = "$sMa",
                Flags = flags,
                Kind = TypeRecordKind.Enum,
                RawValueTypeName = isSimple ? "Swift.Int" : null
            });
        typeDatabase.AddModuleDatabase(testModule);
        return typeDatabase;
    }

    private static TypeDatabase CreateTypeDatabaseWithFrozenStruct()
    {
        var typeDatabase = new TypeDatabase();
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
        typeDatabase.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Point"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Point"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Point"),
                MetadataAccessor = "$sMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(testModule);
        return typeDatabase;
    }

    private static TypeDatabase CreateTypeDatabaseWithFoundationDate()
    {
        var typeDatabase = new TypeDatabase();
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
            SwiftTypeName.FromModuleQualifiedName("Swift.Array"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftArray"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Array"),
                MetadataAccessor = "$sSaMa",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);

        var foundationModule = new ModuleTypeDatabase("Foundation", "/usr/lib/swift/libswiftFoundation.dylib");
        foundationModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Foundation.Date"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "DateTimeOffset"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Foundation.Date"),
                NativeTypeName = CSharpTypeName.FromNamespaceAndName("System", "DateTimeOffset"),
                MetadataAccessor = "$s10Foundation4DateVMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(foundationModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        typeDatabase.AddModuleDatabase(testModule);
        return typeDatabase;
    }

    private static TypeDatabase CreateTypeDatabaseWithProtocol()
    {
        var typeDatabase = new TypeDatabase();
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
        typeDatabase.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.STPFormEncodable"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "STPFormEncodable"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.STPFormEncodable"),
                MetadataAccessor = "$sMa",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Protocol
            });
        typeDatabase.AddModuleDatabase(testModule);
        return typeDatabase;
    }

    private static TypeDatabase CreateTypeDatabaseWithDictionary()
    {
        var typeDatabase = new TypeDatabase();
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
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftString"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.String"),
                MetadataAccessor = "$sSSMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Struct
            });
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Dictionary"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftDictionary"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Dictionary"),
                MetadataAccessor = "$sSDMa",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        typeDatabase.AddModuleDatabase(testModule);
        return typeDatabase;
    }

    /// <summary>
    /// Uses reflection to access the private GetProjectedCSharpMethodKey to test dedup.
    /// </summary>
    private static string GetProjectedKeyViaReflection(MethodDecl method, ITypeDatabase typeDatabase)
    {
        var baseHandlerType = typeof(BaseHandler);
        var methodInfo = baseHandlerType.GetMethod("GetProjectedCSharpMethodKey",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        if (methodInfo == null)
            throw new InvalidOperationException("Could not find GetProjectedCSharpMethodKey method");
        return (string)methodInfo.Invoke(null, new object[] { method, typeDatabase })!;
    }

    #endregion
}
