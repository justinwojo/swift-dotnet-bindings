// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for <see cref="ProtocolProxyEmissionPolicy.Decide"/> — the single source of truth for
/// whether a protocol's C# proxy class is emitted. The regression these lock is the
/// empty-suitable-protocol × full-proxy-eligible cell: when a module emits no EveryProtocol carrier
/// (its suitable-protocol set is empty), a FULL reverse-dispatch proxy would call the never-emitted
/// <c>SBW_CreateEveryProtocol</c> factory — a dangling wrapper symbol. The policy must suppress such
/// a proxy on the carrier fact, not on the historical <c>ConformanceDecisions.Count &gt; 0</c> proxy
/// for it (which was 0 in exactly that module, so it wrongly emitted the dangling full proxy).
/// </summary>
public class ProtocolProxyEmissionPolicyTests
{
    private static ProtocolDecl SimpleProtocol(string name) => new ProtocolDecl
    {
        Name = name,
        SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
        MangledName = $"$s10TestModule{name.Length}{name}P",
        Properties = new List<PropertyDecl>(),
        Methods = new List<MethodDecl>(),
        Types = new List<TypeDecl>(),
        Operators = new List<OperatorDecl>(),
        Subscripts = new List<SubscriptDecl>(),
        AssociatedTypes = new List<AssociatedTypeDecl>(),
        InheritedProtocols = new List<NamedTypeSpec>(),
        HasSelfRequirement = false,
        IsClassBound = false,
        ParentDecl = null,
        ModuleDecl = null,
    };

    private static string QualifiedKey(ProtocolDecl p) => p.SwiftTypeName!.ModuleQualifiedName;

    private static ProxyEmissionDecision Decide(ProtocolDecl p, ModuleEmissionContext ctx)
        => ProtocolProxyEmissionPolicy.Decide(p, new TypeDatabase(), ctx);

    // ---- The fix: no carrier emitted (empty suitable-protocol module) ----

    [Fact]
    public void Decide_NoCarrier_FullProxyEligible_Suppressed()
    {
        // The StripeCore shape: a module whose suitable-protocol set was empty never emitted the
        // EveryProtocol carrier, so a full proxy for this protocol would dangle on
        // SBW_CreateEveryProtocol. It must be suppressed. Before the carrier fix this returned Emit
        // (ConformanceDecisions.Count == 0 disabled the suppression), producing the SWIFTBIND108 hole.
        var p = SimpleProtocol("UnknownFieldsDecodable");
        var ctx = new ModuleEmissionContext();
        // Carrier NOT marked, no conformance recorded — exactly the empty-suitable-protocol module.

        Assert.Equal(ProxyEmissionDecision.SuppressedByConformance, Decide(p, ctx));
    }

    [Fact]
    public void Decide_NoCarrier_ReadOnlyProxy_StillEmits()
    {
        // A read-only (Swift-vended-only) proxy reads `any P` through the existential's own witness
        // table and never calls the carrier factory, so it is emitted even with no carrier.
        var p = SimpleProtocol("ForwardOnly");
        var ctx = new ModuleEmissionContext();
        ctx.MarkReadOnlyProxy(p.Name);

        Assert.Equal(ProxyEmissionDecision.Emit, Decide(p, ctx));
    }

    [Fact]
    public void Decide_NoCarrier_RecordingADroppedDecision_DoesNotFlipToEmit()
    {
        // Report honesty: the dropped-candidacy attribution now records `false` conformance
        // decisions on the empty path so the skip classifies with a structural cause instead of
        // "no decision recorded". That pushes ConformanceDecisions.Count 0->1, which under the OLD
        // count-based gate would have been the very thing that suppressed the proxy. The carrier
        // fact must remain the sole signal: recording the decision must not change the verdict.
        var p = SimpleProtocol("UnknownFieldsEncodable");
        var ctx = new ModuleEmissionContext();
        ctx.RecordConformanceDecision(QualifiedKey(p), emitted: false, "inherits unsatisfiable");

        Assert.Equal(ProxyEmissionDecision.SuppressedByConformance, Decide(p, ctx));
    }

    // ---- Monotone: carrier emitted — behaviour byte-identical to the historical gate ----

    [Fact]
    public void Decide_CarrierEmitted_ConformanceEmitted_Emits()
    {
        // The working case: the module emitted the carrier and this protocol's conformance. A full
        // proxy is valid — emit it. Must stay unchanged by the fix.
        var p = SimpleProtocol("WorkingProto");
        var ctx = new ModuleEmissionContext();
        ctx.MarkEveryProtocolCarrierEmitted();
        ctx.RecordConformanceDecision(QualifiedKey(p), emitted: true, null);

        Assert.Equal(ProxyEmissionDecision.Emit, Decide(p, ctx));
    }

    [Fact]
    public void Decide_CarrierEmitted_ConformanceSkipped_NonReadOnly_Suppressed()
    {
        // Carrier present, but THIS protocol's conformance was skipped (class-bound, genericSig
        // constraint, constructor requirement, ...). Suppress — unchanged from the historical
        // Count > 0 && !WasConformanceEmitted gate.
        var p = SimpleProtocol("SkippedProto");
        var ctx = new ModuleEmissionContext();
        ctx.MarkEveryProtocolCarrierEmitted();
        ctx.RecordConformanceDecision(QualifiedKey(p), emitted: false, "class-bound");

        Assert.Equal(ProxyEmissionDecision.SuppressedByConformance, Decide(p, ctx));
    }

    [Fact]
    public void Decide_CarrierEmitted_ConformanceSkipped_ReadOnly_Emits()
    {
        // Carrier present, conformance skipped, but the protocol is a read-only proxy — it keeps its
        // proxy. Unchanged from the historical gate's IsReadOnlyProxy arm.
        var p = SimpleProtocol("ForwardOnlyWithCarrier");
        var ctx = new ModuleEmissionContext();
        ctx.MarkEveryProtocolCarrierEmitted();
        ctx.RecordConformanceDecision(QualifiedKey(p), emitted: false, "class-superclass");
        ctx.MarkReadOnlyProxy(p.Name);

        Assert.Equal(ProxyEmissionDecision.Emit, Decide(p, ctx));
    }

    [Fact]
    public void Decide_CarrierEmitted_UnrelatedConformanceOnly_TargetSuppressed()
    {
        // Carrier emitted for OTHER protocols; this one has no recorded decision (never suitable).
        // WasConformanceEmitted is false for it, so a full proxy is suppressed — the same answer the
        // historical Count > 0 gate gave once any decision existed.
        var p = SimpleProtocol("UnrelatedSkip");
        var ctx = new ModuleEmissionContext();
        ctx.MarkEveryProtocolCarrierEmitted();
        ctx.RecordConformanceDecision("TestModule.SomeOtherProto", emitted: true, null);

        Assert.Equal(ProxyEmissionDecision.SuppressedByConformance, Decide(p, ctx));
    }
}
