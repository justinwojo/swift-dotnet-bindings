// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Linq;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// The A3 pre-emission pass: the two case-only collision shapes.
///
/// <para>Swift identifiers are case-sensitive, so a library may declare <c>url</c> beside
/// <c>URL</c>, or a namespace-facade container beside a type spelled the same way in different
/// case. Both project onto one C# identifier. Before this pass the member shape was a hard CS0102
/// that cost the later declaration its binding entirely.</para>
/// </summary>
public class CaseOnlyCollisionPassTests
{
    // ---- Member arm --------------------------------------------------------------------------

    [Fact]
    public void MemberArm_TwoSwiftNamesProjectingOntoOneIdentifier_DisambiguatesTheLaterOne()
    {
        var (moduleDecl, typeDatabase, typeDecl) = BuildTypeWithProperties(
            "EndpointSettings", "url", "URL");

        CaseOnlyCollisionPass.Precompute(moduleDecl, typeDatabase);

        // Declaration-order first keeps the natural name — it is the member that binds today, so
        // moving it would change an existing public API.
        Assert.Null(typeDecl.Properties[0].CaseDisambiguatedName);
        Assert.Equal("Url", NameProvider.GetPropertyName(typeDecl.Properties[0]));

        Assert.Equal("Url2", typeDecl.Properties[1].CaseDisambiguatedName);
        Assert.Equal("Url2", NameProvider.GetPropertyName(typeDecl.Properties[1]));
    }

    [Fact]
    public void MemberArm_ChosenNameCollidesWithARealSibling_EscalatesPastIt()
    {
        // `url2` already owns `Url2`, so the disambiguated name has to step over it rather than
        // trade one collision for another.
        var (moduleDecl, typeDatabase, typeDecl) = BuildTypeWithProperties(
            "EndpointSettings", "url", "URL", "url2");

        CaseOnlyCollisionPass.Precompute(moduleDecl, typeDatabase);

        Assert.Equal("Url3", typeDecl.Properties[1].CaseDisambiguatedName);
        Assert.Null(typeDecl.Properties[2].CaseDisambiguatedName);
        Assert.Equal("Url2", NameProvider.GetPropertyName(typeDecl.Properties[2]));
    }

    [Fact]
    public void MemberArm_DistinctProjections_RenamesNothing()
    {
        var (moduleDecl, typeDatabase, typeDecl) = BuildTypeWithProperties(
            "EndpointSettings", "host", "port");

        CaseOnlyCollisionPass.Precompute(moduleDecl, typeDatabase);

        Assert.All(typeDecl.Properties, p => Assert.Null(p.CaseDisambiguatedName));
    }

    [Fact]
    public void MemberArm_ProjectionsThatDifferOrdinally_AreLeftAlone()
    {
        // The scope boundary: the pass fires on a COLLAPSE — both Swift names projecting onto one
        // C# identifier, a hard CS0102 — not on mere case-insensitive similarity. Two members
        // whose projections differ ordinally are distinct, legal C#, so renaming one would churn
        // an API that already compiles.
        var (moduleDecl, typeDatabase, typeDecl) = BuildTypeWithProperties(
            "Document", "documentUrl", "documentUrlPath");

        // Precondition: no stamps yet, so these are the natural projections. If they ever
        // collapse, this test is asserting the wrong shape and should fail here, not silently.
        Assert.NotEqual(
            NameProvider.GetPropertyName(typeDecl.Properties[0]),
            NameProvider.GetPropertyName(typeDecl.Properties[1]));

        CaseOnlyCollisionPass.Precompute(moduleDecl, typeDatabase);

        Assert.All(typeDecl.Properties, p => Assert.Null(p.CaseDisambiguatedName));
    }

    [Fact]
    public void MemberArm_NestedTypeOwnsTheDisambiguatedName_StepsOverIt()
    {
        var (moduleDecl, typeDatabase, typeDecl) = BuildTypeWithProperties(
            "EndpointSettings", "url", "URL");

        var nested = CreateStructDecl("Url2",
            SwiftTypeName.FromModuleQualifiedName("TestModule.EndpointSettings.Url2"), moduleDecl);
        nested.ParentDecl = typeDecl;
        typeDecl.Types.Add(nested);

        CaseOnlyCollisionPass.Precompute(moduleDecl, typeDatabase);

        Assert.Equal("Url3", typeDecl.Properties[1].CaseDisambiguatedName);
    }

    [Fact]
    public void MemberArm_NonEmittableSibling_DoesNotDisplaceTheEmittedOne()
    {
        // An internal property is suppressed from the bindings entirely, so the public `URL` is
        // the only member that reaches C# — it must keep the natural name rather than be pushed
        // to `Url2` by a sibling no consumer can see.
        var (moduleDecl, typeDatabase, typeDecl) = BuildTypeWithProperties(
            "EndpointSettings", "url", "URL");
        typeDecl.Properties[0].IsModuleInternal = true;

        CaseOnlyCollisionPass.Precompute(moduleDecl, typeDatabase);

        Assert.All(typeDecl.Properties, p => Assert.Null(p.CaseDisambiguatedName));
        Assert.Equal("Url", NameProvider.GetPropertyName(typeDecl.Properties[1]));
    }

    // ---- Conformance: the requirement decides -------------------------------------------------

    [Fact]
    public void Conformance_ConformerDeclaringTheRequirementsInReverseOrder_AdoptsTheProtocolsNames()
    {
        // The shape that makes independent choices unsound: C# binds the implicit implementation
        // by NAME, so a conformer that picks its own winner compiles while `IEndpoint.Url` reads
        // the wrong Swift storage.
        var (moduleDecl, typeDatabase, protocolDecl, conformer) =
            BuildProtocolAndConformer(protocolProperties: new[] { "url", "URL" },
                                      conformerProperties: new[] { "URL", "url" });

        CaseOnlyCollisionPass.Precompute(moduleDecl, typeDatabase);

        // Requirement side: declaration order decides once.
        Assert.Null(protocolDecl.Properties[0].CaseDisambiguatedName);
        Assert.Equal("Url2", protocolDecl.Properties[1].CaseDisambiguatedName);

        // Conformer side: `URL` is declared FIRST here, so choosing locally would have given it
        // the natural name. It adopts `Url2` instead, and `url` keeps `Url` — the same Swift
        // storage the interface named.
        Assert.Equal("Url2", NameProvider.GetPropertyName(conformer.Properties[0]));
        Assert.Equal("Url", NameProvider.GetPropertyName(conformer.Properties[1]));
    }

    [Fact]
    public void Conformance_ConformerDeclaringOnlyTheRenamedRequirement_StillAdoptsIt()
    {
        // No local collision at all on the conformer — which is exactly why the local rule cannot
        // be what decides: `URL` alone projects to `Url`, and that is a different interface member.
        var (moduleDecl, typeDatabase, _, conformer) =
            BuildProtocolAndConformer(protocolProperties: new[] { "url", "URL" },
                                      conformerProperties: new[] { "URL" });

        CaseOnlyCollisionPass.Precompute(moduleDecl, typeDatabase);

        Assert.Equal("Url2", NameProvider.GetPropertyName(conformer.Properties[0]));
    }

    [Fact]
    public void Conformance_NonConformingTypeWithTheSameProperties_IsUnaffected()
    {
        // The adoption is scoped to the conformance. An unrelated type declaring the same two
        // names keeps the local rule, so a protocol elsewhere in the module cannot silently
        // reorder an API that never mentioned it.
        var (moduleDecl, typeDatabase, _, conformer) =
            BuildProtocolAndConformer(protocolProperties: new[] { "url", "URL" },
                                      conformerProperties: new[] { "URL", "url" });
        conformer.Conformances.Clear();

        CaseOnlyCollisionPass.Precompute(moduleDecl, typeDatabase);

        Assert.Null(conformer.Properties[0].CaseDisambiguatedName);
        Assert.Equal("Url", NameProvider.GetPropertyName(conformer.Properties[0]));
        Assert.Equal("Url2", NameProvider.GetPropertyName(conformer.Properties[1]));
    }

    [Fact]
    public void Conformance_ProtocolFromADependencyModule_AdoptsFromThePersistedRenameLedger()
    {
        // The cross-module case: the protocol's decision was made by an earlier generator process
        // and survives only as a <renamedMembers> entry on its type record.
        var typeDatabase = new TypeDatabase();
        var dependency = new ModuleTypeDatabase("DepModule", "/tmp/DepModule.dylib");
        var protocolName = SwiftTypeName.FromModuleQualifiedName("DepModule.Endpoint");
        dependency.RegisterType(protocolName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("DepModule", "IEndpoint"),
            SwiftTypeName = protocolName,
            MetadataAccessor = "$sMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Protocol,
            RenamedMembers = new[]
            {
                new RenamedMember(RenamedMemberKind.Property, "URL", false, "Url2",
                    nameof(NameCollisionScheme.CaseOnlyMemberCollision)),
            },
        });
        typeDatabase.AddModuleDatabase(dependency);

        var module = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        var moduleDecl = CreateModuleDecl();
        var conformerName = SwiftTypeName.FromModuleQualifiedName("TestModule.Settings");
        var conformer = CreateStructDecl("Settings", conformerName, moduleDecl);
        conformer.Properties.Add(CreateProperty("URL"));
        conformer.Properties.Add(CreateProperty("url"));
        conformer.Conformances.Add(new TypeConformance(conformerName, protocolName, "$sWP"));
        RegisterType(module, conformerName, "Settings", TypeRecordKind.Struct);
        moduleDecl.Types.Add(conformer);
        typeDatabase.AddModuleDatabase(module);

        CaseOnlyCollisionPass.Precompute(moduleDecl, typeDatabase);

        Assert.Equal("Url2", NameProvider.GetPropertyName(conformer.Properties[0]));
        Assert.Equal("Url", NameProvider.GetPropertyName(conformer.Properties[1]));
    }

    [Fact]
    public void Conformance_RequirementsFlaggedModuleInternal_AreStillResolved()
    {
        // The ABI JSON carries no access-control attribute on a protocol requirement, so the
        // parser's `@inlinable`-without-access-control heuristic marks nearly every one internal.
        // The interface emitter ignores that flag, so honouring it here would switch the member
        // arm off for protocols entirely — and the interface would declare `Url` twice (CS0102).
        var (moduleDecl, typeDatabase, protocolDecl, conformer) =
            BuildProtocolAndConformer(protocolProperties: new[] { "url", "URL" },
                                      conformerProperties: new[] { "url", "URL" });
        foreach (var requirement in protocolDecl.Properties)
            requirement.IsModuleInternal = true;

        CaseOnlyCollisionPass.Precompute(moduleDecl, typeDatabase);

        Assert.Equal("Url", NameProvider.GetPropertyName(protocolDecl.Properties[0]));
        Assert.Equal("Url2", NameProvider.GetPropertyName(protocolDecl.Properties[1]));
        Assert.Equal("Url2", NameProvider.GetPropertyName(conformer.Properties[1]));
    }

    [Fact]
    public void Conformance_UnrelatedSiblingWantingTheAdoptedName_IsTheOneThatMoves()
    {
        // The conformer also declares `url2`, whose natural projection IS the name the adopted
        // requirement claimed. The adopted name cannot move — it names an interface member — so
        // the unrelated sibling is the side that escalates; leaving both would be a CS0102 that
        // costs one of them its binding.
        var (moduleDecl, typeDatabase, _, conformer) =
            BuildProtocolAndConformer(protocolProperties: new[] { "url", "URL" },
                                      conformerProperties: new[] { "url", "URL", "url2" });

        CaseOnlyCollisionPass.Precompute(moduleDecl, typeDatabase);

        Assert.Equal("Url", NameProvider.GetPropertyName(conformer.Properties[0]));
        Assert.Equal("Url2", NameProvider.GetPropertyName(conformer.Properties[1]));
        var relocated = NameProvider.GetPropertyName(conformer.Properties[2]);
        Assert.NotEqual("Url2", relocated);
        Assert.NotEqual("Url", relocated);
    }

    [Fact]
    public void Conformance_SoleSiblingWantingTheAdoptedName_StillMoves()
    {
        // Same collision with no local case-only pair anywhere on the conformer: `URL` adopts
        // `Url2` and `url2` naturally projects onto it. Nothing here is a case-only group, so the
        // arm has to be reachable on adopted-name pressure alone.
        var (moduleDecl, typeDatabase, _, conformer) =
            BuildProtocolAndConformer(protocolProperties: new[] { "url", "URL" },
                                      conformerProperties: new[] { "URL", "url2" });

        CaseOnlyCollisionPass.Precompute(moduleDecl, typeDatabase);

        Assert.Equal("Url2", NameProvider.GetPropertyName(conformer.Properties[0]));
        Assert.NotEqual("Url2", NameProvider.GetPropertyName(conformer.Properties[1]));
    }

    [Fact]
    public void Conformance_ProtocolFromAnAbiReparsedDependency_AdoptsFromTheLiveDecl()
    {
        // A dependency supplied as a raw xcframework rather than a published module database has
        // no persisted ledger to read — the decision was made by THIS process on the dependency's
        // own ModuleDecl. Without the live-decl source the conformer falls back to its local
        // declaration order and binds the interface member to the wrong Swift storage.
        var typeDatabase = new TypeDatabase();

        var dependencyDb = new ModuleTypeDatabase("DepModule", "/tmp/DepModule.dylib");
        var protocolName = SwiftTypeName.FromModuleQualifiedName("DepModule.Endpoint");
        dependencyDb.RegisterType(protocolName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("DepModule", "IEndpoint"),
            SwiftTypeName = protocolName,
            MetadataAccessor = "$sMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Protocol,
        });
        typeDatabase.AddModuleDatabase(dependencyDb);

        var dependencyDecl = CreateModuleDecl();
        dependencyDecl.Name = "DepModule";
        var protocolDecl = CreateProtocolDecl("Endpoint", protocolName, dependencyDecl);
        protocolDecl.Properties.Add(CreateProperty("url"));
        protocolDecl.Properties.Add(CreateProperty("URL"));
        dependencyDecl.Protocols.Add(protocolDecl);
        CaseOnlyCollisionPass.Precompute(dependencyDecl, typeDatabase);
        typeDatabase.AddDependencyModuleDecl(dependencyDecl);

        var module = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        var moduleDecl = CreateModuleDecl();
        var conformerName = SwiftTypeName.FromModuleQualifiedName("TestModule.Settings");
        var conformer = CreateStructDecl("Settings", conformerName, moduleDecl);
        conformer.Properties.Add(CreateProperty("URL"));
        conformer.Properties.Add(CreateProperty("url"));
        conformer.Conformances.Add(new TypeConformance(conformerName, protocolName, "$sWP"));
        RegisterType(module, conformerName, "Settings", TypeRecordKind.Struct);
        moduleDecl.Types.Add(conformer);
        typeDatabase.AddModuleDatabase(module);

        CaseOnlyCollisionPass.Precompute(moduleDecl, typeDatabase);

        Assert.Equal("Url2", NameProvider.GetPropertyName(conformer.Properties[0]));
        Assert.Equal("Url", NameProvider.GetPropertyName(conformer.Properties[1]));
    }

    [Fact]
    public void MemberArm_SupportedAsyncStreamSiblings_CollideLikeAnyOtherPair()
    {
        // The property type gate rejects AsyncStream (its element type comes from `_Concurrency`),
        // but the handler emits it anyway as IAsyncEnumerable<T>, so the pair really does land on
        // one C# name.
        // A stream is "supported" when its element type resolves in the database, so the element is
        // the type this fixture already registered.
        var (moduleDecl, typeDatabase, typeDecl) = BuildTypeWithProperties("Feed");
        typeDecl.Properties.Add(CreateAsyncStreamProperty("stream", "TestModule.Feed"));
        typeDecl.Properties.Add(CreateAsyncStreamProperty("STREAM", "TestModule.Feed"));

        CaseOnlyCollisionPass.Precompute(moduleDecl, typeDatabase);

        Assert.Equal("Stream", NameProvider.GetPropertyName(typeDecl.Properties[0]));
        Assert.Equal("Stream2", NameProvider.GetPropertyName(typeDecl.Properties[1]));
    }

    [Fact]
    public void MemberArm_AsyncStreamWithAnUnknownElementType_IsNotASibling()
    {
        // Anti-vacuity for the carve-out above: the admission test is the SUPPORTED-stream one, not
        // merely "is a stream". An unsupported stream is never emitted, so it must not push a
        // sibling that is off its natural name.
        var (moduleDecl, typeDatabase, typeDecl) = BuildTypeWithProperties("Feed", "stream");
        typeDecl.Properties.Add(CreateAsyncStreamProperty("STREAM", "Nowhere.Absent"));

        CaseOnlyCollisionPass.Precompute(moduleDecl, typeDatabase);

        Assert.Null(typeDecl.Properties[0].CaseDisambiguatedName);
        Assert.Null(typeDecl.Properties[1].CaseDisambiguatedName);
    }

    // ---- Type arm ----------------------------------------------------------------------------

    [Fact]
    public void TypeArm_TypeCollidingWithANamespaceFacadeByCase_RenamesTheTypeNotTheFacade()
    {
        var typeDatabase = new TypeDatabase();
        var module = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        var moduleDecl = CreateModuleDecl();

        // Facade: member-free enum with a nested type → emits as a C# namespace.
        var facadeSwiftName = SwiftTypeName.FromModuleQualifiedName("TestModule.ScanKit");
        var facade = CreateEnumDecl("ScanKit", facadeSwiftName, moduleDecl);
        var facadeNested = CreateStructDecl("Region",
            SwiftTypeName.FromModuleQualifiedName("TestModule.ScanKit.Region"), moduleDecl);
        facadeNested.ParentDecl = facade;
        facade.Types.Add(facadeNested);
        RegisterType(module, facadeSwiftName, "ScanKit", TypeRecordKind.Enum);

        // Sibling spelled the same way in different case. It has a member surface, so it cannot
        // become a namespace — it is the side that has to move.
        var siblingSwiftName = SwiftTypeName.FromModuleQualifiedName("TestModule.SCANKit");
        var sibling = CreateStructDecl("SCANKit", siblingSwiftName, moduleDecl);
        sibling.Properties.Add(CreateProperty("identifier"));
        var siblingNested = CreateStructDecl("Detail",
            SwiftTypeName.FromModuleQualifiedName("TestModule.SCANKit.Detail"), moduleDecl);
        siblingNested.ParentDecl = sibling;
        sibling.Types.Add(siblingNested);
        RegisterType(module, siblingSwiftName, "SCANKit", TypeRecordKind.Struct);
        RegisterType(module, siblingNested.SwiftTypeName, "SCANKit.Detail", TypeRecordKind.Struct);

        moduleDecl.Types.Add(facade);
        moduleDecl.Types.Add(sibling);
        typeDatabase.AddModuleDatabase(module);

        CaseOnlyCollisionPass.Precompute(moduleDecl, typeDatabase);

        // The struct takes the aggregate suffix…
        Assert.True(typeDatabase.TryGetTypeRecord(siblingSwiftName, out var renamed));
        Assert.Equal($"SCANKit{NameCollisionPolicy.AggregateTypeSuffix}", renamed!.CSharpTypeName.Name);

        // …the facade keeps its name, because it is a namespace segment every nested type and
        // every cross-module reference is spelled under.
        Assert.True(typeDatabase.TryGetTypeRecord(facadeSwiftName, out var untouched));
        Assert.Equal("ScanKit", untouched!.CSharpTypeName.Name);

        // A renamed parent is a prefix of every descendant's registered name.
        Assert.True(typeDatabase.TryGetTypeRecord(siblingNested.SwiftTypeName, out var cascaded));
        Assert.Equal($"SCANKit{NameCollisionPolicy.AggregateTypeSuffix}.Detail",
            cascaded!.CSharpTypeName.Name);
    }

    [Fact]
    public void TypeArm_NoFacadeInTheModule_RenamesNothing()
    {
        var typeDatabase = new TypeDatabase();
        var module = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        var moduleDecl = CreateModuleDecl();

        // Same case-fold pair, but neither side is a facade — both are real types with members,
        // and C# accepts the pair, so there is nothing this pass must fix.
        var firstName = SwiftTypeName.FromModuleQualifiedName("TestModule.ScanKit");
        var first = CreateStructDecl("ScanKit", firstName, moduleDecl);
        first.Properties.Add(CreateProperty("identifier"));
        RegisterType(module, firstName, "ScanKit", TypeRecordKind.Struct);

        var secondName = SwiftTypeName.FromModuleQualifiedName("TestModule.SCANKit");
        var second = CreateStructDecl("SCANKit", secondName, moduleDecl);
        second.Properties.Add(CreateProperty("identifier"));
        RegisterType(module, secondName, "SCANKit", TypeRecordKind.Struct);

        moduleDecl.Types.Add(first);
        moduleDecl.Types.Add(second);
        typeDatabase.AddModuleDatabase(module);

        CaseOnlyCollisionPass.Precompute(moduleDecl, typeDatabase);

        Assert.True(typeDatabase.TryGetTypeRecord(firstName, out var firstRecord));
        Assert.Equal("ScanKit", firstRecord!.CSharpTypeName.Name);
        Assert.True(typeDatabase.TryGetTypeRecord(secondName, out var secondRecord));
        Assert.Equal("SCANKit", secondRecord!.CSharpTypeName.Name);
    }

    [Fact]
    public void TypeArm_CollidingEnum_TakesTheEnumSuffixNotTheAggregateOne()
    {
        var typeDatabase = new TypeDatabase();
        var module = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        var moduleDecl = CreateModuleDecl();

        var facadeSwiftName = SwiftTypeName.FromModuleQualifiedName("TestModule.ScanKit");
        var facade = CreateEnumDecl("ScanKit", facadeSwiftName, moduleDecl);
        var facadeNested = CreateStructDecl("Region",
            SwiftTypeName.FromModuleQualifiedName("TestModule.ScanKit.Region"), moduleDecl);
        facadeNested.ParentDecl = facade;
        facade.Types.Add(facadeNested);
        RegisterType(module, facadeSwiftName, "ScanKit", TypeRecordKind.Enum);

        // A populated enum is a real value type, not a facade — and it is an enum, so the
        // kind-aware suffix is Kind.
        var siblingSwiftName = SwiftTypeName.FromModuleQualifiedName("TestModule.SCANKit");
        var sibling = CreateEnumDecl("SCANKit", siblingSwiftName, moduleDecl);
        sibling.Cases.Add(new EnumCaseDecl
        {
            Name = "first",
            MangledName = "$sN",
            ParentDecl = sibling,
            ModuleDecl = moduleDecl,
        });
        RegisterType(module, siblingSwiftName, "SCANKit", TypeRecordKind.Enum);

        moduleDecl.Types.Add(facade);
        moduleDecl.Types.Add(sibling);
        typeDatabase.AddModuleDatabase(module);

        CaseOnlyCollisionPass.Precompute(moduleDecl, typeDatabase);

        Assert.True(typeDatabase.TryGetTypeRecord(siblingSwiftName, out var renamed));
        Assert.Equal($"SCANKit{NameCollisionPolicy.EnumTypeSuffix}", renamed!.CSharpTypeName.Name);
    }

    // ---- Report ledger -----------------------------------------------------------------------
    // The member arm assigns NUMERIC names to the public surface. Every other numbering decision in
    // the generator is published to binding-report.json; these were not, so the one arm that
    // deliberately ships `Url2` was also the one arm no artifact accounted for.

    [Fact]
    public void MemberArm_PublishesEachRenameToTheBindingReport()
    {
        var (moduleDecl, typeDatabase, _) = BuildTypeWithProperties("EndpointSettings", "url", "URL");

        ReportCollector.Start(moduleDecl);
        CaseOnlyCollisionPass.Precompute(moduleDecl, typeDatabase);
        var report = ReportCollector.Complete();
        ReportCollector.Reset();

        Assert.NotNull(report);
        var record = Assert.Single(report!.CaseOnlyRenames);
        Assert.Equal("EndpointSettings", record.DeclaringName);
        Assert.Equal("URL", record.SwiftName);
        // Both names on one record: only a natural/emitted PAIR can tell a rename from an author's
        // own numbered spelling, which is the same reason the overload lane records both.
        Assert.Equal("Url", record.NaturalName);
        Assert.Equal("Url2", record.EmittedName);
        Assert.Equal(nameof(NameCollisionScheme.CaseOnlyMemberCollision), record.Scheme);
    }

    [Fact]
    public void MemberArm_RenamesTravelInTheirOwnLaneNotTheOverloadLane()
    {
        // The two lanes answer to opposite policies — an overload name may never be the natural
        // name plus digits, and this one is exactly that — so a case-only decision must not land in
        // the channel read under the no-numeric-suffix contract.
        var (moduleDecl, typeDatabase, _) = BuildTypeWithProperties("EndpointSettings", "url", "URL");

        ReportCollector.Start(moduleDecl);
        CaseOnlyCollisionPass.Precompute(moduleDecl, typeDatabase);
        var report = ReportCollector.Complete();
        ReportCollector.Reset();

        Assert.NotNull(report);
        Assert.NotEmpty(report!.CaseOnlyRenames);
        Assert.Empty(report.OverloadRenames);
    }

    [Fact]
    public void MemberArm_NoCollision_PublishesNothing()
    {
        var (moduleDecl, typeDatabase, _) = BuildTypeWithProperties("EndpointSettings", "host", "port");

        ReportCollector.Start(moduleDecl);
        CaseOnlyCollisionPass.Precompute(moduleDecl, typeDatabase);
        var report = ReportCollector.Complete();
        ReportCollector.Reset();

        Assert.NotNull(report);
        Assert.Empty(report!.CaseOnlyRenames);
    }

    [Fact]
    public void MemberArm_ConformerAdoptingARequirementName_IsPublishedToo()
    {
        // The conformer declares the pair in the OPPOSITE order and adopts the requirement's name
        // rather than choosing its own. That adoption is a rename like any other — the member does
        // not carry its natural projection — so it has to be visible in the ledger as well.
        var (moduleDecl, typeDatabase, _, _) = BuildProtocolAndConformer(
            protocolProperties: new[] { "url", "URL" },
            conformerProperties: new[] { "URL", "url" });

        ReportCollector.Start(moduleDecl);
        CaseOnlyCollisionPass.Precompute(moduleDecl, typeDatabase);
        var report = ReportCollector.Complete();
        ReportCollector.Reset();

        Assert.NotNull(report);
        var byDeclaringType = report!.CaseOnlyRenames
            .GroupBy(r => r.DeclaringName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var requirement = Assert.Single(byDeclaringType["Endpoint"]);
        Assert.Equal("URL", requirement.SwiftName);
        Assert.Equal("Url2", requirement.EmittedName);

        // Same Swift name, same emitted name — the conformer took the interface's answer, not the
        // one its own declaration order would have produced.
        var adopted = Assert.Single(byDeclaringType["Settings"]);
        Assert.Equal("URL", adopted.SwiftName);
        Assert.Equal("Url", adopted.NaturalName);
        Assert.Equal("Url2", adopted.EmittedName);
    }

    [Fact]
    public void MemberArm_ProtocolReachableFromBothModuleCollections_IsPublishedOnce()
    {
        // A ProtocolDecl IS a TypeDecl, so the parser files it in the module's protocol list AND
        // its type list — the pass walks it twice. The ledger has to count decisions about the
        // public surface, not visits, or a reader takes one renamed member for two.
        var (moduleDecl, typeDatabase, protocolDecl, _) = BuildProtocolAndConformer(
            protocolProperties: new[] { "url", "URL" },
            conformerProperties: new[] { "url", "URL" });
        moduleDecl.Types.Add(protocolDecl);

        ReportCollector.Start(moduleDecl);
        CaseOnlyCollisionPass.Precompute(moduleDecl, typeDatabase);
        var report = ReportCollector.Complete();
        ReportCollector.Reset();

        Assert.NotNull(report);
        var requirement = Assert.Single(
            report!.CaseOnlyRenames, r => string.Equals(r.DeclaringName, "Endpoint", StringComparison.Ordinal));
        Assert.Equal("Url2", requirement.EmittedName);
    }

    [Fact]
    public void MemberArm_RenamesSurviveTheManifestRoundTrip()
    {
        // binding-report.json is REDERIVED from the artifact manifest, so a channel that exists
        // only on the live report reads as empty in the file the ship gate opens.
        var (moduleDecl, typeDatabase, _) = BuildTypeWithProperties("EndpointSettings", "url", "URL");

        ReportCollector.Start(moduleDecl);
        CaseOnlyCollisionPass.Precompute(moduleDecl, typeDatabase);
        var report = ReportCollector.Complete();
        ReportCollector.Reset();

        Assert.NotNull(report);
        var manifest = new BindingArtifactManifest
        {
            Module = report!.ModuleName,
            Generation = GenerationSection.From(report),
        };

        var projected = BindingReportProjection.Project(manifest);

        var record = Assert.Single(projected.CaseOnlyRenames);
        Assert.Equal("URL", record.SwiftName);
        Assert.Equal("Url", record.NaturalName);
        Assert.Equal("Url2", record.EmittedName);
    }

    // ---- Helpers -----------------------------------------------------------------------------

    private static (ModuleDecl, TypeDatabase, StructDecl) BuildTypeWithProperties(
        string typeName, params string[] propertyNames)
    {
        var typeDatabase = new TypeDatabase();
        var module = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        var moduleDecl = CreateModuleDecl();

        var swiftName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{typeName}");
        var typeDecl = CreateStructDecl(typeName, swiftName, moduleDecl);
        foreach (var propertyName in propertyNames)
            typeDecl.Properties.Add(CreateProperty(propertyName));

        RegisterType(module, swiftName, typeName, TypeRecordKind.Struct);
        moduleDecl.Types.Add(typeDecl);
        typeDatabase.AddModuleDatabase(module);

        return (moduleDecl, typeDatabase, typeDecl);
    }

    private static (ModuleDecl, TypeDatabase, ProtocolDecl, StructDecl) BuildProtocolAndConformer(
        string[] protocolProperties, string[] conformerProperties)
    {
        var typeDatabase = new TypeDatabase();
        var module = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        var moduleDecl = CreateModuleDecl();

        var protocolName = SwiftTypeName.FromModuleQualifiedName("TestModule.Endpoint");
        var protocolDecl = CreateProtocolDecl("Endpoint", protocolName, moduleDecl);
        foreach (var propertyName in protocolProperties)
            protocolDecl.Properties.Add(CreateProperty(propertyName));
        RegisterType(module, protocolName, "IEndpoint", TypeRecordKind.Protocol);

        var conformerName = SwiftTypeName.FromModuleQualifiedName("TestModule.Settings");
        var conformer = CreateStructDecl("Settings", conformerName, moduleDecl);
        foreach (var propertyName in conformerProperties)
            conformer.Properties.Add(CreateProperty(propertyName));
        conformer.Conformances.Add(new TypeConformance(conformerName, protocolName, "$sWP"));
        RegisterType(module, conformerName, "Settings", TypeRecordKind.Struct);

        moduleDecl.Protocols.Add(protocolDecl);
        moduleDecl.Types.Add(conformer);
        typeDatabase.AddModuleDatabase(module);

        return (moduleDecl, typeDatabase, protocolDecl, conformer);
    }

    private static void RegisterType(
        ModuleTypeDatabase module, SwiftTypeName swiftName, string csharpName, TypeRecordKind kind)
        => module.RegisterType(swiftName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", csharpName),
            SwiftTypeName = swiftName,
            MetadataAccessor = "$sMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = kind,
        });

    private static PropertyDecl CreateProperty(string name) => new()
    {
        Name = name,
        SwiftTypeSpec = new NamedTypeSpec("Swift.String"),
        IsStatic = false,
        HasStorage = true,
        Accessors = new List<AccessorDecl>(),
        ParentDecl = null,
        ModuleDecl = null,
    };

    private static PropertyDecl CreateAsyncStreamProperty(string name, string elementType) => new()
    {
        Name = name,
        SwiftTypeSpec = new NamedTypeSpec(
            AsyncStreamHandler.AsyncStreamTypeName, new NamedTypeSpec(elementType)),
        IsStatic = false,
        HasStorage = false,
        Accessors = new List<AccessorDecl>(),
        ParentDecl = null,
        ModuleDecl = null,
    };

    private static ModuleDecl CreateModuleDecl() => new()
    {
        Name = "TestModule",
        Properties = new List<PropertyDecl>(),
        Methods = new List<MethodDecl>(),
        Types = new List<TypeDecl>(),
        Dependencies = new List<string>(),
        Protocols = new List<ProtocolDecl>(),
        ParentDecl = null,
        ModuleDecl = null,
    };

    private static StructDecl CreateStructDecl(string name, SwiftTypeName swiftName, ModuleDecl moduleDecl) => new()
    {
        Name = name,
        SwiftTypeName = swiftName,
        MangledName = "$sN",
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
        MetadataAccessor = "$sMa",
    };

    private static ProtocolDecl CreateProtocolDecl(string name, SwiftTypeName swiftName, ModuleDecl moduleDecl) => new()
    {
        Name = name,
        SwiftTypeName = swiftName,
        MangledName = "$sN",
        Properties = new List<PropertyDecl>(),
        Methods = new List<MethodDecl>(),
        Types = new List<TypeDecl>(),
        Operators = new List<OperatorDecl>(),
        Subscripts = new List<SubscriptDecl>(),
        GenericParameters = new List<GenericArgumentDecl>(),
        ParentDecl = moduleDecl,
        ModuleDecl = moduleDecl,
    };

    private static EnumDecl CreateEnumDecl(string name, SwiftTypeName swiftName, ModuleDecl moduleDecl) => new()
    {
        Name = name,
        SwiftTypeName = swiftName,
        MangledName = "$sN",
        Properties = new List<PropertyDecl>(),
        Methods = new List<MethodDecl>(),
        Types = new List<TypeDecl>(),
        Operators = new List<OperatorDecl>(),
        Subscripts = new List<SubscriptDecl>(),
        GenericParameters = new List<GenericArgumentDecl>(),
        Conformances = new List<TypeConformance>(),
        Cases = new List<EnumCaseDecl>(),
        ParentDecl = moduleDecl,
        ModuleDecl = moduleDecl,
        IsFrozen = true,
        MetadataAccessor = "$sMa",
    };
}
