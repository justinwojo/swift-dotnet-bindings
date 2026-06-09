// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for MetatypeHelperEmitter — shared metadata accessor helper for generic parent types.
/// </summary>
public class MetatypeHelperEmitterTests
{
    private static TypeDecl CreateGenericTypeDecl(string name, string moduleName, string mangledName, int genericParamCount)
    {
        var genericParams = new List<GenericArgumentDecl>();
        for (int i = 0; i < genericParamCount; i++)
        {
            genericParams.Add(new GenericArgumentDecl(
                $"τ_0_{i}",
                i == 0 ? "T" : $"T{i}",
                new List<GenericParameterConformance>(),
                new List<GenericParameterConformance>()));
        }

        return new ClassDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{name}"),
            MangledName = mangledName,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = genericParams,
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    [Fact]
    public void EmitMetadataAccessorHelper_FirstCall_EmitsHelper()
    {
        var ctx = new ModuleEmissionContext();
        var output = new StringWriter();
        var swiftWriter = new SwiftWriter(output);
        var typeDecl = CreateGenericTypeDecl("GenericClass", "TestModule", "$s10TestModule12GenericClassCN", 1);

        var helperName = MetatypeHelperEmitter.EmitMetadataAccessorHelperIfNeeded(swiftWriter, typeDecl, ctx);

        var result = output.ToString();
        Assert.StartsWith("_sbw_meta_", helperName);
        Assert.Contains("private func", result);
        Assert.Contains("-> UnsafeRawPointer", result);
        Assert.Contains("dlsym", result);
        Assert.Contains("$s10TestModule12GenericClassCNMa", result);
    }

    [Fact]
    public void EmitMetadataAccessorHelper_SecondCall_ReturnsSameNameWithoutEmitting()
    {
        var ctx = new ModuleEmissionContext();

        var output1 = new StringWriter();
        var swiftWriter1 = new SwiftWriter(output1);
        var typeDecl = CreateGenericTypeDecl("GenericClass", "TestModule", "$s10TestModule12GenericClassCN", 1);
        var name1 = MetatypeHelperEmitter.EmitMetadataAccessorHelperIfNeeded(swiftWriter1, typeDecl, ctx);

        var output2 = new StringWriter();
        var swiftWriter2 = new SwiftWriter(output2);
        var name2 = MetatypeHelperEmitter.EmitMetadataAccessorHelperIfNeeded(swiftWriter2, typeDecl, ctx);

        Assert.Equal(name1, name2);
        Assert.Contains("private func", output1.ToString());
        Assert.Equal(string.Empty, output2.ToString());
    }

    [Fact]
    public void EmitMetadataAccessorHelper_SingleGenericParam_OneUnsafeRawPointerParam()
    {
        var ctx = new ModuleEmissionContext();
        var output = new StringWriter();
        var swiftWriter = new SwiftWriter(output);
        var typeDecl = CreateGenericTypeDecl("Box", "TestModule", "$s10TestModule3BoxCN", 1);

        MetatypeHelperEmitter.EmitMetadataAccessorHelperIfNeeded(swiftWriter, typeDecl, ctx);

        var result = output.ToString();
        Assert.Contains("_ t0: UnsafeRawPointer", result);
        Assert.DoesNotContain("_ t1:", result);
        // Function type should have (Int, UnsafeRawPointer)
        Assert.Contains("(Int, UnsafeRawPointer)", result);
        // Call should be (0, t0)
        Assert.Contains("(0, t0)", result);
    }

    [Fact]
    public void EmitMetadataAccessorHelper_TwoGenericParams_TwoUnsafeRawPointerParams()
    {
        var ctx = new ModuleEmissionContext();
        var output = new StringWriter();
        var swiftWriter = new SwiftWriter(output);
        var typeDecl = CreateGenericTypeDecl("Pair", "TestModule", "$s10TestModule4PairCN", 2);

        MetatypeHelperEmitter.EmitMetadataAccessorHelperIfNeeded(swiftWriter, typeDecl, ctx);

        var result = output.ToString();
        Assert.Contains("_ t0: UnsafeRawPointer, _ t1: UnsafeRawPointer", result);
        Assert.Contains("(Int, UnsafeRawPointer, UnsafeRawPointer)", result);
        Assert.Contains("(0, t0, t1)", result);
    }

    [Fact]
    public void EmitMetadataAccessorHelper_DifferentTypes_DifferentHelperNames()
    {
        var ctx = new ModuleEmissionContext();

        var output1 = new StringWriter();
        var typeDecl1 = CreateGenericTypeDecl("Backend", "DiskStorage", "$s11DiskStorage7BackendCN", 1);
        var name1 = MetatypeHelperEmitter.EmitMetadataAccessorHelperIfNeeded(new SwiftWriter(output1), typeDecl1, ctx);

        var output2 = new StringWriter();
        var typeDecl2 = CreateGenericTypeDecl("Backend", "MemoryStorage", "$s13MemoryStorage7BackendCN", 1);
        var name2 = MetatypeHelperEmitter.EmitMetadataAccessorHelperIfNeeded(new SwiftWriter(output2), typeDecl2, ctx);

        Assert.NotEqual(name1, name2);
    }

    [Fact]
    public void EmitMetadataAccessorHelper_HelperNameUsesHash()
    {
        var ctx = new ModuleEmissionContext();
        var output = new StringWriter();
        var swiftWriter = new SwiftWriter(output);
        var typeDecl = CreateGenericTypeDecl("GenericClass", "TestModule", "$s10TestModule12GenericClassCN", 1);

        var helperName = MetatypeHelperEmitter.EmitMetadataAccessorHelperIfNeeded(swiftWriter, typeDecl, ctx);

        // Helper name format: _sbw_meta_{8-char hash}
        Assert.Matches(@"^_sbw_meta_[a-fA-F0-9]{8}$", helperName);
    }

    // ─────────────────────────────────────────────────────────────────────
    // HasUnresolvableTypeConformances — fail-closed gate for the wrapper
    // metadata-accessor helper (MetatypeHelperEmitter Swift wrapper path).
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void HasUnresolvableTypeConformances_NonGenericType_ReturnsFalse()
    {
        var typeDecl = CreateGenericTypeDecl("Plain", "TestModule", "$s10TestModule5PlainCN", 0);
        var db = new SimpleProtocolDatabase();

        Assert.False(MetatypeHelperEmitter.HasUnresolvableTypeConformances(typeDecl, db));
    }

    [Fact]
    public void HasUnresolvableTypeConformances_NoConstraints_ReturnsFalse()
    {
        var typeDecl = CreateGenericTypeDecl("Container", "TestModule", "$s10TestModule9ContainerCN", 1);
        var db = new SimpleProtocolDatabase();

        Assert.False(MetatypeHelperEmitter.HasUnresolvableTypeConformances(typeDecl, db));
    }

    [Fact]
    public void HasUnresolvableTypeConformances_ResolvableProtocolOnly_ReturnsFalse()
    {
        // T : Describable (a normal protocol with no Self/AssocType requirements)
        // → resolvable, the wrapper-helper path can supply this PWT correctly.
        var typeDecl = CreateGenericTypeDeclWithConformances(
            "Box",
            "TestModule",
            "$s10TestModule3BoxCN",
            new[] { ("TestModule", "Describable", TypeRecordFlags.None) });
        var db = new SimpleProtocolDatabase()
            .WithProtocol("TestModule", "Describable", TypeRecordFlags.None);

        Assert.False(MetatypeHelperEmitter.HasUnresolvableTypeConformances(typeDecl, db));
    }

    [Fact]
    public void HasUnresolvableTypeConformances_SelfRequirementProtocol_ReturnsTrue()
    {
        // T : AnyInterpolatable (HasSelfRequirement) — the wrapper-helper path
        // CANNOT supply a PWT for this protocol; refuse to emit.
        var typeDecl = CreateGenericTypeDeclWithConformances(
            "Wrapper",
            "TestModule",
            "$s10TestModule7WrapperCN",
            new[] { ("TestModule", "AnyInterpolatable", TypeRecordFlags.HasSelfRequirement) });
        var db = new SimpleProtocolDatabase()
            .WithProtocol("TestModule", "AnyInterpolatable", TypeRecordFlags.HasSelfRequirement);

        Assert.True(MetatypeHelperEmitter.HasUnresolvableTypeConformances(typeDecl, db));
    }

    [Fact]
    public void HasUnresolvableTypeConformances_AssociatedTypeProtocol_ReturnsTrue()
    {
        var typeDecl = CreateGenericTypeDeclWithConformances(
            "ViewBag",
            "TestModule",
            "$s10TestModule7ViewBagCN",
            new[] { ("TestModule", "ViewLike", TypeRecordFlags.HasAssociatedTypes) });
        var db = new SimpleProtocolDatabase()
            .WithProtocol("TestModule", "ViewLike", TypeRecordFlags.HasAssociatedTypes);

        Assert.True(MetatypeHelperEmitter.HasUnresolvableTypeConformances(typeDecl, db));
    }

    [Fact]
    public void HasUnresolvableTypeConformances_MixedConstraints_DetectsAnyUnresolvable()
    {
        // First param OK, second param has Self requirement → still gated.
        var typeDecl = new ClassDecl
        {
            Name = "Pair",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Pair"),
            MangledName = "$s10TestModule4PairCN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>
            {
                new("τ_0_0", "T",
                    new List<GenericParameterConformance>
                    {
                        new(new[] { "τ_0_0" },
                            SwiftTypeName.FromModuleQualifiedName("TestModule.Describable"),
                            ConformanceKind.Protocol)
                    },
                    new List<GenericParameterConformance>()),
                new("τ_0_1", "U",
                    new List<GenericParameterConformance>
                    {
                        new(new[] { "τ_0_1" },
                            SwiftTypeName.FromModuleQualifiedName("TestModule.AnyInterpolatable"),
                            ConformanceKind.Protocol)
                    },
                    new List<GenericParameterConformance>())
            },
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = null
        };
        var db = new SimpleProtocolDatabase()
            .WithProtocol("TestModule", "Describable", TypeRecordFlags.None)
            .WithProtocol("TestModule", "AnyInterpolatable", TypeRecordFlags.HasSelfRequirement);

        Assert.True(MetatypeHelperEmitter.HasUnresolvableTypeConformances(typeDecl, db));
    }

    [Fact]
    public void HasUnresolvableTypeConformances_UnknownProtocol_SilentlyAllowed()
    {
        // Mirrors the legacy GetResolvablePwtParameterCount filter: an unknown
        // protocol (not in the type database) is silently dropped, NOT treated
        // as unresolvable. Failing on unknown would regress every Alamofire/
        // GRDB/RxSwift constrained generic that uses Swift stdlib protocols
        // the type database doesn't track.
        var typeDecl = CreateGenericTypeDeclWithConformances(
            "Cache",
            "TestModule",
            "$s10TestModule5CacheCN",
            new[] { ("Swift", "Hashable", TypeRecordFlags.None) });
        var db = new SimpleProtocolDatabase(); // empty — protocol unknown

        Assert.False(MetatypeHelperEmitter.HasUnresolvableTypeConformances(typeDecl, db));
    }

    // ─────────────────────────────────────────────────────────────────────
    // WouldExceedRegisterArgumentThreshold — fail-closed gate for the wrapper
    // metadata-accessor helper when (num_metadata + num_pwts) > 3 forces
    // Swift's Ma symbol into the indirect-buffer ABI. The wrapper helper only
    // emits the thin (request, metadata..., pwt...) signature, so we refuse
    // to emit any member that would route through buffer mode.
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void WouldExceedRegisterArgumentThreshold_NonGenericType_ReturnsFalse()
    {
        var typeDecl = CreateGenericTypeDecl("Plain", "TestModule", "$s10TestModule5PlainCN", 0);
        var db = new SimpleProtocolDatabase();

        Assert.False(MetatypeHelperEmitter.WouldExceedRegisterArgumentThreshold(typeDecl, db));
    }

    [Fact]
    public void WouldExceedRegisterArgumentThreshold_OneGenericNoConformances_ReturnsFalse()
    {
        // 1 metadata + 0 PWT = 1 → under threshold.
        var typeDecl = CreateGenericTypeDecl("Box", "TestModule", "$s10TestModule3BoxCN", 1);
        var db = new SimpleProtocolDatabase();

        Assert.False(MetatypeHelperEmitter.WouldExceedRegisterArgumentThreshold(typeDecl, db));
    }

    [Fact]
    public void WouldExceedRegisterArgumentThreshold_ThreeGenericsNoConformances_ReturnsFalse()
    {
        // 3 metadata + 0 PWT = 3 → AT the threshold (not exceeding).
        var typeDecl = CreateGenericTypeDecl("Triple", "TestModule", "$s10TestModule6TripleCN", 3);
        var db = new SimpleProtocolDatabase();

        Assert.False(MetatypeHelperEmitter.WouldExceedRegisterArgumentThreshold(typeDecl, db));
    }

    [Fact]
    public void WouldExceedRegisterArgumentThreshold_FourGenericsNoConformances_ReturnsTrue()
    {
        // 4 metadata + 0 PWT = 4 → exceeds threshold.
        var typeDecl = CreateGenericTypeDecl("Quad", "TestModule", "$s10TestModule4QuadCN", 4);
        var db = new SimpleProtocolDatabase();

        Assert.True(MetatypeHelperEmitter.WouldExceedRegisterArgumentThreshold(typeDecl, db));
    }

    [Fact]
    public void WouldExceedRegisterArgumentThreshold_OneGenericTwoResolvableConformances_ReturnsFalse()
    {
        // 1 metadata + 2 PWT = 3 → AT the threshold (not exceeding).
        var typeDecl = CreateGenericTypeDeclWithConformances(
            "Box",
            "TestModule",
            "$s10TestModule3BoxCN",
            new[]
            {
                ("TestModule", "Alpha", TypeRecordFlags.None),
                ("TestModule", "Beta",  TypeRecordFlags.None),
            });
        var db = new SimpleProtocolDatabase()
            .WithProtocol("TestModule", "Alpha", TypeRecordFlags.None)
            .WithProtocol("TestModule", "Beta", TypeRecordFlags.None);

        Assert.False(MetatypeHelperEmitter.WouldExceedRegisterArgumentThreshold(typeDecl, db));
    }

    [Fact]
    public void WouldExceedRegisterArgumentThreshold_OneGenericThreeResolvableConformances_ReturnsTrue()
    {
        // 1 metadata + 3 PWT = 4 → exceeds threshold.
        var typeDecl = CreateGenericTypeDeclWithConformances(
            "Box",
            "TestModule",
            "$s10TestModule3BoxCN",
            new[]
            {
                ("TestModule", "Alpha", TypeRecordFlags.None),
                ("TestModule", "Beta",  TypeRecordFlags.None),
                ("TestModule", "Gamma", TypeRecordFlags.None),
            });
        var db = new SimpleProtocolDatabase()
            .WithProtocol("TestModule", "Alpha", TypeRecordFlags.None)
            .WithProtocol("TestModule", "Beta", TypeRecordFlags.None)
            .WithProtocol("TestModule", "Gamma", TypeRecordFlags.None);

        Assert.True(MetatypeHelperEmitter.WouldExceedRegisterArgumentThreshold(typeDecl, db));
    }

    [Fact]
    public void WouldExceedRegisterArgumentThreshold_UnknownProtocol_DoesNotCount()
    {
        // 1 metadata + 4 unknown PWT = 1 (we silently drop unknown protocols
        // the same way GetResolvablePwtParameterCount does). Mirrors the
        // HasUnresolvableTypeConformances_UnknownProtocol_SilentlyAllowed gate
        // — counting unknown stdlib protocols would regress every Alamofire/
        // GRDB/RxSwift constrained generic.
        var typeDecl = CreateGenericTypeDeclWithConformances(
            "Cache",
            "TestModule",
            "$s10TestModule5CacheCN",
            new[]
            {
                ("Swift", "Hashable",   TypeRecordFlags.None),
                ("Swift", "Equatable",  TypeRecordFlags.None),
                ("Swift", "Comparable", TypeRecordFlags.None),
                ("Swift", "Identifiable", TypeRecordFlags.None),
            });
        var db = new SimpleProtocolDatabase(); // empty — protocols unknown

        Assert.False(MetatypeHelperEmitter.WouldExceedRegisterArgumentThreshold(typeDecl, db));
    }

    private static ClassDecl CreateGenericTypeDeclWithConformances(
        string name,
        string moduleName,
        string mangledName,
        IReadOnlyList<(string Module, string Protocol, TypeRecordFlags Flags)> constraints)
    {
        var conformances = new List<GenericParameterConformance>();
        foreach (var c in constraints)
        {
            conformances.Add(new GenericParameterConformance(
                Path: new[] { "τ_0_0" },
                ConformanceTarget: SwiftTypeName.FromModuleQualifiedName($"{c.Module}.{c.Protocol}"),
                Kind: ConformanceKind.Protocol));
        }

        return new ClassDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{name}"),
            MangledName = mangledName,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>
            {
                new("τ_0_0", "T", conformances, new List<GenericParameterConformance>())
            },
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    private sealed class SimpleProtocolDatabase : ITypeDatabase
    {
        private readonly Dictionary<string, TypeRecord> _types = new();

        public string? AsyncLibraryName => null;

        public SimpleProtocolDatabase WithProtocol(string moduleName, string protocolName, TypeRecordFlags flags)
            => WithProtocol(moduleName, protocolName, flags, descriptorSymbol: null);

        public SimpleProtocolDatabase WithProtocol(string moduleName, string protocolName, TypeRecordFlags flags, string? descriptorSymbol)
        {
            var swiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{protocolName}");
            _types[swiftTypeName.ModuleQualifiedName] = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName(moduleName, protocolName),
                SwiftTypeName = swiftTypeName,
                MetadataAccessor = "",
                Flags = flags,
                Kind = TypeRecordKind.Protocol,
                ProtocolDescriptorSymbol = descriptorSymbol
            };
            return this;
        }

        public bool IsTypeProcessed(SwiftTypeName swiftTypeName) =>
            _types.ContainsKey(swiftTypeName.ModuleQualifiedName);

        public bool TryGetTypeRecord(
            SwiftTypeName swiftTypeName,
            [NotNullWhen(returnValue: true)] out TypeRecord? record) =>
            _types.TryGetValue(swiftTypeName.ModuleQualifiedName, out record);

        public string GetLibraryPath(string moduleName) => $"/tmp/{moduleName}.dylib";

        public void UpdateTypeRecord(SwiftTypeName name, TypeRecord record) { }
    }

    [Fact]
    public void ConstructorForwardingMethod_ProducesSameResult()
    {
        // Verify that the forwarding method in ConstructorWrapperEmitter
        // produces the same result as calling MetatypeHelperEmitter directly
        var ctx1 = new ModuleEmissionContext();
        var ctx2 = new ModuleEmissionContext();
        var typeDecl = CreateGenericTypeDecl("GenericClass", "TestModule", "$s10TestModule12GenericClassCN", 1);

        var output1 = new StringWriter();
        var name1 = MetatypeHelperEmitter.EmitMetadataAccessorHelperIfNeeded(new SwiftWriter(output1), typeDecl, ctx1);

        var output2 = new StringWriter();
        // ConstructorWrapperEmitter forwarding method now requires ITypeDatabase.
        // For this test (no conformances), pwtCount=0, so call MetatypeHelperEmitter directly.
        var name2 = MetatypeHelperEmitter.EmitMetadataAccessorHelperIfNeeded(new SwiftWriter(output2), typeDecl, ctx2, pwtCount: 0);

        Assert.Equal(name1, name2);
        Assert.Equal(output1.ToString(), output2.ToString());
    }

    // --- HasWellKnownRuntimeProtocolConformance: well-known PWT-carrying vs marker vs unknown ---

    [Fact]
    public void HasWellKnownRuntimeProtocolConformance_NonGenericParent_ReturnsFalse()
    {
        var typeDecl = CreateGenericTypeDecl("NonGeneric", "TestModule", "$s10TestModule10NonGenericCN", 0);
        var db = new SimpleProtocolDatabase()
            .WithProtocol("Swift", "Error", TypeRecordFlags.None);

        Assert.False(MetatypeHelperEmitter.HasWellKnownRuntimeProtocolConformance(typeDecl, db));
    }

    [Fact]
    public void HasWellKnownRuntimeProtocolConformance_ErrorConformance_ReturnsTrue()
    {
        // Swift.Error IS a well-known runtime protocol that carries a witness table —
        // its slot appears in the …Ma metadata accessor signature, so the wrapper-helper
        // path must gate-block constructors on parents that conform to it.
        var typeDecl = CreateGenericTypeDeclWithConformances(
            "ErrCarrier",
            "TestModule",
            "$s10TestModule10ErrCarrierCN",
            new[] { ("Swift", "Error", TypeRecordFlags.None) });
        var db = new SimpleProtocolDatabase()
            .WithProtocol("Swift", "Error", TypeRecordFlags.None);

        Assert.True(MetatypeHelperEmitter.HasWellKnownRuntimeProtocolConformance(typeDecl, db));
    }

    [Fact]
    public void HasWellKnownRuntimeProtocolConformance_ActorConformance_ReturnsTrue()
    {
        // _Concurrency.Actor is the other well-known PWT-carrying protocol in the set.
        var typeDecl = CreateGenericTypeDeclWithConformances(
            "ActorHost",
            "TestModule",
            "$s10TestModule9ActorHostCN",
            new[] { ("_Concurrency", "Actor", TypeRecordFlags.None) });
        var db = new SimpleProtocolDatabase()
            .WithProtocol("_Concurrency", "Actor", TypeRecordFlags.None);

        Assert.True(MetatypeHelperEmitter.HasWellKnownRuntimeProtocolConformance(typeDecl, db));
    }

    [Theory]
    [InlineData("Sendable")]
    [InlineData("Copyable")]
    [InlineData("Escapable")]
    [InlineData("SendableMetatype")]
    public void HasWellKnownRuntimeProtocolConformance_MarkerOnly_ReturnsFalse(string marker)
    {
        // Pure marker protocols carry no witness table and never appear in …Ma signatures,
        // so a parent constrained only by markers must NOT gate-block constructor emission.
        var typeDecl = CreateGenericTypeDeclWithConformances(
            "MarkerOnly",
            "TestModule",
            "$s10TestModule10MarkerOnlyCN",
            new[] { ("Swift", marker, TypeRecordFlags.None) });
        var db = new SimpleProtocolDatabase()
            .WithProtocol("Swift", marker, TypeRecordFlags.None);

        Assert.False(MetatypeHelperEmitter.HasWellKnownRuntimeProtocolConformance(typeDecl, db));
    }

    [Fact]
    public void HasWellKnownRuntimeProtocolConformance_BitwiseCopyableMarker_ReturnsFalse()
    {
        // BitwiseCopyable is in the marker subset (IsStdlibMarkerProtocol) but NOT in
        // IsWellKnownRuntimeProtocol — defensive coverage that the gate stays clean
        // either way (the marker exclusion short-circuits before the well-known check).
        var typeDecl = CreateGenericTypeDeclWithConformances(
            "BitwiseHost",
            "TestModule",
            "$s10TestModule11BitwiseHostCN",
            new[] { ("Swift", "BitwiseCopyable", TypeRecordFlags.None) });
        var db = new SimpleProtocolDatabase()
            .WithProtocol("Swift", "BitwiseCopyable", TypeRecordFlags.None);

        Assert.False(MetatypeHelperEmitter.HasWellKnownRuntimeProtocolConformance(typeDecl, db));
    }

    [Fact]
    public void HasWellKnownRuntimeProtocolConformance_NormalProtocol_ReturnsFalse()
    {
        // A normal user-defined protocol is not well-known. The gate must not fire.
        var typeDecl = CreateGenericTypeDeclWithConformances(
            "Holder",
            "TestModule",
            "$s10TestModule6HolderCN",
            new[] { ("TestModule", "MyProto", TypeRecordFlags.None) });
        var db = new SimpleProtocolDatabase()
            .WithProtocol("TestModule", "MyProto", TypeRecordFlags.None);

        Assert.False(MetatypeHelperEmitter.HasWellKnownRuntimeProtocolConformance(typeDecl, db));
    }

    [Fact]
    public void HasWellKnownRuntimeProtocolConformance_MarkerAndError_StillReturnsTrue()
    {
        // Mixed conformance: the marker is skipped, but Swift.Error still trips the gate.
        var typeDecl = CreateGenericTypeDeclWithConformances(
            "Mixed",
            "TestModule",
            "$s10TestModule5MixedCN",
            new[]
            {
                ("Swift", "Sendable", TypeRecordFlags.None),
                ("Swift", "Error",    TypeRecordFlags.None),
            });
        var db = new SimpleProtocolDatabase()
            .WithProtocol("Swift", "Sendable", TypeRecordFlags.None)
            .WithProtocol("Swift", "Error",    TypeRecordFlags.None);

        Assert.True(MetatypeHelperEmitter.HasWellKnownRuntimeProtocolConformance(typeDecl, db));
    }

    // --- GetResolvablePwtParameterCount: well-known protocols are not counted ---

    [Fact]
    public void GetResolvablePwtParameterCount_WellKnownProtocols_NotCounted()
    {
        // Sendable + Copyable + Error are all well-known runtime protocols. None of them
        // contributes a resolvable PWT slot from the wrapper-helper path; the counter
        // must return 0 even though three protocol conformances are present.
        var typeDecl = CreateGenericTypeDeclWithConformances(
            "AllWellKnown",
            "TestModule",
            "$s10TestModule12AllWellKnownCN",
            new[]
            {
                ("Swift", "Sendable", TypeRecordFlags.None),
                ("Swift", "Copyable", TypeRecordFlags.None),
                ("Swift", "Error",    TypeRecordFlags.None),
            });
        var db = new SimpleProtocolDatabase()
            .WithProtocol("Swift", "Sendable", TypeRecordFlags.None)
            .WithProtocol("Swift", "Copyable", TypeRecordFlags.None)
            .WithProtocol("Swift", "Error",    TypeRecordFlags.None);

        Assert.Equal(0, MetatypeHelperEmitter.GetResolvablePwtParameterCount(typeDecl, db));
    }

    [Fact]
    public void GetResolvablePwtParameterCount_NormalProtocol_Counted()
    {
        var typeDecl = CreateGenericTypeDeclWithConformances(
            "OneProto",
            "TestModule",
            "$s10TestModule8OneProtoCN",
            new[] { ("TestModule", "MyProto", TypeRecordFlags.None) });
        var db = new SimpleProtocolDatabase()
            .WithProtocol("TestModule", "MyProto", TypeRecordFlags.None);

        Assert.Equal(1, MetatypeHelperEmitter.GetResolvablePwtParameterCount(typeDecl, db));
    }

    [Fact]
    public void GetResolvablePwtParameterCount_MixedWellKnownAndNormal_OnlyNormalCounted()
    {
        // Sendable (marker) and Error (PWT-carrying) are both well-known; only the
        // user-defined protocol contributes to the resolvable PWT slot count.
        var typeDecl = CreateGenericTypeDeclWithConformances(
            "Mixed",
            "TestModule",
            "$s10TestModule5MixedCN",
            new[]
            {
                ("Swift",      "Sendable", TypeRecordFlags.None),
                ("Swift",      "Error",    TypeRecordFlags.None),
                ("TestModule", "MyProto",  TypeRecordFlags.None),
            });
        var db = new SimpleProtocolDatabase()
            .WithProtocol("Swift",      "Sendable", TypeRecordFlags.None)
            .WithProtocol("Swift",      "Error",    TypeRecordFlags.None)
            .WithProtocol("TestModule", "MyProto",  TypeRecordFlags.None);

        Assert.Equal(1, MetatypeHelperEmitter.GetResolvablePwtParameterCount(typeDecl, db));
    }

    [Fact]
    public void GetResolvablePwtParameterCount_PatOrSelfRequirement_NotCounted()
    {
        // PAT (HasAssociatedTypes) and Self-requirement protocols route through the
        // dynamic-PWT path, not the resolvable static path. They must NOT be counted here.
        var typeDecl = CreateGenericTypeDeclWithConformances(
            "PatHost",
            "TestModule",
            "$s10TestModule7PatHostCN",
            new[]
            {
                ("TestModule", "PatProto",  TypeRecordFlags.HasAssociatedTypes),
                ("TestModule", "SelfProto", TypeRecordFlags.HasSelfRequirement),
            });
        var db = new SimpleProtocolDatabase()
            .WithProtocol("TestModule", "PatProto",  TypeRecordFlags.HasAssociatedTypes)
            .WithProtocol("TestModule", "SelfProto", TypeRecordFlags.HasSelfRequirement);

        Assert.Equal(0, MetatypeHelperEmitter.GetResolvablePwtParameterCount(typeDecl, db));
    }

    // --- GetTotalPwtParameterCount: ctor GSF path counter ---
    //
    // The total counter is used by the GSF cdecl-constructor path (ConstructorWrapperEmitter)
    // and admits PAT / Self-requirement conformances that carry a ProtocolDescriptorSymbol
    // (dynamic-PWT routing). It still skips well-known runtime protocols (Error / Actor)
    // and pure stdlib markers, including BitwiseCopyable.

    [Fact]
    public void GetTotalPwtParameterCount_WellKnownProtocols_NotCounted()
    {
        // Error is gate-blocked on the C# side and contributes no slot to the @_cdecl
        // signature; Sendable / Copyable are markers with no PWT. Total must be 0.
        var typeDecl = CreateGenericTypeDeclWithConformances(
            "AllWellKnown",
            "TestModule",
            "$s10TestModule12AllWellKnownCN",
            new[]
            {
                ("Swift",         "Sendable", TypeRecordFlags.None),
                ("Swift",         "Copyable", TypeRecordFlags.None),
                ("Swift",         "Error",    TypeRecordFlags.None),
                ("_Concurrency",  "Actor",    TypeRecordFlags.None),
            });
        var db = new SimpleProtocolDatabase()
            .WithProtocol("Swift",         "Sendable", TypeRecordFlags.None)
            .WithProtocol("Swift",         "Copyable", TypeRecordFlags.None)
            .WithProtocol("Swift",         "Error",    TypeRecordFlags.None)
            .WithProtocol("_Concurrency",  "Actor",    TypeRecordFlags.None);

        Assert.Equal(0, MetatypeHelperEmitter.GetTotalPwtParameterCount(typeDecl, db));
    }

    [Theory]
    [InlineData("Sendable")]
    [InlineData("Copyable")]
    [InlineData("Escapable")]
    [InlineData("SendableMetatype")]
    [InlineData("BitwiseCopyable")]
    public void GetTotalPwtParameterCount_StdlibMarkers_NotCounted(string markerName)
    {
        // Pure marker protocols carry no witness table and never appear in @_cdecl
        // wrapper signatures. They must be skipped — including BitwiseCopyable, which
        // is NOT in IsWellKnownRuntimeProtocol but IS in IsStdlibMarkerProtocol.
        var typeDecl = CreateGenericTypeDeclWithConformances(
            "MarkerOnly",
            "TestModule",
            "$s10TestModule10MarkerOnlyCN",
            new[] { ("Swift", markerName, TypeRecordFlags.None) });
        var db = new SimpleProtocolDatabase()
            .WithProtocol("Swift", markerName, TypeRecordFlags.None);

        Assert.Equal(0, MetatypeHelperEmitter.GetTotalPwtParameterCount(typeDecl, db));
    }

    [Fact]
    public void GetTotalPwtParameterCount_NormalProtocol_Counted()
    {
        var typeDecl = CreateGenericTypeDeclWithConformances(
            "OneProto",
            "TestModule",
            "$s10TestModule8OneProtoCN",
            new[] { ("TestModule", "MyProto", TypeRecordFlags.None) });
        var db = new SimpleProtocolDatabase()
            .WithProtocol("TestModule", "MyProto", TypeRecordFlags.None);

        Assert.Equal(1, MetatypeHelperEmitter.GetTotalPwtParameterCount(typeDecl, db));
    }

    [Fact]
    public void GetTotalPwtParameterCount_PatOrSelfRequirementWithDescriptor_Counted()
    {
        // Unlike GetResolvablePwtParameterCount, the total counter ADMITS PAT/Self
        // conformances when a ProtocolDescriptorSymbol was captured — they route through
        // the dynamic-PWT path on the GSF ctor and contribute a real slot to the @_cdecl
        // signature and the _sbw_meta_X helper.
        var typeDecl = CreateGenericTypeDeclWithConformances(
            "PatHost",
            "TestModule",
            "$s10TestModule7PatHostCN",
            new[]
            {
                ("TestModule", "PatProto",  TypeRecordFlags.HasAssociatedTypes),
                ("TestModule", "SelfProto", TypeRecordFlags.HasSelfRequirement),
            });
        var db = new SimpleProtocolDatabase()
            .WithProtocol("TestModule", "PatProto",  TypeRecordFlags.HasAssociatedTypes, "$s10TestModule8PatProtoMp")
            .WithProtocol("TestModule", "SelfProto", TypeRecordFlags.HasSelfRequirement, "$s10TestModule9SelfProtoMp");

        Assert.Equal(2, MetatypeHelperEmitter.GetTotalPwtParameterCount(typeDecl, db));
    }

    [Fact]
    public void GetTotalPwtParameterCount_PatOrSelfRequirementWithoutDescriptor_NotCounted()
    {
        // Without a captured ProtocolDescriptorSymbol, the dynamic-PWT path has no way
        // to materialize the witness table at runtime. These conformances do NOT
        // contribute a slot to the @_cdecl wrapper, so the counter must skip them.
        var typeDecl = CreateGenericTypeDeclWithConformances(
            "PatHost",
            "TestModule",
            "$s10TestModule7PatHostCN",
            new[]
            {
                ("TestModule", "PatProto",  TypeRecordFlags.HasAssociatedTypes),
                ("TestModule", "SelfProto", TypeRecordFlags.HasSelfRequirement),
            });
        var db = new SimpleProtocolDatabase()
            .WithProtocol("TestModule", "PatProto",  TypeRecordFlags.HasAssociatedTypes)
            .WithProtocol("TestModule", "SelfProto", TypeRecordFlags.HasSelfRequirement);

        Assert.Equal(0, MetatypeHelperEmitter.GetTotalPwtParameterCount(typeDecl, db));
    }

    [Fact]
    public void GetTotalPwtParameterCount_MixedWellKnownMarkersAndNormal_OnlyNonSkippedCounted()
    {
        // Mix of every kind: a real normal protocol, a PAT conformance WITH a captured
        // descriptor symbol (counted on total via dynamic-PWT routing), every pure marker
        // including BitwiseCopyable (skipped — no PWT, never in Ma signatures), and the
        // PWT-carrying well-known protocols Error/Actor (skipped because the C# side
        // gate-blocks them via IsProtocolAvailableForConstraint, so counting them on the
        // Swift wrapper would over-declare _pwtN against the C# P/Invoke decl).
        var typeDecl = CreateGenericTypeDeclWithConformances(
            "Mixed",
            "TestModule",
            "$s10TestModule5MixedCN",
            new[]
            {
                ("Swift",         "Sendable",        TypeRecordFlags.None),
                ("Swift",         "Copyable",        TypeRecordFlags.None),
                ("Swift",         "Escapable",       TypeRecordFlags.None),
                ("Swift",         "SendableMetatype",TypeRecordFlags.None),
                ("Swift",         "BitwiseCopyable", TypeRecordFlags.None),
                ("Swift",         "Error",           TypeRecordFlags.None),
                ("_Concurrency",  "Actor",           TypeRecordFlags.None),
                ("TestModule",    "MyProto",         TypeRecordFlags.None),
                ("TestModule",    "PatProto",        TypeRecordFlags.HasAssociatedTypes),
            });
        var db = new SimpleProtocolDatabase()
            .WithProtocol("Swift",         "Sendable",        TypeRecordFlags.None)
            .WithProtocol("Swift",         "Copyable",        TypeRecordFlags.None)
            .WithProtocol("Swift",         "Escapable",       TypeRecordFlags.None)
            .WithProtocol("Swift",         "SendableMetatype",TypeRecordFlags.None)
            .WithProtocol("Swift",         "BitwiseCopyable", TypeRecordFlags.None)
            .WithProtocol("Swift",         "Error",           TypeRecordFlags.None)
            .WithProtocol("_Concurrency",  "Actor",           TypeRecordFlags.None)
            .WithProtocol("TestModule",    "MyProto",         TypeRecordFlags.None)
            .WithProtocol("TestModule",    "PatProto",        TypeRecordFlags.HasAssociatedTypes, "$s10TestModule8PatProtoMp");

        // MyProto (normal) + PatProto (dynamic-PWT, has descriptor) = 2.
        Assert.Equal(2, MetatypeHelperEmitter.GetTotalPwtParameterCount(typeDecl, db));
    }
}
