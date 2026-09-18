// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Two behavioural pins on the concrete-specialization mirror's generic-parent path, both driven
/// through a real emission pass rather than by calling the recorder directly.
///
/// The fixture is the unit-test shape of the multi-parameter same-type pin: a two-parameter generic
/// parent whose members are declared in an extension that pins one parameter, so the open-generic
/// shell cannot carry them and the closed `…CsmExtensions` classes do.
/// </summary>
[Collection("ReportCollector")]
public class CsmGenericParentRecoveryTests
{
    private const string Module = "TestLib";
    private const string Protocol = "TestLib.Processable";
    private const string CoarseItem = "TestLib.CoarseItem";
    private const string FineItem = "TestLib.FineItem";

    [Fact]
    public void EmitConcreteSpecializationsForGenericParent_RecoveredMethods_AnnotateTheirSkipRows()
    {
        // A method the member pipeline withdrew on the open-generic shell, then re-surfaced by the
        // mirror on each closed parent, must stop reading as unreachable. The annotation is an
        // EMISSION fact: it is recorded only where an overload was actually written, so this test
        // runs the emitter and reads the report, rather than calling RecordMemberRecovered itself.
        //
        // Both member kinds are asserted. The instance method is the population that was already
        // recovered before the recovery annotation reached methods; the static is the member the
        // pinned-static widening added, and it is the one that would regress silently if the
        // annotation were wired to only one of the two call sites in the method loop.
        var (db, engine, typeDecl) = CreateFixture();
        var instanceMethod = typeDecl.Methods.Single(m => m.Name == "coarseUnitsTwice");
        var staticMethod = typeDecl.Methods.Single(m => m.Name == "coarse");

        string cs;
        BindingReport? report;
        ReportCollector.Reset();
        try
        {
            ReportCollector.Start(CreateModuleWithConformers());
            // What the pipeline records for a pinned constrained-extension member on the shell.
            ReportCollector.RecordMemberSkipped(
                instanceMethod, SkipReason.UnsupportedSignature, "pinned constrained-extension method");
            ReportCollector.RecordMemberSkipped(
                staticMethod, SkipReason.UnsupportedSignature, "pinned constrained-extension static");

            cs = EmitGenericParent(db, engine, typeDecl);
            report = ReportCollector.Complete();
        }
        finally
        {
            ReportCollector.Reset();
        }

        // Positive control: the emission the annotations are supposed to describe really happened.
        // Without it a fixture that stopped reaching the emitter would leave both assertions below
        // asserting over an empty report and the test would pass while measuring nothing.
        Assert.Contains("CoarseUnitsTwice(this PinBag<", cs);
        Assert.Contains("CsmExtensions", cs);

        Assert.NotNull(report);
        var instanceRow = Assert.Single(report!.SkippedItems, r => r.Name == "coarseUnitsTwice");
        var staticRow = Assert.Single(report.SkippedItems, r => r.Name == "coarse");

        // Every closed receiver the emitter wrote an overload on is named, and each is named once
        // however many pairings produced it.
        Assert.NotNull(instanceRow.RecoveredBy);
        Assert.NotEmpty(instanceRow.RecoveredBy!);
        Assert.All(instanceRow.RecoveredBy!, p => Assert.EndsWith(".coarseUnitsTwice", p));
        Assert.All(instanceRow.RecoveredBy!, p => Assert.StartsWith("PinBag<", p));
        Assert.Equal(instanceRow.RecoveredBy!.Distinct().Count(), instanceRow.RecoveredBy!.Count);

        Assert.NotNull(staticRow.RecoveredBy);
        Assert.NotEmpty(staticRow.RecoveredBy!);
        Assert.All(staticRow.RecoveredBy!, p => Assert.EndsWith(".coarse", p));

        // The rows keep their skip reason — the shell really could not carry these members — but a
        // reader (and the disposition classifier) now sees them as recovered rather than lost.
        Assert.Equal(SkipReason.UnsupportedSignature, instanceRow.Reason);
        Assert.Equal(SkipDisposition.Recovered, SkipDispositionClassifier.Classify(instanceRow));
        Assert.Equal(SkipDisposition.Recovered, SkipDispositionClassifier.Classify(staticRow));
    }

    [Fact]
    public void IsCsmSyncEligibleForGenericParent_EveryEligibleMethod_HasAClosedOverloadEmitted()
    {
        // The shared-preflight invariant, pinned behaviourally.
        //
        // IsCsmSyncEligibleForGenericParent decides whether the pipeline SUPPRESSES a method's
        // open-generic emission, on the promise that the mirror will emit closed overloads instead.
        // Both sides consult the same CanEmitConcreteOverloadForPairing on the same arguments — the
        // predicate's last loop and TryEmitConcreteOverload's preflight — so by construction they
        // cannot disagree, and loosening one loosens the other. This test is that invariant's
        // observable shadow: it does not re-derive the predicate, it checks the consequence a
        // consumer would feel if the two ever drifted, which is a method suppressed everywhere and
        // emitted nowhere.
        //
        // Enumerated, not sampled: every method on the fixture is classified, and every method the
        // predicate declares eligible must appear as a closed overload.
        var (db, engine, typeDecl) = CreateFixture();

        var cs = EmitGenericParent(db, engine, typeDecl);

        var eligible = typeDecl.Methods
            .Where(m => ConcreteProtocolSpecializationEmitter.IsCsmSyncEligibleForGenericParent(m, typeDecl, db, engine))
            .ToList();

        // The predicate must classify something on this fixture, or the loop below is vacuous.
        // The instance method is the shape it exists for; the static is rejected before the
        // suppression arm (a suppressed static would lose its only surface), which is itself part
        // of the invariant and is asserted rather than assumed.
        Assert.Contains(eligible, m => m.Name == "coarseUnitsTwice");
        Assert.DoesNotContain(eligible, m => m.MethodType == MethodType.Static);

        foreach (var method in eligible)
        {
            var projected = char.ToUpperInvariant(method.Name[0]) + method.Name[1..];
            Assert.True(
                cs.Contains($"{projected}(this PinBag<"),
                $"'{method.Name}' is CSM-sync eligible — its open-generic form is suppressed — but no "
                    + "closed overload was emitted for it, so the member would have no surface at all.");
        }
    }

    // ==================== Fixture ====================

    /// <summary>
    /// `PinBag&lt;Lo, Hi&gt;` with two conformers, carrying one instance and one static member of the
    /// shape a constrained extension pinning one parent parameter produces. Two conformers on two
    /// parameters give four parent pairings, so the enumeration above is not a single-tuple case.
    /// </summary>
    private static (ResolvingTypeDatabase Db, ConcreteSpecializationEngine Engine, StructDecl TypeDecl) CreateFixture()
    {
        // A non-empty AsyncLibraryName is what IsXCFrameworkMode keys on, and the generic-parent
        // path returns early without it.
        var db = new ResolvingTypeDatabase { AsyncLibraryName = "SwiftBindings" };
        db.Register(SwiftTypeName.FromModuleQualifiedName(CoarseItem), Module, "CoarseItem");
        db.Register(SwiftTypeName.FromModuleQualifiedName(FineItem), Module, "FineItem");
        db.Register(SwiftTypeName.FromModuleQualifiedName("Swift.Int"), "System", "Int64");

        var engine = new ConcreteSpecializationEngine(db);
        engine.IndexModuleConformances(CreateModuleWithConformers());

        return (db, engine, CreatePinnedExtensionStruct());
    }

    private static string EmitGenericParent(
        ResolvingTypeDatabase db, ConcreteSpecializationEngine engine, StructDecl typeDecl)
    {
        var csOutput = new StringWriter();
        var swiftOutput = new StringWriter();
        ConcreteProtocolSpecializationEmitter.EmitConcreteSpecializationsForGenericParent(
            new CSharpWriter(csOutput), new SwiftWriter(swiftOutput), typeDecl, db,
            new ModuleEmissionContext(), engine, NullLogger.Instance);
        return csOutput.ToString();
    }

    private static ModuleDecl CreateModuleWithConformers()
    {
        var protocolTypeName = SwiftTypeName.FromModuleQualifiedName(Protocol);

        StructDecl Conformer(string qualifiedName)
        {
            var typeName = SwiftTypeName.FromModuleQualifiedName(qualifiedName);
            return new StructDecl
            {
                Name = typeName.Name,
                ParentDecl = null,
                ModuleDecl = null,
                SwiftTypeName = typeName,
                MangledName = "",
                IsFrozen = true,
                GenericParameters = new List<GenericArgumentDecl>(),
                Properties = new List<PropertyDecl>(),
                Methods = new List<MethodDecl>(),
                Types = new List<TypeDecl>(),
                Operators = new List<OperatorDecl>(),
                Conformances = new List<TypeConformance> { new(typeName, protocolTypeName, "") },
                MetadataAccessor = "",
                AvailabilityAnnotations = null
            };
        }

        return new ModuleDecl
        {
            Name = Module,
            ParentDecl = null,
            ModuleDecl = null,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl> { Conformer(CoarseItem), Conformer(FineItem) },
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            AvailabilityAnnotations = null
        };
    }

    private static StructDecl CreatePinnedExtensionStruct()
    {
        var protocolTypeName = SwiftTypeName.FromModuleQualifiedName(Protocol);

        GenericArgumentDecl Param(string mangled, string name) => new(
            mangled, name,
            new List<GenericParameterConformance>
            {
                new(new[] { mangled }, protocolTypeName, ConformanceKind.Protocol)
            },
            new List<GenericParameterConformance>());

        MethodDecl Member(string name, MethodType methodType) => new()
        {
            Name = name,
            ParentDecl = null,
            ModuleDecl = null,
            MangledName = $"$sTestLib6PinBagV{name}",
            MethodType = methodType,
            IsConstructor = false,
            Throws = false,
            IsAsync = false,
            IsMutating = false,
            IsSynthesizedAccessor = false,
            UsesWrapperLibrary = true,
            // Declared in an extension of the parent, so the member re-declares both parent
            // parameters; it carries no method-own generics of its own.
            GenericParameters = new List<GenericArgumentDecl>
            {
                Param("τ_0_0", "Lo"),
                Param("τ_0_1", "Hi")
            },
            CSSignature = new List<ArgumentDecl>
            {
                new()
                {
                    Name = "", PrivateName = "", IsInOut = false, ParentDecl = null, ModuleDecl = null,
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Int"), IsGeneric = false
                }
            },
            AvailabilityAnnotations = null
        };

        var instanceMethod = Member("coarseUnitsTwice", MethodType.Instance);
        var staticMethod = Member("coarse", MethodType.Static);

        var structDecl = new StructDecl
        {
            Name = "PinBag",
            ParentDecl = null,
            ModuleDecl = null,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestLib.PinBag"),
            MangledName = "",
            IsFrozen = true,
            GenericParameters = new List<GenericArgumentDecl> { Param("τ_0_0", "Lo"), Param("τ_0_1", "Hi") },
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl> { instanceMethod, staticMethod },
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Conformances = new List<TypeConformance>(),
            MetadataAccessor = "",
            AvailabilityAnnotations = null
        };

        instanceMethod.ParentDecl = structDecl;
        staticMethod.ParentDecl = structDecl;
        return structDecl;
    }

    /// <summary>
    /// Minimal type database that resolves the fixture's conformers — the engine only indexes a
    /// conformer whose C# name it can resolve.
    /// </summary>
    private class ResolvingTypeDatabase : ITypeDatabase
    {
        private readonly Dictionary<string, TypeRecord> _records = new();

        public string? AsyncLibraryName { get; set; }
        public bool IsTypeProcessed(SwiftTypeName swiftTypeName) => _records.ContainsKey(swiftTypeName.ToString());
        public bool TryGetTypeRecord(SwiftTypeName swiftTypeName, [NotNullWhen(returnValue: true)] out TypeRecord? record)
            => _records.TryGetValue(swiftTypeName.ToString(), out record);
        public string GetLibraryPath(string moduleName) => "";
        public void UpdateTypeRecord(SwiftTypeName name, TypeRecord record) { }

        public void Register(SwiftTypeName swiftTypeName, string csNamespace, string csName)
        {
            _records[swiftTypeName.ToString()] = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName(csNamespace, csName),
                SwiftTypeName = swiftTypeName,
                MetadataAccessor = "",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Struct,
            };
        }
    }
}
