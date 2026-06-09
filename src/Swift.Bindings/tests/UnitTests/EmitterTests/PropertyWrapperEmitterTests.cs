// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for PropertyWrapperEmitter: per-property @_cdecl wrappers that route
/// property getter/setter P/Invokes through C calling convention to avoid CallConvSwift crashes.
/// </summary>
public class PropertyWrapperEmitterTests
{
    #region ShouldEmitWrapper Guard Tests

    [Fact]
    public void ShouldEmitWrapper_NoAsyncLibraryName_ReturnsFalse()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        // AsyncLibraryName is null — not in xcframework mode

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var (propertyDecl, env) = CreatePropertyAndEnv("name", new NamedTypeSpec("Swift.Int"), parentDecl, moduleDecl, typeDb);

        Assert.False(PropertyWrapperEmitter.ShouldEmitWrapper(propertyDecl, env));
    }

    [Fact]
    public void ShouldEmitWrapper_GenericClassParent_ConcreteProperty_ReturnsTrue()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("GenericBox");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("GenericBox", moduleDecl);
        parentDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new("τ_0_0", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };
        var (propertyDecl, env) = CreatePropertyAndEnv("count", new NamedTypeSpec("Swift.Int"), parentDecl, moduleDecl, typeDb);

        Assert.True(PropertyWrapperEmitter.ShouldEmitWrapper(propertyDecl, env));
    }

    [Fact]
    public void ShouldEmitWrapper_GenericStructParent_ConcreteProperty_ReturnsFalse()
    {
        // Generic struct parent with concrete property type — blocked because the property
        // may come from a constrained extension. Only T-referencing properties are supported.
        var (moduleDecl, typeDb) = CreateTestEnvironment("GenericBox");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateStructDecl("GenericBox", moduleDecl);
        parentDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new("τ_0_0", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };
        var (propertyDecl, env) = CreatePropertyAndEnv("value", new NamedTypeSpec("Swift.Int"), parentDecl, moduleDecl, typeDb);

        Assert.False(PropertyWrapperEmitter.ShouldEmitWrapper(propertyDecl, env));
    }

    [Fact]
    public void ShouldEmitWrapper_GenericStructParent_CollectionConformer_ArrayOfT_ReturnsTrue()
    {
        // Generic struct parent that conforms to Swift.Collection, with a property typed
        // `Array<T>` (MusicKit `MusicItemCollection<TMusicItemType>.items` shape). The
        // Collection-family widening in CanEmitGenericClassPropertyWrapper routes this
        // getter through the @_cdecl static-dispatch wrapper instead of direct
        // CallConvSwift — the latter trips Mono Issue 1 (`!ji->async`) on 2+ type-metadata
        // args and SIGSEGVs on NativeAOT's multi-type-parameter generic P/Invoke path.
        var (moduleDecl, typeDb) = CreateTestEnvironment("MusicItemBag");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateStructDecl("MusicItemBag", moduleDecl);
        parentDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new("τ_0_0", "Item", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };
        parentDecl.Conformances = new List<TypeConformance>
        {
            new(SwiftTypeName.FromModuleQualifiedName("TestModule.MusicItemBag"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Collection"),
                "$s10TestModule12MusicItemBagVyxGSTAAMc")
        };
        var arrayOfT = new NamedTypeSpec("Swift.Array", new[] { new NamedTypeSpec("τ_0_0") });
        var (propertyDecl, env) = CreatePropertyAndEnv("items", arrayOfT, parentDecl, moduleDecl, typeDb);

        Assert.True(PropertyWrapperEmitter.ShouldEmitWrapper(propertyDecl, env));
    }

    [Fact]
    public void ShouldEmitWrapper_GenericStructParent_NonCollectionConformer_ArrayOfT_ReturnsTrue()
    {
        // Same shape as the test above, but the parent struct does NOT conform to any
        // Collection-family protocol. The gate is shape-based — Array<T> on any generic
        // struct routes through the static-dispatch wrapper because the round-trip is
        // proven end-to-end (MusicItemBag<Item>.items / IndexedSeries<Element>.items),
        // and the wrapper renders identically regardless of parent conformance.
        var (moduleDecl, typeDb) = CreateTestEnvironment("CollectibleBag");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateStructDecl("CollectibleBag", moduleDecl);
        parentDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new("τ_0_0", "Item", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };
        var arrayOfT = new NamedTypeSpec("Swift.Array", new[] { new NamedTypeSpec("τ_0_0") });
        var (propertyDecl, env) = CreatePropertyAndEnv("items", arrayOfT, parentDecl, moduleDecl, typeDb);

        Assert.True(PropertyWrapperEmitter.ShouldEmitWrapper(propertyDecl, env));
    }

    [Fact]
    public void ShouldEmitWrapper_GenericStructParent_CollectionConformer_OptionalOfT_ReturnsTrue()
    {
        // `Optional<T>` getter on a generic struct. The wrapper renders via
        // initializeMemory(as: Optional<Item>.self) and the C# side reconstructs through
        // SwiftOptional<TItem> with parent metadata injected at the call site
        // (PInvoke_getMetadata accessor). Proven end-to-end via OptionalGenericHolder<Value>
        // .Stored / .Peek runtime tests on Mono simulator and NativeAOT device.
        var (moduleDecl, typeDb) = CreateTestEnvironment("MusicItemBag");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateStructDecl("MusicItemBag", moduleDecl);
        parentDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new("τ_0_0", "Item", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };
        parentDecl.Conformances = new List<TypeConformance>
        {
            new(SwiftTypeName.FromModuleQualifiedName("TestModule.MusicItemBag"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Collection"),
                "$s10TestModule12MusicItemBagVyxGSTAAMc")
        };
        var optionalOfT = new NamedTypeSpec("Swift.Optional", new[] { new NamedTypeSpec("τ_0_0") });
        var (propertyDecl, env) = CreatePropertyAndEnv("first", optionalOfT, parentDecl, moduleDecl, typeDb);

        Assert.True(PropertyWrapperEmitter.ShouldEmitWrapper(propertyDecl, env));
    }

    [Fact]
    public void ShouldEmitWrapper_GenericStructParent_CollectionConformer_DictionaryOfStringT_ReturnsFalse()
    {
        // `Dictionary<String, T>` getter on a generic struct. The static-dispatch gate is
        // narrowed to shapes with end-to-end runtime evidence — Optional<T> and Array<T>.
        // Dictionary renders identically through initializeMemory(as: Dictionary<String, Item>.self),
        // but no BindingTest covers the round-trip yet, so it stays behind the gate.
        var (moduleDecl, typeDb) = CreateTestEnvironment("MusicItemBag");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateStructDecl("MusicItemBag", moduleDecl);
        parentDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new("τ_0_0", "Item", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };
        parentDecl.Conformances = new List<TypeConformance>
        {
            new(SwiftTypeName.FromModuleQualifiedName("TestModule.MusicItemBag"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Collection"),
                "$s10TestModule12MusicItemBagVyxGSTAAMc")
        };
        var dictStringT = new NamedTypeSpec("Swift.Dictionary",
            new TypeSpec[] { new NamedTypeSpec("Swift.String"), new NamedTypeSpec("τ_0_0") });
        var (propertyDecl, env) = CreatePropertyAndEnv("byId", dictStringT, parentDecl, moduleDecl, typeDb);

        Assert.False(PropertyWrapperEmitter.ShouldEmitWrapper(propertyDecl, env));
    }

    [Fact]
    public void ShouldEmitWrapper_GenericStructParent_CollectionConformer_UserPairOfT_ReturnsFalse()
    {
        // User-defined `Pair<T>` getter on a generic struct. The static-dispatch gate is
        // narrowed to Optional<T> and Array<T> — shapes with end-to-end runtime evidence.
        // User-defined parameterized shapes render through the same emitter path but
        // aren't validated, so they stay behind the gate.
        var (moduleDecl, typeDb) = CreateTestEnvironment("MusicItemBag");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateStructDecl("MusicItemBag", moduleDecl);
        parentDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new("τ_0_0", "Item", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };
        parentDecl.Conformances = new List<TypeConformance>
        {
            new(SwiftTypeName.FromModuleQualifiedName("TestModule.MusicItemBag"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Collection"),
                "$s10TestModule12MusicItemBagVyxGSTAAMc")
        };
        var pairOfT = new NamedTypeSpec("TestModule.Pair", new[] { new NamedTypeSpec("τ_0_0") });
        var (propertyDecl, env) = CreatePropertyAndEnv("paired", pairOfT, parentDecl, moduleDecl, typeDb);

        Assert.False(PropertyWrapperEmitter.ShouldEmitWrapper(propertyDecl, env));
    }

    [Fact]
    public void ShouldEmitWrapper_GenericStructParent_UserCollectionProtocol_ArrayOfT_ReturnsTrue()
    {
        // Parent struct conforms only to a user-defined `Other.Collection`. The shape-based
        // gate doesn't consult parent conformance — Array<T> on any generic struct routes
        // through the static-dispatch wrapper.
        var (moduleDecl, typeDb) = CreateTestEnvironment("MusicItemBag");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateStructDecl("MusicItemBag", moduleDecl);
        parentDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new("τ_0_0", "Item", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };
        parentDecl.Conformances = new List<TypeConformance>
        {
            new(SwiftTypeName.FromModuleQualifiedName("TestModule.MusicItemBag"),
                SwiftTypeName.FromModuleQualifiedName("Other.Collection"),
                "$s5Other10CollectionMp")
        };
        var arrayOfT = new NamedTypeSpec("Swift.Array", new[] { new NamedTypeSpec("τ_0_0") });
        var (propertyDecl, env) = CreatePropertyAndEnv("items", arrayOfT, parentDecl, moduleDecl, typeDb);

        Assert.True(PropertyWrapperEmitter.ShouldEmitWrapper(propertyDecl, env));
    }

    [Fact]
    public void ShouldEmitWrapper_GenericClassParent_GenericProperty_ReturnsTrue()
    {
        // Property type references parent's generic param τ_0_0 — now supported via static dispatch
        var (moduleDecl, typeDb) = CreateTestEnvironment("GenericBox");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("GenericBox", moduleDecl);
        parentDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new("τ_0_0", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };
        var (propertyDecl, env) = CreatePropertyAndEnv("value", new NamedTypeSpec("τ_0_0"), parentDecl, moduleDecl, typeDb);

        Assert.True(PropertyWrapperEmitter.ShouldEmitWrapper(propertyDecl, env));
    }

    [Fact]
    public void ShouldEmitWrapper_GenericClassParent_StaticProperty_ReturnsFalse()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("GenericBox");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("GenericBox", moduleDecl);
        parentDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new("τ_0_0", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };
        var getterMethod = CreateAccessorMethod("getter:shared", isGetter: true, parentDecl, moduleDecl);
        var propertyDecl = new PropertyDecl
        {
            Name = "shared",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            HasStorage = true,
            IsStatic = true,
            Accessors = new List<AccessorDecl> { new GetAccessorDecl { Method = getterMethod } },
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl
        };
        var env = new MethodEnvironment(getterMethod, typeDb);

        Assert.False(PropertyWrapperEmitter.ShouldEmitWrapper(propertyDecl, env));
    }

    // ─────────────────────────────────────────────────────────────────────
    // Wrapper-helper-path fail-closed gates: properties on generic types whose
    // parent has unresolvable PWT conformances OR would force the dlsym'd Ma
    // symbol into buffer mode. The wrapper helper passes only resolvable PWTs
    // and emits only the thin (request, metadata..., pwt...) signature, so
    // either mismatch shifts caller-saved registers and PAC-traps on arm64e.
    // Mirrors the gates in CanEmitGenericDispatch for methods/constructors.
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void ShouldEmitWrapper_GenericClassParent_TTypedProperty_UnresolvableConformance_ReturnsFalse()
    {
        // T-typed property on a generic class with a Self-requirement constraint.
        // T-typed properties route through EmitGenericStaticGetterWrapper, which
        // calls EmitMetadataAccessorHelperIfNeeded — exactly the path the gate
        // is meant to protect. The wrapper helper would call the dlsym'd Ma
        // symbol with one fewer PWT arg than Swift's actual signature expects,
        // shifting registers and PAC-trapping on arm64e.
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithExtraTypes(
            "GenericBox",
            ("TestModule.AnyInterpolatable", TypeRecordFlags.HasSelfRequirement, TypeRecordKind.Protocol));
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("GenericBox", moduleDecl);
        parentDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new("τ_0_0", "T",
                new List<GenericParameterConformance>
                {
                    new(new[] { "τ_0_0" },
                        SwiftTypeName.FromModuleQualifiedName("TestModule.AnyInterpolatable"),
                        ConformanceKind.Protocol)
                },
                new List<GenericParameterConformance>())
        };
        // Property type is τ_0_0 → routes through static dispatch → uses helper.
        var (propertyDecl, env) = CreatePropertyAndEnv(
            "value", new NamedTypeSpec("τ_0_0"), parentDecl, moduleDecl, typeDb);

        Assert.False(PropertyWrapperEmitter.ShouldEmitWrapper(propertyDecl, env));
        Assert.Equal("generic_parent_unresolved_pwt_constraint",
            PropertyWrapperEmitter.GetRejectionReason(propertyDecl, env));
    }

    [Fact]
    public void ShouldEmitWrapper_GenericClassParent_ConcreteProperty_UnresolvableConformance_ReturnsTrue()
    {
        // Regression: concrete (non-T-typed) properties on generic class parents
        // route through SelfReconstructionEmitter.EmitProtocolCast (instance dispatch) and
        // never call the metadata-accessor helper. The wrapper-helper gates (which guard
        // _sbw_meta_*) MUST NOT reject them, even when the parent has an unresolvable
        // conformance. Same constraint as the test above, just a Swift.Int property.
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithExtraTypes(
            "GenericBox",
            ("TestModule.AnyInterpolatable", TypeRecordFlags.HasSelfRequirement, TypeRecordKind.Protocol));
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("GenericBox", moduleDecl);
        parentDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new("τ_0_0", "T",
                new List<GenericParameterConformance>
                {
                    new(new[] { "τ_0_0" },
                        SwiftTypeName.FromModuleQualifiedName("TestModule.AnyInterpolatable"),
                        ConformanceKind.Protocol)
                },
                new List<GenericParameterConformance>())
        };
        var (propertyDecl, env) = CreatePropertyAndEnv(
            "count", new NamedTypeSpec("Swift.Int"), parentDecl, moduleDecl, typeDb);

        Assert.True(PropertyWrapperEmitter.ShouldEmitWrapper(propertyDecl, env));
    }

    [Fact]
    public void ShouldEmitWrapper_GenericClassParent_TTypedProperty_AssociatedTypeConformance_ReturnsFalse()
    {
        // Same gate, but for HasAssociatedTypes — also unresolvable on the wrapper
        // side because we can't supply a runtime witness table for it yet.
        // Property type τ_0_0 routes through static dispatch → uses helper → gate fires.
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithExtraTypes(
            "ViewBag",
            ("TestModule.ViewLike", TypeRecordFlags.HasAssociatedTypes, TypeRecordKind.Protocol));
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("ViewBag", moduleDecl);
        parentDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new("τ_0_0", "V",
                new List<GenericParameterConformance>
                {
                    new(new[] { "τ_0_0" },
                        SwiftTypeName.FromModuleQualifiedName("TestModule.ViewLike"),
                        ConformanceKind.Protocol)
                },
                new List<GenericParameterConformance>())
        };
        var (propertyDecl, env) = CreatePropertyAndEnv(
            "view", new NamedTypeSpec("τ_0_0"), parentDecl, moduleDecl, typeDb);

        Assert.False(PropertyWrapperEmitter.ShouldEmitWrapper(propertyDecl, env));
    }

    [Fact]
    public void ShouldEmitWrapper_GenericClassParent_ConcreteProperty_AssociatedTypeConformance_ReturnsTrue()
    {
        // Regression: concrete properties on classes with HasAssociatedTypes
        // conformance must NOT be rejected — they use protocol-cast dispatch.
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithExtraTypes(
            "ViewBag",
            ("TestModule.ViewLike", TypeRecordFlags.HasAssociatedTypes, TypeRecordKind.Protocol));
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("ViewBag", moduleDecl);
        parentDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new("τ_0_0", "V",
                new List<GenericParameterConformance>
                {
                    new(new[] { "τ_0_0" },
                        SwiftTypeName.FromModuleQualifiedName("TestModule.ViewLike"),
                        ConformanceKind.Protocol)
                },
                new List<GenericParameterConformance>())
        };
        var (propertyDecl, env) = CreatePropertyAndEnv(
            "count", new NamedTypeSpec("Swift.Int"), parentDecl, moduleDecl, typeDb);

        Assert.True(PropertyWrapperEmitter.ShouldEmitWrapper(propertyDecl, env));
    }

    [Fact]
    public void ShouldEmitWrapper_GenericClassParent_TTypedProperty_ExceedsRegisterThreshold_ReturnsFalse()
    {
        // 1 metadata + 3 resolvable PWTs = 4 args → exceeds Swift's (metadata + pwt)
        // > 3 register threshold. Swift's Ma symbol would use buffer-mode ABI but
        // EmitMetadataAccessorHelperIfNeeded only emits the thin signature.
        // Property type τ_0_0 routes through static dispatch → uses helper → gate fires.
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithExtraTypes(
            "GenericBox",
            ("TestModule.Alpha", TypeRecordFlags.None, TypeRecordKind.Protocol),
            ("TestModule.Beta",  TypeRecordFlags.None, TypeRecordKind.Protocol),
            ("TestModule.Gamma", TypeRecordFlags.None, TypeRecordKind.Protocol));
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("GenericBox", moduleDecl);
        parentDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new("τ_0_0", "T",
                new List<GenericParameterConformance>
                {
                    new(new[] { "τ_0_0" },
                        SwiftTypeName.FromModuleQualifiedName("TestModule.Alpha"),
                        ConformanceKind.Protocol),
                    new(new[] { "τ_0_0" },
                        SwiftTypeName.FromModuleQualifiedName("TestModule.Beta"),
                        ConformanceKind.Protocol),
                    new(new[] { "τ_0_0" },
                        SwiftTypeName.FromModuleQualifiedName("TestModule.Gamma"),
                        ConformanceKind.Protocol),
                },
                new List<GenericParameterConformance>())
        };
        var (propertyDecl, env) = CreatePropertyAndEnv(
            "value", new NamedTypeSpec("τ_0_0"), parentDecl, moduleDecl, typeDb);

        Assert.False(PropertyWrapperEmitter.ShouldEmitWrapper(propertyDecl, env));
        Assert.Equal("generic_parent_metadata_buffer_mode",
            PropertyWrapperEmitter.GetRejectionReason(propertyDecl, env));
    }

    [Fact]
    public void ShouldEmitWrapper_GenericClassParent_ConcreteProperty_ExceedsRegisterThreshold_ReturnsTrue()
    {
        // Regression: concrete properties on a constrained generic class that
        // would otherwise trip the buffer-mode threshold. The instance protocol-cast
        // path doesn't use the helper, so the gate must NOT fire.
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithExtraTypes(
            "GenericBox",
            ("TestModule.Alpha", TypeRecordFlags.None, TypeRecordKind.Protocol),
            ("TestModule.Beta",  TypeRecordFlags.None, TypeRecordKind.Protocol),
            ("TestModule.Gamma", TypeRecordFlags.None, TypeRecordKind.Protocol));
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("GenericBox", moduleDecl);
        parentDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new("τ_0_0", "T",
                new List<GenericParameterConformance>
                {
                    new(new[] { "τ_0_0" },
                        SwiftTypeName.FromModuleQualifiedName("TestModule.Alpha"),
                        ConformanceKind.Protocol),
                    new(new[] { "τ_0_0" },
                        SwiftTypeName.FromModuleQualifiedName("TestModule.Beta"),
                        ConformanceKind.Protocol),
                    new(new[] { "τ_0_0" },
                        SwiftTypeName.FromModuleQualifiedName("TestModule.Gamma"),
                        ConformanceKind.Protocol),
                },
                new List<GenericParameterConformance>())
        };
        var (propertyDecl, env) = CreatePropertyAndEnv(
            "count", new NamedTypeSpec("Swift.Int"), parentDecl, moduleDecl, typeDb);

        Assert.True(PropertyWrapperEmitter.ShouldEmitWrapper(propertyDecl, env));
    }

    [Fact]
    public void ShouldEmitWrapper_GenericClassParent_AtRegisterThreshold_ReturnsTrue()
    {
        // 1 metadata + 2 resolvable PWTs = 3 args → AT the threshold (not exceeding).
        // The thin Ma signature is correct here, so the wrapper-helper path is allowed.
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithExtraTypes(
            "GenericBox",
            ("TestModule.Alpha", TypeRecordFlags.None, TypeRecordKind.Protocol),
            ("TestModule.Beta",  TypeRecordFlags.None, TypeRecordKind.Protocol));
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("GenericBox", moduleDecl);
        parentDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new("τ_0_0", "T",
                new List<GenericParameterConformance>
                {
                    new(new[] { "τ_0_0" },
                        SwiftTypeName.FromModuleQualifiedName("TestModule.Alpha"),
                        ConformanceKind.Protocol),
                    new(new[] { "τ_0_0" },
                        SwiftTypeName.FromModuleQualifiedName("TestModule.Beta"),
                        ConformanceKind.Protocol),
                },
                new List<GenericParameterConformance>())
        };
        var (propertyDecl, env) = CreatePropertyAndEnv(
            "value", new NamedTypeSpec("τ_0_0"), parentDecl, moduleDecl, typeDb);

        Assert.True(PropertyWrapperEmitter.ShouldEmitWrapper(propertyDecl, env));
    }

    [Fact]
    public void ShouldEmitWrapper_ClosureProperty_ReadOnly_ReturnsTrue()
    {
        // Read-only direct closure properties are supported via resultPtr + invoke thunk pattern
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int") }),
            TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var (propertyDecl, env) = CreatePropertyAndEnv("handler", closureType, parentDecl, moduleDecl, typeDb);

        Assert.True(PropertyWrapperEmitter.ShouldEmitWrapper(propertyDecl, env));
    }

    [Fact]
    public void ShouldEmitWrapper_ClosureProperty_Writable_ReturnsFalse()
    {
        // Writable direct closure properties are rejected — CdeclParamMapper has no closure
        // handling for the setter path (would fall through to invalid UnsafeRawPointer reconstruction)
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int") }),
            TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var getterMethod = CreateAccessorMethod("getter:handler", isGetter: true, parentDecl, moduleDecl);
        var setterMethod = CreateAccessorMethod("setter:handler", isGetter: false, parentDecl, moduleDecl);
        var propertyDecl = new PropertyDecl
        {
            Name = "handler",
            SwiftTypeSpec = closureType,
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
        Assert.False(PropertyWrapperEmitter.ShouldEmitWrapper(propertyDecl, env));
    }

    [Fact]
    public void ShouldEmitWrapper_OptionalClosureProperty_ReturnsTrue()
    {
        // Optional<Closure> properties ARE allowed — getter routes through IndirectResult buffer
        // with null check via FunctionPointer == IntPtr.Zero (extra-inhabitant encoding).
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int32") }),
            new NamedTypeSpec("Swift.Bool"));
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));
        var optionalClosureType = new NamedTypeSpec("Swift.Optional");
        optionalClosureType.GenericParameters.Add(closureType);

        var (propertyDecl, env) = CreatePropertyAndEnv("onComplete", optionalClosureType, parentDecl, moduleDecl, typeDb);

        Assert.True(PropertyWrapperEmitter.ShouldEmitWrapper(propertyDecl, env));
    }

    [Fact]
    public void GetCdeclReturnMapping_OptionalClosure_IndirectResult()
    {
        // Optional<Closure> returns must use IndirectResult — written to resultPtr buffer
        var (_, typeDb) = CreateTestEnvironment("MyType");

        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int32") }),
            new NamedTypeSpec("Swift.Bool"));
        var optionalClosureType = new NamedTypeSpec("Swift.Optional");
        optionalClosureType.GenericParameters.Add(closureType);

        var (mapping, needsResultPtr) = CdeclReturnMapping.Classify(optionalClosureType, typeDb);

        Assert.Equal("Void", mapping.CdeclReturnType);
        Assert.Equal(CdeclReturnKind.IndirectResult, mapping.Kind);
        Assert.True(needsResultPtr);
    }

    [Fact]
    public void GetRejectionReason_DirectClosure_ReadOnly_ReturnsNull()
    {
        // Read-only direct closure properties are supported — no rejection reason
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int") }),
            TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));
        var (propertyDecl, env) = CreatePropertyAndEnv("handler", closureType, parentDecl, moduleDecl, typeDb);

        Assert.Null(PropertyWrapperEmitter.GetRejectionReason(propertyDecl, env));
    }

    [Fact]
    public void GetRejectionReason_DirectClosure_Writable_ReturnsDirectClosureSetter()
    {
        // Writable direct closure properties are rejected — setter path has no closure handling
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int") }),
            TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var getterMethod = CreateAccessorMethod("getter:handler", isGetter: true, parentDecl, moduleDecl);
        var setterMethod = CreateAccessorMethod("setter:handler", isGetter: false, parentDecl, moduleDecl);
        var propertyDecl = new PropertyDecl
        {
            Name = "handler",
            SwiftTypeSpec = closureType,
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
        Assert.Equal("direct_closure_setter", PropertyWrapperEmitter.GetRejectionReason(propertyDecl, env));
    }

    [Fact]
    public void GetRejectionReason_OptionalClosure_ReturnsNull()
    {
        // Optional<Closure> properties should pass all gates (no rejection)
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int32") }),
            new NamedTypeSpec("Swift.Bool"));
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));
        var optionalClosureType = new NamedTypeSpec("Swift.Optional");
        optionalClosureType.GenericParameters.Add(closureType);

        var (propertyDecl, env) = CreatePropertyAndEnv("onComplete", optionalClosureType, parentDecl, moduleDecl, typeDb);

        Assert.Null(PropertyWrapperEmitter.GetRejectionReason(propertyDecl, env));
    }

    [Fact]
    public void ShouldEmitWrapper_AsyncProperty_ReturnsFalse()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var getterMethod = CreateAccessorMethod("getter:name", isGetter: true, parentDecl, moduleDecl);
        getterMethod.IsAsync = true;

        var propertyDecl = new PropertyDecl
        {
            Name = "name",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            HasStorage = true,
            IsStatic = false,
            Accessors = new List<AccessorDecl> { new GetAccessorDecl { Method = getterMethod } },
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl
        };

        var env = new MethodEnvironment(getterMethod, typeDb);
        Assert.False(PropertyWrapperEmitter.ShouldEmitWrapper(propertyDecl, env));
    }

    [Fact]
    public void ShouldEmitWrapper_ProtocolExistentialProperty_ReturnsTrue()
    {
        // Existential properties are now supported in @_cdecl wrappers via indirect result pointer
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithExtraTypes("MyType",
            ("TestModule.DataCaching", TypeRecordFlags.None, TypeRecordKind.Protocol));
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var protocolListSpec = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.DataCaching") });
        var (propertyDecl, env) = CreatePropertyAndEnv("cache", protocolListSpec, parentDecl, moduleDecl, typeDb);

        Assert.True(PropertyWrapperEmitter.ShouldEmitWrapper(propertyDecl, env));
    }

    [Fact]
    public void ShouldEmitWrapper_NonCopyableStructParent_ReturnsTrue()
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
        var (propertyDecl, env) = CreatePropertyAndEnv("value", new NamedTypeSpec("Swift.Int"), parentDecl, moduleDecl, typeDb);

        Assert.True(PropertyWrapperEmitter.ShouldEmitWrapper(propertyDecl, env));
    }

    [Fact]
    public void ShouldEmitWrapper_NonGenericClass_ReturnsTrue()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyClass");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyClass", moduleDecl);
        var (propertyDecl, env) = CreatePropertyAndEnv("name", new NamedTypeSpec("Swift.Int"), parentDecl, moduleDecl, typeDb);

        Assert.True(PropertyWrapperEmitter.ShouldEmitWrapper(propertyDecl, env));
    }

    [Fact]
    public void ShouldEmitWrapper_NonGenericStruct_ReturnsTrue()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyStruct");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateStructDecl("MyStruct", moduleDecl);
        var (propertyDecl, env) = CreatePropertyAndEnv("value", new NamedTypeSpec("Swift.Int"), parentDecl, moduleDecl, typeDb);

        Assert.True(PropertyWrapperEmitter.ShouldEmitWrapper(propertyDecl, env));
    }

    [Fact]
    public void ShouldEmitPropertyWrapper_MetatypeProperty_ReturnsFalse()
    {
        // S2: Property wrapper emission also gates metatypes
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var (propertyDecl, env) = CreatePropertyAndEnv("myType", new NamedTypeSpec("Any.Type"), parentDecl, moduleDecl, typeDb);

        Assert.False(PropertyWrapperEmitter.ShouldEmitWrapper(propertyDecl, env));
    }

    [Fact]
    public void ShouldEmitPropertyWrapper_OptionalMetatypeProperty_ReturnsFalse()
    {
        // Bug-2 pin: an Optional<AnyClass.Type> property (Parchment shape) used to slip past
        // the bare-metatype gate at PropertyWrapperEmitter.ShouldEmitWrapper line 62 and then
        // hit the protocol-existential branch (line 110) — IsProtocolExistentialType returned
        // true via MetatypeStrategy resolving the inner metatype to the AnyType record
        // (Kind=Protocol). The wrapper would emit "(any AnyClass.Type).self" which is invalid
        // Swift. The fix routes the gate through IsMetatypeTypeIncludingOptional so the
        // property is rejected up front and never reaches wrapper emission.
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var optAnyClass = new NamedTypeSpec("Swift.Optional");
        optAnyClass.GenericParameters.Add(new NamedTypeSpec("AnyClass.Type"));
        var (propertyDecl, env) = CreatePropertyAndEnv("classRef", optAnyClass, parentDecl, moduleDecl, typeDb);

        Assert.False(PropertyWrapperEmitter.ShouldEmitWrapper(propertyDecl, env));
    }

    [Fact]
    public void ShouldEmitPropertyWrapper_InternalProperty_ReturnsFalse()
    {
        // S4: PropertyWrapperEmitter also gates internal properties
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var getterMethod = CreateAccessorMethod("getter:internalProp", isGetter: true, parentDecl, moduleDecl);
        var propertyDecl = new PropertyDecl
        {
            Name = "internalProp",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            HasStorage = true,
            IsStatic = false,
            IsModuleInternal = true,
            Accessors = new List<AccessorDecl> { new GetAccessorDecl { Method = getterMethod } },
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl
        };

        var env = new MethodEnvironment(getterMethod, typeDb);
        Assert.False(PropertyWrapperEmitter.ShouldEmitWrapper(propertyDecl, env));
    }

    #endregion

    #region Symbol Naming Tests

    [Fact]
    public void GetAccessorSymbolName_Getter_CorrectFormat()
    {
        var symbol = PropertyWrapperEmitter.GetAccessorSymbolName("Nuke", "ImagePipeline", "configuration", isGetter: true);
        Assert.Equal("SBW_Get_Nuke_ImagePipeline_configuration", symbol);
    }

    [Fact]
    public void GetAccessorSymbolName_Setter_CorrectFormat()
    {
        var symbol = PropertyWrapperEmitter.GetAccessorSymbolName("Nuke", "ImagePipeline", "configuration", isGetter: false);
        Assert.Equal("SBW_Set_Nuke_ImagePipeline_configuration", symbol);
    }

    [Fact]
    public void GetAccessorSymbolName_NestedType_DotReplacedWithUnderscore()
    {
        var symbol = PropertyWrapperEmitter.GetAccessorSymbolName("Nuke", "ImagePipeline.Configuration", "dataLoader", isGetter: true);
        Assert.Equal("SBW_Get_Nuke_ImagePipeline_Configuration_dataLoader", symbol);
    }

    [Fact]
    public void GetAccessorSymbolName_NestedTypesWithSameLeafName_ProduceDistinctSymbols()
    {
        // Regression: OrderContainer.Status and PaymentContainer.Status both produced
        // SBW_Get_Module_Status_rawValue when only the leaf name was used. The fix passes
        // the module-qualified name (minus the module prefix) so parent types are included.
        var orderSymbol = PropertyWrapperEmitter.GetAccessorSymbolName(
            "SwiftBindingsTestLib", "OrderContainer.Status", "rawValue", isGetter: true);
        var paymentSymbol = PropertyWrapperEmitter.GetAccessorSymbolName(
            "SwiftBindingsTestLib", "PaymentContainer.Status", "rawValue", isGetter: true);

        Assert.Equal("SBW_Get_SwiftBindingsTestLib_OrderContainer_Status_rawValue", orderSymbol);
        Assert.Equal("SBW_Get_SwiftBindingsTestLib_PaymentContainer_Status_rawValue", paymentSymbol);
        Assert.NotEqual(orderSymbol, paymentSymbol);
    }

    [Fact]
    public void GetAccessorSymbolName_LeafNameOnly_WouldCollide()
    {
        // Documents the bug that was fixed: using just the leaf name "Status"
        // would produce identical symbols for different nested types.
        var symbol1 = PropertyWrapperEmitter.GetAccessorSymbolName("Mod", "Status", "rawValue", isGetter: true);
        var symbol2 = PropertyWrapperEmitter.GetAccessorSymbolName("Mod", "Status", "rawValue", isGetter: true);
        Assert.Equal(symbol1, symbol2); // Same input → same output (the bug was in the CALLER)
    }

    #endregion

    #region Getter Wrapper Swift Emission Tests

    [Fact]
    public void EmitSwiftGetterWrapper_PrimitiveInt_DirectReturn()
    {
        var (swiftWriter, sw, propertyDecl, env, ctx) = CreateGetterTestSetup(
            "count", new NamedTypeSpec("Swift.Int"), isClass: true);

        var symbol = "SBW_Get_TestModule_MyType_count";
        PropertyWrapperEmitter.EmitSwiftGetterWrapper(swiftWriter, propertyDecl, symbol, env, ctx);

        var output = sw.ToString();
        Assert.Contains("@_cdecl(\"SBW_Get_TestModule_MyType_count\")", output);
        Assert.Contains("-> Int", output);
        Assert.Contains("let obj = Unmanaged<TestModule.MyType>.fromOpaque(self_).takeUnretainedValue()", output);
        Assert.Contains("return obj.count", output);
    }

    [Fact]
    public void EmitSwiftGetterWrapper_Bool_ReturnsInt8()
    {
        var (swiftWriter, sw, propertyDecl, env, ctx) = CreateGetterTestSetup(
            "isEnabled", new NamedTypeSpec("Swift.Bool"), isClass: true);

        var symbol = "SBW_Get_TestModule_MyType_isEnabled";
        PropertyWrapperEmitter.EmitSwiftGetterWrapper(swiftWriter, propertyDecl, symbol, env, ctx);

        var output = sw.ToString();
        Assert.Contains("-> Int8", output);
        Assert.Contains("return obj.isEnabled ? 1 : 0", output);
    }

    [Fact]
    public void EmitSwiftGetterWrapper_String_SBWUtf8Slice()
    {
        var (swiftWriter, sw, propertyDecl, env, ctx) = CreateGetterTestSetup(
            "name", new NamedTypeSpec("Swift.String"), isClass: true);

        var symbol = "SBW_Get_TestModule_MyType_name";
        PropertyWrapperEmitter.EmitSwiftGetterWrapper(swiftWriter, propertyDecl, symbol, env, ctx);

        var output = sw.ToString();
        // String returns via resultPtr (@_cdecl can't return Swift structs)
        Assert.Contains("resultPtr: UnsafeMutableRawPointer", output);
        Assert.DoesNotContain("-> SBW_Utf8Slice", output);
        Assert.Contains("let result = obj.name", output);
        Assert.Contains("let utf8 = Array(result.utf8)", output);
        Assert.Contains("resultPtr.storeBytes(of: SBW_Utf8Slice(ptr: ptr, len: utf8.count)", output);
        // Also emits the SBW_Utf8Slice struct
        Assert.Contains("@frozen", output);
        Assert.Contains("public struct SBW_Utf8Slice", output);
    }

    [Fact]
    public void EmitSwiftGetterWrapper_SimpleEnum_ReturnsRawValue()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithExtraTypes("MyType",
            ("TestModule.ContentMode", TypeRecordFlags.Frozen | TypeRecordFlags.SimpleEnum, TypeRecordKind.Enum, "Swift.Int"));
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var propertySpec = new NamedTypeSpec("TestModule.ContentMode");
        var getterMethod = CreateAccessorMethod("getter:mode", isGetter: true, parentDecl, moduleDecl);
        var propertyDecl = new PropertyDecl
        {
            Name = "mode",
            SwiftTypeSpec = propertySpec,
            HasStorage = true,
            IsStatic = false,
            Accessors = new List<AccessorDecl> { new GetAccessorDecl { Method = getterMethod } },
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl
        };

        var env = new MethodEnvironment(getterMethod, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);

        var symbol = "SBW_Get_TestModule_MyType_mode";
        PropertyWrapperEmitter.EmitSwiftGetterWrapper(swiftWriter, propertyDecl, symbol, env, ctx);

        var output = sw.ToString();
        Assert.Contains("-> Int", output);
        Assert.Contains("rawValue", output);
    }

    [Fact]
    public void EmitSwiftGetterWrapper_Class_ReturnsUnmanagedPointer()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithExtraTypes("MyType",
            ("TestModule.ChildObj", TypeRecordFlags.None, TypeRecordKind.Class));
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var propertySpec = new NamedTypeSpec("TestModule.ChildObj");
        var getterMethod = CreateAccessorMethod("getter:child", isGetter: true, parentDecl, moduleDecl);
        var propertyDecl = new PropertyDecl
        {
            Name = "child",
            SwiftTypeSpec = propertySpec,
            HasStorage = true,
            IsStatic = false,
            Accessors = new List<AccessorDecl> { new GetAccessorDecl { Method = getterMethod } },
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl
        };

        var env = new MethodEnvironment(getterMethod, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);

        var symbol = "SBW_Get_TestModule_MyType_child";
        PropertyWrapperEmitter.EmitSwiftGetterWrapper(swiftWriter, propertyDecl, symbol, env, ctx);

        var output = sw.ToString();
        Assert.Contains("-> UnsafeMutableRawPointer", output);
        Assert.Contains("Unmanaged.passRetained(obj.child as AnyObject).toOpaque()", output);
    }

    [Fact]
    public void EmitSwiftGetterWrapper_NonFrozenStruct_UsesResultPtr()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithExtraTypes("MyType",
            ("TestModule.Config", TypeRecordFlags.None, TypeRecordKind.Struct));
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var propertySpec = new NamedTypeSpec("TestModule.Config");
        var getterMethod = CreateAccessorMethod("getter:config", isGetter: true, parentDecl, moduleDecl);
        var propertyDecl = new PropertyDecl
        {
            Name = "config",
            SwiftTypeSpec = propertySpec,
            HasStorage = true,
            IsStatic = false,
            Accessors = new List<AccessorDecl> { new GetAccessorDecl { Method = getterMethod } },
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl
        };

        var env = new MethodEnvironment(getterMethod, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);

        var symbol = "SBW_Get_TestModule_MyType_config";
        PropertyWrapperEmitter.EmitSwiftGetterWrapper(swiftWriter, propertyDecl, symbol, env, ctx);

        var output = sw.ToString();
        Assert.Contains("_ resultPtr: UnsafeMutableRawPointer", output);
        Assert.Contains("initializeMemory", output);
        Assert.DoesNotContain("->", output); // void return
    }

    [Fact]
    public void EmitSwiftGetterWrapper_StaticProperty_NoSelfParam()
    {
        var (swiftWriter, sw, propertyDecl, env, ctx) = CreateGetterTestSetup(
            "shared", new NamedTypeSpec("Swift.Int"), isClass: true, isStatic: true);

        var symbol = "SBW_Get_TestModule_MyType_shared";
        PropertyWrapperEmitter.EmitSwiftGetterWrapper(swiftWriter, propertyDecl, symbol, env, ctx);

        var output = sw.ToString();
        Assert.DoesNotContain("self_", output);
        Assert.Contains("TestModule.MyType.shared", output);
    }

    [Fact]
    public void EmitSwiftGetterWrapper_StructProperty_UsesUnsafeRawPointerSelf()
    {
        var (swiftWriter, sw, propertyDecl, env, ctx) = CreateGetterTestSetup(
            "value", new NamedTypeSpec("Swift.Int"), isClass: false);

        var symbol = "SBW_Get_TestModule_MyType_value";
        PropertyWrapperEmitter.EmitSwiftGetterWrapper(swiftWriter, propertyDecl, symbol, env, ctx);

        var output = sw.ToString();
        Assert.Contains("_ self_: UnsafeRawPointer", output);
        Assert.Contains("self_.assumingMemoryBound(to: TestModule.MyType.self).pointee", output);
    }

    [Fact]
    public void EmitSwiftGetterWrapper_MainActorIsolated_HasAnnotationOnCdecl()
    {
        // @MainActor IS propagated to @_cdecl wrappers — Swift 6 requires the caller
        // to share isolation context. @MainActor on @_cdecl is compile-time only.
        var (swiftWriter, sw, propertyDecl, env, ctx) = CreateGetterTestSetup(
            "count", new NamedTypeSpec("Swift.Int"), isClass: true, isMainActorIsolated: true);

        var symbol = "SBW_Get_TestModule_MyType_count";
        PropertyWrapperEmitter.EmitSwiftGetterWrapper(swiftWriter, propertyDecl, symbol, env, ctx);

        var output = sw.ToString();
        Assert.Contains("@MainActor", output);
        Assert.Contains("@_cdecl", output);
    }

    [Fact]
    public void EmitSwiftGetterWrapper_GenericClassParent_EmitsProtocolErasure()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("GenericBox");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("GenericBox", moduleDecl);
        parentDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new("τ_0_0", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };
        var getterMethod = CreateAccessorMethod("getter:count", isGetter: true, parentDecl, moduleDecl);
        var propertyDecl = new PropertyDecl
        {
            Name = "count",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            HasStorage = true,
            IsStatic = false,
            Accessors = new List<AccessorDecl> { new GetAccessorDecl { Method = getterMethod } },
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl
        };

        var env = new MethodEnvironment(getterMethod, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);
        var symbol = "SBW_Get_TestModule_GenericBox_count";

        PropertyWrapperEmitter.EmitSwiftGetterWrapper(swiftWriter, propertyDecl, symbol, env, ctx);

        var output = sw.ToString();
        // Protocol-based type erasure: protocol with getter
        Assert.Contains("private protocol _SBW_PG_", output);
        Assert.Contains("var count: Int { get }", output);
        Assert.Contains("extension TestModule.GenericBox:", output);
        // Metadata parameter (accepted but unused)
        Assert.Contains("_ _metadata0: UnsafeRawPointer", output);
        // Self reconstruction via AnyObject
        Assert.Contains("Unmanaged<AnyObject>.fromOpaque(self_).takeUnretainedValue() as! any _SBW_PG_", output);
        // Should NOT use concrete type for self
        Assert.DoesNotContain("Unmanaged<TestModule.GenericBox>", output);
    }

    [Fact]
    public void EmitSwiftGetterWrapper_ConstrainedGenericClassParent_ConcreteProperty_EmitsPwtAfterMetadata()
    {
        // Regression for the property-getter SIGSEGV on constrained-generic classes:
        // when the parent class carries a resolvable protocol conformance (e.g.,
        // `class Box<T: Marker>`), the C# P/Invoke side passes both _metadata0 AND
        // _pwt0 in the Metadata phase. The Swift wrapper signature must absorb both,
        // otherwise the PWT pointer slides into the self_ slot and the wrapper's
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
        var getterMethod = CreateAccessorMethod("getter:label", isGetter: true, parentDecl, moduleDecl);
        var propertyDecl = new PropertyDecl
        {
            Name = "label",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            HasStorage = true,
            IsStatic = false,
            Accessors = new List<AccessorDecl> { new GetAccessorDecl { Method = getterMethod } },
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl
        };

        var env = new MethodEnvironment(getterMethod, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);
        var symbol = "SBW_Get_TestModule_ConstrainedBox_label";

        PropertyWrapperEmitter.EmitSwiftGetterWrapper(swiftWriter, propertyDecl, symbol, env, ctx);

        var output = sw.ToString();
        Assert.Contains("_ _metadata0: UnsafeRawPointer", output);
        Assert.Contains("_ _pwt0: UnsafeRawPointer", output);
        // PWT must come AFTER metadata and BEFORE self in @_cdecl signature
        var cdeclLine = output.Split('\n').First(l => l.Contains("@_cdecl") || l.Contains("public func _sbw_"));
        // Pick the func signature line specifically — the @_cdecl attribute is on the line above.
        cdeclLine = output.Split('\n').First(l => l.Contains("public func _sbw_get_label_"));
        var metaIdx = cdeclLine.IndexOf("_metadata0");
        var pwtIdx = cdeclLine.IndexOf("_pwt0");
        var selfIdx = cdeclLine.IndexOf("self_");
        Assert.True(metaIdx >= 0 && pwtIdx >= 0 && selfIdx >= 0, $"Expected all three params on the wrapper signature; got: {cdeclLine}");
        Assert.True(metaIdx < pwtIdx, "Metadata must come before PWT");
        Assert.True(pwtIdx < selfIdx, "PWT must come before self_");
    }

    [Fact]
    public void EmitSwiftGetterWrapper_GenericClassParent_TTypedProperty_UsesMetadataAccessorAndCorrectParamOrder()
    {
        // Property type references generic param T — triggers generic static dispatch path.
        // Verifies: (1) _sbw_meta_ helper emitted, (2) metadata params come BEFORE self in @_cdecl.
        var (moduleDecl, typeDb) = CreateTestEnvironment("GenericClass");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("GenericClass", moduleDecl);
        parentDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new("τ_0_0", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };
        var getterMethod = CreateAccessorMethod("getter:value", isGetter: true, parentDecl, moduleDecl);
        var propertyDecl = new PropertyDecl
        {
            Name = "value",
            SwiftTypeSpec = new NamedTypeSpec("τ_0_0"),
            HasStorage = true,
            IsStatic = false,
            Accessors = new List<AccessorDecl> { new GetAccessorDecl { Method = getterMethod } },
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl
        };

        var env = new MethodEnvironment(getterMethod, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);
        var symbol = "SBW_Get_TestModule_GenericClass_value";

        PropertyWrapperEmitter.EmitSwiftGetterWrapper(swiftWriter, propertyDecl, symbol, env, ctx);

        var output = sw.ToString();
        // Metadata accessor helper emitted at module scope
        Assert.Contains("_sbw_meta_", output);
        Assert.Contains("dlsym(dlopen(nil, RTLD_LAZY)", output);
        Assert.Contains("Ma", output); // metadata accessor suffix

        // Metatype dispatch uses helper result
        Assert.Contains("unsafeBitCast(parentMeta, to: Any.Type.self)", output);
        Assert.Contains("as! any _SBW_GSPG_", output);

        // Parameter ordering: metadata BEFORE self in @_cdecl signature
        var cdeclLine = output.Split('\n').First(l => l.Contains("public func _sbw_get_value_"));
        var metaIdx = cdeclLine.IndexOf("_metadata0");
        var selfIdx = cdeclLine.IndexOf("self_");
        Assert.True(metaIdx < selfIdx, "Metadata param must come before self in @_cdecl signature");
    }

    [Fact]
    public void EmitSwiftGetterWrapper_DecomposedOptionalComplexEnum_EmitsHasValuePattern()
    {
        // Optional<ComplexEnum> getter should use decomposed pattern:
        // separate resultPtr for payload and hasValuePtr for the hasValue flag.
        // This exercises the isDecomposedOptionalGetter branch (PropertyWrapperEmitter line ~220).
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithExtraTypes("MyType",
            ("TestModule.Shape", TypeRecordFlags.None, TypeRecordKind.Enum));
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var optionalShapeSpec = MakeOptionalSpec("TestModule.Shape");
        var getterMethod = CreateAccessorMethod("getter:currentShape", isGetter: true, parentDecl, moduleDecl);
        var propertyDecl = new PropertyDecl
        {
            Name = "currentShape",
            SwiftTypeSpec = optionalShapeSpec,
            HasStorage = true,
            IsStatic = false,
            Accessors = new List<AccessorDecl> { new GetAccessorDecl { Method = getterMethod } },
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl
        };

        var env = new MethodEnvironment(getterMethod, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);

        var symbol = "SBW_Get_TestModule_MyType_currentShape";
        PropertyWrapperEmitter.EmitSwiftGetterWrapper(swiftWriter, propertyDecl, symbol, env, ctx);

        var output = sw.ToString();
        // Decomposed pattern: resultPtr + hasValuePtr parameters
        Assert.Contains("_ resultPtr: UnsafeMutableRawPointer", output);
        Assert.Contains("_ hasValuePtr: UnsafeMutableRawPointer", output);
        // Void return (no -> in signature)
        Assert.DoesNotContain("->", output);
        // Some case: writes payload to resultPtr and hasValue=1 to hasValuePtr
        Assert.Contains("if let value = result", output);
        Assert.Contains("resultPtr.initializeMemory(as: TestModule.Shape.self, repeating: value, count: 1)", output);
        Assert.Contains("hasValuePtr.storeBytes(of: Int8(1), as: Int8.self)", output);
        // None case: writes hasValue=0 to hasValuePtr
        Assert.Contains("hasValuePtr.storeBytes(of: Int8(0), as: Int8.self)", output);
    }

    [Fact]
    public void EmitSwiftGetterWrapper_DecomposedOptionalNonFrozenStruct_EmitsHasValuePattern()
    {
        // Optional<NonFrozenStruct> getter should also use decomposed pattern.
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithExtraTypes("MyType",
            ("TestModule.Config", TypeRecordFlags.None, TypeRecordKind.Struct));
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var optionalConfigSpec = MakeOptionalSpec("TestModule.Config");
        var getterMethod = CreateAccessorMethod("getter:config", isGetter: true, parentDecl, moduleDecl);
        var propertyDecl = new PropertyDecl
        {
            Name = "config",
            SwiftTypeSpec = optionalConfigSpec,
            HasStorage = true,
            IsStatic = false,
            Accessors = new List<AccessorDecl> { new GetAccessorDecl { Method = getterMethod } },
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl
        };

        var env = new MethodEnvironment(getterMethod, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);

        var symbol = "SBW_Get_TestModule_MyType_config";
        PropertyWrapperEmitter.EmitSwiftGetterWrapper(swiftWriter, propertyDecl, symbol, env, ctx);

        var output = sw.ToString();
        Assert.Contains("_ resultPtr: UnsafeMutableRawPointer", output);
        Assert.Contains("_ hasValuePtr: UnsafeMutableRawPointer", output);
        Assert.Contains("if let value = result", output);
        Assert.Contains("resultPtr.initializeMemory(as: TestModule.Config.self, repeating: value, count: 1)", output);
        Assert.Contains("hasValuePtr.storeBytes(of: Int8(1), as: Int8.self)", output);
        Assert.Contains("hasValuePtr.storeBytes(of: Int8(0), as: Int8.self)", output);
    }

    [Fact]
    public void EmitSwiftGetterWrapper_OptionalClass_DoesNotUseDecomposedPattern()
    {
        // Optional<Class> should NOT use decomposed pattern — classes use nullable pointer ABI.
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithExtraTypes("MyType",
            ("TestModule.ChildObj", TypeRecordFlags.None, TypeRecordKind.Class));
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var optionalClassSpec = MakeOptionalSpec("TestModule.ChildObj");
        var getterMethod = CreateAccessorMethod("getter:child", isGetter: true, parentDecl, moduleDecl);
        var propertyDecl = new PropertyDecl
        {
            Name = "child",
            SwiftTypeSpec = optionalClassSpec,
            HasStorage = true,
            IsStatic = false,
            Accessors = new List<AccessorDecl> { new GetAccessorDecl { Method = getterMethod } },
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl
        };

        var env = new MethodEnvironment(getterMethod, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);

        var symbol = "SBW_Get_TestModule_MyType_child";
        PropertyWrapperEmitter.EmitSwiftGetterWrapper(swiftWriter, propertyDecl, symbol, env, ctx);

        var output = sw.ToString();
        // Should NOT have hasValuePtr parameter (nullable pointer ABI, not decomposed)
        Assert.DoesNotContain("hasValuePtr", output);
    }

    #endregion

    #region Setter Wrapper Swift Emission Tests

    [Fact]
    public void EmitSwiftSetterWrapper_PrimitiveInt_DirectAssign()
    {
        var (swiftWriter, sw, propertyDecl, env, ctx) = CreateSetterTestSetup(
            "count", new NamedTypeSpec("Swift.Int"), isClass: true);

        var symbol = "SBW_Set_TestModule_MyType_count";
        PropertyWrapperEmitter.EmitSwiftSetterWrapper(swiftWriter, propertyDecl, symbol, env, ctx);

        var output = sw.ToString();
        Assert.Contains("@_cdecl(\"SBW_Set_TestModule_MyType_count\")", output);
        Assert.Contains("_ newValue: Int", output);
        Assert.Contains("obj.count = newValue", output);
    }

    [Fact]
    public void EmitSwiftSetterWrapper_Bool_Int8Param()
    {
        var (swiftWriter, sw, propertyDecl, env, ctx) = CreateSetterTestSetup(
            "isEnabled", new NamedTypeSpec("Swift.Bool"), isClass: true);

        var symbol = "SBW_Set_TestModule_MyType_isEnabled";
        PropertyWrapperEmitter.EmitSwiftSetterWrapper(swiftWriter, propertyDecl, symbol, env, ctx);

        var output = sw.ToString();
        Assert.Contains("_ newValue: Int8", output);
        Assert.Contains("newValueVal = newValue != 0", output);
    }

    [Fact]
    public void EmitSwiftSetterWrapper_String_Utf8PointerAndLength()
    {
        var (swiftWriter, sw, propertyDecl, env, ctx) = CreateSetterTestSetup(
            "name", new NamedTypeSpec("Swift.String"), isClass: true);

        var symbol = "SBW_Set_TestModule_MyType_name";
        PropertyWrapperEmitter.EmitSwiftSetterWrapper(swiftWriter, propertyDecl, symbol, env, ctx);

        var output = sw.ToString();
        Assert.Contains("_ utf8Ptr: UnsafePointer<UInt8>", output);
        Assert.Contains("_ utf8Len: Int", output);
        Assert.Contains("String(bytes: UnsafeBufferPointer(start: utf8Ptr, count: utf8Len), encoding: .utf8)!", output);
        Assert.Contains("obj.name = newValue", output);
    }

    [Fact]
    public void EmitSwiftSetterWrapper_ClassProperty_SetOnReconstructedObj()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithExtraTypes("MyType",
            ("TestModule.ChildObj", TypeRecordFlags.None, TypeRecordKind.Class));
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var propertySpec = new NamedTypeSpec("TestModule.ChildObj");
        var setterMethod = CreateAccessorMethod("setter:child", isGetter: false, parentDecl, moduleDecl);
        var propertyDecl = new PropertyDecl
        {
            Name = "child",
            SwiftTypeSpec = propertySpec,
            HasStorage = true,
            IsStatic = false,
            Accessors = new List<AccessorDecl> { new SetAccessorDecl { Method = setterMethod } },
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl
        };

        var env = new MethodEnvironment(setterMethod, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);

        var symbol = "SBW_Set_TestModule_MyType_child";
        PropertyWrapperEmitter.EmitSwiftSetterWrapper(swiftWriter, propertyDecl, symbol, env, ctx);

        var output = sw.ToString();
        Assert.Contains("_ newValue: UnsafeMutableRawPointer", output);
        Assert.Contains("Unmanaged<TestModule.ChildObj>.fromOpaque(newValue).takeUnretainedValue()", output);
    }

    [Fact]
    public void EmitSwiftSetterWrapper_StructParent_MutatesThroughPointer()
    {
        var (swiftWriter, sw, propertyDecl, env, ctx) = CreateSetterTestSetup(
            "value", new NamedTypeSpec("Swift.Int"), isClass: false);

        var symbol = "SBW_Set_TestModule_MyType_value";
        PropertyWrapperEmitter.EmitSwiftSetterWrapper(swiftWriter, propertyDecl, symbol, env, ctx);

        var output = sw.ToString();
        Assert.Contains("_ self_: UnsafeMutableRawPointer", output);
        Assert.Contains("self_.assumingMemoryBound(to: TestModule.MyType.self).pointee.value = newValue", output);
    }

    [Fact]
    public void EmitSwiftSetterWrapper_StaticProperty_NoSelfParam()
    {
        var (swiftWriter, sw, propertyDecl, env, ctx) = CreateSetterTestSetup(
            "shared", new NamedTypeSpec("Swift.Int"), isClass: true, isStatic: true);

        var symbol = "SBW_Set_TestModule_MyType_shared";
        PropertyWrapperEmitter.EmitSwiftSetterWrapper(swiftWriter, propertyDecl, symbol, env, ctx);

        var output = sw.ToString();
        Assert.DoesNotContain("self_", output);
        Assert.Contains("TestModule.MyType.shared = newValue", output);
    }

    [Fact]
    public void EmitSwiftSetterWrapper_GenericClassParent_EmitsProtocolErasure()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("GenericBox");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("GenericBox", moduleDecl);
        parentDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new("τ_0_0", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };
        var setterMethod = CreateAccessorMethod("setter:count", isGetter: false, parentDecl, moduleDecl);
        var propertyDecl = new PropertyDecl
        {
            Name = "count",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            HasStorage = true,
            IsStatic = false,
            Accessors = new List<AccessorDecl> { new SetAccessorDecl { Method = setterMethod } },
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl
        };

        var env = new MethodEnvironment(setterMethod, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);
        var symbol = "SBW_Set_TestModule_GenericBox_count";

        PropertyWrapperEmitter.EmitSwiftSetterWrapper(swiftWriter, propertyDecl, symbol, env, ctx);

        var output = sw.ToString();
        // Protocol-based type erasure: protocol with getter + setter
        Assert.Contains("private protocol _SBW_PS_", output);
        Assert.Contains("var count: Int { get set }", output);
        Assert.Contains("extension TestModule.GenericBox:", output);
        // Metadata parameter
        Assert.Contains("_ _metadata0: UnsafeRawPointer", output);
        // Mutable self via AnyObject cast
        Assert.Contains("var obj = Unmanaged<AnyObject>.fromOpaque(self_).takeUnretainedValue() as! any _SBW_PS_", output);
        Assert.Contains("obj.count = newValue", output);
        // Should NOT use concrete type for self
        Assert.DoesNotContain("Unmanaged<TestModule.GenericBox>", output);
    }

    [Fact]
    public void EmitSwiftSetterWrapper_ConstrainedGenericClassParent_ConcreteProperty_EmitsPwtAfterMetadata()
    {
        // Setter counterpart of the constrained-generic getter regression — the
        // wrapper must absorb both _metadata0 and _pwt0 in the Metadata phase so
        // the PWT pointer does not slide into the self_ slot.
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
        var setterMethod = CreateAccessorMethod("setter:label", isGetter: false, parentDecl, moduleDecl);
        var propertyDecl = new PropertyDecl
        {
            Name = "label",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            HasStorage = true,
            IsStatic = false,
            Accessors = new List<AccessorDecl> { new SetAccessorDecl { Method = setterMethod } },
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl
        };

        var env = new MethodEnvironment(setterMethod, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);
        var symbol = "SBW_Set_TestModule_ConstrainedBox_label";

        PropertyWrapperEmitter.EmitSwiftSetterWrapper(swiftWriter, propertyDecl, symbol, env, ctx);

        var output = sw.ToString();
        Assert.Contains("_ _metadata0: UnsafeRawPointer", output);
        Assert.Contains("_ _pwt0: UnsafeRawPointer", output);
        var cdeclLine = output.Split('\n').First(l => l.Contains("public func _sbw_set_label_"));
        var metaIdx = cdeclLine.IndexOf("_metadata0");
        var pwtIdx = cdeclLine.IndexOf("_pwt0");
        var selfIdx = cdeclLine.IndexOf("self_");
        Assert.True(metaIdx >= 0 && pwtIdx >= 0 && selfIdx >= 0, $"Expected all three params on the wrapper signature; got: {cdeclLine}");
        Assert.True(metaIdx < pwtIdx, "Metadata must come before PWT");
        Assert.True(pwtIdx < selfIdx, "PWT must come before self_");
    }

    [Fact]
    public void EmitSwiftSetterWrapper_GenericClassParent_TTypedProperty_UsesMetadataAccessorAndCorrectParamOrder()
    {
        // Property type references generic param T — triggers generic static dispatch path.
        // Verifies: (1) _sbw_meta_ helper emitted, (2) metadata params come BEFORE self in @_cdecl.
        var (moduleDecl, typeDb) = CreateTestEnvironment("GenericClass");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("GenericClass", moduleDecl);
        parentDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new("τ_0_0", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
        };
        var setterMethod = CreateAccessorMethod("setter:value", isGetter: false, parentDecl, moduleDecl);
        var propertyDecl = new PropertyDecl
        {
            Name = "value",
            SwiftTypeSpec = new NamedTypeSpec("τ_0_0"),
            HasStorage = true,
            IsStatic = false,
            Accessors = new List<AccessorDecl> { new SetAccessorDecl { Method = setterMethod } },
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl
        };

        var env = new MethodEnvironment(setterMethod, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);
        var symbol = "SBW_Set_TestModule_GenericClass_value";

        PropertyWrapperEmitter.EmitSwiftSetterWrapper(swiftWriter, propertyDecl, symbol, env, ctx);

        var output = sw.ToString();
        // Metadata accessor helper emitted at module scope
        Assert.Contains("_sbw_meta_", output);
        Assert.Contains("dlsym(dlopen(nil, RTLD_LAZY)", output);

        // Metatype dispatch uses helper result
        Assert.Contains("unsafeBitCast(parentMeta, to: Any.Type.self)", output);
        Assert.Contains("as! any _SBW_GSPS_", output);

        // Parameter ordering: metadata BEFORE self in @_cdecl signature
        var cdeclLine = output.Split('\n').First(l => l.Contains("public func _sbw_set_value_"));
        var metaIdx = cdeclLine.IndexOf("_metadata0");
        var selfIdx = cdeclLine.IndexOf("self_");
        Assert.True(metaIdx < selfIdx, "Metadata param must come before self in @_cdecl signature");
    }

    [Fact]
    public void EmitSwiftSetterWrapper_DecomposedOptionalComplexEnum_EmitsHasValuePattern()
    {
        // Optional<ComplexEnum> setter should use decomposed pattern:
        // UnsafeRawPointer payload + Int8 hasValue, with Swift-side Optional reconstruction.
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithExtraTypes("MyType",
            ("TestModule.Shape", TypeRecordFlags.None, TypeRecordKind.Enum));
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var optionalShapeSpec = MakeOptionalSpec("TestModule.Shape");
        var setterMethod = CreateAccessorMethod("setter:currentShape", isGetter: false, parentDecl, moduleDecl);
        var propertyDecl = new PropertyDecl
        {
            Name = "currentShape",
            SwiftTypeSpec = optionalShapeSpec,
            HasStorage = true,
            IsStatic = false,
            Accessors = new List<AccessorDecl> { new SetAccessorDecl { Method = setterMethod } },
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl
        };

        var env = new MethodEnvironment(setterMethod, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);

        var symbol = "SBW_Set_TestModule_MyType_currentShape";
        PropertyWrapperEmitter.EmitSwiftSetterWrapper(swiftWriter, propertyDecl, symbol, env, ctx);

        var output = sw.ToString();
        // Decomposed setter: UnsafeRawPointer payload + Int8 hasValue
        Assert.Contains("_ newValue: UnsafeRawPointer", output);
        Assert.Contains("_ hasValue: Int8", output);
        // Swift-side reconstruction of Optional from decomposed parts
        Assert.Contains("let newValueVal: TestModule.Shape?", output);
        Assert.Contains("hasValue != 0", output);
        Assert.Contains("newValue.assumingMemoryBound(to: TestModule.Shape.self).pointee", output);
        // Nil case: when hasValue == 0, reconstructed optional is nil
        Assert.Contains(": nil", output);
        Assert.Contains("obj.currentShape = newValueVal", output);
    }

    [Fact]
    public void EmitSwiftSetterWrapper_DecomposedOptionalNonFrozenStruct_EmitsHasValuePattern()
    {
        // Optional<NonFrozenStruct> setter should also use decomposed pattern.
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithExtraTypes("MyType",
            ("TestModule.Config", TypeRecordFlags.None, TypeRecordKind.Struct));
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var optionalConfigSpec = MakeOptionalSpec("TestModule.Config");
        var setterMethod = CreateAccessorMethod("setter:config", isGetter: false, parentDecl, moduleDecl);
        var propertyDecl = new PropertyDecl
        {
            Name = "config",
            SwiftTypeSpec = optionalConfigSpec,
            HasStorage = true,
            IsStatic = false,
            Accessors = new List<AccessorDecl> { new SetAccessorDecl { Method = setterMethod } },
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl
        };

        var env = new MethodEnvironment(setterMethod, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);

        var symbol = "SBW_Set_TestModule_MyType_config";
        PropertyWrapperEmitter.EmitSwiftSetterWrapper(swiftWriter, propertyDecl, symbol, env, ctx);

        var output = sw.ToString();
        Assert.Contains("_ newValue: UnsafeRawPointer", output);
        Assert.Contains("_ hasValue: Int8", output);
        Assert.Contains("let newValueVal: TestModule.Config?", output);
        Assert.Contains("hasValue != 0", output);
        // Nil case: when hasValue == 0, reconstructed optional is nil
        Assert.Contains(": nil", output);
        Assert.Contains("obj.config = newValueVal", output);
    }

    #endregion

    #region Dedup Tests

    [Fact]
    public void EmitSwiftGetterWrapper_SameSymbolTwice_OnlyEmitsOnce()
    {
        var (swiftWriter, sw, propertyDecl, env, ctx) = CreateGetterTestSetup(
            "count", new NamedTypeSpec("Swift.Int"), isClass: true);

        var symbol = "SBW_Get_TestModule_MyType_count";
        PropertyWrapperEmitter.EmitSwiftGetterWrapper(swiftWriter, propertyDecl, symbol, env, ctx);
        PropertyWrapperEmitter.EmitSwiftGetterWrapper(swiftWriter, propertyDecl, symbol, env, ctx);

        var output = sw.ToString();
        // Only one @_cdecl annotation
        var cdeclCount = output.Split("@_cdecl(\"").Length - 1;
        Assert.Equal(1, cdeclCount);
    }

    [Fact]
    public void EmitSwiftSetterWrapper_SameSymbolTwice_OnlyEmitsOnce()
    {
        var (swiftWriter, sw, propertyDecl, env, ctx) = CreateSetterTestSetup(
            "count", new NamedTypeSpec("Swift.Int"), isClass: true);

        var symbol = "SBW_Set_TestModule_MyType_count";
        PropertyWrapperEmitter.EmitSwiftSetterWrapper(swiftWriter, propertyDecl, symbol, env, ctx);
        PropertyWrapperEmitter.EmitSwiftSetterWrapper(swiftWriter, propertyDecl, symbol, env, ctx);

        var output = sw.ToString();
        var cdeclCount = output.Split("@_cdecl(\"").Length - 1;
        Assert.Equal(1, cdeclCount);
    }

    #endregion

    #region GetCdeclReturnMapping Tests

    [Fact]
    public void GetCdeclReturnMapping_Int_DirectReturn()
    {
        var (_, typeDb) = CreateTestEnvironment("MyType");
        var (mapping, needsPtr) = CdeclReturnMapping.Classify(
            new NamedTypeSpec("Swift.Int"), typeDb);

        Assert.False(needsPtr);
        Assert.Equal("Int", mapping.CdeclReturnType);
        Assert.Equal(CdeclReturnKind.Direct, mapping.Kind);
    }

    [Fact]
    public void GetCdeclReturnMapping_Bool_Int8()
    {
        var (_, typeDb) = CreateTestEnvironment("MyType");
        var (mapping, needsPtr) = CdeclReturnMapping.Classify(
            new NamedTypeSpec("Swift.Bool"), typeDb);

        Assert.False(needsPtr);
        Assert.Equal("Int8", mapping.CdeclReturnType);
        Assert.Equal(CdeclReturnKind.Bool, mapping.Kind);
    }

    [Fact]
    public void GetCdeclReturnMapping_String_SBWUtf8Slice()
    {
        var (_, typeDb) = CreateTestEnvironment("MyType");
        var (mapping, needsPtr) = CdeclReturnMapping.Classify(
            new NamedTypeSpec("Swift.String"), typeDb);

        Assert.True(needsPtr); // String returns via resultPtr (@_cdecl can't return Swift structs)
        Assert.Equal("SBW_Utf8Slice", mapping.CdeclReturnType);
        Assert.Equal(CdeclReturnKind.String, mapping.Kind);
    }

    [Fact]
    public void GetCdeclReturnMapping_Class_Pointer()
    {
        var (_, typeDb) = CreateTestEnvironmentWithExtraTypes("MyType",
            ("TestModule.ChildObj", TypeRecordFlags.None, TypeRecordKind.Class));
        var (mapping, needsPtr) = CdeclReturnMapping.Classify(
            new NamedTypeSpec("TestModule.ChildObj"), typeDb);

        Assert.False(needsPtr);
        Assert.Equal("UnsafeMutableRawPointer", mapping.CdeclReturnType);
        Assert.Equal(CdeclReturnKind.ClassPointer, mapping.Kind);
    }

    [Fact]
    public void GetCdeclReturnMapping_SimpleEnum_RawValueType()
    {
        var (_, typeDb) = CreateTestEnvironmentWithExtraTypes("MyType",
            ("TestModule.ContentMode", TypeRecordFlags.Frozen | TypeRecordFlags.SimpleEnum, TypeRecordKind.Enum, "Swift.Int"));
        var (mapping, needsPtr) = CdeclReturnMapping.Classify(
            new NamedTypeSpec("TestModule.ContentMode"), typeDb);

        Assert.False(needsPtr);
        Assert.Equal("Int", mapping.CdeclReturnType);
        Assert.Equal(CdeclReturnKind.SimpleEnum, mapping.Kind);
    }

    [Fact]
    public void GetCdeclReturnMapping_NonFrozenStruct_NeedsResultPtr()
    {
        var (_, typeDb) = CreateTestEnvironmentWithExtraTypes("MyType",
            ("TestModule.Config", TypeRecordFlags.None, TypeRecordKind.Struct));
        var (mapping, needsPtr) = CdeclReturnMapping.Classify(
            new NamedTypeSpec("TestModule.Config"), typeDb);

        Assert.True(needsPtr);
        Assert.Equal(CdeclReturnKind.IndirectResult, mapping.Kind);
    }

    [Fact]
    public void GetCdeclReturnMapping_ProtocolExistential_NeedsResultPtr()
    {
        // Protocol existentials are not C-representable in @_cdecl — must use indirect result
        var (_, typeDb) = CreateTestEnvironmentWithExtraTypes("MyType",
            ("TestModule.Cacheable", TypeRecordFlags.None, TypeRecordKind.Protocol));
        var protocolListSpec = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.Cacheable") });
        var (mapping, needsPtr) = CdeclReturnMapping.Classify(protocolListSpec, typeDb);

        Assert.True(needsPtr);
        Assert.Equal("Void", mapping.CdeclReturnType);
        Assert.Equal(CdeclReturnKind.IndirectResult, mapping.Kind);
    }

    [Fact]
    public void GetCdeclReturnMapping_ComplexEnum_NeedsResultPtr()
    {
        var (_, typeDb) = CreateTestEnvironmentWithExtraTypes("MyType",
            ("TestModule.ResultType", TypeRecordFlags.None, TypeRecordKind.Enum));
        var (mapping, needsPtr) = CdeclReturnMapping.Classify(
            new NamedTypeSpec("TestModule.ResultType"), typeDb);

        Assert.True(needsPtr);
        Assert.Equal(CdeclReturnKind.IndirectResult, mapping.Kind);
    }

    [Fact]
    public void GetCdeclReturnMapping_GenericOptional_NeedsResultPtr()
    {
        var (_, typeDb) = CreateTestEnvironment("MyType");
        var optionalSpec = new NamedTypeSpec("Swift.Optional");
        optionalSpec.GenericParameters.Add(new NamedTypeSpec("Swift.String"));
        var (mapping, needsPtr) = CdeclReturnMapping.Classify(optionalSpec, typeDb);

        Assert.True(needsPtr);
        Assert.Equal(CdeclReturnKind.IndirectResult, mapping.Kind);
    }

    [Fact]
    public void GetCdeclReturnMapping_FrozenBlittableStruct_NeedsResultPtr()
    {
        // @_cdecl can't return Swift structs (even @frozen ones), so all structs use resultPtr
        var (_, typeDb) = CreateTestEnvironmentWithExtraTypes("MyType",
            ("TestModule.Point", TypeRecordFlags.Frozen, TypeRecordKind.Struct));
        var (mapping, needsPtr) = CdeclReturnMapping.Classify(
            new NamedTypeSpec("TestModule.Point"), typeDb);

        Assert.True(needsPtr);
        Assert.Equal("Void", mapping.CdeclReturnType);
        Assert.Equal(CdeclReturnKind.IndirectResult, mapping.Kind);
    }

    [Fact]
    public void GetCdeclReturnMapping_NSStringTypedef_IndirectResult_NotClassPointer()
    {
        // NSString typedef structs (e.g., CALayerContentsGravity) are registered as kind="class"
        // with ObjCBridged in the XML database, but they are Swift structs wrapping NSString.
        // Unmanaged.passRetained() is invalid for these — must route through IndirectResult.
        var typeDb = new TypeDatabase();
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        typeDb.AddModuleDatabase(swiftModule);

        var quartzModule = new ModuleTypeDatabase("QuartzCore", "/usr/lib/QuartzCore.dylib");
        quartzModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("QuartzCore.CALayerContentsGravity"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("CoreAnimation", "CALayerContentsGravity"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("QuartzCore.CALayerContentsGravity"),
                MetadataAccessor = "$sMa",
                Flags = TypeRecordFlags.ObjCBridged,
                Kind = TypeRecordKind.Class  // XML says kind="class" but it's really a struct typedef
            });
        typeDb.AddModuleDatabase(quartzModule);

        var (mapping, needsPtr) = CdeclReturnMapping.Classify(
            new NamedTypeSpec("QuartzCore.CALayerContentsGravity"), typeDb);

        // Must NOT be ClassPointer (would emit Unmanaged.passRetained which crashes on a struct)
        Assert.True(needsPtr);
        Assert.Equal("Void", mapping.CdeclReturnType);
        Assert.Equal(CdeclReturnKind.IndirectResult, mapping.Kind);
    }

    [Fact]
    public void MethodRequiresIndirectResult_NSStringTypedef_CdeclPropertyWrapper_ReturnsTrue()
    {
        // Verifies that MethodRequiresIndirectResult agrees with GetCdeclReturnMapping
        // for NSString typedef types (kind="class" + ObjCBridged in XML database).
        // Without this check, the class check at line 162 would return false (no indirect result),
        // while the Swift side expects resultPtr — ABI mismatch.
        var typeDb = new TypeDatabase();
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        typeDb.AddModuleDatabase(swiftModule);

        var quartzModule = new ModuleTypeDatabase("QuartzCore", "/usr/lib/QuartzCore.dylib");
        quartzModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("QuartzCore.CALayerContentsGravity"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("CoreAnimation", "CALayerContentsGravity"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("QuartzCore.CALayerContentsGravity"),
                MetadataAccessor = "$sMa",
                Flags = TypeRecordFlags.ObjCBridged,
                Kind = TypeRecordKind.Class
            });
        typeDb.AddModuleDatabase(quartzModule);

        var moduleDecl = new ModuleDecl
        {
            Name = "QuartzCore",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };

        var parentDecl = CreateClassDecl("CALayer", moduleDecl);
        parentDecl.SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("QuartzCore.CALayer");

        var method = new MethodDecl
        {
            Name = "getter:contentsGravity",
            MangledName = "$s_test_getter",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            IsAccessor = true,
            UsesCdeclPropertyWrapper = true,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    SwiftTypeSpec = new NamedTypeSpec("QuartzCore.CALayerContentsGravity"),
                    Name = "_result",
                    PrivateName = "_result",
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        var env = new MethodEnvironment(method, typeDb);
        Assert.True(MarshallingHelpers.MethodRequiresIndirectResult(env));
    }

    [Fact]
    public void MethodRequiresIndirectResult_ExistentialReturn_CdeclPropertyWrapper_ReturnsTrue()
    {
        // Protocol existential returns must use indirect result in @_cdecl wrappers
        // because existential containers are not C-representable.
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithExtraTypes("MyType",
            ("TestModule.Cacheable", TypeRecordFlags.None, TypeRecordKind.Protocol));
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var protocolListSpec = new ProtocolListTypeSpec(new[] { new NamedTypeSpec("TestModule.Cacheable") });

        var method = new MethodDecl
        {
            Name = "getter:cache",
            MangledName = "$s_test_getter_cache",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            IsAccessor = true,
            UsesCdeclPropertyWrapper = true,
            CSSignature = new List<ArgumentDecl>
            {
                new ArgumentDecl
                {
                    SwiftTypeSpec = protocolListSpec,
                    Name = "_result",
                    PrivateName = "_result",
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = null
                }
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        var env = new MethodEnvironment(method, typeDb);
        Assert.True(MarshallingHelpers.MethodRequiresIndirectResult(env));
    }

    [Fact]
    public void GetCdeclReturnMapping_ObjCBridgedStruct_IndirectResult_NotClassPointer()
    {
        // S10: ObjC-bridged/rooted struct types (e.g., PHPickerResult) were incorrectly
        // routed through the ClassPointer path (Unmanaged.passRetained), which crashes
        // because Unmanaged requires a class type. Fix: guard with Kind != Struct.
        var typeDb = new TypeDatabase();
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        typeDb.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.PHPickerResult"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "PHPickerResult"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.PHPickerResult"),
                MetadataAccessor = "$sMa",
                Flags = TypeRecordFlags.ObjCRooted,
                Kind = TypeRecordKind.Struct  // Struct despite being ObjC-rooted
            });
        typeDb.AddModuleDatabase(testModule);

        var (mapping, needsPtr) = CdeclReturnMapping.Classify(
            new NamedTypeSpec("TestModule.PHPickerResult"), typeDb);

        // Must use IndirectResult, NOT ClassPointer
        Assert.True(needsPtr);
        Assert.Equal(CdeclReturnKind.IndirectResult, mapping.Kind);
    }

    [Fact]
    public void GetCdeclReturnMapping_ObjCBridgedClass_StillUsesClassPointer()
    {
        // Non-struct ObjC-bridged types should still use ClassPointer
        var typeDb = new TypeDatabase();
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        typeDb.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.UIImage"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "UIImage"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.UIImage"),
                MetadataAccessor = "$sMa",
                Flags = TypeRecordFlags.ObjCBridged,
                Kind = TypeRecordKind.Class
            });
        typeDb.AddModuleDatabase(testModule);

        var (mapping, needsPtr) = CdeclReturnMapping.Classify(
            new NamedTypeSpec("TestModule.UIImage"), typeDb);

        Assert.False(needsPtr);
        Assert.Equal(CdeclReturnKind.ClassPointer, mapping.Kind);
    }

    #endregion

    #region MethodDecl Flag Tests

    [Fact]
    public void UsesCdeclWrapper_PropertyWrapperSet_ReturnsTrue()
    {
        var method = new MethodDecl
        {
            Name = "getter:count",
            MangledName = "$test",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public,
            UsesCdeclPropertyWrapper = true
        };

        Assert.True(method.UsesCdeclWrapper);
        Assert.False(method.UsesCdeclConstructorWrapper);
    }

    [Fact]
    public void UsesCdeclWrapper_ConstructorWrapperSet_ReturnsTrue()
    {
        var method = new MethodDecl
        {
            Name = "init",
            MangledName = "$test",
            MethodType = MethodType.Instance,
            IsConstructor = true,
            CSSignature = new List<ArgumentDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public,
            UsesCdeclConstructorWrapper = true
        };

        Assert.True(method.UsesCdeclWrapper);
        Assert.False(method.UsesCdeclPropertyWrapper);
    }

    [Fact]
    public void UsesCdeclWrapper_NeitherSet_ReturnsFalse()
    {
        var method = new MethodDecl
        {
            Name = "doSomething",
            MangledName = "$test",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public
        };

        Assert.False(method.UsesCdeclWrapper);
    }

    #endregion

    #region Helper Methods

    private static NamedTypeSpec MakeOptionalSpec(string innerTypeName)
    {
        var optSpec = new NamedTypeSpec("Swift.Optional");
        optSpec.GenericParameters.Add(new NamedTypeSpec(innerTypeName));
        return optSpec;
    }

    private static (SwiftWriter swiftWriter, StringWriter sw, PropertyDecl propertyDecl, MethodEnvironment env, ModuleEmissionContext ctx) CreateGetterTestSetup(
        string propertyName, TypeSpec typeSpec, bool isClass, bool isStatic = false, bool isMainActorIsolated = false)
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        TypeDecl parentDecl = isClass
            ? CreateClassDecl("MyType", moduleDecl)
            : CreateStructDecl("MyType", moduleDecl);
        if (isMainActorIsolated)
            parentDecl.IsMainActorIsolated = true;

        var getterMethod = CreateAccessorMethod($"getter:{propertyName}", isGetter: true, parentDecl, moduleDecl);
        var propertyDecl = new PropertyDecl
        {
            Name = propertyName,
            SwiftTypeSpec = typeSpec,
            HasStorage = true,
            IsStatic = isStatic,
            Accessors = new List<AccessorDecl> { new GetAccessorDecl { Method = getterMethod } },
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl
        };

        var env = new MethodEnvironment(getterMethod, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);

        return (swiftWriter, sw, propertyDecl, env, ctx);
    }

    private static (SwiftWriter swiftWriter, StringWriter sw, PropertyDecl propertyDecl, MethodEnvironment env, ModuleEmissionContext ctx) CreateSetterTestSetup(
        string propertyName, TypeSpec typeSpec, bool isClass, bool isStatic = false, bool isMainActorIsolated = false)
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        TypeDecl parentDecl = isClass
            ? CreateClassDecl("MyType", moduleDecl)
            : CreateStructDecl("MyType", moduleDecl);
        if (isMainActorIsolated)
            parentDecl.IsMainActorIsolated = true;

        var setterMethod = CreateAccessorMethod($"setter:{propertyName}", isGetter: false, parentDecl, moduleDecl);
        var propertyDecl = new PropertyDecl
        {
            Name = propertyName,
            SwiftTypeSpec = typeSpec,
            HasStorage = true,
            IsStatic = isStatic,
            Accessors = new List<AccessorDecl> { new SetAccessorDecl { Method = setterMethod } },
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl
        };

        var env = new MethodEnvironment(setterMethod, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);

        return (swiftWriter, sw, propertyDecl, env, ctx);
    }

    private static (PropertyDecl propertyDecl, MethodEnvironment env) CreatePropertyAndEnv(
        string propertyName, TypeSpec typeSpec, TypeDecl parentDecl, ModuleDecl moduleDecl, TypeDatabase typeDb)
    {
        var getterMethod = CreateAccessorMethod($"getter:{propertyName}", isGetter: true, parentDecl, moduleDecl);
        var propertyDecl = new PropertyDecl
        {
            Name = propertyName,
            SwiftTypeSpec = typeSpec,
            HasStorage = true,
            IsStatic = false,
            Accessors = new List<AccessorDecl> { new GetAccessorDecl { Method = getterMethod } },
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl
        };

        var env = new MethodEnvironment(getterMethod, typeDb);
        return (propertyDecl, env);
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
            Visibility = Visibility.Public
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
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Int64"),
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
        params (string qualifiedName, TypeRecordFlags flags, TypeRecordKind kind, string? rawValueTypeName)[] extraTypes)
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
            SwiftTypeName.FromModuleQualifiedName("Swift.Bool"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Boolean"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Bool"),
                MetadataAccessor = "$sSbMa",
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

        foreach (var (qualifiedName, flags, kind, rawValue) in extraTypes)
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
                    Kind = kind,
                    RawValueTypeName = rawValue
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

    /// <summary>
    /// Overload without rawValueTypeName for convenience.
    /// </summary>
    private static (ModuleDecl moduleDecl, TypeDatabase typeDb) CreateTestEnvironmentWithExtraTypes(
        string typeName,
        params (string qualifiedName, TypeRecordFlags flags, TypeRecordKind kind)[] extraTypes)
    {
        return CreateTestEnvironmentWithExtraTypes(
            typeName,
            extraTypes.Select(t => (t.qualifiedName, t.flags, t.kind, (string?)null)).ToArray());
    }

    #endregion

    #region Optional<reference-type> Guard Tests

    [Fact]
    public void ShouldEmitWrapper_OptionalClassProperty_ReturnsTrue()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithExtraTypes("MyType",
            ("TestModule.MyClass", TypeRecordFlags.None, TypeRecordKind.Class));
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var optionalSpec = new NamedTypeSpec("Swift.Optional");
        optionalSpec.GenericParameters.Add(new NamedTypeSpec("TestModule.MyClass"));
        var (propertyDecl, env) = CreatePropertyAndEnv("child", optionalSpec, parentDecl, moduleDecl, typeDb);

        Assert.True(PropertyWrapperEmitter.ShouldEmitWrapper(propertyDecl, env));
    }

    [Fact]
    public void ShouldEmitWrapper_OptionalObjCBridgedReadOnlyProperty_ReturnsTrue()
    {
        // Getter-only ObjC optional passes — getter side is calling-convention agnostic
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithExtraTypes("MyType",
            ("TestModule.UIImage", TypeRecordFlags.ObjCBridged, TypeRecordKind.Struct));
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var optionalSpec = new NamedTypeSpec("Swift.Optional");
        optionalSpec.GenericParameters.Add(new NamedTypeSpec("TestModule.UIImage"));
        // getter-only property
        var (propertyDecl, env) = CreatePropertyAndEnv("image", optionalSpec, parentDecl, moduleDecl, typeDb);

        Assert.True(PropertyWrapperEmitter.ShouldEmitWrapper(propertyDecl, env));
    }

    [Fact]
    public void ShouldEmitWrapper_OptionalObjCBridgedStructReadWriteProperty_ReturnsTrue()
    {
        // ObjC-bridged structs (e.g., IndexPath) use nullable pointer ABI via
        // Unmanaged<AnyObject> bridge — setter reconstructs via
        // Unmanaged<AnyObject>.fromOpaque($0).takeUnretainedValue() as! T
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithExtraTypes("MyType",
            ("TestModule.IndexPath", TypeRecordFlags.ObjCBridged, TypeRecordKind.Struct));
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var optionalSpec = new NamedTypeSpec("Swift.Optional");
        optionalSpec.GenericParameters.Add(new NamedTypeSpec("TestModule.IndexPath"));

        var getterMethod = CreateAccessorMethod("getter:indexPath", isGetter: true, parentDecl, moduleDecl);
        var setterMethod = CreateAccessorMethod("setter:indexPath", isGetter: false, parentDecl, moduleDecl);
        var propertyDecl = new PropertyDecl
        {
            Name = "indexPath",
            SwiftTypeSpec = optionalSpec,
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
        Assert.True(PropertyWrapperEmitter.ShouldEmitWrapper(propertyDecl, env));
    }

    [Fact]
    public void ShouldEmitWrapper_OptionalValueTypeProperty_ReturnsTrue()
    {
        // Optional<value-type> properties now handled via @_cdecl IndirectResult
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var optionalSpec = new NamedTypeSpec("Swift.Optional");
        optionalSpec.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        var (propertyDecl, env) = CreatePropertyAndEnv("count", optionalSpec, parentDecl, moduleDecl, typeDb);

        Assert.True(PropertyWrapperEmitter.ShouldEmitWrapper(propertyDecl, env));
    }

    [Fact]
    public void ShouldEmitWrapper_OptionalDoubleProperty_ReturnsTrue()
    {
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var optionalSpec = new NamedTypeSpec("Swift.Optional");
        optionalSpec.GenericParameters.Add(new NamedTypeSpec("Swift.Double"));
        var (propertyDecl, env) = CreatePropertyAndEnv("rate", optionalSpec, parentDecl, moduleDecl, typeDb);

        Assert.True(PropertyWrapperEmitter.ShouldEmitWrapper(propertyDecl, env));
    }

    [Fact]
    public void ShouldEmitWrapper_ArrayProperty_ReturnsTrue()
    {
        // Array properties now handled via @_cdecl UnsafeRawPointer transport
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var arraySpec = new NamedTypeSpec("Swift.Array");
        arraySpec.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        var (propertyDecl, env) = CreatePropertyAndEnv("items", arraySpec, parentDecl, moduleDecl, typeDb);

        Assert.True(PropertyWrapperEmitter.ShouldEmitWrapper(propertyDecl, env));
    }

    [Fact]
    public void ShouldEmitWrapper_DictionaryProperty_ReturnsTrue()
    {
        // Dictionary properties now handled via @_cdecl UnsafeRawPointer transport
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var dictSpec = new NamedTypeSpec("Swift.Dictionary");
        dictSpec.GenericParameters.Add(new NamedTypeSpec("Swift.String"));
        dictSpec.GenericParameters.Add(new NamedTypeSpec("Swift.Int"));
        var (propertyDecl, env) = CreatePropertyAndEnv("mapping", dictSpec, parentDecl, moduleDecl, typeDb);

        Assert.True(PropertyWrapperEmitter.ShouldEmitWrapper(propertyDecl, env));
    }

    [Fact]
    public void ShouldEmitWrapper_SetProperty_ReturnsTrue()
    {
        // Set properties now handled via @_cdecl UnsafeRawPointer transport
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var setSpec = new NamedTypeSpec("Swift.Set");
        setSpec.GenericParameters.Add(new NamedTypeSpec("Swift.String"));
        var (propertyDecl, env) = CreatePropertyAndEnv("tags", setSpec, parentDecl, moduleDecl, typeDb);

        Assert.True(PropertyWrapperEmitter.ShouldEmitWrapper(propertyDecl, env));
    }

    [Fact]
    public void ShouldEmitWrapper_OptionalExistentialProperty_ReturnsTrue()
    {
        // Optional<protocol existential> now allowed — getter needs @_cdecl wrapper because
        // Optional<ExistentialContainer> is too large for CallConvSwift register return.
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var protocolList = new ProtocolListTypeSpec(new List<NamedTypeSpec> { new NamedTypeSpec("Swift.Error") });
        var optionalExistential = new NamedTypeSpec("Swift.Optional");
        optionalExistential.GenericParameters.Add(protocolList);
        var (propertyDecl, env) = CreatePropertyAndEnv("error", optionalExistential, parentDecl, moduleDecl, typeDb);

        Assert.True(PropertyWrapperEmitter.ShouldEmitWrapper(propertyDecl, env));
    }

    [Fact]
    public void EmitSwiftSetterWrapper_OptionalClass_NullablePointerABI()
    {
        // Regression test: Optional<ClassType> setter must use nullable pointer ABI
        // (UnsafeMutableRawPointer?) — the inner class is reconstructed via Unmanaged.fromOpaque.
        // C# must pass the actual object pointer or nil, NOT the buffer address.
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithExtraTypes("MyType",
            ("TestModule.Animation", TypeRecordFlags.None, TypeRecordKind.Class));
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var optionalSpec = new NamedTypeSpec("Swift.Optional");
        optionalSpec.GenericParameters.Add(new NamedTypeSpec("TestModule.Animation"));
        var setterMethod = CreateAccessorMethod("setter:animation", isGetter: false, parentDecl, moduleDecl);
        var propertyDecl = new PropertyDecl
        {
            Name = "animation",
            SwiftTypeSpec = optionalSpec,
            HasStorage = true,
            IsStatic = false,
            Accessors = new List<AccessorDecl> { new SetAccessorDecl { Method = setterMethod } },
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl
        };

        var env = new MethodEnvironment(setterMethod, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);

        var symbol = "SBW_Set_TestModule_MyType_animation";
        PropertyWrapperEmitter.EmitSwiftSetterWrapper(swiftWriter, propertyDecl, symbol, env, ctx);

        var output = sw.ToString();
        // Parameter must be nullable pointer (UnsafeMutableRawPointer?)
        Assert.Contains("_ newValue: UnsafeMutableRawPointer?", output);
        // Must reconstruct via Unmanaged map — not .load(as:)
        Assert.Contains("newValue.map { Unmanaged<TestModule.Animation>.fromOpaque($0).takeUnretainedValue() }", output);
        // Self must still be reconstructed
        Assert.Contains("Unmanaged<TestModule.MyType>.fromOpaque(self_).takeUnretainedValue()", output);
        Assert.Contains("obj.animation = newValueVal", output);
    }

    [Fact]
    public void GetCdeclReturnMapping_OptionalClass_OptionalClassPointer()
    {
        // Regression test: Optional<ClassType> returns must use OptionalClassPointer
        // (UnsafeMutableRawPointer?) — NOT IndirectResult.
        var (_, typeDb) = CreateTestEnvironmentWithExtraTypes("MyType",
            ("TestModule.Animation", TypeRecordFlags.None, TypeRecordKind.Class));

        var optionalSpec = new NamedTypeSpec("Swift.Optional");
        optionalSpec.GenericParameters.Add(new NamedTypeSpec("TestModule.Animation"));

        var (mapping, needsResultPtr) = CdeclReturnMapping.Classify(optionalSpec, typeDb);

        Assert.Equal("UnsafeMutableRawPointer?", mapping.CdeclReturnType);
        Assert.Equal(CdeclReturnKind.OptionalClassPointer, mapping.Kind);
        Assert.False(needsResultPtr);
    }

    [Fact]
    public void ShouldEmitWrapper_PerMemberMainActorProperty_ReturnsTrue()
    {
        // Per-member @MainActor on non-actor class is now allowed — synchronous gate lift
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var (propertyDecl, env) = CreatePropertyAndEnv("state", new NamedTypeSpec("Swift.Int"), parentDecl, moduleDecl, typeDb);
        propertyDecl.IsActorIsolated = true;
        propertyDecl.IsMainActorIsolated = true;

        Assert.True(PropertyWrapperEmitter.ShouldEmitWrapper(propertyDecl, env));
    }

    [Fact]
    public void ShouldEmitWrapper_PerMemberCustomActorProperty_ReturnsFalse()
    {
        // Custom global actor (e.g., @ProcessingActor) on property is still blocked
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var (propertyDecl, env) = CreatePropertyAndEnv("state", new NamedTypeSpec("Swift.Int"), parentDecl, moduleDecl, typeDb);
        propertyDecl.IsActorIsolated = true;
        // IsMainActorIsolated stays false — this is a custom actor

        Assert.False(PropertyWrapperEmitter.ShouldEmitWrapper(propertyDecl, env));
    }

    [Fact]
    public void ShouldEmitWrapper_MainActorParent_ReturnsTrue()
    {
        // @MainActor parent types are now allowed — synchronous gate lift
        var (moduleDecl, typeDb) = CreateTestEnvironment("ViewModel");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("ViewModel", moduleDecl);
        parentDecl.IsMainActorIsolated = true;
        var (propertyDecl, env) = CreatePropertyAndEnv("count", new NamedTypeSpec("Swift.Int"), parentDecl, moduleDecl, typeDb);

        Assert.True(PropertyWrapperEmitter.ShouldEmitWrapper(propertyDecl, env));
    }

    [Fact]
    public void ShouldEmitWrapper_CustomActorProperty_ReturnsFalse()
    {
        // Custom actor types still blocked — require async dispatch
        var (moduleDecl, typeDb) = CreateTestEnvironment("Counter");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("Counter", moduleDecl);
        parentDecl.IsActor = true;
        var (propertyDecl, env) = CreatePropertyAndEnv("count", new NamedTypeSpec("Swift.Int"), parentDecl, moduleDecl, typeDb);

        Assert.False(PropertyWrapperEmitter.ShouldEmitWrapper(propertyDecl, env));
    }

    [Fact]
    public void ShouldEmitWrapper_SpiProtectedProperty_ReturnsFalse()
    {
        // Regression test (Issue K): @_spi protected properties — wrapper can't access
        // them without @_spi import
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var (propertyDecl, env) = CreatePropertyAndEnv("errorCode", new NamedTypeSpec("Swift.Int"), parentDecl, moduleDecl, typeDb);
        propertyDecl.IsSpiProtected = true;

        Assert.False(PropertyWrapperEmitter.ShouldEmitWrapper(propertyDecl, env));
    }

    [Fact]
    public void GetRejectionReason_OptionalObjCBridgedStructWithSetter_ReturnsNull()
    {
        // ObjC-bridged struct Optional with setter is NOT rejected — nullable pointer ABI
        // uses Unmanaged<AnyObject> bridge for both getter and setter.
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithExtraTypes("MyType",
            ("TestModule.UIFontWeight", TypeRecordFlags.ObjCBridged, TypeRecordKind.Struct));
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var optionalSpec = new NamedTypeSpec("Swift.Optional");
        optionalSpec.GenericParameters.Add(new NamedTypeSpec("TestModule.UIFontWeight"));

        var getterMethod = CreateAccessorMethod("getter:weight", isGetter: true, parentDecl, moduleDecl);
        var setterMethod = CreateAccessorMethod("setter:weight", isGetter: false, parentDecl, moduleDecl);
        var propertyDecl = new PropertyDecl
        {
            Name = "weight",
            SwiftTypeSpec = optionalSpec,
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
        Assert.Null(PropertyWrapperEmitter.GetRejectionReason(propertyDecl, env));
    }

    [Fact]
    public void GetRejectionReason_OptionalObjCBridgedClassWithSetter_ReturnsNull()
    {
        // ObjC-bridged class Optional with setter is NOT rejected —
        // CdeclParamMapper handles via nullable pointer ABI (UnsafeMutableRawPointer? + Unmanaged).
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithExtraTypes("MyType",
            ("TestModule.UIImage", TypeRecordFlags.ObjCBridged, TypeRecordKind.Class));
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var optionalSpec = new NamedTypeSpec("Swift.Optional");
        optionalSpec.GenericParameters.Add(new NamedTypeSpec("TestModule.UIImage"));

        var getterMethod = CreateAccessorMethod("getter:image", isGetter: true, parentDecl, moduleDecl);
        var setterMethod = CreateAccessorMethod("setter:image", isGetter: false, parentDecl, moduleDecl);
        var propertyDecl = new PropertyDecl
        {
            Name = "image",
            SwiftTypeSpec = optionalSpec,
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
        Assert.Null(PropertyWrapperEmitter.GetRejectionReason(propertyDecl, env));
    }

    [Fact]
    public void EmitSwiftSetterWrapper_OptionalObjCBridged_NullablePointerABI()
    {
        // ObjC-bridged class Optional setter uses UnsafeMutableRawPointer? with Unmanaged reconstruction
        var (moduleDecl, typeDb) = CreateTestEnvironmentWithExtraTypes("MyType",
            ("TestModule.UIImage", TypeRecordFlags.ObjCBridged, TypeRecordKind.Class));
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var optionalSpec = new NamedTypeSpec("Swift.Optional");
        optionalSpec.GenericParameters.Add(new NamedTypeSpec("TestModule.UIImage"));
        var setterMethod = CreateAccessorMethod("setter:image", isGetter: false, parentDecl, moduleDecl);
        var propertyDecl = new PropertyDecl
        {
            Name = "image",
            SwiftTypeSpec = optionalSpec,
            HasStorage = true,
            IsStatic = false,
            Accessors = new List<AccessorDecl> { new SetAccessorDecl { Method = setterMethod } },
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl
        };

        var env = new MethodEnvironment(setterMethod, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);
        var symbol = "SBW_Set_TestModule_MyType_image";

        PropertyWrapperEmitter.EmitSwiftSetterWrapper(swiftWriter, propertyDecl, symbol, env, ctx);

        var output = sw.ToString();
        Assert.Contains("_ newValue: UnsafeMutableRawPointer?", output);
        Assert.Contains("Unmanaged<AnyObject>.fromOpaque($0).takeUnretainedValue() as! TestModule.UIImage", output);
        Assert.Contains("obj.image = newValueVal", output);
    }

    [Fact]
    public void EmitSwiftSetterWrapper_OptionalClosure_FuncPtrAndContext()
    {
        // Optional<closure> setter uses funcPtr + context params with closure adapter
        var (moduleDecl, typeDb) = CreateTestEnvironment("MyType");
        typeDb.AsyncLibraryName = "TestModuleSwiftBindings";

        var parentDecl = CreateClassDecl("MyType", moduleDecl);
        var closureType = new ClosureTypeSpec(
            new TupleTypeSpec(new[] { new NamedTypeSpec("Swift.Int32") }),
            TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));
        var optionalClosureType = new NamedTypeSpec("Swift.Optional");
        optionalClosureType.GenericParameters.Add(closureType);

        var setterMethod = CreateAccessorMethod("setter:onAction", isGetter: false, parentDecl, moduleDecl);
        var propertyDecl = new PropertyDecl
        {
            Name = "onAction",
            SwiftTypeSpec = optionalClosureType,
            HasStorage = true,
            IsStatic = false,
            Accessors = new List<AccessorDecl> { new SetAccessorDecl { Method = setterMethod } },
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl
        };

        var env = new MethodEnvironment(setterMethod, typeDb);
        var ctx = new ModuleEmissionContext();
        var sw = new StringWriter();
        var swiftWriter = new SwiftWriter(sw);
        var symbol = "SBW_Set_TestModule_MyType_onAction";

        PropertyWrapperEmitter.EmitSwiftSetterWrapper(swiftWriter, propertyDecl, symbol, env, ctx);

        var output = sw.ToString();
        // Must have funcPtr + context params
        Assert.Contains("_ newValueFuncPtr: UnsafeMutableRawPointer?", output);
        Assert.Contains("_ newValueContext: UnsafeMutableRawPointer?", output);
        // Must have optional closure adapter
        Assert.Contains("_adapted_newValue", output);
        Assert.Contains("@convention(c)", output);
        // Must assign adapter to property
        Assert.Contains("obj.onAction = _adapted_newValue", output);
    }

    #endregion
}
