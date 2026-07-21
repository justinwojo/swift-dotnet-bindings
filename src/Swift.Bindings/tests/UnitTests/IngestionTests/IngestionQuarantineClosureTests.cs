// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

// ---------------------------------------------------------------------------------------------
// The proven-closure withdrawal walk over ingestion-quarantined types. Asserts the disposition
// policy: a malformed type is withdrawn whole; every retained inheritance/conformance/stored-field
// edge is withdrawn whole; every retained signature edge is withdrawn as a leaf so healthy siblings
// survive; and a residual reachable only through a type-level generic constraint fails the module
// closed rather than shipping an unprovable closure.
// ---------------------------------------------------------------------------------------------
public class IngestionQuarantineClosureTests
{
    private const string Module = "IngestionBridge";

    [Fact]
    public void NoQuarantine_ReturnsEmptyProvenResult()
    {
        var module = MakeModule();
        var healthy = MakeStruct("Healthy");
        AttachTypes(module, healthy);

        InputResolutionReport.Reset();
        var result = IngestionQuarantineClosure.Compute(module, Module, NullLogger.Instance);

        Assert.True(result.ProvenComplete);
        Assert.Empty(result.Withdrawals);
        Assert.Null(result.UnprovenReason);
        Assert.Empty(InputResolutionReport.Ledger);
    }

    [Fact]
    public void QuarantinedType_WithdrawnAtTypeSurface_AndProven()
    {
        var module = MakeModule();
        var quarantined = MakeStruct("QuarantinedPayload", quarantined: true);
        AttachTypes(module, quarantined);

        InputResolutionReport.Reset();
        var result = IngestionQuarantineClosure.Compute(module, Module, NullLogger.Instance);

        Assert.True(result.ProvenComplete);
        Assert.Contains(
            RecoveryUnitId.Create(DeclIdFactory.ForType(quarantined), RecoveryScope.TypeSurface),
            result.Withdrawals);
    }

    [Fact]
    public void FreeFunctionReachingQuarantinedType_WithdrawnAsLeaf_HealthySiblingSurvives()
    {
        var module = MakeModule();
        var quarantined = MakeStruct("QuarantinedPayload", quarantined: true);
        var makeQ = MakeFreeFunc("makeQuarantinedPayload", returns: "QuarantinedPayload");
        var inspectQ = MakeFreeFunc("inspectQuarantined", returns: "Int", paramTypes: new[] { "QuarantinedPayload" });
        var makeHealthy = MakeFreeFunc("makeHealthyControl", returns: "HealthyControl");
        AttachTypes(module, quarantined);
        AttachMethods(module, makeQ, inspectQ, makeHealthy);

        InputResolutionReport.Reset();
        var result = IngestionQuarantineClosure.Compute(module, Module, NullLogger.Instance);

        Assert.True(result.ProvenComplete);
        Assert.Contains(
            RecoveryUnitId.Create(DeclIdFactory.ForMethod(makeQ), RecoveryScope.LeafApi),
            result.Withdrawals);
        Assert.Contains(
            RecoveryUnitId.Create(DeclIdFactory.ForMethod(inspectQ), RecoveryScope.LeafApi),
            result.Withdrawals);
        // The healthy control free function touches no withdrawn type and must survive untouched.
        Assert.DoesNotContain(
            RecoveryUnitId.Create(DeclIdFactory.ForMethod(makeHealthy), RecoveryScope.LeafApi),
            result.Withdrawals);
    }

    [Fact]
    public void FreeFunctionWithdrawal_LedgersDegradePlaneQuarantineDisposition()
    {
        var module = MakeModule();
        var quarantined = MakeStruct("QuarantinedPayload", quarantined: true);
        var makeQ = MakeFreeFunc("makeQuarantinedPayload", returns: "QuarantinedPayload");
        AttachTypes(module, quarantined);
        AttachMethods(module, makeQ);

        InputResolutionReport.Reset();
        _ = IngestionQuarantineClosure.Compute(module, Module, NullLogger.Instance);

        // The quarantined type itself is ledgered by the parser, not here; the walk ledgers the
        // withdrawn DEPENDENT, on the Degrade plane, as a proven quarantine naming the malformed root.
        var entry = Assert.Single(InputResolutionReport.Ledger);
        Assert.Equal(IngestionPlane.Degrade, entry.Plane);
        Assert.Equal(IngestionCause.MalformedTypeRecord, entry.Cause);
        Assert.Equal(IngestionDisposition.QuarantineType, entry.Disposition);
        Assert.Equal(IngestionStatus.Quarantined, entry.Status);
        // The dependent's identity carries its own mangled symbol; the ledger names the malformed root.
        Assert.Contains("makeQuarantinedPayload", entry.Input.Symbol);
        Assert.Contains("QuarantinedPayload", entry.Referenced ?? string.Empty);
    }

    [Fact]
    public void StoredFieldEdge_WithdrawsWholeOwningType()
    {
        var module = MakeModule();
        var quarantined = MakeStruct("QuarantinedPayload", quarantined: true);
        var wrapper = MakeStruct("Wrapper", storedFields: new[] { ("field", "QuarantinedPayload") });
        AttachTypes(module, quarantined, wrapper);

        InputResolutionReport.Reset();
        var result = IngestionQuarantineClosure.Compute(module, Module, NullLogger.Instance);

        Assert.True(result.ProvenComplete);
        Assert.Contains(
            RecoveryUnitId.Create(DeclIdFactory.ForType(wrapper), RecoveryScope.TypeSurface),
            result.Withdrawals);
    }

    [Fact]
    public void StoredFieldWithdrawal_LedgersReferencedNamesWithdrawnFieldType()
    {
        // A type withdrawn ONLY because a stored field embeds the withdrawn type must record which type
        // it reaches on the ledger's Referenced field — the layout edge that made it indeterminate. The
        // evidence walk previously covered superclass/conformance/enum-payload but not stored fields, so
        // this row read an anonymous '?'; a consumer of the degraded binding could not tell which field
        // poisoned the type.
        var module = MakeModule();
        var quarantined = MakeStruct("QuarantinedPayload", quarantined: true);
        var wrapper = MakeStruct("Wrapper", storedFields: new[] { ("field", "QuarantinedPayload") });
        AttachTypes(module, quarantined, wrapper);

        InputResolutionReport.Reset();
        var result = IngestionQuarantineClosure.Compute(module, Module, NullLogger.Instance);

        Assert.True(result.ProvenComplete);
        var entry = Assert.Single(InputResolutionReport.Ledger);
        Assert.Contains("Wrapper", entry.Input.Symbol);
        Assert.Equal("QuarantinedPayload", entry.Referenced);
        Assert.NotEqual("?", entry.Referenced);
    }

    [Fact]
    public void SuperclassEdge_WithdrawsWholeDerivedClass()
    {
        var module = MakeModule();
        var quarantinedBase = MakeClass("QuarantinedBase", quarantined: true);
        var derived = MakeClass("Derived", superclassNames: new[] { "QuarantinedBase" });
        AttachTypes(module, quarantinedBase, derived);

        InputResolutionReport.Reset();
        var result = IngestionQuarantineClosure.Compute(module, Module, NullLogger.Instance);

        Assert.True(result.ProvenComplete);
        Assert.Contains(
            RecoveryUnitId.Create(DeclIdFactory.ForType(derived), RecoveryScope.TypeSurface),
            result.Withdrawals);
    }

    [Fact]
    public void ConformanceEdge_WithdrawsWholeConformingType()
    {
        var module = MakeModule();
        var quarantinedProto = MakeProtocol("QuarantinedProtocol", quarantined: true);
        var conformer = MakeStruct("Conformer", conformances: new[] { "QuarantinedProtocol" });
        AttachTypes(module, quarantinedProto, conformer);

        InputResolutionReport.Reset();
        var result = IngestionQuarantineClosure.Compute(module, Module, NullLogger.Instance);

        Assert.True(result.ProvenComplete);
        Assert.Contains(
            RecoveryUnitId.Create(DeclIdFactory.ForType(conformer), RecoveryScope.TypeSurface),
            result.Withdrawals);
    }

    [Fact]
    public void StoredFieldChain_WithdrawsTransitivelyToFixpoint()
    {
        var module = MakeModule();
        var quarantined = MakeStruct("QuarantinedPayload", quarantined: true);
        var mid = MakeStruct("Mid", storedFields: new[] { ("q", "QuarantinedPayload") });
        var outer = MakeStruct("Outer", storedFields: new[] { ("m", "Mid") });
        AttachTypes(module, quarantined, mid, outer);

        InputResolutionReport.Reset();
        var result = IngestionQuarantineClosure.Compute(module, Module, NullLogger.Instance);

        Assert.True(result.ProvenComplete);
        Assert.Contains(
            RecoveryUnitId.Create(DeclIdFactory.ForType(mid), RecoveryScope.TypeSurface),
            result.Withdrawals);
        Assert.Contains(
            RecoveryUnitId.Create(DeclIdFactory.ForType(outer), RecoveryScope.TypeSurface),
            result.Withdrawals);
    }

    [Fact]
    public void RetainedMethodReachingQuarantinedType_WithdrawnAsLeaf_TypeSurvives()
    {
        var module = MakeModule();
        var quarantined = MakeStruct("QuarantinedPayload", quarantined: true);
        var touching = MakeMethod("touch", returns: "QuarantinedPayload");
        var host = MakeStruct("Host", methods: new[] { touching });
        AttachTypes(module, quarantined, host);

        InputResolutionReport.Reset();
        var result = IngestionQuarantineClosure.Compute(module, Module, NullLogger.Instance);

        Assert.True(result.ProvenComplete);
        // The member is withdrawn as a leaf; the host type is NOT withdrawn whole.
        Assert.Contains(
            RecoveryUnitId.Create(DeclIdFactory.ForMethod(touching), RecoveryScope.LeafApi),
            result.Withdrawals);
        Assert.DoesNotContain(
            RecoveryUnitId.Create(DeclIdFactory.ForType(host), RecoveryScope.TypeSurface),
            result.Withdrawals);
    }

    [Fact]
    public void TypeLevelGenericConstraintOnQuarantinedType_FailsClosed()
    {
        var module = MakeModule();
        var quarantinedProto = MakeProtocol("QuarantinedProtocol", quarantined: true);
        // struct Box<T> where T: QuarantinedProtocol — a constraint no leaf/whole-type withdrawal here
        // models, so the closure cannot be proven complete and the module must fail closed.
        var box = MakeStruct("Box", genericConstraintOn: "QuarantinedProtocol");
        AttachTypes(module, quarantinedProto, box);

        InputResolutionReport.Reset();
        var result = IngestionQuarantineClosure.Compute(module, Module, NullLogger.Instance);

        Assert.False(result.ProvenComplete);
        Assert.NotNull(result.UnprovenReason);
        Assert.Contains("generic where-clause", result.UnprovenReason!);
    }

    [Fact]
    public void EnumWithAssociatedValuePayloadReachingQuarantinedType_WithdrawnWhole_AndProven()
    {
        var module = MakeModule();
        var quarantined = MakeStruct("QuarantinedPayload", quarantined: true);
        // enum Carrier { case box(QuarantinedPayload) } — the payload IS the enum's in-line layout, so a
        // withdrawn payload makes the whole enum indeterminate: it must be withdrawn whole, not left with
        // a case that lowers against a withheld type.
        var carrier = MakeEnum("Carrier", payloadCases: new[] { ("box", "QuarantinedPayload") });
        var healthyEnum = MakeEnum("Plain", payloadCases: new[] { ("ok", "Int") });
        AttachTypes(module, quarantined, carrier, healthyEnum);

        InputResolutionReport.Reset();
        var result = IngestionQuarantineClosure.Compute(module, Module, NullLogger.Instance);

        Assert.True(result.ProvenComplete);
        Assert.Contains(
            RecoveryUnitId.Create(DeclIdFactory.ForType(carrier), RecoveryScope.TypeSurface),
            result.Withdrawals);
        // An enum whose payloads touch no withdrawn type survives untouched.
        Assert.DoesNotContain(
            RecoveryUnitId.Create(DeclIdFactory.ForType(healthyEnum), RecoveryScope.TypeSurface),
            result.Withdrawals);
    }

    [Fact]
    public void OperatorReachingQuarantinedType_WithdrawnAsLeaf_HostTypeSurvives()
    {
        var module = MakeModule();
        var quarantined = MakeStruct("QuarantinedPayload", quarantined: true);
        // static func == (l: Host, r: QuarantinedPayload) -> Bool — an operand names the withdrawn type.
        // The operator emitter gates on DeclIdFactory.ForOperator, so it is genuinely leaf-withdrawable;
        // the host struct itself has no structural edge and must survive.
        var op = MakeOperator("==", underlyingParamTypes: new[] { "Host", "QuarantinedPayload" }, returns: "Bool");
        var host = MakeStruct("Host", operators: new[] { op });
        AttachTypes(module, quarantined, host);

        InputResolutionReport.Reset();
        var result = IngestionQuarantineClosure.Compute(module, Module, NullLogger.Instance);

        Assert.True(result.ProvenComplete);
        Assert.Contains(
            RecoveryUnitId.Create(DeclIdFactory.ForOperator(op), RecoveryScope.LeafApi),
            result.Withdrawals);
        Assert.DoesNotContain(
            RecoveryUnitId.Create(DeclIdFactory.ForType(host), RecoveryScope.TypeSurface),
            result.Withdrawals);
    }

    // ---- Cross-module dependency-quarantine seeding (Fix A) --------------------------------
    // A protocol/type quarantined in a DEPENDENCY module is withheld from this module's type
    // database, so a primary construct inheriting/conforming/naming it across the module boundary
    // would otherwise be emitted against a name that resolves to nothing at runtime. The dependency-
    // quarantined names seed the closure's reachability walk exactly like a locally-withdrawn name,
    // but are never themselves emitted as withdrawal units of this module.

    private const string DependencyModule = "IngestionBase";

    [Fact]
    public void PrimaryProtocolInheritingDependencyQuarantinedName_WithdrawnWhole()
    {
        // protocol BridgeRelay: IngestionBase.BaseSignal — BaseSignal was quarantined in the
        // dependency module, so BridgeRelay's identity is indeterminate and must be withdrawn whole.
        var module = MakeModule();
        var relay = MakeProtocol("BridgeRelay",
            inheritedProtocols: new[] { $"{DependencyModule}.BaseSignal" });
        AttachTypes(module, relay);

        InputResolutionReport.Reset();
        var result = IngestionQuarantineClosure.Compute(
            module, Module, NullLogger.Instance,
            dependencyQuarantinedNames: new[] { $"{DependencyModule}.BaseSignal" });

        Assert.True(result.ProvenComplete);
        Assert.Contains(
            RecoveryUnitId.Create(DeclIdFactory.ForType(relay), RecoveryScope.TypeSurface),
            result.Withdrawals);
        // The dependency-quarantined protocol belongs to another module and is never a withdrawal
        // unit of THIS module: nothing named BaseSignal appears as a local type-surface withdrawal.
        Assert.DoesNotContain(
            result.Withdrawals,
            u => u.Describe().Contains("BaseSignal", StringComparison.Ordinal)
                 && u.Describe().Contains(DependencyModule, StringComparison.Ordinal));
    }

    [Fact]
    public void HealthySiblingInheritingHealthyDependencyName_NotWithdrawn()
    {
        // BridgeBeacon inherits a healthy dependency protocol (NOT in the quarantined set) and must
        // survive untouched even while a sibling that inherits the poisoned name is withdrawn.
        var module = MakeModule();
        var relay = MakeProtocol("BridgeRelay",
            inheritedProtocols: new[] { $"{DependencyModule}.BaseSignal" });
        var beacon = MakeProtocol("BridgeBeacon",
            inheritedProtocols: new[] { $"{DependencyModule}.BaseProviding" });
        AttachTypes(module, relay, beacon);

        InputResolutionReport.Reset();
        var result = IngestionQuarantineClosure.Compute(
            module, Module, NullLogger.Instance,
            dependencyQuarantinedNames: new[] { $"{DependencyModule}.BaseSignal" });

        Assert.True(result.ProvenComplete);
        Assert.Contains(
            RecoveryUnitId.Create(DeclIdFactory.ForType(relay), RecoveryScope.TypeSurface),
            result.Withdrawals);
        Assert.DoesNotContain(
            RecoveryUnitId.Create(DeclIdFactory.ForType(beacon), RecoveryScope.TypeSurface),
            result.Withdrawals);
    }

    [Fact]
    public void DependencyQuarantinedNameWithNoLocalDependent_ReturnsEmptyProvenResult()
    {
        // A dependency module quarantined a type, but nothing in THIS module reaches it. The seam
        // has nothing to withdraw and no residual to fail on — the closure is trivially proven empty.
        var module = MakeModule();
        var unrelated = MakeProtocol("BridgeBeacon",
            inheritedProtocols: new[] { $"{DependencyModule}.BaseProviding" });
        AttachTypes(module, unrelated);

        InputResolutionReport.Reset();
        var result = IngestionQuarantineClosure.Compute(
            module, Module, NullLogger.Instance,
            dependencyQuarantinedNames: new[] { $"{DependencyModule}.BaseSignal" });

        Assert.True(result.ProvenComplete);
        Assert.Empty(result.Withdrawals);
        Assert.Null(result.UnprovenReason);
    }

    [Fact]
    public void TypeLevelGenericConstraintOnDependencyQuarantinedName_FailsClosed()
    {
        // struct Box<T> where T: IngestionBase.BaseSignal — the constraint reaches a dependency-
        // quarantined name through a channel leaf/whole-type withdrawal does not model, so the
        // module must fail closed exactly as it does for a local generic-constraint residual.
        var module = MakeModule();
        var box = MakeStruct("Box", genericConstraintFullName: $"{DependencyModule}.BaseSignal");
        AttachTypes(module, box);

        InputResolutionReport.Reset();
        var result = IngestionQuarantineClosure.Compute(
            module, Module, NullLogger.Instance,
            dependencyQuarantinedNames: new[] { $"{DependencyModule}.BaseSignal" });

        Assert.False(result.ProvenComplete);
        Assert.NotNull(result.UnprovenReason);
        Assert.Contains("generic where-clause", result.UnprovenReason!);
    }

    // ---- Model factories -------------------------------------------------------------------

    private static ModuleDecl MakeModule() => new()
    {
        Name = Module,
        ParentDecl = null,
        ModuleDecl = null,
        Properties = new List<PropertyDecl>(),
        Methods = new List<MethodDecl>(),
        Types = new List<TypeDecl>(),
        Dependencies = new List<string>(),
        Protocols = new List<ProtocolDecl>(),
    };

    private static void AttachTypes(ModuleDecl module, params TypeDecl[] types)
    {
        foreach (var t in types)
        {
            t.ParentDecl = module;
            t.ModuleDecl = module;
            module.Types.Add(t);
            if (t is ProtocolDecl pd)
                module.Protocols.Add(pd);
        }
    }

    private static void AttachMethods(ModuleDecl module, params MethodDecl[] methods)
    {
        foreach (var m in methods)
        {
            m.ParentDecl = module;
            m.ModuleDecl = module;
            module.Methods.Add(m);
        }
    }

    private static StructDecl MakeStruct(
        string name,
        bool quarantined = false,
        (string field, string type)[]? storedFields = null,
        string[]? conformances = null,
        MethodDecl[]? methods = null,
        OperatorDecl[]? operators = null,
        string? genericConstraintOn = null,
        string? genericConstraintFullName = null)
    {
        var properties = (storedFields ?? Array.Empty<(string, string)>())
            .Select(f => MakeStoredProperty(f.field, f.type))
            .ToList();
        var generics = new List<GenericArgumentDecl>();
        // genericConstraintOn names a LOCAL protocol (module-prefixed here); genericConstraintFullName
        // carries an already-qualified name that may live in a DEPENDENCY module (e.g. IngestionBase.X).
        var constraintQualified = genericConstraintFullName
            ?? (genericConstraintOn is not null ? $"{Module}.{genericConstraintOn}" : null);
        if (constraintQualified is not null)
        {
            generics.Add(new GenericArgumentDecl(
                TypeName: "T",
                SugaredTypeName: "T",
                GenericConformances: new List<GenericParameterConformance>
                {
                    new(new[] { "T" }, SwiftTypeName.FromModuleQualifiedName(constraintQualified),
                        ConformanceKind.Protocol),
                },
                AssosiatedTypeConformances: new List<GenericParameterConformance>()));
        }

        return new StructDecl
        {
            Name = name,
            ParentDecl = null,
            ModuleDecl = null,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{Module}.{name}"),
            MangledName = quarantined ? string.Empty : $"$s{name}",
            IsFrozen = true,
            IsIngestionQuarantined = quarantined,
            GenericParameters = generics,
            Properties = properties,
            Methods = (methods ?? Array.Empty<MethodDecl>()).ToList(),
            Types = new List<TypeDecl>(),
            Operators = (operators ?? Array.Empty<OperatorDecl>()).ToList(),
            Conformances = (conformances ?? Array.Empty<string>())
                .Select(p => new TypeConformance(
                    SwiftTypeName.FromModuleQualifiedName($"{Module}.{name}"),
                    SwiftTypeName.FromModuleQualifiedName($"{Module}.{p}"),
                    string.Empty))
                .ToList(),
            MetadataAccessor = string.Empty,
            AvailabilityAnnotations = null,
        };
    }

    private static ClassDecl MakeClass(
        string name,
        bool quarantined = false,
        string[]? superclassNames = null)
    {
        return new ClassDecl
        {
            Name = name,
            ParentDecl = null,
            ModuleDecl = null,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{Module}.{name}"),
            MangledName = quarantined ? string.Empty : $"$s{name}",
            IsIngestionQuarantined = quarantined,
            GenericParameters = new List<GenericArgumentDecl>(),
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Conformances = new List<TypeConformance>(),
            SuperclassNames = (superclassNames ?? Array.Empty<string>()).ToList(),
            AvailabilityAnnotations = null,
        };
    }

    private static ProtocolDecl MakeProtocol(
        string name,
        bool quarantined = false,
        string[]? inheritedProtocols = null)
    {
        // Inherited protocol names are carried as-is: a cross-module parent (e.g. "IngestionBase.BaseSignal")
        // keeps its module prefix so the walker matches it by its full qualified form.
        var inherited = (inheritedProtocols ?? Array.Empty<string>())
            .Select(p => new NamedTypeSpec(p))
            .ToList();

        return new ProtocolDecl
        {
            Name = name,
            ParentDecl = null,
            ModuleDecl = null,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{Module}.{name}"),
            MangledName = quarantined ? string.Empty : $"$s{name}",
            IsIngestionQuarantined = quarantined,
            GenericParameters = new List<GenericArgumentDecl>(),
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            InheritedProtocols = inherited,
            AvailabilityAnnotations = null,
        };
    }

    private static PropertyDecl MakeStoredProperty(string name, string typeName) => new()
    {
        Name = name,
        ParentDecl = null,
        ModuleDecl = null,
        SwiftTypeSpec = new NamedTypeSpec(typeName),
        HasStorage = true,
        IsStatic = false,
        Accessors = Array.Empty<AccessorDecl>(),
    };

    private static EnumDecl MakeEnum(string name, (string caseName, string payloadType)[]? payloadCases = null)
    {
        var cases = (payloadCases ?? Array.Empty<(string, string)>())
            .Select(c => new EnumCaseDecl
            {
                Name = c.caseName,
                ParentDecl = null,
                ModuleDecl = null,
                MangledName = $"$s{name}{c.caseName}",
                AssociatedValues = new List<TypeSpec> { new NamedTypeSpec(c.payloadType) },
            })
            .ToList();

        return new EnumDecl
        {
            Name = name,
            ParentDecl = null,
            ModuleDecl = null,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{Module}.{name}"),
            MangledName = $"$s{name}",
            IsFrozen = true,
            GenericParameters = new List<GenericArgumentDecl>(),
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Conformances = new List<TypeConformance>(),
            Cases = cases,
            MetadataAccessor = string.Empty,
            AvailabilityAnnotations = null,
        };
    }

    private static OperatorDecl MakeOperator(string symbol, string[] underlyingParamTypes, string returns)
        => new()
        {
            Name = $"op{symbol}",
            ParentDecl = null,
            ModuleDecl = null,
            OperatorSymbol = symbol,
            Kind = OperatorKind.Binary,
            IsPrefix = false,
            UnderlyingMethod = MakeMethod($"op{symbol}", returns, underlyingParamTypes),
        };

    private static MethodDecl MakeFreeFunc(string name, string returns, string[]? paramTypes = null)
        => MakeMethod(name, returns, paramTypes);

    private static MethodDecl MakeMethod(string name, string returns, string[]? paramTypes = null)
    {
        // CSSignature index 0 is the return type; parameters follow (the emitter/walker convention).
        var signature = new List<ArgumentDecl> { MakeArg("__ret", returns) };
        foreach (var (t, i) in (paramTypes ?? Array.Empty<string>()).Select((t, i) => (t, i)))
            signature.Add(MakeArg($"p{i}", t));

        return new MethodDecl
        {
            Name = name,
            ParentDecl = null,
            ModuleDecl = null,
            MangledName = $"$s{name}",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false,
            GenericParameters = new List<GenericArgumentDecl>(),
            CSSignature = signature,
            AvailabilityAnnotations = null,
            RawGenericSig = null,
        };
    }

    private static ArgumentDecl MakeArg(string name, string typeName) => new()
    {
        Name = name,
        ParentDecl = null,
        ModuleDecl = null,
        SwiftTypeSpec = new NamedTypeSpec(typeName),
        PrivateName = name,
        IsInOut = false,
        IsGeneric = false,
    };
}
