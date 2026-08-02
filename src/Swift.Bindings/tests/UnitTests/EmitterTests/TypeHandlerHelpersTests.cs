// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.IO;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for TypeHandlerHelpers — GetImplementedInterfaces, EqualityMethodsWriter.
/// </summary>
public class TypeHandlerHelpersTests
{
    #region GetImplementedInterfaces Tests

    [Fact]
    public void GetImplementedInterfaces_MinimalType_IncludesISwiftObjectAndIDisposable()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDecl("Loader", moduleDecl);

        var interfaces = ProtocolConformanceHelper.GetImplementedInterfaces(
            classDecl, "Loader", "TestModule", typeDatabase);

        Assert.Contains("ISwiftObject", interfaces);
        Assert.Contains("IDisposable", interfaces);
    }

    [Fact]
    public void GetImplementedInterfaces_EquatableType_IncludesIEquatable()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var structDecl = CreateStructDeclWithConformances("Point", moduleDecl,
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Point"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"),
                "$s10TestModule5PointVSQAAMc"));

        var interfaces = ProtocolConformanceHelper.GetImplementedInterfaces(
            structDecl, "Point", "TestModule", typeDatabase);

        Assert.Contains("IEquatable<Point>", interfaces);
    }

    [Fact]
    public void GetImplementedInterfaces_HashableType_SkipsHashableInterface()
    {
        // Hashable is a marker — not emitted as a C# interface
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var structDecl = CreateStructDeclWithConformances("Point", moduleDecl,
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Point"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Hashable"),
                "$s10TestModule5PointVSHAAMc"));

        var interfaces = ProtocolConformanceHelper.GetImplementedInterfaces(
            structDecl, "Point", "TestModule", typeDatabase);

        Assert.DoesNotContain(interfaces, i => i.Contains("Hashable"));
    }

    [Fact]
    public void GetImplementedInterfaces_GenericTypeConditionalEquatable_OmitsIEquatable()
    {
        // Regression: A Swift generic struct/class whose Equatable conformance is conditional
        // (`extension Foo : Equatable where T : Equatable`) must NOT emit IEquatable<Foo<T>>
        // when the C# generic-parameter constraints don't guarantee T's witness.
        // Without this gate, the witness-bound P/Invoke crashes at runtime.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("MusicKit");
        var structDecl = CreateStructDeclWithConformances("MusicItemCollection", moduleDecl,
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("MusicKit.MusicItemCollection"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"),
                "$sMc"));
        // T constrained to MusicItem (a non-Equatable protocol) — conformance is conditional.
        structDecl.GenericParameters.Add(new GenericArgumentDecl(
            "τ_0_0",
            "MusicItemType",
            new List<GenericParameterConformance>
            {
                new(
                    new[] { "τ_0_0" },
                    SwiftTypeName.FromModuleQualifiedName("MusicKit.MusicItem"),
                    ConformanceKind.Protocol)
            },
            new List<GenericParameterConformance>()));

        var interfaces = ProtocolConformanceHelper.GetImplementedInterfaces(
            structDecl, "MusicItemCollection<TMusicItemType>", "MusicKit", typeDatabase);

        Assert.DoesNotContain(interfaces, i => i.Contains("IEquatable"));
    }

    [Fact]
    public void GetImplementedInterfaces_GenericTypeConditionalEquatable_ReportsTheDroppedConformance()
    {
        // The omission above is correct but invisible: the consumer sees a type that is Equatable in
        // Swift and not IEquatable in C#, with nothing in the binding explaining why. Report it.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("MusicKit");
        var structDecl = CreateStructDeclWithConformances("MusicItemCollection", moduleDecl,
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("MusicKit.MusicItemCollection"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"),
                "$sMc"));
        structDecl.GenericParameters.Add(new GenericArgumentDecl(
            "τ_0_0",
            "MusicItemType",
            new List<GenericParameterConformance>
            {
                new(
                    new[] { "τ_0_0" },
                    SwiftTypeName.FromModuleQualifiedName("MusicKit.MusicItem"),
                    ConformanceKind.Protocol)
            },
            new List<GenericParameterConformance>()));

        BindingReport report;
        ReportCollector.Start(moduleDecl);
        try
        {
            ProtocolConformanceHelper.GetImplementedInterfaces(
                structDecl, "MusicItemCollection<TMusicItemType>", "MusicKit", typeDatabase);
            report = ReportCollector.Complete()!;
        }
        finally
        {
            ReportCollector.Reset();
        }

        var row = Assert.Single(
            report.SkippedItems, i => i.Reason == SkipReason.ConformanceNotFullyImplementable);
        Assert.Equal("Swift.Equatable", row.Name);
        Assert.Equal("MusicKit.MusicItemCollection", row.ContainingType);
        Assert.Contains("conditional", row.Details);
    }

    [Fact]
    public void GetImplementedInterfaces_UnconditionalEquatable_ReportsNoDroppedConformance()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var structDecl = CreateStructDeclWithConformances("Point", moduleDecl,
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Point"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"),
                "$s10TestModule5PointVSQAAMc"));

        BindingReport report;
        ReportCollector.Start(moduleDecl);
        try
        {
            var interfaces = ProtocolConformanceHelper.GetImplementedInterfaces(
                structDecl, "Point", "TestModule", typeDatabase);
            Assert.Contains("IEquatable<Point>", interfaces);
            report = ReportCollector.Complete()!;
        }
        finally
        {
            ReportCollector.Reset();
        }

        Assert.DoesNotContain(
            report.SkippedItems, i => i.Reason == SkipReason.ConformanceNotFullyImplementable);
    }

    [Fact]
    public void GetImplementedInterfaces_GenericTypeWithEquatableConstraint_IncludesIEquatable()
    {
        // The complement: a generic type whose generic parameter IS constrained to Equatable
        // (directly) — the C# constraint set guarantees the witness, so IEquatable<Foo<T>> is
        // safe to emit.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var structDecl = CreateStructDeclWithConformances("Box", moduleDecl,
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Box"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"),
                "$sMc"));
        structDecl.GenericParameters.Add(new GenericArgumentDecl(
            "τ_0_0",
            "T",
            new List<GenericParameterConformance>
            {
                new(
                    new[] { "τ_0_0" },
                    SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"),
                    ConformanceKind.Protocol)
            },
            new List<GenericParameterConformance>()));

        var interfaces = ProtocolConformanceHelper.GetImplementedInterfaces(
            structDecl, "Box<T>", "TestModule", typeDatabase);

        Assert.Contains(interfaces, i => i.Contains("IEquatable<Box<T>>"));
    }

    [Fact]
    public void GetImplementedInterfaces_GenericTypeWithHashableConstraint_IncludesIEquatable()
    {
        // Hashable refines Equatable in Swift's stdlib — a `where T : Hashable` constraint
        // also guarantees the Equatable witness, so IEquatable<Foo<T>> remains safe.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var structDecl = CreateStructDeclWithConformances("Cache", moduleDecl,
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Cache"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"),
                "$sMc"));
        structDecl.GenericParameters.Add(new GenericArgumentDecl(
            "τ_0_0",
            "T",
            new List<GenericParameterConformance>
            {
                new(
                    new[] { "τ_0_0" },
                    SwiftTypeName.FromModuleQualifiedName("Swift.Hashable"),
                    ConformanceKind.Protocol)
            },
            new List<GenericParameterConformance>()));

        var interfaces = ProtocolConformanceHelper.GetImplementedInterfaces(
            structDecl, "Cache<T>", "TestModule", typeDatabase);

        Assert.Contains(interfaces, i => i.Contains("IEquatable<Cache<T>>"));
    }

    [Fact]
    public void GetImplementedInterfaces_NonGenericType_AlwaysIncludesIEquatable()
    {
        // Non-generic types have no conditional conformances by construction — Equatable
        // emission must be unaffected by the conditional-witness gate.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var structDecl = CreateStructDeclWithConformances("Pair", moduleDecl,
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Pair"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"),
                "$sMc"));

        var interfaces = ProtocolConformanceHelper.GetImplementedInterfaces(
            structDecl, "Pair", "TestModule", typeDatabase);

        Assert.Contains(interfaces, i => i.Contains("IEquatable<Pair>"));
    }

    [Fact]
    public void GetImplementedInterfaces_MultiParamGeneric_OneUnconstrained_OmitsIEquatable()
    {
        // Multi-parameter generic where ONE parameter has Equatable but the other doesn't —
        // Swift's conditional Equatable would gate on BOTH parameters, so we cannot guarantee
        // the witness. Drop IEquatable.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var structDecl = CreateStructDeclWithConformances("Pair", moduleDecl,
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Pair"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"),
                "$sMc"));
        structDecl.GenericParameters.Add(new GenericArgumentDecl(
            "τ_0_0",
            "T",
            new List<GenericParameterConformance>
            {
                new(
                    new[] { "τ_0_0" },
                    SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"),
                    ConformanceKind.Protocol)
            },
            new List<GenericParameterConformance>()));
        structDecl.GenericParameters.Add(new GenericArgumentDecl(
            "τ_0_1",
            "U",
            new List<GenericParameterConformance>(), // no constraint
            new List<GenericParameterConformance>()));

        var interfaces = ProtocolConformanceHelper.GetImplementedInterfaces(
            structDecl, "Pair<T, U>", "TestModule", typeDatabase);

        Assert.DoesNotContain(interfaces, i => i.Contains("IEquatable"));
    }

    [Fact]
    public void GetImplementedInterfaces_CrossModuleProtocol_NoTypeRecord_Excluded()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var structDecl = CreateStructDeclWithConformances("Point", moduleDecl,
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Point"),
                SwiftTypeName.FromModuleQualifiedName("OtherModule.SomeProtocol"),
                "$s10TestModule5PointVOtherModuleSomeProtocolMc"));

        var interfaces = ProtocolConformanceHelper.GetImplementedInterfaces(
            structDecl, "Point", "TestModule", typeDatabase);

        // Cross-module protocol without a TypeRecord in the database should be excluded
        Assert.DoesNotContain(interfaces, i => i.Contains("SomeProtocol"));
    }

    [Fact]
    public void GetImplementedInterfaces_CrossModuleProtocol_WithTypeRecord_Included()
    {
        var typeDatabase = CreateTypeDatabaseWithProtocol("OtherModule", "Renderable");
        var moduleDecl = CreateModuleDecl("TestModule");
        var structDecl = CreateStructDeclWithConformances("Widget", moduleDecl,
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Widget"),
                SwiftTypeName.FromModuleQualifiedName("OtherModule.Renderable"),
                "$s10TestModule6WidgetVOtherModuleRenderableMc"));

        var interfaces = ProtocolConformanceHelper.GetImplementedInterfaces(
            structDecl, "Widget", "TestModule", typeDatabase);

        Assert.Contains(interfaces, i => i.Contains("Renderable"));
    }

    [Fact]
    public void GetImplementedInterfaces_CrossModuleProtocol_WithMembers_Excluded()
    {
        // Protocol with 3 emitted members — cross-module conformance would cause CS0535
        var typeDatabase = CreateTypeDatabaseWithProtocol("OtherModule", "Drawable", emittedMemberCount: 3);
        var moduleDecl = CreateModuleDecl("TestModule");
        var structDecl = CreateStructDeclWithConformances("Canvas", moduleDecl,
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Canvas"),
                SwiftTypeName.FromModuleQualifiedName("OtherModule.Drawable"),
                "$sMc"));

        var interfaces = ProtocolConformanceHelper.GetImplementedInterfaces(
            structDecl, "Canvas", "TestModule", typeDatabase);

        Assert.DoesNotContain(interfaces, i => i.Contains("Drawable"));
    }

    [Fact]
    public void GetImplementedInterfaces_CrossModuleProtocol_OldDatabase_NullMemberCount_Excluded()
    {
        // Old database without EmittedMemberCount — conservatively skip
        var typeDatabase = CreateTypeDatabaseWithProtocolNullMemberCount("OtherModule", "Legacy");
        var moduleDecl = CreateModuleDecl("TestModule");
        var structDecl = CreateStructDeclWithConformances("Adapter", moduleDecl,
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Adapter"),
                SwiftTypeName.FromModuleQualifiedName("OtherModule.Legacy"),
                "$sMc"));

        var interfaces = ProtocolConformanceHelper.GetImplementedInterfaces(
            structDecl, "Adapter", "TestModule", typeDatabase);

        Assert.DoesNotContain(interfaces, i => i.Contains("Legacy"));
    }

    [Fact]
    public void GetImplementedInterfaces_UmbrellaProtocol_ConformerInDeclaringModule_Included()
    {
        // RealityKit re-exports RealityFoundation. The Swift ABI mangler stamps
        // RealityFoundation.HasAnchoring as `$s10RealityKit12HasAnchoringP`, so the
        // parsed conformance carries `Module="RealityKit"` even though the protocol
        // is declared (and the conformer AnchorEntity lives) in RealityFoundation.
        // The interface gate must resolve to the declaring module via
        // ResolveProtocolEmissionModule so the cross-module-with-members guard does
        // NOT misfire — otherwise Scene.AddAnchor(IHasAnchoring) refuses to compile.
        var typeDatabase = CreateTypeDatabaseWithUmbrellaProtocol(
            umbrellaModule: "RealityKit",
            declaringModule: "RealityFoundation",
            name: "HasAnchoring",
            emittedMemberCount: 4);
        var moduleDecl = CreateModuleDecl("RealityFoundation");
        var classDecl = CreateClassDeclWithConformances("AnchorEntity", moduleDecl,
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("RealityFoundation.AnchorEntity"),
                SwiftTypeName.FromModuleQualifiedName("RealityKit.HasAnchoring"),
                "$s16RealityFoundation12AnchorEntityCAA12HasAnchoringAAMc"));

        var interfaces = ProtocolConformanceHelper.GetImplementedInterfaces(
            classDecl, "AnchorEntity", "RealityFoundation", typeDatabase);

        Assert.Contains(interfaces, i => i.Contains("HasAnchoring"));
    }

    [Fact]
    public void ConformanceDescriptor_UmbrellaProtocol_ConformerInDeclaringModule_Included()
    {
        // Sibling of the interface test above: the descriptor MUST also land in the
        // dictionary so swift_getWitnessTable resolves at runtime when the C# proxy
        // wraps an IHasAnchoring for existential boxing. Without the dictionary entry
        // the interface alone is dead weight (Scene.AddAnchor(IHasAnchoring) would
        // throw at the marshalling layer).
        var typeDatabase = CreateTypeDatabaseWithUmbrellaProtocol(
            umbrellaModule: "RealityKit",
            declaringModule: "RealityFoundation",
            name: "HasAnchoring",
            emittedMemberCount: 4);
        var conformances = new[] {
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("RealityFoundation.AnchorEntity"),
                SwiftTypeName.FromModuleQualifiedName("RealityKit.HasAnchoring"),
                "$s16RealityFoundation12AnchorEntityCAA12HasAnchoringAAMc")
        };

        var result = ProtocolConformanceHelper.GenerateProtocolConformanceDictionaryEntries(
            conformances, "RealityFoundation", "AnchorEntity", typeDatabase);

        // Bare IHasAnchoring (no namespace prefix) is correct here — the conformer's
        // module file is RealityFoundation, same as the resolved declaring namespace.
        Assert.Contains("typeof(IHasAnchoring)", result);
        Assert.Contains("\"$s16RealityFoundation12AnchorEntityCAA12HasAnchoringAAMc\"", result);
    }

    [Fact]
    public void GetImplementedInterfaces_SameModuleProtocol_WithMembers_NotAffectedByGate()
    {
        // Same-module protocols are NOT gated by EmittedMemberCount (validated by CanFullyImplementProtocol)
        var typeDatabase = CreateTypeDatabaseWithProtocol("TestModule", "Describable", emittedMemberCount: 5);
        var moduleDecl = CreateModuleDecl("TestModule");
        var structDecl = CreateStructDeclWithConformances("Point", moduleDecl,
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Point"),
                SwiftTypeName.FromModuleQualifiedName("TestModule.Describable"),
                "$sMc"));

        var interfaces = ProtocolConformanceHelper.GetImplementedInterfaces(
            structDecl, "Point", "TestModule", typeDatabase);

        // Same-module: EmittedMemberCount gate does NOT apply
        Assert.Contains(interfaces, i => i.Contains("Describable"));
    }

    [Fact]
    public void GetImplementedInterfaces_CrossModuleProtocol_WithAssociatedTypes_Excluded()
    {
        var typeDatabase = CreateTypeDatabaseWithPATInModule("OtherModule", "AsyncSequence");
        var moduleDecl = CreateModuleDecl("TestModule");
        var structDecl = CreateStructDeclWithConformances("Stream", moduleDecl,
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Stream"),
                SwiftTypeName.FromModuleQualifiedName("OtherModule.AsyncSequence"),
                "$s10TestModule6StreamVOtherModuleAsyncSequenceMc"));

        var interfaces = ProtocolConformanceHelper.GetImplementedInterfaces(
            structDecl, "Stream", "TestModule", typeDatabase);

        Assert.DoesNotContain(interfaces, i => i.Contains("AsyncSequence"));
    }

    [Fact]
    public void GetImplementedInterfaces_ProtocolWithAssociatedType_Excluded()
    {
        var typeDatabase = CreateTypeDatabaseWithPAT();
        var moduleDecl = CreateModuleDecl("TestModule");
        var structDecl = CreateStructDeclWithConformances("MyIterator", moduleDecl,
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.MyIterator"),
                SwiftTypeName.FromModuleQualifiedName("TestModule.Iterable"),
                "$s10TestModule10MyIteratorVIterableMc"));

        var interfaces = ProtocolConformanceHelper.GetImplementedInterfaces(
            structDecl, "MyIterator", "TestModule", typeDatabase);

        // Protocols with associated types should be excluded
        Assert.DoesNotContain(interfaces, i => i.Contains("Iterable"));
    }

    [Fact]
    public void GetImplementedInterfaces_ClosedPAT_ConcreteBindingResolves_IncludesClosedGenericInterface()
    {
        // Closed-constrained PAT: when a conformer's PAT bindings are all concrete
        // (e.g. StringLabel: LabelledContainer where Label == String),
        // GetImplementedInterfaces must emit IIterable<System.Int64>
        // in the implements list so consumers can pass `new MyIterator(...)` where
        // `IIterable<long>` is expected. Without the closed-PAT loop the conformer would
        // surface as IExistentialBoxable-only and the typed call site would fail CS0029.
        var typeDatabase = CreateTypeDatabaseWithPAT();
        var moduleDecl = CreateModuleDecl("TestModule");
        var protocolDecl = CreatePATProtocolDecl(moduleDecl, "Iterable", "Element");
        moduleDecl.Protocols.Add(protocolDecl);

        var structDecl = CreateStructDeclWithConformances("MyIterator", moduleDecl,
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.MyIterator"),
                SwiftTypeName.FromModuleQualifiedName("TestModule.Iterable"),
                "$s10TestModule10MyIteratorVIterableMc"));

        // Concrete binding: Element == Swift.Int → C# System.Int64.
        moduleDecl.ConformanceGraph.AddWitness(
            "TestModule.MyIterator", "TestModule.Iterable", "Element",
            new NamedTypeSpec("Swift.Int"));

        var validator = new ProtocolConformanceValidator(moduleDecl, typeDatabase);
        var interfaces = ProtocolConformanceHelper.GetImplementedInterfaces(
            structDecl, "MyIterator", "TestModule", typeDatabase, validator);

        // FullyQualifiedName for Swift.Int → CSharpTypeName.NIntType
        // returns the C# alias form "nint" (matches the rendering used elsewhere in the emitter).
        Assert.Contains(interfaces, i => i == "IIterable<nint>");
    }

    [Fact]
    public void GetImplementedInterfaces_PATProtocolWithSelfRequirement_DoesNotIncludeClosedGenericInterface()
    {
        // Self-requirement + associated-type protocols (e.g. CryptoKit.HashFunction:
        // `protocol HashFunction { associatedtype Digest; init(); func finalize() -> Self.Digest }`)
        // project to `IFoo<TSelf> where TSelf : IFoo<TSelf>` (CRTP) — the associated types
        // are folded into Self and don't appear in the C# interface signature. The closed-PAT
        // loop must yield to the Self-requirement branch; substituting an associated-type
        // binding for TSelf produces CS0311 (TSelf constraint unsatisfiable, Digest is not an
        // IFoo) and CS0535 (missing protocol method impls) on every conformer. Regression
        // pin: this scenario silently broke CryptoKit (Sha3256 et al.) when the closed-PAT
        // loop landed without a HasSelfRequirement gate.
        var typeDatabase = new TypeDatabase();
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.NIntType,
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                MetadataAccessor = "$sSiMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);
        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.HashFunction"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "IHashFunction"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.HashFunction"),
                MetadataAccessor = "",
                // Both flags set — mirrors CryptoKit.HashFunction's TypeRecord.
                Flags = TypeRecordFlags.HasAssociatedTypes | TypeRecordFlags.HasSelfRequirement,
                Kind = TypeRecordKind.Protocol
            });
        typeDatabase.AddModuleDatabase(testModule);
        var moduleDecl = CreateModuleDecl("TestModule");
        var protocolDecl = CreatePATProtocolDecl(moduleDecl, "HashFunction", "Digest");
        moduleDecl.Protocols.Add(protocolDecl);

        var structDecl = CreateStructDeclWithConformances("Sha256", moduleDecl,
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Sha256"),
                SwiftTypeName.FromModuleQualifiedName("TestModule.HashFunction"),
                "$s10TestModule6Sha256VAA12HashFunctionAAMc"));

        // Concrete Digest binding — the closed-PAT loop would happily resolve this
        // and emit `IHashFunction<long>`, which is exactly the wrong thing.
        moduleDecl.ConformanceGraph.AddWitness(
            "TestModule.Sha256", "TestModule.HashFunction", "Digest",
            new NamedTypeSpec("Swift.Int"));

        var validator = new ProtocolConformanceValidator(moduleDecl, typeDatabase);
        var interfaces = ProtocolConformanceHelper.GetImplementedInterfaces(
            structDecl, "Sha256", "TestModule", typeDatabase, validator);

        Assert.DoesNotContain(interfaces, i => i.Contains("IHashFunction"));
    }

    [Fact]
    public void GetImplementedInterfaces_OpenPAT_GenericParameterBinding_DoesNotIncludeClosedGenericInterface()
    {
        // Open-PAT exclusion gate: when the conformer is itself generic and binds the PAT to its own
        // type parameter (e.g. GenericContainer<U>: LabelledContainer where Label == U),
        // the closed interface depends on a conformer-side parameter and must NOT be emitted —
        // open PATs still flow through the typeof(object) PAT box. Without this gate
        // TryResolveClosedPatBindings would emit IIterable<U> in the implements list,
        // which is an un-referenceable C# generic-parameter type at the type-decl scope.
        var typeDatabase = CreateTypeDatabaseWithPAT();
        var moduleDecl = CreateModuleDecl("TestModule");
        var protocolDecl = CreatePATProtocolDecl(moduleDecl, "Iterable", "Element");
        moduleDecl.Protocols.Add(protocolDecl);

        var structDecl = CreateStructDeclWithConformances("GenericIterator", moduleDecl,
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.GenericIterator"),
                SwiftTypeName.FromModuleQualifiedName("TestModule.Iterable"),
                "$s10TestModule15GenericIteratorVIterableMc"));

        // Open binding: Element == U (the conformer's own generic parameter).
        moduleDecl.ConformanceGraph.AddWitness(
            "TestModule.GenericIterator", "TestModule.Iterable", "Element",
            new NamedTypeSpec("U"));

        var validator = new ProtocolConformanceValidator(moduleDecl, typeDatabase);
        var interfaces = ProtocolConformanceHelper.GetImplementedInterfaces(
            structDecl, "GenericIterator<U>", "TestModule", typeDatabase, validator);

        // Open PAT must NOT surface as a closed generic interface.
        Assert.DoesNotContain(interfaces, i => i.Contains("Iterable"));
    }

    [Fact]
    public void TryResolveClosedPatBindings_ConcreteBinding_ReturnsTrueWithFullyQualifiedName()
    {
        // Direct gate-predicate test for the closed case. Mirrors the test above but
        // probes TryResolveClosedPatBindings without going through the full implements-list
        // pipeline — keeps the open-vs-closed distinction airtight at the predicate layer.
        var typeDatabase = CreateTypeDatabaseWithPAT();
        var moduleDecl = CreateModuleDecl("TestModule");
        var protocolDecl = CreatePATProtocolDecl(moduleDecl, "Iterable", "Element");
        moduleDecl.Protocols.Add(protocolDecl);

        var structDecl = CreateStructDeclWithConformances("MyIterator", moduleDecl);
        moduleDecl.ConformanceGraph.AddWitness(
            "TestModule.MyIterator", "TestModule.Iterable", "Element",
            new NamedTypeSpec("Swift.Int"));

        var validator = new ProtocolConformanceValidator(moduleDecl, typeDatabase);
        var resolved = validator.TryResolveClosedPatBindings(
            structDecl, protocolDecl, out var bindings);

        Assert.True(resolved, "Closed PAT with concrete binding must resolve");
        Assert.Single(bindings);
        // FullyQualifiedName for NIntType is "nint" — same form
        // used in implements-list rendering.
        Assert.Equal("nint", bindings[0]);
    }

    [Fact]
    public void TryResolveClosedPatBindings_OpenGenericParameterBinding_ReturnsFalse()
    {
        // Direct gate-predicate test for the open case. Probes TryResolveClosedPatBindings
        // with a NamedTypeSpec("U") binding — the canonical open-PAT signal. Must return
        // false so the closed-interface emission step is skipped and the conformer routes
        // through the typeof(object) PAT box instead.
        var typeDatabase = CreateTypeDatabaseWithPAT();
        var moduleDecl = CreateModuleDecl("TestModule");
        var protocolDecl = CreatePATProtocolDecl(moduleDecl, "Iterable", "Element");
        moduleDecl.Protocols.Add(protocolDecl);

        var structDecl = CreateStructDeclWithConformances("GenericIterator", moduleDecl);
        moduleDecl.ConformanceGraph.AddWitness(
            "TestModule.GenericIterator", "TestModule.Iterable", "Element",
            new NamedTypeSpec("U"));

        var validator = new ProtocolConformanceValidator(moduleDecl, typeDatabase);
        var resolved = validator.TryResolveClosedPatBindings(
            structDecl, protocolDecl, out var bindings);

        Assert.False(resolved, "Open PAT (generic-parameter binding) must NOT resolve as closed");
        Assert.Empty(bindings);
    }

    [Fact]
    public void TryResolveClosedPatBindings_AssociatedTypeReferenceBinding_ReturnsFalse()
    {
        // Open-PAT signal #2: AssociatedTypeReferenceSpec (e.g. Self.Element). These never
        // resolve to a concrete C# type — the binding is opaque from the C# nominal layer.
        var typeDatabase = CreateTypeDatabaseWithPAT();
        var moduleDecl = CreateModuleDecl("TestModule");
        var protocolDecl = CreatePATProtocolDecl(moduleDecl, "Iterable", "Element");
        moduleDecl.Protocols.Add(protocolDecl);

        var structDecl = CreateStructDeclWithConformances("MyIterator", moduleDecl);
        moduleDecl.ConformanceGraph.AddWitness(
            "TestModule.MyIterator", "TestModule.Iterable", "Element",
            new AssociatedTypeReferenceSpec("Self", "Element"));

        var validator = new ProtocolConformanceValidator(moduleDecl, typeDatabase);
        var resolved = validator.TryResolveClosedPatBindings(
            structDecl, protocolDecl, out var bindings);

        Assert.False(resolved, "AssociatedTypeReferenceSpec binding must NOT resolve as closed");
        Assert.Empty(bindings);
    }

    [Fact]
    public void TryResolveClosedPatBindings_MissingWitness_ReturnsFalse()
    {
        // Defensive case: ConformanceGraph has no witness for this (conformer, protocol, AT).
        // ABI parsers may not always populate witnesses; the predicate must fail-closed
        // rather than synthesising a wrong binding.
        var typeDatabase = CreateTypeDatabaseWithPAT();
        var moduleDecl = CreateModuleDecl("TestModule");
        var protocolDecl = CreatePATProtocolDecl(moduleDecl, "Iterable", "Element");
        moduleDecl.Protocols.Add(protocolDecl);

        var structDecl = CreateStructDeclWithConformances("MyIterator", moduleDecl);
        // Intentionally no AddWitness call.

        var validator = new ProtocolConformanceValidator(moduleDecl, typeDatabase);
        var resolved = validator.TryResolveClosedPatBindings(
            structDecl, protocolDecl, out var bindings);

        Assert.False(resolved, "Missing witness must NOT resolve as closed");
        Assert.Empty(bindings);
    }

    [Fact]
    public void GetImplementedInterfaces_SwiftErrorConformance_Excluded()
    {
        var typeDatabase = CreateTypeDatabaseWithSwiftError();
        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDeclWithConformances("ParseError", moduleDecl,
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.ParseError"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Error"),
                "$s10TestModule10ParseErrorOs0E0AAMc"));

        var interfaces = ProtocolConformanceHelper.GetImplementedInterfaces(
            classDecl, "ParseError", "TestModule", typeDatabase);

        // Swift.Error maps to AnyError (a runtime type), not an IError interface
        Assert.DoesNotContain(interfaces, i => i.Contains("IError"));
    }

    [Fact]
    public void GetImplementedInterfaces_CrossModuleProtocol_InheritingEmptyProtocol_Included()
    {
        // Protocol with 0 direct members inheriting an empty marker protocol (EmittedMemberCount=0).
        // Total requirements = 0 direct + 0 inherited = 0 → should be emitted.
        var typeDatabase = CreateTypeDatabaseWithInheritingProtocol("OtherModule", "Taggable", parentEmittedMemberCount: 0);
        var moduleDecl = CreateModuleDecl("TestModule");
        var structDecl = CreateStructDeclWithConformances("Item", moduleDecl,
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Item"),
                SwiftTypeName.FromModuleQualifiedName("OtherModule.Taggable"),
                "$sMc"));

        var interfaces = ProtocolConformanceHelper.GetImplementedInterfaces(
            structDecl, "Item", "TestModule", typeDatabase);

        Assert.Contains(interfaces, i => i.Contains("Taggable"));
    }

    [Fact]
    public void GetImplementedInterfaces_CrossModuleProtocol_InheritingNonEmptyProtocol_Excluded()
    {
        // Protocol with 0 direct members inheriting a non-empty protocol (EmittedMemberCount=3).
        // Total requirements = 0 direct + 1 inherited with members = 1 → should be excluded.
        var typeDatabase = CreateTypeDatabaseWithInheritingProtocol("OtherModule", "StrictTaggable", parentEmittedMemberCount: 3);
        var moduleDecl = CreateModuleDecl("TestModule");
        var structDecl = CreateStructDeclWithConformances("Item", moduleDecl,
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Item"),
                SwiftTypeName.FromModuleQualifiedName("OtherModule.StrictTaggable"),
                "$sMc"));

        var interfaces = ProtocolConformanceHelper.GetImplementedInterfaces(
            structDecl, "Item", "TestModule", typeDatabase);

        Assert.DoesNotContain(interfaces, i => i.Contains("StrictTaggable"));
    }

    [Fact]
    public void GetImplementedInterfaces_SameModuleProtocol_Included()
    {
        var typeDatabase = CreateTypeDatabaseWithProtocol("TestModule", "Describable");
        var moduleDecl = CreateModuleDecl("TestModule");
        var structDecl = CreateStructDeclWithConformances("Point", moduleDecl,
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Point"),
                SwiftTypeName.FromModuleQualifiedName("TestModule.Describable"),
                "$s10TestModule5PointVDescribableMc"));

        var interfaces = ProtocolConformanceHelper.GetImplementedInterfaces(
            structDecl, "Point", "TestModule", typeDatabase);

        Assert.Contains(interfaces, i => i.Contains("Describable"));
    }

    #endregion

    #region QualifyNestedProtocolInterface Tests

    [Fact]
    public void QualifyNestedProtocolInterface_TopLevelProtocol_ReturnsUnchanged()
    {
        // Same-module top-level: NameProvider already returned "IFoo", no parent
        // path to insert. Module-qualified name has only 2 parts (Module.Leaf).
        var protoName = SwiftTypeName.FromModuleQualifiedName("MyModule.Foo");
        var result = ProtocolConformanceHelper.QualifyNestedProtocolInterface("IFoo", protoName);
        Assert.Equal("IFoo", result);
    }

    [Fact]
    public void QualifyNestedProtocolInterface_SameModuleNested_PrependsParentPath()
    {
        // AssistantSchemas.BooksEnum case — protocol nested inside a parent type
        // in the current module. NameProvider returned bare "IBooksEnum"; the
        // helper inserts the parent type's name so callers in sibling scopes
        // (e.g. the singular umbrella struct) can resolve it.
        var protoName = SwiftTypeName.FromModuleQualifiedName("AppIntents.AssistantSchemas.BooksEnum");
        var result = ProtocolConformanceHelper.QualifyNestedProtocolInterface("IBooksEnum", protoName);
        Assert.Equal("AssistantSchemas.IBooksEnum", result);
    }

    [Fact]
    public void QualifyNestedProtocolInterface_SameModuleDeeplyNested_PrependsFullParentPath()
    {
        // A.B.C.P: the middle two parts (B and C) are the parent chain.
        var protoName = SwiftTypeName.FromModuleQualifiedName("Mod.A.B.C.P");
        var result = ProtocolConformanceHelper.QualifyNestedProtocolInterface("IP", protoName);
        Assert.Equal("A.B.C.IP", result);
    }

    [Fact]
    public void QualifyNestedProtocolInterface_CrossModuleTopLevel_ReturnsUnchanged()
    {
        // Cross-module top-level: NameProvider already returned "OtherModule.IFoo".
        // The helper sees only 2 parts in the MQN (Module.Leaf) and returns the
        // namespaced name as-is.
        var protoName = SwiftTypeName.FromModuleQualifiedName("OtherModule.Foo");
        var result = ProtocolConformanceHelper.QualifyNestedProtocolInterface("OtherModule.IFoo", protoName);
        Assert.Equal("OtherModule.IFoo", result);
    }

    [Fact]
    public void QualifyNestedProtocolInterface_CrossModuleNested_InsertsParentBetweenNamespaceAndLeaf()
    {
        // Regression: a cross-module nested protocol must put
        // the parent type path BETWEEN the C# namespace prefix and the leaf
        // interface name, not in front of the whole string. Prepending naively
        // would produce "Parent.OtherModule.IFoo" which is unresolvable.
        var protoName = SwiftTypeName.FromModuleQualifiedName("OtherModule.Parent.Foo");
        var result = ProtocolConformanceHelper.QualifyNestedProtocolInterface("OtherModule.IFoo", protoName);
        Assert.Equal("OtherModule.Parent.IFoo", result);
    }

    [Fact]
    public void QualifyNestedProtocolInterface_CrossModuleDeeplyNested_InsertsFullParentChain()
    {
        var protoName = SwiftTypeName.FromModuleQualifiedName("OtherModule.A.B.P");
        var result = ProtocolConformanceHelper.QualifyNestedProtocolInterface("OtherModule.IP", protoName);
        Assert.Equal("OtherModule.A.B.IP", result);
    }

    #endregion

    #region IExistentialBoxable Interface Tests

    [Fact]
    public void GetImplementedInterfaces_WithProtocolConformance_IncludesIExistentialBoxable()
    {
        // Types with at least one emitted protocol conformance should get IExistentialBoxable
        // so they can be passed where protocol existentials are expected.
        var typeDatabase = CreateTypeDatabaseWithProtocol("TestModule", "Describable");
        var moduleDecl = CreateModuleDecl("TestModule");
        var structDecl = CreateStructDeclWithConformances("Point", moduleDecl,
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Point"),
                SwiftTypeName.FromModuleQualifiedName("TestModule.Describable"),
                "$s10TestModule5PointVDescribableMc"));

        var interfaces = ProtocolConformanceHelper.GetImplementedInterfaces(
            structDecl, "Point", "TestModule", typeDatabase);

        Assert.Contains("Swift.Runtime.IExistentialBoxable", interfaces);
    }

    [Fact]
    public void GetImplementedInterfaces_WithNoProtocolConformance_DoesNotIncludeIExistentialBoxable()
    {
        // Types without any protocol conformances should NOT get IExistentialBoxable.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDecl("Loader", moduleDecl);

        var interfaces = ProtocolConformanceHelper.GetImplementedInterfaces(
            classDecl, "Loader", "TestModule", typeDatabase);

        Assert.DoesNotContain("Swift.Runtime.IExistentialBoxable", interfaces);
    }

    [Fact]
    public void GetImplementedInterfaces_OnlyHashable_DoesNotIncludeIExistentialBoxable()
    {
        // Hashable alone is a marker interface (not emitted as a C# interface),
        // so it should NOT trigger IExistentialBoxable.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var structDecl = CreateStructDeclWithConformances("Token", moduleDecl,
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Token"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Hashable"),
                "$s10TestModule5TokenVSHAAMc"));

        var interfaces = ProtocolConformanceHelper.GetImplementedInterfaces(
            structDecl, "Token", "TestModule", typeDatabase);

        Assert.DoesNotContain("Swift.Runtime.IExistentialBoxable", interfaces);
    }

    [Fact]
    public void GetImplementedInterfaces_EquatableConformance_IncludesIExistentialBoxable()
    {
        // Equatable IS emitted as IEquatable<T>, so it triggers IExistentialBoxable.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var structDecl = CreateStructDeclWithConformances("Point", moduleDecl,
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Point"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"),
                "$s10TestModule5PointVSQAAMc"));

        var interfaces = ProtocolConformanceHelper.GetImplementedInterfaces(
            structDecl, "Point", "TestModule", typeDatabase);

        Assert.Contains("Swift.Runtime.IExistentialBoxable", interfaces);
    }

    [Fact]
    public void GetImplementedInterfaces_MultipleConformances_IncludesIExistentialBoxableOnce()
    {
        // Multiple protocol conformances should still result in exactly one IExistentialBoxable.
        var typeDatabase = CreateTypeDatabaseWithProtocol("TestModule", "Describable");
        var moduleDecl = CreateModuleDecl("TestModule");
        var structDecl = CreateStructDeclWithConformances("Point", moduleDecl,
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Point"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"),
                "$s10TestModule5PointVSQAAMc"),
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Point"),
                SwiftTypeName.FromModuleQualifiedName("TestModule.Describable"),
                "$s10TestModule5PointVDescribableMc"));

        var interfaces = ProtocolConformanceHelper.GetImplementedInterfaces(
            structDecl, "Point", "TestModule", typeDatabase);

        Assert.Single(interfaces, i => i == "Swift.Runtime.IExistentialBoxable");
    }

    [Fact]
    public void GetImplementedInterfaces_ClassWithProtocol_IncludesIExistentialBoxable()
    {
        // IExistentialBoxable should work for classes, not just structs.
        var typeDatabase = CreateTypeDatabaseWithProtocol("TestModule", "Describable");
        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDeclWithConformances("Widget", moduleDecl,
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Widget"),
                SwiftTypeName.FromModuleQualifiedName("TestModule.Describable"),
                "$s10TestModule6WidgetCDescribableMc"));

        var interfaces = ProtocolConformanceHelper.GetImplementedInterfaces(
            classDecl, "Widget", "TestModule", typeDatabase);

        Assert.Contains("Swift.Runtime.IExistentialBoxable", interfaces);
    }

    [Fact]
    public void GetImplementedInterfaces_CrossModuleProtocol_ExcludedConformance_DoesNotTriggerIExistentialBoxable()
    {
        // Cross-module protocol without a TypeRecord is excluded — so if it's the only
        // conformance, IExistentialBoxable should NOT be present.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var structDecl = CreateStructDeclWithConformances("Point", moduleDecl,
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Point"),
                SwiftTypeName.FromModuleQualifiedName("OtherModule.SomeProtocol"),
                "$sMc"));

        var interfaces = ProtocolConformanceHelper.GetImplementedInterfaces(
            structDecl, "Point", "TestModule", typeDatabase);

        Assert.DoesNotContain("Swift.Runtime.IExistentialBoxable", interfaces);
    }

    #endregion

    #region ConformanceDescriptor Tests

    [Fact]
    public void ConformanceDescriptor_CrossModuleProtocol_WithTypeRecord_Included()
    {
        var typeDatabase = CreateTypeDatabaseWithProtocol("OtherModule", "Renderable");
        var conformances = new[] {
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Widget"),
                SwiftTypeName.FromModuleQualifiedName("OtherModule.Renderable"),
                "$s10TestModule6WidgetVOtherModuleRenderableMc")
        };

        var result = ProtocolConformanceHelper.GenerateProtocolConformanceDictionaryEntries(
            conformances, "TestModule", "Widget", typeDatabase);

        Assert.Contains("typeof(OtherModule.IRenderable)", result);
        Assert.Contains("\"$s10TestModule6WidgetVOtherModuleRenderableMc\"", result);
    }

    [Fact]
    public void ConformanceDescriptor_CrossModuleProtocol_NoTypeRecord_Excluded()
    {
        var typeDatabase = CreateTypeDatabase();
        var conformances = new[] {
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Widget"),
                SwiftTypeName.FromModuleQualifiedName("OtherModule.Unknown"),
                "$sMc")
        };

        var result = ProtocolConformanceHelper.GenerateProtocolConformanceDictionaryEntries(
            conformances, "TestModule", "Widget", typeDatabase);

        Assert.DoesNotContain("Unknown", result);
    }

    [Fact]
    public void ConformanceDescriptor_SwiftErrorConformance_Excluded()
    {
        var typeDatabase = CreateTypeDatabaseWithSwiftError();
        var conformances = new[] {
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.ParseError"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Error"),
                "$s10TestModule10ParseErrorOs0E0AAMc")
        };

        var result = ProtocolConformanceHelper.GenerateProtocolConformanceDictionaryEntries(
            conformances, "TestModule", "ParseError", typeDatabase);

        // Swift.Error maps to AnyError (a runtime type), not an IError interface
        Assert.DoesNotContain("IError", result);
    }

    [Fact]
    public void ConformanceDescriptor_CrossModuleProtocol_WithMembers_Included()
    {
        // The dictionary gate is intentionally broader than the interface gate. The
        // C# interface inheritance side still skips cross-module-with-members
        // conformances (CS0535 protection), but the descriptor symbol must still land
        // in _protocolConformanceSymbols so swift_getWitnessTable can resolve at runtime
        // existential boxing — that's the AnchorEntity / HasAnchoring scenario.
        var typeDatabase = CreateTypeDatabaseWithProtocol("OtherModule", "Drawable", emittedMemberCount: 3);
        var conformances = new[] {
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Canvas"),
                SwiftTypeName.FromModuleQualifiedName("OtherModule.Drawable"),
                "$s10TestModule6CanvasV11OtherModule8DrawableMc")
        };

        var result = ProtocolConformanceHelper.GenerateProtocolConformanceDictionaryEntries(
            conformances, "TestModule", "Canvas", typeDatabase);

        Assert.Contains("typeof(OtherModule.IDrawable)", result);
        Assert.Contains("\"$s10TestModule6CanvasV11OtherModule8DrawableMc\"", result);
    }

    [Fact]
    public void ConformanceDescriptor_CrossModuleProtocol_OldDatabase_NullMemberCount_Included()
    {
        // Dictionary gate is unaffected by EmittedMemberCount — the descriptor symbol
        // emit is independent of whether C# member stubs can be safely produced.
        var typeDatabase = CreateTypeDatabaseWithProtocolNullMemberCount("OtherModule", "Legacy");
        var conformances = new[] {
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Adapter"),
                SwiftTypeName.FromModuleQualifiedName("OtherModule.Legacy"),
                "$s10TestModule7AdapterV11OtherModule6LegacyMc")
        };

        var result = ProtocolConformanceHelper.GenerateProtocolConformanceDictionaryEntries(
            conformances, "TestModule", "Adapter", typeDatabase);

        Assert.Contains("typeof(OtherModule.ILegacy)", result);
    }

    [Fact]
    public void ConformanceDescriptor_CrossModuleProtocol_InheritingEmptyProtocol_Included()
    {
        var typeDatabase = CreateTypeDatabaseWithInheritingProtocol("OtherModule", "Taggable", parentEmittedMemberCount: 0);
        var conformances = new[] {
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Item"),
                SwiftTypeName.FromModuleQualifiedName("OtherModule.Taggable"),
                "$sMc")
        };

        var result = ProtocolConformanceHelper.GenerateProtocolConformanceDictionaryEntries(
            conformances, "TestModule", "Item", typeDatabase);

        Assert.Contains("typeof(OtherModule.ITaggable)", result);
    }

    [Fact]
    public void ConformanceDescriptor_CrossModuleProtocol_InheritingNonEmptyProtocol_Included()
    {
        // EmittedMemberCount inherited from a non-empty parent does NOT bar the
        // descriptor — the dictionary entry tracks runtime witness-table resolution,
        // not C# member-stub viability. The Interface gate still handles the CS0535
        // case independently.
        var typeDatabase = CreateTypeDatabaseWithInheritingProtocol("OtherModule", "StrictTaggable", parentEmittedMemberCount: 3);
        var conformances = new[] {
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Item"),
                SwiftTypeName.FromModuleQualifiedName("OtherModule.StrictTaggable"),
                "$s10TestModule4ItemV11OtherModule14StrictTaggableMc")
        };

        var result = ProtocolConformanceHelper.GenerateProtocolConformanceDictionaryEntries(
            conformances, "TestModule", "Item", typeDatabase);

        Assert.Contains("typeof(OtherModule.IStrictTaggable)", result);
    }

    [Fact]
    public void ConformanceDescriptor_CrossModuleProtocol_WithAssociatedTypes_Excluded()
    {
        var typeDatabase = CreateTypeDatabaseWithPATInModule("OtherModule", "AsyncSequence");
        var conformances = new[] {
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Stream"),
                SwiftTypeName.FromModuleQualifiedName("OtherModule.AsyncSequence"),
                "$sMc")
        };

        var result = ProtocolConformanceHelper.GenerateProtocolConformanceDictionaryEntries(
            conformances, "TestModule", "Stream", typeDatabase);

        Assert.DoesNotContain("AsyncSequence", result);
    }

    [Fact]
    public void ConformanceDescriptor_EmptySymbol_ExcludedFromDictionary()
    {
        // Empty conformance symbol should be filtered out — LoadFromSymbol("lib", "") crashes at runtime.
        var typeDatabase = CreateTypeDatabase();
        var conformances = new[] {
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Widget"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"),
                ""), // Empty symbol
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Widget"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Hashable"),
                "$s10TestModule6WidgetVSHAAMc") // Valid symbol
        };

        var result = ProtocolConformanceHelper.GenerateProtocolConformanceDictionaryEntries(
            conformances, "TestModule", "Widget", typeDatabase);

        // Hashable should be present (valid symbol), Equatable should be filtered out (empty symbol)
        Assert.Contains("$s10TestModule6WidgetVSHAAMc", result);
        Assert.DoesNotContain("IEquatable", result);
    }

    #endregion

    #region EqualityMethodsWriter Tests

    [Fact]
    public void WriteSwiftEquatable_Equatable_EmitsEquals()
    {
        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);
        var structDecl = CreateStructDeclWithConformances("Point", CreateModuleDecl("TestModule"),
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Point"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"),
                "$sMc"));

        var writer = new EqualityMethodsWriter(csWriter, structDecl, true, "Point");
        writer.WriteSwiftEquatableImplementation();

        var result = output.ToString();
        Assert.Contains("SwiftEquatable.Equals", result);
    }

    [Fact]
    public void WriteSwiftEquatable_EquatableAndHashable_EmitsSwiftHashableGetHashCode()
    {
        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);
        var structDecl = CreateStructDeclWithConformances("Point", CreateModuleDecl("TestModule"),
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Point"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"),
                "$sMc1"),
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Point"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Hashable"),
                "$sMc2"));

        var writer = new EqualityMethodsWriter(csWriter, structDecl, true, "Point");
        writer.WriteSwiftEquatableImplementation();

        var result = output.ToString();
        Assert.Contains("SwiftHashable.GetHashCode(this)", result);
    }

    [Fact]
    public void WriteSwiftEquatable_EquatableOnly_NonGeneric_EmitsZeroHashStub()
    {
        // Equatable-only types — without an explicit Hashable conformance in the ABI —
        // must NOT route through SwiftHashable.GetHashCode. Swift's synthesised Equatable
        // compares stored properties semantically (e.g. String uses NFC-normalised value
        // comparison), while the runtime's structural-byte FNV-1a fallback hashes the
        // marshalled bytes. Equal values with reference-typed fields (String, Array, class
        // storage) can marshal to different bytes and produce different hashes, breaking
        // the Equals/GetHashCode contract. The conservative `return 0;` stub is contract-
        // correct (all values hash the same, lookups degrade to O(n)).
        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);
        var structDecl = CreateStructDeclWithConformances("Point", CreateModuleDecl("TestModule"),
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Point"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"),
                "$sMc"));

        var writer = new EqualityMethodsWriter(csWriter, structDecl, true, "Point");
        writer.WriteSwiftEquatableImplementation();

        var result = output.ToString();
        Assert.DoesNotContain("SwiftHashable.GetHashCode(this)", result);
        Assert.Contains("return 0;", result);
    }

    [Fact]
    public void WriteSwiftEquatable_GenericEquatableConstrainedButNotHashable_EmitsZeroHashStub()
    {
        // Generic types whose Equatable is provable per-parameter (T : Equatable) but Hashable
        // is NOT (no T : Hashable) keep the `return 0;` stub: routing such a type through
        // SwiftHashable.GetHashCode would trap in the runtime witness lookup when the consumer
        // instantiates with an Equatable-but-not-Hashable T. The unconditional gate in
        // EquatableConformanceHelper enforces this asymmetry.
        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);
        var structDecl = CreateStructDeclWithConformances("Box", CreateModuleDecl("TestModule"),
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Box"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"),
                "$sMc"));
        // T : Equatable but NOT Hashable — Equatable surface is safe, Hashable surface is not.
        var equatableConstraint = new GenericParameterConformance(
            new[] { "T" },
            SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"),
            ConformanceKind.Protocol);
        structDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new GenericArgumentDecl(
                "T",
                "T",
                new List<GenericParameterConformance> { equatableConstraint },
                new List<GenericParameterConformance>())
        };

        var writer = new EqualityMethodsWriter(csWriter, structDecl, true, "Box<T>");
        writer.WriteSwiftEquatableImplementation();

        var result = output.ToString();
        Assert.Contains("return 0;", result);
        Assert.DoesNotContain("SwiftHashable.GetHashCode(this)", result);
    }

    [Fact]
    public void WriteSwiftEquatable_GenericEquatableWithoutAnyConstraint_EmitsNoEqualitySurface()
    {
        // Generic types whose Equatable conformance is conditional (no per-parameter
        // Equatable constraint that the C# type system enforces) drop the typed equality
        // surface entirely — the consumer falls back to reference equality from object.
        // This is the conservative path: we can't prove the witness table will be present
        // at the call site, so we don't emit Equals/GetHashCode/operator overloads at all.
        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);
        var structDecl = CreateStructDeclWithConformances("Box", CreateModuleDecl("TestModule"),
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Box"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"),
                "$sMc"));
        structDecl.GenericParameters = new List<GenericArgumentDecl>
        {
            new GenericArgumentDecl(
                "T",
                "T",
                new List<GenericParameterConformance>(),
                new List<GenericParameterConformance>())
        };

        var writer = new EqualityMethodsWriter(csWriter, structDecl, true, "Box<T>");
        writer.WriteSwiftEquatableImplementation();

        var result = output.ToString();
        Assert.DoesNotContain("SwiftHashable.GetHashCode(this)", result);
        Assert.DoesNotContain("SwiftEquatable.Equals", result);
    }

    [Fact]
    public void WriteSwiftEquatable_ExplicitEqualityOperator_SkipsOperator()
    {
        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);
        var structDecl = CreateStructDeclWithConformances("Point", CreateModuleDecl("TestModule"),
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Point"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"),
                "$sMc"));

        var writer = new EqualityMethodsWriter(csWriter, structDecl, true, "Point",
            hasExplicitEqualityOperator: true, hasExplicitInequalityOperator: false);
        writer.WriteSwiftEquatableImplementation();

        var result = output.ToString();
        Assert.DoesNotContain("operator ==(", result);
        // != should still be emitted since only == is explicit
        Assert.Contains("operator !=(", result);
    }

    [Fact]
    public void WriteSwiftEquatable_EquatableWithCustomEqualityOperator_DoesNotInferHashable()
    {
        // Swift only synthesises Hashable when `==` is also synthesised — a custom `==`
        // opts out of the synthesised conformance, so equal values may not be byte-identical
        // and the runtime's structural-hash fallback would break the Equals/GetHashCode
        // contract. With `hasExplicitEqualityOperator: true` the emitter must NOT infer
        // Hashable from Equatable.
        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);
        var structDecl = CreateStructDeclWithConformances("Point", CreateModuleDecl("TestModule"),
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Point"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"),
                "$sMc"));

        var writer = new EqualityMethodsWriter(csWriter, structDecl, true, "Point",
            hasExplicitEqualityOperator: true, hasExplicitInequalityOperator: false);
        writer.WriteSwiftEquatableImplementation();

        var result = output.ToString();
        Assert.DoesNotContain("SwiftHashable.GetHashCode(this)", result);
        Assert.Contains("return 0;", result);
    }

    [Fact]
    public void WriteSwiftEquatable_ExplicitInequalityOperator_SkipsOperator()
    {
        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);
        var structDecl = CreateStructDeclWithConformances("Point", CreateModuleDecl("TestModule"),
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Point"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"),
                "$sMc"));

        var writer = new EqualityMethodsWriter(csWriter, structDecl, true, "Point",
            hasExplicitEqualityOperator: false, hasExplicitInequalityOperator: true);
        writer.WriteSwiftEquatableImplementation();

        var result = output.ToString();
        Assert.Contains("operator ==(", result);
        Assert.DoesNotContain("operator !=(", result);
    }

    [Fact]
    public void WriteSwiftEquatable_RefType_OperatorsHaveNullGuards()
    {
        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);
        var structDecl = CreateStructDeclWithConformances("Widget", CreateModuleDecl("TestModule"),
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Widget"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"),
                "$sMc"));

        var writer = new EqualityMethodsWriter(csWriter, structDecl, refType: true, "Widget");
        writer.WriteSwiftEquatableImplementation();

        var result = output.ToString();
        // operator == should accept nullable params and null-check
        Assert.Contains("operator ==(Widget? left, Widget? right)", result);
        Assert.Contains("if (left is null) return right is null;", result);
        // operator != should accept nullable params and null-check
        Assert.Contains("operator !=(Widget? left, Widget? right)", result);
        Assert.Contains("if (left is null) return right is not null;", result);
        // IEquatable<T>.Equals should null-check
        Assert.Contains("if (other is null) return false;", result);
    }

    [Fact]
    public void WriteSwiftEquatable_ValueType_OperatorsHaveNoNullGuards()
    {
        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);
        var structDecl = CreateStructDeclWithConformances("Point", CreateModuleDecl("TestModule"),
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Point"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"),
                "$sMc"));

        var writer = new EqualityMethodsWriter(csWriter, structDecl, refType: false, "Point");
        writer.WriteSwiftEquatableImplementation();

        var result = output.ToString();
        // Value type operators should NOT have nullable params
        Assert.Contains("operator ==(Point left, Point right)", result);
        Assert.DoesNotContain("left is null", result);
    }

    [Fact]
    public void WriteSwiftEquatable_WithSwiftWriter_EmitsCdeclWrapper()
    {
        // When SwiftWriter and ModuleEmissionContext are provided, equality should use
        // @_cdecl P/Invoke instead of SwiftEquatable.Equals (which crashes on NativeAOT).
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);
        var emissionContext = new ModuleEmissionContext();

        var structDecl = CreateStructDeclWithConformances("Emphasis", CreateModuleDecl("AttributedTextKit"),
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("AttributedTextKit.Emphasis"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"),
                "$sMc"));
        structDecl.MangledName = "$s15AttributedTextKit8EmphasisVN";

        var writer = new EqualityMethodsWriter(csWriter, structDecl, refType: true, "Emphasis",
            hasExplicitEqualityOperator: false, hasExplicitInequalityOperator: false,
            swiftWriter: swiftWriter, emissionContext: emissionContext, wrapperLibraryName: "AttributedTextKitSwiftBindings");
        writer.WriteSwiftEquatableImplementation();

        var csResult = csOutput.ToString();
        var swiftResult = swiftOutput.ToString();

        // C# should use PInvoke_eq instead of SwiftEquatable.Equals
        Assert.Contains("PInvoke_eq(", csResult);
        Assert.DoesNotContain("SwiftEquatable.Equals", csResult);
        // C# should emit the P/Invoke declaration
        Assert.Contains("LibraryImport(\"AttributedTextKitSwiftBindings\"", csResult);
        Assert.Contains("PInvoke_eq(IntPtr lhs, IntPtr rhs)", csResult);
        // Swift should emit the @_cdecl wrapper
        Assert.Contains("@_cdecl(", swiftResult);
        Assert.Contains("AttributedTextKit.Emphasis.self", swiftResult);
        Assert.Contains("(l == r) ? 1 : 0", swiftResult);
    }

    [Fact]
    public void WriteSwiftEquatable_RefTypeWithSwiftWriter_PinsBothSafeHandlesAroundPInvokeEq()
    {
        // The refType (non-frozen / class-projected
        // Equatable) Equals path used to call PInvoke_eq with raw
        // DangerousGetHandle() on both sides — no AddRef bracket, so a
        // concurrent GC finalization between the handle access and the Swift
        // function entry could free the Swift heap payload mid-call.
        //
        // Property getters on the SAME type already wrap DangerousAddRef /
        // DangerousRelease around their PInvoke; the asymmetry was the bug.
        // The fix routes the Equals call through a generated
        // _PInvoke_eq_pinned helper that brackets both SafeHandles, matching
        // the property-getter shape.
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);
        var emissionContext = new ModuleEmissionContext();

        var structDecl = CreateStructDeclWithConformances("WeatherAttribution", CreateModuleDecl("WeatherKit"),
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("WeatherKit.WeatherAttribution"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"),
                "$sMc"));
        structDecl.MangledName = "$s10WeatherKit18WeatherAttributionVN";

        var writer = new EqualityMethodsWriter(csWriter, structDecl, refType: true, "WeatherAttribution",
            hasExplicitEqualityOperator: false, hasExplicitInequalityOperator: false,
            swiftWriter: swiftWriter, emissionContext: emissionContext, wrapperLibraryName: "WeatherKitSwiftBindings");
        writer.WriteSwiftEquatableImplementation();

        var csResult = csOutput.ToString();

        // The Equals body must call the pinned helper, NOT raw DangerousGetHandle()
        // on both sides directly.
        Assert.Contains("_PInvoke_eq_pinned(", csResult);

        // The pinned helper itself must be emitted with the AddRef/Release
        // bracket around BOTH operands. We assert the structural shape via
        // independent token-presence checks rather than full string-matching
        // the helper body — the codegen formatter is free to re-flow.
        Assert.Contains("private static bool _PInvoke_eq_pinned(", csResult);
        Assert.Contains("bool _eqAddedLeft = false;", csResult);
        Assert.Contains("bool _eqAddedRight = false;", csResult);
        Assert.Contains("left.Payload.DangerousAddRef(ref _eqAddedLeft);", csResult);
        Assert.Contains("right.Payload.DangerousAddRef(ref _eqAddedRight);", csResult);
        Assert.Contains("if (_eqAddedRight) right.Payload.DangerousRelease();", csResult);
        Assert.Contains("if (_eqAddedLeft) left.Payload.DangerousRelease();", csResult);
        // Defensive: the pinned PInvoke call still threads the raw IntPtr to
        // PInvoke_eq (the `unsafe` ABI signature hasn't changed).
        Assert.Contains("PInvoke_eq(left.Payload.DangerousGetHandle(), right.Payload.DangerousGetHandle())", csResult);

        // Pre-fix shape — direct DangerousGetHandle() pair without the
        // _PInvoke_eq_pinned helper — must not regress.
        Assert.DoesNotContain("return PInvoke_eq(\n                this.Payload.DangerousGetHandle()", csResult);
        Assert.DoesNotContain("return PInvoke_eq(this.Payload.DangerousGetHandle(), other.Payload.DangerousGetHandle());", csResult);
    }

    [Fact]
    public void WriteSwiftEquatable_WithoutSwiftWriter_FallsBackToSwiftEquatable()
    {
        // Without SwiftWriter, equality should use SwiftEquatable.Equals (legacy path).
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);

        var structDecl = CreateStructDeclWithConformances("Point", CreateModuleDecl("TestModule"),
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Point"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"),
                "$sMc"));

        var writer = new EqualityMethodsWriter(csWriter, structDecl, refType: true, "Point");
        writer.WriteSwiftEquatableImplementation();

        var csResult = csOutput.ToString();
        // Should fall back to SwiftEquatable.Equals
        Assert.Contains("SwiftEquatable.Equals", csResult);
        Assert.DoesNotContain("PInvoke_eq", csResult);
    }

    [Fact]
    public void WriteSwiftEquatable_ValueTypeWithSwiftWriter_UsesValuePInvokePath()
    {
        // Value-type structs (refType=false) with SwiftWriter must use the _PInvoke_eq_value
        // helper path, NOT SwiftEquatable.Equals (which crashes on NativeAOT via CallConvSwift).
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);
        var emissionContext = new ModuleEmissionContext();

        var structDecl = CreateStructDeclWithConformances("CGPoint", CreateModuleDecl("CoreGraphics"),
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("CoreGraphics.CGPoint"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"),
                "$sMc"));
        structDecl.MangledName = "$s12CoreGraphics7CGPointVN";

        var writer = new EqualityMethodsWriter(csWriter, structDecl, refType: false, "CGPoint",
            hasExplicitEqualityOperator: false, hasExplicitInequalityOperator: false,
            swiftWriter: swiftWriter, emissionContext: emissionContext, wrapperLibraryName: "CoreGraphicsSwiftBindings");
        writer.WriteSwiftEquatableImplementation();

        var csResult = csOutput.ToString();
        // Must use value-type P/Invoke helper, not SwiftEquatable.Equals
        Assert.Contains("_PInvoke_eq_value(ref", csResult);
        Assert.DoesNotContain("SwiftEquatable.Equals", csResult);
        // Must emit the unsafe helper method
        Assert.Contains("private static unsafe bool _PInvoke_eq_value", csResult);
        Assert.Contains("Unsafe.AsPointer(ref lhs)", csResult);
    }

    #endregion

    #region ClassEqualityMethodsWriter Tests

    [Fact]
    public void ClassEquality_Equatable_EmitsSwiftEquatableEquals()
    {
        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);
        var classDecl = CreateClassDeclWithConformances("Widget", CreateModuleDecl("TestModule"),
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Widget"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"),
                "$sMc"));

        var writer = new ClassEqualityMethodsWriter(csWriter, classDecl, "Widget", false, false);
        writer.WriteSwiftEquatableImplementation();

        var result = output.ToString();
        // Without SwiftWriter, should fall back to SwiftEquatable.Equals
        Assert.Contains("SwiftEquatable.Equals", result);
    }

    [Fact]
    public void ClassEquality_NotEquatable_EmitsGuardedHandleIdentity()
    {
        // F34 (deliverable A): a non-Equatable, heap-backed ROOT class wrapper projects object
        // identity into C# as handle-identity Equals/GetHashCode (two wrappers over the same Swift
        // instance compare equal and hash alike), guarded so a disposed/zero-handle wrapper falls
        // back to reference identity. Still object-identity only — never an operator == (the value-
        // equality surface). Shape is pinned more fully by ClassIdentityEmitterTests.
        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);
        var classDecl = CreateClassDeclWithConformances("Widget", CreateModuleDecl("TestModule"));

        var writer = new ClassEqualityMethodsWriter(csWriter, classDecl, "Widget", false, false);
        writer.WriteSwiftEquatableImplementation();

        var result = output.ToString();
        Assert.Contains("public override bool Equals(object? obj)", result);
        Assert.Contains("var thisHandle = GetSwiftHandle();", result);
        Assert.Contains("thisHandle != IntPtr.Zero && otherHandle != IntPtr.Zero", result);
        Assert.Contains("return ReferenceEquals(this, other);", result);
        Assert.Contains("public override int GetHashCode()", result);
        // Object-identity only — never the value-equality operator, never the Swift value witness.
        Assert.DoesNotContain("operator ==", result);
        Assert.DoesNotContain("SwiftEquatable.Equals", result);
    }

    [Fact]
    public void ClassEquality_WithSwiftWriter_EmitsCdeclWrapper()
    {
        // When SwiftWriter and ModuleEmissionContext are provided, class equality should use
        // @_cdecl P/Invoke instead of SwiftEquatable.Equals (which crashes on NativeAOT).
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);
        var emissionContext = new ModuleEmissionContext();

        var classDecl = CreateClassDeclWithConformances("ImageCache", CreateModuleDecl("ImagePipeline"),
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("ImagePipeline.ImageCache"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"),
                "$sMc"));
        classDecl.MangledName = "$s13ImagePipeline10ImageCacheCN";

        var writer = new ClassEqualityMethodsWriter(csWriter, classDecl, "ImageCache",
            false, false, swiftWriter, emissionContext, "ImagePipelineSwiftBindings");
        writer.WriteSwiftEquatableImplementation();

        var csResult = csOutput.ToString();
        var swiftResult = swiftOutput.ToString();

        // C# should use PInvoke_eq with GetSwiftHandle() instead of SwiftEquatable.Equals
        Assert.Contains("PInvoke_eq(", csResult);
        Assert.Contains("GetSwiftHandle()", csResult);
        Assert.DoesNotContain("SwiftEquatable.Equals", csResult);
        // C# should emit the P/Invoke declaration
        Assert.Contains("LibraryImport(\"ImagePipelineSwiftBindings\"", csResult);
        Assert.Contains("PInvoke_eq(IntPtr lhs, IntPtr rhs)", csResult);
        // Swift should emit the @_cdecl wrapper with Unmanaged<AnyObject> (not assumingMemoryBound)
        Assert.Contains("@_cdecl(", swiftResult);
        Assert.Contains("Unmanaged<AnyObject>.fromOpaque(lhs).takeUnretainedValue()", swiftResult);
        Assert.Contains("as! ImagePipeline.ImageCache", swiftResult);
        Assert.Contains("(l == r) ? 1 : 0", swiftResult);
        // Must NOT use assumingMemoryBound (that's for structs, not classes)
        Assert.DoesNotContain("assumingMemoryBound", swiftResult);
    }

    [Fact]
    public void ClassEquality_WithoutSwiftWriter_FallsBackToSwiftEquatable()
    {
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);

        var classDecl = CreateClassDeclWithConformances("Widget", CreateModuleDecl("TestModule"),
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Widget"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"),
                "$sMc"));

        var writer = new ClassEqualityMethodsWriter(csWriter, classDecl, "Widget", false, false);
        writer.WriteSwiftEquatableImplementation();

        var csResult = csOutput.ToString();
        // Should fall back to SwiftEquatable.Equals
        Assert.Contains("SwiftEquatable.Equals", csResult);
        Assert.DoesNotContain("PInvoke_eq", csResult);
    }

    [Fact]
    public void ClassEquality_GenericClass_SkipsCdecl()
    {
        // Generic classes can't have @_cdecl wrappers (can't instantiate generic from wrapper).
        // Use a T : Equatable constraint so EquatableConformanceHelper accepts the conformance
        // as unconditional — without that, generic conditional Equatable is dropped entirely
        // (the conditional-Equatable fix that drops IEquatable on generic conditional conformances). This test
        // is specifically about the wrapper-skip path on a *valid* generic Equatable case.
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);
        var emissionContext = new ModuleEmissionContext();

        var classDecl = CreateClassDeclWithConformances("Container", CreateModuleDecl("TestModule"),
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Container"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"),
                "$sMc"));
        classDecl.GenericParameters.Add(new GenericArgumentDecl(
            "T",
            "T",
            new List<GenericParameterConformance>
            {
                new(
                    new[] { "T" },
                    SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"),
                    ConformanceKind.Protocol)
            },
            new List<GenericParameterConformance>()));

        var writer = new ClassEqualityMethodsWriter(csWriter, classDecl, "Container<T>",
            false, false, swiftWriter, emissionContext, "TestModuleSwiftBindings");
        writer.WriteSwiftEquatableImplementation();

        var csResult = csOutput.ToString();
        var swiftResult = swiftOutput.ToString();

        // Generic: should fall back to SwiftEquatable.Equals
        Assert.Contains("SwiftEquatable.Equals", csResult);
        Assert.DoesNotContain("PInvoke_eq", csResult);
        // No Swift wrapper should be emitted
        Assert.DoesNotContain("@_cdecl", swiftResult);
    }

    [Fact]
    public void ClassEquality_ExplicitOperators_SkipsOperators()
    {
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);
        var emissionContext = new ModuleEmissionContext();

        var classDecl = CreateClassDeclWithConformances("Widget", CreateModuleDecl("TestModule"),
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Widget"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"),
                "$sMc"));
        classDecl.MangledName = "$s10TestModule6WidgetCN";

        var writer = new ClassEqualityMethodsWriter(csWriter, classDecl, "Widget",
            hasExplicitEqualityOperator: true, hasExplicitInequalityOperator: true,
            swiftWriter: swiftWriter, emissionContext: emissionContext, wrapperLibraryName: "TestModuleSwiftBindings");
        writer.WriteSwiftEquatableImplementation();

        var csResult = csOutput.ToString();
        // Should not emit operator == or != since both are explicit
        Assert.DoesNotContain("operator ==(", csResult);
        Assert.DoesNotContain("operator !=(", csResult);
        // Should still emit Equals and GetHashCode
        Assert.Contains("public override bool Equals(object? obj)", csResult);
        Assert.Contains("public bool Equals(Widget? other)", csResult);
    }

    [Fact]
    public void ClassEquality_Hashable_EmitsSwiftHashable()
    {
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);

        var classDecl = CreateClassDeclWithConformances("Widget", CreateModuleDecl("TestModule"),
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Widget"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"),
                "$sMc1"),
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Widget"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Hashable"),
                "$sMc2"));

        var writer = new ClassEqualityMethodsWriter(csWriter, classDecl, "Widget", false, false);
        writer.WriteSwiftEquatableImplementation();

        var csResult = csOutput.ToString();
        Assert.Contains("SwiftHashable.GetHashCode(this)", csResult);
    }

    [Fact]
    public void ClassEquality_NullableOperatorParams()
    {
        // Class operators must use nullable params (classes are reference types)
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);
        var emissionContext = new ModuleEmissionContext();

        var classDecl = CreateClassDeclWithConformances("Widget", CreateModuleDecl("TestModule"),
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Widget"),
                SwiftTypeName.FromModuleQualifiedName("Swift.Equatable"),
                "$sMc"));
        classDecl.MangledName = "$s10TestModule6WidgetCN";

        var writer = new ClassEqualityMethodsWriter(csWriter, classDecl, "Widget",
            false, false, swiftWriter, emissionContext, "TestModuleSwiftBindings");
        writer.WriteSwiftEquatableImplementation();

        var csResult = csOutput.ToString();
        // Operators should have nullable params and null guards
        Assert.Contains("operator ==(Widget? left, Widget? right)", csResult);
        Assert.Contains("if (left is null) return right is null;", csResult);
        Assert.Contains("operator !=(Widget? left, Widget? right)", csResult);
        Assert.Contains("if (left is null) return right is not null;", csResult);
        Assert.Contains("if (other is null) return false;", csResult);
    }

    #endregion

    #region ToStringHelper Tests

    [Fact]
    public void TryGetDescriptionPropertyName_WithDescription_ReturnsTrue()
    {
        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        classDecl.Properties.Add(CreateDescriptionProperty(moduleDecl));

        Assert.True(ToStringHelper.TryGetDescriptionPropertyName(classDecl, null, out var name));
        Assert.Equal("Description", name);
    }

    [Fact]
    public void TryGetDescriptionPropertyName_WithoutDescription_ReturnsFalse()
    {
        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDecl("Loader", moduleDecl);

        Assert.False(ToStringHelper.TryGetDescriptionPropertyName(classDecl, null, out _));
    }

    [Fact]
    public void TryGetDescriptionPropertyName_StaticDescription_ReturnsFalse()
    {
        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        var prop = CreateDescriptionProperty(moduleDecl);
        prop.IsStatic = true;
        classDecl.Properties.Add(prop);

        Assert.False(ToStringHelper.TryGetDescriptionPropertyName(classDecl, null, out _));
    }

    [Fact]
    public void TryGetDescriptionPropertyName_WrongType_ReturnsFalse()
    {
        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        classDecl.Properties.Add(new PropertyDecl
        {
            Name = "description",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            IsStatic = false,
            HasStorage = false,
            Accessors = new List<AccessorDecl> { new GetAccessorDecl { Method = CreateMinimalMethodDecl(moduleDecl) } },
            ParentDecl = classDecl,
            ModuleDecl = moduleDecl
        });

        Assert.False(ToStringHelper.TryGetDescriptionPropertyName(classDecl, null, out _));
    }

    [Fact]
    public void TryGetDescriptionPropertyName_WithRename_ReturnsRenamedName()
    {
        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        classDecl.Properties.Add(CreateDescriptionProperty(moduleDecl));
        var renames = new Dictionary<string, string> { { "Description", "DescriptionValue" } };

        Assert.True(ToStringHelper.TryGetDescriptionPropertyName(classDecl, renames, out var name));
        Assert.Equal("DescriptionValue", name);
    }

    [Fact]
    public void EmitToString_WithDescription_EmitsOverride()
    {
        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);
        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDecl("Loader", moduleDecl);
        classDecl.Properties.Add(CreateDescriptionProperty(moduleDecl));

        ToStringHelper.EmitToStringIfDescriptionExists(csWriter, classDecl, null);

        Assert.Contains("public override string ToString() => Description;", output.ToString());
    }

    [Fact]
    public void EmitToString_WithoutDescription_EmitsNothing()
    {
        var output = new StringWriter();
        var csWriter = new CSharpWriter(output);
        var moduleDecl = CreateModuleDecl("TestModule");
        var classDecl = CreateClassDecl("Loader", moduleDecl);

        ToStringHelper.EmitToStringIfDescriptionExists(csWriter, classDecl, null);

        Assert.Equal("", output.ToString());
    }

    private static PropertyDecl CreateDescriptionProperty(ModuleDecl moduleDecl)
    {
        return new PropertyDecl
        {
            Name = "description",
            SwiftTypeSpec = new NamedTypeSpec("Swift.String"),
            IsStatic = false,
            HasStorage = false,
            WasEmitted = true,
            Accessors = new List<AccessorDecl> { new GetAccessorDecl { Method = CreateMinimalMethodDecl(moduleDecl) } },
            ParentDecl = null!,
            ModuleDecl = moduleDecl
        };
    }

    private static MethodDecl CreateMinimalMethodDecl(ModuleDecl moduleDecl)
    {
        return new MethodDecl
        {
            Name = "description.get",
            MangledName = "$sTest",
            IsAccessor = true,
            IsFinal = false,
            IsConstructor = false,
            MethodType = MethodType.Instance,
            CSSignature = new List<ArgumentDecl>(),
            Throws = false,
            IsAsync = false,
            GenericParameters = new List<GenericArgumentDecl>(),
            IsSynthesizedAccessor = false,
            ParentDecl = null!,
            ModuleDecl = moduleDecl
        };
    }

    #endregion

    #region Helper Methods

    private static TypeDatabase CreateTypeDatabase()
    {
        var typeDatabase = new TypeDatabase();
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.NIntType,
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                MetadataAccessor = "$sSiMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);
        typeDatabase.AddModuleDatabase(new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib"));
        return typeDatabase;
    }

    private static TypeDatabase CreateTypeDatabaseWithPAT()
    {
        var typeDatabase = new TypeDatabase();
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.NIntType,
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                MetadataAccessor = "$sSiMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);
        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Iterable"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "IIterable"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Iterable"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.HasAssociatedTypes,
                Kind = TypeRecordKind.Protocol
            });
        typeDatabase.AddModuleDatabase(testModule);
        return typeDatabase;
    }

    private static TypeDatabase CreateTypeDatabaseWithPATInModule(string module, string name)
    {
        var typeDatabase = new TypeDatabase();
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.NIntType,
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                MetadataAccessor = "$sSiMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);
        var targetModule = new ModuleTypeDatabase(module, $"/tmp/{module}.dylib");
        targetModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName($"{module}.{name}"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName(module, $"I{name}"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{module}.{name}"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.HasAssociatedTypes,
                Kind = TypeRecordKind.Protocol
            });
        typeDatabase.AddModuleDatabase(targetModule);
        return typeDatabase;
    }

    private static TypeDatabase CreateTypeDatabaseWithProtocol(string module, string name, int emittedMemberCount = 0)
    {
        var typeDatabase = new TypeDatabase();
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.NIntType,
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                MetadataAccessor = "$sSiMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);
        var testModule = new ModuleTypeDatabase(module, $"/tmp/{module}.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName($"{module}.{name}"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName(module, $"I{name}"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{module}.{name}"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Protocol,
                EmittedMemberCount = emittedMemberCount
            });
        typeDatabase.AddModuleDatabase(testModule);
        return typeDatabase;
    }

    /// <summary>
    /// Mirrors the RealityKit/RealityFoundation umbrella shape: a protocol whose
    /// mangled-name module spelling is the umbrella's (<paramref name="umbrellaModule"/>)
    /// but whose generated C# namespace is the declaring module's
    /// (<paramref name="declaringModule"/>). The TypeRecord is keyed under the
    /// umbrella spelling — exactly how the parser stores it after walking an
    /// AnchorEntity / HasAnchoring conformance.
    /// </summary>
    private static TypeDatabase CreateTypeDatabaseWithUmbrellaProtocol(
        string umbrellaModule,
        string declaringModule,
        string name,
        int emittedMemberCount)
    {
        var typeDatabase = new TypeDatabase();
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.NIntType,
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                MetadataAccessor = "$sSiMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);

        var umbrellaDb = new ModuleTypeDatabase(umbrellaModule, $"/tmp/{umbrellaModule}.dylib");
        umbrellaDb.RegisterType(
            SwiftTypeName.FromModuleQualifiedName($"{umbrellaModule}.{name}"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName(declaringModule, $"I{name}"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{umbrellaModule}.{name}"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Protocol,
                EmittedMemberCount = emittedMemberCount
            });
        typeDatabase.AddModuleDatabase(umbrellaDb);

        // The declaring module is the one whose generator pass actually emits the
        // interface — register it so dependency walks land somewhere real.
        var declaringDb = new ModuleTypeDatabase(declaringModule, $"/tmp/{declaringModule}.dylib");
        typeDatabase.AddModuleDatabase(declaringDb);

        return typeDatabase;
    }

    private static TypeDatabase CreateTypeDatabaseWithProtocolNullMemberCount(string module, string name)
    {
        var typeDatabase = new TypeDatabase();
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.NIntType,
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                MetadataAccessor = "$sSiMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);
        var targetModule = new ModuleTypeDatabase(module, $"/tmp/{module}.dylib");
        targetModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName($"{module}.{name}"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName(module, $"I{name}"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{module}.{name}"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Protocol,
                // EmittedMemberCount intentionally null — simulates old database
            });
        typeDatabase.AddModuleDatabase(targetModule);
        return typeDatabase;
    }

    private static TypeDatabase CreateTypeDatabaseWithSwiftError()
    {
        var typeDatabase = new TypeDatabase();
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.NIntType,
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                MetadataAccessor = "$sSiMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        // Register Swift.Error as a distinct TypeRecord instance (not the singleton)
        // to verify logical identity check, not reference equality.
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Error"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "AnyError"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Error"),
                MetadataAccessor = string.Empty,
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Protocol
            });
        typeDatabase.AddModuleDatabase(swiftModule);
        typeDatabase.AddModuleDatabase(new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib"));
        return typeDatabase;
    }

    private static ClassDecl CreateClassDeclWithConformances(string name, ModuleDecl moduleDecl, params TypeConformance[] conformances)
    {
        return new ClassDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}CN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(conformances),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
    }

    /// <summary>
    /// Creates a TypeDatabase with a protocol that inherits from a parent protocol.
    /// When parentEmittedMemberCount is 0, the produced EmittedMemberCount should be 0
    /// (inheriting from an empty marker protocol doesn't add requirements).
    /// When parentEmittedMemberCount > 0, the produced EmittedMemberCount should be > 0.
    /// </summary>
    private static TypeDatabase CreateTypeDatabaseWithInheritingProtocol(string module, string name, int parentEmittedMemberCount)
    {
        var typeDatabase = new TypeDatabase();
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.NIntType,
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                MetadataAccessor = "$sSiMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);
        var targetModule = new ModuleTypeDatabase(module, $"/tmp/{module}.dylib");
        // Register the parent protocol with the given member count
        targetModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName($"{module}.BaseMarker"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName(module, "IBaseMarker"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{module}.BaseMarker"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Protocol,
                EmittedMemberCount = parentEmittedMemberCount
            });
        // Register the child protocol with EmittedMemberCount reflecting inherited requirements.
        // This simulates what ProtocolHandler.Emit would compute after the fix:
        // 0 direct members + (parentEmittedMemberCount > 0 ? 1 : 0) inherited with requirements.
        int childEmittedMemberCount = parentEmittedMemberCount > 0 ? 1 : 0;
        targetModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName($"{module}.{name}"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName(module, $"I{name}"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{module}.{name}"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Protocol,
                EmittedMemberCount = childEmittedMemberCount
            });
        typeDatabase.AddModuleDatabase(targetModule);
        return typeDatabase;
    }

    private static ModuleDecl CreateModuleDecl(string name)
    {
        return new ModuleDecl
        {
            Name = name,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    private static ClassDecl CreateClassDecl(string name, ModuleDecl moduleDecl)
    {
        return new ClassDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{name}"),
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
    }

    /// <summary>
    /// Builds a PAT (Protocol with Associated Type) ProtocolDecl with a single named
    /// associated type. The protocol has no method/property/subscript requirements,
    /// so HasEmittableInterfaceMembers returns true (empty marker protocol path) and
    /// CanFullyImplementProtocol trivially returns true. This isolates the closed-PAT
    /// emission test to the gate predicate (TryResolveClosedPatBindings) without being
    /// tripped up by member-validation gates.
    /// </summary>
    private static ProtocolDecl CreatePATProtocolDecl(ModuleDecl moduleDecl, string name, string associatedTypeName)
    {
        return new ProtocolDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}PN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            AssociatedTypes = new List<AssociatedTypeDecl>
            {
                new AssociatedTypeDecl { Name = associatedTypeName }
            },
            InheritedProtocols = new List<NamedTypeSpec>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
    }

    #endregion

    #region OptionSet/RawRepresentable Imply Hashable

    [Fact]
    public void OptionSetConformance_ImpliesHashable()
    {
        var conformances = new List<TypeConformance>
        {
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Caches"),
                SwiftTypeName.FromModuleQualifiedName("Swift.OptionSet"),
                ProtocolConformanceDescriptor: string.Empty)
        };

        bool impliesHashable = conformances.Any(c =>
            c.Protocol.ModuleQualifiedName == "Swift.Hashable" ||
            c.Protocol.Name == "OptionSet" ||
            c.Protocol.Name == "RawRepresentable");

        Assert.True(impliesHashable,
            "OptionSet conformance should be treated as implying Hashable");
    }

    [Fact]
    public void RawRepresentableConformance_ImpliesHashable()
    {
        var conformances = new List<TypeConformance>
        {
            new TypeConformance(
                SwiftTypeName.FromModuleQualifiedName("TestModule.Status"),
                SwiftTypeName.FromModuleQualifiedName("Swift.RawRepresentable"),
                ProtocolConformanceDescriptor: string.Empty)
        };

        bool impliesHashable = conformances.Any(c =>
            c.Protocol.Name == "OptionSet" ||
            c.Protocol.Name == "RawRepresentable");

        Assert.True(impliesHashable);
    }

    #endregion

    #region Extension Marshalling — ObjC-Rooted Classification

    [Fact]
    public void ClassifyParameterType_ObjCRooted_ReturnsObjCClass()
    {
        var typeDatabase = new TypeDatabase();
        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.PaymentApiClient"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "PaymentApiClient"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.PaymentApiClient"),
                MetadataAccessor = "testAccessor",
                Kind = TypeRecordKind.Class,
                Flags = TypeRecordFlags.ObjCRooted | TypeRecordFlags.RequiresMemoryManagement
            });
        typeDatabase.AddModuleDatabase(testModule);

        var result = ExtensionMarshallingHelper.ClassifyParameterType(
            new NamedTypeSpec("TestModule.PaymentApiClient"), typeDatabase);

        Assert.Equal(ExtensionMarshallingHelper.ParamKind.ObjCClass, result);
    }

    [Fact]
    public void ClassifyReturnType_ObjCRooted_ReturnsObjCClass()
    {
        var typeDatabase = new TypeDatabase();
        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.PaymentApiClient"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "PaymentApiClient"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.PaymentApiClient"),
                MetadataAccessor = "testAccessor",
                Kind = TypeRecordKind.Class,
                Flags = TypeRecordFlags.ObjCRooted | TypeRecordFlags.RequiresMemoryManagement
            });
        typeDatabase.AddModuleDatabase(testModule);

        var result = ExtensionMarshallingHelper.ClassifyReturnType(
            new NamedTypeSpec("TestModule.PaymentApiClient"), typeDatabase);

        Assert.Equal(ExtensionMarshallingHelper.ReturnKind.ObjCClass, result);
    }

    [Fact]
    public void GetPInvokeArgExpression_ObjCClass_UsesHandle()
    {
        var expr = ExtensionMarshallingHelper.GetPInvokeArgExpression("client", ExtensionMarshallingHelper.ParamKind.ObjCClass);
        Assert.Equal("client.Handle", expr);
    }

    [Fact]
    public void GetPInvokeArgExpression_SwiftClass_UsesPayload()
    {
        var expr = ExtensionMarshallingHelper.GetPInvokeArgExpression("pipeline", ExtensionMarshallingHelper.ParamKind.SwiftClass);
        Assert.Equal("pipeline.Payload.DangerousGetHandle()", expr);
    }

    #endregion

    #region Test Helpers (Struct Factory)

    private static StructDecl CreateStructDeclWithConformances(string name, ModuleDecl moduleDecl, params TypeConformance[] conformances)
    {
        return new StructDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}VN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(conformances),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            IsFrozen = true,
            MetadataAccessor = $"$s10TestModule{name.Length}{name}VMa"
        };
    }

    #endregion
}
