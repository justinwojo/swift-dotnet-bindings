// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Diagnostics.CodeAnalysis;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for <see cref="ConcreteSpecializationEngine"/> — protocol conformer discovery
/// from hints and ABI, and specializable method detection.
/// </summary>
// Collection-serialized: one test drives the process-global ReportCollector skipped-type set
// to exercise the value-struct predicate's skip-set consultation.
public class ConcreteSpecializationEngineTests
{
    private static ITypeDatabase CreateEmptyTypeDatabase() => new EmptyTypeDatabase();

    [Fact]
    public void EmitConcreteSpecializations_PureSwiftClassInstanceMethod_SelfArgIsLeasedPayload()
    {
        // A concrete protocol-generic specialization emitted DIRECTLY on a class (not via an
        // extension) forwards `self` to the @_cdecl P/Invoke. A pure-Swift class exposes its
        // handle as the public `Payload` SafeHandle, so self is forwarded as that SafeHandle and
        // the LibraryImport marshaller leases it for the duration of the call (AddRef/Release, and
        // ObjectDisposedException if it is already closed) — the same mechanism the ordinary
        // method emitter uses. It must never be the private `_handle` field (a CS0103 on class
        // flavors that don't declare one) nor a raw IntPtr snapshot, which a concurrent Dispose()
        // could free mid-call. `some Collection<String>` on a class (the CollectionHost shape)
        // specializes to `Swift.SwiftArray<Swift.SwiftString>` and exercises the class self-arg.
        var db = new ResolvingTypeDatabase { AsyncLibraryName = "SwiftBindings" };
        db.Register(SwiftTypeName.FromModuleQualifiedName("TestLib.Host"), "TestLib", "Host");
        var engine = new ConcreteSpecializationEngine(db);
        var typeDecl = CreateClassWithSomeCollectionElementMethod("Host", "Swift.String");

        var csOutput = new StringWriter();
        var swiftOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(swiftOutput);
        ConcreteProtocolSpecializationEmitter.EmitConcreteSpecializations(
            csWriter, swiftWriter, typeDecl, db, new ModuleEmissionContext(), engine, NullLogger.Instance);

        var cs = csOutput.ToString();

        // The specialized overload must be emitted (guards against a silent no-op test).
        Assert.Contains("public string JoinItems(Swift.SwiftArray<Swift.SwiftString> items)", cs);
        // The P/Invoke declares self as a SafeHandle, which is what makes the marshaller lease it.
        Assert.Contains("global::System.Runtime.InteropServices.SafeHandle self_", cs);
        // ...and the call forwards the public accessor, never the private field or a raw pointer.
        Assert.DoesNotContain("_handle.DangerousGetHandle()", cs);
        Assert.DoesNotContain("IntPtr self_", cs);
    }

    [Fact]
    public void EmitConcreteSpecializations_ObjCRootedClassInstanceMethod_SelfArgUsesGetSwiftHandle()
    {
        // Shape B regression (DGCharts ChartDataSet, ChartData): an ObjC-rooted class has no
        // `_handle` field and no `Payload` SafeHandle at all — its handle IS `NSObject.Handle` —
        // so the specialized self-arg keeps the IntPtr `GetSwiftHandle()` accessor that every
        // class flavor emits. There is no SafeHandle to lease on this flavor; forwarding a
        // SafeHandle here would be a CS1061, so the leased shape must NOT be applied blindly.
        var db = new ResolvingTypeDatabase { AsyncLibraryName = "SwiftBindings" };
        db.Register(SwiftTypeName.FromModuleQualifiedName("TestLib.Host"), "TestLib", "Host");
        var engine = new ConcreteSpecializationEngine(db);
        var typeDecl = CreateClassWithSomeCollectionElementMethod("Host", "Swift.String");
        typeDecl.IsObjCRooted = true;

        var csOutput = new StringWriter();
        var swiftOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(swiftOutput);
        ConcreteProtocolSpecializationEmitter.EmitConcreteSpecializations(
            csWriter, swiftWriter, typeDecl, db, new ModuleEmissionContext(), engine, NullLogger.Instance);

        var cs = csOutput.ToString();

        Assert.Contains("public string JoinItems(Swift.SwiftArray<Swift.SwiftString> items)", cs);
        Assert.Contains("GetSwiftHandle()", cs);
        Assert.Contains("IntPtr self_", cs);
        // ...never the raw private field, which does not exist on an ObjC-rooted class.
        Assert.DoesNotContain("_handle.DangerousGetHandle()", cs);
    }

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

    // ==================== Multi-protocol generic constraints — fail-closed secondary (F20) ====================
    //
    // When a generic param carries more than one protocol constraint (`<T : A & B>`),
    // FindSpecializableProtocolConstraint picks ONE protocol (A) and GetConformers(A)
    // supplies the candidate conformers. ConformerSatisfiesAllConstraints must then verify
    // each candidate against the NON-selected constraints (B). The old code accepted any
    // verdict that was not ABI-Disproved — so a conformer the ABI could neither confirm nor
    // deny (Uncertain) and that no curated hint listed under B slipped through, emitting a
    // CSM<conformer> overload whose `conformer : B` requirement the Swift wrapper cannot
    // satisfy. The F20 fail-closed flip rejects unless the conformer is a KNOWN conformer of
    // B — ABI-declared OR curated-hint-listed-and-not-ABI-disproved (the same hint+ABI
    // discovery GetConformers performs for the selected protocol).

    [Fact]
    public void FindSpecializableMethods_MultiConstraintParam_SecondaryUnprovableNoHint_DropsConformer()
    {
        // <T : ProcessableA & ProcessableB>. ConcreteItem is ABI-indexed conforming to
        // ProcessableA only (the selected protocol). ProcessableB is a same-module protocol
        // ConcreteItem does NOT declare and no hint lists it under → VerifyHintAgainstAbi
        // returns Uncertain (same-module plausible refiner). Old behavior leaked ConcreteItem
        // (Uncertain != Disproved). Fail-closed drops it; with no surviving conformer the
        // method is not specializable.
        var db = new ResolvingTypeDatabase();
        var conformerTypeName = SwiftTypeName.FromModuleQualifiedName("TestLib.ConcreteItem");
        db.Register(conformerTypeName, "TestLib", "ConcreteItem");

        var engine = new ConcreteSpecializationEngine(db);
        var moduleDecl = CreateModuleWithConformer("TestLib", "TestLib.ConcreteItem", "TestLib.ProcessableA");
        engine.IndexModuleConformances(moduleDecl);

        var typeDecl = CreateStructWithMultiConstraintMethod(
            "Processor", "process", "TestLib.ProcessableA", "TestLib.ProcessableB");

        var result = engine.FindSpecializableMethods(typeDecl);

        Assert.Empty(result);
    }

    [Fact]
    public void FindSpecializableMethods_MultiConstraintParam_SecondaryHintBacked_KeepsConformer()
    {
        // <T : Foundation.DataProtocol & Foundation.ContiguousBytes>. Foundation.Data is a
        // curated hint conformer of BOTH protocols (specialization-hints.json) but lives in
        // no indexed module here, so the ABI can neither confirm nor deny the ContiguousBytes
        // membership (Uncertain). The fail-closed flip must still admit it — the ledger's
        // "hint-backed cross-module conformance → Yes" — because GetConformers(ContiguousBytes)
        // lists Data. This guards the flip against over-rejecting genuine hint conformances.
        var engine = new ConcreteSpecializationEngine(CreateEmptyTypeDatabase());

        var typeDecl = CreateStructWithMultiConstraintMethod(
            "Sink", "consume", "Foundation.DataProtocol", "Foundation.ContiguousBytes");

        var result = engine.FindSpecializableMethods(typeDecl);

        Assert.Single(result);
        Assert.Single(result[0].SpecializableParams);
        Assert.Contains(result[0].SpecializableParams[0].Conformers,
            c => c.SwiftQualifiedName == "Foundation.Data");
    }

    [Fact]
    public void FindSpecializableMethods_MultiConstraintParam_SecondaryAbiConfirmed_KeepsConformer()
    {
        // <T : ProcessableA & ProcessableB>. ConcreteItem is ABI-indexed declaring BOTH
        // conformances, so the secondary constraint resolves ABI-Confirmed. The conformer
        // must survive — GetConformers folds ABI conformers in, so the membership check
        // covers ABI-declared secondaries too (not just hint-backed ones).
        var db = new ResolvingTypeDatabase();
        var conformerTypeName = SwiftTypeName.FromModuleQualifiedName("TestLib.ConcreteItem");
        db.Register(conformerTypeName, "TestLib", "ConcreteItem");

        var engine = new ConcreteSpecializationEngine(db);
        var moduleDecl = CreateModuleWithTwoConformances(
            "TestLib", "TestLib.ConcreteItem", "TestLib.ProcessableA", "TestLib.ProcessableB");
        engine.IndexModuleConformances(moduleDecl);

        var typeDecl = CreateStructWithMultiConstraintMethod(
            "Processor", "process", "TestLib.ProcessableA", "TestLib.ProcessableB");

        var result = engine.FindSpecializableMethods(typeDecl);

        Assert.Single(result);
        Assert.Single(result[0].SpecializableParams);
        Assert.Contains(result[0].SpecializableParams[0].Conformers,
            c => c.SwiftQualifiedName == "TestLib.ConcreteItem");
    }

    [Fact]
    public void ParentTupleSatisfiesMethodConstraints_Conformance_ExtraConstraintUnprovableNoHint_FailsClosed()
    {
        // Method-level `where τ_0_0 : ExtraProto` is stricter than the parent type's
        // declaration (parent param carries no constraints here, so it is not skipped as
        // a parent-level constraint). The chosen parent conformer (SongItem) neither
        // ABI-declares nor is hint-listed under ExtraProto → Uncertain. Old behavior accepted
        // any non-Disproved verdict, emitting a closed-form CSM whose method `where` clause
        // the conformer fails. Same fail-open pattern as ConformerSatisfiesAllConstraints —
        // fail-closed: reject.
        var engine = new ConcreteSpecializationEngine(CreateEmptyTypeDatabase());
        var method = CreateMethodWithSig("restrict", "<τ_0_0 where τ_0_0 : TestLib.ExtraProto>");
        var parent = CreateGenericStructDecl("Bag", "τ_0_0");

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
            "an unprovable, un-hinted method-level conformance constraint must fail-closed");
    }

    [Fact]
    public void ParentTupleSatisfiesMethodConstraints_Conformance_ExtraConstraintHintBacked_Admits()
    {
        // Method-level `where τ_0_0 : Foundation.ContiguousBytes` with the parent conformer
        // Foundation.Data — a curated hint conformer of ContiguousBytes. The conformer is a
        // known conformer of the extra constraint (GetConformers lists it), so the stricter
        // method-level clause is satisfied and the tuple admits. Guards the fix against
        // over-rejecting genuine hint-backed method-level constraints.
        var engine = new ConcreteSpecializationEngine(CreateEmptyTypeDatabase());
        var method = CreateMethodWithSig("restrict", "<τ_0_0 where τ_0_0 : Foundation.ContiguousBytes>");
        var parent = CreateGenericStructDecl("Bag", "τ_0_0");

        var conformer = new ConcreteSpecializationEngine.ConcreteConformer(
            SwiftQualifiedName: "Foundation.Data",
            CSharpType: "Data");
        var specParam = new ConcreteSpecializationEngine.SpecializableParam(
            GenericParam: parent.GenericParameters[0],
            ConstraintProtocol: SwiftTypeName.FromModuleQualifiedName("Foundation.DataProtocol"),
            Conformers: new List<ConcreteSpecializationEngine.ConcreteConformer> { conformer },
            CouplingConstraints: null,
            IsParentGeneric: true);

        var parentTuple = new List<(ConcreteSpecializationEngine.SpecializableParam, ConcreteSpecializationEngine.ConcreteConformer)>
        {
            (specParam, conformer)
        };

        Assert.True(
            engine.ParentTupleSatisfiesMethodConstraints(method, parent, parentTuple),
            "a hint-backed method-level conformance constraint must be admitted");
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
    public void FindSpecializableMethods_AssociatedTypeSugarVsCanonical_StillSpecializes()
    {
        // The conformer's associated-type value can carry the canonical spelling
        // (`Swift.Array<Swift.UInt8>` — the conformance-graph witness merge stores
        // `resolved.ToString(true)`), while the method's same-type constraint target
        // is the sugared ABI printing (`[Swift.UInt8]`). Both denote the same type,
        // so the conformer satisfies the constraint and the method must specialize;
        // a raw ordinal compare falsely rejects it and the method silently loses
        // the conformer.
        var db = new ResolvingTypeDatabase();
        var conformerTypeName = SwiftTypeName.FromModuleQualifiedName("TestLib.ByteChunk");
        db.Register(conformerTypeName, "TestLib", "ByteChunk");

        var engine = new ConcreteSpecializationEngine(db);

        var moduleDecl = CreateModuleWithConformer("TestLib", "TestLib.ByteChunk", "TestLib.Chunkable");
        ((StructDecl)moduleDecl.Types[0]).Typealiases["Element"] = "Swift.Array<Swift.UInt8>";
        engine.IndexModuleConformances(moduleDecl);

        var typeDecl = CreateStructWithProtocolConstrainedMethod("Processor", "process", "TestLib.Chunkable");
        // Same-type constraint: τ_1_0.Element == [Swift.UInt8]
        typeDecl.Methods[0].GenericParameters[0].AssosiatedTypeConformances.Add(
            new GenericParameterConformance(
                new[] { "τ_1_0", "Element" },
                SwiftTypeName.FromModuleQualifiedName("[Swift.UInt8]"),
                ConformanceKind.ConcreteType));

        var result = engine.FindSpecializableMethods(typeDecl);

        Assert.Single(result);
        Assert.Single(result[0].SpecializableParams);
        var conformer = Assert.Single(result[0].SpecializableParams[0].Conformers);
        Assert.Equal("TestLib.ByteChunk", conformer.SwiftQualifiedName);
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
    public void EmitConcreteSpecializations_ThrowingConstructor_NestedConformer_EmitsThrowingFromFactory()
    {
        // The CSM dispatcher historically dropped `IsConstructor && Throws`, so a throwing generic
        // initializer (the CryptoKit HPKE Sender/Recipient shape — every HPKE init is
        // `init<…>(…) throws`) never produced a From{Conformer} factory even though the wrapper +
        // ConstructorAdmissibility machinery composes throws+ctor. This drives the full sync CSM
        // emission path (not just discovery) over a deeply-nested THREE-segment conformer
        // (`TestLib.Outer.Inner`, exactly HPKE's `Curve25519.KeyAgreement.PublicKey` depth) and
        // asserts (a) the per-conformer factory is emitted and (b) it is a *throwing* factory —
        // the wrapper carries the error-out parameter. Before the skip was lifted this emitted
        // nothing; it is the unit-level red/green witness for the dispatcher change.
        var db = new ResolvingTypeDatabase { AsyncLibraryName = "SwiftBindings" };
        var conformerTypeName = SwiftTypeName.FromModuleQualifiedName("TestLib.Outer.Inner");
        db.Register(conformerTypeName, "TestLib", "Outer.Inner");
        // The host's result type must project for the factory return type.
        db.Register(SwiftTypeName.FromModuleQualifiedName("TestLib.Box"), "TestLib", "Box");

        var engine = new ConcreteSpecializationEngine(db);
        var moduleDecl = CreateModuleWithConformer("TestLib", "TestLib.Outer.Inner", "TestLib.Processable");
        engine.IndexModuleConformances(moduleDecl);

        var typeDecl = CreateStructWithProtocolConstrainedConstructor(
            "Box", "TestLib.Processable", throws: true);

        var csOutput = new StringWriter();
        var swiftOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(swiftOutput);

        ConcreteProtocolSpecializationEmitter.EmitConcreteSpecializations(
            csWriter, swiftWriter, typeDecl, db, new ModuleEmissionContext(), engine, NullLogger.Instance);

        var cs = csOutput.ToString();
        var swift = swiftOutput.ToString();

        // (a) the per-conformer factory exists for the three-segment conformer, with a readable
        // PascalCase token (TestLib.Outer.Inner → TestLibOuterInner, not the old TestLib_Outer_Inner)
        Assert.Contains("FromTestLibOuterInner", cs);
        // (b) it is a throwing factory — the C# P/Invoke + factory carry the Swift error-out,
        // and the @_cdecl wrapper opens a do/catch. Asserting these (not exact strings) pins the
        // throwing-ctor ABI without coupling to formatting.
        Assert.Contains("errorPtr", cs);
        Assert.Contains("errorOut", swift);
    }

    [Fact]
    public void EmitConcreteSpecializations_ThrowingConstructor_ConcreteDataParam_MarshalsViaByteArray()
    {
        // HPKE's Sender/Recipient inits carry a concrete `info: Foundation.Data` param alongside
        // the specializable generic key. Foundation.Data classifies as the NativeRemapped ABI
        // category (Data ↔ NSData), which the concrete-param compatibility preflight historically
        // rejected — so a generic init with a concrete Data param dropped to a generic-only stub
        // even after the throwing-ctor skip was lifted, and HPKE construction stayed unreachable.
        // This drives the full sync CSM emission over a three-segment conformer whose throwing
        // init takes a concrete Data param and asserts the factory (i) is emitted, (ii) exposes
        // the idiomatic byte[] public surface, and (iii) crosses the @_cdecl boundary as the
        // ownership-balanced two-Int-word decomposition (C# FromByteArray + Swift unsafeBitCast
        // back to Foundation.Data) rather than a pointer/handle. Before the NativeRemapped arm
        // this emitted nothing.
        var db = new ResolvingTypeDatabase { AsyncLibraryName = "SwiftBindings" };
        var conformerTypeName = SwiftTypeName.FromModuleQualifiedName("TestLib.Outer.Inner");
        db.Register(conformerTypeName, "TestLib", "Outer.Inner");
        db.Register(SwiftTypeName.FromModuleQualifiedName("TestLib.Box"), "TestLib", "Box");
        // Foundation.Data registered with a NativeTypeName so ClassifyParam returns NativeRemapped,
        // matching the production classification that the fix admits.
        db.Register(SwiftTypeName.FromModuleQualifiedName("Foundation.Data"), "Swift.Foundation", "Data",
            nativeTypeName: CSharpTypeName.FromNamespaceAndName("Foundation", "NSData"));

        var engine = new ConcreteSpecializationEngine(db);
        var moduleDecl = CreateModuleWithConformer("TestLib", "TestLib.Outer.Inner", "TestLib.Processable");
        engine.IndexModuleConformances(moduleDecl);

        var typeDecl = CreateStructWithProtocolConstrainedConstructor(
            "Box", "TestLib.Processable", throws: true, withConcreteDataParam: true);

        var csOutput = new StringWriter();
        var swiftOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(swiftOutput);

        ConcreteProtocolSpecializationEmitter.EmitConcreteSpecializations(
            csWriter, swiftWriter, typeDecl, db, new ModuleEmissionContext(), engine, NullLogger.Instance);

        var cs = csOutput.ToString();
        var swift = swiftOutput.ToString();

        // (i) the factory is emitted for the three-segment conformer despite the concrete Data param
        Assert.Contains("FromTestLibOuterInner", cs);
        // (ii) idiomatic byte[] public surface for the concrete Data param
        Assert.Contains("byte[] info", cs);
        // (iii) C# converts via FromByteArray then decomposes into two nint words; the Swift wrapper
        // reconstructs via unsafeBitCast to Foundation.Data. Semantic checks, not exact formatting.
        // The word naming ({name}_w0/{name}_w1 off a {name}Swift holder) matches the ordinary-cdecl
        // Data decomposition (WrapperEmitter.Marshalling) so the two Data emitters stay in sync.
        Assert.Contains("Swift.Foundation.Data.FromByteArray(info)", cs);
        Assert.Contains("info_w0", cs);
        Assert.Contains("info_w1", cs);
        Assert.Contains("to: Foundation.Data.self", swift);
    }

    [Fact]
    public void EmitConcreteSpecializations_OwnershipTransferReturn_ThrowBeforeHandoffFreesResultBuffer()
    {
        // The ownership-transfer arm allocates the indirect-result buffer with NativeMemory.Alloc
        // and hands it to the returned SafeHandle at the marshal call. Everything between those two
        // points can throw WITHOUT the handle ever adopting the buffer — most importantly the
        // [LibraryImport] marshaller, which rejects a disposed SafeHandle argument with
        // ObjectDisposedException before native code is entered, so the throw happens after the
        // allocation and before Swift ever runs. The arm previously had no try/finally at all (only
        // an inline free on the Swift-error path), so every such rejection leaked the buffer.
        //
        // Behaviour asserted, not formatting: the allocation is guarded, the reclaim is conditioned
        // on ownership NOT having transferred, and the flag flips only after the marshal call.
        var cs = EmitOwnershipTransferFactory();

        Assert.Contains("NativeMemory.Alloc", cs);
        Assert.Contains("finally", cs);

        int allocAt = cs.IndexOf("NativeMemory.Alloc", System.StringComparison.Ordinal);
        int tryAt = cs.IndexOf("try", allocAt, System.StringComparison.Ordinal);
        int callAt = cs.IndexOf("SBW_CSM_", allocAt, System.StringComparison.Ordinal);
        int marshalAt = cs.IndexOf("MarshalFromSwift", allocAt, System.StringComparison.Ordinal);
        int finallyAt = cs.IndexOf("finally", marshalAt, System.StringComparison.Ordinal);
        int freeAt = cs.IndexOf("NativeMemory.Free", finallyAt, System.StringComparison.Ordinal);

        Assert.True(tryAt > allocAt && tryAt < callAt && callAt < marshalAt,
            $"The specialized call must run inside a try opened after the allocation.\n{cs}");
        Assert.True(freeAt > finallyAt,
            $"The buffer must be reclaimed from the finally, not only on the Swift-error path.\n{cs}");

        // The reclaim is conditional on THE ownership flag, and that flag starts out false outside
        // the guarded region — an unconditional finally free would double-free the buffer the
        // returned SafeHandle now owns, and a flag that started true would reclaim nothing.
        var flagDecl = System.Text.RegularExpressions.Regex.Match(cs, @"bool (\w+) = false;");
        Assert.True(flagDecl.Success, $"The arm must declare an ownership flag initialized false.\n{cs}");
        string flag = flagDecl.Groups[1].Value;
        Assert.True(flagDecl.Index > allocAt && flagDecl.Index < tryAt,
            $"The ownership flag must be declared after the allocation and before the try.\n{cs}");

        string finallyClause = cs.Substring(finallyAt, cs.IndexOf('\n', freeAt) - finallyAt);
        Assert.Contains($"if (!{flag})", finallyClause);
    }

    [Fact]
    public void EmitConcreteSpecializations_OwnershipTransferReturn_SuccessPathKeepsHandoffAndFreesOnce()
    {
        // The success-path handoff must survive the leak fix: the flag flips AFTER the marshal call
        // (so a throw inside the marshal still reclaims) and BEFORE the return, and the buffer is
        // reclaimed from exactly one place. The Swift-error path used to free inline; with the
        // finally in place that inline free would be a double free, so it must be gone.
        var cs = EmitOwnershipTransferFactory();

        int allocAt = cs.IndexOf("NativeMemory.Alloc", System.StringComparison.Ordinal);
        int callAt = cs.IndexOf("SBW_CSM_", allocAt, System.StringComparison.Ordinal);
        int marshalAt = cs.IndexOf("MarshalFromSwift", System.StringComparison.Ordinal);
        int returnAt = cs.IndexOf("return ", marshalAt, System.StringComparison.Ordinal);
        int throwErrorAt = cs.IndexOf("ThrowSwiftError", System.StringComparison.Ordinal);

        Assert.True(marshalAt >= 0 && returnAt > marshalAt,
            $"The marshal call must be captured into a local ahead of the return.\n{cs}");

        // The whole point of the flag is WHERE it flips: after the handoff the marshal call
        // performs, and before the return. Flipping it earlier — anywhere at or before the P/Invoke
        // — would silence the finally for exactly the pre-native rejection this guards against.
        string flag = System.Text.RegularExpressions.Regex.Match(cs, @"bool (\w+) = false;").Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(flag), $"The arm must declare an ownership flag.\n{cs}");
        int flagSetAt = cs.IndexOf($"{flag} = true;", System.StringComparison.Ordinal);
        Assert.True(callAt > allocAt && callAt < marshalAt,
            $"The specialized call must run between the allocation and the marshal.\n{cs}");
        Assert.True(flagSetAt > marshalAt && flagSetAt < returnAt,
            $"Ownership must be recorded after the marshal handoff and before the return.\n{cs}");

        // Exactly one reclaim site, and it is not on the error-check line.
        int freeCount = System.Text.RegularExpressions.Regex.Matches(cs, "NativeMemory\\.Free").Count;
        Assert.True(freeCount == 1, $"Expected a single reclaim site, found {freeCount}.\n{cs}");

        int errorLineStart = cs.LastIndexOf('\n', throwErrorAt) + 1;
        string errorLine = cs.Substring(errorLineStart, cs.IndexOf('\n', throwErrorAt) - errorLineStart);
        Assert.DoesNotContain("NativeMemory.Free", errorLine);
    }

    /// <summary>
    /// Emits a throwing generic constructor on a NON-frozen struct host — the shape whose
    /// indirect result buffer transfers to the returned SafeHandle (`needsResultPtrOwnershipTransfer`).
    /// The host record is registered without <c>TypeRecordFlags.Frozen</c>, which is what puts the
    /// return on that arm.
    /// </summary>
    private static string EmitOwnershipTransferFactory()
    {
        var db = new ResolvingTypeDatabase { AsyncLibraryName = "SwiftBindings" };
        var conformerTypeName = SwiftTypeName.FromModuleQualifiedName("TestLib.Outer.Inner");
        db.Register(conformerTypeName, "TestLib", "Outer.Inner");
        db.Register(SwiftTypeName.FromModuleQualifiedName("TestLib.Box"), "TestLib", "Box");

        var engine = new ConcreteSpecializationEngine(db);
        var moduleDecl = CreateModuleWithConformer("TestLib", "TestLib.Outer.Inner", "TestLib.Processable");
        engine.IndexModuleConformances(moduleDecl);

        var typeDecl = CreateStructWithProtocolConstrainedConstructor(
            "Box", "TestLib.Processable", throws: true);

        var csOutput = new StringWriter();
        var swiftOutput = new StringWriter();
        ConcreteProtocolSpecializationEmitter.EmitConcreteSpecializations(
            new CSharpWriter(csOutput), new SwiftWriter(swiftOutput), typeDecl, db,
            new ModuleEmissionContext(), engine, NullLogger.Instance);

        return csOutput.ToString();
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
    public void EmitConcreteSpecializations_ParentOnlyPropertyGetter_ReadsPropertyWithoutCallParens()
    {
        // Regression (RealityFoundation `FromToByAction<T>.isReversible`/`isAdditive`): a read-only
        // protocol-extension Bool PROPERTY default is surfaced as a synthetic zero-parameter getter
        // method (IsExtensionPropertyGetter=true) and, when the conformer is a generic parent, flows
        // into the CSM parent-only path. That path historically emitted `__self.isReversible()` —
        // calling a Bool value like a function — so swiftc rejected the whole wrapper with "cannot
        // call value of non-function type 'Bool'" and the SDK gave up (SWIFTBIND051). The getter
        // must be READ, not invoked. Asserts the emitted Swift reads `.isReversible` with NO call
        // parens while still folding Bool → Int8 (`? 1 : 0`).
        // AsyncLibraryName non-empty → GenerationMode.XCFramework, which the parent-generic
        // CSM emission gates on (WrapperValidation.IsXCFrameworkMode).
        var db = new ResolvingTypeDatabase { AsyncLibraryName = "SwiftBindings" };
        var conformerTypeName = SwiftTypeName.FromModuleQualifiedName("TestLib.ConcreteItem");
        db.Register(conformerTypeName, "TestLib", "ConcreteItem");

        var engine = new ConcreteSpecializationEngine(db);
        var moduleDecl = CreateModuleWithConformer("TestLib", "TestLib.ConcreteItem", "TestLib.Processable");
        engine.IndexModuleConformances(moduleDecl);

        var typeDecl = CreateGenericStructWithParentOnlyPropertyGetter(
            "Bag", "isReversible", "TestLib.Processable");

        var csOutput = new StringWriter();
        var swiftOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(swiftOutput);

        // Parent-generic specs route through the dedicated generic-parent entry point.
        ConcreteProtocolSpecializationEmitter.EmitConcreteSpecializationsForGenericParent(
            csWriter, swiftWriter, typeDecl, db, new ModuleEmissionContext(), engine, NullLogger.Instance);

        var swift = swiftOutput.ToString();

        // The getter is read as a property...
        Assert.Contains("__self.isReversible", swift);
        // ...NOT invoked like a function (the bug shape).
        Assert.DoesNotContain("__self.isReversible()", swift);
        // Bool return still folds to the Int8 cdecl shape.
        Assert.Contains("? 1 : 0", swift);
    }

    // ==================== Typed-collection property projection (MusicItemCollection<T> shape) ====================
    //
    // A generic parent exposing a property whose type is a bound generic MENTIONING the parent
    // parameter (`MusicLibraryResponse<T>.items : MusicItemCollection<T>`) resolves that parameter
    // to Swift.AnyType on the open shell — PropertyHandler skips it (AnyTypeFallback), leaving the
    // property dead. FindSpecializableProperties discovers exactly this shape so the emitter can
    // project a closed per-conformer getter through the parent-CSM extension path.

    [Fact]
    public void FindSpecializableProperties_ContainerPropertyMentioningParentParam_ReturnsProperty()
    {
        // `Bag<T: Processable>.items : TypedBag<T>` — the AnyTypeFallback shape. The property's
        // return is a bound generic whose argument is the parent param, so it must be discovered.
        var db = new ResolvingTypeDatabase();
        db.Register(SwiftTypeName.FromModuleQualifiedName("TestLib.ConcreteItem"), "TestLib", "ConcreteItem");

        var engine = new ConcreteSpecializationEngine(db);
        var moduleDecl = CreateModuleWithConformer("TestLib", "TestLib.ConcreteItem", "TestLib.Processable");
        engine.IndexModuleConformances(moduleDecl);

        var typeDecl = CreateGenericStructWithContainerProperty(
            "Bag", "items", "TestLib.Processable",
            new NamedTypeSpec("TestLib.TypedBag", new NamedTypeSpec("τ_0_0")));

        var result = engine.FindSpecializableProperties(typeDecl);

        Assert.Single(result);
        Assert.Equal("items", result[0].Property.Name);
        // The synthetic getter is re-keyed onto the property's Swift name (renders `Items`, not
        // `Items_Get`) and flagged as a property read (no call parens in the Swift wrapper).
        Assert.Equal("items", result[0].Getter.Name);
        Assert.True(result[0].Getter.IsExtensionPropertyGetter);
        Assert.Single(result[0].ParentParams);
        Assert.True(result[0].ParentParams[0].IsParentGeneric);
    }

    [Fact]
    public void FindSpecializableProperties_ConcreteReturnProperty_ReturnsEmpty()
    {
        // A property whose return is a bound generic that does NOT mention the parent param
        // (`items : TypedBag<Int>`) projects fine on the open shell — no per-T projection needed.
        var db = new ResolvingTypeDatabase();
        db.Register(SwiftTypeName.FromModuleQualifiedName("TestLib.ConcreteItem"), "TestLib", "ConcreteItem");

        var engine = new ConcreteSpecializationEngine(db);
        var moduleDecl = CreateModuleWithConformer("TestLib", "TestLib.ConcreteItem", "TestLib.Processable");
        engine.IndexModuleConformances(moduleDecl);

        var typeDecl = CreateGenericStructWithContainerProperty(
            "Bag", "items", "TestLib.Processable",
            new NamedTypeSpec("TestLib.TypedBag", new NamedTypeSpec("Swift.Int")));

        var result = engine.FindSpecializableProperties(typeDecl);

        Assert.Empty(result);
    }

    [Fact]
    public void FindSpecializableProperties_BareParentParamProperty_ReturnsEmpty()
    {
        // A property returning the bare parent param (`first : T`) is not a bound-generic
        // container — GenericParameters.Count == 0 — so it is out of this projection's scope.
        var db = new ResolvingTypeDatabase();
        db.Register(SwiftTypeName.FromModuleQualifiedName("TestLib.ConcreteItem"), "TestLib", "ConcreteItem");

        var engine = new ConcreteSpecializationEngine(db);
        var moduleDecl = CreateModuleWithConformer("TestLib", "TestLib.ConcreteItem", "TestLib.Processable");
        engine.IndexModuleConformances(moduleDecl);

        var typeDecl = CreateGenericStructWithContainerProperty(
            "Bag", "first", "TestLib.Processable",
            new NamedTypeSpec("τ_0_0"));

        var result = engine.FindSpecializableProperties(typeDecl);

        Assert.Empty(result);
    }

    [Fact]
    public void FindSpecializableProperties_StaticProperty_ReturnsEmpty()
    {
        // Static properties have no instance receiver to extend per conformer — excluded.
        var db = new ResolvingTypeDatabase();
        db.Register(SwiftTypeName.FromModuleQualifiedName("TestLib.ConcreteItem"), "TestLib", "ConcreteItem");

        var engine = new ConcreteSpecializationEngine(db);
        var moduleDecl = CreateModuleWithConformer("TestLib", "TestLib.ConcreteItem", "TestLib.Processable");
        engine.IndexModuleConformances(moduleDecl);

        var typeDecl = CreateGenericStructWithContainerProperty(
            "Bag", "items", "TestLib.Processable",
            new NamedTypeSpec("TestLib.TypedBag", new NamedTypeSpec("τ_0_0")),
            isStatic: true);

        var result = engine.FindSpecializableProperties(typeDecl);

        Assert.Empty(result);
    }

    [Fact]
    public void FindSpecializableProperties_NonResolvableParent_ReturnsEmpty()
    {
        // The parent generic must hint-resolve to usable conformers (all-or-nothing), the same
        // gate the method path uses — otherwise the emitter has no cartesian to enumerate.
        var engine = new ConcreteSpecializationEngine(CreateEmptyTypeDatabase());

        var typeDecl = CreateGenericStructWithContainerProperty(
            "Bag", "items", "TestLib.UnknownProtocol",
            new NamedTypeSpec("TestLib.TypedBag", new NamedTypeSpec("τ_0_0")));

        var result = engine.FindSpecializableProperties(typeDecl);

        Assert.Empty(result);
    }

    [Fact]
    public void FindSpecializableProperties_StdlibContainerReturn_ReturnsEmpty()
    {
        // A stdlib container of the parent param (`items : [Item]` → Swift.Array<τ_0_0>) already
        // projects on the OPEN generic shell as IReadOnlyList<TItem> (the factory routes it through
        // a real ArrayProjection with the parent's C# type param — no AnyType tombstone). Admitting
        // it would emit a second, shadowed `Items()` extension alongside the working property; only
        // USER-defined bound generics (which tombstone to AnyType) are the CSM target.
        var db = new ResolvingTypeDatabase();
        db.Register(SwiftTypeName.FromModuleQualifiedName("TestLib.ConcreteItem"), "TestLib", "ConcreteItem");

        var engine = new ConcreteSpecializationEngine(db);
        var moduleDecl = CreateModuleWithConformer("TestLib", "TestLib.ConcreteItem", "TestLib.Processable");
        engine.IndexModuleConformances(moduleDecl);

        var typeDecl = CreateGenericStructWithContainerProperty(
            "Bag", "items", "TestLib.Processable",
            new NamedTypeSpec("Swift.Array", new NamedTypeSpec("τ_0_0")));

        var result = engine.FindSpecializableProperties(typeDecl);

        Assert.Empty(result);
    }

    [Fact]
    public void FindSpecializableProperties_AsyncOrThrowsOrMutatingGetter_ReturnsEmpty()
    {
        // The CSM getter body is a plain synchronous instance read (`__self.name`) with no @_cdecl
        // form for effectful accessors — async / throwing / mutating getters are excluded even
        // though their return is the in-scope user-defined-container shape.
        var db = new ResolvingTypeDatabase();
        db.Register(SwiftTypeName.FromModuleQualifiedName("TestLib.ConcreteItem"), "TestLib", "ConcreteItem");

        var engine = new ConcreteSpecializationEngine(db);
        var moduleDecl = CreateModuleWithConformer("TestLib", "TestLib.ConcreteItem", "TestLib.Processable");
        engine.IndexModuleConformances(moduleDecl);

        TypeSpec Container() => new NamedTypeSpec("TestLib.TypedBag", new NamedTypeSpec("τ_0_0"));

        Assert.Empty(engine.FindSpecializableProperties(CreateGenericStructWithContainerProperty(
            "Bag", "items", "TestLib.Processable", Container(), isAsync: true)));
        Assert.Empty(engine.FindSpecializableProperties(CreateGenericStructWithContainerProperty(
            "Bag", "items", "TestLib.Processable", Container(), throws: true)));
        Assert.Empty(engine.FindSpecializableProperties(CreateGenericStructWithContainerProperty(
            "Bag", "items", "TestLib.Processable", Container(), isMutating: true)));
    }

    [Fact]
    public void EmitConcreteSpecializationsForGenericParent_ContainerProperty_ProjectsClosedGetterPerConformer()
    {
        // End-to-end unit witness: the container property projects to a closed extension getter
        // per conformer. The public C# getter is named `Items`, receives the CLOSED parent
        // (`Bag<ConcreteItem>`) and returns the SUBSTITUTED container (`TypedBag<ConcreteItem>` —
        // no leaked `T`/AnyType), and the Swift wrapper READS `__self.items` with NO call parens.
        var db = new ResolvingTypeDatabase { AsyncLibraryName = "SwiftBindings" };
        db.Register(SwiftTypeName.FromModuleQualifiedName("TestLib.ConcreteItem"), "TestLib", "ConcreteItem");
        // Non-frozen struct record → ClassWithOpaquePayload → indirect-result ISwiftObject, so the
        // substituted TypedBag<ConcreteItem> return is admitted by CanEmitConcreteOverloadForPairing.
        db.Register(SwiftTypeName.FromModuleQualifiedName("TestLib.TypedBag"), "TestLib", "TypedBag");

        var engine = new ConcreteSpecializationEngine(db);
        var moduleDecl = CreateModuleWithConformer("TestLib", "TestLib.ConcreteItem", "TestLib.Processable");
        engine.IndexModuleConformances(moduleDecl);

        var typeDecl = CreateGenericStructWithContainerProperty(
            "Bag", "items", "TestLib.Processable",
            new NamedTypeSpec("TestLib.TypedBag", new NamedTypeSpec("τ_0_0")));

        var csOutput = new StringWriter();
        var swiftOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(swiftOutput);

        ConcreteProtocolSpecializationEmitter.EmitConcreteSpecializationsForGenericParent(
            csWriter, swiftWriter, typeDecl, db, new ModuleEmissionContext(), engine, NullLogger.Instance);

        var cs = csOutput.ToString();
        var swift = swiftOutput.ToString();

        // Closed extension getter named Items on the closed parent receiver.
        Assert.Contains("Items(this Bag<", cs);
        Assert.Contains("ConcreteItem", cs);
        // The return is the SUBSTITUTED container — the parent param is closed, not leaked.
        Assert.Contains("TypedBag<", cs);
        Assert.DoesNotContain("Swift.AnyType", cs);
        // Swift wrapper READS the property (no call parens) — the AnyTypeFallback getter is a read.
        Assert.Contains("__self.items", swift);
        Assert.DoesNotContain("__self.items()", swift);
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
    public void EmitConcreteSpecializations_ParentOnlyAsyncMethod_TcsCarriesRunContinuationsAsynchronously()
    {
        // Finding 39: the parent-only async CSM specialization is the one async TCS site that
        // historically lacked TaskCreationOptions.RunContinuationsAsynchronously. Without it the
        // continuation runs inline on Swift's executor (reverse-deadlock setup). This guard drives
        // the async-parent emission and asserts the flag is present, matching every other async
        // TCS site (WrapperEmitter.Async, MethodHandler, AsyncMethodGenericBridge).
        // The parent-only async CSM surface is hint-driven (HasKnownHintConformers gate), so the
        // protocol must be one registered in specialization-hints.json. SwiftBindingsTestLib.AsyncBagItem
        // is the test-lib async hint protocol; its conformers (MockStringItem/MockIntItem) are global.
        var db = new ResolvingTypeDatabase { AsyncLibraryName = "SwiftBindings" };
        db.Register(SwiftTypeName.FromModuleQualifiedName("Swift.Int"), "System", "Int64");

        var engine = new ConcreteSpecializationEngine(db);

        // `func produce() async -> Int` on a generic parent constrained to the hint protocol —
        // a parent-only async method with a blittable primitive return (owns-buffer shape).
        var typeDecl = CreateGenericStructWithParentOnlyAsyncMethod(
            "Producer", "produce", "SwiftBindingsTestLib.AsyncBagItem", "Swift.Int");

        var csOutput = new StringWriter();
        var swiftOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(swiftOutput);

        // Parent-generic specs are routed away from EmitConcreteSpecializations (it skips any
        // spec with an IsParentGeneric param) to the dedicated parent-generic entry point, which
        // reaches TryEmitParentOnlyAsyncOverload — the site that allocates the parent-only TCS.
        ConcreteProtocolSpecializationEmitter.EmitConcreteSpecializationsForGenericParent(
            csWriter, swiftWriter, typeDecl, db, new ModuleEmissionContext(), engine, NullLogger.Instance);

        var cs = csOutput.ToString();
        // The parent-only async overload must have been emitted...
        Assert.Contains("global::System.Threading.Tasks.TaskCompletionSource<", cs);
        // ...and every TaskCompletionSource it allocates must carry the flag.
        foreach (var line in cs.Split('\n').Where(l => l.Contains("new global::System.Threading.Tasks.TaskCompletionSource<")))
        {
            Assert.Contains("global::System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously", line);
        }
    }

    [Fact]
    public void EmitConcreteSpecializations_ParentOnlyAsyncMethod_WiresCancellationToken()
    {
        // S13 Pillar E (CSM cancellation gap): the parent-only async CSM specialization historically
        // had NO cancellation — only RunContinuationsAsynchronously. It now mirrors the live method
        // emitters' registration blocks: a trailing CancellationToken parameter, a _sbwCancelKey from
        // the registry, a token-registration that calls SBW_CancelTask, and the SBW_CancelTask /
        // SBW_UnregisterTask P/Invokes hosted in the extension class. Red before the wiring landed.
        var db = new ResolvingTypeDatabase { AsyncLibraryName = "SwiftBindings" };
        db.Register(SwiftTypeName.FromModuleQualifiedName("Swift.Int"), "System", "Int64");

        var engine = new ConcreteSpecializationEngine(db);

        var typeDecl = CreateGenericStructWithParentOnlyAsyncMethod(
            "Producer", "produce", "SwiftBindingsTestLib.AsyncBagItem", "Swift.Int");

        var csOutput = new StringWriter();
        var swiftOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(swiftOutput);

        ConcreteProtocolSpecializationEmitter.EmitConcreteSpecializationsForGenericParent(
            csWriter, swiftWriter, typeDecl, db, new ModuleEmissionContext(), engine, NullLogger.Instance);

        var cs = csOutput.ToString();
        var swift = swiftOutput.ToString();

        // C#: trailing defaulted CancellationToken on the public method, registry key, token
        // registration that cancels the suspended producer, and both registry P/Invokes.
        Assert.Contains("global::System.Threading.CancellationToken cancellationToken = default", cs);
        Assert.Contains("SwiftAsyncCancellation.NextCancelKey()", cs);
        Assert.Contains("cancellationToken.Register(", cs);
        Assert.Contains("SBW_CancelTask(", cs);
        Assert.Contains("SBW_UnregisterTask(", cs);

        // Swift: the @_cdecl wrapper takes the cancelKey and registers/assigns the launched Task.
        Assert.Contains("_ cancelKey: Int64", swift);
        Assert.Contains("_sbwRegisterTask(cancelKey, _entry)", swift);
        Assert.Contains("if _sbwAssignTask(_entry, _sbwLaunchedTask) { _sbwLaunchedTask.cancel() }", swift);
    }

    [Fact]
    public void EmitConcreteSpecializations_ParentOnlyAsyncMethod_PreCancelledTokenShortCircuitsWithoutLaunch()
    {
        // Pre-cancel short-circuit: launching the Swift producer on an ALREADY-cancelled token
        // spins up the reverse-P/Invoke completion callback on a foreign Swift-concurrency executor
        // thread whose managed transition races the main-thread OperationCanceledException unwind —
        // the Mono arm64 JIT unwinder then walks a mis-tagged LMF and SIGSEGVs (the deterministic
        // sim crash this fix closes). The parent-only async CSM specialization was the sole async
        // emitter that did NOT short-circuit here; the live emitters (WrapperEmitter.Async,
        // AsyncMethodGenericBridgeEmitter, CrossModuleExtensionEmitter) all do. This asserts the
        // guard is emitted, returns Task.FromCanceled<Int64> for the value shape, and sits BEFORE
        // any TCS/GCHandle allocation so the no-launch path has nothing to clean up.
        var db = new ResolvingTypeDatabase { AsyncLibraryName = "SwiftBindings" };
        db.Register(SwiftTypeName.FromModuleQualifiedName("Swift.Int"), "System", "Int64");

        var engine = new ConcreteSpecializationEngine(db);

        var typeDecl = CreateGenericStructWithParentOnlyAsyncMethod(
            "Producer", "produce", "SwiftBindingsTestLib.AsyncBagItem", "Swift.Int");

        var csOutput = new StringWriter();
        var swiftOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(swiftOutput);

        ConcreteProtocolSpecializationEmitter.EmitConcreteSpecializationsForGenericParent(
            csWriter, swiftWriter, typeDecl, db, new ModuleEmissionContext(), engine, NullLogger.Instance);

        var cs = csOutput.ToString();

        // The pre-cancel guard is present and returns a GENERIC cancelled task whose type argument
        // is EXACTLY the same T the TaskCompletionSource<T> uses — a plain generic-open assertion
        // (`FromCanceled<`) would pass even if the emitter cancelled to the wrong type, so pin the
        // invariant that Task.FromCanceled<T>, Task<T>, and TaskCompletionSource<T> agree on T.
        Assert.Contains("if (cancellationToken.IsCancellationRequested)", cs);
        var tcsGeneric = System.Text.RegularExpressions.Regex.Match(
            cs, @"new global::System\.Threading\.Tasks\.TaskCompletionSource<(?<t>.+?)>\(");
        Assert.True(tcsGeneric.Success, "expected a generic TaskCompletionSource<T> allocation in the value-shape output");
        var t = tcsGeneric.Groups["t"].Value;
        Assert.Contains(
            $"return global::System.Threading.Tasks.Task.FromCanceled<{t}>(cancellationToken);", cs);

        // Ordering invariant: the short-circuit must precede the first TCS allocation, otherwise it
        // would have allocated (and would need to free) the resources the no-launch path skips.
        var guardIndex = cs.IndexOf("if (cancellationToken.IsCancellationRequested)", System.StringComparison.Ordinal);
        var tcsIndex = cs.IndexOf("new global::System.Threading.Tasks.TaskCompletionSource<", System.StringComparison.Ordinal);
        Assert.True(guardIndex >= 0 && tcsIndex >= 0, "expected both the pre-cancel guard and a TCS allocation in the output");
        Assert.True(guardIndex < tcsIndex, "pre-cancel guard must be emitted before the TaskCompletionSource allocation");
    }

    [Fact]
    public void EmitConcreteSpecializations_ParentOnlyAsyncVoidMethod_PreCancelledTokenShortCircuitsWithNonGenericFromCanceled()
    {
        // Void-return sibling of the pre-cancel short-circuit (the DonateAfterDelayAsync/void shape
        // where the deterministic crash was observed). A void parent-only async method must emit the
        // guard with a NON-generic Task.FromCanceled(...) — the value shape uses Task.FromCanceled<T>.
        var db = new ResolvingTypeDatabase { AsyncLibraryName = "SwiftBindings" };

        var engine = new ConcreteSpecializationEngine(db);

        var voidMethod = CreateParentOnlyAsyncVoidMethodDecl(
            "Donator", "donate", throws: false, withStringParam: false);
        var typeDecl = CreateGenericStructWithParentOnlyAsyncVoidMethods(
            "Donator", "SwiftBindingsTestLib.AsyncBagItem", voidMethod);

        var csOutput = new StringWriter();
        var swiftOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(swiftOutput);

        ConcreteProtocolSpecializationEmitter.EmitConcreteSpecializationsForGenericParent(
            csWriter, swiftWriter, typeDecl, db, new ModuleEmissionContext(), engine, NullLogger.Instance);

        var cs = csOutput.ToString();

        Assert.Contains("if (cancellationToken.IsCancellationRequested)", cs);
        Assert.Contains("return global::System.Threading.Tasks.Task.FromCanceled(cancellationToken);", cs);
        // Void must NOT emit the generic overload.
        Assert.DoesNotContain("Task.FromCanceled<", cs);
    }

    [Fact]
    public void EmitConcreteSpecializations_ParentOnlyAsyncVoidMethod_NonGenericTaskAndContextOnlyCompletion()
    {
        // Void-return parent-only async (`func donate() async` on a generic struct parent —
        // the ActivityKit `Activity<T>.update`/`end` and TipKit `Tips.Event<T>.donate` shape).
        // Before the void fork in IsEmittableParentOnlyAsyncPairing, an empty-tuple return was
        // hard-rejected and no wrapper emitted at all. After: the C# extension returns a
        // NON-generic Task (no Task<…>, no TaskCompletionSource<…>) and the Swift completion
        // callback carries ONLY the GCHandle context — no result pointer is allocated or passed.
        var db = new ResolvingTypeDatabase { AsyncLibraryName = "SwiftBindings" };

        var engine = new ConcreteSpecializationEngine(db);

        var voidMethod = CreateParentOnlyAsyncVoidMethodDecl(
            "Donator", "donate", throws: false, withStringParam: false);
        var typeDecl = CreateGenericStructWithParentOnlyAsyncVoidMethods(
            "Donator", "SwiftBindingsTestLib.AsyncBagItem", voidMethod);

        var csOutput = new StringWriter();
        var swiftOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(swiftOutput);

        ConcreteProtocolSpecializationEmitter.EmitConcreteSpecializationsForGenericParent(
            csWriter, swiftWriter, typeDecl, db, new ModuleEmissionContext(), engine, NullLogger.Instance);

        var cs = csOutput.ToString();
        var swift = swiftOutput.ToString();

        // The overload emitted at all.
        Assert.Contains("DonateAsync", cs);
        // Non-generic Task surface: a void method must NOT introduce a generic Task or TCS.
        Assert.DoesNotContain("TaskCompletionSource<", cs);
        Assert.DoesNotContain("global::System.Threading.Tasks.Task<", cs);
        // The public extension method returns the non-generic Task.
        Assert.Contains("global::System.Threading.Tasks.Task DonateAsync(", cs);
        // A plain (non-generic) TaskCompletionSource backs it.
        Assert.Contains("new global::System.Threading.Tasks.TaskCompletionSource(", cs);

        // "No result buffer" ABI, pinned positively and negatively:
        //   - no result buffer is allocated on the void path (value path NativeMemory.Allocs one),
        Assert.DoesNotContain("NativeMemory.Alloc", cs);
        //   - the holder's result slot is the IntPtr.Zero placeholder (stable 3-slot layout), and
        Assert.Contains("(object)(nint)IntPtr.Zero", cs);
        //   - the success callback / completion are the 1-arg (context-only) shape, never the
        //     2-arg (resultPtr, context) shape the value path uses.
        Assert.Contains("delegate* unmanaged[Cdecl]<IntPtr, void>", cs);
        Assert.DoesNotContain("delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>", cs);

        // Swift completion is context-only (1 arg), and is invoked with just the context.
        Assert.Contains("_ completion: @convention(c) (UnsafeMutableRawPointer) -> Void", swift);
        Assert.Contains("completion(context)", swift);
        // No result pointer is threaded into the wrapper, and no 2-arg success completion exists.
        Assert.DoesNotContain("_ resultPtr:", swift);
        Assert.Contains("await __self.donate()", swift);
    }

    [Fact]
    public void EmitConcreteSpecializations_ParentOnlyAsyncVoidThrowingMethod_InstallsErrorCallbackNoResultBuffer()
    {
        // Throwing void parent-only async (`func donate(_ name: String) async throws`). The
        // wrapper must install BOTH the 1-arg success completion AND the 2-arg errorCallback
        // inside a do/catch, route the thrown error through errorCallback(errorPtr, context),
        // and still allocate NO result buffer. The C# error path faults the non-generic Task
        // via TrySetException — there is no result slot to read. A caught CancellationError
        // must instead surface as a *cancelled* Task: the Swift catch reports it with a nil
        // error-pointer sentinel and the C# error callback maps that to TrySetCanceled,
        // without widening the two-argument error callback ABI.
        var db = new ResolvingTypeDatabase { AsyncLibraryName = "SwiftBindings" };

        var engine = new ConcreteSpecializationEngine(db);

        var voidThrowing = CreateParentOnlyAsyncVoidMethodDecl(
            "Donator", "donate", throws: true, withStringParam: true);
        var typeDecl = CreateGenericStructWithParentOnlyAsyncVoidMethods(
            "Donator", "SwiftBindingsTestLib.AsyncBagItem", voidThrowing);

        var csOutput = new StringWriter();
        var swiftOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(swiftOutput);

        ConcreteProtocolSpecializationEmitter.EmitConcreteSpecializationsForGenericParent(
            csWriter, swiftWriter, typeDecl, db, new ModuleEmissionContext(), engine, NullLogger.Instance);

        var cs = csOutput.ToString();
        var swift = swiftOutput.ToString();

        // Swift: both callbacks present, do/catch routing, context-only success completion.
        Assert.Contains("_ completion: @convention(c) (UnsafeMutableRawPointer) -> Void", swift);
        // The error pointer is optional so cancellation can travel as a nil sentinel.
        Assert.Contains("_ errorCallback: @convention(c) (UnsafeMutableRawPointer?, UnsafeMutableRawPointer) -> Void", swift);
        Assert.Contains("try await __self.donate(", swift);
        Assert.Contains("completion(context)", swift);
        // Cancellation reported as a nil sentinel; every other error boxes and flows normally.
        Assert.Contains("} catch is CancellationError {", swift);
        Assert.Contains("errorCallback(nil, context)", swift);
        Assert.Contains("errorCallback(errorPtr, context)", swift);
        // No result buffer on the throwing void path either.
        Assert.DoesNotContain("_ resultPtr:", swift);

        // C#: still a non-generic Task; the error path faults via TrySetException, but a
        // nil error-pointer sentinel is classified as cancellation and mapped to TrySetCanceled.
        Assert.DoesNotContain("TaskCompletionSource<", cs);
        Assert.Contains("global::System.Threading.Tasks.Task DonateAsync(", cs);
        Assert.Contains("TrySetException", cs);
        Assert.Contains("if (errorPtr == IntPtr.Zero)", cs);
        Assert.Contains("TrySetCanceled(", cs);
    }

    [Fact]
    public void EmitConcreteSpecializations_ParentOnlyAsyncVoidOverloads_BothAritiesEmit()
    {
        // sigKey param-dedup: two same-named void async overloads on one parent —
        // `donate() async` and `donate(_ name: String) async`. Both project to the C# name
        // DonateAsync and land in the SAME per-conformer extension class. If the dedup key
        // were name-only (`async|DonateAsync`), the second overload would be silently dropped.
        // Keying on the projected parameter list lets both arities coexist.
        var db = new ResolvingTypeDatabase { AsyncLibraryName = "SwiftBindings" };

        var engine = new ConcreteSpecializationEngine(db);

        var noParam = CreateParentOnlyAsyncVoidMethodDecl(
            "Donator", "donate", throws: false, withStringParam: false);
        var stringParam = CreateParentOnlyAsyncVoidMethodDecl(
            "Donator", "donate", throws: false, withStringParam: true);
        var typeDecl = CreateGenericStructWithParentOnlyAsyncVoidMethods(
            "Donator", "SwiftBindingsTestLib.AsyncBagItem", noParam, stringParam);

        var csOutput = new StringWriter();
        var swiftOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(swiftOutput);

        ConcreteProtocolSpecializationEmitter.EmitConcreteSpecializationsForGenericParent(
            csWriter, swiftWriter, typeDecl, db, new ModuleEmissionContext(), engine, NullLogger.Instance);

        var cs = csOutput.ToString();

        // The parameterless overload: first user param is the defaulted CancellationToken.
        Assert.Contains("DonateAsync(this Donator<", cs);
        Assert.Contains("self, global::System.Threading.CancellationToken cancellationToken = default)", cs);
        // The string overload: a `string` user param precedes the CancellationToken.
        Assert.Contains("self, string name, global::System.Threading.CancellationToken cancellationToken = default)", cs);
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
    public void InlineSwiftStructAllowlist_FoundationData_ProjectsPublicSurfaceToByteArray()
    {
        // A concrete CSM overload returning Foundation.Data must present `byte[]` on its public
        // surface (a drop-in for the generic SB0001 stub it shadows) while the wire is still
        // sized/marshaled on the ISwiftObject type. The projection metadata supplies the public
        // type ("byte[]") and the marshal-to-public conversion suffix (".ToByteArray()").
        var (publicType, suffix) = ConcreteProtocolSpecializationEmitter
            .GetInlineSwiftStructReturnProjectionForTesting("Foundation.Data");
        Assert.Equal("byte[]", publicType);
        Assert.Equal(".ToByteArray()", suffix);
    }

    [Fact]
    public void InlineSwiftStructAllowlist_FoundationUUID_HasNoDistinctPublicProjection()
    {
        // Foundation.UUID → System.Guid is its own idiomatic public surface; there is no
        // separate projection type or conversion suffix, so both are null. (UUID is also
        // ineligible for the indirect-return path per the eligibility test above.)
        var (publicType, suffix) = ConcreteProtocolSpecializationEmitter
            .GetInlineSwiftStructReturnProjectionForTesting("Foundation.UUID");
        Assert.Null(publicType);
        Assert.Null(suffix);
    }

    private static TypeRecord MakeStructRecord(string moduleQualifiedName, TypeRecordFlags flags,
        CSharpTypeName? nativeTypeName = null, TypeRecordKind kind = TypeRecordKind.Struct)
    {
        var dot = moduleQualifiedName.IndexOf('.');
        var ns = dot > 0 ? moduleQualifiedName.Substring(0, dot) : moduleQualifiedName;
        var name = dot > 0 ? moduleQualifiedName.Substring(dot + 1) : moduleQualifiedName;
        return new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName(ns, name),
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName(moduleQualifiedName),
            MetadataAccessor = string.Empty,
            Flags = flags,
            Kind = kind,
            NativeTypeName = nativeTypeName,
        };
    }

    [Fact]
    public void BlittableValueStructProjection_FrozenTrivialStruct_IsAdmitted()
    {
        // A frozen struct with no RequiresMemoryManagement (only trivial fields) projects to a
        // C# value `struct : ISwiftObject` — the FixedSignature / P256 ECDSASignature shape. It
        // is the case the CSM return gate previously rejected; it must now be admitted for both
        // the indirect-result return path and the non-generic pin-and-pass param path.
        var named = new NamedTypeSpec("TestModule.FixedSignature");
        var record = MakeStructRecord("TestModule.FixedSignature", TypeRecordFlags.Frozen);
        Assert.True(ConcreteProtocolSpecializationEmitter
            .ProjectsAsBlittableValueStructForTesting(named, record));
    }

    [Fact]
    public void BlittableValueStructProjection_FrozenWithMemory_IsRejected()
    {
        // Frozen + RequiresMemoryManagement (e.g. a frozen struct holding a String, the
        // SignatureBlob shape) projects to a C# class-with-buffer, NOT a value struct. The old
        // gate already admitted this via its class/memory arm, so the value-struct predicate
        // must NOT claim it (it has no inline value-struct projection to pin-and-pass).
        var named = new NamedTypeSpec("TestModule.SignatureBlob");
        var record = MakeStructRecord("TestModule.SignatureBlob",
            TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement);
        Assert.False(ConcreteProtocolSpecializationEmitter
            .ProjectsAsBlittableValueStructForTesting(named, record));
    }

    [Fact]
    public void BlittableValueStructProjection_NonFrozenStruct_IsRejected()
    {
        // Non-frozen structs project to a SafeHandle-backed C# class; not a pin-and-pass value.
        var named = new NamedTypeSpec("TestModule.OpaqueStruct");
        var record = MakeStructRecord("TestModule.OpaqueStruct", TypeRecordFlags.None);
        Assert.False(ConcreteProtocolSpecializationEmitter
            .ProjectsAsBlittableValueStructForTesting(named, record));
    }

    [Fact]
    public void BlittableValueStructProjection_NativeRemappedStruct_IsRejected()
    {
        // A frozen struct remapped to a .NET built-in (NativeTypeName set, e.g. Foundation.UUID
        // → System.Guid) is NOT an ISwiftObject, so GetSwiftTypeSize<T>/MarshalFromSwift<T> would
        // fail the T : ISwiftObject constraint. The predicate must reject it.
        var named = new NamedTypeSpec("Foundation.UUID");
        var record = MakeStructRecord("Foundation.UUID", TypeRecordFlags.Frozen,
            nativeTypeName: CSharpTypeName.FromNamespaceAndName("System", "Guid"));
        Assert.False(ConcreteProtocolSpecializationEmitter
            .ProjectsAsBlittableValueStructForTesting(named, record));
    }

    [Fact]
    public void BlittableValueStructProjection_KnownAppleValueType_IsRejected()
    {
        // A known Apple framework value type (e.g. a simd matrix, from the valueTypesOnly `simd`
        // module) has its own dedicated marshalling and may remap to a .NET primitive rather than
        // an ISwiftObject; the predicate excludes it even when frozen with no NativeTypeName on
        // the synthetic record.
        var named = new NamedTypeSpec("simd.simd_float4x4");
        var record = MakeStructRecord("simd.simd_float4x4", TypeRecordFlags.Frozen);
        Assert.False(ConcreteProtocolSpecializationEmitter
            .ProjectsAsBlittableValueStructForTesting(named, record));
    }

    [Fact]
    public void BlittableValueStructProjection_NonCopyableStruct_IsRejected()
    {
        // A ~Copyable frozen struct cannot be byte-copied across the boundary without violating
        // move-only semantics, so it must not take the pin-and-pass / byte-copy path.
        var named = new NamedTypeSpec("TestModule.MoveOnly");
        var record = MakeStructRecord("TestModule.MoveOnly",
            TypeRecordFlags.Frozen | TypeRecordFlags.NonCopyable);
        Assert.False(ConcreteProtocolSpecializationEmitter
            .ProjectsAsBlittableValueStructForTesting(named, record));
    }

    [Fact]
    public void BlittableValueStructProjection_TypeSkipPrePassSkippedStruct_IsRejected()
    {
        // A frozen, non-RMM struct can still be recorded skipped by the pre-emission pass — e.g. a
        // frozen value struct whose sub-word Optional<primitive> field shifts a following field's
        // byte offset (TypeSkipPrePass Condition 4), or whose Buffer layout is indeterminate
        // (Condition 3). Such a struct is never declared, so a CSM overload returning or taking it
        // by value would reference a C# type that is never generated (CS0246). The flag checks above
        // cannot see this — the struct is still frozen and non-RMM — so the predicate must consult
        // the authoritative skipped-type set, the same oracle the member gate uses.
        var named = new NamedTypeSpec("TestModule.SubWordOptionalSig");
        var record = MakeStructRecord("TestModule.SubWordOptionalSig", TypeRecordFlags.Frozen);

        var moduleDecl = BuildEmptyModule("TestModule");
        var structDecl = BuildFrozenStruct(moduleDecl, "SubWordOptionalSig", "TestModule.SubWordOptionalSig");

        ReportCollector.Start(moduleDecl);
        try
        {
            // Active session but NOT yet skipped: every flag condition is satisfied, so the predicate
            // admits it. This proves the session being active is not itself what flips the result.
            Assert.True(ConcreteProtocolSpecializationEmitter
                .ProjectsAsBlittableValueStructForTesting(named, record));

            ReportCollector.RecordTypeSkipped(structDecl, SkipReason.IndeterminateStructLayout,
                "frozen value struct sub-word optional layout mismatch");
            Assert.True(ReportCollector.IsTypeSkipped("TestModule.SubWordOptionalSig"));

            // Now recorded skipped → the predicate must reject it even though all flag checks pass.
            Assert.False(ConcreteProtocolSpecializationEmitter
                .ProjectsAsBlittableValueStructForTesting(named, record));
        }
        finally
        {
            ReportCollector.Reset();
        }
    }

    [Fact]
    public void ClassifyConformerStructurally_WithdrawnConformer_ReturnsWithdrawnType()
    {
        // The RealityFoundation regression: a conformer whose concrete type was withdrawn by the
        // ingestion-quarantine proven-closure walk (here recorded skipped) is never declared. A
        // CSM overload naming it as a type argument would reference a non-existent C# type
        // (CS0234). The structural gate must reject it up front with WithdrawnType. The narrow
        // blittable-value-struct arm alone misses class-/non-frozen-struct-projected conformers —
        // exactly how RealityFoundation's withdrawn Transform slipped through every other arm.
        var moduleDecl = BuildEmptyModule("TestModule");
        var withdrawn = BuildFrozenStruct(moduleDecl, "Withdrawn", "TestModule.Withdrawn");
        var conformer = new ConcreteSpecializationEngine.ConcreteConformer(
            SwiftQualifiedName: "TestModule.Withdrawn",
            CSharpType: "Withdrawn",
            SwiftType: SwiftTypeName.FromModuleQualifiedName("TestModule.Withdrawn"));

        ReportCollector.Start(moduleDecl);
        try
        {
            // Active session but NOT yet skipped: the gate does not return WithdrawnType, proving
            // an active ReportCollector session is not itself what flips the classification.
            Assert.NotEqual(
                ConcreteProtocolSpecializationEmitter.StructuralEmitReject.WithdrawnType,
                ConcreteProtocolSpecializationEmitter.ClassifyConformerStructurally(conformer, CreateEmptyTypeDatabase()));

            ReportCollector.RecordTypeSkipped(withdrawn, SkipReason.Unknown);

            Assert.Equal(
                ConcreteProtocolSpecializationEmitter.StructuralEmitReject.WithdrawnType,
                ConcreteProtocolSpecializationEmitter.ClassifyConformerStructurally(conformer, CreateEmptyTypeDatabase()));
        }
        finally
        {
            ReportCollector.Reset();
        }
    }

    [Fact]
    public void ClassifyConformerStructurally_WithdrawnGenericConformerNullSwiftType_ReturnsWithdrawnType()
    {
        // A generic conformer (e.g. Array<UInt8>) has a null SwiftType and carries its identity
        // only in SwiftQualifiedName. The withdrawal check's second OR-arm keys off that string,
        // so a conformer whose SwiftQualifiedName was recorded skipped is still caught even with a
        // null SwiftType. Guards the OR from silently degrading to only the SwiftType path.
        var moduleDecl = BuildEmptyModule("TestModule");
        var withdrawn = BuildFrozenStruct(moduleDecl, "Withdrawn", "TestModule.Withdrawn");
        var conformer = new ConcreteSpecializationEngine.ConcreteConformer(
            SwiftQualifiedName: "TestModule.Withdrawn",
            CSharpType: "Withdrawn",
            SwiftType: null);

        ReportCollector.Start(moduleDecl);
        try
        {
            ReportCollector.RecordTypeSkipped(withdrawn, SkipReason.Unknown);

            Assert.Equal(
                ConcreteProtocolSpecializationEmitter.StructuralEmitReject.WithdrawnType,
                ConcreteProtocolSpecializationEmitter.ClassifyConformerStructurally(conformer, CreateEmptyTypeDatabase()));
        }
        finally
        {
            ReportCollector.Reset();
        }
    }

    [Fact]
    public void ClassifyConformerStructurally_WithdrawnConformerUmbrellaSpelled_ReturnsWithdrawnType()
    {
        // Apple compileImportModule shape: RealityFoundation declares compileImportModule=RealityKit,
        // so a type withdrawn under its SOURCE-module declaration key (RealityFoundation.Widget) is
        // referenced by a conformer under the UMBRELLA spelling (RealityKit.Widget). The bare
        // IsTypeSkipped key would miss it and the CSM overload would name a non-existent C# type
        // (CS0234). The umbrella-remap-aware oracle re-attaches the source module and matches.
        var moduleDecl = BuildEmptyModule("RealityFoundation");
        var withdrawn = BuildFrozenStruct(moduleDecl, "Widget", "RealityFoundation.Widget");
        var conformer = new ConcreteSpecializationEngine.ConcreteConformer(
            SwiftQualifiedName: "RealityKit.Widget",
            CSharpType: "RealityKit.Widget",
            SwiftType: SwiftTypeName.FromModuleQualifiedName("RealityKit.Widget"));

        // Precondition: the umbrella reverse-map is loaded so the test fails loudly if the registry
        // isn't initialized, rather than passing for the wrong reason.
        Assert.Contains("RealityFoundation",
            AppleFrameworkRegistry.GetCompileImportSourceModules("RealityKit"));

        ReportCollector.Start(moduleDecl);
        try
        {
            ReportCollector.RecordTypeSkipped(withdrawn, SkipReason.Unknown);

            Assert.Equal(
                ConcreteProtocolSpecializationEmitter.StructuralEmitReject.WithdrawnType,
                ConcreteProtocolSpecializationEmitter.ClassifyConformerStructurally(conformer, CreateEmptyTypeDatabase()));
        }
        finally
        {
            ReportCollector.Reset();
        }
    }

    [Fact]
    public void ClassifyConformerStructurally_UnrelatedUmbrellaSkip_DoesNotReject()
    {
        // Negative control for the umbrella remap: a DIFFERENT source-module type is withdrawn
        // (RealityFoundation.Widget), but the conformer names RealityKit.Other. Re-attaching the
        // source module yields RealityFoundation.Other, which is NOT skipped, so the gate must not
        // over-fire and suppress a valid conformer just because some sibling umbrella type was
        // withdrawn.
        var moduleDecl = BuildEmptyModule("RealityFoundation");
        var withdrawn = BuildFrozenStruct(moduleDecl, "Widget", "RealityFoundation.Widget");
        var conformer = new ConcreteSpecializationEngine.ConcreteConformer(
            SwiftQualifiedName: "RealityKit.Other",
            CSharpType: "RealityKit.Other",
            SwiftType: SwiftTypeName.FromModuleQualifiedName("RealityKit.Other"));

        ReportCollector.Start(moduleDecl);
        try
        {
            ReportCollector.RecordTypeSkipped(withdrawn, SkipReason.Unknown);

            Assert.NotEqual(
                ConcreteProtocolSpecializationEmitter.StructuralEmitReject.WithdrawnType,
                ConcreteProtocolSpecializationEmitter.ClassifyConformerStructurally(conformer, CreateEmptyTypeDatabase()));
        }
        finally
        {
            ReportCollector.Reset();
        }
    }

    [Theory]
    [InlineData(TypeRecordFlags.ObjCRooted)]
    [InlineData(TypeRecordFlags.ObjCBridged)]
    [InlineData(TypeRecordFlags.ObjCBridgeable)]
    public void ClassifyConformerStructurally_HandleAccessorConformer_ReturnsObjCBridged(TypeRecordFlags flags)
    {
        // Every ObjC-backed projection flavor exposes the native pointer via `.Handle`, never an
        // ISwiftObject `.Payload` SafeHandle — while the CSM param/self arms render exclusively
        // through Payload (CS1061 if one were admitted). The gate must reject all three flavors
        // via the single UsesHandleAccessor oracle; the bridgeable flavor (Foundation.URL-style
        // value types) is the one an open-coded rooted|bridged union silently misses.
        var db = new ResolvingTypeDatabase();
        var typeName = SwiftTypeName.FromModuleQualifiedName("TestLib.ObjCThing");
        db.Register(typeName, "TestLib", "ObjCThing", kind: TypeRecordKind.Class, flags: flags);
        var conformer = new ConcreteSpecializationEngine.ConcreteConformer(
            SwiftQualifiedName: "TestLib.ObjCThing",
            CSharpType: "ObjCThing",
            SwiftType: typeName);

        Assert.Equal(
            ConcreteProtocolSpecializationEmitter.StructuralEmitReject.ObjCBridged,
            ConcreteProtocolSpecializationEmitter.ClassifyConformerStructurally(conformer, db));
    }

    [Fact]
    public void ClassifyConformerStructurally_PureSwiftClassConformer_NotRejected()
    {
        // Negative control: a pure-Swift class conformer (no ObjC flags, no native remap) must
        // keep flowing to the Class arm, whose public Payload SafeHandle rendering is valid for
        // every pure-Swift generated class.
        var db = new ResolvingTypeDatabase();
        var typeName = SwiftTypeName.FromModuleQualifiedName("TestLib.SwiftThing");
        db.Register(typeName, "TestLib", "SwiftThing", kind: TypeRecordKind.Class);
        var conformer = new ConcreteSpecializationEngine.ConcreteConformer(
            SwiftQualifiedName: "TestLib.SwiftThing",
            CSharpType: "SwiftThing",
            SwiftType: typeName);

        Assert.Equal(
            ConcreteProtocolSpecializationEmitter.StructuralEmitReject.None,
            ConcreteProtocolSpecializationEmitter.ClassifyConformerStructurally(conformer, db));
    }

    [Fact]
    public void ClassifyConformerStructurally_WithdrawnInnerGenericArg_ReturnsWithdrawnType()
    {
        // A real shipped hint conformer is `Swift.Array<MusicKit.Album>` (SwiftType == null because
        // generic names don't parse to a single SwiftTypeName). Its identity string names an INNER
        // type. When that inner type is withdrawn, the CSM overload still spells
        // `SwiftArray<...Withdrawn>` and references a non-existent C# type (CS0234). The withdrawal
        // check must recurse into the conformer's generic arguments — not only probe the whole
        // qualified name — to catch it.
        var moduleDecl = BuildEmptyModule("TestModule");
        var withdrawn = BuildFrozenStruct(moduleDecl, "Withdrawn", "TestModule.Withdrawn");
        var conformer = new ConcreteSpecializationEngine.ConcreteConformer(
            SwiftQualifiedName: "Swift.Array<TestModule.Withdrawn>",
            CSharpType: "Swift.SwiftArray<TestModule.Withdrawn>",
            SwiftType: null);

        ReportCollector.Start(moduleDecl);
        try
        {
            // The whole qualified name "Swift.Array<TestModule.Withdrawn>" is NOT itself a skip key —
            // only the inner "TestModule.Withdrawn" is — so this must catch the INNER arg.
            ReportCollector.RecordTypeSkipped(withdrawn, SkipReason.Unknown);

            Assert.Equal(
                ConcreteProtocolSpecializationEmitter.StructuralEmitReject.WithdrawnType,
                ConcreteProtocolSpecializationEmitter.ClassifyConformerStructurally(conformer, CreateEmptyTypeDatabase()));
        }
        finally
        {
            ReportCollector.Reset();
        }
    }

    [Fact]
    public void ConformerReferencesWithdrawnType_SharedOracle_UmbrellaAndGenericAndLive()
    {
        // Pins the shared oracle that the sync structural gate AND both async pairing gates
        // (IsEmittableAsyncPairing / IsEmittableParentOnlyAsyncPairing) consult, so the parallel
        // admission mechanisms cannot disagree on withdrawal. Covers: umbrella-spelled match,
        // withdrawn inner generic arg, and a live conformer (no over-fire).
        var moduleDecl = BuildEmptyModule("RealityFoundation");
        var withdrawn = BuildFrozenStruct(moduleDecl, "Widget", "RealityFoundation.Widget");

        var umbrella = new ConcreteSpecializationEngine.ConcreteConformer(
            "RealityKit.Widget", "RealityKit.Widget",
            SwiftType: SwiftTypeName.FromModuleQualifiedName("RealityKit.Widget"));
        var genericInner = new ConcreteSpecializationEngine.ConcreteConformer(
            "Swift.Array<RealityKit.Widget>", "Swift.SwiftArray<RealityKit.Widget>", SwiftType: null);
        var live = new ConcreteSpecializationEngine.ConcreteConformer(
            "RealityKit.Other", "RealityKit.Other",
            SwiftType: SwiftTypeName.FromModuleQualifiedName("RealityKit.Other"));

        ReportCollector.Start(moduleDecl);
        try
        {
            ReportCollector.RecordTypeSkipped(withdrawn, SkipReason.Unknown);

            Assert.True(ConcreteProtocolSpecializationEmitter.ConformerReferencesWithdrawnType(umbrella));
            Assert.True(ConcreteProtocolSpecializationEmitter.ConformerReferencesWithdrawnType(genericInner));
            Assert.False(ConcreteProtocolSpecializationEmitter.ConformerReferencesWithdrawnType(live));
        }
        finally
        {
            ReportCollector.Reset();
        }
    }

    [Fact]
    public void ConformerReferencesWithdrawnType_PlainConcreteStructConformer_FlipsOnWithdrawal()
    {
        // The KeyPath-family emitters (KeyPathSingletonEmitter, KeyPathBagValueSpecializationEmitter,
        // AppEntityKeyPathSingletonEmitter, ConformerKeyPathInitFactoryEmitter) draw conformers from
        // the SAME ConcreteSpecializationEngine.GetConformers source as the CSM gates and, before this
        // fix, named a withdrawn conformer as `global::Module.Type` with no C# declaration (CS0234),
        // failing the whole binding closed. They now gate each conformer on ConformerReferencesWithdrawnType
        // — the same shared oracle — exactly as the CSM gates do. This pins that the predicate they call
        // flips for the plain concrete-struct conformer shape a KeyPath Root actually is (end-to-end
        // emission of the eligible conformer is pinned by the MockBook AppEntity BindingTests).
        var moduleDecl = BuildEmptyModule("TestModule");
        var withdrawn = BuildFrozenStruct(moduleDecl, "MockBook", "TestModule.MockBook");
        var conformer = new ConcreteSpecializationEngine.ConcreteConformer(
            SwiftQualifiedName: "TestModule.MockBook",
            CSharpType: "MockBook",
            SwiftType: SwiftTypeName.FromModuleQualifiedName("TestModule.MockBook"));

        ReportCollector.Start(moduleDecl);
        try
        {
            // Live conformer: the KeyPath emitters keep it.
            Assert.False(ConcreteProtocolSpecializationEmitter.ConformerReferencesWithdrawnType(conformer));

            ReportCollector.RecordTypeSkipped(withdrawn, SkipReason.Unknown);

            // Withdrawn conformer: the KeyPath emitters must drop it.
            Assert.True(ConcreteProtocolSpecializationEmitter.ConformerReferencesWithdrawnType(conformer));
        }
        finally
        {
            ReportCollector.Reset();
        }
    }

    private static ModuleDecl BuildEmptyModule(string name) => new ModuleDecl
    {
        Name = name,
        ParentDecl = null,
        ModuleDecl = null,
        Properties = new List<PropertyDecl>(),
        Methods = new List<MethodDecl>(),
        Types = new List<TypeDecl>(),
        Dependencies = new List<string>(),
        Protocols = new List<ProtocolDecl>(),
        AvailabilityAnnotations = null,
    };

    private static StructDecl BuildFrozenStruct(ModuleDecl moduleDecl, string name, string moduleQualifiedName)
    {
        var structDecl = new StructDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName(moduleQualifiedName),
            MangledName = "",
            IsFrozen = true,
            GenericParameters = new List<GenericArgumentDecl>(),
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            Conformances = new List<TypeConformance>(),
            MetadataAccessor = "",
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            AvailabilityAnnotations = null,
        };
        moduleDecl.Types.Add(structDecl);
        return structDecl;
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
    public void ParentTupleSatisfiesMethodConstraints_SameType_SugaredConcreteTarget_Admits()
    {
        // Direct-arm SameType match: `<τ_0_0 where τ_0_0 == Swift.Optional<Swift.Int>>`.
        // The chosen parent conformer names the SAME type in sugared form — its
        // SwiftQualifiedName is "Swift.Int?" while the generic-sig target prints the
        // desugared "Swift.Optional<Swift.Int>". These are the same Swift type, so the
        // clause IS satisfied and the tuple must admit. An ordinal string compare on the
        // direct arm false-rejects the sugar mismatch and silently skips a legal member
        // (undercount); canonicalizing both sides through the type-spec parser admits it.
        var engine = new ConcreteSpecializationEngine(CreateEmptyTypeDatabase());
        var method = CreateMethodWithSig(
            "pinOptional",
            "<τ_0_0 where τ_0_0 == Swift.Optional<Swift.Int>>");
        var parent = CreateGenericStructDecl("Box", "τ_0_0");

        var conformer = new ConcreteSpecializationEngine.ConcreteConformer(
            SwiftQualifiedName: "Swift.Int?",
            CSharpType: "SwiftOptional<nint>");
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
            "a sugared/desugared spelling of the same SameType concrete target must be admitted, not false-rejected");
    }

    [Fact]
    public void ParentTupleSatisfiesMethodConstraints_Conformance_CompositeTarget_DeclinesWithoutThrowing()
    {
        // A method-level DIRECT conformance constraint whose target is an unqualified
        // protocol-composition (`τ_0_0 : A & B`). ParseSignature keeps such targets
        // verbatim (it is the lossless reader), so `target` reaches the conformance-arm's
        // SwiftTypeName.FromModuleQualifiedName(target) call — which throws on an
        // unqualified/`&`-containing string (no module dot). Building the type name must
        // not crash the generator: an unparseable constraint target is unprovable, so the
        // tuple fails-closed (declines) rather than throwing.
        var engine = new ConcreteSpecializationEngine(CreateEmptyTypeDatabase());
        var method = CreateMethodWithSig("compose", "<τ_0_0 where τ_0_0 : A & B>");
        var parent = CreateGenericStructDecl("Box", "τ_0_0");

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
            "an unparseable composite/unqualified conformance target must fail-closed (decline), not throw");
    }

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

    [Fact]
    public void ParentTupleSatisfiesMethodConstraints_SameType_GenericRhs_NotTruncated_AdmitsMatch()
    {
        // Regression: the whole generic signature is wrapped in one pair of angle brackets,
        // and when the last SameType RHS is itself a generic type the sig ends in `>>` —
        // `<τ_0_0 where τ_0_0 == Foundation.Measurement<Foundation.UnitDuration>>`. A greedy
        // TrimEnd('>') ate BOTH closers, truncating the target to
        // `Foundation.Measurement<Foundation.UnitDuration`. That made an exactly-matching
        // conformer compare unequal (false reject, silent under-emission) AND, on the
        // dependent-member path, threw out of TypeSpecParser.Parse. The parser must keep the
        // target's own closing `>`, so this exact-match conformer is admitted.
        var engine = new ConcreteSpecializationEngine(CreateEmptyTypeDatabase());
        var method = CreateMethodWithSig(
            "expectMeasurement",
            "<τ_0_0 where τ_0_0 == Foundation.Measurement<Foundation.UnitDuration>>");
        var parent = CreateGenericStructDecl("Holder", "τ_0_0");

        var conformer = new ConcreteSpecializationEngine.ConcreteConformer(
            SwiftQualifiedName: "Foundation.Measurement<Foundation.UnitDuration>",
            CSharpType: "Measurement");
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
            "generic SameType RHS must retain its closing '>' — exact-match conformer must be admitted, not truncated away");
    }

    [Fact]
    public void ParentTupleSatisfiesMethodConstraints_SameType_GenericRhs_StillRejectsMismatch()
    {
        // Negative control for the truncation fix: keeping the full (untruncated) target
        // must still reject a conformer that differs only by the generic argument — proving
        // the compare is against the whole `Measurement<UnitDuration>`, not a prefix.
        var engine = new ConcreteSpecializationEngine(CreateEmptyTypeDatabase());
        var method = CreateMethodWithSig(
            "expectMeasurement",
            "<τ_0_0 where τ_0_0 == Foundation.Measurement<Foundation.UnitDuration>>");
        var parent = CreateGenericStructDecl("Holder", "τ_0_0");

        var conformer = new ConcreteSpecializationEngine.ConcreteConformer(
            SwiftQualifiedName: "Foundation.Measurement<Foundation.UnitLength>",
            CSharpType: "Measurement");
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
            "generic SameType RHS mismatch (UnitDuration vs UnitLength) must still be rejected");
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
            IsSynthesizedAccessor = false,
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

    // ============ KeyPath generic-arg resolvability gate ============
    // A CSM KeyPath-family parameter renders its inner generic arguments into the public C#
    // signature via BuildKeyPathPublicCSharpType → ResolvePublicCSharpType, which falls back to
    // an UNqualified bare name for any type with no TypeRecord (e.g. CoreSpotlight's ObjC-rooted
    // CSSearchableItemAttributeSet in an AppIntents-only generation), producing
    // PartialKeyPath<CSSearchableItemAttributeSet> → CS0246. IsKeyPathGenericArgResolvable gates
    // the specialization so the predicate and the renderer agree.

    [Fact]
    public void IsKeyPathGenericArgResolvable_SwiftPrimitive_NeedsNoTypeRecord()
    {
        // Empty DB: no records at all, yet a Swift primitive still renders to a C# keyword.
        Assert.True(ConcreteProtocolSpecializationEmitter.IsKeyPathGenericArgResolvable(
            new NamedTypeSpec("Swift.String"), new EmptyTypeDatabase()));
        Assert.True(ConcreteProtocolSpecializationEmitter.IsKeyPathGenericArgResolvable(
            new NamedTypeSpec("Swift.Int"), new EmptyTypeDatabase()));
    }

    [Fact]
    public void IsKeyPathGenericArgResolvable_TypeWithRecord_IsResolvable()
    {
        var db = new ResolvingTypeDatabase();
        db.Register(SwiftTypeName.FromModuleQualifiedName("AppIntents.IntentFile"),
            "AppIntents", "IntentFile");

        Assert.True(ConcreteProtocolSpecializationEmitter.IsKeyPathGenericArgResolvable(
            new NamedTypeSpec("AppIntents.IntentFile"), db));
    }

    [Fact]
    public void IsKeyPathGenericArgResolvable_ForeignTypeWithoutRecord_IsRejected()
    {
        // CoreSpotlight has no TypeRecord (declKind=Import, ObjC-rooted) → would render
        // unqualified → reject. This is the AppIntents indexingKey: regression.
        Assert.False(ConcreteProtocolSpecializationEmitter.IsKeyPathGenericArgResolvable(
            new NamedTypeSpec("CoreSpotlight.CSSearchableItemAttributeSet"), new ResolvingTypeDatabase()));
    }

    [Fact]
    public void IsKeyPathGenericArgResolvable_NestedGenericWithUnresolvableInner_IsRejected()
    {
        // Resolvable base, unresolvable inner arg: ResolvePublicCSharpType recurses into the
        // inner type, so the gate must too — an inner unqualified name still breaks the build.
        var db = new ResolvingTypeDatabase();
        db.Register(SwiftTypeName.FromModuleQualifiedName("Swift.Array"), "Swift", "SwiftArray");

        var nested = new NamedTypeSpec("Swift.Array",
            new NamedTypeSpec("CoreSpotlight.CSSearchableItemAttributeSet"));

        Assert.False(ConcreteProtocolSpecializationEmitter.IsKeyPathGenericArgResolvable(nested, db));
    }

    [Fact]
    public void IsKeyPathGenericArgResolvable_NonNamedArg_RendersToIntPtr_IsResolvable()
    {
        // Non-named args render to "IntPtr" (resolvable); the gate must not over-reject them.
        Assert.True(ConcreteProtocolSpecializationEmitter.IsKeyPathGenericArgResolvable(
            new TupleTypeSpec(), new EmptyTypeDatabase()));
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

        // Settable so a test can flip IsXCFrameworkMode on (it keys off a non-empty
        // AsyncLibraryName) and drive the wrapper-emitting CSM path, not just discovery.
        public string? AsyncLibraryName { get; set; }
        public bool IsTypeProcessed(SwiftTypeName swiftTypeName) => _records.ContainsKey(swiftTypeName.ToString());
        public bool TryGetTypeRecord(SwiftTypeName swiftTypeName, [NotNullWhen(returnValue: true)] out TypeRecord? record)
            => _records.TryGetValue(swiftTypeName.ToString(), out record);
        public string GetLibraryPath(string moduleName) => "";
        public void UpdateTypeRecord(SwiftTypeName name, TypeRecord record) { }

        public void Register(SwiftTypeName swiftTypeName, string csNamespace, string csName,
            TypeRecordKind kind = TypeRecordKind.Struct,
            SwiftTypeName? superclass = null,
            IReadOnlyList<SwiftTypeName>? protocolConformances = null,
            CSharpTypeName? nativeTypeName = null,
            TypeRecordFlags flags = TypeRecordFlags.None)
        {
            _records[swiftTypeName.ToString()] = new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName(csNamespace, csName),
                SwiftTypeName = swiftTypeName,
                MetadataAccessor = "",
                Flags = flags,
                Kind = kind,
                SuperclassTypeName = superclass,
                ProtocolConformances = protocolConformances,
                // A non-null NativeTypeName makes MethodClosureBridge.ClassifyParam return the
                // NativeRemapped category (Foundation.Data ↔ NSData), matching production.
                NativeTypeName = nativeTypeName,
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
            IsSynthesizedAccessor = false,
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
            IsSynthesizedAccessor = false,
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

    /// <summary>
    /// Builds a struct with a static method whose single method-own generic parameter carries
    /// TWO protocol constraints (`func m&lt;T : A &amp; B&gt;(_ item: T) -&gt; String`). Used to
    /// exercise the multi-constraint intersection in ConformerSatisfiesAllConstraints.
    /// </summary>
    private static StructDecl CreateStructWithMultiConstraintMethod(
        string typeName, string methodName, string protocolNameA, string protocolNameB)
    {
        var protoA = SwiftTypeName.FromModuleQualifiedName(protocolNameA);
        var protoB = SwiftTypeName.FromModuleQualifiedName(protocolNameB);
        var confA = new GenericParameterConformance(new[] { "τ_1_0" }, protoA, ConformanceKind.Protocol);
        var confB = new GenericParameterConformance(new[] { "τ_1_0" }, protoB, ConformanceKind.Protocol);

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
            IsSynthesizedAccessor = false,
            GenericParameters = new List<GenericArgumentDecl>
            {
                new("τ_1_0", "T", new List<GenericParameterConformance> { confA, confB }, new())
            },
            CSSignature = new List<ArgumentDecl>
            {
                new() { Name = "", PrivateName = "", IsInOut = false, ParentDecl = null, ModuleDecl = null, SwiftTypeSpec = new NamedTypeSpec("Swift.String"), IsGeneric = false },
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

    /// <summary>
    /// Builds a module with a single type that declares TWO protocol conformances. Used to
    /// exercise the ABI-Confirmed arm of the multi-constraint check: a conformer indexed as
    /// declaring both protocols satisfies a non-selected constraint via the ABI merge.
    /// </summary>
    private static ModuleDecl CreateModuleWithTwoConformances(
        string moduleName, string conformerType, string protocolA, string protocolB)
    {
        var conformerTypeName = SwiftTypeName.FromModuleQualifiedName(conformerType);
        var protoA = SwiftTypeName.FromModuleQualifiedName(protocolA);
        var protoB = SwiftTypeName.FromModuleQualifiedName(protocolB);

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
                new(conformerTypeName, protoA, ""),
                new(conformerTypeName, protoB, "")
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

    private static StructDecl CreateStructWithProtocolConstrainedConstructor(
        string typeName, string protocolName, bool throws = false, bool withConcreteDataParam = false)
    {
        var protocolTypeName = SwiftTypeName.FromModuleQualifiedName(protocolName);
        var conformance = new GenericParameterConformance(
            new[] { "τ_0_0" }, protocolTypeName, ConformanceKind.Protocol);

        var paramTypeSpec = new NamedTypeSpec("τ_0_0");

        var csSignature = new List<ArgumentDecl>
        {
            // Return type (first element) — constructor returns Self (Box here)
            new() { Name = "", PrivateName = "", IsInOut = false, ParentDecl = null, ModuleDecl = null, SwiftTypeSpec = new NamedTypeSpec($"TestLib.{typeName}"), IsGeneric = false },
            // Parameter of generic type
            new() { Name = "source", PrivateName = "source", IsInOut = false, ParentDecl = null, ModuleDecl = null, SwiftTypeSpec = paramTypeSpec, IsGeneric = true }
        };
        if (withConcreteDataParam)
        {
            // A concrete (non-generic) Foundation.Data param alongside the generic one — the HPKE
            // `init(recipientKey:ciphersuite:info:)` shape that drove the NativeRemapped fix.
            csSignature.Add(new() { Name = "info", PrivateName = "info", IsInOut = false, ParentDecl = null, ModuleDecl = null, SwiftTypeSpec = new NamedTypeSpec("Foundation.Data"), IsGeneric = false });
        }

        var ctor = new MethodDecl
        {
            Name = "init",
            ParentDecl = null,
            ModuleDecl = null,
            MangledName = $"$s{typeName}init",
            MethodType = MethodType.Static,
            IsConstructor = true,
            Throws = throws,
            IsAsync = false,
            IsSynthesizedAccessor = false,
            GenericParameters = new List<GenericArgumentDecl>
            {
                new("τ_0_0", "T", new List<GenericParameterConformance> { conformance }, new())
            },
            CSSignature = csSignature,
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
            IsSynthesizedAccessor = false,
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

    // Builds a generic parent whose parent-only member is a read-only protocol-extension
    // PROPERTY default surfaced as a synthetic zero-parameter Bool getter method
    // (IsExtensionPropertyGetter=true) — the RealityKit `FromToByAction<T>.isReversible`
    // shape. The CSM wrapper must READ this member (`__self.isReversible`), not CALL it
    // (`__self.isReversible()`), or swiftc rejects the wrapper with "cannot call value of
    // non-function type 'Bool'".
    private static StructDecl CreateGenericStructWithParentOnlyPropertyGetter(
        string typeName, string getterName, string protocolName)
    {
        var protocolTypeName = SwiftTypeName.FromModuleQualifiedName(protocolName);

        var parentConformance = new GenericParameterConformance(
            new[] { "τ_0_0" }, protocolTypeName, ConformanceKind.Protocol);

        var parentGenericParam = new GenericArgumentDecl(
            "τ_0_0", "T",
            new List<GenericParameterConformance> { parentConformance },
            new List<GenericParameterConformance>());

        // Zero-parameter Bool getter: CSSignature carries only the return slot (Swift.Bool).
        var method = new MethodDecl
        {
            Name = getterName,
            ParentDecl = null,
            ModuleDecl = null,
            MangledName = $"$s{typeName}{getterName}",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false,
            IsProtocolExtensionMethod = true,
            IsExtensionPropertyGetter = true,
            UsesWrapperLibrary = true,
            UsesFreeFunctionWrapper = true,
            GenericParameters = new List<GenericArgumentDecl>(),
            CSSignature = new List<ArgumentDecl>
            {
                new() { Name = "", PrivateName = "", IsInOut = false, ParentDecl = null, ModuleDecl = null, SwiftTypeSpec = new NamedTypeSpec("Swift.Bool"), IsGeneric = false }
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

    // Builds `struct {typeName}<τ_0_0 : {protocolName}> { var {propertyName}: {returnSpec} { get } }`
    // as a PropertyDecl carrying a single GetAccessorDecl, the shape FindSpecializableProperties
    // reads (property getters live under PropertyDecl.Accessors, never in typeDecl.Methods). The
    // getter's CSSignature is return-only (index 0 = returnSpec, no user params).
    private static StructDecl CreateGenericStructWithContainerProperty(
        string typeName, string propertyName, string protocolName, TypeSpec returnSpec,
        bool isStatic = false, bool isAsync = false, bool throws = false, bool isMutating = false)
    {
        var protocolTypeName = SwiftTypeName.FromModuleQualifiedName(protocolName);

        var parentConformance = new GenericParameterConformance(
            new[] { "τ_0_0" }, protocolTypeName, ConformanceKind.Protocol);

        var parentGenericParam = new GenericArgumentDecl(
            "τ_0_0", "T",
            new List<GenericParameterConformance> { parentConformance },
            new List<GenericParameterConformance>());

        var getterMethod = new MethodDecl
        {
            Name = $"{propertyName}_Get",
            ParentDecl = null,
            ModuleDecl = null,
            MangledName = $"$s{typeName}{propertyName}g",
            MethodType = isStatic ? MethodType.Static : MethodType.Instance,
            IsConstructor = false,
            Throws = throws,
            IsAsync = isAsync,
            IsMutating = isMutating,
            IsSynthesizedAccessor = true,
            UsesWrapperLibrary = true,
            GenericParameters = new List<GenericArgumentDecl>(),
            CSSignature = new List<ArgumentDecl>
            {
                new() { Name = "", PrivateName = "", IsInOut = false, ParentDecl = null, ModuleDecl = null, SwiftTypeSpec = returnSpec, IsGeneric = false }
            },
            AvailabilityAnnotations = null
        };

        var property = new PropertyDecl
        {
            Name = propertyName,
            ParentDecl = null,
            ModuleDecl = null,
            SwiftTypeSpec = returnSpec,
            HasStorage = false,
            IsStatic = isStatic,
            Accessors = new List<AccessorDecl> { new GetAccessorDecl { Method = getterMethod } }
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
            Properties = new List<PropertyDecl> { property },
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Conformances = new List<TypeConformance>(),
            MetadataAccessor = "",
            AvailabilityAnnotations = null
        };

        getterMethod.ParentDecl = structDecl;
        return structDecl;
    }

    private static StructDecl CreateGenericStructWithParentOnlyAsyncMethod(
        string typeName, string methodName, string protocolName, string returnType)
    {
        var protocolTypeName = SwiftTypeName.FromModuleQualifiedName(protocolName);

        var parentConformance = new GenericParameterConformance(
            new[] { "τ_0_0" }, protocolTypeName, ConformanceKind.Protocol);

        var parentGenericParam = new GenericArgumentDecl(
            "τ_0_0", "T",
            new List<GenericParameterConformance> { parentConformance },
            new List<GenericParameterConformance>());

        // Parent-only async method with no own generics and a non-void primitive return
        // (`func produce() async -> Int`). Drives EmitConcreteSpecializationsForGenericParent's
        // async branch (TryEmitParentOnlyAsyncOverload), which allocates the parent-only TCS.
        var method = new MethodDecl
        {
            Name = methodName,
            ParentDecl = null,
            ModuleDecl = null,
            MangledName = $"$s{typeName}{methodName}",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = false,
            IsAsync = true,
            IsSynthesizedAccessor = false,
            GenericParameters = new List<GenericArgumentDecl>(),
            CSSignature = new List<ArgumentDecl>
            {
                new() { Name = "", PrivateName = "", IsInOut = false, ParentDecl = null, ModuleDecl = null, SwiftTypeSpec = new NamedTypeSpec(returnType), IsGeneric = false }
            },
            AvailabilityAnnotations = null
        };

        var structDecl = new StructDecl
        {
            Name = typeName,
            ParentDecl = null,
            ModuleDecl = null,
            // SwiftBindingsTestLib so the module matches the hint protocol's registry scope.
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"SwiftBindingsTestLib.{typeName}"),
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

    // Builds a parent-only async VOID method decl: CSSignature[0] is the empty-tuple return
    // (`isVoid`), optionally followed by one Swift.String user param (admitted Utf8Slice) and
    // optionally throwing. The empty-tuple return is what IsEmittableParentOnlyAsyncPairing's
    // void fork keys on. A per-arity suffix on the mangled name keeps two same-named overloads'
    // cdecl symbols distinct.
    private static MethodDecl CreateParentOnlyAsyncVoidMethodDecl(
        string typeName, string methodName, bool throws, bool withStringParam)
    {
        var sig = new List<ArgumentDecl>
        {
            // index 0 = return type; empty tuple → void
            new() { Name = "", PrivateName = "", IsInOut = false, ParentDecl = null, ModuleDecl = null, SwiftTypeSpec = TupleTypeSpec.Empty, IsGeneric = false }
        };
        if (withStringParam)
        {
            sig.Add(new() { Name = "name", PrivateName = "name", IsInOut = false, ParentDecl = null, ModuleDecl = null, SwiftTypeSpec = new NamedTypeSpec("Swift.String"), IsGeneric = false });
        }

        return new MethodDecl
        {
            Name = methodName,
            ParentDecl = null,
            ModuleDecl = null,
            MangledName = $"$s{typeName}{methodName}{(withStringParam ? "1p" : "0p")}",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = throws,
            IsAsync = true,
            IsSynthesizedAccessor = false,
            GenericParameters = new List<GenericArgumentDecl>(),
            CSSignature = sig,
            AvailabilityAnnotations = null
        };
    }

    // Builds a frozen generic struct whose single PAT generic is hint-resolved, carrying the
    // supplied parent-only async void methods. Module is SwiftBindingsTestLib so the hint
    // protocol's registry scope matches (mirrors CreateGenericStructWithParentOnlyAsyncMethod).
    private static StructDecl CreateGenericStructWithParentOnlyAsyncVoidMethods(
        string typeName, string protocolName, params MethodDecl[] methods)
    {
        var protocolTypeName = SwiftTypeName.FromModuleQualifiedName(protocolName);

        var parentConformance = new GenericParameterConformance(
            new[] { "τ_0_0" }, protocolTypeName, ConformanceKind.Protocol);

        var parentGenericParam = new GenericArgumentDecl(
            "τ_0_0", "T",
            new List<GenericParameterConformance> { parentConformance },
            new List<GenericParameterConformance>());

        var structDecl = new StructDecl
        {
            Name = typeName,
            ParentDecl = null,
            ModuleDecl = null,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"SwiftBindingsTestLib.{typeName}"),
            MangledName = "",
            IsFrozen = true,
            GenericParameters = new List<GenericArgumentDecl> { parentGenericParam },
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(methods),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Conformances = new List<TypeConformance>(),
            MetadataAccessor = "",
            AvailabilityAnnotations = null
        };

        foreach (var m in methods)
        {
            m.ParentDecl = structDecl;
        }
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
            IsSynthesizedAccessor = false,
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

    // ==================== ResolveParentCSharpTypeRef ====================

    [Fact]
    public void ResolveParentCSharpTypeRef_NestedRenamedParent_ReturnsLiveCSharpName()
    {
        // A nested type whose C# projection was renamed by the nested-type-collision pre-pass
        // (a sibling property projecting to the same name) is only reachable under its post-rename
        // name. The parent's raw Swift declaration name is stale by then, so a specialized
        // constructor factory emitted INSIDE that renamed class must declare the live name as its
        // return type — otherwise it names a type that either does not exist or is a different
        // type that happens to be visible in scope, and the mismatch compiles silently.
        var db = new ResolvingTypeDatabase();
        var parentSwiftName = SwiftTypeName.FromModuleQualifiedName("TestLib.DerivedVault.Token");
        db.Register(parentSwiftName, "TestLib", "DerivedVault.TokenInfo");
        var parentDecl = CreateBareClass("Token", parentSwiftName);

        var resolved = ConcreteProtocolSpecializationEmitter.ResolveParentCSharpTypeRef(parentDecl, db);

        Assert.Equal("TestLib.DerivedVault.TokenInfo", resolved);
    }

    [Fact]
    public void ResolveParentCSharpTypeRef_FlatParent_KeepsBareDeclarationName()
    {
        // A top-level parent is emitted inside its own class body, where the bare leaf name is
        // both correct and unambiguous, and it is never renamed by the nested-type pre-pass. It
        // must keep the bare name so the emitted output for the overwhelmingly common case is
        // unchanged.
        var db = new ResolvingTypeDatabase();
        var parentSwiftName = SwiftTypeName.FromModuleQualifiedName("TestLib.SealedKey");
        db.Register(parentSwiftName, "TestLib", "SealedKey");
        var parentDecl = CreateBareClass("SealedKey", parentSwiftName);

        var resolved = ConcreteProtocolSpecializationEmitter.ResolveParentCSharpTypeRef(parentDecl, db);

        Assert.Equal("SealedKey", resolved);
    }

    [Fact]
    public void ResolveParentCSharpTypeRef_NestedParentWithoutRecord_FallsBackToDeclarationName()
    {
        // No type record means no live name to read; falling back to the declaration name keeps
        // the resolver total rather than throwing during emission.
        var db = new ResolvingTypeDatabase();
        var parentDecl = CreateBareClass(
            "Token", SwiftTypeName.FromModuleQualifiedName("TestLib.DerivedVault.Token"));

        var resolved = ConcreteProtocolSpecializationEmitter.ResolveParentCSharpTypeRef(parentDecl, db);

        Assert.Equal("Token", resolved);
    }

    private static ClassDecl CreateBareClass(string name, SwiftTypeName swiftTypeName)
        => new ClassDecl
        {
            Name = name,
            ParentDecl = null,
            ModuleDecl = null,
            SwiftTypeName = swiftTypeName,
            MangledName = "",
            GenericParameters = new List<GenericArgumentDecl>(),
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Conformances = new List<TypeConformance>(),
            AvailabilityAnnotations = null,
            IsFinal = true,
        };

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
        // The bilateral filter previously skipped Protocol-kind
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
        // when `Dog : Animal`. Exact-name match alone would falsely
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
        // semantics of the original bilateral-filter fix.
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
    // identifiers (e.g. SHA3_256, which digit-boundary casing now preserves verbatim as SHA3_256).
    // Non-identifier C# type expressions such as Byte[], Foundation.Data, Swift.Array<Byte> must
    // pass through verbatim; a previous, too-eager canonicalisation mangled them to invalid
    // identifiers like Byte__.

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
    [InlineData("SHA3_256", "SHA3_256")]  // SHA3 and 256 both carry a digit → verbatim + underscore kept
    [InlineData("SHA256", "SHA256")]      // single digit-bearing designator → verbatim (was Sha256)
    [InlineData("SHA_384", "Sha384")]     // SHA (pure-letter word) title-cases; only 384 is a designator
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

    [Theory]
    // Readable factory-token map for the closed-specialization `From…` constructor name.
    // The old SanitizeTypeName leaked `byteArr_`, `TestLib_Outer_Inner`, etc. into public names.
    [InlineData("byte[]", "ByteArray")]                                   // array marker spelled out (was byteArr_)
    [InlineData("TestLib.Outer.Inner", "TestLibOuterInner")]              // dotted namespace fragments concatenated
    [InlineData("SHA256", "SHA256")]                                      // digit-bearing designator preserved verbatim
    [InlineData("Foundation.Data", "FoundationData")]                     // two-fragment dotted name
    [InlineData("HashedAuthenticationCode<SHA256>", "HashedAuthenticationCodeSHA256")] // generic args folded in
    [InlineData("Swift.Array<Byte>", "SwiftArrayByte")]                   // generic + namespace
    [InlineData("byte[][]", "ByteArrayArray")]                            // jagged array
    [InlineData("Byte", "Byte")]                                          // single fragment, capitalised
    [InlineData("", "")]                                                  // empty → empty
    public void BuildReadableFactoryTypeToken_ProducesReadablePascalToken(string input, string expected)
    {
        Assert.Equal(expected, ConcreteProtocolSpecializationEmitter.BuildReadableFactoryTypeToken(input));
    }

    [Theory]
    // Collision-safety: two structurally different C# type spellings can fold to the SAME readable
    // token. This is intentional and safe — the factory name flows through BuildCSharpSignatureKey
    // (name + emitted param types = C#'s own overload identity), so a token collision either yields
    // a valid overload (params differ) or a genuine duplicate that the emittedSignatures dedup
    // collapses; it can NEVER emit CS0111. This pins the fold so a future "make it injective" change
    // is a deliberate decision, not an accident.
    [InlineData("Foo.Bar", "FooBar")]   // dotted
    [InlineData("FooBar", "FooBar")]    // already one fragment — collides with the dotted form
    public void BuildReadableFactoryTypeToken_SeparatorVariants_FoldToSameToken(string input, string folded)
    {
        Assert.Equal(folded, ConcreteProtocolSpecializationEmitter.BuildReadableFactoryTypeToken(input));
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
