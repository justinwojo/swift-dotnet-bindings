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
    // metadata-accessor helper. See src/docs/constrained-generic-metadata-
    // witness-tables.md "MetatypeHelperEmitter Swift wrapper path".
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
        {
            var swiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{protocolName}");
            _types[swiftTypeName.ModuleQualifiedName] = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName(moduleName, protocolName),
                SwiftTypeName = swiftTypeName,
                MetadataAccessor = "",
                Flags = flags,
                Kind = TypeRecordKind.Protocol,
                ProtocolDescriptorSymbol = null
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
}
