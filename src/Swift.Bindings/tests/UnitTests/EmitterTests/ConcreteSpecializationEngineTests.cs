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
    public void FindSpecializableMethods_ParentOnlyPlainMethod_ReturnsParentSpecs()
    {
        // Parent-only CSM: `Bag<T: Processable>.attach(text: String)`.
        // The method has no own generics; every CSM dimension is driven by the parent.
        // Before the fix, FindSpecializableMethods filtered the method out at the
        // `ownParams.Count == 0` early continue, leaving the wired-but-unreached
        // `methodParams.Count == 0` branch in EmitConcreteSpecializationsForGenericParent
        // un-fed. After the fix, the method registers with only parent-generic specs.
        var db = new ResolvingTypeDatabase();
        var conformerTypeName = SwiftTypeName.FromModuleQualifiedName("TestLib.ConcreteItem");
        db.Register(conformerTypeName, "TestLib", "ConcreteItem");

        var engine = new ConcreteSpecializationEngine(db);

        var moduleDecl = CreateModuleWithConformer("TestLib", "TestLib.ConcreteItem", "TestLib.Processable");
        engine.IndexModuleConformances(moduleDecl);

        var typeDecl = CreateGenericStructWithParentOnlyPlainMethod(
            "Bag", "attach", "TestLib.Processable");

        var result = engine.FindSpecializableMethods(typeDecl);

        Assert.Single(result);
        Assert.Equal("attach", result[0].Method.Name);
        Assert.Single(result[0].SpecializableParams);
        Assert.True(result[0].SpecializableParams[0].IsParentGeneric,
            "Sole specializable param should be flagged IsParentGeneric (no method-own generics).");
        Assert.Equal("TestLib.Processable", result[0].SpecializableParams[0].ConstraintProtocol.ToString());
    }

    [Fact]
    public void FindSpecializableMethods_ParentOnlyPlainMethod_NonResolvableParent_ReturnsEmpty()
    {
        // Parent-only methods require the parent's PAT generic to have hint-resolved
        // conformers. Without any conformer in the engine, ResolveParentSpecializableParams
        // returns null and the method is correctly NOT registered — protecting the emitter
        // from being fed a SpecializableMethod with no usable cartesian.
        var engine = new ConcreteSpecializationEngine(CreateEmptyTypeDatabase());

        // No module is indexed → no conformers for TestLib.UnknownProtocol.
        var typeDecl = CreateGenericStructWithParentOnlyPlainMethod(
            "Bag", "attach", "TestLib.UnknownProtocol");

        var result = engine.FindSpecializableMethods(typeDecl);

        Assert.Empty(result);
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
    public void InlineSwiftStructAllowlist_FoundationData_IsISwiftObjectEligibleForIndirectReturn()
    {
        // Foundation.Data → Swift.Foundation.Data is an ISwiftObject inline struct, so a
        // method whose generic return is specialized to Foundation.Data is allowed to use
        // the indirect-result return path that allocates via `GetSwiftTypeSize<T>()`
        // (constrained to `T : ISwiftObject`).
        var (isInline, isISwiftObject) = ConcreteProtocolSpecializationEmitter
            .GetInlineSwiftStructIndirectReturnEligibilityForTesting("Foundation.Data");
        Assert.True(isInline);
        Assert.True(isISwiftObject);
    }

    [Fact]
    public void InlineSwiftStructAllowlist_FoundationUUID_IsNotISwiftObjectAndRejectedForIndirectReturn()
    {
        // Foundation.UUID → System.Guid: System.Guid is unmanaged blittable (valid as a
        // parameter via `(IntPtr)(&guid)`) but does NOT implement ISwiftObject. Emitting
        // `SwiftMarshal.GetSwiftTypeSize<System.Guid>()` on the indirect-result path would
        // fail the `T : ISwiftObject` constraint at compile time. The allowlist entry must
        // record IsISwiftObject=false so the indirect-result-is-ISwiftObject gate in
        // `CanEmitConcreteOverloadForPairing` rejects a generic-return UUID specialization
        // upstream rather than producing uncompilable C#.
        var (isInline, isISwiftObject) = ConcreteProtocolSpecializationEmitter
            .GetInlineSwiftStructIndirectReturnEligibilityForTesting("Foundation.UUID");
        Assert.True(isInline);
        Assert.False(isISwiftObject);
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

    // ==================== ParentTupleSatisfiesMethodConstraints — where-clause filter ====================
    //
    // Regression coverage for the canonicalization fix in the SameType branch of
    // ParentTupleSatisfiesMethodConstraints. The filter is parent-tuple-only — it sees
    // only the parent generic's chosen conformer, not the method-own generic's.
    //
    // The canonical single-hop dependent-member RHS shape `τ_<d>+_<d>+.<id>` (e.g.
    // `τ_0_0 == τ_1_0.Element`) is a cross-level coupling — registered via AddCoupling
    // at CSE.cs:668-695 and validated by ConformerPairingSatisfiesCoupling under the
    // full conformer pairing. The filter MUST skip it (re-rejecting discards valid
    // pairings that coupling would have admitted — the AppendAll regression in Commit C).
    //
    // Other τ_-rooted shapes (bare `τ_X_Y`, multi-segment `τ_X_Y.A.B`) are NOT covered
    // by the coupling model and must fail-closed (literal-compare path rejects, since
    // the placeholder won't equal any real conformer name).
    //
    // These tests are construction-only (no full engine wiring): they exercise
    // ParentTupleSatisfiesMethodConstraints directly with a parent-tuple list and a
    // MethodDecl whose RawGenericSig carries each clause shape.

    [Fact]
    public void ParentTupleSatisfiesMethodConstraints_SameType_TauRootedRhs_Admits()
    {
        // `<τ_0_0, τ_1_0 where τ_1_0 : Permitted, τ_0_0 == τ_1_0.Element>` —
        // the τ_0_0==τ_1_0.Element clause is a cross-level coupling, not a parent-tuple
        // constraint. The filter must skip it; coupling enforces at pairing time.
        var engine = new ConcreteSpecializationEngine(CreateEmptyTypeDatabase());
        var method = CreateMethodWithSig(
            "appendAll",
            "<τ_0_0, τ_1_0 where τ_1_0 : TestLib.Permitted, τ_0_0 == τ_1_0.Element>");
        var parent = CreateGenericStructDecl("ElementBoundContainer", "τ_0_0");

        var conformer = new ConcreteSpecializationEngine.ConcreteConformer(
            SwiftQualifiedName: "TestLib.SongItem",
            CSharpType: "SongItem");
        var specParam = new ConcreteSpecializationEngine.SpecializableParam(
            GenericParam: parent.GenericParameters[0],
            ConstraintProtocol: SwiftTypeName.FromModuleQualifiedName("TestLib.Permitted"),
            Conformers: new List<ConcreteSpecializationEngine.ConcreteConformer> { conformer },
            CouplingConstraints: null,
            IsParentGeneric: true);

        var parentTuple = new List<(ConcreteSpecializationEngine.SpecializableParam, ConcreteSpecializationEngine.ConcreteConformer)>
        {
            (specParam, conformer)
        };

        Assert.True(
            engine.ParentTupleSatisfiesMethodConstraints(method, parent, parentTuple),
            "single-hop dependent-member SameType RHS (cross-level coupling) must be skipped by the parent-tuple filter");
    }

    [Fact]
    public void ParentTupleSatisfiesMethodConstraints_SameType_BareTauRhs_FailsClosed()
    {
        // Bare-τ form `<τ_0_0, τ_1_0 where τ_0_0 == τ_1_0>` (no dotted associated-type
        // suffix). Not covered by AddCoupling registrations at 668-695 — the coupling
        // model stores `(AssocName, OtherParamName)` and has no bare-token equality
        // entry. The narrow predicate `τ_<d>+_<d>+.<id>$` must NOT match this; the
        // SameType branch falls through to literal-compare and rejects, fail-closed.
        var engine = new ConcreteSpecializationEngine(CreateEmptyTypeDatabase());
        var method = CreateMethodWithSig(
            "passthrough",
            "<τ_0_0, τ_1_0 where τ_0_0 == τ_1_0>");
        var parent = CreateGenericStructDecl("Holder", "τ_0_0");

        var conformer = new ConcreteSpecializationEngine.ConcreteConformer(
            SwiftQualifiedName: "TestLib.SongItem",
            CSharpType: "SongItem");
        var specParam = new ConcreteSpecializationEngine.SpecializableParam(
            GenericParam: parent.GenericParameters[0],
            ConstraintProtocol: SwiftTypeName.FromModuleQualifiedName("TestLib.Permitted"),
            Conformers: new List<ConcreteSpecializationEngine.ConcreteConformer> { conformer },
            CouplingConstraints: null,
            IsParentGeneric: true);

        var parentTuple = new List<(ConcreteSpecializationEngine.SpecializableParam, ConcreteSpecializationEngine.ConcreteConformer)>
        {
            (specParam, conformer)
        };

        Assert.False(
            engine.ParentTupleSatisfiesMethodConstraints(method, parent, parentTuple),
            "bare-τ SameType RHS is not covered by coupling and must fail-closed via literal-compare reject");
    }

    [Fact]
    public void ParentTupleSatisfiesMethodConstraints_SameType_ParentParentRhs_FailsClosed()
    {
        // Parent-parent same-type: BOTH τ_ roots belong to the parent's generics. The
        // cross-level AddCoupling block at CSE.cs:680-695 explicitly requires RHS root
        // to be in `ownParamNames` (line 691) — parent-parent shapes are never
        // registered, so ConformerPairingSatisfiesCoupling enforces nothing. The
        // predicate must NOT skip; literal-compare path rejects (fail-closed).
        var engine = new ConcreteSpecializationEngine(CreateEmptyTypeDatabase());
        var method = CreateMethodWithSig(
            "parentParentEqual",
            "<τ_0_0, τ_0_1 where τ_0_0 == τ_0_1.Element>");

        var parent = new StructDecl
        {
            Name = "TwoParam",
            ParentDecl = null,
            ModuleDecl = null,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestLib.TwoParam"),
            MangledName = "",
            IsFrozen = true,
            GenericParameters = new List<GenericArgumentDecl>
            {
                new("τ_0_0", "T", new(), new()),
                new("τ_0_1", "U", new(), new()),
            },
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Conformances = new List<TypeConformance>(),
            MetadataAccessor = "",
            AvailabilityAnnotations = null,
        };

        var conformerT = new ConcreteSpecializationEngine.ConcreteConformer(
            SwiftQualifiedName: "TestLib.SongItem", CSharpType: "SongItem");
        var conformerU = new ConcreteSpecializationEngine.ConcreteConformer(
            SwiftQualifiedName: "TestLib.AlbumItem", CSharpType: "AlbumItem");

        var specParamT = new ConcreteSpecializationEngine.SpecializableParam(
            GenericParam: parent.GenericParameters[0],
            ConstraintProtocol: SwiftTypeName.FromModuleQualifiedName("TestLib.Permitted"),
            Conformers: new List<ConcreteSpecializationEngine.ConcreteConformer> { conformerT },
            CouplingConstraints: null,
            IsParentGeneric: true);
        var specParamU = new ConcreteSpecializationEngine.SpecializableParam(
            GenericParam: parent.GenericParameters[1],
            ConstraintProtocol: SwiftTypeName.FromModuleQualifiedName("TestLib.Permitted"),
            Conformers: new List<ConcreteSpecializationEngine.ConcreteConformer> { conformerU },
            CouplingConstraints: null,
            IsParentGeneric: true);

        var parentTuple = new List<(ConcreteSpecializationEngine.SpecializableParam, ConcreteSpecializationEngine.ConcreteConformer)>
        {
            (specParamT, conformerT),
            (specParamU, conformerU),
        };

        Assert.False(
            engine.ParentTupleSatisfiesMethodConstraints(method, parent, parentTuple),
            "parent-parent same-type (RHS root is another parent-tuple param) is not coupling-registered and must fail-closed");
    }

    [Fact]
    public void ParentTupleSatisfiesMethodConstraints_SameType_MultiSegmentTauRhs_FailsClosed()
    {
        // Multi-segment `<τ_0_0, τ_1_0 where τ_0_0 == τ_1_0.SubSequence.Element>`.
        // ConformerPairingSatisfiesCoupling looks up `AssociatedTypes[assocName]` for
        // a single hop only; multi-hop chains aren't enforced. Predicate must reject
        // and fall through to fail-closed literal-compare.
        var engine = new ConcreteSpecializationEngine(CreateEmptyTypeDatabase());
        var method = CreateMethodWithSig(
            "deepChain",
            "<τ_0_0, τ_1_0 where τ_0_0 == τ_1_0.SubSequence.Element>");
        var parent = CreateGenericStructDecl("Holder", "τ_0_0");

        var conformer = new ConcreteSpecializationEngine.ConcreteConformer(
            SwiftQualifiedName: "TestLib.SongItem",
            CSharpType: "SongItem");
        var specParam = new ConcreteSpecializationEngine.SpecializableParam(
            GenericParam: parent.GenericParameters[0],
            ConstraintProtocol: SwiftTypeName.FromModuleQualifiedName("TestLib.Permitted"),
            Conformers: new List<ConcreteSpecializationEngine.ConcreteConformer> { conformer },
            CouplingConstraints: null,
            IsParentGeneric: true);

        var parentTuple = new List<(ConcreteSpecializationEngine.SpecializableParam, ConcreteSpecializationEngine.ConcreteConformer)>
        {
            (specParam, conformer)
        };

        Assert.False(
            engine.ParentTupleSatisfiesMethodConstraints(method, parent, parentTuple),
            "multi-segment τ_X_Y.A.B SameType RHS is not enforced by coupling (single-hop only) and must fail-closed");
    }

    [Fact]
    public void ParentTupleSatisfiesMethodConstraints_SameType_ConcreteRhs_StillRejectsMismatch()
    {
        // Concrete-typed RHS (`τ_0_0 == Swift.String`) is fully bound at parent-tuple
        // time and the filter MUST still reject mismatches — the τ_-prefix skip
        // narrowly targets τ_-rooted RHS and must not loosen the concrete branch.
        var engine = new ConcreteSpecializationEngine(CreateEmptyTypeDatabase());
        var method = CreateMethodWithSig(
            "expectString",
            "<τ_0_0 where τ_0_0 == Swift.String>");
        var parent = CreateGenericStructDecl("Holder", "τ_0_0");

        var conformer = new ConcreteSpecializationEngine.ConcreteConformer(
            SwiftQualifiedName: "TestLib.NotAString",
            CSharpType: "NotAString");
        var specParam = new ConcreteSpecializationEngine.SpecializableParam(
            GenericParam: parent.GenericParameters[0],
            ConstraintProtocol: SwiftTypeName.FromModuleQualifiedName("TestLib.Permitted"),
            Conformers: new List<ConcreteSpecializationEngine.ConcreteConformer> { conformer },
            CouplingConstraints: null,
            IsParentGeneric: true);

        var parentTuple = new List<(ConcreteSpecializationEngine.SpecializableParam, ConcreteSpecializationEngine.ConcreteConformer)>
        {
            (specParam, conformer)
        };

        Assert.False(
            engine.ParentTupleSatisfiesMethodConstraints(method, parent, parentTuple),
            "concrete SameType mismatch must still be rejected — τ_-skip is narrow");
    }

    [Fact]
    public void ParentTupleSatisfiesMethodConstraints_SameType_ConcreteRhs_AdmitsMatch()
    {
        // Positive concrete case: `τ_0_0 == Swift.String` with the String conformer
        // must still be admitted (sanity check that the τ_-skip didn't accidentally
        // short-circuit the concrete path).
        var engine = new ConcreteSpecializationEngine(CreateEmptyTypeDatabase());
        var method = CreateMethodWithSig(
            "expectString",
            "<τ_0_0 where τ_0_0 == Swift.String>");
        var parent = CreateGenericStructDecl("Holder", "τ_0_0");

        var conformer = new ConcreteSpecializationEngine.ConcreteConformer(
            SwiftQualifiedName: "Swift.String",
            CSharpType: "string");
        var specParam = new ConcreteSpecializationEngine.SpecializableParam(
            GenericParam: parent.GenericParameters[0],
            ConstraintProtocol: SwiftTypeName.FromModuleQualifiedName("TestLib.Permitted"),
            Conformers: new List<ConcreteSpecializationEngine.ConcreteConformer> { conformer },
            CouplingConstraints: null,
            IsParentGeneric: true);

        var parentTuple = new List<(ConcreteSpecializationEngine.SpecializableParam, ConcreteSpecializationEngine.ConcreteConformer)>
        {
            (specParam, conformer)
        };

        Assert.True(
            engine.ParentTupleSatisfiesMethodConstraints(method, parent, parentTuple),
            "concrete SameType match (τ_0_0 == Swift.String against Swift.String) must be admitted");
    }

    // The five tests above all exercise the BARE-LHS SameType branch (`τ_0_0 == …`, routed
    // through IsCouplingDeferredSameTypeTarget). The three below cover the complementary
    // DEPENDENT-MEMBER-LHS branch (`τ_0_0.Element == …`, routed through
    // DependentMemberClauseSatisfied) when the RHS is itself a generic-parameter placeholder.
    // A method-own RHS is a registered coupling and must be deferred; a parent-parent RHS is
    // registered by NO coupling path, so it must be proven against the bound conformer or
    // fail-closed — deferring it (the prior behavior) was a false-acceptance that let CSM emit
    // a closed form Swift then rejects.

    [Fact]
    public void ParentTupleSatisfiesMethodConstraints_DependentMemberLhs_ParentParentRhs_AdmitsMatch()
    {
        // `<τ_0_0, τ_0_1 where τ_0_0.Element == τ_0_1>` — τ_0_0's conformer resolves Element to
        // the SAME Swift type chosen for τ_0_1, so the parent-parent equality is provably
        // satisfied against the bound tuple and must be admitted.
        var engine = new ConcreteSpecializationEngine(CreateEmptyTypeDatabase());
        var method = CreateMethodWithSig(
            "pairedElement",
            "<τ_0_0, τ_0_1 where τ_0_0.Element == τ_0_1>");
        var parent = CreateTwoParamGenericStructDecl("TwoParam", "τ_0_0", "τ_0_1");

        var conformerT = new ConcreteSpecializationEngine.ConcreteConformer(
            SwiftQualifiedName: "TestLib.SongList", CSharpType: "SongList",
            AssociatedTypes: new Dictionary<string, string> { ["Element"] = "TestLib.Song" });
        var conformerU = new ConcreteSpecializationEngine.ConcreteConformer(
            SwiftQualifiedName: "TestLib.Song", CSharpType: "Song");

        var parentTuple = TwoParentTuple(parent, conformerT, conformerU);

        Assert.True(
            engine.ParentTupleSatisfiesMethodConstraints(method, parent, parentTuple),
            "parent-parent dependent-member SameType is provably satisfied (Element == bound τ_0_1 conformer) and must be admitted");
    }

    [Fact]
    public void ParentTupleSatisfiesMethodConstraints_DependentMemberLhs_ParentParentRhs_RejectsMismatch()
    {
        // Same shape, but τ_0_0's Element (TestLib.Song) differs from τ_0_1's chosen conformer
        // (TestLib.Album). No coupling path registers a parent-parent dependent-member equality,
        // so deferring would be a false-acceptance — it must be proven and fail-closed on mismatch.
        var engine = new ConcreteSpecializationEngine(CreateEmptyTypeDatabase());
        var method = CreateMethodWithSig(
            "pairedElement",
            "<τ_0_0, τ_0_1 where τ_0_0.Element == τ_0_1>");
        var parent = CreateTwoParamGenericStructDecl("TwoParam", "τ_0_0", "τ_0_1");

        var conformerT = new ConcreteSpecializationEngine.ConcreteConformer(
            SwiftQualifiedName: "TestLib.SongList", CSharpType: "SongList",
            AssociatedTypes: new Dictionary<string, string> { ["Element"] = "TestLib.Song" });
        var conformerU = new ConcreteSpecializationEngine.ConcreteConformer(
            SwiftQualifiedName: "TestLib.Album", CSharpType: "Album");

        var parentTuple = TwoParentTuple(parent, conformerT, conformerU);

        Assert.False(
            engine.ParentTupleSatisfiesMethodConstraints(method, parent, parentTuple),
            "parent-parent dependent-member SameType with a mismatched bound τ_0_1 conformer is not coupling-enforced and must fail-closed");
    }

    [Fact]
    public void ParentTupleSatisfiesMethodConstraints_DependentMemberLhs_MethodOwnRhs_Admits()
    {
        // `<τ_0_0, τ_1_0 where τ_0_0.Element == τ_1_0>` — τ_1_0 is METHOD-OWN (absent from the
        // parent tuple). That coupling IS registered (RHS-form) and enforced at cartesian pairing,
        // so the parent-tuple filter must defer (admit) rather than fail-closed.
        var engine = new ConcreteSpecializationEngine(CreateEmptyTypeDatabase());
        var method = CreateMethodWithSig(
            "ownElement",
            "<τ_0_0, τ_1_0 where τ_0_0.Element == τ_1_0>");
        var parent = CreateGenericStructDecl("Holder", "τ_0_0");

        var conformer = new ConcreteSpecializationEngine.ConcreteConformer(
            SwiftQualifiedName: "TestLib.SongList", CSharpType: "SongList",
            AssociatedTypes: new Dictionary<string, string> { ["Element"] = "TestLib.Song" });
        var specParam = new ConcreteSpecializationEngine.SpecializableParam(
            GenericParam: parent.GenericParameters[0],
            ConstraintProtocol: SwiftTypeName.FromModuleQualifiedName("TestLib.Permitted"),
            Conformers: new List<ConcreteSpecializationEngine.ConcreteConformer> { conformer },
            CouplingConstraints: null,
            IsParentGeneric: true);

        var parentTuple = new List<(ConcreteSpecializationEngine.SpecializableParam, ConcreteSpecializationEngine.ConcreteConformer)>
        {
            (specParam, conformer)
        };

        Assert.True(
            engine.ParentTupleSatisfiesMethodConstraints(method, parent, parentTuple),
            "method-own dependent-member SameType RHS is a registered coupling and must be deferred (admitted) by the parent-tuple filter");
    }

    private static StructDecl CreateTwoParamGenericStructDecl(string name, string tok0, string tok1)
    {
        return new StructDecl
        {
            Name = name,
            ParentDecl = null,
            ModuleDecl = null,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestLib.{name}"),
            MangledName = "",
            IsFrozen = true,
            GenericParameters = new List<GenericArgumentDecl>
            {
                new(tok0, "T", new(), new()),
                new(tok1, "U", new(), new()),
            },
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Conformances = new List<TypeConformance>(),
            MetadataAccessor = "",
            AvailabilityAnnotations = null,
        };
    }

    private static List<(ConcreteSpecializationEngine.SpecializableParam, ConcreteSpecializationEngine.ConcreteConformer)> TwoParentTuple(
        StructDecl parent,
        ConcreteSpecializationEngine.ConcreteConformer conformer0,
        ConcreteSpecializationEngine.ConcreteConformer conformer1)
    {
        var permitted = SwiftTypeName.FromModuleQualifiedName("TestLib.Permitted");
        var specParam0 = new ConcreteSpecializationEngine.SpecializableParam(
            GenericParam: parent.GenericParameters[0],
            ConstraintProtocol: permitted,
            Conformers: new List<ConcreteSpecializationEngine.ConcreteConformer> { conformer0 },
            CouplingConstraints: null,
            IsParentGeneric: true);
        var specParam1 = new ConcreteSpecializationEngine.SpecializableParam(
            GenericParam: parent.GenericParameters[1],
            ConstraintProtocol: permitted,
            Conformers: new List<ConcreteSpecializationEngine.ConcreteConformer> { conformer1 },
            CouplingConstraints: null,
            IsParentGeneric: true);
        return new List<(ConcreteSpecializationEngine.SpecializableParam, ConcreteSpecializationEngine.ConcreteConformer)>
        {
            (specParam0, conformer0),
            (specParam1, conformer1),
        };
    }

    private static MethodDecl CreateMethodWithSig(string methodName, string rawGenericSig)
    {
        return new MethodDecl
        {
            Name = methodName,
            ParentDecl = null,
            ModuleDecl = null,
            MangledName = $"$s{methodName}",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public,
            GenericParameters = new List<GenericArgumentDecl>(),
            CSSignature = new List<ArgumentDecl>(),
            AvailabilityAnnotations = null,
            RawGenericSig = rawGenericSig
        };
    }

    private static StructDecl CreateGenericStructDecl(string name, string tauTokenName)
    {
        return new StructDecl
        {
            Name = name,
            ParentDecl = null,
            ModuleDecl = null,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestLib.{name}"),
            MangledName = "",
            IsFrozen = true,
            GenericParameters = new List<GenericArgumentDecl>
            {
                new(tauTokenName, "T", new(), new())
            },
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Conformances = new List<TypeConformance>(),
            MetadataAccessor = "",
            AvailabilityAnnotations = null
        };
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

        public void Register(SwiftTypeName swiftTypeName, string csNamespace, string csName,
            TypeRecordKind kind = TypeRecordKind.Struct,
            SwiftTypeName? superclass = null,
            IReadOnlyList<SwiftTypeName>? protocolConformances = null)
        {
            _records[swiftTypeName.ToString()] = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName(csNamespace, csName),
                SwiftTypeName = swiftTypeName,
                MetadataAccessor = "",
                Flags = TypeRecordFlags.None,
                Kind = kind,
                SuperclassTypeName = superclass,
                ProtocolConformances = protocolConformances,
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

    /// <summary>
    /// Builds <c>struct {typeName}&lt;T : {protocolName}&gt; { public func {methodName}(text: String) }</c>.
    /// The parent declares one PAT-constrained generic param; the method has no own generics
    /// and references no generic types in its signature — the canonical shape that exercises
    /// the parent-only sync CSM path. Matches the production fixture
    /// <c>BindingTests/.../Generics/PatParentOnlyMethods.swift</c>.
    /// </summary>
    private static StructDecl CreateGenericStructWithParentOnlyPlainMethod(
        string typeName, string methodName, string protocolName)
    {
        var protocolTypeName = SwiftTypeName.FromModuleQualifiedName(protocolName);

        // Parent's generic param `T : protocol` lives at depth 0 on the struct.
        var parentConformance = new GenericParameterConformance(
            new[] { "τ_0_0" }, protocolTypeName, ConformanceKind.Protocol);

        var parentGenericParam = new GenericArgumentDecl(
            "τ_0_0", "T",
            new List<GenericParameterConformance> { parentConformance },
            new List<GenericParameterConformance>());

        // Plain instance method with no own generics — empty GenericParameters list.
        // Signature: `func attach(text: String)` → returns Void, single Swift.String arg.
        var method = new MethodDecl
        {
            Name = methodName,
            ParentDecl = null,
            ModuleDecl = null,
            MangledName = $"$s{typeName}{methodName}",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = false,
            IsAsync = false,
            Visibility = Visibility.Public,
            GenericParameters = new List<GenericArgumentDecl>(),
            CSSignature = new List<ArgumentDecl>
            {
                new() { Name = "", PrivateName = "", IsInOut = false, ParentDecl = null, ModuleDecl = null, SwiftTypeSpec = new TupleTypeSpec(new List<TypeSpec>()), IsGeneric = false },
                new() { Name = "text", PrivateName = "text", IsInOut = false, ParentDecl = null, ModuleDecl = null, SwiftTypeSpec = new NamedTypeSpec("Swift.String"), IsGeneric = false }
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
            GenericParameters = new List<GenericArgumentDecl> { parentGenericParam },
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

    [Fact]
    public void AssociatedTypeConstraints_ClassInheritanceConstraint_LeafMatches_Accepts()
    {
        // EntityCollection.insert<S where S: Sequence, S.Element: RealityKit.Entity>:
        // class-inheritance bound parses as ConformanceKind.Protocol but the target is
        // a class, not a protocol. With a TypeDatabase that resolves Entity as a class,
        // a conformer whose Element matches must still be accepted.
        var db = new ResolvingTypeDatabase();
        db.Register(SwiftTypeName.FromModuleQualifiedName("RealityKit.Entity"),
            "RealityKit", "Entity", TypeRecordKind.Class);

        var method = CreateStructWithProtocolConstrainedMethod(
            "EntityCollection", "insert", "Swift.Sequence").Methods[0];
        var parent = (TypeDecl)method.ParentDecl!;
        var pairing = MakeSequencePairingProtocolKind(
            conformerSwiftName: "Swift.Array<RealityKit.Entity>",
            elementAssocType: "RealityKit.Entity",
            expectedElement: "RealityKit.Entity");

        Assert.True(ConcreteProtocolSpecializationEmitter
            .DoesPairingSatisfyAssociatedTypeConstraints(method, parent, pairing, db));
    }

    [Fact]
    public void AssociatedTypeConstraints_ClassInheritanceConstraint_LeafMismatches_Rejects()
    {
        // The Bug 3 pathology: the bilateral filter previously skipped Protocol-kind
        // entries unconditionally, so an EntityCollection conformer with Element=UInt8
        // sailed through and produced a wrapper body referencing the wrong insert overload.
        // With a class target registered in the TypeDatabase, mismatched Elements must reject.
        var db = new ResolvingTypeDatabase();
        db.Register(SwiftTypeName.FromModuleQualifiedName("RealityKit.Entity"),
            "RealityKit", "Entity", TypeRecordKind.Class);

        var method = CreateStructWithProtocolConstrainedMethod(
            "EntityCollection", "insert", "Swift.Sequence").Methods[0];
        var parent = (TypeDecl)method.ParentDecl!;
        var pairing = MakeSequencePairingProtocolKind(
            conformerSwiftName: "Swift.Array<Swift.UInt8>",
            elementAssocType: "Swift.UInt8",
            expectedElement: "RealityKit.Entity");

        Assert.False(ConcreteProtocolSpecializationEmitter
            .DoesPairingSatisfyAssociatedTypeConstraints(method, parent, pairing, db));
    }

    [Fact]
    public void AssociatedTypeConstraints_ClassInheritanceConstraint_MissingAssociatedTypesMap_Rejects()
    {
        // ABI-only conformers (no specialization hint, no AssociatedTypes recorded) must
        // fail closed when the constraint is a class-inheritance bound — we can't verify
        // the conformer's Element, and the prior behavior (accept everything) emitted broken
        // wrappers across every Sequence/Collection conformer.
        var db = new ResolvingTypeDatabase();
        db.Register(SwiftTypeName.FromModuleQualifiedName("RealityKit.Entity"),
            "RealityKit", "Entity", TypeRecordKind.Class);

        var method = CreateStructWithProtocolConstrainedMethod(
            "EntityCollection", "insert", "Swift.Sequence").Methods[0];
        var parent = (TypeDecl)method.ParentDecl!;
        var pairing = MakeSequencePairingProtocolKind(
            conformerSwiftName: "RealityFoundation.SomeRangeReplaceableCollection",
            elementAssocType: null,
            expectedElement: "RealityKit.Entity");

        Assert.False(ConcreteProtocolSpecializationEmitter
            .DoesPairingSatisfyAssociatedTypeConstraints(method, parent, pairing, db));
    }

    [Fact]
    public void AssociatedTypeConstraints_ClassInheritanceConstraint_LeafIsSubclass_Accepts()
    {
        // Swift class subtype admits subclasses: `where S.Element : Animal` accepts `[Dog]`
        // when `Dog : Animal`. Exact-name match alone (the first cut of Bug 3) would falsely
        // reject this. With both records resolvable in typeDatabase, the filter walks the
        // conformer Element's SuperclassTypeName chain and accepts when expected appears.
        var db = new ResolvingTypeDatabase();
        var animalName = SwiftTypeName.FromModuleQualifiedName("SwiftBindingsTestLib.Animal");
        var dogName = SwiftTypeName.FromModuleQualifiedName("SwiftBindingsTestLib.Dog");
        db.Register(animalName, "SwiftBindingsTestLib", "Animal", TypeRecordKind.Class);
        db.Register(dogName, "SwiftBindingsTestLib", "Dog", TypeRecordKind.Class, superclass: animalName);

        var method = CreateStructWithProtocolConstrainedMethod(
            "AnimalRoster", "insert", "Swift.Sequence").Methods[0];
        var parent = (TypeDecl)method.ParentDecl!;
        var pairing = MakeSequencePairingProtocolKind(
            conformerSwiftName: "Swift.Array<SwiftBindingsTestLib.Dog>",
            elementAssocType: "SwiftBindingsTestLib.Dog",
            expectedElement: "SwiftBindingsTestLib.Animal");

        Assert.True(ConcreteProtocolSpecializationEmitter
            .DoesPairingSatisfyAssociatedTypeConstraints(method, parent, pairing, db));
    }

    [Fact]
    public void AssociatedTypeConstraints_ClassInheritanceConstraint_LeafIsTransitiveSubclass_Accepts()
    {
        // Multi-hop subtype: `Puppy : Dog : Animal`. The chain walk must follow
        // SuperclassTypeName transitively, not just the direct parent.
        var db = new ResolvingTypeDatabase();
        var animalName = SwiftTypeName.FromModuleQualifiedName("SwiftBindingsTestLib.Animal");
        var dogName = SwiftTypeName.FromModuleQualifiedName("SwiftBindingsTestLib.Dog");
        var puppyName = SwiftTypeName.FromModuleQualifiedName("SwiftBindingsTestLib.Puppy");
        db.Register(animalName, "SwiftBindingsTestLib", "Animal", TypeRecordKind.Class);
        db.Register(dogName, "SwiftBindingsTestLib", "Dog", TypeRecordKind.Class, superclass: animalName);
        db.Register(puppyName, "SwiftBindingsTestLib", "Puppy", TypeRecordKind.Class, superclass: dogName);

        var method = CreateStructWithProtocolConstrainedMethod(
            "AnimalRoster", "insert", "Swift.Sequence").Methods[0];
        var parent = (TypeDecl)method.ParentDecl!;
        var pairing = MakeSequencePairingProtocolKind(
            conformerSwiftName: "Swift.Array<SwiftBindingsTestLib.Puppy>",
            elementAssocType: "SwiftBindingsTestLib.Puppy",
            expectedElement: "SwiftBindingsTestLib.Animal");

        Assert.True(ConcreteProtocolSpecializationEmitter
            .DoesPairingSatisfyAssociatedTypeConstraints(method, parent, pairing, db));
    }

    [Fact]
    public void AssociatedTypeConstraints_ClassInheritanceConstraint_LeafIsUnrelatedClass_Rejects()
    {
        // Cat is a sibling class with no Animal in its chain — must reject for
        // `where S.Element : Animal`. Confirms the chain walk doesn't false-accept
        // unrelated classes registered in the same database.
        var db = new ResolvingTypeDatabase();
        var animalName = SwiftTypeName.FromModuleQualifiedName("SwiftBindingsTestLib.Animal");
        var catName = SwiftTypeName.FromModuleQualifiedName("SwiftBindingsTestLib.Cat");
        db.Register(animalName, "SwiftBindingsTestLib", "Animal", TypeRecordKind.Class);
        db.Register(catName, "SwiftBindingsTestLib", "Cat", TypeRecordKind.Class);

        var method = CreateStructWithProtocolConstrainedMethod(
            "AnimalRoster", "insert", "Swift.Sequence").Methods[0];
        var parent = (TypeDecl)method.ParentDecl!;
        var pairing = MakeSequencePairingProtocolKind(
            conformerSwiftName: "Swift.Array<SwiftBindingsTestLib.Cat>",
            elementAssocType: "SwiftBindingsTestLib.Cat",
            expectedElement: "SwiftBindingsTestLib.Animal");

        Assert.False(ConcreteProtocolSpecializationEmitter
            .DoesPairingSatisfyAssociatedTypeConstraints(method, parent, pairing, db));
    }

    [Fact]
    public void AssociatedTypeConstraints_ClassInheritanceConstraint_LeafElementUnresolvable_Rejects()
    {
        // Conformer Element type is missing from the type database (cross-module class
        // we couldn't index when generating the consumer module). With expected
        // resolvable as a class but declared not, we cannot prove subclass relationship.
        // Fail-closed mirrors the broader "unverifiable cross-module class target"
        // semantics of the original Bug 3 fix.
        var db = new ResolvingTypeDatabase();
        db.Register(SwiftTypeName.FromModuleQualifiedName("SwiftBindingsTestLib.Animal"),
            "SwiftBindingsTestLib", "Animal", TypeRecordKind.Class);

        var method = CreateStructWithProtocolConstrainedMethod(
            "AnimalRoster", "insert", "Swift.Sequence").Methods[0];
        var parent = (TypeDecl)method.ParentDecl!;
        var pairing = MakeSequencePairingProtocolKind(
            conformerSwiftName: "Swift.Array<OtherModule.MysteryAnimal>",
            elementAssocType: "OtherModule.MysteryAnimal",
            expectedElement: "SwiftBindingsTestLib.Animal");

        Assert.False(ConcreteProtocolSpecializationEmitter
            .DoesPairingSatisfyAssociatedTypeConstraints(method, parent, pairing, db));
    }

    [Fact]
    public void AssociatedTypeConstraints_ProtocolTarget_ConformerElementConforms_Accepts()
    {
        // True protocol-conformance bound `S.Element : Hashable`. The conformer's recorded
        // Element (Swift.UInt8) is registered with Hashable in its ProtocolConformances list.
        // Filter walks the conformer Element's TypeRecord chain and accepts on direct match.
        var hashableName = SwiftTypeName.FromModuleQualifiedName("Swift.Hashable");
        var uint8Name = SwiftTypeName.FromModuleQualifiedName("Swift.UInt8");
        var db = new ResolvingTypeDatabase();
        db.Register(hashableName, "Swift", "Hashable", TypeRecordKind.Protocol);
        db.Register(uint8Name, "Swift", "UInt8", TypeRecordKind.Struct,
            protocolConformances: new[] { hashableName });

        var method = CreateStructWithProtocolConstrainedMethod(
            "HashableCollection", "init", "Swift.Sequence").Methods[0];
        var parent = (TypeDecl)method.ParentDecl!;
        var pairing = MakeSequencePairingProtocolKind(
            conformerSwiftName: "Swift.Array<Swift.UInt8>",
            elementAssocType: "Swift.UInt8",
            expectedElement: "Swift.Hashable");

        Assert.True(ConcreteProtocolSpecializationEmitter
            .DoesPairingSatisfyAssociatedTypeConstraints(method, parent, pairing, db));
    }

    [Fact]
    public void AssociatedTypeConstraints_ProtocolTarget_ConformerElementDoesNotConform_Rejects()
    {
        // Element is registered, but its ProtocolConformances list does NOT contain the target
        // protocol (and no transitive refining edge reaches it). The previous pass-through
        // semantics would have accepted any Sequence conformer; the tightened filter rejects.
        var hashableName = SwiftTypeName.FromModuleQualifiedName("Swift.Hashable");
        var fooName = SwiftTypeName.FromModuleQualifiedName("TestLib.NonHashableThing");
        var db = new ResolvingTypeDatabase();
        db.Register(hashableName, "Swift", "Hashable", TypeRecordKind.Protocol);
        db.Register(fooName, "TestLib", "NonHashableThing", TypeRecordKind.Struct,
            protocolConformances: Array.Empty<SwiftTypeName>());

        var method = CreateStructWithProtocolConstrainedMethod(
            "HashableCollection", "init", "Swift.Sequence").Methods[0];
        var parent = (TypeDecl)method.ParentDecl!;
        var pairing = MakeSequencePairingProtocolKind(
            conformerSwiftName: "Swift.Array<TestLib.NonHashableThing>",
            elementAssocType: "TestLib.NonHashableThing",
            expectedElement: "Swift.Hashable");

        Assert.False(ConcreteProtocolSpecializationEmitter
            .DoesPairingSatisfyAssociatedTypeConstraints(method, parent, pairing, db));
    }

    [Fact]
    public void AssociatedTypeConstraints_ProtocolTarget_ConformerElementUnresolvable_Rejects()
    {
        // Element record missing entirely (cross-module element type whose database wasn't
        // loaded for this generation pass). Fail-closed mirrors the class-chain path's
        // posture for unresolvable hops — better to drop a specialization than emit a
        // wrapper whose conformance we couldn't verify.
        var hashableName = SwiftTypeName.FromModuleQualifiedName("Swift.Hashable");
        var db = new ResolvingTypeDatabase();
        db.Register(hashableName, "Swift", "Hashable", TypeRecordKind.Protocol);

        var method = CreateStructWithProtocolConstrainedMethod(
            "HashableCollection", "init", "Swift.Sequence").Methods[0];
        var parent = (TypeDecl)method.ParentDecl!;
        var pairing = MakeSequencePairingProtocolKind(
            conformerSwiftName: "Swift.Array<OtherModule.UnresolvedElement>",
            elementAssocType: "OtherModule.UnresolvedElement",
            expectedElement: "Swift.Hashable");

        Assert.False(ConcreteProtocolSpecializationEmitter
            .DoesPairingSatisfyAssociatedTypeConstraints(method, parent, pairing, db));
    }

    [Fact]
    public void AssociatedTypeConstraints_ProtocolTarget_ConformerElementNullProtocolConformances_Rejects()
    {
        // Element record exists but its ProtocolConformances field is null — typically
        // a record loaded from an older module database file that predates the field.
        // We can't verify, so fail-closed (same posture as outright-missing).
        var hashableName = SwiftTypeName.FromModuleQualifiedName("Swift.Hashable");
        var stringName = SwiftTypeName.FromModuleQualifiedName("Swift.String");
        var db = new ResolvingTypeDatabase();
        db.Register(hashableName, "Swift", "Hashable", TypeRecordKind.Protocol);
        db.Register(stringName, "Swift", "String", TypeRecordKind.Struct,
            protocolConformances: null);

        var method = CreateStructWithProtocolConstrainedMethod(
            "HashableCollection", "init", "Swift.Sequence").Methods[0];
        var parent = (TypeDecl)method.ParentDecl!;
        var pairing = MakeSequencePairingProtocolKind(
            conformerSwiftName: "Swift.Array<Swift.String>",
            elementAssocType: "Swift.String",
            expectedElement: "Swift.Hashable");

        Assert.False(ConcreteProtocolSpecializationEmitter
            .DoesPairingSatisfyAssociatedTypeConstraints(method, parent, pairing, db));
    }

    [Fact]
    public void AssociatedTypeConstraints_ProtocolTarget_TransitiveConformance_Accepts()
    {
        // `S.Element : Equatable` accepts an element whose declared conformance is
        // Hashable (Hashable refines Equatable in stdlib). The element itself doesn't
        // list Equatable directly — only Hashable — so the filter must walk the
        // refining edge `Hashable : Equatable` to admit the pairing.
        var equatableName = SwiftTypeName.FromModuleQualifiedName("Swift.Equatable");
        var hashableName = SwiftTypeName.FromModuleQualifiedName("Swift.Hashable");
        var intName = SwiftTypeName.FromModuleQualifiedName("Swift.Int");
        var db = new ResolvingTypeDatabase();
        db.Register(equatableName, "Swift", "Equatable", TypeRecordKind.Protocol);
        db.Register(hashableName, "Swift", "Hashable", TypeRecordKind.Protocol,
            protocolConformances: new[] { equatableName });
        db.Register(intName, "Swift", "Int", TypeRecordKind.Struct,
            protocolConformances: new[] { hashableName });

        var method = CreateStructWithProtocolConstrainedMethod(
            "EquatableCollection", "init", "Swift.Sequence").Methods[0];
        var parent = (TypeDecl)method.ParentDecl!;
        var pairing = MakeSequencePairingProtocolKind(
            conformerSwiftName: "Swift.Array<Swift.Int>",
            elementAssocType: "Swift.Int",
            expectedElement: "Swift.Equatable");

        Assert.True(ConcreteProtocolSpecializationEmitter
            .DoesPairingSatisfyAssociatedTypeConstraints(method, parent, pairing, db));
    }

    [Fact]
    public void AssociatedTypeConstraints_ProtocolTarget_ConformerElementIsExistentialOfTarget_Rejects()
    {
        // Swift rejects `where S.Element : P` paired with `S.Element == any P`:
        // `type 'any P' cannot conform to 'P'`. Pre-fix the exact-name fast path
        // ran before the target-kind branch, so a conformer whose Element was
        // recorded as the protocol existential itself slipped through. Post-fix
        // the fast path is gated to non-protocol targets — the protocol-target
        // branch always requires the conformance walk, which correctly rejects
        // because the protocol's own ProtocolConformances (its inherited
        // protocols) does not include itself.
        var hashableName = SwiftTypeName.FromModuleQualifiedName("Test.HashLike");
        var db = new ResolvingTypeDatabase();
        db.Register(hashableName, "Test", "HashLike", TypeRecordKind.Protocol);

        var method = CreateStructWithProtocolConstrainedMethod(
            "HashSink", "sumHashes", "Swift.Sequence").Methods[0];
        var parent = (TypeDecl)method.ParentDecl!;
        var pairing = MakeSequencePairingProtocolKind(
            conformerSwiftName: "Swift.Array<Test.HashLike>",
            elementAssocType: "Test.HashLike",
            expectedElement: "Test.HashLike");

        Assert.False(ConcreteProtocolSpecializationEmitter
            .DoesPairingSatisfyAssociatedTypeConstraints(method, parent, pairing, db));
    }

    [Fact]
    public void AssociatedTypeConstraints_ProtocolTarget_ConformerElementIsRefiningProtocol_Rejects()
    {
        // Sister case to the existential-of-target rejection: Swift also refuses
        // `[any ChildProtocol]` for `where S.Element : P` when ChildProtocol : P.
        // Only concrete types satisfy a generic protocol-conformance constraint,
        // even when the existential's protocol refines the target. Pre-fix, the
        // DFS in IsDeclaredConformingToProtocol walked ChildProtocol's own
        // ProtocolConformances (its inherited protocols, populated for protocol-
        // kind records) and incorrectly accepted because the inheritance chain
        // reaches P. Post-fix, an existential element is rejected before the walk.
        var pName = SwiftTypeName.FromModuleQualifiedName("Test.HashLike");
        var childName = SwiftTypeName.FromModuleQualifiedName("Test.RichHashLike");
        var db = new ResolvingTypeDatabase();
        db.Register(pName, "Test", "HashLike", TypeRecordKind.Protocol);
        db.Register(childName, "Test", "RichHashLike", TypeRecordKind.Protocol,
            protocolConformances: new[] { pName });

        var method = CreateStructWithProtocolConstrainedMethod(
            "HashSink", "sumHashes", "Swift.Sequence").Methods[0];
        var parent = (TypeDecl)method.ParentDecl!;
        var pairing = MakeSequencePairingProtocolKind(
            conformerSwiftName: "Swift.Array<Test.RichHashLike>",
            elementAssocType: "Test.RichHashLike",
            expectedElement: "Test.HashLike");

        Assert.False(ConcreteProtocolSpecializationEmitter
            .DoesPairingSatisfyAssociatedTypeConstraints(method, parent, pairing, db));
    }

    [Fact]
    public void AssociatedTypeConstraints_ProtocolTarget_TransitiveConformance_Cycle_DoesNotInfiniteLoop()
    {
        // Defensive coverage: pathological self-referential chain (P : P) must terminate
        // via the visited set rather than spin. Should reject — target Q never appears.
        var pName = SwiftTypeName.FromModuleQualifiedName("Test.SelfRefP");
        var qName = SwiftTypeName.FromModuleQualifiedName("Test.SeparateQ");
        var elementName = SwiftTypeName.FromModuleQualifiedName("Test.ElementWithCycle");
        var db = new ResolvingTypeDatabase();
        db.Register(pName, "Test", "SelfRefP", TypeRecordKind.Protocol,
            protocolConformances: new[] { pName });
        db.Register(qName, "Test", "SeparateQ", TypeRecordKind.Protocol);
        db.Register(elementName, "Test", "ElementWithCycle", TypeRecordKind.Struct,
            protocolConformances: new[] { pName });

        var method = CreateStructWithProtocolConstrainedMethod(
            "QCollection", "init", "Swift.Sequence").Methods[0];
        var parent = (TypeDecl)method.ParentDecl!;
        var pairing = MakeSequencePairingProtocolKind(
            conformerSwiftName: "Swift.Array<Test.ElementWithCycle>",
            elementAssocType: "Test.ElementWithCycle",
            expectedElement: "Test.SeparateQ");

        Assert.False(ConcreteProtocolSpecializationEmitter
            .DoesPairingSatisfyAssociatedTypeConstraints(method, parent, pairing, db));
    }

    [Fact]
    public void AssociatedTypeConstraints_ProtocolKindConstraint_TargetNotInDatabase_FailsClosed()
    {
        // Cross-module class target (e.g. `S.Element : RealityKit.Entity` while generating
        // RealityFoundation): the parser tags it ConformanceKind.Protocol because Swift's
        // genericSig text uses `:` for both class-inheritance and protocol-conformance.
        // typeDatabase.TryGetTypeRecord misses on RealityKit types because we don't load
        // RealityKit when generating RealityFoundation. The previous pass-through behavior
        // let every Sequence conformer (Foundation.Data, [UInt8], MeshModelCollection, …)
        // slip through and produced wrappers that fail to Swift-compile. Fail closed: when
        // the target's nature is unknown, require the conformer's Element to exact-match
        // the target's name.
        var db = new ResolvingTypeDatabase();
        var method = CreateStructWithProtocolConstrainedMethod(
            "EntityCollection", "insert", "Swift.Sequence").Methods[0];
        var parent = (TypeDecl)method.ParentDecl!;
        var pairing = MakeSequencePairingProtocolKind(
            conformerSwiftName: "Swift.Array<Swift.UInt8>",
            elementAssocType: "Swift.UInt8",
            expectedElement: "RealityKit.Entity");

        Assert.False(ConcreteProtocolSpecializationEmitter
            .DoesPairingSatisfyAssociatedTypeConstraints(method, parent, pairing, db));
    }

    [Fact]
    public void AssociatedTypeConstraints_ProtocolKindConstraint_TargetNotInDatabase_ElementMatches_Accepts()
    {
        // Mirror of the fail-closed case: when the target is unresolvable but the conformer's
        // recorded Element exactly matches the target name, accept. This preserves legitimate
        // pairings (e.g. Swift.Array<RealityKit.Entity> against `where S.Element : Entity`)
        // even when the target's class-vs-protocol nature can't be determined from the DB.
        var db = new ResolvingTypeDatabase();
        var method = CreateStructWithProtocolConstrainedMethod(
            "EntityCollection", "insert", "Swift.Sequence").Methods[0];
        var parent = (TypeDecl)method.ParentDecl!;
        var pairing = MakeSequencePairingProtocolKind(
            conformerSwiftName: "Swift.Array<RealityKit.Entity>",
            elementAssocType: "RealityKit.Entity",
            expectedElement: "RealityKit.Entity");

        Assert.True(ConcreteProtocolSpecializationEmitter
            .DoesPairingSatisfyAssociatedTypeConstraints(method, parent, pairing, db));
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
    /// Builds a pairing for `init&lt;S: Sequence&gt;() where S.Element: SomeType`
    /// — the class-inheritance / protocol-conformance form. Swift's `genericSig`
    /// parser writes any `:` clause as <see cref="ConformanceKind.Protocol"/>, so
    /// class-inheritance bounds like <c>S.Element: RealityKit.Entity</c> land here.
    /// The bilateral filter must inspect <c>typeDatabase</c> to disambiguate
    /// class-inheritance (must enforce) from true protocol conformance (pass-through).
    /// </summary>
    private static (ConcreteSpecializationEngine.SpecializableParam Param,
                    ConcreteSpecializationEngine.ConcreteConformer Conformer)[]
        MakeSequencePairingProtocolKind(string conformerSwiftName, string? elementAssocType, string expectedElement)
    {
        var sequenceName = SwiftTypeName.FromModuleQualifiedName("Swift.Sequence");
        var expectedElementName = SwiftTypeName.FromModuleQualifiedName(expectedElement);

        var protocolConformance = new GenericParameterConformance(
            new[] { "T" }, sequenceName, ConformanceKind.Protocol);
        var elementConstraint = new GenericParameterConformance(
            new[] { "T", "Element" }, expectedElementName, ConformanceKind.Protocol);

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

    #region SubstituteSelfAndPairingGenericsInTypeSpec — return-type closure

    // A protocol-extension requirement returning `Self` (e.g. AnimationDefinition.repeated()
    // -> Self) is carried in the ABI as the unbound conformer nominal once Self is resolved.
    // The conformer here is `ActionAnimation<ActionType>`, so the bare return `TestLib.Holder`
    // must be closed over the parent pairing → `TestLib.Holder<TestLib.SongItem>`. Without
    // this, C# emits the open generic and Roslyn reports CS0305.
    [Fact]
    public void SubstituteSelfAndPairingGenerics_BareParentNominal_ClosesOverPairing()
    {
        var parent = CreateGenericStructDecl("Holder", "τ_0_0");
        var pairing = BuildParentPairing(parent, "TestLib.SongItem", "SongItem");

        var result = ConcreteProtocolSpecializationEmitter.SubstituteSelfAndPairingGenericsInTypeSpec(
            new NamedTypeSpec("TestLib.Holder"), parent, pairing);

        var named = Assert.IsType<NamedTypeSpec>(result);
        Assert.Equal("TestLib.Holder", named.Name);
        Assert.Single(named.GenericParameters);
        Assert.Equal("TestLib.SongItem", ((NamedTypeSpec)named.GenericParameters[0]).Name);
    }

    [Fact]
    public void SubstituteSelfAndPairingGenerics_LiteralSelf_ResolvesToClosedParent()
    {
        var parent = CreateGenericStructDecl("Holder", "τ_0_0");
        var pairing = BuildParentPairing(parent, "TestLib.SongItem", "SongItem");

        var result = ConcreteProtocolSpecializationEmitter.SubstituteSelfAndPairingGenericsInTypeSpec(
            new NamedTypeSpec("Self"), parent, pairing);

        var named = Assert.IsType<NamedTypeSpec>(result);
        Assert.Equal("TestLib.Holder", named.Name);
        Assert.Single(named.GenericParameters);
        Assert.Equal("TestLib.SongItem", ((NamedTypeSpec)named.GenericParameters[0]).Name);
    }

    [Fact]
    public void SubstituteSelfAndPairingGenerics_UnrelatedNonGenericType_PassesThrough()
    {
        // A return type that is neither Self nor the parent nominal must be left untouched —
        // the bare-parent closure must not over-fire on same-named-but-unrelated types.
        var parent = CreateGenericStructDecl("Holder", "τ_0_0");
        var pairing = BuildParentPairing(parent, "TestLib.SongItem", "SongItem");

        var result = ConcreteProtocolSpecializationEmitter.SubstituteSelfAndPairingGenericsInTypeSpec(
            new NamedTypeSpec("Swift.String"), parent, pairing);

        var named = Assert.IsType<NamedTypeSpec>(result);
        Assert.Equal("Swift.String", named.Name);
        Assert.Empty(named.GenericParameters);
    }

    private static (ConcreteSpecializationEngine.SpecializableParam, ConcreteSpecializationEngine.ConcreteConformer)[]
        BuildParentPairing(TypeDecl parent, string conformerSwift, string conformerCs)
    {
        var conformer = new ConcreteSpecializationEngine.ConcreteConformer(
            SwiftQualifiedName: conformerSwift,
            CSharpType: conformerCs);
        var specParam = new ConcreteSpecializationEngine.SpecializableParam(
            GenericParam: parent.GenericParameters[0],
            ConstraintProtocol: SwiftTypeName.FromModuleQualifiedName("TestLib.Permitted"),
            Conformers: new List<ConcreteSpecializationEngine.ConcreteConformer> { conformer },
            CouplingConstraints: null,
            IsParentGeneric: true);
        return new[] { (specParam, conformer) };
    }

    #endregion
}
