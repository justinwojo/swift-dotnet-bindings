// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Coverage for the Stage-3 dependency-finalization orchestration pieces:
/// <see cref="DependencyFinalizationPlanner"/> (the byte-identity-critical stable topological
/// order), <see cref="DependencyReferenceScanner"/> (the exact reference-edge extractor), and
/// <see cref="NominalSkeletonIndex"/> (the pre-layout identity plane).
///
/// The load-bearing property is <b>stability</b>: when the supplied order is already a valid
/// finalize order, the plan MUST be the identity permutation — that is the guarantee that a
/// well-formed, in-order graph produces byte-identical generated output. Reordering may only happen
/// where the supplied order actually violated a reference edge (the case the old sequential loop
/// silently lost a cross-module layout fact).
/// </summary>
public class CrossModuleFinalizationTests
{
    // ---- DependencyFinalizationPlanner: byte-identity-critical ordering ----

    private static List<string> PlanOrder(
        IReadOnlyList<string> items,
        Dictionary<string, string[]> edges)
    {
        var groups = DependencyFinalizationPlanner.Plan(
            items,
            keyOf: s => s,
            referencedModulesOf: s => edges.TryGetValue(s, out var e) ? e : System.Array.Empty<string>());
        return groups.SelectMany(g => g.Members).ToList();
    }

    [Fact]
    public void Plan_AlreadyDependencyFirstOrder_IsIdentityPermutation()
    {
        // Top references Mid references Dep; supplied dependency-first. A valid order in → same
        // order out. This is the byte-identity guarantee for the well-formed corpus.
        var items = new[] { "Dep", "Mid", "Top" };
        var edges = new Dictionary<string, string[]>
        {
            ["Mid"] = new[] { "Dep" },
            ["Top"] = new[] { "Mid" },
        };

        Assert.Equal(new[] { "Dep", "Mid", "Top" }, PlanOrder(items, edges));
    }

    [Fact]
    public void Plan_ReverseDependencyOrder_ReordersToDependencyFirst()
    {
        // The gain case: supplied dependent-first (Top, Mid, Dep). The old sequential loop would
        // finalize Top before Dep and lose Dep's foreign layout. The plan must hoist Dep first.
        var items = new[] { "Top", "Mid", "Dep" };
        var edges = new Dictionary<string, string[]>
        {
            ["Mid"] = new[] { "Dep" },
            ["Top"] = new[] { "Mid" },
        };

        Assert.Equal(new[] { "Dep", "Mid", "Top" }, PlanOrder(items, edges));
    }

    [Fact]
    public void Plan_IndependentModules_PreserveInputOrderStably()
    {
        // No edges between them: the stable topological order is exactly the input order — never a
        // DFS-order or sorted-name reshuffle.
        var items = new[] { "Charlie", "Alpha", "Bravo" };
        var edges = new Dictionary<string, string[]>();

        Assert.Equal(new[] { "Charlie", "Alpha", "Bravo" }, PlanOrder(items, edges));
    }

    [Fact]
    public void Plan_ReferenceOutsideTheSet_ImposesNoConstraint()
    {
        // A reference to a module not among the supplied items (SDK/runtime/absent input) is not an
        // edge, so it cannot reorder the set.
        var items = new[] { "One", "Two" };
        var edges = new Dictionary<string, string[]>
        {
            ["One"] = new[] { "Foundation" }, // not in the set
            ["Two"] = new[] { "Swift" },      // not in the set
        };

        Assert.Equal(new[] { "One", "Two" }, PlanOrder(items, edges));
    }

    [Fact]
    public void Plan_MutualCycle_IsOneCycleGroupWithBothMembersInInputOrder()
    {
        var items = new[] { "A", "B" };
        var edges = new Dictionary<string, string[]>
        {
            ["A"] = new[] { "B" },
            ["B"] = new[] { "A" },
        };

        var groups = DependencyFinalizationPlanner.Plan(
            items, keyOf: s => s,
            referencedModulesOf: s => edges[s]);

        Assert.Single(groups);
        Assert.True(groups[0].IsCycle);
        Assert.Equal(new[] { "A", "B" }, groups[0].Members);
    }

    [Fact]
    public void Plan_SelfReference_IsSingleMemberCycle()
    {
        var items = new[] { "Solo" };
        var edges = new Dictionary<string, string[]> { ["Solo"] = new[] { "Solo" } };

        var groups = DependencyFinalizationPlanner.Plan(
            items, keyOf: s => s, referencedModulesOf: s => edges[s]);

        Assert.Single(groups);
        Assert.True(groups[0].IsCycle);
        Assert.Equal(new[] { "Solo" }, groups[0].Members);
    }

    [Fact]
    public void Plan_AcyclicSingleton_IsNotACycle()
    {
        var items = new[] { "Solo" };
        var groups = DependencyFinalizationPlanner.Plan(
            items, keyOf: s => s, referencedModulesOf: _ => System.Array.Empty<string>());

        Assert.Single(groups);
        Assert.False(groups[0].IsCycle);
    }

    [Fact]
    public void Plan_Empty_ReturnsNoGroups()
    {
        var groups = DependencyFinalizationPlanner.Plan(
            System.Array.Empty<string>(), keyOf: s => s,
            referencedModulesOf: _ => System.Array.Empty<string>());

        Assert.Empty(groups);
    }

    [Fact]
    public void Plan_DiamondSuppliedReversed_HoistsSharedDependencyFirst()
    {
        // Diamond: Top -> {Left, Right}, both -> Base. Supplied fully reversed. Base must finalize
        // first, Top last; Left/Right keep their relative input order (stable).
        var items = new[] { "Top", "Right", "Left", "Base" };
        var edges = new Dictionary<string, string[]>
        {
            ["Top"] = new[] { "Left", "Right" },
            ["Left"] = new[] { "Base" },
            ["Right"] = new[] { "Base" },
        };

        var order = PlanOrder(items, edges);
        Assert.Equal("Base", order[0]);
        Assert.Equal("Top", order[3]);
        // Right precedes Left because Right has the lower input ordinal among the two.
        Assert.True(order.IndexOf("Right") < order.IndexOf("Left"));
    }

    // ---- NominalSkeletonIndex: pre-layout identity plane ----

    private static NominalSkeleton Skeleton(string qualified, SkeletonOwnershipState state =
        SkeletonOwnershipState.Resolved)
    {
        var name = SwiftTypeName.FromModuleQualifiedName(qualified);
        return new NominalSkeleton(name, TypeRecordKind.Struct, name.Module,
            mangledName: null, isDeclaredFrozen: false, state);
    }

    [Fact]
    public void SkeletonIndex_FirstWriterWins_OnDuplicateIdentity()
    {
        var index = new NominalSkeletonIndex();
        var owner = new NominalSkeleton(
            SwiftTypeName.FromModuleQualifiedName("Dep.Widget"), TypeRecordKind.Class,
            "Dep", "owner-mangled", isDeclaredFrozen: false, SkeletonOwnershipState.Resolved);
        var reexport = new NominalSkeleton(
            SwiftTypeName.FromModuleQualifiedName("Dep.Widget"), TypeRecordKind.Struct,
            "Reexporter", "reexport-mangled", isDeclaredFrozen: true, SkeletonOwnershipState.Resolved);

        index.Register(owner);
        index.Register(reexport); // must NOT displace the canonical owner

        Assert.True(index.TryGet(SwiftTypeName.FromModuleQualifiedName("Dep.Widget"), out var got));
        Assert.Equal("Dep", got.OwningModule);
        Assert.Equal(TypeRecordKind.Class, got.Kind);
        Assert.Equal("owner-mangled", got.MangledName);
        Assert.Equal(1, index.Count);
    }

    [Fact]
    public void SkeletonIndex_IsEmptyAndIsNominalKnown_TrackRegistration()
    {
        var index = new NominalSkeletonIndex();
        Assert.True(index.IsEmpty);
        Assert.False(index.IsNominalKnown(SwiftTypeName.FromModuleQualifiedName("Dep.Widget")));

        index.Register(Skeleton("Dep.Widget"));

        Assert.False(index.IsEmpty);
        Assert.True(index.IsNominalKnown(SwiftTypeName.FromModuleQualifiedName("Dep.Widget")));
    }

    [Fact]
    public void SkeletonIndex_UnresolvedOwners_ReturnsOnlyUnresolved()
    {
        var index = new NominalSkeletonIndex();
        index.Register(Skeleton("Dep.Resolved", SkeletonOwnershipState.Resolved));
        index.Register(Skeleton("Absent.Orphan", SkeletonOwnershipState.UnresolvedOwner));

        var unresolved = index.UnresolvedOwners();
        Assert.Single(unresolved);
        Assert.Equal("Absent", unresolved[0].OwningModule);
    }

    // ---- DependencyReferenceScanner: exact reference-edge extraction ----

    private static PropertyDecl Property(string name, TypeSpec type, bool isStatic = false) =>
        new PropertyDecl
        {
            Name = name,
            SwiftTypeSpec = type,
            IsStatic = isStatic,
            HasStorage = true,
            Accessors = new List<AccessorDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
        };

    private static StructDecl Struct(string module, string name, IEnumerable<PropertyDecl>? props = null,
        IEnumerable<TypeConformance>? conformances = null, bool isFrozen = true) =>
        new StructDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{module}.{name}"),
            MangledName = $"$s{module.Length}{module}{name.Length}{name}VN",
            IsFrozen = isFrozen,
            MetadataAccessor = $"$s{module.Length}{module}{name.Length}{name}VMa",
            Properties = props?.ToList() ?? new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Conformances = conformances?.ToList() ?? new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = null,
        };

    private static ClassDecl Class(string module, string name,
        IEnumerable<string>? superclassNames = null, IEnumerable<PropertyDecl>? props = null,
        string? superclassUsr = null) =>
        new ClassDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{module}.{name}"),
            MangledName = $"$s{module.Length}{module}{name.Length}{name}CN",
            Properties = props?.ToList() ?? new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Conformances = new List<TypeConformance>(),
            SuperclassNames = superclassNames?.ToList() ?? new List<string>(),
            SuperclassUsr = superclassUsr,
            ParentDecl = null,
            ModuleDecl = null,
        };

    private static ProtocolDecl Protocol(string module, string name,
        IEnumerable<NamedTypeSpec>? inherited = null) =>
        new ProtocolDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{module}.{name}"),
            MangledName = $"$s{module.Length}{module}{name.Length}{name}PN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            InheritedProtocols = inherited?.ToList() ?? new List<NamedTypeSpec>(),
            ParentDecl = null,
            ModuleDecl = null,
        };

    private static EnumCaseDecl Case(string name, params TypeSpec[] payloads) =>
        new EnumCaseDecl
        {
            Name = name,
            MangledName = $"case-{name}",
            AssociatedValues = payloads.ToList(),
            ParentDecl = null,
            ModuleDecl = null,
        };

    private static EnumDecl Enum(string module, string name,
        IEnumerable<EnumCaseDecl>? cases = null, bool isFrozen = true) =>
        new EnumDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{module}.{name}"),
            MangledName = $"$s{module.Length}{module}{name.Length}{name}ON",
            MetadataAccessor = $"$s{module.Length}{module}{name.Length}{name}OMa",
            IsFrozen = isFrozen,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Conformances = new List<TypeConformance>(),
            Cases = cases?.ToList() ?? new List<EnumCaseDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
        };

    private static ModuleDecl Module(string name, params TypeDecl[] types) =>
        new ModuleDecl
        {
            Name = name,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = types.ToList(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
        };

    [Fact]
    public void Scanner_StoredPropertyForeignType_YieldsThatModuleEdge()
    {
        var module = Module("Consumer",
            Struct("Consumer", "Widget",
                props: new[] { Property("core", new NamedTypeSpec("Dependency.CoreType")) }));

        var refs = DependencyReferenceScanner.ReferencedModules(module);
        Assert.Contains("Dependency", refs);
    }

    [Fact]
    public void Scanner_OwnModuleReference_IsExcluded()
    {
        var module = Module("Consumer",
            Struct("Consumer", "Widget",
                props: new[] { Property("sibling", new NamedTypeSpec("Consumer.Other")) }));

        Assert.DoesNotContain("Consumer", DependencyReferenceScanner.ReferencedModules(module));
    }

    [Fact]
    public void Scanner_StaticProperty_IsNotALayoutEdge()
    {
        // Static properties are not part of the value's storage layout, so they impose no
        // finalize-order constraint (matches the finalizer walking only non-static stored props).
        var module = Module("Consumer",
            Struct("Consumer", "Widget",
                props: new[] { Property("shared", new NamedTypeSpec("Dependency.CoreType"), isStatic: true) }));

        Assert.DoesNotContain("Dependency", DependencyReferenceScanner.ReferencedModules(module));
    }

    [Fact]
    public void Scanner_NonOptionalGenericArgument_IsNotAnEdge()
    {
        // Array/Dictionary/Set are heap containers: the finalizer's field classifier returns a
        // fixed layout for the outer container WITHOUT looking up the element record, so a foreign
        // element type imposes no finalize-order constraint. Including it would be an
        // over-approximation that could reorder an already-valid input and break byte identity.
        var generic = new NamedTypeSpec("Swift.Array", new NamedTypeSpec("Dependency.Element"));
        var module = Module("Consumer",
            Struct("Consumer", "Widget", props: new[] { Property("items", generic) }));

        Assert.DoesNotContain("Dependency", DependencyReferenceScanner.ReferencedModules(module));
    }

    [Fact]
    public void Scanner_OptionalWrappedForeignType_YieldsInnerEdge()
    {
        // Optional is the ONE generic the finalizer unwraps: it looks up the inner type's record
        // to classify the field, so a `Dependency.CoreType?` property IS an order edge.
        var optional = new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("Dependency.CoreType"));
        var module = Module("Consumer",
            Struct("Consumer", "Widget", props: new[] { Property("core", optional) }));

        Assert.Contains("Dependency", DependencyReferenceScanner.ReferencedModules(module));
    }

    [Fact]
    public void Scanner_NonFrozenStructProperty_IsNotAnEdge()
    {
        // A non-frozen struct returns from CacluateFlags before the property loop, so it never
        // looks up a foreign property record. Only a frozen struct's property types are edges.
        var module = Module("Consumer",
            Struct("Consumer", "Widget",
                props: new[] { Property("core", new NamedTypeSpec("Dependency.CoreType")) },
                isFrozen: false));

        Assert.DoesNotContain("Dependency", DependencyReferenceScanner.ReferencedModules(module));
    }

    [Fact]
    public void Scanner_StructConformance_IsNotAnEdge()
    {
        // A struct/class/enum conformance to a foreign protocol is recorded by name only
        // (BuildDirectProtocolConformances stores the qualified name; Copyable/Escapable are
        // decided by string match) — the finalizer never looks up the protocol's record, so it is
        // not an order edge.
        var conformance = new TypeConformance(
            SwiftTypeName.FromModuleQualifiedName("Consumer.Widget"),
            SwiftTypeName.FromModuleQualifiedName("Dependency.Marker"),
            "conformance-descriptor");
        var module = Module("Consumer",
            Struct("Consumer", "Widget", conformances: new[] { conformance }));

        Assert.DoesNotContain("Dependency", DependencyReferenceScanner.ReferencedModules(module));
    }

    [Fact]
    public void Scanner_ForeignSuperclass_YieldsEdge()
    {
        // ResolveClassHierarchy looks up the cross-module superclass record, so a foreign
        // superclass IS an order edge.
        var module = Module("Consumer",
            Class("Consumer", "Derived", superclassNames: new[] { "Dependency.Base" }));

        Assert.Contains("Dependency", DependencyReferenceScanner.ReferencedModules(module));
    }

    [Fact]
    public void Scanner_ClassStoredProperty_IsNotAnEdge()
    {
        // A class is a reference type; its own field layout is not order-sensitive, and the
        // finalizer does not look up a class's stored-property records. Only the superclass is.
        var module = Module("Consumer",
            Class("Consumer", "Widget",
                props: new[] { Property("core", new NamedTypeSpec("Dependency.CoreType")) }));

        Assert.DoesNotContain("Dependency", DependencyReferenceScanner.ReferencedModules(module));
    }

    [Fact]
    public void Scanner_ProtocolInheritance_IsNotAnEdge()
    {
        // Although ClassBound/Codable flag propagation CAN look up a cross-module inherited
        // protocol's record, both propagation walks short-circuit (on IsClassBound, an earlier
        // AnyObject, or an earlier Codable-family name) and may never reach a later inherited
        // foreign protocol. An unconditional inherited-protocol edge would thus be a SUPERSET of the
        // finalizer's actual lookups; because finalize order is emission-order relevant, a spurious
        // reorder can change generated bytes, so the edge is dropped (strict subset). This only
        // forgoes a reordering fix the historical sequential loop never provided.
        var module = Module("Consumer",
            Protocol("Consumer", "Refined",
                inherited: new[] { new NamedTypeSpec("Dependency.BaseProtocol") }));

        Assert.DoesNotContain("Dependency", DependencyReferenceScanner.ReferencedModules(module));
    }

    [Fact]
    public void Scanner_EnumAssociatedValuePayload_IsNotAnEdge()
    {
        // CalculateEnumFlags reads only a boolean (HasAssociatedValueCases); it never examines the
        // payload types, so a foreign associated-value type imposes no finalize-order constraint.
        var module = Module("Consumer",
            Enum("Consumer", "Tagged",
                cases: new[] { Case("wrap", new NamedTypeSpec("Dependency.Payload")) }));

        Assert.DoesNotContain("Dependency", DependencyReferenceScanner.ReferencedModules(module));
    }

    [Fact]
    public void Scanner_TransitiveSuperclassAncestor_IsNotAnEdge()
    {
        // ResolveClassHierarchy looks up ONLY the direct superclass (SuperclassNames[0]); the
        // transitive ancestor chain is never a foreign-record lookup. Here the direct parent is
        // same-module and the foreign name is a transitive ancestor, so it must NOT be an edge.
        var module = Module("Consumer",
            Class("Consumer", "Derived",
                superclassNames: new[] { "Consumer.Base", "Dependency.Root" }));

        Assert.DoesNotContain("Dependency", DependencyReferenceScanner.ReferencedModules(module));
    }

    [Fact]
    public void Scanner_GenericInstantiatedSuperclass_IsNotAnEdge()
    {
        // The finalizer skips a generic-instantiated direct-parent name (one containing '<'), so
        // it performs no foreign-record lookup and the parent module is not an order edge.
        var module = Module("Consumer",
            Class("Consumer", "Derived",
                superclassNames: new[] { "Dependency.Base<Consumer.Arg>" }));

        Assert.DoesNotContain("Dependency", DependencyReferenceScanner.ReferencedModules(module));
    }

    [Fact]
    public void Scanner_ObjCSuperclass_IsNotAnEdge()
    {
        // A Swift class deriving from an ObjC class (SuperclassUsr "c:") has HasObjCSuperclass=true;
        // ResolveClassHierarchy short-circuits on that BEFORE any TryGetTypeRecord (both the
        // cross-module link at :1131 and the IsObjCRooted fixed-point at :1169), and an ObjC parent
        // is never a Swift Class record. So even when the ObjC parent belongs to another supplied
        // (mixed) dependency, no foreign Swift superclass record is consumed and it is not an edge.
        var module = Module("Consumer",
            Class("Consumer", "Derived",
                superclassNames: new[] { "Dependency.ObjCBase" },
                superclassUsr: "c:objc(cs)DependencyObjCBase"));

        Assert.DoesNotContain("Dependency", DependencyReferenceScanner.ReferencedModules(module));
    }

    [Fact]
    public void Scanner_NonObjCForeignSuperclass_YieldsEdge()
    {
        // Control for Scanner_ObjCSuperclass_IsNotAnEdge: the SAME shape with a Swift superclass USR
        // ("s:") keeps HasObjCSuperclass=false, so the foreign direct superclass IS a consumed
        // record and therefore an order edge — proving the ObjC exclusion is doing real work.
        var module = Module("Consumer",
            Class("Consumer", "Derived",
                superclassNames: new[] { "Dependency.Base" },
                superclassUsr: "s:10Dependency4BaseC"));

        Assert.Contains("Dependency", DependencyReferenceScanner.ReferencedModules(module));
    }

    [Fact]
    public void Scanner_RetainedForeignSuperclass_IsNotAnEdge()
    {
        // A module can RETAIN a foreign class it extends (an ownership re-export where this module
        // contributes extension members). When it does, the finalizer resolves the superclass
        // against the module's OWN class set (classesByName, keyed by module-qualified name) and
        // short-circuits BEFORE the foreign TryGetTypeRecord — so no foreign record is consumed and
        // there is no finalize-order constraint. Contrast Scanner_ForeignSuperclass_YieldsEdge: the
        // SAME `Derived : Dependency.Base` shape yields an edge when Dependency.Base is NOT retained.
        // Emitting an edge here would be a spurious over-approx that could reorder a valid supplied
        // order and change emitted bytes.
        var module = Module("Consumer",
            Class("Dependency", "Base"), // retained foreign class (this module extends it)
            Class("Consumer", "Derived", superclassNames: new[] { "Dependency.Base" }));

        Assert.DoesNotContain("Dependency", DependencyReferenceScanner.ReferencedModules(module));
    }

    [Fact]
    public void Scanner_UmbrellaUsrResolvedSuperclass_IsNotAnEdge()
    {
        // Umbrella re-export: SuperclassNames uses the original Swift module ("RealityKit.Entity")
        // while the parent is retained under the umbrella module ("RealityFoundation.Entity"), and
        // the USR is canonical. TryResolveSuperclassByUsr resolves the parent same-module via the
        // USR, so the finalizer never reads a foreign RealityKit record — the scanner must mirror
        // that (reusing ModuleProcessor.TryParseSwiftClassUsr) and NOT emit a RealityKit edge.
        var module = Module("RealityFoundation",
            Class("RealityFoundation", "Entity"), // retained under the umbrella module
            Class("RealityFoundation", "ModelEntity",
                superclassNames: new[] { "RealityKit.Entity" },
                superclassUsr: "s:17RealityFoundation6EntityC"));

        Assert.DoesNotContain("RealityKit", DependencyReferenceScanner.ReferencedModules(module));
    }

    [Fact]
    public void Scanner_DoublyOptionalForeignType_DoesNotYieldInnerEdge()
    {
        // The finalizer unwraps exactly ONE Optional level: the immediate payload of a
        // `Dependency.CoreType??` is Swift.Optional, not Dependency.CoreType, so it never looks the
        // foreign type up. Recursing into the nested Optional would be an over-approximation.
        var doublyOptional = new NamedTypeSpec("Swift.Optional",
            new NamedTypeSpec("Swift.Optional", new NamedTypeSpec("Dependency.CoreType")));
        var module = Module("Consumer",
            Struct("Consumer", "Widget", props: new[] { Property("core", doublyOptional) }));

        Assert.DoesNotContain("Dependency", DependencyReferenceScanner.ReferencedModules(module));
    }
}
