// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Pins the persisted-report contract for a degraded EveryProtocol reverse-dispatch member.
/// When a <c>{Protocol}Proxy</c> conformance cannot be synthesized the generator keeps the
/// surrounding member but degrades it — a PRODUCE getter/return throws, a CONSUME setter/parameter
/// silently drops its C#-conformer wrap, or a reverse-dispatch receiver fail-fasts. Each outcome
/// used to be invisible in <c>binding-report.json</c>; <see cref="SuppressedProxyReporting"/> is
/// the single point that promotes the per-member decline to a classified
/// <see cref="SkipReason.SuppressedProxyMemberDegraded"/> skip with a greppable, site-stamped
/// <see cref="SkippedItem.Details"/>. These tests assert the row lands, carries the site token,
/// and reads back as <see cref="SkipDisposition.KnownLimitation"/> — the honest fixture that a
/// decline is now a durable diagnostic, not a log line.
/// </summary>
public class SuppressedProxyReportingTests
{
    [Theory]
    [InlineData(SuppressedProxyReporting.Site.ProduceThrow, "produce-throw")]
    [InlineData(SuppressedProxyReporting.Site.ConsumeDegraded, "consume-degraded")]
    [InlineData(SuppressedProxyReporting.Site.ReceiverFailFast, "receiver-failfast")]
    public void Details_StampsGreppableSiteToken(SuppressedProxyReporting.Site site, string expectedToken)
    {
        var details = SuppressedProxyReporting.Details(site, "MyProtocolProxy");
        Assert.Contains($"({expectedToken})", details);
        // The subject (proxy/protocol) is named so triage can grep by protocol too.
        Assert.Contains("MyProtocolProxy", details);
    }

    [Fact]
    public void Details_NullSubject_FallsBackToGenericPhrase()
    {
        var details = SuppressedProxyReporting.Details(SuppressedProxyReporting.Site.ProduceThrow, proxyOrProtocol: null);
        Assert.Contains("a suppressed protocol proxy", details);
    }

    [Fact]
    public void Record_Method_ProduceThrow_RecordsClassifiedDegradedSkip()
    {
        RunWithCollector(moduleDecl =>
        {
            var method = TestModelFactory.CreateMethod("materials", parent: moduleDecl);
            SuppressedProxyReporting.Record(method, SuppressedProxyReporting.Site.ProduceThrow, "IMaterialProxy");
        },
        report =>
        {
            var item = Assert.Single(report.SkippedItems);
            Assert.Equal(BindingItemKind.Method, item.Kind);
            Assert.Equal("materials", item.Name);
            Assert.Equal(SkipReason.SuppressedProxyMemberDegraded, item.Reason);
            Assert.Contains("(produce-throw)", item.Details);
            Assert.Equal(SkipDisposition.KnownLimitation, SkipDispositionClassifier.Classify(item));
        });
    }

    [Fact]
    public void Record_MethodParameter_ConsumeDegraded_RecordsClassifiedDegradedSkip()
    {
        RunWithCollector(moduleDecl =>
        {
            var method = TestModelFactory.CreateMethod("setDelegate", parent: moduleDecl);
            SuppressedProxyReporting.Record(method, SuppressedProxyReporting.Site.ConsumeDegraded, "IRoomCaptureDelegateProxy");
        },
        report =>
        {
            var item = Assert.Single(report.SkippedItems);
            Assert.Equal(SkipReason.SuppressedProxyMemberDegraded, item.Reason);
            Assert.Contains("(consume-degraded)", item.Details);
            Assert.Equal(SkipDisposition.KnownLimitation, SkipDispositionClassifier.Classify(item));
        });
    }

    /// <summary>
    /// A getter that throws (PRODUCE) and a setter that silently drops the C#-conformer wrap
    /// (CONSUME) on the SAME property are two distinct declines and must record as two rows —
    /// the get+set granularity is why the helper takes an <see cref="AccessorKind"/>.
    /// </summary>
    [Fact]
    public void Record_Property_GetterThrowAndSetterConsume_CoexistAsTwoRows()
    {
        RunWithCollector(moduleDecl =>
        {
            var property = TestModelFactory.CreateProperty("handler", parent: moduleDecl);
            SuppressedProxyReporting.Record(property, SuppressedProxyReporting.Site.ProduceThrow, "IHandlerProxy", AccessorKind.Getter);
            SuppressedProxyReporting.Record(property, SuppressedProxyReporting.Site.ConsumeDegraded, "IHandlerProxy", AccessorKind.Setter);
        },
        report =>
        {
            Assert.Equal(2, report.SkippedItems.Count);
            Assert.All(report.SkippedItems, i =>
            {
                Assert.Equal(BindingItemKind.Property, i.Kind);
                Assert.Equal("handler", i.Name);
                Assert.Equal(SkipReason.SuppressedProxyMemberDegraded, i.Reason);
                Assert.Equal(SkipDisposition.KnownLimitation, SkipDispositionClassifier.Classify(i));
            });
            Assert.Contains(report.SkippedItems, i => i.Details!.Contains("(produce-throw)"));
            Assert.Contains(report.SkippedItems, i => i.Details!.Contains("(consume-degraded)"));
        });
    }

    [Fact]
    public void Record_Subscript_ProduceThrow_RecordsClassifiedDegradedSkip()
    {
        RunWithCollector(moduleDecl =>
        {
            var subscriptDecl = TestModelFactory.CreateSubscript(
                moduleDecl, new[] { ("index", "Swift.Int") });
            SuppressedProxyReporting.Record(subscriptDecl, SuppressedProxyReporting.Site.ProduceThrow, "IElementProxy", AccessorKind.SubscriptGetter);
        },
        report =>
        {
            var item = Assert.Single(report.SkippedItems);
            Assert.Equal(BindingItemKind.Subscript, item.Kind);
            Assert.Equal(SkipReason.SuppressedProxyMemberDegraded, item.Reason);
            Assert.Contains("(produce-throw)", item.Details);
            Assert.Equal(SkipDisposition.KnownLimitation, SkipDispositionClassifier.Classify(item));
        });
    }

    [Fact]
    public void Record_DescriptorMember_RecordsClassifiedDegradedSkip()
    {
        RunWithCollector(moduleDecl =>
        {
            SuppressedProxyReporting.Record(
                BindingItemKind.Method, "Palette.TryGetSwatch", moduleDecl,
                SuppressedProxyReporting.Site.ProduceThrow, "ISwatchProxy");
        },
        report =>
        {
            var item = Assert.Single(report.SkippedItems);
            Assert.Equal(BindingItemKind.Method, item.Kind);
            Assert.Equal("Palette.TryGetSwatch", item.Name);
            Assert.Equal(SkipReason.SuppressedProxyMemberDegraded, item.Reason);
            Assert.Contains("(produce-throw)", item.Details);
            Assert.Equal(SkipDisposition.KnownLimitation, SkipDispositionClassifier.Classify(item));
        });
    }

    [Fact]
    public void RecordReceiver_FailFast_RecordsClassifiedDegradedSkip()
    {
        RunWithCollector(_ =>
        {
            SuppressedProxyReporting.RecordReceiver("IAnalyzer.analyze(with:)", "IAnalyzerProxy");
        },
        report =>
        {
            var item = Assert.Single(report.SkippedItems);
            Assert.Equal(SkipReason.SuppressedProxyMemberDegraded, item.Reason);
            Assert.Contains("(receiver-failfast)", item.Details);
            // API contract: a non-null proxy name reaches the row's Details (so triage can grep the
            // receiver-failfast rows by proxy just like the produce/consume rows) rather than falling back
            // to the generic "a suppressed protocol proxy". This asserts the RecordReceiver→Details plumbing
            // for a named subject; that the EMITTER actually passes the exception's ProxyClassName here
            // (not null) is locked separately by
            // EmitReceiverOrDegrade_SuppressedProxy_RecordsRowNamingTheProxyFromException, which drives the
            // real emitter catch path.
            Assert.Contains("IAnalyzerProxy", item.Details);
            Assert.DoesNotContain("a suppressed protocol proxy", item.Details);
            Assert.Equal(SkipDisposition.KnownLimitation, SkipDispositionClassifier.Classify(item));
        });
    }

    /// <summary>
    /// End-to-end wiring lock for the reverse-dispatch receiver path. Drives the actual emitter degrade
    /// branch (<see cref="ProtocolProxyEmitter.EmitReceiverOrDegrade"/>) with an <c>emitBody</c> that throws
    /// <see cref="SuppressedProxyReferenceException"/> — the shape a receiver whose existential references a
    /// suppressed proxy takes — and asserts the recorded row NAMES the proxy carried on the exception. This
    /// is the regression the API-level <see cref="RecordReceiver_FailFast_RecordsClassifiedDegradedSkip"/>
    /// cannot catch: reverting the emitter to pass <c>null</c> (dropping <c>ex.ProxyClassName</c>) leaves that
    /// test green but fails this one, because only this test exercises the catch → <c>ex.ProxyClassName</c> →
    /// <see cref="SuppressedProxyReporting.RecordReceiver"/> plumbing.
    /// </summary>
    [Fact]
    public void EmitReceiverOrDegrade_SuppressedProxy_RecordsRowNamingTheProxyFromException()
    {
        RunWithCollector(_ =>
        {
            var typeDatabase = new TypeDatabase();
            typeDatabase.AddModuleDatabase(new ModuleTypeDatabase("TestModule", "/fake/path"));
            var emitter = new ProtocolProxyEmitter(
                typeDatabase, NullLogger.Instance, "TestModule", new ModuleEmissionContext());

            var writer = new CSharpWriter(new StringWriter());
            emitter.EmitReceiverOrDegrade(
                writer, "void", "Receive_analyze", "IntPtr vtHandle, IntPtr selfContainer, IntPtr valuePtr",
                "IAnalyzer.analyze(with:)",
                emitBody: () => throw new SuppressedProxyReferenceException("IAnalyzerReceiverProxy"));
        },
        report =>
        {
            var item = Assert.Single(report.SkippedItems);
            Assert.Equal(SkipReason.SuppressedProxyMemberDegraded, item.Reason);
            Assert.Contains("(receiver-failfast)", item.Details);
            // The name comes off the thrown exception, threaded through EmitSuppressedProxyReceiverStub —
            // NOT the null-subject generic fallback. This is what pins the emitter wiring.
            Assert.Contains("IAnalyzerReceiverProxy", item.Details);
            Assert.DoesNotContain("a suppressed protocol proxy", item.Details);
            Assert.Equal(SkipDisposition.KnownLimitation, SkipDispositionClassifier.Classify(item));
        });
    }

    /// <summary>
    /// Dedup pin: the SAME degraded member recorded twice collapses to ONE row. This is the shape a
    /// closure site produces when it re-queries the suppression predicate at more than one emission
    /// call site (the callback-return AND invoke-arg seams can both fire for one method), collapsed on
    /// the member's <see cref="MemberDiagnosticIdentity"/> — the second <c>Add</c> is a no-op.
    /// </summary>
    [Fact]
    public void Record_SameMethodTwice_CollapsesToOneRow()
    {
        RunWithCollector(moduleDecl =>
        {
            var method = TestModelFactory.CreateMethod("sumBoxables", parent: moduleDecl);
            SuppressedProxyReporting.Record(method, SuppressedProxyReporting.Site.ConsumeDegraded, "BoxableProxy");
            SuppressedProxyReporting.Record(method, SuppressedProxyReporting.Site.ConsumeDegraded, "BoxableProxy");
        },
        report =>
        {
            var item = Assert.Single(report.SkippedItems);
            Assert.Equal("sumBoxables", item.Name);
            Assert.Contains("(consume-degraded)", item.Details);
        });
    }

    /// <summary>
    /// Dedup pin for the descriptor path (a synthesized enum-case payload accessor with no member
    /// <c>BaseDecl</c>): the same (kind, descriptor, containingDecl) recorded once per existential
    /// payload collapses to one row — so an enum case carrying several <c>any P</c> payloads that each
    /// walk to the same suppressed proxy reports once, not per-payload.
    /// </summary>
    [Fact]
    public void Record_SameDescriptorTwice_CollapsesToOneRow()
    {
        RunWithCollector(moduleDecl =>
        {
            SuppressedProxyReporting.Record(
                BindingItemKind.Method, "boxed", moduleDecl,
                SuppressedProxyReporting.Site.ConsumeDegraded, "BoxableProxy");
            SuppressedProxyReporting.Record(
                BindingItemKind.Method, "boxed", moduleDecl,
                SuppressedProxyReporting.Site.ConsumeDegraded, "BoxableProxy");
        },
        report => Assert.Single(report.SkippedItems));
    }

    private static void RunWithCollector(System.Action<ModuleDecl> record, System.Action<BindingReport> assert)
    {
        try
        {
            var moduleDecl = TestModelFactory.CreateModuleDecl("TestModule");
            ReportCollector.Start(moduleDecl);
            record(moduleDecl);
            var report = ReportCollector.Complete();
            Assert.NotNull(report);
            assert(report!);
        }
        finally
        {
            ReportCollector.Reset();
        }
    }
}

/// <summary>
/// Pins <see cref="SuppressedProxyProjectionWalk.CollectSuppressedProxyNames"/> — the stateless walk a
/// decl-owning handler runs over a container/optional/tuple projection tree to find the suppressed-proxy
/// names at its existential leaves (a <c>[any P]</c> / <c>(any P)?</c> element drops its per-element
/// <c>static __v =&gt; new {Proxy}(__v)</c> wrap fallback but has no owning decl). The walk is keyed
/// STRICTLY on <see cref="ExistentialProjection.SuppressedProxyName"/>: a suppressed proxy contributes
/// its name; a live proxy, an <c>object</c>/well-known leaf, and an existential union (all null-named)
/// contribute nothing — so it never re-enters the ExistentialUnion projection path. Purely additive: it
/// reads the tree and emits no C#.
/// </summary>
public class SuppressedProxyProjectionWalkTests
{
    private const string EC1 = "Swift.Runtime.ExistentialContainer1";
    private const string EC2 = "Swift.Runtime.ExistentialContainer2";

    private static ExistentialProjection Suppressed(string proxy = "BoxableProxy") =>
        new(EC1, "IBoxable", proxy, proxyIsSuppressed: true);

    // Live proxy: proxy emitted, so SuppressedProxyName is null — the CONSUME wrap fallback is kept.
    private static ExistentialProjection LiveProxy() =>
        new(EC1, "IBoxable", "BoxableProxy", proxyIsSuppressed: false);

    // No-proxy / well-known leaf (public type "object").
    private static ExistentialProjection ObjectLeaf() =>
        new(EC1, "object", proxyClassName: null);

    // Composition / existential union leaf: no single suppressed proxy → null SuppressedProxyName.
    private static ExistentialProjection UnionLeaf() =>
        new(EC2, "object", proxyClassName: null);

    [Fact]
    public void Root_SuppressedExistential_YieldsName() =>
        Assert.Equal("BoxableProxy", Assert.Single(SuppressedProxyProjectionWalk.CollectSuppressedProxyNames(Suppressed())));

    [Fact]
    public void Array_SuppressedElement_YieldsName() =>
        Assert.Equal("BoxableProxy", Assert.Single(SuppressedProxyProjectionWalk.CollectSuppressedProxyNames(
            new ArrayProjection(Suppressed(), isParameter: true))));

    [Fact]
    public void Set_SuppressedElement_YieldsName() =>
        Assert.Equal("BoxableProxy", Assert.Single(SuppressedProxyProjectionWalk.CollectSuppressedProxyNames(
            new SetProjection(Suppressed(), isParameter: true))));

    [Fact]
    public void Optional_SuppressedInner_YieldsName() =>
        Assert.Equal("BoxableProxy", Assert.Single(SuppressedProxyProjectionWalk.CollectSuppressedProxyNames(
            new OptionalProjection(Suppressed()))));

    [Fact]
    public void Dictionary_SuppressedValue_LiveKey_YieldsOnlyValueName() =>
        Assert.Equal("BoxableProxy", Assert.Single(SuppressedProxyProjectionWalk.CollectSuppressedProxyNames(
            new DictionaryProjection(LiveProxy(), Suppressed(), isParameter: true))));

    [Fact]
    public void Tuple_SuppressedElement_YieldsName() =>
        Assert.Equal("BoxableProxy", Assert.Single(SuppressedProxyProjectionWalk.CollectSuppressedProxyNames(
            new TupleProjection(new ITypeProjection[] { ObjectLeaf(), Suppressed() }))));

    [Fact]
    public void NestedArrayOfOptional_SuppressedLeaf_YieldsName() =>
        Assert.Equal("BoxableProxy", Assert.Single(SuppressedProxyProjectionWalk.CollectSuppressedProxyNames(
            new ArrayProjection(new OptionalProjection(Suppressed()), isParameter: true))));

    [Fact]
    public void LiveProxyLeaf_YieldsEmpty() =>
        Assert.Empty(SuppressedProxyProjectionWalk.CollectSuppressedProxyNames(
            new ArrayProjection(LiveProxy(), isParameter: true)));

    [Fact]
    public void ObjectLeaf_YieldsEmpty() =>
        Assert.Empty(SuppressedProxyProjectionWalk.CollectSuppressedProxyNames(
            new ArrayProjection(ObjectLeaf(), isParameter: true)));

    // A union leaf carries a null SuppressedProxyName: the walk must NOT treat "is an existential" as a
    // hit, or it would re-enter the ExistentialUnion inert-engine path.
    [Fact]
    public void UnionLeaf_YieldsEmpty() =>
        Assert.Empty(SuppressedProxyProjectionWalk.CollectSuppressedProxyNames(
            new OptionalProjection(UnionLeaf())));

    [Fact]
    public void NullProjection_YieldsEmpty() =>
        Assert.Empty(SuppressedProxyProjectionWalk.CollectSuppressedProxyNames(null));

    // Same suppressed proxy at two leaves → one distinct name (List.Contains guard in the walk).
    [Fact]
    public void SameProxyAtTwoLeaves_Deduped() =>
        Assert.Equal("BoxableProxy", Assert.Single(SuppressedProxyProjectionWalk.CollectSuppressedProxyNames(
            new DictionaryProjection(Suppressed(), Suppressed(), isParameter: true))));

    // Two distinct suppressed proxies → both names, source order preserved.
    [Fact]
    public void TwoDistinctSuppressedProxies_BothCollectedInOrder() =>
        Assert.Equal(
            new[] { "BoxableProxy", "LabelledContainerProxy" },
            SuppressedProxyProjectionWalk.CollectSuppressedProxyNames(
                new TupleProjection(new ITypeProjection[] { Suppressed("BoxableProxy"), Suppressed("LabelledContainerProxy") })).ToArray());

    // A suppressed COMPOSITION (EC2+) leaf marshals via
    // ((ISwiftExistentialConvertible<…>)x).GetExistentialContainer() and never emits a per-element
    // `static __v => new {Proxy}(__v)` wrap fallback — so dropping nothing is NOT a consume-degrade. The
    // EC1 gate on SuppressedProxyName keeps the walk from mis-recording it even though the proxy is
    // suppressed; the conversion is byte-identical between the live and suppressed composition.
    private static ExistentialProjection SuppressedComposition() =>
        new(EC2, "IBoxableAndLabelled", "BoxableAndLabelledProxy", proxyIsSuppressed: true);

    [Fact]
    public void SuppressedCompositionRoot_YieldsEmpty() =>
        Assert.Empty(SuppressedProxyProjectionWalk.CollectSuppressedProxyNames(SuppressedComposition()));

    [Fact]
    public void SuppressedCompositionInContainer_YieldsEmpty() =>
        Assert.Empty(SuppressedProxyProjectionWalk.CollectSuppressedProxyNames(
            new ArrayProjection(SuppressedComposition(), isParameter: true)));
}

/// <summary>
/// Pins <see cref="SuppressedProxyTypeSpecWalk.CollectSuppressedProxyNames"/> — the TypeSpec twin of the
/// projection walk, used at the enum-case and closure CONSUME sites where building a fresh projection just
/// to report would not be diagnostic-only (a <c>Foundation.Data</c> projection records an Apple-supplement
/// dependency, changing emission). The walk re-queries the suppression predicate over the raw type: it
/// records only a suppressed single-protocol <see cref="ExistentialContainer1"/> leaf (the shape whose
/// CONSUME arm drops a wrap fallback), mirrors the projection walk's container coverage, and — keyed on the
/// same EC1 gate — reports the SAME set of names for the same member. A live proxy, an <c>object</c> leaf,
/// a non-existential leaf, and an EC2+/composition leaf each contribute nothing.
/// </summary>
public class SuppressedProxyTypeSpecWalkTests
{
    // A TypeDatabase carrying the protocol TypeRecords the walk's ProjectsToProxyInterface gate reads
    // (side-effect-free, via TryGetTypeRecordWithoutSupplement) to decide whether a suppressed protocol
    // actually projects to a real proxy interface or degrades to `object`. Boxable/Labelled are plain
    // protocols (real I{Name} proxy → recorded); Container is a PAT (HasAssociatedTypes) and SelfP a
    // Self-requirement protocol — both degrade to `object`, so their suppressed conformance carries NO
    // wrap fallback to drop and must NOT be reported. The real generator always has such a record for a
    // suppressed proxy's protocol; the prior empty-DB harness predated this object-degrade gate.
    private static TypeDatabase Db()
    {
        var db = new TypeDatabase();
        var module = new ModuleTypeDatabase("SwiftBindingsTestLib", "/tmp/SwiftBindingsTestLib.dylib");
        void Register(string name, string iface, TypeRecordFlags flags)
        {
            var swiftName = SwiftTypeName.FromModuleQualifiedName($"SwiftBindingsTestLib.{name}");
            module.RegisterType(swiftName, new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("SwiftBindingsTestLib", iface),
                SwiftTypeName = swiftName,
                MetadataAccessor = string.Empty,
                Flags = flags,
                Kind = TypeRecordKind.Protocol,
            });
        }
        Register("Boxable", "IBoxable", TypeRecordFlags.None);
        Register("Labelled", "ILabelled", TypeRecordFlags.None);
        Register("Container", "IContainer", TypeRecordFlags.HasAssociatedTypes);
        Register("SelfP", "ISelfP", TypeRecordFlags.HasSelfRequirement);
        db.AddModuleDatabase(module);
        return db;
    }

    // CurrentModuleName left UNSET so QualifyProxyClassName returns the bare proxy name and suppression
    // matches the current-module set. TypeRecords ARE registered (Db) because ProjectsToProxyInterface
    // reads them to reproduce the projection factory's `proxyClassName != null` object-degrade half.
    private static ExistentialHandler Handler() => new(Db());

    private static ModuleEmissionContext Ctx(params string[] suppressedProxies)
    {
        var ctx = new ModuleEmissionContext();
        foreach (var p in suppressedProxies)
            ctx.RecordSuppressedProxy(p);
        return ctx;
    }

    // `any Boxable` — single non-marker protocol → EC1 → proxy class "BoxableProxy".
    private static NamedTypeSpec AnyBoxable(string protocol = "SwiftBindingsTestLib.Boxable") =>
        new(protocol) { IsAny = true };

    private static IReadOnlyList<string> Walk(TypeSpec ts, ModuleEmissionContext ctx) =>
        SuppressedProxyTypeSpecWalk.CollectSuppressedProxyNames(ts, Handler(), ctx);

    [Fact]
    public void ScalarSuppressed_YieldsName() =>
        Assert.Equal("BoxableProxy", Assert.Single(Walk(AnyBoxable(), Ctx("BoxableProxy"))));

    [Fact]
    public void ArraySuppressedElement_YieldsName() =>
        Assert.Equal("BoxableProxy", Assert.Single(Walk(
            new NamedTypeSpec("Swift.Array", AnyBoxable()), Ctx("BoxableProxy"))));

    [Fact]
    public void OptionalSuppressedInner_YieldsName() =>
        Assert.Equal("BoxableProxy", Assert.Single(Walk(
            new NamedTypeSpec("Swift.Optional", AnyBoxable()), Ctx("BoxableProxy"))));

    [Fact]
    public void SetSuppressedElement_YieldsName() =>
        Assert.Equal("BoxableProxy", Assert.Single(Walk(
            new NamedTypeSpec("Swift.Set", AnyBoxable()), Ctx("BoxableProxy"))));

    [Fact]
    public void DictionarySuppressedValue_YieldsName() =>
        Assert.Equal("BoxableProxy", Assert.Single(Walk(
            new NamedTypeSpec("Swift.Dictionary", new NamedTypeSpec("Swift.String"), AnyBoxable()), Ctx("BoxableProxy"))));

    [Fact]
    public void TupleSuppressedElement_YieldsName()
    {
        var tuple = new TupleTypeSpec(new TypeSpec[] { new NamedTypeSpec("Swift.Int"), AnyBoxable() });
        Assert.Equal("BoxableProxy", Assert.Single(Walk(tuple, Ctx("BoxableProxy"))));
    }

    [Fact]
    public void NestedArrayOfOptional_YieldsName() =>
        Assert.Equal("BoxableProxy", Assert.Single(Walk(
            new NamedTypeSpec("Swift.Array", new NamedTypeSpec("Swift.Optional", AnyBoxable())), Ctx("BoxableProxy"))));

    // Proxy live (absent from the suppressed set) → nothing to report.
    [Fact]
    public void LiveProxy_YieldsEmpty() =>
        Assert.Empty(Walk(new NamedTypeSpec("Swift.Array", AnyBoxable()), Ctx(/* none suppressed */)));

    // Non-existential leaf → nothing.
    [Fact]
    public void NonExistentialLeaf_YieldsEmpty() =>
        Assert.Empty(Walk(new NamedTypeSpec("Swift.Array", new NamedTypeSpec("Swift.Int")), Ctx("BoxableProxy")));

    [Fact]
    public void Null_YieldsEmpty() =>
        Assert.Empty(Walk(null!, Ctx("BoxableProxy")));

    // A suppressed COMPOSITION (EC2) never had a per-element wrap fallback: the EC1 gate excludes it even
    // though its proxy is in the suppressed set — the TypeSpec twin of the projection-walk composition guard.
    [Fact]
    public void SuppressedComposition_YieldsEmpty()
    {
        var composition = new ProtocolListTypeSpec(new[]
        {
            new NamedTypeSpec("SwiftBindingsTestLib.Alpha"),
            new NamedTypeSpec("SwiftBindingsTestLib.Beta"),
        });
        Assert.Empty(Walk(composition, Ctx("AlphaAndBetaProxy")));
    }

    // Same suppressed proxy at two container leaves → one distinct name (List.Contains guard).
    [Fact]
    public void SameProxyTwice_Deduped() =>
        Assert.Equal("BoxableProxy", Assert.Single(Walk(
            new NamedTypeSpec("Swift.Dictionary", AnyBoxable(), AnyBoxable()), Ctx("BoxableProxy"))));

    // Two distinct suppressed proxies → both, source order preserved.
    [Fact]
    public void TwoDistinctSuppressed_BothInOrder() =>
        Assert.Equal(
            new[] { "BoxableProxy", "LabelledProxy" },
            Walk(
                new TupleTypeSpec(new TypeSpec[]
                {
                    AnyBoxable("SwiftBindingsTestLib.Boxable"),
                    AnyBoxable("SwiftBindingsTestLib.Labelled"),
                }),
                Ctx("BoxableProxy", "LabelledProxy")).ToArray());

    // `any Container` — a protocol WITH associated types (PAT) — degrades to `object` in the existential
    // projection (can't be named without type arguments), so the factory sets proxyClassName == null and the
    // CONSUME arm has NO `static __v => new {Proxy}(__v)` wrap fallback to drop. Even though "ContainerProxy"
    // is in the suppressed set (a PAT conformance IS recorded as suppressed via SuppressedByConformance),
    // reporting a degrade row here would be a false positive the projection path never emits. The
    // ProjectsToProxyInterface gate excludes it — keeping the two walks in lockstep.
    private static NamedTypeSpec AnyPat(string protocol = "SwiftBindingsTestLib.Container") =>
        new(protocol) { IsAny = true };

    [Fact]
    public void SuppressedPatLeaf_YieldsEmpty() =>
        Assert.Empty(Walk(AnyPat(), Ctx("ContainerProxy")));

    [Fact]
    public void SuppressedPatInContainer_YieldsEmpty() =>
        Assert.Empty(Walk(new NamedTypeSpec("Swift.Array", AnyPat()), Ctx("ContainerProxy")));

    // A Self-requirement protocol (`Self` in a method signature) also degrades to `object` for the same
    // reason — the generic `I{Name}<TSelf>` interface can't be referenced without a type argument.
    [Fact]
    public void SuppressedSelfRequirementLeaf_YieldsEmpty() =>
        Assert.Empty(Walk(new NamedTypeSpec("SwiftBindingsTestLib.SelfP") { IsAny = true }, Ctx("SelfPProxy")));

    // A suppressed name with NO protocol TypeRecord (misclassified metatype / absent) also degrades to
    // `object`; the side-effect-free TryGetTypeRecordWithoutSupplement probe misses it → not reported.
    [Fact]
    public void SuppressedProtocolWithoutTypeRecord_YieldsEmpty() =>
        Assert.Empty(Walk(new NamedTypeSpec("SwiftBindingsTestLib.Ghost") { IsAny = true }, Ctx("GhostProxy")));

    // A constrained `any Container<Int>` binds its associated types at the use site and projects to a real
    // `IContainer<…>` interface (generic args present), so — unlike plain `any Container` — its suppressed
    // conformance DOES drop a wrap fallback and IS reported. Mirrors the factory's constrained-existential
    // arm; the walk keys "constrained" on generic-args-present (the side-effect-free approximation of the
    // factory's supplement-recording arg-resolvability check).
    [Fact]
    public void ConstrainedPat_YieldsName() =>
        Assert.Equal("ContainerProxy", Assert.Single(Walk(
            new NamedTypeSpec("SwiftBindingsTestLib.Container", new NamedTypeSpec("Swift.Int")) { IsAny = true },
            Ctx("ContainerProxy"))));

    // `any Swift.Error` is a well-known existential (projects to Foundation.AnyError, not a proxy), so the
    // gate must return false — mirroring the factory's `!TryGetWellKnownProtocolType` half. Critically it
    // must short-circuit on the NAME, BEFORE probing the Swift.Error TypeRecord: that resolve runs through
    // SwiftErrorStrategy, which records the Foundation.AnyError Apple-supplement reference. A reporting-only
    // walk that recorded a supplement could add a SwiftBindings.Apple PackageReference to the consumer csproj
    // on a tree emission never resolves — breaking the diagnostic-only / byte-identical contract. So walking
    // `any Swift.Error` yields no row AND records no supplement, even with "ErrorProxy" in the suppressed set.
    [Fact]
    public void SuppressedWellKnownErrorLeaf_YieldsEmpty_AndRecordsNoSupplement()
    {
        AppleSupplementReferences.Reset();
        var result = Walk(new NamedTypeSpec("Swift.Error") { IsAny = true }, Ctx("ErrorProxy"));
        Assert.Empty(result);
        Assert.DoesNotContain("Foundation.AnyError", AppleSupplementReferences.Current);
    }
}
