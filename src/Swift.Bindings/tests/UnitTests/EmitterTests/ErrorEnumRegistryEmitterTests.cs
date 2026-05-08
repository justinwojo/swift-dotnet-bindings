// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Phase 4 Layer 1 — verifies the per-module error-type registry built by
/// <see cref="ErrorEnumRegistryEmitter"/>. The registry feeds the wire-format
/// extension, Swift cascade helper, and C# typed-exception dispatcher emitted
/// by subsequent layers, so its determinism (alphabetical ordering, idempotent
/// precompute) is load-bearing.
/// </summary>
public class ErrorEnumRegistryEmitterTests
{
    [Fact]
    public void Precompute_EnumConformingToSwiftError_RegisteredWithId1()
    {
        var moduleDecl = BuildModule();
        moduleDecl.Types.Add(BuildErrorEnum(moduleDecl, "WeatherError", "Swift.Error"));

        var ctx = new ModuleEmissionContext();
        ErrorEnumRegistryEmitter.Precompute(moduleDecl, ctx);

        Assert.True(ctx.TryGetErrorTypeId("TestModule.WeatherError", out var id));
        Assert.Equal(1, id); // id 0 is reserved for "untyped"
    }

    [Fact]
    public void Precompute_MultipleErrorEnums_AssignedAlphabeticalIds()
    {
        // Source order: Zebra, Apple, Mango. Registered order must be alphabetical
        // for cross-run determinism (the C# Dictionary<int, Type> + Swift cascade
        // emit consume ErrorTypeOrder, which mirrors id assignment).
        var moduleDecl = BuildModule();
        moduleDecl.Types.Add(BuildErrorEnum(moduleDecl, "ZebraError", "Swift.Error"));
        moduleDecl.Types.Add(BuildErrorEnum(moduleDecl, "AppleError", "Swift.Error"));
        moduleDecl.Types.Add(BuildErrorEnum(moduleDecl, "MangoError", "Swift.Error"));

        var ctx = new ModuleEmissionContext();
        ErrorEnumRegistryEmitter.Precompute(moduleDecl, ctx);

        Assert.True(ctx.TryGetErrorTypeId("TestModule.AppleError", out var appleId));
        Assert.True(ctx.TryGetErrorTypeId("TestModule.MangoError", out var mangoId));
        Assert.True(ctx.TryGetErrorTypeId("TestModule.ZebraError", out var zebraId));
        Assert.Equal(1, appleId);
        Assert.Equal(2, mangoId);
        Assert.Equal(3, zebraId);
        Assert.Equal(
            new[] { "TestModule.AppleError", "TestModule.MangoError", "TestModule.ZebraError" },
            ctx.ErrorTypeOrder);
    }

    [Fact]
    public void Precompute_StructConformingToError_Registered()
    {
        var moduleDecl = BuildModule();
        var structDecl = new StructDecl
        {
            Name = "MyStructError",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.MyStructError"),
            MangledName = "",
            IsFrozen = false,
            GenericParameters = new(),
            Properties = new(),
            Methods = new(),
            Types = new(),
            Operators = new(),
            Subscripts = new(),
            Conformances = new()
            {
                new TypeConformance(
                    ConformingType: SwiftTypeName.FromModuleQualifiedName("TestModule.MyStructError"),
                    Protocol: SwiftTypeName.FromModuleQualifiedName("Swift.Error"),
                    ProtocolConformanceDescriptor: ""),
            },
            MetadataAccessor = "",
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
        };
        moduleDecl.Types.Add(structDecl);

        var ctx = new ModuleEmissionContext();
        ErrorEnumRegistryEmitter.Precompute(moduleDecl, ctx);

        Assert.True(ctx.TryGetErrorTypeId("TestModule.MyStructError", out _));
    }

    [Fact]
    public void Precompute_ClassConformingToError_Registered()
    {
        var moduleDecl = BuildModule();
        var classDecl = new ClassDecl
        {
            Name = "MyClassError",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.MyClassError"),
            MangledName = "",
            IsFinal = true,
            GenericParameters = new(),
            Properties = new(),
            Methods = new(),
            Types = new(),
            Operators = new(),
            Subscripts = new(),
            Conformances = new()
            {
                new TypeConformance(
                    ConformingType: SwiftTypeName.FromModuleQualifiedName("TestModule.MyClassError"),
                    Protocol: SwiftTypeName.FromModuleQualifiedName("Swift.Error"),
                    ProtocolConformanceDescriptor: ""),
            },
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
        };
        moduleDecl.Types.Add(classDecl);

        var ctx = new ModuleEmissionContext();
        ErrorEnumRegistryEmitter.Precompute(moduleDecl, ctx);

        Assert.True(ctx.TryGetErrorTypeId("TestModule.MyClassError", out _));
    }

    [Fact]
    public void Precompute_CaselessNamespaceEnumWithLocalizedError_NotRegistered()
    {
        // WeatherKit-shaped pattern: a caseless namespace enum (used as a container for
        // `static let` constants) that conforms to LocalizedError via extension. The C#
        // emission projects a caseless enum as a `static class`, which can't be a generic
        // type argument and can't be cast to. The cascade dispatcher would emit
        // SwiftException<StaticClass> code that fails to compile, so the registry must
        // skip these and let the untyped SwiftException fallback handle them at runtime.
        var moduleDecl = BuildModule();
        var caselessEnum = new EnumDecl
        {
            Name = "WeatherErrorNamespace",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.WeatherErrorNamespace"),
            MangledName = "",
            IsFrozen = false,
            Cases = new(), // <-- Caseless: zero cases.
            GenericParameters = new(),
            Properties = new(),
            Methods = new(),
            Types = new(),
            Operators = new(),
            Subscripts = new(),
            Conformances = new()
            {
                new TypeConformance(
                    ConformingType: SwiftTypeName.FromModuleQualifiedName("TestModule.WeatherErrorNamespace"),
                    Protocol: SwiftTypeName.FromModuleQualifiedName("Foundation.LocalizedError"),
                    ProtocolConformanceDescriptor: ""),
            },
            MetadataAccessor = "",
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
        };
        moduleDecl.Types.Add(caselessEnum);

        var ctx = new ModuleEmissionContext();
        ErrorEnumRegistryEmitter.Precompute(moduleDecl, ctx);

        Assert.False(ctx.TryGetErrorTypeId("TestModule.WeatherErrorNamespace", out _));
        Assert.Empty(ctx.ErrorTypeOrder);
    }

    [Fact]
    public void Precompute_NonErrorEnum_NotRegistered()
    {
        var moduleDecl = BuildModule();
        moduleDecl.Types.Add(BuildErrorEnum(moduleDecl, "Color", "Swift.Equatable"));

        var ctx = new ModuleEmissionContext();
        ErrorEnumRegistryEmitter.Precompute(moduleDecl, ctx);

        Assert.False(ctx.TryGetErrorTypeId("TestModule.Color", out _));
        Assert.Empty(ctx.ErrorTypeOrder);
    }

    [Fact]
    public void Precompute_FoundationLocalizedError_Detected()
    {
        // WeatherKit's WeatherError conforms to Foundation.LocalizedError (which
        // refines Swift.Error). The parser may surface only the LocalizedError side
        // depending on swiftinterface flavor, so the detector must accept it
        // independently of the bare Swift.Error conformance.
        var moduleDecl = BuildModule();
        moduleDecl.Types.Add(BuildErrorEnum(moduleDecl, "WeatherError", "Foundation.LocalizedError"));

        var ctx = new ModuleEmissionContext();
        ErrorEnumRegistryEmitter.Precompute(moduleDecl, ctx);

        Assert.True(ctx.TryGetErrorTypeId("TestModule.WeatherError", out _));
    }

    [Fact]
    public void Precompute_NestedErrorEnum_Registered()
    {
        // Swift idiom: extension SomeService { public enum FetchError: Error { ... } }
        // The nested type must be registered with its module-qualified path.
        var moduleDecl = BuildModule();
        var outerStruct = new StructDecl
        {
            Name = "SomeService",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.SomeService"),
            MangledName = "",
            IsFrozen = false,
            GenericParameters = new(),
            Properties = new(),
            Methods = new(),
            Types = new(),
            Operators = new(),
            Subscripts = new(),
            Conformances = new(),
            MetadataAccessor = "",
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
        };
        var nestedEnum = new EnumDecl
        {
            Name = "FetchError",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.SomeService.FetchError"),
            MangledName = "",
            IsFrozen = true,
            // At least one case — caseless enums are filtered out as
            // namespace-only types by the registry.
            Cases = new()
            {
                new EnumCaseDecl
                {
                    Name = "notFound",
                    MangledName = "",
                    ParentDecl = null,
                    ModuleDecl = moduleDecl,
                },
            },
            GenericParameters = new(),
            Properties = new(),
            Methods = new(),
            Types = new(),
            Operators = new(),
            Subscripts = new(),
            Conformances = new()
            {
                new TypeConformance(
                    ConformingType: SwiftTypeName.FromModuleQualifiedName("TestModule.SomeService.FetchError"),
                    Protocol: SwiftTypeName.FromModuleQualifiedName("Swift.Error"),
                    ProtocolConformanceDescriptor: ""),
            },
            MetadataAccessor = "",
            ParentDecl = outerStruct,
            ModuleDecl = moduleDecl,
        };
        outerStruct.Types.Add(nestedEnum);
        moduleDecl.Types.Add(outerStruct);

        var ctx = new ModuleEmissionContext();
        ErrorEnumRegistryEmitter.Precompute(moduleDecl, ctx);

        Assert.True(ctx.TryGetErrorTypeId("TestModule.SomeService.FetchError", out var id));
        Assert.Equal(1, id);
        // Outer non-error struct was not registered.
        Assert.False(ctx.TryGetErrorTypeId("TestModule.SomeService", out _));
    }

    [Fact]
    public void Precompute_SpiProtectedErrorType_NotRegistered()
    {
        // @_spi types are suppressed by HandleBaseDecl (IHandler.cs:226) and never reach
        // the C# emitter. Registering one in the cascade dispatcher would emit code that
        // references a type the C# binding never declares (CS0234). Falls through to the
        // untyped SwiftException default branch — the correct degradation.
        var moduleDecl = BuildModule();
        var spiError = BuildErrorEnum(moduleDecl, "AttestationError", "Swift.Error");
        spiError.IsSpiProtected = true;
        moduleDecl.Types.Add(spiError);

        var ctx = new ModuleEmissionContext();
        ErrorEnumRegistryEmitter.Precompute(moduleDecl, ctx);

        Assert.False(ctx.TryGetErrorTypeId("TestModule.AttestationError", out _));
        Assert.Empty(ctx.ErrorTypeOrder);
    }

    [Fact]
    public void Precompute_ModuleInternalErrorType_NotRegistered()
    {
        // `@usableFromInline internal` types DO get C# bindings (HandleBaseDecl emits
        // them so they're referenceable when they appear in public signatures of
        // @inlinable functions — see IHandler.cs:222-225). But the Swift cascade
        // dispatcher operates from the wrapper module, which only sees `public`
        // declarations through plain `import Module`; an `as? Module.InternalType`
        // in the wrapper produces "module 'X' has no member named Y" / "no type
        // named Y in module 'X'" (real-world example: CryptoSwift's
        // `@usableFromInline internal class StreamDecryptor.Error`).
        var moduleDecl = BuildModule();
        var internalError = BuildErrorEnum(moduleDecl, "StreamDecryptorError", "Swift.Error");
        internalError.IsModuleInternal = true;
        moduleDecl.Types.Add(internalError);

        var ctx = new ModuleEmissionContext();
        ErrorEnumRegistryEmitter.Precompute(moduleDecl, ctx);

        Assert.False(ctx.TryGetErrorTypeId("TestModule.StreamDecryptorError", out _));
        Assert.Empty(ctx.ErrorTypeOrder);
    }

    [Fact]
    public void Precompute_NestedErrorInModuleInternalParent_NotRegistered()
    {
        // Same parent-chain principle as the SPI/underscore-suppressed cases: an
        // `@usableFromInline internal` parent class hides any nested types from the
        // wrapper module's import-time visibility, so a publicly-spelled nested error
        // inside it cannot appear in the cascade dispatcher either. Mirrors the
        // CryptoSwift `StreamDecryptor.Error` shape directly.
        var moduleDecl = BuildModule();
        var internalOuter = BuildOuterStruct(moduleDecl, "StreamDecryptor");
        internalOuter.IsModuleInternal = true;
        var nestedError = BuildNestedErrorEnum(moduleDecl, internalOuter, "Error", "unsupported");
        internalOuter.Types.Add(nestedError);
        moduleDecl.Types.Add(internalOuter);

        var ctx = new ModuleEmissionContext();
        ErrorEnumRegistryEmitter.Precompute(moduleDecl, ctx);

        Assert.False(ctx.TryGetErrorTypeId("TestModule.StreamDecryptor.Error", out _));
        Assert.Empty(ctx.ErrorTypeOrder);
    }

    [Fact]
    public void Precompute_UnderscoreSuppressedErrorType_NotRegistered()
    {
        // Underscore-prefixed types not structurally required are suppressed before any
        // handler dispatches. The cascade dispatcher would emit a CS0234 reference if
        // it kept their registry id.
        var moduleDecl = BuildModule();
        var underscoreError = BuildErrorEnum(moduleDecl, "_InternalError", "Swift.Error");
        moduleDecl.Types.Add(underscoreError);

        var ctx = new ModuleEmissionContext();
        ctx.SetUnderscoreSuppressedNames(new HashSet<string>(StringComparer.Ordinal)
        {
            underscoreError.SwiftTypeName.ToString(),
        });
        ErrorEnumRegistryEmitter.Precompute(moduleDecl, ctx);

        Assert.False(ctx.TryGetErrorTypeId("TestModule._InternalError", out _));
        Assert.Empty(ctx.ErrorTypeOrder);
    }

    [Fact]
    public void Precompute_NestedInGenericParent_NotRegistered()
    {
        // Reproduces the Alamofire / RealityFoundation regression shape:
        // `DecodableWebSocketMessageDecoder<TValue>.Error` (and the equivalent
        // `FromToByAction<TValue>.DecodingErrors`). The cascade dispatcher renders
        // module-qualified names verbatim (`global::Module.Outer.Inner`) and has no
        // way to synthesize a closed type argument for an open generic parent at
        // precompute time, so these entries must drop out and the cascade fall through
        // to untyped SwiftException at runtime.
        var moduleDecl = BuildModule();
        var genericOuter = new StructDecl
        {
            Name = "DecodableWebSocketMessageDecoder",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.DecodableWebSocketMessageDecoder"),
            MangledName = "",
            IsFrozen = false,
            GenericParameters = new()
            {
                new GenericArgumentDecl(
                    TypeName: "TValue",
                    SugaredTypeName: "TValue",
                    GenericConformances: new(),
                    AssosiatedTypeConformances: new()),
            },
            Properties = new(),
            Methods = new(),
            Types = new(),
            Operators = new(),
            Subscripts = new(),
            Conformances = new(),
            MetadataAccessor = "",
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
        };
        var nestedError = new EnumDecl
        {
            Name = "Error",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.DecodableWebSocketMessageDecoder.Error"),
            MangledName = "",
            IsFrozen = true,
            Cases = new()
            {
                new EnumCaseDecl
                {
                    Name = "decoding",
                    MangledName = "",
                    ParentDecl = null,
                    ModuleDecl = moduleDecl,
                },
            },
            GenericParameters = new(),
            Properties = new(),
            Methods = new(),
            Types = new(),
            Operators = new(),
            Subscripts = new(),
            Conformances = new()
            {
                new TypeConformance(
                    ConformingType: SwiftTypeName.FromModuleQualifiedName("TestModule.DecodableWebSocketMessageDecoder.Error"),
                    Protocol: SwiftTypeName.FromModuleQualifiedName("Swift.Error"),
                    ProtocolConformanceDescriptor: ""),
            },
            MetadataAccessor = "",
            ParentDecl = genericOuter,
            ModuleDecl = moduleDecl,
        };
        genericOuter.Types.Add(nestedError);
        moduleDecl.Types.Add(genericOuter);

        var ctx = new ModuleEmissionContext();
        ErrorEnumRegistryEmitter.Precompute(moduleDecl, ctx);

        Assert.False(ctx.TryGetErrorTypeId("TestModule.DecodableWebSocketMessageDecoder.Error", out _));
        Assert.Empty(ctx.ErrorTypeOrder);
    }

    [Fact]
    public void Precompute_OpenGenericErrorType_NotRegistered()
    {
        // A generic struct that itself conforms to Error (e.g. `struct FromToByAction<TValue>: Error`)
        // would similarly require a generic dispatcher; drop it from the registry.
        var moduleDecl = BuildModule();
        var genericError = new StructDecl
        {
            Name = "FromToByAction",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.FromToByAction"),
            MangledName = "",
            IsFrozen = false,
            GenericParameters = new()
            {
                new GenericArgumentDecl(
                    TypeName: "TValue",
                    SugaredTypeName: "TValue",
                    GenericConformances: new(),
                    AssosiatedTypeConformances: new()),
            },
            Properties = new(),
            Methods = new(),
            Types = new(),
            Operators = new(),
            Subscripts = new(),
            Conformances = new()
            {
                new TypeConformance(
                    ConformingType: SwiftTypeName.FromModuleQualifiedName("TestModule.FromToByAction"),
                    Protocol: SwiftTypeName.FromModuleQualifiedName("Swift.Error"),
                    ProtocolConformanceDescriptor: ""),
            },
            MetadataAccessor = "",
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
        };
        moduleDecl.Types.Add(genericError);

        var ctx = new ModuleEmissionContext();
        ErrorEnumRegistryEmitter.Precompute(moduleDecl, ctx);

        Assert.False(ctx.TryGetErrorTypeId("TestModule.FromToByAction", out _));
        Assert.Empty(ctx.ErrorTypeOrder);
    }

    [Fact]
    public void Precompute_RecursionDescendsThroughNonErrorParent_FindsEmittableNested()
    {
        // Recursion into nested types must NOT be gated by the parent being itself an
        // error type. A public non-error outer struct (e.g. PhotogrammetrySession) can
        // legitimately hold a public, emittable nested error type — both the parent
        // (skipped from the registry because it's not an error) and the child (registered
        // because it is) flow through HandleBaseDecl's normal emission path.
        var moduleDecl = BuildModule();
        var nonErrorOuter = BuildOuterStruct(moduleDecl, "PhotogrammetrySession");
        var nestedError = BuildNestedErrorEnum(moduleDecl, nonErrorOuter, "Error", "ioError");
        nonErrorOuter.Types.Add(nestedError);
        moduleDecl.Types.Add(nonErrorOuter);

        var ctx = new ModuleEmissionContext();
        ErrorEnumRegistryEmitter.Precompute(moduleDecl, ctx);

        Assert.True(ctx.TryGetErrorTypeId("TestModule.PhotogrammetrySession.Error", out var id));
        Assert.Equal(1, id);
    }

    [Fact]
    public void Precompute_NestedErrorInSpiParent_NotRegistered()
    {
        // HandleBaseDecl skips the entire subtree of an @_spi parent — the parent's C#
        // decl is never written, and its nested types are emitted as nested members of
        // a missing decl, so they vanish too. The cascade dispatcher must therefore drop
        // any nested error inside an SPI parent, even when the nested error itself is
        // public. Without this, the dispatcher emits `Module.SpiOuter.PublicError` and
        // the C# build fails with CS0234 because `SpiOuter` is not present.
        var moduleDecl = BuildModule();
        var spiOuter = BuildOuterStruct(moduleDecl, "SpiOuter");
        spiOuter.IsSpiProtected = true;
        var nestedError = BuildNestedErrorEnum(moduleDecl, spiOuter, "Error", "denied");
        spiOuter.Types.Add(nestedError);
        moduleDecl.Types.Add(spiOuter);

        var ctx = new ModuleEmissionContext();
        ErrorEnumRegistryEmitter.Precompute(moduleDecl, ctx);

        Assert.False(ctx.TryGetErrorTypeId("TestModule.SpiOuter.Error", out _));
        Assert.Empty(ctx.ErrorTypeOrder);
    }

    [Fact]
    public void Precompute_NestedErrorInUnderscoreSuppressedParent_NotRegistered()
    {
        // Same shape as the SPI-parent case but for the underscore-prefix gate: the
        // outer `_HiddenInfra` is suppressed by HandleBaseDecl before any nested type
        // is emitted, so the cascade dispatcher must not register `Module._HiddenInfra.Error`
        // either.
        var moduleDecl = BuildModule();
        var hiddenOuter = BuildOuterStruct(moduleDecl, "_HiddenInfra");
        var nestedError = BuildNestedErrorEnum(moduleDecl, hiddenOuter, "Error", "boom");
        hiddenOuter.Types.Add(nestedError);
        moduleDecl.Types.Add(hiddenOuter);

        var ctx = new ModuleEmissionContext();
        ctx.SetUnderscoreSuppressedNames(new HashSet<string>(StringComparer.Ordinal)
        {
            hiddenOuter.SwiftTypeName.ToString(),
        });
        ErrorEnumRegistryEmitter.Precompute(moduleDecl, ctx);

        Assert.False(ctx.TryGetErrorTypeId("TestModule._HiddenInfra.Error", out _));
        Assert.Empty(ctx.ErrorTypeOrder);
    }

    [Fact]
    public void Precompute_Idempotent_SecondCallNoOp()
    {
        var moduleDecl = BuildModule();
        moduleDecl.Types.Add(BuildErrorEnum(moduleDecl, "WeatherError", "Swift.Error"));

        var ctx = new ModuleEmissionContext();
        ErrorEnumRegistryEmitter.Precompute(moduleDecl, ctx);
        // Add another error AFTER the first precompute; second call must not re-walk.
        moduleDecl.Types.Add(BuildErrorEnum(moduleDecl, "LateError", "Swift.Error"));
        ErrorEnumRegistryEmitter.Precompute(moduleDecl, ctx);

        Assert.True(ctx.TryGetErrorTypeId("TestModule.WeatherError", out _));
        Assert.False(ctx.TryGetErrorTypeId("TestModule.LateError", out _));
        Assert.True(ctx.ErrorTypeRegistryComputed);
    }

    [Fact]
    public void RegisterErrorTypeId_DuplicateRegistration_ReturnsExistingId()
    {
        var ctx = new ModuleEmissionContext();
        var firstId = ctx.RegisterErrorTypeId("TestModule.SomeError");
        var secondId = ctx.RegisterErrorTypeId("TestModule.SomeError");

        Assert.Equal(1, firstId);
        Assert.Equal(firstId, secondId);
        Assert.Single(ctx.ErrorTypeIds);
    }

    [Fact]
    public void ConformsToError_ProtocolDecl_ReturnsFalse()
    {
        // Protocols themselves go through moduleDecl.Protocols, not Types.
        // ConformsToError is shape-gated to concrete-type decls only — a
        // ProtocolDecl reaching this helper would return false because it has no
        // Conformances surface in the EnumDecl/StructDecl/ClassDecl sense.
        var protocolDecl = new ProtocolDecl
        {
            Name = "MyErrorProtocol",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.MyErrorProtocol"),
            MangledName = "",
            GenericParameters = new(),
            Properties = new(),
            Methods = new(),
            Types = new(),
            Operators = new(),
            Subscripts = new(),
            ParentDecl = null,
            ModuleDecl = null,
        };
        Assert.False(ErrorEnumRegistryEmitter.ConformsToError(protocolDecl));
    }

    private static ModuleDecl BuildModule(string name = "TestModule") => new()
    {
        Name = name,
        ParentDecl = null,
        ModuleDecl = null,
        Properties = new(),
        Methods = new(),
        Types = new(),
        Dependencies = new(),
        Protocols = new(),
    };

    private static StructDecl BuildOuterStruct(ModuleDecl moduleDecl, string name)
    {
        var swiftName = SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{name}");
        return new StructDecl
        {
            Name = name,
            SwiftTypeName = swiftName,
            MangledName = "",
            IsFrozen = false,
            GenericParameters = new(),
            Properties = new(),
            Methods = new(),
            Types = new(),
            Operators = new(),
            Subscripts = new(),
            Conformances = new(),
            MetadataAccessor = "",
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
        };
    }

    private static EnumDecl BuildNestedErrorEnum(ModuleDecl moduleDecl, TypeDecl parent, string name, string caseName)
    {
        var swiftName = SwiftTypeName.FromModuleQualifiedName(
            $"{parent.SwiftTypeName.ModuleQualifiedName}.{name}");
        return new EnumDecl
        {
            Name = name,
            SwiftTypeName = swiftName,
            MangledName = "",
            IsFrozen = true,
            Cases = new()
            {
                new EnumCaseDecl
                {
                    Name = caseName,
                    MangledName = "",
                    ParentDecl = null,
                    ModuleDecl = moduleDecl,
                },
            },
            GenericParameters = new(),
            Properties = new(),
            Methods = new(),
            Types = new(),
            Operators = new(),
            Subscripts = new(),
            Conformances = new()
            {
                new TypeConformance(
                    ConformingType: swiftName,
                    Protocol: SwiftTypeName.FromModuleQualifiedName("Swift.Error"),
                    ProtocolConformanceDescriptor: ""),
            },
            MetadataAccessor = "",
            ParentDecl = parent,
            ModuleDecl = moduleDecl,
        };
    }

    private static EnumDecl BuildErrorEnum(ModuleDecl moduleDecl, string name, string protocolModuleQualifiedName)
    {
        var swiftName = SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{name}");
        return new EnumDecl
        {
            Name = name,
            SwiftTypeName = swiftName,
            MangledName = "",
            IsFrozen = true,
            // At least one case so the registry's caseless-namespace filter
            // (IsInstantiable) treats this as a real error enum, not a
            // WeatherKit-shaped static namespace.
            Cases = new()
            {
                new EnumCaseDecl
                {
                    Name = "someCase",
                    MangledName = "",
                    ParentDecl = null,
                    ModuleDecl = moduleDecl,
                },
            },
            GenericParameters = new(),
            Properties = new(),
            Methods = new(),
            Types = new(),
            Operators = new(),
            Subscripts = new(),
            Conformances = new()
            {
                new TypeConformance(
                    ConformingType: swiftName,
                    Protocol: SwiftTypeName.FromModuleQualifiedName(protocolModuleQualifiedName),
                    ProtocolConformanceDescriptor: ""),
            },
            MetadataAccessor = "",
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
        };
    }

}
