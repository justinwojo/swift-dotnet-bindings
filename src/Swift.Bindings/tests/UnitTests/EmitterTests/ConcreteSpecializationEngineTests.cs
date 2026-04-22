// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Diagnostics.CodeAnalysis;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for <see cref="ConcreteSpecializationEngine"/> — protocol conformer discovery
/// from hints and ABI, and specializable method detection.
/// </summary>
public class ConcreteSpecializationEngineTests
{
    private static ITypeDatabase CreateEmptyTypeDatabase() => new EmptyTypeDatabase();

    [Fact]
    public void LoadedHints_ContainsDataProtocol()
    {
        var hints = ConcreteSpecializationEngine.LoadedHints;
        Assert.True(hints.ContainsKey("Foundation.DataProtocol"), "Should have DataProtocol hints");
        Assert.True(hints["Foundation.DataProtocol"].Count >= 2, "DataProtocol should have at least 2 conformers");
    }

    [Fact]
    public void LoadedHints_ContainsContiguousBytes()
    {
        var hints = ConcreteSpecializationEngine.LoadedHints;
        Assert.True(hints.ContainsKey("Foundation.ContiguousBytes"), "Should have ContiguousBytes hints");
    }

    [Fact]
    public void GetConformers_HintProtocol_ReturnsConformers()
    {
        var engine = new ConcreteSpecializationEngine(CreateEmptyTypeDatabase());

        var protocol = SwiftTypeName.FromModuleQualifiedName("Foundation.DataProtocol");
        var conformers = engine.GetConformers(protocol);

        Assert.True(conformers.Count >= 2, "DataProtocol should have at least 2 conformers from hints");
        Assert.Contains(conformers, c => c.CSharpType == "Data");
        Assert.Contains(conformers, c => c.CSharpType == "byte[]");
    }

    [Fact]
    public void GetConformers_UnknownProtocol_ReturnsEmpty()
    {
        var engine = new ConcreteSpecializationEngine(CreateEmptyTypeDatabase());

        var protocol = SwiftTypeName.FromModuleQualifiedName("Unknown.Protocol");
        var conformers = engine.GetConformers(protocol);

        Assert.Empty(conformers);
    }

    [Fact]
    public void IndexModuleConformances_AddsConformers()
    {
        var db = new ResolvingTypeDatabase();
        var conformerTypeName = SwiftTypeName.FromModuleQualifiedName("TestLib.MyType");
        db.Register(conformerTypeName, "TestLib", "MyType");

        var engine = new ConcreteSpecializationEngine(db);

        var moduleDecl = CreateModuleWithConformer("TestLib", "TestLib.MyType", "TestLib.MyProtocol");
        engine.IndexModuleConformances(moduleDecl);

        var protocol = SwiftTypeName.FromModuleQualifiedName("TestLib.MyProtocol");
        var conformers = engine.GetConformers(protocol);

        Assert.Single(conformers);
        Assert.Equal("TestLib.MyType", conformers[0].SwiftQualifiedName);
    }

    [Fact]
    public void IndexModuleConformances_MergesDependencyModuleNamesIntoPlausibilitySet()
    {
        // Production wires resolved --framework-dependency module names through
        // ModuleDecl.DependencyModuleNames (Program.cs), while ModuleDecl.Dependencies
        // is the ABI parser's nominal list (empty in production today). The cross-module
        // plausibility check in VerifyHintAgainstAbi must see both sources, or imported
        // non-stdlib targets can be wrongly disproved.
        var engine = new ConcreteSpecializationEngine(CreateEmptyTypeDatabase());

        var moduleDecl = CreateModuleWithConformer("TestLib", "TestLib.MyType", "TestLib.MyProtocol");
        moduleDecl.Dependencies = new List<string> { "DepFromAbi" };
        moduleDecl.DependencyModuleNames = new List<string> { "DepFromProgram" };

        engine.IndexModuleConformances(moduleDecl);

        Assert.Contains("DepFromAbi", engine.IndexedModuleDependenciesForTesting);
        Assert.Contains("DepFromProgram", engine.IndexedModuleDependenciesForTesting);
    }

    [Fact]
    public void GetConformers_HintConformerInABIButNotDeclaringProtocol_IsFiltered()
    {
        // Hints list SwiftBindingsTestLib.ColorAttribute under AttributeKind. If the
        // current module's ABI indexes ColorAttribute WITHOUT a declared conformance
        // to AttributeKind, the engine must drop the hint — otherwise the emitted
        // Swift wrapper would call a generic overload the type can't satisfy. This
        // mirrors the MusicKit.MusicVideo / PlayableMusicItem bug that caused the
        // original uncompilable wrappers.
        var db = new ResolvingTypeDatabase();
        var colorName = SwiftTypeName.FromModuleQualifiedName("SwiftBindingsTestLib.ColorAttribute");
        db.Register(colorName, "SwiftBindingsTestLib", "ColorAttribute");

        var engine = new ConcreteSpecializationEngine(db);

        var moduleDecl = CreateModuleWithTypeOnly(
            "SwiftBindingsTestLib", "SwiftBindingsTestLib.ColorAttribute");
        engine.IndexModuleConformances(moduleDecl);

        var protocol = SwiftTypeName.FromModuleQualifiedName("SwiftBindingsTestLib.AttributeKind");
        var conformers = engine.GetConformers(protocol);

        // ColorAttribute was indexed but did not declare AttributeKind conformance → dropped.
        // SizeAttribute and FlagAttribute were not indexed, so they pass through (we
        // can't verify cross-module hints).
        Assert.DoesNotContain(conformers,
            c => c.SwiftQualifiedName == "SwiftBindingsTestLib.ColorAttribute");
        Assert.Contains(conformers,
            c => c.SwiftQualifiedName == "SwiftBindingsTestLib.SizeAttribute");
        Assert.Contains(conformers,
            c => c.SwiftQualifiedName == "SwiftBindingsTestLib.FlagAttribute");
    }

    [Fact]
    public void GetConformers_HintConformerNotInABI_PassesThrough()
    {
        // Cross-module hints (conformer type from a module we didn't index) must
        // bypass the ABI cross-check — we have no ground truth to verify them.
        var engine = new ConcreteSpecializationEngine(CreateEmptyTypeDatabase());

        var protocol = SwiftTypeName.FromModuleQualifiedName("Foundation.DataProtocol");
        var conformers = engine.GetConformers(protocol);

        Assert.Contains(conformers, c => c.CSharpType == "Data");
    }

    [Fact]
    public void GetConformers_HintConformerDeclaresRefiningProtocol_IsKept()
    {
        // ColorAttribute declares conformance to RefinedAttribute, which inherits
        // AttributeKind. The hint ColorAttribute→AttributeKind must survive the
        // ABI cross-check because Swift conformance is transitive through protocol
        // refinement. Prior to the fix, the check only accepted direct conformers
        // and would silently drop this valid hint.
        var db = new ResolvingTypeDatabase();
        var colorName = SwiftTypeName.FromModuleQualifiedName("SwiftBindingsTestLib.ColorAttribute");
        db.Register(colorName, "SwiftBindingsTestLib", "ColorAttribute");

        var engine = new ConcreteSpecializationEngine(db);

        var moduleDecl = CreateModuleWithRefinedConformance(
            moduleName: "SwiftBindingsTestLib",
            conformerType: "SwiftBindingsTestLib.ColorAttribute",
            refiningProtocol: "SwiftBindingsTestLib.RefinedAttribute",
            baseProtocol: "SwiftBindingsTestLib.AttributeKind");
        engine.IndexModuleConformances(moduleDecl);

        var protocol = SwiftTypeName.FromModuleQualifiedName("SwiftBindingsTestLib.AttributeKind");
        var conformers = engine.GetConformers(protocol);

        Assert.Contains(conformers,
            c => c.SwiftQualifiedName == "SwiftBindingsTestLib.ColorAttribute");
    }

    [Fact]
    public void GetConformers_HintConformerDeclaresOnlyUnrelatedExternalProtocols_IsDropped()
    {
        // ColorAttribute is indexed in our module and declares conformance only to a
        // protocol from an unrelated external module. For that protocol to refine the
        // target AttributeKind, the external module would have to import our module —
        // implausible. The old behavior (treat every unindexed declared protocol as
        // Uncertain) let Swift.Hashable/Sendable/Codable mask false-positive hints
        // against user protocols. The engine now uses same-module plausibility:
        // cross-module unindexed protocols cannot save the hint.
        var db = new ResolvingTypeDatabase();
        var colorName = SwiftTypeName.FromModuleQualifiedName("SwiftBindingsTestLib.ColorAttribute");
        db.Register(colorName, "SwiftBindingsTestLib", "ColorAttribute");

        var engine = new ConcreteSpecializationEngine(db);

        var moduleDecl = CreateModuleWithExternalConformance(
            moduleName: "SwiftBindingsTestLib",
            conformerType: "SwiftBindingsTestLib.ColorAttribute",
            externalProtocol: "ExternalModule.ExternalProtocol");
        engine.IndexModuleConformances(moduleDecl);

        var protocol = SwiftTypeName.FromModuleQualifiedName("SwiftBindingsTestLib.AttributeKind");
        var conformers = engine.GetConformers(protocol);

        Assert.DoesNotContain(conformers,
            c => c.SwiftQualifiedName == "SwiftBindingsTestLib.ColorAttribute");
    }

    [Fact]
    public void GetConformers_HintConformerDeclaresSameModuleUnindexedProtocol_IsKept()
    {
        // ColorAttribute declares conformance to a same-module protocol we don't have
        // indexed as a ProtocolDecl (the ABI can legitimately omit protocols that were
        // never fully parsed). Same-module refinement is plausible, so we preserve
        // uncertainty and keep the hint rather than producing a false negative.
        var db = new ResolvingTypeDatabase();
        var colorName = SwiftTypeName.FromModuleQualifiedName("SwiftBindingsTestLib.ColorAttribute");
        db.Register(colorName, "SwiftBindingsTestLib", "ColorAttribute");

        var engine = new ConcreteSpecializationEngine(db);

        var moduleDecl = CreateModuleWithExternalConformance(
            moduleName: "SwiftBindingsTestLib",
            conformerType: "SwiftBindingsTestLib.ColorAttribute",
            externalProtocol: "SwiftBindingsTestLib.UnindexedSiblingProtocol");
        engine.IndexModuleConformances(moduleDecl);

        var protocol = SwiftTypeName.FromModuleQualifiedName("SwiftBindingsTestLib.AttributeKind");
        var conformers = engine.GetConformers(protocol);

        Assert.Contains(conformers,
            c => c.SwiftQualifiedName == "SwiftBindingsTestLib.ColorAttribute");
    }

    [Fact]
    public void GetConformers_HintConformerRefinesImportedTargetThroughCurrentModuleUnindexedProtocol_IsKept()
    {
        // Foundation.Data is a hint conformer for Foundation.DataProtocol. Index Foundation
        // as our current module with Data declaring conformance to a same-module
        // helper protocol we don't have parsed. Target Foundation.DataProtocol lives in
        // Swift stdlib, which is implicitly imported by every Swift module. The
        // relaxed plausibility check must preserve Uncertain — same-module-only
        // plausibility would drop this valid hint because refiner module
        // (Foundation) != target module (Swift).
        var db = new ResolvingTypeDatabase();
        var dataName = SwiftTypeName.FromModuleQualifiedName("Foundation.Data");
        db.Register(dataName, "Foundation", "Data");

        var engine = new ConcreteSpecializationEngine(db, currentModuleName: "Foundation");

        var moduleDecl = CreateModuleWithExternalConformance(
            moduleName: "Foundation",
            conformerType: "Foundation.Data",
            externalProtocol: "Foundation.UnparsedSequenceHelper");
        engine.IndexModuleConformances(moduleDecl);

        var protocol = SwiftTypeName.FromModuleQualifiedName("Foundation.DataProtocol");
        var conformers = engine.GetConformers(protocol);

        Assert.Contains(conformers,
            c => c.SwiftQualifiedName == "Foundation.Data");
    }

    [Fact]
    public void GetConformers_HintConformerMixesRefinedAndUnrelatedExternal_IsKept()
    {
        // ColorAttribute declares BOTH RefinedAttribute (same-module, refines target)
        // AND an unrelated external protocol. The indexed refining chain confirms
        // conformance; the external protocol is irrelevant. Hint must survive —
        // guards against the plausibility check accidentally overriding a Confirmed
        // result from a sibling declaration.
        var db = new ResolvingTypeDatabase();
        var colorName = SwiftTypeName.FromModuleQualifiedName("SwiftBindingsTestLib.ColorAttribute");
        db.Register(colorName, "SwiftBindingsTestLib", "ColorAttribute");

        var engine = new ConcreteSpecializationEngine(db);

        var moduleDecl = CreateModuleWithRefinedAndExternalConformances(
            moduleName: "SwiftBindingsTestLib",
            conformerType: "SwiftBindingsTestLib.ColorAttribute",
            refiningProtocol: "SwiftBindingsTestLib.RefinedAttribute",
            baseProtocol: "SwiftBindingsTestLib.AttributeKind",
            externalProtocol: "Swift.Hashable");
        engine.IndexModuleConformances(moduleDecl);

        var protocol = SwiftTypeName.FromModuleQualifiedName("SwiftBindingsTestLib.AttributeKind");
        var conformers = engine.GetConformers(protocol);

        Assert.Contains(conformers,
            c => c.SwiftQualifiedName == "SwiftBindingsTestLib.ColorAttribute");
    }

    [Fact]
    public void FindSpecializableMethods_NonGenericMethod_ReturnsEmpty()
    {
        var engine = new ConcreteSpecializationEngine(CreateEmptyTypeDatabase());

        var typeDecl = CreateStructWithMethod("Processor", "doWork", isGeneric: false);
        var result = engine.FindSpecializableMethods(typeDecl);

        Assert.Empty(result);
    }

    [Fact]
    public void FindSpecializableMethods_MethodWithConformers_ReturnsMethods()
    {
        var db = new ResolvingTypeDatabase();
        var conformerTypeName = SwiftTypeName.FromModuleQualifiedName("TestLib.ConcreteItem");
        db.Register(conformerTypeName, "TestLib", "ConcreteItem");

        var engine = new ConcreteSpecializationEngine(db);

        // Index module conformances
        var moduleDecl = CreateModuleWithConformer("TestLib", "TestLib.ConcreteItem", "TestLib.Processable");
        engine.IndexModuleConformances(moduleDecl);

        // Create type with method-level generic constrained to Processable
        var typeDecl = CreateStructWithProtocolConstrainedMethod(
            "Processor", "process", "TestLib.Processable");

        var result = engine.FindSpecializableMethods(typeDecl);

        Assert.Single(result);
        Assert.Equal("process", result[0].Method.Name);
        Assert.Single(result[0].SpecializableParams);
    }

    [Fact]
    public void FindSpecializableMethods_MethodWithoutConformers_ReturnsEmpty()
    {
        var engine = new ConcreteSpecializationEngine(CreateEmptyTypeDatabase());

        // Don't index any conformances → no conformers for the protocol
        var typeDecl = CreateStructWithProtocolConstrainedMethod(
            "Processor", "process", "TestLib.UnknownProtocol");

        var result = engine.FindSpecializableMethods(typeDecl);

        Assert.Empty(result);
    }

    [Fact]
    public void GetConformers_AttributeKindProtocol_ReturnsThreeConformers()
    {
        var engine = new ConcreteSpecializationEngine(CreateEmptyTypeDatabase());

        var protocol = SwiftTypeName.FromModuleQualifiedName("SwiftBindingsTestLib.AttributeKind");
        var conformers = engine.GetConformers(protocol);

        Assert.Equal(3, conformers.Count);
        Assert.Contains(conformers, c => c.CSharpType == "ColorAttribute");
        Assert.Contains(conformers, c => c.CSharpType == "SizeAttribute");
        Assert.Contains(conformers, c => c.CSharpType == "FlagAttribute");
    }

    [Fact]
    public void GetConformers_RoomPlanCapturedRoomAttribute_ReturnsFourConformers()
    {
        var engine = new ConcreteSpecializationEngine(CreateEmptyTypeDatabase());

        var protocol = SwiftTypeName.FromModuleQualifiedName("RoomPlan.CapturedRoomAttribute");
        var conformers = engine.GetConformers(protocol);

        Assert.Equal(4, conformers.Count);
        Assert.Contains(conformers, c => c.CSharpType == "RoomPlan.ChairType");
        Assert.Contains(conformers, c => c.CSharpType == "RoomPlan.SofaType");
        Assert.Contains(conformers, c => c.CSharpType == "RoomPlan.TableType");
        Assert.Contains(conformers, c => c.CSharpType == "RoomPlan.StorageType");
    }

    [Fact]
    public void ConcreteConformerNaming_ByteArray_HasSwiftLiteral()
    {
        var hints = ConcreteSpecializationEngine.LoadedHints;
        var dataProtocol = hints["Foundation.DataProtocol"];
        var byteArrayConformer = dataProtocol.FirstOrDefault(c => c.CSharpType == "byte[]");

        Assert.NotNull(byteArrayConformer);
        Assert.Equal("[UInt8]", byteArrayConformer!.SwiftLiteral);
    }

    [Fact]
    public void LoadedHints_ContainsSwiftCollection()
    {
        var hints = ConcreteSpecializationEngine.LoadedHints;
        Assert.True(hints.ContainsKey("Swift.Collection"), "Should have Swift.Collection hints");

        var stringArrayConformer = hints["Swift.Collection"]
            .FirstOrDefault(c => c.SwiftLiteral == "[String]");
        Assert.NotNull(stringArrayConformer);
        Assert.Equal("Swift.SwiftArray<Swift.SwiftString>", stringArrayConformer!.CSharpType);
        Assert.NotNull(stringArrayConformer.AssociatedTypes);
        Assert.Equal("Swift.String", stringArrayConformer.AssociatedTypes!["Element"]);
    }

    [Fact]
    public void FindSpecializableMethods_SomeCollectionString_SpecializesToStringArray()
    {
        // `func joinItems(_ items: some Collection<String>) -> String` parses as
        // `<τ_0_0 where τ_0_0 : Swift.Collection, τ_0_0.Element == Swift.String>`.
        // The engine should match the [String] hint (which declares Element == Swift.String)
        // against the associated-type constraint and specialize — NOT blanket-skip.
        var engine = new ConcreteSpecializationEngine(CreateEmptyTypeDatabase());

        var typeDecl = CreateClassWithSomeCollectionStringMethod("Host");

        var result = engine.FindSpecializableMethods(typeDecl);

        Assert.Single(result);
        Assert.Single(result[0].SpecializableParams);
        var specializable = result[0].SpecializableParams[0];
        Assert.Equal("Swift.Collection", specializable.ConstraintProtocol.ToString());
        Assert.Single(specializable.Conformers);
        Assert.Equal("Swift.SwiftArray<Swift.SwiftString>", specializable.Conformers[0].CSharpType);
    }

    [Fact]
    public void FindSpecializableMethods_AssociatedTypeMismatch_NoSpecialization()
    {
        // If the method constrains Element == Swift.Int but the only hint conformer
        // declares Element == Swift.String, we must NOT specialize.
        var engine = new ConcreteSpecializationEngine(CreateEmptyTypeDatabase());

        var typeDecl = CreateClassWithSomeCollectionElementMethod("Host", "Swift.Int");

        var result = engine.FindSpecializableMethods(typeDecl);

        Assert.Empty(result);
    }

    [Fact]
    public void IndexModuleConformances_PropagatesAvailabilityToConformers()
    {
        // Regression test: specialized methods for a conformer like CryptoKit.SHA3_256Digest
        // (iOS 26+) must carry the conformer's @available floor onto the emitted wrapper,
        // otherwise the @_cdecl wrapper fails to compile against an iOS 13 floor.
        var db = new ResolvingTypeDatabase();
        var conformerTypeName = SwiftTypeName.FromModuleQualifiedName("TestLib.FuturisticDigest");
        db.Register(conformerTypeName, "TestLib", "FuturisticDigest");

        var engine = new ConcreteSpecializationEngine(db);

        var availability = new List<AvailabilityAnnotation>
        {
            new(Platform: "iOS", IntroducedVersion: "26.0",
                DeprecatedVersion: null, ObsoletedVersion: null,
                IsUnconditionallyDeprecated: false, IsUnconditionallyUnavailable: false,
                Message: null, Renamed: null)
        };
        var moduleDecl = CreateModuleWithConformer(
            "TestLib", "TestLib.FuturisticDigest", "TestLib.Digest",
            availability);
        engine.IndexModuleConformances(moduleDecl);

        var protocol = SwiftTypeName.FromModuleQualifiedName("TestLib.Digest");
        var conformers = engine.GetConformers(protocol);

        Assert.Single(conformers);
        Assert.NotNull(conformers[0].AvailabilityAnnotations);
        Assert.Contains(conformers[0].AvailabilityAnnotations!,
            a => a.Platform == "iOS" && a.IntroducedVersion == "26.0");
    }

    [Fact]
    public void IndexModuleConformances_MergesParentTypeAvailability()
    {
        // Nested conformer types inherit availability from their parent type chain.
        // Verify that when a nested struct conforms to a protocol, its ancestors'
        // @available annotations are merged onto the ConcreteConformer.
        var db = new ResolvingTypeDatabase();
        var parentTypeName = SwiftTypeName.FromModuleQualifiedName("TestLib.Outer");
        var nestedTypeName = SwiftTypeName.FromModuleQualifiedName("TestLib.Outer.Inner");
        db.Register(parentTypeName, "TestLib", "Outer");
        db.Register(nestedTypeName, "TestLib", "Outer.Inner");

        var engine = new ConcreteSpecializationEngine(db);

        var parentAvailability = new List<AvailabilityAnnotation>
        {
            new(Platform: "iOS", IntroducedVersion: "17.0",
                DeprecatedVersion: null, ObsoletedVersion: null,
                IsUnconditionallyDeprecated: false, IsUnconditionallyUnavailable: false,
                Message: null, Renamed: null)
        };
        var moduleDecl = CreateModuleWithNestedConformer(
            "TestLib", "TestLib.Outer", "TestLib.Outer.Inner",
            "TestLib.Digest", parentAvailability);
        engine.IndexModuleConformances(moduleDecl);

        var protocol = SwiftTypeName.FromModuleQualifiedName("TestLib.Digest");
        var conformers = engine.GetConformers(protocol);

        Assert.Single(conformers);
        Assert.NotNull(conformers[0].AvailabilityAnnotations);
        Assert.Contains(conformers[0].AvailabilityAnnotations!,
            a => a.Platform == "iOS" && a.IntroducedVersion == "17.0");
    }

    [Fact]
    public void FindSpecializableMethods_Constructor_ReturnsMethod()
    {
        var db = new ResolvingTypeDatabase();
        var conformerTypeName = SwiftTypeName.FromModuleQualifiedName("TestLib.ConcreteItem");
        db.Register(conformerTypeName, "TestLib", "ConcreteItem");

        var engine = new ConcreteSpecializationEngine(db);

        var moduleDecl = CreateModuleWithConformer("TestLib", "TestLib.ConcreteItem", "TestLib.Processable");
        engine.IndexModuleConformances(moduleDecl);

        // Create a type whose constructor has a method-level generic constrained to Processable.
        var typeDecl = CreateStructWithProtocolConstrainedConstructor(
            "Box", "TestLib.Processable");

        var result = engine.FindSpecializableMethods(typeDecl);

        Assert.Single(result);
        Assert.True(result[0].Method.IsConstructor, "Generic constructor should be specializable");
        Assert.Single(result[0].SpecializableParams);
    }

    [Fact]
    public void ComputePairingCount_SingleParam_ReturnsConformerCount()
    {
        var specParams = new List<ConcreteSpecializationEngine.SpecializableParam>
        {
            MakeSpecParam(conformerCount: 4),
        };

        Assert.Equal(4, ConcreteProtocolSpecializationEmitter.ComputePairingCount(specParams));
    }

    [Fact]
    public void ComputePairingCount_MultipleParams_ReturnsProduct()
    {
        var specParams = new List<ConcreteSpecializationEngine.SpecializableParam>
        {
            MakeSpecParam(conformerCount: 2),
            MakeSpecParam(conformerCount: 3),
            MakeSpecParam(conformerCount: 4),
        };

        Assert.Equal(24, ConcreteProtocolSpecializationEmitter.ComputePairingCount(specParams));
    }

    [Fact]
    public void ComputePairingCount_ParamWithZeroConformers_ReturnsZero()
    {
        var specParams = new List<ConcreteSpecializationEngine.SpecializableParam>
        {
            MakeSpecParam(conformerCount: 5),
            MakeSpecParam(conformerCount: 0),
        };

        Assert.Equal(0, ConcreteProtocolSpecializationEmitter.ComputePairingCount(specParams));
    }

    [Fact]
    public void ComputePairingCount_OverflowSaturatesToLongMaxValue()
    {
        // Six params each with 1000 conformers → 10^18, overflows Int64 when multiplied
        // (Int64.MaxValue ≈ 9.2×10^18; still fits, but seven params × 1000 = 10^21 doesn't).
        var specParams = Enumerable.Range(0, 7)
            .Select(_ => MakeSpecParam(conformerCount: 1000))
            .ToList();

        Assert.Equal(long.MaxValue,
            ConcreteProtocolSpecializationEmitter.ComputePairingCount(specParams));
    }

    [Fact]
    public void ComputePairingCount_WeatherKitPathology_ExceedsCap()
    {
        // Reproduces the WeatherKit.WeatherService.weather<T1..T6> blow-up where every
        // generic param is constrained to Swift.Sendable and the ABI yields ~50 module-local
        // conformers per param. The product (50^6 ≈ 15 billion) must be above the cap so
        // the CSM-async paths short-circuit before iterating the cartesian product.
        var specParams = Enumerable.Range(0, 6)
            .Select(_ => MakeSpecParam(conformerCount: 50))
            .ToList();

        var product = ConcreteProtocolSpecializationEmitter.ComputePairingCount(specParams);
        Assert.True(product > ConcreteProtocolSpecializationEmitter.MaxCsmCartesianProductSize,
            $"Pathological product ({product}) should exceed cap " +
            $"({ConcreteProtocolSpecializationEmitter.MaxCsmCartesianProductSize}).");
    }

    private static ConcreteSpecializationEngine.SpecializableParam MakeSpecParam(int conformerCount)
    {
        var conformers = Enumerable.Range(0, conformerCount)
            .Select(i => new ConcreteSpecializationEngine.ConcreteConformer(
                SwiftQualifiedName: $"TestLib.Conformer{i}",
                CSharpType: $"Conformer{i}"))
            .ToList();

        return new ConcreteSpecializationEngine.SpecializableParam(
            GenericParam: new GenericArgumentDecl(
                TypeName: "T",
                SugaredTypeName: "T",
                GenericConformances: new List<GenericParameterConformance>(),
                AssosiatedTypeConformances: new List<GenericParameterConformance>()),
            ConstraintProtocol: SwiftTypeName.FromModuleQualifiedName("TestLib.Protocol"),
            Conformers: conformers);
    }

    // ==================== Test Doubles ====================

    private class EmptyTypeDatabase : ITypeDatabase
    {
        public string? AsyncLibraryName => null;
        public bool IsTypeProcessed(SwiftTypeName swiftTypeName) => false;
        public bool TryGetTypeRecord(SwiftTypeName swiftTypeName, [NotNullWhen(returnValue: true)] out TypeRecord? record)
        {
            record = null;
            return false;
        }
        public string GetLibraryPath(string moduleName) => "";
        public void UpdateTypeRecord(SwiftTypeName name, TypeRecord record) { }
    }

    /// <summary>
    /// Type database that resolves specific types — needed because ConcreteSpecializationEngine
    /// only indexes conformers whose C# names can be resolved via the type database.
    /// </summary>
    private class ResolvingTypeDatabase : ITypeDatabase
    {
        private readonly Dictionary<string, TypeRecord> _records = new();

        public string? AsyncLibraryName => null;
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
                Kind = TypeRecordKind.Struct
            };
        }
    }

    // ==================== Helpers ====================

    private static ModuleDecl CreateModuleWithConformer(
        string moduleName, string conformerType, string protocolType,
        List<AvailabilityAnnotation>? availability = null)
    {
        var conformerTypeName = SwiftTypeName.FromModuleQualifiedName(conformerType);
        var protocolTypeName = SwiftTypeName.FromModuleQualifiedName(protocolType);

        var structDecl = new StructDecl
        {
            Name = conformerTypeName.Name,
            ParentDecl = null,
            ModuleDecl = null,
            SwiftTypeName = conformerTypeName,
            MangledName = "",
            IsFrozen = true,
            GenericParameters = new List<GenericArgumentDecl>(),
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Conformances = new List<TypeConformance>
            {
                new(conformerTypeName, protocolTypeName, "")
            },
            MetadataAccessor = "",
            AvailabilityAnnotations = availability
        };

        return new ModuleDecl
        {
            Name = moduleName,
            ParentDecl = null,
            ModuleDecl = null,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl> { structDecl },
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            AvailabilityAnnotations = null
        };
    }

    /// <summary>
    /// Builds a module with a single type that declares no protocol conformances.
    /// Used to exercise the ABI cross-check: a hint conformer for a type indexed
    /// here (but not declaring the target protocol) must be dropped.
    /// </summary>
    private static ModuleDecl CreateModuleWithTypeOnly(string moduleName, string typeQualifiedName)
    {
        var typeName = SwiftTypeName.FromModuleQualifiedName(typeQualifiedName);
        var structDecl = new StructDecl
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
            Conformances = new List<TypeConformance>(),
            MetadataAccessor = "",
            AvailabilityAnnotations = null
        };

        return new ModuleDecl
        {
            Name = moduleName,
            ParentDecl = null,
            ModuleDecl = null,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl> { structDecl },
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            AvailabilityAnnotations = null
        };
    }

    /// <summary>
    /// Builds a module containing a conformer that declares conformance to a refining
    /// protocol, plus two ProtocolDecls — the refining protocol (inheriting the base)
    /// and the base protocol. Used to verify the ABI cross-check walks
    /// <see cref="ProtocolDecl.InheritedProtocols"/> transitively so hints keyed on a
    /// refined protocol survive when the conformer declares only the refining one.
    /// </summary>
    private static ModuleDecl CreateModuleWithRefinedConformance(
        string moduleName, string conformerType, string refiningProtocol, string baseProtocol)
    {
        var conformerTypeName = SwiftTypeName.FromModuleQualifiedName(conformerType);
        var refiningName = SwiftTypeName.FromModuleQualifiedName(refiningProtocol);
        var baseName = SwiftTypeName.FromModuleQualifiedName(baseProtocol);

        var structDecl = new StructDecl
        {
            Name = conformerTypeName.Name,
            ParentDecl = null,
            ModuleDecl = null,
            SwiftTypeName = conformerTypeName,
            MangledName = "",
            IsFrozen = true,
            GenericParameters = new List<GenericArgumentDecl>(),
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Conformances = new List<TypeConformance>
            {
                new(conformerTypeName, refiningName, "")
            },
            MetadataAccessor = "",
            AvailabilityAnnotations = null
        };

        var refiningProto = BuildProtocolDecl(
            refiningName,
            inherited: new List<NamedTypeSpec> { new NamedTypeSpec(baseName.ToString()) });
        var baseProto = BuildProtocolDecl(baseName, inherited: new List<NamedTypeSpec>());

        return new ModuleDecl
        {
            Name = moduleName,
            ParentDecl = null,
            ModuleDecl = null,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl> { structDecl, refiningProto, baseProto },
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl> { refiningProto, baseProto },
            AvailabilityAnnotations = null
        };
    }

    /// <summary>
    /// Builds a module whose conformer declares conformance to a protocol from another
    /// module that the engine doesn't have indexed. The ABI cross-check must treat this
    /// as Uncertain (cannot verify) and keep the hint rather than dropping it.
    /// </summary>
    private static ModuleDecl CreateModuleWithExternalConformance(
        string moduleName, string conformerType, string externalProtocol)
    {
        var conformerTypeName = SwiftTypeName.FromModuleQualifiedName(conformerType);
        var externalName = SwiftTypeName.FromModuleQualifiedName(externalProtocol);

        var structDecl = new StructDecl
        {
            Name = conformerTypeName.Name,
            ParentDecl = null,
            ModuleDecl = null,
            SwiftTypeName = conformerTypeName,
            MangledName = "",
            IsFrozen = true,
            GenericParameters = new List<GenericArgumentDecl>(),
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Conformances = new List<TypeConformance>
            {
                new(conformerTypeName, externalName, "")
            },
            MetadataAccessor = "",
            AvailabilityAnnotations = null
        };

        return new ModuleDecl
        {
            Name = moduleName,
            ParentDecl = null,
            ModuleDecl = null,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl> { structDecl },
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            AvailabilityAnnotations = null
        };
    }

    /// <summary>
    /// Builds a module whose conformer declares TWO conformances: one to a refining
    /// protocol that transitively reaches the target base protocol (indexed locally),
    /// and one to an unrelated external protocol (unindexed, cross-module). The ABI
    /// cross-check must produce Confirmed from the local chain and ignore the external
    /// noise — the same-module plausibility gate shouldn't downgrade a Confirmed
    /// result once the walk has already found one.
    /// </summary>
    private static ModuleDecl CreateModuleWithRefinedAndExternalConformances(
        string moduleName, string conformerType, string refiningProtocol,
        string baseProtocol, string externalProtocol)
    {
        var conformerTypeName = SwiftTypeName.FromModuleQualifiedName(conformerType);
        var refiningName = SwiftTypeName.FromModuleQualifiedName(refiningProtocol);
        var baseName = SwiftTypeName.FromModuleQualifiedName(baseProtocol);
        var externalName = SwiftTypeName.FromModuleQualifiedName(externalProtocol);

        var structDecl = new StructDecl
        {
            Name = conformerTypeName.Name,
            ParentDecl = null,
            ModuleDecl = null,
            SwiftTypeName = conformerTypeName,
            MangledName = "",
            IsFrozen = true,
            GenericParameters = new List<GenericArgumentDecl>(),
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Conformances = new List<TypeConformance>
            {
                new(conformerTypeName, refiningName, ""),
                new(conformerTypeName, externalName, ""),
            },
            MetadataAccessor = "",
            AvailabilityAnnotations = null
        };

        var refiningProto = BuildProtocolDecl(
            refiningName,
            inherited: new List<NamedTypeSpec> { new NamedTypeSpec(baseName.ToString()) });
        var baseProto = BuildProtocolDecl(baseName, inherited: new List<NamedTypeSpec>());

        return new ModuleDecl
        {
            Name = moduleName,
            ParentDecl = null,
            ModuleDecl = null,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl> { structDecl, refiningProto, baseProto },
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl> { refiningProto, baseProto },
            AvailabilityAnnotations = null
        };
    }

    private static ProtocolDecl BuildProtocolDecl(SwiftTypeName name, List<NamedTypeSpec> inherited)
    {
        return new ProtocolDecl
        {
            Name = name.Name,
            ParentDecl = null,
            ModuleDecl = null,
            SwiftTypeName = name,
            MangledName = "",
            GenericParameters = new List<GenericArgumentDecl>(),
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            AssociatedTypes = new List<AssociatedTypeDecl>(),
            InheritedProtocols = inherited,
            AvailabilityAnnotations = null
        };
    }

    private static ModuleDecl CreateModuleWithNestedConformer(
        string moduleName, string parentType, string nestedType, string protocolType,
        List<AvailabilityAnnotation>? parentAvailability)
    {
        var parentTypeName = SwiftTypeName.FromModuleQualifiedName(parentType);
        var nestedTypeName = SwiftTypeName.FromModuleQualifiedName(nestedType);
        var protocolTypeName = SwiftTypeName.FromModuleQualifiedName(protocolType);

        var nestedStruct = new StructDecl
        {
            Name = nestedTypeName.Name,
            ParentDecl = null,
            ModuleDecl = null,
            SwiftTypeName = nestedTypeName,
            MangledName = "",
            IsFrozen = true,
            GenericParameters = new List<GenericArgumentDecl>(),
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Conformances = new List<TypeConformance>
            {
                new(nestedTypeName, protocolTypeName, "")
            },
            MetadataAccessor = "",
            AvailabilityAnnotations = null
        };

        var parentStruct = new StructDecl
        {
            Name = parentTypeName.Name,
            ParentDecl = null,
            ModuleDecl = null,
            SwiftTypeName = parentTypeName,
            MangledName = "",
            IsFrozen = true,
            GenericParameters = new List<GenericArgumentDecl>(),
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl> { nestedStruct },
            Operators = new List<OperatorDecl>(),
            Conformances = new List<TypeConformance>(),
            MetadataAccessor = "",
            AvailabilityAnnotations = parentAvailability
        };

        nestedStruct.ParentDecl = parentStruct;

        return new ModuleDecl
        {
            Name = moduleName,
            ParentDecl = null,
            ModuleDecl = null,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl> { parentStruct },
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            AvailabilityAnnotations = null
        };
    }

    private static StructDecl CreateStructWithMethod(string typeName, string methodName, bool isGeneric)
    {
        var method = new MethodDecl
        {
            Name = methodName,
            ParentDecl = null,
            ModuleDecl = null,
            MangledName = $"$s{typeName}{methodName}",
            MethodType = MethodType.Static,
            IsConstructor = false,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public,
            GenericParameters = isGeneric
                ? new List<GenericArgumentDecl> { new("τ_1_0", "T", new(), new()) }
                : new List<GenericArgumentDecl>(),
            CSSignature = new List<ArgumentDecl>
            {
                new() { Name = "", PrivateName = "", IsInOut = false, ParentDecl = null, ModuleDecl = null, SwiftTypeSpec = new TupleTypeSpec(new List<TypeSpec>()), IsGeneric = false }
            },
            AvailabilityAnnotations = null
        };

        var structDecl = new StructDecl
        {
            Name = typeName,
            ParentDecl = null,
            ModuleDecl = null,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestLib.{typeName}"),
            MangledName = "",
            IsFrozen = true,
            GenericParameters = new List<GenericArgumentDecl>(),
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl> { method },
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Conformances = new List<TypeConformance>(),
            MetadataAccessor = "",
            AvailabilityAnnotations = null
        };

        method.ParentDecl = structDecl;
        return structDecl;
    }

    private static StructDecl CreateStructWithProtocolConstrainedMethod(
        string typeName, string methodName, string protocolName)
    {
        var protocolTypeName = SwiftTypeName.FromModuleQualifiedName(protocolName);
        var conformance = new GenericParameterConformance(
            new[] { "τ_1_0" }, protocolTypeName, ConformanceKind.Protocol);

        var paramTypeSpec = new NamedTypeSpec("τ_1_0");

        var method = new MethodDecl
        {
            Name = methodName,
            ParentDecl = null,
            ModuleDecl = null,
            MangledName = $"$s{typeName}{methodName}",
            MethodType = MethodType.Static,
            IsConstructor = false,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public,
            GenericParameters = new List<GenericArgumentDecl>
            {
                new("τ_1_0", "T", new List<GenericParameterConformance> { conformance }, new())
            },
            CSSignature = new List<ArgumentDecl>
            {
                // Return type (first element)
                new() { Name = "", PrivateName = "", IsInOut = false, ParentDecl = null, ModuleDecl = null, SwiftTypeSpec = new NamedTypeSpec("Swift.String"), IsGeneric = false },
                // Parameter
                new() { Name = "item", PrivateName = "item", IsInOut = false, ParentDecl = null, ModuleDecl = null, SwiftTypeSpec = paramTypeSpec, IsGeneric = true }
            },
            AvailabilityAnnotations = null
        };

        var structDecl = new StructDecl
        {
            Name = typeName,
            ParentDecl = null,
            ModuleDecl = null,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestLib.{typeName}"),
            MangledName = "",
            IsFrozen = true,
            GenericParameters = new List<GenericArgumentDecl>(),
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl> { method },
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Conformances = new List<TypeConformance>(),
            MetadataAccessor = "",
            AvailabilityAnnotations = null
        };

        method.ParentDecl = structDecl;
        return structDecl;
    }

    private static StructDecl CreateStructWithProtocolConstrainedConstructor(
        string typeName, string protocolName)
    {
        var protocolTypeName = SwiftTypeName.FromModuleQualifiedName(protocolName);
        var conformance = new GenericParameterConformance(
            new[] { "τ_0_0" }, protocolTypeName, ConformanceKind.Protocol);

        var paramTypeSpec = new NamedTypeSpec("τ_0_0");

        var ctor = new MethodDecl
        {
            Name = "init",
            ParentDecl = null,
            ModuleDecl = null,
            MangledName = $"$s{typeName}init",
            MethodType = MethodType.Static,
            IsConstructor = true,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public,
            GenericParameters = new List<GenericArgumentDecl>
            {
                new("τ_0_0", "T", new List<GenericParameterConformance> { conformance }, new())
            },
            CSSignature = new List<ArgumentDecl>
            {
                // Return type (first element) — constructor returns Self (Box here)
                new() { Name = "", PrivateName = "", IsInOut = false, ParentDecl = null, ModuleDecl = null, SwiftTypeSpec = new NamedTypeSpec($"TestLib.{typeName}"), IsGeneric = false },
                // Parameter of generic type
                new() { Name = "source", PrivateName = "source", IsInOut = false, ParentDecl = null, ModuleDecl = null, SwiftTypeSpec = paramTypeSpec, IsGeneric = true }
            },
            AvailabilityAnnotations = null
        };

        var structDecl = new StructDecl
        {
            Name = typeName,
            ParentDecl = null,
            ModuleDecl = null,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestLib.{typeName}"),
            MangledName = "",
            IsFrozen = true,
            GenericParameters = new List<GenericArgumentDecl>(),
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl> { ctor },
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Conformances = new List<TypeConformance>(),
            MetadataAccessor = "",
            AvailabilityAnnotations = null
        };

        ctor.ParentDecl = structDecl;
        return structDecl;
    }

    private static ClassDecl CreateClassWithSomeCollectionStringMethod(string typeName)
        => CreateClassWithSomeCollectionElementMethod(typeName, "Swift.String");

    private static ClassDecl CreateClassWithSomeCollectionElementMethod(string typeName, string elementType)
    {
        var collectionName = SwiftTypeName.FromModuleQualifiedName("Swift.Collection");
        var elementTypeName = SwiftTypeName.FromModuleQualifiedName(elementType);

        // Mirror the ABI parser output for `some Collection<String>`:
        //   GenericConformances:   Path=["τ_0_0"], target=Swift.Collection, Kind=Protocol
        //   AssosiatedTypeConformances: Path=["τ_0_0", "Element"], target=<elementType>, Kind=ConcreteType
        var protocolConformance = new GenericParameterConformance(
            new[] { "τ_0_0" }, collectionName, ConformanceKind.Protocol);
        var elementConformance = new GenericParameterConformance(
            new[] { "τ_0_0", "Element" }, elementTypeName, ConformanceKind.ConcreteType);

        var paramTypeSpec = new NamedTypeSpec("τ_0_0");

        var method = new MethodDecl
        {
            Name = "joinItems",
            ParentDecl = null,
            ModuleDecl = null,
            MangledName = $"$s{typeName}joinItems",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public,
            GenericParameters = new List<GenericArgumentDecl>
            {
                new("τ_0_0", "T",
                    new List<GenericParameterConformance> { protocolConformance },
                    new List<GenericParameterConformance> { elementConformance })
            },
            CSSignature = new List<ArgumentDecl>
            {
                new() { Name = "", PrivateName = "", IsInOut = false, ParentDecl = null, ModuleDecl = null, SwiftTypeSpec = new NamedTypeSpec("Swift.String"), IsGeneric = false },
                new() { Name = "items", PrivateName = "items", IsInOut = false, ParentDecl = null, ModuleDecl = null, SwiftTypeSpec = paramTypeSpec, IsGeneric = true }
            },
            AvailabilityAnnotations = null
        };

        var classDecl = new ClassDecl
        {
            Name = typeName,
            ParentDecl = null,
            ModuleDecl = null,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestLib.{typeName}"),
            MangledName = "",
            GenericParameters = new List<GenericArgumentDecl>(),
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl> { method },
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Conformances = new List<TypeConformance>(),
            AvailabilityAnnotations = null,
            IsFinal = false,
        };

        method.ParentDecl = classDecl;
        return classDecl;
    }

    // ==================== SubstitutePairingGenericsInTypeSpec ====================

    [Fact]
    public void SubstitutePairingGenerics_SimpleTopLevel_ReplacesH()
    {
        // Top-level `H` should be rewritten to the conformer's qualified name.
        // This is the degenerate case — the sync path's existing matcher already
        // handles top-level-only, but the helper must not regress it.
        var returnSpec = new NamedTypeSpec("H");
        var pairing = MakeCryptoHashPairing("CryptoKit.SHA256");

        var result = ConcreteProtocolSpecializationEmitter.SubstitutePairingGenericsInTypeSpec(returnSpec, pairing);

        var named = Assert.IsType<NamedTypeSpec>(result);
        Assert.Equal("CryptoKit.SHA256", named.Name);
        Assert.Empty(named.GenericParameters);
    }

    [Fact]
    public void SubstitutePairingGenerics_NestedInBoundGeneric_ReplacesH()
    {
        // `HashedAuthenticationCode<H>` is where the pre-fix bug lived: the sync-path
        // matcher only checked the top-level NamedTypeSpec name, so `H` survived
        // into `initializeMemory(as: HashedAuthenticationCode<H>.self, ...)`
        // and the @_cdecl wrapper failed to compile for CryptoKit.
        var returnSpec = new NamedTypeSpec("CryptoKit.HashedAuthenticationCode", new NamedTypeSpec("H"));
        var pairing = MakeCryptoHashPairing("CryptoKit.SHA256");

        var result = ConcreteProtocolSpecializationEmitter.SubstitutePairingGenericsInTypeSpec(returnSpec, pairing);

        var named = Assert.IsType<NamedTypeSpec>(result);
        Assert.Equal("CryptoKit.HashedAuthenticationCode", named.Name);
        Assert.Single(named.GenericParameters);
        var inner = Assert.IsType<NamedTypeSpec>(named.GenericParameters[0]);
        Assert.Equal("CryptoKit.SHA256", inner.Name);
    }

    [Fact]
    public void SubstitutePairingGenerics_UnmatchedName_PassesThrough()
    {
        // A type that doesn't reference any pairing generic should be returned
        // unchanged (no mutation of unrelated names).
        var returnSpec = new NamedTypeSpec("Swift.String");
        var pairing = MakeCryptoHashPairing("CryptoKit.SHA256");

        var result = ConcreteProtocolSpecializationEmitter.SubstitutePairingGenericsInTypeSpec(returnSpec, pairing);

        var named = Assert.IsType<NamedTypeSpec>(result);
        Assert.Equal("Swift.String", named.Name);
    }

    // ==================== DoesPairingSatisfyAssociatedTypeConstraints ====================

    [Fact]
    public void AssociatedTypeConstraints_MatchingElementType_Accepts()
    {
        // `MusicItemCollection<MusicItem>.init<S: Sequence>() where S.Element == MusicItem`
        // paired with conformer whose AssociatedTypes reports `Element → MusicItem`
        // should pass the filter.
        var method = CreateStructWithProtocolConstrainedMethod(
            "MusicItemCollection", "init", "Swift.Sequence").Methods[0];
        var parent = (TypeDecl)method.ParentDecl!;
        var pairing = MakeSequencePairing(
            conformerSwiftName: "MusicKit.Album",
            elementAssocType: "MusicKit.MusicItem",
            expectedElement: "MusicKit.MusicItem");

        Assert.True(ConcreteProtocolSpecializationEmitter
            .DoesPairingSatisfyAssociatedTypeConstraints(method, parent, pairing));
    }

    [Fact]
    public void AssociatedTypeConstraints_MismatchedElementType_Rejects()
    {
        // The MusicKit.init<[UInt8]>() pathology: `Array<UInt8>.Element == UInt8`,
        // but the method's constraint is `S.Element == MusicItem`. The filter must
        // reject or the planner emits an uncompilable `init<[UInt8]>` wrapper.
        var method = CreateStructWithProtocolConstrainedMethod(
            "MusicItemCollection", "init", "Swift.Sequence").Methods[0];
        var parent = (TypeDecl)method.ParentDecl!;
        var pairing = MakeSequencePairing(
            conformerSwiftName: "Swift.Array<Swift.UInt8>",
            elementAssocType: "Swift.UInt8",
            expectedElement: "MusicKit.MusicItem");

        Assert.False(ConcreteProtocolSpecializationEmitter
            .DoesPairingSatisfyAssociatedTypeConstraints(method, parent, pairing));
    }

    [Fact]
    public void AssociatedTypeConstraints_NoConstraintOnParam_Accepts()
    {
        // A pairing with no ConcreteType associated-type floor (e.g. HMAC's H: HashFunction
        // — protocol-only conformance, no S.Element == anything) must not be rejected.
        var method = CreateStructWithProtocolConstrainedMethod(
            "HMAC", "authenticationCode", "CryptoKit.HashFunction").Methods[0];
        var parent = (TypeDecl)method.ParentDecl!;
        var pairing = MakeHashFunctionPairing("CryptoKit.SHA256");

        Assert.True(ConcreteProtocolSpecializationEmitter
            .DoesPairingSatisfyAssociatedTypeConstraints(method, parent, pairing));
    }

    [Fact]
    public void AssociatedTypeConstraints_ConformerMissingAssociatedTypesMap_Rejects()
    {
        // When the method declares S.Element == T but the conformer's AssociatedTypes
        // dictionary is null, we must reject — we can't verify the constraint is satisfied
        // so the safe bet (no false-positive wrappers) is to skip.
        var method = CreateStructWithProtocolConstrainedMethod(
            "MusicItemCollection", "init", "Swift.Sequence").Methods[0];
        var parent = (TypeDecl)method.ParentDecl!;
        var pairing = MakeSequencePairing(
            conformerSwiftName: "MusicKit.Album",
            elementAssocType: null,
            expectedElement: "MusicKit.MusicItem");

        Assert.False(ConcreteProtocolSpecializationEmitter
            .DoesPairingSatisfyAssociatedTypeConstraints(method, parent, pairing));
    }

    [Fact]
    public void AssociatedTypeConstraints_MultiHopPath_LeafMatches_Accepts()
    {
        // Deep chain `S.SubSequence.Element == MusicItem`: for stdlib Collection
        // conformers (Array, Set, Dictionary.Values) the SubSequence alias exposes
        // the same Element. Leaf-name verification against the conformer's flat
        // AssociatedTypes map must still accept when the leaf matches.
        var method = CreateStructWithProtocolConstrainedMethod(
            "MusicItemCollection", "init", "Swift.Sequence").Methods[0];
        var parent = (TypeDecl)method.ParentDecl!;
        var pairing = MakeSequencePairingMultiHop(
            pathSegments: new[] { "T", "SubSequence", "Element" },
            conformerSwiftName: "MusicKit.Album",
            elementAssocType: "MusicKit.MusicItem",
            expectedElement: "MusicKit.MusicItem");

        Assert.True(ConcreteProtocolSpecializationEmitter
            .DoesPairingSatisfyAssociatedTypeConstraints(method, parent, pairing));
    }

    [Fact]
    public void AssociatedTypeConstraints_MultiHopPath_LeafMismatches_Rejects()
    {
        // Same deep chain, but the conformer's Element is UInt8 while the constraint
        // demands MusicItem. Before the multi-hop fix we silently accepted any chain
        // longer than two segments; now we fail-closed on leaf mismatch.
        var method = CreateStructWithProtocolConstrainedMethod(
            "MusicItemCollection", "init", "Swift.Sequence").Methods[0];
        var parent = (TypeDecl)method.ParentDecl!;
        var pairing = MakeSequencePairingMultiHop(
            pathSegments: new[] { "T", "SubSequence", "Element" },
            conformerSwiftName: "Swift.Array<Swift.UInt8>",
            elementAssocType: "Swift.UInt8",
            expectedElement: "MusicKit.MusicItem");

        Assert.False(ConcreteProtocolSpecializationEmitter
            .DoesPairingSatisfyAssociatedTypeConstraints(method, parent, pairing));
    }

    // ==================== Test helpers for the new filters ====================

    /// <summary>
    /// Builds a pairing shaped like CryptoKit's HMAC.authenticationCode:
    /// single param `H: HashFunction` (protocol-only conformance, no concrete
    /// associated-type floor) with the given conformer's SwiftType.
    /// </summary>
    private static (ConcreteSpecializationEngine.SpecializableParam Param,
                    ConcreteSpecializationEngine.ConcreteConformer Conformer)[]
        MakeCryptoHashPairing(string conformerQualifiedName)
    {
        var hashFuncName = SwiftTypeName.FromModuleQualifiedName("CryptoKit.HashFunction");
        var conformance = new GenericParameterConformance(
            new[] { "H" }, hashFuncName, ConformanceKind.Protocol);

        var genericParam = new GenericArgumentDecl(
            TypeName: "H",
            SugaredTypeName: "H",
            GenericConformances: new List<GenericParameterConformance> { conformance },
            AssosiatedTypeConformances: new List<GenericParameterConformance>());

        var param = new ConcreteSpecializationEngine.SpecializableParam(
            GenericParam: genericParam,
            ConstraintProtocol: hashFuncName,
            Conformers: new List<ConcreteSpecializationEngine.ConcreteConformer>());

        var conformer = new ConcreteSpecializationEngine.ConcreteConformer(
            SwiftQualifiedName: conformerQualifiedName,
            CSharpType: conformerQualifiedName.Split('.')[^1]);

        return new[] { (param, conformer) };
    }

    /// <summary>
    /// Builds a pairing for `init&lt;S: Sequence&gt;() where S.Element == MusicItem`.
    /// The associated-type constraint lives on the param's AssosiatedTypeConformances
    /// as a <see cref="ConformanceKind.ConcreteType"/> entry for Path=["T","Element"].
    /// </summary>
    private static (ConcreteSpecializationEngine.SpecializableParam Param,
                    ConcreteSpecializationEngine.ConcreteConformer Conformer)[]
        MakeSequencePairing(string conformerSwiftName, string? elementAssocType, string expectedElement)
    {
        var sequenceName = SwiftTypeName.FromModuleQualifiedName("Swift.Sequence");
        var expectedElementName = SwiftTypeName.FromModuleQualifiedName(expectedElement);

        var protocolConformance = new GenericParameterConformance(
            new[] { "T" }, sequenceName, ConformanceKind.Protocol);
        // The key constraint: S.Element must be a specific concrete type.
        var elementConstraint = new GenericParameterConformance(
            new[] { "T", "Element" }, expectedElementName, ConformanceKind.ConcreteType);

        var genericParam = new GenericArgumentDecl(
            TypeName: "T",
            SugaredTypeName: "S",
            GenericConformances: new List<GenericParameterConformance> { protocolConformance },
            AssosiatedTypeConformances: new List<GenericParameterConformance> { elementConstraint });

        var param = new ConcreteSpecializationEngine.SpecializableParam(
            GenericParam: genericParam,
            ConstraintProtocol: sequenceName,
            Conformers: new List<ConcreteSpecializationEngine.ConcreteConformer>());

        IReadOnlyDictionary<string, string>? assocTypes = elementAssocType is null
            ? null
            : new Dictionary<string, string> { ["Element"] = elementAssocType };

        var conformer = new ConcreteSpecializationEngine.ConcreteConformer(
            SwiftQualifiedName: conformerSwiftName,
            CSharpType: conformerSwiftName.Contains('<') ? "Array" : conformerSwiftName.Split('.')[^1],
            AssociatedTypes: assocTypes);

        return new[] { (param, conformer) };
    }

    /// <summary>
    /// Variant of <see cref="MakeSequencePairing"/> that lets the test specify the
    /// full associated-type Path (e.g. <c>["T", "SubSequence", "Element"]</c>).
    /// The conformer still reports its associated types by leaf name only.
    /// </summary>
    private static (ConcreteSpecializationEngine.SpecializableParam Param,
                    ConcreteSpecializationEngine.ConcreteConformer Conformer)[]
        MakeSequencePairingMultiHop(
            string[] pathSegments, string conformerSwiftName,
            string? elementAssocType, string expectedElement)
    {
        var sequenceName = SwiftTypeName.FromModuleQualifiedName("Swift.Sequence");
        var expectedElementName = SwiftTypeName.FromModuleQualifiedName(expectedElement);

        var protocolConformance = new GenericParameterConformance(
            new[] { "T" }, sequenceName, ConformanceKind.Protocol);
        var deepConstraint = new GenericParameterConformance(
            pathSegments, expectedElementName, ConformanceKind.ConcreteType);

        var genericParam = new GenericArgumentDecl(
            TypeName: "T",
            SugaredTypeName: "S",
            GenericConformances: new List<GenericParameterConformance> { protocolConformance },
            AssosiatedTypeConformances: new List<GenericParameterConformance> { deepConstraint });

        var param = new ConcreteSpecializationEngine.SpecializableParam(
            GenericParam: genericParam,
            ConstraintProtocol: sequenceName,
            Conformers: new List<ConcreteSpecializationEngine.ConcreteConformer>());

        var leafName = pathSegments[^1];
        IReadOnlyDictionary<string, string>? assocTypes = elementAssocType is null
            ? null
            : new Dictionary<string, string> { [leafName] = elementAssocType };

        var conformer = new ConcreteSpecializationEngine.ConcreteConformer(
            SwiftQualifiedName: conformerSwiftName,
            CSharpType: conformerSwiftName.Contains('<') ? "Array" : conformerSwiftName.Split('.')[^1],
            AssociatedTypes: assocTypes);

        return new[] { (param, conformer) };
    }

    /// <summary>
    /// Builds a pairing for `H: HashFunction` with no associated-type floor —
    /// models HMAC's unconstrained hash-function specialization so the filter has
    /// nothing to enforce and must accept every conformer.
    /// </summary>
    private static (ConcreteSpecializationEngine.SpecializableParam Param,
                    ConcreteSpecializationEngine.ConcreteConformer Conformer)[]
        MakeHashFunctionPairing(string conformerQualifiedName)
    {
        var hashFuncName = SwiftTypeName.FromModuleQualifiedName("CryptoKit.HashFunction");
        var protocolConformance = new GenericParameterConformance(
            new[] { "T" }, hashFuncName, ConformanceKind.Protocol);

        var genericParam = new GenericArgumentDecl(
            TypeName: "T",
            SugaredTypeName: "H",
            GenericConformances: new List<GenericParameterConformance> { protocolConformance },
            AssosiatedTypeConformances: new List<GenericParameterConformance>());

        var param = new ConcreteSpecializationEngine.SpecializableParam(
            GenericParam: genericParam,
            ConstraintProtocol: hashFuncName,
            Conformers: new List<ConcreteSpecializationEngine.ConcreteConformer>());

        var conformer = new ConcreteSpecializationEngine.ConcreteConformer(
            SwiftQualifiedName: conformerQualifiedName,
            CSharpType: conformerQualifiedName.Split('.')[^1]);

        return new[] { (param, conformer) };
    }

    // === Issue A (2026-04-22) — CryptoKit emitter: only canonicalize bare SCREAMING_CASE
    // identifiers (e.g. SHA3_256 → Sha3256). Non-identifier C# type expressions such as
    // Byte[], Foundation.Data, Swift.Array<Byte> must pass through verbatim; a previous,
    // too-eager canonicalisation mangled them to invalid identifiers like Byte__.

    [Theory]
    [InlineData("SHA3_256")]
    [InlineData("SHA_384")]
    [InlineData("BYTE_BUFFER_42")]
    public void IsBareScreamingCaseIdentifier_BareIdentifier_ReturnsTrue(string input)
    {
        Assert.True(ConcreteProtocolSpecializationEmitter.IsBareScreamingCaseIdentifier(input));
    }

    [Theory]
    [InlineData("byte[]")]                 // array suffix
    [InlineData("Foundation.Data")]        // dotted namespace-qualified
    [InlineData("Swift.Array<Byte>")]      // generic angle brackets
    [InlineData("Byte")]                   // no underscore — nothing to canonicalize
    [InlineData("SomePascal")]             // no underscore
    [InlineData("")]                       // empty
    [InlineData(" SHA3_256")]              // leading whitespace
    public void IsBareScreamingCaseIdentifier_NonBare_ReturnsFalse(string input)
    {
        Assert.False(ConcreteProtocolSpecializationEmitter.IsBareScreamingCaseIdentifier(input));
    }

    [Theory]
    [InlineData("SHA3_256", "Sha3256")]
    [InlineData("SHA_384", "Sha384")]
    public void CanonicalizeConformerCSharpType_BareScreamingCase_PascalCases(string input, string expected)
    {
        Assert.Equal(expected, ConcreteProtocolSpecializationEmitter.CanonicalizeConformerCSharpType(input));
    }

    [Theory]
    [InlineData("byte[]")]
    [InlineData("Foundation.Data")]
    [InlineData("Swift.Array<Byte>")]
    [InlineData("Byte")]
    [InlineData("")]
    public void CanonicalizeConformerCSharpType_NonBare_ReturnsVerbatim(string input)
    {
        Assert.Equal(input, ConcreteProtocolSpecializationEmitter.CanonicalizeConformerCSharpType(input));
    }
}
