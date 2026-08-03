// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Unit tests for DefaultParameterOverloadEmitter.
/// Validates CountTrailingDefaults, BuildOverloadDecl, and TryEmitOverloads skip guards.
/// </summary>
public class DefaultParameterOverloadEmitterTests
{
    #region CountTrailingDefaults Tests

    [Fact]
    public void CountTrailingDefaults_ZeroParams_ReturnsZero()
    {
        var method = CreateMethodWithArgs();
        Assert.Equal(0, DefaultParameterOverloadEmitter.CountTrailingDefaults(method));
    }

    [Fact]
    public void CountTrailingDefaults_AllDefaults_ReturnsAll()
    {
        var method = CreateMethodWithArgs(
            CreateArg("limit", hasDefault: true),
            CreateArg("offset", hasDefault: true),
            CreateArg("page", hasDefault: true));
        Assert.Equal(3, DefaultParameterOverloadEmitter.CountTrailingDefaults(method));
    }

    [Fact]
    public void CountTrailingDefaults_NonTrailingOnly_ReturnsZero()
    {
        // (query: String = "", page: Int) — default is NOT trailing
        var method = CreateMethodWithArgs(
            CreateArg("query", hasDefault: true),
            CreateArg("page", hasDefault: false));
        Assert.Equal(0, DefaultParameterOverloadEmitter.CountTrailingDefaults(method));
    }

    [Fact]
    public void CountTrailingDefaults_Mixed_ReturnsTrailingCount()
    {
        // (query: String, limit: Int = 10, offset: Int = 0) — 2 trailing defaults
        var method = CreateMethodWithArgs(
            CreateArg("query", hasDefault: false),
            CreateArg("limit", hasDefault: true),
            CreateArg("offset", hasDefault: true));
        Assert.Equal(2, DefaultParameterOverloadEmitter.CountTrailingDefaults(method));
    }

    [Fact]
    public void CountTrailingDefaults_OneTrailing_ReturnsOne()
    {
        var method = CreateMethodWithArgs(
            CreateArg("name", hasDefault: false),
            CreateArg("verbose", hasDefault: true));
        Assert.Equal(1, DefaultParameterOverloadEmitter.CountTrailingDefaults(method));
    }

    #endregion

    #region BuildOverloadDecl Tests

    [Fact]
    public void BuildOverloadDecl_SetsUsesWrapperLibrary()
    {
        var method = CreateMethodWithArgs(
            CreateArg("a", hasDefault: false),
            CreateArg("b", hasDefault: true));

        var overload = DefaultParameterOverloadEmitter.BuildOverloadDecl(method, trimCount: 1);

        Assert.True(overload.UsesWrapperLibrary);
    }

    [Fact]
    public void BuildOverloadDecl_CorrectParamCount()
    {
        // Original: return + 3 params, trim 2 → return + 1 param
        var method = CreateMethodWithArgs(
            CreateArg("query", hasDefault: false),
            CreateArg("limit", hasDefault: true),
            CreateArg("offset", hasDefault: true));

        var overload = DefaultParameterOverloadEmitter.BuildOverloadDecl(method, trimCount: 2);

        // CSSignature[0] is return type, rest are params
        Assert.Equal(2, overload.CSSignature.Count); // return + 1 kept param
        Assert.Equal("query", overload.CSSignature[1].Name);
    }

    [Fact]
    public void BuildOverloadDecl_PreservesOwnershipOnKeptParam()
    {
        // Regression: Ownership is an intrinsic, position-independent property of a parameter.
        // A `consuming` (Owned) parameter that survives into a trimmed default-overload must
        // keep Ownership.Owned — otherwise it reverts to Default and routes off the
        // .move()/MarkConsumed path → double-free.
        // Repro shape: func f(_ resource: consuming TrackedResource, flags: Int = 0).
        var resource = CreateArg("resource", hasDefault: false);
        resource.Ownership = ParameterOwnership.Owned;
        var method = CreateMethodWithArgs(
            resource,
            CreateArg("flags", hasDefault: true));

        // Trim the trailing default; `resource` is kept.
        var overload = DefaultParameterOverloadEmitter.BuildOverloadDecl(method, trimCount: 1);

        Assert.Equal(2, overload.CSSignature.Count); // return + resource
        var keptResource = overload.CSSignature[1];
        Assert.Equal("resource", keptResource.Name);
        Assert.Equal(ParameterOwnership.Owned, keptResource.Ownership);
    }

    [Fact]
    public void BuildOverloadDecl_PreservesBorrowingOwnershipOnKeptParam()
    {
        // Shared (borrowing, +0) must also survive cloning — losing the distinction between
        // Owned and Shared is the exact bug the Ownership field exists to prevent.
        var resource = CreateArg("resource", hasDefault: false);
        resource.Ownership = ParameterOwnership.Shared;
        var method = CreateMethodWithArgs(
            resource,
            CreateArg("flags", hasDefault: true));

        var overload = DefaultParameterOverloadEmitter.BuildOverloadDecl(method, trimCount: 1);

        Assert.Equal(ParameterOwnership.Shared, overload.CSSignature[1].Ownership);
    }

    #endregion

    #region BuildGateReducedDecl Tests

    [Fact]
    public void BuildGateReducedDecl_KeepsOriginalMangledName()
    {
        // Unlike BuildOverloadDecl (which forces a DBW_ silgen-shim symbol), the gate-reduced
        // clone keeps the original silgen MangledName: it is routed back through the normal
        // handler as a fresh primary that calls the Swift declaration directly (Swift supplies
        // the dropped trailing default).
        var method = CreateMethodWithArgs(
            CreateArg("tip", hasDefault: false),
            CreateArg("arrowEdge", hasDefault: true));

        var reduced = DefaultParameterOverloadEmitter.BuildGateReducedDecl(method, dropCount: 1);

        Assert.Equal(method.MangledName, reduced.MangledName);
        // Contrast with BuildOverloadDecl, which forces a _dbw_ silgen-shim symbol.
        Assert.DoesNotContain("_dbw_", reduced.MangledName);
    }

    [Fact]
    public void BuildGateReducedDecl_DoesNotForceWrapperLibrary()
    {
        // BuildOverloadDecl unconditionally sets UsesWrapperLibrary=true; the gate-reduced
        // clone must carry the original's value through unchanged so the handler makes its own
        // @_cdecl wrapper decision (forcing it true would mis-trip ShouldEmitWrapper).
        var method = CreateMethodWithArgs(
            CreateArg("tip", hasDefault: false),
            CreateArg("arrowEdge", hasDefault: true));
        method.UsesWrapperLibrary = false;

        var reduced = DefaultParameterOverloadEmitter.BuildGateReducedDecl(method, dropCount: 1);

        Assert.False(reduced.UsesWrapperLibrary);
    }

    [Fact]
    public void BuildGateReducedDecl_DropsTrailingParams()
    {
        // init(tip:arrowEdge:) → init(tip:): drop the single trailing default.
        var method = CreateMethodWithArgs(
            CreateArg("tip", hasDefault: false),
            CreateArg("arrowEdge", hasDefault: true));

        var reduced = DefaultParameterOverloadEmitter.BuildGateReducedDecl(method, dropCount: 1);

        // CSSignature[0] is the return type; one kept param remains.
        Assert.Equal(2, reduced.CSSignature.Count);
        Assert.Equal("tip", reduced.CSSignature[1].Name);
    }

    [Fact]
    public void BuildGateReducedDecl_PreservesConstructorAndFailability()
    {
        var method = CreateMethodWithArgs(
            CreateArg("tip", hasDefault: false),
            CreateArg("arrowEdge", hasDefault: true));
        method.IsConstructor = true;
        method.IsFailable = true;

        var reduced = DefaultParameterOverloadEmitter.BuildGateReducedDecl(method, dropCount: 1);

        Assert.True(reduced.IsConstructor);
        Assert.True(reduced.IsFailable);
    }

    [Fact]
    public void BuildGateReducedDecl_PreservesOwnershipOnKeptParam()
    {
        // Ownership is an intrinsic, position-independent property; a kept consuming param must
        // not silently revert to Default (same double-free hazard BuildOverloadDecl guards).
        var resource = CreateArg("resource", hasDefault: false);
        resource.Ownership = ParameterOwnership.Owned;
        var method = CreateMethodWithArgs(
            resource,
            CreateArg("flags", hasDefault: true));

        var reduced = DefaultParameterOverloadEmitter.BuildGateReducedDecl(method, dropCount: 1);

        Assert.Equal(ParameterOwnership.Owned, reduced.CSSignature[1].Ownership);
    }

    [Fact]
    public void BuildGateReducedDecl_PreservesOverrideFlag()
    {
        // The reduced decl re-enters emission as a fresh primary; a dropped IsOverride would emit
        // a name-hiding method instead of a C# override, breaking managed polymorphism.
        var method = CreateMethodWithArgs(
            CreateArg("tip", hasDefault: false),
            CreateArg("arrowEdge", hasDefault: true));
        method.IsOverride = true;

        var reduced = DefaultParameterOverloadEmitter.BuildGateReducedDecl(method, dropCount: 1);

        Assert.True(reduced.IsOverride);
    }

    [Fact]
    public void BuildGateReducedDecl_PreservesActorIsolationFlags()
    {
        // A dropped @MainActor flag makes the reduced @_cdecl wrapper omit its actor annotation
        // → Swift "call to main actor-isolated ... in a synchronous nonisolated context" error.
        var method = CreateMethodWithArgs(
            CreateArg("tip", hasDefault: false),
            CreateArg("arrowEdge", hasDefault: true));
        method.IsActorIsolated = true;
        method.IsMainActorIsolated = true;

        var reduced = DefaultParameterOverloadEmitter.BuildGateReducedDecl(method, dropCount: 1);

        Assert.True(reduced.IsActorIsolated);
        Assert.True(reduced.IsMainActorIsolated);
    }

    [Fact]
    public void BuildGateReducedDecl_PreservesSpiProtectedFlag()
    {
        // A dropped @_spi flag lets a non-externally-callable member slip past the rescue's
        // re-validation (which only sees the clone), then fail to link from the wrapper module.
        var method = CreateMethodWithArgs(
            CreateArg("tip", hasDefault: false),
            CreateArg("arrowEdge", hasDefault: true));
        method.IsSpiProtected = true;

        var reduced = DefaultParameterOverloadEmitter.BuildGateReducedDecl(method, dropCount: 1);

        Assert.True(reduced.IsSpiProtected);
    }

    [Fact]
    public void BuildGateReducedDecl_PreservesFinalAndExtensionFlags()
    {
        // IsFinal / IsExtensionMethod drive dispatch (Tj thunk vs direct/static symbol); a wrong
        // value here computes the wrong entry-point symbol on any non-wrapped fallback path.
        var method = CreateMethodWithArgs(
            CreateArg("tip", hasDefault: false),
            CreateArg("arrowEdge", hasDefault: true));
        method.IsFinal = true;
        method.IsExtensionMethod = true;

        var reduced = DefaultParameterOverloadEmitter.BuildGateReducedDecl(method, dropCount: 1);

        Assert.True(reduced.IsFinal);
        Assert.True(reduced.IsExtensionMethod);
    }

    [Fact]
    public void BuildGateReducedDecl_PreservesSelfOwnership()
    {
        // consuming/borrowing self (funcSelfKind) is distinct from per-parameter Ownership; a lost
        // IsConsuming on a ~Copyable parent reverts the wrapper off the move()/MarkConsumed path.
        var method = CreateMethodWithArgs(
            CreateArg("tip", hasDefault: false),
            CreateArg("arrowEdge", hasDefault: true));
        method.IsConsuming = true;

        var reduced = DefaultParameterOverloadEmitter.BuildGateReducedDecl(method, dropCount: 1);

        Assert.True(reduced.IsConsuming);
    }

    [Fact]
    public void BuildGateReducedDecl_PreservesTypedThrows()
    {
        // Typed throws forces a @_cdecl wrapper (swifterror register carries a raw typed value);
        // dropping ThrownErrorType would mis-route error extraction.
        var method = CreateMethodWithArgs(
            CreateArg("tip", hasDefault: false),
            CreateArg("arrowEdge", hasDefault: true));
        method.Throws = true;
        method.ThrownErrorType = new NamedTypeSpec("TestModule.ParseError");

        var reduced = DefaultParameterOverloadEmitter.BuildGateReducedDecl(method, dropCount: 1);

        Assert.NotNull(reduced.ThrownErrorType);
        Assert.True(reduced.HasTypedThrows);
    }

    [Fact]
    public void BuildGateReducedDecl_ResetsEmissionMutableState()
    {
        // The clone re-enters dedup + emission as a fresh primary, so any emission-time state from
        // the source must NOT leak in (it would mis-route the P/Invoke or claim a stale name).
        var method = CreateMethodWithArgs(
            CreateArg("tip", hasDefault: false),
            CreateArg("arrowEdge", hasDefault: true));
        method.MarkEmitted();
        method.EmittedCSharpName = "StaleName";
        method.UsesCdeclMethodWrapper = true;

        var reduced = DefaultParameterOverloadEmitter.BuildGateReducedDecl(method, dropCount: 1);

        Assert.False(reduced.WasEmitted);
        Assert.Null(reduced.EmittedCSharpName);
        Assert.Equal(WrapperStrategy.None, reduced.WrapperStrategy);
    }

    [Fact]
    public void BuildGateReducedDecl_SetsGateReducedOverloadFlag()
    {
        // The clone keeps the full-ABI MangledName but emits fewer args, so it MUST be realized by
        // a @_cdecl wrapper (which fills the dropped defaults), never a native thunk (which would
        // `bl` the full symbol with the dropped param's register uninitialized). The flag forces
        // that: NativeThunkEmitter.ShouldEmitThunk returns false for any decl carrying it.
        var method = CreateMethodWithArgs(
            CreateArg("tip", hasDefault: false),
            CreateArg("arrowEdge", hasDefault: true));
        Assert.False(method.IsGateReducedOverload);

        var reduced = DefaultParameterOverloadEmitter.BuildGateReducedDecl(method, dropCount: 1);

        Assert.True(reduced.IsGateReducedOverload);
    }

    [Fact]
    public void BuildGateReducedDecl_PreservesConstLiteralOnKeptParam()
    {
        // A kept _const param must stay _const: the wrap-required gate consults IsConstLiteral via
        // the @_cdecl eligibility check, which REJECTS _const params (the boundary passes a runtime
        // value to a compile-time-literal parameter). A dropped flag would let the gate accept a
        // candidate the emitter then can't compile.
        var min = CreateArg("min", hasDefault: false);
        min.IsConstLiteral = true;
        var method = CreateMethodWithArgs(
            min,
            CreateArg("arrowEdge", hasDefault: true));

        var reduced = DefaultParameterOverloadEmitter.BuildGateReducedDecl(method, dropCount: 1);

        Assert.True(reduced.CSSignature[1].IsConstLiteral);
    }

    [Fact]
    public void BuildGateReducedDecl_PreservesSubscriptIndexAndDefaultExpressionOnKeptParam()
    {
        // IsUnlabeledSubscriptIndex selects `_` vs the real label; SwiftDefaultExpression is the
        // raw default text. Both are per-parameter parser facts the kept arg must carry over.
        var idx = CreateArg("index0", hasDefault: false);
        idx.IsUnlabeledSubscriptIndex = true;
        var kept = CreateArg("kept", hasDefault: true);
        kept.SwiftDefaultExpression = "7";
        var method = CreateMethodWithArgs(
            idx,
            kept,
            CreateArg("trailing", hasDefault: true));

        var reduced = DefaultParameterOverloadEmitter.BuildGateReducedDecl(method, dropCount: 1);

        Assert.True(reduced.CSSignature[1].IsUnlabeledSubscriptIndex);
        Assert.Equal("7", reduced.CSSignature[2].SwiftDefaultExpression);
    }

    #endregion

    #region TryEmitOverloads Skip Guard Tests

    [Fact]
    public void TryEmitOverloads_GenericParentType_SkipsOverloads()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("GenericContainer");

        var parentDecl = new StructDecl
        {
            Name = "GenericContainer",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.GenericContainer"),
            MangledName = "$s10TestModule16GenericContainerVN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>
            {
                new("T", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
            },
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            IsFrozen = true,
            MetadataAccessor = "$s10TestModule16GenericContainerVMa"
        };

        var method = new MethodDecl
        {
            Name = "process",
            MangledName = "$s10TestModule16GenericContainerV7processSiyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateReturnArg(moduleDecl),
                CreateArg("value", hasDefault: true)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };
        parentDecl.Methods.Add(method);

        var (csOutput, swiftOutput) = EmitOverloads(method, typeDb);

        // Generic parent type → no overloads emitted
        Assert.Empty(csOutput);
        Assert.Empty(swiftOutput);
    }

    [Fact]
    public void TryEmitOverloads_SiblingCollision_SkipsOverload()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("Fetcher");

        var parentDecl = new StructDecl
        {
            Name = "Fetcher",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Fetcher"),
            MangledName = "$s10TestModule7FetcherVN",
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
            MetadataAccessor = "$s10TestModule7FetcherVMa"
        };

        // Existing sibling: fetch(query: Int) — 1 param
        var existingSibling = new MethodDecl
        {
            Name = "fetch",
            MangledName = "$s10TestModule7FetcherV5fetchySiF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateReturnArg(moduleDecl),
                CreateArg("query", hasDefault: false)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };
        parentDecl.Methods.Add(existingSibling);

        // Method with default: fetch(query: Int, limit: Int = 10) — trim=1 would produce fetch(query:)
        // which collides with the existing sibling
        var method = new MethodDecl
        {
            Name = "fetch",
            MangledName = "$s10TestModule7FetcherV5fetchySi_SitF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateReturnArg(moduleDecl),
                CreateArg("query", hasDefault: false),
                CreateArg("limit", hasDefault: true)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };
        parentDecl.Methods.Add(method);

        var (csOutput, swiftOutput) = EmitOverloads(method, typeDb);

        // Sibling collision → overload skipped
        Assert.Empty(csOutput);
        Assert.Empty(swiftOutput);
    }

    [Fact]
    public void TryEmitOverloads_Constructor_NoBackticksInFuncName()
    {
        // Regression: constructors (Name="init") were getting backtick-escaped
        // via ParserNameToSwift, producing `_dbw_`init`_HASH_N` — invalid Swift syntax.
        var (moduleDecl, typeDb) = CreateTestEnvironment("Counter");

        var parentDecl = new StructDecl
        {
            Name = "Counter",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Counter"),
            MangledName = "$s10TestModule7CounterVN",
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
            MetadataAccessor = "$s10TestModule7CounterVMa"
        };

        var method = new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule7CounterVySiSiSitcfC",
            MethodType = MethodType.Instance,
            IsConstructor = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateReturnArg(moduleDecl),
                CreateArg("start", hasDefault: false),
                CreateArg("step", hasDefault: false),
                CreateArg("limit", hasDefault: true)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };
        parentDecl.Methods.Add(method);

        var (_, swiftOutput) = EmitOverloads(method, typeDb);

        // Must contain _dbw_init_ (no backticks)
        Assert.Contains("_dbw_init_", swiftOutput);
        // Must NOT contain backtick-escaped init
        Assert.DoesNotContain("`init`", swiftOutput);
        // Verify it's a valid static func declaration
        Assert.Contains("public static func _dbw_init_", swiftOutput);
    }

    [Fact]
    public void TryEmitOverloads_ProjectedKeyCollision_SkipsOverload()
    {
        // An explicit 1-param overload collides with the trimmed default-param overload
        // after C# projection.
        // find(query: Int, limit: Int = 10) → trimmed to find(query: Int)
        // find(query: Int) → explicit 1-param overload
        // Both produce the same projected C# key → skip the trimmed overload.
        var (moduleDecl, typeDb) = CreateTestEnvironment("SearchService");

        var parentDecl = new StructDecl
        {
            Name = "SearchService",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.SearchService"),
            MangledName = "$s10TestModule13SearchServiceVN",
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
            MetadataAccessor = "$s10TestModule13SearchServiceVMa"
        };

        // Explicit 1-param overload: find(query: Int)
        var explicitOverload = new MethodDecl
        {
            Name = "find",
            MangledName = "$s10TestModule13SearchServiceV4findySSSgSSF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateReturnArg(moduleDecl),
                CreateArg("query", hasDefault: false)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };
        parentDecl.Methods.Add(explicitOverload);

        // Method with default param: find(query: Int, limit: Int = 10)
        // Trimming limit produces find(query: Int) → collides with explicit overload
        var methodWithDefault = new MethodDecl
        {
            Name = "find",
            MangledName = "$s10TestModule13SearchServiceV4findySSSgSS_SitF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateReturnArg(moduleDecl),
                CreateArg("query", hasDefault: false),
                CreateArg("limit", hasDefault: true)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };
        parentDecl.Methods.Add(methodWithDefault);

        var (csOutput, swiftOutput) = EmitOverloads(methodWithDefault, typeDb);

        // Sibling collision with explicit overload → trimmed overload skipped
        Assert.Empty(csOutput);
        Assert.Empty(swiftOutput);
    }

    #endregion

    #region EC-5: Method-Level Generic Skip

    [Fact]
    public void TryEmitOverloads_MethodLevelGeneric_SkipsOverloads()
    {
        // EC-5: Method-level generics produce unresolved τ_0_0 type parameters
        // in wrapper code. TryEmitOverloads must skip these methods entirely.
        var (moduleDecl, typeDb) = CreateTestEnvironment("DataRequest");

        var parentDecl = new StructDecl
        {
            Name = "DataRequest",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.DataRequest"),
            MangledName = "$s10TestModule11DataRequestVN",
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
            MetadataAccessor = "$s10TestModule11DataRequestVMa"
        };

        // Method with method-level generic (τ_0_0 in parameter type)
        var method = new MethodDecl
        {
            Name = "publishResponse",
            MangledName = "$s10TestModule11DataRequestV15publishResponseyx_tF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateReturnArg(moduleDecl),
                new ArgumentDecl
                {
                    Name = "serializer",
                    PrivateName = "serializer",
                    SwiftTypeSpec = new NamedTypeSpec("τ_0_0"),
                    HasDefaultArg = false,
                    IsInOut = false,
                    IsGeneric = true,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                },
                CreateArg("queue", hasDefault: true)
            },
            // Method-level generic parameters — NOT class-level
            GenericParameters = new List<GenericArgumentDecl>
            {
                new("T", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
            },
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };
        parentDecl.Methods.Add(method);

        var (csOutput, swiftOutput) = EmitOverloads(method, typeDb);

        // Method-level generic → no overloads emitted (would produce invalid τ_0_0 in Swift code)
        Assert.Empty(csOutput);
        Assert.Empty(swiftOutput);
    }

    [Fact]
    public void TryEmitOverloads_NonGenericMethod_EmitsOverloads()
    {
        // Verify that non-generic methods with defaults still produce overloads
        var (moduleDecl, typeDb) = CreateTestEnvironment("Processor");

        var parentDecl = new StructDecl
        {
            Name = "Processor",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Processor"),
            MangledName = "$s10TestModule9ProcessorVN",
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
            MetadataAccessor = "$s10TestModule9ProcessorVMa"
        };

        var method = new MethodDecl
        {
            Name = "process",
            MangledName = "$s10TestModule9ProcessorV7processSiyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateReturnArg(moduleDecl),
                CreateArg("value", hasDefault: false),
                CreateArg("limit", hasDefault: true)
            },
            GenericParameters = new List<GenericArgumentDecl>(), // No method-level generics
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };
        parentDecl.Methods.Add(method);

        var (csOutput, swiftOutput) = EmitOverloads(method, typeDb);

        // Non-generic → overloads should be emitted
        Assert.NotEmpty(swiftOutput);
        Assert.Contains("_dbw_process_", swiftOutput);
    }

    #endregion

    #region EC-11: Silgen Function Name Consistency

    [Fact]
    public void GetSilgenFuncName_ProducesConsistentName()
    {
        // EC-11: The silgen function name must be consistent between EmitSwiftWrapper
        // and the @_cdecl dispatch section. GetSilgenFuncName is the single source of truth.
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

        var method = new MethodDecl
        {
            Name = "getFormattedExampleNumber",
            MangledName = "$s14PhoneNumberLib0bC9FormatterV24getFormattedExampleNumberySSSg_tF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateReturnArg(moduleDecl),
                CreateArg("countryCode", hasDefault: false),
                CreateArg("type", hasDefault: true),
                CreateArg("format", hasDefault: true)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };

        // trim=2 (remove type + format, keep countryCode)
        var silgenName_trim2 = DefaultParameterOverloadEmitter.GetSilgenFuncName(method, 2);
        Assert.Contains("_dbw_getFormattedExampleNumber_", silgenName_trim2);
        Assert.EndsWith("_2", silgenName_trim2);

        // trim=1 (remove format, keep countryCode + type)
        var silgenName_trim1 = DefaultParameterOverloadEmitter.GetSilgenFuncName(method, 1);
        Assert.Contains("_dbw_getFormattedExampleNumber_", silgenName_trim1);
        Assert.EndsWith("_1", silgenName_trim1);

        // Different trim values produce different names
        Assert.NotEqual(silgenName_trim1, silgenName_trim2);

        // Same trim value produces same name (idempotent)
        Assert.Equal(silgenName_trim2, DefaultParameterOverloadEmitter.GetSilgenFuncName(method, 2));
    }

    [Fact]
    public void TryEmitOverloads_CdeclOverload_SilgenNameMatchesBetweenWrappers()
    {
        // EC-11: Verify that the @_silgen_name function name in EmitSwiftWrapper
        // matches the silgenTarget passed to MethodWrapperEmitter.EmitSwiftMethodWrapper.
        // If they diverge, the @_cdecl wrapper calls a non-existent function → compile error.
        var typeDb = new TypeDatabase();
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

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
        typeDb.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.FinalFormatter"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "FinalFormatter"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.FinalFormatter"),
                MetadataAccessor = "$s10TestModule14FinalFormatterCMa",
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
            Name = "FinalFormatter",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.FinalFormatter"),
            MangledName = "$s10TestModule14FinalFormatterCN",
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

        // Method with 3 params, 2 trailing defaults → 2 overloads
        var method = new MethodDecl
        {
            Name = "format",
            MangledName = "$s10TestModule14FinalFormatterC6formatySS_S2itF",
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
                CreateArg("value", hasDefault: false),
                CreateArg("precision", hasDefault: true),
                CreateArg("style", hasDefault: true),
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false,
            UsesCdeclMethodWrapper = true,
            UsesWrapperLibrary = true,
        };
        parentDecl.Methods.Add(method);

        var emissionContext = new ModuleEmissionContext();
        var csStringWriter = new StringWriter();
        var swiftStringWriter = new StringWriter();
        var csWriter = new CSharpWriter(csStringWriter);
        var swiftWriter = new SwiftWriter(swiftStringWriter);
        var env = new MethodEnvironment(method, typeDb);
        var logger = NullLogger.Instance;

        DefaultParameterOverloadEmitter.TryEmitOverloads(
            csWriter, swiftWriter, env, logger, emissionContext);

        var swiftOutput = swiftStringWriter.ToString();

        // For each @_silgen_name function emitted, the @_cdecl wrapper must call it by name.
        // Extract all _dbw_ function names from @_silgen_name declarations and @_cdecl call sites.
        var silgenFuncNames = System.Text.RegularExpressions.Regex.Matches(
            swiftOutput, @"func (_dbw_\w+)\(")
            .Cast<System.Text.RegularExpressions.Match>()
            .Select(m => m.Groups[1].Value)
            .ToList();

        // Must have silgen functions for trim=2 and trim=1
        Assert.True(silgenFuncNames.Count >= 2,
            $"Expected at least 2 silgen functions, got {silgenFuncNames.Count}. Output:\n{swiftOutput}");

        // Each _dbw_ function declared must also appear as a call target
        foreach (var funcName in silgenFuncNames)
        {
            // The function should appear as a call: obj.funcName( or TypeName.funcName(
            var callPattern = $".{funcName}(";
            Assert.Contains(callPattern, swiftOutput);
        }

        // The trim suffixes should be _1 and _2 (not _3)
        Assert.Contains(silgenFuncNames, n => n.EndsWith("_1"));
        Assert.Contains(silgenFuncNames, n => n.EndsWith("_2"));
    }

    #endregion

    #region Availability Propagation Tests

    [Fact]
    public void TryEmitOverloads_ConstructorOnAvailableType_EmitsAvailabilityOnExtension()
    {
        // Regression for CryptoKit: SecureEnclave.MLDSA65.PrivateKey is iOS 26+. The
        // @_silgen_name wrapper inside `extension ... {}` must carry @available or the
        // Swift compiler rejects it as "referencing iOS 26 API from older context".
        var (moduleDecl, typeDb) = CreateTestEnvironment("Vault");

        var parentDecl = new StructDecl
        {
            Name = "Vault",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Vault"),
            MangledName = "$s10TestModule5VaultVN",
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
            MetadataAccessor = "$s10TestModule5VaultVMa",
            AvailabilityAnnotations = new List<AvailabilityAnnotation>
            {
                new("iOS", "26.0", null, null, false, false, null, null),
                new("macOS", "26.0", null, null, false, false, null, null),
            }
        };

        var method = new MethodDecl
        {
            Name = "init",
            MangledName = "$s10TestModule5VaultV4nameSSAeA5TokenVSgtcfC",
            MethodType = MethodType.Instance,
            IsConstructor = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateReturnArg(moduleDecl),
                CreateArg("name", hasDefault: false),
                CreateArg("token", hasDefault: true)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };
        parentDecl.Methods.Add(method);

        var (_, swiftOutput) = EmitOverloads(method, typeDb);

        Assert.NotEmpty(swiftOutput);
        Assert.Contains("@available(iOS 26.0, *)", swiftOutput);
        Assert.Contains("@available(macOS 26.0, *)", swiftOutput);
    }

    [Fact]
    public void TryEmitOverloads_MethodOnAvailableType_EmitsAvailabilityOnExtension()
    {
        // Non-constructor methods also go inside extensions. The @available annotation
        // must precede the @_silgen_name attribute inside the extension block.
        var (moduleDecl, typeDb) = CreateTestEnvironment("Hasher");

        var parentDecl = new StructDecl
        {
            Name = "Hasher",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Hasher"),
            MangledName = "$s10TestModule6HasherVN",
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
            MetadataAccessor = "$s10TestModule6HasherVMa",
            AvailabilityAnnotations = new List<AvailabilityAnnotation>
            {
                new("iOS", "17.0", null, null, false, false, null, null),
            }
        };

        var method = new MethodDecl
        {
            Name = "update",
            MangledName = "$s10TestModule6HasherV6updateySiSi_SitF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateReturnArg(moduleDecl),
                CreateArg("length", hasDefault: false),
                CreateArg("padding", hasDefault: true)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };
        parentDecl.Methods.Add(method);

        var (_, swiftOutput) = EmitOverloads(method, typeDb);

        Assert.NotEmpty(swiftOutput);
        Assert.Contains("@available(iOS 17.0, *)", swiftOutput);
        // The availability attribute must precede the `extension` keyword — the
        // extended type name itself is gated by availability, so an inner-function
        // @available arrives too late for the Swift compiler.
        var availIdx = swiftOutput.IndexOf("@available(iOS 17.0, *)");
        var extensionIdx = swiftOutput.IndexOf("extension TestModule.Hasher");
        Assert.True(availIdx >= 0 && extensionIdx >= 0 && availIdx < extensionIdx,
            $"@available must precede the extension line. Output:\n{swiftOutput}");
    }

    [Fact]
    public void TryEmitOverloads_MemberLevelAvailability_OverridesParentInherit()
    {
        // If the method itself has a stricter availability annotation, the strictest
        // floor per platform wins — the looser parent annotation is redundant and gets
        // collapsed. Previously we emitted both lines; that was confusing and masked
        // availability bugs in stacked CSM wrappers (SHA3 conformers losing iOS 26).
        var (moduleDecl, typeDb) = CreateTestEnvironment("Lightweight");

        var parentDecl = new StructDecl
        {
            Name = "Lightweight",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Lightweight"),
            MangledName = "$s10TestModule11LightweightVN",
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
            MetadataAccessor = "$s10TestModule11LightweightVMa",
            AvailabilityAnnotations = new List<AvailabilityAnnotation>
            {
                new("iOS", "15.0", null, null, false, false, null, null),
            }
        };

        var method = new MethodDecl
        {
            Name = "configure",
            MangledName = "$s10TestModule11LightweightV9configureySi_SitF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateReturnArg(moduleDecl),
                CreateArg("value", hasDefault: false),
                CreateArg("fallback", hasDefault: true)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false,
            AvailabilityAnnotations = new List<AvailabilityAnnotation>
            {
                new("iOS", "18.0", null, null, false, false, null, null),
            }
        };
        parentDecl.Methods.Add(method);

        var (_, swiftOutput) = EmitOverloads(method, typeDb);

        Assert.NotEmpty(swiftOutput);
        // Strictest wins: iOS 18.0 (member) is emitted; iOS 15.0 (parent) is redundant and
        // deliberately dropped so stacked annotations don't under-guard the call site.
        Assert.Contains("@available(iOS 18.0, *)", swiftOutput);
        Assert.DoesNotContain("@available(iOS 15.0, *)", swiftOutput);
    }

    #endregion

    #region Helpers

    private static ArgumentDecl CreateArg(string name, bool hasDefault)
    {
        return new ArgumentDecl
        {
            Name = name,
            PrivateName = name,
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            HasDefaultArg = hasDefault,
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    private static ArgumentDecl CreateReturnArg(ModuleDecl moduleDecl)
    {
        return new ArgumentDecl
        {
            Name = "",
            PrivateName = "",
            SwiftTypeSpec = TupleTypeSpec.Empty,
            HasDefaultArg = false,
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = moduleDecl
        };
    }

    /// <summary>
    /// Creates a MethodDecl with the given args as parameters (return type auto-added as void).
    /// </summary>
    private static MethodDecl CreateMethodWithArgs(params ArgumentDecl[] args)
    {
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

        var csSignature = new List<ArgumentDecl>
        {
            CreateReturnArg(moduleDecl)
        };
        csSignature.AddRange(args);

        return new MethodDecl
        {
            Name = "testMethod",
            MangledName = "$s10TestModule10testMethodyyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = csSignature,
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };
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

    private static (string csOutput, string swiftOutput) EmitOverloads(MethodDecl methodDecl, TypeDatabase typeDatabase)
    {
        var csStringWriter = new StringWriter();
        var swiftStringWriter = new StringWriter();
        var csWriter = new CSharpWriter(csStringWriter);
        var swiftWriter = new SwiftWriter(swiftStringWriter);

        var env = new MethodEnvironment(methodDecl, typeDatabase);
        var logger = NullLogger.Instance;

        DefaultParameterOverloadEmitter.TryEmitOverloads(csWriter, swiftWriter, env, logger);

        return (csStringWriter.ToString(), swiftStringWriter.ToString());
    }

    #endregion

    #region EmitDebugParamWrapper Autoclosure Tests

    [Fact]
    public void EmitDebugParamWrapper_AutoclosureParam_InvokedWithParens()
    {
        // Issue N: @autoclosure params in _dbg_* wrappers must be invoked with ()
        // when forwarded to the original method. Without this, Swift complains about
        // "add () to forward '@autoclosure' parameter".
        var (moduleDecl, typeDb) = CreateTestEnvironment("VectorAnimationLogger");
        var parentDecl = new StructDecl
        {
            Name = "VectorAnimationLogger",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.VectorAnimationLogger"),
            MangledName = "$s10TestModule21VectorAnimationLoggerVN",
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
            MetadataAccessor = "$s10TestModule21VectorAnimationLoggerVMa"
        };

        // Create @autoclosure () -> Bool parameter
        var autoclosureType = new ClosureTypeSpec(TupleTypeSpec.Empty, new NamedTypeSpec("Swift.Bool"));
        autoclosureType.Attributes.Add(new TypeSpecAttribute("autoclosure"));
        autoclosureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        // Create debug parameter (file: StaticString = #file)
        var debugParam = new ArgumentDecl
        {
            Name = "file",
            PrivateName = "file",
            SwiftTypeSpec = new NamedTypeSpec("Swift.StaticString"),
            HasDefaultArg = true,
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = moduleDecl
        };

        var method = new MethodDecl
        {
            Name = "assert",
            MangledName = "$s10TestModule21VectorAnimationLoggerV6assertyyXK_SSXKSSzcFtF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateReturnArg(moduleDecl),
                new ArgumentDecl
                {
                    Name = "arg0",
                    PrivateName = "arg0",
                    SwiftTypeSpec = autoclosureType,
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                },
                debugParam
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };
        parentDecl.Methods.Add(method);

        // Emit the debug param wrapper
        var stringWriter = new StringWriter();
        var swiftWriter = new SwiftWriter(stringWriter);
        var env = new MethodEnvironment(method, typeDb);
        DefaultParameterOverloadEmitter.EmitDebugParamWrapper(swiftWriter, env);
        var output = stringWriter.ToString();

        // The autoclosure param should be invoked with () in the call
        Assert.Contains("arg0()", output);
        // The wrapper strips the debug param (file) from the emitted callable surface. The
        // provenance anchor comment names the owning member via its DeclId, which encodes the
        // `file:` label — that comment is metadata, not code, so exclude it from the check.
        foreach (var line in output.Split('\n'))
        {
            if (line.TrimStart().StartsWith("// SBW-ORIGIN:", System.StringComparison.Ordinal))
                continue;
            Assert.DoesNotContain("file:", line);
        }
    }

    #endregion

    #region AllTrailingDefaultsAreCSharpMappable Tests

    [Fact]
    public void AllTrailingDefaultsAreCSharpMappable_AllLiterals_ReturnsTrue()
    {
        var (_, typeDb) = CreateTestEnvironment("Config");
        var method = CreateMethodWithArgs(
            CreateArg("query", hasDefault: false),
            CreateArgWithDefault("limit", "10"),
            CreateArgWithDefault("offset", "0"));
        Assert.True(DefaultParameterOverloadEmitter.AllTrailingDefaultsAreCSharpMappable(method, typeDb));
    }

    [Fact]
    public void AllTrailingDefaultsAreCSharpMappable_BoolLiterals_ReturnsTrue()
    {
        var (_, typeDb) = CreateTestEnvironment("Config");
        var method = CreateMethodWithArgs(
            CreateArgWithDefault("verbose", "true"),
            CreateArgWithDefault("strict", "false"));
        Assert.True(DefaultParameterOverloadEmitter.AllTrailingDefaultsAreCSharpMappable(method, typeDb));
    }

    [Fact]
    public void AllTrailingDefaultsAreCSharpMappable_MixedWithComplex_ReturnsFalse()
    {
        var (_, typeDb) = CreateTestEnvironment("Config");
        var method = CreateMethodWithArgs(
            CreateArg("query", hasDefault: false),
            CreateArgWithDefault("config", "Config()"), // unmappable
            CreateArgWithDefault("limit", "10")); // mappable but gap before
        Assert.False(DefaultParameterOverloadEmitter.AllTrailingDefaultsAreCSharpMappable(method, typeDb));
    }

    [Fact]
    public void AllTrailingDefaultsAreCSharpMappable_NoDefaults_ReturnsFalse()
    {
        var (_, typeDb) = CreateTestEnvironment("Config");
        var method = CreateMethodWithArgs(
            CreateArg("x", hasDefault: false),
            CreateArg("y", hasDefault: false));
        Assert.False(DefaultParameterOverloadEmitter.AllTrailingDefaultsAreCSharpMappable(method, typeDb));
    }

    [Fact]
    public void AllTrailingDefaultsAreCSharpMappable_MissingExpression_ReturnsFalse()
    {
        var (_, typeDb) = CreateTestEnvironment("Config");
        // HasDefaultArg=true but no SwiftDefaultExpression (ABI-only default, no swiftinterface)
        var method = CreateMethodWithArgs(
            CreateArg("x", hasDefault: false),
            CreateArg("y", hasDefault: true));
        Assert.False(DefaultParameterOverloadEmitter.AllTrailingDefaultsAreCSharpMappable(method, typeDb));
    }

    [Fact]
    public void AllTrailingDefaultsAreCSharpMappable_NilDefault_ReturnsTrue()
    {
        var (_, typeDb) = CreateTestEnvironment("Config");
        var optionalArg = CreateArgWithDefault("value", "nil");
        optionalArg.SwiftTypeSpec = new NamedTypeSpec("Swift.Optional");
        ((NamedTypeSpec)optionalArg.SwiftTypeSpec).GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        var method = CreateMethodWithArgs(optionalArg);
        Assert.True(DefaultParameterOverloadEmitter.AllTrailingDefaultsAreCSharpMappable(method, typeDb));
    }

    private static ArgumentDecl CreateArgWithDefault(string name, string swiftDefaultExpr)
    {
        return new ArgumentDecl
        {
            Name = name,
            PrivateName = name,
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            HasDefaultArg = true,
            SwiftDefaultExpression = swiftDefaultExpr,
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    #endregion

    #region All-defaults form (interior defaults)

    [Fact]
    public void BuildOverloadDeclDropping_KeepsParametersOutsideTheDropSet()
    {
        // (a, b = _, c, d = _) with b and d dropped — the two required parameters survive in order.
        var method = CreateMethodWithArgs(
            CreateArg("a", hasDefault: false),
            CreateArg("b", hasDefault: true),
            CreateArg("c", hasDefault: false),
            CreateArg("d", hasDefault: true));

        var overload = DefaultParameterOverloadEmitter.BuildOverloadDeclDropping(
            method.MangledName, method, new[] { 1, 3 });

        var kept = overload.CSSignature.Skip(1).Select(a => a.Name).ToList();
        Assert.Equal(new[] { "a", "c" }, kept);
    }

    [Fact]
    public void BuildOverloadDeclDropping_RoutesThroughTheWrapperLibrary()
    {
        var method = CreateMethodWithArgs(
            CreateArg("a", hasDefault: true),
            CreateArg("b", hasDefault: false));
        method.UsesWrapperLibrary = false;

        var overload = DefaultParameterOverloadEmitter.BuildOverloadDeclDropping(
            method.MangledName, method, new[] { 0 });

        Assert.True(overload.UsesWrapperLibrary);
        Assert.NotEqual(method.MangledName, overload.MangledName);
    }

    [Fact]
    public void BuildOverloadDeclDropping_TrailingDropSet_MatchesTheTrailingTrimBuilder()
    {
        // The two builders must agree wherever their inputs describe the same omission, or a method
        // whose defaults happen to all be trailing would get a second shim under a different symbol.
        var method = CreateMethodWithArgs(
            CreateArg("a", hasDefault: false),
            CreateArg("b", hasDefault: true),
            CreateArg("c", hasDefault: true));

        var trimmed = DefaultParameterOverloadEmitter.BuildOverloadDecl(method.MangledName, method, 2);
        var dropped = DefaultParameterOverloadEmitter.BuildOverloadDeclDropping(
            method.MangledName, method, new[] { 1, 2 });

        Assert.Equal(trimmed.MangledName, dropped.MangledName);
        Assert.Equal(
            trimmed.CSSignature.Skip(1).Select(a => a.Name),
            dropped.CSSignature.Skip(1).Select(a => a.Name));
    }

    [Fact]
    public void BuildOverloadDeclDropping_PreservesOwnershipAndInOutOnKeptParameters()
    {
        // Ownership is an intrinsic property of the parameter: losing `consuming` on a surviving
        // parameter routes it off the move/MarkConsumed path and double-frees.
        var owned = CreateArg("owned", hasDefault: false);
        owned.Ownership = ParameterOwnership.Owned;
        var byRef = CreateArg("byRef", hasDefault: false);
        byRef.IsInOut = true;

        var method = CreateMethodWithArgs(
            CreateArg("skipped", hasDefault: true), owned, byRef);

        var overload = DefaultParameterOverloadEmitter.BuildOverloadDeclDropping(
            method.MangledName, method, new[] { 0 });

        var kept = overload.CSSignature.Skip(1).ToList();
        Assert.Equal(2, kept.Count);
        Assert.Equal(ParameterOwnership.Owned, kept[0].Ownership);
        Assert.True(kept[1].IsInOut);
    }

    [Fact]
    public void RequiredCountFor_TrailingMappableDefaultsBecomeOptional()
    {
        // (query, limit = 10, offset = 0) projects to three C# parameters, two of them optional.
        var (_, typeDb) = CreateTestEnvironment("Config");
        var method = CreateMethodWithArgs(
            CreateArg("query", hasDefault: false),
            CreateArgWithDefault("limit", "10"),
            CreateArgWithDefault("offset", "0"));

        Assert.Equal(1, OverloadAmbiguityGuard.RequiredCountFor(method, typeDb, "Search(string,int,int)"));
    }

    [Fact]
    public void RequiredCountFor_InteriorDefaultIsNotOptional()
    {
        // (a = 1, b) — a C# optional must be trailing, so the interior default stays required.
        var (_, typeDb) = CreateTestEnvironment("Config");
        var method = CreateMethodWithArgs(
            CreateArgWithDefault("a", "1"),
            CreateArg("b", hasDefault: false));

        Assert.Equal(2, OverloadAmbiguityGuard.RequiredCountFor(method, typeDb, "Foo(int,int)"));
    }

    [Fact]
    public void RequiredCountFor_UnmappableDefaultEndsTheOptionalRun()
    {
        // (a, b = Config(), c = 0): `c` maps but `b` does not, so only `c` is optional.
        var (_, typeDb) = CreateTestEnvironment("Config");
        var method = CreateMethodWithArgs(
            CreateArg("a", hasDefault: false),
            CreateArgWithDefault("b", "Config()"),
            CreateArgWithDefault("c", "0"));

        Assert.Equal(2, OverloadAmbiguityGuard.RequiredCountFor(method, typeDb, "Foo(int,int,int)"));
    }

    [Fact]
    public void RequiredCountFor_AsyncAppendsTheCancellationTokenOptional()
    {
        // An async method's emitted signature ends with `CancellationToken cancellationToken = default`,
        // which is one more optional than the Swift parameter list shows.
        var (_, typeDb) = CreateTestEnvironment("Config");
        var method = CreateMethodWithArgs(CreateArg("a", hasDefault: false));
        method.IsAsync = true;

        Assert.Equal(
            1,
            OverloadAmbiguityGuard.RequiredCountFor(
                method, typeDb, "FooAsync(int,System.Threading.CancellationToken)"));
    }

    [Fact]
    public void AllDefaultsOverload_RecordsItsOwnApiManifestEntry()
    {
        // describe(a:b:c:) with `b` interior-defaulted: the primary keeps all three parameters (a C#
        // optional must be trailing), so the two-parameter all-defaults form is a member the main
        // dedup loop never sees and never records. An emitted-but-undocumented member is exactly the
        // api-surface drift the manifest gate exists to catch, so this producer records it itself.
        var (method, env) = CreateClassMethodEnvironment(
            "Formatter", "describe",
            CreateArg("a", hasDefault: false),
            CreateArg("b", hasDefault: true),
            CreateArg("c", hasDefault: false));

        var emissionContext = RunOverloads(env);

        var entry = Assert.Single(
            emissionContext.ApiManifestEntries,
            e => e.Key.StartsWith("Formatter.Describe(", StringComparison.Ordinal));
        Assert.Equal(2, OverloadAmbiguityGuard.ParseKey(entry.Key, 0).ParameterTypes.Count);
        // The manifest maps a signature to the native symbol it dispatches through. The all-defaults
        // form calls its OWN Swift shim, not the original declaration — recording the original's
        // symbol would document two distinct C# members as sharing one entry point.
        Assert.NotEqual(method.MangledName, entry.Value);
    }

    [Fact]
    public void DeclinedOverload_LeavesNoApiManifestEntry()
    {
        // find(a:b:c:) yields candidates taking (a) and (a, b). Another producer has already claimed
        // the one-parameter signature, so that candidate is declined. Every decline path — the
        // exact-signature dedup and the CS0121 set-validity guard — shares one `continue` ahead of
        // the manifest record, so a declined candidate must leave the documented surface untouched:
        // a manifest entry for a member that was never written is the same api-surface drift as an
        // emitted member with no entry.
        var (method, env) = CreateClassMethodEnvironment(
            "Search", "find",
            CreateArg("a", hasDefault: false),
            CreateArgWithDefault("b", "0"),
            CreateArgWithDefault("c", "Config()"));

        var trimmedToOne = DefaultParameterOverloadEmitter.BuildOverloadDecl(
            method.MangledName, method, trimCount: 2);
        var claimedKey = DefaultParameterOverloadEmitter.GetProjectedOverloadKey(
            trimmedToOne, env.TypeDatabase, env.SiblingPropertyNames, env.DisambiguatedNameInput);
        env.EmittedProjectedSignatures!.Add(claimedKey);
        OverloadAmbiguityGuard.RecordReservation(
            env.ReservedOverloadShapes, claimedKey,
            OverloadAmbiguityGuard.RequiredCountFor(trimmedToOne, env.TypeDatabase, claimedKey));

        var emissionContext = RunOverloads(env);

        var survivor = Assert.Single(
            emissionContext.ApiManifestEntries.Keys,
            k => k.StartsWith("Search.Find(", StringComparison.Ordinal));
        Assert.Equal(2, OverloadAmbiguityGuard.ParseKey(survivor, 0).ParameterTypes.Count);
    }

    [Fact]
    public void TrimmedOverload_CarriesNoCSharpOptionalTail()
    {
        // The trimmed clone deliberately does NOT carry the kept parameters' Swift default
        // expressions forward, and this producer's whole overload ladder depends on that: emitting
        // (a, b = 0) alongside (a) would make the one-argument call bind both equally well — CS0121
        // in a set this producer built by itself. Copying the expression into the clone would put
        // the set-validity guard in the position of silently declining one of its own rungs.
        var (_, env) = CreateClassMethodEnvironment(
            "Search", "find",
            CreateArg("a", hasDefault: false),
            CreateArgWithDefault("b", "0"),
            CreateArgWithDefault("c", "Config()"));

        var trimmed = DefaultParameterOverloadEmitter.BuildOverloadDecl(
            env.MethodDecl.MangledName, env.MethodDecl, trimCount: 1);
        var key = DefaultParameterOverloadEmitter.GetProjectedOverloadKey(
            trimmed, env.TypeDatabase, env.SiblingPropertyNames, env.DisambiguatedNameInput);

        Assert.Equal(
            OverloadAmbiguityGuard.ParseKey(key, 0).ParameterTypes.Count,
            OverloadAmbiguityGuard.RequiredCountFor(trimmed, env.TypeDatabase, key));
    }

    [Fact]
    public void AmbiguousReservation_DeclinesTheCandidateAndRecordsTheSuppression()
    {
        // The CS0121 branch at the emitter layer, as distinct from the exact-key dedup: the reserved
        // key is a DIFFERENT signature that one argument list binds just as well. Async is what makes
        // this reachable at all — a trimmed clone carries no C# optional tail, so only the appended
        // `CancellationToken = default` gives a candidate an optional parameter to tie on. The
        // candidate `ResolveAsync(nint, CT = default)` and an already-emitted sibling
        // `ResolveAsync(nint, nint = …, CT = default)` are both bound by a one-argument call with
        // neither better. The candidate must be declined AND the suppression recorded — an unreported
        // decline is a member that vanished from the surface with no audit trail.
        var (method, env) = CreateClassMethodEnvironment(
            "Lattice", "resolve",
            CreateArg("id", hasDefault: false),
            CreateArgWithDefault("tag", "Config()"));
        method.IsAsync = true;

        // Spliced from the candidate's own key rather than written out, so the test cannot drift from
        // whatever namespace, name shaping and CancellationToken spelling the key builder produces.
        var candidateDecl = DefaultParameterOverloadEmitter.BuildOverloadDecl(
            method.MangledName, method, trimCount: 1);
        var candidateKey = DefaultParameterOverloadEmitter.GetProjectedOverloadKey(
            candidateDecl, env.TypeDatabase, env.SiblingPropertyNames, env.DisambiguatedNameInput);
        var reserved = candidateKey.Replace("(", "(nint,", StringComparison.Ordinal);
        env.EmittedProjectedSignatures!.Add(reserved);
        OverloadAmbiguityGuard.RecordReservation(env.ReservedOverloadShapes, reserved, requiredCount: 1);

        var emissionContext = RunOverloads(env);

        Assert.DoesNotContain(
            emissionContext.ApiManifestEntries.Keys,
            k => k.StartsWith("Lattice.Resolve", StringComparison.Ordinal));
        var suppressed = Assert.Single(emissionContext.SuppressedAmbiguousOverloads);
        Assert.Contains(reserved, suppressed);
    }

    [Fact]
    public void AllDefaultsOverload_DeclinedWhenASiblingAcceptsTheReducedCall()
    {
        // Two declarations of `describe` differing ONLY in the type of a defaulted parameter. The
        // all-defaults form omits that parameter, and the call left behind — describe(a:c:) — fits
        // both declarations exactly, which swiftc rejects as an ambiguous use. This is not one lost
        // member: the wrapper library compiles as a unit, so the ambiguous shim takes every binding
        // in the module down with it.
        var (method, env) = CreateClassMethodEnvironment(
            "Log", "describe",
            CreateArg("a", hasDefault: false),
            CreateArgWithDefault("b", "Config()"),
            CreateArg("c", hasDefault: false));

        AddSibling(
            method,
            CreateArg("a", hasDefault: false),
            WithSwiftType(CreateArgWithDefault("b", "\"\""), "Swift.String"),
            CreateArg("c", hasDefault: false));

        var emissionContext = RunOverloads(env);

        Assert.DoesNotContain(
            emissionContext.ApiManifestEntries.Keys,
            k => k.StartsWith("Log.Describe(", StringComparison.Ordinal));
    }

    [Fact]
    public void AllDefaultsOverload_SurvivesWhenTheSiblingStillNeedsAnOmittedArgument()
    {
        // Same base name, but the sibling's second parameter is REQUIRED. The reduced call supplies
        // nothing for it, so only one declaration accepts the call and Swift resolves it. Declining
        // here would drop a member over a sibling that was never a candidate.
        var (method, env) = CreateClassMethodEnvironment(
            "Log", "describe",
            CreateArg("a", hasDefault: false),
            CreateArgWithDefault("b", "Config()"),
            CreateArg("c", hasDefault: false));

        AddSibling(
            method,
            CreateArg("a", hasDefault: false),
            WithSwiftType(CreateArg("b", hasDefault: false), "Swift.String"),
            CreateArg("c", hasDefault: false));

        var emissionContext = RunOverloads(env);

        var entry = Assert.Single(
            emissionContext.ApiManifestEntries.Keys,
            k => k.StartsWith("Log.Describe(", StringComparison.Ordinal));
        Assert.Equal(2, OverloadAmbiguityGuard.ParseKey(entry, 0).ParameterTypes.Count);
    }

    [Fact]
    public void AllDefaultsOverload_SurvivesWhenASiblingDiffersOnAKeptParameterType()
    {
        // The sibling defaults the same parameter, so it accepts a call of the same LENGTH — but the
        // kept argument `a` has a different type in each declaration, and that is precisely what lets
        // Swift tell them apart. Treating same-shape as ambiguous would decline on arity alone and
        // lose the member for every overloaded family in a library.
        var (method, env) = CreateClassMethodEnvironment(
            "Log", "describe",
            CreateArg("a", hasDefault: false),
            CreateArgWithDefault("b", "Config()"),
            CreateArg("c", hasDefault: false));

        AddSibling(
            method,
            WithSwiftType(CreateArg("a", hasDefault: false), "Swift.String"),
            CreateArgWithDefault("b", "Config()"),
            CreateArg("c", hasDefault: false));

        var emissionContext = RunOverloads(env);

        var entry = Assert.Single(
            emissionContext.ApiManifestEntries.Keys,
            k => k.StartsWith("Log.Describe(", StringComparison.Ordinal));
        Assert.Equal(2, OverloadAmbiguityGuard.ParseKey(entry, 0).ParameterTypes.Count);
    }

    /// <summary>
    /// Adds a second declaration of <paramref name="primary"/>'s Swift name to the same parent, so
    /// the emitter sees the overload family a real module would present.
    /// </summary>
    private static void AddSibling(MethodDecl primary, params ArgumentDecl[] args)
    {
        var csSignature = new List<ArgumentDecl> { CreateReturnArg(primary.ModuleDecl!) };
        csSignature.AddRange(args);

        var sibling = new MethodDecl
        {
            Name = primary.Name,
            MangledName = primary.MangledName + "_sibling",
            MethodType = primary.MethodType,
            IsConstructor = primary.IsConstructor,
            CSSignature = csSignature,
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = primary.ParentDecl,
            ModuleDecl = primary.ModuleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false,
        };

        ((TypeDecl)primary.ParentDecl!).Methods.Add(sibling);
    }

    private static ArgumentDecl WithSwiftType(ArgumentDecl arg, string swiftTypeName)
    {
        arg.SwiftTypeSpec = new NamedTypeSpec(swiftTypeName);
        return arg;
    }

    /// <summary>
    /// Builds a final class <c>TestModule.{typeName}</c> carrying one instance method with
    /// <paramref name="args"/>, and returns it alongside an environment wired with the dedup and
    /// reservation tables the real emission loop threads through (without them the producer records
    /// nothing, which would make an API-manifest assertion vacuously pass).
    /// </summary>
    private static (MethodDecl method, MethodEnvironment env) CreateClassMethodEnvironment(
        string typeName, string methodName, params ArgumentDecl[] args)
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
        typeDb.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName($"TestModule.{typeName}"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", typeName),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{typeName}"),
                MetadataAccessor = $"$s10TestModule{typeName.Length}{typeName}CMa",
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
            Name = typeName,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{typeName}"),
            MangledName = $"$s10TestModule{typeName.Length}{typeName}CN",
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

        var csSignature = new List<ArgumentDecl> { CreateReturnArg(moduleDecl) };
        csSignature.AddRange(args);

        var method = new MethodDecl
        {
            Name = methodName,
            MangledName = $"$s10TestModule{typeName.Length}{typeName}C{methodName.Length}{methodName}yyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = csSignature,
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false,
            UsesCdeclMethodWrapper = true,
            UsesWrapperLibrary = true,
        };
        parentDecl.Methods.Add(method);

        var env = new MethodEnvironment(method, typeDb)
        {
            EmittedProjectedSignatures = new HashSet<string>(StringComparer.Ordinal),
            ReservedOverloadShapes = new Dictionary<string, int>(StringComparer.Ordinal),
        };

        return (method, env);
    }

    private static ModuleEmissionContext RunOverloads(MethodEnvironment env)
    {
        var emissionContext = new ModuleEmissionContext();
        var csWriter = new CSharpWriter(new StringWriter());
        var swiftWriter = new SwiftWriter(new StringWriter());

        DefaultParameterOverloadEmitter.TryEmitOverloads(
            csWriter, swiftWriter, env, NullLogger.Instance, emissionContext);

        return emissionContext;
    }

    #endregion

    #region @_cdecl Method Wrapper Inheritance

    [Fact]
    public void BuildOverloadDecl_SetsUsesWrapperLibrary_True()
    {
        // BuildOverloadDecl unconditionally sets UsesWrapperLibrary = true.
        // This is expected — overloads always go through the wrapper library.
        var method = CreateMethodWithArgs(
            CreateArg("a", hasDefault: false),
            CreateArg("b", hasDefault: true));
        method.UsesWrapperLibrary = false;

        var overload = DefaultParameterOverloadEmitter.BuildOverloadDecl(method, 1);

        Assert.True(overload.UsesWrapperLibrary);
    }

    [Fact]
    public void OverloadCdeclCheck_UsesOriginalMethod_NotOverload()
    {
        // The overload emitter should check the ORIGINAL method's UsesCdeclMethodWrapper
        // flag, not the overload's. BuildOverloadDecl sets UsesWrapperLibrary=true on
        // overloads, which would cause ShouldEmitWrapper to return false if called on
        // the overload directly.
        var method = CreateMethodWithArgs(
            CreateArg("a", hasDefault: false),
            CreateArg("b", hasDefault: true));
        method.UsesCdeclMethodWrapper = true;

        var overload = DefaultParameterOverloadEmitter.BuildOverloadDecl(method, 1);

        // Overload has UsesWrapperLibrary=true, so ShouldEmitWrapper would reject it
        Assert.True(overload.UsesWrapperLibrary);
        Assert.False(overload.UsesCdeclMethodWrapper); // Not set yet (set by overload emitter)

        // But original method has the flag — the overload emitter should check this
        Assert.True(method.UsesCdeclMethodWrapper);
    }

    [Fact]
    public void TryEmitOverloads_MethodWithCdecl_EmitsBothSilgenAndCdeclWrappers()
    {
        // Full integration: a class instance method with UsesCdeclMethodWrapper=true
        // and a trailing default param should produce:
        //   1. A @_silgen_name Swift wrapper (calls original method with fewer args)
        //   2. A @_cdecl Swift wrapper on top (calls the @_silgen_name function)
        //   3. C# P/Invoke routed through the @_cdecl symbol
        // Build TypeDatabase with class type registered
        var typeDb = new TypeDatabase();
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

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
        typeDb.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.FinalCounter"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "FinalCounter"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.FinalCounter"),
                MetadataAccessor = "$s10TestModule12FinalCounterCMa",
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
            Name = "FinalCounter",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.FinalCounter"),
            MangledName = "$s10TestModule12FinalCounterCN",
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

        var method = new MethodDecl
        {
            Name = "add",
            MangledName = "$s10TestModule12FinalCounterC3add6amount2bySi_SitF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                // Return: Swift.Int
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
                CreateArg("amount", hasDefault: false),
                CreateArg("by", hasDefault: true),
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false,
            // Simulate that MethodHandler already set this flag on the primary method
            UsesCdeclMethodWrapper = true,
            UsesWrapperLibrary = true,
        };
        parentDecl.Methods.Add(method);

        var emissionContext = new ModuleEmissionContext();
        var csStringWriter = new StringWriter();
        var swiftStringWriter = new StringWriter();
        var csWriter = new CSharpWriter(csStringWriter);
        var swiftWriter = new SwiftWriter(swiftStringWriter);
        var env = new MethodEnvironment(method, typeDb);
        var logger = NullLogger.Instance;

        DefaultParameterOverloadEmitter.TryEmitOverloads(
            csWriter, swiftWriter, env, logger, emissionContext);

        var swiftOutput = swiftStringWriter.ToString();
        var csOutput = csStringWriter.ToString();

        // 1. Must have @_silgen_name wrapper (calls original Swift method with default)
        Assert.Contains("@_silgen_name", swiftOutput);
        Assert.Contains("_dbw_add_", swiftOutput);

        // 2. Must have @_cdecl wrapper on top of the @_silgen_name function
        Assert.Contains("@_cdecl", swiftOutput);
        Assert.Contains("SBW_TestModule_FinalCounter_add_", swiftOutput);

        // 3. The @_cdecl wrapper must call the @_silgen_name function (not the original method)
        // The silgen function name follows the pattern _dbw_{methodName}_{hash}_{trimCount}
        Assert.Matches(@"_dbw_add_\w+_1", swiftOutput);

        // 4. C# output must have a P/Invoke with the @_cdecl symbol as entry point
        Assert.Contains("SBW_TestModule_FinalCounter_add_", csOutput);
        Assert.Contains("LibraryImport", csOutput);
    }

    [Fact]
    public void TryEmitOverloads_AsyncMethodWithCdecl_SkipsCdeclWrapper()
    {
        // Issue O: Async methods should NOT get @_cdecl wrappers — @_cdecl functions
        // are synchronous and cannot call async _dbw_ extension methods (missing 'await').
        var typeDb = new TypeDatabase();
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

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
        typeDb.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.FinalCounter"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "FinalCounter"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.FinalCounter"),
                MetadataAccessor = "$s10TestModule12FinalCounterCMa",
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
            Name = "FinalCounter",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.FinalCounter"),
            MangledName = "$s10TestModule12FinalCounterCN",
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

        var method = new MethodDecl
        {
            Name = "fetch",
            MangledName = "$s10TestModule12FinalCounterC5fetch5limitSi_SitF",
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
                CreateArg("limit", hasDefault: false),
                CreateArg("offset", hasDefault: true),
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = true, // ASYNC method
            IsSynthesizedAccessor = false,
            UsesCdeclMethodWrapper = true,
            UsesWrapperLibrary = true,
        };
        parentDecl.Methods.Add(method);

        var emissionContext = new ModuleEmissionContext();
        var csStringWriter = new StringWriter();
        var swiftStringWriter = new StringWriter();
        var csWriter = new CSharpWriter(csStringWriter);
        var swiftWriter = new SwiftWriter(swiftStringWriter);
        var env = new MethodEnvironment(method, typeDb);
        var logger = NullLogger.Instance;

        DefaultParameterOverloadEmitter.TryEmitOverloads(
            csWriter, swiftWriter, env, logger, emissionContext);

        var swiftOutput = swiftStringWriter.ToString();

        // Should still emit the @_silgen_name wrapper (synchronous factory calling with defaults)
        Assert.Contains("@_silgen_name", swiftOutput);
        Assert.Contains("_dbw_fetch_", swiftOutput);

        // Should NOT emit a synchronous @_cdecl method wrapper (Issue O).
        // The async callback @_cdecl (with _async suffix + Task { await }) IS expected —
        // it's the correct way to bridge async methods. What must NOT happen is
        // MethodWrapperEmitter emitting a synchronous @_cdecl that calls the async _dbw_
        // extension method without await.
        // The synchronous wrapper would have symbol like "SBW_TestModule_FinalCounter_fetch_..."
        // WITHOUT the _async suffix. The async wrapper correctly wraps with Task { await }.
        var cdeclMatches = System.Text.RegularExpressions.Regex.Matches(swiftOutput, @"@_cdecl\(""([^""]+)""\)");
        foreach (System.Text.RegularExpressions.Match match in cdeclMatches)
        {
            var symbol = match.Groups[1].Value;
            // Async wrapper symbols end in _async — those are fine
            Assert.True(symbol.EndsWith("_async"),
                $"Non-async @_cdecl wrapper found for async method: {symbol}. " +
                "Synchronous @_cdecl wrappers cannot call async _dbw_ extension methods.");
        }
    }

    #endregion
}
